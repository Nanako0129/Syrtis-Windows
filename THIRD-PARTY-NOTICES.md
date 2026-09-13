# Third-party notices

Syrtis is distributed as a binary that contains third-party components. This
file collects the notices those components require, and it is copied into the
application payload so that it travels with the binary rather than only living
in this repository — that is what the licences ask for.

It is **incomplete**. See Coverage below for exactly what it does and does not
carry, and for the remaining work.

It is shipped beside `Syrtis.App.exe` in every distribution: the portable ZIP,
the Velopack `.nupkg`, and therefore the `Setup.exe` built from it.

---

## Coverage — this file is not complete, and says so on purpose

**It does not yet discharge the project's third-party notice obligations.** It
carries the notices that have been verified one at a time, and the shipped
payload contains components it does not mention. Read it as work in progress
with a known remainder, not as the notice.

What is here has been verified against each component's own metadata rather
than recalled: the vendored engine's licence in full, and the directly
referenced .NET packages with the licence and copyright from their `.nuspec`.

What is missing is most of it, by count:

| Set | Size | State |
| --- | --- | --- |
| .NET packages referenced directly | 6 | licence + copyright below |
| .NET packages resolved transitively | 29 total in `packages.lock.json` | **not covered** — `H.NotifyIcon`, `SharpGen.Runtime`, `SharpGen.Runtime.COM`, `Vortice.DirectX`, `Vortice.Mathematics` and others ship as DLLs in `lib/app` |
| Rust crates, direct and transitive | 264 in `Cargo.lock` | **not covered** — the inventory below is names only |
| The bundled .NET 10 runtime | — | **not covered** — terms at <https://github.com/dotnet/runtime> |
| Windows App SDK payload | — | **partially** — named and linked below, but its `license.txt` is neither reproduced here nor shipped |

Hand-curation is the wrong instrument for roughly 293 components and was
abandoned after five successive review findings, each correctly naming a set
this file had silently skipped. The remaining work is therefore **a generator
rather than more entries**: resolve `Cargo.lock` against crates.io and
`packages.lock.json` against nuget.org, emit licence identifier plus copyright
per component, and run it in CI so a committed copy cannot drift from the lock
files. Completing a few sets by hand would leave the file reading as finished
while staying just as incomplete, which is worse than the state it is in now.

Until that lands, the sections below are the verified subset and nothing more.

---

## tokscale-core

The shared parsing engine, vendored at `vendor/tokscale-core` and compiled into
`tb_core_ffi.dll`, which ships inside every package. Derived from
[tokscale](https://github.com/junhoyeo/tokscale).

```
MIT License

Copyright (c) 2025 Junho Yeo

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## .NET packages

Licence and copyright below were read from each package's own `.nuspec` on
nuget.org, not from memory. The MIT text reproduced above applies to each
package marked MIT, with the copyright line shown for that package.

| Package | Version | Licence | Copyright |
| --- | --- | --- | --- |
| [Velopack](https://github.com/velopack/velopack) | 1.2.0 | MIT | Copyright © Velopack Ltd. |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | MIT | Copyright (c) 2020 havendv |
| Vortice.Direct3D11 | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.D3DCompiler | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.DXGI | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/windowsappsdk) | 1.8.260710003 | Microsoft Software License Terms (the package's own `license.txt`, not an SPDX expression) | © Microsoft Corporation. All rights reserved. |

Test-only and build-only packages — xunit, coverlet, Microsoft.NET.Test.Sdk,
Microsoft.Windows.SDK.BuildTools, Microsoft.NETFramework.ReferenceAssemblies —
are excluded because nothing from them is redistributed.

---

## Rust direct dependencies — inventory only, not a notice

**This section does not discharge anything.** It records which crates are
compiled into `tb_core_ffi.dll` so the scope of the outstanding work is
visible; it carries no licence identifiers and no copyright lines, which is
what the licences actually require. See Coverage above.

Every dependency declared in `crates/tb_core_ffi/Cargo.toml`:

Unconditional — in every artifact:

`tokscale-core` (vendored, above), `serde`, `serde_json`, `reqwest`,
`hyper-util`, `tower-service`, `rustls`, `base64`, `hmac`, `sha2`, `fs2`,
`tokio`, `parking_lot`, `chrono`, `dirs`, `rayon`.

Windows-only, so present in every artifact this repository ships:

`windows-sys`.

`security-framework` is declared under
`[target.'cfg(target_os = "macos")'.dependencies]` and is therefore compiled
into no Windows artifact. It is listed here only so that the absence reads as
deliberate.

To re-derive this list rather than trust it, read the `[dependencies]` and
`[target.*.dependencies]` tables of `crates/tb_core_ffi/Cargo.toml` in full —
the first version of this section was written from a truncated view of that
file and silently omitted `dirs`, `rayon`, and `windows-sys`.
