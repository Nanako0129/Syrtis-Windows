//! Grok Bot weekly quota, kept separate from Grok Build.
//!
//! This adapter adds a quota source; it does not add a Bot session parser.
//!
//! Ported from macOS 451b4329 `agent_grokbot.rs`, Cursor-IDE route only. macOS
//! prefers the standalone Grok Bot app's login (Electron safeStorage) and falls
//! back to the Cursor IDE's `state.vscdb` only when no desktop login exists. The
//! Windows desktop route (DPAPI, consent) is not ported yet; until it is, a
//! Grok Bot install is detected by the EXISTENCE of
//! `%APPDATA%\Grok Bot\sand-secrets.json` (metadata only — the file is never
//! opened) and answered with a fixed terminal error. It never falls back to
//! Cursor in that case: macOS's invariant is "never silently switch to a
//! different IDE account", and the Cursor route sends no team id, so even the
//! same login could show another team's meter.
//!
//! Credentials are read-only and never logged or persisted by Syrtis; only HMAC
//! fingerprints reach the account-scope store. Cursor owns token refresh;
//! expired logins produce an actionable error.
//!
//! Windows: no `TOKENBAR_*` path override. The config root, the usage URL and
//! the account-scope resolvers come from the caller (`agent_usage::GrokBotDeps`),
//! whose only non-test value is `dirs::config_dir()`, `GROK_BOT_USAGE_URL` and
//! the real `agent_account_scope` functions.

use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, AuthoritativeIdKind, HistoryScope,
};
use crate::agent_kiro::ResolveCredential;
use crate::agent_quota_duration::DurationEvidence;
use crate::agent_usage::{
    provider_http_client_builder, read_response_body, AgentIdentity, ProviderCacheBinding,
    ProviderFetchFailure, ResponseReadFailure, TransportErrorFacts, TransportPhase, UsageWindow,
};
use chrono::{DateTime, Utc};
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::time::Duration;

pub(crate) const GROK_BOT_USAGE_URL: &str =
    "https://cursor.com/api/dashboard/get-sand-usage-status";
/// The desktop route's endpoint (macOS). Unused until the Windows desktop route
/// lands; kept so both routes' destinations are stated in one place.
#[allow(dead_code)]
const GROK_BOT_DESKTOP_USAGE_URL: &str =
    "https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus";
/// `state.vscdb` can be briefly locked by a running Cursor; wait rather than
/// fail the poll.
const SQLITE_BUSY_TIMEOUT: Duration = Duration::from_millis(3000);
pub(crate) const WEEKLY_WINDOW_KEY: &str = "weekly.v1";

/// Terminal answer while the Windows desktop route is not implemented: a Grok
/// Bot install is present, so the Cursor login must not be used instead.
pub(crate) const GROK_BOT_DESKTOP_UNSUPPORTED: &str =
    "Grok Bot sign-in on Windows isn't supported yet.";

pub(crate) type ResolveHistoryScope =
    dyn Fn(&str, Option<(AuthoritativeIdKind, &str)>) -> Result<HistoryScope, AccountScopeError>;

#[derive(Debug)]
pub(crate) struct GrokBotData {
    pub identity: Option<AgentIdentity>,
    pub account_scope: Result<AccountScope, AccountScopeError>,
    pub history_scope: Result<HistoryScope, AccountScopeError>,
    pub cache_binding: Option<ProviderCacheBinding>,
    pub windows: Vec<UsageWindow>,
}

#[derive(Clone)]
struct CursorCredentials {
    user_id: String,
    access_token: String,
}

/// macOS also has a `Desktop { access_token, team_id }` variant; the Windows
/// desktop route adds it back.
enum GrokBotCredentials {
    Cursor(CursorCredentials, PathBuf),
}

impl GrokBotCredentials {
    /// The cache binds the exact request credential and every account selector.
    /// The common resolver HMACs this material; no raw token is persisted.
    fn scope_material(&self) -> (&'static str, &Path, String) {
        match self {
            Self::Cursor(credentials, db_path) => (
                "grok-bot-cursor-dashboard",
                db_path,
                serde_json::json!([credentials.user_id, credentials.access_token]).to_string(),
            ),
        }
    }

    fn resolve_account_scope(
        &self,
        resolve_credential: &ResolveCredential,
    ) -> Result<AccountScope, AccountScopeError> {
        let (source, path, marker) = self.scope_material();
        let location = agent_account_scope::canonical_file_location(path, None)?;
        resolve_credential("grok-bot", source, &location, marker.as_bytes())
    }

