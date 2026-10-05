using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// The C# half of the extra Claude accounts (W4): the saved list, the UI path
/// rules, the serialized pusher (security review R7), type-only logging (R6),
/// the launch gate, and which account's local usage a card may show.
/// </summary>
public class ClaudeExtraRootsTests
{
    private const string Sentinel = @"C:\SENTINEL-7f3a\.claude-work";

    private static SettingsStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"), "settings.json"));

    private static RootsResult Ok(int count, params RootRejection[] rejected) => new(count, rejected);

    // ---- persistence and UI rules -----------------------------------------

    [Fact]
    public void SavedListRoundTripsAsAJsonArrayString()
    {
        var store = TempStore();
        ClaudeExtraRoots.Save(store, [@"D:\work\.claude", @"E:\b"]);

        Assert.Equal("[\"D:\\\\work\\\\.claude\",\"E:\\\\b\"]", store.GetString(ClaudeExtraRoots.Key));
        Assert.Equal([@"D:\work\.claude", @"E:\b"], ClaudeExtraRoots.Load(store));
    }

    [Fact]
    public void MalformedOrMissingListLoadsEmpty()
    {
        var store = TempStore();
        Assert.Empty(ClaudeExtraRoots.Load(store));
        store.SetString(ClaudeExtraRoots.Key, "{not json");
        Assert.Empty(ClaudeExtraRoots.Load(store));
    }

