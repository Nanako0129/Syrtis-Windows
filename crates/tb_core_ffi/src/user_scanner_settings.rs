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
//! The engine drops a configured path silently when its file name is not
//! `opencode.db` / `opencode-<channel>.db` or the file is absent. So that a
//! configured path never silently does nothing, every rejection is recorded
//! with a fixed reason code (never the path: it names a user directory) and
//! surfaced in `tb_probe` under `opencodeDb`, and logged once to stderr.

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
pub(crate) struct Rejected {
    /// `"env"` or `"file"`.
    pub source: &'static str,
    /// Position within that source's list.
    pub index: usize,
    /// `notAbsolute`, `badFileName` (path dropped); `missing` (kept, the
    /// engine skips it until the file exists).
    pub reason: &'static str,
}

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Report {
    /// `absent`, `ok`, `tooLarge`, `unreadable`, `invalidJson`.
    pub settings_file: &'static str,
    pub accepted: usize,
    pub rejected: Vec<Rejected>,
}

static LAST: Mutex<Option<Report>> = Mutex::new(None);
static LAST_LOGGED: Mutex<Option<Report>> = Mutex::new(None);

/// What the last [`opencode_db_paths`] call found, for `tb_probe`.
pub(crate) fn diagnostics() -> serde_json::Value {
    let report = LAST
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .clone()
        .unwrap_or(Report { settings_file: "absent", accepted: 0, rejected: Vec::new() });
    serde_json::to_value(report).unwrap_or(serde_json::Value::Null)
}

/// The OpenCode DB paths the engine should scan, from the live config dir and
/// environment. Does file I/O: never call it under the process-context lock.
pub(crate) fn opencode_db_paths() -> Vec<PathBuf> {
    let env = std::env::var_os("OPENCODE_DB").map(|v| v.to_string_lossy().into_owned());
    let (paths, report) = resolve(
        &tokscale_core::paths::get_config_dir().join("settings.json"),
        env,
        &|name| std::env::var(name).ok(),
    );
    publish(&report);
    paths
}

fn publish(report: &Report) {
    *LAST.lock().unwrap_or_else(|p| p.into_inner()) = Some(report.clone());
    let problem =
        !report.rejected.is_empty() || !matches!(report.settings_file, "absent" | "ok");
    let mut logged = LAST_LOGGED.lock().unwrap_or_else(|p| p.into_inner());
    if problem && logged.as_ref() != Some(report) {
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

/// Replace each `%NAME%` that `vars` knows; unknown references stay literal
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
                None => {
                    out.push('%');
                    rest = after;
                }
            },
            _ => {
                out.push('%');
                rest = after;
            }
        }
    }
    out.push_str(rest);
    out
}

fn normalize(raw: &str, vars: &dyn Fn(&str) -> Option<String>) -> Result<PathBuf, &'static str> {
    let mut text = raw.trim();
    for quote in ['"', '\''] {
        if let Some(inner) = text.strip_prefix(quote).and_then(|t| t.strip_suffix(quote)) {
            text = inner.trim();
            break;
        }
    }
    let path = PathBuf::from(expand(text, vars));
    if !path.is_absolute() {
        return Err("notAbsolute");
    }
    match path.file_name().and_then(|n| n.to_str()) {
        Some(name) if is_opencode_db_filename(name) => Ok(path),
        _ => Err("badFileName"),
    }
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