    /// Called only after the server accepts the request. Token rotation must
    /// not split history, so the owner is the Cursor user id, not the token.
    fn history_owner(&self) -> Option<String> {
        let (owner, team): (String, Option<u64>) = match self {
            Self::Cursor(credentials, _) => (credentials.user_id.clone(), None),
        };
        // JSON tuple encoding keeps owner/team boundaries unambiguous.
        Some(serde_json::json!([owner, team]).to_string())
    }
}

/// Returns `None` only when no login exists. A Grok Bot install must surface an
/// error, never silently switch to a different IDE account.
///
/// Takes no `now`: the reset validation in `map_response` is a comparison
/// against the present, so the clock is read after the body arrives (macOS
/// `bf7a6b92`).
pub(crate) async fn fetch(
    config_dir: Option<PathBuf>,
    usage_url: &str,
    resolve_credential: &ResolveCredential,
    resolve_history_scope: &ResolveHistoryScope,
) -> Result<Option<GrokBotData>, ProviderFetchFailure> {
    let Some(config_dir) = config_dir else {
        return Ok(None);
    };
    // SQLite may wait on Cursor's lock (busy timeout); do not block the runtime.
    let loaded = match tokio::task::spawn_blocking(move || load_credentials(&config_dir)).await {
        Ok(result) => result,
        Err(_) => {
            return Err(ProviderFetchFailure::terminal(
                "Could not read the Grok Bot login.",
            ))
        }
    };
    let credentials = match loaded {
        Ok(Some(c)) => c,
        Ok(None) => return Ok(None),
        Err(e) => return Err(ProviderFetchFailure::terminal(e)),
    };
    fetch_with_credentials(
        credentials,
        usage_url,
        resolve_credential,
        resolve_history_scope,
    )
    .await
    .map(Some)
}

async fn fetch_with_credentials(
    credentials: GrokBotCredentials,
    usage_url: &str,
    resolve_credential: &ResolveCredential,
    resolve_history_scope: &ResolveHistoryScope,
) -> Result<GrokBotData, ProviderFetchFailure> {
    let scope = credentials
        .resolve_account_scope(resolve_credential)
        .map_err(|_| {
            ProviderFetchFailure::terminal("Grok Bot account identity could not be verified.")
        })?;
    let binding = Some(ProviderCacheBinding::primary(scope.clone()));
    let client = provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|_| {
            ProviderFetchFailure::terminal("Grok Bot usage client could not be created.")
        })?;

    let response = usage_request(&client, &credentials, usage_url)
        .send()
        .await
        .map_err(|error| {
            ProviderFetchFailure::from_send_error(
                "Grok Bot usage request failed. Retrying automatically.",
                binding.clone(),
                &error,
            )
        })?;

    let status = response.status().as_u16();
    let body = read_response_body(status, false, || async {
        response.text().await.map_err(|error| {
            TransportErrorFacts::from_reqwest(&error, TransportPhase::ResponseBody)
        })
    })
    .await
    .map_err(|failure| response_failure(failure, &credentials, binding.clone()))?;

    // Taken here, after the body is in hand, so the expiry comparison below
    // judges the reading against the instant it was actually read.
    let mut data = map_response(&body, Utc::now()).map_err(ProviderFetchFailure::terminal)?;
    data.account_scope = Ok(scope);
    data.cache_binding = binding;
    data.history_scope = match credentials.history_owner() {
        Some(owner) => {
            resolve_history_scope("grok-bot", Some((AuthoritativeIdKind::OpaqueId, &owner)))
        }
        None => Err(AccountScopeError::NoTrustedEvidence),
    };
    Ok(data)
}

fn response_failure(
    failure: ResponseReadFailure,
    credentials: &GrokBotCredentials,
    binding: Option<ProviderCacheBinding>,
) -> ProviderFetchFailure {
    match failure {
        ResponseReadFailure::Transient(diagnostic) => ProviderFetchFailure::transient(
            "Grok Bot usage request failed. Retrying automatically.",
            binding,
            diagnostic,
        ),
        ResponseReadFailure::Terminal(401 | 403) => {
            let app = match credentials {
                GrokBotCredentials::Cursor(..) => "Cursor",
            };
            ProviderFetchFailure::terminal(format!(
                "{app} login expired. Open {app} and sign in again, then refresh."
            ))
        }
        ResponseReadFailure::Terminal(status) => {
            ProviderFetchFailure::terminal(format!("Grok Bot usage API returned {status}."))
        }
    }
}

