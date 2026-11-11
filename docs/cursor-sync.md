# Cursor desktop sync

Syrtis can read Cursor usage straight from the signed-in Cursor desktop app,
without the tokscale CLI. This page records how that works on Windows and why.
It is the Windows port of macOS Syrtis #487; where the two differ, the
difference is called out.

## Data flow

```
per profile, one at a time (the default first, then each added --user-data-dir folder):
Cursor state.vscdb (read-only)
  └─ access token + its own JWT `sub`
       └─ POST https://cursor.com/api/dashboard/get-filtered-usage-events   (paginated, full history)
            │   (page 1 a JSON object without usageEventsDisplay: a Billing V2 account, see below)
            │  └─ GET https://cursor.com/api/dashboard/v2/query-billing-items   (per UTC month, cursor pages)
            └─ usage.<hmac>.json  in  %APPDATA%\com.nyanako.tokenbar\cursor-cache                      (default profile)
                                  or  %LOCALAPPDATA%\com.nyanako.tokenbar\cursor-profiles\<UUID>-<root digest>  (added profile)
                 └─ engine scans the newest copy of each account's file as an extra Cursor root
                    (files only, never a directory; only while the takeover is on)
```

| Piece | Where |
| --- | --- |
| Login reader, shared with the Grok Bot Cursor fallback | `crates/tb_core_ffi/src/cursor_desktop.rs` |
| Walk, file, registry, takeover decision, Billing V2 switch, sidecar and commit | `crates/tb_core_ffi/src/cursor_sync.rs` |
| Billing V2 walker (`query-billing-items`, item mapping, S7 principal rule) | `crates/tb_core_ffi/src/cursor_billing_v2.rs` |
| Takeover applied to the process scan, recheck, exports | `crates/tb_core_ffi/src/lib.rs` (`capture_process_context`, `recheck_cursor_takeover`, `tb_set_cursor_sync`, `tb_cursor_sync`, `tb_validate_cursor_profile`, `tb_cursor_present`) |
| Added-profile database checks (handle-first) | `crates/tb_core_ffi/src/agent_storage_windows.rs` (`open_foreign_file`, `drive_type`, `directory_identity`) |
| C# surface | `src/TokenBar.Interop/CursorSync.cs`, `TbCore.SetCursorSync` / `CursorSync` / `ValidateCursorProfile` / `CursorPresent`; `src/TokenBar.Core/CursorSync.cs` (controller, `configRejected`), `CursorProfileStore.cs` (local-only list of added folders) |
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

## Billing V2 accounts (CURSOR-BV2-W, port of macOS #554 and #559)

