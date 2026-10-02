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
| Reviewed pin | `6712ed8a0ff67bf1b2d0a97c41d2d94821b507fa` |
| TokenBar alignment | macOS `main` pins `319ffa8` (engine PR #46), an ancestor of this pin; the macOS advance to the same engine head is in progress |
| Engine alignment | `bb9a2a9ac787344bb4bd3120d645208217b21016` → `6712ed8a0ff67bf1b2d0a97c41d2d94821b507fa` (engine `main`) |
| Native consumer baseline | `704426e8df9acfb8e82fe4bf3b7ed3e5adbc2fea` |
| Windows pre-migration baseline | `68e2541c5e9adb14a47433f8b25e26b0be84d1fc` |
| Upstream and local-patch ledger | Immutable [`UPSTREAM.md`](https://github.com/Nanako0129/tokscale-core/blob/6712ed8a0ff67bf1b2d0a97c41d2d94821b507fa/UPSTREAM.md) |

> **Warning:** Do not edit shared source on a consumer branch. Engine changes
> must pass review in `tokscale-core`; this repository then advances only the
> reviewed gitlink and runs the Windows consumer gates.

## Current pin: `6712ed8a`, the 2026-10-02 upstream sync (engine PRs #41–#62)

The reviewed pin is the merge commit of tokscale-core PR #62 on the engine's
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
