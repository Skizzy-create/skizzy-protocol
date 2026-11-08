# 02 Architecture

## 1. Components

| Component | Runs in | Language / target | Responsibility |
|---|---|---|---|
| `Skizzy.Protocol.dll` | KSP and bridge | C# net48 + netstandard2.0 | wire constants, JSON DOM, msgpack reader/writer, schema builder/validator, permission model, track/order/lease record types, compact text formatter |
| `Skizzy.Host.dll` | KSP | C# net48 | socket server, sessions, actors, dispatcher, catalog (Tier 0–3), engines (Tactical, C2, Flight, Behaviour, Recorder, Camera, Editor/Craft, Scenario), in-game UI, kill switch |
| `Skizzy.BDArmory.dll` | KSP | C# net48, refs `BDArmory.dll` | BDArmory adapter: spawn, teams, WM, AI, radar, competition, hooks; reflection fallbacks for stock BDA Plus |
| `Skizzy.Naval.dll` | KSP | C# net48, soft refs via reflection | adapters for HullBreach, SubTrim (`IDepthCommandSource`), KP Naval ballast, BuoyancyAdjuster, ACA, NAS, MNWS, PRE, VesselMover, HyperEdit, Trajectories, KerbalEngineer, AtmosphereAutopilot, Hangar, KerbalKonstructs |
| `skizzy.exe` (`Skizzy.Cli`) | outside | C# .NET 10, self-contained | `mcp` (stdio MCP server), `serve` (Streamable HTTP MCP + native relay + `/metrics`), `battle run|compare`, `rec export|query|replay-server`, `map`, `doctor`. `skizzy-mcp.exe` = alias of `skizzy mcp` |
| `skizzy-map.exe` (`Skizzy.Map`) | outside | C# .NET 10, Avalonia + SkiaSharp | tactical map, planning tools, replay, headless PNG renderer (`skizzy map render`) |
| `Skizzy.Client` | outside | C# netstandard2.0 / net10 | C# SDK used by bridge, map, tests, third parties |
| `skizzy` (Python package) | outside | Python ≥ 3.11 | SDK, behaviours runtime, agents orchestrator, mini-sim, recordings reader |
| `Skizzy.Sim` | CI / dev | C# .NET 10 | FakeKsp: implements `IGameWorld` so host engines run headless |

### 1.1 Host internals

