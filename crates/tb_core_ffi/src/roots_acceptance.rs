//! W4b acceptance: the root setters, the re-captured process context and the
//! generation gate, driven through the production entries (`process()`, the
//! two setters, `tb_graph`, `tb_window_usage`, the tail tick).
//!
//! Each scenario runs in a child test process with the environment cleared
//! and `HOME`, `TOKSCALE_CONFIG_DIR` and the XDG roots pointed at a fixture,
//! so the lazy process capture happens fresh in that child and its source
//! cache lives under the fixture. On macOS the `dirs` crate derives the
//! platform roots from `$HOME`, so the child reads only the fixture. On Windows
//! the platform roots are Known Folders that `env_clear` does not move, so a
//! Windows child also scans the real `%APPDATA%` / `%LOCALAPPDATA%` client
//! roots; assertions are therefore on the Claude lane or on deltas against the
//! child's own baseline. Scenarios that register a real directory need an
//! absolute drive path (the registries' rule), so they run on Windows only.

use std::ffi::{c_char, CStr, CString};
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::time::Duration;

const CHILD_ROOT: &str = "TOKENBAR_ROOTS_ACCEPTANCE_ROOT";

fn fresh_root(label: &str) -> PathBuf {
    let root = std::env::temp_dir().join(format!(
        "tokenbar-roots-{label}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(root.join("data")).unwrap();
    std::fs::create_dir_all(root.join("tmp")).unwrap();
    root
}

/// Run `test` (this module's test of that name) in an env-cleared child whose
/// home is a fresh fixture root, and fail if the child failed.
fn run_in_child(test: &str, label: &str) {
    run_in_child_at(&format!("roots_acceptance::{test}"), label);
}

/// [`run_in_child`] for a test anywhere in the crate, by its full path.
pub(crate) fn run_in_child_at(test_path: &str, label: &str) {
    let test = test_path;
    let root = fresh_root(label);
    let mut command = std::process::Command::new(std::env::current_exe().unwrap());
    command
        .arg(test_path)
        .arg("--exact")
        .arg("--nocapture")
        .env_clear()
        .env(CHILD_ROOT, &root)
        .env("HOME", &root)
        .env("TMPDIR", root.join("tmp"))
        .env("TOKSCALE_CONFIG_DIR", root.join("tokscale-config"))
        .env("XDG_CONFIG_HOME", root.join("config"))
        .env("XDG_DATA_HOME", root.join("data"))
        .env("TOKSCALE_PRICING_CACHE_ONLY", "1");
    // Windows processes need these to start and to resolve temp paths.
    for key in ["SystemRoot", "SYSTEMROOT", "windir", "TEMP", "TMP"] {
        if let Some(value) = std::env::var_os(key) {
            command.env(key, value);
        }
    }
    let output = command.output().unwrap();
    let _ = std::fs::remove_dir_all(&root);
    let stdout = String::from_utf8_lossy(&output.stdout);
    assert!(
        output.status.success(),
        "child {test} failed:\n{stdout}\n{}",
        String::from_utf8_lossy(&output.stderr)
    );
    // A filter that matched nothing would also exit 0: require that the child
    // ran exactly this one test.
    assert!(
        stdout.contains("test result: ok. 1 passed"),
        "child {test} did not run its body:\n{stdout}"
    );
}

pub(crate) fn child_root() -> Option<PathBuf> {
    std::env::var_os(CHILD_ROOT).map(PathBuf::from)
}

unsafe fn take(p: *mut c_char) -> serde_json::Value {
    let text = unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned();
    unsafe { crate::tb_free(p) };
    serde_json::from_str(&text).unwrap_or_else(|error| panic!("{error}: {text}"))
}

fn call_set_scan(json: &str) -> serde_json::Value {
    let raw = CString::new(json).unwrap();
    unsafe { take(crate::tb_set_extra_scan_paths(raw.as_ptr())) }
}

fn call_set_dirs(json: &str) -> serde_json::Value {
    let raw = CString::new(json).unwrap();
    unsafe { take(crate::tb_set_claude_config_dirs(raw.as_ptr())) }
}

fn call_window(account: Option<&str>, from_ms: i64, until_ms: i64) -> serde_json::Value {
    let account = account.map(|value| CString::new(value).unwrap());
    let pointer = account.as_ref().map_or(std::ptr::null(), |value| value.as_ptr());
    unsafe { take(crate::tb_window_usage(pointer, from_ms, until_ms)) }
}

fn call_graph() -> serde_json::Value {
    unsafe { take(crate::tb_graph(std::ptr::null())) }
}

fn call_context_id() -> serde_json::Value {
    unsafe { take(crate::tb_source_context_id()) }
}

fn generation() -> u64 {
    crate::ROOT_GENERATION.load(Ordering::SeqCst)
}

const WINDOW_FROM: i64 = 1_767_225_600_000; // 2026-01-01T00:00:00Z
const WINDOW_UNTIL: i64 = 1_767_312_000_000; // 2026-01-02T00:00:00Z

/// One Claude assistant turn with a distinct id, so a root's contribution is
/// identifiable by its output tokens alone (macOS fixture shape).
fn write_session(root: &Path, id: &str, output: i64) {
    let dir = root.join("projects").join("proj");
    std::fs::create_dir_all(&dir).unwrap();
    std::fs::write(
        dir.join(format!("{id}.jsonl")),
        format!(
            r#"{{"type":"assistant","timestamp":"2026-01-01T00:00:00.000Z","requestId":"req_{id}","message":{{"id":"msg_{id}","model":"claude-3-5-sonnet","usage":{{"input_tokens":0,"output_tokens":{output}}}}}}}"#
        ),
    )
    .unwrap();
}

fn claude_output(window: &serde_json::Value) -> i64 {
    window["data"]["messages"]
        .as_array()
        .unwrap_or_else(|| panic!("window payload: {window}"))
        .iter()
        .filter(|message| message["client"] == "claude")
        .map(|message| message["output"].as_i64().unwrap_or(0))
        .sum()
}

/// Claude output tokens across the graph's per-day client rows: the Claude
/// lane only, because a Windows child also reads other clients' real data.
fn claude_graph_output(graph: &serde_json::Value) -> i64 {
    graph["data"]["contributions"]
        .as_array()
        .unwrap_or_else(|| panic!("graph payload: {graph}"))
        .iter()
        .flat_map(|day| day["clients"].as_array().cloned().unwrap_or_default())
        .filter(|row| row["client"] == "claude")
        .map(|row| row["tokens"]["output"].as_i64().unwrap_or(0))
        .sum()
}

/// Output on the Claude lane including its `.cc-mirror` variants: the engine
/// labels a variant's messages `cc-mirror/<variant>`, not `claude`.
fn claude_lane_output(window: &serde_json::Value) -> i64 {
    window["data"]["messages"]
        .as_array()
        .unwrap_or_else(|| panic!("window payload: {window}"))
        .iter()
        .filter(|message| is_claude_lane(&message["client"]))
        .map(|message| message["output"].as_i64().unwrap_or(0))
        .sum()
}

fn claude_lane_graph_output(graph: &serde_json::Value) -> i64 {
    graph["data"]["contributions"]
        .as_array()
        .unwrap_or_else(|| panic!("graph payload: {graph}"))
        .iter()
        .flat_map(|day| day["clients"].as_array().cloned().unwrap_or_default())
        .filter(|row| is_claude_lane(&row["client"]))
        .map(|row| row["tokens"]["output"].as_i64().unwrap_or(0))
        .sum()
}

fn is_claude_lane(client: &serde_json::Value) -> bool {
    client
        .as_str()
        .is_some_and(|id| id == "claude" || id.starts_with("cc-mirror/"))
}

// ---- 1′ and 9: run everywhere -------------------------------------------

/// 1′ structural: with no registry calls, and again after both registries are
/// set empty, the process context is the plain `ScannerSettings::default()`
/// capture and the primary window scans the process context itself.
#[test]
fn no_roots_keeps_the_process_context() {
    let Some(_) = child_root() else {
        return run_in_child("no_roots_keeps_the_process_context", "no-roots");
    };
    let expected = tokscale_core::ResolvedLocalSourceContext::capture(
        crate::user_home_dir(),
        true,
        tokscale_core::ScannerSettings::default(),
    )
    .unwrap()
    .identity_string();
    let check = || {
        assert_eq!(call_context_id()["data"], expected.as_str());
        let process = crate::LocalSourceContext::process().unwrap();
        let scoped = crate::window_usage::scoped_context_for_test(&process, &None).unwrap();
        assert!(scoped.same_resolved(&process), "no roots: primary must use the process context");
    };
    check();
    assert_eq!(call_set_dirs("[]")["ok"], true);
    assert_eq!(call_set_scan("{}")["ok"], true);
    check();
}

/// 9 (mandatory): setters run before any read, as W4's launch push does; the
/// lazy fill must store the current generation, so the caches still fill.
#[test]
fn setters_before_the_first_read_leave_caches_working() {
    let Some(_) = child_root() else {
        return run_in_child("setters_before_the_first_read_leave_caches_working", "lazy-fill");
    };
    assert!(crate::lock_context_cell().is_none(), "the child must start with an empty cell");
    // A syntactically valid drive path that need not exist: registers on every
    // platform.
    assert_eq!(call_set_dirs(r#"["C:\\tokenbar-acceptance\\work"]"#)["ok"], true);
    assert!(crate::lock_context_cell().is_none(), "config dirs alone leave the cell empty");
    assert_ne!(generation(), 0, "the config-dir setter moved the atomic");
    // The first read now fills the cell lazily. This is the path the rule is
    // about: the scan setter would fill the cell itself and skip it.

    let before = crate::window_usage::scan_count();
    assert_eq!(call_window(None, WINDOW_FROM, WINDOW_UNTIL)["ok"], true);
    assert_eq!(call_window(None, WINDOW_FROM, WINDOW_UNTIL)["ok"], true);
    assert_eq!(crate::window_usage::scan_count(), before + 1, "second window must be a cache hit");

    assert_eq!(call_graph()["ok"], true);
    assert!(
        crate::GRAPH_CACHE.lock().unwrap().contains_key(""),
        "the graph must have been published under the current generation"
    );

    // The launch push's second call, the scan setter, replaces the cell; the
    // caches keep working after it too.
    assert_eq!(call_set_scan("{}")["ok"], true);
    let before = crate::window_usage::scan_count();
    assert_eq!(call_window(None, WINDOW_FROM, WINDOW_UNTIL)["ok"], true);
    assert_eq!(call_window(None, WINDOW_FROM, WINDOW_UNTIL)["ok"], true);
    assert_eq!(crate::window_usage::scan_count(), before + 1);
}

/// Same rule when the very first call is a read and a config-dir setter comes
/// afterwards: the cell's stored generation moves with the atomic.
#[test]
fn a_config_dir_change_after_the_first_read_keeps_the_cell_current() {
    let Some(_) = child_root() else {
        return run_in_child("a_config_dir_change_after_the_first_read_keeps_the_cell_current", "cell-bump");
    };
    let first = crate::LocalSourceContext::process().unwrap();
    assert_eq!(call_set_dirs(r#"["C:\\tokenbar-acceptance\\work"]"#)["ok"], true);
    let after = crate::LocalSourceContext::process().unwrap();
    assert!(!first.is_current(), "the old context must be stale after a root change");
    assert!(after.is_current());
    assert!(after.same_resolved(&first), "config dirs do not re-capture the process context");
}

/// The verifier's scenario, runnable everywhere: with an extra account's root
/// registered, totals include it, its own window returns exactly it (Claude
/// rows only), and the primary window returns exactly the primary's tokens.
/// Roots go in through the setter's commit path (the drive-path rule would
/// refuse a POSIX fixture). On `main` the primary window would be 1,000 here
/// too, because nothing registered a root; before this fix W4b returned 8,000.
#[test]
fn a_registered_root_never_reaches_the_primary_window() {
    let Some(root) = child_root() else {
        return run_in_child("a_registered_root_never_reaches_the_primary_window", "primary-only");
    };
    const PRIMARY: i64 = 1_000;
    const EXTRA: i64 = 7_000;
    write_session(&root.join(".claude"), "primary", PRIMARY);
    let work = root.join("work-d");
    write_session(&work, "d", EXTRA);
    let work_text = work.display().to_string();

    assert_eq!(claude_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)), PRIMARY);
    crate::apply_scan_roots_for_test(std::collections::BTreeMap::from([(
        "claude".to_string(),
        vec![work.join("projects"), work.join("transcripts")],
    )]))
    .unwrap();

    assert_eq!(claude_graph_output(&call_graph()), PRIMARY + EXTRA, "totals include the extra root");
    assert_eq!(
        claude_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)),
        PRIMARY,
        "the primary window must not count the extra account"
    );
    let extra = call_window(Some(&work_text), WINDOW_FROM, WINDOW_UNTIL);
    assert_eq!(claude_output(&extra), EXTRA);
    assert!(extra["data"]["messages"].as_array().unwrap().iter().all(|m| m["client"] == "claude"));

    // The race, made deterministic: a primary request holds the context
    // captured with the root, then a setter clears the roots before the
    // request looks at the registry. The answer is still the primary's only
    // (its publish is dropped as stale; the returned value must not mix).
    let held = crate::LocalSourceContext::process().unwrap();
    crate::apply_scan_roots_for_test(std::collections::BTreeMap::new()).unwrap();
    let raced = crate::window_usage::cached(&held, &None, WINDOW_FROM, WINDOW_UNTIL).unwrap();
    assert_eq!(
        claude_output(&serde_json::json!({ "data": raced })),
        PRIMARY,
        "a context captured with roots must not answer for the primary"
    );
}

/// W4c B-1b: a config directory with NO registered scan root still never
/// reaches the primary window. The engine finds `D/projects` on its own here,
/// through a `.cc-mirror` variant whose `configDir` is D, so only the
/// config-dir half of the primary's exclusion set can keep it out. The control
/// (before D is registered) proves the mirror route really reaches D; totals
/// keep it either way. Config dirs go in through the setter's commit path
/// (`apply_config_dirs_for_test`; the drive-path rule refuses a POSIX
/// fixture), so this runs on the macOS host with exact numbers and on Windows.
#[test]
fn a_config_dir_alone_never_reaches_the_primary_window() {
    let Some(root) = child_root() else {
        return run_in_child("a_config_dir_alone_never_reaches_the_primary_window", "config-dir-only");
    };
    const PRIMARY: i64 = 1_000;
    const EXTRA: i64 = 7_000;
    write_session(&root.join(".claude"), "primary", PRIMARY);
    let dir = root.join("work-d");
    write_session(&dir, "d", EXTRA);
    let variant = root.join(".cc-mirror").join("v1");
    std::fs::create_dir_all(&variant).unwrap();
    std::fs::write(
        variant.join("variant.json"),
        serde_json::json!({ "configDir": dir }).to_string(),
    )
    .unwrap();

    assert_eq!(
        claude_lane_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)),
        PRIMARY + EXTRA,
        "fixture is inert: the mirror route did not reach D"
    );
    crate::apply_config_dirs_for_test(vec![dir.display().to_string()]);
    assert_eq!(
        claude_lane_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)),
        PRIMARY,
        "a configured directory reached the primary window through the mirror route"
    );
    assert_eq!(
        claude_lane_graph_output(&call_graph()),
        PRIMARY + EXTRA,
        "totals keep every account"
    );
}

