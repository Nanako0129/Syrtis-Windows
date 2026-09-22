using TokenBar.Core;
using TokenBar.Interop;

// End-to-end check of the Rust↔C# seam: load the cdylib and exercise every
// FFI entry point, printing a one-line summary each. The macOS counterpart is
// `swift run TokenBar --smoke`. Exit 0 only if every exercised entry decoded.
//
// Env knobs:
//   TB_SMOKE_MIN_MESSAGES  fail unless tb_probe parses at least N messages
//                          (CI points HOME at a hermetic fixture tree)
//   TB_SMOKE_SKIP_NETWORK  "1" skips tb_agent_usage (network-bound, ~30s
//                          worst case; useless on hosts with no credentials)
//   TB_SMOKE_EXPECT_CLIENT assert that every local report exposes this client
//                          (CI uses "codex" to prove relocated roots are read)

var minRaw = Environment.GetEnvironmentVariable("TB_SMOKE_MIN_MESSAGES");
long min = 0;
if (!string.IsNullOrEmpty(minRaw) && !long.TryParse(minRaw, out min))
{
    // A set-but-unparseable expectation must fail loudly, not silently
    // degrade the hermetic assertion to "no crash".
    Console.Error.WriteLine($"TB_SMOKE_MIN_MESSAGES is not a number: '{minRaw}'");
    return 2;
}

var skipNetwork = Environment.GetEnvironmentVariable("TB_SMOKE_SKIP_NETWORK") == "1";
var expectedClient = Environment.GetEnvironmentVariable("TB_SMOKE_EXPECT_CLIENT")?.Trim();
if (string.IsNullOrEmpty(expectedClient))
    expectedClient = null;
var failed = 0;
double? expectedClientTokensPerMin = null;

