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
use std::io::{Read as _, Write as _};
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
        let path = std::env::temp_dir().join(format!(
            "tokenbar-cursor-sync-{}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos(),
            NEXT.fetch_add(1, Ordering::Relaxed)
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

fn run_with(
    db: &Path,
    dir: &Path,
    url: &str,
    limits: Limits,
    enabled: &dyn Fn() -> bool,
) -> Outcome {
    let target = Target {
        db_path: db,
        url,
        https_only: false,
        file_stem: &stem,
    };
    crate::RUNTIME.block_on(run(&target, dir, limits, enabled))
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
    Outcome {
        stop: Stop::new(state, reason),
        events: 0,
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
    assert_eq!(
        outcome,
        Outcome {
            stop: Stop::new(State::Ok, None),
            events: 3
        }
    );

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
        assert_eq!(
            run_with(&db, &tmp.path().join("sync"), &url, limits(), &|| true),
            stop(State::Error, Some("account_mismatch"))
        );
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
        cli_takeover_confirmed: false,
    })
}

fn sync_status(f: &Fixture, url: &str, registry: &RwLock<Config>) -> serde_json::Value {
    let target = Target {
        db_path: &f.db,
        url,
        https_only: false,
        file_stem: &stem,
    };
    let source = || registry.read().unwrap().clone();
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
            cli_takeover_confirmed: true
        }
    );
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
    assert!(names(root.path()).is_empty(), "{:?}", names(root.path()));
}

#[test]
fn disable_deletes_only_syrtis_usage_files() {
    let root = DataRoot::new();
    set(serde_json::json!({"enabled": true})).unwrap();
    let dir = root.sync_dir();
    // Not `usage.<64 hex>.json`: still a Syrtis name, so it is deleted.
    let synced = dir.join("usage.not-complete.json");
    let temp = dir.join(format!("{TEMP_FILE_PREFIX}1-1"));
    let unrelated = dir.join("keep.txt");
    for path in [&synced, &temp, &unrelated] {
        write_secure(path, b"x");
    }
    let off = set(serde_json::json!({"enabled": false})).unwrap();
    assert_eq!(off["removedFiles"], 2);
    assert!(off["dir"].is_null());
    assert!(!synced.exists() && !temp.exists());
    assert!(
        unrelated.exists(),
        "disable must delete only Syrtis usage files"
    );
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

fn write_complete_file(dir: &Path) {
    write_secure(
        &dir.join(format!("usage.{}.json", "c".repeat(64))),
        br#"{"totalUsageEventsCount":0,"usageEventsDisplay":[]}"#,
    );
}

fn claude_settings() -> tokscale_core::scanner::ScannerSettings {
    tokscale_core::scanner::ScannerSettings {
        extra_scan_paths: [("claude".to_string(), vec![PathBuf::from(r"C:\claude\extra")])].into(),
        ..Default::default()
    }
}

#[test]
fn takeover_matrix() {
    for bits in 0..16u8 {
        let (enabled, complete, cli_files, confirmed) =
            (bits & 1 != 0, bits & 2 != 0, bits & 4 != 0, bits & 8 != 0);
        let home = TempDir::new();
        let dir = home.path().join("sync");
        let cli = cli_root(home.path());
        std::fs::create_dir_all(&cli).unwrap();
        drop(secure::ensure_dir(&dir).unwrap());
        if complete {
            write_complete_file(&dir);
        }
        if cli_files {
            std::fs::write(cli.join("usage.csv"), "x").unwrap();
        }
        let config = Config {
            enabled,
            dir: Some(dir.clone()),
            cli_takeover_confirmed: confirmed,
        };
        let mut settings = claude_settings();
        apply_takeover(&mut settings, &takeover(&config, Some(home.path())));

        let on = enabled && complete && (!cli_files || confirmed);
        let label =
            format!("enabled={enabled} complete={complete} cli={cli_files} confirmed={confirmed}");
        assert_eq!(
            settings.extra_scan_paths["claude"],
            [PathBuf::from(r"C:\claude\extra")],
            "{label}"
        );
        assert_eq!(
            settings.extra_scan_paths.get("cursor").cloned(),
            on.then(|| vec![dir.clone()]),
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

#[test]
fn only_an_ok_walk_blocked_by_d6_reads_cli_present() {
    let ok = serde_json::json!({"state": "ok", "events": 2, "lastSuccessMs": 1});
    assert_eq!(status_for(ok.clone(), &Takeover::Blocked)["state"], "cliPresent");
    assert_eq!(status_for(ok.clone(), &Takeover::Off)["state"], "ok");
    let partial = serde_json::json!({"state": "partial", "events": 0});
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
    let target = Target {
        db_path: db,
        url,
        https_only: false,
        file_stem: &stem,
    };
    let limits = Limits {
        page_size: 500,
        ..limits()
    };
    crate::cursor_sync_then_recheck(|| sync_with(true, &target, &config, Some(limits)))
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

