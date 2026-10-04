//! Antigravity (Google Code Assist) usage/quota — ported from codexbar's
//! Antigravity provider. Antigravity 2.0 (the IDE and the `antigravity-cli`
//! that replaced `gemini-cli`) does NOT persist per-message token counts
//! locally, so a contribution-graph style breakdown isn't derivable; the quota
//! API is the only usable signal. We mirror codexbar's `auto` source:
//!
//! 1. **Local IDE API (`cli`)** — when Antigravity is running, find its
//!    `language_server` process (carrying a `--csrf_token`), discover its
//!    listening ports with platform-native process tools, and call the local
//!    Connect-RPC `GetUserStatus` over loopback TLS. Live, no token refresh,
//!    no disk writes.
//! 2. **OAuth remote (`oauth`)** — otherwise read the shared Google creds under
//!    `GEMINI_CLI_HOME` (falling back to `~/.gemini`), refresh against Google
//!    (client id/secret scanned from the installed Antigravity.app binary), and
//!    hit the `cloudcode-pa.googleapis.com` Code Assist quota endpoints.
//!
//! Both yield per-model "remaining fraction + reset" which map to `UsageWindow`s.

use crate::agent_account_scope::{
    self, AccountScope, AccountScopeError, AuthoritativeIdKind, HistoryScope, RefreshCheckpoint,
    RefreshScopeTransaction,
};
use crate::agent_usage::{
    clean_plan, parse_datetime, percent_encode, provider_http_client_builder, read_response_body,
    request_after_verified_binding, AgentIdentity, ProviderCacheBinding, ProviderFetchFailure,
    ResponseReadFailure, SafeTransportDiagnostic, TransportErrorFacts, TransportPhase, UsageWindow,
};
use base64::Engine;
use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{json, value::RawValue, Value};
use std::collections::{BTreeMap, BTreeSet, HashMap};
#[cfg(windows)]
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

const LANG_SERVICE: &str = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
const CODE_ASSIST_BASE: &str = "https://cloudcode-pa.googleapis.com/v1internal";
const GOOGLE_TOKEN_URL: &str = "https://oauth2.googleapis.com/token";
const REFRESH_SAFETY_SECS: i64 = 60;

pub(crate) const ANTIGRAVITY_UNCONFIGURED_ERROR: &str =
    "Antigravity is not logged in. Re-login in Antigravity.";
/// A credential that exists but cannot be used (permission error, corrupt
/// JSON, ...). Distinct from `ANTIGRAVITY_UNCONFIGURED_ERROR`: a genuinely
/// absent file means nothing is configured, but a file that exists and can't
/// be read belongs to a configured account with a broken credential and must
/// say so rather than reading as "you never set this up".
const ANTIGRAVITY_UNREADABLE_ERROR: &str =
    "Antigravity credentials could not be read. Re-login in Antigravity.";
/// A Code Assist 401. A captured account rewrites it (`fetch_captured_with`):
/// re-login in Antigravity would sign in the wrong account.
const ANTIGRAVITY_AUTH_EXPIRED: &str = "Antigravity Google auth expired. Re-login in Antigravity.";

#[derive(Debug)]
pub(crate) struct Fetched {
    pub source: String,
    pub identity: Option<AgentIdentity>,
    pub account_scope: Result<AccountScope, AccountScopeError>,
    pub history_scope: Result<HistoryScope, AccountScopeError>,
    pub cache_binding: Option<ProviderCacheBinding>,
    pub windows: Vec<UsageWindow>,
}

#[derive(Debug)]
enum LocalAttempt {
    Success(Fetched),
    RouteMiss,
}

#[derive(Debug)]
enum PrimaryAttempt<Context> {
    Success(Fetched),
    Forbidden(Context),
    SchemaContradiction {
        context: Context,
        failure: ProviderFetchFailure,
    },
    Transient(Context),
    FinalFailure(ProviderFetchFailure),
}

async fn fetch_with<
    Local,
    LocalFuture,
    Primary,
    PrimaryFuture,
    Secondary,
    SecondaryFuture,
    Context,
>(
    local_attempt: Local,
    primary_remote_attempt: Primary,
    secondary_remote_attempt: Secondary,
) -> Result<Fetched, ProviderFetchFailure>
where
    Local: FnOnce() -> LocalFuture,
    LocalFuture: std::future::Future<Output = LocalAttempt>,
    Primary: FnOnce() -> PrimaryFuture,
    PrimaryFuture: std::future::Future<Output = PrimaryAttempt<Context>>,
    Secondary: FnOnce(Context) -> SecondaryFuture,
    SecondaryFuture: std::future::Future<Output = Result<Fetched, ProviderFetchFailure>>,
{
    if let LocalAttempt::Success(fetched) = local_attempt().await {
        return Ok(fetched);
    }

    match primary_remote_attempt().await {
        PrimaryAttempt::Success(fetched) => Ok(fetched),
        PrimaryAttempt::Forbidden(context) => secondary_remote_attempt(context).await,
        PrimaryAttempt::SchemaContradiction { context, failure } => {
            match secondary_remote_attempt(context).await {
                Ok(fetched) => Ok(fetched),
                Err(_) => Err(failure),
            }
        }
        PrimaryAttempt::Transient(context) => match secondary_remote_attempt(context).await {
            Ok(fetched) => Ok(fetched),
            Err(failure) => Err(failure),
        },
        PrimaryAttempt::FinalFailure(failure) => Err(failure),
    }
}

/// Auto: prefer the live Local IDE API, then the OAuth remote API, and finally
/// (Windows only) the `agy` CLI usage command when the earlier routes are
/// unavailable.
pub(crate) async fn fetch(now: DateTime<Utc>) -> Result<Fetched, ProviderFetchFailure> {
    let primary = fetch_with(
        || async move {
            match fetch_local_ide(now).await {
                Ok(local) if !local.windows.is_empty() => LocalAttempt::Success(local),
                Ok(_) | Err(_) => LocalAttempt::RouteMiss,
            }
        },
        || fetch_oauth_primary(now),
        |context| fetch_oauth_secondary(context, now),
    )
    .await;
    agy_leg(primary, now).await
}

#[cfg(windows)]
async fn agy_leg(
    primary: Result<Fetched, ProviderFetchFailure>,
    now: DateTime<Utc>,
) -> Result<Fetched, ProviderFetchFailure> {
    with_agy_fallback(primary, || fetch_agy_cli(now)).await
}

/// This repository ships Windows only; its non-Windows build exists to run the
/// tests, so the CLI leg is not wired there.
#[cfg(not(windows))]
async fn agy_leg(
    primary: Result<Fetched, ProviderFetchFailure>,
    _now: DateTime<Utc>,
) -> Result<Fetched, ProviderFetchFailure> {
    primary
}

// ── agy CLI route ───────────────────────────────────────────────────────────
//
// A user signed in only through the `agy` CLI has neither a running IDE nor
// `oauth_creds.json`; `agy` keeps its tokens in Credential Manager. Measured on
// Windows (2026-09-23): a signed-OUT `agy --print` does not fail, it starts a
// browser OAuth flow and waits. So a background poll may spawn it only when
// agy's credential exists, the route is not latched, and DNS resolves.

#[cfg(any(windows, test))]
const AGY_PAUSED_MESSAGE: &str =
    "Antigravity CLI quota check paused after a failed attempt. Restart Syrtis, or sign in to agy again, to retry.";

/// The pause after a run that did not finish in time. Measured on a Windows
/// host (agy 1.2.16, 2026-10-03): with a stale credential `agy --print /usage`
/// printed "Authentication required. Please visit the URL to log in:" on
/// stderr and kept waiting past its own `--print-timeout 30s`, so a timeout is
/// most likely agy waiting for a browser sign-in.
#[cfg(any(windows, test))]
const AGY_TIMED_OUT_MESSAGE: &str =
    "Antigravity CLI quota check timed out; agy was probably waiting for a sign-in. Sign in to agy again, or restart Syrtis, to retry.";

/// agy's own login. Syrtis only ever reads it: the poll path for `LastWritten`,
/// and the captured-account path (`agy_read_call`) for the blob.
const AGY_CREDENTIAL_TARGET: &str = "gemini:antigravity";

#[cfg(windows)]
const AGY_STDOUT_CAP: u64 = 1 << 20;

/// Why the `agy` leg produced no windows. Only the two pauses are ever shown;
/// every other reason surfaces the earlier routes' failure instead.
#[cfg(any(windows, test))]
#[derive(Debug, PartialEq, Eq)]
enum AgyFailure {
    Paused,
    /// Latched after a run that timed out (see `AGY_TIMED_OUT_MESSAGE`).
    TimedOut,
    Unavailable,
}

/// A run's outcome as far as the latch cares: whether a process was created,
/// and for a created one whether it timed out (the latch records that so the
/// card can name the timeout).
#[cfg(any(windows, test))]
#[derive(Debug)]
enum AgyRunFailure {
    NotStarted,
    Failed,
    TimedOut,
}

#[derive(Debug)]
pub(crate) struct CredentialUnreadable;

/// Once a spawned `agy` fails, it is not spawned again while its credential
/// keeps the same `LastWritten` — a failure may be the signed-out browser
/// prompt, and repeating it every poll is the harm. A re-login (which rewrites
/// the credential) or an app restart (the latch is in memory) re-arms it.
/// Merely running agy need not: with a still-valid token it signs in silently
/// and may leave the credential untouched (unmeasured), which is why the paused
/// message names only restart and re-login. No timed re-arm, on purpose: with
/// a dead credential each re-arm is another sign-in page.
///
/// Check-then-set is not atomic across the await on the run. That is safe
/// because there is at most one in-flight agent-usage fetch per process:
/// `with_publication_gate` (lib.rs) is held across `agent_usage::run`, and the
/// C# `AgentUsageFetchCoordinator` shares one fetch across callers.
#[cfg(any(windows, test))]
struct AgyLatch(std::sync::Mutex<Option<(u64, bool)>>);

#[cfg(any(windows, test))]
impl AgyLatch {
    const fn new() -> Self {
        Self(std::sync::Mutex::new(None))
    }

    fn state(&self) -> std::sync::MutexGuard<'_, Option<(u64, bool)>> {
        self.0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// `Some(timed_out)` while latched on this credential write.
    fn blocks(&self, last_written: u64) -> Option<bool> {
        self.state()
            .filter(|(latched, _)| *latched == last_written)
            .map(|(_, timed_out)| timed_out)
    }

    /// `(LastWritten, whether the failed run timed out)`, or `None` to clear.
    fn set(&self, latched: Option<(u64, bool)>) {
        *self.state() = latched;
    }
}

#[cfg(windows)]
static AGY_LATCH: AgyLatch = AgyLatch::new();

/// The `agy` route's arbitration. When the CLI cannot answer, the caller sees
/// the ORIGINAL failure rather than the CLI's (as on macOS), with one Windows
/// exception: a latched route says so, instead of the misleading
/// "not logged in" the earlier routes produce for a CLI-only user.
#[cfg(any(windows, test))]
async fn with_agy_fallback<Agy, AgyFuture>(
    primary: Result<Fetched, ProviderFetchFailure>,
    agy: Agy,
) -> Result<Fetched, ProviderFetchFailure>
where
    Agy: FnOnce() -> AgyFuture,
    AgyFuture: std::future::Future<Output = Result<Fetched, AgyFailure>>,
{
    match primary {
        Ok(fetched) => Ok(fetched),
        Err(primary_failure) if should_try_agy_fallback(&primary_failure) => match agy().await {
            Ok(fetched) => Ok(fetched),
            Err(AgyFailure::Paused) => Err(ProviderFetchFailure::terminal(AGY_PAUSED_MESSAGE)),
            Err(AgyFailure::TimedOut) => Err(ProviderFetchFailure::terminal(AGY_TIMED_OUT_MESSAGE)),
            Err(AgyFailure::Unavailable) => Err(primary_failure),
        },
        Err(primary_failure) => Err(primary_failure),
    }
}

#[cfg(any(windows, test))]
fn should_try_agy_fallback(failure: &ProviderFetchFailure) -> bool {
    matches!(failure, ProviderFetchFailure::Terminal { .. })
}

/// At most one candidate: agy's installer location, else the first ABSOLUTE
/// PATH entry holding a file named exactly `agy.exe`. Empty and relative
/// entries are skipped and PATHEXT is not consulted (no .cmd/.bat shims), so a
/// file planted in the working directory or a script wrapper never runs.
/// Not cached: agy may be installed after launch, and the fixed path sees that
/// even though the process's PATH snapshot does not.
#[cfg(any(windows, test))]
fn agy_executable_from(
    local_app_data: Option<&std::ffi::OsStr>,
    path: Option<&std::ffi::OsStr>,
    is_file: impl Fn(&Path) -> bool,
) -> Option<PathBuf> {
    let installed = local_app_data
        .map(|root| Path::new(root).join("agy").join("bin").join("agy.exe"))
        .filter(|candidate| candidate.is_absolute() && is_file(candidate));
    installed.or_else(|| {
        std::env::split_paths(path?)
            .filter(|dir| dir.is_absolute())
            .map(|dir| dir.join("agy.exe"))
            .find(|candidate| is_file(candidate))
    })
}

/// One poll of the `agy` leg: executable -> credential -> latch -> DNS -> run.
///
/// Every dependency is injected so the ordering and the latch table are
/// tested without a CLI, Credential Manager or the network:
/// - no process created (not found, credential absent or unreadable, DNS
///   refused, spawn failed): no latch;
/// - process created and usage parsed: clear the latch;
/// - process created and anything else: latch on the credential's
///   `LastWritten` read AFTER the run (agy may rewrite it while failing).
#[cfg(any(windows, test))]
async fn fetch_agy_cli_gated<Cred, Dns, DnsFuture, Run, RunFuture>(
    now: DateTime<Utc>,
    executable: Option<PathBuf>,
    credential_last_written: Cred,
    latch: &AgyLatch,
    endpoint_resolves: Dns,
    run: Run,
) -> Result<Fetched, AgyFailure>
where
    Cred: Fn() -> Result<Option<u64>, CredentialUnreadable>,
    Dns: FnOnce() -> DnsFuture,
    DnsFuture: std::future::Future<Output = bool>,
    Run: FnOnce(PathBuf) -> RunFuture,
    RunFuture: std::future::Future<Output = Result<Vec<u8>, AgyRunFailure>>,
{
    let Some(executable) = executable else {
        return Err(AgyFailure::Unavailable);
    };
    // Absent and unreadable both fail closed: without agy's credential a run
    // is the signed-out browser prompt.
    let Ok(Some(before)) = credential_last_written() else {
        return Err(AgyFailure::Unavailable);
    };
    if let Some(timed_out) = latch.blocks(before) {
        return Err(if timed_out { AgyFailure::TimedOut } else { AgyFailure::Paused });
    }
    // #329 (macOS): with an unresolvable token endpoint `agy --print` escalates
    // to interactive OAuth, and a post-spawn timeout cannot stop it across a
    // sleep. Only not spawning does.
    if !endpoint_resolves().await {
        return Err(AgyFailure::Unavailable);
    }
    let (parsed, timed_out) = match run(executable).await {
        Err(AgyRunFailure::NotStarted) => return Err(AgyFailure::Unavailable),
        Err(AgyRunFailure::Failed) => (None, false),
        Err(AgyRunFailure::TimedOut) => (None, true),
        // The raw output and the parse error are dropped here on purpose.
        Ok(stdout) => (parse_agy_usage(&stdout, now).ok(), false),
    };
    match parsed {
        Some(fetched) => {
            latch.set(None);
            Ok(fetched)
        }
        None => {
            // Credential gone or unreadable after the run: the gate already
            // closes the route while that lasts; the pre-run value keeps it
            // closed if the same credential comes back.
            let after = match credential_last_written() {
                Ok(Some(after)) => after,
                Ok(None) | Err(_) => before,
            };
            latch.set(Some((after, timed_out)));
            // Paused from this poll on, not the next: returning Unavailable
            // here would show the primary "not logged in" for the one poll
            // in which agy was in fact found, signed in and run. A timeout is
            // latched like any other failure (no timed re-arm: it is most
            // likely the sign-in wait), only named differently.
            Err(if timed_out { AgyFailure::TimedOut } else { AgyFailure::Paused })
        }
    }
}

#[cfg(windows)]
async fn fetch_agy_cli(now: DateTime<Utc>) -> Result<Fetched, AgyFailure> {
    let executable = agy_executable_from(
        std::env::var_os("LOCALAPPDATA").as_deref(),
        std::env::var_os("PATH").as_deref(),
        Path::is_file,
    );
    fetch_agy_cli_gated(
        now,
        executable,
        read_agy_credential_last_written,
        &AGY_LATCH,
        oauth_endpoint_resolves,
        run_agy_cli,
    )
    .await
}

/// Existence and `LastWritten` of agy's Credential Manager entry, nothing
/// else. `CredReadW` has no metadata-only mode, so the secret enters this
/// process regardless; it is zeroed before `CredFree`, and no pointer into the
/// `CREDENTIALW` (blob, `UserName`, `Comment`) leaves the unsafe block. No
/// keyring crate and no `Debug` type touches it, so nothing can format it.
#[cfg(windows)]
fn read_agy_credential_last_written() -> Result<Option<u64>, CredentialUnreadable> {
    use windows_sys::Win32::Foundation::{GetLastError, ERROR_NOT_FOUND};
    use windows_sys::Win32::Security::Credentials::{
        CredFree, CredReadW, CREDENTIALW, CRED_TYPE_GENERIC,
    };

    let target: Vec<u16> = AGY_CREDENTIAL_TARGET
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let mut credential: *mut CREDENTIALW = std::ptr::null_mut();
    // SAFETY: `target` is NUL-terminated and outlives the call. On success
    // CredReadW hands back one allocation that is only read here, its blob is
    // written within `CredentialBlobSize`, and it is freed exactly once.
    unsafe {
        if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) == 0 {
            return if GetLastError() == ERROR_NOT_FOUND {
                Ok(None)
            } else {
                Err(CredentialUnreadable)
            };
        }
        if credential.is_null() {
            return Err(CredentialUnreadable);
        }
        let written = (*credential).LastWritten;
        let blob = (*credential).CredentialBlob;
        if !blob.is_null() {
            for offset in 0..(*credential).CredentialBlobSize as usize {
                std::ptr::write_volatile(blob.add(offset), 0);
            }
        }
        CredFree(credential.cast::<core::ffi::c_void>());
        Ok(Some(
            (u64::from(written.dwHighDateTime) << 32) | u64::from(written.dwLowDateTime),
        ))
    }
}

#[cfg(windows)]
const OAUTH_TOKEN_HOST: &str = "oauth2.googleapis.com";

/// DNS only, 2 s: the escalation is gated on resolution failing, and this runs
/// ahead of a subprocess already allowed 35 s. An empty answer is a failure.
#[cfg(windows)]
async fn oauth_endpoint_resolves() -> bool {
    match tokio::time::timeout(
        std::time::Duration::from_secs(2),
        tokio::net::lookup_host((OAUTH_TOKEN_HOST, 443)),
    )
    .await
    {
        Ok(Ok(mut addresses)) => addresses.next().is_some(),
        Ok(Err(_)) | Err(_) => false,
    }
}

/// Direct spawn, no shell and no Job Object (measured: a native exe whose only
/// child is conhost; a job could also kill a browser agy started). The working
/// directory is agy's own bin directory, not whatever Syrtis inherited.
/// Turns off agy's own auto-update for the runs Syrtis starts (#204).
///
/// On a run whose update check is due (agy throttles it to once per 15
/// minutes), agy spawns `agy --bg-updater`, which can start further agy
/// children of its own. Those get a fresh console rather than the windowless
/// one `CREATE_NO_WINDOW` gave this run, and on Windows 11 with Windows
/// Terminal as the default terminal that console is handed to Terminal: a
/// visible window that takes the foreground (reproduced on 188, agy 1.2.16).
/// With this variable agy logs "Auto-update disabled via environment variable"
/// and starts no updater. The value is `true`: `1` was measured NOT to disable
/// it. agy run by the user in a terminal still updates itself.
#[cfg(windows)]
const AGY_DISABLE_AUTO_UPDATE: (&str, &str) = ("AGY_CLI_DISABLE_AUTO_UPDATE", "true");

/// The `agy --print /usage` command Syrtis runs, without spawning it.
#[cfg(windows)]
fn agy_command(executable: &Path, bin_dir: &Path) -> tokio::process::Command {
    let mut command = tokio::process::Command::new(executable);
    command
        .args([
            "--print",
            "/usage",
            "--output-format",
            "json",
            "--print-timeout",
            "30s",
        ])
        .current_dir(bin_dir)
        .env(AGY_DISABLE_AUTO_UPDATE.0, AGY_DISABLE_AUTO_UPDATE.1)
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .kill_on_drop(true);
    command
}

#[cfg(windows)]
async fn run_agy_cli(executable: PathBuf) -> Result<Vec<u8>, AgyRunFailure> {
    use tokio::io::AsyncReadExt as _;

    let bin_dir = executable.parent().ok_or(AgyRunFailure::NotStarted)?;
    let mut child = agy_command(&executable, bin_dir)
        .spawn()
        .map_err(|_| AgyRunFailure::NotStarted)?;
    // `child` moves into the future, so a timeout or an early return drops it
    // and `kill_on_drop` ends the process.
    let run = async move {
        let stdout = child.stdout.take().ok_or(AgyRunFailure::Failed)?;
        let mut output = Vec::new();
        stdout
            .take(AGY_STDOUT_CAP + 1)
            .read_to_end(&mut output)
            .await
            .map_err(|_| AgyRunFailure::Failed)?;
        if output.len() as u64 > AGY_STDOUT_CAP {
            return Err(AgyRunFailure::Failed);
        }
        let status = child.wait().await.map_err(|_| AgyRunFailure::Failed)?;
        if !status.success() {
            return Err(AgyRunFailure::Failed);
        }
        Ok(output)
    };
    tokio::time::timeout(std::time::Duration::from_secs(35), run)
        .await
        .map_err(|_| AgyRunFailure::TimedOut)?
}

// ── Local IDE API ───────────────────────────────────────────────────────────

struct ProcInfo {
    pid: i32,
    csrf_token: String,
    extension_port: Option<u16>,
    extension_csrf: Option<String>,
}

async fn fetch_local_ide(now: DateTime<Utc>) -> Result<Fetched, String> {
    let processes = discover_local_ide()?;

    let client = reqwest::Client::builder()
        .danger_accept_invalid_certs(true) // loopback language_server uses a self-signed cert
        .timeout(std::time::Duration::from_secs(8))
        .build()
        .map_err(|e| format!("build Antigravity local client: {e}"))?;
    let body = json!({
        "metadata": {
            "ideName": "antigravity",
            "extensionName": "antigravity",
            "ideVersion": "unknown",
            "locale": "en",
        }
    });

    let mut last_err = "Antigravity local IDE API not reachable".to_string();
    for (port, csrf) in local_api_candidates(processes) {
        let url = format!("https://127.0.0.1:{port}{LANG_SERVICE}");
        let resp = client
            .post(&url)
            .header("X-Codeium-Csrf-Token", &csrf)
            .header("Connect-Protocol-Version", "1")
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .json(&body)
            .send()
            .await;
        let resp = match resp {
            Ok(r) => r,
            Err(e) => {
                last_err = format!("local request failed: {e}");
                continue;
            }
        };
        if !resp.status().is_success() {
            last_err = format!("local API returned {}", resp.status().as_u16());
            continue;
        }
        let Ok(text) = resp.text().await else {
            continue;
        };
        match parse_user_status(&text, now) {
            Ok(mut fetched) if !fetched.windows.is_empty() => {
                fetched.account_scope = resolve_local_account_scope(fetched.identity.as_ref());
                fetched.history_scope = resolve_local_history_scope(fetched.identity.as_ref());
                fetched.cache_binding = fetched
                    .account_scope
                    .as_ref()
                    .ok()
                    .cloned()
                    .map(ProviderCacheBinding::primary);
                return Ok(fetched);
            }
            Ok(_) => last_err = "local API returned no model quotas".to_string(),
            Err(e) => last_err = e,
        }
    }
    Err(last_err)
}

fn local_api_candidates(processes: Vec<(ProcInfo, Vec<u16>)>) -> Vec<(u16, String)> {
    let mut candidates = Vec::new();
    for (proc, ports) in processes {
        // language-server ports use the language-server CSRF; the extension
        // server (if advertised) carries its own token.
        candidates.extend(
            ports
                .into_iter()
                .map(|port| (port, proc.csrf_token.clone())),
        );
        if let Some(port) = proc.extension_port {
            if let Some(csrf) = proc.extension_csrf.as_ref() {
                candidates.push((port, csrf.clone()));
            }
            candidates.push((port, proc.csrf_token.clone()));
        }
    }
    candidates
}

#[cfg(not(windows))]
fn discover_local_ide() -> Result<Vec<(ProcInfo, Vec<u16>)>, String> {
    let proc = detect_process()?;
    let ports = listening_ports(proc.pid)?;
    Ok(vec![(proc, ports)])
}

#[cfg(not(windows))]
fn detect_process() -> Result<ProcInfo, String> {
    let output = Command::new("/bin/ps")
        .args(["-ax", "-o", "pid=,command="])
        .output()
        .map_err(|e| format!("run ps: {e}"))?;
    let stdout = String::from_utf8_lossy(&output.stdout);

    let mut saw_antigravity = false;
    for line in stdout.lines() {
        let line = line.trim_start();
        let Some((pid_str, cmd)) = line.split_once(' ') else {
            continue;
        };
        let Ok(pid) = pid_str.trim().parse::<i32>() else {
            continue;
        };
        let lower = cmd.to_lowercase();
        if !is_language_server(&lower) || !is_antigravity(&lower) {
            continue;
        }
        saw_antigravity = true;
        let Some(csrf) = extract_flag(cmd, "--csrf_token") else {
            continue;
        };
        return Ok(ProcInfo {
            pid,
            csrf_token: csrf,
            extension_port: extract_flag(cmd, "--extension_server_port")
                .and_then(|s| s.parse().ok()),
            extension_csrf: extract_flag(cmd, "--extension_server_csrf_token"),
        });
    }
    if saw_antigravity {
        Err("Antigravity is running but no CSRF token was found".to_string())
    } else {
        Err("Antigravity is not running".to_string())
    }
}

fn is_language_server(lower_cmd: &str) -> bool {
    lower_cmd.contains("language_server")
}

fn is_antigravity(lower_cmd: &str) -> bool {
    (lower_cmd.contains("--app_data_dir") && lower_cmd.contains("antigravity"))
        || lower_cmd.contains("/antigravity/")
}

/// Value of `flag` in a command line, accepting either `flag value` or `flag=value`.
fn extract_flag(cmd: &str, flag: &str) -> Option<String> {
    let idx = cmd.find(flag)?;
    let rest = &cmd[idx + flag.len()..];
    let rest = rest.trim_start_matches(['=', ' ']);
    let value: String = rest.chars().take_while(|c| !c.is_whitespace()).collect();
    (!value.is_empty()).then_some(value)
}

#[cfg(not(windows))]
fn listening_ports(pid: i32) -> Result<Vec<u16>, String> {
    let lsof = ["/usr/sbin/lsof", "/usr/bin/lsof"]
        .into_iter()
        .find(|p| Path::new(p).exists())
        .ok_or_else(|| "lsof not available".to_string())?;
    let output = Command::new(lsof)
        .args(["-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", &pid.to_string()])
        .output()
        .map_err(|e| format!("run lsof: {e}"))?;
    let stdout = String::from_utf8_lossy(&output.stdout);
    let ports: BTreeSet<u16> = stdout.lines().filter_map(parse_listen_port).collect();
    if ports.is_empty() {
        Err("no listening ports for Antigravity".to_string())
    } else {
        Ok(ports.into_iter().collect())
    }
}

/// Pull the port out of an `lsof` LISTEN line, e.g. `... TCP 127.0.0.1:54321 (LISTEN)`.
fn parse_listen_port(line: &str) -> Option<u16> {
    let idx = line.find("(LISTEN)")?;
    let before = line[..idx].trim_end();
    let colon = before.rfind(':')?;
    before[colon + 1..].trim().parse().ok()
}

#[cfg(any(windows, test))]
const WINDOWS_DISCOVERY_PREFIX: &str = "ANTIGRAVITY_V1";

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[cfg(any(windows, test))]
const WINDOWS_DISCOVERY_SCRIPT: &str = r#"
$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name LIKE 'language_server%.exe'")
    if ($processes.Count -eq 0) {
        [Console]::Out.WriteLine("ANTIGRAVITY_V1`tN")
        exit 0
    }

    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop)
    foreach ($process in $processes) {
        $commandLine = [string]$process.CommandLine
        $encoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($commandLine))
        $ports = @(
            $listeners |
                Where-Object { $_.OwningProcess -eq [uint32]$process.ProcessId } |
                ForEach-Object { [uint16]$_.LocalPort } |
                Sort-Object -Unique
        )
        [Console]::Out.WriteLine(
            "ANTIGRAVITY_V1`tP`t$($process.ProcessId)`t$encoded`t$($ports -join ',')"
        )
    }
} catch {
    exit 1
}
exit 0
"#;

#[cfg(any(windows, test))]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum WindowsDiscoveryError {
    ProcessNotFound,
    TokenMissing,
    PortsMissing,
    PowerShellFailed,
    MalformedOutput,
}

#[cfg(any(windows, test))]
impl WindowsDiscoveryError {
    fn message(self) -> &'static str {
        match self {
            Self::ProcessNotFound => "Antigravity is not running",
            Self::TokenMissing => "Antigravity is running but no CSRF token was found",
            Self::PortsMissing => "no listening ports for Antigravity",
            Self::PowerShellFailed => "Antigravity PowerShell discovery failed",
            Self::MalformedOutput => "malformed Antigravity PowerShell discovery output",
        }
    }
}

#[cfg(windows)]
fn discover_local_ide() -> Result<Vec<(ProcInfo, Vec<u16>)>, String> {
    let system_root = std::env::var_os("SystemRoot").ok_or_else(|| {
        WindowsDiscoveryError::PowerShellFailed
            .message()
            .to_string()
    })?;
    let powershell = PathBuf::from(system_root)
        .join("System32")
        .join("WindowsPowerShell")
        .join("v1.0")
        .join("powershell.exe");
    let output = Command::new(powershell)
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            WINDOWS_DISCOVERY_SCRIPT,
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|_| {
            WindowsDiscoveryError::PowerShellFailed
                .message()
                .to_string()
        })?;
    if !output.status.success() {
        return Err(WindowsDiscoveryError::PowerShellFailed
            .message()
            .to_string());
    }
    let stdout = std::str::from_utf8(&output.stdout)
        .map_err(|_| WindowsDiscoveryError::MalformedOutput.message().to_string())?;
    parse_windows_discovery(stdout).map_err(|error| error.message().to_string())
}

