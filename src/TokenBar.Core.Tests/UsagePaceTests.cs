using System.Globalization;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Ported from SelfTest.swift's UsagePace block. Fixtures use the typed v3
// paceStatus contract; WindowMinutes is only the compatibility mirror.
public class UsagePaceTests
{
    // Localization.Load installs one process-wide table. English assertions in
    // this class used to hold only because every test that loads zh-Hant
    // restores it in a finally — a convention every future test would have to
    // remember. Establishing the precondition here makes it a mechanism: xUnit
    // constructs the class before each test.
    public UsagePaceTests() => Localization.Load("en", AppContext.BaseDirectory);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private static UsageWindow Window(
        double used,
        UsagePaceState state = UsagePaceState.LearningHistory,
        long? durationSeconds = 3_600,
        double untilReset = 1_800,
        HistoricalPace? historicalPace = null,
        string? resetsAt = null,
        long? windowMinutes = null,
        UsagePaceUnavailableReason? reason = null)
    {
        var duration = state is UsagePaceState.LearningHistory or UsagePaceState.Available
            ? durationSeconds : null;
        UsagePaceDurationSource? durationSource = duration is not null
            ? UsagePaceDurationSource.Contract
            : state == UsagePaceState.LearningDuration
                ? UsagePaceDurationSource.Observed : null;
        var status = new PaceStatus(
            State: state,
            WindowKey: state == UsagePaceState.LegacyMissing ? null : "session.v3",
            DurationSeconds: duration,
            DurationSource: durationSource,
            CompleteCycles: state == UsagePaceState.Available ? 5 : 0,
            Reason: state == UsagePaceState.Unavailable ? reason : null);
        var compatibilityMinutes = windowMinutes ??
            (duration is { } exactDuration
                ? exactDuration / 60
                : state == UsagePaceState.LegacyMissing ? 60 : null);

        return new UsageWindow(
            Label: "Session",
            UsedPercent: used,
            RemainingPercent: 100 - used,
            ResetsAt: resetsAt ?? ResetAt(untilReset),
            ResetText: null,
            WindowMinutes: compatibilityMinutes,
            CardId: state == UsagePaceState.LegacyMissing ? null : "session.v3",
            PaceStatus: status,
            HistoricalPace: historicalPace,
            DurationSeconds: duration);
    }

    private static string ResetAt(double seconds) =>
        Now.AddSeconds(seconds).UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [Fact]
    public void OnTrackAtHalfway()
    {
        var pace = UsagePace.Compute(Window(used: 50), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.OnTrack, pace.Stage);
        Assert.Equal(UsagePaceBasis.Linear, pace.Basis);
        Assert.Equal("On pace", pace.Label);
    }

    [Fact]
    public void FarAheadLabelAndEta()
    {
        var pace = UsagePace.Compute(Window(used: 80), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.FarAhead, pace.Stage);
        Assert.Equal("30% in deficit", pace.Label);
        // 80% in 30min → 100% in 37.5min, before the 30min reset → ETA 7.5min.
        Assert.False(pace.WillLastToReset);
        Assert.True(Math.Abs(pace.EtaSeconds!.Value - 450) < 1);
        Assert.Equal("Projected empty in 8m", pace.EtaText);
    }

    [Fact]
    public void BehindPaceIsReserveAndLasts()
    {
        var pace = UsagePace.Compute(Window(used: 40), Now);
        Assert.NotNull(pace);
        Assert.Equal(PaceStage.Behind, pace.Stage);
        Assert.Equal("10% in reserve", pace.Label);
        Assert.True(pace.WillLastToReset);
        Assert.Equal("On average: lasts until reset", pace.EtaText);
    }

    [Fact]
    public void HistoricalAvailablePreservesBackendProjection()
    {
        var historical = new HistoricalPace(
            ExpectedUsedPercent: 50,
            EtaSeconds: 120,
            WillLastToReset: false,
            RunOutProbability: 0.8);
        var pace = UsagePace.Compute(
            Window(used: 90, state: UsagePaceState.Available, historicalPace: historical),
            PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceBasis.Historical, pace.Basis);
        Assert.Equal(50, pace.ExpectedUsedPercent);
        Assert.Equal(120, pace.EtaSeconds);
        Assert.False(pace.WillLastToReset);
        Assert.True(pace.IsHistoricalDeficit);
        Assert.Equal(PaceStage.FarAhead, pace.Stage);
    }

