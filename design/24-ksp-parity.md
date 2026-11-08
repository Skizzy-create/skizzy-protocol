# 24. KSP parity: every mockup feature runs in KSP

**Rule (user, 2026-11-08).** The web mockup is the reference for look and behaviour. It is not the product. Every behaviour proven there must run in KSP with BDArmory Overdrive. A feature is not "done" until it passes in-game acceptance on the test machine (§3).

This table is the source of truth for that mapping. Update it whenever the mockup or a milestone changes.

## 1. Feature map

Hooks are in `07-bdarmory-integration.md` §5. "Skizzy" means the C2 engine in `Skizzy.Host`. "BDA" means BDArmory Overdrive.

| # | Mockup feature (version) | How it runs in KSP | Hooks | Milestone | Phase |
|---|---|---|---|---|---|
| 1 | Tactical plot, symbols, density sizing (v1–8) | Skizzy.Map renders the live track picture streamed from the host | — | M3 (render core in progress) | v1 |
| 2 | Groups, number keys, box/double-click select, Groups panel (v6–8) | Map-side selection; groups stored in the host (`c2.group.*`) so agents see them too | — | M3/M4 | v1 |
| 3 | Move / attack by right-click; drawn routes (v6–8) | `move_to`, `engage`, `move_route` orders. The executor feeds BDA waypoints and target overrides | F1, F6 | M4 | v1 |
| 4 | Attack spreads across the target group, retargets after a kill (learning) | Skizzy WTA assigns one override per shooter and re-assigns on `unit.destroyed` | F1, F10 | M4 | v1 |
| 5 | **Stay on task** (no distraction by crossing hostiles) (v11) | F3 `fireAuthority=Tight` plus an F4 `ExternalTargetFilter` allowing only the assigned group, plus an F1 sticky override. BDA guard, team assist and `GetGuardNonTarget` all obey the filter | F1, F3, F4 | M4 | v1 |
| 6 | **Engage on contact** (divert to any hostile the unit's own radar sees, then continue) (v11) | `fireAuthority=Free`, no filter. Skizzy watches per-vessel detections (F5, F10 `OnTargetDetected`) and re-points the override, then restores the original order when the contact dies or is lost | F1, F5, F10 | M4 | v1 |
| 7 | **Stealth contact seen only by a drone's own radar** (v11) | Per-vessel radar contacts (F5), not only team data link. The picture marks the track as "own-sensor only" until shared | F5 | M2/M4 | v1 |
| 8 | **Weapons control per aircraft: Free = guard, Tight, Hold = passive** (v11) | Free = guard mode on, fire authority Free. Tight = guard on, fire authority Tight with the filter. Hold = fire authority Hold (scans and locks, never fires); countermeasures still allowed | F3 | M4 | v1 |
| 9 | **Racetrack patrol that fights** (v10–11) | `hold_pattern` turns the racetrack into BDA waypoints with laps. **F6b** lets waypoints yield to combat when guard is on and a target exists, with a leash and a resume at the same waypoint. Hold stays on the pattern. Verified gap: stock `BDModulePilotAI` puts `FlyWaypoints` before the attack branch, so a stock patrol never fights | F6, **F6b**, F3 | M4 | v1 |
| 10 | RTB, RTB all (top bar), refusals with reasons (v10–11) | `rtb` order (`09` §2.1.1): base resolution, marshal point, refusals reported per unit; an in-game toolbar "RTB selected / RTB all" that works without any AI client | F6 | M4 | v1 |
| 11 | Carrier landing, deck, rearm, launch (v10) | Needs a carrier-landing autopilot driving ACA arrestor wires (the `recover` adapter). Until then `rtb` ends at marshal. Raised from P2 to P1 at the user's request | new adapter | M8 | v3 |
| 12 | Fighters and drones engage missiles when no aircraft are in view (v9) | Doctrine `airEngageMissiles`; BDA interceptor / missile-targeting settings on the weapon manager, priority via the F2/F4 filter | F2, F4 | M4/M6 | v1 |
| 13 | VIPs and protect orders (v9) | `defend_unit` order plus a side-level `vip` flag feeding WTA priority; protectors get waypoints orbiting the VIP and a filter limited to threats within the radius | F1, F4, F6 | M4 | v1 |
| 14 | Ship SAM priority for VIP threats (v9) | WTA priority on surface units' assignments (F2 secondary targets for multi-target ships) | F2 | M4 | v1 |
| 15 | Friendly fire logging (v7) | BDA simulates it physically; Skizzy attributes it from `Scoring` events and logs a counter and pause-on-event | F10 | M2 | v1 |
| 16 | Posture Defensive / Proactive (v5) | Side doctrine preset (`09` §11) | — | M6 | v1 |
| 17 | Orders never silently dropped (learning) | Validation on issue with typed refusals; every order ends with a report | — | M4 (rule from M0) | v1 |
| 18 | Comms, diplomacy, stance (v3+) | `22-communications-diplomacy.md` | — | M6/M7 | v1 |
| 19 | Timeline replay, skip to next event (v4, learning) | Recorder plus `sim.time.*` warp-until-trigger | — | M2/M3 | v1 |
| 20 | Multi-spawn in a line, any count (design) | OD1 in Overdrive plus `ksp.vessel.spawn_craft {layout:"line"}` | OD1, F11 | M1 (OD1 in progress) | v2 |
| 21 | Air-defence CAP squadrons armed with AAMs hold their CAPs and fight (v12) | Any side's flights get `hold_pattern` with patrol engage (row 9). For the opposing side the scenario director issues the order, so it is the same mechanism as Blue patrols | F6, F6b, F3 | M4 | v1 |
| 22 | Strike escorts carry AAMs and fly ahead of the bombers to screen them, so the bombers reach anti-ship launch range (v14) | `escort {unit}` with a screen slot ahead of the package leader. Skizzy computes the slot each tick and feeds it as a one-point F6 route (or a BDA wing formation slot through F7 reflection). Escorts run guard mode with AAMs, so BDA engages interceptors that come into range; the escort returns to its slot when the fight ends | F6, F7, F3 | M4/M6 | v1 |
| 23 | Fighters self-defend first against a hostile fighter within 25 km (v12) | Doctrine `selfDefenceFighterKm` (default 25). When a hostile fighter in the picture comes within that range of an own fighter, Skizzy sets a short non-sticky F1 assignment on it, ahead of the current order, logs the interruption (`23` §6 point 10, rule 2) and restores the order when the threat dies or opens. Under Tight the filter always admits such a threat | F1, F4, F5 | M4 | v1 |
| 24 | Own-radar classification by unit type: fighter 45 km, drone 25, AEW 175, ships 150 (v12) | Detection comes from BDA radar parts and RCS (F5 per-vessel contacts). Skizzy's track engine marks a track "identified" only when an own unit's radar holds it inside that unit's classification range (doctrine, per unit class). Engagement rules that need "identified" read that flag | F5, F10 | M2 | v1 |
| 25 | **All attack** (top bar, double-click to confirm): every unit engages; ships extend SAM range (v13) | `all_attack` order, side scope (`09` §2.1.3): every unit gets `attack_nearest`. Ships raise `engageRangeMax` on their SAM launchers (`EngageableWeapon.engageRangeMax`) up to the part's `maxStaticLaunchRange`; KSP cannot exceed the missile's real range, so the mockup's 90 km is reached only where the craft's SAM allows it | F1, F3, F4 | M4 | v1 |
| 26 | **Attack nearest** ("hunt") for the selection (v13) | `attack_nearest` order: Skizzy claims the nearest unclaimed closing hostile aircraft, or else an unengaged missile, per unit, and sets a non-sticky F1 assignment; claims spread units across targets; re-picks on F1 loss or F10 death; winchester aircraft get `rtb`. Stock BDA already prefers less-engaged targets (`GetAirToAirTarget` skips targets with 2+ friendlies engaging, `BDATargetManager.cs:1401`; `GetUnengagedMissileTarget`, `:1764`) but has no closing test and no one-shooter claim | F1, F4, F10, F12 | M4 | v1 |
| 27 | First-launch alerts, one for the opposing side and one for Blue (v13) | F9 `OnMissileFired` filtered by team; the first launch per side per session raises an alert and honours pause-on-event | F9 | M2 | v1 |
| 28 | **Advance on contact** (top-bar toggle, default on) (v14) | Doctrine `advanceOnContact`. From the first identified hostile (row 24), every airborne fighter and drone that is not AEW, not on Hold, not under an explicit operator order and not winchester gets a one-point F6 route to the nearest identified hostile, re-picked every 2 s. Guard mode stays on, so BDA attacks when in range. Not a lease-holding order: any operator order replaces it | F6, F5, F10 | M4 | v1 |
| 29 | Scenario of any size from force templates (carrier groups, squadrons, escorts, submarines) (v12–v14) | A scenario file lists groups and craft; `ksp.vessel.spawn_craft` places each group with the OD1 line algorithm (rows of aircraft in the air, ships abreast, subs at their combat depth). Count is a scenario parameter, not a built-in. Large scenarios depend on the performance limits in `07` §6 | OD1, F11 | M1/M8 | v2 |
| 30 | Weapon loads per craft (aircraft AAM loadouts, ship VLS cells and magazines) (v12, v14) | Loads come from the craft file. BDA counts a launcher's rounds as `railAmmo` plus linked `ModuleMissileMagazine` rounds (`ModuleMissileRearm.cs:29-31`; a multi-missile launcher's `maxAmmo` is its tube count, `MultiMissileLauncher.cs:207`). Skizzy reads them through `bda.ammo` for winchester and order validation | — | M1 (craft library) | v2 |
| 31 | Carriers in an attack order launch their armed deck reserve toward the nearest closing craft or unengaged missiles (v13) | `launch {aircraft}` adapter: deck aircraft are separate vessels with the pilot AI in Standby (`BDModulePilotAI.cs:794`, takes off when a target appears, `:2105-2115`). Skizzy launches them one at a time with the public `CommandTakeOff()` (`BDGenericAIBase.cs:463`) and a launch gap, then hands each an `attack_nearest`. Gap: BDA has no catapult, so a moving-deck takeoff depends on the craft's thrust; OD5 proposes a catapult assist. Fallback: spawn the reserve airborne beside the carrier | OD5 (gap), OD1 | M8 | v2 |
| 32 | Submarine units at depth (v14) | Surface AI with type Submarine holds `CombatAltitude` (default -75 m, `BDModuleSurfaceAI.cs:91`) and comes up to -10 m when a missile is selected (`:1081-1091`). Spawned at depth by the scenario. Picture: sonar contacts only, through F5 | F5 | M8 | v2 |
| 33 | **Sub-launched SAM with data link** (v14): the sub fires at an identified hostile aircraft only while a friendly unit holds the target close enough to guide it (fighter 45 km, drone 25, destroyer 60); in flight the link hands over to any unit that holds the target | Launch gate: the sub's F4 filter admits a target only while a provider within its range holds it in its own radar (F5), and F9 `FireMissileAt` fires it. Guidance: stock inertial missiles with data-link updates already take target updates from the launcher's radar picture, which includes allied radars linked through the VRD data link (`MissileBase.cs:1886-1945`), but with no provider ranges, no per-provider handover and no events. **Gap: OD2** (external link provider). Launch depth: stock BDA refuses a missile launch below -10 m (`MissileFire.cs:9514`), so subs launch at periscope depth; OD4 would allow deep launch | F4, F5, F9, **OD2**, OD4 (optional) | M8 | v3 |
| 34 | Link-loss coast and **self-destruct after 10 s**; link regained within 10 s continues; log entries for lost, regained, self-destruct (v14) | Stock: without updates an inertial missile flies on to the last known position; after `seekerTimeout` (part field, default 5 s, `MissileBase.cs:604`) guidance switches off (`:1878-1882`), and the miss check then detonates it 1.5 s later (`MissileLauncher.cs:2434-2449`). Setting `seekerTimeout = 10` in the SSAM part config gets close without code, but with no events and a late detonation. **Gap: OD3** (link-loss timer with self-destruct and events) | **OD2, OD3** | M8 | v3 |

