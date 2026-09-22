using TokenBar.App;
using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// The Overview quota summary card's string composition (port of the display
// half of TokenBarCore/QuotaSummaryLine.swift). DashboardView.xaml.cs is
// WinUI and no test project compiles it, so this — and the layout it feeds —
// is where these strings are asserted.
public class QuotaSummaryTextTests
{
    public QuotaSummaryTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    // `withoutResetsAt` rather than letting `resetsAt: null` mean it: the
    // helper defaults an unspecified timestamp to a live one, so `?? default`
    // collapsed "not specified" and "explicitly absent" into the same value and
    // the first two reset-precedence tests below silently exercised neither
    // case. Same shape as the argument defect this suite already pins — a
    // signal that cannot carry the distinction being asked of it.
    private static QuotaSummary Summary(
        int otherWindows = 0, int othersComfortable = 0,
        BurnWarning? burning = null, int paceChecked = 0, string? resetsAt = null,
        string? resetTextFallback = null, bool withoutResetsAt = false) =>
        new(
            TightestClient: "claude",
            TightestAccountKey: null,
            TightestLabel: "Weekly",
            RemainingPercent: 42,
            ResetsAt: withoutResetsAt
                ? null
                : resetsAt ?? Now.AddSeconds(2 * 86_400 + 2 * 3_600).UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ResetTextFallback: resetTextFallback,
            OtherWindows: otherWindows,
            OthersComfortable: othersComfortable,
            Burning: burning,
            PaceCheckedWindows: paceChecked);

