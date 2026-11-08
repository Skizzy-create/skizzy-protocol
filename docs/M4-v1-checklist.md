# M4 v1 in-game acceptance checklist: C2 orders and doctrine

Scope: `24-ksp-parity.md` rows 3–10, 12–14, 17 and 21–28, engine side (`src/Skizzy.C2`). Each row below lists setup, steps, what to watch in game and in Skizzy, and the pass criterion. The FakeBda scenario tests in `tests/Skizzy.C2.Tests` cover the same rows headless. This checklist is for the real game.

## 0. Prerequisites

1. **BDArmory with the Overdrive hooks.** Branch `overdrive-hooks` at commit `9fac1f3bc` or later. `host.status` (or the runtime probe) reports F1, F2, F3, F4, F5, F6, F6b, F9 and F10 as present. If a hook is missing, orders that need it are refused with `HOOK_MISSING`. That is correct behaviour, not a failure.
2. **The KSP binding.** It implements `IC2World`, `IBdaControl` and `IHookEvents` (`src/Skizzy.C2/Hooks.cs`) over BDArmory. It subscribes to the F1, F6, F6b, F9 and F10 static events and forwards them to `C2Engine`.
3. **M0 wiring.**
   - `C2Engine.Tick()` runs from the C2 slot of the host's FixedUpdate at 5 Hz.
   - The protocol methods map onto the engine API:

     | Method | Engine call |
     |---|---|
     | `c2.order.issue` | `Issue` |
     | `c2.order.cancel` | `Cancel` |
     | `c2.doctrine.set` | `SetDoctrine` |
     | `c2.doctrine.unset` | `UnsetDoctrine` |
     | `c2.doctrine.effective` | `ExplainDoctrine` |
     | `c2.unit.status` | `GetUnitView` |
     | `c2.unit.release`* | `Release` |
     | `c2.vip.set`* | `SetVip` |
     | `c2.side.managed`* | `SetSideManaged` (the "managed" steps below) |

     \* Proposed name. The design docs do not name these yet; M0 owns the final names.
   - `C2Event`s are published as `order.*` / `log` events.
   - `FirstLaunch` honours pause-on-event.
4. **Test scene.** KSP sandbox, Kerbin, ocean east of KSC, BDA teams **Blue** and **Red**. Spawn:

   | Side | Units |
   |---|---|
   | Blue | carrier **Kestrel** (weapon manager, SAMs, heading 090)<br>destroyers **Halberd** and **Pike** (VLS SAMs)<br>fighters **Cobra 1–4** (radar, 4 AAMs, guns)<br>drones **Wasp 1–2** (small radar, 2 AAMs)<br>AEW **Eye** (long-range radar, unarmed) |
   | Red | fighters **Bandit 1–4**<br>bombers **Raider 1–2** (anti-ship missiles) |

   Set the Blue aircraft's home to Kestrel. Use pilot AI on aircraft and surface AI on ships, with guard mode on.
5. **Tools.** `skizzy call <method> '<json>'` for orders. `skizzy call c2.unit.status '{"unit":"v:<id>"}'` shows:
   - task, order statuses, hold
   - fire authority, whether a filter is installed and what it allows
   - override target and stickiness, route type and points
   - yield and leash, divert, self-defence, advance and VIP threat
   - secondary targets

   The BDA weapon manager window shows the live target and guard state.

Every step that issues an order must produce an `order.accepted` or `order.refused` report naming each unit. Every accepted order must end with `achieved`, `failed`, `cancelled` or `superseded` and a reason (row 17). Check this on every row.

## Row 3: move, attack, drawn routes

1. `c2.order.issue {type:"move_to", side:"Blue", units:[Cobra 1, Cobra 2], point:{lat,lon}, altitudeM:3000}` with a point 40 km east.
   - **Expect** each fighter to get a one-point BDA waypoint route (F6), each offset by about 2.2 km (formation), with yield to combat on.
   - On arrival (within 5 km): `order.achieved` with reason `arrived` and the text "on station, orbiting the ordered point". Then an 8-point orbit (laps 1000) around the point. Status `hold: point`.