```
Skizzy.Host
├─ Core/            Unity-free: WebSocket server (RFC 6455), msgpack framing, sessions, actors, JSON-RPC router,
│                   catalog, dispatcher queues + budget, operations, confirmations, fault tracker, rate limits, audit writer
├─ Ksp/             KSPAddon bootstrap, pump MonoBehaviour, scene tracker, handle registry, Tier-0 reflector + PAW writer,
│                   stock providers (ksp.*), time/warp control, self-checks
├─ Engines/
│  ├─ Tactical/     TrackTable, SensorFusion, Iff, Frames (BRAA/bullseye), Zones, ThreatScoring, DeadReckoning, Snapshots
│  ├─ C2/           OrderBook, LeaseTable, Groups, Doctrine loader, RoeEvaluator, Triggers, Executors (order → actions)
│  ├─ Flight/       Holds (PID setpoints), ControllerArbiter, RouteExecutor, FormationExecutor, DirectControl dead-man
│  ├─ Behaviour/    BT runtime (zero-alloc), plug-in loader (`GameData/SkizzyProtocol/Behaviours/*.dll`), blackboard
│  ├─ Recorder/     Profiles, samplers, chunk writer (.skzrec), event/audit JSONL, perf telemetry
│  ├─ Camera/       screenshot pipeline, camera control
│  ├─ Craft/        CraftDocument model, editor round-trip, validation, blueprint assembler, sea trials
│  └─ Scenario/     scenario loader, spawn plans, snapshot/restore of battle state, batch cell runner (host side)
├─ Abi/             SkizzyAbi (Tier 3b entry point)
└─ Ui/              IMGUI windows (connections, permissions, leases, orders, recorder, map overlay), prompts, kill-switch banner
```

Engines depend on `IGameWorld` (vessels, parts, physics time, BDArmory facade). `Ksp/` implements it for KSP; `Skizzy.Sim` implements it for CI. This is what makes D13 (mock KSP) real rather than aspirational.

## 2. Threading model

Rule: **no Unity or KSP object is touched off the main thread, ever.** Carried from PLAN v0.2 §3.4 with these additions.

| Thread | Work |
|---|---|
| Acceptor | `TcpListener` on `127.0.0.1:47549`; WebSocket upgrade; `Origin`/`Host`/token checks; connection cap (default 32, raised from 4 because of multi-agent) |
| Per-connection reader | frame decode (text or binary), JSON or msgpack parse with size/depth caps, envelope validation, schema validation, permission pre-check against the catalog snapshot, enqueue `Job` |
| Per-connection writer | serialises results and snapshots (JSON or msgpack) from immutable records; bounded outbound queue (16 MB) with slow-client disconnect |
| Recorder writer | drains chunk queue to disk; fsyncs at chunk boundaries; never blocks the game |
| Audit writer | JSONL append |
| Main thread `Update` | drains `main`-affinity jobs within budget; UI; catalog maintenance; op stepping |
| Main thread `FixedUpdate` | drains `fixed`-affinity jobs; **engine ticks** in a fixed order: Flight (holds write `FlightCtrlState` via `OnFlyByWire`, so actually at the vessel callback), Behaviour, C2 executors, Tactical sampling, Recorder sampling; publishes snapshots with `Interlocked.Exchange` |
| Main thread `LateUpdate` | batches outbound events, one batch per session per frame |

### 2.1 Affinity classes

`main` (default), `fixed` (physics-aligned), `snapshot` (served from the last published snapshot on the network thread, zero main-thread cost), `any` (pure host). **All tactical queries, SITREPs, track subscriptions and recordings are `snapshot`**: a Haiku agent querying BRAA 20 times a second never costs the main thread anything beyond the one sampling pass that already runs.

### 2.2 Engine tick scheduling

Each engine has a rate and a budget; the scheduler is a tiny fixed-step accumulator per engine. Defaults:

| Engine | Rate | Budget per tick | Notes |
|---|---|---|---|
| Flight holds | every physics step (vessel callback) | 20 µs per hold | PIDs, pre-allocated |
| Behaviour trees | 10 Hz per tree (configurable 1–50) | 50 µs per tree | round-robin across vessels, max N trees per step |
| C2 executors | 5 Hz | 0.2 ms total | order progress, lease TTLs, trigger evaluation |
| Tactical picture | 5 Hz (1–20) | 1.0 ms at 1000 vessels | incremental; degrade rate before exceeding budget |
| Recorder sampling | per profile (0.1–50 Hz) | 0.5 ms total | shares TPE snapshot where possible |
| Dispatcher jobs | every frame | adaptive 0.5–8 ms, default 2 ms | as PLAN |

The total main-thread budget target is **≤ 4 ms per frame at 1000 vessels with everything on**, ≤ 0.1 ms with no client and no profile active (dispatcher disabled, engines idle because nothing is subscribed, no leases, no profiles).

### 2.3 Zero-allocation rules (normative, code-reviewed, CI-grepped)

- Hot paths are tagged `// skizzy:hot`: no LINQ, no `foreach` over interfaces, no closures capturing state, no string concatenation, no boxing, no `new` per tick.
- Tables are struct arrays (`TrackRecord[]`, `LeaseRecord[]`) sized by doubling; free lists reuse slots; ids are ints with a generation counter for stale detection.
- Names are interned once per vessel (`vessel.persistentId → string`), never rebuilt per tick.
- Snapshots are double-buffered: the engine writes buffer B while network threads read A; publish is a pointer swap.
- Serialisers write into pooled `byte[]` segments; msgpack writers are allocation-free; JSON writer uses a pooled `StringBuilder` and `Utf8` scratch buffers.
- Compiled field accessors (expression trees built once per module type, PLAN §4.1) replace `FieldInfo.GetValue`.
- Per-engine counters: ticks, µs p50/p99, GC alloc bytes (via `GC.GetAllocatedBytesForCurrentThread` deltas around the tick in debug builds) are exposed in `host.stats` and recorded in every recording.

## 3. Data flow

### 3.1 Observation path (game → clients)

```
FixedUpdate ─► TPE.Tick(): for each loaded vessel (BDATargetManager.LoadedVessels, O(N)):
               own-team truth; enemy detection from TargetInfo.detected[team]/detectedTime[team] (BDA's fog-of-war),
               per-unit radar contacts from VesselRadarData displayed contacts (hook/reflection), RWR pings, sonar;
               update TrackRecord in place; compute deltas vs last published; classify; score threats
           ─► publish TrackSnapshot (immutable) ─► network threads:
               • tac.query answers from the snapshot (filter, frame transform, format)
               • subscriptions compute per-subscriber deltas against the subscriber's last seq (thresholds, fields)
               • compact text / JSON / msgpack writers
           ─► Recorder samples the same snapshot (no second pass)
```

### 3.2 Command path (clients → game)

```
client ─► cap.call c2.order.issue ─► network thread: schema, permission, actor, lease check (lease table snapshot)
       ─► main thread (C2 engine): validate against doctrine/ROE, zones; create Order; pick Executor;
          Executor binds to BDA (CommandFlyTo, SetOverrideTarget, guard/fire authority, WM fields),
          to Flight holds, or marks the order "claimed by actor" when a script/agent executes it
       ─► events: order.accepted → order.executing → order.progress … → order.achieved|failed|superseded
```

### 3.3 Fast loop path (scripts)

```
Python 20 Hz loop: recv msgpack track delta ◄── host snapshot (5–20 Hz)
                   compute setpoints        ──► flight.setpoint batch (msgpack, one frame for 20 drones)
host: fixed-affinity job applies setpoints to holds; holds run at 50 Hz between updates
dead-man: no setpoint for 500 ms → hold keeps last; for 2 s → release to BDA AI or neutral (configurable)
```

## 4. Clocks and time

- **`ut`** (`Planetarium.GetUniversalTime()`) is the primary clock on every message, sample and event. **`frame`** is the host's physics-frame counter since scene load. **`wallUtc`** is attached to session events and recording chunk headers; the recording header stores the `(ut, wallUtc, frame)` triple at start and at every chunk so wall ↔ game mapping is piecewise-linear even across time-scale changes and pauses.
- `sim.clock` stream (1 Hz default) reports `{ut, frame, timeScale (Time.timeScale), physicsDeltaTime, warpMode, warpRate, paused, fpsEstimate, physicsStepsPerFrame}`.
- **Time scale**: `sim.time.set {scale}` drives BDArmory's `TIME_OVERRIDE`/`TIME_SCALE` path (`OtherUtils.SetTimeOverride`, `Time.timeScale`; `BDArmorySetup.cs:3366-3372`) so physics step size is unchanged. Physics warp (`TimeWarp`) is exposed too; on-rails warp is refused while leases or competitions are active.
- **Think-time control**: `sim.time.slow {scale, reason, maxSeconds}` by agents is a `control`-level capability; the orchestrator uses it when a decision deadline would be missed; `sim.hold`/`sim.step`/`sim.release` (lockstep, P1) uses `Time.timeScale = 0` for hold and runs exactly N physics steps by counting `FixedUpdate`s before pausing again (frame-granular, documented).
- **Subscriptions and timeouts are in UT**, with a wall-clock cap, so slow-motion does not break them.
- **Determinism limits**: Unity PhysX and KSP are not deterministic; BDArmory uses `UnityEngine.Random` globally and frame-dependent coroutines. Lockstep makes runs reproducible in *structure* (same orders at the same frames), not bit-identical. Batch testing is statistical (`14-testing-batch-sim.md`). A fork patch may seed `UnityEngine.Random.InitState` per competition (P3) to reduce variance from BDA's own randomness.

## 5. Failure modes and recovery

| Failure | Detection | Recovery |
|---|---|---|
| Agent/script dies or hangs | lease TTL (default 5 s heartbeat, 15 s expiry) | lease released; `lease.expired` event; vessel falls back per doctrine: `bda_guard` (default), `hold_last` or `neutral`; orders owned by the actor become `orphaned` and are offered to the parent actor |
| Bridge crashes | host sees socket close | sessions end; operations keep running (owned by host); on reconnect the bridge re-attaches with `op.list`, re-subscribes with `fromSeq` |
| Host plugin exception | fault tracker | capability auto-disabled after 5 faults/60 s; engines isolate per vessel (a throwing BT is disabled for that vessel only) |
| KSP scene change | `onGameSceneLoadRequested` | leases released, holds released, orders suspended with `suspended_scene`; when the flight scene returns and the vessels exist, `c2.resume` offers to resume |
| KSP crash | bridge loses socket; KSP.log tail | bridge degrades to offline mode: logs/recordings still readable; `skizzy doctor` reports |
| Slow client | outbound queue > 16 MB | disconnect with `SLOW_CONSUMER`; its leases expire normally |
| Budget overrun | engine timing | TPE and recorder lower their rate and flag `degraded`; dispatcher shrinks the budget; `host.stats.worst` names the culprit |
| LLM budget exhausted | orchestrator accounting | agent is frozen with `budget_exhausted`; its units fall back to doctrine defaults; commander is told |
| Order impossible (target dead, no ammo, out of range) | executor preconditions | `order.failed {reason}` with a machine-readable reason and a hint |
| Stale decision | `basedOnSeq` older than `maxStaleSeq` | `STALE_DECISION` error with the current seq; agent re-queries |

## 6. User data layout

```
<KSP>/
  GameData/SkizzyProtocol/            plugin DLLs, defaults, Annotations/, Behaviours/ (user plug-ins)
  GameData/SkizzyProtocol/PluginData/ auth.token, permissions.cfg, settings.cfg, audit/*.jsonl (host-written)
  Skizzy/                             user data root (sandboxed write root)
    library/craft/<name>/<version>/   .craft + manifest.json + trials.json + thumbs/
    library/blueprints/*.yaml
    library/doctrine/*.yaml           ROE, envelopes, formations, tactics parameters
    library/tactics/*.yaml            tactic templates
    library/scenarios/*.yaml
    library/logprofiles/*.yaml
    library/roles/*.cfg               SKIZZY_PART_ROLES, hull slot templates
    recordings/<yyyy-mm-dd>/<battle-id>/ *.skzrec, events.jsonl, meta.json, scores.json, bda.log
    runs/<timestamp>-<name>/          batch runner output (cells, summaries)
    memory/<role>/                    agent memory (AAR notes, craft performance db)
  AutoSpawn/                          BDA spawn folders (writable)
  saves/<profile>/Ships/{VAB,SPH}/    craft files (writable)
```

## 7. Observability

- `host.stats`: budget, p50/p99 main-thread ms, queue depth, sessions, per-engine tick µs and alloc bytes, degraded flags, auto-disabled capabilities, worst capabilities, GC collections per minute, frame time p50/p99, physics steps/frame.
- `host.engines` stream (1 Hz) with the same fields, recorded in every recording as the `perf` channel.
- The bridge exposes Prometheus text at `http://127.0.0.1:47550/metrics` (`skizzy serve`): host stats mirrored, MCP calls per tool, latency histograms, agent token usage and spend by actor.
- Structured host log with throttled categories (`[Skizzy.TPE]`, `[Skizzy.C2]`, …) written to `KSP.log` only for warnings and above; verbose logs go to `PluginData/host.log` with rotation.
- Trace ids: every order carries `traceId`; the audit, events and recordings include it so an AAR can reconstruct "why did DDG-2 turn north at UT 1203.4".
- `skizzy doctor`: checks token, port, host version, BDArmory hook availability (fork vs stock), Harmony presence, annotation pack versions, PRE range, and reports the idle-cost self-check results from the last session.

## 8. Performance budgets (acceptance numbers)

| Scenario | Budget |
|---|---|
| Idle (no client, no profile, no lease) | < 0.1 ms/frame, 0 B/frame GC alloc, dispatcher disabled |
| One map client + one Opus agent, 100 vessels | TPE ≤ 0.3 ms per tick at 5 Hz; total host ≤ 1.5 ms/frame |
| 20-drone swarm script at 20 Hz setpoints + 10 Hz track deltas | ≤ 0.6 ms/frame main thread; network RTT ≤ 5 ms p99 loopback |
| 200 vessels, 4 teams, fog-of-war, 2 agents, recorder 10 Hz, map | TPE ≤ 0.6 ms; recorder ≤ 0.4 ms; total ≤ 3 ms/frame |
| 1000 vessels (stress), god view, recorder 2 Hz | TPE ≤ 1.0 ms per tick (rate may degrade to 2 Hz); total ≤ 4 ms/frame; 0 B/tick steady-state alloc |
| Screenshot 1024 px JPEG | ≤ 30 ms main thread, ≤ 1 per 2 s per session |
| Editor batch apply of 50 part ops | one reload, ≤ 150 ms on a 300-part craft |

These are measured by in-game self-checks (`host.selfcheck.perf`) and asserted in milestone acceptance tests.
