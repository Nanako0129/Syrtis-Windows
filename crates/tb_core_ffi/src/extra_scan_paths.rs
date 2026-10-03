//! Process-wide registry of extra scan roots, per public client id (only
//! `claude` today): the transcript directories of the extra Claude accounts
//! (`<dir>\projects`, `<dir>\transcripts`). Written by
//! `tb_set_extra_scan_paths`; read when the process source context is
//! (re)captured and by the per-account window usage.
//!
//! Port of macOS `extra_scan_paths.rs`, with this crate's path rule
//! (`claude_config_dirs::normalize`: absolute drive paths only) and its
//! reporting rule (index + fixed reason code; the input is never echoed back
//! across the FFI, since a path names a user directory).
//!
//! Parsing and committing are separate: the setter in `lib.rs` captures a
//! source context from the candidate first and commits only when that capture
//! succeeds, so a failed capture leaves the registry exactly as it was.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{LazyLock, RwLock};

use crate::claude_config_dirs::{duplicate_key, is_at_or_under_default_config_dir, normalize};

/// Client ids this consumer wires an extra scan root for. Upstream silently
/// drops an unknown id deep inside the scan, long after the setter reported
/// success, so an unsupported id is refused here instead.
const SUPPORTED_CLIENTS: &[&str] = &["claude"];

/// Two roots (`projects`, `transcripts`) for each of the eight config
/// directories `claude_config_dirs` allows.
const MAX_EXTRA_SCAN_PATHS: usize = 16;

static EXTRA_SCAN_PATHS: LazyLock<RwLock<BTreeMap<String, Vec<PathBuf>>>> =
    LazyLock::new(|| RwLock::new(BTreeMap::new()));

/// The registered roots. Empty by default, which is `ScannerSettings::default()`:
/// a process that never calls the setter scans exactly as before.
pub(crate) fn snapshot() -> BTreeMap<String, Vec<PathBuf>> {
    EXTRA_SCAN_PATHS
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
}

/// A parsed, validated replacement that has not been committed.
pub(crate) struct Candidate {
    pub(crate) registry: BTreeMap<String, Vec<PathBuf>>,
    pub(crate) report: serde_json::Value,
}

/// Replace the registry. Called by the setter only after a context capture
/// from `registry` succeeded.
pub(crate) fn commit(registry: BTreeMap<String, Vec<PathBuf>>) {
    *EXTRA_SCAN_PATHS
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = registry;
}

/// What an accepted path is right now.
enum Shape {
    /// Registered. `true` when it cannot be read at this moment (missing, a
    /// stalled mount, no permission) but may become readable on its own; the
    /// scan already skips a root that is missing at scan time.
    Registrable(bool),
    /// Exists and is not a directory. The walker would yield a regular file as
    /// its own entry and count its contents, so it is refused (macOS rule).
    NotDirectory,
}

fn classify(path: &Path) -> Shape {
    match std::fs::metadata(path) {
        Ok(meta) if meta.is_dir() => Shape::Registrable(std::fs::read_dir(path).is_err()),
        Ok(_) => Shape::NotDirectory,
        Err(_) => Shape::Registrable(true),
    }
}

/// The scan registry's rule for one path on its own, before any list or
/// filesystem check: the drive-path rule (`claude_config_dirs::normalize`),
/// then nothing at or under the primary's `<home>\.claude` (security review
/// R2). Shared with the pre-save check (`claude_config_dirs::validate`), so
/// that check gives this registry's answer without touching the disk.
pub(crate) fn path_rule(raw: &str, home: Option<&Path>) -> Result<String, &'static str> {
    let path = normalize(raw)?;
    if is_at_or_under_default_config_dir(&path, home) {
        return Err("defaultConfigDir");
    }
    Ok(path)
}

