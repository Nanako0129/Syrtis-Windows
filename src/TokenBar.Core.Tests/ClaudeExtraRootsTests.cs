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

    [Theory]
    [InlineData(@"C:\Users\Me", "homeDirectory")]
    [InlineData(@"c:/users/me/", "homeDirectory")]
    [InlineData(@"C:\Users\Me\.claude", "defaultConfigDir")]
    [InlineData(@"c:/USERS/me/.CLAUDE/", "defaultConfigDir")]
    [InlineData(@"d:/WORK/.claude", "duplicate")]
    [InlineData(@"C:\Users\Me\.claude-work", null)]
    [InlineData(@"C:\Users\Me\.claude\projects", null)]
    public void UiRulesFoldCaseAndSeparators(string path, string? expected)
    {
        Assert.Equal(expected, ClaudeExtraRoots.UiRejection(path, [@"D:\work\.claude"], @"C:\Users\Me"));
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
        var pusher = new ClaudeRootsPusher(
            () => [@"D:\a", @"\\server\share", @"E:\b"],
            dirs => Ok(2, new RootRejection(1, "unsupportedPath")),
            roots =>
            {
                scanned = roots;
                // Index 3 = E:\b\transcripts, the second accepted directory.
                return Ok(3, new RootRejection(3, "notDirectory"));
            },
            _ => { });

        Assert.True(pusher.Request().Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal([@"D:\a\projects", @"D:\a\transcripts", @"E:\b\projects", @"E:\b\transcripts"], scanned);
        Assert.Equal(
            new Dictionary<int, string> { [1] = "unsupportedPath", [2] = "notDirectory" },
            pusher.Last!.Rejected);
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

    [Fact]
    public void LaunchGateFallsBackOnAFailedPushAndLogsTheTypeOnly()
    {
        var log = new List<string>();
        var failed = Task.FromException(new UnauthorizedAccessException($"denied: {Sentinel}"));

        ClaudeExtraRoots.AwaitLaunch(failed, TimeSpan.FromSeconds(5), log.Add);

        var line = Assert.Single(log);
        Assert.Contains(nameof(UnauthorizedAccessException), line);
        Assert.DoesNotContain("SENTINEL", line, StringComparison.OrdinalIgnoreCase);
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
        string selectedAccount, IReadOnlyDictionary<string, WindowUsage>? accountUsage)
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
            accountUsage).Client!;
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
