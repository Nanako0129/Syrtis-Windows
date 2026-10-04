using TokenBar.Interop;

namespace TokenBar.Core.Tests;

// W6c. QuotaEpoch is process-wide static; the assembly runs serially
// (AssemblyInfo.cs), and each test reasons relative to values it captured.
public class QuotaEpochTests
{
    private static readonly AgentUsagePayload Payload = new("now", []);

    [Fact]
    public void SignalRaisesChangedOnceAndIncrementsCurrent()
    {
        var raised = 0;
        void Count() => Interlocked.Increment(ref raised);
        var before = QuotaEpoch.Current;
        QuotaEpoch.Changed += Count;
        try
        {
            QuotaEpoch.Signal();
        }
        finally
        {
            QuotaEpoch.Changed -= Count;
        }

        Assert.Equal(1, raised);
        Assert.Equal(before + 1, QuotaEpoch.Current);
    }

    // A fetch delegate that parks until released, counting concurrency.
    private sealed class GatedFetch
    {
        private readonly List<TaskCompletionSource> _gates = [];
        private int _running;
        public int Started;
        public int MaxConcurrent;

        public AgentUsagePayload Run()
        {
            TaskCompletionSource gate;
            lock (_gates)
            {
                gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Add(gate);
                Started++;
            }

            var now = Interlocked.Increment(ref _running);
            lock (_gates)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, now);
            }

