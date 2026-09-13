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

**Not yet enumerated:** two sets, named here rather than left silent.

- **The Rust dependency graph.** `Cargo.lock` resolves 264 packages. Only the
  direct dependencies of `crates/tb_core_ffi` and the vendored engine appear
  below. Producing the full list needs a generator (`cargo about` or
  equivalent) wired into CI so it cannot go stale; until that exists this file
  does not claim to cover the transitive graph.
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
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | MIT | — |
| Vortice.Direct3D11 | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.D3DCompiler | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.DXGI | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/windowsappsdk) | 1.8.260710003 | Microsoft Software License Terms (the package's own `license.txt`, not an SPDX expression) | © Microsoft Corporation. All rights reserved. |

Test-only and build-only packages — xunit, coverlet, Microsoft.NET.Test.Sdk,
Microsoft.Windows.SDK.BuildTools, Microsoft.NETFramework.ReferenceAssemblies —
are excluded because nothing from them is redistributed.

---

## Rust direct dependencies

Direct dependencies of `crates/tb_core_ffi`, all compiled into
`tb_core_ffi.dll`. Their own licences are declared in their crates.io metadata;
the transitive graph they pull in is the set this file does not yet enumerate.

`tokscale-core` (vendored, above), `serde`, `serde_json`, `reqwest`,
`hyper-util`, `tower-service`, `rustls`, `base64`, `hmac`, `sha2`, `fs2`,
`tokio`, `parking_lot`, `chrono`.
