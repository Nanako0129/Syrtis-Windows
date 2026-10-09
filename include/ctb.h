#ifndef CTB_H
#define CTB_H

#include <stdint.h>

// C-ABI surface of crates/tb_core_ffi. Every function returns a heap-allocated
// NUL-terminated JSON string that must be released with tb_free.
//
// Envelope: every entry point except tb_probe returns
//   {"ok":true,"data":<payload>}   on success
//   {"ok":false,"err":"..."}       on failure
// Payload fields use the Tauri frontend's camelCase contract. In particular,
// AgentUsagePayload is `{generatedAt, publicationGeneration?, agents,
// opencodeSubscriptions}` (the subscription array is omitted when empty).
// `publicationGeneration` is an additive optional Rust `u64` JSON integer for
// generated payloads; demo/legacy payloads omit it.
// Rust assigns it with a checked increment at process-wide publication-gate
// entry before the complete provider run; exhaustion returns an outer error
// rather than repeating a generation. The gate orders generations and pointer
// creation, but is released before `tb_agent_usage` returns and therefore does
// not promise C return order. Swift's shared MainActor publication coordinator
// rejects a lower generation for dashboard, Settings, tray, and snapshot
// consumers. AgentUsage snapshots may additionally carry the additive optional
// camelCase field
// `transportDiagnostic?: {category, status?, osCode?}`. `error` remains the
// user-visible provider status and may coexist with last-good windows;
// `transportDiagnostic` is the only provider failure detail permitted in the
// public Unified Log. Its `category` is limited to timeout/dns/tls/
// connectionRefused/connectionReset/connect/request/responseBody/rateLimited/
// serverError. `rateLimited` accepts only status 429; `serverError` accepts only
// 500...599. HTTP categories do not carry `osCode`; non-HTTP categories do not
// carry `status`. `osCode`, when present, is a 32-bit OS error integer. Neither
// field carries token, header,
// body, URL/query, email, account ID, credential path, or free-form cause data.
// Other report payloads retain their existing camelCase shapes from the Tauri
// contract. Adding these fields does not change C function signatures, ownership,
// ABI, or any other wire fields. Each v3 quota window uses
// `{cardId, label, usedPercent, remainingPercent, resetsAt, resetText,
// windowMinutes, paceStatus, historicalPace}`. `paceStatus` is required and
// carries `{state, windowKey, durationSeconds, durationSource, completeCycles,
// reason}`; positive durationSeconds is the pace calculation source of truth,
// while windowMinutes is compatibility output derived by integer division.
// historicalPace is present only for `available` and carries one coherent Rust
// result: `{expectedUsedPercent, etaSeconds, willLastToReset,
// runOutProbability}`. A legacy payload missing the entire paceStatus key is
// not eligible for an implicit Linear fallback. ETA/risk remain optional inside
// an available historical result. Other report payloads retain their existing
// camelCase shapes from the Tauri contract.
// tb_probe keeps its Phase 0 shape: {"ok":true,"messages":N} / {"ok":false,...}.
//
// `year` parameters may be NULL or "" for the all-time view, otherwise a
// 4-digit year string ("2026"). All calls are blocking; tb_agent_usage also
// performs network requests — invoke from a background thread.

// Smoke probe: total locally parsed messages.
char *tb_probe(void);

// Contribution graph (UsagePayload). Serves a <=30s-old cached payload.
char *tb_graph(const char *year);
// Contribution graph, local-first per call: bypasses pricing resolution and the
// authoritative year-only graph cache. Provider-reported costs remain intact.
char *tb_graph_local_first(const char *year);
// Contribution graph, always recomputed (cache refreshed as a side effect).
char *tb_refresh_graph(const char *year);

// Per-model report (ModelReport).
char *tb_model_report(const char *year);
// Per-hour report (HourlyReport). `clients` = comma-joined canonical ids to
// restrict to, or NULL/empty for all clients (filtered in the streaming scan).
char *tb_hourly_report(const char *year, const char *clients);
// Per-(sub-)agent report (AgentsReport). `clients` as in tb_hourly_report.
char *tb_agents_report(const char *year, const char *clients);

// Source configuration identity. Stable while the scan roots are; it changes
// when tb_set_extra_scan_paths replaces them, so read it again after that
// call rather than once per process. Success uses the standard
// envelope with `data` equal to exactly `sc1:` plus 64 lowercase hex digits.
// Failure is the fixed redacted `sourceContextUnavailable`; no path, descriptor,
// panic payload, or native cause crosses this boundary. The ID is not a secret
// and must not be written to normal diagnostics, UI, or Smoke output.
char *tb_source_context_id(void);

// Source-generation-aware hourly/Agents filter parity diagnostic. The graph
// client list is derived from a fresh graph and all reports are bracketed by
// one opaque local-source token sequence. The success payload contains only
// lower-camel status values (match/mismatch/sourceChanged/tokenUnavailable),
// bounded report aggregates, and presentClientCount; it never exposes source
// paths, raw messages, cache data, credentials, providers, models, agents, or
// workspaces. A token probe failure is a successful tokenUnavailable result;
// graph/report/mapping/serialization failures use the normal outer error
// envelope. All calls are blocking and must be made off the main thread.
char *tb_filter_parity_probe(void);