/// Register `dirs` as extra accounts in both registries (config dir and its
/// `projects` / `transcripts` roots) through the setters' commit paths.
fn register_accounts(dirs: &[&Path]) {
    crate::apply_config_dirs_for_test(dirs.iter().map(|dir| dir.display().to_string()).collect());
    crate::apply_scan_roots_for_test(std::collections::BTreeMap::from([(
        "claude".to_string(),
        dirs.iter()
            .flat_map(|dir| [dir.join("projects"), dir.join("transcripts")])
            .collect(),
    )]))
    .unwrap();
}

/// #175 deferred item: an account nested at `<outer>\.claude` was counted in
/// both windows. The outer window is captured with the outer directory as
/// home, and the engine scans `<home>/.claude/projects` (Claude's declared
/// root) on its own, which is the inner account. Each account's window is its
/// own scope minus every other account. Totals keep every account.
#[test]
fn a_nested_account_is_counted_in_its_own_window_only() {
    let Some(root) = child_root() else {
        return run_in_child("a_nested_account_is_counted_in_its_own_window_only", "nested-account");
    };
    const PRIMARY: i64 = 1_000;
    const OUTER: i64 = 7_000;
    const INNER: i64 = 3_000;
    write_session(&root.join(".claude"), "primary", PRIMARY);
    let outer = root.join("work-d");
    let inner = outer.join(".claude");
    write_session(&outer, "outer", OUTER);
    write_session(&inner, "inner", INNER);
    register_accounts(&[&outer, &inner]);

    let outer_text = outer.display().to_string();
    let inner_text = inner.display().to_string();
    assert_eq!(
        claude_lane_output(&call_window(Some(&outer_text), WINDOW_FROM, WINDOW_UNTIL)),
        OUTER,
        "the outer account's window counted the nested account"
    );
    assert_eq!(
        claude_lane_output(&call_window(Some(&inner_text), WINDOW_FROM, WINDOW_UNTIL)),
        INNER
    );
    assert_eq!(
        claude_lane_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)),
        PRIMARY
    );
    assert_eq!(
        claude_lane_graph_output(&call_graph()),
        PRIMARY + OUTER + INNER,
        "totals keep every account once"
    );
}

