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
| Reviewed pin | `fcb85923e5544488f98001cfa390740d0009b9da` |
| TokenBar alignment | macOS `main` still pins `8fc63ced` (behind; its pin advance, E3 of the Grok Bot attribution plan, is pending) |
| Engine alignment | `8fc63cedfaf4aeec73c9a4e65711c280e7add15e` → `fcb85923e5544488f98001cfa390740d0009b9da` (engine `main`) |
| Native consumer baseline | `704426e8df9acfb8e82fe4bf3b7ed3e5adbc2fea` |
| Windows pre-migration baseline | `68e2541c5e9adb14a47433f8b25e26b0be84d1fc` |
| Upstream and local-patch ledger | Immutable [`UPSTREAM.md`](https://github.com/Nanako0129/tokscale-core/blob/fcb85923e5544488f98001cfa390740d0009b9da/UPSTREAM.md) |

> **Warning:** Do not edit shared source on a consumer branch. Engine changes
> must pass review in `tokscale-core`; this repository then advances only the
> reviewed gitlink and runs the Windows consumer gates.

## Current pin: `fcb85923`, Grok Bot attribution (engine PRs #68, #69)

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
  `grok-<version>` in the engine's reports (#46); this consumer applies the same
  display fold to graph-derived rows (`ModelGrouping`).
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
has stayed 4 through the current pin.

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