// Live trace buckets over the trailing window (array of TraceBucket;
// snake_case fields, e.g. tokens_per_min). Lazily re-parses at most every 10s.
char *tb_usage_trace(int64_t window_secs);
// Live rate: {"tokensPerMin": <number>} (10-minute-window average).
char *tb_tokens_per_min(void);

// OAuth quota cards (AgentUsagePayload) for codex/claude/antigravity/copilot/grok.
// Network-bound; per-provider failures are reported inside each snapshot.
// Claude can appear more than once: the primary card has no accountKey; each
// configured config directory and the Claude Desktop login ("claude-desktop")
// carry one. A card of the same account as an earlier card is merged away.
char *tb_agent_usage(void);

// Persisted quota-pace history for the quota lens, one entry per stored series:
// [{providerId, accountScope, windowKey, samples:[{resetAt, durationSeconds,
//   durationSource, usedPercent, sampledAt, origin, isActiveGroup}]}].
// Identity is the store's own triple; cardId/label are not in the store and are
// joined consumer-side on (clientId, paceStatus.windowKey). `isActiveGroup` is
// always emitted and is the producer's answer to whether the sample belongs to
// the cycle still running — a consumer cannot derive it, because the series'
// active reset is the raw provider value while stored samples are normalized.
// Disk-bound (call off the UI thread) and strictly read-only: unlike the
// recording path, this never quarantines, locks, or rewrites the store. A
// missing history file is an empty array; an unparseable one is an error
// envelope.
char *tb_quota_history(void);

// Per-message usage rows inside the absolute interval [from_ms, until_ms)
// (WindowUsage: {messages:[{timestamp,client,providerId,modelId,input,output,
// cacheRead,cacheWrite,reasoning,cost,isTurnStart}], undatedCount,
// processingTimeMs}). No bucketing and no attribution — attribution is applied
// C#-side, since it is the user's own declaration. Backs the quota lens's
// per-cycle folds, which need usage scoped to one quota cycle's observed span
// (HourlyReport's hour buckets and tb_usage_trace's trailing live window can't
// slice an arbitrary five-hour cycle). Expensive: an unbounded window scans
// the whole local corpus, so this is never a call the UI thread should make
// directly. Cached per (account_key, from_ms); until_ms is NOT quantised and
// is NOT part of the cache key. A poll-every-60s caller gets a cache hit on every
// call after the first through a source-change-token probe — see the
// tb_core_ffi window_usage module. account_key is NULL for the primary Claude
// account (its window excludes every registered extra account's roots), or an
// extra account's config directory exactly as registered (its window reads
// only that account's registered roots; none registered is an error, never an
// empty window). The cache is per (account, from_ms).
char *tb_window_usage(const char *account_key, int64_t from_ms, int64_t until_ms);

// Replace the extra scan-root registry with {"<client>":["<path>",...]} (only
// "claude"; absolute drive paths, e.g. an extra account's <dir>\projects and
// <dir>\transcripts); {} clears it. The next report scans the new roots.
// Success data: {"registeredCount":N,"rejected":[{"client","index","reason"}],
// "unreadable":[{"client","index","reason":"unreadable"}]}. Errors and reasons
// are fixed codes (a root at, under or above one already accepted is
// overlappingRoot); the input is never echoed. On error nothing changed.
char *tb_set_extra_scan_paths(const char *json);

// Replace the registry of extra Claude config directories (CLAUDE_CONFIG_DIR
// accounts) with a JSON array of absolute drive paths; [] clears it. Each
// directory becomes its own Claude card (accountKey = the directory) on the
// next tb_agent_usage, read only from <dir>\.credentials.json. Success data:
// {"registeredCount":N,"rejected":[{"index":i,"reason":code}]}. Errors and
// reasons are fixed codes: empty, unsupportedPath, rootDirectory,
// invalidComponent, homeDirectory (the home folder itself), defaultConfigDir
// (the primary's <home>\.claude or a folder above it), duplicate,
// limitExceeded. A directory nested in another is allowed.
// The input is never echoed. On error the registry is unchanged.
char *tb_set_claude_config_dirs(const char *json);

// Pre-save check for one extra Claude config directory:
// {"candidate":"<dir>","existing":["<dir>",...]} -> data {"reason":null} or
// {"reason":"<code>"}: tb_set_claude_config_dirs's code for that position,
// else tb_set_extra_scan_paths's for the account's projects/transcripts:
// defaultConfigDir (under <home>\.claude) or overlappingRoot (at, under or
// above a root of an account already in the list). Changes no
// registry and touches no filesystem; the input is never echoed.
char *tb_validate_claude_config_dir(const char *json);

