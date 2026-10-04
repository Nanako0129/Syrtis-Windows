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

    // ---- Bars under an error (macOS AgentLimitsCard.swift:771-787) ---------
    //
    // These pin BarWindows, the one place the card decides which windows to
    // draw: an "error skips the bars" mutant there fails the first test.
    // DashboardView.BuildLimits is WinUI and no test project compiles it, so
    // a `continue` reintroduced in the view itself would not be caught here.

    // A transient failure after a success: the core hands back the last-good
    // windows with the error stamped on them. The card keeps the bars under
    // the red detail and the red badge, as macOS does.
    [Fact]
    public void AnErrorWithLastGoodWindowsKeepsTheBarsUnderTheRedLine()
    {
        var stale = Snap(error: "Copilot usage request failed. Retrying automatically.", withWindow: true);

        Assert.Equal("session.v1", Assert.Single(AgentLimitsText.BarWindows(stale)).CardId);
        Assert.Equal(new LimitsDetail(stale.Error!, IsError: true), AgentLimitsText.Detail(stale));
        Assert.Equal(LimitsTone.Red, AgentLimitsText.StatusBadge(stale, isLive: false).Tone);
    }

    // A failure with nothing cached carries no windows: only the red line.
    [Fact]
    public void AnErrorWithNoWindowsDrawsOnlyTheRedLine()
    {
        var failed = Snap(error: "Copilot usage request failed. Retrying automatically.");

        Assert.Empty(AgentLimitsText.BarWindows(failed));
        Assert.Equal(new LimitsDetail(failed.Error!, IsError: true), AgentLimitsText.Detail(failed));
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
        // The token is read live from the registry: no restart to ask for.
        Assert.DoesNotContain("Start menu", prose);
        Assert.DoesNotContain("reopen", prose);
        Assert.DoesNotContain("Keychain", prose);
        // Setting first, then how to undo it: the claude CLI reads the same
        // variable and prefers it over /login (user decision, 2026-10-04).
        Assert.Equal(
            [AgentLimitsText.ClaudeSetupCommand, AgentLimitsText.ClaudeRemoveCommand],
            prompt.Parts.Where(static p => p.IsCommand).Select(static p => p.Text));
        Assert.Contains("prefers it over /login", prose);
        // The English removal sentence ends at the command.
        Assert.Equal(new LimitsSetupPart(AgentLimitsText.ClaudeRemoveCommand, IsCommand: true), prompt.Parts[^1]);
        // The token is typed at a prompt, never passed on the command line.
        Assert.Contains("Read-Host", AgentLimitsText.ClaudeSetupCommand);
        Assert.Contains("'User'", AgentLimitsText.ClaudeSetupCommand);
        Assert.Contains("$null, 'User'", AgentLimitsText.ClaudeRemoveCommand);
    }

    // The {0} entry, not a pair of keys, is what lets each language place
    // the removal command; in zh-Hant nothing follows it, and no sentence
    // asks for a restart.
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
            var prose = string.Join(" ", prompt.Parts.Where(static p => !p.IsCommand).Select(static p => p.Text));
            Assert.DoesNotContain("重新開啟", prose);
            Assert.DoesNotContain("開始選單", prose);
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

    private static IReadOnlyList<string> Rows(IReadOnlyList<AgentUsageSnapshot> agents) =>
        [.. agents.Select(a => a.Account.AccountKey is null ? a.ClientId : $"{a.ClientId}#{a.Account.AccountKey}")];

    [Fact]
    public void CardsFollowTheSavedTabOrderWithExtrasAfterTheirPrimary()
    {
        var agents = new[]
        {
            Who("claude"), Who("codex"), Who("claude", accountKey: "claude-desktop"), Who("gemini"),
        };
        Assert.Equal(["codex", "claude", "claude#claude-desktop", "gemini"],
            Rows(LimitsCardOrder.Apply(agents, "codex,antigravity,claude")));
        // No saved order: payload order, extras still beside their primary.
        Assert.Equal(["claude", "claude#claude-desktop", "codex", "gemini"],
            Rows(LimitsCardOrder.Apply(agents, "")));
    }

    [Fact]
    public void AnExtraWhosePrimaryIsHiddenKeepsItsCardAtTheEnd()
    {
        var agents = new[] { Who("claude", accountKey: "claude-desktop"), Who("codex") };
        Assert.Equal(["codex", "claude#claude-desktop"], Rows(LimitsCardOrder.Apply(agents, "claude,codex")));
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
