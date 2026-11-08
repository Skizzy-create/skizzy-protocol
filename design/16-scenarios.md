# 16 End-to-end scenarios

**[R]** Ten walkthroughs (§0 added by the review: the first thing the user will do, available from M0/M1). Each lists the actors, the message flow (abridged wire calls), the latency budget, and which design elements it exercises. Times: `wall` = real time, `UT` = game time. Loopback RTT host↔client is 1–3 ms; an MCP tool call through the bridge adds ~5 ms; a Haiku 5.5 turn is typically 1.5–4 s wall, an Opus 5.5 turn 5–20 s with `high` effort.

## 0. First contact (M0/M1): connect, read, fly, screenshot, log — [R]

```
user installs the zip, starts KSP → first-run wizard: token created, preset Battle Lab, port 47549 OK, "Copy MCP command" → pastes into Claude Code
user (Claude Code): "what am I flying?"
agent → ksp_read {vessel:"active"}            → compact: name, situation, speed, altitude, parts 214, crew 2, AI none, WM none  (≈ 10 lines)
agent → ksp_camera_screenshot {mode:"chase", width:1024} → image (≤ 3 ms main thread)
user: "log altitude, speed and throttle at 5 Hz to CSV and climb to 3000 m at 200 m/s, heading 090"
agent → rec_log_start {name:"climb", select:{vessels:{active:true}}, fields:["altitude","srfSpeed","ctrlState.mainThrottle"], rateHz:5, format:"csv"}
agent → flight_hold {vessel:"active", heading:90, altitude:3000, speed:200}   → lease steer (agent, 600); hold installed
user grabs the stick → human.took_controls; hold suspended; lets go → resumes after 5 s idle
agent → flight_release {vessel:"active"}; rec_log_stop → Skizzy/logs/2026-11-08/climb.csv
user presses Ctrl+Alt+K at any point → everything Skizzy released, BDA pilot off on the active vessel, banner shown
no Claude Code: the same steps with `skizzy call ksp.read vessel=active`, `skizzy shot --mode chase`, `skizzy log start climb …`, or the in-game console (`21-user-experience.md` §3–4)
```

Budget: each call ≤ 50 ms end to end except the screenshot (≤ 300 ms); host idle cost after the session < 0.1 ms/frame. Exercises: install, first-run wizard, auth, catalog, Tier-0 read, screenshot pipeline, log v0, holds, arbiter and human override, kill switch, CLI.

## 1. Design and build an Arleigh Burke-style destroyer, iterate via test battles

Actors: user (Claude Code), builder agent (Opus 5.5, toolset `core+craft+bda+rec+sim`), in-game executors. Prereqs: `saves/skizzy-range` loaded, library with LBP/MNWS role packs.

```
user: "Design an Arleigh Burke-style destroyer for the mod pack, test it against a 20-missile raid, iterate until it survives 2 of 3."
agent → lib_blueprint_list {tags:["destroyer"]}                         → templates ddg-generic-v1
agent → ksp_parts_catalog {role:"vls"} / {role:"radar.air"} / {role:"ciws"} → MNWS MissleCell2050 (8 cells), SPY1D, 35mmAAMS …
agent → lib_blueprint_save ddg-burke-v1 (YAML as in 05-craft-building.md §3)
agent → craft_assemble {blueprint:"ddg-burke-v1"}                        → doc:3, assembly report (all slots filled, 412 parts)
agent → craft_validate {doc:"doc:3"}                                     → GM 0.62 m < 0.8 warning; draft 6.4 m; WM/AI ok; 2 EC warnings
agent → craft_apply {doc:"doc:3", ops:[move aft VLS lower by 1.2 m, add ballast tank role]} → craft_validate → GM 0.91 ok
agent → lib_craft_save {doc:"doc:3", name:"DDG Burke", version:"v1"}
agent → craft_trial {library:"DDG Burke@v1", profile:"naval.basic"}      (op, ~4 min UT at TIME_SCALE 3 ≈ 80 s wall)
      ← op.done: draft 6.6 m (pred 6.4), top 17.9 kt, turn circle 410 m, roll period 9.8 s
agent → sim_scenario_run {scenario:"ddg-vs-raid-20", vars:{blue_craft:"DDG Burke@v1"}, repeats:3, profile:"battle.standard"} (op)
      ← op.progress per cell …  op.done: survival 1/3, ESSM/kill 2.9, CIWS leakers 4, floods on cell 2
agent → rec_query {rec:"…", q:"missiles_in_flight_at max per track; time of first launch"}   → first launch at 38 km; too late
agent → lib_blueprint_patch ddg-burke-v2 {wm.guardRange 60000→90000, wm.maxMissilesOnTarget 2→3, wm.parallelSalvoSize 4→6, ai.MaxEngagementRange 8000, add second CIWS aft}
agent → craft_assemble/validate/save v2 → craft_trial → sim_scenario_run x3 → survival 3/3, ESSM/kill 2.1
agent → user: summary + lib_craft_compare v1 v2 + one ksp_camera_screenshot of v2 in the SPH
```