// Replace the registry of credential reads the user has agreed to with
// {"<client>":true|false} (only "grok-bot"); {} clears every grant. In-memory,
// empty at launch: the caller re-applies the stored answer before the first
// tb_agent_usage. Without a grant the grok-bot card has source
// "keychain-consent" and the Grok Bot desktop login is not read (Windows shows
// no OS prompt; this is the only gate). Success data:
// {"grantedCount":N,"rejectedCount":M}. Errors are fixed codes (nullPayload,
// invalidUtf8, invalidJson); the input is never echoed. On error nothing
// changed.
char *tb_set_keychain_consent(const char *json);

// Replace the registry of captured Antigravity accounts with
// [{"key":"<64 lowercase hex>","label":"..."}]; [] clears it. Each entry
// becomes its own Antigravity card (accountKey = key) after the primary on
// the next tb_agent_usage. Success data: {"registeredCount":N,"rejected":
// [{"index":i,"reason":"..."}]}; malformed JSON is invalid_accounts_json and
// changes nothing. Holds no secret.
char *tb_set_antigravity_accounts(const char *json);

// Bind agy's current account for the next tb_agent_usage calls:
// {"key":"<64 lowercase hex>","marker":"<agy login marker>"} sets, NULL or
// {"key":null} clears. marker is what tb_antigravity_login_marker returns for
// a present login (non-zero decimal FILETIME; "absent" is refused). Success
// data: {"bound":true|false}. Any other input clears the binding first, then
// fails with a fixed code (invalid_binding_json, invalid_key, invalid_marker);
// the input is never echoed. While the key is a registered captured account
// and agy's live marker equals it before and after the fetch, the primary
// Antigravity card takes that account's OAuth result (source "oauth", with
// agyLoginMarker) instead of running agy. Holds no secret.
char *tb_set_antigravity_binding(const char *json);

// Copy agy's current Google login (Credential Manager gemini:antigravity,
// read only) into a Syrtis generic credential
// com.nyanako.tokenbar.antigravity-account:<key> (persist local machine),
// after one refresh at Google with the client named by its aud. Starts no
// process. Success data: {"key","label"}. Errors are fixed codes
// (agy_not_signed_in, agy_login_unreadable, agy_login_missing_identity,
// oauth_client_not_found, oauth_client_rejected, refresh_rejected,
// refresh_unreachable, account_mismatch, invalid_credential_format,
// keychain_write_failed). Blocking (network). Does not register the account.
char *tb_antigravity_capture(void);

// agy's login marker: {"marker":"<LastWritten FILETIME, decimal>"} or
// {"marker":"absent"}; an unreadable credential is marker_unavailable.
char *tb_antigravity_login_marker(void);

// One automatic capture. removed_keys_json is ["<64 hex>",...]; a listed
// account is skipped before any request. Success data:
// {"status":"captured"|"unchanged","key","label"} or
// {"status":"skipped_removed"}. Errors: the capture codes (except
// agy_not_signed_in), not_signed_in, paused, invalid_removed_keys.
char *tb_antigravity_auto_capture(const char *removed_keys_json);

// Delete one captured account's credential and cached access token. key must
// be 64 lowercase hex, else invalid_key before any Credential Manager call.
// Already gone counts as removed; never revokes at Google. Success data:
// {"removed":true}; errors invalid_key, keychain_delete_failed.
char *tb_antigravity_remove(const char *key);

// Configure Cursor desktop sync. `json` is {"enabled":bool,
// "cliTakeoverConfirmed":bool}; any other key (a "dir" included) is
// invalidJson. The sync dir is chosen here:
// %APPDATA%\com.nyanako.tokenbar[.secure]\cursor-cache[.secure]. Full replace,
// in-memory, default off: the caller re-applies it at launch. Success data:
// {"enabled","dir","cliTakeoverConfirmed","removedFiles":N} (dir is the
// resolved sync dir while enabled, null when disabled). Disabling deletes
// Syrtis usage files from every existing candidate dir (both roots x both
// child names) and creates no directory. Errors: nullPayload, invalidUtf8,
// invalidJson (nothing changed), storageUnavailable (enabling; the registry
// is unchanged, but directories the resolution already created may remain),
// cleanupFailed (disabling: sync is off and the takeover removed, but a
// Syrtis usage file could not be deleted). Rechecks the takeover.
char *tb_set_cursor_sync(const char *json);

// Sync Cursor usage from the signed-in Cursor desktop app now. Blocking
// (SQLite read + network, up to 10 min): never on the UI thread.
// `user_initiated` non-zero = the user's "Sync now". Single-flight. Success
// data: {"state":"ok|partial|expired|notSignedIn|offline|error|disabled|
// cliPresent","events":N,"lastSuccessMs":ms|null,"reason"?:"<fixed code>"}.
// cliPresent = the walk completed but tokscale CLI Cursor files exist and the
// takeover is not confirmed, so reports read the CLI's files.
char *tb_cursor_sync(int32_t user_initiated);

// Whether Cursor desktop's state.vscdb exists: {"present":bool}. Metadata
// only; nothing in the file is read.
char *tb_cursor_present(void);

// Release a string returned by any tb_* entry point.
void tb_free(char *p);

#endif
