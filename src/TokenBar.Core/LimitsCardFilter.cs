using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Which quota cards the Agent-limits card shows (macOS
/// <c>AgentLimitsCard.baseClients</c>, 945dbcc2). The per-client limits toggle
/// (<see cref="ClientRegistry.LimitsHiddenKey"/>) hides that client's PRIMARY
/// card on every surface; an extra account (CLAUDE_CONFIG_DIR, Claude Desktop)
/// keeps its own card, as on macOS. A hidden tab hides every card of that
/// client, but only on the multi-client view: a client tab that is hidden
/// cannot be the one on screen. A tab can hold several clients (the grouped
/// "Grok Build &amp; Bot" tab); a single client is a one-element set.</summary>
public static class LimitsCardFilter
{
    /// <param name="clientIds">The per-client tab's clients (every member of a
    /// grouped tab), or null for the multi-client (Overview) card.</param>
    /// <param name="tabHidden">Tab-hidden ids, folded both ways
    /// (<see cref="ClientRegistry.HiddenTabClients(SettingsStore)"/>).</param>
    /// <param name="limitsHidden">Limits-hidden ids, member-specific
    /// (<see cref="ClientRegistry.HiddenLimitsClients"/>).</param>
    public static IReadOnlyList<AgentUsageSnapshot> Visible(
        IReadOnlyList<AgentUsageSnapshot> agents,
        IReadOnlyList<string>? clientIds,
        IReadOnlySet<string> tabHidden,
        IReadOnlySet<string> limitsHidden) =>
        [.. agents.Where(agent =>
            (clientIds is null || clientIds.Contains(agent.ClientId))
            && !Hides(agent.ClientId, agent.Account.AccountKey is null,
                multiClient: clientIds is null, tabHidden, limitsHidden))];

    /// <summary>The one hide rule for a card, snapshot or placeholder
    /// (<see cref="LimitsPlaceholders.Rows"/> asks it too, so the two cannot
    /// drift): the limits toggle hides a PRIMARY card everywhere; a hidden tab
    /// hides every card of that client on the multi-client card only.</summary>
    public static bool Hides(
        string clientId, bool isPrimary, bool multiClient,
        IReadOnlySet<string> tabHidden, IReadOnlySet<string> limitsHidden) =>
        (isPrimary && limitsHidden.Contains(clientId))
        || (multiClient && tabHidden.Contains(clientId));

    /// <summary>Whether a client tab draws no Agent-limits card at all: the
    /// ONE rule both call sites (Overview lens, Quota lens) ask. Ported from
    /// macOS AgentLimitsCard.swift <c>body</c> (:526-530): the card is not
    /// drawn when
    /// <list type="bullet">
    /// <item><c>allRestrictedClientsHidden</c> (:502-510): the list is
    /// non-empty, every client in it is limits-hidden, and none has an extra
    /// account (an extra account is exempt from the hide, so its row still
    /// renders); decided from settings alone, so a switched-off card goes at
    /// once instead of waiting on the network; or</item>
    /// <item><c>restrict, visibleClients.isEmpty, usageAttempted</c> (:529):
    /// once the quota fetch has been attempted, a card with nothing to draw
    /// (no visible snapshot row and no placeholder row) is not drawn. Before
    /// the attempt it waits and shows its loading state (:547). "Nothing to
    /// draw" is computed from the pieces the card draws with
    /// (<see cref="Visible"/> + <see cref="LimitsPlaceholders.Rows"/>, which
    /// ports <c>baseClients</c> :444-447, <c>known</c> :437-439 and the
    /// placeholders :784-787), so the card and this decision cannot disagree.</item>
    /// </list>
    /// An empty client list hides nothing (:502-504). On the opencode tab the
    /// list the card draws is the routed one (<see cref="OpencodeRoutes"/>,
    /// :421-440), and the switched-off test reads opencode plus the
    /// subscriptions it forwards (<see cref="OpencodeRoutes.HideClients"/>,
    /// macOS #480 <c>allRestrictedClientsHidden</c> :520-545), so switching
    /// off opencode's own card keeps the forwarded cards.</summary>
    /// <param name="restricted">The clients the switched-off test reads
    /// (macOS <c>clients</c>, plus the forwarded subscriptions on the opencode
    /// tab); null = <paramref name="clientIds"/>.
    /// <paramref name="clientIds"/> is the list the card draws.</param>
    /// <param name="attempted">The quota fetch has completed once, successfully
    /// or not (<c>DashboardModel.Snapshot.QuotaAttempted</c>; macOS
    /// <c>usageAttempted</c>).</param>
    public static bool HidesClientCard(
        IReadOnlyList<AgentUsageSnapshot> agents,
        IReadOnlyList<string> clientIds,
        IReadOnlySet<string> limitsHidden,
        bool attempted,
        IReadOnlyList<string>? restricted = null)
    {
        restricted ??= clientIds;
        if (restricted.Count == 0)
        {
            return false;
        }

        if (restricted.All(limitsHidden.Contains)
            && !agents.Any(agent => restricted.Contains(agent.ClientId) && agent.Account.AccountKey is not null))
        {
            return true;
        }

        // A client tab is never the multi-client card, so tab-hidden is moot.
        IReadOnlySet<string> noTabHidden = new HashSet<string>();
        return attempted
            && LimitsPlaceholders.Rows(
                Visible(agents, clientIds, noTabHidden, limitsHidden),
                agents, clientIds, multiClient: false, noTabHidden, limitsHidden).Count == 0;
    }
}