fn usage_request(
    client: &reqwest::Client,
    credentials: &GrokBotCredentials,
    usage_url: &str,
) -> reqwest::RequestBuilder {
    let request = match credentials {
        GrokBotCredentials::Cursor(credentials, _) => client
            .post(usage_url)
            .header(reqwest::header::ORIGIN, "https://cursor.com")
            .header(reqwest::header::REFERER, "https://cursor.com/dashboard")
            .header(
                reqwest::header::COOKIE,
                format!(
                    "WorkosCursorSessionToken={}%3A%3A{}",
                    credentials.user_id, credentials.access_token
                ),
            ),
    };
    request
        .header(reqwest::header::ACCEPT, "application/json")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .header(reqwest::header::USER_AGENT, "TokenBar")
        .body("{}")
}

/// Pure response mapping, split out so the endpoint contract is unit-testable
/// without network or credentials. Accepts the dashboard's camelCase and
/// snake_case shapes (both observed in the wild).
pub(crate) fn map_response(body: &str, now: DateTime<Utc>) -> Result<GrokBotData, String> {
    let payload: Value = serde_json::from_str(body)
        .map_err(|_| "Grok Bot usage response could not be decoded.".to_string())?;
    let obj = payload
        .as_object()
        .ok_or_else(|| "Cursor returned an unexpected response.".to_string())?;

    if obj
        .get("error")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .is_some()
    {
        return Err("Grok Bot usage is unavailable. Open Grok Bot, then refresh.".to_string());
    }
    // Both spellings, like every other field below. Reading only camelCase
    // publishes a pooled team allowance as an individual weekly quota.
    if first_bool(
        obj,
        &[
            "usesPooledEnterpriseAllowance",
            "uses_pooled_enterprise_allowance",
        ],
    ) == Some(true)
    {
        return Err(
            "Grok Bot uses a pooled team allowance; no individual weekly quota is available."
                .to_string(),
        );
    }

    if first_bool(
        obj,
        &["hasNonZeroIncludedLimit", "has_non_zero_included_limit"],
    ) == Some(false)
    {
        return Err(
            "Grok Bot has no included allowance; no individual weekly quota is available."
                .to_string(),
        );
    }

    let used = first_f64(obj, &["usagePercent", "usage_percent"])
        .ok_or_else(|| "Cursor omitted the Grok Bot usage percentage.".to_string())?;
    if !used.is_finite() || !(0.0..=100.0).contains(&used) {
        return Err("Cursor returned an invalid Grok Bot usage percentage.".to_string());
    }

    let reset = first_timestamp(obj, &["nextResetTimestampUtc", "next_reset_timestamp_utc"])
        .ok_or_else(|| "Cursor omitted the Grok Bot reset time.".to_string())?;
    // An expired reset invalidates the reading, not just the reset: the field
    // reports the NEXT reset, and `usable_success` caches the card purely
    // because `weekly.v1` exists, so a stale reading would overwrite the
    // last-good entry rather than be discarded.
    if reset <= now {
        return Err(
            "Grok Bot reported a quota reset that has already passed. Open Grok Bot, then refresh."
                .to_string(),
        );
    }
    let start = first_timestamp(obj, &["currentPeriodStart", "current_period_start"]);
    // A start at or after the reset cannot describe the window that reset ends;
    // drop just the derived duration and keep the reading.
    let start = start.filter(|start| *start < reset);
    let duration = start
        .map(|start| DurationEvidence::provider(reset.timestamp(), (reset - start).num_seconds()));

    Ok(GrokBotData {
        account_scope: Err(AccountScopeError::NoTrustedEvidence),
        history_scope: Err(AccountScopeError::NoTrustedEvidence),
        cache_binding: None,
        identity: obj
            .get("grokPlanLabel")
            .and_then(Value::as_str)
            .filter(|plan| !plan.is_empty())
            .map(|plan| AgentIdentity {
                email: None,
                plan: Some(plan.to_string()),
            }),
        windows: vec![UsageWindow::from_used_percent(
            "Weekly".to_string(),
            used,
            Some(reset),
            now,
            None,
        )
        .with_identity(
            WEEKLY_WINDOW_KEY,
            Some(WEEKLY_WINDOW_KEY.to_string()),
            duration,
            None,
        )],
    })
}

fn first_f64(obj: &serde_json::Map<String, Value>, keys: &[&str]) -> Option<f64> {
    keys.iter()
        .find_map(|k| obj.get(*k).and_then(Value::as_f64))
}

fn first_bool(obj: &serde_json::Map<String, Value>, keys: &[&str]) -> Option<bool> {
    keys.iter()
        .find_map(|k| obj.get(*k).and_then(Value::as_bool))
}

