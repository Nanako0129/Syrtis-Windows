using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// The Quota lens's seven independent snapshot-assembly sites, folded into
/// one place. Every state choice still belongs to <see cref="QuotaLensData"/>,
/// <see cref="QuotaEquivalenceFold"/>, <see cref="WindowCardText"/>,
/// <see cref="WindowHistoryText"/> and <see cref="SubscriptionTrendText"/> —
/// this file does not re-decide anything they already decide. What it owns is
/// the step before them: turning the snapshot's raw parts into the exact
/// arguments those functions require, done once instead of at each of the
/// seven call sites <c>DashboardView.Quota.cs</c> used to do it at.
/// <para>
/// Takes Core/Interop types only, never <c>DashboardModel.Snapshot</c> —
/// that record is nested in a file that opens with
/// <c>using Microsoft.UI.Dispatching;</c>, so a method that took it could not
/// be compiled by <c>TokenBar.Core.Tests</c>, which is the entire point of
/// this file existing. The view still does one small unpack from the
/// snapshot into these parameters (three reads: <c>Confirmed</c>,
/// <c>_model?.Year</c>, and the fields the parameter list below names), and
/// makes no decision while doing it.
/// </para>
/// <para>
/// This does NOT make it impossible for a future card to read the snapshot's
/// raw parts directly instead of calling here — <c>DashboardView</c> is a
/// <c>sealed partial class</c> and <c>_snapshot</c> stays reachable from any
/// partial file added to it. The justification is testability alone: three of
/// seven review rounds on this lens found defects inside a file no test
/// project compiled, and the decisions in it move here, to one that does. A
/// bypass stays a review question, not a compiler error.
/// </para>
/// </summary>
public static class QuotaLensProjection
{
    /// <summary>
    /// The view's own persisted selection, read here and never written —
    /// <c>_activeClientTab</c> and <c>_windowCardTab</c> keep living as the
    /// view's fields and keep persisting through <c>AppSettings.Store</c>
    /// exactly as they do today. Only the two that decide WHICH data this
    /// lens computes are read here; the display-only toggles
    /// (<c>_trendMetric</c>, <c>_heatmapWindow</c>, <c>_windowMetric</c>,
    /// <c>_historyExpanded</c>) choose how already-decided data is drawn and
    /// stay entirely on the view's side of this call.
    /// </summary>
    /// <param name="HistoryShownWindow">Which window
    /// <paramref name="HistoryShownCount"/> was grown against, as
    /// <see cref="WindowId"/> formats it, or null before the reader has
    /// pressed anything. The count is honoured only when this still names the
    /// window <see cref="BuildHistory"/> resolves, so it cannot survive into a
    /// different history. The alternative — resetting on the view's side when
    /// the tab is clicked — answers only one of the two ways the resolved
    /// window moves: a stored preference belonging to another client leaves
    /// this one's window alone, and a window vanishing from the payload moves
    /// it with no click at all.</param>
    /// <param name="HistoryShownCount">How many history rows the reader has
    /// grown the card to. Not a display-only toggle despite looking like one:
    /// it decides which cycles are folded, and the ≈ line and the usage bar's
    /// scale are both computed over exactly those — which is why it is read
    /// here and not left on the view's side with
    /// <c>_historyExpanded</c>.</param>
    public readonly record struct Selection(
        string ActiveClientTab,
        string WindowCardTab,
        string? HistoryShownWindow = null,
        int HistoryShownCount = WindowHistoryText.VisibleRows);

    /// <summary>Everything the Quota lens's seven sites decided, assembled
    /// once. <see cref="Client"/> is null exactly when <see cref="Selection.ActiveClientTab"/>
    /// is <see cref="ClientRegistry.OverviewTab"/> — the same branch
    /// <c>BuildQuota</c> already makes before deciding which cards to
    /// build.</summary>
    public sealed record Model(Overview Overview, SubscriptionTrend Trend, bool TrendPastYearSelected, Client? Client);

    /// <summary>Sites 1 (the strip/heatmap equivalence fold) and, by way of
    /// <see cref="QuotaLensData.Build"/>, the summaries/windows/grids the two
    /// all-clients cards draw from.</summary>
    public sealed record Overview(
        IReadOnlyList<QuotaWindowSummary> Summaries,
        IReadOnlyList<QuotaHeatmapWindow> Windows,
        IReadOnlyDictionary<QuotaWindowIdentity, QuotaHeatmap> Grids,
        WindowEquivalence.FetchOutcome Outcome,
        IReadOnlyDictionary<QuotaWindowIdentity, WindowEquivalence.Row> Equivalences);

