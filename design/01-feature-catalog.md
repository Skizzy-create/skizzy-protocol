# 01 Feature catalog

Every feature of skizzy-protocol with a priority and an origin tag. `[IDEAS]` = in `IDEAS.md`; `[ADDED]` = added by design pass 1.0 (with a one-line rationale); `[REVIEW-ADDED]` = added by review pass 1.1 (§15). Priorities: **P0** MVP, delivered by the end of M4; **P1** core, by the end of M7; **P2** nice-to-have, by the end of M9 (release); **P3** after 1.0. Several P0 items land as early as M0/M1 because the user needs them first (connect, read, fly, screenshot, log). Milestone contents are in `17-milestones.md`; `19-review.md` §1 maps every `IDEAS.md` bullet to the rows here. Rows changed by the review carry **[R]**. **[R2]** marks pass 2 changes (2026-11-08: the command-model change, communications and diplomacy, and spawn anywhere; `IDEAS.md` §15–16). The new rows are in §17 and §18.

Reading the tables: "Front ends" lists where the feature is usable: **S**cripts, **A**gents (MCP), **M**ap, **G**ame (in-game UI/controls). The rule "nothing exists in only one front end" is checked here: every row has at least S+A, and M/G where a UI makes sense.

## 1. Core server

| Feature | Pri | Origin | Front ends | Notes / rationale |
|---|---|---|---|---|
| In-game host plugin, C# bridge exe, shared protocol library | P0 | [IDEAS] | S A M G | PLAN v0.2 architecture kept |
| Native WebSocket socket (JSON) for scripts and high-rate agents | P0 | [IDEAS] | S | |
| MCP projection (stdio) | P0 | [IDEAS] | A | |
| MCP Streamable HTTP on loopback with per-session actor identity | P1 | [ADDED] | A | **[R2]** a commander and the sub-agents its AI client spawns (delegates) share one bridge without N processes; stdio uses the `as` argument instead (`09-c2-multi-agent.md` §1.2) |
| MessagePack binary encoding negotiated per session | P0 | [ADDED] | S M | 5–10× smaller than JSON for track/telemetry streams; zero-garbage parse in Python/C# |
| Compact text encoding for LLM-facing tactical data | P0 | [ADDED] | A | token economy: one track = one short line |
| Main-thread dispatcher, per-frame adaptive budget, zero idle cost | P0 | [IDEAS] | — | |
| **Optional layer, zero cost when unused**: BDArmory guard mode unchanged with nothing connected; no LLM/bridge/map needed; no callbacks, patches or engine ticks while idle | P0 | [IDEAS] 2026-11-08 | — | principle in `00-overview.md`; enforced by the idle gate in `02-architecture.md` §8 |
| Parallelism where cheap and safe: multi-threaded bridge, map, export and batch analysis; in-game only pure math on snapshot copies on one worker thread, and only where measured to help | P2 | [IDEAS] 2026-11-08 | — | `02-architecture.md` §2.4; not before M8; must not complicate M0–M3 |
| Zero-alloc hot paths, pooled buffers, struct tables | P0 | [IDEAS] | — | |
| Fail-closed tools, fault tracker, no exceptions into KSP | P0 | [IDEAS] | — | |
| Graceful scene changes, handle invalidation, op cancellation | P0 | [IDEAS] | — | |
| Operations (long-running), progress, re-attach after reconnect | P0 | [IDEAS] | S A M | |
| Streams (sampled / on-change / delta), events ring buffer | P0 | [IDEAS] | S A M | |
| Triggers: host-evaluated conditions that emit events | P1 | [ADDED] | S A M | lets agents sleep until "any hostile within 30 km of group X" without polling |
| Catalog with revisions, search, describe; stable tool list across scenes | P0 | [IDEAS] | A | |
| Clock model: `ut` + `frame` on every message, `sim.clock` stream, wall-clock mapping | P0 | [ADDED] | S A M | time sync is a prerequisite for logs, replay and lockstep |
| Lockstep: `sim.hold` / `sim.step(n)` / `sim.release` | P1 | [ADDED] | S A | deterministic-ish stepping for tests and "pause while agents think"; **[R]** exact single-step semantics in `20-runtime-lifecycle.md` §4 |
| Observability of the system itself: `host.stats`, per-engine timings, GC alloc counters, Prometheus endpoint in bridge, trace ids per order | P0/P1 | [ADDED] | S A M G | the user's first-class requirement is performance; it must be measurable |
| Protocol versioning: `MAJOR.MINOR`, capability `@N`, schema `$id`s, msgpack schema ids | P0 | [ADDED] | — | |
| Hot reload of annotations, doctrine, behaviour trees, C# plug-ins | P2 | [ADDED] | S A G | iteration speed without restarting KSP |

