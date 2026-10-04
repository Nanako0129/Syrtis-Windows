using System.Collections.Concurrent;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class GraphRequestCoordinatorTests
{
    private static UsagePayload Payload(
        string? year,
        PricingMode pricing = PricingMode.LocalOnly,
        CostCoverage coverage = CostCoverage.Complete) =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-12-31"),
                pricing,
                coverage),
            new UsageSummary(1, 2, 1, 1, 2, 2, [], []),
            year is null ? [] : [new YearMeta(year, 1, 2,
                new DateRange($"{year}-01-01", $"{year}-12-31"))],
            []);

    [Fact]
    public void GraphQueryNormalizesDashboardYearIdentity()
    {
        Assert.Equal("2026", GraphQuery.Normalize(" 2026 ").Year);
        Assert.Null(GraphQuery.Normalize(" ").Year);
        Assert.Null(GraphQuery.Normalize(null).Year);
    }

    [Fact]
    public async Task LocalPublishesBeforeBlockedRicherAndRetainedAttachAddsNoCalls()
    {
        var richerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = new ConcurrentQueue<GraphPublication>();
        var localCalls = 0;
        var richerCalls = 0;
        var local = Payload(null);
        var richer = Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref localCalls);
                return local;
            },
            _ =>
            {
                Interlocked.Increment(ref richerCalls);
                richerStarted.TrySetResult(true);
                richerGate.Task.GetAwaiter().GetResult();
                return richer;
            });
        coordinator.Published += publication =>
        {
            publications.Enqueue(publication);
            if (publication.Stage == GraphPublicationStage.LocalFirst)
            {
                localPublished.TrySetResult(true);
            }
        };
        coordinator.Completed += completion => completed.TrySetResult(completion);

        var requestId = coordinator.Request(null);
        await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphPublicationStage.LocalFirst,
            publications.First().Stage);
        await richerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);

        var retained = coordinator.Attach(null);
        Assert.Equal(requestId, retained.RequestId);
        Assert.True(retained.InFlight);
        Assert.Equal(GraphPublicationStage.LocalFirst, retained.Latest?.Stage);

        richerGate.TrySetResult(true);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Succeeded);
        Assert.Equal(2, publications.Count);

        var late = coordinator.Attach(null);
        Assert.Equal(requestId, late.RequestId);
        Assert.False(late.InFlight);
        Assert.Equal(GraphPublicationStage.Richer, late.Latest?.Stage);
        var consumer = new GraphConsumerState();
        consumer.Begin(late.RequestId);
        Assert.True(consumer.TryAcceptGraph(
            late.RequestId,
            late.Latest!.Payload,
            late.Latest.Stage));
        Assert.True(consumer.LocalAccepted);
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);
    }

    [Fact]
    public async Task AttachReplaysRicherPublishedBeforeBegin()
    {
        var localPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerPublishedRejected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                localPublished.TrySetResult(true);
                return Payload(null, PricingMode.LocalOnly, CostCoverage.Complete);
            },
            _ =>
            {
                richerStarted.TrySetResult(true);
                richerGate.Task.GetAwaiter().GetResult();
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            });
        coordinator.Published += publication =>
        {
            if (publication.Stage == GraphPublicationStage.LocalFirst)
            {
                localPublished.TrySetResult(true);
            }
        };

        var requestId = coordinator.Request(null);
        await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await richerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retained = coordinator.Attach(null);
        Assert.Equal(requestId, retained.RequestId);
        Assert.Equal(GraphPublicationStage.LocalFirst, retained.Latest?.Stage);

        var consumer = new GraphConsumerState();
        coordinator.Published += publication =>
        {
            if (publication.RequestId == requestId
                && publication.Stage == GraphPublicationStage.Richer
                && !consumer.TryAcceptGraph(
                    publication.RequestId, publication.Payload, publication.Stage))
            {
                richerPublishedRejected.TrySetResult(true);
            }
        };

        var beginEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBegin = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attachmentTask = Task.Run(() => coordinator.Attach(
            null,
            id =>
            {
                beginEntered.TrySetResult(true);
                allowBegin.Task.GetAwaiter().GetResult();
                Assert.True(consumer.Begin(id));
            }));

        await beginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(consumer.DesiredId);
        richerGate.TrySetResult(true);
        await richerPublishedRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(consumer.DesiredId);

        allowBegin.TrySetResult(true);
        var attachment = await attachmentTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requestId, attachment.RequestId);
        Assert.Equal(GraphPublicationStage.Richer, attachment.Latest?.Stage);
        Assert.True(consumer.TryAcceptGraph(
            attachment.RequestId,
            attachment.Latest!.Payload,
            attachment.Latest.Stage));
        Assert.True(consumer.CostAuthoritative);
    }

    [Fact]
    public async Task CoalescedRequestReplaysLatestAfterConsumerBegins()
    {
        var localPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var local = Payload(null);
        var coordinator = new GraphRequestCoordinator(
            _ => local,
            _ =>
            {
                richerGate.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("blocked richer");
            });
        coordinator.Published += publication =>
        {
            if (publication.Stage == GraphPublicationStage.LocalFirst)
            {
                localPublished.TrySetResult(true);
            }
        };
        coordinator.Completed += completion => completed.TrySetResult(completion);

        var requestId = coordinator.Request(null);
        await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var consumer = new GraphConsumerState();
        GraphPublication? replayed = null;
        var coalesced = coordinator.Request(
            null,
            onBegin: id => Assert.True(consumer.Begin(id)),
            onReplay: publication =>
            {
                replayed = publication;
                Assert.True(consumer.TryAcceptGraph(
                    publication.RequestId,
                    publication.Payload,
                    publication.Stage));
            });

        Assert.Equal(requestId, coalesced);
        Assert.Equal(GraphPublicationStage.LocalFirst, replayed?.Stage);
        Assert.Same(local, consumer.AcceptedGraph);

        richerGate.TrySetResult(true);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.LocalSucceeded);
        Assert.False(result.RicherSucceeded);
        Assert.Same(local, consumer.AcceptedGraph);
    }

    [Fact]
    public async Task AttachAndNonForceRequestCoalesceInFlight()
    {
        var localGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(true);
                localGate.Task.GetAwaiter().GetResult();
                return Payload("2026");
            },
            _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete));
        coordinator.Completed += _ => completion.TrySetResult(true);

        var first = coordinator.Attach(" 2026 ");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.Attach("2026");
        var third = coordinator.Request("2026");

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(second.RequestId, third);
        Assert.Equal(1, calls);

        localGate.TrySetResult(true);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ForceSupersedesOldRicherAndPublishesOnlyCurrentRicher()
    {
        var oldRicherGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRicherStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new ConcurrentQueue<GraphRequestCompletion>();
        var publications = new ConcurrentQueue<GraphPublication>();
        var localCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Payload(null);
            },
            _ =>
            {
                oldRicherStarted.TrySetResult(true);
                oldRicherGate.Task.GetAwaiter().GetResult();
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            },
            _ => Payload(null, PricingMode.BestEffort, CostCoverage.Complete));
        coordinator.Published += publications.Enqueue;
        coordinator.Completed += completions.Enqueue;

        var oldId = coordinator.Request(null);
        await oldRicherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var currentId = coordinator.Request(null, force: true);
        Assert.True(currentId.Generation > oldId.Generation);

        await WaitUntil(() => completions.Any(c => c.RequestId == currentId));
        oldRicherGate.TrySetResult(true);
        await Task.Delay(50);

        Assert.Equal(2, localCalls);
        Assert.Contains(publications, p => p.RequestId == currentId
            && p.Stage == GraphPublicationStage.Richer);
        Assert.DoesNotContain(publications, p => p.RequestId == oldId
            && p.Stage == GraphPublicationStage.Richer);
    }

    [Fact]
    public async Task StartedBroadcastKeepsAllTimeConsumersOnOneGeneration()
    {
        var secondLocalGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                if (Interlocked.Increment(ref localCalls) == 2)
                {
                    secondLocalGate.Task.GetAwaiter().GetResult();
                }

                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            },
            _ => Payload(null, PricingMode.BestEffort, CostCoverage.Complete),
            _ => Payload(null, PricingMode.BestEffort, CostCoverage.Complete));
        var tray = new GraphConsumerState();
        var dashboard = new GraphConsumerState();
        var starts = new ConcurrentQueue<GraphRequestId>();
        coordinator.Started += _ => throw new InvalidOperationException("observer");
        coordinator.Started += id =>
        {
            starts.Enqueue(id);
            if (id.Query.Year is null)
            {
                tray.Begin(id);
            }
        };
        coordinator.Started += id =>
        {
            if (id.Query.Year is null)
            {
                dashboard.Begin(id);
            }
        };
        coordinator.Published += publication =>
        {
            tray.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
            dashboard.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
        };
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Generation == 1)
            {
                firstCompletion.TrySetResult(completion);
            }
            else
            {
                secondCompletion.TrySetResult(completion);
            }
        };

        var first = coordinator.Request(null);
        await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tray.CostAuthoritative);
        Assert.True(dashboard.CostAuthoritative);

        var second = coordinator.Request(null, force: true,
            onBegin: id => dashboard.Begin(id));
        Assert.Equal(second, tray.DesiredId);
        Assert.Equal(second, dashboard.DesiredId);
        Assert.False(tray.CostAuthoritative);
        Assert.False(dashboard.CostAuthoritative);

        secondLocalGate.TrySetResult(true);
        await secondCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tray.CostAuthoritative);
        Assert.True(dashboard.CostAuthoritative);

        var retained = coordinator.Attach(null);
        Assert.Equal(second, retained.RequestId);
        Assert.Equal(2, starts.Count);
    }

    [Fact]
    public async Task YearStartedDoesNotMoveAllTimeTrayState()
    {
        var firstCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var yearCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            year => Payload(year, PricingMode.BestEffort, CostCoverage.Complete),
            year => Payload(year, PricingMode.BestEffort, CostCoverage.Complete));
        var tray = new GraphConsumerState();
        var dashboard = new GraphConsumerState();
        coordinator.Started += id =>
        {
            if (id.Query.Year is null)
            {
                tray.Begin(id);
            }
        };
        coordinator.Started += id =>
        {
            if (id.Query.Year == "2026")
            {
                dashboard.Begin(id);
            }
        };
        coordinator.Published += publication =>
        {
            tray.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
            dashboard.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
        };
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Query.Year is null)
            {
                firstCompletion.TrySetResult(completion);
            }
            else
            {
                yearCompletion.TrySetResult(completion);
            }
        };

        var allTime = coordinator.Request(null);
        await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tray.CostAuthoritative);

        var year = coordinator.Request("2026");
        Assert.Equal(allTime, tray.DesiredId);
        Assert.True(tray.CostAuthoritative);
        Assert.Equal(year, dashboard.DesiredId);

        await yearCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(allTime, tray.DesiredId);
        Assert.True(tray.CostAuthoritative);
        Assert.True(dashboard.CostAuthoritative);
    }

    [Fact]
    public async Task SupersededBlockedLocalSkipsOldRicherDelegate()
    {
        var oldLocalStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldLocalGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newerCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localCalls = 0;
        var normalRicherCalls = 0;
        var refreshRicherCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                if (Interlocked.Increment(ref localCalls) == 1)
                {
                    oldLocalStarted.TrySetResult(true);
                    oldLocalGate.Task.GetAwaiter().GetResult();
                }

                return Payload(null);
            },
            _ =>
            {
                Interlocked.Increment(ref normalRicherCalls);
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            },
            _ =>
            {
                Interlocked.Increment(ref refreshRicherCalls);
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            });
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Generation > 1)
            {
                newerCompletion.TrySetResult(completion);
            }
        };

        var old = coordinator.Request(null);
        await oldLocalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = coordinator.Request(null, force: true);
        var result = await newerCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(newer, result.RequestId);
        Assert.True(result.RicherSucceeded);
        Assert.Equal(1, refreshRicherCalls);

        oldLocalGate.TrySetResult(true);
        await Task.Delay(100);
        Assert.Equal(0, normalRicherCalls);
        Assert.True(newer.Generation > old.Generation);
    }

    [Fact]
    public async Task NormalDefaultRicherUsesInjectedRefreshWhenGraphDelegateIsMissing()
    {
        var completed = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = new ConcurrentQueue<GraphPublication>();
        var local = Payload(null);
        var refreshed = Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
        var refreshCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            localFirst: _ => local,
            graph: null,
            refreshGraph: _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                return refreshed;
            });
        coordinator.Published += publications.Enqueue;
        coordinator.Completed += completion => completed.TrySetResult(completion);

        var requestId = coordinator.Request(null);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(requestId, result.RequestId);
        Assert.True(result.RicherSucceeded);
        Assert.Equal(1, refreshCalls);
        Assert.Collection(
            publications,
            publication =>
            {
                Assert.Equal(GraphPublicationStage.LocalFirst, publication.Stage);
                Assert.Same(local, publication.Payload);
            },
            publication =>
            {
                Assert.Equal(GraphPublicationStage.Richer, publication.Stage);
                Assert.Same(refreshed, publication.Payload);
            });
    }

    [Fact]
    public async Task GraphStagesRunInsideInjectedBoostScopes()
    {
        var completed = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var opened = 0;
        var closed = 0;
        var localBoosted = false;
        var richerBoosted = false;
        IDisposable Boost()
        {
            Interlocked.Increment(ref opened);
            Interlocked.Increment(ref active);
            return new CallbackScope(() =>
            {
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref closed);
            });
        }

        var coordinator = new GraphRequestCoordinator(
            localFirst: _ =>
            {
                localBoosted = Volatile.Read(ref active) == 1;
                return Payload(null);
            },
            graph: _ =>
            {
                richerBoosted = Volatile.Read(ref active) == 1;
                return Payload(
                    null,
                    PricingMode.BestEffort,
                    CostCoverage.Complete);
            },
            boost: Boost);
        coordinator.Completed += completion => completed.TrySetResult(completion);

        var requestId = coordinator.Request(null);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(requestId, result.RequestId);
        Assert.True(result.Succeeded);
        Assert.True(localBoosted);
        Assert.True(richerBoosted);
        Assert.Equal(2, opened);
        Assert.Equal(2, closed);
        Assert.Equal(0, active);
    }

    [Fact]
    public void CompletionMatchesOnlyExactSuccessfulRicherRequest()
    {
        var allTime = new GraphRequestId(GraphQuery.Normalize(null), 7);
        var otherGeneration = new GraphRequestId(GraphQuery.Normalize(null), 6);
        var year = new GraphRequestId(GraphQuery.Normalize("2026"), 7);

        Assert.True(new GraphRequestCompletion(allTime, true, true)
            .IsSuccessfulRicherFor(allTime));
        Assert.False(new GraphRequestCompletion(allTime, true, false)
            .IsSuccessfulRicherFor(allTime));
        Assert.False(new GraphRequestCompletion(allTime, true, true)
            .IsSuccessfulRicherFor(otherGeneration));
        Assert.False(new GraphRequestCompletion(year, true, true)
            .IsSuccessfulRicherFor(allTime));
    }

    [Fact]
    public async Task StageFailuresPublishNoSuccessForThatStage()
    {
        var localCompleted = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localPublications = new ConcurrentQueue<GraphPublication>();
        var richerCalls = 0;
        var localFailing = new GraphRequestCoordinator(
            _ => throw new InvalidOperationException("blocked local"),
            _ =>
            {
                Interlocked.Increment(ref richerCalls);
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            });
        localFailing.Published += localPublications.Enqueue;
        localFailing.Completed += completion =>
            localCompleted.TrySetResult(completion);
        var localId = localFailing.Request(null);
        var localResult = await localCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(localId, localResult.RequestId);
        Assert.False(localResult.Succeeded);
        Assert.Empty(localPublications);
        Assert.Equal(0, richerCalls);

        var richerCompleted = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerPublications = new ConcurrentQueue<GraphPublication>();
        var richerFailing = new GraphRequestCoordinator(
            _ => Payload(null),
            _ => throw new InvalidOperationException("blocked richer"));
        richerFailing.Published += richerPublications.Enqueue;
        richerFailing.Completed += completion =>
            richerCompleted.TrySetResult(completion);
        var richerId = richerFailing.Request(null);
        var richerResult = await richerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(richerId, richerResult.RequestId);
        Assert.False(richerResult.Succeeded);
        Assert.Single(richerPublications);
        Assert.Equal(GraphPublicationStage.LocalFirst,
            richerPublications.Single().Stage);
    }

    [Fact]
    public async Task FreshSnapshotPublishesOnceWithoutSuppressingLiveStages()
    {
        var now = DateTimeOffset.UtcNow;
        var localStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLocal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = Payload("2026", PricingMode.BestEffort);
        var local = Payload("2026");
        var richer = Payload("2026", PricingMode.BestEffort, CostCoverage.Complete);
        var publications = new ConcurrentQueue<GraphPublication>();
        var access = new SnapshotAccess(
            _ => new GraphSnapshotReadResult(
                GraphSnapshotReadStatus.Hit, snapshot, now.AddMinutes(-5)),
            (_, _, _, _) => GraphSnapshotWriteStatus.Skipped);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                localStarted.TrySetResult(true);
                releaseLocal.Task.GetAwaiter().GetResult();
                return local;
            },
            _ => richer,
            snapshot: access,
            utcNow: () => now);
        coordinator.Published += publication =>
        {
            publications.Enqueue(publication);
            if (ReferenceEquals(publication.Payload, snapshot))
            {
                snapshotPublished.TrySetResult(true);
            }
        };
        coordinator.Completed += value => completion.TrySetResult(value);

        var requestId = coordinator.Attach(" 2026 ").RequestId;
        await localStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await snapshotPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseLocal.TrySetResult(true);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.Equal(3, publications.Count);
        Assert.Equal(requestId, publications.First().RequestId);
        Assert.Same(snapshot, publications.First().Payload);
        Assert.Equal(now.AddMinutes(-5), publications.First().RestoredFrom);
        Assert.Contains(publications, value => ReferenceEquals(value.Payload, local));
        Assert.Contains(publications, value => ReferenceEquals(value.Payload, richer));
        Assert.All(publications.Skip(1), value => Assert.Null(value.RestoredFrom));
    }

    /// <summary>A cold start the next day shows the last run's graph, marked
    /// with its capture time, instead of nothing (macOS keeps 90 days; the old
    /// 30-minute limit dropped it). The live stages still follow, unmarked.
    /// A capture time up to an hour ahead of the clock is restored too, as
    /// macOS's futureSkewAllowance allows (-25 h and +30 min below).</summary>
    [Theory]
    [InlineData(-25 * 60)]
    [InlineData(30)]
    public async Task SnapshotIsRestoredAndMarkedWithItsCaptureTime(int capturedMinutesFromNow)
    {
        var now = DateTimeOffset.UtcNow;
        var capturedAt = now.AddMinutes(capturedMinutesFromNow);
        var completion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // The live local stage is held until the snapshot has published: a
        // snapshot that loses the race to a live stage is dropped by design,
        // and that is not what this test is about.
        var releaseLocal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = Payload("2026", PricingMode.BestEffort);
        var publications = new ConcurrentQueue<GraphPublication>();
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                releaseLocal.Task.GetAwaiter().GetResult();
                return Payload("2026");
            },
            _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete),
            snapshot: new SnapshotAccess(
                _ => new GraphSnapshotReadResult(GraphSnapshotReadStatus.Hit, snapshot, capturedAt),
                (_, _, _, _) => GraphSnapshotWriteStatus.Skipped),
            utcNow: () => now);
        coordinator.Published += publication =>
        {
            publications.Enqueue(publication);
            if (ReferenceEquals(publication.Payload, snapshot))
            {
                snapshotPublished.TrySetResult(true);
            }
        };
        coordinator.Completed += value => completion.TrySetResult(value);

        coordinator.Attach("2026");
        await snapshotPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseLocal.TrySetResult(true);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var restored = Assert.Single(publications, value => ReferenceEquals(value.Payload, snapshot));
        Assert.Equal(capturedAt, restored.RestoredFrom);
        Assert.Equal(3, publications.Count);
        Assert.Equal(2, publications.Count(value => value.RestoredFrom is null));
    }

    /// <summary>The header control and Ctrl+R share one busy condition, and
    /// a background request (poll, year switch) counts, not only a manual
    /// refresh (macOS refreshDisabled).</summary>
    [Fact]
    public void RefreshIsBusyForAnyGraphRequest()
    {
        Assert.False(RefreshTip.Busy(loading: false, manualRefresh: false, graphInFlight: false));
        Assert.True(RefreshTip.Busy(loading: false, manualRefresh: false, graphInFlight: true));
        Assert.True(RefreshTip.Busy(loading: false, manualRefresh: true, graphInFlight: false));
        Assert.True(RefreshTip.Busy(loading: true, manualRefresh: false, graphInFlight: false));
    }

    /// <summary>Only restored data whose live refresh failed is tinted; restored
    /// data with the refresh still to come, and live data, are not.</summary>
    [Fact]
    public void OnlyAFailedRestoreIsTinted()
    {
        var at = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        Assert.True(RefreshTip.ShowsStaleRestore(at, restoreFailed: true));
        Assert.False(RefreshTip.ShowsStaleRestore(at, restoreFailed: false));
        Assert.False(RefreshTip.ShowsStaleRestore(null, restoreFailed: true));
    }

    [Fact]
    public void RefreshTipSaysHowOldRestoredDataIs()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Refresh usage data", RefreshTip.Text(null, now));
        Assert.Equal(
            "Refresh usage data — showing data from 1d ago",
            RefreshTip.Text(now.AddHours(-25), now));
    }

    [Fact]
    public async Task LivePublicationWinsAndLateSnapshotIsNotEmitted()
    {
        var snapshotStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var livePublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = Payload("2026", PricingMode.BestEffort);
        var local = Payload("2026");
        var richer = Payload("2026", PricingMode.BestEffort, CostCoverage.Complete);
        var publications = new ConcurrentQueue<GraphPublication>();
        var access = new SnapshotAccess(
            _ =>
            {
                snapshotStarted.TrySetResult(true);
                releaseSnapshot.Task.GetAwaiter().GetResult();
                return new GraphSnapshotReadResult(
                    GraphSnapshotReadStatus.Hit, snapshot, DateTimeOffset.UtcNow);
            },
            (_, _, _, _) => GraphSnapshotWriteStatus.Skipped);
        var coordinator = new GraphRequestCoordinator(
            _ => local,
            _ => richer,
            snapshot: access);
        coordinator.Published += publication =>
        {
            publications.Enqueue(publication);
            if (ReferenceEquals(publication.Payload, local))
            {
                livePublished.TrySetResult(true);
            }
        };
        coordinator.Completed += value => completion.TrySetResult(value);

        coordinator.Attach("2026");
        await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await livePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseSnapshot.TrySetResult(true);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.DoesNotContain(publications, value => ReferenceEquals(value.Payload, snapshot));
        Assert.Equal(2, publications.Count);
        Assert.Same(richer, publications.Last().Payload);
    }

    [Fact]
    public async Task NonHitAndSnapshotFailuresLeaveLivePipelineUnchanged()
    {
        var statuses = new[]
        {
            GraphSnapshotReadStatus.Missing,
            GraphSnapshotReadStatus.InvalidData,
            GraphSnapshotReadStatus.ContextMismatch,
            GraphSnapshotReadStatus.QueryMismatch,
            GraphSnapshotReadStatus.IoFailure,
        };

        foreach (var status in statuses)
        {
            var completion = new TaskCompletionSource<GraphRequestCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var publications = new ConcurrentQueue<GraphPublication>();
            var coordinator = new GraphRequestCoordinator(
                _ => Payload("2026"),
                _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete),
                snapshot: new SnapshotAccess(
                    _ => new GraphSnapshotReadResult(status),
                    (_, _, _, _) => GraphSnapshotWriteStatus.Skipped));
            coordinator.Published += publications.Enqueue;
            coordinator.Completed += value => completion.TrySetResult(value);

            var requestId = coordinator.Attach("2026").RequestId;
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(requestId, result.RequestId);
            Assert.True(result.Succeeded);
            Assert.Equal(2, publications.Count);
            Assert.Equal(GraphPublicationStage.LocalFirst, publications.First().Stage);
            Assert.Equal(GraphPublicationStage.Richer, publications.Last().Stage);
        }

        var thrownCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thrownPublications = new ConcurrentQueue<GraphPublication>();
        var thrown = new GraphRequestCoordinator(
            _ => Payload("2026"),
            _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete),
            snapshot: new SnapshotAccess(
                _ => throw new InvalidOperationException(),
                (_, _, _, _) => GraphSnapshotWriteStatus.Skipped));
        thrown.Published += thrownPublications.Enqueue;
        thrown.Completed += value => thrownCompletion.TrySetResult(value);
        var thrownId = thrown.Attach("2026").RequestId;
        Assert.Equal(thrownId,
            (await thrownCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5))).RequestId);
        Assert.Equal(2, thrownPublications.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var capturedAt in new[]
        {
            now - GraphRequestCoordinator.SnapshotMaxAge - TimeSpan.FromMinutes(1),
            // Past macOS's one-hour future skew allowance.
            now.AddHours(2),
        })
        {
            // The live local stage waits until the snapshot has been read and
            // given time to publish: if live published first, the race rule
            // would drop the snapshot and this case would pass with no age
            // check at all.
            var snapshotRead = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<GraphRequestCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stale = Payload("2026", PricingMode.BestEffort);
            var publications = new ConcurrentQueue<GraphPublication>();
            var coordinator = new GraphRequestCoordinator(
                _ =>
                {
                    snapshotRead.Task.GetAwaiter().GetResult();
                    Thread.Sleep(300);
                    return Payload("2026");
                },
                _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete),
                snapshot: new SnapshotAccess(
                    _ =>
                    {
                        snapshotRead.TrySetResult(true);
                        return new GraphSnapshotReadResult(
                            GraphSnapshotReadStatus.Hit, stale, capturedAt);
                    },
                    (_, _, _, _) => GraphSnapshotWriteStatus.Skipped),
                utcNow: () => now);
            coordinator.Published += publications.Enqueue;
            coordinator.Completed += value => completion.TrySetResult(value);

            coordinator.Attach("2026");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(publications, value => ReferenceEquals(value.Payload, stale));
            Assert.Equal(2, publications.Count);
        }
    }

    [Fact]
    public async Task BlockedSnapshotReadDoesNotDelayAttachLiveOrSupersession()
    {
        var readStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var livePublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                livePublished.TrySetResult(true);
                return Payload("2026");
            },
            _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete),
            snapshot: new SnapshotAccess(
                _ =>
                {
                    readStarted.TrySetResult(true);
                    releaseRead.Task.GetAwaiter().GetResult();
                    return new GraphSnapshotReadResult(GraphSnapshotReadStatus.Missing);
                },
                (_, _, _, _) => GraphSnapshotWriteStatus.Skipped));

        try
        {
            var attach = await Task.Run(() => coordinator.Attach("2026"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await livePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var newer = coordinator.Request("2026", force: true);
            Assert.True(newer.Generation > attach.RequestId.Generation);
        }
        finally
        {
            releaseRead.TrySetResult(true);
        }
    }

    [Fact]
    public async Task OlderQueryCannotCommitAfterNewerQueryStarts()
    {
        var firstWriteStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWriteFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCommitted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = new ConcurrentQueue<string>();
        var access = new SnapshotAccess(
            _ => new GraphSnapshotReadResult(GraphSnapshotReadStatus.Missing),
            (year, _, _, fence) =>
            {
                if (year is null)
                {
                    firstWriteStarted.TrySetResult(true);
                    releaseFirstWrite.Task.GetAwaiter().GetResult();
                }

                var committed = false;
                fence!(() =>
                {
                    committed = true;
                    commits.Enqueue(year ?? "all");
                    if (year == "2026")
                    {
                        secondCommitted.TrySetResult(true);
                    }
                });
                if (year is null)
                {
                    firstWriteFinished.TrySetResult(true);
                }
                return committed
                    ? GraphSnapshotWriteStatus.Written
                    : GraphSnapshotWriteStatus.Skipped;
            });
        var coordinator = new GraphRequestCoordinator(
            year => Payload(year),
            year => Payload(year, PricingMode.BestEffort, CostCoverage.Complete),
            snapshot: access);

        try
        {
            coordinator.Request(null);
            await firstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            coordinator.Request("2026");
            await secondCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseFirstWrite.TrySetResult(true);
            await firstWriteFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["2026"], commits);
        }
        finally
        {
            releaseFirstWrite.TrySetResult(true);
        }
    }

    private sealed class CallbackScope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private static async Task WaitForCompletion(
        GraphRequestCoordinator coordinator,
        GraphRequestId requestId)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(GraphRequestCompletion value)
        {
            if (value.RequestId == requestId)
            {
                completion.TrySetResult(true);
            }
        }

        coordinator.Completed += OnCompleted;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            coordinator.Completed -= OnCompleted;
        }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("condition was not reached");
            }

            await Task.Delay(10);
        }
    }
}
