# M3 command parity: control inventory

The functional spec is the web mockup `design/mockups/command-screen-v1.html`. Every control in it must work in the
desktop map app (`src/Skizzy.Map.App`). This file lists each control with three things:

- what the mockup does,
- the app's state at `main` 6371b15 (the v1.1.0 map app),
- the target behaviour and how it is wired.

The results, one row per control with its test, are in `M3-command-parity-report.md`.

**App state** (at 6371b15) uses three values:

| State | Meaning |
|---|---|
| works | Behaves as in the mockup. |
| display | Shown, but has no effect, or is greyed out with an "orders arrive in M4" tooltip. |
| missing | Not present at all. |

**Command path.** All orders go through one app service, `CommandService`. It sends the same c2.* contract as agents and
the CLI (`c2.order.issue`, `c2.order.cancel`, `c2.doctrine.set`, `c2.unit.release`, `c2.vip.set`) to whichever source is
open:

| Source | How c2.* calls are served |
|---|---|
| Live host | Sent through `SkizzyClient` to the Skizzy host. |
| FakeKSP | Sent through `SkizzyClient` to the in-process host. |
| Demo battle | Served in-process by the real `Skizzy.C2` engine (`C2Engine`). The engine drives the demo battle through an `IC2World`/`IBdaControl`/`IHookEvents` executor in `BattleSim`, so demo units move, engage, RTB, fly racetracks and launch from deck as in the mockup. |

Refusals come back with the engine's reasons (for example `NOT_APPLICABLE` "Kestrel is a carrier (a base)", or
`HOOK_MISSING`). They appear in Messages (category Orders) and as a toast.

Controls a source cannot perform are shown disabled with a tooltip that says why. These exceptions are listed at the end
and kept to a minimum.

## Top bar

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 1 | Pause / play (`❚❚`/`▶`) | Toggles the sim clock. | works | Unchanged. |
| 2 | Speed segment | Sets the sim rate. | works | Unchanged. |
| 3 | Restart | Restarts the scenario. | works | Unchanged. The demo's C2 engine and orders also reset. |
| 4 | Stance pill "Blue ↔ Red · War" | Display, with a tooltip. | works | Unchanged. |
| 5 | UT clock | Shows the scenario UT. | works | Unchanged. |
| 6 | Advance: on/off | Toggles advance on contact.<br>Logs "Advance on contact on." or "Advance on contact off: aircraft hold their stations until ordered." | display (sim flag only, no log) | `c2.doctrine.set` side Blue `advanceOnContact`. Demo mirrors the flag into the sim. Mockup log line. |
| 7 | All attack | Two clicks within 3 s.<br>First click: the label becomes "Confirm all attack" and a toast appears.<br>Second click: every Blue unit attacks the nearest hostiles and the carriers launch their decks. | display (disabled) | Same confirm flow. Then `c2.order.issue all_attack` side scope. Demo carriers launch ready aircraft to hunt. Engine summary in the log. |
| 8 | RTB all | Two clicks within 3 s ("Confirm RTB all", with a toast). Then every aircraft goes to the marshal stack and lands one at a time. | display (disabled) | Same confirm flow. Then `c2.order.issue rtb` with every Blue aircraft. Demo recover adapter lands them one every 5 s. |
| 9 | Blue posture Defensive / Proactive | Sets the posture and logs the posture line. | display (sim only, no log) | `c2.doctrine.set` side `posture`. Demo mirrors it into the sim. Mockup log line. |
| 10 | Friendly fire select | Sets the misfire rate and logs it. | works on demo (no log) | Demo: sets the sim rate and logs the mockup line. |
| 11 | Seat pill and "Hand seat to agent" / "Take seat back" | Toggles the seat label and logs the hand-over. | display (disabled) | Demo: toggles the seat with the mockup pill text and log lines. |
| 12 | Panels: Tracks / Unit / Log toggles | Show or hide the panel. | works | Unchanged. |
| 13 | Reset layout | Restores the default panel sizes and shows all panels. | works (only inside the Layouts menu) | Add a top-bar "Reset layout" button as in the mockup. |

## Panel chrome

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 14 | Panel × close (Tracks, Unit, Log) | Hides the panel. | works (Dock close) | Unchanged. |
| 15 | Splitter drag | Resizes the panel. | works (Dock splitters) | Unchanged. |
| 16 | Splitter arrow keys | A focused splitter resizes by 20 px per key. | missing | Splitters take focus and handle the arrow keys. |

## Tracks panel

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 17 | Group chips (incl. "All aircraft") | Selects the group, with a toast. | works | Unchanged. |
| 18 | Filter chips | All / Aircraft / Missiles / Ships & subs / Inbound, with counts. | works | Unchanged. |
| 19 | Track row click | Selects the track. | works | Unchanged. |
| 20 | Track row double-click | Centres the plot on the track. | works | Unchanged. |

