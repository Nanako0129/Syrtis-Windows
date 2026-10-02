using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>
/// The fold behind 每日・依訂閱: <see cref="AttributedDailySeries"/> plus
/// <see cref="SubscriptionTrendFold"/>. Transcribed from
/// AttributedDailySeries.swift and SubscriptionTrend.swift.
/// </summary>
public class SubscriptionTrendTests
{
    private static ContributionClient Client(
        string client, string provider, string model, long tokens, double cost) =>
        new(client, model, provider, new TokenBreakdown(tokens, 0, 0, 0, 0), cost, 1);

    private static Contribution Day(string date, params ContributionClient[] clients) =>
        new(
            date,
            new ContributionTotals(
                clients.Aggregate(0L, (acc, c) => acc + c.Tokens.Total),
                clients.Sum(c => c.Cost),
                clients.Length),
            1,
            new TokenBreakdown(0, 0, 0, 0, 0),
            clients);

    private static UsageAttribution.Record Assigned(
        string client, string provider, string target) =>
        new(client, provider, null, UsageAttribution.State.Assigned(target));

    private static UsageAttribution.Record Excluded(string client, string provider) =>
        new(client, provider, null, UsageAttribution.State.Excluded);

    // ---- AttributedDailySeries ------------------------------------------

    // The rule the card is built on: usage the user has not classified is a
    // point of its own, not a dropped row.
    [Fact]
    public void UnclassifiedUsageKeepsItsOwnPoint()
    {
        var points = AttributedDailySeries.Points(
            [Day(
                "2026-08-30",
                Client("claude", "anthropic", "sonnet", 100, 1.5),
                Client("codex", "openai", "gpt-5", 40, 0.5))],
            [Assigned("claude", "anthropic", "claude")]);

        Assert.Equal(2, points.Count);
        var assigned = Assert.Single(points, p => p.State == UsageAttribution.State.Assigned("claude"));
        var unassigned = Assert.Single(points, p => p.State == UsageAttribution.State.Unassigned);
        Assert.Equal(100, assigned.Tokens);
        Assert.Equal(40, unassigned.Tokens);
        Assert.Equal(140, points.Sum(p => p.Tokens));
        Assert.Equal(2.0, points.Sum(p => p.Cost), 6);
    }

    // Attribution matches the raw id; the bucket groups by display identity.
    [Fact]
    public void GrokBuildRowsBucketUnderTheGroupedModel()
    {
        var points = AttributedDailySeries.Points(
            [Day(
                "2026-08-30",
                Client("grok", "xai", "grok-4.6-build", 100, 2.0),
                Client("grok", "xai", "grok-4.6", 50, 1.0))],
            []);

        var point = Assert.Single(points);
        Assert.Equal("grok-4.6", point.Model);
        Assert.Equal(150, point.Tokens);
        Assert.Equal(3.0, point.Cost, 6);
    }

    // A merged provider id names more than one provider, so no declaration
    // about a single one can speak for the row.
    [Fact]
    public void MergedProviderIdStaysUnassignedDespiteAMatchingRecord()
    {
        var points = AttributedDailySeries.Points(
            [Day("2026-08-30", Client("claude", "anthropic,vertex", "sonnet", 100, 1))],
            [Assigned("claude", "anthropic,vertex", "claude")]);

        Assert.Equal(UsageAttribution.State.Unassigned, Assert.Single(points).State);
    }

    [Fact]
    public void RowsWithNeitherTokensNorCostAreNotPoints()
    {
        Assert.Empty(AttributedDailySeries.Points(
            [Day("2026-08-30", Client("claude", "anthropic", "sonnet", 0, 0))],
            []));
    }

    // Same (date, state, model) from two rows is one point, and permuting the
    // input cannot change the sum.
    [Fact]
    public void SameBucketFoldsIndependentlyOfRowOrder()
    {
        var a = Client("claude", "anthropic", "sonnet", 10, 0.1);
        var b = Client("claude", "anthropic", "sonnet", 3, 0.03);
        var forward = AttributedDailySeries.Points([Day("2026-08-30", a, b)], []);
        var reverse = AttributedDailySeries.Points([Day("2026-08-30", b, a)], []);

        Assert.Equal(13, Assert.Single(forward).Tokens);
        Assert.Equal(forward, reverse);
    }

    // ---- SubscriptionTrendFold ------------------------------------------

