using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Which of the window-history card's states applies.</summary>
public enum WindowHistoryState
{
    /// <summary>The persisted-curve read has not settled. On every cold start
    /// an empty list means "still asking", not "nothing recorded" — without
    /// this the card announced no history while the card above it was still
    /// showing its own spinner.</summary>
    Loading,

    NoHistory,

    /// <summary>The quota-history read was attempted and threw — distinct
    /// from <see cref="NoHistory"/>, which claims the read landed and
    /// genuinely found no earlier windows. Round 8's finding: a failed read
    /// used to collapse into <see cref="NoHistory"/> because <c>State</c>
    /// took only a <c>bool attempted</c>, which cannot carry this
    /// distinction.</summary>
    Failed,

    Rows,
}

/// <summary>One earlier window, as the card draws it.</summary>
/// <param name="QuotaFraction">The quota bar's fill, 0…1 on a FIXED 0…100
/// scale. Rescaling to the largest row would make a 3% window and a 58% one
/// look alike, and comparing cycles to each other and to the ceiling is what
/// the bar is for.</param>
/// <param name="UsageFraction">The usage bar's fill, relative to the heaviest
/// window ON SCREEN. Zero is a real answer: this window consumed allowance and
/// nothing in it was declared as this subscription's.</param>
/// <param name="ThinObservation">The app was running for less than half of
/// this window, so its consumption figure is a floor rather than a
/// measurement. Shown, but marked.</param>
public sealed record WindowHistoryRow(
    long ResetAtMs,
    string Stamp,
    string Range,
    double UsedPercent,
    double QuotaFraction,
    long Tokens,
    double Cost,
    double UsageFraction,
    double ObservedFraction,
    bool ThinObservation);

/// <summary>
/// The 時間窗歷史 card's rows and copy (port of <c>QuotaHistoryCard.swift</c>;
/// the WinUI layout lives in <c>DashboardView.Quota.cs</c>).
/// <para>
/// Pulled into <c>TokenBar.Core.Tests</c> via &lt;Compile Include&gt; for the
/// same reason as its neighbours: the row fold's scale rule and the disclaimer
/// would otherwise sit in a file no test project compiles.
/// </para>
/// </summary>
public static class WindowHistoryText
{
    /// <summary>
    /// How many rows the card opens with, and how many each press of the
    /// footer button adds. The ceiling is not here:
    /// <see cref="QuotaLensProjection"/> hands this file a cycle list already
    /// capped at <see cref="QuotaHistoryFold.ConsideredCycles"/>, so pressing
    /// until the button disappears shows everything the fold admits and
    /// reaching further is an engine change, not a display one.
    /// <para>
    /// Growing the list rather than paging it: the two aggregates on this card
    /// — the ≈ line (<see cref="Equivalence"/>) and the usage bar's scale
    /// (<see cref="Rows"/>) — are computed over the rows on screen, so a pager
    /// would hand the same history a different ratio on every page. The whole
    /// flyout also already sits in one vertical scroller, which a second
    /// same-axis scroller inside the card would fight for the wheel.
    /// </para>
    /// <para>
    /// <see cref="QuotaHistoryFold.ConsideredCycles"/> has to stay at or above
    /// this, which a test asserts: a cap below it would draw fewer rows than
    /// this card intends with nothing saying so.
    /// </para>
    /// </summary>
    public const int VisibleRows = 12;

    /// <summary>
    /// How many admitted cycles are not on screen. Zero means the grow control
    /// is not drawn at all, which is also how the card says there is no more
    /// history rather than leaving a control that would do nothing.
    /// <para>
    /// The count that is LEFT, not the count the next press adds: that is the
    /// phrasing this app's other grown list already uses (the Hourly lens's
    /// timeline, <c>"Show more ({0} left)"</c>), and a second wording for the
    /// same gesture would be the only thing distinguishing the two cards.
    /// macOS names the step instead and has to clamp it so a "Show 0 more"
    /// cannot appear; naming the remainder makes that case unreachable.
    /// </para>
    /// </summary>
    public static int Remaining(int total, int shown) => Math.Max(0, total - shown);

