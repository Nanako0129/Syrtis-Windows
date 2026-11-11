//! Hermetic (Windows only: storage is `agent_storage_windows`): temp SQLite
//! fixtures, temp dirs, an in-process HTTP server on 127.0.0.1. No real
//! `%APPDATA%` (the data root is a test seam that is unset by default), no
//! real Cursor database, no network.
//!
//! Ported from macOS #487 `cursor_sync/tests.rs`, plus the Windows Plan's B1
//! acceptance: the W1 dir rules, W2 storage/NTFS cases, W7 cleanup errors,
//! and the W5 takeover through `LocalSourceContext::process()` and the
//! primary window (env-cleared child processes, as `roots_acceptance`).

use super::*;
use base64::Engine as _;
use std::io::Read as _;
use std::net::{TcpListener, TcpStream};
use std::sync::Arc;

const USER: &str = "user_CANARYuser0123456789AB";
const OTHER_USER: &str = "user_OTHERuser01234567890CD";
const THIRD_USER: &str = "user_THIRDuser01234567890EF";
const SIGNATURE: &str = "CANARYsignatureXYZ";
const OWNING_CANARY: &str = "OWNINGUSERCANARY42";
const KEY: [u8; 32] = [7; 32];

/// A temp directory removed on drop.
struct TempDir(PathBuf);

impl TempDir {
    fn new() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        // Short: the deepest fixture (`.secure` at every level plus a usage
        // file name) must stay under MAX_PATH, as production paths do.
        let path = std::env::temp_dir().join(format!(
            "tbcs-{}-{}-{:x}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .subsec_nanos()
        ));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn jwt(sub: &str, exp: Option<f64>) -> String {
    let mut claims = serde_json::json!({ "sub": sub, "type": "session" });
    if let Some(exp) = exp {
        claims["exp"] = exp.into();
    }
    let encode = |v: &str| base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(v);
    format!(
        "{}.{}.{SIGNATURE}",
        encode(r#"{"alg":"HS256"}"#),
        encode(&claims.to_string())
    )
}

fn valid_token() -> String {
    jwt(&format!("auth0|{USER}"), Some(now_secs() + 3600.0))
}

fn state_db(dir: &Path, rows: &[(&str, &str)]) -> PathBuf {
    let path = dir.join("state.vscdb");
    let conn = rusqlite::Connection::open(&path).unwrap();
    conn.execute_batch("CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT)")
        .unwrap();
    for (key, value) in rows {
        conn.execute(
            "INSERT INTO ItemTable (key, value) VALUES (?1, ?2)",
            rusqlite::params![key, value],
        )
        .unwrap();
    }
    path
}

fn signed_in_db(dir: &Path, token: &str) -> PathBuf {
    state_db(
        dir,
        &[
            ("cursorAuth/accessToken", &format!("\"{token}\"")),
            ("glass.lastSignedInAuthId", &format!("glass-{USER}")),
        ],
    )
}

// --- local server ---------------------------------------------------------

#[derive(Clone)]
enum Reply {
    Raw(Vec<u8>),
    Delayed(Duration, Vec<u8>),
    /// Headers sent, body never finishes.
    Hang,
}

#[derive(Clone, Debug)]
struct Seen {
    head: String,
    body: String,
}

fn http(status: &str, content_type: &str, body: &str) -> Reply {
    Reply::Raw(
        format!(
            "HTTP/1.1 {status}\r\nContent-Type: {content_type}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        )
        .into_bytes(),
    )
}

fn json_ok(body: &str) -> Reply {
    http("200 OK", "application/json", body)
}

/// The Billing V2 URL on the same loopback server (which answers in order,
/// whatever the path), so a test can tell the two endpoints apart.
fn v2_url(url: &str) -> String {
    url.replace("/api/usage", "/api/dashboard/v2/query-billing-items")
}

fn read_request(stream: &mut TcpStream) -> Seen {
    let mut buf = Vec::new();
    let mut chunk = [0u8; 4096];
    let head_end = loop {
        if let Some(i) = buf.windows(4).position(|w| w == b"\r\n\r\n") {
            break i + 4;
        }
        let n = stream.read(&mut chunk).unwrap_or(0);
        if n == 0 {
            break buf.len();
        }
        buf.extend_from_slice(&chunk[..n]);
    };
    let head = String::from_utf8_lossy(&buf[..head_end]).to_string();
    let length = head
        .lines()
        .find_map(|l| {
            let (k, v) = l.split_once(':')?;
            k.eq_ignore_ascii_case("content-length")
                .then(|| v.trim().parse::<usize>().ok())?
        })
        .unwrap_or(0);
    while buf.len() < head_end + length {
        let n = stream.read(&mut chunk).unwrap_or(0);
        if n == 0 {
            break;
        }
        buf.extend_from_slice(&chunk[..n]);
    }
    Seen {
        head,
        body: String::from_utf8_lossy(&buf[head_end..]).to_string(),
    }
}

/// Serves `replies` in order (then 404s) forever; records every request.
fn serve(replies: Vec<Reply>) -> (String, Arc<Mutex<Vec<Seen>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let url = format!("http://{}/api/usage", listener.local_addr().unwrap());
    let seen = Arc::new(Mutex::new(Vec::new()));
    let log = seen.clone();
    std::thread::spawn(move || {
        let mut replies = replies.into_iter();
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { continue };
            let request = read_request(&mut stream);
            log.lock().unwrap().push(request);
            match replies.next() {
                Some(Reply::Raw(bytes)) => {
                    let _ = stream.write_all(&bytes);
                }
                Some(Reply::Delayed(delay, bytes)) => {
                    std::thread::sleep(delay);
                    let _ = stream.write_all(&bytes);
                }
                Some(Reply::Hang) => {
                    let _ = stream.write_all(
                        b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n{\"usageEventsDisplay\":[",
                    );
                    std::thread::sleep(Duration::from_secs(3));
                }
                None => {
                    let _ = stream.write_all(
                        b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    );
                }
            }
        }
    });
    (url, seen)
}

fn event(id: &str) -> serde_json::Value {
    serde_json::json!({
        "timestamp": "1788171994001",
        "model": "claude-4-sonnet",
        "kind": "USAGE_EVENT_KIND_INCLUDED_IN_PRO",
        "tokenUsage": {"inputTokens": 10, "outputTokens": 5, "cacheReadTokens": 2,
                       "totalCents": 1.25, "cacheWriteTokens": 9},
        "chargedCents": 1.25,
        "usageBasedCosts": "$0.01",
        "requestsCosts": 0,
        "cursorTokenFee": 0,
        "isChargeable": true,
        "isTokenBasedCall": true,
        "isHeadless": false,
        "conversationId": id,
        "owningUser": OWNING_CANARY,
        "serviceAccountId": "SERVICEACCOUNTCANARY",
        "subscriptionProductId": "SUBPRODUCTCANARY",
        "customSubscriptionName": "CUSTOMSUBCANARY",
    })
}

fn page(total: usize, ids: &[&str]) -> String {
    serde_json::json!({
        "totalUsageEventsCount": total,
        "usageEventsDisplay": ids.iter().map(|id| event(id)).collect::<Vec<_>>(),
    })
    .to_string()
}

fn limits() -> Limits {
    Limits {
        budget: Duration::from_secs(20),
        page_timeout: Duration::from_millis(700),
        page_size: 2,
        max_pages: 10,
        max_v2_pages: 64,
        max_body_bytes: 1024 * 1024,
        explicit: false,
    }
}

fn stem(user: &str) -> Option<String> {
    crate::agent_account_scope::keyed_digest_hex(&KEY, FILE_NAME_DOMAIN, user.as_bytes()).ok()
}

fn expected_file(dir: &Path) -> PathBuf {
    dir.join(format!("usage.{}.json", stem(USER).unwrap()))
}

/// The test target: a local server, the fixed key, the real drive-type
/// query (fixtures live on the system drive, a fixed one). Consent defaults to
/// granted and the key loader to one that must never run — a plaintext test
/// touches neither; sealed tests build their own with `sealed_target`.
fn target<'a>(db: &'a Path, url: &'a str) -> Target<'a> {
    Target {
        db_path: db,
        url,
        billing_url: url,
        https_only: false,
        file_stem: &stem,
        drive_type: &cursor_desktop::drive_type,
        consent: &allow_consent,
        load_seal_key: &unexpected_seal_key,
    }
}

fn allow_consent() -> bool {
    true
}

/// The default loader: a plaintext test must never reach a key load.
fn unexpected_seal_key(_: &Path) -> Result<Box<dyn SealKey>, ()> {
    panic!("a key load happened where no sealed token was expected");
}

/// One default-profile walk into `dir`.
fn run_with(
    db: &Path,
    dir: &Path,
    url: &str,
    limits: Limits,
    enabled: &dyn Fn() -> bool,
) -> Outcome {
    let slot = Slot {
        id: DEFAULT_PROFILE_ID.to_string(),
        root: None,
        dir: Some(dir.to_path_buf()),
    };
    let billing = v2_url(url);
    crate::RUNTIME.block_on(run(
        &Target {
            billing_url: &billing,
            ..target(db, url)
        },
        &slot,
        dir,
        limits,
        enabled,
        &mut |_, _| None,
    ))
}

struct Fixture {
    db: PathBuf,
    dir: PathBuf,
    token: String,
    _tmp: TempDir,
}

fn fixture_with_token(token: &str) -> Fixture {
    let tmp = TempDir::new();
    let db = signed_in_db(tmp.path(), token);
    let dir = tmp.path().join("sync");
    Fixture {
        db,
        dir,
        token: token.to_string(),
        _tmp: tmp,
    }
}

fn fixture() -> Fixture {
    fixture_with_token(&valid_token())
}

fn stop(state: State, reason: Option<&'static str>) -> Outcome {
    Outcome::stopped(Stop::new(state, reason))
}

/// A walk that wrote new bytes.
fn ok(events: usize) -> Outcome {
    Outcome {
        stop: Stop::new(State::Ok, None),
        events,
        duplicate_of: None,
        changed: true,
        cleanup_failed: false,
        refused: false,
        dropped: 0,
    }
}

/// A file the way Syrtis writes one: secure dir, secure new file.
fn write_secure(path: &Path, bytes: &[u8]) {
    drop(secure::ensure_dir(path.parent().unwrap()).unwrap());
    let mut file = secure::create_new(path).unwrap();
    file.write_all(bytes).unwrap();
    file.sync_all().unwrap();
}

/// A complete file from an earlier sync, to prove failures leave it alone.
fn seed_old_file(dir: &Path) -> Vec<u8> {
    let old = br#"{"totalUsageEventsCount":0,"usageEventsDisplay":[]}"#.to_vec();
    write_secure(&expected_file(dir), &old);
    old
}

fn names(dir: &Path) -> Vec<String> {
    std::fs::read_dir(dir)
        .unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
        .collect()
}

// --- walk ------------------------------------------------------------------

#[test]
fn walks_every_page_and_writes_the_complete_history() {
    let f = fixture();
    let (url, seen) = serve(vec![
        json_ok(&page(3, &["a", "b"])),
        json_ok(&page(3, &["c"])),
    ]);
    let outcome = run_with(&f.db, &f.dir, &url, limits(), &|| true);
    assert_eq!(outcome, ok(3));

    let seen = seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 2);
    for (i, request) in seen.iter().enumerate() {
        let body: serde_json::Value = serde_json::from_str(&request.body).unwrap();
        assert_eq!(body["page"], i as u64 + 1);
        assert_eq!(body["pageSize"], 2);
        assert_eq!(body["startDate"], "0");
        assert!(body["endDate"].as_str().unwrap().parse::<u128>().is_ok());
        assert!(body.get("teamId").is_none(), "no teamId (#1397)");
        assert!(request.head.starts_with("POST /api/usage "));
        assert_eq!(
            header(&request.head, "cookie").as_deref(),
            Some(format!("WorkosCursorSessionToken={USER}%3A%3A{}", f.token).as_str())
        );
        let head = request.head.to_ascii_lowercase();
        assert!(head.contains("origin: https://cursor.com\r\n"));
        assert!(head.contains("referer: https://cursor.com/dashboard\r\n"));
        assert!(head.contains("content-type: application/json\r\n"));
    }

    let written: serde_json::Value =
        serde_json::from_slice(&std::fs::read(expected_file(&f.dir)).unwrap()).unwrap();
    assert_eq!(written["totalUsageEventsCount"], 3);
    let ids: Vec<_> = written["usageEventsDisplay"]
        .as_array()
        .unwrap()
        .iter()
        .map(|e| e["conversationId"].as_str().unwrap())
        .collect();
    assert_eq!(ids, ["a", "b", "c"]);
}

fn header(head: &str, name: &str) -> Option<String> {
    head.lines().find_map(|line| {
        let (key, value) = line.split_once(':')?;
        key.eq_ignore_ascii_case(name)
            .then(|| value.trim().to_string())
    })
}

#[test]
fn stops_at_the_reported_total_without_an_extra_page() {
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(2, &["a", "b"]))]);
    let outcome = run_with(&f.db, &f.dir, &url, limits(), &|| true);
    assert_eq!(outcome.events, 2);
    assert_eq!(seen.lock().unwrap().len(), 1);
}

#[test]
fn byte_cap_is_an_error_and_keeps_the_old_file() {
    let small = Limits {
        max_body_bytes: 2000,
        ..limits()
    };
    let big = page(1, &[&"x".repeat(4000)]);
    // Content-Length precheck, unknown-length streaming, and the cap being
    // cumulative across pages (page 1 fits, page 2 does not).
    let streamed = Reply::Raw(
        format!(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{big}"
        )
        .into_bytes(),
    );
    let page1 = page(4, &["a", "b"]);
    assert!(page1.len() < 2000 && page1.len() * 2 > 2000);
    for replies in [
        vec![json_ok(&big)],
        vec![streamed],
        vec![json_ok(&page1), json_ok(&page1)],
    ] {
        let f = fixture();
        let old = seed_old_file(&f.dir);
        let (url, _) = serve(replies);
        let outcome = run_with(&f.db, &f.dir, &url, small, &|| true);
        assert_eq!(outcome, stop(State::Error, Some("body_too_large")));
        assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), old);
    }
    // Control: the same page under the default cap is accepted.
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&big)]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
}

#[test]
fn timeout_is_partial_and_keeps_the_old_file() {
    let f = fixture();
    let old = seed_old_file(&f.dir);
    let (url, seen) = serve(vec![json_ok(&page(4, &["a", "b"])), Reply::Hang]);
    let outcome = run_with(&f.db, &f.dir, &url, limits(), &|| true);
    assert_eq!(outcome, stop(State::Partial, Some("timeout")));
    assert_eq!(seen.lock().unwrap().len(), 2);
    assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), old);
}

#[test]
fn spent_budget_is_partial() {
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(4, &["a", "b"]))]);
    let outcome = run_with(
        &f.db,
        &f.dir,
        &url,
        Limits {
            budget: Duration::ZERO,
            ..limits()
        },
        &|| true,
    );
    assert_eq!(outcome, stop(State::Partial, Some("budget_exhausted")));
    assert!(seen.lock().unwrap().is_empty());
}

#[test]
fn redirect_is_expired_and_never_followed() {
    let f = fixture();
    let (elsewhere, elsewhere_seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let (url, seen) = serve(vec![Reply::Raw(
        format!(
            "HTTP/1.1 307 Temporary Redirect\r\nLocation: {elsewhere}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        )
        .into_bytes(),
    )]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true),
        stop(State::Expired, None)
    );
    assert_eq!(seen.lock().unwrap().len(), 1);
    std::thread::sleep(Duration::from_millis(100));
    assert!(
        elsewhere_seen.lock().unwrap().is_empty(),
        "the redirect was followed"
    );
}

#[test]
fn http_statuses_map_to_states() {
    let cases: Vec<(Reply, Outcome)> = vec![
        (
            http("401 Unauthorized", "application/json", "{}"),
            stop(State::Expired, None),
        ),
        (
            http(
                "403 Forbidden",
                "application/json",
                r#"{"error":"not_authenticated"}"#,
            ),
            stop(State::Expired, None),
        ),
        (
            http("403 Forbidden", "text/html", "<html>blocked</html>"),
            stop(State::Error, Some("forbidden_non_json")),
        ),
        (
            http("429 Too Many Requests", "application/json", "{}"),
            stop(State::Error, Some("rate_limited")),
        ),
        (
            http("500 Internal Server Error", "text/plain", "x"),
            stop(State::Error, Some("server_error")),
        ),
        (
            http("503 Service Unavailable", "text/plain", "x"),
            stop(State::Error, Some("server_error")),
        ),
        (
            http("404 Not Found", "text/plain", "x"),
            stop(State::Error, Some("http_status")),
        ),
        // Billing V2: an object without `usageEventsDisplay` now switches
        // (D1a); these two still do not.
        (
            json_ok(r#"{"totalUsageEventsCount":1,"usageEventsDisplay":"x"}"#),
            stop(State::Error, Some("unexpected_response")),
        ),
        (
            json_ok("[]"),
            stop(State::Error, Some("unexpected_response")),
        ),
        (
            json_ok("<html>"),
            stop(State::Error, Some("unexpected_response")),
        ),
    ];
    for (reply, expected) in cases {
        let f = fixture();
        let old = seed_old_file(&f.dir);
        let (url, seen) = serve(vec![reply]);
        assert_eq!(run_with(&f.db, &f.dir, &url, limits(), &|| true), expected);
        assert_eq!(seen.lock().unwrap().len(), 1);
        assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), old);
    }
}

#[test]
fn unreachable_server_is_offline() {
    let f = fixture();
    let port = TcpListener::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let url = format!("http://127.0.0.1:{port}/api/usage");
    // Windows retries a refused loopback connect for about 2 s before
    // failing it, so a 700 ms page timeout would report `timeout` (measured
    // on 188); the walk is given room to see the refusal.
    let patient = Limits {
        page_timeout: Duration::from_secs(10),
        ..limits()
    };
    assert_eq!(
        run_with(&f.db, &f.dir, &url, patient, &|| true),
        stop(State::Offline, Some("unreachable"))
    );
}

// --- gates before any send ---------------------------------------------------

#[test]
fn switch_off_sends_nothing_and_on_sends() {
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| false),
        stop(State::Disabled, None)
    );
    assert!(seen.lock().unwrap().is_empty());
    // Off is decided before the login is even read: an absent database still
    // reports `disabled`, not `notSignedIn`.
    assert_eq!(
        run_with(&f.dir.join("absent.vscdb"), &f.dir, &url, limits(), &|| {
            false
        }),
        stop(State::Disabled, None)
    );
    // Control: the same fixture with the switch on does reach the server.
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    assert_eq!(seen.lock().unwrap().len(), 1);
}

#[test]
fn switch_turned_off_mid_walk_stops_before_the_next_page() {
    let f = fixture();
    let (url, seen) = serve(vec![
        json_ok(&page(4, &["a", "b"])),
        json_ok(&page(4, &["c", "d"])),
    ]);
    let calls = std::cell::Cell::new(0);
    // run's own check, then page 1's; off from page 2 on.
    let enabled = || {
        calls.set(calls.get() + 1);
        calls.get() <= 2
    };
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &enabled),
        stop(State::Disabled, None)
    );
    assert_eq!(seen.lock().unwrap().len(), 1);
    assert!(!expected_file(&f.dir).exists());
}

/// The switch is also re-read under the commit lock: a disable that lands
/// after the last page and before the write leaves no file.
#[test]
fn switch_turned_off_after_the_last_page_writes_nothing() {
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let calls = std::cell::Cell::new(0);
    // run's own check, page 1's, then the commit's.
    let enabled = || {
        calls.set(calls.get() + 1);
        calls.get() <= 2
    };
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &enabled),
        stop(State::Disabled, None)
    );
    assert_eq!(seen.lock().unwrap().len(), 1);
    assert!(!expected_file(&f.dir).exists());
}

#[test]
fn expired_or_unverifiable_token_sends_nothing() {
    for token in [
        jwt(&format!("auth0|{USER}"), Some(now_secs() - 10.0)),
        jwt(&format!("auth0|{USER}"), Some(now_secs() + 30.0)),
        jwt(&format!("auth0|{USER}"), None),
    ] {
        let f = fixture_with_token(&token);
        let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
        assert_eq!(
            run_with(&f.db, &f.dir, &url, limits(), &|| true),
            stop(State::Expired, None)
        );
        assert!(seen.lock().unwrap().is_empty());
    }
    // Control: an hour of validity is sent.
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    assert_eq!(seen.lock().unwrap().len(), 1);
}

#[test]
fn stored_account_that_differs_from_the_token_is_refused() {
    let token = valid_token();
    for rows in [
        vec![("glass.lastSignedInAuthId", format!("glass-{OTHER_USER}"))],
        vec![
            ("glass.lastSignedInAuthId", format!("glass-{USER}")),
            (
                "cursorAuth/cachedScopedProfile",
                format!(r#"{{"userId":"{OTHER_USER}"}}"#),
            ),
        ],
        // The every-occurrence `extract_user_id`: a short `user_id` key in
        // front of the other account's id must not hide it.
        vec![(
            "cursorAuth/cachedScopedProfile",
            format!(r#"{{"user_id":"x","userId":"{OTHER_USER}"}}"#),
        )],
    ] {
        let tmp = TempDir::new();
        let mut all: Vec<(&str, String)> = vec![("cursorAuth/accessToken", token.clone())];
        all.extend(rows.iter().map(|(k, v)| (*k, v.clone())));
        let refs: Vec<(&str, &str)> = all.iter().map(|(k, v)| (*k, v.as_str())).collect();
        let db = state_db(tmp.path(), &refs);
        let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
        let outcome = run_with(&db, &tmp.path().join("sync"), &url, limits(), &|| true);
        assert_eq!(outcome.stop, Stop::error("account_mismatch"));
        assert!(outcome.refused);
        assert!(seen.lock().unwrap().is_empty());
    }
    // Controls: matching stored ids, and no stored id at all, are both sent.
    for rows in [
        vec![(
            "cursorAuth/cachedScopedProfile",
            format!(r#"{{"userId":"{USER}"}}"#),
        )],
        vec![],
    ] {
        let tmp = TempDir::new();
        let mut all: Vec<(&str, String)> = vec![("cursorAuth/accessToken", token.clone())];
        all.extend(rows.iter().map(|(k, v)| (*k, v.clone())));
        let refs: Vec<(&str, &str)> = all.iter().map(|(k, v)| (*k, v.as_str())).collect();
        let db = state_db(tmp.path(), &refs);
        let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
        assert_eq!(
            run_with(&db, &tmp.path().join("sync"), &url, limits(), &|| true)
                .stop
                .state,
            State::Ok
        );
        assert_eq!(seen.lock().unwrap().len(), 1);
    }
}

#[test]
fn missing_login_is_not_signed_in() {
    let tmp = TempDir::new();
    let (url, seen) = serve(vec![]);
    for db in [tmp.path().join("absent.vscdb"), state_db(tmp.path(), &[])] {
        assert_eq!(
            run_with(&db, &tmp.path().join("sync"), &url, limits(), &|| true),
            stop(State::NotSignedIn, None)
        );
    }
    assert!(seen.lock().unwrap().is_empty());
}

// --- what is written ---------------------------------------------------------

/// Exactly what tokscale-core's Cursor JSON parser reads (`CursorUsageEvent`,
/// `CursorTokenUsage` at pin 8fc63ced).
const ALLOWED_EVENT_FIELDS: &[&str] = &[
    "conversationId",
    "timestamp",
    "model",
    "chargedCents",
    "tokenUsage",
];
const ALLOWED_TOKEN_FIELDS: &[&str] = &[
    "inputTokens",
    "outputTokens",
    "cacheReadTokens",
    "cacheWriteTokens",
    "totalCents",
];

#[test]
fn written_file_holds_only_the_parser_fields() {
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    let text = std::fs::read_to_string(expected_file(&f.dir)).unwrap();
    let written: serde_json::Value = serde_json::from_str(&text).unwrap();
    let mut top: Vec<_> = written.as_object().unwrap().keys().cloned().collect();
    top.sort();
    assert_eq!(top, ["totalUsageEventsCount", "usageEventsDisplay"]);
    let event = written["usageEventsDisplay"][0].as_object().unwrap();
    let mut keys: Vec<&str> = event.keys().map(String::as_str).collect();
    keys.sort();
    let mut allowed = ALLOWED_EVENT_FIELDS.to_vec();
    allowed.sort();
    // Exact: every allowed field the fixture carries survives (control), and
    // nothing else does.
    assert_eq!(keys, allowed);
    let mut token_keys: Vec<&str> = event["tokenUsage"]
        .as_object()
        .unwrap()
        .keys()
        .map(String::as_str)
        .collect();
    token_keys.sort();
    let mut allowed_tokens = ALLOWED_TOKEN_FIELDS.to_vec();
    allowed_tokens.sort();
    assert_eq!(token_keys, allowed_tokens);
    assert_eq!(event["tokenUsage"]["totalCents"], 1.25);
    assert_eq!(event["chargedCents"], 1.25);
    for forbidden in [
        "owningUser",
        "serviceAccountId",
        "subscriptionProductId",
        "customSubscriptionName",
        "kind",
        "usageBasedCosts",
        "requestsCosts",
        "cursorTokenFee",
        "isChargeable",
        "isTokenBasedCall",
        "isHeadless",
    ] {
        assert!(!text.contains(forbidden), "{forbidden}");
    }
}

/// The engine coerces numeric strings, so a page carrying one must still
/// sync (written back as a number); free text in a numeric field must not
/// reach the file.
#[test]
fn numeric_strings_are_accepted_and_free_text_is_refused() {
    let mut lenient = event("a");
    lenient["chargedCents"] = "2.5".into();
    lenient["tokenUsage"]["inputTokens"] = "10".into();
    let body = serde_json::json!({"totalUsageEventsCount": 1, "usageEventsDisplay": [lenient]});
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&body.to_string())]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    let written: serde_json::Value =
        serde_json::from_slice(&std::fs::read(expected_file(&f.dir)).unwrap()).unwrap();
    assert_eq!(written["usageEventsDisplay"][0]["chargedCents"], 2.5);
    assert_eq!(
        written["usageEventsDisplay"][0]["tokenUsage"]["inputTokens"],
        10.0
    );

    let mut junk = event("a");
    junk["chargedCents"] = "FREETEXTCANARY".into();
    let body = serde_json::json!({"totalUsageEventsCount": 1, "usageEventsDisplay": [junk]});
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&body.to_string())]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true),
        stop(State::Error, Some("unexpected_response"))
    );
    assert!(!expected_file(&f.dir).exists());
}

#[test]
fn temp_file_name_can_never_be_scanned_as_usage() {
    let name = temp_file_name();
    assert!(name.starts_with('.'));
    assert!(!name.starts_with("usage"));
    assert!(!is_sync_file_name(&name));
}

/// A complete walk drops superseded usage files and orphan temps (a crash
/// between create and replace), only Syrtis names; a failed walk drops
/// nothing.
#[test]
fn a_complete_walk_drops_other_usage_files_and_a_failed_one_does_not() {
    let f = fixture();
    let orphan = f.dir.join(format!("usage.{}.json", "a".repeat(64)));
    let orphan_temp = f.dir.join(format!("{TEMP_FILE_PREFIX}999-0"));
    let unrelated = f.dir.join("notes.txt");
    write_secure(&orphan, b"{}");
    write_secure(&orphan_temp, b"half");
    write_secure(&unrelated, b"keep");

    let (url, _) = serve(vec![http("500 Internal Server Error", "text/plain", "")]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Error
    );
    assert!(
        orphan.exists() && orphan_temp.exists(),
        "a failed walk must not clean up"
    );

    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    assert!(!orphan.exists());
    assert!(!orphan_temp.exists(), "orphan temp left behind");
    assert!(unrelated.exists());
    assert!(expected_file(&f.dir).exists());
    let mut left = names(&f.dir);
    left.sort();
    let mut expected = vec![
        LOCK_FILE_NAME.to_string(),
        "notes.txt".to_string(),
        expected_file(&f.dir)
            .file_name()
            .unwrap()
            .to_string_lossy()
            .into_owned(),
    ];
    expected.sort();
    assert_eq!(left, expected, "no temp file may remain");
}

#[test]
fn file_name_is_keyed_and_domain_separated() {
    let name = stem(USER).unwrap();
    assert_eq!(name.len(), 64);
    assert!(is_sync_file_name(&format!("usage.{name}.json")));
    assert_ne!(Some(name.clone()), stem(OTHER_USER));
    assert_ne!(
        Some(name),
        crate::agent_account_scope::keyed_digest_hex(&KEY, "other-domain", USER.as_bytes()).ok()
    );
}

// --- FFI-level: registry, single flight, canaries ---------------------------

fn lock() -> std::sync::MutexGuard<'static, ()> {
    TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner())
}

/// A registry local to the test, so a sync test never turns on the takeover
/// for other tests through the process-wide one.
fn registry(enabled: bool, dir: &Path) -> RwLock<Config> {
    RwLock::new(Config {
        enabled,
        dir: Some(dir.to_path_buf()),
        ..Config::default()
    })
}

fn sync_status(f: &Fixture, url: &str, registry: &RwLock<Config>) -> serde_json::Value {
    let source = || registry.read().unwrap().clone();
    let billing = v2_url(url);
    let target = Target {
        billing_url: &billing,
        ..target(&f.db, url)
    };
    sync_with(true, &target, &source, Some(limits())).0
}

#[test]
fn ffi_switch_off_sends_nothing_even_when_explicit() {
    let _guard = lock();
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_status(&f, &url, &registry(false, &f.dir));
    assert_eq!(status["state"], "disabled");
    assert!(seen.lock().unwrap().is_empty());
    // Control.
    let status = sync_status(&f, &url, &registry(true, &f.dir));
    assert_eq!(status["state"], "ok");
    assert_eq!(status["events"], 1);
    assert!(status["lastSuccessMs"].as_u64().unwrap() > 0);
    assert_eq!(seen.lock().unwrap().len(), 1);
}

#[test]
fn single_flight_second_caller_waits_and_shares_the_result() {
    let _guard = lock();
    let f = fixture();
    let (url, seen) = serve(vec![Reply::Delayed(
        Duration::from_millis(300),
        match json_ok(&page(1, &["a"])) {
            Reply::Raw(bytes) => bytes,
            _ => unreachable!(),
        },
    )]);
    let on = registry(true, &f.dir);
    let first = std::thread::scope(|scope| {
        let first = scope.spawn(|| sync_status(&f, &url, &on));
        let started = Instant::now();
        while seen.lock().unwrap().is_empty() {
            assert!(started.elapsed() < Duration::from_secs(5));
            std::thread::sleep(Duration::from_millis(5));
        }
        let second = sync_status(&f, &url, &on);
        let first = first.join().unwrap();
        assert_eq!(second, first);
        first
    });
    assert_eq!(first["state"], "ok");
    assert_eq!(
        seen.lock().unwrap().len(),
        1,
        "the second caller started a walk"
    );
}

#[test]
fn canaries_never_reach_the_file_names_status_or_errors() {
    let _guard = lock();
    let token = valid_token();
    let canaries = [
        token.as_str(),
        SIGNATURE,
        USER,
        OWNING_CANARY,
        "SERVICEACCOUNTCANARY",
    ];
    let replies = vec![
        vec![json_ok(&page(1, &["a"]))],
        vec![http("401 Unauthorized", "application/json", "{}")],
        vec![http("403 Forbidden", "text/html", "<html>blocked</html>")],
        vec![http("429 Too Many Requests", "application/json", "{}")],
        vec![json_ok(&format!(
            r#"{{"usageEventsDisplay":{{"owningUser":"{OWNING_CANARY}"}}}}"#
        ))],
        vec![json_ok(&page(4, &["a", "b"])), Reply::Hang],
    ];
    let mut outputs = Vec::new();
    for reply in replies {
        let f = fixture_with_token(&token);
        let (url, _) = serve(reply);
        outputs.push(sync_status(&f, &url, &registry(true, &f.dir)).to_string());
        for entry in std::fs::read_dir(&f.dir).into_iter().flatten().flatten() {
            outputs.push(entry.file_name().to_string_lossy().into_owned());
            if entry.file_type().unwrap().is_file() {
                outputs.push(String::from_utf8_lossy(&std::fs::read(entry.path()).unwrap()).into_owned());
            }
        }
    }
    // Mismatch refusal, through the same entry point.
    let tmp = TempDir::new();
    let db = state_db(
        tmp.path(),
        &[
            ("cursorAuth/accessToken", &token),
            ("glass.lastSignedInAuthId", OTHER_USER),
        ],
    );
    let mismatch = Fixture {
        db,
        dir: tmp.path().join("sync"),
        token: token.clone(),
        _tmp: tmp,
    };
    outputs.push(
        sync_status(
            &mismatch,
            "http://127.0.0.1:9/never",
            &registry(true, &mismatch.dir),
        )
        .to_string(),
    );

    // Control: the scenarios above did produce the outputs being searched.
    let all = outputs.join("\n");
    for expected in [
        "\"ok\"",
        "\"expired\"",
        "forbidden_non_json",
        "rate_limited",
        "unexpected_response",
        "\"partial\"",
        "account_mismatch",
        "usage.",
    ] {
        assert!(all.contains(expected), "missing {expected} in {all}");
    }
    for canary in canaries {
        assert!(!all.contains(canary), "canary leaked");
    }
}

// --- registry (W1, W7) ---------------------------------------------------------

/// The data-root seam and the global registry, reset on drop.
struct DataRoot {
    tmp: TempDir,
    _guard: std::sync::MutexGuard<'static, ()>,
}

impl DataRoot {
    fn new() -> Self {
        let guard = lock();
        let tmp = TempDir::new();
        // The local base exists, as `%LOCALAPPDATA%` does.
        std::fs::create_dir(tmp.path().join(TEST_LOCAL_DIR)).unwrap();
        set_data_root_for_test(Some(tmp.path().to_path_buf()));
        *CONFIG.write().unwrap() = Config::default();
        Self { tmp, _guard: guard }
    }

    fn path(&self) -> &Path {
        self.tmp.path()
    }

    fn sync_dir(&self) -> PathBuf {
        self.path().join(APP_DIR_NAME).join(SYNC_DIR_NAME)
    }
}

impl Drop for DataRoot {
    fn drop(&mut self) {
        *CONFIG.write().unwrap_or_else(|p| p.into_inner()) = Config::default();
        set_data_root_for_test(None);
    }
}

fn set(json: serde_json::Value) -> Result<serde_json::Value, String> {
    set_from_json(&json.to_string())
}

