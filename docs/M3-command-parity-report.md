# M3 command parity: report

This report checks each control in the mockup `design/mockups/command-screen-v1.html` against the desktop map app on
branch `m3-command-parity`. The inventory, with the app state before this work and the target wiring, is in
`M3-command-parity.md`. Row numbers are the same in both files.

## Summary

| | Count |
|---|---|
| Controls in the mockup | 85 |
| Working on the demo battle | 85 |
| Working on Live and FakeKSP | 80 |
| Exceptions (disabled with their reason on Live and FakeKSP only) | 5 (rows 10, 11, 71, 74, 76) |
| Control tests (`Control_<Name>_<Effect>`), all passing | 87: 85 rows plus 2 live-path tests |

Every order goes through one service, `CommandService`, as the c2.* contract (`c2.order.issue`, `c2.order.cancel`,
`c2.doctrine.set`, `c2.unit.release`, `c2.vip.set`, `c2.unit.status`):

- **Demo battle:** the real `Skizzy.C2` engine serves the calls in-process. It drives the battle through a BDArmory-level
  executor in `BattleSim`.
- **FakeKSP and Live:** the calls go through `SkizzyClient` to the host's C2 engine.

Refusals come back with the engine's reason. They go to Messages (category Orders) and to a toast, worded as in the
mockup. A source that takes no orders at all, such as a replay file, disables the order controls with the tooltip "This
source does not take orders."

## How the tests work

Each control has one headless Avalonia test in `tests/Skizzy.Map.App.Tests/ControlTests.*.cs`. Each test does what a
user does: it clicks, double-clicks, right-clicks, drags, scrolls the wheel, presses keys, or types into the real
controls in the real `MainWindow`. Nothing is called directly.

Each test makes two kinds of assertion:

- **Order issued.** For order controls, this is the c2.* call with its arguments, the engine's reply, and the mockup's
  log line or toast. For view and layout controls, which issue no order, it is the app state the control sets. Those
  tests also check that no order went out.
- **World effect.** This is what happened as a result. For example:
  - demo units moved, engaged, landed on the deck, held their station, or held SAM fire;
  - the plot drew or hid a layer, or its pixels changed;
  - a panel appeared or was resized;
  - on FakeKSP, the BDArmory-level control saw the call.

Test classes:

| Class | Rows |
|---|---|
| `TopBarControlTests` | 1–16 |
| `PlotControlTests` | 17–38 |
| `GestureControlTests` | 39–51 |
| `KeyControlTests` | 52–63 |
| `UnitPanelControlTests` | 64–79 |
| `BottomPanelControlTests` | 80–85 |
| `LiveControlTests` | Live path and exceptions |

## Top bar and panel chrome

