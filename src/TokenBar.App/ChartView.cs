namespace TokenBar.App;

/// <summary>
/// The usage card's three views, ported from macOS <c>ChartView</c>
/// (UsageChartCard.swift:28-60). The persisted raw values are macOS's, so the
/// same <c>tokenbar.chart.view</c> string round-trips: "2d" bars (also the
/// legacy default), "heat", "3d". Anything else falls back to Bars.
/// </summary>
public enum ChartView
{
    Bars,
    Heatmap,
    ThreeD,
}

public static class ChartViews
{
    public const string Key = "tokenbar.chart.view";

    public static string Raw(this ChartView view) => view switch
    {
        ChartView.Heatmap => "heat",
        ChartView.ThreeD => "3d",
        _ => "2d",
    };

    public static ChartView Parse(string? raw) => raw switch
    {
        "heat" => ChartView.Heatmap,
        "3d" => ChartView.ThreeD,
        _ => ChartView.Bars,
    };

    /// <summary>Ctrl+G's cycle and the header picker's left-to-right order:
    /// bars, heatmap, 3D, bars. The only copy of the order.</summary>
    public static ChartView Next(this ChartView view) => view switch
    {
        ChartView.Bars => ChartView.Heatmap,
        ChartView.Heatmap => ChartView.ThreeD,
        _ => ChartView.Bars,
    };
}