/// W1: a `dir` key, or any unknown key, is refused before anything changes;
/// the dir is always `<data root>\com.nyanako.tokenbar\cursor-cache`, so no
/// caller path can reach storage.
#[test]
fn a_dir_key_is_refused_and_the_dir_is_chosen_here() {
    let root = DataRoot::new();
    let caller = root.path().join("caller-chosen");
    for bad in [
        serde_json::json!({"enabled": true, "dir": caller}),
        serde_json::json!({"enabled": false, "dir": caller}),
        serde_json::json!({"enabled": true, "extra": 1}),
        serde_json::json!({"cliTakeoverConfirmed": true}),
    ] {
        assert_eq!(set(bad.clone()), Err("invalidJson".to_string()), "{bad}");
        assert_eq!(config(), Config::default(), "rejected input changed the registry");
    }
    assert!(!caller.exists());
    assert!(
        !root.path().join(APP_DIR_NAME).exists(),
        "a refused input created storage"
    );

    let ok = set(serde_json::json!({"enabled": true, "cliTakeoverConfirmed": true})).unwrap();
    assert_eq!(ok["removedFiles"], 0);
    assert_eq!(ok["dir"], root.sync_dir().to_string_lossy().as_ref());
    assert_eq!(
        config(),
        Config {
            enabled: true,
            dir: Some(root.sync_dir()),
            cli_takeover_confirmed: true,
            ..Config::default()
        }
    );
    // Intended change (S6): the result echoes the default profile, nothing else.
    assert_eq!(ok["profiles"], serde_json::json!([{"id": "default"}]));
    assert!(ok["profilesDir"].is_null());
    assert!(!caller.exists());

    // Without a data root there is no dir to choose: enabling fails and
    // leaves the registry as it was.
    set_data_root_for_test(None);
    assert_eq!(
        set(serde_json::json!({"enabled": true})),
        Err("storageUnavailable".to_string())
    );
    assert!(config().enabled);
}

/// Turning sync off on a PC that never synced creates nothing.
#[test]
fn disabling_without_a_sync_dir_creates_nothing() {
    let root = DataRoot::new();
    let off = set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(off["removedFiles"], 0);
    assert!(off["dir"].is_null());
    assert_eq!(names(root.path()), [TEST_LOCAL_DIR], "{:?}", names(root.path()));
    assert!(names(&root.path().join(TEST_LOCAL_DIR)).is_empty());
}

/// The launch retry the app's "Restart Syrtis to retry" copy relies on: a
/// fresh process (the registry at its default, never enabled here) pushes
/// `enabled: false` at launch, and that alone deletes a usage file an
/// earlier run left behind. The other disable tests enable first; this one
/// must not.
#[test]
fn a_launch_off_push_from_the_default_registry_deletes_a_left_file() {
    let root = DataRoot::new();
    assert_eq!(config(), Config::default(), "fixture: a fresh launch");
    drop(secure::ensure_dir(&root.path().join(APP_DIR_NAME)).unwrap());
    let left = root.sync_dir().join(format!("usage.{}.json", "d".repeat(64)));
    write_secure(&left, b"{}");

    let off = set(serde_json::json!({"enabled": false, "cliTakeoverConfirmed": false})).unwrap();
    assert_eq!(off["removedFiles"], 1, "{off}");
    assert!(!left.exists(), "the launch off push left the file");
    assert!(!config().enabled);
}

/// Intended change (S6, macOS B2): only `usage.<64 hex>.json` and the temp
/// prefix are Syrtis's; any other `usage*` name is left.
#[test]
fn disable_deletes_only_syrtis_usage_files() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true})).unwrap();
    let dir = root.sync_dir();
    let synced = dir.join(hex_name('a'));
    let temp = dir.join(format!("{TEMP_FILE_PREFIX}1-1"));
    let unrelated = dir.join("keep.txt");
    let not_ours = dir.join("usage.not-complete.json");
    let short = dir.join(format!("usage.{}.json", "a".repeat(63)));
    for path in [&synced, &temp, &unrelated, &not_ours, &short] {
        write_secure(path, b"x");
    }
    let off = set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(off["removedFiles"], 2);
    assert!(off["dir"].is_null());
    assert_eq!(off["profiles"], serde_json::json!([]));
    assert!(!synced.exists() && !temp.exists());
    for kept in [&unrelated, &not_ours, &short] {
        assert!(kept.exists(), "disable must delete only Syrtis usage files");
    }
    assert!(!config().enabled);
}

/// W2 + W7: a Syrtis-named file that fails the storage contract (here one
/// written with inherited permissions) is never deleted, and the disable is
/// an error, not `removedFiles: 0`. Sync is off all the same.
#[test]
fn a_contract_failing_file_is_left_and_the_disable_reports_it() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true})).unwrap();
    let dir = root.sync_dir();
    let loose = dir.join(format!("usage.{}.json", "b".repeat(64)));
    std::fs::write(&loose, b"{}").unwrap();
    let ours = dir.join(format!("usage.{}.json", "c".repeat(64)));
    write_secure(&ours, b"{}");

    assert_eq!(
        set(serde_json::json!({"enabled": false})),
        Err("cleanupFailed".to_string())
    );
    assert!(!config().enabled, "a failed cleanup must still turn sync off");
    assert!(loose.exists(), "a contract-failing file was deleted");
    assert!(!ours.exists(), "the secure file beside it is still deleted");

    // Control: once the loose file is gone, the same call succeeds.
    std::fs::remove_file(&loose).unwrap();
    assert_eq!(set(serde_json::json!({"enabled": false})).unwrap()["removedFiles"], 0);
}

// --- storage on NTFS (W2; Plan B1 Windows-only acceptance) ---------------------

fn open_handle(path: &Path, directory: bool) -> std::fs::File {
    use std::os::windows::fs::OpenOptionsExt as _;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, READ_CONTROL,
    };
    let mut options = std::fs::OpenOptions::new();
    options.access_mode(READ_CONTROL).custom_flags(
        FILE_FLAG_OPEN_REPARSE_POINT | if directory { FILE_FLAG_BACKUP_SEMANTICS } else { 0 },
    );
    options.open(path).unwrap()
}

/// `GetSecurityInfo` read-back of owner and DACL: owner = current user,
/// protected, exactly {current user, LocalSystem}, no inherited ACE.
fn meets_contract(path: &Path, directory: bool) -> bool {
    use std::os::windows::io::AsRawHandle as _;
    let handle = open_handle(path, directory);
    crate::agent_storage_windows::verify_storage_handle(handle.as_raw_handle() as _).is_ok()
}

#[test]
fn the_sync_dir_and_files_meet_the_storage_contract() {
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(
        run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    assert!(meets_contract(&f.dir, true), "sync dir");
    assert!(meets_contract(&expected_file(&f.dir), false), "usage file");
    assert!(meets_contract(&f.dir.join(LOCK_FILE_NAME), false), "lock file");
    // Control: the read-back does reject a file with inherited permissions,
    // and the temp parent the fixture lives in.
    let loose = f.dir.join("loose.txt");
    std::fs::write(&loose, b"x").unwrap();
    assert!(!meets_contract(&loose, false), "control: inherited ACL accepted");
    assert!(!meets_contract(f.dir.parent().unwrap(), true), "control: temp dir accepted");
}

/// A pre-created `cursor-cache` with loose permissions is never used or
/// tightened: the sticky `.secure` sibling is, and the original is untouched.
#[test]
fn a_loose_sync_dir_falls_back_to_the_secure_sibling() {
    let root = DataRoot::new();
    // A secure root, so only the child is loose.
    drop(secure::resolve_dir(&root.path().join(APP_DIR_NAME)).unwrap());
    let planted = root.sync_dir();
    std::fs::create_dir(&planted).unwrap();
    std::fs::write(planted.join("marker.txt"), b"planted").unwrap();
    assert!(!meets_contract(&planted, true), "fixture is inert");

    let on = set(serde_json::json!({"enabled": true})).unwrap();
    let fallback = root.path().join(APP_DIR_NAME).join("cursor-cache.secure");
    assert_eq!(on["dir"], fallback.to_string_lossy().as_ref());
    assert!(meets_contract(&fallback, true));
    assert_eq!(names(&planted), ["marker.txt"], "the planted dir was written");
    assert!(!meets_contract(&planted, true), "the planted dir was tightened");

    // A loose candidate with nothing of Syrtis's in it does not fail the
    // disable (control for the test below).
    let off = set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(off["removedFiles"], 0);
    assert_eq!(names(&planted), ["marker.txt"]);
}

/// Every directory under `root`, recursively (files ignored).
fn dir_tree(root: &Path) -> Vec<PathBuf> {
    let mut found = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(&dir).unwrap().flatten() {
            if entry.file_type().unwrap().is_dir() {
                found.push(entry.path());
                stack.push(entry.path());
            }
        }
    }
    found.sort();
    found
}

/// The fallbacks are sticky, so the dir in use can move after a file was
/// written: disabling cleans every existing candidate, counts what it
/// removed, and creates no directory.
#[test]
fn disable_cleans_a_sync_dir_left_behind_when_the_root_moved() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true})).unwrap();
    let first = root.sync_dir();
    let stranded = first.join(format!("usage.{}.json", "a".repeat(64)));
    write_secure(&stranded, b"{}");

    // The root now resolves to its `.secure` sibling (sticky once it exists).
    let secure_root = root
        .path()
        .join(format!("{APP_DIR_NAME}{}", secure::FALLBACK_SUFFIX));
    drop(secure::ensure_dir(&secure_root).unwrap());
    let on = set(serde_json::json!({"enabled": true})).unwrap();
    let moved = secure_root.join(SYNC_DIR_NAME);
    assert_eq!(on["dir"], moved.to_string_lossy().as_ref(), "fixture is inert");
    let current = moved.join(format!("usage.{}.json", "b".repeat(64)));
    write_secure(&current, b"{}");

    let before = dir_tree(root.path());
    let off = set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(off["removedFiles"], 2, "{off}");
    assert!(!stranded.exists(), "the file under the old root survived");
    assert!(!current.exists());
    assert_eq!(dir_tree(root.path()), before, "disable created a directory");
}

/// A candidate that is a real dir failing the contract and holds a
/// Syrtis-named file cannot be cleaned safely: `cleanupFailed`, sync off,
/// the file left.
#[test]
fn a_loose_candidate_holding_a_syrtis_file_fails_the_cleanup() {
    let root = DataRoot::new();
    drop(secure::resolve_dir(&root.path().join(APP_DIR_NAME)).unwrap());
    let planted = root.sync_dir();
    std::fs::create_dir(&planted).unwrap();
    let loose = planted.join(format!("usage.{}.json", "e".repeat(64)));
    std::fs::write(&loose, b"{}").unwrap();
    set(serde_json::json!({"enabled": true})).unwrap();

    assert_eq!(
        set(serde_json::json!({"enabled": false})),
        Err("cleanupFailed".to_string())
    );
    assert!(!config().enabled);
    assert!(loose.exists());
}

/// Under the root's `.secure` fallback the sync dir is its child.
#[test]
fn under_the_root_fallback_the_sync_dir_is_its_child() {
    let root = DataRoot::new();
    let planted = root.path().join(APP_DIR_NAME);
    std::fs::create_dir_all(&planted).unwrap();
    let on = set(serde_json::json!({"enabled": true})).unwrap();
    let expected = root
        .path()
        .join(format!("{APP_DIR_NAME}.secure"))
        .join(SYNC_DIR_NAME);
    assert_eq!(on["dir"], expected.to_string_lossy().as_ref());
    assert!(names(&planted).is_empty(), "the loose root was written");
}

/// A junction at the final component is refused (never followed): the
/// `.secure` sibling is used and the junction's target stays empty.
#[test]
fn a_junction_at_the_sync_dir_is_refused() {
    let root = DataRoot::new();
    drop(secure::resolve_dir(&root.path().join(APP_DIR_NAME)).unwrap());
    let target = root.path().join("elsewhere");
    std::fs::create_dir_all(&target).unwrap();
    let link = root.sync_dir();
    let cmd = std::env::var_os("SystemRoot")
        .map(|system| PathBuf::from(system).join("System32").join("cmd.exe"))
        .unwrap();
    let made = std::process::Command::new(cmd)
        .args(["/C", "mklink", "/J"])
        .arg(&link)
        .arg(&target)
        .output()
        .unwrap();
    assert!(made.status.success(), "mklink /J failed: {made:?}");
    assert!(link.is_dir(), "fixture is inert: the junction does not resolve");

    let on = set(serde_json::json!({"enabled": true})).unwrap();
    let fallback = root.path().join(APP_DIR_NAME).join("cursor-cache.secure");
    assert_eq!(on["dir"], fallback.to_string_lossy().as_ref());
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    let dir = config().dir.unwrap();
    assert_eq!(
        run_with(&f.db, &dir, &url, limits(), &|| true).stop.state,
        State::Ok
    );
    assert!(names(&target).is_empty(), "wrote through the junction");
}

/// A Rust std reader (the engine's scan; it opens with FILE_SHARE_DELETE)
/// holding the usage file makes that walk fail cleanly: `write_failed`, the
/// old bytes intact, no temp left. The next walk writes it.
///
/// Measured on 188 (Windows 10.0.26200, NTFS): the secure replace
/// (`tokscale_core::fs_atomic::replace_file`, `MoveFileExW`
/// REPLACE_EXISTING|WRITE_THROUGH, 5 attempts) returns ERROR_ACCESS_DENIED
/// (code 5) while such a reader is open, whereas `std::fs::rename` of the
/// same files succeeds. Accepted as transient (Plan B1 decision
/// 2026-10-08); `agent_storage_windows` and `fs_atomic` stay as they are.
#[test]
fn a_std_reader_holding_the_file_fails_the_walk_cleanly() {
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state, State::Ok);
    let path = expected_file(&f.dir);
    let old = std::fs::read(&path).unwrap();

    let mut reader = std::fs::File::open(&path).unwrap();
    let (url, _) = serve(vec![json_ok(&page(1, &["b"]))]);
    let outcome = run_with(&f.db, &f.dir, &url, limits(), &|| true);
    let mut held = Vec::new();
    reader.read_to_end(&mut held).unwrap();
    drop(reader);
    eprintln!("measured: replace with a share-delete reader open -> {outcome:?}");
    assert_eq!(held, old, "the reader sees the bytes it opened");
    assert_eq!(outcome, stop(State::Error, Some("write_failed")));
    assert_eq!(std::fs::read(&path).unwrap(), old, "the old file must stay intact");
    assert!(
        names(&f.dir).iter().all(|n| !n.starts_with(TEMP_FILE_PREFIX)),
        "{:?}",
        names(&f.dir)
    );

    // Control: with the reader gone the same walk replaces the file.
    let (url, _) = serve(vec![json_ok(&page(1, &["b"]))]);
    assert_eq!(run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state, State::Ok);
    assert_ne!(std::fs::read(&path).unwrap(), old, "the file was replaced");
}

/// A reader that does not share delete (some scanners): the replace fails
/// after its retries, `write_failed`, the old file intact, no temp left.
#[test]
fn replace_while_a_no_share_delete_reader_holds_the_file_fails_cleanly() {
    use std::os::windows::fs::OpenOptionsExt as _;
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(run_with(&f.db, &f.dir, &url, limits(), &|| true).stop.state, State::Ok);
    let path = expected_file(&f.dir);
    let current = std::fs::read(&path).unwrap();
    let blocker = std::fs::OpenOptions::new()
        .read(true)
        .share_mode(windows_sys::Win32::Storage::FileSystem::FILE_SHARE_READ)
        .open(&path)
        .unwrap();
    let (url, _) = serve(vec![json_ok(&page(1, &["c"]))]);
    let outcome = run_with(&f.db, &f.dir, &url, limits(), &|| true);
    drop(blocker);
    eprintln!("measured: replace with a no-share-delete reader open -> {outcome:?}");
    assert_eq!(outcome, stop(State::Error, Some("write_failed")));
    assert_eq!(std::fs::read(&path).unwrap(), current);
    assert!(
        names(&f.dir).iter().all(|n| !n.starts_with(TEMP_FILE_PREFIX)),
        "{:?}",
        names(&f.dir)
    );
}

/// Turning sync off while a reader (std, FILE_SHARE_DELETE) holds the usage
/// file: measured outcome asserted. The delete is reported done, the takeover
/// no longer sees a complete file, and the name is gone once the reader
/// closes.
#[test]
fn disable_while_a_reader_holds_the_file() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true})).unwrap();
    let dir = config().dir.unwrap();
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    assert_eq!(run_with(&f.db, &dir, &url, limits(), &|| true).stop.state, State::Ok);
    let path = expected_file(&dir);
    let reader = std::fs::File::open(&path).unwrap();

    let off = set(serde_json::json!({"enabled": false}));
    let exists_while_held = std::fs::symlink_metadata(&path).is_ok();
    let complete_while_held = complete_file(&dir).is_some();
    drop(reader);
    eprintln!(
        "measured: disable with a reader open -> {off:?}; name visible while held: \
         {exists_while_held}; complete file seen while held: {complete_while_held}"
    );
    assert_eq!(off.unwrap()["removedFiles"], 1);
    assert!(!complete_while_held, "a deleted file still counts as complete");
    assert!(!path.exists(), "the name outlived the reader");
    drop(root);
}

const LOCK_CHILD_DIR: &str = "TOKENBAR_CURSOR_LOCK_CHILD_DIR";

/// Two processes: while one holds the secure lock, the other's disable-time
/// cleanup waits (blocking `fs2` lock) and finishes only after release.
#[test]
fn a_second_process_waits_for_the_lock() {
    if let Some(dir) = std::env::var_os(LOCK_CHILD_DIR) {
        let dir = PathBuf::from(dir);
        let removed = remove_usage_files_locked(&dir).unwrap();
        std::fs::write(dir.join("child-done.txt"), removed.to_string()).unwrap();
        return;
    }
    let tmp = TempDir::new();
    let dir = tmp.path().join("sync");
    write_secure(&dir.join(format!("usage.{}.json", "d".repeat(64))), b"{}");
    let held = secure::open_lock(&dir.join(LOCK_FILE_NAME)).unwrap();

    let mut command = std::process::Command::new(std::env::current_exe().unwrap());
    command
        .arg("cursor_sync::tests::a_second_process_waits_for_the_lock")
        .arg("--exact")
        .env(LOCK_CHILD_DIR, &dir);
    let mut child = command.spawn().unwrap();
    std::thread::sleep(Duration::from_millis(2000));
    let done = dir.join("child-done.txt");
    assert!(!done.exists(), "the second process did not wait for the lock");
    assert!(child.try_wait().unwrap().is_none(), "the second process exited early");
    let released = Instant::now();
    drop(held);
    assert!(child.wait().unwrap().success());
    eprintln!(
        "measured: second process finished {:?} after the lock was released",
        released.elapsed()
    );
    assert_eq!(std::fs::read_to_string(&done).unwrap(), "1");
}

// --- takeover ------------------------------------------------------------------

fn write_complete_file(dir: &Path) -> PathBuf {
    write_named_file(dir, 'c')
}

fn write_named_file(dir: &Path, c: char) -> PathBuf {
    let path = dir.join(hex_name(c));
    write_secure(
        &path,
        br#"{"totalUsageEventsCount":0,"usageEventsDisplay":[]}"#,
    );
    path
}

fn claude_settings() -> tokscale_core::scanner::ScannerSettings {
    tokscale_core::scanner::ScannerSettings {
        extra_scan_paths: [("claude".to_string(), vec![PathBuf::from(r"C:\claude\extra")])].into(),
        ..Default::default()
    }
}

/// 13: the takeover rule over the default and an added profile. Only files
/// are registered (item 7), one per account.
#[test]
fn takeover_matrix() {
    for bits in 0..32u8 {
        let (enabled, in_default, in_added, cli_files, confirmed) = (
            bits & 1 != 0,
            bits & 2 != 0,
            bits & 4 != 0,
            bits & 8 != 0,
            bits & 16 != 0,
        );
        let home = TempDir::new();
        let cli = cli_root(home.path());
        std::fs::create_dir_all(&cli).unwrap();
        let config = multi(home.path(), enabled, vec![profile(PROFILE_A, Path::new(r"C:\x\a"))]);
        drop(secure::ensure_dir(&default_sync_dir(home.path())).unwrap());
        let mut files = Vec::new();
        if in_default {
            files.push(write_named_file(&default_sync_dir(home.path()), 'a'));
        }
        if in_added {
            let a = profile(PROFILE_A, Path::new(r"C:\x\a"));
            drop(secure::ensure_dir(&profile_dir(home.path(), &a)).unwrap());
            files.push(write_named_file(&profile_dir(home.path(), &a), 'b'));
        }
        if cli_files {
            std::fs::write(cli.join("usage.csv"), "x").unwrap();
        }
        let config = Config {
            cli_takeover_confirmed: confirmed,
            ..config
        };
        let mut settings = claude_settings();
        apply_takeover(&mut settings, &takeover(&config, Some(home.path())));

        let complete = in_default || in_added;
        let on = enabled && complete && (!cli_files || confirmed);
        let label = format!(
            "enabled={enabled} default={in_default} added={in_added} cli={cli_files} confirmed={confirmed}"
        );
        assert_eq!(
            settings.extra_scan_paths["claude"],
            [PathBuf::from(r"C:\claude\extra")],
            "{label}"
        );
        assert_eq!(
            settings.extra_scan_paths.get("cursor").cloned(),
            on.then(|| files.clone()),
            "{label}"
        );
        assert_eq!(
            settings.excluded_scan_paths.get("cursor").cloned(),
            on.then(|| vec![cli.clone()]),
            "{label}"
        );
        let blocked = enabled && complete && cli_files && !confirmed;
        assert_eq!(
            takeover(&config, Some(home.path())) == Takeover::Blocked,
            blocked,
            "{label}"
        );
    }
}

#[test]
fn cli_file_detection_errs_toward_asking() {
    let home = TempDir::new();
    let cli = home.path().join("cursor-cache");
    assert!(!cli_has_cursor_files(&cli), "a missing root holds nothing");
    std::fs::create_dir_all(cli.join("archive")).unwrap();
    std::fs::write(cli.join("usage.last-sync-attempt"), "x").unwrap();
    assert!(!cli_has_cursor_files(&cli), "control: no usage data file");
    std::fs::write(cli.join("archive/usage.backup-1.csv"), "x").unwrap();
    assert!(cli_has_cursor_files(&cli));
    std::fs::remove_file(cli.join("archive/usage.backup-1.csv")).unwrap();
    std::fs::write(cli.join("usage.json"), "{}").unwrap();
    assert!(cli_has_cursor_files(&cli));
}

/// `cliPresent` follows a profile whose walk completed (`ok`), whatever the
/// headline and whether or not its bytes changed, and carries no reason
/// (macOS FIX 5, `cli_present_carries_no_other_reason`).
#[test]
fn cli_present_follows_a_synced_profile_and_carries_no_reason() {
    let ok = serde_json::json!({"state": "ok", "events": 2, "lastSuccessMs": 1, "changed": true,
        "profiles": [{"id": "default", "state": "ok"}]});
    assert_eq!(status_for(ok.clone(), &Takeover::Blocked)["state"], "cliPresent");
    assert_eq!(status_for(ok.clone(), &Takeover::Off)["state"], "ok");
    // A walk that completed with the file's own bytes is not `changed`, and
    // still reads as cliPresent while the takeover is blocked (main's rule).
    let same = serde_json::json!({"state": "ok", "events": 2, "changed": false,
        "profiles": [{"id": "default", "state": "ok"}]});
    assert_eq!(status_for(same, &Takeover::Blocked)["state"], "cliPresent");
    let mixed = serde_json::json!({"state": "error", "reason": "account_mismatch",
        "events": 1, "changed": true, "profiles": [
            {"id": "default", "state": "error", "reason": "account_mismatch"},
            {"id": PROFILE_A, "state": "ok"}]});
    let relabelled = status_for(mixed.clone(), &Takeover::Blocked);
    assert_eq!(relabelled["state"], "cliPresent");
    assert!(relabelled.get("reason").is_none(), "{relabelled}");
    assert_eq!(status_for(mixed, &Takeover::Off)["reason"], "account_mismatch");
    let partial = serde_json::json!({"state": "partial", "events": 0, "changed": false,
        "profiles": [{"id": "default", "state": "partial"}]});
    assert_eq!(status_for(partial, &Takeover::Blocked)["state"], "partial");
}

// --- end to end through the process context (child processes) ----------------

/// Two events of one usage history: 300 input, 60 output, 30 cache read.
fn e2e_body() -> String {
    e2e_body_times(1)
}

/// The same history with every token count multiplied by `k`.
fn e2e_body_times(k: i64) -> String {
    let event = |id: &str, ts: &str, input: i64, output: i64, cache_read: i64, cents: f64| {
        serde_json::json!({
            "conversationId": id, "timestamp": ts, "model": "gpt-5",
            "chargedCents": 1,
            "tokenUsage": {"inputTokens": input, "outputTokens": output,
                           "cacheReadTokens": cache_read, "totalCents": cents},
            "owningUser": OWNING_CANARY,
        })
    };
    serde_json::json!({
        "totalUsageEventsCount": 2,
        "usageEventsDisplay": [
            event("conv-a", "1788256800000", 100 * k, 20 * k, 30 * k, 50.0),
            event("conv-b", "1788260400000", 200 * k, 40 * k, 0, 25.0),
        ],
    })
    .to_string()
}

const SYNCED_OUTPUT: i64 = 60;
/// The CLI's CSV: a different output count from the synced file, so every
/// total says which copy (or both) was counted: 60 synced, 1000 CLI, 1060
/// double.
const CLI_OUTPUT: i64 = 1000;
const CLI_CSV: &str = "Date,Kind,Model,Max Mode,Input (w/ Cache Write),Input (w/o Cache Write),Cache Read,Output Tokens,Total Tokens,Cost\n\
\"2026-09-01T10:00:00.000Z\",\"Included\",\"gpt-5\",\"No\",\"0\",\"100\",\"0\",\"1000\",\"1100\",\"0.50\"\n";

const WINDOW_FROM: i64 = 1_788_220_800_000; // 2026-09-01T00:00:00Z
const WINDOW_UNTIL: i64 = 1_788_307_200_000; // 2026-09-02T00:00:00Z

unsafe fn take(p: *mut std::ffi::c_char) -> serde_json::Value {
    let text = unsafe { std::ffi::CStr::from_ptr(p) }
        .to_string_lossy()
        .into_owned();
    unsafe { crate::tb_free(p) };
    serde_json::from_str(&text).unwrap_or_else(|error| panic!("{error}: {text}"))
}

fn call_set(json: &str) -> serde_json::Value {
    let raw = std::ffi::CString::new(json).unwrap();
    unsafe { take(crate::tb_set_cursor_sync(raw.as_ptr())) }
}

/// One sync the way `tb_cursor_sync` runs it (walk, then the recheck), with
/// the fixture database, the local server and the test key.
fn walk_now(db: &Path, url: &str) -> serde_json::Value {
    let limits = Limits {
        page_size: 500,
        ..limits()
    };
    crate::cursor_sync_then_recheck(|| sync_with(true, &target(db, url), &config, Some(limits)))
}

/// Cursor (output tokens, sorted session ids) through `process()`.
fn cursor_report() -> (i64, Vec<String>) {
    let context = crate::LocalSourceContext::process().unwrap();
    let options = tokscale_core::ReportOptions {
        group_by: tokscale_core::GroupBy::Session,
        ..context.report_options(None, Some(vec!["cursor".to_string()]))
    };
    let report = crate::RUNTIME
        .block_on(tokscale_core::get_model_report_with_source_context(
            context.resolved(),
            options,
        ))
        .expect("model report");
    let mut sessions: Vec<String> = report
        .entries
        .iter()
        .map(|e| e.session_id.clone().unwrap_or_default())
        .collect();
    sessions.sort();
    (report.total_output, sessions)
}

fn graph_cursor_output() -> i64 {
    let graph = unsafe { take(crate::tb_graph(std::ptr::null())) };
    graph["data"]["contributions"]
        .as_array()
        .unwrap_or_else(|| panic!("graph payload: {graph}"))
        .iter()
        .flat_map(|day| day["clients"].as_array().cloned().unwrap_or_default())
        .filter(|row| row["client"] == "cursor")
        .map(|row| row["tokens"]["output"].as_i64().unwrap_or(0))
        .sum()
}

fn window_cursor_output() -> i64 {
    let window =
        unsafe { take(crate::tb_window_usage(std::ptr::null(), WINDOW_FROM, WINDOW_UNTIL)) };
    window["data"]["messages"]
        .as_array()
        .unwrap_or_else(|| panic!("window payload: {window}"))
        .iter()
        .filter(|message| message["client"] == "cursor")
        .map(|message| message["output"].as_i64().unwrap_or(0))
        .sum()
}

fn child_setup(root: &Path) -> PathBuf {
    let data = root.join("appdata");
    std::fs::create_dir_all(&data).unwrap();
    set_data_root_for_test(Some(data));
    signed_in_db(root, &valid_token())
}

