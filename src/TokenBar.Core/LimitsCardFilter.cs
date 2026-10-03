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
            (clientIds is null ? !tabHidden.Contains(agent.ClientId) : clientIds.Contains(agent.ClientId))
            && !(agent.Account.AccountKey is null && limitsHidden.Contains(agent.ClientId)))];

    /// <summary>Whether a client tab draws no Agent-limits card at all: the
    /// ONE rule both call sites (Overview lens, Quota lens) ask.
    /// <para>macOS procedure (AgentLimitsCard.swift): a restricted card lists
    /// <c>clients.filter(known)</c> (<c>baseClients</c> :444-447), where
    /// <c>known(id)</c> is <c>placeholderRows[id] != nil || snapshots[primary(id)]
    /// != nil</c> (:437-439; <c>placeholderRows</c> :228-234); the per-member
    /// limits toggle then drops hidden primaries, except that an extra account
    /// always stays (<c>expandedWithExtraAccounts</c>). The card is not drawn
    /// when <c>allRestrictedClientsHidden</c> (:502-510: every member hidden and
    /// none with an extra account) or when <c>restrict, visibleClients.isEmpty,
    /// usageAttempted</c> (:529); a card with no rows yet and no answer waits
    /// (:547), and a known client with no snapshot draws placeholder rows
    /// (:784-787). An empty client list hides nothing (:502-504).</para>
    /// <para>Windows has no placeholder rows, so a member with no snapshot can
    /// never be shown. Rule here: the members considered are those with at
    /// least one snapshot in <paramref name="agents"/>; if none has one (still
    /// loading, failed, or not signed in), the tab's quota owner (the first
    /// client id) stands in for the tab. The card is hidden iff every
    /// considered member is limits-hidden and none has an extra account. So a
    /// single-client tab behaves exactly as main's #181 (the switch hides the
    /// card at once, data or not), and an Antigravity tab with antigravity
    /// hidden is hidden even though antigravity-cli never has a snapshot.
    /// Known differences from macOS, all from the missing placeholder rows
    /// (:228-234, :784-787): a codex/claude tab not switched off and with no
    /// snapshot draws "No quota data yet." where macOS draws placeholder rows;
    /// a Grok tab with grok hidden and no grok-bot snapshot is hidden where
    /// macOS draws a grok-bot placeholder row; a tab not switched off whose
    /// members are all absent from an answered payload keeps "No quota data
    /// yet." where macOS draws nothing (:529). To align once Windows has
    /// placeholder rows (G3b): switch the considered set to macOS's known()
    /// (placeholder or snapshot) and port :529.</para>
    /// <para>Read from the setting plus the payload, not from an empty
    /// <see cref="Visible"/> list, which is also empty before the first payload;
    /// that card must keep its loading state.</para></summary>
    public static bool HidesClientCard(
        IReadOnlyList<AgentUsageSnapshot> agents,
        IReadOnlyList<string> clientIds,
        IReadOnlySet<string> limitsHidden)
    {
        if (clientIds.Count == 0)
        {
            return false;
        }

        var withSnapshot = clientIds.Where(id => agents.Any(agent => agent.ClientId == id)).ToList();
        IReadOnlyList<string> members = withSnapshot.Count > 0 ? withSnapshot : [clientIds[0]];
        return members.All(id =>
                limitsHidden.Contains(id)
                && !agents.Any(agent => agent.ClientId == id && agent.Account.AccountKey is not null));
    }
}
