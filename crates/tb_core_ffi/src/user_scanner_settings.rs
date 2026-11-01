//! The user-owned part of the process scan: where the user told tokscale an
//! OpenCode database lives outside opencode's default data dir.
//!
//! Two sources, the same two the tokscale CLI user has: the `scanner` key of
//! `<tokscale config dir>/settings.json` (`opencodeDbPaths`; the dir is the
//! engine's own `paths::get_config_dir()`, so `TOKSCALE_CONFIG_DIR` and the
//! Windows `%APPDATA%\tokscale` default match the CLI), and the `OPENCODE_DB`
//! environment variable opencode itself reads. The engine honours neither on
//! its own: `ScannerSettings::opencode_db_paths` is an input and `OPENCODE_DB`
//! is read nowhere in it. Every source-scanning entry point takes its settings
//! from the process context, which `capture_process_context` builds from
//! [`opencode_db_paths`], so this is the one place the paths enter. They are
//! part of the engine's source-context identity, so a change re-keys caches.
//!
//! When it runs: callers read the paths BEFORE taking the process-context cell
//! lock (V-2b: no file I/O under it) and hand them to the capture. That is at
//! the first use of the process context, and again at each re-capture (the
//! root setters and the Cursor takeover recheck). An edited `settings.json` is
//! therefore picked up at the next such capture; `OPENCODE_DB` is fixed when
//! the process starts, so a changed value needs a restart of the app.
//!
//! Only `opencodeDbPaths` is taken from the file: `extra_scan_paths` and
//! `excluded_scan_paths` stay owned by the root registry and Cursor takeover.
//!
//! `OPENCODE_DB` is read exactly as opencode does (packages/cli/src/
//! database-path.ts: `path.resolve(data, filename)`, with the exact string
//! `:memory:` kept as is): no trimming, unquoting or `%VAR%` expansion; a
//! relative value is resolved against opencode's data dir, which is the dir
//! the engine scans for the default OpenCode DB (`XDG_DATA_HOME`, else
//! `<home>/.local/share`, plus `opencode`; see [`opencode_data_dir`]); only
//! the exact string `:memory:` names no file. An exactly empty value is our
//! own choice to ignore: opencode would resolve it to its data directory,
//! which holds no database, so it is reported as `emptyEnv` and not handed
//! over. The `settings.json` entries are tokscale's, not opencode's: they are
//! trimmed, unquoted and `%VAR%`-expanded, and must be absolute. Windows
//! drive-relative forms (`D:opencode.db`) are not resolved and end up
//! `notAbsolute`; see [`data_dir_from`] for the other limits.
//!
//! The engine drops a configured path silently when its file name is not
//! `opencode.db` / `opencode-<channel>.db` or the file is absent. Every
//! absolute path is handed to the engine regardless (the engine owns its
//! rules, so a rule change on a pin bump cannot be masked here); a local copy
//! of the name rule only produces the `badFileName` diagnostic. Each problem
//! is recorded with a fixed reason code (never the path: it names a user
//! directory) and is visible through `tb_probe` under `opencodeDb` (used by
//! Smoke and the tests; the WinUI app does not call it) and on stderr when the
//! set of problems changes (a GUI process discards stderr). Surfacing it in
//! the app UI is a deferred product decision. The probe shows what the last read found, which
//! is not necessarily the live context when a capture failed after the read.

use std::collections::HashSet;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

/// A settings file larger than this is not a hand-edited config; rejected.
const MAX_SETTINGS_BYTES: u64 = 1024 * 1024;

#[derive(serde::Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
struct SettingsFile {
    scanner: ScannerKey,
}

