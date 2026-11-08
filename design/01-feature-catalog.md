# 01 Feature catalog

Every feature of skizzy-protocol with a priority and an origin tag. `[IDEAS]` = in `IDEAS.md`; `[ADDED]` = added by this design (with a one-line rationale). Priorities: **P0** MVP (M0–M2), **P1** core (M3–M5), **P2** nice-to-have (M6–M7), **P3** later. Milestone mapping is in `17-milestones.md`.

Reading the tables: "Front ends" lists where the feature is usable: **S**cripts, **A**gents (MCP), **M**ap, **G**ame (in-game UI/controls). The rule "nothing exists in only one front end" is checked here: every row has at least S+A, and M/G where a UI makes sense.

## 1. Core server

| Feature | Pri | Origin | Front ends | Notes / rationale |
|---|---|---|---|---|
| In-game host plugin, C# bridge exe, shared protocol library | P0 | [IDEAS] | S A M G | PLAN v0.2 architecture kept |
| Native WebSocket socket (JSON) for scripts and high-rate agents | P0 | [IDEAS] | S | |
| MCP projection (stdio) | P0 | [IDEAS] | A | |
| MCP Streamable HTTP on loopback with per-session actor identity | P1 | [ADDED] | A | multi-agent needs N agents through one bridge without N processes |
| MessagePack binary encoding negotiated per session | P0 | [ADDED] | S M | 5–10× smaller than JSON for track/telemetry streams; zero-garbage parse in Python/C# |
| Compact text encoding for LLM-facing tactical data | P0 | [ADDED] | A | token economy: one track = one short line |
| Main-thread dispatcher, per-frame adaptive budget, zero idle cost | P0 | [IDEAS] | — | |
| Zero-alloc hot paths, pooled buffers, struct tables | P0 | [IDEAS] | — | |
| Fail-closed tools, fault tracker, no exceptions into KSP | P0 | [IDEAS] | — | |
| Graceful scene changes, handle invalidation, op cancellation | P0 | [IDEAS] | — | |
| Operations (long-running), progress, re-attach after reconnect | P0 | [IDEAS] | S A M | |
| Streams (sampled / on-change / delta), events ring buffer | P0 | [IDEAS] | S A M | |
| Triggers: host-evaluated conditions that emit events | P1 | [ADDED] | S A M | lets agents sleep until "any hostile within 30 km of group X" without polling |
| Catalog with revisions, search, describe; stable tool list across scenes | P0 | [IDEAS] | A | |
| Clock model: `ut` + `frame` on every message, `sim.clock` stream, wall-clock mapping | P0 | [ADDED] | S A M | time sync is a prerequisite for logs, replay and lockstep |
| Lockstep: `sim.hold` / `sim.step(n)` / `sim.release` | P1 | [ADDED] | S A | deterministic-ish stepping for tests and "pause while agents think" |
| Observability of the system itself: `host.stats`, per-engine timings, GC alloc counters, Prometheus endpoint in bridge, trace ids per order | P0/P1 | [ADDED] | S A M G | the user's first-class requirement is performance; it must be measurable |
| Protocol versioning: `MAJOR.MINOR`, capability `@N`, schema `$id`s, msgpack schema ids | P0 | [ADDED] | — | |
| Hot reload of annotations, doctrine, behaviour trees, C# plug-ins | P2 | [ADDED] | S A G | iteration speed without restarting KSP |

## 2. Access to KSP and every mod

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Tier 0 reflection: fields, events, actions, action groups, resources, staging, part tree, crew, post-MM configs | P0 | [IDEAS] | S A G | PLAN v0.2 §4 |
| Tier 1 annotation packs (descriptions, units, safety, hidden properties, statics, declared methods) | P0 | [IDEAS] | S A | shipped packs for BDArmory, HullBreach, SubTrim, KP Naval, ACA, NAS, MNWS, PRE |
| Tier 2 duck-typed attributes, Tier 3 contract DLL / JSON-delegate ABI | P1 | [IDEAS] | S A | |
| Deep reflection (private/static/singletons), off by default, per mod | P1 | [IDEAS] | S A | |
| Bulk query selector across vessels/parts/modules (`ksp.query`) | P0 | [IDEAS] | S A M | |
| Mod adapters: PhysicsRangeExtender, VesselMover, HyperEdit, HullBreach, SubTrim, KP Naval ballast, BuoyancyAdjuster, ACA (catapults/wires), NAS, MNWS VLS, KerbalEngineer, Trajectories, AtmosphereAutopilot, Hangar, KerbalKonstructs, WildBlueTools | P1 | [IDEAS] | S A M G | `04-ksp-and-mod-access.md` §5 |
| Part-role catalog (`SKIZZY_PART_ROLES`): which parts are VLS cells, CIWS, radars, sonars, hulls, catapults | P1 | [ADDED] | S A | blueprints and symbology need semantic roles, not part names |
| Vessel classification (destroyer/carrier/sub/fighter/drone/missile) from parts and AI type | P0 | [ADDED] | S A M | drives map symbols, threat scoring, doctrine |

