using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Daily token and cost points grouped by attribution bucket and model. Port of
/// TokenBarCore/AttributedDailySeries.swift.
/// </summary>
public static class AttributedDailySeries
{
    public sealed record Point(
        string Date,
        UsageAttribution.State State,
        string Model,
        long Tokens,
        double Cost);

    private readonly record struct Key(string Date, UsageAttribution.State State, string Model)
    {
        public string StateId => StateIdFor(State);
    }

    /// <summary>Fold each graph client row into its date, attribution state and
    /// model bucket. Confirmed records are the only declarations read — a
    /// suggestion is a proposal, not a classification.</summary>
    public static IReadOnlyList<Point> Points(
        IReadOnlyList<Contribution> contributions,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var rows = new List<(Key Key, long Tokens, double Cost)>();
        foreach (var contribution in contributions)
        {
            foreach (var client in contribution.Clients)
            {
                var tokens = client.Tokens.Total;
                if (tokens == 0 && client.Cost == 0)
                {
                    continue;
                }

                // A merged provider id names more than one provider, so no
                // declaration about a single one can speak for the row.
                var state = client.ProviderId.Contains(',')
                    ? UsageAttribution.State.Unassigned
                    : UsageAttribution.Resolve(
                        client.Client, client.ProviderId, client.ModelId, confirmed);
                // Attribution matched the raw id above; the bucket groups by
                // display identity so it agrees with the Models view.
                rows.Add((
                    new Key(contribution.Date, state, ModelGrouping.GroupId(client.ModelId)),
                    tokens,
                    client.Cost));
            }
        }

        // Canonicalize the fold order so permuting graph rows cannot change
        // floating-point or saturating-integer accumulation.
        rows.Sort((left, right) =>
        {
            var date = string.CompareOrdinal(left.Key.Date, right.Key.Date);
            if (date != 0)
            {
                return date;
            }

            var state = string.CompareOrdinal(left.Key.StateId, right.Key.StateId);
            if (state != 0)
            {
                return state;
            }

            var model = string.CompareOrdinal(left.Key.Model, right.Key.Model);
            if (model != 0)
            {
                return model;
            }

            var tokens = left.Tokens.CompareTo(right.Tokens);
            return tokens != 0 ? tokens : left.Cost.CompareTo(right.Cost);
        });

        var buckets = new Dictionary<Key, (long Tokens, double Cost)>();
        foreach (var row in rows)
        {
            var value = buckets.GetValueOrDefault(row.Key);
            buckets[row.Key] = (value.Tokens.SaturatingAdd(row.Tokens), value.Cost + row.Cost);
        }

        return buckets.Keys
            .OrderBy(key => key.Date, StringComparer.Ordinal)
            .ThenBy(key => key.StateId, StringComparer.Ordinal)
            .ThenBy(key => key.Model, StringComparer.Ordinal)
            .Select(key => (Key: key, Value: buckets[key]))
            .Where(entry => entry.Value.Tokens != 0 || entry.Value.Cost != 0)
            .Select(entry => new Point(
                entry.Key.Date,
                entry.Key.State,
                entry.Key.Model,
                entry.Value.Tokens,
                entry.Value.Cost))
            .ToList();
    }

    private static string StateIdFor(UsageAttribution.State state) => state.Kind switch
    {
        UsageAttribution.StateKind.Assigned => $"assigned:{state.Target}",
        UsageAttribution.StateKind.Excluded => "excluded",
        _ => "unassigned",
    };
}