    /// <summary>Sites 2, 4, 5 and 6 — the per-client lens.</summary>
    public sealed record Client(
        string Owner,
        IReadOnlyList<WindowCardTab> Tabs,
        WindowCardTab? Selected,
        IReadOnlyList<WindowMessage> Messages,
        IReadOnlyList<WindowMessage> Mine,
        WindowEquivalence.Row? LiveEquivalence,
        int UndatedCount,
        WindowHistory History,
        // Round 9's second finding: DashboardView.Quota.cs read
        // snapshot.QuotaHistoryOutcome directly at two call sites (the window
        // card and the history card) instead of through this record — because
        // this record had nowhere to put it. It is the same fetch's outcome
        // the overview's own Outcome field above carries; a client lens needs
        // its own copy because the two are built from different snapshot
        // reads (BuildOverview vs BuildClient) and neither may read the
        // other's field.
        WindowEquivalence.FetchOutcome QuotaHistoryOutcome);

    /// <summary>Site 6 on its own: the window-history card's rows and its
    /// pooled ≈ line.</summary>
    /// <param name="Remaining">How many admitted cycles are not on screen,
    /// zero when every one of them is already drawn. Folded here rather than
    /// recomputed by the view, so the button and the rows beside it cannot
    /// disagree about how much history is left.</param>
    /// <param name="ShownWindow">The window <see cref="DisplayRows"/> was
    /// resolved from, as <see cref="WindowId"/> formats it — what the view
    /// stores alongside its own count so the next render can tell whether the
    /// count still belongs to this history. Null when no window resolved, in
    /// which case there are no rows to grow either.</param>
    public sealed record WindowHistory(
        IReadOnlyList<QuotaCycle> Cycles,
        IReadOnlyList<QuotaHistoryRow> Rows,
        IReadOnlyDictionary<long, QuotaHistoryRow> ByResetAt,
        IReadOnlyList<WindowHistoryRow> DisplayRows,
        WindowEquivalence.Row Equivalence,
        int Remaining,
        string? ShownWindow);

    /// <summary>
    /// <paramref name="windowUsageOutcome"/> is the ONE fact every EQUIVALENCE
    /// gate below reads — never <paramref name="quotaHistoryOutcome"/>, and
    /// never <c>windowUsage is null</c>. Round 7's first finding was three
    /// readings of what should be one fact: the overview path (site 1) used
    /// to fold equivalences behind a plain <c>WindowUsageAttempted</c> bool
    /// while the live card and the history card (sites 4 and 6) already
    /// switched on the outcome enum — so a fetch that had ATTEMPTED and
    /// FAILED still passed the boolean gate at site 1 and computed
    /// equivalences from whatever <paramref name="windowUsage"/> happened to
    /// be retaining, which is exactly the data a failed fetch must not be
    /// trusted to have refreshed (round 7's second finding, fixed upstream in
    /// <c>DashboardModel.Snapshot.WindowUsageOutcome</c> — see that
    /// property's own doc comment for why a failed pass that retains stale
    /// data is not the same fact as <c>WindowUsage is not null</c>). Passed in
    /// as its own parameter for the same reason: this file cannot compute the
    /// distinction itself without the retained-vs-fresh signal
    /// <c>DashboardModel</c> alone has.
    /// <para>
    /// <paramref name="quotaHistoryOutcome"/> is a SEPARATE fetch's outcome
    /// (the store lane, not the window-usage lane) and gates a separate
    /// thing: whether the strip/heatmap/window/history cards themselves have
    /// anything to draw at all (<see cref="QuotaLensText.HeatmapState"/>,
    /// <see cref="QuotaLensText.StripState"/>, <see cref="WindowCardText.State"/>,
    /// <see cref="WindowHistoryText.State"/>), independently of whether the
    /// equivalence LINE those cards additionally print is available. Until
    /// round 9 this parameter was a plain <c>bool quotaHistoryAttempted</c> —
    /// asked-vs-not, with no room for "asked and it threw" — so a failed
    /// history read rendered identically to a first cold-start read still in
    /// flight on every card gated by it. It is now the same three-value
    /// outcome as <paramref name="windowUsageOutcome"/>, for the same reason.
    /// </para>
    /// <para>
    /// A known asymmetry, left as-is deliberately: below, the equivalence
    /// fold this file builds for the Overview strip/heatmap collapses
    /// <paramref name="windowUsageOutcome"/> to a two-way check
    /// (<c>== Succeeded</c>), so <c>Failed</c> and <c>NotAttempted</c> both
    /// produce an empty equivalences dictionary and the overview strip and
    /// heatmap omit their <c>≈</c> line for both alike — they cannot tell a
    /// broken read from one still in flight. The client lens's history card,
    /// reading the very same fact, does distinguish them
    /// (<c>Row.ScanFailed()</c> vs <c>Row.Loading()</c> in
    /// <see cref="BuildHistory"/> below). Changing what the overview renders
    /// is a product decision, not a defect to fix on sight — noted here so
    /// the next reader finds it recorded rather than rediscovers it.
    /// </para>
    /// </summary>
    public static Model Build(
        IReadOnlyList<QuotaHistorySeries>? history,
        AgentUsagePayload? quota,
        UsagePayload graph,
        Interop.WindowUsage? windowUsage,
        WindowEquivalence.FetchOutcome windowUsageOutcome,
        WindowEquivalence.FetchOutcome quotaHistoryOutcome,
        UsageAttribution.Table confirmed,
        string? year,
        Selection selection)
    {
        var overview = BuildOverview(history, quota, windowUsage, windowUsageOutcome, quotaHistoryOutcome, confirmed);
        var (trend, pastYearSelected) = BuildTrend(graph, confirmed, year);
        var client = selection.ActiveClientTab == ClientRegistry.OverviewTab
            ? null
            : BuildClient(
                selection.ActiveClientTab, selection.WindowCardTab,
                history, quota, windowUsage, windowUsageOutcome, quotaHistoryOutcome, confirmed,
                selection);
        return new Model(overview, trend, pastYearSelected, client);
    }

