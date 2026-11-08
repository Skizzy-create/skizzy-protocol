# 17 Milestones

**[R] Rewritten by review pass 1.1; [R2] re-scoped by pass 2** (the command-model change, communications and diplomacy, spawn anywhere: `19-review.md` §9). Every milestone ends with something the user can try in KSP. Each one is sized so that a single Opus 5.5 coding agent can implement it from this design plus the previous milestone's code, working through PR-sized tasks in the listed order (spikes first, then acceptance tests, then features). Every entry lists scope, the in-game demo, acceptance tests, performance gates and the governing design files. Estimates assume one developer plus coding agents, part-time.

**No code exists yet.** Pass 1.0 said PLAN v0.2's M0/M1 code "was built offline"; the repository holds only `IDEAS.md`, `PLAN.md`, `PROTOCOL-SPEC-DRAFT.md` and `design/` (checked 2026-11-08). M0 starts from an empty solution.

**Rules for every milestone:** the robustness and performance rules of `02-architecture.md` §2–3 and §8 apply; `host.selfcheck.perf` with the gates in `02-architecture.md` §8.3 is part of every acceptance list, and a failed gate blocks the milestone; the principle in `00-overview.md` (BDArmory unchanged and zero cost with nothing connected) is re-checked in every milestone; each feature is reachable from scripts, agents, the map (once it exists) and the game (`21-user-experience.md` §3).

**Why this order (changed from pass 1.0).** Pass 1.0 put flight control in M2, screenshots nowhere, logging and the map in M3, which meant the user could not fly, see or log anything for two to three months. The new order gives connect, read, screenshot and log in M0 and flying in M1, then BDA and the compact picture, then the map. The order after M2 (map, then control depth, then craft) is a default the user can change (`18-risks-and-decisions.md` §3).

**[R2] What pass 2 changed.**

- **Spawning moves into M1.** The user wants it right away: spawn anywhere (air, sea, submerged, ground, runway), multi-spawn with any count, rows and clear-space search, formations, and the in-game Spawn window. Map drag-spawn follows in M3, waves and respawn in M6.
- **M4 becomes "Command basics".** It adds the command seat, delegation to client-spawned sub-agents, automatic reports, the team channel and comms panel v1. It grows from 5 to 6 weeks.
- **M6 becomes "Missions, doctrine and stance".** The built-in orchestrator and the commander-plus-unit-agents demo are gone. In their place come missions and their planners, full doctrine, stance and surrender, the API-key example and the scenario editor. It grows from 5 to 6 weeks.
- **M7 adds diplomacy.** The diplomatic channel, negotiation, psyops and the agent-vs-agent scenario come in M7, which grows from 4 to 5 weeks.
- **M8 adds comms realism.** Comms range, relays and jamming come in M8. Agent memory and the analyst role move to Later (P3).
- **New "Later (P3)" section** for the deferred built-in agent layers.

| M | Theme | What the user can do after it | Est. |
|---|---|---|---|
| M0 | First contact | connect any MCP client (Claude Code and Codex checked), a Python script or the CLI; read any vessel/part/module; take screenshots in any camera mode; log any field to CSV/JSONL; use every capability from an in-game console | 3 wk |
| M1 | **[R2]** Fly, place and spawn | fly planes, ships and subs with holds; co-fly with the human override; kill switch; place vessels anywhere; **spawn anywhere** (mid-air at speed, at sea, submerged, ground, runway), **multi-spawn any count** in lines that wrap into rows with clear-space search, formations and swarms in one call, from scripts, agents and the in-game Spawn window; write module fields; basic BDA AI/guard/team control | 4 wk |
| M2 | BDA core, compact picture, recorder v1 | configure WMs (multi-target/salvo), run competitions, ask "what's the threat" and get a ten-line compact answer, assign targets, record a battle and query it | 5 wk |
| M3 | Tactical map v1 | watch battles on a dockable command screen with 2525 symbols where every one of 40 aircraft and 128 missiles stays individually visible and readable; fog/god windows; replay and slow-motion scrubbing; **[R2]** drag-spawn from the map library with count, altitude and speed; in-game overlay v1 | 5 wk |
| M4 | **[R2]** Command basics | take the command seat (human or agent) and hand it over; leases on all channels, orders, routes, formations, fire authority, take-over; let the AI client spawn sub-agents with cluster leases; get automatic reports; talk on the team channel from the map's comms panel; EMCON and basic doctrine; swarm scripts with the async SDK; plan on the map | 6 wk |
| M5 | Craft building v1 and analytics | blueprint → ship → trials → library; SQL over recordings; metrics and comparisons | 5 wk |
| M6 | **[R2]** Missions, doctrine and stance | run a force through CMO-style missions (patrol, strike, escort) with full doctrine, triggers, tactics and WTA; broadcast, ceasefire, surrender and violations; waves and respawn; behaviour trees; the API-key commander example; the scenario editor | 6 wk |
| M7 | **[R2]** Batch, CI and diplomacy | unattended 200-vessel × 50-run A/B; lockstep; full FakeKsp CI; the diplomatic channel, negotiation and psyops; agent-vs-agent battles (Claude Code vs Codex) | 5 wk |
| M8 | Naval depth and extensibility | sub and carrier operations, damage control, **[R2]** comms range, relays and jamming, orbit spawn, mod extension points, training export, parallelism where measured | 6 wk |
| M9 | Hardening and release | CKAN, docs, PyPI, upgrades, security gate, 4-hour soak, spec 1.0 | 4 wk |
| Later | **[R2]** P3 | built-in group and unit agents, a built-in orchestrator, per-role models and budgets, agent memory, the analyst role, automatic surrender by doctrine | — |