/// The two halves of the exclusion each catch a case the other misses,
/// because the registries are set by separate calls and can disagree: the
/// inner account known only as a config directory (no roots yet), or only by
/// its roots (no config-dir entry). The outer window must drop it either way.
#[test]
fn a_nested_account_known_to_one_registry_is_still_excluded() {
    let Some(root) = child_root() else {
        return run_in_child(
            "a_nested_account_known_to_one_registry_is_still_excluded",
            "nested-one-registry",
        );
    };
    let outer = root.join("work-d");
    let inner = outer.join(".claude");
    write_session(&outer, "outer", 7_000);
    write_session(&inner, "inner", 3_000);
    let outer_text = outer.display().to_string();
    let outer_roots = vec![outer.join("projects"), outer.join("transcripts")];
    let inner_roots = vec![inner.join("projects"), inner.join("transcripts")];

    // Config dir only: the inner account has no registered root.
    crate::apply_config_dirs_for_test(vec![outer_text.clone(), inner.display().to_string()]);
    crate::apply_scan_roots_for_test(std::collections::BTreeMap::from([(
        "claude".to_string(),
        outer_roots.clone(),
    )]))
    .unwrap();
    assert_eq!(
        claude_lane_output(&call_window(Some(&outer_text), WINDOW_FROM, WINDOW_UNTIL)),
        7_000,
        "a nested account known only as a config directory reached the outer window"
    );

    // Roots only: the inner account is not a configured directory.
    crate::apply_config_dirs_for_test(vec![outer_text.clone()]);
    crate::apply_scan_roots_for_test(std::collections::BTreeMap::from([(
        "claude".to_string(),
        outer_roots.into_iter().chain(inner_roots).collect(),
    )]))
    .unwrap();
    assert_eq!(
        claude_lane_output(&call_window(Some(&outer_text), WINDOW_FROM, WINDOW_UNTIL)),
        7_000,
        "a nested account known only by its roots reached the outer window"
    );
}

