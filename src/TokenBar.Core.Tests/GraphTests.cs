using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Delta coverage for Graph.swift → Interop/Graph.cs: saturating Int64 folds,
// TokenBreakdown.Total, and the hidden-client-aware tray totals.
public class GraphTests
{
    [Fact]
    public void SaturatingAddMatchesPlusForNormalValues() =>
        Assert.Equal(30L, 10L.SaturatingAdd(20L));

    [Fact]
    public void SaturatingAddClampsPositiveOverflowToMax() =>
        Assert.Equal(long.MaxValue, long.MaxValue.SaturatingAdd(1));

    [Fact]
    public void SaturatingAddClampsNegativeOverflowToMin() =>
        Assert.Equal(long.MinValue, long.MinValue.SaturatingAdd(-1));

    [Fact]
    public void SaturatingAddKeysClampOffTheAddendSign()
    {
        // max + max overflows; addend > 0 → clamps to max.
        Assert.Equal(long.MaxValue, long.MaxValue.SaturatingAdd(long.MaxValue));
        // min + min overflows; addend < 0 → clamps to min.
        Assert.Equal(long.MinValue, long.MinValue.SaturatingAdd(long.MinValue));
    }

    [Fact]
    public void TokenBreakdownTotalSums() =>
        Assert.Equal(150L, new TokenBreakdown(10, 20, 15, 5, 100).Total);

    [Fact]
    public void TokenBreakdownTotalSaturatesCorruptLane() =>
        Assert.Equal(long.MaxValue, new TokenBreakdown(long.MaxValue, 1, 0, 0, 0).Total);

    private static ContributionClient Client(string id, TokenBreakdown tokens, double cost) =>
        new(id, "m", "p", tokens, cost, Messages: 1);

