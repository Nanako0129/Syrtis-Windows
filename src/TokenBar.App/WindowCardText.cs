using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>Which of the Session-window card's states applies. Ported from
/// <c>WindowUsageCard.swift</c>'s five-case <c>WindowCardState</c>, minus the
/// two-stage split macOS needs for a scan that lands after the quota half:
/// Windows fetches both lanes through <c>DashboardModel</c>'s snapshot, so a
/// chart with no messages yet simply draws no bars.</summary>
public enum WindowCardState
{
    /// <summary>The persisted-curve read has not settled. Distinct from having
    /// no history, and it is the first paint of every cold start.</summary>
    Loading,

    /// <summary>Nothing recorded for this window at all, so there is no line to
    /// draw.</summary>
    NoQuotaHistory,

    /// <summary>The quota-history read was attempted and threw — distinct
    /// from <see cref="NoQuotaHistory"/>, which claims the read landed and
    /// genuinely found nothing for this window. Round 8's finding: a failed
    /// read used to collapse into <see cref="NoQuotaHistory"/> because
    /// <c>State</c> took only a <c>bool attempted</c>, which cannot carry
    /// this distinction.</summary>
    HistoryFetchFailed,

    /// <summary>The last window ended and nothing has been used since.</summary>
    Idle,

    /// <summary>A window IS running, and the subscription reported no usable
    /// reset time to place it with. Not <see cref="Idle"/>: that one says the
    /// user stopped working, and this one says the provider stopped
    /// answering.</summary>
    Unplaceable,

    Chart,
}

/// <summary>One sub-tab of the Session-window card: a window of the selected
/// client, and the cycle running inside it.</summary>
/// <param name="HasHistory">Whether a stored series was found for this window
/// at all. Distinct from <paramref name="Active"/> being null: that also
/// happens when a series EXISTS but its last window simply ended, which is
/// <see cref="WindowCardState.Idle"/>, not <see cref="WindowCardState.NoQuotaHistory"/>
/// — two different facts a shared null would collapse into one.</param>
/// <param name="RemainingPercent">The live window's own remaining percent,
/// for <see cref="QuotaLensProjection"/>'s "most depleted" default-selection
/// tiebreak — mirroring macOS's <c>$0.remainingPercent</c> scan in
/// <c>WindowCardLoader.pick</c>. Null on the store-fallback path (no live
/// <c>UsageWindow</c> to read it off).</param>
public sealed record WindowCardTab(
    QuotaWindowIdentity Id, string? Label, QuotaActiveCycle? Active, bool HasHistory,
    double? RemainingPercent = null);

/// <summary>
/// Every state choice and every string on the Session-window card (port of
/// <c>WindowUsageCard.swift</c>; the WinUI layout lives in
/// <c>DashboardView.Quota.cs</c>).
/// <para>
/// Pulled into <c>TokenBar.Core.Tests</c> via &lt;Compile Include&gt; for the
/// same reason as <see cref="QuotaLensText"/> and
/// <see cref="WindowEquivalenceText"/>: <c>DashboardView.Quota.cs</c> is
/// compiled by no test project, so a branch decided there is untested by
/// construction.
/// </para>
/// </summary>
public static class WindowCardText
{
    /// <summary>The used/remaining direction is one preference shared with the
    /// Agent-limits card, the same <c>@AppStorage("tokenbar.limits.asUsed")</c>
    /// macOS reads on both. Two toggles over two keys would let one card count
    /// up while the card below it counted down.</summary>
    public const string AsUsedKey = "tokenbar.limits.asUsed";

    /// <summary>Which window of this client the card is showing. Its own key,
    /// not the heatmap's: those are different lists (the heatmap drops windows
    /// with no movement) and one stored value would name a window the other
    /// picker does not offer.</summary>
    public const string TabKey = "tokenbar.windowcard.window";

    /// <summary>The account scope a live window's tab carries when neither a
    /// stored series nor the live payload itself can supply one — the live
    /// agent's own <see cref="AgentUsageSnapshot.HistoryScope"/> resolution
    /// failed (or this snapshot predates the field), so there is no real HMAC
    /// to compare against and this placeholder is the only identity left to
    /// name the tab with. Last resort, not the common case: whenever the live
    /// scope IS known, <see cref="Tabs"/> uses it directly.</summary>
    private const string PrimaryAccountScope = "primary";

