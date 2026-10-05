using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// Q36: the all-clients strip and heatmap draw only visible agents' windows
/// (macOS DashboardModel.swift:1502-1508 visibleAgents, :1684-1712 reads,
/// :1509-1526 prune of summaries / heatmap windows / heatmaps). A hidden
/// client's retained series must not draw a row, a picker window or a grid.
/// </summary>
public class QuotaVisibleAgentsTests
{
    public QuotaVisibleAgentsTests() => Localization.Load("en", AppContext.BaseDirectory);

    private const long Hour = 3_600;

    private static AgentUsageSnapshot Agent(string client, string? accountKey, string scope) =>
        new(client, "source", "2026-01-01T00:00:00Z",
            [new UsageWindow(Label: "Weekly", UsedPercent: 10, RemainingPercent: 90, CardId: client + "|weekly.v1",
                PaceStatus: new PaceStatus(UsagePaceState.Available, WindowKey: "weekly.v1"))],
            AccountKey: accountKey, HistoryScope: new AccountScopeStatus(scope));

    private static QuotaHistorySeries Series(string client, string scope) =>
        new(client, scope, "weekly.v1",
        [
            new QuotaHistorySample(100 * Hour, 5 * Hour, QuotaHistoryDurationSource.Provider, 10,
                96 * Hour, QuotaHistorySampleOrigin.LiveV3, false),
            new QuotaHistorySample(100 * Hour, 5 * Hour, QuotaHistoryDurationSource.Provider, 40,
                97 * Hour, QuotaHistorySampleOrigin.LiveV3, false),
        ]);

