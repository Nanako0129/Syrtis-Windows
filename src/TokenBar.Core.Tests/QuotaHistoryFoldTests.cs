using System.Text.Json;
using TokenBar.Interop;
using Xunit;

namespace TokenBar.Core.Tests;

// Expectations transcribed from TokenBarCore/QuotaHistory.swift and
// QuotaCurve.swift, not invented.
public class QuotaHistoryFoldTests
{
    private const long FiveHours = 5 * 3_600;
    private const long ResetAt = 1_767_330_000;

    private static QuotaHistorySample Sample(
        long resetAt, long sampledAt, double usedPercent, bool isActiveGroup = false) =>
        new(
            ResetAt: resetAt,
            DurationSeconds: FiveHours,
            DurationSource: QuotaHistoryDurationSource.Provider,
            UsedPercent: usedPercent,
            SampledAt: sampledAt,
            Origin: QuotaHistorySampleOrigin.LiveV3,
            IsActiveGroup: isActiveGroup);

    // "a cycle first seen at 40% and last seen at 100% consumed 60 points as far
    // as this machine can tell, and reached the ceiling. Deriving 'never ran out'
    // from the span called that cycle a quiet one."
    [Fact]
    public void SpanAndPeakAreNotInterchangeable()
    {
        var cycle = Assert.Single(QuotaHistoryFold.Cycles(
        [
            Sample(ResetAt, ResetAt - FiveHours + 600, 40),
            Sample(ResetAt, ResetAt - FiveHours + 3_600, 70),
            Sample(ResetAt, ResetAt - 600, 100),
        ]));

        Assert.Equal(60, cycle.UsedPercent);
        Assert.Equal(100, cycle.PeakUsedPercent);
        Assert.Equal(3, cycle.SampleCount);
        Assert.Equal(ResetAt * 1000, cycle.ResetAtMs);
        Assert.Equal((ResetAt - FiveHours) * 1000, cycle.StartMs);
        Assert.Equal((ResetAt - FiveHours + 600) * 1000, cycle.FirstSampleMs);
        Assert.Equal((ResetAt - 600) * 1000, cycle.LastSampleMs);
        Assert.Equal((double)(FiveHours - 1_200) / FiveHours, cycle.ObservedFraction, 9);
    }

    // "The running cycle is excluded, and each point says for itself whether it
    // is in it."
    [Fact]
    public void TheRunningCycleIsNotACycle()
    {
        var running = ResetAt + FiveHours;

        var cycles = QuotaHistoryFold.Cycles(
        [
            Sample(ResetAt, ResetAt - FiveHours + 600, 10),
            Sample(ResetAt, ResetAt - 600, 80),
            Sample(running, running - FiveHours + 600, 5, isActiveGroup: true),
            Sample(running, running - 600, 30, isActiveGroup: true),
        ]);

        var only = Assert.Single(cycles);
        Assert.Equal(ResetAt * 1000, only.ResetAtMs);
    }

    // "Groups curve points into cycles, newest first."
    [Fact]
    public void CyclesComeBackNewestFirst()
    {
        var older = ResetAt - FiveHours;

        var cycles = QuotaHistoryFold.Cycles(
        [
            Sample(older, older - FiveHours + 600, 20),
            Sample(older, older - 600, 25),
            Sample(ResetAt, ResetAt - FiveHours + 600, 10),
            Sample(ResetAt, ResetAt - 600, 80),
        ]);

        Assert.Equal(new[] { ResetAt * 1000, older * 1000 }, cycles.Select(cycle => cycle.ResetAtMs).ToArray());
    }

    // "Rust always emits it, so an absent key is ABI drift. Defaulting it to
    // false would render every point as finished history — the exact misreading
    // this field exists to remove, arriving silently instead of as a decode
    // failure."
    [Fact]
    public void APayloadMissingIsActiveGroupFailsToDecode()
    {
        Assert.Throws<JsonException>(() => TbCore.DecodeEnvelope<IReadOnlyList<QuotaHistorySeries>>(
            """
            {"ok":true,"data":[{"providerId":"codex","accountScope":"scope","windowKey":"weekly.v1",
              "samples":[{"resetAt":1767330000,"durationSeconds":18000,"durationSource":"provider",
                          "usedPercent":40.0,"sampledAt":1767320000,"origin":"liveV3"}]}]}
            """));
    }

