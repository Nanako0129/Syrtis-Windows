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
        var clock = new FakeClock { Now = TimeSpan.Zero };
        var calls = 0;
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            timeProvider: clock);

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

        clock.Now += TimeSpan.FromMilliseconds(episode.NextDelayMilliseconds);

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
        var clock = new FakeClock { Now = TimeSpan.Zero };
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            timeProvider: clock);
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

            clock.Now += TimeSpan.FromMilliseconds(episode.NextDelayMilliseconds);
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
        var clock = new FakeClock { Now = TimeSpan.Zero };
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(
                TrayForceCreatePolicy.EpisodeDeadlineMilliseconds),
            timeProvider: clock);

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
                clock.Now += TimeSpan.FromMilliseconds(episode.NextDelayMilliseconds);
            }

            safety++;
            Assert.True(safety < 10_000, "episode failed to exhaust under fake clock");
        }
        while (result == TrayForceCreateTickResult.Continue);

        Assert.Equal(TrayForceCreateTickResult.Exhausted, result);
        Assert.True(
            clock.Now.TotalMilliseconds
            >= TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds,
            $"production soft-fail window {clock.Now.TotalMilliseconds}ms is below minimum "
            + $"{TrayForceCreatePolicy.MinProductionRetryWindowMilliseconds}ms");
        Assert.True(episode.IsExhausted);
        Assert.True(episode.Attempts > 5, "time-based budget must allow more than five attempts");
    }

    [Fact]
    public void Episode_elapsed_ignores_backward_clock_steps()
    {
        // Wall-clock going backwards (NTP at logon) must not invert exhaustion
        // or extend the episode past the monotonic deadline. Elapsed
        // that steps backwards is clamped to zero for IsExhausted math.
        var clock = new FakeClock { Now = TimeSpan.FromMilliseconds(200) };
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(500),
            timeProvider: clock);

        Assert.Equal(
            TrayForceCreateTickResult.Continue,
            episode.Tick(() => throw new InvalidOperationException("soft")));
        Assert.Equal(1, episode.Attempts);

        // Step "backwards" — must not throw and must not look like more time remaining
        // than before (clamped elapsed does not go negative).
        clock.Now = TimeSpan.FromMilliseconds(-5_000);
        Assert.Equal(TimeSpan.Zero, episode.Elapsed);
        Assert.False(episode.IsExhausted);

        // Advance past deadline on the monotonic axis.
        clock.Now = TimeSpan.FromMilliseconds(700);
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
        var clock = new FakeClock { Now = TimeSpan.Zero };
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(250),
            timeProvider: clock);

        TrayForceCreateTickResult Tick() => episode.Tick(
            () => throw new InvalidOperationException("shell not ready"),
            _ => 100);

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(100, episode.NextDelayMilliseconds);
        clock.Now += TimeSpan.FromMilliseconds(100);

        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(100, episode.NextDelayMilliseconds);
        clock.Now += TimeSpan.FromMilliseconds(100);

        // Elapsed 200ms, remaining 50ms → delay clamped to remaining.
        Assert.Equal(TrayForceCreateTickResult.Continue, Tick());
        Assert.Equal(50, episode.NextDelayMilliseconds);
        clock.Now += TimeSpan.FromMilliseconds(50);

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
        var clock = new FakeClock { Now = TimeSpan.Zero };
        var first = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromMilliseconds(50),
            timeProvider: clock);
        _ = first.Tick(() => throw new InvalidOperationException("a"));
        clock.Now += TimeSpan.FromMilliseconds(first.NextDelayMilliseconds);
        Assert.Equal(
            TrayForceCreateTickResult.Exhausted,
            first.Tick(() => throw new InvalidOperationException("b")));
        Assert.True(first.IsExhausted);

        var second = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            timeProvider: clock);
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

    // Deterministic TimeProvider: Now is the monotonic timestamp axis; moving it
    // forward fires due timers. Thread-safe, because gate waits dispose their
    // timers from continuation threads while the test thread moves time.
    private sealed class FakeClock : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private TimeSpan _now;

        public TimeSpan Now
        {
            get { lock (_lock) { return _now; } }
            set
            {
                FakeTimer[] due;
                lock (_lock)
                {
                    _now = value;
                    due = _timers.Where(t => t.IsDue(value)).ToArray();
                }

                foreach (var t in due)
                {
                    t.Fire();
                }
            }
        }

        public int LiveTimers
        {
            get { lock (_lock) { return _timers.Count; } }
        }

        public DateTimeOffset Utc { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now.Ticks;

        public override DateTimeOffset GetUtcNow() => Utc;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                var timer = new FakeTimer(this, callback, state, _now + dueTime);
                _timers.Add(timer);
                return timer;
            }
        }

        private sealed class FakeTimer(
            FakeClock owner, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private bool _done;

            public bool IsDue(TimeSpan now)
            {
                lock (owner._lock) { return !_done && now >= due; }
            }

            public void Fire()
            {
                lock (owner._lock)
                {
                    if (_done)
                    {
                        return;
                    }

                    _done = true;
                }

                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
                lock (owner._lock)
                {
                    _done = true;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void Default_episode_clock_is_monotonic_not_wall_time()
    {
        var clock = new FakeClock();
        var episode = new TrayForceCreateEpisode(
            deadline: TimeSpan.FromSeconds(10),
            timeProvider: clock);

        // Wall clock steps back an hour, then forward a day, while 4 s of
        // monotonic time pass: only the monotonic source may be consulted.
        clock.Utc = clock.Utc.AddHours(-1);
        clock.Now = TimeSpan.FromSeconds(4);
        Assert.Equal(TimeSpan.FromSeconds(4), episode.Elapsed);
        Assert.False(episode.IsExhausted);

        clock.Utc = clock.Utc.AddDays(1);
        Assert.Equal(TimeSpan.FromSeconds(4), episode.Elapsed);

        clock.Now = TimeSpan.FromSeconds(10);
        Assert.True(episode.IsExhausted);
    }

    // Real-time bound used only so a broken gate fails the test instead of
    // hanging the run; passing runs never wait on it.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Ceiling =
        TimeSpan.FromMilliseconds(TrayForceCreatePolicy.ReadyCeilingMilliseconds);

    [Fact]
    public async Task Ready_before_the_ceiling_succeeds_and_releases_the_ceiling_timer()
    {
        var clock = new FakeClock();
        var gate = new TrayReadyGate(clock);
        var wait = gate.WaitAsync(CancellationToken.None);
        Assert.Equal(1, clock.LiveTimers);

        clock.Now = Ceiling - TimeSpan.FromMilliseconds(1);
        Assert.False(wait.IsCompleted);
        Assert.True(gate.TrySetReady());
        await wait.WaitAsync(Settle);

        Assert.Equal(0, clock.LiveTimers);
        clock.Now = Ceiling + TimeSpan.FromMinutes(1);
        Assert.False(gate.TrySetFailed(new InvalidOperationException("late")));
    }

    [Fact]
    public async Task Restarting_episodes_still_end_at_the_ceiling_with_the_named_failure()
    {
        var clock = new FakeClock();
        var gate = new TrayReadyGate(clock);
        var wait = gate.WaitAsync(CancellationToken.None);

        // A new episode starts one second before each deadline, so no episode
        // ever reaches Exhausted and nothing else completes the gate. Time moves
        // exactly to each boundary: 9 s, 18 s, 27 s, then the 30 s ceiling.
        var deadline = TimeSpan.FromMilliseconds(TrayForceCreatePolicy.EpisodeDeadlineMilliseconds);
        var step = deadline - TimeSpan.FromSeconds(1);
        var episode = new TrayForceCreateEpisode(deadline, clock);
        for (var i = 1; i <= 3; i++)
        {
            clock.Now = step * i;
            Assert.NotEqual(
                TrayForceCreateTickResult.Exhausted,
                episode.Tick(() => throw new InvalidOperationException("soft")));
            episode = new TrayForceCreateEpisode(deadline, clock);
        }

        Assert.Equal(TimeSpan.FromSeconds(27), clock.Now);
        Assert.False(wait.IsCompleted);
        clock.Now = Ceiling;
        await Assert.ThrowsAsync<TrayReadyCeilingException>(() => wait.WaitAsync(Settle));
        Assert.Equal(0, clock.LiveTimers);
    }

    [Fact]
    public async Task The_first_terminal_result_wins_over_the_ceiling()
    {
        var clock = new FakeClock();
        var gate = new TrayReadyGate(clock);
        var wait = gate.WaitAsync(CancellationToken.None);

        Assert.True(gate.TrySetFailed(new InvalidOperationException("episode exhausted")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait.WaitAsync(Settle));
        Assert.Equal("episode exhausted", ex.Message);

        clock.Now = Ceiling;
        Assert.Equal(0, clock.LiveTimers);
    }

    [Fact]
    public async Task Caller_cancellation_ends_the_wait_and_a_later_wait_still_has_a_ceiling()
    {
        var clock = new FakeClock();
        var gate = new TrayReadyGate(clock);
        using var cts = new CancellationTokenSource();
        var first = gate.WaitAsync(cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Settle));
        Assert.False(gate.IsCompleted);
        Assert.Equal(0, clock.LiveTimers);

        // The second wait arms its own ceiling; a concurrent third is bounded too.
        var second = gate.WaitAsync(CancellationToken.None);
        var third = gate.WaitAsync(CancellationToken.None);
        Assert.Equal(2, clock.LiveTimers);
        clock.Now = Ceiling;
        await Assert.ThrowsAsync<TrayReadyCeilingException>(() => second.WaitAsync(Settle));
        await Assert.ThrowsAsync<TrayReadyCeilingException>(() => third.WaitAsync(Settle));
        Assert.Equal(0, clock.LiveTimers);
    }

    [Fact]
    public async Task A_gate_faulted_by_the_ceiling_is_replaced_for_the_next_wait()
    {
        var clock = new FakeClock();
        var pending = new TrayReadyGate(clock);
        Assert.Same(pending, TrayReadyGate.ForNextWait(pending, clock));

        var wait = pending.WaitAsync(CancellationToken.None);
        clock.Now = Ceiling;
        await Assert.ThrowsAsync<TrayReadyCeilingException>(() => wait.WaitAsync(Settle));
        Assert.True(pending.IsCompleted);

        var next = TrayReadyGate.ForNextWait(pending, clock);
        Assert.NotSame(pending, next);
        var nextWait = next.WaitAsync(CancellationToken.None);
        Assert.True(next.TrySetReady());
        await nextWait.WaitAsync(Settle);
        Assert.Equal(0, clock.LiveTimers);
    }
}
