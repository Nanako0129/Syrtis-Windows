//! Cursor usage synced from the signed-in Cursor desktop app (Windows port of
//! macOS Syrtis #487; Plan `cursor-desktop-sync-windows-plan.md`), for the
//! default Cursor profile and each added `--user-data-dir` profile (CMP-W W1,
//! port of macOS #532).
//!
//! Data flow, per profile (the default one first, then each added root, one
//! at a time): its `state.vscdb` (read-only, `cursor_desktop`) → access
//! token + its own JWT `sub` → POST `get-filtered-usage-events` on
//! cursor.com, full history, paginated → a trimmed JSON file
//! `usage.<hmac>.json` in that profile's Syrtis-owned dir → the engine scans
//! the newest copy of each account's file as an extra Cursor root (files
//! only, never a directory). Aggregation happens in the engine; this module
//! only produces the files and the takeover decision. The takeover is
//! applied where Windows fixes scanner settings, `capture_process_context`
//! in `lib.rs` (W5), never through the C#-owned scan-root registry.
//!
//! Security contract (macOS S-1..S-6, P3-*; Windows W1..W10; CMP-W WB1..WB11):
//! - The token is never logged, stored, or put in a status/error string;
//!   every failure maps to a fixed literal `reason`.
//! - The cookie is keyed by the sent token's own JWT `sub`; a stored glass or
//!   profile id that differs refuses the walk (S-1). Token, cookie and target
//!   are fixed for one walk.
//! - An expired token is refused locally, before any request; Cursor's
//!   refresh token is never used (rotating it could sign the user out).
//! - A Cursor 3.27+ sealed sign-in (`cursor-safe-storage:v1:…`, S3b) is
//!   decrypted only here, only after the "cursor" consent check read
//!   immediately before `Local State` is opened (C2), with the key from the
//!   user-data-dir the database belongs to (C6); no key is cached (U3). A
//!   blob of 3 or more bytes not starting with `v10` is
//!   `token_sealed_unsupported`; a blob shorter than a v10 blob can be (1 or
//!   2 bytes included, which the version check cannot see) and any other
//!   failure is `token_sealed_unreadable` (win_safe_storage's text never
//!   passed through). `query_login`/`read_login` never decrypt (C1).
//! - C8, what is zeroed: the unwrapped key's final heap copy (the
//!   `Box<SafeStorageKey>`, on drop), the DPAPI output in `load_key`, the
//!   decrypt output buffer, the access-token row's String in `query_login`,
//!   the login's token String (`WipedLogin`, every exit of `run`), and the
//!   cookie value String (`WipedString`, built at final size). Other copies
//!   of the token, the cookie and the key are NOT wiped, and this list of
//!   residuals is not exhaustive. Examples: the key's intermediate copies on the way into that Box (the
//!   `try_into` array in `key_from_bytes`, the by-value returns from
//!   `key_from_bytes` and `load_key`, and the move in `Ok(Box::new(key))`);
//!   rows `query_login` already read when a later row fails to read (the
//!   `collect` drops them, the token row's String if it was read first); SQLite's and
//!   rusqlite's internal buffers, serde_json's unquoting scratch, the decoded
//!   JWT payload, the `HeaderValue` and whatever reqwest copies; and on the
//!   Grok route (`agent_grokbot::load_credentials_from`), the login dropped
//!   on its non-JWT and no-user-id returns and the `CursorCredentials` move
//!   of the same token buffer; the Grok route's scope JSON value and String
//!   (`agent_grokbot.rs`), its cookie String and `HeaderValue`; the Grok
//!   desktop route's decrypted token String and bearer header; and the CNG
//!   key object `BCryptGenerateSymmetricKey` builds in `win_safe_storage`.
//! - The endpoint is a constant; only tests inject one, by parameter (S-4).
//! - Redirects are never followed and production is https-only; any 3xx
//!   (cursor.com sends an expired session to WorkOS) means `expired`.
//! - The switch is in Rust, default off, and is re-read before every page and
//!   under the commit lock (S-3), together with "this profile is still
//!   configured as when the round started" (`Slot::still_in`, WB4).
//! - Only the engine parser's fields are deserialised and written (S-6).
//! - Only a complete walk replaces the file; partial or failed walks leave it.
//! - W1: the caller passes no Syrtis path. The default profile's dir is
//!   `<secure root>\cursor-cache` under `dirs::data_dir()\com.nyanako.tokenbar`
//!   (roaming, as every release); added profiles live in
//!   `<secure root>\cursor-profiles\<UUID>-<root digest>` under
//!   `dirs::data_local_dir()\com.nyanako.tokenbar` (local only, WB5), each
//!   level following the `.secure` fallback like quota history.
//! - W2: storage only through `agent_storage_windows` (protected DACL
//!   {user, SYSTEM}, owner = user, final-component reparse points refused);
//!   deletion only of Syrtis names (`is_ours`), each re-verified before
//!   removal, plus the leftover `.cursor-sync.root` of an earlier W1 build
//!   (`LEGACY_ROOT_FILE_NAME`), removed best-effort with the lock when a
//!   contract-meeting profile dir is emptied; an emptied profile dir is
//!   `rmdir`ed, never `remove_dir_all`.
//! - Added profiles: the input carries `(UUID, root)` only; the database is
//!   always `<root>\User\globalStorage\state.vscdb`, read handle-first on a
//!   fixed drive each sync (`cursor_desktop::read_login_added`). `sub` never
//!   crosses the FFI: a profile whose account an earlier profile already
//!   covered this round reports that profile's id (`duplicateOf`).

use crate::cursor_desktop::{self, CursorLogin, DriveType};
use serde::{Deserialize, Serialize};
use std::io::{self, Write as _};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{LazyLock, Mutex, RwLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const USAGE_EVENTS_URL: &str = "https://cursor.com/api/dashboard/get-filtered-usage-events";
const FILE_NAME_DOMAIN: &str = "cursor-sync-file";
const LOCK_FILE_NAME: &str = ".cursor-sync.lock";
/// A file earlier W1 builds (review round 2) left in added profiles' dirs;
/// nothing writes it now. Removed best-effort when such a dir is emptied
/// (`clean_candidate`), never counted and never a cleanup failure.
const LEGACY_ROOT_FILE_NAME: &str = ".cursor-sync.root";
/// Starts with a dot so it can never match the engine's `usage*` patterns: a
/// half-written temp file must not be scanned.
const TEMP_FILE_PREFIX: &str = ".cursor-sync.tmp-";
/// Same root as quota history and account scope.
const APP_DIR_NAME: &str = "com.nyanako.tokenbar";
/// The default profile's dir, the one location every release has used.
const SYNC_DIR_NAME: &str = "cursor-cache";
/// Added profiles' parent, under the local app root (WB5). A sibling of
/// nothing an older build registers, so a downgrade never scans it.
const PROFILES_DIR_NAME: &str = "cursor-profiles";
/// The default profile's id in statuses and results; not a UUID, so it can
/// never collide with an added profile's.
const DEFAULT_PROFILE_ID: &str = "default";
/// Bounds the per-sync work one config can ask for.
const MAX_PROFILES: usize = 32;

const PAGE_SIZE: u32 = 500;
/// Upstream's per-page timeout (`CURSOR_HTTP_TIMEOUT`), clamped to the budget.
const PAGE_TIMEOUT: Duration = Duration::from_secs(15);
/// 256 pages × 500 = 128k events. The byte cap below normally binds first.
const MAX_PAGES: u32 = 256;
/// Cumulative over the walk. macOS C0.5 measured ≈ 553 bytes/event, so
/// 64 MiB is ≈ 120k events, far above the largest history seen (3542 events
/// ≈ 2 MB).
const MAX_BODY_BYTES: usize = 64 * 1024 * 1024;
/// Only a 403 body is read, to tell an auth refusal (JSON) from an HTML wall.
const MAX_ERROR_BODY_BYTES: usize = 64 * 1024;
/// The first sync (no complete file yet) and every explicit "Sync now".
const FULL_BUDGET: Duration = Duration::from_secs(10 * 60);
/// Later background syncs (macOS D4: ≥ 2× the measured full walk, ≤ 10 min).
const BACKGROUND_BUDGET: Duration = Duration::from_secs(60);
/// A token this close to `exp` could expire mid-walk; treat it as expired.
const EXP_SKEW_SECS: f64 = 60.0;

// ---------------------------------------------------------------------------
// Switch registry (in-memory, default off; the app re-applies it at launch).

/// An added Cursor profile: a caller-made uppercase UUID and the
/// `--user-data-dir` folder the user chose (normalized). Never an account id
/// or anything derived from one. Equality is `same_slot` (no derived,
/// raw-root comparison).
#[derive(Debug, Clone)]
pub(crate) struct Profile {
    pub id: String,
    pub root: PathBuf,
}

impl Profile {
    /// The same id and `same_root`, so a respelling of the folder is the
    /// same profile. `keep_only`, `Slot::still_in` and `PartialEq` use it;
    /// the budget compares slots with `Slot::same_slot` (the same id and
    /// `same_root` check); `profile_dirs` matches dir names made from the
    /// folded root (`profile_dir_name`).
    pub(crate) fn same_slot(&self, other: &Profile) -> bool {
        self.id == other.id && same_root(&self.root, &other.root)
    }
}

impl PartialEq for Profile {
    fn eq(&self, other: &Self) -> bool {
        self.same_slot(other)
    }
}

impl Eq for Profile {}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) struct Config {
    pub enabled: bool,
    /// The default profile's resolved sync dir while enabled (W1: chosen
    /// here, never input).
    pub dir: Option<PathBuf>,
    /// The resolved local `cursor-profiles` dir while enabled with added
    /// profiles; `None` when there are none or it could not be resolved.
    pub profiles_dir: Option<PathBuf>,
    /// Added profiles only; the default profile is implicit.
    pub profiles: Vec<Profile>,
    pub cli_takeover_confirmed: bool,
}

static CONFIG: LazyLock<RwLock<Config>> = LazyLock::new(|| RwLock::new(Config::default()));

pub(crate) fn config() -> Config {
    CONFIG
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
}

fn commit_config(new: Config) {
    *CONFIG
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = new;
}

/// The two per-user data parents: roaming `dirs::data_dir()` for the default
/// profile's dir (W10, as every release) and local `dirs::data_local_dir()`
/// for added profiles (WB5). Tests get only the roots they set, never the
/// real ones; the `test-support` FFI passes its own.
#[derive(Debug, Clone, Default)]
pub(crate) struct Bases {
    pub roaming: Option<PathBuf>,
    pub local: Option<PathBuf>,
}

impl Bases {
    pub(crate) fn production() -> Self {
        #[cfg(test)]
        {
            let root = TEST_DATA_ROOT
                .read()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .clone();
            Self {
                local: root.as_ref().map(|root| root.join(TEST_LOCAL_DIR)),
                roaming: root,
            }
        }
        #[cfg(not(test))]
        Self {
            roaming: dirs::data_dir(),
            local: dirs::data_local_dir(),
        }
    }
}

