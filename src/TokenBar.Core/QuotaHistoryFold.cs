using TokenBar.Interop;

namespace TokenBar.Core;

// Port of TokenBarCore/QuotaHistory.swift's cycle fold, comments included: they
// record the defect each rule exists to prevent.
//
// `rows`, `spans`, `spanTotals` and `inScope` — the message join — are ported
// below now that WindowMessage, UsageAttribution, WindowEquivalence and
// ModelScope all exist on Windows. Still NOT ported: QuotaOverviewFold.summaries,
// where the strip card's "ran out / never ran out" lives; this fold ships the
// PeakUsedPercent it will read.

/// <summary>One recorded reset cycle, derived from the persisted samples alone.</summary>
/// <param name="ResetAtMs">Reset instant in ms. Doubles as the cycle's identity —
/// the engine groups its samples by exactly this value.</param>
/// <param name="UsedPercent">
/// How much of the allowance this cycle consumed: the span between the lowest
/// and highest reading in it, NOT the highest reading alone.
/// <para>
/// The distinction is not cosmetic. Sampling starts whenever the app happens to
/// be running, so a cycle first observed at 40% used would report 40% as its
/// consumption when the app only witnessed the last few points of it. The span
/// is what this app can honestly claim to have seen.
/// </para>
/// <para>
/// Note the floor this leaves: the store rejects <c>usedPercent == 0</c>, so a
/// cycle's first reading is always above zero and the span necessarily omits
/// whatever was consumed before it. That understates, and understating is the
/// right direction — the alternative assumes the app witnessed a window it may
/// have joined late.
/// </para>
/// </param>
/// <param name="PeakUsedPercent">
/// The highest absolute reading seen in this cycle.
/// <para>
/// Separate from <paramref name="UsedPercent"/>, which is the observed SPAN. The
/// two answer different questions and only coincide when the app watched the
/// cycle from zero: a cycle first seen at 40% and last seen at 100% consumed 60
/// points as far as this machine can tell, and reached the ceiling. Deriving
/// "never ran out" from the span called that cycle a quiet one.
/// </para>
/// </param>
/// <param name="FirstSampleMs">First reading's instant. Carried because a ratio
/// is only meaningful when its numerator and denominator cover the same
/// interval, and the denominator is a span between two readings — not the whole
/// window. Usage from a stretch the app was not running for would otherwise be
/// counted against quota movement nobody observed.</param>
/// <param name="LastSampleMs">Last reading's instant, same reason.</param>
/// <param name="ObservedFraction">Fraction of the window the samples actually
/// cover, 0…1. A cycle observed for eight minutes of five hours is not evidence
/// about that cycle, and the UI has to be able to say so.</param>
/// <param name="RisingRuns">How many separately measured rises
/// <paramref name="UsedPercent"/> summed — see <see cref="QuotaHistoryFold.RisingRuns"/>.
/// Defaults to 1 so a caller that predates this field still compiles and
/// still gets the right answer for the ordinary case it was built for: a
/// cycle whose readings only rise is exactly one run.</param>
public sealed record QuotaCycle(
    long ResetAtMs,
    long StartMs,
    double UsedPercent,
    double PeakUsedPercent,
    int SampleCount,
    double ObservedFraction,
    long FirstSampleMs,
    long LastSampleMs,
    int RisingRuns = 1)
{
    /// <summary>
    /// How far back this cycle's evidence reaches — the earlier of where the
    /// window is computed to have started and where sampling actually began.
    /// <para>
    /// Normally <see cref="StartMs"/>, since the first reading lands after the
    /// window opens. They invert when the provider SHORTENS its reported
    /// duration mid-cycle: <see cref="StartMs"/> is derived from the newest
    /// sample's duration, so a window that went from seven days to five moves
    /// its own start forward, past readings already taken. Bounding a message
    /// scan at <see cref="StartMs"/> would then drop usage from before it while
    /// <see cref="UsedPercent"/> — the span between the first and last reading —
    /// still counts the movement those readings showed. Numerator short,
    /// denominator whole.
    /// </para>
    /// </summary>
    public long EvidenceStartMs => Math.Min(StartMs, FirstSampleMs);

    public long DurationMs => ResetAtMs - StartMs;
}

