# Cursor desktop sync

Syrtis can read Cursor usage straight from the signed-in Cursor desktop app,
without the tokscale CLI. This page records how that works on Windows and why.
It is the Windows port of macOS Syrtis #487; where the two differ, the
difference is called out.

## Data flow

```
Cursor state.vscdb (read-only)
  └─ access token + its own JWT `sub`
       └─ POST https://cursor.com/api/dashboard/get-filtered-usage-events   (paginated, full history)
            └─ usage.<hmac>.json  in  %APPDATA%\com.nyanako.tokenbar\cursor-cache
                 └─ engine scans the dir as an extra Cursor root (only while the takeover is on)
```

| Piece | Where |
| --- | --- |
| Login reader, shared with the Grok Bot Cursor fallback | `crates/tb_core_ffi/src/cursor_desktop.rs` |
| Walk, file, registry, takeover decision | `crates/tb_core_ffi/src/cursor_sync.rs` |
| Takeover applied to the process scan, recheck, exports | `crates/tb_core_ffi/src/lib.rs` (`capture_process_context`, `recheck_cursor_takeover`, `tb_set_cursor_sync`, `tb_cursor_sync`, `tb_cursor_present`) |
| C# surface | `src/TokenBar.Interop/CursorSync.cs`, `TbCore.SetCursorSync` / `CursorSync` / `CursorPresent` |
| ABI | `include/ctb.h` |

## A second consumer of the Cursor token

Before this feature only Grok Bot's fallback read Cursor's access token. The
sync is the second reader. Both go through `cursor_desktop::read_login`, which
opens `state.vscdb` read-only and returns the token to its caller only. The
sync sends the token to one constant endpoint as the `WorkosCursorSessionToken`
cookie (header marked sensitive), keyed by the user id taken from the token's
own JWT `sub`; a stored glass or profile id that differs refuses the walk. An
expired token, or one within 60 s of `exp`, is refused before any request;
Cursor's refresh token is never used.

## Takeover and the D6 rule

The synced file and the CLI's cache (`%USERPROFILE%\.config\tokscale\cursor-cache`)
can hold the same usage. Only one is counted:

| Sync on | Complete synced file | CLI Cursor files | User confirmed | Reports read | Status after a completed walk |
| --- | --- | --- | --- | --- | --- |
| no | – | – | – | CLI | `disabled` |
| yes | no | – | – | CLI | – (no walk has completed) |
| yes | yes | no | – | synced file only | `ok` |
| yes | yes | yes | no | CLI only (D6) | `cliPresent` |
| yes | yes | yes | yes | synced file only | `ok` |

"CLI Cursor files" means any `usage*.csv` / `usage*.json` up to four levels
under the CLI root, and an unreadable root counts as present, so the guard
errs toward asking. The CLI's files are never moved, hidden or deleted. The
last column is for a walk that completed; a walk that stops early reports its
own state (`partial`, `expired`, `notSignedIn`, `offline`, `error`,
`disabled`) whatever the takeover.

On Windows the scanner settings are fixed when the process source context is
captured, so the takeover lives there (`capture_process_context`), from Rust
state only, never through the C#-owned scan-root registry. The primary Claude
quota window, which captures its own scoped context when extra Claude roots
exist, merges the process context's exclusions instead of replacing them, so
the CLI copy stays excluded there too.

### Recheck points

The takeover is recomputed, and on a change the process context is
re-captured, the root generation bumped and every scan cache cleared (the same
commit as the scan-root setter), at:

- every `tb_set_cursor_sync`; the Rust registry starts off every launch and
  the caller re-applies its stored answer then, so launch is a recheck point;
- the end of every `tb_cursor_sync` walk, whatever its outcome.

So CLI files that appear after an automatic takeover turn it off at the next
walk (at most one sync interval), and the status then says `cliPresent`.

## Storage

- The sync dir is chosen by Rust, never passed in: `<root>\cursor-cache`,
  where `<root>` is `%APPDATA%\com.nyanako.tokenbar` resolved exactly as quota
  history resolves it (following its `.secure` fallback). `tb_set_cursor_sync`
  refuses any key besides `enabled` and `cliTakeoverConfirmed`.
