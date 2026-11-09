# M3 spikes: the map app stack

Milestone M3 builds `skizzy-map.exe` (release v1.1.0, rows 1 and 2 of Phase v1 in design/24-ksp-parity.md).
Before building the app shell, four questions had to be settled. This page gives each question, what was
tried, and what was decided. The code that proves each answer is on branch `m3-map-app`.

## 0. Library line and pins (coordinator decision)

The lead chose **Avalonia 12.1.5, SkiaSharp 3.119.4 and Dock.Avalonia 12.1.5**. This replaces "Avalonia 11"
in doc 11 and the brief. The reason is that Avalonia 11.3 pins SkiaSharp 2.88. A process can load only one
SkiaSharp, and the render core needs 3.x. Avalonia 12 targets net8 and net10, and the app targets net10.0.
SkiaSharp 4.x is not used.

Exact pins:

| Package | Version | Licence |
| --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Skia, Avalonia.Themes.Fluent | 12.1.5 | MIT |
| Dock.Avalonia, Dock.Model.Avalonia, Dock.Avalonia.Themes.Fluent | 12.1.5 | MIT |
| SkiaSharp (through the render core), SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT |
| Avalonia.Headless.XUnit (tests) | 12.1.5 | MIT |
| xunit.v3 (tests) | 3.2.2 | Apache-2.0 |

The full transitive list is in THIRD-PARTY-NOTICES.md.

`Avalonia.BuildServices` comes in with Avalonia and sends anonymous build telemetry. Builds set
`AVALONIA_TELEMETRY_OPTOUT=1` to turn it off.

Avalonia 12 API changes we hit:

- `TextBox.Watermark` is obsolete; use `PlaceholderText`.
- `TryFindResource` is an extension method, so it needs a `this.` receiver.
- `Dock.Right` is ambiguous between `Avalonia.Controls.Dock` and the `Dock.*` namespaces, so the enum is written out in full.
- Headless key input uses `KeyPressQwerty(PhysicalKey, RawInputModifiers)`.

## 1. Dock.Avalonia: licence and layout serialisation

- **Licence.** Every Dock package we use is MIT. That includes the transitive ones: Dock.Model,
  Dock.Settings, Dock.MarkupExtension and Dock.Controls.*.
- **Model.** We use `Dock.Model.Avalonia`:
  - each panel is a `Tool`, and each map window is a `Document`;
  - the factory is `ShellDockFactory : Factory`;
  - `HideToolsOnClose` is on, so a closed panel is hidden and the Panels toggles bring it back;
  - a closed map window frees its view model;
  - content comes from a `Func<IServiceProvider, object>` per dockable Id. That works for both tools and
    documents, including in floating windows.
- **Serialisation: Dock's own serializer is rejected.** `Dock.Serializer.SystemTextJson` 12.1.5 can write a
  `Dock.Model.Avalonia` tree but cannot read it back. The model types are Avalonia `StyledElement`s.
  Without the package's source-generated contracts, the reflection fallback serialises
  `Resources (IResourceDictionary)`, and deserialisation then throws `NotSupportedException`.
- **Decision.** We save our own small, versioned `DockSnapshot` (`src/Skizzy.Map.App/Shell/DockSnapshot.cs`).
  It holds:
  - the split tree (orientation and proportions);
  - tool docks and the map documents dock (ids in tab order, plus the active id);
  - hidden panels, with the dock each was hidden from;
  - floating windows, with their bounds and contents.

  Restore rebuilds the tree through the factory and drops unknown or duplicate ids. Floating windows come
  back through `SplitToWindow` and hidden panels through `HideDockable`. If no map window would survive,
  restore fails and the current layout stays.
- **Persistence.** Named layouts are saved as `layouts/<name>.json`. The session is saved as
  `session-layout.json` on exit and restored at start. Each layout also stores every map window's settings
  (`MapConfig`). Files live in `%APPDATA%/skizzy/map`; `SKIZZY_MAP_HOME` or `--home` overrides that.
  Writes are atomic, and corrupt files are moved aside rather than thrown.
- We dropped the serializer package, so there is one dependency fewer.
- **Tests:**
  - `NamedLayoutRoundTripsThroughDisk`
  - `SessionLayoutAndThemeComeBackOnTheNextStart`
  - `CorruptSessionFallsBackToTheDefaultLayout`
  - `FloatingPanelComesBackInItsOwnWindow`

## 2. Hosting PlotRenderer in Avalonia at 60 fps

- **How it draws.** `PlotControl.Render` emits an `ICustomDrawOperation`. On the render thread, that
  operation leases Avalonia's own Skia canvas through `ISkiaSharpApiLeaseFeature` and calls
  `PlotRenderer.Render` straight into the window's surface, with no bitmap copy (`Plot/PlotControl.cs`).
