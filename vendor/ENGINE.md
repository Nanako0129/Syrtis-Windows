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
| Reviewed pin | `be0861d4ec5331a97410b5eb747ffc92db002d2f` |
| TokenBar alignment | `v1.17.0` (engine `8a88602b`) → ahead of it; macOS `main` pins `3eec5846`, both ancestors of this pin |
| Engine alignment | `d6512f5ae62c2be6751ed93adb9391ffe3f91579` → `be0861d4ec5331a97410b5eb747ffc92db002d2f` (engine `main`, the same advance macOS TokenBar took in its PR #386) |
| Native consumer baseline | `704426e8df9acfb8e82fe4bf3b7ed3e5adbc2fea` |
| Windows pre-migration baseline | `68e2541c5e9adb14a47433f8b25e26b0be84d1fc` |
| Upstream and local-patch ledger | Immutable [`UPSTREAM.md`](https://github.com/Nanako0129/tokscale-core/blob/be0861d4ec5331a97410b5eb747ffc92db002d2f/UPSTREAM.md) |

> **Warning:** Do not edit shared source on a consumer branch. Engine changes
> must pass review in `tokscale-core`; this repository then advances only the
> reviewed gitlink and runs the Windows consumer gates.

## Current pin: `be0861d`, engine PRs #28–#40

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
slice kept the cache schema and public interfaces unchanged — a statement about
that advance, not about the current one, which does change the cache format.

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
