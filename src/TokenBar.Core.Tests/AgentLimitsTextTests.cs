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

    private static UsageWindow HourWindow(double used, long? durationSeconds = 3_600) =>
        new("Session", used, 100 - used,
            ResetsAt: Now.AddSeconds(1_800).ToString("yyyy-MM-ddTHH:mm:ssZ"),
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
}
