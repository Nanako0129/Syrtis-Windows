using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class GraphConsumerStateTests
{
    private static UsagePayload Payload(
        PricingMode pricing = PricingMode.BestEffort,
        CostCoverage coverage = CostCoverage.Complete,
        IReadOnlyList<YearMeta>? years = null) =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-12-31"),
                pricing,
                coverage),
            new UsageSummary(1, 2, 1, 1, 2, 2, [], []),
            years ?? [],
            []);

    private static GraphRequestId Id(string? year, long generation) =>
        new(GraphQuery.Normalize(year), generation);

    [Fact]
    public void CompletionPolicyFencesStaleSettlesHiddenAndKeepsRerunSpinner()
    {
        var stale = GraphCompletionPolicy.Decide(
            isCurrent: false,
            polling: true,
            sameYear: true,
            forceRequested: false,
            refreshing: true);
        Assert.False(stale.IsCurrent);
        Assert.False(stale.ShouldRerun);
        Assert.False(stale.ClearRefreshing);

        var hidden = GraphCompletionPolicy.Decide(
            isCurrent: true,
            polling: false,
            sameYear: false,
            forceRequested: true,
            refreshing: true);
        Assert.True(hidden.IsCurrent);
        Assert.False(hidden.ShouldRerun);
        Assert.True(hidden.ClearRefreshing);

        var rerun = GraphCompletionPolicy.Decide(
            isCurrent: true,
            polling: true,
            sameYear: false,
            forceRequested: false,
            refreshing: true);
        Assert.True(rerun.IsCurrent);
        Assert.True(rerun.ShouldRerun);
        Assert.False(rerun.ClearRefreshing);
    }

    [Fact]
    public void MissingYearPolicyClearsOnlyTheExactSelectedYear()
    {
        var missing = new GraphPublication(
            Id("2026", 1),
            GraphPublicationStage.LocalFirst,
            Payload());
        var present = missing with
        {
            Payload = Payload(years:
            [
                new YearMeta(
                    "2026",
                    1,
                    2,
                    new DateRange("2026-01-01", "2026-12-31")),
            ]),
        };
        var allTime = missing with { RequestId = Id(null, 2) };

        Assert.True(GraphYearPolicy.ShouldClearToAllTime("2026", missing));
        Assert.False(GraphYearPolicy.ShouldClearToAllTime("2025", missing));
        Assert.False(GraphYearPolicy.ShouldClearToAllTime(null, missing));
        Assert.False(GraphYearPolicy.ShouldClearToAllTime("2026", present));
        Assert.False(GraphYearPolicy.ShouldClearToAllTime("2026", allTime));
    }

    [Fact]
    public void LazyRefreshSchedulesOncePerExactGraphRequest()
    {
        var first = Id(null, 1);
        var second = Id(null, 2);
        var otherQuery = Id("2026", 1);

        Assert.True(GraphLazyRefreshPolicy.ShouldRequest(null, first));
        Assert.False(GraphLazyRefreshPolicy.ShouldRequest(first, first));
        Assert.True(GraphLazyRefreshPolicy.ShouldRequest(first, second));
        Assert.True(GraphLazyRefreshPolicy.ShouldRequest(first, otherQuery));
    }

    // Round 19's finding 1: the model's lazy lanes used to accumulate
    // ("ensure") with no way back to unwanted, so a Quota visit left
    // WindowUsage's expensive scan wanted for the model's lifetime even
    // after switching away. LazyLaneActivation.For is the single answer
    // DashboardModel.SetActiveView now reads: exactly the currently-open
    // lens's own lanes wanted, every sibling lane explicitly off.
    [Fact]
    public void OnlyTheOpenLenssOwnLanesAreWanted()
    {
        Assert.Equal(new LazyLaneActivation.Wanted(true, false, false, false), LazyLaneActivation.For(AppView.Hourly));
        Assert.Equal(new LazyLaneActivation.Wanted(false, true, false, false), LazyLaneActivation.For(AppView.Agents));
        Assert.Equal(new LazyLaneActivation.Wanted(false, false, true, true), LazyLaneActivation.For(AppView.Quota));
        // Overview wants the quota history alone: its Agent-limits card draws
        // trend arrows from it (G3a, 188 check on 075d4d1 showed none there
        // until Quota had been visited). The expensive window-usage scan stays
        // Quota's own.
        Assert.Equal(new LazyLaneActivation.Wanted(false, false, true, false), LazyLaneActivation.For(AppView.Overview));
        // Every other lens wants none of the four lazy lanes: this is what
        // actually stops the leak, since the model reads this fresh on every
        // SwitchTo rather than only ever setting flags true.
        Assert.Equal(default, LazyLaneActivation.For(AppView.Models));
        Assert.Equal(default, LazyLaneActivation.For(AppView.Monthly));
        Assert.Equal(default, LazyLaneActivation.For(AppView.Daily));
        Assert.Equal(default, LazyLaneActivation.For(AppView.Stats));
    }

    [Fact]
    public void BeginRevokesOldGraphAndRejectsDelayedCallback()
    {
        var state = new GraphConsumerState();
        var first = Id("2026", 1);
        var second = Id("2026", 2);
        var querySwitch = Id("2025", 3);

        Assert.True(state.Begin(first));
        Assert.True(state.TryAcceptGraph(
            first, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);

        Assert.True(state.Begin(second));
        Assert.False(state.LocalAccepted);
        Assert.False(state.CostAuthoritative);
        Assert.Null(state.AcceptedGraph);
        Assert.False(state.TryAcceptGraph(
            first, Payload(), GraphPublicationStage.Richer));

        Assert.True(state.Begin(querySwitch));
        Assert.Null(state.AcceptedGraph);
        Assert.False(state.TryAcceptGraph(
            second, Payload(), GraphPublicationStage.Richer));
    }

    [Fact]
    public void BeginRejectsOlderSameQueryButAllowsOlderDifferentQuery()
    {
        var state = new GraphConsumerState();
        var current = Id(null, 9);
        var olderSameQuery = Id(null, 8);
        var olderDifferentQuery = Id("2025", 1);

        Assert.True(state.Begin(current));
        Assert.True(state.TryAcceptGraph(
            current, Payload(), GraphPublicationStage.Richer));
        Assert.False(state.Begin(olderSameQuery));
        Assert.Equal(current, state.DesiredId);
        Assert.True(state.CostAuthoritative);

        Assert.True(state.Begin(olderDifferentQuery));
        Assert.Equal(olderDifferentQuery, state.DesiredId);
        Assert.False(state.CostAuthoritative);
    }

    [Fact]
    public void RetainedLocalCannotRegressSameGenerationRicherPublication()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        var richer = Payload();
        var retainedLocal = Payload(PricingMode.LocalOnly, CostCoverage.None);
        state.Begin(id);

        Assert.True(state.TryAcceptGraph(
            id, richer, GraphPublicationStage.Richer));
        Assert.False(state.TryAcceptGraph(
            id, retainedLocal, GraphPublicationStage.LocalFirst));

        Assert.Same(richer, state.AcceptedGraph);
        Assert.Equal(GraphPublicationStage.Richer, state.AcceptedStage);
        Assert.True(state.LocalAccepted);
        Assert.True(state.CostAuthoritative);
    }

    [Theory]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Complete)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.Partial)]
    [InlineData(PricingMode.LocalOnly, CostCoverage.None)]
    [InlineData(PricingMode.BestEffort, CostCoverage.None)]
    public void NonAuthoritativeMetadataStaysChecking(
        PricingMode pricing, CostCoverage coverage)
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);

        Assert.True(state.TryAcceptGraph(
            id, Payload(pricing, coverage), GraphPublicationStage.LocalFirst));
        Assert.True(state.LocalAccepted);
        Assert.False(state.CostAuthoritative);
    }

    /// <summary>Partial priced at least one message and folded the rest in at
    /// zero, so the total is real. It is also terminal, not a loading step —
    /// withholding it left the cost surface reading "Checking" forever on any
    /// profile holding one unpriced model.</summary>
    [Fact]
    public void PartialCoverageIsCostAuthoritative()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);

        Assert.True(state.TryAcceptGraph(
            id,
            Payload(PricingMode.BestEffort, CostCoverage.Partial),
            GraphPublicationStage.Richer));
        Assert.True(state.LocalAccepted);
        Assert.True(state.CostAuthoritative);
    }

    [Fact]
    public void BlockedOrFailedLocalLeavesLocalAcceptedFalse()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);

        Assert.False(state.LocalAccepted);
        Assert.False(state.TryBeginModel(id));
        Assert.False(state.CostAuthoritative);
    }

    [Fact]
    public void ExactCurrentCompleteRestoresAuthorityAndModelStartsOnce()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 4);
        state.Begin(id);

        // A retained richer publication is enough for a late observer to prove
        // the local stage and start ModelReport exactly once.
        Assert.True(state.TryAcceptGraph(
            id, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.TryBeginModel(id));
        Assert.False(state.TryBeginModel(id));
        Assert.True(state.TryAcceptModel(id, new ModelReport([], 0, 0, 0, 0, 0, 0)));
        Assert.True(state.ModelAccepted);

        var newer = Id(null, 5);
        Assert.True(state.Begin(newer));
        Assert.False(state.CostAuthoritative);
        Assert.False(state.TryAcceptModel(
            id, new ModelReport([], 0, 0, 0, 0, 0, 0)));
        Assert.True(state.TryAcceptGraph(
            newer, Payload(), GraphPublicationStage.Richer));
        Assert.True(state.CostAuthoritative);
    }

    [Fact]
    public void DisposeFencesGraphAndModelTransitions()
    {
        var state = new GraphConsumerState();
        var id = Id(null, 1);
        state.Begin(id);
        state.Dispose();

        Assert.True(state.IsDisposed);
        Assert.False(state.Begin(Id(null, 2)));
        Assert.False(state.TryAcceptGraph(
            id, Payload(), GraphPublicationStage.Richer));
        Assert.False(state.TryBeginModel(id));
        Assert.False(state.TryAcceptModel(
            id, new ModelReport([], 0, 0, 0, 0, 0, 0)));
    }
}