| # | Control | Mockup behaviour | App behaviour | Test | Result |
|---|---|---|---|---|---|
| 1 | Pause / play | Toggles the sim clock. | Pauses (label ▶, clock frozen); a second click resumes. | `Control_PausePlay_TogglesTheSimClock` | pass |
| 2 | Speed segment | Sets the sim rate. | 4× advances about 4 s per wall second. | `Control_SpeedSegment_SetsTheSimRate` | pass |
| 3 | Restart | Restarts the scenario. | Back to T+0. The C2 engine, the orders and the selection are reset. | `Control_Restart_RestartsTheScenarioAndTheC2Engine` | pass |
| 4 | Stance pill | Display, with a tooltip. | "Blue ↔ Red · War", tooltip "Side stance". | `Control_StancePill_ShowsTheStance` | pass |
| 5 | UT clock | Shows the scenario UT. | Ticks with the sim. | `Control_UtClock_ShowsTheScenarioUt` | pass |
| 6 | Advance on/off | Toggles advance on contact, with the log line. | `c2.doctrine.set` side `advanceOnContact`, with the mockup lines. When off, no fighter advances past first identification. | `Control_Advance_SetsSideDoctrineAndAircraftHoldTheirStations` | pass |
| 7 | All attack | Two clicks within 3 s, then every unit attacks and the decks launch. | First click shows "Confirm all attack" and a toast; the confirmation lapses after 3 s. The second click sends `c2.order.issue all_attack`: units go to "Your order · attack nearest", the Kestrel deck launches, and its fighters hunt. | `Control_AllAttack_ConfirmsThenEveryUnitHuntsAndTheDecksLaunch` | pass |
| 8 | RTB all | Two clicks within 3 s, then every aircraft goes to the marshal stack and lands. | "Confirm RTB all" and the mockup toast, then `rtb` "All aircraft (N)". Aircraft land on Kestrel one every 5 s. | `Control_RtbAll_ConfirmsThenAircraftLandOnTheCarrier` | pass |
| 9 | Posture | Sets the posture, with the log line. | `c2.doctrine.set` side `posture`, with the mockup line. Defensive holds the alert fighters; Proactive launches Lance 1 at T+20. | `Control_Posture_SetsSideDoctrineAndTheCarrierHoldsItsAlertFighters` | pass |
| 10 | Friendly fire | Sets the misfire rate and logs it. | Demo: the sim rate becomes 0.01, with the mockup line; Off logs "Friendly-fire simulation off." | `Control_FriendlyFire_SetsTheMisfireRateAndLogs` | pass (exception on Live) |
| 11 | Seat hand-over | Toggles the seat and logs it. | Demo: the pill reads "BLUE SEAT · AGENT (CLAUDE CODE)" and back, with both mockup lines. | `Control_Seat_HandsTheSeatToTheAgentAndBack` | pass (exception on Live) |
| 12 | Panels toggles | Show or hide a panel. | Tracks, Unit and Log hide and come back. | `Control_PanelToggles_ShowAndHideThePanels` | pass |
| 13 | Reset layout | Restores the default panels. | New top-bar button. All panels return, with the toast "Default layout restored." | `Control_ResetLayout_RestoresTheDefaultPanels` | pass |
| 14 | Panel × close | Hides the panel. | The Dock close button hides Tracks, and the layout records it. | `Control_PanelClose_HidesThePanel` | pass |
| 15 | Splitter drag | Resizes the panel. | A 60 px drag widens Tracks by about 60 px. | `Control_SplitterDrag_ResizesThePanel` | pass |
| 16 | Splitter arrow keys | 20 px per key. | The splitter is focusable; each arrow key resizes by 20 px and goes no further (no pan, no other shortcut). | `Control_SplitterArrowKeys_ResizeBy20Px` | pass |

## Tracks panel, plot toolbar and overlays

