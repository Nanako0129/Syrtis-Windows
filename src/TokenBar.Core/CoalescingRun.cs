namespace TokenBar.Core;

/// <summary>One run at a time, and a request made while one is in flight is
/// not lost: it becomes exactly one more run when the current one ends, however
/// many requests arrived during it (coalesced, not queued). The quota lane's
/// gate, so an answer that changes what the core may read (the Grok Bot consent
/// card) is honoured by a fetch that starts after it rather than waiting for
/// the next tick.
/// <para>
/// Same shape as DashboardModel's lazy lane: the request flag is set BEFORE
/// the in-flight flag is tried, and the runner re-checks it after clearing
/// in-flight, so a request racing the end of a run is either picked up by the
/// loop or starts a run of its own.
/// </para></summary>
public sealed class CoalescingRun(Action<Action> schedule, Action run)
{
    private int _inFlight;
    private int _pending;

    /// <summary>Ask for a run. Starts one through <c>schedule</c> when idle;
    /// otherwise records one follow-up for the run in flight.</summary>
    public void Request()
    {
        Volatile.Write(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return;
        }

        schedule(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _pending, 0) == 1)
                {
                    run();
                }
            }
            finally
            {
                Volatile.Write(ref _inFlight, 0);
                if (Volatile.Read(ref _pending) == 1)
                {
                    Request();
                }
            }
        });
    }
}
