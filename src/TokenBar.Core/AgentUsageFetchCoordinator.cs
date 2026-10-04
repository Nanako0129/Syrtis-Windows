using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Shares one blocking agent-usage fetch across concurrent UI callers.</summary>
public sealed class AgentUsageFetchCoordinator(Func<AgentUsagePayload> fetch)
{
    private readonly object _gate = new();
    private Task<AgentUsagePayload>? _inFlight;
    private Action? _beforeFirstFetch;

    /// <summary>Raised with every successfully fetched payload, whichever
    /// surface asked for it, so a consumer that did not ask (the tray feed,
    /// when the flyout polled) still holds the newest payload instead of one
    /// up to a slow tick old. Raised on the fetch thread, before the next
    /// fetch can start, so payloads are raised in fetch order; a handler must
    /// marshal to its own thread. Not raised for a failed fetch.</summary>
    public event Action<AgentUsagePayload>? Fetched;

    // Waits for the launch push so the first fetch already carries the extra
    // Claude accounts' cards.
    public static AgentUsageFetchCoordinator Shared { get; } = new(() =>
    {
        ClaudeExtraRoots.AwaitLaunch();
        return TbCore.AgentUsage();
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
                    // Before the in-flight slot clears: no later fetch exists
                    // yet, so handlers see payloads in fetch order.
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        Fetched?.Invoke(completed.Result);
                    }

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
