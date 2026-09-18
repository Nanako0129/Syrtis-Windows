using Xunit;

namespace TokenBar.Core.Tests;

// Expectations transcribed from macos-ref/QuotaOverview.swift and the observed
// notes beside it, not invented. macOS ships no tests for this fold, so these
// are new work rather than a port: the 99 threshold, the omit-empty rule and the
// strip's direction have no executable statement on either platform without
// them.
public class QuotaOverviewFoldTests
{
    private static QuotaWindowIdentity Id(
        string provider = "claude", string scope = "primary", string window = "session.v1") =>
        new(provider, scope, window);

    /// <summary>A completed cycle observed from <paramref name="from"/> to
    /// <paramref name="to"/>: span is the consumption, the higher reading is the
    /// peak. <paramref name="resetAt"/> only orders cycles.</summary>
    private static QuotaCycle Cycle(double from, double to, long resetAt = 0) =>
        new(
            ResetAtMs: resetAt,
            StartMs: resetAt - 5 * 3_600_000,
            UsedPercent: to - from,
            PeakUsedPercent: to,
            SampleCount: 2,
            ObservedFraction: 1,
            FirstSampleMs: resetAt - 5 * 3_600_000,
            LastSampleMs: resetAt);

    private static IReadOnlyList<QuotaWindowSummary> Summaries(
        params (QuotaWindowIdentity Id, string? Label, IReadOnlyList<QuotaCycle> Cycles)[] windows) =>
        QuotaOverviewFold.Summaries(windows);

    // "`UsedPercent` is a span, so a cycle first observed at 40% and last at
    // 100% has a span of 60 and would have been called quiet." Both numbers are
    // carried because they answer different questions.
    [Fact]
    public void ConsumptionIsTheSpanAndThePeakIsTheReading()
    {
        var summary = Assert.Single(Summaries((Id(), "Session", [Cycle(40, 100)])));

        Assert.Equal(60, Assert.Single(summary.Recent));
        Assert.Equal(100, Assert.Single(summary.RecentPeaks));
        Assert.Equal(100, summary.PeakPercent);
        Assert.False(summary.NeverExhausted);
    }

    // 99, not 100: "providers report whole percents and stop updating once the
    // allowance is spent, so an exhausted window is observed at 99 or 100
    // depending on when the last sample landed."
    [Theory]
    [InlineData(99, false)]
    [InlineData(98, true)]
    public void TheCeilingIsNinetyNine(double peak, bool neverExhausted)
    {
        var summary = Assert.Single(Summaries((Id(), "Session", [Cycle(0, peak)])));

        Assert.Equal(neverExhausted, summary.NeverExhausted);
        Assert.Equal(99, QuotaOverviewFold.ExhaustedPercent);
    }

    // "Windows with no history are omitted rather than shown empty" — the row
    // that would otherwise repeat across every fresh install.
    [Fact]
    public void WindowsWithNoCyclesAreOmitted()
    {
        var summaries = Summaries(
            (Id(window: "weekly.v1"), "Weekly", []),
            (Id(), "Session", [Cycle(0, 12)]));

        Assert.Equal("session.v1", Assert.Single(summaries).Id.WindowKey);
    }

    // "Peak is taken over ALL cycles, not only the 16 shown": a capped fold made
    // a window that ran out thirty-three cycles ago report that it never had.
    [Fact]
    public void PeakLooksPastTheStripLength()
    {
        // Newest first, as QuotaHistoryFold hands them over: seventeen quiet
        // cycles, then the 100% one, which is the oldest and falls outside the
        // sixteen the strip shows.
        var cycles = Enumerable.Range(0, QuotaOverviewFold.StripLength + 1)
            .Select(index => Cycle(0, 10, resetAt: 100 - index))
            .Append(Cycle(0, 100, resetAt: 0))
            .ToList();

        var summary = Assert.Single(Summaries((Id(), "Session", cycles)));

        Assert.Equal(100, summary.PeakPercent);
        Assert.False(summary.NeverExhausted);
        Assert.Equal(QuotaOverviewFold.StripLength, summary.Recent.Count);
        Assert.Equal(18, summary.CycleCount);
    }

    [Fact]
    public void SummariesLeadWithTheHeaviestPeak()
    {
        var summaries = Summaries(
            (Id(window: "a"), "A", [Cycle(0, 30)]),
            (Id(window: "b"), "B", [Cycle(0, 90)]),
            (Id(window: "c"), "C", [Cycle(0, 60)]));

        Assert.Equal(
            new[] { "b", "c", "a" },
            summaries.Select(summary => summary.Id.WindowKey).ToArray());
    }

