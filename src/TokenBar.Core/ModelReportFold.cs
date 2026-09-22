using TokenBar.Interop;

namespace TokenBar.Core;

/// <summary>
/// Folds provider-split <see cref="ModelReportEntry"/> rows back to the
/// <c>(client, model)</c> view every consumer actually wants (port of
/// TokenBarCore/ModelReport.swift's <c>modelLevelEntries</c>).
/// <para>
/// tokscale-core groups usage by <c>(client, provider, model)</c>, so one
/// model used through two providers (or a provider name that changed
/// mid-history) arrives as two entries. Left unfolded: a model shows twice in
/// the Models lens, <c>entries.Count</c> overstates how many models are in
/// use, and the largest single component — not the model — wins "Favorite
/// model" on Stats.
/// </para>
/// <para>
/// Extracted here rather than left at each call site (<c>DashboardView</c>,
/// <c>CostSurfaceProjection</c>) for the same reason as
/// <see cref="LazyLaneFold"/>: <c>DashboardView.xaml.cs</c> is compiled by no
/// test project, so a fold that only lived there would be unverifiable.
/// </para>
/// </summary>
public static class ModelReportFold
{
    /// <summary>The report's entries, folded to one row per
    /// <c>(client, model)</c>. Throughput (<see cref="ModelReportEntry.MsPer1kTokens"/>)
    /// is dropped on a merge: tokscale only computes it honestly over the
    /// complete per-provider rollup, so a folded row states it has none rather
    /// than keep one component's figure as if it described the whole.</summary>
    public static IReadOnlyList<ModelReportEntry> ModelLevelEntries(this ModelReport report)
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var folded = new List<ModelReportEntry>();

        foreach (var entry in report.Entries)
        {
            var key = entry.Client + "\u0000" + entry.Model;
            if (!indices.TryGetValue(key, out var index))
            {
                indices[key] = folded.Count;
                folded.Add(entry);
                continue;
            }

            var current = folded[index];
            var input = current.Input.SaturatingAdd(entry.Input);
            var output = current.Output.SaturatingAdd(entry.Output);
            var cacheRead = current.CacheRead.SaturatingAdd(entry.CacheRead);
            var cacheWrite = current.CacheWrite.SaturatingAdd(entry.CacheWrite);
            var reasoning = current.Reasoning.SaturatingAdd(entry.Reasoning);
            var messageSum = (long)current.MessageCount + entry.MessageCount;
            folded[index] = current with
            {
                Provider = MergedProviders(current.Provider, entry.Provider),
                Input = input,
                Output = output,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                Reasoning = reasoning,
                Total = input.SaturatingAdd(output).SaturatingAdd(cacheRead)
                    .SaturatingAdd(cacheWrite).SaturatingAdd(reasoning),
                MessageCount = messageSum > int.MaxValue ? int.MaxValue : (int)messageSum,
                Cost = current.Cost + entry.Cost,
                MsPer1kTokens = null,
                // Set explicitly, never left to `with`: an unset property would
                // keep the FIRST row's estimate while Cost above is the sum, and
                // cost/estimate would then describe two rows' spend against one
                // row's price — a ratio inflated by construction, i.e. a false
                // warning. All-or-nothing, as macOS: if any contributing row
                // could not be priced, the merged row has no estimate rather
                // than a partial denominator.
                CostEstimate = current.CostEstimate is { } a && entry.CostEstimate is { } b
                    ? a + b
                    : null,
            };
        }

        return folded;
    }

    /// <summary>The union of two rows' provider labels, as one sorted list.
    /// <para>
    /// Unspecified providers are dropped when any named one survives. The
    /// engine emits a bare model key alongside prefixed ones — see the
    /// `same-model` case in <c>crates/tb_core_ffi/src/model_report.rs</c>'s own
    /// fixture — which reaches here as an empty component. Kept, it sorts
    /// first and `string.Join` renders it as a leading separator:
    /// <c>", nvidia, openai"</c>, displayed verbatim by
    /// <c>DashboardView.ModelTip</c>.
    /// </para>
    /// <para>
    /// An all-unspecified merge still yields the empty string, because that is
    /// a true statement about those rows — the fold must not invent a provider
    /// name for usage the engine did not attribute to one.
    /// </para></summary>
    private static string MergedProviders(string first, string second)
    {
        var parts = first.Split(", ")
            .Concat(second.Split(", "))
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return string.Join(", ", parts);
    }
}
