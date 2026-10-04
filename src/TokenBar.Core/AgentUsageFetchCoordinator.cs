using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Shares one blocking agent-usage fetch across concurrent UI callers.
/// A <see cref="RequestFollowUp"/> that arrives while a fetch is in flight
/// makes that same task fetch once more before it completes, so every caller
/// awaiting it gets a payload newer than the request.</summary>
public sealed class AgentUsageFetchCoordinator(Func<AgentUsagePayload> fetch)
{
    private readonly object _gate = new();
    private Task<AgentUsagePayload>? _inFlight;
    private long _inFlightEpoch;
    private long _inFlightId;
    private long _lastId;
    private Action? _beforeFirstFetch;
    private bool _owed;

    /// <summary>Follow-ups one task may run. ponytail: a cap, not a count of
    /// anything measured; it only stops a payload that keeps raising changes
    /// from holding the task forever. Later requests wait for the next tick.</summary>
    private const int MaxFollowUps = 2;

    // Waits for the launch push so the first fetch already carries the extra
    // Claude accounts' cards; AntigravityFetch installs the captured
    // Antigravity accounts first, runs automatic capture's pre-fetch step and
    // dedups the result, for both quota consumers.
    public static AgentUsageFetchCoordinator Shared { get; } = new(() =>
    {
        ClaudeExtraRoots.AwaitLaunch();
        return AntigravityFetch.Run(
            TbCore.AgentUsage, AntigravityAccounts.Installer, AntigravityAutoCapture.Shared);
    });

    /// <summary>Run <paramref name="action"/> once, on the fetch thread, before
    /// the first fetch this coordinator starts after the call. For the core's
    /// in-memory registries the app re-applies at launch (the Grok Bot
    /// consent): every agent-usage poll goes through <see cref="Shared"/>, so
    /// this orders "re-applied" before "first fetched" whichever surface polls
    /// first.</summary>
    public void RunBeforeFirstFetch(Action action)
    {
        lock (_gate)
        {
            _beforeFirstFetch = action;
        }
    }

    /// <summary>Single-flight and epoch-aware. A caller at the epoch the
    /// in-flight fetch was requested at joins it. A caller that saw a newer
    /// <see cref="QuotaEpoch"/> gets a fetch chained after it (never joined:
    /// its payload may predate the change; never parallel: at most one fetch
    /// delegate runs at a time), which then becomes the one later callers at
    /// that epoch join.</summary>
    public Task<AgentUsagePayload> FetchAsync()
    {
        lock (_gate)
        {
            var epoch = QuotaEpoch.Current;
            Task<AgentUsagePayload> next;
            var id = ++_lastId;
            if (_inFlight is { } current)
            {
                if (epoch <= _inFlightEpoch)
                {
                    return current;
                }

                next = current.ContinueWith(
                    _ =>
                    {
                        Action? chainedBefore;
                        lock (_gate)
                        {
                            chainedBefore = _beforeFirstFetch;
                            _beforeFirstFetch = null;
                            // Starts after any owed change: owes nothing yet.
                            _owed = false;
                        }

                        return FetchWithFollowUps(id, chainedBefore);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            else
            {
                var before = _beforeFirstFetch;
                _beforeFirstFetch = null;
                _owed = false;
                // The launch re-apply runs once, before the first fetch of the
                // chain; follow-ups owed during it do not run it again.
                next = Task.Run(() => FetchWithFollowUps(id, before));
            }

            _inFlight = next;
            _inFlightEpoch = epoch;
            _inFlightId = id;
            return next;
        }
    }

    /// <summary>Something the in-flight fetch may not have seen changed (the
    /// captured Antigravity list reached the core, agy's current account
    /// changed): owe one more fetch if one is in flight. With none in flight
    /// this does nothing; the caller's own refresh starts a fresh fetch.</summary>
    public void RequestFollowUp()
    {
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                _owed = true;
            }
        }
    }

    /// <summary>Runs the chain and clears <see cref="_inFlight"/> in the same
    /// locked decision that ends it. A completed task runs its continuations
    /// after it is marked complete, so clearing from a continuation left a
    /// window where a caller that saw the chain finished still found it in
    /// flight, lost its <see cref="RequestFollowUp"/> and joined the old
    /// payload. The id check keeps a chain from clearing a newer one chained
    /// after it.</summary>
    private AgentUsagePayload FetchWithFollowUps(long id, Action? before)
    {
        AgentUsagePayload payload;
        try
        {
            before?.Invoke();
            payload = fetch();
        }
        catch
        {
            Finish(id);
            throw;
        }

        for (var i = 0; ; i++)
        {
            lock (_gate)
            {
                if (i >= MaxFollowUps || !_owed)
                {
                    ClearIfCurrent(id);
                    return payload;
                }

                _owed = false;
            }

            try
            {
                payload = fetch();
            }
            catch
            {
                // A failed follow-up keeps the payload already fetched.
                Finish(id);
                return payload;
            }
        }
    }

    private void Finish(long id)
    {
        lock (_gate)
        {
            ClearIfCurrent(id);
        }
    }

    private void ClearIfCurrent(long id)
    {
        if (_inFlightId == id)
        {
            _inFlight = null;
            _inFlightEpoch = 0;
        }
    }
}