/// <summary>
/// The running cycle: where it sits on the clock, and every reading taken
/// inside it. See <see cref="QuotaHistoryFold.Active"/>.
/// <para>
/// <see cref="ResetAtMs"/> and <see cref="StartMs"/> are nullable together, and
/// that is the third outcome this type exists to carry: a window that IS
/// running but whose newest reading reports no usable duration cannot be
/// placed on an axis. "Nothing is running" and "something is running that we
/// cannot place" are different sentences on the card, and a record that could
/// only say "no cycle" would have merged them into the first — which is the
/// one that claims the user has stopped working.
/// </para>
/// </summary>
public sealed record QuotaActiveCycle(
    long? ResetAtMs, long? StartMs, IReadOnlyList<QuotaSample> Samples)
{
    /// <summary>True when the window has both ends and the geometry can be
    /// asked for a chart.</summary>
    public bool IsPlaced => ResetAtMs is not null && StartMs is not null;
}

/// <summary>One of this subscription's models inside one <see cref="QuotaHistoryRow"/>.</summary>
/// <param name="ProviderId">Carried alongside the model because
/// <see cref="ModelColorMap.Color"/> keys on the pair. Without it the segments
/// here would be coloured by a different rule than the same models in the
/// model breakdown and the usage chart, and one model would be two colours
/// depending on which card you were looking at.</param>
/// <param name="Breakdown"><paramref name="Tokens"/> split by class, on the
/// same five lanes the row's own <see cref="QuotaHistoryRow.MineBreakdown"/>
/// uses — a model line and the row above it are the same question at two
/// scopes, so a reader who can break one down and not the other is being told
/// the detail exists only sometimes.</param>
public sealed record QuotaHistoryModel(
    string ProviderId, string ModelId, long Tokens, TokenBreakdown Breakdown, double Cost)
{
    public string Id => $"{ProviderId}|{ModelId}";
}

/// <summary>A cycle plus what was spent inside it, split by whether it counted
/// against the subscription this history belongs to.</summary>
/// <param name="MineTokens">Attributed to THIS subscription — the number the
/// quota bar is about.</param>
/// <param name="MineTokensExCacheRead">
/// The same total on the BARS' basis — cache reads removed.
/// <para>
/// Not the equivalence's basis. <see cref="SpanTokens"/> below is the full
/// count, for the reason given there; this one matches the card geometry,
/// which sizes the bars from everything except cache reads because including
/// them decouples the bars from the quota line (measured: 4.5x vs 1.2x
/// discrimination). A figure printed beside the bars should be countable in
/// them.
/// </para>
/// </param>
/// <param name="SpanTokens">
/// The same two quantities restricted to the cycle's OBSERVED span, which is
/// the only interval the quota delta describes. The whole-window figures
/// above are what the row displays — "this window cost me X" is a question
/// about the window — while these are the only ones a ratio may divide.
/// <para>
/// The FULL token count, cache reads included, and that is the load-bearing
/// part. These two feed <see cref="WindowEquivalence"/>, which prints them on
/// one line as "10% of quota ~ X tokens · $Y API-equivalent" — two
/// descriptions of the same work, which a reader divides. <see cref="SpanCost"/>
/// is the message's whole priced cost, and a message's cost cannot be
/// decomposed here: <see cref="WindowMessage"/> carries one cost, not one per
/// token class. So a count excluding cache reads beside a cost including them
/// is the one pairing that cannot be made consistent, and it inflated the
/// implied per-token price by the cache-read share — most of a Claude Code
/// workload, which this repo's own bar comment puts at 200x the volume at a
/// tenth the price.
/// </para>
/// <para>
/// Full-and-full is therefore not a preference between two workable options;
/// it is the only achievable pairing until per-class cost crosses the FFI.
/// </para>
/// </param>
/// <param name="OtherTokens">
/// Everything else recorded in the same interval, summed. Not noise: it is
/// the answer to "the window barely moved, so where did the work go".
/// <para>
/// THREE attribution states, not one. A message lands here when the user
/// declared it against another subscription, when they declared it excluded,
/// and when they have not classified it at all — and calling the whole bucket
/// "other subscriptions" is a claim the user never made about any but the
/// first.
/// </para>
/// </param>
/// <param name="OtherHasAssigned">
/// Which of the three states are PRESENT in the bucket, as facts rather than
/// as totals.
/// <para>
/// Deliberately NOT a token count. The first version carried
/// <c>otherAssignedTokens</c> and the label compared it against
/// <see cref="OtherTokens"/> — which is a comparison in one dimension over
/// data that has two, so an unclassified row carrying cost and no tokens made
/// the totals equal and the whole bucket read as "other subscriptions" while
/// some of the spend was unclassified. A presence flag cannot be fooled by
/// which dimension a contribution happens to arrive in.
/// </para>
/// </param>
/// <param name="OtherHasExcluded">
/// Declared EXCLUDED by the user, kept apart from never-classified.
/// <para>
/// Both are "not this subscription's", so folding them into one flag and
/// labelling the result "Unclassified usage" tells the user their own
/// explicit exclusion was work they had not got round to classifying.
/// Excluding a source IS classifying it; the difference between "I dealt with
/// this" and "there is something here for you to deal with" is the only
/// thing this line is read for.
/// </para>
/// </param>
/// <param name="Models">This subscription's models, largest first.</param>
/// <param name="MineBreakdown">
/// <see cref="MineTokens"/> split by class, for the row's hover breakdown.
/// <para>
/// Five classes, because <see cref="WindowMessage.Tokens"/> sums exactly five
/// and <see cref="MineTokens"/> is its running total: a four-way split would
/// not add up to the number printed beside it, and a breakdown that does not
/// reconcile with the figure it explains is worse than none.
/// <see cref="TokenBreakdown.Total"/> exists so that identity can be asserted
/// rather than assumed.
/// </para>
/// <para>
/// Accumulated alongside <see cref="MineTokens"/> in the same loop, not
/// recovered from it by subtraction — the mistake <see cref="MineTokensExCacheRead"/>'s
/// own comment documents on a saturated row.
/// </para>
/// </param>
public sealed record QuotaHistoryRow(
    QuotaCycle Cycle,
    long MineTokens,
    long MineTokensExCacheRead,
    TokenBreakdown MineBreakdown,
    double MineCost,
    long SpanTokens,
    double SpanCost,
    long OtherTokens,
    double OtherCost,
    bool OtherHasAssigned,
    bool OtherHasExcluded,
    bool OtherHasUnattributed,
    IReadOnlyList<QuotaHistoryModel> Models)
{
    public long Id => Cycle.ResetAtMs;
}