Total ≈ 49 weeks (**[R2]** pass 1.1 had 45; pass 2 adds one week each to M1, M4, M6 and M7). P0 features are all delivered by the end of M4, P1 by M7, P2 by M9 (`01-feature-catalog.md`).

## M0 — First contact (3 weeks)

Spikes first: (1) `ImageConversion.EncodeArrayToJPG` off the main thread; (2) `AsyncGPUReadback` availability on the user's graphics API; (3) `UnitySynchronizationContext` idle wake; (4) build against KSP's `Managed` folder with `NoStdLib`.

Scope:
- Solution layout per `02-architecture.md` §1 with the net48 build guard and CI (unit, host core, `Skizzy.Sim` stub, allocation asserts on codec paths).
- Host: `KSPAddon` bootstrap; own RFC 6455 WebSocket server over `TcpListener`; token/`Origin`/`Host` checks; JSON-RPC text frames (msgpack arrives in M2); `session.hello` with `actor.kind`/`client.kind` and precedence clamp; dispatcher with budgets and idle wake; catalog with search/describe; port fallback and `PluginData/endpoint.json`; reconnect with `resume`; unified error taxonomy (`03-protocol.md` §9).
- Reading: `ksp.vessels.list`, `ksp.vessel.get` (alias `ksp.read`), `ksp.query`, `ksp.module.describe|get`, `ksp.mods.list`, `ksp.log.tail`; sampled and on-change streams for query selectors; events ring with `vessel.*`, `sim.*`, `host.*`.
- Screenshots and camera (`13-vision-camera.md` §1): capture pipeline, modes follow/orbit/chase/free/look-at, `ksp.camera.save`.
- Log v0 (`12-logging-recording-replay.md` §1.1): `rec.log.start|stop|list|tail`, `rec.get_file`, CSV/JSONL.
- Safety: permission file with the Battle Lab preset and `ROLE` rules, in-game `ask` prompts, audit JSONL, kill switch stub (deny non-read calls, banner).
- In-game UI (`21-user-experience.md` §3): first-run wizard, connections/permissions window, capability console, recorder window with the **Log…** picker, camera window.
- Outside: bridge `skizzy mcp` with the `core` toolset, output caps and **[R2]** actor options (`--actor`, `--role`, `--side`; `09-c2-multi-agent.md` §8.1); CLI `skizzy call|watch|log|shot|doctor` (doctor v0: token, port, versions, KSPCF, CommNet); Python `skizzy` 0.1 sync client; README quickstart.
- `host.stats`, `host.selfcheck` (idle cost, dispatcher timing, heap slope, texture counts).

Demo: scenario `16-scenarios.md` §0 without the flying part: from Claude Code, read the active vessel, take a chase screenshot, log three fields at 5 Hz to CSV and open it in Excel; repeat the same with `skizzy` CLI commands and with the in-game console.

Acceptance:
1. Nothing connected: < 0.1 ms/frame and heap slope equal to the baseline over 10 minutes; no `OnFlyByWire` callback, Harmony patch or engine tick registered (self-check lists them); a BDA competition runs identically with and without the plugin (same scores in 3 runs, within normal variance).
2. `hello` → `ksp.read` RTT < 20 ms p50 in Flight; every M0 capability callable from MCP, CLI, Python and the in-game console.
3. 1024 px JPEG ≤ 3 ms main thread (≤ 30 ms on the fallback); 100 captures leave `Texture2D`/`RenderTexture` counts unchanged; all five camera modes work.
4. Log v0: 100 vessels × 3 fields at 10 Hz for 10 minutes costs ≤ 0.2 ms per FixedUpdate; the CSV opens in Excel; the in-game picker builds the same selector as the CLI.
5. Five scene switches and one KSP restart with a connected client: no exceptions, no object growth, the client reconnects automatically and gets `NOT_FOUND`/`stale` for old handles.
6. `Origin`-bearing, token-less and bad-token connections refused (automated); **[R2]** an `advisor` actor asking for god view gets `PERMISSION_DENIED`/`god_view`.
7. A clean install to a working Claude Code connection takes < 5 minutes following the README (manual). **[R2]** Codex CLI connects through the same `skizzy mcp` command and gets the same tool list (any MCP client, `IDEAS.md` §15).
8. Perf gates: idle, screenshot, log v0.

