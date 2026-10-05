using System.Text.Json;
using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

/// <summary>
/// A model-scoped window (Claude's <c>limits[]</c> <c>weekly_scoped</c> entry,
/// mapped in Rust by <c>append_claude_scoped_windows</c>) reaching every
/// surface that has to count only its model: the wire, the one lookup
/// (<see cref="ModelScope.Of"/>), the window card's bars, the history rows and
/// the equivalence estimate. Port of macOS ac38f477 (#231 in v1.14.0): before
/// it, a "Fable only" window's bars, history and estimate counted every model.
/// </summary>
public class ModelScopeWiringTests
{
    private const long FiveHours = 5 * 3_600;
    private const string Scoped = "weekly_scoped.fable.v1";

    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };

    public ModelScopeWiringTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static WindowMessage Message(long timestampMs, string model, long tokens, double cost) =>
        new(timestampMs, "claude", "anthropic", model, tokens, 0, 0, 0, 0, cost, true);

    private static UsageAttribution.Table Confirmed() =>
        new([new UsageAttribution.Record("claude", "anthropic", UsageAttribution.State.Assigned("claude"))],
            IsWritable: true);

    private static UsagePayload EmptyGraph() =>
        new(
            new UsageMeta("g", "v", new DateRange("2026-01-01", "2026-01-01"),
                PricingMode.BestEffort, CostCoverage.Complete),
            new UsageSummary(0, 0, 0, 0, 0, 0, [], []),
            [],
            []);

    private static UsageWindow Window(string cardId, string label, string? modelScope) =>
        new UsageWindow(
            Label: label,
            UsedPercent: 15,
            RemainingPercent: 85,
            CardId: cardId,
            PaceStatus: new PaceStatus(UsagePaceState.Available, WindowKey: cardId))
        // The payload window the running cycle is placed from (reset 6,000 s,
        // 5 h) and, via Build's `now`, still ahead of it.
        with
        {
            ModelScope = modelScope,
            ResetsAt = DateTimeOffset.FromUnixTimeSeconds(6_000).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            DurationSeconds = FiveHours,
        };

    private static AgentUsagePayload Quota(params UsageWindow[] windows) =>
        new("2026-01-01T00:00:00Z",
            [new AgentUsageSnapshot("claude", "source", "2026-01-01T00:00:00Z", windows)]);

    private static QuotaHistorySample Sample(double used, long sampledAt, long resetAt, bool active) =>
        new(
            ResetAt: resetAt,
            DurationSeconds: FiveHours,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: used,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: active);

    /// <summary>Two completed cycles (resets at 2,000 s and 4,000 s) and a
    /// running one (samples at 5,000 s and 5,900 s, reset 6,000 s).</summary>
    private static QuotaHistorySeries Series(string windowKey) =>
        new("claude", "primary", windowKey,
        [
            Sample(40, 1_500, 2_000, active: false),
            Sample(70, 3_500, 4_000, active: false),
            Sample(10, 5_000, 6_000, active: true),
            Sample(15, 5_900, 6_000, active: true),
        ]);

    private static QuotaLensProjection.Model Build(
        AgentUsagePayload quota, string windowKey, IReadOnlyList<WindowMessage> messages) =>
        Build(quota, Series(windowKey), messages);

    private static QuotaLensProjection.Model Build(
        AgentUsagePayload quota, QuotaHistorySeries series, IReadOnlyList<WindowMessage> messages) =>
        QuotaLensProjection.Build(
            [series], quota, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(), year: null,
            new QuotaLensProjection.Selection("claude", string.Empty),
            now: DateTimeOffset.FromUnixTimeSeconds(5_950));

    // ---- the wire ----------------------------------------------------------

    [Fact]
    public void ModelScopeDecodesFromTheShapeRustSerializesAndRoundTrips()
    {
        // `modelScope` as `UsageWindowWire` emits it (agent_usage.rs, test
        // maps_claude_fable_scoped_weekly_limit asserts the Rust half).
        const string json = """
            {
              "cardId":"weekly_scoped.fable.v1",
              "label":"Fable only",
              "usedPercent":12.5,
              "remainingPercent":87.5,
              "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"windowIdentity"},
              "modelScope":"fable"
            }
            """;
        var window = JsonSerializer.Deserialize<UsageWindow>(json, Web)!;
        Assert.Equal("fable", window.ModelScope);

        var reencoded = JsonSerializer.Serialize(window, Web);
        Assert.Contains("\"modelScope\":\"fable\"", reencoded);
    }

    [Fact]
    public void AnUnscopedWindowHasNoScopeAndWritesNoKey()
    {
        const string json = """
            {
              "cardId":"claude.weekly.v1",
              "label":"Weekly",
              "usedPercent":40.0,
              "remainingPercent":60.0,
              "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"windowIdentity"}
            }
            """;
        var window = JsonSerializer.Deserialize<UsageWindow>(json, Web)!;
        Assert.Null(window.ModelScope);
        Assert.DoesNotContain("modelScope", JsonSerializer.Serialize(window, Web));
    }

    // ---- the one lookup ----------------------------------------------------

    [Fact]
    public void OfFindsTheScopeByClientAndWindowKey()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"), Window("weekly.v1", "Weekly", null));
        Assert.Equal("fable", ModelScope.Of(quota, "claude", null, Scoped));
        Assert.Null(ModelScope.Of(quota, "claude", null, "weekly.v1"));
        Assert.Null(ModelScope.Of(quota, "codex", null, Scoped));
        Assert.Null(ModelScope.Of(quota, "claude", null, "missing.v1"));
        Assert.Null(ModelScope.Of(null, "claude", null, Scoped));
    }

    /// <summary>The same flat-successor key can be unscoped on one account
    /// (still sent <c>seven_day_sonnet</c>) and scoped on another (sent only
    /// the <c>limits[]</c> entry): each account's own snapshot answers.</summary>
    [Fact]
    public void OfAnswersFromTheNamedAccountsOwnSnapshot()
    {
        const string sonnet = "sonnet.weekly.v1";
        var quota = new AgentUsagePayload("2026-01-01T00:00:00Z",
        [
            new AgentUsageSnapshot("claude", "source", "2026-01-01T00:00:00Z",
                [Window(sonnet, "Sonnet", null)],
                HistoryScope: new AccountScopeStatus("account-a")),
            new AgentUsageSnapshot("claude", "source", "2026-01-01T00:00:00Z",
                [Window(sonnet, "Sonnet", "sonnet")],
                HistoryScope: new AccountScopeStatus("account-b")),
        ]);
        Assert.Null(ModelScope.Of(quota, "claude", "account-a", sonnet));
        Assert.Equal("sonnet", ModelScope.Of(quota, "claude", "account-b", sonnet));
        // No live account to join by: macOS's rule, the first snapshot.
        Assert.Null(ModelScope.Of(quota, "claude", "unknown", sonnet));
        Assert.Null(ModelScope.Of(quota, "claude", null, sonnet));
    }

    // ---- the window card ---------------------------------------------------

    [Fact]
    public void TheCardsUsageCountsOnlyTheScopedModel()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"));
        var messages = new[]
        {
            Message(5_200_000, "claude-fable-5", 1_000, 1.0),
            Message(5_300_000, "claude-sonnet-4", 5_000, 3.0),
        };

        var client = Build(quota, Scoped, messages).Client!;

        var only = Assert.Single(client.Mine);
        Assert.Equal("claude-fable-5", only.ModelId);
        Assert.False(client.ScopeMatchedNothing);
    }

    [Fact]
    public void AnUnscopedWindowStillCountsEveryModel()
    {
        var quota = Quota(Window("weekly.v1", "Weekly", null));
        var messages = new[]
        {
            Message(5_200_000, "claude-fable-5", 1_000, 1.0),
            Message(5_300_000, "claude-sonnet-4", 5_000, 3.0),
        };

        var client = Build(quota, "weekly.v1", messages).Client!;

        Assert.Equal(2, client.Mine.Count);
        Assert.False(client.ScopeMatchedNothing);
    }

    [Fact]
    public void AScopeThatMatchesNothingIsSaidNotDrawnAsIdle()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"));
        var messages = new[] { Message(5_300_000, "claude-sonnet-4", 5_000, 3.0) };

        var client = Build(quota, Scoped, messages).Client!;

        Assert.Empty(client.Mine);
        Assert.True(client.ScopeMatchedNothing);
        Assert.Equal(
            "No local usage matched Fable only, though this subscription has other usage in this window",
            WindowCardText.ScopeNote(client.ScopeMatchedNothing, client.Selected));
    }

    [Fact]
    public void AFailedUsageReadRaisesNoScopeNote()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"));
        var messages = new[] { Message(5_300_000, "claude-sonnet-4", 5_000, 3.0) };

        var client = QuotaLensProjection.Build(
            [Series(Scoped)], quota, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Failed,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(), year: null,
            new QuotaLensProjection.Selection("claude", string.Empty),
            now: DateTimeOffset.FromUnixTimeSeconds(5_950)).Client!;

        // Control: the cycle is placed and the same inputs with a Succeeded
        // read raise the note (AScopeThatMatchesNothing...), so only the
        // Failed outcome keeps it false here.
        Assert.NotNull(client.Selected?.Active);
        Assert.False(client.ScopeMatchedNothing);
    }

    [Fact]
    public void NoSubscriptionUsageInTheWindowIsNotAScopeMismatch()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"));

        var client = Build(quota, Scoped, []).Client!;

        Assert.False(client.ScopeMatchedNothing);
        Assert.Null(WindowCardText.ScopeNote(client.ScopeMatchedNothing, client.Selected));
    }

    // ---- the history rows --------------------------------------------------

    [Fact]
    public void TheHistoryRowsCountOnlyTheScopedModel()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"));
        // Inside the completed cycle that resets at 4,000 s.
        var messages = new[]
        {
            Message(3_000_000, "claude-fable-5", 1_000, 1.0),
            Message(3_100_000, "claude-sonnet-4", 5_000, 3.0),
        };

        var history = Build(quota, Scoped, messages).Client!.History;

        var row = history.ByResetAt[4_000_000];
        Assert.Equal(1_000, row.MineTokens);
    }

    // ---- the equivalence estimate ------------------------------------------

    [Fact]
    public void EquivalenceSpansCountOnlyTheScopedModel()
    {
        var cycle = new QuotaCycle(
            ResetAtMs: 3_000, StartMs: 0, UsedPercent: 40, PeakUsedPercent: 40,
            SampleCount: 2, ObservedFraction: 1.0, FirstSampleMs: 1_000, LastSampleMs: 2_000);
        var messages = new[]
        {
            Message(1_500, "claude-fable-5", 1_000, 1.0),
            Message(1_600, "claude-sonnet-4", 5_000, 3.0),
        };

        var scoped = Assert.Single(QuotaEquivalenceFold.Cycles(
            [cycle], "claude", messages, Confirmed().Records, modelScope: "fable"));
        var unscoped = Assert.Single(QuotaEquivalenceFold.Cycles(
            [cycle], "claude", messages, Confirmed().Records, modelScope: null));

        Assert.Equal(1_000, scoped.SpanTokens);
        Assert.Equal(6_000, unscoped.SpanTokens);
    }

    [Fact]
    public void TheOverviewEstimateIsNarrowedToEachWindowsOwnScope()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"));
        // Four completed cycles that each rise 10% -> 70% across a real
        // sample span, enough admitted cycles for a ratio, with one message
        // per model inside each span.
        var samples = new List<QuotaHistorySample>();
        var messages = new List<WindowMessage>();
        for (var i = 1; i <= 4; i++)
        {
            var resetAt = i * 2 * FiveHours;
            samples.Add(Sample(10, resetAt - FiveHours, resetAt, active: false));
            samples.Add(Sample(70, resetAt - 60, resetAt, active: false));
            var inside = (resetAt - FiveHours + 60) * 1_000;
            messages.Add(Message(inside, "claude-fable-5", 1_000, 1.0));
            messages.Add(Message(inside + 1_000, "claude-sonnet-4", 5_000, 3.0));
        }

        var series = new QuotaHistorySeries("claude", "primary", Scoped, samples);
        var model = Build(quota, series, messages);

        var expected = QuotaEquivalenceFold.Build([series], messages, Confirmed(), _ => "fable");
        var unscoped = QuotaEquivalenceFold.Build([series], messages, Confirmed(), _ => null);
        var id = new QuotaWindowIdentity("claude", "primary", Scoped);
        Assert.IsType<WindowEquivalence.Row.Ratio>(expected[id]);
        Assert.Equal(expected[id], model.Overview.Equivalences[id]);
        Assert.NotEqual(unscoped[id], model.Overview.Equivalences[id]);
    }

    /// <summary>Each series gets its OWN scope: an unscoped window beside a
    /// scoped one keeps its all-models estimate.</summary>
    [Fact]
    public void AnUnscopedSeriesBesideAScopedOneKeepsItsUnscopedEstimate()
    {
        var quota = Quota(Window(Scoped, "Fable only", "fable"), Window("weekly.v1", "Weekly", null));
        var samples = new List<QuotaHistorySample>();
        var messages = new List<WindowMessage>();
        for (var i = 1; i <= 4; i++)
        {
            var resetAt = i * 2 * FiveHours;
            samples.Add(Sample(10, resetAt - FiveHours, resetAt, active: false));
            samples.Add(Sample(70, resetAt - 60, resetAt, active: false));
            var inside = (resetAt - FiveHours + 60) * 1_000;
            messages.Add(Message(inside, "claude-fable-5", 1_000, 1.0));
            messages.Add(Message(inside + 1_000, "claude-sonnet-4", 5_000, 3.0));
        }

        var scopedSeries = new QuotaHistorySeries("claude", "primary", Scoped, samples);
        var weeklySeries = new QuotaHistorySeries("claude", "primary", "weekly.v1", samples);
        var model = QuotaLensProjection.Build(
            [scopedSeries, weeklySeries], quota, EmptyGraph(),
            windowUsage: new WindowUsage(messages, 0, 0),
            windowUsageOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            quotaHistoryOutcome: WindowEquivalence.FetchOutcome.Succeeded,
            Confirmed(), year: null,
            new QuotaLensProjection.Selection(ClientRegistry.OverviewTab, string.Empty));

        var unscoped = QuotaEquivalenceFold.Build([weeklySeries], messages, Confirmed(), _ => null);
        var scoped = QuotaEquivalenceFold.Build([scopedSeries], messages, Confirmed(), _ => "fable");
        var weeklyId = new QuotaWindowIdentity("claude", "primary", "weekly.v1");
        var scopedId = new QuotaWindowIdentity("claude", "primary", Scoped);
        Assert.Equal(unscoped[weeklyId], model.Overview.Equivalences[weeklyId]);
        Assert.Equal(scoped[scopedId], model.Overview.Equivalences[scopedId]);
        Assert.NotEqual(model.Overview.Equivalences[weeklyId], model.Overview.Equivalences[scopedId]);
    }
}
