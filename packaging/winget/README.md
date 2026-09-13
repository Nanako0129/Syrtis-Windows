# winget manifests

Manifests for the Windows Package Manager community repository. Nothing here is
submitted yet, and **the package is not installable through winget today**.

## Layout

Two packages, because Full and Lite are not interchangeable:

| Package | Channel | Installers |
| --- | --- | --- |
| `Nyanako.Syrtis` | Full — bundles .NET 10 | `win-x64`, `win-arm64` |
| `Nyanako.Syrtis.Lite` | Lite — acquires .NET 10 at install time | `win-x64-lite`, `win-arm64-lite` |

They are separate identifiers rather than four installers under one package
because Velopack refuses to serve a Full update to a Lite install and the
reverse, comparing the channel with `StringComparison.Ordinal`. One package
offering both would let `winget upgrade` move a machine across that line, after
which the application silently stops updating. See
[`docs/lite-distribution.md`](../../docs/lite-distribution.md).

## What is verified

Run on a real Windows host with winget `v1.29.290`, against the published
v0.3.0 release:

- All six files parse, and the four `InstallerSha256` values equal the
  corresponding lines in the release's `SHA256SUMS.txt`.
- `winget validate --manifest` reports **exactly one** error per package:
  `Missing required property 'License'`. Nothing else — the installer and
  version manifests pass the real schema.
- `Setup.exe --silent` was measured returning exit 0 on a `win-x64` host
  upgrading 0.2.1 and a `win-arm64` host upgrading 0.2.2, each then reaching
  the application's own tray-ready gate. That is the evidence behind
  `InstallerSwitches.Silent` and `UpgradeBehavior: install`.
- The install is per-user: v0.3.0's uninstall entry is under `HKCU`, with
  `DisplayName = Syrtis`, `Publisher = Nyanako.Syrtis`, key `Nyanako.Syrtis`.
  `Scope: user` and `AppsAndFeaturesEntries` mirror that.

## What blocks submission

**1. Licensing.** `License` is a required field in the defaultLocale manifests
and is deliberately absent, so validation fails loudly rather than passing with
a value nobody chose. Two facts have to be settled first:

- This repository has no `LICENSE` file, so its own source is under default
  copyright.
- The shipped package is not only its own code. `lib/app/tb_core_ffi.dll` in
  the nupkg is built from `vendor/tokscale-core`, which is MIT
  (`Copyright (c) 2025 Junho Yeo`; `Cargo.toml` declares `license = "MIT"`,
  and upstream `junhoyeo/tokscale` is MIT). MIT requires the copyright and
  permission notice to be included in all copies or substantial portions.
  **No notice file ships inside the package today** — checked against the
  published `Nyanako.Syrtis-0.3.0-win-x64-full.nupkg`.

Submitting to `microsoft/winget-pkgs` asserts a right to distribute, so this is
a prerequisite rather than a follow-up. The fix is a `LICENSE` for this
repository's own code plus a third-party notice file that packaging copies into
the publish root, so the notice travels with the binary rather than only
living in the repository.

**2. The submission itself** is a pull request to `microsoft/winget-pkgs` from
the maintainer's own account, under their CLA. It is not something CI does.

## Regenerating for a new version

Nothing generates these yet. For a new tag the values that change are
`PackageVersion` (three files per package), `ReleaseDate`, the four
`InstallerUrl` tags, and the four `InstallerSha256` values, which come from the
release's `SHA256SUMS.txt`. Note that `AppsAndFeaturesEntries.Publisher` must
keep describing what that version actually wrote to the registry: v0.3.0 wrote
`Nyanako.Syrtis` because `vpk pack` had no `--packAuthors`, and 0.3.1 onward
writes `Nyanako`.
