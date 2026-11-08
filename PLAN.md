# skizzy-protocol: AI control for Kerbal Space Program 1.12.5

**Implementation plan (no code yet).** BDArmory gets first-class support, and the plan defines an open standard any KSP mod can implement to become AI-controllable.

| | |
|---|---|
| Status | Draft v0.1, 2026-11-07 |
| Owner | Skizzy-create |
| Companion document | [`PROTOCOL-SPEC-DRAFT.md`](PROTOCOL-SPEC-DRAFT.md) (wire format and contract API) |
| Target | KSP 1.12.5 (build 03190, Epic), BDArmory Plus 1.13.1.x, Windows 11, .NET Framework 4.8 / Mono (Unity 2019.4) |

---

## 0. Decisions at a glance

| Topic | Decision |
|---|---|
| Architecture | **Option (b)** with a narrow hybrid escape hatch: our own in-game host plugin, plus an external MCP bridge. kRPC is an *optional, later* extra backend for stock flight control. It is not the foundation. (§3) |
| Wire protocol | JSON-RPC 2.0 over WebSocket on `127.0.0.1:47549`, bearer token, Origin and Host checks. Method names are brand-free, so the protocol name is cosmetic. (§5, spec) |
| Threading | Network I/O and JSON run on background threads. A main-thread dispatcher drains a queue under a per-frame time budget: in `Update`, in `FixedUpdate`, and in `Update` while paused. Telemetry is served from snapshots taken each `FixedUpdate`. Long operations return operation handles. (§3.4) |
| Universal access | **Tier 0:** reflection over every `PartModule`/`VesselModule` (`BaseFields`, `BaseEvents`, `BaseActions`, `UI_Control` ranges, resources, action groups, part trees, MM config). **Tier 1:** config annotations that can be patched with ModuleManager. **Unsafe tier:** deep reflection, off by default. (§4) |
| Opt-in protocol | **Tier 2:** duck-typed attributes copied as source, with *zero* binary dependency. **Tier 3:** a contract DLL, or a JSON-delegate ABI found through reflection. (§5) |
| Permissions | Levels `read`, `control`, `destructive`, `cheat` and `unsafe`, set per mod and per capability as allow, ask or deny. Default is deny except stock `read`. Enforced *in the host*, so it also applies to clients that bypass MCP. In-game UI, a ConfigNode file, a JSONL audit log and a kill switch. (§6) |
| BDArmory | Phase 1 is a separate shim mod, `Skizzy.BDArmory.dll`, about 30 tools. Phase 2 is an upstream PR that adds Tier-2 attributes to BDArmory with no new dependency, plus a handful of small public-API additions. The shim steps aside when it detects the in-tree provider. (§7) |
| MCP bridge | TypeScript on Node 22 LTS using the official MCP TypeScript SDK v2 (`@modelcontextprotocol/server`), targeting MCP **2026-07-28** with fallback to 2025-11-25. stdio by default, optional Streamable HTTP. The same package ships the `skizzy battle` test-runner CLI. (§5.6, §9) |
| Name | Default and working name: **skizzy-protocol**. Recommended public name: **Kerbal Uplink** ("Uplink-ready" mods). (§2) |
| MVP | Read state, spawn fleets, set teams and guard mode, start and stop a competition, read scores and logs, all through Claude Code with permissions and audit working. (§10, M1) |

---

## 1. Goals, non-goals and environment

### 1.1 Goals
1. Let any MCP client (Claude Code, Claude Desktop, other agents), and any program that speaks WebSocket and JSON, observe and control KSP.
2. First-class BDArmory support:
   - spawning and loading craft
   - teams
   - weapon managers, including the new multi-target and parallel-fire settings
   - AI pilots, surface, submarine, VTOL and orbital
   - competitions and tournaments
   - firing weapons
   - radar, targets, missiles, damage, scores and logs
3. Every installed mod becomes reachable with no work from its author (Tier 0). Mods that opt in get rich, safe, well-described tools (Tiers 1–3).
4. The user decides which mods and which kinds of action an AI may touch, and every action is audited.
5. Unattended, repeatable test battles validate the user's BDArmory multi-target and parallel-fire changes and the patched naval mods (HullBreach, MNWS, NAS).

### 1.2 Non-goals for v1
- Remote or LAN access. Loopback only; use an SSH tunnel if needed.
- Replacing kOS or MechJeb autopilots. The AI commands BDArmory's AI and stock controls; it does not hand-fly at 50 Hz.
- Career or science automation.
- Rendering or screenshots (perhaps in M6).
- Deterministic physics replay. KSP and Unity physics are not deterministic, so testing is statistical (§8.5).

### 1.3 Environment facts this plan relies on (checked 2026-11-07)

| Fact | Where |
|---|---|
| KSP 1.12.5, build 03190 (2022-12-12), Epic distribution | `D:\Epic Games\KerbalSpaceProgram\English\buildID64.txt` |
| BDArmory **1.13.1.0**, `KSPAssembly("BDArmory",1,0)`, licence CC-BY-SA 2.0 | `Properties/AssemblyInfo.cs:20-31`, `Distribution/GameData/BDArmory/License.txt` |
| BDArmory source is on branch `multitarget-1.13.1`. HEAD `be734f0bb` is "Multi-target missile engagement: 1024 targets / 128 missiles per target…". It has uncommitted `.csproj` edits from another agent and is **read-only for us**. | `git log` (read-only) |
| `MissileFire.maxAllowableTargets = 1024`, `maxAllowableMissilesOnTarget = 128` | `Control/MissileFire.cs:680, 690` |
| The `KSP_x64_Data\Managed` folder has **no** `Newtonsoft.Json`, `System.Net.Http`, `System.Runtime.Serialization` or `netstandard.dll`. It does have `mscorlib`, `System`, `System.Core`, `Mono.Security` and `UnityEngine.*`. | directory listing |
| Installed and relevant: `000_Harmony`, `001_ToolbarControl`, `000_ClickThroughBlocker`, `ModuleManager.4.2.3`, `PhysicsRangeExtender`, `VesselMover`, `KerbalEngineer`, `Trajectories`, `AtmosphereAutopilot`, `KSPCommunityFixes`, `BDArmoryExtended`, `BDArmoryRefurbished`, `StockArmory`, `NKDR`, `KerbalField`, `KerbalPowers` | `GameData/` |
| **Not installed:** kRPC, kOS, MechJeb, HullBreach, MNWS, NAS | `GameData/` |
| Patched naval mods exist but are not installed: `_downloads\patched\{HullBreach, MNWS, NAS, IronSeasKANES, LargeBoatPartsPack, MaritimePack, AircraftCarrierAccessories}`. HullBreach has full source (SDK-style net48 `.csproj`). NAS has `AntiSubmarineWeapon.dll` with source. MNWS is config only. | `_downloads/patched/*` |
| BDArmory spawns from `<KSP>\AutoSpawn\<folder>\`; each subfolder is a team. `AutoSpawn\` is currently empty. | `VesselSpawning/VesselSpawnerBase.cs:52-53`, `CircularSpawning.cs:132-164` |
| BDArmory writes results to `GameData\BDArmory\Logs\<CompetitionID>[-tag].log`; tournaments write to `Logs\Tournament <id>\Round <n>\`. | `Competition/Scoring.cs:934-945`, `BDATournament.cs:2286` |
| BDArmory keeps its settings in `GameData\BDArmory\PluginData\settings.cfg` so that saving them does not invalidate the MM cache. We copy this pattern. | `Settings/BDArmorySettings.cs:10-11` |

---

## 2. Naming

The working and default name stays **skizzy-protocol** (wire identifier `"skizzy-protocol"`, code namespace `Skizzy.*`). Method names on the wire are brand-free (`catalog.get`, `cap.call`, `ksp.vessels.list`, `bda.start_competition`). A rename therefore changes only the handshake identifier, which the host accepts as an alias, plus package and display names.

| Candidate | Rationale | Concerns |
|---|---|---|
| **Kerbal Uplink** (short form *Uplink*, id `uplink`) | Spacecraft language: commands go *up* and telemetry comes *down* ("downlink streams"). "Uplink-ready" works as a badge on a SpaceDock page. Self-explanatory to KSP players. | "Uplink" alone is generic (there is an old hacking game of that name). Always pair it with "Kerbal" in titles. |
| **Krakenlink** | Unique, very searchable, and fits KSP culture (the Kraken). | The Kraken means physics bugs and craft exploding, which is a poor association for a safety-focused control protocol. |
| **KOCP: Kerbal Open Control Protocol** | Sounds like a standard and says exactly what it is. | Dull, and the acronym is hard to say. |
| **Jeb (Joint Extensible Bridge)** | Playful, and Jeb is the iconic Kerbal. | A forced backronym. "Jeb-compatible" reads oddly. |
| **Mission Control Link (MCL)** | Matches the "AI as mission control" mental model. | Generic, and easily confused with the stock "Mission Control" building and with "MCP". |

Rejected: **CAPCOM** (Capcom Co. is a video-game trademark) and **CommNet** (name of a stock KSP system).

**Recommendation: Kerbal Uplink** for the public standard once v1.0 ships. Keep `skizzy-protocol` as the default identifier until the user decides. The cost of deciding later is close to zero.

---

## 3. Architecture

### 3.1 Options compared

| Criterion | (a) kRPC 0.7.0 plus a custom BDArmory kRPC service | (b) Own host plugin plus external MCP bridge | (c1) Hybrid: our host *and* kRPC side by side | (c2) Hybrid: our protocol carried over kRPC transport |
|---|---|---|---|---|
| Dynamic, per-craft catalog (Tier 0 reflection) | ✗. kRPC services are compile-time static classes (`KRPCService`/`KRPCClass`/`KRPCProcedure`) with protobuf types. There is no runtime tool list and no JSON Schema. | ✓, built for this | ✓ (our side) | ~. Possible only by tunnelling JSON strings through one generic procedure, which defeats the point of kRPC. |
| Third-party mods integrate with no hard dependency | ✗. Implementing a service means referencing `KRPC.Core` at compile time. | ✓ (Tier 2 duck typing, Tier 3 reflection ABI) | ✓ | ✗ |
| Permissions and auth | ✗. kRPC has no auth or permission model, only an auto-accept prompt and loopback binding. | ✓, designed in (§6) | ~. kRPC remains an unguarded side door. | ~ |
| LLM-oriented metadata (descriptions, safety, units) | ✗. Only doc comments. | ✓ | ✓ | ~ |
| Protocol stability | ✗. 0.7.0 (2026-10-21) added a version handshake that breaks older clients. 0.8 (protocol v2, merged to `main` on 2026-10-30) breaks it again. | We own the protocol | Mixed | ✗ |
| Licence impact | LGPLv3 core; **GPLv3 SpaceCenter**. A service built on SpaceCenter types inherits GPL questions. | Our choice (MIT) | Separate processes, so clean | GPL questions |
| BDArmory upstream acceptance | Unlikely. Maintainers would not take a kRPC dependency. | Plausible (Tier-2 attributes, no dependency) | Plausible | Unlikely |
| Free stock flight control (orbits, autopilot, nodes) | ✓, mature | ✗ (build what we need) | ✓ | ✓ |
| Threading and performance model | Proven: RPCs run in `FixedUpdate` with an adaptive budget (`MaxTimePerUpdate` 5 ms) | We copy the proven pattern | Two plugins competing for frame time | — |
| Effort to MVP | Medium | Medium | High | High |

Two further variants were considered:
- **MCP server *inside* KSP** (lhervier/KSP-Util-MCPServer, ClaudePilot). Rejected as the primary design for three reasons:
  - Official MCP SDKs do not run on KSP's Mono/.NET 4.8 without pulling in `System.Text.Json` and `Microsoft.Extensions.*`, which causes DLL conflicts in a shared AppDomain.
  - The MCP spec just changed fundamentally (2026-07-28 is stateless, adds `server/discover`, and replaces subscriptions with `subscriptions/listen`). Tracking those changes inside a game plugin is painful.
  - stdio clients need a separate process anyway.
- **Telemachus or kOS-telnet style**: stringly typed and coarse, with no auth. Rejected.

### 3.2 Decision: (b), keeping (c1) open as a later, optional backend

**Why:**
1. The two hardest requirements, *every mod reachable through reflection with zero author work* and *a standard any mod can implement with no hard dependency*, are fundamentally dynamic. kRPC's static, compile-time service model cannot express them without degenerating into "one procedure that takes a JSON string", and at that point kRPC adds nothing.
2. Permissions must be enforced at the single choke point inside the game. kRPC would be a second, unguarded door. If the user ever installs kRPC for other reasons, our UI states clearly that kRPC traffic is outside Skizzy's permission model.
3. We control versioning. Neither kRPC's 0.7 to 0.8 break nor MCP's 2026 break can strand the in-game plugin, because MCP churn is absorbed in the TypeScript bridge, which updates with `npm i`.
4. BDArmory upstream is far more likely to accept "copy one attribute file, no dependency" than "depend on kRPC".

**What we take from kRPC:**
- its threading and budget design (§3.4)
- its stream semantics: values sampled once per `FixedUpdate`, only changes sent
- its yield/continuation idea for long procedures (our operation handles)
- its 0.7 *tick-hold* concept, for an optional lockstep mode in M6
- its handle-based object references

**(c1) later:** in M7 the bridge *may* add a kRPC backend that exposes a curated set of SpaceCenter flight tools (autopilot, maneuver nodes) when kRPC is installed and the user enables it. It stays bridge-side, so it never touches the host.

### 3.3 Component diagram

```
 ┌──────────────── MCP clients ────────────────┐      ┌────── direct clients ──────┐
 │ Claude Code / Claude Desktop / any MCP host │      │ Python notebook, CI script,│
 └──────────────┬──────────────────────────────┘      │ another agent, a web dash  │
                │ MCP 2026-07-28 (stdio | Streamable   └─────────────┬──────────────┘
                │ HTTP on 127.0.0.1)                                 │
 ┌──────────────▼──────────────────────────────┐                     │
 │ skizzy-mcp  (Node 22, TypeScript)           │                     │
 │  • catalog → MCP tools/resources projection │                     │
 │  • toolsets, name mapping, MRTR/elicitation │                     │
 │  • op waiting, progress, Tasks ext.         │                     │
 │  • `skizzy battle` runner + offline log rdr │                     │
 └──────────────┬──────────────────────────────┘                     │
                │ skizzy-protocol: JSON-RPC 2.0 / WebSocket          │
                │ ws://127.0.0.1:47549/skizzy  + Bearer token        │
 ═══════════════╪════════════════ KSP process boundary ══════════════╪═══════════
 ┌──────────────▼────────────────────────────────────────────────────▼──────────┐
 │ Skizzy.Host.dll  (KSPAddon Instantly, DontDestroyOnLoad)                     │
 │  Net threads: accept/read/write, JSON parse, auth, schema validation         │
 │  Permission engine ── Audit log (JSONL) ── In-game UI (ToolbarControl)       │
 │  Main-thread Dispatcher (Update / FixedUpdate / paused) ── Snapshot publisher│
 │  Catalog ◄── Tier0 Reflector  ◄── Tier1 Annotations (SKIZZY_ANNOTATIONS cfg) │
 │          ◄── Tier2 Duck-typed attribute scanner                              │
 │          ◄── Tier3 Provider registry (Contract DLL | JSON-delegate ABI)      │
 │  Built-in providers: ksp.* (stock), host.*, logs.*                           │
 └──────┬──────────────────────────────┬──────────────────────────┬─────────────┘
        │                              │                          │
 ┌──────▼──────────────┐   ┌───────────▼───────────┐   ┌──────────▼──────────────┐
 │ Skizzy.BDArmory.dll │   │ HullBreach (Tier 2,   │   │ Any other mod: Tier 0   │
 │ (Phase 1 shim, refs │   │ user-owned patched    │   │ automatically; Tier 1   │
 │ BDArmory.dll)  ──►  │   │ source)               │   │ via MM-patchable cfg    │
 │ Phase 2: in-tree    │   └───────────────────────┘   └─────────────────────────┘
 │ BDArmory provider   │
 └─────────────────────┘
