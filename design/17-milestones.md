# 17 Milestones

Ordered so that every milestone delivers something the user can try in KSP, and so that an Opus 5.5 coding agent can implement each one from this design plus the previous milestone's code. Each entry lists scope, the in-game demo, acceptance tests, and the design files that govern it. Estimates are for one developer plus coding agents, part-time. **Every milestone must keep the robustness and performance rules of `02-architecture.md` §2–3 and §8**; the perf self-check is part of every acceptance.

Existing work: PLAN v0.2 M0/M1 code (host skeleton, WebSocket, dispatcher, Tier 0, BDA core tools, bridge) was built offline but never run in-game; it is the starting point for M0/M1 here, re-based on protocol 1.0.

| M | Theme | What the user can do after it |
|---|---|---|
| M0 | Foundations | connect Claude Code and a Python script, list vessels, see zero idle cost |
| M1 | BDA core + tactical picture v1 | spawn fleets, configure WMs, run a battle, query the picture in BRAA/compact text, record it |
| M2 | Control | fly ships/planes via holds and routes, formations, leases, kill switch, swarm script, co-fly with wingmen |
| M3 | Logging + map v1 | log anything, export/query, watch and replay on the tactical map, draw routes |
| M4 | C2 multi-agent | commander + unit agents with doctrine, sectors, budgets; the SAG-vs-raid scenario |
| M5 | Craft building | blueprint → ship → trials → library; the destroyer design loop |
| M6 | Batch sim + CI | 200-vessel × 50-run A/B; FakeKsp CI; 1000-vessel stress |
| M7 | Naval depth | sub ops, carrier ops, HullBreach damage control, EW/comms model, upstream PR |
| M8 | Hardening + release | security gate, CKAN, docs, lockstep, Streamable HTTP, AAR |

## M0 — Foundations (2–3 weeks)

Scope: monorepo as `02-architecture.md` §1 (+ `Skizzy.Sim`, `Skizzy.Client`, `python/skizzy`); host loads in KSP; loopback WebSocket with token/Origin/Host; JSON **and** msgpack frames; `session.hello` with actors and `view`; dispatcher with budgets and idle wake; `ksp.read` provider; `host.stats`/`host.selfcheck` (idle cost, dispatcher timing, PAW write probe); `IGameWorld` abstraction with the KSP implementation and a FakeKsp stub; bridge `skizzy mcp` with core toolset; Python `skizzy.connect`, `cap.call`, streams; CI pipeline.

Demo: `claude mcp add skizzy -- skizzy.exe mcp --ksp "D:\Epic Games\KerbalSpaceProgram\English"`; "list my vessels" works; a 10-line Python script lists vessels over msgpack.

Acceptance:
1. No client: < 0.1 ms/frame, 0 B/frame GC alloc (Unity profiler + self-check log). One idle client: < 0.2 ms.
2. `hello` → `ksp.vessels.list` RTT < 20 ms p50 in Flight; msgpack and JSON give identical results (conformance vectors).
3. Five scene switches with a connected client: no exceptions, no tracked-object growth.
4. Origin-bearing, token-less and bad-token connections refused (automated).
5. CI: protocol + host core + FakeKsp smoke tests green without KSP.

## M1 — BDArmory core and tactical picture v1 (4 weeks)

Scope: `Skizzy.BDArmory` with PLAN's M1/M3 tool set (spawn ring/template/single, remove, teams, guard, WM configure incl. multi-target/salvo, AI list/get/set mode/command, competition start/stop/state/scores, radar contacts via reflection, missiles in flight, damage report, settings, log tail, countermeasures, fire tools at `destructive`/ask); `bda.hooks`; fork patches **F1, F2, F5** landed in `BDArmory-Skizzy` with shim fallbacks; TPE v1 (per-team tracks from `TargetInfo` detection + VRD contacts + `FiredMissiles`, god/fog, IFF, classification v1, quality/age/DR, BRAA/bullseye/geo frames, `tac.query`, `tac.sitrep`, `tac.threats`, subscriptions with deltas, compact text); events ring + `tac_wait_events` in the bridge; Recorder v1 (`battle.standard` profile, `.skzrec` writer, `events.jsonl`, `skizzy rec export --jsonl|csv`); permissions file with `ROLE` rules and the Battle Lab preset; in-game window (connections, areas, grid, activity, kill switch stub).

Demo: from Claude Code: spawn `AutoSpawn/skizzy/sag` vs `raid`, set teams and guard, start a competition, then "what's the biggest threat to DDG-1 right now, in BRAA?" answers from the compact picture; recording saved and exported.

