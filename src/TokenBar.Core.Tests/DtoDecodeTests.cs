using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

public class DtoDecodeTests
{
    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };

    private const string AvailableJson = """
        {
          "cardId":"codex.weekly.v1",
          "label":"Weekly",
          "usedPercent":40.0,
          "remainingPercent":60.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "resetText":"Resets in 2d",
          "windowMinutes":10080,
          "paceStatus":{
            "state":"available",
            "windowKey":"codex.weekly.v1",
            "durationSeconds":604800,
            "durationSource":"provider",
            "completeCycles":5
          },
          "historicalPace":{
            "expectedUsedPercent":55.5,
            "etaSeconds":1200,
            "willLastToReset":false,
            "runOutProbability":0.7
          }
        }
        """;

    private const string LearningHistoryJson = """
        {
          "cardId":"claude.weekly.v1",
          "label":"Weekly",
          "usedPercent":25.0,
          "remainingPercent":75.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "windowMinutes":1440,
          "paceStatus":{
            "state":"learningHistory",
            "windowKey":"claude.weekly.v1",
            "durationSeconds":86400,
            "durationSource":"contract",
            "completeCycles":1
          }
        }
        """;

    private const string LearningDurationObservedJson = """
        {
          "cardId":"antigravity.session.v1",
          "label":"Session",
          "usedPercent":10.0,
          "remainingPercent":90.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "paceStatus":{
            "state":"learningDuration",
            "windowKey":"antigravity.session.v1",
            "durationSource":"observed",
            "completeCycles":0
          }
        }
        """;

    private const string LearningDurationJson = """
        {
          "cardId":"grok.session.v1",
          "label":"Session",
          "usedPercent":10.0,
          "remainingPercent":90.0,
          "resetsAt":"2026-07-09T00:00:00Z",
          "paceStatus":{
            "state":"learningDuration",
            "windowKey":"grok.session.v1",
            "completeCycles":0
          }
        }
        """;

    private const string UnavailableJson = """
        {
          "cardId":"provider.unknown.v1",
          "label":"Unknown",
          "usedPercent":50.0,
          "remainingPercent":50.0,
          "paceStatus":{
            "state":"unavailable",
            "completeCycles":0,
            "reason":"windowIdentity"
          }
        }
        """;

    private static UsageWindow DecodeWindow(string json) =>
        JsonSerializer.Deserialize<UsageWindow>(json, Web)
        ?? throw new InvalidOperationException("window decoded to null");

    // The account-scope wire contract, asserted against the bytes Rust emits
    // rather than against a C#-built object.
    //
    // `AgentUsageSnapshot.AccountScope` is nullable and `AccountScopeStatus`'s
    // members are nullable, so a shape this decoder does not recognise arrives
    // as null and throws nothing. `WindowCardText.Tabs` then falls back to
    // picking whichever stored series comes first — the exact defect the field
    // was added to remove — with every other test still green. Nothing else on
    // either side compares these bytes: the Rust half pins the Err shape in
    // `provider_quota_pace_v3_fixture_locks_production_serializer`, and every
    // other C# test constructs `AccountScopeStatus` directly.
    [Fact]
    public void AnAccountScopeDecodesFromTheShapeRustSerializes()
    {
        var ok = DecodeAgentUsagePayload(
            agentsJson: """[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[],"accountScope":{"scope":"scope-a"}}]""");
        var okScope = Assert.Single(ok.Agents).AccountScope;
        Assert.NotNull(okScope);
        Assert.Equal("scope-a", okScope!.Scope);
        Assert.Null(okScope.Error);

        var failed = DecodeAgentUsagePayload(
            agentsJson: """[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[],"accountScope":{"error":"no trusted account evidence"}}]""");
        var failedScope = Assert.Single(failed.Agents).AccountScope;
        Assert.NotNull(failedScope);
        Assert.Equal("no trusted account evidence", failedScope!.Error);
        Assert.Null(failedScope.Scope);
    }

    // `historyScope` is the key `WindowCardText.Tabs` joins a live agent to its
    // stored series by, so it has the same failure mode as `accountScope`
    // above: an unrecognised shape decodes to null and the join silently falls
    // back to first-wins. Asserted against the bytes Rust emits
    // (`account_and_history_scopes_serialize_as_two_case_objects`), with the
    // two fields carrying different values so a decoder reading one into the
    // other fails here.
    [Fact]
    public void AHistoryScopeDecodesFromTheShapeRustSerializes()
    {
        var ok = DecodeAgentUsagePayload(
            agentsJson: """[{"clientId":"claude","source":"oauth","updatedAt":"now","windows":[],"accountScope":{"scope":"lineage-a"},"historyScope":{"scope":"history-a"}}]""");
        var agent = Assert.Single(ok.Agents);
        Assert.Equal("lineage-a", agent.AccountScope!.Scope);
        Assert.NotNull(agent.HistoryScope);
        Assert.Equal("history-a", agent.HistoryScope!.Scope);
        Assert.Null(agent.HistoryScope.Error);

        var failed = DecodeAgentUsagePayload(
            agentsJson: """[{"clientId":"claude","source":"oauth","updatedAt":"now","windows":[],"accountScope":{"scope":"lineage-a"},"historyScope":{"error":"installation key failed validation"}}]""");
        var failedScope = Assert.Single(failed.Agents).HistoryScope;
        Assert.NotNull(failedScope);
        Assert.Equal("installation key failed validation", failedScope!.Error);
        Assert.Null(failedScope.Scope);

        Assert.Null(Assert.Single(DecodeAgentUsagePayload().Agents).HistoryScope);
    }

    // An older cdylib emits no field at all. That is a third state and it must
    // stay distinguishable from both: a resolved scope, a resolution that
    // failed, and a producer that predates the field entirely.
    [Fact]
    public void AnAbsentAccountScopeStaysNullRatherThanBecomingAnEmptyStatus()
    {
        var payload = DecodeAgentUsagePayload();

        Assert.Null(Assert.Single(payload.Agents).AccountScope);
    }

    private static AgentUsagePayload DecodeAgentUsagePayload(
        string? subscriptionsJson = null,
        string agentsJson = """[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[]}]""")
    {
        var subscriptions = subscriptionsJson is null
            ? ""
            : $$""","opencodeSubscriptions":{{subscriptionsJson}}""";
        return JsonSerializer.Deserialize<AgentUsagePayload>(
            $$"""{"generatedAt":"now","agents":{{agentsJson}}{{subscriptions}}}""",
            Web) ?? throw new InvalidOperationException("agent usage payload decoded to null");
    }

    private static AgentUsageSnapshot DecodeTransportDiagnostic(string? diagnosticJson = null)
    {
        var diagnostic = diagnosticJson is null
            ? ""
            : $$""","transportDiagnostic":{{diagnosticJson}}""";
        var payload = TbCore.DecodeEnvelope<AgentUsagePayload>(
            $$$"""{"ok":true,"data":{"generatedAt":"now","agents":[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[{"label":"Weekly","usedPercent":40,"remainingPercent":60}]{{{diagnostic}}}}]}}""");
        Assert.Equal("now", payload.GeneratedAt);
        var snapshot = Assert.Single(payload.Agents);
        Assert.Single(snapshot.Windows);
        return snapshot;
    }

    private static void AssertRejects(string json) =>
        Assert.Throws<JsonException>(() => DecodeWindow(json));

    private static string Replace(string json, string oldValue, string newValue) =>
        json.Replace(oldValue, newValue, StringComparison.Ordinal);

    [Fact]
    public void TraceBucketDecodesSnakeCase()
    {
        var json = """
            {"ok":true,"data":[{"client":"claude","agent":"main","model":"claude-sonnet-4",
             "tokens":1234,"messages":5,"tokens_per_min":678.5}]}
            """;
        var buckets = TbCore.DecodeEnvelope<IReadOnlyList<TraceBucket>>(json);
        var b = Assert.Single(buckets);
        Assert.Equal("claude", b.Client);
        Assert.Equal(1234, b.Tokens);
        Assert.Equal(678.5, b.TokensPerMin);
    }

    [Fact]
    public void TokensPerMinDecodesCamelCase()
    {
        var v = TbCore.DecodeEnvelope<TokensPerMin>("""{"ok":true,"data":{"tokensPerMin":42.5}}""");
        Assert.Equal(42.5, v.Value);
    }

    [Fact]
    public void SourceContextIdAcceptsOnlyExactVersionedLowercaseSha256Grammar()
    {
        var valid = "sc1:" + new string('a', 64);
        Assert.Equal(valid, TbCore.ValidateSourceContextId(valid));

        foreach (var invalid in new[]
        {
            "", "sc1:" + new string('a', 63), "sc1:" + new string('a', 65),
            "sc2:" + new string('a', 64), "SC1:" + new string('a', 64),
            "sc1:" + new string('A', 64), "sc1:" + new string('g', 64),
            "sc1:" + new string('a', 63) + "\n", " " + valid, valid + "suffix",
        })
        {
            Assert.Throws<TbCoreException>(() => TbCore.ValidateSourceContextId(invalid));
        }
    }

    [Fact]
    public void SourceContextIdEnvelopeRejectsMissingAndNonStringPayloads()
    {
        Assert.Throws<TbCoreException>(() => TbCore.DecodeEnvelope<string>("""{"ok":true}"""));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<string>("""{"ok":true,"data":1}"""));
    }

    [Fact]
    public void FilterParityProbeDecodesStatusesNullableAggregatesAndBoundedSummary()
    {
        var payload = TbCore.DecodeEnvelope<FilterParityProbe>(
            """
            {"ok":true,"data":{
              "hourly":{
                "status":"match",
                "unfiltered":{"entryCount":1,"input":2,"output":3,"cacheRead":4,
                  "cacheWrite":5,"reasoning":6,"totalTokens":20,"messageCount":7,
                  "totalCost":1.0},
                "full":{"entryCount":1,"input":2,"output":3,"cacheRead":4,
                  "cacheWrite":5,"reasoning":6,"totalTokens":20,"messageCount":7,
                  "totalCost":1.5},
                "delta":{"entryCount":0,"input":0,"output":0,"cacheRead":0,
                  "cacheWrite":0,"reasoning":0,"totalTokens":0,"messageCount":0,
                  "totalCost":0.5}
              },
              "agents":{"status":"sourceChanged","unfiltered":null,"full":null,"delta":null},
              "presentClientCount":2
            }}
            """);

        Assert.Equal(FilterParityStatus.Match, payload.Hourly.Status);
        Assert.Equal(FilterParityStatus.SourceChanged, payload.Agents.Status);
        Assert.Null(payload.Agents.Delta);
        Assert.Equal(2, payload.PresentClientCount);
        Assert.Equal(
            "hourly=MATCH entriesΔ=0 tokensΔ=0 messagesΔ=0 costΔ=0.50; " +
            "agents=SOURCE_CHANGED / SKIP",
            payload.SmokeSummary);
    }

    [Fact]
    public void FilterParityProbeRejectsUnknownOrNumericStatus()
    {
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<FilterParityProbe>(
            """
            {"ok":true,"data":{
              "hourly":{"status":"changed","unfiltered":null,"full":null,"delta":null},
              "agents":{"status":"match","unfiltered":null,"full":null,"delta":null},
              "presentClientCount":0
            }}
            """));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<FilterParityProbe>(
            """
            {"ok":true,"data":{
              "hourly":{"status":0,"unfiltered":null,"full":null,"delta":null},
              "agents":{"status":"match","unfiltered":null,"full":null,"delta":null},
              "presentClientCount":0
            }}
            """));
    }

    [Fact]
    public void ModelReportDecodesLowercaseKQuirkAndOptionals()
    {
        var json = """
            {"ok":true,"data":{"entries":[{"client":"claude","model":"m","provider":"anthropic",
             "input":1,"output":2,"cacheRead":3,"cacheWrite":4,"reasoning":0,"total":10,
             "messageCount":1,"cost":0.5,"msPer1kTokens":12.5}],
             "totalInput":1,"totalOutput":2,"totalCacheRead":3,"totalCacheWrite":4,
             "totalMessages":1,"totalCost":0.5}}
            """;
        var r = TbCore.DecodeEnvelope<ModelReport>(json);
        Assert.Equal(12.5, r.Entries[0].MsPer1kTokens);
        Assert.Null(r.PricingUpdatedAt);
    }

    [Fact]
    public void AgentUsageDecodesLegacyFieldsWithoutTrustingScalars()
    {
        var json = """
            {"ok":true,"data":{"generatedAt":"2026-07-02T00:00:00Z","agents":[
             {"clientId":"codex","source":"oauth","updatedAt":"2026-07-02T00:00:00Z",
              "identity":{"email":null,"plan":"plus"},
              "windows":[{"label":"Weekly","usedPercent":40.0,"remainingPercent":60.0,
                "resetsAt":null,"resetText":"Resets in 2d","windowMinutes":10080,
                "historicalExpectedPercent":55.5,"runOutProbability":0.1,
                "durationSeconds":604800}],
              "credits":null,"error":null}]}}
            """;
        var p = TbCore.DecodeEnvelope<AgentUsagePayload>(json);
        var w = Assert.Single(Assert.Single(p.Agents).Windows);
        Assert.Equal(10080, w.WindowMinutes);
        Assert.Equal(UsageWindow.LegacyMissingPresentationId, w.CardId);
        Assert.Equal(UsagePaceState.LegacyMissing, w.PaceStatus.State);
        Assert.Null(w.DurationSeconds);
        Assert.Null(w.HistoricalPace);
        Assert.Null(typeof(UsageWindow).GetProperty("HistoricalExpectedPercent"));
        Assert.Null(typeof(UsageWindow).GetProperty("RunOutProbability"));
        Assert.Null(p.OpencodeSubscriptions);
    }

    [Fact]
    public void AgentUsagePayloadIgnoresUnknownPublicationGeneration()
    {
        var payload = TbCore.DecodeEnvelope<AgentUsagePayload>(
            """{"ok":true,"data":{"generatedAt":"now","publicationGeneration":42,"agents":[]}}""");
        Assert.Equal("now", payload.GeneratedAt);
        Assert.Empty(payload.Agents);
    }

    [Fact]
    public void AgentUsageTransportDiagnosticDecodesHttpAndOsFacts()
    {
        var http = Assert.IsType<AgentUsageTransportDiagnostic>(
            DecodeTransportDiagnostic(
                """{"category":"rateLimited","status":429}""").TransportDiagnostic);
        Assert.Equal(new AgentUsageTransportDiagnostic("rateLimited", Status: 429), http);

        var os = Assert.IsType<AgentUsageTransportDiagnostic>(
            DecodeTransportDiagnostic(
                """{"category":"connectionRefused","osCode":10061}""").TransportDiagnostic);
        Assert.Equal(new AgentUsageTransportDiagnostic("connectionRefused", OsCode: 10061), os);
    }

    [Fact]
    public void AgentUsageTransportDiagnosticOmittedNullOrNonObjectDecodesAsNull()
    {
        foreach (var diagnosticJson in new string?[] { null, "null", "[]" })
        {
            Assert.Null(DecodeTransportDiagnostic(diagnosticJson).TransportDiagnostic);
        }
    }

    [Fact]
    public void AgentUsageTransportDiagnosticCategoryTypeMismatchPreservesOtherFields()
    {
        var diagnostic = Assert.IsType<AgentUsageTransportDiagnostic>(
            DecodeTransportDiagnostic(
                """{"category":42,"status":429,"osCode":10061}""").TransportDiagnostic);
        Assert.Equal(new AgentUsageTransportDiagnostic(Status: 429, OsCode: 10061), diagnostic);
    }

    [Theory]
    [InlineData("\"429\"")]
    [InlineData("9223372036854775808")]
    public void AgentUsageTransportDiagnosticMalformedStatusPreservesCategoryAndOsCode(
        string statusJson)
    {
        var diagnostic = Assert.IsType<AgentUsageTransportDiagnostic>(
            DecodeTransportDiagnostic(
                $$"""{"category":"connectionRefused","status":{{statusJson}},"osCode":10061}""")
                .TransportDiagnostic);
        Assert.Equal(
            new AgentUsageTransportDiagnostic("connectionRefused", OsCode: 10061),
            diagnostic);
    }

    [Fact]
    public void AgentUsageTransportDiagnosticPreservesUnknownCategory()
    {
        var diagnostic = Assert.IsType<AgentUsageTransportDiagnostic>(
            DecodeTransportDiagnostic(
                """{"category":"futureTransport","status":599}""").TransportDiagnostic);
        Assert.Equal(new AgentUsageTransportDiagnostic("futureTransport", Status: 599), diagnostic);
    }

    [Fact]
    public void AgentUsageTransportDiagnosticDoesNotRetainUnknownSensitiveMember()
    {
        var snapshot = DecodeTransportDiagnostic(
            """{"category":"timeout","authorization":"Bearer token-secret"}""");
        Assert.Equal(
            new AgentUsageTransportDiagnostic("timeout"),
            Assert.IsType<AgentUsageTransportDiagnostic>(snapshot.TransportDiagnostic));

        var serialized = JsonSerializer.Serialize(snapshot, Web);
        Assert.False(serialized.Contains("authorization", StringComparison.OrdinalIgnoreCase));
        Assert.False(serialized.Contains("token-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentUsagePayloadDecodesStringSubscriptions()
    {
        var payload = DecodeAgentUsagePayload("""["Codex","Claude"]""");
        Assert.Equal(new[] { "Codex", "Claude" }, payload.OpencodeSubscriptions);
    }

    [Fact]
    public void AgentUsagePayloadAllowsOmittedOrNullSubscriptions()
    {
        Assert.Null(DecodeAgentUsagePayload().OpencodeSubscriptions);
        Assert.Null(DecodeAgentUsagePayload("null").OpencodeSubscriptions);
    }

    [Fact]
    public void AgentUsagePayloadRejectsNullSubscriptionElement() =>
        Assert.Throws<JsonException>(() => DecodeAgentUsagePayload("[null]"));

    [Fact]
    public void AgentUsagePayloadRejectsNullAgentElement() =>
        Assert.Throws<JsonException>(() => DecodeAgentUsagePayload(agentsJson: "[null]"));

    [Fact]
    public void AgentUsageSnapshotRejectsNullWindowElement() =>
        Assert.Throws<JsonException>(() => DecodeAgentUsagePayload(
            agentsJson: """[{"clientId":"codex","source":"oauth","updatedAt":"now","windows":[null]}]"""));

    [Fact]
    public void UsagePayloadDecodesNestedShape()
    {
        var json = """
            {"ok":true,"data":{
             "meta":{"generatedAt":"g","version":"v","dateRange":{"start":"2026-01-01","end":"2026-07-02"},
               "pricingMode":"localOnly","costCoverage":"partial"},
             "summary":{"totalTokens":100,"totalCost":1.5,"totalDays":10,"activeDays":8,
               "averagePerDay":10.0,"maxCostInSingleDay":0.9,"clients":["claude"],"models":["m"]},
             "years":[{"year":"2026","totalTokens":100,"totalCost":1.5,
               "range":{"start":"2026-01-01","end":"2026-07-02"}}],
             "contributions":[{"date":"2026-07-01","totals":{"tokens":50,"cost":0.7,"messages":3},
               "intensity":2,"tokenBreakdown":{"input":10,"output":20,"cacheRead":15,"cacheWrite":5,"reasoning":0},
               "turnsByClient":{"cc-mirror/foo":2},
               "clients":[{"client":"claude","modelId":"m","providerId":"anthropic",
                 "tokens":{"input":10,"output":20,"cacheRead":15,"cacheWrite":5,"reasoning":0},
                 "cost":0.7,"messages":3}]}]}}
            """;
        var p = TbCore.DecodeEnvelope<UsagePayload>(json);
        Assert.Equal(8, p.Summary.ActiveDays);
        Assert.Equal(PricingMode.LocalOnly, p.Meta.PricingMode);
        Assert.Equal(CostCoverage.Partial, p.Meta.CostCoverage);
        Assert.Equal("anthropic", p.Contributions[0].Clients[0].ProviderId);
        Assert.Equal(15, p.Contributions[0].TokenBreakdown.CacheRead);
        Assert.Equal(2, p.Contributions[0].TurnsByClient!["cc-mirror/foo"]);
    }

    private static string GraphEnvelope(string metadata) =>
        $$"""
        {
          "ok":true,
          "data":{
            "meta":{
              "generatedAt":"g",
              "version":"v",
              "dateRange":{"start":"2026-01-01","end":"2026-07-02"}{{metadata}}
            },
            "summary":{
              "totalTokens":0,
              "totalCost":0,
              "totalDays":0,
              "activeDays":0,
              "averagePerDay":0,
              "maxCostInSingleDay":0,
              "clients":[],
              "models":[]
            },
            "years":[],
            "contributions":[]
          }
        }
        """;

    [Fact]
    public void UsageMetaRequiresKnownStringModeAndCoverage()
    {
        var payload = TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":\"localOnly\",\"costCoverage\":\"complete\""));
        Assert.Equal(PricingMode.LocalOnly, payload.Meta.PricingMode);
        Assert.Equal(CostCoverage.Complete, payload.Meta.CostCoverage);

        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"costCoverage\":\"complete\"")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":\"localOnly\"")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":null,\"costCoverage\":\"complete\"")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":\"future\",\"costCoverage\":\"complete\"")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":0,\"costCoverage\":\"complete\"")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":\"localOnly\",\"costCoverage\":null")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":\"localOnly\",\"costCoverage\":\"future\"")));
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<UsagePayload>(
            GraphEnvelope(",\"pricingMode\":\"localOnly\",\"costCoverage\":0")));
    }

    [Fact]
    public void ContributionTurnsByClientAllowsAbsentOrEmptyMaps()
    {
        Contribution DecodeContribution(string suffix) =>
            JsonSerializer.Deserialize<Contribution>(
                $$"""{"date":"2026-08-09","totals":{"tokens":0,"cost":0,"messages":1},"intensity":0,"tokenBreakdown":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":0},"clients":[]{{suffix}}}""",
                Web) ?? throw new InvalidOperationException("contribution decoded to null");

        Assert.Null(DecodeContribution("").TurnsByClient);
        Assert.Empty(DecodeContribution(",\"turnsByClient\":{}").TurnsByClient!);
    }

    [Fact]
    public void AvailablePaceDecodesTypedNestedValues()
    {
        var window = DecodeWindow(AvailableJson);
        Assert.Equal("codex.weekly.v1", window.CardId);
        Assert.Equal(UsagePaceState.Available, window.PaceStatus.State);
        Assert.Equal("codex.weekly.v1", window.PaceStatus.WindowKey);
        Assert.Equal(604800, window.PaceStatus.DurationSeconds);
        Assert.Equal(UsagePaceDurationSource.Provider, window.PaceStatus.DurationSource);
        Assert.Equal(5, window.PaceStatus.CompleteCycles);
        Assert.Null(window.PaceStatus.Reason);
        Assert.Equal(604800, window.DurationSeconds);
        Assert.Equal(55.5, window.HistoricalPace!.ExpectedUsedPercent);
        Assert.Equal(1200, window.HistoricalPace.EtaSeconds);
        Assert.False(window.HistoricalPace.WillLastToReset);
        Assert.Equal(0.7, window.HistoricalPace.RunOutProbability);
    }

    [Fact]
    public void LearningHistoryDecodesWithoutHistoricalPace()
    {
        var window = DecodeWindow(LearningHistoryJson);
        Assert.Equal(UsagePaceState.LearningHistory, window.PaceStatus.State);
        Assert.Equal(86400, window.DurationSeconds);
        Assert.Equal(1440, window.WindowMinutes);
        Assert.Null(window.HistoricalPace);
    }

    [Fact]
    public void LearningDurationSupportsObservedOrMissingSource()
    {
        var observed = DecodeWindow(LearningDurationObservedJson);
        Assert.Equal(UsagePaceState.LearningDuration, observed.PaceStatus.State);
        Assert.Equal(UsagePaceDurationSource.Observed, observed.PaceStatus.DurationSource);
        Assert.Null(observed.PaceStatus.DurationSeconds);
        Assert.Null(observed.DurationSeconds);

        var missing = DecodeWindow(LearningDurationJson);
        Assert.Equal(UsagePaceState.LearningDuration, missing.PaceStatus.State);
        Assert.Null(missing.PaceStatus.DurationSource);
        Assert.Null(missing.DurationSeconds);
    }

    [Fact]
    public void UnavailableIdentityPaceDecodesWithNullWindowKey()
    {
        var window = DecodeWindow(UnavailableJson);
        Assert.Equal(UsagePaceState.Unavailable, window.PaceStatus.State);
        Assert.Null(window.PaceStatus.WindowKey);
        Assert.Equal(UsagePaceUnavailableReason.WindowIdentity, window.PaceStatus.Reason);
        Assert.Null(window.DurationSeconds);
        Assert.Null(window.HistoricalPace);
    }

    [Fact]
    public void MissingPaceStatusKeyIsLegacyButPresentNullFails()
    {
        var legacy = DecodeWindow("""
            {"label":"Weekly","usedPercent":40,"remainingPercent":60}
            """);
        Assert.Equal(UsageWindow.LegacyMissingPresentationId, legacy.CardId);
        Assert.Equal(UsagePaceState.LegacyMissing, legacy.PaceStatus.State);

        AssertRejects("""
            {"cardId":"codex.weekly.v1","label":"Weekly","usedPercent":40,
             "remainingPercent":60,"paceStatus":null}
            """);
    }

    [Fact]
    public void LegacyPresentNullCardIdFails()
    {
        AssertRejects("""
            {"cardId":null,"label":"Weekly","usedPercent":40,"remainingPercent":60}
            """);
    }

    [Theory]
    [InlineData("cardId", "")]
    [InlineData("cardId", "   ")]
    [InlineData("cardId", "null")]
    [InlineData("cardId", "123")]
    public void V3CardIdMustBePresentNonEmptyAndString(string key, string value)
    {
        var json = value == "null" || value == "123"
            ? Replace(AvailableJson, "\"cardId\":\"codex.weekly.v1\"", $"\"{key}\":{value}")
            : Replace(AvailableJson, "\"cardId\":\"codex.weekly.v1\"", $"\"{key}\":\"{value}\"");
        AssertRejects(json);
    }

    [Fact]
    public void V3MissingCardIdFails()
    {
        AssertRejects(Replace(AvailableJson, "\"cardId\":\"codex.weekly.v1\",", ""));
    }

    [Theory]
    [InlineData("\"state\":\"available\"", "\"state\":\"future\"")]
    [InlineData("\"durationSource\":\"provider\"", "\"durationSource\":\"future\"")]
    public void UnknownPaceEnumValuesFail(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void UnknownUnavailableReasonFails()
    {
        var json = """
            {"cardId":"x","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"future"}}
            """;
        AssertRejects(json);
    }

    [Fact]
    public void NegativeCompleteCyclesFail() =>
        AssertRejects(Replace(AvailableJson, "\"completeCycles\":5", "\"completeCycles\":-1"));

    [Theory]
    [InlineData("\"windowKey\":\"codex.weekly.v1\",", "")]
    [InlineData("\"windowKey\":\"codex.weekly.v1\",", "\"windowKey\":null,")]
    public void NonIdentityStateRequiresNonEmptyWindowKey(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void IdentityUnavailableCannotCarryWindowKey()
    {
        var json = Replace(UnavailableJson, "\"completeCycles\":0,", "\"windowKey\":\"x\",\"completeCycles\":0,");
        AssertRejects(json);
    }

    [Fact]
    public void NonIdentityUnavailableRequiresWindowKey()
    {
        var json = """
            {"cardId":"x","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"missingReset"}}
            """;
        AssertRejects(json);
    }

    [Theory]
    [InlineData("\"durationSeconds\":604800,", "\"durationSeconds\":0,")]
    [InlineData("\"durationSeconds\":604800,", "\"durationSeconds\":34560001,")]
    [InlineData("\"durationSeconds\":604800,", "\"durationSeconds\":604800,")]
    public void DurationBoundsAndRequiredSourceAreStrict(string oldValue, string newValue)
    {
        var json = oldValue == newValue
            ? Replace(AvailableJson, "\"durationSource\":\"provider\",", "")
            : Replace(AvailableJson, oldValue, newValue);
        AssertRejects(json);
    }

    [Fact]
    public void DurationSourceWithoutDurationIsOnlyObservedLearningDuration()
    {
        var history = Replace(
            LearningHistoryJson,
            "\"durationSeconds\":86400,",
            "");
        AssertRejects(history);

        var duration = Replace(
            LearningDurationObservedJson,
            "\"state\":\"learningDuration\"",
            "\"state\":\"learningHistory\"");
        AssertRejects(duration);
    }

    [Fact]
    public void LearningDurationCannotCarryDurationOrReason()
    {
        var withDuration = Replace(
            LearningDurationJson,
            "\"completeCycles\":0",
            "\"durationSeconds\":60,\"durationSource\":\"observed\",\"completeCycles\":0");
        AssertRejects(withDuration);

        var withReason = Replace(
            LearningDurationJson,
            "\"completeCycles\":0",
            "\"completeCycles\":0,\"reason\":\"history\"");
        AssertRejects(withReason);
    }

    [Fact]
    public void UnavailableRequiresReasonAndCannotCarryHistoricalPace()
    {
        var missingReason = Replace(
            UnavailableJson,
            "\"reason\":\"windowIdentity\"",
            "\"unknown\":\"windowIdentity\"");
        AssertRejects(missingReason);

        var withHistory = """
            {"cardId":"provider.unknown.v1","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "paceStatus":{"state":"unavailable","completeCycles":0,"reason":"windowIdentity"},
             "historicalPace":{"expectedUsedPercent":10,"willLastToReset":true}}
            """;
        AssertRejects(withHistory);
    }

    [Fact]
    public void UnavailableCannotCarryDuration()
    {
        var json = Replace(
            UnavailableJson,
            "\"remainingPercent\":50.0,",
            "\"remainingPercent\":50.0,\"windowMinutes\":1,");
        json = Replace(
            json,
            "\"completeCycles\":0,",
            "\"durationSeconds\":60,\"durationSource\":\"provider\",\"completeCycles\":0,");
        AssertRejects(json);
    }

    [Fact]
    public void V3ResetPresenceAndFormatMustMatchPaceState()
    {
        const string reset = "\"resetsAt\":\"2026-07-09T00:00:00Z\",";
        AssertRejects(Replace(AvailableJson, reset, ""));
        AssertRejects(Replace(LearningHistoryJson, reset, ""));
        AssertRejects(Replace(LearningDurationJson, reset, ""));
        AssertRejects(Replace(AvailableJson, reset, "\"resetsAt\":\"tomorrow\","));

        var missingResetWithReset = """
            {"cardId":"x","label":"Unknown","usedPercent":50,"remainingPercent":50,
             "resetsAt":"2026-07-09T00:00:00Z",
             "paceStatus":{"state":"unavailable","windowKey":"x",
               "completeCycles":0,"reason":"missingReset"}}
            """;
        AssertRejects(missingResetWithReset);
    }

    [Fact]
    public void WindowMinutesMustMirrorDurationOrBeAbsentWithoutDuration()
    {
        AssertRejects(Replace(AvailableJson, "\"windowMinutes\":10080", "\"windowMinutes\":10079"));

        var learning = """
            {"cardId":"grok.session.v1","label":"Session","usedPercent":10,"remainingPercent":90,
             "windowMinutes":1,
             "paceStatus":{"state":"learningDuration","windowKey":"grok.session.v1","completeCycles":0}}
            """;
        AssertRejects(learning);
    }

    [Fact]
    public void HistoricalPacePresenceMustMatchState()
    {
        AssertRejects(Replace(AvailableJson, "\"historicalPace\":", "\"ignoredPace\":"));

        var learningHistoryWithPace = """
            {"cardId":"claude.weekly.v1","label":"Weekly","usedPercent":25,"remainingPercent":75,
             "windowMinutes":1440,
             "paceStatus":{"state":"learningHistory","windowKey":"claude.weekly.v1",
               "durationSeconds":86400,"durationSource":"contract","completeCycles":1},
             "historicalPace":{"expectedUsedPercent":10,"willLastToReset":true}}
            """;
        AssertRejects(learningHistoryWithPace);
    }

    [Theory]
    [InlineData("\"expectedUsedPercent\":55.5", "\"expectedUsedPercent\":-0.1")]
    [InlineData("\"expectedUsedPercent\":55.5", "\"expectedUsedPercent\":100.1")]
    [InlineData("\"etaSeconds\":1200", "\"etaSeconds\":-1")]
    [InlineData("\"etaSeconds\":1200", "\"etaSeconds\":\"soon\"")]
    [InlineData("\"runOutProbability\":0.7", "\"runOutProbability\":-0.1")]
    [InlineData("\"runOutProbability\":0.7", "\"runOutProbability\":1.1")]
    public void HistoricalPaceRangesAndTypesAreStrict(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Theory]
    [InlineData("\"etaSeconds\":1200,", "\"etaSeconds\":null,")]
    [InlineData("\"willLastToReset\":false", "\"willLastToReset\":true")]
    public void HistoricalEtaAndWillLastMustAgree(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void HistoricalRequiredFieldsAndWrongTypesFail()
    {
        AssertRejects(Replace(AvailableJson, "\"expectedUsedPercent\":55.5,", ""));
        AssertRejects(Replace(AvailableJson, "\"willLastToReset\":false,", ""));
        AssertRejects(Replace(AvailableJson, "\"expectedUsedPercent\":55.5", "\"expectedUsedPercent\":\"55.5\""));
    }

    [Theory]
    [InlineData("\"usedPercent\":40.0", "\"usedPercent\":-1")]
    [InlineData("\"usedPercent\":40.0", "\"usedPercent\":101")]
    [InlineData("\"remainingPercent\":60.0", "\"remainingPercent\":\"60\"")]
    [InlineData("\"remainingPercent\":60.0", "\"remainingPercent\":59")]
    public void PercentagesMustBeFiniteBoundedComplementaryAndNumeric(string oldValue, string newValue) =>
        AssertRejects(Replace(AvailableJson, oldValue, newValue));

    [Fact]
    public void RequiredLabelAndPercentKeysCannotBeMissing()
    {
        AssertRejects(Replace(AvailableJson, "\"label\":\"Weekly\",", ""));
        AssertRejects(Replace(AvailableJson, "\"usedPercent\":40.0,", ""));
        AssertRejects(Replace(AvailableJson, "\"remainingPercent\":60.0,", ""));
    }

    [Fact]
    public void StandardJsonCannotRepresentNaNPercentages()
    {
        Assert.Throws<JsonException>(() => DecodeWindow(
            Replace(AvailableJson, "\"usedPercent\":40.0", "\"usedPercent\":NaN")));
    }

    [Fact]
    public void OptionalResetFieldsOnlyAcceptStringOrNull()
    {
        AssertRejects(Replace(AvailableJson, "\"resetsAt\":\"2026-07-09T00:00:00Z\"", "\"resetsAt\":123"));
        AssertRejects(Replace(AvailableJson, "\"resetText\":\"Resets in 2d\"", "\"resetText\":false"));
        AssertRejects(Replace(AvailableJson, "\"windowMinutes\":10080", "\"windowMinutes\":10080.5"));
    }

    [Fact]
    public void TopLevelLegacyPaceScalarsCannotOverrideNestedV3Values()
    {
        var json = Replace(
            AvailableJson,
            "\"paceStatus\":",
            "\"historicalExpectedPercent\":99,\"runOutProbability\":0.99,\"durationSeconds\":1,\"paceStatus\":");
        var window = DecodeWindow(json);
        Assert.Equal(55.5, window.HistoricalPace!.ExpectedUsedPercent);
        Assert.Equal(604800, window.DurationSeconds);
        Assert.Equal(UsagePaceState.Available, window.PaceStatus.State);
    }

    [Fact]
    public void DirectDefaultWebOptionsUseUsageWindowAttributeConverter()
    {
        var window = JsonSerializer.Deserialize<UsageWindow>(
            AvailableJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(window);
        Assert.Equal(UsagePaceState.Available, window!.PaceStatus.State);
        Assert.Equal(604800, window.DurationSeconds);
    }

    [Fact]
    public void UsageWindowSerializationDoesNotHitUnsupportedConverterWrite()
    {
        var json = JsonSerializer.Serialize(DecodeWindow(AvailableJson), Web);
        Assert.Contains("\"paceStatus\"", json);
        Assert.Contains("\"durationSeconds\":604800", json);
    }

    [Fact]
    public void UniqueCardWindowsPreservesOrderAndFirstDuplicate()
    {
        var first = DecodeWindow(AvailableJson) with { Label = "first" };
        var duplicate = DecodeWindow(AvailableJson) with { Label = "duplicate" };
        var other = DecodeWindow(LearningHistoryJson);
        var snapshot = new AgentUsageSnapshot(
            ClientId: "codex",
            Source: "oauth",
            UpdatedAt: "now",
            Windows: [first, duplicate, other]);

        var unique = snapshot.UniqueCardWindows;
        Assert.Equal(2, unique.Count);
        Assert.Same(first, unique[0]);
        Assert.Same(other, unique[1]);
    }


    // The FFI now ships a local price estimate per row. Present, it decodes;
    // absent — any payload from before this field existed — or explicitly
    // null (the Rust side's "cannot price these tokens"), it is null. Neither
    // of the last two may throw or become 0, which would be a usable-looking
    // denominator for a price nobody computed.
    [Theory]
    [InlineData(",\"costEstimate\":1.25", 1.25)]
    [InlineData("", null)]
    [InlineData(",\"costEstimate\":null", null)]
    public void ModelReportCarriesTheLocalCostEstimate(string field, double? expected)
    {
        var json = """
            {"ok":true,"data":{"entries":[{"client":"opencode","model":"m","provider":"deepseek",
             "input":1,"output":2,"cacheRead":0,"cacheWrite":0,"reasoning":0,"total":3,
             "messageCount":1,"cost":0.5
            """ + field + """
            }],"totalInput":1,"totalOutput":2,"totalCacheRead":0,"totalCacheWrite":0,
             "totalMessages":1,"totalCost":0.5}}
            """;
        var r = TbCore.DecodeEnvelope<ModelReport>(json);
        Assert.Equal(expected, r.Entries[0].CostEstimate);
    }
}
