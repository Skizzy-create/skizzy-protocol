# M3 acceptance checklist: skizzy-map.exe (v1.1.0)

This checklist is for the separate test agent. Branch: `m3-map-app`. Every check gives the steps and the
expected result. Mark each one pass or fail and add a note for every failure. Background is in
docs/M3-spikes.md. Reference screenshots are in docs/map-app-samples/.

Read-only safety: none of these checks touches KSP, GameData or the BDArmory repository. The live-host checks
(L1–L3) only connect to a running KSP; skip them if none is running.

## 0. Build and automated tests

Run everything from the worktree root. Use Git Bash, or set the same variables in PowerShell.

```bash
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build Skizzy.Map.slnx -c Release
dotnet test tests/Skizzy.Map.Render.Tests -c Release --no-build        # expect 88 passed
tests/Skizzy.Map.App.Tests/bin/Release/net10.0/Skizzy.Map.App.Tests.exe # expect all passed (49)
```

- [ ] A0.1: The build ends with 0 warnings and 0 errors. Warnings are treated as errors.
- [ ] A0.2: Render tests: 88 pass.
- [ ] A0.3: App tests: all pass. Run the executable directly to filter, for example
  `-class "*ShellTests"` or `-method "*Banner*"`; `dotnet test --filter` does not reach xunit v3 here.
- [ ] A0.4: `bin/Release/net10.0/ui/perf.txt` has two new lines. Record the numbers. The gate asserts 1.5× the
  60 Hz budget; the 60 fps target itself is reported, not asserted (see P1).
- [ ] A0.5: `bin/Release/net10.0/samples/` holds 8 PNGs that match docs/map-app-samples/ in layout. Pixel values
  can differ slightly.

## 1. Launch

Use a throwaway settings folder so your runs do not touch a real profile.

```bash
dotnet run --project src/Skizzy.Map.App -c Release -- --home "$TEMP/skizzy-map-check"
# other sources: add  --source fake  or  --source live
```

- [ ] S1: The window opens at about 1440×900, centred. Dark theme. Its look matches
  docs/map-app-samples/01-dense-battle-dark.png. Text is in the bundled fonts: Saira for headers and labels,
  IBM Plex Sans Condensed for text, IBM Plex Mono for numbers. There is no Segoe UI or Inter fallback anywhere.
- [ ] S2: The top bar shows, from left to right:
  - SKIZZY COMMAND and the scenario line;
  - the Kerbin UT clock, which ticks;
  - pause, the speed buttons (.25× to 8×) and Restart;
  - the BLUE ↔ RED · WAR pill;
  - Advance, All attack and RTB all (the last two disabled, with an M4 tooltip);
  - the Blue posture segments and the Friendly fire drop-down;
  - the Blue seat pill and Hand seat to agent (disabled).

  Source, Panels, Layouts, Dark/Light and Commands sit at the right edge. The bar wraps when the window is narrow.
- [ ] S3: Resize, move to another monitor, and maximise, then close and reopen. The window comes back at the same
  size, position, monitor and maximised state.
- [ ] S4: Unplug that monitor, or edit `settings.json` so the window position is off-screen, then reopen. The
  window opens centred on a connected screen, not off-screen.
- [ ] S5: Closing the main window ends the app, even when a panel is floating in its own window.

## 2. Theme

- [ ] T1: Click Light in the top bar. Everything switches together: the top bar, panels, Dock tabs and
  splitters, the track table rows, the plot (sea, grid, symbols), the legend, menus and tooltips. Compare with
  02-dense-battle-light.png. No panel keeps dark rows.
- [ ] T2: Restart the app. The theme you picked comes back.

## 3. Docking and layouts

- [ ] D1: The default layout is:
  - Tracks on the left;
  - the map with Messages, Comms and Losses below it, the tab strip at the bottom;
  - Unit, Layers and Host health on the right.
- [ ] D2: Drag a panel's tab to another dock, into a new split, and out of the window. A floating window opens
  and its content stays live.
- [ ] D3: Close a panel with ×, then bring it back from the top-bar Panels toggles (Tracks / Unit / Log). It
  returns to the dock it left.