## 1.1 Phases

Phases group rows by what the KSP side needs, not by date. The milestone column says when a row is scheduled; a v1 row can still land in a late milestone (comms in M6/M7, for example). A phase is done when every row in it passes in-game acceptance (§3).

- **Phase v1: orders and doctrine on the existing hooks** (F1–F12, F6b, OD1). Rows 1–10, 12–19, 21–28. Everything here is Skizzy logic driving BDA through hooks that already exist on `overdrive-hooks`: stay on task, engage on contact, racetrack patrol that fights, RTB, attack nearest, all attack, VIPs and protect, missiles as targets, first-launch alerts, advance on contact, escort screening, own-radar classification. No new BDA code.
- **Phase v2: force scale.** Rows 20, 29–32. Capabilities, not counts:
  - spawn a scenario of any size from force templates (OD1 plus craft files), group by group;
  - weapon loads per craft come from the craft file (BDA's launcher and magazine model, row 30), and Skizzy reads them for winchester and validation;
  - deck reserve launch: sequenced take-off of armed deck aircraft (the `launch` adapter; OD5 if a catapult assist is needed);
  - submarine units held at depth by the stock submarine AI.

  The mockup's v14 test scenario (3 carrier groups, 30 destroyers, 6 submarines, about 200 Blue units) is the reference load for v2 acceptance, not a requirement.
- **Phase v3: new hooks.** Rows 11, 33, 34. Sub-launched SAM with data-link handover (OD2), link-loss coast and self-destruct (OD3), optional deep launch (OD4), and the carrier-landing `recover` adapter. Each needs new code in Overdrive or a new adapter; the hooks are proposed in `07-bdarmory-integration.md` §5.2.

## 1.2 Releases

**Rule (user, 2026-11-08).** Every release is a usable version. Phases map to major versions, and work inside a phase ships as minor and patch releases (semantic versioning).

- **Major (`vN.0.0`)**: a phase's core is usable in the game. v1.0.0 = Phase v1 core, v2.0.0 = Phase v2 core, v3.0.0 = Phase v3 core.
- **Minor (`vN.M.0`)**: one or more parity rows added inside the phase, each passing its in-game acceptance item.
- **Patch (`vN.M.P`)**: fixes only, with no behaviour change beyond the fix.
- **Pre-1.0 (`v0.x`)**: the M0 foundation (connect, read, camera, log) is released as v0.x so it can be tested in the game before orders exist.

Proposed cut (to confirm once M0 lands):

| Release | Contents | Usable for |
|---|---|---|
| v0.1.0 | M0: host, `skizzy` CLI and MCP bridge, reads, streams, camera, log v0, install scripts | Connect Claude Code to KSP, read vessels, take screenshots |
| v1.0.0 | KSP plugin binding the Overdrive hooks plus the C2 orders engine: rows 3–10, 12–14, 17, 21–28 via MCP, plus an in-game toolbar (RTB selected / RTB all, fire authority) | Command a BDA battle from Claude Code or the toolbar |
| v1.1.0 | Tactical map app (rows 1–2) | Watch and command from the map screen |
| v1.2.0+ | Remaining v1 rows (15 friendly-fire attribution, 16 posture, 19 replay; 18 comms when M6 lands) | — |
| v2.0.0 | Phase v2 core (scenario spawn from templates, magazine reads, submarines) | Large scenarios |
| v3.0.0 | Phase v3 core (OD2/OD3 data link and self-destruct, `recover`) | Linked missiles, carrier recovery |

Each release ships through `skizzy-ckan` with the matching BDArmory Overdrive version pinned. Every release needs a passing test-agent report (§3) and the user's approval before it is pushed.

## 2. Rules for builders

- When a mockup behaviour has no KSP mechanism yet, add a row with "gap" and the proposed hook *before* building it in the mockup. Example: F6b came from the patrol request.
- Every row gets an in-game acceptance item in the milestone's checklist (`docs/Mx-checklist.md`) and, where it can be scripted, an automated scenario test driven through the Skizzy MCP once M0 exists (§3).
- Behaviour constants proven in the mockup are defaults in the KSP implementation and stay configurable in doctrine:
  - patrol leash 55 km
  - drone own-radar range 25 km, fighter 45 km (the real values come from the craft's radar parts)
  - classification range by unit class: fighter 45 km, drone 25, AEW 175, ships 150
  - fighter self-defence radius 25 km
  - advance-on-contact re-pick interval 2 s
  - sub-launched SAM link provider range: fighter 45 km, drone 25, destroyer 60; link-loss self-destruct 10 s
  - marshal 8 km astern
  - landing gap 5 s
  - deck turnaround 90 s

## 3. Testing on the user's desktop

Test machine: the user's desktop (Ryzen 9 9950X3D, RTX 5080, 32 GB).

- **A separate test agent runs there,** in Claude Code. It is a different agent from the builders: builders don't grade their own work.
- **Builds reach it through git.** Branches are pushed to the user's GitHub with the user's approval. The test agent pulls, builds, installs into a **separate test copy of KSP** (never the user's play install), runs the checklist, and pushes a report (`test-reports/<date>-<milestone>.md` plus `KSP.log` excerpts) back for the builders to read.
- **Before M0 works,** tests are guided manual runs from the checklists, with log scraping.
- **After M0,** the test agent drives KSP itself through `skizzy mcp`: it spawns scenarios, runs BDA competitions, reads tracks, takes screenshots and logs fields. In-game acceptance becomes scripted and repeatable, with real numbers.
- **Optional live debugging from the build machine:** run `skizzy serve` on the desktop, reach it over an SSH tunnel or Tailscale (never an open port), and authenticate with the token.
