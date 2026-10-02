using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Delta coverage for DayBars.swift → DayBars.cs: saturating token folds and
// the selection-derived range-end anchoring of the trailing window.
public class DayBarsTests
{
    private static Contribution Day(string date, params ContributionClient[] clients) =>
        new(date, new ContributionTotals(0, 0, 0), 1, new TokenBreakdown(0, 0, 0, 0, 0), clients);

    private static ContributionClient Client(
        string id, long tokens, double cost = 1, int messages = 1) =>
        new(id, "m", "anthropic", new TokenBreakdown(tokens, 0, 0, 0, 0), cost, messages);

    private static UsagePayload Payload(string metaEnd, params Contribution[] days) =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-06-01", metaEnd), PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            days);

    // Stacked by model, Grok Build's raw -build stripe and the session's
    // grok-<version> are one segment, as in the Models view.
    [Fact]
    public void ModelStackingGroupsGrokBuildUnderTheDisplayModel()
    {
        ContributionClient Grok(string model, long tokens) =>
            new("grok", model, "xai", new TokenBreakdown(tokens, 0, 0, 0, 0), 1, 1);
        var bars = DayBars.Build(
            Payload("2026-06-02", Day("2026-06-02", Grok("grok-4.6-build", 100), Grok("grok-4.6", 30))),
            ["grok"], StackBy.Model, ChartMetric.Tokens, new ModelColorMap([]), "2026-06-02");

        var segment = Assert.Single(bars.Single(bar => bar.Date == "2026-06-02").Segments);
        Assert.Equal("grok-4.6", segment.Key);
        Assert.Equal(130L, segment.Tokens);
    }

    [Fact]
    public void RangeEndOverrideAnchorsWindowToSelectedClients()
    {
        // meta range end is 2026-06-30 (a hidden client's later activity), but
        // the selected clients last acted on 2026-06-10. Passing rangeEnd keeps
        // 2026-06-10 as the last bar instead of trailing empty days to the 30th.
        var payload = Payload(
            "2026-06-30",
            Day("2026-06-10", Client("claude", 100)));

        var bars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Tokens,
            new ModelColorMap([]), endFallback: "2026-07-15",
            rangeEnd: "2026-06-10");

        Assert.Equal("2026-06-10", bars[^1].Date);
        Assert.Equal(100, bars[^1].TotalTokens);
    }

    [Fact]
    public void MetricWithoutDataFallsBackToMetaRange()
    {
        var payload = Payload(
            "2026-06-20",
            Day("2026-06-10", Client("claude", 0, 0, 1)));

        var bars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Tokens,
            new ModelColorMap([]), endFallback: "2026-07-15");

        Assert.Equal("2026-06-20", bars[^1].Date);
    }

    [Fact]
    public void TotalTokensSaturatesInsteadOfOverflowing()
    {
        // Two lanes each near long.MaxValue in the same day must clamp, not wrap.
        var payload = Payload(
            "2026-06-10",
            Day("2026-06-10",
                Client("claude", long.MaxValue), Client("codex", long.MaxValue)));

        var bars = DayBars.Build(
            payload, ["claude", "codex"], StackBy.Agent, ChartMetric.Tokens,
            new ModelColorMap([]), endFallback: "2026-07-15",
            rangeEnd: "2026-06-10");

        Assert.Equal(long.MaxValue, bars[^1].TotalTokens);
    }

    [Fact]
    public void CanonicalMembershipKeepsRawAgentSegmentIdentity()
    {
        var payload = Payload(
            "2026-06-10",
            Day("2026-06-10",
                Client("claude-code", 100, 0),
                Client("codex-cli", 50, 0)));

        var bars = DayBars.Build(
            payload, ["claude", "codex"], StackBy.Agent, ChartMetric.Tokens,
            new ModelColorMap([]), endFallback: "2026-07-15",
            rangeEnd: "2026-06-10");
        var segments = bars[^1].Segments;

        Assert.Equal(["claude-code", "codex-cli"], segments.Select(s => s.Key));
        Assert.Equal(
            [ClientRegistry.ShortName("claude-code"), ClientRegistry.ShortName("codex-cli")],
            segments.Select(s => s.Label));
    }

    [Fact]
    public void MetricAnchorFollowsOnlyTheActiveMetric()
    {
        var payload = Payload(
            "2026-06-30",
            Day("2026-06-01", Client("claude", 10, 0, 0)),
            Day("2026-06-02", Client("claude", 0, 2, 0)),
            Day("2026-06-03", Client("claude", 0, 0, 5)));

        var tokenBars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Tokens,
            new ModelColorMap([]), endFallback: "2026-07-15");
        var costBars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Cost,
            new ModelColorMap([]), endFallback: "2026-07-15");

        Assert.Equal("2026-06-01", tokenBars[^1].Date);
        Assert.Equal(10L, tokenBars[^1].TotalTokens);
        Assert.Equal("2026-06-02", costBars[^1].Date);
        Assert.Equal(2.0, costBars[^1].TotalCost);
        Assert.Empty(costBars[^1].Segments.Where(s => s.Tokens == 0 && s.Cost == 0));
    }

    [Fact]
    public void MessageOnlyStripeNeverCreatesSegment()
    {
        var payload = Payload(
            "2026-06-03",
            Day("2026-06-03", Client("claude", 0, 0, 4)));

        var bars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Tokens,
            new ModelColorMap([]), endFallback: "2026-07-15",
            rangeEnd: "2026-06-03");

        Assert.Empty(bars[^1].Segments);
    }

    [Fact]
    public void MetricWithoutPositiveDatumFallsBackToRangeThenMetaThenFallback()
    {
        var payload = Payload(
            "2026-06-20",
            Day("2026-06-01", Client("claude", 10, 0, 0)));

        var rangeBars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Cost,
            new ModelColorMap([]), endFallback: "2026-07-15",
            rangeEnd: "2026-06-10");
        var metaBars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Cost,
            new ModelColorMap([]), endFallback: "2026-07-15");
        var fallbackBars = DayBars.Build(
            Payload("", Day("2026-06-01", Client("claude", 10, 0, 0))),
            ["claude"], StackBy.Agent, ChartMetric.Cost,
            new ModelColorMap([]), endFallback: "2026-07-15");

        Assert.Equal("2026-06-10", rangeBars[^1].Date);
        Assert.Equal("2026-06-20", metaBars[^1].Date);
        Assert.Equal("2026-07-15", fallbackBars[^1].Date);
    }

    [Fact]
    public void HiddenAndUnselectedMetricTailsDoNotMoveAnchor()
    {
        var payload = Payload(
            "2026-06-30",
            Day("2026-06-02", Client("claude", 0, 2, 0)),
            Day("2026-06-29", Client("gemini", 0, 9, 0)),
            Day("2026-06-30", Client("claude", 0, 0, 4)));

        var bars = DayBars.Build(
            payload, ["claude"], StackBy.Agent, ChartMetric.Cost,
            new ModelColorMap([]), endFallback: "2026-07-15");

        Assert.Equal("2026-06-02", bars[^1].Date);
    }
}