    [Fact]
    public void EachDirectoryScansProjectsAndTranscripts()
    {
        Assert.Equal(
            // The registry takes either separator; the directory's own spelling is kept.
            [@"D:\a\projects", @"D:\a\transcripts", @"E:/b\projects", @"E:/b\transcripts"],
            ClaudeExtraRoots.ScanRoots([@"D:\a\", "E:/b/"]));
    }

    // ---- the pusher (R7) ---------------------------------------------------

    /// <summary>Two full-replace setters racing: the first push blocks inside
    /// its config-dir call while the list is changed and pushed again. The
    /// registries must end on the LAST saved list and never run two pushes at
    /// once. A pusher that captured the list at request time, or ran each
    /// request on its own task, ends on the first list.</summary>
    [Fact]
    public void LastSavedListWinsAndPushesNeverOverlap()
    {
        var saved = new List<string> { @"D:\first" };
        IReadOnlyList<string>? configRegistry = null;
        IReadOnlyList<string>? scanRegistry = null;
        var inside = 0;
        var maxInside = 0;
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = 0;
        var pusher = new ClaudeRootsPusher(
            () => [.. saved],
            dirs =>
            {
                var now = Interlocked.Increment(ref inside);
                maxInside = Math.Max(maxInside, now);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(10));
                }

                configRegistry = dirs;
                Interlocked.Decrement(ref inside);
                return Ok(dirs.Count);
            },
            roots =>
            {
                scanRegistry = roots;
                return Ok(roots.Count);
            },
            _ => { });

        var first = pusher.Request();
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
        saved = [@"D:\second"];
        var second = pusher.Request();
        var third = pusher.Request();
        releaseFirst.Set();
        Assert.True(Task.WaitAll([first, second, third], TimeSpan.FromSeconds(10)));

        Assert.Equal([@"D:\second"], configRegistry);
        Assert.Equal([@"D:\second\projects", @"D:\second\transcripts"], scanRegistry);
        Assert.Equal(1, maxInside);
        // The two requests made during the first push share one push.
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ScanRootsCoverOnlyDirectoriesTheConfigRegistryAcceptedAndMapBack()
    {
        IReadOnlyList<string>? scanned = null;
        var configCalls = new List<IReadOnlyList<string>>();
        var pusher = new ClaudeRootsPusher(
            () => [@"D:\a", @"\\server\share", @"E:\b"],
            dirs =>
            {
                configCalls.Add(dirs);
                return dirs.Count == 3 ? Ok(2, new RootRejection(1, "unsupportedPath")) : Ok(dirs.Count);
            },
            roots =>
            {
                scanned = roots;
                // Index 3 = E:\b\transcripts, the second accepted directory.
                return roots.Count == 4 ? Ok(3, new RootRejection(3, "notDirectory")) : Ok(roots.Count);
            },
            _ => { });

        Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal(
            new Dictionary<int, string> { [1] = "unsupportedPath", [2] = "notDirectory" },
            pusher.Last!.Rejected);
        // E:\b's transcripts can't be scanned, so it gets no card and its
        // projects root is not scanned either: both registries end on the
        // directories both took.
        Assert.Equal([@"D:\a"], configCalls[^1]);
        Assert.Equal([@"D:\a\projects", @"D:\a\transcripts"], scanned);
    }

    /// <summary>A scan setter that fails leaves its registry as it was; the
    /// config registry goes back to the last list both took.</summary>
    [Fact]
    public void AFailedScanSetterPutsTheConfigRegistryBack()
    {
        IReadOnlyList<string> saved = [@"D:\a"];
        IReadOnlyList<string> config = [];
        var failScan = false;
        var pusher = new ClaudeRootsPusher(
            () => saved,
            dirs =>
            {
                config = dirs;
                return Ok(dirs.Count);
            },
            roots => failScan ? throw new InvalidOperationException() : Ok(roots.Count),
            _ => { });
        Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(10)));

        saved = [@"D:\a", @"D:\b"];
        failScan = true;
        Assert.Throws<AggregateException>(() => pusher.Request().Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal([@"D:\a"], config);
    }

    // ---- R6: logs carry exception types, never paths -----------------------

    [Fact]
    public void AFailedPushLogsTheExceptionTypeOnly()
    {
        var log = new List<string>();
        var pusher = new ClaudeRootsPusher(
            () => [Sentinel],
            _ => throw new IOException($"could not read {Sentinel}"),
            _ => Ok(0),
            log.Add);

        var push = pusher.Request();
        Assert.Throws<AggregateException>(() => push.Wait(TimeSpan.FromSeconds(10)));

        var line = Assert.Single(log);
        Assert.Contains(nameof(IOException), line);
        Assert.DoesNotContain("SENTINEL", line, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the launch gate ---------------------------------------------------

    /// <summary>A failed launch push is logged once, by type, and every
    /// reader after it continues with the current context without logging
    /// again.</summary>
    [Fact]
    public void LaunchGateFallsBackOnAFailedPushAndLogsTheTypeOnce()
    {
        var log = new List<string>();
        var launch = ClaudeExtraRoots.Observe(
            Task.FromException(new UnauthorizedAccessException($"denied: {Sentinel}")), log.Add);

        for (var reader = 0; reader < 3; reader++)
        {
            ClaudeExtraRoots.AwaitLaunch(launch, TimeSpan.FromSeconds(5), log.Add);
        }

        var line = Assert.Single(log);
        Assert.Contains(nameof(UnauthorizedAccessException), line);
        Assert.DoesNotContain("SENTINEL", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only a launch push that ran is marked as one: after a failed
    /// launch push, the user's next save is a normal push the dashboard must
    /// refresh for.</summary>
    [Fact]
    public void OnlyTheLaunchRequestIsMarkedLaunch()
    {
        var fail = true;
        var pushes = new List<ClaudeRootsPush>();
        var pusher = new ClaudeRootsPusher(
            () => [],
            dirs => fail ? throw new InvalidOperationException() : Ok(0),
            roots => Ok(0),
            _ => { });
        pusher.Pushed += pushes.Add;

        Assert.Throws<AggregateException>(() => pusher.Request(launch: true).Wait(TimeSpan.FromSeconds(10)));
        fail = false;
        Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(10)));

        Assert.False(Assert.Single(pushes).Launch);
        Assert.True(pusher.Request(launch: true).Wait(TimeSpan.FromSeconds(10)));
        Assert.True(pushes[^1].Launch);
    }

    [Fact]
    public void LaunchGateReturnsAfterTheTimeoutWhenThePushHangs()
    {
        var log = new List<string>();
        var hung = new TaskCompletionSource().Task;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        ClaudeExtraRoots.AwaitLaunch(hung, TimeSpan.FromMilliseconds(50), log.Add);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains("timed out", Assert.Single(log));
    }

    [Fact]
    public void LaunchGateIsSilentOnceThePushLanded()
    {
        var log = new List<string>();
        ClaudeExtraRoots.AwaitLaunch(Task.CompletedTask, TimeSpan.FromSeconds(1), log.Add);
        Assert.Empty(log);
    }

    // ---- R4: the snapshot follows the current source id --------------------

    /// <summary>A snapshot written while one set of roots was registered must
    /// not be served once the roots (and so the source id) change. The id used
    /// to be read once at construction, so the second read was a hit.</summary>
    [Fact]
    public void SnapshotReadsTheSourceIdOnEveryAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "tokenbar-tests", "profile-" + Guid.NewGuid().ToString("N"));
        var id = "sc1:before";
        try
        {
            var coordinator = GraphRequestCoordinator.CreateForApp(
                getFolderPath: _ => root,
                sourceContextId: () => id,
                localFirst: _ => throw new InvalidOperationException(),
                graph: _ => throw new InvalidOperationException());
            var snapshot = coordinator.Snapshot!;
            Assert.Equal(
                GraphSnapshotWriteStatus.Written,
                snapshot.Write("2026", DateTimeOffset.UtcNow, RetainedGraphStartupTests.SnapshotPayload("2026-01-01"), null));
            Assert.Equal(GraphSnapshotReadStatus.Hit, snapshot.Read("2026").Status);

            id = "sc1:after";
            Assert.Equal(GraphSnapshotReadStatus.ContextMismatch, snapshot.Read("2026").Status);

            id = "sc1:before";
            Assert.Equal(GraphSnapshotReadStatus.Hit, snapshot.Read("2026").Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A richer scan that started under one set of roots and finished
    /// after they changed is not saved under the new id; one that ran wholly
    /// under the current roots is.</summary>
    [Fact]
    public void AScanThatStraddledARootChangeIsNotSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "tokenbar-tests", "profile-" + Guid.NewGuid().ToString("N"));
        var id = "sc1:before";
        UsagePayload? scanned = null;
        try
        {
            var coordinator = GraphRequestCoordinator.CreateForApp(
                getFolderPath: _ => root,
                sourceContextId: () => id,
                localFirst: _ => throw new InvalidOperationException(),
                graph: _ =>
                {
                    scanned = RetainedGraphStartupTests.SnapshotPayload("2026-01-01");
                    id = "sc1:after"; // the roots change while this scan runs
                    return scanned;
                });
            var richer = typeof(GraphRequestCoordinator)
                .GetField("_graph", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(coordinator) as Func<string?, UsagePayload>;
            var payload = richer!("2026");

            Assert.Equal(
                GraphSnapshotWriteStatus.Skipped,
                coordinator.Snapshot!.Write("2026", DateTimeOffset.UtcNow, payload, null));

            var again = richer("2026"); // starts and ends under sc1:after
            Assert.Equal(
                GraphSnapshotWriteStatus.Written,
                coordinator.Snapshot.Write("2026", DateTimeOffset.UtcNow, again, null));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ---- which account's usage a card shows (#160 contract) ----------------

    private static AgentUsagePayload ClaudeCards(params string?[] accountKeys) =>
        new("2026-01-01T00:00:00Z",
            [.. accountKeys.Select(key => new AgentUsageSnapshot(
                "claude", "source", "2026-01-01T00:00:00Z",
                [new UsageWindow(
                    Label: "Session",
                    UsedPercent: 10,
                    RemainingPercent: 90,
                    CardId: "claude.session",
                    PaceStatus: new PaceStatus(UsagePaceState.Available, WindowKey: "session.v1"))],
                AccountKey: key))]);

    [Fact]
    public void AttributableKeysAreTheConfigDirectoryCardsOnly()
    {
        var quota = new AgentUsagePayload("2026-01-01T00:00:00Z",
        [
            .. ClaudeCards(null, @"D:\work", AccountLabel.ClaudeDesktopKey, @"D:\work").Agents,
            new AgentUsageSnapshot("codex", "source", "2026-01-01T00:00:00Z", [], AccountKey: @"D:\other"),
        ]);

        Assert.Equal([@"D:\work"], ClaudeExtraRoots.AttributableAccountKeys(quota));
        Assert.Empty(ClaudeExtraRoots.AttributableAccountKeys(null));
    }

    private static QuotaLensProjection.Client ClaudeCard(
        string selectedAccount, IReadOnlyDictionary<string, WindowUsage>? accountUsage,
        long? mainFromMs = null, long? accountFromMs = null)
    {
        Localization.Load("en", AppContext.BaseDirectory);
        var primary = new WindowMessage(1_000, "claude", "anthropic", "m", 100, 0, 0, 0, 0, 0, true);
        return QuotaLensProjection.Build(
            history: [],
            ClaudeCards(null, @"D:\work"),
            new UsagePayload(
                new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                    PricingMode.BestEffort, CostCoverage.Complete),
                new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], []),
            windowUsage: new WindowUsage([primary], 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            new UsageAttribution.Table([], IsWritable: true),
            year: null,
            new QuotaLensProjection.Selection("claude", string.Empty, WindowCardAccount: selectedAccount),
            accountUsage,
            windowUsageFromMs: mainFromMs,
            accountWindowUsageFromMs: accountFromMs).Client!;
    }

    // A registry-attributable account whose own scan has not landed is not
    // read yet, not "unattributable": it must wait, not say "start unknown".
    [Fact]
    public void AnAttributableAccountWithNoScanYetIsPendingNotUnattributed()
    {
        var scan = ClaudeCard(@"D:\work", null).Scan!.Value;

        Assert.False(scan.Unattributed);
        Assert.Equal(WindowEquivalence.FetchOutcome.NotAttempted, scan.Outcome);
    }

    // The account rows come from the account scan, so coverage must use the
    // account scan's bound, not the main read's (the two are replaced separately).
    [Fact]
    public void AnAccountCardCoversWithTheAccountScansOwnBound()
    {
        var own = new WindowMessage(2_000, "claude", "anthropic", "m", 7, 0, 0, 0, 0, 0, true);
        var scan = ClaudeCard(
            @"D:\work",
            new Dictionary<string, WindowUsage> { [@"D:\work"] = new([own], 0, 0) },
            mainFromMs: 100, accountFromMs: 900).Scan!.Value;

        Assert.Equal(900, scan.FromMs);
        Assert.False(scan.Covers(500));
    }

    [Fact]
    public void AnExtraAccountWithItsOwnScanIsAttributedToThatScan()
    {
        var own = new WindowMessage(2_000, "claude", "anthropic", "m", 7, 0, 0, 0, 0, 0, true);
        var client = ClaudeCard(@"D:\work", new Dictionary<string, WindowUsage>
        {
            [@"D:\work"] = new([own], 0, 0),
        });

        Assert.Equal(@"D:\work", client.SelectedAccount);
        Assert.False(client.LocalUsageUnattributed);
        Assert.Equal([own], client.Messages);
    }

    /// <summary>A key the registry did not report (no scan for it) leaves the
    /// card unattributed and never shows the primary's messages.</summary>
    [Fact]
    public void AnAccountWithoutItsOwnScanStaysUnattributed()
    {
        var client = ClaudeCard(@"D:\work", new Dictionary<string, WindowUsage>
        {
            [@"D:\not-registered"] = new([], 0, 0),
        });

        Assert.Equal(@"D:\work", client.SelectedAccount);
        Assert.True(client.LocalUsageUnattributed);
        Assert.Empty(client.Messages);
    }

    // ---- Settings copy -----------------------------------------------------

    [Fact]
    public void EveryReasonCodeHasItsOwnSentence()
    {
        var generic = ClaudeAccountsCopy.Reason("not-a-code");
        foreach (var code in ClaudeAccountsCopy.ReasonCodes.Where(code => code != "empty"))
        {
            Assert.NotEqual(generic, ClaudeAccountsCopy.Reason(code));
        }
    }

    [Theory]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    public void EverySettingsStringIsTranslated(string language)
    {
        Localization.Load(language, AppContext.BaseDirectory);
        try
        {
            foreach (var english in ClaudeAccountsCopy.All())
            {
                Assert.NotEqual(english, english.Localized());
            }
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }
}
