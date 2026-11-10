# Shared Rust engine consumer

TokenBar for Windows consumes the public
[`Nanako0129/tokscale-core`](https://github.com/Nanako0129/tokscale-core)
repository through an immutable Git submodule. Shared parser, scanner, cache,
pricing, and aggregation changes land in the engine repository before either
app consumer advances its reviewed pin.

| Field | Value |
|---|---|
| Path | `vendor/tokscale-core` |
| Repository | `https://github.com/Nanako0129/tokscale-core.git` |
| Reviewed pin | `e156d67caa9708458fd2030f379d3c2a77ff05bf` |
| TokenBar alignment | macOS pins the same commit `e156d67c` (Nanako0129/syrtis #558), which carries engine #88 over `fc339e98` |
| Engine alignment | `fc339e98237fcb71f32ab1208b61aa77c3c35c02` → `e156d67caa9708458fd2030f379d3c2a77ff05bf` (engine `main`; previous advance `15204f4e` → `fc339e98`) |
| Native consumer baseline | `704426e8df9acfb8e82fe4bf3b7ed3e5adbc2fea` |
| Windows pre-migration baseline | `68e2541c5e9adb14a47433f8b25e26b0be84d1fc` |
| Upstream and local-patch ledger | Immutable [`UPSTREAM.md`](https://github.com/Nanako0129/tokscale-core/blob/e156d67caa9708458fd2030f379d3c2a77ff05bf/UPSTREAM.md) |

> **Warning:** Do not edit shared source on a consumer branch. Engine changes
> must pass review in `tokscale-core`; this repository then advances only the
> reviewed gitlink and runs the Windows consumer gates.

## Current pin: `e156d67c`, engine PR #88 (Gemini report grouping)

`fc339e98` → `e156d67c` is 3 engine commits and one first-parent merge, PR #88.
It extends `builtin_grouping` (`src/model_alias.rs`, the presentation-only seam
the Grok `-build` rule uses): `gemini-4-argon-c` and `gemini-4-argon-trial`
group as `gemini-4-argon`; `gemini-3.5-flash-{high,medium,low,extra-low}` group
as `gemini-3.5-flash` (exact labels only, maintainer decision 2026-11-08). It
applies to the model, monthly and hourly reports. Pricing, cache identity,
graph keys and export keep the raw id, and nothing is re-parsed.

Consumer side, matching Syrtis #558:

- `ModelGrouping.GroupId` (`src/TokenBar.Core/ModelGrouping.cs`) mirrors the
  same labels for graph-derived rows, which the engine's grouping does not
  reach. `Fixtures/model-grouping-cases.json` (28 rows, shared with macOS) is
  checked against it (`ModelGroupingTests`) and against the engine
  (`model_grouping_cases_match_the_engine`); both require at least 28.
- `ModelReportFold.TabEntries` (port of macOS `tabEntries(clientIds:)`): when
  the selection spans more than one client, rows sharing model and provider
  merge into one with per-client parts in the tooltip (one part per canonical
  client; the raw ids `claude` and `claude-code` are one). The Overview Models
  card reads the same rows, so with the whole selection it merges one model and
  provider across all clients. A row flagged by
  `CostPlausibility` is never merged. The Models lens and the Stats favourite
  model both read these rows (`DashboardView.SelectedModelEntries`).

## Previous pin: `fc339e98`, the 2026-11 upstream sync (engine PRs #75 to #87)

The reviewed pin is the merge commit of tokscale-core PR #87 on the engine's
`main`, covering `15204f4e` → `fc339e98`: 13 first-parent merges, PRs #75 to
#87. The diff touches 21 files (`src/lib.rs`, `src/message_cache.rs`,
`src/scanner.rs`, `src/source_context.rs`, `src/pricing/lookup.rs`,
`src/pricing/aliases.rs`, `src/provider_identity.rs`, `src/sessions/claudecode.rs`,
`antigravity_cli.rs`, `cline.rs`, `droid.rs`, `hermes.rs`, `hindsight.rs`,
`kilo.rs`, `kilocode.rs`, `qwen.rs`, `roocode.rs`, `tests/hermes.rs`,
`Cargo.toml`, `Cargo.lock`, `UPSTREAM.md`). `src/clients.rs` is not among
them.

| PRs | What |
|---|---|
| #75, #76 | #75 adds a `get_window_usage` cc-mirror test only. #76 applies `excluded_scan_paths` where each source is selected, with a final sweep, instead of only on the scan task list (narrowing only) |
| #77, #78, #79 | Claude parser fixes (Claude `parser_version` 4 → 5), resume from a cached checkpoint (macOS arm64 only), and a subagent's fingerprint no longer lists its parent session |
| #80 | Droid replies counted on their own dates (Droid 2 → 3) |
| #81 | Hermes reasoning counted once; Qwen cached prefix and reasoning counted once (Qwen 1 → 2) |
| #82 | Roo Code / Kilo Code / Cline VS Code roots and parser (RooCode, KiloCode, Cline 2 → 3), `RESOLVER_CONTRACT_VERSION` 3 → 4 |
| #83, #84 | Pricing: Google-hinted rows from Google's own entry, the hinted vendor's root row wins, Antigravity route labels folded; then a pricing memo (output byte-identical) |
| #85, #86 | Ledger text only |
| #87 | Goose database under `%APPDATA%\Block\goose\data\sessions\`, `RESOLVER_CONTRACT_VERSION` 4 → 5. The one Windows-visible item |

Measured on this advance, not relayed:

- `ClientId` is unchanged (`clients.rs` is not in the diff), so
  `Fixtures/engine-client-ids.json`, the C# registry and the C header needed no
  edit. `engine_client_ids_match_the_fixture` passes on 188 without a fixture
  change. `crates/tb_core_ffi` compiled against the new engine and needed no
  production change.
- `CACHE_FORMAT_VERSION` stays 4 (`message_cache.rs:39`). Claude shards are
  written in an envelope of their own, `CLAUDE_CACHE_FORMAT_VERSION = 100`
  (`message_cache.rs:48`), on every target. Only the checkpoint inside it is
  macOS-arm64-only (`CLAUDE_CHECKPOINTS`, `sessions/claudecode.rs:737`), so on
  Windows the shards take the new envelope and pv 5 but never carry a
  checkpoint, and a grown transcript is still parsed from byte 0.
- `RESOLVER_CONTRACT_VERSION` is 5 (`source_context.rs:183`). The consumer
  grep for it is empty: nothing in `crates`, `src` or `include` binds a scope
  approval to it. The only persisted source-context id is the one
  `GraphSnapshotStore` writes into each snapshot envelope, where a differing
  id reads as `ContextMismatch` (`src/TokenBar.Core/GraphSnapshotStore.cs:104`), so by the code
  path the first launch after this update waits for the live pass instead of
  drawing the restored picture. Not observed on a Windows machine.
- The root `Cargo.lock` gains `sha2-asm` 0.6.4 (same entry as the engine's own
  lock; its dependency `cc` was already locked). The engine enables sha2's
  `asm` feature for macOS arm64 only (`Cargo.toml`, target-specific table), so
  the Windows build does not compile `sha2-asm`; the lock lists it because a
  lockfile covers every target, and `--locked` fails without it.
- On 188 (`win188`, lane `x4-pin`): `cargo test -p tb_core_ffi --locked`
  passes, 854 passed, 0 failed, 11 ignored, including
  `engine_client_ids_match_the_fixture`. `cargo clippy -D warnings` still
  fails, with the same 28 lib / 31 test errors, the same multiset of messages,
  on the commit before this pin (`dcf1b68`) and on this one; all are in
  `crates/tb_core_ffi`, none in the engine, none from this advance.
  `dotnet test` (TokenBar.Core.Tests) passes, 2288 passed, 0 failed, 0
  skipped, and `build-app` (`dotnet build src/TokenBar.App`, x64, win-x64)
  succeeds with 0 warnings and 0 errors. The gates ran on the gitlink and
  `Cargo.lock` of this commit; this document was edited afterwards.
- Not compared against a real Windows corpus: no before/after totals were run
  on Windows. The figures below that carry numbers come from the engine's
  ledger or from macOS measurements and say so.

### Release-note items

These are the items the next Windows release that carries this pin should
state. The repository keeps no unreleased-notes file; `.github/release-notes/`
holds one file per released version, written at the `chore(release)` commit.

1. **Claude is re-parsed once.** The first scan after updating re-parses every
   Claude transcript once (Claude `parser_version` 4 → 5). By the code path
   (`message_cache.rs:1573-1580`) the upgrade loses nothing: an entry on an
   older Claude `parser_version` is migrated rather than dropped, so turns that
   only the cache still holds survive. Observed on 188 (2026-11-04, X4 build, real
   cache): the first launch rewrote all 246 non-empty pv4 shards as pv5 within about
   10.6 s of launch, the next launch rewrote none, and no crash or error-log entry
   appeared. Whether any cache-only turn was kept was not checked (totals were not
   compared). On macOS the same step took 25 to 27 s for 256 shards. Claude input tokens fall because input is now taken from the
   line that carries the cache split instead of the larger of a prompt
   snapshot and the split (#77): on the maintainer's Mac, Claude only, input
   47,960,589 → 19,631,634 (-59.1%) and cost -0.23%, the change confined to
   two Claude Code rows routed to non-Claude models. The size depends on the
   corpus. Not measured on Windows.
2. **Going back to an older version loses cache-only Claude history unless the
   cache is backed up before pressing Install in the update dialog.** The
   updater relaunches the new build at once, so afterwards it is too late. See
   the procedure below.
3. **Goose usage is detected on Windows.** A Goose database at
   `%APPDATA%\Block\goose\data\sessions\sessions.db` is now found; it is
   ranked after an explicit `GOOSE_PATH_ROOT` and before the XDG and legacy
   candidates, so a stale leftover cannot mask it. `GOOSE_PATH_ROOT` itself
   behaves as before (UPSTREAM.md, decision D7). 0 → N for a profile with that
   file; not seen on a Windows machine, because 188 has no Goose data.
4. **Pricing.** Rows with a Google provider hint are priced from Google's own
   `gemini/<model>` entry; the hinted vendor's own root row wins over a longer
   reseller row; Antigravity `gemini-3.8-flash-*` route labels fold onto
   `gemini-3.8-flash`. Engine measurement on the maintainer's data, one frozen
   pricing snapshot: total -$521.12 (-1.54%), anthropic -$452.16
   (`claude-sonnet-5-5`), openai -$68.97 (`gpt-5.3-codex`), google and xai 0;
   tokens and messages identical. One machine, not re-measured on Windows. The
   memo (#84) leaves output byte-identical. Cached Claude, Muse, DSH, Junie and
   OpenCodeReview messages keep their old folded-label names until re-parsed.
5. **Other clients move.** Roo Code and Kilo Code (and Cline) are read under
   `%APPDATA%\Code\User\globalStorage\<extension-id>\tasks` (`scanner.rs:970-986`), and their cost is
   the extension's own; Hermes and Qwen totals drop where reasoning and cached
   prefix had been counted twice; Droid replies are counted on their own
   dates. Roo, Kilo, Cline, Droid and Qwen re-parse once. None of this was
   compared against Windows data.

### Windows backup and rollback of the Claude cache (D8)

An engine at Claude `parser_version` 4 reads a pv 5 shard as stale and
rewrites it from an empty map, dropping the Claude turns that only the cache
still holds; an engine without the format 100 envelope does the same
(`message_cache.rs:46-47` says "rolling back needs the Claude-cache backup";
`message_cache.rs:1573-1574`: a downgrade cannot be migrated). The only way
back is a backup taken before the new build first scans.

Which releases this covers: v0.3.0 (pins `d6512f5`, Claude pv 4) up to and
including v1.3.0 (pins `2d64ec42`, Claude pv 4); `ClientId::Claude => 4` at
both pins (`message_cache.rs`), and no advance in between moved it (see the
sections below). v0.1.0 to v0.2.2 (v0.2.2 pins `84e0d66`) predate the
format 4 / pv 4 layout, so v0.3.0 is the oldest version this procedure can
roll back to.

Where the cache is, derived from the engine:

| Step | Source |
|---|---|
| The app does not set `TOKSCALE_CONFIG_DIR` (only tests and CI do), so unless the user set it, the engine's default applies | `paths.rs:55-59` (the override), `paths.rs:52` (doc: Windows is `dirs::config_dir().join("tokscale")`), `paths.rs:68-70` (the code) |
| `dirs::config_dir()` on Windows is the Roaming AppData known folder (FOLDERID_RoamingAppData), so the config root is `<ApplicationData>\tokscale`, usually `%APPDATA%\tokscale`. The commands below ask Windows for that folder instead of reading the environment variable | `dirs` 5 (`Cargo.toml:36`); `clients.rs:93-95` states default Windows scans read "the same %APPDATA% root used by cache writers"; `docs/lite-distribution.md:18` |
| The cache is `<config>\cache` | `paths.rs` `get_cache_dir` (`<config_dir>/cache`) |
| Shards are under `source-message-cache-v2`, one folder per client namespace | `message_cache.rs:57`, `message_cache.rs:2101-2103` |

So the folder to keep is
`<ApplicationData>\tokscale\cache\source-message-cache-v2\claude\` (or
`<TOKSCALE_CONFIG_DIR>\cache\source-message-cache-v2\claude\` when that
variable is set to a non-empty value; then set `$claude` below to it). Seen on
188 (read-only listing): that folder holds 256 files `shard-00.bin` to
`shard-ff.bin`, and the first bytes of each are `04 06 63 6c 61 75 64 65 04`,
that is format 4, the string `claude`, `parser_version` 4. A shard written by
this pin starts `64 06 63 6c 61 75 64 65 05` (format 100, `claude`, pv 5),
which is what `bincode::options()` writes for the envelope fields at
`message_cache.rs:2474-2485`. The first bytes do not prove a restore: a pv 4
engine that rebuilt a shard from an empty map writes the same
`04 06 63 6c 61 75 64 65 04`. The restore is therefore checked by comparing the
restored folder with the backup, not by looking at the header.

Windows PowerShell 5.1 and PowerShell 7 both run these commands. They were
exercised on 188 against a scratch folder (backup equals source and a changed
file shows as a difference; restore leaves no nested `claude\claude`; running
the restore twice gives the same result); not against a real Claude cache.

Setup, used by both procedures (paste it first in each PowerShell window):

```powershell
$claude = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'tokscale\cache\source-message-cache-v2\claude'
$h = { param($d) Get-ChildItem -LiteralPath $d -File | Sort-Object Name | ForEach-Object { '{0} {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name } }
```

Backup, before pressing Install in the update dialog. The in-app updater
quits Syrtis and relaunches the new build at once
(`src/TokenBar.App/UpdateFlow.cs:219-222`, `restart: true`), so there is no
moment between the update and the first scan in which to back up. A user who
has already updated can no longer back up the pv 4 shards: the first scan has
rewritten them.

1. Quit Syrtis (tray menu, Quit) and any other program that uses the tokscale
   engine, such as a `tokscale` CLI, then run the setup above.
2. Copy to a new, dated destination outside `...\tokscale\cache`. The
   destination does not exist yet, so the copy cannot nest and an earlier
   backup is never overwritten:

   ```powershell
   $backup = Join-Path $env:USERPROFILE ('syrtis-claude-cache-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
   Copy-Item -Recurse -LiteralPath $claude -Destination $backup
   Compare-Object (& $h $claude) (& $h $backup)
   $backup
   ```

   `Compare-Object` printing nothing means the copy matches file for file
   (SHA-256 and name). Note the printed `$backup` folder name; the restore
   needs it. Optionally, also write down the Claude totals shown before the
   update.
3. Only then press Install.

Rollback to an older Syrtis (v0.3.0 or later), in this order:

1. Quit every program running the new engine: this Syrtis, a test build, any
   other tokscale build that writes the cache. The cache lock is advisory and
   taken only when the engine saves, so a running pv 5 process re-parses the
   restored pv 4 entries and writes them back as pv 5. Do not start the older
   Syrtis yet.
2. Run the setup, then set `$backup` to the folder the backup printed, and
   restore:

   ```powershell
   $backup = Join-Path $env:USERPROFILE 'syrtis-claude-cache-backup-<the stamp printed by the backup>'
   & {
     if (Test-Path -LiteralPath $claude) { Remove-Item -Recurse -Force -LiteralPath $claude -ErrorAction Stop }
     if (Test-Path -LiteralPath $claude) { throw 'claude folder still exists; quit every Syrtis/tokscale process and retry' }
     Copy-Item -Recurse -LiteralPath $backup -Destination $claude -ErrorAction Stop
   }
   Compare-Object (& $h $backup) (& $h $claude)
   ```

   `claude` is removed first and then recreated as a copy of the backup, so
   the result is `...\claude\shard-00.bin`, never `...\claude\claude\...`. The
   backup is only read, so the step can be run again and gives the same
   result. If a shard is still held open, `Remove-Item` stops the block
   (`-ErrorAction Stop`) before anything is copied, so a partial delete never
   turns into a nested `claude\<backup>` copy. The three lines sit in one `& { }`
   block so the first error ends all of them, also when pasted into a console.
3. Check that `Compare-Object` printed nothing. If it printed anything, do not
   start the older Syrtis; repeat step 2.
4. Install and start the older Syrtis. Optionally compare its Claude totals
   with the ones written down before the update.

The shard header (`04 06 63 6c 61 75 64 65 04`) is not a restore check, see
above.

Without a backup the loss is accepted. A pv 4 build that runs on the same
profile after a pv 5 build has written the cache drops the cache-only turns on
every scan, so do not run an older Syrtis or tokscale against this profile
between the update and the restore.

Known limit: neither the app nor the engine takes this backup, and a pv 5
build shares the one cache folder with older builds instead of using a cache
directory of its own. Both were considered and not done (user decision D1,
2026-11-02); the procedure above is the only protection.

## Historical: `15204f4e`, Antigravity extension client (engine PRs #70, #71, #73)

The reviewed pin is the merge commit of tokscale-core PR #73 on the engine's
`main`, covering `2d64ec42` → `af1cb144` (#71) → `52815d03` (#70) →
`15204f4e` (#73). The diff touches `src/clients.rs`, `src/lib.rs`,
`src/scanner.rs`, `src/source_context.rs`, `src/sessions/antigravity_cli.rs`,
`src/sessions/codex.rs`, `src/pricing/openrouter.rs` and `UPSTREAM.md`.

- #71 (`af1cb144`): new `ClientId::AntigravityExtension`
  (`antigravity-extension`, COUNT 42 → 43) reading
  `~/.gemini/antigravity/conversations/*.db` with the Antigravity CLI parser.
  Responses are deduplicated by response id across the IDE cache, the CLI and
  the extension inside the engine (`AntigravityFamilyGate`), with two accepted
  dependences recorded in the engine's `UPSTREAM.md`: the whole family must be
  requested together (a source outside the requested set cannot gate the
  others), and `modified_after` pruning can count the extension's copy of a
  response in place of an older copy that was pruned. On Windows the engine
  never prunes the CLI or extension dbs (`prune_scan_result_by_mtime` skips
  both lanes under `cfg!(windows)`; UPSTREAM.md: every db stays unpruned on
  Windows), so the caveat applies only to the IDE cache copy under
  `antigravity`. Windows `usage_tail.rs` scans with `modified_after`, so a
  tail window can attribute a response to the extension rather than an
  older, pruned IDE copy.
- #70 (`52815d03`): OpenRouter listing cache rates (pricing only).
- #73 (`15204f4e`): Codex scan pattern `*.jsonl` becomes `*.jsonl*`, so
  `.jsonl.zst` archives are scanned.
- The Windows registry gains `antigravity-extension` in the same PR as this
  pin: name "Antigravity Extension", color `#0369a1`, the antigravity icon,
  a member of the Antigravity tab group beside `antigravity` and
  `antigravity-cli`, and `ClientRegistry.QuotaOwner` folds it to
  `antigravity` like the CLI (all shared with macOS `cc13f64e`).
  `Fixtures/engine-client-ids.json` gains the id.
- Measured on this advance: `crates/tb_core_ffi` needed no production change.
  On 188, `cargo test -p tb_core_ffi --locked` passes (772 passed, 7 ignored,
  including `engine_client_ids_match_the_fixture`) and `dotnet test` passes
  (2238). `cargo clippy -D warnings` fails with the same 27 lib / 30 test
  errors on `d189b82` (before this pin), in files this advance does not touch.
  Not compared against a real Windows corpus.

## Historical: `2d64ec42`, DeepSeek Harness client (engine PR #66)

The reviewed pin is the merge commit of tokscale-core PR #66 (an outside
contribution) on the engine's `main`, covering `fcb85923` → `2d64ec42`. The
diff touches `src/clients.rs`, a new `src/sessions/dsh.rs`, `src/scanner.rs`,
`src/lib.rs`, `src/message_cache.rs`, `src/source_context.rs`,
`src/sessions/mod.rs` and `UPSTREAM.md`.

- New `ClientId::Dsh` (`dsh`, COUNT 41 → 42): DeepSeek Harness session event
  logs under `<home>/.dsh/sessions/<project>/<session>/session.v4.jsonl.zstd`,
  plus the pre-format-4 `session.jsonl.zstd` a migration leaves beside it,
  decoded through `zstd` (decoded bytes capped at 1 GiB). One message per
  `assistant/message` event: input is `usage.inputTokens` (already the
  uncached slice), cache read `cacheReadTokens`, output `outputTokens`, cache
  write `cacheWriteTokens`; provider and model from the step's
  `data.message.source`. Each `compaction/summary` counts as one message;
  subagent sessions count on their own, and steps a subagent inherits stay
  under the parent session.
- The Windows registry, icon and `Fixtures/engine-client-ids.json` gain `dsh`
  in the same PR as this pin, so `engine_client_ids_match_the_fixture` and
  `EveryEngineClientIsRegistered` stay green. Name "DeepSeek Harness", color
  `#001259`, icon the DSH desktop app icon, all shared with macOS.

Measured on this advance, not relayed:

- `CACHE_FORMAT_VERSION` stays 4 and `RESOLVER_CONTRACT_VERSION` stays 3. The
  only new cache namespace is `dsh` (`parser_version` 1); no existing
  namespace's `parser_version` moves, so nothing re-parses.
- The source-context descriptor moves: it includes every client's id, root
  and pattern, and the engine's macOS and Linux goldens change with it. By the
  code path (`GraphRequestCoordinator` keys `GraphSnapshotStore` on the
  source-context id), the first launch after this update finds no matching
  snapshot and waits for the live pass instead of drawing the restored
  picture. Not observed on a Windows machine.
- `crates/tb_core_ffi` needed no production change.
- Not compared against a real Windows corpus: the DeepSeek Harness data used
  to check this advance was recorded on the maintainer's Mac.

## Historical: `fcb85923`, Grok Bot attribution (engine PRs #68, #69)

The reviewed pin is the merge commit of tokscale-core PR #69 on the engine's
`main`, covering `8fc63ced` → `522ee8f2` (#68) → `fcb85923` (#69). The diff
touches `src/sessions/cursor.rs`, `src/scanner.rs`, `src/lib.rs`,
`src/message_cache.rs`, `UPSTREAM.md` and a new test module,
`src/grok_bot_attribution_tests.rs`.

- #69 (`fcb85923`): a Cursor usage event whose model starts with `grok-bot`
  (ASCII case-insensitive; the only known name is `grok-bot-default`) is
  tagged client `grok-bot` instead of `cursor`, in both the usage-events JSON
  and the CSV parser (`cursor_client_for_model`). A request naming `grok-bot`
  scans the Cursor lane (`enabled_clients` in both scanner builders), and the
  default all-clients list of `resolve_report_clients` gains `grok-bot`.
  Provider (`xai`) and cost (provider-reported cents) are unchanged.
- #68 (`522ee8f2`): performance only. `prune_scan_result_by_mtime` stops
  exempting Antigravity CLI dbs on macOS and Linux and prunes one when both
  the db and its `-wal` are older than `modified_after`
  (`antigravity_cli_db_mtime_ms`). Every db stays unpruned under
  `cfg!(windows)`, so it changes nothing on this platform; the pin carries it
  anyway.

Measured on this advance, not relayed:

- `CACHE_FORMAT_VERSION` stays 4. `RESOLVER_CONTRACT_VERSION` stays 3.
  `ClientId` is unchanged (`clients.rs` is not in the diff); `grok-bot` is a
  client string the Cursor parser emits, not a `ClientId`.
- Cursor's `parser_version` moves 3 → 4, so every cached Cursor parse
  re-parses once on first use: the synced and CLI `usage*.json|csv` files are
  unchanged on disk, so only the bump delivers the new tag. Other namespaces
  keep their cache.
- `pub` items: none added or removed (the only widened item is
  `message_cache::append_path_suffix`, now `pub(crate)`). `crates/tb_core_ffi`
  needed no production change.
- The source-context identity does not move: `source_context.rs` and
  `clients.rs` are untouched, so the descriptor and its golden vectors are
  unchanged. `GraphSnapshotStore` therefore still reads a pre-advance
  snapshot as a hit, and that snapshot carries Grok Bot usage under `cursor`;
  the live pass replaces it when it publishes (`SnapshotMaxAge` comment in
  `GraphRequestCoordinator`), so the old attribution can show until then.
- Effect on figures: the events whose model starts with `grok-bot` move from
  client `cursor` to client `grok-bot`. Cursor totals drop by exactly those
  events (tokens, messages, cost); "Grok Build & Bot" gains exactly them; the
  all-clients total is unchanged. Covered hermetically by
  `model_report::tests::grok_bot_usage_moves_from_cursor_to_the_grok_tab`
  (fails on `8fc63ced`, passes here). Not compared against a real Windows
  corpus.
- Overview counts present clients minus tab-hidden ones, so a user who hid the
  Cursor tab now sees Grok Bot usage in Overview, and a user who hid the Grok
  tab no longer does (the hide set folds both members of the group).
- Usage Attribution keys rows by (client, provider), so the moved rows become
  `(grok-bot, xai)`; `UsageAttributionSettings.SubscriptionProviderMap` has a
  `grok-bot` entry for this. A saved `cursor|xai` assignment stops covering
  them.

## Historical: `8fc63ced`, Cursor usage-events JSON (engine PRs #65, #67)

The pin was the merge commit of tokscale-core PR #67 on the engine's
`main`, covering `726efd70` → `8fc63ced`. The gitlink moved to `a024eb7` (#65)
earlier without an update to this file; this entry covers both merges. The
diff touches `src/scanner.rs`, `src/sessions/cursor.rs`, `src/source_context.rs`,
`src/clients.rs`, `src/message_cache.rs`, `src/lib.rs` (tests) and `UPSTREAM.md`.

- #65 (`a024eb7`): the source-context scan now honours `excluded_scan_paths`.
  `retain_unexcluded_scan_tasks` runs in `scan_all_clients_resolved_inner`
  (scanner.rs), which every `*_with_source_context` report, parse and change
  token goes through; before, only the context-free scan applied it. A relative
  exclusion is bound to the capture cwd, and a remote context requires absolute
  ones. `window_usage.rs` already relies on this for the primary account window.
- #67 (`8fc63ced`): Cursor's scan pattern becomes `usage*.json|usage*.csv` and
  a usage-events JSON parser (`parse_cursor_events_json`) reads the
  `usage.json` / `usage.<account>.json` the current tokscale CLI writes, which
  this tree did not read before, so a CLI-current user saw no Cursor usage.
  When a JSON and a CSV sit in the same directory with the same stem the JSON
  wins and the CSV is not counted. Cost is `tokenUsage.totalCents`, else
  `chargedCents`, divided by 100 and provider-reported.

Measured on this advance, not relayed:

- `CACHE_FORMAT_VERSION` stays 4 and Cursor's `parser_version` stays 3 (#67
  adds a comment saying why: the source cache is keyed per path, so a new
  `usage*.json` has no entry to be stale). No namespace re-parses. `ClientId`
  is unchanged. `RESOLVER_CONTRACT_VERSION` stays 3.
- `pub` items: `sessions::cursor::parse_cursor_events_json` is added
  (`parse_cursor_file` keeps its signature and now dispatches on the extension);
  this crate uses neither. `crates/tb_core_ffi` needed no production change.
- Source-context identity changes once. The scan pattern is part of the
  descriptor, so the new Cursor pattern moves every identity (the engine's
  `descriptor_has_fixed_sha256_and_native_path_vectors` golden moved on macOS
  and Linux). #65 adds field 16 only when a context carries a non-empty
  exclusion, so it moves nothing by itself. Here, `tb_source_context_id` is
  persisted: `GraphSnapshotStore` writes it into each snapshot envelope and a
  differing id reads as `ContextMismatch` (a miss), so the first launch after
  this advance rescans instead of serving the old graph snapshot. Nothing
  else in this repo stores one.
- Effect on figures: Cursor usage that exists only as `usage.json` is now
  counted, in tokens and cost. CSV-only installs are unchanged, covered by a
  control in `model_report::tests::cursor_usage_json_and_csv_reach_the_report`.
  Not compared against a real Windows corpus.

## Historical: `726efd70`, lazy cache namespaces (engine PRs #63, #64)

The pin was the merge commit of tokscale-core PR #64 on the engine's
`main`, two merges after `6712ed8a`. The diff touches `src/lib.rs`,
`src/message_cache.rs` and `UPSTREAM.md` only.

- #63 makes the engine's streaming scan load each source-cache namespace on
  its lane's first lookup and release that namespace's clean entries once the lane is done (Claude is never released).
  Report output is byte-identical to `6712ed8a` on the engine side (#63).
  #64 is documentation only.
- Measured on this advance, not relayed: `CACHE_FORMAT_VERSION` stays 4, no
  `parser_version` moves, `ClientId` is unchanged (the
  `engine_client_ids_match_the_fixture` guard passes without a fixture change),
  and no `pub` item changes (the new cache functions are `pub(crate)`).
  `crates/tb_core_ffi` needed no change.
- Memory, as measured in #63 on macOS (warm scan, maintainer corpus snapshot,
  frozen pricing, median of 3; warm peak RSS before → after): all clients
  329.0 → 301.9 MiB (−8.2%), cursor only 153.2 → 17.8 MiB (−88%), claude only
  218.1 → 148.6 MiB (−32%), codex only 219.9 → 155.4 MiB (−29%). Cold scans
  are unchanged. All clients drops only 8% because the Claude namespace stays
  resident while the Codex lane runs; reordering the lanes is recorded in
  `UPSTREAM.md` as the next candidate. These figures were not re-measured on
  Windows.
- The slice's bar of an all-clients drop of about 10% was waived by the user on
  2026-10-03, citing the single-client gains: `tb_hourly_report` takes a client
  filter, so a filtered call gets the per-client drops above.
- No Windows real-machine numeric comparison was run for this advance: the
  change is memory-only and the engine side showed byte-identical output.

## Historical: `6712ed8a`, the 2026-10-02 upstream sync (engine PRs #41–#62)

The pin was the merge commit of tokscale-core PR #62 on the engine's
`main`, 19 merges after `bb9a2a9`.

Measured on this advance, not relayed:

- `CACHE_FORMAT_VERSION` stays 4. `RESOLVER_CONTRACT_VERSION` moves 1→3: 2
  for the Kimi Work scan roots (#56), 3 for removing `~/.omp/agent/sessions`
  from `pi` (#62). Every source-context identity changes once; nothing on this
  side persists one. The `pub` items added are the new client modules
  (`augment`, `hindsight`, `kimchi`, `muse`, `omp`, `reasonix`, `senpi`,
  `zcode`), `pricing::aliases::uses_cursor_pricing` and
  `ScanResult::zcode_db`; this crate uses none of them. `ClientId` grows from
  33 to 41.
- Among existing clients the parser identities that move are Kiro (1→2, #51)
  and Pi (2→3, #62), so those namespaces re-parse once. Pi's parse itself is
  unchanged; the bump drops shards cached for OMP files under `pi`. New clients
  start at 1.
- `crates/tb_core_ffi` needed no production change: its exhaustive
  `TokenBreakdown` literal already fills `cache_write_1h` with
  `entry.cache_write` (the upper-bound rule above). Two tests were added:
  `model_grouping_cases_match_the_engine` checks the shared model-grouping
  case table against the engine, and `engine_client_ids_match_the_fixture`
  keeps `Fixtures/engine-client-ids.json` equal to `ClientId::ALL`, so a later
  advance that adds a client fails until the list, and then the C# registry
  (`ClientRegistryTests.EveryEngineClientIsRegistered`), catch up.

What reaches the figures:

- New clients: ZCode v2 CLI usage database (#49), Augment Code (#50),
  Hindsight (#54), Muse Code (#57), Reasonix (#58), and three Pi-format
  clients on the shared Pi parser: Kimchi Coding (#60), Senpi (#61) and
  Oh My Pi (#62).
- Oh My Pi usage moves from `pi` to `omp` (#62): the `pi` client no longer
  scans `~/.omp/agent/sessions`. Totals are unchanged when the two trees share
  no records. A record present under both `~/.pi` and `~/.omp` (a session
  copied during a move, say) used to count once in `pi` and now counts once in
  each client, as upstream: there is no cross-client dedup. The same holds for
  a session under both the Pi root and the Kimchi or Senpi root. Kimi Desktop ("Kimi Work")
  sessions are scanned as Kimi on Windows and macOS (#56; `%APPDATA%` or the
  relocated `shareDir` when environment roots are on).
- Grok Build turn usage keyed `grok-<version>-build` groups with the session's
  `grok-<version>` in the engine's reports (#46), and since #88 the listed
  Gemini label variants group onto their bare model; this consumer applies the
  same display fold to graph-derived rows (`ModelGrouping`).
- Pi transcripts that start with a UTF-8 BOM are no longer dropped whole (#47).
- Kiro conversations that report credits carry them as a provider-reported cost
  (credits × $0.04) on the turn that spent them; IDE and SQLite turns take their
  request counts from the source. Token counts are unchanged (#51).
- Pricing (#48, #52, #53, #55, #59): Kimi Work ids priced and
  `kimi-for-coding` retargeted, Daybreak Blue and Cursor-tier aliases; GPT-5.6 /
  GPT-6 billed above 272K request-wide, including on hinted rows; OpenRouter
  models priced at the author's standard service tier; Composer 2 cache creation
  free.

## Historical: `bb9a2a9`, engine PRs #42, #30, #43

The reviewed pin is the merge commit of tokscale-core PR #43 on the engine's
`main`, 13 commits after `be0861d`:

- #42 counts Pi fork copies once, keyed across sessions by provider and
  response id.
- #30 lets OpenRouter entries take part in pricing lookup across
  version-separator spellings (`claude-fable-5-1` vs `claude-fable-5.1`).
- #43 attributes Droid usage per reply.

Measured on this advance:

- `CACHE_FORMAT_VERSION` stays 4 and no `pub` item changes. The parser
  identities move for Pi (1→2) and Droid (1→2), so those namespaces re-parse
  once. `crates/tb_core_ffi` needed no change.
- Windows numeric comparison on a real corpus, two rounds, graph path only,
  frozen cache-only pricing: old pin cold, old pin cold again, new pin warm on
  the old cache, and new pin cold. The two old runs match each other and the
  two new runs match each other. Tokens are identical on every date × client
  × provider × model row. No Pi or Droid data was present, so those two
  changes rest on the engine's tests.

What #30 does to costs, by mechanism:

- **Historical cost falls where the old lookup chose a GovCloud price.** When
  the dotted OpenRouter spelling could not match, the provider-scoped LiteLLM
  stage picked a Bedrock GovCloud entry (`bedrock/us-gov-*/anthropic.*`, or
  `us-gov.anthropic.*`), whose rates are 1.2× first-party. The first-party
  OpenRouter entry now wins. On the measured corpus `claude-fable-5-1` and
  `claude-opus-5-5` fell by exactly 1/1.2 (−16.7%). The 1-hour cache-write
  premium is unaffected.
- **Cost rises where a cache-write rate was missing.** Rows whose chosen entry
  carried no cache-write rate priced cache writes at $0. They are now priced:
  `claude-opus-4-8` and an OpenCode `claude-haiku-4-5` row rose by the value
  of their cache writes.

## Historical: `be0861d`, engine PRs #28–#40

The reviewed pin is the merge commit of tokscale-core PR #40 on the engine's
`main`: 49 commits after `d6512f5`, carrying engine PRs #28, #33, #35–#40
(#29, #31 and #32 are docs and review configuration only). It deliberately
passes `c96aef21` (#33), whose Antigravity CLI log index #36 removed.

Measured on this advance, not relayed:

- `CACHE_FORMAT_VERSION` stays 4 (`src/message_cache.rs:39`). No `pub` item is
  added or removed across the range. `src/lib.rs` changes only in tests, and
  `crates/tb_core_ffi` needed no change.
- Per-client parser identities move, so each of these namespaces re-parses
  once on the first scan: Copilot 4→5, Grok 3→4, OpenClaw →3, Cursor →3,
  OpenCode →2, RooCode / KiloCode / Cline →2. Claude and Codex parsers are
  untouched; no file under their parsers changes.

What reaches the figures:

- OpenCode 2.x `session_v2` usage is read (#35).
- Antigravity CLI turns are dated from the `steps` table via upstream #1327's
  responseId → gen_idx join (#33 + #36); no log files enter the change token.
- The scanner no longer nests `par_bridge` and caps at four workers (#37).
- OpenClaw `.jsonl.zst` archives decode and checkpoints are no longer double
  counted (#38).
- Grok and Copilot Desktop `events.jsonl` skip a bad line instead of stopping
  (#39).
- Cursor `Input (w/ Cache Write)` is its own bucket, and any numeric Cost,
  $0.00 included, counts as provider-reported; Cline 4.x prices under its
  real model (#40).

The Windows numeric comparison for this advance (old pin cold, new pin warm
on the old cache, new pin cold; per date × client) is recorded in the pull
request that made it.

## Historical: `d6512f5`, the window-usage reconciliation

The reviewed pin is the immutable GitHub merge commit for tokscale-core
PR #27, which reconciled two divergent implementations of `get_window_usage`
and landed `get_window_usage_with_source_context` — the entry point
`crates/tb_core_ffi` calls and the reason this pin could not advance before.
The previous gitlink, `4dd3533`, sat on the branch `feat/window-usage-export`,
one commit past merge-base `6a9de8c` and 60 behind the engine's `main`.

**This advance is not cache-neutral for this consumer.** `CACHE_FORMAT_VERSION`
moves 3 to 4 and per-client parser identities move with it (Codex 4 to 6,
Claude 2 to 4), so existing installs take a migration on first launch. The
engine's `mod format3` migrates format-3 shards rather than discarding them,
which matters beyond cold-start cost: for Claude the cache is the only copy of
turns a compacting rewrite removed from the transcript.

Displayed figures change, because the stranded branch was producing different
ones. Measured over a fixed five-hour window on a real corpus, the old pin
returned 42 rows / `input=206039` / `cost=9.684641` where this pin returns
26 / `189822` / `12.186039`; the token lanes other than input are identical.
The arriving fixes include Codex reasoning tokens no longer priced twice,
Claude `tool_result` input no longer char-estimated, Claude 1-hour cache
writes priced at their own rate, and deterministic ordering for
equal-length pricing fallbacks.

Two behaviour changes arrive with the reconciled export: an empty or inverted
window returns an empty row list rather than an error, and the window scan's
client filter is aligned to the aggregate paths (inert here — this consumer
passes `clients: None`).

## Historical: v1.13 source-context and retained-token alignment

Superseded by the section above; retained because it records what an earlier
pin established, not what is built today. At that pin — the merge commit for
tokscale-core PR #12, following the source-context foundation in PR #10 — the
Windows FFI captured the engine-owned context once per process and routed graph, report, parse,
source-token, and live-tail work through the context-aware APIs. The engine
source token includes report-visible retained-only Claude cache state, and that
slice kept the cache schema and public interfaces unchanged. That statement
is about that advance only. The format moved 3 to 4 later, at `d6512f5`, and
stayed 4 for every namespace until `fc339e98`, where Claude shards are written
at format 100 (`CLAUDE_CACHE_FORMAT_VERSION`); the generic
`CACHE_FORMAT_VERSION` is still 4.

## Ownership

| Owner | Surface |
|---|---|
| `tokscale-core` | Shared Rust source, tests, standalone lock and CI, upstream baseline, and local-patch ledger |
| TokenBar for Windows | Gitlink, root `Cargo.lock`, `crates/tb_core_ffi`, C header, C# bridge, application, and packaging wiring |

## Checkout

Clone recursively:

```bash
git clone --recurse-submodules https://github.com/Nanako0129/TokenBar-Windows.git
```

Initialize an existing checkout before building:

```bash
git submodule update --init --recursive
```

The submodule must be clean, and its checked-out `HEAD` must equal the
superproject gitlink.

## Historical sync boundary

The final manual copy came from Native commit
[`729dc3adf21cc31e16ef0b8b742f0244197d7058`](https://github.com/Nanako0129/TokenBar/commit/729dc3adf21cc31e16ef0b8b742f0244197d7058)
and reached Windows baseline `68e2541c5e9adb14a47433f8b25e26b0be84d1fc`.
At that checkpoint, all 63 shared files were byte-identical after excluding
the former Windows-only `SYNC.md`; Windows carried no shared-tree local patch.

The public engine preserves that source history and the complete authoritative
ledger. This document replaces the former `vendor/tokscale-core/SYNC.md` and
the duplicated vendor ledger; the manual-copy procedure is retired.