/// The W5 acceptance through the production entries: the takeover follows
/// every recheck point through `LocalSourceContext::process()`, Cursor is
/// counted once in every state (D6), a takeover change bumps the generation
/// and clears the cached graph and window.
#[test]
fn the_takeover_reaches_the_process_context_and_counts_once() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::the_takeover_reaches_the_process_context_and_counts_once",
            "cursor-takeover",
        );
    };
    let db = child_setup(&root);
    let synced_sessions = vec!["conv-a".to_string(), "conv-b".to_string()];

    let on = call_set(r#"{"enabled":true}"#);
    assert_eq!(on["ok"], true, "{on}");
    assert_eq!(cursor_report().0, 0, "no complete file yet: nothing to count");
    assert_eq!(graph_cursor_output(), 0);
    assert_eq!(window_cursor_output(), 0);

    // Empty CLI root (control): the first complete walk turns the takeover on
    // at the end-of-walk recheck, and the cached graph and window move with it.
    let (url, _) = serve(vec![json_ok(&e2e_body())]);
    let status = walk_now(&db, &url);
    assert_eq!(status["state"], "ok", "{status}");
    assert_eq!(cursor_report(), (SYNCED_OUTPUT, synced_sessions.clone()));
    assert_eq!(graph_cursor_output(), SYNCED_OUTPUT, "stale graph served");
    assert_eq!(window_cursor_output(), SYNCED_OUTPUT, "stale window served");

    // The CLI's files appear after the automatic takeover. Until the next
    // recheck the CLI root stays excluded: still one copy.
    let cli = cli_root(&root);
    std::fs::create_dir_all(&cli).unwrap();
    std::fs::write(cli.join("usage.csv"), CLI_CSV).unwrap();
    assert_eq!(cursor_report(), (SYNCED_OUTPUT, synced_sessions.clone()));

    // The next walk: D6 turns the takeover off (not confirmed); the status
    // and the scan agree, and neither the cached graph nor window survives.
    let old = crate::LocalSourceContext::process().unwrap();
    let (url, _) = serve(vec![json_ok(&e2e_body())]);
    let status = walk_now(&db, &url);
    assert_eq!(status["state"], "cliPresent", "{status}");
    assert!(!old.is_current(), "the takeover change did not move the generation");
    crate::graph_compute(&old, "").unwrap();
    assert!(
        !crate::GRAPH_CACHE.lock().unwrap().contains_key(""),
        "a graph from the old context was published"
    );
    let (output, sessions) = cursor_report();
    assert_eq!(output, CLI_OUTPUT, "the CLI's data must be visible, once");
    assert!(sessions.iter().all(|s| !synced_sessions.contains(s)), "{sessions:?}");
    assert_eq!(graph_cursor_output(), CLI_OUTPUT);
    assert_eq!(window_cursor_output(), CLI_OUTPUT);

    // Confirmed: the synced file, once.
    let confirmed = call_set(r#"{"enabled":true,"cliTakeoverConfirmed":true}"#);
    assert_eq!(confirmed["ok"], true, "{confirmed}");
    assert_eq!(cursor_report(), (SYNCED_OUTPUT, synced_sessions.clone()));
    assert_eq!(graph_cursor_output(), SYNCED_OUTPUT);
    assert_eq!(window_cursor_output(), SYNCED_OUTPUT);

    // Off: the synced file is deleted and the CLI's data is back, once.
    let off = call_set(r#"{"enabled":false}"#);
    assert_eq!(off["data"]["removedFiles"], 1, "{off}");
    assert_eq!(cursor_report().0, CLI_OUTPUT);
    assert_eq!(graph_cursor_output(), CLI_OUTPUT);
    assert!(cli.join("usage.csv").exists(), "the CLI's file was touched");
}

/// A second complete walk with new content while the takeover is already on
/// (no recapture) still clears the cached graph and window.
#[test]
fn a_new_walk_under_the_same_takeover_refreshes_the_caches() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::a_new_walk_under_the_same_takeover_refreshes_the_caches",
            "cursor-refresh",
        );
    };
    let db = child_setup(&root);
    assert_eq!(call_set(r#"{"enabled":true}"#)["ok"], true);
    let (url, _) = serve(vec![json_ok(&e2e_body())]);
    assert_eq!(walk_now(&db, &url)["state"], "ok");
    assert_eq!(graph_cursor_output(), SYNCED_OUTPUT);
    assert_eq!(window_cursor_output(), SYNCED_OUTPUT);

    let generation = crate::LocalSourceContext::process().unwrap().generation();
    let (url, _) = serve(vec![json_ok(&e2e_body_times(2))]);
    assert_eq!(walk_now(&db, &url)["state"], "ok");
    assert_eq!(
        crate::LocalSourceContext::process().unwrap().generation(),
        generation,
        "fixture is inert: the takeover changed"
    );
    assert_eq!(graph_cursor_output(), 2 * SYNCED_OUTPUT, "stale graph served");
    assert_eq!(window_cursor_output(), 2 * SYNCED_OUTPUT, "stale window served");
}

/// W-2: with an extra Claude root registered the primary window is a scoped
/// context; its exclusions must keep the takeover's CLI exclusion. And with
/// no Claude root the primary window is the process context itself.
#[test]
fn the_primary_window_counts_cursor_once_with_an_extra_claude_root() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::the_primary_window_counts_cursor_once_with_an_extra_claude_root",
            "cursor-window",
        );
    };
    let db = child_setup(&root);
    let cli = cli_root(&root);
    std::fs::create_dir_all(&cli).unwrap();
    std::fs::write(cli.join("usage.csv"), CLI_CSV).unwrap();
    assert_eq!(call_set(r#"{"enabled":true,"cliTakeoverConfirmed":true}"#)["ok"], true);
    let (url, _) = serve(vec![json_ok(&e2e_body())]);
    assert_eq!(walk_now(&db, &url)["state"], "ok");

    assert_eq!(window_cursor_output(), SYNCED_OUTPUT);
    let process = crate::LocalSourceContext::process().unwrap();
    let scoped = crate::window_usage::scoped_context_for_test(&process, &None).unwrap();
    assert!(
        scoped.same_resolved(&process),
        "no Claude root: the primary window must be the process scan"
    );

    let work = root.join("work-d");
    std::fs::create_dir_all(work.join("projects")).unwrap();
    crate::apply_scan_roots_for_test(std::collections::BTreeMap::from([(
        "claude".to_string(),
        vec![work.join("projects")],
    )]))
    .unwrap();
    assert_eq!(cursor_report().0, SYNCED_OUTPUT, "a root change dropped the takeover");
    assert_eq!(
        window_cursor_output(),
        SYNCED_OUTPUT,
        "the primary window counted the CLI copy too"
    );
}

/// Sync, the scan-root setter, the sync setter and graph reads at once:
/// everything finishes (no lock-order deadlock between `ROOTS_SETTER`, the
/// cell, `IN_FLIGHT` and the commit lock).
#[test]
fn sync_setters_and_reads_run_concurrently_without_deadlock() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::sync_setters_and_reads_run_concurrently_without_deadlock",
            "cursor-concurrency",
        );
    };
    let db = child_setup(&root);
    assert_eq!(call_set(r#"{"enabled":true}"#)["ok"], true);
    let (url, _) = serve((0..8).map(|_| json_ok(&e2e_body())).collect());
    let (done_tx, done_rx) = std::sync::mpsc::channel::<()>();
    let mut threads = Vec::new();
    {
        let (done, db, url) = (done_tx.clone(), db.clone(), url.clone());
        threads.push(std::thread::spawn(move || {
            for _ in 0..4 {
                let status = walk_now(&db, &url);
                assert!(status["state"].is_string(), "{status}");
            }
            done.send(()).unwrap();
        }));
    }
    {
        let done = done_tx.clone();
        threads.push(std::thread::spawn(move || {
            for _ in 0..6 {
                let raw = std::ffi::CString::new("{}").unwrap();
                let set = unsafe { take(crate::tb_set_extra_scan_paths(raw.as_ptr())) };
                assert_eq!(set["ok"], true, "{set}");
            }
            done.send(()).unwrap();
        }));
    }
    {
        let done = done_tx.clone();
        threads.push(std::thread::spawn(move || {
            for round in 0..6 {
                let confirmed = round % 2 == 0;
                let set = call_set(&format!(
                    r#"{{"enabled":true,"cliTakeoverConfirmed":{confirmed}}}"#
                ));
                assert_eq!(set["ok"], true, "{set}");
            }
            done.send(()).unwrap();
        }));
    }
    {
        let done = done_tx.clone();
        threads.push(std::thread::spawn(move || {
            for _ in 0..6 {
                graph_cursor_output();
            }
            done.send(()).unwrap();
        }));
    }
    drop(done_tx);
    for _ in 0..threads.len() {
        done_rx
            .recv_timeout(Duration::from_secs(120))
            .expect("a thread did not finish in time: deadlock or stall");
    }
    for thread in threads {
        thread.join().unwrap();
    }
}


// --- several Cursor profiles (CMP-W W1, port of macOS #532) --------------------

const PROFILE_A: &str = "0A1B2C3D-0000-4000-8000-00000000000A";
const PROFILE_B: &str = "0A1B2C3D-0000-4000-8000-00000000000B";
const PROFILE_C: &str = "0A1B2C3D-0000-4000-8000-00000000000C";
const PROFILE_D: &str = "0A1B2C3D-0000-4000-8000-00000000000D";
const PROFILE_E: &str = "0A1B2C3D-0000-4000-8000-00000000000E";
const PROFILE_F: &str = "0A1B2C3D-0000-4000-8000-00000000000F";
const DRIVE_REMOVABLE: u32 = 2;
const DRIVE_REMOTE: u32 = 4;
const DRIVE_CDROM: u32 = 5;
const DRIVE_RAMDISK: u32 = 6;

fn hex_name(c: char) -> String {
    format!("usage.{}.json", c.to_string().repeat(64))
}

/// The profiles exactly as stored, root spelling included (`Profile`'s
/// equality folds roots, so a whole-config comparison cannot see a
/// respelling).
fn raw_profiles(config: &Config) -> Vec<(String, PathBuf)> {
    config
        .profiles
        .iter()
        .map(|p| (p.id.clone(), p.root.clone()))
        .collect()
}

fn profile(id: &str, root: &Path) -> Profile {
    Profile {
        id: id.to_string(),
        root: root.to_path_buf(),
    }
}

fn default_sync_dir(base: &Path) -> PathBuf {
    base.join(SYNC_DIR_NAME)
}

/// Where an added profile's files go under `base` (`profile_dir_name`).
fn profile_dir(base: &Path, profile: &Profile) -> PathBuf {
    base.join(PROFILES_DIR_NAME).join(profile_dir_name(profile))
}

/// A registry whose default dir is `<base>\cursor-cache` and whose profiles
/// parent `<base>\cursor-profiles` exists (production resolves both at the
/// on push).
fn multi(base: &Path, enabled: bool, profiles: Vec<Profile>) -> Config {
    drop(secure::ensure_dir(&base.join(PROFILES_DIR_NAME)).unwrap());
    Config {
        enabled,
        dir: Some(default_sync_dir(base)),
        profiles_dir: Some(base.join(PROFILES_DIR_NAME)),
        profiles,
        cli_takeover_confirmed: false,
    }
}

fn set_owner(path: &Path, owner: crate::agent_storage_windows::TestOwner) {
    crate::agent_storage_windows::set_owner_for_test(path, owner)
        .unwrap_or_else(|error| panic!("fixture: owner of {path:?} not set: {error}"));
}

fn login_db(dir: &Path, token: &str, user: &str) -> PathBuf {
    std::fs::create_dir_all(dir).unwrap();
    state_db(
        dir,
        &[
            ("cursorAuth/accessToken", &format!("\"{token}\"")),
            ("glass.lastSignedInAuthId", &format!("glass-{user}")),
        ],
    )
}

/// A `--user-data-dir` root whose login is `token` for `user`; the database
/// is owned by the current user (188 runs elevated, where new files belong
/// to Administrators).
fn profile_root(base: &Path, name: &str, token: &str, user: &str) -> PathBuf {
    let root = base.join(name);
    let db = login_db(&root.join("User").join("globalStorage"), token, user);
    set_owner(&db, crate::agent_storage_windows::TestOwner::CurrentUser);
    root
}

/// A root whose `User\globalStorage` exists and holds nothing yet.
fn empty_root(base: &Path, name: &str) -> PathBuf {
    let root = base.join(name);
    std::fs::create_dir_all(root.join("User").join("globalStorage")).unwrap();
    root
}

fn other_token() -> String {
    jwt(&format!("auth0|{OTHER_USER}"), Some(now_secs() + 3600.0))
}

fn junction(link: &Path, target: &Path) {
    let cmd = std::env::var_os("SystemRoot")
        .map(|system| PathBuf::from(system).join("System32").join("cmd.exe"))
        .unwrap();
    let made = std::process::Command::new(cmd)
        .args(["/C", "mklink", "/J"])
        .arg(link)
        .arg(target)
        .output()
        .unwrap();
    assert!(made.status.success(), "mklink /J failed: {made:?}");
}

fn sync_round(default_db: &Path, url: &str, registry: &RwLock<Config>) -> serde_json::Value {
    sync_round_with(default_db, url, registry, limits(), &cursor_desktop::drive_type)
}

fn sync_round_with(
    default_db: &Path,
    url: &str,
    registry: &RwLock<Config>,
    limits: Limits,
    drive_type: &DriveType<'_>,
) -> serde_json::Value {
    let billing = v2_url(url);
    let target = Target {
        drive_type,
        billing_url: &billing,
        ..target(default_db, url)
    };
    let source = || registry.read().unwrap().clone();
    sync_with(true, &target, &source, Some(limits)).0
}

fn states(status: &serde_json::Value) -> Vec<String> {
    status["profiles"]
        .as_array()
        .unwrap_or_else(|| panic!("no profiles: {status}"))
        .iter()
        .map(|p| {
            let reason = p["reason"]
                .as_str()
                .map(|r| format!(":{r}"))
                .unwrap_or_default();
            format!("{}{reason}", p["state"].as_str().unwrap())
        })
        .collect()
}

fn usage_names(dir: &Path) -> Vec<String> {
    let mut found: Vec<String> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with("usage"))
        .collect();
    found.sort();
    found
}

/// Creates every directory from `under` down to `path`, each the secure way.
fn secure_tree(under: &Path, path: &Path) {
    let mut current = under.to_path_buf();
    for part in path.strip_prefix(under).unwrap().components() {
        current.push(part);
        drop(secure::ensure_dir(&current).unwrap());
    }
}

// Rounds over several profiles ---------------------------------------------------

/// The same account in a second profile is not walked again this round; its
/// status names the profile that did walk it, never the account.
#[test]
fn a_second_profile_on_the_same_account_is_not_walked_again() {
    let _guard = lock();
    let tmp = TempDir::new();
    let token = valid_token();
    let default_db = login_db(&tmp.path().join("default"), &token, USER);
    let root = profile_root(tmp.path(), "b", &token, USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(seen.lock().unwrap().len(), 1, "the account was walked twice");
    assert_eq!(states(&status), ["ok", "duplicate"]);
    assert_eq!(status["profiles"][1]["duplicateOf"], "default");
    assert_eq!(status["state"], "ok");
    assert_eq!(status["changed"], true);
    assert!(expected_file(&default_sync_dir(tmp.path())).exists());
    assert!(!profile_dir(tmp.path(), &profile(PROFILE_A, &root)).exists());
    // Control: a different account in the second profile is walked.
    let other = profile_root(tmp.path(), "c", &other_token(), OTHER_USER);
    *registry.write().unwrap() = multi(tmp.path(), true, vec![profile(PROFILE_A, &other)]);
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["b"]))]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(seen.lock().unwrap().len(), 2);
    assert_eq!(states(&status), ["ok", "ok"]);
    assert_eq!(status["events"], 2);
    assert_eq!(usage_names(&profile_dir(tmp.path(), &profile(PROFILE_A, &other))).len(), 1);
}

/// A listed folder that does not exist is accepted and reports its own
/// state; the default profile still syncs (item 2).
#[test]
fn a_nonexistent_listed_folder_is_accepted_while_the_default_syncs() {
    let root = DataRoot::new();
    let absent = root.path().join("never-created");
    let on = set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": absent}]}))
    .unwrap();
    assert_eq!(on["rejected"], serde_json::json!([]));
    assert_eq!(
        on["profiles"],
        serde_json::json!([{"id": "default"}, {"id": PROFILE_A}])
    );
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let registry = RwLock::new(config());
    let status = sync_round(&f.db, &url, &registry);
    assert_eq!(states(&status), ["ok", "notSignedIn"]);
    assert_eq!(status["state"], "ok");
    assert_eq!(seen.lock().unwrap().len(), 1);
    assert!(!absent.exists(), "the listed folder was created");
}

/// 7 ✱ WB1: an added profile's database that is a symlink, reached through a
/// junction onto another user's file, a hard link to another user's file, a
/// directory, a file carrying a reparse tag, or a junction, is refused with a
/// fixed reason before any request. Controls: the user's own file, an
/// Administrators-owned file (Cursor once run elevated), and a junction
/// ancestor onto the user's own folder are read.
#[test]
fn unsafe_profile_databases_send_nothing() {
    use crate::agent_storage_windows::TestOwner;
    let _guard = lock();
    let tmp = TempDir::new();
    let foreign_dir = tmp.path().join("foreign");
    let foreign_db = login_db(&foreign_dir, &other_token(), OTHER_USER);
    set_owner(&foreign_db, TestOwner::LocalSystem);
    let own = profile_root(tmp.path(), "own", &valid_token(), USER);
    let own_db = cursor_desktop::profile_db_path(&own);

    let linked = empty_root(tmp.path(), "linked");
    std::os::windows::fs::symlink_file(&own_db, cursor_desktop::profile_db_path(&linked))
        .expect("fixture: file symlink (needs elevation or developer mode)");
    let via_junction = tmp.path().join("via-junction");
    std::fs::create_dir_all(via_junction.join("User")).unwrap();
    junction(&via_junction.join("User").join("globalStorage"), &foreign_dir);
    let hard = empty_root(tmp.path(), "hard");
    std::fs::hard_link(&foreign_db, cursor_desktop::profile_db_path(&hard)).unwrap();
    let directory = empty_root(tmp.path(), "directory");
    std::fs::create_dir(cursor_desktop::profile_db_path(&directory)).unwrap();
    let tagged = profile_root(tmp.path(), "tagged", &valid_token(), USER);
    set_reparse_tag(&cursor_desktop::profile_db_path(&tagged), THIRD_PARTY_TAG);
    let junction_db = empty_root(tmp.path(), "junction-db");
    junction(
        &cursor_desktop::profile_db_path(&junction_db),
        &own.join("User").join("globalStorage"),
    );

    let registry = RwLock::new(multi(
        tmp.path(),
        true,
        vec![
            profile(PROFILE_A, &linked),
            profile(PROFILE_B, &via_junction),
            profile(PROFILE_C, &hard),
            profile(PROFILE_D, &directory),
            profile(PROFILE_E, &tagged),
            profile(PROFILE_F, &junction_db),
        ],
    ));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    clear_reparse_tag(&cursor_desktop::profile_db_path(&tagged), THIRD_PARTY_TAG);
    assert!(seen.lock().unwrap().is_empty(), "an unsafe database was sent: {status}");
    assert_eq!(
        states(&status),
        [
            "notSignedIn",
            "error:db_symlink",
            "error:db_foreign_owner",
            "error:db_foreign_owner",
            "error:db_not_file",
            "error:db_symlink",
            "error:db_symlink",
        ]
    );

    // Controls: read (the account is walked once, the others are duplicates
    // of it, which needs their token read).
    let admins = profile_root(tmp.path(), "admins", &valid_token(), USER);
    set_owner(&cursor_desktop::profile_db_path(&admins), TestOwner::Administrators);
    let via_own_junction = tmp.path().join("via-own-junction");
    std::fs::create_dir_all(via_own_junction.join("User")).unwrap();
    junction(
        &via_own_junction.join("User").join("globalStorage"),
        &own.join("User").join("globalStorage"),
    );
    *registry.write().unwrap() = multi(
        tmp.path(),
        true,
        vec![
            profile(PROFILE_A, &own),
            profile(PROFILE_B, &admins),
            profile(PROFILE_C, &via_own_junction),
        ],
    );
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok", "duplicate", "duplicate"]);
    assert_eq!(seen.lock().unwrap().len(), 1);
}

const THIRD_PARTY_TAG: u32 = 0x0000_0BEE;

/// A non-name-surrogate reparse tag on a file, the way a cloud placeholder
/// carries one (CMP-W0 measured `is_file()` true for it). A third-party tag,
/// not the Microsoft cloud tag: W0's cloud-tag fixtures could not be deleted
/// afterwards (residue on 188), and the check under test reads only the
/// handle's reparse attribute, which every tag sets.
fn set_reparse_tag(path: &Path, tag: u32) {
    let ok = reparse_ioctl(path, tag, true);
    assert!(ok, "fixture: FSCTL_SET_REPARSE_POINT failed on {path:?}");
}

fn clear_reparse_tag(path: &Path, tag: u32) {
    let _ = reparse_ioctl(path, tag, false);
}

fn reparse_ioctl(path: &Path, tag: u32, set: bool) -> bool {
    use std::os::windows::fs::OpenOptionsExt as _;
    use std::os::windows::io::AsRawHandle as _;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
    };
    const FSCTL_SET_REPARSE_POINT: u32 = 0x0009_00A4;
    const FSCTL_DELETE_REPARSE_POINT: u32 = 0x0009_00AC;
    let Ok(file) = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS)
        .open(path)
    else {
        return false;
    };
    let data: &[u8] = if set { &[0xAB; 8] } else { &[] };
    let mut buffer = Vec::new();
    buffer.extend_from_slice(&tag.to_le_bytes());
    buffer.extend_from_slice(&(data.len() as u16).to_le_bytes());
    buffer.extend_from_slice(&0u16.to_le_bytes());
    buffer.extend_from_slice(&[0x5A; 16]); // ReparseGuid (third-party tag)
    buffer.extend_from_slice(data);
    let mut returned = 0u32;
    let code = if set {
        FSCTL_SET_REPARSE_POINT
    } else {
        FSCTL_DELETE_REPARSE_POINT
    };
    unsafe {
        windows_sys::Win32::System::IO::DeviceIoControl(
            file.as_raw_handle() as _,
            code,
            buffer.as_ptr().cast(),
            buffer.len() as u32,
            std::ptr::null_mut(),
            0,
            &mut returned,
            std::ptr::null_mut(),
        ) != 0
    }
}

/// WB1: the file SQLite opens is held to the one the checks ran on. Here the
/// junction ancestor is swapped to another folder between the checked open
/// and SQLite's open: `db_changed`, nothing sent.
#[test]
fn a_database_swapped_after_the_checks_sends_nothing() {
    let _guard = lock();
    let tmp = TempDir::new();
    let first = login_db(&tmp.path().join("first"), &valid_token(), USER);
    set_owner(&first, crate::agent_storage_windows::TestOwner::CurrentUser);
    let second = login_db(&tmp.path().join("second"), &other_token(), OTHER_USER);
    set_owner(&second, crate::agent_storage_windows::TestOwner::CurrentUser);
    let root = tmp.path().join("root");
    std::fs::create_dir_all(root.join("User")).unwrap();
    let link = root.join("User").join("globalStorage");
    junction(&link, &tmp.path().join("first"));
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let swapped = std::sync::atomic::AtomicBool::new(false);
    let swap = || {
        std::fs::remove_dir(&link).expect("fixture: junction removed while a file under it is open");
        junction(&link, &tmp.path().join("second"));
        swapped.store(true, Ordering::SeqCst);
    };
    let status = cursor_desktop::with_between_opens(&swap, || {
        sync_round(&tmp.path().join("absent.vscdb"), &url, &registry)
    });
    assert!(swapped.load(Ordering::SeqCst), "fixture is inert: no swap");
    assert_eq!(states(&status), ["notSignedIn", "error:db_changed"]);
    assert!(seen.lock().unwrap().is_empty(), "the swapped database was sent");
    // Control: without a swap the same root is read and sent.
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
}

/// WB2: an added profile on a drive that is not fixed (network, removable,
/// optical, RAM disk, unknown) is never read; an unmounted letter is "not
/// signed in". The drive type comes through the seam a mapped drive in the
/// maintainer's interactive session answers `DRIVE_REMOTE` to (win188 runs in
/// session 0, where mapped drives do not exist).
#[test]
fn a_profile_on_a_drive_that_is_not_fixed_is_never_read() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    for (kind, expected) in [
        (DRIVE_REMOTE, "error:db_not_fixed_drive"),
        (DRIVE_REMOVABLE, "error:db_not_fixed_drive"),
        (DRIVE_CDROM, "error:db_not_fixed_drive"),
        (DRIVE_RAMDISK, "error:db_not_fixed_drive"),
        (0, "error:db_not_fixed_drive"),
        (cursor_desktop::DRIVE_NO_ROOT_DIR, "notSignedIn"),
    ] {
        let probed = std::sync::atomic::AtomicUsize::new(0);
        let drive = |path: &Path| {
            probed.fetch_add(1, Ordering::SeqCst);
            assert!(path.starts_with(&root), "drive probed for {path:?}");
            kind
        };
        let status = sync_round_with(
            &tmp.path().join("absent.vscdb"),
            &url,
            &registry,
            limits(),
            &drive,
        );
        assert_eq!(states(&status), ["notSignedIn", expected], "kind {kind}");
        assert_eq!(probed.load(Ordering::SeqCst), 1);
    }
    assert!(seen.lock().unwrap().is_empty(), "a non-fixed drive was read");
    // Control: the same root on a fixed drive is read and sent.
    let fixed = |_: &Path| cursor_desktop::DRIVE_FIXED;
    let status = sync_round_with(
        &tmp.path().join("absent.vscdb"),
        &url,
        &registry,
        limits(),
        &fixed,
    );
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    assert_eq!(seen.lock().unwrap().len(), 1);
}

/// WB2's second check: a database whose final path is a UNC path (reached
/// through a directory symlink onto `\\localhost\C$`) is `db_remote`. Runs
/// only where the loopback admin share is reachable: an S4U job (win188,
/// session 0) has no network credentials, so there it prints NOT RUN.
#[test]
fn a_database_behind_a_unc_path_is_refused() {
    let _guard = lock();
    let tmp = TempDir::new();
    let real = profile_root(tmp.path(), "real", &valid_token(), USER);
    let local = real.join("User").to_string_lossy().into_owned();
    let unc = format!(r"\\localhost\{}$\{}", &local[..1], &local[3..]);
    if std::fs::metadata(Path::new(&unc)).is_err() {
        eprintln!("NOT RUN: {unc} is not reachable from this session");
        return;
    }
    let root = tmp.path().join("root");
    std::fs::create_dir_all(&root).unwrap();
    std::os::windows::fs::symlink_dir(&unc, root.join("User")).unwrap();
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    eprintln!("measured: UNC final path -> {}", states(&status).join(","));
    assert_eq!(states(&status), ["notSignedIn", "error:db_remote"]);
    assert!(seen.lock().unwrap().is_empty());
}

/// 8 ✱: each profile's token goes only in its own requests, and no token or
/// account id reaches a file name, a file, or the status.
#[test]
fn each_profile_token_reaches_only_its_own_requests() {
    let _guard = lock();
    let tmp = TempDir::new();
    let (token_a, token_b) = (valid_token(), other_token());
    let default_db = login_db(&tmp.path().join("default"), &token_a, USER);
    let root = profile_root(tmp.path(), "b", &token_b, OTHER_USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![
        json_ok(&page(3, &["a", "b"])),
        json_ok(&page(3, &["c"])),
        json_ok(&page(1, &["d"])),
    ]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["ok", "ok"]);
    let seen = seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 3);
    for (i, request) in seen.iter().enumerate() {
        let cookie = header(&request.head, "cookie").unwrap();
        let (own, other) = if i < 2 {
            (&token_a, &token_b)
        } else {
            (&token_b, &token_a)
        };
        assert!(cookie.contains(own.as_str()), "request {i} lacks its own token");
        assert!(!request.head.contains(other.as_str()) && !request.body.contains(other.as_str()));
    }
    let mut outputs = vec![status.to_string()];
    for dir in [default_sync_dir(tmp.path()), profile_dir(tmp.path(), &profile(PROFILE_A, &root))] {
        for entry in std::fs::read_dir(&dir).unwrap().flatten() {
            outputs.push(entry.file_name().to_string_lossy().into_owned());
            outputs.push(std::fs::read_to_string(entry.path()).unwrap_or_default());
        }
    }
    let all = outputs.join("\n");
    assert!(all.contains("usage.") && all.contains("conversationId") && all.contains("\"ok\""));
    for canary in [token_a.as_str(), token_b.as_str(), SIGNATURE, USER, OTHER_USER] {
        assert!(!all.contains(canary), "canary leaked");
    }
}

/// WB9: per-profile canaries (tokens, account ids, a folder name) reach none
/// of the push result, the add-time validation result, the refusal codes,
/// the status, the file names or contents, or a panic's envelope.
#[test]
fn profile_canaries_reach_no_result_status_file_or_panic() {
    let root = DataRoot::new();
    let folder = root.path().join("ROOTCANARYFOLDER");
    let token_b = other_token();
    let profile_root_dir = profile_root(&folder, "b", &token_b, OTHER_USER);
    let mut outputs = Vec::new();
    let push = |profiles: serde_json::Value| {
        set(serde_json::json!({"enabled": true, "profiles": profiles}))
            .map_or_else(|e| e, |v| v.to_string())
    };
    outputs.push(push(serde_json::json!([{"id": PROFILE_A, "root": profile_root_dir}])));
    outputs.push(push(serde_json::json!([
        {"id": PROFILE_A, "root": profile_root_dir},
        {"id": PROFILE_B, "root": format!(r"{}\..\x", folder.display())},
    ])));
    outputs.push(push(serde_json::json!([{"id": "ROOTCANARYID", "root": profile_root_dir}])));
    outputs.push(
        validate_with(
            &serde_json::json!({"root": profile_root_dir, "existing": [profile_root_dir]})
                .to_string(),
            &cursor_desktop::drive_type,
            None,
        )
        .unwrap()
        .to_string(),
    );
    set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": profile_root_dir}]}))
    .unwrap();
    let token_a = valid_token();
    let default_db = login_db(&root.path().join("default-db"), &token_a, USER);
    let registry = RwLock::new(config());
    let (url, _) = serve(vec![
        json_ok(&page(1, &["a"])),
        json_ok(&page(1, &["b"])),
        json_ok(&page(1, &["c"])),
    ]);
    outputs.push(sync_round(&default_db, &url, &registry).to_string());
    for dir in [config().dir.unwrap(), config().profiles_dir.unwrap()] {
        let mut stack = vec![dir];
        while let Some(dir) = stack.pop() {
            for entry in std::fs::read_dir(&dir).unwrap().flatten() {
                outputs.push(entry.file_name().to_string_lossy().into_owned());
                if entry.file_type().unwrap().is_dir() {
                    stack.push(entry.path());
                } else {
                    outputs.push(std::fs::read_to_string(entry.path()).unwrap_or_default());
                }
            }
        }
    }
    // A panic while the login is held, through the entry point's guard.
    PANIC_AFTER_LOGIN.with(|flag| flag.set(true));
    let envelope = crate::guarded("tb_cursor_sync", || {
        crate::envelope(Ok(sync_round(&default_db, &url, &registry)))
    });
    PANIC_AFTER_LOGIN.with(|flag| flag.set(false));
    let panicked = unsafe { take(envelope) }.to_string();
    assert!(panicked.contains("panicked"), "{panicked}");
    outputs.push(panicked);
    let all = outputs.join("\n");
    for expected in ["\"ok\"", "invalidComponent", "invalidProfileId", "duplicate", "usage."] {
        assert!(all.contains(expected), "missing {expected} in {all}");
    }
    for canary in [
        token_a.as_str(),
        token_b.as_str(),
        SIGNATURE,
        USER,
        OTHER_USER,
        "ROOTCANARY",
    ] {
        assert!(!all.contains(canary), "canary {canary} leaked");
    }
}

/// 9 + WB4: the profile's root changes while its walk runs → nothing is
/// written for that walk and no further page is sent.
#[test]
fn root_changed_mid_walk_writes_nothing() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let reply = match json_ok(&page(4, &["a", "b"])) {
        Reply::Raw(bytes) => bytes,
        _ => unreachable!(),
    };
    let (url, seen) = serve(vec![
        Reply::Delayed(Duration::from_millis(300), reply),
        json_ok(&page(4, &["c", "d"])),
    ]);
    let status = std::thread::scope(|scope| {
        let walk = scope.spawn(|| sync_round(&tmp.path().join("absent.vscdb"), &url, &registry));
        let started = Instant::now();
        while seen.lock().unwrap().is_empty() {
            assert!(started.elapsed() < Duration::from_secs(5));
            std::thread::sleep(Duration::from_millis(5));
        }
        registry.write().unwrap().profiles[0].root = tmp.path().join("moved");
        walk.join().unwrap()
    });
    assert_eq!(states(&status), ["notSignedIn", "disabled"]);
    std::thread::sleep(Duration::from_millis(100));
    assert_eq!(seen.lock().unwrap().len(), 1, "a page was sent after the change");
    assert!(usage_names(&profile_dir(tmp.path(), &profile(PROFILE_A, &root))).is_empty());
}

/// 10 ✱: sync off sends nothing for any profile; turned off during the
/// first profile's walk, the next profile sends nothing.
#[test]
fn switch_off_sends_nothing_for_every_profile() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &other_token(), OTHER_USER);
    let registry = RwLock::new(multi(tmp.path(), false, vec![profile(PROFILE_A, &root)]));
    let reply = match json_ok(&page(1, &["a"])) {
        Reply::Raw(bytes) => bytes,
        _ => unreachable!(),
    };
    let (url, seen) = serve(vec![
        Reply::Delayed(Duration::from_millis(300), reply.clone()),
        Reply::Raw(reply),
    ]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(status["state"], "disabled");
    assert!(seen.lock().unwrap().is_empty());

    registry.write().unwrap().enabled = true;
    let status = std::thread::scope(|scope| {
        let walk = scope.spawn(|| sync_round(&default_db, &url, &registry));
        let started = Instant::now();
        while seen.lock().unwrap().is_empty() {
            assert!(started.elapsed() < Duration::from_secs(5));
            std::thread::sleep(Duration::from_millis(5));
        }
        registry.write().unwrap().enabled = false;
        walk.join().unwrap()
    });
    assert_eq!(states(&status), ["disabled", "disabled"]);
    std::thread::sleep(Duration::from_millis(100));
    assert_eq!(seen.lock().unwrap().len(), 1, "the next profile sent after off");
    assert!(!expected_file(&default_sync_dir(tmp.path())).exists());
}

/// 12: an S-1 mismatch in one profile refuses only that profile.
#[test]
fn account_mismatch_refuses_only_its_own_profile() {
    let _guard = lock();
    let tmp = TempDir::new();
    let mismatched = login_db(&tmp.path().join("default"), &valid_token(), OTHER_USER);
    let root = profile_root(tmp.path(), "b", &other_token(), OTHER_USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&mismatched, &url, &registry);
    assert_eq!(states(&status), ["error:account_mismatch", "ok"]);
    assert_eq!(status["state"], "error");
    // A file was written although the headline is an error.
    assert_eq!(status["changed"], true);
    assert_eq!(status["reason"], "account_mismatch");
    let seen = seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 1);
    assert!(header(&seen[0].head, "cookie").unwrap().contains(OTHER_USER));
}

/// One budget for the whole round: a spent one starts no profile.
#[test]
fn one_budget_covers_every_profile() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &other_token(), OTHER_USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["b"]))]);
    let status = sync_round_with(
        &default_db,
        &url,
        &registry,
        Limits {
            budget: Duration::ZERO,
            ..limits()
        },
        &cursor_desktop::drive_type,
    );
    assert_eq!(
        states(&status),
        ["partial:budget_exhausted", "partial:budget_exhausted"]
    );
    assert!(seen.lock().unwrap().is_empty());
}

/// An account-wide failure (429) still covers the account for the round, so
/// a second profile on it sends nothing.
#[test]
fn a_rate_limited_account_is_not_retried_from_another_profile() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![
        http("429 Too Many Requests", "application/json", "{}"),
        json_ok(&page(1, &["a"])),
    ]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["error:rate_limited", "duplicate"]);
    assert_eq!(seen.lock().unwrap().len(), 1, "the 429 was repeated from B");
}

/// WB4: a profile removed mid-walk (`disabled`) sends no further page, writes
/// nothing, and does not cover its account, so the next profile on it still
/// walks.
#[test]
fn a_profile_removed_mid_walk_does_not_cover_its_account() {
    let _guard = lock();
    let tmp = TempDir::new();
    let a = profile_root(tmp.path(), "a", &valid_token(), USER);
    let b = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(
        tmp.path(),
        true,
        vec![profile(PROFILE_A, &a), profile(PROFILE_B, &b)],
    ));
    let first_page = match json_ok(&page(4, &["a", "b"])) {
        Reply::Raw(bytes) => bytes,
        _ => unreachable!(),
    };
    let (url, seen) = serve(vec![
        Reply::Delayed(Duration::from_millis(300), first_page),
        json_ok(&page(1, &["c"])),
    ]);
    let status = std::thread::scope(|scope| {
        let walk = scope.spawn(|| sync_round(&tmp.path().join("absent.vscdb"), &url, &registry));
        let started = Instant::now();
        while seen.lock().unwrap().is_empty() {
            assert!(started.elapsed() < Duration::from_secs(5));
            std::thread::sleep(Duration::from_millis(5));
        }
        registry.write().unwrap().profiles.remove(0);
        walk.join().unwrap()
    });
    assert_eq!(states(&status), ["notSignedIn", "disabled", "ok"]);
    assert_eq!(seen.lock().unwrap().len(), 2, "A kept walking, or B was skipped");
    assert!(usage_names(&profile_dir(tmp.path(), &profile(PROFILE_A, &a))).is_empty());
    assert!(covers_account(Stop::error("rate_limited")));
    assert!(!covers_account(Stop::new(State::Disabled, None)));
}

/// FIX 6: the default profile follows a symlinked `state.vscdb`, as every
/// release did; an added profile's symlinked database is refused.
#[test]
fn a_symlinked_default_database_still_syncs() {
    let _guard = lock();
    let tmp = TempDir::new();
    let real = login_db(&tmp.path().join("real"), &valid_token(), USER);
    let link = tmp.path().join("link.vscdb");
    std::os::windows::fs::symlink_file(&real, &link).unwrap();
    let added = empty_root(tmp.path(), "added");
    let other = login_db(&tmp.path().join("other"), &other_token(), OTHER_USER);
    std::os::windows::fs::symlink_file(&other, cursor_desktop::profile_db_path(&added)).unwrap();
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &added)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["b"]))]);
    let status = sync_round(&link, &url, &registry);
    assert_eq!(states(&status), ["ok", "error:db_symlink"]);
    assert_eq!(seen.lock().unwrap().len(), 1);
}

/// S6: with no added folder the round reports one profile, the default, and
/// the headline is that profile's own outcome.
#[test]
fn a_default_only_round_reports_one_profile() {
    let _guard = lock();
    let f = fixture();
    let (url, _) = serve(vec![
        json_ok(&page(1, &["a"])),
        http("429 Too Many Requests", "application/json", "{}"),
    ]);
    let status = sync_status(&f, &url, &registry(true, &f.dir));
    assert_eq!(states(&status), ["ok"]);
    assert_eq!(status["profiles"][0]["id"], "default");
    assert_eq!(status["changed"], true);
    assert_eq!(status["lastSuccessMs"], status["profiles"][0]["lastSuccessMs"]);
    let status = sync_status(&f, &url, &registry(true, &f.dir));
    assert_eq!(status["state"], "error");
    assert_eq!(status["reason"], "rate_limited");
    assert_eq!(status["changed"], false);
}

// Registry: refusals, cleanup, scope --------------------------------------------

/// FIX 3: an off push is always accepted and turns the takeover off,
/// whatever its profiles carry. (Windows keeps `deny_unknown_fields`, so an
/// off push with an unknown key such as `dir` is still `invalidJson`:
/// `a_dir_key_is_refused_and_the_dir_is_chosen_here`.)
#[test]
fn an_off_push_is_always_accepted() {
    let root = DataRoot::new();
    let home = TempDir::new();
    let too_many: Vec<serde_json::Value> = (0..=MAX_PROFILES)
        .map(|i| serde_json::json!({"id": format!("0A1B2C3D-0000-4000-8000-{i:012}"), "root": r"C:\r"}))
        .collect();
    for profiles in [
        serde_json::json!([{"id": "/", "root": "relative"}]),
        serde_json::json!(too_many),
        serde_json::json!("not a list"),
        serde_json::json!([{"id": PROFILE_A, "root": r"C:\x", "dir": r"C:\y"}]),
    ] {
        set(serde_json::json!({"enabled": true})).unwrap();
        write_complete_file(&root.sync_dir());
        let off = set(serde_json::json!({"enabled": false, "profiles": profiles}))
            .unwrap_or_else(|e| panic!("{profiles}: {e}"));
        assert_eq!(off["enabled"], false, "{profiles}");
        assert_eq!(off["profiles"], serde_json::json!([]));
        assert!(!config().enabled);
        assert!(config().profiles.is_empty());
        assert_eq!(takeover(&config(), Some(home.path())), Takeover::Off);
        assert!(usage_names(&root.sync_dir()).is_empty(), "the off push cleaned nothing");
    }
}