public static class QuotaHistoryFold
{
    /// <summary>
    /// Groups samples into cycles, newest first.
    /// <para>
    /// <c>DurationSeconds</c> is per sample rather than per cycle, so the
    /// cycle's length is taken from its own newest sample: a window whose
    /// provider changed its reported duration mid-cycle should be placed by what
    /// it reports now, not by what it reported when sampling began.
    /// </para>
    /// <para>
    /// Completed cycles only. The running cycle is excluded, and each sample says
    /// for itself whether it is in it. Folding it in put the running cycle in a
    /// strip captioned "past windows", let a partially observed span stand beside
    /// completed ones, and let it count toward the three-cycle threshold the
    /// equivalence needs — an estimate that would then change under the reader as
    /// the cycle filled.
    /// </para>
    /// <para>
    /// The first version took the series' <c>activeResetAt</c> and compared it to
    /// each sample's <c>resetAt</c>. Those are not comparable values: the engine
    /// publishes the RAW provider reset there while every stored <c>resetAt</c>
    /// has been through <c>normalize_sample_reset</c>, so the exclusion silently
    /// did nothing whenever the provider's reset was off the quantum — which is
    /// most of the time. <c>IsActiveGroup</c> is the producer's own answer, so
    /// there is no longer a parameter a caller can omit and no rule stated twice
    /// in two languages.
    /// </para>
    /// <para>
    /// Returns EVERY recorded cycle. The cap that bounds the message scan is
    /// <see cref="Considered"/>, applied by the consumers that pay for a scan —
    /// not here. Putting it here looked like the tidier place ("bound it once, at
    /// the source") and was wrong: the overview fold derives LIFETIME facts from
    /// this list — peak percent, never-exhausted, cycle count — and costs no scan
    /// at all. A capped fold made a window that ran out thirty-three cycles ago
    /// report that it never had.
    /// </para>
    /// </summary>
    /// <summary>
    /// How much allowance a run of readings records consuming: the distance
    /// they travelled, summed over the rises between consecutive readings.
    /// <para>
    /// Neither the range nor the displacement, though on a cycle whose
    /// readings only rise all three are the same number. They stop agreeing
    /// the moment a reset falls inside the group: the readings return to
    /// zero, so the displacement collapses toward nothing while the range
    /// saturates at whatever the highest reading happened to be. Measured on
    /// a real merged group — 8 by displacement, 85 by range, 93 actually
    /// travelled. The numerator keeps every message from both sides of that
    /// reset whichever denominator is used, so one that does not accumulate
    /// makes the ratio report a multiple of the truth: 11.6x on that group,
    /// which is where a card reading $2974.92 came from.
    /// </para>
    /// <para>
    /// Only rises count, which keeps the property the range was chosen for.
    /// Sampling starts whenever the app happens to be running, so a cycle
    /// first seen at 40% must not claim the 40% nobody watched.
    /// </para>
    /// <para>
    /// Order-dependent where the range was not: <paramref name="readings"/>
    /// must already be sorted by sample time, or this silently returns a
    /// wrong answer.
    /// </para>
    /// <para>
    /// Every rise counts, including one that only undoes a provider's
    /// downward correction: <c>[0, 40, 35, 40]</c> returns 45 where 40 was
    /// consumed. That is a known overstatement and it is not fixable from
    /// the readings — a decline does not say whether the quota was refilled
    /// or the earlier number was wrong, and neither its size nor where it
    /// lands separates the two. Measured across every series in a real
    /// store: persistent drops run 2 to 100 points and bounce-backs 2 to 22,
    /// fully overlapping; real resets land anywhere from 0% to 21%, with
    /// bounce-backs landing at 0% as well.
    /// </para>
    /// <para>
    /// The numerator has the twin of this, and it is left in place for the
    /// same reason. A declining interval contributes nothing here while
    /// every message inside it still counts above the line, so whatever was
    /// consumed between the last reading before a reset and the reset
    /// itself is divided by the rises around it. Excluding those messages
    /// is right when the decline was a reset and wrong twice over when it
    /// was a correction — numerator down, denominator already up — and the
    /// paragraph above is the measurement saying the two cannot be told
    /// apart. Bound, from the same store: 2 of 128 groups contain any
    /// decline at all, declining intervals hold 0.17% of all observed time,
    /// 4.08% in the worst single group and 0.92% in the merged group this
    /// whole change was written for. Time, not messages — the message share
    /// is NOT measured, and a gap denser than average carries
    /// proportionally more.
    /// </para>
    /// <para>
    /// It is kept because the bound is small and the alternative costs
    /// more. Over 121 groups in that store, this and the range agreed on
    /// 119 — every cycle whose readings only rise — and differed by at most
    /// 3.5% on the two that had been refilled, where the range prices the
    /// window at its widest excursion rather than at what it consumed. The
    /// residual also leans the safe way: overstating the denominator
    /// understates the ratio, so a quota reads as worth less rather than
    /// more.
    /// </para>
    /// </summary>
    public static double Consumed(IReadOnlyList<double> readings)
    {
        var total = 0.0;
        for (var i = 1; i < readings.Count; i++)
        {
            total += Math.Max(0, readings[i] - readings[i - 1]);
        }

        return total;
    }

