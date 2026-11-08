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
- Agents flying wingmen never touch the active vessel unless the human explicitly grants it (`c2.lease.grant {vessel:"active", to:"a:wing-ai", channel:"steer"}` from the in-game window or the map).
- The in-game HUD shows a one-line banner: `AI: wingmen 2 (follow) · you: manual · Ctrl+Alt+K stops all`.
- Voice/text commands from the human to wingmen go through the same orders (`c2.order.issue` from the in-game command window, the map or Claude Code).

## 9. Scene setup and positioning (P0)

`ksp.vessel.place {vessel, lat, lon, alt?, heading, pitch?, roll?, aboveTerrain?, speedMps?}` **[R]** is implemented by Skizzy itself (pass 1.0 delegated to BDA's VesselMover, but `PlaceVessel(vessel, skipMovingCheck)` takes no position: it lowers a vessel from where it already is onto the surface, and aborts unless the vessel was picked up first or `skipMovingCheck` is true): position from `body.GetWorldSurfacePosition(lat, lon, alt)` (with `aboveTerrain` adding terrain height, sea level for water), `vessel.SetPosition`, `vessel.SetRotation` from heading/pitch/roll in the local north-east-up frame, `vessel.SetWorldVelocity` (zero or `speedMps` along the heading), `vessel.IgnoreGForces(240)` around the move, Krakensbane velocity offset accounted for, one settle frame for KJR before physics resumes. Landing a vessel gently on terrain uses BDA's `VesselMover.Instance.PlaceVessel(v, skipMovingCheck:true)` when BDA is installed (`VesselSpawning/VesselMover.cs:445`); `PickUpAndDrop` (`:542`) backs `lift`/`drop`; `ksp.vessel.lift {vessel, m}`, `ksp.vessel.drop`; `ksp.vessel.teleport_orbit` via HyperEdit (P2). Formation placement of many vessels uses `bda.spawn_template` (`CustomTemplateSpawning.SpawnCustomTemplate`, `CustomSpawnTemplate.cs:95`) or `sim.scenario.load`. All `cheat`.

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