## 2. Access to KSP and every mod

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Tier 0 reflection: fields, events, actions, action groups, resources, staging, part tree, crew, post-MM configs | P0 | [IDEAS] | S A G | PLAN v0.2 §4 |
| Tier 1 annotation packs (descriptions, units, safety, hidden properties, statics, declared methods) | P0 | [IDEAS] | S A | shipped packs for BDArmory, HullBreach, SubTrim, KP Naval, ACA, NAS, MNWS, PRE |
| Tier 2 duck-typed attributes, Tier 3 contract DLL / JSON-delegate ABI | P2 | [IDEAS] | S A | **[R]** P1→P2: Tier 0/1 plus adapters cover every installed mod; Tier 2/3 only pay off once third-party authors opt in (M8) |
| Deep reflection (private/static/singletons), off by default, per mod | P1 | [IDEAS] | S A | |
| Bulk query selector across vessels/parts/modules (`ksp.query`) | P0 | [IDEAS] | S A M | |
| Mod adapters: PhysicsRangeExtender, VesselMover, HyperEdit, HullBreach, SubTrim, KP Naval ballast, BuoyancyAdjuster, ACA (catapults/wires), NAS, MNWS VLS, KerbalEngineer, Trajectories, AtmosphereAutopilot, Hangar, KerbalKonstructs, WildBlueTools | P1 | [IDEAS] | S A M G | `04-ksp-and-mod-access.md` §5. **[R]** Placement and SubTrim depth land in M1, HullBreach in the M2 SITREP, the rest across M4–M7. API names re-verified against the installed DLLs (`19-review.md` §4) |
| Part-role catalog (`SKIZZY_PART_ROLES`): which parts are VLS cells, CIWS, radars, sonars, hulls, catapults | P1 | [ADDED] | S A | blueprints and symbology need semantic roles, not part names |
| Vessel classification (destroyer/carrier/sub/fighter/drone/missile) from parts and AI type | P0 | [ADDED] | S A M | drives map symbols, threat scoring, doctrine |

## 3. Vessel creation and craft building

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Editor control: new/load, add/remove/move/rotate, node and surface attach, symmetry, staging, module config, save | P1 | [IDEAS] | S A G | |
| Headless `.craft` document model: read, edit, write, diff | P1 | [IDEAS] | S A | needed before the editor path; no scene requirement. **[R]** P0→P1: craft building is one milestone (M5), so P0 was unreachable without delaying flight and BDA. M1 can already spawn an existing `.craft` at a position (`ksp.vessel.spawn_craft`) |
| Blueprint / parametric DSL assembled through hull slot templates and the part-role catalog | P1 | [IDEAS] | S A | |
| Validation report: mass, CoM, CoL, CoT, TWR, buoyancy/waterline, stability (GM estimate), WM/AI placement, part count, clipping | P1 | [IDEAS] | S A G | |
| Sea trials / flight trials: spawn in a test range, measure draft, top speed, turn rate, roll period, climb rate; attach results to the craft version | P1 | [ADDED] | S A | closes the "build, measure, adjust" loop with numbers, not screenshots |
| Iteration loop: build → test battle → metrics → adjust → rebuild | P1 | [IDEAS] | A S | composed from craft + batch + logs |
| Shared library of craft, templates, loadouts, with versions and performance records | P1 | [IDEAS] | S A M | `<KSP>/Skizzy/library/` |
| Loadout editor: swap missiles in MML/VLS cells, ammo, countermeasures | P1 | [ADDED] | S A G | BDA `MultiMissileLauncher`/`ModuleMissileRearm` already allow custom loadouts; expose as one operation |
| Craft thumbnails and 3-view renders for agents | P2 | [ADDED] | A | agents iterate faster when they can see the ship |
| AI parameter auto-tune integration (BDA AutoTune) and evolution hooks | P2 | [ADDED] | S A | BDA has `AutoTune` and `BDAModuleEvolution`; expose instead of reinventing |

## 4. Flight and vessel control

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Throttle, SAS modes, RCS, staging, gear, brakes, lights, action groups, active vessel switch | P0 | [IDEAS] | S A G | M1. **[R]** staging goes through the staging permission resolver (`06-flight-control.md` §2) |
| Orbital: point at prograde/retrograde/normal/target via SAS modes, `flight.orbit_point`; hand-off to manoeuvre tools out of scope | P1 | [REVIEW-ADDED] | S A G | IDEAS 4.3 says "orbital"; SAS target modes are cheap; full manoeuvre planning is P3 |
| Setpoint holds at physics rate: heading, pitch, roll, altitude, speed, depth, vertical speed | P0 | [IDEAS] | S A M G | |
| Direct control channel (raw axes at ≤ 50 Hz with dead-man) | P1 | [ADDED] | S | swarm scripts and external autopilots want it; dead-man neutralises on silence |
| Waypoints/routes, orbit/loiter, formation keeping (ships and aircraft) | P0 | [IDEAS] | S A M | route executor + WingCommander formations |
| Hand-off to/from BDArmory AI and AtmosphereAutopilot | P0 | [IDEAS] | S A G | |
| Human + agent co-flying with arbitration | P0 | [IDEAS] | G S A | |
| Kill switch (hotkey, toolbar, map, API) that always restores manual control | P0 | [IDEAS] | S A M G | |
| Scene setup and positioning (VesselMover/HyperEdit style): place, lift, drop, teleport, orient | P0 | [IDEAS] | S A M | **[R2]** spawning anywhere is now its own set of rows in §18 |
| Submarine depth control via SubTrim + KP Naval ballast | P1 | [ADDED] | S A M G | **[R]** v1 writes `ModuleSubTrim.manualDepth`/`targetDepth` through the PAW path (M1). The `IDepthCommandSource` route needs an optional separate assembly and a SubTrim selection-priority patch, so it is P2 (`04-ksp-and-mod-access.md` §5) |
| Carrier launch/recovery sequencing (ACA catapults, wires, hooks; Hangar) | P2 | [ADDED] | S A M | the user flies carriers; deck cycles are a state machine, not a one-shot |
| Collision/deconfliction advisories for scripted swarms | P2 | [ADDED] | S | BDA avoids collisions for its AI only |

