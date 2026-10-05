using System.Globalization;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Which of the heatmap card's four states applies, in macOS's order
/// (<c>QuotaHeatmapCard.swift:59-82</c>).</summary>
public enum QuotaHeatmapState
{
    /// <summary>The 7 x 24 grid, its hour axis and its footnote.</summary>
    Grid,

    /// <summary>Movement WAS recorded — every pair of readings was simply too far
    /// apart to place it on a grid, which leaves <c>Total</c> at zero. Falling
    /// through to <see cref="NoMovement"/> stated the opposite of the truth, and
    /// hid the one line that explains it.</summary>
    Unplaced,

    /// <summary>Asked, and the allowance has not moved yet.</summary>
    NoMovement,

    /// <summary>Not asked yet. Not optional and not rare: a lazy lens fetches on
    /// first visit, so the first paint on every cold start lands here. Collapsed
    /// into <see cref="NoMovement"/> the card says "nothing recorded yet" at the
    /// exact moment the truth is "not asked yet" — the absence-versus-reason
    /// defect this repo already paid for on the Overview quota card.</summary>
    Loading,

    /// <summary>Asked, and the most recent fetch threw. Distinct from
    /// <see cref="NoMovement"/> ("asked and the answer was nothing") and from
    /// <see cref="Loading"/> ("still waiting for a first answer") — a failed
    /// read is neither, and collapsing it into either one states something
    /// false about a request that never landed.</summary>
    Failed,
}

/// <summary>The strip card's states. Same last two as the heatmap's, for the
/// same reason, with its own copy.</summary>
public enum QuotaStripState
{
    Rows,
    NoCompletedWindows,
    Loading,

    /// <summary>Same distinction as <see cref="QuotaHeatmapState.Failed"/>.</summary>
    Failed,
}

/// <summary>
/// The state choice and the card copy for the Quota lens's two cards (port of
/// the string half of <c>QuotaHistoryStripCard.swift</c> and
/// <c>QuotaHeatmapCard.swift</c>; the WinUI layout lives in
/// DashboardView.xaml.cs).
/// <para>
/// Pulled into TokenBar.Core.Tests via &lt;Compile Include&gt;, the same way
/// <see cref="QuotaSummaryText"/> is and for the same reason: left inside
/// <c>BuildQuota</c> the four-state branch would sit in a file no test project
/// compiles, which is to say it would not be asserted at all.
/// </para>
/// </summary>
public static class QuotaLensText
{
    /// <summary>Which of the heatmap card's four-plus-one states applies.
    /// Order matters: a drawable grid, then movement that could not be
    /// placed, then whatever the fetch's own outcome says about the rest.
    /// <para><paramref name="outcome"/> is a fact about the REQUEST, not about
    /// the result — <c>grid is null</c> cannot tell "asked and empty" from
    /// "not asked" from "asked and it threw", because all three leave it
    /// null.</para></summary>
    public static QuotaHeatmapState HeatmapState(
        QuotaHeatmap? grid, WindowEquivalence.FetchOutcome outcome, bool unreadable, bool windowsEmpty) =>
        grid is { IsEmpty: false } ? QuotaHeatmapState.Grid
        : grid is { HasMovement: true } ? QuotaHeatmapState.Unplaced
        : outcome switch
        {
            // macOS QuotaHeatmapCard.swift:62-95: "could not be read" only when
            // attempted AND unreadable AND no window is listed; any other
            // attempted answer is "no movement".
            WindowEquivalence.FetchOutcome.Succeeded => QuotaHeatmapState.NoMovement,
            WindowEquivalence.FetchOutcome.Failed =>
                unreadable && windowsEmpty ? QuotaHeatmapState.Failed : QuotaHeatmapState.NoMovement,
            _ => QuotaHeatmapState.Loading,
        };

    /// <summary>The strip card's state. Same <paramref name="outcome"/>
    /// contract as the heatmap's: an empty list is not an answer.
    /// <paramref name="unreadable"/> is <see cref="QuotaLensProjection.UnreadableClients"/>
    /// reduced to this card's clients.</summary>
    public static QuotaStripState StripState(
        IReadOnlyList<QuotaWindowSummary> summaries, WindowEquivalence.FetchOutcome outcome, bool unreadable) =>
        summaries.Count > 0 ? QuotaStripState.Rows
        : outcome switch
        {
            // macOS QuotaHistoryStripCard.swift:40-61: "could not be read" only
            // when attempted AND unreadable (a failed fetch of an unrelated
            // client does not make this strip unreadable).
            WindowEquivalence.FetchOutcome.Succeeded => QuotaStripState.NoCompletedWindows,
            WindowEquivalence.FetchOutcome.Failed =>
                unreadable ? QuotaStripState.Failed : QuotaStripState.NoCompletedWindows,
            _ => QuotaStripState.Loading,
        };