Governing files: `02`, `03` §1–3, §9, §11, `04` §2, `12` §1.1, `13`, `15`, `21`.

## M1 — Fly, place and spawn (4 weeks) — [R2]

Spikes first: `OnFlyByWire` ordering with BDA's re-registration; `SetPosition`/`SetWorldVelocity` placement with KJR installed; **[R2]** spawn settling at sea and submerged with KJR (buoyancy settle, `settleS`); initial airborne velocity without BDA's `VESSEL_SPAWN_INITIAL_VELOCITY` path, and keeping guard on (BDA's `SpawnUtils.AirborneActivation` turns it off); calibrating the large-spawn performance estimate (ms of physics per part) on the user's machine.

Scope:
- Flight engine (`06-flight-control.md`): mixer; `flight.set_controls`, `action_group`, `stage` with the staging resolver, `switch_vessel`, `orbit_point`; holds (plane, rocket, boat heading/speed, submarine depth via SubTrim `manualDepth`, pitch-only fallback); `flight.setpoint_batch` with `deadmanMs`; `flight.direct` with dead-man; `flight.release`, `flight.holds`.
- Arbiter with `steer` leases (acquire, release, TTL, preempt, `onExpire`), implicit human lease with trim-relative axes and throttle-change detection, active-vessel switch handling (`20-runtime-lifecycle.md` §2).
- Hand-off to BDA / manual / AtmosphereAutopilot with `pilotEnabled` checks; kill switch full semantics including the active-vessel BDA pilot rule.
- Placement: `ksp.vessel.place|lift|drop`, PRE range get/set via BDA's helper.
- **[R2] Spawn anywhere** (`06-flight-control.md` §9.1):
  - `ksp.vessel.spawn_craft` with modes `air` (altitude, heading, pitch, airspeed, engines, throttle, gear), `sea` (waterline and buoyancy settle), `submerged` (depth), `ground` and `runway`; team, guard, AI, group and mission assignment;
  - **multi-spawn**: `count` with no cap, `layout:"line"`, abreast or trail, `spacingM` auto or metres, `perRow`; clear-space search against terrain, buildings, water/land and existing vessels by bounding size plus margin; lines that wrap into parallel rows; the `large_spawn` warning with a performance estimate above 50;
  - `ksp.vessel.spawn_formation` with templates (wedge, line, echelon, box, screen) for swarms;
  - the staggered spawner (≤ 400 parts per frame);
  - the in-game **Spawn** window (craft picker, mode, count field, spacing, rows, team, guard, AI, group, warning); CLI `skizzy spawn …`; MCP tool in the `core` and `flight` toolsets.
- **[R2] Fork feature OD1** (BDArmory Overdrive multi-spawn in the VesselMover spawn window, `07-bdarmory-integration.md` §5.1) as a separate PR to the fork. It is P1 and does not block M1. Skizzy's own spawner does not depend on it.
- Writes: `ksp.module.set|invoke|action` through the PAW path; confirmations; `ksp.vessel.rename` (control).
- Deep reflection read (`reflect.get`, enabled per mod in-game only).
- BDA minimal: `bda.status`, `list_teams`, `set_team`, `set_guard_mode`, `list_ai`, `get_ai`, `set_ai_mode`, `command_ai`.
- Vessel lifecycle states and warp/pause policy for holds (`20-runtime-lifecycle.md` §1, §3); `sim.time.set|slow` (BDA time scale) and `sim.clock`.
- In-game flight window (holds, lease holder, human-override banner); MCP `flight` toolset; CLI `skizzy fly …` helpers.

Demo: scenario §0 complete, including "spawning on day one" (six destroyers in a line at sea, twelve fighters abreast at 3 km and 250 m/s); 60 drones from the in-game Spawn window wrapping into rows with the warning shown; a 20-drone wedge in one call; a SubTrim sub spawned at 80 m holding depth; hand a jet to BDA AI and back.

Acceptance:
1. `06-flight-control.md` §12 items 1, 4, 5, 7, 8, 9, 10 and **[R2]** 11–14 (spawn mid-air, sea and submerged, multi-spawn with rows and the warning, swarm formation).
2. Pausing and unpausing keeps a BDA time scale of 2; holds on a vessel that goes on rails report `vessel_packed` and resume.
3. A hold set by an agent survives 60 s of agent silence; a `setpoint_batch` stream with `deadmanMs:500` goes to `hold_last` after 500 ms of silence.
4. After the flown vessel dies and KSP switches, holds on the new active vessel keep running until the human touches a control.
5. **[R2]** The same multi-spawn (count 12, line, air) gives the same slots from the API, the CLI and the in-game Spawn window.
6. Perf gates: 20 holds ≤ 20 µs each per step; arbiter + mixer ≤ 0.3 ms per step for 20 vessels; **[R2]** a 100-vessel staggered spawn keeps the worst frame ≤ 1.5× the median, and clear-space search for 100 slots takes ≤ 5 ms.

Governing files: `06` (incl. §9.1), `09` §4, `20` §1–3, `04` §5 (placement, SubTrim, PRE), `07` §1.1–1.3, §5.1.

## M2 — BDA core, compact picture, recorder v1 (5 weeks)

Spikes first: VRD `displayedTargets` reflection cost at 200 radars; `TargetInfo.detected` semantics for allied teams; BRAA test vectors shared with Python; in-house msgpack codec zero-alloc on Mono.

Scope:
- `Skizzy.BDArmory`: the PLAN tool set (spawn ring/template/single, remove, WM configure incl. multi-target and salvo, AI commands, competition start/stop/state/scores, radar contacts, missiles in flight, damage report, settings, log tail, countermeasures, fire tools at `destructive`/ask); `bda.hooks`; `bda.assign_target` with the re-issue executor (`07-bdarmory-integration.md` §1.4 "Override semantics"); formation slots by reflection; `bda.dlz`, `bda.ammo`, `bda.ai_status`; competition-as-referee start; name deconfliction at competition start.
- Fork patches **F1, F2, F5** as separate PRs to `BDArmory-Skizzy` (coordination per `18-risks-and-decisions.md` §3), with shim fallbacks so M2 does not depend on them merging.
- TPE v1 (`08-tactical-picture.md`): time-sliced tick, fog/god, IFF, classification v1, quality/age/DR, packed/unloaded/debris handling, frames, `tac.query|summary|sitrep|threats|engagements`, subscriptions with deltas, compact text, output caps and pagination; zones and bullseyes.
- Events `track.*`, `missile.*`, `engagement.*`, `unit.*`, `bda.*` and the bridge's `tac_wait_events`; msgpack encoding negotiated per session; conformance vectors.
- Recorder v1 (`12-logging-recording-replay.md` §2–4): profiles, `.skzrec` writer/index/repair, `events.jsonl`, CSV/JSONL sinks, host-side `rec.query`, `rec.mark`, `skizzy rec export --csv|jsonl`.
- Synthetic perf mode `host.selfcheck.perf --synthetic N`.

Demo: from Claude Code, spawn `AutoSpawn/skizzy/sag` vs `raid`, set teams and guard, start a competition, ask "what's the biggest threat to DDG-1 right now?" and get `tac.summary` plus a top-10 BRAA list; assign a target; stop; query max flood per ship from the recording with the bridge closed.

Acceptance:
1. PLAN M1 acceptance items 1–5 (spawn → scores round trip, hidden tools denied, salvo clamp read-back, kill switch cancels spawn, scene change safe).
2. `08-tactical-picture.md` §12 items 1, 2, 3, 4, 6, 7.
3. `bda.hooks` reports `fork` on the user's install and `reflection` on a stock BDA Plus install; in reflection mode an assignment survives a missile routine (re-issued) and `assignment_lost` is reported after three overwrites by team assist.
4. `12-logging-recording-replay.md` §9 items 1, 2, 6.
5. Two spawned craft named "DDG" become "DDG" and "DDG #2" before the competition starts; scores have two rows.
6. Perf gates: TPE at 100 and 200 loaded vessels, synthetic 1000, allocation gate, 30-minute memory gate.

Governing files: `07`, `08`, `03` §4–8, `12` §2–4, `20` §6–7.

## M3 — Tactical map v1 (5 weeks)

Spikes first: `Dock.Avalonia` on Avalonia 11 (licence, serialisation); Skia `DrawAtlas` throughput on the user's GPU; Avalonia headless rendering in CI.

Scope (`11-tactical-map.md`):
- Connection, `TrackMirror`, history rings; `symbols.json` and procedural 2525D symbols with the SIDC unit test.
- Clarity at scale (§2.1): individual tracks always; density- and zoom-aware sizing with the symbol → small symbol → dot tiers; RCS size hierarchy and the alternative size modes; type/team/item/selection overrides; altitude labels and ticks; trails and velocity leaders; missile glyphs and target lines; intercept/impact/kill markers; label declutter with leader lines; filter chips; hover/selection highlight and focus dimming; the optional Counts view.
- Layout (§5.1) and zoom (§4.1): resizable window, docking and detachable panels, saved named layouts, smooth zoom/pan, fit-to-selection, fit-all.
- Layers (§3), fog and god windows, panels: track list, unit details, orders (read-only), events/log, layers, recorder, host health; command palette.
- Replay through `skizzy rec replay-server` with timeline scrubbing, 0.1×–16× speed, event stepping, loops and interpolation; the `battle.detailed` profile; live slow motion from the map (last task; may slip into M4 without blocking).
- In-game overlay v1 (§8); zone creation from the map (`tac.zones.*`).
- **[R2] Drag-spawn from the map library** (`11-tactical-map.md` §6, `06-flight-control.md` §9.1): drag a craft, set mode, altitude, speed, heading and count on the spawn card, see the slot and row preview and the large-count warning; Shift-drag for a formation.
- Genre features (§12): message log with category filters, time compression bar and pause-on-event, unit details with the magazine view, live losses & expenditures panel, range rings (weapon, sensor, DLZ, threat), emission badges, DR uncertainty ellipses, `idState` display.

Demo: drag 40 fighters onto the map in two groups (count 20 each, 4 km, 250 m/s) and watch a Blue-vs-Red battle with 40 aircraft and 128 missiles on Blue-fog, Red-fog and god windows, every missile visible on its own path; drag the track list to a second monitor; restart the map and get the same layout; replay the densest 30 seconds at 0.25× and step through the intercepts.

Acceptance:
1. `11-tactical-map.md` §11 items 1, 3, 5, 6, 7, 8, 9, 11; the message log filters and jumps; pause-on-event "missile launched at own unit" pauses within one frame (`09-c2-multi-agent.md` §11.8 item 5).
2. Perf gates: 5000 synthetic symbols at ≥ 60 fps in the map; map bandwidth 20–60 KB/s at 200 tracks; overlay ≤ 0.3 ms/frame; host cost unchanged by three map clients beyond the stream serialisation budget.

Governing files: `11`, `08` §5–7, `12` §6.

## M4 — Command basics (6 weeks) — [R2]

Spikes first: `CommandFlyTo` arrival behaviour for the surface AI; aircraft `FlyTo` orbit radius vs leg geometry; **[R2]** delegation identity per AI client: does Claude Code (and Codex) let a subagent start its own `skizzy mcp --actor-token` connection, or does it share the parent's connection, so that the `as` argument is needed (`09-c2-multi-agent.md` §1.2, risk R30); whether BDA guard mode re-activates a radar that EMCON switched off (decides whether F3's radar gate is needed, §11.3).

