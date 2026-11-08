# 18 Risks, rejected alternatives, decisions for the user

## 1. Risk register

| # | Risk | L/I | Mitigation |
|---|---|---|---|
| R1 | Unity threading violations from new engines | M/H | engines run only in FixedUpdate/Update; network threads see immutable snapshots; debug assertions; FakeKsp tests exercise the same code paths |
| R2 | Frame-time and GC regressions as features pile up (the user's main fear) | H/H | per-engine budgets and counters from day one, `host.stats`, perf self-check gates in every milestone, `perf` channel in recordings, soak tests, CI grep for allocations on `skizzy:hot` paths |
| R3 | BDArmory internals change under the shim (other agent committing on the fork; upstream BDA Plus moving) | H/M | fork-first hooks with reflection fallbacks resolved once at startup, per-capability fail-closed, `bda.hooks` truthfulness, build against the installed DLL, conformance tests on both modes |
| R4 | `OnFlyByWire` ordering makes the mixer unreliable without F8 | M/M | re-registration on `pilotEnabled` edges and every second; F8 lands in M2 for the fork; documented |
| R5 | Fog-of-war picture diverges from what BDA's AI actually "sees" | M/M | TPE uses BDA's own `detected/detectedTime` as truth source; god view for debugging; tests on FakeKsp with known truth |
| R6 | Track id churn when KSP reassigns `persistentId` or vessels split (debris, fighters detaching) | M/M | identity by `Vessel` reference + name fallback; `track.split` events; re-index on `onVesselCreate` |
| R7 | LLM latency too high for tactical decisions | H/M | agents shape engagements, in-game executors and BDA fire regardless; event-driven turns; Haiku for units; slow-mo on urgent triggers; budgets stop runaway loops |
| R8 | LLM cost surprises | M/M | per-actor dollar budgets, cache-stable prompts, compact text, digest cadence; cost shown live on the map |
| R9 | Forced tool choice removed on Opus 5.5 / Sonnet 5.5 causes agents to answer in prose instead of acting | M/L | `auto` + strict schemas + explicit instructions; orchestrator treats a turn without tool calls as a report |
| R10 | Lease logic becomes a usability nightmare (constant `LEASE_HELD`) | M/M | sane defaults (BDA owns everything until someone asks), delegation, clear errors with holder names, map lease panel, doctrine-driven preemption rules |
| R11 | Editor reload path drops state or spawns floating subassemblies | H/M | reload spike first in M5; headless path independent of the editor; B9PartSwitch/TweakScale state carried in the document; round-trip diff tests |
| R12 | Blueprint assembler needs curated hull slots for every hull family | H/M | ship slots for the pack's main hulls; `craft.probe_slots` fallback; agents can still place parts by coordinates with validation |
| R13 | Carrier arrested recovery is beyond BDA AI's landing ability | H/M | P2, scoped as launch + marshal in v1; recovery BDA-assisted or human; measured success rate reported honestly |
| R14 | Recording volume and disk I/O on long 1000-vessel battles | M/L | adaptive rates, columnar layouts, lz4 compaction, retention policy |
| R15 | Multi-agent through one bridge: actor spoofing or confusion | M/M | role clamps in the permission file, bridge asserts identity per MCP session, audit per actor |
| R16 | Scene changes and quick-loads during battles corrupt orders/leases | M/M | suspend/resume rules, handles invalidated, orders `suspended_scene`, `sim.scenario.snapshot` as the supported way to save a battle position |
| R17 | KSP `Vessel.OnFlyByWire` for non-active vessels only runs when loaded/unpacked; PRE range changes mid-battle | M/M | holds report `inactive` when the vessel is packed; PRE range is a scenario parameter and checked in preflight |
| R18 | Harmony-based stopgaps destabilise stock installs | L/M | Harmony mode off by default, only for the private-member hooks, removed when the upstream PR lands |
| R19 | Avalonia/SkiaSharp performance on the user's GPU at 1000 tracks | L/M | cached pictures per symbol, culling, benchmark in M3; fallback to lower map rate |
| R20 | Determinism expectations for A/B | H/L | statistics with CIs, repeats, invariants on every sample; lockstep for structure only; documented |
| R21 | MCP spec/SDK churn | M/M | all MCP code in the bridge; conformance tests; pinned SDK with a review date |
| R22 | SubTrim/HullBreach API drift (user's own mods) | L/L | the user controls them; adapters resolve by name and fail closed |
| R23 | `UnityEngine.Random` seeding changes BDA behaviour subtly | L/L | P3 patch behind a setting, off by default |

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
| Bridge name | keep `skizzy-mcp.exe` as the only exe | it is now a CLI with runner/exporter/metrics/map; `skizzy.exe` with an alias |

## 3. Decisions for the user

Decisions the design could not make alone. Each has a default the design assumes.

1. **Default permissions and areas.** Design assumes the **Battle Lab** preset: all areas on except deep reflection; read/control allow; cheat allow for spawn/settings/placement/scenarios; destructive **ask**; unsafe deny. Confirm, or pick `Test Director`/`Full Combat`.
2. **Fork-first hooks.** The design lands patches F1–F12 in `BDArmory-Skizzy` (the user's fork) starting in M1, with shim fallbacks. Another agent is committing on that repo now: who coordinates the patch PRs and in which order? Default: separate small PRs per hook group (F1/F2/F5 first), reviewed by the user, after the current memory-leak work is merged.
3. **Tactical map technology.** Avalonia + SkiaSharp desktop app in C#. The alternative that keeps a browser UI would need JS/TS (rejected) or a Python UI (slower, weaker rendering). Confirm Avalonia.
4. **Bridge rename** to `skizzy.exe` with subcommands, keeping `skizzy-mcp.exe` as an alias. Cosmetic; confirm.
5. **Competition as referee.** C2 battles always start a BDA competition (scoring only, no killer GM, no duration) so kills/scores exist. Acceptable? Alternative: our own kill/score tracker (duplicates BDA, P2).
6. **Test range save.** Create `saves/skizzy-range` (open water at `lat -0.15, lon -74.20`, PRE 100 km, parked probe) and let `craft.trial`/batch runs require it. Confirm the site and whether the runner may `ksp.game.load` it (destructive) when it is not loaded.
7. **LLM models and budgets.** `claude-haiku-5-5` for group/unit agents, `claude-opus-5-5` (effort `high`) for the commander, `claude-sonnet-5-5` or Opus for the analyst. Default dollar caps: commander $3 per battle, each unit agent $0.50, orchestrator total $8. Confirm models and caps.
8. **Orchestrator stack.** Python with the `anthropic` SDK (Tool Runner over MCP tools via the bridge) rather than the Claude Agent SDK. Confirm, or ask for an Agent SDK variant.
9. **Multiplayer/humans.** v1 is loopback only; a second human uses an SSH tunnel to the map port. LAN binding with TLS is P3. Acceptable?
10. **Harmony stopgap.** Allow an optional Harmony mode (off by default) for stock BDA Plus installs lacking the fork hooks? Default: yes, optional, removed when the upstream PR lands.
11. **Recording format and retention.** `.skzrec` (msgpack) + JSONL events, Parquet export on demand, 60-day retention of recordings by default. Confirm.
12. **Screenshot `hideUi` default** stays `false` (PLAN open question 12). Confirm.
13. **Hull slot authoring.** The blueprint assembler needs slot templates for the hull families you actually use (LBP destroyer/carrier, Maritime/MNWS hulls). The design ships templates for the pack's main hulls and a probe fallback; tell us which hulls matter most.
14. **Lua/MoonSharp in-game scripting** is rejected for now (BTs + C# plug-ins + external Python). Confirm.
15. **Realism defaults.** Fog-of-war IFF: radar-only tracks stay `unknown` until 40 km (air) / 20 km (surface) or a hostile act; comms model off by default. Confirm or set your own id ranges.
16. **MCP SDK pin.** PLAN pinned ModelContextProtocol 2.3.0 with a 2026-11-17 review date; the design keeps "newest release at least two weeks old at build time". Confirm the policy.
17. **Upstream PR timing.** Pitch the BDA Plus maintainers after M4 (hooks proven in battles) rather than before. Confirm.

## 4. Open technical spikes (first tasks of their milestones)

- M0: msgpack writer zero-alloc verification on Mono; `SynchronizationContext` idle wake on this KSP build.
- M1: VRD `displayedTargets` reflection cost at 200 radars; `TargetInfo.detected` semantics for allied teams; BRAA math test vectors shared with Python.
- M2: `OnFlyByWire` ordering behaviour with BDA re-activation; SubTrim `IDepthCommandSource` registration timing; `CommandFlyTo` arrival behaviour for surface AI.
- M3: Avalonia headless rendering in CI; Parquet.Net column mapping for nested track records.
- M4: Streamable HTTP session identity in the MCP SDK; prompt-cache hit rates with digest injection.
- M5: editor reload path (`LoadShip`+`SpawnConstruct` vs `LoadShipFromFile`); GM estimate accuracy vs observed roll period.
- M6: 1000-vessel TPE tick cost; lz4 in-game cost vs bridge-side compaction.
- M7: ACA catapult attach sequence; passive sonar bearing noise model from BDA's acoustic signature.