    [Fact]
    public void TheFullPayloadDecodesThroughTheEnvelope()
    {
        var series = Assert.Single(TbCore.DecodeEnvelope<IReadOnlyList<QuotaHistorySeries>>(
            """
            {"ok":true,"data":[{"providerId":"codex","accountScope":"scope","windowKey":"weekly.v1",
              "samples":[{"resetAt":1767330000,"durationSeconds":18000,"durationSource":"observed",
                          "usedPercent":40.0,"sampledAt":1767320000,"origin":"importedV2",
                          "isActiveGroup":true}]}]}
            """));

        Assert.Equal("codex", series.ProviderId);
        Assert.Equal("scope", series.AccountScope);
        Assert.Equal("weekly.v1", series.WindowKey);
        var sample = Assert.Single(series.Samples);
        Assert.Equal(QuotaHistoryDurationSource.Observed, sample.DurationSource);
        Assert.Equal(QuotaHistorySampleOrigin.ImportedV2, sample.Origin);
        Assert.True(sample.IsActiveGroup);
    }

    // ---- the running cycle (5e) ----------------------------------------

    // The same IsActiveGroup bit, read the other way round: what Cycles leaves
    // out is exactly what the Session-window card draws.
    [Fact]
    public void ActiveIsTheCycleCyclesExcludes()
    {
        QuotaHistorySample[] samples =
        [
            Sample(ResetAt - FiveHours, ResetAt - FiveHours - 600, 55),
            Sample(ResetAt, ResetAt - 3_600, 20, isActiveGroup: true),
            Sample(ResetAt, ResetAt - 600, 40, isActiveGroup: true),
        ];

        Assert.Single(QuotaHistoryFold.Cycles(samples));
        var active = QuotaHistoryFold.Active(samples);

        Assert.NotNull(active);
        Assert.True(active!.IsPlaced);
        Assert.Equal(ResetAt * 1000, active.ResetAtMs);
        Assert.Equal((ResetAt - FiveHours) * 1000, active.StartMs);
        // Oldest first, in ms, so the geometry can walk them straight through.
        Assert.Equal([20d, 40d], active.Samples.Select(sample => sample.UsedPercent));
        Assert.Equal((ResetAt - 3_600) * 1000, active.Samples[0].AtMs);
    }

    [Fact]
    public void NoActiveSampleMeansNothingIsRunning() =>
        Assert.Null(QuotaHistoryFold.Active([Sample(ResetAt, ResetAt - 600, 40)]));

    // ---- the message join: Rows, Spans, InScope (PARITY-3b's deferred half) --

    private static WindowMessage Message(
        long timestampMs, string client, string provider, string model, long tokens, double cost) =>
        new(
            Timestamp: timestampMs,
            Client: client,
            ProviderId: provider,
            ModelId: model,
            Input: tokens,
            Output: 0,
            CacheRead: 0,
            CacheWrite: 0,
            Reasoning: 0,
            Cost: cost,
            IsTurnStart: true);

    private static QuotaCycle HistoryCycle(long firstMs, long lastMs, double usedPercent) =>
        new(
            ResetAtMs: lastMs + 1000,
            StartMs: firstMs - 1000,
            UsedPercent: usedPercent,
            PeakUsedPercent: usedPercent,
            SampleCount: 2,
            ObservedFraction: 1.0,
            FirstSampleMs: firstMs,
            LastSampleMs: lastMs);

