//! Issue #223: an OpenCode database outside opencode's default data dir
//! (`OPENCODE_DB`, or `scanner.opencodeDbPaths` in the tokscale settings file)
//! must reach every source-scanning FFI entry point, because all of them take
//! the process context that `capture_process_context` builds.
//!
//! Each scenario runs in an env-cleared child (see `roots_acceptance`), so the
//! lazy process capture reads the fixture's environment and config dir. The
//! fixture DB sits in `<root>/external`, away from the child's XDG data dir;
//! the child's default OpenCode root is empty, so the numbers are exact.

use std::ffi::{c_char, CStr, CString};
use std::path::{Path, PathBuf};

use crate::roots_acceptance::{child_root, run_in_child_at};

const MODEL: &str = "oc-fixture-model";
const OUTPUT: i64 = 4321;
const WINDOW_FROM: i64 = 1_767_225_600_000; // 2026-01-01T00:00:00Z
const WINDOW_UNTIL: i64 = 1_767_312_000_000; // 2026-01-02T00:00:00Z

fn write_external_db(root: &Path) -> PathBuf {
    write_db_in(&root.join("external"))
}

fn write_db_in(dir: &Path) -> PathBuf {
    std::fs::create_dir_all(dir).unwrap();
    let db = dir.join("opencode.db");
    let conn = rusqlite::Connection::open(&db).unwrap();
    conn.execute_batch(
        "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, data TEXT NOT NULL);
         CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT NOT NULL);",
    )
    .unwrap();
    conn.execute("INSERT INTO session VALUES ('s1', '/work')", []).unwrap();
    let data = format!(
        r#"{{"id":"m1","role":"assistant","modelID":"{MODEL}","providerID":"anthropic","mode":"build","cost":0.0,
        "tokens":{{"input":100,"output":{OUTPUT},"reasoning":0,"cache":{{"read":0,"write":0}}}},
        "time":{{"created":1767225600000.0,"completed":1767225601000.0}}}}"#
    );
    conn.execute("INSERT INTO message VALUES ('m1', 's1', ?1)", [data]).unwrap();
    db
}

unsafe fn take(p: *mut c_char) -> serde_json::Value {
    let text = unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned();
    unsafe { crate::tb_free(p) };
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{e}: {text}"))
}

/// Every payload an entry point returns, keyed by entry name.
fn all_entries() -> Vec<(&'static str, serde_json::Value)> {
    let nul = std::ptr::null();
    let opencode = CString::new("opencode").unwrap();
    unsafe {
        vec![
            ("tb_graph", take(crate::tb_graph(nul))),
            ("tb_graph_local_first", take(crate::tb_graph_local_first(nul))),
            ("tb_refresh_graph", take(crate::tb_refresh_graph(nul))),
            ("tb_model_report", take(crate::tb_model_report(nul))),
            ("tb_hourly_report", take(crate::tb_hourly_report(nul, opencode.as_ptr()))),
            ("tb_agents_report", take(crate::tb_agents_report(nul, opencode.as_ptr()))),
            ("tb_window_usage", take(crate::tb_window_usage(nul, WINDOW_FROM, WINDOW_UNTIL))),
        ]
    }
}

/// Whether any JSON number node equals `n`.
fn has_int(value: &serde_json::Value, n: i64) -> bool {
    match value {
        serde_json::Value::Number(x) => x.as_i64() == Some(n),
        serde_json::Value::Array(items) => items.iter().any(|v| has_int(v, n)),
        serde_json::Value::Object(map) => map.values().any(|v| has_int(v, n)),
        _ => false,
    }
}

/// Entry payloads that scanned the DB: they name the fixture model (agent for
/// the Agents report) and carry its output tokens exactly once -- the fixture
/// output appears, and twice that (a double count) does not.
fn entries_seeing_the_db() -> Vec<&'static str> {
    all_entries()
        .into_iter()
        .inspect(|(name, payload)| assert_eq!(payload["ok"], true, "{name}: {payload}"))
        .filter(|(name, payload)| {
            // The Agents report buckets by agent (OpenCode's `mode`), not model.
            let marker = if *name == "tb_agents_report" { "Build" } else { MODEL };
            assert!(!has_int(payload, 2 * OUTPUT), "{name} double counted: {payload}");
            payload.to_string().contains(marker) && has_int(payload, OUTPUT)
        })
        .map(|(name, _)| name)
        .collect()
}

const EVERY_ENTRY: [&str; 7] = [
    "tb_graph",
    "tb_graph_local_first",
    "tb_refresh_graph",
    "tb_model_report",
    "tb_hourly_report",
    "tb_agents_report",
    "tb_window_usage",
];

fn default_identity() -> String {
    tokscale_core::ResolvedLocalSourceContext::capture(
        crate::user_home_dir(),
        true,
        tokscale_core::ScannerSettings::default(),
    )
    .unwrap()
    .identity_string()
}

fn context_id() -> String {
    let payload = unsafe { take(crate::tb_source_context_id()) };
    payload["data"].as_str().unwrap().to_string()
}

#[test]
fn opencode_db_env_reaches_every_entry() {
    let Some(root) = child_root() else {
        return run_in_child_at("opencode_db_acceptance::opencode_db_env_reaches_every_entry", "oc-env");
    };
    let db = write_external_db(&root);
    // Single-threaded child, set before the first (lazy) capture.
    std::env::set_var("OPENCODE_DB", &db);
    assert_ne!(context_id(), default_identity(), "the DB path is part of the identity");
    assert_eq!(entries_seeing_the_db(), EVERY_ENTRY);
}