    /// <summary>
    /// How many separately measured rises <see cref="Consumed"/> summed: the
    /// number of times the readings started going up after not going up.
    /// Zero on readings that never rose.
    /// <para>
    /// The provider reports whole percents, so each rise carries the same
    /// ±0.5 quantisation whether it spans 3 points or 90. Summing two rises
    /// sums their uncertainty as well, and a ratio built on
    /// <c>[0, 3, 0, 3]</c> is not a 6-point measurement at ±0.5 — it is two
    /// 3-point measurements at ±0.5 each, neither of which clears the bar
    /// on its own. Callers that quote an error or gate on a minimum delta
    /// scale both by this count, so a group that crossed a reset does not
    /// come out looking more precise than a group that did not.
    /// </para>
    /// <para>
    /// A positive delta starts a run iff no earlier delta was positive, or
    /// the most recent non-zero delta before it was negative. A zero delta
    /// is transparent: a plateau inside a rise is still one measurement
    /// span under the ±0.5-per-reading model, so it neither starts nor ends
    /// a run. NOT <c>1 + declines</c> — macOS's first cut counted declines
    /// instead, and it charged a trailing decline as a run that contributed
    /// no rise.
    /// </para>
    /// </summary>
    public static int RisingRuns(IReadOnlyList<double> readings)
    {
        var runs = 0;
        var lastNonZeroWasNegative = true;
        for (var i = 1; i < readings.Count; i++)
        {
            var before = readings[i - 1];
            var after = readings[i];
            if (after > before)
            {
                if (lastNonZeroWasNegative)
                {
                    runs++;
                }

                lastNonZeroWasNegative = false;
            }
            else if (after < before)
            {
                lastNonZeroWasNegative = true;
            }
        }

        return runs;
    }

