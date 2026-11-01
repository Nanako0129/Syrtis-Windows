namespace TokenBar.App;

/// <summary>
/// Pure policy + injectable per-episode ForceCreate state machine.
/// Free of WinUI types so unit tests can inject attempt, delay, and a time
/// provider without constructing a tray.
/// </summary>
internal static class TrayForceCreatePolicy
{
    /// <summary>
    /// Monotonic budget for one tray-creation episode (normal launch).
    /// Logon autostart is the primary path; the shell can take several seconds
    /// to accept a tray icon. Exhaustion is elapsed-time based, not attempt-count.
    /// </summary>
    public const int EpisodeDeadlineMilliseconds = 10_000;

    /// <summary>
    /// Minimum total elapsed window the production soft-fail schedule must cover.
    /// Tests fail if a schedule change shrinks the recovery window.
    /// </summary>
    public const int MinProductionRetryWindowMilliseconds = EpisodeDeadlineMilliseconds;

    /// <summary>
    /// Upper bound for one --startup-smoke readiness wait (see TrayReadyGate).
    /// Three episode deadlines: a single episode always ends well inside it.
    /// </summary>
    public const int ReadyCeilingMilliseconds = EpisodeDeadlineMilliseconds * 3;

    /// <summary>Cap on a single inter-attempt delay (ms).</summary>
    public const int MaxDelayMilliseconds = 500;

    /// <summary>Base step for the linear backoff before the cap (ms).</summary>
    public const int DelayStepMilliseconds = 50;

    /// <summary>Delay before the next timer tick after a soft failure (ms).</summary>
    public static int DelayMilliseconds(int attemptNumberAfterFailure)
    {
        if (attemptNumberAfterFailure < 1)
        {
            return 0;
        }

        return Math.Min(DelayStepMilliseconds * attemptNumberAfterFailure, MaxDelayMilliseconds);
    }

    /// <summary>
    /// Only InvalidOperationException is soft-retryable (shell not ready).
    /// Everything else fails closed immediately.
    /// </summary>
    public static bool IsSoftRetryable(Exception exception) =>
        exception is InvalidOperationException;
}

/// <summary>Named failure when the smoke readiness ceiling expires.</summary>
internal sealed class TrayReadyCeilingException(TimeSpan ceiling) : TimeoutException(
    $"tray readiness not reached within the {ceiling.TotalMilliseconds:0} ms ceiling "
    + "(creation episodes kept restarting or the dispatcher is wedged)");

/// <summary>
/// The single terminal result of tray creation. Ready, an exhausted episode,
/// disposal and the ceiling all complete one TaskCompletionSource, so the first
/// writer wins and a waiter sees exactly one outcome. The episode deadline
/// alone cannot bound --startup-smoke: EnsureIconCreated can start a new
/// episode, restarting that deadline, while the last tick is still queued. The
/// ceiling is therefore an upper bound well above the episode deadline; its
/// expiry is a distinct <see cref="TrayReadyCeilingException"/>. It starts when
/// a caller waits (one timer per wait), so normal launches have no extra timer.
/// </summary>
internal sealed class TrayReadyGate
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly TimeSpan _ceiling;

    public TrayReadyGate(TimeProvider? timeProvider = null, TimeSpan? ceiling = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _ceiling = ceiling
            ?? TimeSpan.FromMilliseconds(TrayForceCreatePolicy.ReadyCeilingMilliseconds);
    }

    public bool TrySetReady() => _tcs.TrySetResult(true);

    public bool TrySetFailed(Exception exception) => _tcs.TrySetException(exception);

    public bool TrySetCanceled() => _tcs.TrySetCanceled();

    /// <summary>True once any terminal result (including the ceiling) landed.</summary>
    public bool IsCompleted => _tcs.Task.IsCompleted;

    /// <summary>
    /// The gate a waiter should use. With no creation episode running there is
    /// nothing that could ever complete a gate, so it fails at once instead of
    /// waiting out the ceiling. Otherwise <paramref name="current"/> is kept
    /// while pending and replaced once completed: a gate faulted by the ceiling
    /// must not fail every later wait.
    /// </summary>
    public static TrayReadyGate ForNextWait(
        TrayReadyGate? current, bool episodeRunning, TimeProvider? timeProvider = null)
    {
        if (current is { IsCompleted: false })
        {
            return current;
        }

        if (!episodeRunning)
        {
            throw new InvalidOperationException("tray creation episode was not started");
        }

        return new TrayReadyGate(timeProvider);
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        // One ceiling timer per wait, so a second or concurrent waiter is
        // bounded too and each timer is released when its own wait ends.
        using var timer = _time.CreateTimer(
            _ => _tcs.TrySetException(new TrayReadyCeilingException(_ceiling)),
            null, _ceiling, Timeout.InfiniteTimeSpan);
        await _tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
    }
}

