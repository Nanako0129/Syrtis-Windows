using TokenBar.Core;

namespace TokenBar.App;

internal enum OnboardingStep
{
    Agents,
    Icon,
    Title,
    Login,
    Discord,
}

/// <summary>The Discord card's two actions. Neither turns the feature on: the
/// only route to on is the Settings toggle, where the full disclosure is
/// (OnboardingSetup.swift:55-66).</summary>
internal enum DiscordChoice
{
    SetUp,
    NotNow,
}

/// <summary>
/// First-run setup as a set of cards on the global Overview (port of macOS
/// <c>OnboardingSetup.swift</c> and the show rules in
/// <c>OnboardingSetupCards.swift</c> / <c>AnimationPaceOnboardingCard.swift</c>).
/// Every decision is here because <c>DashboardView</c> compiles under no test
/// project. Existing users go through it once too: the keys are versioned and
/// nothing reads whether a setting was changed before (OnboardingSetup.swift
/// :3-9), so they see their current choices selected.
/// </summary>
internal static class OnboardingSetup
{
    /// <summary>Same keys as macOS (OnboardingSetup.swift:17-20).</summary>
    internal const string KeyPrefix = "tokenbar.onboarding.v1.";

    internal const string CompletedKey = KeyPrefix + "completed";

    /// <summary>The pace card's own answer (AnimationPaceOnboarding.answeredKey,
    /// AnimationPaceOnboardingCard.swift:41). Choosing a pace alone does not
    /// answer it; "Done" does.</summary>
    internal const string PaceAnsweredKey = KeyPrefix + "pace";

    internal static string AnsweredKey(OnboardingStep step) =>
        KeyPrefix + step.ToString().ToLowerInvariant();

    internal static readonly OnboardingStep[] AllSteps =
        [OnboardingStep.Agents, OnboardingStep.Icon, OnboardingStep.Title,
         OnboardingStep.Login, OnboardingStep.Discord];

    /// <summary>Render order on the Overview (OnboardingSetupCards.swift
    /// :96-104). The login card is also dropped where it is not applicable.</summary>
    internal enum Card
    {
        Header,
        Agents,
        Icon,
        Title,
        Pace,
        Attribution,
        /// <summary>The Cursor sync notice; never counted in
        /// <see cref="Remaining"/>.</summary>
        CursorSync,
        Login,
        Discord,
    }

    internal static readonly Card[] RenderOrder =
    [
        Card.Header, Card.Agents, Card.Icon, Card.Title, Card.Pace,
        Card.Attribution, Card.CursorSync, Card.Login, Card.Discord,
    ];

    internal static bool IsCompleted(SettingsStore store) => store.GetBool(CompletedKey, false);

    internal static bool IsAnswered(SettingsStore store, OnboardingStep step) =>
        store.GetBool(AnsweredKey(step), false) || IsCompleted(store);

    /// <summary>Steps that apply here. Start-at-login is left out, and not
    /// counted, where it cannot work (OnboardingSetup.swift:68-73). The
    /// Windows registry Run key is always writable, so the app passes true;
    /// the parameter stays so the rule is the macOS one.</summary>
    internal static IReadOnlyList<OnboardingStep> ApplicableSteps(bool loginAvailable) =>
        [.. AllSteps.Where(s => s != OnboardingStep.Login || loginAvailable)];

    /// <summary>OnboardingSetup.swift:30-39: completed once every applicable
    /// step has its OWN key (not <see cref="IsAnswered"/>, which would be
    /// circular through completed).</summary>
    internal static void Answer(SettingsStore store, OnboardingStep step, bool loginAvailable = true)
    {
        store.SetBool(AnsweredKey(step), true);
        if (ApplicableSteps(loginAvailable).All(s => store.GetBool(AnsweredKey(s), false)))
        {
            store.SetBool(CompletedKey, true);
        }
    }

    /// <summary>"Skip setup" (OnboardingSetup.swift:41-52): every step counts as
    /// answered, the pace falls back to its default only when none was chosen,
    /// the pace card is answered and the attribution card dismissed, so neither
    /// stays behind asking on its own.</summary>
    internal static void SkipAll(SettingsStore store)
    {
        foreach (var step in AllSteps)
        {
            store.SetBool(AnsweredKey(step), true);
        }

        store.SetBool(CompletedKey, true);
        if (!IsPaceChosen(store.GetString(AnimationPaces.StorageKey)))
        {
            store.SetString(AnimationPaces.StorageKey, AnimationPaces.Default.RawValue());
        }

        store.SetBool(PaceAnsweredKey, true);
        AttributionOnboardingCard.MarkDismissed(store);
    }

