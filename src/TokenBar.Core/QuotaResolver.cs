using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Carries the picked snapshot itself, not just its ClientId: two
/// snapshots share a ClientId when one client has several accounts, and a
/// caller that re-searched <c>payload.Agents</c> by ClientId (as
/// <c>QuotaSelectionPolicy.ResolvedAt</c> used to) could match the wrong
/// one. <see cref="AccountKey"/> is the card's account within
/// <see cref="ClientId"/> (null = primary); (ClientId, AccountKey) is the
/// card's identity.</summary>
public sealed record QuotaPick(AgentUsageSnapshot Agent, UsageWindow Window)
{
    public string ClientId => Agent.ClientId;
    public string? AccountKey => Agent.Account.AccountKey;
}

/// <summary>
/// Picks which quota window the tray displays (port of
/// TokenBarCore/QuotaResolver.swift). The selection string is "auto" (the
/// tightest window — lowest remaining percent — across every agent) or
/// "&lt;clientId&gt;|&lt;cardId&gt;" for an explicit pick of the primary
/// account, or "&lt;clientId&gt;|&lt;cardId&gt;|&lt;accountKey&gt;" for another
/// account's card (an account key never contains '|'; a build that predates
/// accounts finds no card by that string and simply shows nothing). The
/// primary form is byte-identical to what earlier builds persisted.
/// </summary>
public static class QuotaResolver
{
    public const string Auto = "auto";

    /// <summary>Builds the canonical persisted selection for one quota card.</summary>
    public static string Selection(string clientId, string cardId, string? accountKey = null) =>
        AccountIdentity.Normalize(accountKey) is { } key
            ? $"{clientId}|{cardId}|{key}"
            : $"{clientId}|{cardId}";

    /// <summary>
    /// Canonicalizes a persisted selection against the current payload. Empty,
    /// Auto, and malformed explicit selections become Auto. A legacy label is
    /// migrated only when it identifies exactly one unique card; unmatched
    /// explicit selections remain unchanged.
    /// </summary>
    public static string CanonicalSelection(AgentUsagePayload? payload, string selection)
    {
        var parsed = ParseExplicitSelection(selection);
        if (parsed is null)
        {
            return Auto;
        }

        if (payload is null)
        {
            return selection;
        }

        var (agent, value) = Locate(payload, parsed.Value);
        if (agent is null)
        {
            return selection;
        }

        // The RAW card view: a persisted pre-v3 selection holds the label the
        // provider sent, and whether that label was ambiguous is a fact about
        // what the provider sent. UniqueCardWindows qualifies a repeated
        // label with its window's duration, which can leave exactly one
        // window still carrying the raw text and migrate a selection that
        // used to match two. Card IDs are identical in both views, so the
        // value this returns is unchanged for every selection that was
        // already unique.
        var windows = agent.RawCardWindows;
        var exact = windows.FirstOrDefault(w => w.CardId == value);
        if (exact is not null)
        {
            return Selection(agent.ClientId, exact.CardId, agent.Account.AccountKey);
        }

        var labelMatches = windows.Where(w => w.Label == value).ToArray();
        return labelMatches.Length == 1
            ? Selection(agent.ClientId, labelMatches[0].CardId, agent.Account.AccountKey)
            : selection;
    }

    /// <summary><paramref name="excluding"/> is the set of client ids to skip in
    /// AUTO mode only (the user's tab-hidden ∪ limits-hidden clients) — so the
    /// menu-bar quota can't surface a client the popover hides. An EXPLICIT
    /// <c>clientId|cardId</c> selection is always honored, even for an excluded
    /// client (the user deliberately picked it as the tray source). Null/empty
    /// set = pre-hide behavior, byte-identical.</summary>
    public static QuotaPick? Resolve(
        AgentUsagePayload? payload, string selection, IReadOnlySet<string>? excluding = null)
    {
        if (payload is null)
        {
            return null;
        }

        var canonical = CanonicalSelection(payload, selection);
        if (canonical == Auto)
        {
            return AutoCandidate(payload, excluding);
        }

        var parsed = ParseExplicitSelection(canonical);
        if (parsed is null)
        {
            return null;
        }

        var (agent, value) = Locate(payload, parsed.Value);
        var window = agent?.UniqueCardWindows.FirstOrDefault(w => w.CardId == value);
        return window is null ? null : new QuotaPick(agent!, window);
    }