Budget: whole loop ~25–35 min wall for two iterations, dominated by battles at time scale 3; ~40 tool calls; ~$1.5–3 of Opus 5.5 with caching. Exercises: craft document, blueprint assembler, role catalog, validation (GM), trials, library versions, scenario runner, recorder queries, camera.

## 2. 3-ship SAG + submarine vs a 40-missile air raid; commander assigns air-defence sectors

Actors: human (map, god view), commander agent (Opus 5.5), AD coordinator (Haiku 5.5, group role), three unit agents (Haiku 5.5) on DDG-1/2/3, SSN-1 under a BT (`sub_shoot_and_scoot`), Red side under BDA guard mode with a scripted wave (`scenario director`). Fog-of-war for Blue agents.

```
t0   sim.scenario.load sag-vs-raid-40 (Blue: 3 DDG in screen_3 + SSN at 150 m; Red: 4 bombers + 40 ASM via waves at UT+120 s)
     c2.group.create g:sag-1 … ; c2.doctrine.load blue-naval-default ; orchestrator starts 5 agents, leases:
       commander: none (orders only); ad-coord: targeting+weapons on g:sag-1 (700); unit agents: ai_params+sensors on own ship (600)
t+30 UT  Red bombers detected at 160 km by DDG-1 SPY → track.new ×4 (UNK AIR) → digest to commander (compact, 6 lines)
     commander turn (12 s wall): tac_threats → c2_order_issue {type:"tactic", name:"ad_umbrella", group:"g:sag-1", threatAxis:"auto"}
                                               c2_order_issue {type:"weapons_tight", recipient:"g:sag-1"}; c2_order_issue {type:"emcon", recipient:"v:SSN-1", level:"passive"}
     planner expands: sectors 300–060 / 060–180 / 180–300 around centroid weighted toward bearing 045; per-ship defend_sector; missile_budget per_track 2
t+120 UT  40 ASM launched → missile.launch ×40 within 4 s; TPE classifies MSL/ASM, HOT, tcpa 95–140 s
     trigger "raid" already satisfied → no re-expansion (cooldown)
     AD coordinator wakes on missile.launch digest (first turn 3 s wall): c2_wta_solve {threats: 40 tracks, shooters: 3} → 40 assignments
       → bda_assign_targets ×3 (primary + secondary lists per ship), bda_fire_authority tight (already), report
     in-game: ExternalSecondaryTargets hook (F2) feeds targetsAssigned; parallel salvo (parallelSalvoSize 6, stagger 2) fires ESSM in batches
     unit agents wake on missile.inbound (each ~6 inbound): check ammo, bda_fire_countermeasures chaff when tcpa<15 s, report; one agent raises `maxMissilesOnTarget 2→3` on its own ship for leakers inside 10 km (ai_params lease allows)
t+150–220 UT  engagement.saturation events; CIWS auto (BDA turrets with targetsAssigned); 34 ASM killed by ESSM, 4 by CIWS, 2 hits on DDG-3 → unit.flooding (HullBreach) → DDG-3 agent orders damage control (P2: today: reports and reduces speed per doctrine)
     commander digest at t+210: losses, ammo state (ESSM 9/32 on DDG-1) → c2_order_issue {type:"missile_budget", per_track:1} ; asks human (c2_ask_human) whether to pursue bombers with the sub
t+300 UT  human on the map drags a route for SSN-1 toward the bombers' launch datum → move_route order at precedence 1000; BT keeps depth/emcon
end  rec.profile.stop → summary.md: missiles per kill 1.9, leakers 6, DDG-3 flood 120 t, agent cost $0.71 (commander $0.58)
```