    private static Overview BuildOverview(
        IReadOnlyList<QuotaHistorySeries>? history,
        AgentUsagePayload? quota,
        Interop.WindowUsage? windowUsage,
        WindowEquivalence.FetchOutcome windowUsageOutcome,
        WindowEquivalence.FetchOutcome quotaHistoryOutcome,
        UsageAttribution.Table confirmed)
    {
        var (summaries, windows, grids) = QuotaLensData.Build(history, quota);
        // Absent (not merely empty) unless the fetch actually SUCCEEDED — not
        // "was attempted", which a failed pass also satisfies while retaining
        // stale (or no) messages. The strip/heatmap draw no line for a window
        // with no key rather than computing one from a read that did not
        // land; see this method's own doc comment on `windowUsageOutcome`.
        var equivalences = windowUsageOutcome == WindowEquivalence.FetchOutcome.Succeeded
            ? QuotaEquivalenceFold.Build(history ?? [], windowUsage?.Messages ?? [], confirmed)
            : new Dictionary<QuotaWindowIdentity, WindowEquivalence.Row>();
        return new Overview(summaries, windows, grids, quotaHistoryOutcome, equivalences);
    }

    /// <summary>
    /// <see cref="SubscriptionTrend"/>'s window is always the most recent
    /// <see cref="SubscriptionTrendText.Window"/> calendar days ending today,
    /// unconditionally of the dashboard's year filter — but the year filter
    /// still bounds which years <paramref name="graph"/>'s own
    /// <c>Contributions</c> hold, so a selected year whose data does not
    /// cover the WHOLE window leaves some of those days with nothing to show.
    /// <para>
    /// Round 7's fourth finding: the prior check compared the selected year
    /// to today's CALENDAR year, which misses the case a January 1-13
    /// selection of the CURRENT year hits — the window still reaches back
    /// into December of the year before, which <paramref name="graph"/> does
    /// not hold either, and those columns render empty while the card claims
    /// no usage. Comparing the window's own earliest date's year to the
    /// selection instead catches both: a genuinely past year (the window
    /// never overlaps it at all) and a current-year selection whose window
    /// crosses backward over the boundary (the window's first date's year is
    /// the earlier one). Range arithmetic, not a rule that was applied at
    /// some call sites and missed at others — this is the seventh site, and
    /// the fix belongs here with the rest for that reason alone.
    /// </para>
    /// </summary>
    private static (SubscriptionTrend Trend, bool PastYearSelected) BuildTrend(
        UsagePayload graph, UsageAttribution.Table confirmed, string? year)
    {
        var today = Format.TodayKey();
        var trend = SubscriptionTrendFold.Build(
            AttributedDailySeries.Points(graph.Contributions, confirmed.Records),
            today,
            SubscriptionTrendText.Window);

        return (trend, PastYearSelected(today, year));
    }