/// W1: no `dir`. A `dir` key (or any other) is refused, so no caller path can
/// reach storage. `profiles` is read only for an on push: an off push is
/// accepted whatever it carries there.
#[derive(Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct SetInput {
    enabled: bool,
    #[serde(default)]
    cli_takeover_confirmed: bool,
    #[serde(default)]
    profiles: serde_json::Value,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ProfileInput {
    id: String,
    root: String,
}

/// `Guid.ToString("D").ToUpperInvariant()`: 8-4-4-4-12 uppercase hex.
/// Uppercase only, so two ids can never name the same dir on a
/// case-insensitive volume.
fn valid_profile_id(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(i, b)| match i {
            8 | 13 | 18 | 23 => b == b'-',
            _ => b.is_ascii_digit() || (b'A'..=b'F').contains(&b),
        })
}

/// Cursor's own user-data folder, `<config>\Cursor` (the default profile).
fn cursor_own_root() -> Option<PathBuf> {
    dirs::config_dir().map(|dir| dir.join("Cursor"))
}

/// One root's syntactic rule (item 2): `claude_config_dirs::normalize` (an
/// absolute drive path; no UNC, `\\?\`, device paths, reserved names, drive
/// roots), and not Cursor's own folder. Nothing is read from disk: an absent,
/// unmounted or unreadable folder is accepted and reports its own state at
/// sync time.
fn root_rule(raw: &str, cursor_own: Option<&Path>) -> Result<PathBuf, &'static str> {
    let root = crate::claude_config_dirs::normalize(raw)?;
    if cursor_own.is_some_and(|own| same_root_str(&own.to_string_lossy(), &root)) {
        return Err("cursorDefault");
    }
    Ok(PathBuf::from(root))
}

/// An on push's profile list: whole-push refusals (shape, more than
/// `MAX_PROFILES`, an id that is not an uppercase UUID, a duplicate id), then
/// per-entry refusals of a root by index (WB3: the valid entries register).
fn parse_profiles(
    value: serde_json::Value,
    cursor_own: Option<&Path>,
) -> Result<(Vec<Profile>, Vec<serde_json::Value>), &'static str> {
    let input: Vec<ProfileInput> = if value.is_null() {
        Vec::new()
    } else {
        serde_json::from_value(value).map_err(|_| "invalidJson")?
    };
    if input.len() > MAX_PROFILES {
        return Err("tooManyProfiles");
    }
    for (index, profile) in input.iter().enumerate() {
        if !valid_profile_id(&profile.id) || input[..index].iter().any(|p| p.id == profile.id) {
            return Err("invalidProfileId");
        }
    }
    let mut profiles = Vec::with_capacity(input.len());
    let mut rejected = Vec::new();
    for (index, profile) in input.into_iter().enumerate() {
        match root_rule(&profile.root, cursor_own) {
            Ok(root) => profiles.push(Profile {
                id: profile.id,
                root,
            }),
            Err(reason) => rejected.push(serde_json::json!({"index": index, "reason": reason})),
        }
    }
    Ok((profiles, rejected))
}

/// W1: `<resolved secure root>\cursor-cache`. The root resolves exactly as
/// quota history's does (`resolve_secure_storage_directory` on
/// `<data>\com.nyanako.tokenbar`, so a `.secure` fallback is followed), then
/// the child resolves the same way. Creates what is missing.
fn resolve_sync_dir(bases: &Bases) -> io::Result<PathBuf> {
    resolve_under(bases.roaming.as_deref(), SYNC_DIR_NAME)
}

/// WB5: `<resolved secure local root>\cursor-profiles`, resolved like the
/// default's dir. Creates what is missing.
fn resolve_profiles_dir(bases: &Bases) -> io::Result<PathBuf> {
    resolve_under(bases.local.as_deref(), PROFILES_DIR_NAME)
}

fn resolve_under(base: Option<&Path>, child: &str) -> io::Result<PathBuf> {
    let base = base.ok_or_else(|| io::Error::from(io::ErrorKind::NotFound))?;
    let root = secure::resolve_dir(&base.join(APP_DIR_NAME))?;
    secure::resolve_dir(&root.join(child))
}

/// A root as roots compare everywhere (`claude_config_dirs::duplicate_key`:
/// case and `/` vs `\` folded, then a trailing `\` dropped), so a
/// respelling of the same folder is the same root. Roots from `normalize`
/// never end in a separator, so their dir names (`profile_dir_name`) are what
/// they were before the trim.
fn folded_str(root: &str) -> String {
    crate::claude_config_dirs::duplicate_key(root)
        .trim_end_matches('\\')
        .to_string()
}

fn folded_root(root: &Path) -> String {
    folded_str(&root.to_string_lossy())
}

/// The one root comparison: equal once folded (`folded_str`), both sides
/// the same way.
fn same_root(a: &Path, b: &Path) -> bool {
    folded_root(a) == folded_root(b)
}

/// `same_root` for roots still held as strings (the push and add-time
/// checks).
fn same_root_str(a: &str, b: &str) -> bool {
    folded_str(a) == folded_str(b)
}

/// Length of the root digest in an added profile's dir name.
const ROOT_DIGEST_HEX: usize = 16;

/// An added profile's dir name: `<UUID>-<first 16 hex of SHA-256 of the
/// folded root>`. A new root for the same id is another dir, so the old one
/// is unlisted and cleaned like a removed profile's; a respelling is the same
/// dir. A digest, not the path (WB9: no root in any Syrtis name).
fn profile_dir_name(profile: &Profile) -> String {
    let digest = crate::agent_usage::sha256_hex_exact(folded_root(&profile.root).as_bytes());
    format!("{}-{}", profile.id, &digest[..ROOT_DIGEST_HEX])
}

/// The profile id of a Syrtis profile dir name (`.secure` stripped first):
/// `<UUID>-<16 hex>`, or a bare `<UUID>` (earlier W1 builds, cleaned as
/// unlisted). `None` for anything else, which is never touched.
fn profile_dir_id(name: &str) -> Option<&str> {
    let id = name.get(..36).filter(|id| valid_profile_id(id))?;
    match name.get(36..) {
        Some("") => Some(id),
        Some(rest) => rest
            .strip_prefix('-')
            .filter(|hex| {
                hex.len() == ROOT_DIGEST_HEX
                    && hex.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            })
            .map(|_| id),
        None => None,
    }
}

/// `name` and `name.secure`: the two spellings one sticky-fallback level can
/// take.
fn with_fallback(name: &str) -> [String; 2] {
    [name.to_string(), format!("{name}{}", secure::FALLBACK_SUFFIX)]
}

/// Every dir `<base>\<app root>\<child>` Syrtis could have written to: root ∈
/// {preferred, `.secure`} × child ∈ {`child`, `child.secure`}. The fallbacks
/// are sticky, so the dir in use can move after files were written
/// elsewhere; cleanup covers them all (`cursor-cache` under the roaming base,
/// the `cursor-profiles` parents under the local one, WB7).
fn candidates(base: &Path, child: &str) -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    for root in with_fallback(APP_DIR_NAME) {
        for child in with_fallback(child) {
            candidates.push(base.join(&root).join(child));
        }
    }
    candidates
}

