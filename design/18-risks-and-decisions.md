# 18 Risks, rejected alternatives, decisions for the user

## 1. Risk register

| # | Risk | L/I | Mitigation |
|---|---|---|---|
| R1 | Unity threading violations from new engines | M/H | engines run only in FixedUpdate/Update; network threads see immutable snapshots; debug assertions; FakeKsp tests exercise the same code paths |
| R2 | Frame-time and GC regressions as features pile up (the user's main fear) | H/H | per-engine budgets and counters from day one, `host.stats`, perf self-check gates in every milestone, `perf` channel in recordings, soak tests, CI grep for allocations on `skizzy:hot` paths |
| R3 | BDArmory internals change under the shim (other agent committing on the fork; upstream BDA Plus moving) | H/M | fork-first hooks with reflection fallbacks resolved once at startup, per-capability fail-closed, `bda.hooks` truthfulness, build against the installed DLL, conformance tests on both modes |
| R4 | `OnFlyByWire` ordering makes the mixer unreliable without F8 | M/M | re-registration on `pilotEnabled` edges and every second; F8 lands in M4 if merged; documented |
| R5 | Fog-of-war picture diverges from what BDA's AI actually "sees" | M/M | TPE uses BDA's own `detected/detectedTime` as truth source; god view for debugging; tests on FakeKsp with known truth |
| R6 | Track id churn when KSP reassigns `persistentId` or vessels split (debris, fighters detaching) | M/M | identity by `Vessel` reference + name fallback; `track.split` events; re-index on `onVesselCreate` |
| R7 | LLM latency too high for tactical decisions | H/M | agents shape engagements, in-game executors and BDA fire regardless; event-driven turns; Haiku for units; slow-mo on urgent triggers; budgets stop runaway loops |
| R8 | LLM cost surprises | M/M | per-actor dollar budgets, cache-stable prompts, compact text, digest cadence; cost shown live on the map |
| R9 | Forced tool choice removed on Opus 5.5 / Sonnet 5.5 causes agents to answer in prose instead of acting | M/L | `auto` + strict schemas + explicit instructions; orchestrator treats a turn without tool calls as a report |
| R10 | Lease logic becomes a usability nightmare (constant `LEASE_HELD`) | M/M | sane defaults (BDA owns everything until someone asks), delegation, clear errors with holder names, map lease panel, doctrine-driven preemption rules |
| R11 | Editor reload path drops state or spawns floating subassemblies | H/M | reload spike first in M5; headless path independent of the editor; B9PartSwitch/TweakScale state carried in the document; round-trip diff tests |
| R12 | Blueprint assembler needs curated hull slots for every hull family | H/M | ship slots for the pack's main hulls; `craft.probe_slots` fallback; agents can still place parts by coordinates with validation |
| R13 | Carrier arrested recovery is beyond BDA AI's landing ability | H/M | P2, scoped as launch + marshal in v1; recovery BDA-assisted or human; measured success rate reported honestly |
| R14 | Recording volume and disk I/O on long battles (`battle.detailed` ≈ 1.2 GB/h) | M/L | adaptive rates, columnar layouts, lz4 compaction, manual prune and a 20 GB warning |
| R15 | Multi-agent through one bridge: actor spoofing or confusion | M/M | role clamps in the permission file, bridge asserts identity per MCP session, audit per actor |
| R16 | Scene changes and quick-loads during battles corrupt orders/leases | M/M | suspend/resume rules, handles invalidated, orders `suspended` (reason `scene`), `sim.scenario.snapshot` as the supported way to save a battle position (`20-runtime-lifecycle.md` §5) |
| R17 | KSP `Vessel.OnFlyByWire` for non-active vessels only runs when loaded/unpacked; PRE range changes mid-battle | M/M | holds report `inactive` when the vessel is packed; PRE range is a scenario parameter and checked in preflight |
| R18 | Harmony-based stopgaps destabilise stock installs | L/M | Harmony mode off by default, only for the private-member hooks, removed when the upstream PR lands |
| R19 | Avalonia/SkiaSharp performance with thousands of individually drawn symbols, trails and labels | M/M | sprite atlas + `DrawAtlas`, density sizing and label placement on a worker, batched trails, spike at the start of M3; fallback to shorter trails and a lower label budget, never to merging tracks |
| R20 | Determinism expectations for A/B | H/L | statistics with CIs, repeats, invariants on every sample; lockstep for structure only; documented |
| R21 | MCP spec/SDK churn | M/M | all MCP code in the bridge; conformance tests; pinned SDK with a review date |
| R22 | SubTrim/HullBreach API drift (user's own mods) | L/L | the user controls them; adapters resolve by name and fail closed |
| R23 | `UnityEngine.Random` seeding changes BDA behaviour subtly | L/L | P3 patch behind a setting, off by default |
| R24 **[R]** | Stock BDA drops or overwrites Skizzy target assignments (missile routine, team assist) | H/M | re-issue executor with back-off and `assignment_lost` (`07-bdarmory-integration.md` §1.4); F1 makes assignments sticky |
| R25 **[R]** | CommNet/RemoteTech/Kerbalism make vessels uncontrollable, silently stopping BDA guard and player input | M/M | `not_controllable` in SITREP and events, doctor check, holds refused unless allowed (`20-runtime-lifecycle.md` §9) |
| R26 **[R]** | A Unity API assumed thread-safe is not (`EncodeArrayToJPG`) | M/L | M0 spike; main-thread fallback within budget |
| R27 **[R]** | Assembly conflicts (KSPBurst's `System.Runtime*`, netstandard) break the host at load | M/H | net48 build against KSP's `Managed` folder, no external serialisers in-game, CI reference check, doctor check |
| R28 **[R]** | `Dock.Avalonia` does not fit Avalonia 11 or the layout needs | L/M | M3 spike; fallback of splitters and detachable windows |
| R29 **[R]** | Duplicate vessel names corrupt BDA scores (scores are keyed by name) | H/M | automatic deconfliction at competition start (`20-runtime-lifecycle.md` §6) |

## 2. Rejected alternatives

| Area | Rejected | Why |
|---|---|---|
| Foundation | kRPC services; MCP server inside KSP | static services, no permissions, GPL questions; DLL conflicts and spec churn in-process (PLAN §3) |
| Encoding | JSON only | 1000 tracks × 5 Hz ≈ 2–3 MB/s of text and parse garbage in Python; msgpack is 5–10× smaller and zero-copy |
| Encoding | Protobuf/FlatBuffers | code generation for every schema change; no in-game library; msgpack keeps schemas in JSON Schema and still has Python/C# libs |
| Map | Browser/TypeScript web app | the user rejected TypeScript; a C# Avalonia app shares types and renders fast |
| Map | In-game IMGUI only | IMGUI cannot carry 2525 layers, planning tools and replay at acceptable cost; kept as a thin overlay |
| Scripting in-game | MoonSharp/Lua, Python-in-Unity | GC pressure and sandboxing; C# plug-ins + BTs cover the need; P3 if demanded |
| Autopilots | hand-flying over MCP; 50 Hz network control as the primary mode | latency and LLM speed; holds and BDA AI run in-process; the direct channel exists with a dead-man for research |
| Recording | Parquet in-game; SQLite in-game; CSV only | no Parquet library for Mono/net48 without heavy deps; native SQLite dep risky in KSP; CSV cannot hold nested records |
| Recording | bridge-side sampling (host streams, bridge writes) | doubles socket traffic and fails when no bridge is connected; the host owns sampling, writes locally |
| Hooks | shim-only reflection forever | brittle, private members, no events; the user owns a fork that already ships via CKAN |
| Hooks | Harmony as the primary mechanism | reversible but intrusive; kept only as an optional stopgap for stock installs |
| Permissions | PLAN's off-by-default areas | contradicts the user's explicit wish; Battle Lab preset instead, with `destructive` ask |
| Multi-agent transport | one stdio bridge process per agent only | works for 2–3 agents; Streamable HTTP on loopback scales to 10+ with per-session identity |
| Picture | per-unit `CanSeeTarget` calls for fog | O(N²) and allocating; BDA already stores per-team detection |
| Orders | free-text orders to units | unexecutable by in-game code; typed orders with schemas, free text only as notes |
| Memory | vector database for agent memory | small, human-editable files suffice; revisit if memories grow |
| Time | KSP save/load as battle snapshot | unreliable mid-combat (missiles, debris, 200 vessels); scenario snapshot/respawn instead |
| Encoding (in-game) **[R]** | MessagePack-CSharp inside KSP | needs `System.Memory`/`Unsafe`, which KSPBurst already ships in other versions; an in-house subset codec is ~600 lines |
| Map **[R]** | clustering/aggregating tracks by default | the user wants every missile visible; aggregation is an optional Counts view |
| Parallelism **[R]** | Unity Jobs/Burst for the TPE | blittable world copies every tick and a hard KSPBurst dependency for a sub-millisecond gain |
| Hooks **[R]** | depending on BDA source line numbers | lines move with every commit; members are resolved by name and checked by `bda.hooks` |
| Bridge name | keep `skizzy-mcp.exe` as the only exe | it is now a CLI with runner/exporter/metrics/map; `skizzy.exe` with an alias |

## 3. Decisions

**[R] Consolidated by review pass 1.1.** Pass 1.0 listed 17 open questions. Most had a clear answer from `IDEAS.md`, the installed mod set or the code, so the review decided them (§3.2). Four need the user (§3.1). Each has a default the design assumes until the user answers.

### 3.1 Decisions for the user

| # | Question | Recommended default | Why |
|---|---|---|---|
| U1 | Who coordinates the BDArmory fork patches, and when do they go upstream? | Small separate PRs to `BDArmory-Skizzy` in the order F1/F2/F5 (M2), then F3/F4/F6 (M4), F8–F12 later; opened only after the current multi-target/leak work is merged; the user reviews each; pitch BDA Plus upstream after M6, when the hooks have been used in real battles | the fork is being changed by another agent right now, and every patch has a shim fallback, so nothing in Skizzy waits on them |
| U2 | How much may LLM agents spend per battle? | Commander $3, each unit agent $0.50, whole orchestrator $8 per battle; batch runs never use LLMs unless the scenario explicitly opts in | caps stop runaway loops; the SAG scenario estimate is under $1, so the caps leave headroom |
| U3 | Which hull families get blueprint slot templates first? | LBP destroyer hull, LBP carrier hull, and one MNWS/Maritime hull | these are the hulls in the user's naval pack; everything else uses `craft.probe_slots` |
| U4 | Order of milestones after M2 | M3 tactical map, then M4 control depth and C2 basics, then M5 craft building | the user named the map and the compact picture among the most-missed items; craft building is the largest and least urgent block |

### 3.2 Decided by the review (no action needed; say so to change any)

| # | Topic | Decision | Why |
|---|---|---|---|
| D-1 | Default permissions | **Battle Lab** preset: all areas on except deep reflection; read/control allow; cheat allow for spawn, placement, scenarios and BDA settings, ask for `INFINITE_*` and gravity changes; destructive ask; unsafe deny | matches IDEAS ("editor, flight, screenshots allowed; destructive ask") |
| D-2 | Map technology | Avalonia + SkiaSharp desktop app in C# | the user rejected TypeScript; one language; GPU rendering |
| D-3 | Bridge name | `skizzy.exe` with subcommands; `skizzy-mcp.exe` kept as an alias | the exe now does far more than MCP |
| D-4 | Competition as referee | C2 battles start a BDA competition for scoring only (no killer GM, no duration) | reuses BDA's kill/score logic instead of duplicating it |
| D-5 | Test range | `saves/skizzy-range` created from a shipped template by the wizard or `skizzy doctor --fix --create-range`; open water at lat -0.15, lon -74.20; loading it from a running game is `ask` | a known site makes trials comparable; loading a save is destructive |
| D-6 | Orchestrator stack | Python with the `anthropic` SDK (tool runner over the bridge's MCP tools) | Python is the scripting language of the project; no Agent SDK dependency needed |
| D-7 | Network exposure | loopback only; a second human uses an SSH tunnel; LAN/TLS is P3 | smallest attack surface |
| D-8 | Harmony stopgap | optional, off by default, only for stock BDA Plus installs without the fork; removed when hooks land upstream | the user's install has the fork |
| D-9 | Recording format and retention | `.skzrec` + JSONL, CSV/JSONL direct sinks, Parquet on export; **no automatic deletion**, manual prune plus a 20 GB warning | auto-deleting data the user may want to compare is worse than a disk warning |
| D-10 | Screenshot `hideUi` default | `false` | hiding the UI flickers for a frame; agents rarely need it |
| D-11 | In-game Lua scripting | rejected (BTs, C# plug-ins, external Python cover it) | GC and sandboxing risk |
| D-12 | Fog-of-war realism defaults | radar-only tracks stay `unknown` until 40 km (air) / 20 km (surface) or a hostile act; comms model off; all tunable in doctrine | sensible defaults, no user input needed |
| D-13 | MCP SDK pinning | newest release at least two weeks old at build time, reviewed each milestone | avoids both stale pins and day-zero releases |
| D-14 | SubTrim integration | v1 writes `manualDepth`/`targetDepth` through the PAW path; `IDepthCommandSource` is an optional v2 needing a SubTrim priority option | works today without touching SubTrim |
| D-15 | Kill switch on the active vessel | also deactivates the BDA pilot there (guard unchanged); all other vessels keep BDA AI and guard | "always restores manual control" for the ship the human is in, without killing the fleet |
| D-16 | Staging permission | resolver: staging is control unless the next stage fires a weapon or strands crew | a prompt on every rocket stage would make flight unusable |
| D-17 | Renaming vessels | control, not destructive | reversible |
| D-18 | Save files | Skizzy never writes `.sfs` in v1; battle positions are scenario snapshots | save corruption is the worst failure a mod can cause |
| D-19 | CommNet / `IsControllable` | warn in `skizzy doctor` and the SITREP, never change the user's CommNet settings | it is a game setting, not ours |
| D-20 | Map clarity | individual tracks always; aggregation only as an optional Counts view (user correction, 2026-11-08) | the user wants to see every missile |
| D-21 | Map docking | `Dock.Avalonia` (MIT), with a splitter-and-window fallback if it fails the M3 spike | docking is a solved problem; do not hand-build it |
| D-22 | In-game parallelism | at most one worker thread for pure math on snapshot copies, only where measured, not before M8; no Unity Jobs/Burst | Unity/KSP APIs are main-thread only; complexity must not reach M0–M3 |
| D-23 | LLM models | `claude-haiku-5-5` for group/unit agents, `claude-opus-5-5` (effort `high`) for the commander and builder, `claude-sonnet-5-5` for the analyst | cost and latency per role |
| D-24 | Take-over and hand-back defaults | Default channels are steer, weapons and targeting; take-over changes nothing physically until the human commands (`hold_last`, locks and fire authority kept); release resumes the mission, which re-plans from the unit's position; one-shot interventions return automatically; human take-overs survive a map disconnect for 120 s; no automatic release, only a reminder after 5 min | the user wants mission command and micromanagement together with seamless switching (2026-11-08); surprises at the moment of switching are the main way such a feature fails |

## 4. Open technical spikes

Spikes are now the first tasks of each milestone in `17-milestones.md`. The full list:

- M0: `EncodeArrayToJPG` off the main thread; `AsyncGPUReadback` support on the user's graphics API; `UnitySynchronizationContext` idle wake; `NoStdLib` build against KSP's `Managed` folder.
- M1: `OnFlyByWire` ordering with BDA's re-registration; placement with KJR installed.
- M2: VRD `displayedTargets` reflection cost at 200 radars; `TargetInfo.detected` semantics for allied teams; BRAA test vectors; in-house msgpack zero-alloc on Mono.
- M3: `Dock.Avalonia` on Avalonia 11; Skia `DrawAtlas` throughput; Avalonia headless rendering in CI.
- M4: `CommandFlyTo` arrival behaviour for the surface AI; aircraft `FlyTo` orbit radius vs leg geometry.
- M5: editor reload path and its time at 300 parts; GM estimate vs observed roll period.
- M6: Streamable HTTP session identity in the MCP SDK; prompt-cache hit rates with digest injection; compaction over a 2-hour battle.
- M8: ACA catapult attach sequence; passive sonar bearing noise model from BDA's acoustic signature; whether any in-game parallel offload saves ≥ 0.2 ms.
