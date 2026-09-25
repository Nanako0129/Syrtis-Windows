using System.Globalization;
using System.Text.RegularExpressions;
using TokenBar.Interop;

namespace TokenBar.Core;

// Usage pace — port of TokenBarCore/UsagePace.swift (originally
// src/lib/usagePace.ts, itself ported from codexbar's UsagePace).
//
// Given a rate-limit window's length and reset time, work out how much you'd
// be *expected* to have used if you paced evenly, compare it to actual usage,
// and classify the gap. Positive delta = ahead of pace ("in deficit", burning
// fast); negative = behind pace ("in reserve"). Also projects when the window
// empties at the current burn rate.

/// <summary>How the pace marker is derived (PaceMode in settings).</summary>
public enum PaceMode
{
    Historical,
    Linear,
    Off,
}

public enum PaceStage
{
    OnTrack,
    SlightlyAhead,
    Ahead,
    FarAhead,
    SlightlyBehind,
    Behind,
    FarBehind,
}

public static class PaceStageExtensions
{
    public static bool IsDeficit(this PaceStage stage) =>
        stage is PaceStage.SlightlyAhead or PaceStage.Ahead or PaceStage.FarAhead;
}

/// <summary>The source of the expected-usage projection.</summary>
public enum UsagePaceBasis
{
    Linear,
    Historical,
}

