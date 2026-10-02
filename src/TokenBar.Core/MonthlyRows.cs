using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>UI-free projection for one calendar month. Built by folding
/// <see cref="DailyRows"/> rather than re-walking the payload, so client
/// selection, turn scoping and the activity threshold stay defined in exactly
/// one place.</summary>
public sealed record MonthlyRow(
    string Month,
    long Tokens,
    double Cost,
    long Messages,
    long? Turns,
    IReadOnlyList<string> TurnClients,
    IReadOnlyList<ContributionClient> Clients);

public static class MonthlyRows
{
    /// <summary>Project selected payload stripes into most-recent-first months.
    /// Model stripes are merged across every day in the month — Daily merges
    /// within one contribution, Monthly folds up to 31 of them — keyed by raw
    /// client id plus model and provider, so two clients reporting the same
    /// model stay separate rows in the drill-down.</summary>
    public static IReadOnlyList<MonthlyRow> Build(
        UsagePayload payload, IReadOnlyList<string> orderedSelectedClients)
    {
        var months = new Dictionary<string, Slot>(StringComparer.Ordinal);

        foreach (var day in DailyRows.Build(payload, orderedSelectedClients))
        {
            // Contribution.Date is "YYYY-MM-DD"; anything shorter is not a date
            // this fold can bucket, so it is dropped rather than guessed at.
            if (day.Date.Length < 7)
            {
                continue;
            }

            var key = day.Date[..7];
            if (!months.TryGetValue(key, out var slot))
            {
                slot = new Slot();
                months[key] = slot;
            }

            slot.Tokens = slot.Tokens.SaturatingAdd(day.Tokens);
            slot.Messages = slot.Messages.SaturatingAdd(day.Messages);
            slot.Cost += day.Cost;

            // A month has turn data if any of its days did; days without it
            // contribute nothing rather than forcing the month to zero.
            if (day.Turns is { } turns)
            {
                slot.Turns = (slot.Turns ?? 0).SaturatingAdd(turns);
            }

            foreach (var id in day.TurnClients)
            {
                if (!slot.TurnClients.Contains(id, StringComparer.Ordinal))
                {
                    slot.TurnClients.Add(id);
                }
            }

            foreach (var client in day.Clients)
            {
                var stripe = (client.Client, client.ModelId, client.ProviderId);
                if (slot.Clients.TryGetValue(stripe, out var merged))
                {
                    slot.Clients[stripe] = Merge(merged, client);
                }
                else
                {
                    slot.Clients[stripe] = client;
                }
            }
        }

        return [.. months
            .Select(entry => new MonthlyRow(
                entry.Key,
                entry.Value.Tokens,
                entry.Value.Cost,
                entry.Value.Messages,
                entry.Value.Turns,
                entry.Value.TurnClients,
                [.. entry.Value.Clients.Values.OrderByDescending(c => c.Cost)]))
            .OrderByDescending(row => row.Month, StringComparer.Ordinal)];
    }

    /// <summary>One stripe's sums for two stripes of the same key. Shared with
    /// <see cref="DailyRows"/>, which merges the stripes that model display
    /// grouping folds together within a day.</summary>
    internal static ContributionClient Merge(ContributionClient merged, ContributionClient client) =>
        merged with
        {
            Tokens = Add(merged.Tokens, client.Tokens),
            Cost = merged.Cost + client.Cost,
            // ContributionClient.Messages is int, so this cannot use the long
            // SaturatingAdd the rest of the fold does.
            Messages = (int)Math.Min((long)merged.Messages + client.Messages, int.MaxValue),
        };

    private static TokenBreakdown Add(TokenBreakdown a, TokenBreakdown b) =>
        new(
            a.Input.SaturatingAdd(b.Input),
            a.Output.SaturatingAdd(b.Output),
            a.CacheRead.SaturatingAdd(b.CacheRead),
            a.CacheWrite.SaturatingAdd(b.CacheWrite),
            a.Reasoning.SaturatingAdd(b.Reasoning));

    private sealed class Slot
    {
        public long Tokens;
        public long Messages;
        public double Cost;
        public long? Turns;
        public List<string> TurnClients { get; } = [];
        public Dictionary<(string, string, string), ContributionClient> Clients { get; } = [];
    }
}
