using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// ModelReportFold.TabEntries (port of macOS ModelReport.tabEntries(clientIds:),
// Syrtis #558): a tab covering several clients shows one row per model and
// provider, each client's tokens in ClientParts.
public class ModelReportTabEntriesTests
{
    private static ModelReportEntry Row(
        string client, string model, string provider, long total, double cost, double? estimate) =>
        new(client, model, provider, total, 0, 0, 0, 0, total, 1, cost, MsPer1kTokens: 1.0,
            CostEstimate: estimate);

    private static ModelReport Report(params ModelReportEntry[] rows) =>
        new(rows, 0, 0, 0, 0, 0, 0);

    private static ModelReport AntigravityReport() => Report(
        Row("antigravity-cli", "gemini-3.8-flash", "google", 900, 0.9, 1.0),
        Row("antigravity-extension", "gemini-3.8-flash", "google", 100, 0.1, 0.5),
        Row("antigravity-cli", "gemini-3.1-pro", "google", 50, 0.2, null),
        Row("claude", "gemini-3.8-flash", "google", 7, 0.01, null));

    [Fact]
    public void MultiClientTabMergesOneModelAndListsEachClientsTokens()
    {
        var rows = AntigravityReport().TabEntries(["antigravity-cli", "antigravity-extension"]);

        Assert.Equal(2, rows.Count);
        var flash = Assert.Single(rows, r => r.Model == "gemini-3.8-flash");
        Assert.Equal(1000, flash.Total);
        Assert.Equal(2, flash.MessageCount);
        Assert.Equal(1.5, flash.CostEstimate);
        Assert.Equal(
            [new ClientPart("antigravity-cli", 900), new ClientPart("antigravity-extension", 100)],
            flash.ClientParts);
        Assert.Null(Assert.Single(rows, r => r.Model == "gemini-3.1-pro").ClientParts);
    }

    [Fact]
    public void OneClientTabKeepsItsRowsAndAddsNoParts()
    {
        var rows = AntigravityReport().TabEntries(["claude"]);

        var row = Assert.Single(rows);
        Assert.Equal(7, row.Total);
        Assert.Null(row.ClientParts);
    }

