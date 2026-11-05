//! Read-only access to the Cursor desktop app's login in `state.vscdb`.
//!
//! Two consumers share this reader: the Grok Bot quota fallback
//! (`agent_grokbot`) and the Cursor usage sync (`cursor_sync`). The database
//! is opened read-only, never written, and the token is returned to the
//! caller only; nothing here logs or persists it. Pure: no consent check (the
//! Grok Bot consent gate covers the Grok Bot desktop secrets, not this file).
//!
//! Windows: no path override. The database is
//! `dirs::config_dir()\Cursor\User\globalStorage\state.vscdb` (roaming
//! `%APPDATA%`); tests pass a path by parameter.
//!
//! Three readers (CMP-W W1 item 8):
//! - `read_login`: Grok Bot, unchanged; it only ever reads the default
//!   profile's database.
//! - `read_login_default`: the Cursor sync's default profile. `read_login`'s
//!   checks plus `DEFENSIVE` and `trusted_schema=OFF` (WB10), nothing else.
//! - `read_login_added`: an added `--user-data-dir` profile. The root's drive
//!   letter must be a fixed drive before any I/O, and so must the volume the
//!   opened handle is on (WB2), then the rest of the handle-first open in
//!   `agent_storage_windows::open_foreign_file` (WB1); SQLite then opens the
//!   same name and its handle must be that same file. SQLite's
//!   `SQLITE_OPEN_NOFOLLOW` is not used: on Windows it refuses nothing
//!   (measured on 188, CMP-W0).

use serde_json::Value;
use std::path::{Path, PathBuf};
use std::time::Duration;

/// `<root>\User\globalStorage\state.vscdb`: an added profile's database
/// (layout measured on 188, CMP-W U4).
pub(crate) fn profile_db_path(root: &Path) -> PathBuf {
    root.join("User").join("globalStorage").join("state.vscdb")
}

/// `GetDriveTypeW` of a profile root's drive letter, or of a volume GUID root
/// `\\?\Volume{…}\` (the handle check); tests inject their own.
pub(crate) type DriveType<'a> = dyn Fn(&Path) -> u32 + Sync + 'a;

#[cfg(windows)]
pub(crate) fn drive_type(path: &Path) -> u32 {
    crate::agent_storage_windows::drive_type(path)
}

#[cfg(not(windows))]
pub(crate) fn drive_type(_: &Path) -> u32 {
    0
}

/// `GetDriveTypeW` values: only a fixed drive is read.
pub(crate) const DRIVE_NO_ROOT_DIR: u32 = 1;
pub(crate) const DRIVE_FIXED: u32 = 3;

/// `state.vscdb` can be briefly locked by a running Cursor; wait rather than
/// fail the poll.
const SQLITE_BUSY_TIMEOUT: Duration = Duration::from_millis(3000);

/// The three rows a login is made of. Each user id is `None` when its row is
/// absent or holds no recognisable `user_...` id. No `Debug`: it holds the
/// access token.
pub(crate) struct CursorLogin {
    pub access_token: String,
    pub glass_user_id: Option<String>,
    pub profile_user_id: Option<String>,
}

impl CursorLogin {
    /// The stored account id: `glass.lastSignedInAuthId`, else the cached
    /// profile. Grok Bot's cookie is keyed by this.
    pub(crate) fn stored_user_id(&self) -> Option<&str> {
        self.glass_user_id
            .as_deref()
            .or(self.profile_user_id.as_deref())
    }
}

/// `<config>\Cursor\User\globalStorage\state.vscdb`.
pub(crate) fn state_db_path_under(config_dir: &Path) -> PathBuf {
    config_dir
        .join("Cursor")
        .join("User")
        .join("globalStorage")
        .join("state.vscdb")
}

/// The signed-in Cursor's database, or `None` when the platform has no
/// config directory.
pub(crate) fn state_db_path() -> Option<PathBuf> {
    dirs::config_dir().map(|dir| state_db_path_under(&dir))
}

/// Whether Cursor's `state.vscdb` exists at `db_path`. Metadata only: the
/// file is not opened.
pub(crate) fn is_present(db_path: &Path) -> bool {
    db_path.is_file()
}

