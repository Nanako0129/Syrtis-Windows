# Third-party notices

Syrtis is distributed as a binary that contains third-party components. This
file carries the notices those components require, and it is copied into the
application payload so that it travels with the binary rather than only living
in this repository — that is what the licences ask for.

It is shipped beside `Syrtis.App.exe` in every distribution: the portable ZIP,
the Velopack `.nupkg`, and therefore the `Setup.exe` built from it.

---

## Coverage

**Complete and verified:** the vendored parsing engine and the .NET packages
this repository references directly. Those are enumerated below with licence
text or SPDX identifier read from each component's own metadata.

**Not covered:** two sets, named here rather than left silent. Neither is
discharged by this file, and both are outstanding obligations rather than
footnotes.

- **The Rust crates**, direct and transitive alike. `Cargo.lock` resolves 264
  packages. The section below lists the direct ones by name, which is an
  inventory and not a notice: naming a crate places neither its licence
  identifier nor its copyright line in the payload, and most of those licences
  require exactly that. Discharging this needs a generator — `cargo about` or
  equivalent — run in CI so the output cannot go stale, and the result
  concatenated into this file. Doing the sixteen direct crates by hand while
  248 stay missing would leave the file just as wrong while reading as though
  it were finished.
- **The bundled .NET runtime.** Full-channel packages carry the .NET 10
  runtime files (`coreclr.dll`, `clrjit.dll`, and the rest of `lib/app`),
  redistributed from Microsoft's .NET distribution. Its terms are published at
  <https://github.com/dotnet/runtime> and <https://dotnet.microsoft.com/> and
  are not reproduced here.

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
what the licences actually require. See "Not covered" above.

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
