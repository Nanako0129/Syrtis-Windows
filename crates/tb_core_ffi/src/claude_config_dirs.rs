//! Process-wide registry of the extra Claude config directories the user has
//! configured (`CLAUDE_CONFIG_DIR`-isolated accounts). Written by
//! `tb_set_claude_config_dirs`, read once per run by the Claude quota fetch.
//!
//! Each directory is one Claude card: its credential is read from
//! `<dir>\.credentials.json` only, its `accountKey` is the directory exactly as
//! registered, and its durable history is keyed on that exact string.
//!
//! A `RwLock` static rather than an env var: the process is resident and a
//! Settings edit must take effect without a restart.

use std::sync::{LazyLock, RwLock};

/// More directories than any realistic setup; each one costs a Claude request
/// per poll.
const MAX_CLAUDE_CONFIG_DIRS: usize = 8;

/// `(directories, generation)`. The generation moves on every successful
/// replace, so a Claude run can tell whether the registry changed while it was
/// in flight.
static CLAUDE_CONFIG_DIRS: LazyLock<RwLock<(Vec<String>, u64)>> =
    LazyLock::new(|| RwLock::new((Vec::new(), 0)));

/// Configured directories, in the order the user listed them, and the
/// registry generation they belong to. Empty by default: a process that never
/// calls the setter fetches only the primary (and, when signed in, Claude
/// Desktop).
pub(crate) fn snapshot() -> (Vec<String>, u64) {
    CLAUDE_CONFIG_DIRS
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
}

/// Accept only an absolute drive path (`C:\...` or `C:/...`). Verbatim
/// (`\\?\`), UNC, device (`\\.\`), drive-relative (`C:x`) and rooted (`\x`)
/// paths are refused; a mapped network drive is still a drive letter and is
/// accepted (documented limitation). Trailing separators are stripped; nothing
/// else is edited — no trimming, no case folding — so the registered string is
/// the identity.
///
/// A component is refused rather than repaired when Windows would resolve it
/// to something other than the directory the identity names, or cannot name
/// it at all: empty, `.` or `..`; ending in a space or a dot; containing
/// `: < > | ? * "` or a control character; or a reserved device name (`CON`,
/// `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`, any case, with or without
/// an extension).
///
/// Returns a fixed reason code on refusal; never the input.
///
/// Shared with the scan-root registry (`extra_scan_paths`), so both registries
/// apply one path rule.
pub(crate) fn normalize(raw: &str) -> Result<String, &'static str> {
    if raw.is_empty() {
        return Err("empty");
    }
    let bytes = raw.as_bytes();
    let is_separator = |c: char| c == '\\' || c == '/';
    if bytes.len() < 3
        || !bytes[0].is_ascii_alphabetic()
        || bytes[1] != b':'
        || !is_separator(bytes[2] as char)
    {
        return Err("unsupportedPath");
    }
    let body = raw[3..].trim_end_matches(is_separator);
    if body.is_empty() {
        return Err("rootDirectory");
    }
    if !body.split(is_separator).all(valid_component) {
        return Err("invalidComponent");
    }
    Ok(format!("{}{}", &raw[..3], body))
}

fn valid_component(component: &str) -> bool {
    const RESERVED: [&str; 4] = ["con", "prn", "aux", "nul"];
    let stem = component
        .split('.')
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase();
    let reserved = RESERVED.contains(&stem.as_str())
        || ((stem.starts_with("com") || stem.starts_with("lpt"))
            && stem.len() == 4
            && matches!(stem.as_bytes()[3], b'1'..=b'9'));
    !component.is_empty()
        && component != "."
        && component != ".."
        && !component.ends_with(' ')
        && !component.ends_with('.')
        && !component
            .chars()
            .any(|c| c.is_control() || ":<>|?*\"".contains(c))
        && !reserved
}

/// Two spellings of one directory would be two cards writing two series for
/// one account. Windows paths are case-insensitive and accept either
/// separator, so duplicates are detected on a folded form; the stored string
/// stays exactly as given.
pub(crate) fn duplicate_key(dir: &str) -> String {
    dir.to_lowercase().replace('/', "\\")
}

/// The primary account's own config directory, `<home>\.claude`, folded like
/// [`duplicate_key`]. Its `.credentials.json` IS the primary's credential and
/// its transcripts are the primary's usage, so registering it as an extra
/// account would show the primary twice and, once its roots are excluded from
/// the primary window, hide the primary's own usage (security review R2).
fn default_config_dir_key(home: &std::path::Path) -> String {
    duplicate_key(&home.join(".claude").to_string_lossy())
}

/// Whether `dir` (already normalized) is the primary's `<home>\.claude` or an
/// ancestor of it (the home directory, a drive folder above it). An ancestor
/// is refused too: the primary window excludes registered directories by
/// component prefix, so registering `C:\Users\x` would hide the primary's own
/// `C:\Users\x\.claude` usage, which is the harm R2 exists to prevent. Alias
/// forms a junction could create are not detected; literal and folded matches
/// are.
pub(crate) fn is_default_config_dir(dir: &str, home: Option<&std::path::Path>) -> bool {
    let Some(home) = home else {
        return false;
    };
    let base = default_config_dir_key(home);
    let key = duplicate_key(dir);
    key == base || base.starts_with(&format!("{key}\\"))
}

