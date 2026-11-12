# M2 map missiles: per-control action report

This report covers every map control added in the M2 missile slice. For each one it gives the control, what it does,
the test that drives it, and the result. The app tests click the real control in a headless window (Avalonia
headless, frames stepped by hand). The render tests drive the renderer directly.

Runs: 2026-11-12, Debug build, Windows 11.
- `tests/Skizzy.Map.App.Tests/bin/Debug/net10.0/Skizzy.Map.App.Tests.exe -class "*MissileControlTests"`: 12/12
  passed, three runs in a row (about 40 s each).
- `dotnet test tests/Skizzy.Map.Render.Tests`: 93/93 passed, including the 5 `MissilePictureTests`.

| # | Control / behaviour | Action | Expected | Test | Result |
|---|---|---|---|---|---|
| 1 | Plot toolbar toggle **Missiles** | Click it, then click it again | `PlotOptions.Missiles` flips. The toggle shows the state. More than 150 plot pixels change. A second click restores the plot. | `MissileControlTests.Control_MissilesToggle_ShowsAndHidesMissiles` | Pass |
| 2 | Plot toolbar toggle **Missile trails** | Click it, then click it again | `PlotOptions.MissileTrails` flips. More than 150 pixels change, and the plot comes back. `Trails` stays on. | `Control_MissileTrailsToggle_ShowsAndHidesMissileTrails` | Pass |
| 3 | Both toggles in the saved layout | Turn both off, then save and apply the map config | `MapConfig.Missiles` and `MissileTrails` round-trip. | `Control_MissileToggles_PersistInTheLayoutConfig` | Pass |
| 4 | Inbound ring | 10 Red missiles guiding on a Blue ship, viewed in Blue fog, Red fog, and with Missiles off | 10 rings in Blue fog, 0 in Red fog, and 0 with Missiles off (10 counted as hidden). | `Control_InboundRing_MarksMissilesInboundToAFriendly` | Pass |
| 5 | Readable when zoomed out | Salvo at 1.5 px/km (the view spans about 700 km) | All 10 are drawn as headed arrowheads of 6 px or more. | `Control_MissilesStayArrowheads_ZoomedOutPast30Km` | Pass |
| 6 | Layers panel toggles **Missiles** and **Missile trails** | Click the Layers tab, then click each toggle | The active map's option flips and the plot changes. The toolbar toggle follows, and the other way round. | `Control_LayersMissileToggles_FlipTheActiveMapAndSyncTheToolbar` | Pass |
| 7 | **Engagements** tab (bottom dock) | Click the tab, then click Messages | The panel shows, with the note "No missiles fired yet." The note hides once data arrives. Clicking Messages hides the panel. | `Control_EngagementsTab_ShowsThePanelWithItsNote` | Pass |
| 8 | Palette action **Show / hide Engagements** | Ctrl+K, then run the action twice | The panel hides, then comes back. | `Control_EngagementsPaletteAction_ShowsAndHidesThePanel` | Pass |
| 9 | Totals table | Load a 7-entry book with two teams | Every column header shows. Team A spent 3,400, 1,250 lost to intercepts, 1,250 hit value. B spent 700. A is coloured Blue, B Red. The list is newest first. | `Control_EngagementTotals_ShowPerTeamCountsAndFunds` | Pass |
| 10 | Kind chips (Fired, Hits, Intercepted, Missed, Destroyed, Kills) | Click Fired off, then on, then add an entry | The counts are right (Fired 4, Intercepted 1). Fired lines hide and come back. A new line goes on top and the count goes to 5. | `Control_EngagementChips_ToggleAKindWithCounts` | Pass |
| 11 | Log row click | Click the "hit" line of a missile | The map goes to the missile's position at 8 px/km or more, and the missile is selected. | `Control_EngagementRow_Click_PansTheMapToTheMissile` | Pass |
| 12 | Live path, end to end | FakeKSP host, then attach FakeBda (teams A and B), fire a salvo of 3 plus 1 SM-2, then end them as intercepted, hit and missed | See below. | `Live_TacticalStream_DrawsMissilesAndFillsTheEngagementLog` | Pass |

Row 12 checks these steps in order:
1. The map starts on the M0 stream with the "needs the tactical stream" note.
2. Within 5 s of the binding attaching, it moves up to `source.tactical`.
3. Team A is drawn Blue and team B Red.
4. The 3 missiles target Kirov as AntiShipMissile; the SM-2 is a SurfaceToAirMissile.
5. Unit details show the Weapon and Cost ("1,250 funds" for the 2-part AGM-84).
6. Red fog shows 3 or more inbound rings.
7. The fired, intercepted, hit and missed lines arrive, and the missiles die.
8. Impact, Intercept and Miss plot events are added.
9. The Messages log gets Intercept and Damage lines, and no "left the picture" line for a missile.
10. Team A totals: fired 3, hits 1, misses 1, intercepted 1; spent 3,400, intercepted value 1,250, hit value 1,250.
11. Team B has 1 intercept made.
12. The panel lists the lines and shows 3,400. Losses shows Funds spent 3,400.

Render-level checks (`tests/Skizzy.Map.Render.Tests/MissilePictureTests.cs`):

| Check | Result |
|---|---|
| At 6.4 and 1 px/km (300 km and 1,920 km across 1,920 px), all 40 missiles in a salvo spaced 300 m apart are Missile-tier arrowheads of 6 px or more. With `MissileMinPx = 0` (the mockup rule) they become dots. | Pass |
| The Missiles filter hides all 12 missiles (`MissilesHidden = 12`, no inbound rings), keeps the ship, and restores the frame pixel for pixel. | Pass |
| Missile trails off changes more than 200 pixels and leaves the ship's trail band untouched. `Trails` off still removes the ship trail. | Pass |
| The inbound ring is drawn in the critical red at the ring radius, with `InboundMarks = 1`. Without the flag there is no ring. | Pass |
| Missiles are coloured by affiliation (hostile red, friend blue). | Pass |
| The existing steady-state test still shows 0 B allocated per frame with the new ring path. | Pass |

Not covered here: the in-game look against real BDArmory missiles. That is `docs/M2-checklist.md` G14–G20.