| # | Control | Mockup behaviour | App behaviour | Test | Result |
|---|---|---|---|---|---|
| 17 | Group chips | Select the group, with a toast. | The 5-unit escort, and "All aircraft" (over 50, all air), each with "N units selected." | `Control_GroupChips_SelectTheGroupWithAToast` | pass |
| 18 | Filter chips | Filter the list, with counts. | Ships & subs shows only non-air rows, with a matching count; All restores the full list. | `Control_FilterChips_FilterTheListWithCounts` | pass |
| 19 | Track row click | Selects the track. | Becomes the single, primary selection; the unit panel shows it. | `Control_TrackRowClick_SelectsTheTrack` | pass |
| 20 | Track row double-click | Centres the plot. | The plot centres on the track. | `Control_TrackRowDoubleClick_CentresThePlotOnTheTrack` | pass |
| 21 | Symbols mode | Changes how symbols are sized. | The plot's size mode changes and the plot's pixels change. | `Control_SymbolsMode_ChangesHowSymbolsAreSized` | pass |
| 22 | Symbol scale | Scales all symbols. | The scale moves to about ×1.2 and the plot's pixels change. | `Control_SymbolScale_ScalesAllSymbols` | pass |
| 23 | Trails | Layer on or off. | The layer flag and the toggle flip and the plot's pixels change; toggled back, the picture returns. | `Control_TrailsToggle_ShowsAndHidesTrails` | pass |
| 24 | Altitude | Layer on or off. | As row 23. | `Control_AltitudeToggle_ShowsAndHidesAltitudeStalks` | pass |
| 25 | Target lines | Layer on or off. | As row 23. | `Control_TargetLinesToggle_ShowsAndHidesTargetLines` | pass |
| 26 | Rings | Layer on or off. | As row 23. | `Control_RingsToggle_ShowsAndHidesRangeRings` | pass |
| 27 | Labels | Layer on or off. | As row 23. | `Control_LabelsToggle_ShowsAndHidesLabels` | pass |
| 28 | Counts | Layer on or off. | As row 23. | `Control_CountsToggle_GroupsDronesAndMissilesIntoCounts` | pass |
| 29 | Zoom − | Zooms out. | The scale goes down by ×1.4. | `Control_ZoomOut_ZoomsOut` | pass |
| 30 | Zoom + | Zooms in. | The scale goes up by ×1.4. | `Control_ZoomIn_ZoomsIn` | pass |
| 31 | Fit | Fits all tracks. | All of more than 100 tracks are inside the plot. | `Control_Fit_FitsAllTracks` | pass |
| 32 | Banner: Resume | Resumes after a pause-on-event. | The banner goes and the clock runs again. | `Control_BannerResume_ResumesTheBattle` | pass |
| 33 | Banner: Don't pause again | Turns pause-on-event off, with the log line. | The mockup line; later events no longer pause. | `Control_BannerDontPauseAgain_KeepsRunningThroughEvents` | pass |
| 34 | Hover tooltip | Shows the track details. | Kestrel's name, bearing and range; it goes when the pointer leaves. | `Control_HoverTooltip_ShowsTheTrackDetails` | pass |
| 35 | Toast | A status line for 4.5 s. | Visible, then hidden after 4.5 s. | `Control_Toast_ShowsAStatusLineFor4_5Seconds` | pass |
| 36 | Legend | Symbol key. | The legend is shown with the symbol key. | `Control_Legend_ShowsTheSymbolKey` | pass |
| 37 | Replay scrub | Enters replay at that frame. | Replay at the scrubbed time; the plot shows the past position and the Live button appears. | `Control_ReplayScrub_EntersReplayAtThatFrame` | pass |
| 38 | Back to live | Leaves replay. | Leaves replay; the view time is the live time. | `Control_BackToLive_LeavesReplay` | pass |

## Plot gestures and order drawing

