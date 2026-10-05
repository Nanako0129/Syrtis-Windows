namespace TokenBar.Core;

// Port of TokenBarCore/QuotaOverview.swift, comments included: they record the
// defect each rule exists to prevent.
//
// Deliberately NOT ported: the equivalence line (`WindowEquivalence`,
// `UsageAttribution`, `ModelScope`), which has no Windows source at all. The
// cards fed from here render without it rather than with a blank row.

/// <summary>
/// One window, as the quota store keys it.
/// <para>
/// The store's own triple — <c>(providerId, accountScope, windowKey)</c>, see
/// <see cref="Interop.QuotaHistorySeries"/> — and not the
/// <c>(clientId, windowKey)</c> pair the UI could get away with today. "Windows
/// has no multi-account surface" is a fact about the UI, not about the store's
/// primary key: the real store already holds scopes like
/// <c>claude|7M08…|session.v1</c>, and two series landing on one live window
/// stay separate by contract. Collapsing the scope out would put two
/// indistinguishable rows on the strip and let one window's grid overwrite
/// another's in any dictionary keyed by this.
/// </para>
/// <para>
/// macOS's <c>cardId</c> has no Windows source and is dropped;
/// <see cref="WindowKey"/> plays the same role in the export's key.
/// </para>
/// </summary>
public sealed record QuotaWindowIdentity(string ProviderId, string AccountScope, string WindowKey);

/// <summary>
/// What the recorded cycles say about a window, without needing a message scan.
/// <para>
/// Everything here comes from the persisted quota curve alone — a ~2ms read —
/// so the all-subscription lens can carry it.
/// </para>
/// </summary>
/// <param name="Id">See <see cref="QuotaWindowIdentity"/>. Both the key of a
/// rendered list and the lookup into the grids the dashboard stores per
/// window.</param>
/// <param name="WindowLabel">The live window's display label, or null when the
/// join found no live window. Kept nullable rather than defaulted at fold time:
/// the fallback belongs to <see cref="QuotaLabels"/>, which is where the "never
/// a trailing separator" rule is stated and asserted.</param>
/// <param name="Recent">Consumption of each recorded cycle, oldest first. Drawn
/// as a strip, so the reader compares the latest bar against the ones before it
/// rather than against a number.</param>
/// <param name="RecentPeaks">The highest absolute reading of each cycle in
/// <paramref name="Recent"/>, same order. The bars draw consumption; only this
/// can say whether a cycle ran out.</param>
/// <param name="PeakPercent">The heaviest cycle ever recorded for this
/// window.</param>
/// <param name="NeverExhausted">True when no recorded cycle reached the
/// ceiling. Stated positively because on real data it is the answer, and "you
/// have never run out" is worth more than a gauge that is 90% full of
/// nothing.</param>
public sealed record QuotaWindowSummary(
    QuotaWindowIdentity Id,
    string? WindowLabel,
    IReadOnlyList<double> Recent,
    IReadOnlyList<double> RecentPeaks,
    double PeakPercent,
    bool NeverExhausted,
    int CycleCount);

/// <summary>
/// A window the heatmap can draw, whether or not it has completed a cycle yet.
/// <para>
/// Deliberately not <see cref="QuotaWindowSummary"/>: that one exists only for
/// windows with recorded HISTORY, and the heatmap is built from the raw curve. A
/// window whose only movement is in the cycle currently running has a grid and
/// no summary, so keying the picker on summaries made that grid unreachable and
/// the card announced "no allowance movement recorded yet" until the first cycle
/// completed — days, on a weekly window.
/// </para>
/// </summary>
/// <param name="Total">Everything the grid accounts for, used to order the
/// picker so the window worth looking at leads.</param>
public sealed record QuotaHeatmapWindow(
    QuotaWindowIdentity Id,
    string? WindowLabel,
    double Total);

public static class QuotaOverviewFold
{
    /// <summary>A cycle at or above this counts as having reached the ceiling.
    /// Not 100: providers report whole percents and stop updating once the
    /// allowance is spent, so an exhausted window is observed at 99 or 100
    /// depending on when the last sample landed.</summary>
    public const double ExhaustedPercent = 99;

