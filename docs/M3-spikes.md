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

- **How it draws (desktop).** Each map window has a plot thread (`Plot/PlotSurface.cs`):
  1. The UI thread syncs the mirror into a free scene (three scenes, triple-buffered) and publishes it. It
     never waits for drawing.
  2. The plot thread renders the latest scene with `PlotRenderer.Render` into a CPU raster bitmap (two,
     alternating) at the window's pixel density.
  3. `PlotControl` emits an `ICustomDrawOperation`. On Avalonia's render thread it leases the window's Skia
     canvas (`ISkiaSharpApiLeaseFeature`) and only draws the latest bitmap, moved and scaled from the viewport it
     was rendered for to the live one. A pan or zoom tracks the pointer even when the plot thread is a frame
     behind; hit tests map the point the same way.
- **Why a raster bitmap.** The plot is thousands of small anti-aliased paths, sprites and text runs. Skia's CPU
  rasteriser draws that about 3× faster than its GPU (ANGLE) backend: 19 ms against 58 ms at 5,000 tracks on
  the same desktop. One bitmap blit costs about 1 ms.
- **Threading** (the coordinator's constraint: "the renderer isn't thread-safe; layout runs on the render thread"):
  - each map window owns its own `PlotRenderer` and `RenderState`, inside its `PlotSurface`;
  - they are only used under the surface's `Gate`: by the plot thread to render, by the UI for click hit tests,
    and for a theme swap, where the old renderer is disposed under the lock;
  - hover hit tests use `TryEnter` and keep the last hover rather than wait;
  - layer options belong to the UI thread and are copied into the render state at the start of each frame.
    A test checks that every `PlotOptions` property is copied.
  - a plot that has not been drawn for 0.5 s (a hidden tab, a minimised window) stops rendering until it is
    drawn again, so background map windows cost only their scene sync.

  No renderer is ever used from two threads at once.
- **Synchronous mode.** Headless tests (and `--plot-sync`) keep one scene, synced under `Gate` by the UI and
  drawn under `Gate` on the render thread, so screenshots and assertions are deterministic. `--plot-gpu` with
  `--plot-sync` draws straight onto the GPU canvas, for comparison.
- **Frame loop.** `TopLevel.RequestAnimationFrame` drives the frame loop. Each frame calls `AppState.Frame(dt)`:
  1. the source ticks;
  2. each map publishes its scene and invalidates its plot;
  3. panels refresh on a 0.4 s slow tick.

  The display's vsync paces the loop.
- **Measured on the desktop.** Run `skizzy-map.exe --bench 5000|2000|demo [--bench-seconds 8]`. It opens the
  real window maximised with a clean profile, warms up for 3 s, measures, and appends one line to `bench.txt`
  next to the exe. Test machine: 2560×1600 screen at 150 % scaling, 1707×1044 window, plot 953×689 logical px
  (1430×1034 device px), display clock about 73 Hz. Release build, 8 s per run.

  | Case | UI frames | Plot frames | Plot draw, median (p95) | UI frame work, median |
  | --- | --- | --- | --- | --- |
  | 5,000 synthetic tracks, plot thread | **70 to 74 fps** | **42 to 46 fps** | 20 to 22 ms (28 to 32) | 2.6 ms |
  | 2,000 synthetic tracks, plot thread | 74 fps | 73 fps | 9.8 ms (14.3) | 1.0 ms |
  | Demo battle at 8×, dense raid (1,012 tracks), plot thread | 73 fps | 73 fps | 6.8 ms (9.2) | 2.0 ms |
  | 5,000, `--plot-sync` (raster bitmap on the render thread) | 24 to 26 fps | same | 18 to 19 ms | 20 ms, of which 17 ms waiting for the draw |
  | 5,000, `--plot-sync --plot-gpu` (straight on the GPU canvas) | 10 fps | same | 58 ms | 55 ms waiting |

  At 5,000 the plot layers take: layout 5.2 to 5.8 ms, tracks 6.7 to 8.6 ms (4,611 dots, 18 full symbols at
  fit-all), trails 4.1 to 4.5 ms, labels 0.4 ms. The UI side (data source, scene sync, panels) stays under 3 ms.
- **Measured headless.** These are the perf tests in `PerfAndSampleTests.cs`; every run appends to
  `bin/Release/net10.0/ui/perf.txt`. Ranges are over several runs on a machine that other builds were also
  using.

  | Case | Result |
  | --- | --- |
  | Render core alone, 5,000 moving tracks, full 1920×1080 frame | best batch median **16.7 to 20.7 ms (48 to 60 fps)**; p95 24 to 31 ms; 0 B allocated per frame |
  | In-app, synchronous: 1920×1080 window, plot 1077×717 px, all 5,000 on screen; data, sync and panels plus plot draw | best batch median **15.4 to 17.3 ms (58 to 65 fps)**, of which data, sync and panels take 1.2 to 1.6 ms |
  | Whole window composited headless (software) | 28 to 36 ms (28 to 35 fps) |

- **Verdict: honest, not fully green.** The app itself runs at the display rate with 5,000 tracks: 70 to 74 fps,
  with 2.6 ms of UI work per frame, and pans and zooms follow the pointer. The plot picture at 5,000 refreshes at
  42 to 46 fps at this 150 % pixel density. Below about 2,000 tracks, and in the dense demo raid (1,012 tracks),
  everything holds the display rate. The remaining cost is entirely the render core's draw.
- **Gate in the tests.** The tests assert a regression guard at 1.5× the frame budget and report the measured
  figure. Asserting 60 fps exactly would fail at random on a loaded machine.
- **Render core follow-up.** These would bring 5,000 under 16.7 ms on the plot thread:
  - batch the dot tier (one `DrawPoints` per colour instead of a path per dot): dots are 7 to 9 ms;
  - make the no-overlap layout incremental (5 to 6 ms);
  - draw trails as one path per colour and alpha bucket (4 to 4.5 ms).

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
