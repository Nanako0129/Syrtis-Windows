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
    // agent appeared, kept its first options. An open page is rebuilt when
    // the payload's set of choices differs from the one it was built with.
    [Fact]
    public void ANewChoiceArrivesWithTheFirstFetchAndWithANewAgent()
    {
        var beforeFetch = QuotaSourceChoices.Selections(null);
        var codexOnly = QuotaSourceChoices.Selections(Payload(Codex));

        Assert.True(QuotaSourceChoices.ChoicesChanged(beforeFetch, Payload(Codex)));
        Assert.True(QuotaSourceChoices.ChoicesChanged(codexOnly, Payload(Codex, Copilot)));
        Assert.False(QuotaSourceChoices.ChoicesChanged(codexOnly, Payload(Codex)));
        Assert.True(QuotaSourceChoices.ChoicesChanged(null, Payload(Codex)));
    }

    // macOS recomputes the list from the current payload: an agent that stops
    // reporting (Grok Bot after its sign-in is turned off, or an error) leaves
    // the list, unless it is the selection, which stays as the unavailable
    // row. The list used to keep every row seen while Settings was open.
    [Fact]
    public void AVanishedAgentLeavesTheListUnlessSelected()
    {
        const string failedCopilot =
            """{"clientId":"copilot","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[],"error":"rate limited"}""";
        var built = QuotaSourceChoices.Selections(Payload(Codex, Copilot));
        var withoutCopilot = Payload(Codex, failedCopilot);
        var copilot = QuotaResolver.Selection("copilot", "premium.v1");

        Assert.True(QuotaSourceChoices.ChoicesChanged(built, withoutCopilot));
        Assert.DoesNotContain(copilot, QuotaSourceChoices.Selections(withoutCopilot));
        Assert.Null(QuotaSourceChoices.Unavailable(withoutCopilot, QuotaResolver.Auto));
        Assert.Equal(copilot, QuotaSourceChoices.Unavailable(withoutCopilot, copilot)?.Selection);
        // The same set again (labels or order aside) is no change.
        Assert.False(QuotaSourceChoices.ChoicesChanged(
            QuotaSourceChoices.Selections(withoutCopilot), Payload(Codex, failedCopilot)));
    }

    // The user picked a Copilot window, then Copilot errored (its last-good
    // lived only in memory, gone after a restart): the list used to drop it
    // with nothing checked. It now stays as a disabled row, which the radio
    // group checks because it is the current selection.
    [Fact]
    public void AnUnavailableSelectionStaysListedDisabled()
    {
        const string failedCopilot =
            """{"clientId":"copilot","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[],"error":"blocked"}""";
        var payload = Payload(Codex, failedCopilot);
        var selection = QuotaSelectionPolicy.EffectiveSelection(
            payload, QuotaResolver.Selection("copilot", "premium.v1"));

        Assert.DoesNotContain(selection, QuotaSourceChoices.Selections(payload));
        var row = QuotaSourceChoices.Unavailable(payload, selection);
        Assert.Equal(selection, row?.Selection);
        Assert.Equal($"{ClientRegistry.Style("copilot").DisplayName} · Unavailable selection", row?.Label);
    }

    [Fact]
    public void AnOfferedOrAutoSelectionHasNoUnavailableRow()
    {
        var payload = Payload(Codex, Copilot);

        Assert.Null(QuotaSourceChoices.Unavailable(payload, QuotaResolver.Auto));
        Assert.Null(QuotaSourceChoices.Unavailable(null, QuotaResolver.Auto));
        // Before any payload an explicit pick is not known to be unavailable.
        var copilot = QuotaResolver.Selection("copilot", "premium.v1");
        Assert.Equal(
            $"{ClientRegistry.Style("copilot").DisplayName} · —",
            QuotaSourceChoices.Unavailable(null, copilot)?.Label);
        Assert.Null(QuotaSourceChoices.Unavailable(
            payload, QuotaResolver.Selection("copilot", "premium.v1")));
    }

    [Fact]
    public void TheDashboardKeyChangesWhenANewAgentReports() =>
        Assert.NotEqual(
            QuotaSourceChoices.DashboardKey(Payload(Codex)),
            QuotaSourceChoices.DashboardKey(Payload(Codex, Copilot)));
}