void Step(string name, Func<string> body)
{
    try
    {
        Console.WriteLine($"{name,-18} {body()}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"{name,-18} FAILED: {ex.Message}");
    }
}

try
{
    _ = TbCore.SourceContextId();
    Console.WriteLine("source-context:v1 valid");
}
catch
{
    failed++;
    Console.WriteLine("source-context:v1 FAILED");
}

long probeMessages = -1;
Step("tb_probe", () =>
{
    var probe = TbCore.Probe();
    probeMessages = probe.Messages;
    return $"ok messages={probe.Messages}";
});

Step("tb_graph", () =>
{
    var g = TbCore.Graph();
    if (expectedClient is not null && !g.Summary.Clients.Contains(expectedClient))
        throw new InvalidOperationException(
            $"tb_graph expected client '{expectedClient}' in Summary.Clients");
    return $"ok days={g.Contributions.Count} activeDays={g.Summary.ActiveDays} " +
           $"totalTokens={g.Summary.TotalTokens} totalCost={g.Summary.TotalCost:F2}";
});

Step("tb_graph_local_first", () =>
{
    var g = TbCore.GraphLocalFirst();
    if (expectedClient is not null && !g.Summary.Clients.Contains(expectedClient))
        throw new InvalidOperationException(
            $"tb_graph_local_first expected client '{expectedClient}' in Summary.Clients");
    return $"ok days={g.Contributions.Count} totalTokens={g.Summary.TotalTokens} " +
           $"mode={g.Meta.PricingMode} coverage={g.Meta.CostCoverage}";
});

Step("tb_refresh_graph", () =>
{
    var g = TbCore.RefreshGraph();
    return $"ok days={g.Contributions.Count} totalTokens={g.Summary.TotalTokens}";
});

Step("tb_model_report", () =>
{
    var r = TbCore.ModelReport();
    if (expectedClient is not null && !r.Entries.Any(e => e.Client == expectedClient))
        throw new InvalidOperationException(
            $"tb_model_report expected client '{expectedClient}' in Entries");
    // Implausible-cost probe. Every folded (client, model) row whose reported
    // cost the local estimate cannot justify, judged with the same
    // ModelLevelEntries fold and CostPlausibility rule the Models lens uses,
    // and named by the canonical client the lens's tabs are keyed on. All-time
    // (this call passes no year): compare it against the lens with the year
    // picker on All years, or the two are looking at different slices.
    var flagged = r.ModelLevelEntries()
        .Select(e => (Entry: e, Ratio: CostPlausibility.ImplausibleRatio(e)))
        .Where(x => x.Ratio is not null)
        .Select(x => $"{ClientRegistry.CanonicalClient(x.Entry.Client)}/"
            + $"{x.Entry.Model}@{Format.CompactRatio(x.Ratio!.Value)}")
        .ToList();
    var estimated = r.Entries.Count(e => e.CostEstimate is not null);
    return $"ok entries={r.Entries.Count} totalCost={r.TotalCost:F2} " +
           $"pricingUpdatedAt={(r.PricingUpdatedAt?.ToString() ?? "-")} " +
           $"estimated={estimated} " +
           $"flagged={(flagged.Count == 0 ? "none" : string.Join(",", flagged))}";
});

Step("tb_hourly_report", () =>
{
    var r = TbCore.HourlyReport();
    // Exercise the clients-filter marshaling path too: a single-client slice
    // must never cost more than the unfiltered report.
    var filtered = TbCore.HourlyReport(clients: ["claude"]);
    if (filtered.TotalCost > r.TotalCost)
        throw new InvalidOperationException(
            $"filtered cost {filtered.TotalCost} exceeds unfiltered {r.TotalCost}");
    if (expectedClient is not null &&
        !r.Entries.SelectMany(e => e.Clients).Contains(expectedClient))
        throw new InvalidOperationException(
            $"tb_hourly_report expected client '{expectedClient}' in Entries[].Clients");
    if (expectedClient is not null)
    {
        var allMessages = r.Entries.Sum(e => e.MessageCount);
        var claudeMessages = filtered.Entries.Sum(e => e.MessageCount);
        if (claudeMessages >= allMessages)
            throw new InvalidOperationException(
                $"tb_hourly_report expected client '{expectedClient}'; " +
                $"Claude-only MessageCount {claudeMessages} must be less than unfiltered {allMessages}");
    }
    return $"ok entries={r.Entries.Count} totalCost={r.TotalCost:F2} " +
           $"claudeOnlyCost={filtered.TotalCost:F2}";
});

Step("tb_agents_report", () =>
{
    var r = TbCore.AgentsReport();
    var filtered = TbCore.AgentsReport(clients: ["claude"]);
    if (filtered.TotalMessages > r.TotalMessages)
        throw new InvalidOperationException(
            $"filtered messages {filtered.TotalMessages} exceed unfiltered {r.TotalMessages}");
    if (expectedClient is not null &&
        !r.Entries.SelectMany(e => e.Clients).Contains(expectedClient))
        throw new InvalidOperationException(
            $"tb_agents_report expected client '{expectedClient}' in Entries[].Clients");
    if (expectedClient is not null && filtered.TotalMessages >= r.TotalMessages)
        throw new InvalidOperationException(
            $"tb_agents_report expected client '{expectedClient}'; " +
            $"Claude-only TotalMessages {filtered.TotalMessages} must be less than unfiltered {r.TotalMessages}");
    return $"ok entries={r.Entries.Count} totalMessages={r.TotalMessages} " +
           $"claudeOnlyMessages={filtered.TotalMessages}";
});

Step("tb_filter_parity", () => TbCore.FilterParityProbe().SmokeSummary);

Step("tb_usage_trace", () =>
{
    var buckets = TbCore.UsageTrace(600);
    if (expectedClient is not null)
    {
        var matching = buckets.Where(b => b.Client == expectedClient).ToList();
        if (matching.Count == 0)
            throw new InvalidOperationException(
                $"tb_usage_trace expected client '{expectedClient}' in current window");
        var unexpectedClients = buckets
            .Where(b => b.Client != expectedClient)
            .Select(b => b.Client)
            .Distinct()
            .ToList();
        if (unexpectedClients.Count > 0)
            throw new InvalidOperationException(
                $"tb_usage_trace expected only client '{expectedClient}' in current window; " +
                $"found {string.Join(", ", unexpectedClients)}");
        expectedClientTokensPerMin = matching.Sum(b => b.TokensPerMin);
        if (expectedClientTokensPerMin is not > 0)
            throw new InvalidOperationException(
                $"tb_usage_trace expected client '{expectedClient}' with TokensPerMin > 0");
    }
    return $"ok buckets={buckets.Count}";
});

Step("tb_tokens_per_min", () =>
{
    var rate = TbCore.TokensPerMin();
    if (expectedClient is not null)
    {
        if (expectedClientTokensPerMin is not > 0)
            throw new InvalidOperationException(
                $"tb_tokens_per_min expected a positive '{expectedClient}' trace rate");
        var tolerance = Math.Max(0.1, Math.Abs(expectedClientTokensPerMin.Value) * 0.000001);
        if (!(rate > 0) || Math.Abs(rate - expectedClientTokensPerMin.Value) > tolerance)
            throw new InvalidOperationException(
                $"tb_tokens_per_min rate {rate:F1} does not match " +
                $"'{expectedClient}' trace rate {expectedClientTokensPerMin.Value:F1}");
    }
    return $"ok rate={rate:F1}";
});

if (skipNetwork)
{
    Console.WriteLine($"{"tb_agent_usage",-18} skipped (TB_SMOKE_SKIP_NETWORK=1)");
}
else
{
    Step("tb_agent_usage", () =>
    {
        var payload = TbCore.AgentUsage();
        var agents = string.Join(", ", payload.Agents.Select(a =>
            $"{a.ClientId}:{(a.Source is "cli" or "oauth" ? a.Source : "other")}:{(a.Error is null ? $"{a.Windows.Count}win" : "err")}"));
        return $"ok agents=[{agents}]";
    });
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} entry point(s) failed");
    return 1;
}

if (probeMessages < min)
{
    Console.Error.WriteLine($"tb_probe messages={probeMessages} < expected {min}");
    return 1;
}

return 0;