#[cfg(any(windows, test))]
fn parse_windows_discovery(
    stdout: &str,
) -> Result<Vec<(ProcInfo, Vec<u16>)>, WindowsDiscoveryError> {
    let mut processes = Vec::new();
    let mut saw_valid_record = false;
    let mut saw_antigravity = false;
    let mut saw_csrf = false;

    for line in stdout.lines() {
        let fields: Vec<&str> = line.trim_end_matches('\r').split('\t').collect();
        match fields.as_slice() {
            [prefix, "N"] if *prefix == WINDOWS_DISCOVERY_PREFIX => {
                saw_valid_record = true;
            }
            [prefix, "P", pid, encoded_cmd, port_field] if *prefix == WINDOWS_DISCOVERY_PREFIX => {
                let Ok(pid) = pid.parse::<i32>() else {
                    continue;
                };
                let Some(ports) = parse_windows_ports(port_field) else {
                    continue;
                };
                let Ok(command_bytes) =
                    base64::engine::general_purpose::STANDARD.decode(encoded_cmd)
                else {
                    continue;
                };
                let Ok(cmd) = String::from_utf8(command_bytes) else {
                    continue;
                };
                if cmd.is_empty() {
                    continue;
                }
                saw_valid_record = true;
                let lower = cmd.to_lowercase();
                if !is_language_server(&lower) || !is_antigravity(&lower) {
                    continue;
                }
                saw_antigravity = true;
                let Some(csrf) = extract_flag(&cmd, "--csrf_token") else {
                    continue;
                };
                saw_csrf = true;
                if ports.is_empty() {
                    continue;
                }
                processes.push((
                    ProcInfo {
                        pid,
                        csrf_token: csrf,
                        extension_port: extract_flag(&cmd, "--extension_server_port")
                            .and_then(|value| value.parse().ok()),
                        extension_csrf: extract_flag(&cmd, "--extension_server_csrf_token"),
                    },
                    ports,
                ));
            }
            _ => {}
        }
    }

    if !processes.is_empty() {
        Ok(processes)
    } else if saw_csrf {
        Err(WindowsDiscoveryError::PortsMissing)
    } else if saw_antigravity {
        Err(WindowsDiscoveryError::TokenMissing)
    } else if saw_valid_record {
        Err(WindowsDiscoveryError::ProcessNotFound)
    } else {
        Err(WindowsDiscoveryError::MalformedOutput)
    }
}

#[cfg(any(windows, test))]
fn parse_windows_ports(field: &str) -> Option<Vec<u16>> {
    if field.is_empty() {
        return Some(Vec::new());
    }
    let mut ports = BTreeSet::new();
    for value in field.split(',') {
        let port = value.parse::<u16>().ok()?;
        if port == 0 {
            return None;
        }
        ports.insert(port);
    }
    Some(ports.into_iter().collect())
}

#[derive(Debug, Deserialize)]
struct UserStatusResponse {
    #[serde(rename = "userStatus")]
    user_status: Option<UserStatus>,
}

#[derive(Debug, Deserialize)]
struct UserStatus {
    email: Option<String>,
    #[serde(rename = "planStatus")]
    plan_status: Option<PlanStatus>,
    #[serde(rename = "cascadeModelConfigData")]
    cascade_model_config_data: Option<ModelConfigData>,
    #[serde(rename = "userTier")]
    user_tier: Option<NamedTier>,
}

#[derive(Debug, Deserialize)]
struct PlanStatus {
    #[serde(rename = "planInfo")]
    plan_info: Option<LocalPlanInfo>,
}

#[derive(Debug, Deserialize)]
struct LocalPlanInfo {
    #[serde(rename = "planDisplayName")]
    plan_display_name: Option<String>,
    #[serde(rename = "displayName")]
    display_name: Option<String>,
    #[serde(rename = "productName")]
    product_name: Option<String>,
    #[serde(rename = "planName")]
    plan_name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct NamedTier {
    name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ModelConfigData {
    #[serde(rename = "clientModelConfigs")]
    client_model_configs: Option<Vec<Box<RawValue>>>,
}

#[derive(Debug, Deserialize)]
struct AvailableModelsResponse {
    #[serde(default)]
    models: BTreeMap<String, Box<RawValue>>,
}

#[derive(Debug, Deserialize)]
struct QuotaBucketsResponse {
    #[serde(default)]
    buckets: Vec<Box<RawValue>>,
}

#[cfg(any(windows, test))]
#[derive(Debug, Deserialize)]
struct AgyUsageResponse {
    status: Option<String>,
    command: Option<AgyUsageCommand>,
}

#[cfg(any(windows, test))]
#[derive(Debug, Deserialize)]
struct AgyUsageCommand {
    name: Option<String>,
    data: Option<AgyUsageData>,
}

#[cfg(any(windows, test))]
#[derive(Debug, Deserialize)]
struct AgyUsageData {
    #[serde(default)]
    groups: Vec<AgyUsageGroup>,
}

#[cfg(any(windows, test))]
#[derive(Debug, Deserialize)]
struct AgyUsageGroup {
    name: Option<String>,
    #[serde(default)]
    buckets: Vec<AgyUsageBucket>,
}

#[cfg(any(windows, test))]
#[derive(Debug, Deserialize)]
struct AgyUsageBucket {
    id: Option<String>,
    name: Option<String>,
    #[serde(rename = "remaining_fraction")]
    remaining_fraction: Option<f64>,
    #[serde(rename = "reset_time")]
    reset_time: Option<String>,
}

#[derive(Debug)]
struct ModelCandidate {
    model_id: Option<String>,
    fraction: f64,
    reset: Option<DateTime<Utc>>,
    source_index: usize,
    label: String,
}

fn valid_remaining_fraction(fraction: f64) -> bool {
    fraction.is_finite() && (0.0..=1.0).contains(&fraction)
}

fn quota_window(
    label: String,
    fraction: f64,
    reset: Option<DateTime<Utc>>,
    now: DateTime<Utc>,
    card_id: String,
    window_key: Option<String>,
) -> Option<UsageWindow> {
    UsageWindow::try_from_provider_fraction(label, fraction, reset, now)
        .map(|window| window.with_identity(card_id, window_key, None, None))
}

#[cfg(any(windows, test))]
fn parse_agy_usage(body: &[u8], now: DateTime<Utc>) -> Result<Fetched, String> {
    let response: AgyUsageResponse =
        serde_json::from_slice(body).map_err(|e| format!("decode agy usage: {e}"))?;
    if response.status.as_deref() != Some("SUCCESS") {
        return Err("agy usage command was not successful".to_string());
    }
    let command = response
        .command
        .ok_or_else(|| "agy usage response missing command".to_string())?;
    if command.name.as_deref() != Some("usage") {
        return Err("agy usage response missing usage command".to_string());
    }
    let data = command
        .data
        .ok_or_else(|| "agy usage response missing data".to_string())?;

    let mut windows = Vec::new();
    for group in data.groups {
        let group_name = group
            .name
            .as_deref()
            .map(str::trim)
            .filter(|name| !name.is_empty())
            .unwrap_or("Antigravity");
        for bucket in group.buckets {
            let Some(id) = bucket
                .id
                .as_deref()
                .map(str::trim)
                .filter(|id| !id.is_empty())
            else {
                continue;
            };
            let Some(fraction) = bucket.remaining_fraction else {
                continue;
            };
            let reset = bucket.reset_time.as_deref().and_then(parse_datetime);
            let label = bucket
                .name
                .as_deref()
                .map(str::trim)
                .filter(|name| !name.is_empty())
                .map(|name| format!("{group_name} · {name}"))
                .unwrap_or_else(|| format!("{group_name} · Limit"));
            let card_id = format!("agy.{id}.v1");
            if let Some(window) =
                quota_window(label, fraction, reset, now, card_id.clone(), Some(card_id))
            {
                windows.push(window);
            }
        }
    }
    if windows.is_empty() {
        return Err("agy usage response contained no valid quota windows".to_string());
    }

    Ok(Fetched {
        source: "agy".to_string(),
        identity: None,
        account_scope: Err(AccountScopeError::NoTrustedEvidence),
        history_scope: Err(AccountScopeError::NoTrustedEvidence),
        cache_binding: None,
        windows,
    })
}

fn parse_user_status(body: &str, now: DateTime<Utc>) -> Result<Fetched, String> {
    let response: UserStatusResponse =
        serde_json::from_str(body).map_err(|e| format!("decode GetUserStatus: {e}"))?;
    let status = response
        .user_status
        .ok_or_else(|| "GetUserStatus missing userStatus".to_string())?;

    let configs = status
        .cascade_model_config_data
        .and_then(|d| d.client_model_configs)
        .unwrap_or_default();
    let mut selected: BTreeMap<String, ModelCandidate> = BTreeMap::new();
    let mut missing_model = Vec::new();
    for (index, config) in configs.into_iter().enumerate() {
        let Ok(config) = serde_json::from_str::<Value>(config.get()) else {
            continue;
        };
        let Some(quota) = config.get("quotaInfo") else {
            continue;
        };
        let Some(fraction) = quota.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset = quota
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        let model_id = config
            .pointer("/modelOrAlias/model")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|model| !model.is_empty())
            .map(str::to_string);
        let label = config
            .get("label")
            .and_then(Value::as_str)
            .filter(|s| !s.trim().is_empty())
            .map(str::to_string)
            .or_else(|| model_id.clone())
            .unwrap_or_else(|| "Model".to_string());
        let candidate = ModelCandidate {
            model_id: model_id.clone(),
            fraction,
            reset,
            source_index: index,
            label,
        };
        let Some(model_id) = model_id else {
            missing_model.push(candidate);
            continue;
        };
        match selected.get(&model_id) {
            Some(current)
                if !binding_candidate_is_better(
                    candidate.fraction,
                    candidate.reset,
                    candidate.source_index,
                    current.fraction,
                    current.reset,
                    current.source_index,
                    now,
                ) => {}
            _ => {
                selected.insert(model_id, candidate);
            }
        }
    }
    let mut candidates: Vec<ModelCandidate> = selected.into_values().collect();
    candidates.extend(missing_model);
    candidates.sort_by_key(|candidate| candidate.source_index);
    let windows: Vec<UsageWindow> = candidates
        .into_iter()
        .filter_map(|candidate| {
            let (card_id, window_key) = match candidate.model_id {
                Some(model_id) => {
                    let key = format!("model.{model_id}.v1");
                    (key.clone(), Some(key))
                }
                None => (
                    format!("row.cli.config.{}.v1", candidate.source_index),
                    None,
                ),
            };
            quota_window(
                candidate.label,
                candidate.fraction,
                candidate.reset,
                now,
                card_id,
                window_key,
            )
        })
        .collect();

    let plan = status
        .user_tier
        .and_then(|t| t.name)
        .filter(|s| !s.trim().is_empty())
        .or_else(|| {
            status
                .plan_status
                .and_then(|p| p.plan_info)
                .and_then(local_plan_name)
        });

    let email = status.email.filter(|value| !value.trim().is_empty());
    Ok(Fetched {
        source: "cli".to_string(),
        identity: Some(AgentIdentity { email, plan }),
        // Parsing remains pure and hermetic. fetch_local_ide resolves this only
        // after the authenticated loopback response has been accepted.
        account_scope: Err(AccountScopeError::NoTrustedEvidence),
        history_scope: Err(AccountScopeError::NoTrustedEvidence),
        cache_binding: None,
        windows,
    })
}

fn resolve_local_account_scope(
    identity: Option<&AgentIdentity>,
) -> Result<AccountScope, AccountScopeError> {
    let email = identity
        .and_then(|identity| identity.email.as_deref())
        .ok_or(AccountScopeError::NoTrustedEvidence)?;
    agent_account_scope::resolve_authoritative("antigravity", AuthoritativeIdKind::Email, email)
}

/// The local IDE route is one of the two routes with an authoritative owner ID
/// today: `GetUserStatus` returns the authenticated account's email. The history
/// scope must consume it, so two accounts keep two series — and so the series
/// this route recorded before `HistoryScope` keep their exact keys.
fn resolve_local_history_scope(
    identity: Option<&AgentIdentity>,
) -> Result<HistoryScope, AccountScopeError> {
    resolve_local_history_scope_with(identity, agent_account_scope::resolve_history_scope)
}

fn resolve_local_history_scope_with<R>(
    identity: Option<&AgentIdentity>,
    resolve: R,
) -> Result<HistoryScope, AccountScopeError>
where
    R: FnOnce(&str, Option<(AuthoritativeIdKind, &str)>) -> Result<HistoryScope, AccountScopeError>,
{
    resolve(
        "antigravity",
        identity
            .and_then(|identity| identity.email.as_deref())
            .map(str::trim)
            .filter(|email| !email.is_empty())
            .map(|email| (AuthoritativeIdKind::Email, email)),
    )
}

fn local_plan_name(info: LocalPlanInfo) -> Option<String> {
    [
        info.plan_display_name,
        info.display_name,
        info.product_name,
        info.plan_name,
    ]
    .into_iter()
    .flatten()
    .map(|s| s.trim().to_string())
    .find(|s| !s.is_empty())
}

// ── OAuth remote (Google Code Assist) ─────────────────────────────────────────

struct RemoteContext {
    client: reqwest::Client,
    access_token: String,
    project: Option<String>,
    plan: Option<String>,
    account_scope: AccountScope,
    cache_binding: Option<ProviderCacheBinding>,
}

impl RemoteContext {
    fn finish(self, windows: Vec<UsageWindow>) -> Fetched {
        // Remote OAuth carries no authoritative owner ID today: the stored
        // Google `id_token` is not read yet (macOS HISTID-B, not ported).
        let history_scope = agent_account_scope::resolve_history_scope("antigravity", None);
        self.finish_with_history(windows, history_scope)
    }

    /// `finish` with the caller's history scope: a captured account keys its
    /// history on its own key, not the per-installation constant.
    fn finish_with_history(
        self,
        windows: Vec<UsageWindow>,
        history_scope: Result<HistoryScope, AccountScopeError>,
    ) -> Fetched {
        Fetched {
            source: "oauth".to_string(),
            // google_accounts.active is unrelated local state, not authenticated
            // by the credential that fetched these quotas.
            identity: Some(remote_identity(self.plan)),
            account_scope: Ok(self.account_scope),
            history_scope,
            cache_binding: self.cache_binding,
            windows,
        }
    }
}

enum PrimaryQuotaAttempt {
    Success(Vec<UsageWindow>),
    Forbidden,
    SchemaContradiction(ProviderFetchFailure),
    Transient,
    Terminal(ProviderFetchFailure),
}

async fn fetch_oauth_primary(now: DateTime<Utc>) -> PrimaryAttempt<RemoteContext> {
    let context = match prepare_remote_context(now).await {
        Ok(context) => context,
        Err(failure) => return PrimaryAttempt::FinalFailure(failure),
    };
    match fetch_available_models(&context, now).await {
        PrimaryQuotaAttempt::Success(windows) => PrimaryAttempt::Success(context.finish(windows)),
        PrimaryQuotaAttempt::Forbidden => PrimaryAttempt::Forbidden(context),
        PrimaryQuotaAttempt::SchemaContradiction(failure) => {
            PrimaryAttempt::SchemaContradiction { context, failure }
        }
        PrimaryQuotaAttempt::Transient => PrimaryAttempt::Transient(context),
        PrimaryQuotaAttempt::Terminal(failure) => PrimaryAttempt::FinalFailure(failure),
    }
}

async fn fetch_oauth_secondary(
    context: RemoteContext,
    now: DateTime<Utc>,
) -> Result<Fetched, ProviderFetchFailure> {
    let windows = fetch_user_quota(&context, now).await?;
    Ok(context.finish(windows))
}

async fn prepare_remote_context(now: DateTime<Utc>) -> Result<RemoteContext, ProviderFetchFailure> {
    let creds_path = gemini_home()
        .map(|home| home.join("oauth_creds.json"))
        .ok_or_else(|| {
            ProviderFetchFailure::terminal("Antigravity credential location could not be resolved.")
        })?;
    let creds = remote_credentials_or_unconfigured(&creds_path)?;
    let verified = if remote_credentials_need_refresh(&creds, now) {
        refresh_access_token(&creds_path, now).await.map(
            |(_, access_token, account_scope, cache_binding)| {
                (access_token, account_scope, cache_binding)
            },
        )
    } else {
        remote_access_token(&creds)
            .map_err(|_| {
                ProviderFetchFailure::terminal("Antigravity credentials have no access token.")
            })
            .and_then(|access_token| {
                resolve_remote_account_scope(&creds_path, &creds)
                    .map(|account_scope| {
                        let cache_binding = ProviderCacheBinding::primary(account_scope.clone());
                        (access_token, account_scope, Some(cache_binding))
                    })
                    .map_err(|_| {
                        ProviderFetchFailure::terminal(
                            "Antigravity account identity could not be verified.",
                        )
                    })
            })
    };

    request_after_verified_binding(
        verified,
        |(access_token, account_scope, cache_binding)| async move {
            let client = provider_http_client_builder()
                .timeout(std::time::Duration::from_secs(30))
                .build()
                .map_err(|_| {
                    ProviderFetchFailure::terminal(
                        "Antigravity usage client could not be created.",
                    )
                })?;
            let code_assist_body = code_assist_post(
                &client,
                "loadCodeAssist",
                &json!({
                    "metadata": { "ideType": "ANTIGRAVITY", "platform": "PLATFORM_UNSPECIFIED", "pluginType": "GEMINI" }
                }),
                &access_token,
                cache_binding.clone(),
                false,
            )
            .await
            .map_err(|failure| match failure {
                CodeAssistPostFailure::Forbidden => ProviderFetchFailure::terminal(
                    "Antigravity loadCodeAssist permission was denied.",
                ),
                CodeAssistPostFailure::Failure(failure) => failure,
            })?;
            let code_assist: Value = serde_json::from_str(&code_assist_body).map_err(|_| {
                ProviderFetchFailure::terminal(
                    "Antigravity loadCodeAssist response could not be decoded.",
                )
            })?;

            Ok(RemoteContext {
                client,
                access_token,
                project: project_id(&code_assist),
                plan: resolve_remote_plan(&code_assist),
                account_scope,
                cache_binding,
            })
        },
    )
    .await
}

fn remote_identity(plan: Option<String>) -> AgentIdentity {
    AgentIdentity { email: None, plan }
}

/// Why the shared Google credential could not be loaded. Only a genuinely
/// absent file means "nothing is configured"; a file that exists but can't be
/// read or parsed belongs to a configured account with a broken credential.
#[derive(Debug, PartialEq, Eq)]
enum RemoteCredentialError {
    Absent,
    Unreadable,
}

/// The credential step of `prepare_remote_context`, split out so the pairing
/// of "no credential at all" with `ANTIGRAVITY_UNCONFIGURED_ERROR` is
/// reachable from a test without a Gemini home, a running IDE or the network.
fn remote_credentials_or_unconfigured(path: &Path) -> Result<Value, ProviderFetchFailure> {
    load_remote_credentials(path).map_err(|error| match error {
        RemoteCredentialError::Absent => {
            ProviderFetchFailure::terminal(ANTIGRAVITY_UNCONFIGURED_ERROR)
        }
        RemoteCredentialError::Unreadable => {
            ProviderFetchFailure::terminal(ANTIGRAVITY_UNREADABLE_ERROR)
        }
    })
}

fn load_remote_credentials(path: &Path) -> Result<Value, RemoteCredentialError> {
    let raw = std::fs::read_to_string(path).map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            RemoteCredentialError::Absent
        } else {
            RemoteCredentialError::Unreadable
        }
    })?;
    serde_json::from_str(&raw).map_err(|_| RemoteCredentialError::Unreadable)
}

fn remote_access_token(creds: &Value) -> Result<String, String> {
    creds
        .get("access_token")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .map(str::to_string)
        .ok_or_else(|| "Antigravity creds have no access token".to_string())
}

fn remote_refresh_marker(creds: &Value) -> Option<&[u8]> {
    creds
        .get("refresh_token")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .map(str::as_bytes)
}

fn remote_credentials_need_refresh(creds: &Value, now: DateTime<Utc>) -> bool {
    let expiry_ms = creds.get("expiry_date").and_then(Value::as_f64);
    let now_ms = now.timestamp_millis() as f64;
    expiry_ms.is_none_or(|expiry| expiry <= now_ms + (REFRESH_SAFETY_SECS * 1000) as f64)
}

fn remote_scope_location(path: &Path) -> Result<String, AccountScopeError> {
    agent_account_scope::canonical_file_location(path, Some("refresh_token"))
}

fn resolve_remote_account_scope(
    path: &Path,
    creds: &Value,
) -> Result<AccountScope, AccountScopeError> {
    let marker = remote_refresh_marker(creds).ok_or(AccountScopeError::NoTrustedEvidence)?;
    agent_account_scope::resolve_credential(
        "antigravity",
        "google-oauth-creds",
        &remote_scope_location(path)?,
        marker,
    )
}

async fn refresh_access_token(
    creds_path: &Path,
    now: DateTime<Utc>,
) -> Result<(Value, String, AccountScope, Option<ProviderCacheBinding>), ProviderFetchFailure> {
    let refresh = agent_account_scope::begin_refresh("antigravity").map_err(|_| {
        ProviderFetchFailure::terminal("Antigravity credential refresh lock is unavailable.")
    })?;
    refresh_access_token_with(
        creds_path,
        now,
        &refresh,
        request_access_token,
        |creds| write_creds_atomic(creds_path, creds),
        |_| Ok(()),
    )
    .await
}

async fn request_access_token(
    refresh_token: String,
    attempt_binding: ProviderCacheBinding,
) -> Result<Value, ProviderFetchFailure> {
    // Off the async poll: the first call reads and scans the installed IDE's
    // binary — 130 MB for the Windows `language_server.exe`; the manual check
    // that read it twice and scanned it three times took 11 s on the x64
    // host — and `agent_usage::run` polls every provider in one
    // `tokio::join!`, so a synchronous scan here would hold every provider's
    // result, not just this one. Later calls hit the `OnceLock` and return
    // at once.
    let client = tokio::task::spawn_blocking(resolve_oauth_client)
        .await
        .ok()
        .flatten()
        .ok_or_else(|| {
        ProviderFetchFailure::terminal(
            "Antigravity OAuth client was not found. Install Antigravity.app or configure its OAuth client.",
        )
    })?;
    let http = provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|_| {
            ProviderFetchFailure::terminal("Antigravity refresh client could not be created.")
        })?;
    let form = format!(
        "client_id={}&client_secret={}&refresh_token={}&grant_type=refresh_token",
        percent_encode(&client.0),
        percent_encode(&client.1),
        percent_encode(&refresh_token),
    );
    let response = http
        .post(GOOGLE_TOKEN_URL)
        .header(
            reqwest::header::CONTENT_TYPE,
            "application/x-www-form-urlencoded",
        )
        .body(form)
        .send()
        .await
        .map_err(|error| {
            ProviderFetchFailure::from_send_error(
                "Antigravity token refresh failed. Retrying automatically.",
                Some(attempt_binding.clone()),
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
    .map_err(|failure| match failure {
        ResponseReadFailure::Transient(diagnostic) => ProviderFetchFailure::transient(
            "Antigravity token refresh failed. Retrying automatically.",
            Some(attempt_binding),
            diagnostic,
        ),
        ResponseReadFailure::Terminal(_) => ProviderFetchFailure::terminal(
            "Antigravity token refresh was rejected. Re-login in Antigravity.",
        ),
    })?;
    serde_json::from_str(&body).map_err(|_| {
        ProviderFetchFailure::terminal("Antigravity token refresh response could not be decoded.")
    })
}

async fn refresh_access_token_with<R, Request, RequestFuture, Save, Checkpoint>(
    creds_path: &Path,
    now: DateTime<Utc>,
    refresh: &R,
    request: Request,
    save: Save,
    mut checkpoint: Checkpoint,
) -> Result<(Value, String, AccountScope, Option<ProviderCacheBinding>), ProviderFetchFailure>
where
    R: RefreshScopeTransaction + ?Sized,
    Request: FnOnce(String, ProviderCacheBinding) -> RequestFuture,
    RequestFuture: std::future::Future<Output = Result<Value, ProviderFetchFailure>>,
    Save: FnOnce(&Value) -> std::io::Result<()>,
    Checkpoint: FnMut(RefreshCheckpoint) -> Result<(), ProviderFetchFailure>,
{
    let creds = load_remote_credentials(creds_path).map_err(|_| {
        ProviderFetchFailure::terminal("Antigravity credentials could not be reloaded.")
    })?;
    checkpoint(RefreshCheckpoint::Reloaded)?;
    let location = remote_scope_location(creds_path).map_err(|_| {
        ProviderFetchFailure::terminal("Antigravity auth location could not be verified.")
    })?;
    let old_marker = remote_refresh_marker(&creds)
        .ok_or_else(|| {
            ProviderFetchFailure::terminal("Antigravity credential has no trusted refresh marker.")
        })?
        .to_vec();
    let pre_scope = refresh
        .resolve_current("google-oauth-creds", &location, &old_marker)
        .map_err(|_| {
            ProviderFetchFailure::terminal("Antigravity account identity could not be verified.")
        })?;
    let pre_binding = ProviderCacheBinding::primary(pre_scope.clone());
    if !remote_credentials_need_refresh(&creds, now) {
        let access_token = remote_access_token(&creds).map_err(|_| {
            ProviderFetchFailure::terminal("Antigravity credentials have no access token.")
        })?;
        return Ok((creds, access_token, pre_scope, Some(pre_binding)));
    }

    let refresh_token = std::str::from_utf8(&old_marker)
        .map_err(|_| ProviderFetchFailure::terminal("Antigravity refresh credential is invalid."))?
        .to_string();
    let json = request(refresh_token, pre_binding).await?;
    checkpoint(RefreshCheckpoint::NetworkReturned)?;
    let access_token = json
        .get("access_token")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .ok_or_else(|| {
            ProviderFetchFailure::terminal(
                "Antigravity token refresh response had no access token.",
            )
        })?
        .to_string();

    // The provider refresh lock serializes TokenBar writers, but the credential
    // file has no cross-process compare-and-swap. Re-reading closes the network
    // wait race; an external writer can still race this check and atomic rename.
    let mut current_creds = load_remote_credentials(creds_path).map_err(|_| {
        ProviderFetchFailure::terminal(
            "Antigravity credentials changed during refresh; refusing stale write-back.",
        )
    })?;
    let current_marker = remote_refresh_marker(&current_creds).ok_or_else(|| {
        ProviderFetchFailure::terminal(
            "Antigravity credentials changed during refresh; refusing stale write-back.",
        )
    })?;
    if current_marker != old_marker.as_slice() {
        return Err(ProviderFetchFailure::terminal(
            "Antigravity credentials changed during refresh; refusing stale write-back.",
        ));
    }

    let obj = current_creds.as_object_mut().ok_or_else(|| {
        ProviderFetchFailure::terminal(
            "Antigravity credentials changed during refresh; refusing stale write-back.",
        )
    })?;
    obj.insert("access_token".into(), Value::String(access_token.clone()));
    if let Some(expires_in) = json.get("expires_in").and_then(Value::as_f64) {
        let expiry = now.timestamp_millis() as f64 + expires_in * 1000.0;
        obj.insert("expiry_date".into(), json!(expiry));
    }
    if let Some(id_token) = json.get("id_token").and_then(Value::as_str) {
        obj.insert("id_token".into(), Value::String(id_token.to_string()));
    }
    if let Some(replacement) = json
        .get("refresh_token")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
    {
        obj.insert(
            "refresh_token".into(),
            Value::String(replacement.to_string()),
        );
    }
    let new_marker = remote_refresh_marker(&current_creds)
        .ok_or_else(|| {
            ProviderFetchFailure::terminal(
                "Antigravity refreshed credential has no trusted marker.",
            )
        })?
        .to_vec();
    let account_scope = refresh
        .transfer("google-oauth-creds", &location, &old_marker, &new_marker)
        .map_err(|_| {
            ProviderFetchFailure::terminal("Antigravity credential lineage could not be preserved.")
        })?;
    checkpoint(RefreshCheckpoint::MetadataHandled)?;
    let persisted = save(&current_creds).is_ok();
    checkpoint(RefreshCheckpoint::CredentialsPersisted)?;
    let cache_binding = if persisted {
        Some(ProviderCacheBinding::primary(
            refresh
                .resolve_current("google-oauth-creds", &location, &new_marker)
                .map_err(|_| {
                    ProviderFetchFailure::terminal(
                        "Antigravity account identity could not be verified after refresh.",
                    )
                })?,
        ))
    } else {
        None
    };
    Ok((current_creds, access_token, account_scope, cache_binding))
}

fn write_creds_atomic(path: &Path, creds: &Value) -> std::io::Result<()> {
    use std::io::Write as _;
    use std::sync::atomic::{AtomicU64, Ordering};

    let data = serde_json::to_vec_pretty(creds).map_err(std::io::Error::other)?;
    let directory = path.parent().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "credential path has no parent",
        )
    })?;
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let tmp = directory.join(format!(
        ".oauth_creds.json.tokenbar.{}.{}",
        std::process::id(),
        COUNTER.fetch_add(1, Ordering::Relaxed)
    ));
    let staged = (|| {
        let mut options = std::fs::OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt as _;
            options.mode(0o600);
        }
        let mut file = options.open(&tmp)?;
        file.write_all(&data)?;
        file.sync_all()
    })();
    if let Err(error) = staged {
        let _ = std::fs::remove_file(&tmp);
        return Err(error);
    }
    if let Err(error) = tokscale_core::fs_atomic::replace_file(&tmp, path) {
        let _ = std::fs::remove_file(&tmp);
        return Err(error);
    }
    #[cfg(unix)]
    {
        let _ = std::fs::File::open(directory).and_then(|dir| dir.sync_all());
    }
    Ok(())
}

enum CodeAssistPostFailure {
    Forbidden,
    Failure(ProviderFetchFailure),
}

