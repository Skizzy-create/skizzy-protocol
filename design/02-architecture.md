# 02 Architecture

## 1. Components

| Component | Runs in | Language / target | Responsibility |
|---|---|---|---|
| `Skizzy.Protocol.dll` | KSP and bridge | **[R]** two builds of one source tree: net48 for KSP (compiled against `KSP_x64_Data/Managed`), netstandard2.0 for the bridge, map and SDK | wire constants, JSON DOM, msgpack reader/writer, schema builder/validator, permission model, track/order/lease record types, compact text formatter |
| `Skizzy.Host.dll` | KSP | C# net48 | socket server, sessions, actors, dispatcher, catalog (Tier 0–3), engines (Tactical, C2, Flight, Behaviour, Recorder, Camera, Editor/Craft, Scenario), in-game UI, kill switch |
| `Skizzy.BDArmory.dll` | KSP | C# net48, refs `BDArmory.dll` | BDArmory adapter: spawn, teams, WM, AI, radar, competition, hooks; reflection fallbacks for stock BDA Plus |
| `Skizzy.Naval.dll` | KSP | C# net48, soft refs via reflection (so it cannot implement a mod's interfaces; see `04-ksp-and-mod-access.md` §5) | adapters for HullBreach, SubTrim (`manualDepth` via the PAW path; optional separate `Skizzy.SubTrim.dll` for `IDepthCommandSource`), WildBlueTools, KP Naval ballast, BuoyancyAdjuster, ACA, NAS, MNWS, PRE, VesselMover, HyperEdit, Trajectories, KerbalEngineer, AtmosphereAutopilot, Hangar, KerbalKonstructs |
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

**Build guard [R].** KSP's `Managed` folder has no `netstandard.dll`, `System.Memory` or `System.Runtime.CompilerServices.Unsafe` (KSPBurst ships its own copies of the latter two, which is a version-conflict trap). The in-game assemblies are therefore compiled with `NoStdLib` against KSP's own `mscorlib.dll`, `System*.dll`, `UnityEngine*.dll` and `Assembly-CSharp*.dll`, so any API missing from Unity's Mono profile is a compile error rather than a `MissingMethodException` in the field. `System.Span<T>` and `Memory<T>` are not used in shared code (in this mscorlib they are internal). CI also runs a reference check that fails if an in-game DLL references `netstandard` or any assembly not present in `KSP_x64_Data/Managed` or the declared mod DLLs.

Engines depend on `IGameWorld` (vessels, parts, physics time, BDArmory facade). `Ksp/` implements it for KSP; `Skizzy.Sim` implements it for CI. This is what makes D13 (mock KSP) real rather than aspirational.

## 2. Threading model

Rule: **no Unity or KSP object is touched off the main thread, ever.** Carried from PLAN v0.2 §3.4 with these additions. **[R]** The zero-allocation rule (§2.3) applies to the network threads too: Unity 2019.4 runs Mono with the Boehm collector (`mono-2.0-bdwgc`, incremental with `gc-max-time-slice=3` in `boot.config`), which has one shared heap, so garbage made by a reader thread triggers the same collections that hitch the game. Main-thread wake-up for queued work uses `UnitySynchronizationContext` posting (present in this build) so an idle host costs nothing per frame.

| Thread | Work |
|---|---|
| Acceptor | `TcpListener` on `127.0.0.1:47549`; WebSocket upgrade; `Origin`/`Host`/token checks; connection cap (default 32, raised from 4 because of multi-agent) |
| Per-connection reader | frame decode (text or binary), JSON or msgpack parse with size/depth caps, envelope validation, schema validation, permission pre-check against the catalog snapshot, enqueue `Job` |
| Per-connection writer | serialises results and snapshots (JSON or msgpack) from immutable records; bounded outbound queue (16 MB) with slow-client disconnect; JPEG encoding of screenshot frames (`ImageConversion.EncodeArrayToJPG` on raw bytes; thread safety confirmed in the M0 spike, else it moves to the main thread at ≤ 8 ms per 1024 px frame) |
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
| Tactical picture | 5 Hz (1–20) | **[R]** time-sliced: amortised ≤ 0.25 ms per physics step, ≤ 1.0 ms in the worst step | one full pass is spread over the steps of one period (10 steps at 5 Hz and 50 Hz physics), vessels processed in fixed slices; snapshot published at the end of the pass; degrade rate before exceeding budget |
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
- Per-engine counters: ticks, µs p50/p99 and allocation are exposed in `host.stats` and recorded in every recording. **[R]** Unity 2019.4's mscorlib has no `GC.GetAllocatedBytesForCurrentThread`, so allocation is measured in two ways (§8.1): exact 0-byte asserts per hot path in CI under .NET 10 (where that API exists) running the same engine code on `Skizzy.Sim`, and in-game heap-slope gates from `Profiler.GetMonoUsedSizeLong()`, `Profiler.GetTotalAllocatedMemoryLong()` and `GC.CollectionCount(0)`.

