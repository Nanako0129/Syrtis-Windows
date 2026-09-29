//! Read-only Electron `safeStorage` decryption on Windows (Chromium `os_crypt`
//! v10), used to read the Claude Desktop login.
//!
//! The value format is `"v10" || nonce(12) || ciphertext || tag(16)`, sealed
//! with AES-256-GCM under a key that `Local State` keeps at
//! `os_crypt.encrypted_key` as base64(`"DPAPI"` || CryptProtectData(key)).
//!
//! Every error is a fixed string: no serde, base64, UTF-8 or NTSTATUS detail
//! and no secret byte can reach a message. Only the buffers this module owns
//! are overwritten: the key on drop, the DPAPI output and the intermediate key
//! copy before release, and the plaintext on a failed decrypt. The plaintext
//! returned to the caller, and any copy the caller or serde makes of it, is
//! the caller's to wipe or not.

use std::ptr::{null, null_mut};
use std::sync::atomic::{compiler_fence, Ordering};

use base64::Engine;
use serde_json::Value;
use windows_sys::Win32::Security::Cryptography::{
    BCryptDecrypt, BCryptDestroyKey, BCryptGenerateSymmetricKey, CryptUnprotectData,
    BCRYPT_AES_GCM_ALG_HANDLE, BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO,
    BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION, BCRYPT_KEY_HANDLE, CRYPTPROTECT_UI_FORBIDDEN,
    CRYPT_INTEGER_BLOB,
};

use crate::agent_storage_windows::LocalAllocation;

const DPAPI_PREFIX: &[u8] = b"DPAPI";
const V10_PREFIX: &[u8] = b"v10";
const NONCE_LEN: usize = 12;
const TAG_LEN: usize = 16;
const KEY_LEN: usize = 32;

const KEY_ERROR: &str = "safeStorage key could not be read.";
const VALUE_ERROR: &str = "safeStorage value could not be decrypted.";

/// The unwrapped AES-256 key. Deliberately neither `Debug` nor `Clone`; the
/// bytes are wiped on drop.
pub(crate) struct SafeStorageKey([u8; KEY_LEN]);

impl Drop for SafeStorageKey {
    fn drop(&mut self) {
        wipe(&mut self.0);
    }
}

/// Zero a buffer in a way the optimizer cannot elide as a dead store.
pub(crate) fn wipe(bytes: &mut [u8]) {
    for byte in bytes.iter_mut() {
        unsafe { std::ptr::write_volatile(byte, 0) };
    }
    compiler_fence(Ordering::SeqCst);
}

/// Unwrap the safeStorage key from the parsed `Local State` JSON.
pub(crate) fn load_key(local_state: &Value) -> Result<SafeStorageKey, &'static str> {
    let wrapped = encrypted_key_from_local_state(local_state)?;
    let protected = strip_dpapi_prefix(&wrapped)?;
    let mut raw = dpapi_unprotect(protected)?;
    let key = key_from_bytes(&raw);
    wipe(&mut raw);
    key
}

/// Decrypt one base64 safeStorage value. The caller owns (and must wipe) the
/// returned plaintext.
pub(crate) fn decrypt(key: &SafeStorageKey, value_base64: &str) -> Result<Vec<u8>, &'static str> {
    let blob = base64::engine::general_purpose::STANDARD
        .decode(value_base64.trim())
        .map_err(|_| VALUE_ERROR)?;
    let (nonce, ciphertext, tag) = split_v10_blob(&blob)?;
    aes_gcm_decrypt(&key.0, nonce, ciphertext, tag)
}

fn encrypted_key_from_local_state(local_state: &Value) -> Result<Vec<u8>, &'static str> {
    let encoded = local_state
        .get("os_crypt")
        .and_then(|os_crypt| os_crypt.get("encrypted_key"))
        .and_then(Value::as_str)
        .ok_or(KEY_ERROR)?;
    base64::engine::general_purpose::STANDARD
        .decode(encoded.trim())
        .map_err(|_| KEY_ERROR)
}

fn strip_dpapi_prefix(wrapped: &[u8]) -> Result<&[u8], &'static str> {
    wrapped
        .strip_prefix(DPAPI_PREFIX)
        .filter(|protected| !protected.is_empty())
        .ok_or(KEY_ERROR)
}

