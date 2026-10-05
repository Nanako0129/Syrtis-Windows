using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Feeds <see cref="WindowEquivalence.Cycle"/>'s <c>SpanTokens</c>/<c>SpanCost</c>
/// from the 5d-1 export and folds every window's admitted cycles into its
/// <see cref="WindowEquivalence.Row"/>.
/// <para>
/// No macOS file is snapshotted for this join — TokenBarCore's DashboardModel
/// wires it inline and was not part of this slice's snapshot. This is built
/// from the two pieces that are: <see cref="AttributedDailySeries.Points"/>'s
/// attribution join (<c>UsageAttribution.Resolve(client, provider, model,
/// confirmed)</c>), and <see cref="QuotaHistoryFold"/>'s own comments on the
/// span a cycle's delta describes and the <see cref="QuotaHistoryFold.Considered"/>
/// cap it names as belonging to "the admitted set behind the equivalence".
/// </para>
/// <para>
/// A message counts toward a window without any extra join table, because
/// <see cref="QuotaHistorySeries.ProviderId"/> is — despite the field name
/// inherited from the wire — already a registered CLIENT id: the
/// quota-tracked subscription owner (<c>QuotaHistoryFoldTests</c> decodes
/// series with <c>providerId: "codex"</c>). <see cref="UsageAttribution.Resolve"/>
/// resolves each message into that exact same id space via its
/// <c>Assigned(target)</c> case, so a message is this window's evidence
/// precisely when its resolved target equals the window's own
/// <see cref="QuotaHistorySeries.ProviderId"/>.
/// </para>
/// </summary>
public static class QuotaEquivalenceFold
{
    /// <summary>One message, resolved and sorted once per <see cref="Build"/>
    /// (or per public-API call, for callers that only have the raw list) so
    /// no downstream scan re-runs <see cref="UsageAttribution.Resolve"/> or
    /// re-derives <see cref="WindowEquivalence.RatioTokens"/> per cycle.
    /// Round 11's P1 finding: the naive per-cycle filter over the WHOLE
    /// message list made the fold <c>O(series × cycles × messages)</c>, and
    /// on the 10-second fast lane that re-ran on the UI thread every tick the
    /// Quota lens stayed open, unconditionally of whether the underlying
    /// snapshot had changed. This record plus <see cref="LowerBound"/> gives
    /// each cycle a contiguous slice instead — the same shape
    /// <see cref="QuotaHistoryFold.Rows"/> already uses for its own
    /// per-cycle join.</summary>
    private readonly record struct ResolvedMessage(
        long Timestamp, UsageAttribution.State State, long Tokens, double Cost);

    private static (IReadOnlyList<ResolvedMessage> Sorted, IReadOnlyList<long> Stamps) ResolveAndSort(
        IReadOnlyList<WindowMessage> messages, IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var sorted = messages
            .OrderBy(message => message.Timestamp)
            .Select(message => new ResolvedMessage(
                message.Timestamp,
                UsageAttribution.Resolve(message.Client, message.ProviderId, message.ModelId, confirmed),
                WindowEquivalence.RatioTokens(message),
                message.Cost))
            .ToList();
        return (sorted, [.. sorted.Select(message => message.Timestamp)]);
    }

    /// <summary>Each cycle's span restricted to messages attributed to
    /// <paramref name="providerId"/> — the window's own subscription-owner
    /// client id — and falling inside that cycle's own
    /// <see cref="QuotaCycle.FirstSampleMs"/>…<see cref="QuotaCycle.LastSampleMs"/>
    /// span, matching <see cref="WindowEquivalence.LiveRow"/>'s
    /// <c>timestamp &gt; first &amp;&amp; timestamp &lt;= last</c> rule.
    /// <para>
    /// Resolves and sorts <paramref name="messages"/> itself, once, since this
    /// public entry point does not have a pre-resolved list to reuse — see
    /// <see cref="Build"/>, which does the same work exactly once across every
    /// series and calls <see cref="CyclesCore"/> directly instead of this.
    /// </para></summary>
    public static IReadOnlyList<WindowEquivalence.Cycle> Cycles(
        IReadOnlyList<QuotaCycle> cycles,
        string providerId,
        IReadOnlyList<WindowMessage> messages,
        IReadOnlyList<UsageAttribution.Record> confirmed,
        string? modelScope)
    {
        // No default for the scope, as macOS QuotaHistoryFold.spans: a caller
        // that forgets it would silently count every model for a scoped
        // window, so leaving it out does not compile.
        var (sorted, stamps) = ResolveAndSort(QuotaHistoryFold.InScope(messages, modelScope), confirmed);
        return CyclesCore(cycles, providerId, sorted, stamps);
    }