/// A real directory (not a reparse point of any kind), told apart from
/// "absent" and from "cannot be examined".
fn real_dir(path: &Path) -> io::Result<bool> {
    match std::fs::symlink_metadata(path) {
        Ok(meta) => Ok(meta.is_dir() && !meta.file_type().is_symlink()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(error),
    }
}

/// Cleanup bookkeeping. `failed` names what could not be fully cleaned: a
/// profile id, or `cursor-profiles` (a parent folder could not be read).
#[derive(Default)]
struct Cleanup {
    removed: usize,
    failed: Vec<String>,
}

impl Cleanup {
    fn record(&mut self, id: &str, result: io::Result<usize>) {
        match result {
            Ok(removed) => self.removed += removed,
            Err(_) => {
                if !self.failed.iter().any(|f| f == id) {
                    self.failed.push(id.to_string());
                }
            }
        }
    }

    /// The default profile's candidates (disable only).
    fn default_dirs(&mut self, roaming: Option<&Path>) {
        let Some(base) = roaming else {
            return;
        };
        for dir in candidates(base, SYNC_DIR_NAME) {
            let cleaned = clean_candidate(&dir, false, None).and_then(Removal::complete);
            self.record(DEFAULT_PROFILE_ID, cleaned);
        }
    }

    /// Every profile dir (`profile_dir_id`, plain or `.secure`) under every
    /// local `cursor-profiles` candidate whose name is not a `keep`
    /// profile's (WB7): a profile removed, or listed with another root (its
    /// dir name differs), or a bare `<UUID>` dir of an earlier W1 build. Only
    /// real directories; a junction or symlink at any level is skipped, never
    /// followed; a listed profile's dir is not opened; anything else is left
    /// alone.
    fn profile_dirs(&mut self, local: Option<&Path>, keep: &[Profile]) {
        let Some(local) = local else {
            return;
        };
        let kept: Vec<String> = keep.iter().map(profile_dir_name).collect();
        for parent in candidates(local, PROFILES_DIR_NAME) {
            match real_dir(&parent) {
                Ok(true) => {}
                Ok(false) => continue,
                Err(_) => {
                    self.record(PROFILES_DIR_NAME, Err(io::Error::other("unreadable")));
                    continue;
                }
            }
            let Ok(entries) = std::fs::read_dir(&parent) else {
                self.record(PROFILES_DIR_NAME, Err(io::Error::other("unreadable")));
                continue;
            };
            for entry in entries {
                let Ok(entry) = entry else {
                    self.record(PROFILES_DIR_NAME, Err(io::Error::other("unreadable")));
                    continue;
                };
                let name = entry.file_name();
                let Some(name) = name.to_str() else {
                    continue;
                };
                let dir_name = name.strip_suffix(secure::FALLBACK_SUFFIX).unwrap_or(name);
                let Some(id) = profile_dir_id(dir_name) else {
                    continue;
                };
                if kept.iter().any(|name| name == dir_name) {
                    continue;
                }
                let id = id.to_string();
                match real_dir(&entry.path()) {
                    Ok(true) => self.record(
                        &id,
                        clean_candidate(&entry.path(), true, None).and_then(Removal::complete),
                    ),
                    Ok(false) => {}
                    Err(error) => self.record(&id, Err(error)),
                }
            }
        }
    }
}

/// Cleanup of one EXISTING candidate dir. No directory is created (a
/// candidate is opened only after it was seen to exist, so only a dir deleted
/// in between could be re-created, empty). A candidate that meets the storage
/// contract is cleaned under its secure lock with the secure delete; one that
/// is a real dir failing the contract (never written by Syrtis, or loosened
/// after it was) fails when it holds a Syrtis-named file, which then cannot
/// be deleted safely, or cannot be listed. A candidate that is itself not a
/// real directory (a junction included) is skipped; ancestor junctions are
/// followed, as the storage contract allows (same-user planting, out of
/// scope). `remove_dir`: an added profile's dir; once our files are gone,
/// the lock file and a leftover `LEGACY_ROOT_FILE_NAME` go too (secure
/// delete, best-effort; never in a dir failing the contract, never for the
/// default profile's dirs) and the dir is `rmdir`ed, which succeeds only if
/// nothing else is left. `keep`: one file name to leave (a duplicate profile
/// keeps its current account's copy, as a commit does); when that file is
/// there (as the listing saw it), the dir keeps its lock and no `rmdir` is
/// attempted. Files that could not be deleted are counted in `left`, next to
/// what was removed (`Removal::complete` makes them an error).
fn clean_candidate(dir: &Path, remove_dir: bool, keep: Option<&str>) -> io::Result<Removal> {
    if !real_dir(dir)? {
        return Ok(Removal::default());
    }
    if secure::ensure_dir(dir).is_err() {
        let ours = std::fs::read_dir(dir).map(|entries| {
            entries.flatten().any(|entry| {
                let name = entry.file_name().to_string_lossy().into_owned();
                is_ours(&name) && Some(name.as_str()) != keep
            })
        });
        // Report only: nothing is deleted from a dir failing the contract.
        // The legacy record is not one of our names, so alone it is no
        // deletion failure.
        return match ours {
            Ok(false) => Ok(Removal::default()),
            _ => Err(io::Error::other("a Syrtis usage file could not be deleted")),
        };
    }
    let lock = secure::open_lock(&dir.join(LOCK_FILE_NAME))?;
    let removal = remove_usage_files(dir, keep)?;
    // A kept file (a duplicate's current account) stays with its dir and
    // lock: no lock deleted, no rmdir attempted under a dir still in use.
    if remove_dir && removal.left == 0 && !removal.kept {
        // The lock handle does not share delete: release it first.
        drop(lock);
        let _ = secure::remove_file(&dir.join(LEGACY_ROOT_FILE_NAME));
        let _ = secure::remove_file(&dir.join(LOCK_FILE_NAME));
        let _ = std::fs::remove_dir(dir);
    }
    Ok(removal)
}

/// Disable-time cleanup of every existing candidate, default and added
/// profiles alike. Every candidate is tried; any failure makes the whole
/// cleanup an error (W7).
fn remove_usage_files_everywhere(bases: &Bases) -> io::Result<usize> {
    let mut cleanup = Cleanup::default();
    cleanup.default_dirs(bases.roaming.as_deref());
    cleanup.profile_dirs(bases.local.as_deref(), &[]);
    if cleanup.failed.is_empty() {
        Ok(cleanup.removed)
    } else {
        Err(io::Error::other("a Syrtis usage file could not be deleted"))
    }
}

/// `<HOME>\.config\tokscale\cursor-cache`, the CLI's Cursor root (engine
/// `clients.rs`, `PathRoot::Home`).
pub(crate) fn cli_root(home: &Path) -> PathBuf {
    home.join(".config").join("tokscale").join("cursor-cache")
}

/// Replace the registry from
/// `{"enabled":bool,"cliTakeoverConfirmed":bool,"profiles":[{"id":"<UUID>","root":"X:\\..."}]}`
/// with the production data parents.
pub(crate) fn set_from_json(raw: &str) -> Result<serde_json::Value, String> {
    set_with_bases(raw, &Bases::production())
}

/// `set_from_json` against explicit data parents (the `test-support` FFI;
/// tests reach it through `set_from_json`'s test bases).
///
/// Off (always accepted, whatever its profiles): commits `enabled: false`
/// first, then deletes the Syrtis usage files from every existing default
/// and added-profile candidate dir without creating any (P3-7); a cleanup
/// that cannot finish is the error `cleanupFailed` (W7).
///
/// On: whole-push refusals are `invalidJson` (shape, unknown key),
/// `tooManyProfiles`, `invalidProfileId` and `storageUnavailable` (the
/// default dir); a refused root is reported by index in `rejected` while the
/// other entries register (WB3). On any refusal the committed config keeps
/// only the profiles that are also in this input (none when the input cannot
/// be read), so a profile the user removed is never walked by a stale config.
/// An accepted on push removes our files from every profile dir it does not
/// list (a dir's name follows its root, so a profile listed with another
/// root leaves its old dir unlisted) (non-fatal: `cleanupFailed` /
/// `cleanupFailedProfiles`).
pub(crate) fn set_with_bases(raw: &str, bases: &Bases) -> Result<serde_json::Value, String> {
    let input: SetInput = match serde_json::from_str(raw) {
        Ok(input) => input,
        Err(_) => {
            keep_only(&[]);
            return Err("invalidJson".to_string());
        }
    };
    if !input.enabled {
        commit_config(Config {
            cli_takeover_confirmed: input.cli_takeover_confirmed,
            ..Config::default()
        });
        let removed =
            remove_usage_files_everywhere(bases).map_err(|_| "cleanupFailed".to_string())?;
        return Ok(set_result(&config(), &[], removed, &[]));
    }
    let (profiles, rejected) = match parse_profiles(input.profiles, cursor_own_root().as_deref())
    {
        Ok(parsed) => parsed,
        Err(code) => {
            keep_only(&[]);
            return Err(code.to_string());
        }
    };
    let dir = match resolve_sync_dir(bases) {
        Ok(dir) => dir,
        Err(_) => {
            keep_only(&profiles);
            return Err("storageUnavailable".to_string());
        }
    };
    let profiles_dir = if profiles.is_empty() {
        None
    } else {
        resolve_profiles_dir(bases).ok()
    };
    let new = Config {
        enabled: true,
        dir: Some(dir),
        profiles_dir,
        profiles,
        cli_takeover_confirmed: input.cli_takeover_confirmed,
    };
    commit_config(new.clone());
    // Every unlisted profile dir goes, whatever the config before this push
    // was: a removed profile's, and a re-rooted one's old dir.
    let mut cleanup = Cleanup::default();
    cleanup.profile_dirs(bases.local.as_deref(), &new.profiles);
    Ok(set_result(&new, &rejected, cleanup.removed, &cleanup.failed))
}

/// The refusal rule (WB3): keep only what the refused input also lists.
fn keep_only(listed: &[Profile]) {
    let mut guard = CONFIG
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    guard
        .profiles
        .retain(|profile| listed.iter().any(|p| p.same_slot(profile)));
}

/// Success data. `profiles` echoes ids only (WB9: no root in any result);
/// `dir` and `profilesDir` are Syrtis's own dirs.
fn set_result(
    config: &Config,
    rejected: &[serde_json::Value],
    removed: usize,
    failed: &[String],
) -> serde_json::Value {
    let mut ids: Vec<&str> = Vec::new();
    if config.enabled {
        ids.push(DEFAULT_PROFILE_ID);
        ids.extend(config.profiles.iter().map(|p| p.id.as_str()));
    }
    serde_json::json!({
        "enabled": config.enabled,
        "dir": config.dir.as_deref().map(Path::to_string_lossy),
        "profilesDir": config.profiles_dir.as_deref().map(Path::to_string_lossy),
        "profiles": ids.iter().map(|id| serde_json::json!({"id": id})).collect::<Vec<_>>(),
        "rejected": rejected,
        "cliTakeoverConfirmed": config.cli_takeover_confirmed,
        "removedFiles": removed,
        "cleanupFailed": !failed.is_empty(),
        "cleanupFailedProfiles": failed,
    })
}

// ---------------------------------------------------------------------------
// Add-time validation (item 2): validate-only, commits nothing.

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ValidateInput {
    root: String,
    #[serde(default)]
    existing: Vec<String>,
}

/// Whether a folder can be added next to `existing`: `{"accepted":bool,
/// "reason":code|null}`, never a path (WB9). In order: the list is not full;
/// the root rule (`normalize`); a fixed drive (`GetDriveTypeW`, before any
/// I/O, WB2); then Cursor's own folder and the listed folders, by folded path
/// or by directory identity (volume serial + file id, reparse points
/// followed, so a junction to a listed folder is the same folder). Metadata
/// only: no database is opened. A folder that cannot be read has no identity
/// and never counts as an identity match.
pub(crate) fn validate_from_json(raw: &str) -> Result<serde_json::Value, String> {
    validate_with(
        raw,
        &cursor_desktop::drive_type,
        cursor_own_root().as_deref(),
    )
}

fn validate_with(
    raw: &str,
    drive_type: &DriveType<'_>,
    cursor_own: Option<&Path>,
) -> Result<serde_json::Value, String> {
    let input: ValidateInput =
        serde_json::from_str(raw).map_err(|_| "invalidJson".to_string())?;
    let reason = (|| {
        if input.existing.len() >= MAX_PROFILES {
            return Some("tooManyProfiles");
        }
        let root = match crate::claude_config_dirs::normalize(&input.root) {
            Ok(root) => root,
            Err(code) => return Some(code),
        };
        if drive_type(Path::new(&root)) != cursor_desktop::DRIVE_FIXED {
            return Some("notFixedDrive");
        }
        let identity = folder_identity(Path::new(&root));
        let same = |other: &str| {
            let Ok(other) = crate::claude_config_dirs::normalize(other) else {
                return false;
            };
            if same_root_str(&other, &root) {
                return true;
            }
            identity.is_some()
                && drive_type(Path::new(&other)) == cursor_desktop::DRIVE_FIXED
                && folder_identity(Path::new(&other)) == identity
        };
        if cursor_own.is_some_and(|own| same(&own.to_string_lossy())) {
            return Some("cursorDefault");
        }
        if input.existing.iter().any(|existing| same(existing)) {
            return Some("duplicate");
        }
        None
    })();
    Ok(serde_json::json!({"accepted": reason.is_none(), "reason": reason}))
}

#[cfg(windows)]
fn folder_identity(path: &Path) -> Option<crate::agent_storage_windows::FileIdentity> {
    crate::agent_storage_windows::directory_identity(path)
}

#[cfg(not(windows))]
fn folder_identity(_: &Path) -> Option<()> {
    None
}

// ---------------------------------------------------------------------------
// Profiles in one round.

/// One profile to sync: where its usage file goes and which login it reads.
#[derive(Debug, Clone)]
struct Slot {
    id: String,
    /// `None` for the default profile.
    root: Option<PathBuf>,
    /// The default's resolved dir; an added profile's preferred dir
    /// (`<profiles dir>\<UUID>-<root digest>`, resolved at commit), or `None` when the
    /// local storage could not be resolved.
    dir: Option<PathBuf>,
}

/// The default profile first, then the added ones in config order. Empty
/// without a default dir (never enabled since launch, or turned off).
fn slots(config: &Config) -> Vec<Slot> {
    let Some(dir) = config.dir.clone() else {
        return Vec::new();
    };
    let mut slots = vec![Slot {
        id: DEFAULT_PROFILE_ID.to_string(),
        root: None,
        dir: Some(dir),
    }];
    slots.extend(config.profiles.iter().map(|p| Slot {
        id: p.id.clone(),
        root: Some(p.root.clone()),
        dir: config.profiles_dir.as_ref().map(|parent| parent.join(profile_dir_name(p))),
    }));
    slots
}

impl Slot {
    /// Still configured exactly as when the round started (WB4): sync on,
    /// the same default dir, and for an added profile the same resolved
    /// profiles dir and the same `(id, root)` still listed. The default
    /// profile does not depend on the added ones: adding the first or
    /// removing the last (which sets or clears `profiles_dir`) leaves its
    /// walk alone.
    fn still_in(&self, start: &Config, now: &Config) -> bool {
        now.enabled
            && now.dir == start.dir
            && self.profile().is_none_or(|me| {
                now.profiles_dir == start.profiles_dir
                    && now.profiles.iter().any(|p| p.same_slot(&me))
            })
    }

    /// An added profile's slot as the profile it syncs; `None` for the
    /// default.
    fn profile(&self) -> Option<Profile> {
        self.root.clone().map(|root| Profile {
            id: self.id.clone(),
            root,
        })
    }

    /// How the budget remembers a slot: the same id and, for added
    /// profiles, `same_root` (the check `Profile::same_slot` makes, without
    /// building a `Profile`); the default matches only the default; the dir
    /// is not compared. So a respelling of the same folder is the same slot.
    fn same_slot(&self, other: &Slot) -> bool {
        self.id == other.id
            && match (&self.root, &other.root) {
                (Some(mine), Some(theirs)) => same_root(mine, theirs),
                (None, None) => true,
                _ => false,
            }
    }

    /// Where this profile's files can be: the default's one resolved dir
    /// (as every release); an added profile's dir and its `.secure` sibling
    /// (WB8), since it is resolved only when a walk commits.
    fn candidates(&self) -> Vec<PathBuf> {
        match (&self.root, &self.dir) {
            (_, None) => Vec::new(),
            (None, Some(dir)) => vec![dir.clone()],
            (Some(_), Some(dir)) => {
                let mut fallback = dir.clone().into_os_string();
                fallback.push(secure::FALLBACK_SUFFIX);
                vec![dir.clone(), PathBuf::from(fallback)]
            }
        }
    }

    /// The newest complete file across the candidates.
    fn complete_file(&self) -> Option<(PathBuf, SystemTime)> {
        self.candidates()
            .iter()
            .filter_map(|dir| complete_file(dir))
            .max_by_key(|(_, modified)| *modified)
    }
}

// ---------------------------------------------------------------------------
// Takeover (Core rule + D6 ownership guard).

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) enum Takeover {
    #[default]
    Off,
    /// CLI Cursor files exist and the user has not confirmed: `cliPresent`.
    Blocked,
    On {
        cli_root: PathBuf,
        /// One file per account, never a directory (item 7).
        files: Vec<PathBuf>,
    },
}