```

### 3.4 Threading model

KSP's API is not thread-safe, and almost every Unity call must happen on the main thread. Our rule: **no game object is touched off the main thread, ever.**

| Thread | Responsibilities |
|---|---|
| **Acceptor thread** | `TcpListener` on `127.0.0.1:47549`. Performs the WebSocket upgrade, checks the Origin, Host and token, and enforces the connection limit (default 4). |
| **Per-connection reader and writer threads** (or async I/O on the thread pool) | Frame decoding, UTF-8, JSON parsing with depth and size limits, JSON-RPC envelope validation, argument validation against the capability's JSON Schema (pure CPU work), and a **pre-check** of permission policy against the immutable catalog snapshot. Requests that are denied never reach the main thread. Accepted requests become a `Job { capability, args, affinity, deadline, cancelToken, session }` on a lock-free `ConcurrentQueue` (in .NET 4.x `mscorlib`). |
| **Main thread: `SkizzyDispatcher` MonoBehaviour** | `[KSPAddon(Instantly, once:true)]`, `DontDestroyOnLoad`, early execution order. Drains the queues within a time budget measured by `Stopwatch`. |

**Affinity classes**, declared per capability:

| Affinity | Drained in | Use for |
|---|---|---|
| `main` (default) | `Update()` | Most control and reads: scene ops, UI-affecting calls, BDArmory calls that start coroutines |
| `fixed` | `FixedUpdate()` | Anything that must line up with physics: flight control inputs, reads that must be consistent within one physics step |
| `snapshot` | *Network thread*, from the last published snapshot | High-rate telemetry reads (vessel kinematics, scores, missile list). Zero main-thread cost per request. |
| `any` | Network thread | Pure host functions: `catalog.get`, `permissions.get`, `host.stats`, schema lookups |

**Budgets** (adaptive, as in kRPC):
- The request budget defaults to 2 ms per frame and adapts between 0.5 and 8 ms.
  - It shrinks when frame time exceeds the target, measured with `Time.unscaledDeltaTime` against a 16.7 ms goal.
  - It grows while the queue is backing up.
- The stream-sampling budget defaults to 1 ms per `FixedUpdate`.
- A job that overruns its slice is not pre-empted (that is impossible in-process). Instead, capabilities are written to do bounded work and to move heavy work into an *operation* (below). The host records the worst offenders in `host.stats` so we can find them.

**Backpressure:**
- The queue cap is 512 jobs (configurable). Beyond that, calls fail immediately with `BUSY {retryAfterMs}`.
- Each session may have 32 calls in flight.
- Snapshot reads are never queued.

**Snapshots:**
- At the end of each `FixedUpdate`, a `SnapshotPublisher` builds immutable records for the sources that live streams have subscribed to: vessel kinematics, BDArmory scores and missiles, subscribed fields.
- It builds them only for subscribed sources and within the stream budget, then swaps them in atomically with `Interlocked.Exchange`.
- Network threads serialize these snapshots and deltas without blocking the game.
- Snapshots use pooled buffers and avoid LINQ and string concatenation in the hot path. BDArmory's own `Notes.md` lists these as GC hazards.

**Long-running work (operations):**
- Spawning, competitions, scene loads and waiting for conditions are coroutines or multi-frame processes.
- A capability marked `longRunning` returns `{status:"accepted", op:"op:N"}` straight away.
- The host then:
  - runs the provider's coroutine, or a declarative poller such as "complete when `VesselSpawnerStatus.vesselsSpawning == false`"
  - emits `op.progress` events
  - finishes with `op.done`
- Clients can call `op.get`, `op.cancel` or `op.wait`. `op.wait` blocks a *network* thread, never the game.
- Operations survive a client disconnect, and the bridge can re-attach to them.
- Operation timeouts are measured in **game time (UT)**, with a wall-clock safety cap.

**Outbound events** are batched in `LateUpdate` and handed to writer threads, at most one batch per frame per session.

**Exceptions** from mod code are caught on the main thread and returned as `PROVIDER_ERROR` with a sanitized message. They are also logged once per (capability, exception type) per minute, so a failing provider cannot spam `KSP.log`.

### 3.5 Time, pause, warp and scenes

| Situation | Behaviour |
|---|---|
| **Normal flight** | `FixedUpdate` runs at the physics rate: KSP's default `Time.fixedDeltaTime` is 0.02 s (50 Hz), with possibly several steps per frame. `fixed` jobs are drained once per `FixedUpdate` call, so up to N steps per frame. |
| **Paused** (`FlightDriver.Pause`, Esc menu, `Time.timeScale = 0`) | `FixedUpdate` stops. The dispatcher drains *all* queues from `Update`, as kRPC does when paused. `fixed`-affinity capabilities that need physics fail with `PRECONDITION_FAILED {reason:"paused"}` unless they declare `allowWhilePaused`. Streams send one `paused` state frame and then nothing until physics resumes. |
| **Physics warp** (up to 4×) | Allowed. Every response carries `warp: {mode:"physics", rate}`. Telemetry is stamped with UT, so rates stay correct. |
| **On-rails warp** (vessels packed) | BDArmory AI, weapons and physics do not run. Capabilities marked `requires: ["unpacked"]` fail with `PRECONDITION_FAILED {reason:"vessel_packed"}`. `ksp.time.set_warp` is `control` level. The battle harness refuses to warp on rails while a competition is active. |
| **BDArmory time scale** (`BDArmorySettings.TIME_OVERRIDE`/`TIME_SCALE`, up to `TIME_SCALE_MAX` = 10; `BDArmorySettings.cs:483-485`) | Changes `Time.timeScale` without changing the physics step, so it speeds up sims. All our timeouts and stream periods use UT, so they scale correctly. The harness uses this to run battles faster than real time when the CPU allows. |
| **Scene change requested** (`GameEvents.onGameSceneLoadRequested`) | Catalog state becomes `transitioning`. New control calls fail with `UNAVAILABLE_IN_SCENE {reason:"transition"}`. Running operations that are bound to the scene are cancelled with reason `scene_change`. All vessel, part and module handles are invalidated. |
| **Scene ready** (`onFlightReady`, `onLevelWasLoadedGUIReady`) | The catalog is rebuilt incrementally and `catalog.changed` is emitted. Note that `BDACompetitionMode` is `[KSPAddon(Startup.Flight)]` (`BDACompetitionMode.cs:25`), so `Instance` is null outside Flight. BDArmory capabilities declare `scenes:["FLIGHT"]`. |
| **Game quit or crash** | The bridge keeps working in degraded mode: it can still read `KSP.log` and BDArmory log files from disk (read-only, bridge-side, §6.8). |

The tool list stays **stable across scenes**. A capability that is unavailable in the current scene stays listed and returns `UNAVAILABLE_IN_SCENE`. This avoids churning `tools/list_changed` notifications, which is bad for LLM prompt caching. The MCP 2026-07-28 spec also asks for deterministic `tools/list` ordering.

### 3.6 Identity and handles

| Object | Handle | Source | Notes |
|---|---|---|---|
| Vessel | `v:<persistentId>` | `Vessel.persistentId` (uint, saved in the game file) | KSP reassigns it on collision when duplicates spawn, so the host re-indexes on `onVesselCreate`. `vessel.id` (GUID) is kept in metadata. |
| Part | `p:<persistentId>` | `Part.persistentId` | Stable within a session and across save and load. |
| Module | `m:<partPid>:<index>:<ModuleName>` | index in `part.Modules` plus the class name | The class name guards against index drift, for example when B9PartSwitch changes modules. A mismatch returns `NOT_FOUND {reason:"stale"}`. |
| Operation, subscription, confirmation | `op:N`, `sub:N`, `cf:N` | host counters | Scoped to the host process. |
| BDArmory competitor | vessel name | BDArmory keys scores by **vessel name** (`Scoring.cs:26`) and deconflicts names when spawning (`SpawnUtils.DeconflictVesselName`, `SpawnUtils.cs:321`) | The adapter maps names to `v:` handles both ways. Tools accept a handle or an exact name. If a name is ambiguous, the call fails with `INVALID_PARAMS` and lists the candidates. |

Positions are never exposed as raw Unity world coordinates, because KSP's floating origin and Krakensbane make them meaningless across frames. They are exposed as:
- body-relative latitude, longitude and altitude, plus
- an optional vector **relative to a named reference vessel**, in a north/east/up frame.

Velocities are given as surface and orbital speed, heading and pitch.

---

## 4. Universal access layer (Tier 0 automatic, Tier 1 annotated, deep reflection)

### 4.1 What is discovered

| Source | How | Exposed as |
|---|---|---|
| Loaded mods | `AssemblyLoader.loadedAssemblies` (name, version, `url` under GameData, `KSPAssembly` attribute) and the top-level GameData folders (config-only mods such as MNWS's `Johnny/`) | `ksp.mods.list`, and the `ksp://mods` resource |
| Module types | All non-abstract subclasses of `PartModule`, `VesselModule` and `ScenarioModule` in each assembly | `ksp.module.describe(type)`, which is static per type and cached |
| Fields | `PartModule.Fields` (`BaseFieldList`), with `[KSPField]` metadata: `guiName`, `guiUnits`, `guiFormat`, `isPersistant`, `guiActive`, `guiActiveEditor`, `advancedTweakable`, `groupName`, and `uiControlFlight`/`uiControlEditor` | typed members with a JSON Schema |
| Events | `PartModule.Events` (`BaseEventList`): `guiName`, `active`, `guiActive`, `guiActiveEditor`, `guiActiveUnfocused`, `unfocusedRange`, `externalToEVAOnly` | invocable members |
| Actions | `PartModule.Actions` (`BaseActionList`): `guiName`, `actionGroup`, `defaultActionGroup`, `active` | invocable members, with Activate, Deactivate or Toggle |
| Action groups | `vessel.ActionGroups` (Stage, Gear, Light, RCS, SAS, Brakes, Abort, Custom01–10), and which `BaseAction`s are bound to each | `ksp.vessel.action_group` |
| Resources | `part.Resources` (`PartResource`: `resourceName`, `amount`, `maxAmount`, `flowState`, `isTweakable`) | read is `read`; `flowState` is `control`; `amount` is `cheat` |
| Vessel and part tree | `vessel.parts`, `rootPart`, `parent`, `children`, `inverseStage`, attach nodes, crew, temperature, local CoM | `ksp.vessel.get(depth)` |
| VesselModule fields | `VesselModule.Fields` where present (verify per type in the M0 spike) | same as part fields |
| ScenarioModules | `ScenarioRunner.GetLoadedModules()`, their `[KSPField]`s | members, mostly read |
| ModuleManager config (post-patch) | `GameDatabase.Instance.GetConfigs(type)` (with `UrlConfig.url`), `PartLoader.LoadedPartsList[*].partConfig` | read-only resources `ksp://config/{type}/{name}`, size-capped. These are *config* values; runtime values can differ. |

Every module type gets an immutable **type descriptor** built once, the first time it is seen. Per-instance reads use **compiled accessors**: expression-tree delegates built from each `BaseField`'s `FieldInfo`, the same technique as BDArmory's `ModIntegration/ReflectionUtils.cs`. This avoids `FieldInfo.GetValue` boxing in hot paths.

### 4.2 Attributing members to mods
- **Behaviour belongs to the assembly that defines the module type.** For example, `ModuleWeapon` is BDArmory's, `ModuleHullBreach` is HullBreach's, and `ModuleEngines` is Squad's.
- **Parts belong to the GameData folder their config came from** (`AvailablePart.partUrl`). For example, an MNWS gun part belongs to `Johnny`.
- **Permission checks use the module's assembly.** Firing an MNWS turret is BDArmory `ModuleWeapon` behaviour, so it needs the BDArmory grant.
- An optional *part-pack filter* in the permission file can also restrict which part packs the AI may act on, for example "BDArmory control allowed, except on parts from pack X".
- The UI shows both attributions.

### 4.3 Schema derivation

| C# type | JSON Schema |
|---|---|
| `bool` | `boolean` |
| `int`, `short`, `long`, `uint` | `integer` (range from the type) |
| `float`, `double` | `number` (NaN and Infinity become `null` plus a `nonFinite` flag) |
| `string` | `string` |
| `enum` | `string` with `enum` names. Flags enums become arrays of names. |
| `Vector2`, `Vector3`, `Vector3d`, `Quaternion`, `Color` | fixed-length `array` of numbers with an `x-skz-kind` |
| `FloatCurve`, `ConfigNode`, other complex types | read-only `object` (serialized key arrays or config text), size-capped |

