namespace TokenBar.Core;

/// <summary>
/// The retain-on-failure fold every lazy Dashboard lane's own fetch pass must
/// apply before publishing: a failed read keeps whatever the snapshot already
/// had rather than replacing good, previously-fetched data with null, and
/// records its own attempted/failed facts independently of what ended up
/// retained — the same shape <c>DashboardModel.Snapshot.QuotaHistory</c> and
/// <c>WindowUsage</c> already carry (see those properties' own doc comments).
/// <para>
/// Round 9's finding: the Hourly and Agents lanes did not apply this fold at
/// all — <c>DashboardModel.FetchLazyWanted</c> published
/// <c>Hourly = hourly ? hourlyReport : s.Hourly</c>, so a transient failure
/// (<c>hourlyReport is null</c> because the fetch threw) overwrote a
/// previously-good report with null, and the view then showed "Loading
/// hourly data…" — indistinguishable from a cold start that has not fetched
/// yet, even though good data existed a moment before. Strictly worse than
/// the loading-vs-failed confusion rounds 7 and 8 already fixed on the other
/// two lanes.
/// </para>
/// <para>
/// Extracted here rather than left inline in <c>DashboardModel.Publish</c>,
/// because <c>DashboardModel.cs</c> is compiled by no test project (it opens
/// with <c>using Microsoft.UI.Dispatching;</c>) — this is the seam a test can
/// actually reach.
/// </para>
/// </summary>
public static class LazyLaneFold
{
    /// <summary>The two facts one lane's own Publish call needs, folded from
    /// this pass's own result and the snapshot's prior state.
    /// <para>
    /// A third output, <c>FetchFailed</c> — this pass's own success/failure,
    /// recorded independently of whether <c>Value</c> ended up retained —
    /// was dropped along with the <c>previousFetchFailed</c> input it read.
    /// Every reader on the equivalence path that cared about that
    /// distinction already checks <c>Value</c> for retained data before ever
    /// falling back to an outcome derived from it, so once <c>Value</c> is
    /// non-null a caller deriving Failed-vs-Succeeded from
    /// <c>Value is null</c> alone reaches the same answer that flag existed
    /// to correct — see <c>DashboardModel.Snapshot.HourlyOutcome</c>'s own
    /// doc comment.
    /// </para></summary>
    public readonly record struct Result<T>(T? Value, bool Attempted) where T : class;

    /// <param name="requested">Whether THIS pass asked for the lane at all —
    /// the caller's own "wanted" flag, not derived from <paramref name="fetched"/>:
    /// <c>fetched is null</c> alone cannot tell "did not ask" from "asked and
    /// it threw", which is exactly the ambiguity this fold exists to
    /// resolve.</param>
    /// <param name="fetched">This pass's own fetch result — null when not
    /// requested, and also null when requested but the fetch threw
    /// (<c>TryFetch</c> collapses both to null, which is why
    /// <paramref name="requested"/> has to travel alongside it rather than
    /// being inferred from it).</param>
    /// <param name="previous">The snapshot's own already-published value,
    /// kept when this pass has nothing newer to report.</param>
    /// <param name="previousAttempted">Whether an earlier pass ever
    /// requested this lane. <c>LazyLaneActivation</c> (App) un-wants a lane
    /// once its lens is no longer the active one, so <paramref name="requested"/>
    /// alone can go back to false on a later pass — this input is what keeps
    /// Attempted (and therefore the lane's outcome) true for the lens's
    /// lifetime rather than reverting to NotAttempted the moment the user
    /// looks away.</param>
    public static Result<T> Apply<T>(
        bool requested, T? fetched, T? previous, bool previousAttempted)
        where T : class =>
        new(fetched ?? previous, requested || previousAttempted);

    /// <summary>The three states a lazy lane can be in, derived from the two
    /// facts a snapshot actually holds. Every <c>DashboardModel.Snapshot</c>
    /// lane property — Hourly, Agents, Quota, QuotaHistory, WindowUsage —
    /// answers with this; they carried five byte-identical copies of the
    /// ternary before it had a name.
    /// <para>
    /// <paramref name="value"/> being null is what distinguishes Failed from
    /// Succeeded, and that is sound because of the two invariants either side
    /// of this function: <c>TryFetch</c> returns null only when the fetch
    /// threw, and every lane's legitimately-empty result publishes a non-null
    /// empty payload rather than null. So "attempted, and nothing is
    /// retained" is exactly "the most recent attempt threw and no earlier one
    /// had succeeded". A separate per-lane <c>FetchFailed</c> flag used to
    /// carry the most recent attempt's own outcome independently of what was
    /// retained; it was deleted because a failed retry that keeps earlier data
    /// must render that data, not a failure — the rule every reader on this
    /// path applies by checking its payload first.
    /// </para>
    /// <para>
    /// Here rather than in <c>DashboardModel</c> for the same reason
    /// <see cref="Apply"/> is: that file is compiled by no test project, so a
    /// rule left inline in it can only ever be verified by reading. This is
    /// the seam that lets the retained-data rule fail a build instead.
    /// </para></summary>
    public static WindowEquivalence.FetchOutcome Outcome<T>(bool attempted, T? value)
        where T : class =>
        !attempted ? WindowEquivalence.FetchOutcome.NotAttempted
        : value is null ? WindowEquivalence.FetchOutcome.Failed
        : WindowEquivalence.FetchOutcome.Succeeded;

    /// <summary>Whether the LATEST read failed, independent of what is
    /// retained: a pass that read sets it from its own result (a failed
    /// read returns null), a pass that did not read carries the previous
    /// answer. <see cref="Outcome"/> cannot say this once an earlier read
    /// retained a value (an empty list included); the Quota lens's "could
    /// not be read" needs it (macOS decides per read, DashboardModel.swift
    /// :1670-1712). Here, not inline in DashboardModel, so a test fails if
    /// the rule changes.</summary>
    public static bool LatestReadFailed<T>(bool requested, T? value, bool previous)
        where T : class =>
        requested ? value is null : previous;
}