/// `Ok(None)` when the database is absent or holds no access token.
pub(crate) fn read_login(db_path: &Path) -> Result<Option<CursorLogin>, String> {
    if !db_path.is_file() {
        return Ok(None);
    }
    let unreadable = |_| "Could not read the Cursor login database.".to_string();
    let conn =
        rusqlite::Connection::open_with_flags(db_path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(unreadable)?;
    query_login(&conn).map_err(unreadable)
}

/// The sync's default-profile reader (WB10): `read_login`'s checks (a path
/// that is not a file is "not signed in") plus `DEFENSIVE` and
/// `trusted_schema=OFF`. No owner or reparse checks: the default profile reads
/// what every release read.
pub(crate) fn read_login_default(db_path: &Path) -> Result<Option<CursorLogin>, &'static str> {
    if !db_path.is_file() {
        return Ok(None);
    }
    let conn = default_connection(db_path).map_err(|_| "login_unreadable")?;
    query_login(&conn).map_err(|_| "login_unreadable")
}

/// The default profile's connection (WB10's test reads its pragmas).
fn default_connection(db_path: &Path) -> rusqlite::Result<rusqlite::Connection> {
    open_hardened(db_path)
}

/// Read-only, `DEFENSIVE`, `trusted_schema=OFF`: a database another program
/// wrote can neither run schema-defined SQL functions nor corrupt itself
/// through this connection.
fn open_hardened(db_path: &Path) -> rusqlite::Result<rusqlite::Connection> {
    let conn =
        rusqlite::Connection::open_with_flags(db_path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    conn.set_db_config(rusqlite::config::DbConfig::SQLITE_DBCONFIG_DEFENSIVE, true)?;
    conn.pragma_update(None, "trusted_schema", false)?;
    Ok(conn)
}

/// An added profile's reader (WB1, WB2), every sync. In order: the root's
/// drive letter is a fixed drive (no other I/O before that; an unmounted
/// letter is "not signed in"); the handle-first checks on
/// `<root>\User\globalStorage\state.vscdb`, which begin with the UNC check
/// and with `drive_type` asked again for the volume the opened file is on (a
/// junction under the root can lead to another volume); the
/// hardened SQLite open of that name while the checked handle is held; and
/// SQLite's own handle names the same file. Every refusal is a fixed reason;
/// `Ok(None)` is no database or no token.
#[cfg(windows)]
pub(crate) fn read_login_added(
    root: &Path,
    drive_type: &DriveType<'_>,
) -> Result<Option<CursorLogin>, &'static str> {
    use crate::agent_storage_windows::{open_foreign_file, ForeignRefusal};
    match drive_type(root) {
        DRIVE_FIXED => {}
        DRIVE_NO_ROOT_DIR => return Ok(None),
        _ => return Err("db_not_fixed_drive"),
    }
    let db = profile_db_path(root);
    let (pinned, identity) = match open_foreign_file(&db, drive_type) {
        Ok(opened) => opened,
        Err(ForeignRefusal::Absent) => return Ok(None),
        Err(refusal) => return Err(refusal.code()),
    };
    #[cfg(test)]
    if let Some(hook) = BETWEEN_OPENS.with(std::cell::Cell::get) {
        // SAFETY: set only by `with_between_opens`, which clears it before the
        // hook it points to goes out of scope.
        unsafe { (*hook)() }
    }
    let conn = open_hardened(&db).map_err(|_| "login_unreadable")?;
    if sqlite_file_identity(&conn) != Some(identity) {
        return Err("db_changed");
    }
    let login = query_login(&conn).map_err(|_| "login_unreadable");
    drop(conn);
    drop(pinned);
    login
}