pub(crate) fn takeover(config: &Config, home: Option<&Path>) -> Takeover {
    let (true, Some(home)) = (config.enabled, home) else {
        return Takeover::Off;
    };
    let files = newest_per_account(config);
    if files.is_empty() {
        return Takeover::Off;
    }
    let cli_root = cli_root(home);
    // D6: the CLI's files are the user's; never hide them unasked.
    if cli_has_cursor_files(&cli_root) && !config.cli_takeover_confirmed {
        return Takeover::Blocked;
    }
    Takeover::On { cli_root, files }
}

/// Every complete sync file across the current config's slots (WB8: a
/// removed profile's leftovers are never read), grouped by file name
/// (`usage.<HMAC(sub)>.json`, one name per account), keeping the newest copy
/// of each. A stale copy elsewhere (an account switch whose delete failed)
/// is never returned.
fn newest_per_account(config: &Config) -> Vec<PathBuf> {
    let mut newest: std::collections::BTreeMap<String, (SystemTime, PathBuf)> =
        std::collections::BTreeMap::new();
    for slot in slots(config) {
        for dir in slot.candidates() {
            for (path, modified) in sync_files(&dir) {
                let name = path
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .into_owned();
                // Strictly newer: on a tie the earlier profile (default first) wins.
                if newest.get(&name).is_none_or(|(seen, _)| modified > *seen) {
                    newest.insert(name, (modified, path));
                }
            }
        }
    }
    newest.into_values().map(|(_, path)| path).collect()
}

/// The takeover the current registry and files call for. File I/O; the
/// caller holds no sync lock (V-2b).
pub(crate) fn current_takeover(home: Option<&Path>) -> Takeover {
    takeover(&config(), home)
}

/// Merge the takeover into settings already holding the registry's extra
/// paths (e.g. Claude's), never replacing them. Only file paths are
/// registered, never `cursor-cache`, `cursor-profiles` or a profile dir; the
/// engine walks a file root as that single file (measured, CMP-W U2).
pub(crate) fn apply_takeover(
    settings: &mut tokscale_core::scanner::ScannerSettings,
    takeover: &Takeover,
) {
    if let Takeover::On { cli_root, files } = takeover {
        settings
            .extra_scan_paths
            .entry("cursor".to_string())
            .or_default()
            .extend(files.iter().cloned());
        settings
            .excluded_scan_paths
            .entry("cursor".to_string())
            .or_default()
            .push(cli_root.clone());
    }
}

/// Any `usage*.csv` / `usage*.json` anywhere under the CLI root (depth ≤ 4),
/// including files the engine would skip (archive, backups): the ownership
/// guard errs toward asking. An unreadable root counts as present for the
/// same reason.
fn cli_has_cursor_files(root: &Path) -> bool {
    fn walk(dir: &Path, depth: u32) -> bool {
        let entries = match std::fs::read_dir(dir) {
            Ok(entries) => entries,
            Err(error) => return error.kind() != io::ErrorKind::NotFound,
        };
        entries.flatten().any(|entry| {
            let Ok(kind) = entry.file_type() else {
                return true;
            };
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if kind.is_dir() {
                return depth < 4 && walk(&entry.path(), depth + 1);
            }
            name.starts_with("usage") && (name.ends_with(".csv") || name.ends_with(".json"))
        })
    }
    walk(root, 0)
}

fn is_sync_file_name(name: &str) -> bool {
    name.strip_prefix("usage.")
        .and_then(|rest| rest.strip_suffix(".json"))
        .is_some_and(|hex| hex.len() == 64 && hex.bytes().all(|b| b.is_ascii_hexdigit()))
}

/// Every complete synced file in `dir` (a real directory), with its mtime.
/// Only complete walks ever create one, so existence is completeness.
fn sync_files(dir: &Path) -> Vec<(PathBuf, SystemTime)> {
    if !std::fs::symlink_metadata(dir).is_ok_and(|m| m.is_dir()) {
        return Vec::new();
    }
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    entries
        .flatten()
        .filter(|entry| is_sync_file_name(&entry.file_name().to_string_lossy()))
        .filter_map(|entry| {
            let meta = std::fs::symlink_metadata(entry.path()).ok()?;
            let modified = meta.modified().ok()?;
            meta.is_file().then(|| (entry.path(), modified))
        })
        .collect()
}

/// The newest complete synced file in `dir`, with its mtime.
fn complete_file(dir: &Path) -> Option<(PathBuf, SystemTime)> {
    sync_files(dir)
        .into_iter()
        .max_by_key(|(_, modified)| *modified)
}

// ---------------------------------------------------------------------------
// Files (W2). Every operation goes through `agent_storage_windows`.

#[cfg(windows)]
mod secure {
    use crate::agent_storage_windows as storage;
    use std::fs::File;
    use std::io;
    use std::path::{Path, PathBuf};

    pub(super) const FALLBACK_SUFFIX: &str = storage::SECURE_STORAGE_FALLBACK_SUFFIX;

    pub(super) fn resolve_dir(preferred: &Path) -> io::Result<PathBuf> {
        storage::resolve_secure_storage_directory(preferred)
    }

    pub(super) fn ensure_dir(path: &Path) -> io::Result<File> {
        storage::ensure_secure_storage_directory(path)
    }

    pub(super) fn open_lock(path: &Path) -> io::Result<File> {
        storage::open_secure_lock_file(path)
    }

    pub(super) fn create_new(path: &Path) -> io::Result<File> {
        storage::create_new_secure_file(path)
    }

    /// Commit point. Flushes the directory itself on success.
    pub(super) fn replace(directory: &File, dir: &Path, staged: &Path, dest: &Path) -> io::Result<()> {
        storage::replace_secure_file(directory, dir, staged, dest)
    }

    /// Whether the Syrtis file at `path` (meeting the storage contract)
    /// already holds exactly `bytes`. If so its modified time is set to now,
    /// on the same handle, so it still dates the last complete walk
    /// (`lastSuccessMs`, the newest copy per account) without a rewrite:
    /// `Some(true)`, or `Some(false)` when that time could not be set (the
    /// caller rewrites the same bytes to move it). `None`: different bytes,
    /// or the file could not be read through the contract.
    pub(super) fn refresh_if_same(path: &Path, bytes: &[u8]) -> Option<bool> {
        use std::io::Read as _;
        let mut file = storage::open_existing_secure_file(path, true).ok()?;
        storage::verify_secure_file_path(&file, path).ok()?;
        if file.metadata().ok()?.len() != bytes.len() as u64 {
            return None;
        }
        let mut old = Vec::new();
        file.read_to_end(&mut old).ok()?;
        if old != bytes {
            return None;
        }
        // The test fault stands in for the call (the time is then not set),
        // so the production expression below is what decides.
        #[cfg(test)]
        let touched = if super::TOUCH_FAILS_FOR_TEST.with(std::cell::Cell::get) {
            Err(io::Error::other("injected touch failure"))
        } else {
            file.set_modified(std::time::SystemTime::now())
        };
        #[cfg(not(test))]
        let touched = file.set_modified(std::time::SystemTime::now());
        Some(touched.is_ok())
    }

    /// Delete one Syrtis file by pathname, only if it is a regular file that
    /// meets the storage contract and is still the object just opened.
    /// `DeleteFileW` on a reparse point would remove the link itself, but the
    /// open refuses a reparse point before that. A failing file is left.
    pub(super) fn remove_file(path: &Path) -> io::Result<()> {
        let file = storage::open_existing_secure_file(path, false)?;
        storage::verify_secure_file_path(&file, path)?;
        std::fs::remove_file(path)
    }
}

/// Non-Windows builds have no secure storage here: sync cannot write.
#[cfg(not(windows))]
mod secure {
    use std::fs::File;
    use std::io;
    use std::path::{Path, PathBuf};

    pub(super) const FALLBACK_SUFFIX: &str = ".secure";

