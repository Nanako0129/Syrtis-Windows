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

/// <summary>The line under a limits-card header.</summary>
public sealed record LimitsDetail(string Text, bool IsError);

/// <summary>Card order on the multi-client Agent-limits card (macOS
/// <c>AgentLimitsCard.visibleClients</c> :471-491 and the drag's
/// <c>onEnded</c> :693-706). The order lives in
/// <see cref="ClientRegistry.TabOrderKey"/>, shared with the client tab row,
/// so dragging a card also moves its tab and the other way round.</summary>
public static class LimitsCardOrder
{
    /// <summary>Primary cards sorted by the saved order (unsaved ids keep
    /// their payload order at the end); each primary's extra accounts follow
    /// it; an extra whose primary is absent (hidden) keeps its relative place
    /// at the end. Extra accounts are never part of the saved order.</summary>
    public static IReadOnlyList<AgentUsageSnapshot> Apply(
        IReadOnlyList<AgentUsageSnapshot> agents, string orderRaw)
    {
        // A list for the payload order (Dictionary enumeration order is not
        // a contract), a dictionary for lookup. One primary per client.
        var primaryList = agents.Where(static a => a.Account.AccountKey is null)
            .DistinctBy(static a => a.ClientId)
            .ToList();
        var primaries = primaryList.ToDictionary(static a => a.ClientId);
        var ordered = ClientRegistry.OrderedClients([.. primaryList.Select(static a => a.ClientId)], orderRaw);
        var output = new List<AgentUsageSnapshot>(agents.Count);
        foreach (var id in ordered)
        {
            output.Add(primaries[id]);
            output.AddRange(agents.Where(a => a.ClientId == id && a.Account.AccountKey is not null));
        }

        output.AddRange(agents.Where(a => a.Account.AccountKey is not null && !primaries.ContainsKey(a.ClientId)));
        return output;
    }

    /// <summary>The order to save after dropping <paramref name="from"/> on
    /// <paramref name="to"/>. <paramref name="visible"/> is the on-screen
    /// primary order; ids off screen (hidden tabs, clients with no quota) keep
    /// their saved slots instead of falling out of the key.</summary>
    public static string Dropped(string orderRaw, IReadOnlyList<string> visible, string from, string to) =>
        string.Join(',', ClientRegistry.MergeReorder(ClientRegistry.ParseIdList(orderRaw), visible, from, to));

    /// <summary>One card group's vertical extent while dragging.</summary>
    public readonly record struct Span(string Id, double Top, double Bottom);

    /// <summary>The group a release at <paramref name="y"/> drops onto (the
    /// dragged one itself means "no move"). The drop line is drawn in the gap
    /// on the side the card moves toward — under the target when dragging
    /// down, over it when dragging up — so each gap belongs to the group
    /// whose line it shows: below the dragged group's top, the last group
    /// whose top is at or above the pointer; above it, the first group whose
    /// bottom is at or below it. Every point maps to a group, so there is no
    /// dead zone where the line vanishes and a release silently does
    /// nothing.</summary>
    public static string DropTarget(IReadOnlyList<Span> spans, string dragged, double y)
    {
        var ordered = spans.OrderBy(static span => span.Top).ToList();
        var from = ordered.First(span => span.Id == dragged);
        // Never empty: the dragged group satisfies whichever side applies.
        return y >= from.Top
            ? ordered.Last(span => span.Top <= y).Id
            : ordered.First(span => span.Bottom >= y).Id;
    }

    /// <summary>Whether the drop line sits under the target (dragging down)
    /// rather than over it — the direction-aware insert
    /// <see cref="ClientRegistry.Reorder"/> performs.</summary>
    public static bool DropsBelow(IReadOnlyList<string> visible, string from, string to)
    {
        List<string> list = [.. visible];
        return list.IndexOf(from) < list.IndexOf(to);
    }
}

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

    /// <summary>The line under a client header (macOS <c>detailText</c>
    /// :994-999): the error when there is one, drawn red; otherwise
    /// "email · plan" with whichever part is present. Email belongs on this
    /// card only — <see cref="AccountLabel"/> and the tray never carry
    /// it.</summary>
    public static LimitsDetail? Detail(AgentUsageSnapshot snapshot)
    {
        if (snapshot.Error is { } error)
        {
            return new(error, IsError: true);
        }

        var parts = new[] { snapshot.Identity?.Email, snapshot.Identity?.Plan }.OfType<string>().ToList();
        return parts.Count == 0 ? null : new(string.Join(" · ", parts), IsError: false);
    }

    /// <summary>The windows a card draws as bars, whether or not it carries
    /// an error (macOS AgentLimitsCard.swift:771-787 draws the red detail and
    /// then <c>uniqueWindows</c>). A transient failure after a success comes
    /// back from the core as the last-good snapshot with the error stamped on
    /// it (agent_usage.rs <c>apply_account_outcome_with</c>), so its bars stay
    /// under the red line; a failure with nothing cached has no windows and
    /// draws only the red line.</summary>
    public static IReadOnlyList<UsageWindow> BarWindows(AgentUsageSnapshot snapshot) =>
        snapshot.UniqueCardWindows;

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