| # | Control | Mockup behaviour | App behaviour | Test | Result |
|---|---|---|---|---|---|
| 39 | Left click | Selects; a click on empty plot clears. | Same. | `Control_LeftClick_SelectsATrackAndEmptyPlotClears` | pass |
| 40 | Shift-click | Toggles the track in the selection. | Same. | `Control_ShiftClick_TogglesTheTrackInTheSelection` | pass |
| 41 | Shift-drag box | Box-selects, preferring own units. | Selects three or more units, all Blue. | `Control_ShiftDragBox_BoxSelectsPreferringOwnUnits` | pass |
| 42 | Drag | Pans. | Pans to the expected centre and does not change the selection. | `Control_Drag_PansThePlot` | pass |
| 43 | Wheel | Zooms at the cursor. | The point under the cursor stays put. | `Control_Wheel_ZoomsAtTheCursor` | pass |
| 44 | Double-click | Selects the whole group. | The whole escort, with "5 units selected." Fixed: the second release no longer undid it. | `Control_DoubleClick_SelectsTheWholeGroup` | pass |
| 45 | Right-click empty | Moves in formation, with the log line; a toast if nothing is selected. | With nothing selected, the toast and no call. Otherwise `move_to` (2 accepted) and "Hawk 1, Hawk 2: move to bearing BBB, R km from Kestrel." Both arrive within 8 km and spread out. | `Control_RightClickEmpty_MovesTheSelectionInFormation` | pass |
| 46 | Right-click hostile | Attacks it and the rest of its group. | `engage` on the target, with the mockup line. The shooters are spread over the target's group, and the BDA override is set. | `Control_RightClickHostile_AttacksItAndTheRestOfItsGroup` | pass |
| 47 | Right-click own unit | Protects it and marks it VIP. | `defend_unit` `protect`, with the mockup line. Thimble becomes VIP; the escorts stay within 40 km. | `Control_RightClickOwnUnit_ProtectsItAndMarksItVip` | pass |
| 48 | Right-drag | Draws a route the selection follows. | A live preview while dragging, a point every 14 px. Then `move_route`, with the mockup line, and the unit reaches the route's end. | `Control_RightDrag_DrawsARouteTheSelectionFollows` | pass |
| 49 | Racetrack drag | Sets the long leg (a click makes a 20 km leg east); ships refused. | A live preview, then `hold_pattern` `widthKm` 6, with the mockup line. Halyard is refused with the mockup line. The BDA unit flies the route; a click makes 20 km legs. | `Control_RacetrackDrag_SetsTheHoldingPatternAndRefusesShips` | pass |
| 50 | Send to… then click | The next click moves, or attacks a hostile. | `move_to` (arrives within 8 km), then `engage`. The selection is kept. | `Control_SendTo_ThenClickMovesOrAttacks` | pass |
| 51 | Order drawing | Move lines, target lines, routes, racetrack ovals, RTB marshal stack. | The plot scene gets each order type; turning the orders on changes the pixels. | `Control_OrderDrawing_FeedsThePlot` | pass |

## Keyboard

| # | Control | Mockup behaviour | App behaviour | Test | Result |
|---|---|---|---|---|---|
| 52 | Space | Pause / play. | Same. | `Control_Space_PausesAndPlays` | pass |
| 53 | F | Fit all. | F fits all; Shift+F fits the selection. | `Control_F_FitsAllAndShiftFFitsTheSelection` | pass |
| 54 | Escape | Cancels a pending mode, then clears the selection. | Cancels a racetrack (including one mid-drag, which then orders nothing) and Send to…, then clears. | `Control_Escape_CancelsAPendingModeThenClearsTheSelection` | pass |
| 55 | V | Toggles VIP; a toast if nothing is selected. | The toast, then `c2.vip.set` both ways with the mockup lines. The demo unit and the track flags follow. | `Control_V_TogglesVipOnTheSelection` | pass |
| 56 | A | Attack nearest. | `attack_nearest` for both units, "Not armed for this: Watch 1." Hawk 1 picks a target. | `Control_A_AttackNearestForTheSelection` | pass |
| 57 | Shift+A | All attack after two presses. | The toast, then `all_attack`. More than 20 units attack. | `Control_ShiftA_AllAttackAfterTwoPresses` | pass |
| 58 | R | RTB; carriers refused. | `rtb` with the mockup line; "RTB refused: Kestrel is a carrier (a base)." Hawk 2 lands on Kestrel's deck. | `Control_R_RtbForTheSelectionRefusesCarriers` | pass |
| 59 | Shift+R | RTB all after two presses. | The toast, then `rtb` "All aircraft (N)". More than 50 units RTB. | `Control_ShiftR_RtbAllAfterTwoPresses` | pass |
| 60 | T | Toggles racetrack mode, with a toast. | Pending racetrack, the mockup toast, and the Racetrack… button pressed; T again cancels. | `Control_T_TogglesRacetrackMode` | pass |
| 61 | [ / ] | Shrink or enlarge the selected symbols. | ×1.25 and back; the plot symbol follows. | `Control_Brackets_ShrinkAndEnlargeTheSelectedSymbols` | pass |
| 62 | Alt+digit | Puts the selection on a number key. | "Group 3", with the mockup toast and log; the chip shows the key badge. | `Control_AltDigit_PutsTheSelectionOnANumberKey` | pass |
| 63 | Digit | Recalls the group. | Selects the group, "2 units selected." | `Control_Digit_RecallsTheGroupOnThatKey` | pass |

