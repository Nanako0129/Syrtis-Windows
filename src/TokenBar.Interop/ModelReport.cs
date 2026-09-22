namespace TokenBar.Interop;

// Per-model report (`ModelReport` in types.ts; Swift port:
// TokenBarCore/ModelReport.swift). Note: the wire key for throughput is
// `msPer1kTokens` (serde camelCase of ms_per_1k_tokens) — types.ts declares
// `msPer1KTokens` but the Rust serialization wins; the camelCase policy maps
// property `MsPer1kTokens` to exactly that.

public sealed record ModelReportEntry(
    string Client,
    string Model,
    string Provider,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning,
    long Total,
    int MessageCount,
    double Cost,
    double? MsPer1kTokens = null,
    // What the local pricing table would charge for these tokens (wire key
    // `costEstimate`), or null when it cannot price them. Evidence only: the
    // judgement is TokenBar.Core's CostPlausibility, taken on the row after
    // ModelReportFold has merged provider-split rows.
    double? CostEstimate = null);

public sealed record ModelReport(
    IReadOnlyList<ModelReportEntry> Entries,
    long TotalInput,
    long TotalOutput,
    long TotalCacheRead,
    long TotalCacheWrite,
    int TotalMessages,
    double TotalCost,
    // Unix-seconds time the LiteLLM pricing dataset was last fetched
    // (drives the "Prices updated …" hint). Absent before the first fetch.
    ulong? PricingUpdatedAt = null);
