using TokenBar.Core;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The Agent-limits card's status badge and trend indicator, against
/// macOS <c>AgentLimitsCard.statusBadge</c> / <c>trendIndicator</c>
/// (945dbcc2).</summary>
public class AgentLimitsTextTests
{
    public AgentLimitsTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private static AgentUsageSnapshot Snap(
        string source = "oauth", string? error = null, bool withWindow = false) =>
        new("claude", source, "2026-10-04T00:00:00Z",
            withWindow ? [new UsageWindow("Session", 20, 80, CardId: "session.v1")] : [],
            Error: error);

    // ---- Status badge ----------------------------------------------------

    // Every placeholder carries an error; the setup key must win over it.
    [Theory]
    [InlineData("unconfigured", "Set up")]
    [InlineData("keychain-consent", "Allow")]
    [InlineData("keychain-denied", "Allow")]
    public void APlaceholderCardReadsAsAPromptNotAnError(string source, string expected)
    {
        var badge = AgentLimitsText.StatusBadge(Snap(source, error: "credentials not found"), isLive: false);
        Assert.Equal(new LimitsBadge(expected, LimitsTone.Secondary), badge);
    }

    [Fact]
    public void AnErrorIsRed()
    {
        Assert.Equal(new LimitsBadge("Error", LimitsTone.Red),
            AgentLimitsText.StatusBadge(Snap(error: "HTTP 500", withWindow: true), isLive: true));
    }

    [Fact]
    public void AWorkingCardShowsItsBackendSourceUppercased()
    {
        Assert.Equal(new LimitsBadge("OAUTH", LimitsTone.Secondary),
            AgentLimitsText.StatusBadge(Snap(withWindow: true), isLive: true));
    }

    [Fact]
    public void NoWindowsReadsLiveOnlyWhileTheClientIsActive()
    {
        Assert.Equal(new LimitsBadge("Live", LimitsTone.Green),
            AgentLimitsText.StatusBadge(Snap(), isLive: true));
        Assert.Equal(new LimitsBadge("No quota", LimitsTone.Secondary),
            AgentLimitsText.StatusBadge(Snap(), isLive: false));
        Assert.Equal(new LimitsBadge("No quota", LimitsTone.Secondary),
            AgentLimitsText.StatusBadge(null, isLive: false));
    }

    [Fact]
    public void LiveClientsFoldsTraceAliasesAndIgnoresIdleBuckets()
    {
        var live = AgentLimitsText.LiveClients(
        [
            new TraceBucket("claude-code", "a", "m", 10, 1, 5),
            new TraceBucket("antigravity-cli", "a", "m", 10, 1, 5),
            new TraceBucket("gemini", "a", "m", 0, 0, 0),
        ]);
        Assert.Equal(new HashSet<string> { "claude", "antigravity" }, live);
    }

    // ---- Setup prompt -----------------------------------------------------

    [Fact]
    public void AnUnconfiguredClaudeCardOffersTheWindowsSetupTokenCommand()
    {
        var prompt = AgentLimitsText.Setup(new AgentUsageSnapshot(
            "claude", "unconfigured", "2026-10-04T00:00:00Z", [], Error: "credentials not found"));
        Assert.NotNull(prompt);
        var prose = string.Join(" ", prompt!.Parts.Where(static p => !p.IsCommand).Select(static p => p.Text));
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", prose);
        Assert.Contains("Start menu", prose);
        Assert.DoesNotContain("Keychain", prose);
        // Setting first, then how to undo it: the claude CLI reads the same
        // variable and prefers it over /login (user decision, 2026-10-04).
        Assert.Equal(
            [AgentLimitsText.ClaudeSetupCommand, AgentLimitsText.ClaudeRemoveCommand],
            prompt.Parts.Where(static p => p.IsCommand).Select(static p => p.Text));
        Assert.Contains("prefers it over /login", prose);
        Assert.Equal("and reopen Syrtis.", prompt.Parts[^1].Text);
        // The token is typed at a prompt, never passed on the command line.
        Assert.Contains("Read-Host", AgentLimitsText.ClaudeSetupCommand);
        Assert.Contains("'User'", AgentLimitsText.ClaudeSetupCommand);
        Assert.Contains("$null, 'User'", AgentLimitsText.ClaudeRemoveCommand);
    }