/// 6 ✱ + WB3: whole-push refusals (shape, more than 32, an id that is not an
/// uppercase UUID, a duplicate id) change nothing but keep no profile; a
/// refused root is reported by index and the other entries register.
#[test]
fn bad_profile_ids_and_roots_are_rejected() {
    let root = DataRoot::new();
    let with = |profiles: serde_json::Value| {
        set(serde_json::json!({"enabled": true, "profiles": profiles}))
    };
    let too_many: Vec<serde_json::Value> = (0..=MAX_PROFILES)
        .map(|i| serde_json::json!({"id": format!("0A1B2C3D-0000-4000-8000-{i:012}"), "root": r"C:\r"}))
        .collect();
    for (bad, code) in [
        (serde_json::json!([{"id": "/", "root": r"C:\x\b"}]), "invalidProfileId"),
        (serde_json::json!([{"id": "..", "root": r"C:\x\b"}]), "invalidProfileId"),
        (serde_json::json!([{"id": format!(r"..\{PROFILE_A}"), "root": r"C:\x\b"}]), "invalidProfileId"),
        (serde_json::json!([{"id": PROFILE_A.to_lowercase(), "root": r"C:\x\b"}]), "invalidProfileId"),
        (serde_json::json!([{"id": PROFILE_A.replace('-', ""), "root": r"C:\x\b"}]), "invalidProfileId"),
        (serde_json::json!([{"id": "default", "root": r"C:\x\b"}]), "invalidProfileId"),
        (serde_json::json!([{"id": PROFILE_A, "root": r"C:\x\b"}, {"id": PROFILE_A, "root": r"C:\x\c"}]), "invalidProfileId"),
        (serde_json::json!(too_many), "tooManyProfiles"),
        (serde_json::json!([{"id": PROFILE_A, "root": r"C:\x\b", "dir": r"C:\tmp"}]), "invalidJson"),
        (serde_json::json!([{"id": PROFILE_A}]), "invalidJson"),
        (serde_json::json!({"id": PROFILE_A, "root": r"C:\x\b"}), "invalidJson"),
    ] {
        assert_eq!(with(bad.clone()), Err(code.to_string()), "{bad}");
        assert_eq!(config(), Config::default(), "rejected {bad} changed the registry");
    }
    assert!(
        names(&root.path().join(TEST_LOCAL_DIR)).is_empty(),
        "a refused push created storage"
    );

    // Per entry: each refused root by index, the rest registered.
    let cursor_own = cursor_own_root().unwrap();
    let ok = with(serde_json::json!([
        {"id": PROFILE_A, "root": r"C:\x\cursor-b"},
        {"id": PROFILE_B, "root": "relative"},
        {"id": PROFILE_C, "root": r"C:\"},
        {"id": PROFILE_D, "root": r"\\server\share\cursor"},
        {"id": PROFILE_E, "root": r"\\?\C:\x\y"},
        {"id": PROFILE_F, "root": r"C:\x\..\y"},
        {"id": "0A1B2C3D-0000-4000-8000-000000000010", "root": r"C:\x\con\y"},
        {"id": "0A1B2C3D-0000-4000-8000-000000000011", "root": cursor_own.to_string_lossy().to_uppercase()},
        {"id": "0A1B2C3D-0000-4000-8000-000000000012", "root": ""},
    ]))
    .unwrap();
    assert_eq!(
        ok["rejected"],
        serde_json::json!([
            {"index": 1, "reason": "unsupportedPath"},
            {"index": 2, "reason": "rootDirectory"},
            {"index": 3, "reason": "unsupportedPath"},
            {"index": 4, "reason": "unsupportedPath"},
            {"index": 5, "reason": "invalidComponent"},
            {"index": 6, "reason": "invalidComponent"},
            {"index": 7, "reason": "cursorDefault"},
            {"index": 8, "reason": "empty"},
        ])
    );
    assert_eq!(
        ok["profiles"],
        serde_json::json!([{"id": "default"}, {"id": PROFILE_A}])
    );
    assert_eq!(
        config().profiles,
        [profile(PROFILE_A, Path::new(r"C:\x\cursor-b"))]
    );
    let profiles_dir = PathBuf::from(ok["profilesDir"].as_str().unwrap());
    assert!(profiles_dir.starts_with(root.path().join(TEST_LOCAL_DIR)), "{profiles_dir:?}");
}

/// WB3: a refused on push keeps only the profiles it also lists: none when
/// its list cannot be read, the intersection when only the storage failed.
#[test]
fn a_refused_push_keeps_only_profiles_it_lists() {
    let root = DataRoot::new();
    let a = serde_json::json!({"id": PROFILE_A, "root": r"C:\x\a"});
    let b = serde_json::json!({"id": PROFILE_B, "root": r"C:\x\b"});
    set(serde_json::json!({"enabled": true, "profiles": [a, b]})).unwrap();
    assert_eq!(config().profiles.len(), 2);
    // A push keeps the exact spelling it was given (case included).
    let mixed = serde_json::json!({"id": PROFILE_C, "root": r"C:\X\MixedCase"});
    set(serde_json::json!({"enabled": true, "profiles": [a, b, mixed]})).unwrap();
    assert_eq!(
        raw_profiles(&config())[2],
        (PROFILE_C.to_string(), PathBuf::from(r"C:\X\MixedCase")),
        "the stored root was respelled"
    );
    set(serde_json::json!({"enabled": true, "profiles": [a, b]})).unwrap();
    assert_eq!(
        set(serde_json::json!({"enabled": true, "profiles": [a, {"id": "bad", "root": r"C:\x\c"}]})),
        Err("invalidProfileId".to_string())
    );
    assert!(config().profiles.is_empty(), "a refused push left a profile walking");
    assert!(config().enabled, "a refused push turned sync off");

    set(serde_json::json!({"enabled": true, "profiles": [a, b]})).unwrap();
    set_data_root_for_test(None);
    assert_eq!(
        set(serde_json::json!({"enabled": true, "profiles": [a]})),
        Err("storageUnavailable".to_string())
    );
    assert_eq!(
        raw_profiles(&config()),
        [(PROFILE_A.to_string(), PathBuf::from(r"C:\x\a"))]
    );
    drop(root);
}

/// WB3 acceptance: push [A, B], then [A, C with a refused root]: C is
/// reported by index, B's dir is cleaned, and the next round sends nothing
/// with B's token.
#[test]
fn a_profile_dropped_by_a_push_stops_and_its_dir_is_cleaned() {
    let root = DataRoot::new();
    let a = profile_root(root.path(), "root-a", &valid_token(), USER);
    let token_b = other_token();
    let b = profile_root(root.path(), "root-b", &token_b, OTHER_USER);
    set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": a}, {"id": PROFILE_B, "root": b}]}))
    .unwrap();
    let b_dir = config().profiles_dir.unwrap().join(profile_dir_name(&profile(PROFILE_B, &b)));
    drop(secure::ensure_dir(&b_dir).unwrap());
    write_secure(&b_dir.join(hex_name('b')), b"{}");

    let result = set(serde_json::json!({"enabled": true, "profiles": [
        {"id": PROFILE_A, "root": a},
        {"id": PROFILE_C, "root": "relative"},
    ]}))
    .unwrap();
    assert_eq!(
        result["rejected"],
        serde_json::json!([{"index": 1, "reason": "unsupportedPath"}])
    );
    assert_eq!(result["removedFiles"], 1);
    assert_eq!(result["cleanupFailed"], false);
    assert!(!b_dir.exists(), "B's emptied dir was left");

    let (url, seen) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["b"]))]);
    let registry = RwLock::new(config());
    let status = sync_round(&root.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    let seen = seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 1);
    assert!(seen.iter().all(|r| !r.head.contains(token_b.as_str())), "B's token was sent");
}

/// Turning sync off deletes only our files from every default and profile
/// dir (every `.secure` level, WB7), and removes a profile dir it emptied.
#[test]
fn disable_deletes_only_our_files_in_every_profile_dir() {
    let root = DataRoot::new();
    let local = root.path().join(TEST_LOCAL_DIR);
    std::fs::create_dir_all(&local).unwrap();
    let mut profile_dirs = Vec::new();
    for app in with_fallback(APP_DIR_NAME) {
        for parent in with_fallback(PROFILES_DIR_NAME) {
            for id in [PROFILE_A.to_string(), format!("{PROFILE_B}.secure")] {
                let dir = local.join(&app).join(&parent).join(id);
                secure_tree(&local, &dir);
                write_secure(&dir.join(hex_name('a')), b"x");
                write_secure(&dir.join(format!("{TEMP_FILE_PREFIX}1-1")), b"x");
                profile_dirs.push(dir);
            }
        }
    }
    let keep = &profile_dirs[0];
    write_secure(&keep.join("keep.txt"), b"x");
    write_secure(&keep.join("usage.not-ours.json"), b"x");
    secure_tree(root.path(), &root.sync_dir());
    write_secure(&root.sync_dir().join(hex_name('c')), b"x");

    let off = set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(off["removedFiles"], 2 * profile_dirs.len() + 1, "{off}");
    let mut left = names(keep);
    left.sort();
    assert_eq!(left, ["keep.txt", "usage.not-ours.json"]);
    for dir in &profile_dirs[1..] {
        assert!(!dir.exists(), "an emptied profile dir was left: {dir:?}");
    }
    assert!(usage_names(&root.sync_dir()).is_empty());
}

/// 5 ✱ + WB7: profiles removed while on (or left by an earlier run) are
/// cleaned of our files only; a junction at `<UUID>` or at
/// `cursor-profiles`, a non-UUID or lowercase dir, foreign files and kept
/// profiles are left alone.
#[test]
fn removed_and_orphaned_profiles_are_cleaned_and_nothing_else() {
    let root = DataRoot::new();
    let local = root.path().join(TEST_LOCAL_DIR);
    let parent = local.join(APP_DIR_NAME).join(PROFILES_DIR_NAME);
    secure_tree(&local, &parent);
    let temp = format!("{TEMP_FILE_PREFIX}9-9");
    // A (kept) holds a temp file of ours and a foreign file. A's bare
    // `<UUID>` dir is what review round 2's build left: its usage file, its
    // lock and its `.cursor-sync.root` record.
    let a = parent.join(profile_dir_name(&profile(PROFILE_A, Path::new(r"C:\x\a"))));
    secure_tree(&parent, &a);
    write_secure(&a.join(&temp), b"x");
    write_secure(&a.join("foreign.txt"), b"x");
    let a_bare = parent.join(PROFILE_A);
    secure_tree(&parent, &a_bare);
    write_secure(&a_bare.join(hex_name('a')), b"x");
    drop(secure::open_lock(&a_bare.join(LOCK_FILE_NAME)).unwrap());
    write_secure(&a_bare.join(LEGACY_ROOT_FILE_NAME), "0".repeat(64).as_bytes());
    // B (orphan) holds ours plus a foreign file named like usage; its
    // `.secure` variant holds ours.
    let b = parent.join(PROFILE_B);
    secure_tree(&parent, &b);
    write_secure(&b.join(hex_name('b')), b"x");
    write_secure(&b.join(&temp), b"x");
    write_secure(&b.join("usage.foreign.json"), b"x");
    let b_secure = parent.join(format!("{PROFILE_B}.secure"));
    secure_tree(&parent, &b_secure);
    write_secure(&b_secure.join(hex_name('b')), b"x");
    // C is a junction to a dir holding a file of ours: not followed.
    let outside = root.path().join("outside");
    secure_tree(root.path(), &outside);
    write_secure(&outside.join(hex_name('c')), b"x");
    junction(&parent.join(PROFILE_C), &outside);
    // Malformed names: not ours.
    for name in ["not-a-uuid", &PROFILE_D.to_lowercase()] {
        let dir = parent.join(name);
        secure_tree(&parent, &dir);
        write_secure(&dir.join(hex_name('d')), b"x");
    }
    // A junction in place of the `.secure` parent: never entered.
    let elsewhere = root.path().join("elsewhere");
    let planted = elsewhere.join(PROFILE_E);
    secure_tree(root.path(), &planted);
    write_secure(&planted.join(hex_name('e')), b"x");
    junction(
        &local.join(APP_DIR_NAME).join(format!("{PROFILES_DIR_NAME}.secure")),
        &elsewhere,
    );

    let result = set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": r"C:\x\a"}]}))
    .unwrap();
    // B's usage file and temp, B.secure's usage file, the bare dir's usage
    // file; the bare dir's lock and leftover record go uncounted.
    assert_eq!(result["removedFiles"], 4, "{result}");
    assert_eq!(result["cleanupFailed"], false);
    let mut left = names(&a);
    left.sort();
    assert_eq!(left, [temp.clone(), "foreign.txt".to_string()], "a listed dir was opened");
    assert!(!a_bare.exists(), "the earlier build's bare dir was left");
    assert_eq!(usage_names(&b), ["usage.foreign.json"], "B: only ours went");
    assert!(!b_secure.exists(), "B.secure was emptied and removed");
    assert_eq!(usage_names(&outside), [hex_name('c')], "the junction was followed");
    for name in ["not-a-uuid", &PROFILE_D.to_lowercase()] {
        assert_eq!(usage_names(&parent.join(name)), [hex_name('d')], "{name}");
    }
    assert_eq!(usage_names(&planted), [hex_name('e')], "the junction parent was entered");

    // Removing A while on: its files of ours go, its foreign file stays.
    let result = set(serde_json::json!({"enabled": true, "profiles": []})).unwrap();
    assert_eq!(result["removedFiles"], 1);
    assert_eq!(names(&a), ["foreign.txt".to_string()]);
}

/// WB4: removing a profile cleans its dir under the same secure lock a walk
/// commits under: while the lock is held, the push waits.
#[test]
fn removal_cleanup_waits_for_the_profile_lock() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": r"C:\x\a"}]}))
    .unwrap();
    let dir = config().profiles_dir.unwrap().join(profile_dir_name(&profile(PROFILE_A, Path::new(r"C:\x\a"))));
    drop(secure::ensure_dir(&dir).unwrap());
    let file = dir.join(hex_name('a'));
    write_secure(&file, b"{}");
    let held = secure::open_lock(&dir.join(LOCK_FILE_NAME)).unwrap();
    let done = std::sync::atomic::AtomicBool::new(false);
    std::thread::scope(|scope| {
        let push = scope.spawn(|| {
            let result = set(serde_json::json!({"enabled": true, "profiles": []}));
            done.store(true, Ordering::SeqCst);
            result
        });
        std::thread::sleep(Duration::from_millis(1500));
        assert!(!done.load(Ordering::SeqCst), "the cleanup did not wait for the lock");
        assert!(file.exists());
        drop(held);
        assert_eq!(push.join().unwrap().unwrap()["removedFiles"], 1);
    });
    assert!(!file.exists());
    drop(root);
}

/// WB5: added profiles live under the local base only; a profile dir under
/// the roaming base (another PC's, say) is never cleaned or scanned.
#[test]
fn profiles_live_under_the_local_base_only() {
    let root = DataRoot::new();
    let roaming_planted = root
        .path()
        .join(APP_DIR_NAME)
        .join(PROFILES_DIR_NAME)
        .join(PROFILE_B);
    secure_tree(root.path(), &roaming_planted);
    write_secure(&roaming_planted.join(hex_name('b')), b"{}");
    let on = set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": r"C:\x\a"}]}))
    .unwrap();
    let profiles_dir = PathBuf::from(on["profilesDir"].as_str().unwrap());
    let dir = PathBuf::from(on["dir"].as_str().unwrap());
    assert!(profiles_dir.starts_with(root.path().join(TEST_LOCAL_DIR)), "{profiles_dir:?}");
    assert!(!dir.starts_with(root.path().join(TEST_LOCAL_DIR)), "{dir:?}");
    let home = TempDir::new();
    assert_eq!(takeover(&config(), Some(home.path())), Takeover::Off);
    set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(usage_names(&roaming_planted), [hex_name('b')]);
}

/// WB8: the takeover registers only the current config's profiles. A profile
/// removed while its file could not be deleted (it fails the storage
/// contract) is reported and never scanned.
#[test]
fn a_removed_profiles_leftovers_are_never_registered() {
    let _root = DataRoot::new();
    let home = TempDir::new();
    set(serde_json::json!({"enabled": true, "profiles": [
        {"id": PROFILE_A, "root": r"C:\x\a"},
        {"id": PROFILE_B, "root": r"C:\x\b"},
    ]}))
    .unwrap();
    let b_dir = config()
        .profiles_dir
        .unwrap()
        .join(profile_dir_name(&profile(PROFILE_B, Path::new(r"C:\x\b"))));
    drop(secure::ensure_dir(&b_dir).unwrap());
    let stuck = b_dir.join(hex_name('b'));
    std::fs::write(&stuck, b"{}").unwrap(); // inherited ACL: the secure delete refuses it
    let Takeover::On { files, .. } = takeover(&config(), Some(home.path())) else {
        panic!("control: B's file is registered while B is listed");
    };
    assert_eq!(files, [stuck.clone()]);

    let result = set(serde_json::json!({"enabled": true, "profiles": [
        {"id": PROFILE_A, "root": r"C:\x\a"},
    ]}))
    .unwrap();
    assert_eq!(result["cleanupFailed"], true);
    assert_eq!(result["cleanupFailedProfiles"], serde_json::json!([PROFILE_B]));
    assert!(stuck.exists(), "fixture is inert: the file was deleted");
    assert_eq!(takeover(&config(), Some(home.path())), Takeover::Off);
    set(serde_json::json!({"enabled": false})).unwrap_err();
    std::fs::remove_file(&stuck).unwrap();
}

/// Item 2: the add-time check, validate-only. Refusals in order, by folded
/// path and by directory identity (a junction to a listed folder or to
/// Cursor's own); an unreadable folder is never an identity match; nothing
/// is committed and no path is echoed.
#[test]
fn add_time_validation_answers_without_committing() {
    let _guard = lock();
    let tmp = TempDir::new();
    let cursor_own = tmp.path().join("Cursor");
    std::fs::create_dir_all(&cursor_own).unwrap();
    let listed = tmp.path().join("listed");
    std::fs::create_dir_all(&listed).unwrap();
    let fresh = tmp.path().join("fresh");
    std::fs::create_dir_all(&fresh).unwrap();
    let to_listed = tmp.path().join("to-listed");
    junction(&to_listed, &listed);
    let to_cursor = tmp.path().join("to-cursor");
    junction(&to_cursor, &cursor_own);
    let before = config();
    let check = |root: &Path, existing: Vec<String>, drive: &DriveType<'_>| {
        let raw = serde_json::json!({"root": root, "existing": existing}).to_string();
        let result = validate_with(&raw, drive, Some(&cursor_own)).unwrap();
        assert!(!result.to_string().contains("listed"), "a path was echoed: {result}");
        result["reason"].as_str().map(str::to_string)
    };
    fn fixed_drive(_: &Path) -> u32 {
        cursor_desktop::DRIVE_FIXED
    }
    fn remote_drive(_: &Path) -> u32 {
        DRIVE_REMOTE
    }
    fn removable_drive(_: &Path) -> u32 {
        DRIVE_REMOVABLE
    }
    let fixed: &DriveType<'_> = &fixed_drive;
    let listed_str = vec![listed.to_string_lossy().into_owned()];
    let full: Vec<String> = (0..MAX_PROFILES).map(|i| format!(r"C:\p{i}")).collect();
    let cases: Vec<(PathBuf, Vec<String>, &DriveType<'_>, Option<&str>)> = vec![
        (fresh.clone(), full, fixed, Some("tooManyProfiles")),
        (PathBuf::from("relative"), vec![], fixed, Some("unsupportedPath")),
        (PathBuf::from(r"C:\"), vec![], fixed, Some("rootDirectory")),
        (PathBuf::from(r"\\server\share\x"), vec![], fixed, Some("unsupportedPath")),
        (PathBuf::from(r"C:\x\con"), vec![], fixed, Some("invalidComponent")),
        (fresh.clone(), vec![], &remote_drive, Some("notFixedDrive")),
        (fresh.clone(), vec![], &removable_drive, Some("notFixedDrive")),
        (PathBuf::from(cursor_own.to_string_lossy().to_uppercase()), vec![], fixed, Some("cursorDefault")),
        (to_cursor.clone(), vec![], fixed, Some("cursorDefault")),
        (PathBuf::from(listed.to_string_lossy().to_uppercase()), listed_str.clone(), fixed, Some("duplicate")),
        (to_listed.clone(), listed_str.clone(), fixed, Some("duplicate")),
        (tmp.path().join("absent"), listed_str.clone(), fixed, None),
        (fresh.clone(), listed_str.clone(), fixed, None),
    ];
    for (root, existing, drive, expected) in cases {
        assert_eq!(
            check(&root, existing, drive).as_deref(),
            expected,
            "{root:?}"
        );
    }
    assert_eq!(config(), before, "validation committed something");
    assert_eq!(raw_profiles(&config()), raw_profiles(&before));
    assert_eq!(
        validate_with(r#"{"root":"C:\\x","extra":1}"#, fixed, None),
        Err("invalidJson".to_string())
    );
}

// Reports (child processes: hermetic engine roots) -------------------------------

fn e2e_event(id: &str, ts: &str, input: i64, output: i64, cache_read: i64, cents: f64) -> serde_json::Value {
    serde_json::json!({
        "conversationId": id, "timestamp": ts, "model": "gpt-5",
        "chargedCents": 1,
        "tokenUsage": {"inputTokens": input, "outputTokens": output,
                       "cacheReadTokens": cache_read, "totalCents": cents},
    })
}

fn x_event() -> serde_json::Value {
    e2e_event("conv-x", "1788256800000", 100, 20, 30, 50.0)
}

fn y_event() -> serde_json::Value {
    e2e_event("conv-y", "1788260400000", 200, 40, 0, 25.0)
}

fn z_event() -> serde_json::Value {
    e2e_event("conv-z", "1788264000000", 400, 80, 0, 10.0)
}

/// A usage file as the sync writes it, named for `account` (stands in for
/// `HMAC(sub)`), `age_secs` old.
fn usage_file(dir: &Path, account: char, events: &[serde_json::Value], age_secs: u64) -> PathBuf {
    std::fs::create_dir_all(dir).unwrap();
    let path = dir.join(hex_name(account));
    let body = serde_json::json!({"totalUsageEventsCount": events.len(), "usageEventsDisplay": events});
    std::fs::write(&path, body.to_string()).unwrap();
    std::fs::File::options()
        .write(true)
        .open(&path)
        .unwrap()
        .set_modified(SystemTime::now() - Duration::from_secs(age_secs))
        .unwrap();
    path
}

/// (input, output, cost, sorted session ids) of the Cursor rows, scanned the
/// way the process context is: the takeover's files, or (`legacy_dir`) one
/// directory registered the way builds before CMP-W did.
fn report_with(home: &Path, config: &Config, legacy_dir: Option<&Path>) -> (i64, i64, f64, Vec<String>) {
    let mut settings = tokscale_core::scanner::ScannerSettings::default();
    match legacy_dir {
        None => apply_takeover(&mut settings, &takeover(config, Some(home))),
        Some(dir) => apply_takeover(
            &mut settings,
            &Takeover::On {
                cli_root: cli_root(home),
                files: vec![dir.to_path_buf()],
            },
        ),
    }
    let options = tokscale_core::ReportOptions {
        home_dir: Some(home.to_string_lossy().into_owned()),
        use_env_roots: false,
        clients: Some(vec!["cursor".to_string()]),
        group_by: tokscale_core::GroupBy::Session,
        scanner_settings: settings,
        ..Default::default()
    };
    let report = crate::RUNTIME
        .block_on(tokscale_core::get_model_report(options))
        .expect("model report");
    let mut sessions: Vec<String> = report
        .entries
        .iter()
        .map(|e| e.session_id.clone().unwrap_or_default())
        .collect();
    sessions.sort();
    (report.total_input, report.total_output, report.total_cost, sessions)
}

fn report(home: &Path, config: &Config) -> (i64, i64, f64, Vec<String>) {
    report_with(home, config, None)
}

/// 1 ✱: one account synced into two profiles (same file name) is counted
/// once. Control: a different account in the added profile counts as well.
#[test]
fn same_account_in_two_profiles_counts_once() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::same_account_in_two_profiles_counts_once",
            "cmp-same",
        );
    };
    let base = root.join("appdata");
    let config = multi_plain(&base, vec![profile(PROFILE_A, Path::new(r"C:\x\a"))]);
    usage_file(&default_sync_dir(&base), 'a', &[x_event()], 10);
    let copy = usage_file(&profile_dir(&base, &profile(PROFILE_A, Path::new(r"C:\x\a"))), 'a', &[x_event()], 0);
    let (input, output, cost, sessions) = report(&root, &config);
    assert_eq!((input, output), (100, 20), "double counted");
    assert!((cost - 0.5).abs() < 1e-9, "cost {cost}");
    assert_eq!(sessions, ["conv-x"]);

    std::fs::remove_file(copy).unwrap();
    usage_file(&profile_dir(&base, &profile(PROFILE_A, Path::new(r"C:\x\a"))), 'b', &[y_event()], 0);
    let (input, output, cost, sessions) = report(&root, &config);
    assert_eq!((input, output), (300, 60));
    assert!((cost - 0.75).abs() < 1e-9, "cost {cost}");
    assert_eq!(sessions, ["conv-x", "conv-y"]);
}

/// A registry over plain (non-secure) dirs, for report fixtures.
fn multi_plain(base: &Path, profiles: Vec<Profile>) -> Config {
    Config {
        enabled: true,
        dir: Some(default_sync_dir(base)),
        profiles_dir: Some(base.join(PROFILES_DIR_NAME)),
        profiles,
        cli_takeover_confirmed: false,
    }
}

/// 2 ✱: the default dir holds X (newest) and an old Y left by a failed
/// delete; profile A holds the newest Y (in its `.secure` sibling); B holds
/// another account Z. Each counts once and Z is not hidden. Mutation:
/// register the dirs instead of the files.
#[test]
fn a_stale_copy_in_another_profile_is_never_scanned() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::a_stale_copy_in_another_profile_is_never_scanned",
            "cmp-stale",
        );
    };
    let base = root.join("appdata");
    let config = multi_plain(
        &base,
        vec![
            profile(PROFILE_A, Path::new(r"C:\x\a")),
            profile(PROFILE_B, Path::new(r"C:\x\b")),
        ],
    );
    usage_file(&default_sync_dir(&base), 'a', &[x_event()], 0);
    usage_file(&default_sync_dir(&base), 'b', &[y_event()], 100);
    let a_secure = base
        .join(PROFILES_DIR_NAME)
        .join(format!("{}.secure", profile_dir_name(&profile(PROFILE_A, Path::new(r"C:\x\a")))));
    let newest_y = usage_file(&a_secure, 'b', &[y_event()], 0);
    usage_file(&profile_dir(&base, &profile(PROFILE_B, Path::new(r"C:\x\b"))), 'c', &[z_event()], 50);

    let (input, output, cost, sessions) = report(&root, &config);
    assert_eq!((input, output), (700, 140), "a stale copy was counted");
    assert!((cost - 0.85).abs() < 1e-9, "cost {cost}");
    assert_eq!(sessions, ["conv-x", "conv-y", "conv-z"]);
    let Takeover::On { files, .. } = takeover(&config, Some(&root)) else {
        panic!("takeover off");
    };
    assert!(files.contains(&newest_y), "{files:?}");
    assert_eq!(files.len(), 3);
}

/// 3 + S6: the default profile's existing file keeps counting once, alone or
/// with an added profile on the same account; only files are registered.
#[test]
fn upgrade_keeps_the_existing_file_and_registers_no_directory() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::upgrade_keeps_the_existing_file_and_registers_no_directory",
            "cmp-upgrade",
        );
    };
    let base = root.join("appdata");
    usage_file(&default_sync_dir(&base), 'a', &[x_event(), y_event()], 0);
    for config in [
        multi_plain(&base, vec![]),
        multi_plain(&base, vec![profile(PROFILE_A, Path::new(r"C:\x\a"))]),
    ] {
        if !config.profiles.is_empty() {
            usage_file(&profile_dir(&base, &profile(PROFILE_A, Path::new(r"C:\x\a"))), 'a', &[x_event(), y_event()], 5);
        }
        let (input, _, _, sessions) = report(&root, &config);
        assert_eq!(input, 300, "{config:?}");
        assert_eq!(sessions, ["conv-x", "conv-y"]);
        let mut settings = tokscale_core::scanner::ScannerSettings::default();
        apply_takeover(&mut settings, &takeover(&config, Some(&root)));
        let cursor = &settings.extra_scan_paths["cursor"];
        assert_eq!(cursor.len(), 1);
        assert!(cursor.iter().all(|p| p.is_file()), "a directory was registered: {cursor:?}");
    }
}

/// Rollback: a build from before CMP-W registers `cursor-cache` as a dir. An
/// added profile's copy of the same account lives under the local
/// `cursor-profiles`, which that build never walks: counted once.
#[test]
fn rollback_scan_of_cursor_cache_counts_the_account_once() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::rollback_scan_of_cursor_cache_counts_the_account_once",
            "cmp-rollback",
        );
    };
    let base = root.join("appdata");
    usage_file(&default_sync_dir(&base), 'a', &[x_event()], 0);
    usage_file(&profile_dir(&base, &profile(PROFILE_A, Path::new(r"C:\x\a"))), 'a', &[x_event()], 0);
    usage_file(&profile_dir(&base, &profile(PROFILE_B, Path::new(r"C:\x\b"))), 'b', &[y_event()], 0);
    let (input, _, _, sessions) =
        report_with(&root, &Config::default(), Some(&default_sync_dir(&base)));
    assert_eq!(input, 100);
    assert_eq!(sessions, ["conv-x"]);
}

/// 4: an account switch in one profile leaves only the new account's file,
/// and the report shows only the new account's usage there.
#[test]
fn account_switch_in_a_profile_leaves_only_the_new_file() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::account_switch_in_a_profile_leaves_only_the_new_file",
            "cmp-switch",
        );
    };
    let base = root.join("appdata");
    std::fs::create_dir_all(&base).unwrap();
    let registry = RwLock::new(multi(&base, true, vec![]));
    let cursor_root = profile_root(&root, "b", &other_token(), OTHER_USER);
    let old_dir = profile_dir(&base, &profile(PROFILE_A, &cursor_root));
    drop(secure::ensure_dir(&old_dir).unwrap());
    write_secure(&old_dir.join(hex_name('a')), x_body().as_bytes());
    registry.write().unwrap().profiles = vec![profile(PROFILE_A, &cursor_root)];
    let body = serde_json::json!({"totalUsageEventsCount": 1, "usageEventsDisplay": [y_event()]});
    let (url, _) = serve(vec![json_ok(&body.to_string())]);
    let status = sync_round(&root.join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    assert_eq!(status["state"], "ok", "no default login is not a problem here");
    assert_eq!(
        usage_names(&old_dir),
        [format!("usage.{}.json", stem(OTHER_USER).unwrap())]
    );
    let (input, _, _, sessions) = report(&root, &registry.read().unwrap());
    assert_eq!(input, 200);
    assert_eq!(sessions, ["conv-y"]);
}

fn x_body() -> String {
    serde_json::json!({"totalUsageEventsCount": 1, "usageEventsDisplay": [x_event()]}).to_string()
}

/// FIX 2 ✱: a walk failure specific to that profile's session does not cover
/// the account (`covers_account`: expired, removed mid-walk,
/// `client_unavailable`, `write_failed`; any other failure, a 429 say, does
/// cover it). The default profile's session for X is refused (401); profile
/// A on the same account then tries its own session, and X still counts
/// once.
#[test]
fn a_failed_walk_does_not_cover_the_account_for_later_profiles() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::a_failed_walk_does_not_cover_the_account_for_later_profiles",
            "cmp-failed",
        );
    };
    let base = root.join("appdata");
    std::fs::create_dir_all(&base).unwrap();
    let default_db = login_db(&root.join("default"), &valid_token(), USER);
    let cursor_root = profile_root(&root, "b", &valid_token(), USER);
    let registry = RwLock::new(multi(&base, true, vec![profile(PROFILE_A, &cursor_root)]));
    let (url, seen) = serve(vec![
        http("401 Unauthorized", "application/json", "{}"),
        json_ok(&x_body()),
    ]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["expired", "ok"]);
    assert_eq!(seen.lock().unwrap().len(), 2, "A never tried its own session");
    assert!(expected_file(&profile_dir(&base, &profile(PROFILE_A, &cursor_root))).exists());
    let (input, _, _, sessions) = report(&root, &registry.read().unwrap());
    assert_eq!(input, 100);
    assert_eq!(sessions, ["conv-x"]);
}

/// WB8 in the report: a profile no longer listed contributes nothing, even
/// with its newest file still on disk.
#[test]
fn a_removed_profile_is_not_in_the_report() {
    let Some(root) = crate::roots_acceptance::child_root() else {
        return crate::roots_acceptance::run_in_child_at(
            "cursor_sync::tests::a_removed_profile_is_not_in_the_report",
            "cmp-removed",
        );
    };
    let base = root.join("appdata");
    usage_file(&default_sync_dir(&base), 'a', &[x_event()], 0);
    usage_file(&profile_dir(&base, &profile(PROFILE_B, Path::new(r"C:\x\b"))), 'b', &[y_event()], 0);
    let listed = multi_plain(&base, vec![profile(PROFILE_B, Path::new(r"C:\x\b"))]);
    assert_eq!(report(&root, &listed).3, ["conv-x", "conv-y"], "control");
    let removed = multi_plain(&base, vec![]);
    assert_eq!(report(&root, &removed).3, ["conv-x"]);
}