Latency budget: missile launch → `missile.launch` event ≤ 200 ms (one TPE tick at 5 Hz); event → WTA assignments pushed ≤ 4 s (Haiku turn) — within the 95 s flight time; unit agent countermeasure decision ≤ 4 s against `tcpa` 15 s; in-game fallbacks mean nothing waits on an agent to fire (BDA guard fires regardless; agents shape *who/at what/how many*). Exercises: fog picture, triggers, tactics, WTA, hooks F1–F3, leases, budgets, ask-human, map route, recorder.

## 3. A Python script flies a 20-drone swarm while a Haiku agent re-tasks it

```
script (actor a:swarm-py, 450): lease steer+formation on g:swarm (20 drones, BDA pilot AI enabled but SteeringOverride via holds in "override" for formation legs)
  loop 20 Hz: mirror tracks (10 Hz deltas, msgpack layout tracks.v1, ~1.2 KB/frame for 15 tracks) → swarm law → flight.setpoint_batch (20 items, 1 msgpack frame ~700 B)
  host: fixed job applies 20 setpoints in ~40 µs; holds run at 50 Hz
Haiku swarm-tasker (actor a:swarm-ai, 600, tools core+tactical+c2): wakes on track.new within 40 km or every 60 s
  turn: tac_query hostile air BRAA from g:swarm → c2_order_issue {type:"patrol_area", recipient:"g:swarm", area:…} or {type:"engage_all", filter:{class:["drone"]}}
  the script has `claim`ed orders of type patrol_area/engage_all for g:swarm: it receives order.* events, changes its law, reports progress (c2.order.report)
  if the agent instead issues move_route without a claimant → in-game executor would need steer; LEASE_HELD (script holds at 450 < 600 → preemptible) → doctrine says agent may preempt → lease.preempted → script switches to monitoring mode and re-acquires when the order completes
kill switch at any time: holds released within one physics step; drones revert to BDA pilot AI (on_expire bda_ai)
```

Budget: script loop jitter ≤ 5 ms at 20 Hz on loopback; host cost ≤ 0.3 ms per physics step; agent cadence ≤ 2 turns/min ($0.05/battle). Exercises: msgpack streams, setpoint batch, mirror, claimed orders, preemption, dead-man, kill switch.

## 4. The human co-flies a jet while agents fly the wingmen

```
human in F-22 (active vessel), two wingmen with BDA pilot AI; wing agent (Haiku) holds formation+ai_params+targeting on both (600)
flight.formation {leader:"active", members:[…], template:"combat_spread", spacingM:600}  (WingCommander CommandFollow with slots via F7)
human flies normally: implicit steer lease on F-22 only; wingmen follow at 50 Hz in-game
human says in Claude Code / map: "Two, engage the bandit at BRAA 045/12" → c2.order.issue engage {recipient:"v:wing-2", track:"t:31"} at 1000
  executor: ReleaseCommand on wing-2 (leaves Follow), SetOverrideTarget (F1 sticky), fire_authority free, BDA AI engages; wing-3 keeps formation
  wing agent reports "Two engaging T31, Three on your wing" (compact note → in-game banner)
bandit kills wing-2's target → order achieved → doctrine: rejoin → flight.formation re-adds wing-2 (CommandFollow)
human takes a hit, wants to eject the AI: Ctrl+Alt+K → Skizzy holds off (there were none on F-22); wingmen keep BDA AI (default); banner shows state
```

Budget: order → BDA command ≤ 50 ms (one main-thread job); formation re-slot ≤ 1 s. Exercises: implicit human lease, formation executor, hooks F1/F7, in-game banner, voice-like command via text.

## 5. Carrier strike: launch, strike package, recovery