#[test]
fn settings_file_opencode_db_paths_reach_every_entry() {
    let Some(root) = child_root() else {
        return run_in_child_at(
            "opencode_db_acceptance::settings_file_opencode_db_paths_reach_every_entry",
            "oc-file",
        );
    };
    let db = write_external_db(&root);
    write_settings(&root, serde_json::json!([db]));
    assert_ne!(context_id(), default_identity());
    assert_eq!(entries_seeing_the_db(), EVERY_ENTRY);
}

/// Control: the DB exists but nothing points at it. Behavior is exactly the
/// pre-fix default, and so is the context identity.
#[test]
fn without_configuration_the_external_db_stays_unscanned() {
    let Some(root) = child_root() else {
        return run_in_child_at(
            "opencode_db_acceptance::without_configuration_the_external_db_stays_unscanned",
            "oc-none",
        );
    };
    write_external_db(&root);
    assert_eq!(context_id(), default_identity());
    assert_eq!(entries_seeing_the_db(), Vec::<&str>::new());
}

/// The same DB named by `OPENCODE_DB` and by settings.json is scanned once.
#[test]
fn the_same_db_from_env_and_file_counts_once() {
    let Some(root) = child_root() else {
        return run_in_child_at(
            "opencode_db_acceptance::the_same_db_from_env_and_file_counts_once",
            "oc-same",
        );
    };
    let db = write_external_db(&root);
    write_settings(&root, serde_json::json!([db]));
    std::env::set_var("OPENCODE_DB", &db);
    assert_eq!(entries_seeing_the_db(), EVERY_ENTRY);
}

/// settings.json edited after the first capture is picked up by the next
/// re-capture, which a root setter performs (here its test seam).
#[test]
fn a_root_setter_recapture_picks_up_a_changed_settings_file() {
    let Some(root) = child_root() else {
        return run_in_child_at(
            "opencode_db_acceptance::a_root_setter_recapture_picks_up_a_changed_settings_file",
            "oc-recapture",
        );
    };
    let db = write_external_db(&root);
    assert_eq!(entries_seeing_the_db(), Vec::<&str>::new());
    write_settings(&root, serde_json::json!([db]));
    assert_eq!(entries_seeing_the_db(), Vec::<&str>::new(), "no re-capture yet");
    crate::apply_scan_roots_for_test(Default::default()).unwrap();
    assert_eq!(entries_seeing_the_db(), EVERY_ENTRY);
}

/// A configured path the engine would silently drop is reported by tb_probe.
#[test]
fn tb_probe_reports_rejected_paths() {
    let Some(root) = child_root() else {
        return run_in_child_at("opencode_db_acceptance::tb_probe_reports_rejected_paths", "oc-probe");
    };
    let good = write_external_db(&root);
    let bad = root.join("external").join("mydb.db");
    write_settings(&root, serde_json::json!([bad, good]));
    let probe = unsafe { take(crate::tb_probe()) };
    assert_eq!(probe["ok"], true, "{probe}");
    let report = &probe["opencodeDb"];
    assert_eq!(report["settingsFile"], "ok");
    assert_eq!(report["handedToEngine"], 2);
    assert_eq!(report["issues"][0]["reason"], "badFileName");
    assert_eq!(report["issues"][0]["index"], 0);
    assert!(!report.to_string().contains("mydb"), "a path must not cross the ABI");
}

fn write_settings(root: &Path, paths: serde_json::Value) {
    let config = root.join("tokscale-config");
    std::fs::create_dir_all(&config).unwrap();
    let settings = serde_json::json!({"scanner": {"opencodeDbPaths": paths}});
    std::fs::write(config.join("settings.json"), settings.to_string()).unwrap();
}

/// A relative OPENCODE_DB is resolved against the data dir the engine scans
/// for its default OpenCode DB. The DB sits in a subdirectory, which the
/// engine's own discovery (flat) does not list, so only the join reaches it.
#[test]
fn a_relative_opencode_db_resolves_against_xdg_data_home() {
    let Some(root) = child_root() else {
        return run_in_child_at(
            "opencode_db_acceptance::a_relative_opencode_db_resolves_against_xdg_data_home",
            "oc-rel-xdg",
        );
    };
    // The child harness sets XDG_DATA_HOME=<root>/data.
    write_db_in(&root.join("data").join("opencode").join("sub"));
    std::env::set_var("OPENCODE_DB", Path::new("sub").join("opencode.db"));
    assert_eq!(entries_seeing_the_db(), EVERY_ENTRY);
}

#[test]
fn a_relative_opencode_db_resolves_against_home_without_xdg_data_home() {
    let Some(root) = child_root() else {
        return run_in_child_at(
            "opencode_db_acceptance::a_relative_opencode_db_resolves_against_home_without_xdg_data_home",
            "oc-rel-home",
        );
    };
    std::env::remove_var("XDG_DATA_HOME");
    write_db_in(&root.join(".local").join("share").join("opencode").join("sub"));
    std::env::set_var("OPENCODE_DB", Path::new("sub").join("opencode.db"));
    assert_eq!(entries_seeing_the_db(), EVERY_ENTRY);
}
