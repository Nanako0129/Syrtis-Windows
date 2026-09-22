using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Text composition for the Overview quota summary card (port of the
/// string-building half of TokenBarCore/QuotaSummaryLine.swift; the WinUI
/// grid layout lives in DashboardView.xaml.cs since WinUI elements cannot be
/// unit tested from TokenBar.Core.Tests). Pulled into TokenBar.Core.Tests via
/// &lt;Compile Include&gt;, the same way DrillDownSummary is.</summary>
/// <summary>Which of the card's three empty states applies. summary == null
/// alone cannot tell "still asking" from "asked and nothing reported a
/// window" — Attempted, from whether the quota lane has produced a payload
/// yet, is what tells them apart.</summary>
/// <summary>The card's second row: the burn warning, the reassurance that
/// replaces it when nothing is burning, or neither.</summary>
public enum QuotaSummarySecondRow
{
    Burning,
    Reassurance,
    None,
}

public enum QuotaSummaryState
{
    Ready,
    /// <summary>The payload is healthy and the user has hidden every candidate.
    /// The card is suppressed entirely rather than explained: macOS's own rule
    /// is that a card absent because the user hid its subject needs no
    /// placeholder, and telling someone "nothing is reporting" when they chose
    /// the silence blames the provider for their setting.</summary>
    AllHidden,
    NoWindowReporting,
    Loading,

    /// <summary>Asked, and the most recent agent-usage fetch threw. Distinct
    /// from <see cref="NoWindowReporting"/> ("asked and nothing reported a
    /// window") and from <see cref="Loading"/> ("still waiting for a first
    /// answer") — collapsing a failed read into either states something false
    /// about a request that never landed.</summary>
    Failed,
}

/// <summary>The Agent-limits card's three states — same shape as
/// <see cref="QuotaSummaryState"/>, one payload and one gate simpler because
/// this card has no all-hidden case of its own.</summary>
public enum AgentLimitsState
{
    Ready,
    Loading,
    Failed,
}

public static class QuotaSummaryText
{
    /// <summary>Which of the Agent-limits card's three states applies, given
    /// whether it currently has a payload to draw from (<paramref name="hasAgents"/>
    /// — the agent list after any per-client narrowing, since narrowing can
    /// turn a non-empty snapshot into nothing to show for THIS client) and the
    /// most recent quota fetch's own outcome.
    /// <para>
    /// <paramref name="hasAgents"/> is checked FIRST, before <paramref name="outcome"/>
    /// — retained data wins. a3ea946 got this backwards: it checked
    /// <c>outcome == Failed</c> before checking whether data existed, so a
    /// failed retry after an earlier success replaced a populated card with a
    /// failure message even though the retained payload (DashboardModel's
    /// quota lane does not null <c>Quota</c> out on a failed refetch) was
    /// still good — disagreeing with this same file's own <see cref="State"/>
    /// above, which already checks <c>summary is not null</c> first for
    /// exactly this reason, on the very same lane. This restores that order:
    /// <c>Failed</c> now only replaces an EMPTY card, never a populated one.
    /// </para>
    /// <para>
    /// ponytail: no staleness indicator. A card showing retained data after a
    /// failed refetch carries no marker that the read behind it just failed —
    /// preferring stale-but-present data over destroying it with an error was
    /// the ask; a "last updated"/staleness UI was deliberately not built (no
    /// macOS reference for it). Add one if this becomes a real complaint.
    /// </para></summary>
    public static AgentLimitsState LimitsState(bool hasAgents, WindowEquivalence.FetchOutcome outcome) =>
        hasAgents ? AgentLimitsState.Ready
        : outcome == WindowEquivalence.FetchOutcome.Failed ? AgentLimitsState.Failed
        : AgentLimitsState.Loading;

    /// <summary>Which of five things the card is looking at.
    ///
    /// <para><paramref name="outcome"/> must be a fact about the request, not
    /// about the result. Derived from "a payload exists" it cannot separate
    /// "still asking" from "asked and it failed" from "asked and it threw" —
    /// a failed fetch publishes no payload either, so a bool-shaped signal
    /// left the loading line on screen for a request that had already come
    /// back with an error, indistinguishable from one that came back empty.
    /// Three-value for the same reason <c>QuotaLensText.HeatmapState</c> and
    /// <c>StripState</c> are: <c>NotAttempted</c>/<c>Succeeded</c>/<c>Failed</c>,
    /// not a bool plus an inferred result.</para>
    ///
    /// <para><paramref name="allHidden"/> is checked before the reporting
    /// state, because a fold that returns null after every candidate was
    /// excluded looks exactly like one that returned null because nothing
    /// reported.</para></summary>
    public static QuotaSummaryState State(QuotaSummary? summary, WindowEquivalence.FetchOutcome outcome, bool allHidden) =>
        summary is not null ? QuotaSummaryState.Ready
        : allHidden ? QuotaSummaryState.AllHidden
        : outcome switch
        {
            WindowEquivalence.FetchOutcome.Succeeded => QuotaSummaryState.NoWindowReporting,
            WindowEquivalence.FetchOutcome.Failed => QuotaSummaryState.Failed,
            _ => QuotaSummaryState.Loading,
        };


    /// <summary>Who a window belongs to, qualified by account when the
    /// client has more than one. Windows has no AccountIdentity/account-label
    /// surface yet (no multi-account support), so accountKey is always null
    /// today and this degrades to the bare client name; once accountKey stops
    /// being null this is where the qualified label is composed.</summary>
    private static string AccountQualifiedName(string clientId, string? accountKey)
    {
        var name = ClientRegistry.Style(clientId).DisplayName;
        return accountKey is null ? name : $"{name} {accountKey}";
    }

    public static string TightestName(QuotaSummary summary) =>
        AccountQualifiedName(summary.TightestClient, summary.TightestAccountKey);

    public static string BurnName(BurnWarning burning) =>
        AccountQualifiedName(burning.ClientId, burning.AccountKey);

    public static string TightestHeadline(QuotaSummary summary) =>
        $"{TightestName(summary)} · {summary.TightestLabel.Localized()}";

    /// <summary>"<paramref name="summary"/>% left · <reset>". Reset comes
    /// from UsagePace.ResetText, the same helper the quota cards use, so the
    /// countdown here cannot drift from the one shown next to the bar.</summary>
    public static string TightestDetail(QuotaSummary summary, DateTimeOffset now)
    {
        var left = "{0}% left".Localized(
            (int)Math.Round(summary.RemainingPercent, MidpointRounding.AwayFromZero));
        var reset = UsagePace.ResetTextOr(summary.ResetsAt, summary.ResetTextFallback, now);
        return reset is null ? left : $"{left} · {reset}";
    }

    /// <summary>"N other windows, all at or above X%" or "N of M other windows are
    /// below X%" — two distinct forms, never rendered the same, per
    /// QuotaSummary.OthersComfortable's doc comment. Caller must check
    /// summary.OtherWindows &gt; 0 first.</summary>
    public static string OthersText(QuotaSummary summary) =>
        summary.AllOthersComfortable
            // "at or above", not "above": the fold counts a window as
            // comfortable at v >= ComfortablePercent, and quota percentages
            // land on whole numbers often enough that the boundary is a case
            // rather than a curiosity. QuotaSummary.OthersComfortable's own
            // comment says "at or above"; this sentence had drifted from the
            // record it describes, one file away. macOS carries the same
            // imprecision — its predicate is >= and its string says "above" —
            // so this is a deliberate divergence, not a port error.
            ? "{0} other windows, all at or above {1}%".Localized(
                summary.OtherWindows, (int)QuotaSummaryFold.ComfortablePercent)
            : "{0} of {1} other windows are below {2}%".Localized(
                summary.OtherWindows - summary.OthersComfortable,
                summary.OtherWindows,
                (int)QuotaSummaryFold.ComfortablePercent);

    /// <summary>"Every measured window is under its expected pace" — gated
    /// by the caller on PaceCheckedWindows &gt; 0. "Every measured", never
    /// "every": with four windows and one measurable, the wider wording
    /// would vouch for three nobody looked at.</summary>
    public static string PaceReassurance() => "Every measured window is under its expected pace".Localized();

    /// <summary>Which second row the card shows, decided here rather than in
    /// the view.
    ///
    /// <para>The rule has three arms and only one of them is obvious. A burn
    /// warning <b>replaces</b> the reassurance rather than joining it: a slot
    /// that is empty whenever nothing is wrong teaches the reader to ignore
    /// it, and on real data nothing is wrong nearly always. The reassurance is
    /// then gated on <c>PaceCheckedWindows</c> as well as on there being other
    /// windows, because with pace off or the historical basis still learning
    /// <c>Compute</c> returns null everywhere and <c>Burning</c> is null for
    /// want of asking, not for want of anything to find — printing it there
    /// vouches for a check that never ran.</para>
    ///
    /// <para>This lives in the text layer because the view lives in
    /// DashboardView, which no test project compiles. Left inline there, "the
    /// reassurance appears when nothing is burning and something was measured"
    /// was a property nothing could assert, and the only way to observe it was
    /// to catch the live data in that state.</para></summary>
    public static QuotaSummarySecondRow SecondRow(QuotaSummary summary) =>
        summary.Burning is not null ? QuotaSummarySecondRow.Burning
        : summary.OtherWindows > 0 && summary.PaceCheckedWindows > 0
            ? QuotaSummarySecondRow.Reassurance
            : QuotaSummarySecondRow.None;

    public static string BurnHeadline(BurnWarning burning) =>
        $"{BurnName(burning)} · {burning.Label.Localized()}";

    /// <summary>"N% ahead of pace · <risk-or-eta>". Risk text takes
    /// precedence over the ETA the same way UsagePace.Presentation
    /// prioritizes it for the Agent-limits card.</summary>
    public static string BurnDetail(BurnWarning burning)
    {
        var ahead = "{0}% ahead of pace".Localized(
            (int)Math.Round(burning.AheadPercent, MidpointRounding.AwayFromZero));
        var tail = burning.RiskText ?? burning.EtaText;
        return tail is null ? ahead : $"{ahead} · {tail}";
    }

    /// <summary>"1.2M tokens · $3.40". The cost half goes through the
    /// token-aware <c>CostSurfaceProjection.CostText</c>, so a day whose usage
    /// all failed to price reads "—" beside its tokens.
    /// <para>
    /// This comment used to claim such a day reads "Checking" instead of a
    /// fabricated "$0.00". That held only when the WHOLE graph had
    /// <c>CostCoverage.None</c>: authority is graph-level, and with anything
    /// else priced it is true, so the day read "$0.00" — exactly what the
    /// comment said it prevented. "Checking" still appears, but only while
    /// nothing has priced at all; the per-day answer comes from the tokens.
    /// </para></summary>
    public static string TodayText(long tokens, double cost, bool authoritative) =>
        "{0} tokens · {1}".Localized(
            Format.CompactTokens(tokens),
            CostSurfaceProjection.CostText(tokens, cost, authoritative));

    public static string NoWindowReporting() =>
        "No subscription is reporting a usage window right now.".Localized();

    public static string CheckingLimits() => "Checking agent limits…".Localized();

    /// <summary>Own wording rather than reusing <c>QuotaLensText.Failed</c>'s
    /// "Quota history could not be read" — that string names the store lane
    /// (quota-history, a persisted export), and this card's fetch is the
    /// agent-usage/limits lane instead (the same one <see cref="CheckingLimits"/>
    /// names above); the two are separate fetches with separate failure
    /// causes, and this card should not claim the wrong one broke.</summary>
    public static string CouldNotCheckLimits() => "Could not check agent limits. It will be retried.".Localized();
}
