using TokenBar.App;
using TokenBar.Core;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Syrtis-Windows #247. DashboardModel.cs is compiled by no test project, so
// these drive ModelsLineage, the type its Begin/Publish/Apply handlers call.
public class ModelsLineageTests
{
    private static readonly GraphQuery AllTime = new(null);
    private static readonly GraphQuery Y2025 = new("2025");

    private static GraphRequestId Req(GraphQuery q, long gen) => new(q, gen);

    private static ModelReport Report(double cost) =>
        new([], 0, 0, 0, 0, 0, cost);

    // A settled snapshot: report current for request 1, graph authoritative.
    private static ModelsLineage Settled(out ModelReport report)
    {
        report = Report(1);
        return new ModelsLineage(null, null, null, false, false)
            .Publish(Req(AllTime, 1), report, graphAuthoritative: true);
    }

    [Fact]
    public void SameQueryGenerationKeepsModelsButStatesNoCost()
    {
        var s = Settled(out var report);
        Assert.True(s.ModelsCostAuthoritative);

        var begun = s.Begin(Req(AllTime, 2));
        Assert.Same(report, begun.Models);
        Assert.False(begun.ModelsCostAuthoritative);

        // Graph acceptance re-grants the graph flag, not the stale models'.
        var published = begun.Publish(Req(AllTime, 2), early: null, graphAuthoritative: true);
        Assert.Same(report, published.Models);
        Assert.True(published.GraphAuthoritative);
        Assert.False(published.ModelsCostAuthoritative);
    }

    [Fact]
    public void FailedModelFetchNeverPromotesTheOldReport()
    {
        var s = Settled(out _).Begin(Req(AllTime, 2))
            .Publish(Req(AllTime, 2), early: null, graphAuthoritative: true);
        // Several more graph publications, still no report for them.
        s = s.Publish(Req(AllTime, 2), early: null, graphAuthoritative: true);
        Assert.False(s.ModelsCostAuthoritative);
    }

    [Fact]
    public void MatchingReportRestoresAuthority()
    {
        var s = Settled(out _).Begin(Req(AllTime, 2))
            .Publish(Req(AllTime, 2), early: null, graphAuthoritative: true);
        var fresh = Report(2);
        s = s.Apply(Req(AllTime, 2), fresh);
        Assert.Same(fresh, s.Models);
        Assert.True(s.ModelsCostAuthoritative);
    }

    [Fact]
    public void ReportArrivingBeforeTheGraphIsAppliedAtPublication()
    {
        var early = Report(3);
        var s = Settled(out _).Begin(Req(AllTime, 2))
            .Publish(Req(AllTime, 2), early, graphAuthoritative: true);
        Assert.Same(early, s.Models);
        Assert.True(s.ModelsCostAuthoritative);
    }

    [Fact]
    public void DifferentQueryDropsModelsAndTheSnapshot()
    {
        var s = Settled(out _);
        Assert.True(ModelsLineage.DropsSnapshot(s.RequestId, Req(Y2025, 2)));
        Assert.False(ModelsLineage.DropsSnapshot(s.RequestId, Req(AllTime, 2)));
        Assert.Null(s.Begin(Req(Y2025, 2)).Models);
    }

    [Fact]
    public void RestoredSnapshotWithChangedYearIsDroppedNotKept()
    {
        // A fresh DashboardModel seeds its request id from the restored
        // snapshot (RequestId), so the first Begin sees the year change.
        var restored = Settled(out _);
        Assert.True(ModelsLineage.DropsSnapshot(restored.RequestId, Req(Y2025, 1)));
    }

    [Fact]
    public void RankBasisIsHeldWhileModelsAreStale()
    {
        var s = Settled(out _);
        Assert.True(s.RankByCost);
        var begun = s.Begin(Req(AllTime, 2));
        Assert.True(begun.RankByCost);
        var published = begun.Publish(Req(AllTime, 2), null, graphAuthoritative: true);
        Assert.True(published.RankByCost);
        Assert.True(published.Apply(Req(AllTime, 2), Report(2)).RankByCost);
    }

    [Fact]
    public void RankBasisSurvivesGraphAuthorityDroppingWhileStale()
    {
        var s = Settled(out _).Begin(Req(AllTime, 2))
            .Publish(Req(AllTime, 2), early: null, graphAuthoritative: false);
        Assert.True(s.RankByCost);
    }

    [Fact]
    public void EmptyNewReportReplacesTheKeptOne()
    {
        var s = Settled(out _).Begin(Req(AllTime, 2))
            .Publish(Req(AllTime, 2), early: null, graphAuthoritative: true);
        var empty = Report(0);
        s = s.Apply(Req(AllTime, 2), empty);
        Assert.Same(empty, s.Models);
    }
}