    fn unsupported() -> io::Error {
        io::Error::from(io::ErrorKind::Unsupported)
    }
    pub(super) fn resolve_dir(_: &Path) -> io::Result<PathBuf> {
        Err(unsupported())
    }
    pub(super) fn ensure_dir(_: &Path) -> io::Result<File> {
        Err(unsupported())
    }
    pub(super) fn open_lock(_: &Path) -> io::Result<File> {
        Err(unsupported())
    }
    pub(super) fn create_new(_: &Path) -> io::Result<File> {
        Err(unsupported())
    }
    pub(super) fn replace(_: &File, _: &Path, _: &Path, _: &Path) -> io::Result<()> {
        Err(unsupported())
    }
    pub(super) fn refresh_if_same(_: &Path, _: &[u8]) -> Option<bool> {
        None
    }
    pub(super) fn remove_file(_: &Path) -> io::Result<()> {
        Err(unsupported())
    }
}

/// Exactly our file names: `usage.<64 hex>.json` and our temp prefix. The
/// leftover `LEGACY_ROOT_FILE_NAME` is not counted here: `clean_candidate`
/// removes it best-effort with the lock.
fn is_ours(name: &str) -> bool {
    is_sync_file_name(name) || name.starts_with(TEMP_FILE_PREFIX)
}

/// What `remove_usage_files` did: usage files and orphan temps removed, our
/// names that could not be deleted (a file failing the storage contract is
/// never deleted), and whether the listing held the `keep` file.
#[derive(Debug, Default)]
struct Removal {
    usage: usize,
    temps: usize,
    left: usize,
    kept: bool,
}

impl Removal {
    /// Files removed, or an error when one of ours was left.
    fn complete(self) -> io::Result<usize> {
        if self.left > 0 {
            Err(io::Error::other("a Syrtis usage file could not be deleted"))
        } else {
            Ok(self.usage + self.temps)
        }
    }
}

/// Delete Syrtis usage files and temp leftovers in `dir` except `keep`.
/// Never touches anything else. Caller holds the dir lock.
/// An entry the listing cannot read counts in `left` and the listing goes
/// on, so files removed before it still count; only a dir that cannot be
/// listed at all is an `Err` (nothing removed).
fn remove_usage_files(dir: &Path, keep: Option<&str>) -> io::Result<Removal> {
    let mut removal = Removal::default();
    let entries = std::fs::read_dir(dir)?;
    #[cfg(test)]
    let entries = entries.chain(
        LISTING_ERROR_FOR_TEST
            .with(std::cell::Cell::get)
            .then(|| Err(io::Error::other("injected listing error"))),
    );
    for entry in entries {
        let Ok(entry) = entry else {
            removal.left += 1;
            continue;
        };
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if Some(name.as_ref()) == keep {
            removal.kept = true;
            continue;
        }
        if !is_ours(&name) {
            continue;
        }
        match secure::remove_file(&entry.path()) {
            Ok(()) if is_sync_file_name(&name) => removal.usage += 1,
            Ok(()) => removal.temps += 1,
            Err(_) => removal.left += 1,
        }
    }
    Ok(removal)
}

/// Disable-time cleanup under the secure lock. Any file left is an error.
#[cfg(test)]
fn remove_usage_files_locked(dir: &Path) -> io::Result<usize> {
    let _lock = secure::open_lock(&dir.join(LOCK_FILE_NAME))?;
    remove_usage_files(dir, None)?.complete()
}

static TEMP_COUNTER: AtomicU64 = AtomicU64::new(0);

fn temp_file_name() -> String {
    format!(
        "{TEMP_FILE_PREFIX}{}-{}",
        std::process::id(),
        TEMP_COUNTER.fetch_add(1, Ordering::Relaxed)
    )
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct UsageFile<'a> {
    total_usage_events_count: usize,
    usage_events_display: &'a [Event],
}

/// Write the complete walk: secure dir, secure lock, secure temp, flush,
/// secure replace (the commit point; it flushes the directory), then drop
/// every other usage file and orphan temp. `still_wanted` is checked under
/// the lock, so a concurrent disable or profile removal (which deletes under
/// the same lock) can never be undone by a walk finishing after it: no usage
/// file is written for a profile no longer wanted. It is also checked before
/// anything is created, which keeps a profile removed earlier (during its
/// last page, say) from creating its dir; that check is not atomic with the
/// creation, so a removal racing it can leave an empty dir and lock file,
/// cleaned at the next accepted push. `resolve`: an added profile's dir,
/// resolved (and created) only now. Returns whether a usage file's content
/// changed (`Commit::changed`): a file whose bytes already equal the new ones
/// is not rewritten (only its modified time moves to now; if that fails the
/// same bytes are rewritten to move it, still no change), so an identical
/// walk changes nothing (finding 8) unless a superseded usage file was
/// removed (an orphan temp does not count); and how many of our files it
/// could not delete (`Commit::left`, reported as `cleanupFailedProfiles`).
fn commit_file(
    dir: &Path,
    resolve: bool,
    stem: &str,
    events: &[Event],
    still_wanted: &dyn Fn() -> bool,
) -> Result<Commit, Stop> {
    let failed = || Stop::error("write_failed");
    if !still_wanted() {
        return Err(Stop::new(State::Disabled, None));
    }
    let resolved;
    let dir = if resolve {
        resolved = secure::resolve_dir(dir).map_err(|_| failed())?;
        resolved.as_path()
    } else {
        dir
    };
    let directory = secure::ensure_dir(dir).map_err(|_| failed())?;
    let _lock = secure::open_lock(&dir.join(LOCK_FILE_NAME)).map_err(|_| failed())?;
    if !still_wanted() {
        return Err(Stop::new(State::Disabled, None));
    }
    let payload = serde_json::to_vec(&UsageFile {
        total_usage_events_count: events.len(),
        usage_events_display: events,
    })
    .map_err(|_| failed())?;
    let final_name = format!("usage.{stem}.json");
    // Read only through the storage contract: a file that fails it is
    // replaced, never compared.
    let same = secure::refresh_if_same(&dir.join(&final_name), &payload);
    if same != Some(true) {
        write_file(&directory, dir, &final_name, &payload).map_err(|_| failed())?;
    }
    // Superseded files and orphan temps go. A Syrtis-named file that cannot
    // be deleted (one failing the storage contract is never deleted), or a
    // dir that cannot be listed, is a cleanup failure, so the round reports
    // this profile in `cleanupFailedProfiles`: a superseded account's file
    // left there would still be registered.
    let (removed, cleanup_failed) = match remove_usage_files(dir, Some(&final_name)) {
        Ok(removal) => (removal.usage, removal.left > 0),
        Err(_) => (0, true),
    };
    Ok(Commit {
        changed: same.is_none() || removed > 0,
        cleanup_failed,
        dir: dir.to_path_buf(),
    })
}

/// What `commit_file` did besides writing: whether a usage file's content
/// changed, whether one of our files could not be deleted (or the dir could
/// not be listed), and the dir it committed into (the resolved one).
struct Commit {
    changed: bool,
    cleanup_failed: bool,
    dir: PathBuf,
}

/// Secure temp, flush, secure replace (it flushes the directory); the temp
/// is removed on failure.
fn write_file(directory: &std::fs::File, dir: &Path, name: &str, bytes: &[u8]) -> io::Result<()> {
    let temp_path = dir.join(temp_file_name());
    let written = (|| {
        let mut file = secure::create_new(&temp_path)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        secure::replace(directory, dir, &temp_path, &dir.join(name))
    })();
    if written.is_err() {
        let _ = secure::remove_file(&temp_path);
    }
    written
}

// ---------------------------------------------------------------------------
// Wire types: exactly the fields the engine's Cursor JSON parser reads
// (tokscale-core `sessions/cursor.rs`, `CursorUsageEvent` /
// `CursorTokenUsage`, pin 8fc63ced), S-6. Everything else — owningUser,
// serviceAccountId, subscriptionProductId, customSubscriptionName, kind,
// cost breakdowns, flags — is dropped by construction.

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Page {
    usage_events_display: Vec<Event>,
    #[serde(default)]
    total_usage_events_count: Option<u64>,
}

/// The event timestamp: Unix ms as a string (observed) or a number; the
/// engine accepts both.
#[derive(Deserialize, Serialize)]
#[serde(untagged)]
enum Timestamp {
    Text(String),
    Number(serde_json::Number),
}

/// A count or cents value. The engine coerces a number or a numeric string,
/// so both are accepted here; anything else (an object, a non-numeric
/// string) fails the page rather than carrying free text into the file.
/// Written back as a JSON number.
#[derive(Serialize)]
#[serde(transparent)]
struct Num(serde_json::Number);

impl<'de> Deserialize<'de> for Num {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        use serde::de::Error as _;
        match serde_json::Value::deserialize(deserializer)? {
            serde_json::Value::Number(number) => Ok(Num(number)),
            serde_json::Value::String(text) => text
                .trim()
                .parse::<f64>()
                .ok()
                .and_then(serde_json::Number::from_f64)
                .map(Num)
                .ok_or_else(|| D::Error::custom("not a number")),
            _ => Err(D::Error::custom("not a number")),
        }
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct TokenUsage {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    input_tokens: Option<Num>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    output_tokens: Option<Num>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    cache_read_tokens: Option<Num>,
    /// Read by the engine (`cache_write`), though absent on many events.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    cache_write_tokens: Option<Num>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    total_cents: Option<Num>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Event {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    conversation_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    timestamp: Option<Timestamp>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    model: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    charged_cents: Option<Num>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    token_usage: Option<TokenUsage>,
}

// ---------------------------------------------------------------------------
// Outcome.

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) enum State {
    Ok,
    Partial,
    Expired,
    NotSignedIn,
    Offline,
    Error,
    Disabled,
    CliPresent,
    /// The account was already covered this round by an earlier profile.
    Duplicate,
}

/// Every `reason` is a fixed literal: nothing from the token, the account,
/// the response or the filesystem can reach a status string.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Stop {
    state: State,
    reason: Option<&'static str>,
}