    private static UsagePayload PayloadWith(params Contribution[] contributions) =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-07-01", "2026-07-02"), PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(999, 9.9, 2, 2, 5, 5, ["claude", "codex"], ["m"]),
            [],
            contributions);

    [Fact]
    public void TrayTotalsEmptyHiddenTakesSummaryFastPath()
    {
        var payload = PayloadWith(
            new Contribution(
                "2026-07-02", new ContributionTotals(50, 0.7, 3), 2,
                new TokenBreakdown(0, 0, 0, 0, 0),
                [Client("claude", new TokenBreakdown(10, 20, 15, 5, 0), 0.7)]));

        var t = payload.TrayTotals(new HashSet<string>(), today: "2026-07-02");
        // Fast path reads Summary + the today contribution's totals verbatim.
        Assert.Equal(999L, t.TotalTokens);
        Assert.Equal(9.9, t.TotalCost);
        Assert.Equal(50L, t.TodayTokens);
        Assert.Equal(0.7, t.TodayCost);
    }

    [Fact]
    public void TrayTotalsExcludesHiddenClientsOnSlowPath()
    {
        var payload = PayloadWith(
            new Contribution(
                "2026-07-01", new ContributionTotals(0, 0, 0), 1,
                new TokenBreakdown(0, 0, 0, 0, 0),
                [
                    Client("claude", new TokenBreakdown(100, 0, 0, 0, 0), 1.0),
                    Client("codex", new TokenBreakdown(40, 0, 0, 0, 0), 0.4),
                ]),
            new Contribution(
                "2026-07-02", new ContributionTotals(0, 0, 0), 1,
                new TokenBreakdown(0, 0, 0, 0, 0),
                [
                    Client("claude", new TokenBreakdown(10, 0, 0, 0, 0), 0.1),
                    Client("codex", new TokenBreakdown(7, 0, 0, 0, 0), 0.07),
                ]));

        var t = payload.TrayTotals(new HashSet<string> { "codex" }, today: "2026-07-02");
        // Only claude survives: 100 + 10 total, 10 today.
        Assert.Equal(110L, t.TotalTokens);
        Assert.Equal(1.1, t.TotalCost, 6);
        Assert.Equal(10L, t.TodayTokens);
        Assert.Equal(0.1, t.TodayCost, 6);
    }

    // ---- TodayAllowlisted: the Discord presence's positive allowlist ----
    //
    // The aggregate a fast path would read (todayEntry.Totals, Summary) is
    // summed upstream in the Rust core across EVERY client, unregistered ones
    // included. These fixtures make that aggregate disagree with the stripes,
    // so a regression back onto an aggregate is visible in the numbers.

    private static readonly IReadOnlySet<string> Registered =
        new HashSet<string>(ClientRegistry.AllIds, StringComparer.Ordinal);

    private static Contribution Day(string date, long totalTokens, double totalCost, params ContributionClient[] clients) =>
        new(date, new ContributionTotals(totalTokens, totalCost, 1), 1, new TokenBreakdown(0, 0, 0, 0, 0), clients);

    private static AllowlistedToday Allowlisted(
        UsagePayload payload, IReadOnlySet<string>? hidden = null, IReadOnlySet<string>? only = null) =>
        payload.TodayAllowlisted(
            hidden ?? new HashSet<string>(), "2026-07-02", only ?? Registered, ClientRegistry.CanonicalClient);

    [Fact]
    public void AllowlistWithUnregisteredOnlyGraphIsZero()
    {
        var payload = PayloadWith(Day(
            "2026-07-02", 500, 5.0,
            Client("cc-mirror/acme-internal", new TokenBreakdown(500, 0, 0, 0, 0), 5.0)));

        var t = Allowlisted(payload);

        Assert.Equal(0L, t.Tokens);
        Assert.Equal(0.0, t.Cost);
        Assert.Null(t.TopClient);
    }

    [Fact]
    public void AllowlistNeverReadsTheUpstreamAggregateEvenWithNothingHidden()
    {
        // Upstream totals 150/1.5 include the unregistered client; the
        // allowlisted figure is claude's stripe alone.
        var payload = PayloadWith(Day(
            "2026-07-02", 150, 1.5,
            Client("claude", new TokenBreakdown(100, 0, 0, 0, 0), 1.0),
            Client("cc-mirror/acme-internal", new TokenBreakdown(50, 0, 0, 0, 0), 0.5)));

        var t = Allowlisted(payload);
        Assert.Equal(100L, t.Tokens);
        Assert.Equal(1.0, t.Cost, 6);
        Assert.Equal("claude", t.TopClient);

        // The existing tray caller keeps its fast path, untouched.
        var tray = payload.TrayTotals(new HashSet<string>(), "2026-07-02");
        Assert.Equal(150L, tray.TodayTokens);
        Assert.Equal(999L, tray.TotalTokens);
    }

    [Fact]
    public void HiddenTopClientContributesNeitherNameNorNumbers()
    {
        var payload = PayloadWith(Day(
            "2026-07-02", 1_010, 10.1,
            Client("claude", new TokenBreakdown(1_000, 0, 0, 0, 0), 10.0),
            Client("codex", new TokenBreakdown(10, 0, 0, 0, 0), 0.1)));

        var t = Allowlisted(payload, hidden: new HashSet<string> { "claude" });

        Assert.Equal("codex", t.TopClient);
        Assert.Equal(10L, t.Tokens);
        Assert.Equal(0.1, t.Cost, 6);
    }

    [Fact]
    public void TopClientAndFiguresFoldEveryContributionDatedToday()
    {
        // Two entries share today's date. The figures and the top client must
        // come from the same set: codex wins on the combined day (60 > 50)
        // though it loses on the last entry alone (20 < 50).
        var payload = PayloadWith(
            Day("2026-07-01", 0, 0, Client("amp", new TokenBreakdown(900, 0, 0, 0, 0), 9)),
            Day("2026-07-02", 0, 0, Client("codex", new TokenBreakdown(40, 0, 0, 0, 0), 0.4)),
            Day("2026-07-02", 0, 0,
                Client("claude", new TokenBreakdown(50, 0, 0, 0, 0), 0.5),
                Client("codex", new TokenBreakdown(20, 0, 0, 0, 0), 0.2)));

        var t = Allowlisted(payload);

        Assert.Equal(110L, t.Tokens);
        Assert.Equal(1.1, t.Cost, 6);
        Assert.Equal("codex", t.TopClient);
    }

    [Fact]
    public void AliasStripeCountsUnderItsCanonicalId()
    {
        // `claude-code` is the live-tail alias of `claude`; the picker, the
        // hidden set and the registry all key on the canonical id.
        var payload = PayloadWith(Day(
            "2026-07-02", 0, 0,
            Client("claude-code", new TokenBreakdown(40, 0, 0, 0, 0), 0.4),
            Client("claude", new TokenBreakdown(30, 0, 0, 0, 0), 0.3),
            Client("codex", new TokenBreakdown(50, 0, 0, 0, 0), 0.5)));

        // Most used: 70 under "claude" beats codex's 50.
        var mostUsed = Allowlisted(payload);
        Assert.Equal("claude", mostUsed.TopClient);
        Assert.Equal(120L, mostUsed.Tokens);

        // Only("claude") includes the alias stripe.
        var only = Allowlisted(payload, only: new HashSet<string> { "claude" });
        Assert.Equal(70L, only.Tokens);

        // Hiding the canonical id hides the alias stripe too.
        var hidden = Allowlisted(payload, hidden: new HashSet<string> { "claude" });
        Assert.Equal(50L, hidden.Tokens);
        Assert.Equal("codex", hidden.TopClient);
    }

    [Fact]
    public void TopClientBreaksTiesDeterministically()
    {
        // codex spreads 60 over two model stripes; claude has one stripe of 50.
        // The busiest CLIENT is codex, though claude has the largest stripe.
        Assert.Equal("codex", Allowlisted(PayloadWith(Day("2026-07-02", 0, 0,
            Client("claude", new TokenBreakdown(50, 0, 0, 0, 0), 0.5),
            Client("codex", new TokenBreakdown(30, 0, 0, 0, 0), 0.1),
            Client("codex", new TokenBreakdown(30, 0, 0, 0, 0), 0.1)))).TopClient);
        // Token tie → higher cost wins; full tie → ordinally smaller id.
        Assert.Equal("codex", Allowlisted(PayloadWith(Day("2026-07-02", 0, 0,
            Client("codex", new TokenBreakdown(10, 0, 0, 0, 0), 0.2),
            Client("amp", new TokenBreakdown(10, 0, 0, 0, 0), 0.1)))).TopClient);
        Assert.Equal("amp", Allowlisted(PayloadWith(Day("2026-07-02", 0, 0,
            Client("codex", new TokenBreakdown(10, 0, 0, 0, 0), 0.1),
            Client("amp", new TokenBreakdown(10, 0, 0, 0, 0), 0.1)))).TopClient);
    }
}
