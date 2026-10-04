//! Window usage for the quota lens: per-message rows inside an absolute interval.
//!
//! Returns the messages inside an absolute [from, until) window, one row each.
//! No bucketing: a quota window is a tiny slice of history, so the consumer
//! folds it however the UI wants without another round trip. Attribution is a
//! C#-side declaration, so it is deliberately NOT applied here.
//!
//! Ported from TokenBar-Native's `window_usage.rs` (crates/tb_core_ffi, macOS)
//! with the cache kept — it exists because a full window scan is not a call
//! any UI thread can make. macOS's own probe records 14.93 days and 109,278
//! messages at 67 seconds. Windows reuses the source-context token probe
//! `graph_cached` already uses instead of the plain (non-source-context)
//! probe the macOS module calls; publication is gated on the root generation
//! (see the W4b section below).
//!
//! The key is `from_ms` alone, not `(from_ms, until_ms)`. macOS's own module
//! quantises `until_ms` to the minute instead and carries the same defect:
//! `DashboardModel` requests this export every 60s with `until_ms = now`, so
//! a minute-floored key changes on almost every poll (a poll rarely lands
//! exactly on the boundary the previous one floored to), each miss bypasses
//! the source-token check entirely, and `compute` re-runs the full scan.
//! Keying by `from_ms` instead fixes the miss-every-poll half of that: an
//! ordinary poll whose `until_ms` did NOT grow past what is already cached
//! reuses that scan's data once the source-change token confirms storage is
//! unchanged. A request whose `until_ms` grew past what is cached forces a
//! rescan — see `cached` below.
//!
//! ## The bounded-scan detour this module tried and removed
//!
//! Nine commits across nineteen rounds tried to widen the cache without a
//! rescan when `until_ms` grew past what was cached, by bounding the fresh
//! scan at (an approximation of) "now" instead of at the caller's own
//! `until_ms`, and proving — by reasoning about clocks — that the cached
//! entry could safely be treated as covering everything up to that bound.
//! Four different races were each found and closed in turn, and every one of
//! them was a soundness gap in the same premise:
//!
//! - Round 10: the token alone did not justify widening past `cached_until`
//!   at all — a message could sit on disk past the earlier scan's own bound
//!   without the token having moved.
//! - Round 15: a caller queued behind another's in-progress scan read its own
//!   "now" before it waited, so by the time its scan actually ran, that
//!   reading was stale.
//! - Round 16: even an UNCONTENDED acquisition did not prove no time had
//!   passed between the caller reading its own clock and the scan actually
//!   starting — P/Invoke marshalling and first-time source-context init could
//!   both delay arrival by an unmeasured amount.
//! - Round 18: a P/Invoke harness against a real 5.3 GB store measured the
//!   surviving slack-constant design as intermittent between successive runs
//!   of the very same binary — 1450-1740ms on a hit, 6300-6500ms on a miss —
//!   with OS file-cache warmth the likely mechanism, meaning the gate was
//!   least reliable exactly on a cold start.
//!
//! The whole detour was unnecessary. `tokscale_core::get_window_usage` (see
//! its doc comment, `vendor/tokscale-core/src/lib.rs`) does no
//! `modified_after` pruning: the window scan considers every source file
//! regardless of the requested bound, because the caller-chosen window can
//! reach arbitrarily far back. `until_ms` is applied only as a post-filter
//! over already-read messages
//! (`m.timestamp >= from_ms && m.timestamp < until_ms`). A scan bounded at
//! `i64::MAX` therefore reads exactly the same files, at exactly the same
//! cost, as one bounded at any narrower `until_ms` — there was never a
//! cheaper bound to buy soundness for. `run` below always scans through
//! `i64::MAX`; a widening request is answered by the post-filter
//! (`narrow_to_request`) alone, with no rescan and no clock read anywhere in
//! this module.
//!
//! ## Scoped to one Claude account (W4b, macOS #258)
//!
//! A quota window belongs to one account, so its usage must come from that
//! account's transcripts only. The primary (`account == None`) scans the
//! process inputs with every extra account excluded — the configured
//! directories and the registered Claude roots, as `excluded_scan_paths`,
//! which the engine applies whichever route reached a directory (W4c,
//! tokscale-core #65; macOS `primary_exclusions`). An extra account
//! (`Some(<config dir>)`) reads only its own registered roots and the Claude
//! client. Each scope runs
//! on its own captured context (the engine reads scanner settings from the
//! context, never from report options), memoized per root generation. With no
//! extra account registered (no root, no config directory) the primary uses
//! the process context itself, so its output is what it was before this
//! existed.
//!
//! Since W4b the cache key is `(account, from_ms)` and publication is gated on
//! the root generation the scan's context was captured at.
//!
//! A future reader tempted to re-add `until_ms` as the scan's own upper bound
//! to "avoid scanning too far" should read the `tokscale_core` comment above
//! first — the bound buys nothing there, and every fix this module made to
//! defend it was chasing a race that a real bound would have needed to exist
//! for.