impl Stop {
    fn new(state: State, reason: Option<&'static str>) -> Self {
        Self { state, reason }
    }
    fn error(reason: &'static str) -> Self {
        Self::new(State::Error, Some(reason))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Outcome {
    stop: Stop,
    events: usize,
    /// For `Duplicate`: the index (in this round's slots) of the profile that
    /// already covered the same account. Never the account itself.
    duplicate_of: Option<usize>,
    /// A usage file in this profile's dirs was written with new bytes or
    /// removed.
    changed: bool,
    /// One of our files in this profile's dirs could not be deleted: by a
    /// duplicate's cleanup, or a superseded file after a commit (reported in
    /// `cleanupFailedProfiles`).
    cleanup_failed: bool,
    /// Stopped by a refusal decided where it arose, so no list of codes can
    /// miss one: every `Err` a login reader returns (all its refusals; no
    /// login is `Ok(None)`), a token that cannot be read or does not match
    /// its account, no installation key, no storage for the profile.
    refused: bool,
}

impl Outcome {
    fn stopped(stop: Stop) -> Self {
        Self {
            stop,
            events: 0,
            duplicate_of: None,
            changed: false,
            cleanup_failed: false,
            refused: false,
        }
    }

    fn refused(stop: Stop) -> Self {
        Self {
            refused: true,
            ..Self::stopped(stop)
        }
    }
}

/// Outcomes that write no file however long the round runs: a duplicate, no
/// login, an expired session, or a refusal (`Outcome::refused`). A slot
/// whose last round ended so does not ask for the full budget while it has
/// no file (finding 1); a timeout, an outage or a spent budget does.
fn writes_no_file(outcome: &Outcome) -> bool {
    outcome.refused
        || matches!(
            outcome.stop.state,
            State::Duplicate | State::NotSignedIn | State::Expired
        )
}

// ---------------------------------------------------------------------------
// The walk.

#[derive(Clone, Copy)]
struct Limits {
    budget: Duration,
    page_timeout: Duration,
    page_size: u32,
    max_pages: u32,
    max_body_bytes: usize,
}

impl Limits {
    fn production(budget: Duration) -> Self {
        Self {
            budget,
            page_timeout: PAGE_TIMEOUT,
            page_size: PAGE_SIZE,
            max_pages: MAX_PAGES,
            max_body_bytes: MAX_BODY_BYTES,
        }
    }
}

/// Everything one sync depends on. Production builds it from constants;
/// tests inject a local server, a fixture database, a fixed key and a drive
/// type.
struct Target<'a> {
    /// The default profile's database; added profiles derive their own.
    db_path: &'a Path,
    url: &'a str,
    https_only: bool,
    file_stem: &'a dyn Fn(&str) -> Option<String>,
    /// `GetDriveTypeW` for added profiles' roots (WB2).
    drive_type: &'a DriveType<'a>,
    /// The user's Cursor encrypted-sign-in consent (production:
    /// `keychain_consent::allowed("cursor")`), re-read immediately before a
    /// sealed token's `Local State` is opened — never sampled at round start
    /// (C2). A plaintext token never consults it (Design 1).
    consent: &'a dyn Fn() -> bool,
    /// Unwraps the safeStorage key from a user-data-dir's `Local State`
    /// (production: `load_seal_key`). The only reader of `Local State` and the
    /// DPAPI key; called at most once per sealed profile, only after consent,
    /// no cache (U3). Tests inject a key straight from raw bytes (DPAPI seam).
    load_seal_key: &'a LoadSealKey<'a>,
}

/// An unwrapped safeStorage key that decrypts one Electron v10 blob. The
/// production key wipes itself on drop (`win_safe_storage::SafeStorageKey`);
/// the returned plaintext is the caller's to wipe.
pub(crate) trait SealKey {
    /// Decrypt a decoded `"v10" || nonce || ct || tag` blob to its plaintext
    /// bytes. `Err(())` for any failure (wrong key, length, tampering): the
    /// caller maps it to one fixed reason, so no detail leaks (T5).
    fn decrypt(&self, blob: &[u8]) -> Result<Vec<u8>, ()>;
}

/// Reads a user-data-dir's `Local State` and unwraps its key. `Err(())` maps
/// to `token_sealed_unreadable`; the real error text (which can name a file
/// path) is never carried through (T5).
type LoadSealKey<'a> = dyn Fn(&Path) -> Result<Box<dyn SealKey>, ()> + 'a;

#[cfg(windows)]
impl SealKey for crate::win_safe_storage::SafeStorageKey {
    fn decrypt(&self, blob: &[u8]) -> Result<Vec<u8>, ()> {
        use base64::Engine as _;
        // ponytail: re-encode to reuse the audited `win_safe_storage::decrypt`
        // (base64 in) rather than widen its crypto API; a token is a few
        // hundred bytes and this runs once per sealed profile per round.
        let b64 = base64::engine::general_purpose::STANDARD.encode(blob);
        crate::win_safe_storage::decrypt(self, &b64).map_err(|_| ())
    }
}

/// Production loader: `<user-data-dir>\Local State` → the safeStorage key.
#[cfg(windows)]
fn load_seal_key(local_state: &Path) -> Result<Box<dyn SealKey>, ()> {
    let raw = std::fs::read(local_state).map_err(|_| ())?;
    let json: serde_json::Value = serde_json::from_slice(&raw).map_err(|_| ())?;
    let key = crate::win_safe_storage::load_key(&json).map_err(|_| ())?;
    Ok(Box::new(key))
}

/// Off Windows there is no safeStorage reader; a sealed token is refused by
/// `stored_token` before this is reached, so the loader is only a stub.
#[cfg(not(windows))]
fn load_seal_key(_local_state: &Path) -> Result<Box<dyn SealKey>, ()> {
    Err(())
}

/// The user-data-dir whose `Local State` holds the key for `slot`'s database
/// (C6): an added profile's root is its own user-data-dir; the default
/// profile's is `db_path`'s `…\Cursor` (three levels up from
/// `…\Cursor\User\globalStorage\state.vscdb`). `Local State` sits directly in
/// it (`%APPDATA%\Cursor\Local State` by default).
#[cfg(windows)]
fn local_state_path(target: &Target<'_>, slot: &Slot) -> Option<PathBuf> {
    let user_data_dir = match &slot.root {
        Some(root) => root.clone(),
        None => target
            .db_path
            .parent()
            .and_then(Path::parent)
            .and_then(Path::parent)?
            .to_path_buf(),
    };
    Some(user_data_dir.join("Local State"))
}

/// The shortest possible v10 blob: `"v10"` || nonce(12) || tag(16), with an
/// empty ciphertext.
#[cfg(windows)]
const V10_MIN_LEN: usize = 3 + 12 + 16;

/// Open one profile's sealed token (S3b item 1). Order: the decoded blob's
/// shape, in one place and with no I/O — a 3-byte-or-longer blob that is not
/// `v10` is `token_sealed_unsupported`; anything shorter than a v10 blob can
/// be (fewer than 3 bytes included) is `token_sealed_unreadable` (T10); then
/// the consent check, immediately before `Local State` is opened (C2); the key
/// load (the only `Local State`/DPAPI read); the AES-GCM decrypt; the decrypt
/// output wiped on every path, UTF-8 failure included (C8). Windows only;
/// `stored_token` never yields `Sealed` elsewhere.
#[cfg(windows)]
fn open_sealed(blob: &[u8], target: &Target<'_>, slot: &Slot) -> Result<String, Stop> {
    let refuse = Stop::error;
    if blob.len() >= 3 && !blob.starts_with(b"v10") {
        return Err(refuse("token_sealed_unsupported"));
    }
    if blob.len() < V10_MIN_LEN {
        return Err(refuse("token_sealed_unreadable"));
    }
    // C2: read consent here, not at round start; a withdrawal before this
    // point stops the read, and `Local State` is not opened.
    if !(target.consent)() {
        return Err(refuse("keychain_consent"));
    }
    let local_state = local_state_path(target, slot).ok_or(refuse("token_sealed_unreadable"))?;
    let key = (target.load_seal_key)(&local_state).map_err(|_| refuse("token_sealed_unreadable"))?;
    let mut plaintext = key.decrypt(blob).map_err(|_| refuse("token_sealed_unreadable"))?;
    drop(key); // SafeStorageKey wipes the key on drop (C8).
    // One exact-size copy (`to_owned` allocates the string's length); the
    // decrypt output is wiped whether it was valid UTF-8 or not (C8). The
    // copy goes straight into the login, which `run` holds in a `WipedLogin`.
    let token = std::str::from_utf8(&plaintext).ok().map(str::to_owned);
    crate::win_safe_storage::wipe(&mut plaintext);
    token.ok_or(refuse("token_sealed_unreadable"))
}

/// The login while `run` holds it: the access token String (plaintext as
/// stored, or the unsealed one) is wiped when this drops, so on every exit
/// of `run` after the token is classified (C8).
struct WipedLogin(CursorLogin);

impl Drop for WipedLogin {
    fn drop(&mut self) {
        cursor_desktop::wipe_str(&mut self.0.access_token);
    }
}

/// The cookie value String (it embeds the token), built at its final size so
/// it never reallocates (a reallocation would leave the old buffer
/// unwiped), and wiped when it drops.
struct WipedString(String);

impl Drop for WipedString {
    fn drop(&mut self) {
        cursor_desktop::wipe_str(&mut self.0);
    }
}

/// `WorkosCursorSessionToken=<user>%3A%3A<token>` in one allocation of
/// exactly its length plus one spare byte (the test-only bad-cookie fault
/// appends a `\n`).
fn cookie_value(user: &str, token: &str) -> WipedString {
    const PREFIX: &str = "WorkosCursorSessionToken=";
    const SEPARATOR: &str = "%3A%3A";
    let mut value = String::with_capacity(PREFIX.len() + user.len() + SEPARATOR.len() + token.len() + 1);
    value.push_str(PREFIX);
    value.push_str(user);
    value.push_str(SEPARATOR);
    value.push_str(token);
    WipedString(value)
}

#[cfg(not(windows))]
fn open_sealed(_blob: &[u8], target: &Target<'_>, _slot: &Slot) -> Result<String, Stop> {
    // No safeStorage reader off Windows. Touch the seams so they are not dead
    // fields on the macOS-host test build; neither is consulted here.
    let _ = (&target.consent, target.load_seal_key);
    Err(Stop::error("token_sealed_unsupported"))
}

fn now_secs() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs_f64())
        .unwrap_or(0.0)
}

/// The cookie user id: the part of the token's own `sub` after the last `|`.
/// Refuses (S-1) when a stored glass/profile id exists and differs.
fn token_user(login: &CursorLogin) -> Result<(String, f64), Stop> {
    let unreadable = Stop::error("token_unreadable");
    let claims = cursor_desktop::jwt_claims(&login.access_token).ok_or(unreadable)?;
    let sub = claims
        .get("sub")
        .and_then(serde_json::Value::as_str)
        .ok_or(unreadable)?;
    let user = sub.rsplit('|').next().unwrap_or_default();
    if user.is_empty() || !user.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_') {
        return Err(unreadable);
    }
    for stored in [&login.glass_user_id, &login.profile_user_id]
        .into_iter()
        .flatten()
    {
        if stored != user {
            return Err(Stop::error("account_mismatch"));
        }
    }
    // A missing or non-numeric `exp` cannot be shown to be valid: treat it
    // like an expired login rather than send it.
    let exp = claims
        .get("exp")
        .and_then(serde_json::Value::as_f64)
        .unwrap_or(0.0);
    Ok((user.to_string(), exp))
}

enum BodyError {
    TooLarge,
    Timeout,
    Transport,
}

