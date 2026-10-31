using TokenBar.App;

namespace TokenBar.Core.Tests;

public sealed class TrayForceCreatePolicyTests
{
    [Fact]
    public void Episode_deadline_covers_logon_scale_window()
    {
        Assert.True(
            TrayForceCreatePolicy.EpisodeDeadlineMilliseconds >= 10_000,
            "Episode deadline must cover a multi-second logon shell window.");
        Assert.Equal(
            TrayForceCreatePolicy.EpisodeDeadlineMilliseconds,
            TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds);
        Assert.True(TrayForceCreatePolicy.MaxDelayMilliseconds >= 50);
        Assert.True(
            TrayForceCreatePolicy.MaxDelayMilliseconds
            <= TrayForceCreatePolicy.EpisodeDeadlineMilliseconds);
    }

    [Fact]
    public void Only_InvalidOperationException_is_soft_retryable()
    {
        Assert.True(TrayForceCreatePolicy.IsSoftRetryable(new InvalidOperationException("shell")));
        Assert.False(TrayForceCreatePolicy.IsSoftRetryable(new ArgumentException("bad")));
        Assert.False(TrayForceCreatePolicy.IsSoftRetryable(new Exception("generic")));
        Assert.False(TrayForceCreatePolicy.IsSoftRetryable(new NotImplementedException("platform")));
    }

    [Fact]
    public void Delay_grows_with_attempt_number_and_is_capped()
    {
        Assert.Equal(0, TrayForceCreatePolicy.DelayMilliseconds(0));
        Assert.Equal(50, TrayForceCreatePolicy.DelayMilliseconds(1));
        Assert.Equal(100, TrayForceCreatePolicy.DelayMilliseconds(2));
        Assert.Equal(
            TrayForceCreatePolicy.MaxDelayMilliseconds,
            TrayForceCreatePolicy.DelayMilliseconds(100));
    }

    [Fact]
    public void Episode_stops_immediately_on_success()
    {
        var calls = 0;
        var delayCalls = 0;
        var episode = new TrayForceCreateEpisode();
        var result = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            attempt =>
            {
                delayCalls++;
                return attempt * 10;
            });

        Assert.Equal(TrayForceCreateTickResult.Success, result);
        Assert.Equal(1, calls);
        Assert.Equal(0, delayCalls);
        Assert.Equal(0, episode.NextDelayMilliseconds);
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Episode_soft_fail_then_success_same_episode()
    {
        var elapsed = TimeSpan.Zero;
        var calls = 0;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            getElapsed: () => elapsed);

        var soft = episode.Tick(
            () =>
            {
                calls++;
                throw new InvalidOperationException("shell not ready");
            },
            attempt => attempt * 10);
        Assert.Equal(TrayForceCreateTickResult.Continue, soft);
        Assert.Equal(10, episode.NextDelayMilliseconds);
        Assert.Equal(1, episode.Attempts);

        elapsed += TimeSpan.FromMilliseconds(episode.NextDelayMilliseconds);