use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

pub(crate) type CacheKey = (Option<String>, i64);
/// (published-at [`Instant`, re-stamped on every token-match hit — see
/// `cached` below, matching `graph_cached`'s own re-stamp shape], source
/// token, the `until_ms` this entry was scanned through — always `i64::MAX`,
/// see the module doc — mapped window payload).
type CacheEntry = (Instant, u64, i64, Value);

static SCAN_COUNT: AtomicUsize = AtomicUsize::new(0);

/// key → cache entry. Same role as `GRAPH_CACHE` in lib.rs. The key is the
/// window's stable lower bound; see the module doc comment for why `until_ms`
/// is not part of it, and the account (`None` = primary) is. `publish` keeps
/// one entry per account: not a history, so a window scan (tens of seconds on
/// a large store) left resident forever is one window's messages per account,
/// not one per poll the lens stayed open.
static WINDOW_USAGE_CACHE: LazyLock<Mutex<HashMap<CacheKey, CacheEntry>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

pub(crate) fn cache_key(account: &Option<String>, from_ms: i64) -> CacheKey {
    (account.as_deref().map(account_identity), from_ms)
}

/// One account directory however it is spelled: folded like both registries
/// (`duplicate_key`) with trailing separators trimmed, so `C:\Acct`,
/// `c:/acct` and `C:\acct\` share one cache entry, one scoped context and
/// the same registered roots.
fn account_identity(dir: &str) -> String {
    crate::claude_config_dirs::duplicate_key(dir)
        .trim_end_matches('\\')
        .to_string()
}

const CLAUDE: &str = "claude";

/// An extra account whose roots are not registered cannot be answered for,
/// and says so instead of reporting an empty window (macOS wording): an empty
/// scan means "nothing was read", and rendering it would mean "this account
/// used nothing". The two registries are set by separate calls, so a
/// just-added account can briefly be in this state.
pub(crate) const NO_REGISTERED_ROOTS: &str =
    "This Claude account has no registered scan root yet, so its window cannot be read.";

/// `(root generation, account)` → the context that account's window scans.
/// Inserted only while its generation is current; cleared with the cache.
static SCOPED_CONTEXTS: LazyLock<Mutex<HashMap<(u64, Option<String>), crate::LocalSourceContext>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

/// The registered `claude` roots that belong to the config directory `dir`:
/// direct children only (macOS `registered_roots_under`), so a nested
/// account's roots are not folded into the outer one. Compared on the folded
/// form (`duplicate_key`), which is how both registries de-duplicate.
fn registered_roots_under(
    dir: &str,
    registry: &std::collections::BTreeMap<String, Vec<std::path::PathBuf>>,
) -> Vec<std::path::PathBuf> {
    let prefix = format!("{}\\", account_identity(dir));
    registry
        .get(CLAUDE)
        .map(|roots| {
            roots
                .iter()
                .filter(|root| {
                    let key = crate::claude_config_dirs::duplicate_key(&root.to_string_lossy());
                    key.strip_prefix(&prefix)
                        .is_some_and(|rest| !rest.is_empty() && !rest.contains('\\'))
                })
                .cloned()
                .collect()
        })
        .unwrap_or_default()
}

