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

    /// <summary>Raised with every fetch chain's resulting payload, whichever
    /// surface asked for it, so a consumer that did not ask (the tray feed,
    /// when the flyout polled) still holds the newest payload instead of one
    /// up to a slow tick old. Raised on the fetch thread, before the chain
    /// completes and before the in-flight slot clears. Within one epoch
    /// payloads are raised in fetch order; a fetch chained for a newer epoch
    /// may start first, but the payload raised before it carries the older
    /// epoch and is discarded. A handler's <see cref="FetchAsync"/> joins the
    /// chain being raised for, and its <see cref="RequestFollowUp"/> is
    /// honoured by that chain (up to the follow-up cap; the payload that
    /// follow-up fetches is raised too). A throwing handler is caught and
    /// logged by exception type: it neither fails the fetch nor pins the slot.
    /// Not raised for a failed fetch. A handler must marshal to its own
    /// thread. Carries the <see cref="QuotaEpoch"/> the fetch was requested
    /// at: a handler must discard a payload whose epoch is no longer current,
    /// as <see cref="QuotaPoller"/> does.</summary>
    public event Action<AgentUsagePayload, long>? Fetched;

    /// <summary>Where a throwing <see cref="Fetched"/> handler is logged (the
    /// exception type only). Silent until the host points it somewhere.</summary>
    public Action<string> Log { get; set; } = _ => { };

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
            if (_inFlight is { } joined && epoch <= _inFlightEpoch)
            {
                return joined;
            }

            Task<AgentUsagePayload> next;
            var id = ++_lastId;
            if (_inFlight is { } current)
            {
                next = current.ContinueWith(
                    _ => FetchWithFollowUps(id, epoch, null, chained: true),
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
                next = Task.Run(() => FetchWithFollowUps(id, epoch, before, chained: false));
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

    /// <summary>Runs the chain, raises <see cref="Fetched"/> for its result
    /// and clears <see cref="_inFlight"/> in the same locked decision that
    /// ends it. A completed task runs its continuations after it is marked
    /// complete, so clearing from a continuation left a window where a caller
    /// that saw the chain finished still found it in flight, lost its
    /// <see cref="RequestFollowUp"/> and joined the old payload. The in-lock
    /// clear closes that race; the finally is the net for every other exit (a
    /// throwing action or fetch). The id check keeps a chain from clearing a
    /// newer one chained after it. A payload superseded by an owed follow-up
    /// is not raised; the result is raised once, and a follow-up a handler
    /// requests while it is raised makes the chain fetch (and raise) again.</summary>
    private AgentUsagePayload FetchWithFollowUps(long id, long epoch, Action? before, bool chained)
    {
        try
        {
            if (chained)
            {
                lock (_gate)
                {
                    before = _beforeFirstFetch;
                    _beforeFirstFetch = null;
                    // Starts after any owed change: owes nothing yet.
                    _owed = false;
                }
            }

            before?.Invoke();
            var payload = fetch();
            var raised = false;
            var failed = false;
            for (var i = 0; ; )
            {
                bool again;
                lock (_gate)
                {
                    again = !failed && i < MaxFollowUps && _owed;
                    if (again)
                    {
                        _owed = false;
                    }
                    else if (raised)
                    {
                        ClearIfCurrent(id);
                        return payload;
                    }
                }

                if (!again)
                {
                    Raise(payload, epoch);
                    raised = true;
                    continue;
                }

                try
                {
                    payload = fetch();
                    i++;
                    raised = false;
                }
                catch
                {
                    // A failed follow-up keeps the payload already fetched
                    // (raised once, by the end decision above).
                    failed = true;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                ClearIfCurrent(id);
            }
        }
    }

    private void Raise(AgentUsagePayload payload, long epoch)
    {
        try
        {
            Fetched?.Invoke(payload, epoch);
        }
        catch (Exception ex)
        {
            // A handler's failure must not fail the fetch or pin the slot.
            Log($"agentUsage fetched handler failed: {ex.GetType().Name}");
        }
    }

    private void ClearIfCurrent(long id)
    {
        if (_inFlightId == id)
        {
            _inFlight = null;
        }
    }
}