    // The one omission the user asked for by declaring it, and it is not the
    // same as unassigned: an excluded row leaves no band and no total.
    [Fact]
    public void DeclaredExcludedUsageIsDropped()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [Day(
                    "2026-08-30",
                    Client("claude", "anthropic", "sonnet", 100, 1),
                    Client("gemini", "google", "flash", 900, 9))],
                [Assigned("claude", "anthropic", "claude"), Excluded("gemini", "google")]),
            "2026-08-30",
            1);

        var day = Assert.Single(trend.Days);
        Assert.Equal(["claude"], trend.Targets);
        Assert.Equal(100, day.TotalTokens);
        Assert.Equal(1, day.TotalCost, 6);
    }

    // The reason unclassified usage is a band rather than a filter: the
    // segments have to add up to the day the rest of the app reports.
    [Fact]
    public void SegmentsSumToTheDayTotal()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [Day(
                    "2026-08-30",
                    Client("claude", "anthropic", "sonnet", 100, 1),
                    Client("claude", "anthropic", "opus", 20, 2),
                    Client("codex", "openai", "gpt-5", 40, 0.5))],
                [Assigned("claude", "anthropic", "claude")]),
            "2026-08-30",
            1);

        var day = Assert.Single(trend.Days);
        Assert.Equal(
            ["__unassigned", "claude"],
            day.ByTarget.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(day.TotalTokens, day.ByTarget.Values.Sum(b => b.Tokens));
        Assert.Equal(day.TotalCost, day.ByTarget.Values.Sum(b => b.Cost), 6);
        Assert.Equal(160, day.TotalTokens);
        Assert.Equal(3.5, day.TotalCost, 6);
        Assert.Equal(SubscriptionTrendFold.UnassignedTarget, "__unassigned");
    }

    // Gaps are days, not absences: a chart that omitted idle days would draw a
    // steady week and a burst-then-silence week identically.
    [Fact]
    public void EveryCalendarDayInTheRangeIsPresentOldestFirst()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [Day("2026-03-03", Client("claude", "anthropic", "sonnet", 5, 0.5))],
                []),
            "2026-03-04",
            3);

        Assert.Equal(
            ["2026-03-02", "2026-03-03", "2026-03-04"],
            trend.Days.Select(d => d.Date));
        Assert.True(trend.Days[0].IsEmpty);
        Assert.False(trend.Days[1].IsEmpty);
        Assert.True(trend.Days[2].IsEmpty);
    }

    // Neither the columns nor the legend may carry a day the range does not
    // cover: a target whose only usage is outside the window would be ranked and
    // listed with no band anywhere on the chart.
    [Fact]
    public void PointsOutsideTheRangeAreIgnored()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [
                    Day("2026-03-01", Client("gemini", "google", "flash", 900, 9)),
                    Day("2026-03-05", Client("codex", "openai", "gpt-5", 700, 7)),
                    Day("2026-03-03", Client("claude", "anthropic", "sonnet", 5, 0.5)),
                ],
                [
                    Assigned("claude", "anthropic", "claude"),
                    Assigned("codex", "openai", "codex"),
                    Assigned("gemini", "google", "gemini"),
                ]),
            "2026-03-04",
            3);

        Assert.Equal(["claude"], trend.Targets);
        Assert.Equal(["claude"], trend.TargetsByTokens);
        Assert.Equal(5, trend.PeakTokens);
        Assert.Equal(0.5, trend.PeakCost, 6);
    }

    // Cost and tokens do not rank the same subscriptions the same way, and the
    // legend draws the first four of whichever order is selected.
    [Fact]
    public void TokenOrderAndCostOrderAreSeparate()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [Day(
                    "2026-08-30",
                    Client("claude", "anthropic", "sonnet", 1_000_000, 0.01),
                    Client("codex", "openai", "gpt-5", 10, 40))],
                [Assigned("claude", "anthropic", "claude"), Assigned("codex", "openai", "codex")]),
            "2026-08-30",
            1);

        Assert.Equal(["codex", "claude"], trend.Targets);
        Assert.Equal(["claude", "codex"], trend.TargetsByTokens);
        Assert.Equal(trend.Targets, trend.TargetsFor(byTokens: false));
        Assert.Equal(trend.TargetsByTokens, trend.TargetsFor(byTokens: true));
    }

    // Equal totals tie-break by name so the bands do not reshuffle between
    // refreshes for no reason the user can see.
    [Fact]
    public void EqualTotalsTieBreakByName()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [Day(
                    "2026-08-30",
                    Client("codex", "openai", "gpt-5", 10, 1),
                    Client("claude", "anthropic", "sonnet", 10, 1))],
                [Assigned("claude", "anthropic", "claude"), Assigned("codex", "openai", "codex")]),
            "2026-08-30",
            1);

        Assert.Equal(["claude", "codex"], trend.Targets);
        Assert.Equal(["claude", "codex"], trend.TargetsByTokens);
    }

    [Fact]
    public void PeaksAreTheHeaviestDayNotTheRangeTotal()
    {
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(
                [
                    Day("2026-08-29", Client("claude", "anthropic", "sonnet", 10, 1)),
                    Day("2026-08-30", Client("claude", "anthropic", "sonnet", 30, 2)),
                ],
                []),
            "2026-08-30",
            2);

        Assert.Equal(30, trend.PeakTokens);
        Assert.Equal(2, trend.PeakCost, 6);
    }

    [Fact]
    public void ANonDateAnchorOrANonPositiveWindowIsTheEmptyTrend()
    {
        Assert.Same(
            SubscriptionTrend.Empty, SubscriptionTrendFold.Build([], "not-a-date", 14));
        Assert.Same(SubscriptionTrend.Empty, SubscriptionTrendFold.Build([], "2026-08-30", 0));
        Assert.Null(SubscriptionTrendFold.CalendarRange("2026-08-30", 0));
        Assert.Null(SubscriptionTrendFold.CalendarRange("2026-13-01", 3));
    }

    // Walked as calendar days, so a month boundary and a leap day land where a
    // reader expects them.
    [Fact]
    public void CalendarRangeCrossesMonthAndLeapBoundaries()
    {
        Assert.Equal(
            ["2026-01-30", "2026-01-31", "2026-02-01"],
            SubscriptionTrendFold.CalendarRange("2026-02-01", 3));
        Assert.Equal(
            ["2024-02-28", "2024-02-29", "2024-03-01"],
            SubscriptionTrendFold.CalendarRange("2024-03-01", 3));
    }
}
