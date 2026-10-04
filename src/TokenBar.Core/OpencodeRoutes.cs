using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>The opencode tab's routed-subscription limits cards. opencode is
/// primarily a router: its client view shows the cards of the subscriptions it
/// is authed against (payload <c>opencodeSubscriptions</c>), led by its own
/// OpenCode Go card when that has a snapshot. Ported from macOS
/// AgentLimitsCard.swift (<c>opencodeSubs</c> :322, <c>opencodeView</c> :328,
/// <c>baseClients</c> opencode branch :421-440, <c>opencodeCardClients</c>
/// :364-366, header :537-543, empty state :559-565). The data flow: the Rust
/// engine reports the labels; this file resolves them to clients and decides
/// the list the card draws, and the card's own hide rules
/// (<see cref="LimitsCardFilter"/>, <see cref="LimitsPlaceholders"/>) then run
/// over that list exactly as for any restricted card.</summary>
public static class OpencodeRoutes
{
    public const string ClientId = "opencode";

    /// <summary>macOS <c>opencodeView = restrict &amp;&amp; clients.contains("opencode")</c>
    /// (:328): a restricted (client-tab) card whose tab holds opencode.</summary>
    public static bool IsView(IReadOnlyList<string> tabClients) => tabClients.Contains(ClientId);

    /// <summary>macOS <c>opencodeCardClients</c> (:364-366): opencode's own card
    /// first when it has a quota, then the subscriptions, never duplicating
    /// opencode (a routed label can resolve back to it).</summary>
    public static IReadOnlyList<string> CardClients(bool ownQuotaPresent, IEnumerable<string> subscriptions) =>
    [
        .. ownQuotaPresent ? [ClientId] : Array.Empty<string>(),
        .. subscriptions.Where(id => id != ClientId),
    ];

    /// <summary>macOS <c>baseClients</c> opencode branch (:421-440) before the
    /// hide filter: labels resolve through the subscription-owner mapping
    /// (not the raw label mapper: <c>Xai</c> is keyed <c>grok</c> in snapshots),
    /// keep those with a primary snapshot, then <see cref="CardClients"/>.</summary>
    public static IReadOnlyList<string> Clients(
        IReadOnlyList<AgentUsageSnapshot> agents, IEnumerable<string>? subscriptionLabels)
    {
        bool HasPrimary(string id) => agents.Any(a => a.ClientId == id && a.Account.AccountKey is null);
        var subs = (subscriptionLabels ?? [])
            .Select(UsageAttributionSettings.SubscriptionClientForLabel)
            .OfType<string>()
            .Where(HasPrimary);
        return CardClients(HasPrimary(ClientId), subs);
    }

    /// <summary>The clients the limits card draws for a tab: the tab's own
    /// clients, or on the opencode tab the routed list.</summary>
    public static IReadOnlyList<string> LimitsClients(IReadOnlyList<string> tabClients, AgentUsagePayload? quota) =>
        IsView(tabClients) ? Clients(quota?.Agents ?? [], quota?.OpencodeSubscriptions) : tabClients;

    /// <summary>The line under the card header (:537-543): "↔ Routes through
    /// opencode" on the opencode view; on the multi-client card with routed
    /// subscriptions, "opencode also taps: A · B"; otherwise none.</summary>
    public static string? HeaderLine(bool opencodeView, bool restricted, IReadOnlyList<string> subscriptions) =>
        opencodeView ? "↔ Routes through opencode".Localized()
        : !restricted && subscriptions.Count > 0
            ? "opencode also taps: {0}".Localized(string.Join(" · ", subscriptions))
        : null;

    /// <summary>The text for a card with nothing visible after the fetch was
    /// attempted (:559-565): the labels on the opencode view when it has
    /// any, else "No supported agents yet".</summary>
    public static string EmptyText(bool opencodeView, IReadOnlyList<string> subscriptions) =>
        opencodeView && subscriptions.Count > 0
            ? "Subscriptions: {0}".Localized(string.Join(" · ", subscriptions))
            : "No supported agents yet".Localized();
}
