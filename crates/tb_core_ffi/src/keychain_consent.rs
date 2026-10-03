//! Process-wide registry of the credential reads the user has agreed to.
//! Written by `tb_set_keychain_consent`, read by `agent_grokbot` before it
//! touches the Grok Bot desktop login.
//!
//! Ported from macOS 451b4329 `keychain_consent.rs`; the name is kept so both
//! platforms share one FFI symbol and one settings key
//! (`tokenbar.grokBot.keychainConsent`), although Windows has no Keychain.
//!
//! **On Windows this is the only gate.** macOS shows a Keychain dialog after
//! the in-app question; Windows' `CryptUnprotectData` succeeds silently for any
//! process running as the user, so the in-app answer recorded here is the one
//! thing standing between "Grok Bot is installed" and "Syrtis decrypted its
//! login". `agent_grokbot::decode_desktop_secret` asks it before every use of a
//! desktop secret — `plaintext:v1:` values included, unlike macOS (Q6-4) — and
//! before `Local State` is read or the DPAPI key is unwrapped.
//!
//! **Deliberately two-valued.** "Never asked" and "declined" are the same
//! instruction to the adapter — do not read — so only granted ids are stored.
//! The third state lives in the app's settings, where it picks the copy.
//!
//! A `RwLock` static rather than an env var: the process is resident, and an
//! "Allow" that needs a relaunch is not an Allow button. The registry is
//! in-memory and starts empty every launch, so the app re-applies the stored
//! answer at startup; a process that never calls the setter reads no Grok Bot
//! login.
//!
//! Not a process-wide guarantee: the Claude Desktop login is decrypted through
//! the same DPAPI primitive without consulting this registry (R6-20, deferred).

use std::collections::BTreeSet;
use std::sync::{LazyLock, RwLock};

/// Public client ids wired to this registry. Any other id is refused rather
/// than stored, so a typo cannot register a grant that nothing reads.
const CONSENTABLE_CLIENTS: &[&str] = &["grok-bot"];

static KEYCHAIN_CONSENT: LazyLock<RwLock<BTreeSet<String>>> =
    LazyLock::new(|| RwLock::new(BTreeSet::new()));

/// Whether the user has agreed to let Syrtis read this client's login.
/// `false` by default: "never asked" and "said no" behave identically.
pub(crate) fn allowed(client_id: &str) -> bool {
    KEYCHAIN_CONSENT
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .contains(client_id)
}

/// Replace the whole registry from `{"<public-client-id>": true|false}`.
/// Full-replace: `{}` clears every grant, and an id mapped to `false` is
/// absent afterwards. Success data is `{"grantedCount":N,"rejectedCount":M}`.
/// Errors are fixed codes and the input is never echoed (the Windows setter
/// convention); on an error nothing changes.
pub(crate) fn set_from_json(raw: &str) -> Result<serde_json::Value, String> {
    let input: std::collections::BTreeMap<String, bool> =
        serde_json::from_str(raw).map_err(|_| "invalidJson".to_string())?;

    let mut granted: BTreeSet<String> = BTreeSet::new();
    let mut rejected = 0usize;
    for (client_id, allowed) in input {
        if !CONSENTABLE_CLIENTS.contains(&client_id.as_str()) {
            rejected += 1;
            continue;
        }
        if allowed {
            granted.insert(client_id);
        }
    }

    let granted_count = granted.len();
    *KEYCHAIN_CONSENT
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = granted;

    Ok(serde_json::json!({
        "grantedCount": granted_count,
        "rejectedCount": rejected,
    }))
}

/// One process-wide mutex for every test that reads or writes the static.
#[cfg(test)]
pub(crate) static TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[cfg(test)]
pub(crate) fn reset_for_test() {
    *KEYCHAIN_CONSENT
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = BTreeSet::new();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn registers_and_clears() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner());
        reset_for_test();

        assert!(
            !allowed("grok-bot"),
            "an untouched registry must deny, or a process that never calls the \
             setter would read the login without asking"
        );

        let result = set_from_json(r#"{"grok-bot":true}"#).unwrap();
        assert_eq!(result["grantedCount"], 1);
        assert!(allowed("grok-bot"));

        let result = set_from_json(r#"{"grok-bot":false}"#).unwrap();
        assert_eq!(result["grantedCount"], 0);
        assert_eq!(result["rejectedCount"], 0);
        assert!(!allowed("grok-bot"));

        set_from_json(r#"{"grok-bot":true}"#).unwrap();
        set_from_json("{}").unwrap();
        assert!(!allowed("grok-bot"));
        reset_for_test();
    }

    #[test]
    fn an_unwired_client_id_is_refused_rather_than_stored() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner());
        reset_for_test();

        let result = set_from_json(r#"{"grok-bot":true,"claude":true}"#).unwrap();
        assert_eq!(result["grantedCount"], 1);
        assert_eq!(result["rejectedCount"], 1);
        assert!(allowed("grok-bot"));
        assert!(!allowed("claude"));
        assert!(
            !result.to_string().contains("claude"),
            "the input is never echoed"
        );
        reset_for_test();
    }

    #[test]
    fn malformed_json_leaves_the_registry_untouched() {
        let _guard = TEST_LOCK.lock().unwrap_or_else(|p| p.into_inner());
        reset_for_test();
        set_from_json(r#"{"grok-bot":true}"#).unwrap();
        for bad in ["{not json", r#"{"grok-bot":"yes"}"#, "[]"] {
            assert_eq!(set_from_json(bad).unwrap_err(), "invalidJson");
            assert!(allowed("grok-bot"));
        }
        reset_for_test();
    }
}
