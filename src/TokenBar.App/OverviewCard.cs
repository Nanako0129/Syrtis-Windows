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
/// <para>Hiding individual cards is a separate macOS capability
/// (<c>tokenbar.overview.hidden</c>) that Windows does not have yet. This enum
/// is deliberately only an order, not a visibility model — the seat is here
/// when that slice arrives, but nothing speculative is built into it now.</para>
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
}

internal static class OverviewCards
{
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
}
