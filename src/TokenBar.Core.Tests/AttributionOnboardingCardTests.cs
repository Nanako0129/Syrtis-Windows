using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The attribution onboarding card's decisions (macOS
/// AttributionOnboardingCard.swift; UsageAttributionSettings.swift:502-565).
/// The card is drawn in DashboardView.Quota.cs, which no test project compiles.</summary>
public class AttributionOnboardingCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    private SettingsStore StoreWithJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, json);
        return new SettingsStore(StorePath);
    }

    private static ModelReportEntry Entry(string client, string provider, string model = "m") =>
        new(client, model, provider, 0, 0, 0, 0, 0, 100, 1, 1.0);

    private static ModelReport Report(params ModelReportEntry[] entries) =>
        new(entries, 0, 0, 0, 0, 0, 0);

    private static AgentUsagePayload Payload(params string[] labels) => new("now", [], labels);

    private static UsageAttribution.Record Proposal(string client = "claude", string provider = "openai") =>
        new(client, provider, UsageAttribution.State.Assigned("codex"));

    private static readonly string[] UserArgs = ["Syrtis.exe"];

    // ---- names -----------------------------------------------------------

    [Fact]
    public void DismissedKeyMatchesMacOS() =>
        Assert.Equal("tokenbar.usage.attribution.onboardingDismissed", AttributionOnboardingCard.DismissedKey);

    // ---- onboardingSummary -----------------------------------------------

    [Fact]
    public void SummaryCountsProposalsAndTheUnassignedSourcesWithoutOne()
    {
        // claude/openai has a proposal (Codex subscription); cursor/zzz-unknown
        // is unassigned with nothing proposing a target.
        var summary = AttributionOnboardingCard.Summary(
            Report(Entry("claude", "openai"), Entry("cursor", "zzz-unknown")), Payload("Codex"))!;

        Assert.Equal([Proposal()], summary.Records);
        Assert.Equal(1, summary.UnsuggestedCount);
    }

    [Fact]
    public void SummaryIsNullUntilBothInputsHaveLoaded()
    {
        Assert.Null(AttributionOnboardingCard.Summary(null, Payload("Codex")));
        Assert.Null(AttributionOnboardingCard.Summary(Report(Entry("claude", "openai")), null));
    }

    [Fact]
    public void AnAlreadyConfirmedSourceIsNeitherProposedNorCountedUnsuggested()
    {
        var summary = UsageAttributionSettings.OnboardingSummaryFor(
            [Entry("claude", "openai")],
            [new UsageAttribution.Record("claude", "openai", UsageAttribution.State.Excluded)],
            ["codex"]);

        Assert.Empty(summary.Records);
        Assert.Equal(0, summary.UnsuggestedCount);
    }

    // ---- mayShow: each gate on its own ------------------------------------

    private static bool May(
        bool writable = true, bool hasRecords = false, bool dismissed = false,
        string[]? args = null) =>
        AttributionOnboardingCard.MayShow(
            new UsageAttribution.Table(hasRecords ? [Proposal()] : [], writable),
            dismissed,
            args ?? UserArgs);

    [Fact]
    public void MayShowWhenNothingIsConfirmedAndNothingDismissed() => Assert.True(May());

    [Fact]
    public void AnUnwritableConfirmedTableNeverInvites() => Assert.False(May(writable: false));

    [Fact]
    public void AnyConfirmedRecordRetiresTheCard() => Assert.False(May(hasRecords: true));

    [Fact]
    public void NotNowRetiresTheCard() => Assert.False(May(dismissed: true));

    [Theory]
    [InlineData("--startup-smoke")]
    [InlineData("--dump-tray-icons")]
    [InlineData("--update-dialog-demo")]
    [InlineData("--graph3d")]
    [InlineData("--soak3d")]
    public void NonUserRuntimesNeverShowIt(string flag) =>
        Assert.False(May(args: ["Syrtis.exe", flag]));

    [Theory]
    [InlineData("--settings")]
    [InlineData("--open-flyout")]
    public void OpeningRealUiOnRealDataStillShowsIt(string flag) =>
        Assert.True(May(args: ["Syrtis.exe", flag]));

    [Fact]
    public void MayShowReadsTheStoreAndOnlyAJsonTrueCountsAsDismissed()
    {
        Assert.True(AttributionOnboardingCard.MayShow(StoreWithJson("{}"), UserArgs));
        Assert.False(AttributionOnboardingCard.MayShow(
            StoreWithJson("""{"tokenbar.usage.attribution.onboardingDismissed": true}"""), UserArgs));
        // macOS reads `as? Bool == true`: a string is not an answer.
        Assert.True(AttributionOnboardingCard.MayShow(
            StoreWithJson("""{"tokenbar.usage.attribution.onboardingDismissed": "true"}"""), UserArgs));
        Assert.False(AttributionOnboardingCard.MayShow(
            StoreWithJson("""{"tokenbar.usage.attribution.confirmed": 7}"""), UserArgs));
    }

    // ---- isVisible --------------------------------------------------------

    [Fact]
    public void VisibilityNeedsTheGatesAndALoadedSummaryWithSomethingToOffer()
    {
        var none = new UsageAttributionSettings.OnboardingSummary([], 0);
        var proposals = new UsageAttributionSettings.OnboardingSummary([Proposal()], 0);
        var unsuggested = new UsageAttributionSettings.OnboardingSummary([], 2);

        Assert.False(AttributionOnboardingCard.IsVisible(true, null));
        Assert.False(AttributionOnboardingCard.IsVisible(true, none));
        Assert.True(AttributionOnboardingCard.IsVisible(true, proposals));
        Assert.True(AttributionOnboardingCard.IsVisible(true, unsuggested));
        Assert.False(AttributionOnboardingCard.IsVisible(false, proposals));
    }

    [Fact]
    public void ShownDrawsUntilDismissedAndNotAfter()
    {
        var store = StoreWithJson("{}");
        var report = Report(Entry("claude", "openai"));

        Assert.NotNull(AttributionOnboardingCard.Shown(store, report, Payload("Codex"), UserArgs));
        Assert.Null(AttributionOnboardingCard.Shown(store, null, Payload("Codex"), UserArgs));

        AttributionOnboardingCard.MarkDismissed(store);

        Assert.Null(AttributionOnboardingCard.Shown(store, report, Payload("Codex"), UserArgs));
        Assert.True(new SettingsStore(StorePath).GetBool(AttributionOnboardingCard.DismissedKey, false));
    }

    // ---- accept ----------------------------------------------------------

    [Fact]
    public void AcceptConfirmsTheRecordsAndRemovesThemFromTheSuggestions()
    {
        var store = StoreWithJson("{}");
        Assert.Null(UsageAttributionPage.RefreshSuggestions(
            store, [Entry("claude", "openai")], Payload("Codex")));
        Assert.Equal([Proposal()], UsageAttribution.Suggestions(store).Records);

        Assert.Null(UsageAttributionSettings.Accept(store, [Proposal()]));

        var reread = new SettingsStore(StorePath);
        Assert.Equal([Proposal()], UsageAttribution.Confirmed(reread).Records);
        Assert.Empty(UsageAttribution.Suggestions(reread).Records);
    }

    [Fact]
    public void AcceptOfNothingIsASuccessThatWritesNothing()
    {
        var store = StoreWithJson("""{"tokenbar.unrelated": "x"}""");
        var before = File.ReadAllText(StorePath);

        Assert.Null(UsageAttributionSettings.Accept(store, []));

        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    // The encode-before-write rule: confirmed would encode fine, suggestions
    // holds a foreign value and cannot. Nothing may be written, or confirmed
    // would carry the records while suggestions still offers them.
    [Fact]
    public void AnEncodeFailureOnSuggestionsWritesNeitherTable()
    {
        const string raw = """{"tokenbar.usage.attribution.suggestions": 7}""";
        var store = StoreWithJson(raw);

        Assert.Equal(
            UsageAttributionSettings.WriteFailure.InvalidExistingValue,
            UsageAttributionSettings.Accept(store, [Proposal()]));

        Assert.Equal(raw, File.ReadAllText(StorePath));
    }

    [Fact]
    public void AnEncodeFailureOnConfirmedWritesNeitherTable()
    {
        const string raw = """{"tokenbar.usage.attribution.confirmed": 7}""";
        var store = StoreWithJson(raw);

        Assert.Equal(
            UsageAttributionSettings.WriteFailure.InvalidExistingValue,
            UsageAttributionSettings.Accept(store, [Proposal()]));

        Assert.Equal(raw, File.ReadAllText(StorePath));
    }

    // Settings' "Accept all" goes through the same Accept: the failure above
    // must leave the file untouched from there too (the old two-step write
    // confirmed first and only then discovered suggestions could not encode).
    [Fact]
    public void SettingsAcceptAllSharesTheSameAtomicWrite()
    {
        const string raw = """{"tokenbar.usage.attribution.suggestions": 7}""";
        var store = StoreWithJson(raw);
        var rows = UsageAttributionSettings.Rows(
            [Entry("claude", "openai")], [], [Proposal()]);

        Assert.Equal(
            UsageAttributionSettings.WriteFailure.InvalidExistingValue,
            UsageAttributionPage.AcceptAll(store, rows));

        Assert.Equal(raw, File.ReadAllText(StorePath));
    }

    [Fact]
    public void ApplyReportsTheFailureMessageAndNullOnSuccess()
    {
        var summary = new UsageAttributionSettings.OnboardingSummary([Proposal()], 0);

        Assert.Null(AttributionOnboardingCard.Apply(StoreWithJson("{}"), summary));
        Assert.Equal(
            UsageAttributionPage.Copy.InvalidExistingValue,
            AttributionOnboardingCard.Apply(
                StoreWithJson("""{"tokenbar.usage.attribution.confirmed": 7}"""), summary));
    }

    // ---- lines and copy --------------------------------------------------

    [Fact]
    public void SuggestionLineNamesClientProviderAndTarget()
    {
        string Name(string id) => ClientRegistry.Style(id).DisplayName;

        Assert.Equal($"{Name("claude")} · openai → {Name("codex")}",
            AttributionOnboardingCard.SuggestionLine(Proposal()));
        Assert.Equal($"{Name("claude")} · Unspecified provider → {Name("codex")}",
            AttributionOnboardingCard.SuggestionLine(Proposal(provider: "")));
        Assert.Equal($"{Name("claude")} · openai → Not a subscription",
            AttributionOnboardingCard.SuggestionLine(
                new UsageAttribution.Record("claude", "openai", UsageAttribution.State.Excluded)));
    }

    [Fact]
    public void AtMostFourLinesThenAndNMore()
    {
        var six = new UsageAttributionSettings.OnboardingSummary(
            [.. Enumerable.Range(0, 6).Select(i => Proposal(provider: "p" + i))], 0);

        Assert.Equal(4, AttributionOnboardingCard.VisibleLines(six).Count);
        Assert.Equal("and 2 more", AttributionOnboardingCard.MoreLine(six));

        var four = new UsageAttributionSettings.OnboardingSummary(six.Records.Take(4).ToList(), 0);
        Assert.Equal(4, AttributionOnboardingCard.VisibleLines(four).Count);
        Assert.Null(AttributionOnboardingCard.MoreLine(four));
    }

    [Fact]
    public void TheUnsuggestedLineAppearsOnlyWhenThereAreUnsuggestedSources()
    {
        Assert.Equal(
            "Without a suggestion: 3 — set them in Settings.",
            AttributionOnboardingCard.UnsuggestedLine(
                new UsageAttributionSettings.OnboardingSummary([], 3)));
        Assert.Null(AttributionOnboardingCard.UnsuggestedLine(
            new UsageAttributionSettings.OnboardingSummary([Proposal()], 0)));
    }

    [Fact]
    public void CopyIsMacOSsExactEnglish()
    {
        Assert.Equal("Attribute usage to subscriptions", AttributionOnboardingCard.Copy.Title);
        Assert.Equal(
            "Quota history shows no tokens or API-equivalent value until usage is attributed.",
            AttributionOnboardingCard.Copy.Subtitle);
        Assert.Equal("Not now", AttributionOnboardingCard.Copy.NotNow);
        Assert.Equal("Set up manually…", AttributionOnboardingCard.Copy.SetUpManually);
        Assert.Equal("Apply suggestions", AttributionOnboardingCard.Copy.ApplySuggestions);
        Assert.Equal(0.10, AttributionOnboardingCard.AccentFill);
        Assert.Equal(0.55, AttributionOnboardingCard.AccentStroke);
    }

    [Fact]
    public void EveryCopyStringHasBothTranslations()
    {
        string[] keys =
        [
            AttributionOnboardingCard.Copy.Title,
            AttributionOnboardingCard.Copy.Subtitle,
            AttributionOnboardingCard.Copy.SuggestionLine,
            AttributionOnboardingCard.Copy.MoreCount,
            AttributionOnboardingCard.Copy.UnsuggestedHint,
            AttributionOnboardingCard.Copy.NotNow,
            AttributionOnboardingCard.Copy.SetUpManually,
            AttributionOnboardingCard.Copy.ApplySuggestions,
        ];
        foreach (var file in new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" })
        {
            var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
            foreach (var key in keys)
            {
                Assert.True(table.ContainsKey(key), $"{file}: {key}");
            }
        }
    }
}