## Plot toolbar and overlays

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 21 | Symbols mode select | Auto / radar size / grow with zoom / fixed. | works | Unchanged. |
| 22 | Symbol scale slider | Scales all symbols. | works | Unchanged. |
| 23 | Trails toggle | Layer on or off. | works | Unchanged. |
| 24 | Altitude toggle | Layer on or off. | works | Unchanged. |
| 25 | Target lines toggle | Layer on or off. | works | Unchanged. |
| 26 | Rings toggle | Layer on or off. | works | Unchanged. |
| 27 | Labels toggle | Layer on or off. | works | Unchanged. |
| 28 | Counts toggle | Layer on or off. | works | Unchanged. |
| 29 | Zoom − | Zooms out. | works | Unchanged. |
| 30 | Zoom + | Zooms in. | works | Unchanged. |
| 31 | Fit | Fits all tracks. | works | Unchanged. |
| 32 | Banner: Resume | Resumes after a pause-on-event. | works | Unchanged. |
| 33 | Banner: Don't pause again | Turns pause-on-event off and logs "Pause on events off. Events are still logged." | works | Unchanged. |
| 34 | Hover tooltip | Shows the track details. | works | Unchanged. |
| 35 | Toast | Shows a status line for 4.5 s. | works | Unchanged. Order flows use it. |
| 36 | Legend | Symbol key. | works | Unchanged. |
| 37 | Replay scrub | Enters replay at that frame. | works | Unchanged. |
| 38 | Live / Back to live | Leaves replay. | works | Unchanged. |

## Plot gestures and order drawing

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 39 | Left click | Selects a track. A click on empty plot clears the selection. | works | Unchanged. |
| 40 | Shift-click | Toggles the track in the selection. | works | Unchanged. |
| 41 | Shift-drag box | Box-selects, preferring own units. | works | Unchanged. |
| 42 | Drag | Pans the plot. | works | Unchanged. |
| 43 | Wheel | Zooms at the cursor. | works | Unchanged. |
| 44 | Double-click | Selects the whole group. | works | Unchanged. |
| 45 | Right-click empty plot | Moves the selection there in formation.<br>Log: "…: move to bearing BBB, R km from Kestrel."<br>With nothing selected, a toast. | missing (context menu with disabled M4 items) | `c2.order.issue move_to`, one per formation slot (air one-point F6 route, ships fly-to). Mockup log line. |
| 46 | Right-click hostile | Attacks it, spreading the shooters over its group.<br>Log: "…: attack X and the rest of its group (N aircraft)." | missing | `c2.order.issue engage`: target is the clicked track, targets are the rest of its group. |
| 47 | Right-click own unit (not selected) | The selection protects it, and it is marked VIP.<br>Log: "…: protect X (now marked VIP). They stay with it and engage anything that comes within 40 km." | missing | `c2.order.issue defend_unit` with `protect`. The engine marks the VIP. |
| 48 | Right-drag | Draws a route (a point every 14 px, after 10 px of movement) and the selection follows it.<br>Log: "…: follow drawn route, N points, ending bearing …". | missing | `c2.order.issue move_route`. A live preview polyline is drawn while dragging. |
| 49 | Racetrack drag (after T or Racetrack…) | Dragging along the plot sets the long leg; a click makes a 20 km leg pointing east.<br>Ships are refused ("Racetrack refused for X: ships cannot fly a holding pattern. Use Send to… or a drawn route."). | missing | `c2.order.issue hold_pattern` (`patternA`, `patternB`, `widthKm` 6). Engine refusal for ships. A live preview is drawn while dragging. |
| 50 | Send to… then click | The next click moves the selection there, or attacks the clicked hostile. | missing | Pending mode, then the same issue as #45 or #46. |
| 51 | Order drawing | Lines to move points and targets, drawn routes, racetrack ovals, the RTB marshal stack. | missing (the renderer supports orders, but nothing feeds them) | Order picture from the source (demo: engine unit views; live: `order.*` events). It goes into `MapData`, and `SceneSync` copies it into `PlotScene.Orders/Routes/Racetracks` without allocating. |

## Keyboard

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 52 | Space | Pause / play. | works | Unchanged. |
| 53 | F | Fit all. | differs (fits the selection) | F fits all, as in the mockup. Shift+F fits the selection. Home is still fit all. |
| 54 | Escape | Cancels a pending racetrack first; otherwise clears the selection. | partial (only clears) | Cancels a pending racetrack, Send to… or route first, then clears the selection. |
| 55 | V | Toggles VIP on the selection.<br>With nothing selected, a toast. | missing | `c2.vip.set` for each selected unit. Mockup log and toast. |
| 56 | A | Attack nearest for the selection. | missing | Same as #67. |
| 57 | Shift+A | All attack (press twice within 3 s). | missing | Same as #7. |
| 58 | R | RTB for the selection. | missing | Same as #68. |
| 59 | Shift+R | RTB all (press twice within 3 s). | missing | Same as #8. |
| 60 | T | Toggles racetrack mode, with a toast ("T again cancels."). | missing | Pending racetrack, then #49. |
| 61 | [ and ] | Shrink or enlarge the selected symbols. | works | Unchanged. |
| 62 | Alt+1…9 | Puts the selection on a number key, making "Group d" if needed. | works | Unchanged. |
| 63 | 1…9 | Recalls the group on that key. | works | Unchanged. |

