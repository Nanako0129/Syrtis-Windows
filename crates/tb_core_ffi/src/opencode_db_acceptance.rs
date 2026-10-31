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
    let dir = root.join("external");
    std::fs::create_dir_all(&dir).unwrap();
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

/// Entry payloads that mention the fixture model, i.e. that scanned the DB.
fn entries_seeing_the_db() -> Vec<&'static str> {
    all_entries()
        .into_iter()
        .inspect(|(name, payload)| assert_eq!(payload["ok"], true, "{name}: {payload}"))
        .filter(|(name, payload)| {
            // The Agents report buckets by agent (OpenCode's `mode`), not model.
            let marker = if *name == "tb_agents_report" { "Build" } else { MODEL };
            payload.to_string().contains(marker)
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
    let config = root.join("tokscale-config");
    std::fs::create_dir_all(&config).unwrap();
    let settings = serde_json::json!({"scanner": {"opencodeDbPaths": [db]}});
    std::fs::write(config.join("settings.json"), settings.to_string()).unwrap();
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