/// The context `account`'s window scans. `context` is the process context the
/// caller already holds (taken before any registry or memo lock, per the W4b
/// lock order); a derived context carries its generation.
fn scoped_context(
    context: &crate::LocalSourceContext,
    account: &Option<String>,
) -> Result<crate::LocalSourceContext, String> {
    let registry = crate::extra_scan_paths::snapshot();
    // The primary window is the process context with every extra account
    // excluded (macOS `account_options` / `primary_exclusions`, #258): the
    // configured directories and the registered Claude roots. The engine
    // drops an excluded prefix whichever route reached it, the registered
    // roots, a `.cc-mirror` variant naming one of those directories, or
    // `TOKSCALE_EXTRA_DIRS` (tokscale-core #65, which made the source-context
    // scan honor `excluded_scan_paths`).
    //
    // The roots half is read from the held context, not from the registry: a
    // request holding a context captured with roots, racing a setter that just
    // cleared them, would otherwise exclude nothing and scan the old context's
    // removed roots under primary scope (CodeRabbit security review, #167).
    // The config dirs are not part of any context, so they come from their
    // registry; a config-dir setter racing this request moves the generation,
    // and the publish of a window computed under the old one is dropped.
    let held = context.resolved().scanner_settings();
    let (config_dirs, _) = crate::claude_config_dirs::snapshot();
    if account.is_none() && held.extra_scan_paths.is_empty() && config_dirs.is_empty() {
        return Ok(context.clone());
    }
    let memo_key = (context.generation(), account.as_deref().map(account_identity));
    if let Some(found) = SCOPED_CONTEXTS
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .get(&memo_key)
    {
        return Ok(found.clone());
    }
    let resolved = match account {
        None => {
            let mut excluded: Vec<std::path::PathBuf> =
                config_dirs.iter().map(std::path::PathBuf::from).collect();
            excluded.extend(held.extra_scan_paths.get(CLAUDE).into_iter().flatten().cloned());
            tokscale_core::ResolvedLocalSourceContext::capture(
                crate::user_home_dir(),
                true,
                tokscale_core::ScannerSettings {
                    extra_scan_paths: held.extra_scan_paths.clone(),
                    excluded_scan_paths: std::collections::BTreeMap::from([(
                        CLAUDE.to_string(),
                        excluded,
                    )]),
                    ..Default::default()
                },
            )
        }
        Some(dir) => {
            let own = registered_roots_under(dir, &registry);
            if own.is_empty() {
                return Err(NO_REGISTERED_ROOTS.to_string());
            }
            // Every other account nested under this one is excluded. This
            // window is captured with `dir` as home, and the engine adds
            // `<home>/.claude/projects`, `<home>/.claude/transcripts` and
            // cowork trees under it on its own, so an account nested at
            // `<dir>\.claude` was counted here and in its own window (#175
            // deferred). Those routes all lie under `dir`. A `.cc-mirror`
            // variant under `dir` may name a directory elsewhere, but its rows
            // carry the client id `cc-mirror/<variant>`, and an account window
            // asks for exactly `claude` (see `clients` in `compute`), which the
            // engine matches per message, so they never reach this window
            // (measured on 188: with no exclusion at all, a variant under D
            // naming the unrelated account E adds nothing to D's window). So
            // only paths under `dir` are listed: an unrelated account adds
            // nothing, and an account that contains this one (an ancestor) is
            // never listed, which would remove this account's own files. Compared on the folded form; a nested
            // account registered under another spelling of its folder (a
            // junction, `subst` drive, 8.3 name) is not recognised, so it
            // stays counted twice, as before this fix, rather than hidden.
            let under_me = format!("{}\\", account_identity(dir));
            let is_nested = |path: &str| account_identity(path).starts_with(&under_me);
            let mut excluded: Vec<std::path::PathBuf> = config_dirs
                .iter()
                .filter(|other| is_nested(other))
                .map(std::path::PathBuf::from)
                .collect();
            excluded.extend(
                registry
                    .get(CLAUDE)
                    .into_iter()
                    .flatten()
                    .filter(|root| !own.contains(root) && is_nested(&root.to_string_lossy()))
                    .cloned(),
            );
            // The registries compare folded strings, but the engine resolves
            // exclusion prefixes with `canonicalize` before matching
            // (`retain_unexcluded_scan_tasks`). A "nested" account that is an
            // alias of this one (`<dir>\.claude` as a junction back to
            // `dir`) would then exclude this account's own roots and empty its
            // window. Drop any exclusion that covers one of our own roots,
            // compared the way the engine compares. Erring here double counts
            // an aliased neighbour; erring the other way hid real usage.
            let resolve = |path: &std::path::Path| {
                std::fs::canonicalize(path).unwrap_or_else(|_| path.to_path_buf())
            };
            let own_resolved: Vec<std::path::PathBuf> = own.iter().map(|root| resolve(root)).collect();
            excluded.retain(|path| {
                let prefix = resolve(path);
                !own_resolved.iter().any(|root| root.starts_with(&prefix))
            });
            let mut excluded_scan_paths = std::collections::BTreeMap::new();
            if !excluded.is_empty() {
                excluded_scan_paths.insert(CLAUDE.to_string(), excluded);
            }
            // `use_env_roots = false`: `TOKSCALE_EXTRA_DIRS` can name `claude:`
            // roots of its own and would pour another account's files into
            // this one's window (macOS `account_options`).
            tokscale_core::ResolvedLocalSourceContext::capture(
                Some(std::path::PathBuf::from(dir)),
                false,
                tokscale_core::ScannerSettings {
                    extra_scan_paths: std::collections::BTreeMap::from([(CLAUDE.to_string(), own)]),
                    excluded_scan_paths,
                    ..Default::default()
                },
            )
        }
    }
    .map_err(|_| "sourceContextUnavailable".to_string())?;
    let scoped = crate::LocalSourceContext::derived(resolved, context.generation());
    let mut memo = SCOPED_CONTEXTS.lock().unwrap_or_else(|p| p.into_inner());
    if scoped.is_current() {
        memo.insert(memo_key, scoped.clone());
    }
    Ok(scoped)
}