    // ---- Strip card ----------------------------------------------------

    public static string StripTitle() => "Past windows".Localized();

    /// <summary>Null unless every window has stayed below the ceiling — the
    /// headline worth putting in a subtitle, and only honest when it holds for
    /// all of them.</summary>
    public static string? StripSubtitle(IReadOnlyList<QuotaWindowSummary> summaries) =>
        summaries.Count > 0 && summaries.All(summary => summary.NeverExhausted)
            ? "never exhausted".Localized()
            : null;

    /// <summary>"Peaked at", not "heaviest": this is the highest READING, while
    /// the bars draw consumption, and the two differ whenever the app started
    /// watching a cycle partway through.</summary>
    public static string Headline(QuotaWindowSummary summary)
    {
        var peak = ((int)Math.Round(summary.PeakPercent, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture);
        return summary.NeverExhausted
            ? "Peaked at {0}% · never ran out".Localized(peak)
            : "Peaked at {0}% · ran out at least once".Localized(peak);
    }

    public static string WindowCount(int cycles) => "{0} windows".Localized(cycles);

    public static string NoCompletedWindows() =>
        "No completed windows recorded yet. They accumulate as TokenBar runs.".Localized();

    /// <summary>The strip is oldest-to-newest, so counting back from the end is
    /// what turns a bar into "how many windows ago".</summary>
    public static string BarAge(int windowsAgo) =>
        windowsAgo == 0
            ? "Most recent window".Localized()
            : "{0} windows ago".Localized(windowsAgo);

    public static string BarConsumed(double usedPercent) =>
        "Consumed {0}% of the allowance".Localized(
            (int)Math.Round(usedPercent, MidpointRounding.AwayFromZero));

    public static string RanOut() => "Ran out".Localized();

    // ---- Heatmap card --------------------------------------------------

    public static string HeatmapTitle() => "When the allowance goes".Localized();

    /// <summary>Distinct local days carrying a reading — the honest denominator
    /// for "is this enough to read a weekly rhythm". Null while there is no grid
    /// to describe.</summary>
    public static string? HeatmapSubtitle(QuotaHeatmap? grid) =>
        grid is { IsEmpty: false } ? "{0} days observed".Localized(grid.ObservedDays) : null;

    /// <summary>Weekday 0 = Monday, matching <see cref="QuotaHeatmapFold"/>'s
    /// cell order.</summary>
    public static IReadOnlyList<string> WeekdayLabels { get; } =
        ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static string UnplacedBody(double unplacedPercent) =>
        ("{0}% consumed, but every pair of readings was too far apart to place on the grid. "
         + "It fills in as sampling gets denser.").Localized(Percent(unplacedPercent));

    /// <summary>Stated, not swallowed: consumption we could not place in time is
    /// consumption the grid is not showing, and a reader comparing it against the
    /// strip card above would otherwise find a gap with no explanation. Below one
    /// point there is no line — that threshold is about whether a line is worth
    /// drawing, not about whether anything happened, which is why
    /// <see cref="QuotaHeatmap.HasMovement"/> uses a different one.</summary>
    public static string? Footnote(QuotaHeatmap grid) =>
        grid.UnplacedPercent >= 1
            ? "{0}% consumed between readings too far apart to place".Localized(
                (int)Math.Round(grid.UnplacedPercent, MidpointRounding.AwayFromZero))
            : null;

    public static string NoMovement() =>
        "No allowance movement recorded yet. It accumulates as TokenBar runs.".Localized();

    public static string Loading() => "Reading quota history…".Localized();

    /// <summary>Shared by the strip and the heatmap — both states resolve to
    /// this on a failed quota-history fetch, and both drew "Reading quota
    /// history…" instead for the two review rounds this was wrong: a scan
    /// that threw is not a scan still running. Same string already shipped
    /// for the window-history card's own failed read
    /// (<see cref="WindowHistoryText"/>), reused rather than authored a
    /// second time for the same fact.</summary>
    public static string Failed() => "Quota history could not be read. It will be retried.".Localized();

    public static string SlotHeader(int weekday, int hour) =>
        $"{WeekdayLabels[weekday].Localized()} {hour:00}:00";

    public static string SlotEmpty() => "No allowance consumed in this slot".Localized();

    /// <summary>Points, not "% of the allowance". A slot accumulates across every
    /// recorded cycle, so with enough history it passes 100 and the percentage
    /// has no denominator left to be a percentage of.</summary>
    public static string SlotSpend(double points) =>
        "{0} allowance-points spent here".Localized(Percent(points));

    /// <summary>One decimal below 10, none above: a 0.4% slot and an idle one
    /// must not both render as "0%".</summary>
    public static string Percent(double value) =>
        value < 10
            ? value.ToString("F1", CultureInfo.InvariantCulture)
            : ((int)Math.Round(value, MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture);
}
