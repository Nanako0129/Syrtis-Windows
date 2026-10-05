using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// Q33: the Quota strip/heatmap "could not be read" state follows macOS's
/// per-client rule (DashboardModel.swift:1502-1508 visibleAgents,
/// :1845-1862 quotaUnreadableClients; QuotaView.swift:87/:92 tab,
/// :125/:132 all-clients).
/// <para>Windows reads quota history in ONE call and has no per-window read
/// failure, so partial unreadability ("A failed, B read") is not reachable
/// here. These tests use the whole-fetch-failure analog: outcome Failed means
/// nothing is retained, and every visible agent with a card window is
/// unreadable.</para>
/// </summary>
public class QuotaUnreadableTests
{
    public QuotaUnreadableTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static UsageWindow Window(string cardId) =>
        new(Label: "Weekly", UsedPercent: 10, RemainingPercent: 90, CardId: cardId,
            PaceStatus: new PaceStatus(UsagePaceState.Available, WindowKey: "weekly.v1"));

    private static AgentUsageSnapshot Agent(string clientId, string? accountKey, params UsageWindow[] windows) =>
        new(clientId, "source", "2026-01-01T00:00:00Z", windows, AccountKey: accountKey);

    private static AgentUsagePayload Payload(params AgentUsageSnapshot[] agents) =>
        new("2026-01-01T00:00:00Z", agents);

    private static QuotaLensProjection.Selection Sel(
        string tab, IReadOnlySet<string>? limitsHidden = null) =>
        new(tab, string.Empty, PresentClients: ["codex", "opencode"],
            TabHidden: new HashSet<string>(), LimitsHidden: limitsHidden ?? new HashSet<string>());

    private static QuotaLensProjection.Overview Overview(
        AgentUsagePayload? quota, WindowEquivalence.FetchOutcome outcome, IReadOnlySet<string>? limitsHidden = null) =>
        QuotaLensProjection.Build(
            history: null, quota, EmptyGraph(), windowUsage: null,
            windowUsageOutcome: WindowEquivalence.FetchOutcome.NotAttempted,
            quotaHistoryOutcome: outcome,
            UsageAttribution.Table.Empty, year: null,
            Sel(ClientRegistry.OverviewTab, limitsHidden)).Overview;

    private static UsagePayload EmptyGraph() =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            []);

    private static QuotaWindowSummary Summary(double peak) =>
        new(
            new QuotaWindowIdentity("claude", "primary", "session.v1"),
            "Session", Recent: [peak], RecentPeaks: [peak], PeakPercent: peak,
            NeverExhausted: true, CycleCount: 1);

    private static QuotaStripState Strip(QuotaLensProjection.Overview o, bool unreadable) =>
        QuotaLensText.StripState(o.Summaries, o.Outcome, unreadable);

    private static QuotaHeatmapState Heat(QuotaLensProjection.Overview o, bool unreadable) =>
        QuotaLensText.HeatmapState(null, o.Outcome, unreadable, o.Windows.Count == 0);

    private const WindowEquivalence.FetchOutcome Failed = WindowEquivalence.FetchOutcome.Failed;

    // Tab case: A (codex) has a card window, B (opencode) has none. Old code:
    // both tabs Failed (whole-fetch outcome). New: only A.
    [Fact]
    public void FailedFetchMarksOnlyTheTabWhoseClientHasACardWindow()
    {
        var o = Overview(Payload(Agent("codex", null, Window("codex|weekly.v1")), Agent("opencode", null)), Failed);

        var codex = o.UnreadableIn(ClientRegistry.TabSlice("codex"));
        var opencode = o.UnreadableIn(ClientRegistry.TabSlice("opencode"));

        Assert.True(codex);
        Assert.False(opencode);
        Assert.Equal(QuotaStripState.Failed, Strip(o, codex));
        Assert.Equal(QuotaHeatmapState.Failed, Heat(o, codex));
        Assert.Equal(QuotaStripState.NoCompletedWindows, Strip(o, opencode));
        Assert.Equal(QuotaHeatmapState.NoMovement, Heat(o, opencode));
    }

    // Overview case: no visible agent has a card window -> not unreadable.
    [Fact]
    public void OverviewIsNotUnreadableWhenNoVisibleAgentHasACardWindow()
    {
        var o = Overview(Payload(Agent("codex", null)), Failed);

        Assert.Empty(o.UnreadableClients);
        Assert.Equal(QuotaStripState.NoCompletedWindows, Strip(o, o.UnreadableClients.Count > 0));
        Assert.Equal(QuotaHeatmapState.NoMovement, Heat(o, o.UnreadableClients.Count > 0));
    }

    [Fact]
    public void OverviewIsUnreadableWhenAVisibleAgentHasACardWindow()
    {
        var o = Overview(Payload(Agent("codex", null, Window("codex|weekly.v1"))), Failed);

        Assert.Equal(["codex"], o.UnreadableClients);
        Assert.Equal(QuotaStripState.Failed, Strip(o, o.UnreadableClients.Count > 0));
        Assert.Equal(QuotaHeatmapState.Failed, Heat(o, o.UnreadableClients.Count > 0));
    }

    // macOS visibleAgents: a limits-hidden PRIMARY drops out, its extra account stays.
    [Fact]
    public void ALimitsHiddenPrimaryIsNotVisibleButItsExtraAccountIs()
    {
        var hidden = new HashSet<string> { "codex" };
        var primary = Overview(Payload(Agent("codex", null, Window("codex|weekly.v1"))), Failed, hidden);
        var extra = Overview(Payload(Agent("codex", "acct-2", Window("codex|weekly.v1"))), Failed, hidden);

        Assert.Empty(primary.UnreadableClients);
        Assert.Equal(["codex"], extra.UnreadableClients);
    }

    [Theory]
    [InlineData(WindowEquivalence.FetchOutcome.Succeeded)]
    [InlineData(WindowEquivalence.FetchOutcome.NotAttempted)]
    public void ASucceededOrLoadingFetchLeavesNothingUnreadable(WindowEquivalence.FetchOutcome outcome)
    {
        var o = Overview(Payload(Agent("codex", null, Window("codex|weekly.v1"))), outcome);

        Assert.Empty(o.UnreadableClients);
        Assert.False(o.UnreadableIn(ClientRegistry.TabSlice("codex")));
    }

    [Fact]
    public void LoadingStaysLoadingEvenWhenUnreadable()
    {
        var o = Overview(null, WindowEquivalence.FetchOutcome.NotAttempted);

        Assert.Equal(QuotaStripState.Loading, Strip(o, true));
        Assert.Equal(QuotaHeatmapState.Loading, Heat(o, true));
    }

    // Rows / Grid win over everything; the heatmap's Failed also needs an empty window list.
    [Fact]
    public void RowsAndGridWinAndHeatmapFailedNeedsNoWindows()
    {
        Assert.Equal(QuotaStripState.Rows, QuotaLensText.StripState([Summary(40)], Failed, true));
        Assert.Equal(QuotaHeatmapState.NoMovement, QuotaLensText.HeatmapState(null, Failed, true, windowsEmpty: false));
        Assert.Equal(QuotaHeatmapState.Failed, QuotaLensText.HeatmapState(null, Failed, true, windowsEmpty: true));
        Assert.Equal(
            QuotaHeatmapState.Grid,
            QuotaLensText.HeatmapState(QuotaHeatmap.Empty with { Total = 12 }, Failed, true, true));
    }
}