async fn read_capped(response: &mut reqwest::Response, cap: usize) -> Result<Vec<u8>, BodyError> {
    if response
        .content_length()
        .is_some_and(|length| length > cap as u64)
    {
        return Err(BodyError::TooLarge);
    }
    let mut body = Vec::new();
    loop {
        match response.chunk().await {
            Ok(Some(chunk)) => {
                if body.len() + chunk.len() > cap {
                    return Err(BodyError::TooLarge);
                }
                body.extend_from_slice(&chunk);
            }
            Ok(None) => return Ok(body),
            Err(error) if error.is_timeout() => return Err(BodyError::Timeout),
            Err(_) => return Err(BodyError::Transport),
        }
    }
}

async fn walk(
    target: &Target<'_>,
    cookie: &reqwest::header::HeaderValue,
    limits: Limits,
    enabled: &dyn Fn() -> bool,
) -> Result<Vec<Event>, Stop> {
    use reqwest::header;
    let client = crate::agent_usage::provider_http_client_builder()
        .redirect(reqwest::redirect::Policy::none())
        .https_only(target.https_only)
        .build()
        .map_err(|_| Stop::error("client_unavailable"))?;
    let end_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    let deadline = Instant::now() + limits.budget;
    let mut events: Vec<Event> = Vec::new();
    let mut bytes_read = 0usize;
    for page in 1..=limits.max_pages {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(Stop::new(State::Partial, Some("budget_exhausted")));
        }
        // S-3: the switch is re-read before every send.
        if !enabled() {
            return Err(Stop::new(State::Disabled, None));
        }
        let body = serde_json::json!({
            "startDate": "0",
            "endDate": end_ms.to_string(),
            "page": page,
            "pageSize": limits.page_size,
        });
        let sent = client
            .post(target.url)
            .header(header::COOKIE, cookie.clone())
            .header(header::ORIGIN, "https://cursor.com")
            .header(header::REFERER, "https://cursor.com/dashboard")
            .header(header::CONTENT_TYPE, "application/json")
            .header(header::ACCEPT, "application/json")
            .header(header::USER_AGENT, "TokenBar")
            .body(body.to_string())
            .timeout(limits.page_timeout.min(remaining))
            .send()
            .await;
        let mut response = match sent {
            Ok(response) => response,
            Err(error) if error.is_timeout() => {
                return Err(Stop::new(State::Partial, Some("timeout")))
            }
            Err(_) => return Err(Stop::new(State::Offline, Some("unreachable"))),
        };
        let status = response.status();
        if status.is_redirection() || status.as_u16() == 401 {
            return Err(Stop::new(State::Expired, None));
        }
        if status.as_u16() == 403 {
            // P3-4: an auth refusal is JSON; an HTML wall (bot check, outage
            // page) is not a sign-in problem and must not tell the user to
            // sign in again.
            let json = read_capped(&mut response, MAX_ERROR_BODY_BYTES)
                .await
                .ok()
                .is_some_and(|b| serde_json::from_slice::<serde_json::Value>(&b).is_ok());
            return Err(if json {
                Stop::new(State::Expired, None)
            } else {
                Stop::error("forbidden_non_json")
            });
        }
        if status.as_u16() == 429 {
            // A maintainer stop condition (plan B3 stops), distinct on purpose.
            return Err(Stop::error("rate_limited"));
        }
        if status.is_server_error() {
            return Err(Stop::error("server_error"));
        }
        if !status.is_success() {
            return Err(Stop::error("http_status"));
        }
        let cap = limits.max_body_bytes.saturating_sub(bytes_read);
        let body = match read_capped(&mut response, cap).await {
            Ok(body) => body,
            Err(BodyError::TooLarge) => return Err(Stop::error("body_too_large")),
            Err(BodyError::Timeout) => return Err(Stop::new(State::Partial, Some("timeout"))),
            Err(BodyError::Transport) => {
                return Err(Stop::new(State::Offline, Some("unreachable")))
            }
        };
        bytes_read += body.len();
        let page: Page =
            serde_json::from_slice(&body).map_err(|_| Stop::error("unexpected_response"))?;
        let received = page.usage_events_display.len();
        events.extend(page.usage_events_display);
        let reached_total = page
            .total_usage_events_count
            .is_some_and(|total| events.len() as u64 >= total);
        if received < limits.page_size as usize || reached_total {
            return Ok(events);
        }
    }
    Err(Stop::error("too_many_pages"))
}

/// A walk failure covers the account for the round (a second session would
/// only repeat it: 429 amplification), except one specific to this
/// profile's session or to this machine: expired (local expiry, 401,
/// redirect, 403 JSON), disabled (removed mid-walk), client_unavailable,
/// write_failed.
fn covers_account(stop: Stop) -> bool {
    !matches!(stop.state, State::Expired | State::Disabled)
        && !matches!(stop.reason, Some("client_unavailable" | "write_failed"))
}

/// One profile's sync, start to finish. `enabled` is the live switch (and,
/// in production, "this profile is still configured as when the round
/// started"). `claim(stem, false)` returns the index of an earlier profile
/// that already covered the same account this round; `claim(stem, true)`
/// records this one: after its file is committed, or after a walk failure
/// that is not this session's own (`covers_account`).
async fn run(
    target: &Target<'_>,
    slot: &Slot,
    dir: &Path,
    limits: Limits,
    enabled: &dyn Fn() -> bool,
    claim: &mut dyn FnMut(&str, bool) -> Option<usize>,
) -> Outcome {
    let stopped = Outcome::stopped;
    if !enabled() {
        return stopped(Stop::new(State::Disabled, None));
    }
    let read = match &slot.root {
        None => cursor_desktop::read_login_default(target.db_path),
        Some(root) => cursor_desktop::read_login_added(root, target.drive_type),
    };
    let mut login = match read {
        Ok(Some(login)) => login,
        Ok(None) => return stopped(Stop::new(State::NotSignedIn, None)),
        Err(reason) => return Outcome::refused(Stop::error(reason)),
    };
    #[cfg(test)]
    if PANIC_AFTER_LOGIN.with(std::cell::Cell::get) {
        panic!("injected after the login was read");
    }
    #[cfg(test)]
    run_after_login_hook();
    // A sealed token is opened before anything reads it; a plaintext one is
    // used as stored, with no consent lookup and no key load (Design 1). The
    // classification never decrypts (C1).
    match cursor_desktop::stored_token(&login.access_token) {
        cursor_desktop::StoredToken::Plain => {}
        cursor_desktop::StoredToken::Sealed(blob) => match open_sealed(&blob, target, slot) {
            Ok(token) => login.access_token = token,
            Err(stop) => return Outcome::refused(stop),
        },
        cursor_desktop::StoredToken::Refused(reason) => {
            return Outcome::refused(Stop::error(reason))
        }
    }
    // From here every exit wipes the token (C8).
    let login = WipedLogin(login);
    let (user, exp) = match token_user(&login.0) {
        Ok(found) => found,
        Err(stop) => return Outcome::refused(stop),
    };
    // Cursor refreshes the token only while it runs, and an expired one is
    // answered with a redirect to WorkOS. Decide locally; send nothing.
    if exp <= now_secs() + EXP_SKEW_SECS {
        return stopped(Stop::new(State::Expired, None));
    }
    let Some(stem) = (target.file_stem)(&user) else {
        return Outcome::refused(Stop::error("key_unavailable"));
    };
    // An account already covered this round by an earlier profile is not
    // walked again (saves requests, avoids 429); its file name is the same,
    // so the takeover keeps one copy. Any other account's file this profile
    // still holds (it was signed in to that one before) goes, under its
    // lock, the way a commit drops superseded files (finding 3).
    if let Some(first) = claim(&stem, false) {
        let keep = format!("usage.{stem}.json");
        // Usage files removed count as a change even when another one could
        // not be removed.
        let (mut removed, mut cleanup_failed) = (0, false);
        for dir in slot.candidates() {
            match clean_candidate(&dir, slot.root.is_some(), Some(&keep)) {
                Ok(removal) => {
                    removed += removal.usage;
                    cleanup_failed |= removal.left > 0;
                }
                Err(_) => cleanup_failed = true,
            }
        }
        return Outcome {
            stop: Stop::new(State::Duplicate, None),
            events: 0,
            duplicate_of: Some(first),
            changed: removed > 0,
            cleanup_failed,
            refused: false,
        };
    }
    #[cfg_attr(not(test), allow(unused_mut))]
    let mut value = cookie_value(&user, &login.0.access_token);
    #[cfg(test)]
    if BAD_COOKIE_FOR_TEST.with(std::cell::Cell::get) {
        value.0.push('\n');
    }
    let Ok(mut cookie) = reqwest::header::HeaderValue::from_str(&value.0) else {
        return Outcome::refused(Stop::error("token_unreadable"));
    };
    cookie.set_sensitive(true);
    drop(value);
    drop(login);
    let events = match walk(target, &cookie, limits, enabled).await {
        Ok(events) => events,
        Err(stop) => {
            if covers_account(stop) {
                claim(&stem, true);
            }
            return stopped(stop);
        }
    };
    drop(cookie);
    match commit_file(dir, slot.root.is_some(), &stem, &events, enabled) {
        Ok(commit) => {
            claim(&stem, true);
            // The profile's other candidate dir (an added profile's dir and
            // its `.secure` sibling; the commit cleaned the one it resolved)
            // can still hold an older account's file: it goes too, under that
            // dir's lock, or the profile is reported.
            let (mut changed, mut cleanup_failed) = (commit.changed, commit.cleanup_failed);
            for other in slot.candidates().into_iter().filter(|d| *d != commit.dir) {
                match clean_candidate(&other, slot.root.is_some(), None) {
                    Ok(removal) => {
                        changed |= removal.usage > 0;
                        cleanup_failed |= removal.left > 0;
                    }
                    Err(_) => cleanup_failed = true,
                }
            }
            Outcome {
                stop: Stop::new(State::Ok, None),
                events: events.len(),
                duplicate_of: None,
                changed,
                cleanup_failed,
                refused: false,
            }
        }
        Err(stop) => stopped(stop),
    }
}

// ---------------------------------------------------------------------------
// Entry point (single-flight).

/// What the last round left, behind the single-flight lock. A caller
/// arriving while a sync runs waits for it and returns its status instead of
/// starting another.
struct LastRound {
    status: Option<serde_json::Value>,
    /// The slots whose outcome writes no file (`writes_no_file`), compared
    /// with `Slot::same_slot`. A slot changed since (another root) is not in
    /// it, so its first round may take the full budget; a respelling is. The
    /// dir is not compared: a slot coming back from `storage_unavailable` with
    /// the same root is still in it, so in a background round its first walk
    /// gets the 60 s budget (and may end `partial`, completed under the full
    /// budget the round after). "Sync now", or another profile that still
    /// needs the full budget, gives that round 10 min.
    no_file: Vec<Slot>,
}

static IN_FLIGHT: Mutex<LastRound> = Mutex::new(LastRound {
    status: None,
    no_file: Vec::new(),
});