## 5. BDArmory

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Spawning: rings, exact formations, single vessels, templates | P0 | [IDEAS] | S A M G | BDA's spawners (M2); **[R2]** Skizzy's own spawn-anywhere and multi-spawn are in §18 (M1) |
| Teams, guard mode, full weapon-manager configuration incl. 1024 targets / 128 per target / parallel salvo | P0 | [IDEAS] | S A M G | |
| AI modes (pilot/surface/sub/VTOL/orbital), live parameter changes, commands (attack/fly-to/follow/waypoints/take-off/release) | P0 | [IDEAS] | S A M G | |
| Direct weapons: fire missile at target, guns, countermeasures incl. bubbles, radar/sonar on/off/lock, targeting pods | P0 | [IDEAS] | S A M G | `destructive`, ask by default |
| Competitions, tournaments, scores, damage reports, missiles in flight, settings, log tailing | P0 | [IDEAS] | S A M | |
| Fork hooks: steering override, target assignment, fire authority (hold/tight/free), formation positions, arbitrary waypoints, events | P0 | [IDEAS] | — | `07-bdarmory-integration.md` §5. **[R]** F1/F2/F5 in M2, F3/F4/F6 in M4, F7 downgraded to P2 (reflection fallback works) |
| Per-unit modified AI behaviours (BTs and plug-ins layered on BDA AI) | P1 | [IDEAS] | S A | |
| Launch envelope (DLZ) queries per weapon vs target | P1 | [ADDED] | S A M | BDA computes `MissileLaunchParams`; agents need "can I shoot now, from where" |
| Weapon-target assignment solver (interceptors × threats under constraints) as an in-game service | P1 | [ADDED] | S A M | 40 inbound missiles cannot be assigned one tool call at a time |
| Magazine/ammo accounting per vessel and weapon | P0 | [ADDED] | S A M | SITREPs and doctrine need it |
| Friendly-fire / blue-on-blue line-of-fire check | P1 | [ADDED] | S A | BDA has `CheckSafeToFireGuns`; expose and extend to missiles |
| Evolution/tournament automation via API | P2 | [ADDED] | S A | existing BDA features, worth exposing |

## 6. Tactical picture

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Per-team fused track table (radar, IRST, sonar, visual, RWR, datalink), IFF, quality, age | P0 | [IDEAS] | S A M G | |
| Fog-of-war or god view, per session | P0 | [IDEAS] | S A M | |
| Filters (area, role, team, threat, domain), frames (geo, BRAA, bullseye, formation), top-N | P0 | [IDEAS] | S A M | |
| Subscriptions with deltas and events only | P0 | [IDEAS] | S A M | |
| Compact LLM text, JSON, msgpack columnar | P0 | [IDEAS] | S A | |
| Stable track ids, dead reckoning for stale tracks, drop timers | P0 | [ADDED] | S A M | agents must refer to "T17" across minutes |
| Bearing-only tracks (RWR, passive sonar) and intent inference (closing/opening, CPA) | P1 | [ADDED] | S A M | realism and the sub scenario |
| Threat scoring with explanations; engagement status (who engages whom, missiles in flight per track) | P0 | [ADDED] | S A M | |
| Named bullseyes, zones (MEZ/FEZ/JEZ, no-fire, patrol areas, sectors) as shared picture objects | P0 | [ADDED] | S A M | ROE and sector assignment need geometry objects |
| SITREP: compact own-unit status (fuel, ammo, damage, flooding, leases, orders) | P0 | [ADDED] | S A M | the single most used agent tool |
| Comms/datalink model: per-team link graph, latency, jamming degrading the shared picture (optional realism) | P2 | [ADDED] | S A M | EW realism; default is perfect comms. **[R2]** merged with message comms range and jamming (§17, `22-communications-diplomacy.md` §7) |

