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

    /// <summary>Whether a client tab draws no Agent-limits card at all (macOS
    /// <c>allRestrictedClientsHidden</c>): the user switched the client's card
    /// off and it has no extra account to keep showing. Read from the setting,
    /// not inferred from an empty <see cref="Visible"/> list, which is also
    /// empty before the first quota payload arrives; that card must keep its
    /// loading state, while a hidden one must not claim to be loading.
    /// Grouped tab rule, as macOS: the card is hidden only when EVERY member is
    /// switched off AND no member has an extra account
    /// (<c>AgentLimitsCard.allRestrictedClientsHidden</c>, AgentLimitsCard.swift
    /// :502-510; per-member row filtering <c>baseClients</c> :402-413, :445-447;
    /// Settings toggles each id on its own, SettingsPanel.swift :477-503). One
    /// visible member keeps the card, which then lists just that member's
    /// rows.    /// <para>An empty <paramref name="clientIds"/> hides nothing
    /// (<c>guard restrict, !clients.isEmpty else { return false }</c>,
    /// AgentLimitsCard.swift:502-504); a vacuous <c>All</c> would hide it.</para></summary>
    public static bool HidesClientCard(
        IReadOnlyList<AgentUsageSnapshot> agents,
        IReadOnlyList<string> clientIds,
        IReadOnlySet<string> limitsHidden) =>
        clientIds.Count > 0
        && clientIds.All(clientId =>
            limitsHidden.Contains(clientId)
            && !agents.Any(agent => agent.ClientId == clientId && agent.Account.AccountKey is not null));

    /// <summary>Whether a client tab draws no Agent-limits card: either
    /// <see cref="HidesClientCard"/>, or macOS's second empty branch,
    /// <c>restrict, visibleClients.isEmpty, usageAttempted → EmptyView</c>
    /// (AgentLimitsCard.swift:529): nothing is left to list once the per-member
    /// toggle is applied and the fetch has been answered. That is the
    /// Antigravity tab with antigravity hidden (antigravity-cli has no snapshot
    /// and can never be hidden, so the first rule never fires) and a Grok user
    /// with no Grok Bot snapshot who hides grok. Before the first answer
    /// (<see cref="WindowEquivalence.FetchOutcome.NotAttempted"/>) an empty
    /// list is "still loading" and keeps its card. Not for the Overview card
    /// (<paramref name="clientIds"/> null), which says "No supported agents
    /// yet".</summary>
    public static bool HidesClientCard(
        IReadOnlyList<AgentUsageSnapshot> agents,
        IReadOnlyList<string> clientIds,
        IReadOnlySet<string> limitsHidden,
        WindowEquivalence.FetchOutcome outcome) =>
        HidesClientCard(agents, clientIds, limitsHidden)
        || (clientIds.Count > 0
            && outcome != WindowEquivalence.FetchOutcome.NotAttempted
            && Visible(agents, clientIds, new HashSet<string>(), limitsHidden).Count == 0);
}
