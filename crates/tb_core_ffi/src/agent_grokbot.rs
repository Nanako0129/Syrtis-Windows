//! Grok Bot weekly quota, kept separate from Grok Build.
//!
//! This adapter adds a quota source; it does not add a Bot session parser.
//!
//! Ported from macOS 451b4329 `agent_grokbot.rs`. Prefer the standalone Grok
//! Bot app's active account in `%APPDATA%\Grok Bot\sand-secrets.json`, sealed
//! by Electron safeStorage on Windows: `Local State` (the secrets file's
//! sibling) holds `os_crypt.encrypted_key` = `"DPAPI"` || CryptProtectData(key)
//! and each value is base64(`"v10"` || nonce || AES-256-GCM ciphertext || tag)
//! (`win_safe_storage`). Query DashboardService/GetSandUsageStatus with that
//! account and its selected team. Fall back to the Cursor IDE's `state.vscdb`
//! only when no desktop login exists; never once one does (a desktop login
//! that cannot be read, or that the user has not allowed, is an error, not a
//! reason to switch accounts).
//!
//! **Consent is the only gate on Windows.** macOS shows a Keychain dialog after
//! the in-app question; DPAPI unwraps silently for any process running as the
//! user. So `decode_desktop_secret` asks `keychain_consent` before EVERY use of
//! a desktop secret — `plaintext:v1:` values included (Q6-4, a deliberate
//! divergence from macOS) — and the key loader, which is the only code that
//! reads `Local State` or calls DPAPI, runs lazily behind that check. Consent
//! is re-read before the second (team id) decode, again before the decoded
//! sign-in is fingerprinted for the account scope, and once more immediately
//! before the request is sent. The contract is that a withdrawal takes effect
//! from the next refresh; a refresh already under way may finish. The re-reads
//! are best-effort hardening that often stop an in-flight fetch earlier, not a
//! guarantee (a withdrawal after the last one does not recall the request).
//!
//! Credentials are read-only and never logged or persisted by Syrtis; only HMAC
//! fingerprints reach the account-scope store. Every error is a fixed string.
//! Grok Bot / Cursor own token refresh; expired logins produce an actionable
//! error.
//!
//! Windows: no `TOKENBAR_*` path override. The config root, the usage URLs,
//! the consent check, the key loader and the account-scope resolvers come from
//! the caller (`agent_usage::GrokBotDeps`), whose only non-test value is
//! `dirs::config_dir()`, the two URL constants, `keychain_consent::allowed`,
//! `load_dpapi_key` and the real `agent_account_scope` functions.

use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, AuthoritativeIdKind, HistoryScope,
};
use crate::agent_kiro::{ResolveCredential, ResolveHistoryScope};
use crate::agent_quota_duration::DurationEvidence;
use crate::agent_usage::{
    provider_http_client_builder, read_response_body, AgentIdentity, ProviderCacheBinding,
    ProviderFetchFailure, ResponseReadFailure, TransportErrorFacts, TransportPhase, UsageWindow,
};
use base64::Engine as _;
use chrono::{DateTime, Utc};
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::time::Duration;

pub(crate) const GROK_BOT_USAGE_URL: &str =
    "https://cursor.com/api/dashboard/get-sand-usage-status";
pub(crate) const GROK_BOT_DESKTOP_USAGE_URL: &str =
    "https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus";
/// `state.vscdb` can be briefly locked by a running Cursor; wait rather than
/// fail the poll.
const SQLITE_BUSY_TIMEOUT: Duration = Duration::from_millis(3000);
pub(crate) const WEEKLY_WINDOW_KEY: &str = "weekly.v1";

/// Marker error for "a desktop login is here, but the user has not agreed to
/// let Syrtis read it". `fetch_grokbot` publishes it with
/// `source == "keychain-consent"`, which the app renders as the consent card
/// rather than an error. Never names the Keychain: Windows has none.
pub(crate) const GROK_BOT_KEYCHAIN_CONSENT_REQUIRED: &str =
    "Syrtis needs your permission to read the Grok Bot sign-in.";

/// Every Windows decrypt-side failure (R6-14): `Local State` missing or
/// unreadable, a key DPAPI will not unwrap, a value that is not `v10`, an
/// AES-GCM failure, non-UTF-8 plaintext. Always `Err`, never `Ok(None)`, so
/// none of them falls through to the Cursor login.
pub(crate) const GROK_BOT_LOGIN_UNREADABLE: &str =
    "Grok Bot login could not be read. Open Grok Bot and sign in again.";

/// An unwrapped safeStorage key. The production one is
/// `win_safe_storage::SafeStorageKey`; tests substitute a spy.
pub(crate) trait DesktopKey {
    /// Decrypt one base64 safeStorage value to UTF-8. Fixed-string errors.
    fn decrypt(&self, value_base64: &str) -> Result<String, String>;
}