## Unit panel

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 64 | Header, state and values | Symbol, class, control state ("Your order · …"), Brg/Rng/Alt/Hdg/Speed/RCS, magazines, fuel, damage. | works (control state comes from the sim only) | The control state also reflects engine orders ("Your order · move/attack/RTB · inbound/…"). |
| 65 | Make group (name and button) | Groups the selection, with a log line and a toast. | works | Unchanged. |
| 66 | Take control / Return to mission | Take: the unit is manual and its mission is suspended ("You took control of X. The mission is suspended, not cancelled.").<br>Return: "X returned to <mission>. The mission re-plans from the unit's position." | display (disabled) | Take: `c2.order.issue move_to` at the unit's own position, labelled take_control, so the engine holds it; shown as "Manual · you".<br>Return: `c2.unit.release`; the demo unit resumes its mission AI. |
| 67 | Attack nearest | Aircraft hunt; ships weapons free at full range; carriers launch their decks; AEW and submarines refused ("Not armed for this"). | display (disabled) | `c2.order.issue attack_nearest` for the selection. Demo carriers launch ready aircraft to hunt. |
| 68 | RTB & land | Aircraft go to the marshal stack and land; ships return to station; carriers refused ("X is a carrier (a base)"). | display (disabled) | `c2.order.issue rtb`. Refusals are logged and toasted. Demo recover adapter lands the aircraft. |
| 69 | Send to… | Toggles pending move, with a toast. | display (disabled) | Pending mode (#50). The button shows pressed while pending. |
| 70 | Mark VIP / VIP ✓ | Toggles VIP (#55). | display (disabled) | `c2.vip.set`. |
| 71 | Fly in KSP | Toast: "In the real build this makes X the active vessel in KSP so you can fly it yourself. The map keeps tracking it." | display (disabled) | Demo: the mockup toast. |
| 72 | Center | Centres the plot on the unit. | works | Unchanged. |
| 73 | Racetrack… | Toggles pending racetrack, with a toast ("Esc cancels."). | display (disabled) | Pending racetrack (#49). |
| 74 | Launch ready aircraft (carrier) | Launches the deck's ready aircraft; with none ready, a toast.<br>Log: "B launched N aircraft (…). They return to their missions." | display (disabled) | Demo: `demo.deck.launch` (the demo's launch adapter, as listed in 24-ksp-parity row 31). |
| 75 | Weapons control Free / Tight / Hold | Sets it for the selection.<br>Log: "names: weapons free (guard)." | display (read-only) | `c2.doctrine.set` level unit `weaponsControl` for each selected unit. Mockup log line. |
| 76 | Radar Active / Passive (ships) | Sets the ship's EMCON. Passive stops SAM fire.<br>Log: "X: radar passive." | display (read-only) | Demo: sets the sim EMCON. |
| 77 | On contact: Stay on task / Engage on contact | Sets it for the selection, with a log line. | display (read-only) | `c2.doctrine.set` level unit `onContact` (task/engage). Mockup log line. |
| 78 | Symbol size Smaller / Larger / Reset | Resizes the selected symbols. | works | Unchanged. |
| 79 | Flight deck readout | "N aircraft on deck, M ready · K rearming (90 s)". | works | Unchanged. The demo deck now also fills from engine RTBs. |

## Messages, Comms, Losses

| # | Control | Mockup behaviour | App at 6371b15 | Target and wiring |
|---|---|---|---|---|
| 80 | Tabs Messages / Comms / Losses | Switch the bottom panel. | works (Dock tabs) | Unchanged. |
| 81 | Message category chips | Toggle a category, with counts. | works | Unchanged. Order and refusal lines use the Orders category. |
| 82 | Log line click | Jumps to the line's location, and scrubs to it in replay. | works | Unchanged. |
| 83 | Comms channel chips | Filter by channel. | works | Unchanged. |
| 84 | Comms compose and Send | Posts a message as the Blue commander. A Diplomatic message gets a reply after 2.5 s. | works (demo) | Unchanged. |
| 85 | Losses | Per-side losses and expenditure. | works | Unchanged. |

## Exceptions (planned)

These are disabled on the named source, with a tooltip that gives the reason. Everything else works on every source
that has the capability.

| # | Control | Sources | Reason |
|---|---|---|---|
| 10 | Friendly fire | Live, FakeKSP | Friendly fire in KSP is BDArmory physics. The host has no misfire capability. |
| 11 | Seat hand-over | Live, FakeKSP | The host's seat is informational in M0. There is no seat hand-over capability yet. |
| 71 | Fly in KSP | Live, FakeKSP | The host has no "make active vessel" capability (`ksp.vessel.set_active` is not in the catalog). |
| 74 | Launch ready aircraft | Live, FakeKSP | Deck launch needs the M8 launch adapter (24-ksp-parity row 31). |
| 76 | Radar Active / Passive | Live, FakeKSP | The C2 doctrine has no EMCON key, and BDA radar control is not exposed by the host. |
