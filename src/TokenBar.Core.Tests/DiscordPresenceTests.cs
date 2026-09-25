using System.Text.Json;
using TokenBar.Interop;
using Component = TokenBar.Core.DiscordPresence.Component;
using CostStyle = TokenBar.Core.DiscordPresence.CostStyle;
using Selection = TokenBar.Core.DiscordPresence.Selection;

namespace TokenBar.Core.Tests;

// F2: the pure payload builder, its strict preference reads, and the wire JSON
// walked byte by byte. Ported from the macOS selftest's DiscordPresence /
// DiscordIPC assertions.
public sealed class DiscordPresenceTests : IDisposable
{
    private const string Today = "2026-09-25";

    private static readonly IReadOnlySet<Component> All = DiscordPresence.DefaultComponents;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-discord-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    internal static ContributionClient Stripe(string id, long tokens, double cost) =>
        new(id, "m", "p", new TokenBreakdown(tokens, 0, 0, 0, 0), cost, 1);

    internal static UsagePayload Graph(params ContributionClient[] today) =>
        new(
            new UsageMeta("g", "v", new DateRange(Today, Today), PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(
                today.Sum(c => c.Tokens.Total), today.Sum(c => c.Cost), 1, 1, 0, 0,
                [.. today.Select(c => c.Client).Distinct()], ["m"]),
            [],
            [
                new Contribution(
                    Today,
                    new ContributionTotals(today.Sum(c => c.Tokens.Total), today.Sum(c => c.Cost), 1),
                    1, new TokenBreakdown(0, 0, 0, 0, 0), today),
            ]);

    private static DiscordPresence.Payload? Build(
        UsagePayload graph, IReadOnlySet<string>? hidden = null, CostStyle style = CostStyle.Banded,
        IReadOnlySet<Component>? components = null, Selection? selection = null) =>
        DiscordPresence.Build(
            graph, hidden ?? new HashSet<string>(), Today, style, components ?? All,
            selection ?? new Selection.MostUsed());

    private SettingsStore StoreWithJson(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    // ---- payload ---------------------------------------------------------

    [Fact]
    public void DefaultCompositionIsTokensThenClientAndCost()
    {
        var payload = Build(Graph(Stripe("claude", 1_234_567, 12.34)));

        Assert.NotNull(payload);
        Assert.Equal("1.2M tokens today", payload.Details);
        Assert.Equal("Claude Code · $10-50", payload.State);
        Assert.Equal("syrtis", payload.LargeImageKey);
    }

    [Fact]
    public void OneComponentOmitsStateEntirely()
    {
        var payload = Build(Graph(Stripe("claude", 5_000, 1)), components: new HashSet<Component> { Component.Cost });

        Assert.NotNull(payload);
        Assert.Equal("<$10", payload.Details);
        Assert.False(payload.Fields.ContainsKey("state"));
    }

    [Fact]
    public void NothingPublishesAtZeroUsageEmptyComponentsOrMalformedSelection()
    {
        Assert.Null(Build(Graph(Stripe("claude", 0, 0))));
        Assert.Null(Build(Graph(Stripe("claude", 5_000, 1)), components: new HashSet<Component>()));
        Assert.Null(Build(Graph(Stripe("claude", 5_000, 1)), selection: new Selection.Malformed()));
        // An unregistered selection never widens to most-used.
        Assert.Null(Build(Graph(Stripe("claude", 5_000, 1)), selection: new Selection.Only("not-a-client")));
    }

    [Fact]
    public void CostOnlyDayStillPublishes()
    {
        // `||`, not `&&`: a day can carry cost with no tokens.
        var payload = Build(Graph(Stripe("claude", 0, 3)));
        Assert.NotNull(payload);
        Assert.Equal("<1K tokens today", payload.Details);
    }

    [Fact]
    public void NonFiniteCostBlocksOnlyWhenCostIsPublished()
    {
        var graph = Graph(Stripe("claude", 5_000, double.PositiveInfinity));
        Assert.Null(Build(graph));
        Assert.NotNull(Build(graph, components: new HashSet<Component> { Component.Tokens }));
    }

    [Fact]
    public void UnregisteredClientContributesNothingAndIsNeverNamed()
    {
        var payload = Build(Graph(
            Stripe("claude", 2_000, 1),
            Stripe("cc-mirror/acme-internal", 9_000_000, 900)));

        Assert.NotNull(payload);
        Assert.Equal("2K tokens today", payload.Details);
        Assert.Equal("Claude Code · <$10", payload.State);
        Assert.DoesNotContain(payload.Fields.Values, v => v.Contains("acme", StringComparison.OrdinalIgnoreCase));
        Assert.Null(Build(Graph(Stripe("cc-mirror/acme-internal", 9_000, 1))));
    }

    [Fact]
    public void HiddenTopClientLeavesNeitherNameNorNumbers()
    {
        var graph = Graph(Stripe("claude", 5_000_000, 400), Stripe("codex", 2_000, 1));

        var payload = Build(graph, hidden: new HashSet<string> { "claude" });

        Assert.NotNull(payload);
        Assert.Equal("2K tokens today", payload.Details);
        Assert.Equal("Codex · <$10", payload.State);
        // Selecting the hidden client cannot defeat hiding.
        Assert.Null(Build(graph, hidden: new HashSet<string> { "claude" }, selection: new Selection.Only("claude")));
    }

    [Fact]
    public void NamedSelectionPublishesOnlyThatClient()
    {
        var payload = Build(
            Graph(Stripe("claude", 5_000_000, 400), Stripe("codex", 2_000, 1)),
            selection: new Selection.Only("codex"));

        Assert.NotNull(payload);
        Assert.Equal("2K tokens today", payload.Details);
        Assert.Equal("Codex · <$10", payload.State);
    }

    [Theory]
    [InlineData(-5.0, "<$10")]
    [InlineData(0.0, "<$10")]
    [InlineData(9.99, "<$10")]
    [InlineData(10.0, "$10-50")]
    [InlineData(50.0, "$50-100")]
    [InlineData(100.0, "$100-250")]
    [InlineData(250.0, "$250-500")]
    [InlineData(500.0, "$500-1000")]
    [InlineData(1000.0, "$1000+")]
    [InlineData(double.NaN, "<$10")]
    [InlineData(double.PositiveInfinity, "<$10")]
    public void CostBucketBands(double cost, string expected) =>
        Assert.Equal(expected, DiscordPresence.CostBucket(cost));

    [Theory]
    [InlineData(0.0, "$0")]
    [InlineData(-3.0, "$0")]
    [InlineData(0.4, "$0")]
    [InlineData(0.5, "$1")]
    [InlineData(2.5, "$3")] // half away from zero, not banker's "$2"
    [InlineData(12.34, "$12")]
    [InlineData(999_999.4, "$999999")]
    [InlineData(999_999.5, "$1000000+")]
    [InlineData(1e308, "$1000000+")]
    [InlineData(double.NaN, "$0")]
    [InlineData(double.PositiveInfinity, "$0")]
    public void WholeDollarsNeverCentsAndTotal(double cost, string expected) =>
        Assert.Equal(expected, DiscordPresence.WholeDollars(cost));

    [Fact]
    public void WholeDollarsOnlyOnOptIn()
    {
        var graph = Graph(Stripe("claude", 5_000, 12.34));
        Assert.Equal("Claude Code · $10-50", Build(graph)!.State);
        Assert.Equal("Claude Code · $12", Build(graph, style: CostStyle.WholeDollars)!.State);
    }

    [Theory]
    [InlineData(-42L, "<1K")]
    [InlineData(0L, "<1K")]
    [InlineData(999L, "<1K")]
    [InlineData(1_000L, "1K")]
    [InlineData(1_234_567L, "1.2M")]
    public void TokenBandNeverExact(long tokens, string expected) =>
        Assert.Equal(expected, DiscordPresence.TokenBand(tokens));

    // ---- strict preference reads ----------------------------------------

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"tokenbar.discord.enabled\": true}", true)]
    [InlineData("{\"tokenbar.discord.enabled\": false}", false)]
    [InlineData("{\"tokenbar.discord.enabled\": \"true\"}", false)]
    [InlineData("{\"tokenbar.discord.enabled\": 1}", false)]
    public void EnabledRequiresARealJsonTrue(string json, bool expected) =>
        Assert.Equal(expected, DiscordPresence.Enabled(StoreWithJson(json)));