## Unit panel

| # | Control | Mockup behaviour | App behaviour | Test | Result |
|---|---|---|---|---|---|
| 64 | Header and values | Symbol, class, control state, values, magazines, fuel. | Same, including "YOUR ORDER · MOVE" from engine orders. | `Control_UnitHeader_ShowsTheStateAndValues` | pass |
| 65 | Make group | Groups the selection, with a log line and a toast. | "Strike A", with both mockup texts. | `Control_MakeGroup_GroupsTheSelection` | pass |
| 66 | Take control / Return to mission | Manual, then back to the mission. | `move_to` labelled take_control; "MANUAL · YOU", and the unit holds within 6 km. Return sends `c2.unit.release`, with both mockup lines, and the unit is back under its mission. | `Control_TakeControl_HoldsTheUnitAndReturnToMissionReleasesIt` | pass |
| 67 | Attack nearest | Aircraft hunt, carriers launch, unarmed units refused. | `attack_nearest`: "carriers launched", "Not armed for this: Watch 1." The deck empties and Hawk 1 hunts. | `Control_AttackNearest_HuntsLaunchesTheDeckAndRefusesUnarmedUnits` | pass |
| 68 | RTB & land | Aircraft land; carriers refused. | `rtb`, the refusal in the log and as a toast. Hawk 2 lands. | `Control_RtbAndLand_SendsAircraftHomeAndRefusesTheCarrier` | pass |
| 69 | Send to… | Toggles the pending move, with a toast. | Pressed while pending, with the mockup toast. | `Control_SendToButton_TogglesThePendingMove` | pass |
| 70 | Mark VIP | Toggles VIP. | `c2.vip.set`; "VIP ✓" and back; the demo unit follows. | `Control_MarkVip_TogglesVip` | pass |
| 71 | Fly in KSP | Explanatory toast. | Demo: the mockup toast. | `Control_FlyInKsp_ExplainsTheRealBuild` | pass (exception on Live) |
| 72 | Center | Centres the plot on the unit. | Same. | `Control_Center_CentresThePlotOnTheUnit` | pass |
| 73 | Racetrack… | Toggles the pending racetrack, with a toast. | Pressed while pending, with the mockup toast. | `Control_RacetrackButton_TogglesThePendingRacetrack` | pass |
| 74 | Launch ready aircraft | Launches the deck, with the log line. | Demo: `demo.deck.launch` (6 launched), with the mockup line. The deck empties, the aircraft leave the carrier, and the button disables with nothing ready. | `Control_LaunchReadyAircraft_LaunchesTheDeck` | pass (exception on Live) |
| 75 | Weapons control | Sets it for the selection, with the log line. | `c2.doctrine.set` unit `weaponsControl`, with the mockup lines. The demo doctrine and the segment follow. | `Control_WeaponsControl_SetsUnitDoctrine` | pass |
| 76 | Radar | Sets the ship's EMCON; Passive stops SAM fire. | Demo: `demo.unit.emcon`, "Halyard: radar passive." Halyard holds its SAMs while other destroyers fire. | `Control_Radar_SetsShipEmconAndPassiveStopsSamFire` | pass (exception on Live) |
| 77 | On contact | Sets it for the selection, with the log line. | `c2.doctrine.set` unit `onContact`, both ways, with the mockup lines. | `Control_OnContact_SetsUnitDoctrine` | pass |
| 78 | Symbol size | Smaller / Larger / Reset. | ×1.25, ×0.8, reset; the plot follows. | `Control_SymbolSize_SmallerLargerReset` | pass |
| 79 | Flight deck readout | "N aircraft on deck, M ready…". | Same, including "Deck empty" and the count after a landing. | `Control_FlightDeckReadout_ShowsTheDeck` | pass |

