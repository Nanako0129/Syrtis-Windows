//! The user-owned part of the process `ScannerSettings`: where the user told
//! tokscale an OpenCode database lives outside opencode's default data dir.
//!
//! Two sources, the same two the tokscale CLI user has: the `scanner` key of
//! `<tokscale config dir>/settings.json` (`opencodeDbPaths`; the dir is the
//! engine's own `paths::get_config_dir()`, so `TOKSCALE_CONFIG_DIR` and the
//! Windows `%APPDATA%\tokscale` default match the CLI), and the `OPENCODE_DB`
//! environment variable opencode itself reads. The engine honours neither on
//! its own: `opencode_db_paths` is an input, and `OPENCODE_DB` is read nowhere
//! in it. Every source-scanning entry point takes its settings from the
//! process context, which `capture_process_context` builds from [`load`], so
//! this is the one place the paths enter. They are part of the engine's
//! source-context identity (`identity_bytes`), so a change re-keys the caches.
//!
//! Read at each (re)capture, never per call; an edited settings file or a new
//! `OPENCODE_DB` is picked up by the next capture (launch, or a root setter).
//! Only `opencodeDbPaths` is taken from the file: the registered roots and the
//! Cursor takeover own `extra_scan_paths` / `excluded_scan_paths` here.

use std::ffi::OsString;
use std::path::{Path, PathBuf};

use tokscale_core::ScannerSettings;

/// A settings file larger than this is not a hand-edited config; ignore it.
const MAX_SETTINGS_BYTES: u64 = 1024 * 1024;

#[derive(serde::Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
struct SettingsFile {
    scanner: ScannerKey,
}

#[derive(serde::Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
struct ScannerKey {
    opencode_db_paths: Vec<PathBuf>,
}

/// The user's scanner settings from the live config dir and environment.
pub(crate) fn load() -> ScannerSettings {
    from_sources(
        &tokscale_core::paths::get_config_dir().join("settings.json"),
        std::env::var_os("OPENCODE_DB"),
    )
}

fn from_sources(settings_file: &Path, opencode_db_env: Option<OsString>) -> ScannerSettings {
    let mut paths = std::fs::metadata(settings_file)
        .ok()
        .filter(|meta| meta.len() <= MAX_SETTINGS_BYTES)
        .and_then(|_| std::fs::read(settings_file).ok())
        .and_then(|bytes| serde_json::from_slice::<SettingsFile>(&bytes).ok())
        .map(|file| file.scanner.opencode_db_paths)
        .unwrap_or_default();
    paths.extend(opencode_db_env.map(PathBuf::from));
    // Absolute only: a relative path would resolve against whatever the
    // process cwd is at capture. Missing files are skipped by the engine.
    let mut seen = std::collections::HashSet::new();
    paths.retain(|path| path.is_absolute() && seen.insert(path.clone()));
    ScannerSettings {
        opencode_db_paths: paths,
        ..Default::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn abs(name: &str) -> PathBuf {
        std::env::temp_dir().join(name)
    }

    fn write(dir: &Path, json: &str) -> PathBuf {
        let file = dir.join("settings.json");
        std::fs::write(&file, json).unwrap();
        file
    }

    fn temp(label: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("tb-uss-{label}-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn no_file_and_no_env_is_the_default() {
        let dir = temp("none");
        let s = from_sources(&dir.join("absent.json"), None);
        assert!(s.opencode_db_paths.is_empty());
        assert!(s.extra_scan_paths.is_empty() && s.excluded_scan_paths.is_empty());
    }

    #[test]
    fn file_then_env_deduped_absolute_only() {
        let dir = temp("both");
        let a = abs("a.db");
        let json = serde_json::json!({"scanner": {"opencodeDbPaths": [a, "rel.db", a]}});
        let file = write(&dir, &json.to_string());
        let b = abs("b.db");
        let s = from_sources(&file, Some(b.clone().into_os_string()));
        assert_eq!(s.opencode_db_paths, vec![a, b]);
        let s = from_sources(&file, Some(OsString::from("rel-env.db")));
        assert_eq!(s.opencode_db_paths.len(), 1);
    }

    #[test]
    fn garbage_and_other_keys_are_ignored() {
        let dir = temp("junk");
        let file = write(&dir, "{not json");
        assert!(from_sources(&file, None).opencode_db_paths.is_empty());
        let file = write(&dir, r#"{"scanner":{"extraScanPaths":{"claude":["/x"]}}}"#);
        let s = from_sources(&file, None);
        assert!(s.opencode_db_paths.is_empty() && s.extra_scan_paths.is_empty());
    }
}