| UI control | Schema effect |
|---|---|
| `UI_FloatRange(min, max, step)` | `minimum`, `maximum`, `multipleOf` (hint), `x-skz-step` |
| `UI_FloatEdit` | `minimum`/`maximum` plus increment hints |
| `UI_Toggle(enabledText, disabledText)` | `boolean` with `x-skz-labels` |
| `UI_ChooseOption(options, display)` | `enum: options`, with display names in `x-skz-enumLabels`. For example, BDArmory `SurfaceTypeName`: `Stationary`, `Land`, `Water`, `Amphibious`, `Submarine` (`BDModuleSurfaceAI.cs:76-78`). |
| `UI_Cycle`, `UI_ScaleEdit`, `UI_VariantSelector` | `enum` or list as appropriate |
| `UI_Label`, `UI_ProgressBar` | `readOnly: true` |
| *Unknown subclasses* (BDArmory's `UI_FloatSemiLogRange`, `UI_FloatPowerRange`) | Duck-read `minValue`, `maxValue` and `stepIncrement` by reflection if present. Otherwise unconstrained, with `x-skz-uiControl: "<TypeName>"`. |

- The flight and editor controls can differ, so the host emits the schema for the *current* scene and reports the other one in metadata.
- `guiName` values such as `#LOC_BDArmory_WMWindow_MultiTargetNum` are resolved through `Localizer.Format` into `title`. The raw key is kept as `x-skz-locKey`.

### 4.4 Operations (wire methods; full definitions in the spec)

| Method | Purpose | Level |
|---|---|---|
| `ksp.mods.list` | Mods, assemblies, versions, module types, part counts, integration tier, grant state | read |
| `ksp.vessels.list` | Loaded and unloaded vessels: handle, name, type, situation, body, lat/lon/alt, packed, part count | read |
| `ksp.vessel.get {vessel, depth}` | Tree of parts and modules (depth-limited) | read |
| `ksp.parts.find {vessel?, module?, partName?, tag?}` | Find parts by module type, name or tag | read |
| `ksp.module.describe {type}` | Member schemas for a module type | read |
| `ksp.module.get {module, fields[]}` | Read values | read |
| `ksp.module.set {module, values{}, symmetry?: true}` | Write fields with UI controls (§4.5) | control (or as annotated) |
| `ksp.module.invoke {module, event}` | Invoke a `BaseEvent` | control (heuristics or annotation can raise it, §4.6) |
| `ksp.module.action {module, action, type}` | Invoke a `BaseAction` | control (same) |
| `ksp.vessel.action_group {vessel, group, state? \| toggle}` | Stock action groups | control (Abort is destructive) |
| `ksp.vessel.stage {vessel}` | `StageManager.ActivateNextStage()` on the active vessel | destructive |
| `ksp.query {select, fields, limit, cursor}` | **Bulk selector** across vessels, parts and modules (below) | read |
| `stream.subscribe {source: query \| stream, mode, rate}` | Change subscriptions (below) | read |
| `ksp.config.get {type, name}` | Post-MM config node | read |

**Query selector.** This matters for the LLM's token economy and for telemetry efficiency, because one call replaces hundreds:

```json
{ "select": { "vessels": {"loaded": true, "nameLike": "Destroyer*"},
              "modules": {"type": ["ModuleHullBreach"]} },
  "fields": ["hullStatus", "floodedDisplay", "inflowDisplay"],
  "aggregate": "perVessel", "limit": 200 }
```

**Change detection.** KSP has no general field-change events. For each subscription the host polls the selected members in `FixedUpdate` at the subscription's rate (stated in UT seconds or physics steps), compares them with the last values sent (with a float dead-band), and emits deltas. The cost is bounded by the stream budget. If the budget is exceeded, the host lowers the effective rate and reports `degraded: true` in `stream.data`.

### 4.5 Write semantics (do what the Part Action Window does)

Writing a raw field skips the callbacks mods depend on. For example, `BDModuleSurfaceAI` refreshes which fields are visible when `SurfaceTypeName` changes (`BDModuleSurfaceAI.cs:267-299`). For each `ksp.module.set` the host therefore:

1. Validates the value against the schema (range, step, enum) and rejects it with `INVALID_PARAMS` and the allowed range.
2. Calls `BaseField.SetValue(value, host)`.
3. Invokes the scene's `UI_Control.onFieldChanged(field, oldValue)`. If `affectSymCounterparts` is set (and the caller asked for `symmetry`), it also applies the value to `part.symmetryCounterparts` and fires `onSymmetryFieldChanged`.
4. Fires the `BaseField` value-modified hook that KSP 1.12 uses for PAW edits. *Exact API to be confirmed in the M0 spike* (risk R3).
5. Reads the value back and returns `{old, new, applied: true|false}`. Some mods clamp or reject values in their callbacks.

Fields *without* a writable UI control are read-only through Tier 0. Writing them needs a Tier 1 annotation that marks them writable, or deep reflection (`unsafe`).

### 4.6 Classifying unannotated members by safety

| Member | Default level |
|---|---|
| Field read, event and action metadata | `read` |
| Write to a field that has a scene-appropriate, enabled UI control | `control` |
| Event or action invocation | `control`, raised to `destructive` if the name or `guiName` matches a keyword list: explode, detonate, destruct, jettison, decouple, separate, fire, launch, abort, delete, remove, kill, cut, arm, eject, salvo, scuttle |
| Write to a non-UI field (deep write) | `unsafe` |
| Static members, private members, arbitrary methods | `unsafe` (deep reflection only) |

These heuristics are deliberately conservative. Tier 1 and Tier 2 metadata override them.

### 4.7 Tier 1: configuration annotations (any mod, no author work, MM-patchable)

A `SKIZZY_ANNOTATIONS` ConfigNode (format in spec §11.4) can be shipped by the host for popular mods, by a mod author, or by the user, and patched with ModuleManager. It can:
- add descriptions, units, safety levels and aliases to Tier 0 members
- hide members (`expose = false`)
- mark extra fields writable, at a stated level
- **expose C# properties** (read-only) on modules and vessel modules. Example: HullBreach's `HullBreachVesselModule.WaterTonnes`, `CapacityTonnes`, `InflowTonnesPerSec` and `Foundered` are C# properties, not KSPFields (`HullBreachVesselModule.cs:32-40`), so Tier 0 cannot see them.
- **expose static members by attribute or by name**. For example, every `[BDAPersistentSettingsField]` static in `BDArmorySettings` (409 of them) can be exposed as a settings capability, mirroring what BDArmory's own `BDAPersistentSettingsField.Save` does (`Settings/BDAPersistentSettingsField.cs`), with per-pattern overrides such as `INFINITE_*` set to `cheat`.
- declare **composite tools** that invoke a named public method with parameters: `METHOD { type=…; name=…; PARAM {…} }`. This is still reflection, but declared by a human, so it is deliberate.

Annotations are versioned with `modVersion = 1.13.*` and are ignored, with a warning, when the installed version does not match.

### 4.8 Deep reflection (the "any and all parts of every mod" escape hatch)

`reflect.get`, `reflect.set` and `reflect.call` act on an arbitrary type and member path, including statics, privates and singletons. Examples:
- `BDArmory.Control.MissileFire` instance → `missileSaturatedTargets`
- `BDArmory.Radar.VesselRadarData` → `displayedTargets`

lhervier's `get_member`, `set_member` and `call_method` work the same way.

Gating:
- **Off by default.** It can only be enabled per mod, by editing the config file or in the in-game UI. There is no network method that enables it.
- `reflect.get` needs the per-mod flag `deepRead = allow`.
- `set` and `call` need level `unsafe`, plus an in-game confirmation every session.
- Results are depth-limited and size-limited, and cycles are broken.
- Every call is audited with the full member path.

This is meant for the user's own debugging, such as inspecting the multi-target patch's private state. It is not for routine AI use.

### 4.9 Limits of reflection-only access (why Tiers 1–3 exist)

1. **No semantics.** A field name like `fireBurstLength` carries no intent, units or safety meaning, and an LLM will misuse terse names.
2. **Hidden state.** Private fields and C# properties are invisible. Examples: `VesselRadarData.displayedTargets` is private (`VesselRadarData.cs:141`); `MissileFire.FireCurrentMissile` is private (`MissileFire.cs:4570`); HullBreach's flood totals are properties.
3. **Most real APIs are not on parts.** `BDACompetitionMode.Instance`, `BDATargetManager` and the vessel spawners are `KSPAddon`/`MonoBehaviour` singletons or statics.
4. **KSP events take no parameters,** but real operations do. Compare `StartCompetitionMode(distance, startDespiteFailures, tag, compType)`.
5. **No completion signal.** An event that starts a coroutine, such as spawning, returns immediately, with no progress or result.
6. **Logic tied to the GUI.** Many features (BDArmory's Vessel Spawner window, KER readouts) live in `OnGUI` code paths.
7. **Callbacks bypassed.** Raw writes can desynchronise mod state (§4.5).
8. **Coordinate frames.** Vector fields are in floating-origin world space.
9. **Localization.** `#LOC` keys.
10. **No stability contract.** Field names change between mod versions.
11. **Cost.** Reflecting over hundreds of parts at high rates creates garbage. BDArmory's notes warn about GC.
12. **Safety.** Invoking arbitrary events or methods can destroy craft or corrupt state, and there is no undo.

---

## 5. The protocol (Tiers 2–3: opt-in, richer semantics)

Full normative text is in [`PROTOCOL-SPEC-DRAFT.md`](PROTOCOL-SPEC-DRAFT.md). This section summarises the design.

### 5.1 Integration tiers

| Tier | Mod author effort | Dependency | Gives |
|---|---|---|---|
| 0: Reflection | none | none | Fields, events, actions, resources, trees |
| 1: Annotations | none (anyone can write a `.cfg`) | none | Descriptions, units, safety, hidden or extra members, statics, properties, declared methods |
| 2: Duck-typed attributes | Copy one CC0 file (`SkizzyAttributes.cs`) into the mod's own namespace and tag methods | **none**: the host matches attributes *by type name* | Named tools with typed parameters, schemas from C# signatures, long operations through `IEnumerator`, read resources, simple events |
| 3a: Contract DLL | Reference `Skizzy.Contract.dll` and declare a CKAN dependency | hard (small, stable ABI) | Fully dynamic catalogs (builders), push streams, events, typed context (progress, cancellation, caller grants), custom JSON Schema |
| 3b: JSON-delegate ABI | Copy `SkizzyLink.cs` (CC0) | **none**: found by reflection at runtime | Everything in 3a, through `string → string` JSON delegates. Version-proof. |

### 5.2 The contract assembly and soft-dependency mechanics

`Skizzy.Contract.dll`:
- targets net48 and references only `mscorlib`, `System`, `Assembly-CSharp` and `UnityEngine.CoreModule` (for `Vessel` and `Part` in typed helpers)
- keeps `AssemblyVersion` fixed at the major version (`1.0.0.0`) and bumps `AssemblyFileVersion`, so binding never breaks on minor updates
- ships **only** inside `GameData/Skizzy/Plugins/`. Mods must not bundle it, because a duplicate assembly name in KSP's shared AppDomain loads first-wins.
- Hard-dependent mods add `[assembly: KSPAssemblyDependency("Skizzy.Contract", 1, 0)]` to get the right load order.

**Duck typing.** The host scans `AssemblyLoader.loadedAssemblies` with `CustomAttributeData`, which avoids instantiating foreign attribute types. It matches attribute classes **by simple name**: `SkizzyProviderAttribute`, `SkizzyToolAttribute`, `SkizzyParamAttribute`, `SkizzyResourceAttribute` and `SkizzyEventAttribute`, in any namespace. It then reads their named arguments. lhervier's `McpToolAttribute` uses the same idea. Mods copy a single CC0 file, change its namespace, and ship with **zero** binary dependency. If Skizzy is not installed, the attributes are inert metadata.

**Duck-typed calling conventions** (no host types needed):
- **Parameters.** Primitives, enums, strings, nullables, arrays and lists, `Vector3d`, and `Dictionary<string,object>`. Parameters of type `Vessel`, `Part` or a `PartModule` subclass are resolved from handles or names by the host.
- **Injected parameters, by name and type.** `Action<double,string> progress` and `Func<bool> isCancelled`.
- **Return values.** `void`, a primitive, a POCO (public fields and properties serialized), `IDictionary`, a raw JSON `string` when `ReturnsJson = true`, or **`IEnumerator`**. An `IEnumerator` makes the tool long-running: the host runs it as a coroutine. Yielded objects with `progress` and `message` members are progress reports; the last other yielded value is the result.
- **Errors.** Throw any exception. If it has a `SkizzyErrorKind` string property, the host maps it to that kind; otherwise it becomes `PROVIDER_ERROR`.

### 5.3 Example: a mod registering a tool

**(1) Tier 2, duck-typed, zero dependency.** This is what the BDArmory upstream PR would look like. The file `BDArmory/ModIntegration/Skizzy/SkizzyAttributes.cs` is copied verbatim with `namespace BDArmory.ModIntegration.Skizzy`.

```csharp
using System;
using System.Collections;
using BDArmory.Competition;
using BDArmory.ModIntegration.Skizzy;   // mod-local copy of the CC0 attribute file

namespace BDArmory.ModIntegration
{
    [SkizzyProvider("bdarmory", Title = "BDArmory", Authority = 100,
        Description = "Weapons, AI pilots, competitions and scoring for combat in KSP.")]
    public static class BDArmorySkizzyTools
    {
        [SkizzyTool("bda.start_competition", Safety = "control", Scenes = "FLIGHT", LongRunning = true,
            Description = "Start a BDArmory competition between all loaded vessels that have an AI " +
                          "and a weapon manager on a non-neutral team. Completes when the competition " +
                          "has *started* (not finished); follow it with bda.get_competition_state.")]
        public static IEnumerator StartCompetition(
            [SkizzyParam(Description = "Separation distance in metres before engaging.",
                         Minimum = 0, Maximum = 200000, Default = 1000)] float distance,
            [SkizzyParam(Description = "FFA, SEQUENCED or WAYPOINTS.", Default = "FFA")]
                CompetitionType type = CompetitionType.FFA,
            [SkizzyParam(Description = "Tag appended to the results log file name.")] string tag = "",
            [SkizzyParam(Description = "Skip 'waiting for teams to get in position' after N seconds.",
                         Minimum = 0)] float? startNowAfterSeconds = null,
            Func<bool> isCancelled = null)                     // injected by the host
        {
            var comp = BDACompetitionMode.Instance;            // Flight-scene KSPAddon
            if (comp.competitionStarting || comp.competitionIsActive)
                throw new SkizzyError("CONFLICT", $"Competition {comp.CompetitionID} is already running.");

            comp.StartCompetitionMode(distance, false, tag, type);   // BDACompetitionMode.cs:352
            if (startNowAfterSeconds.HasValue) comp.StartCompetitionNow(startNowAfterSeconds.Value);

            while (comp.competitionStarting)                   // wait for CompetitionStarted()
            {
                if (isCancelled != null && isCancelled()) { comp.StopCompetition(); yield break; }
                yield return new { progress = 0.5, message = comp.competitionStatus.ToString() };
            }
            if (!comp.competitionIsActive)
                throw new SkizzyError("PRECONDITION_FAILED", comp.competitionStartFailureReason.ToString());

            yield return new { competitionId = comp.CompetitionID, tag = comp.competitionTag,
                               startUT = comp.competitionStartTime };
        }
    }

    // Mod-local exception type. The host reads the SkizzyErrorKind property by name.
    sealed class SkizzyError : System.Exception
    {
        public string SkizzyErrorKind { get; }
        public SkizzyError(string kind, string msg) : base(msg) { SkizzyErrorKind = kind; }
    }
}
```

**(2) Tier 3a builder style, hard reference.** Use this when the catalog depends on runtime state. Here, one enum value per craft file currently in `AutoSpawn`:

```csharp
using Skizzy.Protocol;

[SkizzyProvider("bdarmory")]
public sealed class BdaDynamicProvider : ISkizzyProvider
{
    public void Register(ISkizzyRegistry r)
    {
        r.Tool("bda.spawn_from_autospawn")
         .Describe("Spawn every craft in an AutoSpawn subfolder in a ring; subfolders are teams.")
         .Safety(Safety.Cheat).Scenes(GameScenes.FLIGHT).LongRunning()
         .Param("folder", Schema.Enum(AutoSpawnFolders()), "Folder under <KSP>/AutoSpawn")
         .Param("distance", Schema.Number(min: 10, max: 50000, def: 1000), "Ring radius in metres")
         .Coroutine((ctx, args) => SpawnRoutine(ctx, args));   // IEnumerator run on the main thread as an op

        r.Stream("bda.engagement", StreamOptions.Sampled(defaultHz: 2, maxHz: 20))
         .Describe("Per weapon manager: current targets, missiles away per target, guard state.")
         .Sampler(ctx => EngagementSnapshot.Build());       // runs in FixedUpdate within budget

        r.Event("bda.vessel_killed").Describe("A competitor died; includes killer and cause.");
        Scoring.OnDeath += d => r.Emit("bda.vessel_killed", d); // hypothetical hook; see §7.4
        r.WatchFolder("AutoSpawn", () => r.NotifyCatalogChanged()); // enum changes → catalog.changed
    }
}
```

### 5.4 Example JSON carried by the protocol

Catalog entry, abridged:

```json
{
  "name": "bda.configure_weapon_manager",
  "version": 1,
  "provider": "bdarmory",
  "title": "Configure weapon manager",
  "description": "Set weapon-manager (MissileFire) options on a vessel. Only the fields you pass are changed. Ranges are read live from the game, so new or changed limits show up automatically.",
  "safety": "control",
  "scenes": ["FLIGHT", "EDITOR"],
  "affinity": "main",
  "idempotent": true,
  "inputSchema": {
    "type": "object",
    "properties": {
      "vessel": { "type": "string", "description": "Vessel handle (v:123) or exact vessel name" },
      "multiTargetNum": { "type": "integer", "minimum": 1, "maximum": 1024, "title": "Max Turret Targets" },
      "multiMissileTgtNum": { "type": "integer", "minimum": 1, "maximum": 1024, "title": "Max Missile Targets" },
      "maxMissilesOnTarget": { "type": "integer", "minimum": 1, "maximum": 128, "title": "Missiles/Target" },
      "guardRange": { "type": "number", "minimum": 100, "maximum": 200000, "x-skz-units": "m" }
    },
    "required": ["vessel"],
    "additionalProperties": true
  },
  "permission": { "state": "allow", "source": "MOD[BDArmory].control=allow" },
  "availability": { "available": true }
}
```

A call, the long-operation flow, and an error:

```json
→ {"jsonrpc":"2.0","id":7,"method":"cap.call","params":{"name":"bda.spawn_vessels","args":{"folder":"naval-mt","body":"Kerbin","lat":-0.2,"lon":-74.0,"alt":0,"distance":4000}}}
← {"jsonrpc":"2.0","id":7,"result":{"status":"accepted","op":"op:12","meta":{"ut":204511.32,"scene":"FLIGHT"}}}
← {"jsonrpc":"2.0","method":"op.progress","params":{"op":"op:12","progress":0.5,"message":"Spawned 4/8 vessels"}}
← {"jsonrpc":"2.0","method":"op.done","params":{"op":"op:12","status":"ok","value":{"vessels":[{"handle":"v:3281937421","name":"Kirov_1","team":"A"}]}}}
→ {"jsonrpc":"2.0","id":8,"method":"cap.call","params":{"name":"bda.fire_missile_at","args":{"vessel":"Kirov_1","target":"Iowa_2"}}}
← {"jsonrpc":"2.0","id":8,"error":{"code":-32002,"message":"bda.fire_missile_at needs 'destructive' for BDArmory (policy: deny)","data":{"kind":"PERMISSION_DENIED","capability":"bda.fire_missile_at","mod":"BDArmory","required":"destructive","retryable":false,"hint":"Ask the user to allow it in the Skizzy window, or call permissions.request."}}}
```

### 5.5 Versioning, handshake, negotiation, errors (summary)

- **Protocol version** is `MAJOR.MINOR`. 0.x is unstable. From 1.0, a minor bump is additive only.
- **Capabilities** carry their own integer `version`. Breaking changes publish `name@2` alongside `name@1`.
- **Providers** use semver.
- **Catalog revision** is a monotonically increasing integer with `catalog.changed {revision, added, removed, changed}`.
- **Handshake.** The WebSocket upgrade carries `Authorization: Bearer <token>` (header check) and the `Sec-WebSocket-Protocol: skizzy.v0` subprotocol. Then:
  1. `session.hello {protocol, versions[], client, features[]}`
  2. The host replies `{version, host, session, features[], limits, catalogRevision, state}`.
  3. `catalog.get`
  4. calls
- **Feature negotiation.** Feature flags are intersected: `ops`, `streams`, `events`, `events.poll`, `confirm.game`, `confirm.client`, `deepReflection`, `lockstep`. Unknown features are ignored. A version mismatch returns `VERSION_UNSUPPORTED {supported:[…]}`.
- **Error model.** JSON-RPC error objects carry a numeric `code` (-32001…-32013) and `data.kind`, a stable string: `UNAUTHENTICATED`, `PERMISSION_DENIED`, `CONFIRMATION_REQUIRED`, `UNAVAILABLE_IN_SCENE`, `NOT_FOUND`, `BUSY`, `TIMEOUT`, `CONFLICT`, `PRECONDITION_FAILED`, `PROVIDER_ERROR`, `CANCELLED`, `RATE_LIMITED`, `VERSION_UNSUPPORTED`, plus the standard JSON-RPC errors. Each error also carries `retryable`, `hint` and context. Full table in spec §9.

### 5.6 Mapping onto MCP (bridge behaviour)

| skizzy-protocol | MCP 2026-07-28 | Notes |
|---|---|---|
| `session.hello` / catalog | `server/discover` capabilities, `tools/list`, `resources/list`, `resources/templates/list` | Lists are deterministic (sorted). `ttlMs` follows the catalog revision, with `cacheScope:"private"`. |
| Capability | **Tool** | `inputSchema` and `outputSchema` pass through (JSON Schema 2020-12). Results include `structuredContent` *and* a short text summary, as the spec recommends. |
| Capability name `bda.start_competition` | Tool name `bda_start_competition` | The Claude API restricts tool names to `^[a-zA-Z0-9_-]{1,64}$`, so `.` becomes `_` and names are truncated with a hash suffix if needed. The original name goes in `_meta`. |
| `safety` | Tool annotations | `read` → `readOnlyHint:true`. `destructive`/`cheat`/`unsafe` → `destructiveHint:true`. `idempotentHint` comes from the capability. `openWorldHint:false`, since KSP is a closed world. |
| Permission state | Tool visibility | Denied capabilities are **hidden** by default, so the model does not try them (configurable: `--show-denied`). Grant changes emit `notifications/tools/list_changed` on the `subscriptions/listen` stream. |
| Resource and stream | Resource and resource template | `ksp://vessels`, `ksp://vessel/{handle}`, `ksp://mods`, `ksp://config/{type}/{name}`, `bda://competition`, `bda://scores`, `bda://logs/{competitionId}`, `skizzy://audit/recent`, `skizzy://catalog` |
| `stream.data` | `notifications/resources/updated` (on `subscriptions/listen`, `resourceSubscriptions`) | Throttled to at least 1 s, because MCP resource updates carry only the URI and the client re-reads. High-rate data stays on the native socket for the harness. |
| Long operation (`op:N`) | MCP **Tasks extension** (`io.modelcontextprotocol/tasks`) if the client supports it. Otherwise the tool waits up to `waitMs` (default 30 s) while sending `notifications/progress` and then returns `{op, status:"running"}`. The model continues with `skizzy_wait(op)` or `skizzy_cancel(op)`. | The model never needs to busy-poll. |
| `CONFIRMATION_REQUIRED` | **MRTR `InputRequiredResult`** carrying an elicitation form ("Allow Claude to fire a missile from Kirov_1 at Iowa_2?"), if the policy's `confirmChannel` allows `client` | The human answers in the MCP client UI. Otherwise the bridge waits on the in-game popup and tells the model "waiting for the user in-game". |
| Errors | Tool results with `isError:true` and structured `{kind, message, hint}` | Execution errors are shown to the model so it can recover, rather than raised as protocol errors. |
| `ksp.log`, `bda.tail_log` | Tools, plus `bda://logs/*` resources | — |

**Controlling tool count.** Tier 0 can surface thousands of members, so members are **never** turned into one tool each. The bridge exposes:

- **`core` toolset** (about 14, always on):
  - `skizzy_status`, `skizzy_search_capabilities`, `skizzy_call`, `skizzy_wait`, `skizzy_cancel`
  - `skizzy_request_permission`
  - `ksp_list_vessels`, `ksp_get_vessel`, `ksp_query`, `ksp_describe_module`, `ksp_set_fields`, `ksp_invoke`, `ksp_action_group`
  - `ksp_tail_log`
- **`bda` toolset** (about 24, on by default when BDArmory is installed and granted)
- **`bda-advanced`**, **`universal-write`** and **`deep`**: opt-in with `--toolsets`
- **Tier 2 and 3 providers from other mods** are reachable at once through `skizzy_search_capabilities`, which returns schemas, followed by `skizzy_call`. A user can promote a provider to first-class tools with `--toolsets +hullbreach`.

**Transports.** stdio is the default. Example registration:

```
claude mcp add skizzy -- npx -y skizzy-mcp --ksp "D:\Epic Games\KerbalSpaceProgram\English"
```

Optional Streamable HTTP is bound to `127.0.0.1`, validates `Origin`, and requires the same token. The SDK also negotiates the older 2025-11-25 handshake for clients that have not moved to 2026-07-28.

### 5.7 AI-agnostic by construction
- The **host protocol is the standard.** MCP is one projection of it.
- Any language with a WebSocket client can connect, read the token from `GameData/Skizzy/PluginData/auth.token`, and call `session.hello`.
- The spec includes a 25-line Python example.
- Permissions are enforced host-side, so direct clients get exactly the same safety guarantees as MCP clients.
- A future OpenAI-tools or LangChain adapter would be another thin projection, like the MCP bridge.

---

## 6. User permission model

### 6.1 Levels

| Level | Meaning | Examples |
|---|---|---|
| `read` | Observe; no game state changes | List vessels, read fields, scores, radar contacts, logs |
| `control` | Reversible actions a player could take in normal play | Set AI parameters, toggle guard mode, set team, action groups, throttle, select weapon, change BDArmory competition settings, BDArmory time scale |
| `destructive` | Irreversible, or loses state | Fire weapons, stage, decouple or self-destruct, remove vessels, stop a competition early, revert, load a save (loses unsaved state) |
| `cheat` | Not possible in normal play | Spawn vessels, teleport or move (VesselMover), set resource amounts, set HP, BDArmory `INFINITE_*`, gravity hacks |
| `unsafe` | Can break the game or reach outside it | Deep reflection set or call, writing files outside sandboxes, arbitrary method invocation |

Levels are **not** a strict hierarchy. Each is granted separately, so a "test director" can have `cheat` (spawn) without `destructive` (manual weapon fire).

### 6.2 Policy evaluation (in the host, on every call)

```
decision = clamp_by_client_cap(
    capability rule  (CAPABILITY name=…)          ─┐  most specific wins;
 ?? mod-level rule   (MOD id=… <level>=…)          │  deny beats allow at equal specificity;
 ?? part-pack filter (PARTPACK id=… deny=control)  │  session grants (from the in-game prompt)
 ?? global default   (defaultPolicy = deny)       ─┘  sit just above file rules
)
→ allow | ask | deny        (+ rate limits, + scene/lockout checks)
```

- **Stock `read` is the only default allow.** Everything else is `deny` until the user enables it.
- `unsafe` can **never** be set to `allow` from the network. It can only be set by editing the file, or set to `ask` in the in-game UI.
- No protocol method changes permissions. The AI can only *request*: `permissions.request {capability|mod, level, reason}` shows a prompt to the human, with the AI's reason **labelled as AI-supplied text**.
- Rate limits apply per capability (for example `bda.spawn_vessels` at 6/min, `bda.fire_missile_at` at 20/min) and globally per session.
- **Kill switch.** A toolbar "Pause AI" button and a hotkey (default `Ctrl+Alt+K`, configurable) immediately:
  - deny all non-read calls
  - cancel operations
  - optionally pause the game
  - display a banner until the user resumes

  This is an idea from IshiakiZ/ksp-ai-bridge.

### 6.3 First run and presets
On first connection the host shows a dialog listing the client name and offering presets:
- **Observer:** stock and BDArmory `read`.
- **Test Director:** BDArmory `read`, `control` and `cheat` (spawn, settings); `destructive` set to `ask`; other mods `read`.
- **Full Combat:** BDArmory everything except `unsafe`.
- **Custom.**

The choice is written to the config file.

### 6.4 Config file: `GameData/Skizzy/PluginData/permissions.cfg`

ConfigNode format matches KSP conventions, and `PluginData` keeps edits out of the MM cache, following BDArmory's pattern.

```
SKIZZY_PERMISSIONS
{
    version = 1
    defaultPolicy = deny
    confirmChannel = game          // game | client | either
    confirmTimeoutSec = 60
    MOD { id = Squad       read = allow }
    MOD
    {
        id = BDArmory
        read = allow
        control = allow
        cheat = allow
        destructive = ask
        CAPABILITY { name = bda.spawn_vessels   policy = allow  rateLimit = 6/min }
        CAPABILITY { name = bda.fire_missile_at policy = ask    rateLimit = 20/min }
        CAPABILITY { name = bda.set_settings    policy = allow  denyKeys = INFINITE_*, REMOTE_ORCHESTRATION_* }
    }
    MOD { id = HullBreach  read = allow  control = ask }
    PARTPACK { id = Johnny  control = allow }          // MNWS part pack (config-only)
    CLIENT  { name = skizzy-mcp  maxLevel = cheat }    // cap any client by name
    DEEP_REFLECTION
    {
        enabled = false
        MOD { id = BDArmory  deepRead = deny  deepWrite = deny  deepCall = deny }
    }
}
```

### 6.5 In-game UI
- A toolbar button through ToolbarControl (installed; soft dependency, falling back to stock `ApplicationLauncher`).
- An IMGUI window styled like BDArmory, with ClickThroughBlocker as a soft dependency. Tabs:
  1. **Connections:** clients, session age, calls/min, Pause AI, Disconnect.
  2. **Mods:** a mods × levels grid of allow / ask / deny toggles, integration tier, and part packs.
  3. **Capabilities:** search, per-capability override, last used.
  4. **Activity:** live audit feed with filters.
  5. **Settings:** port, regenerate token, budgets, toolsets hint, deep-reflection switches (with warnings).

### 6.6 Confirmation flow (`ask`)
1. The host shows a `PopupDialog`. It contains:
   - the client name
   - the capability title and safety level
   - a human-readable summary of the arguments (vessel names, not handles)
   - the AI's stated reason, if any

   Buttons: **Allow once**, **Allow this session**, **Deny**, **Deny and pause AI**.
2. The call waits **on a network thread** until the human decides or `confirmTimeoutSec` passes, which counts as deny.
3. If `confirmChannel` includes `client`, the bridge can satisfy the prompt through MCP elicitation instead (§5.6). Game-side prompts stay authoritative when the channel is `game`.

### 6.7 Audit log
- Format: JSONL at `GameData/Skizzy/PluginData/audit/audit-YYYYMMDD.jsonl`. Rotation keeps 14 days or 50 MB.
- Each line contains:
  - `tsUtc`, `ut`, `session`, `client`
  - `method`/`capability`, `args` (truncated to 1 KB, with redaction hooks)
  - `decision` and the matched rule (`policySource`)
  - `confirmedBy`: `game`, `client` or `n/a`
  - `status`, `errorKind`, `durationMs`, `mainThreadMs`
- Viewable in the UI and readable by a client as `skizzy://audit/recent` (read level, own session only unless granted).

### 6.8 Network and host security
- **Bind** to `127.0.0.1` only (optionally `::1`). There is no `0.0.0.0` option in v1, unlike Telemachus.
- **Token:**
  - 256-bit from `RNGCryptoServiceProvider`, stored in `PluginData/auth.token`
  - regenerated from the UI
  - compared in constant time
  - the bridge reads it from `--ksp <dir>`
- **Browser attacks:**
  - A web page *can* open `ws://127.0.0.1` in the user's browser. The host therefore **rejects any request that carries an `Origin` header** unless it is in an explicit allow-list (default empty).
  - It also checks `Host` is `127.0.0.1:<port>` or `localhost:<port>`, which defeats DNS rebinding.
  - The token is still required.
- **Resource limits:**
  - 4 MB maximum message
  - JSON depth 64
  - 4 connections
  - 32 calls in flight per session
  - 64 subscriptions per session
  - idle timeout 10 minutes
- **Path sandbox.** Every path parameter is canonicalised with `Path.GetFullPath` and prefix-checked against allowed roots:
  - `Ships/`, `saves/<profile>/Ships/`, `AutoSpawn/`
  - `GameData/BDArmory/Logs/`
  - `KSP.log`
  - `GameData/Skizzy/PluginData/`

  Writes are allowed only to `AutoSpawn/` and our `PluginData/`.
- **Prompt injection.** Vessel names, craft descriptions and log lines are attacker-controllable text, for example downloaded craft files. The bridge returns them as data fields and never puts them in tool descriptions. Because permissions are enforced host-side, an injected instruction cannot escalate beyond what the user granted.
- **The bridge's offline log reader** (when the game is down) is read-only and limited to `KSP.log` and BDArmory `Logs/`.

---

## 7. BDArmory adapter

### 7.1 Tool list

All tools take `vessel` as a handle or an exact name. Ranges are read live from `UI_Control` and are not hard-coded. "WM" means `MissileFire`.

| Tool | Level | Key parameters | Maps to (BDArmory source) |
|---|---|---|---|
| `bda.status` | read | — | `BDArmorySetup.Version` (`UI/BDArmorySetup.cs:104`), `BDACompetitionMode.Instance` flags, `BDATournament.Instance.tournamentStatus` (`Competition/BDATournament.cs:1576-1588`), `VesselSpawnerStatus.vesselsSpawning`/`vesselSpawnSuccess`/`spawnFailureReason` (`VesselSpawning/VesselSpawnerBase.cs:20-48`) |
| `bda.list_craft` | read | `source: autospawn\|ships\|all`, `folder?`, `withModules?` | Enumerates `<KSP>/AutoSpawn/**` (`VesselSpawnerBase.AutoSpawnFolder`, `:52`) and `saves/<profile>/Ships/{VAB,SPH}` (`VesselMover.GetShipsPathFor`, `VesselSpawning/VesselMover.cs:1689`). It parses the craft ConfigNode headers and `.loadmeta` and flags whether each craft has `MissileFire` and an AI module (pre-flight validation; compare `SpawnUtils.CheckAIWMPlacement`, `SpawnUtils.cs:469`). |
| `bda.stage_craft` | cheat (file write, sandboxed) | `files[]`, `folder`, `team?` | Copies craft into `AutoSpawn/<folder>/<team>/`. Subfolders become teams (`CircularSpawning.cs:132-164`). |
| `bda.spawn_vessels` (op) | cheat | `folder` or `teams:[[craft…]…]`, `body` (world index; Kerbin = 1), `lat`, `lon`, `alt`, `distance`, `absDistance`, `refHeading`, `killEverythingFirst`, `assignTeams`, `numberOfTeams`, `teamCounts` | `CircularSpawning.Instance.SpawnAllVesselsOnce(CircularSpawnConfig)` (`VesselSpawning/CircularSpawning.cs:98-114`). `SpawnConfig` fields come from `SpawnConfig.cs:17-58`. Completion: `VesselSpawnerStatus.vesselsSpawning == false`, then `vesselSpawnSuccess`/`spawnFailureReason`. Returns new vessels and their handles. |
| `bda.spawn_template` (op) | cheat | `vessels:[{craft, lat, lon, heading, team}]` | `CustomTemplateSpawning.Instance.SpawnCustomTemplate(CustomSpawnConfig)` (`VesselSpawning/CustomSpawnTemplate.cs:95`) with `CustomVesselSpawnConfig(lat, lon, heading, teamIndex)` (`SpawnConfig.cs:137-152`). **Best fit for naval formations**, for example "carrier group A at X, line B at Y". |
| `bda.spawn_vessel` (op) | cheat | `craft`, `lat`, `lon`, `alt`, `heading`, `pitch` | `SingleVesselSpawning.Instance.SpawnVessel(...)` coroutine (`VesselSpawning/SingleVesselSpawning.cs:57`). The lower-level `VesselSpawner.SpawnVesselFromCraftFile` (`VesselSpawner.cs:70`) is the fallback. |
| `bda.remove_vessels` (op) | destructive | `vessels[] \| all` | `SpawnUtils.RemoveVessel` / `RemoveAllVessels` (`SpawnUtils.cs:462-464`) |
| `bda.list_teams` | read | — | `BDArmorySetup.Instance.Teams` (`SortedList<string,BDTeam>`, `UI/BDArmorySetup.cs:217`), `BDTeam` (`Competition/BDTeam.cs`) |
| `bda.set_team` | control | `vessel`, `team`, `allies?[]`, `neutral?` | `MissileFire.SetTeam(BDTeam.Get(name))` (`Control/MissileFire.cs:1161`). It sets every WM on the vessel and handles `TargetInfo`. **Do not use `SetTeamByName`: it is an empty stub (`:1194`).** `MissileFire.OnChangeTeam` (`:1159`) drives the `bda.team_changed` event. |
| `bda.set_guard_mode` | control | `vessel`, `enabled` | `guardMode` (`MissileFire.cs:1030`). Calls `ToggleGuardMode()` (`:879`) **only if the state differs**, because toggling has side effects (unslaves turrets, clears the selected weapon). |
| `bda.get_weapon_manager` | read | `vessel` | All WM `KSPField`s, plus state: `guardMode`, `currentTarget` (`:420`), `targetsAssigned`/`missilesAssigned` (`:422-423`), `firedMissiles` (`:94`), `selectedWeapon`, `weaponArray` (`:64`), `missileIsIncoming` (`:393`), `underFire` (`:609`), `vesselRadarData` (`:491`). It covers all WMs on the vessel via `VesselModuleRegistry.GetMissileFires` (`Utils/VesselModuleRegistry.cs:521`). |
| `bda.configure_weapon_manager` | control | `vessel`, and any WM field. Notably:<br>• `multiTargetNum` (1…**1024**, `MissileFire.cs:680-684`)<br>• `multiMissileTgtNum` (1…1024, `:688`)<br>• `maxMissilesOnTarget` (1…**128**, `:690-694`)<br>• `targetScanInterval`, `fireBurstLength`, `AutoFireCosAngleAdjustment`, `guardAngle`, `guardRange`, `gunRange`<br>• target priority weights (`targetBias`, `targetWeight*`, `:739+`)<br>• `targetCoM`/`targetCommand`/`targetEngine`/`targetWeapon`/`targetMass`/`targetRandom`<br>• `isVIP` (`:873`), `weaponChannel` (`:877`), `nonGuardModeCMs`, `DynamicRadarOverride`<br>• **parallel-fire fields: auto-discovered** (below) | Uses the §4.5 write path (`BaseField.SetValue` plus `onFieldChanged`) so BDArmory's UI callbacks run. The schema is built from `[KSPField]`/`UI_FloatRange` at runtime, so **the parallel-fire settings the other agent is adding show up with no adapter change.** A Tier-1 annotation then adds descriptions once their names are final. |
| `bda.list_ai` / `bda.get_ai` | read | `vessel` | AI modules on the vessel: `BDModulePilotAI`, `BDModuleSurfaceAI`, `BDModuleVTOLAI`, `BDModuleOrbitalAI` (all `BDGenericAIBase`, `Control/BDGenericAIBase.cs:23`), plus `pilotEnabled` (`:27`) and `currentCommand` (`:88`) |
| `bda.set_ai_mode` | control | `vessel`, `ai: pilot\|surface\|vtol\|orbital`, `surfaceType?: Stationary\|Land\|Water\|Amphibious\|Submarine`, `params{}`, `activate?` | Surface or submarine: `BDModuleSurfaceAI.SurfaceTypeName` (`BDModuleSurfaceAI.cs:76-78`), with `CombatAltitude` as depth for subs (default -75, `:89-91`), `CruiseSpeed`, `MaxSpeed`, `BroadsideAttack`, `MinEngagementRange`, `MaxEngagementRange`, … Pilot: `defaultAltitude`, `minAltitude`, `maxSpeed`, `idleSpeed`, `maxAllowedGForce`, `extendDistanceAirToAir` (`BDModulePilotAI.cs:328-570`). Activation: `vessel.ActiveController().SetActiveAI(ai)` (`BDGenericAIBase.cs:248-253`, `Extensions/VesselExtensions.cs:361`); deactivation: `DeactivatePilot()` (`:213`). *It can only switch between AI modules present on the craft.* |
| `bda.command_ai` | control | `vessel`, `command: free\|attack\|fly_to\|follow\|waypoints`, `lat/lon/alt?` | `ReleaseCommand`, `CommandAttack`, `CommandFlyTo`, `CommandFollowWaypoints`, `SetWaypoints` (`BDGenericAIBase.cs:404-540`); `PilotCommands` (`Control/IBDAIControl.cs:74`) |
| `bda.start_competition` (op) | control | `distance`, `type: FFA\|SEQUENCED\|WAYPOINTS`, `tag`, `startDespiteFailures`, `startNowAfterSeconds?` | `BDACompetitionMode.Instance.StartCompetitionMode(distance, startDespiteFailures, tag, compType)` (`Competition/BDACompetitionMode.cs:352`) and `StartCompetitionNow(delay)` (`:338`). Completes when `competitionIsActive`. Fails with `competitionStartFailureReason` (`CompetitionStartFailureReason`, `:22`). |
| `bda.stop_competition` | destructive (ends a run early) | `tag?` | `StopCompetition()` (`:392`), which calls `LogResults` → `Scores.LogResults` and writes `Logs/<id>[-tag].log` (`Scoring.cs:934-945`). Returns the log path. |
| `bda.get_competition_state` | read (snapshot) | — | `competitionStarting`, `competitionIsActive`, `sequencedCompetitionStarting` (`:313-315`), `CompetitionID`, `competitionTag`, `competitionType`, `competitionStartTime` (`:36-39`), elapsed UT, `competitionStatus.ToString()` (only populated when `DISPLAY_COMPETITION_STATUS`, `BDArmorySettings.cs:107`), alive and dead players, `Scores.deathOrder` |
| `bda.get_scores` | read (snapshot) | `players?[]`, `fields?[]`, `format: full\|summary` | `BDACompetitionMode.Instance.Scores.ScoreData` (`Dictionary<string,ScoringData>`, `Scoring.cs:23-30`). `ScoringData` (`:949-1052`) is serialized by reflecting over its public fields, so it stays correct as BDArmory adds fields. `summary` gives per player: team, alive state, kills and assists, hits/shots, missile strikes, damage dealt and taken, `remainingHP`, death order. |
| `bda.list_targets` | read (snapshot) | `vessel \| team` | `BDATargetManager.TargetList(team)` (`UI/BDATargetManager.cs:1356`); `TargetInfo` (`Targeting/TargetInfo.cs`): name, team, `isMissile`, position/velocity converted to geo, `NumFriendliesEngaging` (`:497`), and `BDATargetManager.TargetPriorityScore(target, targetMf, mf)` (`:1602`) |
| `bda.get_radar_contacts` | read | `vessel` | `wm.vesselRadarData.GetLockedTargets()` (`Radar/VesselRadarData.cs:158`), `numLockedTargets`, `MaxRadarRange()` (`:567`). The full contact list (`displayedTargets`, private, `:141`) is read **by reflection in the shim**; the PR adds a public read-only accessor. Contacts are `TargetSignatureData` (`Targeting/TargetSignatureData.cs:13-35`): vessel, team, `geoPos`, velocity, `signalStrength`, `range`, `isDecoy`, `lockedByRadar`. IRST contacts work the same way. |
| `bda.radar_control` | control | `vessel`, `enable?`, `lock?: target`, `unlockAll?` | `ModuleRadar.EnableRadar()`/`DisableRadar()` (`Radar/ModuleRadar.cs:444-462`), `VesselRadarData.TryLockTarget(Vessel)` (`:964`), `UnlockAllTargets()` (`:2404`) |
| `bda.select_weapon` | control | `vessel`, `index \| name` | `MissileFire.CycleWeapon(int)` (`:5455`), `UpdateList()` (`:4763`) |
| `bda.fire_missile_at` | destructive | `vessel`, `target`, `missile?` (short name or part), `overrideGuard?` | `FireCurrentMissile(missile, checkClearance, targetVessel, targetData)` is **private** (`MissileFire.cs:4570`). Phase 1: the shim calls it through a cached reflected delegate after `SetTarget(TargetInfo)` (`:8348`), which keeps turret, rotary-rail and deployable-rail handling and `SendTargetDataToMissile` (`:8831`). Phase 2: the PR adds `public bool FireMissileAt(MissileBase, Vessel)`. Refused with `CONFLICT` while guard mode is on, unless `overrideGuard`. |
| `bda.fire_guns` | destructive | `vessel`, `seconds` | `AGFireGunsHold`/`AGFireGunsToggle` actions (`MissileFire.cs:1348-1365`) with timed release |
| `bda.fire_countermeasures` | destructive (expends stores) | `vessel`, `type: flares\|chaff\|smoke\|decoys\|bubbles\|ecm`, `count?`, `duration?` | `FireFlares`/`FireChaff`/`FireSmoke`/`FireDecoys`/**`FireBubbles`** (subs)/`FireECM`/`FireAllCountermeasures` (`MissileFire.cs:4242-4318`) |
| `bda.get_missiles_in_flight` | read (snapshot) | `team?`, `source?`, `target?` | `BDATargetManager.FiredMissiles` (`UI/BDATargetManager.cs:30`) as `MissileBase`: `MissileState`, `GuidanceMode`, `TargetingMode`, `SourceVessel`, `Team`, `targetVessel`, `TargetPosition`, `TimeIndex`, `HasFired` (`Weapons/Missiles/MissileBase.cs:429-554`). Also returns per-target counts and `wm.GetMissilesAway(target)` (`MissileFire.cs:2275`), which the multi-target validation relies on. |
| `bda.get_damage_report` | read | `vessel`, `partsBelowPct?` | Per part `HitpointTracker.Hitpoints`, `GetMaxHitpoints()`, `Armor`, `ArmorThickness` (`Damage/HitpointTracker.cs:27-96, 1072`). Vessel totals, parts lost (`ScoringData.previousPartCount`), last attacker. Merged with HullBreach flood state when that provider is present. |
| `bda.get_settings` / `bda.set_settings` | read / control, with `cheat` for `INFINITE_*`, gravity and similar | `keys[] \| prefix`, `values{}`, `persist?` | `BDArmorySettings` statics marked `[BDAPersistentSettingsField]` (409 fields), reflected exactly as `BDAPersistentSettingsField.Save` does. For example `COMPETITION_DURATION` (`BDArmorySettings.cs:138`), `COMPETITION_DISTANCE`, `COMPETITION_START_NOW_AFTER`, `TIME_OVERRIDE`/`TIME_SCALE`, `DEBUG_*`. `persist:true` calls `BDArmorySetup.SaveConfig()` (`UI/BDArmorySetup.cs:961`). Otherwise changes are session-only, and the harness restores them. |
| `bda.tournament` (op) | cheat | `action: setup\|run\|stop\|status`, `folder`, `rounds`, `vesselsPerHeat`, `teamsPerHeat`, `style`, … | `BDATournament.Instance.SetupTournament(...)` (`BDATournament.cs:1687`), `RunTournament()` (`:1723`), `StopTournament()` (`:1735`) |
| `bda.tail_log` | read | `source: ksp\|bda\|competition\|tournament\|timesync`, `lines`, `cursor?`, `grep?`, `competitionId?` | `KSP.log` through the host's `Application.logMessageReceivedThreaded` ring buffer (filtered by `[BDArmory.` prefixes or a regex), and result files from `GameData/BDArmory/Logs/…` (`Scoring.cs:934-945`, `BDATournament.cs:2286`, `UI/BDArmorySetup.cs:4762`, `UI/LoadedVesselSwitcher.cs:1686`) |

**Events and streams provided by the adapter:**

| Name | Type | Source |
|---|---|---|
| `bda.competition_started` / `bda.competition_ended` | event | The host watches transitions of `competitionStarting` and `competitionIsActive`; there are no BDArmory events for these. |
| `bda.vessel_killed` | event | Diff of `Scores.deathOrder`, plus `GameEvents.onVesselDestroy` |
| `bda.team_changed` | event | `MissileFire.OnChangeTeam` (`MissileFire.cs:1159`) |
| `bda.missile_launched` | event | `RadarWarningReceiver.OnMissileLaunch` (`Radar/RadarWarningReceiver.cs:28`) |
| `bda.radar_ping` | event | `RadarWarningReceiver.OnRadarPing` (`Radar/RadarWarningReceiver.cs:24`), rate-limited |
| `bda.engagement` | stream | Per WM: current target, assigned targets, missiles away per target, guard state, incoming missiles |
| `bda.scores` | stream | — |
| `bda.missiles` | stream | — |

### 7.2 Implementation notes and gotchas
- **Build against the installed DLL, not the source tree.** The shim references `GameData/BDArmory/Plugins/BDArmory.dll`, the same pattern as the patched HullBreach `.csproj`. The other agent's working tree is never touched.
- **Version gate.** The shim checks the BDArmory assembly version (1.13.x). Each reflected private member is resolved once at startup. A missing member disables *that one tool*, which reports `PRECONDITION_FAILED {reason:"bda_api_changed"}`, and logs the details.
- **Scene-bound singletons** are re-acquired after each scene load: `BDACompetitionMode.Instance`, `CircularSpawning.Instance`, `CustomTemplateSpawning.Instance`, `BDATournament.Instance`.
- **Guard mode conflicts.** Manual fire and `command_ai` are refused while guard mode is on unless the call overrides it explicitly. Otherwise the AI and the agent fight each other.
- **Settings hygiene.** `set_settings` keeps a pre-change snapshot per session. The harness always restores it, and `persist` is opt-in.
- **Vessel name churn.** Names are deconflicted at spawn time, so the name ↔ handle map is refreshed on every `onVesselCreate`/`onVesselRename`.
- **Performance at 1024 targets.** Snapshot builders iterate `targetsAssigned` and `FiredMissiles` once per sample, with no LINQ and pooled lists. At most one WM-heavy builder runs per `FixedUpdate`, round-robin.
- **Remote orchestration is not reused.** BDArmory's in-tree `RemoteOrchestration` (`BDAScoreClient`/`BDAScoreService`, HTTPS polling of `REMOTE_ORCHESTRATION_BASE_URL`, `BDArmorySettings.cs:335`) is deprecated according to comments in `CircularSpawning.cs:107-110`. Only its *concepts* carry over: spawn strategies and per-heat score models.

### 7.3 Where the adapter lives: both, in phases

| | Phase 1: separate shim mod `Skizzy.BDArmory` | Phase 2: upstream PR into BDArmory |
|---|---|---|
| Why | Ships now, independent of BDArmory release cadence and of the in-progress multi-target branch. Can use reflection for private members. | Durable. In-tree code is refactored along with BDArmory, so it never breaks silently. |
| Dependency | Hard reference to `BDArmory.dll` (it is BDArmory-specific anyway), plus `Skizzy.Contract` | **None.** Tier-2 attributes in a CC0 file copied into `BDArmory/ModIntegration/Skizzy/`. |
| What the PR adds | — | (1) Attribute-tagged tool methods, mostly thin wrappers. (2) A small public API: `MissileFire.FireMissileAt(...)`, `VesselRadarData.GetDisplayedContacts()` (read-only copy), `BDACompetitionMode.OnCompetitionStarted`/`OnCompetitionEnded` and `Scoring.OnDeath` static events, a working `MissileFire.SetTeamByName`. (3) Zero runtime cost when no Skizzy host is present: attributes are inert and no reflection runs. |
| Coexistence | The shim registers provider `bdarmory` with `Authority = 50`. The PR uses 100. The host keeps the higher-authority implementation of each capability name. The shim's remaining tools fill gaps. | — |
| Licence | CC-BY-SA 2.0 compatible: distribute the shim under CC-BY-SA 4.0, or dual MIT / CC-BY-SA, to stay clear of share-alike questions since it links BDArmory. | Inherits BDArmory's CC-BY-SA 2.0. The copied attribute file is CC0, so no conflict. |

PR etiquette: open a forum or Discord discussion with the maintainers (DocNappers / BrettRyland et al.) before writing it. Target the `dev` branch, as their README requires, and keep the PR small and reviewable: attributes plus four public methods.

---

## 8. Telemetry and automated test battles

### 8.1 Scenario format (YAML, run with `skizzy battle run` or the `bda_run_battle` tool)

```yaml
name: naval-multitarget-v1
build_label: "bda-1.13.1+multitarget be734f0bb"   # recorded; also auto-captured from assembly + file hash
site: { body: Kerbin, lat: -0.15, lon: -74.20, alt: 0 }   # open water east of KSC
spawn:
  mode: template             # circular | template
  teams:
    A: { craft: ["Ships/SPH/Kirov.craft"], positions: [{lat: -0.15, lon: -74.20, heading: 90}] }
    B: { craft: ["Ships/SPH/Iowa.craft","Ships/SPH/Iowa.craft","Ships/SPH/Fletcher.craft"],
         formation: { kind: line, origin: {lat: -0.15, lon: -73.95}, spacing_m: 600, heading: 270 } }
settings:                     # BDArmorySettings overrides, restored afterwards
  COMPETITION_DURATION: 10
  TIME_OVERRIDE: true
  TIME_SCALE: 3
vessel_config:
  "team:A":
    weapon_manager: { multiTargetNum: "${mt}", maxMissilesOnTarget: "${mmt}", guardRange: 30000 }
    ai: { ai: surface, surfaceType: Water, MaxEngagementRange: 25000 }
competition: { distance: 8000, type: FFA, start_now_after_s: 30 }
stop: { ut_limit_s: 600, when: one_team_left }
matrix: { mt: [1, 4, 16, 64], mmt: [1, 2, 4] }
repeats: 5
telemetry: { rate_hz: 4, streams: [bda.engagement, bda.missiles, bda.scores], fields:
  [ {module: ModuleHullBreach, fields: [floodedDisplay, inflowDisplay], aggregate: perVessel} ] }
expect:
  - "max(missiles_in_flight_per_target) <= wm.maxMissilesOnTarget"
  - "count(exceptions) == 0"
  - "for mt>1: max(distinct_turret_targets(A)) > 1"
```

### 8.2 Runner lifecycle (in the bridge, over the native socket)
1. **Preflight.** Host reachable; scene is `FLIGHT`; BDArmory present and version recorded; required grants present (`bda` control and cheat) or the run aborts with a clear message; no competition running; craft files exist; craft contain WM and AI (from `bda.list_craft`).
2. **Snapshot settings** with `bda.get_settings`, then apply overrides.
3. For each (matrix cell × repeat):
   1. `bda.remove_vessels all`
   2. spawn (`template` or `circular`) and wait for the operation
   3. apply `vessel_config` per selector
   4. read the configuration back and verify it
   5. subscribe to telemetry
   6. `bda.start_competition` and wait for the operation
   7. stream telemetry to `telemetry.jsonl` and events to `events.jsonl` until the stop condition (UT limit, one team left, all dead, or competition ended)
   8. `bda.stop_competition` → log path
   9. `bda.get_scores full`
   10. copy the BDArmory log
   11. extract `KSP.log` lines for the run window: exceptions, `[ERR`, `NullReferenceException`
4. **Restore settings** and remove vessels.
5. **Summarise.** Write the per-cell metrics table, evaluate assertions, write `summary.md` and `summary.json`, and exit non-zero on failure, which suits CI.

Output folder: `runs/<timestamp>-<name>/{scenario.yaml, meta.json, cells/<cell>/<rep>/{telemetry.jsonl, events.jsonl, scores.json, bda.log, ksp_errors.log}, summary.md}`.

The runner never needs the LLM. An AI uses it through `bda_run_battle` (a long operation) and then reads `summary.md` or `summary.json`, which is a token-efficient way to work.

### 8.3 Metrics for the user's validation targets

| Change under test | Metric (computed from streams) | Source |
|---|---|---|
| Multi-target turrets (`multiTargetNum` up to 1024) | Distinct `visualTargetVessel` values across a WM's turrets per sample; size of `targetsAssigned`; time to the first secondary engagement | `ModuleWeapon.visualTargetVessel`, `MissileFire.targetsAssigned` (`:422`) |
| Multi-target missiles (`multiMissileTgtNum`, `maxMissilesOnTarget` up to 128, retargeting) | Missiles in flight grouped by (source WM, target): max and p95 per target compared with the setting (**assert ≤**); distinct targets with missiles in flight; retarget count (target changes on a missile in flight); saturation set size | `BDATargetManager.FiredMissiles`, `GetMissilesAway` (`:2275`), `missileSaturatedTargets` (private, `:425`, read by reflection in the shim or exposed by the PR) |
| Parallel fire (being added) | Launches per fixed-step window per vessel; salvo spread (Δt between launches in a salvo); peak simultaneous launches; guns firing concurrently | `bda.missile_launched` events with UT and fixed-frame index. Field names are TBD, and the metric plugin is keyed by configuration. |
| HullBreach (patched) | Per-vessel water tonnes, capacity and inflow over time; time to founder; leaking and critical counts | `HullBreachVesselModule` properties (`:32-40`) through a Tier-2 provider in the user's patched source, or a Tier-1 property annotation |
| MNWS / NAS | Hits and kills by weapon name (scores keyed by weapon); torpedo (SLW) and ASW events; submarine depth (`vessel.altitude < 0`) | BDArmory scores and events; NAS `AntiSubmarineWeapon.dll` module fields through Tier 0 |
| Performance regression | Frame time p50 and p99 (`Time.unscaledDeltaTime`), physics steps per frame, GC collection count, host main-thread ms | `host.stats`, Unity `GC.CollectionCount(0)` |
| Stability | Exceptions per minute in `KSP.log` during the run | log ring buffer |

### 8.4 A/B comparisons
- Swapping BDArmory builds needs a KSP restart.
- `skizzy battle compare runs/A runs/B` aligns matrix cells and reports, for each metric, the delta with a bootstrap confidence interval. The build is identified by the label plus the auto-captured `BDArmory.dll` SHA-256.
- Restarting KSP automatically (process control) would be an M6+ opt-in, because it gives the bridge `unsafe`-like power over the user's machine (open question Q6).

### 8.5 Statistics and determinism
- KSP physics, BDArmory randomness and frame timing are not deterministic, so the plan uses repeats (default 5) and fixed spawn geometry.
- Reports include mean ± 95% CI, win rates, and the distributions of the key metrics. Assertions that must *never* fail, such as the missile-per-target cap, are checked on every sample of every run.

---

## 9. Project layout and stack

### 9.1 Repositories
Start as **one monorepo** with five packages, for velocity while the spec is unstable. Once v1.0 ships and outside mods adopt it, split `spec` and `contract` into their own repo (stable, slow-moving).

```
D:\KerbalSpaceProgram\skizzy-protocol\          (future: github.com/Skizzy-create/skizzy-protocol)
├─ PLAN.md, PROTOCOL-SPEC-DRAFT.md, README.md, LICENSE-*  
├─ spec/                      # protocol text, JSON Schemas (envelope, catalog, errors), conformance suite
│  ├─ schemas/*.schema.json
│  └─ conformance/            # language-neutral test vectors + runner (TS)
├─ src/
│  ├─ Directory.Build.props   # KSPRoot, KSPManaged, LangVersion, net48, shared refs
│  ├─ Skizzy.Contract/        # net48, MIT. Attributes, interfaces, descriptors (Tier 3a)
│  ├─ Skizzy.Contract.Source/ # CC0 single files: SkizzyAttributes.cs (Tier 2), SkizzyLink.cs (Tier 3b)
│  ├─ Skizzy.Host/            # net48 KSP plugin. Server, dispatcher, catalog, reflector, permissions, UI
│  ├─ Skizzy.BDArmory/        # net48 shim. Refs BDArmory.dll (installed copy) + Skizzy.Contract
│  ├─ Skizzy.HullBreach/      # (M4) example Tier-2 provider, or a patch to the user's HullBreach source
│  └─ Skizzy.Host.Tests/      # net48 + NUnit for pure logic (JSON, schema gen, policy engine, WS framing)
├─ mcp/                       # TypeScript: skizzy-mcp (MCP bridge) + `skizzy` CLI (battle runner)
│  ├─ src/{protocol,catalog,mcp,battle,cli}/
│  └─ test/
├─ clients/python/            # (M6) skizzy-client: thin direct-socket client for notebooks/analytics
├─ annotations/               # Tier-1 .cfg packs shipped with the host (BDArmory settings, stock modules…)
├─ Distribution/GameData/Skizzy/{Plugins, PluginData(.gitkeep), Annotations, Skizzy.version}
└─ .github/workflows/         # build (net48 via reference assemblies), tests, conformance, packaging
```

### 9.2 Build tooling (matching BDArmory and the patched HullBreach)
- **SDK-style `.csproj` targeting `net48`**, built with `dotnet build -c Release`, like `_downloads/patched/HullBreach/src/HullBreach.csproj`. BDArmory itself uses an old-style csproj with `TargetFrameworkVersion v4.8` and HintPaths into `KSP_x64_Data\Managed`. We use the same references:

  ```xml
  <KSPRoot Condition="'$(KSPRoot)' == ''">D:\Epic Games\KerbalSpaceProgram\English</KSPRoot>
  <KSPManaged>$(KSPRoot)\KSP_x64_Data\Managed</KSPManaged>
  <Reference Include="Assembly-CSharp"><HintPath>$(KSPManaged)\Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
  <!-- + Assembly-CSharp-firstpass, UnityEngine, UnityEngine.CoreModule, IMGUIModule, PhysicsModule, … -->
  ```

- **`Microsoft.NETFramework.ReferenceAssemblies`** NuGet, so net48 builds without a targeting pack (CI on Linux works, as BDArmory's `Notes.md` does with `FrameWorkPathOverride`).
- **Language version.** `LangVersion` 12 is fine as long as features need no newer runtime types. Polyfill `IsExternalInit` if `init` is used. `ValueTuple` is fine, since BDArmory relies on it heavily.
- **CI** cannot ship KSP DLLs (licence). Either use a private reference-assembly package generated with stub tooling (for example, JetBrains Refasmer over the user's local DLLs, kept out of the public repo), or build in CI only `Skizzy.Contract`, the TypeScript bridge and the pure-logic tests, and do full plugin builds locally.
- **Deploy.** Reuse BDArmory's `_LocalDev/ksp_dir.txt` convention (`D:\KerbalSpaceProgram\_LocalDev\`). A post-build target copies `Distribution/GameData/Skizzy` to each listed KSP directory **only when `-p:Deploy=true`**. Nothing is installed by default.
- **Debugging** follows BDArmory's `Notes.md`: a Unity 2019.4.18f1 development player and `pdb2mdb` where needed.

### 9.3 Host dependencies (and why there is no Newtonsoft)
- KSP loads every DLL into one AppDomain, first assembly name wins. Mods shipping different `Newtonsoft.Json` versions is a known source of breakage, and `Managed/` has none.
- **JSON:** vendor a small MIT single-file JSON DOM, parser and writer as *source*, compiled `internal` into `Skizzy.Host` (or write about 600 lines of our own), with streaming writers for snapshots.
- **WebSocket:** a minimal RFC 6455 server over `TcpListener`:
  - handshake with SHA-1 and Base64, both in `mscorlib`
  - text frames, ping/pong, close and fragmentation
  - no compression
  - about 400 lines

  Alternative: vendor websocket-sharp (MIT) as internal source. Decide in the M0 spike; `HttpListener.AcceptWebSocketAsync` support on Unity's Mono is uncertain (risk R5).
- **No Harmony** in the host. Harmony is available (`000_Harmony`) for *adapters* that need to hook events BDArmory lacks, such as death, until the upstream PR lands.

### 9.4 MCP bridge stack
- **Node 22 LTS, TypeScript 5.x**, using `@modelcontextprotocol/server` **2.x**: the official v2 SDK, which implements 2026-07-28 and negotiates older handshakes.
- `ws` for the WebSocket client, `zod` for internal validation, and `ajv` (2020-12) to validate host schemas once at catalog load.
- `yaml` and `commander` for the battle CLI.
- Distributed on npm as `skizzy-mcp`, run with `npx -y skizzy-mcp`, and also bundled as a single-file executable with Node SEA for users without Node.
- **Why TypeScript over Python or C#:**
  - It is the reference MCP SDK and the first to track spec changes (the 2026 rewrite proved that matters).
  - The tool lists are dynamic, and JSON Schema is native.
  - `npx` makes it a one-line install.
- **Alternatives.** Python `mcp` 2.3 is a fine choice if the user prefers Python for analysis; the battle runner's outputs are plain JSONL and CSV either way. A C# bridge using the `ModelContextProtocol` NuGet package would share a language with the host but nothing else, because the host cannot use `System.Text.Json`.

### 9.5 Packaging
- **GameData layout:**

  ```
  GameData/Skizzy/
    Plugins/
      Skizzy.Contract.dll
      Skizzy.Host.dll
    Annotations/*.cfg
    PluginData/
    Skizzy.version
    LICENSE
  ```

  The shim ships separately as `GameData/SkizzyBDArmory/Plugins/Skizzy.BDArmory.dll`.
- **KSP-AVC `.version` files**, set to KSP 1.12.x.
- **CKAN.** Submit `.netkan` files to `KSP-CKAN/NetKAN` with `$kref: "#/ckan/spacedock/<id>"` or GitHub releases.
  - `Skizzy`: `depends` none; `recommends` ToolbarController and ClickThroughBlocker.
  - `SkizzyBDArmory`: `depends` Skizzy and BDArmoryPlus (`>= 1.13`).
  - Mods with Tier 3a: `depends: Skizzy`. Mods with Tier 2 or 3b: `suggests: Skizzy`.
- **SpaceDock** pages for both, with an "Uplink-ready" (or "skizzy-ready") badge for implementing mods.
- **The bridge** is published to npm, and GitHub releases carry SEA binaries.

### 9.6 Licensing suggestions

| Artifact | Licence | Why |
|---|---|---|
| Spec text | CC-BY 4.0 | Anyone can reimplement it |
| `Skizzy.Contract.dll` | MIT | Linkable from GPL, CC-BY-SA and proprietary-ish mods alike |
| `SkizzyAttributes.cs`, `SkizzyLink.cs` | **CC0** | Copied into other mods' source; must impose nothing |
| `Skizzy.Host` | MIT (or MPL-2.0 if you want changes to the host shared back) | Permissive, so it can be embedded or forked |
| `Skizzy.BDArmory` shim | CC-BY-SA 4.0 (compatible with BDArmory's CC-BY-SA 2.0 terms), or dual-licensed | Links BDArmory; avoids share-alike ambiguity |
| `skizzy-mcp` | MIT | npm ecosystem norm |
| Upstream BDArmory PR | BDArmory's CC-BY-SA 2.0 | Contributor terms of that repo |

HullBreach is CC BY-NC-SA 3.0 and NAS/ASW is CC BY-SA 4.0. Any Tier-2 provider added *inside* those mods inherits their licence, which is fine because the attribute file is CC0.

---

## 10. Phased roadmap

Effort estimates assume one developer working with coding agents, part-time. Each milestone ends with a demo and its acceptance tests passing.

### M0: Spikes and skeleton (1–2 weeks)
**Scope:**
- monorepo, `Directory.Build.props`, an empty host that loads in KSP
- loopback WebSocket with token, Origin and Host checks
- dispatcher with budgets
- JSON library choice
- `session.hello`, `host.stats`, `ksp.vessels.list`
- Python and Node smoke clients

**Spikes that answer API questions:**
- (a) the exact `BaseField` write and callback path that mirrors the PAW, and `UI_Control` cast quirks (BDArmory's TODO mentions random `InvalidCastException`s on `uiControlFlight`)
- (b) how stable `persistentId` is when duplicate craft spawn
- (c) the WebSocket approach on Unity's Mono
- (d) whether `VesselModule.Fields` exists per type
- (e) whether BDArmory spawning works when there is no active vessel (`VesselSpawner.SpawnSpawnProbe`, `VesselSpawner.cs:47`)

**Acceptance:**
- A round trip `hello` → `vessels.list` in the Flight scene takes less than 20 ms p50.
- With no client connected, frame-time overhead is under 0.1 ms; with one idle client, under 0.2 ms.
- Origin-bearing and token-less connections are rejected (automated test).
- The host survives five scene switches without leaks.

**Risks:** R3, R5 and R7 (§11).

### M1: MVP (2–3 weeks): read state, spawn, start a competition, read results
**Scope:**
- **Host:**
  - permission engine (file config, default deny, Test Director preset)
  - minimal in-game prompt for `ask`, kill switch, audit JSONL
  - log ring buffer
  - operations (`op.*`)
  - stock read provider: `ksp.mods.list`, `ksp.vessels.list`, `ksp.vessel.get`, `ksp.module.describe`, `ksp.module.get`
- **BDArmory shim:**
  - `bda.status`, `list_craft`, `stage_craft`
  - `spawn_vessels` (circular), `spawn_template`, `remove_vessels`
  - `list_teams`, `set_team`, `set_guard_mode`
  - `start_competition`, `stop_competition`, `get_competition_state`, `get_scores`, `tail_log`
- **Bridge:** stdio, dynamic tool projection, name mapping, `skizzy_wait` and `skizzy_cancel`, structured output, the `core` and `bda` toolsets.

**Acceptance (scripted end to end, also run by hand from Claude Code):**
1. With two test craft in `AutoSpawn/mvp/A` and `AutoSpawn/mvp/B`, the prompt "spawn mvp on water at lat -0.15 lon -74.2, start FFA, tell me who won" works:
   - The AI calls `bda_spawn_vessels`, `bda_set_guard_mode`, `bda_start_competition` and `skizzy_wait`, then finishes with `bda_get_scores`.
   - The scores JSON matches `GameData/BDArmory/Logs/<id>.log` (HPLEFT, ACCURACY and kill lines).
2. With BDArmory `cheat` denied, `bda_spawn_vessels` is not listed. A raw-socket call returns `PERMISSION_DENIED`, and the audit log records it.
3. The kill switch cancels an in-flight spawn operation, and further control calls are denied.
4. A scene change during a competition returns `UNAVAILABLE_IN_SCENE` and does not crash the game.

**Risks:**
- BDArmory spawner coroutines fail silently (always read `spawnFailureReason`).
- Name deconfliction surprises.
- The MCP client's handling of long-running tools (mitigated with `waitMs` and `skizzy_wait`).

### M2: Universal layer and subscriptions (3 weeks)
**Scope:**
- Full Tier 0: set, invoke, action, action groups, resources, `ksp.query` selector, `ksp.config.get`.
- `stream.subscribe` with deltas and dead-bands; the `events.*` ring buffer (2048 entries, cursor, `resync_required`).
- The Tier-1 annotation loader, with a starter pack for stock destructive events and BDArmory settings.
- The full in-game permissions UI.

**Acceptance:**
- **No HullBreach code changes needed:** read `ModuleHullBreach.floodedDisplay`, set `crushDepth` within its `UI_FloatRange` 0…600, and get rejected outside it with `INVALID_PARAMS {min, max}`. The PAW reflects the change, and symmetry counterparts update.
- Invoking a `Decouple` event is classified `destructive` by heuristic.
- **Performance:** a subscription of 500 parts × 5 fields at 5 Hz costs less than 1 ms per `FixedUpdate` (measured by `host.stats`), with zero GC allocation per sample in steady state (Unity profiler).

**Risks:**
- Callback semantics differ between mods (R3).
- GC (R8).
- Localization edge cases.

### M3: Complete BDArmory adapter and battle harness v1 (3–4 weeks)
**Scope:**
- The rest of §7.1: weapon manager, AI, radar, missiles, damage, settings, tournament, fire tools, events and streams.
- `skizzy battle run` and `compare`, plus `bda_run_battle`.
- Multi-target metrics and assertions.

**Acceptance:**
- The matrix `multiTargetNum` {1, 8, 64} × `maxMissilesOnTarget` {1, 4} × 3 repeats runs **unattended**.
  - The report shows the per-target missile concurrency assertion holding on every sample.
  - Distinct turret targets are greater than 1 when `mt` > 1.
  - It captures exceptions.
- **Resilience:** the run survives a vessel exploding during spawn (the cell is retried once, then marked failed).
- `configure_weapon_manager` sets `multiTargetNum = 1024` and reads it back.
- After the other agent's parallel-fire fields land, they appear in `bda.get_weapon_manager` **with no shim rebuild**.

**Risks:**
- BDArmory churn on the other agent's branch (R10).
- Performance at 1024 targets (R8).
- Reflection on private members breaking (R9).

### M4: Public protocol draft v0.5 and the first external implementers (3 weeks)
**Scope:**
- `Skizzy.Contract.dll`, `SkizzyAttributes.cs` and `SkizzyLink.cs`.
- Tier 2 and Tier 3 scanners and the JSON-delegate ABI.
- A conformance suite.
- A **HullBreach Tier-2 provider** in the user's patched HullBreach source (`hullbreach.get_flood_state`, `hullbreach.set_pumps`).
- A NAS ASW annotation pack.

**Acceptance:**
- HullBreach exposes tools **without referencing any Skizzy assembly** and loads cleanly when Skizzy is absent (verified by removing `GameData/Skizzy`).
- The conformance suite passes for the host plus two providers.
- An outside reader can implement a provider from the spec alone (dry-run review).

**Risks:**
- Attribute duck-typing edge cases (generic methods, overloads): forbid them in the spec.
- Contract versioning mistakes: keep `AssemblyVersion` fixed.

### M5: Upstream BDArmory PR (2–3 weeks, plus review time)
**Scope:**
- In-tree Tier-2 provider.
- Public API additions (§7.3).
- The shim's authority handoff.

**Acceptance:**
- The catalog is identical whether it comes from the shim or the PR (diff test).
- No measurable cost when no host is present (profiler: 0 allocations, no reflection at runtime).
- Maintainer review feedback is addressed.

**Risks:**
- Maintainer appetite. Mitigate by discussing first and keeping the PR small.
- Merge timing against the multi-target branch.

### M6: Hardening and public release (3–4 weeks)
**Scope:**
- CKAN and SpaceDock packaging, docs site.
- Streamable HTTP transport.
- MCP Tasks extension, MRTR elicitation confirmations.
- **Lockstep mode:** `sim.hold`, `sim.step(n)`, `sim.release`, inspired by kRPC 0.7 tick-hold and ASTRA's pause-on-return, for deterministic agent-driven experiments.
- Python client.
- Gated deep reflection.
- Optional scene and save control (`ksp.scene.load_save`, `ksp.scene.goto_flight`).

**Acceptance:**
- A fresh install through CKAN on a clean KSP works.
- **Security checklist:**
  - A browser test page cannot connect (Origin check).
  - A DNS-rebinding test is refused (Host check).
  - Path traversal (`..\..\`) is rejected.
  - A fuzzed JSON corpus (10k cases) causes no host exception.
  - The token is not readable through any API.

**Risks:**
- MCP client feature fragmentation: some clients lack Tasks or elicitation, so keep the fallbacks.
- Antivirus flags on SEA binaries.

### M7 and later: optional extras
- A **kRPC backend in the bridge** for stock flight tools (c1).
- Adapters for:
  - **PhysicsRangeExtender** ranges, which are essential for long-range naval battles; BDArmory already integrates with it in `ModIntegration/PhysicsRangeExtender.cs`
  - VesselMover
  - KerbalEngineer readouts
  - Trajectories
  - AtmosphereAutopilot
- Camera control and screenshots for an AI "battle commentator".
- A web dashboard as a direct client.

---

## 11. Risk register

| # | Risk | Likelihood / impact | Mitigation |
|---|---|---|---|
| R1 | **Unity threading violations** (touching game objects off the main thread causes random crashes) | M / H | Single dispatcher. Code review rule: only `any`/`snapshot` code runs off-thread. Debug-build assertions that check `Thread.CurrentThread == mainThread` in every game-touching accessor. |
| R2 | **Frame-time impact** of reflection, snapshots and JSON | M / M | Budgets, adaptive rate, compiled accessors, pooled buffers, snapshot affinity, `host.stats` |
| R3 | **KSP API quirks**: PAW callback path, `UI_Control` cast exceptions, scene-specific controls, events that need parameters | H / M | M0 spike. Per-module fallbacks. Tier-1 overrides. Read back after every write. |
| R4 | **Security**: a browser or local process abuses the socket; prompt injection | M / H | Loopback only, token, Origin and Host checks, host-side permissions, no network path to change permissions, sandboxed paths, audit, kill switch |
| R5 | **WebSocket on Mono** (`HttpListener` WS support uncertain) | M / L | Own minimal RFC 6455 implementation over `TcpListener` (well understood) |
| R6 | **DLL conflicts** (JSON libraries, duplicate contract DLL) | M / H | Vendor as internal source; contract shipped only by the host; fixed `AssemblyVersion` |
| R7 | **Scene transitions and stale handles** | H / M | Invalidation on `onGameSceneLoadRequested`, the class-name guard in module handles, `NOT_FOUND {stale}` |
| R8 | **Performance at 1024 targets** and large fleets; GC spikes | M / H | Round-robin snapshot builders, no LINQ, measured in M3; the harness records frame-time regressions |
| R9 | **BDArmory internals change** (private members used by reflection) | H / M | Resolve members once with per-tool degradation; move to public APIs through the PR (M5) |
| R10 | **Concurrent edits** by the other agent on the BDArmory branch (parallel-fire field names unknown) | H / L | Build against the installed DLL; auto-discover fields; only annotations need updating |
| R11 | **MCP spec and SDK churn** (2026-07-28 was a breaking redesign) | M / M | Keep all MCP code in the bridge; SDK negotiates older versions; the conformance suite pins behaviour |
| R12 | **LLM misuse**: wrong units, runaway loops, oversized results | M / M | Rich descriptions, units, schemas with ranges, rate limits, pagination and summaries (`format: summary`), `skizzy_wait` instead of polling |
| R13 | **BDArmory upstream declines the PR** | M / L | The shim remains fully functional |
| R14 | **Licence ambiguity** (CC-BY-SA BDArmory, GPL kRPC) | L / M | Separate shim with a compatible licence; no kRPC code; CC0 attribute file |
| R15 | **Non-deterministic battles** make A/B tests noisy | H / M | Repeats, CIs, fixed geometry, invariant assertions checked on every sample |

---

## 12. Prior art: what we reuse and why we diverge

Researched 2026-11-07. Repository facts come from GitHub; details are in the session's research notes.

| Project | What it is | Reuse | Diverge because |
|---|---|---|---|
| **caseys/ksp-mcp** (TypeScript, about 32 tools, kOS telnet plus MechJeb, MIT, early development) | MCP through kOS terminal scraping | One library behind both the CLI and MCP with a parity check (we do the same for `skizzy battle` and the MCP tools). Sentinel-based completion detection. | Screen-scraping; coarse maneuver macros; dev-build dependencies; no permissions; no combat |
| **codecraft3r/ksp-mcp** (C# / .NET 10 with the C# MCP SDK, 12 tools, kOS plus craft-file generation) | Craft generation and kOS scripts | Craft-file parsing ideas for `bda.list_craft` validation | Windows-path hard-coding; kOS pauses with the game; tiny scope |
| **krpc-mcp** (MaiaJP-AIXplain; Python, 28 curated tools or the full raw kRPC API) | Thin wrapper over kRPC | The **curated vs full** toolset split (our toolsets); handles passed as arguments | kRPC-bound, so no BDArmory, no permissions, stale docs |
| **astra-ksp** (shoal-rat; Python, 74 tools, kRPC 0.5.4 plus a custom HTTP plugin) | The most complete agent-flight system | **Pause-on-return and reflexes** (our lockstep mode); **safety interlocks that report but do not act**; `compute_*` tools that show their maths; JSONL call log; checkpoint and restore | Two connections into the game; pinned to old kRPC; no permission model; flight-focused |
| **lhervier/KSP-Util-MCPServer** (C#, MCP *inside* KSP, about 70 tools) | In-process MCP with generic reflection tools | **Duck-typed attribute discovery by name** (our Tier 2); generic `get_member`/`set_member`/`call_method` (our gated deep reflection); one mutating call at a time with concurrent reads; cancellation and watchdog | MCP in-process (spec churn, DLL risk); no auth |
| **ziksjksk/kerbal-space-program-mcp** (C# plugin, HTTP bridge, Python stdio MCP) | Token-optional HTTP bridge | `confirm=true` gates; **2048-entry event ring buffer with cursor and `resync_required`**; separate observation and command queues; per-frame budgets; `structuredContent` | Optional token; coarse tools |
| **NodeNestor/ClaudePilot**, **L4GN/KerbalMCP** | In-game copilot with 80+ tools; MechJeb through reflection | Main-thread marshalling pattern; reaching mods by reflection | No permission model |
| **IshiakiZ/ksp-ai-bridge** | Loopback plugin plus a zero-dependency MCP server | **Token rotated per game start** (we offer regenerate-on-start as an option); **"Pause AI" and "Take controls" buttons** (our kill switch) | Limited scope |
| **kRPC 0.7.0** (2026-10-21; LGPLv3 core, GPLv3 SpaceCenter; RPC on 50000 and streams on 50001) | The canonical RPC mod | Threading and budget model (`MaxTimePerUpdate`, adaptive rate); stream-once-per-`FixedUpdate` with only changes sent; yield continuations; **tick-hold** (0.7); handles | Static compile-time services; no auth or permissions; protocol breaks in 0.7 and again in 0.8; mods must hard-reference it; GPL questions; see §3.1 |
| **Telemachus Reborn** (HTTP and WebSocket on 8085, bound to 0.0.0.0, no auth) | Telemetry datalink | WebSocket subscribe/unsubscribe-with-rate ergonomics | Stringly typed; insecure defaults |
| **kOS** (GPLv3; telnet on 5410) | In-game scripting | — | Not a structured API |
| **BDArmory RemoteOrchestration** (in-tree, deprecated) | HTTPS polling of a remote score server for tournaments | Spawn strategies and per-heat score models (concepts) | Pull-based, remote, deprecated |

The common gaps in all of the above are no permission model, no BDArmory support, and no zero-dependency standard for mods. Those gaps are exactly the niche this project fills.

---

## 13. Open questions for the user

1. **Name.** Adopt *Kerbal Uplink* publicly at v1.0, or stay with *skizzy-protocol*?
2. **Bridge language.** Is TypeScript acceptable, or would you rather have Python, which also suits analysing battle results?
3. **Manual weapon fire.** Should the AI be allowed to fire weapons directly (`bda.fire_*`), or only enable guard mode and let BDArmory's AI fight? The default is `ask`.
4. **Parallel-fire fields.** What are the final field names and semantics for the parallel-fire settings the other agent is adding? This affects the descriptions and harness metrics, though not the plumbing.
5. **Naval mods.** HullBreach, MNWS and NAS are in `_downloads\patched\` but **not installed** in GameData. Install them before M3? We have not installed anything.
6. **KSP process control.** Should the harness ever restart KSP or swap BDArmory builds automatically for A/B runs? That is machine-level power, opt-in only.
7. **Test site and save.** Should there be a dedicated save (for example `saves/naval-range`) with a parked vessel at a water site, for unattended runs? M0 checks whether BDArmory can spawn with no active vessel.
8. **Upstream.** Is there a relationship with the BDArmory maintainers? Should the M5 PR be pitched early (before M3) to get API feedback?
9. **Denied tools.** Hide denied tools from the AI (the default) or show them marked "[needs permission]"?
10. **Multiple clients.** Should a human be able to fly while an AI observes and controls other vessels at the same time? The design supports it, but the policy defaults matter.
11. **Licences.** Is MIT acceptable for the host, contract and bridge, with CC0 for the copied files?

---

## Appendix A: verified BDArmory API anchors (branch `multitarget-1.13.1`, HEAD `be734f0bb`)

| Symbol | Location |
|---|---|
| `MissileFire.multiTargetNum` / `multiMissileTgtNum` / `maxMissilesOnTarget`, consts 1024 / 128 | `Control/MissileFire.cs:680-694` |
| `MissileFire.ToggleGuardMode()`, `guardMode` | `:879`, `:1030` |
| `MissileFire.SetTeam(BDTeam)`, `SetTeamByName` (empty stub), `OnChangeTeam` | `:1161`, `:1194`, `:1159` |
| `MissileFire.FireCurrentMissile(...)` (private) | `:4570` |
| `MissileFire.GetMissilesAway(TargetInfo)` | `:2275` |
| `MissileFire.targetsAssigned` / `missilesAssigned` / `missileSaturatedTargets` (private) | `:422-425` |
| Countermeasures `FireFlares` … `FireBubbles` | `:4242-4318` |
| `BDACompetitionMode.StartCompetitionMode` / `StartCompetitionNow` / `StopCompetition` / `ResetCompetitionStuff` | `Competition/BDACompetitionMode.cs:352`, `:338`, `:392`, `:454` |
| `CompetitionScores.ScoreData`, `ScoringData`, results log writer | `Competition/Scoring.cs:23-30`, `:949-1052`, `:934-945` |
| `CircularSpawning.SpawnAllVesselsOnce`, `CustomTemplateSpawning.SpawnCustomTemplate`, `SingleVesselSpawning.SpawnVessel` | `VesselSpawning/CircularSpawning.cs:98-114`, `CustomSpawnTemplate.cs:95`, `SingleVesselSpawning.cs:57` |
| `VesselSpawnerStatus` (spawning, success, failure reason) | `VesselSpawning/VesselSpawnerBase.cs:20-48` |
| `BDATargetManager.TargetList`, `FiredMissiles`, `TargetPriorityScore` | `UI/BDATargetManager.cs:1356`, `:30`, `:1602` |
| `VesselRadarData.GetLockedTargets`, `displayedTargets` (private), `TryLockTarget` | `Radar/VesselRadarData.cs:158`, `:141`, `:964` |
| `BDModuleSurfaceAI.SurfaceTypeName` (includes Submarine), `CombatAltitude` | `Control/BDModuleSurfaceAI.cs:76-91` |
| `BDGenericAIBase` commands and activation | `Control/BDGenericAIBase.cs:213-540` |
| `HitpointTracker.Hitpoints`, `GetMaxHitpoints` | `Damage/HitpointTracker.cs:27-29`, `:1072` |
| `BDArmorySettings` persistent statics, `SaveConfig` | `Settings/BDArmorySettings.cs`, `UI/BDArmorySetup.cs:961` |
| `BDATournament.SetupTournament` / `RunTournament` / `StopTournament` | `Competition/BDATournament.cs:1687-1735` |