    // The one case the five above cannot catch. They are all order-invariant, so
    // a fold that drops the reversal passes every one of them, and a reversed
    // strip of varying-height bars looks entirely plausible on screen — the card
    // derives "how many windows ago" by counting back from the end, so the ages
    // come out inverted but readable.
    //
    // More than StripLength cycles, deliberately: reversing BEFORE taking the
    // prefix shows the oldest 16 instead of the newest 16, and a plain reverse
    // test would pass that bug.
    [Fact]
    public void StripRunsOldestToNewestAndKeepsTheNewestCycles()
    {
        // Newest first, as the fold receives them: peaks 17, 16 … 2, 1.
        var cycles = Enumerable.Range(1, QuotaOverviewFold.StripLength + 1)
            .Reverse()
            .Select(peak => Cycle(0, peak, resetAt: peak))
            .ToList();

        var summary = Assert.Single(Summaries((Id(), "Session", cycles)));

        // Oldest-first among the newest 16: peak 1 is the dropped one, so the
        // strip runs 2 … 17 and ends on the most recent cycle.
        Assert.Equal(
            Enumerable.Range(2, QuotaOverviewFold.StripLength).Select(peak => (double)peak).ToArray(),
            summary.RecentPeaks.ToArray());
        // Aligned index for index with the peaks: same cycles, same order.
        Assert.Equal(summary.RecentPeaks.ToArray(), summary.Recent.ToArray());
        Assert.Equal(17, summary.PeakPercent);
    }

    // ---- the picker's windows ------------------------------------------

    private static QuotaHeatmap Grid(double total, double unplaced) =>
        QuotaHeatmap.Empty with { Total = total, UnplacedPercent = unplaced };

    // "`HasMovement` and `total > 0` are different questions." A window whose
    // every reading pair straddles more than six hours has Total == 0 and moved;
    // dropping it made the card say nothing was recorded and hid the line that
    // explains why.
    [Fact]
    public void MovementWithNothingPlaceableStaysInThePicker()
    {
        var windows = QuotaOverviewFold.HeatmapWindows([
            (Id(window: "unplaced"), "Weekly", Grid(total: 0, unplaced: 7)),
            (Id(window: "silent"), "Session", Grid(total: 0, unplaced: 0)),
        ]);

        var window = Assert.Single(windows);
        Assert.Equal("unplaced", window.Id.WindowKey);
        Assert.Equal(0, window.Total);
    }

    [Fact]
    public void PickerLeadsWithTheHeaviestTotal()
    {
        var windows = QuotaOverviewFold.HeatmapWindows([
            (Id(window: "light"), "A", Grid(total: 3, unplaced: 0)),
            (Id(window: "heavy"), "B", Grid(total: 300, unplaced: 0)),
            (Id(window: "middling"), "C", Grid(total: 30, unplaced: 0)),
        ]);

        Assert.Equal(
            new[] { "heavy", "middling", "light" },
            windows.Select(window => window.Id.WindowKey).ToArray());
    }

    // The picker's set is NOT the strip card's: summaries need a completed
    // cycle, so a window whose only movement is in the running cycle has a grid
    // and no summary. Keying the picker on summaries made that grid unreachable
    // for days on a weekly window.
    [Fact]
    public void AWindowWithAGridAndNoCompletedCycleIsPickableAndUnsummarised()
    {
        var running = Id(window: "running");

        Assert.Empty(Summaries((running, "Weekly", [])));
        Assert.Single(QuotaOverviewFold.HeatmapWindows([(running, "Weekly", Grid(9, 0))]));
    }

    // ---- identity -------------------------------------------------------

    // "No multi-account surface" is a fact about the UI, not about the store's
    // primary key. Two scopes of one client must not collapse: the strip would
    // show two indistinguishable rows and one window's grid would overwrite the
    // other's in any dictionary keyed by the identity.
    [Fact]
    public void TwoScopesOfOneClientStayDistinct()
    {
        var work = Id(scope: "7M08");
        var personal = Id(scope: "primary");

        Assert.NotEqual(work, personal);

        var summaries = Summaries(
            (work, "Session", [Cycle(0, 80)]),
            (personal, "Session", [Cycle(0, 20)]));
        Assert.Equal(2, summaries.Count);

        var grids = new Dictionary<QuotaWindowIdentity, QuotaHeatmap>
        {
            [work] = Grid(total: 10, unplaced: 0),
            [personal] = Grid(total: 20, unplaced: 0),
        };
        Assert.Equal(2, grids.Count);
        Assert.Equal(10, grids[work].Total);
        Assert.Equal(20, grids[personal].Total);
    }