    private static QuotaLensProjection.Overview Overview(
        AgentUsageSnapshot[] agents, QuotaHistorySeries[] history,
        string[]? limitsHidden = null, string[]? tabHidden = null,
        bool payload = true, bool attempted = true,
        WindowEquivalence.FetchOutcome historyOutcome = WindowEquivalence.FetchOutcome.Succeeded) =>
        QuotaLensProjection.Build(
            history, payload ? new AgentUsagePayload("2026-01-01T00:00:00Z", agents) : null,
            new UsagePayload(
                new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                    PricingMode.BestEffort, CostCoverage.Complete),
                new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], []),
            windowUsage: null, windowUsageOutcome: WindowEquivalence.FetchOutcome.NotAttempted,
            quotaHistoryOutcome: historyOutcome,
            UsageAttribution.Table.Empty, year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty,
                PresentClients: ["codex", "opencode"],
                TabHidden: (tabHidden ?? []).ToHashSet(), LimitsHidden: (limitsHidden ?? []).ToHashSet()),
            quotaHistoryReadFailed: false, quotaAttempted: attempted).Overview;

    private static QuotaStripState Strip(QuotaLensProjection.Overview o) =>
        QuotaLensText.StripState(o.Summaries, o.Outcome, o.UnreadableClients.Count > 0);

    private static QuotaHeatmapState Heat(QuotaLensProjection.Overview o) =>
        QuotaLensText.HeatmapState(null, o.Outcome, o.UnreadableClients.Count > 0, o.Windows.Count == 0);

    [Fact]
    public void AVisibleClientsSeriesDrawsInStripPickerAndGrid()
    {
        var o = Overview([Agent("codex", null, "p")], [Series("codex", "p")]);

        Assert.Equal(QuotaStripState.Rows, Strip(o));
        Assert.Single(o.Summaries);
        Assert.Single(o.Windows);
        Assert.Single(o.Grids);
    }

    [Fact]
    public void ALimitsHiddenPrimaryDrawsNothingButItsExtraAccountStillDoes()
    {
        var o = Overview(
            [Agent("codex", null, "p"), Agent("codex", "x", "x")],
            [Series("codex", "p"), Series("codex", "x")], limitsHidden: ["codex"]);

        Assert.Equal(["x"], o.Summaries.Select(s => s.Id.AccountScope));
        Assert.Equal(["x"], o.Windows.Select(w => w.Id.AccountScope));
        Assert.Equal(["x"], o.Grids.Keys.Select(k => k.AccountScope));
    }

    [Fact]
    public void ATabHiddenClientsSeriesDrawsNothing()
    {
        var o = Overview(
            [Agent("codex", null, "p"), Agent("opencode", null, "o")],
            [Series("codex", "p"), Series("opencode", "o")], tabHidden: ["opencode"]);

        Assert.Equal(["codex"], o.Summaries.Select(s => s.Id.ProviderId));
        Assert.Equal(["codex"], o.Windows.Select(w => w.Id.ProviderId));
        Assert.Equal(["codex"], o.Grids.Keys.Select(k => k.ProviderId));
    }

    [Fact]
    public void EveryClientHiddenWithRetainedSeriesSaysNothingRecorded()
    {
        var o = Overview([Agent("codex", null, "p")], [Series("codex", "p")], limitsHidden: ["codex"]);

        Assert.Empty(o.Summaries);
        Assert.Empty(o.Windows);
        Assert.Empty(o.Grids);
        Assert.Equal(QuotaStripState.NoCompletedWindows, Strip(o));
        Assert.Equal(QuotaHeatmapState.NoMovement, Heat(o));
    }

    // No payload yet. macOS has no rows then (DashboardModel.swift:1543/:1663);
    // Windows deliberately draws retained series (maintainer decision), but
    // never a tab-hidden or limits-hidden client's. Round-1 2cb9c9b drew
    // nothing; ccd994a drew every series, hidden clients' included.
    [Fact]
    public void NoPayloadYetDrawsRetainedSeriesExceptHiddenClients()
    {
        // Maintainer-approved deviation from macOS (which has no rows before a
        // payload): Windows draws retained series at once, as the client lens
        // does, and drops by settings the clients visibleAgents would drop.
        QuotaHistorySeries[] history =
            [Series("codex", "p"), Series("opencode", "o"), Series("claude", "c")];

        var o = Overview([], history, limitsHidden: ["claude"], tabHidden: ["opencode"], payload: false, attempted: false);
        Assert.Equal(["codex"], o.Summaries.Select(s => s.Id.ProviderId));
        Assert.Equal(["codex"], o.Windows.Select(w => w.Id.ProviderId));
        Assert.Equal(["codex"], o.Grids.Keys.Select(k => k.ProviderId));
        Assert.Equal(QuotaStripState.Rows, Strip(o));
    }

    [Fact]
    public void NoPayloadAndNothingRetainedSaysLoadingUntilAttempted()
    {
        var loading = Overview([], [], payload: false, attempted: false);
        Assert.Equal(QuotaStripState.Loading, Strip(loading));
        Assert.Equal(QuotaHeatmapState.Loading, Heat(loading));

        var attempted = Overview([], [], payload: false, attempted: true);
        Assert.Equal(QuotaStripState.NoCompletedWindows, Strip(attempted));
        Assert.Equal(QuotaHeatmapState.NoMovement, Heat(attempted));
    }

    [Fact]
    public void NoPayloadStaysLoadingWhileTheHistoryReadIsUnattemptedEvenAfterTheFetch()
    {
        var o = Overview([], [], payload: false, attempted: true,
            historyOutcome: WindowEquivalence.FetchOutcome.NotAttempted);

        Assert.Equal(QuotaStripState.Loading, Strip(o));
    }

    // Q36 L2: a visible single-account client's series under a former account
    // scope is not drawn (macOS reads only historyReadAccountKey's curve).
    [Fact]
    public void AVisibleSingleAccountClientsFormerScopeSeriesIsDropped()
    {
        var o = Overview([Agent("codex", null, "p")], [Series("codex", "former"), Series("codex", "p")]);

        Assert.Equal(["p"], o.Summaries.Select(s => s.Id.AccountScope));
        Assert.Equal(["p"], o.Grids.Keys.Select(k => k.AccountScope));
    }

    // Q36 L3b: an Antigravity merged primary reads the ADOPTED account's
    // scope (HistoryReadScope), not its own HistoryScope.
    [Fact]
    public void AMergedPrimaryReadsTheAdoptedAccountsScopeNotItsOwn()
    {
        var merged = Agent("codex", null, "cli") with
        {
            HistoryAccountKey = "acct",
            HistoryAccountScope = new AccountScopeStatus("adopted"),
        };

        var o = Overview([merged],
            [Series("codex", "adopted"), Series("codex", "cli"), Series("codex", "other")]);

        Assert.Equal(["adopted"], o.Summaries.Select(s => s.Id.AccountScope));
        Assert.Equal(["adopted"], o.Windows.Select(w => w.Id.AccountScope));
    }
}