Acceptance:
1. PLAN M1 acceptance items 1–5 (spawn → scores round trip, hidden denied tools, salvo clamp read-back, kill switch cancels spawn, scene change safe).
2. Fog view: a hostile beyond radar range is absent, appears `UNK` on detection with quality, becomes `HOS` on launch, `DR` after loss, dropped after 90 s.
3. BRAA within 1°/50 m of hand computation at 30 km; compact top-10 threats < 600 chars.
4. TPE tick ≤ 0.3 ms at 100 vessels; 0 B/tick alloc after warm-up; `host.stats` shows it.
5. `bda.hooks` reports `fork` on the user's install and `reflection` on a stock BDA Plus test install; `bda.assign_target` works in both (15 s refresh in reflection mode).
6. Recording of a 10-minute battle exports to CSV; `events.jsonl` contains `missile.launch` within one tick of BDA launch logs.

## M2 — Control (4 weeks)

Scope: Flight engine (mixer, holds incl. boat/sub, setpoint batch, direct with dead-man), leases (channels, precedence, TTL, preemption, implicit human lease), Route executor (ships via `CommandFlyTo`; aircraft via holds and via **F6**), Formation executor (WingCommander + **F7**; Skizzy station keeping), hand-off (BDA, AA, manual), kill switch full semantics, VesselMover placement, SubTrim `IDepthCommandSource` adapter (`Skizzy.Naval` v1), orders v1 (`move_*`, `follow`, `formation`, `engage`, `weapons_*`, `set_depth`, `report`, `claim`) with executors, groups, `sim.time.set/slow`; Python SDK v1 (mirror, setpoint batch, orders, leases, clock); `skizzy run` supervisor; `skizzy.sim` mini-sim; fork patches **F3, F4, F7, F6** (F8 if time permits).

Demo: the swarm script (`16-scenarios.md` §3) and the co-flying scenario (§4); a destroyer follows a 6-leg route; kill switch returns everything to BDA.

Acceptance: `06-flight-control.md` §12 items 1–6; `09-c2-multi-agent.md` §10 item 1; `10-scripting-sdk.md` §9 items 1–2; fire authority `tight` verified: a ship with an assigned target never fires at a non-assigned hostile (F3), and the stock fallback is documented as partial.

## M3 — Logging and the tactical map v1 (5 weeks)

Scope: log profiles (any selector/rate/on-change), `rec.*` capabilities, `.skzrec` index/repair/compact, `skizzy rec export --parquet`, `rec query` (built-in engine; DuckDB optional), `rec metrics`, replay server; **map app v1**: live tracks with 2525 symbols, layers (tracks, trails, ranges, engagements, zones, orders, leases), fog/god windows, panels (units, orders, leases, events, recorder, health), planning tools (route, loiter, zone, assign target, ROE, lease), shared annotations, replay/scrub; `tac.zones.*`, `tac.bullseye.*`; in-game recorder window; Prometheus `/metrics` in `skizzy serve`.

Demo: the user watches a Blue-vs-Red AI battle on two fog windows and a god window, draws a route for DDG-2 (§8), stops the recording and replays it.

Acceptance: `11-tactical-map.md` §11 items 1–4; `12-logging-recording-replay.md` §9 items 1–4; scenario §9 end-to-end from Claude Code.

## M4 — C2 multi-agent (5 weeks)