Cursor moved some accounts to a new billing system ("Billing V2", syrtis#520).
For those, the old endpoint's page 1 is a 2xx JSON object without
`usageEventsDisplay`. All of Billing V2 lives in Rust; C# only shows the four
fixed reasons below.

- **Switch (D1a).** The old walk runs first every round and keeps every result
  it had. Only that page-1 shape switches the profile's round to the V2
  walker; any other answer (`[]`, a malformed `usageEventsDisplay`, a status)
  is what it was. An unmigrated account never sends a V2 request.
- **V2 walk.** `GET query-billing-items?scope=user&startTime&endTime&pageSize=100[&cursor]`
  with the same client (no redirects, https only) and the same sensitive
  cookie; the URL is a constant base plus `query_pairs_mut`. Windows are UTC
  calendar months, newest first, at most the current one and the 11 before
  it; an earlier month ends 23:59:59Z, the current one now. The cursor is at
  most 4096 bytes and never repeats (else `partial`, `too_many_pages`); pages
  (`MAX_V2_PAGES` = 1280, unmeasured) and body bytes (64 MiB) are capped over
  the whole walk, inside the round's remaining budget. A background round
  reads the last three months plus any month V2 has never read (and, with no
  old rows, stops after three empty calendar months in a row); Sync Now reads
  the whole range. 3xx/401/403/429/5xx map as on the old walk;
  `availability: missing_team_id` is `billing_v2_team_required`, any other
  `availability` `billing_v2_unavailable`. Any refusal or partial walk
  writes nothing.
- **Items.** Only `billingItemId`, `eventTime`, `product`,
  `displayExtras.model`, the four token meters, `pricing.currency` and
  `.contracted`, `hasTokenMeters` and `principal.{type,id}` are read
  (`userEmail` never). Only `product == "cursor"` rows are written (D3), with
  no conversation id (sessions per account-day, D4); `contracted` 0 writes no
  `chargedCents` (the engine prices the row locally, D2), a positive USD
  amount writes integer cents from its digits (at least 1). Rows whose
  `eventTime` does not parse, or with no model, are dropped and counted
  (`droppedRows` on the profile's status). Items are deduped by
  `billingItemId` in memory only.
- **S7, principal (macOS #559).** Every item, written or not, must be on the
  cookie's own user id (any `type`), or on one other non-empty id whose
  principal `type` is exactly `user`; the first such item fixes that id for
  every later page and month of the walk. A second other id, another id whose
  `type` is not exactly `user` (`User`, `team`, missing), an empty id or a
  missing principal ends the walk with `billing_v2_principal_mismatch`, and
  nothing is written. Neither id is stored, written or put in a reason.
- **History sidecar (D5, D6).** The live `usage.<hmac>.json` stays the one
  file the engine reads. Beside it, `.cursor-sync.history.<hmac>.json` (dot
  prefixed, never scanned; the same name as macOS) holds the old rows frozen
  at the first V2 commit, the cutover (the newest frozen row, clamped to now),
  the months V2 has read, and the SHA-256 of the live file the last V2 commit
  wrote. A V2 commit writes frozen rows, then the earlier V2 rows of months
  not re-read with rows, then this walk's rows after the cutover; a month
  that answers empty keeps its rows. A sidecar whose digest no longer matches
  the live file (an older build or an old walk replaced it) is not trusted:
  the next V2 commit freezes the live file afresh. No old rows and no V2 rows
  is `billing_v2_no_items` and writes nothing. A successful old walk deletes
  the sidecar (the full old history is back).
- **Windows storage.** Both files are read and written only through the
  storage contract above (`secure::read`, secure temp, secure replace), under
  the dir's secure lock; the sidecar is one of Syrtis's names for every
  cleanup (disable, profile removal, account switch, other candidates), and a
  duplicate profile keeps its own account's sidecar with its file. A
  same-stem sidecar Syrtis cannot read (a directory, one failing the
  contract, one over 128 MiB, or one that does not parse) in the dir a
  commit would use stops the round with `write_failed` before any request,
  old or V2; one in the profile's other dir never refuses (its cleanup
  reports it). The old walk's commit never fails on a sidecar: it deletes
  it, best effort, only after the live file was written, and a directory,
  junction or symbolic link at a sidecar name is spared there (disable still
  reports it).
- **Reasons** (each shown with its own line that keeps the code, macOS copy
  verbatim): `billing_v2_team_required`, `billing_v2_unavailable`,
  `billing_v2_no_items`, `billing_v2_principal_mismatch`.

## Several Cursor profiles (CMP-W, port of macOS #532)

Besides the default Cursor profile, the user can add folders Cursor is started
with using `--user-data-dir`. Each is synced on its own; their usage adds up to
one Cursor total. In this build added profiles cannot be set up yet: the core
and its C# plumbing are in place, but no UI adds a folder and the app pushes
no added profile (WB11); the Accounts page adds them in a later slice (W2).

- **Input.** `tb_set_cursor_sync` takes `profiles: [{id, root}]`: an uppercase
  UUID made by C# and the folder. Rust derives the database,
  `<root>\User\globalStorage\state.vscdb` (layout measured with
  `cursor --user-data-dir`, CMP-W U4), and every Syrtis dir. Whole-push
  refusals: bad JSON or an unknown key, more than 32 profiles, an id that is
  not an uppercase UUID, a duplicate id, and (enabling) the default dir's
  storage. A root that fails the path rule (`claude_config_dirs::normalize`:
  absolute drive path; no UNC, `\\?\`, device paths, reserved names, drive
  roots) or is Cursor's own `%APPDATA%\Cursor` is refused by index and the
  other entries register. A folder that does not exist, is unmounted or cannot
  be read is accepted: it reports its own state at sync time. On any refusal
  the registry keeps only profiles the refused push also lists (none when its
  list cannot be read), so a profile the user removed never keeps sending its
  token; C# then shows `configRejected` and sends no sync, scheduled or
  pressed, until a push is accepted. An off push is accepted whatever its
  profiles.
- **Reading an added profile, every sync.** The root's drive letter must be a
  fixed drive (`GetDriveTypeW` on `X:\`) before any I/O, so a network,
  removable or WSL drive letter is never touched (an unmounted letter is "not
  signed in"). Then the database is opened handle-first: `CreateFileW`
  without following a final reparse point and without `FILE_SHARE_DELETE` (so
  the name cannot be renamed, deleted or replaced while held). On that handle,
  before anything else: its drive-letter final path
  (`GetFinalPathNameByHandleW`, `VOLUME_NAME_DOS`) must not be UNC
  (`db_remote`; a file with no drive-letter path goes on to the next check;
  measured on 188, a file on a volume mounted only at a folder still has one,
  through the mount folder), and the volume the
  file is on must be a fixed drive: the handle's volume GUID path
  (`\\?\Volume{…}\`, `VOLUME_NAME_GUID`) goes to `GetDriveTypeW`, so a
  junction or mount folder under a fixed root that leads onto a removable,
  optical or other non-fixed volume is `db_not_fixed_drive` too (a file with
  no GUID path, a network file among others, is refused the same way).
  Then: a regular disk file with no reparse attribute (symlinks, junctions
  and cloud placeholders refused), a volume with persistent ACLs,
  and an owner that
  is the current user or BUILTIN\Administrators (Cursor once run elevated;
  measured owner for a normal run is the user, U4). SQLite then opens the same
  name read-only with `DEFENSIVE` and `trusted_schema=OFF`, and the file behind
  SQLite's own handle (`SQLITE_FCNTL_WIN32_GET_HANDLE`) must be the one the
  checks ran on (volume serial + 128-bit file id), else `db_changed`.
  `SQLITE_OPEN_NOFOLLOW` is not used: on Windows it refuses nothing (measured,
  CMP-W0). An ancestor junction onto the user's own folder is allowed (no
  final-path comparison; a duplicate is counted once anyway). Refusals are
  fixed reasons: `db_not_fixed_drive`, `db_symlink`, `db_not_file`,
  `db_no_acl_volume`, `db_remote`, `db_foreign_owner`, `db_changed`,
  `login_unreadable`.
- **The default profile** reads what every release read, now with `DEFENSIVE`
  and `trusted_schema=OFF`; it has no owner or reparse checks. Grok Bot reads
  only the default profile's database.
- **Rounds.** Profiles are walked one at a time under one time budget: 10 min
  for "Sync now" and while some profile that could write a file has none;
  60 s otherwise. A profile whose last round, with the same id and root
  (folded like `duplicate_key`, so a respelling is the same root), was
  `duplicate`, `notSignedIn`, `expired` or a refusal of its login or database
  does not hold the background budget at 10 min (it may get 10 min again once
  its config changes, or after a round it ran out of time); the same holds
  for a profile with no installation key (`key_unavailable`) or no storage
  (`storage_unavailable`). This is decided per profile, not per account: in
  a background round, a profile that signs in to an account with no file yet
  walks it under the 60 s budget first, so a long history can end that round
  `partial` and is completed under the full budget the round after. The same
  holds for a profile coming back from `storage_unavailable` with the same
  root: the budget remembers it by id and folded root, not by dir. "Sync
  now", or a round in which another profile still needs the full budget,
  gives those walks 10 min instead. An
  account already covered this round by an earlier profile is not walked
  again; that profile reports `duplicate` with `duplicateOf` (a profile id,
  never the account), and any other account's file it still holds is removed
  under its lock (its current account's copy, if any, stays; the takeover
  keeps the newest copy by name; a file that cannot be deleted is reported by
  profile id in the status's `cleanupFailedProfiles`). A failure covers the
  account too (a second session would only repeat it, e.g. a 429), except one
  specific to that session or
  this machine: expired, removed mid-walk, `client_unavailable`,
  `write_failed`. Before every page and under the commit lock the walk checks
  that sync is on and its `(id, root)` is still listed (the default profile:
  that sync is on with the same default dir; adding or removing added
  profiles never stops its walk); a profile removed or re-rooted mid-walk
  sends no further page and writes no usage file. Its dir is not created when
  the removal is seen before the commit starts; a removal that races the
  commit's first check can leave an empty dir and lock file, which the next
  accepted push cleans. The status
  carries a headline (`ok` when every profile is `ok` or `duplicate`, else the
  first other state; a `notSignedIn` profile is ignored while another synced),
  `changed` (some profile's usage file content changed: new bytes written,
  or a superseded usage file removed, an orphan temp not counting; a walk
  whose bytes equal the file's is not
  rewritten, only its modified time moves to now so `lastSuccessMs` dates
  the walk (where that time cannot be set, the same bytes are rewritten to
  move it), and is not a change; C# refreshes on it) and per-profile
  entries with id, state, reason and `duplicateOf` only, plus
  `cleanupFailedProfiles`: ids of profiles one of whose files of ours could
  not be deleted this round (a duplicate's other-account file, or a
  superseded file after a commit in any of the profile's dirs; a file
  failing the storage contract is never deleted). It can contain `default`,
  the default profile's id, which is not a stored profile.
- **Takeover.** `extra_scan_paths["cursor"]` holds the newest file per
  account name (`usage.<HMAC(sub)>.json`) across the current config's
  profiles (an added profile's dir and its `.secure` sibling), never a
  directory, so a stale copy left by a failed delete is never counted and a
  removed profile's leftovers are never scanned.
- **Cleanup.** Only `usage.<64 hex>.json`, the temp prefix and the lock are
  Syrtis's. An added profile's dir is named `<UUID>-<root digest>`: the
  first 16 hex of SHA-256 of its root folded like `duplicate_key` (case and
  `/` vs `\` ignored, so a respelling of the same folder is the same dir; a
  digest, not the path). A new root for the same id is therefore another dir,
  and the old one is no longer listed.
  Disabling cleans every default and added-profile candidate (each `.secure`
  level); an accepted on push cleans every profile dir it does not list
  (non-fatal, `cleanupFailed` / `cleanupFailedProfiles` in the result), under
  the same secure lock a walk commits under, whatever the config before it
  was (so after a refused push or a restart too). A listed profile's dir is
  not opened. Only real profile dirs (`<UUID>-<root digest>` or a bare
  `<UUID>`, each also with `.secure`) are entered (junctions and symlinks at
  any level are skipped), files are deleted through the secure delete, and an
  emptied dir is `rmdir`ed. A bare `<UUID>` dir comes from an earlier W1 build
  (test machines only; added profiles never shipped): it is never listed, so
  an accepted push or turning sync off cleans it like a removed profile's.
  The `.cursor-sync.root` file review round 2's build left in it (nothing
  writes that file now) is removed best-effort, with the lock, once that
  dir's usage files are gone; in a dir failing the storage contract nothing
  is deleted, and that file alone never counts as a cleanup failure.
- **Limits of the folding.** Roots are folded with Unicode lowercase and
  `/` to `\`, not with NTFS's own case table. Two added profiles whose roots
  differ only by case (folders that can both exist inside a directory with
  NTFS case sensitivity turned on, or differ by a character whose Unicode
  lowercase differs from NTFS's upper-casing) are still two ids, two dirs
  and two walks. The fold decides two things for them: the add-time check
  refuses the second such folder as already listed (`duplicate`), and a
  case-only re-root of one id keeps that id's dir and file.
- **Path length.** The longest path Syrtis writes, with the `.secure`
  fallback taken at all three levels
  (`%LOCALAPPDATA%\com.nyanako.tokenbar.secure\cursor-profiles.secure\<UUID>-<digest>.secure\usage.<64 hex>.json`),
  is about 211 characters plus the length of the profile folder's name under
  `C:\Users` (longer with a domain suffix, or for a profile moved or
  redirected elsewhere). Past MAX_PATH (260) the two kinds of call differ:
  the `std::fs` calls (listing, metadata, `rmdir`) add the `\\?\` prefix
  themselves, while the `CreateFileW`-based secure calls (the storage
  contract's opens, `remove_file`, `refresh_if_same`, the temp write and
  replace) pass the plain path. So such a path would leave commits failing
  (`write_failed`) and removals reported as `cleanupFailed`, while reads
  still see the file. Whether that happens depends on Windows long-path
  support for the process; not measured.
- **Add-time check.** `tb_validate_cursor_profile` (validate-only, metadata
  only, no database opened) refuses a full list, a path the rule refuses, a
  drive that is not fixed (checked first), Cursor's own folder and a listed
  folder, by folded path or by directory identity (a junction to a listed
  folder is the same folder); an unreadable folder never counts as a match.
  Results carry reason codes, never a path.

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
  history resolves it (following its `.secure` fallback). Added profiles live
  in `<local root>\cursor-profiles\<UUID>-<root digest>` under
  `%LOCALAPPDATA%`, resolved the same way (the digest: below, Cleanup). `tb_set_cursor_sync` refuses any key besides `enabled`,
  `cliTakeoverConfirmed` and `profiles`.
- Every file operation goes through `agent_storage_windows`: owner = current
  user, protected DACL with exactly {current user, SYSTEM}, final-component
  reparse points refused, existing objects used only if they already meet that
  contract (a loose pre-created dir makes the sticky `.secure` sibling the dir).
- Writes: secure lock (`.cursor-sync.lock`), secure temp
  (`.cursor-sync.tmp-*`, never matching `usage*`), flush, secure replace.
- Only a complete walk replaces the file. Turning sync off deletes the synced
  file, temps and Billing V2 sidecars from every sync dir that exists: both roots
  (`com.nyanako.tokenbar`, `com.nyanako.tokenbar.secure`) times both child
  names (`cursor-cache`, `cursor-cache.secure`), because the `.secure`
  fallbacks are sticky and the dir in use can move after a file was written
  elsewhere. No directory is created on the way (a candidate deleted between
  the existence check and the open could be re-created empty), but a cleaned
  candidate that had no `.cursor-sync.lock` gets one. A candidate that meets
  the contract is cleaned under its own secure lock. One that is a real dir
  but fails the contract (never written by Syrtis, or written while it met the
  contract and loosened since) fails the cleanup if it holds a Syrtis-named
  file or cannot be listed. A candidate that is itself a junction or another
  non-directory is skipped; a junction at an ancestor (a root) is followed,
  as the storage contract allows, so only same-user planting reaches it. A Syrtis-named file that fails the storage contract is never
  deleted; at disable time that is the error `cleanupFailed` (sync is off all
  the same), and during a walk it is left in place without a report. Putting
  such a file into the protected dir needs a process running as the same
  user, which this threat model does not cover.

### Roaming and two PCs

The default profile's file lives in roaming `%APPDATA%`, like quota history,
and it holds no secret (only timestamp, model, token counts, cost fields,
conversation id). Its name is an HMAC of the Cursor user id under the
installation key, which also roams. A second PC that receives the roamed file
and has an empty CLI cache may take over from it for the same account; that is
accepted.

Added profiles are local only (maintainer decision 2026-11-02, CMP-W WB5):
the list of folders is in `%LOCALAPPDATA%\TokenBar\cursor-profiles.json`,
never in the roaming `settings.json`, and their usage files are under
`%LOCALAPPDATA%\com.nyanako.tokenbar\cursor-profiles`. So another PC's
folders (and the consent given there) never arrive on this one, and two PCs
never delete each other's profile files: cleanup touches the local scope
only.

## Threat model

| Adversary | Covered | How |
| --- | --- | --- |
| Another standard user on the PC | yes | protected DACL {user, SYSTEM} |
| Another process of the same user | no | it can read Cursor's own `state.vscdb` anyway |
| Administrators / SYSTEM | no | SYSTEM is granted on purpose (backup, AV) |
| Local planting (pre-created dir, junction, loose ACL) | yes | `agent_storage_windows` contract and `.secure` fallback |
| Network attacker, proxy, DNS | yes | TLS, https-only, constant endpoint, redirects never followed (a 3xx means `expired`), body caps (64 MiB per walk) |
| Concurrent readers | partly | see below |
| Other local users and an added profile's folder | no (not Syrtis's to change) | A folder readable by other local users (for example one directly under `C:\`, whose children inherit Authenticated Users Modify, measured on 188) exposes the token to them through Cursor's own storage; Syrtis neither adds nor removes that exposure. Others with write access can also alter that database or plant `-wal` / `-journal` sidecars, which SQLite opens by path (integrity). Syrtis checks the database file's owner and type, not the folder's DACL (CMP-W WB6, decision 2026-11-02). |

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
- Added profiles (CMP-W): no bundle id in the input; a refused root is
  reported by index and the rest register (macOS refuses the whole push); an
  off push with an unknown key is still `invalidJson`; the added-profile
  reader is handle-first with a fixed-drive rule (macOS: `SQLITE_OPEN_NOFOLLOW`
  plus an owner uid check), and accepts BUILTIN\Administrators as an owner;
  added profiles and their list are local only.
- Billing V2: the sidecar and the live file are read through the storage
  contract, so a live file or sidecar failing it stops a V2 round
  (`write_failed`; macOS reads a non-regular live file as empty). An added
  profile's dir is resolved (and created) only at commit, so a V2 round
  plans from the dir resolution would pick, decided by the same rule
  without creating anything (`agent_storage_windows::peek_secure_storage_directory`;
  on an error there the old walk still runs and reports its own outcome,
  its commit failing as before, while a V2 round stops `write_failed`
  before its requests), and the commit re-reads under the lock in the dir
  it resolves. When that dir holds neither file (the first commit after
  the sticky `.secure` sibling appeared), the history in the profile's
  other dir is carried over (frozen rows, cutover, fetched months) before
  that dir is cleaned; a readable live file there is carried over even
  beside an unreadable sidecar (frozen whole).
- Accepted residual of the carry-over (maintainer decision 2026-11-11): the
  other dir's folder is not checked. The sibling exists in practice only
  because the preferred dir failed the folder-level contract, and a folder
  gate would stop the carry-over from ever running, dropping V2 history
  older than the 12 re-read months. So Syrtis reads the live file and
  sidecar from a folder that may have a loosened DACL or another owner, or
  that is a junction or other reparse point. Each file is still checked:
  it is read only if it is a regular, non-reparse file meeting the exact
  owner/DACL contract at its path and within the size cap. Afterwards
  cleanup leaves the files in a contract-failing folder (reported as
  `cleanupFailedProfiles`) and skips a junction without a report; the
  newer copy in the commit dir is the one the engine reads. Files read back
  are capped at 128 MiB (`MAX_STORED_FILE_BYTES`), and a V2 commit that
  would write a bigger live file or sidecar writes nothing
  (`write_failed`).
- The old walk's commit deletes the sidecar after the live write (macOS
  deletes it before; a failed live write there leaves a sidecar-less V2
  file, which the next V2 round freezes whole).
