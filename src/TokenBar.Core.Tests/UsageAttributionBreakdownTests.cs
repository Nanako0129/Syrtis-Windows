using TokenBar.Interop;

namespace TokenBar.Core.Tests;

/// <summary>The Stats "API-list-price equivalent" card's pure fold, ported from
/// TokenBarCore/UsageAttributionBreakdown.swift.</summary>
public class UsageAttributionBreakdownTests
{
    private static ModelReportEntry Entry(
        string client, string provider, string model, long total = 100, double cost = 1.0) =>
        new(client, model, provider, 0, 0, 0, 0, 0, total, 1, cost);

    [Fact]
    public void AssignedRowsAreGroupedPerConfirmedSubscriptionAndSortedByTarget()
    {
        ModelReportEntry[] entries =
        [
            Entry("claude", "anthropic", "m1", 10, 1.0),
            Entry("cc-mirror/foo", "anthropic", "m1", 5, 0.5),
            Entry("codex-cli", "openai", "gpt-5", 3, 0.3),
        ];
        UsageAttribution.Record[] confirmed =
        [
            new("claude", "anthropic", UsageAttribution.State.Assigned("claude")),
            new("cc-mirror/foo", "anthropic", UsageAttribution.State.Assigned("claude")),
            new("codex-cli", "openai", UsageAttribution.State.Assigned("codex")),
        ];

        var rows = UsageAttributionBreakdown.Rows(
            entries, ["claude", "cc-mirror/foo", "codex-cli"], confirmed);

        Assert.Collection(
            rows,
            row =>
            {
                // "claude" sorts before "codex" — sorted by target, and the two
                // sources routed to it are merged into one row.
                Assert.Equal(UsageAttribution.State.Assigned("claude"), row.State);
                Assert.Equal(15, row.Tokens);
                Assert.Equal(1.5, row.Cost, 10);
            },
            row =>
            {
                Assert.Equal(UsageAttribution.State.Assigned("codex"), row.State);
                Assert.Equal(3, row.Tokens);
                Assert.Equal(0.3, row.Cost, 10);
            });
    }

    [Fact]
    public void ExcludedRowsMergeIntoOneBucket()
    {
        ModelReportEntry[] entries =
        [
            Entry("cursor", "openai", "gpt-5", 10, 1.0),
            Entry("cursor", "anthropic", "claude-fable-5", 4, 0.4),
        ];
        UsageAttribution.Record[] confirmed =
        [
            new("cursor", "openai", UsageAttribution.State.Excluded),
            new("cursor", "anthropic", UsageAttribution.State.Excluded),
        ];

        var row = Assert.Single(UsageAttributionBreakdown.Rows(entries, ["cursor"], confirmed));

        Assert.Equal(UsageAttribution.State.Excluded, row.State);
        Assert.Equal(14, row.Tokens);
        Assert.Equal(1.4, row.Cost, 10);
    }

    [Fact]
    public void UnclassifiedSourcesFallIntoTheUnassignedBucket()
    {
        ModelReportEntry[] entries = [Entry("opencode", "moonshot", "kimi-k2", 8, 0.8)];

        var row = Assert.Single(UsageAttributionBreakdown.Rows(entries, ["opencode"], []));

        Assert.Equal(UsageAttribution.State.Unassigned, row.State);
        Assert.Equal(8, row.Tokens);
        Assert.Equal(0.8, row.Cost, 10);
    }

    [Fact]
    public void ClientIdsFilterDropsSourcesOutsideTheSelection()
    {
        ModelReportEntry[] entries =
        [
            Entry("claude", "anthropic", "m1", 10, 1.0),
            Entry("codex", "openai", "gpt-5", 5, 0.5),
        ];
        UsageAttribution.Record[] confirmed =
        [
            new("claude", "anthropic", UsageAttribution.State.Assigned("claude")),
            new("codex", "openai", UsageAttribution.State.Assigned("codex")),
        ];

        var row = Assert.Single(UsageAttributionBreakdown.Rows(entries, ["claude"], confirmed));

        Assert.Equal(UsageAttribution.State.Assigned("claude"), row.State);
        Assert.Equal(10, row.Tokens);
    }

    [Fact]
    public void EmptyEntriesAndZeroSourcesProduceNoRows()
    {
        Assert.Empty(UsageAttributionBreakdown.Rows([], ["claude"], []));

        // A source with zero tokens and zero cost is dropped before folding —
        // it must not surface as an empty assigned/excluded/unassigned row.
        ModelReportEntry[] zero = [Entry("claude", "anthropic", "m1", 0, 0)];
        Assert.Empty(UsageAttributionBreakdown.Rows(zero, ["claude"], []));
    }

    [Fact]
    public void RowOrderIsAssignedThenExcludedThenUnassigned()
    {
        ModelReportEntry[] entries =
        [
            Entry("opencode", "moonshot", "kimi-k2", 1, 0.1), // unassigned
            Entry("cursor", "openai", "gpt-5", 1, 0.1), // excluded
            Entry("claude", "anthropic", "m1", 1, 0.1), // assigned
        ];
        UsageAttribution.Record[] confirmed =
        [
            new("claude", "anthropic", UsageAttribution.State.Assigned("claude")),
            new("cursor", "openai", UsageAttribution.State.Excluded),
        ];

        var rows = UsageAttributionBreakdown.Rows(
            entries, ["opencode", "cursor", "claude"], confirmed);

        Assert.Collection(
            rows,
            row => Assert.Equal(UsageAttribution.State.Assigned("claude"), row.State),
            row => Assert.Equal(UsageAttribution.State.Excluded, row.State),
            row => Assert.Equal(UsageAttribution.State.Unassigned, row.State));
    }
}