Scope: C2 engine completion (order taxonomy v1 incl. `defend_sector`, `intercept`, `screen`, `escort`, `patrol_area`, `emcon`; constraints/ROE/zones evaluation; achievement tests; supersession; stale-decision check), doctrine loader and `c2.doctrine.*`, triggers, tactics library v1 (`ad_umbrella`, `cap`, `intercept`, `screen`, `pincer`, `saturation`), WTA solver, `c2.ask_human`; **orchestrator `skizzy.agents`** (roles, models `claude-haiku-5-5`/`claude-opus-5-5`, event-driven wake, digests, prompt-cache-stable prefixes, budgets, think-time slow-mo, `resume`), bridge Streamable HTTP with actor identity and toolsets per role, scripted-fake-LLM tests; fork patches **F9, F10, F12**; behaviour engine v1 (BT interpreter, `sub_shoot_and_scoot`, C# plug-in loader).

Demo: scenario §2 (SAG + sub vs 40 missiles) with a commander, an AD coordinator and three unit agents; the user watches on the map and answers one `ask_human`.

Acceptance: `09-c2-multi-agent.md` §10 items 2–5; agent cost report per battle matches `usage` sums; cache read tokens > 70 % of input on steady-state turns; orchestrator kill → fallbacks within 15 s; scripted-LLM CI suite green.

## M5 — Craft building (5 weeks)

Scope: CraftDocument + `craft.*` headless ops; editor round-trip (`ksp.editor.*`, reload spike first); `ksp.parts.catalog` with roles; part-role packs for LBP, MNWS, Iron Seas, NAS, ACA, Maritime; hull slot templates for the destroyer and carrier hulls in the pack + `craft.probe_slots`; blueprint assembler; validation report (incl. GM and waterline); `craft.trial` profiles (`naval.basic`, `sub.basic`, `air.basic`, `combat.smoke`); library with versions/trials/battles; `lib.*`; loadout editor; `saves/skizzy-range` provisioning script; builder toolset in the bridge.

Demo: scenario §1 (design the destroyer and iterate) end-to-end from Claude Code.

Acceptance: `05-craft-building.md` §9 items 1–4; headless `craft.diff` after an editor round-trip is empty; editor batch of 50 ops ≤ 150 ms on a 300-part craft; a blueprint with a missing role fails with a clear assembly report.

## M6 — Batch simulation and CI depth (4 weeks)

Scope: scenario schema v1 complete (waves, end conditions, matrix, expect), `skizzy battle run|compare` with checkpoint/resume and two-key armed batch mode, `sim.scenario.snapshot/restore`, validation scenario pack (`14-testing-batch-sim.md` §6), FakeKsp full `IBdaFacade`, conformance suite, soak tests, perf gates in self-check, 1000-vessel stress optimisation (round-robin VRD reads, columnar layouts, recorder lz4), fork patch **F11** (spawner fixes), `bda.evolution.*`/`bda.autotune.*` wrappers.

Demo: scenario §7 (200 × 50 A/B) runs overnight; `skizzy rec compare` prints CIs; the 1000-vessel stress cell stays within budgets.

Acceptance: `14-testing-batch-sim.md` §7 items 1–3; `02-architecture.md` §8 budgets for 200 and 1000 vessels met; injected regression caught by `expect`.

## M7 — Naval depth (5 weeks)

Scope: sub ops (sonar-only picture polish: bearing-only association, active ping events, comms/EW model v1), HullBreach events and damage-control orders (what the patched source allows; propose HullBreach changes for pumps), KP Naval ballast backend, ACA carrier launch/recover state machine + Hangar, Trajectories/KerbalEngineer adapters, in-game map overlay, headless map render + `ksp.camera.look_at/annotate/multi`, agent memory (AAR store, analyst role), tactics `asw_screen`, `asw_prosecute`, `sub_ambush`, `strike_package`, `carrier_launch/recover`; upstream PR to BDA Plus with F1–F12 (fork-first already shipping).

Demo: scenarios §5 (carrier strike: launch + marshal; recovery assisted) and §6 (sub ambush).

Acceptance: `08-tactical-picture.md` §12 item 5; sub scenario completes with two torpedo hits registered; carrier launches 6 aircraft unattended at ≤ 60 s cadence; recovery captures a wire in ≥ 3 of 5 attempts with BDA-assisted approach (documented stretch); comms model delays a datalink track by the configured latency in FakeKsp and in KSP.

## M8 — Hardening and release (4 weeks)

Scope: security gate (`15-security-permissions.md` §8), lockstep `sim.hold/step/release`, Streamable HTTP hardening, CKAN/SpaceDock packaging (`SkizzyProtocol`, `SkizzyNaval` recommends), docs site, Python package on PyPI, `skizzy doctor`, AAR generation from recordings (analyst), optional Harmony stopgap for stock BDA Plus hooks (off by default), MCP SDK pin review, performance soak report, first public spec 1.0.

Acceptance: fresh install via CKAN + `pip install skizzy` reproduces M1–M4 demos; security checklist all green; 8-hour soak slope < 1 MB/h; spec conformance suite green against host + FakeKsp and host + KSP.

## Implementation notes for coding agents

- Start each milestone by reading `00-overview.md`, `02-architecture.md`, `03-protocol.md` and the governing file(s); then the previous milestone's code; then write the acceptance tests first (FakeKsp where possible).
- Never touch `D:\KerbalSpaceProgram\BDArmory` from the Skizzy build; fork patches are separate PRs to `BDArmory-Skizzy` with their own tests; the shim builds against the **installed** `BDArmory.dll` copied to `refs/`.
- Every hot path gets a `// skizzy:hot` tag and a CI grep; every engine gets a timing counter on day one.
- Protocol changes update the JSON Schemas and the conformance vectors in the same PR; the Python models are regenerated, never hand-edited.