- Every file operation goes through `agent_storage_windows`: owner = current
  user, protected DACL with exactly {current user, SYSTEM}, final-component
  reparse points refused, existing objects used only if they already meet that
  contract (a loose pre-created dir makes the sticky `.secure` sibling the dir).
- Writes: secure lock (`.cursor-sync.lock`), secure temp
  (`.cursor-sync.tmp-*`, never matching `usage*`), flush, secure replace.
- Only a complete walk replaces the file. Turning sync off deletes the synced
  file and temps from every sync dir that exists: both roots
  (`com.nyanako.tokenbar`, `com.nyanako.tokenbar.secure`) times both child
  names (`cursor-cache`, `cursor-cache.secure`), because the `.secure`
  fallbacks are sticky and the dir in use can move after a file was written
  elsewhere. Nothing is created on the way. A candidate that meets the
  contract is cleaned under its own secure lock; one that is a real dir but
  fails the contract was never written by Syrtis and only fails the cleanup
  if it holds a Syrtis-named file; a junction or other non-directory is
  skipped. A Syrtis-named file that fails the storage contract is never
  deleted; at disable time that is the error `cleanupFailed` (sync is off all
  the same), and during a walk it is left in place without a report. Putting
  such a file into the protected dir needs a process running as the same
  user, which this threat model does not cover.

### Roaming and two PCs

The file lives in roaming `%APPDATA%`, like quota history, and it holds no
secret (only timestamp, model, token counts, cost fields, conversation id).
Its name is an HMAC of the Cursor user id under the installation key, which
also roams. A second PC that receives the roamed file and has an empty CLI
cache may take over from it for the same account; that is accepted.

## Threat model

| Adversary | Covered | How |
| --- | --- | --- |
| Another standard user on the PC | yes | protected DACL {user, SYSTEM} |
| Another process of the same user | no | it can read Cursor's own `state.vscdb` anyway |
| Administrators / SYSTEM | no | SYSTEM is granted on purpose (backup, AV) |
| Local planting (pre-created dir, junction, loose ACL) | yes | `agent_storage_windows` contract and `.secure` fallback |
| Network attacker, proxy, DNS | yes | TLS, https-only, constant endpoint, redirects never followed (a 3xx means `expired`), body caps (64 MiB per walk) |
| Concurrent readers | partly | see below |

TLS and proxy, read from `Cargo.toml` and the reqwest 0.13.4 source (not
measured): reqwest is built without default features, with `json` and
`rustls`; `rustls` brings the aws-lc-rs provider and
rustls-platform-verifier, which validates against the Windows certificate
store. The `system-proxy` feature is off, so a proxy is taken only from
`HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`, never from the
Windows (WinINET) proxy settings.

### Measured: a reader blocks the replace

On Windows 10.0.26200 (NTFS, the 188 test machine) the secure replace
(`tokscale_core::fs_atomic::replace_file`: `MoveFileExW` with
`REPLACE_EXISTING | WRITE_THROUGH`, five attempts) fails with
ERROR_ACCESS_DENIED while any reader holds the usage file open, even a Rust
std reader that shares delete; `std::fs::rename` of the same files succeeds.
A walk that meets a reader (the engine's own scan, an antivirus) therefore
reports `write_failed`, keeps the previous file and leaves no temp behind; the
next walk writes it. Accepted as transient (decision 2026-10-08). Deleting
the file while a std reader holds it does succeed, and the name is gone at
once.

## Things that differ from macOS

- No `dir` in `tb_set_cursor_sync`; Rust chooses it (no bundle-id isolation
  is needed on Windows).
- A failed disable-time cleanup is an error (`cleanupFailed`), not a success
  with `removedFiles: 0`.
- `tb_cursor_present` lets the app's notice ask whether Cursor is installed
  without re-deriving Cursor's path in C#.
- No `TOKENBAR_CURSOR_STATE_VSCDB` override; tests pass paths by parameter.
