using TokenBar.App;
using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>The window card's "has local records" gate is year-independent:
/// a client recorded by an earlier graph keeps its card scanned on a year
/// where it has no records (macOS cb77fa36 WCP2-year).</summary>
public class LocalRecordClientsTests
{
    private static SettingsStore Fresh() =>
        new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json"));

    [Fact]
    public void AClientSeenInAnyYearStaysScannedOnAYearWithoutIt()
    {
        var store = Fresh();
        // An all-time (or earlier-year) graph had codex records.
        LocalRecordClients.Record(store, ["claude", "codex"]);
        // The selected year's graph has no codex.
        var gate = LocalRecordClients.Union(store, ["claude"]);
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords("codex", gate, []),
            "a client with records in another year must keep its card scanned");
        // Control: a client never seen in any graph is still quota-only.
        Assert.True(QuotaLensProjection.TabHasNoLocalRecords("grok", gate, []));
        // And the selected year's own clients count without being recorded.
        Assert.False(QuotaLensProjection.TabHasNoLocalRecords("opencode", LocalRecordClients.Union(store, ["opencode"]), []));
    }

    [Fact]
    public void TheSetOnlyGrowsAndSurvivesAReload()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new SettingsStore(path);
        LocalRecordClients.Record(store, ["codex"]);
        LocalRecordClients.Record(store, ["claude"]);
        LocalRecordClients.Record(store, []);
        Assert.Equal("claude,codex", new SettingsStore(path).GetString(LocalRecordClients.Key));
    }
    /// <summary>A graph with nothing new raises no Changed (the store drops an
    /// equal value), so background graph polls cannot keep signalling the
    /// Settings window.</summary>
    [Fact]
    public void RecordWritesOnlyWhenSomethingNewAppears()
    {
        var store = Fresh();
        var changes = 0;
        store.Changed += key => { if (key == LocalRecordClients.Key) { changes++; } };
        LocalRecordClients.Record(store, ["codex", "claude"]);
        Assert.Equal(1, changes);
        LocalRecordClients.Record(store, ["claude"]);
        LocalRecordClients.Record(store, ["codex", "claude"]);
        Assert.Equal(1, changes);
        LocalRecordClients.Record(store, ["opencode"]);
        Assert.Equal(2, changes);
    }
}
