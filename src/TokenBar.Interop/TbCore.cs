using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenBar.Interop;

public sealed class TbCoreException(string message) : Exception(message);

/// <summary>
/// Typed facade over the tb_core_ffi C ABI. Mirrors TBCore.swift in the macOS
/// repo: every entry point returns heap JSON that must be freed with tb_free
/// exactly once, wrapped in the envelope {"ok":true,"data":…} /
/// {"ok":false,"err":…} (tb_probe keeps its legacy top-level shape).
/// All calls are blocking — invoke off the UI thread; AgentUsage() is also
/// network-bound (~30s worst case).
/// </summary>
public static class TbCore
{
    // Required-parameter enforcement mirrors Swift Decodable's strictness:
    // a wire payload missing a non-optional field fails the decode loudly
    // instead of silently binding 0/null (nullable DTO params carry `= null`
    // defaults, so omitted optionals still decode like Swift optionals).
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters =
        {
            new JsonStringEnumConverter<FilterParityStatus>(allowIntegerValues: false),
            new JsonStringEnumConverter<PricingMode>(allowIntegerValues: false),
            new JsonStringEnumConverter<CostCoverage>(allowIntegerValues: false),
            new JsonStringEnumConverter<QuotaHistoryDurationSource>(allowIntegerValues: false),
            new JsonStringEnumConverter<QuotaHistorySampleOrigin>(allowIntegerValues: false),
        },
    };

    public static ProbeResult Probe()
    {
        var json = TakeString(NativeMethods.tb_probe());
        var probe = JsonSerializer.Deserialize<ProbeResult>(json, JsonOpts)
            ?? throw new TbCoreException("tb_probe returned null JSON");
        if (!probe.Ok)
        {
            throw new TbCoreException(probe.Err ?? "tb_probe reported ok=false");
        }

        return probe;
    }

    /// <summary>Contribution graph for <paramref name="year"/> (null = all
    /// time). Served from a &lt;=30s cache inside the cdylib when warm.</summary>
    public static UsagePayload Graph(string? year = null) =>
        Unwrap<UsagePayload>(NativeMethods.tb_graph(year));

    /// <summary>
    /// Contribution graph from the local scan for this call only. This is a
    /// blocking off-UI-thread operation; it bypasses outbound pricing
    /// resolution and the authoritative year-only graph cache.
    /// </summary>
    public static UsagePayload GraphLocalFirst(string? year = null) =>
        Unwrap<UsagePayload>(NativeMethods.tb_graph_local_first(year));

    /// <summary>Contribution graph, always recomputed.</summary>
    public static UsagePayload RefreshGraph(string? year = null) =>
        Unwrap<UsagePayload>(NativeMethods.tb_refresh_graph(year));

    public static ModelReport ModelReport(string? year = null) =>
        Unwrap<ModelReport>(NativeMethods.tb_model_report(year));

    /// <summary>Per-hour report for <paramref name="year"/> (null = all time),
    /// restricted to <paramref name="clients"/> (null/empty = all clients).
    /// The core filters at the streaming scan, so a client slice yields
    /// accurate per-client totals for hours shared across clients (a
    /// downstream membership filter cannot — buckets fold all clients into
    /// one mixed total). NOTE: an empty selection therefore reaches the core
    /// as "all clients", not "no clients" — DashboardModel must publish an
    /// explicit empty report without calling this method.</summary>
    public static HourlyReport HourlyReport(string? year = null, IReadOnlyList<string>? clients = null) =>
        Unwrap<HourlyReport>(NativeMethods.tb_hourly_report(year, JoinClients(clients)));

    /// <summary>Per-agent report for <paramref name="year"/> (null = all
    /// time), restricted to <paramref name="clients"/> (null/empty = all
    /// clients). Scan-level filter, same rationale as
    /// <see cref="HourlyReport"/>.</summary>
    public static AgentsReport AgentsReport(string? year = null, IReadOnlyList<string>? clients = null) =>
        Unwrap<AgentsReport>(NativeMethods.tb_agents_report(year, JoinClients(clients)));

    /// <summary>
    /// Source configuration identity. Stable while the scan roots are; it
    /// changes when <c>tb_set_extra_scan_paths</c> replaces them, so read it
    /// after that call, not once per process. The full value is for cache
    /// binding only and must not be written to normal diagnostics or UI.
    /// </summary>
    public static string SourceContextId() =>
        ValidateSourceContextId(Unwrap<string>(NativeMethods.tb_source_context_id()));

    public static string ValidateSourceContextId(string value)
    {
        if (value is null || value.Length != 68 ||
            !value.StartsWith("sc1:", StringComparison.Ordinal))
        {
            throw new TbCoreException("source context ID has invalid format");
        }

        for (var index = 4; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f')))
            {
                throw new TbCoreException("source context ID has invalid format");
            }
        }

        return value;
    }

    /// <summary>Source-generation-aware nil/full parity diagnostic for the
    /// hourly and Agents report filters.</summary>
    public static FilterParityProbe FilterParityProbe() =>
        Unwrap<FilterParityProbe>(NativeMethods.tb_filter_parity_probe());

    private static string? JoinClients(IReadOnlyList<string>? clients) =>
        clients is { Count: > 0 } ? string.Join(',', clients) : null;

    /// <summary>Live trace buckets over the trailing window (lazily re-parses
    /// at most every 10s inside the cdylib).</summary>
    public static IReadOnlyList<TraceBucket> UsageTrace(long windowSecs) =>
        Unwrap<IReadOnlyList<TraceBucket>>(NativeMethods.tb_usage_trace(windowSecs));

    /// <summary>Live tokens/min estimate (10-minute-window average).</summary>
    public static double TokensPerMin() =>
        Unwrap<TokensPerMin>(NativeMethods.tb_tokens_per_min()).Value;

    /// <summary>OAuth quota cards for codex/claude/antigravity/copilot.
    /// Network-bound; per-provider failures are reported in each snapshot's
    /// Error, not thrown.</summary>
    public static AgentUsagePayload AgentUsage() =>
        Unwrap<AgentUsagePayload>(NativeMethods.tb_agent_usage());

    /// <summary>Persisted quota-pace history, one entry per stored series.
    /// Disk-bound (call off the UI thread) and strictly read-only: unlike the
    /// recording path, this never quarantines, locks, or rewrites the store.
    /// A missing history file decodes as an empty list.</summary>
    public static IReadOnlyList<QuotaHistorySeries> QuotaHistory() =>
        Unwrap<IReadOnlyList<QuotaHistorySeries>>(NativeMethods.tb_quota_history());

    /// <summary>Per-message usage rows inside the absolute interval
    /// <c>[fromMs, untilMs)</c>. Expensive — scans the whole local corpus when
    /// the cdylib's cache is cold — and must be called off the UI thread. The
    /// cache is keyed by account and <c>fromMs</c>; <c>untilMs</c> is not
    /// quantised and is not part of the key. See <c>ctb.h</c>'s <c>tb_window_usage</c>
    /// for the cache shape this is built on. Always the primary Claude
    /// account (<c>accountKey</c> NULL).</summary>
    public static WindowUsage WindowUsage(long fromMs, long untilMs) =>
        WindowUsage(null, fromMs, untilMs);

    /// <summary><see cref="WindowUsage(long, long)"/> for one Claude account:
    /// <paramref name="accountKey"/> null is the primary (its window excludes
    /// every extra account's roots); otherwise an extra config directory
    /// exactly as the registry reports it on its <c>tb_agent_usage</c> card.
    /// A key with no registered roots is an error, never an empty window.</summary>
    public static WindowUsage WindowUsage(string? accountKey, long fromMs, long untilMs) =>
        Unwrap<WindowUsage>(NativeMethods.tb_window_usage(accountKey, fromMs, untilMs));

    /// <summary>Full-replace the extra Claude config directories
    /// (<c>CLAUDE_CONFIG_DIR</c> accounts) with <paramref name="directories"/>.
    /// Rejections come back by index with a fixed reason code; the input is
    /// never echoed, so neither the result nor an error carries a path.</summary>
    public static RootsResult SetClaudeConfigDirs(IReadOnlyList<string> directories) =>
        Unwrap<RootsResult>(NativeMethods.tb_set_claude_config_dirs(
            JsonSerializer.Serialize(directories, JsonOpts)));

    /// <summary>Whether appending <paramref name="candidate"/> to the saved
    /// list <paramref name="existing"/> would add a working extra Claude
    /// account: null, or the fixed reason code the registries would give.
    /// Changes no registry and touches no filesystem.</summary>
    public static string? ValidateClaudeConfigDir(string candidate, IReadOnlyList<string> existing) =>
        Unwrap<ClaudeConfigDirCheck>(NativeMethods.tb_validate_claude_config_dir(
            JsonSerializer.Serialize(new { candidate, existing }, JsonOpts))).Reason;

    /// <summary>Full-replace the extra Claude scan roots (each account's
    /// <c>projects</c> and <c>transcripts</c>). The next report scans them and
    /// <see cref="SourceContextId"/> changes with them.</summary>
    public static RootsResult SetExtraClaudeScanPaths(IReadOnlyList<string> roots) =>
        Unwrap<RootsResult>(NativeMethods.tb_set_extra_scan_paths(
            JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<string>> { ["claude"] = roots }, JsonOpts)));

    /// <summary>Replace the core's in-memory consent registry with
    /// <c>{"grok-bot":true}</c> or <c>{}</c> (see ctb.h). Local and cheap, no
    /// I/O. Throws <see cref="TbCoreException"/> on an error envelope.</summary>
    public static void SetKeychainConsent(string json) =>
        Unwrap<JsonElement>(NativeMethods.tb_set_keychain_consent(json));

    /// <summary>Full-replace the captured Antigravity accounts with
    /// <paramref name="json"/>, <c>[{"key","label"}]</c> exactly as Settings
    /// stores it. Holds no secret; a refused entry comes back by index with a
    /// fixed reason.</summary>
    public static RootsResult SetAntigravityAccounts(string json) =>
        Unwrap<RootsResult>(NativeMethods.tb_set_antigravity_accounts(json));

    /// <summary>Copy agy's current Google login into Syrtis's own Credential
    /// Manager entry (one refresh at Google first). Blocking, network. Throws
    /// <see cref="TbCoreException"/> carrying a fixed code (ctb.h). Does not
    /// register the account.</summary>
    public static AntigravityAccount AntigravityCapture() =>
        Unwrap<AntigravityAccount>(NativeMethods.tb_antigravity_capture());

    /// <summary>agy's login marker (its credential's LastWritten as a decimal
    /// FILETIME, or <c>absent</c>); attributes only, no secret.</summary>
    public static string AntigravityLoginMarker() =>
        Unwrap<AntigravityMarker>(NativeMethods.tb_antigravity_login_marker()).Marker;

    /// <summary>One automatic capture; <paramref name="removedKeys"/> are
    /// skipped before any request. Blocking, network.</summary>
    public static AntigravityAutoCaptureResult AntigravityAutoCapture(IReadOnlyList<string> removedKeys) =>
        Unwrap<AntigravityAutoCaptureResult>(NativeMethods.tb_antigravity_auto_capture(
            JsonSerializer.Serialize(removedKeys, JsonOpts)));

    /// <summary>Delete one captured account's Credential Manager entry and
    /// cached access token. Never revokes at Google.</summary>
    public static void AntigravityRemove(string key) =>
        Unwrap<AntigravityRemoved>(NativeMethods.tb_antigravity_remove(key));

    /// <summary>Configure Cursor desktop sync (in-memory, default off: re-apply
    /// at launch). The native side chooses the directory. Turning sync off
    /// deletes Syrtis's synced usage; when that cleanup cannot finish this
    /// throws <see cref="TbCoreException"/> <c>cleanupFailed</c> and sync is
    /// off all the same. Disk-bound: call off the UI thread.</summary>
    public static CursorSyncConfig SetCursorSync(bool enabled, bool cliTakeoverConfirmed) =>
        Unwrap<CursorSyncConfig>(NativeMethods.tb_set_cursor_sync(
            CursorSyncRequest.Json(enabled, cliTakeoverConfirmed)));

    /// <summary>One Cursor sync now. Blocking (SQLite read + network, up to
    /// 10 minutes): never on the UI thread. Single-flight.</summary>
    public static CursorSyncStatus CursorSync(bool userInitiated) =>
        Unwrap<CursorSyncStatus>(NativeMethods.tb_cursor_sync(userInitiated ? 1 : 0));

    /// <summary>Whether Cursor desktop's state.vscdb exists (metadata only).</summary>
    public static bool CursorPresent() =>
        Unwrap<CursorPresence>(NativeMethods.tb_cursor_present()).Present;

    /// <summary>
    /// Decodes the standard FFI envelope, returning the payload or throwing
    /// the embedded error. Pure logic, split out (like TBCore.decodeEnvelope)
    /// so the contract is unit-testable without the native library.
    /// </summary>
    public static T DecodeEnvelope<T>(string json)
    {
        using var doc = ParseOrThrow(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new TbCoreException("FFI envelope missing boolean 'ok'");
        }

        if (ok.ValueKind == JsonValueKind.False)
        {
            var err = root.TryGetProperty("err", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()!
                : "unknown FFI error";
            throw new TbCoreException(err);
        }

        if (!root.TryGetProperty("data", out var data))
        {
            throw new TbCoreException("FFI envelope ok=true but missing 'data'");
        }

        return data.Deserialize<T>(JsonOpts)
            ?? throw new TbCoreException("FFI envelope 'data' decoded to null");
    }

    private static T Unwrap<T>(nint ptr) => DecodeEnvelope<T>(TakeString(ptr));

    private static JsonDocument ParseOrThrow(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TbCoreException($"FFI returned malformed JSON: {ex.Message}");
        }
    }

    // Reads the returned heap JSON and frees it exactly once (TBCore.swift's
    // takeBytes counterpart) — the single legal consumer of a tb_* pointer.
    private static string TakeString(nint ptr)
    {
        if (ptr == 0)
        {
            throw new TbCoreException("FFI returned NULL");
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr)
                ?? throw new TbCoreException("FFI returned invalid UTF-8");
        }
        finally
        {
            NativeMethods.tb_free(ptr);
        }
    }
}

public sealed record ProbeResult(
    [property: JsonPropertyName("ok")] bool Ok,
    // The legacy error shape {"ok":false,"err":…} carries no messages field.
    [property: JsonPropertyName("messages")] long Messages = 0,
    [property: JsonPropertyName("err")] string? Err = null);
