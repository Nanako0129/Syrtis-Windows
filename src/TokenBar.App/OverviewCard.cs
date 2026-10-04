using TokenBar.Core;

namespace TokenBar.App;

/// <summary>The pieces the Overview lens shows, in render order.
///
/// <para><b>Declaration order is render order</b>, mirroring macOS's
/// <c>OverviewCard</c> so that "what appears where" has one source rather than
/// two that can disagree. The order itself is not arbitrary and is not ours to
/// choose freely: it is the shipped macOS arrangement, and a Windows build that
/// puts the same cards in a different sequence is a parity gap the eye notices
/// before any feature list does.</para>
///
/// <para>The order is pinned by a test, and that is worth explaining rather
/// than assuming. macOS got this exact sequence wrong once — its own comment
/// records that the commit which claimed to restore the order had two cards
/// the other way round and said so in its message, and that the pinned order
/// was what caught it. Windows arrived at the same mistake independently: the
/// quota summary was first built directly above the limits card, which put the
/// usage chart ahead of it. Repeating a mistake another platform has already
/// paid for and already guarded against is the avoidable kind.</para>
///
/// <para>Hiding is a visibility model layered on top of this order, not part
/// of the enum: <see cref="OverviewCards.Visible"/> filters
/// <see cref="OverviewCards.RenderOrder"/> by the persisted
/// <c>tokenbar.overview.hidden</c> set (macOS <c>OverviewCard.visible</c>,
/// OverviewCard.swift) and by the Agent-limits master switch.</para>
/// </summary>
internal enum OverviewCard
{
    QuotaSummary,
    Chart,
    Limits,
    Trace,
    Models,
    Streaks,
}

/// <summary>
/// What the Overview lens shows for a given client tab (port of the decision
/// macOS's <c>OverviewView.card(_:)</c> makes inline). Extracted to a pure
/// function for the same reason <see cref="OverviewCards"/>'s render order is:
/// <c>DashboardView.xaml.cs</c>, where the lens is actually built, is WinUI
/// and compiled by no test project.
/// <para>
/// Selecting a single client's tab (rather than Overview) scopes the lens to
/// that one client: the quota summary headline and the live-session card both
/// answer "across everything right now", which is not what a single-client
/// tab asked, so both are suppressed there; the Agent-limits card instead
/// narrows to that one client's own window rather than showing every agent's
/// bars underneath a tab that named one of them.
/// </para>
/// </summary>
internal static class OverviewScope
{
    /// <summary>The client this Overview render is scoped to, or null for the
    /// Overview tab itself (every client).</summary>
    internal static string? SingleClient(string activeClientTab) =>
        activeClientTab == ClientRegistry.OverviewTab ? null : activeClientTab;

    internal static bool ShowsQuotaSummary(string? singleClient) => singleClient is null;

    internal static bool ShowsTrace(string? singleClient) => singleClient is null;

    /// <summary>A client tab none of whose clients has local usage records in
    /// range: its chart card says so instead of drawing an empty chart (macOS
    /// OverviewView.swift:101-107, <c>singleClient != nil &amp;&amp; !hasLocalUsage</c>,
    /// where hasLocalUsage asks whether any tab client is among the stats'
    /// present clients). Present clients are raw stripe ids (claude-code)
    /// and tab clients canonical (claude), so the present side is
    /// canonicalized before the test.</summary>
    internal static bool HasNoLocalUsage(
        string? singleClient, IEnumerable<string> tabClients, IReadOnlyList<string> presentClients) =>
        singleClient is not null
        && !tabClients.Intersect(presentClients.Select(ClientRegistry.CanonicalClient)).Any();

    /// <summary>The quota owner of the tab's client, or null on Overview.
    /// Its only production consumer is <see cref="LimitsClients"/>, the one
    /// derivation of the limits card's client set; the card title reads
    /// <see cref="ClientRegistry.TabDisplayName"/> of the tab, not this.
    /// <para>
    /// Mapped through <see cref="ClientRegistry.QuotaOwner"/>, not the raw tab
    /// id: <c>antigravity-cli</c>'s rows arrive keyed <c>antigravity</c>, so
    /// the raw id matched nothing and the card said "No quota data yet". Every
    /// other subscription-facing lookup keys by owner too
    /// (<c>QuotaLensProjection.BuildClient</c>).
    /// </para></summary>
    internal static string? LimitsClientId(string? singleClient) =>
        singleClient is null ? null : ClientRegistry.QuotaOwner(singleClient);

    /// <summary>The Models card's title: "&lt;client&gt; models" on a client
    /// tab (macOS <c>OverviewView.card(.models)</c>), else "Models".</summary>
    internal static string ModelsTitle(string? singleClient) =>
        singleClient is null
            ? "Models".Localized()
            : "{0} models".Localized(ClientRegistry.Style(singleClient).DisplayName);

    /// <summary>Rows the collapsed Models card shows (macOS
    /// <c>ModelBreakdownCard.maxRows</c>).</summary>
    internal const int ModelRowCap = 8;