```
sim.scenario.load cvn-strike (CVN with ACA catapults/wires, 6 strikers, 2 escorts; Red SAG 180 km away under BDA guard)
commander (Opus) → c2.order.issue tactic strike_package {targets:[Red DDGs], strikers:g:strike-1, escorts:g:cap-a, ingress route via map or auto}
planner: c2_order_issue {type:"launch", carrier, aircraft list} → naval.carrier.launch per aircraft (ACA Catapult: taxi hold → hook → Launch → CommandTakeOff; 45 s cadence)
         move_route ingress (patched waypoints F6 for aircraft), escort orders, strike {weapon allocation: 2 ASM per DDG via missile_budget}, egress, return_to carrier
in flight: strike lead agent (Haiku) monitors tac_threats; Red SAM launches → missile.inbound → agent orders evasive leg (amend route) and continues
strike: BDA guard fires assigned ASMs (bda.assign_targets + fire_authority tight → free at release point via trigger "in_range")
recovery: c2_order_issue {type:"recover"} → naval.carrier.recover: marshal racetrack 10 km astern (loiter), one aircraft at a time to the glideslope hold, Hook.HookDeploy, wire capture (Arrestwire.IsWire) → taxi clear; v1 fallback: BDA AI "land" is unreliable, so recovery can be hand-flown by the human with the agent calling the pattern
```

Budget: launch cycle 6 aircraft ≈ 5 min UT; the deck state machine runs at 5 Hz in-game. Exercises: ACA adapter (P2), routes, WTA for strike, triggers, carrier ops state machine. Honest status: P2; v1 ships launch + marshal; arrested recovery is the hardest part (`18-risks-and-decisions.md`).

## 6. Submarine ambush with sonar-only tracks under fog-of-war

```
Blue SSN-1 (SubTrim, passive + active sonar as ModuleRadar sonarMode, torpedoes SLW), BT sub_shoot_and_scoot attached; Red convoy of 4 under BDA surface AI at 12 kt
TPE for Blue under fog: no radar → tracks come only from sonar; passive contacts are bearing-only (`pos:null`, bearing from SSN-1, class guess from acoustic signature → "SUR?")
sub agent (Haiku, sensors+targeting+ai_params on SSN-1) wakes on track.new: tac_query frame braa → "T41 UNK SUR? B032 R? bearing-only q.3"
  decision: hold passive, c2_order_issue set_depth 60 m (→ flight.depth via SubTrim manualDepth), move_to intercept bearing (c2.order.issue move_to computed from bearing drift via tac.intercept with range unknown → host returns a bearing-line intercept)
two more bearing-only tracks; TPE associates when bearings converge → after 3 ticks `track.merged`; still no range
agent: sonar active one ping (c2.order.issue sonar {mode:"active", pingS:5}) → ranged tracks (quality .8), Red gets `sonar_ping` event (their RWR-equivalent) → Red AI goes to evasion
BT: track_in_range torpedo → fire_depth → fire_authority free → 2 SLW away → hide depth 150 m → turn → reattack timer
Red: torpedo inbound → BDA evades, bubbles (FireBubbles) → missile.defeated or impact → unit.flooding on Red DDG (HullBreach) → founder event
```

Budget: passive detection → agent turn ≤ 4 s; active ping → ranged tracks within one TPE tick. Exercises: bearing-only tracks, association, sonar modes, SubTrim depth backend, BT, HullBreach events, fog IFF (`UNK` until hostile act).

## 7. A 200-vessel batch simulation run 50 times for A/B statistics

```
skizzy battle run library/scenarios/fleet-200.yaml --matrix "salvo=off,on" --repeats 25 --profile battle.light --resume
preflight: hooks ok, PRE 150 km (set by scenario), library versions pinned, 2 teams × 100 vessels (DDG/CG/CVN/subs/fighters/drones from the library)
per cell: remove all (two-key: armed batch mode) → spawn template (25 s) → settle 60 s → referee competition (scoring only) → waves none → end when one team < 20 % or UT 15 min at TIME_SCALE 2 (≈ 8 min wall) → stop → scores/log/KSP.log window → cell summary
host load: TPE god view 200 vessels 5 Hz ≤ 0.6 ms; recorder battle.light (tracks 2 Hz, missiles 5 Hz, scores, events, perf) ≤ 0.3 ms; no agents
50 cells × ~10 min ≈ 8–9 h unattended; checkpoint after each cell; user restarts KSP if memory climbs (soak gate warns); runner resumes
skizzy rec compare runs/<ts>-fleet-200 --metric missiles_per_kill --metric survival --metric frame_ms_p99
  → salvo on: missiles/kill 2.3 → 1.8 (95 % CI [−0.7, −0.3]); survival Blue 0.44 → 0.57; p99 frame 41 → 44 ms
```