/// `<D>\\.claude` registered as its own account but actually a junction back to
/// D (registries compare folded strings, so both are accepted). The engine
/// canonicalizes exclusion prefixes, so excluding the "nested" account would
/// exclude D's own roots and empty D's window. D must keep its own usage.
#[cfg(windows)]
#[test]
fn a_nested_alias_of_the_account_itself_does_not_empty_its_window() {
    let Some(root) = child_root() else {
        return run_in_child(
            "a_nested_alias_of_the_account_itself_does_not_empty_its_window",
            "nested-alias",
        );
    };
    let d = root.join("work-d");
    write_session(&d, "d", 7_000);
    let alias = d.join(".claude");
    let cmd = std::env::var_os("SystemRoot")
        .map(|system| std::path::PathBuf::from(system).join("System32").join("cmd.exe"))
        .unwrap();
    let made = std::process::Command::new(cmd)
        .args(["/C", "mklink", "/J"])
        .arg(&alias)
        .arg(&d)
        .output()
        .unwrap();
    assert!(made.status.success(), "mklink /J failed: {made:?}");
    assert!(
        alias.join("projects").is_dir(),
        "fixture is inert: the junction does not resolve"
    );
    register_accounts(&[&d, &alias]);

    assert_eq!(
        claude_lane_output(&call_window(
            Some(&d.display().to_string()),
            WINDOW_FROM,
            WINDOW_UNTIL
        )),
        7_000,
        "an alias of the account itself excluded the account's own roots"
    );
}

