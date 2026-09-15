#!/usr/bin/env bash
# Regenerate bucket/syrtis.json from a published release.
#
# A self-hosted Scoop bucket does not update itself. `checkver` and `autoupdate`
# in the manifest are consumed by `scoop checkver <app> -u`, which is maintainer
# tooling; a client running `scoop update syrtis` re-reads the bucket and finds
# whatever version the bucket says. So the manifest has to be rewritten and
# committed after each release, or Scoop users stay pinned to the last version
# that was committed.
#
# This cannot run before the release exists: the hashes are of the published
# .nupkg files, which only exist once the packaging jobs have run. That is why
# it is a post-release step rather than part of the version-contract gate that
# release.yml already applies to Directory.Build.props.
#
# Usage:
#   scripts/update-scoop-manifest.sh [TAG]        # defaults to the latest release
#   scripts/update-scoop-manifest.sh --check TAG  # exit 1 if the file would change
#
# Requires: gh (authenticated), python3.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$repo_root/bucket/syrtis.json"

check_only=false
if [ "${1:-}" = "--check" ]; then
    check_only=true
    shift
fi

tag="${1:-}"
if [ -z "$tag" ]; then
    tag="$(gh release view --json tagName --jq .tagName)"
fi
version="${tag#v}"

sums="$(gh release download "$tag" --pattern SHA256SUMS.txt --output - 2>/dev/null)"
if [ -z "$sums" ]; then
    echo "no SHA256SUMS.txt on release $tag" >&2
    exit 1
fi

# Scoop ships the Full channel only. Lite is framework-dependent and relies on
# Velopack's installer to acquire .NET, which a Scoop extraction never runs.
hash_for() {
    local name="$1" h
    h="$(printf '%s\n' "$sums" | awk -v n="$name" '$2 == n { print $1 }')"
    if [ -z "$h" ]; then
        echo "no checksum for $name in release $tag" >&2
        exit 1
    fi
    printf '%s' "$h"
}

x64_name="Nyanako.Syrtis-${version}-win-x64-full.nupkg"
arm64_name="Nyanako.Syrtis-${version}-win-arm64-full.nupkg"

python3 - "$manifest" "$version" "$tag" \
    "$x64_name" "$(hash_for "$x64_name")" \
    "$arm64_name" "$(hash_for "$arm64_name")" \
    "$check_only" <<'PY'
import json, sys

path, version, tag, x64_name, x64_hash, arm_name, arm_hash, check_only = sys.argv[1:9]
base = f"https://github.com/Nanako0129/Syrtis-Windows/releases/download/{tag}"

with open(path, encoding="utf-8") as f:
    original = f.read()
manifest = json.loads(original)

manifest["version"] = version
manifest["architecture"]["64bit"]["url"] = f"{base}/{x64_name}#/syrtis.zip"
manifest["architecture"]["64bit"]["hash"] = x64_hash
manifest["architecture"]["arm64"]["url"] = f"{base}/{arm_name}#/syrtis.zip"
manifest["architecture"]["arm64"]["hash"] = arm_hash

updated = json.dumps(manifest, indent=4, ensure_ascii=False) + "\n"

if check_only == "true":
    if updated != original:
        print(f"bucket/syrtis.json is stale for {tag}", file=sys.stderr)
        sys.exit(1)
    print(f"bucket/syrtis.json matches {tag}")
else:
    with open(path, "w", encoding="utf-8") as f:
        f.write(updated)
    print(f"bucket/syrtis.json updated to {version}")
PY
