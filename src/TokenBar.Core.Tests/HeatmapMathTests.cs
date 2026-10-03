using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

// ContributionHeatmap.swift's pure helpers. 2026-01-01 is a Thursday, so col 0
// starts Sun 2025-12-28 and Feb 1 (a Sunday) is col 5 row 0.
public class HeatmapMathTests
{
    private static PerDay Day(string date, long tokens, double cost) =>
        new(date, tokens, cost, 1, 0);

    private static GridLayout Grid2026(params PerDay[] days) =>
        Grid.Build("2026", days.ToDictionary(d => d.Date));

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(1, 100, 1)]
    [InlineData(24, 100, 1)]
    [InlineData(25, 100, 2)]
    [InlineData(49, 100, 2)]
    [InlineData(50, 100, 3)]
    [InlineData(74, 100, 3)]
    [InlineData(75, 100, 4)]
    [InlineData(100, 100, 4)]
    [InlineData(5, 0, 0)]
    public void LevelBuckets(double value, double max, int level) =>
        Assert.Equal(level, HeatmapMath.Level(value, max));

    [Fact]
    public void FillRampMatchesMacOs()
    {
        Assert.Equal(((byte)255, (byte)180, (byte)230, (byte)255), HeatmapMath.Fill(4, true));
        Assert.Equal(((byte)56, (byte)90, (byte)170, (byte)255), HeatmapMath.Fill(1, false));
        Assert.Equal(((byte)13, (byte)255, (byte)255, (byte)255), HeatmapMath.Fill(0, true));
        Assert.Equal(((byte)13, (byte)0, (byte)0, (byte)0), HeatmapMath.Fill(0, false));
        Assert.Equal(HeatmapMath.Fill(4, true), HeatmapMath.Fill(99, true));
    }

    [Fact]
    public void CutoffClipsAtTodayForCurrentAndFutureYearsAndDec31ForPast()
    {
        Assert.Equal("2026-10-03", HeatmapMath.Cutoff("2026", "2026-10-03"));
        Assert.Equal("2026-10-03", HeatmapMath.Cutoff("2027", "2026-10-03"));
        Assert.Equal("2025-12-31", HeatmapMath.Cutoff("2025", "2026-10-03"));
    }

    [Fact]
    public void FutureDaysAndOutOfYearPaddingAreNotRenderable()
    {
        var grid = Grid2026();
        var cutoff = "2026-02-10";
        Assert.False(HeatmapMath.IsRenderable(grid.Cells[0], cutoff));       // 2025-12-28 padding
        Assert.True(HeatmapMath.IsRenderable(grid.Cells.First(c => c.Date == "2026-02-10"), cutoff));
        Assert.False(HeatmapMath.IsRenderable(grid.Cells.First(c => c.Date == "2026-02-11"), cutoff));
    }

    [Fact]
    public void HasDataIsPerMetricNotActive()
    {
        // Cost with zero tokens: Grid's Active is tokens-gated, the heatmap's Price view must still draw it.
        var cell = Grid2026(Day("2026-03-04", 0, 1.5)).Cells.First(c => c.Date == "2026-03-04");
        Assert.True(HeatmapMath.HasData(cell, ChartMetric.Cost));
        Assert.False(HeatmapMath.HasData(cell, ChartMetric.Tokens));
        Assert.Equal(1.5, HeatmapMath.Value(cell, ChartMetric.Cost));
    }

    [Fact]
    public void MaxValueIgnoresHiddenFutureCellsAndFollowsMetric()
    {
        var grid = Grid2026(Day("2026-02-01", 100, 2), Day("2026-06-01", 9_000, 90));
        Assert.Equal(100, HeatmapMath.MaxValue(grid, ChartMetric.Tokens, "2026-03-01"));
        Assert.Equal(2, HeatmapMath.MaxValue(grid, ChartMetric.Cost, "2026-03-01"));
        Assert.Equal(9_000, HeatmapMath.MaxValue(grid, ChartMetric.Tokens, "2026-12-31"));
    }

    [Fact]
    public void VisibleColsAndMonthLabelsStopAtTheCutoff()
    {
        var grid = Grid2026();
        var cutoff = "2026-03-15";
        // Mar 15 2026 is a Sunday: (Mar15 - Dec28)/7 = 11 → col 11 → 12 columns.
        Assert.Equal(12, HeatmapMath.VisibleCols(grid, cutoff));
        var labels = HeatmapMath.MonthLabels(grid, cutoff);
        Assert.Equal([1, 2, 3], labels.Select(l => l.Month));
        Assert.Equal(0, labels[0].Col);  // Jan 1 sits in col 0 (Thursday)
        Assert.Equal(5, labels[1].Col);  // Feb 1 is a Sunday, col 5
        Assert.Equal(0, HeatmapMath.VisibleCols(Grid2026(), "2025-01-01")); // nothing renderable
    }

    [Fact]
    public void ContentWidthReservesLabelMarginOnlyWhenLastColumnIsLabelled()
    {
        Assert.Equal(0, HeatmapMath.ContentWidth(0, []));
        var plain = HeatmapMath.ContentWidth(12, [(0, 1)]);
        var labelled = HeatmapMath.ContentWidth(12, [(11, 12)]);
        Assert.Equal(HeatmapMath.LastColumnLabelMargin, labelled - plain, 6);
        Assert.Equal(HeatmapMath.CellOrigin(11, 0).X + HeatmapMath.Cell + HeatmapMath.HoverRingReach, plain, 6);
    }

    [Fact]
    public void CellOriginUsesTheStep() =>
        Assert.Equal((HeatmapMath.GridLeading + 2 * 14, HeatmapMath.GridTop + 3 * 14), HeatmapMath.CellOrigin(2, 3));

    [Fact]
    public void ARefreshKeepsTheScrolledPositionForTheSameYearAndCutoff() =>
        Assert.Equal(120.0, HeatmapMath.RestoreOffset(
            HeatmapMath.Anchor("2026", "2026-10-03"), HeatmapMath.Anchor("2026", "2026-10-03"), 120.0));

    [Theory]
    [InlineData("2026", "2026-10-04")] // the day rolled over: re-anchor to today
    [InlineData("2025", "2025-12-31")] // another year
    public void ANewYearOrCutoffReanchorsToTheLatestColumn(string year, string cutoff) =>
        Assert.Null(HeatmapMath.RestoreOffset(
            HeatmapMath.Anchor("2026", "2026-10-03"), HeatmapMath.Anchor(year, cutoff), 120.0));

    [Fact]
    public void TheFirstBuildOpensAtTheLatestColumn() =>
        Assert.Null(HeatmapMath.RestoreOffset(null, HeatmapMath.Anchor("2026", "2026-10-03"), null));
}
