using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>Text color a limits-card label takes, named rather than a hex so
/// the decision stays in Core and the brush stays in the App.</summary>
public enum LimitsTone
{
    Secondary,
    Tertiary,
    Red,
    Green,
}

/// <summary>The per-client status badge on the right of a limits-card
/// header.</summary>
public sealed record LimitsBadge(string Text, LimitsTone Tone);

/// <summary>One row's recent-trend indicator: the arrow, read on the axis the
/// row shows, plus at most one short phrase.</summary>
public sealed record LimitsTrendLabel(QuotaTrendDirection Direction, string? Text, LimitsTone Tone);

/// <summary>Display decisions of macOS <c>AgentLimitsCard</c> (945dbcc2) that
/// the Windows card draws: the status badge (:963-992), the trend indicator
/// (:1185-1250) and its tooltip (:599-617). DashboardView compiles under no
/// test project, so each decision lives here.</summary>
public static class AgentLimitsText
{
    /// <summary>The badge key for a card waiting on the user, or null for any
    /// other card (macOS <c>AgentUsageSnapshot.setupBadgeKey</c>). Decided
    /// beside the source list so a new placeholder source cannot half-land:
    /// "Set up" is wrong for a login that exists and only needs
    /// authorizing.</summary>
    public static string? SetupBadgeKey(AgentUsageSnapshot snapshot) => snapshot.Source switch
    {
        "unconfigured" => "Set up",
        "keychain-consent" or "keychain-denied" => "Allow",
        _ => null,
    };

    /// <summary>Setup first: every placeholder state also carries an error,
    /// and <see cref="AgentUsageSnapshot.Source"/> is the only field that
    /// tells them apart.</summary>
    public static LimitsBadge StatusBadge(AgentUsageSnapshot? snapshot, bool isLive)
    {
        if (snapshot is not null && SetupBadgeKey(snapshot) is { } key)
        {
            return new(key.Localized(), LimitsTone.Secondary);
        }

        if (snapshot?.Error is not null)
        {
            return new("Error".Localized(), LimitsTone.Red);
        }

        if (snapshot is not null && snapshot.UniqueCardWindows.Count > 0)
        {
            // Backend-reported source ("oauth", "api", …): data, not copy.
            return new(snapshot.Source.ToUpperInvariant(), LimitsTone.Secondary);
        }

        return isLive
            ? new("Live".Localized(), LimitsTone.Green)
            : new("No quota".Localized(), LimitsTone.Secondary);
    }

    /// <summary>Clients whose live tail shows activity right now.</summary>
    public static IReadOnlySet<string> LiveClients(IReadOnlyList<TraceBucket>? trace) =>
        (trace ?? []).Where(static b => b.TokensPerMin > 0)
            .Select(b => NormalizeTraceClient(b.Client))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>macOS <c>normalizeTraceClient</c>: an explicit alias wins;
    /// otherwise a trailing <c>-cli</c> is dropped.</summary>
    public static string NormalizeTraceClient(string id)
    {
        var canonical = ClientRegistry.CanonicalClient(id);
        if (canonical != id)
        {
            return canonical;
        }

        return id.EndsWith("-cli", StringComparison.Ordinal) ? id[..^4] : id;
    }

    /// <summary>The recent-trend fold resolved against the window's own
    /// bounds; null for no duration, no parseable reset, or too few samples —
    /// never a fabricated zero.
    /// <para>Only while the window is running (start ≤ now &lt; reset), as
    /// macOS <c>WindowResolver.resolve</c> with no first-usage anchor: a reset
    /// already passed is <c>.idle</c> there, and here the fold would instead
    /// project over zero remaining time and draw a bare arrow for a window
    /// that has ended. Like macOS, there is no duration fallback: a window
    /// without <c>DurationSeconds</c> has no trend on either side.</para></summary>
    public static QuotaTrend? Trend(UsageWindow window, IReadOnlyList<QuotaSample>? samples, long nowMs) =>
        samples is { Count: > 0 }
            && UsagePace.WindowBoundsMs(window) is { } bounds
            && nowMs < bounds.EndMs
            ? QuotaTrendFold.Trend(window.UsedPercent, bounds.StartMs, bounds.EndMs, nowMs, samples)
            : null;

    /// <summary>The arrow and the delta it still costs, on the axis the row
    /// shows: on the remaining axis consuming 18 more points reads "18% less
    /// left", worded rather than signed because a minus beside "left" reads as
    /// a negative remainder. Past 100% projected the delta is larger than the
    /// axis has room for, so the state is named instead. Flat, or a delta that
    /// rounds to zero, shows the arrow alone.</summary>
    public static LimitsTrendLabel? TrendLabel(QuotaTrend? trend, bool asUsed)
    {
        if (trend is null)
        {
            return null;
        }

        var direction = asUsed ? trend.Direction : trend.Direction switch
        {
            QuotaTrendDirection.Rising => QuotaTrendDirection.Falling,
            QuotaTrendDirection.Falling => QuotaTrendDirection.Rising,
            _ => QuotaTrendDirection.Flat,
        };
        if (trend.RunsOutEarly)
        {
            return new(direction, "Recently: runs out".Localized(), LimitsTone.Red);
        }

        var tone = direction == QuotaTrendDirection.Flat ? LimitsTone.Tertiary : LimitsTone.Secondary;
        var rounded = (int)Math.Round(
            asUsed ? trend.ProjectedDeltaPercent : -trend.ProjectedDeltaPercent,
            MidpointRounding.AwayFromZero);
        if (direction == QuotaTrendDirection.Flat || rounded == 0)
        {
            return new(direction, null, tone);
        }

        var magnitude = $"{Math.Abs(rounded)}%";
        var text = asUsed
            ? "{0} used".Localized($"{(rounded > 0 ? "+" : "−")}{magnitude}")
            : (rounded < 0 ? "{0} less left" : "{0} more left").Localized(magnitude);
        return new(direction, text, tone);
    }

    /// <summary>Arrow glyph for a direction, read on the row's axis.</summary>
    public static string TrendGlyph(QuotaTrendDirection direction) => direction switch
    {
        QuotaTrendDirection.Rising => "↗",
        QuotaTrendDirection.Falling => "↘",
        _ => "→",
    };

    /// <summary>The hover text. Pace and this trend answer different
    /// questions (pace: the level against the usual pattern; trend: the
    /// current slope), and they disagreed on 3 of 7 live windows with both
    /// right, so the tooltip says so.</summary>
    public static string TrendTooltip(QuotaTrend trend)
    {
        var projected = (int)Math.Round(trend.ProjectedUsedPercent, MidpointRounding.AwayFromZero);
        var line = trend.ProjectedUsedPercent > 100
            ? "At this rate it runs out before reset · projected {0}% used".Localized(projected)
            : "At this rate it reaches {0}% used by reset".Localized(projected);
        return string.Join('\n',
            "Recent consumption".Localized(),
            line,
            "The pace line beside it compares you with your usual pattern instead.".Localized());
    }
}