/// Whether `dir` is the primary's `<home>\.claude`, anything under it, or an
/// ancestor of it — the scan-root rule: the scan registry is handed
/// `<dir>\projects` and `<dir>\transcripts`, never the config directory
/// itself, and an ancestor root would both walk the whole profile and, once
/// excluded from the primary window, hide the primary's usage.
pub(crate) fn is_at_or_under_default_config_dir(dir: &str, home: Option<&std::path::Path>) -> bool {
    let Some(home) = home else {
        return false;
    };
    let base = default_config_dir_key(home);
    let key = duplicate_key(dir);
    is_default_config_dir(dir, Some(home)) || key.starts_with(&format!("{base}\\"))
}

/// Replace the whole registry from a JSON array of directory strings and
/// return the directories now registered. Full-replace, not merge: `[]`
/// clears every configured account. Malformed JSON leaves the registry
/// untouched.
///
/// The report names refused entries by index and fixed reason code only; the
/// input is never echoed back across the FFI.
pub(crate) fn set_from_json(raw: &str) -> Result<(serde_json::Value, Vec<String>), &'static str> {
    let input: Vec<String> = serde_json::from_str(raw).map_err(|_| "invalidJson")?;

    let mut registered: Vec<String> = Vec::new();
    let mut rejected: Vec<serde_json::Value> = Vec::new();
    let home = crate::user_home_dir();
    for (index, raw_dir) in input.iter().enumerate() {
        let reason = match normalize(raw_dir) {
            Ok(dir) if is_default_config_dir(&dir, home.as_deref()) => "defaultConfigDir",
            Ok(dir)
                if registered
                    .iter()
                    .any(|existing| duplicate_key(existing) == duplicate_key(&dir)) =>
            {
                "duplicate"
            }
            Ok(_) if registered.len() >= MAX_CLAUDE_CONFIG_DIRS => "limitExceeded",
            Ok(dir) => {
                registered.push(dir);
                continue;
            }
            Err(reason) => reason,
        };
        rejected.push(serde_json::json!({ "index": index, "reason": reason }));
    }

    {
        let mut state = CLAUDE_CONFIG_DIRS
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.0 = registered.clone();
        state.1 = state.1.wrapping_add(1);
    }

    Ok((
        serde_json::json!({
            "registeredCount": registered.len(),
            "rejected": rejected,
        }),
        registered,
    ))
}

/// One process-wide mutex for every test that writes the static, so parallel
/// `cargo test` threads do not observe each other's registry.
#[cfg(test)]
pub(crate) static TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

/// Commit `dirs` as given (no `normalize`), moving the registry generation as
/// a real replace does; returns what was registered. Only for
/// `apply_config_dirs_for_test`.
#[cfg(test)]
pub(crate) fn commit_for_test(dirs: Vec<String>) -> Vec<String> {
    let mut state = CLAUDE_CONFIG_DIRS
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    state.0 = dirs.clone();
    state.1 = state.1.wrapping_add(1);
    dirs
}

