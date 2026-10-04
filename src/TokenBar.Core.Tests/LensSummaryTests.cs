using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

// G5a/G5b/G5e: the Hourly, Agents and Stats lenses against macOS HourlyView,
// AgentsView and StatsView.
public class LensSummaryTests
{
    public LensSummaryTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static HourlyReportEntry Slot(string hour, long total, double cost) =>
        new(hour, ["claude"], ["m"], total, 0, 0, 0, 0, total, 1, 0, cost);

    [Fact]
    public void HourlyModePersistsUnderTheMacKeyAndRawValues()
    {
        Assert.Equal("tokenbar.hourly.mode", HourlyLens.ModeKey);
        Assert.Equal(HourlyMode.Profile, HourlyLens.ParseMode(HourlyLens.RawValue(HourlyMode.Profile)));
        Assert.Equal("profile", HourlyLens.RawValue(HourlyMode.Profile));
        Assert.Equal("timeline", HourlyLens.RawValue(HourlyMode.Timeline));
        Assert.Equal(HourlyMode.Timeline, HourlyLens.ParseMode(null));
        Assert.Equal(HourlyMode.Timeline, HourlyLens.ParseMode("bogus"));
    }

    [Fact]
    public void TimelineDropsIdleSlotsAndListsNewestFirst()
    {
        var rows = HourlyLens.Timeline(
            [Slot("2026-10-01 09:00", 5, 0), Slot("2026-10-01 10:00", 0, 0),
             Slot("2026-10-02 08:00", 0, 0.5), Slot("2026-10-01 23:00", 7, 0)]);

        Assert.Equal(
            ["2026-10-02 08:00", "2026-10-01 23:00", "2026-10-01 09:00"],
            rows.Select(r => r.Hour));
    }

    [Theory]
    [InlineData(200, 450, "Show 200 more · 200 of 450")]
    [InlineData(400, 450, "Show 50 more · 400 of 450")]
    [InlineData(200, 200, null)]
    public void TimelineWindowsTwoHundredRowsAtATime(int visible, int total, string? expected)
    {
        Assert.Equal(200, HourlyLens.TimelineInitial);
        Assert.Equal(200, HourlyLens.TimelineStep);
        Assert.Equal(expected, HourlyLens.ShowMore(visible, total));
    }

    [Fact]
    public void ProfileFoldsTokensAndCostOntoTheHourOfDay()
    {
        var profile = HourlyLens.Profile(
            [Slot("2026-10-01 09:00", 5, 1.0), Slot("2026-10-02 09:00", 7, 0.5),
             Slot("2026-10-02 14:00", 3, 2.0), Slot("garbage", 99, 9.0)]);

        Assert.Equal((12L, 1.5), profile[9]);
        Assert.Equal((3L, 2.0), profile[14]);
        Assert.Equal(15, profile.Sum(b => b.Tokens));
    }

    [Fact]
    public void SubtitleNamesThePeakHourOrTheSlotCount()
    {
        var entries = new[] { Slot("2026-10-01 09:00", 5, 1.0), Slot("2026-10-02 14:00", 5, 2.0) };
        var timeline = HourlyLens.Timeline(entries);
        var profile = HourlyLens.Profile(entries);

        // Tied at 5 tokens: the earlier hour is the peak, as macOS max(by:) keeps it.
        Assert.Equal("peak 09:00 · $3.00",
            HourlyLens.Subtitle(HourlyMode.Profile, timeline, profile, authoritative: true));
        Assert.Equal("2 hrs · $3.00",
            HourlyLens.Subtitle(HourlyMode.Timeline, timeline, profile, authoritative: true));
        Assert.Equal("—",
            HourlyLens.Subtitle(HourlyMode.Timeline, [], HourlyLens.Profile([]), authoritative: true));
    }

    private static AgentReportEntry Agent(string name, long total, double cost) =>
        new(name, ["claude"], total, 0, 0, 0, 0, total, cost, 1);

    [Fact]
    public void AgentShareIsTheRowsPartOfTheListedTotal()
    {
        AgentReportEntry[] entries = [Agent("a", 10, 3.0), Agent("b", 30, 1.0)];

        Assert.Equal("75.0%", CostSurfaceProjection.AgentShare(entries[0], entries, true));
        // Before costs are confirmed the bar and its share are both on tokens.
        Assert.Equal("25.0%", CostSurfaceProjection.AgentShare(entries[0], entries, false));
    }

    [Fact]
    public void AgentsHeaderCountsAgentsAndTheirCost()
    {
        Assert.Equal("1 agent · $3.00",
            CostSurfaceProjection.AgentsHeader([Agent("a", 10, 3.0)], true));
        Assert.Equal("2 agents · $4.00",
            CostSurfaceProjection.AgentsHeader([Agent("a", 10, 3.0), Agent("b", 30, 1.0)], true));
    }

    private static UsagePayload Payload() =>
        new(
            new UsageMeta("g", "test", new DateRange("2026-01-01", "2026-01-03"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            [Day("2026-01-01", 100, 2.0), Day("2026-01-02", 300, 5.0)]);

    private static Contribution Day(string date, long tokens, double cost) =>
        new(date, new ContributionTotals(tokens, cost, 1), 1, new TokenBreakdown(tokens, 0, 0, 0, 0),
            [new ContributionClient("claude", "m", "anthropic",
                new TokenBreakdown(tokens, 0, 0, 0, 0), cost, 1)]);

    [Fact]
    public void StatsSummaryHasTheSixMacCellsWithStreaksAmongThem()
    {
        var stats = new UsageStats(Payload(), new HashSet<string> { "claude" });

        var cells = StatsSummary.Cells(stats, authoritative: true);

        Assert.Equal(
            ["Total tokens", "Total spend", "Active days", "Avg / day", "Current streak", "Longest streak"],
            cells.Select(c => c.Label));
        Assert.Equal("$7.00", cells[1].Value);
        Assert.True(cells[1].Accent);
        Assert.Equal("2", cells[2].Value);
        Assert.Equal($"{stats.Streaks.Current}d", cells[4].Value);
        Assert.Equal("2d", cells[5].Value);
        Assert.Equal("$7.00", StatsSummary.Spending(stats, true));
    }

    [Fact]
    public void BestDayLineCarriesDateAndCost()
    {
        var stats = new UsageStats(Payload(), new HashSet<string> { "claude" });

        Assert.Equal($"{Format.MonthDay("2026-01-02")} · $5.00", StatsSummary.BestDay(stats, true));
        Assert.Equal("Checking", StatsSummary.BestDay(stats, false));
        Assert.Null(StatsSummary.BestDay(
            new UsageStats(Payload(), new HashSet<string> { "nobody" }), true));
        // No usage at all: no line, whether or not costs are confirmed yet.
        Assert.Null(StatsSummary.BestDay(
            new UsageStats(Payload(), new HashSet<string> { "nobody" }), false));
    }
}
