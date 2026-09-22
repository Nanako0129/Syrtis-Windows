using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Where a client-reported cost stops being expensive and starts being broken
/// (port of TokenBarCore/ModelReport.swift's <c>CostPlausibility</c> and
/// <c>implausibleCostRatio</c>).
/// <para>
/// Judged on a row AFTER <see cref="ModelReportFold"/> has merged provider-split
/// rows, because that is the row the reader sees and the fold sums both cost and
/// estimate — judging a component would describe part of a row's spend.
/// </para>
/// </summary>
public static class CostPlausibility
{
    /// <summary>
    /// Measured on macOS against real local data, LiteLLM estimate vs
    /// OpenCode's own figure:
    /// <code>
    ///     deepseek-v4-flash (default)          0.7139 /    2.5719 =   0.3x
    ///     deepseek-v4-flash (low)              0.2943 /    1.0470 =   0.3x
    ///     deepseek-v4-pro                      3.9039 /   26.4468 =   0.1x
    ///     deepseek-v4-flash @ exptech (user) 5495.30  /   17.84   = 308x
    ///     deepseek-v4-flash-free    (user)   3174.69  /    9.79   = 324x
    /// </code>
    /// Healthy rows top out near 0.3x, so 50x leaves two orders of magnitude of
    /// headroom while still catching the reported ones. The estimate is only
    /// approximate — a near-miss pricing key, or a row missing cache rates,
    /// skews it by single-digit multiples — and nothing short of a unit-scale
    /// error clears 50x. These figures are macOS's measurement, not repeated
    /// on Windows.
    /// </summary>
    public const double Threshold = 50;

    /// <summary>Amber, not red: the row is untrustworthy, not broken — the tokens
    /// are real and the client may yet be right about its own rate.</summary>
    public const string WarningColor = "#f59e0b";

    /// <summary>
    /// How many times the local estimate this row's reported cost is, or null
    /// when it is not implausible or cannot be judged. Null whenever there is no
    /// usable estimate or no positive finite cost: a table that cannot price the
    /// tokens is no evidence about the figure, and a zero cost is not a claim
    /// to check.
    /// </summary>
    public static double? ImplausibleRatio(ModelReportEntry entry)
    {
        if (entry.CostEstimate is not { } estimate || !(estimate > 0)
            || !double.IsFinite(entry.Cost) || !(entry.Cost > 0))
        {
            return null;
        }

        var ratio = entry.Cost / estimate;
        return ratio > Threshold ? ratio : null;
    }
}
