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
    /// <param name="LocalUsageClients">Client ids with local usage records in
    /// any year: the view passes <see cref="LocalRecordClients.Union"/>
    /// (the persisted all-years set plus the loaded graph's
    /// <c>Summary.Clients</c>), which can be wider than the year-scoped list
    /// that decides which tabs are quota-only. Null means unknown and keeps
    /// the pre-rule behaviour (tests and callers without a graph). See
    /// <see cref="TabHasNoLocalRecords"/>.</param>
    /// <param name="PresentClients">The year-scoped present clients (macOS
    /// <c>stats.presentClients</c>), with <paramref name="TabHidden"/> and
    /// <paramref name="LimitsHidden"/> (raw saved sets) the inputs of
    /// <see cref="WindowCardOwner"/>. Null reads as empty.</param>
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
        int HistoryShownCount = WindowHistoryText.VisibleRows,
        string? WindowCardAccount = null,
        IReadOnlyCollection<string>? LocalUsageClients = null,
        IReadOnlyList<string>? PresentClients = null,
        IReadOnlySet<string>? TabHidden = null,
        IReadOnlySet<string>? LimitsHidden = null);

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
        IReadOnlyDictionary<QuotaWindowIdentity, WindowEquivalence.Row> Equivalences,
        IReadOnlySet<string> UnreadableClients)
    {
        /// <summary>A single-client tab's flag: its slice intersected with the
        /// unreadable set (macOS QuotaView.swift:87/:92,
        /// <c>!stripUnreadableClients.isDisjoint(with: clientIds)</c>).</summary>
        public bool UnreadableIn(IReadOnlyList<string> slice) => slice.Any(UnreadableClients.Contains);
    }

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
        WindowEquivalence.FetchOutcome QuotaHistoryOutcome,
        IReadOnlyList<WindowCardText.AccountPill> Accounts,
        string? SelectedAccount,
        // True for any non-primary account, and for every account of a tab
        // with no local records: Mine/LiveEquivalence/History carry no local
        // usage and the view prints the fixed line instead.
        bool LocalUsageUnattributed,
        string? AccountLabel,
        // The selected window is model-scoped and none of this
        // subscription's usage inside it matched the scope, though some
        // unscoped usage did (macOS WindowUsageHalf.scopeMatchedNothing,
        // WindowCardLoader.swift:327-329). Said on the card, not drawn as an
        // idle week.
        bool ScopeMatchedNothing = false,
        // False when macOS draws no window card for this tab
        // (<see cref="WindowCardOwner"/> returned null): the view draws the
        // strip and heatmap in its place and no window history.
        // <c>Owner</c> and the folds are then the tab's own owner, unused.
        bool HasWindowCard = true,
        // What WindowCardText.State needs to decide Idle vs pending.
        LocalScan? Scan = null);

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
    /// <para>
    /// <paramref name="quota"/> null (no agent-usage payload yet): macOS
    /// draws no rows then (its curve reads need the payload,
    /// DashboardModel.swift:1543/:1663; :1509 is the prune guard). Windows
    /// deliberately differs, by the maintainer's decision: it draws the
    /// retained series at once, minus tab-hidden and limits-hidden clients
    /// by settings. <paramref name="quotaAttempted"/>
    /// (<c>Snapshot.QuotaAttempted</c>, macOS <c>usageAttempted</c>) decides
    /// Loading vs NoCompletedWindows/NoMovement when nothing retained is
    /// left to draw, by folding NotAttempted into <c>Overview.Outcome</c>.
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
        Selection selection,
        IReadOnlyDictionary<string, Interop.WindowUsage>? accountWindowUsage = null,
        bool quotaHistoryReadFailed = false,
        bool quotaAttempted = true,
        DateTimeOffset? now = null,
        long? windowUsageFromMs = null,
        long? accountWindowUsageFromMs = null)
    {
        var overview = BuildOverview(
            history, quota, windowUsage, windowUsageOutcome, quotaHistoryOutcome, confirmed, selection,
            quotaHistoryReadFailed, quotaAttempted);
        var (trend, pastYearSelected) = BuildTrend(graph, confirmed, year);
        var client = selection.ActiveClientTab == ClientRegistry.OverviewTab
            ? null
            : BuildClient(
                selection.ActiveClientTab, selection.WindowCardTab,
                history, quota, windowUsage, windowUsageOutcome, quotaHistoryOutcome, confirmed,
                selection, accountWindowUsage, now ?? DateTimeOffset.UtcNow,
                windowUsageFromMs, accountWindowUsageFromMs);
        return new Model(overview, trend, pastYearSelected, client);
    }

    private static Overview BuildOverview(
        IReadOnlyList<QuotaHistorySeries>? history,
        AgentUsagePayload? quota,
        Interop.WindowUsage? windowUsage,
        WindowEquivalence.FetchOutcome windowUsageOutcome,
        WindowEquivalence.FetchOutcome quotaHistoryOutcome,
        UsageAttribution.Table confirmed,
        Selection selection,
        bool quotaHistoryReadFailed,
        bool quotaAttempted)
    {
        // macOS reads ONLY visibleAgents' history-key card windows
        // (DashboardModel.swift:1684-1712, behind `if let payload = agentUsage`
        // at :1543/:1663), then prunes summaries, heatmap windows, heatmaps and
        // equivalences to visibleAgents' window keys (:1509-1526, guarded by
        // `quotaVisibility != nil, agentUsage != nil` at :1509), so a
        // tab-hidden or limits-hidden client's retained series draws nothing.
        // With a payload, a stored series of a visible single-account client
        // under a FORMER account scope is dropped too, as on macOS (it reads
        // only historyReadAccountKey's curve).
        // Before the first payload macOS has no rows at all (they only come
        // from those curve reads). Windows deliberately differs, as the
        // maintainer decided: it draws the retained series right away, as the
        // client lens already does (round 16), excluding by settings what
        // visibleAgents would drop once a payload names the agents — a
        // tab-hidden client (folded to its group) and a limits-hidden client.
        // Without a payload a limits-hidden client's extra accounts cannot be
        // told from its primary, so all of its series wait for the payload.
        // Filtered ONCE; everything below derives from this slice.
        if (quota is null)
        {
            var excluded = ClientRegistry.QuotaExcludedClients(
                selection.TabHidden ?? new HashSet<string>(), selection.LimitsHidden ?? new HashSet<string>());
            history = history?.Where(s => !excluded.Contains(s.ProviderId)).ToList();
            // Until the agent-usage fetch has been attempted (macOS
            // usageAttempted) a card with nothing to draw says Loading — the
            // strip when no retained row is left, the heatmap when its
            // selected window has no grid — then NoCompleted/NoMovement.
            // Loading while EITHER that fetch or the history read is
            // unattempted: the history outcome already drives Loading.
            if (!quotaAttempted)
            {
                quotaHistoryOutcome = WindowEquivalence.FetchOutcome.NotAttempted;
            }
        }
        else
        {
            var visible = VisibleAgents(quota, selection);
            history = history?.Where(s => visible.Any(a => ReadsSeries(a, s))).ToList();
        }

        var (summaries, windows, grids) = QuotaLensData.Build(history, quota);
        // Absent (not merely empty) unless the fetch actually SUCCEEDED — not
        // "was attempted", which a failed pass also satisfies while retaining
        // stale (or no) messages. The strip/heatmap draw no line for a window
        // with no key rather than computing one from a read that did not
        // land; see this method's own doc comment on `windowUsageOutcome`.
        // Also absent before the first payload: without it no series can be
        // narrowed to its account or model scope (LocalUsageScopable and
        // ModelScope.Of pass everything through for a null quota), so an
        // extra account's or a model-scoped window's estimate would be priced
        // from the whole unscoped scan and overstated. macOS has no rows to
        // price before a payload either.
        var equivalences = quota is not null && windowUsageOutcome == WindowEquivalence.FetchOutcome.Succeeded
            ? QuotaEquivalenceFold.Build(
                [.. (history ?? []).Where(s => LocalUsageScopable(quota, s))],
                windowUsage?.Messages ?? [], confirmed,
                // Each window's estimate narrowed to its OWN scope, not to
                // whichever window a card happens to show (macOS
                // DashboardModel.swift:1836-1843).
                series => ModelScope.Of(quota, series.ProviderId, series.AccountScope, series.WindowKey))
            : new Dictionary<QuotaWindowIdentity, WindowEquivalence.Row>();
        return new Overview(
            summaries, windows, grids, quotaHistoryOutcome, equivalences,
            UnreadableClients(quota, quotaHistoryReadFailed, summaries, selection));
    }

    /// <summary>The clients whose strip/heatmap must say "could not be read"
    /// (macOS <c>quotaUnreadableClients</c>, DashboardModel.swift:1845-1862,
    /// over <c>visibleAgents</c>, :1502-1508): clients of visible agents —
    /// payload agents in <see cref="ClientRegistry.QuotaClients"/>, minus a
    /// limits-hidden PRIMARY (extra accounts stay) — that have a card window
    /// which failed to read and is not drawn. Only a window macOS would READ
    /// counts: one with a history key (DashboardModel.swift:1686;
    /// <c>PaceStatus.historyKey</c>, TokenBarCore/AgentUsage.swift:140-150 —
    /// none without a window key, and none for an account-scope-unavailable
    /// window, the <c>agy</c> CLI route or a Grok Bot token with no subject,
    /// which is never recorded and so never "could not be read").
    /// <para>macOS decides per window on EVERY publication from the latest
    /// read (:1670-1712, a throw puts the window in failedWindowIds), and
    /// "drawn" is read from the summaries as they now stand, which keep a
    /// previously drawn window over a failed pass (:1788-1796, :1845).
    /// Windows reads quota history in one call, so the analog is:
    /// <paramref name="latestReadFailed"/> (the latest read threw — NOT
    /// <c>outcome == Failed</c>, which holds only when nothing is retained and
    /// misses an earlier empty read retained under a later throw) makes every
    /// visible agent with at least one history-key card window that has no
    /// summary in <paramref name="summaries"/> unreadable. A summary matches
    /// a window by the identity <see cref="QuotaLensData"/> builds them with
    /// (client, account scope, window key); an agent with no history scope
    /// matches at client + window key. When the latest read did not fail,
    /// none.</para></summary>
    internal static IReadOnlySet<string> UnreadableClients(
        AgentUsagePayload? quota, bool latestReadFailed,
        IReadOnlyList<QuotaWindowSummary> summaries, Selection selection)
    {
        if (!latestReadFailed)
        {
            return new HashSet<string>();
        }

        return VisibleAgents(quota, selection)
            .Where(a => a.UniqueCardWindows.Any(w => HasHistoryKey(w) && !Drawn(a, w, summaries)))
            .Select(a => a.ClientId)
            .ToHashSet();
    }

    /// <summary>macOS <c>visibleAgents</c> (DashboardModel.swift:1502-1508):
    /// payload agents in <see cref="ClientRegistry.QuotaClients"/> (tab-hidden
    /// already excluded), minus a limits-hidden PRIMARY (extra accounts stay).
    /// The one definition the strip/heatmap filter and
    /// <see cref="UnreadableClients"/> share.</summary>
    internal static IReadOnlyList<AgentUsageSnapshot> VisibleAgents(AgentUsagePayload? quota, Selection selection)
    {
        var cardClients = ClientRegistry.QuotaClients(
            selection.PresentClients ?? [], quota?.ConfiguredClientIds ?? [], selection.TabHidden ?? new HashSet<string>());
        var limitsHidden = selection.LimitsHidden ?? new HashSet<string>();
        return [.. (quota?.Agents ?? []).Where(a => cardClients.Contains(a.ClientId)
            && (a.AccountKey is not null || !limitsHidden.Contains(a.ClientId)))];
    }

    // Whether a visible agent's history-key card window is this stored series
    // (same identity match as Drawn: client + window key, plus the agent's
    // history scope when it has one).
    private static bool ReadsSeries(AgentUsageSnapshot agent, QuotaHistorySeries series) =>
        series.ProviderId == agent.ClientId
        && (agent.HistoryReadScope?.Scope is not { } scope || series.AccountScope == scope)
        && agent.UniqueCardWindows.Any(w => HasHistoryKey(w) && w.PaceStatus.WindowKey == series.WindowKey);

    private static bool Drawn(AgentUsageSnapshot agent, UsageWindow window, IReadOnlyList<QuotaWindowSummary> summaries) =>
        summaries.Any(s => s.Id.ProviderId == agent.ClientId
            && s.Id.WindowKey == window.PaceStatus.WindowKey
            && (agent.HistoryReadScope?.Scope is not { } scope || s.Id.AccountScope == scope));

    // macOS PaceStatus.historyKey (TokenBarCore/AgentUsage.swift:148-150).
    private static bool HasHistoryKey(UsageWindow window) =>
        window.PaceStatus.WindowKey is not null
        && !(window.PaceStatus.State == UsagePaceState.Unavailable
            && window.PaceStatus.Reason == UsagePaceUnavailableReason.AccountScope);

    /// <summary>Whether a stored series may get a local-usage equivalence.
    /// A client with no non-primary card keeps every series (as before). With
    /// any non-primary card only the PRIMARY's series qualify (none when there
    /// is no primary card or it has no scope): Windows has no per-account
    /// scan, so a non-primary (or unmatched) series would be priced from the
    /// primary's messages.</summary>
    internal static bool LocalUsageScopable(AgentUsagePayload? quota, QuotaHistorySeries series)
    {
        var cards = (quota?.Agents ?? []).Where(a => a.ClientId == series.ProviderId).ToList();
        return cards.All(a => a.Account.AccountKey is null)
            || cards.Any(a => a.Account.AccountKey is null && a.HistoryReadScope?.Scope == series.AccountScope);
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
        Selection selection,
        IReadOnlyDictionary<string, Interop.WindowUsage>? accountWindowUsage,
        DateTimeOffset now,
        long? windowUsageFromMs,
        long? accountWindowUsageFromMs)
    {
        // Every subscription-facing lookup below is keyed by the quota OWNER,
        // not the raw client id — antigravity-cli spends the antigravity
        // subscription — or, on a grouped tab whose owner is not a card
        // client (a Grok Bot-only install), by the member that is
        // (WindowCardOwner).
        var cardOwner = WindowCardOwner(quota, clientId, selection.PresentClients, selection.TabHidden, selection.LimitsHidden);
        var owner = cardOwner ?? ClientRegistry.QuotaOwner(clientId);
        // One card per client: the primary when it has windows, else the
        // first other account that does (Desktop-only users).
        var account = WindowCardText.WindowCardAccount(quota, owner, selection.WindowCardAccount);
        // A non-primary account reads local usage only from its own scan:
        // tb_window_usage(accountKey) for a Claude config directory, fetched
        // with the key the native registry itself reported on that card
        // (ClaudeExtraRoots.AttributableAccountKeys). Anything else — Claude
        // Desktop, another provider's account, a scan that failed or has not
        // run — stays unattributed and never reads the primary's messages
        // (spec rule 6).
        Interop.WindowUsage? accountUsage = null;
        if (account is not null && owner == ClaudeExtraRoots.ClientId)
        {
            accountWindowUsage?.TryGetValue(account, out accountUsage);
        }

        // The card moved to another member only when the owner is not a card
        // client, i.e. has no local records in the tab and no configured
        // quota (a Grok Bot-only install): the tab has no local usage to
        // read, and macOS gives it no scan.
        var unattributed = (account is not null && accountUsage is null)
            || owner != ClientRegistry.QuotaOwner(clientId)
            || TabHasNoLocalRecords(clientId, selection.LocalUsageClients, confirmed.Records);
        // An account the registry CAN attribute whose own scan has not landed
        // (cold start, or failed with nothing retained) is not "unattributed":
        // it is not read yet, and the card waits (PlacementPending) rather than
        // saying "start unknown" and flipping when the scan arrives. The
        // failed-vs-not-yet distinction is not carried per account, so both wait.
        var accountScanPending = account is not null
            && owner == ClaudeExtraRoots.ClientId
            && accountUsage is null
            && ClaudeExtraRoots.AttributableAccountKeys(quota).Contains(account);
        // The bound belongs to the scan the card actually reads from.
        var scanFromMs = windowUsageFromMs;
        if (accountUsage is not null)
        {
            windowUsage = accountUsage;
            windowUsageOutcome = WindowEquivalence.FetchOutcome.Succeeded;
            scanFromMs = accountWindowUsageFromMs;
        }

        var tabs = WindowCardText.Tabs(history, quota, owner, account).ToList();
        var selected = tabs.FirstOrDefault(tab => WindowId(tab.Id) == windowCardTab)
            ?? DefaultTab(tabs);
        IReadOnlyList<WindowMessage> messages = unattributed ? [] : windowUsage?.Messages ?? [];
        // The selected window's model scope, looked up once (ModelScope.Of)
        // and handed to every surface of this card: the bars and live line
        // (`mine`), and the history rows (BuildHistory). Port of macOS
        // WindowCardLoader.swift:300-330 / DashboardModel.swift:1893-1902.
        var modelScope = ModelScope.Of(
            quota, selected?.Id.ProviderId, selected?.Id.AccountScope, selected?.Id.WindowKey);
        var subscription = WindowCardText.Mine(messages, owner, confirmed.Records);
        // macOS WindowResolver's `.active` and `.inferred` branches
        // (WindowResolution.swift:29-35): no running cycle in the store, but
        // the live reset is ahead within one window length, or passed within
        // one and this subscription (attribution only, no model scope) has
        // used it since. Done before everything below so the scope note and
        // the live line follow the placed window.
        if (selected is not null
            && WindowCardText.Infer(selected, subscription, now.ToUnixTimeMilliseconds()) is { } inferred)
        {
            tabs[tabs.IndexOf(selected)] = inferred;
            selected = inferred;
        }

        // The scope narrows what the card DISPLAYS only. Placement
        // (selected.Active) comes from the quota samples and never reads
        // messages, so a scope join that matches nothing leaves the window
        // where it is instead of blanking it (macOS's own reason for keeping
        // the scope out of placement).
        var mine = QuotaHistoryFold.InScope(subscription, modelScope);
        var scopeMatchedNothing = false;
        // Only from a read that landed: a failed refetch keeps stale messages,
        // and the note is a claim about this subscription's usage. Never for
        // an unattributed account: it reads no messages, so "nothing matched"
        // would be a claim about usage it cannot see.
        if (!unattributed
            && modelScope is not null
            && windowUsageOutcome == WindowEquivalence.FetchOutcome.Succeeded
            && selected?.Active is { IsPlaced: true } placed)
        {
            bool Inside(WindowMessage message) =>
                message.Timestamp >= placed.StartMs!.Value && message.Timestamp < placed.ResetAtMs!.Value;
            scopeMatchedNothing = !mine.Any(Inside) && subscription.Any(Inside);
        }

        // Only when the selected tab has a placed running cycle — the same
        // condition WindowCardText.State resolves to WindowCardState.Chart
        // for, which is the only state the view draws this line under.
        WindowEquivalence.Row? liveEquivalence = null;
        // A cycle placed from the live reset has no samples, hence no quota line to compare.
        if (!unattributed && selected?.Active is { IsPlaced: true, Samples.Count: > 0 } active)
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
            history, selected, messages, confirmed, owner, windowUsageOutcome, selection, modelScope);
        return new Client(
            owner, tabs, selected, messages, mine, liveEquivalence,
            unattributed ? 0 : windowUsage?.UndatedCount ?? 0, windowHistory, quotaHistoryOutcome,
            WindowCardText.AccountPills(quota, owner), account, unattributed,
            WindowCardText.HeaderAccountLabel(quota, owner, account),
            scopeMatchedNothing, HasWindowCard: cardOwner is not null,
            Scan: new LocalScan(
                accountScanPending ? WindowEquivalence.FetchOutcome.NotAttempted : windowUsageOutcome,
                scanFromMs, unattributed && !accountScanPending));
    }

    /// <summary>The tab-group member whose window card, history and account
    /// pills a client tab draws. Ported from macOS
    /// <c>WindowCardGate.clients</c> (WindowCardLoader.swift:625-639) with its
    /// inputs from PopoverView.swift:151-165: the card clients are
    /// <see cref="ClientRegistry.QuotaClients"/> (present clients' slices plus
    /// the payload's configured ids, minus tab-hidden); the tab's quota owner
    /// draws when it is one, else the first slice member that is and is not
    /// excluded (<see cref="ClientRegistry.QuotaExcludedClients(IReadOnlySet{string}, IReadOnlySet{string})"/>).
    /// A Grok Bot-only user gets the grok-bot window on the "Grok Build &amp; Bot"
    /// tab; with Grok Build present locally the card is keyed on grok even
    /// when only the Bot reports windows. At most one card per tab.
    /// Null when macOS draws no card: the tab itself is excluded
    /// (limits-hidden owner, <c>guard !excluded.contains(tab)</c>; the tab is
    /// <see cref="ClientRegistry.QuotaOwner"/> of the active tab, which is what
    /// the Windows tab selection stores) or no member qualifies.</summary>
    internal static string? WindowCardOwner(
        AgentUsagePayload? quota, string clientId,
        IReadOnlyList<string>? present = null,
        IReadOnlySet<string>? tabHidden = null, IReadOnlySet<string>? limitsHidden = null)
    {
        var owner = ClientRegistry.QuotaOwner(clientId);
        var hidden = tabHidden ?? new HashSet<string>();
        return ClientRegistry.WindowCardClient(
            owner,
            ClientRegistry.QuotaClients(present ?? [], quota?.ConfiguredClientIds ?? [], hidden),
            ClientRegistry.QuotaExcludedClients(hidden, limitsHidden ?? new HashSet<string>()));
    }

    /// <summary>True only when presence is KNOWN and no member of the tab group
    /// behind <paramref name="clientId"/> has local records: a scan of such a
    /// tab returns zeros that read as "nothing used", so every account of it is
    /// unattributed. Null (not loaded) keeps today's behaviour. Members come
    /// from <see cref="ClientRegistry.TabSlice"/>, so an Antigravity tab with
    /// records only under antigravity-cli still counts as having records; so
    /// does a present client with a confirmed Assigned record whose target is
    /// a member (<paramref name="confirmed"/> is the table the card's Mine fold
    /// reads; macOS WindowCardGate.tabHasLocalRecords).</summary>
    internal static bool TabHasNoLocalRecords(
        string clientId,
        IReadOnlyCollection<string>? localClients,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        if (localClients is null)
        {
            return false;
        }

        var present = localClients.Select(ClientRegistry.CanonicalClient).ToHashSet();
        var slice = ClientRegistry.TabSlice(ClientRegistry.QuotaOwner(clientId));
        if (slice.Any(present.Contains))
        {
            return false;
        }

        // A present client with a confirmed record assigning it to a member
        // counts too (macOS #468 WindowCardGate.tabHasLocalRecords, same
        // form): a Codex used only through OpenCode (confirmed
        // opencode·openai → codex) otherwise read as quota-only and hid that
        // usage. The client is matched raw, as UsageAttribution.Resolve (which
        // Mine uses) matches it. This is coarser than Mine: it does not check
        // the record's provider/model against local messages, and it accepts
        // any slice member while Mine credits the owner.
        return !confirmed.Any(record =>
            localClients.Contains(record.Client)
            && slice.Any(member => record.State == UsageAttribution.State.Assigned(member)));
    }

    /// <summary>Which tab opens when the user has no explicit pick for this
    /// client — port of macOS's <c>WindowCardLoader.pick</c> (its
    /// no-explicit-pick tail): (1) the first session-class tab, by
    /// <see cref="WindowCardText.IsSessionClass"/>; else (2) the tab with the
    /// lowest finite <see cref="WindowCardTab.RemainingPercent"/>; else
    /// (3) the first tab. Tab display order (<see cref="WindowCardText.Tabs"/>)
    /// is the provider's own order and plays no part in this choice — the
    /// defect this replaces conflated the two by re-sorting the tabs
    /// themselves so "first" happened to mean "running".</summary>
    private static WindowCardTab? DefaultTab(IReadOnlyList<WindowCardTab> tabs)
    {
        var session = tabs.FirstOrDefault(tab => WindowCardText.IsSessionClass(tab.Id.WindowKey));
        if (session is not null)
        {
            return session;
        }

        var mostDepleted = tabs
            .Where(tab => tab.RemainingPercent is { } percent && double.IsFinite(percent))
            .OrderBy(tab => tab.RemainingPercent!.Value)
            .FirstOrDefault();
        return mostDepleted ?? tabs.FirstOrDefault();
    }

    private static WindowHistory BuildHistory(
        IReadOnlyList<QuotaHistorySeries>? history,
        WindowCardTab? selected,
        IReadOnlyList<WindowMessage> messages,
        UsageAttribution.Table confirmed,
        string owner,
        WindowEquivalence.FetchOutcome windowUsageOutcome,
        Selection selection,
        string? modelScope)
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
        // this move: sorted once, one contiguous slice per cycle. Narrowed to
        // the selected window's model scope: the history has to answer for the
        // same allowance the chart above it draws (macOS
        // DashboardModel.swift:1893-1902).
        var rows = QuotaHistoryFold.Rows(cycles, messages, owner, modelScope, confirmed.Records);
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
