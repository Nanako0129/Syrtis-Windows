namespace TokenBar.Core;

/// <summary>One quota consumer's whole poll cycle (the dashboard, the tray):
/// single in-flight flag, fetch, publish only a payload that is still
/// current, rerun once when the epoch moved meanwhile, and wake on
/// <see cref="QuotaEpoch.Changed"/> so an idle consumer refetches right after
/// a consent change. The consumer's own writes happen only in
/// <c>apply</c>, run on <c>post</c>'s thread (the dispatcher), so a payload
/// built before an epoch change never reaches its state.</summary>
public sealed class QuotaPoller : IDisposable
{
    private readonly Func<Task<AgentUsagePayload?>> _fetch;
    private readonly Action<Action> _post;
    private readonly Action<AgentUsagePayload?> _apply;
    private readonly Action? _settled;
    private readonly Action<Exception>? _log;
    private int _inFlight;
    private int _disposed;

    /// <param name="fetch">One fetch (production: the shared coordinator).</param>
    /// <param name="post">Runs an action on the consumer's thread.</param>
    /// <param name="apply">The consumer's writes; null = the fetch failed. Not
    /// called when the epoch moved since the fetch began.</param>
    /// <param name="settled">After every posted action, applied or discarded
    /// (the tray's stale-age re-render).</param>
    /// <param name="log">A fetch exception, already treated as a null result.</param>
    public QuotaPoller(
        Func<Task<AgentUsagePayload?>> fetch,
        Action<Action> post,
        Action<AgentUsagePayload?> apply,
        Action? settled = null,
        Action<Exception>? log = null)
    {
        _fetch = fetch;
        _post = post;
        _apply = apply;
        _settled = settled;
        _log = log;
        QuotaEpoch.Changed += Request;
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        QuotaEpoch.Changed -= Request;
    }

    /// <summary>Start a fetch unless one is in flight (then it is dropped; the
    /// in-flight one reruns if the epoch moved).</summary>
    public void Request()
    {
        if (Volatile.Read(ref _disposed) == 1
            || Interlocked.Exchange(ref _inFlight, 1) == 1)
        {
            return;
        }

        var captured = QuotaEpoch.Current;
        _ = Task.Run(async () =>
        {
            try
            {
                AgentUsagePayload? payload = null;
                try
                {
                    payload = await _fetch().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Invoke(ex);
                }

                _post(() =>
                {
                    // Checked here, on the consumer's thread, so a change that
                    // lands between the fetch and this callback still discards.
                    if (QuotaEpoch.Current == captured)
                    {
                        _apply(payload);
                    }

                    _settled?.Invoke();
                });
            }
            finally
            {
                // Flag first, then compare: a signal before the clear is seen
                // by the compare, one after it starts its own fetch.
                Volatile.Write(ref _inFlight, 0);
                if (QuotaEpoch.Current != captured)
                {
                    Request();
                }
            }
        });
    }
}