/// Accept ISO-8601 strings and epoch seconds-or-milliseconds (number or
/// numeric string) — the dashboard has sent all three shapes.
fn first_timestamp(obj: &serde_json::Map<String, Value>, keys: &[&str]) -> Option<DateTime<Utc>> {
    keys.iter().find_map(|k| parse_timestamp(obj.get(*k)?))
}

fn parse_timestamp(value: &Value) -> Option<DateTime<Utc>> {
    match value {
        Value::Number(n) => {
            let secs = n.as_f64()?;
            let secs = if secs > 100_000_000_000.0 {
                secs / 1000.0
            } else {
                secs
            };
            DateTime::from_timestamp(secs as i64, 0)
        }
        Value::String(s) => {
            let trimmed = s.trim();
            if trimmed.is_empty() {
                return None;
            }
            if let Ok(secs) = trimmed.parse::<f64>() {
                let secs = if secs > 100_000_000_000.0 {
                    secs / 1000.0
                } else {
                    secs
                };
                if let Some(dt) = DateTime::from_timestamp(secs as i64, 0) {
                    return Some(dt);
                }
            }
            DateTime::parse_from_rfc3339(trimmed)
                .ok()
                .map(|dt| dt.with_timezone(&Utc))
        }
        _ => None,
    }
}

/// Both stores live under the roaming config root (`dirs::config_dir()`, i.e.
/// `%APPDATA%`), like Claude Desktop's.
fn desktop_secrets_path(config_dir: &Path) -> PathBuf {
    config_dir.join("Grok Bot").join("sand-secrets.json")
}

fn cursor_state_db_path(config_dir: &Path) -> PathBuf {
    config_dir
        .join("Cursor")
        .join("User")
        .join("globalStorage")
        .join("state.vscdb")
}

fn load_credentials(config_dir: &Path) -> Result<Option<GrokBotCredentials>, String> {
    load_credentials_from_sources(
        &desktop_secrets_path(config_dir),
        &cursor_state_db_path(config_dir),
    )
}

/// macOS shape: the desktop login first, then Cursor only when there is none.
fn load_credentials_from_sources(
    desktop_path: &Path,
    cursor_path: &Path,
) -> Result<Option<GrokBotCredentials>, String> {
    if let Some(credentials) = load_desktop_credentials_from(desktop_path)? {
        return Ok(Some(credentials));
    }
    load_credentials_from(cursor_path)
        .map(|value| value.map(|c| GrokBotCredentials::Cursor(c, cursor_path.to_path_buf())))
}

/// The Windows desktop route's placeholder: an existence check only. Metadata
/// is read, the file is never opened. Anything other than "not found" — a
/// file, a directory, an unreadable parent — counts as installed, so an
/// unclear answer fails closed rather than reading Cursor's login instead.
fn load_desktop_credentials_from(path: &Path) -> Result<Option<GrokBotCredentials>, String> {
    match std::fs::symlink_metadata(path) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        _ => Err(GROK_BOT_DESKTOP_UNSUPPORTED.to_string()),
    }
}

