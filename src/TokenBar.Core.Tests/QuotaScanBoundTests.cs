using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Q38: the window-usage scan's lower bound also takes the payload windows'
// start (macOS WindowCardLoader.unionStart), so a client with no stored cycle
// is scanned from its window start instead of not at all.
public class QuotaScanBoundTests
{
    private const long Hour = 3_600_000;
    private const long Now = 1_800_000_000_000;
    private const long FiveHours = 5 * 3_600;

    public QuotaScanBoundTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static string Rfc(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static UsageWindow Window(string id, long resetMs, long durationS) =>
        new(
            Label: id,
            UsedPercent: 10,
            RemainingPercent: 90,
            ResetsAt: Rfc(resetMs),
            CardId: $"codex|{id}",
            PaceStatus: new PaceStatus(UsagePaceState.Available, WindowKey: id, DurationSeconds: durationS),
            DurationSeconds: durationS);

    private static AgentUsageSnapshot Agent(
        string client, string? error, string? accountKey, params UsageWindow[] windows) =>
        new(client, "source", "2026-01-01T00:00:00Z", windows, Error: error, AccountKey: accountKey);

    private static AgentUsagePayload Payload(params AgentUsageSnapshot[] agents) =>
        new("2026-01-01T00:00:00Z", agents);

    // A stored series that places nothing: its running group has no duration,
    // so BoundFromMs finds no cycle to bound a scan by (a new client).
    private static QuotaHistorySeries UnplacedSeries(long sampledAtMs) =>
        new("codex", "primary", "session.v1",
        [
            new QuotaHistorySample(
                ResetAt: (Now + 2 * Hour) / 1000, DurationSeconds: 0, QuotaHistoryDurationSource.Provider,
                UsedPercent: 10, SampledAt: sampledAtMs / 1000, QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: true),
        ]);

    [Fact]
    public void ANewClientIsScannedFromItsActiveWindowStart()
    {
        var reset = Now + 2 * Hour;
        var payload = Payload(Agent("codex", null, null, Window("session.v1", reset, FiveHours)));

        Assert.Equal(reset - FiveHours * 1000, QuotaEquivalenceFold.ScanFromMs([], Now, payload));
        // No payload: today's "nothing scanned".
        Assert.Equal(Now, QuotaEquivalenceFold.ScanFromMs([], Now));
    }

    [Fact]
    public void ANewClientsWindowCardSeesAMessageAfterTheResetAndCoversIt()
    {
        var reset = Now - Hour; // reset passed within one duration: idle, scan must reach back to it
        var payload = Payload(Agent("codex", null, null, Window("session.v1", reset, FiveHours)));
        var history = new[] { UnplacedSeries(Now - 2 * Hour) };
        var message = new WindowMessage(
            reset + 10 * 60_000, "codex", "openai", "m", 1000, 0, 0, 0, 0, 5.0, true);
        var confirmed = new UsageAttribution.Table(
            [new UsageAttribution.Record("codex", "openai", UsageAttribution.State.Assigned("codex"))], true);

        // The native export drops messages before the bound it is given.
        QuotaLensProjection.Client CardFor(long fromMs) => QuotaLensProjection.Build(
            history, payload,
            new UsagePayload(
                new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                    PricingMode.BestEffort, CostCoverage.Complete),
                new UsageSummary(0, 0, 0, 0, 0, 0, [], []), [], []),
            new WindowUsage([.. new[] { message }.Where(m => m.Timestamp >= fromMs)], 0, 0),
            WindowEquivalence.FetchOutcome.Succeeded, WindowEquivalence.FetchOutcome.Succeeded,
            confirmed, year: null, new QuotaLensProjection.Selection("codex", string.Empty),
            now: DateTimeOffset.FromUnixTimeMilliseconds(Now),
            windowUsageFromMs: fromMs).Client!;

        var oldBound = QuotaEquivalenceFold.ScanFromMs(history, Now);
        Assert.Equal(Now, oldBound);
        var before = CardFor(oldBound);
        Assert.Empty(before.Mine);
        Assert.False(before.Scan!.Value.Covers(reset));
        var nowAt = DateTimeOffset.FromUnixTimeMilliseconds(Now);
        Assert.Equal(WindowCardState.PlacementPending,
            WindowCardText.State(before.Selected, WindowEquivalence.FetchOutcome.Succeeded, nowAt, before.Scan));

        var bound = QuotaEquivalenceFold.ScanFromMs(history, Now, payload);
        Assert.Equal(reset - FiveHours * 1000, bound);
        var after = CardFor(bound);
        Assert.Single(after.Mine);
        Assert.True(after.Scan!.Value.Covers(reset));
        Assert.Equal(WindowCardState.Chart,
            WindowCardText.State(after.Selected, WindowEquivalence.FetchOutcome.Succeeded, nowAt, after.Scan));
    }