    /// <summary>True when <see cref="Resolve"/> returned null ONLY because the
    /// exclusion removed every otherwise-resolvable auto candidate (there IS a
    /// healthy window, but all of them belong to excluded clients). Lets a caller
    /// distinguish "all candidates hidden" from "no payload / fetch failed / no
    /// healthy window": in the former it must suppress a stale cache fallback
    /// (the hidden client's last reading) rather than keep showing it. Only
    /// meaningful for the auto/empty selection — an explicit pick ignores the
    /// exclusion, so this returns false for it (and for an empty exclusion or no
    /// payload).</summary>
    public static bool ExcludedAllCandidates(
        AgentUsagePayload? payload, string selection, IReadOnlySet<string> excluding)
    {
        if (payload is null || excluding.Count == 0)
        {
            return false;
        }

        if (CanonicalSelection(payload, selection) != Auto)
        {
            return false;
        }

        if (AutoCandidate(payload, excluding: null) is null)
        {
            return false;
        }

        return AutoCandidate(payload, excluding) is null;
    }

    private static QuotaPick? AutoCandidate(
        AgentUsagePayload payload, IReadOnlySet<string>? excluding)
    {
        QuotaPick? best = null;
        foreach (var agent in payload.Agents)
        {
            if (agent.Error is not null || excluding?.Contains(agent.ClientId) == true)
            {
                continue;
            }

            foreach (var window in agent.UniqueCardWindows)
            {
                if (!double.IsFinite(window.RemainingPercent))
                {
                    continue;
                }

                if (best is null || window.RemainingPercent < best.Window.RemainingPercent)
                {
                    best = new QuotaPick(agent, window);
                }
            }
        }

        return best;
    }

    /// <summary>The card with this exact (client, account) identity.</summary>
    private static AgentUsageSnapshot? Find(
        AgentUsagePayload payload, string clientId, string? accountKey) =>
        payload.Agents.FirstOrDefault(a =>
            a.ClientId == clientId && a.Account.AccountKey == accountKey);

    private static (string ClientId, string Value)? ParseExplicitSelection(string raw)
    {
        if (raw.Length == 0 || raw == Auto)
        {
            return null;
        }

        var separator = raw.IndexOf('|');
        if (separator < 0)
        {
            return null;
        }

        var clientId = raw[..separator];
        var value = raw[(separator + 1)..];
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(value)
            ? null
            : (clientId, value);
    }

    /// <summary>Finds the card a parsed selection names, and the card-id (or
    /// legacy label) part of it. Card ids may themselves contain '|'
    /// (<c>model.gpt|preview.v1</c>), so "cardId|accountKey" cannot be split
    /// blindly: an account key never contains '|', which makes the text after
    /// the LAST '|' the account key exactly when a non-primary card of that
    /// client carries it; otherwise the whole rest is the primary card's
    /// id.</summary>
    private static (AgentUsageSnapshot? Agent, string Value) Locate(
        AgentUsagePayload payload, (string ClientId, string Value) parsed)
    {
        var last = parsed.Value.LastIndexOf('|');
        if (last > 0 && last < parsed.Value.Length - 1)
        {
            var extra = Find(payload, parsed.ClientId, parsed.Value[(last + 1)..]);
            if (extra is not null)
            {
                return (extra, parsed.Value[..last]);
            }
        }

        return (Find(payload, parsed.ClientId, null), parsed.Value);
    }
}