/// Control: accounts that do not nest scan exactly what an account window
/// scanned before the exclusion existed (its own directory as home, its own
/// roots, no exclusion), message for message.
#[test]
fn unrelated_accounts_scan_exactly_what_they_did_before() {
    let Some(root) = child_root() else {
        return run_in_child("unrelated_accounts_scan_exactly_what_they_did_before", "unrelated-accounts");
    };
    write_session(&root.join(".claude"), "primary", 1_000);
    let d = root.join("work-d");
    let e = root.join("work-e");
    write_session(&d, "d", 7_000);
    write_session(&e, "e", 2_000);
    register_accounts(&[&d, &e]);

    for (dir, expected) in [(&d, 7_000), (&e, 2_000)] {
        let text = dir.display().to_string();
        let window = call_window(Some(&text), WINDOW_FROM, WINDOW_UNTIL);
        assert_eq!(claude_lane_output(&window), expected);
        // Not just the same messages: the same scope, with no exclusion.
        let scoped = crate::window_usage::scoped_context_for_test(
            &crate::LocalSourceContext::process().unwrap(),
            &Some(text.clone()),
        )
        .unwrap();
        assert!(
            scoped.resolved().scanner_settings().excluded_scan_paths.is_empty(),
            "an unrelated account was excluded from {text}"
        );
        let before = crate::LocalSourceContext::derived(
            tokscale_core::ResolvedLocalSourceContext::capture(
                Some(dir.to_path_buf()),
                false,
                tokscale_core::ScannerSettings {
                    extra_scan_paths: std::collections::BTreeMap::from([(
                        "claude".to_string(),
                        vec![dir.join("projects"), dir.join("transcripts")],
                    )]),
                    ..Default::default()
                },
            )
            .unwrap(),
            generation(),
        );
        let reference = crate::window_usage::run(&before, WINDOW_FROM, WINDOW_UNTIL).unwrap();
        assert_eq!(
            window["data"]["messages"], reference["messages"],
            "an unrelated account's window changed"
        );
        assert!(
            !reference["messages"].as_array().unwrap().is_empty(),
            "fixture is inert"
        );
    }
}

