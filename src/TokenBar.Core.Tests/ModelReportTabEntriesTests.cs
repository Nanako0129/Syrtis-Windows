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
        Assert.False(row.TabKeyed);
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

    [Fact]
    public void RowIdIsStableWhenASecondClientJoins()
    {
        // The same model alone in a multi-client tab, then merged once the other
        // client starts using it, keeps one id regardless of which client the
        // engine listed first. The flagged row keeps the client-bearing triple
        // so it cannot collide with a merged row of the same model.
        var alone = Report(Row("a", "m", "p", 10, 1, 1), Row("b", "x", "p", 5, 1, 1))
            .TabEntries(["a", "b"]);
        var joined = Report(Row("b", "m", "p", 10, 1, 1), Row("a", "m", "p", 10, 1, 1))
            .TabEntries(["a", "b"]);
        Assert.Equal(Assert.Single(alone, r => r.Model == "m").RowId, Assert.Single(joined).RowId);
        Assert.Equal("*|m|p", Assert.Single(joined).RowId);

        var flagged = Report(Row("a", "m", "p", 10, 300, 1), Row("b", "m", "p", 10, 1, 100))
            .TabEntries(["a", "b"]);
        var flaggedRow = Assert.Single(flagged, r => r.Client == "a");
        Assert.False(flaggedRow.TabKeyed);
        Assert.Equal("a|m|p", flaggedRow.RowId);
        Assert.Equal("*|m|p", Assert.Single(flagged, r => r.Client == "b").RowId);

        Assert.Equal("claude|gemini-3.8-flash|google",
            Assert.Single(AntigravityReport().TabEntries(["claude"])).RowId);
    }
}