    // 7. Every state is distinct — a fetch that has not returned must never
    // render the same as one that returned and found nothing, and a fetch
    // that threw must never render the same as either.
    //
    // This was written as "three states", then "four": review found first
    // that a payload hidden entirely by the user arrives as the same null
    // summary as one nothing reported, then that a failed fetch arrives as
    // the same null payload as one still in flight, and finally — the
    // agent-usage lane conversion — that a failed fetch and one that landed
    // empty both used to arrive as the same bool `attempted: true`. Every
    // collapse says something untrue: the first blames the provider for the
    // user's setting, the second waits forever for an answer already
    // returned, the third tells the reader nothing reported when the actual
    // fact is that the read itself broke.
    [Fact]
    public void EveryEmptyStateIsDistinct()
    {
        var states = new[]
        {
            QuotaSummaryText.State(Summary(), outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: false),
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: true),
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: false),
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.NotAttempted, allHidden: false),
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Failed, allHidden: false),
        };

        Assert.Equal(
            [
                QuotaSummaryState.Ready,
                QuotaSummaryState.AllHidden,
                QuotaSummaryState.NoWindowReporting,
                QuotaSummaryState.Loading,
                QuotaSummaryState.Failed,
            ],
            states);
        Assert.Equal(states.Length, states.Distinct().Count());
        // Every declared state is reachable: one that no input produces is a
        // branch the view carries and nothing can enter.
        Assert.Empty(Enum.GetValues<QuotaSummaryState>().Except(states));
    }

    // A summary outranks everything: having an answer is not affected by
    // whether the request has formally completed.
    [Fact]
    public void ASummaryIsReadyWhateverTheAttemptSays() =>
        Assert.Equal(
            QuotaSummaryState.Ready,
            QuotaSummaryText.State(Summary(), outcome: WindowEquivalence.FetchOutcome.NotAttempted, allHidden: false));

    [Fact]
    public void TightestHeadlineNamesClientAndWindow() =>
        Assert.Equal("Claude Code · Weekly", QuotaSummaryText.TightestHeadline(Summary()));

    // Reset text is produced by UsagePace.ResetText, the same helper the
    // quota cards use — asserting the exact composed string here is what
    // pins that reuse rather than a re-derived countdown.
    [Fact]
    public void TightestDetailUsesUsagePaceResetText()
    {
        var summary = Summary();
        var expectedReset = UsagePace.ResetText(summary.ResetsAt!, Now);
        Assert.Equal($"42% left · {expectedReset}", QuotaSummaryText.TightestDetail(summary, Now));
    }

    [Fact]
    public void TightestDetailOmitsResetWhenUnparseable()
    {
        var summary = Summary(resetsAt: "not-a-timestamp");
        Assert.Equal("42% left", QuotaSummaryText.TightestDetail(summary, Now));
    }

    // 5. othersText: two distinct shapes, never rendered the same.
    [Fact]
    public void OthersTextAllComfortable() =>
        Assert.Equal(
            "4 other windows, all at or above 60%",
            QuotaSummaryText.OthersText(Summary(otherWindows: 4, othersComfortable: 4)));

    [Fact]
    public void OthersTextSomeBelowThreshold() =>
        Assert.Equal(
            "2 of 14 other windows are below 60%",
            QuotaSummaryText.OthersText(Summary(otherWindows: 14, othersComfortable: 12)));

    [Fact]
    public void BurnHeadlineAndDetailPreferRiskOverEta()
    {
        var burn = new BurnWarning("codex", null, "Session", 18.4, "Projected empty in 2h", "≈ 30% run-out risk");
        Assert.Equal("Codex · Session", QuotaSummaryText.BurnHeadline(burn));
        Assert.Equal("18% ahead of pace · ≈ 30% run-out risk", QuotaSummaryText.BurnDetail(burn));
    }

    [Fact]
    public void BurnDetailFallsBackToEtaWithoutRisk()
    {
        var burn = new BurnWarning("codex", null, "Session", 18.4, "Projected empty in 2h", null);
        Assert.Equal("18% ahead of pace · Projected empty in 2h", QuotaSummaryText.BurnDetail(burn));
    }

    // 6. Today's money goes through the tokens-and-cost formatter: an
    // unauthoritative day must read "Checking", never a fabricated "$0.00".
    [Fact]
    public void TodayTextShowsCheckingWhenCostIsNotAuthoritative() =>
        Assert.Equal(
            "0 tokens · Checking", QuotaSummaryText.TodayText(0, 0, authoritative: false));

    [Fact]
    public void TodayTextShowsMoneyWhenAuthoritative() =>
        Assert.Equal(
            "1.2M tokens · $3.40", QuotaSummaryText.TodayText(1_234_000, 3.4, authoritative: true));

    // ---- i18n ------------------------------------------------------------

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

    [Fact]
    public void EveryStringIsTranslated() => InChinese(() =>
    {
        var summary = Summary();
        Assert.Equal("Claude Code · 每週", QuotaSummaryText.TightestHeadline(summary));
        Assert.Equal("剩餘 42% · 2天2小時 後重置", QuotaSummaryText.TightestDetail(summary, Now));
        Assert.Equal(
            "另外 14 個時間窗有 2 個低於 60%",
            QuotaSummaryText.OthersText(Summary(otherWindows: 14, othersComfortable: 12)));
        Assert.Equal("已測量的時間窗都在預期步調之內", QuotaSummaryText.PaceReassurance());
        Assert.Equal("目前沒有訂閱回報使用時間窗。", QuotaSummaryText.NoWindowReporting());
        Assert.Equal("正在查詢 Agent 額度…", QuotaSummaryText.CheckingLimits());
        Assert.Equal("無法查詢 Agent 額度，稍後會重試。", QuotaSummaryText.CouldNotCheckLimits());
    });

    // --- SecondRow ---------------------------------------------------------
    //
    // Added after the reassurance row went missing on a real machine and could
    // not be explained: the counts said it should render, the row helper was
    // sound, the translation was present, and the live data flipped to a burn
    // warning before the question could be settled. The decision was inline in
    // the view, which no test compiles, so "it appears when nothing is burning
    // and something was measured" was a property only observable by catching
    // the data in that state.

    [Fact]
    public void SecondRowIsTheBurnWarningWhenSomethingIsBurning() =>
        Assert.Equal(
            QuotaSummarySecondRow.Burning,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 5, burning: true)));

    // The burn warning REPLACES the reassurance; they never both show.
    [Fact]
    public void BurnWarningWinsEvenWhenTheReassuranceWouldAlsoQualify() =>
        Assert.NotEqual(
            QuotaSummarySecondRow.Reassurance,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 5, burning: true)));

    [Fact]
    public void SecondRowIsTheReassuranceWhenNothingIsBurningAndSomethingWasMeasured() =>
        Assert.Equal(
            QuotaSummarySecondRow.Reassurance,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 5, burning: false)));

    // Nothing measured: Burning is null for want of asking, not for want of
    // anything to find, so the reassurance would vouch for a check never run.
    [Fact]
    public void NothingIsShownWhenNoWindowWasMeasured() =>
        Assert.Equal(
            QuotaSummarySecondRow.None,
            QuotaSummaryText.SecondRow(SummaryWith(others: 7, paceChecked: 0, burning: false)));

    // One window and nothing to compare it to.
    [Fact]
    public void NothingIsShownWhenThereAreNoOtherWindows() =>
        Assert.Equal(
            QuotaSummarySecondRow.None,
            QuotaSummaryText.SecondRow(SummaryWith(others: 0, paceChecked: 5, burning: false)));

    private static QuotaSummary SummaryWith(int others, int paceChecked, bool burning) =>
        new(
            TightestClient: "claude",
            TightestAccountKey: null,
            TightestLabel: "Weekly",
            RemainingPercent: 41,
            ResetsAt: null,
            ResetTextFallback: null,
            OtherWindows: others,
            OthersComfortable: others,
            Burning: burning
                ? new BurnWarning("codex", null, "Weekly", 12, null, null)
                : null,
            PaceCheckedWindows: paceChecked);

    // --- State -------------------------------------------------------------
    //
    // Four states, and the two that were collapsed are the ones that mislead.
    // A failed fetch publishes completion with no payload, so deriving
    // "attempted" from the payload left the loading line on screen for a
    // request that had already come back. And a fold returning null because
    // the user hid every client looks identical to one returning null because
    // nothing reported — which blames the provider for the user's setting.

    [Fact]
    public void StateIsReadyWhenThereIsASummary() =>
        Assert.Equal(
            QuotaSummaryState.Ready,
            QuotaSummaryText.State(SummaryWith(others: 1, paceChecked: 1, burning: false),
                outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: false));

    [Fact]
    public void StateIsLoadingBeforeTheFirstAttemptCompletes() =>
        Assert.Equal(
            QuotaSummaryState.Loading,
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.NotAttempted, allHidden: false));

    // The case that was indistinguishable: the fetch finished and produced
    // nothing. Rendered as Loading, this waited forever.
    [Fact]
    public void StateIsNoWindowReportingAfterASuccessfulEmptyAttempt() =>
        Assert.Equal(
            QuotaSummaryState.NoWindowReporting,
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: false));

    // The defect this lane conversion closes: a failed agent-usage fetch used
    // to publish `QuotaAttempted = true` with no way to say the read itself
    // broke, so it rendered identically to NoWindowReporting above.
    [Fact]
    public void StateIsFailedAfterAFailedAttempt() =>
        Assert.Equal(
            QuotaSummaryState.Failed,
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Failed, allHidden: false));

    // The regression a3ea946 introduced and this pins: a Failed outcome must
    // not replace a card that still has data to show. Retained data wins —
    // the Agent-limits card must agree with State's own `summary is not
    // null` precedence above, on the same lane.
    [Fact]
    public void LimitsStateIsReadyOnAFailedOutcomeWhenDataIsRetained() =>
        Assert.Equal(
            AgentLimitsState.Ready,
            QuotaSummaryText.LimitsState(hasAgents: true, outcome: WindowEquivalence.FetchOutcome.Failed));

    // The other half of the same rule: Failed only wins when there is
    // nothing to show at all.
    [Fact]
    public void LimitsStateIsFailedOnAFailedOutcomeWithNoRetainedData() =>
        Assert.Equal(
            AgentLimitsState.Failed,
            QuotaSummaryText.LimitsState(hasAgents: false, outcome: WindowEquivalence.FetchOutcome.Failed));

    [Fact]
    public void StateIsAllHiddenWhenEveryCandidateIsExcluded() =>
        Assert.Equal(
            QuotaSummaryState.AllHidden,
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: true));

    // AllHidden outranks the reporting state: both arrive as a null summary,
    // and only one of them is the provider's doing.
    [Fact]
    public void AllHiddenIsNotReportedAsNothingReporting() =>
        Assert.NotEqual(
            QuotaSummaryState.NoWindowReporting,
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.Succeeded, allHidden: true));

    // Hidden before the first answer is still hidden — the user's choice does
    // not become visible just because a fetch is outstanding.
    [Fact]
    public void AllHiddenOutranksLoading() =>
        Assert.Equal(
            QuotaSummaryState.AllHidden,
            QuotaSummaryText.State(null, outcome: WindowEquivalence.FetchOutcome.NotAttempted, allHidden: true));

    // --- reset text precedence --------------------------------------------
    //
    // Three-way, and it was written out twice: the Agent-limits row had it
    // complete, this had only the first arm. A window whose timestamp will not
    // parse — supported, and what the engine's English compatibility field is
    // for — showed a countdown beside the bar and none in the summary directly
    // above it. UsagePace.ResetTextOr is now the only statement of the rule.

    [Fact]
    public void DetailFallsBackToTheEngineTextWhenThereIsNoTimestamp()
    {
        var detail = QuotaSummaryText.TightestDetail(
            Summary(withoutResetsAt: true, resetTextFallback: "resets in 3h"), Now);

        Assert.Contains("resets in 3h", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailFallsBackToTheEngineTextWhenTheTimestampWillNotParse()
    {
        var detail = QuotaSummaryText.TightestDetail(
            Summary(resetsAt: "not-a-timestamp", resetTextFallback: "resets in 3h"), Now);

        Assert.Contains("resets in 3h", detail, StringComparison.Ordinal);
    }

    // The derived countdown wins when it can be derived: it follows the UI
    // language, the engine's field is English only.
    [Fact]
    public void DetailPrefersTheDerivedCountdownOverTheEngineText()
    {
        var detail = QuotaSummaryText.TightestDetail(
            Summary(resetTextFallback: "resets in 3h"), Now);

        Assert.DoesNotContain("resets in 3h", detail, StringComparison.Ordinal);
    }

    // Neither available: the percentage stands alone rather than trailing a
    // separator with nothing after it.
    [Fact]
    public void DetailIsJustThePercentageWhenNeitherIsAvailable()
    {
        var detail = QuotaSummaryText.TightestDetail(
            Summary(withoutResetsAt: true, resetTextFallback: null), Now);

        Assert.DoesNotContain("·", detail, StringComparison.Ordinal);
    }

    // The boundary is a case, not a curiosity: quota percentages land on whole
    // numbers routinely. The fold counts v >= ComfortablePercent as
    // comfortable, so a window sitting exactly on the threshold is counted —
    // and the sentence has to be true of it.
    [Fact]
    public void AWindowExactlyOnTheThresholdIsCountedComfortable()
    {
        var others = new[] { QuotaSummaryFold.ComfortablePercent };

        Assert.Single(others.Where(v => v >= QuotaSummaryFold.ComfortablePercent));
    }

    [Fact]
    public void OthersTextSaysAtOrAboveRatherThanAbove()
    {
        var text = QuotaSummaryText.OthersText(
            Summary(otherWindows: 4, othersComfortable: 4));

        // The claim must hold for a window sitting exactly on the threshold.
        Assert.DoesNotContain("all above", text, StringComparison.Ordinal);
    }

    // The header renders today as `today?.Tokens ?? 0`, so it shows a zero on
    // a day with no entry. The summary row sits centimetres below it and must
    // say the same thing; gating the row on the map entry made the two
    // disagree on exactly the inactive days. TodayText has always accepted
    // zero — the branch was simply unreachable from here, which is what a
    // wrongly-placed gate looks like from the inside.
    [Fact]
    public void TodayTextRendersAZeroDayRatherThanNothing()
    {
        var text = QuotaSummaryText.TodayText(0, 0, authoritative: true);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("0", text, StringComparison.Ordinal);
    }

    // Zero tokens with no cost authority must not fabricate a price. The
    // unpriced day sums to exactly zero, and "$0.00" is a number where the
    // honest answer is that nothing is known yet.
    [Fact]
    public void TodayTextDoesNotInventAPriceForAnUnpricedZeroDay() =>
        Assert.DoesNotContain(
            "$0.00", QuotaSummaryText.TodayText(0, 0, authoritative: false), StringComparison.Ordinal);


    // The case this method's doc comment used to claim it handled and did not:
    // a day of usage that all failed to price, in a graph where something else
    // priced (so authority is true). It read "$0.00". Now "—".
    [Fact]
    public void TodayTextMarksAnUnpricedDayRatherThanCallingItFree() =>
        Assert.Equal(
            "1.2M tokens · —", QuotaSummaryText.TodayText(1_234_000, 0, authoritative: true));
}