    public static IReadOnlyList<QuotaCycle> Cycles(IReadOnlyList<QuotaHistorySample> samples)
    {
        var grouped = new Dictionary<long, List<QuotaHistorySample>>();
        foreach (var sample in samples)
        {
            if (sample.IsActiveGroup)
            {
                continue;
            }

            if (!grouped.TryGetValue(sample.ResetAt, out var group))
            {
                grouped[sample.ResetAt] = group = [];
            }

            group.Add(sample);
        }

        var cycles = new List<QuotaCycle>(grouped.Count);
        foreach (var (resetAt, raw) in grouped)
        {
            var sorted = raw.OrderBy(sample => sample.SampledAt).ToList();
            var first = sorted[0];
            var last = sorted[^1];
            if (last.DurationSeconds <= 0)
            {
                continue;
            }

            var readings = sorted.Select(sample => sample.UsedPercent).ToList();
            var maximum = sorted.Max(sample => sample.UsedPercent);
            cycles.Add(new QuotaCycle(
                ResetAtMs: resetAt * 1000,
                StartMs: (resetAt - last.DurationSeconds) * 1000,
                UsedPercent: Consumed(readings),
                PeakUsedPercent: maximum,
                SampleCount: sorted.Count,
                ObservedFraction: Math.Clamp(
                    (double)(last.SampledAt - first.SampledAt) / last.DurationSeconds, 0, 1),
                FirstSampleMs: first.SampledAt * 1000,
                LastSampleMs: last.SampledAt * 1000,
                RisingRuns: RisingRuns(readings)));
        }

        return cycles.OrderByDescending(cycle => cycle.ResetAtMs).ToList();
    }

    /// <summary>
    /// The cycle still running, and the readings taken inside it — the half
    /// <see cref="Cycles"/> deliberately excludes.
    /// <para>
    /// The Session-window card draws exactly this: a line through the samples
    /// of the window the user is in right now. It is the same
    /// <c>IsActiveGroup</c> bit that keeps the running cycle out of the strip's
    /// "past windows", read the other way round, so the two surfaces cannot
    /// disagree about which cycle is current.
    /// </para>
    /// <para>
    /// Placement comes from the NEWEST active sample's own
    /// <c>ResetAt</c>/<c>DurationSeconds</c>, the same rule
    /// <see cref="Cycles"/> applies per cycle: a provider that changes its
    /// reported duration mid-window should have the window placed by what it
    /// reports now. Every active sample is carried, including any taken before
    /// such a shift — the geometry clips to the window bounds, and dropping a
    /// reading here would delete evidence the card is meant to show.
    /// </para>
    /// <para>Null only when nothing is running. A running window whose newest
    /// sample reports no usable duration comes back with its readings and no
    /// placement (<see cref="QuotaActiveCycle.IsPlaced"/> false), because an
    /// unplaceable window is not an idle one and the card has a different
    /// sentence for each.</para>
    /// </summary>
    public static QuotaActiveCycle? Active(IReadOnlyList<QuotaHistorySample> samples)
    {
        var active = samples
            .Where(sample => sample.IsActiveGroup)
            .OrderBy(sample => sample.SampledAt)
            .ToList();
        if (active.Count == 0)
        {
            return null;
        }

        var newest = active[^1];
        var placed = newest.DurationSeconds > 0;
        return new QuotaActiveCycle(
            ResetAtMs: placed ? newest.ResetAt * 1000 : null,
            StartMs: placed ? (newest.ResetAt - newest.DurationSeconds) * 1000 : null,
            Samples: active
                .Select(sample => new QuotaSample(sample.SampledAt * 1000, sample.UsedPercent))
                .ToList());
    }

    /// <summary>
    /// The newest cycles a scan-paying surface may look at. Applied by the
    /// consumers whose cost grows with the answer: the history card's cycle list,
    /// which bounds the union scan through its oldest entry's
    /// <see cref="QuotaCycle.EvidenceStartMs"/>, and the admitted set behind the
    /// equivalence. Lifetime summaries deliberately do not call this.
    /// </summary>
    public static IReadOnlyList<QuotaCycle> Considered(IReadOnlyList<QuotaCycle> cycles) =>
        cycles.Take(ConsideredCycles).ToList();