## 7. Command and control

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Orders as objects: intent, constraints, ROE, priority, owner, status, timeout | P0 | [IDEAS] | S A M G | |
| Hierarchy: commander → groups → units; roles | P0 | [IDEAS] | S A M | **[R2]** one command seat per side (human or one agent) → groups → units; roles commander, advisor, delegate, observer, script, behaviour, harness, human (§17) |
| Leases per control channel, TTL heartbeats, preemption events | P0 | [IDEAS]+[ADDED] | S A M G | channels and TTL added |
| Precedence human > commander > unit > script > BDA default (numeric bands) | P0 | [IDEAS] | — | **[R2]** bands: human 1000, commander 800, delegate 600 (max 700), script 400, behaviour 300, BDA 0; the referee's surrender locks at 1100 (`09-c2-multi-agent.md` §4) |
| Tactics library: CAP, intercept, pincer, AD umbrella/sectors, ASW screen, strike package, saturation, sub ambush, carrier launch/recovery | P1 | [IDEAS] | S A M | |
| Model per role; events pushed to agents | P3 / P0 | [IDEAS] | A | **[R]** P0→P1: events reach agents in M2 (`tac_wait_events`). **[R2]** model per role → P3 with the built-in agent layers (`09-c2-multi-agent.md` §8.7); events reach any client by long-poll (`tac_wait_events` M2, `comms_wait` M4), P0 |
| Time control for thinking (auto slow-mo/pause) | P1 | [IDEAS] | A G | **[R2]** a host-side think-time policy for agent seats (`09-c2-multi-agent.md` §8.4), M6; pause-on-event M3 |
| LLMs one actor among several | P0 | [IDEAS] | — | |
| Order lifecycle with acknowledgement, progress, unachievable reasons, supersession | P0 | [ADDED] | S A M | |
| Stale-decision rejection: orders carry `basedOnSeq`/`validUntilUt` | P1 | [ADDED] | S A | an LLM decision made 8 s ago may be wrong now |
| Doctrine files (YAML): ROE, envelopes, formations, sectors, triggers → tactics | P1 | [ADDED] | S A M | |
| Agent memory between battles (AAR store, per-role notes, craft performance DB) | P3 | [ADDED] | A | **[R2]** P2→P3 (user decision, `IDEAS.md` §15); the user's own client can analyse recordings with the `rec` tools |
| Token/cost budgets per agent and per battle, spend kill switch | P1 | [ADDED] | A G | **[R2]** optional, and only in the API-key example (`--max-usd`, `--max-turns-per-min`, `09-c2-multi-agent.md` §8.5); per-agent budgets in the core are gone (user decision U2 resolved) |
| Scenario director: waves, timelines, scripted triggers | P1 | [ADDED] | S A M | |
| Multiple humans (several map operators) | P2 | [ADDED] | M | loopback in v1; SSH tunnel documented; LAN binding P3 |
| **Mission command plus direct control**: per unit and group control state (BDA / mission / order / agent / script / manual / piloted), shown on the map and in the SITREP; one-click take-over (all or per channel) and hand-back that re-plans from the unit's position; one-shot interventions (route, assign target, fire this weapon, hold fire) return automatically; "Fly in KSP"; suspension stack so nothing is lost | P0 | [IDEAS] 2026-11-08 (user clarification of the genre reference) | S A M G | `09-c2-multi-agent.md` §4.1, `11-tactical-map.md` §6.1; M4 with orders, M6 with missions |

## 8. Scripting and automation

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Python SDK (typed models, async streams, BRAA helpers, formations, order lifecycle) | P0 | [IDEAS] | S | |
| C# SDK (shared by map, bridge, tests) | P0 | [IDEAS] | S | |
| Script templates and plug-in model (swarm, AD coordinator, ASW screen) | P1 | [IDEAS] | S | |
| In-game behaviour trees (zero-alloc) and C# behaviour plug-ins | P1 | [IDEAS] | S A G | |
| Declarative scenario and doctrine files | P1 | [IDEAS] | S A M | |
| `skizzy.sim` mini-sim for script development without KSP | P1 | [ADDED] | S | develop a swarm script on a laptop |
| Script supervisor: restart policy, dead-man release, resource caps per script | P1 | [ADDED] | S G | failure mode: a crashed script must not leave 20 drones diving |

## 9. Tactical map

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Live map with APP-6/2525 symbols per type and affiliation | P0 | [IDEAS] | M | **[R]** P1→P0: map v1 is M3 and the user's 2026-11-08 requirements make the command screen non-optional; `symbols.json` mapping (`11-tactical-map.md` §2) |
| **Command-screen layout**: freely resizable window; dockable/movable/detachable panels (track list, unit details, orders, timeline, log, layers); layout saved and restored per user; smooth wheel/pinch zoom and pan; fit-to-selection, fit-all | P0 | [IDEAS] 2026-11-08 | M | `11-tactical-map.md` §4.1, §5.1; M3 |
| **Clarity at scale, individual tracks always**: every missile and aircraft drawn individually by default (aggregated counts only as an optional Counts view); density- and zoom-aware sizing that avoids overlap and degrades full symbol → small symbol → dot, never hiding or merging; size hierarchy by radar cross-section with overrides per track, type, team, selection; compact altitude/depth labels and ticks; trails or velocity leaders; missile glyphs and optional target lines; brief intercept/impact/kill markers; label declutter with leader lines that never hides symbols; filters by type/team/threat; hover/selection highlight | P0 | [IDEAS] 2026-11-08 (corrected the same day) | M (G for the overlay subset) | `11-tactical-map.md` §2.1; M3; per-item persistent overrides and leader-line refinements may finish in M4 |
| Replay and slow-motion scrubbing for dense engagements (0.1×–16×, event stepping, loops, interpolation; `battle.detailed` profile; live slow motion from the map) | P0 | [IDEAS] 2026-11-08 | M S | `11-tactical-map.md` §2.1 rule 5; replay is the last M3 task |
| Map rendering performance: thousands of symbols at 60 fps (sprite atlas, GPU Skia, worker-thread clustering) | P0 | [IDEAS] 2026-11-08 | M | `11-tactical-map.md` §10: 5000 symbols at 60 fps |
| **Memos**: notes and labels pinned to tracks or map points, shared, recorded, readable by agents as data | P1 | [IDEAS] 2026-11-08 | S A M | `11-tactical-map.md` §7; M4 |
| Layers: tracks, history, sensor/weapon ranges, engagement lines, orders, threat axes | P1 | [IDEAS] | M | |
| Planning: draw routes, areas, formations; assign targets; set ROE; agent orders appear live | P1 | [IDEAS] | M | |
| Fog-of-war or god view | P1 | [IDEAS] | M | |
| External desktop app + in-game overlay from the same stream | P1 | [IDEAS] | M G | **[R]** overlay P2→P1: overlay v1 (own-team symbols, selected track BRAA) ships with map v1 in M3 |
| Headless map render to PNG for agents | P2 | [ADDED] | A | the "picture" an LLM can look at when it needs geometry |
| Replay/scrub of recordings in the map, AAR mode | P1 | [IDEAS] | M | |
| Shared annotations (drawings, labels) synchronised to all clients | P1 | [ADDED] | S A M | agents and humans plan on the same canvas |