2. `move_route` with 3–4 points.
   - **Expect** the waypoints fed in order, then `waypoint k of N` progress reports, then `achieved/arrived` after the last point.
3. During the route, `engage` a Red bomber.
   - **Expect** the route reported `suspended`, BDA waypoints cleared, and a sticky F1 on the bomber.
   - After the kill: `achieved/targets_destroyed`, then `move_route resumed`. The route is re-fed **from the next unreached waypoint**, not from the start.
4. `move_to` for Halberd.
   - **Expect** a stock `CommandFlyTo` (ships use fly-to), arrival within 0.6 km, then `hold: point`.
   - *Spike to verify:* the surface AI stops or holds near the point after `CommandFlyTo`.

**Pass:** all reports appear, aircraft fly the fed points, and the route resumes at the right waypoint.

## Row 4: attack spreads across the group and retargets after a kill

1. Give Raider 1 and Raider 2 the same BDA group (or keep them within 15 km of each other). `engage {target: Raider 1, units:[Cobra 1, Cobra 2]}`.
   - **Expect** the summary "attack Raider 1 and the rest of its group (2 targets)". The two fighters hold **different** sticky overrides.
2. Shoot down one raider.
   - **Expect** "re-assigned to Raider 2 (1 left)" within one tick. Both fighters' overrides are now Raider 2.
3. Shoot down the second.
   - **Expect** `achieved/targets_destroyed`. Overrides cleared.

**Pass:** spread on issue, re-assign on kill, completion report.

## Row 5: stay on task

1. Doctrine defaults (`weaponsControl: free`, `onContact: task`). Send Cobra 1 on `move_to` 80 km east, past a Red bomber crossing 15 km off its track.
   - **Expect** fire authority Tight (F3) and a filter installed (F4) that does not admit the bomber.
   - BDA guard ignores the bomber: no lock, no launch, and the waypoint does not yield.
2. `engage` a far target while another bomber passes close.
   - **Expect** the filter admits only the engage targets (plus close fighters, see row 23). No shots at the crosser.

**Pass:** no distraction by crossing non-fighter hostiles during move_to, move_route or engage.

## Row 6: engage on contact

1. `c2.doctrine.set {level:"unit", target: Cobra 1, values:{onContact:"engage"}}`, then `move_to` far east with a Red bomber ahead.
   - **Expect** fire authority Free and no filter.
2. When the bomber enters Cobra 1's **own** radar (about 45 km; data-link contacts do not count):
   - **Expect** a `Diversion` report ("own radar contact … diverting to engage") and a sticky F1 on it. The move order stays executing and the waypoint yields (F6b).
3. Kill the bomber, or let it leave Cobra 1's own radar for 5 s.
   - **Expect** `BackOnTask` ("back on move_to"). Override cleared; the move continues.
4. Repeat during an `engage` order against a far ship.
   - **Expect** the override re-pointed to the pop-up contact, then **restored to the original target** after it dies.

**Pass:** diversion only to own-radar contacts, and the order restored afterwards.

## Row 7: stealth contact seen only by a drone's own radar

1. Fly Wasp 1 toward a low-RCS Red fighter, with no other Blue sensor in range.
   - **Expect** the report "Own-sensor contact: Wasp 1's own radar holds … No other unit holds it." (once per track).
   - `Picture(Blue).IsOwnSensorOnly` is true (the map shows "own-sensor only").
2. Bring Eye into range.
   - **Expect** the flag to clear (a surveillance platform now holds it).

**Pass:** the flag is set and cleared correctly, and the report fires once.

## Row 8: weapons control per aircraft

1. Set the three settings at unit level: Cobra 1 `free`, Cobra 2 `tight`, Cobra 3 `hold`. Put a Red bomber 15 km away.
   - **Expect:**

     | Unit | F3 | Filter | Fires at the bomber |
     |---|---|---|---|
     | Cobra 1 | Free | none | yes |
     | Cobra 2 | Tight | installed; does not admit the bomber | no (not assigned, not a close fighter) |
     | Cobra 3 | Hold | none | no; guard stays on, so it scans and locks |