/// WB2 with a real mapped drive, for the maintainer's interactive session
/// (mapped drives do not exist in win188's session 0). `CMPW_MAPPED_ROOT` is
/// a folder on a `net use` drive letter; the real `GetDriveTypeW` must
/// answer non-fixed, the sync must refuse it before any I/O and the add-time
/// check must refuse it too.
#[test]
#[ignore = "manual: needs CMPW_MAPPED_ROOT on a `net use` drive in an interactive session"]
fn a_mapped_drive_is_never_read() {
    let Some(root) = std::env::var_os("CMPW_MAPPED_ROOT").map(PathBuf::from) else {
        eprintln!("NOT RUN: set CMPW_MAPPED_ROOT");
        return;
    };
    let _guard = lock();
    let kind = cursor_desktop::drive_type(&root);
    eprintln!("measured: GetDriveTypeW({}) = {kind}", root.display());
    assert_eq!(kind, DRIVE_REMOTE, "not a mapped network drive");
    assert_eq!(
        cursor_desktop::read_login_added(&root, &cursor_desktop::drive_type).err(),
        Some("db_not_fixed_drive")
    );
    let tmp = TempDir::new();
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "error:db_not_fixed_drive"]);
    assert!(seen.lock().unwrap().is_empty());
    let raw = serde_json::json!({"root": root, "existing": []}).to_string();
    assert_eq!(
        validate_with(&raw, &cursor_desktop::drive_type, None).unwrap()["reason"],
        "notFixedDrive"
    );
}

// /code-review (high) findings on W1 ----------------------------------------------

fn delayed(reply: Reply) -> Reply {
    match reply {
        Reply::Raw(bytes) => Reply::Delayed(Duration::from_millis(300), bytes),
        _ => unreachable!(),
    }
}

/// Runs `round` on another thread, waits for its first request, then calls
/// `change` while that request is in flight.
fn change_mid_walk(
    seen: &Mutex<Vec<Seen>>,
    round: impl FnOnce() -> serde_json::Value + Send,
    change: impl FnOnce(),
) -> serde_json::Value {
    std::thread::scope(|scope| {
        let walk = scope.spawn(round);
        let started = Instant::now();
        while seen.lock().unwrap().is_empty() {
            assert!(started.elapsed() < Duration::from_secs(5));
            std::thread::sleep(Duration::from_millis(5));
        }
        change();
        walk.join().unwrap()
    })
}

/// Finding 1: the 60 s background budget applies once every profile that
/// could write a file has one. A profile whose last round was `notSignedIn`
/// or `duplicate` (or refused) writes none however long the round runs, so it
/// no longer holds every later round at the full budget. A profile changed
/// since (another root) may take the full budget for its first round, and
/// "Sync now" always does.
#[test]
fn a_profile_that_cannot_write_a_file_does_not_hold_the_full_budget() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let empty = empty_root(tmp.path(), "empty");
    let same = profile_root(tmp.path(), "same", &valid_token(), USER);
    let registry = RwLock::new(multi(
        tmp.path(),
        true,
        vec![profile(PROFILE_A, &empty), profile(PROFILE_B, &same)],
    ));
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])); 6]);
    let round = |explicit: bool| {
        let source = || registry.read().unwrap().clone();
        let status = sync_with(explicit, &target(&default_db, &url), &source, Some(limits())).0;
        (states(&status), ROUND_BUDGET.with(std::cell::Cell::get).unwrap())
    };
    let (states, budget) = round(false);
    assert_eq!(states, ["ok", "notSignedIn", "duplicate"]);
    assert_eq!(budget, FULL_BUDGET, "the default had no file yet");
    assert_eq!(round(false).1, BACKGROUND_BUDGET);
    assert_eq!(round(true).1, FULL_BUDGET, "Sync now");
    assert_eq!(round(false).1, BACKGROUND_BUDGET);
    registry.write().unwrap().profiles[0].root = tmp.path().join("moved");
    assert_eq!(round(false).1, FULL_BUDGET, "a changed profile's first round");
    assert_eq!(round(false).1, BACKGROUND_BUDGET);
}

/// Finding 5: the default profile's walk does not depend on the added
/// profiles. Adding the first one (which sets `profiles_dir`) or removing
/// the last (which clears it) while the default walks leaves that walk alone.
#[test]
fn adding_or_removing_profiles_mid_walk_leaves_the_default_walk_alone() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let mut default_only = multi(tmp.path(), true, vec![]);
    default_only.profiles_dir = None;
    let with_a = multi(tmp.path(), true, vec![profile(PROFILE_A, &tmp.path().join("a"))]);

    let registry = RwLock::new(default_only.clone());
    let (url, seen) = serve(vec![delayed(json_ok(&page(1, &["a"])))]);
    let status = change_mid_walk(
        &seen,
        || sync_round(&default_db, &url, &registry),
        || *registry.write().unwrap() = with_a.clone(),
    );
    assert_eq!(states(&status), ["ok"]);
    assert!(expected_file(&default_sync_dir(tmp.path())).exists());

    *registry.write().unwrap() = with_a;
    let (url, seen) = serve(vec![delayed(json_ok(&page(1, &["b"])))]);
    let status = change_mid_walk(
        &seen,
        || sync_round(&default_db, &url, &registry),
        || *registry.write().unwrap() = default_only,
    );
    assert_eq!(states(&status), ["ok", "disabled"]);
    assert_eq!(status["changed"], true, "the default wrote its new file");
}

/// Finding 6: a profile removed while its last page is in flight writes
/// nothing and creates nothing: no `<UUID>` dir, no lock file.
#[test]
fn a_profile_removed_during_its_last_page_leaves_no_dir() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![delayed(json_ok(&page(1, &["a"])))]);
    let status = change_mid_walk(
        &seen,
        || sync_round(&tmp.path().join("absent.vscdb"), &url, &registry),
        || registry.write().unwrap().profiles.clear(),
    );
    assert_eq!(states(&status), ["notSignedIn", "disabled"]);
    assert_eq!(seen.lock().unwrap().len(), 1, "control: the last page was answered");
    let dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    assert!(!dir.exists(), "the removed profile's dir was created");
    assert!(!Slot { id: PROFILE_A.into(), root: Some(root), dir: Some(dir) }.candidates()[1].exists());
}

/// Finding 3: a profile that now resolves to `duplicate` (its folder signed
/// in to the account an earlier profile covered) drops the file of the
/// account it held before, under its lock, so that account is no longer
/// registered; its current account's copy stays (the takeover keeps the
/// newest copy by name), as a commit keeps it.
#[test]
fn a_duplicate_profile_drops_its_old_accounts_file() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let a_dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    let old_name = format!("usage.{}.json", stem(OTHER_USER).unwrap());
    write_secure(&a_dir.join(&old_name), b"{}");
    let current = expected_file(&a_dir);
    write_secure(&current, b"{}");
    assert!(
        newest_per_account(&registry.read().unwrap()).iter().any(|p| p.ends_with(&old_name)),
        "control: the old account's file is registered before the round"
    );
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["ok", "duplicate"]);
    assert!(!a_dir.join(&old_name).exists(), "the old account's file stayed");
    assert!(current.exists(), "the current account's copy was removed");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([]));
    // Round-4 finding 4: a dir that keeps a file keeps its lock; no rmdir.
    assert!(a_dir.join(LOCK_FILE_NAME).exists(), "the kept dir's lock was deleted");
    let registered = newest_per_account(&registry.read().unwrap());
    assert!(registered.iter().all(|p| !p.ends_with(&old_name)), "{registered:?}");
    assert_eq!(registered.len(), 1);
}

/// Finding 3: a profile listed again under the same id with another root
/// starts clean (its dir name follows the root, so the old dir is unlisted
/// and cleaned like a removed one); the same root keeps its file.
#[test]
fn a_profile_moved_to_another_root_is_cleaned() {
    let root = DataRoot::new();
    let push = |at: &str| {
        set(serde_json::json!({"enabled": true, "profiles": [{"id": PROFILE_A, "root": at}]}))
            .unwrap()
    };
    push(r"C:\x\a");
    let dir = config().profiles_dir.unwrap().join(profile_dir_name(&profile(PROFILE_A, Path::new(r"C:\x\a"))));
    let file = dir.join(hex_name('a'));
    write_secure(&file, b"{}");
    assert_eq!(push(r"C:\x\a")["removedFiles"], 0);
    assert!(file.exists(), "control: the same root kept its file");
    assert_eq!(push(r"C:\x\b")["removedFiles"], 1);
    assert!(!dir.exists(), "the old root's dir was left");
    drop(root);
}

/// Finding 8: `changed` only when a file's content changed. A walk whose
/// bytes equal the file's does not rewrite it and is not `changed` (the app
/// refreshes on `changed`); its modified time still moves, so
/// `lastSuccessMs` dates this walk. New content is `changed`. "Not
/// rewritten" is observed through a std reader held open: a replace fails
/// under one (`a_std_reader_holding_the_file_fails_the_walk_cleanly`).
#[test]
fn an_identical_walk_changes_nothing() {
    let _guard = lock();
    let f = fixture();
    let (url, _) = serve(vec![
        json_ok(&page(1, &["a"])),
        json_ok(&page(1, &["a"])),
        json_ok(&page(1, &["b"])),
    ]);
    let on = registry(true, &f.dir);
    let first = sync_status(&f, &url, &on);
    assert_eq!(first["changed"], true);
    let path = expected_file(&f.dir);
    let bytes = std::fs::read(&path).unwrap();
    std::thread::sleep(Duration::from_millis(50));
    let reader = std::fs::File::open(&path).unwrap();
    let again = sync_status(&f, &url, &on);
    drop(reader);
    assert_eq!(states(&again), ["ok"], "the identical file was replaced");
    assert_eq!(again["changed"], false, "an identical rewrite read as a change");
    assert_eq!(std::fs::read(&path).unwrap(), bytes);
    assert!(
        again["lastSuccessMs"].as_u64() > first["lastSuccessMs"].as_u64(),
        "{again} {first}"
    );
    let new = sync_status(&f, &url, &on);
    assert_eq!(new["changed"], true);
}

// Round 2 of the /code-review findings ------------------------------------------

/// Pushes profile A at `at` and syncs once, so A's dir holds its account's
/// file. Returns A's dir and the file.
fn synced_at(root: &DataRoot, at: &Path) -> (PathBuf, PathBuf) {
    set(serde_json::json!({"enabled": true, "profiles": [{"id": PROFILE_A, "root": at}]}))
        .unwrap();
    let registry = RwLock::new(config());
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&root.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    let dir = config().profiles_dir.unwrap().join(profile_dir_name(&profile(PROFILE_A, at)));
    let file = dir.join(format!("usage.{}.json", stem(OTHER_USER).unwrap()));
    assert!(file.exists(), "fixture: no commit");
    (dir, file)
}

/// Round-2 finding 1 / round-3 A: a push with a new root cleans the old
/// account's file even when the in-memory config no longer knows the old
/// root (after a refused push that kept no profile, after a restart): the
/// old root's dir has another name, so it is unlisted.
#[test]
fn a_new_root_cleans_the_old_file_after_a_refused_push_or_a_restart() {
    let root = DataRoot::new();
    let old = profile_root(root.path(), "old", &other_token(), OTHER_USER);
    let new = root.path().join("new");
    let push_new = || {
        set(serde_json::json!({"enabled": true, "profiles": [{"id": PROFILE_A, "root": new}]}))
            .unwrap()
    };

    let (_, file) = synced_at(&root, &old);
    let refused = set(serde_json::json!({"enabled": true, "profiles": [
        {"id": PROFILE_A, "root": new}, {"id": "not-a-uuid", "root": new}]}));
    assert_eq!(refused, Err("invalidProfileId".to_string()));
    assert!(config().profiles.is_empty(), "fixture: the refusal kept a profile");
    assert!(file.exists(), "fixture: the refusal itself cleaned");
    assert_eq!(push_new()["removedFiles"], 1);
    assert!(!file.exists(), "after a refused push the old root's file stayed");

    let (_, file) = synced_at(&root, &old);
    *CONFIG.write().unwrap() = Config::default();
    assert_eq!(push_new()["removedFiles"], 1);
    assert!(!file.exists(), "after a restart the old root's file stayed");
}

/// Round-2 finding 8 / round-3 A: roots compare as `duplicate_key` folds
/// them; a case-only (or `/` vs `\`) respelling names the same dir and keeps
/// the file.
#[test]
fn a_case_only_root_change_is_the_same_root() {
    let root = DataRoot::new();
    let old = profile_root(root.path(), "old", &other_token(), OTHER_USER);
    let (_, file) = synced_at(&root, &old);
    let respelled = old.to_string_lossy().to_uppercase().replace('\\', "/");
    let result = set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": respelled}]}))
    .unwrap();
    assert_eq!(result["rejected"], serde_json::json!([]));
    assert_eq!(result["removedFiles"], 0);
    assert!(file.exists(), "a case-only change cleaned the profile");
    assert_eq!(
        profile_dir_name(&profile(PROFILE_A, Path::new(&respelled))),
        profile_dir_name(&profile(PROFILE_A, &old)),
        "a respelling named another dir"
    );
}

/// Round-2 finding 3: a profile without an installation key
/// (`key_unavailable`) or without storage (`storage_unavailable`) writes no
/// file however long the round runs, so it does not hold the full budget.
#[test]
fn no_key_or_no_storage_does_not_hold_the_full_budget() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let other = profile_root(tmp.path(), "other", &other_token(), OTHER_USER);
    let no_storage = Config {
        profiles_dir: None,
        ..multi(tmp.path(), true, vec![profile(PROFILE_A, &other)])
    };
    let registry = RwLock::new(no_storage);
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])); 6]);
    let no_key_for_other = |user: &str| (user == USER).then(|| stem(user)).flatten();
    let round = || {
        let source = || registry.read().unwrap().clone();
        let target = Target {
            file_stem: &no_key_for_other,
            ..target(&default_db, &url)
        };
        let status = sync_with(false, &target, &source, Some(limits())).0;
        (states(&status), ROUND_BUDGET.with(std::cell::Cell::get).unwrap())
    };
    let (states_seen, budget) = round();
    assert_eq!(states_seen, ["ok", "error:storage_unavailable"]);
    assert_eq!(budget, FULL_BUDGET, "the default had no file yet");
    assert_eq!(round().1, BACKGROUND_BUDGET, "storage_unavailable held the full budget");

    // Storage back, same id and root: the same slot for the budget (round 3
    // keys it by id and folded root), so no full budget for this round.
    *registry.write().unwrap() = multi(tmp.path(), true, vec![profile(PROFILE_A, &other)]);
    let (states_seen, budget) = round();
    assert_eq!(states_seen, ["ok", "error:key_unavailable"]);
    assert_eq!(budget, BACKGROUND_BUDGET);
    assert_eq!(round().1, BACKGROUND_BUDGET, "key_unavailable held the full budget");
}

/// Round-2 finding 6: `changed` counts usage files only. An identical walk
/// whose dir also held an orphan temp removes the temp and is not a change.
#[test]
fn an_orphan_temp_alone_is_not_a_change() {
    let _guard = lock();
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    let on = registry(true, &f.dir);
    assert_eq!(sync_status(&f, &url, &on)["changed"], true);
    let temp = f.dir.join(format!("{TEMP_FILE_PREFIX}orphan"));
    write_secure(&temp, b"half");
    let again = sync_status(&f, &url, &on);
    assert_eq!(states(&again), ["ok"]);
    assert!(!temp.exists(), "control: the orphan temp was not removed");
    assert_eq!(again["changed"], false, "an orphan temp's removal read as a change");
}

/// Round-2 finding 7: a duplicate's cleanup that cannot delete one of our
/// files (here one failing the storage contract) is reported by id in
/// `cleanupFailedProfiles`, as a push's cleanup is; the round still reports
/// the duplicate.
#[test]
fn a_duplicates_failed_cleanup_is_reported() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let a_dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    drop(secure::ensure_dir(&a_dir).unwrap());
    let loose = a_dir.join(format!("usage.{}.json", stem(OTHER_USER).unwrap()));
    std::fs::write(&loose, b"{}").unwrap();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["ok", "duplicate"]);
    assert!(loose.exists(), "fixture: the contract-failing file was deleted");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));
    // Control: once it is gone, nothing is reported.
    std::fs::remove_file(&loose).unwrap();
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([]));
}

// Round 3 of the /code-review findings ------------------------------------------

/// Round-3 B: a respelling of the same folder (case, `/` vs `\`) while the
/// profile walks is still the same root: the walk goes on and writes its
/// file.
#[test]
fn a_respelled_root_mid_walk_keeps_walking() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, seen) = serve(vec![delayed(json_ok(&page(1, &["a"])))]);
    let respelled = PathBuf::from(root.to_string_lossy().to_uppercase().replace('\\', "/"));
    let status = change_mid_walk(
        &seen,
        || sync_round(&tmp.path().join("absent.vscdb"), &url, &registry),
        || registry.write().unwrap().profiles[0].root = respelled.clone(),
    );
    assert_eq!(states(&status), ["notSignedIn", "ok"], "a respelling aborted the walk");
    assert!(expected_file(&profile_dir(tmp.path(), &profile(PROFILE_A, &root))).exists());
}

/// Round-3 B: the budget remembers a slot by its folded root, so a
/// respelled root that still writes no file keeps the background budget.
#[test]
fn a_respelled_root_keeps_the_background_budget() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let empty = empty_root(tmp.path(), "empty");
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &empty)]));
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])); 4]);
    let round = || {
        let source = || registry.read().unwrap().clone();
        let status = sync_with(false, &target(&default_db, &url), &source, Some(limits())).0;
        (states(&status), ROUND_BUDGET.with(std::cell::Cell::get).unwrap())
    };
    assert_eq!(round().0, ["ok", "notSignedIn"]);
    assert_eq!(round().1, BACKGROUND_BUDGET);
    let respelled = PathBuf::from(empty.to_string_lossy().to_uppercase().replace('\\', "/"));
    registry.write().unwrap().profiles[0].root = respelled;
    let (states_seen, budget) = round();
    assert_eq!(states_seen, ["ok", "notSignedIn"]);
    assert_eq!(budget, BACKGROUND_BUDGET, "a respelling reset the budget");
}

/// Round-3 D: a duplicate's cleanup that removes one usage file and then
/// cannot remove another reports both: `changed` (a file went) and
/// `cleanupFailedProfiles`.
#[test]
fn a_partly_failed_duplicate_cleanup_still_counts_as_a_change() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    assert_eq!(states(&sync_round(&default_db, &url, &registry)), ["ok", "duplicate"]);
    // Planted after the first round, so the default's walk is unchanged.
    let a_dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    let removable = a_dir.join(hex_name('e'));
    write_secure(&removable, b"{}");
    let loose = a_dir.join(hex_name('f'));
    std::fs::write(&loose, b"{}").unwrap();
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["ok", "duplicate"]);
    assert!(!removable.exists() && loose.exists(), "fixture");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));
    assert_eq!(status["changed"], true, "a removed file was not a change");
}

// Round 4 of the /code-review findings ------------------------------------------

/// Round-4 finding 2: a login whose cookie header cannot be built
/// (`token_unreadable`) is a refusal like every other unreadable token, so
/// it does not hold the full budget; nothing is sent. Reached only through a
/// test fault: `token_user` admits only characters a header value takes.
#[test]
fn an_unbuildable_cookie_is_a_refusal() {
    let f = fixture();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    BAD_COOKIE_FOR_TEST.with(|fault| fault.set(true));
    let outcome = run_with(&f.db, &f.dir, &url, limits(), &|| true);
    BAD_COOKIE_FOR_TEST.with(|fault| fault.set(false));
    assert_eq!(outcome.stop, Stop::error("token_unreadable"));
    assert!(outcome.refused, "an unbuildable cookie was not a refusal");
    assert!(seen.lock().unwrap().is_empty());
    // Control: without the fault the same login is sent.
    assert_eq!(run_with(&f.db, &f.dir, &url, limits(), &|| true), ok(1));
}

/// Round-4 finding 7: a refused push keeps the profiles it lists, compared
/// by folded root like everywhere else, so a respelled root in the refused
/// push keeps the profile.
#[test]
fn a_refused_push_with_a_respelled_root_keeps_the_profile() {
    let root = DataRoot::new();
    let folder = root.path().join("Folder");
    set(serde_json::json!({"enabled": true, "profiles": [{"id": PROFILE_A, "root": folder}]}))
        .unwrap();
    let respelled = folder.to_string_lossy().to_uppercase().replace('\\', "/");
    let no_roaming = Bases {
        roaming: None,
        local: Some(root.path().join(TEST_LOCAL_DIR)),
    };
    let refused = set_with_bases(
        &serde_json::json!({"enabled": true,
            "profiles": [{"id": PROFILE_A, "root": respelled}]})
        .to_string(),
        &no_roaming,
    );
    assert_eq!(refused, Err("storageUnavailable".to_string()));
    let ids: Vec<String> = config().profiles.iter().map(|p| p.id.clone()).collect();
    assert_eq!(ids, [PROFILE_A], "a respelled root dropped the profile");
    // Control: a refused push with another root, refused for the storage
    // alone (its root is valid), does drop it.
    let other = root.path().join("Other").to_string_lossy().into_owned();
    let control = serde_json::json!([{"id": PROFILE_A, "root": other}]);
    let (parsed, rejected) = parse_profiles(control.clone(), None).unwrap();
    assert!(rejected.is_empty(), "fixture: the control root was rejected: {rejected:?}");
    assert_eq!(parsed.len(), 1);
    let refused = set_with_bases(
        &serde_json::json!({"enabled": true, "profiles": control}).to_string(),
        &no_roaming,
    );
    assert_eq!(refused, Err("storageUnavailable".to_string()));
    assert!(config().profiles.is_empty());
    drop(root);
}

// Round 5 of the /code-review findings ------------------------------------------

/// Round-5 finding 4: whether a duplicate's dir keeps its current account's
/// file is decided from the listing, not by stat'ing the name. Here the kept
/// name is a dangling symlink (listed, but `exists()` is false): the dir still
/// keeps its lock and no rmdir is attempted.
#[test]
fn a_kept_file_that_cannot_be_stated_still_keeps_the_dir() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_db = login_db(&tmp.path().join("default"), &valid_token(), USER);
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let a_dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    drop(secure::ensure_dir(&a_dir).unwrap());
    let kept = expected_file(&a_dir);
    std::os::windows::fs::symlink_file(tmp.path().join("nowhere.json"), &kept)
        .expect("fixture: file symlink (needs elevation or developer mode)");
    assert!(!kept.exists() && std::fs::symlink_metadata(&kept).is_ok(), "fixture");
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["ok", "duplicate"]);
    assert!(a_dir.join(LOCK_FILE_NAME).exists(), "the kept dir's lock was deleted");
}

/// Round-5 finding 5 / delta 1: a profile dir that fails the storage
/// contract and holds only the leftover `.cursor-sync.root` is no usage-file
/// deletion failure (neither an on push nor turning sync off reports
/// cleanupFailed), and nothing in it is deleted.
#[test]
fn a_leftover_root_record_alone_is_never_a_cleanup_failure() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": r"C:\x\a"}]}))
    .unwrap();
    let loose = config().profiles_dir.unwrap().join(PROFILE_B);
    std::fs::create_dir(&loose).unwrap();
    // The record as review round 2's build wrote it (a file meeting the
    // storage contract, so a secure delete would succeed), in a dir that
    // fails the contract (loosened since).
    // (`write_secure` cannot be used: it secures the parent dir first.)
    let mut record = secure::create_new(&loose.join(LEGACY_ROOT_FILE_NAME))
        .expect("fixture: secure record");
    record.write_all("5f3c".repeat(16).as_bytes()).unwrap(); // a 64-hex digest
    drop(record);
    assert!(secure::ensure_dir(&loose).is_err(), "fixture: the dir meets the contract");
    let on = set(serde_json::json!({"enabled": true,
        "profiles": [{"id": PROFILE_A, "root": r"C:\x\a"}]}))
    .unwrap();
    assert_eq!(on["cleanupFailed"], false, "{on}");
    assert_eq!(on["removedFiles"], 0);
    assert!(set(serde_json::json!({"enabled": false})).is_ok(), "disable reported cleanupFailed");
    // Report only: nothing is deleted from a dir failing the contract.
    assert!(
        loose.join(LEGACY_ROOT_FILE_NAME).exists(),
        "a file was deleted from a contract-failing dir"
    );
    // Control: a usage file in the same loose dir is a failure.
    std::fs::write(loose.join(hex_name('b')), b"{}").unwrap();
    assert_eq!(
        set(serde_json::json!({"enabled": false})),
        Err("cleanupFailed".to_string())
    );
    drop(root);
}

// Merge-delta review ------------------------------------------------------------

/// Merge-delta 8: roots that differ only by a trailing `\` are the same
/// root, whichever side carries it: Cursor's own folder is refused, an
/// already listed folder is a duplicate, and `same_root` agrees.
#[test]
fn a_trailing_separator_is_the_same_root() {
    let own = Path::new(r"C:\Users\me\AppData\Roaming\Cursor");
    let own_slash = Path::new(r"C:\Users\me\AppData\Roaming\Cursor\");
    assert_eq!(root_rule(r"C:\Users\me\AppData\Roaming\Cursor\", Some(own)), Err("cursorDefault"));
    assert_eq!(root_rule(r"C:\Users\me\AppData\Roaming\Cursor", Some(own_slash)), Err("cursorDefault"));
    assert!(root_rule(r"C:\Users\me\AppData\Roaming\Cursor2", Some(own_slash)).is_ok(), "control");
    assert!(same_root(Path::new(r"C:\x\a\"), Path::new(r"C:\x\a")));
    assert!(same_root_str(r"C:\x\a", r"c:/x/a/"));
    assert!(!same_root_str(r"C:\x\a", r"C:\x\ab"), "control");
    let fixed = |_: &Path| cursor_desktop::DRIVE_FIXED;
    for (root, existing) in [(r"C:\x\a\", r"C:\x\a"), (r"C:\x\a", r"C:\x\a\")] {
        let raw = serde_json::json!({"root": root, "existing": [existing]}).to_string();
        assert_eq!(validate_with(&raw, &fixed, None).unwrap()["reason"], "duplicate", "{root}");
    }
}

// CodeRabbit review on PR #268 ---------------------------------------------------

/// CodeRabbit 1: `changed` follows the content comparison, not whether the
/// identical file's modified time could be set. With that set failing (test
/// fault), the same bytes are rewritten so the time still moves
/// (`lastSuccessMs` dates the walk), and the round is still not a change.
#[test]
fn an_identical_walk_whose_time_cannot_be_set_is_still_no_change() {
    let _guard = lock();
    let f = fixture();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    let on = registry(true, &f.dir);
    let first = sync_status(&f, &url, &on);
    assert_eq!(first["changed"], true);
    let bytes = std::fs::read(expected_file(&f.dir)).unwrap();
    std::thread::sleep(Duration::from_millis(50));
    TOUCH_FAILS_FOR_TEST.with(|fault| fault.set(true));
    let again = sync_status(&f, &url, &on);
    TOUCH_FAILS_FOR_TEST.with(|fault| fault.set(false));
    assert_eq!(states(&again), ["ok"]);
    assert_eq!(again["changed"], false, "a failed time update read as a change");
    assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), bytes);
    assert!(
        again["lastSuccessMs"].as_u64() > first["lastSuccessMs"].as_u64(),
        "the rewrite did not move the time: {again} {first}"
    );
}

/// CodeRabbit 2: a commit that cannot delete a superseded usage file (here
/// one failing the storage contract, an old account's) reports its profile
/// in `cleanupFailedProfiles`; the file would otherwise stay registered
/// unnoticed. Control: none reported once it is gone.
#[test]
fn a_commit_that_cannot_delete_a_superseded_file_reports_it() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let a_dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    drop(secure::ensure_dir(&a_dir).unwrap());
    let loose = a_dir.join(format!("usage.{}.json", stem(OTHER_USER).unwrap()));
    std::fs::write(&loose, b"{}").unwrap();
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    assert!(expected_file(&a_dir).exists() && loose.exists(), "fixture");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));
    std::fs::remove_file(&loose).unwrap();
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([]));
}

// Local review of the PR #268 commit cleanup -------------------------------------

/// Commit cleanup 1: a listing error during the commit's cleanup is a
/// cleanup failure (the profile is in `cleanupFailedProfiles`), and a
/// superseded file removed before the error still counts toward `changed`.
/// The walk's bytes are identical, so only that removal can make it a change.
#[test]
fn a_listing_error_during_the_commit_cleanup_is_reported_and_removals_still_count() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let a_dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    let (url, _) = serve(vec![json_ok(&page(1, &["a"])), json_ok(&page(1, &["a"]))]);
    let first = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&first), ["notSignedIn", "ok"]);
    let old = a_dir.join(format!("usage.{}.json", stem(OTHER_USER).unwrap()));
    write_secure(&old, b"{}");
    LISTING_ERROR_FOR_TEST.with(|fault| fault.set(true));
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    LISTING_ERROR_FOR_TEST.with(|fault| fault.set(false));
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    assert!(!old.exists(), "control: the superseded file was not removed");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));
    assert_eq!(status["changed"], true, "a removal before the listing error was not counted");
}

/// Commit cleanup 2: a commit cleans every candidate dir of the profile, not
/// only the one it resolved. With the sticky `.secure` fallback in use the
/// commit goes there; an older account's file in the plain dir is removed
/// too (and that emptied dir goes).
#[test]
fn a_commit_cleans_the_profiles_other_candidate_dir() {
    let _guard = lock();
    let tmp = TempDir::new();
    let root = profile_root(tmp.path(), "b", &valid_token(), USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let plain = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    let mut sibling = plain.clone().into_os_string();
    sibling.push(secure::FALLBACK_SUFFIX);
    let sibling = PathBuf::from(sibling);
    drop(secure::ensure_dir(&sibling).unwrap());
    let old = plain.join(format!("usage.{}.json", stem(OTHER_USER).unwrap()));
    write_secure(&old, b"{}");
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&tmp.path().join("absent.vscdb"), &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"]);
    assert!(expected_file(&sibling).exists(), "fixture: the commit did not use the fallback");
    assert!(!old.exists(), "the other candidate's old-account file stayed");
    assert!(!plain.exists(), "the emptied other candidate was left");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([]));
    let registered = newest_per_account(&registry.read().unwrap());
    assert_eq!(registered, [expected_file(&sibling)]);
}

/// Plan S3a: `token_user` keeps its accept/reject cases and reasons now that
/// the token check is the shared `cursor_desktop::jwt_claims` (Grok Bot's
/// fallback uses the same check). The charset, segment-count and
/// empty-segment cases each fail one part of that check while passing the
/// others; the non-JSON and non-object payloads fail only the decode; the
/// sealed token fails both the charset and the segment count; the `sub` and
/// stored-id cases pass the shared check and are refused after it. The valid
/// token is the control.
#[test]
fn token_user_accepts_and_rejects_as_before_through_the_shared_check() {
    let login = |token: String, glass: Option<&str>| CursorLogin {
        access_token: token,
        glass_user_id: glass.map(str::to_string),
        profile_user_id: None,
    };
    let user = |token: String| token_user(&login(token, None)).map(|(user, _)| user);
    let token = valid_token();
    let parts: Vec<&str> = token.split('.').collect();
    let encode = |v: &str| base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(v);
    let unreadable = Err(Stop::error("token_unreadable"));

    // Control: the session JWT is accepted, with the stored id matching too.
    assert_eq!(user(token.clone()), Ok(USER.to_string()));
    assert_eq!(
        token_user(&login(token.clone(), Some(USER))).map(|(user, _)| user),
        Ok(USER.to_string())
    );
    // Charset only: a decodable three-segment JWT with `=` in its signature.
    assert_eq!(user(format!("{token}=")), unreadable, "charset");
    // Segment count only: a fourth segment (the payload still decodes).
    assert_eq!(user(format!("{token}.extra")), unreadable, "four segments");
    // Empty segment only: no signature (the payload still decodes).
    assert_eq!(
        user(format!("{}.{}.", parts[0], parts[1])),
        unreadable,
        "empty segment"
    );
    // Payload that is not JSON.
    assert_eq!(
        user(format!("{}.{}.{SIGNATURE}", parts[0], encode("not json"))),
        unreadable,
        "payload"
    );
    // A JSON payload that is not an object: refused as before (it has no
    // `sub`), now already by the shared check.
    for value in ["1", "[]"] {
        assert_eq!(
            user(format!("{}.{}.{SIGNATURE}", parts[0], encode(value))),
            unreadable,
            "{value}"
        );
    }
    // A sealed token is refused as unreadable (S3b adds the sealed path).
    let sealed = format!(
        "cursor-safe-storage:v1:{}",
        base64::engine::general_purpose::STANDARD.encode(b"v10 sealed blob")
    );
    assert_eq!(user(sealed), unreadable, "sealed");
    // A `sub` with no user part, and a stored id that differs.
    assert_eq!(
        user(jwt("auth0|", Some(now_secs() + 3600.0))),
        unreadable,
        "sub"
    );
    assert_eq!(
        token_user(&login(token, Some(OTHER_USER))).map(|(user, _)| user),
        Err(Stop::error("account_mismatch"))
    );
}

// --- sealed Cursor sign-in (S3b) ------------------------------------------
//
// The fixture (`cursor_desktop::seal_fixture`, shared with agent_grokbot's
// test) was generated independently of the crate by
// `gen_cursor_seal_fixture.py` (Python `cryptography`, AES-256-GCM, no AAD)
// on the Mac, which has no DPAPI. The key is fed straight into the loader
// seam (`key_from_raw`, no DPAPI, no Local State); the real CNG `AesGcm`
// still decrypts, so this whole module is Windows only.
use crate::cursor_desktop::seal_fixture as seal;

/// A key loader that records every `Local State` path it is asked for and
/// counts its calls, returning the fixture key straight from raw bytes.
struct LoaderSpy {
    calls: std::sync::Arc<AtomicU64>,
    paths: std::sync::Arc<Mutex<Vec<PathBuf>>>,
}

impl LoaderSpy {
    fn new() -> Self {
        Self {
            calls: std::sync::Arc::new(AtomicU64::new(0)),
            paths: std::sync::Arc::new(Mutex::new(Vec::new())),
        }
    }

    fn loader(&self) -> impl Fn(&Path) -> Result<Box<dyn SealKey>, ()> + '_ {
        move |local_state| {
            self.calls.fetch_add(1, Ordering::SeqCst);
            self.paths.lock().unwrap().push(local_state.to_path_buf());
            Ok(Box::new(crate::win_safe_storage::test_support::key_from_raw(seal::SEAL_KEY))
                as Box<dyn SealKey>)
        }
    }

    fn calls(&self) -> u64 {
        self.calls.load(Ordering::SeqCst)
    }
}

/// A default-profile `state.vscdb` at the real depth
/// (`<base>\Cursor\User\globalStorage\state.vscdb`), so `local_state_path`
/// resolves to `<base>\Cursor\Local State`.
fn nested_signed_in_db(base: &Path, token: &str) -> PathBuf {
    let gs = base.join("Cursor").join("User").join("globalStorage");
    std::fs::create_dir_all(&gs).unwrap();
    signed_in_db(&gs, token)
}

fn sealed_cookie_from(seen: &Arc<Mutex<Vec<Seen>>>) -> Option<String> {
    let seen = seen.lock().unwrap();
    seen.first().and_then(|r| header(&r.head, "cookie"))
}