/// Drop every cached window and scoped context (root change).
pub(crate) fn clear_all() {
    WINDOW_USAGE_CACHE.lock().unwrap_or_else(|p| p.into_inner()).clear();
    SCOPED_CONTEXTS.lock().unwrap_or_else(|p| p.into_inner()).clear();
}

#[cfg(test)]
pub(crate) fn scoped_context_for_test(
    context: &crate::LocalSourceContext,
    account: &Option<String>,
) -> Result<crate::LocalSourceContext, String> {
    scoped_context(context, account)
}

// Only read from tests (asserting the cache actually avoids a re-scan); no
// production consumer needs the count.
#[cfg(test)]
pub(crate) fn scan_count() -> usize {
    SCAN_COUNT.load(Ordering::Relaxed)
}

pub(crate) fn cached(
    context: &crate::LocalSourceContext,
    account: &Option<String>,
    from_ms: i64,
    until_ms: i64,
) -> Result<Value, String> {
    let scoped = scoped_context(context, account)?;
    let context = &scoped;
    let key = cache_key(account, from_ms);
    let cached = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache.get(&key).map(|(published_at, token, cached_until, data)| {
            (
                published_at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS),
                *token,
                *cached_until,
                data.clone(),
            )
        })
    };
    let Some((fresh_enough, token, cached_until, data)) = cached else {
        return compute(context, from_ms, until_ms, key);
    };
    if fresh_enough {
        return Ok(narrow_to_request(data, until_ms, cached_until));
    }

    // Aged out — but if no source state changed since the scan, the window
    // cannot have changed either. Probe and, on a match, re-stamp so the next
    // calls inside the oneshot window skip the probe entirely — the same
    // shape `graph_cached` in lib.rs uses, no ceiling on how many times a
    // token match can re-stamp.
    if let Ok(probe_token) = tokscale_core::local_source_change_token_with_source_context(
        context.resolved(),
        &context.parse_options(None, None),
    ) {
        if probe_token == token {
            let mut cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            if let Some(entry) = cache.get_mut(&key) {
                entry.0 = Instant::now();
            }
            return Ok(narrow_to_request(data, until_ms, cached_until));
        }
    }

    compute(context, from_ms, until_ms, key)
}

/// Trims a cache hit's messages to the caller's own `until_ms`. `run` always
/// scans through `i64::MAX` (see the module doc), so `cached_until` is always
/// `i64::MAX` and this is the only place a request's own `until_ms` is
/// actually enforced on the response path. Also the single exit that
/// rewrites the payload's own `untilMs` field to `until_ms`, so a consumer
/// sees what it asked for rather than the scan's unbounded internal marker.
fn narrow_to_request(data: Value, until_ms: i64, cached_until: i64) -> Value {
    let mut data = data;
    if cached_until > until_ms {
        if let Some(object) = data.as_object_mut() {
            if let Some(Value::Array(messages)) = object.get_mut("messages") {
                messages.retain(|message| {
                    message
                        .get("timestamp")
                        .and_then(Value::as_i64)
                        .is_some_and(|timestamp| timestamp < until_ms)
                });
            }
        }
    }
    if let Some(object) = data.as_object_mut() {
        object.insert("untilMs".to_string(), Value::from(until_ms));
    }
    data
}