- [ ] D4: Open another map window from Layouts ▾ → New map window, the + on the map tab strip, or the Layers
  panel. Each map window has its own view, picture (fog/god), layers and filter, and both animate.
- [ ] D5: Layouts ▾ → type a name → Save. Rearrange everything, including floating a panel. Then Layouts ▾ →
  your layout → Load. Every panel, map window, the floating window and its bounds, and each map's view and
  options come back.
- [ ] D6: Layouts ▾ → Delete removes the layout; Reset to default layout restores D1.
- [ ] D7: Quit with a custom arrangement and restart. The session layout is restored.
- [ ] D8: Write garbage into `session-layout.json` and restart. The default layout opens, no error dialog
  appears, and the bad file is renamed `*.bad-<time>`.

## 4. Data sources

- [ ] DS1, Demo (the default): about 864 tracks at start, deterministic. Restart replays the same battle.
- [ ] DS2, FakeKSP (Source → FakeKSP): Host health shows Online, `source.query vessels (M0)`, about 5 Hz and a
  round-trip time. Five vessels appear (DDG-1 Kirov, …, F-14 Tomcat, Tanker). The Unit panel shows Vessel
  extras such as situation, body, lat/lon and crew. Compare with 08-fakeksp-live-source.png.
- [ ] DS3: Switching back to Demo stops the FakeKSP host. Its temp folder under %TEMP%\skizzy-map-fake-* is
  removed.
- [ ] L1, Live (only with KSP running the Skizzy plugin): Source → Live host connects through endpoint
  discovery and tracks appear. Without KSP the state is Reconnecting with a hint, and the app keeps retrying
  without freezing.
- [ ] L2: The auth token never appears in the UI, the logs or `settings.json`.
- [ ] L3: Load a non-flight scene in KSP. The status reads "waiting for the flight scene" and recovers once
  you are back in flight.

## 5. Plot interaction

- [ ] P-1: The mouse wheel zooms smoothly around the cursor. Left-drag or middle-drag pans. Fit (Home) frames
  everything; Shift+Home frames what the filter shows; F frames the selection.
- [ ] P-2: Click selects one track; Shift-click adds or removes. Selected symbols draw 1.2× larger.
  Double-click selects the track's whole group.
- [ ] P-3: Shift-drag draws a box and selects inside it, preferring your own units. Esc clears the selection.
- [ ] P-4: Hovering a track shows the tooltip (name, class, alt, speed, heading). Right-click opens the context
  menu: Select, Select group and Center work; the order items are disabled (M4).
- [ ] P-5: The map toolbar has:
  - the symbol size mode and scale slider;
  - Trails, Altitude, Target lines, Rings, Labels and Counts;
  - −, + and Fit;
  - Blue fog / Red fog / God;
  - filter chips with live counts.

  Each control changes the plot at once. While a filter hides tracks, a "N hidden by filter" note shows.
- [ ] P-6: God view shows stealth tracks that Blue fog hides (03-god-view-target-lines.png). Red fog hides Blue
  units more than 150 km from Red aircraft.
- [ ] P-7: Number keys: with units selected, Alt+1…9 stores the selection. 1…9 recalls it. The slot badge
  appears on the group chip in Tracks.
- [ ] P-8: [ and ] shrink and enlarge the selected symbols, and Unit → Symbol size shows ×N.

## 6. Panels

- [ ] U-1, Tracks: the count note, group chips (with slot badges and All aircraft), filter chips (All /
  Aircraft / Missiles / Ships & subs / Inbound, with counts), and a sortable table.
  - Click a header to sort; click again to reverse.
  - The default sort is range, nearest first.
  - Click a row to select it, Shift-click to add, double-click to centre the map.
  - The wheel scrolls.
- [ ] U-2, Unit: select Kestrel. It shows:
  - the header (symbol, name, class · side) and the mission pill;
  - Brg, Rng, Alt, Hdg, Speed, RCS;
  - magazine bars and damage;
  - Orders for the selection and the Flight deck;
  - Weapons control and Radar segments (read-only);
  - Symbol size.

  The order buttons are disabled and their tooltips say M4. Selecting several units shows the multi-select
  note and Make group.
- [ ] U-3, Messages: category chips with counts toggle each category. Clicking a message centres the map on the
  unit, and in replay it also scrubs to that time.
