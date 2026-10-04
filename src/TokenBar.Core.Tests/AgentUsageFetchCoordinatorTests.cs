using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class AgentUsageFetchCoordinatorTests
{
    [Fact]
    public async Task ConcurrentCallersShareOneFetchAndNextCallStartsFresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var payload = new AgentUsagePayload("now", []);
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return payload;
        });

        var first = coordinator.FetchAsync();
        await started.Task;
        var second = coordinator.FetchAsync();

        Assert.Same(first, second);
        release.SetResult();
        Assert.Same(payload, await first);
        Assert.Same(payload, await second);
        Assert.Equal(1, Volatile.Read(ref calls));

        Assert.Same(payload, await coordinator.FetchAsync());
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>A follow-up requested while a fetch is in flight makes that
    /// task fetch exactly once more (two requests still owe one), and its
    /// callers get the newer payload; with nothing in flight it owes nothing.</summary>
    [Fact]
    public async Task AFollowUpRequestedDuringAFetchRunsExactlyOnceMore()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return new AgentUsagePayload(n.ToString(), []);
        });

        var first = coordinator.FetchAsync();
        await started.Task;
        coordinator.RequestFollowUp();
        coordinator.RequestFollowUp();
        release.SetResult();

        Assert.Equal("2", (await first).GeneratedAt);
        Assert.Equal(2, Volatile.Read(ref calls));

        coordinator.RequestFollowUp(); // nothing in flight: owes nothing
        Assert.Equal("3", (await coordinator.FetchAsync()).GeneratedAt);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    /// <summary>The fixed code never hands out a completed chain: the chain
    /// clears itself in the decision that ends it, so a caller that sees it
    /// finished and immediately requests a follow-up and fetches starts a new
    /// fetch. Looped because the old window (clearing from a continuation) was
    /// a race; this does not prove the old code fails, it pins the new
    /// behavior.</summary>
    [Fact]
    public async Task ACallerSeeingAChainFinishedAlwaysStartsANewFetch()
    {
        for (var i = 0; i < 1000; i++)
        {
            var calls = 0;
            var coordinator = new AgentUsageFetchCoordinator(
                () => new AgentUsagePayload(Interlocked.Increment(ref calls).ToString(), []));

            await coordinator.FetchAsync();
            coordinator.RequestFollowUp();
            Assert.Equal("2", (await coordinator.FetchAsync()).GeneratedAt);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
    }

    /// <summary>A chain's end clears only itself: when a newer-epoch caller
    /// chained B after A, A finishing must not drop B from flight, so a caller
    /// at B's epoch joins B and a follow-up requested during B is owed to B.</summary>
    [Fact]
    public async Task AFinishingChainDoesNotClearTheNewerChainChainedAfterIt()
    {
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                releaseA.Task.GetAwaiter().GetResult();
            }
            else if (n == 2)
            {
                startedB.TrySetResult();
                releaseB.Task.GetAwaiter().GetResult();
            }

            return new AgentUsagePayload(n.ToString(), []);
        });

        var a = coordinator.FetchAsync();
        QuotaEpoch.Signal();
        var b = coordinator.FetchAsync();
        Assert.NotSame(a, b);

        releaseA.SetResult();
        Assert.Equal("1", (await a).GeneratedAt);
        await startedB.Task;

        Assert.Same(b, coordinator.FetchAsync());
        coordinator.RequestFollowUp();
        releaseB.SetResult();

        Assert.Equal("3", (await b).GeneratedAt);
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal("4", (await coordinator.FetchAsync()).GeneratedAt);
    }

    /// <summary>A first fetch that throws leaves nothing in flight.</summary>
    [Fact]
    public async Task AThrowingFetchLeavesNothingInFlight()
    {
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return new AgentUsagePayload("ok", []);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FetchAsync());
        Assert.Equal("ok", (await coordinator.FetchAsync()).GeneratedAt);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>The launch re-apply (RunBeforeFirstFetch) and an owed
    /// follow-up share one fetch chain: the action runs once, before the first
    /// fetch, not again before the follow-up, and a later chain does not run
    /// it again (it was consumed).</summary>
    [Fact]
    public async Task TheLaunchReApplyRunsOncePerChainNotBeforeTheFollowUp()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            var n = Interlocked.Increment(ref calls);
            order.Enqueue("fetch" + n);
            if (n == 1)
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return new AgentUsagePayload(n.ToString(), []);
        });
        coordinator.RunBeforeFirstFetch(() => order.Enqueue("before"));

        var first = coordinator.FetchAsync();
        await started.Task;
        coordinator.RequestFollowUp();
        release.SetResult();

        Assert.Equal("2", (await first).GeneratedAt);
        Assert.Equal(["before", "fetch1", "fetch2"], order.ToList());

        Assert.Equal("3", (await coordinator.FetchAsync()).GeneratedAt);
        Assert.Equal(["before", "fetch1", "fetch2", "fetch3"], order.ToList());
    }

    [Fact]
    public async Task FailedFetchIsNotCached()
    {
        var calls = 0;
        var payload = new AgentUsagePayload("now", []);
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("synthetic failure");
            }

            return payload;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FetchAsync());
        Assert.Same(payload, await coordinator.FetchAsync());
        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