    [Theory]
    [InlineData("{}", CostStyle.Banded)]
    [InlineData("{\"tokenbar.discord.wholeDollars\": true}", CostStyle.WholeDollars)]
    [InlineData("{\"tokenbar.discord.wholeDollars\": \"true\"}", CostStyle.Banded)]
    [InlineData("{\"tokenbar.discord.wholeDollars\": 1}", CostStyle.Banded)]
    public void CostStyleIsBandedUnlessStrictlyOptedIn(string json, CostStyle expected) =>
        Assert.Equal(expected, DiscordPresence.ReadCostStyle(StoreWithJson(json)));

    [Fact]
    public void ComponentsDistinguishAbsentFromMalformed()
    {
        Assert.True(DiscordPresence.Components(StoreWithJson("{}")).SetEquals(All));
        Assert.Empty(DiscordPresence.Components(StoreWithJson("{\"tokenbar.discord.components\": 1}")));
        Assert.Empty(DiscordPresence.Components(StoreWithJson("{\"tokenbar.discord.components\": \"\"}")));
        Assert.True(DiscordPresence.Components(
                StoreWithJson("{\"tokenbar.discord.components\": \" cost, tokens,bogus,CLIENT\"}"))
            .SetEquals([Component.Tokens, Component.Cost]));
        Assert.Equal("tokens,cost", DiscordPresence.RawComponents(
            new HashSet<Component> { Component.Cost, Component.Tokens }));
    }