    /// <summary>
    /// One tab per window the client is CURRENTLY reporting, each carrying
    /// the running cycle a stored series has for it.
    /// <para>
    /// Enumerated from the live side — <c>AgentUsageSnapshot.UniqueCardWindows</c>
    /// — the same shape as macOS's <c>uniqueCardWindows</c>-driven
    /// <c>candidates</c> list, and for the same reason: the live payload names
    /// each window once, with the provider's own label, while the store can
    /// hold several series for what is now one window (a rename, a re-carded
    /// provider revision). Enumerating the store side instead is what
    /// produced the duplicate and raw-key tabs this replaces — several stored
    /// series mapping to one live window each drew their own tab, and a
    /// series matching no live label fell back to printing its raw store key.
    /// </para>
    /// <para>
    /// A stored series with no live window (the provider stopped reporting
    /// it) deliberately produces no tab: macOS cannot show a window its own
    /// live payload no longer offers either, and a history-only tab would
    /// have no live label to show. That rule assumes this client's own live
    /// data actually landed, though — see <see cref="LiveWindowsUnavailable"/>
    /// for the two shapes in which it did not, and what this falls back to
    /// when it did not: enumerating the store's own window keys directly —
    /// one tab per stored series, no live label, its running cycle read
    /// purely off the stored samples — so a successfully-read series stays
    /// visible while the live lane is down. A window the store has nothing
    /// under either still produces no tab, exactly as it does today.
    /// </para>
    /// <para>
    /// <see cref="QuotaHistorySeries.ProviderId"/> is — despite the field name
    /// inherited from the wire — already a registered CLIENT id, the
    /// quota-tracked subscription owner, which is what
    /// <see cref="QuotaEquivalenceFold"/> already relies on. So the per-client
    /// filter is that equality and nothing else; no join table, and no
    /// second id space to drift.
    /// </para>
    /// <para>
    /// The store can hold several series under one
    /// <c>(providerId, windowKey)</c> that differ only in
    /// <see cref="QuotaHistorySeries.AccountScope"/>. The live agent's
    /// <see cref="AgentUsageSnapshot.HistoryScope"/> is the exact key the
    /// writer is recording under right now, so a series is only eligible for
    /// this join when its <c>AccountScope</c> equals it; any other series is
    /// excluded outright, not merely deprioritised. Joining by
    /// <c>WindowKey</c> alone would leave the choice to dictionary iteration
    /// order.
    /// </para>
    /// <para>
    /// What that filter isolates depends on the provider, because the history
    /// scope does (Rust <c>resolve_history_scope</c>). Codex and the
    /// Antigravity local IDE key history on an authoritative owner ID (the
    /// ChatGPT account ID; the signed-in email), so two accounts there keep
    /// two series and the previous account's series stays out of the new
    /// one's tab. Claude, Copilot, Grok and Antigravity's remote OAuth route
    /// have no owner ID in anything fetched, so their history scope is one
    /// constant per installation and provider: every account signed in on
    /// this installation records into, and is shown, the same series. That
    /// is macOS's trade, accepted for Windows on 2026-09-25 — the curve
    /// models the operator, not the billing account — and it is what stops a
    /// credential rotation from starting the history over. Before it, those
    /// providers keyed history on the credential lineage and this filter
    /// isolated accounts for them too; the series that rule left behind are
    /// merged into the history-scope series once, by the Rust store's
    /// one-time schema-3 fold (a window whose merge fails validation keeps
    /// its old series, which this filter then no longer shows).
    /// </para>
    /// <para>
    /// <see cref="AgentUsageSnapshot.AccountScope"/> is deliberately not used
    /// here: for a provider without an owner ID it is the credential lineage,
    /// which no longer names any stored series. When the history scope itself
    /// could not be resolved (an <see cref="AccountScopeStatus.Error"/>, or an
    /// older payload that predates the field) there is no signal to filter by
    /// at all, so every series is kept and the live join below falls back to
    /// first-wins.
    /// </para>
    /// <para>
    /// Round 19's finding: that "no account signal" case still went through
    /// <c>byWindowKey</c> — keyed on <c>WindowKey</c> alone — for the
    /// no-live-windows fallback below, so two accounts' series sharing one
    /// <c>WindowKey</c> collapsed into whichever <c>TryAdd</c> saw first, the
    /// same first-wins bug this doc comment used to describe as fixed. The
    /// fallback now enumerates the scope-filtered set directly
    /// (<c>matching</c>) instead of going through that collapsed dictionary,
    /// which stays reserved for the live join below, where joining by
    /// <c>WindowKey</c> alone is the live payload's own limitation
    /// (<c>PaceStatus</c> carries no <c>AccountScope</c>), not a shortcut
    /// taken here.
    /// </para>
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Round 17's finding: <paramref name="quota"/> being null is not the
    /// only shape in which this client's live windows are unusable. Rust's
    /// <c>agent_usage.rs</c> was read end to end (<c>AgentUsagePayload</c>,
    /// <c>AgentUsageSnapshot</c> and every producer of one) to enumerate every
    /// shape rather than add a second condition beside the first:
    /// </para>
    /// <list type="bullet">
    /// <item>The whole payload is null (fetch pending or the last attempt
    /// threw before producing anything at all). Handled by the <c>quota is
    /// null</c> half of the condition below, unchanged since round 16.</item>
    /// <item>This client's snapshot IS present but is Rust's own
    /// <c>empty_error_snapshot</c> placeholder — <c>Error</c> set,
    /// <c>Windows</c> empty. Built at three sites: an account-identity
    /// verification failure, a terminal fetch failure, and a transient
    /// failure with no last-good cache entry to fall back to. This is the
    /// shape round 17 actually reported, and <see cref="LiveWindowsUnavailable"/>
    /// below is what now catches it.</item>
    /// <item>This client's snapshot is present, <c>Error</c> is set, but
    /// <c>Windows</c> is NOT empty — a transient failure that DID find a
    /// last-good cache entry to fall back to (Rust re-uses that cached
    /// snapshot's windows and stamps the new failure's <c>Error</c> onto it
    /// as a staleness note). This is real, previously-live data, merely
    /// stale — not the shape this fix targets, and
    /// <see cref="LiveWindowsUnavailable"/> deliberately answers false for
    /// it so the live loop below keeps drawing those windows rather than
    /// discarding them for a store fallback that would be a step backward,
    /// not forward.</item>
    /// <item>This client has no entry in <c><paramref name="quota"/>.Agents</c>
    /// at all — Rust's <c>ProviderFetchOutcome::Absent</c> (no credential
    /// found for this provider). Deliberately NOT treated as unavailable
    /// here: unlike every shape above, Absent carries no <c>Error</c> string
    /// to swallow, and it does not mean "attempted and failed" the way the
    /// other three do — it means "not authenticated for this provider right
    /// now". Falling back to old stored tabs for a client the user may have
    /// logged out of would present retired history as if it were still an
    /// active subscription. Pre-existing behaviour (zero tabs, same as
    /// before round 16) is left as is.</item>
    /// <item>A provider present with windows but no stored series matching
    /// its live history scope — already excluded by the <c>liveScope</c>
    /// filter above <c>byWindowKey</c> is built from; not a new shape, and not
    /// unavailability, it is data recorded under a different key being
    /// correctly kept out.</item>
    /// </list>
    /// <para>
    /// The <c>Error</c> string on the true-unavailable shapes is not
    /// swallowed by this fallback: <c>DashboardView.xaml.cs</c>'s
    /// <c>BuildLimits</c> already reads <c>agent.Error</c> directly off the
    /// same <see cref="AgentUsageSnapshot"/> and shows it in the Agent-limits
    /// card, independently of what this method returns — checked, and it
    /// does not read through <see cref="Tabs"/> at all, so this fallback
    /// cannot make that message any less visible than it already is today.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<WindowCardTab> Tabs(
        IReadOnlyList<QuotaHistorySeries>? history,
        AgentUsagePayload? quota,
        string clientId)
    {
        var agent = quota?.Agents.FirstOrDefault(a => a.ClientId == clientId);
        var liveScope = agent?.HistoryScope?.Scope;

        // Every stored series this client's own scope-filtered set contains —
        // restricted to the live history scope first (see the doc
        // comment above), but every (AccountScope, WindowKey) pair inside
        // that filtered set kept, not collapsed. The fallback loop below
        // needs exactly this: with no live window to join against, it has no
        // reason to drop a second account's distinct history for the same
        // WindowKey.
        var matching = new List<QuotaHistorySeries>();
        foreach (var series in history ?? [])
        {
            if (series.ProviderId != clientId)
            {
                continue;
            }

            if (liveScope is not null && series.AccountScope != liveScope)
            {
                continue;
            }

            matching.Add(series);
        }

        // Keyed by the store's WindowKey alone — the join PaceStatus.WindowKey
        // can reach — so a live window finds its own running cycle without
        // needing to know the store's AccountScope half up front. When
        // liveScope was resolved, `matching` already holds only that one
        // account's series, so no two entries can share a WindowKey and
        // TryAdd never actually discards anything; when it was not, this
        // collapse is the live payload's own limitation (PaceStatus carries
        // no AccountScope to join by), not a shortcut taken here. Either
        // way, this dictionary stays reserved for the live join below — the
        // fallback path has no live scope to join against and must not go
        // through this collapsed view.
        var byWindowKey = new Dictionary<string, QuotaHistorySeries>();
        foreach (var series in matching)
        {
            byWindowKey.TryAdd(series.WindowKey, series);
        }

        var tabs = new List<WindowCardTab>();
        if (quota is null || LiveWindowsUnavailable(agent))
        {
            // Either shape of "this client's own live windows are not there
            // to enumerate" — see LiveWindowsUnavailable's doc comment. That
            // must not mean this client's own stored history goes
            // undisplayed too: fall back to the store's own series directly
            // (already scope-filtered above, same as the live path would
            // have been) — `matching`, not `byWindowKey.Values`, so two
            // stored series for the same WindowKey under different accounts
            // both still produce a tab instead of the second silently losing
            // to dictionary iteration order (round 19's finding). No live
            // label exists for these, so `Title` falls back to the window
            // key, and there is no `PaceStatus` to read a running cycle off
            // — `Active` comes from the stored samples alone, same as the
            // live path already does for a window the store has a series
            // for.
            foreach (var series in matching)
            {
                tabs.Add(new WindowCardTab(
                    new QuotaWindowIdentity(clientId, series.AccountScope, series.WindowKey),
                    Label: null,
                    QuotaHistoryFold.Active(series.Samples),
                    HasHistory: true));
            }
        }
        else
        {
            foreach (var window in agent?.UniqueCardWindows ?? [])
            {
                var series = window.PaceStatus.WindowKey is { } key
                    ? byWindowKey.GetValueOrDefault(key)
                    : null;
                tabs.Add(new WindowCardTab(
                    // The store's own WindowKey when a series was found — that is
                    // what BuildWindowHistoryCard joins back against to find this
                    // window's past cycles — else the live PaceStatus.WindowKey
                    // (still the real dotted key, just not one the store has
                    // recorded yet), and the live CardId (`<client>|<window>`,
                    // per QuotaLabels) only as the last-resort identity for a
                    // window with neither — where no join and no dot-component
                    // read (IsSessionClass) is possible anyway. The account half
                    // prefers the matched series' own scope, then the live scope
                    // (a window the store has nothing under yet, yet the live
                    // agent still names an account), then the last-resort
                    // placeholder.
                    new QuotaWindowIdentity(
                        clientId,
                        series?.AccountScope ?? liveScope ?? PrimaryAccountScope,
                        series?.WindowKey ?? window.PaceStatus.WindowKey ?? window.CardId),
                    window.Label,
                    series is null ? null : QuotaHistoryFold.Active(series.Samples),
                    HasHistory: series is not null,
                    RemainingPercent: window.RemainingPercent));
            }
        }

        // Tab order is the provider's own order (live UniqueCardWindows order,
        // or the store's order on the fallback path) — never re-sorted here.
        // Which tab OPENS first is a separate question, answered by
        // QuotaLensProjection's default-selection logic, the same split
        // macOS's WindowCardLoader keeps between `candidates` (display order)
        // and `pick` (which one is shown first).
        return tabs;
    }

    /// <summary>Whether <paramref name="windowKey"/> names a session-class
    /// window — a dot-COMPONENT match, so <c>weekly_scoped.fable.v1</c> does
    /// not qualify despite containing the substring nowhere. Mirrors macOS's
    /// <c>WindowCardLoader.isSessionClass</c> exactly, including the reason:
    /// a substring match would let a provider's own naming (e.g. a scoped
    /// weekly window) pass for the window the card was built to prefer.
    /// </summary>
    internal static bool IsSessionClass(string? windowKey) =>
        windowKey is not null && windowKey.Split('.').Contains("session");

    /// <summary>
    /// The one predicate every "does this client have live windows worth
    /// enumerating" question in <see cref="Tabs"/> asks — named once so a new
    /// shape of unavailability is a change to this method, not a new
    /// disjunct at the call site. True precisely for Rust's
    /// <c>empty_error_snapshot</c> shape: <see cref="AgentUsageSnapshot.Error"/>
    /// is set AND <see cref="AgentUsageSnapshot.Windows"/> is empty. See
    /// <see cref="Tabs"/>'s own remarks for the full enumeration this was
    /// checked against, including the shapes that deliberately answer false
    /// here (a stale-but-populated fallback snapshot, and an agent absent
    /// from the payload altogether).
    /// </summary>
    private static bool LiveWindowsUnavailable(AgentUsageSnapshot? agent) =>
        agent is not null && agent.Error is not null && agent.Windows.Count == 0;

    /// <summary>Messages this window's own subscription is answerable for.
    /// Same rule as <see cref="QuotaEquivalenceFold.Cycles"/>: a message is
    /// this window's evidence precisely when the user's confirmed
    /// classification resolves it to the window's own client id. An
    /// unclassified machine therefore draws no bars — deliberately, because
    /// bars under this line are a claim about which subscription paid.</summary>
    public static IReadOnlyList<WindowMessage> Mine(
        IReadOnlyList<WindowMessage> messages,
        string clientId,
        IReadOnlyList<UsageAttribution.Record> confirmed) =>
        [.. messages.Where(message =>
        {
            var state = UsageAttribution.Resolve(
                message.Client, message.ProviderId, message.ModelId, confirmed);
            return state.Kind == UsageAttribution.StateKind.Assigned
                && state.Target == clientId;
        })];

    /// <summary>The Chart state's own <c>≈</c> line — the live counterpart of
    /// <see cref="WindowHistoryText.Equivalence"/>'s pooled one. Takes the
    /// window's own quota readings (the same ones the chart draws) and
    /// <paramref name="mine"/>, <see cref="Mine"/>'s already-attributed
    /// messages, so this card and the chart above it can never disagree about
    /// which usage counts.
    /// <para>
    /// <paramref name="declared"/> and <paramref name="attempt"/> are the
    /// same two facts <see cref="WindowHistoryText.Equivalence"/> already
    /// requires for its pooled line, required here for the same reason:
    /// <see cref="WindowEquivalence.LiveRow"/> cannot accept a call that
    /// omits either, so this wrapper cannot either.
    /// </para>
    /// </summary>
    public static WindowEquivalence.Row LiveEquivalence(
        IReadOnlyList<QuotaSample> samples,
        IReadOnlyList<WindowMessage> mine,
        bool declared,
        WindowEquivalence.FetchOutcome attempt) =>
        WindowEquivalence.LiveRow(
            declared,
            attempt,
            [.. samples.Select(sample => new WindowEquivalence.Sample(sample.AtMs, sample.UsedPercent))],
            mine);

    public static WindowCardState State(WindowCardTab? tab, WindowEquivalence.FetchOutcome outcome)
    {
        // No tab at all (nothing to select) and a tab with no stored series
        // (a live window the store has nothing recorded for) are the same
        // fact from this card's point of view, and `outcome` still decides
        // whether that is a wait, a failure, or an answer either way.
        if (tab is null || !tab.HasHistory)
        {
            return outcome switch
            {
                WindowEquivalence.FetchOutcome.NotAttempted => WindowCardState.Loading,
                WindowEquivalence.FetchOutcome.Failed => WindowCardState.HistoryFetchFailed,
                _ => WindowCardState.NoQuotaHistory,
            };
        }

        return tab.Active switch
        {
            null => WindowCardState.Idle,
            { IsPlaced: false } => WindowCardState.Unplaceable,
            _ => WindowCardState.Chart,
        };
    }

    /// <summary>The card's own heading. The window half goes through
    /// <see cref="QuotaLabels.Window"/> — the one place a window is named — so
    /// this title, the tab pill above it and the strip card's row cannot
    /// disagree about what the same window is called.</summary>
    public static string Title(WindowCardTab? tab) =>
        tab is null
            ? "Session window".Localized()
            : "{0} window".Localized(QuotaLabels.Window(tab.Label, tab.Id.WindowKey));

    /// <summary>The tab pill's text. Same naming as <see cref="Title"/>, without
    /// the "window" noun the heading adds.</summary>
    public static string TabLabel(WindowCardTab tab) =>
        QuotaLabels.Window(tab.Label, tab.Id.WindowKey);

    /// <summary>The line under the title. Every state names itself, so the
    /// subtitle can never say "waiting" while the body says it gave up.</summary>
    public static string Subtitle(WindowCardState state, WindowCardTab? tab, DateTimeOffset now) =>
        state switch
        {
            WindowCardState.Loading => "Waiting for quota".Localized(),
            WindowCardState.NoQuotaHistory => "No quota history".Localized(),
            WindowCardState.HistoryFetchFailed => "Quota history could not be read. It will be retried.".Localized(),
            WindowCardState.Idle => "No window running".Localized(),
            WindowCardState.Unplaceable => "Window unavailable".Localized(),
            _ => "Resets in {0}".Localized(UsagePace.DurationText(
                Math.Max(0, (tab!.Active!.ResetAtMs!.Value / 1000.0) - now.ToUnixTimeSeconds()))),
        };

    /// <summary>The body copy for every state that draws no chart. Each one is
    /// its own sentence: "no history at all", "the window ended", and "the
    /// provider gave no reset time" are three different facts, and one shared
    /// line would report two of them as the third.</summary>
    public static string EmptyBody(WindowCardState state) => state switch
    {
        WindowCardState.Loading => "Waiting for quota…".Localized(),
        WindowCardState.NoQuotaHistory =>
            "This window has no recorded quota history, so there is no line to draw.".Localized(),
        WindowCardState.HistoryFetchFailed =>
            "Quota history could not be read. It will be retried.".Localized(),
        WindowCardState.Idle =>
            "The last window ended and nothing has been used since — no window is running."
                .Localized(),
        _ => "This subscription did not report a usable reset time, so the window cannot be placed."
            .Localized(),
    };

    /// <summary>The big number, or the reason there is none. Read off the last
    /// sample INSIDE the window rather than off the live payload, so it agrees
    /// with the dot the curve ends on.</summary>
    public static (string? Percent, string Caption) Headline(
        ChartGeometry geometry, QuotaMetric metric)
    {
        if (geometry.SamplePoints.Count == 0)
        {
            return (null, "No quota reading in this window".Localized());
        }

        var latest = geometry.SamplePoints[^1].Y;
        return (
            ((int)Math.Round(latest, MidpointRounding.AwayFromZero))
                .ToString(System.Globalization.CultureInfo.CurrentCulture) + "%",
            (metric == QuotaMetric.Used ? "used" : "remaining").Localized());
    }

    /// <summary>
    /// The stretches with no quota sample, as a series the chart DRAWS rather
    /// than as the space it leaves between two things it drew.
    /// <para>
    /// Hatch means one thing: no quota reading here, so no line. Both the
    /// pre-sampling stretch and the future qualify, so both get one weight;
    /// whether usage data exists is answered by the bars drawn over it. The
    /// distinction the region carries is "we did not look here", which is not
    /// "nothing happened here" — a gap says the second while meaning the
    /// first.
    /// </para>
    /// <para>Empty pairs are dropped: a window sampled from its first
    /// millisecond has no leading region, and a zero-width hatch is a
    /// rendering artefact rather than a statement.</para>
    /// </summary>
    public static IReadOnlyList<(double From, double To)> NoSampleRegions(ChartGeometry geometry) =>
        [.. new[] { (From: 0.0, To: geometry.FirstSampleX), (From: geometry.NowX, To: 1.0) }
            .Where(region => region.To > region.From)];

    public static string QuotaKey() => "Quota".Localized();

    public static string UsageKey() => "Usage".Localized();

    public static string NoSampleKey() => "No sample".Localized();

    public static string Readings(int count) =>
        "{0} readings".Localized(count);

    public static string MetricLabel(QuotaMetric metric) =>
        (metric == QuotaMetric.Used ? "Used" : "Remaining").Localized();

    /// <summary>Stated, not swallowed. The engine counts rows it could not
    /// place in time precisely so a consumer cannot present a window total as
    /// definitive while omitting them.
    /// <para>Worded as a fact about the SCAN, not about this card's totals: an
    /// undated row has no timestamp, so its window membership does not exist to
    /// be recovered, and "not in these totals" would claim this card had lost a
    /// row that may belong to another subscription entirely.</para></summary>
    public static string? UndatedNote(int undatedCount) =>
        undatedCount > 0
            ? "{0} scanned rows have no usable timestamp, so no window can count them"
                .Localized(undatedCount)
            : null;

    // ── Hover ────────────────────────────────────────────────────────────

    /// <summary><c>HH:mm – HH:mm</c> in the viewer's own zone. macOS's
    /// <c>Format.clockRange</c> is not in this slice's snapshot, so this is the
    /// shape the interval needs rather than a transcription of that
    /// function.</summary>
    public static string ClockRange(long fromMs, long toMs) =>
        $"{Clock(fromMs)} – {Clock(toMs)}";

    private static string Clock(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(
            "HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The quota row of the hover. A zone with no closing sample is
    /// the hatched stretch, and must say so rather than show nothing.</summary>
    public static string ZoneQuota(HitZone zone, QuotaMetric metric) =>
        zone.ClosingSample is { } sample
            ? "Quota {0}% {1}".Localized(
                (int)Math.Round(metric.Value(sample.UsedPercent), MidpointRounding.AwayFromZero),
                (metric == QuotaMetric.Used ? "used" : "remaining").Localized())
            : "No quota reading in this interval".Localized();

    /// <summary>What this interval cost, signed the way the card is currently
    /// read. Null rather than zero when an end has no reading — an interval
    /// nobody measured is not an interval that consumed nothing.</summary>
    public static string? ZoneConsumed(HitZone zone, QuotaMetric metric)
    {
        if (zone.Consumed(metric) is not { } delta)
        {
            return null;
        }

        var rounded = Math.Round(delta, 2, MidpointRounding.AwayFromZero);
        return "{0}{1}% this interval".Localized(
            rounded > 0 ? "+" : string.Empty,
            rounded.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>The tokens and money a zone's own messages carry, or the line
    /// that says it carries none.</summary>
    public static (string? Tokens, string? Money, string? Empty) ZoneUsage(
        IReadOnlyList<WindowMessage> messages)
    {
        if (messages.Count == 0)
        {
            return (null, null, "No usage in this interval".Localized());
        }

        long tokens = 0;
        var cost = 0.0;
        foreach (var message in messages)
        {
            tokens = tokens.SaturatingAdd(message.Tokens);
            cost += message.Cost;
        }

        return ("{0} tokens".Localized(Format.Tokens(tokens, cost)), Format.Money(tokens, cost), null);
    }

    /// <summary>The messages inside one zone. Zone 0 owns its own lower bound,
    /// matching <see cref="WindowCardGeometry.UsageGeometry"/> — without this
    /// the bar drawn from a window-start message and the tooltip explaining
    /// that bar disagree about whether the message is in it.</summary>
    public static IReadOnlyList<WindowMessage> InZone(
        IReadOnlyList<WindowMessage> messages, HitZone zone) =>
        [.. messages.Where(message =>
            (zone.Index == 0
                ? message.Timestamp >= zone.LoMs
                : message.Timestamp > zone.LoMs)
            && message.Timestamp <= zone.HiMs)];
}
