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

    /// <summary>The clientId <c>BuildLimits</c> should restrict its rows to,
    /// or null to show every agent (Overview tab).
    /// <para>
    /// Mapped through <see cref="ClientRegistry.QuotaOwner"/>, not the raw tab
    /// id: <c>BuildLimits</c> filters on <c>agent.ClientId == clientId</c>
    /// against the quota payload (every account of that client passes), and a client that spends another
    /// subscription's allowance is keyed there under the owner.
    /// <c>antigravity-cli</c> is the one such client today — its rows arrive as
    /// <c>antigravity</c>, so passing the tab id straight through matched
    /// nothing and the card said "No quota data yet" while the quota was
    /// sitting in the payload. Every other subscription-facing lookup already
    /// keys by owner (see <c>QuotaLensProjection.BuildClient</c>); this is the
    /// same rule, and it belongs here rather than at the call site so the
    /// Overview cannot apply it differently from the Quota lens.
    /// </para></summary>
    internal static string? LimitsClientId(string? singleClient) =>
        singleClient is null ? null : ClientRegistry.QuotaOwner(singleClient);

    /// <summary>Every client id whose rows the limits card shows: the owner's
    /// whole tab group, so the "Grok Build &amp; Bot" tab carries the
    /// <c>grok-bot</c> card (and its consent prompt) beside <c>grok</c>. Null
    /// on the Overview tab (every agent).</summary>
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
