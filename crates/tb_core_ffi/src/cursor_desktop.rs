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

use serde_json::Value;
use std::path::{Path, PathBuf};
use std::time::Duration;

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
    conn.busy_timeout(SQLITE_BUSY_TIMEOUT).map_err(unreadable)?;

    let mut stmt = conn
        .prepare(
            "SELECT key, value FROM ItemTable WHERE key IN \
             ('cursorAuth/accessToken','glass.lastSignedInAuthId','cursorAuth/cachedScopedProfile')",
        )
        .map_err(unreadable)?;
    let rows: Vec<(String, String)> = stmt
        .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
        .map_err(unreadable)?
        .collect::<Result<_, _>>()
        .map_err(unreadable)?;

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