- **Threading** (the coordinator's constraint: "the renderer isn't thread-safe; layout runs on the render thread"):
  - each map window owns its own `PlotRenderer`, inside its `PlotSurface`;
  - only the render thread calls that renderer, and only while holding the surface's `Gate`;
  - the UI thread takes the same `Gate` while it mutates the scene (data sync, selection, options);
  - on a theme change the old renderer is disposed under the lock.

  No renderer is ever shared or used from two threads.
- **Frame loop.** `TopLevel.RequestAnimationFrame` drives the frame loop. Each frame calls `AppState.Frame(dt)`:
  1. the source ticks;
  2. each map syncs its scene and invalidates its plot;
  3. panels refresh on a 0.4 s slow tick.

  The display's vsync paces the loop.
- **Measured.** Release build on this 16-thread desktop CPU with CPU raster. The perf tests are in
  `PerfAndSampleTests.cs`; every run appends to `bin/Release/net10.0/ui/perf.txt`. Ranges are over several runs
  on a machine that other builds were also using.

  | Case | Result |
  | --- | --- |
  | Render core alone, 5,000 moving tracks, full 1920×1080 frame | best batch median **16.7 to 20.7 ms (48 to 60 fps)**, the high end on a busy machine; p95 24 to 31 ms; 0 B allocated per frame |
  | Inside the app: 1920×1080 window, plot 1077×717 px, all 5,000 on screen (18 full symbols, 4,825 dots at fit-all); data source, scene sync and panels plus plot draw | best batch median **15.4 to 17.3 ms (58 to 65 fps)**, of which data, sync and panels take 1.2 to 1.6 ms |
  | Plot layers at 5,000 (render core timings) | layout 4.4 to 7.4 ms, tracks 6.1 to 6.7 ms, trails 2.6 to 4.7 ms, labels 0.3 ms |
  | Whole window composited headless | 28 to 36 ms (28 to 35 fps) |

  The headless whole-window figure is low because headless rasterises every panel in software on the test
  thread. On the desktop Avalonia composites on the GPU (ANGLE on Windows), so that cost largely goes away.
  A desktop GPU run has not been measured yet; it is item P1 in the checklist.
- **Verdict: honest, not green.** 5,000 symbols land on the 60 fps line, not reliably above it. The host side
  is cheap: about 1.5 ms per frame for the data, sync and panels. The rest is the render core's own draw, and
  its no-overlap layout pass alone takes 4 to 7 ms at 5,000 and runs on the render thread.
- **Gate in the tests.** The tests assert a regression guard at 1.5× the frame budget and report the measured
  figure. Asserting 60 fps exactly would fail at random on a loaded machine.
- **Render core follow-up.** Make the layout incremental, or move it off the render thread, and batch the dot
  tier (one draw per colour). Either should bring 5,000 symbols well under budget.

## 3. Headless rendering for tests

- **Setup.**
  - Test library: `Avalonia.Headless.XUnit` 12.1.5 on xunit.v3 3.2.2.
  - App builder: `.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })`.
  - The bundled fonts are the default family.
  - The Skia lease works headless, so the real plot draws in tests.
  - `Window.CaptureRenderedFrame()` returns the composited frame. That frame is used for the screenshots in
    docs/map-app-samples/.
- **Determinism.** The app's display clock is off in tests (`MainWindow.AutoFrames = false`). Tests step
  `AppState.Frame` themselves and call `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`. Input is real
  headless input: `MouseDown`, `MouseMove`, `MouseUp`, `MouseWheel` and `KeyPressQwerty`.
- **Fonts.** A test checks that every family resolves to the bundled files at the weights in use. Face names
  carry the weight, for example "Saira SemiCondensed Medium".
- **Gotcha.** `dotnet test --filter` does not reach xunit v3 filters through VSTest here. To run one test, run
  the test executable directly: `Skizzy.Map.App.Tests.exe -method "*Name*"` or `-class "*Class*"`.

## 4. What the shell does with the data (for reviewers)

- **Sources.** `IDataSource` has three implementations:
  - `DemoBattleSource`: the mockup's battle, ported to C#, deterministic, seed 11;
  - `FakeKspDataSource`: the M0 SimHost in-process on a free loopback port;
  - `HostDataSource`: a live host found by `Endpoint.Discover`.

  The host sources first try `stream.subscribe` with `source.tactical`. M0 answers that with an error
  ("tactical sources arrive in M2"), so they fall back to the M0 vessel query stream at 5 Hz. Positions are
  projected azimuthal-equidistant about the first fix. Protocol errors (`SkizzyError`) never kill the
  connect loop, and the token is never shown or logged.
- **TrackMirror** (`MapData` + `TrackInfo`):
  - history rings at 1 Hz, keeping 20 min;
  - live extrapolation with 0.25 s error-blend smoothing;
  - replay reads the history rings with Hermite interpolation;
  - fog is per view: Blue, Red or God.
- **Replay is in memory.** `.skzrec` recordings wait for M2.
- **Orders are read-only until M4.** They are laid out as in the mockup and shown disabled, with tooltips
  that say so.
