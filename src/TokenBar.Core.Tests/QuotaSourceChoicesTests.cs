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
    // agent appeared, kept its first options. An open page is rebuilt exactly
    // when the payload offers a choice it was not built with.
    [Fact]
    public void ANewChoiceArrivesWithTheFirstFetchAndWithANewAgent()
    {
        var beforeFetch = QuotaSourceChoices.Selections(null);
        var codexOnly = QuotaSourceChoices.Selections(Payload(Codex));

        Assert.True(QuotaSourceChoices.OffersNewChoice(beforeFetch, Payload(Codex)));
        Assert.True(QuotaSourceChoices.OffersNewChoice(codexOnly, Payload(Codex, Copilot)));
        Assert.False(QuotaSourceChoices.OffersNewChoice(codexOnly, Payload(Codex)));
        Assert.True(QuotaSourceChoices.OffersNewChoice(null, Payload(Codex)));
    }

    // An agent that errors on one poll and recovers on the next must not
    // rebuild the page under the user each time.
    [Fact]
    public void AnAgentFlappingIntoAnErrorOffersNothingNew()
    {
        const string failedCopilot =
            """{"clientId":"copilot","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[],"error":"rate limited"}""";
        var built = QuotaSourceChoices.Selections(Payload(Codex, Copilot));

        Assert.False(QuotaSourceChoices.OffersNewChoice(built, Payload(Codex, failedCopilot)));
        Assert.False(QuotaSourceChoices.OffersNewChoice(built, Payload(Codex, Copilot)));
    }

    // Two agents erroring in turn: rebuilding from each payload alone swapped
    // rows and rebuilt the page every poll. Rows accumulate, so after one
    // rebuild per choice nothing is new.
    [Fact]
    public void AgentsErroringInTurnRebuildAtMostOncePerChoice()
    {
        const string failedCodex =
            """{"clientId":"codex","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[],"error":"rate limited"}""";
        const string failedCopilot =
            """{"clientId":"copilot","source":"oauth","updatedAt":"2026-10-05T00:00:00Z","windows":[],"error":"rate limited"}""";
        var onlyCopilot = Payload(failedCodex, Copilot);
        var onlyCodex = Payload(Codex, failedCopilot);

        var listed = QuotaSourceChoices.Listed(onlyCopilot, []);
        Assert.True(QuotaSourceChoices.OffersNewChoice(Keys(listed), onlyCodex));
        listed = QuotaSourceChoices.Listed(onlyCodex, listed);

        Assert.False(QuotaSourceChoices.OffersNewChoice(Keys(listed), onlyCopilot));
        Assert.False(QuotaSourceChoices.OffersNewChoice(Keys(listed), onlyCodex));
        Assert.True(Keys(listed).SetEquals(QuotaSourceChoices.Selections(Payload(Codex, Copilot))));
        // Before: a page rebuilt from the payload alone lost the other row.
        Assert.True(QuotaSourceChoices.OffersNewChoice(
            QuotaSourceChoices.Selections(onlyCodex), onlyCopilot));
    }

    private static HashSet<string> Keys(IEnumerable<(string Selection, string Label)> rows) =>
        rows.Select(row => row.Selection).ToHashSet(StringComparer.Ordinal);

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