Scope:
- **[R2] Command seat** (`09-c2-multi-agent.md` §1.1): `c2.seat.get|take|hand|release|brief`, seat-owned orders, `agentTakeover` (ask, allow, deny) with the in-game and map prompt, `seatGraceS`, free-play seating; the human's Take and Hand buttons on the map and in-game.
- **[R2] Delegation** (§1.2): `c2.delegate.create|list|update|revoke`, cluster scopes (units, group, mission), tokens (hashed, revocable, shown once), both identity modes (own connection with `--actor-token`, shared connection with the `as` argument), `out_of_scope` denials, idle TTL; the Connections window lists delegates with Revoke.
- **[R2] Automatic reports** (§1.3): the report engine with the default kinds (engaged, contact, weapons spent, damaged, kill, winchester, bingo/RTB, lost), rate limits and grouping; `c2.reports.config`.
- **[R2] Comms P0/P1 part** (`22-communications-diplomacy.md` §0): team and direct channels, `comms.send|inbox|wait|thread`, recording and replay of messages; the map **comms panel v1** (Team and Reports tabs, compose, message to own commander or delegate; `11-tactical-map.md` §5.2).
- **[R2] MCP for commanders**: toolset presets `commander` and `delegate`, `comms_wait` long-poll, the `as` argument when the delegation option is on; `skizzy.testing.FakeCommander` for CI.
- Leases on all channels with per-channel `onExpire`, `grant`/`delegate`/`renew`, ROLE clamps, suspension on human preemption and the suspension stack (`09-c2-multi-agent.md` §4).
- **Mission command plus direct control** (`09-c2-multi-agent.md` §4.1, user clarification 2026-11-08):
  - control state per unit and group, with `c2.control.get` and `control.changed` events;
  - sticky take-over and release (`c2.control.take|release|release_all`), one-shot interventions and "Fly in KSP";
  - on the map (`11-tactical-map.md` §6.1): control badges and halos, one-click Take control and Return, the Manual units panel and the in-game Flight window buttons;
  - missions join this in M6.