    /// <summary>
    /// <see cref="Cycles"/>'s actual work, over an already resolved-and-sorted
    /// message list: a binary-search slice per cycle
    /// (<c>[FirstSampleMs, LastSampleMs]</c>, a safe superset — the `if` below
    /// states the exact <c>(first, last]</c> rule, the same superset-then-filter
    /// shape as <see cref="QuotaHistoryFold"/>'s own <c>SpanTotals</c>) instead
    /// of a full scan of every message per cycle.
    /// </summary>
    private static IReadOnlyList<WindowEquivalence.Cycle> CyclesCore(
        IReadOnlyList<QuotaCycle> cycles,
        string providerId,
        IReadOnlyList<ResolvedMessage> sorted,
        IReadOnlyList<long> stamps)
    {
        var result = new List<WindowEquivalence.Cycle>(cycles.Count);
        foreach (var cycle in cycles)
        {
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

                if (message.State.Kind != UsageAttribution.StateKind.Assigned || message.State.Target != providerId)
                {
                    continue;
                }

                tokens = tokens.SaturatingAdd(message.Tokens);
                cost += message.Cost;
            }

            result.Add(new WindowEquivalence.Cycle(
                DeltaPercent: cycle.UsedPercent,
                SpanTokens: tokens,
                SpanCost: cost,
                ObservedFraction: cycle.ObservedFraction,
                RisingRuns: cycle.RisingRuns));
        }