    [Fact]
    public void ProvidersStayApartInAMultiClientTab()
    {
        var rows = Report(
            Row("a", "m", "openrouter", 10, 1, 1),
            Row("b", "m", "anthropic", 10, 1, 1)).TabEntries(["a", "b"]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.ClientParts));
    }

    [Fact]
    public void EachProvidersTokensStayTogetherAcrossClients()
    {
        // a reaches the model through two providers, b through one of them.
        var report = Report(
            Row("a", "m", "nvidia", 10, 1, 1),
            Row("a", "m", "openai", 20, 1, 1),
            Row("b", "m", "openai", 5, 1, 1));

        var rows = report.TabEntries(["a", "b"]);

        Assert.Equal(2, rows.Count);
        var openai = Assert.Single(rows, r => r.Provider == "openai");
        Assert.Equal(25, openai.Total);
        Assert.Equal([new ClientPart("a", 20), new ClientPart("b", 5)], openai.ClientParts);
        var nvidia = Assert.Single(rows, r => r.Provider == "nvidia");
        Assert.Equal(10, nvidia.Total);
        Assert.Null(nvidia.ClientParts);

        // Its own tab still joins that client's providers into one row.
        var own = Assert.Single(report.TabEntries(["a"]));
        Assert.Equal("nvidia, openai", own.Provider);
        Assert.Equal(30, own.Total);
    }

    [Fact]
    public void FlaggedRowIsNeverMerged()
    {
        // a: 300 / 1 = 300x the estimate (flagged); b: healthy. Summed they would
        // read 301 / 101, under the threshold, and the warning would vanish.
        var rows = Report(
            Row("a", "m", "p", 10, 300, 1),
            Row("b", "m", "p", 10, 1, 100)).TabEntries(["a", "b"]);

        Assert.Equal(2, rows.Count);
        Assert.NotNull(CostPlausibility.ImplausibleRatio(Assert.Single(rows, r => r.Client == "a")));
    }

    [Fact]
    public void FlaggedRowStaysAloneWithThreeClientsAndTheOthersStillMerge()
    {
        var rows = Report(
            Row("a", "m", "p", 10, 300, 1),
            Row("b", "m", "p", 20, 1, 100),
            Row("c", "m", "p", 30, 1, 100)).TabEntries(["a", "b", "c"]);

        Assert.Equal(2, rows.Count);
        Assert.NotNull(CostPlausibility.ImplausibleRatio(Assert.Single(rows, r => r.Client == "a")));
        var merged = Assert.Single(rows, r => r.ClientParts is not null);
        Assert.Equal(50, merged.Total);
        Assert.Equal(
            [new ClientPart("c", 30), new ClientPart("b", 20)], merged.ClientParts);
    }

    [Fact]
    public void TiedPartsOrderByClient()
    {
        var rows = Report(
            Row("b", "m", "p", 10, 1, 1),
            Row("a", "m", "p", 10, 1, 1)).TabEntries(["a", "b"]);

        Assert.Equal(
            [new ClientPart("a", 10), new ClientPart("b", 10)],
            Assert.Single(rows).ClientParts);
    }

    // Port of the macOS SelfTest "a tab's rows count, label and rank by model,
    // not by provider row".
    [Fact]
    public void RowsCountLabelAndRankByModelNotByProviderRow()
    {
        var rows = Report(
            Row("a", "solo", "p", 1, 3, 3),
            Row("a", "m", "nvidia", 1, 1.5, 1.5),
            Row("a", "m", "", 1, 2.5, 2.5),
            Row("b", "other", "p", 1, 0.1, null)).TabEntries(["a", "b"]);

        Assert.Equal(4, rows.Count);
        Assert.Equal(3, rows.ModelCount());
        Assert.Equal(["m"], rows.RepeatedModels());
        // m: 1.5 + 2.5 beats solo: 3, and its costliest row is the unspecified one.
        var favorite = rows.FavoriteRow(byCost: true)!;
        Assert.Equal("m", favorite.Model);
        Assert.Equal("", favorite.Provider);
        Assert.Equal("Unspecified provider", favorite.ProviderLabel());
        Assert.Equal("nvidia", Assert.Single(rows, r => r.Provider == "nvidia").ProviderLabel());
        Assert.Null(Array.Empty<ModelReportEntry>().FavoriteRow(true));
        Assert.Equal(0, Array.Empty<ModelReportEntry>().ModelCount());
    }

    [Fact]
    public void FavoriteMeasuresTokensWhenNotRankingByCost()
    {
        // By tokens m (2 + 2) beats solo (3) although solo costs more.
        var rows = new[]
        {
            Row("a", "solo", "p", 3, 9, null),
            Row("a", "m", "x", 2, 1, null),
            Row("a", "m", "y", 2, 1, null),
        };

        Assert.Equal("solo", rows.FavoriteRow(byCost: true)!.Model);
        Assert.Equal("m", rows.FavoriteRow(byCost: false)!.Model);
    }

    [Fact]
    public void FavoriteTieGoesToTheEarlierRow()
    {
        var rows = new[] { Row("a", "x", "p", 1, 2, null), Row("a", "y", "p", 1, 2, null) };

        Assert.Equal("x", rows.FavoriteRow(true)!.Model);
    }

    // Port of the macOS SelfTest "a flagged provider row stands alone in a
    // multi-client tab".
    [Fact]
    public void FlaggedProviderRowStandsAloneInAMultiClientTab()
    {
        var report = Report(
            Row("a", "m", "p1", 10, 300, 1),
            Row("a", "m", "p2", 10, 1, 100),
            Row("b", "m", "p2", 10, 1, 100));

        var tab = report.TabEntries(["a", "b"]);

        Assert.Equal(2, tab.Count);
        var p1 = Assert.Single(tab, r => r.Provider == "p1");
        Assert.NotNull(CostPlausibility.ImplausibleRatio(p1));
        Assert.Null(p1.ClientParts);
        Assert.Equal(20, Assert.Single(tab, r => r.Provider == "p2").Total);
        // Its client's own tab joins the two providers and reads 301 / 101.
        Assert.Null(CostPlausibility.ImplausibleRatio(Assert.Single(report.TabEntries(["a"]))));
    }
}