    [Fact]
    public void SelectionDistinguishesAbsentEmptyMalformedAndNamed()
    {
        Assert.Equal(new Selection.MostUsed(), DiscordPresence.ReadSelection(StoreWithJson("{}")));
        Assert.Equal(new Selection.MostUsed(), DiscordPresence.ReadSelection(
            StoreWithJson("{\"tokenbar.discord.client\": \"\"}")));
        Assert.Equal(new Selection.Malformed(), DiscordPresence.ReadSelection(
            StoreWithJson("{\"tokenbar.discord.client\": 7}")));
        Assert.Equal(new Selection.Only("codex"), DiscordPresence.ReadSelection(
            StoreWithJson("{\"tokenbar.discord.client\": \"codex\"}")));
    }

    [Fact]
    public void TestArgumentsOutrankTheSwitch()
    {
        // Every non-user run mode App.xaml.cs parses.
        foreach (var flag in new[] { "--startup-smoke", "--dump-tray-icons", "--update-dialog-demo", "--graph3d", "--soak3d" })
        {
            Assert.False(DiscordPresence.MayConnect(["TokenBar.App.exe", flag], enabled: true));
        }

        Assert.True(DiscordPresence.MayConnect(["TokenBar.App.exe", "--settings"], enabled: true));
        Assert.False(DiscordPresence.MayConnect(["TokenBar.App.exe"], enabled: false));
    }

    [Fact]
    public void SelectableClientsOfferOnlyPublishableRows()
    {
        var present = new[] { "claude", "codex", "cc-mirror/acme", "amp" };
        var offered = DiscordPresence.SelectableClients(present, "amp", "codex,claude", new Selection.MostUsed());
        Assert.Equal(["codex", "claude"], offered);

        // A stored, still-registered selection that stopped qualifying stays.
        Assert.Equal(["codex", "claude", "amp"],
            DiscordPresence.SelectableClients(present, "amp", "codex,claude", new Selection.Only("amp")));
        // An unknown one is never listed.
        Assert.Equal(["codex", "claude"],
            DiscordPresence.SelectableClients(present, "amp", "codex,claude", new Selection.Only("cc-mirror/acme")));
        // Before the first graph: the registry, minus hidden...
        Assert.DoesNotContain("amp",
            DiscordPresence.SelectableClients(null, "amp", "", new Selection.MostUsed()));
        // ...and a stored selection that is hidden still stays listed there too.
        var beforeGraph = DiscordPresence.SelectableClients(null, "amp", "", new Selection.Only("amp"));
        Assert.Single(beforeGraph, id => id == "amp");
        Assert.Equal("amp", beforeGraph[^1]);
    }

