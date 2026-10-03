using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Shares one blocking agent-usage fetch across concurrent UI callers.</summary>
public sealed class AgentUsageFetchCoordinator(Func<AgentUsagePayload> fetch)
{
    private readonly object _gate = new();
    private Task<AgentUsagePayload>? _inFlight;
    private Action? _beforeFirstFetch;
    private long _generation;

    public static AgentUsageFetchCoordinator Shared { get; } = new(TbCore.AgentUsage);

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

    /// <summary>Read before <see cref="FetchAsync"/>; pass to
    /// <see cref="IsCurrent"/> before publishing what it returns.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>The core's inputs changed (the Grok Bot grant): the next
    /// <see cref="FetchAsync"/> starts a new fetch instead of joining one that
    /// began before this call, and every fetch begun before it fails
    /// <see cref="IsCurrent"/>, so its payload is discarded rather than
    /// published (macOS GrokBotKeychainConsent.apply's epoch signal).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _inFlight = null;
        }
    }

    /// <summary>False when <see cref="Invalidate"/> ran after
    /// <paramref name="generation"/> was read: the payload was (or may have
    /// been) built from the old inputs and must not be published.</summary>
    public bool IsCurrent(long generation) => Generation == generation;

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