2. `engage` the bomber with Cobra 3 (Hold).
   - **Expect** the explicit order to lift Cobra 3 to Tight for its own targets. It fires at the bomber only.
3. Set a side lock, `c2.doctrine.set {level:"side", target:"Blue", values:{weaponsControl:"tight"}, lock:["weaponsControl"]}`, then try to set a unit to `free`.
   - **Expect** the error `locked_by:side`. `c2.doctrine.effective` shows `from: side:Blue, locked: true`.

**Pass:** F3, F4 and guard state as in the table, explicit engage authorises its targets, locks hold.

## Rows 9 and 21: racetrack patrol that fights

1. `hold_pattern {units:[Cobra 1, Cobra 2], a, b}` with a 20 km leg.
   - **Expect** the summary "racetrack holding pattern, 20 km legs, 6 km wide …, stacked by altitude", and a 44-point looping route at 4500 m and 4800 m.
   - F6b settings: yield on, leash 55 km. The progress report "on the racetrack at 4500 m" appears.
2. Include Halberd in the order.
   - **Expect** a per-unit refusal `NOT_APPLICABLE` ("ships cannot fly a holding pattern").
3. Fly a Red bomber within guard range of the patrol.
   - **Expect** an `OnPatrolEngage` event and the report "leaving the patrol to engage …", then the fight.
   - After the kill: "resuming the patrol". BDA resumes **at the same waypoint**; Skizzy does not re-feed the route.
4. Lure a fighter away with a fast target running out.
   - **Expect** a break-off at 55 km from the anchor ("leash reached, breaking off and resuming the patrol"). That target is not re-engaged for 30 s.
5. Set Cobra 1 to weapons Hold.
   - **Expect** yield off. It stays on the pattern and never leaves.
6. **Row 21:** as the scenario director (`role: director`, `side: "Red"`), issue `hold_pattern` for Red fighters.
   - **Expect** the same behaviour for Red.

**Pass:** the patrol fights and resumes, the leash and cooldown hold, Hold stays on the pattern.

## Row 10: RTB, RTB all, refusals

1. With Cobra 1 and 2 40 km out, `rtb {units:[Cobra 1, Cobra 2]}`.
   - **Expect** the summary "RTB to Kestrel. 2 aircraft to the marshal stack 8 km astern; landing waits for the recover adapter."
   - Each fighter gets a route to the point 8 km astern of Kestrel (re-fed when the carrier has moved 2 km). Fire authority Tight (`rtbWeapons`).
2. On arrival:
   - **Expect** `achieved/on_station` with the text "at the marshal point … stack position N at 1200 / 1500 m. Landing waits for the recover adapter."
   - An orbit of radius 3.5 km at the stack altitude. Status `hold: marshal`.
3. Refusals in one call: `rtb {units:[Kestrel, Cobra 3]}`.
   - **Expect** Kestrel refused `NOT_APPLICABLE` ("is a carrier (a base)") and Cobra 3 accepted. One combined refusal report.
   - With no carrier or airfield alive: `NO_BASE`.
4. **RTB all:** `rtb {side:"Blue", filter:{domain:"air"}}`.
   - **Expect** every Blue aircraft accepted, ships untouched.
   - The same call as a delegate is refused `NOT_PERMITTED`.
5. Sink Kestrel while aircraft are inbound, with a second carrier alive.
   - **Expect** "Kestrel is lost; diverting to …". With no base left: `failed/base_lost`.
6. `rtb` for Halberd.
   - **Expect** a fly-to back to its home station, then `achieved` ("back on station") and `hold: home`.
7. **M0 item:** the in-game toolbar buttons "RTB selected" and "RTB all" issue the same orders without any client connected.

**Pass:** marshal geometry and stack, per-unit refusals, side scope and permission, base loss handled.

## Row 12: fighters and drones engage missiles when no aircraft are in view

1. Mark the Blue side managed. With no hostile aircraft within 75 km of a fighter:
   - **Expect** its missile-engagement setting on.
