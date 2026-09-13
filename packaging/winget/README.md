# winget manifests

Manifests for the Windows Package Manager community repository. Nothing here is
submitted yet, and **the package is not installable through winget today**. The
manifests themselves are complete and validate against the real tool.

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
- `winget validate --manifest` succeeds for both packages.
- `Setup.exe --silent` was measured returning exit 0 on a `win-x64` host
  upgrading 0.2.1 and a `win-arm64` host upgrading 0.2.2, each then reaching
  the application's own tray-ready gate. That is the evidence behind
  `InstallerSwitches.Silent` and `UpgradeBehavior: install`.
- The install is per-user: v0.3.0's uninstall entry is under `HKCU`, with
  `DisplayName = Syrtis`, `Publisher = Nyanako.Syrtis`, key `Nyanako.Syrtis`.
  `Scope: user` and `AppsAndFeaturesEntries` mirror that.
- winget resolves the installed package from that entry unaided:
  `winget list --id Nyanako.Syrtis` returns
  `Syrtis  ARP\User\X64\Nyanako.Syrtis  0.3.0`. That correlation is what most
  commonly fails for a custom installer.

## What blocks submission

**The submission itself.** A pull request to `microsoft/winget-pkgs` from the
maintainer's own account, under their CLA. It is not something CI does, and it
is the only step left.

Licensing, which previously blocked this, is settled: the repository is MIT and
`THIRD-PARTY-NOTICES.md` ships inside the payload, so submission no longer
asserts a distribution right the repository had not granted.

## What is still unproven

Installing **from the local manifest** was not exercised.
`winget install --manifest` requires `winget settings --enable
LocalManifestFiles`, which needs an elevated prompt. The installer's own silent
behaviour is separately measured, so what remains unproven is winget's
orchestration of it rather than the installer. The winget-pkgs pipeline runs
that install in a sandbox during review.

To close it locally, on a Windows host, once:

```powershell
winget settings --enable LocalManifestFiles   # elevated
winget install --manifest .\packaging\winget\Nyanako.Syrtis\0.3.0
```

## Regenerating for a new version

Nothing generates these yet. For a new tag the values that change are
`PackageVersion` (three files per package), `ReleaseDate`, the four
`InstallerUrl` tags, and the four `InstallerSha256` values, which come from the
release's `SHA256SUMS.txt`. Note that `AppsAndFeaturesEntries.Publisher` must
keep describing what that version actually wrote to the registry: v0.3.0 wrote
`Nyanako.Syrtis` because `vpk pack` had no `--packAuthors`, and 0.3.1 onward
writes `Nyanako`.
