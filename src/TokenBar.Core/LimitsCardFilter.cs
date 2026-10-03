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
    /// never be shown. Rule here: consider only the members that have at least
    /// one snapshot in <paramref name="agents"/>; hide the card iff that set is
    /// non-empty, every member in it is limits-hidden, and none has an extra
    /// account. No member with a snapshot (still loading, failed, or a client
    /// with no data) returns false, so the card keeps its own "No quota data
    /// yet." / could-not-check states. Where macOS draws placeholder rows,
    /// Windows differs: a codex/claude tab with no snapshot draws the card's
    /// own "No quota data yet." (macOS: placeholder rows), and a Grok tab with
    /// grok hidden and no grok-bot snapshot is hidden here, while macOS draws a
    /// grok-bot placeholder row (:228-234, :784-787). Every other case matches. To align after G3b lands: once Windows has placeholder rows,
    /// switch the member set from "members with a snapshot" to macOS's known()
    /// (placeholder or snapshot).</para>
    /// <para>Read from the setting plus the payload, not from an empty
    /// <see cref="Visible"/> list, which is also empty before the first payload;
    /// that card must keep its loading state.</para></summary>
    public static bool HidesClientCard(
        IReadOnlyList<AgentUsageSnapshot> agents,
        IReadOnlyList<string> clientIds,
        IReadOnlySet<string> limitsHidden)
    {
        var members = clientIds.Where(id => agents.Any(agent => agent.ClientId == id)).ToList();
        return members.Count > 0
            && members.All(id =>
                limitsHidden.Contains(id)
                && !agents.Any(agent => agent.ClientId == id && agent.Account.AccountKey is not null));
    }
}
