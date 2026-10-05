using TokenBar.Core;
using Xunit;

namespace TokenBar.Core.Tests;

// Round 9's third finding: the Hourly and Agents lanes had no retain-on-
// failure fold at all, so DashboardModel.FetchLazyWanted published
// `Hourly = hourly ? hourlyReport : s.Hourly` — a transient failure
// (hourlyReport is null because TryFetch swallowed a throw) overwrote a
// previously-good report with null. DashboardModel.cs itself is compiled by
// no test project (it opens with `using Microsoft.UI.Dispatching;`), so this
// is the seam that actually gets exercised.
//
// A third output, FetchFailed, and the previousFetchFailed input that fed it,
// were dropped later: every reader on the equivalence path that cared about
// "did the MOST RECENT attempt fail" already checked Value for retained data
// first, so once Value is non-null, deriving Failed-vs-Succeeded from
// Value is null alone reaches the same answer that output existed to correct
// — see DashboardModel.Snapshot.HourlyOutcome's own doc comment. The tests
// that existed only to pin that output are gone; the rest keep asserting the
// Value/Attempted behaviour that remains.
public class LazyLaneFoldTests
{
    private sealed record Report(int Value);

    // ---- the latest read's own failure (Q33b) -------------------------------

    // A retained empty list says nothing about the latest read: a pass that
    // read and failed is a failure even though something is retained, a pass
    // that read and succeeded clears it, a pass that did not read carries it.
    [Fact]
    public void LatestReadFailedFollowsTheLatestReadNotWhatIsRetained()
    {
        Assert.True(LazyLaneFold.LatestReadFailed<Report>(requested: true, value: null, previous: false));
        Assert.False(LazyLaneFold.LatestReadFailed(requested: true, value: new Report(0), previous: true));
        Assert.True(LazyLaneFold.LatestReadFailed<Report>(requested: false, value: null, previous: true));
        Assert.False(LazyLaneFold.LatestReadFailed<Report>(requested: false, value: null, previous: false));
    }

    // ---- the retention bug itself -----------------------------------------

    [Fact]
    public void AFailedFetchRetainsThePreviousValueRatherThanOverwritingItWithNull()
    {
        var previous = new Report(7);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: null, previous, previousAttempted: true);

        // The bug this fold exists to fix: `requested ? fetched : previous`
        // (the old Hourly/Agents line) would have set Value to null here,
        // discarding `previous` outright. The mutant that flips `??` to `:`
        // for the Value line reproduces exactly that regression.
        Assert.Equal(previous, result.Value);
        Assert.True(result.Attempted);
    }

    [Fact]
    public void ASuccessfulFetchReplacesThePreviousValue()
    {
        var previous = new Report(7);
        var fresh = new Report(9);

        var result = LazyLaneFold.Apply(
            requested: true, fetched: fresh, previous, previousAttempted: true);

        Assert.Equal(fresh, result.Value);
        Assert.True(result.Attempted);
    }

    [Fact]
    public void APassThatDidNotAskLeavesTheValueAtWhateverThePriorPassLeftIt()
    {
        var previous = new Report(7);

        var result = LazyLaneFold.Apply(
            requested: false, fetched: null, previous, previousAttempted: true);

        Assert.Equal(previous, result.Value);
        Assert.True(result.Attempted);
    }

    // ---- attempted as a fact about THIS request, not the result -----------

    [Fact]
    public void NeverRequestedAndNothingRetainedIsNotAttempted()
    {
        var result = LazyLaneFold.Apply<Report>(
            requested: false, fetched: null, previous: null, previousAttempted: false);

        Assert.Null(result.Value);
        Assert.False(result.Attempted);
    }
}

// The retained-data rule, pinned where CI can fail on it.
//
// Four commits on this branch replaced "a failed fetch wins over retained
// data" with "retained data wins, and a failure shows only when there is
// nothing to show" — the last of them by deleting five per-lane FetchFailed
// flags and deriving each lane's outcome from `Attempted` plus the payload's
// nullness instead. That rule then lived in five byte-identical ternaries
// inside DashboardModel.Snapshot, which no test project compiles, so the
// only way to check it was to drop a real network connection on a real
// machine and look at the screen.
//
// LazyLaneFold.Outcome is the same rule with a name, in a place a build can
// reach. These tests are the automated form of that manual check.
public class LazyLaneOutcomeTests
{
    private sealed record Payload(int Rows);

    [Fact]
    public void ALaneNobodyAskedForIsNotAttemptedWhicheverWayItsPayloadReads()
    {
        Assert.Equal(
            WindowEquivalence.FetchOutcome.NotAttempted,
            LazyLaneFold.Outcome<Payload>(attempted: false, value: null));
        Assert.Equal(
            WindowEquivalence.FetchOutcome.NotAttempted,
            LazyLaneFold.Outcome(attempted: false, value: new Payload(3)));
    }

    [Fact]
    public void AskedWithNothingRetainedIsFailedRatherThanEmpty()
    {
        // Sound only because of the invariant either side of this call:
        // TryFetch returns null solely on a throw, and a completed-and-empty
        // read publishes a non-null empty payload. So "attempted and null"
        // cannot mean "asked, and there was genuinely nothing".
        Assert.Equal(
            WindowEquivalence.FetchOutcome.Failed,
            LazyLaneFold.Outcome<Payload>(attempted: true, value: null));
    }

    [Fact]
    public void AFailedRetryThatKeptEarlierDataReportsSucceededSoTheCardKeepsDrawingIt()
    {
        // This is the case a human could previously only reach by pulling the
        // network cable: one good fetch, then a throw. Apply retains the
        // earlier payload; Outcome must then answer Succeeded, because a card
        // that checks its data first has data to draw and must not be handed a
        // failure state that blanks it.
        var good = new Payload(7);
        var afterThrow = LazyLaneFold.Apply(
            requested: true, fetched: (Payload?)null, previous: good, previousAttempted: true);

        Assert.Equal(good, afterThrow.Value);
        Assert.True(afterThrow.Attempted);
        Assert.Equal(
            WindowEquivalence.FetchOutcome.Succeeded,
            LazyLaneFold.Outcome(afterThrow.Attempted, afterThrow.Value));
    }

    [Fact]
    public void AColdStartWhoseVeryFirstFetchThrowsIsTheOneCaseThatRendersTheFailure()
    {
        // The counterpart of the test above, and the reason Failed still has to
        // exist: nothing was ever retained, so there is nothing to prefer over
        // the failure message.
        var afterThrow = LazyLaneFold.Apply(
            requested: true, fetched: (Payload?)null, previous: null, previousAttempted: false);

        Assert.Null(afterThrow.Value);
        Assert.Equal(
            WindowEquivalence.FetchOutcome.Failed,
            LazyLaneFold.Outcome(afterThrow.Attempted, afterThrow.Value));
    }

    [Fact]
    public void LookingAwayFromALensDoesNotRevertItsOutcomeToNotAttempted()
    {
        // LazyLaneActivation un-wants a lane when its lens stops being active,
        // so `requested` goes back to false on later passes. Attempted has to
        // survive that, or a lane that already answered would start claiming it
        // had never been asked.
        var afterLookingAway = LazyLaneFold.Apply(
            requested: false, fetched: (Payload?)null, previous: new Payload(2),
            previousAttempted: true);

        Assert.Equal(
            WindowEquivalence.FetchOutcome.Succeeded,
            LazyLaneFold.Outcome(afterLookingAway.Attempted, afterLookingAway.Value));
    }
}
