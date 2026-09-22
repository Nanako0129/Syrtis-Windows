using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.App;

/// <summary>One authority-aware policy for every cost-bearing surface. The
/// graph metadata, accepted by GraphConsumerState, is the only authority input;
/// a numeric cost is never used to infer readiness.</summary>
public static class CostSurfaceProjection
{
    // Properties, not consts: a const cannot call Localized(), and the table is
    // loaded after these would already have been baked into every call site.
    public static string Checking => "Checking".Localized();

    public static string ChartChecking => "Checking cost…".Localized();

    public static bool CanUseCost(bool authoritative) => authoritative;

    public static bool IsChartCostChecking(
        bool authoritative, ChartMetric requested) =>
        !authoritative && requested == ChartMetric.Cost;

    /// <summary>Partial counts as authoritative. The engine's coverage fold
    /// reports Partial when at least one message priced and at least one did
    /// not, so the total is every cost it could resolve with the rest folded in
    /// at zero — the same number macOS displays, since macOS has no coverage
    /// gate at all. Only None is withheld: there the total really would be a
    /// fabricated $0. Treating Partial as unauthoritative made the cost surface
    /// read "Checking" permanently on any profile holding one unpriced model,
    /// because Partial is a terminal state, not a loading one.</summary>
    public static bool IsAuthoritative(UsagePayload? graph) =>
        graph?.Meta.PricingMode == PricingMode.BestEffort
        && graph.Meta.CostCoverage != CostCoverage.None;

    public static string CostText(double cost, bool authoritative) =>
        authoritative ? Format.Usd(cost) : Checking;

    /// <summary>
    /// A cost shown beside the token count it was charged for — the shape of a
    /// per-model row, where a model the engine could not price must not read
    /// as a free one.
    /// <para>
    /// Two questions, answered by two layers that cannot substitute for each
    /// other. <paramref name="authoritative"/> is whole-graph: it is true as
    /// soon as ANY message priced (<see cref="IsAuthoritative"/> counts
    /// Partial on purpose), so it cannot say that THIS row did not. That half
    /// comes from the row itself through <see cref="Format.Money"/> — tokens
    /// with no cost is usage nobody could price, and reads as a dash.
    /// </para>
    /// <para>
    /// Reported 2026-09-23: a model released that day was absent from both
    /// pricing sources while the rest of the profile priced normally, so
    /// coverage was Partial, <paramref name="authoritative"/> was true, and
    /// the two-argument overload rendered that model's row "$0.00".
    /// </para>
    /// </summary>
    public static string CostText(long tokens, double cost, bool authoritative) =>
        authoritative ? Format.Money(tokens, cost) : Checking;

    public static string HeaderCostLine(
        double todayCost, UsageStats stats, bool authoritative) =>
        authoritative
            ? "{0} today · {1} all time · {2} active days".Localized(
                Format.Usd(todayCost), Format.Usd(stats.TotalCost), stats.ActiveDays)
            : "{0} · {1} active days".Localized(Checking, stats.ActiveDays);

    public static string HeaderCostLine(UsageStats stats, bool authoritative) =>
        HeaderCostLine(
            stats.PerDayMap.TryGetValue(Format.TodayKey(), out var today)
                ? today.Cost
                : 0,
            stats,
            authoritative);

    public static string ModelsSubtitle(
        IReadOnlyList<ModelReportEntry> entries, bool authoritative) =>
        "{0} models · {1}".Localized(
            entries.Count,
            authoritative ? Format.Usd(entries.Sum(e => e.Cost)) : Checking);

    public static IReadOnlyList<ModelReportEntry> OrderModels(
        IEnumerable<ModelReportEntry> entries, bool authoritative)
    {
        var query = entries
            .OrderByDescending(e => authoritative ? FiniteOrZero(e.Cost) : e.Total)
            .ThenBy(e => e.Provider, StringComparer.Ordinal)
            .ThenBy(e => e.Model, StringComparer.Ordinal)
            .ThenBy(e => e.Client, StringComparer.Ordinal);
        return [.. query];
    }

    public static ModelReportEntry? FavoriteModel(
        IEnumerable<ModelReportEntry> entries, bool authoritative) =>
        OrderModels(entries, authoritative).FirstOrDefault();

    public static IReadOnlyList<ContributionClient> OrderContributionClients(
        IEnumerable<ContributionClient> clients, bool authoritative)
    {
        var query = clients
            .OrderByDescending(e => authoritative ? FiniteOrZero(e.Cost) : e.Tokens.Total)
            .ThenBy(e => e.ProviderId, StringComparer.Ordinal)
            .ThenBy(e => e.ModelId, StringComparer.Ordinal)
            .ThenBy(e => e.Client, StringComparer.Ordinal);
        return [.. query];
    }

    public static string BestDayText((string Date, double Cost)? bestDay, bool authoritative) =>
        !authoritative ? Checking
        : bestDay is { } best ? Format.MonthDay(best.Date) : "—";

    public static ChartMetric EffectiveMetric(bool authoritative, ChartMetric requested) =>
        authoritative || requested != ChartMetric.Cost ? requested : ChartMetric.Tokens;

    public static string ChartCostLabel(bool authoritative) =>
        authoritative ? "Price".Localized() : "Price · Checking".Localized();

    public static IReadOnlyList<DaySegment> OrderDaySegments(
        IEnumerable<DaySegment> segments,
        bool authoritative,
        ChartMetric requested)
    {
        var metric = EffectiveMetric(authoritative, requested);
        var query = metric == ChartMetric.Cost
            ? segments.OrderByDescending(s => s.Cost)
            : segments.OrderByDescending(s => s.Tokens);
        return [.. query.ThenBy(s => s.Key, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<AgentReportEntry> OrderAgents(
        IEnumerable<AgentReportEntry> entries, bool authoritative)
    {
        var query = entries
            .OrderByDescending(e => authoritative ? FiniteOrZero(e.Cost) : e.Total)
            .ThenBy(e => e.Agent, StringComparer.Ordinal);
        return [.. query];
    }

    public static double AgentScale(
        IReadOnlyList<AgentReportEntry> entries, bool authoritative)
    {
        var max = entries.Count == 0
            ? 0
            : entries.Max(e => authoritative ? FiniteOrZero(e.Cost) : e.Total);
        return Math.Max(max, authoritative ? 0.0001 : 1);
    }

    public static double AgentBarValue(AgentReportEntry entry, bool authoritative) =>
        authoritative ? FiniteOrZero(entry.Cost) : entry.Total;

    public static string AgentsTitle(bool authoritative) =>
        authoritative ? "Agents by cost".Localized() : "Agents by tokens".Localized();

    public static string TrayTitle(
        TrayMode mode,
        TrayTotals? totals,
        double? tokensPerMin,
        double? quotaRemaining,
        bool authoritative) =>
        !authoritative
            && mode is (TrayMode.TodayCost or TrayMode.TotalCost)
            ? Checking
            : mode.Title(totals, tokensPerMin, quotaRemaining);

    public static string HourlyCost(double cost, bool authoritative) =>
        CostText(cost, authoritative);

    public static string DayTipCost(double cost, bool authoritative) =>
        CostText(cost, authoritative);

    /// <summary>The Models row's hover card. Token-aware for the same reason as
    /// the row it sits on: the two describe one model, and must not disagree
    /// about whether it has a price.</summary>
    public static string ModelTipCost(long tokens, double cost, bool authoritative) =>
        CostText(tokens, cost, authoritative);

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