    /// <summary>
    /// Pulled out of <see cref="BuildTrend"/> so the range arithmetic can be
    /// asserted against a fixed <paramref name="today"/> — <see cref="Build"/>'s
    /// own signature has no clock parameter (the design deliberately keeps it
    /// to Core/Interop data plus the selection), so this is the seam a test
    /// reaches instead of depending on which day it happens to run.
    /// </summary>
    internal static bool PastYearSelected(string today, string? year)
    {
        var range = SubscriptionTrendFold.CalendarRange(today, SubscriptionTrendText.Window);
        return year is not null && range is not null && range[0][..4] != year;
    }

    private static Client BuildClient(
        string clientId,
        string windowCardTab,
        IReadOnlyList<QuotaHistorySeries>? history,
        AgentUsagePayload? quota,
        Interop.WindowUsage? windowUsage,
        WindowEquivalence.FetchOutcome windowUsageOutcome,
        WindowEquivalence.FetchOutcome quotaHistoryOutcome,
        UsageAttribution.Table confirmed,
        Selection selection)
    {
        // Every subscription-facing lookup below is keyed by the quota OWNER,
        // not the raw client id — antigravity-cli spends the antigravity
        // subscription.
        var owner = ClientRegistry.QuotaOwner(clientId);
        var tabs = WindowCardText.Tabs(history, quota, owner);
        var selected = tabs.FirstOrDefault(tab => WindowId(tab.Id) == windowCardTab)
            ?? tabs.FirstOrDefault();
        var messages = windowUsage?.Messages ?? [];
        var mine = WindowCardText.Mine(messages, owner, confirmed.Records);

        // Only when the selected tab has a placed running cycle — the same
        // condition WindowCardText.State resolves to WindowCardState.Chart
        // for, which is the only state the view draws this line under.
        WindowEquivalence.Row? liveEquivalence = null;
        if (selected?.Active is { IsPlaced: true } active)
        {
            // The card and this line must describe the same interval:
            // WindowCardGeometry.Chart already clips its bars and curve to
            // [active.StartMs, now), because a provider that shortens its
            // reported duration mid-cycle moves StartMs past readings
            // QuotaHistoryFold.Active deliberately still carries (see that
            // method's own doc comment). Declared() and LiveEquivalence()
            // used to run over the full unclipped Samples, so this line
            // could count quota movement and messages from before the
            // window the chart above it actually draws. One clip here feeds
            // both calls, rather than each re-deriving its own bound.
            var clipped = active.Samples.Where(sample => sample.AtMs >= active.StartMs!.Value).ToList();
            IReadOnlyList<QuotaSample> clippedSamples = clipped.Count == 0 ? active.Samples : clipped;

            var declared = QuotaEquivalenceFold.DeclaredSpan(
                clippedSamples[0].AtMs, clippedSamples[^1].AtMs, owner, messages, confirmed.Records);
            liveEquivalence = WindowCardText.LiveEquivalence(clippedSamples, mine, declared, windowUsageOutcome);
        }

        var windowHistory = BuildHistory(
            history, selected, messages, confirmed, owner, windowUsageOutcome, selection);
        return new Client(
            owner, tabs, selected, messages, mine, liveEquivalence,
            windowUsage?.UndatedCount ?? 0, windowHistory, quotaHistoryOutcome);
    }

