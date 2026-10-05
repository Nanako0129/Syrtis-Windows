using TokenBar.App;
using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// The Quota lens's state choice and copy. DashboardView.xaml.cs is WinUI and no
// test project compiles it, so this — reached with no XAML type involved — is
// where the four-state branch is asserted.
public class QuotaLensTextTests
{
    public QuotaLensTextTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static QuotaHeatmap Grid(double total = 0, double unplaced = 0, int days = 0) =>
        QuotaHeatmap.Empty with { Total = total, UnplacedPercent = unplaced, ObservedDays = days };

    private static QuotaWindowSummary Summary(double peak) =>
        new(
            new QuotaWindowIdentity("claude", "primary", "session.v1"),
            "Session",
            Recent: [peak],
            RecentPeaks: [peak],
            PeakPercent: peak,
            NeverExhausted: peak < QuotaOverviewFold.ExhaustedPercent,
            CycleCount: 1);

    // All five, in macOS's order (QuotaHeatmapCard.swift:59-82) plus the
    // round-9 Failed state, each on the input that separates it from its
    // neighbour.
    [Fact]
    public void EveryHeatmapStateIsDistinct()
    {
        Assert.Equal(
            QuotaHeatmapState.Grid,
            QuotaLensText.HeatmapState(Grid(total: 12), WindowEquivalence.FetchOutcome.Succeeded, unreadable: true, windowsEmpty: true));

        // Total == 0 with movement that could not be placed. This must NOT fall
        // through to "nothing recorded yet": that states the opposite of the
        // truth and hides the one line explaining it.
        Assert.Equal(
            QuotaHeatmapState.Unplaced,
            QuotaLensText.HeatmapState(Grid(total: 0, unplaced: 7), WindowEquivalence.FetchOutcome.Succeeded, unreadable: true, windowsEmpty: true));

        // The trio that differs only in the outcome, which is the whole point:
        // a card that has lost the distinction passes every assertion above.
        Assert.Equal(
            QuotaHeatmapState.NoMovement,
            QuotaLensText.HeatmapState(Grid(), WindowEquivalence.FetchOutcome.Succeeded, unreadable: true, windowsEmpty: true));
        Assert.Equal(
            QuotaHeatmapState.Loading,
            QuotaLensText.HeatmapState(Grid(), WindowEquivalence.FetchOutcome.NotAttempted, unreadable: true, windowsEmpty: true));
        // The round-9 finding: a failed read must not render as "nothing
        // recorded yet" (NoMovement) or as "still asking" (Loading) — it is
        // neither, and it used to fall into whichever of those two the old
        // `bool attempted` collapsed it into.
        Assert.Equal(
            QuotaHeatmapState.Failed,
            QuotaLensText.HeatmapState(Grid(), WindowEquivalence.FetchOutcome.Failed, unreadable: true, windowsEmpty: true));
    }

    // A lazy lens fetches on first visit, so a null grid before the fetch is the
    // first paint of every cold start — not "nothing recorded yet".
    [Fact]
    public void NoGridYetIsLoadingUntilTheFetchHasBeenAttempted()
    {
        Assert.Equal(QuotaHeatmapState.Loading, QuotaLensText.HeatmapState(null, WindowEquivalence.FetchOutcome.NotAttempted, unreadable: true, windowsEmpty: true));
        Assert.Equal(QuotaHeatmapState.NoMovement, QuotaLensText.HeatmapState(null, WindowEquivalence.FetchOutcome.Succeeded, unreadable: true, windowsEmpty: true));
        Assert.Equal(QuotaHeatmapState.Failed, QuotaLensText.HeatmapState(null, WindowEquivalence.FetchOutcome.Failed, unreadable: true, windowsEmpty: true));
    }

    [Fact]
    public void EveryStripStateIsDistinct()
    {
        Assert.Equal(
            QuotaStripState.Rows,
            QuotaLensText.StripState([Summary(40)], WindowEquivalence.FetchOutcome.Succeeded, unreadable: true));
        Assert.Equal(
            QuotaStripState.NoCompletedWindows,
            QuotaLensText.StripState([], WindowEquivalence.FetchOutcome.Succeeded, unreadable: true));
        Assert.Equal(
            QuotaStripState.Loading,
            QuotaLensText.StripState([], WindowEquivalence.FetchOutcome.NotAttempted, unreadable: true));
        // Round-9 finding, strip half: same failed-vs-loading-vs-empty
        // distinction as the heatmap's.
        Assert.Equal(
            QuotaStripState.Failed,
            QuotaLensText.StripState([], WindowEquivalence.FetchOutcome.Failed, unreadable: true));
    }

    [Fact]
    public void HeadlineReadsTheCeilingOffThePeak()
    {
        Assert.Equal("Peaked at 98% · never ran out", QuotaLensText.Headline(Summary(98)));
        Assert.Equal("Peaked at 99% · ran out at least once", QuotaLensText.Headline(Summary(99)));
    }

