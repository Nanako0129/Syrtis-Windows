# Lite deployment channel — distribution policy

Framework-dependent **Lite** packages omit the bundled .NET 10 runtime. Full remains self-contained.

## One default per surface

| Surface | Default | Also available |
| --- | --- | --- |
| README / primary download button | Full | none |
| GitHub Releases asset list | Full listed first | Lite |
| winget | `Nyanako.Syrtis` = Full | `Nyanako.Syrtis.Lite` |
| Scoop | **Full only** | — |

## Guidance

- **Settled: Scoop ships Full, and the earlier Lite-default intent was wrong.** Scoop consumes the published `-full.nupkg` directly, extracting `lib\app` into `~/scoop/apps/syrtis/<version>` under its own `current` junction. No new artifact and no Velopack installer is involved — and that last part is what rules Lite out. Lite is framework-dependent and relies on **Velopack's installer** to acquire .NET 10; a Scoop install never runs that installer, so a Lite payload would simply fail to start on a machine without the runtime, with nothing to bootstrap it. Full is self-contained and works unconditionally.
- The reasoning that produced the Lite intent — Scoop users are likelier to already have .NET 10 — is still true and still irrelevant, because "likelier" is not "guaranteed" and there is no bootstrap to fall back on.
- Verified on the x64 test host: `scoop install` from the manifest produced 555 files / 191 MB with `Syrtis.App.exe` reporting 0.3.0 and `tb_core_ffi.dll` present, and `scoop uninstall` removed the app directory and Start Menu shortcut while leaving both the Velopack install and `%APPDATA%\tokscale` untouched.
- On a clean machine, Lite download plus runtime bootstrap may approach the Full total download size.
- Do **not** advertise Lite as an unconditional ~50% saving for every user.
- The .NET runtime bootstrap remains **Velopack's** responsibility.
- Do **not** assume winget `PackageDependencies` installs or enforces the runtime.
- All Full and Lite assets remain reachable even though each surface has one default.

## Channels

| Channel | Mode | Architecture |
| --- | --- | --- |
| `win-x64` | Full | x64 |
| `win-x64-lite` | Lite | x64 |
| `win-arm64` | Full | arm64 |
| `win-arm64-lite` | Lite | arm64 |

Full cannot consume Lite updates and Lite cannot consume Full updates. Near-miss channel names fail closed before network access.

## Package identity

Durable Velopack pack id is the frozen literal `Nyanako.Syrtis` (not derived from `TbProductName`). UpdateFlow.PackageId must match the packaging-script literal.