## 10. Logging, recording, replay

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Log anything by query at any rate (on change or N Hz) | P0 | [IDEAS] | S A M G | **[R]** log v0 in M0: any `ksp.query` selector to CSV/JSONL on disk, started from the in-game window, MCP or CLI (`12-logging-recording-replay.md` §1.1) |
| Named log profiles, switchable per battle | P0 | [IDEAS] | S A M G | |
| Saved to disk in open formats: `.skzrec` (msgpack) + JSONL; Parquet/CSV export | P0 | [IDEAS] | S A M G | **[R]** CSV/JSONL written directly in-game from M0; `.skzrec` M2; Parquet export M5 |
| Query saved logs by time/vessel/team/event | P1 | [IDEAS] | S A M | **[R]** basic host-side `rec.query` (time window, vessel, fields, decimation) is P0 in M2, so the map and agents can retrieve logs without the bridge; SQL over Parquet is M5 |
| Full battle recording and replay | P1 | [IDEAS] | S M | |
| Metrics and A/B reports | P1 | [IDEAS] | S A | |
| Session audit log | P0 | [IDEAS] | G S | |
| Recording as agent context (AAR summaries) | P2 | [IDEAS] | A | |
| Performance telemetry stream (frame time, physics steps, GC, host budgets) in every recording | P0 | [ADDED] | S M | regressions must be visible next to the battle |

## 11. Vision

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Screenshots as images, resolution/rate caps | P0 | [IDEAS] | S A M G | **[R]** M0. `AsyncGPUReadback` pipeline, ≤ 3 ms main thread (`13-vision-camera.md` §1) |
| Camera modes: follow, orbit, chase, free, look-at-target | P0 | [IDEAS] | S A M G | **[R]** M0, mapped to KSP `FlightCamera` modes (`13-vision-camera.md` §1) |
| Frame-synchronous capture of several cameras / targets in one call | P2 | [ADDED] | A | compare two ships at once |
| Hide-UI capture, annotation burn-in (labels on tracks) | P2 | [ADDED] | A | |

## 12. Testing and batch simulation

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Batch mode running many battles from scenario files | P1 | [IDEAS] | S A | |
| Repeats with statistics; A/B comparisons | P1 | [IDEAS] | S A | |
| Validation of the user's mods (multi-target, salvo, depth charges, SubTrim, MNWS VLS, Iron Seas guns) | P1 | [IDEAS] | S | |
| Mock KSP for CI (`Skizzy.Sim`) | P0 | [ADDED] | — | |
| Conformance suite for the protocol | P1 | [ADDED] | — | |
| Soak tests (hours, memory growth gates) and perf gates in self-checks | P1 | [ADDED] | G | leaks were the user's pain. **[R]** a 30-minute soak gate applies from M2; the 4-hour soak is the M9 gate |

## 13. Permissions and safety

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Levels read/control/destructive/cheat/unsafe, allow/ask/deny per mod and tool | P0 | [IDEAS] | G | |
| Defaults: editor, flight, screenshots allowed; destructive ask | P0 | [IDEAS] | G | D11 |
| Kill switch, ROE guards, friendly-fire checks, audit, localhost only, token | P0 | [IDEAS] | G S A M | |
| Actor identities and lease ACLs (which roles may preempt) | P0 | [ADDED] | G | |
| Two-key confirmation for bulk destructive/cheat operations (remove all vessels, 200-vessel spawn) | P1 | [ADDED] | G A | **[R2]** spawns are no longer two-key: any count is allowed with a performance warning above 50 (user correction); two-key stays for removing all vessels and kill-everything scenario loads |
| Prompt-injection isolation of game text; no permission changes over the wire | P0 | [IDEAS]/[ADDED] | — | |

## 14. Extensibility and extras

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Other mods add unit types, sensors, weapons, symbology via annotation packs | P1 | [IDEAS] | — | **[R]** part roles + `symbols.json` class mapping are P1 (M3); code-level sensor/unit-type providers are P2 (§15) |
| Logistics, rearm, damage control | P2/P3 | [IDEAS] | S A M | damage control via HullBreach/SubTrim first |
| Comms degradation and EW | P2 | [IDEAS] | S A M | **[R2]** specified in `22-communications-diplomacy.md` §7 (§17), M8 |
| Web dashboard | P3 | [IDEAS] | — | superseded by the desktop map; a read-only web view may come later |
| AI battle commentator | P3 | [IDEAS] | A | from recordings |
| CKAN packaging, docs site | P2 | [ADDED] | — | **[R]** detailed by the install and docs rows in §15 |

## 15. Review additions — [REVIEW-ADDED]

Added by review pass 1.1 because they were missing, implied but unspecified, or needed to make another row buildable. Details live in the referenced file.