    /// <summary>
    /// How far back any cycle-derived surface reaches, in cycles.
    /// <para>
    /// The engine retains 128 cycles per series and this fold used to return all
    /// of them, but nothing downstream wants that many: the history card draws 12
    /// rows and the overview strip 16. The cost of the extra ones is not the
    /// list, it is that the OLDEST cycle sets where the message scan starts —
    /// <c>min(windowStart, cycles.last.startMs)</c> — so a 5-hour session window
    /// at 128 cycles asked for a 26-day scan to render twelve rows, and a weekly
    /// window walked that start backwards for ever.
    /// </para>
    /// <para>
    /// 32, not the 16 the issue proposed. A sweep over real history on
    /// 2026-08-21 put 16 on the cliff edge: two runs an hour apart, differing by
    /// one newly completed cycle, landed on opposite sides of "here is the
    /// number" and "we cannot say". 32 does not bite on any window there today
    /// (the widest has 27), which is the point: it bounds the growth without
    /// moving a number anyone reads.
    /// </para>
    /// </summary>
    public const int ConsideredCycles = 32;

    /// <summary>Grouping key for the model breakdown. The pair, not the model
    /// alone: the same model id reached through two providers is two rows
    /// everywhere else in this app, and collapsing them here would make this
    /// card the one place that disagrees.</summary>
    private readonly record struct ModelKey(string ProviderId, string ModelId);