/// Reads the given `Local State` and unwraps its key.
pub(crate) type LoadDesktopKey<'a> =
    dyn Fn(&Path) -> Result<Box<dyn DesktopKey>, String> + Send + Sync + 'a;

/// The consent check and the key loader the desktop route runs behind. Both
/// `'static` because the load runs on a blocking thread.
#[derive(Clone, Copy)]
pub(crate) struct DesktopAccess {
    pub consent: &'static (dyn Fn() -> bool + Send + Sync),
    /// Reads the given `Local State` and unwraps its key. The only code that
    /// touches `Local State` or DPAPI; called at most once per load, and only
    /// after consent.
    pub load_key: &'static LoadDesktopKey<'static>,
}

/// Production key loader: `Local State` -> `win_safe_storage::load_key`.
#[cfg(target_os = "windows")]
pub(crate) fn load_dpapi_key(local_state: &Path) -> Result<Box<dyn DesktopKey>, String> {
    let raw = std::fs::read(local_state).map_err(unreadable)?;
    let json: Value = serde_json::from_slice(&raw).map_err(unreadable)?;
    let key: Box<dyn DesktopKey> =
        Box::new(crate::win_safe_storage::load_key(&json).map_err(unreadable)?);
    Ok(key)
}

/// Off Windows there is no safeStorage reader; reading never succeeds and
/// never touches the file (the macOS-host test build only).
#[cfg(not(target_os = "windows"))]
pub(crate) fn load_dpapi_key(_local_state: &Path) -> Result<Box<dyn DesktopKey>, String> {
    Err(GROK_BOT_LOGIN_UNREADABLE.to_string())
}

#[cfg(target_os = "windows")]
impl DesktopKey for crate::win_safe_storage::SafeStorageKey {
    fn decrypt(&self, value_base64: &str) -> Result<String, String> {
        let plaintext = crate::win_safe_storage::decrypt(self, value_base64).map_err(unreadable)?;
        String::from_utf8(plaintext).map_err(|error| {
            let mut bytes = error.into_bytes();
            crate::win_safe_storage::wipe(&mut bytes);
            GROK_BOT_LOGIN_UNREADABLE.to_string()
        })
    }
}

#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
fn unreadable<E>(_: E) -> String {
    GROK_BOT_LOGIN_UNREADABLE.to_string()
}

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

enum GrokBotCredentials {
    /// `secrets_path` is only the account-scope location, never re-read.
    Desktop {
        access_token: String,
        team_id: Option<u64>,
        secrets_path: PathBuf,
    },
    Cursor(CursorCredentials, PathBuf),
}

