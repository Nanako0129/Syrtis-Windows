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
    /// <para>Windows has no macOS known() (placeholder rows or a snapshot,
    /// :437-439), so this rule is an interim one chosen to leave every tab that
    /// existed before the Grok group exactly as main's #181: the members
    /// considered are those with at least one snapshot in
    /// <paramref name="agents"/>; if none has one (loading, failed, not signed
    /// in), the tab's quota owner (<c>clientIds[0]</c>, the first member of
    /// <see cref="ClientRegistry.TabSlice"/>) stands in. The card is hidden iff
    /// every considered member is limits-hidden and none has an extra account.
    /// A single-client tab is therefore main's rule exactly, and the Antigravity
    /// tab hides with antigravity switched off whatever antigravity-cli does
    /// (it has no snapshot and no Settings switch).</para>
    /// <para>Known differences from macOS (not a complete list; reviewed against
    /// AgentLimitsCard.swift): placeholder rows (:228-234, drawn :784-787) for a
    /// codex/claude/gemini/grok member with no snapshot, and grok-bot's
    /// sign-in/loading line (:763-769) — Windows draws both now
    /// (LimitsPlaceholders.Rows), but only once this rule has let the card
    /// exist, so a member whose card this rule drops never shows them; the
    /// :529 branch (no card once answered with
    /// nothing visible); on the Grok tab before any snapshot, Windows follows
    /// the owner (grok) alone, while macOS keeps the card for an unhidden
    /// grok-bot; the opencode tab's routed subscriptions (:421-436); and a
    /// member with only an extra-account snapshot, which macOS's known()
    /// excludes. Align all of these by switching the considered set to the
    /// Core known() from the placeholder-rows slice (G3b-2) once it merges.</para>
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