    // "Deliberately NOT a token count... a presence flag cannot be fooled by
    // which dimension a contribution happens to arrive in." The defect this
    // guards against: an unclassified message carrying cost and no tokens
    // must not be invisible to the flag just because the token comparison
    // that broke this once would have called it zero.
    [Fact]
    public void AnUnattributedCostOnlyMessageStillSetsThePresenceFlag()
    {
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[]
        {
            // No confirmed record at all -> Unassigned. Cost-only: Input 0.
            new WindowMessage(1500, "unknown", "anthropic", "some-model", 0, 0, 0, 0, 0, 7.5, true),
        };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, []));

        Assert.True(row.OtherHasUnattributed);
        Assert.False(row.OtherHasAssigned);
        Assert.False(row.OtherHasExcluded);
        Assert.Equal(0, row.OtherTokens);
        Assert.Equal(7.5, row.OtherCost);
    }

    // Round-3 P1: the per-client lens must key `subscription` by the quota
    // OWNER (ClientRegistry.QuotaOwner), not the raw client id — a message
    // the user assigned to "antigravity" is that subscription's evidence
    // regardless of which CLI process logged it. Passing the raw
    // "antigravity-cli" id here — what DashboardView.Quota.cs's per-client
    // lens did before the fix — finds this row's own usage under nobody's
    // name and empties the whole card, which the second assertion pins.
    [Fact]
    public void RowsKeysOnTheQuotaOwnerNotTheRawClientId()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("antigravity-cli", "antigravity", UsageAttribution.State.Assigned("antigravity")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "antigravity-cli", "antigravity", "m1", 100, 1.0) };

        var owner = ClientRegistry.QuotaOwner("antigravity-cli");
        Assert.Equal("antigravity", owner);

        var byOwner = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, owner, null, confirmed));
        Assert.Equal(100, byOwner.MineTokens);
        Assert.Equal(1.0, byOwner.MineCost);
        Assert.Equal(0, byOwner.OtherTokens);

        // The bug: the raw id finds none of this window's own evidence, so
        // every message it should own falls through to "other" instead.
        var byRawId = Assert.Single(
            QuotaHistoryFold.Rows(cycles, messages, "antigravity-cli", null, confirmed));
        Assert.Equal(0, byRawId.MineTokens);
        Assert.Equal(100, byRawId.OtherTokens);
    }

    // A client whose owner equals its own id (no CLI/subscription split) must
    // see identical behaviour either way — the fix must not introduce a
    // second id space where none exists.
    [Fact]
    public void RowsIsUnaffectedWhenTheOwnerEqualsTheRawClientId()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("codex", "openai", UsageAttribution.State.Assigned("codex")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "codex", "openai", "m1", 100, 1.0) };

        var owner = ClientRegistry.QuotaOwner("codex");
        Assert.Equal("codex", owner);

        var byOwner = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, owner, null, confirmed));
        var byRawId = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "codex", null, confirmed));
        Assert.Equal(100, byOwner.MineTokens);
        Assert.Equal(byOwner.MineTokens, byRawId.MineTokens);
        Assert.Equal(byOwner.MineCost, byRawId.MineCost);
        Assert.Equal(byOwner.OtherTokens, byRawId.OtherTokens);
    }

    // Each state sets EXACTLY its own flag and no other — isolated, so a fold
    // that swapped which case sets which flag (Assigned setting
    // OtherHasExcluded, say) cannot pass by having both flags true from a mix
    // of states the way a test asserting only "both are true" would miss.
    [Fact]
    public void AnAssignedElsewhereMessageSetsOnlyOtherHasAssigned()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("elsewhere", "anthropic", UsageAttribution.State.Assigned("codex")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "elsewhere", "anthropic", "m1", 100, 1.0) };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.True(row.OtherHasAssigned);
        Assert.False(row.OtherHasExcluded);
        Assert.False(row.OtherHasUnattributed);
    }

    [Fact]
    public void AnExcludedMessageSetsOnlyOtherHasExcluded()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("excluded-source", "anthropic", UsageAttribution.State.Excluded),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "excluded-source", "anthropic", "m1", 100, 1.0) };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.False(row.OtherHasAssigned);
        Assert.True(row.OtherHasExcluded);
        Assert.False(row.OtherHasUnattributed);
    }

    [Fact]
    public void AnUnclassifiedMessageSetsOnlyOtherHasUnattributed()
    {
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "unknown", "anthropic", "m1", 100, 1.0) };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, []));

        Assert.False(row.OtherHasAssigned);
        Assert.False(row.OtherHasExcluded);
        Assert.True(row.OtherHasUnattributed);
    }

    // The three attribution states are independent flags, and a row can carry
    // more than one at once.
    [Fact]
    public void AllThreeOtherStatesAreDistinguishedInOneRow()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("elsewhere", "anthropic", UsageAttribution.State.Assigned("codex")),
            new("excluded-source", "anthropic", UsageAttribution.State.Excluded),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[]
        {
            Message(1500, "elsewhere", "anthropic", "m1", 100, 1.0), // assigned to codex
            Message(1500, "excluded-source", "anthropic", "m1", 100, 1.0), // excluded
            Message(1500, "unknown", "anthropic", "m1", 100, 1.0), // unassigned
            Message(1500, "mine", "anthropic", "m1", 100, 1.0), // no record -> unassigned too, mixed in
        };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.True(row.OtherHasAssigned);
        Assert.True(row.OtherHasExcluded);
        Assert.True(row.OtherHasUnattributed);
        Assert.Equal(0, row.MineTokens);
        Assert.Equal(400, row.OtherTokens);
    }

    // A row whose messages resolve entirely to this subscription carries none
    // of the "other" flags at all — the case the same-hours line must stay
    // silent for.
    [Fact]
    public void ARowWithNothingElseCarriesNoOtherFlags()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "mine", "anthropic", "m1", 100, 1.0) };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.False(row.OtherHasAssigned);
        Assert.False(row.OtherHasExcluded);
        Assert.False(row.OtherHasUnattributed);
        Assert.Equal(0, row.OtherTokens);
        Assert.Equal(0.0, row.OtherCost);
    }

    // "This subscription's models, largest first" — tokens, then cost, then
    // the (providerId, modelId) key, so a tie between cost-only models does
    // not depend on dictionary iteration order.
    [Fact]
    public void ModelsAreOrderedTokensThenCostThenKey()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[]
        {
            Message(1500, "mine", "anthropic", "small", 10, 1.0),
            Message(1500, "mine", "anthropic", "big", 1000, 1.0),
            Message(1500, "mine", "anthropic", "zero-b", 0, 5.0), // cost-only, tied on tokens at 0
            Message(1500, "mine", "anthropic", "zero-a", 0, 5.0), // ties on tokens AND cost -> key order
        };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.Equal(
            new[] { "big", "small", "zero-a", "zero-b" },
            row.Models.Select(m => m.ModelId).ToArray());
    }

    // A model scope that covers nothing leaves the row present — the cycle is
    // still real — but empty: no messages counted as this subscription's, and
    // no span evidence either. This is the "nothing charged to this
    // subscription" case the card's detail view must show copy for rather
    // than a bare empty list.
    [Fact]
    public void AScopeThatMatchesNoMessagesLeavesAnEmptyButPresentRow()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "mine", "anthropic", "claude-sonnet-5", 100, 1.0) };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", "fable", confirmed));

        Assert.Empty(row.Models);
        Assert.Equal(0, row.MineTokens);
        Assert.Equal(0, row.SpanTokens);
        Assert.Equal(0.0, row.SpanCost);
    }

    // The scope narrows every one of the three surfaces identically: Rows,
    // Spans and InScope must agree about which messages exist at all.
    [Fact]
    public void InScopeIsWhatRowsAndSpansBothFilterThrough()
    {
        var messages = new[]
        {
            Message(1500, "mine", "anthropic", "claude-fable-5", 100, 1.0),
            Message(1500, "mine", "anthropic", "claude-sonnet-5", 200, 2.0),
        };

        Assert.Equal(
            ["claude-fable-5"],
            QuotaHistoryFold.InScope(messages, "fable").Select(m => m.ModelId).ToArray());
        Assert.Equal(2, QuotaHistoryFold.InScope(messages, null).Count);
    }

    // Rows and Spans compute the SAME span figures — one statement of the
    // rule, read from two entry points.
    [Fact]
    public void RowsAndSpansAgreeOnTheSameSpanFigures()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[] { Message(1500, "mine", "anthropic", "m1", 100, 1.0) };

        var rows = QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed);
        var spans = QuotaHistoryFold.Spans(cycles, messages, "claude", null, confirmed);

        Assert.Equal(rows[0].SpanTokens, spans[0].Tokens);
        Assert.Equal(rows[0].SpanCost, spans[0].Cost);
    }

    // The span is restricted to the cycle's OWN observed sample interval,
    // (FirstSampleMs, LastSampleMs] — narrower than the whole-window mine
    // totals, which is why a row can show tokens without any span figure
    // behind the equivalence line.
    [Fact]
    public void MineTotalsCoverTheWholeWindowWhileSpanCoversOnlyTheObservedInterval()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[]
        {
            Message(900, "mine", "anthropic", "m1", 100, 1.0), // before EvidenceStartMs (0): still whole-window
            Message(1500, "mine", "anthropic", "m1", 100, 1.0), // inside the observed span too
        };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.Equal(200, row.MineTokens); // both messages: cycle's EvidenceStartMs is min(StartMs, FirstMs) = 0
        Assert.Equal(100, row.SpanTokens); // only the one strictly after FirstSampleMs
    }

    // The third outcome, and the one a nullable cycle alone would have merged
    // into "nothing is running": a window IS running and the provider reported
    // no usable duration to place it with.
    [Fact]
    public void ARunningWindowWithNoUsableDurationKeepsItsReadingsAndLosesItsPlacement()
    {
        var active = QuotaHistoryFold.Active(
        [
            Sample(ResetAt, ResetAt - 600, 40, isActiveGroup: true) with
            {
                DurationSeconds = 0,
            },
        ]);

        Assert.NotNull(active);
        Assert.False(active!.IsPlaced);
        Assert.Null(active.StartMs);
        Assert.Null(active.ResetAtMs);
        Assert.Single(active.Samples);
    }

    // ── Consumed / RisingRuns (distance travelled, not range or displacement) ──

    // The invariant that makes this whole change safe: a cycle whose readings
    // only rise has runs = 1, and Consumed equals both the range (max - min)
    // and the displacement (last - first). SpanAndPeakAreNotInterchangeable
    // above already pins this — [40, 70, 100] still reads 60 — this states it
    // directly for readings alone.
    [Fact]
    public void ConsumedOnAPurelyRisingSeriesEqualsRangeAndDisplacement()
    {
        double[] readings = [40, 70, 100];
        Assert.Equal(60, QuotaHistoryFold.Consumed(readings));
        Assert.Equal(1, QuotaHistoryFold.RisingRuns(readings));
    }

    // The doc comment's own worked example: a rise that only undoes a
    // provider's downward correction still counts in full. Known
    // overstatement — 45 where 40 was actually consumed — and not fixable
    // from the readings alone.
    [Fact]
    public void ConsumedOverstatesWhenARiseUndoesADownwardCorrection()
    {
        double[] readings = [0, 40, 35, 40];
        Assert.Equal(45, QuotaHistoryFold.Consumed(readings));
        Assert.Equal(2, QuotaHistoryFold.RisingRuns(readings)); // 0->40, then 35->40
    }

    // The distinguishing case that disagreed between the live row and the
    // pooled aggregate on macOS before this fix: two 3-point rises separated
    // by a decline back to zero. Consumed sums both rises (6), but each rise
    // is separately quantised, so RisingRuns must report 2, not 1 — a single
    // 6-point measurement at +/-0.5 is a materially different claim from two
    // 3-point measurements at +/-0.5 each.
    [Fact]
    public void TwoSeparatedRisesCountAsTwoRunsNotOne()
    {
        double[] readings = [0, 3, 0, 3];
        Assert.Equal(6, QuotaHistoryFold.Consumed(readings));
        Assert.Equal(2, QuotaHistoryFold.RisingRuns(readings));
    }

    // A plateau inside a rise is transparent: it neither starts nor ends a
    // run. Deltas here are +3, 0, 0, +3 — one continuous rise, not two.
    [Fact]
    public void APlateauInsideARiseIsOneRunNotTwo()
    {
        double[] readings = [0, 3, 3, 3, 6];
        Assert.Equal(6, QuotaHistoryFold.Consumed(readings));
        Assert.Equal(1, QuotaHistoryFold.RisingRuns(readings));
    }

    // RisingRuns is NOT `1 + declines`. A single rise followed by a trailing
    // decline is one run that contributed a rise, not two — macOS's first
    // cut counted declines instead and got this case wrong.
    [Fact]
    public void RisingRunsIsNotOnePlusDeclines()
    {
        double[] readings = [0, 10, 5];
        Assert.Equal(10, QuotaHistoryFold.Consumed(readings));
        Assert.Equal(1, QuotaHistoryFold.RisingRuns(readings));
    }

    [Fact]
    public void ReadingsThatNeverRiseHaveZeroRisingRuns()
    {
        double[] readings = [10, 10, 5, 5];
        Assert.Equal(0, QuotaHistoryFold.Consumed(readings));
        Assert.Equal(0, QuotaHistoryFold.RisingRuns(readings));
    }

    // Cycles() itself, not just the standalone functions: a cycle whose
    // middle reading dips (a correction, not a reset) must report the
    // travelled distance and the run count Consumed/RisingRuns compute, not
    // the old range (40) or displacement (40) — both of which this exact
    // reading sequence would have agreed on before this change.
    [Fact]
    public void CyclesReportsTravelledDistanceAndRunsAcrossADecline()
    {
        var cycle = Assert.Single(QuotaHistoryFold.Cycles(
        [
            Sample(ResetAt, ResetAt - FiveHours + 600, 0),
            Sample(ResetAt, ResetAt - FiveHours + 1_200, 40),
            Sample(ResetAt, ResetAt - FiveHours + 1_800, 35),
            Sample(ResetAt, ResetAt - 600, 40),
        ]));

        Assert.Equal(45, cycle.UsedPercent); // 40 + max(0, -5) + 5 = 45, not 40
        Assert.Equal(2, cycle.RisingRuns);
        Assert.Equal(40, cycle.PeakUsedPercent); // peak is untouched by this change
    }

    // ---- the hover breakdown: MineBreakdown / QuotaHistoryModel.Breakdown ---

    // Five DISTINCT non-zero values, so a fold that reads one field twice (or
    // swaps two lanes) cannot pass by arithmetic coincidence.
    private static WindowMessage FullMessage(
        long timestampMs, string client, string provider, string model,
        long input, long output, long cacheRead, long cacheWrite, long reasoning, double cost) =>
        new(
            Timestamp: timestampMs,
            Client: client,
            ProviderId: provider,
            ModelId: model,
            Input: input,
            Output: output,
            CacheRead: cacheRead,
            CacheWrite: cacheWrite,
            Reasoning: reasoning,
            Cost: cost,
            IsTurnStart: true);

    [Fact]
    public void MineBreakdownAccumulatesEachClassIntoItsOwnLane()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[]
        {
            FullMessage(1200, "mine", "anthropic", "m1", 1, 20, 300, 4_000, 50_000, 1.0),
            FullMessage(1600, "mine", "anthropic", "m1", 2, 30, 400, 5_000, 60_000, 2.0),
        };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.Equal(3, row.MineBreakdown.Input);
        Assert.Equal(50, row.MineBreakdown.Output);
        Assert.Equal(700, row.MineBreakdown.CacheRead);
        Assert.Equal(9_000, row.MineBreakdown.CacheWrite);
        Assert.Equal(110_000, row.MineBreakdown.Reasoning);

        // The five lanes sum to the row's own printed total — the identity
        // the breakdown exists to reconcile with, not two derived values
        // agreeing with each other by construction.
        Assert.Equal(row.MineTokens, row.MineBreakdown.Total);
        Assert.Equal(119_753, row.MineTokens); // literal, so the identity above
                                                // is not two derived values.

        var model = Assert.Single(row.Models);
        Assert.Equal(row.MineBreakdown, model.Breakdown);
        Assert.Equal(model.Tokens, model.Breakdown.Total);
    }

    [Fact]
    public void PerModelBreakdownSumsToTheModelsOwnTokenTotal()
    {
        var confirmed = new List<UsageAttribution.Record>
        {
            new("mine", "anthropic", UsageAttribution.State.Assigned("claude")),
        };
        var cycles = new[] { HistoryCycle(1000, 2000, 40) };
        var messages = new[]
        {
            FullMessage(1200, "mine", "anthropic", "model-a", 1, 20, 300, 4_000, 50_000, 1.0),
            FullMessage(1300, "mine", "anthropic", "model-b", 9, 8, 7, 6, 5, 0.5),
        };

        var row = Assert.Single(QuotaHistoryFold.Rows(cycles, messages, "claude", null, confirmed));

        Assert.Equal(2, row.Models.Count);
        foreach (var model in row.Models)
        {
            Assert.Equal(model.Tokens, model.Breakdown.Total);
        }
    }
}
