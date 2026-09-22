using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// ModelReportFold.ModelLevelEntries (item 2): tokscale groups usage by
// (client, provider, model), so one model reached through two providers (or a
// provider name that changed mid-history) arrives as two entries. Left
// unfolded, a model shows twice in the Models lens, "N models" overcounts, and
// the larger provider-split component — not the model — wins "Favorite model"
// on Stats.
public class ModelReportFoldTests
{
    private static ModelReportEntry Entry(
        string client, string provider, string model,
        long input, long output, double cost, int messageCount = 1) =>
        new(client, model, provider, input, output, 0, 0, 0,
            input + output, messageCount, cost, MsPer1kTokens: 12.5);

    [Fact]
    public void SameClientAndModelAcrossTwoProvidersFoldsIntoOneRow()
    {
        var report = new ModelReport(
            Entries:
            [
                Entry("codex", "openai", "gpt-5", input: 100, output: 50, cost: 1.0),
                Entry("codex", "azure", "gpt-5", input: 40, output: 10, cost: 0.5),
            ],
            TotalInput: 140, TotalOutput: 60, TotalCacheRead: 0, TotalCacheWrite: 0,
            TotalMessages: 2, TotalCost: 1.5);

        var folded = report.ModelLevelEntries();

        var row = Assert.Single(folded);
        Assert.Equal("codex", row.Client);
        Assert.Equal("gpt-5", row.Model);
        Assert.Equal("azure, openai", row.Provider); // merged, sorted, deduped
        Assert.Equal(140, row.Input);
        Assert.Equal(60, row.Output);
        Assert.Equal(200, row.Total);
        Assert.Equal(2, row.MessageCount);
        Assert.Equal(1.5, row.Cost, 6);
        // Throughput is dropped, not averaged or carried from one component:
        // tokscale only computes it honestly over the complete rollup.
        Assert.Null(row.MsPer1kTokens);
    }

    [Fact]
    public void DifferentClientsOrModelsStayDistinctRows()
    {
        var report = new ModelReport(
            Entries:
            [
                Entry("codex", "openai", "gpt-5", 100, 50, 1.0),
                Entry("claude", "anthropic", "gpt-5", 100, 50, 1.0), // same model, different client
                Entry("codex", "openai", "gpt-5-mini", 100, 50, 1.0), // same client, different model
            ],
            TotalInput: 300, TotalOutput: 150, TotalCacheRead: 0, TotalCacheWrite: 0,
            TotalMessages: 3, TotalCost: 3.0);

        Assert.Equal(3, report.ModelLevelEntries().Count);
    }

    [Fact]
    public void MergeOrderIsStableFirstOccurrenceWins() =>
        Assert.Equal(
            ["gpt-5", "o1"],
            new ModelReport(
                Entries:
                [
                    Entry("codex", "openai", "gpt-5", 1, 1, 0.1),
                    Entry("codex", "openai", "o1", 1, 1, 0.1),
                    Entry("codex", "azure", "gpt-5", 1, 1, 0.1),
                ],
                TotalInput: 3, TotalOutput: 3, TotalCacheRead: 0, TotalCacheWrite: 0,
                TotalMessages: 3, TotalCost: 0.3)
                .ModelLevelEntries()
                .Select(e => e.Model));

    // The engine emits a bare model key alongside prefixed ones — the
    // `same-model` case in model_report.rs's own fixture — which arrives as an
    // empty provider. Kept in the merge it sorts first and renders as a leading
    // separator, ", nvidia, openai", which ModelTip displays verbatim.
    [Fact]
    public void AnUnspecifiedProviderDoesNotBecomeALeadingSeparator()
    {
        var folded = new ModelReport(
            Entries:
            [
                Entry("claude", "openai", "same-model", 1, 2, 0.1),
                Entry("claude", "nvidia", "same-model", 3, 4, 0.2),
                Entry("claude", "", "same-model", 5, 6, 0.3),
            ],
            TotalInput: 9, TotalOutput: 12, TotalCacheRead: 0, TotalCacheWrite: 0,
            TotalMessages: 3, TotalCost: 0.6).ModelLevelEntries();

        var row = Assert.Single(folded);
        Assert.Equal("nvidia, openai", row.Provider);
    }

    // ...but an all-unspecified merge stays empty. The fold must not invent a
    // provider name for usage the engine did not attribute to one.
    [Fact]
    public void ProvidersStayEmptyWhenNoRowNamesOne()
    {
        var folded = new ModelReport(
            Entries:
            [
                Entry("claude", "", "same-model", 1, 2, 0.1),
                Entry("claude", "", "same-model", 3, 4, 0.2),
            ],
            TotalInput: 4, TotalOutput: 6, TotalCacheRead: 0, TotalCacheWrite: 0,
            TotalMessages: 2, TotalCost: 0.3).ModelLevelEntries();

        Assert.Equal(string.Empty, Assert.Single(folded).Provider);
    }


    // ---- the local price estimate across a merge ----------------------------

    private static ModelReport Report(params ModelReportEntry[] entries) =>
        new(entries, 0, 0, 0, 0, entries.Length, entries.Sum(e => e.Cost));

    // Cost is summed, so the estimate beside it must be summed too: the ratio
    // taken from the merged row then describes the merged spend.
    [Fact]
    public void EstimatesOfTwoPricedProvidersAreSummed()
    {
        var folded = Assert.Single(Report(
            Entry("opencode", "deepseek", "m", 100, 0, cost: 10) with { CostEstimate = 1.0 },
            Entry("opencode", "openrouter", "m", 100, 0, cost: 20) with { CostEstimate = 2.0 })
            .ModelLevelEntries());

        Assert.Equal(30, folded.Cost);
        Assert.Equal(3.0, folded.CostEstimate);
    }

    // The hazard this field brings to a `with`-based fold. Left unset, `with`
    // would keep the FIRST row's estimate (1.0) while Cost is the sum (30), and
    // 30 / 1.0 is a ratio inflated by construction. All-or-nothing instead: a
    // merged row with any unpriced component has no estimate, so it cannot be
    // judged at all rather than judged on a partial denominator.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMergedRowWithAnyUnpricedComponentHasNoEstimate(bool unpricedFirst)
    {
        var priced = Entry("opencode", "deepseek", "m", 100, 0, cost: 10) with { CostEstimate = 1.0 };
        var unpriced = Entry("opencode", "openrouter", "m", 100, 0, cost: 20) with { CostEstimate = null };

        var folded = Assert.Single((unpricedFirst
            ? Report(unpriced, priced)
            : Report(priced, unpriced)).ModelLevelEntries());

        Assert.Equal(30, folded.Cost);
        Assert.Null(folded.CostEstimate);
    }

    // A row that is never merged keeps its own estimate untouched.
    [Fact]
    public void AnUnmergedRowKeepsItsEstimate() =>
        Assert.Equal(4.5, Assert.Single(Report(
            Entry("opencode", "deepseek", "m", 100, 0, cost: 10) with { CostEstimate = 4.5 })
            .ModelLevelEntries()).CostEstimate);
}
