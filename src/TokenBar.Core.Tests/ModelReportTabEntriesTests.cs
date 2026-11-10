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
    public void RawEngineIdsMatchACanonicalSelection()
    {
        // The report carries claude-code and codex-cli; the selection holds the
        // short ids claude and codex.
        var rows = Report(
            Row("claude-code", "m", "p", 10, 1, 1),
            Row("codex-cli", "m", "p", 5, 1, 1),
            Row("gemini-cli", "other", "p", 1, 1, 1)).TabEntries(["claude", "codex"]);

        var row = Assert.Single(rows);
        Assert.Equal(15, row.Total);
        Assert.Equal(
            [new ClientPart("claude", 10), new ClientPart("codex", 5)], row.ClientParts);
    }

    [Fact]
    public void AliasedRawIdsOfOneClientShareOnePart()
    {
        var rows = Report(
            Row("claude", "m", "p", 10, 1, 1),
            Row("claude-code", "m", "p", 20, 1, 1),
            Row("codex-cli", "m", "p", 25, 1, 1)).TabEntries(["claude", "codex"]);

        // claude 30 beats codex 25 only once the two claude spellings are summed.
        Assert.Equal(
            [new ClientPart("claude", 30), new ClientPart("codex", 25)],
            Assert.Single(rows).ClientParts);
    }
}