### 2.4 Parallelism (P2, not before M8) — [IDEAS, added 2026-11-08]

Use more cores and the GPU where it is cheap and safe, without complicating M0–M3.

- **Constraint, stated plainly:** KSP and Unity APIs are main-thread only. No worker thread ever touches a `Vessel`, `Part`, `PartModule`, `Transform`, `GameObject`, BDArmory object or Unity API (including `Debug.Log`). Sampling stays on the main thread in its time slice.
- **Outside KSP, parallel by default:** the bridge (async I/O, one task per session), the map (Skia GPU rendering on Avalonia's render thread; clustering and label placement on a worker, `11-tactical-map.md` §10), recording export (one Parquet writer per channel in parallel), batch analysis and `rec query` (parallel over runs and cells; DuckDB uses its own threads), the mini-sim.
- **Inside KSP, at most this:** pure math on snapshot copies may move to **one** dedicated long-lived worker thread (not the thread pool; a single-producer/single-consumer queue of pre-allocated work items, results applied on the main thread at the next tick). Candidates, in order: threat scoring, BRAA/CPA batches for many subscribers (most already run on the network threads from the snapshot), clustering for the in-game overlay, WTA solving. Each candidate moves only if a benchmark on the user's machine shows it saves ≥ 0.2 ms per frame; the zero-allocation rule (§2.3) applies to the worker too.
- **Not done:** Unity Jobs/Burst (KSPBurst exists, but jobs would need blittable copies of the whole world every tick and add a hard dependency for a sub-millisecond gain), parallel physics, anything that changes BDArmory's own threading.

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
dead-man [R]: applies only to `flight.direct` and to setpoint streams that declare deadmanMs (default 500 ms):
          silence > deadmanMs → that stream's onExpire action (default hold_last for setpoints, neutral for direct axes)
          plain holds set by flight.hold persist until released or until their lease expires (§5)
```

## 4. Clocks and time

- **`ut`** (`Planetarium.GetUniversalTime()`) is the primary clock on every message, sample and event. **`frame`** is the host's physics-frame counter since scene load. **`wallUtc`** is attached to session events and recording chunk headers; the recording header stores the `(ut, wallUtc, frame)` triple at start and at every chunk so wall ↔ game mapping is piecewise-linear even across time-scale changes and pauses.
- `sim.clock` stream (1 Hz default) reports `{ut, frame, timeScale (Time.timeScale), physicsDeltaTime, warpMode, warpRate, paused, fpsEstimate, physicsStepsPerFrame}`.
- **Time scale**: `sim.time.set {scale}` drives BDArmory's `TIME_OVERRIDE`/`TIME_SCALE` path (`OtherUtils.SetTimeOverride`, `Time.timeScale`; `BDArmorySetup.cs:3366-3372`) so physics step size is unchanged. Physics warp (`TimeWarp`) is exposed too; on-rails warp is refused while leases or competitions are active. **[R]** KSP's pause menu sets `Time.timeScale = 0` and restores `1` on unpause, which silently drops a BDA time override; the host re-applies the requested scale on `onGameUnpause`. Full warp/pause policy: `20-runtime-lifecycle.md` §3.
- **BDA timestamps [R]**: `TargetInfo.detectedTime` and other BDA timers are Unity `Time.time` (scaled game seconds since start), not UT. The TPE keeps a `(Time.time, ut)` pair per tick and converts; no code compares the two directly.
- **Think-time control**: `sim.time.slow {scale, reason, maxSeconds}` by agents is a `control`-level capability; the orchestrator uses it when a decision deadline would be missed; `sim.hold`/`sim.step`/`sim.release` (lockstep, P1) uses `Time.timeScale = 0` for hold and runs exactly N physics steps by counting `FixedUpdate`s before pausing again. **[R]** Unity may run several `FixedUpdate`s in one rendered frame, so exact stepping runs at a low `timeScale` (≤ 1 physics step per frame) and sets `timeScale = 0` from the step-counting `FixedUpdate` itself (`20-runtime-lifecycle.md` §4).
- **Subscriptions and timeouts are in UT**, with a wall-clock cap, so slow-motion does not break them.
- **Determinism limits**: Unity PhysX and KSP are not deterministic; BDArmory uses `UnityEngine.Random` globally and frame-dependent coroutines. Lockstep makes runs reproducible in *structure* (same orders at the same frames), not bit-identical. Batch testing is statistical (`14-testing-batch-sim.md`). A fork patch may seed `UnityEngine.Random.InitState` per competition (P3) to reduce variance from BDA's own randomness.

## 5. Failure modes and recovery

| Failure | Detection | Recovery |
|---|---|---|
| Agent/script dies or hangs | lease TTL (default 5 s heartbeat, 15 s expiry) | lease released; `lease.expired` event; the channel's `onExpire` action runs. **[R]** One enum everywhere: `onExpire: bda_ai \| bda_guard \| hold_last \| neutral \| keep`. `bda_ai` = BDA pilot active, guard as it was; `bda_guard` = BDA pilot active and guard on (default for `steer`); `hold_last` = hold the last setpoints until a human or BDA takes over; `neutral` = zero axes, throttle unchanged (default for `flight.direct`); `keep` = leave the channel as it is (default for `ai_params`, `sensors`). Not every value is valid on every channel (`09-c2-multi-agent.md` §4). Orders owned by the actor become `orphaned` (event `order.orphaned`, `owner: null`) and are offered to the parent actor |
| Bridge crashes | host sees socket close | sessions end; operations keep running (owned by host); on reconnect the bridge re-attaches with `op.list`, re-subscribes with `fromSeq` |
| Host plugin exception | fault tracker | capability auto-disabled after 5 faults/60 s; engines isolate per vessel (a throwing BT is disabled for that vessel only) |
| KSP scene change or quickload | `onGameSceneLoadRequested` | leases released, holds released, orders `suspended` (reason `scene`); handles invalidated (`NOT_FOUND`, reason `stale`); when the flight scene returns and the vessels exist (matched by persistentId, then name), `c2.resume` offers to resume. Details: `20-runtime-lifecycle.md` §5 |
| Active vessel changes (death, BDA switch, human switch) | `onVesselChange` | the implicit human lease moves to the new active vessel only when the human touches an input; camera follow updates; `vessel.active_changed` event (`20-runtime-lifecycle.md` §2) |
| Vessel goes on rails / unloads | `onVesselGoOnRails`, `onVesselUnloaded` | holds and direct streams on that vessel report `PRECONDITION_FAILED` (reason `vessel_packed`/`vessel_unloaded`); leases stay; holds resume on `onVesselGoOffRails` unless released (`20-runtime-lifecycle.md` §1) |
| Port 47549 in use | bind failure | try 47549–47559, write the bound port, pid and start time to `PluginData/endpoint.json`; clients read it; the in-game window shows the port |
| Host restarted (KSP restart) | socket close, new `endpoint.json` | clients auto-reconnect with back-off (0.5, 1, 2, 4, max 10 s), re-authenticate, re-subscribe with `fromSeq` (falls back to a full snapshot when the ring buffer has moved on) |
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
  GameData/SkizzyProtocol/PluginData/ auth.token, permissions.cfg, settings.cfg, endpoint.json, host.log, audit/*.jsonl (host-written)
  Skizzy/                             user data root (sandboxed write root)
    library/craft/<name>/<version>/   .craft + manifest.json + trials.json + thumbs/
    library/blueprints/*.yaml
    library/doctrine/*.yaml           ROE, envelopes, formations, tactics parameters
    library/tactics/*.yaml            tactic templates
    library/scenarios/*.yaml
    library/logprofiles/*.yaml
    library/roles/*.cfg               SKIZZY_PART_ROLES, hull slot templates
    recordings/<yyyy-mm-dd>/<battle-id>/ *.skzrec, events.jsonl, meta.json, scores.json, bda.log
    logs/<yyyy-mm-dd>/<name>.{csv,jsonl}   log v0 / direct-sink output (12 §1.1)
    screenshots/<yyyy-mm-dd>/*.jpg|png     saved screenshots (13 §1)
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
- `skizzy doctor`: checks token, port, host version, BDArmory hook availability (fork vs stock), Harmony presence, annotation pack versions, PRE range, and reports the idle-cost self-check results from the last session. **[R]** It also runs the mod-interference checks of `20-runtime-lifecycle.md` §9, and `skizzy doctor --fix` repairs what it safely can (`21-user-experience.md` §1).

## 8. Performance budgets (acceptance numbers)

**[R] What "N vessels" means.** KSP cannot keep 1000 vessels *loaded* (physics-active) at playable frame rates: each loaded vessel costs PhysX and part-module time that dwarfs anything Skizzy does. Budgets are therefore stated for two populations: **loaded** vessels (inside PRE range, unpacked; realistic battles are 50–200) and **tracked** records (loaded plus packed plus unloaded vessels and synthetic contacts, up to 1000). The 1000 figure is tested with the synthetic mode `host.selfcheck.perf --synthetic 1000`, which injects 1000 fake track sources into the real in-game TPE, recorder and serialisers next to the real scene (`14-testing-batch-sim.md` §3).

| Scenario | Budget (main thread unless stated) |
|---|---|
| Idle (no client, no profile, no lease) | < 0.1 ms/frame, 0 B/frame managed alloc, dispatcher disabled |
| One map client + one Opus agent, 100 loaded vessels | TPE amortised ≤ 0.1 ms per physics step; total host ≤ 1.5 ms/frame |
| 20-drone swarm script at 20 Hz setpoints + 10 Hz track deltas | ≤ 0.6 ms/frame; network RTT ≤ 5 ms p99 loopback |
| 200 loaded vessels, 4 teams, fog-of-war, 2 agents, recorder 5 Hz, map | TPE amortised ≤ 0.15 ms per step, worst step ≤ 0.6 ms; recorder ≤ 0.4 ms per sample; total ≤ 3 ms/frame |
| 1000 tracked records (synthetic), god view, recorder 2 Hz | TPE amortised ≤ 0.25 ms per step, worst step ≤ 1.0 ms (rate may degrade to 2 Hz); total ≤ 4 ms/frame |
| Screenshot 1024 px JPEG | ≤ 3 ms main thread with `AsyncGPUReadback` (≤ 30 ms with the `ReadPixels` fallback); ≤ 1 per 2 s per session by default |
| Editor batch apply of 50 part ops | one reload; ≤ 1.5 s on a 300-part craft (pass 1.0 said 150 ms, which is not credible for a KSP editor reload; the M5 spike measures it and sets the final number) |
| Steady-state allocation, whole host, all threads | hot paths 0 B per tick (CI assert); whole host ≤ 64 KB/s in a standard battle (200 loaded vessels, map, 2 agents, recorder) |

### 8.1 How it is measured [R]

| Quantity | In-game method | CI method |
|---|---|---|
| Engine time | `Stopwatch.GetTimestamp()` around each tick, p50/p99/max over a 10 s window, in `host.stats` | same code on `Skizzy.Sim` with BenchmarkDotNet for regressions |
| Allocation | heap slope: `Profiler.GetMonoUsedSizeLong()` and `Profiler.GetTotalAllocatedMemoryLong()` sampled at 1 Hz, `GC.CollectionCount(0)` per minute; a run with Skizzy idle gives the baseline | `GC.GetAllocatedBytesForCurrentThread()` around each `skizzy:hot` method on .NET 10, asserted == 0 after warm-up, for main-thread engines and for the reader/writer serialisation paths |
| Frame cost | `host.stats` frame time p50/p99 with and without Skizzy active, same scene, same camera | — |
| Bandwidth | per-session bytes in/out counters | conformance tests count bytes per scenario |
| Memory growth | `GetMonoUsedSizeLong` after forced collection at 10-minute marks; Unity object counts (`Resources.FindObjectsOfTypeAll` for `Texture2D`, `RenderTexture`, `Material`, `GameObject`) at the same marks | soak on `Skizzy.Sim` for 24 simulated hours |

**Allocation gate (in-game):** over 10 minutes of a standard battle the managed heap slope with Skizzy active must be ≤ baseline + 16 KB/s, and Gen-0 collections per minute ≤ baseline + 1. **Memory gate:** after a forced collection, heap growth between the 10-minute and 30-minute marks ≤ 4 MB with no Unity object count increasing monotonically (the 2-hour and 4-hour soaks use a slope < 1 MB/h, `14-testing-batch-sim.md` §1).

### 8.2 Bandwidth (loopback, msgpack unless stated)

| Client | Typical | Basis |
|---|---|---|
| Map, 200 tracks, 5 Hz deltas | 20–60 KB/s | ~45 B per changed track, deltas only beyond thresholds; JSON would be about 5× |
| Swarm script, 20 drones | ~40 KB/s | 20 Hz × 20 setpoints × ~40 B up, 10 Hz × 50 tracks × ~45 B down |
| LLM agent (compact text) | ~600 characters per top-10 picture, ~150–200 tokens | `tac.summary` + top-10 lines (`03-protocol.md` §6, `08-tactical-picture.md` §7) |
| Recorder (disk) | ~90 KB/s at 200 vessels × 5 Hz, ~45 KB/s after lz4 | ~90 B per vessel sample (pose, velocity, attitude, controls, health, ammo summary) |

### 8.3 Gates per milestone

Each milestone's acceptance list (`17-milestones.md`) includes `host.selfcheck.perf` with the budgets above that apply to the features it ships: M0 idle, screenshot and log v0; M1 holds and arbiter; M2 TPE (100/200 loaded and synthetic 1000), recorder, allocation gate and the 30-minute memory gate; M3 map bandwidth; M4 swarm; M7 batch throughput; M9 the 4-hour soak. A failed gate blocks the milestone.
