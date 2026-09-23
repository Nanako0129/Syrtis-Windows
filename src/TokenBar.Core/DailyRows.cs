using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>UI-free projection for one active contribution day. Clients retain
/// the payload's raw stripe ids for drill-down; turn scope ids are canonical.</summary>
public sealed record DailyRow(
    string Date,
    long Tokens,
    double Cost,
    long Messages,
    long? Turns,
    IReadOnlyList<string> TurnClients,
    IReadOnlyList<ContributionClient> Clients);

public static class DailyRows
{
    private static readonly HashSet<string> SupportedTurnClients =
        ["codex", "claude"];

    /// <summary>Project selected payload stripes into most-recent-first rows.
    /// Selection is canonical, while raw client ids and turn-map keys are never
    /// normalized or substituted.</summary>
    public static IReadOnlyList<DailyRow> Build(
        UsagePayload payload, IReadOnlyList<string> orderedSelectedClients)
    {
        var selected = orderedSelectedClients
            .Select(ClientRegistry.CanonicalClient)
            .ToHashSet(StringComparer.Ordinal);
        var orderedTurnClients = TurnScope(orderedSelectedClients);
        var rows = new List<DailyRow>();

        foreach (var contribution in payload.Contributions)
        {
            var clients = contribution.Clients
                .Where(client => selected.Contains(
                    ClientRegistry.CanonicalClient(client.Client)))
                .OrderByDescending(client => client.Cost)
                .ToList();
            long tokens = 0;
            long messages = 0;
            var cost = 0.0;
            foreach (var client in clients)
            {
                tokens = tokens.SaturatingAdd(client.Tokens.Total);
                messages = messages.SaturatingAdd(client.Messages);
                cost += client.Cost;
            }

            if (!UsageActivity.IsActive(tokens, cost, messages))
            {
                continue;
            }

            var turnClients = orderedTurnClients
                .Where(canonical => clients.Any(client =>
                    ClientRegistry.CanonicalClient(client.Client) == canonical))
                .ToList();
            long? turns = turnClients.Count == 0
                ? null
                : SumTurns(contribution.TurnsByClient, clients);
            rows.Add(new DailyRow(
                contribution.Date,
                tokens,
                cost,
                messages,
                turns,
                turnClients,
                clients));
        }

        return rows
            .OrderByDescending(row => row.Date, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The selected clients whose turns are counted, canonical and in
    /// selection order. The lens-level scope: a row's own TurnClients is the
    /// subset of this that was active that day.</summary>
    public static IReadOnlyList<string> TurnScope(IReadOnlyList<string> orderedSelectedClients) =>
        orderedSelectedClients
            .Select(ClientRegistry.CanonicalClient)
            .Where(SupportedTurnClients.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static long SumTurns(
        IReadOnlyDictionary<string, long>? turnsByClient,
        IReadOnlyList<ContributionClient> clients)
    {
        if (turnsByClient is null || turnsByClient.Count == 0)
        {
            return 0;
        }

        long total = 0;
        var seenRawIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in clients)
        {
            if (!SupportedTurnClients.Contains(
                    ClientRegistry.CanonicalClient(client.Client))
                || !seenRawIds.Add(client.Client))
            {
                continue;
            }

            // Query only exact raw stripe ids. Map-only keys and aliases that did
            // not appear as matching stripes must not contribute turns.
            if (turnsByClient.TryGetValue(client.Client, out var count))
            {
                total = total.SaturatingAdd(count);
            }
        }

        return total;
    }
}
