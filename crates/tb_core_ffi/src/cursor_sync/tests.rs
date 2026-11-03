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
        max_body_bytes: 1024 * 1024,
    }
}

fn stem(user: &str) -> Option<String> {
    crate::agent_account_scope::keyed_digest_hex(&KEY, FILE_NAME_DOMAIN, user.as_bytes()).ok()
}

fn expected_file(dir: &Path) -> PathBuf {
    dir.join(format!("usage.{}.json", stem(USER).unwrap()))
}

/// The test target: a local server, the fixed key, the real drive-type
/// query (fixtures live on the system drive, a fixed one).
fn target<'a>(db: &'a Path, url: &'a str) -> Target<'a> {
    Target {
        db_path: db,
        url,
        https_only: false,
        file_stem: &stem,
        drive_type: &cursor_desktop::drive_type,
    }
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
    crate::RUNTIME.block_on(run(
        &target(db, url),
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
        (
            json_ok(r#"{"totalUsageEventsCount":1}"#),
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
    sync_with(true, &target(&f.db, url), &source, Some(limits())).0
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
        vec![json_ok(&format!(r#"{{"owningUser":"{OWNING_CANARY}"}}"#))],
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
    let target = Target {
        drive_type,
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
