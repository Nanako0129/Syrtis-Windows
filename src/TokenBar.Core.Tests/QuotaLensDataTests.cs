using TokenBar.App;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// What the lens hands its two cards, assembled from the persisted series and
// the live agent-usage payload. DashboardView.xaml.cs is compiled by no test
// project, which is why the join lives here and not inside BuildQuota.
public class QuotaLensDataTests
{
    public QuotaLensDataTests() => Localization.Load("en", AppContext.BaseDirectory);

    private const long Hour = 3_600;

    /// <summary>Two readings an hour apart inside one completed cycle: enough
    /// for a cycle AND a placeable grid.</summary>
    private static QuotaHistorySeries Series(
        string provider = "claude", string scope = "primary", string window = "session.v1",
        double from = 10, double to = 40) =>
        new(provider, scope, window,
        [
            new QuotaHistorySample(
                ResetAt: 100 * Hour, DurationSeconds: 5 * Hour,
                DurationSource: QuotaHistoryDurationSource.Provider, UsedPercent: from,
                SampledAt: 96 * Hour, Origin: QuotaHistorySampleOrigin.LiveV3,
                IsActiveGroup: false),
            new QuotaHistorySample(
                ResetAt: 100 * Hour, DurationSeconds: 5 * Hour,
                DurationSource: QuotaHistoryDurationSource.Provider, UsedPercent: to,
                SampledAt: 97 * Hour, Origin: QuotaHistorySampleOrigin.LiveV3,
                IsActiveGroup: false),
        ]);

    private static AgentUsagePayload Payload(
        string clientId, string windowKey, string label) =>
        new("2026-08-31T00:00:00Z",
        [
            new AgentUsageSnapshot(clientId, "oauth", "2026-08-31T00:00:00Z",
            [
                new UsageWindow(
                    Label: label, UsedPercent: 40, RemainingPercent: 60,
                    CardId: "card.v1",
                    PaceStatus: new PaceStatus(
                        UsagePaceState.Available, WindowKey: windowKey,
                        DurationSeconds: 5 * Hour)),
            ]),
        ]);

    [Fact]
    public void TheLabelJoinIsClientAndWindowKey()
    {
        var (summaries, windows, _) = QuotaLensData.Build(
            [Series()], Payload("claude", "session.v1", "Session"));

        Assert.Equal("Session", Assert.Single(summaries).WindowLabel);
        Assert.Equal("Claude Code · Session", QuotaLabels.RowLabel(summaries[0]));
        Assert.Equal("Claude Code · Session", QuotaLabels.PickerLabel(Assert.Single(windows)));
    }

    // A no-match series keeps its identity and loses only its label. The null
    // has to survive this seam: pre-filling it with the WindowKey here would
    // leave nothing able to tell the two apart.
    //
    // Since 2026-09-19 the two DO render alike — QuotaLabels.Window turns the
    // key `session.v1` into the same "Session" a joined label produces, because
    // an internal code has no business on screen. That makes the null MORE
    // load-bearing, not less: the rendered string can no longer distinguish a
    // live label from a derived one, so this field is the only thing that can,
    // and any future code needing the distinction has to read it here.
    [Theory]
    [InlineData("codex", "session.v1")] // wrong client
    [InlineData("claude", "weekly.v1")] // wrong window
    public void AMissedJoinLeavesTheLabelNullAndFallsBackInQuotaLabels(
        string client, string window)
    {
        var (summaries, _, _) = QuotaLensData.Build(
            [Series()], Payload(client, window, "Session"));

        var summary = Assert.Single(summaries);
        Assert.Null(summary.WindowLabel);
        Assert.Equal("Claude Code · Session", QuotaLabels.RowLabel(summary));
    }

    [Fact]
    public void NoPayloadAtAllStillProducesRows()
    {
        var (summaries, windows, grids) = QuotaLensData.Build([Series()], null);

        Assert.Null(Assert.Single(summaries).WindowLabel);
        Assert.Single(windows);
        Assert.Single(grids);
    }

    // The store's own triple, not (client, window): the grids are keyed by it,
    // and a key that dropped the scope would let one account's grid overwrite
    // the other's.
    [Fact]
    public void TwoAccountScopesOfOneWindowKeepSeparateGrids()
    {
        var (summaries, windows, grids) = QuotaLensData.Build(
            [
                Series(scope: "acct-a", from: 10, to: 40),
                Series(scope: "acct-b", from: 10, to: 90),
            ],
            Payload("claude", "session.v1", "Session"));

        Assert.Equal(2, grids.Count);
        Assert.Equal(2, summaries.Count);
        Assert.Equal(2, windows.Count);
        Assert.Equal(
            30, grids[new QuotaWindowIdentity("claude", "acct-a", "session.v1")].Total, 3);
        Assert.Equal(
            80, grids[new QuotaWindowIdentity("claude", "acct-b", "session.v1")].Total, 3);
    }

    [Fact]
    public void NoHistoryYetIsEmptyRatherThanAThrow()
    {
        var (summaries, windows, grids) = QuotaLensData.Build(null, null);

        Assert.Empty(summaries);
        Assert.Empty(windows);
        Assert.Empty(grids);
    }
}
