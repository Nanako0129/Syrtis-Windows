namespace TokenBar.Core;

// Port of AgentLimitsCard.swift's `LimitsLayout` (:13-15) and its
// `sparklineInterval` gate (:142-151). `Chart` is a third card density,
// alongside `Full` and `Classic`: it draws a window row's quota on a time
// axis instead of a fill bar, but only when the window has enough recorded
// history to place a curve on it — a bar is read at a glance and a curve is
// not, so this is a density choice, not an upgrade every row gets for free.

/// <summary>Card density for the Agent-limits card's window rows.</summary>
public enum LimitsLayout
{
    Full,
    Classic,
    Chart,
}

public static class LimitsChartFold
{
    /// <summary>Decodes the persisted <c>"tokenbar.limits.layout"</c> string.
    /// An unrecognised or absent value falls back to <see cref="LimitsLayout.Full"/>
    /// — the store's own prior default, and what every earlier build already
    /// wrote for a reader who never opened the layout radio.</summary>
    public static LimitsLayout ParseLayout(string? raw) => raw switch
    {
        "classic" => LimitsLayout.Classic,
        "chart" => LimitsLayout.Chart,
        _ => LimitsLayout.Full,
    };

    /// <summary>Below this many samples inside the drawn interval, a curve
    /// would either not exist or draw a flat guess at whatever the one
    /// reading said. Same floor as macOS's <c>sparklineInterval</c> (:142-151).
    /// </summary>
    public const int MinimumSamples = 2;

    /// <summary>
    /// Whether a window row can draw a curve instead of a bar, and over what
    /// interval.
    /// <para>
    /// Port of <c>AgentLimitsCard.sparklineInterval</c> (:142-151). Two
    /// readings must fall INSIDE <c>[windowStartMs, nowMs]</c>, not merely
    /// exist on file — a series whose points all predate this window would
    /// otherwise draw a flat line at whatever the last one said, which is a
    /// confident claim about a window nobody has sampled yet.
    /// </para>
    /// <para>
    /// Null whenever the window itself cannot be placed on a clock
    /// (<paramref name="windowStartMs"/> or <paramref name="windowEndMs"/>
    /// absent, or the interval is empty/inverted) — the same failure case
    /// macOS's <c>WindowCardLoader.interval</c> answers with <c>nil</c>.
    /// </para>
    /// </summary>
    public static (long Start, long End)? SparklineInterval(
        long? windowStartMs, long? windowEndMs, long nowMs, IReadOnlyList<QuotaSample> samples)
    {
        if (windowStartMs is not { } start || windowEndMs is not { } end || end <= start)
        {
            return null;
        }

        var inside = samples.Count(sample => sample.AtMs >= start && sample.AtMs <= nowMs);
        return inside >= MinimumSamples ? (start, end) : null;
    }
}
