using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Shares one blocking agent-usage fetch across concurrent UI callers.</summary>
public sealed class AgentUsageFetchCoordinator(Func<AgentUsagePayload> fetch)
{
    private readonly object _gate = new();
    private Task<AgentUsagePayload>? _inFlight;
    private long _inFlightEpoch;
    private Action? _beforeFirstFetch;

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
                        }

                        chainedBefore?.Invoke();
                        return fetch();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            else
            {
                var before = _beforeFirstFetch;
                _beforeFirstFetch = null;
                next = Task.Run(() =>
                {
                    before?.Invoke();
                    return fetch();
                });
            }

            _inFlight = next;
            _inFlightEpoch = epoch;
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