fn key_from_bytes(raw: &[u8]) -> Result<SafeStorageKey, &'static str> {
    let bytes: [u8; KEY_LEN] = raw.try_into().map_err(|_| KEY_ERROR)?;
    Ok(SafeStorageKey(bytes))
}

/// Split `"v10" || nonce || ciphertext || tag`. The ciphertext may be empty.
fn split_v10_blob(blob: &[u8]) -> Result<(&[u8], &[u8], &[u8]), &'static str> {
    let body = blob.strip_prefix(V10_PREFIX).ok_or(VALUE_ERROR)?;
    if body.len() < NONCE_LEN + TAG_LEN {
        return Err(VALUE_ERROR);
    }
    let (nonce, rest) = body.split_at(NONCE_LEN);
    let (ciphertext, tag) = rest.split_at(rest.len() - TAG_LEN);
    Ok((nonce, ciphertext, tag))
}

/// CryptUnprotectData as the current user, never showing UI. The DPAPI output
/// is copied, then zeroed before `LocalFree`.
fn dpapi_unprotect(protected: &[u8]) -> Result<Vec<u8>, &'static str> {
    let input = CRYPT_INTEGER_BLOB {
        cbData: u32::try_from(protected.len()).map_err(|_| KEY_ERROR)?,
        pbData: protected.as_ptr().cast_mut(),
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: null_mut(),
    };
    // SAFETY: `input` borrows `protected` for the call; description, entropy,
    // reserved and prompt are null as the API allows.
    let ok = unsafe {
        CryptUnprotectData(
            &input,
            null_mut(),
            null(),
            null(),
            null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    let _allocation = LocalAllocation(output.pbData.cast());
    if output.pbData.is_null() {
        return Err(KEY_ERROR);
    }
    // SAFETY: DPAPI returned a LocalAlloc'd buffer of `cbData` bytes, freed by
    // `_allocation` only after this slice's last use.
    let bytes = unsafe { std::slice::from_raw_parts_mut(output.pbData, output.cbData as usize) };
    let result = if ok != 0 {
        Ok(bytes.to_vec())
    } else {
        Err(KEY_ERROR)
    };
    wipe(bytes);
    result
}

struct KeyHandle(BCRYPT_KEY_HANDLE);

impl Drop for KeyHandle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                let _ = BCryptDestroyKey(self.0);
            }
        }
    }
}

fn aes_gcm_key(key: &[u8]) -> Result<KeyHandle, &'static str> {
    let mut handle = KeyHandle(null_mut());
    // SAFETY: the AES-GCM pseudo-handle needs no open/close; a null key object
    // lets CNG allocate it, released by `BCryptDestroyKey` in `KeyHandle`.
    let status = unsafe {
        BCryptGenerateSymmetricKey(
            BCRYPT_AES_GCM_ALG_HANDLE,
            &mut handle.0,
            null_mut(),
            0,
            key.as_ptr(),
            u32::try_from(key.len()).map_err(|_| VALUE_ERROR)?,
            0,
        )
    };
    if status < 0 || handle.0.is_null() {
        return Err(VALUE_ERROR);
    }
    Ok(handle)
}

fn aes_gcm_decrypt(
    key: &[u8],
    nonce: &[u8],
    ciphertext: &[u8],
    tag: &[u8],
) -> Result<Vec<u8>, &'static str> {
    let handle = aes_gcm_key(key)?;
    let length = u32::try_from(ciphertext.len()).map_err(|_| VALUE_ERROR)?;
    let info = BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO {
        cbSize: size_of_u32::<BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO>(),
        dwInfoVersion: BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION,
        // Decrypt only reads the nonce and tag.
        pbNonce: nonce.as_ptr().cast_mut(),
        cbNonce: u32::try_from(nonce.len()).map_err(|_| VALUE_ERROR)?,
        pbTag: tag.as_ptr().cast_mut(),
        cbTag: u32::try_from(tag.len()).map_err(|_| VALUE_ERROR)?,
        ..Default::default()
    };
    let mut plaintext = vec![0u8; ciphertext.len()];
    let mut written = 0u32;
    // SAFETY: every pointer borrows a live buffer of the stated length; the IV
    // lives in `info`, so pbIV is null and flags are 0.
    let status = unsafe {
        BCryptDecrypt(
            handle.0,
            ciphertext.as_ptr(),
            length,
            (&info as *const BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO).cast(),
            null_mut(),
            0,
            plaintext.as_mut_ptr(),
            length,
            &mut written,
            0,
        )
    };
    if status < 0 || written != length {
        wipe(&mut plaintext);
        return Err(VALUE_ERROR);
    }
    Ok(plaintext)
}

