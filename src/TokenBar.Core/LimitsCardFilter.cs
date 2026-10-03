using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Which quota cards the Agent-limits card shows (macOS
/// <c>AgentLimitsCard.baseClients</c>, 945dbcc2). The per-client limits toggle
/// (<see cref="ClientRegistry.LimitsHiddenKey"/>) hides that client's PRIMARY
/// card on every surface; an extra account (CLAUDE_CONFIG_DIR, Claude Desktop)
/// keeps its own card, as on macOS. A hidden tab hides every card of that
/// client, but only on the multi-client view: a client tab that is hidden
/// cannot be the one on screen.</summary>
public static class LimitsCardFilter
{
    /// <param name="clientId">The per-client tab's client, or null for the
    /// multi-client (Overview) card.</param>
    /// <param name="tabHidden">Tab-hidden ids, folded both ways
    /// (<see cref="ClientRegistry.HiddenTabClients(SettingsStore)"/>).</param>
    /// <param name="limitsHidden">Limits-hidden ids, member-specific
    /// (<see cref="ClientRegistry.HiddenLimitsClients"/>).</param>
    public static IReadOnlyList<AgentUsageSnapshot> Visible(
        IReadOnlyList<AgentUsageSnapshot> agents,
        string? clientId,
        IReadOnlySet<string> tabHidden,
        IReadOnlySet<string> limitsHidden) =>
        [.. agents.Where(agent =>
            (clientId is null ? !tabHidden.Contains(agent.ClientId) : agent.ClientId == clientId)
            && !(agent.Account.AccountKey is null && limitsHidden.Contains(agent.ClientId)))];

    /// <summary>Whether a client tab draws no Agent-limits card at all (macOS
    /// <c>allRestrictedClientsHidden</c>): the user switched the client's card
    /// off and it has no extra account to keep showing. Read from the setting,
    /// not inferred from an empty <see cref="Visible"/> list, which is also
    /// empty before the first quota payload arrives; that card must keep its
    /// loading state, while a hidden one must not claim to be loading.</summary>
    public static bool HidesClientCard(
        IReadOnlyList<AgentUsageSnapshot> agents,
        string clientId,
        IReadOnlySet<string> limitsHidden) =>
        limitsHidden.Contains(clientId)
        && !agents.Any(agent => agent.ClientId == clientId && agent.Account.AccountKey is not null);
}