    // Pins PayloadStartMs's inlined Unavailable test to WindowCardText.Resolve.
    [Theory]
    [InlineData(5 * 3_600_000L)]
    [InlineData(1_000L)]
    [InlineData(0L)]
    [InlineData(-1_000L)]
    public void PayloadStartAgreesWithResolveAtTheBoundaries(long durationMs)
    {
        foreach (var offset in new long[] { -durationMs - 1000, -durationMs, -durationMs + 1000, -1000, 0, 1000,
                     durationMs - 1000, durationMs, durationMs + 1000 })
        {
            var reset = Now + offset;
            var payload = Payload(Agent("codex", null, null, Window("w", reset, durationMs / 1000)));
            var resolved = WindowCardText.Resolve(reset / 1000 * 1000, durationMs, Now, null);
            Assert.True(
                (QuotaEquivalenceFold.PayloadStartMs(payload, Now) is not null)
                    == (resolved.Kind != WindowCardText.WindowResolutionKind.Unavailable),
                $"duration {durationMs} offset {offset}: {resolved.Kind}");
        }
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(100L, null, true)]
    [InlineData(100L, 200L, true)]
    [InlineData(200L, 200L, false)]
    [InlineData(300L, 200L, false)]
    public void NeedsRescanOnlyWhenThePayloadReachesBackPastThePublishedBound(long? start, long? from, bool expected) =>
        Assert.Equal(expected, QuotaEquivalenceFold.NeedsRescan(start, from));

    [Theory]
    [InlineData(-6)] // reset more than one duration past
    [InlineData(6)]  // reset more than one duration ahead
    public void AWindowThatResolvesUnavailableDoesNotPullTheBound(long resetOffsetHours)
    {
        var payload = Payload(Agent("codex", null, null, Window("session.v1", Now + resetOffsetHours * Hour, FiveHours)));

        Assert.Null(QuotaEquivalenceFold.PayloadStartMs(payload, Now));
        Assert.Equal(Now, QuotaEquivalenceFold.ScanFromMs([], Now, payload));
    }

    [Fact]
    public void AnErroredAgentsWindowsDoNotCount()
    {
        var good = Agent("codex", null, null, Window("session.v1", Now + Hour, FiveHours));
        // Strictly earlier start (10 h window) than the good agent's, so only
        // skipping the errored agent keeps the bound at the good one.
        var errored = Agent("claude", "boom", null, Window("w", Now + Hour, 10 * 3_600));

        Assert.Equal(Now + Hour - FiveHours * 1000, QuotaEquivalenceFold.PayloadStartMs(Payload(good, errored), Now));
    }

    // Locks existing behavior (passes on cdd5989 too; not an old != new test):
    // PayloadStartMs skips an errored agent, yet the store half of the bound
    // still counts that agent's completed cycle, so its history card loses no
    // scan range. Without a stored cycle the errored agent adds nothing.
    [Fact]
    public void AnErroredAgentsStoredCycleStillBoundsTheScanAndWithoutOneItAddsNothing()
    {
        var resetS = (Now - 20 * Hour) / 1000;
        var cycleStart = resetS * 1000 - FiveHours * 1000;
        var history = new[]
        {
            new QuotaHistorySeries("claude", "primary", "session.v1",
            [
                new QuotaHistorySample(resetS, FiveHours, QuotaHistoryDurationSource.Provider,
                    UsedPercent: 10, SampledAt: resetS - 4 * 3_600, QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
                new QuotaHistorySample(resetS, FiveHours, QuotaHistoryDurationSource.Provider,
                    UsedPercent: 40, SampledAt: resetS - 3_600, QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: false),
            ]),
        };
        var errored = Agent("claude", "boom", null, Window("session.v1", Now + Hour, FiveHours));
        var good = Agent("codex", null, null, Window("w", Now + Hour, FiveHours));

        Assert.Equal(cycleStart, QuotaEquivalenceFold.ScanFromMs(history, Now, Payload(errored)));
        Assert.Equal(cycleStart, QuotaEquivalenceFold.ScanFromMs(history, Now, Payload(errored, good)));
        // No stored cycle: same as the agent being absent.
        Assert.Equal(
            QuotaEquivalenceFold.ScanFromMs([], Now, Payload(good)),
            QuotaEquivalenceFold.ScanFromMs([], Now, Payload(errored, good)));
        Assert.Equal(QuotaEquivalenceFold.ScanFromMs([], Now), QuotaEquivalenceFold.ScanFromMs([], Now, Payload(errored)));
    }

    [Fact]
    public void TheStoreBoundIsKeptWhenItIsEarlierThanThePayloadStart()
    {
        var storeStart = Now - 20 * Hour;
        var history = new[]
        {
            new QuotaHistorySeries("codex", "primary", "session.v1",
            [
                new QuotaHistorySample(
                    ResetAt: (Now + 2 * Hour) / 1000, DurationSeconds: 25 * 3_600, QuotaHistoryDurationSource.Provider,
                    UsedPercent: 10, SampledAt: (storeStart + Hour) / 1000, QuotaHistorySampleOrigin.LiveV3, IsActiveGroup: true),
            ]),
        };
        var payload = Payload(Agent("codex", null, null, Window("session.v1", Now + Hour, FiveHours)));

        var store = QuotaEquivalenceFold.ScanFromMs(history, Now);
        Assert.True(store < Now + Hour - FiveHours * 1000);
        Assert.Equal(store, QuotaEquivalenceFold.ScanFromMs(history, Now, payload));
    }

    // The account scan shares the global bound, which already unions every
    // agent in the payload — so an extra account's own window pulls it.
    [Fact]
    public void AnExtraAccountsOwnWindowPullsTheBoundTheAccountScanUses()
    {
        var main = Agent("claude", null, null, Window("session.v1", Now + 4 * Hour, FiveHours));
        var extra = Agent("claude", null, @"D:\work", Window("session.v1", Now + Hour, FiveHours));

        var fresh = QuotaEquivalenceFold.ScanFromMs([], Now, Payload(main, extra));
        Assert.Equal(Now + Hour - FiveHours * 1000, fresh);
        Assert.Equal(fresh, QuotaEquivalenceFold.NextAccountBound(prior: null, fresh, scanned: true, keptPrior: false));
    }
}
