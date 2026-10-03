using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The Stats lens's summary card, after macOS StatsView.swift:47-106:
/// the spending sentence, six metric cells in macOS order, and the best-day
/// line. Streaks are two of the six cells, so Stats carries no Streaks card of
/// its own. DashboardView.xaml.cs compiles under no test project, so the cell
/// list lives here.</summary>
public static class StatsSummary
{
    public sealed record Cell(string Label, string Value, bool Accent);

    /// <summary>The amount the "Your total spending is " sentence ends on.</summary>
    public static string Spending(UsageStats stats, bool authoritative) =>
        CostSurfaceProjection.CostText(stats.TotalCost, authoritative);

    public static IReadOnlyList<Cell> Cells(UsageStats stats, bool authoritative) =>
    [
        new("Total tokens".Localized(), Format.CompactTokens(stats.TotalTokens), false),
        new("Total spend".Localized(), Spending(stats, authoritative), authoritative),
        new("Active days".Localized(), $"{stats.ActiveDays}", false),
        new("Avg / day".Localized(),
            CostSurfaceProjection.CostText(stats.AveragePerDay, authoritative), false),
        new("Current streak".Localized(), "{0}d".Localized(stats.Streaks.Current), false),
        new("Longest streak".Localized(), "{0}d".Localized(stats.Streaks.Longest), false),
    ];

    /// <summary>"MM-DD · $", or null when there is no best day, so the line
    /// is left out as macOS leaves it out. While costs are unconfirmed the
    /// best day itself is unknown, so the line reads "Checking".</summary>
    public static string? BestDay(UsageStats stats, bool authoritative) =>
        !authoritative ? CostSurfaceProjection.Checking
        : stats.BestDay is { } best ? $"{Format.MonthDay(best.Date)} · {Format.Usd(best.Cost)}"
        : null;
}
