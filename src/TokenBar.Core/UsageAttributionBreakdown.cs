using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Pure aggregation for the Stats "API-list-price equivalent" card. Port of
/// TokenBarCore/UsageAttributionBreakdown.swift.
/// </summary>
public static class UsageAttributionBreakdown
{
    public sealed record Row(UsageAttribution.State State, long Tokens, double Cost)
    {
        public string Id => State.Kind switch
        {
            UsageAttribution.StateKind.Assigned => $"assigned:{State.Target}",
            UsageAttribution.StateKind.Excluded => "excluded",
            _ => "unassigned",
        };
    }

    /// <summary>Classify and merge raw provider rows for the selected clients.
    /// Consumes RAW entries (client/provider/model), not
    /// <c>ModelReportFold</c>'s model-level fold: that fold merges providers
    /// back together, which would erase the exact dimension the attribution
    /// declarations resolve against.</summary>
    public static IReadOnlyList<Row> Rows(
        IReadOnlyList<ModelReportEntry> entries,
        IReadOnlyList<string> clientIds,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        // Both sides canonicalised, as the Stats lens's own entry filter does
        // (DashboardView SelectedModelEntries): the report carries raw ids such
        // as claude-code and codex-cli while the selection holds short ids.
        var allowed = new HashSet<string>(
            clientIds.Select(ClientRegistry.CanonicalClient), StringComparer.Ordinal);
        var assigned = new Dictionary<string, (long Tokens, double Cost)>(StringComparer.Ordinal);
        (long Tokens, double Cost) excluded = (0, 0);
        (long Tokens, double Cost) unassigned = (0, 0);

        foreach (var entry in entries)
        {
            if (!allowed.Contains(ClientRegistry.CanonicalClient(entry.Client))
                || (entry.Total == 0 && entry.Cost == 0))
            {
                continue;
            }

            var state = UsageAttribution.Resolve(entry, confirmed);
            switch (state.Kind)
            {
                case UsageAttribution.StateKind.Assigned:
                    var target = state.Target!;
                    var current = assigned.GetValueOrDefault(target);
                    assigned[target] = (
                        current.Tokens.SaturatingAdd(entry.Total), current.Cost + entry.Cost);
                    break;
                case UsageAttribution.StateKind.Excluded:
                    excluded = (excluded.Tokens.SaturatingAdd(entry.Total), excluded.Cost + entry.Cost);
                    break;
                default:
                    unassigned = (
                        unassigned.Tokens.SaturatingAdd(entry.Total), unassigned.Cost + entry.Cost);
                    break;
            }
        }

        var result = new List<Row>();
        foreach (var target in assigned.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            var value = assigned[target];
            if (value.Tokens != 0 || value.Cost != 0)
            {
                result.Add(new Row(UsageAttribution.State.Assigned(target), value.Tokens, value.Cost));
            }
        }

        if (excluded.Tokens != 0 || excluded.Cost != 0)
        {
            result.Add(new Row(UsageAttribution.State.Excluded, excluded.Tokens, excluded.Cost));
        }

        if (unassigned.Tokens != 0 || unassigned.Cost != 0)
        {
            result.Add(new Row(UsageAttribution.State.Unassigned, unassigned.Tokens, unassigned.Cost));
        }

        return result;
    }
}