        return result;
    }

    /// <summary>First index whose value is &gt;= <paramref name="value"/>. Own
    /// copy of <c>QuotaHistoryFold</c>'s private helper of the same name and
    /// shape — that one is not reachable from here, and each fold over a
    /// timestamp-sorted message list owns this the same way.</summary>
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

    /// <summary>Every window's equivalence row, keyed the same way the strip
    /// and heatmap cards already key their own data
    /// (<see cref="QuotaWindowIdentity"/>). <paramref name="messages"/> is the
    /// whole 5d-1 export for the fetched range — unfiltered by window, since
    /// <see cref="Cycles"/> does that per window from the attribution join.
    /// <para>
    /// Resolves and sorts <paramref name="messages"/> exactly once for every
    /// series in <paramref name="history"/> — round 11's P1 fix. The previous
    /// version called the public, self-resolving <see cref="Cycles"/> and
    /// <see cref="Declared"/> once PER SERIES, each re-sorting and
    /// re-resolving the whole export; a store with several windows repeated
    /// that work per window on top of the per-cycle cost <see cref="CyclesCore"/>
    /// already fixed.
    /// </para>
    /// <para>
    /// <paramref name="scopeOf"/> answers a series' model scope (the caller
    /// passes <see cref="ModelScope.Of"/> over its provider, account and
    /// window key), so each window's span is narrowed to its own scope (macOS DashboardModel.swift:1836-1843). Only the span
    /// numerator is narrowed; <see cref="DeclaredCore"/> is unchanged.
    /// </para></summary>
    public static IReadOnlyDictionary<QuotaWindowIdentity, WindowEquivalence.Row> Build(
        IReadOnlyList<QuotaHistorySeries> history,
        IReadOnlyList<WindowMessage> messages,
        UsageAttribution.Table confirmed,
        Func<QuotaHistorySeries, string?> scopeOf)
    {
        var (sorted, stamps) = ResolveAndSort(messages, confirmed.Records);
        // A scoped series' span numerator reads the export narrowed by
        // QuotaHistoryFold.InScope, the one statement of the scope rule,
        // resolved and sorted once per distinct scope (a handful at most).
        var scoped = new Dictionary<string, (IReadOnlyList<ResolvedMessage>, IReadOnlyList<long>)>(
            StringComparer.Ordinal);
        var result = new Dictionary<QuotaWindowIdentity, WindowEquivalence.Row>();
        foreach (var series in history)
        {
            var id = new QuotaWindowIdentity(series.ProviderId, series.AccountScope, series.WindowKey);
            // The same cap the history card's message scan bounds itself by
            // (QuotaHistoryFold.Considered's own doc comment): the oldest
            // cycles beyond it are not part of "the admitted set behind the
            // equivalence" either.
            var considered = QuotaHistoryFold.Considered(QuotaHistoryFold.Cycles(series.Samples));
            var (spanSorted, spanStamps) = (sorted, stamps);
            if (scopeOf(series) is { } scope)
            {
                if (!scoped.TryGetValue(scope, out var narrowed))
                {
                    narrowed = ResolveAndSort(QuotaHistoryFold.InScope(messages, scope), confirmed.Records);
                    scoped[scope] = narrowed;
                }

                (spanSorted, spanStamps) = narrowed;
            }

            var spanCycles = CyclesCore(considered, series.ProviderId, spanSorted, spanStamps);
            // Per window, not per app: a user who classified their Codex
            // usage but never touched this window's own messages has still
            // declared nothing about THIS subscription's evidence, so a zero
            // span here means "unclassified", not "recorded as zero". Scoped
            // to series.ProviderId — see DeclaredCore's own doc comment for
            // why an unscoped scan over the shared time span is wrong once
            // two subscriptions' cycles overlap (round 11's P2 finding).
            var declared = DeclaredCore(considered, series.ProviderId, sorted, stamps);
            result[id] = WindowEquivalence.Aggregate(declared, spanCycles);
        }

        return result;
    }

    /// <summary>Whether this subscription's own admitted cycles have any
    /// evidence classified against THEM — assigned to <paramref name="providerId"/>
    /// itself, or explicitly excluded (which is subscription-agnostic: the
    /// user said "this is not part of any subscription", an answer that
    /// applies here as much as anywhere).
    /// <para>
    /// Deliberately NOT "assigned to a different subscription" — that used to
    /// count too, reasoning that reaching the evidence and routing it
    /// elsewhere still proves the window was seen. Round 11's P2 finding: once
    /// two subscriptions' cycles overlap in time (an ordinary session window
    /// and weekly window do), a message inside the shared span that was
    /// assigned to the OTHER subscription made THIS one read as declared even
    /// when every source actually relevant to it — same client, same
    /// provider — was still unassigned. <see cref="Cycles"/>'s own numerator
    /// already excludes that message (its target does not match
    /// <paramref name="providerId"/>); this check must exclude it from the
    /// declaration too, or <c>Aggregate</c> reports "recorded as zero"
    /// (<c>Unaccounted</c>) for a window nobody has actually classified
    /// anything in (<c>Undeclared</c>).
    /// </para></summary>
    public static bool Declared(
        IReadOnlyList<QuotaCycle> cycles,
        string providerId,
        IReadOnlyList<WindowMessage> messages,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var (sorted, stamps) = ResolveAndSort(messages, confirmed);
        return DeclaredCore(cycles, providerId, sorted, stamps);
    }

    private static bool DeclaredCore(
        IReadOnlyList<QuotaCycle> cycles,
        string providerId,
        IReadOnlyList<ResolvedMessage> sorted,
        IReadOnlyList<long> stamps) =>
        cycles.Any(cycle => DeclaredSpanCore(cycle.FirstSampleMs, cycle.LastSampleMs, providerId, sorted, stamps));

    /// <summary>
    /// <see cref="Declared"/>'s own per-cycle check, pulled out so the live
    /// window card — one running cycle, not a stored <see cref="QuotaCycle"/>
    /// list — can ask the identical question over its own sample span
    /// (<c>QuotaLensProjection.BuildClient</c>) instead of copying the scan.
    /// <paramref name="fromMs"/>/<paramref name="toMs"/> use the same
    /// <c>(from, to]</c> rule as <see cref="Cycles"/> and
    /// <see cref="WindowEquivalence.LiveRow"/>. <paramref name="providerId"/>
    /// carries the same scoping <see cref="Declared"/>'s own doc comment
    /// explains.
    /// </summary>
    public static bool DeclaredSpan(
        long fromMs,
        long toMs,
        string providerId,
        IReadOnlyList<WindowMessage> messages,
        IReadOnlyList<UsageAttribution.Record> confirmed)
    {
        var (sorted, stamps) = ResolveAndSort(messages, confirmed);
        return DeclaredSpanCore(fromMs, toMs, providerId, sorted, stamps);
    }

    private static bool DeclaredSpanCore(
        long fromMs,
        long toMs,
        string providerId,
        IReadOnlyList<ResolvedMessage> sorted,
        IReadOnlyList<long> stamps)
    {
        var lo = LowerBound(stamps, fromMs);
        var hi = LowerBound(stamps, toMs.SaturatingAdd(1));
        for (var index = lo; index < Math.Max(lo, hi); index++)
        {
            var message = sorted[index];
            if (message.Timestamp <= fromMs || message.Timestamp > toMs)
            {
                continue;
            }

            if (message.State.Kind == UsageAttribution.StateKind.Excluded)
            {
                return true;
            }

            if (message.State.Kind == UsageAttribution.StateKind.Assigned && message.State.Target == providerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bound a window-usage pass records for coverage
    /// (<c>LocalScan.Covers</c>): the real fetch bound, or — with no history to
    /// bound a fetch by, so nothing is scanned — <paramref name="nowMs"/>, which
    /// covers no past window.</summary>
    public static long ScanFromMs(IReadOnlyList<QuotaHistorySeries> history, long nowMs) =>
        history.Count == 0 ? nowMs : BoundFromMs(history, nowMs);

    /// <summary>The earliest instant any window's admitted cycles need
    /// messages for — the lower bound a caller should pass to
    /// <c>TbCore.WindowUsage</c>, so the export is not asked to scan further
    /// back than any card can use.
    /// <para>
    /// <see cref="QuotaCycle.EvidenceStartMs"/>, not
    /// <see cref="QuotaCycle.FirstSampleMs"/>: the same "evidence reaches back
    /// to whichever is earlier" rule <see cref="QuotaHistoryFold"/> already
    /// states for a provider that shortened its reported window mid-cycle.
    /// </para>
    /// <para>
    /// Also considers each series' placed running cycle
    /// (<see cref="QuotaHistoryFold.Active"/>), applying the same
    /// earlier-of-start-or-first-reading rule. <see cref="QuotaHistoryFold.Cycles"/>
    /// deliberately excludes the running cycle, so a series holding only its
    /// active cycle — a fresh install, or any window whose first cycle has not
    /// completed — has an empty <see cref="Considered"/> set; without this, the
    /// scan bound falls back to <paramref name="fallbackMs"/> (the caller's
    /// "now"), the requested window collapses to <c>[now, now)</c>, and
    /// <c>BuildWindowCard</c> — which draws the active cycle's usage from
    /// exactly these messages — shows no usage for a cycle that is plainly
    /// running. An unplaced active cycle (<see cref="QuotaActiveCycle.IsPlaced"/>
    /// false) contributes nothing: there is no start to bound a scan by.
    /// </para></summary>
    public static long BoundFromMs(IReadOnlyList<QuotaHistorySeries> history, long fallbackMs)
    {
        long? earliest = null;
        void Consider(long candidateMs)
        {
            if (earliest is null || candidateMs < earliest)
            {
                earliest = candidateMs;
            }
        }

        foreach (var series in history)
        {
            foreach (var cycle in QuotaHistoryFold.Considered(QuotaHistoryFold.Cycles(series.Samples)))
            {
                Consider(cycle.EvidenceStartMs);
            }

            if (QuotaHistoryFold.Active(series.Samples) is { IsPlaced: true } active)
            {
                var firstSampleMs = active.Samples[0].AtMs;
                Consider(Math.Min(active.StartMs!.Value, firstSampleMs));
            }
        }

        return earliest ?? fallbackMs;
    }

    /// <summary>The scan start the published account rows can vouch for. No
    /// account pass: the prior bound stands. A key whose rescan failed keeps
    /// its prior rows (scanned from the prior bound), so only then is the
    /// shared bound the later of the two, never over-claiming; a pass where
    /// every key rescanned takes the fresh bound, which may be earlier (a
    /// series whose evidence starts earlier joined the history).</summary>
    public static long? NextAccountBound(long? prior, long? fresh, bool scanned, bool keptPrior) =>
        !scanned ? prior
        : keptPrior && prior is { } p && fresh is { } f ? Math.Max(p, f)
        : fresh;
}