/// <summary>Result of one ForceCreate tick inside an episode.</summary>
internal enum TrayForceCreateTickResult
{
    /// <summary>Icon reported created; stop retrying.</summary>
    Success,

    /// <summary>Soft failure; schedule another tick before the deadline.</summary>
    Continue,

    /// <summary>Episode elapsed deadline passed without success.</summary>
    Exhausted,
}

/// <summary>
/// Per-episode ForceCreate accounting driven by a monotonic elapsed deadline.
/// Call <see cref="Tick(Func{bool})"/> once per timer tick. This type never
/// sleeps or owns a timer; production schedules the next tick with
/// DispatcherQueueTimer and tests inject attempt, delay, and elapsed functions.
/// </summary>
internal sealed class TrayForceCreateEpisode
{
    private readonly TimeSpan _deadline;
    private readonly Func<TimeSpan> _getElapsed;

    public TrayForceCreateEpisode(
        TimeSpan? deadline = null,
        TimeProvider? timeProvider = null)
    {
        _deadline = deadline
            ?? TimeSpan.FromMilliseconds(TrayForceCreatePolicy.EpisodeDeadlineMilliseconds);
        if (_deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        // Monotonic timestamps (TimeProvider.System is Stopwatch-backed);
        // wall-clock NTP corrections at logon must not shrink or stretch the
        // tray-creation window. timeProvider is the single test seam.
        var clock = timeProvider ?? TimeProvider.System;
        var startTimestamp = clock.GetTimestamp();
        _getElapsed = () => clock.GetElapsedTime(startTimestamp);
    }

    public int Attempts { get; private set; }

    /// <summary>
    /// Monotonic elapsed time since the episode started. Negative injected
    /// values are clamped to zero so a bad test clock cannot invert exhaustion.
    /// </summary>
    public TimeSpan Elapsed
    {
        get
        {
            var elapsed = _getElapsed();
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
    }

    /// <summary>True when the monotonic deadline has been reached or passed.</summary>
    public bool IsExhausted => Elapsed >= _deadline;

    /// <summary>
    /// Delay computed for the next host-scheduled tick after a Continue result.
    /// Zero in terminal states. Never exceeds the delay cap or remaining budget.
    /// </summary>
    public int NextDelayMilliseconds { get; private set; }

    /// <summary>
    /// Runs one attempt using the production delay policy.
    /// </summary>
    public TrayForceCreateTickResult Tick(Func<bool> attempt) =>
        Tick(attempt, TrayForceCreatePolicy.DelayMilliseconds);

    /// <summary>
    /// Runs exactly one ForceCreate attempt unless the deadline has already
    /// passed. Propagates non-retryable attempt exceptions and invalid delay
    /// output. Soft failures return Continue while time remains, otherwise
    /// Exhausted. The injected delay function is called only when another tick
    /// is actually required.
    /// </summary>
    public TrayForceCreateTickResult Tick(
        Func<bool> attempt,
        Func<int, int> delayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(delayMilliseconds);

        if (IsExhausted)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        Attempts++;
        try
        {
            if (attempt())
            {
                NextDelayMilliseconds = 0;
                return TrayForceCreateTickResult.Success;
            }
        }
        catch (Exception ex) when (TrayForceCreatePolicy.IsSoftRetryable(ex))
        {
            // Soft miss — keep retrying until the monotonic deadline.
        }

        var remainingMs = (int)Math.Ceiling((_deadline - Elapsed).TotalMilliseconds);
        if (remainingMs <= 0)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        var delay = delayMilliseconds(Attempts);
        if (delay < 1)
        {
            throw new InvalidOperationException(
                $"ForceCreate retry delay must be positive after attempt {Attempts}.");
        }

        // Cap single-step delay, then never schedule past the episode deadline.
        delay = Math.Min(delay, TrayForceCreatePolicy.MaxDelayMilliseconds);
        delay = Math.Min(delay, remainingMs);
        if (delay < 1)
        {
            NextDelayMilliseconds = 0;
            return TrayForceCreateTickResult.Exhausted;
        }

        NextDelayMilliseconds = delay;
        return TrayForceCreateTickResult.Continue;
    }
}