#[cfg(test)]
thread_local! {
    /// The budget the last round on this thread chose (finding 1's test).
    static ROUND_BUDGET: std::cell::Cell<Option<Duration>> = const { std::cell::Cell::new(None) };
}

/// The round's budget: full for "Sync now", and while some profile has no
/// complete file but could write one; background otherwise. A profile whose
/// last round could not write a file however long it ran (duplicate, not
/// signed in, refused) does not hold every later round at the full budget.
fn round_budget(
    explicit: bool,
    slots: &[Slot],
    files: &[Option<SystemTime>],
    no_file: &[Slot],
) -> Duration {
    let wants_full = slots
        .iter()
        .zip(files)
        .any(|(slot, file)| file.is_none() && !no_file.iter().any(|s| s.same_slot(slot)));
    if explicit || wants_full {
        FULL_BUDGET
    } else {
        BACKGROUND_BUDGET
    }
}

/// Run one sync round against Cursor, every profile. Returns the status JSON
/// and whether a synced file changed. The takeover recheck and the
/// `cliPresent` mapping are the caller's (`lib.rs`), after this returns,
/// because they take `ROOTS_SETTER`, which is never taken while `IN_FLIGHT`
/// is held (V-2b).
pub(crate) fn sync_now(explicit: bool) -> (serde_json::Value, bool) {
    let stem = |user: &str| {
        crate::agent_account_scope::installation_digest_hex(FILE_NAME_DOMAIN, user.as_bytes()).ok()
    };
    // No config dir: an empty path is never a file, so the default profile
    // reports `notSignedIn` without reading anything.
    let db_path = cursor_desktop::state_db_path().unwrap_or_default();
    sync_with(
        explicit,
        &Target {
            db_path: &db_path,
            url: USAGE_EVENTS_URL,
            https_only: true,
            file_stem: &stem,
            drive_type: &cursor_desktop::drive_type,
            consent: &|| crate::keychain_consent::allowed("cursor"),
            load_seal_key: &load_seal_key,
        },
        &config,
        None,
    )
}

/// The round's headline state for callers that show one line: `ok` when
/// every profile is `ok` or `duplicate`, else the first other state in
/// profile order (default first). A profile with no login (`notSignedIn`)
/// is not a problem while another profile synced.
fn headline(outcomes: &[Outcome]) -> Stop {
    let fine = |o: &Outcome| matches!(o.stop.state, State::Ok | State::Duplicate);
    let any_fine = outcomes.iter().any(fine);
    outcomes
        .iter()
        .find(|o| !fine(o) && !(any_fine && o.stop.state == State::NotSignedIn))
        .map(|o| o.stop)
        .unwrap_or(Stop::new(State::Ok, None))
}

fn sync_with(
    explicit: bool,
    target: &Target<'_>,
    config: &(dyn Fn() -> Config + Sync),
    test_limits: Option<Limits>,
) -> (serde_json::Value, bool) {
    let mut last = match IN_FLIGHT.try_lock() {
        Ok(last) => last,
        Err(std::sync::TryLockError::Poisoned(poisoned)) => poisoned.into_inner(),
        Err(std::sync::TryLockError::WouldBlock) => {
            let last = IN_FLIGHT.lock().unwrap_or_else(|p| p.into_inner());
            return (last.status.clone().unwrap_or(serde_json::Value::Null), false);
        }
    };
    let start = config();
    // Off is decided per profile, before its login is read (`run`).
    let profiles = slots(&start);
    // Each slot's newest complete file, read once (budget, per-profile and
    // top-level `lastSuccessMs`); read again below only for a slot whose
    // files this round touched.
    let mut files: Vec<Option<SystemTime>> = profiles
        .iter()
        .map(|p| p.complete_file().map(|(_, modified)| modified))
        .collect();
    let budget = round_budget(explicit, &profiles, &files, &last.no_file);
    #[cfg(test)]
    ROUND_BUDGET.with(|cell| cell.set(Some(budget)));
    let limits = test_limits.unwrap_or(Limits::production(budget));
    // One budget for the whole round, so several profiles cannot hold
    // IN_FLIGHT longer than one would. (A database read that blocks in the
    // kernel is not bounded by it; added profiles are on fixed drives only.)
    let deadline = Instant::now() + limits.budget;
    let mut walked: Vec<(String, usize)> = Vec::new();
    let mut outcomes: Vec<Outcome> = Vec::with_capacity(profiles.len());
    for (index, profile) in profiles.iter().enumerate() {
        let remaining = deadline.saturating_duration_since(Instant::now());
        let blocked = Outcome::stopped;
        let outcome = match &profile.dir {
            None => Outcome::refused(Stop::error("storage_unavailable")),
            Some(_) if remaining.is_zero() => {
                blocked(Stop::new(State::Partial, Some("budget_exhausted")))
            }
            Some(dir) => {
                let still_wanted = || profile.still_in(&start, &config());
                let mut claim = |stem: &str, record: bool| {
                    if record {
                        walked.push((stem.to_string(), index));
                        return None;
                    }
                    walked
                        .iter()
                        .find(|(s, _)| s == stem)
                        .map(|(_, first)| *first)
                };
                crate::RUNTIME.block_on(run(
                    target,
                    profile,
                    dir,
                    Limits {
                        budget: remaining,
                        ..limits
                    },
                    &still_wanted,
                    &mut claim,
                ))
            }
        };
        // A completed walk moved its file's time (rewritten or not); a
        // duplicate may have removed one.
        if outcome.changed || outcome.stop.state == State::Ok {
            files[index] = profile.complete_file().map(|(_, modified)| modified);
        }
        outcomes.push(outcome);
    }
    let changed = outcomes.iter().any(|o| o.changed);
    let head = if profiles.is_empty() {
        Stop::new(State::Disabled, None)
    } else {
        headline(&outcomes)
    };
    let last_success: Vec<Option<u64>> = files
        .iter()
        .map(|file| {
            file.and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_millis() as u64)
        })
        .collect();
    // WB9: id, state, reason code and `duplicateOf` only, never a root.
    let per_profile: Vec<serde_json::Value> = profiles
        .iter()
        .zip(&outcomes)
        .zip(&last_success)
        .map(|((profile, outcome), last_success)| {
            let mut entry = serde_json::json!({
                "id": profile.id,
                "state": outcome.stop.state,
                "events": outcome.events,
                "lastSuccessMs": last_success,
            });
            if let Some(reason) = outcome.stop.reason {
                entry["reason"] = reason.into();
            }
            if let Some(first) = outcome.duplicate_of.and_then(|i| profiles.get(i)) {
                entry["duplicateOf"] = first.id.clone().into();
            }
            entry
        })
        .collect();
    let mut status = serde_json::json!({
        "state": head.state,
        "events": outcomes.iter().map(|o| o.events).sum::<usize>(),
        "lastSuccessMs": last_success.iter().flatten().max(),
        "changed": changed,
        "profiles": per_profile,
        "cleanupFailedProfiles": profiles
            .iter()
            .zip(&outcomes)
            .filter(|(_, outcome)| outcome.cleanup_failed)
            .map(|(profile, _)| profile.id.as_str())
            .collect::<Vec<_>>(),
    });
    if let Some(reason) = head.reason {
        status["reason"] = reason.into();
    }
    last.no_file = profiles
        .iter()
        .zip(&outcomes)
        .filter(|(_, outcome)| writes_no_file(outcome))
        .map(|(profile, _)| profile.clone())
        .collect();
    last.status = Some(status.clone());
    (status, changed)
}

/// `cliPresent` when a profile's walk completed (`ok`) this round but the D6
/// guard blocks the takeover the recheck just computed (macOS mapping; main's
/// rule, whether or not the file's bytes changed). It carries no reason:
/// another profile's must not ride along.
pub(crate) fn status_for(mut status: serde_json::Value, takeover: &Takeover) -> serde_json::Value {
    let synced = status["profiles"]
        .as_array()
        .is_some_and(|profiles| profiles.iter().any(|p| p["state"] == "ok"));
    if synced && *takeover == Takeover::Blocked {
        status["state"] = serde_json::json!(State::CliPresent);
        if let Some(object) = status.as_object_mut() {
            object.remove("reason");
        }
    }
    status
}

/// The local base under a test data root (the roaming base is the root).
#[cfg(test)]
const TEST_LOCAL_DIR: &str = "Local";

#[cfg(test)]
static TEST_DATA_ROOT: RwLock<Option<PathBuf>> = RwLock::new(None);

/// Both test bases: roaming = `root`, local = `root\Local`.
#[cfg(test)]
pub(crate) fn set_data_root_for_test(root: Option<PathBuf>) {
    *TEST_DATA_ROOT
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = root;
}

#[cfg(test)]
pub(crate) static TEST_LOCK: Mutex<()> = Mutex::new(());

#[cfg(test)]
pub(crate) fn set_config_for_test(config: Config) {
    commit_config(config);
}

#[cfg(test)]
thread_local! {
    /// Test fault: the cookie header cannot be built (unreachable otherwise:
    /// `token_user` admits only characters a header value takes).
    static BAD_COOKIE_FOR_TEST: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
    /// Test fault: setting an identical file's modified time fails (the call
    /// is replaced by an error; the time is not set).
    static TOUCH_FAILS_FOR_TEST: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
    /// Test fault: listing a dir for cleanup yields one unreadable entry
    /// after the real ones.
    static LISTING_ERROR_FOR_TEST: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

#[cfg(test)]
thread_local! {
    /// WB9: a panic while a login is held, to check what the envelope carries.
    static PANIC_AFTER_LOGIN: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
    /// Runs between the login read and the sealed-token open, on this thread,
    /// so a test can withdraw consent "mid-round": correct code reads consent
    /// only inside `open_sealed`, after this fires (C2 mutation test).
    static AFTER_LOGIN_HOOK: std::cell::Cell<Option<*const (dyn Fn() + 'static)>> =
        const { std::cell::Cell::new(None) };
}

#[cfg(test)]
fn run_after_login_hook() {
    if let Some(hook) = AFTER_LOGIN_HOOK.with(std::cell::Cell::get) {
        // SAFETY: set only by `with_after_login`, which clears it before the
        // hook it points to goes out of scope.
        unsafe { (*hook)() }
    }
}

/// Run `hook` between the login read and the sealed-token open, on this
/// thread, for the duration of `body`.
#[cfg(test)]
pub(crate) fn with_after_login<R>(hook: &dyn Fn(), body: impl FnOnce() -> R) -> R {
    let pointer: *const (dyn Fn() + '_) = hook;
    // SAFETY: only the lifetime is erased; the pointer is cleared below,
    // before `hook` can go out of scope (a panic in `body` ends the test).
    let pointer: *const (dyn Fn() + 'static) = unsafe { std::mem::transmute(pointer) };
    AFTER_LOGIN_HOOK.with(|cell| cell.set(Some(pointer)));
    let result = body();
    AFTER_LOGIN_HOOK.with(|cell| cell.set(None));
    result
}

#[cfg(all(test, windows))]
mod tests;