/// Parse a `{"<client-id>": ["<path>", ...]}` replacement. Full-replace: `{}`
/// clears every root. Each path is normalized by the config-dir rule, refused
/// when it is at or under the primary's `<home>\.claude` (security review R2),
/// de-duplicated on the folded form, and capped. Malformed JSON is an error
/// and nothing is parsed.
///
/// Per path, in order: [`path_rule`], then the list rules (duplicate,
/// limit), then the filesystem shape.
pub(crate) fn parse(raw: &str, home: Option<&Path>) -> Result<Candidate, &'static str> {
    let input: BTreeMap<String, Vec<String>> =
        serde_json::from_str(raw).map_err(|_| "invalidJson")?;

    let mut registry: BTreeMap<String, Vec<PathBuf>> = BTreeMap::new();
    let mut seen: Vec<String> = Vec::new();
    let mut rejected: Vec<serde_json::Value> = Vec::new();
    let mut unreadable: Vec<serde_json::Value> = Vec::new();

    for (client, paths) in input {
        let supported = SUPPORTED_CLIENTS.contains(&client.as_str());
        let mut accepted: Vec<PathBuf> = Vec::new();
        for (index, raw_path) in paths.iter().enumerate() {
            let reason = if !supported {
                "unsupportedClient"
            } else {
                match path_rule(raw_path, home) {
                    Err(reason) => reason,
                    Ok(path) if seen.contains(&duplicate_key(&path)) => "duplicate",
                    Ok(_) if seen.len() >= MAX_EXTRA_SCAN_PATHS => "limitExceeded",
                    Ok(path) => match classify(Path::new(&path)) {
                        Shape::NotDirectory => "notDirectory",
                        Shape::Registrable(not_readable_now) => {
                            if not_readable_now {
                                unreadable.push(serde_json::json!({
                                    "client": client, "index": index, "reason": "unreadable",
                                }));
                            }
                            seen.push(duplicate_key(&path));
                            accepted.push(PathBuf::from(path));
                            continue;
                        }
                    },
                }
            };
            rejected.push(serde_json::json!({
                "client": if supported { client.as_str() } else { "unsupported" },
                "index": index,
                "reason": reason,
            }));
        }
        if !accepted.is_empty() {
            registry.insert(client, accepted);
        }
    }

    let report = serde_json::json!({
        "registeredCount": seen.len(),
        "rejected": rejected,
        "unreadable": unreadable,
    });
    Ok(Candidate { registry, report })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn refuses_unsupported_clients_and_never_echoes_input() {
        let candidate = parse(r#"{"codex": ["C:\\secret-dir"]}"#, None).unwrap();
        assert!(candidate.registry.is_empty());
        assert_eq!(
            candidate.report["rejected"],
            serde_json::json!([{"client": "unsupported", "index": 0, "reason": "unsupportedClient"}])
        );
        let text = candidate.report.to_string();
        assert!(!text.contains("secret-dir") && !text.contains("codex"), "{text}");
    }

    #[test]
    fn applies_the_drive_path_rule_dedup_and_cap() {
        let mut paths: Vec<String> = vec![
            "relative\\dir".into(),
            r"C:\claude\a\projects".into(),
            "c:/CLAUDE/a/projects".into(),
        ];
        paths.extend((0..20).map(|i| format!(r"C:\claude\x{i}\projects")));
        let raw = serde_json::json!({ "claude": paths }).to_string();
        let candidate = parse(&raw, None).unwrap();
        let roots = &candidate.registry["claude"];
        assert_eq!(roots.len(), MAX_EXTRA_SCAN_PATHS);
        assert_eq!(roots[0], PathBuf::from(r"C:\claude\a\projects"));
        let reasons: Vec<&str> = candidate.report["rejected"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| entry["reason"].as_str().unwrap())
            .collect();
        assert_eq!(reasons[0], "unsupportedPath");
        assert_eq!(reasons[1], "duplicate");
        assert!(reasons[2..].iter().all(|reason| *reason == "limitExceeded"));
        // Missing directories are registered but reported, never dropped.
        assert_eq!(candidate.report["unreadable"].as_array().unwrap().len(), MAX_EXTRA_SCAN_PATHS);
    }

    /// R2 for the scan registry: anything at or under the primary's
    /// `<home>\.claude`, in any case or separator, is refused.
    #[test]
    fn refuses_roots_at_or_under_the_primary_config_dir() {
        let home = Path::new(r"C:\Users\x");
        let raw = serde_json::json!({ "claude": [
            r"C:\Users\x\.claude",
            "c:/users/X/.CLAUDE/projects",
            r"C:\Users\x\.claude-work\projects",
        ]})
        .to_string();
        let candidate = parse(&raw, Some(home)).unwrap();
        let reasons: Vec<&str> = candidate.report["rejected"]
            .as_array()
            .unwrap()
            .iter()
            .map(|entry| entry["reason"].as_str().unwrap())
            .collect();
        assert_eq!(reasons, ["defaultConfigDir", "defaultConfigDir"]);
        assert_eq!(
            candidate.registry["claude"],
            vec![PathBuf::from(r"C:\Users\x\.claude-work\projects")],
            "a sibling whose name only starts with .claude is not the primary's"
        );
    }

    #[test]
    fn malformed_json_parses_nothing() {
        assert_eq!(parse("[", None).err(), Some("invalidJson"));
    }
}