    /// <summary>How many of <paramref name="count"/> model rows to draw, and
    /// the toggle under them: "Show N more" collapsed, "Show less" expanded,
    /// none when every row already fits (macOS <c>ModelBreakdownCard</c>).</summary>
    internal static (int Shown, string? Toggle) ModelRows(int count, bool expanded)
    {
        var hidden = count - Math.Min(count, ModelRowCap);
        if (hidden == 0)
        {
            return (count, null);
        }

        return expanded
            ? (count, "Show less".Localized())
            : (ModelRowCap, "Show {0} more".Localized(hidden));
    }

    /// <summary>The ONE derivation of the limits card's client set, used by the
    /// Overview lens and the Quota lens alike (the Quota lens once took
    /// <c>TabSlice(client.Owner)</c>, and <c>Owner</c> can be <c>grok-bot</c>,
    /// which dropped grok's rows). Every client id whose rows the limits card
    /// shows: the owner's whole tab group, so the "Grok Build &amp; Bot" tab
    /// carries the <c>grok-bot</c> card beside <c>grok</c> — and with it the
    /// consent prompt, unless Grok Bot's limits are switched off in Settings,
    /// where the Settings switch remains the way to answer. Null on the
    /// Overview tab (every agent).</summary>
    internal static IReadOnlyList<string>? LimitsClients(string? singleClient) =>
        LimitsClientId(singleClient) is { } owner ? ClientRegistry.TabSlice(owner) : null;
}

internal static class OverviewCards
{
    /// <summary>Same key name as macOS (OverviewCard.swift <c>hiddenKey</c>).</summary>
    internal const string HiddenKey = "tokenbar.overview.hidden";

    /// <summary>Same key name as macOS's <c>limitsEnabled</c> default
    /// (SettingsPanel.swift:458-467). Default true: an absent key must not
    /// hide the card for a user who never touched the switch.</summary>
    internal const string LimitsEnabledKey = "tokenbar.limits.enabled";

    /// <summary>Render order, read by <c>DashboardView.BuildOverview</c> and
    /// asserted by <c>OverviewCardTests</c>. Enum declaration order is the
    /// source; this exists so the order can be enumerated and compared rather
    /// than only being implied by a sequence of Add calls that no test can
    /// see.</summary>
    internal static readonly OverviewCard[] RenderOrder =
    [
        OverviewCard.QuotaSummary,
        OverviewCard.Chart,
        OverviewCard.Limits,
        OverviewCard.Trace,
        OverviewCard.Models,
        OverviewCard.Streaks,
    ];

    /// <summary>Every card but the chart (OverviewCard.swift <c>toggleable</c>).
    /// The chart is the fixed anchor: Overview is the fallback every hidden
    /// lens returns to, and a fallback that can be emptied leaves the user
    /// nowhere to land.</summary>
    internal static readonly IReadOnlyList<OverviewCard> Toggleable =
        [.. RenderOrder.Where(c => c != OverviewCard.Chart)];

    /// <summary>The persisted id: the macOS rawValue (camelCase), so the same
    /// preference name carries the same values on both platforms.</summary>
    internal static string Id(OverviewCard card) =>
        char.ToLowerInvariant(card.ToString()[0]) + card.ToString()[1..];

    /// <summary>Sentence-cased label (OverviewCard.swift <c>label</c>, as of
    /// Syrtis #464): a space and a lower-case letter for each interior capital
    /// of the id, first letter upper-cased — "Quota summary", matching macOS's
    /// English text and the key its string catalogs carry.</summary>
    internal static string Label(OverviewCard card) =>
        System.Text.RegularExpressions.Regex.Replace(
            card.ToString(), "(?<!^)([A-Z])", m => " " + char.ToLowerInvariant(m.Value[0]));

    /// <summary>Master gate for the Agent-limits card (macOS
    /// OverviewView.swift:69-90, QuotaView.swift:57 and :116). Every place
    /// that can draw the card asks here, so Overview, the Quota lens and a
    /// client's Quota tab cannot disagree.</summary>
    internal static bool ShowsLimitsCard(bool limitsEnabled) => limitsEnabled;

    /// <summary>Cards to draw, in render order. Anchors survive a tampered
    /// <paramref name="hiddenRaw"/> (OverviewCard.swift <c>visible</c>). With
    /// the limits switch off, BOTH the quota summary line and the limits card
    /// go (OverviewView.swift:69-90): the summary is the one-line answer to
    /// the same question the card answers.</summary>
    internal static List<OverviewCard> Visible(string hiddenRaw, bool limitsEnabled = true)
    {
        var hidden = ClientRegistry.ParseIdSet(hiddenRaw);
        return [.. RenderOrder.Where(c =>
            (!Toggleable.Contains(c) || !hidden.Contains(Id(c)))
            && (ShowsLimitsCard(limitsEnabled) || c is not (OverviewCard.QuotaSummary or OverviewCard.Limits)))];
    }

    internal static bool LimitsEnabled(SettingsStore store) => store.GetBool(LimitsEnabledKey, true);

    internal static List<OverviewCard> Visible(SettingsStore store) =>
        Visible(store.GetString(HiddenKey) ?? string.Empty, LimitsEnabled(store));
}
