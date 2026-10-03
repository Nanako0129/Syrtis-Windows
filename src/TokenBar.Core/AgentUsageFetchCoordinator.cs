using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Shares one blocking agent-usage fetch across concurrent UI callers.</summary>
public sealed class AgentUsageFetchCoordinator(Func<AgentUsagePayload> fetch)
{
    private readonly object _gate = new();
    private Task<AgentUsagePayload>? _inFlight;
    private Action? _beforeFirstFetch;

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
            var next = Task.Run(() =>
            {
                before?.Invoke();
                return fetch();
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
}
