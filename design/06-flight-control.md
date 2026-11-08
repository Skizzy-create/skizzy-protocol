# 06 Flight and vessel control

Principle (D5): **control loops run in the game at physics rate; clients send setpoints or orders.** Who may write what is decided by leases on the `steer` channel (`09-c2-multi-agent.md` §4). This file covers the `flight.*` family (alias `ksp.flight.*`), the in-game executors it drives, hand-off, co-flying, scene setup and the kill switch.

## 1. Controller stack per vessel

```
FlightCtrlState for vessel V, every physics step (Vessel.OnFlyByWire invocation order):
  1. KSP SAS (vessel.Autopilot)                            stock
  2. BDArmory AI autoPilot (BDGenericAIBase.autoPilot)     if pilotEnabled   [BDGenericAIBase.cs:111-144, registered :180]
  3. BDAirspeedControl / BDLandSpeedControl                BDA throttle
  4. Skizzy FlightMixer (per vessel, registered last)      only while a hold, route, formation or direct lease exists
       • mode "override": replaces pitch/yaw/roll/throttle with Skizzy hold outputs
       • mode "mix":      lets BDA steer but clamps/offsets (e.g. speed cap, heading bias, depth floor)
       • mode "observe":  no writes (used to detect human input)
  5. Human input (FlightInputHandler.state for the active vessel) is read before 1; Skizzy sees it in "observe"
```

The mixer is a single `FlightInputCallback` per vessel, pre-allocated, zero-alloc, enabled only while needed (RB3). Ordering caveat: BDA's `ActivatePilot()` does `OnFlyByWire -= autoPilot; += autoPilot` (`BDGenericAIBase.cs:177-180`), which moves BDA to the end of the multicast list. The mixer therefore re-registers itself (remove + add) when it observes a `pilotEnabled` rising edge on the vessel's AI, and once per second as a safety net; with the fork hooks `BDGenericAIBase.SteeringOverride`/`PostAutoPilot` (F8, `07-bdarmory-integration.md` §5) ordering stops mattering.

**[R] When callbacks run at all.** `Vessel.OnFlyByWire` is invoked only for loaded, unpacked vessels. A packed vessel (on rails, outside the unpack range, or during rails warp) runs no callbacks, so holds on it do nothing; the host reports this rather than pretending (`PRECONDITION_FAILED`, reason `vessel_packed`) and holds resume when the vessel unpacks (`20-runtime-lifecycle.md` §1). When `vessel.IsControllable` is false (no crew/probe power, or CommNet/RemoteTech/Kerbalism without signal) KSP still calls the callbacks, but BDA's guard mode stops (`MissileFire.cs:2477`) and the player cannot fly; Skizzy reports `not_controllable` in the SITREP and refuses new holds unless `allowUncontrollable:true` (test ranges).

## 2. Direct controls (P0)

`flight.state`, `flight.set_controls {throttle, sas, sasMode, rcs, gear, brakes, lights}`, `flight.action_group`, `flight.stage`, `flight.switch_vessel`: as PLAN v0.2 §8.2. `flight.set_controls` on a non-active vessel writes through the mixer (one-shot setpoint held until released).

- **Staging permission resolver [R].** Pass 1.0 made every `flight.stage` destructive, which put a prompt in front of every rocket launch. The host now inspects the parts of the *next* stage: if it contains a BDA weapon (`MissileLauncher`, `ModuleWeapon`, `MultiMissileLauncher`), a decoupler or separator whose separation would leave a crewed part without command, or an abort/LES, the call is `destructive`; otherwise it is `control`. The resolved level and the reason are in the result and in the audit log. Action groups get the same treatment for groups bound to weapon fire actions.
- **Orbital pointing [R]** (IDEAS 4.3 "orbital"): `flight.orbit_point {vessel, mode: stability|prograde|retrograde|normal|antinormal|radial_in|radial_out|target|antitarget|maneuver}` maps to `vessel.Autopilot.SetMode(VesselAutopilot.AutopilotMode.*)` after `CanSetMode` (fails with `PRECONDITION_FAILED`, reason `api_missing`, when the pilot/probe level does not offer the mode). Orbital hand-off to BDA's `BDModuleOrbitalAI` is a normal `flight.handoff`. Manoeuvre planning (nodes, burns) is P3; agents can create a node with `ksp.vessel.maneuver.add` (P3).
- **AI activation check [R].** BDA's `CommandFlyTo`, `CommandAttack`, `CommandFollow` and `ReleaseCommand` silently return when `pilotEnabled` is false (`BDGenericAIBase.cs:426,445,455,470`). Every executor that calls them checks `pilotEnabled` first and either activates the pilot (when the caller holds the `steer` lease) or fails with `PRECONDITION_FAILED`, reason `ai_inactive`.