    /// <summary>
    /// Joins each cycle to the messages inside it.
    /// <para>
    /// <paramref name="subscription"/> is the attribution target this history
    /// belongs to — the subscription whose quota the cycles measure. A message
    /// counts as "mine" only when the user's own declaration assigns it there;
    /// excluded and unassigned usage lands in the other column rather than
    /// being dropped, because an unclassified source still consumed real time
    /// in that window.
    /// </para>
    /// <para>
    /// <paramref name="modelScope"/> narrows the fold to the model a window's
    /// allowance counts, for a provider-scoped window like Claude's
    /// "Fable only" weekly limit, and has NO DEFAULT on purpose — see the note
    /// on <see cref="Spans"/>. Nil means the window is not scoped and every
    /// model counts.
    /// </para>
    /// <para>
    /// Applied HERE rather than by the caller, and that is the point of the
    /// parameter existing. The current-window chart, these history rows and
    /// the equivalence spans are three surfaces of one quota; narrowing only
    /// the chart made them disagree about the same window — corrected bars
    /// above, uncorrected "past windows" underneath. A rule the fold owns
    /// cannot be applied at two of three call sites.
    /// </para>
    /// </summary>
    public static IReadOnlyList<QuotaHistoryRow> Rows(
        IReadOnlyList<QuotaCycle> cycles,
        IReadOnlyList<WindowMessage> messages,
        string subscription,
        string? modelScope,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        // Sorted once, then each cycle takes a contiguous slice: the naive
        // filter-per-cycle is O(cycles x messages), and on live data that is
        // 15 x 45,844 walks of the whole array on the main actor.
        var sorted = InScope(messages, modelScope).OrderBy(message => message.Timestamp).ToList();
        var stamps = sorted.Select(message => message.Timestamp).ToList();
        var spans = SpanTotals(cycles, sorted, stamps, subscription, confirmed);

        var rows = new List<QuotaHistoryRow>(cycles.Count);
        for (var i = 0; i < cycles.Count; i++)
        {
            var cycle = cycles[i];
            var span = spans[i];

            // `[evidenceStart, reset)` — inclusive at the start, exclusive at
            // the reset. The start is `EvidenceStartMs` rather than `StartMs`
            // so this column cannot come out SMALLER than the span inside it
            // when a provider shortens its reported duration. The reset
            // instant is when the allowance refills, so work stamped exactly
            // there was charged to the cycle that instant OPENS, not the one
            // it closes. Adjacent cycles share that boundary, so getting it
            // wrong double counts rather than merely misfiling.
            var lo = LowerBound(stamps, cycle.EvidenceStartMs);
            var hi = LowerBound(stamps, cycle.ResetAtMs);

            long mineTokens = 0, mineExCacheRead = 0, otherTokens = 0;
            long mineInput = 0, mineOutput = 0, mineCacheRead = 0, mineCacheWrite = 0, mineReasoning = 0;
            var mineCost = 0.0;
            var otherCost = 0.0;
            bool otherHasAssigned = false, otherHasExcluded = false, otherHasUnattributed = false;
            var byModel = new Dictionary<ModelKey, (long Tokens, double Cost, TokenBreakdown Breakdown)>();

            for (var index = lo; index < Math.Max(lo, hi); index++)
            {
                var message = sorted[index];
                var state = UsageAttribution.Resolve(
                    message.Client, message.ProviderId, message.ModelId, confirmed);
                if (state.Kind == UsageAttribution.StateKind.Assigned && state.Target == subscription)
                {
                    // Saturating, like every other fold over these counters.
                    // Saturating per message is not enough: two rows that each
                    // saturate still trap when added together, and the
                    // accumulator is where a corrupt transcript would land.
                    mineTokens = mineTokens.SaturatingAdd(message.Tokens);
                    mineExCacheRead = mineExCacheRead.SaturatingAdd(message.TokensExCacheRead);
                    // Per class, alongside the totals rather than derived
                    // from them: message.Tokens is already a sum, and a split
                    // recovered by subtraction is the mistake
                    // MineTokensExCacheRead's own comment documents.
                    mineInput = mineInput.SaturatingAdd(message.Input);
                    mineOutput = mineOutput.SaturatingAdd(message.Output);
                    mineCacheRead = mineCacheRead.SaturatingAdd(message.CacheRead);
                    mineCacheWrite = mineCacheWrite.SaturatingAdd(message.CacheWrite);
                    mineReasoning = mineReasoning.SaturatingAdd(message.Reasoning);
                    mineCost += message.Cost;
                    var key = new ModelKey(message.ProviderId, message.ModelId);
                    var current = byModel.GetValueOrDefault(
                        key, (Tokens: 0L, Cost: 0.0, Breakdown: new TokenBreakdown(0, 0, 0, 0, 0)));
                    byModel[key] = (
                        current.Tokens.SaturatingAdd(message.Tokens),
                        current.Cost + message.Cost,
                        new TokenBreakdown(
                            current.Breakdown.Input.SaturatingAdd(message.Input),
                            current.Breakdown.Output.SaturatingAdd(message.Output),
                            current.Breakdown.CacheRead.SaturatingAdd(message.CacheRead),
                            current.Breakdown.CacheWrite.SaturatingAdd(message.CacheWrite),
                            current.Breakdown.Reasoning.SaturatingAdd(message.Reasoning)));
                }
                else
                {
                    // Three states reach here, and they mean three different
                    // things to the person reading the line: someone else's
                    // subscription, a source they excluded, and one they have
                    // not classified. Only the last is an open question.
                    switch (state.Kind)
                    {
                        case UsageAttribution.StateKind.Assigned:
                            otherHasAssigned = true;
                            break;
                        case UsageAttribution.StateKind.Excluded:
                            otherHasExcluded = true;
                            break;
                        default:
                            otherHasUnattributed = true;
                            break;
                    }

                    otherTokens = otherTokens.SaturatingAdd(message.Tokens);
                    otherCost += message.Cost;
                }
            }

            rows.Add(new QuotaHistoryRow(
                Cycle: cycle,
                MineTokens: mineTokens,
                MineTokensExCacheRead: mineExCacheRead,
                MineBreakdown: new TokenBreakdown(
                    mineInput, mineOutput, mineCacheRead, mineCacheWrite, mineReasoning),
                MineCost: mineCost,
                SpanTokens: span.Tokens,
                SpanCost: span.Cost,
                OtherTokens: otherTokens,
                OtherCost: otherCost,
                OtherHasAssigned: otherHasAssigned,
                OtherHasExcluded: otherHasExcluded,
                OtherHasUnattributed: otherHasUnattributed,
                Models: byModel
                    .Select(entry => new QuotaHistoryModel(
                        entry.Key.ProviderId, entry.Key.ModelId, entry.Value.Tokens,
                        entry.Value.Breakdown, entry.Value.Cost))
                    // Tokens, then cost, then the model key. Ordering on tokens
                    // alone leaves every cost-only model tied at zero, so their
                    // order came from dictionary iteration while the card
                    // renders `Take(4)` — the four shown could reshuffle
                    // between refreshes and omit the largest recorded spend.
                    // The key breaks the remaining ties so the order is stable
                    // rather than merely deterministic-per-run.
                    .OrderByDescending(model => model.Tokens)
                    .ThenByDescending(model => model.Cost)
                    .ThenBy(model => model.ProviderId, StringComparer.Ordinal)
                    .ThenBy(model => model.ModelId, StringComparer.Ordinal)
                    .ToList()));
        }

        return rows;
    }

