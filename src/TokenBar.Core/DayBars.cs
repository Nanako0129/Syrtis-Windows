using TokenBar.Interop;

namespace TokenBar.Core;

// Stacked-bar series for the Token Usage chart, port of
// TokenBarCore/DayBars.swift (originally the grouping logic in
// UsageBarGraph2D.tsx). UI-free so the builder is unit-testable; the chart
// just renders the result.

public enum StackBy
{
    Model,
    Agent,
}

/// <summary>Whether bar length encodes token count or spend.</summary>
public enum ChartMetric
{
    Tokens,
    Cost,
}

// Deliberately a class, not a record: the builder and legend accumulate into
// Tokens/Cost, and a mutable record's synthesized value equality/hash would be
// a trap the moment one lands in a set or gets diffed.
public sealed class DaySegment(string key, string label, string color)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    /// <summary>Hex color (provider shade for model stacking, brand color for
    /// agent).</summary>
    public string Color { get; } = color;
    public long Tokens { get; set; }
    public double Cost { get; set; }
}

public sealed record DayBar(string Date, IReadOnlyList<DaySegment> Segments)
{
    public long TotalTokens => Segments.Aggregate(0L, (acc, s) => acc.SaturatingAdd(s.Tokens));
    public double TotalCost => Segments.Sum(s => s.Cost);
    public bool IsEmpty => Segments.Count == 0;
}

public static class DayBars
{
    public const int Window = 30;

    /// <summary>Build the trailing Window-day series using the active metric's
    /// last positive selected datum as the anchor. If that metric has no datum,
    /// fall back to the selected range end, payload range end, then the caller's
    /// fallback date. Days outside the data render as empty bars.
    ///
    /// <paramref name="rangeEnd"/> is the SELECTED clients' range end
    /// (UsageStats.DateRange.End, selection-derived), NOT the unfiltered
    /// payload.Meta.DateRange.End. Membership is canonical, but agent segment
    /// keys and labels retain each stripe's raw id.</summary>
    public static IReadOnlyList<DayBar> Build(
        UsagePayload payload,
        IReadOnlyList<string> clientIds,
        StackBy stackBy,
        ChartMetric metric,
        ModelColorMap colors,
        string endFallback,
        string? rangeEnd = null)
    {
        var allowed = clientIds
            .Select(ClientRegistry.CanonicalClient)
            .ToHashSet(StringComparer.Ordinal);
        var byDate = new Dictionary<string, DayBar>();
        string? metricEnd = null;
        foreach (var contribution in payload.Contributions)
        {
            var day = BuildDayBar(contribution, allowed, stackBy, colors);
            if (day.TotalTokens > 0 || day.TotalCost > 0)
            {
                byDate[day.Date] = day;
            }

            if (contribution.Clients.Any(client =>
                allowed.Contains(ClientRegistry.CanonicalClient(client.Client))
                && (metric == ChartMetric.Cost
                    ? client.Cost > 0
                    : client.Tokens.Total > 0))
                && (metricEnd is null
                    || string.CompareOrdinal(contribution.Date, metricEnd) > 0))
            {
                metricEnd = contribution.Date;
            }
        }

        // A metric-specific tail is authoritative: a message-only tail must not
        // move the chart, and a cost chart must not follow token-only data.
        ISODay? endDay = null;
        foreach (var candidate in new[]
        {
            metricEnd,
            rangeEnd,
            payload.Meta.DateRange.End,
            endFallback,
        })
        {
            if (!string.IsNullOrEmpty(candidate) && ISODay.Parse(candidate) is { } parsed)
            {
                endDay = parsed;
                break;
            }
        }

        if (endDay is null)
        {
            return [];
        }

        return Enumerable.Range(0, Window)
            .Select(i =>
            {
                var date = new ISODay(endDay.Value.Number - (Window - 1) + i).Iso;
                return byDate.GetValueOrDefault(date) ?? new DayBar(date, []);
            })
            .ToList();
    }

    internal static DayBar BuildDayBar(
        Contribution contribution, IReadOnlySet<string> allowed, StackBy stackBy,
        ModelColorMap colors)
    {
        // Group each day either by model (tokscale-style provider shades) or
        // by agent/client (brand colors). Color + label follow the mode.
        var grouped = new Dictionary<string, DaySegment>();
        foreach (var client in contribution.Clients)
        {
            if (!allowed.Contains(ClientRegistry.CanonicalClient(client.Client)))
            {
                continue;
            }

            var tokens = client.Tokens.Total;
            if (!UsageActivity.IsActive(tokens, client.Cost, 0))
            {
                continue;
            }

            var model = client.ModelId.Length == 0 ? "unknown" : ModelGrouping.GroupId(client.ModelId);
            var key = stackBy == StackBy.Model ? model : client.Client;
            if (!grouped.TryGetValue(key, out var slot))
            {
                slot = stackBy == StackBy.Model
                    ? new DaySegment(key, model, colors.Color(client.ProviderId, model))
                    : new DaySegment(
                        key,
                        ClientRegistry.ShortName(client.Client),
                        ClientRegistry.Style(client.Client).Color);
                grouped[key] = slot;
            }

            slot.Tokens = slot.Tokens.SaturatingAdd(tokens);
            slot.Cost += client.Cost;
        }

        // Stable stacking order across days: sort by key.
        return new DayBar(
            contribution.Date,
            [.. grouped.Values.OrderBy(s => s.Key, StringComparer.Ordinal)]);
    }

    /// <summary>Aggregate every segment across the visible window for the
    /// legend, heaviest-first by the active metric.</summary>
    public static IReadOnlyList<DaySegment> Legend(IReadOnlyList<DayBar> bars, ChartMetric metric)
    {
        var agg = new Dictionary<string, DaySegment>();
        foreach (var seg in bars.SelectMany(b => b.Segments))
        {
            if (!agg.TryGetValue(seg.Key, out var slot))
            {
                slot = new DaySegment(seg.Key, seg.Label, seg.Color);
                agg[seg.Key] = slot;
            }

            slot.Tokens = slot.Tokens.SaturatingAdd(seg.Tokens);
            slot.Cost += seg.Cost;
        }

        // Key tiebreak: the Swift original inherits Dictionary iteration order
        // on metric ties (random per process); a deterministic order is a
        // strict improvement and worth upstreaming.
        return metric == ChartMetric.Cost
            ? [.. agg.Values.OrderByDescending(s => s.Cost).ThenBy(s => s.Key, StringComparer.Ordinal)]
            : [.. agg.Values.OrderByDescending(s => s.Tokens).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }
}