## 3. Vessel creation and craft building

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Editor control: new/load, add/remove/move/rotate, node and surface attach, symmetry, staging, module config, save | P1 | [IDEAS] | S A G | |
| Headless `.craft` document model: read, edit, write, diff | P0 | [IDEAS] | S A | needed before the editor path; no scene requirement |
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
| Throttle, SAS modes, RCS, staging, gear, brakes, lights, action groups, active vessel switch | P0 | [IDEAS] | S A G | |
| Setpoint holds at physics rate: heading, pitch, roll, altitude, speed, depth, vertical speed | P0 | [IDEAS] | S A M G | |
| Direct control channel (raw axes at ≤ 50 Hz with dead-man) | P1 | [ADDED] | S | swarm scripts and external autopilots want it; dead-man neutralises on silence |
| Waypoints/routes, orbit/loiter, formation keeping (ships and aircraft) | P0 | [IDEAS] | S A M | route executor + WingCommander formations |
| Hand-off to/from BDArmory AI and AtmosphereAutopilot | P0 | [IDEAS] | S A G | |
| Human + agent co-flying with arbitration | P0 | [IDEAS] | G S A | |
| Kill switch (hotkey, toolbar, map, API) that always restores manual control | P0 | [IDEAS] | S A M G | |
| Scene setup and positioning (VesselMover/HyperEdit style): place, lift, drop, teleport, orient | P0 | [IDEAS] | S A M | |
| Submarine depth control via SubTrim `IDepthCommandSource` + KP Naval ballast | P1 | [ADDED] | S A M G | SubTrim exposes a plug-in API for exactly this |
| Carrier launch/recovery sequencing (ACA catapults, wires, hooks; Hangar) | P2 | [ADDED] | S A M | the user flies carriers; deck cycles are a state machine, not a one-shot |
| Collision/deconfliction advisories for scripted swarms | P2 | [ADDED] | S | BDA avoids collisions for its AI only |

## 5. BDArmory

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Spawning: rings, exact formations, single vessels, templates | P0 | [IDEAS] | S A M G | |
| Teams, guard mode, full weapon-manager configuration incl. 1024 targets / 128 per target / parallel salvo | P0 | [IDEAS] | S A M G | |
| AI modes (pilot/surface/sub/VTOL/orbital), live parameter changes, commands (attack/fly-to/follow/waypoints/take-off/release) | P0 | [IDEAS] | S A M G | |
| Direct weapons: fire missile at target, guns, countermeasures incl. bubbles, radar/sonar on/off/lock, targeting pods | P0 | [IDEAS] | S A M G | `destructive`, ask by default |
| Competitions, tournaments, scores, damage reports, missiles in flight, settings, log tailing | P0 | [IDEAS] | S A M | |
| Fork hooks: steering override, target assignment, fire authority (hold/tight/free), formation positions, arbitrary waypoints, events | P0 | [IDEAS] | — | `07-bdarmory-integration.md` §5 |
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
| Comms/datalink model: per-team link graph, latency, jamming degrading the shared picture (optional realism) | P2 | [ADDED] | S A M | EW realism; default is perfect comms |

