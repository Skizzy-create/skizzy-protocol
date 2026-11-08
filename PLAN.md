# skizzy-protocol: AI control for Kerbal Space Program 1.12.5

**Implementation plan, revision 0.2.** It gives an AI agent (Claude Code first) full, permissioned access to KSP and its mods: building craft, flying them, firing weapons, and seeing the result. BDArmory gets first-class support, and the plan defines an open standard any KSP mod can implement to become AI-controllable. The quality bar is that it must never destabilise a heavily modded game (§3.7).

| | |
|---|---|
| Status | Draft v0.2, 2026-11-08. Revises v0.1 (2026-11-07) for the user decisions listed in §0. |
| Owner | Skizzy-create |
| Companion document | [`PROTOCOL-SPEC-DRAFT.md`](PROTOCOL-SPEC-DRAFT.md) (wire format and contract API), draft 0.2 |
| Target | KSP 1.12.5 (build 03190, Epic), BDArmory Plus 1.13.1.x, Windows 11. In-game code: .NET Framework 4.8 on Unity 2019.4 Mono. Bridge: .NET 10 LTS. |

**What changed in v0.2:**
- The MCP bridge is now **C#**, built on the official ModelContextProtocol C# SDK. The TypeScript, Node and npm stack is gone.
- The host and the bridge now share a contract project, `Skizzy.Protocol`.
- Three new capability areas: craft building, flight control and vision (§8).
- Robustness principles are now hard requirements (§3.7).
- Every capability area has its own enable switch, off by default.
- Revised roadmap (§11). Vision moves from M6 to M2.
- Accepted defaults: denied tools are hidden; direct weapon fire is allowed but `destructive` with policy `ask`; the harness never restarts KSP; MIT and CC0 licences.

---

## 0. Decisions at a glance

| Topic | Decision |
|---|---|
| Architecture | **Option (b)** with a narrow hybrid escape hatch: our own in-game host plugin, plus an external MCP bridge. kRPC is an *optional, later* extra backend for stock flight control. It is not the foundation. (§3) |
| Wire protocol | JSON-RPC 2.0 over WebSocket on `127.0.0.1:47549`, bearer token, Origin and Host checks. Method names are brand-free, so the protocol name is cosmetic. (§5, spec) |
| Threading | Network I/O and JSON run on background threads. A main-thread dispatcher drains a queue under a per-frame time budget: in `Update`, in `FixedUpdate`, and in `Update` while paused. Telemetry is served from snapshots taken each `FixedUpdate`. Long operations return operation handles. With no client connected the dispatcher is disabled and costs nothing. (§3.4, §3.7) |
| Robustness | Hard requirements, each with a verification method (§3.7): per-area enable switches (off by default), fail-closed tools, zero idle cost, main-thread-only Unity access within a budget, no exceptions escaping into KSP (with auto-disable of faulting tools), resource caps, no LINQ in hot paths, and graceful scene changes. |
| Universal access | **Tier 0:** reflection over every `PartModule`/`VesselModule` (`BaseFields`, `BaseEvents`, `BaseActions`, `UI_Control` ranges, resources, action groups, part trees, MM config). **Tier 1:** config annotations that can be patched with ModuleManager. **Unsafe tier:** deep reflection, off by default. (§4) |
| Opt-in protocol | **Tier 2:** duck-typed attributes copied as source, with *zero* binary dependency. **Tier 3:** a contract DLL, or a JSON-delegate ABI found through reflection. (§5) |
| Permissions | Levels `read`, `control`, `destructive`, `cheat` and `unsafe`, set per mod and per capability as allow, ask or deny. Default is deny except stock `read`. Enforced *in the host*, so it also applies to clients that bypass MCP. In-game UI, a ConfigNode file, a JSONL audit log and a kill switch. **Denied tools are hidden** from MCP clients. (§6) |
| Capability areas | `ksp.read` (stock reads, **on** by default), `ksp.module` (Tier-0 writes), `bdarmory`, `ksp.flight`, `ksp.camera`, and `ksp.editor`/`craft`. Each has its own switch and is **off by default**. A disabled area registers nothing, hooks nothing and costs nothing. (§3.7) |
| BDArmory | Phase 1 is a separate shim, `Skizzy.BDArmory.dll`, about 30 tools. Phase 2 is an upstream PR that adds Tier-2 attributes to BDArmory with no new dependency, plus a handful of small public-API additions. The shim steps aside when it detects the in-tree provider. Direct weapon fire (`bda.fire_*`) is allowed but is `destructive`, so the default policy is **ask**. (§7) |
| Craft building | `ksp.editor.*` drives the VAB/SPH through `EditorLogic` and `ShipConstruction`. Headless `craft.*` reads and writes `.craft` ConfigNode files without the editor, and validates them by loading them back. (§8.1) |
| Flight control | `ksp.flight.*` covers throttle, SAS, RCS, action groups, staging, gear, brakes and lights. **Setpoint autopilots run in game at physics rate** through `Vessel.OnFlyByWire`. The AI only sets targets, so there is no 50 Hz network loop. It also covers hand-off to and from BDArmory AI and AtmosphereAutopilot. The kill switch always restores manual control. (§8.2) |
| Vision | `ksp.camera.*` returns screenshots as MCP image content, captured at end of frame on the main thread, with capped size and rate, plus camera modes and targeting. Moved from M6 to **M2**. (§8.3) |
| Shared contract | `Skizzy.Protocol`, multi-targeted to **net48 and netstandard2.0**. It holds wire constants, error kinds, a dependency-free JSON DOM, a JSON Schema builder and validator, the permission model and evaluator, and tool-name mapping. The host (net48) and the bridge (net10) compile against the same source. (§10.1) |
| MCP bridge | **C# on .NET 10 LTS**, using the official **ModelContextProtocol C# SDK, pinned to exactly 2.3.0** (§10.4 gives the reasons). It speaks MCP **2026-07-28** and negotiates down to 2025-11-25. stdio only in v1. Ships as a **self-contained single-file exe** (`skizzy-mcp.exe`); no Node or npm. The battle runner comes later as a subcommand of the same exe. (§5.6, §10.4) |
| Process control | The harness **never** restarts KSP or swaps DLLs. A/B runs across BDArmory builds need a manual restart. (§9.4) |
| Name | **skizzy-protocol**, decided 2026-11-08. *Kerbal Uplink* stays on file as an alternative public name. (§2) |
| Licences | **MIT** for all our code: host, protocol, shim and bridge. **CC0** for the copy-in attribute file and its example. CC-BY 4.0 for the spec text. (§10.6) |
| MVP (M1) | Read state, spawn fleets, set teams and guard mode, configure weapon managers (multi-target and parallel fire), start and stop a competition, read scores and logs, all through Claude Code with permissions, hiding of denied tools, and audit working. (§11, M1) |

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

6. The AI can **build** craft (VAB/SPH or headless `.craft` files), **fly** them through in-game setpoint autopilots, and **see** the result through screenshots (§8).

### 1.2 Non-goals for v1
- Remote or LAN access. Loopback only; use an SSH tunnel if needed.
- Closing a control loop over the network. The AI never hand-flies at 50 Hz across MCP. It sets *targets*, such as a heading, an altitude or a depth, and in-game controllers run them at physics rate (§8.2). Replacing kOS or MechJeb is not a goal.
- Career or science automation.
- Restarting KSP, or swapping mod DLLs, from the harness (decided; §9.4).
- Deterministic physics replay. KSP and Unity physics are not deterministic, so testing is statistical (§9.5).

### 1.3 Environment facts this plan relies on (checked 2026-11-07, re-checked 2026-11-08)

| Fact | Where |
|---|---|
| KSP 1.12.5, build 03190 (2022-12-12), Epic distribution | `D:\Epic Games\KerbalSpaceProgram\English\buildID64.txt` |
| BDArmory **1.13.1.0**, `KSPAssembly("BDArmory",1,0)`, licence CC-BY-SA 2.0 | `Properties/AssemblyInfo.cs:20-31`, `Distribution/GameData/BDArmory/License.txt` |
| BDArmory source is on branch `multitarget-1.13.1`. HEAD was `be734f0bb` on 2026-11-07 and is `01d0c9676` on 2026-11-08. Parallel salvo landed in `4d14497f4`. Another agent commits there, so it is **read-only for us**. | `git log` (read-only) |
| `MissileFire.maxAllowableTargets = 1024`, `maxAllowableMissilesOnTarget = 128` | `Control/MissileFire.cs:680, 690` |
| **Parallel-fire fields** (`[KSPField]`, persistent): `parallelFire` (bool, `UI_Toggle`), `parallelSalvoSize` (float 1…128, step 1), `parallelSalvoStagger` (float 0…10 frames, step 1, `maxParallelSalvoStagger = 10`). The callback `OnParallelSalvoSettingsChanged(BaseField, object)` clamps them. All of these are present in the **installed** `BDArmory.dll` (built 2026-11-08). | `Control/MissileFire.cs:696-708, 1680`; DLL metadata |
| The `KSP_x64_Data\Managed` folder has **no** `Newtonsoft.Json`, `System.Net.Http`, `System.Runtime.Serialization` or `netstandard.dll`. It does have `mscorlib`, `System`, `System.Core`, `Mono.Security` and `UnityEngine.*`. So in-game assemblies must target **net48**, not netstandard. | directory listing |
| Installed and relevant: `000_Harmony`, `001_ToolbarControl`, `000_ClickThroughBlocker`, `ModuleManager.4.2.3`, `PhysicsRangeExtender`, `VesselMover`, `KerbalEngineer`, `Trajectories`, `AtmosphereAutopilot`, `KSPCommunityFixes`, `BDArmoryExtended`, `BDArmoryRefurbished`, `StockArmory`, `NKDR`, `KerbalField`, `KerbalPowers` | `GameData/` |
| **Now installed** (they were not on 2026-11-07): `HullBreach`, `NAS`, `Johnny` (MNWS), `IronSeasKANES`, `Maritime Pack`. **Not installed:** kRPC, kOS, MechJeb. | `GameData/` |
| The patched naval sources stay in `_downloads\patched\*`. HullBreach has full source (SDK-style net48 `.csproj`). NAS has `AntiSubmarineWeapon.dll` with source. MNWS is config only. | `_downloads/patched/*` |
| .NET SDKs 9.0.320 and **10.0.403** are installed. .NET 8 LTS reaches end of support on 2026-11-10, so the bridge targets **net10.0** (LTS until 2028-11). | `dotnet --list-sdks` |
| AtmosphereAutopilot exposes `AtmosphereAutopilot.AtmosphereAutopilot.Instance`, `getVesselModules(Vessel)`, and `TopModuleManager.activateAutopilot(Type)`. Its controllers (`StandardFlyByWire`, `CruiseController`) are public types. | DLL metadata |
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

