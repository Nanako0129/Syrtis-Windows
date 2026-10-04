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

    /// <summary>What the menu-bar page's quota choices depend on: the
    /// selection keys, in order. Labels are left out: a label change alone
    /// (a plan rename) is not worth rebuilding a page under the user.</summary>
    public static string MenuBarKey(AgentUsagePayload? payload) =>
        string.Join('\n', Of(payload).Select(choice => choice.Selection));

    /// <summary>What the dashboard page's quota-derived rows depend on: the
    /// configured quota clients (client tabs) and the reporting agents'
    /// client ids (per-client limits switches).</summary>
    public static string DashboardKey(AgentUsagePayload? payload) =>
        string.Join(',', payload?.ConfiguredClientIds ?? [])
        + "|"
        + string.Join(',', (payload?.Agents ?? []).Select(agent => agent.ClientId).Distinct());
}