## 7. Command and control

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Orders as objects: intent, constraints, ROE, priority, owner, status, timeout | P0 | [IDEAS] | S A M G | |
| Hierarchy: commander → groups → units; roles | P0 | [IDEAS] | S A M | |
| Leases per control channel, TTL heartbeats, preemption events | P0 | [IDEAS]+[ADDED] | S A M G | channels and TTL added |
| Precedence human > commander > unit > script > BDA default (numeric bands) | P0 | [IDEAS] | — | |
| Tactics library: CAP, intercept, pincer, AD umbrella/sectors, ASW screen, strike package, saturation, sub ambush, carrier launch/recovery | P1 | [IDEAS] | S A M | |
| Model per role; events pushed to agents | P0 | [IDEAS] | A | |
| Time control for thinking (auto slow-mo/pause) | P1 | [IDEAS] | A G | |
| LLMs one actor among several | P0 | [IDEAS] | — | |
| Order lifecycle with acknowledgement, progress, unachievable reasons, supersession | P0 | [ADDED] | S A M | |
| Stale-decision rejection: orders carry `basedOnSeq`/`validUntilUt` | P1 | [ADDED] | S A | an LLM decision made 8 s ago may be wrong now |
| Doctrine files (YAML): ROE, envelopes, formations, sectors, triggers → tactics | P1 | [ADDED] | S A M | |
| Agent memory between battles (AAR store, per-role notes, craft performance DB) | P2 | [ADDED] | A | |
| Token/cost budgets per agent and per battle, spend kill switch | P1 | [ADDED] | A G | |
| Scenario director: waves, timelines, scripted triggers | P1 | [ADDED] | S A M | |
| Multiple humans (several map operators) | P2 | [ADDED] | M | loopback in v1; SSH tunnel documented; LAN binding P3 |

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
| Live map with APP-6/2525 symbols per type and affiliation | P1 | [IDEAS] | M | |
| Layers: tracks, history, sensor/weapon ranges, engagement lines, orders, threat axes | P1 | [IDEAS] | M | |
| Planning: draw routes, areas, formations; assign targets; set ROE; agent orders appear live | P1 | [IDEAS] | M | |
| Fog-of-war or god view | P1 | [IDEAS] | M | |
| External desktop app + in-game overlay from the same stream | P1/P2 | [IDEAS] | M G | |
| Headless map render to PNG for agents | P2 | [ADDED] | A | the "picture" an LLM can look at when it needs geometry |
| Replay/scrub of recordings in the map, AAR mode | P1 | [IDEAS] | M | |
| Shared annotations (drawings, labels) synchronised to all clients | P1 | [ADDED] | S A M | agents and humans plan on the same canvas |

## 10. Logging, recording, replay

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Log anything by query at any rate (on change or N Hz) | P0 | [IDEAS] | S A M G | |
| Named log profiles, switchable per battle | P0 | [IDEAS] | S A M G | |
| Saved to disk in open formats: `.skzrec` (msgpack) + JSONL; Parquet/CSV export | P0 | [IDEAS] | S | |
| Query saved logs by time/vessel/team/event | P1 | [IDEAS] | S A M | |
| Full battle recording and replay | P1 | [IDEAS] | S M | |
| Metrics and A/B reports | P1 | [IDEAS] | S A | |
| Session audit log | P0 | [IDEAS] | G S | |
| Recording as agent context (AAR summaries) | P2 | [IDEAS] | A | |
| Performance telemetry stream (frame time, physics steps, GC, host budgets) in every recording | P0 | [ADDED] | S M | regressions must be visible next to the battle |

## 11. Vision

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Screenshots as images, resolution/rate caps | P0 | [IDEAS] | A S | |
| Camera modes: follow, orbit, chase, free, look-at-target | P0 | [IDEAS] | A S G | |
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
| Soak tests (hours, memory growth gates) and perf gates in self-checks | P1 | [ADDED] | G | leaks were the user's pain |

## 13. Permissions and safety

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Levels read/control/destructive/cheat/unsafe, allow/ask/deny per mod and tool | P0 | [IDEAS] | G | |
| Defaults: editor, flight, screenshots allowed; destructive ask | P0 | [IDEAS] | G | D11 |
| Kill switch, ROE guards, friendly-fire checks, audit, localhost only, token | P0 | [IDEAS] | G S A M | |
| Actor identities and lease ACLs (which roles may preempt) | P0 | [ADDED] | G | |
| Two-key confirmation for bulk destructive/cheat operations (remove all vessels, 200-vessel spawn) | P1 | [ADDED] | G A | |
| Prompt-injection isolation of game text; no permission changes over the wire | P0 | [IDEAS]/[ADDED] | — | |

## 14. Extensibility and extras

| Feature | Pri | Origin | Front ends | Notes |
|---|---|---|---|---|
| Other mods add unit types, sensors, weapons, symbology via annotation packs | P1 | [IDEAS] | — | |
| Logistics, rearm, damage control | P2/P3 | [IDEAS] | S A M | damage control via HullBreach/SubTrim first |
| Comms degradation and EW | P2 | [IDEAS] | S A M | |
| Web dashboard | P3 | [IDEAS] | — | superseded by the desktop map; a read-only web view may come later |
| AI battle commentator | P3 | [IDEAS] | A | from recordings |
| CKAN packaging, docs site | P2 | [ADDED] | — | |

Count: 112 features, 46 of them `[ADDED]`.