#[derive(serde::Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
struct ScannerKey {
    opencode_db_paths: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub(crate) struct Issue {
    /// `"env"` or `"file"`.
    pub source: &'static str,
    /// Zero-based position within that source's list (`env` has one entry).
    pub index: usize,
    /// `notAbsolute`, `emptyEnv`: not handed to the engine. `badFileName`, `missing`:
    /// handed over anyway, the engine skips it (until the file exists, for
    /// `missing`).
    pub reason: &'static str,
}

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Report {
    /// `absent`, `ok`, `tooLarge`, `unreadable`, `invalidJson`.
    pub settings_file: &'static str,
    /// Distinct paths handed to the engine (it dedups again by canonical
    /// path). Includes paths that also carry a `badFileName`/`missing` issue.
    pub handed_to_engine: usize,
    pub issues: Vec<Issue>,
}

static LAST: Mutex<Option<Report>> = Mutex::new(None);
static LAST_LOGGED: Mutex<Option<Report>> = Mutex::new(None);

/// What the last [`opencode_db_paths`] call found, for `tb_probe`.
pub(crate) fn diagnostics() -> serde_json::Value {
    let report = LAST
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .clone()
        .unwrap_or(Report { settings_file: "absent", handed_to_engine: 0, issues: Vec::new() });
    serde_json::to_value(report).unwrap_or(serde_json::Value::Null)
}

/// The OpenCode DB paths the engine should scan, from the live config dir and
/// environment. Does file I/O: never call it under the process-context lock.
pub(crate) fn opencode_db_paths() -> Vec<PathBuf> {
    let (paths, report) = resolve(
        &tokscale_core::paths::get_config_dir().join("settings.json"),
        std::env::var_os("OPENCODE_DB"),
        opencode_data_dir().as_deref(),
        &|name| std::env::var(name).ok(),
    );
    publish(&report);
    paths
}

/// opencode's data dir as the engine resolves it for the default OpenCode DB
/// (scanner.rs: `<XdgData root>/opencode`), the base a relative `OPENCODE_DB`
/// is resolved against.
fn opencode_data_dir() -> Option<PathBuf> {
    data_dir_from(
        &crate::user_home_dir()?,
        std::env::var_os("XDG_DATA_HOME"),
        &std::env::current_dir().ok()?,
    )
}

/// The engine's rule for `PathRoot::XdgData` under a captured context
/// (`resolve_client_root`, which is crate-private there and so cannot be
/// asked): an unset, exactly empty or non-Unicode `XDG_DATA_HOME` falls back
/// to `<home>/.local/share`; any other value is taken as is and, when
/// relative, joined onto the working directory read when the paths are read
/// (not at the engine's capture, which follows immediately).
///
/// Limits, not handled: a Windows drive-relative form (`C:data` in
/// `XDG_DATA_HOME`, `D:opencode.db` in `OPENCODE_DB`) is not resolved like
/// Node's `path.resolve` or the engine's `fully_qualified`, and ends up
/// `notAbsolute`; a relative HOME is not qualified against the working
/// directory; `..` is not normalized (the engine's canonical dedup still
/// collapses duplicates, but `handedToEngine` may over-count).
fn data_dir_from(home: &Path, xdg: Option<std::ffi::OsString>, cwd: &Path) -> Option<PathBuf> {
    let root = match xdg.filter(|v| !v.is_empty() && v.to_str().is_some()) {
        Some(v) if Path::new(&v).is_absolute() => PathBuf::from(v),
        Some(v) => cwd.join(v),
        None => home.join(".local").join("share"),
    };
    Some(root.join("opencode"))
}

fn publish(report: &Report) {
    *LAST.lock().unwrap_or_else(|p| p.into_inner()) = Some(report.clone());
    let problem = !report.issues.is_empty() || !matches!(report.settings_file, "absent" | "ok");
    let mut logged = LAST_LOGGED.lock().unwrap_or_else(|p| p.into_inner());
    if !problem {
        // A problem that comes back after a clean read logs again.
        *logged = None;
    } else if logged.as_ref() != Some(report) {
        eprintln!("tb_core_ffi: opencode db settings: {}", serde_json::json!(report));
        *logged = Some(report.clone());
    }
}

/// Engine rule (`is_opencode_db_filename`, private there): `opencode.db` or
/// `opencode-<channel>.db`.
fn is_opencode_db_filename(name: &str) -> bool {
    let Some(stem) = name.strip_suffix(".db") else {
        return false;
    };
    stem == "opencode"
        || stem.strip_prefix("opencode-").is_some_and(|channel| {
            !channel.is_empty()
                && channel
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
        })
}

/// Expand `%NAME%` references in a `settings.json` entry (not `OPENCODE_DB`)
/// that `vars` knows; unknown references stay literal
/// (and then fail the absolute check).
fn expand(raw: &str, vars: &dyn Fn(&str) -> Option<String>) -> String {
    let mut out = String::new();
    let mut rest = raw;
    while let Some(start) = rest.find('%') {
        out.push_str(&rest[..start]);
        let after = &rest[start + 1..];
        match after.find('%') {
            Some(end) if end > 0 => match vars(&after[..end]) {
                Some(value) => {
                    out.push_str(&value);
                    rest = &after[end + 1..];
                }
                // ExpandEnvironmentStrings: an unknown name stays literal with
                // both `%`, and the closing `%` is not reused as an opener.
                None => {
                    out.push_str(&rest[start..start + end + 2]);
                    rest = &after[end + 1..];
                }
            },
            Some(_) => {
                out.push_str("%%");
                rest = &after[1..];
            }
            None => {
                out.push('%');
                rest = after;
            }
        }
    }
    out.push_str(rest);
    out
}

/// The name diagnostic for a path handed to the engine (it will probably skip
/// a file the engine's own name rule rejects).
fn checked(path: PathBuf) -> (PathBuf, Option<&'static str>) {
    let name_ok = path
        .file_name()
        .and_then(|n| n.to_str())
        .is_some_and(is_opencode_db_filename);
    (path, (!name_ok).then_some("badFileName"))
}

/// A `settings.json` entry: normalized, and it must be absolute. Only
/// `notAbsolute` withholds a path.
fn normalize_entry(
    raw: &str,
    vars: &dyn Fn(&str) -> Option<String>,
) -> Result<(PathBuf, Option<&'static str>), &'static str> {
    let path = normalize(raw, vars);
    if path.is_absolute() { Ok(checked(path)) } else { Err("notAbsolute") }
}

/// `OPENCODE_DB` as opencode reads it: the raw value, resolved against the
/// data dir when relative. `Ok(None)`: the exact string `:memory:`.
fn normalize_env(
    raw: &std::ffi::OsStr,
    data_dir: Option<&Path>,
) -> Result<Option<(PathBuf, Option<&'static str>)>, &'static str> {
    if raw == ":memory:" {
        return Ok(None);
    }
    if raw.is_empty() {
        return Err("emptyEnv");
    }
    let path = Path::new(raw);
    let resolved = if path.is_absolute() {
        path.to_path_buf()
    } else {
        data_dir.ok_or("notAbsolute")?.join(path)
    };
    if resolved.is_absolute() { Ok(Some(checked(resolved))) } else { Err("notAbsolute") }
}

fn normalize(raw: &str, vars: &dyn Fn(&str) -> Option<String>) -> PathBuf {
    let mut text = raw.trim();
    for quote in ['"', '\''] {
        if let Some(inner) = text.strip_prefix(quote).and_then(|t| t.strip_suffix(quote)) {
            text = inner.trim();
            break;
        }
    }
    PathBuf::from(expand(text, vars))
}

/// One bounded read; a UTF-8 BOM (Windows PowerShell 5.1, some editors) is
/// stripped before parsing.
fn read_file_paths(file: &Path) -> (&'static str, Vec<String>) {
    let handle = match std::fs::File::open(file) {
        Ok(handle) => handle,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return ("absent", Vec::new()),
        Err(_) => return ("unreadable", Vec::new()),
    };
    let mut bytes = Vec::new();
    if handle.take(MAX_SETTINGS_BYTES + 1).read_to_end(&mut bytes).is_err() {
        return ("unreadable", Vec::new());
    }
    if bytes.len() as u64 > MAX_SETTINGS_BYTES {
        return ("tooLarge", Vec::new());
    }
    let body = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes);
    match serde_json::from_slice::<SettingsFile>(body) {
        Ok(parsed) => ("ok", parsed.scanner.opencode_db_paths),
        Err(_) => ("invalidJson", Vec::new()),
    }
}

fn hand_over(
    paths: &mut Vec<PathBuf>,
    issues: &mut Vec<Issue>,
    seen: &mut HashSet<PathBuf>,
    source: &'static str,
    index: usize,
    (path, name_issue): (PathBuf, Option<&'static str>),
) {
    if let Some(reason) = name_issue {
        issues.push(Issue { source, index, reason });
    }
    if !path.is_file() {
        issues.push(Issue { source, index, reason: "missing" });
    }
    if seen.insert(path.clone()) {
        paths.push(path);
    }
}

fn resolve(
    settings_file: &Path,
    opencode_db_env: Option<std::ffi::OsString>,
    opencode_data_dir: Option<&Path>,
    vars: &dyn Fn(&str) -> Option<String>,
) -> (Vec<PathBuf>, Report) {
    let (state, from_file) = read_file_paths(settings_file);
    let mut paths = Vec::new();
    let mut issues = Vec::new();
    let mut seen = HashSet::new();
    for (index, raw) in from_file.iter().enumerate() {
        match normalize_entry(raw, vars) {
            Ok(found) => hand_over(&mut paths, &mut issues, &mut seen, "file", index, found),
            Err(reason) => issues.push(Issue { source: "file", index, reason }),
        }
    }
    if let Some(raw) = &opencode_db_env {
        match normalize_env(raw, opencode_data_dir) {
            Ok(Some(found)) => hand_over(&mut paths, &mut issues, &mut seen, "env", 0, found),
            Ok(None) => {}
            Err(reason) => issues.push(Issue { source: "env", index: 0, reason }),
        }
    }
    let report = Report { settings_file: state, handed_to_engine: paths.len(), issues };
    (paths, report)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(label: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("tb-uss-{label}-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn db(dir: &Path, name: &str) -> PathBuf {
        let p = dir.join(name);
        std::fs::write(&p, b"x").unwrap();
        p
    }

    fn settings(dir: &Path, bytes: &[u8]) -> PathBuf {
        let file = dir.join("settings.json");
        std::fs::write(&file, bytes).unwrap();
        file
    }

    fn data_dir() -> PathBuf {
        std::env::temp_dir().join("tb-uss-opencode-data")
    }

    fn resolve_t(
        file: &Path,
        env: Option<std::ffi::OsString>,
        vars: &dyn Fn(&str) -> Option<String>,
    ) -> (Vec<PathBuf>, Report) {
        resolve(file, env, Some(&data_dir()), vars)
    }

    fn none(_: &str) -> Option<String> {
        None
    }

    #[test]
    fn no_file_and_no_env_is_the_default() {
        let dir = temp("none");
        let (paths, report) = resolve_t(&dir.join("absent.json"), None, &none);
        assert!(paths.is_empty() && report.issues.is_empty());
        assert_eq!(report.settings_file, "absent");
    }

    #[test]
    fn file_and_env_naming_the_same_db_count_once() {
        let dir = temp("same");
        let a = db(&dir, "opencode.db");
        let json = serde_json::json!({"scanner": {"opencodeDbPaths": [a, a]}});
        let file = settings(&dir, json.to_string().as_bytes());
        let (paths, report) = resolve_t(&file, Some(a.clone().into_os_string()), &none);
        assert_eq!(paths, vec![a]);
        assert!(report.issues.is_empty());
    }

    #[test]
    fn bom_quotes_and_percent_variables() {
        let dir = temp("norm");
        db(&dir, "opencode-beta.db");
        let b = db(&dir, "opencode.db");
        let dir_str = dir.to_string_lossy().into_owned();
        let quoted = format!("  \"{}\" ", b.display());
        let json = serde_json::json!({"scanner": {"opencodeDbPaths":
            ["%TBDIR%/opencode-beta.db", quoted]}});
        let mut bytes = b"\xEF\xBB\xBF".to_vec();
        bytes.extend(json.to_string().as_bytes());
        let file = settings(&dir, &bytes);
        let vars = |n: &str| (n == "TBDIR").then(|| dir_str.clone());
        let (paths, report) = resolve_t(&file, None, &vars);
        assert_eq!(report.settings_file, "ok");
        assert_eq!(paths, vec![PathBuf::from(format!("{dir_str}/opencode-beta.db")), b]);
        assert!(report.issues.is_empty());
    }

    #[test]
    fn rejections_carry_a_reason_and_never_a_path() {
        let dir = temp("rej");
        let ok = db(&dir, "opencode.db");
        let missing = dir.join("opencode-gone.db");
        let json = serde_json::json!({"scanner": {"opencodeDbPaths": [
            "rel/opencode.db", "%NOPE%/opencode.db", dir.join("mydb.db"), missing, ok]}});
        let file = settings(&dir, json.to_string().as_bytes());
        let (paths, report) = resolve_t(&file, None, &none);
        // Only the non-absolute ones are withheld; the engine decides on the rest.
        assert_eq!(paths, vec![dir.join("mydb.db"), missing, ok]);
        assert_eq!(report.handed_to_engine, 3);
        let got: Vec<_> = report.issues.iter().map(|r| (r.index, r.reason)).collect();
        assert_eq!(
            got,
            vec![
                (0, "notAbsolute"),
                (1, "notAbsolute"),
                (2, "badFileName"),
                (2, "missing"),
                (3, "missing")
            ]
        );
    }

    #[test]
    fn unknown_variables_stay_literal_and_do_not_reuse_the_closing_percent() {
        let vars = |n: &str| (n == "TEMP").then(|| "T".to_string());
        assert_eq!(expand("%NOPE%TEMP%", &vars), "%NOPE%TEMP%");
        assert_eq!(expand("%TEMP%/x%TEMP%", &vars), "T/xT");
        assert_eq!(expand("a%b", &vars), "a%b");
        assert_eq!(expand("%%x", &vars), "%%x");
    }

    /// A path that is not valid Unicode reaches the engine byte-for-byte.
    #[test]
    fn a_non_unicode_env_path_is_passed_through_untouched() {
        let dir = temp("nonuni");
        let raw = non_unicode_path(&dir);
        let (paths, report) = resolve_t(&dir.join("absent.json"), Some(raw.clone()), &none);
        assert_eq!(paths, vec![PathBuf::from(&raw)]);
        assert_eq!(report.handed_to_engine, 1);
        assert!(report.issues.iter().any(|i| i.reason == "missing"));
    }

    #[cfg(windows)]
    fn non_unicode_path(dir: &Path) -> std::ffi::OsString {
        use std::os::windows::ffi::{OsStrExt, OsStringExt};
        let mut wide: Vec<u16> = dir.as_os_str().encode_wide().collect();
        wide.extend("\\bad".encode_utf16());
        wide.push(0xD800); // unpaired surrogate
        wide.extend("\\opencode.db".encode_utf16());
        std::ffi::OsString::from_wide(&wide)
    }

    #[cfg(unix)]
    fn non_unicode_path(dir: &Path) -> std::ffi::OsString {
        use std::os::unix::ffi::OsStringExt;
        let mut bytes = dir.as_os_str().as_encoded_bytes().to_vec();
        bytes.extend(b"/bad\xFF/opencode.db");
        std::ffi::OsString::from_vec(bytes)
    }

    #[test]
    fn unusable_files_are_reported_and_ignored() {
        let dir = temp("junk");
        let file = settings(&dir, b"{not json");
        assert_eq!(resolve_t(&file, None, &none).1.settings_file, "invalidJson");
        let file = settings(&dir, &vec![b' '; MAX_SETTINGS_BYTES as usize + 1]);
        let (paths, report) = resolve_t(&file, None, &none);
        assert!(paths.is_empty());
        assert_eq!(report.settings_file, "tooLarge");
        let file = settings(&dir, br#"{"scanner":{"extraScanPaths":{"claude":["/x"]}}}"#);
        let (paths, report) = resolve_t(&file, None, &none);
        assert!(paths.is_empty() && report.settings_file == "ok");
    }

    #[test]
    fn opencode_db_env_is_read_exactly_as_opencode_does() {
        let dir = temp("envsem");
        let absent = dir.join("absent.json");
        // Exactly `:memory:` names no file and is not an issue.
        let (paths, report) = resolve_t(&absent, Some(":memory:".into()), &none);
        assert!(paths.is_empty() && report.issues.is_empty());
        // Our own choice: an exactly empty value is reported, not handed over.
        let (paths, report) = resolve_t(&absent, Some("".into()), &none);
        assert!(paths.is_empty());
        assert_eq!(report.issues, vec![Issue { source: "env", index: 0, reason: "emptyEnv" }]);
        // No trimming, unquoting or expansion: these are relative file names.
        let vars = |_: &str| Some("EXPANDED".to_string());
        for odd in [" :memory: ", "\"opencode.db\"", "%X%/opencode.db", " opencode.db"] {
            let (paths, report) = resolve_t(&absent, Some(odd.into()), &vars);
            assert_eq!(paths, vec![data_dir().join(odd)], "{odd:?}");
            assert_eq!(report.handed_to_engine, 1);
        }
        let (paths, report) = resolve_t(&absent, Some("sub/opencode.db".into()), &none);
        assert_eq!(paths, vec![data_dir().join("sub/opencode.db")]);
        // The data dir does not exist here, so `missing` is the only issue.
        assert_eq!(
            report.issues.iter().map(|i| i.reason).collect::<Vec<_>>(),
            vec!["missing"]
        );
        // Without a data dir a relative value is notAbsolute.
        let (paths, report) = resolve(&absent, Some("opencode.db".into()), None, &none);
        assert!(paths.is_empty() && report.issues[0].reason == "notAbsolute");
        // settings.json entries stay absolute-only and are never joined.
        let file = settings(&dir, br#"{"scanner":{"opencodeDbPaths":["opencode.db"]}}"#);
        let (paths, report) = resolve_t(&file, None, &none);
        assert!(paths.is_empty() && report.issues[0].reason == "notAbsolute");
    }

    #[test]
    fn the_data_dir_follows_the_engines_xdg_rule() {
        let home = temp("home");
        let cwd = temp("cwd");
        let xdg = temp("xdg");
        let from = |v: Option<std::ffi::OsString>| data_dir_from(&home, v, &cwd).unwrap();
        let default = home.join(".local").join("share").join("opencode");
        assert_eq!(from(None), default);
        assert_eq!(from(Some("".into())), default);
        assert_eq!(from(Some(xdg.clone().into())), xdg.join("opencode"));
        // A relative XDG_DATA_HOME is made absolute against the working dir;
        // only an exactly empty value counts as unset.
        assert_eq!(from(Some("rel".into())), cwd.join("rel").join("opencode"));
        assert_eq!(from(Some(" ".into())), cwd.join(" ").join("opencode"));
        // Matches the engine's `resolve_source_environment_paths` at pin
        // 15204f4 (falls back for a non-Unicode value) by reading that code;
        // only the local copy is exercised here, not the engine end to end.
        assert_eq!(from(Some(non_unicode_path(&xdg))), default);
    }
}