## 3. Setpoint holds (P0)

`flight.hold {vessel, heading?, pitch?, roll?, altitude?, speed?, depth?, verticalSpeed?, bank?, limits?, gains?, waitFor?:"settled"}` installs a hold on the `steer` channel (acquiring the lease, or failing with `LEASE_HELD`). Controllers are cascaded PIDs with per-class default gains (plane, rocket, rover, boat, submarine), mass/inertia scaling, output slew limits and anti-windup, as in PLAN v0.2 §8.2. Additions:

- **Boat heading hold** uses yaw + `wheelSteer`, with a rudder-authority estimate from speed; **speed hold** uses `mainThrottle` + `wheelThrottle`, with reverse allowed when `allowReverse:true`.
- **Depth hold** (`depth`) has three backends, chosen per vessel: SubTrim, KP Naval `ModuleShipBallast.targetDepth`, or pitch-only (dive planes) as a last resort. The SITREP reports which backend is active. **[R]** The SubTrim backend in v1 sets every `ModuleSubTrim` on the vessel to manual mode with `manualDepth`/`targetDepth` through the PAW path and restores the previous mode on release, so SubTrim's own trim/ballast controller does the work. The `IDepthCommandSource` registration is v2 (P2) because it needs a hard-referencing assembly and a SubTrim priority option (`04-ksp-and-mod-access.md` §5).
- **Hold lifetime [R].** A hold persists until `flight.release`, a `flight.handoff`, the kill switch, or the expiry of the holder's lease (then the lease's `onExpire` runs). There is no dead-man on plain holds: an agent that sets "heading 090, 15 kt" and goes quiet for a minute is normal. Dead-man timers apply only to `flight.direct` and to setpoint streams that declare `deadmanMs` (§4).
- **Setpoint batch** `flight.setpoint_batch {items:[{vessel, heading, speed, altitude}...], deadmanMs?, onExpire?}` applies setpoints for many vessels in one message (msgpack) for swarm scripts; one `fixed` job, O(n). With `deadmanMs` (recommended for swarms, e.g. 500) silence longer than that runs `onExpire` (default `hold_last`) for every vessel in the stream.
- `flight.holds` lists holds with errors, saturation and settled state; `flight.release {vessel|all}` restores the pre-hold SAS state.

## 4. Direct control channel (P1) — [ADDED]

`flight.direct {vessel, pitch, yaw, roll, throttle, wheelSteer, wheelThrottle, ttlMs:200}` writes raw axes for one tick window. A **dead-man**: if no update arrives within `ttlMs` the mixer applies `neutral` (zero axes, throttle unchanged) and after `releaseAfterMs` (default 2000) releases the lease, whose `onExpire` then runs (`bda_guard` by default). Rate cap 50 Hz per vessel. Intended for external autopilots and research; the SDK docs state the loopback latency (~1–3 ms) and the physics step (20 ms) so users understand the loop they are closing.

## 5. Route executor (P0)

`flight.route {vessel|group, waypoints:[{lat,lon,alt?,speed?,radiusM?}], mode:"transit"|"patrol"|"racetrack", loop, finalHeading?}` creates a `move_route` order executed in-game:

| Vessel kind | Mechanism |
|---|---|
| BDA surface AI (ships, subs) | sequence `CommandFlyTo(gps)` per waypoint (`BDGenericAIBase.cs:443`), advance when within `radiusM` (default 200 m) or when `AIUtils.TimeToCPA` says the point is passed; speed via `CruiseSpeed`/`MaxSpeed` fields (restored at the end); depth via the depth hold |
| BDA pilot AI (aircraft) | with the fork patch `SetWaypoints(points, radius, laps, speeds)`: BDA's own waypoint mode; without it: Skizzy heading/altitude/speed holds in `override` mode while BDA guard mode keeps firing (weapons channel untouched); on `engage` orders control returns to BDA (`ReleaseCommand`). **[R]** Stock `SetWaypoints` is usable only when the list is no longer than the selected course and `WAYPOINTS_SCALE > 0` is set for the duration (`07-bdarmory-integration.md` §1.3) |
| Skizzy-only vessel (no BDA AI) | holds |