            gate.Task.GetAwaiter().GetResult();
            Interlocked.Decrement(ref _running);
            return Payload;
        }

        public int StartedCount
        {
            get { lock (_gates) { return Started; } }
        }

        public void Release(int index)
        {
            TaskCompletionSource gate;
            lock (_gates)
            {
                gate = _gates[index];
            }

            gate.SetResult();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task WithoutASignalAnInFlightFetchIsJoined()
    {
        var fetch = new GatedFetch();
        var coordinator = new AgentUsageFetchCoordinator(fetch.Run);
        var a = coordinator.FetchAsync();
        await WaitUntil(() => fetch.StartedCount == 1);
        Assert.Same(a, coordinator.FetchAsync());
        fetch.Release(0);
        await a;
    }

    [Fact]
    public async Task ASignalMakesTheNextCallChainAfterTheInFlightFetch()
    {
        var fetch = new GatedFetch();
        var coordinator = new AgentUsageFetchCoordinator(fetch.Run);
        var a = coordinator.FetchAsync();
        await WaitUntil(() => fetch.StartedCount == 1);
        QuotaEpoch.Signal();
        var b = coordinator.FetchAsync();
        Assert.NotSame(a, b);
        // Not joined and not parallel: the second delegate has not started.
        await Task.Delay(100);
        Assert.Equal(1, fetch.StartedCount);
        Assert.False(b.IsCompleted);
        // Callers at the same (new) epoch join the chained fetch.
        Assert.Same(b, coordinator.FetchAsync());
        fetch.Release(0);
        await a;
        await WaitUntil(() => fetch.StartedCount == 2);
        fetch.Release(1);
        await b;
        Assert.Equal(1, fetch.MaxConcurrent);
    }

    [Fact]
    public async Task ConcurrentCallersAcrossSignalsNeverOverlapFetches()
    {
        var fetch = new GatedFetch();
        var coordinator = new AgentUsageFetchCoordinator(fetch.Run);
        var tasks = new List<Task<AgentUsagePayload>>();
        for (var m = 0; m < 4; m++)
        {
            // Task.Run(void) so the call returns at once; FetchAsync itself
            // does not block.
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                var t = coordinator.FetchAsync();
                lock (tasks)
                {
                    tasks.Add(t);
                }
            })));
            QuotaEpoch.Signal();
        }

        tasks.Add(coordinator.FetchAsync());
        for (var i = 0; i < 5; i++)
        {
            await WaitUntil(() => fetch.StartedCount > i);
            fetch.Release(i);
        }

        await Task.WhenAll(tasks);
        Assert.Equal(5, fetch.StartedCount);
        Assert.Equal(1, fetch.MaxConcurrent);
    }

    // Test doubles for the poller: a queue instead of a dispatcher.
    private sealed class Harness : IDisposable
    {
        public readonly List<Action> Posted = [];
        public readonly List<AgentUsagePayload?> Applied = [];
        public int Settled;
        public int Fetches;
        public Func<Task<AgentUsagePayload?>> Next = () => Task.FromResult<AgentUsagePayload?>(Payload);
        public readonly QuotaPoller Poller;

        public Harness()
        {
            Poller = new QuotaPoller(
                () =>
                {
                    Interlocked.Increment(ref Fetches);
                    return Next();
                },
                action => { lock (Posted) { Posted.Add(action); } },
                payload => { lock (Applied) { Applied.Add(payload); } },
                () => Interlocked.Increment(ref Settled));
        }

        public int PostedCount
        {
            get { lock (Posted) { return Posted.Count; } }
        }

        public void RunPosted()
        {
            Action[] batch;
            lock (Posted)
            {
                batch = [.. Posted];
                Posted.Clear();
            }

            foreach (var action in batch)
            {
                action();
            }
        }

        public void Dispose() => Poller.Dispose();
    }

    [Fact]
    public async Task PayloadIsAppliedOnlyIfTheEpochStillMatchesWhenThePostedActionRuns()
    {
        using var current = new Harness();
        current.Poller.Request();
        await WaitUntil(() => current.PostedCount == 1);
        current.RunPosted();
        Assert.Equal([Payload], current.Applied);
        Assert.Equal(1, current.Settled);

        using var stale = new Harness();
        stale.Poller.Request();
        await WaitUntil(() => stale.PostedCount == 1);
        QuotaEpoch.Signal(); // fetch finished, posted action not yet run
        stale.RunPosted();
        Assert.Empty(stale.Applied);
        Assert.Equal(1, stale.Settled); // the re-render still runs when discarded
    }

    // Rerun-once for each way a fetch can end: payload, null, throws.
    public static TheoryData<int> Outcomes() => [0, 1, 2];

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task ASignalDuringAFetchTriggersExactlyOneRerun(int outcome)
    {
        using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        h.Next = async () =>
        {
            if (!first)
            {
                return Payload;
            }

            first = false;
            await release.Task;
            return outcome switch
            {
                0 => Payload,
                1 => null,
                _ => throw new InvalidOperationException("synthetic"),
            };
        };
        h.Poller.Request();
        await WaitUntil(() => h.Fetches == 1);
        QuotaEpoch.Signal(); // dropped by the wake (in flight), covered by the rerun
        release.SetResult();
        await WaitUntil(() => h.Fetches == 2);
        await Task.Delay(100);
        Assert.Equal(2, h.Fetches);
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task WithoutASignalThereIsNoRerun(int outcome)
    {
        using var h = new Harness();
        h.Next = () => outcome switch
        {
            0 => Task.FromResult<AgentUsagePayload?>(Payload),
            1 => Task.FromResult<AgentUsagePayload?>(null),
            _ => throw new InvalidOperationException("synthetic"),
        };
        h.Poller.Request();
        await WaitUntil(() => h.PostedCount == 1);
        await Task.Delay(100);
        Assert.Equal(1, h.Fetches);
    }

    // Acceptance 8: the idle consumer is woken by the signal itself.
    [Fact]
    public async Task AnIdlePollerFetchesOnceAndAppliesOnceOnASignal()
    {
        using var h = new Harness();
        QuotaEpoch.Signal();
        await WaitUntil(() => h.PostedCount == 1);
        h.RunPosted();
        await Task.Delay(100);
        Assert.Equal(1, h.Fetches);
        Assert.Equal([Payload], h.Applied);
    }

    [Fact]
    public async Task ADisposedPollerIgnoresSignals()
    {
        var h = new Harness();
        h.Dispose();
        QuotaEpoch.Signal();
        await Task.Delay(100);
        Assert.Equal(0, h.Fetches);
    }
}