#[cfg(test)]
pub(crate) fn reset_for_test() {
    CLAUDE_CONFIG_DIRS
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .0 = Vec::new();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalize_accepts_only_absolute_drive_paths() {
        for (raw, expected) in [
            (r"C:\Users\x\.claude-work", r"C:\Users\x\.claude-work"),
            (r"C:\Users\x\.claude-work\\", r"C:\Users\x\.claude-work"),
            ("D:/claude/work/", "D:/claude/work"),
            (r"C:\Users\x\my dir", r"C:\Users\x\my dir"),
            (r"C:\Users\x\console", r"C:\Users\x\console"),
            (r"C:\Users\x\com10", r"C:\Users\x\com10"),
            (r"C:\Users\x\Ünïcode", r"C:\Users\x\Ünïcode"),
        ] {
            assert_eq!(normalize(raw).as_deref(), Ok(expected), "{raw}");
        }
        for (raw, reason) in [
            ("", "empty"),
            ("   ", "unsupportedPath"),
            ("relative\\dir", "unsupportedPath"),
            (r"C:dir", "unsupportedPath"),
            (r"\Users\x", "unsupportedPath"),
            (r"\\server\share\claude", "unsupportedPath"),
            (r"\\?\C:\claude", "unsupportedPath"),
            (r"\\?\UNC\server\share\claude", "unsupportedPath"),
            (r"\\.\C:\claude", "unsupportedPath"),
            ("/Users/x/.claude", "unsupportedPath"),
            (r"C:\", "rootDirectory"),
            (r"C:\\\", "rootDirectory"),
        ] {
            assert_eq!(normalize(raw), Err(reason), "{raw:?}");
        }
    }

    #[test]
    fn normalize_refuses_components_windows_would_misresolve() {
        for raw in [
            r"C:\Users\..\x",
            r"C:\Users\.\x",
            r"C:\Users\\x",
            r"C:\Users\x\dir ",
            r"C:\Users\x\dir.",
            r"C:\Users\x \dir",
            r"C:\Users\x\a:b",
            r"C:\Users\x\a<b",
            r"C:\Users\x\a>b",
            r"C:\Users\x\a|b",
            r"C:\Users\x\a?b",
            r"C:\Users\x\a*b",
            "C:\\Users\\x\\a\"b",
            "C:\\Users\\x\\a\u{1}b",
            "C:\\Users\\x\\a\tb",
        ] {
            assert_eq!(normalize(raw), Err("invalidComponent"), "{raw:?}");
        }
    }

    #[test]
    fn normalize_refuses_reserved_device_names() {
        for name in [
            "CON",
            "con",
            "Prn",
            "AUX",
            "nul",
            "COM1",
            "com9",
            "LPT1",
            "lpt9",
            "NUL.txt",
            "con.claude",
            "Com3.log",
        ] {
            assert_eq!(
                normalize(&format!(r"C:\Users\x\{name}")),
                Err("invalidComponent"),
                "{name}"
            );
            assert_eq!(
                normalize(&format!(r"C:\{name}\x")),
                Err("invalidComponent"),
                "{name} as a middle component"
            );
        }
    }

    #[test]
    fn the_primary_config_dir_and_its_ancestors_are_refused() {
        let home = std::path::Path::new(r"C:\Users\x");
        for dir in [r"C:\Users\x\.claude", "c:/users/X/.CLAUDE", r"C:\Users\x", r"C:\Users"] {
            assert!(is_default_config_dir(dir, Some(home)), "{dir}");
            assert!(is_at_or_under_default_config_dir(dir, Some(home)), "{dir}");
        }
        assert!(is_at_or_under_default_config_dir(r"C:\Users\x\.claude\projects", Some(home)));
        assert!(!is_default_config_dir(r"C:\Users\x\.claude\projects", Some(home)));
        for dir in [r"C:\Users\x\.claude-work", r"C:\Users\xy", r"D:\Users\x"] {
            assert!(!is_default_config_dir(dir, Some(home)), "{dir}");
            assert!(!is_at_or_under_default_config_dir(dir, Some(home)), "{dir}");
        }
    }

    #[test]
    fn claude_desktop_sentinel_can_never_be_registered() {
        assert!(normalize("claude-desktop").is_err());
    }

    #[test]
    fn registers_in_order_and_reports_refusals_without_echoing_input() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner());
        reset_for_test();

        let (_, generation_before) = snapshot();
        let (result, registered) = set_from_json(
            r#"["C:\\Users\\x\\.claude-work\\", "relative\\secret-name", "c:/users/X/.claude-work", "D:\\b", "", "C:\\ÄÖ", "c:/äö"]"#,
        )
        .unwrap();
        assert_eq!(
            registered,
            vec![
                r"C:\Users\x\.claude-work".to_string(),
                r"D:\b".to_string(),
                r"C:\ÄÖ".to_string()
            ]
        );
        let (dirs, generation) = snapshot();
        assert_eq!(dirs, registered);
        assert_ne!(
            generation, generation_before,
            "a replace moves the generation"
        );
        assert_eq!(result["registeredCount"], 3);
        assert_eq!(
            result["rejected"],
            serde_json::json!([
                {"index": 1, "reason": "unsupportedPath"},
                {"index": 2, "reason": "duplicate"},
                {"index": 4, "reason": "empty"},
                {"index": 6, "reason": "duplicate"},
            ]),
            "duplicates fold case (including non-ASCII) and separators"
        );
        assert!(!result.to_string().contains("secret-name"));

        set_from_json("[]").unwrap();
        let (dirs, generation_after) = snapshot();
        assert!(dirs.is_empty());
        assert_ne!(generation_after, generation);
        reset_for_test();
    }

    #[test]
    fn caps_the_registry_at_eight_directories() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner());
        reset_for_test();
        let dirs: Vec<String> = (0..10).map(|index| format!(r"C:\claude\{index}")).collect();
        let (result, registered) = set_from_json(&serde_json::to_string(&dirs).unwrap()).unwrap();
        assert_eq!(registered, dirs[..8].to_vec());
        assert_eq!(
            result["rejected"],
            serde_json::json!([
                {"index": 8, "reason": "limitExceeded"},
                {"index": 9, "reason": "limitExceeded"},
            ])
        );
        reset_for_test();
    }

    #[test]
    fn malformed_json_leaves_the_registry_untouched() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner());
        reset_for_test();
        set_from_json(r#"["C:\\claude\\work"]"#).unwrap();
        let before = snapshot();
        assert_eq!(set_from_json("{not json"), Err("invalidJson"));
        assert_eq!(set_from_json(r#"{"a":1}"#), Err("invalidJson"));
        assert_eq!(
            snapshot(),
            before,
            "neither the list nor the generation moved"
        );
        assert_eq!(before.0, vec![r"C:\claude\work".to_string()]);
        reset_for_test();
    }
}