    [Fact]
    public void StripSubtitleOnlyClaimsWhatHoldsForEveryWindow()
    {
        Assert.Equal("never exhausted", QuotaLensText.StripSubtitle([Summary(40), Summary(80)]));
        Assert.Null(QuotaLensText.StripSubtitle([Summary(40), Summary(100)]));
        Assert.Null(QuotaLensText.StripSubtitle([]));
    }

    [Fact]
    public void HeatmapSubtitleCountsObservedDaysAndIsAbsentWithoutAGrid()
    {
        Assert.Equal("21 days observed", QuotaLensText.HeatmapSubtitle(Grid(total: 5, days: 21)));
        Assert.Null(QuotaLensText.HeatmapSubtitle(Grid(days: 21)));
        Assert.Null(QuotaLensText.HeatmapSubtitle(null));
    }

    // The footnote's one-point threshold is about whether a line is worth
    // drawing; HasMovement's is about whether anything happened. Different
    // questions, different thresholds — a window can be in the picker with no
    // footnote.
    [Fact]
    public void FootnoteAppearsOnlyFromAFullPointOfUnplacedConsumption()
    {
        Assert.Equal(
            "7% consumed between readings too far apart to place",
            QuotaLensText.Footnote(Grid(unplaced: 6.6)));
        Assert.Null(QuotaLensText.Footnote(Grid(unplaced: 0.5)));
        Assert.True(Grid(unplaced: 0.5).HasMovement);
    }

    // "A 0.4% slot and an idle one must not both render as 0%."
    [Theory]
    [InlineData(0.4, "0.4")]
    [InlineData(9.94, "9.9")]
    [InlineData(72.4, "72")]
    public void SlotPercentKeepsADecimalBelowTen(double value, string expected) =>
        Assert.Equal(expected, QuotaLensText.Percent(value));

    [Fact]
    public void SlotCopySeparatesSpendFromSilence()
    {
        Assert.Equal("72 allowance-points spent here", QuotaLensText.SlotSpend(72));
        Assert.Equal("No allowance consumed in this slot", QuotaLensText.SlotEmpty());
        Assert.Equal("Fri 16:00", QuotaLensText.SlotHeader(weekday: 4, hour: 16));
        Assert.Equal("Mon 00:00", QuotaLensText.SlotHeader(weekday: 0, hour: 0));
    }

    // ---- i18n ----------------------------------------------------------
    //
    // Against the *shipped* strings-zh-Hant.json (the csproj copies it beside
    // the test assembly), not a fixture: the failure being guarded against is a
    // Localized() call site whose key was never added to that file, and against
    // a fixture written next to the test that failure is unreachable. Each card
    // shows one state at a time, so no screenshot can prove the others have
    // entries — only driving each branch can.
    [Fact]
    public void EveryStringTheCardsCanShowHasATableEntry()
    {
        var grid = Grid(total: 5, unplaced: 7, days: 21);
        Func<string?>[] surfaces =
        [
            QuotaLensText.StripTitle,
            () => QuotaLensText.StripSubtitle([Summary(40)]),
            () => QuotaLensText.Headline(Summary(40)),
            () => QuotaLensText.Headline(Summary(100)),
            () => QuotaLensText.WindowCount(9),
            QuotaLensText.NoCompletedWindows,
            () => QuotaLensText.BarAge(0),
            () => QuotaLensText.BarAge(3),
            () => QuotaLensText.BarConsumed(42),
            QuotaLensText.RanOut,
            QuotaLensText.HeatmapTitle,
            () => QuotaLensText.HeatmapSubtitle(grid),
            () => QuotaLensText.UnplacedBody(7),
            () => QuotaLensText.Footnote(grid),
            QuotaLensText.NoMovement,
            QuotaLensText.Loading,
            QuotaLensText.Failed,
            () => QuotaLensText.SlotHeader(weekday: 4, hour: 16),
            QuotaLensText.SlotEmpty,
            () => QuotaLensText.SlotSpend(72),
            () => AppViews.Label(AppView.Quota),
        ];

        var english = surfaces.Select(surface => surface()).ToList();
        Localization.Load("zh-Hant", AppContext.BaseDirectory);
        try
        {
            for (var i = 0; i < surfaces.Length; i++)
            {
                Assert.NotEqual(english[i], surfaces[i]());
            }
        }
        finally
        {
            Localization.Load("en", AppContext.BaseDirectory);
        }
    }

    // The strip is oldest-to-newest, so the newest bar is zero windows ago.
    [Fact]
    public void BarAgeNamesTheMostRecentWindowRatherThanCountingItAsZero()
    {
        Assert.Equal("Most recent window", QuotaLensText.BarAge(0));
        Assert.Equal("3 windows ago", QuotaLensText.BarAge(3));
    }
}
