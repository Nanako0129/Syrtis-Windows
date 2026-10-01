using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>
/// The Quota lens's two cards, assembled from one pass over the persisted
/// series.
/// <para>
/// Pulled out of <c>DashboardView</c> and into the test project's
/// &lt;Compile Include&gt; list for the same reason as
/// <see cref="QuotaLensText"/>: the label join can miss, and what a miss leaves
/// behind is the rule this slice was most at risk of collapsing.
/// </para>
/// </summary>
public static class QuotaLensData
{
    /// <summary>
    /// Summaries, picker windows and grids, keyed by the store's own triple.
    /// <para>
    /// The picker's windows and the strip's summaries are enumerated
    /// independently from the same export, never derived from one another: a
    /// window whose only movement is in the cycle now running has a grid and no
    /// summary, and keying the picker on summaries made that grid unreachable
    /// until the first cycle completed — days, on a weekly window.
    /// </para>
    /// </summary>
    public static (
        IReadOnlyList<QuotaWindowSummary> Summaries,
        IReadOnlyList<QuotaHeatmapWindow> Windows,
        IReadOnlyDictionary<QuotaWindowIdentity, QuotaHeatmap> Grids)
        Build(IReadOnlyList<QuotaHistorySeries>? history, AgentUsagePayload? quota)
    {
        var labels = new WindowLabelJoin(quota);
        var forSummaries =
            new List<(QuotaWindowIdentity Id, string? Label, IReadOnlyList<QuotaCycle> Cycles)>();
        var forWindows = new List<(QuotaWindowIdentity Id, string? Label, QuotaHeatmap Grid)>();
        // Keyed by the identity record, not by "client|window": two accounts of
        // one client can hold the same window, and a key that dropped the scope
        // would let one of their grids overwrite the other's.
        var grids = new Dictionary<QuotaWindowIdentity, QuotaHeatmap>();
        foreach (var series in history ?? [])
        {
            var id = new QuotaWindowIdentity(
                series.ProviderId, series.AccountScope, series.WindowKey);
            // Null when the join found no live window — NOT pre-filled with the
            // WindowKey here. The fallback belongs to QuotaLabels, where "never
            // a trailing separator" is stated and asserted; applying it at this
            // seam would collapse "no label" into "labelled with its own key"
            // in the data layer, where nothing downstream could tell them apart
            // again.
            var label = labels.Lookup(series);
            var grid = QuotaHeatmapFold.Build(series.Samples);
            grids[id] = grid;
            forWindows.Add((id, label, grid));
            forSummaries.Add((id, label, QuotaHistoryFold.Cycles(series.Samples)));
        }

        return (
            QuotaOverviewFold.Summaries(forSummaries),
            QuotaOverviewFold.HeatmapWindows(forWindows),
            grids);
    }

    /// <summary>The label join from a stored series to a live window.
    /// <para>A client with ONE card in the payload (every provider but
    /// Claude, and Claude without extra accounts) joins exactly as it always
    /// did: <c>(client, PaceStatus.WindowKey)</c>, scope ignored.</para>
    /// <para>A client with several cards joins by the one history rule: the
    /// snapshot with the same <c>ProviderId</c> AND
    /// <c>AccountScope == HistoryScope.Scope</c>, then the window key. A
    /// non-primary account's label is prefixed with its
    /// <see cref="AccountLabel"/>. A series with no exact match gets no live
    /// label (the caller's derived fallback) — never another account's.</para>
    /// A miss leaves null rather than dropping the row.</summary>
    private sealed class WindowLabelJoin
    {
        private readonly HashSet<string> _multi = [];
        private readonly Dictionary<(string Client, string Window), string> _byClient = [];
        private readonly Dictionary<(string Client, string Scope, string Window), string> _byScope = [];

        public WindowLabelJoin(AgentUsagePayload? quota)
        {
            var agents = quota?.Agents ?? [];
            foreach (var group in agents.GroupBy(a => a.ClientId).Where(g => g.Count() > 1))
            {
                _multi.Add(group.Key);
            }

            foreach (var agent in agents)
            {
                var multi = _multi.Contains(agent.ClientId);
                var account = agent.Account;
                if (multi && agent.HistoryScope?.Scope is null)
                {
                    continue;
                }

                foreach (var window in agent.UniqueCardWindows)
                {
                    if (window.PaceStatus.WindowKey is not { } key)
                    {
                        continue;
                    }

                    if (multi)
                    {
                        _byScope.TryAdd(
                            (agent.ClientId, agent.HistoryScope!.Scope!, key),
                            account.AccountKey is null
                                ? window.Label
                                : $"{AccountLabel.Of(account, quota)} · {window.Label}");
                    }
                    else
                    {
                        _byClient.TryAdd((agent.ClientId, key), window.Label);
                    }
                }
            }
        }

        public string? Lookup(QuotaHistorySeries series) =>
            _multi.Contains(series.ProviderId)
                ? _byScope.GetValueOrDefault((series.ProviderId, series.AccountScope, series.WindowKey))
                : _byClient.GetValueOrDefault((series.ProviderId, series.WindowKey));
    }
}
