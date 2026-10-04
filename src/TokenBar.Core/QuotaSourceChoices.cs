using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>The Settings "Quota source" choices a payload offers: Auto, then
/// every window of every agent that reported without an error. One list for
/// the radio group and for the key Settings compares to decide whether an
/// open page must be rebuilt, so the two cannot disagree.</summary>
public static class QuotaSourceChoices
{
    public static IReadOnlyList<(string Selection, string Label)> Of(AgentUsagePayload? payload)
    {
        var choices = new List<(string, string)> { (QuotaResolver.Auto, "Auto (tightest window)".Localized()) };
        if (payload is null)
        {
            return choices;
        }

        foreach (var agent in payload.Agents.Where(a => a.Error is null))
        {
            foreach (var window in agent.UniqueCardWindows)
            {
                choices.Add((
                    QuotaResolver.Selection(agent.ClientId, window.CardId, agent.Account.AccountKey),
                    $"{AccountLabel.Of(agent, payload)} · {window.Label.Localized()}"));
            }
        }

        return choices;
    }

    /// <summary>The persisted selection as a disabled, checked row when the
    /// payload does not offer it (its agent errored or is gone, e.g. a
    /// last-good that lived only in memory before a restart), so the user can
    /// still see what is selected. With no payload yet (before the first
    /// fetch, or while every fetch fails) nothing is known to be unavailable,
    /// so the window reads "—", as macOS shows it with no agent. Null for
    /// Auto or an offered selection. The
    /// persisted value is never rewritten. Port of macOS SettingsPanel
    /// (availableClientIds + [selected], "Unavailable selection").</summary>
    public static (string Selection, string Label)? Unavailable(
        AgentUsagePayload? payload, string selection)
    {
        if (selection == QuotaResolver.Auto
            || Of(payload).Any(choice => choice.Selection == selection))
        {
            return null;
        }

        var separator = selection.IndexOf('|');
        var clientId = separator < 0 ? selection : selection[..separator];
        var window = payload is null ? "—" : "Unavailable selection".Localized();
        return (selection, $"{ClientRegistry.Style(clientId).DisplayName} · {window}");
    }

    /// <summary>The selection keys a payload offers.</summary>
    public static IReadOnlySet<string> Selections(AgentUsagePayload? payload) =>
        Of(payload).Select(choice => choice.Selection).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="payload"/> offers a choice the page was
    /// not built with — the only change worth rebuilding an open page for. A
    /// choice that disappears does not count: an agent that errors on one
    /// poll and recovers on the next would otherwise rebuild the page (losing
    /// focus and scroll) on every poll, and its option stays listed until the
    /// page is next built. Labels are left out for the same reason.</summary>
    public static bool OffersNewChoice(IReadOnlySet<string>? built, AgentUsagePayload? payload) =>
        built is null || Of(payload).Any(choice => !built.Contains(choice.Selection));

    /// <summary>What the dashboard page's quota-derived rows depend on: the
    /// configured quota clients (client tabs) and the reporting agents'
    /// client ids (per-client limits switches).</summary>
    public static string DashboardKey(AgentUsagePayload? payload) =>
        string.Join(',', payload?.ConfiguredClientIds ?? [])
        + "|"
        + string.Join(',', (payload?.Agents ?? []).Select(agent => agent.ClientId).Distinct());
}