async fn code_assist_post(
    client: &reqwest::Client,
    method: &'static str,
    body: &Value,
    access_token: &str,
    attempt_binding: Option<ProviderCacheBinding>,
    forbidden_is_route_miss: bool,
) -> Result<String, CodeAssistPostFailure> {
    let response = client
        .post(format!("{CODE_ASSIST_BASE}:{method}"))
        .bearer_auth(access_token)
        .header(reqwest::header::USER_AGENT, "antigravity")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .json(body)
        .send()
        .await
        .map_err(|error| {
            CodeAssistPostFailure::Failure(ProviderFetchFailure::from_send_error(
                format!("Antigravity {method} request failed. Retrying automatically."),
                attempt_binding.clone(),
                &error,
            ))
        })?;
    let status = response.status().as_u16();
    if status == 403 && forbidden_is_route_miss {
        return Err(CodeAssistPostFailure::Forbidden);
    }
    read_response_body(status, false, || async {
        response.text().await.map_err(|error| {
            TransportErrorFacts::from_reqwest(&error, TransportPhase::ResponseBody)
        })
    })
    .await
    .map_err(|failure| {
        CodeAssistPostFailure::Failure(match failure {
            ResponseReadFailure::Transient(diagnostic) => ProviderFetchFailure::transient(
                format!("Antigravity {method} request failed. Retrying automatically."),
                attempt_binding,
                diagnostic,
            ),
            ResponseReadFailure::Terminal(401) => {
                ProviderFetchFailure::terminal(ANTIGRAVITY_AUTH_EXPIRED)
            }
            ResponseReadFailure::Terminal(403) => ProviderFetchFailure::terminal(format!(
                "Antigravity {method} permission was denied."
            )),
            ResponseReadFailure::Terminal(status) => ProviderFetchFailure::terminal(format!(
                "Antigravity {method} rejected the request (status {status})."
            )),
        })
    })
}

async fn fetch_available_models(
    context: &RemoteContext,
    now: DateTime<Utc>,
) -> PrimaryQuotaAttempt {
    let body = match context.project.as_deref() {
        Some(project) => json!({ "project": project }),
        None => json!({}),
    };
    let response = match code_assist_post(
        &context.client,
        "fetchAvailableModels",
        &body,
        &context.access_token,
        context.cache_binding.clone(),
        true,
    )
    .await
    {
        Ok(response) => response,
        Err(CodeAssistPostFailure::Forbidden) => return PrimaryQuotaAttempt::Forbidden,
        Err(CodeAssistPostFailure::Failure(failure @ ProviderFetchFailure::Transient { .. })) => {
            let _ = failure;
            return PrimaryQuotaAttempt::Transient;
        }
        Err(CodeAssistPostFailure::Failure(failure)) => {
            return PrimaryQuotaAttempt::Terminal(failure);
        }
    };
    match models_from_available(&response, now) {
        Ok(windows) if !windows.is_empty() => PrimaryQuotaAttempt::Success(windows),
        Ok(_) | Err(_) => PrimaryQuotaAttempt::SchemaContradiction(ProviderFetchFailure::terminal(
            "Antigravity fetchAvailableModels returned no usable quota windows.",
        )),
    }
}

async fn fetch_user_quota(
    context: &RemoteContext,
    now: DateTime<Utc>,
) -> Result<Vec<UsageWindow>, ProviderFetchFailure> {
    let body = match context.project.as_deref() {
        Some(project) => json!({ "project": project }),
        None => json!({}),
    };
    let response = code_assist_post(
        &context.client,
        "retrieveUserQuota",
        &body,
        &context.access_token,
        context.cache_binding.clone(),
        false,
    )
    .await
    .map_err(|failure| match failure {
        CodeAssistPostFailure::Forbidden => {
            ProviderFetchFailure::terminal("Antigravity retrieveUserQuota permission was denied.")
        }
        CodeAssistPostFailure::Failure(failure) => failure,
    })?;
    let windows = buckets_from_quota(&response, now).map_err(|_| {
        ProviderFetchFailure::terminal(
            "Antigravity retrieveUserQuota response could not be decoded.",
        )
    })?;
    if windows.is_empty() {
        return Err(ProviderFetchFailure::terminal(
            "Antigravity retrieveUserQuota returned no usable quota windows.",
        ));
    }
    Ok(windows)
}

fn project_id(code_assist: &Value) -> Option<String> {
    match code_assist.get("cloudaicompanionProject") {
        Some(Value::String(s)) if !s.trim().is_empty() => Some(s.trim().to_string()),
        Some(Value::Object(obj)) => obj
            .get("value")
            .or_else(|| obj.get("id"))
            .and_then(Value::as_str)
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty()),
        _ => None,
    }
}

fn resolve_remote_plan(code_assist: &Value) -> Option<String> {
    if let Some(plan_type) = code_assist
        .pointer("/planInfo/planType")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
    {
        return Some(clean_plan(plan_type));
    }
    match code_assist
        .pointer("/currentTier/id")
        .and_then(Value::as_str)
        .map(str::trim)
    {
        Some("standard-tier") => Some("Paid".to_string()),
        Some("free-tier") => Some("Free".to_string()),
        Some("legacy-tier") => Some("Legacy".to_string()),
        _ => code_assist
            .pointer("/currentTier/name")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string),
    }
}

fn models_from_available(body: &str, now: DateTime<Utc>) -> Result<Vec<UsageWindow>, String> {
    let response: AvailableModelsResponse = serde_json::from_str(body)
        .map_err(|e| format!("decode Antigravity fetchAvailableModels: {e}"))?;
    let mut selected: BTreeMap<String, ModelCandidate> = BTreeMap::new();
    let mut missing_model = Vec::new();
    for (source_index, (raw_id, model)) in response.models.into_iter().enumerate() {
        let Ok(model) = serde_json::from_str::<Value>(model.get()) else {
            continue;
        };
        let Some(quota) = model.get("quotaInfo") else {
            continue;
        };
        let Some(fraction) = quota.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset = quota
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        let model_id = raw_id.trim().to_string();
        let model_id = (!model_id.is_empty()).then_some(model_id);
        let label = model
            .get("displayName")
            .and_then(Value::as_str)
            .filter(|s| !s.trim().is_empty())
            .or_else(|| {
                model
                    .get("label")
                    .and_then(Value::as_str)
                    .filter(|s| !s.trim().is_empty())
            })
            .unwrap_or(raw_id.as_str())
            .to_string();
        let candidate = ModelCandidate {
            model_id: model_id.clone(),
            fraction,
            reset,
            source_index,
            label,
        };
        let Some(model_id) = model_id else {
            missing_model.push(candidate);
            continue;
        };
        match selected.get(&model_id) {
            Some(current)
                if !binding_candidate_is_better(
                    candidate.fraction,
                    candidate.reset,
                    candidate.source_index,
                    current.fraction,
                    current.reset,
                    current.source_index,
                    now,
                ) => {}
            _ => {
                selected.insert(model_id, candidate);
            }
        }
    }

    let mut candidates: Vec<ModelCandidate> = selected.into_values().collect();
    candidates.extend(missing_model);
    candidates.sort_by_key(|candidate| candidate.source_index);
    Ok(candidates
        .into_iter()
        .filter_map(|candidate| {
            let (card_id, window_key) = match candidate.model_id {
                Some(model_id) => {
                    let key = format!("model.{model_id}.v1");
                    (key.clone(), Some(key))
                }
                None => (format!("row.models.{}.v1", candidate.source_index), None),
            };
            quota_window(
                candidate.label,
                candidate.fraction,
                candidate.reset,
                now,
                card_id,
                window_key,
            )
        })
        .collect())
}

#[derive(Debug)]
struct QuotaBucketCandidate {
    model_id: Option<String>,
    fraction: f64,
    reset: Option<DateTime<Utc>>,
    source_index: usize,
}

fn buckets_from_quota(body: &str, now: DateTime<Utc>) -> Result<Vec<UsageWindow>, String> {
    let response: QuotaBucketsResponse = serde_json::from_str(body)
        .map_err(|e| format!("decode Antigravity retrieveUserQuota: {e}"))?;
    let mut selected: BTreeMap<String, QuotaBucketCandidate> = BTreeMap::new();
    let mut missing = Vec::new();
    for (source_index, bucket) in response.buckets.into_iter().enumerate() {
        let Ok(bucket) = serde_json::from_str::<Value>(bucket.get()) else {
            continue;
        };
        let Some(fraction) = bucket.get("remainingFraction").and_then(Value::as_f64) else {
            continue;
        };
        let reset = bucket
            .get("resetTime")
            .and_then(Value::as_str)
            .and_then(parse_datetime);
        let model_id = bucket
            .get("modelId")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|model| !model.is_empty())
            .map(str::to_string);
        let candidate = QuotaBucketCandidate {
            model_id: model_id.clone(),
            fraction,
            reset,
            source_index,
        };
        let Some(model_id) = model_id else {
            missing.push(candidate);
            continue;
        };
        match selected.get(&model_id) {
            Some(current) if !bucket_candidate_is_better(&candidate, current, now) => {}
            _ => {
                selected.insert(model_id, candidate);
            }
        }
    }

    let mut chosen: Vec<QuotaBucketCandidate> = selected.into_values().collect();
    chosen.extend(missing);
    chosen.sort_by_key(|candidate| candidate.source_index);
    Ok(chosen
        .into_iter()
        .filter_map(|candidate| {
            let label = candidate
                .model_id
                .clone()
                .unwrap_or_else(|| "Model".to_string());
            let (card_id, window_key) = match candidate.model_id {
                Some(model_id) => {
                    let key = format!("model.{model_id}.v1");
                    (key.clone(), Some(key))
                }
                None => (
                    format!("row.quota.bucket.{}.v1", candidate.source_index),
                    None,
                ),
            };
            quota_window(
                label,
                candidate.fraction,
                candidate.reset,
                now,
                card_id,
                window_key,
            )
        })
        .collect())
}

fn bucket_candidate_is_better(
    candidate: &QuotaBucketCandidate,
    current: &QuotaBucketCandidate,
    now: DateTime<Utc>,
) -> bool {
    binding_candidate_is_better(
        candidate.fraction,
        candidate.reset,
        candidate.source_index,
        current.fraction,
        current.reset,
        current.source_index,
        now,
    )
}

fn binding_candidate_is_better(
    candidate_fraction: f64,
    candidate_reset: Option<DateTime<Utc>>,
    candidate_index: usize,
    current_fraction: f64,
    current_reset: Option<DateTime<Utc>>,
    current_index: usize,
    now: DateTime<Utc>,
) -> bool {
    match (
        valid_remaining_fraction(candidate_fraction),
        valid_remaining_fraction(current_fraction),
    ) {
        (true, false) => return true,
        (false, true) => return false,
        _ => {}
    }
    match candidate_fraction.total_cmp(&current_fraction) {
        std::cmp::Ordering::Less => return true,
        std::cmp::Ordering::Greater => return false,
        std::cmp::Ordering::Equal => {}
    }
    let candidate_reset = candidate_reset.filter(|reset| *reset > now);
    let current_reset = current_reset.filter(|reset| *reset > now);
    match (candidate_reset, current_reset) {
        (Some(candidate), Some(current)) if candidate != current => return candidate < current,
        (Some(_), None) => return true,
        (None, Some(_)) => return false,
        _ => {}
    }
    candidate_index < current_index
}

// ── OAuth client discovery (scan installed Antigravity.app) ───────────────────

fn resolve_oauth_client() -> Option<(String, String)> {
    if let (Ok(id), Ok(secret)) = (
        std::env::var("ANTIGRAVITY_OAUTH_CLIENT_ID"),
        std::env::var("ANTIGRAVITY_OAUTH_CLIENT_SECRET"),
    ) {
        let (id, secret) = (id.trim().to_string(), secret.trim().to_string());
        if !id.is_empty() && !secret.is_empty() {
            return Some((id, secret));
        }
    }
    static CACHE: OnceLock<Option<(String, String)>> = OnceLock::new();
    CACHE.get_or_init(discover_client_from_app).clone()
}

fn discover_client_from_app() -> Option<(String, String)> {
    for path in client_artifact_candidates() {
        let Ok(data) = std::fs::read(&path) else {
            continue;
        };
        let ids = scan_client_ids(&data);
        let secrets = scan_client_secrets(&data);
        if let Some(client) = preferred_client(&ids, &secrets) {
            return Some(client);
        }
    }
    None
}

/// Where the installed IDE's own binaries carry the OAuth client id and secret.
///
/// Windows: the IDE installs per user under
/// `%LOCALAPPDATA%\Programs\Antigravity` (measured on the x64 test host,
/// 2026-09-25: the id and secret are in `resources\bin\language_server.exe`;
/// no `main.js` carries them), or machine-wide under `%ProgramFiles%`. This
/// list used to hold only the macOS `.app` paths, so on Windows the scan never
/// read a file and an expired `oauth_creds.json` access token could not be
/// refreshed.
#[cfg(windows)]
fn client_artifact_candidates() -> Vec<PathBuf> {
    windows_client_artifact_candidates(
        std::env::var_os("LOCALAPPDATA").as_deref(),
        std::env::var_os("ProgramFiles").as_deref(),
    )
}

#[cfg(any(windows, test))]
fn windows_client_artifact_candidates(
    local_app_data: Option<&std::ffi::OsStr>,
    program_files: Option<&std::ffi::OsStr>,
) -> Vec<PathBuf> {
    const RELATIVE: &str = "resources/bin/language_server.exe";
    [
        local_app_data.map(|root| Path::new(root).join("Programs").join("Antigravity")),
        program_files.map(|root| Path::new(root).join("Antigravity")),
    ]
    .into_iter()
    .flatten()
    // A relative root would resolve against whatever directory the app was
    // started from.
    .filter(|root| root.is_absolute())
    .map(|root| root.join(RELATIVE))
    .collect()
}

#[cfg(not(windows))]
fn client_artifact_candidates() -> Vec<PathBuf> {
    let relative = [
        "Contents/Resources/bin/language_server",
        "Contents/Resources/bin/language_server_macos",
        "Contents/Resources/app/extensions/antigravity/bin/language_server_macos_arm",
        "Contents/Resources/app/extensions/antigravity/bin/language_server_macos_x64",
        "Contents/Resources/app/extensions/antigravity/bin/language_server_macos",
        "Contents/Resources/app/out/main.js",
    ];
    let mut roots = vec![PathBuf::from("/Applications/Antigravity.app")];
    if let Some(home) = crate::user_home_dir() {
        roots.push(home.join("Applications/Antigravity.app"));
    }
    roots
        .iter()
        .flat_map(|root| relative.iter().map(move |r| root.join(r)))
        .collect()
}

fn is_token_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || b == b'-' || b == b'_'
}

fn find_sub(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || haystack.len() < needle.len() {
        return None;
    }
    haystack.windows(needle.len()).position(|w| w == needle)
}

fn scan_client_ids(data: &[u8]) -> Vec<String> {
    let suffix = b".apps.googleusercontent.com";
    let mut out: Vec<String> = Vec::new();
    let mut i = 0;
    while let Some(pos) = find_sub(&data[i..], suffix) {
        let end = i + pos + suffix.len();
        let mut start = i + pos;
        while start > 0 && is_token_byte(data[start - 1]) {
            start -= 1;
        }
        // The walk-back is greedy over `-` and `_` as well as alphanumerics, and
        // in a packed binary the bytes in front of a client id belong to whichever
        // string was laid down next to it. A Google client id is `<digits>-<token>`
        // with a single hyphen — the token and the `.apps.googleusercontent.com`
        // suffix carry none — so the id's delimiter is the LAST hyphen in the
        // segment. Re-anchor there and keep only the digit run immediately before
        // it. Anchoring on the first hyphen instead would keep a neighbour's own
        // `…letters<digits>-` tail in the head, and because `valid_client_id` only
        // checks the digits before the first hyphen it would accept the fabricated
        // id (e.g. `123-beta456-real.apps…` instead of `456-real.apps…`).
        if let Some(dash) = data[start..end].iter().rposition(|b| *b == b'-') {
            let mut head = start + dash;
            while head > start && data[head - 1].is_ascii_digit() {
                head -= 1;
            }
            start = head;
        }
        if let Ok(candidate) = std::str::from_utf8(&data[start..end]) {
            if valid_client_id(candidate) && !out.contains(&candidate.to_string()) {
                out.push(candidate.to_string());
            }
        }
        i = i + pos + suffix.len();
    }
    out
}

fn valid_client_id(s: &str) -> bool {
    s.ends_with(".apps.googleusercontent.com")
        && s.split_once('-')
            .is_some_and(|(head, _)| !head.is_empty() && head.bytes().all(|b| b.is_ascii_digit()))
}

fn scan_client_secrets(data: &[u8]) -> Vec<String> {
    let prefix = b"GOCSPX-";
    let total = prefix.len() + 28;
    let mut out: Vec<String> = Vec::new();
    let mut i = 0;
    while let Some(pos) = find_sub(&data[i..], prefix) {
        let abs = i + pos;
        if abs + total <= data.len() {
            let candidate = &data[abs..abs + total];
            if candidate[prefix.len()..].iter().all(|b| is_token_byte(*b)) {
                if let Ok(s) = std::str::from_utf8(candidate) {
                    if !out.contains(&s.to_string()) {
                        out.push(s.to_string());
                    }
                }
            }
        }
        i = abs + prefix.len();
    }
    out
}

/// codexbar's pairing heuristic for the (possibly multiple) ids/secrets baked
/// into the language_server binary.
fn preferred_client(ids: &[String], secrets: &[String]) -> Option<(String, String)> {
    if ids.is_empty() || secrets.is_empty() {
        return None;
    }
    if secrets.len() == 1 && ids.len() > 1 {
        return Some((ids[ids.len() - 1].clone(), secrets[0].clone()));
    }
    let secret = if secrets.len() == ids.len() && secrets.len() > 1 {
        secrets[secrets.len() - 1].clone()
    } else {
        secrets[0].clone()
    };
    Some((ids[0].clone(), secret))
}

// ── shared ────────────────────────────────────────────────────────────────────

fn gemini_home() -> Option<PathBuf> {
    gemini_home_from(
        std::env::var("GEMINI_CLI_HOME"),
        crate::user_home_dir().as_deref(),
    )
}

fn gemini_home_from(
    gemini_cli_home: Result<String, std::env::VarError>,
    user_home: Option<&Path>,
) -> Option<PathBuf> {
    let root = match gemini_cli_home {
        Ok(root) if !root.trim().is_empty() => root,
        Ok(_) | Err(_) => format!("{}/.gemini", user_home?.to_string_lossy()),
    };
    Some(PathBuf::from(root))
}

// ── Captured accounts (extra Google accounts copied from agy's login) ─────────
//
// Ported from macOS `agent_antigravity.rs` (945dbcc2) with Windows Credential
// Manager in place of the login keychain. A captured account is a second
// Google login the user signed `agy` into once and asked Syrtis to keep.
// Capture copies that login's refresh token, plus the OAuth client that issued
// it, into a generic credential Syrtis owns; every later poll refreshes from
// that credential and calls the same Code Assist quota methods as the primary
// remote route.
//
// It deliberately does not reuse the primary's file-bound refresh path
// (`refresh_access_token_with`): that path exists to share a credential with an
// external writer, and this credential has exactly one writer, Syrtis. So there
// is no refresh lock, no lineage binding and no compare-and-swap.
//
// What crosses each boundary:
// - agy's own credential (`gemini:antigravity`) is read (`agy_read_call`) when
//   the user presses Capture, or once per login change while automatic capture
//   is on (`auto_capture_with`), and never written or deleted: no builder
//   produces a write or delete for it. The poll path reads only its
//   `LastWritten` (`login_marker_with`). Nothing on this path starts a process.
// - Syrtis's credential (`CAPTURED_TARGET_PREFIX` + key) is a
//   `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE` credential whose blob is
//   the UTF-8 JSON `{"refresh_token","client_id","client_secret"}`. Its target
//   is built only by `captured_target`, which refuses anything but 64 hex.
// - The raw Google `sub` never leaves `capture_with` / `auto_capture_with`.
//   Everything downstream — the target, the registry, the FFI `accountKey`,
//   the account and history scopes — uses `captured_key(sub)`.
// - Every error leaving this section is a fixed code or a literal string: no
//   token, sub, key, email, Win32 error, serde text or Google
//   `error_description`.

/// Target prefix of the credentials Syrtis writes for captured accounts. The
/// same string as macOS's keychain service, so support can name one thing.
const CAPTURED_TARGET_PREFIX: &str = "com.nyanako.tokenbar.antigravity-account:";
/// Domain separator for `captured_key`, so the key cannot collide with a
/// SHA-256 of the bare `sub` computed anywhere else.
const CAPTURED_KEY_DOMAIN: &[u8] = b"antigravity-account\0";
/// An access token is reused until this long before its expiry.
const CAPTURED_TOKEN_MARGIN_SECS: i64 = 5 * 60;
const CAPTURED_FALLBACK_LABEL: &str = "Antigravity account";

// Win32 values (wincred.h), spelled here so the pure builders below and their
// tests compile off Windows; the Windows build asserts they equal windows-sys.
const CRED_TYPE_GENERIC: u32 = 1;
/// This user's sessions on this computer only. Never `CRED_PERSIST_ENTERPRISE`,
/// which roams with a domain profile and would carry the token elsewhere.
const CRED_PERSIST_LOCAL_MACHINE: u32 = 2;
const CRED_MAX_CREDENTIAL_BLOB_SIZE: usize = 5 * 512;

#[cfg(windows)]
const _: () = {
    use windows_sys::Win32::Security::Credentials as wincred;
    assert!(CRED_TYPE_GENERIC == wincred::CRED_TYPE_GENERIC);
    assert!(CRED_PERSIST_LOCAL_MACHINE == wincred::CRED_PERSIST_LOCAL_MACHINE);
    assert!(CRED_MAX_CREDENTIAL_BLOB_SIZE == wincred::CRED_MAX_CREDENTIAL_BLOB_SIZE as usize);
};

const CAPTURED_REFRESH_RETRY: &str = "Antigravity token refresh failed. Retrying automatically.";
pub(crate) const CAPTURED_ITEM_MISSING: &str =
    "Antigravity account credential was not found. Capture the account again.";
const CAPTURED_ITEM_UNREADABLE: &str =
    "Antigravity account credential could not be read. Capture the account again.";
const CAPTURED_REFRESH_REJECTED: &str =
    "Antigravity account sign-in was rejected. Capture the account again.";
const CAPTURED_AUTH_EXPIRED: &str =
    "Antigravity account sign-in expired. Capture the account again.";
const CAPTURED_IDENTITY_UNVERIFIED: &str = "Antigravity account identity could not be verified.";
const CAPTURED_CLIENT_UNAVAILABLE: &str = "Antigravity usage client could not be created.";

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct CapturedAccount {
    pub key: String,
    pub label: String,
}

// ── registry ──

static CAPTURED_ACCOUNTS: std::sync::LazyLock<std::sync::RwLock<Vec<CapturedAccount>>> =
    std::sync::LazyLock::new(|| std::sync::RwLock::new(Vec::new()));

/// Registered captured accounts, in the order the shell listed them. Empty by
/// default, so a process that never calls the setter fetches the one
/// Antigravity card it always has. Holds no secret.
pub(crate) fn captured_accounts() -> Vec<CapturedAccount> {
    CAPTURED_ACCOUNTS
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
}

/// Replace the registry from `[{"key","label"}]`. Full-replace (`[]` clears).
/// A rejected entry is reported by index and a fixed reason, never echoed.
pub(crate) fn set_captured_accounts_from_json(raw: &str) -> Result<Value, String> {
    let input: Value =
        serde_json::from_str(raw).map_err(|_| "invalid_accounts_json".to_string())?;
    let entries = input
        .as_array()
        .ok_or_else(|| "invalid_accounts_json".to_string())?;
    let mut registered: Vec<CapturedAccount> = Vec::new();
    let mut rejected: Vec<Value> = Vec::new();
    for (index, entry) in entries.iter().enumerate() {
        let key = entry.get("key").and_then(Value::as_str);
        let label = entry.get("label").and_then(Value::as_str);
        let reason = match (key, label) {
            (Some(key), Some(_)) if !valid_captured_key(key) => "invalid key",
            (Some(key), Some(_)) if registered.iter().any(|a| a.key == key) => "duplicate key",
            (Some(key), Some(label)) => {
                registered.push(CapturedAccount {
                    key: key.to_string(),
                    label: label.to_string(),
                });
                continue;
            }
            _ => "invalid entry",
        };
        rejected.push(json!({ "index": index, "reason": reason }));
    }
    let registered_count = registered.len();
    *CAPTURED_ACCOUNTS
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = registered;
    Ok(json!({ "registeredCount": registered_count, "rejected": rejected }))
}

#[cfg(test)]
pub(crate) static CAPTURED_ACCOUNTS_TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

// ── key, target and Credential Manager calls ──

/// `hex(SHA-256("antigravity-account\0" + sub))`, 64 lowercase hex.
pub(crate) fn captured_key(sub: &str) -> String {
    use sha2::{Digest, Sha256};
    let mut hasher = Sha256::new();
    hasher.update(CAPTURED_KEY_DOMAIN);
    hasher.update(sub.as_bytes());
    hasher
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

/// `^[0-9a-f]{64}$`.
fn valid_captured_key(key: &str) -> bool {
    key.len() == 64 && key.bytes().all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
}

/// The only way a Syrtis target is built: the prefix plus a validated key. So
/// no captured call can ever name `gemini:*` or anything outside the prefix.
fn captured_target(key: &str) -> Option<String> {
    valid_captured_key(key).then(|| format!("{CAPTURED_TARGET_PREFIX}{key}"))
}

/// A credential blob, zeroed when dropped. No `Debug`, so it cannot be logged.
pub(crate) struct SecretBytes(Vec<u8>);

impl Drop for SecretBytes {
    fn drop(&mut self) {
        for byte in self.0.iter_mut() {
            // SAFETY: `byte` is a valid, exclusive reference into the Vec.
            unsafe { std::ptr::write_volatile(byte, 0) };
        }
    }
}

impl std::ops::Deref for SecretBytes {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        &self.0
    }
}

/// One Credential Manager operation. Only the builders below create one, and
/// every captured builder goes through `captured_target`. No `Debug`: a write
/// carries the secret.
#[derive(Clone, PartialEq, Eq)]
pub(crate) enum CredCall {
    Read {
        target: String,
        cred_type: u32,
    },
    Write {
        target: String,
        cred_type: u32,
        persist: u32,
        blob: Vec<u8>,
    },
    Delete {
        target: String,
        cred_type: u32,
    },
}

/// What a `CredCall` returned. `Ok` carries the blob of a read, and is empty
/// for a write or delete. The Win32 error code is never kept.
#[cfg_attr(not(any(windows, test)), allow(dead_code))]
pub(crate) enum CredOutcome {
    Ok(SecretBytes),
    NotFound,
    Failed,
}

/// agy's login, read only. There is no write or delete builder for it.
fn agy_read_call() -> CredCall {
    CredCall::Read {
        target: AGY_CREDENTIAL_TARGET.to_string(),
        cred_type: CRED_TYPE_GENERIC,
    }
}

fn read_item_call(key: &str) -> Option<CredCall> {
    Some(CredCall::Read {
        target: captured_target(key)?,
        cred_type: CRED_TYPE_GENERIC,
    })
}

/// Refuses an empty blob or one over Credential Manager's 2560-byte limit,
/// before any call.
fn write_item_call(key: &str, blob: Vec<u8>) -> Option<CredCall> {
    if blob.is_empty() || blob.len() > CRED_MAX_CREDENTIAL_BLOB_SIZE {
        return None;
    }
    Some(CredCall::Write {
        target: captured_target(key)?,
        cred_type: CRED_TYPE_GENERIC,
        persist: CRED_PERSIST_LOCAL_MACHINE,
        blob,
    })
}

fn delete_item_call(key: &str) -> Option<CredCall> {
    Some(CredCall::Delete {
        target: captured_target(key)?,
        cred_type: CRED_TYPE_GENERIC,
    })
}

// ── stored credential and agy's login ──

/// The blob of a captured credential. The client is pinned at capture so a
/// poll never rescans application binaries.
#[derive(Clone, PartialEq, Eq)]
struct StoredCredential {
    refresh_token: String,
    client: OAuthClient,
}

#[derive(Clone, PartialEq, Eq)]
pub(crate) struct OAuthClient {
    id: String,
    secret: String,
}

/// UTF-8 JSON. macOS base64-encodes the same object only because its value
/// passes through the `security -i` command language; Credential Manager
/// stores bytes.
fn encode_stored(credential: &StoredCredential) -> Vec<u8> {
    json!({
        "refresh_token": credential.refresh_token,
        "client_id": credential.client.id,
        "client_secret": credential.client.secret,
    })
    .to_string()
    .into_bytes()
}

fn non_empty_str<'a>(value: &'a Value, field: &str) -> Option<&'a str> {
    value
        .get(field)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|text| !text.is_empty())
}

fn decode_stored(blob: &[u8]) -> Option<StoredCredential> {
    let value: Value = serde_json::from_slice(blob).ok()?;
    Some(StoredCredential {
        refresh_token: non_empty_str(&value, "refresh_token")?.to_string(),
        client: OAuthClient {
            id: non_empty_str(&value, "client_id")?.to_string(),
            secret: non_empty_str(&value, "client_secret")?.to_string(),
        },
    })
}

/// The claims of a JWT, decoded without verification. Callers use them only
/// as a label, a client selector, or a value compared with another JWT's.
fn jwt_claims(token: &str) -> Option<Value> {
    let payload = token.split('.').nth(1)?.trim_end_matches('=');
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(payload)
        .ok()?;
    serde_json::from_slice(&bytes).ok()
}

struct AgyLogin {
    refresh_token: String,
    sub: String,
    aud: String,
    email: Option<String>,
}

/// agy's Windows credential blob: raw UTF-8 JSON
/// `{token:{refresh_token,…}, id_token, …}`, no prefix and no base64
/// (measured on a real device by a structure-only probe). macOS's
/// `go-keyring-base64:` form is not accepted here.
fn parse_agy_login(blob: &[u8]) -> Result<AgyLogin, CaptureError> {
    let login: Value =
        serde_json::from_slice(blob).map_err(|_| CaptureError::AgyLoginUnreadable)?;
    let refresh_token = login
        .get("token")
        .and_then(|token| non_empty_str(token, "refresh_token"))
        .ok_or(CaptureError::AgyLoginUnreadable)?
        .to_string();
    let claims = non_empty_str(&login, "id_token")
        .and_then(jwt_claims)
        .ok_or(CaptureError::AgyLoginMissingIdentity)?;
    let sub = non_empty_str(&claims, "sub").ok_or(CaptureError::AgyLoginMissingIdentity)?;
    let aud = non_empty_str(&claims, "aud").ok_or(CaptureError::AgyLoginMissingIdentity)?;
    Ok(AgyLogin {
        refresh_token,
        sub: sub.to_string(),
        aud: aud.to_string(),
        email: non_empty_str(&claims, "email").map(str::to_string),
    })
}

