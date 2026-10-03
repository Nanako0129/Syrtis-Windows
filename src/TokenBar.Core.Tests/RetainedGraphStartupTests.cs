using System.Collections.Concurrent;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class RetainedGraphStartupTests
{
    private static UsagePayload Payload() =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-12-31"),
                PricingMode.BestEffort,
                CostCoverage.Complete),
            new UsageSummary(1, 2, 1, 1, 2, 2, [], []),
            [],
            []);

    [Fact]
    public async Task TrayFirstDashboardAttachReusesRichPublicationAndModelGate()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localCalls = 0;
        var richerCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Payload();
            },
            _ =>
            {
                Interlocked.Increment(ref richerCalls);
                return Payload();
            });
        coordinator.Completed += _ => completion.TrySetResult(true);

        var tray = new GraphConsumerState();
        var trayAttachment = coordinator.Attach(null, id => { tray.Begin(id); });
        Assert.True(trayAttachment.InFlight);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);

        var dashboard = new GraphConsumerState();
        var dashboardAttachment = coordinator.Attach(
            null, id => { dashboard.Begin(id); });
        Assert.Equal(trayAttachment.RequestId, dashboardAttachment.RequestId);
        Assert.False(dashboardAttachment.InFlight);
        Assert.NotNull(dashboardAttachment.Latest);
        Assert.True(dashboard.TryAcceptGraph(
            dashboardAttachment.RequestId,
            dashboardAttachment.Latest!.Payload,
            dashboardAttachment.Latest.Stage));
        Assert.True(dashboard.TryBeginModel(dashboardAttachment.RequestId));
        Assert.False(dashboard.TryBeginModel(dashboardAttachment.RequestId));
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);

        var newer = coordinator.Request(
            null,
            force: true,
            onBegin: id => { dashboard.Begin(id); });
        Assert.False(dashboard.TryAcceptModel(
            dashboardAttachment.RequestId,
            new ModelReport([], 0, 0, 0, 0, 0, 0)));
        Assert.True(newer.Generation > dashboardAttachment.RequestId.Generation);
    }

    [Fact]
    public async Task AttachResumeRefreshesCompletedYearsOnly()
    {
        var completions = new ConcurrentQueue<GraphRequestCompletion>();
        var localCalls = new ConcurrentDictionary<string, int>();
        var yearStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseYear = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            year =>
            {
                var key = year ?? "all";
                localCalls.AddOrUpdate(key, 1, (_, count) => count + 1);
                if (year == "2025")
                {
                    yearStarted.TrySetResult(true);
                    releaseYear.Task.GetAwaiter().GetResult();
                }

                return Payload();
            },
            year =>
            {
                var key = $"richer:{year ?? "all"}";
                localCalls.AddOrUpdate(key, 1, (_, count) => count + 1);
                return Payload();
            });
        coordinator.Completed += completions.Enqueue;

        var completedYear = coordinator.Request(" 2026 ");
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == completedYear));
        var completedAttachment = coordinator.Attach(" 2026 ");
        Assert.Equal("2026", completedAttachment.RequestId.Query.Year);
        Assert.False(completedAttachment.InFlight);
        Assert.NotNull(completedAttachment.Latest);
        Assert.True(GraphResumePolicy.ShouldRefreshAfterAttach(completedAttachment));

        var resumedYear = coordinator.Request(
            completedAttachment.RequestId.Query.Year,
            force: false);
        Assert.True(resumedYear.Generation > completedYear.Generation);
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == resumedYear));
        Assert.Equal(2, localCalls["2026"]);

        var inFlightYear = coordinator.Request("2025");
        await yearStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inFlightAttachment = coordinator.Attach(" 2025 ");
        Assert.Equal(inFlightYear, inFlightAttachment.RequestId);
        Assert.True(inFlightAttachment.InFlight);
        Assert.False(GraphResumePolicy.ShouldRefreshAfterAttach(inFlightAttachment));
        if (GraphResumePolicy.ShouldRefreshAfterAttach(inFlightAttachment))
        {
            coordinator.Request(inFlightAttachment.RequestId.Query.Year);
        }

        releaseYear.TrySetResult(true);
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == inFlightYear));
        Assert.Equal(1, localCalls["2025"]);

        var allTime = coordinator.Request(null);
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == allTime));
        var allTimeCalls = localCalls["all"];
        var allTimeAttachment = coordinator.Attach(null);
        Assert.Equal(allTime, allTimeAttachment.RequestId);
        Assert.False(allTimeAttachment.InFlight);
        Assert.False(GraphResumePolicy.ShouldRefreshAfterAttach(allTimeAttachment));
        Assert.Equal(allTimeCalls, localCalls["all"]);
    }

    [Fact]
    public async Task SupersededRicherCannotCommitAndNextCoordinatorRestoresIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "tokenbar-tests", "coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "graph.json");
        var store = new GraphSnapshotStore(path);
        var first = SnapshotPayload("2026-01-01");
        var second = SnapshotPayload("2026-02-01");
        var writeCount = 0;
        var firstFenceStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new SnapshotAccess(
            year => store.Read("ctx", year),
            (year, capturedAt, payload, commitFence) => store.Write(
                "ctx",
                year,
                capturedAt,
                payload,
                commit =>
                {
                    if (Interlocked.Increment(ref writeCount) == 1)
                    {
                        firstFenceStarted.TrySetResult(true);
                        releaseFirstFence.Task.GetAwaiter().GetResult();
                    }

                    commitFence!(commit);
                }));
        var localCalls = 0;
        var firstCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            _ => Interlocked.Increment(ref localCalls) == 1 ? first : second,
            _ => Interlocked.CompareExchange(ref localCalls, 0, 0) == 1 ? first : second,
            _ => second,
            snapshot: access,
            utcNow: () => DateTimeOffset.UtcNow);
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Generation == 1)
            {
                firstCompletion.TrySetResult(true);
            }
            else
            {
                secondCompletion.TrySetResult(true);
            }
        };

        coordinator.Request("2026");
        await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstFenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Request("2026", force: true);
        await secondCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => Volatile.Read(ref writeCount) >= 2);
        releaseFirstFence.TrySetResult(true);
        await Task.Delay(100);

        var stored = store.Read("ctx", "2026");
        Assert.Equal(GraphSnapshotReadStatus.Hit, stored.Status);
        Assert.Equal("2026-02-01", stored.Payload!.Contributions[0].Date);
        Assert.Empty(Directory.EnumerateFiles(root, ".*.tmp"));

        var restored = new TaskCompletionSource<GraphPublication>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restoredCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLocal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new GraphRequestCoordinator(
            _ =>
            {
                releaseLocal.Task.GetAwaiter().GetResult();
                return first;
            },
            _ => second,
            snapshot: new SnapshotAccess(
                year => store.Read("ctx", year),
                (_, _, _, _) => GraphSnapshotWriteStatus.Skipped),
            utcNow: () => DateTimeOffset.UtcNow);
        next.Published += publication =>
        {
            if (publication.Payload.Contributions[0].Date == "2026-02-01")
            {
                restored.TrySetResult(publication);
            }
        };
        next.Completed += _ => restoredCompletion.TrySetResult(true);

        var restoredId = next.Attach("2026").RequestId;
        var restoredPublication = await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(restoredId, restoredPublication.RequestId);
        Assert.Equal(GraphPublicationStage.LocalFirst, restoredPublication.Stage);
        releaseLocal.TrySetResult(true);
        await restoredCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void CreateForAppUsesLocalApplicationDataAndFallsBackLiveOnly()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "tokenbar-tests", "profile-" + Guid.NewGuid().ToString("N"));
        var requested = (Environment.SpecialFolder?)null;
        var created = new List<string>();
        var idCalls = 0;
        try
        {
            var coordinator = GraphRequestCoordinator.CreateForApp(
                getFolderPath: folder =>
                {
                    requested = folder;
                    return root;
                },
                sourceContextId: () =>
                {
                    idCalls++;
                    return "ctx";
                },
                createDirectory: path =>
                {
                    created.Add(path);
                    Directory.CreateDirectory(path);
                },
                localFirst: _ => Payload(),
                graph: _ => Payload());

            Assert.Equal(
                (Environment.SpecialFolder?)GraphRequestCoordinator.SnapshotProfileRoot,
                requested);
            Assert.Equal(Path.Combine(root, "TokenBar"), Assert.Single(created));
            // Read per access, not at construction (security review R4).
            Assert.Equal(0, idCalls);
            Assert.NotNull(coordinator.Snapshot);
            Assert.Equal(
                GraphSnapshotWriteStatus.Written,
                coordinator.Snapshot!.Write(
                    "2026",
                    DateTimeOffset.UtcNow,
                    SnapshotPayload("2026-01-01"),
                    null));
            Assert.Equal(1, idCalls);
            Assert.True(File.Exists(Path.Combine(
                root, "TokenBar", "graph-snapshot.json")));

            var blank = GraphRequestCoordinator.CreateForApp(
                getFolderPath: _ => " ",
                sourceContextId: () => throw new InvalidOperationException(),
                localFirst: _ => Payload(),
                graph: _ => Payload());
            Assert.Null(blank.Snapshot);

            var relative = GraphRequestCoordinator.CreateForApp(
                getFolderPath: _ => "relative",
                sourceContextId: () => throw new InvalidOperationException(),
                localFirst: _ => Payload(),
                graph: _ => Payload());
            Assert.Null(relative.Snapshot);

            var mkdirCalls = 0;
            var mkdirFailure = GraphRequestCoordinator.CreateForApp(
                getFolderPath: _ => root,
                createDirectory: _ =>
                {
                    mkdirCalls++;
                    throw new IOException();
                },
                sourceContextId: () => throw new InvalidOperationException(),
                localFirst: _ => Payload(),
                graph: _ => Payload());
            Assert.Null(mkdirFailure.Snapshot);
            Assert.Equal(1, mkdirCalls);

            // An id that cannot be read misses and skips that access only.
            var idFailure = GraphRequestCoordinator.CreateForApp(
                getFolderPath: _ => root,
                createDirectory: _ => { },
                sourceContextId: () => throw new InvalidOperationException(),
                localFirst: _ => Payload(),
                graph: _ => Payload());
            Assert.Equal(GraphSnapshotReadStatus.Missing, idFailure.Snapshot!.Read("2026").Status);
            Assert.Equal(
                GraphSnapshotWriteStatus.Skipped,
                idFailure.Snapshot.Write("2026", DateTimeOffset.UtcNow, SnapshotPayload("2026-01-01"), null));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static UsagePayload SnapshotPayload(string date) => new(
        new UsageMeta(
            "generated",
            "test",
            new DateRange(date, date),
            PricingMode.BestEffort,
            CostCoverage.Complete),
        new UsageSummary(1, 1, 1, 1, 1, 1, ["claude"], ["model"]),
        [new YearMeta("2026", 1, 1, new DateRange(date, date))],
        [new Contribution(
            date,
            new ContributionTotals(1, 1, 1),
            1,
            new TokenBreakdown(1, 0, 0, 0, 0),
            [new ContributionClient(
                "claude",
                "model",
                "provider",
                new TokenBreakdown(1, 0, 0, 0, 0),
                1,
                1)],
            new Dictionary<string, long> { ["claude"] = 1 })]);

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