// ---- 4′: the gate, everywhere (empty setters still move the generation) --

/// 4′(a): a window scan that took its context before a root change publishes
/// nothing after it.
#[test]
fn a_window_scan_from_before_a_root_change_is_not_published() {
    let Some(_) = child_root() else {
        return run_in_child("a_window_scan_from_before_a_root_change_is_not_published", "gate-window");
    };
    // The caller's context is taken here, before the setter, so it predates
    // the root change however the threads are scheduled; the held COMPUTE
    // lock keeps its publish after the bump.
    let old = crate::LocalSourceContext::process().unwrap();
    let held = crate::window_usage::lock_compute_for_test();
    let caller = std::thread::spawn(move || {
        crate::window_usage::cached(&old, &None, WINDOW_FROM, WINDOW_UNTIL)
    });
    let generation_before = generation();
    assert_eq!(call_set_scan("{}")["ok"], true);
    assert_ne!(generation(), generation_before);
    drop(held);
    assert!(caller.join().unwrap().is_ok(), "the caller is still answered");
    assert!(
        !crate::window_usage::has_entry_for_test(&None, WINDOW_FROM),
        "a scan from the old generation must not be cached"
    );
}

/// 4′(b): same for the graph.
#[test]
fn a_graph_from_before_a_root_change_is_not_published() {
    let Some(_) = child_root() else {
        return run_in_child("a_graph_from_before_a_root_change_is_not_published", "gate-graph");
    };
    let old = crate::LocalSourceContext::process().unwrap();
    assert_eq!(call_set_scan("{}")["ok"], true);
    crate::graph_compute(&old, "").unwrap();
    assert!(!crate::GRAPH_CACHE.lock().unwrap().contains_key(""));
    // Control: the current context does publish.
    let current = crate::LocalSourceContext::process().unwrap();
    crate::graph_compute(&current, "").unwrap();
    assert!(crate::GRAPH_CACHE.lock().unwrap().contains_key(""));
}