    /// <summary>
    /// The count one press produces, from the number of rows currently DRAWN.
    /// <para>
    /// Taking the drawn count rather than the stored one is the whole content
    /// of this function, and it is not interchangeable: after a window loses
    /// cycles, <see cref="Rows"/> clamps and the stored count stays above what
    /// is on screen, so stepping from the stored value would take several
    /// presses to move a single row. Stated here rather than inline in the
    /// click handler because <c>DashboardView.Quota.cs</c> is WinUI and no test
    /// project compiles it — the arithmetic is the part a test can hold, and
    /// what is left in the handler is wiring.
    /// </para>
    /// </summary>
    public static int Grown(int drawnCount) => drawnCount + VisibleRows;

    /// <summary>Below this the cycle was barely witnessed and its consumption
    /// figure is not evidence about the window — the app simply was not running
    /// for most of it.</summary>
    public const double ThinObservation = 0.5;

    public static WindowHistoryState State(
        IReadOnlyList<WindowHistoryRow> rows, WindowEquivalence.FetchOutcome outcome) =>
        rows.Count > 0 ? WindowHistoryState.Rows
            : outcome switch
            {
                WindowEquivalence.FetchOutcome.NotAttempted => WindowHistoryState.Loading,
                WindowEquivalence.FetchOutcome.Failed => WindowHistoryState.Failed,
                _ => WindowHistoryState.NoHistory,
            };

    /// <summary>
    /// The visible rows, newest first.
    /// <para>
    /// <paramref name="spans"/> is <see cref="QuotaEquivalenceFold.Cycles"/>'s
    /// output for the same list and in the same order, which is where the
    /// attributed tokens and cost per cycle already live; asking a second fold
    /// for them would be a second answer free to disagree with the ≈ line the
    /// strip card prints from the first.
    /// </para>
    /// <para>
    /// The usage bar's scale is taken over the rows ACTUALLY SHOWN. macOS
    /// states the same rule and records what breaking it cost: a hidden older
    /// cycle with the largest total set the scale and made every visible bar
    /// short, which is precisely the comparison the bar claims to be making.
    /// Pressing Show more therefore moves both this scale and the ≈ line, and
    /// that is the intended reading: they describe what is on screen. It is
    /// also why <paramref name="shownCount"/> only ever grows — a control that
    /// could shrink it would make the ≈ line oscillate between two answers for
    /// one history.
    /// </para>
    /// </summary>
    /// <param name="shownCount">How many rows the reader has grown the list
    /// to. Defaults to the card's opening state, which is what every caller
    /// but the projection wants; clamped at both ends here rather than at the
    /// call site, because a window that lost cycles between two renders
    /// carries a count larger than its own history.</param>
    public static IReadOnlyList<WindowHistoryRow> Rows(
        IReadOnlyList<QuotaCycle> cycles,
        IReadOnlyList<WindowEquivalence.Cycle> spans,
        int shownCount = VisibleRows)
    {
        var shown = Math.Min(cycles.Count, Math.Max(VisibleRows, shownCount));
        var peak = 0L;
        for (var i = 0; i < shown && i < spans.Count; i++)
        {
            peak = Math.Max(peak, spans[i].SpanTokens);
        }

        var rows = new List<WindowHistoryRow>(shown);
        for (var i = 0; i < shown; i++)
        {
            var cycle = cycles[i];
            var span = i < spans.Count ? spans[i] : null;
            var tokens = span?.SpanTokens ?? 0;
            rows.Add(new WindowHistoryRow(
                ResetAtMs: cycle.ResetAtMs,
                Stamp: Stamp(cycle.StartMs),
                Range: WindowCardText.ClockRange(cycle.StartMs, cycle.ResetAtMs),
                UsedPercent: cycle.UsedPercent,
                QuotaFraction: Math.Min(1, cycle.UsedPercent / 100),
                Tokens: tokens,
                Cost: span?.SpanCost ?? 0,
                UsageFraction: peak > 0 ? Math.Min(1, (double)tokens / peak) : 0,
                ObservedFraction: cycle.ObservedFraction,
                ThinObservation: cycle.ObservedFraction < ThinObservation));
        }

        return rows;
    }

