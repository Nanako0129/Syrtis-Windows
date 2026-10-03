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

    // The Grok Bot grant: a fetch begun before Invalidate was built without
    // it. The next caller must get a new core fetch, not join the old one, and
    // the old fetch's payload must fail the publish check while the new one's
    // passes.
    [Fact]
    public async Task InvalidateStartsAFreshFetchAndDiscardsTheOlderOne()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return new AgentUsagePayload("now", []);
        });

        var before = coordinator.Generation;
        var old = coordinator.FetchAsync();
        await started.Task;
        coordinator.Invalidate();
        var after = coordinator.Generation;
        var fresh = coordinator.FetchAsync();

        Assert.NotSame(old, fresh);
        release.SetResult();
        Assert.NotSame(await old, await fresh);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.False(coordinator.IsCurrent(before));
        Assert.True(coordinator.IsCurrent(after));
    }
}
