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

    /// <summary>A follow-up that throws keeps the first payload and leaves
    /// nothing in flight.</summary>
    [Fact]
    public async Task AThrowingFollowUpReturnsTheFirstPayloadAndLeavesNothingInFlight()
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
            else if (n == 2)
            {
                throw new InvalidOperationException("follow-up failed");
            }

            return new AgentUsagePayload(n.ToString(), []);
        });

        var first = coordinator.FetchAsync();
        await started.Task;
        coordinator.RequestFollowUp();
        release.SetResult();

        Assert.Equal("1", (await first).GeneratedAt);
        Assert.Equal("3", (await coordinator.FetchAsync()).GeneratedAt);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    /// <summary>A launch re-apply that throws faults the task and leaves
    /// nothing in flight.</summary>
    [Fact]
    public async Task AThrowingLaunchReApplyFaultsTheTaskAndLeavesNothingInFlight()
    {
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(
            () => new AgentUsagePayload(Interlocked.Increment(ref calls).ToString(), []));
        coordinator.RunBeforeFirstFetch(() => throw new InvalidOperationException("re-apply failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FetchAsync());
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("1", (await coordinator.FetchAsync()).GeneratedAt);
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
        coordinator.Fetched += (p, _) => { lock (raised) { raised.Add(p); } };

        var first = coordinator.FetchAsync();
        var second = coordinator.FetchAsync();
        bothJoined.Set();
        await Task.WhenAll(first, second);

        Assert.Same(payload, Assert.Single(raised));
    }

    [Fact]
    public async Task AFailedFetchIsNotRaised()
    {
        var coordinator = new AgentUsageFetchCoordinator(
            () => throw new InvalidOperationException("synthetic failure"));
        var raised = 0;
        coordinator.Fetched += (_, _) => Interlocked.Increment(ref raised);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FetchAsync());

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
        coordinator.Fetched += (_, _) => askedFromHandler ??= coordinator.FetchAsync();

        var first = await coordinator.FetchAsync();

        Assert.Equal("call-1", first.GeneratedAt);
        Assert.Equal("call-1", (await askedFromHandler!).GeneratedAt);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    /// <summary>A handler's RequestFollowUp while it is raised for is honoured
    /// by the same chain: it fetches once more, the awaited payload is the
    /// second, both are raised in order, and nothing stays in flight.</summary>
    [Fact]
    public async Task AHandlerRequestingAFollowUpIsHonouredByTheSameChain()
    {
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
            new AgentUsagePayload($"call-{Interlocked.Increment(ref calls)}", []));
        var raised = new List<string>();
        coordinator.Fetched += (p, _) =>
        {
            raised.Add(p.GeneratedAt);
            if (raised.Count == 1)
            {
                coordinator.RequestFollowUp();
            }
        };

        Assert.Equal("call-2", (await coordinator.FetchAsync()).GeneratedAt);
        Assert.Equal(["call-1", "call-2"], raised);
        Assert.Equal("call-3", (await coordinator.FetchAsync()).GeneratedAt);
    }

    // A handler that throws must not leave the finished fetch in flight,
    // where every later caller would get it back instead of a fresh fetch.
    [Fact]
    public async Task AThrowingHandlerDoesNotPinTheFinishedFetch()
    {
        var calls = 0;
        var coordinator = new AgentUsageFetchCoordinator(() =>
            new AgentUsagePayload($"call-{Interlocked.Increment(ref calls)}", []));
        coordinator.Fetched += (_, _) => throw new InvalidOperationException("handler");

        await coordinator.FetchAsync();
        var second = await coordinator.FetchAsync();

        Assert.Equal("call-2", second.GeneratedAt);
    }

    /// <summary>A follow-up that fails keeps the earlier payload, and
    /// <see cref="AgentUsageFetchCoordinator.Fetched"/> sees it exactly once,
    /// whether the follow-up was owed before the raise or requested by the
    /// handler during it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedFollowUpRaisesTheEarlierPayloadExactlyOnce(bool requestedByHandler)
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
                if (!requestedByHandler)
                {
                    release.Task.GetAwaiter().GetResult();
                }
            }
            else
            {
                throw new InvalidOperationException("follow-up failed");
            }

            return new AgentUsagePayload("call-1", []);
        });
        var raised = new List<string>();
        coordinator.Fetched += (p, _) =>
        {
            raised.Add(p.GeneratedAt);
            if (requestedByHandler)
            {
                coordinator.RequestFollowUp();
            }
        };

        var fetch = coordinator.FetchAsync();
        if (!requestedByHandler)
        {
            await started.Task;
            coordinator.RequestFollowUp();
            release.SetResult();
        }

        Assert.Equal("call-1", (await fetch).GeneratedAt);
        Assert.Equal(["call-1"], raised);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>A throwing handler is logged once by exception type and does
    /// not starve the handlers after it.</summary>
    [Fact]
    public async Task AThrowingHandlerIsLoggedAndTheNextHandlerStillRuns()
    {
        var coordinator = new AgentUsageFetchCoordinator(() => new AgentUsagePayload("now", []));
        var logged = new List<string>();
        coordinator.Log = logged.Add;
        AgentUsagePayload? received = null;
        coordinator.Fetched += (_, _) => throw new InvalidOperationException("handler");
        coordinator.Fetched += (p, _) => received = p;

        var result = await coordinator.FetchAsync();

        Assert.Same(result, received);
        Assert.Equal(["agentUsage fetched handler failed: InvalidOperationException"], logged);
    }
}
