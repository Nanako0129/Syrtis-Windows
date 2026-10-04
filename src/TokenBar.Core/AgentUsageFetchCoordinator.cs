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

    public Task<AgentUsagePayload> FetchAsync()
    {
        lock (_gate)
        {
            if (_inFlight is { } current)
            {
                return current;
            }

            var before = _beforeFirstFetch;
            _beforeFirstFetch = null;
            _owed = false;
            // The launch re-apply runs once, before the first fetch of the
            // chain; follow-ups owed during it do not run it again.
            var next = Task.Run(() =>
            {
                before?.Invoke();
                return FetchWithFollowUps();
            });
            _inFlight = next;
            _ = next.ContinueWith(
                completed =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_inFlight, completed))
                        {
                            _inFlight = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

    private AgentUsagePayload FetchWithFollowUps()
    {
        var payload = fetch();
        for (var i = 0; i < MaxFollowUps; i++)
        {
            lock (_gate)
            {
                if (!_owed)
                {
                    break;
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
                break;
            }
        }

        return payload;
    }
}