| Feature | Pri | Front ends | Where | Why |
|---|---|---|---|---|
| **Generic in-game capability console**: catalog search, a form generated from each capability's JSON Schema, result viewer, audit tail | P0 | G | `21-user-experience.md` §3 | makes "every feature reachable from in-game" structural instead of hand-building a window per feature |
| Map command palette (same catalog-driven form inside the map) | P1 | M | `11-tactical-map.md` §5 | same rule for the map |
| **Log v0**: any selector → CSV/JSONL directly in-game, start/stop from the in-game window, MCP, CLI | P0 | S A M G | `12-logging-recording-replay.md` §1.1 | "log whatever I want, get it, save it" must work in M0, not after the recorder |
| Host-side basic `rec.query` (window, vessel, fields, decimation) and `rec.get_file` | P0 | S A M | `12-logging-recording-replay.md` §4 | retrieval without the bridge, from the map and agents |
| Training-episode export (observations/actions/rewards per actor, JSONL + Parquet) | P2 | S | `12-logging-recording-replay.md` §4 | IDEAS 10.4 training data |
| `AsyncGPUReadback` screenshot pipeline with a `ReadPixels` fallback | P0 | S A M G | `13-vision-camera.md` §1 | screenshots at rate without frame spikes |
| **Vessel lifecycle state machine** (loaded/unpacked, packed, unloaded, destroyed) on every handle, and unloaded tracks in god view | P0 | S A M | `20-runtime-lifecycle.md` §1 | packed vessels silently ignore controls; tools must say so |
| Active-vessel change handling (death, BDA switching, human switching) | P0 | S A G | `20-runtime-lifecycle.md` §2 | the human-override lease and camera follow the active vessel |
| Warp and pause policy (physics warp, rails warp, BDA time scale, pause menu, other menus) | P0 | S A G | `20-runtime-lifecycle.md` §3 | holds, streams and leases must behave predictably when time stops or jumps |
| Lockstep exact single-stepping | P2 | S A | `20-runtime-lifecycle.md` §4 | reproducible tests |
| Quickload / scene reload handling with an optional auto-snapshot | P1 | S A M G | `20-runtime-lifecycle.md` §5 | quickload mid-battle must not leave orphan leases or dead handles |
| No `.sfs` writes in v1 (decision) | P0 | — | `20-runtime-lifecycle.md` §5 | save corruption is the worst failure a mod can cause |
| Duplicate vessel-name handling (deconfliction at competition start, persistentId re-index) | P0 | S A M | `20-runtime-lifecycle.md` §6 | BDA scores by name |
| Vessel splits and debris policy | P0 | S A M | `20-runtime-lifecycle.md` §7 | splits create new vessels mid-battle |
| **Long-session hygiene**: bounded caches, eviction, file rotation, memory self-check, event-unsubscribe audit | P0 | G S | `20-runtime-lifecycle.md` §8 | leaks were the user's pain |
| **Mod-interference doctor** (CommNet/`IsControllable`, Kerbalism, RemoteTech, other `OnFlyByWire` users, KJR, PRE, KSPCF, Harmony, assembly conflicts) | P0 | G S | `20-runtime-lifecycle.md` §9 | one row per thing that can silently break control |
| Auto-reconnect, port conflict fallback, `PluginData/endpoint.json` | P0 | S A M | `02-architecture.md` §5, `20-runtime-lifecycle.md` §10 | KSP restarts and port clashes are routine |
| **First-run wizard** (token, preset, port test, "copy MCP command") and `skizzy doctor --fix` | P0 | G | `21-user-experience.md` §1 | the first five minutes decide whether it gets used |
| Zip install (P0) and CKAN (P2) | P0/P2 | — | `21-user-experience.md` §2 | |
| Docs and examples: README quickstart (P0), cookbook (P1), compact-text grammar, agent prompt templates, troubleshooting | P0/P1 | — | `21-user-experience.md` §5 | |
| **No-Claude-Code path**: `skizzy call`/`watch`/`log`/`shot` CLI (P0), Python REPL helpers (P0), any MCP client, map, in-game console | P0/P1 | S G M | `21-user-experience.md` §4 | the platform must not depend on one client |
| Upgrade and migration (settings/permission schema versions, recording versions, BDA update detection, SDK compatibility) | P1 | — | `21-user-experience.md` §6 | |
| **Unified error taxonomy** (stable string codes mapped to JSON-RPC numbers) | P0 | S A M G | `03-protocol.md` §9 | agents and scripts branch on codes, not messages |
| net48 build guard (compile in-game assemblies against KSP's Managed folder) | P0 | — | `02-architecture.md` §1 | KSP has no `netstandard.dll` |
| In-game allocation gate (heap slope over 10 minutes) next to the CI 0-byte asserts | P0 | G | `02-architecture.md` §8 | Unity's mscorlib has no per-thread allocation counter |
| Time-sliced TPE (amortised over physics steps) | P0 | — | `02-architecture.md` §2.2, `08-tactical-picture.md` §10 | keeps the worst physics step under budget |
| Picture output caps (default 10 lines, hard cap 50, pagination) and `tac.summary` | P0 | A | `03-protocol.md` §11, `08-tactical-picture.md` §6 | "never a token dump" enforced by the tool, not by the agent |
| `flight.orbit_point` and orbital hand-off | P1 | S A G | `06-flight-control.md` §2 | IDEAS 4.3 |
| Crew operations (`ksp.crew.list`, transfer, EVA board) | P2 | S A | `04-ksp-and-mod-access.md` §2 | IDEAS 2.1 lists crew |
| Staging permission resolver (stage = control unless the next stage fires a weapon or a decoupler on a crewed vessel) | P1 | S A G | `06-flight-control.md` §2 | staging is how many missiles are launched |
| Unattended batch profile (no prompts, two-key pre-armed, wall-clock cap) | P2 | S | `14-testing-batch-sim.md` §4 | "headless" honestly means unattended |
| Sensor and unit-type provider extension point | P2 | — | `04-ksp-and-mod-access.md` §7 | IDEAS 14.1 |
| `symbols.json`: vessel class → MIL-STD-2525D symbol set / entity, editable | P1 | M G | `11-tactical-map.md` §2 | concrete symbology instead of "APP-6 style" |
| **[R2]** API-key commander example: refusal handling and append-only history with compaction (was: orchestrator) | P1 | A | `09-c2-multi-agent.md` §8.5 | required by the current Claude API behaviour; the built-in orchestrator is gone |

Count: 122 rows in §1–14 (112 from pass 1.0, the orbital row, and 9 rows from the user's 2026-11-08 additions to IDEAS) and 34 in §15. **[R2]** Pass 2 adds 19 rows in §17 and 8 in §18; no row was deleted, and superseded rows say so in their notes.

## 16. Genre reference additions (CMO-style command features) — [REVIEW-ADDED]

The user named *Command: Modern Operations* as the genre reference for the command screen (features only, not UI). Rows already covered elsewhere are listed in `11-tactical-map.md` §12 and `09-c2-multi-agent.md` §11; these are the rows that were missing or only implied.

| Feature | Pri | Front ends | Where | M |
|---|---|---|---|---|
| NTDS symbol style option | P2 | M | `11-tactical-map.md` §12 | M8 |
| Track uncertainty display: bearing lines/wedges for bearing-only, ellipses for DR/passive ranging | P1 | M G | `11-tactical-map.md` §12 | M3/M8 |
| Emission-state badges and EMCON layer | P1 | M G | `11-tactical-map.md` §12 | M3/M4 |
| EMCON per unit/group/mission/side with detectability effects | P1 | S A M G | `09-c2-multi-agent.md` §11.3 | M4 |
| Doctrine/ROE inheritance side → mission → group → unit (weapons control per domain, engage unknowns, opportunity targets, salvo per target class, winchester, bingo, withdraw/redeploy) and `c2.doctrine.effective` | P1 | S A M | `09-c2-multi-agent.md` §11.1 | M4 basic, M6 full |
| Missions as first-class objects (patrol AAW/ASuW/ASW/sea control, strike, escort; support-AEW and ferry P2) with unit assignment | P1 | S A M | `09-c2-multi-agent.md` §11.2 | M6 |
| Reference points (fixed or unit-relative) and zone roles (patrol, prosecution, exclusion, no-fire, MEZ/FEZ/JEZ) | P1 | S A M | `11-tactical-map.md` §12 | M4 |
| Message log with category filters and jump-to-position/time | P0 | M G | `11-tactical-map.md` §12, `03-protocol.md` §7 | M3 |
| Losses & expenditures panel per side (`c2.losses`) and post-battle scorecard | P1 | S A M | `11-tactical-map.md` §12 | M3/M5 |
| Time compression bar and pause-on-event (`session.pause_on`) | P0 | M G S | `11-tactical-map.md` §12, `09-c2-multi-agent.md` §11.6 | M3 |
| Loadout/magazine view per unit with winchester/bingo markers | P0 | M G | `11-tactical-map.md` §12 | M3 |
| Contact classification flow `detected → classified → identified` (`idState`) | P1 | S A M | `09-c2-multi-agent.md` §11.4, `08-tactical-picture.md` §2 | M2/M3 |
| Side posture via BDA team relations (`bda.team.relations`) | P1 | S A M | `09-c2-multi-agent.md` §11.5 | **[R2]** superseded by side stance (§17), M6 |
| Map scenario editor: place units, sides, posture, doctrine, missions, save scenario YAML | P1 | M | `11-tactical-map.md` §12.8 (**[R2]** spawn specs with any count, waves, respawn, seats, stance, comms, end conditions) | M6 |

**[R] Mission command is added on top of Skizzy's own capabilities, not instead of them** (user clarification 2026-11-08). The take-over and hand-back row is in §7. Skizzy's strengths beyond the genre stay first-class and are not traded away for genre features:

- real KSP physics and BDA AI;
- LLM and script actors;
- flying a unit yourself;
- craft building;
- full logging and replay.

They are listed in `09-c2-multi-agent.md` §11.7 and `11-tactical-map.md` §12.

## 17. Command model, communications and diplomacy — [R2, IDEAS §15–16]

The user's pass 2 decisions. Group and unit agents, the orchestrator, per-role models, per-agent budgets and agent memory are P3 (`09-c2-multi-agent.md` §8.7). Their work is done by the in-game logic in `09-c2-multi-agent.md` §11.

| Feature | Pri | Front ends | Where | M |
|---|---|---|---|---|
| Any MCP client as commander, advisor or delegate (Claude Code, Codex, other tools, local models); bridge actor options; two clients checked | P0 | A | `09-c2-multi-agent.md` §8.1 | M0/M4 |
| **Command seat** per side: a human or one agent; take, hand, release; `agentTakeover` ask/allow/deny; seat-owned orders, missions and delegations; hand-over brief; free-play seating | P0 | S A M G | `09-c2-multi-agent.md` §1.1 | M4 |
| **Delegation** to sub-agents spawned by the commander's AI client: cluster leases (units, group, mission), revocable tokens, own connection or the `as` argument, idle TTL | P0 | S A M G | `09-c2-multi-agent.md` §1.2 | M4 |
| **Automatic reports without an LLM**: engaged, contact, weapons spent, damaged, kill, winchester, bingo/RTB, on station, mission complete, lost, surrendered | P0 | S A M G | `09-c2-multi-agent.md` §1.3 | M4 |
| Team and direct channels; message store, recording and replay | P0 | S A M G | `22-communications-diplomacy.md` §1–2, §8 | M4 |
| Visibility of traffic: own side plus diplomatic, or god view with true senders | P0 | S A M | `22-communications-diplomacy.md` §8 | M4 |
| Map comms panel v1 and command strip (no LLM chat in the map; the AI client is the chat) | P1 | M | `11-tactical-map.md` §5.2 | M4 |
| Think-time policy for agent seats | P1 | A M G | `09-c2-multi-agent.md` §8.4 | M6 |
| API-key commander example with optional spending caps | P1 | S | `09-c2-multi-agent.md` §8.5 | M6 |
| Broadcast channel; `broadcastCallsigns` setting | P1 | S A M G | `22-communications-diplomacy.md` §2–3 | M6 |
| Side stance war / ceasefire / peace mapped to `BDTeam.Allies` in both directions; structured terms; expiry | P1 | S A M G | `22-communications-diplomacy.md` §4 | M6 |
| Surrender: weapons safed, neutral `<Side>·Surrendered` team, referee lock, subdued scoring; surrender demands | P1 | S A M G | `22-communications-diplomacy.md` §5 | M6 |
| Violations (fired on surrendered, ceasefire broken, perfidy, terms breach) and penalties | P1 | S A M | `22-communications-diplomacy.md` §6 | M6 |
| End conditions for "subdue or talk it out": `side_surrendered`, `stance_held`, subdued counts in scores | P1 | S A M | `22-communications-diplomacy.md` §5, `10-scripting-sdk.md` §7 | M6 |
| Comms panel v2 (stance strip, templates, proposal cards, Apply) and the in-game Comms window | P1 | M G | `11-tactical-map.md` §5.2, `22-communications-diplomacy.md` §11 | M6 |
| Diplomatic channel with two-sided opt-in; negotiation protocol (proposals, counters, terms) | P2 | S A M G | `22-communications-diplomacy.md` §2, §4 | M7 |
| Psyops: unverified content, unauthenticated broadcast callsigns, private credibility tags; untrusted delivery and the doctor warning | P2 | S A M | `22-communications-diplomacy.md` §3, §10 | M7 |
| Agent-vs-agent negotiation scenario and its batch form | P2 | S A | `16-scenarios.md` §10 | M7 |
| Comms range, relays, submarine depth and jamming (off by default) | P2 | S A M | `22-communications-diplomacy.md` §7 | M8 |

## 18. Spawning anywhere — [R2, user requirement 2026-11-08]

VesselMover-style spawning is first-class and available from M1. Skizzy does its own exact pose and velocity and reuses BDA's spawners where they fit (`06-flight-control.md` §9.1 has the reuse table).

| Feature | Pri | Front ends | Where | M |
|---|---|---|---|---|
| **Spawn anywhere**: mid-air (altitude, heading, pitch, airspeed, engines, throttle, gear up), sea surface with buoyancy settle, submerged at depth, ground, runway; team, guard, AI, group and mission in the same call | P0 | S A M G | `06-flight-control.md` §9.1 | M1 |
| **Multi-spawn**: any count (no cap), a line abreast or in trail, auto or fixed spacing, clear-space search (terrain, buildings, water/land, vessels by bounding size plus margin), wrapping into parallel rows, a performance warning above 50, internal batches staggered over frames | P0 | S A M G | `06-flight-control.md` §9.1 | M1 |
| Formation and swarm spawn in one call (wedge, line, echelon, box, screen) | P0 | S A M G | `06-flight-control.md` §9.1 | M1 |
| In-game Spawn window (craft picker, mode, count field, spacing, rows, team, guard, AI, group, warning) | P0 | G | `06-flight-control.md` §9.1, `21-user-experience.md` §3 | M1 |
| Map drag-spawn from the library with the spawn card and slot preview | P0 | M | `11-tactical-map.md` §6 | M3 |
| Mid-battle reinforcements: scenario waves, respawn rules, BDA continuous spawning wrapper | P1 | S A M | `06-flight-control.md` §9.1, `09-c2-multi-agent.md` §9 | M6 |
| Orbit spawn | P2 | S A M | `06-flight-control.md` §9.1 | M8 |
| **OD1, BDArmory Overdrive feature**: multi-spawn with any count, rows and clear-space in the fork's VesselMover spawn window (a fork feature, not a hook) | P1 | G | `07-bdarmory-integration.md` §5.1 | M1 |