    /// <summary><c>MM-DD HH:mm</c> in the viewer's own zone: the window's start,
    /// which is what identifies a row.</summary>
    public static string Stamp(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(
            "MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public static string Title() => "Window history".Localized();

    /// <summary>Counts the rows ON SCREEN, not the cycles retained: the
    /// subtitle names what the reader can see, and grows with each press.</summary>
    public static string? Subtitle(IReadOnlyList<WindowHistoryRow> rows) =>
        rows.Count == 0 ? null : "{0} windows".Localized(rows.Count);

    /// <summary>The grow control's label, shared verbatim with the Hourly
    /// lens's timeline so the two grown lists read as the same gesture.</summary>
    public static string ShowMore(int remaining) => "Show more ({0} left)".Localized(remaining);

    public static string EmptyBody(WindowHistoryState state) => state switch
    {
        WindowHistoryState.Loading => "Reading quota history…".Localized(),
        WindowHistoryState.Failed => "Quota history could not be read. It will be retried.".Localized(),
        _ => "No earlier windows recorded yet. They accumulate as TokenBar runs.".Localized(),
    };

    public static string? ThinObservationNote(WindowHistoryRow row) =>
        row.ThinObservation
            ? "TokenBar observed {0}% of this window, so its usage figure is a floor.".Localized(
                (int)Math.Round(row.ObservedFraction * 100, MidpointRounding.AwayFromZero))
            : null;

    /// <summary>The line that keeps the money column honest: these are API
    /// list-price equivalents for usage the user themselves declared as this
    /// subscription's, not what the subscription charged. Not noise — without
    /// it the column reads as a bill.</summary>
    /// <param name="owner">The quota OWNER (<c>ClientRegistry.QuotaOwner</c>),
    /// not the raw tab id: antigravity-cli's rows are folded against
    /// <c>client.Owner == "antigravity"</c> and confirmed against that same
    /// subscription, so the disclaimer must name the subscription actually
    /// summarized — "Antigravity" — rather than the tab's own display name
    /// ("Antigravity CLI"), which the raw id would have produced.</param>
    public static string Disclaimer(string owner) =>
        "Amounts are API list-price equivalents for usage you declared as {0} — not what the subscription charged."
            .Localized(ClientRegistry.Style(owner).DisplayName);

    /// <summary>The row's own numbers, formatted the way the rest of the app
    /// already does.</summary>
    public static string Tokens(WindowHistoryRow row) => Format.Tokens(row.Tokens, row.Cost);

    public static string Cost(WindowHistoryRow row) => Format.Money(row.Tokens, row.Cost);

    public static string Percent(WindowHistoryRow row) =>
        ((int)Math.Round(row.UsedPercent, MidpointRounding.AwayFromZero))
            .ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";

    // ---- the expanded row (PARITY-3b's deferred half, now unblocked by 5a,
    // 5d-1 and 5d-2) --------------------------------------------------------

    /// <summary>One coloured segment of the collapsed row's usage bar — this
    /// subscription's models, proportioned by tokens. Empty when nothing has
    /// been attributed to this cycle at all.</summary>
    public readonly record struct WindowHistorySegment(string Color, double Fraction);

    /// <summary>The usage bar's segments, coloured from the app's shared
    /// palette — the same one the model breakdown and the usage chart use, so
    /// a model is not one colour on this card and another everywhere else —
    /// in the same order <see cref="QuotaHistoryRow.Models"/> lists them, so
    /// a reader who opens a row finds the segments in the order they just
    /// read about.</summary>
    public static IReadOnlyList<WindowHistorySegment> Segments(QuotaHistoryRow row, ModelColorMap colors) =>
        row.MineTokens <= 0
            ? []
            : [.. row.Models.Select(model => new WindowHistorySegment(
                colors.Color(model.ProviderId, model.ModelId), (double)model.Tokens / row.MineTokens))];

    /// <summary>The expander's model rows: the heaviest four, in the same
    /// order the usage bar segments them.</summary>
    public static IReadOnlyList<QuotaHistoryModel> TopModels(QuotaHistoryRow row) => [.. row.Models.Take(4)];

    /// <summary>A model row's own token figure. Dash, not "0", when the
    /// metric itself is absent — the mirror of <see cref="ModelCost"/>: a
    /// model attributed by cost alone carries no token count, and printing
    /// "0" states a measurement nobody took. Shares <see cref="Format.Tokens"/>
    /// with the collapsed row above it (<see cref="Tokens"/>) rather than its
    /// own ad hoc rule, so the two never disagree about the same missing
    /// metric.</summary>
    public static string ModelTokens(QuotaHistoryModel model) => Format.Tokens(model.Tokens, model.Cost);

    /// <summary>The mirror of <see cref="ModelTokens"/>: dash when this model
    /// carried no price.</summary>
    public static string ModelCost(QuotaHistoryModel model) => Format.Money(model.Tokens, model.Cost);

    /// <summary>Shown in place of the model rows when every message this
    /// window's messages resolved out of this subscription — declared
    /// elsewhere, excluded, or never classified — leaving nothing charged to
    /// it. Distinct from an empty list with no explanation: an expanded row
    /// with nothing under it otherwise reads as a card that gave up mid-scan.</summary>
    public static string NothingChargedNote() =>
        "Nothing in this window was charged to this subscription.".Localized();

    /// <summary>
    /// The line that explains a flat quota bar: everything else recorded in
    /// the same hours, named by which of three attribution states it actually
    /// holds — declared elsewhere, declared excluded, or never classified.
    /// Null when there is nothing to report.
    /// <para>
    /// Five variants, not one flat "other subscriptions": that phrase is a
    /// claim the user only made about the first state. Reporting an explicit
    /// exclusion back to the user as "unclassified" would tell them their own
    /// decision was an outstanding question, so excluded and unclassified are
    /// kept apart, and each combination that can actually occur gets its own
    /// sentence.
    /// </para>
    /// </summary>
    public static string? SameHoursLine(QuotaHistoryRow row)
    {
        // "Recorded" means either kind of evidence, the same rule the row's
        // own money column uses: a provider entry can carry a cost with no
        // token components.
        if (row.OtherTokens <= 0 && row.OtherCost <= 0)
        {
            return null;
        }

        // One-value phrasing whenever one side is absent, in EITHER
        // direction — the mirror of the model rows' own dash rule, stated
        // here as a single combined value rather than two dashed fields
        // because this line is prose, not a table column.
        var value = row.OtherTokens > 0 && row.OtherCost > 0
            ? Format.CompactTokens(row.OtherTokens) + " · " + Format.UsdOrBelowCent(row.OtherCost)
            : row.OtherTokens > 0 ? Format.CompactTokens(row.OtherTokens) : Format.UsdOrBelowCent(row.OtherCost);

        if (row.OtherHasUnattributed)
        {
            return row.OtherHasAssigned || row.OtherHasExcluded
                ? "Other and unclassified usage in the same hours: {0}".Localized(value)
                : "Unclassified usage in the same hours: {0}".Localized(value);
        }

        if (row.OtherHasExcluded)
        {
            return row.OtherHasAssigned
                ? "Other and excluded usage in the same hours: {0}".Localized(value)
                : "Excluded usage in the same hours: {0}".Localized(value);
        }

        return "Other subscriptions in the same hours: {0}".Localized(value);
    }

    /// <summary>
    /// Above the rows: what a tenth of this window's allowance has been worth,
    /// pooled across every cycle ON SCREEN rather than read off one — a single
    /// row's ratio is dominated by the 1-point reading quantisation, and the
    /// whole reason to pool is that the individual figures cannot be trusted.
    /// <paramref name="shown"/> must already be restricted to the rows the
    /// card actually draws: a hidden older cycle folded in here would move a
    /// number nobody can see the evidence for.
    /// </summary>
    public static WindowEquivalence.Row Equivalence(IReadOnlyList<QuotaHistoryRow> shown, bool declared) =>
        WindowEquivalence.Aggregate(
            declared,
            [.. shown.Select(row => new WindowEquivalence.Cycle(
                row.Cycle.UsedPercent, row.SpanTokens, row.SpanCost, row.Cycle.ObservedFraction,
                row.Cycle.RisingRuns))]);
}