impl GrokBotCredentials {
    /// The cache binds the exact request credential and every account selector.
    /// The common resolver HMACs this material; no raw token is persisted.
    fn scope_material(&self) -> (&'static str, &Path, String) {
        match self {
            Self::Desktop {
                access_token,
                team_id,
                secrets_path,
            } => (
                "grok-bot-desktop",
                secrets_path,
                serde_json::json!([access_token, team_id]).to_string(),
            ),
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

    /// Called only after the server accepts the request. A desktop JWT subject
    /// identifies the authenticated owner (signature unverified: a local
    /// history key only, never authorization); the selected team also scopes
    /// usage. No owner -> keep the quota but no durable history. Token
    /// rotation must not split history.
    fn history_owner(&self) -> Option<String> {
        let (owner, team): (String, Option<u64>) = match self {
            Self::Desktop {
                access_token,
                team_id,
                ..
            } => {
                // macOS's `jwt_payload` requires exactly three non-empty
                // segments; the Windows one is laxer, so check here rather
                // than change it for its other callers.
                let segments: Vec<&str> = access_token.split('.').collect();
                if segments.len() != 3 || segments.iter().any(|segment| segment.is_empty()) {
                    return None;
                }
                let claims = crate::agent_usage::jwt_payload(access_token)?;
                let subject = claims.get("sub")?.as_str()?.trim();
                if subject.is_empty() {
                    return None;
                }
                (subject.to_string(), *team_id)
            }
            Self::Cursor(credentials, _) => (credentials.user_id.clone(), None),
        };
        // JSON tuple encoding keeps owner/team boundaries unambiguous.
        Some(serde_json::json!([owner, team]).to_string())
    }
}

/// Returns `None` only when neither app has a login. A desktop login that is
/// unreadable or not allowed surfaces an error, never a silent switch to a
/// different IDE account.
///
/// Takes no `now`: the reset validation in `map_response` is a comparison
/// against the present, so the clock is read after the body arrives (macOS
/// `bf7a6b92`).
pub(crate) async fn fetch(
    config_dir: Option<PathBuf>,
    cursor_usage_url: &str,
    desktop_usage_url: &str,
    desktop: DesktopAccess,
    resolve_credential: &ResolveCredential,
    resolve_history_scope: &ResolveHistoryScope,
) -> Result<Option<GrokBotData>, ProviderFetchFailure> {
    let Some(config_dir) = config_dir else {
        return Ok(None);
    };
    // SQLite may wait on Cursor's lock (busy timeout) and DPAPI is a blocking
    // call; do not block the runtime.
    let loaded =
        match tokio::task::spawn_blocking(move || load_credentials(&config_dir, desktop)).await {
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
    let usage_url = match credentials {
        GrokBotCredentials::Desktop { .. } => desktop_usage_url,
        GrokBotCredentials::Cursor(..) => cursor_usage_url,
    };
    fetch_with_credentials(
        credentials,
        usage_url,
        desktop.consent,
        resolve_credential,
        resolve_history_scope,
    )
    .await
    .map(Some)
}

async fn fetch_with_credentials(
    credentials: GrokBotCredentials,
    usage_url: &str,
    consent: &(dyn Fn() -> bool + Send + Sync),
    resolve_credential: &ResolveCredential,
    resolve_history_scope: &ResolveHistoryScope,
) -> Result<GrokBotData, ProviderFetchFailure> {
    // The decoded desktop sign-in is used twice below: HMAC'd into the
    // account scope, then sent. Consent is re-read before each as best-effort
    // hardening: a Settings withdrawal that lands before a re-read stops the
    // fetch there. The promise itself is only "from the next refresh".
    let ensure_consent = || -> Result<(), ProviderFetchFailure> {
        if matches!(credentials, GrokBotCredentials::Desktop { .. }) && !consent() {
            return Err(ProviderFetchFailure::terminal(
                GROK_BOT_KEYCHAIN_CONSENT_REQUIRED,
            ));
        }
        Ok(())
    };
    ensure_consent()?;
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

    // The last consent read before the desktop sign-in leaves the machine
    // (best-effort, like the one above): a withdrawal during the scope resolve
    // or client build sends nothing.
    ensure_consent()?;
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
    let app = match credentials {
        GrokBotCredentials::Desktop { .. } => "Grok Bot",
        GrokBotCredentials::Cursor(..) => "Cursor",
    };
    let mut data =
        map_response_as(&body, Utc::now(), app).map_err(ProviderFetchFailure::terminal)?;
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
                GrokBotCredentials::Desktop { .. } => "Grok Bot",
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
        GrokBotCredentials::Desktop {
            access_token,
            team_id,
            ..
        } => {
            let mut request = client
                .post(usage_url)
                .bearer_auth(access_token)
                .header("connect-protocol-version", "1")
                .header("x-cursor-client-type", "sand")
                .header("x-ghost-mode", "true");
            if let Some(team_id) = team_id {
                request = request.header("x-cursor-team-id", team_id.to_string());
            }
            request
        }
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
/// `app` names the sign-in the request used ("Grok Bot" or "Cursor"), so a
/// failure points the user at the app that can fix it.
pub(crate) fn map_response_as(
    body: &str,
    now: DateTime<Utc>,
    app: &str,
) -> Result<GrokBotData, String> {
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
        return Err(format!("Grok Bot usage is unavailable. Open {app}, then refresh."));
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
        return Err(format!(
            "Grok Bot reported a quota reset that has already passed. Open {app}, then refresh."
        ));
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

/// `Local State` is the secrets file's sibling (R6-16): Electron keeps both in
/// the app's `userData` folder.
fn local_state_path(desktop_path: &Path) -> PathBuf {
    desktop_path.with_file_name("Local State")
}

fn load_credentials(
    config_dir: &Path,
    desktop: DesktopAccess,
) -> Result<Option<GrokBotCredentials>, String> {
    load_credentials_from_sources(
        &desktop_secrets_path(config_dir),
        &cursor_state_db_path(config_dir),
        desktop.consent,
        desktop.load_key,
    )
}

/// macOS shape: the desktop login first, then Cursor only when there is none.
/// Every desktop error — consent withheld included — propagates, so the
/// Cursor login is never used while a desktop login exists.
fn load_credentials_from_sources(
    desktop_path: &Path,
    cursor_path: &Path,
    consent: &dyn Fn() -> bool,
    load_key: &LoadDesktopKey<'_>,
) -> Result<Option<GrokBotCredentials>, String> {
    if let Some(credentials) = load_desktop_credentials_from(desktop_path, consent, load_key)? {
        return Ok(Some(credentials));
    }
    load_credentials_from(cursor_path)
        .map(|value| value.map(|c| GrokBotCredentials::Cursor(c, cursor_path.to_path_buf())))
}

/// Reads and parses `sand-secrets.json` (file I/O and JSON only — that a login
/// exists stays knowable without consent, which is what lets the app explain
/// itself first). Only the active account is used. Secrets are decoded by
/// `decode_desktop_secret`, which checks consent first every time; the key is
/// loaded lazily on the first encrypted value, after that check.
fn load_desktop_credentials_from(
    path: &Path,
    consent: &dyn Fn() -> bool,
    load_key: &LoadDesktopKey<'_>,
) -> Result<Option<GrokBotCredentials>, String> {
    let raw = match std::fs::read_to_string(path) {
        Ok(raw) => raw,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => {
            return Err(
                "Could not read Grok Bot login data. Open Grok Bot, then refresh.".to_string(),
            )
        }
    };
    let malformed =
        || "Grok Bot login data is invalid. Open Grok Bot and sign in again.".to_string();
    let root: Value = serde_json::from_str(&raw).map_err(|_| malformed())?;
    let root = root.as_object().ok_or_else(malformed)?;
    // Current versions keep a JSON-encoded account map. Only the active
    // account may supply credentials; never pick an arbitrary saved account.
    let accounts: Option<Value> = root
        .get("cursor-accounts")
        .map(|value| {
            let encoded = value.as_str().ok_or_else(malformed)?;
            serde_json::from_str(encoded).map_err(|_| malformed())
        })
        .transpose()?;
    let active = match accounts.as_ref() {
        Some(record) => {
            let map = record
                .get("accounts")
                .and_then(Value::as_object)
                .ok_or_else(malformed)?;
            match record.get("active") {
                Some(Value::Null) => None,
                Some(Value::String(id)) => Some(
                    map.get(id)
                        .and_then(Value::as_object)
                        .ok_or_else(malformed)?,
                ),
                _ => return Err(malformed()),
            }
        }
        None => None,
    };
    // Legacy stores have no account map. Once a map exists, a signed-out or
    // incomplete active account must never resurrect a stale top-level login.
    let login = match (accounts.as_ref(), active) {
        (None, _) => root,
        (Some(_), Some(active)) => active,
        (Some(_), None) => return Ok(None),
    };
    let Some(stored_token) = login.get("cursor-access-token") else {
        return Ok(None);
    };

    let local_state = local_state_path(path);
    let mut key: Option<Box<dyn DesktopKey>> = None;
    let mut decrypt = |value: &str| -> Result<String, String> {
        let key = match &mut key {
            Some(key) => key,
            slot => slot.insert(load_key(&local_state)?),
        };
        key.decrypt(value)
    };
    let access_token = decode_desktop_secret(
        stored_token.as_str().ok_or_else(malformed)?,
        consent,
        &mut decrypt,
    )?;
    if access_token.is_empty() {
        return Err(malformed());
    }
    let team_id = login
        .get("cursor-selected-team-id")
        .map(|value| {
            let team = decode_desktop_secret(
                value.as_str().ok_or_else(malformed)?,
                consent,
                &mut decrypt,
            )?;
            team.parse::<u64>()
                .ok()
                .filter(|id| *id > 0)
                .ok_or_else(malformed)
        })
        .transpose()?;
    Ok(Some(GrokBotCredentials::Desktop {
        access_token,
        team_id,
        secrets_path: path.to_path_buf(),
    }))
}

/// One desktop value: `plaintext:v1:<b64>`, `scoped:v1:<64 hex>:<b64>`, or
/// bare base64 safeStorage ciphertext (`v10`).
fn decode_desktop_secret(
    value: &str,
    consent: &dyn Fn() -> bool,
    decrypt: &mut dyn FnMut(&str) -> Result<String, String>,
) -> Result<String, String> {
    // The gate is the first statement, before any decode of any format. On
    // macOS it sits just before the Keychain decrypt and `plaintext:v1:`
    // values skip it, because there the OS dialog is a second gate and a
    // plaintext value raises none. On Windows nothing else asks, and the
    // Settings switch promises that turning it off stops Syrtis reading Grok
    // Bot's sign-in — plaintext sign-ins included (Q6-4, R6-13). Above
    // `decrypt`, so the key loader (the only reader of `Local State` and the
    // only DPAPI call) cannot run without it (R6-1).
    //
    // A closure re-read per value, not a snapshot: this runs twice per login
    // (access token, then team id), and a withdrawal between the two must stop
    // the second.
    if !consent() {
        return Err(GROK_BOT_KEYCHAIN_CONSENT_REQUIRED.to_string());
    }
    let malformed =
        || "Grok Bot login data could not be decoded. Open Grok Bot and sign in again.".to_string();
    if let Some(encoded) = value.strip_prefix("plaintext:v1:") {
        return String::from_utf8(
            base64::engine::general_purpose::STANDARD
                .decode(encoded)
                .map_err(|_| malformed())?,
        )
        .map_err(|_| malformed());
    }
    let encoded = if let Some(scoped) = value.strip_prefix("scoped:v1:") {
        let (scope, encoded) = scoped.split_once(':').ok_or_else(malformed)?;
        if scope.len() != 64 || !scope.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(malformed());
        }
        encoded
    } else {
        value
    };
    decrypt(encoded)
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

/// The Cursor route's wording, which the decoding tests exercise.
#[cfg(test)]
pub(crate) fn map_response(body: &str, now: DateTime<Utc>) -> Result<GrokBotData, String> {
    map_response_as(body, now, "Cursor")
}

#[cfg(test)]
pub(crate) mod tests {
    //! Ported from macOS 451b4329 `agent_grokbot.rs` tests. The response
    //! decoding and the Cursor route apply unchanged. The desktop-route tests
    //! run against `FakeKey` (a stand-in for the DPAPI key that "decrypts"
    //! base64(`v10` || plaintext)), so they run on every host; the real DPAPI
    //! round trip is `agent_usage::grokbot_tests::windows_dpapi`, Windows only.
    //! Differences from macOS, all deliberate: consent also gates
    //! `plaintext:v1:` values (Q6-4), and the gate precedes the key loader.
    use super::*;

    #[test]
    fn response_errors_name_the_app_the_route_signed_in_with() {
        let expired = r#"{"usagePercent": 10.0, "nextResetTimestampUtc": "2020-01-01T00:00:00Z", "hasNonZeroIncludedLimit": true}"#;
        let unavailable = r#"{"error": "x"}"#;
        for body in [expired, unavailable] {
            let desktop = map_response_as(body, now(), "Grok Bot").unwrap_err();
            assert!(desktop.ends_with("Open Grok Bot, then refresh."), "{desktop}");
            let cursor = map_response_as(body, now(), "Cursor").unwrap_err();
            assert!(cursor.ends_with("Open Cursor, then refresh."), "{cursor}");
        }
    }
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;

    /// Counts its decrypts; decrypts base64(`v10` || plaintext).
    pub(crate) struct FakeKey(pub(crate) Arc<AtomicUsize>);

    impl DesktopKey for FakeKey {
        fn decrypt(&self, value_base64: &str) -> Result<String, String> {
            self.0.fetch_add(1, Ordering::SeqCst);
            let blob = base64::engine::general_purpose::STANDARD
                .decode(value_base64)
                .map_err(unreadable)?;
            let plaintext = blob.strip_prefix(b"v10").ok_or_else(|| unreadable(()))?;
            String::from_utf8(plaintext.to_vec()).map_err(unreadable)
        }
    }

    /// A bare `v10` value as `FakeKey` reads it.
    pub(crate) fn sealed(plaintext: &str) -> String {
        base64::engine::general_purpose::STANDARD.encode([b"v10", plaintext.as_bytes()].concat())
    }

    /// Key loader + decrypt counters for one load.
    #[derive(Default)]
    struct Spy {
        key_loads: Arc<AtomicUsize>,
        decrypts: Arc<AtomicUsize>,
    }

    impl Spy {
        fn loader(&self) -> impl Fn(&Path) -> Result<Box<dyn DesktopKey>, String> + '_ {
            move |local_state| {
                assert_eq!(local_state.file_name().unwrap(), "Local State");
                self.key_loads.fetch_add(1, Ordering::SeqCst);
                Ok(Box::new(FakeKey(Arc::clone(&self.decrypts))) as Box<dyn DesktopKey>)
            }
        }

        fn counts(&self) -> (usize, usize) {
            (
                self.key_loads.load(Ordering::SeqCst),
                self.decrypts.load(Ordering::SeqCst),
            )
        }
    }

    fn no_key(_: &Path) -> Result<Box<dyn DesktopKey>, String> {
        panic!("the key loader must not run here")
    }

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
            "Grok Bot usage is unavailable. Open Cursor, then refresh."
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

    fn encoded(value: &[u8]) -> String {
        base64::engine::general_purpose::STANDARD.encode(value)
    }

    /// The account-map shape measured on Windows (Q6-1, 188): `cursor-accounts`
    /// is a JSON string, `active` a 64-char key into `accounts`, each account
    /// holding access token, profile and refresh token.
    fn account_map(active: Value, accounts: Value) -> String {
        serde_json::json!({
            "cursor-accounts": serde_json::json!({"active": active, "accounts": accounts}).to_string()
        })
        .to_string()
    }

    /// Writes `<dir>/Grok Bot/sand-secrets.json` and returns its path; the
    /// sibling `Local State` is never written here (`FakeKey` needs none).
    fn write_desktop(dir: &Path, contents: &str) -> PathBuf {
        let path = dir.join("Grok Bot").join("sand-secrets.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, contents).unwrap();
        path
    }

    #[test]
    fn standalone_active_account_loads_without_an_ide_login() {
        let (dir, cursor_path) = temp_state_db("desktop", &[]);
        let active = "a".repeat(64);
        let desktop_path = write_desktop(
            &dir,
            &account_map(
                Value::String(active.clone()),
                serde_json::json!({
                    "b".repeat(64): { "cursor-access-token": sealed("wrong-account") },
                    active: {
                        "cursor-access-token": sealed("desktop-test-token"),
                        "cursor-selected-team-id": sealed("42"),
                        "cursor-account-profile": "must-never-read-this",
                        "cursor-refresh-token": "must-never-read-this"
                    }
                }),
            ),
        );
        let spy = Spy::default();
        let credentials =
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &spy.loader())
                .unwrap()
                .expect("standalone login must supply quota credentials");
        assert_eq!(
            spy.counts(),
            (1, 2),
            "one lazy key load, exactly the token and the team decrypted"
        );
        let request = usage_request(
            &reqwest::Client::new(),
            &credentials,
            GROK_BOT_DESKTOP_USAGE_URL,
        )
        .build()
        .unwrap();
        assert_eq!(request.url().as_str(), GROK_BOT_DESKTOP_USAGE_URL);
        assert_eq!(
            request.headers()["authorization"],
            "Bearer desktop-test-token"
        );
        assert_eq!(request.headers()["x-cursor-team-id"], "42");
        assert_eq!(request.headers()["connect-protocol-version"], "1");
        assert_eq!(request.headers()["x-cursor-client-type"], "sand");
        assert_eq!(request.headers()["x-ghost-mode"], "true");
        assert!(!request.headers().contains_key("cookie"));
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn standalone_login_takes_precedence_and_failures_are_errors_not_fallbacks() {
        let (dir, cursor_path) = temp_state_db("desktop_priority", &signed_in_rows());
        let desktop_path = write_desktop(
            &dir,
            &serde_json::json!({ "cursor-access-token": sealed("native-token") }).to_string(),
        );
        let spy = Spy::default();
        let credentials =
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &spy.loader())
                .unwrap()
                .unwrap();
        assert!(matches!(credentials, GrokBotCredentials::Desktop { .. }));

        // R6-14: a key that cannot be loaded (Local State missing / DPAPI
        // refused) and a value that does not decrypt are both errors.
        let error = load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &|_| {
            Err(GROK_BOT_LOGIN_UNREADABLE.to_string())
        })
        .err()
        .unwrap();
        assert_eq!(error, GROK_BOT_LOGIN_UNREADABLE);
        std::fs::write(
            &desktop_path,
            serde_json::json!({ "cursor-access-token": encoded(b"v11not-v10") }).to_string(),
        )
        .unwrap();
        let error =
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &spy.loader())
                .err()
                .unwrap();
        assert_eq!(error, GROK_BOT_LOGIN_UNREADABLE);

        // Unreadable or malformed secrets files: errors, never Cursor.
        std::fs::write(&desktop_path, "invalid JSON").unwrap();
        assert!(
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &no_key).is_err()
        );
        std::fs::remove_file(&desktop_path).unwrap();
        std::fs::create_dir_all(&desktop_path).unwrap();
        assert!(
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &no_key).is_err()
        );
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn missing_desktop_login_preserves_cursor_fallback() {
        let (dir, cursor_path) = temp_state_db("fallback", &signed_in_rows());
        let credentials = load_credentials_from_sources(
            &dir.join("missing.json"),
            &cursor_path,
            &|| true,
            &no_key,
        )
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

    #[test]
    fn signed_out_desktop_does_not_select_an_inactive_account() {
        let (dir, cursor_path) = temp_state_db("signed_out", &[]);
        let path = write_desktop(
            &dir,
            &account_map(
                Value::Null,
                serde_json::json!({"previous-account": {"cursor-access-token": sealed("previous-token")}}),
            ),
        );
        assert!(
            load_credentials_from_sources(&path, &cursor_path, &|| true, &no_key)
                .unwrap()
                .is_none()
        );
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn desktop_secret_formats_and_invalid_team() {
        let ciphertext = encoded(b"encrypted-token");
        let scoped = format!("scoped:v1:{}:{ciphertext}", "a".repeat(64));
        assert_eq!(
            decode_desktop_secret(&scoped, &|| true, &mut |data: &str| {
                assert_eq!(data, ciphertext);
                Ok("decoded-token".to_string())
            })
            .unwrap(),
            "decoded-token"
        );
        assert_eq!(
            decode_desktop_secret(
                &format!("plaintext:v1:{}", encoded(b"dev-token")),
                &|| true,
                &mut |_: &str| unreachable!()
            )
            .unwrap(),
            "dev-token"
        );
        assert!(decode_desktop_secret(
            "scoped:v1:invalid:abc",
            &|| true,
            &mut |_: &str| unreachable!()
        )
        .is_err());

        let (dir, _) = temp_state_db("invalid_team", &[]);
        let path = write_desktop(
            &dir,
            &serde_json::json!({
                "cursor-access-token": format!("plaintext:v1:{}", encoded(b"token")),
                "cursor-selected-team-id": format!("plaintext:v1:{}", encoded(b"not-a-team"))
            })
            .to_string(),
        );
        assert!(load_desktop_credentials_from(&path, &|| true, &no_key).is_err());
        std::fs::remove_dir_all(dir).unwrap();
    }

    /// An encrypted desktop login — the shape the device check measured —
    /// with (dir, desktop path, cursor path).
    fn temp_encrypted_desktop_login(
        tag: &str,
        rows: &[(&str, &str)],
    ) -> (PathBuf, PathBuf, PathBuf) {
        let (dir, cursor_path) = temp_state_db(tag, rows);
        let desktop_path = write_desktop(
            &dir,
            &serde_json::json!({"cursor-access-token": sealed("granted-token")}).to_string(),
        );
        (dir, desktop_path, cursor_path)
    }

    /// R6-1, the whole point: without consent the key loader — the only code
    /// that reads `Local State` or calls DPAPI — never runs, and nothing is
    /// decrypted.
    #[test]
    fn desktop_login_is_not_decrypted_without_consent() {
        let (dir, desktop_path, cursor_path) = temp_encrypted_desktop_login("no_consent", &[]);
        let spy = Spy::default();
        let error =
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| false, &spy.loader())
                .err()
                .unwrap();
        assert_eq!(error, GROK_BOT_KEYCHAIN_CONSENT_REQUIRED);
        assert_eq!(spy.counts(), (0, 0), "no key load, no decrypt");
        std::fs::remove_dir_all(dir).unwrap();
    }

    /// Control for the test above: the same fixture does reach the key loader
    /// and the decrypt when consent is present.
    #[test]
    fn consented_desktop_login_still_decrypts() {
        let (dir, desktop_path, cursor_path) = temp_encrypted_desktop_login("consent", &[]);
        let spy = Spy::default();
        let credentials =
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &spy.loader())
                .unwrap()
                .expect("a consented desktop login must still load");
        assert!(matches!(credentials, GrokBotCredentials::Desktop { .. }));
        assert_eq!(spy.counts(), (1, 1));
        std::fs::remove_dir_all(dir).unwrap();
    }

    /// Withholding consent must not quietly fetch quota for a different
    /// account through the (perfectly usable) Cursor login.
    #[test]
    fn consent_absent_does_not_fall_back_to_the_cursor_login() {
        let (dir, desktop_path, cursor_path) =
            temp_encrypted_desktop_login("no_consent_no_fallback", &signed_in_rows());
        assert!(
            load_credentials_from_sources(
                &dir.join("missing.json"),
                &cursor_path,
                &|| false,
                &no_key
            )
            .unwrap()
            .is_some(),
            "the Cursor fixture must be loadable for this test to mean anything"
        );
        let error = load_credentials_from_sources(&desktop_path, &cursor_path, &|| false, &no_key)
            .err()
            .unwrap();
        assert_eq!(error, GROK_BOT_KEYCHAIN_CONSENT_REQUIRED);
        std::fs::remove_dir_all(dir).unwrap();
    }

    /// Q6-4 / R6-13, the deliberate divergence from macOS's
    /// `plaintext_desktop_secret_needs_no_consent`: on Windows consent covers
    /// every use of a desktop secret, so a plaintext login is refused without
    /// it, and loads with it without any key.
    #[test]
    fn plaintext_desktop_secret_needs_consent_on_windows() {
        let (dir, cursor_path) = temp_state_db("plaintext_consent", &signed_in_rows());
        let desktop_path = write_desktop(
            &dir,
            &serde_json::json!({
                "cursor-access-token": format!("plaintext:v1:{}", encoded(b"dev-token"))
            })
            .to_string(),
        );
        let error = load_credentials_from_sources(&desktop_path, &cursor_path, &|| false, &no_key)
            .err()
            .expect("a plaintext login must not be used without consent");
        assert_eq!(error, GROK_BOT_KEYCHAIN_CONSENT_REQUIRED);
        let credentials =
            load_credentials_from_sources(&desktop_path, &cursor_path, &|| true, &no_key)
                .unwrap()
                .expect("with consent a plaintext login loads without a key");
        assert!(matches!(
            credentials,
            GrokBotCredentials::Desktop { ref access_token, .. } if access_token == "dev-token"
        ));
        std::fs::remove_dir_all(dir).unwrap();
    }

    /// A withdrawal between the token and the team-id decode stops the second.
    #[test]
    fn consent_withdrawn_mid_load_stops_the_next_decrypt() {
        let (dir, cursor_path) = temp_state_db("withdrawn", &[]);
        let desktop_path = write_desktop(
            &dir,
            &serde_json::json!({
                "cursor-access-token": sealed("granted-token"),
                "cursor-selected-team-id": sealed("42")
            })
            .to_string(),
        );
        let asked = std::cell::Cell::new(0);
        let spy = Spy::default();
        let error = load_credentials_from_sources(
            &desktop_path,
            &cursor_path,
            &|| {
                asked.set(asked.get() + 1);
                asked.get() == 1
            },
            &spy.loader(),
        )
        .err()
        .unwrap();
        assert_eq!(error, GROK_BOT_KEYCHAIN_CONSENT_REQUIRED);
        assert_eq!(spy.counts(), (1, 1), "only the first decrypt ran");
        assert_eq!(asked.get(), 2, "consent must be re-read per value");
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn cursor_only_users_are_unaffected_by_the_consent_gate() {
        let (dir, cursor_path) = temp_state_db("cursor_only_no_consent", &signed_in_rows());
        let credentials = load_credentials_from_sources(
            &dir.join("missing.json"),
            &cursor_path,
            &|| false,
            &no_key,
        )
        .unwrap()
        .expect("the Cursor fallback must load with consent withheld");
        assert!(matches!(credentials, GrokBotCredentials::Cursor(..)));
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn current_account_map_never_resurrects_legacy_credentials() {
        let dir = temp_dir("legacy");
        for active in [Value::Null, Value::String("current".to_string())] {
            let mut root: Value =
                serde_json::from_str(&account_map(active, serde_json::json!({"current": {}})))
                    .unwrap();
            root["cursor-access-token"] = Value::String(sealed("stale-legacy-token"));
            let path = write_desktop(&dir, &root.to_string());
            assert!(load_desktop_credentials_from(&path, &|| true, &no_key)
                .unwrap()
                .is_none());
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
        assert_eq!(
            expired,
            "Grok Bot reported a quota reset that has already passed. Open Cursor, then refresh."
        );

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

    fn desktop_token(subject: &str, signature: &str, team_id: Option<u64>) -> GrokBotCredentials {
        let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .encode(serde_json::json!({"sub": subject}).to_string());
        raw_desktop_token(format!("header.{payload}.{signature}"), team_id)
    }

    fn raw_desktop_token(access_token: String, team_id: Option<u64>) -> GrokBotCredentials {
        GrokBotCredentials::Desktop {
            access_token,
            team_id,
            secrets_path: PathBuf::from("fixture-login"),
        }
    }

    #[test]
    fn history_owner_requires_a_compact_three_segment_jwt() {
        let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .encode(serde_json::json!({"sub": "user-a"}).to_string());
        assert_eq!(
            raw_desktop_token(format!("header.{payload}.signature"), None).history_owner(),
            Some(r#"["user-a",null]"#.to_string()),
            "control: the same claims in a three-segment token must yield an owner"
        );
        for malformed in [
            format!("header.{payload}"),
            format!("header.{payload}.signature.extra"),
            format!("header.{payload}."),
            format!(".{payload}.signature"),
        ] {
            assert!(
                raw_desktop_token(malformed.clone(), None)
                    .history_owner()
                    .is_none(),
                "{malformed}"
            );
        }
    }

    #[test]
    fn history_owner_decodes_url_safe_payload_alphabet() {
        let subject = "~~~???>>>";
        let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .encode(serde_json::json!({"sub": subject}).to_string());
        assert!(payload.contains('-') && payload.contains('_'), "{payload}");
        assert_eq!(
            desktop_token(subject, "credential-a", None).history_owner(),
            Some(serde_json::json!([subject, null]).to_string())
        );
    }

    /// macOS's desktop version: the cache binding follows the token and the
    /// team, history follows subject + team only.
    #[test]
    fn desktop_cache_binding_tracks_request_but_history_survives_token_rotation() {
        use crate::agent_account_scope::{test_support::TestRefreshScope, RefreshScopeTransaction};
        let resolver = TestRefreshScope::new("grok-bot", "grokbot-desktop-owner");
        let a = desktop_token("user-a", "credential-a", None);
        let rotated = desktop_token("user-a", "credential-b", None);
        let b = desktop_token("user-b", "credential-b", None);
        let team = desktop_token("user-a", "credential-a", Some(42));
        let resolve = |credentials: &GrokBotCredentials| {
            let (source, _, marker) = credentials.scope_material();
            resolver
                .resolve_current(source, "fixture-login", marker.as_bytes())
                .unwrap()
        };
        let a_scope = resolve(&a);
        assert_eq!(a_scope, resolve(&a));
        for other in [&rotated, &b, &team] {
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
        assert_ne!(history(&a), history(&team));
        let metadata = String::from_utf8(resolver.metadata_bytes()).unwrap();
        for private in ["user-a", "credential-a", "fixture-login"] {
            assert!(!metadata.contains(private));
        }
        assert!(raw_desktop_token("opaque-token".to_string(), None)
            .history_owner()
            .is_none());
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