        var ok = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            _ => 99);
        Assert.Equal(TrayForceCreateTickResult.Success, ok);
        Assert.Equal(0, episode.NextDelayMilliseconds);
        Assert.Equal(2, calls);
        Assert.Equal(2, episode.Attempts);
        Assert.False(episode.IsExhausted);
    }

    [Fact]
    public void Host_simulation_cancellation_stops_before_deadline()
    {
        using var cts = new CancellationTokenSource();
        var elapsed = TimeSpan.Zero;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            getElapsed: () => elapsed);
        var calls = 0;

        while (!episode.IsExhausted && !cts.IsCancellationRequested)
        {
            var result = episode.Tick(
                () =>
                {
                    calls++;
                    if (calls >= 2)
                    {
                        cts.Cancel();
                    }

                    throw new InvalidOperationException("shell not ready");
                },
                attempt => attempt * 10);

            if (cts.IsCancellationRequested || result != TrayForceCreateTickResult.Continue)
            {
                break;
            }

            elapsed += TimeSpan.FromMilliseconds(episode.NextDelayMilliseconds);
        }

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(2, calls);
        Assert.False(episode.IsExhausted);
        Assert.Equal(2, episode.Attempts);
    }

    [Fact]
    public void Production_soft_fail_schedule_covers_minimum_retry_window()
    {
        // Advance a fake monotonic clock by each production delay until Exhausted.
        var elapsed = TimeSpan.Zero;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(
                TrayForceCreatePolicy.EpisodeDeadlineMilliseconds),
            getElapsed: () => elapsed);

        var safety = 0;
        TrayForceCreateTickResult result;
        do
        {
            result = episode.Tick(
                () => throw new InvalidOperationException("shell not ready"));
            if (result == TrayForceCreateTickResult.Continue)
            {
                Assert.True(episode.NextDelayMilliseconds >= 1);
                Assert.True(
                    episode.NextDelayMilliseconds
                    <= TrayForceCreatePolicy.MaxDelayMilliseconds);
                elapsed += TimeSpan.FromMilliseconds(episode.NextDelayMilliseconds);
            }

            safety++;
            Assert.True(safety < 10_000, "episode failed to exhaust under fake clock");
        }
        while (result == TrayForceCreateTickResult.Continue);

        Assert.Equal(TrayForceCreateTickResult.Exhausted, result);
        Assert.True(
            elapsed.TotalMilliseconds
            >= TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds,
            $"production soft-fail window {elapsed.TotalMilliseconds}ms is below minimum "
            + $"{TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds}ms");
        Assert.True(episode.IsExhausted);
        Assert.True(episode.Attempts > 5, "time-based budget must allow more than five attempts");
    }

    [Fact]
    public void Episode_elapsed_ignores_backward_clock_steps()
    {
        // Wall-clock going backwards (NTP at logon) must not invert exhaustion
        // or extend the episode past the monotonic deadline. Injected elapsed
        // that steps backwards is clamped to zero for IsExhausted math after
        // production uses Stopwatch; here we prove the clamp on the seam.
        var elapsed = TimeSpan.FromMilliseconds(200);
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(500),
            getElapsed: () => elapsed);

        Assert.Equal(
            TrayForceCreateTickResult.Continue,
            episode.Tick(() => throw new InvalidOperationException("soft")));
        Assert.Equal(1, episode.Attempts);

        // Step "backwards" — must not throw and must not look like more time remaining
        // than before (clamped elapsed does not go negative).
        elapsed = TimeSpan.FromMilliseconds(-5_000);
        Assert.Equal(TimeSpan.Zero, episode.Elapsed);
        Assert.False(episode.IsExhausted);

        // Advance past deadline on the monotonic axis.
        elapsed = TimeSpan.FromMilliseconds(500);
        Assert.Equal(
            TrayForceCreateTickResult.Exhausted,
            episode.Tick(() => throw new InvalidOperationException("soft")));
        Assert.True(episode.IsExhausted);
        // Exhausted on entry does not consume another attempt.
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Episode_soft_failures_continue_until_deadline_then_exhaust()
    {
        var elapsed = TimeSpan.Zero;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(250),
            getElapsed: () => elapsed);

        TrayForceCreateTickResult Tick() => episode.Tick(
            () => throw new InvalidOperationException("shell not ready"),
            _ => 100);

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(100, episode.NextDelayMilliseconds);
        elapsed += TimeSpan.FromMilliseconds(100);

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(100, episode.NextDelayMilliseconds);
        elapsed += TimeSpan.FromMilliseconds(100);

        // Elapsed 200ms, remaining 50ms → delay clamped to remaining.
        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(50, episode.NextDelayMilliseconds);
        elapsed += TimeSpan.FromMilliseconds(50);

        // Elapsed == deadline → Exhausted on entry without another attempt.
        Assert.Equal(TrayForceCreateTickResult.Exhausted, Tick());
        Assert.Equal(0, episode.NextDelayMilliseconds);
        Assert.True(episode.IsExhausted);
        Assert.Equal(3, episode.Attempts);

        var calls = 0;
        var after = episode.Tick(
            () =>
            {
                calls++;
                return true;
            },
            _ => 1);
        Assert.Equal(TrayForceCreateTickResult.Exhausted, after);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Episode_rejects_non_positive_injected_delay()
    {
        var episode = new TrayForceCreateEpisode(deadline: TimeSpan.FromSeconds(5));

        var error = Assert.Throws<InvalidOperationException>(() =>
            episode.Tick(() => false, _ => 0));

        Assert.Contains("delay must be positive", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Episode_propagates_non_retryable_exceptions()
    {
        var episode = new TrayForceCreateEpisode(deadline: TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() =>
            episode.Tick(() => throw new ArgumentException("boom")));
        Assert.Equal(1, episode.Attempts);
    }

    [Fact]
    public void Separate_episodes_have_independent_deadlines()
    {
        var elapsed = TimeSpan.Zero;
        var first = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(50),
            getElapsed: () => elapsed);
        _ = first.Tick(() => throw new InvalidOperationException("a"));
        elapsed += TimeSpan.FromMilliseconds(first.NextDelayMilliseconds);
        Assert.Equal(
            TrayForceCreateTickResult.Exhausted,
            first.Tick(() => throw new InvalidOperationException("b")));
        Assert.True(first.IsExhausted);

        var second = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            getElapsed: () => elapsed);
        var ok = second.Tick(() => true);
        Assert.Equal(TrayForceCreateTickResult.Success, ok);
        Assert.Equal(1, second.Attempts);
        Assert.False(second.IsExhausted);
    }

    [Fact]
    public void No_ThreadSleep_in_TrayService_source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TokenBar.App", "TrayService.cs");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            dir = dir.Parent;
        }

        Assert.False(string.IsNullOrEmpty(path), "TrayService.cs not found from test base directory");
        var text = File.ReadAllText(path!);
        Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
    }

    // Wall clock for the whole class of NTP/RTC corrections at logon: a
    // provider whose UTC time jumps while its monotonic timestamps advance.
    private sealed class SteppedClock : TimeProvider
    {
        public long Ticks;
        public DateTimeOffset Utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Ticks;

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    [Fact]
    public void Default_episode_clock_is_monotonic_not_wall_time()
    {
        var clock = new SteppedClock();
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            timeProvider: clock);

        // Wall clock steps back an hour, then forward a day, while 4 s of
        // monotonic time pass: only the monotonic source may be consulted.
        clock.Utc = clock.Utc.AddHours(-1);
        clock.Ticks = 4_000;
        Assert.Equal(TimeSpan.FromSeconds(4), episode.Elapsed);
        Assert.False(episode.IsExhausted);

        clock.Utc = clock.Utc.AddDays(1);
        Assert.Equal(TimeSpan.FromSeconds(4), episode.Elapsed);

        clock.Ticks = 10_000;
        Assert.True(episode.IsExhausted);
    }

    [Fact]
    public async Task Ready_wait_has_no_timer_and_reports_the_episodes_own_terminal_result()
    {
        // Fake clock + manual TCS stand in for the DispatcherQueueTimer: the
        // terminal tick is "queued" for as long as the test withholds it, with
        // no real time passing. The wait must neither fault nor complete early.
        var elapsed = TimeSpan.Zero;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(TrayForceCreatePolicy.EpisodeDeadlineMilliseconds),
            getElapsed: () => elapsed);
        var terminal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = TrayReadyWait.AwaitAsync(terminal.Task, CancellationToken.None);

        // Dispatcher congested far past the episode deadline plus any margin.
        elapsed = TimeSpan.FromMilliseconds(TrayForceCreatePolicy.EpisodeDeadlineMilliseconds)
            + TimeSpan.FromMinutes(5);
        Assert.False(wait.IsCompleted);

        // The delayed terminal tick finally runs and publishes Exhausted.
        var result = episode.Tick(() => throw new InvalidOperationException("soft"));
        Assert.Equal(TrayForceCreateTickResult.Exhausted, result);
        terminal.SetException(new InvalidOperationException("episode exhausted"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Equal("episode exhausted", ex.Message);
    }

    [Fact]
    public async Task Ready_wait_returns_when_readiness_lands_after_the_deadline_instant()
    {
        var terminal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = TrayReadyWait.AwaitAsync(terminal.Task, CancellationToken.None);
        Assert.False(wait.IsCompleted);

        terminal.SetResult(true);
        await wait;
    }

    [Fact]
    public async Task Ready_wait_ends_early_only_through_the_callers_token()
    {
        var terminal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var wait = TrayReadyWait.AwaitAsync(terminal.Task, cts.Token);
        Assert.False(wait.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