    /// <summary>A stored pace that is one of the three raw values
    /// (macOS <c>AnimationPace(rawValue:) == nil</c>); <see cref="AnimationPaces.Parse"/>
    /// alone cannot tell unset from moderate.</summary>
    internal static bool IsPaceChosen(string? raw) => raw is "light" or "moderate" or "heavy";

    /// <summary>OnboardingSetup.swift:59-66. Never writes
    /// <see cref="DiscordPresence.EnabledKey"/>.</summary>
    internal static void Perform(
        SettingsStore store, DiscordChoice choice, Action openSettings, bool loginAvailable = true)
    {
        if (choice == DiscordChoice.SetUp)
        {
            openSettings();
        }

        Answer(store, OnboardingStep.Discord, loginAvailable);
    }

    /// <summary>Cards still waiting for an answer, counting the pace and
    /// attribution cards only when they would show (OnboardingSetup.swift
    /// :75-84).</summary>
    internal static int Remaining(
        SettingsStore store, bool loginAvailable, bool paceCardShows, bool attributionCardShows) =>
        ApplicableSteps(loginAvailable).Count(s => !IsAnswered(store, s))
        + (paceCardShows ? 1 : 0) + (attributionCardShows ? 1 : 0);

    /// <summary>Maps to macOS <c>!BuildIdentity.isNonUserRuntime</c>, via the
    /// run modes <see cref="DiscordPresence.TestArguments"/> names (the same
    /// check the attribution card uses).</summary>
    internal static bool IsUserRuntime(IEnumerable<string> arguments) =>
        !arguments.Any(DiscordPresence.TestArguments.Contains);

    /// <summary>A step card shows when user runtime && !completed && !answered
    /// (OnboardingSetupCards.swift:69-73); the login card also needs login to
    /// be available (:101).</summary>
    internal static bool Shows(
        SettingsStore store, OnboardingStep step, IEnumerable<string> arguments,
        bool loginAvailable = true) =>
        IsUserRuntime(arguments) && !IsCompleted(store)
        && !store.GetBool(AnsweredKey(step), false)
        && (step != OnboardingStep.Login || loginAvailable);

    /// <summary>AnimationPaceOnboardingCard.swift:46-51: an animated style that
    /// is animating, before the card was answered, in a user runtime.</summary>
    internal static bool PaceCardShows(
        string styleRaw, bool animate, bool answered, IEnumerable<string> arguments) =>
        SandShoal.IsAnimated(styleRaw) && animate && !answered && IsUserRuntime(arguments);

    internal static bool PaceCardShows(SettingsStore store, IEnumerable<string> arguments) =>
        PaceCardShows(
            store.GetString("tokenbar.tray.animationStyle", "cat") ?? "cat",
            store.GetBool("tokenbar.tray.animate", true),
            store.GetBool(PaceAnsweredKey, false),
            arguments);

    /// <summary>The header is visible when user runtime and something is left
    /// (OnboardingSetupCards.swift:95).</summary>
    internal static bool HeaderVisible(IEnumerable<string> arguments, int remaining) =>
        IsUserRuntime(arguments) && remaining > 0;

    /// <summary>Only the global Overview tab: a single client's tab already
    /// knows whose subscription it belongs to, so there is nothing to onboard
    /// (PopoverView.swift:712-720).</summary>
    internal static bool ShowsOnTab(string activeClientTab) =>
        activeClientTab == ClientRegistry.OverviewTab;

    /// <summary>Whether the setup cards render at all for this lens and tab.
    /// macOS puts them in the Overview lens only (PopoverView.swift:712).</summary>
    internal static bool ShowsOn(AppView view, string activeClientTab) =>
        view == AppView.Overview && ShowsOnTab(activeClientTab);

    /// <summary>Every present agent, hidden tabs included: the card asks which
    /// ones get a tab, so it must list the ones already hidden too. Includes
    /// quota-only clients (macOS presentTabClients, PopoverView.swift:126-129;
    /// OnboardingSetupCards.swift:151-158).</summary>
    internal static IReadOnlyList<string> PresentTabClients(
        IEnumerable<string> usageClients, IReadOnlyList<string> quotaIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ClientRegistry.TabClients(
            [.. usageClients.Select(ClientRegistry.CanonicalClient).Where(seen.Add)], quotaIds);
    }