/// Every client with id `client_id` found in the artifacts, one per secret of
/// each artifact that contains that id. The id is the token's own `aud`, not
/// `preferred_client`'s positional pick: macOS measured agy's issuing client
/// differing from that pick, and a refresh token only refreshes with the
/// client that issued it.
fn clients_for_aud<I>(client_id: &str, artifacts: I) -> Vec<OAuthClient>
where
    I: IntoIterator<Item = Vec<u8>>,
{
    let mut clients: Vec<OAuthClient> = Vec::new();
    for data in artifacts {
        if !scan_client_ids(&data).iter().any(|id| id == client_id) {
            continue;
        }
        for secret in scan_client_secrets(&data) {
            let client = OAuthClient {
                id: client_id.to_string(),
                secret,
            };
            if !clients.contains(&client) {
                clients.push(client);
            }
        }
    }
    clients
}

/// Every file a captured account's client may be scanned from: the IDE's
/// binaries plus `agy.exe`, found by path lookup only (never started).
#[cfg(any(windows, test))]
fn windows_captured_artifact_candidates(
    local_app_data: Option<&std::ffi::OsStr>,
    program_files: Option<&std::ffi::OsStr>,
    path: Option<&std::ffi::OsStr>,
    is_file: impl Fn(&Path) -> bool,
) -> Vec<PathBuf> {
    let mut paths = windows_client_artifact_candidates(local_app_data, program_files);
    paths.extend(agy_executable_from(local_app_data, path, is_file));
    paths
}

#[cfg(windows)]
fn captured_artifact_candidates() -> Vec<PathBuf> {
    windows_captured_artifact_candidates(
        std::env::var_os("LOCALAPPDATA").as_deref(),
        std::env::var_os("ProgramFiles").as_deref(),
        std::env::var_os("PATH").as_deref(),
        Path::is_file,
    )
}

#[cfg(not(windows))]
fn captured_artifact_candidates() -> Vec<PathBuf> {
    client_artifact_candidates()
}

// ── token endpoint ──

enum TokenRejection {
    /// `invalid_client` / `unauthorized_client`: this secret is not the
    /// issuing client's; capture tries the next candidate.
    WrongClient,
    Other,
}

/// Only the OAuth `error` code is read from a rejection; `error_description`
/// is never looked at, so it cannot reach a card or the FFI.
fn token_response(status: u16, body: &str) -> Result<Value, TokenRejection> {
    let json: Option<Value> = serde_json::from_str(body).ok();
    if (200..=299).contains(&status) {
        return json
            .filter(|json| non_empty_str(json, "access_token").is_some())
            .ok_or(TokenRejection::Other);
    }
    match json.as_ref().and_then(|json| non_empty_str(json, "error")) {
        Some("invalid_client" | "unauthorized_client") => Err(TokenRejection::WrongClient),
        _ => Err(TokenRejection::Other),
    }
}

// ── I/O seam ──

/// Everything the captured path does outside this process. Production is
/// `SystemCapturedIo`; tests substitute every method, so no test touches
/// Credential Manager, scans an installed binary, or reaches the network.
/// There is no process method: nothing on this path can start one.
pub(crate) trait CapturedIo {
    fn credential(&self, call: CredCall) -> CredOutcome;
    /// agy's credential `LastWritten` (FILETIME), `Ok(None)` when absent.
    fn agy_last_written(&self) -> Result<Option<u64>, CredentialUnreadable>;
    /// The bytes of each Antigravity IDE / agy artifact that may embed a
    /// client. Called when the user presses Capture, or by an automatic
    /// capture after a login change.
    async fn client_artifacts(&self) -> Vec<Vec<u8>>;
    /// POST a refresh-token grant; `Ok((status, body))` for any HTTP answer
    /// except 429 / 5xx, which are transient failures like every other
    /// transport failure.
    async fn token_post(
        &self,
        client: &OAuthClient,
        refresh_token: &str,
        binding: Option<ProviderCacheBinding>,
    ) -> Result<(u16, String), ProviderFetchFailure>;
    fn scopes(
        &self,
        key: &str,
    ) -> (
        Result<AccountScope, AccountScopeError>,
        Result<HistoryScope, AccountScopeError>,
    );
    async fn quota(
        &self,
        access_token: String,
        account_scope: AccountScope,
        history_scope: Result<HistoryScope, AccountScopeError>,
        now: DateTime<Utc>,
    ) -> Result<Fetched, ProviderFetchFailure>;
}

/// A captured account's scopes: `OpaqueId` = its key, under the provider
/// "antigravity". The key differs per `sub`, and the primary's account scope
/// is credential-derived and its history scope the per-installation constant,
/// so neither can coincide with a captured account's.
fn captured_scopes<Auth, History>(
    key: &str,
    resolve_authoritative: Auth,
    resolve_history: History,
) -> (
    Result<AccountScope, AccountScopeError>,
    Result<HistoryScope, AccountScopeError>,
)
where
    Auth: FnOnce(&str, AuthoritativeIdKind, &str) -> Result<AccountScope, AccountScopeError>,
    History: FnOnce(
        &str,
        Option<(AuthoritativeIdKind, &str)>,
    ) -> Result<HistoryScope, AccountScopeError>,
{
    (
        resolve_authoritative("antigravity", AuthoritativeIdKind::OpaqueId, key),
        resolve_history("antigravity", Some((AuthoritativeIdKind::OpaqueId, key))),
    )
}

/// `retrieveUserQuotaSummary`: the allowance groups agy's `/usage` prints,
/// e.g. "Gemini Models" and "Claude and GPT models". Shape measured on macOS
/// on 2026-10-02: `groups[].{displayName, buckets[].{bucketId, displayName,
/// remainingFraction, resetTime, window}}`. Labels and card ids follow the
/// Windows agy route (`parse_agy_usage`), so a captured card reads like the
/// primary's. `None` on any failure or an empty answer: the caller falls back
/// to the catalog.
async fn fetch_quota_summary(
    context: &RemoteContext,
    now: DateTime<Utc>,
) -> Option<Vec<UsageWindow>> {
    let body = match context.project.as_deref() {
        Some(project) => json!({ "project": project }),
        None => json!({}),
    };
    let response = code_assist_post(
        &context.client,
        "retrieveUserQuotaSummary",
        &body,
        &context.access_token,
        context.cache_binding.clone(),
        true,
    )
    .await
    .ok()?;
    let windows = windows_from_quota_summary(&response, now);
    (!windows.is_empty()).then_some(windows)
}

/// The bucket's declared `window` ("weekly", "5h") is not read: a declared
/// duration is a separate change (W7c).
fn windows_from_quota_summary(body: &str, now: DateTime<Utc>) -> Vec<UsageWindow> {
    let Ok(response) = serde_json::from_str::<Value>(body) else {
        return Vec::new();
    };
    let mut windows = Vec::new();
    for group in response
        .get("groups")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        let group_name = non_empty_str(group, "displayName").unwrap_or("Antigravity");
        for bucket in group
            .get("buckets")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
        {
            let Some(id) = non_empty_str(bucket, "bucketId") else {
                continue;
            };
            let Some(fraction) = bucket.get("remainingFraction").and_then(Value::as_f64) else {
                continue;
            };
            let reset = bucket
                .get("resetTime")
                .and_then(Value::as_str)
                .and_then(parse_datetime);
            let label = format!(
                "{group_name} · {}",
                non_empty_str(bucket, "displayName").unwrap_or("Limit")
            );
            let card_id = format!("agy.{id}.v1");
            if let Some(window) =
                quota_window(label, fraction, reset, now, card_id.clone(), Some(card_id))
            {
                windows.push(window);
            }
        }
    }
    windows
}

/// Both the token and the Code Assist requests of a captured account go
/// through this client. No redirects: the default policy would resend a POST
/// body carrying the refresh token or the bearer to wherever a 307/308 points.
fn captured_http_client() -> Result<reqwest::Client, ProviderFetchFailure> {
    provider_http_client_builder()
        .timeout(std::time::Duration::from_secs(30))
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|_| ProviderFetchFailure::terminal(CAPTURED_CLIENT_UNAVAILABLE))
}

/// The Credential Manager calls. Kept this thin on purpose: every input
/// (target, type, persist, blob) comes from the pure builders above, which the
/// tests assert on.
#[cfg(windows)]
mod credential_manager {
    use super::{CredCall, CredOutcome, SecretBytes};
    use windows_sys::Win32::Foundation::{GetLastError, ERROR_NOT_FOUND};
    use windows_sys::Win32::Security::Credentials::{
        CredDeleteW, CredFree, CredReadW, CredWriteW, CREDENTIALW,
    };

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn not_found_or_failed() -> CredOutcome {
        // SAFETY: reads the calling thread's last-error value only.
        if unsafe { GetLastError() } == ERROR_NOT_FOUND {
            CredOutcome::NotFound
        } else {
            CredOutcome::Failed
        }
    }

    pub(super) fn call(call: CredCall) -> CredOutcome {
        match call {
            CredCall::Read { target, cred_type } => read(&target, cred_type),
            CredCall::Write {
                target,
                cred_type,
                persist,
                blob,
            } => {
                let blob = SecretBytes(blob);
                write(&target, cred_type, persist, &blob)
            }
            CredCall::Delete { target, cred_type } => {
                let target = wide(&target);
                // SAFETY: `target` is NUL-terminated and outlives the call.
                if unsafe { CredDeleteW(target.as_ptr(), cred_type, 0) } != 0 {
                    CredOutcome::Ok(SecretBytes(Vec::new()))
                } else {
                    not_found_or_failed()
                }
            }
        }
    }

    /// Copies the blob out, then zeroes the OS allocation before `CredFree`.
    /// No pointer into the `CREDENTIALW` leaves this function.
    fn read(target: &str, cred_type: u32) -> CredOutcome {
        let target = wide(target);
        let mut credential: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: `target` is NUL-terminated and outlives the call. On success
        // CredReadW hands back one allocation that is only read here, its blob
        // spans `CredentialBlobSize` bytes, and it is freed exactly once.
        unsafe {
            if CredReadW(target.as_ptr(), cred_type, 0, &mut credential) == 0 {
                return not_found_or_failed();
            }
            if credential.is_null() {
                return CredOutcome::Failed;
            }
            let size = (*credential).CredentialBlobSize as usize;
            let blob = (*credential).CredentialBlob;
            let copy = if blob.is_null() || size == 0 {
                SecretBytes(Vec::new())
            } else {
                let copy = SecretBytes(std::slice::from_raw_parts(blob, size).to_vec());
                for offset in 0..size {
                    std::ptr::write_volatile(blob.add(offset), 0);
                }
                copy
            };
            CredFree(credential.cast::<core::ffi::c_void>());
            CredOutcome::Ok(copy)
        }
    }

    fn write(target: &str, cred_type: u32, persist: u32, blob: &[u8]) -> CredOutcome {
        let Ok(size) = u32::try_from(blob.len()) else {
            return CredOutcome::Failed;
        };
        let mut target = wide(target);
        let credential = CREDENTIALW {
            Type: cred_type,
            TargetName: target.as_mut_ptr(),
            CredentialBlobSize: size,
            // CredWriteW only reads the blob; the `*mut` is the struct's type.
            CredentialBlob: blob.as_ptr().cast_mut(),
            Persist: persist,
            ..Default::default()
        };
        // SAFETY: every pointer in `credential` is valid for the call: `target`
        // is NUL-terminated and `blob` spans `size` bytes, both outliving it.
        if unsafe { CredWriteW(&credential, 0) } != 0 {
            CredOutcome::Ok(SecretBytes(Vec::new()))
        } else {
            CredOutcome::Failed
        }
    }
}

/// The fake `CapturedIo` backend never runs `credential_manager`, so a wrong
/// struct field, flag or blob length there would pass every other test. This
/// one writes, reads and deletes a real Credential Manager entry through the
/// production builder and dispatcher. Ignored because it mutates the running
/// user's Credential Manager; run it on a Windows host with
/// `cargo test -p tb_core_ffi -- --ignored credential_manager_round_trip`.
#[cfg(all(windows, test))]
mod credential_manager_tests {
    use super::{
        credential_manager, write_item_call, CredCall, CredOutcome, CAPTURED_TARGET_PREFIX,
        CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC,
    };
    use windows_sys::Win32::Foundation::{GetLastError, ERROR_NOT_FOUND};
    use windows_sys::Win32::Security::Credentials::{
        self as wincred, CredDeleteW, CredEnumerateW, CredFree, CredReadW, CREDENTIALW,
    };

    /// Differs from `CAPTURED_TARGET_PREFIX` by `-test` before the colon, so
    /// the test can never name a real captured account.
    const TEST_PREFIX: &str = "com.nyanako.tokenbar.antigravity-account-test:";

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// 64 lowercase hex, unique per run. No RNG crate is a direct dependency,
    /// so it is SHA-256 of the process id and the current time.
    fn random_hex() -> String {
        use sha2::{Digest, Sha256};
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let mut hasher = Sha256::new();
        hasher.update(std::process::id().to_le_bytes());
        hasher.update(nanos.to_le_bytes());
        hasher
            .finalize()
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect()
    }

    /// Removes the test entry even when an assertion panics.
    struct DeleteOnDrop(Vec<u16>);

    impl Drop for DeleteOnDrop {
        fn drop(&mut self) {
            // SAFETY: the target is NUL-terminated and outlives the call. The
            // result is ignored: the test may already have deleted it.
            unsafe { CredDeleteW(self.0.as_ptr(), wincred::CRED_TYPE_GENERIC, 0) };
        }
    }

    #[test]
    #[ignore = "writes to the real Credential Manager; run on Windows with --ignored"]
    fn credential_manager_round_trip() {
        let key = random_hex();
        let target = format!("{TEST_PREFIX}{key}");
        assert!(!target.starts_with("gemini:"));
        assert!(!target.starts_with(CAPTURED_TARGET_PREFIX));
        assert!(!target.starts_with("com.nyanako.tokenbar.antigravity-account:"));
        let _guard = DeleteOnDrop(wide(&target));

        // Fake, clearly non-secret data the size of a real agy item.
        const BLOB_LEN: usize = 1434;
        let mut text = String::from(r#"{"tokenbar_test":"not a secret","pad":""#);
        text.push_str(&"x".repeat(BLOB_LEN - text.len() - 2));
        text.push_str(r#""}"#);
        let blob = text.into_bytes();
        assert_eq!(blob.len(), BLOB_LEN);

        // Type and persist come from the production builder; only the target
        // is swapped for the test one.
        let Some(CredCall::Write {
            cred_type,
            persist,
            blob: built_blob,
            ..
        }) = write_item_call(&key, blob.clone())
        else {
            panic!("write_item_call refused a valid key and blob");
        };
        let write = CredCall::Write {
            target: target.clone(),
            cred_type,
            persist,
            blob: built_blob,
        };
        if !matches!(credential_manager::call(write), CredOutcome::Ok(_)) {
            // SAFETY: reads the calling thread's last-error value only; nothing
            // since CredWriteW has made a Win32 call that sets it.
            let error = unsafe { GetLastError() };
            panic!("write failed: Win32 error {error}");
        }

        match credential_manager::call(CredCall::Read {
            target: target.clone(),
            cred_type,
        }) {
            CredOutcome::Ok(read) => assert!(*read == *blob, "read-back blob differs"),
            CredOutcome::NotFound => panic!("read after write: not found"),
            CredOutcome::Failed => panic!("read after write: failed"),
        }

        // What Windows actually stored, read independently of production.
        let wide_target = wide(&target);
        // SAFETY: `wide_target` is NUL-terminated and outlives the call; the
        // allocation is read only here and freed exactly once.
        let (stored_type, stored_persist, stored_size) = unsafe {
            let mut credential: *mut CREDENTIALW = std::ptr::null_mut();
            let ok = CredReadW(
                wide_target.as_ptr(),
                wincred::CRED_TYPE_GENERIC,
                0,
                &mut credential,
            );
            assert!(ok != 0 && !credential.is_null(), "raw CredReadW failed");
            let fields = (
                (*credential).Type,
                (*credential).Persist,
                (*credential).CredentialBlobSize,
            );
            CredFree(credential.cast::<core::ffi::c_void>());
            fields
        };
        assert_eq!(stored_type, wincred::CRED_TYPE_GENERIC);
        assert_eq!(stored_type, CRED_TYPE_GENERIC);
        assert_eq!(stored_persist, wincred::CRED_PERSIST_LOCAL_MACHINE);
        assert_eq!(stored_persist, CRED_PERSIST_LOCAL_MACHINE);
        assert_eq!(stored_size as usize, blob.len());

        assert!(
            matches!(
                credential_manager::call(CredCall::Delete {
                    target: target.clone(),
                    cred_type,
                }),
                CredOutcome::Ok(_)
            ),
            "delete failed"
        );
        assert!(
            matches!(
                credential_manager::call(CredCall::Read {
                    target: target.clone(),
                    cred_type,
                }),
                CredOutcome::NotFound
            ),
            "read after delete must be NotFound"
        );

        // No test entry left behind, from this run or an earlier one. The
        // filter is required: never enumerate the user's whole vault.
        let filter = wide(&format!("{TEST_PREFIX}*"));
        let mut count: u32 = 0;
        let mut credentials: *mut *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: `filter` is NUL-terminated and outlives the call. On success
        // the array is one allocation, freed once and never dereferenced.
        let ok = unsafe { CredEnumerateW(filter.as_ptr(), 0, &mut count, &mut credentials) };
        // SAFETY: reads the calling thread's last-error value only, right
        // after the call that set it.
        let error = unsafe { GetLastError() };
        if ok != 0 {
            unsafe { CredFree(credentials.cast::<core::ffi::c_void>()) };
            panic!("{count} test credential(s) left behind");
        }
        assert_eq!(error, ERROR_NOT_FOUND, "filtered CredEnumerateW failed");
    }
}

pub(crate) struct SystemCapturedIo;

impl CapturedIo for SystemCapturedIo {
    #[cfg(windows)]
    fn credential(&self, call: CredCall) -> CredOutcome {
        credential_manager::call(call)
    }

    /// This repository ships Windows only; its non-Windows build exists to run
    /// the tests, which never use this backend.
    #[cfg(not(windows))]
    fn credential(&self, _call: CredCall) -> CredOutcome {
        CredOutcome::Failed
    }

    #[cfg(windows)]
    fn agy_last_written(&self) -> Result<Option<u64>, CredentialUnreadable> {
        read_agy_credential_last_written()
    }

    #[cfg(not(windows))]
    fn agy_last_written(&self) -> Result<Option<u64>, CredentialUnreadable> {
        Err(CredentialUnreadable)
    }

    /// Off the async poll: the IDE's `language_server.exe` is ~130 MB.
    async fn client_artifacts(&self) -> Vec<Vec<u8>> {
        tokio::task::spawn_blocking(|| {
            captured_artifact_candidates()
                .into_iter()
                .filter_map(|path| std::fs::read(path).ok())
                .collect()
        })
        .await
        .unwrap_or_default()
    }

    async fn token_post(
        &self,
        client: &OAuthClient,
        refresh_token: &str,
        binding: Option<ProviderCacheBinding>,
    ) -> Result<(u16, String), ProviderFetchFailure> {
        let http = captured_http_client()?;
        let form = format!(
            "client_id={}&client_secret={}&refresh_token={}&grant_type=refresh_token",
            percent_encode(&client.id),
            percent_encode(&client.secret),
            percent_encode(refresh_token),
        );
        let response = http
            .post(GOOGLE_TOKEN_URL)
            .header(
                reqwest::header::CONTENT_TYPE,
                "application/x-www-form-urlencoded",
            )
            .body(form)
            .send()
            .await
            .map_err(|error| {
                ProviderFetchFailure::from_send_error(
                    CAPTURED_REFRESH_RETRY,
                    binding.clone(),
                    &error,
                )
            })?;
        let status = response.status().as_u16();
        if status == 429 {
            return Err(ProviderFetchFailure::transient(
                CAPTURED_REFRESH_RETRY,
                binding,
                SafeTransportDiagnostic::rate_limited(status),
            ));
        }
        if (500..=599).contains(&status) {
            return Err(ProviderFetchFailure::transient(
                CAPTURED_REFRESH_RETRY,
                binding,
                SafeTransportDiagnostic::server_error(status),
            ));
        }
        let body = response.text().await.map_err(|error| {
            ProviderFetchFailure::transient(
                CAPTURED_REFRESH_RETRY,
                binding,
                SafeTransportDiagnostic::from_facts(TransportErrorFacts::from_reqwest(
                    &error,
                    TransportPhase::ResponseBody,
                )),
            )
        })?;
        Ok((status, body))
    }

    fn scopes(
        &self,
        key: &str,
    ) -> (
        Result<AccountScope, AccountScopeError>,
        Result<HistoryScope, AccountScopeError>,
    ) {
        captured_scopes(
            key,
            agent_account_scope::resolve_authoritative,
            agent_account_scope::resolve_history_scope,
        )
    }

    /// loadCodeAssist, then retrieveUserQuotaSummary, then fetchAvailableModels
    /// with retrieveUserQuota as the fallback, with the same precedence as the
    /// primary remote route (`fetch_with` with no local route).
    async fn quota(
        &self,
        access_token: String,
        account_scope: AccountScope,
        history_scope: Result<HistoryScope, AccountScopeError>,
        now: DateTime<Utc>,
    ) -> Result<Fetched, ProviderFetchFailure> {
        let cache_binding = ProviderCacheBinding::primary(account_scope.clone());
        let client = captured_http_client()?;
        let code_assist_body = code_assist_post(
            &client,
            "loadCodeAssist",
            &json!({
                "metadata": { "ideType": "ANTIGRAVITY", "platform": "PLATFORM_UNSPECIFIED", "pluginType": "GEMINI" }
            }),
            &access_token,
            Some(cache_binding.clone()),
            false,
        )
        .await
        .map_err(|failure| match failure {
            CodeAssistPostFailure::Forbidden => {
                ProviderFetchFailure::terminal("Antigravity loadCodeAssist permission was denied.")
            }
            CodeAssistPostFailure::Failure(failure) => failure,
        })?;
        let code_assist: Value = serde_json::from_str(&code_assist_body).map_err(|_| {
            ProviderFetchFailure::terminal(
                "Antigravity loadCodeAssist response could not be decoded.",
            )
        })?;
        let context = RemoteContext {
            client,
            access_token,
            project: project_id(&code_assist),
            plan: resolve_remote_plan(&code_assist),
            account_scope,
            cache_binding: Some(cache_binding),
        };
        // The same grouped allowances agy's `/usage` prints (and the primary
        // card shows on the agy route); the per-model catalog is the fallback.
        if let Some(windows) = fetch_quota_summary(&context, now).await {
            return Ok(context.finish_with_history(windows, history_scope));
        }
        let secondary_history = history_scope.clone();
        fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async move {
                match fetch_available_models(&context, now).await {
                    PrimaryQuotaAttempt::Success(windows) => {
                        PrimaryAttempt::Success(context.finish_with_history(windows, history_scope))
                    }
                    PrimaryQuotaAttempt::Forbidden => PrimaryAttempt::Forbidden(context),
                    PrimaryQuotaAttempt::SchemaContradiction(failure) => {
                        PrimaryAttempt::SchemaContradiction { context, failure }
                    }
                    PrimaryQuotaAttempt::Transient => PrimaryAttempt::Transient(context),
                    PrimaryQuotaAttempt::Terminal(failure) => PrimaryAttempt::FinalFailure(failure),
                }
            },
            |context: RemoteContext| async move {
                let windows = fetch_user_quota(&context, now).await?;
                Ok(context.finish_with_history(windows, secondary_history))
            },
        )
        .await
    }
}

// ── access tokens ──

pub(crate) struct CachedToken {
    access_token: String,
    expires_at: DateTime<Utc>,
}

/// Access tokens per key, in memory only. Never persisted.
pub(crate) type CapturedTokenCache = std::sync::Mutex<HashMap<String, CachedToken>>;

static CAPTURED_TOKENS: std::sync::LazyLock<CapturedTokenCache> =
    std::sync::LazyLock::new(|| std::sync::Mutex::new(HashMap::new()));

fn lock_tokens(
    cache: &CapturedTokenCache,
) -> std::sync::MutexGuard<'_, HashMap<String, CachedToken>> {
    cache
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// A cached access token while it has more than five minutes left; otherwise
/// read the credential and refresh once. The credential is written only when
/// Google returns a refresh token different from the stored one.
///
/// ponytail: no cross-process lock. Two Syrtis processes refreshing one
/// captured account both succeed, because Google issues a new access token
/// without rotating the refresh token (measured on macOS). A lock is added
/// only if rotation is ever observed, which is a stop condition.
async fn captured_access_token<I: CapturedIo>(
    io: &I,
    cache: &CapturedTokenCache,
    key: &str,
    binding: &ProviderCacheBinding,
    now: DateTime<Utc>,
) -> Result<String, ProviderFetchFailure> {
    if let Some(cached) = lock_tokens(cache).get(key).filter(|cached| {
        cached.expires_at - chrono::Duration::seconds(CAPTURED_TOKEN_MARGIN_SECS) > now
    }) {
        return Ok(cached.access_token.clone());
    }
    let call = read_item_call(key)
        .ok_or_else(|| ProviderFetchFailure::terminal(CAPTURED_ITEM_UNREADABLE))?;
    let stored = match io.credential(call) {
        CredOutcome::Ok(blob) => decode_stored(&blob),
        CredOutcome::NotFound => return Err(ProviderFetchFailure::terminal(CAPTURED_ITEM_MISSING)),
        CredOutcome::Failed => None,
    }
    .ok_or_else(|| ProviderFetchFailure::terminal(CAPTURED_ITEM_UNREADABLE))?;

    let (status, body) = io
        .token_post(&stored.client, &stored.refresh_token, Some(binding.clone()))
        .await?;
    let json = token_response(status, &body)
        .map_err(|_| ProviderFetchFailure::terminal(CAPTURED_REFRESH_REJECTED))?;
    let access_token = non_empty_str(&json, "access_token")
        .ok_or_else(|| ProviderFetchFailure::terminal(CAPTURED_REFRESH_REJECTED))?
        .to_string();
    if let Some(expires_in) = json.get("expires_in").and_then(Value::as_i64) {
        lock_tokens(cache).insert(
            key.to_string(),
            CachedToken {
                access_token: access_token.clone(),
                expires_at: now + chrono::Duration::seconds(expires_in),
            },
        );
    }
    if let Some(rotated) = non_empty_str(&json, "refresh_token") {
        if rotated != stored.refresh_token {
            let updated = StoredCredential {
                refresh_token: rotated.to_string(),
                client: stored.client.clone(),
            };
            // A failed write keeps the token that was just used; the next cold
            // refresh then tries the old refresh token and reports rejection.
            if let Some(call) = write_item_call(key, encode_stored(&updated)) {
                let _ = io.credential(call);
            }
        }
    }
    Ok(access_token)
}

// ── capture, fetch, remove ──

/// Fixed capture/remove outcomes. `code()` is the whole FFI error text, the
/// same vocabulary as macOS (hence `keychain_*` for Credential Manager).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum CaptureError {
    AgyNotSignedIn,
    AgyLoginUnreadable,
    AgyLoginMissingIdentity,
    OAuthClientNotFound,
    OAuthClientRejected,
    RefreshRejected,
    RefreshUnreachable,
    AccountMismatch,
    InvalidCredentialFormat,
    KeychainWriteFailed,
    InvalidKey,
    KeychainDeleteFailed,
    /// Automatic capture only: agy's credential read failed other than "not
    /// found".
    Paused,
    /// Automatic capture only: agy has no credential.
    NotSignedIn,
}

impl CaptureError {
    pub(crate) fn code(self) -> &'static str {
        match self {
            Self::AgyNotSignedIn => "agy_not_signed_in",
            Self::AgyLoginUnreadable => "agy_login_unreadable",
            Self::AgyLoginMissingIdentity => "agy_login_missing_identity",
            Self::OAuthClientNotFound => "oauth_client_not_found",
            Self::OAuthClientRejected => "oauth_client_rejected",
            Self::RefreshRejected => "refresh_rejected",
            Self::RefreshUnreachable => "refresh_unreachable",
            Self::AccountMismatch => "account_mismatch",
            Self::InvalidCredentialFormat => "invalid_credential_format",
            Self::KeychainWriteFailed => "keychain_write_failed",
            Self::InvalidKey => "invalid_key",
            Self::KeychainDeleteFailed => "keychain_delete_failed",
            Self::Paused => "paused",
            Self::NotSignedIn => "not_signed_in",
        }
    }
}

/// Copy agy's current login into a Syrtis-owned credential and return its key
/// and label. Nothing is written unless the token refreshed with its own
/// issuing client and any `id_token` in that response names the same `sub`.
async fn capture_with<I: CapturedIo>(io: &I) -> Result<CapturedAccount, CaptureError> {
    let CredOutcome::Ok(agy_blob) = io.credential(agy_read_call()) else {
        return Err(CaptureError::AgyNotSignedIn);
    };
    let login = parse_agy_login(&agy_blob)?;
    drop(agy_blob);

    let (client, json) = refresh_with_issuing_client(io, &login).await?;

    // The stored id_token is local and untrusted; Google's answer is not.
    if let Some(id_token) = json.get("id_token") {
        if response_sub(Some(id_token)).as_deref() != Some(login.sub.as_str()) {
            return Err(CaptureError::AccountMismatch);
        }
    }

    let key = captured_key(&login.sub);
    write_captured(io, &key, &login, client, &json)?;
    Ok(CapturedAccount {
        key,
        label: login
            .email
            .unwrap_or_else(|| CAPTURED_FALLBACK_LABEL.to_string()),
    })
}

/// One refresh of `login`'s token with the client named by its `aud`,
/// trying that client's candidate secrets; `invalid_client` /
/// `unauthorized_client` falls through to the next, any other answer stops.
async fn refresh_with_issuing_client<I: CapturedIo>(
    io: &I,
    login: &AgyLogin,
) -> Result<(OAuthClient, Value), CaptureError> {
    let clients = clients_for_aud(&login.aud, io.client_artifacts().await);
    if clients.is_empty() {
        return Err(CaptureError::OAuthClientNotFound);
    }
    for client in clients {
        let (status, body) = io
            .token_post(&client, &login.refresh_token, None)
            .await
            .map_err(|_| CaptureError::RefreshUnreachable)?;
        match token_response(status, &body) {
            Ok(json) => return Ok((client, json)),
            Err(TokenRejection::WrongClient) => continue,
            Err(TokenRejection::Other) => return Err(CaptureError::RefreshRejected),
        }
    }
    Err(CaptureError::OAuthClientRejected)
}