    // ---- the labels -----------------------------------------------------

    public QuotaOverviewFoldTests() => Localization.Load("en", AppContext.BaseDirectory);

    private static QuotaWindowSummary Summary(string? label) =>
        Assert.Single(Summaries((Id(), label, [Cycle(0, 10)])));

    [Fact]
    public void LabelsNameTheClientAndTheWindow()
    {
        Assert.Equal("Claude Code · Session", QuotaLabels.RowLabel(Summary("Session")));
        Assert.Equal(
            "Claude Code · Session",
            QuotaLabels.PickerLabel(new QuotaHeatmapWindow(Id(), "Session", 10)));
    }

    // The join can miss, and PaceStatus.WindowKey is itself nullable. A no-match
    // series keeps its identity and loses only its label, so it still names its
    // window — which is what this has always guarded. What changed on
    // 2026-09-19 is the string: the fallback used to be the raw `session.v1`,
    // and a reader meets it in the ordinary case (any provider whose live read
    // is unavailable, an expired login included), where it reads as an internal
    // code rather than a name.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnjoinedLabelStillNamesItsWindow(string? label)
    {
        Assert.Equal("Claude Code · Session", QuotaLabels.RowLabel(Summary(label)));
        Assert.Equal(
            "Claude Code · Session",
            QuotaLabels.PickerLabel(new QuotaHeatmapWindow(Id(), label, 0)));
    }

    // The store's keys compose, and one of them is built at runtime, so the
    // fallback reads them segment by segment rather than matching whole keys.
    [Theory]
    // The two every provider has.
    [InlineData("session.v1", "Session")]
    [InlineData("weekly.v1", "Weekly")]
    // Underscores are word breaks, not part of the word.
    [InlineData("extra_usage.v1", "Extra usage")]
    [InlineData("premium_interactions.v1", "Premium interactions")]
    // Composed keys keep every segment, in order.
    [InlineData("main.weekly.v1", "Main · Weekly")]
    [InlineData("opus.weekly.v1", "Opus · Weekly")]
    // Codex's per-account key: the hash is not a name and is dropped.
    [InlineData(
        "additional.d62616d234d82d5e7e4593f3112e1eefe54104f92fac0b29d052e87e3ec71975.primary.v1",
        "Additional · Primary")]
    // A segment this vocabulary has never seen still reads as a word.
    [InlineData("some_future_window.v3", "Some future window")]
    // Not always a store key: WindowCardText.Tabs puts the live CardId in this
    // slot for a window the store has nothing under, and a CardId is
    // `<client>|<window>`.
    [InlineData("claude|weekly.v1", "Claude · Weekly")]
    public void AWindowKeyReadsAsWordsRatherThanAsAnIdentifier(string key, string expected) =>
        Assert.Equal(expected, QuotaLabels.FromKey(key));

    // The version suffix is on every key, so it names nothing — but a key that
    // is ONLY a version, or only a hash, would render as an empty pill. The raw
    // key is the lesser evil there, and it is unreachable in practice.
    [Theory]
    [InlineData("v1")]
    [InlineData("deadbeefdeadbeefdeadbeef")]
    public void AKeyWithNoWordsInItKeepsItsRawForm(string key) =>
        Assert.Equal(key, QuotaLabels.FromKey(key));

    // Hex-looking words must not be mistaken for hashes. The bound is length,
    // and no English word this vocabulary could meet comes near it.
    [Fact]
    public void AShortHexLikeWordIsNotTreatedAsAHash() =>
        Assert.Equal("Decade · Weekly", QuotaLabels.FromKey("decade.weekly.v1"));

    // Never a separator with nothing after it — the visible defect this slice
    // refuses everywhere else too. With no label and no window key there is only
    // a client name to show.
    [Fact]
    public void NeitherLabelEverEndsInADanglingSeparator()
    {
        var empty = new QuotaWindowIdentity("claude", "primary", "");

        Assert.Equal("Claude Code", QuotaLabels.PickerLabel(new QuotaHeatmapWindow(empty, null, 0)));
        Assert.Equal(
            "Claude Code",
            QuotaLabels.RowLabel(Assert.Single(Summaries((empty, null, [Cycle(0, 10)])))));
    }
}