#[cfg(test)]
thread_local! {
    /// WB1 test seam: runs between the checked open and SQLite's open.
    static BETWEEN_OPENS: std::cell::Cell<Option<*const (dyn Fn() + 'static)>> =
        const { std::cell::Cell::new(None) };
}

/// Run `body` with `hook` called between the checked open and SQLite's open
/// of an added profile's database, on this thread.
#[cfg(test)]
pub(crate) fn with_between_opens<R>(hook: &dyn Fn(), body: impl FnOnce() -> R) -> R {
    let pointer: *const (dyn Fn() + '_) = hook;
    // SAFETY: only the lifetime is erased; the pointer is cleared below,
    // before `hook` can go out of scope (a panic in `body` ends the test).
    let pointer: *const (dyn Fn() + 'static) = unsafe { std::mem::transmute(pointer) };
    BETWEEN_OPENS.with(|cell| cell.set(Some(pointer)));
    let result = body();
    BETWEEN_OPENS.with(|cell| cell.set(None));
    result
}

#[cfg(not(windows))]
pub(crate) fn read_login_added(
    _: &Path,
    _: &DriveType<'_>,
) -> Result<Option<CursorLogin>, &'static str> {
    Err("login_unreadable")
}

/// The identity of the file SQLite opened for `main`
/// (`SQLITE_FCNTL_WIN32_GET_HANDLE`, measured to match on 188).
#[cfg(windows)]
fn sqlite_file_identity(
    conn: &rusqlite::Connection,
) -> Option<crate::agent_storage_windows::FileIdentity> {
    let mut handle: windows_sys::Win32::Foundation::HANDLE = std::ptr::null_mut();
    // SAFETY: `conn` is open, "main" is NUL-terminated, and this opcode writes
    // one HANDLE through the pointer; the handle stays SQLite's (not closed).
    let rc = unsafe {
        rusqlite::ffi::sqlite3_file_control(
            conn.handle(),
            c"main".as_ptr(),
            rusqlite::ffi::SQLITE_FCNTL_WIN32_GET_HANDLE,
            (&mut handle as *mut windows_sys::Win32::Foundation::HANDLE).cast(),
        )
    };
    if rc != rusqlite::ffi::SQLITE_OK || handle.is_null() {
        return None;
    }
    crate::agent_storage_windows::raw_handle_identity(handle)
}

fn query_login(conn: &rusqlite::Connection) -> rusqlite::Result<Option<CursorLogin>> {
    conn.busy_timeout(SQLITE_BUSY_TIMEOUT)?;
    let mut stmt = conn.prepare(
        "SELECT key, value FROM ItemTable WHERE key IN \
         ('cursorAuth/accessToken','glass.lastSignedInAuthId','cursorAuth/cachedScopedProfile')",
    )?;
    let rows: Vec<(String, String)> = stmt
        .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?
        .collect::<Result<_, _>>()?;

    let mut token: Option<String> = None;
    let mut identity: Option<String> = None;
    let mut profile: Option<String> = None;
    for (key, value) in &rows {
        let normalized = normalized_stored_string(value);
        match key.as_str() {
            "cursorAuth/accessToken" => token = normalized,
            "glass.lastSignedInAuthId" => identity = normalized,
            "cursorAuth/cachedScopedProfile" => profile = normalized,
            _ => {}
        }
    }

    let Some(access_token) = token.filter(|t| !t.is_empty()) else {
        return Ok(None);
    };
    Ok(Some(CursorLogin {
        access_token,
        glass_user_id: identity.as_deref().and_then(extract_user_id),
        profile_user_id: profile.as_deref().and_then(extract_user_id),
    }))
}

/// Values in `state.vscdb` are sometimes JSON-encoded strings (wrapped in an
/// extra layer of quotes) — unwrap one layer when present, else use as-is.
pub(crate) fn normalized_stored_string(value: &str) -> Option<String> {
    if value.is_empty() {
        return None;
    }
    if value.starts_with('"') {
        if let Ok(Value::String(inner)) = serde_json::from_str(value) {
            return if inner.is_empty() { None } else { Some(inner) };
        }
    }
    Some(value.to_string())
}

/// The claims of a token that looks like a Cursor session JWT: only
/// `[A-Za-z0-9._-]` (it goes into a cookie), exactly three non-empty
/// segments, and a payload that decodes to a JSON object. On Windows this one
/// check is shared by the usage sync's `token_user`, Grok Bot's Cursor
/// fallback and Grok Bot's desktop `history_owner`. A sealed token
/// (`cursor-safe-storage:…`) fails the charset on its `:`.
///
/// Not the same as macOS, which checks less. macOS `jwt_claims` is the
/// charset plus macOS `jwt_payload`, which itself requires three non-empty
/// segments, and has no object filter. macOS `history_owner` calls
/// `jwt_payload` only: no charset, no object filter. The segment check is
/// written here because the Windows `agent_usage::jwt_payload` has none.
pub(crate) fn jwt_claims(token: &str) -> Option<Value> {
    if !token
        .bytes()
        .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
    {
        return None;
    }
    let segments: Vec<&str> = token.split('.').collect();
    if segments.len() != 3 || segments.iter().any(|segment| segment.is_empty()) {
        return None;
    }
    crate::agent_usage::jwt_payload(token).filter(Value::is_object)
}

/// Cursor user ids look like `user_...` (20+ alphanumerics). Scan for the
/// first qualifying occurrence rather than depending on the surrounding JSON
/// shape.
pub(crate) fn extract_user_id(text: &str) -> Option<String> {
    // Every `user_` occurrence, not only the first: a key such as `"user_id"`
    // before the real id must not hide it (it feeds the S-1 account check).
    text.match_indices("user_").find_map(|(start, _)| {
        let rest = &text[start + "user_".len()..];
        let len = rest
            .char_indices()
            .take_while(|(_, c)| c.is_ascii_alphanumeric())
            .map(|(i, c)| i + c.len_utf8())
            .last()
            .unwrap_or(0);
        (len >= 20).then(|| format!("user_{}", &rest[..len]))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_short_user_prefix_before_the_real_id_does_not_hide_it() {
        let id = "user_01ABCDEFGHIJKLMNOPQRSTUV";
        assert_eq!(
            extract_user_id(&format!(r#"{{"user_id":"x","sub":"{id}"}}"#)).as_deref(),
            Some(id)
        );
        // Control: no qualifying occurrence at all.
        assert_eq!(extract_user_id(r#"{"user_id":"user_short"}"#), None);
    }

    fn empty_db(dir: &Path) -> PathBuf {
        let path = dir.join("state.vscdb");
        rusqlite::Connection::open(&path)
            .unwrap()
            .execute_batch("CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT)")
            .unwrap();
        path
    }

    fn temp_root(tag: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "tokenbar-cursor-desktop-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&root).unwrap();
        root
    }

    /// WB10: the default profile's sync reader runs with `trusted_schema=OFF`
    /// and `DEFENSIVE` (macOS `the_sync_reader_runs_untrusted_schema_and_defensive`).
    #[test]
    fn the_default_reader_runs_untrusted_schema_and_defensive() {
        let root = temp_root("pragmas");
        let conn = default_connection(&empty_db(&root)).unwrap();
        let trusted: i64 = conn
            .query_row("PRAGMA trusted_schema", [], |row| row.get(0))
            .unwrap();
        assert_eq!(trusted, 0);
        assert!(conn
            .db_config(rusqlite::config::DbConfig::SQLITE_DBCONFIG_DEFENSIVE)
            .unwrap());
        // Control: a plain connection has both the other way.
        let plain = rusqlite::Connection::open(root.join("state.vscdb")).unwrap();
        let trusted: i64 = plain
            .query_row("PRAGMA trusted_schema", [], |row| row.get(0))
            .unwrap();
        assert_eq!(trusted, 1);
        drop((conn, plain));
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// WB10 / S6: the default reader treats what `read_login` treated as
    /// "not signed in" the same way (absent, a directory), and follows a
    /// symlinked file (Windows `the_default_reader_follows_a_symlink...`).
    #[test]
    fn the_default_reader_keeps_the_old_signed_out_cases() {
        let root = temp_root("default");
        let db = empty_db(&root);
        for signed_out in [root.clone(), root.join("absent.vscdb"), root.join("x").join("state.vscdb")] {
            assert!(matches!(read_login_default(&signed_out), Ok(None)), "{signed_out:?}");
        }
        assert!(matches!(read_login_default(&db), Ok(None)), "no token row");
        std::fs::write(root.join("junk.vscdb"), b"not sqlite").unwrap();
        assert_eq!(
            read_login_default(&root.join("junk.vscdb")).err(),
            Some("login_unreadable")
        );
        #[cfg(windows)]
        {
            let link = root.join("link.vscdb");
            std::os::windows::fs::symlink_file(&db, &link).unwrap();
            assert!(matches!(read_login_default(&link), Ok(None)), "the symlink was not followed");
            // An added profile refuses the same link.
            let added = root.join("added");
            std::fs::create_dir_all(added.join("User").join("globalStorage")).unwrap();
            std::os::windows::fs::symlink_file(&db, profile_db_path(&added)).unwrap();
            assert_eq!(
                read_login_added(&added, &|_| DRIVE_FIXED).err(),
                Some("db_symlink")
            );
        }
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// Only added profiles check the owner (WB10: the default has no owner or
    /// reparse checks): a database owned by someone else is read for the
    /// default profile and refused for an added one.
    #[cfg(windows)]
    #[test]
    fn only_added_profiles_check_the_owner() {
        use crate::agent_storage_windows::{set_owner_for_test, TestOwner};
        let root = temp_root("owner");
        let dir = root.join("User").join("globalStorage");
        std::fs::create_dir_all(&dir).unwrap();
        let db = empty_db(&dir);
        set_owner_for_test(&db, TestOwner::LocalSystem).unwrap();
        assert!(matches!(read_login_default(&db), Ok(None)));
        assert_eq!(
            read_login_added(&root, &|_| DRIVE_FIXED).err(),
            Some("db_foreign_owner")
        );
        // Controls: the user's own file and an Administrators-owned one.
        for owner in [TestOwner::CurrentUser, TestOwner::Administrators] {
            set_owner_for_test(&db, owner).unwrap();
            assert!(matches!(read_login_added(&root, &|_| DRIVE_FIXED), Ok(None)));
        }
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// WB2 on the handle (/code-review finding 2): the pre-I/O check sees
    /// only the root's drive letter, so the volume the opened file is really
    /// on is asked again, by its volume GUID root. Injected: the root's letter
    /// answers fixed, the handle's volume answers removable →
    /// `db_not_fixed_drive`. Control: the real query on this fixed volume
    /// passes, and answers fixed for that same GUID root.
    #[cfg(windows)]
    #[test]
    fn the_volume_the_handle_is_on_must_be_fixed() {
        use crate::agent_storage_windows::{set_owner_for_test, TestOwner};
        const DRIVE_REMOVABLE: u32 = 2;
        let root = temp_root("volume");
        let dir = root.join("User").join("globalStorage");
        std::fs::create_dir_all(&dir).unwrap();
        let db = empty_db(&dir);
        set_owner_for_test(&db, TestOwner::CurrentUser).unwrap();
        let asked = std::sync::Mutex::new(Vec::<PathBuf>::new());
        let removable_volume = |path: &Path| {
            asked.lock().unwrap().push(path.to_path_buf());
            if path.to_string_lossy().starts_with(r"\\?\Volume{") {
                DRIVE_REMOVABLE
            } else {
                DRIVE_FIXED
            }
        };
        assert_eq!(
            read_login_added(&root, &removable_volume).err(),
            Some("db_not_fixed_drive")
        );
        let asked = asked.into_inner().unwrap();
        assert_eq!(asked.len(), 2, "{asked:?}");
        assert_eq!(asked[0], root);
        let volume = asked[1].clone();
        assert!(volume.to_string_lossy().ends_with("}\\"), "{volume:?}");
        // Controls: the real query answers fixed for that GUID root, and the
        // same database on it is read.
        assert_eq!(drive_type(&volume), DRIVE_FIXED);
        assert!(matches!(read_login_added(&root, &drive_type), Ok(None)));
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// WB2 on the handle without a drive-letter final path (round-3 F): with
    /// the DOS query failing (test fault; no real volume on 188 lacks one),
    /// the production open still decides by the volume GUID root: a file on
    /// the fixed system volume is read, and the same file is refused when
    /// that root answers removable. Pins `open_foreign_file`'s wiring, which
    /// `the_handle_location_rule` (the pure rule) cannot.
    #[cfg(windows)]
    #[test]
    fn a_file_without_a_dos_path_is_decided_by_its_volume() {
        use crate::agent_storage_windows::{
            set_owner_for_test, TestOwner, NO_DOS_PATH_FOR_TEST, NO_DOS_PATH_HITS_FOR_TEST,
        };
        let root = temp_root("nodos");
        let dir = root.join("User").join("globalStorage");
        std::fs::create_dir_all(&dir).unwrap();
        let db = empty_db(&dir);
        set_owner_for_test(&db, TestOwner::CurrentUser).unwrap();
        let removable_volume = |path: &Path| {
            if path.to_string_lossy().starts_with(r"\\?\Volume{") {
                2
            } else {
                DRIVE_FIXED
            }
        };
        NO_DOS_PATH_HITS_FOR_TEST.with(|hits| hits.set(0));
        NO_DOS_PATH_FOR_TEST.with(|fault| fault.set(true));
        let fixed = read_login_added(&root, &drive_type);
        let removable = read_login_added(&root, &removable_volume);
        NO_DOS_PATH_FOR_TEST.with(|fault| fault.set(false));
        assert_eq!(
            NO_DOS_PATH_HITS_FOR_TEST.with(std::cell::Cell::get),
            2,
            "the fault did not fire: both opens still had a DOS path"
        );
        assert!(matches!(fixed, Ok(None)), "{:?}", fixed.err());
        assert_eq!(removable.err(), Some("db_not_fixed_drive"));
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// WB1 on a volume without persistent ACLs: an exFAT VHD, mounted for
    /// this test only (needs elevation, so it is a manual run:
    /// `cargo test -p tb_core_ffi -- --ignored an_exfat_volume_is_refused`).
    /// The VHD is always detached and deleted.
    #[cfg(windows)]
    #[test]
    #[ignore = "manual: mounts an exFAT VHD with diskpart (needs elevation)"]
    fn an_exfat_volume_is_refused() {
        /// Detaches the VHD, then deletes it and its folder (the file can
        /// stay locked for a moment after the detach).
        struct Vhd(PathBuf);
        impl Drop for Vhd {
            fn drop(&mut self) {
                let _ = diskpart(&format!(
                    "select vdisk file=\"{}\"\ndetach vdisk\n",
                    self.0.display()
                ));
                for _ in 0..50 {
                    if std::fs::remove_file(&self.0).is_ok() || !self.0.exists() {
                        break;
                    }
                    std::thread::sleep(std::time::Duration::from_millis(100));
                }
                if let Some(parent) = self.0.parent() {
                    let _ = std::fs::remove_dir(parent);
                }
            }
        }
        fn diskpart(script: &str) -> bool {
            let file = std::env::temp_dir().join(format!("cmpw-diskpart-{}.txt", std::process::id()));
            std::fs::write(&file, script).unwrap();
            let ok = std::process::Command::new("diskpart")
                .arg("/s")
                .arg(&file)
                .output()
                .map(|out| {
                    eprintln!("{}", String::from_utf8_lossy(&out.stdout));
                    out.status.success()
                })
                .unwrap_or(false);
            let _ = std::fs::remove_file(&file);
            ok
        }
        let used = unsafe { windows_sys::Win32::Storage::FileSystem::GetLogicalDrives() };
        let letter = (b'M'..=b'Y')
            .rev()
            .find(|l| used & (1 << (l - b'A')) == 0)
            .expect("a free drive letter") as char;
        let root = temp_root("exfat");
        let vhd = Vhd(root.join("cmpw-exfat.vhdx"));
        let made = diskpart(&format!(
            "create vdisk file=\"{0}\" maximum=64 type=expandable\nselect vdisk file=\"{0}\"\nattach vdisk\ncreate partition primary\nformat fs=exfat quick label=CMPW\nassign letter={1}\n",
            vhd.0.display(),
            letter
        ));
        assert!(made, "fixture: the exFAT VHD could not be created and mounted");
        let profile = PathBuf::from(format!("{letter}:\\cmpw-profile"));
        let dir = profile.join("User").join("globalStorage");
        std::fs::create_dir_all(&dir).unwrap();
        drop(empty_db(&dir));
        assert_eq!(crate::agent_storage_windows::drive_type(&profile), DRIVE_FIXED);
        let refused = read_login_added(&profile, &drive_type);
        eprintln!("measured: exFAT added profile -> {:?}", refused.as_ref().err());
        assert_eq!(refused.err(), Some("db_no_acl_volume"));
    }

    /// WB2 on the handle (round-2 finding 2): a database on a fixed NTFS
    /// volume mounted only at a folder (no drive letter) is read: its volume
    /// GUID root answers fixed. Measured on 188: the file still has a
    /// drive-letter final path through the mount folder, so this is a
    /// regression check that such a volume is read, not a test of the
    /// no-DOS-path branch (that is `a_file_without_a_dos_path_is_decided_by_
    /// its_volume`, with a test fault). A VHD made and
    /// mounted with diskpart (needs elevation), so a manual run:
    /// `cargo test -p tb_core_ffi -- --ignored --exact cursor_desktop::tests::a_fixed_volume_mounted_at_a_folder_is_read`.
    /// The VHD is always detached and deleted, also when an assertion fails.
    #[cfg(windows)]
    #[test]
    #[ignore = "manual: mounts a VHD at a folder with diskpart (needs elevation)"]
    fn a_fixed_volume_mounted_at_a_folder_is_read() {
        fn diskpart(script: &str) -> bool {
            let file = std::env::temp_dir()
                .join(format!("cmpw-diskpart-mnt-{}.txt", std::process::id()));
            std::fs::write(&file, script).unwrap();
            let ok = std::process::Command::new("diskpart")
                .arg("/s")
                .arg(&file)
                .output()
                .map(|out| {
                    eprintln!("{}", String::from_utf8_lossy(&out.stdout));
                    out.status.success()
                })
                .unwrap_or(false);
            let _ = std::fs::remove_file(&file);
            ok
        }
        /// Detaches the VHD (which removes the mount point), then deletes it
        /// and the work dir.
        struct Vhd(PathBuf, PathBuf);
        impl Drop for Vhd {
            fn drop(&mut self) {
                let _ = diskpart(&format!(
                    "select vdisk file=\"{}\"\ndetach vdisk\n",
                    self.0.display()
                ));
                for _ in 0..50 {
                    if std::fs::remove_file(&self.0).is_ok() || !self.0.exists() {
                        break;
                    }
                    std::thread::sleep(std::time::Duration::from_millis(100));
                }
                let _ = std::fs::remove_dir_all(&self.1);
            }
        }
        let work = temp_root("mnt");
        let mount = work.join("mnt");
        std::fs::create_dir_all(&mount).unwrap();
        let vhd = Vhd(work.join("cmpw-ntfs.vhdx"), work.clone());
        let made = diskpart(&format!(
            "create vdisk file=\"{0}\" maximum=64 type=expandable\nselect vdisk file=\"{0}\"\nattach vdisk\ncreate partition primary\nformat fs=ntfs quick label=CMPWMNT\nassign mount=\"{1}\"\n",
            vhd.0.display(),
            mount.display()
        ));
        assert!(made, "fixture: the VHD could not be created and mounted at a folder");
        let root = mount.join("cmpw-profile");
        let dir = root.join("User").join("globalStorage");
        std::fs::create_dir_all(&dir).unwrap();
        let db = empty_db(&dir);
        use crate::agent_storage_windows::{set_owner_for_test, TestOwner};
        set_owner_for_test(&db, TestOwner::CurrentUser).unwrap();
        assert_eq!(drive_type(&root), DRIVE_FIXED, "the root's letter (the system drive) passes");
        let read = read_login_added(&root, &drive_type);
        eprintln!("measured: folder-mounted fixed volume -> {:?}", read.as_ref().err());
        assert!(matches!(read, Ok(None)), "{:?}", read.err());
        drop(vhd);
    }

    /// WB2 on the handle with a real non-fixed volume: an ISO image (made
    /// with IMAPI2, holding `gs\state.vscdb`) mounted as an optical drive,
    /// reached through a junction `<root>\User\globalStorage` under a root on
    /// the fixed system drive. The root's letter passes the pre-I/O check; the
    /// handle's volume must refuse it. Mounts an image (needs elevation), so
    /// it is a manual run:
    /// `cargo test -p tb_core_ffi -- --ignored --exact cursor_desktop::tests::a_junction_onto_an_optical_volume_is_refused`.
    /// The image is always dismounted and deleted, and the work dir removed,
    /// also when an assertion fails.
    #[cfg(windows)]
    #[test]
    #[ignore = "manual: mounts an ISO image with Mount-DiskImage (needs elevation)"]
    fn a_junction_onto_an_optical_volume_is_refused() {
        /// Removed after the image (declared first, dropped last). The
        /// junction is removed as a link, never followed.
        struct Work(PathBuf);
        impl Drop for Work {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir(self.0.join("root").join("User").join("globalStorage"));
                let _ = std::fs::remove_dir_all(&self.0);
            }
        }
        struct Iso(PathBuf);
        impl Drop for Iso {
            fn drop(&mut self) {
                let _ = powershell(&format!(
                    "Dismount-DiskImage -ImagePath '{}' | Out-Null",
                    self.0.display()
                ));
                for _ in 0..50 {
                    if std::fs::remove_file(&self.0).is_ok() || !self.0.exists() {
                        break;
                    }
                    std::thread::sleep(std::time::Duration::from_millis(100));
                }
            }
        }
        fn powershell(script: &str) -> Option<String> {
            let out = std::process::Command::new("powershell.exe")
                .args(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script])
                .output()
                .ok()?;
            eprintln!("{}", String::from_utf8_lossy(&out.stderr));
            out.status
                .success()
                .then(|| String::from_utf8_lossy(&out.stdout).trim().to_string())
        }
        let work_dir = Work(temp_root("iso"));
        let work = work_dir.0.clone();
        let source = work.join("src");
        std::fs::create_dir_all(source.join("gs")).unwrap();
        drop(empty_db(&source.join("gs")));
        let iso = Iso(work.join("cmpw.iso"));
        // IMAPI2 writes the image; a tiny C# helper copies its IStream out.
        let made = powershell(&format!(
            r#"$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices; using System.Runtime.InteropServices.ComTypes;
public static class CmpwIso {{
  public static void Save(object stream, string path) {{
    var s = (IStream)stream; var buf = new byte[65536]; var read = Marshal.AllocHGlobal(4);
    using (var f = File.Create(path)) {{
      while (true) {{ s.Read(buf, buf.Length, read); int n = Marshal.ReadInt32(read); if (n == 0) break; f.Write(buf, 0, n); }}
    }}
    Marshal.FreeHGlobal(read);
  }}
}}
'@
$fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fsi.FileSystemsToCreate = 4
$fsi.VolumeName = 'CMPW'
$fsi.Root.AddTree('{src}', $false)
[CmpwIso]::Save($fsi.CreateResultImage().ImageStream, '{iso}')
$image = Mount-DiskImage -ImagePath '{iso}' -PassThru
($image | Get-Volume).DriveLetter"#,
            src = source.display(),
            iso = iso.0.display()
        ));
        let letter = made
            .as_deref()
            .and_then(|out| out.lines().last())
            .and_then(|l| l.trim().chars().next())
            .expect("fixture: the ISO could not be made and mounted");
        let target = PathBuf::from(format!("{letter}:\\gs"));
        eprintln!("measured: GetDriveTypeW({letter}:\\) = {}", drive_type(&target));
        assert_ne!(drive_type(&target), DRIVE_FIXED, "fixture: the image mounted as a fixed drive");
        let root = work.join("root");
        std::fs::create_dir_all(root.join("User")).unwrap();
        let cmd = std::env::var_os("SystemRoot")
            .map(|system| PathBuf::from(system).join("System32").join("cmd.exe"))
            .unwrap();
        let linked = std::process::Command::new(cmd)
            .args(["/C", "mklink", "/J"])
            .arg(root.join("User").join("globalStorage"))
            .arg(&target)
            .output()
            .unwrap();
        assert!(linked.status.success(), "mklink /J failed: {linked:?}");
        assert_eq!(drive_type(&root), DRIVE_FIXED, "the root's letter passes");
        let refused = read_login_added(&root, &drive_type);
        eprintln!("measured: junction onto the optical volume -> {:?}", refused.as_ref().err());
        assert_eq!(refused.err(), Some("db_not_fixed_drive"));
        drop(iso);
        drop(work_dir);
    }

    /// The presence probe looks at the path only: a directory or a missing
    /// file is "absent", any file is "present", whatever it holds.
    #[test]
    fn presence_is_the_file_existing() {
        let root = std::env::temp_dir().join(format!(
            "tokenbar-cursor-present-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let db = state_db_path_under(&root);
        assert!(!is_present(&db));
        std::fs::create_dir_all(&db).unwrap();
        assert!(!is_present(&db), "a directory is not Cursor's database");
        std::fs::remove_dir(&db).unwrap();
        std::fs::write(&db, b"not sqlite").unwrap();
        assert!(is_present(&db));
        std::fs::remove_dir_all(&root).unwrap();
    }
}