Why the patch: stock `SetWaypoints` reads `WaypointCourses.CourseLocations[waypointCourseIndex].waypoints[i].scale/maxSpeed` (`BDGenericAIBase.cs:572, 603`), so an arbitrary list indexes a course that does not match it. The `PilotAI` `FlyTo` command only *orbits* the point at a hard-coded 2500 m (`BDModulePilotAI.cs:5335`), which is useless for transit legs; the patch adds `commandOrbitRadius`.

Routes drawn on the map (`11-tactical-map.md` §6) and routes from agents are the same object (`c2.order.issue type:move_route`).

## 6. Formation executor (P0)

- **BDA-native formation:** a lead vessel's `ModuleWingCommander` with `CommandFollow(leader, index)` (`BDGenericAIBase.cs:424`); the follower's AI computes its slot from `leader.GetFormationPosition(index)` (`ModuleWingCommander.cs:694`, default spread/lag ladder) in the leader's velocity frame (`BDModulePilotAI.cs:5422`; surface AI has the equivalent path at `BDModuleSurfaceAI.cs:841`). Both aircraft and ships support Follow. `flight.formation {leader, members:[{vessel, slot|offset:{right,back,up}}], template:"line_abreast|line_ahead|wedge|diamond|screen|box", spacingM}` sets slots. **[R]** Arbitrary slot offsets work on stock BDA Plus: `formationIndexToPosition` is a `private readonly Dictionary<int, Vector2>` (`ModuleWingCommander.cs:687`); `readonly` protects the reference, not the contents, so the shim gets the instance once by reflection and writes entries (`right, back` in metres; there is no vertical component, so `up` is applied by Skizzy's altitude hold for aircraft). The fork's `SetFormationPosition` (F7, now P2) only tidies this. The default ladder with `spread`/`lag` remains the fallback when reflection fails.
- **Skizzy station keeping** for vessels under Skizzy holds (or when the leader is human-flown): a per-member PID on the slot position in the leader's frame, feeding heading/speed/altitude holds; used for naval screens around a carrier at 10–20 kt and for human-led flights (the wingman scenario).
- Templates are doctrine objects; `screen` places escorts on threat-axis-weighted bearings around the main body.

## 7. Hand-off (P0)

`flight.handoff {vessel, to:"manual"|"skizzy"|"bdarmory"|"atmosphereautopilot", aaMode?}`:
- to `bdarmory`: release Skizzy holds, `vessel.ActiveController().SetActiveAI(ai)` / `ActivatePilot()` (`VesselModuleRegistry.cs:1125`, `BDGenericAIBase.cs:172`); guard mode is untouched (weapons channel).
- to `manual`: release holds, `DeactivatePilot()`, restore SAS; the human lease is implicit.
- to `atmosphereautopilot`: `TopModuleManager.activateAutopilot(typeof(StandardFlyByWire|CruiseController))` via reflection, failing closed; Skizzy can then drive AA's `desired_altitude`/`desired_course`/`desired_velocity` as a hold backend for aircraft (smoother than our PIDs on stock aero).
- `flight.state.controller` reports `manual|skizzy|bdarmory|atmosphereautopilot|mixed` and the lease holder.

## 8. Human and agent co-flying (P0)

