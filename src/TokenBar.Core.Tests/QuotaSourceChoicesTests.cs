using System.Text.Json;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

// The Settings "Quota source" options and the keys an open Settings compares
// to rebuild a page when they change.
public class QuotaSourceChoicesTests
{
    public QuotaSourceChoicesTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static AgentUsagePayload Decode(string json) =>
        JsonSerializer.Deserialize<AgentUsagePayload>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private const string Codex =
        """{"clientId":"codex","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[{"cardId":"session.v1","label":"Session","usedPercent":10,"remainingPercent":90,"resetsAt":"2030-01-01T00:00:00Z"}]}""";
    private const string Copilot =
        """{"clientId":"copilot","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[{"cardId":"premium.v1","label":"Premium","usedPercent":5,"remainingPercent":95,"resetsAt":"2030-01-01T00:00:00Z"}]}""";
    private const string FailedGrok =
        """{"clientId":"grok","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[],"error":"offline"}""";

    private static AgentUsagePayload Payload(params string[] agents) =>
        Decode($$"""{"generatedAt":"now","agents":[{{string.Join(',', agents)}}]}""");

    [Fact]
    public void BeforeAnyFetchOnlyAutoIsOffered() =>
        Assert.Equal([QuotaResolver.Auto], QuotaSourceChoices.Of(null).Select(c => c.Selection));

    [Fact]
    public void EveryWindowOfEveryReportingAgentIsOfferedAfterAuto()
    {
        var choices = QuotaSourceChoices.Of(Payload(Codex, FailedGrok));

        Assert.Equal(
            [QuotaResolver.Auto, QuotaResolver.Selection("codex", "session.v1", null)],
            choices.Select(c => c.Selection));
    }

    // The defect: Settings opened before the first fetch, or before a new
    // agent appeared, kept its first options. The key an open Settings
    // compares changes exactly when the offered set does.
    [Fact]
    public void TheMenuBarKeyChangesWhenTheFirstFetchOrANewAgentArrives()
    {
        var beforeFetch = QuotaSourceChoices.MenuBarKey(null);
        var codexOnly = QuotaSourceChoices.MenuBarKey(Payload(Codex));
        var withCopilot = QuotaSourceChoices.MenuBarKey(Payload(Codex, Copilot));

        Assert.NotEqual(beforeFetch, codexOnly);
        Assert.NotEqual(codexOnly, withCopilot);
        Assert.Equal(codexOnly, QuotaSourceChoices.MenuBarKey(Payload(Codex)));
    }

    [Fact]
    public void TheDashboardKeyChangesWhenANewAgentReports() =>
        Assert.NotEqual(
            QuotaSourceChoices.DashboardKey(Payload(Codex)),
            QuotaSourceChoices.DashboardKey(Payload(Codex, Copilot)));
}