public sealed record UsagePace(
    PaceStage Stage,
    UsagePaceBasis Basis,
    // actual − expected, in percentage points (>0 = ahead/deficit).
    double DeltaPercent,
    double ExpectedUsedPercent,
    double ActualUsedPercent,
    // Seconds until the window empties, if before reset.
    double? EtaSeconds,
    // True if the current rate lasts past the reset (won't run out).
    bool WillLastToReset)
{
    /// <summary>Yellow historical deficit is valid only for a backend
    /// historical result.</summary>
    public bool IsHistoricalDeficit => Basis == UsagePaceBasis.Historical && Stage.IsDeficit();

    /// <summary>Short left-hand label: "On pace" / "12% in deficit" /
    /// "8% in reserve".</summary>
    public string Label
    {
        get
        {
            if (Stage == PaceStage.OnTrack)
            {
                return "On pace".Localized();
            }

            var d = (int)Math.Round(Math.Abs(DeltaPercent), MidpointRounding.AwayFromZero);
            return Stage.IsDeficit()
                ? "{0}% in deficit".Localized(d)
                : "{0}% in reserve".Localized(d);
        }
    }

    /// <summary>Right-hand projection: "Historically: lasts until reset" /
    /// "On average: lasts until reset" / "Projected empty in 2h 10m".
    ///
    /// The lasts-until-reset phrasing names its basis, because it is the one
    /// variant that can contradict the recent-trend indicator beside it. That
    /// indicator extrapolates the last few samples and can say the window runs
    /// out while this one, reading a whole-window average or a historical
    /// profile, says it lasts — both true about different spans, and read as a
    /// self-contradiction when each is stated bare. The other variants agree
    /// with the indicator whenever it is showing, so they are left alone.
    ///
    /// Linear reaches this too, so the label follows Basis rather than
    /// assuming the historical one.</summary>
    public string? EtaText
    {
        get
        {
            if (WillLastToReset)
            {
                return Basis == UsagePaceBasis.Historical
                    ? "Historically: lasts until reset".Localized()
                    : "On average: lasts until reset".Localized();
            }

            if (EtaSeconds is not { } eta)
            {
                return null;
            }

            var t = DurationText(eta);
            // DurationText is already localized, so compare against the same
            // localized token rather than the literal "now".
            return t == NowText
                ? "Projected empty now".Localized()
                : "Projected empty in {0}".Localized(t);
        }
    }

    /// <summary>Semantic key: bare "now" is too generic to safely use as a
    /// lookup key.</summary>
    public static string NowText => "duration.now".LocalizedKey("now");

    /// <summary>How long until <paramref name="reset"/>, under the countdown's
    /// own rounding: seconds are floored, then minutes are taken UP. Null once
    /// the reset has passed.
    ///
    /// Not inlined into <see cref="ResetText"/>, because a second caller names
    /// a window by the same span — the qualifier for a repeated card label in
    /// <c>AgentUsageQualifier</c>. <see cref="DurationText"/> alone rounds to
    /// the NEAREST minute, so deriving the span twice put "4h 59m" in a row's
    /// name beside "Resets in 5h" in the same row for the first half of every
    /// minute. One contract, one implementation.</summary>
    public static string? SpanText(DateTimeOffset reset, DateTimeOffset now)
    {
        var seconds = Math.Floor(UnixSeconds(reset) - UnixSeconds(now));
        if (seconds <= 0)
        {
            return null;
        }

        // Truncation toward zero is the ceiling here because seconds > 0,
        // matching Swift's Int((seconds + 59) / 60) on a positive Double.
        var minutes = (int)((seconds + 59) / 60);
        return DurationText(minutes * 60d);
    }

    /// <summary>Localized countdown matching the Rust <c>resetText</c> rounding
    /// contract, ported from Format.swift's resetText(for:now:). The payload's
    /// own <c>resetText</c> is a compatibility field the engine renders in
    /// English; deriving the visible text from the structured timestamp instead
    /// is what lets the quota card follow the selected UI language. Returns
    /// null when the timestamp will not parse, so provider metadata that is not
    /// a countdown keeps its own text.</summary>
    public static string? ResetText(string resetsAt, DateTimeOffset now)
    {
        if (ParseRfc3339(resetsAt) is not { } reset)
        {
            return null;
        }

        return SpanText(reset, now) is { } span
            ? "Resets in {0}".Localized(span)
            : "Resets now".Localized();
    }

    /// <summary>The countdown to show for a window: derived from the
    /// structured timestamp when it parses, and the engine's own text when it
    /// does not or when there is no timestamp at all.
    ///
    /// <para>One function because the precedence is three-way and was written
    /// out twice. The Agent-limits row had it complete; the Overview summary
    /// had only the first arm, so a window whose timestamp will not parse —
    /// supported, and what the engine's English compatibility field exists for
    /// — showed a countdown next to the bar and none in the summary above it.
    /// Two surfaces disagreeing about the same clock is the exact drift the
    /// summary was supposed to be immune to by reusing this file.</para>
    ///
    /// <para>Takes the two values rather than a window so the summary need not
    /// carry one, and so both callers cannot reach for different fields.</para>
    /// </summary>
    public static string? ResetTextOr(string? resetsAt, string? engineText, DateTimeOffset now) =>
        resetsAt is { } value ? ResetText(value, now) ?? engineText : engineText;


    public static string DurationText(double seconds)
    {
        var m = (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (m < 1) { return NowText; }
        if (m < 60) { return "{0}m".Localized(m); }
        var h = m / 60;
        var rem = m % 60;
        if (h < 24)
        {
            return rem > 0 ? "{0}h {1}m".Localized(h, rem) : "{0}h".Localized(h);
        }

        var days = h / 24;
        var hr = h % 24;
        return hr > 0 ? "{0}d {1}h".Localized(days, hr) : "{0}d".Localized(days);
    }

    /// <summary>Compute linear pace for a duration-ready v3 window, or null
    /// if it can't be derived yet.</summary>
    public static UsagePace? Compute(UsageWindow window, DateTimeOffset now) =>
        IsDurationReady(window.PaceStatus.State) ? ComputeCore(window, now) : null;

    /// <summary>Compute pace under the user's chosen mode:
    /// Off → null; Historical → backend projection for Available and local
    /// Linear while LearningHistory; Linear → exact-duration Linear for
    /// duration-ready states.</summary>
    public static UsagePace? Compute(UsageWindow window, PaceMode mode, DateTimeOffset now)
    {
        return mode switch
        {
            PaceMode.Off => null,
            PaceMode.Historical => window.PaceStatus.State switch
            {
                UsagePaceState.Available when window.HistoricalPace is { } historical =>
                    ComputeHistorical(window, historical, now),
                UsagePaceState.LearningHistory => ComputeCore(window, now),
                _ => null,
            },
            PaceMode.Linear when IsDurationReady(window.PaceStatus.State) =>
                ComputeCore(window, now),
            _ => null,
        };
    }

    /// <summary>Assemble display-only projection strings. A visible historical
    /// risk takes precedence over the generic lasts text.</summary>
    public static UsagePacePresentation Presentation(
        UsageWindow window, PaceMode mode, UsagePace pace)
    {
        var risk = mode == PaceMode.Historical ? RunOutRiskLabel(window, pace) : null;
        var eta = pace.WillLastToReset && risk is not null ? null : pace.EtaText;
        return new UsagePacePresentation(eta, risk);
    }

    /// <summary>Build the pure display data shared by quota rows. Pace is
    /// intentionally delegated to <see cref="Compute(UsageWindow, PaceMode,
    /// DateTimeOffset)"/> and <see cref="Presentation"/>.</summary>
    public static UsagePaceRowPresentation RowPresentation(
        UsageWindow window,
        PaceMode mode,
        bool asUsed,
        bool classic,
        DateTimeOffset now)
    {
        var used = Clamp(window.UsedPercent, 0, 100);
        var remaining = Clamp(window.RemainingPercent, 0, 100);
        var fill = asUsed ? used : remaining;
        var amount = FormatAmount(asUsed ? used : remaining, asUsed);

        if (classic || mode == PaceMode.Off)
        {
            return new UsagePaceRowPresentation(
                remaining, fill, amount, null, null, null, null, false);
        }

        var pace = Compute(window, mode, now);
        var projection = pace is null ? null : Presentation(window, mode, pace);
        var status = StatusText(window, mode);
        return new UsagePaceRowPresentation(
            remaining,
            fill,
            amount,
            JoinText(status, pace?.Label),
            projection is null ? null : JoinText(projection.EtaText, projection.RiskText),
            pace is null
                ? null
                : Clamp(asUsed ? pace.ExpectedUsedPercent : 100 - pace.ExpectedUsedPercent, 0, 100),
            pace?.ExpectedUsedPercent,
            pace?.IsHistoricalDeficit == true);
    }

    /// <summary>codexbar-style historical run-out risk, e.g.
    /// "≈ 30% run-out risk", or null. A supplied pace suppresses risk for a
    /// Linear result.</summary>
    public static string? RunOutRiskLabel(UsageWindow window, UsagePace? pace = null)
    {
        if (window.PaceStatus.State != UsagePaceState.Available ||
            pace?.Basis == UsagePaceBasis.Linear ||
            window.HistoricalPace?.RunOutProbability is not { } probability)
        {
            return null;
        }

        var pct = (int)Math.Round(Clamp(probability, 0, 1) * 100,
            MidpointRounding.AwayFromZero);
        return pct <= 0 ? null : "≈ {0}% run-out risk".Localized(pct);
    }

    // Each arm is a whole sentence including its " · ", so the separator never
    // has to survive a lookup; JoinText's separator is language-independent for
    // the same reason.
    private static string? StatusText(UsageWindow window, PaceMode mode) =>
        window.PaceStatus.State switch
        {
            UsagePaceState.LearningHistory when mode == PaceMode.Historical =>
                "Learning history · Linear estimate".Localized(),
            UsagePaceState.LearningHistory when mode == PaceMode.Linear =>
                "Linear".Localized(),
            UsagePaceState.LearningDuration => "Learning reset duration".Localized(),
            UsagePaceState.Available when mode == PaceMode.Linear => "Linear".Localized(),
            UsagePaceState.Available => null,
            UsagePaceState.LegacyMissing => "Pace unavailable · legacy data".Localized(),
            UsagePaceState.Unavailable => UnavailableStatusText(window.PaceStatus.Reason),
            _ => null,
        };

    private static string UnavailableStatusText(UsagePaceUnavailableReason? reason) =>
        reason switch
        {
            null => "Pace unavailable · unavailable reason".Localized(),
            UsagePaceUnavailableReason.WindowIdentity =>
                "Pace unavailable · unknown quota window".Localized(),
            UsagePaceUnavailableReason.MissingReset =>
                "Pace unavailable · missing reset".Localized(),
            UsagePaceUnavailableReason.InvalidEvidence =>
                "Pace unavailable · invalid quota data".Localized(),
            UsagePaceUnavailableReason.AccountScope =>
                "Pace unavailable · account identity unavailable".Localized(),
            UsagePaceUnavailableReason.StoreCapacity =>
                "Pace unavailable · history storage full".Localized(),
            UsagePaceUnavailableReason.History =>
                "Pace unavailable · history unavailable".Localized(),
            UsagePaceUnavailableReason.NonRecurring =>
                "Pace unavailable · non-recurring quota".Localized(),
            _ => "Pace unavailable · unavailable reason".Localized(),
        };

    private static string FormatAmount(double amount, bool asUsed)
    {
        var rounded = (int)Math.Round(amount, MidpointRounding.AwayFromZero);
        // Whole-sentence keys, not "{0}% " + a translated word: 剩餘/已用 lead
        // in Chinese, so a fragment lookup would render "57% 剩餘".
        return asUsed ? "{0}% used".Localized(rounded) : "{0}% left".Localized(rounded);
    }

    private static string? JoinText(params string?[] values)
    {
        var parts = values.Where(static value => !string.IsNullOrEmpty(value)).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }

    private static bool IsDurationReady(UsagePaceState state) =>
        state is UsagePaceState.LearningHistory or UsagePaceState.Available;

    private static UsagePace? ComputeHistorical(
        UsageWindow window, HistoricalPace historical, DateTimeOffset now)
    {
        // Historical data supplies the projection values, but a quota card
        // still needs a current reset boundary before showing a pace marker.
        if (Timing(window, now) is not { } timing)
        {
            return null;
        }

        var actual = Clamp(window.UsedPercent, 0, 100);
        if (timing.Elapsed == 0 && actual > 0)
        {
            return null;
        }

        var expected = Clamp(historical.ExpectedUsedPercent, 0, 100);
        var delta = actual - expected;
        return new UsagePace(
            StageFor(delta), UsagePaceBasis.Historical, delta, expected, actual,
            historical.EtaSeconds, historical.WillLastToReset);
    }

    private static UsagePace? ComputeCore(UsageWindow window, DateTimeOffset now)
    {
        if (Timing(window, now) is not { } timing)
        {
            return null;
        }

        var expected = Clamp(timing.Elapsed / timing.Duration * 100, 0, 100);
        var actual = Clamp(window.UsedPercent, 0, 100);
        if (timing.Elapsed == 0 && actual > 0)
        {
            return null;
        }

        var delta = actual - expected;

        double? etaSeconds = null;
        var willLastToReset = false;
        if (timing.Elapsed > 0 && actual > 0)
        {
            var rate = actual / timing.Elapsed; // percentage points per second
            if (rate > 0)
            {
                var remaining = Math.Max(0, 100 - actual);
                var candidate = remaining / rate;
                if (candidate >= timing.TimeUntilReset)
                {
                    willLastToReset = true;
                }
                else
                {
                    etaSeconds = candidate;
                }
            }
        }
        else if (timing.Elapsed > 0 && actual == 0)
        {
            willLastToReset = true;
        }

        return new UsagePace(
            StageFor(delta), UsagePaceBasis.Linear, delta, expected, actual,
            etaSeconds, willLastToReset);
    }

    /// <summary>The window's own bounds in epoch ms, for the Agent-limits
    /// Chart layout's sparkline gate (<see cref="LimitsChartFold.SparklineInterval"/>).
    /// <para>
    /// Same two inputs <see cref="Timing"/> already requires — <see cref="UsageWindow.ResetsAt"/>
    /// and <see cref="PaceStatus.DurationSeconds"/> — but returns absolute
    /// bounds instead of a duration/elapsed pair, and without <see cref="Timing"/>'s
    /// "already past reset" guard: a chart still wants to draw the window it
    /// is currently sampling even when clock skew put <c>now</c> a beat past
    /// <see cref="UsageWindow.ResetsAt"/>. Does not replicate macOS's
    /// <c>WindowCardLoader.resolution</c> fallback sources (contract/observed
    /// duration) — a window with neither a parseable reset nor a reported
    /// duration returns null here, same as it does for pace, and the row
    /// falls back to its bar.
    /// </para>
    /// </summary>
    public static (long StartMs, long EndMs)? WindowBoundsMs(UsageWindow window)
    {
        if (window.ResetsAt is not { } resetsAtRaw ||
            window.PaceStatus.DurationSeconds is not { } durationSeconds ||
            durationSeconds <= 0 ||
            ParseRfc3339(resetsAtRaw) is not { } resetsAt)
        {
            return null;
        }

        var endMs = resetsAt.ToUnixTimeMilliseconds();
        var startMs = endMs - (long)(durationSeconds * 1000);
        return startMs < endMs ? (startMs, endMs) : null;
    }

    private readonly record struct WindowTiming(
        double Duration,
        double TimeUntilReset,
        double Elapsed);

    private static WindowTiming? Timing(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } resetsAtRaw ||
            window.PaceStatus.DurationSeconds is not { } durationSeconds ||
            durationSeconds <= 0 ||
            ParseRfc3339(resetsAtRaw) is not { } resetsAt)
        {
            return null;
        }

        var duration = durationSeconds;
        var timeUntilReset = UnixSeconds(resetsAt) - UnixSeconds(now);
        if (timeUntilReset <= 0 || timeUntilReset > duration)
        {
            return null;
        }

        return new WindowTiming(
            duration,
            timeUntilReset,
            Clamp(duration - timeUntilReset, 0, duration));
    }

    private static double UnixSeconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.ToUnixTimeSeconds() +
            utc.Ticks % TimeSpan.TicksPerSecond / (double)TimeSpan.TicksPerSecond;
    }

    private static PaceStage StageFor(double delta)
    {
        var a = Math.Abs(delta);
        if (a <= 2) { return PaceStage.OnTrack; }
        if (a <= 6) { return delta >= 0 ? PaceStage.SlightlyAhead : PaceStage.SlightlyBehind; }
        if (a <= 12) { return delta >= 0 ? PaceStage.Ahead : PaceStage.Behind; }
        return delta >= 0 ? PaceStage.FarAhead : PaceStage.FarBehind;
    }

    private static readonly Regex Rfc3339Pattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d+))?(?<zone>[Zz]|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    /// <summary>Strict RFC3339 parser requiring an explicit zone while
    /// tolerating arbitrary fractional precision and lowercase z, matching
    /// the canonical Swift formatter's valid timestamp variants.</summary>
    internal static DateTimeOffset? ParseRfc3339(string s)
    {
        var match = Rfc3339Pattern.Match(s);
        if (!match.Success)
        {
            return null;
        }

        var fraction = match.Groups["fraction"].Value;
        if (fraction.Length > 3)
        {
            // Foundation's ISO8601DateFormatter keeps millisecond precision.
            fraction = fraction[..3];
        }

        var zone = match.Groups["zone"].Value;
        if (zone is "Z" or "z")
        {
            zone = "+00:00";
        }

        var normalized = match.Groups["date"].Value
            + (fraction.Length == 0 ? "" : $".{fraction}")
            + zone;
        var format = fraction.Length == 0
            ? "yyyy-MM-dd'T'HH:mm:sszzz"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz";
        return DateTimeOffset.TryParseExact(
            normalized, format, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static double Clamp(double v, double lo, double hi) =>
        Math.Min(hi, Math.Max(lo, v));
}

/// <summary>Display-only presentation assembled from one pace result and its
/// optional historical risk.</summary>
public sealed record UsagePacePresentation(string? EtaText, string? RiskText);

/// <summary>Pure display values for one quota row.</summary>
public sealed record UsagePaceRowPresentation(
    double RemainingPercent,
    double FillPercent,
    string AmountText,
    string? PaceText,
    string? ProjectionText,
    double? MarkerPercent,
    double? ExpectedUsedPercent,
    bool IsHistoricalDeficit);