- The pilot's inputs are read from `FlightInputHandler.state` each step. **[R]** Pitch/yaw/roll are measured relative to the current trim; throttle is detected by **change** (the human's throttle value moves by more than 2 % between steps), not by its absolute value, because KSP keeps the last human throttle setting while an agent holds speed. A deviation beyond a dead-band on any axis for 3 consecutive steps raises `human.took_controls` and grants the implicit human lease (precedence 1000) on `steer` for the active vessel; agent holds on that vessel are suspended (not cancelled) and resume after `humanIdleS` (default 5 s) without input, emitting `human.released_controls`. Configurable to "never resume" per doctrine. **[R]** This transient behaviour is for touching the stick. A deliberate take-over ("Fly in KSP" from the map, or **Take control** in the in-game Flight window) gives a sticky pilot lease that lasts until **Return to mission**; the mission or order underneath is suspended and re-plans from the vessel's position on return (`09-c2-multi-agent.md` §4.1).
- **Active-vessel switches [R].** When the active vessel changes without the human choosing it (the flown vessel died and KSP switched, or BDA's competition camera switched), the implicit human lease does not jump to the new vessel: holds on the new active vessel keep running until the human actually touches an input there. `vessel.active_changed` is emitted with the cause (`20-runtime-lifecycle.md` §2).
- **[R2]** Delegates and scripts flying wingmen never touch the active vessel unless the human explicitly grants it (`c2.lease.grant {vessel:"active", to:"a:blue/wing", channel:"steer"}` from the in-game window or the map).
- The in-game HUD shows a one-line banner: `AI: wingmen 2 (follow) · you: manual · Ctrl+Alt+K stops all`.
- Voice/text commands from the human to wingmen go through the same orders (`c2.order.issue` from the in-game command window, the map or Claude Code).

## 9. Scene setup, positioning and spawning (P0)

`ksp.vessel.place {vessel, lat, lon, alt?, heading, pitch?, roll?, aboveTerrain?, speedMps?}` **[R]** is implemented by Skizzy itself (pass 1.0 delegated to BDA's VesselMover, but `PlaceVessel(vessel, skipMovingCheck)` takes no position: it lowers a vessel from where it already is onto the surface, and aborts unless the vessel was picked up first or `skipMovingCheck` is true): position from `body.GetWorldSurfacePosition(lat, lon, alt)` (with `aboveTerrain` adding terrain height, sea level for water), `vessel.SetPosition`, `vessel.SetRotation` from heading/pitch/roll in the local north-east-up frame, `vessel.SetWorldVelocity` (zero or `speedMps` along the heading), `vessel.IgnoreGForces(240)` around the move, Krakensbane velocity offset accounted for, one settle frame for KJR before physics resumes. Landing a vessel gently on terrain uses BDA's `VesselMover.Instance.PlaceVessel(v, skipMovingCheck:true)` when BDA is installed (`VesselSpawning/VesselMover.cs:445`); `PickUpAndDrop` (`:542`) backs `lift`/`drop`; `ksp.vessel.lift {vessel, m}`, `ksp.vessel.drop`; `ksp.vessel.teleport_orbit` via HyperEdit (P2). Formation placement of many vessels uses `bda.spawn_template` (`CustomTemplateSpawning.SpawnCustomTemplate`, `CustomSpawnTemplate.cs:95`) or `sim.scenario.load`. All `cheat`.

### 9.1 Spawn anywhere (P0, M1; formations P0 M1; map drag M3; waves M6; orbit P2) — [IDEAS, user requirement 2026-11-08]

The user likes the VesselMover-style spawner, both the standalone VesselMover mod and BDA's built-in VesselMover window, because it puts a craft **anywhere, including mid-air**. Skizzy makes this first-class and builds it on `ksp.vessel.spawn_craft` and `ksp.vessel.place`. BDA's competition spawners (ring and template, `07-bdarmory-integration.md` §1.6) stay as they are.

```
ksp.vessel.spawn_craft {
  craft,                                   // library ref "DDG Burke@v2" or a .craft path in the sandbox
  at: {lat, lon} | {ref, bearingDeg, rangeM} | {site:"runway"|"<KK launch site>"},
  mode: "air" | "sea" | "submerged" | "ground" | "runway" | "orbit",
  altM?, altRef?: "asl"|"agl",             // air (default asl)
  depthM?,                                 // submerged
  orbit?: {altKm | smaKm, inc?, ecc?, lan?, argPe?, meanAnomaly?},   // orbit (P2)
  heading, pitch?, roll?,
  speedMps?,                               // air default: the craft's BDA pilot idleSpeed, else 150; sea and submerged default 0
  engines?: "on"|"off", throttle?,         // air default on, throttle 0.8 until the AI or a hold takes over
  gear?: "up"|"down",                      // air default up
  team?, guard?: bool, ai?: bool,          // ai default true when the craft has a BDA AI; guard default false unless set
  group?: "<id>" | {create:"<name>"}, mission?, name?, uniqueName?: "auto"|"strict", crew?,
  settleS?,                                // default: air 2 s, sea 5 s, submerged 5 s, ground 3 s
  count?: ≥1 (no cap), layout?: "single"|"line", lineDir?: "abreast"|"trail", spacingM?: "auto"|<m>, perRow?: "auto"|<n>   // multi-spawn, below
} → op → {vessels:[{vessel, name, pid, slot, row, settled:{residualMps, attitudeErrDeg}}], group?, shifted?:{byM, reason}, warnings?:[{kind:"large_spawn", count, estimate}]}
```

**Modes.**

| Mode | What Skizzy does | Checks |
|---|---|---|
| `air` | Builds the vessel at the requested altitude (ASL, or AGL with `altRef:"agl"`) and attitude. In its first unpacked physics frame it sets `vessel.SetWorldVelocity` along the heading and pitch at `speedMps`, with `IgnoreGForces(240)` and the Krakensbane offset handled. Retracts gear and starts engines (the BDA convention: action group 10 first, then `SpawnUtils.ActivateAllEngines` if no engine is running) at `throttle`. For `settleS`, a heading/altitude/speed hold (`§3`) flies the craft, then hands over to the BDA pilot with `ActivatePilot` (and guard mode if `guard:true`). The craft never drops or stalls on spawn. | Must be inside the loaded physics range (`PRECONDITION`, reason `outside_physics_range`, otherwise): KSP destroys packed vessels in low atmosphere. Extend the range with PRE first, through BDA's helper. |
| `sea` | Spawns about 2 m above the estimated waterline (from the craft's validated draft, `05-craft-building.md` §4, or its bounds), zero velocity. Then a settle phase: velocity damping until vertical speed is below 0.5 m/s, for at most `settleS`. Reports the residual velocity. | `PRECONDITION`, reason `not_water`, when the terrain at that position is above sea level. |
| `submerged` | Sets the depth controller before release: SubTrim `manual` mode with `manualDepth = depthM` through the SubTrim adapter (`04-ksp-and-mod-access.md` §5), or KP Naval ballast. Spawns at `depthM` with velocity along the heading, then holds depth with `flight.depth` for `settleS`. | `PRECONDITION`, reason `no_depth_control`, when the craft has neither SubTrim nor ballast (override with `force:true`). |
| `ground` | Spawns slightly above the terrain, then lowers it gently with BDA's `VesselMover.Instance.PlaceVessel(v, skipMovingCheck:true)` (`VesselSpawning/VesselMover.cs:445`). Without BDA, Skizzy's own terrain raycast and a slow descent. | Slope and obstruction check from the raycast. |
| `runway` | Position and heading of the KSC runway or a KerbalKonstructs launch site (`LaunchSiteManager.AllLaunchSites`), then as `ground`. | The site exists. |
| `orbit` (P2) | Builds the vessel, then sets its orbit with stock `Orbit.SetOrbit` on `vessel.orbitDriver` and goes on and off rails; HyperEdit is not needed. | Altitude above the body's minimum safe altitude. |

**Multi-spawn: N in a line, with clear-space finding (P0, M1)** — user addition 2026-11-08, corrected the same day.

- **The count is whatever the user enters.** One action spawns N copies of a craft in a straight line near the chosen point, on the ground, on the sea, submerged or in the air: 5 gives 5, 37 gives 37. There is no cap.
- **Batching is internal.** For physics stability, large counts are built in internal batches staggered over frames (below). That is an implementation detail, not a limit.
- **Large counts warn but are allowed.** Above 50 the map, the in-game window and the API result carry a warning with an estimated performance impact, and the spawn still goes ahead. The estimate comes from the per-part physics cost that `host.selfcheck.perf` measured on this machine (before calibration a rough default of 1 ms of physics per 250 parts), and lists:
  - the loaded-vessel total after the spawn;
  - the added parts;
  - the expected frame time;
  - whether the vessels fit inside the physics range.

- **Line.** With `lineDir:"abreast"` (default) the line is perpendicular to the heading; with `"trail"` it runs behind the leader along the heading. All copies get the same heading, the same altitude or depth, and in the air the same initial speed.
- **Spacing.** `spacingM:"auto"` (default) uses the craft's size plus a margin:
  - size: the lateral extent (wingspan) for abreast, the length for trail, taken from the craft's bounding box;
  - margin: `max(10 m, 0.25 × size)` on the surface, `max(30 m, 0.5 × size)` in the air.

  A number sets the spacing in metres.
- **Clear space.** Each slot is checked before anything spawns:

  ```
  slots = line(center = at, direction from lineDir and heading, n = count, spacing)
  free(slot) =
      footprint = craft bounding box at slot (+ margin)
      ground:    land at slot (TerrainAltitude > 0), max slope over a 3×3 raycast grid ≤ 10°, no building or static collider
                 (Physics.OverlapBox on the scenery and KerbalKonstructs static layers)
      runway:    as ground, inside the site's rectangle
      sea:       water deeper than the craft's draft + 2 m, no collider
      submerged: water deeper than depthM + craft height + 10 m
      air:       height above terrain ≥ max(50 m, size) at the slot and along the first 10 s of the initial velocity
      all modes: distance to every loaded vessel ≥ its radius + craft radius + margin, and clear of the slots already accepted
  for each blocked slot: shift it outward along the line by spacing/2, up to 3 times, keeping the order
  if any slot is still blocked: move the whole line. Search candidate centres in rings around `at`
      (step = line length, 8 rings, max 5 km) and take the nearest centre where every slot is free
  if none: PRECONDITION reason no_clear_space, with the best candidate in data
  ```

  The result reports `shifted {byM, reason}` when the line moved, and the map previews the final slots before confirming.
- **Rows.** A long line wraps into parallel rows when space runs out:
  - Wrapping happens when the slots no longer fit within `maxRowLengthM` (default 2 km), when `perRow` is reached, or when clear space along the line ends.
  - With `abreast`, the rows stack behind each other along the reverse heading; with `trail`, they sit side by side. Rows are one `spacing` apart, plus the craft's length or width.
  - Every slot in every row gets the same clear-space check.
  - When the whole block has to move, the ring search grows with the block size (step = block diagonal, up to 8 rings).
- **Options.** `count` (any positive number), `spacingM` (`auto` or metres), `lineDir` (abreast or trail), `perRow` (`auto` or a number), `team`, `guard`, `ai`, `group` (existing or `{create}`), `mission`.
- **Staggering.** Internal batches of one vessel at a time, with the same per-frame part limit as below. A 100-aircraft spawn of 40-part craft takes about 200 frames, roughly 4 s at 50 Hz.
- **Reachable from:**
  - the map: drag a craft, type the count on the spawn card, see the slot and row preview and, above 50, the warning;
  - scripts and agents: `ksp.vessel.spawn_craft {count, layout:"line", …}`;
  - the in-game **Spawn** window: craft picker, mode, a count field, spacing, abreast or trail, per-row count, team, guard, AI, group, the large-count warning, and spawn at the camera target or at a picked vessel;
  - the CLI: `skizzy call ksp.vessel.spawn_craft craft=… count=6 layout=line mode=air altM=3000 speedMps=220`.
- **Also a BDArmory Overdrive feature.** The same count (no cap), line, rows and clear-space options are a good fit for BDA's own VesselMover spawn window in the user's fork, so they work without skizzy-protocol. They are recorded as fork feature **OD1** (P1) in `07-bdarmory-integration.md` §5.1. When OD1 is installed, Skizzy can delegate `layout:"line"` to it. Both use the algorithm above.

**Many at once: formations.** For larger or shaped groups:

```
ksp.vessel.spawn_formation {
  craft | crafts:[{craft, count}],
  count?, formation: {template:"wedge"|"line_abreast"|"echelon_left"|"echelon_right"|"column"|"box"|"ring"|"grid"|"custom", spacingM, slots?},
  at, mode, altM?, depthM?, heading, speedMps?, team?, guard?, ai?, group?, mission?,
  stagger?: {maxPartsPerFrame: 400, minFramesBetween: 2}
} → op (progress per vessel) → {vessels:[…], group}
```

- **Slots.** Positions come from the template (or the doctrine's `formations`, `09-c2-multi-agent.md` §5) relative to the leader's position and heading. Every member gets the same velocity vector, so an air formation spawns already flying in formation.
- **Staggering.** Spawning is spread over frames: at most `maxPartsPerFrame` parts are built per frame, with at least `minFramesBetween` frames between vessels. A 20-drone swarm of 30-part drones spawns in about 40 frames, with no physics spike.
- **Grouping.** The whole set can become a new group, join a mission, and get a team, guard mode and AI in the same call.
- **Example.** Twenty drones in a wedge at 3 km and 250 m/s:

  ```
  spawn_formation {craft:"Drone-X@v3", count:20, formation:{template:"wedge", spacingM:120}, at:{ref:"rp:alpha", bearingDeg:270, rangeM:15000}, mode:"air", altM:3000, heading:90, speedMps:250, team:"Blue", ai:true, guard:true, group:{create:"Swarm-1"}}
  ```

**Mid-battle reinforcements and respawn.**

- **Waves.** The scenario director's waves (`09-c2-multi-agent.md` §9, `10-scripting-sdk.md` §7) use the same spawn spec (`spawn` or `spawn_formation` entries) at a UT or when a trigger fires.
- **Respawn rules.** Set per group or side, in the scenario or live with `sim.respawn.set|clear|list`:

  ```
  respawn: {when: lost, delayS: 60, max: 4, at: base|spawn_point|<ref>, mode, rejoin: group|mission}
  ```

  A respawned unit rejoins its group or mission, and its name gets the next `#n` suffix (`20-runtime-lifecycle.md` §6).
- **BDA's own continuous spawning.** For BDA-style continuous competitions, `bda.spawn_continuous` wraps `ContinuousSpawning.Instance.SpawnVesselsContinuously` (`VesselSpawning/ContinuousSpawning.cs:93`) with BDA's scoring. Skizzy's respawn rules are separate, for C2 battles that need to know about groups, missions and teams.

**From the map, scripts and agents.**

- **Map (M3).** The map's craft library panel supports drag onto the map, which opens a small spawn card. The mode is chosen from the craft type: aircraft get `air` with altitude and speed defaulting to the craft AI's `defaultAltitude` and `idleSpeed`; ships `sea`; submarines `submerged` with a depth. Dragging out an arrow sets the heading, and Shift-drag spawns a formation (count, template, spacing).
- **Scripts, agents and the CLI.** They use the same two calls, for example `skizzy call ksp.vessel.spawn_craft craft="F-22@v1" mode=air altM=6000 speedMps=230 …`.
- **In-game.** The capability console offers the same calls with a position picker ("at camera", "at cursor on the map", "at a vessel").

**What is reused and what is Skizzy's own.**

| Function | Implementation | Why |
|---|---|---|
| Craft file → vessel | BDA `VesselSpawner.SpawnVesselFromCraftFile(craftURL, gpsCoords, heading, pitch, roll, out facility, crew)` (`VesselSpawning/VesselSpawner.cs:70`, public static). Fallback: the standalone VesselMover mod's spawner when present, then Skizzy's own `ProtoVessel` builder (P1, M5 with the craft work) | proven code for crew, naming and part loading; the user always has BDA |
| Exact pose and velocity in air, sea and submerged | Skizzy's placement (§9) | BDA's airborne path (`SingleVesselSpawning.SpawnVessel`, `:57`) treats any altitude above 10 m AGL as airborne and measures altitude from terrain. It sets only the pilot's `idleSpeed`, and only when `VESSEL_SPAWN_INITIAL_VELOCITY` is on (default off, `Settings/BDArmorySettings.cs:368`). Its activation (`SpawnUtils.AirborneActivation`, `:244`) turns guard mode **off**. Skizzy needs exact ASL or AGL altitude, any speed, and guard on when asked |
| Engine start | BDA's convention: action group 10, then `SpawnUtils.ActivateAllEngines` (`:131`, public static) if `CountActiveEngines` (`:126`) is 0 | same behaviour as BDA spawns, so craft built for BDA work unchanged |
| Landing on ground | BDA `VesselMover.PlaceVessel(v, skipMovingCheck:true)` | gentle descent already solved |
| Ring and template spawns | BDA `CircularSpawning` and `CustomTemplateSpawning` (`bda.spawn_*`) | competitions as the user knows them |
| Continuous spawning | BDA `ContinuousSpawning` | BDA scoring |
| Formations, staggering, team/guard/AI, groups, missions, waves, respawn, orbit | Skizzy | not offered by BDA in this form |

**Permissions.** All spawn calls are `cheat`, allowed by default in the Battle Lab preset (`15-security-permissions.md` §1). **[R2]** Large spawns are never blocked: above 50 vessels in one call there is a performance warning only. Pass 1's two-key rule for big spawns is dropped by the user's correction; two-key stays for removing all vessels and for loading scenarios that kill everything first.

## 10. Kill switch (P0)

Hotkey (default `Ctrl+Alt+K`), toolbar button, map button, `host.kill_switch` capability (any actor with `control`), and the in-game banner. Effects within one physics step: deny all non-read calls (`KILL_SWITCH_ACTIVE`); cancel operations; reject pending confirmations; release every Skizzy hold, direct lease and setpoint stream; restore SAS state; **leave BDA AI and guard mode as they were on every vessel except the active one** (the user's fleet must not go dead in the water) unless `alsoDisableBdaAi:true`. **[R]** On the **active vessel** the BDA pilot is also deactivated (guard mode stays as it was), because "always restores manual control" (IDEAS 4.6) means the human can fly the ship they are in immediately; BDA's AI would otherwise keep the stick. Optionally pause; emit `host.kill_switch`; show banner until released from the in-game UI (or by a `human` actor via `host.kill_switch {release}`); release does not restore any lease or hold.

## 11. Carrier operations (P2) — [ADDED]

A `naval.carrier` service wraps ACA: `launch {carrier, aircraft}` = taxi-to-catapult hold (station keeping relative to the catapult transform), hook/attach (`Catapult` module), throttle up, `Launch`, then hand-off to BDA AI with `CommandTakeOff`; `recover {carrier, aircraft}` = marshal (racetrack hold astern), final approach hold on the glideslope to the deck point, `Hook.HookDeploy`, wire capture (`Arrestwire.IsWire`), taxi clear; deck state machine stream (`idle|launching|recovering|foul`). Hangar storage via `HangarMachinery` when present. The AI quality of the final approach is the risk; the scenario in `16-scenarios.md` §5 limits v1 to launch + marshal + hand-flown or BDA-assisted recovery.

## 12. Acceptance (summary)

1. Plane hold converges and holds (PLAN tests); boat heading ±5°, speed ±1 m/s; sub depth ±3 m with SubTrim, ±8 m without.
2. Route of 6 waypoints for a destroyer at 15 kt completes with ≤ 200 m cross-track error at waypoints; aircraft route via holds and via patched waypoints both complete.
3. `flight.formation` puts 4 escorts in a `screen` within 10 % of spacing after 120 s and keeps station during a 30° turn.
4. Human takes the stick: `human.took_controls` within 60 ms; agent hold suspended; resumes after 5 s idle.
5. Kill switch releases 20 scripted drones within one physics step; BDA guard remains active; on the active vessel the BDA pilot is off and the stick responds to the human.
6. Twenty vessels on `flight.setpoint_batch` at 20 Hz cost ≤ 0.3 ms per physics step.
7. **[R]** `ksp.vessel.place` puts a destroyer at a given lat/lon on water, heading ±1°, with no explosion and < 2 m/s residual velocity after 5 s; an aircraft placed at 3000 m with `speedMps:200` flies off without stalling.
8. **[R]** `flight.depth 50` on a SubTrim sub holds ±3 m and `flight.release` restores SubTrim's previous mode and depth.
9. **[R]** A hold on a vessel that goes on rails reports `vessel_packed` and resumes when it unpacks; `flight.stage` on a stage with a missile asks, on a plain engine stage does not.
10. **[R]** `flight.orbit_point prograde` on a probe in orbit points within 2° in 30 s.
11. **[R2] Spawn mid-air.** `spawn_craft {mode:"air", altM:3000, speedMps:230, guard:true}` for an F-22 gives level flight at 3000 ± 50 m within 2 s, gear up and engines running, with no stall and no altitude loss over 50 m. The BDA pilot is active and guard mode is on after `settleS`.
12. **[R2] Spawn at sea and submerged.** A destroyer spawned with `mode:"sea"` settles with vertical speed < 0.5 m/s within 5 s and no part lost. A SubTrim submarine spawned at `depthM:80` holds 80 ± 5 m after 10 s.
13. **[R2] Multi-spawn.** `count:10, layout:"line"` on the KSC runway area spawns ten aircraft abreast. The line shifts clear of a building and of a parked vessel in the way (`shifted` reported), and no two craft overlap. The same call in the air gives ten aircraft at the same altitude and speed, spaced by wingspan plus margin. Every case is staggered: the worst frame-time spike stays under 1.5× the median. `count:60` in the air wraps into rows, returns the `large_spawn` warning with an estimate, and spawns all 60.
14. **[R2] Swarm formation.** `spawn_formation` with 20 drones in a wedge at 3 km and 250 m/s produces a flying wedge within 10 % of spacing after 5 s, assigned to group `Swarm-1` with AI and guard on. Spawning more than 50 at once shows the performance warning and proceeds.
15. **[R2] Respawn and waves.** A respawn rule `{when: lost, delayS: 30, max: 2}` brings back a lost fighter twice, rejoining its CAP mission. A scenario wave spawns a 6-ship formation at its trigger.