- Orders v1 (`move_to`, `move_route`, `patrol_area`, `loiter`, `follow`, `formation`, `engage`, `engage_all`, `weapons_hold|tight|free`, `set_depth`, `emcon`, `report`, `claim`) with the full lifecycle (`suspended`, `orphaned` event), groups, stale-decision check.
- Route executor (ships via `CommandFlyTo`; aircraft via holds or guarded native waypoints), formation executor (reflection slots + station keeping), fire authority and target filter (F3/F4 with fallbacks), F6, F8 if merged; friendly-fire check.
- Map planning tools (route, loiter, zone, assign target, ROE, lease, take/return), reference points and zone roles, shared annotations and memos.
- Genre features: EMCON presets, triggers and executor plus the map layer (`09-c2-multi-agent.md` §11.3); doctrine inheritance at side/group/unit level with winchester/bingo/withdraw actions (§11.1 basic); classification marks (`tac.track.mark`, §11.4).
- Python SDK v1 (async, msgpack, mirror, setpoint batch, orders, leases, clock, **[R2]** comms) and C# `Skizzy.Client`; `skizzy run` supervisor with `--actor-token`; `skizzy.sim` mini-sim.

Demo:
- **Seat hand-over:** the human holds the Blue seat on the map, hands it to Claude Code, takes it back.
- **Scenario §3** (the swarm), once as a Python script with a delegation token and once as a Claude Code subagent with a cluster lease on `g:swarm-1`, with reports on the team channel.
- **Scenario §4** (co-fly with wingmen) and **§8** (draw a route; a standing patrol order resumes afterwards; mission re-planning comes in M6).
- **Take-over:** a destroyer on a `patrol_area` order is taken with one click, steered and held fire by hand, then returned and rejoins the patrol from where it is.