    [Fact]
    public void AliasStripeCountsForMostUsedAndForItsNamedCanonicalId()
    {
        var graph = Graph(Stripe("claude-code", 1_500, 0.5), Stripe("codex", 1_200, 0.5));

        var mostUsed = Build(graph);
        Assert.NotNull(mostUsed);
        Assert.Equal("2.7K tokens today", mostUsed.Details);
        Assert.StartsWith("Claude Code", mostUsed.State);

        var named = Build(graph, selection: new Selection.Only("claude"));
        Assert.NotNull(named);
        Assert.Equal("1.5K tokens today", named.Details);

        // Hiding the canonical id hides the alias stripe.
        Assert.Null(Build(graph, hidden: new HashSet<string> { "claude" }, selection: new Selection.Only("claude")));
    }

    // ---- wire bytes -----------------------------------------------------

    private static (List<string> Keys, List<string> Leaves) Walk(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var keys = new List<string>();
        var leaves = new List<string>();
        void Visit(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        keys.Add(property.Name);
                        Visit(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Visit(item);
                    }

                    break;
                case JsonValueKind.String:
                    leaves.Add(element.GetString()!);
                    break;
                default:
                    leaves.Add(element.GetRawText());
                    break;
            }
        }

        Visit(document.RootElement);
        keys.Sort(StringComparer.Ordinal);
        leaves.Sort(StringComparer.Ordinal);
        return (keys, leaves);
    }

    private static List<string> Sorted(params string[] values)
    {
        var list = values.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    [Fact]
    public void ActivityFrameCarriesExactlyThePinnedKeysAndLeaves()
    {
        var payload = Build(Graph(Stripe("claude", 1_234_567, 12.34)))!;

        var (keys, leaves) = Walk(DiscordIpc.ActivityJson(payload, 4242, "nonce-1"));

        Assert.Equal(
            Sorted("cmd", "args", "pid", "activity", "details", "state", "assets", "large_image",
                "buttons", "label", "url", "nonce"),
            keys);
        Assert.Equal(
            Sorted("SET_ACTIVITY", "4242", "1.2M tokens today", "Claude Code · $10-50", "syrtis",
                "View on GitHub", "https://github.com/Nanako0129/Syrtis-Windows", "nonce-1"),
            leaves);
    }

    [Fact]
    public void ClearFrameIsActivityNullWithoutButtons()
    {
        var (keys, leaves) = Walk(DiscordIpc.ActivityJson(null, 4242, "nonce-2"));

        Assert.Equal(Sorted("cmd", "args", "pid", "activity", "nonce"), keys);
        Assert.Equal(Sorted("SET_ACTIVITY", "4242", "null", "nonce-2"), leaves);
    }

    [Fact]
    public void HandshakeCarriesOnlyVersionAndApplicationId()
    {
        var (keys, leaves) = Walk(DiscordIpc.HandshakeJson());

        Assert.Equal(Sorted("client_id", "v"), keys);
        Assert.Equal(Sorted("1534085299163107348", "1"), leaves);
    }

    [Fact]
    public void TransportConstantsArePinned()
    {
        Assert.Equal("1534085299163107348", DiscordIpc.ApplicationId);
        Assert.Equal("View on GitHub", DiscordIpc.ButtonLabel);
        Assert.Equal("https://github.com/Nanako0129/Syrtis-Windows", DiscordIpc.ButtonUrl);
        Assert.Equal("syrtis", DiscordPresence.LargeImageKey);
    }
}