## Messages, Comms, Losses

| # | Control | Mockup behaviour | App behaviour | Test | Result |
|---|---|---|---|---|---|
| 80 | Bottom tabs | Switch the panel. | Same. | `Control_BottomTabs_SwitchThePanel` | pass |
| 81 | Category chips | Toggle a category, with counts. | Orders count shown; the category hides and comes back. | `Control_MessageCategoryChips_ToggleACategoryWithCounts` | pass |
| 82 | Log line click | Jumps to the line, and scrubs to it in replay. | Same. | `Control_LogLineClick_JumpsToTheLineAndScrubsInReplay` | pass |
| 83 | Comms channel chips | Filter by channel. | Team shows only Team lines; All restores. | `Control_CommsChannelChips_FilterByChannel` | pass |
| 84 | Comms Send | Posts as the Blue commander; Diplomatic gets a reply after 2.5 s. | Same; the reply is not there at 2.1 s and is there at 2.7 s. | `Control_CommsSend_PostsAsTheBlueCommanderAndDiplomaticGetsAReply` | pass |
| 85 | Losses | Per-side losses and expenditure. | Same, matching the battle's tallies. | `Control_Losses_ShowsPerSideLossesAndExpenditure` | pass |

## Live path (FakeKSP)

| Test | What it checks | Result |
|---|---|---|
| `Control_LivePath_C2CallsArriveAtTheHost` | Uses the in-process Skizzy host over the M0 vessel stream, with the C2 engine attached as the BDArmory binding does (FakeBda units keyed by vessel pid). Each user action is followed through `SkizzyClient` to the host. Right-click sends `c2.unit.status` then `move_to`, and BDA sees `F6 set <pid>`. V sends `c2.vip.set`. Weapons Hold sends `c2.doctrine.set`, and BDA sees `F3 <pid>`. Right-click on the Red frigate sends `engage`, and BDA sees `F1 set <f14> <ffg>`. R sends `rtb`, and the engine's verdict is logged. | pass |
| `Control_LiveExceptions_AreDisabledWithTheirReasons` | The five exceptions are disabled with their reason tooltips; the order buttons stay enabled. Without the BDArmory binding, an order is refused with the host's reason ("Move refused: the C2 engine is not attached…"). | pass |

## Exceptions

These controls are disabled only on Live and FakeKSP, with a tooltip that gives the reason. They work on the demo
(rows 10, 11, 71, 74, 76 above).

| # | Control | Reason (the tooltip) |
|---|---|---|
| 10 | Friendly fire | Friendly fire in KSP is BDArmory physics. The host has no misfire capability. |
| 11 | Seat hand-over | The host's seat is informational in M0. There is no seat hand-over capability yet. |
| 71 | Fly in KSP | The host has no "make active vessel" capability (ksp.vessel.set_active is not in the catalog). |
| 74 | Launch ready aircraft | Deck launch needs the M8 launch adapter (24-ksp-parity row 31). |
| 76 | Radar Active / Passive | The C2 doctrine has no EMCON key, and BDA radar control is not exposed by the host. |

## Performance

`CommandParityPerfTests.OrdersOn2000TracksKeepTheFrameBudgetAndReachThePlotWithoutAllocating` measures 2,000 synthetic
tracks at 1920×1080. Each figure is the best batch median of data, sync, panels and plot draw.