    [Fact]
    public void HistoricalLearningHistoryFallsBackToLinear()
    {
        var pace = UsagePace.Compute(
            Window(used: 80), PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceBasis.Linear, pace.Basis);
        Assert.Equal(50, pace.ExpectedUsedPercent);
        Assert.True(pace.Stage.IsDeficit());
        Assert.False(pace.IsHistoricalDeficit);
    }

    [Fact]
    public void HistoricalOnlyDeficitUsesHistoricalBasis()
    {
        var pace = UsagePace.Compute(
            Window(
                used: 90,
                state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(50, 120, false)),
            PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.True(pace.IsHistoricalDeficit);
    }

    [Theory]
    [InlineData(UsagePaceState.LearningDuration)]
    [InlineData(UsagePaceState.Unavailable)]
    [InlineData(UsagePaceState.LegacyMissing)]
    public void HistoricalModeSuppressesNonReadyStates(UsagePaceState state)
    {
        var window = Window(used: 50, state: state);
        Assert.Null(UsagePace.Compute(window, PaceMode.Historical, Now));
        Assert.Null(UsagePace.Compute(window, PaceMode.Linear, Now));
        Assert.Null(UsagePace.Compute(window, Now));
    }

    [Fact]
    public void HistoricalAvailableRequiresHistoricalPace()
    {
        var window = Window(used: 50, state: UsagePaceState.Available);
        Assert.Null(UsagePace.Compute(window, PaceMode.Historical, Now));
    }

    [Fact]
    public void LinearModeIgnoresHistoricalProjection()
    {
        var window = Window(
            used: 50,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(80, 120, false, 0.8));
        var pace = UsagePace.Compute(window, PaceMode.Linear, Now);

        Assert.NotNull(pace);
        Assert.Equal(UsagePaceBasis.Linear, pace.Basis);
        Assert.Equal(50, pace.ExpectedUsedPercent);
        Assert.Null(UsagePace.RunOutRiskLabel(window, pace));
        Assert.Null(UsagePace.Presentation(window, PaceMode.Linear, pace).RiskText);
    }

    [Fact]
    public void LinearModeRequiresDurationReadyState()
    {
        var historical = new HistoricalPace(80, 120, false, 0.8);
        Assert.Null(UsagePace.Compute(
            Window(50, UsagePaceState.LearningDuration, historicalPace: null),
            PaceMode.Linear, Now));
        Assert.Null(UsagePace.Compute(
            Window(50, UsagePaceState.Available, durationSeconds: null, historicalPace: historical),
            PaceMode.Linear, Now));
    }

    [Fact]
    public void PresentationSuppressesLastsWhenRiskIsVisible()
    {
        var window = Window(
            used: 50,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(
                ExpectedUsedPercent: 80,
                EtaSeconds: null,
                WillLastToReset: true,
                RunOutProbability: 0.2));
        var pace = UsagePace.Compute(window, PaceMode.Historical, Now);
        Assert.NotNull(pace);
        Assert.False(pace.IsHistoricalDeficit);
        Assert.Equal("Historically: lasts until reset", pace.EtaText);

        var presentation = UsagePace.Presentation(window, PaceMode.Historical, pace);
        Assert.Null(presentation.EtaText);
        Assert.Equal("≈ 20% run-out risk", presentation.RiskText);
    }

    [Fact]
    public void RunOutRiskLabelFormatsHistoricalAvailableOnly()
    {
        var window = Window(
            used: 50,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(50, null, true, 0.3));
        Assert.Equal("≈ 30% run-out risk", UsagePace.RunOutRiskLabel(window));
        Assert.Null(UsagePace.RunOutRiskLabel(window, UsagePace.Compute(window, PaceMode.Linear, Now)));
        Assert.Null(UsagePace.RunOutRiskLabel(
            Window(50, state: UsagePaceState.LearningHistory), null));
        Assert.Null(UsagePace.RunOutRiskLabel(
            Window(50, state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(50, null, true, 0)), null));
        Assert.Null(UsagePace.RunOutRiskLabel(
            Window(50, state: UsagePaceState.Available), null));
    }

    [Fact]
    public void HistoricalExhaustedEtaUsesBackendZero()
    {
        var window = Window(
            used: 100,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(80, 0, false, 1));
        var pace = UsagePace.Compute(window, PaceMode.Historical, Now);

        Assert.NotNull(pace);
        Assert.Equal(0, pace.EtaSeconds);
        Assert.False(pace.WillLastToReset);
        Assert.Equal("Projected empty now", pace.EtaText);
        Assert.Equal("≈ 100% run-out risk", UsagePace.RunOutRiskLabel(window));
    }

    [Fact]
    public void NoWindowLengthNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, durationSeconds: null), Now));

    [Fact]
    public void PastResetNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: -10), Now));

    [Fact]
    public void ResetBeyondDurationNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: 3_601), Now));

    [Fact]
    public void ElapsedZeroWithUsageNoPace() =>
        Assert.Null(UsagePace.Compute(Window(used: 50, untilReset: 3_600), Now));

    [Fact]
    public void Rfc3339RequiresExplicitZoneAndFullConsumption()
    {
        var timestamp = Now.AddSeconds(1_800).UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string[] invalid =
        [
            timestamp,
            timestamp.Replace('T', 't') + "Z",
            timestamp + "+0000",
            timestamp + "Zjunk",
        ];

        foreach (var resetsAt in invalid)
        {
            Assert.Null(UsagePace.Compute(Window(used: 50, resetsAt: resetsAt), Now));
        }
    }

    [Fact]
    public void ExactDurationUsesPaceStatusSeconds()
    {
        // A non-minute duration proves timing uses exact v3 seconds rather than
        // the compatibility windowMinutes field.
        var pace = UsagePace.Compute(
            Window(used: 50, durationSeconds: 3_601), Now);
        var expected = (3_601d - 1_800) / 3_601 * 100;

        Assert.NotNull(pace);
        Assert.Equal(expected, pace.ExpectedUsedPercent);
        Assert.NotEqual(50, pace.ExpectedUsedPercent);
    }

    [Fact]
    public void ModeOffIsNull() =>
        Assert.Null(UsagePace.Compute(Window(used: 50), PaceMode.Off, Now));

    [Theory]
    [InlineData(130 * 60, "2h 10m")]
    [InlineData(26 * 3600, "1d 2h")]
    [InlineData(20, "now")]
    [InlineData(30, "1m")] // Swift .rounded() is away-from-zero: 0.5 min → 1m
    [InlineData(5 * 60, "5m")]
    public void DurationTextBands(double seconds, string expected) =>
        Assert.Equal(expected, UsagePace.DurationText(seconds));

    [Fact]
    public void CanonicalRfc3339VariantsParse()
    {
        var reset = Now.AddSeconds(1_800);
        var timestamp = reset.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string[] valid =
        [
            timestamp + "Z",
            timestamp + "z",
            timestamp + ".123Z",
            timestamp + ".12345678Z",
            reset.ToOffset(TimeSpan.FromHours(8)).ToString(
                "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        ];

        foreach (var resetsAt in valid)
        {
            Assert.NotNull(UsagePace.Compute(Window(used: 50, resetsAt: resetsAt), Now));
        }
    }

    [Fact]
    public void LinearTimingMatchesFoundationUnixDoublePrecision()
    {
        var now = DateTimeOffset.Parse(
            "2026-07-10T12:00:00.123Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var pace = UsagePace.Compute(
            Window(
                used: 40,
                durationSeconds: 18_000,
                resetsAt: "2026-07-10T15:00:00Z",
                windowMinutes: 300),
            PaceMode.Historical,
            now);

        Assert.NotNull(pace);
        Assert.Equal(40.00068333281411, pace.ExpectedUsedPercent);
        Assert.Equal(-0.0006833328141127026, pace.DeltaPercent);
    }

    [Fact]
    public void ResetFractionUsesFoundationMillisecondPrecision()
    {
        var now = DateTimeOffset.Parse(
            "2026-07-10T12:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        UsagePace Compute(string resetsAt) => UsagePace.Compute(
            Window(
                used: 40,
                durationSeconds: 18_000,
                resetsAt: resetsAt,
                windowMinutes: 300),
            PaceMode.Linear,
            now)!;

        var milliseconds = Compute("2026-07-10T15:00:00.123Z");
        var higherPrecision = Compute("2026-07-10T15:00:00.12345678Z");

        Assert.Equal(milliseconds.ExpectedUsedPercent, higherPrecision.ExpectedUsedPercent);
        Assert.Equal(milliseconds.DeltaPercent, higherPrecision.DeltaPercent);
    }

    [Fact]
    public void RowPresentationComposesHistoricalProjectionAndDeficit()
    {
        var window = Window(
            used: 90,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(50, 120, false, 0.8));
        var row = UsagePace.RowPresentation(
            window, PaceMode.Historical, asUsed: true, classic: false, Now);

        Assert.Equal("40% in deficit", row.PaceText);
        Assert.Equal(
            "Projected empty in 2m · ≈ 80% run-out risk", row.ProjectionText);
        Assert.Equal(50, row.ExpectedUsedPercent);
        Assert.Equal(50, row.MarkerPercent);
        Assert.True(row.IsHistoricalDeficit);
    }

    [Fact]
    public void RowPresentationLabelsLearningHistoryLinearFallback()
    {
        var row = UsagePace.RowPresentation(
            Window(used: 80), PaceMode.Historical, asUsed: true, classic: false, Now);

        Assert.Equal(
            "Learning history · Linear estimate · 30% in deficit", row.PaceText);
        Assert.Equal("Projected empty in 8m", row.ProjectionText);
        Assert.False(row.IsHistoricalDeficit);
    }

    [Fact]
    public void RowPresentationLabelsAvailableLinearWithoutRisk()
    {
        var row = UsagePace.RowPresentation(
            Window(
                used: 50,
                state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(80, 120, false, 0.8)),
            PaceMode.Linear,
            asUsed: true,
            classic: false,
            Now);

        Assert.Equal("Linear · On pace", row.PaceText);
        Assert.Equal("On average: lasts until reset", row.ProjectionText);
        Assert.DoesNotContain("risk", row.ProjectionText ?? "");
    }

    [Theory]
    [InlineData(UsagePaceState.LearningDuration, null, "Learning reset duration")]
    [InlineData(UsagePaceState.LegacyMissing, null, "Pace unavailable · legacy data")]
    [InlineData(UsagePaceState.Unavailable, null, "Pace unavailable · unavailable reason")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.WindowIdentity,
        "Pace unavailable · unknown quota window")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.MissingReset,
        "Pace unavailable · missing reset")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.InvalidEvidence,
        "Pace unavailable · invalid quota data")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.AccountScope,
        "Pace unavailable · account identity unavailable")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.StoreCapacity,
        "Pace unavailable · history storage full")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.History,
        "Pace unavailable · history unavailable")]
    [InlineData(
        UsagePaceState.Unavailable,
        UsagePaceUnavailableReason.NonRecurring,
        "Pace unavailable · non-recurring quota")]
    public void RowPresentationUsesCanonicalStatusCopy(
        UsagePaceState state,
        UsagePaceUnavailableReason? reason,
        string expected)
    {
        var row = UsagePace.RowPresentation(
            Window(50, state: state, reason: reason),
            PaceMode.Historical,
            asUsed: true,
            classic: false,
            Now);

        Assert.Equal(expected, row.PaceText);
    }

    [Fact]
    public void RowPresentationSuppressesPaceForOffAndClassic()
    {
        var off = UsagePace.RowPresentation(
            Window(50), PaceMode.Off, asUsed: true, classic: false, Now);
        Assert.Null(off.PaceText);
        Assert.Null(off.ProjectionText);
        Assert.Null(off.MarkerPercent);
        Assert.Null(off.ExpectedUsedPercent);
        Assert.False(off.IsHistoricalDeficit);

        var classic = UsagePace.RowPresentation(
            Window(50), PaceMode.Historical, asUsed: true, classic: true, Now);
        Assert.Null(classic.PaceText);
        Assert.Null(classic.ProjectionText);
        Assert.Null(classic.MarkerPercent);
        Assert.Null(classic.ExpectedUsedPercent);
        Assert.False(classic.IsHistoricalDeficit);
    }

    [Fact]
    public void RowPresentationUsesClampedAxesAndAwayFromZeroAmount()
    {
        var window = Window(
            used: 12.5,
            state: UsagePaceState.Available,
            historicalPace: new HistoricalPace(12.5));
        var used = UsagePace.RowPresentation(
            window, PaceMode.Historical, asUsed: true, classic: false, Now);
        var remaining = UsagePace.RowPresentation(
            window, PaceMode.Historical, asUsed: false, classic: false, Now);

        Assert.Equal(87.5, used.RemainingPercent);
        Assert.Equal(12.5, used.FillPercent);
        Assert.Equal("13% used", used.AmountText);
        Assert.Equal(12.5, used.MarkerPercent);
        Assert.Equal(12.5, used.ExpectedUsedPercent);

        Assert.Equal(87.5, remaining.FillPercent);
        Assert.Equal("88% left", remaining.AmountText);
        Assert.Equal(87.5, remaining.MarkerPercent);

        var clamped = UsagePace.RowPresentation(
            Window(
                used: 125,
                state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(12.5)),
            PaceMode.Historical,
            asUsed: false,
            classic: false,
            Now);
        Assert.Equal(0, clamped.RemainingPercent);
        Assert.Equal(0, clamped.FillPercent);
        Assert.Equal("0% left", clamped.AmountText);
    }

    // ---- ResetText ------------------------------------------------------
    //
    // Ported from Format.swift's resetText(for:now:). Two roundings stack —
    // floor on the raw seconds, then a ceiling to whole minutes — so the
    // boundaries are asserted individually rather than sampled.

    private static string ResetIn(double seconds) =>
        UsagePace.ResetText(ResetAt(seconds), Now)!;

    [Fact]
    public void ResetTextReturnsNullWhenTheTimestampWillNotParse()
    {
        Assert.Null(UsagePace.ResetText("garbage", Now));
        Assert.Null(UsagePace.ResetText("2026-07-10T15:00:00", Now)); // no zone
    }

    [Theory]
    [InlineData(0, "Resets now")]
    [InlineData(-1, "Resets now")]
    [InlineData(-86_400, "Resets now")]
    // One second left is a minute, not "now": the ceiling runs after the floor.
    [InlineData(1, "Resets in 1m")]
    [InlineData(59, "Resets in 1m")]
    [InlineData(60, "Resets in 1m")]
    [InlineData(61, "Resets in 2m")]
    [InlineData(120, "Resets in 2m")]
    [InlineData(3_600, "Resets in 1h")]
    [InlineData(7_800, "Resets in 2h 10m")]
    // The case from the report: a five-day weekly window.
    [InlineData(5 * 86_400 + 17 * 3_600, "Resets in 5d 17h")]
    public void ResetTextRoundsAtEveryBoundary(double seconds, string expected) =>
        Assert.Equal(expected, ResetIn(seconds));

    [Fact]
    public void TranslatedResetTextKeepsChineseWordOrder() => InChinese(() =>
    {
        Assert.Equal("現在重置", ResetIn(0));
        Assert.Equal("1分 後重置", ResetIn(1));
        Assert.Equal("5天17小時 後重置", ResetIn(5 * 86_400 + 17 * 3_600));
        Assert.Equal("2小時10分 後重置", ResetIn(7_800));
    });

    // ---- i18n ----------------------------------------------------------
    //
    // A pace card shows one status at a time, so no screenshot can prove the
    // other eleven have table entries; only driving each branch can. These run
    // against the *shipped* strings-zh-Hant.json (copied to the output by the
    // csproj) rather than a fixture, because the failure being guarded against
    // is a wrapped call site whose key was never added to that file — against a
    // fixture written alongside the test, that failure is unreachable.

    private static void InChinese(Action body)
    {
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            body();
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    // Each surface is rendered twice — once with no table, once with the
    // shipped one — and the two must differ. Comparing against the English
    // *key* would not work: a missing entry for "{0}% in deficit" renders
    // "30% in deficit", which is not equal to its key either, so the check
    // would pass on exactly the bug it exists to catch.
    [Fact]
    public void EveryPaceStringHasAShippedTranslation()
    {
        var surfaces = new List<(string Name, Func<string> Render)>
        {
            ("On pace", () => UsagePace.Compute(Window(used: 50), Now)!.Label),
            ("in deficit", () => UsagePace.Compute(Window(used: 80), Now)!.Label),
            ("in reserve", () => UsagePace.Compute(Window(used: 40), Now)!.Label),
            ("On average: lasts until reset",
                () => UsagePace.Compute(Window(used: 40), Now)!.EtaText!),
            ("Historically: lasts until reset",
                () => UsagePace.Compute(
                    Window(
                        used: 50,
                        state: UsagePaceState.Available,
                        historicalPace: new HistoricalPace(
                            ExpectedUsedPercent: 80,
                            EtaSeconds: null,
                            WillLastToReset: true,
                            RunOutProbability: 0.2)),
                    PaceMode.Historical,
                    Now)!.EtaText!),
            ("Projected empty in", () => UsagePace.Compute(Window(used: 80), Now)!.EtaText!),
            // 0.01 points left at ~0.028 points/s → ETA under 30s, which
            // DurationText rounds to the "now" token.
            ("Projected empty now",
                () => UsagePace.Compute(Window(used: 99.99, untilReset: 1), Now)!.EtaText!),
            ("run-out risk", () => UsagePace.RunOutRiskLabel(Window(
                used: 50,
                state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(50, RunOutProbability: 0.3)))!),
            ("duration.now", () => UsagePace.DurationText(20)),
            ("m", () => UsagePace.DurationText(5 * 60)),
            ("h", () => UsagePace.DurationText(2 * 3600)),
            ("h m", () => UsagePace.DurationText(130 * 60)),
            ("d", () => UsagePace.DurationText(48 * 3600)),
            ("d h", () => UsagePace.DurationText(26 * 3600)),
            ("% used", () => Amount(asUsed: true)),
            ("% left", () => Amount(asUsed: false)),
            ("Learning reset duration",
                () => Status(Window(used: 50, state: UsagePaceState.LearningDuration))),
            ("legacy data",
                () => Status(Window(used: 50, state: UsagePaceState.LegacyMissing))),
            ("Learning history",
                () => Status(Window(used: 50, state: UsagePaceState.LearningHistory))),
            ("Linear",
                () => Status(
                    Window(used: 50, state: UsagePaceState.LearningHistory), PaceMode.Linear)),
        };

        // null covers the defensive arm as well as an Unavailable window that
        // arrived without a reason.
        foreach (var reason in Enum.GetValues<UsagePaceUnavailableReason>()
            .Select(r => (UsagePaceUnavailableReason?)r).Append(null))
        {
            var captured = reason;
            surfaces.Add((
                $"unavailable · {reason?.ToString() ?? "no reason"}",
                () => Status(
                    Window(used: 50, state: UsagePaceState.Unavailable, reason: captured))));
        }

        Localization.Load("en", AppContext.BaseDirectory);
        var english = surfaces.Select(s => s.Render()).ToList();
        InChinese(() =>
        {
            for (var i = 0; i < surfaces.Count; i++)
            {
                var chinese = surfaces[i].Render();
                Assert.True(
                    chinese != english[i],
                    $"no zh-Hant entry for {surfaces[i].Name}: still renders "
                        + $"\"{english[i]}\"");
            }
        });
    }

    // NotEqual proves a key resolved; it cannot prove the placeholder landed in
    // the right place. Chinese leads with the qualifier where English trails it,
    // so a fragment-wrapped call site would render "57% 剩餘" and still pass.
    [Fact]
    public void TranslatedPlaceholdersKeepChineseWordOrder() => InChinese(() =>
    {
        Assert.Equal("超前 30%", UsagePace.Compute(Window(used: 80), Now)!.Label);
        Assert.Equal("保留 10%", UsagePace.Compute(Window(used: 40), Now)!.Label);
        Assert.Equal("剩餘 88%", Amount(asUsed: false));
        Assert.Equal("已用 13%", Amount(asUsed: true));
        Assert.Equal("預計 8分 後用盡", UsagePace.Compute(Window(used: 80), Now)!.EtaText);
        Assert.Equal("2小時10分", UsagePace.DurationText(130 * 60));
    });

    private static string Amount(bool asUsed) =>
        UsagePace.RowPresentation(
            Window(used: 12.5, state: UsagePaceState.Available,
                historicalPace: new HistoricalPace(12.5)),
            PaceMode.Historical, asUsed, classic: false, Now).AmountText;

    private static string Status(UsageWindow window, PaceMode mode = PaceMode.Historical) =>
        UsagePace.RowPresentation(window, mode, asUsed: false, classic: false, Now)
            .PaceText!;

    // WindowBoundsMs: the Chart layout's sparkline gate needs the window's own
    // clock bounds, not a duration/elapsed pair.
    [Fact]
    public void WindowBoundsMsPlacesTheWindowFromItsResetAndDuration()
    {
        var window = Window(used: 50, durationSeconds: 3_600, untilReset: 1_800);
        var bounds = UsagePace.WindowBoundsMs(window);
        Assert.NotNull(bounds);
        var expectedEnd = Now.AddSeconds(1_800).ToUnixTimeMilliseconds();
        Assert.Equal(expectedEnd, bounds!.Value.EndMs);
        Assert.Equal(expectedEnd - (3_600 * 1000), bounds.Value.StartMs);
    }

    [Fact]
    public void WindowBoundsMsIsNullWithNoParseableResetOrNoDuration()
    {
        Assert.Null(UsagePace.WindowBoundsMs(
            Window(used: 50, durationSeconds: 3_600, resetsAt: "not-a-timestamp")));
        Assert.Null(UsagePace.WindowBoundsMs(
            Window(used: 50, state: UsagePaceState.LegacyMissing)));
    }
}