/// Run one default-profile sync with an injected consent closure and key
/// loader (the sealed-token seam).
fn run_sealed(
    db: &Path,
    dir: &Path,
    url: &str,
    consent: &dyn Fn() -> bool,
    loader: &LoadSealKey<'_>,
    root: Option<PathBuf>,
) -> Outcome {
    let slot = Slot {
        id: DEFAULT_PROFILE_ID.to_string(),
        root,
        dir: Some(dir.to_path_buf()),
    };
    let target = Target {
        db_path: db,
        url,
        billing_url: url,
        https_only: false,
        file_stem: &stem,
        drive_type: &cursor_desktop::drive_type,
        consent,
        load_seal_key: loader,
    };
    crate::RUNTIME.block_on(run(&target, &slot, dir, limits(), &|| true, &mut |_, _| None))
}

/// Acceptance 1: sealed + consent decrypts to the SAME cookie a plaintext twin
/// of the JWT would send.
/// Mutation: cursor_sync.rs `open_sealed` `Ok(token) => login.access_token = token`
/// -> `Ok(_token) => {}` (drop the decrypted token, keep the sealed one) makes
/// `token_user` see `cursor-safe-storage:v1:…` and refuse `token_unreadable`.
#[test]
fn a_sealed_token_decrypts_to_the_same_cookie_as_its_plaintext_twin() {
    let tmp = TempDir::new();
    let base = tmp.path();
    let db = nested_signed_in_db(base, &seal::sealed_token(seal::SEALED_V10_B64));
    let dir = base.join("sync");
    let spy = LoaderSpy::new();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let outcome = run_sealed(&db, &dir, &url, &|| true, &spy.loader(), None);
    assert_eq!(outcome.stop.state, State::Ok, "{:?}", outcome.stop.reason);
    assert_eq!(spy.calls(), 1, "the key is loaded once");
    let sealed_cookie = sealed_cookie_from(&seen);
    assert_eq!(
        sealed_cookie.as_deref(),
        Some(format!("WorkosCursorSessionToken={USER}%3A%3A{}", seal::SEAL_JWT).as_str()),
        "the sealed path sent the decrypted JWT, keyed by its own sub"
    );

    // The plaintext twin: the same JWT stored unsealed sends the same cookie.
    let twin = TempDir::new();
    let twin_db = signed_in_db(twin.path(), seal::SEAL_JWT);
    let twin_dir = twin.path().join("sync");
    let (twin_url, twin_seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let twin_outcome = run_with(&twin_db, &twin_dir, &twin_url, limits(), &|| true);
    assert_eq!(twin_outcome.stop.state, State::Ok);
    assert_eq!(sealed_cookie, sealed_cookie_from(&twin_seen));
}

/// Acceptance 1: a plaintext token syncs and the key source is never touched
/// even when consent is denied (Design 1: plaintext never consults consent).
/// Mutation: cursor_sync.rs `run` `StoredToken::Plain => {}`
/// -> `StoredToken::Plain => { if !(target.consent)() { return Outcome::refused(Stop::error("keychain_consent")); } }`
/// makes this plaintext sync refuse.
#[test]
fn a_plaintext_token_syncs_without_consent_or_a_key_load() {
    let f = fixture(); // a plaintext JWT
    let (url, _seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    // consent denied and a loader that panics if reached.
    let outcome = run_sealed(&f.db, &f.dir, &url, &|| false, &unexpected_seal_key, None);
    assert_eq!(outcome.stop.state, State::Ok, "{:?}", outcome.stop.reason);
}

/// Acceptance 1: a sealed token with consent denied refuses `keychain_consent`
/// and never loads the key (the gate precedes the loader, C2/C1).
/// Mutation: `open_sealed` move the consent check to after the key load
/// (`let key = …; if !(target.consent)() { … }`) makes the loader run once.
#[test]
fn a_sealed_token_without_consent_refuses_and_loads_no_key() {
    let tmp = TempDir::new();
    let base = tmp.path();
    let db = nested_signed_in_db(base, &seal::sealed_token(seal::SEALED_V10_B64));
    let dir = base.join("sync");
    let spy = LoaderSpy::new();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let outcome = run_sealed(&db, &dir, &url, &|| false, &spy.loader(), None);
    assert!(outcome.refused);
    assert_eq!(outcome.stop.reason, Some("keychain_consent"));
    assert_eq!(spy.calls(), 0, "Local State is not opened without consent");
    assert!(seen.lock().unwrap().is_empty(), "nothing was sent");
}

/// Acceptance 1: every malformed sealed token yields its fixed reason. v11/v20
/// are unsupported; a wrong key and a truncated blob are unreadable; bad
/// base64 and a v2 envelope are refused at classification. (The no-leak check
/// is `sealed_rounds_leak_no_fixture_into_status_or_reasons`.)
/// Mutation: `open_sealed` drop the version gate
/// (`if !blob.starts_with(b"v10") { … }` removed) makes v11/v20 reach the
/// decrypt and report `token_sealed_unreadable`, not `token_sealed_unsupported`.
#[test]
fn malformed_sealed_tokens_map_to_fixed_reasons_without_leaking() {
    let run_case = |token: &str, consent: &dyn Fn() -> bool, loader: &LoadSealKey<'_>| {
        let tmp = TempDir::new();
        let db = nested_signed_in_db(tmp.path(), token);
        let dir = tmp.path().join("sync");
        let (url, _seen) = serve(vec![json_ok(&page(1, &["a"]))]);
        let outcome = run_sealed(&db, &dir, &url, consent, loader, None);
        (outcome.stop.state, outcome.stop.reason)
    };

    // v11 / v20: unsupported. The version gate precedes the consent check
    // and the key load, so the spy must see no load.
    for payload in [seal::SEALED_V11_B64, seal::SEALED_V20_B64] {
        let spy = LoaderSpy::new();
        let (state, reason) =
            run_case(&seal::sealed_token(payload), &|| true, &spy.loader());
        assert_eq!(state, State::Error);
        assert_eq!(reason, Some("token_sealed_unsupported"), "payload {payload}");
        assert_eq!(spy.calls(), 0, "an unsupported version loads no key");
    }

    // A wrong key: the fixture decrypts only with SEAL_KEY; a different key
    // fails AES-GCM -> unreadable.
    let wrong = |_: &Path| -> Result<Box<dyn SealKey>, ()> {
        Ok(Box::new(crate::win_safe_storage::test_support::key_from_raw([0x00; 32])))
    };
    let (_, reason) = run_case(&seal::sealed_token(seal::SEALED_V10_B64), &|| true, &wrong);
    assert_eq!(reason, Some("token_sealed_unreadable"), "wrong key");

    // A truncated blob: v10 passes the gate, the tag is short -> unreadable.
    let spy = LoaderSpy::new();
    let (_, reason) =
        run_case(&seal::sealed_token(seal::SEALED_TRUNCATED_B64), &|| true, &spy.loader());
    assert_eq!(reason, Some("token_sealed_unreadable"), "truncated");

    // Bad base64 and a non-v1 envelope: refused at classification, no key,
    // no consent needed.
    for (token, code) in [
        ("cursor-safe-storage:v1:!!!!", "token_sealed_unreadable"),
        ("cursor-safe-storage:v2:AAAA", "token_sealed_unsupported"),
    ] {
        let (_, reason) = run_case(token, &|| true, &unexpected_seal_key);
        assert_eq!(reason, Some(code), "{token}");
    }
}

/// Every form of the fixture secret a leak could take: the plaintext JWT and
/// each of its segments, base64 of it, the sealed payload, and the key bytes
/// (hex, base64, and Rust's `{:?}` array form).
fn fixture_secret_forms() -> Vec<String> {
    use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
    let mut forms = vec![
        seal::SEAL_JWT.to_string(),
        STANDARD.encode(seal::SEAL_JWT),
        URL_SAFE_NO_PAD.encode(seal::SEAL_JWT),
        seal::SEALED_V10_B64.to_string(),
        seal::SEALED_V10_B64[..24].to_string(),
        seal::SEAL_KEY.iter().map(|b| format!("{b:02x}")).collect::<String>(),
        seal::SEAL_KEY.iter().map(|b| format!("{b:02X}")).collect::<String>(),
        STANDARD.encode(seal::SEAL_KEY),
        format!("{:?}", &seal::SEAL_KEY[..4]).trim_end_matches(']').to_string(),
    ];
    forms.extend(seal::SEAL_JWT.split('.').map(str::to_string));
    forms
}

/// Acceptance 1 ("no fixture fragment in any reason, status JSON or log"):
/// full sync rounds through `sync_with` — the success case and every failure
/// case — produce a status JSON (headline and per-profile reasons) that holds
/// none of the fixture's secret forms. `cursor_sync` writes no log lines, so
/// the status JSON and the reasons are its whole output channel.
/// Mutation: cursor_sync.rs `run`
/// `Ok(token) => login.access_token = token,`
/// -> `Ok(token) => return Outcome::refused(Stop::error(Box::leak(token.into_boxed_str()))),`
/// puts the plaintext into the status `reason`.
#[test]
fn sealed_rounds_leak_no_fixture_into_status_or_reasons() {
    let _guard = lock();
    let good = |_: &Path| -> Result<Box<dyn SealKey>, ()> {
        Ok(Box::new(crate::win_safe_storage::test_support::key_from_raw(seal::SEAL_KEY)))
    };
    let wrong = |_: &Path| -> Result<Box<dyn SealKey>, ()> {
        Ok(Box::new(crate::win_safe_storage::test_support::key_from_raw([0x00; 32])))
    };
    let cases: [(&str, String, &LoadSealKey<'_>, &str); 5] = [
        ("success", seal::sealed_token(seal::SEALED_V10_B64), &good, "ok"),
        ("wrong key", seal::sealed_token(seal::SEALED_V10_B64), &wrong, "error"),
        ("truncated", seal::sealed_token(seal::SEALED_TRUNCATED_B64), &good, "error"),
        ("bad base64", "cursor-safe-storage:v1:!!!!".to_string(), &good, "error"),
        ("v2 prefix", "cursor-safe-storage:v2:AAAA".to_string(), &good, "error"),
    ];
    let forms = fixture_secret_forms();
    for (name, token, loader, state) in cases {
        let tmp = TempDir::new();
        let db = nested_signed_in_db(tmp.path(), &token);
        let dir = tmp.path().join("sync");
        let (url, _seen) = serve(vec![json_ok(&page(1, &["a"]))]);
        let target = Target {
            consent: &allow_consent,
            load_seal_key: loader,
            ..target(&db, &url)
        };
        let registry = registry(true, &dir);
        let source = || registry.read().unwrap().clone();
        let status = sync_with(true, &target, &source, Some(limits())).0;
        assert_eq!(status["state"], state, "{name}: {status}");
        let text = status.to_string();
        for form in &forms {
            assert!(!text.contains(form.as_str()), "{name}: status carries {form:?}");
        }
    }
}

/// C6 for the default profile: its key comes from `<user-data-dir>\Local
/// State`, the `…\Cursor` three levels above `User\globalStorage\state.vscdb`.
/// Mutation: cursor_sync.rs `local_state_path`
/// `            .and_then(Path::parent)\n            .and_then(Path::parent)?`
/// -> `            .and_then(Path::parent)?` (two parents: `…\Cursor\User`).
#[test]
fn the_default_profile_reads_the_user_data_dir_local_state() {
    let tmp = TempDir::new();
    let base = tmp.path();
    let db = nested_signed_in_db(base, &seal::sealed_token(seal::SEALED_V10_B64));
    let dir = base.join("sync");
    let spy = LoaderSpy::new();
    let (url, _seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let outcome = run_sealed(&db, &dir, &url, &|| true, &spy.loader(), None);
    assert_eq!(outcome.stop.state, State::Ok, "{:?}", outcome.stop.reason);
    assert_eq!(
        *spy.paths.lock().unwrap(),
        vec![base.join("Cursor").join("Local State")]
    );
}

/// C8: once unsealed, the token String is wiped on a failure path after
/// `open_sealed` (here `account_mismatch`: the stored glass id names another
/// user). Measured through the test-only `cursor_desktop::WIPED` record: some
/// buffer that held exactly the unsealed JWT immediately before `wipe_str`
/// was all zero immediately after. It does not measure memory after the
/// String is freed, nor other copies (the rows read from SQLite are covered
/// by `query_login_wipes_the_token_row_string`).
/// Mutation: cursor_sync.rs `impl Drop for WipedLogin`
/// `        cursor_desktop::wipe_str(&mut self.0.access_token);` -> `        let _ = &self.0.access_token;`
#[test]
fn an_unsealed_token_is_wiped_on_a_failure_after_open() {
    let tmp = TempDir::new();
    let gs = tmp.path().join("Cursor").join("User").join("globalStorage");
    std::fs::create_dir_all(&gs).unwrap();
    let sealed = seal::sealed_token(seal::SEALED_V10_B64);
    let db = state_db(
        &gs,
        &[
            ("cursorAuth/accessToken", &format!("\"{sealed}\"")),
            ("glass.lastSignedInAuthId", &format!("glass-{OTHER_USER}")),
        ],
    );
    let dir = tmp.path().join("sync");
    let spy = LoaderSpy::new();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    cursor_desktop::WIPED.with(|wiped| wiped.borrow_mut().clear());
    let outcome = run_sealed(&db, &dir, &url, &|| true, &spy.loader(), None);
    assert_eq!(outcome.stop.reason, Some("account_mismatch"));
    assert_eq!(spy.calls(), 1, "the token was unsealed before the refusal");
    assert!(seen.lock().unwrap().is_empty());
    let wiped = cursor_desktop::WIPED.with(|wiped| wiped.borrow().clone());
    assert!(
        wiped.contains(&(seal::SEAL_JWT.to_string(), true)),
        "no buffer holding the unsealed token was zeroed ({} wipes)",
        wiped.len()
    );
}

/// C8, the cookie value: on a successful round the String that held
/// `WorkosCursorSessionToken=<user>%3A%3A<token>` is zeroed when dropped.
/// Mutation: cursor_sync.rs `impl Drop for WipedString`
/// `        cursor_desktop::wipe_str(&mut self.0);` -> `        let _ = &self.0;`
#[test]
fn the_cookie_value_is_wiped_after_the_header_is_built() {
    let tmp = TempDir::new();
    let db = nested_signed_in_db(tmp.path(), &seal::sealed_token(seal::SEALED_V10_B64));
    let dir = tmp.path().join("sync");
    let spy = LoaderSpy::new();
    let (url, _seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    cursor_desktop::WIPED.with(|wiped| wiped.borrow_mut().clear());
    let outcome = run_sealed(&db, &dir, &url, &|| true, &spy.loader(), None);
    assert_eq!(outcome.stop.state, State::Ok, "{:?}", outcome.stop.reason);
    let cookie = format!("WorkosCursorSessionToken={USER}%3A%3A{}", seal::SEAL_JWT);
    let wiped = cursor_desktop::WIPED.with(|wiped| wiped.borrow().clone());
    assert!(wiped.contains(&(cookie, true)), "the cookie value was not zeroed");
    assert!(wiped.contains(&(seal::SEAL_JWT.to_string(), true)), "the token was not zeroed");
}

/// T10 / item 6: a decoded blob too short to be v10 (`"v10"` || nonce(12) ||
/// tag(16) = 31 bytes) is `token_sealed_unreadable`, decided before consent,
/// `Local State` or DPAPI: consent here is denied (a later check would say
/// `keychain_consent`) and the loader is never called. Covers 0..=30 bytes,
/// v10-prefixed from 3 bytes on; 31 bytes passes this gate (and then fails
/// AES-GCM, `token_sealed_unreadable` from the decrypt, loader called once).
/// Mutation: cursor_sync.rs `open_sealed`
/// `    if blob.len() < V10_MIN_LEN {\n        return Err(refuse("token_sealed_unreadable"));\n    }` -> *(delete)*
/// -> short v10 blobs reach the consent check (`keychain_consent`).
#[test]
fn blobs_shorter_than_v10_are_unreadable_before_consent() {
    use base64::engine::general_purpose::STANDARD;
    let (url, seen) = serve(vec![]);
    for len in 0..=30usize {
        let mut blob = b"v10".to_vec();
        blob.resize(len.max(3), 0x41);
        blob.truncate(len);
        let tmp = TempDir::new();
        let db = nested_signed_in_db(tmp.path(), &seal::sealed_token(&STANDARD.encode(&blob)));
        let dir = tmp.path().join("sync");
        let spy = LoaderSpy::new();
        let outcome = run_sealed(&db, &dir, &url, &|| false, &spy.loader(), None);
        assert_eq!(outcome.stop.reason, Some("token_sealed_unreadable"), "len {len}");
        assert_eq!(spy.calls(), 0, "len {len}");
    }
    // Boundary: 31 bytes passes the length gate (consent granted here, so
    // the loader runs, and the all-'A' tag fails AES-GCM).
    let mut blob = b"v10".to_vec();
    blob.resize(31, 0x41);
    let tmp = TempDir::new();
    let db = nested_signed_in_db(tmp.path(), &seal::sealed_token(&STANDARD.encode(&blob)));
    let spy = LoaderSpy::new();
    let outcome = run_sealed(&db, &tmp.path().join("sync"), &url, &|| true, &spy.loader(), None);
    assert_eq!(outcome.stop.reason, Some("token_sealed_unreadable"));
    assert_eq!(spy.calls(), 1);
    assert!(seen.lock().unwrap().is_empty());
}

/// Acceptance 1 (C2): consent withdrawn between the login read and the key
/// read -> `keychain_consent`, 0 loader calls. The after-login hook flips the
/// answer to "no" after the DB is read; correct code reads consent only inside
/// `open_sealed` (after the hook), so it sees the withdrawal.
/// Mutation: `open_sealed` read consent into a `bool` at the top of `run`
/// (before `read_login`) and pass it in — the snapshot is taken before the
/// hook flips it, so the loader runs once and the sync is not refused.
#[test]
fn consent_withdrawn_after_the_login_read_refuses_and_loads_no_key() {
    let tmp = TempDir::new();
    let base = tmp.path();
    let db = nested_signed_in_db(base, &seal::sealed_token(seal::SEALED_V10_B64));
    let dir = base.join("sync");
    let spy = LoaderSpy::new();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let withdrawn = std::cell::Cell::new(false);
    let consent = || !withdrawn.get();
    let flip = || withdrawn.set(true);
    let outcome = with_after_login(&flip, || {
        run_sealed(&db, &dir, &url, &consent, &spy.loader(), None)
    });
    assert!(outcome.refused);
    assert_eq!(outcome.stop.reason, Some("keychain_consent"));
    assert_eq!(spy.calls(), 0, "a round-start snapshot would have loaded the key");
    assert!(seen.lock().unwrap().is_empty());
}

/// Acceptance 1 (C6): an added profile's sealed token is opened with the key
/// from THAT profile's user-data-dir `Local State`, not the default's.
/// Mutation: cursor_sync.rs `local_state_path` `Some(root) => root.clone()`
/// -> `Some(_root) => target.db_path...` (ignore the profile root) makes the
/// loader see the default db's Local State path.
#[test]
fn an_added_profile_reads_its_own_user_data_dir_local_state() {
    let tmp = TempDir::new();
    // The added profile's root on a fixed drive (the system temp dir is one).
    let root = tmp.path().join("profileA");
    let gs = cursor_desktop::profile_db_path(&root);
    std::fs::create_dir_all(gs.parent().unwrap()).unwrap();
    let _db = signed_in_db(gs.parent().unwrap(), &seal::sealed_token(seal::SEALED_V10_B64));
    let dir = tmp.path().join("sync");
    let spy = LoaderSpy::new();
    let (url, _seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    // db_path is the default one (unused for an added profile); the root drives
    // the Local State path.
    let default_db = tmp.path().join("Cursor").join("User").join("globalStorage").join("state.vscdb");
    let outcome = run_sealed(&default_db, &dir, &url, &|| true, &spy.loader(), Some(root.clone()));
    assert_eq!(outcome.stop.state, State::Ok, "{:?}", outcome.stop.reason);
    assert_eq!(spy.calls(), 1);
    let asked = spy.paths.lock().unwrap().clone();
    assert_eq!(asked, vec![root.join("Local State")], "the profile's own Local State");
}

// --- Billing V2 (plan CURSOR-BV2 r4, Acceptance 1; port of macOS #554, #559) ---
//
// Fixtures: the discovery's §1 third-party shapes (t3code #28511's redacted
// item, `{}`, `{"data":[]}`, cursor pagination) and G1's observed
// `{"availability":"missing_team_id"}`. Months are relative to the real UTC
// now, so the windows the walk asks for are the ones these replies answer.
// Windows adaptations: files a test plants where Syrtis reads them are
// written the secure way (`write_secure`), since the sidecar and the live
// file are read only through the storage contract; disable and profile
// removal go through `set` under `DataRoot`.

const EMAIL_CANARY: &str = "EMAILCANARY@example.invalid";
const ITEM_ID_CANARY: &str = "BILLINGITEMIDCANARY";
const HOUR: i64 = 3_600_000;
/// The four meters of `item`: 1263 + 46400 + 0 + 312.
const ITEM_TOKENS: f64 = 47975.0;
/// `event`'s four token counts: 10 + 5 + 2 + 9.
const OLD_ROW_TOKENS: f64 = 26.0;

fn now_ms_i64() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}

/// Start (Unix ms) of the UTC month `back` months before the current one.
fn month_start_ms(back: i32) -> i64 {
    use chrono::TimeZone as _;
    let month = cursor_billing_v2::month_index(now_ms_i64()).unwrap() - back;
    chrono::Utc
        .with_ymd_and_hms(
            month.div_euclid(12),
            month.rem_euclid(12) as u32 + 1,
            1,
            0,
            0,
            0,
        )
        .unwrap()
        .timestamp_millis()
}

fn iso(ms: i64) -> String {
    use chrono::TimeZone as _;
    chrono::Utc
        .timestamp_millis_opt(ms)
        .unwrap()
        .to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

/// A V2 billing item shaped like t3code #28511's redacted live sample.
fn item(id: &str, ms: i64) -> serde_json::Value {
    serde_json::json!({
        "billingItemId": id,
        "eventTime": iso(ms),
        "product": "cursor",
        "chargeCategory": "usage",
        "usageType": "included",
        "principal": {"type": "user", "id": USER},
        "usage": {"prompt-text-tokens": "1263", "cached-prompt-text-tokens": "46400",
                  "cache-write-text-tokens": "0", "completion-text-tokens": "312"},
        "totalTokens": "47975",
        "hasTokenMeters": true,
        "pricing": {"currency": "USD", "contracted": "0.0000000000"},
        "displayExtras": {"model": "composer-2.5", "userEmail": EMAIL_CANARY,
                          "displayLabel": "Composer 2.5"},
    })
}

fn v2_page(items: &[serde_json::Value], next: Option<&str>) -> Reply {
    let pagination = match next {
        Some(next) => serde_json::json!({"nextCursor": next, "hasMore": true}),
        None => serde_json::json!({}),
    };
    json_ok(&serde_json::json!({"data": items, "pagination": pagination}).to_string())
}

/// `{}`: t3code reads it as an idle slice (discovery §2).
fn v2_empty() -> Reply {
    json_ok("{}")
}

/// The old endpoint's page 1 for a migrated account (#520): an object
/// without `usageEventsDisplay`.
fn old_migrated() -> Reply {
    json_ok("{}")
}

fn old_page(rows: &[serde_json::Value]) -> Reply {
    json_ok(
        &serde_json::json!({"totalUsageEventsCount": rows.len(), "usageEventsDisplay": rows})
            .to_string(),
    )
}

/// An old-endpoint row at `ms`.
fn old_row(id: &str, ms: i64) -> serde_json::Value {
    let mut row = event(id);
    row["timestamp"] = ms.to_string().into();
    row
}

fn v2_limits(explicit: bool) -> Limits {
    Limits {
        max_pages: 64,
        max_v2_pages: 64,
        explicit,
        ..limits()
    }
}

fn run_v2(f: &Fixture, replies: Vec<Reply>, limits: Limits) -> (Outcome, Vec<Seen>) {
    let (url, seen) = serve(replies);
    let outcome = run_with(&f.db, &f.dir, &url, limits, &|| true);
    let seen = seen.lock().unwrap().clone();
    (outcome, seen)
}

fn v2_requests(seen: &[Seen]) -> Vec<Seen> {
    seen.iter()
        .filter(|s| {
            s.head
                .starts_with("GET /api/dashboard/v2/query-billing-items?")
        })
        .cloned()
        .collect()
}

fn query(seen: &Seen) -> std::collections::BTreeMap<String, String> {
    let target = seen.head.split(' ').nth(1).unwrap();
    reqwest::Url::parse(&format!("http://h{target}"))
        .unwrap()
        .query_pairs()
        .into_owned()
        .collect()
}

fn sidecar_path(dir: &Path) -> PathBuf {
    dir.join(sidecar_name(&stem(USER).unwrap()))
}

fn sidecar_json(dir: &Path) -> serde_json::Value {
    serde_json::from_slice(&std::fs::read(sidecar_path(dir)).unwrap()).unwrap()
}

fn live_rows(dir: &Path) -> Vec<serde_json::Value> {
    let file: serde_json::Value =
        serde_json::from_slice(&std::fs::read(expected_file(dir)).unwrap()).unwrap();
    file["usageEventsDisplay"].as_array().unwrap().clone()
}

fn row_ms(row: &serde_json::Value) -> i64 {
    timestamp_ms(row["timestamp"].as_str().unwrap()).unwrap()
}

fn row_tokens(row: &serde_json::Value) -> f64 {
    [
        "inputTokens",
        "outputTokens",
        "cacheReadTokens",
        "cacheWriteTokens",
    ]
    .iter()
    .map(|key| row["tokenUsage"][key].as_f64().unwrap_or(0.0))
    .sum()
}

fn tokens_in_month(dir: &Path, back: i32) -> f64 {
    let (start, end) = (month_start_ms(back), month_start_ms(back - 1));
    live_rows(dir)
        .iter()
        .filter(|row| (start..end).contains(&row_ms(row)))
        .map(row_tokens)
        .sum()
}

/// The live file as the old walk writes it, from `rows`.
fn seed_by_old_walk(f: &Fixture, rows: &[serde_json::Value]) {
    let (outcome, _) = run_v2(f, vec![old_page(rows)], v2_limits(false));
    assert_eq!(outcome.stop.state, State::Ok);
}

/// The cutover the first V2 round below leaves: the newest old row.
fn v2_cutover() -> i64 {
    month_start_ms(1) + 2 * HOUR
}

/// Two old rows in M-1 (the newer one is the cutover), then a first V2
/// round (windows M, M-1): `v-current` in M; in M-1 one item before the
/// cutover, one at it, `v-after` after it.
fn first_v2_round(f: &Fixture) -> Outcome {
    let cutover = v2_cutover();
    seed_by_old_walk(
        f,
        &[
            old_row("a", month_start_ms(1) + HOUR),
            old_row("b", cutover),
        ],
    );
    let (outcome, seen) = run_v2(
        f,
        vec![
            old_migrated(),
            v2_page(&[item("v-current", month_start_ms(0))], None),
            v2_page(
                &[
                    item("v-before", cutover - HOUR / 2),
                    item("v-at", cutover),
                    item("v-after", cutover + HOUR),
                ],
                None,
            ),
        ],
        v2_limits(false),
    );
    assert_eq!(
        v2_requests(&seen).len(),
        2,
        "range: the cutover's month to now"
    );
    outcome
}

/// D1a control: an account the old endpoint still answers never reaches V2.
/// Mutation: `if page == 1 && lacks_usage_events(&body) {` -> `if page == 1 {`.
#[test]
fn unmigrated_account_never_calls_billing_v2() {
    let f = fixture();
    let (outcome, seen) = run_v2(&f, vec![json_ok(&page(1, &["a"]))], v2_limits(true));
    assert_eq!(outcome, ok(1));
    assert_eq!(seen.len(), 1);
    assert!(seen[0].head.starts_with("POST /api/usage "));
    assert!(!sidecar_path(&f.dir).exists());
}

/// Nothing is frozen at the switch: a refused V2 round leaves the old file
/// alone and creates no sidecar, and a later old success writes only its
/// own rows. Mutation: in `billing_v2`, after `drop((history, live));` add a
/// `commit_v2` of an empty walk (freeze at switch).
#[test]
fn a_refused_switch_freezes_nothing_and_old_success_writes_only_its_rows() {
    let f = fixture();
    let t = month_start_ms(1) + HOUR;
    seed_by_old_walk(&f, &[old_row("a", t), old_row("b", t + 1000)]);
    let before = std::fs::read(expected_file(&f.dir)).unwrap();
    let (outcome, seen) = run_v2(
        &f,
        vec![
            old_migrated(),
            json_ok(r#"{"availability":"missing_team_id"}"#),
        ],
        v2_limits(false),
    );
    assert_eq!(
        outcome,
        stop(State::Error, Some("billing_v2_team_required"))
    );
    assert_eq!(v2_requests(&seen).len(), 1);
    assert!(!sidecar_path(&f.dir).exists(), "frozen at the switch");
    assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), before);

    let rows = [
        old_row("a", t),
        old_row("b", t + 1000),
        old_row("c", t + 2000),
    ];
    let (outcome, _) = run_v2(&f, vec![old_page(&rows)], v2_limits(false));
    assert_eq!(outcome, ok(3));
    assert!(!sidecar_path(&f.dir).exists());
    assert_eq!(live_rows(&f.dir).len(), 3);
    let total: f64 = live_rows(&f.dir).iter().map(row_tokens).sum();
    assert_eq!(total, 3.0 * OLD_ROW_TOKENS);
}

/// D5: the first V2 commit keeps the old rows (pre-cutover total unchanged)
/// and adds only V2 rows after the cutover. Mutations: (plain replace)
/// `                frozen: live.rows,` -> `                frozen: Vec::new(),`;
/// (no cutover filter) `.filter(|row| cutover.is_none_or(|cutover| row.ms >
/// cutover))` -> `.filter(|_| true)`.
#[test]
fn the_first_v2_commit_keeps_old_rows_and_adds_only_rows_after_the_cutover() {
    let f = fixture();
    let outcome = first_v2_round(&f);
    assert_eq!(outcome, ok(4));
    let cutover = v2_cutover();
    let rows = live_rows(&f.dir);
    assert_eq!(rows.len(), 4);
    let pre: f64 = rows
        .iter()
        .filter(|row| row_ms(row) <= cutover)
        .map(row_tokens)
        .sum();
    assert_eq!(pre, 2.0 * OLD_ROW_TOKENS, "pre-cutover total changed");
    let after: Vec<i64> = rows
        .iter()
        .filter(|row| row_ms(row) > cutover)
        .map(row_ms)
        .collect();
    assert_eq!(after.len(), 2);
    assert!(after.contains(&(cutover + HOUR)) && after.contains(&month_start_ms(0)));
    let history = sidecar_json(&f.dir);
    assert_eq!(history["cutoverMs"], cutover);
    assert_eq!(history["frozen"].as_array().unwrap().len(), 2);
    // W2: the sidecar is written the secure way, like the live file.
    assert!(meets_contract(&sidecar_path(&f.dir), false), "sidecar");
}

/// Design 4: a second V2 commit keeps the sidecar, its cutover and frozen
/// rows byte for byte, and writes an item first seen in round 2.
/// Mutation: `remove_usage_files(dir, &[&final_name, &sidecar_name], false)`
/// -> `remove_usage_files(dir, &[&final_name], false)`.
#[test]
fn a_second_v2_commit_keeps_the_sidecar_and_its_cutover() {
    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    assert!(
        sidecar_path(&f.dir).exists(),
        "the first commit dropped its sidecar"
    );
    let first = sidecar_json(&f.dir);
    let cutover = v2_cutover();
    let new_ms = (now_ms_i64() - 60_000).max(month_start_ms(0) + 1);
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(
                &[item("v-current", month_start_ms(0)), item("v-new", new_ms)],
                None,
            ),
            v2_page(&[item("v-after", cutover + HOUR)], None),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome.stop.state, State::Ok);
    assert!(sidecar_path(&f.dir).exists(), "the sidecar was dropped");
    let second = sidecar_json(&f.dir);
    assert_eq!(
        second["cutoverMs"].to_string(),
        first["cutoverMs"].to_string()
    );
    assert_eq!(second["frozen"].to_string(), first["frozen"].to_string());
    let rows = live_rows(&f.dir);
    assert_eq!(rows.len(), 5);
    assert!(rows.iter().any(|row| row_ms(row) == new_ms));
}

/// Design 5b: a background round reads only M-2..M; month M-5's V2 rows
/// stay, and an item dated M-5 inside window M (a month this round did not
/// read) is not written. Mutations: (A) delete `events.extend(live_v2
/// .into_iter().filter(|event| !replaced(event)));`; (B) delete
/// `rows.retain(|row| months.contains(&row.month));`.
#[test]
fn a_background_round_keeps_the_months_it_did_not_re_read() {
    let f = fixture();
    let at = |back: i32| month_start_ms(back) + if back == 0 { 0 } else { HOUR };
    let mut replies = vec![old_migrated()];
    replies.extend((0..12).map(|back| v2_page(&[item(&format!("m{back}"), at(back))], None)));
    let (outcome, seen) = run_v2(&f, replies, v2_limits(true));
    assert_eq!(outcome, ok(12));
    assert_eq!(v2_requests(&seen).len(), 12);
    assert_eq!(tokens_in_month(&f.dir, 5), ITEM_TOKENS);

    let mut leak = item("leak", at(5) + HOUR);
    leak["usage"]["prompt-text-tokens"] = "999999".into();
    let (outcome, seen) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[item("m0", at(0)), leak], None),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome.stop.state, State::Ok);
    assert_eq!(
        v2_requests(&seen).len(),
        3,
        "a background round reads M-2..M"
    );
    assert_eq!(tokens_in_month(&f.dir, 5), ITEM_TOKENS, "month M-5 changed");
    assert_eq!(live_rows(&f.dir).len(), 12);
}

/// Design 6: a month that had rows and now answers `{}` keeps them; the
/// rest of the file is updated. Mutation: in `replaced`,
/// `.is_some_and(|month| with_rows.contains(&month))` ->
/// `.is_some_and(|month| months.contains(&month))` (`{}` = empty month).
#[test]
fn a_month_answering_empty_keeps_its_rows() {
    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    let cutover = v2_cutover();
    let new_ms = (now_ms_i64() - 60_000).max(month_start_ms(0) + 1);
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(
                &[item("v-current", month_start_ms(0)), item("v-new", new_ms)],
                None,
            ),
            v2_empty(),
        ],
        v2_limits(true),
    );
    assert_eq!(outcome.stop.state, State::Ok);
    let times: Vec<i64> = live_rows(&f.dir).iter().map(row_ms).collect();
    assert!(
        times.contains(&(cutover + HOUR)),
        "M-1's row was dropped on {{}}"
    );
    assert!(times.contains(&new_ms), "the file was not updated");
    assert_eq!(times.len(), 5);
}

