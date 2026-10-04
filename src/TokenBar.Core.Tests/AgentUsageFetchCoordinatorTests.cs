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

    // The tray feed adopts every fetch, the flyout's included, through this
    // event; Settings reads the feed. Before it existed the feed kept its own
    // fetch only, up to a slow tick behind the flyout.
    [Fact]
    public async Task EverySuccessfulFetchIsRaisedOnceWhoeverAskedFor()
    {
        var payload = new AgentUsagePayload("now", []);
        using var bothJoined = new ManualResetEventSlim();
        var coordinator = new AgentUsageFetchCoordinator(() =>
        {
            bothJoined.Wait(TimeSpan.FromSeconds(5));
            return payload;
        });
        var raised = new List<AgentUsagePayload>();
        coordinator.Fetched += p => { lock (raised) { raised.Add(p); } };

        var first = coordinator.FetchAsync();
        var second = coordinator.FetchAsync();
        bothJoined.Set();
        await Task.WhenAll(first, second);
        await WaitFor(() => raised.Count >= 1);

        Assert.Same(payload, Assert.Single(raised));
    }

    [Fact]
    public async Task AFailedFetchIsNotRaised()
    {
        var coordinator = new AgentUsageFetchCoordinator(
            () => throw new InvalidOperationException("synthetic failure"));
        var raised = 0;
        coordinator.Fetched += _ => Interlocked.Increment(ref raised);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FetchAsync());
        await Task.Delay(50);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    // Raised before the in-flight slot clears: a handler that asks again gets
    // the fetch it was raised for, not a new one, so payloads are raised in
    // fetch order and an older one cannot land after a newer one.
    [Fact]
    public async Task APayloadIsRaisedBeforeTheNextFetchCanStart()
    {
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
            new AgentUsagePayload($"call-{Interlocked.Increment(ref calls)}", []));
        Task<AgentUsagePayload>? askedFromHandler = null;
        coordinator.Fetched += _ => askedFromHandler ??= coordinator.FetchAsync();

        var first = await coordinator.FetchAsync();
        await WaitFor(() => askedFromHandler is not null);

        Assert.Equal("call-1", first.GeneratedAt);
        Assert.Equal("call-1", (await askedFromHandler!).GeneratedAt);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    // A handler that throws must not leave the finished fetch in flight,
    // where every later caller would get it back instead of a fresh fetch.
    [Fact]
    public async Task AThrowingHandlerDoesNotPinTheFinishedFetch()
    {
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
            new AgentUsagePayload($"call-{Interlocked.Increment(ref calls)}", []));
        coordinator.Fetched += _ => throw new InvalidOperationException("handler");

        await coordinator.FetchAsync();
        await WaitFor(() => Volatile.Read(ref calls) == 1);
        await Task.Delay(50);
        var second = await coordinator.FetchAsync();

        Assert.Equal("call-2", second.GeneratedAt);
    }
}