2. Bring a Red aircraft within 75 km.
   - **Expect** the setting off within about 1 s, and back on when the aircraft leaves.
3. Set `airEngageMissiles: never` or `always` on one unit.
   - **Expect** the setting fixed accordingly.

**Pass:** the toggle follows the doctrine.

## Row 13: VIPs and protect orders

1. `defend_unit {protect: Kestrel, units:[Cobra 1, Halberd]}`.
   - **Expect** Kestrel flagged VIP. Cobra 1 orbits Kestrel (8 points, about 7 km plus a per-unit offset, re-fed as Kestrel moves). Fire authority Tight, with a filter limited to hostiles within 40 km of Kestrel. Halberd takes a station near Kestrel.
2. A raider comes within 40 km of Kestrel.
   - **Expect** "engaging …, a threat to Kestrel" and a sticky F1. A raider 70 km out is not admitted.
3. Sink Kestrel.
   - **Expect** `failed/protected_lost` for both units.
4. **Side-level flag:** `c2.vip.set {unit: Eye}` on a managed side with idle fighters.
   - **Expect** threats within 40 km of Eye drawing the nearest idle fighters (fighters reach 120 km, drones 60 km), spread one per threat ("VIP priority, engaging …").

**Pass:** orbit, filter radius, WTA priority, failure report.

## Row 14: ship SAM priority for VIP threats

1. Kestrel is VIP. Raiders approach and fire anti-ship missiles at Kestrel.
   - **Expect** Halberd's and Pike's F2 secondary targets listing, in order: the missiles aimed at Kestrel, then raiders inside 40 km of Kestrel by distance. The top threat gets a non-sticky F1 once it is inside SAM range.
   - The ships engage those before other targets.

**Pass:** the secondary list order, and ships engaging VIP threats first.

## Row 17: orders never silently dropped

Run in one call: `move_to` with a destroyed unit, a Red unit, an unknown id, an airfield, and an aircraft with its AI off.

- **Expect** one accepted unit and one combined refusal naming each other unit with `UNIT_DESTROYED`, `NOT_OWN_SIDE`, `UNKNOWN_UNIT`, `NOT_APPLICABLE` and `AI_INACTIVE`.
- Switch a moving aircraft's AI off mid-order. **Expect** `failed/ai_inactive` after 3 retries.
- Supersede, cancel and destroy one order each. **Expect** an end report for each.

**Pass:** every unit and every order is accounted for in the reports.

## Row 22: escorts screen ahead of the package

1. `escort {escortOf: Raider-like Blue bomber, units:[Cobra 3, Cobra 4], slot:"ahead"}` (default 14 km).
   - **Expect** "screening 14 km ahead". Each escort flies a one-point route to its slot, led by the leader's velocity and re-fed as the package moves. Leash 30 km; guard on (Free).
2. Red interceptors come into range.
   - **Expect** the escorts to engage, then return to the slot ("in its escort slot ahead of …").
3. Lose the leader.
   - **Expect** `failed/escortee_lost`.

**Pass:** the escorts keep station ahead, fight, and return.

## Row 23: self-defence first

1. Cobra 1 on any order, with a Red fighter closing.
   - **Expect** at ≤ 25 km (acquisition runs at 1 Hz): "self-defence against Bandit … ; move_to paused", and an F1 on the threat for 15 s.
   - The F1 is sticky by default (`selfDefenceSticky`), so the AI turns into a threat still outside its missile range. It is re-asserted while the threat stands.
   - Under Tight, the filter admits the threat.
2. The threat dies, or opens beyond 25 km.
   - **Expect** "back on move_to", F1 cleared, order continuing.
3. A unit on weapons Hold:
   - **Expect** no self-defence.

**Pass:** the interruption is logged, ahead of the order, and the order is restored.

## Row 24: own-radar classification by unit type

1. Blue managed. A Red fighter closes on Cobra 1 head-on.
   - **Expect** the track in view earlier (data link or radar) but identified only inside 45 km of Cobra 1's own radar. Report: "First hostile identified: Bandit … by Cobra 1's own radar at N km" (once per side).