/// An item on the M-1/M boundary that both windows return is written once.
/// Mutation: `if ids.insert(id) { rows.push(row); }` -> `ids.insert(id);
/// rows.push(row);`.
#[test]
fn an_item_on_a_window_boundary_is_written_once() {
    let f = fixture();
    let boundary = item(ITEM_ID_CANARY, month_start_ms(0) - 500);
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[boundary.clone(), item("c", month_start_ms(0))], None),
            v2_page(&[boundary], None),
            v2_empty(),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome.stop.state, State::Ok);
    let at_boundary = live_rows(&f.dir)
        .iter()
        .filter(|row| row_ms(row) == month_start_ms(0) - 500)
        .count();
    assert_eq!(at_boundary, 1);
}

/// Refusals and partial walks leave the live file and the sidecar as they
/// were, with fixed reasons; a 429 stops the whole walk. Mutations: swap
/// the `Some("missing_team_id")` arm's reason with the `Some(_)` arm's;
/// delete `|| !cursors.insert(next.clone())`; delete `next.len() >
/// MAX_CURSOR_BYTES ||`; delete the `if status.as_u16() == 429 {…}` block
/// in `refusal`.
#[test]
fn refusals_leave_the_file_and_sidecar_unchanged() {
    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    let live = std::fs::read(expected_file(&f.dir)).unwrap();
    let side = std::fs::read(sidecar_path(&f.dir)).unwrap();
    let current = || item("v-current", month_start_ms(0));
    let long = "c".repeat(4097);
    let cases: Vec<(Vec<Reply>, Outcome, usize)> = vec![
        (
            vec![json_ok(r#"{"availability":"missing_team_id"}"#)],
            stop(State::Error, Some("billing_v2_team_required")),
            1,
        ),
        (
            vec![json_ok(r#"{"availability":"not_enabled"}"#)],
            stop(State::Error, Some("billing_v2_unavailable")),
            1,
        ),
        (
            vec![v2_page(&[current()], Some("c1")), v2_page(&[], Some("c1"))],
            stop(State::Partial, Some("too_many_pages")),
            2,
        ),
        (
            vec![v2_page(&[current()], Some(&long))],
            stop(State::Partial, Some("too_many_pages")),
            1,
        ),
        (
            vec![
                v2_page(&[current()], None),
                http("429 Too Many Requests", "application/json", "{}"),
            ],
            stop(State::Error, Some("rate_limited")),
            2,
        ),
        (
            vec![http("401 Unauthorized", "application/json", "{}")],
            stop(State::Expired, None),
            1,
        ),
        (
            vec![http("403 Forbidden", "text/html", "<html>blocked</html>")],
            stop(State::Error, Some("forbidden_non_json")),
            1,
        ),
        (
            vec![http("500 Internal Server Error", "text/plain", "x")],
            stop(State::Error, Some("server_error")),
            1,
        ),
        (
            vec![json_ok("[]")],
            stop(State::Error, Some("unexpected_response")),
            1,
        ),
        (
            vec![json_ok("<html>")],
            stop(State::Error, Some("unexpected_response")),
            1,
        ),
    ];
    for (replies, expected, requests) in cases {
        let mut all = vec![old_migrated()];
        all.extend(replies);
        let (outcome, seen) = run_v2(&f, all, v2_limits(false));
        assert_eq!(outcome, expected);
        let v2 = v2_requests(&seen);
        assert_eq!(v2.len(), requests, "{expected:?}");
        if requests == 2 && expected.stop.reason == Some("too_many_pages") {
            assert_eq!(query(&v2[1]).get("cursor").map(String::as_str), Some("c1"));
        }
        assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), live);
        assert_eq!(std::fs::read(sidecar_path(&f.dir)).unwrap(), side);
    }
}

/// `sync_status` as a background round (not Sync Now).
fn sync_background(f: &Fixture, url: &str, registry: &RwLock<Config>) -> serde_json::Value {
    let billing = v2_url(url);
    let target = Target {
        billing_url: &billing,
        ..target(&f.db, url)
    };
    let source = || registry.read().unwrap().clone();
    sync_with(false, &target, &source, Some(limits())).0
}

/// Design 5: no old file and rows only in the current month: the first
/// background round (full budget: no file yet) reads M (two pages), M-1,
/// M-2, M-3 and commits. The request is the GET the plan names. Mutation:
/// delete `if stop_after_empty && empty_run >= EMPTY_WINDOWS_STOP { break; }`.
#[test]
fn no_old_file_and_rows_only_this_month_commit_after_four_windows() {
    let _guard = lock();
    // The default profile's key is the same in every test: forget what an
    // earlier test's last round left, so this background round sees a
    // profile with no file (full budget).
    IN_FLIGHT
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .no_file
        .clear();
    let f = fixture();
    let (url, seen) = serve(vec![
        old_migrated(),
        v2_page(&[item("x1", month_start_ms(0))], Some("n1")),
        v2_page(&[item("x2", month_start_ms(0) + 1)], None),
        v2_empty(),
        json_ok(r#"{"data":[]}"#),
        json_ok(r#"{"data":[],"pagination":{}}"#),
    ]);
    let status = sync_background(&f, &url, &registry(true, &f.dir));
    assert_eq!(status["state"], "ok", "{status}");
    assert_eq!(status["events"], 2);
    assert_eq!(ROUND_BUDGET.with(|b| b.get()), Some(FULL_BUDGET));
    let seen = seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 6);
    let v2 = v2_requests(&seen);
    assert_eq!(v2.len(), 5);
    let first = query(&v2[0]);
    assert_eq!(first.get("scope").map(String::as_str), Some("user"));
    assert_eq!(first.get("pageSize").map(String::as_str), Some("100"));
    assert_eq!(
        first.get("startTime"),
        Some(&iso(month_start_ms(0)).replace(".000Z", "Z"))
    );
    assert!(first.get("endTime").unwrap().ends_with('Z'));
    assert!(!first.contains_key("cursor"));
    assert_eq!(query(&v2[1]).get("cursor").map(String::as_str), Some("n1"));
    assert_eq!(
        query(&v2[2]).get("endTime"),
        Some(&iso(month_start_ms(0) - 1000).replace(".000Z", "Z"))
    );
    for request in &v2 {
        assert_eq!(
            header(&request.head, "cookie").as_deref(),
            Some(format!("WorkosCursorSessionToken={USER}%3A%3A{}", f.token).as_str())
        );
        let head = request.head.to_ascii_lowercase();
        assert!(head.contains("origin: https://cursor.com\r\n"));
        assert!(head.contains("accept: application/json\r\n"));
    }
    assert_eq!(sidecar_json(&f.dir)["cutoverMs"], serde_json::Value::Null);
}

/// D6: no old file and no rows anywhere: nothing is written. Mutation:
/// delete `if events.is_empty() { return Err(Stop::error("billing_v2_no_items")); }`.
#[test]
fn no_old_file_and_no_rows_writes_nothing() {
    let f = fixture();
    let mut grok = item("g", month_start_ms(1) + HOUR);
    grok["product"] = "grok-bot".into();
    let (outcome, seen) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_empty(),
            v2_page(&[grok], None),
            json_ok(r#"{"data":[]}"#),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome, stop(State::Error, Some("billing_v2_no_items")));
    assert_eq!(v2_requests(&seen).len(), 3);
    assert!(!expected_file(&f.dir).exists());
    assert!(!sidecar_path(&f.dir).exists());
}

/// S4: the page cap counts every window of one walk. Mutation: move `let
/// mut pages = 0u32;` into the `for &month in windows` loop (per window).
#[test]
fn the_page_cap_counts_across_windows() {
    let f = fixture();
    let mut replies = vec![old_migrated()];
    replies.extend(
        (0..12).map(|back| v2_page(&[item(&format!("p{back}"), month_start_ms(back) + 1)], None)),
    );
    let (outcome, seen) = run_v2(
        &f,
        replies,
        Limits {
            max_v2_pages: 3,
            ..v2_limits(true)
        },
    );
    assert_eq!(outcome, stop(State::Partial, Some("too_many_pages")));
    assert_eq!(v2_requests(&seen).len(), 3);
    assert!(!expected_file(&f.dir).exists());
}

fn sidecar_of(c: char) -> String {
    format!("{SIDECAR_PREFIX}{}.json", c.to_string().repeat(64))
}

/// Disable, profile removal and an account switch delete live files and
/// sidecars; a sidecar of another stem is never read and is cleaned by a V2
/// commit. Mutation: `|| is_sidecar_name(name)` deleted from `is_ours`.
#[test]
fn cleanup_deletes_sidecars_and_ignores_another_stems() {
    {
        let root = DataRoot::new();
        // Profile removal: B is not configured, so an on push removes it.
        let local = root.path().join(TEST_LOCAL_DIR);
        let parent = local.join(APP_DIR_NAME).join(PROFILES_DIR_NAME);
        secure_tree(&local, &parent);
        let orphan = parent.join(PROFILE_B);
        secure_tree(&parent, &orphan);
        write_secure(&orphan.join(hex_name('a')), b"x");
        write_secure(&orphan.join(sidecar_of('a')), b"x");
        set(serde_json::json!({"enabled": true, "profiles": []})).unwrap();
        assert!(!orphan.exists(), "the orphan's sidecar was left");
        // Disable.
        let dir = root.sync_dir();
        write_secure(&dir.join(hex_name('a')), b"x");
        write_secure(&dir.join(sidecar_of('a')), b"x");
        let off = set(serde_json::json!({"enabled": false})).unwrap();
        assert_eq!(off["removedFiles"], 2, "{off}");
        assert!(usage_names(&dir).is_empty() && !dir.join(sidecar_of('a')).exists());
    }

    // Account switch: another account's live file and sidecar go with the
    // next commit for this account.
    let f = fixture();
    write_secure(&f.dir.join(hex_name('b')), b"x");
    write_secure(&f.dir.join(sidecar_of('b')), b"x");
    seed_by_old_walk(&f, &[old_row("a", month_start_ms(1) + HOUR)]);
    assert!(!f.dir.join(hex_name('b')).exists());
    assert!(!f.dir.join(sidecar_of('b')).exists());
    assert!(expected_file(&f.dir).exists());

    // Another stem's sidecar is never read by a V2 commit, and goes.
    let f = fixture();
    let foreign = serde_json::json!({"frozen": [old_row("x", 1)], "cutoverMs": 1, "fetched": []});
    write_secure(&f.dir.join(sidecar_of('c')), foreign.to_string().as_bytes());
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[item("only", month_start_ms(0))], None),
            v2_empty(),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome, ok(1));
    assert!(!f.dir.join(sidecar_of('c')).exists());
    assert_eq!(live_rows(&f.dir).len(), 1);
    assert!(sidecar_json(&f.dir)["frozen"]
        .as_array()
        .unwrap()
        .is_empty());
}

/// S5/S7: `userEmail`, `principal.id`, `billingItemId`, the token and the
/// cookie never reach a file or the status; a page carrying two users
/// other than the cookie's, or an item without a principal, stops the walk
/// with nothing written (one other user is accepted:
/// `v2_one_user_principal_other_than_the_cookie_user`). Mutations: in
/// `struct Extras`, put `#[serde(rename = "userEmail")]` on `model`; make
/// the principal refusal in `walk` `if false`.
#[test]
fn v2_canaries_stay_out_and_a_second_other_principal_writes_nothing() {
    let _guard = lock();
    let mut outputs = Vec::new();
    let mut collect = |f: &Fixture, status: serde_json::Value| {
        outputs.push(status.to_string());
        for entry in std::fs::read_dir(&f.dir).into_iter().flatten().flatten() {
            outputs.push(entry.file_name().to_string_lossy().into_owned());
            if entry.file_type().unwrap().is_file() {
                outputs.push(String::from_utf8_lossy(&std::fs::read(entry.path()).unwrap()).into_owned());
            }
        }
    };
    let f = fixture();
    let mut replies = vec![
        old_migrated(),
        v2_page(&[item(ITEM_ID_CANARY, month_start_ms(0))], None),
    ];
    replies.extend((0..11).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_status(&f, &url, &registry(true, &f.dir));
    assert_eq!(status["state"], "ok", "{status}");
    collect(&f, status);

    let mut other = item("o", month_start_ms(0));
    other["principal"]["id"] = OTHER_USER.into();
    let mut third = item("t", month_start_ms(0));
    third["principal"]["id"] = THIRD_USER.into();
    let mut missing = item("m", month_start_ms(0));
    missing.as_object_mut().unwrap().remove("principal");
    for bad in [vec![other, third], vec![missing]] {
        let f = fixture();
        let mut page = vec![item("fine", month_start_ms(0))];
        page.extend(bad);
        let (url, _) = serve(vec![old_migrated(), v2_page(&page, None)]);
        let status = sync_status(&f, &url, &registry(true, &f.dir));
        assert_eq!(status["state"], "error");
        assert_eq!(status["reason"], "billing_v2_principal_mismatch");
        assert!(!expected_file(&f.dir).exists());
        assert!(!sidecar_path(&f.dir).exists());
        collect(&f, status);
    }

    let all = outputs.join("\n");
    for canary in [
        EMAIL_CANARY,
        USER,
        OTHER_USER,
        THIRD_USER,
        ITEM_ID_CANARY,
        SIGNATURE,
        f.token.as_str(),
    ] {
        assert!(!all.contains(canary), "canary leaked");
    }
    // Control: the outputs searched hold the written row and the refusal.
    assert!(all.contains("composer-2.5") && all.contains("billing_v2_principal_mismatch"));
}

/// #520 (macOS #559): a migrated account's items can carry one user id that
/// is not the cookie's (reporter: 100 of 100 items, one distinct id). The
/// walk accepts it across pages and windows and keeps it out of the files
/// and the status; it accepts it beside the cookie's own id (months before
/// the move), whose `type` is not read. After the first other id it refuses
/// a third id on a later page or window, a principal on another id whose
/// `type` is `User`, `organization`, `team` or missing, and a missing
/// principal; it refuses a first `team` principal on another id and items
/// whose ids are all empty. Mutations (S7 set): in `walk`, refuse
/// `OtherUser`; accept it without the comparison; declare `other` inside
/// the window loop (pin per window); accept `Refused`; in `owner`, drop the
/// `type` test; let an untyped other id pass; compare `type` without case;
/// require `type: user` for the cookie's id; pass an empty id.
#[test]
fn v2_one_user_principal_other_than_the_cookie_user() {
    let _guard = lock();
    let as_user = |id: &str, ms: i64, user: &str| {
        let mut item = item(id, ms);
        item["principal"]["id"] = user.into();
        item
    };
    let f = fixture();
    let mut replies = vec![
        old_migrated(),
        v2_page(&[as_user("o1", month_start_ms(0), OTHER_USER)], Some("n1")),
        v2_page(&[as_user("o2", month_start_ms(0) + 1, OTHER_USER)], None),
        v2_page(&[as_user("o3", month_start_ms(1), OTHER_USER)], None),
    ];
    replies.extend((0..10).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_status(&f, &url, &registry(true, &f.dir));
    assert_eq!(status["state"], "ok", "{status}");
    assert_eq!(status["events"], 3, "{status}");
    let mut outputs = vec![status.to_string()];
    for entry in std::fs::read_dir(&f.dir).unwrap().flatten() {
        outputs.push(entry.file_name().to_string_lossy().into_owned());
        if entry.file_type().unwrap().is_file() {
            outputs.push(String::from_utf8_lossy(&std::fs::read(entry.path()).unwrap()).into_owned());
        }
    }
    let all = outputs.join("\n");
    assert!(
        !all.contains(OTHER_USER) && !all.contains(USER),
        "principal leaked"
    );
    // Control: the outputs searched hold the written rows.
    assert!(expected_file(&f.dir).exists() && all.contains("composer-2.5"));

    // The move inside one walk: the newest month on the other id, older
    // items on the cookie's id with `type: user`, none, or another spelling.
    let f = fixture();
    let mut untyped = item("c2", month_start_ms(0) + 2);
    untyped["principal"].as_object_mut().unwrap().remove("type");
    let mut spelled = item("c3", month_start_ms(0) + 3);
    spelled["principal"]["type"] = "USER".into();
    let mut replies = vec![
        old_migrated(),
        v2_page(&[as_user("o1", month_start_ms(0), OTHER_USER)], Some("n1")),
        v2_page(&[untyped, spelled], None),
        v2_page(&[item("c4", month_start_ms(1))], None),
    ];
    replies.extend((0..10).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_status(&f, &url, &registry(true, &f.dir));
    assert_eq!(
        status["state"], "ok",
        "cookie id beside the other: {status}"
    );
    assert_eq!(status["events"], 4, "{status}");

    let pinned = || as_user("a", month_start_ms(0), OTHER_USER);
    let later = |change: &dyn Fn(&mut serde_json::Value)| {
        let mut item = as_user("b", month_start_ms(0) + 1, OTHER_USER);
        change(&mut item);
        vec![v2_page(&[pinned()], Some("n1")), v2_page(&[item], None)]
    };
    let mut team = as_user("t", month_start_ms(0), OTHER_USER);
    team["principal"]["type"] = "team".into();
    let cases = [
        (
            "third id on a later page",
            later(&|i| i["principal"]["id"] = THIRD_USER.into()),
        ),
        (
            "third id in a later window",
            vec![
                v2_page(&[pinned()], None),
                v2_page(&[as_user("b", month_start_ms(1), THIRD_USER)], None),
            ],
        ),
        (
            "team after the pin",
            later(&|i| i["principal"]["type"] = "team".into()),
        ),
        (
            "User-cased other after the pin",
            later(&|i| i["principal"]["type"] = "User".into()),
        ),
        (
            "organization after the pin",
            later(&|i| i["principal"]["type"] = "organization".into()),
        ),
        (
            "untyped other after the pin",
            later(&|i| {
                i["principal"].as_object_mut().unwrap().remove("type");
            }),
        ),
        (
            "missing after the pin",
            later(&|i| {
                i.as_object_mut().unwrap().remove("principal");
            }),
        ),
        (
            "every id empty",
            vec![v2_page(
                &[
                    as_user("a", month_start_ms(0), ""),
                    as_user("b", month_start_ms(0) + 1, ""),
                ],
                None,
            )],
        ),
        ("first item a team", vec![v2_page(&[team], None)]),
    ];
    for (case, pages) in cases {
        let f = fixture();
        let mut replies = vec![old_migrated()];
        replies.extend(pages);
        replies.extend((0..12).map(|_| v2_empty()));
        let (url, _) = serve(replies);
        let status = sync_status(&f, &url, &registry(true, &f.dir));
        assert_eq!(status["state"], "error", "{case}: {status}");
        assert_eq!(status["reason"], "billing_v2_principal_mismatch", "{case}");
        assert!(!expected_file(&f.dir).exists(), "{case}");
        assert!(!sidecar_path(&f.dir).exists(), "{case}");
    }
}

/// S8: an unparseable `eventTime` is dropped; a future newest old row
/// clamps the cutover to now. Mutations: (drop) in `map`, `.and_then(timestamp_ms)`
/// -> `.and_then(|text| timestamp_ms(text).or(Some(0)))`; (clamp)
/// `.map_or(now_ms, |newest| newest.min(now_ms))` -> `.map_or(now_ms,
/// |newest| newest)`.
#[test]
fn bad_event_times_are_dropped_and_a_future_cutover_is_clamped() {
    let f = fixture();
    let mut bad = item("bad", month_start_ms(0));
    bad["eventTime"] = "yesterday".into();
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[bad, item("good", month_start_ms(0))], None),
            v2_empty(),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(
        outcome,
        Outcome {
            dropped: 1,
            ..ok(1)
        }
    );

    let f = fixture();
    let future = 4_102_444_800_000; // 2100-01-01
    seed_by_old_walk(&f, &[old_row("a", month_start_ms(1)), old_row("z", future)]);
    let before = now_ms_i64();
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[item("v", month_start_ms(0))], None),
        ],
        v2_limits(true),
    );
    let after = now_ms_i64();
    assert_eq!(outcome.stop.state, State::Ok);
    let cutover = sidecar_json(&f.dir)["cutoverMs"].as_i64().unwrap();
    assert!((before..=after).contains(&cutover), "cutover {cutover}");
}

/// Discovery §4 mapping: meters, model, timestamp, no conversation id; D2
/// (contracted 0 -> no chargedCents, USD > 0 -> integer cents, another
/// currency -> none); D3 (only `cursor` rows); `hasTokenMeters:false`
/// skipped; only the engine's fields written. Mutations: (D3 off) delete
/// `if item.product.as_deref() != Some("cursor") { return Mapped::Skipped; }`;
/// (hasTokenMeters) delete its `return Mapped::Skipped` block; (currency)
/// delete `.filter(|pricing| pricing.currency.as_deref() == Some("USD"))`;
/// (meter swap) `input_tokens: usage.prompt,` <-> `cache_read_tokens:
/// usage.cached,`.
#[test]
fn v2_items_map_to_engine_rows() {
    let f = fixture();
    let t = month_start_ms(0);
    let included = item("included", t);
    let mut paid = item("paid", t + 1);
    paid["pricing"]["contracted"] = "0.0700000000".into();
    let mut euro = item("euro", t + 4);
    euro["pricing"] = serde_json::json!({"currency": "EUR", "contracted": "0.0700000000"});
    let mut garbage = item("garbage", t + 5);
    garbage["pricing"]["contracted"] = "FREETEXTCANARY".into();
    let mut number = item("number", t + 6);
    number["pricing"]["contracted"] = serde_json::json!(4e-3);
    let mut grok = item("grok", t + 2);
    grok["product"] = "grok-bot".into();
    let mut meterless = item("meterless", t + 3);
    meterless["hasTokenMeters"] = false.into();
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(
                &[included, paid, grok, meterless, euro, garbage, number],
                None,
            ),
            v2_empty(),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome, ok(5));
    let rows = live_rows(&f.dir);
    assert_eq!(rows.len(), 5);
    let garbage = rows.iter().find(|r| row_ms(r) == t + 5).unwrap();
    assert!(
        garbage.get("chargedCents").is_none(),
        "free text priced the row"
    );
    assert!(!live_rows(&f.dir)
        .iter()
        .any(|r| r.to_string().contains("FREETEXTCANARY")));
    let number = rows.iter().find(|r| row_ms(r) == t + 6).unwrap();
    assert_eq!(
        number["chargedCents"].to_string(),
        "1",
        "a paid row floors at 1 cent"
    );
    let euro = rows.iter().find(|r| row_ms(r) == t + 4).unwrap();
    assert!(
        euro.get("chargedCents").is_none(),
        "a EUR amount was written as cents"
    );
    let included = rows.iter().find(|r| row_ms(r) == t).unwrap();
    let paid = rows.iter().find(|r| row_ms(r) == t + 1).unwrap();
    assert_eq!(included["timestamp"], t.to_string());
    assert_eq!(included["model"], "composer-2.5");
    assert!(included.get("conversationId").is_none());
    assert!(
        included.get("chargedCents").is_none(),
        "D2: included row priced"
    );
    assert_eq!(
        paid["chargedCents"].to_string(),
        "7",
        "integer cents, no float"
    );
    let usage = &included["tokenUsage"];
    assert_eq!(usage["inputTokens"], 1263.0);
    assert_eq!(usage["cacheReadTokens"], 46400.0);
    assert_eq!(usage["cacheWriteTokens"], 0.0);
    assert_eq!(usage["outputTokens"], 312.0);
    assert!(usage.get("totalCents").is_none());
    for row in &rows {
        for key in row.as_object().unwrap().keys() {
            assert!(ALLOWED_EVENT_FIELDS.contains(&key.as_str()), "{key}");
        }
        for key in row["tokenUsage"].as_object().unwrap().keys() {
            assert!(ALLOWED_TOKEN_FIELDS.contains(&key.as_str()), "{key}");
        }
    }
}

/// Design 4: a successful old walk after V2 commits brings the full old
/// history back: the sidecar goes and the live file is the old rows alone.
/// Mutation (both, since either deletion alone suffices): delete `let _ =
/// secure::remove_file(&dir.join(sidecar_name(stem)));` in `commit_file`,
/// and delete `|| is_sidecar_name(name)` from `is_ours`.
#[test]
fn an_old_success_after_v2_deletes_the_sidecar() {
    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    assert!(sidecar_path(&f.dir).exists());
    let rows = [old_row("a", month_start_ms(1) + HOUR)];
    let (outcome, seen) = run_v2(&f, vec![old_page(&rows)], v2_limits(false));
    assert_eq!(outcome, ok(1));
    assert!(v2_requests(&seen).is_empty());
    assert!(
        !sidecar_path(&f.dir).exists(),
        "the sidecar outlived an old success"
    );
    assert_eq!(live_rows(&f.dir).len(), 1);
}

fn month_of(back: i32) -> i64 {
    i64::from(cursor_billing_v2::month_index(month_start_ms(back)).unwrap())
}

/// A profile that is a duplicate of an earlier one keeps its own live file
/// and that account's sidecar; another account's sidecar goes. Mutation:
/// `clean_candidate(&dir, slot.root.is_some(), &[&live, &sidecar])` ->
/// `clean_candidate(&dir, slot.root.is_some(), &[&live])`.
#[test]
fn a_duplicate_profile_keeps_its_own_sidecar() {
    let _guard = lock();
    let tmp = TempDir::new();
    let token = valid_token();
    let default_db = login_db(&tmp.path().join("default"), &token, USER);
    let root = profile_root(tmp.path(), "b", &token, USER);
    let registry = RwLock::new(multi(tmp.path(), true, vec![profile(PROFILE_A, &root)]));
    let dir = profile_dir(tmp.path(), &profile(PROFILE_A, &root));
    let own_sidecar = sidecar_path(&dir);
    let other_sidecar = dir.join(sidecar_of('e'));
    write_secure(
        &expected_file(&dir),
        br#"{"totalUsageEventsCount":0,"usageEventsDisplay":[]}"#,
    );
    write_secure(&own_sidecar, b"{}");
    write_secure(&other_sidecar, b"{}");
    let (url, _) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["ok", "duplicate"]);
    assert!(expected_file(&dir).exists());
    assert!(
        own_sidecar.exists(),
        "the duplicate dropped its own sidecar"
    );
    assert!(!other_sidecar.exists());
}

/// V2 pages are a fifth of the old size, so the V2 walk has its own cap: a
/// walk over the old 256 pages commits under production limits. Mutation:
/// in `Limits::production`, `max_v2_pages: MAX_V2_PAGES,` -> `max_v2_pages:
/// MAX_PAGES,`.
#[test]
fn a_v2_walk_past_the_old_page_cap_commits() {
    let f = fixture();
    let pages = MAX_PAGES as usize + 1;
    let mut replies = vec![old_migrated()];
    replies.extend((0..pages).map(|i| {
        let next = format!("c{i}");
        v2_page(
            &[item(&format!("p{i}"), month_start_ms(0))],
            (i + 1 < pages).then_some(next.as_str()),
        )
    }));
    replies.extend((0..3).map(|_| v2_empty()));
    let (outcome, seen) = run_v2(&f, replies, Limits::production(Duration::from_secs(120)));
    assert_eq!(outcome, ok(pages));
    assert_eq!(v2_requests(&seen).len(), pages + 3);
}

/// D5: a same-stem sidecar Syrtis cannot read (a directory, a file that
/// does not parse, or, on Windows, a file that fails the storage contract)
/// stops the round before any request, old or V2. Mutation: delete the
/// `if slot.candidates().iter().any(|dir| read_history(dir, &stem).is_err())
/// { return stopped(Stop::error("write_failed")); }` block in `run`.
#[test]
fn an_unreadable_sidecar_fails_before_any_request() {
    for make in [
        (|path: &Path| {
            drop(secure::ensure_dir(path.parent().unwrap()).unwrap());
            std::fs::create_dir(path).unwrap();
        }) as fn(&Path),
        |path: &Path| write_secure(path, b"not json"),
        |path: &Path| {
            drop(secure::ensure_dir(path.parent().unwrap()).unwrap());
            std::fs::write(path, br#"{"frozen":[],"cutoverMs":null,"fetched":[]}"#).unwrap();
        },
    ] {
        let f = fixture();
        make(&sidecar_path(&f.dir));
        let mut replies = vec![old_migrated()];
        replies
            .extend((0..12).map(|i| v2_page(&[item(&format!("u{i}"), month_start_ms(0))], None)));
        let (outcome, seen) = run_v2(&f, replies, v2_limits(true));
        assert_eq!(outcome, stop(State::Error, Some("write_failed")));
        assert!(seen.is_empty(), "requests sent before failing");
        assert!(!expected_file(&f.dir).exists());
    }
}

/// The old walk's commit is unchanged by a directory at another stem's
/// sidecar name (no cleanup failure; the entry stays), while disable still
/// tries it and reports it. Mutations: delete the `if spare_sidecar_dirs &&
/// is_sidecar_name(&name) && entry.file_type().is_ok_and(|kind|
/// kind.is_dir()) { continue; }` block in `remove_usage_files`; in
/// `clean_candidate`, `remove_usage_files(dir, keep, false)` ->
/// `remove_usage_files(dir, keep, true)`.
#[test]
fn a_directory_at_a_sidecar_name_spares_only_the_old_commit() {
    let other = sidecar_of('d');
    let f = fixture();
    drop(secure::ensure_dir(&f.dir).unwrap());
    std::fs::create_dir(f.dir.join(&other)).unwrap();
    let (outcome, _) = run_v2(&f, vec![json_ok(&page(1, &["a"]))], v2_limits(false));
    assert_eq!(outcome, ok(1));
    assert!(f.dir.join(&other).is_dir());
    assert_eq!(live_rows(&f.dir).len(), 1);

    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true})).unwrap();
    std::fs::create_dir_all(root.sync_dir().join(&other).join("inside")).unwrap();
    assert_eq!(
        set(serde_json::json!({"enabled": false})),
        Err("cleanupFailed".to_string())
    );
}

/// Round 3 (macOS item 1): a V2 commit, then an old walk whose sidecar
/// delete failed (the stale sidecar is put back), then V2 again. The stale
/// sidecar is not trusted: the commit freezes the old walk's file (every old
/// row kept), the cutover moves to its newest row, the months are read
/// again, and nothing is counted twice. Mutation: `Some(history) if trusted
/// =>` -> `Some(history) =>`.
#[test]
fn a_stale_sidecar_is_frozen_again_from_the_old_walks_file() {
    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    let stale = std::fs::read(sidecar_path(&f.dir)).unwrap();
    let cutover = v2_cutover();
    let newest = cutover + 3 * HOUR;
    let rows = [
        old_row("a", month_start_ms(1) + HOUR),
        old_row("b", cutover),
        old_row("c", newest),
    ];
    let (outcome, _) = run_v2(&f, vec![old_page(&rows)], v2_limits(false));
    assert_eq!(outcome, ok(3));
    write_secure(&sidecar_path(&f.dir), &stale);
    let (outcome, seen) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[item("v-current", month_start_ms(0))], None),
            v2_page(
                &[
                    item("v-after", cutover + HOUR),
                    item("v-late", newest + HOUR),
                ],
                None,
            ),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome, ok(5));
    assert_eq!(v2_requests(&seen).len(), 2);
    let times: Vec<i64> = live_rows(&f.dir).iter().map(row_ms).collect();
    for old in [month_start_ms(1) + HOUR, cutover, newest] {
        assert!(times.contains(&old), "old row {old} lost");
    }
    assert!(
        !times.contains(&(cutover + HOUR)),
        "counted twice: before the new cutover"
    );
    let total: f64 = live_rows(&f.dir).iter().map(row_tokens).sum();
    assert_eq!(total, 3.0 * OLD_ROW_TOKENS + 2.0 * ITEM_TOKENS);
    let history = sidecar_json(&f.dir);
    assert_eq!(history["cutoverMs"], newest);
    assert_eq!(history["frozen"].as_array().unwrap().len(), 3);
    assert_eq!(
        history["fetched"],
        serde_json::json!([month_of(1), month_of(0)])
    );
    assert!(history["liveSha256"]
        .as_str()
        .is_some_and(|d| d.len() == 64));
}

/// Round 3 (macOS item 2): no cutover, and the live file is gone (an older
/// build's disable deletes it but not the sidecar). The background round
/// reads the full range, with no 3-empty stop, so a month V2 had read is
/// not dropped. Mutation: `let stop_after_empty = cutover.is_none() &&
/// !explicit && !stale;` -> `let stop_after_empty = cutover.is_none() &&
/// !explicit;`.
#[test]
fn a_stale_sidecar_without_a_cutover_reads_the_full_range() {
    let f = fixture();
    let months = |replies: &mut Vec<Reply>| {
        replies.push(v2_page(&[item("m0", month_start_ms(0))], None));
        replies.extend((0..4).map(|_| v2_empty()));
        replies.push(v2_page(&[item("m5", month_start_ms(5) + HOUR)], None));
        replies.extend((0..6).map(|_| v2_empty()));
    };
    let mut replies = vec![old_migrated()];
    months(&mut replies);
    let (outcome, _) = run_v2(&f, replies, v2_limits(true));
    assert_eq!(outcome, ok(2));
    std::fs::remove_file(expected_file(&f.dir)).unwrap();
    let mut replies = vec![old_migrated()];
    months(&mut replies);
    let (outcome, seen) = run_v2(&f, replies, v2_limits(false));
    assert_eq!(outcome, ok(2));
    assert_eq!(v2_requests(&seen).len(), 12);
    assert_eq!(tokens_in_month(&f.dir, 5), ITEM_TOKENS);
}

/// Round 3 (macOS item 6): a dropped copy never hides a complete copy of the
/// same id, and only drops that would otherwise have been written count (a
/// read month, after the cutover). Mutations: in `walk`, `Mapped::Dropped`
/// also inserts its id into `ids`; delete `.filter(|item|
/// self.months.contains(&item.month))`; delete the `.filter(|item| { item.ms
/// .is_none_or(…) })` filter in `dropped_after`.
#[test]
fn only_drops_that_would_have_been_written_count() {
    let f = fixture();
    let t = month_start_ms(0);
    let mut partial = item("same", t);
    partial["displayExtras"] = serde_json::json!({});
    // No cutover here, so only the month filter keeps this one out: M-6 is
    // a month this background walk never reads.
    let mut unread = item("unread", month_start_ms(6) + HOUR);
    unread["displayExtras"] = serde_json::json!({});
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[partial, unread], Some("n")),
            v2_page(&[item("same", t)], None),
            v2_empty(),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(
        outcome,
        ok(1),
        "the complete copy was not written, or a drop that would not be written was counted"
    );

    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    let cutover = v2_cutover();
    let model_less = |id: &str, ms: i64| {
        let mut row = item(id, ms);
        row["displayExtras"] = serde_json::json!({});
        row
    };
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(
                &[
                    model_less("after", (now_ms_i64() - 60_000).max(t + 1)),
                    model_less("unread-month", month_start_ms(5) + HOUR),
                ],
                None,
            ),
            v2_page(&[model_less("before", cutover - HOUR / 2)], None),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome.stop.state, State::Ok);
    assert_eq!(outcome.dropped, 1);
}