/// Held across a scan so overlapping callers share one, instead of each
/// starting its own.
///
/// A plain mutex rather than a per-key future: the second caller waits, then
/// re-checks the cache and finds what the first published. Scans are CPU-bound
/// and already serialise inside tokscale-core's rayon pool, so making them
/// queue costs nothing that running them concurrently was buying.
static COMPUTE: Mutex<()> = Mutex::new(());

/// Test seam: hold the scan lock so a concurrent `tb_window_usage` blocks
/// after it has taken its context (the W4b gate test).
#[cfg(test)]
pub(crate) fn lock_compute_for_test() -> std::sync::MutexGuard<'static, ()> {
    COMPUTE.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[cfg(test)]
pub(crate) fn has_entry_for_test(account: &Option<String>, from_ms: i64) -> bool {
    WINDOW_USAGE_CACHE
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .contains_key(&cache_key(account, from_ms))
}

fn compute(context: &crate::LocalSourceContext, from_ms: i64, until_ms: i64, key: CacheKey) -> Result<Value, String> {
    let _serialised = COMPUTE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    // Re-check under the lock. A caller that queued behind another's scan is
    // asking a question that scan may have just answered; running a second one
    // to produce the same bytes is the duplicate this lock exists to remove.
    let hit = {
        let cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        cache
            .get(&key)
            .and_then(|(published_at, _, cached_until, data)| {
                (published_at.elapsed() <= Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS))
                    .then(|| (data.clone(), *cached_until))
            })
    };
    // `hit` and the freshly-scanned branch below join at ONE exit
    // (`narrow_to_request` below, called exactly once) rather than each
    // returning its own narrowed value. The recheck-hit branch used to return
    // `data` as-is: two concurrent callers with no covering entry yet both
    // reach this function, the first publishes a scan, and the second —
    // narrower — found that entry right here and handed back every message
    // up to the first caller's bound, past its own. `cached()`'s two call
    // sites already narrow correctly; this was the one exit that did not.
    let (data, cached_until) = match hit {
        Some((data, cached_until)) => (data, cached_until),
        None => {
            // Read BEFORE `run` below, not after — load-bearing, not
            // incidental: a write landing mid-scan changes the token read
            // here strictly before it could have been observed, so the next
            // caller's probe (in `cached` above) correctly sees a mismatch
            // and forces a rescan. Reading it after `run` would let a write
            // that happened during the scan silently match the token this
            // scan publishes, and the fast path would then serve stale data
            // past that write forever (until some later, unrelated change
            // moved the token again).
            let token = tokscale_core::local_source_change_token_with_source_context(
                context.resolved(),
                &context.parse_options(None, None),
            )
            .unwrap_or(0);
            // `run` scans through i64::MAX regardless of the caller's own
            // `until_ms` — see the module doc for why that costs nothing
            // extra (`tokscale_core::get_window_usage` does no
            // `modified_after` pruning either way) and buys back every case
            // the nine-commit bounded-scan design was trying to cover: the
            // published entry always contains everything the token attests
            // to, by construction, for any `until_ms` a later caller asks
            // for.
            let scan_until = i64::MAX;
            // An extra account's window is a Claude quota window: scan only the
            // Claude client, so other clients' data (some of which lives in
            // Windows Known Folders that an account root does not move) never
            // lands in it.
            let clients = key.0.as_ref().map(|_| vec![CLAUDE.to_string()]);
            let data = run_with_clients(context, clients, from_ms, scan_until)?;
            publish(context, key, (Instant::now(), token, scan_until, data.clone()));
            (data, scan_until)
        }
    };
    Ok(narrow_to_request(data, until_ms, cached_until))
}

