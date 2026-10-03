using TokenBar.Core;

namespace TokenBar.App;

/// <summary>
/// Pure computations of the flat year heatmap, ported from macOS
/// ContributionHeatmap.swift / HeatmapLayout. The cells come from
/// <see cref="Grid.Build"/>, the same call the 3D graph reads, so the two
/// views cannot disagree on data. DashboardView only draws what this decides.
/// </summary>
public static class HeatmapMath
{
    // Geometry (ContributionHeatmap.swift:13-17,25,81). Named so a design pass
    // touches one line.
    public const double Cell = 11;
    public const double Gap = 3;
    public const double Step = Cell + Gap;
    public const double MonthLabelHeight = 14;
    /// <summary>Extra trailing width when the last column carries a month label
    /// (the label is wider than a cell and would clip at the scroll end).</summary>
    public const double LastColumnLabelMargin = 28;
    /// <summary>Outward reach of the hover ring: 1 (inset into the gap) + 0.5
    /// (half the 1px stroke) + 1.5 (glow). The ring here has no glow, so the
    /// reserve is kept only to match the layout.</summary>
    public const double HoverRingReach = 1 + 0.5 + 1.5;
    public const double GridLeading = HoverRingReach;
    public const double GridTop = MonthLabelHeight + HoverRingReach;
    public const double GridHeight = 7 * Step - Gap;
    public const double ContentHeight = GridTop + GridHeight + HoverRingReach;

    public static readonly string[] MonthAbbrev =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static (double X, double Y) CellOrigin(int col, int row) =>
        (GridLeading + col * Step, GridTop + row * Step);

    /// <summary>Intensity bucket 0..4 (token-monitor heatmapIntensity: >=.75 → 4,
    /// >=.5 → 3, >=.25 → 2, >0 → 1, else 0).</summary>
    public static int Level(double value, double max)
    {
        if (max <= 0 || value <= 0)
        {
            return 0;
        }

        var ratio = value / max;
        return ratio >= 0.75 ? 4 : ratio >= 0.5 ? 3 : ratio >= 0.25 ? 2 : 1;
    }

    /// <summary>Blue ramp of ContributionHeatmap.swift:87-93 as ARGB. Level 0 is
    /// the theme's primary at 5% (white on dark, black on light).</summary>
    public static (byte A, byte R, byte G, byte B) Fill(int level, bool dark) =>
        Math.Clamp(level, 0, 4) switch
        {
            0 => dark ? ((byte)13, (byte)255, (byte)255, (byte)255)
                      : ((byte)13, (byte)0, (byte)0, (byte)0),
            1 => (56, 90, 170, 255),
            2 => (128, 120, 190, 255),
            3 => (209, 150, 210, 255),
            _ => (255, 180, 230, 255),
        };

    public static double Value(GridCell cell, ChartMetric metric) =>
        metric == ChartMetric.Cost ? cell.Cost : cell.Tokens;

    /// <summary>"Has data" per metric, never <c>cell.Active</c> (tokens-only):
    /// a day can carry cost with zero tokens.</summary>
    public static bool HasData(GridCell cell, ChartMetric metric) =>
        cell.InYear && Value(cell, metric) > 0;

    /// <summary>Last day drawn: a past year clips at its Dec 31, the current
    /// (or a future) year at today. ISO strings order chronologically.</summary>
    public static string Cutoff(string year, string today) =>
        string.CompareOrdinal($"{year}-12-31", today) <= 0 ? $"{year}-12-31" : today;

    public static bool IsRenderable(GridCell cell, string cutoff) =>
        cell.InYear && string.CompareOrdinal(cell.Date, cutoff) <= 0;

    /// <summary>Intensity denominator, per metric, over renderable cells only
    /// (a hidden future cell must not dim every visible one).</summary>
    public static double MaxValue(GridLayout grid, ChartMetric metric, string cutoff)
    {
        var max = 0.0;
        foreach (var cell in grid.Cells)
        {
            if (IsRenderable(cell, cutoff))
            {
                max = Math.Max(max, Value(cell, metric));
            }
        }

        return max;
    }

    /// <summary>Number of columns up to the last renderable one; layout width
    /// and month labels derive from this, never from <c>grid.Cols</c>.</summary>
    public static int VisibleCols(GridLayout grid, string cutoff)
    {
        var last = -1;
        foreach (var cell in grid.Cells)
        {
            if (IsRenderable(cell, cutoff))
            {
                last = Math.Max(last, cell.Col);
            }
        }

        return last + 1;
    }

    /// <summary>Columns that carry a month header: the renderable cell dated the
    /// 1st. Month is 1..12.</summary>
    public static List<(int Col, int Month)> MonthLabels(GridLayout grid, string cutoff)
    {
        var labels = new List<(int, int)>();
        foreach (var cell in grid.Cells)
        {
            if (IsRenderable(cell, cutoff) && cell.Date.EndsWith("-01", StringComparison.Ordinal)
                && int.TryParse(cell.Date.AsSpan(5, 2), out var month) && month is >= 1 and <= 12)
            {
                labels.Add((cell.Col, month));
            }
        }

        return labels;
    }

    public static double ContentWidth(int visibleCols, IReadOnlyList<(int Col, int Month)> labels)
    {
        if (visibleCols <= 0)
        {
            return 0;
        }

        var lastCol = visibleCols - 1;
        var baseWidth = CellOrigin(lastCol, 0).X + Cell + HoverRingReach;
        return labels.Any(l => l.Col == lastCol) ? baseWidth + LastColumnLabelMargin : baseWidth;
    }
}