    /// <summary>The tab's slice of the all-clients strip (macOS QuotaView.swift
    /// :85, <c>windowSummaries.filter { clientIds.contains($0.clientId) }</c>):
    /// a client tab with no window card draws the strip for its own clients
    /// only.</summary>
    public static IReadOnlyList<QuotaWindowSummary> ForClients(
        IReadOnlyList<QuotaWindowSummary> summaries, IReadOnlyList<string> clientIds) =>
        [.. summaries.Where(s => clientIds.Contains(s.Id.ProviderId))];

    /// <summary>The same slice of the heatmap's windows (QuotaView.swift :89).</summary>
    public static IReadOnlyList<QuotaHeatmapWindow> ForClients(
        IReadOnlyList<QuotaHeatmapWindow> windows, IReadOnlyList<string> clientIds) =>
        [.. windows.Where(w => clientIds.Contains(w.Id.ProviderId))];

    /// <summary>How many cycles a strip shows. Enough to read a rhythm at
    /// popover width without each bar becoming a hairline.</summary>
    public const int StripLength = 16;

    /// <summary>
    /// One summary per window that has at least one recorded cycle. Windows with
    /// no history are omitted rather than shown empty: the Agent-limits card
    /// already states their current position, and a row saying "no history yet"
    /// repeated across every fresh install is noise.
    /// </summary>
    public static IReadOnlyList<QuotaWindowSummary> Summaries(
        IEnumerable<(QuotaWindowIdentity Id, string? Label, IReadOnlyList<QuotaCycle> Cycles)> windows)
    {
        var summaries = new List<QuotaWindowSummary>();
        foreach (var window in windows)
        {
            if (window.Cycles.Count == 0)
            {
                continue;
            }

            // `Cycles` arrives newest first from `QuotaHistoryFold`; a strip
            // reads left to right in time. Take THEN reverse: reversing first
            // would show the oldest 16 cycles instead of the newest 16, and a
            // strip of plausible-looking bars is how that goes unnoticed.
            var ordered = window.Cycles.Take(StripLength).Reverse().ToList();
            // Consumption for the strip, absolute reading for the ceiling.
            // `UsedPercent` is a span, so a cycle first observed at 40% and last
            // at 100% has a span of 60 and would have been called quiet.
            //
            // Peak is over ALL cycles, not only the ones the strip shows: a
            // window that ran out thirty-three cycles ago did run out.
            var peak = window.Cycles.Max(cycle => cycle.PeakUsedPercent);
            summaries.Add(new QuotaWindowSummary(
                Id: window.Id,
                WindowLabel: window.Label,
                Recent: ordered.Select(cycle => cycle.UsedPercent).ToList(),
                RecentPeaks: ordered.Select(cycle => cycle.PeakUsedPercent).ToList(),
                PeakPercent: peak,
                NeverExhausted: peak < ExhaustedPercent,
                CycleCount: window.Cycles.Count));
        }

        // Heaviest peak first: the window most worth looking at leads.
        return summaries.OrderByDescending(summary => summary.PeakPercent).ToList();
    }

    /// <summary>
    /// Every window the picker may offer, heaviest first.
    /// <para>
    /// Inclusion is <see cref="QuotaHeatmap.HasMovement"/>, NOT
    /// <c>Total &gt; 0</c>. <c>Total</c> is zero when every reading pair
    /// straddles more than <see cref="QuotaHeatmapFold.MaximumGapSeconds"/>, yet
    /// the allowance still moved; dropping such a window made the card report
    /// "nothing recorded yet" and put the one line that explains it out of
    /// reach behind the same condition.
    /// </para>
    /// <para>
    /// A fold rather than a filter inside the card, because both rules here are
    /// defects macOS has already paid for once and
    /// <c>DashboardView.xaml.cs</c> is compiled by no test project — asserted
    /// only through XAML means not asserted.
    /// </para>
    /// </summary>
    public static IReadOnlyList<QuotaHeatmapWindow> HeatmapWindows(
        IEnumerable<(QuotaWindowIdentity Id, string? Label, QuotaHeatmap Grid)> windows) =>
        windows
            .Where(window => window.Grid.HasMovement)
            .Select(window => new QuotaHeatmapWindow(window.Id, window.Label, window.Grid.Total))
            .OrderByDescending(window => window.Total)
            .ToList();
}