Acceptance:
1. `06-flight-control.md` §12 items 2, 3, 6; `09-c2-multi-agent.md` §10 items 1, 4, 5, 6, 7, 8, §11.8 items 1, 2, 4 and §4.1.3 items 1–6 (with standing orders in place of missions); `22-communications-diplomacy.md` §12 items 1, 7, 8; `10-scripting-sdk.md` §9 items 1, 2, 4; `11-tactical-map.md` §11 items 2, 4, 10, 12.
2. With F3, a ship under `weapons_tight` with an assigned target never fires at a non-assigned hostile; the stock fallback is reported as partial in `bda.hooks`.
3. **[R2]** The delegation mode chosen by the spike works in Claude Code and Codex: a subagent's call outside its cluster gets `PERMISSION_DENIED`/`out_of_scope`, and a revoked token is refused.
4. Perf gates: swarm (20 drones at 20 Hz) ≤ 0.6 ms/frame; **[R2]** report engine ≤ 0.05 ms per C2 tick at 200 units, zero with no subscribers; 2-hour soak.

Governing files: `06` §5–8, `09` §1–4, §11.1, §11.3–11.4, `10` §1–4, §6, `11` §5.2, §6–7, `22` §1–3, §9.

## M5 — Craft building v1 and recording analytics (5 weeks)

Spikes first: editor reload path (`LoadShip` + `SpawnConstruct` vs `LoadShipFromFile`) and its time on a 300-part craft; GM estimate vs observed roll period.

Scope: CraftDocument and `craft.*` ops; editor round-trip (`ksp.editor.*`); `ksp.parts.catalog` with roles; part-role packs (LBP, MNWS, Iron Seas, NAS, ACA, Maritime); hull slot templates (hull families per `18-risks-and-decisions.md` §3) and `craft.probe_slots`; blueprint assembler; validation (GM, waterline, WM/AI placement); `craft.trial` profiles; library with versions, trials and battle stats; loadout editor; range-save provisioning; builder toolset. Analytics: Parquet export, `skizzy rec query` (built-in engine, DuckDB optional), `rec metrics`, `rec compare`.

Demo: scenario §1 (design the destroyer and iterate) from Claude Code; scenario §9 with an SQL query over the Parquet export.

Acceptance: `05-craft-building.md` §9 items 1–4; editor batch of 50 ops within the budget the spike sets (target ≤ 1.5 s at 300 parts); a blueprint with a missing role fails with a clear assembly report; `12-logging-recording-replay.md` §9 items 3–4.

Governing files: `05`, `12` §4–5.

## M6 — Missions, doctrine and stance (6 weeks) — [R2]