/// The `sub` of a token response's `id_token`, when it is a decodable JWT.
fn response_sub(id_token: Option<&Value>) -> Option<String> {
    id_token
        .and_then(Value::as_str)
        .and_then(jwt_claims)
        .and_then(|claims| non_empty_str(&claims, "sub").map(str::to_string))
}

/// Write `{refresh_token, client}` to `key`'s credential. The refresh token is
/// the response's when Google rotated it, otherwise the login's.
fn write_captured<I: CapturedIo>(
    io: &I,
    key: &str,
    login: &AgyLogin,
    client: OAuthClient,
    json: &Value,
) -> Result<(), CaptureError> {
    let credential = StoredCredential {
        refresh_token: non_empty_str(json, "refresh_token")
            .unwrap_or(&login.refresh_token)
            .to_string(),
        client,
    };
    let call = write_item_call(key, encode_stored(&credential))
        .ok_or(CaptureError::InvalidCredentialFormat)?;
    match io.credential(call) {
        CredOutcome::Ok(_) => Ok(()),
        CredOutcome::NotFound | CredOutcome::Failed => Err(CaptureError::KeychainWriteFailed),
    }
}

/// What one automatic capture did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum AutoCaptured {
    /// The login was verified at Google and written to its own credential.
    Captured(CapturedAccount),
    /// Its own credential already holds this refresh token: no scan, no
    /// request, no write.
    Unchanged(CapturedAccount),
    /// The user removed this account; nothing was requested or written.
    SkippedRemoved,
}

/// The automatic counterpart of `capture_with`, run once per agy login change
/// while automatic capture is on. Stricter than the manual path, because
/// nobody pressed a button:
/// 1. agy's credential: found continues, not found is `NotSignedIn`, any
///    other read failure is `Paused`, which stops further automatic attempts
///    until the user acts;
/// 2. a key in `removed_keys` is skipped before any request;
/// 3. a refresh token its own credential already holds is `Unchanged`, with
///    no client scan, no request and no write;
/// 4. the refresh response MUST carry an `id_token` whose `sub` equals the
///    stored one, else `AccountMismatch` and no write.
async fn auto_capture_with<I: CapturedIo>(
    io: &I,
    removed_keys: &[String],
) -> Result<AutoCaptured, CaptureError> {
    let agy_blob = match io.credential(agy_read_call()) {
        CredOutcome::Ok(blob) => blob,
        CredOutcome::NotFound => return Err(CaptureError::NotSignedIn),
        CredOutcome::Failed => return Err(CaptureError::Paused),
    };
    let login = parse_agy_login(&agy_blob)?;
    drop(agy_blob);

    let key = captured_key(&login.sub);
    if removed_keys.contains(&key) {
        return Ok(AutoCaptured::SkippedRemoved);
    }

    let stored_label = login.email.clone();
    let own = read_item_call(&key).ok_or(CaptureError::InvalidCredentialFormat)?;
    let unchanged = match io.credential(own) {
        CredOutcome::Ok(blob) => {
            decode_stored(&blob).is_some_and(|stored| stored.refresh_token == login.refresh_token)
        }
        CredOutcome::NotFound | CredOutcome::Failed => false,
    };
    if unchanged {
        return Ok(AutoCaptured::Unchanged(CapturedAccount {
            key,
            label: stored_label.unwrap_or_else(|| CAPTURED_FALLBACK_LABEL.to_string()),
        }));
    }

    let (client, json) = refresh_with_issuing_client(io, &login).await?;
    let claims = json
        .get("id_token")
        .and_then(Value::as_str)
        .and_then(jwt_claims);
    if claims
        .as_ref()
        .and_then(|claims| non_empty_str(claims, "sub"))
        != Some(login.sub.as_str())
    {
        return Err(CaptureError::AccountMismatch);
    }
    let label = claims
        .as_ref()
        .and_then(|claims| non_empty_str(claims, "email"))
        .map(str::to_string)
        .or(stored_label)
        .unwrap_or_else(|| CAPTURED_FALLBACK_LABEL.to_string());

    write_captured(io, &key, &login, client, &json)?;
    Ok(AutoCaptured::Captured(CapturedAccount { key, label }))
}

/// The one place agy's `LastWritten` becomes a login marker: the FILETIME as a
/// decimal string, `"absent"` when agy has no credential, `None` when it could
/// not be read (the caller does nothing that poll). `tb_antigravity_login_marker`
/// returns it, and the agy snapshot's marker (W7b) must come from here too.
pub(crate) fn agy_login_marker(
    last_written: Result<Option<u64>, CredentialUnreadable>,
) -> Option<String> {
    match last_written {
        Ok(Some(filetime)) => Some(filetime.to_string()),
        Ok(None) => Some("absent".to_string()),
        Err(CredentialUnreadable) => None,
    }
}

fn login_marker_with<I: CapturedIo>(io: &I) -> Option<String> {
    agy_login_marker(io.agy_last_written())
}

/// Delete one captured credential and its cached access token. Never revokes:
/// the refresh token stays valid at Google until the user revokes it there.
fn remove_with<I: CapturedIo>(
    io: &I,
    cache: &CapturedTokenCache,
    key: &str,
) -> Result<(), CaptureError> {
    let call = delete_item_call(key).ok_or(CaptureError::InvalidKey)?;
    lock_tokens(cache).remove(key);
    match io.credential(call) {
        CredOutcome::Ok(_) | CredOutcome::NotFound => Ok(()),
        CredOutcome::Failed => Err(CaptureError::KeychainDeleteFailed),
    }
}

/// One captured account's quota. Every failure is a per-account
/// `ProviderFetchFailure`; none is provider-wide.
pub(crate) async fn fetch_captured_with<I: CapturedIo>(
    io: &I,
    cache: &CapturedTokenCache,
    key: &str,
    label: &str,
    now: DateTime<Utc>,
) -> Result<Fetched, ProviderFetchFailure> {
    if !valid_captured_key(key) {
        return Err(ProviderFetchFailure::terminal(CAPTURED_ITEM_UNREADABLE));
    }
    let (account_scope, history_scope) = io.scopes(key);
    let account_scope =
        account_scope.map_err(|_| ProviderFetchFailure::terminal(CAPTURED_IDENTITY_UNVERIFIED))?;
    let binding = ProviderCacheBinding::primary(account_scope.clone());
    let access_token = captured_access_token(io, cache, key, &binding, now).await?;
    let mut fetched = match io
        .quota(access_token, account_scope, history_scope, now)
        .await
    {
        Ok(fetched) => fetched,
        Err(failure) => {
            // A rejected token is not reused for the rest of its lifetime.
            if matches!(failure, ProviderFetchFailure::Terminal { .. }) {
                lock_tokens(cache).remove(key);
            }
            // The quota calls are shared with the primary, whose 401 text
            // tells the user to sign in to Antigravity again. That is wrong
            // advice here: Antigravity is signed in to a different account.
            return Err(match failure {
                ProviderFetchFailure::Terminal { ref display }
                    if display == ANTIGRAVITY_AUTH_EXPIRED =>
                {
                    ProviderFetchFailure::terminal(CAPTURED_AUTH_EXPIRED)
                }
                failure => failure,
            });
        }
    };
    fetched.identity = Some(AgentIdentity {
        email: Some(label.to_string()),
        plan: fetched.identity.and_then(|identity| identity.plan),
    });
    Ok(fetched)
}

pub(crate) async fn capture() -> Result<CapturedAccount, CaptureError> {
    capture_with(&SystemCapturedIo).await
}

pub(crate) async fn auto_capture(removed_keys: &[String]) -> Result<AutoCaptured, CaptureError> {
    auto_capture_with(&SystemCapturedIo, removed_keys).await
}

pub(crate) fn login_marker() -> Option<String> {
    login_marker_with(&SystemCapturedIo)
}

pub(crate) fn remove(key: &str) -> Result<(), CaptureError> {
    remove_with(&SystemCapturedIo, &CAPTURED_TOKENS, key)
}