    private static WindowHistory BuildHistory(
        IReadOnlyList<QuotaHistorySeries>? history,
        WindowCardTab? selected,
        IReadOnlyList<WindowMessage> messages,
        UsageAttribution.Table confirmed,
        string owner,
        WindowEquivalence.FetchOutcome windowUsageOutcome,
        Selection selection)
    {
        IReadOnlyList<QuotaHistorySeries> series = history ?? [];
        var matched = selected is null
            ? null
            : series.FirstOrDefault(s =>
                s.ProviderId == selected.Id.ProviderId
                && s.AccountScope == selected.Id.AccountScope
                && s.WindowKey == selected.Id.WindowKey);
        IReadOnlyList<QuotaCycle> cycles = matched is null
            ? []
            : QuotaHistoryFold.Considered(QuotaHistoryFold.Cycles(matched.Samples));

        // The RESOLVED window, not the stored preference: the preference can
        // name a window this client does not offer, and the window can move
        // without the preference changing. Keying the grown row count on the
        // resolution — the same one `cycles` above just went through — is what
        // stops a count grown on Session from arriving on Weekly.
        var shownWindow = selected is null ? null : WindowId(selected.Id);
        var shownCount = shownWindow is not null && shownWindow == selection.HistoryShownWindow
            ? selection.HistoryShownCount
            : WindowHistoryText.VisibleRows;

        // One join for the whole card, same shape as the view held before
        // this move: sorted once, one contiguous slice per cycle.
        //
        // modelScope: null. Windows has no scoped-window data wired yet, so
        // every model counts — the same unscoped behaviour this card already
        // had.
        var rows = QuotaHistoryFold.Rows(cycles, messages, owner, modelScope: null, confirmed.Records);
        var byResetAt = rows.ToDictionary(row => row.Id);
        // MineTokens/MineCost — the WHOLE-WINDOW totals attributed to this
        // subscription — not SpanTokens/SpanCost, which QuotaHistoryFold
        // restricts to the interval between the cycle's first and last quota
        // sample. Usage after EvidenceStartMs but before that first sample,
        // or after the last sample but before reset, is in Mine and not in
        // Span, so passing Span here under-reported the collapsed row and its
        // bar scale against the same row's own expanded model breakdown
        // (QuotaHistoryModel.Tokens sums to MineTokens, not SpanTokens) —
        // round 8's finding. The span-restricted figures stay where they
        // belong: WindowHistoryText.Equivalence below reads
        // QuotaHistoryRow.SpanTokens/SpanCost directly, because that ratio's
        // denominator (quota movement) is only defined across those same
        // samples.
        var displayRows = WindowHistoryText.Rows(
            cycles,
            [.. rows.Select(row => new WindowEquivalence.Cycle(
                row.Cycle.UsedPercent, row.MineTokens, row.MineCost, row.Cycle.ObservedFraction,
                row.Cycle.RisingRuns))],
            shownCount);

        // The SHOWN cycles, not every admitted one. `Aggregate` reads
        // `declared` as an OR over the very cycles it is folding (its own doc
        // comment says so), and this card pools the ≈ line over the rows on
        // screen — so a `declared` taken over hidden cycles is a vote cast by
        // evidence the reader cannot see. Concretely: with classification only
        // in a hidden cycle, `!declared` is false, `Aggregate` skips its
        // `Undeclared` branch, and rows with movement but nothing attributed
        // report "the quota moved and none of it was recorded on this machine"
        // — a data failure — when the truth is that this user has not
        // classified what the visible rows hold.
        //
        // The misalignment predates the grow control (12 shown against up to
        // ConsideredCycles folded); making the shown count variable is what
        // turned it from a fixed skew into one the reader can move.
        IReadOnlyList<QuotaCycle> shownCycles = [.. cycles.Take(displayRows.Count)];

        // Gated on the fetch's own outcome, not on whether QuotaHistory
        // itself landed: `declared` is computed from `messages`, which come
        // from the separate WindowUsage fetch. History can be ready while
        // that fetch is still in flight or has just failed, and asking
        // Declared() of an empty/stale `messages` list at that moment reads
        // as "nothing classified" — the same wrong-lane read the overview
        // path's own equivalence gate above guards against.
        var equivalence = windowUsageOutcome switch
        {
            WindowEquivalence.FetchOutcome.Succeeded => WindowHistoryText.Equivalence(
                [.. displayRows.Select(row => byResetAt[row.ResetAtMs])],
                declared: QuotaEquivalenceFold.Declared(shownCycles, owner, messages, confirmed.Records)),
            WindowEquivalence.FetchOutcome.Failed => new WindowEquivalence.Row.ScanFailed(),
            _ => new WindowEquivalence.Row.Loading(),
        };

        // Against `cycles`, the admitted list, and `displayRows`, what the
        // fold actually clamped to — not against `shownCount`, which can be
        // larger than either after a window loses history.
        return new WindowHistory(
            cycles, rows, byResetAt, displayRows, equivalence,
            WindowHistoryText.Remaining(cycles.Count, displayRows.Count), shownWindow);
    }

    /// <summary>The store's own triple, flattened for matching the persisted
    /// tab selection — the same format <c>DashboardView.Quota.cs</c>'s own
    /// <c>WindowId</c> writes it in.</summary>
    private static string WindowId(QuotaWindowIdentity id) =>
        $"{id.ProviderId}|{id.AccountScope}|{id.WindowKey}";
}
