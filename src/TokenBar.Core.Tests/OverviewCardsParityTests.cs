using System.Text.Json;
using System.Text.RegularExpressions;
using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

// G5g/G5o/G5n: the live-session card, a client tab with no local records, and
// the window-history zero note, against macOS UsageTraceCard, OverviewView and
// QuotaHistoryCard.
public class OverviewCardsParityTests
{
    public OverviewCardsParityTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static TraceBucket Bucket(string client, string agent, string model, double rate) =>
        new(client, agent, model, (long)rate, 1, rate);

    [Fact]
    public void TraceRowsFilterBeforeTheyCapAtFive()
    {
        TraceBucket[] trace =
        [
            Bucket("codex", "main", "gpt", 9000),
            .. Enumerable.Range(0, 6).Select(i => Bucket("claude-code", $"a{i}", "opus", 100 + i)),
        ];

        var rows = TraceCollapse.CardRows(trace, new HashSet<string> { "claude" }, detailed: true);

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal("claude", r.Client));
        Assert.Single(TraceCollapse.CardRows(trace, new HashSet<string> { "claude" }, detailed: false));
    }

    [Fact]
    public void TraceHeaderSumsEverySelectedRowOverTheTenMinuteWindow()
    {
        TraceBucket[] trace =
        [
            .. Enumerable.Range(0, 7).Select(i => Bucket("claude-code", $"a{i}", "opus", 1000)),
            Bucket("codex", "main", "gpt", 50_000),
        ];

        Assert.Equal(600, TraceCollapse.WindowSecs);
        Assert.Equal(
            $"last 10m · {Format.CompactTokens(7000)}/m total",
            TraceCollapse.Header(trace, new HashSet<string> { "claude" }));
    }

    [Theory]
    [InlineData(50, 100, 50)]
    [InlineData(1, 100, 4)]
    [InlineData(0, 0, 0)]
    public void TraceBarKeepsASlowRowVisible(double rate, double max, double expected) =>
        Assert.Equal(expected, TraceCollapse.BarPercent(rate, max));

    [Fact]
    public void ClientTabWithoutLocalRecordsSaysSo()
    {
        Assert.True(OverviewScope.HasNoLocalUsage("kiro", ["kiro"], ["claude", "codex"]));
        Assert.False(OverviewScope.HasNoLocalUsage("claude", ["claude"], ["claude"]));
        // Stats keep raw stripe ids; a Claude tab whose stripes say
        // claude-code has local usage.
        Assert.False(OverviewScope.HasNoLocalUsage("claude", ["claude"], ["claude-code"]));
        // The Overview tab draws its chart whatever is present.
        Assert.False(OverviewScope.HasNoLocalUsage(null, [], []));
    }

    [Fact]
    public void HistoryZeroNoteShowsOnlyForAnUndeclaredWindow()
    {
        Assert.True(AttributionOnboardingCard.ShowsHistoryZeroNote(
            new WindowEquivalence.Row.Undeclared(), localUsageUnattributed: false));
        Assert.False(AttributionOnboardingCard.ShowsHistoryZeroNote(
            new WindowEquivalence.Row.Undeclared(), localUsageUnattributed: true));
        Assert.False(AttributionOnboardingCard.ShowsHistoryZeroNote(
            new WindowEquivalence.Row.Unaccounted(12), localUsageUnattributed: false));
    }

    // A macOS translation copied without mapping %lld / %1$@ to {n} would
    // render the printf token on screen, and string.Format would not notice.
    [Theory]
    [InlineData("strings-zh-Hant.json")]
    [InlineData("strings-zh-Hans.json")]
    public void NoTranslationCarriesAPrintfPlaceholder(string table)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, table)))!;
        var printf = new Regex(@"%(\d+\$)?(@|l{0,2}d|0\d+l{0,2}d)");

        foreach (var (key, value) in entries)
        {
            Assert.False(printf.IsMatch(value), $"{table}: {key} → {value}");
        }
    }
}