Exercises: scenario loader, batch runner, two-key, recorder budgets, metrics, statistics, resume.

## 8. The user draws a route on the tactical map and an agent executes it

```
map: operator selects DDG-2, Route tool, clicks 5 waypoints, sets 14 kt, double-click → c2.order.issue {type:"move_route", recipient:"v:DDG-2", waypoints:[…], speed:7.2, priority:1000}
host: validate (zones: leg 3 crosses z:minefield stay-out → ROE_VIOLATION with the offending leg) → map highlights leg 3; operator drags waypoint → re-issue ok
executor (ship, BDA surface AI): lease steer at 1000 preempts the group agent's patrol order (lease.preempted → agent gets event, acknowledges), CommandFlyTo leg 1, CruiseSpeed 7.2; progress events per leg; map draws progress
group agent (Haiku) wakes on order.* for its unit: tac_sitrep → notes the human route, re-plans its screen around it (c2.order.issue formation for the other two ships relative to DDG-2)
route achieved → order.achieved → lease released → agent's patrol order resumes (stacked/suspended) → map shows status
```

Budget: draw → order visible to agents ≤ 300 ms; leg transitions evaluated at 5 Hz. Exercises: planning tools, ROE/zone validation, preemption, route executor, agent re-planning.

## 9. The user logs arbitrary fields at 10 Hz, then queries them after the battle

```
user (Claude Code): "log flooded fraction and inflow of every HullBreach part on Blue ships, plus each ship's guard target and missiles away, at 10 Hz, for this battle"
agent → rec_profile_save {name:"flood-study", channels:{flood:{source:"query", selector:{vessels:{team:"Blue"}, modules:{type:["ModuleHullBreach"]}}, fields:["floodedDisplay","inflowDisplay"], rateHz:10},
                                                       wm:{source:"query", selector:{modules:{type:["MissileFire"]}}, fields:["currentTarget","firedMissiles"], rateHz:10}, events:{names:["unit.*","missile.*"]}}}
agent → rec_profile_start {profile:"flood-study"}        → rec:2026-11-08-...; host: sampling 10 Hz over ~3 ships × 40 parts = 120 module reads/tick via compiled accessors, ≤ 0.15 ms
battle runs 20 min; rec_profile_stop
agent → rec_query {rec:"…", q:"per vessel: max floodedDisplay, time of first inflow>0, total firedMissiles delta"} → compact table (12 lines)
user → "give me a CSV" → skizzy rec export --format csv --channels flood → Skizzy/recordings/…/flood.csv ; python: skizzy.rec.open(...).to_pandas()
```

Budget: 10 Hz × 120 fields = 1200 values/s ≈ 15 KB/s; chunk writes every 5 s; query over a 20-minute file < 1 s. Exercises: profiles, selectors, compiled accessors, `.skzrec`, query, export.

**[R] Before M2** the same request works with log v0: `rec_log_start {name:"flood-study", select:{…}, fields:[…], rateHz:10, format:"csv"}` writes `Skizzy/logs/<date>/flood-study.csv` directly, `rec_log_tail` shows the last rows, and the user opens the CSV without any export step (`12-logging-recording-replay.md` §1.1).

## 10. Cross-cutting latency table

| Path | Budget | Mechanism |
|---|---|---|
| Physics event (launch, detection) → event on the wire | ≤ 200 ms (one TPE tick at 5 Hz); ≤ 50 ms at 20 Hz | FixedUpdate sampling, LateUpdate batch |
| Script setpoint → control surface | ≤ 25 ms (RTT + next physics step) | fixed-affinity job + hold |
| Order issue → BDA command applied | ≤ 50 ms | main-thread job |
| Order issue → visible on all maps | ≤ 300 ms | event batch + 5 Hz map stream |
| Haiku unit decision | 1.5–4 s wall | event-driven turn, cached prefix |
| Opus commander decision | 5–20 s wall | digest-driven, slow-mo on urgent triggers |
| Kill switch → controls released | ≤ 20 ms (one physics step) | main-thread flag read by the mixer |
| Screenshot | **[R]** ≤ 3 ms main thread (`AsyncGPUReadback`; ≤ 30 ms on the `ReadPixels` fallback), ≤ 300 ms to the agent | end-of-frame capture, async readback, writer-thread JPEG encode |