2. Check the other ranges: drone 25 km, AEW 175 km, ships 150 km.
   - **Expect** a data-link-only track never identified.

**Pass:** identification follows class ranges; data link alone never identifies.

## Row 25: all attack

1. `c2.order.issue {type:"all_attack", side:"Blue"}` as the seat.
   - **Expect** every armed unit to get `attack_nearest`. Eye is refused "not armed for this". Aircraft weapons Free; ships weapons Free with SAM range raised to the launcher maximum (`engageRangeMax` → `maxStaticLaunchRange`; check in the part's weapon settings). The ships' movement orders continue.
   - The same call as a delegate is refused `NOT_PERMITTED`.
2. `c2.order.cancel` on it.
   - **Expect** the SAM ranges restored to their previous values and the doctrine weapons control back.

**Pass:** all units engage, ship range goes up, then is restored.

## Row 26: attack nearest (hunt)

1. Eye holds a Red fighter pair. `attack_nearest {units:[Cobra 1, Cobra 2]}`.
   - **Expect** each fighter hunting a **different** identified hostile (claims): "hunting Bandit … N km". Overrides are sticky (`huntSticky`), so they close even outside missile range.
2. Shoot one down.
   - **Expect** the re-pick within about 1 s; up to two hunters per target.
3. Only missiles in view, none engaged by another Blue unit:
   - **Expect** a hunter to take the nearest one (within 120 km). A missile already engaged by a ship is skipped.
4. Include an empty fighter at issue.
   - **Expect** it refused `WINCHESTER` and sent home instead (an `rtb` order is created).
   - A fighter that runs dry mid-hunt: `achieved/winchester`, then RTB. Drones with guns keep hunting missiles (`winchesterAction.drone: gunsOnly`).
5. Nothing left for 20 s.
   - **Expect** `achieved/nothing_left`. A suspended patrol or move resumes.

**Pass:** claims spread units, re-pick on kills, winchester goes to RTB, end report.

## Row 27: first-launch alerts

1. Let Red fire the first missile of the session.
   - **Expect** one `FirstLaunch` report ("First Red launch: … fired at … N km") with `PauseRequested` (the game pauses if pause-on-event is on).
   - The second Red launch produces nothing. Blue's first launch produces its own single alert.

**Pass:** exactly one alert per side, and the pause is honoured.

## Row 28: advance on contact

1. Blue managed, `advanceOnContact` on (default). Before any hostile is identified:
   - **Expect** idle fighters and drones to stay put.
2. Eye identifies a Red group.
   - **Expect** one "Advance on contact" report. Every airborne Blue fighter and drone without an order, not on Hold, not winchester and not AEW gets a one-point route to the nearest identified hostile.
   - The target is re-picked every 2 s; the route is re-fed only when the target has moved more than 2 km. Guard stays on, so BDA attacks in range.
3. Give one advancing fighter any order.
   - **Expect** it to stop advancing.
4. Turn `advanceOnContact` off.
   - **Expect** no advance.
5. With `posture: proactive`, any hostile in view triggers the advance (not only identified ones), and ships raise SAM range.

**Pass:** trigger, eligibility, re-pick and refeed cadence, and orders replacing the advance.

## Known gaps to note, not fail

- **Carrier landing (row 11)** is out of scope. RTB ends at the marshal stack. `landGapS` (5 s) and `deckTurnaroundS` (90 s) are stored doctrine values for the future `recover` adapter.
- **Surface AI fly-to** behaviour near the point needs a spike (row 3, step 4).
- **Hunt and self-defence use sticky F1 by default**, deviating from the "non-sticky" wording in rows 23 and 26. A non-sticky assignment outside the missile envelope is dropped at BDA's next scan. Set `huntSticky` / `selfDefenceSticky` to false to get the literal behaviour.
- **Tick cost:** about 0.8 ms per C2 tick (Release, .NET) at 148 loaded units in a two-sided furball, against the 0.2 ms C2 budget in `02` §2.2. Most of it is the interim own-contacts picture, which the M2 TPE replaces. Measure in game with `C2Engine.Profile`.