Spikes first: Streamable HTTP session identity in the MCP SDK; how BDArmory's competition counts allied and neutral teams (risk R32; `22-communications-diplomacy.md` §4); compaction behaviour of the API-key example over a 2-hour battle.

Scope:
- **Missions** (`09-c2-multi-agent.md` §11.2): first-class objects with planners for `patrol` (AAW, ASuW, ASW, sea control, CAP rotation), `strike` (phases, time on target, threat-aware re-route, abort), `escort`; the Missions panel; mission participation in the control state (take-over suspends it, return re-plans from the unit's position, §4.1); mission events and reports.
- Remaining order types (`defend_sector`, `defend_unit`, `intercept`, `screen`, `escort`, `tactic`), doctrine loader and `c2.doctrine.*` with full inheritance including the mission level and locks, triggers, tactics library v1, WTA solver, `c2.ask_human`.
- **[R2] Stance and surrender** (`22-communications-diplomacy.md` §4–6): broadcast channel; `c2.stance.*` mapped to `BDTeam.Allies` (both directions); `c2.surrender` and `c2.surrender.demand`; the referee's weapon locks; violations and scoring (subdued units); the negotiated and surrender end conditions; comms panel v2 (stance strip, templates, proposals with Accept, Reject, Counter and Apply); the in-game Comms window.
- **[R2] Waves and respawn** (`06-flight-control.md` §9.1): scenario waves and `sim.respawn.*`; `bda.spawn_continuous` wrapper.
- **[R2] AI-client support:** the think-time policy (§8.4); pause-on-event defaults per seat kind (§11.6); `skizzy.examples.api_commander` (§8.5: tool runner, adaptive thinking, compaction, refusal handling, optional `--max-usd` and `--max-turns-per-min`); bridge Streamable HTTP with actor identity and per-role toolsets; prompt templates for commander and delegate (`21-user-experience.md` §5).
- Behaviour engine v1 (BT interpreter, `sub_shoot_and_scoot`, C# plug-in loader); scenario director; fork patches F9, F10, F12; in-game overlay v2.
- The map **scenario editor** (`11-tactical-map.md` §12.8): forces with spawn specs (any count, rows, formations, mid-air), waves and respawn, seats and `agentTakeover`, initial stance, comms settings, missions, zones, end conditions.

Demo:
- **Scenario §2** (SAG + sub vs 40 missiles) with a Claude Code commander and an optional ASW subagent; the user watches on the map, answers one `ask_human`, takes DDG-2 out of its mission to fire one chosen missile and returns it, and flies one CAP fighter in KSP before handing it back.
- **A strike mission** from an airfield with time on target and a re-route around a SAM.
- **A surrender:** Red's FAC group surrenders to a broadcast demand.
- **The same §2** with the API-key example and a `--max-usd 3` cap.

Acceptance: `09-c2-multi-agent.md` §10 items 2, 3, 9, §11.8 items 3, 6, 7, 8, 9 and §4.1.3 items 1–6 rerun with missions; `22-communications-diplomacy.md` §12 items 3, 4; `06-flight-control.md` §12 item 15; the API-key example reaching its cap stops with `BUDGET_EXHAUSTED` and the seat becomes vacant after `seatGraceS` while missions keep running; `10-scripting-sdk.md` §9 item 3; a scenario built in the editor round-trips to YAML and runs.

Governing files: `09` §5–9, §11, `10` §5, §7, `11` §5.2, §12, `16` §2, §5, `22` §4–6, §11.

## M7 — Batch, CI and diplomacy (5 weeks) — [R2]

Scope: scenario schema v1 complete (waves, end conditions, matrix, expect, **[R2]** seats, stance, comms); `skizzy battle run|compare` with checkpoint/resume; unattended profile; `sim.scenario.snapshot|restore`; validation scenario pack (`14-testing-batch-sim.md` §6); FakeKsp full `IBdaFacade`; conformance suite; lockstep `sim.hold|step|release`; fork patches F11, F13; `bda.evolution.*`/`bda.autotune.*` wrappers; perf regression gates between builds.

**[R2] Diplomacy** (`22-communications-diplomacy.md` §2–4, P2): the diplomatic channel with two-sided opt-in; the negotiation protocol (proposals, counters, structured terms, `terms_breach`); psyops features (free broadcast callsigns, private credibility tags); untrusted-message delivery and the `skizzy doctor` warning for commanders with non-Skizzy tools (risk R31); the agent-vs-agent scenario (`16-scenarios.md` §10) and its batch form with two API-key commanders.

Demo: scenario §7 (200 vessels × 50 runs A/B) runs overnight unattended; `skizzy rec compare` prints CIs. **[R2]** Scenario §10: a Claude Code commander and a Codex commander settle the strait standoff; the replay shows every message with its true sender.

Acceptance: `14-testing-batch-sim.md` §7 items 1–5; `02-architecture.md` §8 budgets for 200 loaded vessels and the synthetic 1000; an injected regression is caught by `expect`; **[R2]** `22-communications-diplomacy.md` §12 items 2, 5; 20 batch runs of §10 with API-key commanders complete unattended and report the ending kinds.

Governing files: `14`, `10` §7, `20` §4, `22` §2–4, §10, `16` §10.

## M8 — Naval depth and extensibility (6 weeks)

Scope: sub operations (bearing-only association, active ping events); **[R2]** comms realism (`22-communications-diplomacy.md` §7: comms range by domain, relays, submarine depth, jammers, queued orders and reports); HullBreach events and damage-control orders; KP Naval ballast backend; SubTrim v2 (`IDepthCommandSource`, if the SubTrim priority option exists); ACA carrier launch/recover state machine and Hangar; Trajectories and KerbalEngineer adapters; Tier 2/3 and the sensor/unit-type providers (`04-ksp-and-mod-access.md` §7); **[R2]** orbit spawn mode (`06-flight-control.md` §9.1); headless map render; camera extras (`burst`, `multi`, `annotate`); craft thumbnails; training-episode export; lockstep exact stepping; parallelism where measured (`02-architecture.md` §2.4); NTDS symbol style, passive-sonar bearing wedges, `support`-AEW and `ferry` missions.

Demo: scenarios §5 (carrier strike: launch + marshal; recovery assisted) and §6 (sub ambush).

Acceptance: `08-tactical-picture.md` §12 item 5; the sub scenario completes with two torpedo hits registered; the carrier launches 6 aircraft unattended at ≤ 60 s cadence; recovery captures a wire in ≥ 3 of 5 attempts with a BDA-assisted approach (documented stretch); the comms model delays a datalink track by the configured latency; **[R2]** `22-communications-diplomacy.md` §12 item 6; an example sensor provider adds bearing-only tracks; a training export loads in pandas; any parallel offload shows its measured saving in `host.stats`.

Governing files: `04` §5, §7, `06` §9.1, §11, `08` §1, §9, `11` §9, `12` §4, `13`, `22` §7.

## M9 — Hardening and release (4 weeks)

Scope: security gate (`15-security-permissions.md` §8); CKAN/SpaceDock packaging (`SkizzyProtocol`, recommended by `SkizzyNaval`); docs site, cookbook and agent prompt templates; Python package on PyPI; upgrade and migration paths (`21-user-experience.md` §6); 4-hour soak; first public spec 1.0; upstream PR pitch to BDA Plus (timing per `18-risks-and-decisions.md` §3); optional Harmony stopgap for stock BDA Plus hooks (off by default); MCP SDK pin review; multi-human documentation (SSH tunnel).

Acceptance: a fresh install via CKAN + `pip install skizzy` reproduces the M0–M7 demos; an upgrade from the M8 build migrates settings and permissions without loss; the security checklist is green; 4-hour soak slope < 1 MB/h; the conformance suite is green against host + FakeKsp and host + KSP.

Governing files: `15`, `21`, `14` §1.

## Later (P3) — [R2]

Moved out of the core path by the user (`IDEAS.md` §15, `09-c2-multi-agent.md` §8.7). Nothing in the protocol has to change to add them:

- built-in group and unit agent layers, a built-in orchestrator, a fast model per unit by default;
- per-role models and per-agent budgets;
- agent memory between battles and a built-in analyst role (the user's own client can analyse recordings with the `rec` tools);
- automatic surrender by doctrine (`surrenderWhen`, `22-communications-diplomacy.md` §5);
- `support` tanker missions (BDA does not model refuelling).

## Implementation notes for coding agents

- Start each milestone by reading `00-overview.md`, `02-architecture.md`, `03-protocol.md`, `19-review.md` §2 (corrections) and the governing files; then the previous milestone's code; then write the acceptance tests first (FakeKsp where possible).
- Never touch `D:\KerbalSpaceProgram\BDArmory` from the Skizzy build; fork patches are separate PRs to `BDArmory-Skizzy` with their own tests; the shim builds against the **installed** `BDArmory.dll` copied to `refs/` and resolves members by name, never by line number.
- Compile in-game assemblies against KSP's `Managed` folder (`NoStdLib`); every hot path gets a `// skizzy:hot` tag, a CI allocation assert and a timing counter on day one.
- Protocol changes update the JSON Schemas and the conformance vectors in the same PR; the Python models are regenerated, never hand-edited.
- Each milestone's in-game acceptance is run by the user with the agent's checklist; the agent prepares the checklist, the scenario files and the expected numbers.
