#!/usr/bin/env bash
# Compare tb_graph and the primary tb_window_usage between two tb_core_ffi
# builds over the same fixture home, with the volatile fields masked
# (meta.generatedAt, processingTimeMs). Used to show that a change which adds
# account-scoped window usage and extra scan roots leaves the output unchanged
# while no root is registered (W4b acceptance 1').
#
# macOS host only: there the `dirs` crate derives the platform config/data
# roots from $HOME, so each env-cleared run reads only the fixture. On Windows
# those roots are Known Folders that an env change does not move, so a run
# would also read the machine's real %APPDATA% data.
#
# Usage: scripts/compare-window-graph.sh <old-libtb_core_ffi.dylib> <new-libtb_core_ffi.dylib>
#   The old build's tb_window_usage takes (from_ms, until_ms); the new one
#   takes (account_key, from_ms, until_ms) and is called with account_key NULL.
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS only (see header)" >&2
  exit 2
fi
old_lib="$1"
new_lib="$2"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fixture="$work/home"
mkdir -p "$fixture/.claude/projects/proj" "$fixture/tmp"
for i in 1 2 3; do
  printf '{"type":"assistant","timestamp":"2026-01-01T0%s:00:00.000Z","requestId":"req_%s","message":{"id":"msg_%s","model":"claude-3-5-sonnet","usage":{"input_tokens":%s,"output_tokens":%s}}}\n' \
    "$i" "$i" "$i" "$((i * 100))" "$((i * 1000))" >> "$fixture/.claude/projects/proj/s.jsonl"
done

run() {
  local lib="$1" arity="$2" out="$3"
  env -i PATH=/usr/bin:/bin HOME="$fixture" TMPDIR="$fixture/tmp" \
    TOKSCALE_CONFIG_DIR="$work/tokscale-config-$arity" TOKSCALE_PRICING_CACHE_ONLY=1 \
    /usr/bin/python3 - "$lib" "$arity" > "$out" <<'PY'
import ctypes, json, sys
lib = ctypes.CDLL(sys.argv[1])
arity = sys.argv[2]
for name in ("tb_graph", "tb_window_usage"):
    getattr(lib, name).restype = ctypes.c_void_p
lib.tb_free.argtypes = [ctypes.c_void_p]
def take(pointer):
    text = ctypes.cast(pointer, ctypes.c_char_p).value.decode()
    lib.tb_free(pointer)
    return json.loads(text)
def mask(value):
    if isinstance(value, dict):
        return {k: ("<masked>" if k in ("generatedAt", "processingTimeMs") else mask(v)) for k, v in value.items()}
    if isinstance(value, list):
        return [mask(v) for v in value]
    return value
lib.tb_graph.argtypes = [ctypes.c_char_p]
graph = take(lib.tb_graph(None))
FROM, UNTIL = 1767225600000, 1767312000000
if arity == "old":
    lib.tb_window_usage.argtypes = [ctypes.c_int64, ctypes.c_int64]
    window = take(lib.tb_window_usage(FROM, UNTIL))
else:
    lib.tb_window_usage.argtypes = [ctypes.c_char_p, ctypes.c_int64, ctypes.c_int64]
    window = take(lib.tb_window_usage(None, FROM, UNTIL))
print(json.dumps({"graph": mask(graph), "window": mask(window)}, indent=1, sort_keys=True))
PY
}

run "$old_lib" old "$work/old.json"
run "$new_lib" new "$work/new.json"

# Refuse a vacuous match: both runs must have read the fixture's three turns.
check() {
  /usr/bin/python3 - "$1" "$2" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
graph, window = d["graph"], d["window"]
assert graph.get("ok") is True, f"{sys.argv[2]}: graph failed: {graph}"
assert window.get("ok") is True, f"{sys.argv[2]}: window failed: {window}"
claude = [m for m in window["data"]["messages"] if m["client"] == "claude"]
assert len(claude) == 3, f"{sys.argv[2]}: expected the fixture's 3 Claude turns, got {len(claude)}"
assert sum(m["output"] for m in claude) == 6000, f"{sys.argv[2]}: fixture output tokens"
print(f"{sys.argv[2]}: graph ok, window ok, 3 Claude turns, 6000 output tokens")
PY
}
check "$work/old.json" old
check "$work/new.json" new
if diff -u "$work/old.json" "$work/new.json"; then
  echo "IDENTICAL (generatedAt and processingTimeMs masked)"
else
  echo "DIFFERENT" >&2
  exit 1
fi
