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

The mixer is a single `FlightInputCallback` per vessel, pre-allocated, zero-alloc, enabled only while needed (RB3). Ordering caveat: BDA's `ActivatePilot()` does `OnFlyByWire -= autoPilot; += autoPilot` (`BDGenericAIBase.cs:177-180`), which moves BDA to the end of the multicast list. The mixer therefore re-registers itself (remove + add) when it observes a `pilotEnabled` rising edge on the vessel's AI, and once per second as a safety net; with the fork hook `BDGenericAIBase.SteeringHook` (`07-bdarmory-integration.md` §5) ordering stops mattering.

## 2. Direct controls (P0)

`flight.state`, `flight.set_controls {throttle, sas, sasMode, rcs, gear, brakes, lights}`, `flight.action_group`, `flight.stage` (destructive), `flight.switch_vessel`: as PLAN v0.2 §8.2. `flight.set_controls` on a non-active vessel writes through the mixer (one-shot setpoint held until released).

## 3. Setpoint holds (P0)

`flight.hold {vessel, heading?, pitch?, roll?, altitude?, speed?, depth?, verticalSpeed?, bank?, limits?, gains?, waitFor?:"settled"}` installs a hold on the `steer` channel (acquiring the lease, or failing with `LEASE_HELD`). Controllers are cascaded PIDs with per-class default gains (plane, rocket, rover, boat, submarine), mass/inertia scaling, output slew limits and anti-windup, as in PLAN v0.2 §8.2. Additions:

