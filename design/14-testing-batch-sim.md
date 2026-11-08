# 14 Testing strategy and batch simulation

**[R] "Headless" means unattended.** KSP cannot run without a window and a GPU (it is a Unity player with a renderer; `-batchmode` does not run the flight scene usefully). Pass 1.0 said "headless" in places; what the design delivers is (a) a truly headless kinematic world for CI (`Skizzy.Sim`, §2) and (b) **unattended** batch runs in a real KSP window that nobody watches (§4). The headless map renderer (`11-tactical-map.md` §9) is headless; KSP is not.

## 1. Test pyramid

| Level | What | Runs where | Tools |
|---|---|---|---|
| Unit | `Skizzy.Protocol` (JSON/msgpack codecs, schema generation/validation, compact text formatter, permission engine, geometry/BRAA with shared test vectors, PID controllers, WTA solver, BT interpreter) | CI | xUnit; property tests for codecs |
| Host core | WebSocket server, sessions, dispatcher budget, operations, leases, order state machine, triggers, recorder chunking | CI | compiled by link into test project (PLAN §10.1) with a fake clock |
| **Engine tests on FakeKsp** | TPE fusion/fog/IFF/dead reckoning, C2 executors, Flight holds (kinematic model), Recorder end-to-end, scenario loader | CI | `Skizzy.Sim` implementing `IGameWorld` + `IBdaFacade` |
| Bridge | MCP projection against the real host Core + FakeKsp; toolsets; actor identity over Streamable HTTP; op/wait; image blocks | CI | ModelContextProtocol SDK test client |
| SDKs | Python and C# against FakeKsp (via the real host Core) and against `skizzy.sim` | CI | pytest, xUnit; shared conformance vectors |
| Map | symbol rendering snapshots (Skia bitmaps), delta application, planning tools issuing orders against FakeKsp | CI | Avalonia headless |
| Agents | orchestrator loop with a **scripted fake LLM** (deterministic tool calls) against FakeKsp; prompt size and cache-prefix stability assertions; budget enforcement | CI | no API spend |
| Conformance | `spec/conformance`: wire-level checks of `03-protocol.md` against host + FakeKsp, and optionally against a live KSP | CI / manual | |
| In-game self-checks | idle cost, dispatcher timing, PAW write path, hook availability, screenshot leak check, TPE tick cost at current vessel count, recorder cost | KSP start / on demand (`host.selfcheck`) | log lines + `skizzy doctor` |
| In-game acceptance | per-milestone checklists (`17-milestones.md`), run by the user with Claude Code | KSP | |
| Soak | **[R]** 30 min (gate from M2), 2 h (from M4), 4 h (release gate, M9), 8 h optional; recorder on; heap after forced collection, Unity object counts and handle counts sampled; heap-slope gate (`02-architecture.md` §8.1). Exact 0 B/tick asserts are CI-only (Unity's mscorlib has no per-thread allocation counter) | KSP (user machine, unattended) | `host.selfcheck.soak`, `skizzy battle run --soak` |
| Fuzz | 10 k-case JSON/msgpack corpus against the host parser; schema fuzz for `cap.call` | CI | |
| Perf gates | `host.selfcheck.perf` asserts `02-architecture.md` §8 budgets; batch runner flags frame-time regressions between runs | KSP | |
| **[R]** Allocation asserts | every `skizzy:hot` method (engines and the network-thread serialisers) wrapped in `GC.GetAllocatedBytesForCurrentThread()` deltas on .NET 10, asserted 0 after warm-up | CI | xUnit + `Skizzy.Sim` |
| **[R]** In-KSP synthetic load | `host.selfcheck.perf --synthetic N` injects N synthetic track sources (kinematic, from `Skizzy.Sim`'s models) into the real TPE, recorder, subscriptions and serialisers inside KSP, next to the real scene, for budgets that real KSP cannot reach with loaded vessels (1000 records) | KSP | in-game self-check |

## 2. FakeKsp (`Skizzy.Sim`) — [ADDED]

A C# kinematic world implementing the interfaces the host engines consume:

- `IGameWorld`: vessels (id, name, team, class, position on a spherical body, velocity, altitude/depth, hp, parts summary, situation), physics step (20 ms), UT, loaded/packed flags, scene state, vessel create/destroy, `FlightCtrlState` sink per vessel with a first-order dynamics model (turn rate, acceleration limits per class).
- `IBdaFacade`: teams, WM state (guard, fire authority, targets, missiles away, ammo), AI state (type, command, status), radar/sonar/IRST with range/FOV/probability-of-detection, RWR pings, missiles (pursuit with speed/turn limits, hit probability, CM effects), detection flags per team (reproducing `TargetInfo.detected/detectedTime` semantics), competition/scoring (kills, death order), spawners, hooks (always "fork" available so executors are testable; a `stock` mode disables them to test fallbacks).
- Scriptable scenarios (YAML, same schema as real scenarios) and a scripted-LLM harness.
- Runs at ≥ 1000× real time headless; deterministic with a seed.

The point is not physics fidelity; it is that every engine, SDK, map feature and agent flow has a CI test without KSP, and that fog-of-war logic is tested against a world where truth is known.

## 3. In-game test range

A dedicated save `saves/skizzy-range` (decided, `18-risks-and-decisions.md` §3.2 D-5) with a parked probe at an open-water site east of KSC (`lat -0.15, lon -74.20`), PRE range set to the scenario's need (default 100 km), and `AutoSpawn/skizzy/` populated from the library. The batch runner and `craft.trial` require this save to be loaded (they never load saves themselves unless `sim.scenario.load {save}` is granted; `destructive`). **[R]** Decided: the save is created by `skizzy doctor --fix --create-range` (or the first-run wizard) from a template shipped in `GameData/SkizzyProtocol/Defaults/skizzy-range/`; loading it from a running game is `ask`. The site is changeable in `settings.cfg`.

## 4. Batch simulation (`skizzy battle run`, P1)

Lifecycle (bridge-driven over the native socket; PLAN §9.2 extended):

1. Preflight: host reachable, flight scene, BDA version, hooks (`bda.hooks`), grants, PRE range, craft exist (library versions pinned), no competition running, recorder profile exists.
2. Snapshot BDA settings; apply scenario `settings`.
3. For each matrix cell × repeat: `bda.remove_vessels all` → `sim.scenario.load` (spawn via template/ring/list, groups, doctrine, orders, waves) → verify config read-back → `rec.profile.start` → start competition (referee) → run until `end` condition (UT limit, one team left, all dead, custom trigger) → `rec.profile.stop` → scores, logs, `KSP.log` error window → cell summary.
4. Restore settings; remove vessels.
5. Summaries: per-cell metrics, assertions, `summary.md` and `summary.json`; non-zero exit on failure.

**Unattended profile [R]** (P2, `skizzy battle run --unattended`): arming it from the in-game window ("arm batch mode for 8 hours") pre-answers the run's known confirmations and co-signs its two-key operations for that run only, turns every other `ask` into `deny` (nothing can block on a prompt nobody sees), sets a wall-clock cap, writes a heartbeat file the runner watches to detect a hung or crashed KSP, and disarms itself when the run ends, the cap passes or the human touches the controls.

Scale: 200 vessels × 50 runs ≈ 50 × (spawn ~2–4 min + battle ~10 min at `TIME_SCALE` 2–3 + teardown) ≈ 6–10 h unattended; the runner checkpoints after every cell and resumes after a KSP restart (the user restarts KSP; the runner never does: PLAN decision kept). Frame-time and memory are recorded per cell so a stress cell is diagnosable.

## 5. A/B and statistics

- Matrix cells differ by scenario parameters (WM settings, doctrine, craft versions, BDA settings). Build A/B (different BDArmory DLLs) still needs a manual restart; the runner detects the DLL hash change and tags cells.
- Report: mean ± 95 % bootstrap CI, win rates, distributions; invariant assertions checked on every sample (`max(missilesInFlightAt) ≤ maxMissilesOnTarget`, `exceptions == 0`, heap slope within the allocation gate).
- Determinism: not available (Unity physics, BDA randomness); repeats default 5 for tuning runs, 20–50 for publication-grade comparisons. Lockstep reduces timing variance in order issue but not in physics.

## 6. Validation of the user's mods (scenario pack, P1)

| Target | Scenario | Assertions |
|---|---|---|
| Multi-target turrets/missiles, parallel salvo | `mt-matrix`: 1 DDG vs 8/16/32 drones; `multiTargetNum` {1,8,64} × `maxMissilesOnTarget` {1,2,4} × `parallelFire` {off,on} | per-target concurrency ≤ setting on every sample; distinct targets engaged > 1 when `multiTargetNum > 1`; salvo spread ≈ stagger |
| Retarget after kill | `retarget`: 2 shooters, 6 targets | missiles redirect within one scan interval; no missile wasted on a dead target beyond `maxMissilesOnTarget` |
| Depth charges / ASW (NAS) | `asw-1`: 2 escorts vs 1 sub at 60 m | depth charge release over target; sub damage/flood events |
| SubTrim | `sub-depth`: dive/hold/surface profile | depth error bands; emergency blow |
| MNWS VLS | `vls-load`: loadout read-back and reload cycle | loaded names match blueprint; reload timing |
| Iron Seas guns (SkizzyRig) | `gun-line`: 2 ships broadside at 8 km | hits registered by weapon name; turret arcs |
| HullBreach | `flood-1`: ship with scripted damage | flood tonnes monotonic, founder time |
| Performance | `stress-200` (200 loaded vessels, god view, recorder on) and `stress-1000s` (100 loaded + 900 synthetic records) | budgets from `02-architecture.md` §8; frame-time regression < 10 % between builds |

## 7. Acceptance (summary)

1. CI runs unit + host core + FakeKsp engine + bridge + SDK + map + agent tests in < 10 minutes with no KSP.
2. `skizzy battle run library/scenarios/mt-matrix.yaml` completes unattended and produces `summary.md` with CIs; assertions catch an injected regression (a cell with `maxMissilesOnTarget` violated by a test hook).
3. A 2-hour soak shows no monotonic memory growth (slope < 1 MB/h after the first 30 minutes) and no Unity object count rising monotonically; CI shows 0 B/tick alloc in TPE/recorder/serialisers.
4. **[R]** `host.selfcheck.perf --synthetic 1000` passes the 1000-record TPE budget inside KSP on the user's machine.
5. **[R]** An unattended run that hits an unexpected `ask` denies it, logs it and continues; the run is never blocked on a prompt.