**Decision (2026-11-08): skizzy-protocol.** The user accepted the default. The wire identifier, the `Skizzy.*` namespaces and the GameData folder `GameData/SkizzyProtocol/` all use it. *Kerbal Uplink* stays on file as a possible public name for v1.0. A rename would still cost close to nothing, because method names are brand-free.

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
3. We control versioning. Neither kRPC's 0.7 to 0.8 break nor MCP's 2026 break can strand the in-game plugin, because MCP churn is absorbed by the C# bridge. The bridge is a separate exe that can be replaced without touching GameData.
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
 │ skizzy-mcp.exe (.NET 10, C#, self-contained) │                     │
 │  ModelContextProtocol C# SDK 2.3.0 (stdio)  │                     │
 │  • catalog → MCP tools projection, hiding   │                     │
 │  • name mapping, structured + text results  │                     │
 │  • op waiting (skizzy_wait), progress       │                     │
 │  • later: `battle` subcommand, offline logs │                     │
 └──────────────┬──────────────────────────────┘                     │
                │ skizzy-protocol: JSON-RPC 2.0 / WebSocket          │
                │ ws://127.0.0.1:47549/skizzy  + Bearer token        │
 ═══════════════╪════════════════ KSP process boundary ══════════════╪═══════════
 ┌──────────────▼────────────────────────────────────────────────────▼──────────┐
 │ Skizzy.Host.dll  (KSPAddon Instantly, DontDestroyOnLoad)                     │
 │  Net threads: accept/read/write, JSON parse, auth, schema validation         │
 │  Permission engine ── Audit log (JSONL) ── In-game UI (IMGUI + AppLauncher)  │
 │  Main-thread Dispatcher (Update / FixedUpdate / paused) ── Snapshot publisher│
 │  Area switches: ksp.read | ksp.module | bdarmory | ksp.flight | ksp.camera | │
 │                 ksp.editor/craft   (disabled area = nothing registered)      │
 │  Catalog ◄── Tier0 Reflector  ◄── Tier1 Annotations (SKIZZY_ANNOTATIONS cfg) │
 │          ◄── Tier2 Duck-typed attribute scanner                              │
 │          ◄── Tier3 Provider registry (Contract DLL | JSON-delegate ABI)      │
 │  Built-in providers: ksp.* (stock), host.*, logs.*                           │
 ├──────────────────────────────────────────────────────────────────────────────┤
 │ Skizzy.Protocol.dll (net48 build; the bridge uses the netstandard2.0 build)  │
 │  JSON DOM · wire constants · errors · schema builder/validator · permissions │
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

**Exceptions** from mod code are caught on the main thread and returned as `PROVIDER_ERROR` with a sanitized message. They are also logged once per (capability, exception type) per minute, so a failing provider cannot spam `KSP.log`. Repeated faults auto-disable the capability (§3.7).

**Idle and wake-up.** The dispatcher MonoBehaviour disables itself (`enabled = false`) whenever there is no session, no queued job, no running operation and no pending confirmation. Unity then calls none of our `Update`, `FixedUpdate`, `LateUpdate` or `OnGUI` methods. A network thread cannot touch Unity, so it wakes the dispatcher through the main thread's `SynchronizationContext`: Unity 2019.4 installs `UnitySynchronizationContext`, which runs posted callbacks once per frame. A pre-allocated callback sets `enabled = true`, and an interlocked flag ensures at most one wake-up is posted per idle period. If no synchronization context exists, the host falls back to an always-enabled pump whose idle path is a single volatile read. The self-check reports which mode is active.

**Per-target permission resolution.** Some capabilities only learn their permission subject after resolving handles. For example, `ksp.module.set` on a BDArmory module needs the BDArmory grant, and `ksp.module.invoke` of a `Decouple` event is `destructive`. Such capabilities declare a resolver that runs on the main thread first. It returns `(mod, level, human summary)`, and the policy check then proceeds as usual. The resolver only resolves handles and has no side effects.

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

### 3.7 Robustness requirements (hard)

The user's game is heavily modded and long-running. Skizzy must never be the reason it stutters, leaks or crashes. Each requirement below is **normative for every milestone**. A change that violates one does not ship.

| # | Requirement | Mechanism | Verification |
|---|---|---|---|
| RB1 | **Each capability area has its own enable switch, off by default.** Areas: `ksp.read` (the only one on by default), `ksp.module` (Tier-0 writes), `bdarmory`, `ksp.flight`, `ksp.camera`, `ksp.editor`, `craft`. | `AREAS` node in `permissions.cfg`, plus toggles in the in-game window. A disabled area's provider is **not registered**: it has no catalog entries, no `GameEvents` subscriptions, no reflection scans and no per-frame hooks. Toggling an area emits `catalog.changed`. Area switches are independent of the permission policy; both must pass. | Unit test: a disabled area yields no catalog entries. In-game self-check: the log lists area states at start. |
| RB2 | **Fail closed per tool when a reflected API is missing.** | Providers resolve every reflected member (private methods, fields, types in other assemblies) **once** at registration, into cached delegates or `FieldInfo`s. A capability whose resolution failed stays in the catalog with `availability:{available:false, reason:"api_missing", detail}`. Calls return `PRECONDITION_FAILED {reason:"bda_api_changed"\|"api_missing"}` and never fall back to guessing. One missing member disables only the tools that need it. | Unit test with a fake provider whose member is missing. The shim's self-check logs every resolved member as OK or MISSING. |
| RB3 | **Zero per-frame work and zero allocations when no client is connected.** | The dispatcher disables itself when idle and is woken through `SynchronizationContext` (§3.4). The OnGUI component is enabled only while a window, a prompt or the kill-switch banner is visible. No `Application.logMessageReceived` hook: logs are read from disk on demand. Hotkeys are polled only while a session exists. Snapshots and streams are built only for live subscriptions. | Self-check logs the idle mode. M0 acceptance: under 0.1 ms per frame with no client, and 0 bytes/frame of GC alloc (Unity profiler). |
| RB4 | **Main-thread-only Unity access via the dispatcher, with a frame time budget.** | Network threads touch only immutable snapshots and host-owned data. Every game access is a `Job` drained in `Update`/`FixedUpdate` under an adaptive budget (default 2 ms, range 0.5–8 ms; §3.4), measured with `Stopwatch`. Results are converted to plain JSON trees on the main thread and serialized on writer threads. Debug builds assert `Thread.CurrentThread == mainThread` in game-touching helpers. | Unit test of the queue: budget respected, at least one job per drain, ordering. `host.stats` reports per-frame used time at p50 and p99, and the worst capabilities. |
| RB5 | **No exceptions escape into KSP.** Every handler is wrapped, and repeated faults auto-disable the tool. | Every Unity callback (`Update`, `FixedUpdate`, `LateUpdate`, `OnGUI`, `GameEvents` handlers, coroutine steps) and every capability handler runs inside `try/catch`. A **fault tracker** auto-disables a capability after 5 provider exceptions within 60 s. It then reports `availability:{available:false, reason:"auto_disabled"}`, writes one audit line and one log line, and can be re-enabled from the UI. Log output is throttled to once per (capability, exception type) per minute. A failing *provider registration* marks only that provider as `status:"error"`. | Unit tests: the fault tracker disables after N faults in the window, and a throwing handler returns `PROVIDER_ERROR` without leaking. |
| RB6 | **Resource caps on message size, rates and streams.** | Defaults: 4 MB per message, JSON depth 64, 4 connections, 32 in-flight calls per session, 512 queued jobs, 64 subscriptions, per-capability rate limits (token bucket) plus a per-session global cap of 600/min, a 16 MB outbound queue per connection (a slow client is disconnected rather than buffered), image capture of at most 1 per 2 s with a 2 MB cap, and HTTP upgrade headers of at most 8 KB. | Unit tests for the frame limits, JSON depth, rate limiter and backpressure (`BUSY`). |
| RB7 | **Hot paths without LINQ.** | No LINQ, `foreach` over interface-typed collections, lambdas that capture state, string concatenation or boxing in per-frame or per-sample code: the dispatcher drain, snapshot builders, op stepping and the fly-by-wire controllers. Cold paths (catalog build, describe, one-off tool calls) may allocate. | Code review rule, plus a CI grep that rejects `System.Linq` in files tagged `// skizzy:hot`. |
| RB8 | **Graceful handling of scene changes.** | On `onGameSceneLoadRequested`: state becomes `transitioning`; new control calls get `UNAVAILABLE_IN_SCENE {reason:"transition"}`; scene-bound operations are cancelled (`CANCELLED {reason:"scene_change"}`); all handles are invalidated; any in-game autopilot is released and manual control restored. On `onLevelWasLoadedGUIReady`: the state is rebuilt, scene-bound singletons (BDArmory instances) are re-acquired, and `catalog.changed` is emitted only if availability changed. The tool list stays stable across scenes (§3.5). | M0 acceptance: five scene switches with a connected client, with no exceptions and no growth in tracked objects. Covered by the self-check log. |

Two corollaries follow:
- **Default deny with defense in depth.** The area switch, the permission policy, the scene check, the preconditions and the per-capability rate limit must all pass. Any one of them failing refuses the call.
- **The kill switch is absolute.** It denies all non-read calls, cancels every operation, rejects pending confirmations, releases every autopilot hold and restores manual control. It can optionally pause the game.

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

M1 implements `ksp.mods.list`, `ksp.vessels.list`, `ksp.vessel.get`, `ksp.parts.find`, `ksp.module.describe`, `ksp.module.get` and `ksp.log.tail` (area `ksp.read`), plus `ksp.module.set`, `ksp.module.invoke` and `ksp.module.action` (area `ksp.module`, off by default). The rest of the table lands in M2.

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

Writing a raw field skips the callbacks mods depend on. For example:
- `BDModuleSurfaceAI` refreshes which fields are visible when `SurfaceTypeName` changes (`BDModuleSurfaceAI.cs:267-299`).
- `MissileFire.OnParallelSalvoSettingsChanged` clamps `parallelSalvoSize` to `maxMissilesOnTarget`.

**Verified KSP 1.12.5 behaviour.** This comes from decompiling `Assembly-CSharp` on 2026-11-08, which resolves spike (a) offline:
- `BaseField<T>.SetValue(object, object host)` does `FieldInfo.SetValue(host, value)` and then fires the `OnValueModified` event (`Callback<object>`). The value must already be the field's exact CLR type: a `double` written to a `float` field throws inside, and `SetValue` logs and returns `false`.
- The PAW path is `UIPartActionFieldItem.SetFieldValue(object newValue)`:
  1. `old = field.GetValue(host)`; `changed = !Equals(old, new)`.
  2. If `(control.affectSymCounterparts & scene) != 0`, then for each `part.symmetryCounterparts[i]`:
     - Pick the module at the same index if it has the same type, otherwise the module with the same `ClassName`.
     - Call `module.Fields.SetValue(field.name, value)`.
     - Fire `onSymmetryFieldChanged(field, value)` on that counterpart field's editor **and** flight `UI_Control`.
     - Set `changed` if any counterpart value differed.
  3. If `changed`:
     - Call `field.SetValue(new, host)`, which fires `OnValueModified`.
     - Call `control.onFieldChanged?.Invoke(field, old)`.
     - If `!control.suppressEditorShipModified` and the scene is the editor, fire `GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship)`.
- The float slider snaps to `stepIncrement` and rounds to 5 decimals before calling `SetFieldValue`.

For each `ksp.module.set` the host therefore:
1. Validates the value against the schema (range, step, enum) and rejects it with `INVALID_PARAMS` and the allowed range. A value off the step grid is rejected with the nearest valid value in `hint`.
2. Converts the JSON value to the field's exact CLR type (`float`, `double`, `int`, `bool`, `string`, enum). For `UI_ChooseOption` on an `int` field the value is the option index.
3. Runs the exact `SetFieldValue` sequence above. Symmetry is applied when `symmetry` is true (the default, as in the PAW) and the control's `affectSymCounterparts` covers the scene.
4. Reads the value back and returns `{old, new, applied, symmetryUpdated}`, because some mods clamp or reject values in their callbacks.

The host does **not** replicate the PAW's `InputLockManager` check, so AI writes work while the user has a window open. Nor does it require `guiActive`: fields such as `MissileFire.guardRange` have UI controls but are shown in BDArmory's own window rather than the PAW. `describe` reports `visibleInPaw` so a caller can tell the two apart.

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

A `SKIZZY_ANNOTATIONS` ConfigNode (format in spec §12.5) can be shipped by the host for popular mods, by a mod author, or by the user, and patched with ModuleManager. It can:
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
- ships **only** inside `GameData/SkizzyProtocol/Plugins/`. Mods must not bundle it, because a duplicate assembly name in KSP's shared AppDomain loads first-wins.
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
- **Error model.** JSON-RPC error objects carry a numeric `code` (-32001…-32013) and `data.kind`, a stable string: `UNAUTHENTICATED`, `PERMISSION_DENIED`, `CONFIRMATION_REQUIRED`, `UNAVAILABLE_IN_SCENE`, `NOT_FOUND`, `BUSY`, `TIMEOUT`, `CONFLICT`, `PRECONDITION_FAILED`, `PROVIDER_ERROR`, `CANCELLED`, `RATE_LIMITED`, `VERSION_UNSUPPORTED`, plus the standard JSON-RPC errors. Each error also carries `retryable`, `hint` and context. Full table in spec §10.

### 5.6 Mapping onto MCP (bridge behaviour)

| skizzy-protocol | MCP 2026-07-28 | Notes |
|---|---|---|
| `session.hello` / catalog | `server/discover` capabilities, `tools/list`, `resources/list`, `resources/templates/list` | Lists are deterministic (sorted). `ttlMs` follows the catalog revision, with `cacheScope:"private"`. |
| Capability | **Tool** | `inputSchema` and `outputSchema` pass through (JSON Schema 2020-12). Results carry `structuredContent` (always a JSON object; non-object values are wrapped as `{"value": …}`) **and** a text block containing the same JSON, capped at 64 KB with a truncation notice. v0.1 proposed a 400-character summary; that was wrong for Claude Code and other clients that feed only text content to the model. MCP itself recommends serialising structured content into a text block for compatibility. |
| Screenshot (`ksp.camera.screenshot`) | **Image content block** (`ImageContentBlock`, base64 JPEG or PNG), plus a small JSON text block with metadata (resolution, bytes, UT, camera mode) | The host returns `{mimeType, dataBase64, …}`; the bridge turns it into MCP image content. Size and rate caps apply (§8.3). |
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

**Transports.** stdio only in v1. Example registration:

```
claude mcp add skizzy -- "C:\Tools\skizzy-mcp.exe" --ksp "D:\Epic Games\KerbalSpaceProgram\English"
```

The exe is self-contained, so no .NET install is needed. It reads the host token from `<ksp>\GameData\SkizzyProtocol\PluginData\auth.token` on every (re)connect, so regenerating the token in game needs no bridge restart. If KSP is not running, the core tools still answer: `skizzy_status` reports `connected:false` with a hint. Other calls return a clean tool error ("KSP is not running or the Skizzy host is not listening …"). The SDK negotiates the older 2025-11-25 handshake for clients that have not moved to 2026-07-28. Streamable HTTP is deferred to M6 and would be loopback-only with Origin checks and the same token.

### 5.7 AI-agnostic by construction
- The **host protocol is the standard.** MCP is one projection of it.
- Any language with a WebSocket client can connect, read the token from `GameData/SkizzyProtocol/PluginData/auth.token`, and call `session.hello`.
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
- **Kill switch.** A toolbar "Pause AI" button and a hotkey (default `Ctrl+Alt+K`, configurable; polled only while a client is connected) immediately:
  - deny all non-read calls
  - cancel operations and reject pending confirmations
  - release every in-game autopilot hold, so manual control is restored (§8.2)
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

### 6.4 Config file: `GameData/SkizzyProtocol/PluginData/permissions.cfg`

The format is KSP ConfigNode, and `PluginData` keeps edits out of the MM cache, following BDArmory's pattern. KSP's ConfigNode syntax allows **one key per line**, so the file is written multi-line.
- The mod ships the default at `PluginData/defaults/permissions.cfg`: everything off except stock read.
- On first run the host copies it to `PluginData/permissions.cfg`, so mod updates never overwrite user edits.
- Host settings (port, limits, hotkeys, Origin allow-list) live separately in `PluginData/settings.cfg`.

Example of a user-edited file (Test Director preset):

```
SKIZZY_PERMISSIONS
{
    version = 1
    defaultPolicy = deny
    confirmChannel = game          // game | client | either
    confirmTimeoutSec = 60
    AREAS                          // RB1: every area off by default except ksp.read
    {
        ksp.read = true
        ksp.module = false
        bdarmory = true
        ksp.flight = false
        ksp.camera = false
        ksp.editor = false
        craft = false
    }
    MOD
    {
        id = Squad
        read = allow
    }
    MOD
    {
        id = BDArmory
        read = allow
        control = allow
        cheat = allow
        destructive = ask
        CAPABILITY
        {
            name = bda.spawn_vessels
            policy = allow
            rateLimit = 6/min
        }
        CAPABILITY
        {
            name = bda.fire_*
            policy = ask
            rateLimit = 20/min
        }
    }
    MOD
    {
        id = HullBreach
        read = allow
        control = ask
    }
    PARTPACK                       // M2
    {
        id = Johnny
        control = allow
    }
    CLIENT                         // cap any client by name: levels it may ever use
    {
        name = skizzy-mcp
        levels = read, control, destructive, cheat
    }
}
```

Capability names in `CAPABILITY` rules may use a trailing `*` glob. `levels` in `CLIENT` replaces v0.1's `maxLevel`, because levels are not a hierarchy (§6.1).

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
- Format: JSONL at `GameData/SkizzyProtocol/PluginData/audit/audit-YYYYMMDD.jsonl`. Rotation keeps 14 days or 50 MB. Lines are written by a background writer, never on the main thread.
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
  - `GameData/SkizzyProtocol/PluginData/`

  Writes are allowed only to `AutoSpawn/`, our `PluginData/`, and (area `craft`/`ksp.editor`, M3) `saves/<profile>/Ships/{VAB,SPH}/`. File names are restricted to `[A-Za-z0-9 _.-]` and must end in `.craft`.
- **Prompt injection.** Vessel names, craft descriptions and log lines are attacker-controllable text, for example downloaded craft files. The bridge returns them as data fields and never puts them in tool descriptions. Because permissions are enforced host-side, an injected instruction cannot escalate beyond what the user granted.
- **The bridge's offline log reader** (when the game is down) is read-only and limited to `KSP.log` and BDArmory `Logs/`.

---

## 7. BDArmory adapter

### 7.1 Tool list

All tools take `vessel` as a handle or an exact name. Ranges are read live from `UI_Control` and are not hard-coded. "WM" means `MissileFire`. Line numbers refer to HEAD `01d0c9676`.

**M1 set** (area `bdarmory`, off by default): `bda.status`, `list_craft`, `stage_craft`, `spawn_vessels`, `spawn_template`, `remove_vessels`, `list_teams`, `set_team`, `set_guard_mode`, `get_weapon_manager`, `configure_weapon_manager`, `start_competition`, `stop_competition`, `get_competition_state`, `get_scores` and `tail_log`. Everything else lands in M3. The `bda.fire_*` tools are allowed by design but are `destructive`, so every preset sets them to **ask**.

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
| `bda.set_team` | control | `vessel`, `team`, `allies?[]`, `neutral?` | `MissileFire.SetTeam(BDTeam.Get(name))` (`Control/MissileFire.cs:1175`). It sets every WM on the vessel and handles `TargetInfo`. **Do not use `SetTeamByName`: it is an empty stub (`:1208`).** `MissileFire.OnChangeTeam` (`:1173`) drives the `bda.team_changed` event. `BDTeam.Get` (`Competition/BDTeam.cs:28`) creates the team if it is missing. |
| `bda.set_guard_mode` | control | `vessel`/`vessels[]`/`all`, `enabled` | `guardMode` (`MissileFire.cs:1042-1044`). Calls `ToggleGuardMode()` (`:893`) **only if the state differs**, because toggling has side effects (unslaves turrets, clears the selected weapon). Acts on the primary WM (`VesselModuleRegistry.GetMissileFire(v, true)`, `Utils/VesselModuleRegistry.cs:533`). |
| `bda.get_weapon_manager` | read | `vessel` | All WM `KSPField`s, plus state: `guardMode`, `currentTarget` (`:420`), `targetsAssigned`/`missilesAssigned` (`:422-423`), `firedMissiles` (`:94`), `selectedWeapon`, `weaponArray` (`:64`), `missileIsIncoming` (`:393`), `underFire` (`:609`), `vesselRadarData` (`:491`). It covers all WMs on the vessel via `VesselModuleRegistry.GetMissileFires` (`Utils/VesselModuleRegistry.cs:521`). |
| `bda.configure_weapon_manager` | control | `vessel`, `allWeaponManagers?`, and any WM field. Notably:<br>• `multiTargetNum` (float, 1…**1024**, step 1, `MissileFire.cs:682-684`)<br>• `multiMissileTgtNum` (float, 1…1024, step 1, `:686-688`)<br>• `maxMissilesOnTarget` (float, 1…**128**, step 1, `:692-694`)<br>• **parallel fire:** `parallelFire` (bool, `:696-699`), `parallelSalvoSize` (float, 1…128, step 1, `:701-703`; clamped to `maxMissilesOnTarget`), `parallelSalvoStagger` (float frames, 0…10, step 1, `:706-708`)<br>• `targetScanInterval` (`:649`), `fireBurstLength` (`:654`), `AutoFireCosAngleAdjustment` (`:658`), `guardAngle` (`:664`), `guardRange` (`UI_FloatSemiLogRange` 100…200000, `:669`), `gunRange` (`UI_FloatPowerRange` 0…10000, `:674`)<br>• target priority weights (`targetBias`, `targetWeight*`)<br>• `isVIP` (`:885`), `weaponChannel` (`:889`) | Uses the §4.5 write path (`BaseField.SetValue`, which fires `OnValueModified`, then `onFieldChanged`) so BDArmory's callbacks run, including `OnParallelSalvoSettingsChanged` (`:1680`). Fields are applied in dependency order: `maxMissilesOnTarget` before `parallelSalvoSize`. The schema is built at runtime from the `UI_Control` attributes on `MissileFire`'s `FieldInfo`s, which needs no instance. **A field missing from the installed DLL is left out of the schema** (fail closed), and passing it returns `PRECONDITION_FAILED {reason:"api_missing"}`. Any other WM `KSPField` with a writable UI control is accepted through `additionalProperties`. The results are read back. |
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
- **Spawner quirks found while building M1.** These are worked around in the shim and are candidate fixes for the M5 PR:
  - `CircularSpawning.SpawnAllVesselsOnce(CircularSpawnConfig)` does not reset name deconfliction. The shim calls `SpawnUtils.ResetVesselNamingDeconfliction()` first, as BDArmory's own UI does.
  - The folder must exist under `AutoSpawn` even when `craftFiles` is passed (`CircularSpawning.cs:130-178`).
  - Craft paths go straight to `ConfigNode.Load`, so they must be absolute.
  - `teamsSpecific` combined with any `numberOfTeams` other than 1 or -1 puts every vessel on team "A" (an empty `teamVesselNames`, `:371`). The shim only uses folder teams (`numberOfTeams = 1`) or FFA (`0`). For explicit team names it spawns with `assignTeams = false` and calls `MissileFire.SetTeam` afterwards.
  - `CustomTemplateSpawning.Spawn(SpawnConfig)` is a no-op: it never starts the coroutine (`CustomSpawnTemplate.cs:59`). The shim calls `SpawnCustomTemplate(CustomSpawnConfig)` (`:95`) directly.
  - `VesselSpawnerStatus.vesselSpawnSuccess` ANDs the instance flags and can be stale after a template spawn. The shim reads `CustomTemplateSpawning.Instance.vesselSpawnSuccess` instead.
  - `SpawnUtils.CancelSpawning()` (`SpawnUtils.cs:29`) can throw a NullReferenceException when an instance is null and does not cancel templates. The shim calls each spawner's `CancelSpawning()` directly, guarded.
  - `SpawnUtils.RemoveAllVessels()` is a coroutine that may yield Unity `YieldInstruction`s, so the shim runs it through the host's Unity-coroutine bridge (`ctx.RunUnityCoroutine`) rather than stepping it manually.
- **Spawning with no active vessel** (spike e). With `killEverythingFirst = true`, `AcquireSpawnPoint` calls `SpawnUtils.RemoveAllVessels()`, which spawns `SpawnProbe.craft` at the camera, switches to it, removes everything else, and then removes the probe. `VesselSpawner.spawnProbeLocation` must resolve: either `GameData/BDArmory/craft/SpawnProbe.craft` or `Ships/SPH/SpawnProbe.craft`. The shim's self-check reports it.

### 7.3 Where the adapter lives: both, in phases

| | Phase 1: separate shim mod `Skizzy.BDArmory` | Phase 2: upstream PR into BDArmory |
|---|---|---|
| Why | Ships now, independent of BDArmory release cadence and of the in-progress multi-target branch. Can use reflection for private members. | Durable. In-tree code is refactored along with BDArmory, so it never breaks silently. |
| Dependency | Hard reference to `BDArmory.dll` (it is BDArmory-specific anyway), plus `Skizzy.Host` and `Skizzy.Protocol` until the M4 `Skizzy.Contract.dll` exists. `[KSPAssemblyDependency("BDArmory", 1, 0)]` makes KSP skip loading the shim when BDArmory is absent. | **None.** Tier-2 attributes in a CC0 file copied into `BDArmory/ModIntegration/Skizzy/`. |
| What the PR adds | — | (1) Attribute-tagged tool methods, mostly thin wrappers. (2) A small public API: `MissileFire.FireMissileAt(...)`, `VesselRadarData.GetDisplayedContacts()` (read-only copy), `BDACompetitionMode.OnCompetitionStarted`/`OnCompetitionEnded` and `Scoring.OnDeath` static events, a working `MissileFire.SetTeamByName`. (3) Zero runtime cost when no Skizzy host is present: attributes are inert and no reflection runs. |
| Coexistence | The shim registers provider `bdarmory` with `Authority = 50`. The PR uses 100. The host keeps the higher-authority implementation of each capability name. The shim's remaining tools fill gaps. | — |
| Licence | **MIT** (user decision). The shim contains no BDArmory code; it only calls BDArmory's public API at runtime and is distributed separately from BDArmory's files. | Inherits BDArmory's CC-BY-SA 2.0. The copied attribute file is CC0, so no conflict. |

PR etiquette: open a forum or Discord discussion with the maintainers (DocNappers / BrettRyland et al.) before writing it. Target the `dev` branch, as their README requires, and keep the PR small and reviewable: attributes plus four public methods.

---

## 8. New capability areas: craft building, flight control, vision

These three areas follow the same rules as everything else:
- each has its own area switch, off by default (RB1)
- the same `cap.call` pipeline, permission levels, audit and kill switch
- the same robustness requirements (§3.7)

API names below were checked against the installed `Assembly-CSharp` and module metadata on 2026-11-08.

### 8.1 Craft building (`ksp.editor.*` in the VAB/SPH, headless `craft.*`)

**Areas and levels.**

| Area | Scenes | Levels used |
|---|---|---|
| `ksp.editor` | `EDITOR` | `read` (status, validate, part catalog); `control` (any edit: new, load into an empty or saved editor, add, move, rotate, offset, remove, symmetry, staging, module configuration, undo, save as a new file); `destructive` (load or new while the editor has unsaved changes, or save over an existing file) |
| `craft` | any | `read` (`craft.read`, `craft.validate`); `control` (document edits, save as a new file); `destructive` (overwrite) |

Building is normal play, so nothing here is `cheat`. Copying craft into `AutoSpawn` stays `bda.stage_craft` (cheat, §7.1), because it exists only to spawn.

**Tools.**

| Tool | Level | Parameters | KSP APIs |
|---|---|---|---|
| `ksp.editor.status` | read | — | `EditorDriver.editorFacility`, `EditorLogic.fetch.ship` (`ShipConstruct`: `shipName`, `parts`, `persistentId`), `EditorLogic.fetch.symmetryMode`/`symmetryMethod`, the root part, a stage summary from `part.inverseStage` |
| `ksp.parts.catalog` | read | `query?`, `category?`, `module?`, `limit` | `PartLoader.LoadedPartsList` (`AvailablePart.name`, `title`, `category`, `partPrefab.attachNodes` (`AttachNode.id`, `position`, `size`), `attachRules`, `partConfig` MODULE names, mass and cost). The AI needs this to pick parts by name. |
| `ksp.editor.new_craft` | control / destructive | `facility: VAB\|SPH`, `name`, `rootPart` | Generates a one-part craft ConfigNode and loads it through the replace path below |
| `ksp.editor.load_craft` | control / destructive | `path` (sandboxed) or `name`+`facility` | `EditorLogic.LoadShipFromFile(path)`, the same path as the Load button |
| `ksp.editor.add_part` | control | `part` (AvailablePart name), `parent` (part handle), and either `attach:{node, parentNode}` or `surface:{position:[x,y,z], normal:[x,y,z]}` in the parent's frame; `symmetry?:{method: radial\|mirror, count}` | ConfigNode round trip (below). Node positions come from the prefab's `AttachNode.position`/`orientation`. Symmetry uses `sym =` links, as `EditorLogic.symmetryMethod` does. |
| `ksp.editor.move_part`, `rotate_part`, `offset_part` | control | `part`, `position`/`rotation` (euler or quaternion)/`offset` in the parent or ship frame, `withSymmetry` | ConfigNode round trip; the `attPos`/`attRot` fields |
| `ksp.editor.remove_part` | control | `part`, `withChildren` (default true) | ConfigNode round trip |
| `ksp.editor.set_staging` | control | `[{part, stage}]` | `istg`/`dstg`/`sqor` in the round trip; `part.inverseStage` |
| `ksp.editor.apply` | control | `ops:[…]`, any of the edit ops above | **Batch.** Many edits cost one round trip. This is the efficient path for an LLM building a whole craft. |
| `ksp.editor.configure_module` | control | `module`, `values{}` | The Tier-0 write path (§4.5) in the editor scene, so `onEditorShipModified` fires |
| `ksp.editor.undo` | control | — | Restores the backup taken before the last Skizzy edit (`EditorLogic.SetBackup()` before every edit) |
| `ksp.editor.save` | control / destructive | `name`, `facility`, `target: ships\|autospawn`, `overwrite` | `ShipConstruction.SaveShipToPath(HighLogic.SaveFolder, facility, localPath, shipName)`, or `ShipConstruction.SaveShip(ship, filename)`, under `saves/<profile>/Ships/{VAB,SPH}` or `AutoSpawn/<folder>` |
| `ksp.editor.validate` | read | `checks?[]` | Validation report (below) |
| `craft.read` | read | `path` | Header (`ship`, `version`, `type`, `size`), part tree (`part`, `persistentId`, `link`, `attN`, `srfN`, `sym`, `pos`/`rot`, `istg`), modules and resources. Parsed with the shared ConfigNode parser, **off the main thread**. |
| `craft.create`, `craft.add_part`, `craft.remove_part`, `craft.set_module_field`, `craft.set_stage` | control | `doc` handle (`doc:N`) or a path with an expected content hash | Edits an in-memory `CraftDocument`, the same model as the editor round trip |
| `craft.save` | control / destructive | `doc`, `path`, `overwrite` | Sandboxed write. The file name must match `[A-Za-z0-9 _.-]+\.craft`. |
| `craft.validate` | read | `path\|doc`, `loadInEditor?` | Static checks. With `loadInEditor`, which needs the `EDITOR` scene and `ksp.editor`, the file is loaded and the resulting part tree is compared with the document. This is the "validated by loading it back" requirement. |

**Implementation notes.**
- **ConfigNode round trip, not mouse emulation.** KSP's interactive attach logic in `EditorLogic` is private and driven by cursor state. Each edit therefore:
  1. snapshots the ship with `EditorLogic.fetch.ship.SaveShip()` (the `ConfigNode` that undo backups use)
  2. applies the change to a `CraftDocument`
  3. calls `EditorLogic.fetch.SetBackup()` for undo
  4. reloads the ship. The reload uses `ShipConstruct.LoadShip(ConfigNode)` + `EditorLogic.fetch.SpawnConstruct(…)`, or `EditorLogic.LoadShipFromFile` on a temp file in `PluginData/tmp/`.

  Which reload path replaces the ship cleanly, rather than spawning a floating subassembly, is an **M3 spike**. `ksp.editor.apply` keeps the cost to one reload per batch.
- **Part nodes** are generated from `AvailablePart.partConfig`: default `MODULE` and `RESOURCE` nodes. They get fresh `persistentId`s, checked against the ship for uniqueness, and `part = <name>_<uid>` references.
- **Node attach positions:** `child.pos = parent.pos + parent.rot · (parentNode.position) − child.rot · (childNode.position)`, with prefab node positions scaled by `rescaleFactor`. **Surface attach** takes caller-supplied coordinates. In the editor the host can snap them to the parent collider with `Physics.Raycast` along `-normal`.
- **The validation report** (`ksp.editor.validate`, `craft.validate`):
  - wet and dry mass, and CoM as Σ mᵢ·(posᵢ + `CoMOffset`)
  - CoL from the parts' `ILiftProvider`s, the query the editor's CoL marker uses
  - CoT and TWR per stage, from `ModuleEngines.maxThrust`/`thrustTransforms` against the body's surface gravity
  - for ships: a **buoyancy estimate and waterline**. Σ ρ·g·Vᵢ·`part.buoyancy`, with Vᵢ from the drag-cube or bounds volume and ρ from `CelestialBody.oceanDensity`. The waterline comes from a binary search on draft, assuming each part's volume is evenly distributed along the up axis. The report states that this is an estimate and notes any BuoyancyAdjuster overrides.
  - a **missing-WM or missing-AI check**, using the rule of BDArmory's `SpawnUtils.CheckAIWMPlacement` (`SpawnUtils.cs:469`): the AI and WM must sit on the root or a direct child, and the root must be a cockpit or seat or have a seat child. It also counts WMs and AI modules.
  - part-count warnings: over 500 is heavy and over 1000 is very heavy for BDArmory battles
  - clipping warnings: pairwise overlap of world-space bounds between parts that are not directly attached, using a spatial hash; the 20 worst are reported
  - attach-link consistency checks for headless documents

**Risks:**
- The reload path may drop transient editor state, such as a selected part or open PAWs.
- Variant and B9PartSwitch modules need their state carried through the round trip.
- TweakScale rescaling must be preserved.
- Surface-attach geometry is approximate without colliders.
- Reloading very large craft (500+ parts) takes about 100 ms or more per batch.
- KSP's craft-format details, such as `link`/`attN`/`srfN`/`sym`, `persistentId` and `mir`.

**Acceptance tests (M3):**
1. In the SPH, from Claude Code:
   - create a craft with a cockpit root
   - add a fuel tank on the bottom node and mirror-symmetric wings
   - add a BDArmory WM and Pilot AI as root children
   - save to `Ships/SPH/skz_test.craft`

   Reloading it gives an identical part count and attach tree. `validate` reports no WM or AI problems and sensible CoM, CoL and TWR values.
2. **Headless.** `craft.create`, then `add_part` ×5, then `save`, then `craft.validate loadInEditor=true` succeeds. `KSP.log` shows no errors, and the tree is identical after loading.
3. **Boat.** The predicted waterline is within 10% of the observed draft after `bda.spawn_vessel` on water (measured from `ksp.vessel.get` altitudes).
4. Overwriting an existing craft is `destructive` (ask by default). Path traversal (`..\`) and bad file names are rejected. With `ksp.editor` off, no editor tool is listed.

### 8.2 Flight control (`ksp.flight.*`)

**Area and levels.** Area `ksp.flight`, scene `FLIGHT`. Input-writing capabilities use affinity `fixed`.
- `read`: `state`, `holds`
- `control`: controls, holds, hand-off, vessel switch, action groups except Abort
- `destructive`: staging and the Abort group

**Tools.**

| Tool | Level | Parameters | KSP APIs |
|---|---|---|---|
| `ksp.flight.state` | read | `vessel` | Throttle (`FlightInputHandler.state.mainThrottle` for the active vessel, `vessel.ctrlState` otherwise), SAS (`vessel.ActionGroups[SAS]`, `vessel.Autopilot.Mode`/`Enabled`), RCS, gear, brakes, lights, `Custom01–10`, current stage, speeds, altitude, heading, pitch and roll from `vessel.srfRelRotation`/`ReferenceTransform`, active holds, and **controller in charge**: `manual\|skizzy\|bdarmory\|atmosphereautopilot` |
| `ksp.flight.set_controls` | control | `vessel`, `throttle?` (0..1), `sas?`, `sasMode?` (`StabilityAssist\|Prograde\|Retrograde\|Normal\|Antinormal\|RadialIn\|RadialOut\|Target\|AntiTarget\|Maneuver`), `rcs?`, `gear?`, `brakes?`, `lights?` | `FlightInputHandler.state.mainThrottle` (active vessel) or a one-shot `OnFlyByWire` setpoint (non-active); `vessel.ActionGroups.SetGroup(KSPActionGroup.X, bool)`; `vessel.Autopilot.CanSetMode(m)` then `SetMode(m)` |
| `ksp.flight.action_group` | control (Abort: destructive) | `vessel`, `group: Custom01..Custom10\|Gear\|Brakes\|Light\|RCS\|SAS\|Abort`, `state?`\|`toggle` | `ActionGroupList.SetGroup`/`ToggleGroup` |
| `ksp.flight.stage` | destructive | `vessel` (must be active) | `KSP.UI.Screens.StageManager.ActivateNextStage()` |
| `ksp.flight.hold` (op optional) | control | `vessel`, any of `heading`, `pitch`, `roll`, `altitude`, `speed`, `depth`, `verticalSpeed`, plus `limits?{maxBank, maxPitch, maxThrottle}`, `gains?`, `takeOver?`, `waitFor?: "settled"` | Installs a **setpoint autopilot** (below). With `waitFor:"settled"` it returns an operation that completes once every target has been within tolerance for 5 s. |
| `ksp.flight.release` | control | `vessel` or `all` | Removes the holds and restores the pre-hold SAS state |
| `ksp.flight.holds` | read | — | Every hold: targets, errors, saturation, settled, age |
| `ksp.flight.handoff` | control | `vessel`, `to: manual\|skizzy\|bdarmory\|atmosphereautopilot`, `aaMode?: fly_by_wire\|cruise` | BDArmory: `vessel.ActiveController().AI.ActivatePilot()`/`DeactivatePilot()` (`Extensions/VesselExtensions.cs:361`, `Control/IBDAIControl.cs:23-25`). This needs the `bdarmory` area and grant as well. AtmosphereAutopilot, by reflection and failing closed: `AtmosphereAutopilot.AtmosphereAutopilot.Instance.getVesselModules(v)` gives the `TopModuleManager`, then `activateAutopilot(typeof(StandardFlyByWire\|CruiseController))`. |
| `ksp.flight.switch_vessel` | control | `vessel` (loaded) | `FlightGlobals.SetActiveVessel(v)`. Only loaded vessels inside physics range; otherwise `PRECONDITION_FAILED`. |

**Setpoint autopilots, in game at physics rate.**
- **Mechanism.** A per-vessel `SkizzyAutopilot` registers `vessel.OnFlyByWire += Fly` (a `FlightInputCallback(FlightCtrlState)`). KSP invokes it every physics step while the vessel is loaded and unpacked, after the stock SAS hooks (`OnPreAutopilotUpdate`/`OnAutopilotUpdate`). It writes `st.pitch`, `yaw`, `roll`, `mainThrottle`, `wheelThrottle` and `wheelSteer`. No callback is registered while no hold exists (RB3).
- **Controllers.** Cascaded PIDs, pre-allocated and allocation-free per tick (RB7):
  - heading → bank target → roll rate
  - altitude → pitch target (clamped) → pitch rate
  - speed → throttle
  - surface ships: heading → `wheelSteer`/yaw, speed → `wheelThrottle`/`mainThrottle`
  - submarines: depth → pitch and (when a naval mod exposes ballast through Tier 0) ballast setpoints
- **Gains** default per vehicle class (plane, rocket, rover, boat, submarine), scaled by mass and moment of inertia, with output slew limits. Callers may override them within bounds.
- **Safety envelope.**
  - Outputs are clamped, and attitude limits apply (max bank 45°, max pitch ±30° unless raised).
  - A hold **auto-releases** when:
    - the vessel is destroyed, unloaded or packed, or the scene changes
    - rails warp starts
    - the kill switch is used
    - BDArmory AI or AtmosphereAutopilot activates on the vessel (a `CONFLICT` event is reported)
    - **the user touches the controls**: pilot input in `FlightInputHandler.state` beyond a deadband triggers a "manual override" event
- **One owner per vessel.** A `ControllerArbiter` tracks who owns each vessel. A hold on a vessel another controller owns returns `CONFLICT` unless `takeOver:true`, which deactivates the other controller first.
- **No network loop.** The AI sets targets at human pace, in seconds. Controllers run at 50 Hz in game. `ksp.flight.hold {waitFor:"settled"}` plus `skizzy_wait` replaces polling.
- **Visibility.** An on-screen message (`ScreenMessages`) shows "AI holding Kirov_1 — Ctrl+Alt+K to stop" while any hold is active.

**Risks:**
- Fights with SAS, MechJeb, AtmosphereAutopilot or BDArmory. Mitigated by the arbiter and conflict detection.
- Orientation conventions, such as "control from here" and `ReferenceTransform`.
- Tuning PIDs for very different craft.
- Non-active vessels obey `OnFlyByWire` only while loaded and unpacked.
- Detecting user overrides with joystick deadbands.
- Physics warp scales the controller timestep (use `TimeWarp.fixedDeltaTime`).

**Acceptance tests (M2):**
1. A plane in level flight converges under `hold {heading:90, altitude:2000, speed:200}` within 60 s. It then holds within ±50 m, ±3° and ±5 m/s for 60 s, and the audit shows only the single call.
2. Kill switch during a hold: controls are released within one physics step, SAS returns to its pre-hold state, and manual input works at once.
3. Hand-off from hold to BDArmory AI to manual, with each step reflected in `state.controller`.
4. A boat holds heading within ±5° and speed within ±1 m/s. A submarine holds −50 m depth within ±5 m (with a naval ballast mod).
5. `stage` and the Abort group are `destructive` (ask by default). With no hold active, no `OnFlyByWire` callback is registered (self-check).

### 8.3 Vision (`ksp.camera.*`), moved from M6 to M2

**Area and levels.** Area `ksp.camera`, any scene.
- `read`: screenshots, which change no game state
- `control`: camera mode, target and orbit, which change the user's view reversibly

**Tools.**

| Tool | Level | Parameters | KSP / Unity APIs |
|---|---|---|---|
| `ksp.camera.screenshot` | read | `width?` (256…1920, default 1024), `format: jpeg\|png` (default jpeg), `quality?` (10…95, default 75), `hideUi?` (default false) | Captured on the main thread **at end of frame**: a host coroutine runs `yield return new WaitForEndOfFrame()`, then `ScreenCapture.CaptureScreenshotAsTexture()`. It is then downscaled with `Graphics.Blit` into a pooled `RenderTexture.GetTemporary`, read with `Texture2D.ReadPixels`, encoded with `ImageConversion.EncodeToJPG`/`EncodeToPNG`, and every texture is destroyed. `hideUi` fires `GameEvents.onHideUI` for one frame, then `onShowUI`. Returns `{mimeType, width, height, bytes, ut, scene, cameraMode, dataBase64}`; base64 encoding happens on the writer thread. |
| `ksp.camera.set_mode` | control | `mode: auto\|free\|orbital\|chase\|locked` | `FlightCamera.SetMode(FlightCamera.Modes.X)` |
| `ksp.camera.target` | control | `vessel`, `part?` | `FlightCamera.fetch.SetTargetVessel(v)`, or targeting a part. This moves the camera focus without changing the active vessel. |
| `ksp.camera.orbit` | control | `heading`, `pitch`, `distance` | `FlightCamera.fetch` heading, pitch and distance setters, clamped to the camera's limits |

**Caps (RB6):**
- at most 1 capture per 2 s per session (configurable), plus a global cap
- at most 2 MB encoded: JPEG quality steps down until the image fits, and the call fails with `PRECONDITION_FAILED {reason:"too_large"}` if it never does
- at most 1920 px wide
- the main-thread cost of each capture is recorded in `host.stats`

**MCP mapping.** The bridge returns an `ImageContentBlock` (`ImageContentBlock.FromBytes(bytes, mimeType)`) plus a small JSON text block with the metadata. Claude Code shows the image to the model.

**Risks:**
- `ReadPixels` stalls the GPU, about 5–20 ms at 1080p; mitigated by the rate cap and downscaling.
- Visual mods (Scatterer, EVE, TUFX, Firefly) are captured as rendered. A custom render path is avoided for exactly this reason.
- `hideUi` causes a one-frame UI flicker.
- Leaked `Texture2D`/`RenderTexture` objects; every allocation is paired with `Destroy`/`ReleaseTemporary` in `finally`.
- Payload size.
- Privacy: only the game's framebuffer is captured, never the desktop.

**Acceptance tests (M2):**
1. `screenshot` returns a JPEG of 2 MB or less, 1024 px wide, costing under 30 ms of main-thread time, and Claude Code displays it.
2. 100 rate-limited captures leave the `Texture2D` and `RenderTexture` object counts unchanged (self-check via `Resources.FindObjectsOfTypeAll`).
3. A second capture within 2 s returns `RATE_LIMITED {retryAfterMs}`.
4. `target` followed by `screenshot` shows the targeted vessel (manual check).
5. With `ksp.camera` off, no camera tool is listed and no coroutine ever starts.

---

## 9. Telemetry and automated test battles

### 9.1 Scenario format (run with `skizzy-mcp battle run` or the `bda_run_battle` tool; YAML parsed with a pinned YamlDotNet, JSON also accepted)

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

### 9.2 Runner lifecycle (in the bridge, over the native socket)
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

### 9.3 Metrics for the user's validation targets

| Change under test | Metric (computed from streams) | Source |
|---|---|---|
| Multi-target turrets (`multiTargetNum` up to 1024) | Distinct `visualTargetVessel` values across a WM's turrets per sample; size of `targetsAssigned`; time to the first secondary engagement | `ModuleWeapon.visualTargetVessel`, `MissileFire.targetsAssigned` (`:422`) |
| Multi-target missiles (`multiMissileTgtNum`, `maxMissilesOnTarget` up to 128, retargeting) | Missiles in flight grouped by (source WM, target): max and p95 per target compared with the setting (**assert ≤**); distinct targets with missiles in flight; retarget count (target changes on a missile in flight); saturation set size | `BDATargetManager.FiredMissiles`, `GetMissilesAway` (`:2275`), `missileSaturatedTargets` (private, `:425`, read by reflection in the shim or exposed by the PR) |
| Parallel fire (`parallelFire`, `parallelSalvoSize`, `parallelSalvoStagger`) | Launches per fixed-step window per vessel; salvo spread (Δt between launches in a salvo, expected to be about `parallelSalvoStagger` frames); peak simultaneous launches per target (assert ≤ `parallelSalvoSize`, which is itself ≤ `maxMissilesOnTarget`); guns firing concurrently | `bda.missile_launched` events with UT and fixed-frame index; the WM fields through `bda.get_weapon_manager` |
| HullBreach (patched) | Per-vessel water tonnes, capacity and inflow over time; time to founder; leaking and critical counts | `HullBreachVesselModule` properties (`:32-40`) through a Tier-2 provider in the user's patched source, or a Tier-1 property annotation |
| MNWS / NAS | Hits and kills by weapon name (scores keyed by weapon); torpedo (SLW) and ASW events; submarine depth (`vessel.altitude < 0`) | BDArmory scores and events; NAS `AntiSubmarineWeapon.dll` module fields through Tier 0 |
| Performance regression | Frame time p50 and p99 (`Time.unscaledDeltaTime`), physics steps per frame, GC collection count, host main-thread ms | `host.stats`, Unity `GC.CollectionCount(0)` |
| Stability | Exceptions per minute in `KSP.log` during the run | log ring buffer |

### 9.4 A/B comparisons
- Swapping BDArmory builds needs a KSP restart.
- `skizzy-mcp battle compare runs/A runs/B` aligns matrix cells and reports, for each metric, the delta with a bootstrap confidence interval. The build is identified by the label plus the auto-captured `BDArmory.dll` SHA-256.
- **Decided (2026-11-08): the harness never restarts KSP or swaps DLLs.** For an A/B run the user restarts KSP with the other build. The runner detects the new DLL hash and continues the comparison.

### 9.5 Statistics and determinism
- KSP physics, BDArmory randomness and frame timing are not deterministic, so the plan uses repeats (default 5) and fixed spawn geometry.
- Reports include mean ± 95% CI, win rates, and the distributions of the key metrics. Assertions that must *never* fail, such as the missile-per-target cap, are checked on every sample of every run.

---

## 10. Project layout and stack

### 10.1 Repository layout (as built for M0 and M1)
It is one monorepo while the spec is unstable. Once v1.0 ships and outside mods adopt it, `spec` and `contract` split into their own slow-moving repo.

```
D:\KerbalSpaceProgram\skizzy-protocol\          (future: github.com/Skizzy-create/skizzy-protocol)
├─ PLAN.md, PROTOCOL-SPEC-DRAFT.md, README.md, CHANGELOG.md, LICENSE (MIT), THIRD-PARTY-NOTICES.md
├─ Directory.Build.props        # LangVersion, deterministic builds, warnings-as-errors, PathMap
├─ global.json, nuget.config    # SDK 10.0.4xx (rollForward latestFeature); nuget.org only
├─ build/KSP.props              # KSPRoot/KSPManaged HintPaths for in-game projects
├─ build/copy-refs.ps1|.sh      # copies the *installed* BDArmory.dll into refs/ (gitignored)
├─ src/
│  ├─ Skizzy.Protocol/          # net48 + netstandard2.0. Shared by host and bridge:
│  │                            #   JSON DOM/parser/writer, wire constants, ErrorKind, Safety,
│  │                            #   schema builder/validator/CLR mapping, permission config + engine,
│  │                            #   rate limiter, globs, ConfigNode-syntax parser, tool naming,
│  │                            #   Tier-2 duck-typed attribute reader (wired up in M4)
│  ├─ Skizzy.Host/              # net48 KSP plugin
│  │  ├─ Core/                  # Unity-free: WebSocket server + RFC 6455 framing, HTTP upgrade and
│  │  │                         #   auth, sessions, JSON-RPC router, capability registry, dispatcher
│  │  │                         #   queue + budget, operations, confirmations, fault tracker, audit
│  │  ├─ Ksp/                   # KSPAddon bootstrap, pump MonoBehaviour, scene tracking, handles,
│  │  │                         #   Tier-0 reflector + PAW writer, stock providers, self-checks
│  │  └─ Ui/                    # IMGUI window, confirmation prompts, AppLauncher button
│  ├─ Skizzy.BDArmory/          # net48 shim. Refs refs/BDArmory.dll, Skizzy.Host, Skizzy.Protocol
│  └─ Skizzy.Mcp/               # net10.0 bridge → skizzy-mcp.exe (self-contained, single file)
├─ contract/                    # CC0: SkizzyAttributes.cs (Tier 2) + examples/
├─ tests/
│  ├─ Skizzy.Protocol.Tests/    # JSON, schema generation + validation, permission engine, cfg,
│  │                            #   naming, rate limiter, Tier-2 attribute reader (on the CC0 example)
│  ├─ Skizzy.Host.Tests/        # compiles Skizzy.Host/Core/** by link: WebSocket framing, upgrade +
│  │                            #   auth (Origin/Host/token), dispatcher queue + budget, operations,
│  │                            #   fault tracker, router pipeline over a real loopback socket
│  └─ Skizzy.Mcp.Tests/         # the bridge ↔ a fake host running the *real* Core server
├─ GameData/SkizzyProtocol/     # distribution layout, including committed prebuilt plugin DLLs
│  ├─ Plugins/Skizzy.Protocol.dll, Skizzy.Host.dll, Skizzy.BDArmory.dll
│  ├─ PluginData/defaults/permissions.cfg
│  └─ SkizzyProtocol.version, LICENSE.txt
└─ .github/workflows/ci.yml, release.yml
```

`Skizzy.Host/Core` is compiled by link into the test projects instead of being its own DLL. This keeps GameData at three DLLs, and it lets the bridge integration test drive the real server, router and permission code.

Later additions:
- `src/Skizzy.Contract/` (Tier 3a, M4)
- `contract/SkizzyLink.cs` (Tier 3b, M4)
- `spec/schemas`, `spec/conformance` (M4)
- `annotations/` (Tier-1 packs, M2)
- `clients/python` (M6)

### 10.2 Build tooling
- **Project format.** SDK-style `.csproj`. In-game projects target `net48` and reference KSP through HintPaths with `Private=false`:

  ```xml
  <KSPRoot Condition="'$(KSPRoot)' == ''">D:\Epic Games\KerbalSpaceProgram\English</KSPRoot>
  <KSPManaged>$(KSPRoot)\KSP_x64_Data\Managed</KSPManaged>
  ```

  `KSPRoot` can be overridden with `-p:KSPRoot=…` or the `KSP_ROOT` environment variable. `Microsoft.NETFramework.ReferenceAssemblies 1.0.3` lets net48 build without a targeting pack.
- **BDArmory** is referenced from `refs/BDArmory.dll`. `build/copy-refs` copies it from the installed `GameData/BDArmory/Plugins/BDArmory.dll`. That copy is gitignored, and the build never uses the BDArmory source tree.
- **Language version.** `LangVersion` 12, with no newer runtime types in net48 code.
- **No local paths in binaries.** Shipped DLLs are built with **`-p:DebugType=None -p:DebugSymbols=false`**, plus `Deterministic=true`, `PathMap` and `ContinuousIntegrationBuild` in CI, so no local paths are embedded. Release verification greps the DLLs for `D:\` and `C:\Users`.
- **Warnings.** `TreatWarningsAsErrors` is on for our projects, so the build has 0 warnings and 0 errors.
- **CI** cannot legally obtain KSP DLLs. It builds and tests `Skizzy.Protocol`, the host Core (through its test project) and the bridge. The in-game plugin DLLs are built locally and **committed prebuilt** under `GameData/SkizzyProtocol/Plugins/`, the same approach as the Skizzy-create SubTrim repo. The release workflow zips the committed GameData folder and publishes the self-contained bridge exe, with SHA-256 sums.
- **Deploy** is never automatic. The README describes copying `GameData/SkizzyProtocol` by hand.

### 10.3 Host dependencies (and why there is no Newtonsoft)
- KSP loads every DLL into one AppDomain, and the first assembly with a given name wins. Mods shipping different `Newtonsoft.Json` versions is a known source of breakage, and `Managed/` has none. `System.Text.Json` and `Microsoft.Extensions.*` are equally off limits in game.
- **JSON** is our own DOM, parser and writer in `Skizzy.Protocol`. The parser enforces depth and size limits. The writer handles NaN and Infinity and writes invariant-culture numbers.
- **WebSocket**, decided in M0, is our own minimal RFC 6455 server over `TcpListener`:
  - SHA-1 and Base64 handshake
  - text, ping, pong and close frames, plus fragmentation
  - masking enforcement
  - no compression

  `HttpListener` WebSocket support on Unity's Mono is uncertain (R5), while `TcpListener` is solid.
- **No Harmony** in the host. Adapters may use it later for hooks BDArmory lacks.

### 10.4 MCP bridge stack
- **C# on .NET 10 LTS.** .NET 8 LTS ends support on 2026-11-10; .NET 10 is supported until 2028-11.
- **ModelContextProtocol 2.3.0, pinned exactly** (`Version="[2.3.0]"`, with a NuGet lock file). It pulls in `ModelContextProtocol.Core` 2.3.0 (an exact dependency) and `Microsoft.Extensions.{Caching,Hosting}.Abstractions` ≥ 10.0.13. Why 2.3.0:
  1. **The two-week rule.** It is the newest release at least two weeks old on the pin date (2026-11-08). 2.3.0 was published 2026-10-20, 19 days earlier. 2.3.1 (2026-11-03) and 2.4.0 (2026-11-07) are too new.
  2. **The right line.** 2.x is the stable line aligned with MCP **2026-07-28**: discovery-first negotiation, MRTR and the Tasks extension. It also negotiates down for 2025-11-25 clients, so current Claude Code works either way.
  3. **Known state.** 2.3.0 fixed optional server-identity nullability and shutdown reliability. NuGet's advisory data lists no vulnerabilities for it.
  4. **The 2.3.1 patch does not apply.** 2.3.1 is a security fix for *authenticated stateful HTTP sessions*. The bridge is stdio-only. **Re-evaluate the pin on or after 2026-11-17**, when 2.3.1 turns two weeks old.
  5. **Licence.** The SDK is Apache-2.0. The release ships `THIRD-PARTY-NOTICES.md`.
- **No generic host.** The bridge calls `McpServer.Create(new StdioServerTransport(...), options)` with `McpServerHandlers.ListToolsHandler` and `CallToolHandler` for the dynamic catalog. That keeps the exe smaller and startup fast. Logs go to **stderr only**, because stdout is the MCP channel.
- **Host connection.** `System.Net.WebSockets.ClientWebSocket`, which sends no `Origin` header. The wire uses the shared `Skizzy.Protocol` JSON DOM and descriptor types, converted to `System.Text.Json` only at the MCP boundary.
- **Packaging.** `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None` produces one `skizzy-mcp.exe` with no .NET install required. Trimming is off because the SDK uses reflection-based JSON. Other RIDs build but are unsupported.
- **Why C#** (user decision):
  - one language for host, shim, bridge and tests
  - shared contract source, so the protocol types, schemas and permission model are literally the same code
  - the official C# SDK tracks the spec: 2.0.0 shipped on the day MCP 2026-07-28 was published
- **Battle runner** (M3): the `skizzy-mcp battle run|compare` subcommands live in the same exe.

### 10.5 Packaging
- **GameData layout:**

  ```
  GameData/SkizzyProtocol/
    Plugins/
      Skizzy.Protocol.dll
      Skizzy.Host.dll
      Skizzy.BDArmory.dll      // [KSPAssemblyDependency("BDArmory",1,0)]: KSP skips it if BDArmory is absent
    PluginData/
      defaults/permissions.cfg // copied to PluginData/permissions.cfg on first run
    SkizzyProtocol.version     // KSP-AVC, KSP 1.12.x
    LICENSE.txt
  ```

  Runtime files the host creates: `PluginData/auth.token`, `permissions.cfg`, `settings.cfg`, `audit/*.jsonl`. None of them is shipped, so an update never overwrites user state.
- **GitHub releases** carry `SkizzyProtocol-<version>.zip` (the GameData folder) and `skizzy-mcp-<version>-win-x64.exe`, with `SHA256SUMS.txt`.
- **CKAN (M6).** `SkizzyProtocol` depends on nothing and recommends BDArmoryPlus. Mods with Tier 3a get `depends: SkizzyProtocol`; mods with Tier 2 or 3b get `suggests`.

### 10.6 Licensing

| Artifact | Licence | Why |
|---|---|---|
| Spec text | CC-BY 4.0 | Anyone can reimplement it |
| `Skizzy.Protocol`, `Skizzy.Host`, `Skizzy.BDArmory`, `skizzy-mcp` | **MIT** | User decision. Permissive, embeddable and forkable. |
| `contract/SkizzyAttributes.cs` and its examples | **CC0 1.0** | Copied into other mods' source, so it must impose nothing |
| Future `Skizzy.Contract.dll` | MIT | Linkable from GPL, CC-BY-SA and proprietary-ish mods alike |
| Upstream BDArmory PR | BDArmory's CC-BY-SA 2.0 | Contributor terms of that repo |
| Bundled third party (bridge exe) | ModelContextProtocol C# SDK: Apache-2.0. .NET runtime: MIT. | Listed in `THIRD-PARTY-NOTICES.md` |

HullBreach is CC BY-NC-SA 3.0 and NAS/ASW is CC BY-SA 4.0. Any Tier-2 provider added *inside* those mods inherits their licence, which is fine because the attribute file is CC0.

---

## 11. Phased roadmap (revised 2026-11-08)

Effort estimates assume one developer working with coding agents, part-time. Each milestone ends with a demo and its acceptance tests passing. **Every milestone must also satisfy §3.7.**

| Milestone | Theme | Status |
|---|---|---|
| M0 | Spikes and skeleton | Built offline 2026-11-08; the in-game self-checks are pending a run by the user |
| M1 | MVP: core, stock reads, Tier-0, BDArmory core tools | Built offline 2026-11-08; the in-game acceptance test is pending |
| M2 | Flight control, vision, universal layer | Next |
| M3 | Craft building, full BDArmory adapter, battle harness | |
| M4 | Protocol for other mods | |
| M5 | Upstream BDArmory PR | |
| M6 | Hardening and public release | |
| M7+ | Optional extras | |

### M0: Spikes and skeleton (1–2 weeks)
**Scope:**
- the monorepo, `Directory.Build.props`, and a host that loads in KSP
- loopback WebSocket with token, Origin and Host checks
- the dispatcher with budgets and idle wake-up
- the JSON library (own DOM, decided)
- `session.hello`, `host.stats`, `host.state` and `ksp.vessels.list`
- the C# bridge talking to the host

**Spikes.** KSP cannot be run by the agent, so each spike is either answered offline from metadata or decompilation, or implemented as an **in-game self-check** that writes `[Skizzy]` log lines:
- (a) **PAW write hook.** Answered offline (§4.5). Self-check: build a real `BaseField` over a probe object and run the host's PAW writer. It verifies that `OnValueModified` fires, that `onFieldChanged` receives the old value, that symmetry callbacks fire, and that the value reads back.
- (b) **`persistentId` stability** when duplicate craft spawn. Self-check: after every spawn operation and on `onVesselCreate` while a session exists, scan for duplicate vessel `persistentId`s and confirm every returned handle resolves.
- (c) **WebSocket on Unity's Mono.** Self-check: a loopback client inside KSP performs the real handshake with the real token, `session.hello`, and 20 `cap.call ksp.vessels.list` round trips. It logs p50 and p99 latency.
- (d) **`VesselModule.Fields` exists.** Answered offline: yes, a `BaseFieldList Fields` property.
- (e) **BDArmory spawning with no active vessel.** Answered from source (§7.2). Self-check: report whether `SpawnProbe.craft` resolves, plus the active-vessel state. The live test is step 3 of the M1 in-game checklist.
- **Dispatcher timing** (new). Self-check: report the idle mode (sync-context wake vs fallback), the measured cost of a no-op job, and that a burst of 200 queued jobs drains within budget across frames. It logs the per-frame max.

**Acceptance:**
- A round trip `hello` → `vessels.list` in the Flight scene takes less than 20 ms p50.
- With no client connected, overhead is under 0.1 ms per frame (ideally 0, since the pump is disabled) with 0 GC alloc. With one idle client, under 0.2 ms.
- Origin-bearing and token-less connections are rejected (automated test).
- The host survives five scene switches without leaks.

**Risks:** R3, R5 and R7 (§12).

### M1: MVP (2–3 weeks): core plus BDArmory core tools
**Scope:**
- **Host:**
  - WebSocket JSON-RPC, token auth, Origin and Host checks
  - the permission engine: file config, levels, allow/ask/deny per mod and capability, default deny except stock read, session grants, client caps
  - area switches (RB1)
  - the JSONL audit log, the kill switch, and a minimal IMGUI window (connections, areas, per-mod policy grid, activity, token regeneration) with confirmation prompts for `ask`
  - the main-thread dispatcher; operations (`op.*`); dry runs; rate limits; the fault tracker
  - stock read provider (area `ksp.read`): `ksp.mods.list`, `ksp.vessels.list`, `ksp.vessel.get`, `ksp.parts.find`, `ksp.module.describe`, `ksp.module.get`, `ksp.log.tail`
  - Tier-0 writes (area `ksp.module`): `ksp.module.set` (PAW semantics), `ksp.module.invoke`, `ksp.module.action`, with per-target permission resolution
- **BDArmory shim** (area `bdarmory`):
  - `bda.status`, `list_craft`, `stage_craft`
  - `spawn_vessels` (circular), `spawn_template`, `remove_vessels`
  - `list_teams`, `set_team`, `set_guard_mode`
  - `get_weapon_manager`, **`configure_weapon_manager`**: multi-target and parallel-fire fields, read live from the installed DLL
  - `start_competition`, `stop_competition`, `get_competition_state`, `get_scores`, `tail_log`
- **Contract:** the CC0 `SkizzyAttributes.cs` plus an example provider. The host-side scanner arrives in M4; M1 ships a tested reader.
- **Bridge:**
  - stdio and dynamic tool projection, with denied tools hidden
  - name mapping and structured results with full-JSON text blocks
  - `skizzy_status`, `skizzy_search_capabilities`, `skizzy_call`, `skizzy_wait`, `skizzy_cancel`, `skizzy_request_permission`
  - token read from the host's PluginData
  - clean errors when KSP is not running

**Acceptance (also run by hand from Claude Code; the exact in-game checklist is in the README):**
1. With two craft in `AutoSpawn/mvp/A` and `AutoSpawn/mvp/B`, the prompt "spawn mvp on water at lat -0.15 lon -74.2, set teams and guard mode, run an FFA, tell me who won" works:
   - The AI calls `bda_spawn_vessels`, `bda_set_team`, `bda_set_guard_mode`, `bda_start_competition` and `skizzy_wait`, then finishes with `bda_get_scores`.
   - The scores JSON matches `GameData/BDArmory/Logs/<id>.log`: the `ALIVE`/`DEAD`, `HPLEFT`, `ACCURACY` and `RESULT` lines.
2. With BDArmory `cheat` denied, `bda_spawn_vessels` is not listed. A raw-socket call returns `PERMISSION_DENIED`, and the audit log records it.
3. `configure_weapon_manager {multiTargetNum:64, maxMissilesOnTarget:4, parallelFire:true, parallelSalvoSize:8}` returns `parallelSalvoSize` clamped to 4 by BDArmory's callback, and reads back.
4. The kill switch cancels an in-flight spawn operation, and further control calls are denied.
5. A scene change during a competition returns `UNAVAILABLE_IN_SCENE` and does not crash the game.

**Risks:**
- BDArmory spawner coroutines fail silently; always read the instance's `spawnFailureReason`.
- Name deconfliction surprises.
- The MCP client's handling of long-running tools (mitigated with `waitMs` and `skizzy_wait`).

### M2: Flight control, vision, and the universal layer (4 weeks)
**Scope:**
- **Flight (§8.2):** `ksp.flight.*` with in-game setpoint autopilots, the controller arbiter, hand-off to and from BDArmory AI and AtmosphereAutopilot, active-vessel switching, and kill-switch release.
- **Vision (§8.3):** `ksp.camera.*`, with screenshots as MCP image content.
- **Universal layer:**
  - `ksp.query`, action groups, resources, `ksp.config.get`
  - `stream.subscribe` with deltas and dead-bands; the `events.*` ring buffer (2048 entries, cursor, `resync_required`)
  - the Tier-1 annotation loader, with a starter pack for stock destructive events and BDArmory settings
  - the full in-game permissions UI, with part-pack rules
  - ToolbarControl and ClickThroughBlocker as soft dependencies

**Acceptance:** the §8.2 and §8.3 acceptance tests, plus:
- **No HullBreach code changes needed:** read `ModuleHullBreach.floodedDisplay`, set `crushDepth` within its `UI_FloatRange` 0…600, and get rejected outside it with `INVALID_PARAMS {min, max}`. The PAW reflects the change, and symmetry counterparts update.
- Invoking a `Decouple` event is classified `destructive` by heuristic.
- **Performance:** a subscription of 500 parts × 5 fields at 5 Hz costs less than 1 ms per `FixedUpdate`, with zero GC allocation per sample in steady state.

**Risks:**
- Control fights between autopilots (R17).
- GPU stalls from screenshots (R18).
- Callback semantics differ between mods (R3).
- GC (R8).

### M3: Craft building, the full BDArmory adapter, and battle harness v1 (5 weeks)
**Scope:**
- **Craft building (§8.1):** `ksp.editor.*`, headless `craft.*`, `ksp.parts.catalog` and the validation report. The reload-path spike comes first.
- **BDArmory:** the rest of §7.1 (AI, radar, missiles, damage, settings, tournament, the fire tools at `destructive`/ask, events and streams).
- **Harness:** `skizzy-mcp battle run|compare` plus the `bda_run_battle` tool; multi-target and parallel-fire metrics and assertions.

**Acceptance:**
- The §8.1 tests pass.
- The matrix `multiTargetNum` {1, 8, 64} × `maxMissilesOnTarget` {1, 4} × `parallelFire` {off, on} × 3 repeats runs **unattended**. The per-target missile concurrency assertion holds on every sample, and exceptions are captured.
- **Resilience:** a vessel exploding during spawn is retried once, then marked failed.

**Risks:**
- Editor reload path (R19).
- BDArmory churn on the other agent's branch (R10).
- Performance at 1024 targets (R8).

### M4: Protocol for other mods, public draft v0.5 (3 weeks)
**Scope:**
- `Skizzy.Contract.dll` (Tier 3a), host-side Tier-2 scanner and invoker (using the M1 reader), `SkizzyLink.cs` and the JSON-delegate ABI (Tier 3b).
- A conformance suite.
- A **HullBreach Tier-2 provider** in the user's patched HullBreach source.
- A NAS ASW annotation pack.

**Acceptance:**
- HullBreach exposes tools **without referencing any Skizzy assembly** and loads cleanly when Skizzy is absent.
- The conformance suite passes for the host plus two providers.

**Risks:**
- Duck-typing edge cases (forbid generics and overloads).
- Contract versioning (keep `AssemblyVersion` fixed).

### M5: Upstream BDArmory PR (2–3 weeks, plus review time)
**Scope:**
- An in-tree Tier-2 provider.
- Public API additions (§7.3).
- Fixes for the spawner quirks in §7.2.
- The shim's authority handoff.

**Acceptance:**
- The catalog is identical whether it comes from the shim or the PR (diff test).
- There is zero cost when no host is present.

**Risks:**
- Maintainer appetite. Discuss before writing it.

### M6: Hardening and public release (3–4 weeks)
**Scope:**
- CKAN and SpaceDock packaging, a docs site.
- MCP Tasks extension and MRTR elicitation confirmations.
- Optional Streamable HTTP (loopback only).
- **Lockstep mode:** `sim.hold`, `sim.step(n)`, `sim.release`.
- Python client.
- Gated deep reflection.
- Optional scene and save control.

**Acceptance:**
- A fresh install through CKAN works.
- **Security checklist:**
  - Browser connections are refused (Origin check).
  - DNS rebinding is refused (Host check).
  - Path traversal is rejected.
  - A 10k-case fuzzed JSON corpus causes no host exception.
  - The token is not readable through any API.

### M7 and later: optional extras
- A kRPC backend in the bridge (c1).
- Adapters for PhysicsRangeExtender ranges, VesselMover, KerbalEngineer readouts and Trajectories.
- A web dashboard as a direct client.

---

## 12. Risk register

| # | Risk | Likelihood / impact | Mitigation |
|---|---|---|---|
| R1 | **Unity threading violations** (touching game objects off the main thread causes random crashes) | M / H | Single dispatcher. Code review rule: only `any`/`snapshot` code runs off-thread. Debug-build assertions that check `Thread.CurrentThread == mainThread` in every game-touching accessor. |
| R2 | **Frame-time impact** of reflection, snapshots and JSON | M / M | Budgets, adaptive rate, compiled accessors, pooled buffers, snapshot affinity, `host.stats` |
| R3 | **KSP API quirks**: `UI_Control` cast exceptions, scene-specific controls, events that need parameters | H / M | The PAW path is verified from decompilation (§4.5). Every `uiControlFlight`/`uiControlEditor` access is wrapped in try/catch. Per-module fallbacks, Tier-1 overrides, and read-back after every write. |
| R4 | **Security**: a browser or local process abuses the socket; prompt injection | M / H | Loopback only, token, Origin and Host checks, host-side permissions, no network path to change permissions, sandboxed paths, audit, kill switch |
| R5 | **WebSocket on Mono** (`HttpListener` WS support uncertain) | M / L | Own minimal RFC 6455 implementation over `TcpListener` (well understood) |
| R6 | **DLL conflicts** (JSON libraries, duplicate contract DLL) | M / H | Vendor as internal source; contract shipped only by the host; fixed `AssemblyVersion` |
| R7 | **Scene transitions and stale handles** | H / M | Invalidation on `onGameSceneLoadRequested`, the class-name guard in module handles, `NOT_FOUND {stale}` |
| R8 | **Performance at 1024 targets** and large fleets; GC spikes | M / H | Round-robin snapshot builders, no LINQ, measured in M3; the harness records frame-time regressions |
| R9 | **BDArmory internals change** (private members used by reflection) | H / M | Resolve members once with per-tool degradation; move to public APIs through the PR (M5) |
| R10 | **Concurrent edits** by the other agent on the BDArmory branch | H / L | Build against the installed DLL (copied to `refs/`); read field ranges live from `UI_Control` attributes; a field missing from the installed DLL is left out of the schema (fail closed) |
| R11 | **MCP spec and SDK churn** (2026-07-28 was a breaking redesign) | M / M | Keep all MCP code in the bridge; SDK negotiates older versions; the conformance suite pins behaviour |
| R12 | **LLM misuse**: wrong units, runaway loops, oversized results | M / M | Rich descriptions, units, schemas with ranges, rate limits, pagination and summaries (`format: summary`), `skizzy_wait` instead of polling |
| R13 | **BDArmory upstream declines the PR** | M / L | The shim remains fully functional |
| R14 | **Licence ambiguity** (CC-BY-SA BDArmory, GPL kRPC) | L / M | Separate shim with a compatible licence; no kRPC code; CC0 attribute file |
| R15 | **Non-deterministic battles** make A/B tests noisy | H / M | Repeats, CIs, fixed geometry, invariant assertions checked on every sample |
| R16 | **MCP SDK pin and security patches.** The pinned 2.3.0 predates the 2.3.1 security patch. | L / M | 2.3.1 fixes authenticated *stateful HTTP* sessions, and the bridge is stdio-only. Re-evaluate on or after 2026-11-17 (the two-week rule). The exact pin plus a lock file keep builds reproducible. Dependabot-style review comes at each milestone. |
| R17 | **Control fights between autopilots** (Skizzy holds, SAS, BDArmory AI, AtmosphereAutopilot, user input) | M / H | Controller arbiter with one owner per vessel; `CONFLICT` unless `takeOver`; auto-release on user input; the kill switch releases everything (§8.2) |
| R18 | **Screenshot cost**: GPU stalls, texture leaks, oversized payloads | M / M | End-of-frame capture, downscaling, rate and size caps, `Destroy`/`ReleaseTemporary` in `finally`, a leak self-check (§8.3) |
| R19 | **Editor API fragility**: interactive attach code is private; reloads may drop state | H / M | ConfigNode round trip using KSP's own `SaveShip`/`LoadShip`, a batched `ksp.editor.apply`, an undo backup before each edit, and a reload-path spike at the start of M3 (§8.1) |
| R20 | **Runtime end of life**: .NET 8 support ends 2026-11-10 | — | The bridge targets .NET 10 LTS (supported to 2028-11) and is self-contained, so users need no runtime |
| R21 | **Drift from the PAW write path** in a future KSP or mod update | L / M | The sequence is copied from decompiled 1.12.5 code (§4.5) and checked by the in-game self-check each start. Values are read back after every write. |
| R22 | **Antivirus false positives** on the single-file exe | M / L | Publish SHA-256 sums; no packing or obfuscation; the user can build from source with one command |
| R23 | **AI-driven work hurts game stability** (the user's top concern) | M / H | The §3.7 requirements, with self-checks and acceptance gates on every milestone; areas off by default; the fault tracker auto-disables misbehaving tools |

---

## 13. Prior art: what we reuse and why we diverge

Researched 2026-11-07. Repository facts come from GitHub; details are in the session's research notes.

| Project | What it is | Reuse | Diverge because |
|---|---|---|---|
| **caseys/ksp-mcp** (TypeScript, about 32 tools, kOS telnet plus MechJeb, MIT, early development) | MCP through kOS terminal scraping | One library behind both the CLI and MCP with a parity check (we do the same for `skizzy-mcp battle` and the MCP tools). Sentinel-based completion detection. | Screen-scraping; coarse maneuver macros; dev-build dependencies; no permissions; no combat |
| **codecraft3r/ksp-mcp** (C# / .NET 10 with the C# MCP SDK, 12 tools, kOS plus craft-file generation) | Craft generation and kOS scripts | Craft-file parsing ideas for `bda.list_craft` and headless `craft.*` (§8.1). Proof that the C# SDK works for a KSP bridge, which is now our stack too. | Windows-path hard-coding; kOS pauses with the game; tiny scope |
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

## 14. Open questions for the user

**Resolved on 2026-11-08** (user decisions and accepted defaults):

| # | Question | Decision |
|---|---|---|
| 1 | Name | **skizzy-protocol** (§2) |
| 2 | Bridge language | **C#**, ModelContextProtocol C# SDK 2.3.0, .NET 10, self-contained exe (§10.4) |
| 3 | Manual weapon fire | **Allowed**, but `destructive` with default policy **ask** (§7.1) |
| 4 | Parallel-fire field names | Read from source and the installed DLL: `parallelFire`, `parallelSalvoSize`, `parallelSalvoStagger` (§1.3) |
| 5 | Naval mods installed? | HullBreach, NAS, MNWS (`Johnny`), IronSeasKANES and Maritime Pack are **now installed** (§1.3). Nothing was installed by this project. |
| 6 | KSP process control | **Never.** The harness does not restart KSP or swap DLLs (§9.4). |
| 9 | Denied tools | **Hidden** (bridge `--show-denied` to override) |
| 11 | Licences | **MIT** for our code, **CC0** for the copy-in attribute file (§10.6) |

**Still open:**
7. **Test site and save.** A dedicated save (for example `saves/naval-range`) with a parked vessel at a water site, for unattended runs? M1's in-game checklist verifies spawning with no active vessel.
8. **Upstream.** Is there a relationship with the BDArmory maintainers? Should the M5 PR be pitched early (before M3)? It would also carry the spawner fixes in §7.2.
10. **Multiple clients.** Should a human be able to fly while an AI controls other vessels? The arbiter (§8.2) supports it, but the policy defaults matter.
12. **Vision defaults.** Is `hideUi:false` (screenshots include KSP's UI) the right default? Hiding the UI causes a one-frame flicker.
13. **AtmosphereAutopilot hand-off.** Its API is public but undocumented (`TopModuleManager.activateAutopilot(Type)`). Is reflection with fail-closed acceptable, or should hand-off support only BDArmory AI and manual control?
14. **Bridge pin refresh.** Move to ModelContextProtocol 2.3.1 once it passes the two-week rule (2026-11-17)?

---

## Appendix A: verified BDArmory API anchors (branch `multitarget-1.13.1`, HEAD `01d0c9676`)

Re-verified 2026-11-08 against the source (read-only) and the installed `BDArmory.dll` metadata.

| Symbol | Location |
|---|---|
| `[assembly: KSPAssembly("BDArmory", 1, 0)]`, `AssemblyVersion("1.13.1.0")` | `Properties/AssemblyInfo.cs:20-31` |
| `MissileFire.multiTargetNum` / `multiMissileTgtNum` / `maxMissilesOnTarget` (all `float`, `UI_FloatRange` step 1), consts `maxAllowableTargets = 1024f`, `maxAllowableMissilesOnTarget = 128f` | `Control/MissileFire.cs:680-694` |
| `MissileFire.parallelFire` (bool), `parallelSalvoSize` (float 1…128), `parallelSalvoStagger` (float 0…10), `maxParallelSalvoStagger = 10f`, `OnParallelSalvoSettingsChanged(BaseField, object)` | `:696-708`, `:1680` |
| `targetScanInterval`, `fireBurstLength`, `AutoFireCosAngleAdjustment`, `guardAngle`, `guardRange` (`UI_FloatSemiLogRange`), `gunRange` (`UI_FloatPowerRange`) | `:649-676` |
| `isVIP`, `weaponChannel` | `:885-891` |
| `MissileFire.ToggleGuardMode()`, `guardMode` | `:893`, `:1042-1044` |
| `MissileFire.Team` (property), `SetTeam(BDTeam)`, `SetTeamByName` (empty stub), `static event OnChangeTeam` | `:1135`, `:1175`, `:1208`, `:1173` |
| `MissileFire.FireCurrentMissile(...)` (private) | `:4570` (v0.1 anchor; re-check in M3) |
| `MissileFire.GetMissilesAway(TargetInfo)` | `:2275` (v0.1 anchor; re-check in M3) |
| `BDTeam.Get(string)`, `Name`, `Neutral`, `Allies` | `Competition/BDTeam.cs:14-28` |
| `BDArmorySetup.Instance`, `Version`, `Teams`, `SaveConfig()` | `UI/BDArmorySetup.cs:101`, `:104`, `:217`, `:963` |
| `VesselModuleRegistry.GetMissileFires(Vessel)`, `GetMissileFire(Vessel, bool)`, `GetModules<T>`, `GetModule<T>` | `Utils/VesselModuleRegistry.cs:521`, `:533`, `:339`, `:374` |
| `VesselExtensions.ActiveController(this Vessel)` → `ActiveController.WM`/`.AI` | `Extensions/VesselExtensions.cs:360-361`, `Utils/VesselModuleRegistry.cs:955-984` |
| `BDACompetitionMode.Instance`, `StartCompetitionMode(float, bool, string, CompetitionType)`, `StartCompetitionNow(float)`, `StopCompetition()`, `ResetCompetitionStuff`, `LogResults` | `Competition/BDACompetitionMode.cs:29`, `:352`, `:338`, `:392`, `:454`, `:3306` |
| `competitionStarting`, `competitionIsActive`, `CompetitionID`, `competitionTag`, `competitionStartTime`, `competitionStartFailureReason`, `competitionStatus` | `:313-334`, `:36-39` |
| `CompetitionType { FFA, SEQUENCED, WAYPOINTS }`, `CompetitionStartFailureReason` | `:22-23` |
| `CompetitionScores.ScoreData`, `deathOrder`, `survivingTeams`, `deadTeams`, `competitionResult`; `ScoringData` (public fields only); `AliveState`, `SurvivalState` | `Competition/Scoring.cs:17-33`, `:948-1052` |
| Results log path `GameData/BDArmory/Logs/<CompetitionID>[-tag].log` (tournaments: `Logs/Tournament <id>/Round <n>/`) | `Competition/Scoring.cs:934-945` |
| `CircularSpawning.Instance`, `SpawnAllVesselsOnce(CircularSpawnConfig)`, `CancelSpawning()` | `VesselSpawning/CircularSpawning.cs:27`, `:103`, `:47` |
| `SpawnConfig`/`CircularSpawnConfig`/`CustomSpawnConfig`/`CustomVesselSpawnConfig` constructors and fields | `VesselSpawning/SpawnConfig.cs:17-152` |
| `CustomTemplateSpawning.Instance`, `SpawnCustomTemplate(CustomSpawnConfig)`, `CancelSpawning()` | `VesselSpawning/CustomSpawnTemplate.cs:23`, `:95`, `:62` |
| `VesselSpawnerStatus.vesselsSpawning`/`vesselSpawnSuccess`/`spawnFailureReason`; `VesselSpawnerBase.AutoSpawnFolder` | `VesselSpawning/VesselSpawnerBase.cs:22-55` |
| `SpawnUtils.ResetVesselNamingDeconfliction`, `RemoveVessel`, `RemoveAllVessels` (coroutine), `removingVessels`, `CheckAIWMPlacement`, `SaveTeams` | `VesselSpawning/SpawnUtils.cs:296`, `:463`, `:464`, `:462`, `:469`, `:115` |
| `VesselSpawner.spawnProbeLocation`, `SpawnSpawnProbe` | `VesselSpawning/VesselSpawner.cs:24`, `:47` |
| `BDATournament.Instance.tournamentStatus` (`TournamentStatus`), `tournamentID`, `currentRound`, `currentHeat` | `Competition/BDATournament.cs:1576-1594` |
| `BDArmorySettings.COMPETITION_DURATION`, `COMPETITION_DISTANCE`, `COMPETITION_START_NOW_AFTER` | `Settings/BDArmorySettings.cs:138`, `:154`, `:157` |
| `BDATargetManager.TargetList`, `FiredMissiles`, `TargetPriorityScore` | `UI/BDATargetManager.cs:1356`, `:30`, `:1602` (v0.1 anchors) |
| `VesselRadarData.GetLockedTargets`, `displayedTargets` (private), `TryLockTarget` | `Radar/VesselRadarData.cs:158`, `:141`, `:964` (v0.1 anchors) |
| `BDModuleSurfaceAI.SurfaceTypeName` (includes Submarine), `CombatAltitude` | `Control/BDModuleSurfaceAI.cs:76-91` |
| `BDGenericAIBase.pilotEnabled`, `ActivatePilot()`, `DeactivatePilot()` | `Control/BDGenericAIBase.cs:27`, `:172`, `:213` |
| `HitpointTracker.Hitpoints`, `GetMaxHitpoints` | `Damage/HitpointTracker.cs:27-29`, `:1072` |
| `BDATournament.SetupTournament` / `RunTournament` / `StopTournament` | `Competition/BDATournament.cs:1687-1735` |

## Appendix B: verified KSP 1.12.5 API anchors (installed `Assembly-CSharp`, 2026-11-08)

Checked by reading assembly metadata, plus decompiling `BaseField<T>` and the `UIPartActionFieldItem` family.

| Symbol | Notes |
|---|---|
| `BaseField<T>.SetValue(object, object)` | `FieldInfo.SetValue` then `OnValueModified(newValue)`; returns false (and logs) on a type mismatch |
| `BaseField<T>.OnValueModified` | `event Callback<object>` |
| `UIPartActionFieldItem.SetFieldValue(object)` | The PAW write sequence (§4.5): symmetry via `affectSymCounterparts`, `onSymmetryFieldChanged`, `onFieldChanged(field, old)`, `onEditorShipModified` |
| `UI_Control.onFieldChanged`, `onSymmetryFieldChanged` (`Callback<BaseField, object>`), `affectSymCounterparts`, `controlEnabled`, `requireFullControl`, `suppressEditorShipModified`, `scene` | public fields |
| `UI_FloatRange.minValue/maxValue/stepIncrement`, `UI_FloatEdit.minValue/maxValue/incrementLarge/Small/Slide`, `UI_ChooseOption.options/display`, `UI_Toggle.enabledText/disabledText` | public fields |
| `BaseField(KSPField, FieldInfo, object host)` | A public constructor, used by the PAW self-check |
| `BaseFieldList<,>.SetValue(string, object)`, `GetValue(string)`, `this[string]`, `this[int]`, `Count` | used for symmetry counterparts |
| `BaseEvent.Invoke()`, `active`, `guiActive`, `guiActiveEditor`; `BaseAction.Invoke(KSPActionParam)`, `actionGroup`, `active`; `KSPActionParam(KSPActionGroup, KSPActionType)` | events and actions |
| `VesselModule.Fields` | `BaseFieldList`. **Exists** (spike d). |
| `FlightGlobals.FindVessel(uint, out Vessel)`, `FindLoadedPart(uint, out Part)`, `Vessels`, `ActiveVessel`, `SetActiveVessel(Vessel)`, `fetch` | handle resolution and vessel switching |
| `Vessel.OnFlyByWire`, `OnPreAutopilotUpdate`, `OnAutopilotUpdate`, `OnPostAutopilotUpdate` (`FlightInputCallback`), `ctrlState`, `Autopilot` (`VesselAutopilot`: `SetMode`, `CanSetMode`, `Enable`, `Disable`), `ActionGroups` (`SetGroup`, `ToggleGroup`) | flight control (§8.2) |
| `FlightInputHandler.state` (`FlightCtrlState`: `mainThrottle`, `pitch`, `yaw`, `roll`, `wheelThrottle`, `wheelSteer`) | flight control |
| `KSP.UI.Screens.StageManager.ActivateNextStage()` | staging |
| `EditorLogic.fetch.ship` (`ShipConstruct`), `SetBackup()`, `SpawnConstruct(ShipConstruct)`, `static LoadShipFromFile(string)`, `symmetryMode`, `symmetryMethod`; `ShipConstruct.SaveShip()`/`LoadShip(ConfigNode)`; `ShipConstruction.SaveShipToPath(...)`, `GetShipsPathFor(...)`, `LoadShip(string)` | craft building (§8.1) |
| `FlightCamera.SetMode(Modes)`, `fetch.SetTargetVessel(Vessel)`; `UnityEngine.ScreenCapture.CaptureScreenshotAsTexture()`; `ImageConversion.EncodeToJPG(Texture2D, int)`/`EncodeToPNG` | vision (§8.3) |
| `PopupDialog`, `MultiOptionDialog`, `DialogGUIButton`; `ApplicationLauncher.Instance.AddModApplication(...)`, `ApplicationLauncher.Ready` | UI |
| `AssemblyLoader.loadedAssemblies` (`LoadedAssembly`: `name`, `assembly`, `url`, `path`, `versionMajor`, `versionMinor`) | `ksp.mods.list` |
| `PartLoader.LoadedPartsList` (`AvailablePart.name`, `title`, `partPrefab`, `partConfig`, `partUrl`) | describing module types without an instance; part catalog |