- [ ] U-4, Comms: the channel chips, with sender colours by side and an UNVERIFIED SENDER badge where it
  applies. For the demo source, send a Diplomatic message; a canned Red reply arrives about 2.5 s later.
- [ ] U-5, Losses: Blue and Red columns update as units die.
- [ ] U-6, Host health: the source state pill, endpoint, round trip, update rate, track and message counts,
  plus the active map's fps, draw time, layer timings and symbol counts.
- [ ] U-7, Layers: every option applies to the active map window. Switching map windows rebinds the panel.

## 7. Time, replay and the pause-on-event banner

- [ ] R-1: Space pauses and resumes. The speeds change sim speed (demo only).
- [ ] R-2: About ten sim minutes in, the first launch pauses the battle, and a red PAUSED ON EVENT banner
  appears under the map toolbar with Resume and Don't pause again (06-paused-on-event.png).
  - Resume hides the banner and the battle continues.
  - Don't pause again resumes the battle and stops later pauses; later events are still logged.
- [ ] R-3: Drag the timeline scrubber back. A REPLAY pill shows, the picture shows the past (interpolated),
  the battle halts as in the mockup, and Back to live returns to the live edge (07-replay-scrub.png).
- [ ] R-4: Restart resets the battle, the selection, the groups and the clock.

## 8. Command palette (Ctrl+K)

- [ ] C-1: Ctrl+K opens the palette with the search focused. Esc goes from a form back to the list, and a second
  Esc closes it. Clicking the dimmed area closes it.
- [ ] C-2: App actions are listed first: Fit, New map window, Toggle theme, Reset layout, Show / hide panels and
  Source. Enter runs one and closes the palette.
- [ ] C-3: Host capabilities come from `catalog.get` (about 32 with FakeKSP). Each has an area and a safety pill:
  read is green, control amber, and destructive, cheat or unsafe red.
- [ ] C-4: Open `ksp.vessels.list` with FakeKSP and click Run. Pretty-printed JSON appears.
- [ ] C-5: A control capability shows Run…, which asks Confirm or Cancel before it runs.
- [ ] C-6: Destructive, cheat and unsafe capabilities show a disabled Run and an M3/M4 note. Capabilities the
  source cannot serve show Unavailable.
- [ ] C-7: The form fields follow the input schema:
  - text, integer and number boxes, checked strictly ("1,5" is rejected);
  - a check box for each boolean;
  - a drop-down for each enum;
  - JSON boxes for objects and arrays.

  Required fields are marked with *. Errors show inline.

## 9. Keyboard reference (verify each)

| Key | Action |
| --- | --- |
| Space | pause / play |
| F | fit selection |
| Home / Shift+Home | fit all / fit filter |
| Esc | clear selection (or close the palette) |
| [ / ] | shrink / enlarge the selected symbols |
| + / − | zoom in / out |
| Arrows | pan |
| 1…9 / Alt+1…9 | recall / store a selection |
| Ctrl+K | command palette |

## 10. Performance

- [ ] P1, desktop GPU run (not yet measured; only headless figures exist). Use a 1920×1080 window and the demo
  at 8×, about 12 sim minutes in (dense raid). Open Host health and read the fps and draw time for the active
  map. Record both. The target is 60 fps.
- [ ] P2: With two map windows open, both keep animating smoothly, and Host health shows each one's draw time.
- [ ] P3: Run the app for 30 minutes. Memory levels off (history rings are capped at 20 minutes) and the UI never
  hitches more than once a second.

## 11. Licensing

- [ ] LIC-1: THIRD-PARTY-NOTICES.md lists every package in `obj/project.assets.json` for the app. Every licence
  is MIT, BSD, Apache-2.0 or OFL, with nothing GPL or non-commercial.
- [ ] LIC-2: The build output contains `Licences/Fonts/OFL-*.txt` for all three font families.

## Known gaps (not failures for M3)

- Orders, seats and agent hand-over are laid out but disabled until M4.
- The `source.tactical` stream and `.skzrec` recordings arrive in M2. Today replay is in memory and covers the
  last 20 minutes.
- 5,000 symbols sit on the 60 fps line on a CPU raster (spikes doc §2); a render-core follow-up is noted there.
