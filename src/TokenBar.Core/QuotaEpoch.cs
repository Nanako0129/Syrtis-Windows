namespace TokenBar.Core;

/// <summary>The one epoch source for "an input of the quota fetch changed"
/// (today: the Grok Bot consent grant). Mirrors macOS <c>RegistryChange</c>:
/// <see cref="Signal"/> bumps <see cref="Current"/> and raises
/// <see cref="Changed"/>; a fetch that began at an older epoch may carry a
/// payload built before the change, so its consumers discard it (see
/// <see cref="QuotaPoller"/>) and the coordinator never joins it for a caller
/// that already saw the newer epoch (<see cref="AgentUsageFetchCoordinator"/>).
/// </summary>
public static class QuotaEpoch
{
    private static long _current;

    public static long Current => Interlocked.Read(ref _current);

    /// <summary>Raised on the signalling thread, after <see cref="Current"/>
    /// moved. Handlers must not block.</summary>
    public static event Action? Changed;

    public static void Signal()
    {
        Interlocked.Increment(ref _current);
        Changed?.Invoke();
    }
}