    // Chinese folds "reopen Syrtis" into the sentence before the removal
    // command, so nothing follows it there — the {0} entry, not a pair of
    // keys, is what lets each language place it.
    [Fact]
    public void TheRemovalCommandSitsWhereEachLanguagePutsIt()
    {
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            var prompt = AgentLimitsText.Setup(new AgentUsageSnapshot(
                "claude", "unconfigured", "2026-10-04T00:00:00Z", []))!;
            Assert.Equal(new LimitsSetupPart(AgentLimitsText.ClaudeRemoveCommand, IsCommand: true), prompt.Parts[^1]);
            Assert.Contains("優先於 /login", prompt.Parts[^2].Text);
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    // Claude's instructions name Claude's variable; another provider keeps
    // its own one-line instruction (macOS setupInstructions, #345).
    [Fact]
    public void OtherProvidersShowTheirOwnInstructionAndNothingWhenSilent()
    {
        Assert.Equal(new LimitsSetupPrompt("Run `codex` to log in"),
            AgentLimitsText.Setup(new AgentUsageSnapshot(
                "codex", "unconfigured", "2026-10-04T00:00:00Z", [], Error: "Run `codex` to log in")));
        Assert.Null(AgentLimitsText.Setup(new AgentUsageSnapshot(
            "codex", "unconfigured", "2026-10-04T00:00:00Z", [])));
    }

    [Theory]
    [InlineData("oauth")]
    [InlineData("keychain-consent")]
    [InlineData("keychain-denied")]
    public void OnlyAnUnconfiguredCardHasASetupPrompt(string source)
    {
        Assert.Null(AgentLimitsText.Setup(new AgentUsageSnapshot(
            "claude", source, "2026-10-04T00:00:00Z", [], Error: "x")));
    }

    // ---- Detail line ------------------------------------------------------

    private const string Email = "someone@example.com";

    private static AgentUsageSnapshot Who(
        string clientId = "claude", string? accountKey = null, string? error = null,
        string? email = Email, string? plan = "Max") =>
        new(clientId, "oauth", "2026-10-04T00:00:00Z",
            [new UsageWindow("Session", 20, 80, CardId: "session.v1")],
            Identity: new AgentIdentity(email, plan), Error: error, AccountKey: accountKey);

    [Fact]
    public void TheDetailLineIsEmailThenPlanAndAnErrorReplacesItInRed()
    {
        Assert.Equal(new LimitsDetail($"{Email} · Max", false), AgentLimitsText.Detail(Who()));
        Assert.Equal(new LimitsDetail("Max", false), AgentLimitsText.Detail(Who(email: null)));
        Assert.Equal(new LimitsDetail(Email, false), AgentLimitsText.Detail(Who(plan: null)));
        Assert.Null(AgentLimitsText.Detail(Who(email: null, plan: null)));
        Assert.Equal(new LimitsDetail("HTTP 500", true), AgentLimitsText.Detail(Who(error: "HTTP 500")));
    }

    // The email is shown on the card's detail line and nowhere else: not in
    // any account label (tray menu, pickers, summary lines) and not in the tray
    // tooltip, which anyone near the taskbar can read.
    [Theory]
    [InlineData(null)]
    [InlineData("claude-desktop")]
    [InlineData(@"C:\Users\me\.claude-work")]
    public void NeitherTheAccountLabelNorTheTrayLineCarriesTheEmail(string? accountKey)
    {
        var agent = Who(accountKey: accountKey);
        var payload = new AgentUsagePayload("2026-10-04T00:00:00Z", [agent]);
        Assert.DoesNotContain(Email, AccountLabel.Of(agent, payload));
        Assert.DoesNotContain(Email, AccountLabel.Of(agent, payload, full: true));
        var line = new QuotaPick(agent, agent.Windows[0]).TooltipLine(payload);
        Assert.DoesNotContain(Email, line);
        Assert.Contains("80% left", line);
    }

    // ---- Card order -------------------------------------------------------

    private static IReadOnlyList<string> Rows(IReadOnlyList<LimitsRow> rows) =>
        [.. rows.Select(r => r.Snapshot is null ? $"{r.ClientId}?"
            : r.IsPrimary ? r.ClientId : $"{r.ClientId}#{r.Snapshot.Account.AccountKey}")];

    private static IReadOnlyList<LimitsRow> Of(params AgentUsageSnapshot[] agents) =>
        [.. agents.Select(LimitsRow.Of)];

    [Fact]
    public void CardsFollowTheSavedTabOrderWithExtrasAfterTheirPrimary()
    {
        var agents = new[]
        {
            Who("claude"), Who("codex"), Who("claude", accountKey: "claude-desktop"), Who("gemini"),
        };
        Assert.Equal(["codex", "claude", "claude#claude-desktop", "gemini"],
            Rows(LimitsCardOrder.Apply(Of(agents), "codex,antigravity,claude")));
        // No saved order: payload order, extras still beside their primary.
        Assert.Equal(["claude", "claude#claude-desktop", "codex", "gemini"],
            Rows(LimitsCardOrder.Apply(Of(agents), "")));
    }

    [Fact]
    public void AnExtraWhosePrimaryIsHiddenKeepsItsCardAtTheEnd()
    {
        var agents = new[] { Who("claude", accountKey: "claude-desktop"), Who("codex") };
        Assert.Equal(["codex", "claude#claude-desktop"], Rows(LimitsCardOrder.Apply(Of(agents), "claude,codex")));
    }

    [Fact]
    public void ADropRewritesOnlyTheVisibleSlotsOfTheSharedOrder()
    {
        // grok has a tab but no quota card; it must keep its slot.
        Assert.Equal("gemini,grok,claude,codex",
            LimitsCardOrder.Dropped("claude,grok,codex,gemini", ["claude", "codex", "gemini"], "gemini", "claude"));
        Assert.Equal("codex,grok,claude,gemini",
            LimitsCardOrder.Dropped("claude,grok,codex,gemini", ["claude", "codex", "gemini"], "claude", "codex"));
    }

    // ---- Placeholder rows (macOS placeholderRows / known / baseClients) ---

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static IReadOnlyList<string> Placeholders(
        IReadOnlyList<AgentUsageSnapshot> all, IReadOnlyList<string> requested,
        bool multiClient = true, IReadOnlySet<string>? tabHidden = null,
        IReadOnlySet<string>? limitsHidden = null, IReadOnlyList<AgentUsageSnapshot>? visible = null) =>
        Rows(LimitsPlaceholders.Rows(
            visible ?? all, all, requested, multiClient, tabHidden ?? None, limitsHidden ?? None));

    [Fact]
    public void KnownClientsWithoutASnapshotGetAPlaceholderInRequestedOrder()
    {
        // foo has no placeholder and no snapshot; copilot was not requested
        // but has a snapshot, so it follows the requested ones.
        Assert.Equal(["claude?", "codex", "gemini?", "copilot"],
            Placeholders([Who("codex"), Who("copilot")], ["claude", "codex", "gemini", "foo"]));
    }

    [Fact]
    public void ThePlaceholderLabelsAreMacOss()
    {
        Assert.Equal(["Session", "Weekly"], LimitsPlaceholders.Labels["claude"]);
        Assert.Equal(["Session", "Weekly"], LimitsPlaceholders.Labels["codex"]);
        Assert.Equal(["Pro", "Flash"], LimitsPlaceholders.Labels["gemini"]);
        Assert.Equal(["Weekly"], LimitsPlaceholders.Labels["grok"]);
        Assert.Equal(["Weekly"], LimitsPlaceholders.Labels["grok-bot"]);
        Assert.Equal(5, LimitsPlaceholders.Clients.Count);
    }

    // A switched-off client must not come back as a placeholder: the limits
    // toggle everywhere, tab visibility on the multi-client card only.
    [Fact]
    public void APlaceholderObeysTheSameHideRulesAsACard()
    {
        Assert.Equal(["gemini?"], Placeholders([], ["claude", "gemini"], limitsHidden: new HashSet<string> { "claude" }));
        Assert.Equal(["claude?"], Placeholders([], ["claude", "gemini"], tabHidden: new HashSet<string> { "gemini" }));
        Assert.Equal(["gemini?"], Placeholders([], ["gemini"], multiClient: false, tabHidden: new HashSet<string> { "gemini" }));
    }

    // A hidden snapshot is filtered out of `visible`; its client must not get
    // a placeholder in its place.
    [Fact]
    public void AHiddenSnapshotIsNotReplacedByAPlaceholder()
    {
        Assert.Empty(Placeholders([Who("codex")], ["codex"], visible: []));
    }

    // An extra account does not stand in for the primary (macOS
    // expandedWithExtraAccounts): the primary still gets its placeholder.
    [Fact]
    public void AnExtraAccountOnlyClientStillGetsItsPrimaryPlaceholder()
    {
        Assert.Equal(["claude?", "claude#claude-desktop"],
            Placeholders([Who("claude", accountKey: "claude-desktop")], ["claude"]));
        Assert.True(LimitsPlaceholders.Known("claude", []));
        Assert.False(LimitsPlaceholders.Known("copilot", [Who("copilot", accountKey: "x")]));
        Assert.True(LimitsPlaceholders.Known("copilot", [Who("copilot")]));
    }

    [Fact]
    public void SettingsOffersAToggleForEveryKnownPresentClient()
    {
        Assert.Equal(["gemini", "copilot"],
            ClientRegistry.KnownLimitsClients(["gemini", "foo"], ["copilot"], LimitsPlaceholders.Clients));
    }

    // Three groups 100 tall with 10px gaps: A 0-100, B 110-210, C 220-320.
    private static readonly LimitsCardOrder.Span[] Spans =
    [
        new("C", 220, 320), new("A", 0, 100), new("B", 110, 210),
    ];

    [Theory]
    // Dragging A down: the gap under B shows B's bottom line, so it is B's.
    [InlineData("A", 50, "A")]
    [InlineData("A", 105, "A")]
    [InlineData("A", 150, "B")]
    [InlineData("A", 215, "B")]
    [InlineData("A", 400, "C")]
    // Dragging C up: the gap above B shows B's top line, so it is B's.
    [InlineData("C", 250, "C")]
    [InlineData("C", 215, "C")]
    [InlineData("C", 205, "B")]
    [InlineData("C", 105, "B")]
    [InlineData("C", 50, "A")]
    [InlineData("C", -40, "A")]
    public void EveryPointerPositionDropsOntoTheGroupWhoseLineItShows(string dragged, double y, string expected)
    {
        Assert.Equal(expected, LimitsCardOrder.DropTarget(Spans, dragged, y));
    }

    [Fact]
    public void TheDropLineSitsBelowWhenDraggingDown()
    {
        string[] visible = ["claude", "codex", "gemini"];
        Assert.True(LimitsCardOrder.DropsBelow(visible, "claude", "gemini"));
        Assert.False(LimitsCardOrder.DropsBelow(visible, "gemini", "claude"));
    }

    // ---- Trend indicator -------------------------------------------------

    [Fact]
    public void TheUsedAxisSignsTheDelta()
    {
        var up = AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Rising, 68, 18), asUsed: true);
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Rising, "+18% used", LimitsTone.Secondary), up);
        var down = AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Falling, 40, -10), asUsed: true);
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Falling, "−10% used", LimitsTone.Secondary), down);
    }

    // On the remaining axis the arrow flips and the delta is worded: a minus
    // beside "left" would read as a negative remainder.
    [Fact]
    public void TheRemainingAxisFlipsTheArrowAndWordsTheDelta()
    {
        var burning = AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Rising, 68, 18), asUsed: false);
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Falling, "18% less left", LimitsTone.Secondary), burning);
        var refilling = AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Falling, 40, -10), asUsed: false);
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Rising, "10% more left", LimitsTone.Secondary), refilling);
    }

    // Past 100% the delta is larger than the axis; the state is named, in red.
    [Fact]
    public void ARunOutProjectionNamesTheStateInsteadOfTheDelta()
    {
        var label = AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Rising, 175, 88), asUsed: false);
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Falling, "Recently: runs out", LimitsTone.Red), label);
    }

    [Fact]
    public void FlatOrARoundedZeroDeltaShowsTheArrowAlone()
    {
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Flat, null, LimitsTone.Tertiary),
            AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Flat, 50, 3), asUsed: true));
        Assert.Equal(new LimitsTrendLabel(QuotaTrendDirection.Rising, null, LimitsTone.Secondary),
            AgentLimitsText.TrendLabel(new QuotaTrend(QuotaTrendDirection.Rising, 50.4, 0.4), asUsed: true));
        Assert.Null(AgentLimitsText.TrendLabel(null, asUsed: true));
    }

    [Fact]
    public void TheTooltipSwitchesWordingPastAHundredPercent()
    {
        Assert.Contains("runs out before reset · projected 120% used",
            AgentLimitsText.TrendTooltip(new QuotaTrend(QuotaTrendDirection.Rising, 120, 40)));
        Assert.Contains("reaches 70% used by reset",
            AgentLimitsText.TrendTooltip(new QuotaTrend(QuotaTrendDirection.Rising, 70, 20)));
    }

    // ---- Trend resolution against the window's own bounds -----------------

    private static UsageWindow HourWindow(
        double used, long? durationSeconds = 3_600, double untilReset = 1_800) =>
        new("Session", used, 100 - used,
            ResetsAt: Now.AddSeconds(untilReset).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            CardId: "session.v3",
            PaceStatus: new PaceStatus(
                State: durationSeconds is null ? UsagePaceState.LearningDuration : UsagePaceState.LearningHistory,
                WindowKey: "session.v3",
                DurationSeconds: durationSeconds,
                DurationSource: durationSeconds is null ? UsagePaceDurationSource.Observed : UsagePaceDurationSource.Contract,
                CompleteCycles: 0,
                Reason: null));

    [Fact]
    public void TrendResolvesFromTheWindowsResetAndDuration()
    {
        // Window started 30 min ago; 20 → 30 used over the last 10 minutes.
        var nowMs = Now.ToUnixTimeMilliseconds();
        var samples = new[]
        {
            new QuotaSample(nowMs - 600_000, 20),
            new QuotaSample(nowMs, 30),
        };
        var trend = AgentLimitsText.Trend(HourWindow(30), samples, nowMs);
        Assert.NotNull(trend);
        Assert.Equal(QuotaTrendDirection.Rising, trend!.Direction);
        // 10 points per 1/6 of the window → 60 per window; half remains → +30.
        Assert.Equal(30, trend.ProjectedDeltaPercent, 6);
    }

    [Fact]
    public void NoDurationOrNoSamplesMeansNoTrend()
    {
        var nowMs = Now.ToUnixTimeMilliseconds();
        var samples = new[] { new QuotaSample(nowMs - 600_000, 20), new QuotaSample(nowMs, 30) };
        Assert.Null(AgentLimitsText.Trend(HourWindow(30, durationSeconds: null), samples, nowMs));
        Assert.Null(AgentLimitsText.Trend(HourWindow(30), null, nowMs));
    }

    // A stale payload whose reset has passed: macOS resolves it to idle and
    // draws nothing; the fold alone would return a zero-delta arrow.
    [Fact]
    public void AWindowWhoseResetHasPassedHasNoTrend()
    {
        var nowMs = Now.ToUnixTimeMilliseconds();
        var samples = new[] { new QuotaSample(nowMs - 1_200_000, 20), new QuotaSample(nowMs - 660_000, 30) };
        Assert.Null(AgentLimitsText.Trend(HourWindow(30, untilReset: -600), samples, nowMs));
    }
}