    /// <summary>The card's body: a user who already has presence on sees the
    /// already-on text and a single Done (OnboardingSetupCards.swift:218-243).</summary>
    internal static bool DiscordAlreadyOn(SettingsStore store) => DiscordPresence.Enabled(store);

    internal static class Copy
    {
        internal const string HeaderTitle = "Set up Syrtis";
        internal const string HeaderRemaining = "{0} left · everything here is also in Settings";
        internal const string SkipAll = "Skip setup";

        // Windows wording: "this PC" for "this Mac" (OnboardingSetupCards.swift:11-12).
        internal const string AgentsTitle = "Agents on this PC";
        internal const string AgentsBody =
            "Syrtis found these agents on this PC. Choose which ones get a tab, or keep them all.";
        internal const string AgentsNone =
            "No agents found yet. A tab appears once an agent writes its logs or reports a limit.";
        internal const string ChooseTabs = "Choose tabs…";
        internal const string LooksGood = "Looks good";

        internal const string IconTitle = "Menu-bar icon";
        internal const string IconBody =
            "Animated icons follow your token rate; gauges drain as a quota window empties.";

        internal const string TitleTitle = "Menu-bar text";
        internal const string TitleBody = "What shows next to the icon.";

        internal const string Done = "Done";

        internal const string LoginTitle = "Start at login";
        internal const string LoginBody =
            "Open Syrtis when you sign in to Windows, so your usage is always in the menu bar.";
        internal const string LoginOn = "Start at login";
        internal const string LoginOff = "Not now";
        internal const string LoginAlreadyOn = "Syrtis already starts at login.";
        internal const string LoginFailed =
            "Windows did not add Syrtis to your startup apps. Check Settings → Apps → Startup.";

        internal const string DiscordTitle = "Discord";
        internal const string DiscordBody =
            "Your Discord profile can show today's usage. It is off unless you turn it on, and Settings shows exactly what would appear.";
        internal const string DiscordAlreadyOn =
            "Discord is showing your usage. Settings has what appears and how to stop it.";
        internal const string DiscordSetUp = "Set up in Settings…";
        internal const string DiscordNo = "Not now";

        // AnimationPaceOnboardingCard.swift:10-14
        internal const string PaceTitle = "How busy are your agents?";
        internal const string PaceBody =
            "The menu-bar animation follows your live token rate. Pick the range that fits how you work; you can change it later in Settings.";
        internal const string PaceRecommended = "Most people: Moderate";

        internal static readonly string[] All =
        [
            HeaderTitle, HeaderRemaining, SkipAll, AgentsTitle, AgentsBody, AgentsNone, ChooseTabs,
            LooksGood, IconTitle, IconBody, TitleTitle, TitleBody, Done, LoginTitle, LoginBody,
            LoginOn, LoginOff, LoginAlreadyOn, LoginFailed, DiscordTitle, DiscordBody,
            DiscordAlreadyOn, DiscordSetUp, DiscordNo, PaceTitle, PaceBody, PaceRecommended,
        ];
    }

    /// <summary>The pace option tints, one family deepening from slate blue
    /// through indigo to amethyst — not red, amber and green: a pace is a
    /// preference, not a limit (AnimationPaceOnboardingCard.swift:17-28).
    /// RGB bytes of macOS's top/bottom colours.</summary>
    internal static ((byte R, byte G, byte B) Top, (byte R, byte G, byte B) Bottom) PaceTint(AnimationPace pace) =>
        pace switch
        {
            AnimationPace.Light => ((143, 168, 204), (107, 135, 173)),
            AnimationPace.Heavy => ((168, 102, 219), (122, 64, 189)),
            _ => ((115, 117, 224), (82, 92, 204)),
        };

    /// <summary>Option wash and border strength; the picked option is drawn
    /// stronger so the current choice is obvious (:29-33).</summary>
    internal const double PaceOptionFill = 0.16;
    internal const double PaceOptionStroke = 0.45;
    internal const double PaceSelectedFill = 0.34;
    internal const double PaceSelectedStroke = 0.95;

    /// <summary>The icon/title choice grid's wash, selected vs not
    /// (OnboardingSetupCards.swift:271-277).</summary>
    internal const double ChoiceFill = 0.08;
    internal const double ChoiceSelectedFill = 0.28;
    internal const double ChoiceStroke = 0.2;
    internal const double ChoiceSelectedStroke = 0.7;
}