fn load_credentials_from(db_path: &Path) -> Result<Option<CursorCredentials>, String> {
    if !db_path.is_file() {
        return Ok(None);
    }
    let conn =
        rusqlite::Connection::open_with_flags(db_path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(|_| "Could not read the Cursor login database.".to_string())?;
    conn.busy_timeout(SQLITE_BUSY_TIMEOUT)
        .map_err(|_| "Could not read the Cursor login database.".to_string())?;

    let mut stmt = conn
        .prepare(
            "SELECT key, value FROM ItemTable WHERE key IN \
             ('cursorAuth/accessToken','glass.lastSignedInAuthId','cursorAuth/cachedScopedProfile')",
        )
        .map_err(|_| "Could not read the Cursor login database.".to_string())?;
    let rows: Vec<(String, String)> = stmt
        .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
        .map_err(|_| "Could not read the Cursor login database.".to_string())?
        .collect::<Result<_, _>>()
        .map_err(|_| "Could not read the Cursor login database.".to_string())?;

    let mut token: Option<String> = None;
    let mut identity: Option<String> = None;
    let mut profile: Option<String> = None;
    for (key, value) in &rows {
        let normalized = normalized_stored_string(value);
        match key.as_str() {
            "cursorAuth/accessToken" => token = normalized,
            "glass.lastSignedInAuthId" => identity = normalized,
            "cursorAuth/cachedScopedProfile" => profile = normalized,
            _ => {}
        }
    }

    let (Some(token), user_id) = (
        token.filter(|t| !t.is_empty()),
        identity
            .as_deref()
            .and_then(extract_user_id)
            .or_else(|| profile.as_deref().and_then(extract_user_id)),
    ) else {
        return Ok(None);
    };
    let Some(user_id) = user_id.filter(|id| !id.is_empty()) else {
        return Ok(None);
    };
    Ok(Some(CursorCredentials {
        user_id,
        access_token: token,
    }))
}

/// Values in `state.vscdb` are sometimes JSON-encoded strings (wrapped in an
/// extra layer of quotes) — unwrap one layer when present, else use as-is.
fn normalized_stored_string(value: &str) -> Option<String> {
    if value.is_empty() {
        return None;
    }
    if value.starts_with('"') {
        if let Ok(Value::String(inner)) = serde_json::from_str(value) {
            return if inner.is_empty() { None } else { Some(inner) };
        }
    }
    Some(value.to_string())
}

/// Cursor user ids look like `user_...` (20+ alphanumerics). Scan for the
/// first occurrence rather than depending on the surrounding JSON shape.
fn extract_user_id(text: &str) -> Option<String> {
    let start = text.find("user_")?;
    let rest = &text[start + "user_".len()..];
    let len = rest
        .char_indices()
        .take_while(|(_, c)| c.is_ascii_alphanumeric())
        .map(|(i, c)| i + c.len_utf8())
        .last()
        .unwrap_or(0);
    (len >= 20).then(|| format!("user_{}", &rest[..len]))
}

#[cfg(test)]
pub(crate) mod tests {
    //! Ported from macOS 451b4329 `agent_grokbot.rs` tests: the response
    //! decoding and the Cursor route apply unchanged. The desktop-route tests
    //! (decrypt, consent, account map) wait for the Windows desktop route; the
    //! installed-app guard replaces them here.
    use super::*;

    const USER_ID: &str = "user_abcDEF1234567890xyzAB";

    fn now() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-09-10T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc)
    }

    #[test]
    fn parses_camel_case_meter() {
        let data = map_response(
            r#"{
                "usagePercent": 22.5,
                "currentPeriodStart": "2026-09-08T15:40:06.727001+00:00",
                "nextResetTimestampUtc": "2026-09-15T15:40:06.727001+00:00",
                "hasNonZeroIncludedLimit": true
            }"#,
            now(),
        )
        .unwrap();
        assert_eq!(data.windows.len(), 1);
        assert_eq!(data.windows[0].label_for_test(), "Weekly");
        // 100 - 22.5 = 77.5 remaining.
        assert!((data.windows[0].remaining_for_test() - 77.5).abs() < 1e-9);
    }

    #[test]
    fn parses_snake_case_meter_with_epoch_millis() {
        let data = map_response(
            r#"{
                "usage_percent": 90,
                "current_period_start": 1788855606727,
                "next_reset_timestamp_utc": 1789460406727
            }"#,
            now(),
        )
        .unwrap();
        assert!((data.windows[0].remaining_for_test() - 10.0).abs() < 1e-9);
    }

    #[test]
    fn distinguishes_no_included_allowance_from_unused_allowance() {
        for (flag, meter, reset) in [
            (
                "hasNonZeroIncludedLimit",
                "usagePercent",
                "nextResetTimestampUtc",
            ),
            (
                "has_non_zero_included_limit",
                "usage_percent",
                "next_reset_timestamp_utc",
            ),
        ] {
            let mut body = serde_json::json!({
                flag: false, meter: 0, reset: "2026-09-15T15:40:06Z"
            });
            let error = map_response(&body.to_string(), now()).unwrap_err();
            assert_eq!(
                error,
                "Grok Bot has no included allowance; no individual weekly quota is available."
            );

            // A real allowance with zero usage still has all of its quota left.
            body[flag] = Value::Bool(true);
            let unused = map_response(&body.to_string(), now()).unwrap();
            assert_eq!(unused.windows[0].remaining_for_test(), 100.0);

            // Older responses omit this optional signal entirely.
            body.as_object_mut().unwrap().remove(flag);
            assert_eq!(
                map_response(&body.to_string(), now())
                    .unwrap()
                    .windows
                    .len(),
                1
            );
        }
    }

    #[test]
    fn rejects_out_of_range_percent() {
        for used in [-1.0, 140.0] {
            let body = serde_json::json!({"usagePercent": used,
                "nextResetTimestampUtc": "2026-09-15T15:40:06Z"})
            .to_string();
            assert!(map_response(&body, now()).is_err());
        }
    }

    #[test]
    fn missing_percent_is_an_error() {
        let err = map_response(
            r#"{"nextResetTimestampUtc": "2026-09-15T15:40:06Z"}"#,
            now(),
        )
        .unwrap_err();
        assert!(err.contains("usage percentage"), "unexpected: {err}");
    }

    #[test]
    fn missing_reset_is_an_error() {
        let err = map_response(r#"{"usagePercent": 10.0}"#, now()).unwrap_err();
        assert!(err.contains("reset time"), "unexpected: {err}");
    }

    #[test]
    fn remote_error_is_bounded() {
        let err = map_response(r#"{"error": "private-response-canary"}"#, now()).unwrap_err();
        assert_eq!(
            err,
            "Grok Bot usage is unavailable. Open Grok Bot, then refresh."
        );
    }

    #[test]
    fn non_object_body_is_an_error() {
        assert!(map_response("[1,2]", now()).is_err());
    }

    #[test]
    fn normalizes_quoted_and_plain_values() {
        assert_eq!(
            normalized_stored_string("\"abc123\"").as_deref(),
            Some("abc123")
        );
        assert_eq!(normalized_stored_string("plain").as_deref(), Some("plain"));
        assert_eq!(normalized_stored_string(""), None);
        assert_eq!(normalized_stored_string("\"\""), None);
    }

    #[test]
    fn extracts_user_id_from_identity_and_profile_shapes() {
        assert_eq!(
            extract_user_id(&format!("glass-{USER_ID}-suffix")).as_deref(),
            Some(USER_ID)
        );
        assert_eq!(
            extract_user_id(&format!("{{\"id\":\"{USER_ID}\",\"x\":1}}")).as_deref(),
            Some(USER_ID)
        );
        assert_eq!(extract_user_id("user_short"), None);
        assert_eq!(extract_user_id("no id here"), None);
    }

    fn temp_dir(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "tb_grokbot_{tag}_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// Write `rows` to a fresh `state.vscdb` at `path`.
    pub(crate) fn write_state_db(path: &Path, rows: &[(&str, &str)]) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let conn = rusqlite::Connection::open(path).unwrap();
        conn.execute_batch("CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT)")
            .unwrap();
        for (key, value) in rows {
            conn.execute(
                "INSERT INTO ItemTable (key, value) VALUES (?1, ?2)",
                rusqlite::params![key, value],
            )
            .unwrap();
        }
    }

    /// Write `rows` to a fresh temp `state.vscdb` and return (dir, path).
    fn temp_state_db(tag: &str, rows: &[(&str, &str)]) -> (PathBuf, PathBuf) {
        let dir = temp_dir(tag);
        let path = dir.join("state.vscdb");
        write_state_db(&path, rows);
        (dir, path)
    }

    fn signed_in_rows() -> [(&'static str, &'static str); 2] {
        [
            ("cursorAuth/accessToken", "ide-token"),
            ("glass.lastSignedInAuthId", USER_ID),
        ]
    }

    #[test]
    fn missing_db_yields_no_credentials() {
        let dir = temp_dir("missing");
        let loaded = load_credentials_from(&dir.join("state.vscdb")).unwrap();
        assert!(loaded.is_none());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn loads_token_and_user_id() {
        let (dir, path) = temp_state_db(
            "ok",
            &[
                ("cursorAuth/accessToken", "\"tok-123\""),
                ("glass.lastSignedInAuthId", &format!("glass-{USER_ID}")),
            ],
        );
        let creds = load_credentials_from(&path)
            .unwrap()
            .expect("credentials load");
        assert_eq!(creds.access_token, "tok-123");
        assert_eq!(creds.user_id, USER_ID);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn falls_back_to_profile_for_user_id() {
        let (dir, path) = temp_state_db(
            "profile",
            &[
                ("cursorAuth/accessToken", "tok-plain"),
                (
                    "cursorAuth/cachedScopedProfile",
                    &format!("{{\"userId\":\"{USER_ID}\"}}"),
                ),
            ],
        );
        let creds = load_credentials_from(&path)
            .unwrap()
            .expect("credentials load");
        assert_eq!(creds.user_id, USER_ID);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn token_without_user_id_yields_no_credentials() {
        let (dir, path) = temp_state_db("noid", &[("cursorAuth/accessToken", "tok-123")]);
        assert!(load_credentials_from(&path).unwrap().is_none());
        std::fs::remove_dir_all(&dir).ok();
    }

    /// The production paths are `<config>\Grok Bot\sand-secrets.json` and
    /// `<config>\Cursor\User\globalStorage\state.vscdb`.
    #[test]
    fn paths_derive_from_the_config_root() {
        let root = Path::new("root");
        assert_eq!(
            desktop_secrets_path(root),
            root.join("Grok Bot").join("sand-secrets.json")
        );
        assert_eq!(
            cursor_state_db_path(root),
            root.join("Cursor")
                .join("User")
                .join("globalStorage")
                .join("state.vscdb")
        );
    }

    #[test]
    fn missing_desktop_login_preserves_cursor_fallback() {
        let (dir, cursor_path) = temp_state_db("fallback", &signed_in_rows());
        let credentials = load_credentials_from_sources(&dir.join("missing.json"), &cursor_path)
            .unwrap()
            .unwrap();
        let request = usage_request(&reqwest::Client::new(), &credentials, GROK_BOT_USAGE_URL)
            .build()
            .unwrap();
        assert_eq!(request.url().as_str(), GROK_BOT_USAGE_URL);
        assert_eq!(
            request.headers()["cookie"],
            format!("WorkosCursorSessionToken={USER_ID}%3A%3Aide-token").as_str()
        );
        assert!(!request.headers().contains_key("authorization"));
        assert!(!request.headers().contains_key("x-cursor-team-id"));
        std::fs::remove_dir_all(dir).unwrap();
    }

    /// R6-12, the Windows form of macOS's "never silently switch to a different
    /// IDE account" (its consent-absent-does-not-fall-back test): a Grok Bot
    /// install answers with the fixed terminal even though the Cursor login
    /// here is perfectly usable. A directory in the file's place proves the
    /// guard never reads it — reading a directory fails, and that would surface
    /// as a different error.
    #[test]
    fn installed_grok_bot_does_not_fall_back_to_the_cursor_login() {
        let (dir, cursor_path) = temp_state_db("installed", &signed_in_rows());
        // Control: the same Cursor fixture loads when no install is present.
        assert!(
            load_credentials_from_sources(&dir.join("missing.json"), &cursor_path)
                .unwrap()
                .is_some(),
            "the Cursor fixture must be loadable for this test to mean anything"
        );
        let file = dir.join("sand-secrets.json");
        std::fs::write(&file, "not even JSON").unwrap();
        let as_dir = dir.join("as-dir").join("sand-secrets.json");
        std::fs::create_dir_all(&as_dir).unwrap();
        for desktop in [file, as_dir] {
            let error = load_credentials_from_sources(&desktop, &cursor_path)
                .err()
                .expect("an installed Grok Bot must not yield credentials");
            assert_eq!(error, GROK_BOT_DESKTOP_UNSUPPORTED);
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn weekly_window_has_stable_identity_and_provider_duration() {
        let data = map_response(
            r#"{
            "usagePercent": 30,
            "currentPeriodStart": "2026-09-08T12:00:00Z",
            "nextResetTimestampUtc": "2026-09-15T12:00:00Z"
        }"#,
            now(),
        )
        .unwrap();
        let window = serde_json::to_value(&data.windows[0]).unwrap();
        assert_eq!(window["cardId"], "weekly.v1");
        assert_eq!(window["paceStatus"]["windowKey"], "weekly.v1");
        assert_eq!(window["paceStatus"]["durationSeconds"], 604800);
        assert_eq!(window["paceStatus"]["durationSource"], "provider");
        assert_eq!(window["paceStatus"]["state"], "learningHistory");
        let data = map_response(
            r#"{
            "usagePercent": 30, "nextResetTimestampUtc": "2026-09-15T12:00:00Z"
        }"#,
            now(),
        )
        .unwrap();
        let window = serde_json::to_value(&data.windows[0]).unwrap();
        assert_eq!(window["paceStatus"]["state"], "learningDuration");
    }

    #[test]
    fn invalid_reset_bounds_are_rejected_while_a_valid_pair_still_maps() {
        let ok = map_response(
            r#"{
            "usagePercent": 30,
            "currentPeriodStart": "2026-09-08T12:00:00Z",
            "nextResetTimestampUtc": "2026-09-15T12:00:00Z"
        }"#,
            now(),
        )
        .unwrap();
        assert_eq!(
            serde_json::to_value(&ok.windows[0]).unwrap()["paceStatus"]["durationSeconds"],
            604800
        );

        let expired = map_response(
            r#"{
            "usagePercent": 30, "nextResetTimestampUtc": "2026-09-09T12:00:00Z"
        }"#,
            now(),
        )
        .unwrap_err();
        assert!(expired.contains("already passed"), "got {expired}");

        assert!(map_response(
            r#"{
            "usagePercent": 30, "nextResetTimestampUtc": "2026-09-10T12:00:00Z"
        }"#,
            now()
        )
        .unwrap_err()
        .contains("already passed"));

        for start in ["2026-09-15T12:00:00Z", "2026-09-16T12:00:00Z"] {
            let body = format!(
                r#"{{"usagePercent": 30, "currentPeriodStart": "{start}",
                     "nextResetTimestampUtc": "2026-09-15T12:00:00Z"}}"#
            );
            let data = map_response(&body, now()).unwrap();
            let window = serde_json::to_value(&data.windows[0]).unwrap();
            assert_eq!(window["cardId"], "weekly.v1", "the quota reading survives");
            assert_eq!(
                window["paceStatus"]["state"], "learningDuration",
                "but the unusable span is not published as provider duration"
            );
        }
    }

    fn cursor(user_id: &str, token: &str) -> GrokBotCredentials {
        GrokBotCredentials::Cursor(
            CursorCredentials {
                user_id: user_id.to_string(),
                access_token: token.to_string(),
            },
            PathBuf::from("fixture-login"),
        )
    }

    /// macOS's desktop-token version, on the Cursor route: the cache binding
    /// follows the request credential, history follows the user id only.
    #[test]
    fn cache_binding_tracks_request_but_history_survives_token_rotation() {
        use crate::agent_account_scope::{test_support::TestRefreshScope, RefreshScopeTransaction};
        let resolver = TestRefreshScope::new("grok-bot", "grokbot-owner");
        let a = cursor("user-a", "credential-a");
        let rotated = cursor("user-a", "credential-b");
        let b = cursor("user-b", "credential-b");
        let resolve = |credentials: &GrokBotCredentials| {
            let (source, _, marker) = credentials.scope_material();
            resolver
                .resolve_current(source, "fixture-login", marker.as_bytes())
                .unwrap()
        };
        let a_scope = resolve(&a);
        assert_eq!(a_scope, resolve(&a));
        for other in [&rotated, &b] {
            assert_ne!(a_scope, resolve(other));
        }
        let history = |credentials: &GrokBotCredentials| {
            let owner = credentials.history_owner().unwrap();
            resolver
                .resolve_history("grok-bot", Some((AuthoritativeIdKind::OpaqueId, &owner)))
                .unwrap()
        };
        assert_eq!(history(&a), history(&rotated));
        assert_ne!(history(&a), history(&b));
        assert_eq!(a.history_owner().as_deref(), Some(r#"["user-a",null]"#));
        let metadata = String::from_utf8(resolver.metadata_bytes()).unwrap();
        for private in ["user-a", "credential-a", "fixture-login"] {
            assert!(!metadata.contains(private));
        }
        resolver.cleanup();
    }

    #[tokio::test]
    async fn http_failure_classification_precedes_body_and_keeps_request_binding() {
        use crate::agent_account_scope::{test_support::TestRefreshScope, RefreshScopeTransaction};
        let credentials = cursor("user-a", "credential-a");
        let resolver = TestRefreshScope::new("grok-bot", "grokbot-binding");
        let binding = Some(ProviderCacheBinding::primary(
            resolver
                .resolve_current("fixture", "account-a", b"marker-a")
                .unwrap(),
        ));
        for status in [401, 403, 404, 429, 500, 503] {
            let failure = read_response_body(status, false, || async {
                panic!("must not read error body")
            })
            .await
            .unwrap_err();
            match response_failure(failure, &credentials, binding.clone()) {
                ProviderFetchFailure::Transient {
                    attempt_binding, ..
                } => {
                    assert!(status == 429 || status >= 500);
                    assert_eq!(attempt_binding, binding);
                }
                ProviderFetchFailure::Terminal { display } => {
                    assert!([401, 403, 404].contains(&status));
                    if status != 404 {
                        assert!(display.contains("Open Cursor and sign in again"));
                    }
                }
            }
        }
        resolver.cleanup();
    }

    #[test]
    fn native_meter_includes_plan_and_does_not_misreport_a_pooled_allowance() {
        let data = map_response(
            r#"{
            "usagePercent": 25, "nextResetTimestampUtc": "2026-09-15T15:40:06Z",
            "grokPlanLabel": "SuperGrok"
        }"#,
            now(),
        )
        .unwrap();
        assert_eq!(data.identity.unwrap().plan.as_deref(), Some("SuperGrok"));
        for body in [
            r#"{"usagePercent": 25, "nextResetTimestampUtc": "2026-09-15T15:40:06Z",
                "usesPooledEnterpriseAllowance": true}"#,
            r#"{"usage_percent": 25, "next_reset_timestamp_utc": "2026-09-15T15:40:06Z",
                "uses_pooled_enterprise_allowance": true}"#,
        ] {
            assert!(map_response(body, now())
                .unwrap_err()
                .contains("pooled team allowance"));
        }
    }
}