pub(crate) async fn fetch_captured(
    key: &str,
    label: &str,
    now: DateTime<Utc>,
) -> Result<Fetched, ProviderFetchFailure> {
    fetch_captured_with(&SystemCapturedIo, &CAPTURED_TOKENS, key, label, now).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;

    /// #204: the agy run Syrtis starts must disable agy's auto-update, with
    /// the value agy actually honours (`true`; `1` was measured not to work).
    /// (`CREATE_NO_WINDOW` is not readable back from a `Command`.)
    #[cfg(windows)]
    #[test]
    fn the_agy_run_disables_agys_auto_update() {
        let command = agy_command(
            Path::new(r"C:\agy\bin\agy.exe"),
            Path::new(r"C:\agy\bin"),
        );
        let envs: Vec<_> = command.as_std().get_envs().collect();
        assert!(
            envs.contains(&(
                std::ffi::OsStr::new("AGY_CLI_DISABLE_AUTO_UPDATE"),
                Some(std::ffi::OsStr::new("true"))
            )),
            "agy would start its updater (and a Terminal window): {envs:?}"
        );
        let args: Vec<_> = command.as_std().get_args().collect();
        assert_eq!(args, ["--print", "/usage", "--output-format", "json", "--print-timeout", "30s"]);
    }

    /// An **absent** credential file must report the marker verbatim.
    ///
    /// Absent, not unreadable — the two are now different verdicts.
    /// `RemoteCredentialError::Unreadable` deliberately does NOT reach this
    /// marker, and `malformed_remote_credentials_are_unreadable_not_absent`
    /// below is the assertion that keeps it out.
    #[test]
    fn absent_remote_credentials_report_the_unconfigured_marker() {
        let missing = std::env::temp_dir()
            .join("tokenbar-antigravity-unconfigured-probe")
            .join("oauth_creds.json");
        assert!(!missing.exists(), "the probe path must not exist");
        let failure = remote_credentials_or_unconfigured(&missing).unwrap_err();
        assert!(
            matches!(
                failure,
                ProviderFetchFailure::Terminal { ref display }
                    if display == ANTIGRAVITY_UNCONFIGURED_ERROR
            ),
            "absent credentials must carry the unconfigured marker, got {failure:?}"
        );
    }

    /// A credential that exists but cannot be parsed belongs to a configured
    /// account, so it must NOT reach the absence marker. Before the split it
    /// did: every `load_remote_credentials` failure became
    /// `ANTIGRAVITY_UNCONFIGURED_ERROR`, so a corrupt `oauth_creds.json` read
    /// as "never set up".
    #[test]
    fn malformed_remote_credentials_are_unreadable_not_absent() {
        let dir = std::env::temp_dir().join("tokenbar-antigravity-malformed-probe");
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("oauth_creds.json");
        std::fs::write(&path, b"{ this is not json").unwrap();

        assert_eq!(
            load_remote_credentials(&path),
            Err(RemoteCredentialError::Unreadable)
        );
        let failure = remote_credentials_or_unconfigured(&path).unwrap_err();
        assert!(
            matches!(
                failure,
                ProviderFetchFailure::Terminal { ref display }
                    if display == ANTIGRAVITY_UNREADABLE_ERROR
            ),
            "a corrupt credential must not read as an absent one, got {failure:?}"
        );
        std::fs::remove_file(&path).unwrap();
        assert_eq!(
            load_remote_credentials(&path),
            Err(RemoteCredentialError::Absent),
            "control: the same path with the file gone IS absent, so the case above \
             is the parse and not the path"
        );
    }

    #[test]
    fn gemini_home_uses_nonempty_configured_root_unchanged() {
        let configured = " /tmp/gemini-cli-home ";
        assert_eq!(
            gemini_home_from(Ok(configured.to_string()), None),
            Some(PathBuf::from(configured))
        );
    }

    #[test]
    fn gemini_home_falls_back_on_environment_errors() {
        let home = PathBuf::from("resolved-home");
        let fallback = Some(PathBuf::from("resolved-home/.gemini"));
        for error in [
            std::env::VarError::NotPresent,
            std::env::VarError::NotUnicode(std::ffi::OsString::new()),
        ] {
            assert_eq!(gemini_home_from(Err(error), Some(&home)), fallback);
        }
    }

    #[test]
    fn gemini_home_falls_back_for_trim_empty_root() {
        let home = PathBuf::from("resolved-home");
        assert_eq!(
            gemini_home_from(Ok(" \t\n ".to_string()), Some(&home)),
            Some(PathBuf::from("resolved-home/.gemini"))
        );
    }

    #[test]
    fn extracts_flags_both_forms() {
        let cmd = "/x/language_server --app_data_dir /Users/me/.gemini/antigravity --csrf_token=ABC123 --extension_server_port 4567";
        assert_eq!(extract_flag(cmd, "--csrf_token").as_deref(), Some("ABC123"));
        assert_eq!(
            extract_flag(cmd, "--extension_server_port").as_deref(),
            Some("4567")
        );
        assert!(is_language_server(&cmd.to_lowercase()));
        assert!(is_antigravity(&cmd.to_lowercase()));
    }

    #[test]
    fn parses_lsof_listen_port() {
        let line = "language_ 123 nanako 30u IPv4 0x0 0t0 TCP 127.0.0.1:54321 (LISTEN)";
        assert_eq!(parse_listen_port(line), Some(54321));
        assert_eq!(parse_listen_port("... (ESTABLISHED)"), None);
    }

    fn windows_process_fixture(pid: i32, command: &str, ports: &str) -> String {
        let encoded = base64::engine::general_purpose::STANDARD.encode(command);
        format!("{WINDOWS_DISCOVERY_PREFIX}\tP\t{pid}\t{encoded}\t{ports}")
    }

    fn windows_discovery_error(stdout: &str) -> WindowsDiscoveryError {
        match parse_windows_discovery(stdout) {
            Ok(_) => panic!("Windows discovery fixture unexpectedly succeeded"),
            Err(error) => error,
        }
    }

    #[test]
    fn windows_discovery_queries_language_server_executable_family() {
        assert!(WINDOWS_DISCOVERY_SCRIPT.contains(
            r#"Get-CimInstance -ClassName Win32_Process -Filter "Name LIKE 'language_server%.exe'""#
        ));
    }

    #[test]
    fn windows_discovery_preserves_per_process_candidate_binding() {
        let first_csrf = "first-primary-secret";
        let first_extension_csrf = "first-extension-secret";
        let second_csrf = "second-primary-secret";
        let first = windows_process_fixture(
            1001,
            &format!(
                r#""C:\Program Files\Antigravity\language_server_windows_x64.exe" --app_data_dir="C:\Users\me\AppData\Roaming\Antigravity" --csrf_token={first_csrf} --extension_server_port=41999 --extension_server_csrf_token={first_extension_csrf}"#
            ),
            "41002,41001,41002",
        );
        let ignored = windows_process_fixture(
            1009,
            r#"C:\Other\language_server.exe --csrf_token=decoy-secret"#,
            "49999",
        );
        let second = windows_process_fixture(
            1002,
            &format!(
                r#"C:\Antigravity\language_server.exe --app_data_dir antigravity --csrf_token={second_csrf}"#
            ),
            "42000",
        );
        let fixture = format!(
            "garbage\n{first}\n{ignored}\n{WINDOWS_DISCOVERY_PREFIX}\tP\tnot-a-pid\tnot-base64\t80\n{second}\r\n"
        );

        let processes = match parse_windows_discovery(&fixture) {
            Ok(processes) => processes,
            Err(_) => panic!("valid Windows discovery fixture was rejected"),
        };
        assert_eq!(processes.len(), 2);
        assert_eq!(processes[0].0.pid, 1001);
        assert_eq!(processes[0].1.as_slice(), &[41001, 41002]);
        assert_eq!(processes[1].0.pid, 1002);
        assert_eq!(processes[1].1.as_slice(), &[42000]);

        assert_eq!(
            local_api_candidates(processes),
            vec![
                (41001, first_csrf.to_string()),
                (41002, first_csrf.to_string()),
                (41999, first_extension_csrf.to_string()),
                (41999, first_csrf.to_string()),
                (42000, second_csrf.to_string()),
            ]
        );
    }

    #[test]
    fn windows_discovery_rejects_malformed_rows_without_exposing_secrets() {
        let sentinel = "sentinel-secret-that-must-not-leak";
        let command = format!(
            r#"C:\Antigravity\language_server.exe --app_data_dir antigravity --csrf_token={sentinel}"#
        );
        let encoded = base64::engine::general_purpose::STANDARD.encode(&command);
        for malformed in [
            "garbage".to_string(),
            format!("{WINDOWS_DISCOVERY_PREFIX}\tP\tnot-a-pid\t{encoded}\t54321"),
            format!("{WINDOWS_DISCOVERY_PREFIX}\tP\t4242\tnot-base64\t54321"),
            windows_process_fixture(4242, &command, "not-a-port"),
            windows_process_fixture(4242, &command, "0"),
            windows_process_fixture(4242, &command, "65536"),
        ] {
            assert_eq!(
                windows_discovery_error(&malformed),
                WindowsDiscoveryError::MalformedOutput
            );
        }

        assert_eq!(
            windows_discovery_error(&format!("{WINDOWS_DISCOVERY_PREFIX}\tN\n")),
            WindowsDiscoveryError::ProcessNotFound
        );
        assert_eq!(
            windows_discovery_error(&windows_process_fixture(
                4242,
                r#"C:\Other\language_server.exe --csrf_token=decoy-secret"#,
                "54321",
            )),
            WindowsDiscoveryError::ProcessNotFound
        );
        assert_eq!(
            windows_discovery_error(&windows_process_fixture(
                4242,
                r#"C:\Antigravity\language_server.exe --app_data_dir antigravity"#,
                "54321",
            )),
            WindowsDiscoveryError::TokenMissing
        );

        let error = windows_discovery_error(&windows_process_fixture(4242, &command, ""));
        assert_eq!(error, WindowsDiscoveryError::PortsMissing);
        let display = error.message();
        let debug = format!("{error:?}");
        assert!(!display.contains(sentinel));
        assert!(!debug.contains(sentinel));
        assert!(!WindowsDiscoveryError::PowerShellFailed
            .message()
            .contains(sentinel));
    }

    #[test]
    fn scans_and_pairs_oauth_client_from_bytes() {
        let blob = b"junk\x00123-abcDEF_g.apps.googleusercontent.com\x00\x00GOCSPX-abcdefghijklmnopqrstuvwxyz12\x00tail";
        let ids = scan_client_ids(blob);
        let secrets = scan_client_secrets(blob);
        assert_eq!(
            ids,
            vec!["123-abcDEF_g.apps.googleusercontent.com".to_string()]
        );
        assert_eq!(secrets.len(), 1);
        let client = preferred_client(&ids, &secrets).unwrap();
        assert_eq!(client.0, "123-abcDEF_g.apps.googleusercontent.com");
        assert!(client.1.starts_with("GOCSPX-"));
    }

    #[test]
    fn windows_client_candidates_cover_the_per_user_and_machine_installs() {
        let (local, program_files) = if cfg!(windows) {
            (r"C:\Users\u\AppData\Local", r"C:\Program Files")
        } else {
            ("/users/u/local", "/program files")
        };
        let candidates = windows_client_artifact_candidates(
            Some(std::ffi::OsStr::new(local)),
            Some(std::ffi::OsStr::new(program_files)),
        );
        assert_eq!(
            candidates,
            vec![
                Path::new(local)
                    .join("Programs")
                    .join("Antigravity")
                    .join("resources/bin/language_server.exe"),
                Path::new(program_files)
                    .join("Antigravity")
                    .join("resources/bin/language_server.exe"),
            ]
        );
    }

    #[test]
    fn windows_client_candidates_skip_missing_and_relative_roots() {
        assert!(windows_client_artifact_candidates(None, None).is_empty());
        assert!(windows_client_artifact_candidates(
            Some(std::ffi::OsStr::new("relative")),
            Some(std::ffi::OsStr::new("")),
        )
        .is_empty());
    }

    /// Manual check on a Windows host with the Antigravity IDE installed:
    /// `cargo test -p tb_core_ffi real_antigravity_client_discovery -- --ignored --nocapture`.
    /// Prints only whether a client pair was found and how many ids and
    /// secrets the scan saw — never the values.
    #[test]
    #[ignore]
    fn real_antigravity_client_discovery() {
        for path in client_artifact_candidates() {
            match std::fs::read(&path) {
                Ok(data) => println!(
                    "candidate {}: ids={} secrets={} pair={}",
                    path.display(),
                    scan_client_ids(&data).len(),
                    scan_client_secrets(&data).len(),
                    preferred_client(&scan_client_ids(&data), &scan_client_secrets(&data))
                        .is_some()
                ),
                Err(_) => println!("candidate {}: not readable", path.display()),
            }
        }
        println!("discovered={}", discover_client_from_app().is_some());
    }

    #[test]
    fn scans_client_id_glued_to_a_neighbouring_string() {
        // The fixture above separates the id with NUL, which is not a token byte,
        // so the walk-back stops on its own. A real language_server packs strings
        // with no separator: the neighbour's tail is token bytes and gets absorbed
        // into the head, which must be all digits. Observed on Antigravity 1.x —
        // both ids in the shipped binary were rejected this way, which took the
        // whole OAuth route down whenever the IDE was not running.
        let blob = b"someNeighbourKey123-abcDEF_g.apps.googleusercontent.com\x00tail";
        assert_eq!(
            scan_client_ids(blob),
            vec!["123-abcDEF_g.apps.googleusercontent.com".to_string()]
        );

        // A neighbour whose own tail is `…<letters><digits>-` puts a second hyphen
        // in the segment. The id's delimiter is the last one (its token carries
        // none), so anchoring there recovers the real id; anchoring on the first
        // hyphen would keep the neighbour's `123-beta` and `valid_client_id` — which
        // only checks the digits before the first hyphen — would accept it.
        let two_hyphens = b"label123-beta456-real.apps.googleusercontent.com\x00tail";
        assert_eq!(
            scan_client_ids(two_hyphens),
            vec!["456-real.apps.googleusercontent.com".to_string()]
        );

        // ponytail: a neighbour whose tail is digits glued straight onto the id's
        // project number (no intervening hyphen) is indistinguishable from the head,
        // so the longest digit run wins and those digits are kept. Nothing in the
        // byte stream marks that boundary; the only stronger fix would be reading
        // Mach-O string sections instead of scanning bytes, which is a lot of
        // machinery for a case Google's 12-digit ids make rare.
        let glued_digits = b"prefix99000123-abcDEF_g.apps.googleusercontent.com\x00tail";
        assert_eq!(
            scan_client_ids(glued_digits),
            vec!["99000123-abcDEF_g.apps.googleusercontent.com".to_string()]
        );
    }

    #[test]
    fn prefers_last_id_when_single_secret() {
        let ids = vec![
            "1-a.apps.googleusercontent.com".into(),
            "2-b.apps.googleusercontent.com".into(),
        ];
        let secrets = vec!["GOCSPX-only".into()];
        assert_eq!(
            preferred_client(&ids, &secrets).unwrap().0,
            "2-b.apps.googleusercontent.com"
        );
    }

    #[test]
    fn parses_local_user_status_quotas() {
        let now = Utc::now();
        let body = r#"{
            "userStatus": {
                "email": "me@gmail.com",
                "userTier": { "name": "Pro" },
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        { "label": "Gemini 3 Pro", "modelOrAlias": {"model":"gemini-3-pro"},
                          "quotaInfo": { "remainingFraction": 0.42, "resetTime": "2026-06-09T00:00:00Z" } },
                        { "label": "No Quota", "modelOrAlias": {"model":"x"} }
                    ]
                }
            }
        }"#;
        let fetched = parse_user_status(body, now).unwrap();
        assert_eq!(fetched.source, "cli");
        assert_eq!(
            fetched.identity.as_ref().unwrap().email.as_deref(),
            Some("me@gmail.com")
        );
        assert_eq!(
            fetched.identity.as_ref().unwrap().plan.as_deref(),
            Some("Pro")
        );
        assert_eq!(fetched.windows.len(), 1);
        assert_eq!(fetched.windows[0].label_for_test(), "Gemini 3 Pro");
        assert!((fetched.windows[0].remaining_for_test() - 42.0).abs() < 0.01);
    }

    #[test]
    fn maps_available_models_and_quota_buckets() {
        let now = Utc::now();
        let models = json!({
            "models": {
                "gemini-3-pro": { "displayName": "Gemini 3 Pro", "quotaInfo": { "remainingFraction": 0.5 } }
            }
        });
        let w = models_from_available(&models.to_string(), now).unwrap();
        assert_eq!(w.len(), 1);

        let quota = json!({
            "buckets": [
                { "modelId": "claude", "remainingFraction": 0.8 },
                { "modelId": "claude", "remainingFraction": 0.3 }
            ]
        });
        let b = buckets_from_quota(&quota.to_string(), now).unwrap();
        assert_eq!(b.len(), 1);
        assert!((b[0].remaining_for_test() - 30.0).abs() < 0.01); // lowest kept
    }

    #[test]
    fn stage4_antigravity_identity_and_duplicate_rules_are_deterministic() {
        let now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let models = json!({
            "models": {
                "model-A": {
                    "displayName": "Trimmed loser",
                    "quotaInfo": { "remainingFraction": 0.5, "resetTime": "2026-07-12T00:00:00Z" }
                },
                "  model-A  ": {
                    "displayName": "Trimmed winner",
                    "quotaInfo": { "remainingFraction": 0.2, "resetTime": "2026-07-13T00:00:00Z" }
                },
                "model-B": {
                    "displayName": "Shared label",
                    "quotaInfo": { "remainingFraction": 0.4, "resetTime": "2026-07-12T00:00:00Z" }
                },
                "Model-Byte-Case": {
                    "displayName": "Shared label",
                    "quotaInfo": { "remainingFraction": 0.3, "resetTime": "2026-07-13T00:00:00Z" }
                }
            }
        });
        let windows = models_from_available(&models.to_string(), now).unwrap();
        assert_eq!(windows.len(), 3);
        let model_a = windows
            .iter()
            .find(|window| window.pace_window_key_for_test() == Some("model.model-A.v1"))
            .unwrap();
        assert_eq!(model_a.label_for_test(), "Trimmed winner");
        assert!((model_a.remaining_for_test() - 20.0).abs() < 0.01);
        assert_eq!(
            windows
                .iter()
                .filter(|window| window.label_for_test() == "Shared label")
                .count(),
            2,
            "display labels never merge distinct model IDs"
        );
        assert!(windows.iter().any(|window| {
            window.pace_window_key_for_test() == Some("model.Model-Byte-Case.v1")
        }));

        let cli = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "label": "CLI loser",
                            "modelOrAlias": { "model": " Model-X " },
                            "quotaInfo": { "remainingFraction": 0.6, "resetTime": "2026-07-12T00:00:00Z" }
                        },
                        {
                            "label": "CLI winner",
                            "modelOrAlias": { "model": "Model-X" },
                            "quotaInfo": { "remainingFraction": 0.2, "resetTime": "2026-07-13T00:00:00Z" }
                        },
                        { "label": "Config only", "quotaInfo": { "remainingFraction": 0.7 } }
                    ]
                }
            }
        }"#;
        let fetched = parse_user_status(cli, now).unwrap();
        assert_eq!(fetched.windows.len(), 2);
        assert_eq!(
            fetched.windows[0].pace_window_key_for_test(),
            Some("model.Model-X.v1")
        );
        assert_eq!(fetched.windows[0].label_for_test(), "CLI winner");
        let missing_wire = serde_json::to_value(&fetched.windows[1]).unwrap();
        assert_eq!(missing_wire["cardId"], "row.cli.config.2.v1");
        assert_eq!(missing_wire["paceStatus"]["reason"], "windowIdentity");

        let missing_remote = models_from_available(
            &json!({
                "models": {
                    "   ": {
                        "displayName": "Remote model",
                        "quotaInfo": { "remainingFraction": 0.7 }
                    }
                }
            })
            .to_string(),
            now,
        )
        .unwrap();
        let wire = serde_json::to_value(&missing_remote[0]).unwrap();
        assert_eq!(wire["cardId"], "row.models.0.v1");
        assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");

        let missing_bucket = buckets_from_quota(
            &json!({
                "buckets": [
                    { "modelId": "   ", "remainingFraction": 0.7 }
                ]
            })
            .to_string(),
            now,
        )
        .unwrap();
        let wire = serde_json::to_value(&missing_bucket[0]).unwrap();
        assert_eq!(wire["cardId"], "row.quota.bucket.0.v1");
        assert_eq!(wire["paceStatus"]["reason"], "windowIdentity");

        let duplicate = json!({
            "buckets": [
                {
                    "modelId": "same-model",
                    "remainingFraction": 0.25,
                    "resetTime": "2026-07-09T00:00:00Z"
                },
                {
                    "modelId": "same-model",
                    "remainingFraction": 0.25,
                    "resetTime": "2026-07-12T00:00:00Z"
                },
                {
                    "modelId": "same-model",
                    "remainingFraction": 0.25,
                    "resetTime": "2026-07-11T00:00:00Z"
                }
            ]
        });
        let selected = buckets_from_quota(&duplicate.to_string(), now).unwrap();
        assert_eq!(selected.len(), 1);
        assert_eq!(
            selected[0].resets_at_for_test(),
            Some("2026-07-11T00:00:00.000Z"),
            "future reset beats past reset, then earliest future reset wins"
        );
        let same_reset = parse_datetime("2026-07-11T00:00:00Z");
        assert!(!binding_candidate_is_better(
            0.25, same_reset, 1, 0.25, same_reset, 0, now
        ));
    }

    #[test]
    fn stage4_antigravity_rejects_invalid_fractions_at_every_source() {
        assert!(!valid_remaining_fraction(f64::NAN));
        assert!(!valid_remaining_fraction(f64::INFINITY));
        assert!(!valid_remaining_fraction(-0.01));
        assert!(!valid_remaining_fraction(1.01));
        assert!(valid_remaining_fraction(0.0));
        assert!(valid_remaining_fraction(1.0));

        let now = DateTime::parse_from_rfc3339("2026-07-10T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let assert_rows = |windows: Vec<UsageWindow>| {
            assert_eq!(windows.len(), 1);
            assert_eq!(
                windows[0].pace_window_key_for_test(),
                Some("model.valid.v1")
            );
            for key in ["model.negative.v1", "model.over.v1"] {
                assert!(
                    windows
                        .iter()
                        .all(|window| window.pace_window_key_for_test() != Some(key)),
                    "invalid quota row must not be published: {key}"
                );
            }
        };

        let cli = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "modelOrAlias": { "model": "valid" },
                            "quotaInfo": { "remainingFraction": 0.5 }
                        },
                        {
                            "modelOrAlias": { "model": "negative" },
                            "quotaInfo": { "remainingFraction": -0.1 }
                        },
                        {
                            "modelOrAlias": { "model": "over" },
                            "quotaInfo": { "remainingFraction": 1.1 }
                        },
                        {
                            "modelOrAlias": { "model": "missing" },
                            "quotaInfo": {}
                        }
                    ]
                }
            }
        }"#;
        assert_rows(parse_user_status(cli, now).unwrap().windows);

        let assert_overflowing_duplicate = |windows: Vec<UsageWindow>| {
            assert_eq!(windows.len(), 1);
            assert_eq!(
                windows[0].pace_window_key_for_test(),
                Some("model.same-model.v1")
            );
            assert!((windows[0].remaining_for_test() - 50.0).abs() < 0.01);
        };
        let overflowing_cli = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 1e400 }
                        },
                        {
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 0.5 }
                        }
                    ]
                }
            }
        }"#;
        assert_overflowing_duplicate(parse_user_status(overflowing_cli, now).unwrap().windows);

        assert_rows(
            models_from_available(
                &json!({
                    "models": {
                        "valid": { "quotaInfo": { "remainingFraction": 0.5 } },
                        "negative": { "quotaInfo": { "remainingFraction": -0.1 } },
                        "over": { "quotaInfo": { "remainingFraction": 1.1 } },
                        "missing": { "quotaInfo": {} }
                    }
                })
                .to_string(),
                now,
            )
            .unwrap(),
        );

        let overflowing_models = r#"{
            "models": {
                "same-model": {
                    "quotaInfo": { "remainingFraction": 1e400 }
                },
                " same-model ": {
                    "quotaInfo": { "remainingFraction": 0.5 }
                }
            }
        }"#;
        assert_overflowing_duplicate(models_from_available(overflowing_models, now).unwrap());

        assert_rows(
            buckets_from_quota(
                &json!({
                    "buckets": [
                        { "modelId": "valid", "remainingFraction": 0.5 },
                        { "modelId": "negative", "remainingFraction": -0.1 },
                        { "modelId": "over", "remainingFraction": 1.1 },
                        { "modelId": "missing" }
                    ]
                })
                .to_string(),
                now,
            )
            .unwrap(),
        );

        let overflowing_buckets = r#"{
            "buckets": [
                { "modelId": "same-model", "remainingFraction": 1e400 },
                { "modelId": "same-model", "remainingFraction": 0.5 }
            ]
        }"#;
        assert_overflowing_duplicate(buckets_from_quota(overflowing_buckets, now).unwrap());

        let malformed_cli_row = r#"{
            "userStatus": {
                "cascadeModelConfigData": {
                    "clientModelConfigs": [
                        {
                            "label": 1e400,
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 0.4 }
                        },
                        {
                            "modelOrAlias": { "model": "same-model" },
                            "quotaInfo": { "remainingFraction": 0.5 }
                        }
                    ]
                }
            }
        }"#;
        assert_overflowing_duplicate(parse_user_status(malformed_cli_row, now).unwrap().windows);

        let malformed_model_row = r#"{
            "models": {
                "same-model": {
                    "displayName": 1e400,
                    "quotaInfo": { "remainingFraction": 0.4 }
                },
                " same-model ": {
                    "quotaInfo": { "remainingFraction": 0.5 }
                }
            }
        }"#;
        assert_overflowing_duplicate(models_from_available(malformed_model_row, now).unwrap());

        let malformed_bucket_fields = r#"{
            "buckets": [
                { "modelId": 42, "remainingFraction": 0.4 },
                { "modelId": "valid", "remainingFraction": 0.5 }
            ]
        }"#;
        let malformed_bucket_windows = buckets_from_quota(malformed_bucket_fields, now).unwrap();
        assert_eq!(malformed_bucket_windows.len(), 2);
        assert!(malformed_bucket_windows
            .iter()
            .any(|window| window.pace_window_key_for_test() == Some("model.valid.v1")));
        let malformed_bucket_wire = serde_json::to_value(&malformed_bucket_windows[0]).unwrap();
        assert_eq!(
            malformed_bucket_wire["paceStatus"]["reason"],
            "windowIdentity"
        );

        assert!(quota_window(
            "Non-finite".to_string(),
            f64::NAN,
            None,
            now,
            "model.non-finite.v1".to_string(),
            Some("model.non-finite.v1".to_string()),
        )
        .is_none());
        assert!(!binding_candidate_is_better(
            -0.1, None, 1, 0.5, None, 0, now
        ));
    }

    #[test]
    fn remote_scope_and_presentation_ignore_unbound_active_email() {
        let stale_active_email = "stale-other-account@example.com";
        let credentials = json!({
            "access_token": "short-lived-access",
            "refresh_token": "bound-google-refresh"
        });
        assert_eq!(
            remote_refresh_marker(&credentials),
            Some(b"bound-google-refresh".as_slice())
        );
        assert_ne!(
            remote_refresh_marker(&credentials),
            Some(stale_active_email.as_bytes())
        );
        let identity = remote_identity(Some("Paid".to_string()));
        assert_eq!(identity.email, None);
        assert_eq!(identity.plan.as_deref(), Some("Paid"));

        let access_only = json!({ "access_token": "access-is-not-the-frozen-marker" });
        assert_eq!(remote_refresh_marker(&access_only), None);
    }

    #[test]
    fn local_route_fails_closed_without_authenticated_email() {
        let fetched = parse_user_status(
            r#"{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[]}}}"#,
            Utc::now(),
        )
        .unwrap();
        assert_eq!(
            fetched.account_scope,
            Err(AccountScopeError::NoTrustedEvidence)
        );
        assert_eq!(
            fetched.history_scope,
            Err(AccountScopeError::NoTrustedEvidence)
        );
    }

    /// The local IDE route resolves authoritatively by the authenticated
    /// `GetUserStatus` email. Its history scope must consume the same email, or
    /// two accounts on one installation would share one series — and the
    /// series this route recorded before `HistoryScope` would lose their key.
    #[test]
    fn histid_a_local_history_scope_consumes_the_authenticated_email() {
        let scope = TestRefreshScope::new("antigravity", "histid-antigravity");
        let resolve = |provider: &str, authoritative: Option<(AuthoritativeIdKind, &str)>| {
            scope.resolve_history(provider, authoritative)
        };
        let identity = |email: Option<&str>| AgentIdentity {
            email: email.map(str::to_string),
            plan: None,
        };

        let one =
            resolve_local_history_scope_with(Some(&identity(Some("one@example.invalid"))), resolve)
                .expect("authoritative history scope");
        let expected = scope
            .resolve_authoritative(
                "antigravity",
                AuthoritativeIdKind::Email,
                "one@example.invalid",
            )
            .unwrap();
        assert_eq!(one.as_str(), expected.as_str());

        let two =
            resolve_local_history_scope_with(Some(&identity(Some("two@example.invalid"))), resolve)
                .expect("second authoritative history scope");
        assert_ne!(
            crate::agent_quota_history::SeriesKey::new("antigravity", &one, "model.v1"),
            crate::agent_quota_history::SeriesKey::new("antigravity", &two, "model.v1")
        );

        let constant = scope.resolve_history("antigravity", None).unwrap();
        for absent in [None, Some(identity(None)), Some(identity(Some("  ")))] {
            let fallback = resolve_local_history_scope_with(absent.as_ref(), resolve)
                .expect("email-less local route must fall back to the constant, not error");
            assert_eq!(fallback.as_str(), constant.as_str());
            assert_ne!(fallback.as_str(), one.as_str());
            assert_ne!(fallback.as_str(), two.as_str());
        }
        scope.cleanup();
    }

    #[test]
    fn resolves_remote_plan_from_tier() {
        assert_eq!(
            resolve_remote_plan(&json!({"currentTier":{"id":"free-tier"}})).as_deref(),
            Some("Free")
        );
        assert_eq!(
            resolve_remote_plan(&json!({"planInfo":{"planType":"standard"}})).as_deref(),
            Some("Standard")
        );
    }

    fn orchestration_fetched(source: &str) -> Fetched {
        Fetched {
            source: source.to_string(),
            identity: None,
            account_scope: Err(AccountScopeError::NoTrustedEvidence),
            history_scope: Err(AccountScopeError::NoTrustedEvidence),
            cache_binding: None,
            windows: Vec::new(),
        }
    }

    fn orchestration_transient(display: &str) -> ProviderFetchFailure {
        ProviderFetchFailure::transient(
            display,
            None,
            crate::agent_usage::SafeTransportDiagnostic::from_facts(
                TransportErrorFacts::synthetic(true, false, TransportPhase::Request, None),
            ),
        )
    }

    #[tokio::test]
    async fn orchestration_local_success_and_primary_terminal_do_not_call_later_routes() {
        let primary_calls = std::cell::Cell::new(0);
        let secondary_calls = std::cell::Cell::new(0);
        let local = fetch_with(
            || async { LocalAttempt::Success(orchestration_fetched("cli")) },
            || async {
                primary_calls.set(primary_calls.get() + 1);
                PrimaryAttempt::<()>::FinalFailure(ProviderFetchFailure::terminal("unexpected"))
            },
            |_: ()| async {
                secondary_calls.set(secondary_calls.get() + 1);
                Ok(orchestration_fetched("secondary"))
            },
        )
        .await
        .unwrap();
        assert_eq!(local.source, "cli");
        assert_eq!(primary_calls.get(), 0);
        assert_eq!(secondary_calls.get(), 0);

        let failure = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async {
                PrimaryAttempt::<()>::FinalFailure(ProviderFetchFailure::terminal("primary 401"))
            },
            |_: ()| async {
                secondary_calls.set(secondary_calls.get() + 1);
                Ok(orchestration_fetched("secondary"))
            },
        )
        .await
        .unwrap_err();
        assert!(matches!(
            failure,
            ProviderFetchFailure::Terminal { ref display } if display == "primary 401"
        ));
        assert_eq!(secondary_calls.get(), 0);
    }

    #[tokio::test]
    async fn orchestration_primary_forbidden_uses_secondary_result() {
        let success = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async { PrimaryAttempt::Forbidden(()) },
            |()| async { Ok(orchestration_fetched("secondary")) },
        )
        .await
        .unwrap();
        assert_eq!(success.source, "secondary");

        let transient = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async { PrimaryAttempt::Forbidden(()) },
            |()| async { Err(orchestration_transient("secondary transient")) },
        )
        .await
        .unwrap_err();
        assert!(matches!(
            transient,
            ProviderFetchFailure::Transient { ref display, .. }
                if display == "secondary transient"
        ));

        let terminal = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async { PrimaryAttempt::Forbidden(()) },
            |()| async { Err(ProviderFetchFailure::terminal("secondary terminal")) },
        )
        .await
        .unwrap_err();
        assert!(matches!(
            terminal,
            ProviderFetchFailure::Terminal { ref display }
                if display == "secondary terminal"
        ));
    }

    #[tokio::test]
    async fn orchestration_schema_and_transient_precedence_is_fail_closed() {
        let schema_recovered = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async {
                PrimaryAttempt::SchemaContradiction {
                    context: (),
                    failure: ProviderFetchFailure::terminal("primary schema"),
                }
            },
            |()| async { Ok(orchestration_fetched("secondary")) },
        )
        .await
        .unwrap();
        assert_eq!(schema_recovered.source, "secondary");

        let schema_stays_terminal = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async {
                PrimaryAttempt::SchemaContradiction {
                    context: (),
                    failure: ProviderFetchFailure::terminal("primary schema"),
                }
            },
            |()| async { Err(orchestration_transient("secondary transient")) },
        )
        .await
        .unwrap_err();
        assert!(matches!(
            schema_stays_terminal,
            ProviderFetchFailure::Terminal { ref display } if display == "primary schema"
        ));

        let transient_recovered = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async { PrimaryAttempt::Transient(()) },
            |()| async { Ok(orchestration_fetched("secondary")) },
        )
        .await
        .unwrap();
        assert_eq!(transient_recovered.source, "secondary");

        let transient_then_terminal = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async { PrimaryAttempt::Transient(()) },
            |()| async { Err(ProviderFetchFailure::terminal("secondary terminal")) },
        )
        .await
        .unwrap_err();
        assert!(matches!(
            transient_then_terminal,
            ProviderFetchFailure::Terminal { ref display }
                if display == "secondary terminal"
        ));

        let both_transient = fetch_with(
            || async { LocalAttempt::RouteMiss },
            || async { PrimaryAttempt::Transient(()) },
            |()| async { Err(orchestration_transient("secondary transient")) },
        )
        .await
        .unwrap_err();
        assert!(matches!(
            both_transient,
            ProviderFetchFailure::Transient { ref display, .. }
                if display == "secondary transient"
        ));
    }

    fn checkpoint_at(
        target: Option<RefreshCheckpoint>,
    ) -> impl FnMut(RefreshCheckpoint) -> Result<(), ProviderFetchFailure> {
        move |checkpoint| {
            if Some(checkpoint) == target {
                Err(ProviderFetchFailure::terminal("injected crash"))
            } else {
                Ok(())
            }
        }
    }

    async fn test_refresh_response(
        refresh_token: String,
        _attempt_binding: ProviderCacheBinding,
    ) -> Result<Value, ProviderFetchFailure> {
        assert_eq!(refresh_token, "antigravity-old-refresh");
        Ok(json!({
            "access_token": "antigravity-new-access",
            "refresh_token": "antigravity-new-refresh",
            "expires_in": 3600
        }))
    }

    fn setup_refresh(tag: &str) -> (TestRefreshScope, PathBuf, AccountScope, Vec<u8>, String) {
        let scope = TestRefreshScope::new("antigravity", tag);
        let path = scope.root().join("antigravity/oauth_creds.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(
            &path,
            serde_json::to_vec_pretty(&json!({
                "access_token": "antigravity-old-access",
                "refresh_token": "antigravity-old-refresh",
                "expiry_date": 0
            }))
            .unwrap(),
        )
        .unwrap();
        let location = remote_scope_location(&path).unwrap();
        let old_scope = scope
            .resolve_current("google-oauth-creds", &location, b"antigravity-old-refresh")
            .unwrap();
        let metadata = scope.metadata_bytes();
        (scope, path, old_scope, metadata, location)
    }

    async fn run_refresh(
        scope: &TestRefreshScope,
        path: &Path,
        crash: Option<RefreshCheckpoint>,
    ) -> Result<(Value, String, AccountScope, Option<ProviderCacheBinding>), ProviderFetchFailure>
    {
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        refresh_access_token_with(
            path,
            now,
            scope,
            test_refresh_response,
            |creds| write_creds_atomic(path, creds),
            checkpoint_at(crash),
        )
        .await
    }

    fn stored_refresh_token(path: &Path) -> String {
        let credentials = load_remote_credentials(path).unwrap();
        std::str::from_utf8(remote_refresh_marker(&credentials).unwrap())
            .unwrap()
            .to_string()
    }

    #[tokio::test]
    async fn refresh_rejects_concurrent_account_switch_without_touching_b() {
        const B_BYTES: &[u8] = br#"{
  "access_token": "account-b-access",
  "refresh_token": "account-b-refresh",
  "id_token": "account-b-id",
  "expiry_date": 4102444800000,
  "sibling": {"writer": "b", "revision": 2}
}
"#;
        let (scope, path, _, before, _) = setup_refresh("antigravity-target-switch");
        let request_path = path.clone();
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);

        let failure = refresh_access_token_with(
            &path,
            now,
            &scope,
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "antigravity-old-refresh");
                std::fs::write(&request_path, B_BYTES).unwrap();
                Ok(json!({
                    "access_token": "antigravity-new-access",
                    "refresh_token": "antigravity-new-refresh"
                }))
            },
            |_| -> std::io::Result<()> {
                panic!("target mismatch must not reach credential persistence")
            },
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        let stored_bytes = std::fs::read(&path).unwrap();
        assert_eq!(stored_bytes, B_BYTES);
        assert!(!String::from_utf8_lossy(&stored_bytes).contains("antigravity-new"));
        let stored = load_remote_credentials(&path).unwrap();
        assert_eq!(stored["access_token"], "account-b-access");
        assert_eq!(stored["refresh_token"], "account-b-refresh");
        assert_eq!(stored["id_token"], "account-b-id");
        assert_eq!(stored["sibling"]["writer"], "b");
        assert_eq!(scope.metadata_bytes(), before);
        scope.cleanup();
    }

    #[tokio::test]
    async fn refresh_patches_unchanged_target_and_preserves_current_root_siblings() {
        let (scope, path, old_scope, _, location) = setup_refresh("antigravity-target-unchanged");
        std::fs::write(
            &path,
            serde_json::to_vec_pretty(&json!({
                "access_token": "antigravity-old-access",
                "refresh_token": "antigravity-old-refresh",
                "id_token": "antigravity-old-id",
                "expiry_date": 0,
                "stale_only": "must-not-return"
            }))
            .unwrap(),
        )
        .unwrap();
        let current = json!({
            "access_token": "concurrent-access",
            "refresh_token": "antigravity-old-refresh",
            "id_token": "concurrent-id",
            "expiry_date": 0,
            "token_type": "current-writer",
            "sibling": {"writer": "antigravity-cli", "revision": 2},
            "unrelated": [1, 2, 3]
        });
        let current_bytes = serde_json::to_vec_pretty(&current).unwrap();
        let request_path = path.clone();
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);

        let (refreshed, access_token, scope_outcome, cache_binding) = refresh_access_token_with(
            &path,
            now,
            &scope,
            move |refresh_token, _attempt_binding| async move {
                assert_eq!(refresh_token, "antigravity-old-refresh");
                std::fs::write(&request_path, current_bytes).unwrap();
                Ok(json!({
                    "access_token": "antigravity-new-access",
                    "refresh_token": "antigravity-new-refresh",
                    "id_token": "antigravity-new-id",
                    "expires_in": 3600,
                    "token_type": "provider-response"
                }))
            },
            |credentials| write_creds_atomic(&path, credentials),
            checkpoint_at(None),
        )
        .await
        .unwrap();

        assert_eq!(access_token, "antigravity-new-access");
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(
            cache_binding,
            Some(ProviderCacheBinding::primary(old_scope.clone()))
        );
        let stored = load_remote_credentials(&path).unwrap();
        assert_eq!(refreshed, stored);
        assert_eq!(stored["access_token"], "antigravity-new-access");
        assert_eq!(stored["refresh_token"], "antigravity-new-refresh");
        assert_eq!(stored["id_token"], "antigravity-new-id");
        assert_eq!(
            stored["expiry_date"].as_f64(),
            Some(now.timestamp_millis() as f64 + 3_600_000.0)
        );
        assert_eq!(stored["token_type"], "current-writer");
        assert_eq!(stored["sibling"]["writer"], "antigravity-cli");
        assert_eq!(stored["sibling"]["revision"], 2);
        assert_eq!(stored["unrelated"], json!([1, 2, 3]));
        assert!(stored.get("stale_only").is_none());
        assert_eq!(
            scope
                .resolve_current("google-oauth-creds", &location, b"antigravity-new-refresh",)
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn refresh_rejects_concurrent_logout_removal_and_malformed_root_without_restoring_a() {
        const LOGGED_OUT_BYTES: &[u8] = br#"{
  "sibling": {"writer": "logout", "revision": 2}
}
"#;
        const MALFORMED_BYTES: &[u8] = b"{not-json";
        let cases: [(&str, Option<&[u8]>); 3] = [
            ("logout", Some(LOGGED_OUT_BYTES)),
            ("removed", None),
            ("malformed", Some(MALFORMED_BYTES)),
        ];

        for (case, current_bytes) in cases {
            let (scope, path, _, before, _) = setup_refresh(&format!("antigravity-target-{case}"));
            let request_path = path.clone();
            let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
                .unwrap()
                .with_timezone(&Utc);

            let failure = refresh_access_token_with(
                &path,
                now,
                &scope,
                move |refresh_token, _attempt_binding| async move {
                    assert_eq!(refresh_token, "antigravity-old-refresh");
                    if let Some(bytes) = current_bytes {
                        std::fs::write(&request_path, bytes).unwrap();
                    } else {
                        std::fs::remove_file(&request_path).unwrap();
                    }
                    Ok(json!({
                        "access_token": "antigravity-new-access",
                        "refresh_token": "antigravity-new-refresh"
                    }))
                },
                |_| -> std::io::Result<()> {
                    panic!("missing or malformed target must not reach credential persistence")
                },
                checkpoint_at(None),
            )
            .await
            .unwrap_err();

            assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
            assert_eq!(scope.metadata_bytes(), before);
            if let Some(expected) = current_bytes {
                let stored = std::fs::read(&path).unwrap();
                assert_eq!(stored, expected);
                assert!(!String::from_utf8_lossy(&stored).contains("antigravity-new"));
            } else {
                assert!(!path.exists());
            }
            scope.cleanup();
        }
    }

    #[tokio::test]
    async fn refresh_crash_boundaries_and_scope_gate_use_production_sequence() {
        for boundary in [
            RefreshCheckpoint::Reloaded,
            RefreshCheckpoint::NetworkReturned,
            RefreshCheckpoint::MetadataHandled,
            RefreshCheckpoint::CredentialsPersisted,
        ] {
            let (scope, path, old_scope, before, location) = setup_refresh("antigravity-crash");
            let failure = run_refresh(&scope, &path, Some(boundary))
                .await
                .unwrap_err();
            assert!(matches!(
                failure,
                ProviderFetchFailure::Terminal { ref display } if display == "injected crash"
            ));
            assert_eq!(
                stored_refresh_token(&path),
                if boundary == RefreshCheckpoint::CredentialsPersisted {
                    "antigravity-new-refresh"
                } else {
                    "antigravity-old-refresh"
                }
            );
            if matches!(
                boundary,
                RefreshCheckpoint::Reloaded | RefreshCheckpoint::NetworkReturned
            ) {
                assert_eq!(scope.metadata_bytes(), before);
            } else {
                assert_ne!(scope.metadata_bytes(), before);
                assert_eq!(
                    scope
                        .resolve_current(
                            "google-oauth-creds",
                            &location,
                            b"antigravity-old-refresh",
                        )
                        .unwrap(),
                    old_scope
                );
                assert_eq!(
                    scope
                        .resolve_current(
                            "google-oauth-creds",
                            &location,
                            b"antigravity-new-refresh",
                        )
                        .unwrap(),
                    old_scope
                );
            }
            scope.cleanup();
        }

        let (scope, path, old_scope, before, location) = setup_refresh("antigravity-metadata-fail");
        scope.fail_metadata_save();
        let failure = run_refresh(&scope, &path, None).await.unwrap_err();
        assert!(matches!(failure, ProviderFetchFailure::Terminal { .. }));
        assert_eq!(scope.metadata_bytes(), before);
        assert_eq!(stored_refresh_token(&path), "antigravity-old-refresh");
        assert_eq!(
            scope
                .resolve_current("google-oauth-creds", &location, b"antigravity-old-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, old_scope, _, location) = setup_refresh("antigravity-save-fail");
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let (refreshed, access_token, scope_outcome, cache_binding) = refresh_access_token_with(
            &path,
            now,
            &scope,
            test_refresh_response,
            |_| Err(std::io::Error::other("injected save failure")),
            checkpoint_at(None),
        )
        .await
        .unwrap();
        assert_eq!(access_token, "antigravity-new-access");
        assert_eq!(remote_access_token(&refreshed).unwrap(), access_token);
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(cache_binding, None);
        assert_eq!(stored_refresh_token(&path), "antigravity-old-refresh");
        assert_eq!(
            scope
                .resolve_current("google-oauth-creds", &location, b"antigravity-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();

        let (scope, path, old_scope, _, location) = setup_refresh("antigravity-success");
        let (_, _, scope_outcome, cache_binding) = run_refresh(&scope, &path, None).await.unwrap();
        assert_eq!(scope_outcome, old_scope);
        assert_eq!(
            cache_binding,
            Some(ProviderCacheBinding::primary(old_scope.clone()))
        );
        assert_eq!(
            scope
                .resolve_current("google-oauth-creds", &location, b"antigravity-new-refresh")
                .unwrap(),
            old_scope
        );
        scope.cleanup();
    }

    #[tokio::test]
    async fn refresh_transient_uses_lock_reloaded_binding_not_outer_binding() {
        let (scope, path, inner_scope, _, location) = setup_refresh("antigravity-lock-binding");
        let outer_scope = scope
            .resolve_current("google-oauth-creds", &location, b"outer-refresh-a")
            .unwrap();
        assert_ne!(outer_scope, inner_scope);
        let expected = ProviderCacheBinding::primary(inner_scope);
        let request_expected = expected.clone();
        let now = DateTime::parse_from_rfc3339("2026-07-17T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);

        let failure = refresh_access_token_with(
            &path,
            now,
            &scope,
            move |refresh_token, attempt_binding| async move {
                assert_eq!(refresh_token, "antigravity-old-refresh");
                assert_eq!(attempt_binding, request_expected);
                Err(ProviderFetchFailure::transient(
                    "Antigravity token refresh failed. Retrying automatically.",
                    Some(attempt_binding),
                    crate::agent_usage::SafeTransportDiagnostic::from_facts(
                        TransportErrorFacts::synthetic(true, false, TransportPhase::Request, None),
                    ),
                ))
            },
            |credentials| write_creds_atomic(&path, credentials),
            checkpoint_at(None),
        )
        .await
        .unwrap_err();

        match failure {
            ProviderFetchFailure::Transient {
                attempt_binding, ..
            } => assert_eq!(attempt_binding, Some(expected)),
            ProviderFetchFailure::Terminal { .. } => panic!("timeout must remain transient"),
        }
        scope.cleanup();
    }

    // ── agy CLI route ───────────────────────────────────────────────────────

    /// Shape measured from `agy --print /usage --output-format json` on Windows
    /// (2026-09-23). The Gemini buckets carry the measured values; the brief
    /// that relayed the measurement elided the second group's buckets, so
    /// those two are illustrative (ids as on macOS).
    const AGY_WINDOWS_USAGE: &[u8] = br#"{
        "status": "SUCCESS",
        "response": "usage",
        "command": {
            "name": "usage",
            "data": {
                "description": "Quota usage",
                "groups": [
                    {
                        "name": "Gemini Models",
                        "description": "Gemini quota",
                        "buckets": [
                            {"id": "gemini-weekly", "name": "Weekly Limit Remaining", "description": "Weekly", "window": "weekly", "remaining_fraction": 0.8521391749382019, "reset_time": "2026-09-23T14:09:40Z"},
                            {"id": "gemini-5h", "name": "Five Hour Limit Remaining", "window": "5h", "remaining_fraction": 1, "reset_time": "2026-09-23T18:30:15Z"}
                        ]
                    },
                    {
                        "name": "Claude and GPT models",
                        "description": "Third-party quota",
                        "buckets": [
                            {"id": "3p-weekly", "name": "Weekly Limit Remaining", "window": "weekly", "remaining_fraction": 1, "reset_time": "2026-09-28T13:14:23Z"},
                            {"id": "3p-5h", "name": "Five Hour Limit Remaining", "window": "5h", "remaining_fraction": 1, "reset_time": "2026-09-23T18:30:15Z"}
                        ]
                    }
                ]
            }
        }
    }"#;

    fn agy_now() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-09-23T06:00:00Z")
            .unwrap()
            .with_timezone(&Utc)
    }

    #[test]
    fn parses_agy_usage_json_into_quota_windows() {
        let fetched = parse_agy_usage(AGY_WINDOWS_USAGE, agy_now()).unwrap();
        assert_eq!(fetched.source, "agy");
        assert!(fetched.identity.is_none());
        assert!(fetched.cache_binding.is_none());
        assert_eq!(fetched.windows.len(), 4);
        assert_eq!(
            fetched.windows[0].label_for_test(),
            "Gemini Models · Weekly Limit Remaining"
        );
        assert!((fetched.windows[0].remaining_for_test() - 85.21391749382019).abs() < 1e-9);
        assert_eq!(
            fetched.windows[0].pace_window_key_for_test(),
            Some("agy.gemini-weekly.v1")
        );
        assert_eq!(
            fetched.windows[2].label_for_test(),
            "Claude and GPT models · Weekly Limit Remaining"
        );
        let wire = serde_json::to_value(&fetched.windows[1]).unwrap();
        assert_eq!(wire["cardId"], "agy.gemini-5h.v1");
        let third_wire = serde_json::to_value(&fetched.windows[2]).unwrap();
        assert_eq!(third_wire["cardId"], "agy.3p-weekly.v1");
    }

    #[test]
    fn rejects_agy_usage_without_valid_windows() {
        let now = Utc::now();
        let body = br#"{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"buckets":[{"id":"bad","remaining_fraction":2.0}]}]}}}"#;
        assert!(parse_agy_usage(body, now).is_err());
    }

    #[test]
    fn rejects_agy_chat_response_without_usage_command() {
        let now = Utc::now();
        let body = br#"{
            "status": "SUCCESS",
            "response": "A model-generated answer",
            "usage": {"input_tokens": 1, "output_tokens": 1}
        }"#;
        assert!(parse_agy_usage(body, now).is_err());
    }

    #[test]
    fn agy_fallback_only_runs_for_terminal_failures() {
        assert!(!should_try_agy_fallback(&orchestration_transient(
            "temporary"
        )));
        assert!(should_try_agy_fallback(&ProviderFetchFailure::terminal(
            "terminal"
        )));
    }

    /// Counting fakes for one gated poll. `last_written` is what the credential
    /// read returns (`None` = absent); `unreadable` makes the read fail.
    #[derive(Default)]
    struct AgyFakes {
        last_written: std::cell::Cell<Option<u64>>,
        unreadable: std::cell::Cell<bool>,
        resolves: std::cell::Cell<bool>,
        credential_reads: std::cell::Cell<u32>,
        dns_probes: std::cell::Cell<u32>,
        runs: std::cell::Cell<u32>,
    }

    impl AgyFakes {
        fn signed_in(last_written: u64) -> Self {
            let fakes = Self::default();
            fakes.last_written.set(Some(last_written));
            fakes.resolves.set(true);
            fakes
        }

        /// `run` executes when (and only when) the runner is called, so it may
        /// change `last_written` the way agy rewriting its credential would.
        async fn poll(
            &self,
            latch: &AgyLatch,
            executable: Option<PathBuf>,
            run: impl FnOnce() -> Result<Vec<u8>, AgyRunFailure>,
        ) -> Result<Fetched, AgyFailure> {
            fetch_agy_cli_gated(
                agy_now(),
                executable,
                || {
                    self.credential_reads.set(self.credential_reads.get() + 1);
                    if self.unreadable.get() {
                        Err(CredentialUnreadable)
                    } else {
                        Ok(self.last_written.get())
                    }
                },
                latch,
                || {
                    self.dns_probes.set(self.dns_probes.get() + 1);
                    let resolves = self.resolves.get();
                    async move { resolves }
                },
                |_executable| {
                    self.runs.set(self.runs.get() + 1);
                    let result = run();
                    async move { result }
                },
            )
            .await
        }

        async fn poll_with(
            &self,
            latch: &AgyLatch,
            run: impl FnOnce() -> Result<Vec<u8>, AgyRunFailure>,
        ) -> Result<Fetched, AgyFailure> {
            self.poll(latch, Some(std::env::temp_dir().join("agy.exe")), run)
                .await
        }
    }

    fn agy_success() -> Result<Vec<u8>, AgyRunFailure> {
        Ok(AGY_WINDOWS_USAGE.to_vec())
    }

    #[tokio::test]
    async fn agy_not_found_never_touches_the_credential() {
        let fakes = AgyFakes::signed_in(1);
        let latch = AgyLatch::new();
        let result = fakes.poll(&latch, None, agy_success).await;
        assert_eq!(result.unwrap_err(), AgyFailure::Unavailable);
        assert_eq!(fakes.credential_reads.get(), 0);
        assert_eq!(fakes.runs.get(), 0);
    }

    #[tokio::test]
    async fn agy_without_a_readable_credential_is_never_spawned() {
        let latch = AgyLatch::new();

        let absent = AgyFakes::signed_in(1);
        absent.last_written.set(None);
        let result = absent.poll_with(&latch, agy_success).await;
        assert_eq!(result.unwrap_err(), AgyFailure::Unavailable);
        assert_eq!(absent.runs.get(), 0, "credential absent must not spawn agy");
        assert_eq!(absent.dns_probes.get(), 0);

        let unreadable = AgyFakes::signed_in(1);
        unreadable.unreadable.set(true);
        let result = unreadable.poll_with(&latch, agy_success).await;
        assert_eq!(result.unwrap_err(), AgyFailure::Unavailable);
        assert_eq!(unreadable.runs.get(), 0, "a failed read must fail closed");

        // Control: the same latch and runner do spawn with a credential, so
        // the zeros above are the gate and not a dead runner.
        let control = AgyFakes::signed_in(1);
        assert!(control.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(control.runs.get(), 1);
    }

    #[tokio::test]
    async fn agy_dns_refusal_does_not_spawn_and_does_not_latch() {
        let fakes = AgyFakes::signed_in(1);
        let latch = AgyLatch::new();
        fakes.resolves.set(false);
        let refused = fakes.poll_with(&latch, agy_success).await;
        assert_eq!(refused.unwrap_err(), AgyFailure::Unavailable);
        assert_eq!(
            fakes.runs.get(),
            0,
            "an unresolvable endpoint must not spawn agy"
        );

        fakes.resolves.set(true);
        assert!(fakes.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(
            fakes.runs.get(),
            1,
            "a refusal must not pause the next poll"
        );
    }

    #[tokio::test]
    async fn agy_failed_run_pauses_the_route_until_the_credential_changes() {
        let fakes = AgyFakes::signed_in(1);
        let latch = AgyLatch::new();
        let failed = fakes.poll_with(&latch, || Err(AgyRunFailure::Failed)).await;
        assert_eq!(failed.unwrap_err(), AgyFailure::Paused);
        assert_eq!(fakes.runs.get(), 1);

        let paused = fakes.poll_with(&latch, agy_success).await;
        assert_eq!(paused.unwrap_err(), AgyFailure::Paused);
        assert_eq!(fakes.runs.get(), 1, "a latched route must not spawn agy");
        assert_eq!(fakes.dns_probes.get(), 1, "the latch is checked before DNS");

        // Re-armed by a new credential state; a success clears the latch, so
        // the old state no longer blocks either.
        fakes.last_written.set(Some(2));
        assert!(fakes.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(fakes.runs.get(), 2);
        fakes.last_written.set(Some(1));
        assert!(fakes.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(fakes.runs.get(), 3, "success must clear the latch");
    }

    #[tokio::test]
    async fn agy_latch_keys_on_the_credential_as_the_failed_run_left_it() {
        let fakes = AgyFakes::signed_in(1);
        let latch = AgyLatch::new();
        let failed = fakes
            .poll_with(&latch, || {
                fakes.last_written.set(Some(2));
                Err(AgyRunFailure::Failed)
            })
            .await;
        assert_eq!(failed.unwrap_err(), AgyFailure::Paused);
        let paused = fakes.poll_with(&latch, agy_success).await;
        assert_eq!(paused.unwrap_err(), AgyFailure::Paused);
        assert_eq!(
            fakes.runs.get(),
            1,
            "a rewrite during the failed run must not re-arm"
        );

        fakes.last_written.set(Some(3));
        assert!(fakes.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(fakes.runs.get(), 2, "a later credential change re-arms");

        // A run that removes the credential: while it is gone the gate closes
        // the route; if the same credential comes back it is still latched.
        let removed = AgyFakes::signed_in(7);
        let latch = AgyLatch::new();
        let _ = removed
            .poll_with(&latch, || {
                removed.last_written.set(None);
                Err(AgyRunFailure::Failed)
            })
            .await;
        removed.last_written.set(Some(7));
        let paused = removed.poll_with(&latch, agy_success).await;
        assert_eq!(paused.unwrap_err(), AgyFailure::Paused);
        assert_eq!(removed.runs.get(), 1);
    }

    #[tokio::test]
    async fn agy_exit_zero_without_usable_usage_latches() {
        for body in [
            &b"Authentication required. Please visit the URL to log in"[..],
            br#"{"status":"ERROR"}"#,
            br#"{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[]}}}"#,
        ] {
            let fakes = AgyFakes::signed_in(1);
            let latch = AgyLatch::new();
            let failed = fakes.poll_with(&latch, || Ok(body.to_vec())).await;
            assert_eq!(failed.unwrap_err(), AgyFailure::Paused);
            let paused = fakes.poll_with(&latch, agy_success).await;
            assert_eq!(paused.unwrap_err(), AgyFailure::Paused);
            assert_eq!(fakes.runs.get(), 1);
        }
    }

    #[tokio::test]
    async fn agy_spawn_that_created_no_process_does_not_latch() {
        let fakes = AgyFakes::signed_in(1);
        let latch = AgyLatch::new();
        let failed = fakes
            .poll_with(&latch, || Err(AgyRunFailure::NotStarted))
            .await;
        assert_eq!(failed.unwrap_err(), AgyFailure::Unavailable);
        assert!(fakes.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(fakes.runs.get(), 2);
    }

    #[tokio::test]
    async fn agy_fallback_surfaces_only_the_paused_failure() {
        let primary = || Err(ProviderFetchFailure::terminal("primary terminal"));
        let display = |result: Result<Fetched, ProviderFetchFailure>| match result {
            Err(ProviderFetchFailure::Terminal { display }) => display,
            other => panic!("expected a terminal failure, got {other:?}"),
        };

        let fakes = AgyFakes::signed_in(1);
        let latch = AgyLatch::new();
        latch.set(Some((1, false)));
        let paused = with_agy_fallback(primary(), || fakes.poll_with(&latch, agy_success)).await;
        assert_eq!(
            display(paused),
            "Antigravity CLI quota check paused after a failed attempt. Restart Syrtis, or sign in to agy again, to retry."
        );
        assert_eq!(fakes.runs.get(), 0);

        let absent = AgyFakes::signed_in(1);
        absent.last_written.set(None);
        let latch = AgyLatch::new();
        let unchanged =
            with_agy_fallback(primary(), || absent.poll_with(&latch, agy_success)).await;
        assert_eq!(display(unchanged), "primary terminal");

        let failing = AgyFakes::signed_in(1);
        // The failing run itself already reports the pause.
        let failed = with_agy_fallback(primary(), || {
            failing.poll_with(&latch, || Err(AgyRunFailure::Failed))
        })
        .await;
        assert_eq!(display(failed), AGY_PAUSED_MESSAGE);

        let working = AgyFakes::signed_in(5);
        let fetched = with_agy_fallback(primary(), || working.poll_with(&latch, agy_success))
            .await
            .unwrap();
        assert_eq!(fetched.source, "agy");
    }

    /// A run that timed out (agy waiting for a browser sign-in, measured on a
    /// Windows host) latches like any failure, but says so: the card names the
    /// timeout and the likely sign-in wait instead of a generic failure. No
    /// timed re-arm: later polls on the same credential still do not spawn.
    /// A re-login (new `LastWritten`) re-arms. Control: a non-timeout failure
    /// keeps the generic paused message.
    #[tokio::test]
    async fn a_timed_out_agy_run_latches_and_names_the_timeout() {
        let display = |result: Result<Fetched, ProviderFetchFailure>| match result {
            Err(ProviderFetchFailure::Terminal { display }) => display,
            other => panic!("expected a terminal failure, got {other:?}"),
        };
        let primary = || Err(ProviderFetchFailure::terminal("primary terminal"));
        let fakes = AgyFakes::signed_in(7);
        let latch = AgyLatch::new();

        let first = fakes.poll_with(&latch, || Err(AgyRunFailure::TimedOut)).await;
        assert_eq!(first.unwrap_err(), AgyFailure::TimedOut);
        assert_eq!(fakes.runs.get(), 1);

        let shown = with_agy_fallback(primary(), || fakes.poll_with(&latch, agy_success)).await;
        assert_eq!(display(shown), AGY_TIMED_OUT_MESSAGE);
        assert_eq!(fakes.runs.get(), 1, "the same credential is not respawned after a timeout");

        // A re-login rewrites the credential and re-arms the route.
        fakes.last_written.set(Some(8));
        assert!(fakes.poll_with(&latch, agy_success).await.is_ok());
        assert_eq!(fakes.runs.get(), 2);

        // Control: a non-timeout failure keeps the generic pause.
        let failed = with_agy_fallback(primary(), || {
            fakes.poll_with(&latch, || Err(AgyRunFailure::Failed))
        })
        .await;
        assert_eq!(display(failed), AGY_PAUSED_MESSAGE);
        let still = with_agy_fallback(primary(), || fakes.poll_with(&latch, agy_success)).await;
        assert_eq!(display(still), AGY_PAUSED_MESSAGE);
    }

    #[test]
    fn agy_discovery_prefers_the_installer_location_then_one_absolute_exact_path_entry() {
        let root = std::env::temp_dir().join("agy-discovery-fixture");
        let local_app_data = root.join("LocalAppData");
        let installed = local_app_data.join("agy").join("bin").join("agy.exe");
        let tools = root.join("tools");
        let second = root.join("second");
        let shims = root.join("shims");
        let existing = [
            installed.clone(),
            tools.join("agy.exe"),
            second.join("agy.exe"),
            shims.join("agy.cmd"),
            shims.join("agy.bat"),
            // What empty, `.` and relative entries (and a relative
            // LOCALAPPDATA) would resolve to, present so skipping is observable.
            PathBuf::from("agy.exe"),
            Path::new(".").join("agy.exe"),
            Path::new("relative").join("agy.exe"),
            Path::new("LocalAppData")
                .join("agy")
                .join("bin")
                .join("agy.exe"),
        ];
        let is_file = |candidate: &Path| existing.iter().any(|file| file == candidate);
        let sep = if cfg!(windows) { ";" } else { ":" };
        let path_of = |dirs: &[&str]| std::ffi::OsString::from(dirs.join(sep));
        let untrusted = ["", "", ".", "relative", shims.to_str().unwrap()];
        let with_tools = path_of(
            &[
                &untrusted[..],
                &[tools.to_str().unwrap(), second.to_str().unwrap()],
            ]
            .concat(),
        );

        assert_eq!(
            agy_executable_from(Some(local_app_data.as_os_str()), Some(&with_tools), is_file),
            Some(installed.clone()),
            "the installer location wins over PATH"
        );
        assert_eq!(
            agy_executable_from(None, Some(&with_tools), is_file),
            Some(tools.join("agy.exe")),
            "only the first absolute PATH entry with agy.exe"
        );
        assert_eq!(
            agy_executable_from(
                Some(root.join("elsewhere").as_os_str()),
                Some(&with_tools),
                is_file
            ),
            Some(tools.join("agy.exe")),
            "a missing installer location falls back to PATH"
        );
        assert_eq!(
            agy_executable_from(
                Some(std::ffi::OsStr::new("LocalAppData")),
                Some(&with_tools),
                is_file
            ),
            Some(tools.join("agy.exe")),
            "a relative LOCALAPPDATA is not trusted"
        );
        assert_eq!(
            agy_executable_from(None, Some(&path_of(&untrusted)), is_file),
            None,
            "empty, `.`, relative entries and .cmd/.bat shims never match"
        );
        assert_eq!(agy_executable_from(None, None, is_file), None);
    }
}

/// Fakes for every captured-account boundary: Credential Manager, the
/// artifacts, the token endpoint, the scope store and the quota calls. Shared
/// with `agent_usage`'s tests, which compose captured accounts into cards.
///
/// The fake checks Credential Manager calls against the Win32 numbers
/// themselves (`1` = `CRED_TYPE_GENERIC`, `2` = `CRED_PERSIST_LOCAL_MACHINE`)
/// and the literal target prefix, never against this module's constants, so a
/// changed constant is caught rather than mirrored.
#[cfg(test)]
pub(crate) mod captured_test_support {
    use super::*;
    use crate::agent_account_scope::test_support::TestRefreshScope;
    use std::cell::{Cell, RefCell};

    pub(crate) const AUD: &str = "111111111111-agy.apps.googleusercontent.com";
    pub(crate) const OTHER: &str = "222222222222-other.apps.googleusercontent.com";
    pub(crate) const PREFIX: &str = "com.nyanako.tokenbar.antigravity-account:";
    pub(crate) const WIN32_CRED_TYPE_GENERIC: u32 = 1;
    pub(crate) const WIN32_CRED_PERSIST_LOCAL_MACHINE: u32 = 2;

    pub(crate) fn secret(fill: char) -> String {
        format!("GOCSPX-{}", fill.to_string().repeat(28))
    }

    pub(crate) fn jwt(claims: Value) -> String {
        format!(
            "e30.{}.sig",
            base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(claims.to_string())
        )
    }

    /// agy's Windows blob: raw UTF-8 JSON, the shape the device probe saw.
    pub(crate) fn agy_item(refresh_token: &str, claims: Option<Value>) -> Vec<u8> {
        let mut login = json!({
            "token": {
                "access_token": "ya29.stored",
                "token_type": "Bearer",
                "refresh_token": refresh_token,
                "expiry": "2026-10-02T00:00:00Z",
            },
            "auth_method": "oauth",
        });
        if let Some(claims) = claims {
            login["id_token"] = Value::String(jwt(claims));
        }
        login.to_string().into_bytes()
    }

    pub(crate) fn stored_value(
        refresh_token: &str,
        client_id: &str,
        client_secret: &str,
    ) -> Vec<u8> {
        encode_stored(&StoredCredential {
            refresh_token: refresh_token.to_string(),
            client: OAuthClient {
                id: client_id.to_string(),
                secret: client_secret.to_string(),
            },
        })
    }

    /// `(refresh_token, client_id, client_secret)` of a stored blob.
    pub(crate) fn decode_value(value: &[u8]) -> (String, String, String) {
        let stored = decode_stored(value).expect("a decodable stored value");
        (stored.refresh_token, stored.client.id, stored.client.secret)
    }

    pub(crate) fn token_ok(access_token: &str, extra: Value) -> (u16, String) {
        let mut body =
            json!({ "access_token": access_token, "expires_in": 3599, "token_type": "Bearer" });
        if let (Some(body), Some(extra)) = (body.as_object_mut(), extra.as_object()) {
            body.extend(extra.clone());
        }
        (200, body.to_string())
    }

    type TokenFn = Box<dyn Fn(&OAuthClient, &str) -> Result<(u16, String), ProviderFetchFailure>>;

    /// One recorded write: `(target, type, persist, blob)`.
    pub(crate) type Write = (String, u32, u32, Vec<u8>);

    /// How agy's credential read ends when it is not simply found or absent.
    #[derive(Clone, Copy)]
    pub(crate) enum AgyRead {
        NotFound,
        Failed,
    }

    pub(crate) struct FakeIo {
        pub agy_item: Option<Vec<u8>>,
        /// When set, agy's read ends this way instead of following `agy_item`.
        pub agy_read: Option<AgyRead>,
        pub agy_last_written: Result<Option<u64>, ()>,
        pub artifacts: Vec<Vec<u8>>,
        /// The fake Credential Manager: key (target minus prefix) → blob.
        pub items: RefCell<BTreeMap<String, Vec<u8>>>,
        pub write_fails: bool,
        pub token: TokenFn,
        pub cred_calls: RefCell<Vec<CredCall>>,
        /// Every boundary crossing in order, without secrets: `read <target>`,
        /// `write <target>`, `delete <target>`, `scan`, `token`, `quota`.
        pub events: RefCell<Vec<String>>,
        /// `(client_id, client_secret, refresh_token)` per token request.
        pub token_calls: RefCell<Vec<(String, String, String)>>,
        pub artifact_calls: Cell<usize>,
        pub quota_calls: Cell<usize>,
        /// When set, `quota` fails terminally with this display text.
        pub quota_terminal: Option<&'static str>,
        pub scope: TestRefreshScope,
    }

    impl FakeIo {
        pub(crate) fn new(tag: &str) -> Self {
            Self {
                agy_item: None,
                agy_read: None,
                agy_last_written: Ok(None),
                artifacts: Vec::new(),
                items: RefCell::new(BTreeMap::new()),
                write_fails: false,
                token: Box::new(|_, _| Ok(token_ok("ya29.fresh", json!({})))),
                cred_calls: RefCell::new(Vec::new()),
                events: RefCell::new(Vec::new()),
                token_calls: RefCell::new(Vec::new()),
                artifact_calls: Cell::new(0),
                quota_calls: Cell::new(0),
                quota_terminal: None,
                scope: TestRefreshScope::new("antigravity", tag),
            }
        }

        pub(crate) fn writes(&self) -> Vec<Write> {
            self.cred_calls
                .borrow()
                .iter()
                .filter_map(|call| match call {
                    CredCall::Write {
                        target,
                        cred_type,
                        persist,
                        blob,
                    } => Some((target.clone(), *cred_type, *persist, blob.clone())),
                    _ => None,
                })
                .collect()
        }

        pub(crate) fn reads_of_captured_items(&self) -> usize {
            self.cred_calls
                .borrow()
                .iter()
                .filter(|call| {
                    matches!(call, CredCall::Read { target, .. } if target.starts_with(PREFIX))
                })
                .count()
        }

        pub(crate) fn network_calls(&self) -> usize {
            self.token_calls.borrow().len() + self.quota_calls.get()
        }

        fn event(&self, event: String) {
            self.events.borrow_mut().push(event);
        }
    }

    impl Drop for FakeIo {
        fn drop(&mut self) {
            self.scope.cleanup();
        }
    }

    fn ok(blob: Vec<u8>) -> CredOutcome {
        CredOutcome::Ok(SecretBytes(blob))
    }

    impl CapturedIo for FakeIo {
        fn credential(&self, call: CredCall) -> CredOutcome {
            self.cred_calls.borrow_mut().push(call.clone());
            match call {
                CredCall::Read { target, cred_type } => {
                    assert_eq!(cred_type, WIN32_CRED_TYPE_GENERIC, "read of {target}");
                    self.event(format!("read {target}"));
                    if target == "gemini:antigravity" {
                        return match (self.agy_read, &self.agy_item) {
                            (Some(AgyRead::NotFound), _) | (None, None) => CredOutcome::NotFound,
                            (Some(AgyRead::Failed), _) => CredOutcome::Failed,
                            (None, Some(item)) => ok(item.clone()),
                        };
                    }
                    let key = target
                        .strip_prefix(PREFIX)
                        .unwrap_or_else(|| panic!("read outside the prefix: {target}"));
                    match self.items.borrow().get(key) {
                        Some(blob) => ok(blob.clone()),
                        None => CredOutcome::NotFound,
                    }
                }
                CredCall::Write {
                    target,
                    cred_type,
                    persist,
                    blob,
                } => {
                    assert_eq!(cred_type, WIN32_CRED_TYPE_GENERIC, "write of {target}");
                    assert_eq!(
                        persist, WIN32_CRED_PERSIST_LOCAL_MACHINE,
                        "write of {target}"
                    );
                    self.event(format!("write {target}"));
                    let key = target
                        .strip_prefix(PREFIX)
                        .unwrap_or_else(|| panic!("write outside the prefix: {target}"));
                    if self.write_fails {
                        return CredOutcome::Failed;
                    }
                    self.items.borrow_mut().insert(key.to_string(), blob);
                    ok(Vec::new())
                }
                CredCall::Delete { target, cred_type } => {
                    assert_eq!(cred_type, WIN32_CRED_TYPE_GENERIC, "delete of {target}");
                    self.event(format!("delete {target}"));
                    let key = target
                        .strip_prefix(PREFIX)
                        .unwrap_or_else(|| panic!("delete outside the prefix: {target}"));
                    match self.items.borrow_mut().remove(key) {
                        Some(_) => ok(Vec::new()),
                        None => CredOutcome::NotFound,
                    }
                }
            }
        }

        fn agy_last_written(&self) -> Result<Option<u64>, CredentialUnreadable> {
            self.agy_last_written.map_err(|()| CredentialUnreadable)
        }

        async fn client_artifacts(&self) -> Vec<Vec<u8>> {
            self.artifact_calls.set(self.artifact_calls.get() + 1);
            self.event("scan".to_string());
            self.artifacts.clone()
        }

        async fn token_post(
            &self,
            client: &OAuthClient,
            refresh_token: &str,
            _binding: Option<ProviderCacheBinding>,
        ) -> Result<(u16, String), ProviderFetchFailure> {
            self.event("token".to_string());
            self.token_calls.borrow_mut().push((
                client.id.clone(),
                client.secret.clone(),
                refresh_token.to_string(),
            ));
            (self.token)(client, refresh_token)
        }

        fn scopes(
            &self,
            key: &str,
        ) -> (
            Result<AccountScope, AccountScopeError>,
            Result<HistoryScope, AccountScopeError>,
        ) {
            captured_scopes(
                key,
                |provider, kind, id| self.scope.resolve_authoritative(provider, kind, id),
                |provider, authoritative| self.scope.resolve_history(provider, authoritative),
            )
        }

        async fn quota(
            &self,
            _access_token: String,
            account_scope: AccountScope,
            history_scope: Result<HistoryScope, AccountScopeError>,
            now: DateTime<Utc>,
        ) -> Result<Fetched, ProviderFetchFailure> {
            self.event("quota".to_string());
            self.quota_calls.set(self.quota_calls.get() + 1);
            if let Some(display) = self.quota_terminal {
                return Err(ProviderFetchFailure::terminal(display));
            }
            let window = quota_window(
                "Gemini".to_string(),
                0.5,
                Some(now + chrono::Duration::hours(1)),
                now,
                "agy.test.v1".to_string(),
                Some("agy.test.v1".to_string()),
            )
            .expect("a valid window");
            Ok(Fetched {
                source: "oauth".to_string(),
                identity: Some(remote_identity(Some("Paid".to_string()))),
                account_scope: Ok(account_scope.clone()),
                history_scope,
                cache_binding: Some(ProviderCacheBinding::primary(account_scope)),
                windows: vec![window],
            })
        }
    }

    pub(crate) fn new_token_cache() -> CapturedTokenCache {
        std::sync::Mutex::new(HashMap::new())
    }

    pub(crate) fn cached_keys(cache: &CapturedTokenCache) -> Vec<String> {
        lock_tokens(cache).keys().cloned().collect()
    }
}

#[cfg(test)]
mod captured_account_tests {
    use super::captured_test_support::*;
    use super::*;

    fn claims(sub: &str, aud: &str, email: &str) -> Value {
        json!({ "sub": sub, "aud": aud, "email": email })
    }

    fn now() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-10-02T06:00:00Z")
            .unwrap()
            .with_timezone(&Utc)
    }

    /// One artifact naming the token's client and another, with ONE secret:
    /// `preferred_client` pairs that secret with the LAST id (OTHER), so a
    /// positional pick would refresh with the wrong client.
    fn artifacts_where_positional_pick_differs() -> Vec<Vec<u8>> {
        vec![
            format!("ide\0{AUD}\0{OTHER}\0{}\0", secret('a')).into_bytes(),
            format!("agy\0{OTHER}\0{}\0", secret('b')).into_bytes(),
        ]
    }

    #[test]
    fn parses_the_windows_agy_login_and_nothing_else() {
        let login = parse_agy_login(&agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "a@example.com")),
        ))
        .unwrap_or_else(|_| panic!("parse"));
        assert_eq!(login.refresh_token, "1//rt-a");
        assert_eq!(login.sub, "sub-a");
        assert_eq!(login.aud, AUD);
        assert_eq!(login.email.as_deref(), Some("a@example.com"));

        // macOS's go-keyring form is not a Windows blob.
        let mac_form = format!(
            "go-keyring-base64:{}",
            base64::engine::general_purpose::STANDARD.encode(agy_item(
                "1//rt-a",
                Some(claims("sub-a", AUD, "a@example.com"))
            ))
        );
        assert_eq!(
            parse_agy_login(mac_form.as_bytes()).err(),
            Some(CaptureError::AgyLoginUnreadable)
        );
        assert_eq!(
            parse_agy_login(&agy_item("", Some(claims("s", AUD, "e")))).err(),
            Some(CaptureError::AgyLoginUnreadable),
            "without a refresh token"
        );
        // UTF-16 (what a wide-string reader would see) is not accepted either.
        let utf16: Vec<u8> = String::from_utf8(agy_item("1//rt-a", Some(claims("s", AUD, "e"))))
            .unwrap()
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect();
        assert_eq!(
            parse_agy_login(&utf16).err(),
            Some(CaptureError::AgyLoginUnreadable)
        );
    }

    #[tokio::test]
    async fn capture_without_sub_or_aud_fails_without_write() {
        for claims in [
            Some(json!({ "aud": AUD, "email": "a@example.com" })),
            Some(json!({ "sub": "sub-a", "email": "a@example.com" })),
            None,
        ] {
            let mut io = FakeIo::new("capture-missing-identity");
            io.agy_item = Some(agy_item("1//rt-a", claims));
            io.artifacts = artifacts_where_positional_pick_differs();
            assert_eq!(
                capture_with(&io).await.err(),
                Some(CaptureError::AgyLoginMissingIdentity)
            );
            assert_eq!(*io.events.borrow(), ["read gemini:antigravity"]);
            assert!(io.writes().is_empty());
            assert_eq!(io.network_calls(), 0);
        }
    }

    /// Acceptance 1 and 8: exactly one write, to the prefixed 64-hex target,
    /// `CRED_TYPE_GENERIC` + `CRED_PERSIST_LOCAL_MACHINE`, the JSON shape, and
    /// only after the refresh with the `aud`-named client succeeded.
    #[tokio::test]
    async fn capture_writes_one_generic_local_machine_credential_after_an_aud_matched_refresh() {
        let ids = scan_client_ids(&artifacts_where_positional_pick_differs()[0]);
        let secrets = scan_client_secrets(&artifacts_where_positional_pick_differs()[0]);
        assert_eq!(
            preferred_client(&ids, &secrets)
                .map(|client| client.0)
                .as_deref(),
            Some(OTHER),
            "the fixture must make the positional pick differ from aud"
        );

        let mut io = FakeIo::new("capture-by-aud");
        io.agy_item = Some(agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "a@example.com")),
        ));
        io.artifacts = artifacts_where_positional_pick_differs();
        let issuing = secret('a');
        io.token = Box::new(move |client, _| {
            if client.id == AUD && client.secret == issuing {
                Ok(token_ok(
                    "ya29.a",
                    json!({ "id_token": jwt(json!({ "sub": "sub-a" })) }),
                ))
            } else {
                Ok((401, r#"{"error":"invalid_client"}"#.to_string()))
            }
        });
        let account = capture_with(&io).await.unwrap();
        assert_eq!(account.key, captured_key("sub-a"));
        assert!(account.key.len() == 64 && account.key.bytes().all(|b| b.is_ascii_hexdigit()));
        assert_eq!(account.label, "a@example.com");
        assert_eq!(
            *io.token_calls.borrow(),
            vec![(AUD.to_string(), secret('a'), "1//rt-a".to_string())]
        );

        let target = format!("{PREFIX}{}", account.key);
        assert_eq!(
            *io.events.borrow(),
            [
                "read gemini:antigravity".to_string(),
                "scan".to_string(),
                "token".to_string(),
                format!("write {target}"),
            ],
            "the write comes only after the refresh succeeded"
        );
        let writes = io.writes();
        assert_eq!(writes.len(), 1);
        let (written_target, cred_type, persist, blob) = &writes[0];
        assert_eq!(written_target, &target);
        assert_eq!(*cred_type, WIN32_CRED_TYPE_GENERIC);
        assert_eq!(*persist, WIN32_CRED_PERSIST_LOCAL_MACHINE);
        let value: Value = serde_json::from_slice(blob).expect("UTF-8 JSON blob");
        assert_eq!(
            value,
            json!({ "refresh_token": "1//rt-a", "client_id": AUD, "client_secret": secret('a') })
        );
    }

    #[tokio::test]
    async fn capture_tries_the_next_secret_only_for_a_wrong_client() {
        let artifact = format!("ide\0{AUD}\0{}\0{}\0", secret('a'), secret('b')).into_bytes();

        let mut io = FakeIo::new("capture-fallthrough");
        io.agy_item = Some(agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "a@example.com")),
        ));
        io.artifacts = vec![artifact.clone()];
        let second = secret('b');
        io.token = Box::new(move |client, _| {
            if client.secret == second {
                Ok(token_ok("ya29.a", json!({})))
            } else {
                Ok((401, r#"{"error":"unauthorized_client"}"#.to_string()))
            }
        });
        let account = capture_with(&io).await.unwrap();
        assert_eq!(io.token_calls.borrow().len(), 2);
        assert_eq!(
            decode_value(&io.items.borrow()[&account.key]).2,
            secret('b')
        );

        let mut io = FakeIo::new("capture-wrong-client-fallthrough");
        io.agy_item = Some(agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "a@example.com")),
        ));
        io.artifacts = vec![artifact.clone()];
        let second = secret('b');
        io.token = Box::new(move |client, _| {
            if client.secret == second {
                Ok(token_ok("ya29.a", json!({})))
            } else {
                Ok((401, r#"{"error":"invalid_client"}"#.to_string()))
            }
        });
        capture_with(&io).await.unwrap();
        assert_eq!(
            io.token_calls.borrow().len(),
            2,
            "invalid_client falls through"
        );

        let mut io = FakeIo::new("capture-stops");
        io.agy_item = Some(agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "a@example.com")),
        ));
        io.artifacts = vec![artifact];
        io.token = Box::new(|_, _| Ok((400, r#"{"error":"invalid_grant"}"#.to_string())));
        assert_eq!(
            capture_with(&io).await.err(),
            Some(CaptureError::RefreshRejected)
        );
        assert_eq!(io.token_calls.borrow().len(), 1, "any other error stops");
        assert!(io.writes().is_empty());
    }

    /// Acceptance 8: `agy.exe` is a candidate (path lookup only), and the
    /// `aud` match finds a client that only agy.exe carries.
    #[tokio::test]
    async fn the_aud_client_is_found_in_agy_exe_among_the_artifacts() {
        let root = std::env::temp_dir().join("captured-artifacts-fixture");
        let local = root.join("LocalAppData");
        let program_files = root.join("ProgramFiles");
        let agy = local.join("agy").join("bin").join("agy.exe");
        let is_file = |candidate: &Path| candidate == agy;
        let candidates = windows_captured_artifact_candidates(
            Some(local.as_os_str()),
            Some(program_files.as_os_str()),
            None,
            is_file,
        );
        assert_eq!(
            candidates,
            [
                local
                    .join("Programs")
                    .join("Antigravity")
                    .join("resources/bin/language_server.exe"),
                program_files
                    .join("Antigravity")
                    .join("resources/bin/language_server.exe"),
                agy.clone(),
            ]
        );

        // The IDE binary carries another client only; agy.exe carries aud's.
        let mut io = FakeIo::new("capture-agy-exe");
        io.agy_item = Some(agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "a@example.com")),
        ));
        io.artifacts = vec![
            format!("ide\0{OTHER}\0{}\0", secret('o')).into_bytes(),
            format!("agy.exe\0{AUD}\0{}\0", secret('g')).into_bytes(),
        ];
        let account = capture_with(&io).await.unwrap();
        assert_eq!(
            *io.token_calls.borrow(),
            vec![(AUD.to_string(), secret('g'), "1//rt-a".to_string())]
        );
        assert_eq!(decode_value(&io.items.borrow()[&account.key]).1, AUD);
    }

    #[tokio::test]
    async fn capture_rejects_a_refresh_for_another_account() {
        for id_token in [
            Value::String(jwt(json!({ "sub": "sub-b" }))),
            Value::String("not-a-jwt".to_string()),
            Value::Null,
        ] {
            let mut io = FakeIo::new("capture-mismatch");
            io.agy_item = Some(agy_item(
                "1//rt-a",
                Some(claims("sub-a", AUD, "a@example.com")),
            ));
            io.artifacts = artifacts_where_positional_pick_differs();
            let id_token = id_token.clone();
            io.token = Box::new(move |_, _| {
                Ok(token_ok("ya29.a", json!({ "id_token": id_token.clone() })))
            });
            assert_eq!(
                capture_with(&io).await.err(),
                Some(CaptureError::AccountMismatch)
            );
            assert!(io.writes().is_empty());
            assert!(io.items.borrow().is_empty());
        }
    }

    /// Acceptance 3: every captured builder refuses a non-hex key before any
    /// call, every target it builds is under the Syrtis prefix, and the only
    /// call ever naming agy's credential is a read.
    #[test]
    fn credential_calls_name_only_the_syrtis_prefix_and_agy_only_for_a_read() {
        let key = captured_key("sub-a");
        let value = stored_value("1//rt-a", AUD, &secret('a'));
        let bad_keys = [
            String::new(),
            format!("{} ", &key[..63]),
            format!("{}\0", &key[..63]),
            key.to_uppercase(),
            key[..63].to_string(),
            format!("{key}0"),
            "gemini:antigravity".to_string(),
            format!("../{}", &key[..61]),
        ];
        for bad in &bad_keys {
            assert!(write_item_call(bad, value.clone()).is_none(), "{bad:?}");
            assert!(read_item_call(bad).is_none(), "{bad:?}");
            assert!(delete_item_call(bad).is_none(), "{bad:?}");
        }
        assert!(write_item_call(&key, Vec::new()).is_none(), "empty blob");
        assert!(
            write_item_call(&key, vec![b'x'; 2561]).is_none(),
            "over 2560 bytes"
        );
        assert!(write_item_call(&key, vec![b'x'; 2560]).is_some());

        let target = format!("{PREFIX}{key}");
        let calls = [
            read_item_call(&key).unwrap(),
            write_item_call(&key, value.clone()).unwrap(),
            delete_item_call(&key).unwrap(),
        ];
        for call in &calls {
            let (CredCall::Read {
                target: t,
                cred_type,
            }
            | CredCall::Write {
                target: t,
                cred_type,
                ..
            }
            | CredCall::Delete {
                target: t,
                cred_type,
            }) = call;
            assert_eq!(t, &target);
            assert_eq!(*cred_type, WIN32_CRED_TYPE_GENERIC);
        }
        assert!(matches!(
            &calls[1],
            CredCall::Write { persist, .. } if *persist == WIN32_CRED_PERSIST_LOCAL_MACHINE
        ));
        assert!(matches!(
            agy_read_call(),
            CredCall::Read { ref target, cred_type }
                if target == "gemini:antigravity" && cred_type == WIN32_CRED_TYPE_GENERIC
        ));
    }

    /// Acceptance 3: a malformed key reaches no Credential Manager call and no
    /// network, from remove or from a poll.
    #[tokio::test]
    async fn malformed_key_reaches_no_credential_call() {
        let io = FakeIo::new("malformed-key");
        let cache = new_token_cache();
        for bad in ["a b", "a\"b", "a\nb", "ZZ", "gemini:antigravity", ""] {
            assert_eq!(
                remove_with(&io, &cache, bad).err(),
                Some(CaptureError::InvalidKey)
            );
            assert!(fetch_captured_with(&io, &cache, bad, "label", now())
                .await
                .is_err());
        }
        assert!(io.cred_calls.borrow().is_empty());
        assert_eq!(io.network_calls(), 0);
    }

    #[tokio::test]
    async fn polls_within_a_token_lifetime_refresh_once_and_never_write() {
        let mut io = FakeIo::new("polls");
        let key = captured_key("sub-a");
        io.items
            .borrow_mut()
            .insert(key.clone(), stored_value("1//rt-a", AUD, &secret('a')));
        // Google echoing the same refresh token is not a rotation.
        io.token = Box::new(|_, _| Ok(token_ok("ya29.a", json!({ "refresh_token": "1//rt-a" }))));
        let cache = new_token_cache();
        for minutes in [0, 10, 20, 30, 40, 54] {
            let fetched = fetch_captured_with(
                &io,
                &cache,
                &key,
                "a@example.com",
                now() + chrono::Duration::minutes(minutes),
            )
            .await
            .unwrap();
            assert_eq!(
                fetched.identity.as_ref().and_then(|id| id.email.as_deref()),
                Some("a@example.com")
            );
        }
        assert_eq!(io.token_calls.borrow().len(), 1);
        assert_eq!(io.reads_of_captured_items(), 1);
        assert!(io.writes().is_empty());
        assert_eq!(io.quota_calls.get(), 6);

        // Inside the five-minute margin the token is refreshed again.
        fetch_captured_with(
            &io,
            &cache,
            &key,
            "a",
            now() + chrono::Duration::minutes(56),
        )
        .await
        .unwrap();
        assert_eq!(io.token_calls.borrow().len(), 2);
        assert!(io.writes().is_empty());
    }

    #[tokio::test]
    async fn a_rotated_refresh_token_is_written_once_to_its_own_credential() {
        let mut io = FakeIo::new("rotation");
        let key_a = captured_key("sub-a");
        let key_b = captured_key("sub-b");
        let stored_b = stored_value("1//rt-b", AUD, &secret('b'));
        io.items
            .borrow_mut()
            .insert(key_a.clone(), stored_value("1//rt-a", AUD, &secret('a')));
        io.items
            .borrow_mut()
            .insert(key_b.clone(), stored_b.clone());
        io.token = Box::new(|_, refresh_token| {
            if refresh_token == "1//rt-a" {
                Ok(token_ok("ya29.a", json!({ "refresh_token": "1//rt-a2" })))
            } else {
                Ok(token_ok("ya29.other", json!({})))
            }
        });
        let cache = new_token_cache();
        fetch_captured_with(&io, &cache, &key_a, "a", now())
            .await
            .unwrap();
        fetch_captured_with(&io, &cache, &key_b, "b", now())
            .await
            .unwrap();

        let writes = io.writes();
        assert_eq!(writes.len(), 1);
        assert_eq!(writes[0].0, format!("{PREFIX}{key_a}"));
        let items = io.items.borrow();
        assert_eq!(
            decode_value(&items[&key_a]),
            ("1//rt-a2".to_string(), AUD.to_string(), secret('a'))
        );
        assert_eq!(
            items[&key_b], stored_b,
            "the other account's credential is untouched"
        );
    }

    /// Acceptance 7: the scopes come from the temp store only.
    #[test]
    fn captured_accounts_get_their_own_scopes() {
        let io = FakeIo::new("scopes");
        assert!(io.scope.root().starts_with(std::env::temp_dir()));
        let key_a = captured_key("sub-a");
        let key_b = captured_key("sub-b");
        assert_ne!(key_a, key_b);
        assert!(valid_captured_key(&key_a) && !key_a.contains("sub"));

        let (account_a, history_a) = io.scopes(&key_a);
        let (account_b, history_b) = io.scopes(&key_b);
        let (account_a, history_a) = (account_a.unwrap(), history_a.unwrap());
        let (account_b, history_b) = (account_b.unwrap(), history_b.unwrap());
        assert_ne!(account_a, account_b);
        assert_ne!(history_a, history_b);

        let primary_history = io.scope.resolve_history("antigravity", None).unwrap();
        let primary_account = io
            .scope
            .resolve_current(
                "google-oauth-creds",
                "/x/.gemini/oauth_creds.json\0refresh_token",
                b"1//rt-a",
            )
            .unwrap();
        for history in [&history_a, &history_b] {
            assert_ne!(*history, primary_history);
        }
        for account in [&account_a, &account_b] {
            assert_ne!(*account, primary_account);
        }

        // Stable across polls: the same key resolves to the same scopes.
        let (again_account, again_history) = io.scopes(&key_a);
        assert_eq!(again_account.unwrap(), account_a);
        assert_eq!(again_history.unwrap(), history_a);
    }

    /// Acceptance 3: remove deletes exactly its own target, makes no network
    /// call, and drops the cached access token.
    #[tokio::test]
    async fn remove_deletes_only_its_own_target_and_drops_the_cached_token() {
        let io = FakeIo::new("remove");
        let key = captured_key("sub-a");
        let other = captured_key("sub-b");
        io.items
            .borrow_mut()
            .insert(key.clone(), stored_value("1//rt-a", AUD, &secret('a')));
        io.items
            .borrow_mut()
            .insert(other.clone(), stored_value("1//rt-b", AUD, &secret('b')));
        let cache = new_token_cache();
        fetch_captured_with(&io, &cache, &key, "a", now())
            .await
            .unwrap();
        assert_eq!(cached_keys(&cache), vec![key.clone()]);
        let network_before = io.network_calls();
        io.events.borrow_mut().clear();

        remove_with(&io, &cache, &key).unwrap();
        assert_eq!(*io.events.borrow(), [format!("delete {PREFIX}{key}")]);
        assert_eq!(
            io.network_calls(),
            network_before,
            "remove makes no network call"
        );
        assert_eq!(io.artifact_calls.get(), 0);
        assert_eq!(
            io.items.borrow().keys().cloned().collect::<Vec<_>>(),
            vec![other],
            "only that credential is gone"
        );
        assert!(cached_keys(&cache).is_empty());
        // Already gone counts as removed.
        remove_with(&io, &cache, &key).unwrap();

        // A later poll no longer has a token for the removed account.
        let failure = fetch_captured_with(&io, &cache, &key, "a", now())
            .await
            .unwrap_err();
        assert!(matches!(
            failure,
            ProviderFetchFailure::Terminal { ref display } if display == CAPTURED_ITEM_MISSING
        ));
    }

    /// A Code Assist 401 on a captured account asks for a new capture, not
    /// for an Antigravity re-login, which would sign in the wrong account.
    /// Other terminal failures pass through unchanged.
    #[tokio::test]
    async fn a_captured_401_asks_for_a_new_capture() {
        let key = captured_key("sub-a");
        for (from, to) in [
            (ANTIGRAVITY_AUTH_EXPIRED, CAPTURED_AUTH_EXPIRED),
            ("some other failure", "some other failure"),
        ] {
            let mut io = FakeIo::new("captured-401");
            io.items
                .borrow_mut()
                .insert(key.clone(), stored_value("1//rt-a", AUD, &secret('a')));
            io.quota_terminal = Some(from);
            let failure = fetch_captured_with(&io, &new_token_cache(), &key, "a", now())
                .await
                .unwrap_err();
            assert!(
                matches!(failure, ProviderFetchFailure::Terminal { ref display } if display == to),
                "{from} -> {to}"
            );
        }
    }

    /// Acceptance 5: every capture failure is a fixed code: seeded with
    /// sentinel token, sub, email and error_description values, none of them
    /// reaches the FFI JSON.
    #[tokio::test]
    async fn capture_failures_carry_no_secret_or_identity() {
        const SENTINELS: [&str; 5] = [
            "SENTINELTOKEN",
            "SENTINELSUB",
            "SENTINELMAIL",
            "SENTINELDESC",
            "SENTINELOTHERSUB",
        ];
        let login = || {
            agy_item(
                "1//SENTINELTOKEN",
                Some(claims("SENTINELSUB", AUD, "SENTINELMAIL@example.com")),
            )
        };
        let mut cases: Vec<FakeIo> = Vec::new();

        cases.push(FakeIo::new("sentinel-absent"));
        let mut io = FakeIo::new("sentinel-unreadable");
        io.agy_item = Some(b"{\"token\":\"SENTINELTOKEN\"".to_vec());
        cases.push(io);
        let mut io = FakeIo::new("sentinel-identity");
        io.agy_item = Some(agy_item(
            "1//SENTINELTOKEN",
            Some(json!({ "sub": "SENTINELSUB", "email": "SENTINELMAIL@example.com" })),
        ));
        cases.push(io);
        let mut io = FakeIo::new("sentinel-client");
        io.agy_item = Some(login());
        cases.push(io);
        let mut io = FakeIo::new("sentinel-rejected");
        io.agy_item = Some(login());
        io.artifacts = artifacts_where_positional_pick_differs();
        io.token = Box::new(|_, _| {
            Ok((
                400,
                r#"{"error":"invalid_grant","error_description":"SENTINELDESC 1//SENTINELTOKEN"}"#
                    .to_string(),
            ))
        });
        cases.push(io);
        let mut io = FakeIo::new("sentinel-wrong-client");
        io.agy_item = Some(login());
        io.artifacts = artifacts_where_positional_pick_differs();
        io.token = Box::new(|_, _| {
            Ok((
                401,
                r#"{"error":"invalid_client","error_description":"SENTINELDESC"}"#.to_string(),
            ))
        });
        cases.push(io);
        let mut io = FakeIo::new("sentinel-transient");
        io.agy_item = Some(login());
        io.artifacts = artifacts_where_positional_pick_differs();
        io.token = Box::new(|_, _| {
            Err(ProviderFetchFailure::transient(
                "SENTINELDESC",
                None,
                SafeTransportDiagnostic::server_error(503),
            ))
        });
        cases.push(io);
        let mut io = FakeIo::new("sentinel-mismatch");
        io.agy_item = Some(login());
        io.artifacts = artifacts_where_positional_pick_differs();
        io.token = Box::new(|_, _| {
            Ok(token_ok(
                "ya29.SENTINELTOKEN",
                json!({ "id_token": jwt(json!({ "sub": "SENTINELOTHERSUB" })) }),
            ))
        });
        cases.push(io);
        let mut io = FakeIo::new("sentinel-write");
        io.agy_item = Some(login());
        io.artifacts = artifacts_where_positional_pick_differs();
        io.write_fails = true;
        cases.push(io);

        let mut codes = Vec::new();
        for io in &cases {
            let error = capture_with(io).await.expect_err("every case fails");
            let ffi = json!({ "ok": false, "err": error.code() }).to_string();
            let debug = format!("{error:?}");
            for sentinel in SENTINELS {
                assert!(
                    !ffi.contains(sentinel) && !debug.contains(sentinel),
                    "{ffi}"
                );
            }
            codes.push(error.code());
        }
        assert_eq!(
            codes,
            vec![
                "agy_not_signed_in",
                "agy_login_unreadable",
                "agy_login_missing_identity",
                "oauth_client_not_found",
                "refresh_rejected",
                "oauth_client_rejected",
                "refresh_unreachable",
                "account_mismatch",
                "keychain_write_failed",
            ]
        );

        // The automatic path's own codes carry nothing either, and neither
        // does a successful outcome's FFI shape beyond the key and label.
        let mut io = FakeIo::new("sentinel-auto-mismatch");
        io.agy_item = Some(login());
        io.artifacts = artifacts_where_positional_pick_differs();
        let error = auto_capture_with(&io, &[]).await.unwrap_err();
        assert_eq!(error.code(), "account_mismatch");
        for sentinel in SENTINELS {
            assert!(!format!("{error:?}").contains(sentinel));
        }
    }

    #[test]
    fn registry_accepts_only_hex_keys_and_reports_by_index() {
        let _guard = CAPTURED_ACCOUNTS_TEST_LOCK
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let key_a = captured_key("sub-a");
        let key_b = captured_key("sub-b");
        let raw = json!([
            { "key": key_a, "label": "a@example.com" },
            { "key": "sub-raw value", "label": "x" },
            { "key": key_a, "label": "dup" },
            { "label": "no key" },
            "not an object",
            { "key": key_b, "label": "b@example.com" },
        ])
        .to_string();
        let result = set_captured_accounts_from_json(&raw).unwrap();
        assert_eq!(result["registeredCount"], 2);
        assert_eq!(
            result["rejected"],
            json!([
                { "index": 1, "reason": "invalid key" },
                { "index": 2, "reason": "duplicate key" },
                { "index": 3, "reason": "invalid entry" },
                { "index": 4, "reason": "invalid entry" },
            ])
        );
        assert!(
            !result.to_string().contains("sub-raw"),
            "a rejected key is never echoed"
        );
        assert_eq!(
            captured_accounts(),
            vec![
                CapturedAccount {
                    key: key_a.clone(),
                    label: "a@example.com".to_string()
                },
                CapturedAccount {
                    key: key_b,
                    label: "b@example.com".to_string()
                },
            ]
        );

        assert_eq!(
            set_captured_accounts_from_json("{not json SENTINEL").unwrap_err(),
            "invalid_accounts_json"
        );
        assert_eq!(
            captured_accounts().len(),
            2,
            "malformed JSON changes nothing"
        );
        set_captured_accounts_from_json("[]").unwrap();
        assert!(captured_accounts().is_empty());
    }

    // ── automatic capture ──

    fn auto_io(tag: &str) -> FakeIo {
        let mut io = FakeIo::new(tag);
        io.agy_item = Some(agy_item(
            "1//rt-a",
            Some(claims("sub-a", AUD, "stored@example.com")),
        ));
        io.artifacts = artifacts_where_positional_pick_differs();
        io.token = Box::new(|_, _| {
            Ok(token_ok(
                "ya29.a",
                json!({ "id_token": jwt(json!({ "sub": "sub-a", "email": "fresh@example.com" })) }),
            ))
        });
        io
    }

    #[tokio::test]
    async fn auto_capture_writes_once_after_the_refresh() {
        let io = auto_io("auto-captured");
        let key = captured_key("sub-a");
        assert_eq!(
            auto_capture_with(&io, &[]).await,
            Ok(AutoCaptured::Captured(CapturedAccount {
                key: key.clone(),
                label: "fresh@example.com".to_string(),
            })),
            "the label is Google's answer, not the stored id_token"
        );
        let writes = io.writes();
        assert_eq!(writes.len(), 1);
        assert_eq!(writes[0].0, format!("{PREFIX}{key}"));
        assert_eq!(writes[0].1, WIN32_CRED_TYPE_GENERIC);
        assert_eq!(writes[0].2, WIN32_CRED_PERSIST_LOCAL_MACHINE);
        assert_eq!(io.token_calls.borrow().len(), 1);
        assert_eq!(
            io.events.borrow().last().unwrap(),
            &format!("write {PREFIX}{key}")
        );
        assert_eq!(decode_value(&io.items.borrow()[&key]).0, "1//rt-a");
    }

    /// Acceptance 2: a removed key makes no request and no write.
    #[tokio::test]
    async fn auto_capture_skips_a_removed_key_before_any_request() {
        let key = captured_key("sub-a");
        let other = captured_key("sub-b");
        let io = auto_io("auto-removed");
        assert_eq!(
            auto_capture_with(&io, &[other.clone(), key.clone()]).await,
            Ok(AutoCaptured::SkippedRemoved)
        );
        assert_eq!(io.network_calls(), 0);
        assert_eq!(io.artifact_calls.get(), 0);
        assert!(io.writes().is_empty());
        assert_eq!(io.reads_of_captured_items(), 0);

        // Control: another account's removal does not skip this one.
        let io = auto_io("auto-removed-control");
        assert!(matches!(
            auto_capture_with(&io, &[other]).await,
            Ok(AutoCaptured::Captured(_))
        ));
        assert_eq!(io.writes().len(), 1);
    }

    /// Acceptance 2: an identical stored refresh token is `Unchanged` with no
    /// scan, request or write.
    #[tokio::test]
    async fn auto_capture_of_a_stored_token_does_nothing() {
        let key = captured_key("sub-a");
        let io = auto_io("auto-unchanged");
        io.items
            .borrow_mut()
            .insert(key.clone(), stored_value("1//rt-a", AUD, &secret('a')));
        assert_eq!(
            auto_capture_with(&io, &[]).await,
            Ok(AutoCaptured::Unchanged(CapturedAccount {
                key: key.clone(),
                label: "stored@example.com".to_string(),
            }))
        );
        assert_eq!(io.artifact_calls.get(), 0, "no client scan");
        assert_eq!(io.network_calls(), 0, "no request");
        assert!(io.writes().is_empty(), "no write");

        // Control: a different stored token is a new login and is captured.
        let io = auto_io("auto-unchanged-control");
        io.items
            .borrow_mut()
            .insert(key.clone(), stored_value("1//rt-old", AUD, &secret('a')));
        assert!(matches!(
            auto_capture_with(&io, &[]).await,
            Ok(AutoCaptured::Captured(_))
        ));
        assert_eq!(io.writes().len(), 1);
        assert_eq!(decode_value(&io.items.borrow()[&key]).0, "1//rt-a");
    }

    /// Acceptance 1: the automatic path requires Google's `id_token` with the
    /// same `sub`; missing or different writes nothing. The manual path still
    /// accepts a response without one (macOS parity).
    #[tokio::test]
    async fn auto_capture_requires_googles_id_token_for_the_same_sub() {
        for (tag, extra) in [
            ("auto-no-id-token", json!({})),
            (
                "auto-other-sub",
                json!({ "id_token": jwt(json!({ "sub": "sub-b" })) }),
            ),
            ("auto-bad-id-token", json!({ "id_token": "not-a-jwt" })),
        ] {
            let mut io = auto_io(tag);
            let extra = extra.clone();
            io.token = Box::new(move |_, _| Ok(token_ok("ya29.a", extra.clone())));
            assert_eq!(
                auto_capture_with(&io, &[]).await,
                Err(CaptureError::AccountMismatch),
                "{tag}"
            );
            assert!(io.writes().is_empty(), "{tag}");
            assert!(io.items.borrow().is_empty(), "{tag}");
        }
        let mut io = auto_io("manual-no-id-token");
        io.token = Box::new(|_, _| Ok(token_ok("ya29.a", json!({}))));
        assert!(capture_with(&io).await.is_ok());
    }

    /// Acceptance 2: agy not found is `not_signed_in`, any other read failure
    /// is `paused`; neither reaches the network or a write.
    #[tokio::test]
    async fn auto_capture_pauses_on_any_agy_read_but_success_or_not_found() {
        let mut io = auto_io("auto-paused");
        io.agy_read = Some(AgyRead::Failed);
        assert_eq!(auto_capture_with(&io, &[]).await, Err(CaptureError::Paused));
        assert_eq!(io.network_calls(), 0);
        assert!(io.writes().is_empty());

        let mut io = auto_io("auto-not-signed-in");
        io.agy_read = Some(AgyRead::NotFound);
        assert_eq!(
            auto_capture_with(&io, &[]).await,
            Err(CaptureError::NotSignedIn)
        );
        assert_eq!(io.cred_calls.borrow().len(), 1);
        assert_eq!(io.network_calls(), 0);
        assert_eq!(
            [
                CaptureError::Paused.code(),
                CaptureError::NotSignedIn.code()
            ],
            ["paused", "not_signed_in"]
        );

        // The manual path reports either as not signed in.
        for read in [AgyRead::Failed, AgyRead::NotFound] {
            let mut io = auto_io("manual-agy-read");
            io.agy_read = Some(read);
            assert_eq!(
                capture_with(&io).await.err(),
                Some(CaptureError::AgyNotSignedIn)
            );
        }
    }

    /// Acceptance 9. The FFI marker (`login_marker_with`, which
    /// `tb_antigravity_login_marker` calls through `SystemCapturedIo`) and
    /// `agy_login_marker` are one function. W7b: add the agy snapshot's
    /// stamped marker to the same byte-equality assertion here.
    #[test]
    fn agy_login_marker_is_one_function_for_every_path() {
        const FILETIME: u64 = 134_037_498_000_000_000;
        let mut io = FakeIo::new("marker");
        io.agy_last_written = Ok(Some(FILETIME));
        let ffi_path = login_marker_with(&io);
        let function = agy_login_marker(Ok(Some(FILETIME)));
        assert_eq!(
            ffi_path.as_deref().map(str::as_bytes),
            Some(b"134037498000000000".as_slice())
        );
        assert_eq!(ffi_path, function);

        io.agy_last_written = Ok(None);
        assert_eq!(login_marker_with(&io).as_deref(), Some("absent"));
        io.agy_last_written = Err(());
        assert_eq!(login_marker_with(&io), None);
        assert!(
            io.cred_calls.borrow().is_empty(),
            "the marker reads no blob"
        );
        assert_eq!(io.network_calls(), 0);
    }

    #[test]
    fn quota_summary_maps_like_the_agy_route() {
        let now = DateTime::parse_from_rfc3339("2026-10-02T00:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        let body = json!({
            "groups": [
                { "displayName": "Gemini Models", "buckets": [
                    { "bucketId": "gemini-weekly", "displayName": "Weekly Limit Remaining",
                      "remainingFraction": 0.968718, "resetTime": "2026-10-08T18:46:28Z", "window": "weekly" },
                    { "bucketId": "gemini-5h", "displayName": "Five Hour Limit Remaining",
                      "remainingFraction": 0.8799, "resetTime": "2026-10-02T11:20:43Z", "window": "5h" }
                ]},
                { "displayName": "Claude and GPT models", "buckets": [
                    { "bucketId": "3p-weekly", "displayName": "Weekly Limit Remaining",
                      "remainingFraction": 1, "resetTime": "2026-10-09T07:58:14Z", "window": "weekly" },
                    { "displayName": "No id is skipped", "remainingFraction": 1 },
                    { "bucketId": "3p-5h", "displayName": "Five Hour Limit Remaining", "window": "5h" }
                ]}
            ]
        });
        let windows = windows_from_quota_summary(&body.to_string(), now);
        // The same buckets as agy's `/usage` JSON on Windows.
        let agy = json!({ "status": "SUCCESS", "command": { "name": "usage", "data": { "groups": [
            { "name": "Gemini Models", "buckets": [
                { "id": "gemini-weekly", "name": "Weekly Limit Remaining",
                  "remaining_fraction": 0.968718, "reset_time": "2026-10-08T18:46:28Z" },
                { "id": "gemini-5h", "name": "Five Hour Limit Remaining",
                  "remaining_fraction": 0.8799, "reset_time": "2026-10-02T11:20:43Z" } ] },
            { "name": "Claude and GPT models", "buckets": [
                { "id": "3p-weekly", "name": "Weekly Limit Remaining",
                  "remaining_fraction": 1.0, "reset_time": "2026-10-09T07:58:14Z" } ] }
        ] } } });
        let agy_windows = parse_agy_usage(agy.to_string().as_bytes(), now)
            .unwrap()
            .windows;
        assert_eq!(
            serde_json::to_value(&windows).unwrap(),
            serde_json::to_value(&agy_windows).unwrap(),
            "a captured card reads exactly like the agy route's"
        );
        assert_eq!(
            windows[0].label_for_test(),
            "Gemini Models · Weekly Limit Remaining"
        );
        assert_eq!(
            windows[1].pace_window_key_for_test(),
            Some("agy.gemini-5h.v1")
        );
        assert!(windows_from_quota_summary("not json", now).is_empty());
        assert!(windows_from_quota_summary("{}", now).is_empty());
    }

    /// The captured client never follows a redirect, which would resend the
    /// POST body (refresh token) or the bearer to the redirect target. Local
    /// listeners only.
    #[tokio::test]
    async fn the_captured_client_does_not_follow_redirects() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let target = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let target_url = format!("http://{}/stolen", target.local_addr().unwrap());
        let origin = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let origin_url = format!("http://{}/token", origin.local_addr().unwrap());
        let server = tokio::spawn(async move {
            let (mut stream, _) = origin.accept().await.unwrap();
            let mut buf = [0_u8; 4096];
            let _ = stream.read(&mut buf).await.unwrap();
            let response = format!(
                "HTTP/1.1 307 Temporary Redirect\r\nlocation: {target_url}\r\ncontent-length: 0\r\nconnection: close\r\n\r\n"
            );
            stream.write_all(response.as_bytes()).await.unwrap();
            // Answer a followed redirect too, so a regression fails on the
            // assertions below rather than on a dropped connection.
            let Ok(Ok((mut stream, _))) =
                tokio::time::timeout(std::time::Duration::from_millis(300), target.accept()).await
            else {
                return false;
            };
            let _ = stream.read(&mut buf).await;
            let _ = stream
                .write_all(b"HTTP/1.1 200 OK\r\ncontent-length: 0\r\nconnection: close\r\n\r\n")
                .await;
            true
        });
        let response = captured_http_client()
            .unwrap()
            .post(&origin_url)
            .body("refresh_token=SENTINEL")
            .send()
            .await
            .unwrap();
        assert_eq!(response.status().as_u16(), 307);
        assert!(
            !server.await.unwrap(),
            "the redirect target was never contacted"
        );
    }
}