/// Cache a freshly scanned window, keeping only the newest entry per account
/// (macOS `publish`: a whole-map clear would make the primary and an extra
/// account evict each other on every poll). Dropped when the roots changed
/// since `context` was captured. The recheck and re-stamp read paths above are
/// deliberately not gated: the setters clear this cache right after they move
/// the generation.
fn publish(context: &crate::LocalSourceContext, key: CacheKey, entry: CacheEntry) {
    let mut cache = WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    if !context.is_current() {
        return;
    }
    cache.retain(|(account, _), _| *account != key.0);
    cache.insert(key, entry);
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Message {
    timestamp: i64,
    client: String,
    provider_id: String,
    model_id: String,
    input: i64,
    output: i64,
    cache_read: i64,
    cache_write: i64,
    reasoning: i64,
    cost: f64,
    is_turn_start: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowData {
    /// The interval this payload actually speaks for — `[from_ms, until_ms)`
    /// — so a consumer can check what it got against what it asked for
    /// instead of trusting the request echoed the response. `from_ms` never
    /// changes after a scan (it is the cache key); `until_ms` is rewritten by
    /// `narrow_to_request` above to whichever bound the request actually
    /// asked for — that field, not the request's own `until_ms`, is what a
    /// consumer should trust.
    from_ms: i64,
    until_ms: i64,
    messages: Vec<Message>,
    undated_count: u32,
    processing_time_ms: u32,
}

pub(crate) fn run(
    context: &crate::LocalSourceContext,
    from_ms: i64,
    until_ms: i64,
) -> Result<Value, String> {
    run_with_clients(context, None, from_ms, until_ms)
}

fn run_with_clients(
    context: &crate::LocalSourceContext,
    clients: Option<Vec<String>>,
    from_ms: i64,
    until_ms: i64,
) -> Result<Value, String> {
    let options = context.report_options(None, clients);

    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| format!("build runtime: {}", e))?;
    SCAN_COUNT.fetch_add(1, Ordering::Relaxed);
    let usage = runtime.block_on(tokscale_core::get_window_usage_with_source_context(
        context.resolved(),
        options,
        from_ms,
        until_ms,
    ))?;

    let data = WindowData {
        from_ms,
        until_ms,
        messages: usage
            .messages
            .into_iter()
            .map(|m| Message {
                timestamp: m.timestamp,
                client: m.client,
                provider_id: m.provider_id,
                model_id: m.model_id,
                input: m.input,
                output: m.output,
                cache_read: m.cache_read,
                cache_write: m.cache_write,
                reasoning: m.reasoning,
                cost: m.cost,
                is_turn_start: m.is_turn_start,
            })
            .collect(),
        undated_count: usage.undated_count,
        processing_time_ms: usage.processing_time_ms,
    };
    serde_json::to_value(data).map_err(|e| format!("serialize window usage: {}", e))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{LazyLock, Mutex as StdMutex};

    static TEST_LOCK: LazyLock<StdMutex<()>> = LazyLock::new(|| StdMutex::new(()));

    fn wall_clock_now_ms() -> i64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(i64::MAX)
    }

    fn test_context(label: &str) -> crate::LocalSourceContext {
        let home = std::env::temp_dir().join(format!(
            "tokenbar-window-usage-{label}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        crate::LocalSourceContext::capture(Some(home), false, tokscale_core::ScannerSettings::default())
            .unwrap()
    }

    /// An account directory spelled with a trailing separator, other case or
    /// forward slashes still owns its registered roots and shares one cache
    /// key; a nested directory's roots and a sibling are not its own.
    #[test]
    fn an_account_matches_its_roots_however_it_is_spelled() {
        let own = std::path::PathBuf::from(r"C:\Acct\projects");
        let registry = std::collections::BTreeMap::from([(
            CLAUDE.to_string(),
            vec![
                own.clone(),
                std::path::PathBuf::from(r"C:\Acct\nested\projects"),
                std::path::PathBuf::from(r"C:\Acct2\projects"),
            ],
        )]);
        for spelling in [r"C:\Acct", r"C:\Acct\", "c:/acct/", r"c:\ACCT\\"] {
            assert_eq!(registered_roots_under(spelling, &registry), vec![own.clone()], "{spelling}");
            assert_eq!(
                cache_key(&Some(spelling.to_string()), 0),
                cache_key(&Some(r"C:\Acct".to_string()), 0),
                "{spelling}"
            );
        }
        assert_eq!(cache_key(&None, 0).0, None);
    }

    // The half of the original bug that keying by `from_ms` alone still
    // fixes: macOS's minute-floored key changes key on almost every 60s poll
    // even when `until_ms` did not move past what was already scanned, so
    // every such poll bypassed the source-token check and rescanned. Keying
    // by `from_ms` means a repeated request for the SAME already-covered
    // `until_ms` (a poll landing on an unchanged window, or the source-token
    // probe path when nothing grew) is answered from the one scan.
    #[test]
    fn repeated_identical_request_reuses_one_scan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(&None, from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let before = scan_count();
        let context = test_context("polling-drift");

        cached(&context, &None, from_ms, from_ms + 60_000).expect("first window scan");
        cached(&context, &None, from_ms, from_ms + 60_000).expect("second request, same bound, cache reused");

        assert_eq!(
            scan_count(),
            before + 1,
            "a repeated request for an already-covered until_ms must not re-run the scan"
        );
    }

    // What used to be `widening_request_forces_a_rescan`: a request whose
    // `until_ms` grew past what a previous call asked for no longer forces a
    // rescan at all, because `run` always scans through `i64::MAX` (see the
    // module doc's account of why the nine-commit bounded-scan design was
    // unnecessary — `tokscale_core::get_window_usage` applies `until_ms` only
    // as a post-filter, so a scan bounded narrower than `i64::MAX` never read
    // fewer files or cost less). The cached entry already covers any
    // `until_ms`; a widen is answered by `narrow_to_request` alone.
    #[test]
    fn widening_request_reuses_one_scan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(&None, from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let before = scan_count();
        let context = test_context("widening-request");

        cached(&context, &None, from_ms, from_ms + 60_000).expect("first window scan");
        let result = cached(&context, &None, from_ms, from_ms + 65_000).expect("widened request, unchanged source");

        assert_eq!(
            scan_count(),
            before + 1,
            "a request whose until_ms grew past a prior call must be answered from the same \
             unbounded scan, not trigger a second one"
        );
        assert_eq!(result["untilMs"].as_i64(), Some(from_ms + 65_000));
    }

    // The production shape (`DashboardModel.cs:570`'s polling call): two REAL
    // `cached()` calls, both passing `until_ms = wall_clock_now_ms()` at
    // their own call moment, source unchanged, with a deliberate drift
    // between them. Kept from the bounded-scan design's own test suite
    // because the property it proves — an unchanged source polled with a
    // genuinely later `until_ms` reuses one scan — still holds, now trivially
    // (see the module doc): `run` always scans through `i64::MAX`, so there
    // is no soundness gate left to exercise; this is what is left of that
    // test once the gate it was built to prove is gone.
    #[test]
    fn production_widening_drift_reuses_one_scan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = wall_clock_now_ms() - 3_600_000;
        let key = cache_key(&None, from_ms);
        WINDOW_USAGE_CACHE
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&key);
        let context = test_context("production-widening-drift");
        let before = scan_count();

        // First poll: until_ms = now, exactly DashboardModel.cs:570's shape.
        cached(&context, &None, from_ms, wall_clock_now_ms()).expect("first poll");

        // The smallest sleep that buys a real, measurable drift between two
        // wall-clock reads a real caller a moment apart would also see.
        std::thread::sleep(Duration::from_millis(50));

        // Second poll: until_ms grew past the first call (ordinary drift),
        // same unchanged source.
        cached(&context, &None, from_ms, wall_clock_now_ms()).expect("second poll, drifted wider");

        assert_eq!(
            scan_count(),
            before + 1,
            "two real polls of an unchanged source, until_ms = now both times, must share one scan"
        );
    }

    // The finding this module's cache still exists to close: a stale entry
    // whose source-change token no longer matches the real source must be
    // rescanned, not served.
    #[test]
    fn genuine_source_change_forces_a_rescan() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(&None, from_ms);
        let context = test_context("source-change");
        let stale_at = Instant::now()
            .checked_sub(Duration::from_secs(crate::ONESHOT_MAX_AGE_SECS + 5))
            .expect("test clock has enough headroom to backdate");
        let sentinel = serde_json::json!({"sentinel": true});
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            cache.insert(
                key,
                (
                    stale_at,
                    /* token that cannot match the real source */ u64::MAX,
                    from_ms + 60_000,
                    sentinel,
                ),
            );
        }
        let before = scan_count();

        let result = cached(&context, &None, from_ms, from_ms + 60_000).expect("rescan after stale mismatched token");

        assert_eq!(
            scan_count(),
            before + 1,
            "a stale entry whose token no longer matches the source must be rescanned, \
             not served"
        );
        assert_ne!(result, serde_json::json!({"sentinel": true}));
    }

    #[test]
    fn different_from_uses_different_key() {
        assert_ne!(cache_key(&None, 1_700_000_000_000), cache_key(&None, 1_700_000_060_000));
    }

    #[test]
    fn empty_range_returns_empty_list_not_an_error() {
        // Guarded like the other tests here even though it doesn't touch the
        // cache map: it calls `run` directly, which bumps the shared
        // `SCAN_COUNT` atomic that `repeated_identical_request_reuses_one_scan`
        // and `genuine_source_change_forces_a_rescan` read before/after deltas
        // of — unguarded, a parallel run of this test could land inside that
        // window and make the delta look like an extra scan happened.
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let context = test_context("empty-range");
        // from > until: no message's timestamp can ever satisfy the filter.
        let value = run(&context, 1_700_000_060_000, 1_700_000_000_000).expect("empty window");
        assert_eq!(value["messages"].as_array().unwrap().len(), 0);
    }

    // The defect a false comment used to describe as already fixed: a cache
    // entry scanned through `cached_until` must not hand back messages past a
    // NARROWER request's own `until_ms`, even though it is served from cache
    // rather than rescanned.
    #[test]
    fn a_narrower_request_does_not_return_messages_past_its_own_until_ms() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(&None, from_ms);
        let context = test_context("narrow-request");
        let wide_until = from_ms + 120_000;
        let narrow_until = from_ms + 60_000;
        let token = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        )
        .unwrap_or(0);
        let wide = serde_json::json!({
            "fromMs": from_ms,
            "untilMs": wide_until,
            "messages": [
                {"timestamp": from_ms + 10_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
                // Past the narrower request's until_ms: must never come back below.
                {"timestamp": from_ms + 90_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
            ],
            "undatedCount": 0,
            "processingTimeMs": 0,
        });
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            cache.insert(key.clone(), (Instant::now(), token, wide_until, wide));
        }
        let before = scan_count();

        let result = cached(&context, &None, from_ms, narrow_until).expect("narrower request served from cache");

        assert_eq!(
            scan_count(),
            before,
            "a narrower request that the cache already covers must not trigger a rescan"
        );
        let messages = result["messages"].as_array().expect("messages array");
        assert!(
            messages
                .iter()
                .all(|message| message["timestamp"].as_i64().unwrap() < narrow_until),
            "no returned message may carry a timestamp at or past the request's own until_ms: {messages:?}"
        );
        assert_eq!(messages.len(), 1);
        assert_eq!(result["untilMs"].as_i64(), Some(narrow_until));
    }

    #[test]
    fn narrow_to_request_is_a_noop_when_the_cache_did_not_scan_further() {
        let data = serde_json::json!({"messages": [], "untilMs": 100});
        let unchanged = narrow_to_request(data.clone(), 100, 100);
        assert_eq!(unchanged, data);
    }

    // Round-7 finding: `compute`'s own recheck-under-lock used to return the
    // hit unnarrowed. Simulates the second of two concurrent callers with no
    // covering entry yet: the first has already published a WIDER scan by the
    // time this one enters `compute` and finds it under the `COMPUTE` lock.
    #[test]
    fn compute_narrows_a_cache_hit_found_under_its_own_lock() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let from_ms = 1_700_000_000_000;
        let key = cache_key(&None, from_ms);
        let context = test_context("compute-recheck-narrow");
        let wide_until = from_ms + 120_000;
        let narrow_until = from_ms + 60_000;
        let token = tokscale_core::local_source_change_token_with_source_context(
            context.resolved(),
            &context.parse_options(None, None),
        )
        .unwrap_or(0);
        let wide = serde_json::json!({
            "fromMs": from_ms,
            "untilMs": wide_until,
            "messages": [
                {"timestamp": from_ms + 10_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
                // Past the narrower caller's own until_ms: must never come back.
                {"timestamp": from_ms + 90_000, "client": "a", "providerId": "p", "modelId": "m",
                 "input": 1, "output": 1, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0,
                 "cost": 0.0, "isTurnStart": true},
            ],
            "undatedCount": 0,
            "processingTimeMs": 0,
        });
        {
            let mut cache =
                WINDOW_USAGE_CACHE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            cache.insert(key.clone(), (Instant::now(), token, wide_until, wide));
        }
        let before = scan_count();

        // `compute` directly, not `cached`: this is exactly the call the
        // narrower of two concurrent callers makes once it decides there is
        // no covering entry yet (`cached`'s own two exits already narrow
        // correctly and are not what this test is asserting about).
        let result = compute(&context, from_ms, narrow_until, key).expect("recheck hit, narrowed");

        assert_eq!(
            scan_count(),
            before,
            "a hit found under the COMPUTE lock must not trigger a second scan"
        );
        let messages = result["messages"].as_array().expect("messages array");
        assert!(
            messages
                .iter()
                .all(|message| message["timestamp"].as_i64().unwrap() < narrow_until),
            "no returned message may carry a timestamp at or past the caller's own \
             until_ms: {messages:?}"
        );
        assert_eq!(messages.len(), 1);
        assert_eq!(result["untilMs"].as_i64(), Some(narrow_until));
    }
}