fn size_of_u32<T>() -> u32 {
    std::mem::size_of::<T>() as u32
}

/// Synthetic encrypt/protect helpers so the round-trip tests (here and in
/// agent_usage) never need a real Claude value.
#[cfg(test)]
pub(crate) mod test_support {
    use super::*;
    use windows_sys::Win32::Security::Cryptography::{BCryptEncrypt, CryptProtectData};

    /// CryptProtectData(bytes) as the current user.
    pub(crate) fn dpapi_protect(bytes: &[u8]) -> Vec<u8> {
        let input = CRYPT_INTEGER_BLOB {
            cbData: bytes.len() as u32,
            pbData: bytes.as_ptr().cast_mut(),
        };
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: null_mut(),
        };
        let ok = unsafe {
            CryptProtectData(
                &input,
                null(),
                null(),
                null(),
                null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        };
        let _allocation = LocalAllocation(output.pbData.cast());
        assert!(ok != 0 && !output.pbData.is_null());
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize) }.to_vec()
    }

    /// `Local State` JSON whose `os_crypt.encrypted_key` wraps `key`.
    pub(crate) fn local_state_for(key: &[u8]) -> Value {
        let mut wrapped = DPAPI_PREFIX.to_vec();
        wrapped.extend(dpapi_protect(key));
        serde_json::json!({
            "os_crypt": {
                "encrypted_key": base64::engine::general_purpose::STANDARD.encode(wrapped)
            }
        })
    }

    /// Raw `"v10" || nonce || ciphertext || tag` for `plaintext`.
    pub(crate) fn v10_blob(key: &[u8], nonce: &[u8; NONCE_LEN], plaintext: &[u8]) -> Vec<u8> {
        let handle = aes_gcm_key(key).unwrap();
        let mut tag = [0u8; TAG_LEN];
        let info = BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO {
            cbSize: size_of_u32::<BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO>(),
            dwInfoVersion: BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION,
            pbNonce: nonce.as_ptr().cast_mut(),
            cbNonce: NONCE_LEN as u32,
            pbTag: tag.as_mut_ptr(),
            cbTag: TAG_LEN as u32,
            ..Default::default()
        };
        let mut ciphertext = vec![0u8; plaintext.len()];
        let mut written = 0u32;
        let status = unsafe {
            BCryptEncrypt(
                handle.0,
                plaintext.as_ptr(),
                plaintext.len() as u32,
                (&info as *const BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO).cast(),
                null_mut(),
                0,
                ciphertext.as_mut_ptr(),
                ciphertext.len() as u32,
                &mut written,
                0,
            )
        };
        assert!(status >= 0);
        let mut blob = V10_PREFIX.to_vec();
        blob.extend_from_slice(nonce);
        blob.extend(ciphertext);
        blob.extend_from_slice(&tag);
        blob
    }

    /// Base64 safeStorage value, as Claude Desktop stores it in config.json.
    pub(crate) fn v10_value(key: &[u8], nonce: &[u8; NONCE_LEN], plaintext: &[u8]) -> String {
        base64::engine::general_purpose::STANDARD.encode(v10_blob(key, nonce, plaintext))
    }
}

#[cfg(test)]
mod tests {
    use super::test_support::*;
    use super::*;

    const KEY: [u8; KEY_LEN] = [0x5a; KEY_LEN];
    const NONCE: [u8; NONCE_LEN] = [0x11; NONCE_LEN];
    const PLAINTEXT: &[u8] = br#"{"synthetic":"fixture"}"#;

    fn b64(bytes: &[u8]) -> String {
        base64::engine::general_purpose::STANDARD.encode(bytes)
    }

