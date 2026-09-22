using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class CostSurfaceProjectionTests
{
    // Localization.Load installs one process-wide table. English assertions in
    // this class used to hold only because every test that loads zh-Hant
    // restores it in a finally — a convention every future test would have to
    // remember. Establishing the precondition here makes it a mechanism: xUnit
    // constructs the class before each test.
    public CostSurfaceProjectionTests() => Localization.Load("en", AppContext.BaseDirectory);
    private static readonly ModelReportEntry ExpensiveFewTokens =
        new("claude", "model-a", "anthropic", 1, 0, 0, 0, 0, 1, 1, 100);

    private static readonly ModelReportEntry CheapManyTokens =
        new("claude", "model-b", "anthropic", 100, 0, 0, 0, 0, 100, 1, 1);

    private static readonly AgentReportEntry ExpensiveAgent =
        new("agent-a", [], 1, 0, 0, 0, 0, 1, 100, 1);

    private static readonly AgentReportEntry TokenAgent =
        new("agent-b", [], 100, 0, 0, 0, 0, 100, 1, 1);

    private static UsagePayload Payload(
        PricingMode pricing = PricingMode.BestEffort,
        CostCoverage coverage = CostCoverage.Complete) =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-01-02"),
                pricing,
                coverage),
            new UsageSummary(100, 100, 1, 1, 100, 100, ["claude"], ["model-a"]),
            [],
            [new Contribution(
                "2026-01-02",
                new ContributionTotals(100, 100, 1),
                1,
                new TokenBreakdown(100, 0, 0, 0, 0),
                [new ContributionClient(
                    "claude", "model-a", "anthropic",
                    new TokenBreakdown(100, 0, 0, 0, 0), 100, 1)])]);

    [Theory]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Complete, false)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Partial, false)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.None, false)]
    // Partial is authoritative: the engine folds unpriced messages in at zero,
    // so the total is real, just incomplete — and it is a terminal state, not a
    // loading one, so withholding it showed "Checking" forever.
    [InlineData(PricingMode.BestEffort, CostCoverage.Partial, true)]
    [InlineData(PricingMode.BestEffort, CostCoverage.None, false)]
    [InlineData(PricingMode.BestEffort, CostCoverage.Complete, true)]
    public void MetadataMatrixIsTheOnlyCostAuthority(
        PricingMode pricing, CostCoverage coverage, bool expected)
    {
        Assert.Equal(expected, CostSurfaceProjection.IsAuthoritative(
            Payload(pricing, coverage)));
    }

    [Fact]
    public void CheckingPolicyUsesTokenFallbacksAcrossNamedSurfaces()
    {
        var entries = new[] { ExpensiveFewTokens, CheapManyTokens };
        var agents = new[] { ExpensiveAgent, TokenAgent };
        var payload = Payload();
        var stats = new UsageStats(payload, new HashSet<string> { "claude" });

        Assert.Equal("Checking · 1 active days",
            CostSurfaceProjection.HeaderCostLine(stats, false));
        Assert.Equal(ChartMetric.Tokens,
            CostSurfaceProjection.EffectiveMetric(false, ChartMetric.Cost));
        Assert.Equal("Price · Checking",
            CostSurfaceProjection.ChartCostLabel(false));
        Assert.True(CostSurfaceProjection.IsChartCostChecking(
            false, ChartMetric.Cost));
        Assert.False(CostSurfaceProjection.IsChartCostChecking(
            false, ChartMetric.Tokens));
        Assert.False(CostSurfaceProjection.IsChartCostChecking(
            true, ChartMetric.Cost));
        Assert.Equal("Checking cost…", CostSurfaceProjection.ChartChecking);
        Assert.Equal("2 models · Checking",
            CostSurfaceProjection.ModelsSubtitle(entries, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.CostText(100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.DayTipCost(1_000, 100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.HourlyCost(1_000, 100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.ModelTipCost(1_000, 100, false));
        Assert.Equal("Checking",
            CostSurfaceProjection.BestDayText(stats.BestDay, false));
        Assert.Equal("Agents by tokens",
            CostSurfaceProjection.AgentsTitle(false));
        Assert.Equal("Checking",
            CostSurfaceProjection.TrayTitle(
                TrayMode.TodayCost,
                new TrayTotals(1, 100, 1, 100),
                null,
                null,
                false));
        Assert.Equal("Checking",
            CostSurfaceProjection.TrayTitle(
                TrayMode.TotalCost,
                new TrayTotals(1, 100, 1, 100),
                null,
                null,
                false));

        Assert.Equal("model-b",
            CostSurfaceProjection.OrderModels(entries, false).First().Model);
        var segments = new[]
        {
            new DaySegment("a", "a", "#000000") { Tokens = 1, Cost = 100 },
            new DaySegment("b", "b", "#000000") { Tokens = 100, Cost = 1 },
        };
        Assert.Equal("b",
            CostSurfaceProjection.OrderDaySegments(
                segments, false, ChartMetric.Cost).First().Key);
        Assert.Equal("agent-b",
            CostSurfaceProjection.OrderAgents(agents, false).First().Agent);
        Assert.Equal(100,
            CostSurfaceProjection.AgentBarValue(TokenAgent, false));
        Assert.Equal(100, CostSurfaceProjection.AgentScale(agents, false));

        var colors = new ModelColorMap(entries.Select(e =>
            (e.Provider, e.Model, e.Cost)), costAuthoritative: false);
        Assert.Equal("#da7756", colors.Color("anthropic", "model-a"));
        Assert.NotEqual("#da7756", colors.Color("anthropic", "model-b"));

        var clients = new[]
        {
            new ContributionClient(
                "claude", "model-a", "anthropic",
                new TokenBreakdown(1, 0, 0, 0, 0), 100, 1),
            new ContributionClient(
                "claude", "model-b", "anthropic",
                new TokenBreakdown(100, 0, 0, 0, 0), 1, 1),
        };
        Assert.Equal("model-b",
            CostSurfaceProjection.OrderContributionClients(clients, false).First().ModelId);
    }

    [Fact]
    public void ExactCompleteRestoresCostOrderScaleAndFormatting()
    {
        var entries = new[] { ExpensiveFewTokens, CheapManyTokens };
        var agents = new[] { ExpensiveAgent, TokenAgent };
        var payload = Payload();
        var stats = new UsageStats(payload, new HashSet<string> { "claude" });

        Assert.Equal("$100.00 today · $100.00 all time · 1 active days",
            CostSurfaceProjection.HeaderCostLine(100, stats, true));
        Assert.Equal(ChartMetric.Cost,
            CostSurfaceProjection.EffectiveMetric(true, ChartMetric.Cost));
        Assert.Equal("2 models · $101.00",
            CostSurfaceProjection.ModelsSubtitle(entries, true));
        Assert.Equal("$100.00", CostSurfaceProjection.CostText(100, true));
        Assert.Equal("model-a",
            CostSurfaceProjection.OrderModels(entries, true).First().Model);
        Assert.Equal("agent-a",
            CostSurfaceProjection.OrderAgents(agents, true).First().Agent);
        Assert.Equal(100, CostSurfaceProjection.AgentScale(agents, true));
        Assert.Equal("Agents by cost", CostSurfaceProjection.AgentsTitle(true));
        Assert.Equal("$100.00",
            CostSurfaceProjection.TrayTitle(
                TrayMode.TodayCost,
                new TrayTotals(1, 100, 1, 100),
                null,
                null,
                true));
    }

    // ---- a model the engine could not price (reported 2026-09-23) --------

    // The reported case, end to end through both layers. A model released that
    // day had no entry in either pricing source while the rest of the profile
    // priced normally, so coverage was Partial — which IS authoritative, on
    // purpose (see the matrix above). The whole-graph flag therefore cannot
    // say that this one row did not price; only the row's own tokens-without-
    // cost can, and the old overload rendered it "$0.00".
    [Fact]
    public void AnUnpricedModelInAPartiallyPricedProfileReadsAsUnpricedNotFree()
    {
        var authoritative = CostSurfaceProjection.IsAuthoritative(
            Payload(coverage: CostCoverage.Partial));
        Assert.True(authoritative);

        Assert.Equal("—", CostSurfaceProjection.CostText(42_729, 0, authoritative));
        // The row's hover card describes the same model and must agree.
        Assert.Equal("—", CostSurfaceProjection.ModelTipCost(42_729, 0, authoritative));
        // What the two-argument overload says about the very same row — kept
        // as the record of the defect, so the difference is asserted rather
        // than remembered.
        Assert.Equal("$0.00", CostSurfaceProjection.CostText(0, authoritative));
    }

    [Theory]
    // No usage and no cost is a real total, not an unpriced one.
    [InlineData(0L, 0.0, "$0.00")]
    // Priced normally.
    [InlineData(42_729L, 0.11, "$0.11")]
    // Priced but below the format's resolution: small, not nothing. The same
    // rule Format.Money states — "$0.00" there would claim a price nobody has.
    [InlineData(1_000L, 0.001, "<$0.01")]
    public void APricedOrEmptyRowKeepsItsAmount(long tokens, double cost, string expected) =>
        Assert.Equal(expected, CostSurfaceProjection.CostText(tokens, cost, true));

    // The graph-level answer still comes first: while nothing has priced yet,
    // the row says so rather than guessing between free and unpriced.
    [Fact]
    public void AnUnresolvedGraphStillReadsCheckingForAnUnpricedRow() =>
        Assert.Equal("Checking", CostSurfaceProjection.CostText(42_729, 0, false));

    // The Hourly row and the day tooltip render their amount beside their own
    // token count too, so they follow the same rule as the Models row.
    [Fact]
    public void HourlyAndDayTooltipAmountsMarkAnUnpricedBucket()
    {
        Assert.Equal("—", CostSurfaceProjection.HourlyCost(5_000, 0, true));
        Assert.Equal("—", CostSurfaceProjection.DayTipCost(5_000, 0, true));
        // An empty bucket is a real zero, not an unpriced one.
        Assert.Equal("$0.00", CostSurfaceProjection.HourlyCost(0, 0, true));
        Assert.Equal("$0.00", CostSurfaceProjection.DayTipCost(0, 0, true));
    }

    [Fact]
    public void DelayedOldGenerationCannotRestoreCostAuthority()
    {
        var state = new GraphConsumerState();
        var old = new GraphRequestId(GraphQuery.Normalize(null), 1);
        var current = new GraphRequestId(GraphQuery.Normalize(null), 2);
        var query2 = new GraphRequestId(GraphQuery.Normalize("2025"), 3);

        state.Begin(old);
        Assert.True(state.TryAcceptGraph(
            old, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);

        state.Begin(current);
        Assert.False(state.CostAuthoritative);
        Assert.False(state.TryAcceptGraph(
            old, Payload(), GraphPublicationStage.Richer));
        Assert.Equal("Checking", CostSurfaceProjection.CostText(100, false));

        state.Begin(query2);
        Assert.False(state.TryAcceptGraph(
            current, Payload(), GraphPublicationStage.Richer));
        Assert.False(state.CostAuthoritative);

        // A blocked/failed local stage leaves the new query checking until the
        // exact current richer result is accepted.
        Assert.True(state.TryAcceptGraph(
            query2, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);
    }

    // Same English-vs-Chinese contract as UsagePaceTests: render each surface
    // with no table and with the shipped one, and require them to differ.
    [Fact]
    public void EveryCostSurfaceStringHasAShippedTranslation()
    {
        var entries = new[] { ExpensiveFewTokens, CheapManyTokens };
        var stats = new UsageStats(Payload(), new HashSet<string> { "claude" });
        var surfaces = new (string Name, Func<string> Render)[]
        {
            ("Checking", () => CostSurfaceProjection.Checking),
            ("Checking cost…", () => CostSurfaceProjection.ChartChecking),
            ("header · authoritative",
                () => CostSurfaceProjection.HeaderCostLine(stats, true)),
            ("header · checking",
                () => CostSurfaceProjection.HeaderCostLine(stats, false)),
            ("models subtitle",
                () => CostSurfaceProjection.ModelsSubtitle(entries, true)),
            ("Price", () => CostSurfaceProjection.ChartCostLabel(true)),
            ("Price · Checking", () => CostSurfaceProjection.ChartCostLabel(false)),
            ("Agents by cost", () => CostSurfaceProjection.AgentsTitle(true)),
            ("Agents by tokens", () => CostSurfaceProjection.AgentsTitle(false)),
        };

        Localization.Load("en", AppContext.BaseDirectory);
        var english = surfaces.Select(s => s.Render()).ToArray();

        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < surfaces.Length; i++)
            {
                Assert.True(
                    surfaces[i].Render() != english[i],
                    $"no zh-Hant entry for {surfaces[i].Name}: still renders "
                        + $"\"{english[i]}\"");
            }

            // The dollar figures and counts must survive into the translation,
            // which a bare inequality would not catch.
            Assert.Equal(
                $"今日 {Format.Usd(5)} · 累計 {Format.Usd(stats.TotalCost)} · "
                    + $"{stats.ActiveDays} 天有用量",
                CostSurfaceProjection.HeaderCostLine(5, stats, true));
            Assert.Equal("2 個模型 · 查詢中",
                CostSurfaceProjection.ModelsSubtitle(entries, false));
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }
}