    /// <summary>
    /// What this subscription spent inside each cycle's OBSERVED span —
    /// <c>(FirstSampleMs, LastSampleMs]</c>, the interval between the two
    /// readings the cycle's <see cref="QuotaCycle.UsedPercent"/> is the
    /// difference of.
    /// <para>
    /// One statement of that rule, for the two surfaces that need it: the
    /// history card's per-cycle numbers and the equivalence estimate's
    /// numerators.
    /// </para>
    /// <para>
    /// Returned parallel to <paramref name="cycles"/>, one entry each, so a
    /// caller that already has the sorted array pays no second sort.
    /// </para>
    /// <para>
    /// Same <paramref name="modelScope"/> contract as <see cref="Rows"/>: the
    /// equivalence estimate divides a quota delta by the usage that produced
    /// it, so counting a model the allowance does not charge inflates the
    /// denominator and understates the price of the quota.
    /// </para>
    /// <para>
    /// No default value, and the omission is the safeguard: a caller that
    /// forgets to state the scope gets a build error rather than silently
    /// measuring something else.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(long Tokens, double Cost)> Spans(
        IReadOnlyList<QuotaCycle> cycles,
        IReadOnlyList<WindowMessage> messages,
        string subscription,
        string? modelScope,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var sorted = InScope(messages, modelScope).OrderBy(message => message.Timestamp).ToList();
        return SpanTotals(
            cycles, sorted, sorted.Select(message => message.Timestamp).ToList(), subscription, confirmed);
    }

    /// <summary>The messages a scoped window may count. One statement of the
    /// rule, so the three surfaces of a window cannot apply it
    /// differently.</summary>
    public static IReadOnlyList<WindowMessage> InScope(IReadOnlyList<WindowMessage> messages, string? scope) =>
        scope is null ? messages : [.. messages.Where(message => ModelScope.Covers(scope, message.ModelId))];

    private static IReadOnlyList<(long Tokens, double Cost)> SpanTotals(
        IReadOnlyList<QuotaCycle> cycles,
        IReadOnlyList<WindowMessage> sorted,
        IReadOnlyList<long> stamps,
        string subscription,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var result = new List<(long Tokens, double Cost)>(cycles.Count);
        foreach (var cycle in cycles)
        {
            // Bounded by the SPAN, not by `[start, reset)`. The span is what
            // the denominator measures, and it is not always inside the
            // cycle: see `EvidenceStartMs`. The slice is a superset — the
            // `if` below states the actual rule — so the bounds only have to
            // be safe, and `SaturatingAdd` keeps the exclusive upper edge
            // from overflowing on a corrupt timestamp.
            var lo = LowerBound(stamps, cycle.FirstSampleMs);
            var hi = LowerBound(stamps, cycle.LastSampleMs.SaturatingAdd(1));
            long tokens = 0;
            var cost = 0.0;
            for (var index = lo; index < Math.Max(lo, hi); index++)
            {
                var message = sorted[index];
                if (message.Timestamp <= cycle.FirstSampleMs || message.Timestamp > cycle.LastSampleMs)
                {
                    continue;
                }

                var state = UsageAttribution.Resolve(
                    message.Client, message.ProviderId, message.ModelId, confirmed);
                if (state.Kind != UsageAttribution.StateKind.Assigned || state.Target != subscription)
                {
                    continue;
                }

                // Through `WindowEquivalence.RatioTokens`, which is where the
                // basis is stated. The live-window path reads the same
                // function, so the two surfaces cannot drift apart the way
                // they did when each summed for itself.
                tokens = tokens.SaturatingAdd(WindowEquivalence.RatioTokens(message));
                cost += message.Cost;
            }

            result.Add((tokens, cost));
        }

        return result;
    }

    /// <summary>First index whose value is &gt;= <paramref name="value"/>.</summary>
    private static int LowerBound(IReadOnlyList<long> values, long value)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (values[mid] < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