    #[test]
    fn split_v10_blob_separates_nonce_ciphertext_and_tag() {
        let mut blob = b"v10".to_vec();
        blob.extend([1u8; NONCE_LEN]);
        blob.extend([2u8; 5]);
        blob.extend([3u8; TAG_LEN]);
        let (nonce, ciphertext, tag) = split_v10_blob(&blob).unwrap();
        assert_eq!(nonce, [1u8; NONCE_LEN]);
        assert_eq!(ciphertext, [2u8; 5]);
        assert_eq!(tag, [3u8; TAG_LEN]);

        let mut wrong_prefix = blob.clone();
        wrong_prefix[2] = b'1';
        assert_eq!(split_v10_blob(&wrong_prefix).err(), Some(VALUE_ERROR));
        assert_eq!(
            split_v10_blob(&blob[..3 + NONCE_LEN + TAG_LEN - 1]).err(),
            Some(VALUE_ERROR)
        );
        assert!(split_v10_blob(&blob[..3 + NONCE_LEN + TAG_LEN]).is_ok());
    }

    #[test]
    fn strip_dpapi_prefix_requires_the_prefix_and_a_payload() {
        assert_eq!(strip_dpapi_prefix(b"DPAPIabc").unwrap(), b"abc");
        assert_eq!(strip_dpapi_prefix(b"DPAPI").err(), Some(KEY_ERROR));
        assert_eq!(strip_dpapi_prefix(b"XPAPIabc").err(), Some(KEY_ERROR));
        assert!(key_from_bytes(&[0u8; KEY_LEN]).is_ok());
        assert_eq!(key_from_bytes(&[0u8; KEY_LEN - 1]).err(), Some(KEY_ERROR));
    }

    #[test]
    fn crypto_round_trip_decrypts_a_synthetic_value() {
        let key = load_key(&local_state_for(&KEY)).unwrap();
        let plaintext = decrypt(&key, &v10_value(&KEY, &NONCE, PLAINTEXT)).unwrap();
        assert_eq!(plaintext, PLAINTEXT);
    }

    #[test]
    fn crypto_round_trip_rejects_a_tampered_tag_or_ciphertext() {
        let key = load_key(&local_state_for(&KEY)).unwrap();
        let blob = v10_blob(&KEY, &NONCE, PLAINTEXT);
        for index in [blob.len() - 1, 3 + NONCE_LEN] {
            let mut tampered = blob.clone();
            tampered[index] ^= 0x01;
            assert_eq!(decrypt(&key, &b64(&tampered)).err(), Some(VALUE_ERROR));
        }
        let other = load_key(&local_state_for(&[0x33; KEY_LEN])).unwrap();
        assert_eq!(decrypt(&other, &b64(&blob)).err(), Some(VALUE_ERROR));
    }

    #[test]
    fn crypto_round_trip_rejects_wrong_prefix_and_short_values() {
        let key = load_key(&local_state_for(&KEY)).unwrap();
        let mut blob = v10_blob(&KEY, &NONCE, PLAINTEXT);
        blob[..3].copy_from_slice(b"v11");
        assert_eq!(decrypt(&key, &b64(&blob)).err(), Some(VALUE_ERROR));
        assert_eq!(decrypt(&key, &b64(b"v10short")).err(), Some(VALUE_ERROR));
        assert_eq!(decrypt(&key, "not base64 !!").err(), Some(VALUE_ERROR));
    }

    #[test]
    fn crypto_load_key_rejects_missing_prefix_wrong_length_and_bad_dpapi() {
        let without_prefix = serde_json::json!({
            "os_crypt": { "encrypted_key": b64(&dpapi_protect(&KEY)) }
        });
        assert_eq!(load_key(&without_prefix).err(), Some(KEY_ERROR));
        assert_eq!(
            load_key(&local_state_for(&[0x5a; 16])).err(),
            Some(KEY_ERROR)
        );

        let mut garbage = DPAPI_PREFIX.to_vec();
        garbage.extend([0x42u8; 64]);
        let not_dpapi = serde_json::json!({ "os_crypt": { "encrypted_key": b64(&garbage) } });
        assert_eq!(load_key(&not_dpapi).err(), Some(KEY_ERROR));

        assert_eq!(load_key(&serde_json::json!({})).err(), Some(KEY_ERROR));
        let not_base64 = serde_json::json!({ "os_crypt": { "encrypted_key": "!!" } });
        assert_eq!(load_key(&not_base64).err(), Some(KEY_ERROR));
    }
}