| Load | Frame | Order layer | Gate |
|---|---|---|---|
| No orders | 12.2 ms (82 fps) | 0 | — |
| 150 drawn orders (the demo's all attack draws about 140) | 14.7 ms (68 fps) | 1.6 ms | < 16.7 ms: pass |
| 400 drawn orders | 19.5 ms (51 fps) | 3.8 ms | reported only |
| 2,000 drawn orders (every track) | 38.0 ms (26 fps) | 25.5 ms | reported only |

The orders reach the plot scene without allocating. The test measures 30 warm frames with 150 orders and finds **0
bytes**.

Two changes got there:

- **Order lines are drawn as hairlines.** Move and target lines, route legs, rings and racetracks now use hairlines
  with the alpha scaled to the stroke width, which Skia draws on its fast path. Shared routes are drawn as one batched
  path. On the demo's all attack, the order layer went from 6.4 ms to 1.4 ms.
- **The per-frame allocation is gone.** `SceneSync.Sync` boxed the track list's enumerator on every frame (40 bytes).
  This was there before this work. The track loop is now indexed.

The gate is load-resistant in the same way as the other perf gates:

- it runs alone, in the "perf" collection;
- it gates on the best batch median;
- it repeats an over-budget measurement once.

## Fixes found along the way

| Area | Fix |
|---|---|
| Plot double-click | The second release re-selected the single track and undid the group selection. |
| Keys in select boxes and sliders | Keys pressed in a ComboBox or Slider (such as the friendly-fire box) no longer reach the global shortcuts. |
| Toast | Shown for 4.5 s as in the mockup. A toast raised during a re-layout (Reset layout) is replayed in the new plot. |
| C2 engine | `ExplainDoctrine` and `GetUnitView` refresh the world for a unit the engine has not read yet. Before, the first status call on a live vessel answered "unknown unit". |
| Renderer allocation | The grid and the scale bar each allocated 72 bytes per frame in Debug builds: the zoom steps were a `ReadOnlySpan<int>` property, which unoptimised code builds through a field handle. They are now a static array. Before this fix, six renderer tests failed their zero-allocation check on `main` too (for example, `Steady_state_frames_do_not_allocate` measured 144 B per frame); all of them pass now. |

## Full suite

Run with `AVALONIA_TELEMETRY_OPTOUT=1`: `dotnet test Skizzy.slnx` (host, C2, CLI, protocol, KSP) and
`dotnet test Skizzy.Map.slnx` (map app and renderer).

| Project | Passed | Failed | Total |
|---|---|---|---|
| Skizzy.C2.Tests | 90 | 0 | 90 |
| Skizzy.Host.Tests | 68 | 0 | 68 |
| Skizzy.Protocol.Tests | 64 | 0 | 64 |
| Skizzy.Ksp.Tests | 44 | 0 | 44 |
| Skizzy.Cli.Tests | 23 | 0 | 23 |
| Skizzy.Map.Render.Tests | 87 | 1 | 88 |
| Skizzy.Map.App.Tests (87 control tests and the parity perf gate among them) | 143 | 2 | 145 |
| **Total** | **519** | **3** | **522** |

The 3 failures are the time-budget perf gates. They are load-sensitive, and they fail the same way without this work:

| Test | Measured | Budget |
|---|---|---|
| `Skizzy.Map.Render.Tests.PerfTests.Renders_2000_tracks_at_1080p_under_16ms` | 16.9 ms | 16 ms |
| `Skizzy.Map.App.Tests.PerfTests.WholeAppWith5000TracksReportsItsFrameRate` | 34.5 ms | 16.7 ms |
| `Skizzy.Map.App.Tests.PerfTests.RenderCoreDraws5000SymbolsInsideTheFrameBudget` | 53.5 ms | 16.7 ms |

These runs were on a shared machine at about 65 % CPU load from other processes.

- **The 5,000-track tests** fail on `main` as well.
- **The 2,000-track renderer scene** has no orders, so none of the new order-drawing code runs in it. With the
  original renderer, under the same load, it measured 19.4 ms and 17.4 ms.

All allocation assertions pass (0 B per frame), including the renderer's and the parity gate's.