fn resolve(
    settings_file: &Path,
    opencode_db_env: Option<String>,
    vars: &dyn Fn(&str) -> Option<String>,
) -> (Vec<PathBuf>, Report) {
    let (state, from_file) = read_file_paths(settings_file);
    let mut paths = Vec::new();
    let mut rejected = Vec::new();
    let mut seen = HashSet::new();
    let sources = [("file", from_file), ("env", opencode_db_env.into_iter().collect())];
    for (source, raws) in sources {
        for (index, raw) in raws.iter().enumerate() {
            match normalize(raw, vars) {
                Ok(path) => {
                    if !path.is_file() {
                        rejected.push(Rejected { source, index, reason: "missing" });
                    }
                    if seen.insert(path.clone()) {
                        paths.push(path);
                    }
                }
                Err(reason) => rejected.push(Rejected { source, index, reason }),
            }
        }
    }
    let report = Report { settings_file: state, accepted: paths.len(), rejected };
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

    fn none(_: &str) -> Option<String> {
        None
    }

    #[test]
    fn no_file_and_no_env_is_the_default() {
        let dir = temp("none");
        let (paths, report) = resolve(&dir.join("absent.json"), None, &none);
        assert!(paths.is_empty() && report.rejected.is_empty());
        assert_eq!(report.settings_file, "absent");
    }

    #[test]
    fn file_and_env_naming_the_same_db_count_once() {
        let dir = temp("same");
        let a = db(&dir, "opencode.db");
        let json = serde_json::json!({"scanner": {"opencodeDbPaths": [a, a]}});
        let file = settings(&dir, json.to_string().as_bytes());
        let (paths, report) = resolve(&file, Some(a.to_string_lossy().into_owned()), &none);
        assert_eq!(paths, vec![a]);
        assert!(report.rejected.is_empty());
    }

    #[test]
    fn bom_quotes_and_percent_variables() {
        let dir = temp("norm");
        db(&dir, "opencode-beta.db");
        let dir_str = dir.to_string_lossy().into_owned();
        let json =
            serde_json::json!({"scanner": {"opencodeDbPaths": ["%TBDIR%/opencode-beta.db"]}});
        let mut bytes = b"\xEF\xBB\xBF".to_vec();
        bytes.extend(json.to_string().as_bytes());
        let file = settings(&dir, &bytes);
        let vars = |n: &str| (n == "TBDIR").then(|| dir_str.clone());
        let b = db(&dir, "opencode.db");
        let quoted = format!("  \"{}\" ", b.display());
        let (paths, report) = resolve(&file, Some(quoted), &vars);
        assert_eq!(report.settings_file, "ok");
        assert_eq!(paths, vec![PathBuf::from(format!("{dir_str}/opencode-beta.db")), b]);
        assert!(report.rejected.is_empty());
    }

    #[test]
    fn rejections_carry_a_reason_and_never_a_path() {
        let dir = temp("rej");
        let ok = db(&dir, "opencode.db");
        let missing = dir.join("opencode-gone.db");
        let json = serde_json::json!({"scanner": {"opencodeDbPaths": [
            "rel/opencode.db", "%NOPE%/opencode.db", dir.join("mydb.db"), missing, ok]}});
        let file = settings(&dir, json.to_string().as_bytes());
        let (paths, report) = resolve(&file, None, &none);
        assert_eq!(paths, vec![missing, ok]);
        let got: Vec<_> = report.rejected.iter().map(|r| (r.index, r.reason)).collect();
        assert_eq!(
            got,
            vec![(0, "notAbsolute"), (1, "notAbsolute"), (2, "badFileName"), (3, "missing")]
        );
    }

    #[test]
    fn unusable_files_are_reported_and_ignored() {
        let dir = temp("junk");
        let file = settings(&dir, b"{not json");
        assert_eq!(resolve(&file, None, &none).1.settings_file, "invalidJson");
        let file = settings(&dir, &vec![b' '; MAX_SETTINGS_BYTES as usize + 1]);
        let (paths, report) = resolve(&file, None, &none);
        assert!(paths.is_empty());
        assert_eq!(report.settings_file, "tooLarge");
        let file = settings(&dir, br#"{"scanner":{"extraScanPaths":{"claude":["/x"]}}}"#);
        let (paths, report) = resolve(&file, None, &none);
        assert!(paths.is_empty() && report.settings_file == "ok");
    }
}
