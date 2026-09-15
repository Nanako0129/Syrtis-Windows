# Syrtis

Windows port of [TokenBar](https://github.com/Nanako0129/TokenBar) — the
menu-bar/tray AI coding-agent token-usage monitor. Same Rust parsing core,
a native WinUI 3 shell.

Syrtis is the name this port ships under; the macOS build is still called
TokenBar. Solution and namespace identifiers remain `TokenBar.*` — they are
internal, and the Velopack package id `Nyanako.Syrtis` is deliberately
independent of both so a rename cannot move an installed app.

> **Status: [`v0.3.0`](https://github.com/Nanako0129/Syrtis-Windows/releases/tag/v0.3.0) is the
> latest release.** See the [release list](https://github.com/Nanako0129/Syrtis-Windows/releases)
> for history, [`docs/release-velopack.md`](docs/release-velopack.md) for the current packaging
> contract, and [`docs/release.md`](docs/release.md) for the earlier portable one.

## Features

- **Eight lenses** in the flyout: Overview, Quota, Models, Monthly, Daily, Hourly, Stats, Agents —
  each hideable except Overview and Models, with Ctrl+1..8 accelerators.
- **Quota lens** — overview strip, heatmap, per-client window and history cards, and an
  Agent-limits view, backed by a usage-attribution subsystem that resolves which client
  consumed which window.
- **Tray icon**, seven display modes (today/total tokens, today/total cost, tokens-per-minute,
  quota-remaining, hidden), drawn directly into the icon with bars/ring/popsicle gauge styles.
- **3D contribution graph** — a real-time D3D11-rendered activity grid with orbit/pan/zoom.
- **In-app updates** — a Sparkle-style update dialog backed by Velopack.
- **Traditional Chinese** UI, alongside English.

## Install

Download the installer for your architecture from the
[releases page](https://github.com/Nanako0129/Syrtis-Windows/releases). Four channels, permanent
and non-interchangeable once installed:

| Channel | .NET runtime |
|---|---|
| `win-x64` | Bundled (Full) |
| `win-x64-lite` | Acquired at install time |
| `win-arm64` | Bundled (Full) |
| `win-arm64-lite` | Acquired at install time |

Full is the safe default; Lite is smaller to download but only wins on a machine that already has
.NET 10 installed. See [`docs/lite-distribution.md`](docs/lite-distribution.md) for the full
channel/package-identity contract.

### Package managers

**Scoop**, from this repository's own bucket:

```powershell
scoop bucket add syrtis https://github.com/Nanako0129/Syrtis-Windows
scoop install syrtis
```

Scoop manages the app itself, so `scoop update syrtis` is how it updates, and the bucket is
refreshed after each release by [`scripts/update-scoop-manifest.sh`](scripts/update-scoop-manifest.sh).
If the in-app **Check for updates** button reports a failure, that is expected here and nothing is
broken: it talks to the installer's own update feed, which does not manage this copy.

Only the Full channel is published this way — Lite relies on the installer to fetch .NET 10, and a
Scoop install never runs that installer. Your usage data lives outside the Scoop directory, so it
survives updates and is left behind on uninstall.

**winget** is not published yet. Manifests are prepared in
[`packaging/winget/`](packaging/winget/) and validate against the real tool; what remains is the
submission pull request to `microsoft/winget-pkgs`.

## Supported providers

The app reads quota for five agent CLIs, using credentials already written to disk by a login
you have performed. That is usually the provider's own CLI — with one exception worth knowing
before you go looking for a missing card:

| Provider | Mechanism |
|---|---|
| Claude | OAuth via the OS keychain / Claude Code login credentials |
| Codex | OAuth via `~/.codex/auth.json` (`$CODEX_HOME` honored) |
| GitHub Copilot | OAuth via **opencode's** `auth.json` — signing into the GitHub Copilot CLI alone is not enough, and the card is omitted rather than shown as an error |
| Grok | OAuth via `~/.grok/auth.json` |
| Antigravity | OAuth or local IDE credentials, whichever the installed client itself uses |

## Architecture

| Layer | Path | Notes |
|---|---|---|
| Rust core | `crates/tb_core_ffi` + `vendor/tokscale-core` | Public shared engine pinned as a Git submodule; consumer provenance is recorded in `vendor/ENGINE.md`. C ABI, JSON envelope, built as `cdylib` for P/Invoke |
| C ABI contract | `include/ctb.h` | 11 entry points, `{"ok":true,"data":…}` / `{"ok":false,"err":…}` |
| Interop | `src/TokenBar.Interop` | `net10.0`, platform-neutral — P/Invoke facade + envelope decode |
| Logic | `src/TokenBar.Core` | `net10.0`, platform-neutral — C# port of the macOS `TokenBarCore` |
| Shell | `src/TokenBar.App` | WinUI 3, unpackaged. Windows-only build (not in the slnx): `dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64` |
| Windows native packaging | `src/Directory.Build.targets` | The sole `Platform`/`RuntimeIdentifier` → explicit Rust-target mapping. `BuildTbNative` produces the target-specific `tb_core_ffi.dll`; missing/conflicting tuples, wrong PE machines, and stale output/publish bytes fail fast. |

Windows native packaging is opt-in for `TokenBar.App`, `TokenBar.Smoke`, and
`TokenBar.Core.Tests`. The supported tuples are `x64`/`win-x64` →
`x86_64-pc-windows-msvc` and `ARM64`/`win-arm64` →
`aarch64-pc-windows-msvc`; Windows managed builds never select a default
`target/release` DLL.

## Build

Prereqs: Rust `1.96.1`, .NET SDK `10.0.301`, PowerShell `7.0+`; on Windows the MSVC toolchain.

The root `global.json` pins that SDK with `rollForward: disable`, so a machine carrying only a
later 10.0.x cannot run `dotnet` from the repository root at all. [`tools/sdkfree/`](tools/sdkfree/)
is a deliberate escape hatch for that.

On macOS and Linux the Windows native targets are inert, so that is the whole story:

```bash
cd tools/sdkfree && dotnet test ../../src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj
```

**On Windows it is not sufficient on its own.** `TokenBar.Core.Tests` opts into native packaging,
so the same project also needs `-p:Platform=x64` and the `BuildTbNative` step from the Windows
commands below — without them `ValidateTbNativeConfiguration` rejects the default `AnyCPU`/empty-RID
tuple before any test runs. The escape hatch lifts the SDK pin; it does not remove the native
prerequisites.

Use it for tests only. Builds and packaging go through the root pin, which is what makes them
reproducible.

```bash
git submodule update --init --recursive

# macOS (inner loop — no Windows needed)
scripts/check.sh

# Windows x64: restore the locked graph, build the explicit native source, then run the x64 gates
dotnet restore src/TokenBar.slnx --locked-mode
dotnet restore src/TokenBar.App/TokenBar.App.csproj --locked-mode
.\scripts\dev.ps1
dotnet msbuild src/TokenBar.Smoke/TokenBar.Smoke.csproj -t:BuildTbNative -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:TbNativeCargoLocked=true -p:RestoreLockedMode=true
dotnet build src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj -c Release -p:Platform=x64 --no-restore
dotnet test src/TokenBar.Core.Tests/TokenBar.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore
dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore
dotnet build src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -p:Platform=x64 --no-restore
dotnet run --project src/TokenBar.Smoke -c Release -p:Platform=x64 --no-build --no-restore
dotnet publish src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -r win-x64 --self-contained -o out/smoke-win-x64 --no-restore
```

ARM64 is cross-build/package validation only on an x64 runner:

```bash
dotnet restore src/TokenBar.App/TokenBar.App.csproj --locked-mode
dotnet restore src/TokenBar.Smoke/TokenBar.Smoke.csproj --locked-mode
dotnet msbuild src/TokenBar.Smoke/TokenBar.Smoke.csproj -t:BuildTbNative -p:Configuration=Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 -p:TbNativeCargoLocked=true -p:RestoreLockedMode=true
dotnet build src/TokenBar.App/TokenBar.App.csproj -c Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 --no-restore
dotnet publish src/TokenBar.Smoke/TokenBar.Smoke.csproj -c Release -r win-arm64 --self-contained -o out/smoke-win-arm64 --no-restore
```

The unsigned portable App package command performs the locked restore, native
build, publish, ZIP creation, and structure/version/PE/hash checks. Use a clean
Git checkout and a new empty output root on a Windows host or CI runner:

```powershell
.\scripts\build-app-artifact.ps1 -Rid win-x64 -OutputRoot "$env:RUNNER_TEMP\tokenbar-x64"
.\scripts\build-app-artifact.ps1 -Rid win-arm64 -OutputRoot "$env:RUNNER_TEMP\tokenbar-arm64"
```

The cross-language CrossCheck stays platform-neutral and has no Platform/RID:

```bash
TZ=Asia/Taipei dotnet run \
  --project src/TokenBar.CrossCheck \
  -c Release -- \
  crosscheck/fixtures \
  crosscheck/csharp-out
```

CI publishes only short-retention Smoke test-harness artifacts and sanitized
App evidence/checksums. Hosted CI never uploads App ZIP/EXE/DLL files. The
hosted runners perform structure, version, PE, and hash checks; they do not
claim an interactive WinUI startup gate. `cargo fmt` is not currently a
repository CI gate.

See [`docs/verification-history.md`](docs/verification-history.md) for the
historical gate records (which specific runs proved which claim, and when).

Branch naming and merge conventions are in [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Known limitations

Windows does not yet have macOS parity on: Discord Rich Presence, per-client tray items,
Simplified Chinese, menu-bar font colour, a flat-heatmap chart mode, agent brand icons, an
Agent-limits sparkline/chart layout, a Stats attribution-breakdown card, and multi-account
Claude.

## Credits

Shared parsing engine from [tokscale-core](https://github.com/Nanako0129/tokscale-core),
originally derived from [tokscale](https://github.com/junhoyeo/tokscale) by
junhoyeo. Original menu-bar concept by
[handlecusion's tokcat](https://github.com/handlecusion/tokcat).

## License

MIT — see [`LICENSE`](LICENSE).

The shipped binary also contains third-party code, chiefly the vendored
tokscale-core engine compiled into `tb_core_ffi.dll`. Their notices are in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), which packaging copies into
the application payload so it ships beside `Syrtis.App.exe` in the portable ZIP,
the `.nupkg`, and the installer. That file is explicit about being incomplete,
about what it does not yet cover — most of the dependency graph by count — and
about the generator that would close it.
