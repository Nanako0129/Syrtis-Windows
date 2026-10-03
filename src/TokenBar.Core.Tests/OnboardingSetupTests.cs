using TokenBar.App;

namespace TokenBar.Core.Tests;

/// <summary>First-run setup cards (macOS OnboardingSetup.swift,
/// OnboardingSetupCards.swift, AnimationPaceOnboardingCard.swift). The cards
/// are drawn in DashboardView.Setup.cs, which no test project compiles.</summary>
public class OnboardingSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private static readonly string[] User = ["Syrtis.exe"];
    private static readonly string[] Smoke = ["Syrtis.exe", "--startup-smoke"];

    private SettingsStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        return new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));
    }

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

    private static void AnswerAll(SettingsStore store, params OnboardingStep[] steps)
    {
        foreach (var s in steps)
        {
            OnboardingSetup.Answer(store, s);
        }
    }

    // ---- keys ---------------------------------------------------------------

    [Fact]
    public void KeysMatchMacOS()
    {
        Assert.Equal("tokenbar.onboarding.v1.completed", OnboardingSetup.CompletedKey);
        Assert.Equal("tokenbar.onboarding.v1.pace", OnboardingSetup.PaceAnsweredKey);
        Assert.Equal(
            ["tokenbar.onboarding.v1.agents", "tokenbar.onboarding.v1.icon", "tokenbar.onboarding.v1.title",
             "tokenbar.onboarding.v1.login", "tokenbar.onboarding.v1.discord"],
            OnboardingSetup.AllSteps.Select(OnboardingSetup.AnsweredKey));
    }

    // ---- show rule ----------------------------------------------------------

    [Fact]
    public void FreshStoreShowsEveryStepInAUserRuntime()
    {
        var store = NewStore();
        foreach (var step in OnboardingSetup.AllSteps)
        {
            Assert.True(OnboardingSetup.Shows(store, step, User), step.ToString());
        }
    }

    [Fact]
    public void ANonUserRuntimeShowsNothing()
    {
        var store = NewStore();
        foreach (var step in OnboardingSetup.AllSteps)
        {
            Assert.False(OnboardingSetup.Shows(store, step, Smoke), step.ToString());
        }

        Assert.False(OnboardingSetup.HeaderVisible(Smoke, 3));
        Assert.True(OnboardingSetup.HeaderVisible(User, 3));
    }

    [Fact]
    public void AnAnsweredStepHidesOnlyItself()
    {
        var store = NewStore();
        OnboardingSetup.Answer(store, OnboardingStep.Icon);
        Assert.False(OnboardingSetup.Shows(store, OnboardingStep.Icon, User));
        Assert.True(OnboardingSetup.Shows(store, OnboardingStep.Title, User));
        Assert.False(OnboardingSetup.IsCompleted(store));
    }

    [Fact]
    public void CompletedHidesEveryStepEvenWithoutItsOwnKey()
    {
        var store = NewStore();
        store.SetBool(OnboardingSetup.CompletedKey, true);
        foreach (var step in OnboardingSetup.AllSteps)
        {
            Assert.False(OnboardingSetup.Shows(store, step, User), step.ToString());
            Assert.True(OnboardingSetup.IsAnswered(store, step), step.ToString());
        }
    }

    [Fact]
    public void LoginCardNeverShowsWhereAutostartIsUnavailable()
    {
        var store = NewStore();
        Assert.True(OnboardingSetup.Shows(store, OnboardingStep.Login, User, loginAvailable: true));
        Assert.False(OnboardingSetup.Shows(store, OnboardingStep.Login, User, loginAvailable: false));
    }

    [Fact]
    public void HeaderNeedsSomethingLeft()
    {
        Assert.False(OnboardingSetup.HeaderVisible(User, 0));
    }

    // ---- answer / completed ---------------------------------------------------

    [Fact]
    public void CompletedOnlyOnceEveryApplicableStepIsAnswered()
    {
        var store = NewStore();
        AnswerAll(store, OnboardingStep.Agents, OnboardingStep.Icon, OnboardingStep.Title, OnboardingStep.Login);
        Assert.False(OnboardingSetup.IsCompleted(store));
        OnboardingSetup.Answer(store, OnboardingStep.Discord);
        Assert.True(OnboardingSetup.IsCompleted(store));
    }

    [Fact]
    public void LoginIsExcludedFromCompletionWhereUnavailable()
    {
        var store = NewStore();
        foreach (var s in new[] { OnboardingStep.Agents, OnboardingStep.Icon, OnboardingStep.Title })
        {
            OnboardingSetup.Answer(store, s, loginAvailable: false);
        }

        Assert.False(OnboardingSetup.IsCompleted(store));
        OnboardingSetup.Answer(store, OnboardingStep.Discord, loginAvailable: false);
        Assert.True(OnboardingSetup.IsCompleted(store)); // login never answered

        // The same four answers with login applicable are not enough.
        var other = NewStore();
        foreach (var s in new[] { OnboardingStep.Agents, OnboardingStep.Icon, OnboardingStep.Title, OnboardingStep.Discord })
        {
            OnboardingSetup.Answer(other, s, loginAvailable: true);
        }

        Assert.False(OnboardingSetup.IsCompleted(other));
    }

    [Fact]
    public void ApplicableStepsDropLoginOnly()
    {
        Assert.Equal(5, OnboardingSetup.ApplicableSteps(true).Count);
        Assert.Equal(
            [OnboardingStep.Agents, OnboardingStep.Icon, OnboardingStep.Title, OnboardingStep.Discord],
            OnboardingSetup.ApplicableSteps(false));
    }

    // ---- skip all -------------------------------------------------------------

    [Fact]
    public void SkipAllAnswersEverythingAndSettlesPaceAndAttribution()
    {
        var store = NewStore();
        OnboardingSetup.SkipAll(store);
        foreach (var step in OnboardingSetup.AllSteps)
        {
            Assert.True(store.GetBool(OnboardingSetup.AnsweredKey(step), false), step.ToString());
        }

        Assert.True(OnboardingSetup.IsCompleted(store));
        Assert.Equal("moderate", store.GetString(AnimationPaces.StorageKey)); // unset -> default
        Assert.True(store.GetBool(OnboardingSetup.PaceAnsweredKey, false));
        Assert.True(store.GetBool(AttributionOnboardingCard.DismissedKey, false));
        Assert.Equal(0, OnboardingSetup.Remaining(store, true, paceCardShows: false, attributionCardShows: false));
    }

    [Fact]
    public void SkipAllKeepsAChosenPace()
    {
        var store = NewStore();
        store.SetString(AnimationPaces.StorageKey, "heavy");
        OnboardingSetup.SkipAll(store);
        Assert.Equal("heavy", store.GetString(AnimationPaces.StorageKey));
    }

    [Fact]
    public void SkipAllReplacesAnUnreadablePaceWithTheDefault()
    {
        var store = NewStore();
        store.SetString(AnimationPaces.StorageKey, "turbo");
        OnboardingSetup.SkipAll(store);
        Assert.Equal("moderate", store.GetString(AnimationPaces.StorageKey));
    }

    // ---- remaining ------------------------------------------------------------

    [Fact]
    public void RemainingCountsUnansweredStepsPlusPaceAndAttribution()
    {
        var store = NewStore();
        Assert.Equal(5, OnboardingSetup.Remaining(store, true, false, false));
        Assert.Equal(6, OnboardingSetup.Remaining(store, true, paceCardShows: true, attributionCardShows: false));
        Assert.Equal(6, OnboardingSetup.Remaining(store, true, paceCardShows: false, attributionCardShows: true));
        Assert.Equal(7, OnboardingSetup.Remaining(store, true, true, true));
        Assert.Equal(4, OnboardingSetup.Remaining(store, false, false, false)); // login not counted
        OnboardingSetup.Answer(store, OnboardingStep.Agents);
        Assert.Equal(6, OnboardingSetup.Remaining(store, true, true, true));
        store.SetBool(OnboardingSetup.CompletedKey, true);
        Assert.Equal(2, OnboardingSetup.Remaining(store, true, true, true)); // pace + attribution only
    }

    // ---- pace card ------------------------------------------------------------

    [Theory]
    [InlineData("cat", true, false, true)]
    [InlineData("parrot", true, false, true)]
    [InlineData("bars", true, false, false)]
    [InlineData("ring", true, false, false)]
    [InlineData("cat", false, false, false)]
    [InlineData("cat", true, true, false)]
    public void PaceCardShowsForAnAnimatingAnimatedStyleUnanswered(
        string style, bool animate, bool answered, bool expected) =>
        Assert.Equal(expected, OnboardingSetup.PaceCardShows(style, animate, answered, User));

    [Fact]
    public void PaceCardNeverShowsInANonUserRuntime() =>
        Assert.False(OnboardingSetup.PaceCardShows("cat", true, false, Smoke));

    [Fact]
    public void PaceCardReadsItsDefaultsFromTheStore()
    {
        var store = NewStore();
        Assert.True(OnboardingSetup.PaceCardShows(store, User)); // cat + animate default true
        store.SetString(AnimationPaces.StorageKey, "heavy"); // choosing alone does not answer
        Assert.True(OnboardingSetup.PaceCardShows(store, User));
        store.SetBool(OnboardingSetup.PaceAnsweredKey, true);
        Assert.False(OnboardingSetup.PaceCardShows(store, User));
    }

    [Fact]
    public void IsPaceChosenTellsUnsetFromModerate()
    {
        Assert.False(OnboardingSetup.IsPaceChosen(null));
        Assert.False(OnboardingSetup.IsPaceChosen(""));
        Assert.False(OnboardingSetup.IsPaceChosen("turbo"));
        Assert.True(OnboardingSetup.IsPaceChosen("moderate"));
    }

    // ---- discord ----------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DiscordChoicesAnswerTheStepAndNeverTurnPresenceOn(bool opens)
    {
        var choice = opens ? DiscordChoice.SetUp : DiscordChoice.NotNow;
        var store = NewStore();
        var opened = false;
        OnboardingSetup.Perform(store, choice, () => opened = true);
        Assert.Equal(opens, opened);
        Assert.True(store.GetBool(OnboardingSetup.AnsweredKey(OnboardingStep.Discord), false));
        Assert.False(DiscordPresence.Enabled(store));
        Assert.Null(store.GetString(DiscordPresence.EnabledKey)); // not even written
    }

    [Fact]
    public void DiscordAlreadyOnIsReadFromThePresenceSwitchAndLeftAlone()
    {
        var store = NewStore();
        Assert.False(OnboardingSetup.DiscordAlreadyOn(store));
        store.SetBool(DiscordPresence.EnabledKey, true);
        Assert.True(OnboardingSetup.DiscordAlreadyOn(store));
        OnboardingSetup.Perform(store, DiscordChoice.NotNow, () => { });
        Assert.True(DiscordPresence.Enabled(store)); // answering does not switch it off either
    }

    [Fact]
    public void TheLaunchTimeIntroIsRetiredAndItsOldFlagIsNotMigrated()
    {
        // macOS 2e25c7ff deleted the alert and reads tokenbar.discord.introShown
        // nowhere: a user who saw it still gets the card once.
        var store = NewStore();
        store.SetBool("tokenbar.discord.introShown", true);
        Assert.True(OnboardingSetup.Shows(store, OnboardingStep.Discord, User));
        OnboardingSetup.Perform(store, DiscordChoice.NotNow, () => { });
        Assert.False(OnboardingSetup.Shows(store, OnboardingStep.Discord, User));
        Assert.False(DiscordPresence.Enabled(store));
    }

    // ---- placement ----------------------------------------------------------------

    [Fact]
    public void SetupCardsRenderOnlyOnTheOverviewLensOfTheOverviewTab()
    {
        Assert.True(OnboardingSetup.ShowsOnTab(ClientRegistry.OverviewTab));
        Assert.False(OnboardingSetup.ShowsOnTab("claude"));
        Assert.True(OnboardingSetup.ShowsOn(AppView.Overview, ClientRegistry.OverviewTab));
        Assert.False(OnboardingSetup.ShowsOn(AppView.Overview, "claude"));
        foreach (var view in Enum.GetValues<AppView>().Where(v => v != AppView.Overview))
        {
            Assert.False(OnboardingSetup.ShowsOn(view, ClientRegistry.OverviewTab), view.ToString());
        }
    }

    [Fact]
    public void RenderOrderIsTheMacOSOne() =>
        Assert.Equal(
            [OnboardingSetup.Card.Header, OnboardingSetup.Card.Agents, OnboardingSetup.Card.Icon,
             OnboardingSetup.Card.Title, OnboardingSetup.Card.Pace, OnboardingSetup.Card.Attribution,
             OnboardingSetup.Card.Login, OnboardingSetup.Card.Discord],
            OnboardingSetup.RenderOrder);

    // ---- agents -----------------------------------------------------------------

    [Fact]
    public void PresentTabClientsKeepQuotaOnlyAgentsAndFoldGroups()
    {
        var present = OnboardingSetup.PresentTabClients(["claude", "claude", "codex"], ["copilot"]);
        Assert.Equal(["claude", "codex", "copilot"], present);
    }

    // A CLI alias and its product are one agent, as on the tab row
    // (ClientRegistry.CanonicalClient): the card must not list Claude twice.
    [Fact]
    public void PresentTabClientsFoldCliAliasesIntoOneAgent()
    {
        var present = OnboardingSetup.PresentTabClients(["claude-code", "claude", "codex-cli"], []);
        Assert.Equal(["claude", "codex"], present);
    }

    // ---- copy -------------------------------------------------------------------

    [Fact]
    public void EveryCopyStringHasBothTranslations()
    {
        foreach (var file in new[] { "strings-zh-Hant.json", "strings-zh-Hans.json" })
        {
            var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
            // "Discord" is a product name, identical in every language and absent from the table.
            foreach (var key in OnboardingSetup.Copy.All.Where(k => k != OnboardingSetup.Copy.DiscordTitle))
            {
                Assert.True(table.ContainsKey(key), $"{file}: {key}");
            }
        }
    }

    [Fact]
    public void HeaderRemainingFormatsTheCount() =>
        Assert.Equal(
            "3 left · everything here is also in Settings",
            string.Format(OnboardingSetup.Copy.HeaderRemaining, 3));
}