/// 4′(c): a tail tick from the old generation does not stamp.
#[test]
fn a_tail_tick_from_before_a_root_change_does_not_stamp() {
    let Some(_) = child_root() else {
        return run_in_child("a_tail_tick_from_before_a_root_change_does_not_stamp", "gate-tail");
    };
    let old = crate::LocalSourceContext::process().unwrap();
    assert_eq!(call_set_scan("{}")["ok"], true);
    crate::tail_tick_if_stale(&old);
    assert!(crate::lock_tick().last.is_none(), "an old-generation tick must not stamp");
    let current = crate::LocalSourceContext::process().unwrap();
    crate::tail_tick_if_stale(&current);
    assert!(crate::lock_tick().last.is_some(), "control: a current tick stamps");
}

/// 4′(d): the config-dir setter moves the generation only after its registry
/// commit, so a blocked primary window publishes nothing; an `Err` moves
/// nothing.
#[test]
fn the_config_dir_setter_orders_its_bump_after_the_commit() {
    let Some(_) = child_root() else {
        return run_in_child("the_config_dir_setter_orders_its_bump_after_the_commit", "gate-dirs");
    };
    // Context taken before the setter on this thread (see 4′(a)).
    let old = crate::LocalSourceContext::process().unwrap();
    let held = crate::window_usage::lock_compute_for_test();
    let caller = std::thread::spawn(move || {
        crate::window_usage::cached(&old, &None, WINDOW_FROM, WINDOW_UNTIL)
    });
    assert_eq!(call_set_dirs(r#"["C:\\tokenbar-acceptance\\work"]"#)["ok"], true);
    drop(held);
    assert!(caller.join().unwrap().is_ok());
    assert!(!crate::window_usage::has_entry_for_test(&None, WINDOW_FROM));
    assert_eq!(
        crate::claude_config_dirs::snapshot().0,
        vec![r"C:\tokenbar-acceptance\work".to_string()],
        "a reader that sees the new generation sees the new directories"
    );

    // The order itself: right after the registry commit the generation had
    // not moved yet, so a resolver that sees the new generation also sees the
    // new directories. A bump before the replace fails here.
    let generation_before = generation();
    assert_eq!(call_set_dirs(r#"["C:\\tokenbar-acceptance\\other"]"#)["ok"], true);
    assert_eq!(
        crate::GENERATION_AT_CONFIG_DIR_COMMIT.load(Ordering::SeqCst),
        generation_before,
        "the generation moved before the registry commit"
    );
    assert_ne!(generation(), generation_before);

    let generation_before = generation();
    assert_eq!(call_set_dirs("not json")["ok"], false);
    assert_eq!(generation(), generation_before, "an Err must not move the generation");
}

// ---- 8: concurrency smoke, everywhere ------------------------------------

#[test]
fn setters_and_readers_run_concurrently_without_deadlock() {
    let Some(_) = child_root() else {
        return run_in_child("setters_and_readers_run_concurrently_without_deadlock", "smoke");
    };
    let (done_tx, done_rx) = std::sync::mpsc::channel::<()>();
    let mut threads = Vec::new();
    for reader in 0..4 {
        let done = done_tx.clone();
        threads.push(std::thread::spawn(move || {
            for _ in 0..6 {
                match reader {
                    0 => assert_eq!(call_graph()["ok"], true),
                    1 => assert_eq!(call_window(None, WINDOW_FROM, WINDOW_UNTIL)["ok"], true),
                    2 => {
                        let _ = call_window(Some(r"C:\tokenbar-acceptance\work"), WINDOW_FROM, WINDOW_UNTIL);
                    }
                    _ => assert_eq!(unsafe { take(crate::tb_usage_trace(600)) }["ok"], true),
                }
            }
            done.send(()).unwrap();
        }));
    }
    {
        let done = done_tx.clone();
        threads.push(std::thread::spawn(move || {
            for round in 0..6 {
                if round % 2 == 0 {
                    assert_eq!(call_set_dirs(r#"["C:\\tokenbar-acceptance\\work"]"#)["ok"], true);
                    assert_eq!(call_set_scan("{}")["ok"], true);
                } else {
                    assert_eq!(call_set_dirs("[]")["ok"], true);
                    assert_eq!(call_set_scan("{}")["ok"], true);
                }
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

// ---- 2′, 3′, 5′: registering real directories, Windows only ---------------

#[cfg(target_os = "windows")]
#[test]
fn extra_roots_reach_totals_and_their_own_window_only() {
    let Some(root) = child_root() else {
        return run_in_child("extra_roots_reach_totals_and_their_own_window_only", "roots");
    };
    const PRIMARY: i64 = 1_000;
    const EXTRA: i64 = 7_000;
    write_session(&root.join(".claude"), "primary", PRIMARY);
    let work = root.join("work-d");
    write_session(&work, "d", EXTRA);
    let work_text = work.display().to_string();

    let id_before = call_context_id()["data"].clone();
    let total_before = claude_graph_output(&call_graph());
    assert_eq!(total_before, PRIMARY, "control: the fixture's primary session is read");
    assert_eq!(claude_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)), PRIMARY);

    assert_eq!(call_set_dirs(&serde_json::json!([work_text]).to_string())["ok"], true);
    let roots = serde_json::json!({ "claude": [
        work.join("projects").display().to_string(),
        work.join("transcripts").display().to_string(),
    ]});
    let report = call_set_scan(&roots.to_string());
    assert_eq!(report["ok"], true, "{report}");
    assert_eq!(report["data"]["registeredCount"], 2, "{report}");
    assert_ne!(call_context_id()["data"], id_before, "new roots change the context identity");

    // 2′: totals include the extra root.
    assert_eq!(claude_graph_output(&call_graph()), PRIMARY + EXTRA);

    // 3′: each window reads its own account only; alternating costs one scan each.
    let before = crate::window_usage::scan_count();
    for _ in 0..2 {
        assert_eq!(claude_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)), PRIMARY);
        let extra = call_window(Some(&work_text), WINDOW_FROM, WINDOW_UNTIL);
        assert_eq!(claude_output(&extra), EXTRA);
        assert!(
            extra["data"]["messages"].as_array().unwrap().iter().all(|m| m["client"] == "claude"),
            "an extra account's window holds Claude rows only: {extra}"
        );
    }
    assert_eq!(crate::window_usage::scan_count(), before + 2);
    let unknown = call_window(Some(&root.join("not-registered").display().to_string()), WINDOW_FROM, WINDOW_UNTIL);
    assert_eq!(unknown["ok"], false);
    assert!(unknown.to_string().contains("no registered scan root"), "{unknown}");

    // Back to nothing: baseline totals and identity.
    assert_eq!(call_set_scan("{}")["ok"], true);
    assert_eq!(call_set_dirs("[]")["ok"], true);
    assert_eq!(claude_graph_output(&call_graph()), total_before);
    assert_eq!(call_context_id()["data"], id_before);
}

#[cfg(target_os = "windows")]
#[test]
fn the_primary_config_dir_and_non_directories_are_refused() {
    let Some(root) = child_root() else {
        return run_in_child("the_primary_config_dir_and_non_directories_are_refused", "refuse");
    };
    write_session(&root.join(".claude"), "primary", 1_000);
    let primary_before = claude_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL));

    let shouted = root.join(".CLAUDE").display().to_string().replace('\\', "/");
    let dirs = call_set_dirs(&serde_json::json!([shouted]).to_string());
    assert_eq!(dirs["data"]["rejected"][0]["reason"], "defaultConfigDir", "{dirs}");

    let file = root.join("not-a-dir.jsonl");
    std::fs::write(&file, "{}").unwrap();
    let scan = call_set_scan(
        &serde_json::json!({ "claude": [
            format!("{}/projects", shouted),
            file.display().to_string(),
        ]})
        .to_string(),
    );
    let reasons: Vec<&str> = scan["data"]["rejected"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| entry["reason"].as_str().unwrap())
        .collect();
    assert_eq!(reasons, ["defaultConfigDir", "notDirectory"], "{scan}");
    let text = format!("{dirs}{scan}");
    assert!(!text.contains("not-a-dir") && !text.to_lowercase().contains(".claude"), "{text}");
    assert_eq!(claude_output(&call_window(None, WINDOW_FROM, WINDOW_UNTIL)), primary_before);
}
