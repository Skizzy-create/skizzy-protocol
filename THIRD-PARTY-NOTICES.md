# Third-party notices

Skizzy Protocol is licensed under Apache-2.0 (see `LICENSE` and `NOTICE`). The components below keep
their own licences. Every one is Apache-2.0 compatible: MIT, BSD, Apache-2.0, or the SIL Open Font
License for fonts. None is GPL, LGPL, AGPL, or non-commercial.

This file currently covers the tactical map: `skizzy-map.exe` (`src/Skizzy.Map.App`) and
the libraries under it (`Skizzy.Map.Data`, `Skizzy.Map.Render`). Other components add their own section
when they take a third-party dependency.

Versions are pinned exactly in the project files. The lists come from the resolved
`obj/project.assets.json`, and each licence was checked against the package's nuspec licence
expression on 2026-11-08.

## Shipped with skizzy-map.exe

| Component | Version | Licence | Source |
| --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Skia, Avalonia.Themes.Fluent (plus Avalonia.Win32, .X11, .Native, .FreeDesktop, .FreeDesktop.AtSpi, .HarfBuzz, .Remote.Protocol, which come in transitively) | 12.1.5 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Angle.Windows.Natives (ANGLE, the GPU backend on Windows) | 2.1.27548.20260419 | BSD-3-Clause (The ANGLE Project Authors) | https://github.com/AvaloniaUI/angle |
| Dock.Avalonia, Dock.Model, Dock.Model.Avalonia, Dock.Avalonia.Themes.Fluent (plus Dock.Settings, Dock.MarkupExtension, Dock.Controls.ProportionalStackPanel, .DeferredContentControl, .Recycling, .Recycling.Model, which come in transitively) | 12.1.5 | MIT (Wiesław Šoltés) | https://github.com/wieslawsoltes/Dock |
| SkiaSharp, SkiaSharp.NativeAssets.Win32 / .Linux / .macOS / .WebAssembly | 3.119.4 | MIT (Microsoft) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp and its native assets | 8.3.1.3 | MIT (Microsoft) | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | 0.11.6 | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol (used on Linux only) | 0.94.1 | MIT | https://github.com/tmds/Tmds.DBus |

### Fonts

All three fonts are embedded in `Skizzy.Map.Render.dll` and linked into the app as Avalonia resources.
Their licence texts ship next to the executable under `Licences/Fonts/`. Provenance is recorded in
`src/Skizzy.Map.Render/Fonts/SOURCE.txt`.

| Font | Files | Licence |
| --- | --- | --- |
| Saira SemiCondensed (Omnibus-Type) | Medium, SemiBold, Bold | SIL OFL 1.1 |
| IBM Plex Sans Condensed (IBM) | Regular, Medium, SemiBold, Bold | SIL OFL 1.1 |
| IBM Plex Mono (IBM) | Regular, Medium | SIL OFL 1.1 |

## Build time only (not shipped)

| Component | Version | Licence | Note |
| --- | --- | --- | --- |
| Avalonia.BuildServices | 11.3.2 | MIT | Comes in with Avalonia. It sends anonymous build telemetry. Set `AVALONIA_TELEMETRY_OPTOUT=1` to turn it off; the M3 build and test commands in docs/M3-checklist.md set it. |

## Tests only (not shipped)

| Component | Version | Licence |
| --- | --- | --- |
| xunit.v3 (and .assert, .common, .core, .extensibility.core, .runner.*) | 3.2.2 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |
| xunit.analyzers | 1.27.0 | Apache-2.0 |
| Avalonia.Headless, Avalonia.Headless.XUnit | 12.1.5 | MIT |
| Avalonia.Fonts.Inter (package MIT; the Inter font is SIL OFL 1.1) | 12.1.5 | MIT / OFL 1.1 |
| Microsoft.NET.Test.Sdk, Microsoft.TestPlatform.*, Microsoft.CodeCoverage | 18.10.1 | MIT |
| Microsoft.Testing.Platform and its extensions | 1.9.1 | MIT |
| Microsoft.ApplicationInsights (pulled in by the test platform) | 2.23.0 | MIT |
| Microsoft.Bcl.AsyncInterfaces 6.0.0, Microsoft.Win32.Registry 5.0.0 | – | MIT |

The MIT, BSD-3-Clause and OFL licences ask for their copyright and permission notices to travel with
binary redistributions. A release zip of skizzy-map must include this file and the `Licences/` folder.