/// Rows with an unreadable `eventTime` or no model are dropped and counted
/// (`droppedRows`, a number only); they never count toward `events` or
/// against `billing_v2_no_items`. Mutations: (model-less written) the
/// `let Some(model) = … else { return Mapped::Dropped(id, Some(ms)); };`
/// -> `let model = …unwrap_or_default();`; (not recorded) `Mapped::Dropped(id,
/// ms) => dropped.push(…)` -> `Mapped::Dropped(..) => {}`; (not reported)
/// delete `if outcome.dropped > 0 { entry["droppedRows"] = … }`.
#[test]
fn rows_without_a_time_or_model_are_dropped_and_counted() {
    let _guard = lock();
    let f = fixture();
    let t = month_start_ms(0);
    let mut bad_time = item("bad-time", t);
    bad_time["eventTime"] = "not a time".into();
    let mut no_model = item("no-model", t + 1);
    no_model["displayExtras"] = serde_json::json!({"userEmail": EMAIL_CANARY});
    let mut empty_model = item("empty-model", t + 2);
    empty_model["displayExtras"]["model"] = "".into();
    let (url, _) = serve(vec![
        old_migrated(),
        v2_page(
            &[item("good", t + 3), bad_time, no_model, empty_model.clone()],
            None,
        ),
        v2_empty(),
        v2_empty(),
        v2_empty(),
    ]);
    let status = sync_background(&f, &url, &registry(true, &f.dir));
    assert_eq!(status["state"], "ok", "{status}");
    assert_eq!(status["events"], 1);
    assert_eq!(status["profiles"][0]["droppedRows"], 3);
    assert_eq!(live_rows(&f.dir).len(), 1);

    let f = fixture();
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[empty_model], None),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(
        outcome,
        Outcome {
            dropped: 1,
            ..stop(State::Error, Some("billing_v2_no_items"))
        }
    );
    assert!(!expected_file(&f.dir).exists());
}

/// No cutover: Sync Now reads all 12 months; a background round stops after
/// 3 consecutive empty calendar months, and a gap between its windows
/// restarts that count. Mutations: `let stop_after_empty =
/// cutover.is_none() && !explicit && !stale;` -> `let stop_after_empty =
/// cutover.is_none() && !stale;`; delete the `if previous.is_some_and(|previous|
/// previous - 1 != month) { empty_run = 0; }` block.
#[test]
fn explicit_rounds_read_every_month_and_background_rounds_stop_on_calendar_runs() {
    let only_m5 = |empties_after: usize| {
        let mut replies = vec![old_migrated()];
        replies.extend((0..5).map(|_| v2_empty()));
        replies.push(v2_page(&[item("m5", month_start_ms(5) + HOUR)], None));
        replies.extend((0..empties_after).map(|_| v2_empty()));
        replies
    };
    let f = fixture();
    let (outcome, seen) = run_v2(&f, only_m5(6), v2_limits(false));
    assert_eq!(outcome, stop(State::Error, Some("billing_v2_no_items")));
    assert_eq!(v2_requests(&seen).len(), 3);

    let f = fixture();
    let (outcome, seen) = run_v2(&f, only_m5(6), v2_limits(true));
    assert_eq!(outcome, ok(1));
    assert_eq!(v2_requests(&seen).len(), 12);
    assert_eq!(tokens_in_month(&f.dir, 5), ITEM_TOKENS);

    // Round 1 reads M..M-3; round 2 reads M, M-1, M-2, then the never-read
    // M-4, M-5, …: M-4 follows a gap, so the empty run restarts there.
    let f = fixture();
    let m0 = || v2_page(&[item("m0", month_start_ms(0))], None);
    let (outcome, _) = run_v2(
        &f,
        vec![old_migrated(), m0(), v2_empty(), v2_empty(), v2_empty()],
        v2_limits(false),
    );
    assert_eq!(outcome, ok(1));
    let (outcome, seen) = run_v2(
        &f,
        vec![
            old_migrated(),
            m0(),
            v2_empty(),
            v2_empty(),
            v2_empty(),
            v2_page(&[item("m5", month_start_ms(5) + HOUR)], None),
            v2_empty(),
            v2_empty(),
            v2_empty(),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome, ok(2));
    assert_eq!(v2_requests(&seen).len(), 8);
    assert_eq!(tokens_in_month(&f.dir, 5), ITEM_TOKENS);
}

/// Windows only (CURSOR-BV2-W): an added profile whose preferred dir fails
/// the storage contract commits into its `.secure` sibling (WB8); the next
/// background round plans from the history there (only M-2..M, every month
/// already read), not as a first commit (which would read on until three
/// empty months: four requests). Mutation: in `billing_v2`, `for candidate
/// in slot.candidates()` -> `for candidate in [dir.to_path_buf()]`.
#[test]
fn an_added_profile_plans_from_the_history_in_its_secure_sibling() {
    let _guard = lock();
    let tmp = TempDir::new();
    let default_dir = tmp.path().join("default");
    std::fs::create_dir_all(&default_dir).unwrap();
    let default_db = state_db(&default_dir, &[]);
    let root = profile_root(tmp.path(), "b", &other_token(), OTHER_USER);
    let p = profile(PROFILE_A, &root);
    let registry = RwLock::new(multi(tmp.path(), true, vec![p.clone()]));
    let preferred = profile_dir(tmp.path(), &p);
    std::fs::create_dir(&preferred).unwrap();
    let mut sibling = preferred.clone().into_os_string();
    sibling.push(".secure");
    let sibling = PathBuf::from(sibling);
    let as_other = |id: &str, ms: i64| {
        let mut item = item(id, ms);
        item["principal"]["id"] = OTHER_USER.into();
        item
    };
    let mut replies = vec![old_migrated()];
    replies.push(v2_page(&[as_other("m0", month_start_ms(0))], None));
    replies.extend((0..4).map(|_| v2_empty()));
    replies.push(v2_page(&[as_other("m5", month_start_ms(5) + HOUR)], None));
    replies.extend((0..6).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_round(&default_db, &url, &registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "{status}");
    let live = sibling.join(format!("usage.{}.json", stem(OTHER_USER).unwrap()));
    let side = sibling.join(sidecar_name(&stem(OTHER_USER).unwrap()));
    assert!(live.exists() && side.exists(), "fixture: the commit did not use the fallback");

    let mut replies = vec![old_migrated()];
    replies.push(v2_page(&[as_other("m0", month_start_ms(0))], None));
    replies.extend((0..6).map(|_| v2_empty()));
    let (url, seen) = serve(replies);
    let billing = v2_url(&url);
    let target = Target {
        billing_url: &billing,
        ..target(&default_db, &url)
    };
    let source = || registry.read().unwrap().clone();
    let status = sync_with(false, &target, &source, Some(limits())).0;
    assert_eq!(states(&status), ["notSignedIn", "ok"], "{status}");
    let seen = seen.lock().unwrap().clone();
    assert_eq!(v2_requests(&seen).len(), 3, "planned without the sibling's history");
    let rows: Vec<serde_json::Value> = serde_json::from_slice::<serde_json::Value>(
        &std::fs::read(&live).unwrap(),
    )
    .unwrap()["usageEventsDisplay"]
        .as_array()
        .unwrap()
        .clone();
    assert_eq!(rows.len(), 2, "month M-5 was dropped");
}

// --- Billing V2, Windows review round 1 -----------------------------------------

/// An added profile whose files are in its preferred dir, then the
/// commit's dir moves to the sticky `.secure` sibling.
struct SiblingCase {
    _tmp: TempDir,
    default_db: PathBuf,
    registry: RwLock<Config>,
    preferred: PathBuf,
    sibling: PathBuf,
    stem: String,
}

fn sibling_case() -> SiblingCase {
    let tmp = TempDir::new();
    let default_dir = tmp.path().join("default");
    std::fs::create_dir_all(&default_dir).unwrap();
    let default_db = state_db(&default_dir, &[]);
    let root = profile_root(tmp.path(), "b", &other_token(), OTHER_USER);
    let p = profile(PROFILE_A, &root);
    let registry = RwLock::new(multi(tmp.path(), true, vec![p.clone()]));
    let preferred = profile_dir(tmp.path(), &p);
    let mut sibling = preferred.clone().into_os_string();
    sibling.push(".secure");
    SiblingCase {
        _tmp: tmp,
        default_db,
        registry,
        preferred,
        sibling: PathBuf::from(sibling),
        stem: stem(OTHER_USER).unwrap(),
    }
}

fn as_other(id: &str, ms: i64) -> serde_json::Value {
    let mut item = item(id, ms);
    item["principal"]["id"] = OTHER_USER.into();
    item
}

fn rows_in(path: &Path) -> Vec<serde_json::Value> {
    serde_json::from_slice::<serde_json::Value>(&std::fs::read(path).unwrap()).unwrap()
        ["usageEventsDisplay"]
        .as_array()
        .unwrap()
        .clone()
}

/// Review round 1, item 1: the live file and a trusted sidecar sit in an
/// added profile's preferred dir when its `.secure` sibling appears; the next
/// V2 commit goes into the sibling and carries the history over (same
/// cutover, same frozen rows, every row kept) before the preferred dir is
/// cleaned. Mutation: in `v2_source`, delete the `for other in …` carry-over
/// loop (the plan then reads all 12 months and the commit freezes nothing:
/// no cutover, the old rows lost).
#[test]
fn a_sticky_secure_sibling_carries_the_v2_history_over() {
    let _guard = lock();
    let c = sibling_case();
    let cutover = v2_cutover();
    let (url, _) = serve(vec![old_page(&[
        old_row("a", month_start_ms(1) + HOUR),
        old_row("b", cutover),
    ])]);
    assert_eq!(states(&sync_round(&c.default_db, &url, &c.registry)), ["notSignedIn", "ok"]);
    let v2_round = || {
        vec![
            old_migrated(),
            v2_page(&[as_other("v-current", month_start_ms(0))], None),
            v2_page(&[as_other("v-after", cutover + HOUR)], None),
        ]
    };
    let (url, _) = serve(v2_round());
    assert_eq!(states(&sync_round(&c.default_db, &url, &c.registry)), ["notSignedIn", "ok"]);
    let first: serde_json::Value = serde_json::from_slice(
        &std::fs::read(c.preferred.join(sidecar_name(&c.stem))).unwrap(),
    )
    .unwrap();
    assert_eq!(first["cutoverMs"], cutover, "fixture");

    drop(secure::ensure_dir(&c.sibling).unwrap());
    // Padded, so a round planned without the carried-over history (all 12
    // months) still completes and the commit's rows decide.
    let mut replies = v2_round();
    replies.extend((0..10).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "{status}");
    let side: serde_json::Value = serde_json::from_slice(
        &std::fs::read(c.sibling.join(sidecar_name(&c.stem))).unwrap(),
    )
    .unwrap();
    assert_eq!(side["cutoverMs"], first["cutoverMs"], "the cutover was not carried over");
    assert_eq!(side["frozen"].to_string(), first["frozen"].to_string());
    let rows = rows_in(&c.sibling.join(format!("usage.{}.json", c.stem)));
    assert_eq!(rows.len(), 4, "old rows lost");
    let pre: f64 = rows.iter().filter(|r| row_ms(r) <= cutover).map(row_tokens).sum();
    assert_eq!(pre, 2.0 * OLD_ROW_TOKENS);
    assert!(!c.preferred.join(format!("usage.{}.json", c.stem)).exists());
    assert!(!c.preferred.join(sidecar_name(&c.stem)).exists());
}

/// Review round 1, item 2: an unreadable same-stem sidecar in an added
/// profile's dir that a commit does not use (here a loose preferred dir, so
/// commits go to the sibling) never refuses the round, V2 or old: the round
/// syncs and its cleanup reports the profile. The same sidecar in the dir the
/// commit uses still refuses before any request. Mutations: in `run`, check
/// every candidate (`slot.candidates().iter().any(|d| read_history(d,
/// &stem).is_err())`); in `v2_source`, refuse an unreadable other candidate
/// (`let (Ok(stored), Ok(live)) = … else { return Err(()) };`).
#[test]
fn only_the_dir_a_commit_uses_can_refuse_the_round() {
    let _guard = lock();
    let c = sibling_case();
    std::fs::create_dir(&c.preferred).unwrap();
    std::fs::create_dir(c.preferred.join(sidecar_name(&c.stem))).unwrap();
    let mut replies = vec![
        old_migrated(),
        v2_page(&[as_other("m0", month_start_ms(0))], None),
    ];
    replies.extend((0..11).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "V2: {status}");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));
    assert!(c.sibling.join(format!("usage.{}.json", c.stem)).exists());

    let (url, _) = serve(vec![old_page(&[old_row("a", month_start_ms(1) + HOUR)])]);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "old: {status}");
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));

    // Control: in the dir the commit uses, it refuses before any request.
    let used = c.sibling.join(sidecar_name(&c.stem));
    assert!(!used.exists(), "fixture: the old commit left its sidecar");
    std::fs::create_dir(&used).unwrap();
    let (url, seen) = serve(vec![json_ok(&page(1, &["a"]))]);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "error:write_failed"], "{status}");
    assert!(seen.lock().unwrap().is_empty());
}

/// Review round 1, item 3: an old walk whose live write fails (a reader
/// holds the live file) leaves the V2 sidecar in place, so the next V2 round
/// still trusts it: frozen rows and cutover unchanged, no V2 row frozen as
/// old history. Mutation: in `commit_file`, delete the sidecar before
/// `write_payload` (the earlier order).
#[test]
fn a_failed_old_commit_keeps_the_v2_sidecar() {
    let f = fixture();
    assert_eq!(first_v2_round(&f).stop.state, State::Ok);
    let side = std::fs::read(sidecar_path(&f.dir)).unwrap();
    let reader = std::fs::File::open(expected_file(&f.dir)).unwrap();
    let cutover = v2_cutover();
    let (outcome, _) = run_v2(
        &f,
        vec![old_page(&[
            old_row("a", month_start_ms(1) + HOUR),
            old_row("b", cutover),
            old_row("c", cutover + 3 * HOUR),
        ])],
        v2_limits(false),
    );
    drop(reader);
    assert_eq!(outcome, stop(State::Error, Some("write_failed")));
    assert_eq!(
        std::fs::read(sidecar_path(&f.dir)).ok(),
        Some(side),
        "the sidecar went before the live write"
    );
    let (outcome, _) = run_v2(
        &f,
        vec![
            old_migrated(),
            v2_page(&[item("v-current", month_start_ms(0))], None),
            v2_page(&[item("v-after", cutover + HOUR)], None),
        ],
        v2_limits(false),
    );
    assert_eq!(outcome.stop.state, State::Ok);
    let history = sidecar_json(&f.dir);
    assert_eq!(history["cutoverMs"], cutover, "V2 rows were frozen as old history");
    assert_eq!(history["frozen"].as_array().unwrap().len(), 2);
}

/// Review round 1, item 5: a junction at another stem's sidecar name is
/// spared by the old walk's commit like a directory (no cleanup failure, the
/// junction and its target stay). Mutation: in `remove_usage_files`,
/// `kind.is_dir() || kind.is_symlink()` -> `kind.is_dir()`.
#[test]
fn a_junction_at_a_sidecar_name_spares_the_old_commit() {
    let f = fixture();
    drop(secure::ensure_dir(&f.dir).unwrap());
    let target = f._tmp.path().join("elsewhere");
    std::fs::create_dir(&target).unwrap();
    std::fs::write(target.join("inside.txt"), b"x").unwrap();
    let link = f.dir.join(sidecar_of('d'));
    junction(&link, &target);
    assert!(std::fs::symlink_metadata(&link).unwrap().file_type().is_symlink(), "fixture");
    let (outcome, _) = run_v2(&f, vec![json_ok(&page(1, &["a"]))], v2_limits(false));
    assert_eq!(outcome, ok(1));
    assert!(std::fs::symlink_metadata(&link).is_ok(), "the junction was removed");
    assert!(target.join("inside.txt").exists());
}

/// Runs `body` with `MAX_STORED_FILE_BYTES` replaced by `cap` on this
/// thread (the sync runs on it: `RUNTIME.block_on`).
fn with_stored_cap<R>(cap: u64, body: impl FnOnce() -> R) -> R {
    STORED_CAP_FOR_TEST.with(|cell| cell.set(Some(cap)));
    let result = body();
    STORED_CAP_FOR_TEST.with(|cell| cell.set(None));
    result
}

fn v2_month_replies(items: Vec<serde_json::Value>) -> Vec<Reply> {
    let mut replies = vec![old_migrated(), v2_page(&items, None)];
    replies.extend((0..11).map(|_| v2_empty()));
    replies
}

/// Review round 1, item 7 (round 2: a small test cap): a same-stem sidecar
/// larger than the stored-file cap (valid JSON padded with spaces, so only
/// the size refuses it) stops the round before any request; at the cap it
/// is read. Mutation: in `secure::read`, both size checks -> `if false {`
/// and `take(cap + 1)` -> `take(u64::MAX)` (no cap).
#[test]
fn an_oversized_sidecar_is_refused_before_any_request() {
    const CAP: u64 = 4096;
    let json = br#"{"frozen":[],"cutoverMs":null,"fetched":[]}"#;
    for (size, refused) in [(CAP as usize + 1, true), (CAP as usize, false)] {
        let f = fixture();
        let mut bytes = json.to_vec();
        bytes.resize(size, b' ');
        write_secure(&sidecar_path(&f.dir), &bytes);
        let replies = v2_month_replies(vec![item("u", month_start_ms(0))]);
        let (outcome, seen) = with_stored_cap(CAP, || run_v2(&f, replies, v2_limits(true)));
        if refused {
            assert_eq!(outcome, stop(State::Error, Some("write_failed")));
            assert!(seen.is_empty(), "requests sent before failing");
        } else {
            assert_eq!(outcome.stop.state, State::Ok, "control: {outcome:?}");
        }
    }
}

// --- Billing V2, Windows review round 2 -----------------------------------------

/// A live file of old rows, as an old walk writes it.
fn old_live_bytes(rows: &[serde_json::Value]) -> Vec<u8> {
    serde_json::json!({"totalUsageEventsCount": rows.len(), "usageEventsDisplay": rows})
        .to_string()
        .into_bytes()
}

fn sibling_history(c: &SiblingCase) -> serde_json::Value {
    serde_json::from_slice(&std::fs::read(c.sibling.join(sidecar_name(&c.stem))).unwrap()).unwrap()
}

/// Maintainer decision 2026-11-11 (review round 3): the carry-over does not
/// check the other dir's folder, only each file. A preferred dir that no
/// longer meets the contract (another owner) still hands its trusted live
/// file and sidecar to the sibling (same cutover, same frozen rows, every
/// row kept); a live file that fails the per-file contract (inherited
/// permissions) in a secure preferred dir is never read (nothing frozen)
/// and is left in place. Mutation: in `v2_source`, re-add the folder gate
/// `if !matches!(real_dir(other), Ok(true)) || secure::ensure_dir(other).is_err() { continue; }`.
#[test]
fn a_loosened_preferred_dir_is_carried_over_but_a_loose_file_is_not() {
    let _guard = lock();
    let cutover = v2_cutover();
    let v2_round = || {
        let mut replies = vec![
            old_migrated(),
            v2_page(&[as_other("v-current", month_start_ms(0))], None),
            v2_page(&[as_other("v-after", cutover + HOUR)], None),
        ];
        replies.extend((0..10).map(|_| v2_empty()));
        replies
    };
    // A trusted pair in the preferred dir, then that dir gets another owner.
    let c = sibling_case();
    let (url, _) = serve(vec![old_page(&[
        old_row("a", month_start_ms(1) + HOUR),
        old_row("b", cutover),
    ])]);
    assert_eq!(states(&sync_round(&c.default_db, &url, &c.registry)), ["notSignedIn", "ok"]);
    let (url, _) = serve(v2_round());
    assert_eq!(states(&sync_round(&c.default_db, &url, &c.registry)), ["notSignedIn", "ok"]);
    let first: serde_json::Value = serde_json::from_slice(
        &std::fs::read(c.preferred.join(sidecar_name(&c.stem))).unwrap(),
    )
    .unwrap();
    set_owner(&c.preferred, crate::agent_storage_windows::TestOwner::Administrators);
    let _restore = RestoreOwner(c.preferred.clone());
    let (url, _) = serve(v2_round());
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "loosened: {status}");
    let history = sibling_history(&c);
    assert_eq!(history["cutoverMs"], first["cutoverMs"], "a loosened dir was not carried over");
    assert_eq!(history["frozen"].to_string(), first["frozen"].to_string());
    assert_eq!(rows_in(&c.sibling.join(format!("usage.{}.json", c.stem))).len(), 4);
    // Cleanup as the doc states: the loosened dir keeps its files, reported.
    assert!(c.preferred.join(format!("usage.{}.json", c.stem)).exists());
    assert!(c.preferred.join(sidecar_name(&c.stem)).exists());
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));

    // A loose live file in a secure preferred dir: never read.
    let c = sibling_case();
    drop(secure::ensure_dir(&c.preferred).unwrap());
    let loose = c.preferred.join(format!("usage.{}.json", c.stem));
    std::fs::write(
        &loose,
        old_live_bytes(&[old_row("a", month_start_ms(1) + HOUR), old_row("b", cutover)]),
    )
    .unwrap();
    drop(secure::ensure_dir(&c.sibling).unwrap());
    let (url, _) = serve(v2_round());
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "loose file: {status}");
    let history = sibling_history(&c);
    assert!(history["frozen"].as_array().unwrap().is_empty(), "a loose file was read");
    assert!(loose.exists());
    assert_eq!(status["cleanupFailedProfiles"], serde_json::json!([PROFILE_A]));
}

/// Gives a dir back to the current user when dropped, so a failed assert
/// never leaves an Administrators-owned dir in a TempDir.
struct RestoreOwner(PathBuf);

impl Drop for RestoreOwner {
    fn drop(&mut self) {
        let _ = crate::agent_storage_windows::set_owner_for_test(
            &self.0,
            crate::agent_storage_windows::TestOwner::CurrentUser,
        );
    }
}

/// Review round 2, item 2: `secure::peek_dir` answers what
/// `secure::resolve_dir` then returns, in each state the rule knows, and
/// creates nothing: nothing there (preferred), a secure preferred dir
/// (preferred), a loose preferred dir (the sibling), an existing sibling
/// (the sibling), a sibling beside a secure preferred dir (the sibling:
/// sticky), a sibling failing the contract (the error). Mutations: in
/// `peek_secure_storage_directory`, the security-failure arm ->
/// `Ok(preferred.to_path_buf())`; open the preferred dir with
/// `ensure_secure_storage_directory` (creates it); delete the first
/// `match open_existing_secure_storage_directory(&fallback) {…}` block.
#[test]
fn peeking_the_commit_dir_matches_resolution_and_creates_nothing() {
    let tmp = TempDir::new();
    let base = tmp.path().join("p");
    drop(secure::ensure_dir(&base).unwrap());
    let sibling_of = |dir: &Path| {
        let mut name = dir.as_os_str().to_os_string();
        name.push(".secure");
        PathBuf::from(name)
    };
    // Nothing there: the preferred dir, and nothing created.
    let a = base.join("a");
    assert_eq!(secure::peek_dir(&a).unwrap(), a);
    assert!(!a.exists() && !sibling_of(&a).exists(), "the peek created a dir");
    assert_eq!(secure::resolve_dir(&a).unwrap(), a);
    // A secure preferred dir.
    assert_eq!(secure::peek_dir(&a).unwrap(), a);
    // A loose preferred dir: the sibling, not created by the peek.
    let c = base.join("c");
    std::fs::create_dir(&c).unwrap();
    assert_eq!(secure::peek_dir(&c).unwrap(), sibling_of(&c));
    assert!(!sibling_of(&c).exists(), "the peek created the sibling");
    assert_eq!(secure::resolve_dir(&c).unwrap(), sibling_of(&c));
    // An existing sibling.
    assert_eq!(secure::peek_dir(&c).unwrap(), sibling_of(&c));
    // Sticky: the sibling wins over a secure preferred dir.
    let e = base.join("e");
    drop(secure::ensure_dir(&e).unwrap());
    drop(secure::ensure_dir(&sibling_of(&e)).unwrap());
    assert_eq!(secure::peek_dir(&e).unwrap(), sibling_of(&e));
    assert_eq!(secure::resolve_dir(&e).unwrap(), sibling_of(&e));
    // A sibling that fails the contract: the error, as resolution returns it.
    let g = base.join("g");
    std::fs::create_dir(sibling_of(&g)).unwrap();
    assert!(secure::peek_dir(&g).is_err(), "a loose sibling was not an error");
    assert!(secure::resolve_dir(&g).is_err());
    assert!(!g.exists(), "the peek created the preferred dir");
}

/// Review round 2, item 3: a V2 commit whose live file would exceed the
/// stored-file cap writes nothing (`write_failed`), so no later round meets
/// a file it must refuse; under the cap it commits. Mutation: delete the
/// `if payload.len() as u64 > stored_cap() || … { return Err(failed()); }`
/// check in `commit_v2`.
#[test]
fn a_v2_commit_over_the_cap_writes_nothing() {
    let many = |count: usize| {
        (0..count)
            .map(|i| item(&format!("n{i}"), month_start_ms(0) + i as i64))
            .collect::<Vec<_>>()
    };
    let f = fixture();
    let (outcome, seen) =
        with_stored_cap(2048, || run_v2(&f, v2_month_replies(many(20)), v2_limits(true)));
    assert_eq!(outcome, stop(State::Error, Some("write_failed")));
    assert_eq!(v2_requests(&seen).len(), 12, "control: the walk ran");
    assert!(!expected_file(&f.dir).exists(), "an oversized live file was written");
    assert!(!sidecar_path(&f.dir).exists());
    // Control: the same rows under a larger cap commit.
    let (outcome, _) =
        with_stored_cap(1 << 20, || run_v2(&f, v2_month_replies(many(20)), v2_limits(true)));
    assert_eq!(outcome, ok(20));
}

/// Review round 2, item 4: in a candidate that passes the contract, a
/// readable live file is carried over even when its sidecar cannot be read
/// (frozen whole, as a stale sidecar is), so cleanup never deletes the only
/// copy of the old rows. Mutation: in `v2_source`, `let other_stored =
/// read_history(other, stem).ok().flatten();` -> `let Ok(other_stored) =
/// read_history(other, stem) else { continue; };`.
#[test]
fn a_readable_live_file_is_carried_over_beside_an_unreadable_sidecar() {
    let _guard = lock();
    let c = sibling_case();
    let newest = month_start_ms(1) + 2 * HOUR;
    let old = [old_row("a", month_start_ms(1) + HOUR), old_row("b", newest)];
    write_secure(&c.preferred.join(format!("usage.{}.json", c.stem)), &old_live_bytes(&old));
    std::fs::create_dir(c.preferred.join(sidecar_name(&c.stem))).unwrap();
    drop(secure::ensure_dir(&c.sibling).unwrap());
    let mut replies = vec![
        old_migrated(),
        v2_page(&[as_other("v-current", month_start_ms(0))], None),
        v2_page(&[as_other("v-after", newest + HOUR)], None),
    ];
    replies.extend((0..10).map(|_| v2_empty()));
    let (url, _) = serve(replies);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "ok"], "{status}");
    let history = sibling_history(&c);
    assert_eq!(history["cutoverMs"], newest, "the old rows were not carried over");
    assert_eq!(history["frozen"].as_array().unwrap().len(), 2);
    assert_eq!(rows_in(&c.sibling.join(format!("usage.{}.json", c.stem))).len(), 4);
}

// --- Billing V2, Windows review round 3 -----------------------------------------

/// Review round 3, item 5: when the commit dir cannot be resolved (an
/// added profile's sibling fails the contract), the old walk still runs and
/// reports its own outcome (a 401 is `expired`), and an old success fails at
/// its commit (`write_failed`) after its request, as before Billing V2; only
/// a V2 round stops before its requests. Mutation: in `run`, stop on the
/// peek error (`let Ok(planned) = planned_dir(..) else { return
/// stopped(Stop::error("write_failed")); };`).
#[test]
fn an_unresolvable_commit_dir_does_not_stop_the_old_walk() {
    let _guard = lock();
    let c = sibling_case();
    std::fs::create_dir(&c.sibling).unwrap();
    let (url, seen) = serve(vec![http("401 Unauthorized", "application/json", "{}")]);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "expired"], "{status}");
    assert_eq!(seen.lock().unwrap().len(), 1);
    let (url, seen) = serve(vec![old_page(&[old_row("a", month_start_ms(1) + HOUR)])]);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "error:write_failed"], "{status}");
    assert_eq!(seen.lock().unwrap().len(), 1, "the old walk did not run");
    let (url, seen) = serve(vec![old_migrated(), v2_empty()]);
    let status = sync_round(&c.default_db, &url, &c.registry);
    assert_eq!(states(&status), ["notSignedIn", "error:write_failed"], "{status}");
    let seen = seen.lock().unwrap().clone();
    assert!(v2_requests(&seen).is_empty(), "V2 ran without a commit dir");
    assert_eq!(seen.len(), 1, "the old walk's own request was not made");
    assert!(seen[0].head.starts_with("POST /api/usage "), "{}", seen[0].head);
}

/// Review round 3, item 6: the sidecar alone over the cap (no new V2 rows,
/// so the payload is the old rows and the sidecar is those rows plus its
/// cutover, months and digest) also writes nothing. Mutation: in
/// `commit_v2`, drop `|| sidecar.len() as u64 > stored_cap()`.
#[test]
fn a_v2_commit_whose_sidecar_alone_is_over_the_cap_writes_nothing() {
    let f = fixture();
    seed_by_old_walk(
        &f,
        &[old_row("a", month_start_ms(1) + HOUR), old_row("b", v2_cutover())],
    );
    let live = std::fs::read(expected_file(&f.dir)).unwrap();
    let mut replies = vec![old_migrated()];
    replies.extend((0..12).map(|_| v2_empty()));
    // The payload re-serialises these rows (≈ the live file); the sidecar
    // adds about 90 bytes (cutover, two months, a 64-hex digest).
    let cap = live.len() as u64 + 20;
    // The payload alone is under the cap: the commit's payload is these
    // rows re-serialised, exactly as `commit_v2` builds it.
    let rows = serde_json::from_slice::<Page>(&live).unwrap().usage_events_display;
    let payload = serde_json::to_vec(&UsageFile {
        total_usage_events_count: rows.len(),
        usage_events_display: &rows,
    })
    .unwrap();
    assert!(payload.len() as u64 <= cap, "fixture: the payload half would trip");
    let (outcome, seen) = with_stored_cap(cap, || run_v2(&f, replies, v2_limits(false)));
    assert_eq!(outcome, stop(State::Error, Some("write_failed")));
    assert!(!v2_requests(&seen).is_empty(), "control: the walk ran");
    assert_eq!(std::fs::read(expected_file(&f.dir)).unwrap(), live);
    assert!(!sidecar_path(&f.dir).exists(), "an oversized sidecar was written");
}

// --- Billing V2, S7 regressions (verifier on d461ec7c) --------------------------

/// S7 (#559): the walk's one other id is pinned for the whole walk, and the
/// cookie's own id in between never releases the pin: other id A, then a
/// cookie-id item, then another id B on a later page or in a later window
/// is refused with nothing written (F1). Every item is checked, written or
/// not: a `grok-bot` item (never written, D3) carrying a `team` principal or
/// a second other id, and a `hasTokenMeters: false` item (never written)
/// carrying a `team` principal, are refused (F2). Mutations: in `walk`,
/// `Owner::Cookie => true` -> `Owner::Cookie => { other = None; true }`
/// (Vc2); run the S7 loop only over items whose `product` is `cursor` (Vc1).
#[test]
fn v2_the_principal_pin_holds_across_the_cookie_id_and_every_item_is_checked() {
    let _guard = lock();
    let as_user = |id: &str, ms: i64, user: &str| {
        let mut item = item(id, ms);
        item["principal"]["id"] = user.into();
        item
    };
    let unwritten = |id: &str, change: &dyn Fn(&mut serde_json::Value)| {
        let mut item = item(id, month_start_ms(0) + 2);
        change(&mut item);
        item
    };
    let grok = |i: &mut serde_json::Value| i["product"] = "grok-bot".into();
    let meterless = |i: &mut serde_json::Value| i["hasTokenMeters"] = false.into();
    let team = |i: &mut serde_json::Value| i["principal"]["type"] = "team".into();
    let cases: Vec<(&str, Vec<Reply>)> = vec![
        (
            "A, cookie, B on later pages",
            vec![
                v2_page(&[as_user("a", month_start_ms(0), OTHER_USER)], Some("n1")),
                v2_page(&[item("c", month_start_ms(0) + 1)], Some("n2")),
                v2_page(&[as_user("b", month_start_ms(0) + 2, THIRD_USER)], None),
            ],
        ),
        (
            "A, cookie, B in a later window",
            vec![
                v2_page(&[as_user("a", month_start_ms(0), OTHER_USER)], Some("n1")),
                v2_page(&[item("c", month_start_ms(0) + 1)], None),
                v2_page(&[as_user("b", month_start_ms(1) + HOUR, THIRD_USER)], None),
            ],
        ),
        (
            "a grok-bot item on a team principal",
            vec![v2_page(
                &[
                    item("fine", month_start_ms(0)),
                    unwritten("g", &|i| {
                        grok(i);
                        team(i);
                        i["principal"]["id"] = OTHER_USER.into();
                    }),
                ],
                None,
            )],
        ),
        (
            "a grok-bot item on a second other id",
            vec![v2_page(
                &[
                    as_user("a", month_start_ms(0), OTHER_USER),
                    unwritten("g", &|i| {
                        grok(i);
                        i["principal"]["id"] = THIRD_USER.into();
                    }),
                ],
                None,
            )],
        ),
        (
            "a meterless item on a team principal",
            vec![v2_page(
                &[
                    item("fine", month_start_ms(0)),
                    unwritten("m", &|i| {
                        meterless(i);
                        team(i);
                        i["principal"]["id"] = OTHER_USER.into();
                    }),
                ],
                None,
            )],
        ),
    ];
    for (case, pages) in cases {
        let f = fixture();
        let mut replies = vec![old_migrated()];
        replies.extend(pages);
        replies.extend((0..12).map(|_| v2_empty()));
        let (url, _) = serve(replies);
        let status = sync_status(&f, &url, &registry(true, &f.dir));
        assert_eq!(status["state"], "error", "{case}: {status}");
        assert_eq!(status["reason"], "billing_v2_principal_mismatch", "{case}");
        assert!(!expected_file(&f.dir).exists(), "{case}");
        assert!(!sidecar_path(&f.dir).exists(), "{case}");
    }
}