- **Boat heading hold** uses yaw + `wheelSteer`, with a rudder-authority estimate from speed; **speed hold** uses `mainThrottle` + `wheelThrottle`, with reverse allowed when `allowReverse:true`.
- **Depth hold** (`depth`) has three backends, chosen per vessel: SubTrim (`SkizzyDepthSource : IDepthCommandSource`, registered via `SubTrimRegistry.Register`, so SubTrim's own trim/ballast controller does the work and Skizzy only supplies `CommandedAltitude()` — allocation-free as the API demands), KP Naval `ModuleShipBallast.targetDepth`, or pitch-only (dive planes) as a last resort. The SITREP reports which backend is active.
- **Setpoint batch** `flight.setpoint_batch {items:[{vessel, heading, speed, altitude}...]}` applies setpoints for many vessels in one message (msgpack) for swarm scripts; one `fixed` job, O(n).
- `flight.holds` lists holds with errors, saturation and settled state; `flight.release {vessel|all}` restores the pre-hold SAS state.

## 4. Direct control channel (P1) — [ADDED]

`flight.direct {vessel, pitch, yaw, roll, throttle, wheelSteer, wheelThrottle, ttlMs:200}` writes raw axes for one tick window. A **dead-man**: if no update arrives within `ttlMs` the mixer holds neutral and after `releaseAfterMs` (default 2000) releases the lease to the fallback (`bda_guard` by default). Rate cap 50 Hz per vessel. Intended for external autopilots and research; the SDK docs state the loopback latency (~1–3 ms) and the physics step (20 ms) so users understand the loop they are closing.

## 5. Route executor (P0)

`flight.route {vessel|group, waypoints:[{lat,lon,alt?,speed?,radiusM?}], mode:"transit"|"patrol"|"racetrack", loop, finalHeading?}` creates a `move_route` order executed in-game:

| Vessel kind | Mechanism |
|---|---|
| BDA surface AI (ships, subs) | sequence `CommandFlyTo(gps)` per waypoint (`BDGenericAIBase.cs:443`), advance when within `radiusM` (default 200 m) or when `AIUtils.TimeToCPA` says the point is passed; speed via `CruiseSpeed`/`MaxSpeed` fields (restored at the end); depth via the depth hold |
| BDA pilot AI (aircraft) | with the fork patch `SetWaypoints(points, radius, laps, speeds)`: BDA's own waypoint mode; without it: Skizzy heading/altitude/speed holds in `override` mode while BDA guard mode keeps firing (weapons channel untouched); on `engage` orders control returns to BDA (`ReleaseCommand`) |
| Skizzy-only vessel (no BDA AI) | holds |

Why the patch: stock `SetWaypoints` reads `WaypointCourses.CourseLocations[waypointCourseIndex].waypoints[i].scale/maxSpeed` (`BDGenericAIBase.cs:572, 603`), so an arbitrary list indexes a course that does not match it. The `PilotAI` `FlyTo` command only *orbits* the point at a hard-coded 2500 m (`BDModulePilotAI.cs:5335`), which is useless for transit legs; the patch adds `commandOrbitRadius`.

Routes drawn on the map (`11-tactical-map.md` §6) and routes from agents are the same object (`c2.order.issue type:move_route`).

## 6. Formation executor (P0)

- **BDA-native formation:** a lead vessel's `ModuleWingCommander` with `CommandFollow(leader, index)` (`BDGenericAIBase.cs:424`); the follower's AI computes its slot from `leader.GetFormationPosition(index)` (`ModuleWingCommander.cs:694`, default spread/lag ladder) in the leader's velocity frame (`BDModulePilotAI.cs:5422`; surface AI has the equivalent path at `BDModuleSurfaceAI.cs:841`). Both aircraft and ships support Follow. `flight.formation {leader, members:[{vessel, slot|offset:{right,back,up}}], template:"line_abreast|line_ahead|wedge|diamond|screen|box", spacingM}` sets slots. Setting arbitrary slot offsets needs the fork patch `ModuleWingCommander.SetFormationPosition(int, Vector2)` because `formationIndexToPosition` is private (`ModuleWingCommander.cs:687`); the shim falls back to the default ladder with `spread`/`lag` tuned to approximate the template.
- **Skizzy station keeping** for vessels under Skizzy holds (or when the leader is human-flown): a per-member PID on the slot position in the leader's frame, feeding heading/speed/altitude holds; used for naval screens around a carrier at 10–20 kt and for human-led flights (the wingman scenario).
- Templates are doctrine objects; `screen` places escorts on threat-axis-weighted bearings around the main body.

## 7. Hand-off (P0)

`flight.handoff {vessel, to:"manual"|"skizzy"|"bdarmory"|"atmosphereautopilot", aaMode?}`:
- to `bdarmory`: release Skizzy holds, `vessel.ActiveController().SetActiveAI(ai)` / `ActivatePilot()` (`VesselModuleRegistry.cs:1125`, `BDGenericAIBase.cs:172`); guard mode is untouched (weapons channel).
- to `manual`: release holds, `DeactivatePilot()`, restore SAS; the human lease is implicit.
- to `atmosphereautopilot`: `TopModuleManager.activateAutopilot(typeof(StandardFlyByWire|CruiseController))` via reflection, failing closed; Skizzy can then drive AA's `desired_altitude`/`desired_course`/`desired_velocity` as a hold backend for aircraft (smoother than our PIDs on stock aero).
- `flight.state.controller` reports `manual|skizzy|bdarmory|atmosphereautopilot|mixed` and the lease holder.

## 8. Human and agent co-flying (P0)

- The pilot's inputs are read from `FlightInputHandler.state` each step; a deviation beyond a dead-band on any axis for 3 consecutive steps raises `human.took_controls` and grants the implicit human lease (precedence 1000) on `steer` for the active vessel; agent holds on that vessel are suspended (not cancelled) and resume after `humanIdleS` (default 5 s) without input, emitting `human.released_controls`. Configurable to "never resume" per doctrine.
- Agents flying wingmen never touch the active vessel unless the human explicitly grants it (`c2.lease.grant {vessel:"active", to:"a:wing-ai", channel:"steer"}` from the in-game window or the map).
- The in-game HUD shows a one-line banner: `AI: wingmen 2 (follow) · you: manual · Ctrl+Alt+K stops all`.
- Voice/text commands from the human to wingmen go through the same orders (`c2.order.issue` from the in-game command window, the map or Claude Code).

## 9. Scene setup and positioning (P0)

`ksp.vessel.place {vessel, lat, lon, alt?, heading, pitch?, roll?, aboveTerrain?}` uses BDA's built-in VesselMover (`VesselSpawning/VesselMover.cs:445 PlaceVessel`, `:542 PickUpAndDrop`), handling Krakensbane/floating origin correctly; `ksp.vessel.lift {vessel, m}`, `ksp.vessel.drop`; `ksp.vessel.teleport_orbit` via HyperEdit (P2). Formation placement of many vessels uses `bda.spawn_template` (`CustomTemplateSpawning.SpawnCustomTemplate`, `CustomSpawnTemplate.cs:95`) or `sim.scenario.load`. All `cheat`.

## 10. Kill switch (P0)

Hotkey (default `Ctrl+Alt+K`), toolbar button, map button, `host.kill_switch` capability (any actor with `control`), and the in-game banner. Effects within one physics step: deny all non-read calls; cancel operations; reject pending confirmations; release every Skizzy hold and direct lease; restore SAS state; **leave BDA AI and guard mode as they were** (the user's fleet must not go dead in the water) unless `alsoDisableBdaAi:true`; optionally pause; emit `host.kill_switch`; show banner until resumed from the UI.

## 11. Carrier operations (P2) — [ADDED]

A `naval.carrier` service wraps ACA: `launch {carrier, aircraft}` = taxi-to-catapult hold (station keeping relative to the catapult transform), hook/attach (`Catapult` module), throttle up, `Launch`, then hand-off to BDA AI with `CommandTakeOff`; `recover {carrier, aircraft}` = marshal (racetrack hold astern), final approach hold on the glideslope to the deck point, `Hook.HookDeploy`, wire capture (`Arrestwire.IsWire`), taxi clear; deck state machine stream (`idle|launching|recovering|foul`). Hangar storage via `HangarMachinery` when present. The AI quality of the final approach is the risk; the scenario in `16-scenarios.md` §5 limits v1 to launch + marshal + hand-flown or BDA-assisted recovery.

## 12. Acceptance (summary)

1. Plane hold converges and holds (PLAN tests); boat heading ±5°, speed ±1 m/s; sub depth ±3 m with SubTrim, ±8 m without.
2. Route of 6 waypoints for a destroyer at 15 kt completes with ≤ 200 m cross-track error at waypoints; aircraft route via holds and via patched waypoints both complete.
3. `flight.formation` puts 4 escorts in a `screen` within 10 % of spacing after 120 s and keeps station during a 30° turn.
4. Human takes the stick: `human.took_controls` within 60 ms; agent hold suspended; resumes after 5 s idle.
5. Kill switch releases 20 scripted drones within one physics step; BDA guard remains active.
6. Twenty vessels on `flight.setpoint_batch` at 20 Hz cost ≤ 0.3 ms per physics step.
