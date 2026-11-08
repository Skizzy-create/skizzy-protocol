# 20 Runtime lifecycle and edge cases — [REVIEW-ADDED]

Added by review pass 1.1. Pass 1.0 assumed every vessel is loaded, the active vessel never changes by itself, time always flows, names are unique and sessions are short. None of that holds in a real KSP battle. This file defines what the host does in each case; other files refer here.

## 1. Vessel states and handles

| State | KSP condition | Physics / `OnFlyByWire` | BDA | Skizzy behaviour |
|---|---|---|---|---|
| `live` | loaded and unpacked (`loaded && !packed`) | yes | AI, WM, radar active | everything works |
| `packed` | loaded but on rails (`packed`): beyond the unpack range, or during rails warp | no callbacks, no forces | BDA skips packed vessels | holds, routes and direct streams are kept but report `PRECONDITION_FAILED`/`vessel_packed` on new calls and `state:packed` in SITREPs; they resume on `onVesselGoOffRails` unless released; positions still update from the orbit |
| `unloaded` | outside the load range (`!loaded`), only a `ProtoVessel` | none | none | read-only: name, team (from the craft's BDA team field when present), position from the orbit at 1 Hz; god-view tracks only (`08-tactical-picture.md` §1); control calls fail with `vessel_unloaded`; placement and destroy still work |
| `dead` | destroyed this session | — | — | handle resolves to `NOT_FOUND`/`destroyed` for 10 minutes, then `unknown`; tracks show `state:dead` for 10 s |

- **Transitions** come from `GameEvents.onVesselGoOnRails`/`onVesselGoOffRails`, `onVesselLoaded`/`onVesselUnloaded`, `onVesselWillDestroy`/`onVesselDestroy`, and emit `vessel.packed|unpacked|loaded|unloaded|destroyed`.
- **PRE boundary.** PhysicsRangeExtender widens the load/unpack ranges; the effective ranges are read through BDA's helper and reported in `host.state` and `skizzy doctor`. A battle whose spawn geometry exceeds the range is flagged in preflight ("Red group spawns at 140 km; PRE range 100 km: those vessels will be packed").
- **Handles** are `v:<persistentId>`. `onVesselPersistentIdChanged` (docking, undocking, some collisions) re-keys the vessel, emits `vessel.pid_changed {old, new}` and keeps an alias from the old handle for 60 s.

## 2. Active-vessel changes

The active vessel changes when the human switches (`[`/`]`, map click), when the flown vessel is destroyed and KSP picks another, when BDA's competition or camera tools switch vessels, or when a Skizzy client calls `flight.switch_vessel`.

- `GameEvents.onVesselSwitching`/`onVesselChange` → `vessel.active_changed {from, to, cause: human|death|bda|skizzy|unknown}`. The cause is inferred: `death` if the old vessel died in the same frame, `skizzy` if a Skizzy call is in flight, `bda` if BDA's switching code ran (its competition camera is the common case), otherwise `human`.
- **The implicit human lease does not jump.** The human's `steer` lease attaches to whatever vessel the human actually moves a control on. After an automatic switch, holds and agent leases on the new active vessel keep running until the human touches an input there (`06-flight-control.md` §8). This prevents "my ship died, KSP switched me to a wingman, and the wingman's agent hold silently stopped".
- **Camera follow** in Skizzy camera modes follows the camera target, not the active vessel; camera calls never change the active vessel (`13-vision-camera.md` §1.2).
- **Kill switch** applies its active-vessel rule to the vessel that is active at the moment it is pressed.

## 3. Time: warp, BDA time scale, pause, menus

| Situation | How detected | Skizzy behaviour |
|---|---|---|
| **BDA time scale** (`TIME_OVERRIDE`, `Time.timeScale` ≠ 1, physics step unchanged) | `sim.clock` | everything works; rates are in UT; subscriptions report `timeScale` |
| **Physics warp** (×2–×4; KSP enlarges the physics step) | `onTimeWarpRateChanged`, `TimeWarp.WarpMode == LOW` | holds keep running with the larger step; PID gains are scaled by step size; a warning event because BDA aiming degrades; refused by `sim.*` while a competition runs unless forced |
| **Rails warp** (×5 and up; all vessels packed) | `TimeWarp.WarpMode == HIGH` | every vessel becomes `packed` (§1); new control calls fail with `warp_active`; Skizzy refuses to *start* rails warp while leases or a competition are active; if the human starts it, holds pause and resume after warp |
| **Pause menu** (Esc) | `onGamePause`/`onGameUnpause` | `Time.timeScale = 0`: streams send one `sim.paused` and then only heartbeats; holds and leases freeze (TTL clocks are UT-based, so leases do not expire during a pause); calls needing physics fail with `paused`; KSP resets `Time.timeScale` to 1 on unpause, so the host re-applies a BDA/Skizzy time scale that was active before |
| **Other menus and dialogs** (map view, Alt+F12, part action windows) | — | not a pause; nothing changes |
| **UI hidden** (F2) | `onHideUI`/`onShowUI` | in-game windows hide; the kill-switch hotkey still works |
| **Lockstep hold** (`sim.hold`) | Skizzy's own | `Time.timeScale = 0` with a banner; the human can resume from the banner or the kill switch |

Whenever time stops, wall-clock caps on operations keep running so a forgotten pause does not leave operations alive forever; UT-based timeouts wait.

## 4. Lockstep exact stepping (P2)

`sim.step {n}` must advance exactly *n* physics steps. Unity can run several `FixedUpdate`s in one rendered frame when the time scale is high, so the host steps at a low time scale (so that at most one physics step happens per frame), counts steps in its own `FixedUpdate` (registered at `TimingManager` stage `Precalc` so it runs before BDA and the engines), and sets `Time.timeScale = 0` from inside the step that reaches *n*. The result reports `{stepsRun, ut, frame}`. Basic lockstep (P1, M7) documents ±1-step accuracy; exact stepping is P2 (M8).

## 5. Scene changes, quicksave and quickload; saves

- **Scene change or quickload (F9)** is a scene reload: `onGameSceneLoadRequested` fires, every vessel object is recreated. The session survives (the socket is owned by the host addon, which persists across scenes). Leases and holds are released, orders become `suspended` (reason `scene`), subscriptions get `kind:"resync"` after the scene loads, operations are cancelled with `CANCELLED`/`scene_change`, and all handles become `NOT_FOUND`/`stale`.
- **Resume:** when the flight scene is back, `c2.resume` offers to resume suspended orders whose vessels exist again (matched by persistentId, then by name), and re-acquires leases for the same actors; the human or the commander confirms.
- **Auto-snapshot (P1, off by default):** on `GameEvents.onGameStateSave` (the user quicksaves), Skizzy writes its own state (orders, leases, groups, zones, bullseyes, doctrine, annotations) to `Skizzy/snapshots/<save>/<ut>.json`. After a quickload, `c2.resume` uses the snapshot whose UT matches the loaded save, so the C2 picture comes back with the vessels.
- **Saves are never written by Skizzy in v1.** No `ScenarioModule`, `PartModule` or `VesselModule` is registered (KSP serialises vessel modules into the save), and nothing edits `.sfs` files. Battle positions are saved as scenario snapshots (`sim.scenario.snapshot`), which respawn vessels. Removing `GameData/SkizzyProtocol` therefore leaves the game and all saves exactly as they were. `ksp.game.save|load|quicksave|quickload` call KSP's own functions and are `destructive`.

## 6. Duplicate vessel names

BDArmory's competition scores are a `Dictionary<string, ScoringData>` keyed by **vessel name** and its death order is a list of names (`07-bdarmory-integration.md` §1.6), so two craft called "DDG" share one score row and the second death is lost.

- Skizzy identifies vessels by persistentId everywhere; names are only labels.
- **Before Skizzy starts a competition** (and in `bda.spawn_*` and **[R2]** `ksp.vessel.spawn_craft`/`spawn_formation`, multi-spawn and respawn, with `uniqueNames` default `auto`) duplicate names are made unique by appending ` #2`, ` #3` … (`ksp.vessel.rename`, which is control level); a `vessel.renamed` event records old and new names. `uniqueNames:"strict"` fails with `DUPLICATE_NAME` instead; `"off"` leaves names alone and warns.
- If a competition is started from BDA's own UI with duplicates, Skizzy warns in the SITREP and the event log and keys its own statistics by persistentId.

## 7. Vessel splits and debris

Decouplers, weapon releases, structural failures and BDA damage split vessels mid-battle.

- `onVesselCreate` with a parent link (from `onPartDeCoupleComplete`/`onPartUndock`, or a same-frame part-count drop on the parent via `onVesselPartCountChanged`) → `vessel.split {parent, children}`.
- A child with a command part and a WM or AI is a new unit: it inherits the parent's team, group and leases are **not** copied (the owner gets an event and can acquire them). A child without command is **debris**: no track, no events, excluded from queries unless `includeDebris:true` (`08-tactical-picture.md` §1).
- Missiles launched by BDA appear through `FiredMissiles`, not as splits.
- BDA's own debris cleanup runs during competitions; Skizzy does not delete debris itself.

## 8. Long-session hygiene

The user just fixed memory leaks; a battle session may last hours.

- **Bounded structures:** every cache has a cap and an eviction rule: name interning (evicted on vessel destroy and scene change), handle registry (dead handles purged after 10 minutes), event ring (fixed 8192+), per-subscriber delta state (freed on unsubscribe/close), operation table (finished operations kept 10 minutes), alias table (60 s), per-session rate-limit windows.
- **Event subscriptions:** every `GameEvents.X.Add` goes through one registry that removes all of them on scene change and on addon destroy; `host.selfcheck` reports the count per event so a leak shows as a number that keeps rising. KSPCF's `MemoryLeaks` warnings in `KSP.log` are parsed by `skizzy doctor`.
- **Files:** audit and host logs rotate at 50 MB (keep 10); log v0 files stop at `maxMB`; recordings are chunked; nothing grows without bound on disk except recordings and logs, which have the 20 GB warning.
- **Memory self-check** every 10 minutes during a session: heap size (`Profiler.GetMonoUsedSizeLong`), allocation rate, Gen-0 collections per minute and, at the same marks, counts of `Texture2D`, `RenderTexture`, `Material`, `GameObject` (the count query allocates, so it runs only at these marks or on demand). A rising trend raises `host.memory_warning` and appears in the in-game health strip.
- **Unity objects:** RenderTextures, textures and materials Skizzy creates are pooled and destroyed explicitly; the screenshot self-check counts them before and after 100 captures.

## 9. Mod interference (checked by `skizzy doctor` and at host start)

| Mod or setting | Risk | Check and response |
|---|---|---|
| **CommNet** (stock setting) with probe craft lacking connection | `vessel.IsControllable` false: BDA guard mode stops (`MissileFire.cs:2477`), the player cannot fly | doctor warns when CommNet is on; SITREP and `unit.not_controllable` event per vessel; holds refused unless `allowUncontrollable:true`; Skizzy never changes the setting |
| **RemoteTech** (not installed now) | signal delay and its flight computer take over input | detected by assembly name; flight capabilities report `api_missing` until an adapter exists; doctor explains |
| **Kerbalism** (not installed now) | its comms/signal and automation can block control; it changes resource/comms behaviour | detected; same `not_controllable` path; doctor warns that Kerbalism + BDA is untested |
| **Other `OnFlyByWire` users**: AtmosphereAutopilot (installed), MechJeb, kOS, kRPC, TCA | two autopilots writing the same axes | doctor lists installed ones; at hold start the host inspects the vessel's `OnFlyByWire` invocation list once and warns if a foreign autopilot is active; AA is integrated as a hand-off target instead (`06-flight-control.md` §7) |
| **ThrottleMultiplier** (installed) | scales throttle input | doctor notes it; human-override detection uses throttle *changes*, so scaling does not cause false triggers |
| **Kerbal Joint Reinforcement** (installed) | joints stiffen/relax around physics changes; teleports can explode | placement waits one settle frame and uses `IgnoreGForces`; doctor reports KJR presence |
| **PhysicsRangeExtender** (installed) | range too short for the scenario → packed vessels | preflight range check (§1) |
| **KSPCommunityFixes** (installed) | its `MemoryLeaks` option is the user's leak guard | doctor verifies `MemoryLeaks = true` and reports KSPCF leak warnings |
| **Harmony** (installed `000_Harmony`) | several Harmony copies or versions | doctor lists loaded `0Harmony` assemblies and versions; Skizzy uses none unless the optional stopgap is enabled |
| **KSPBurst** (installed) | ships `System.Runtime*`, `System.Runtime.CompilerServices.Unsafe` | in-game Skizzy assemblies reference none of them (`02-architecture.md` §1 build guard); doctor lists duplicate `System.*` assemblies |
| **ClickThroughBlocker / ToolbarControl** (installed) | IMGUI click-through; toolbar integration | Skizzy windows use ClickThroughBlocker when present and register their button through ToolbarControl, falling back to stock `ApplicationLauncher` |
| **Graphics mods** (Scatterer, EVE, TUFX, Deferred, Waterfall: installed) | screenshot cost and look | screenshots capture what the player sees; the self-check measures capture time with them active |
| **TimeControl** or similar (not installed now) | changes `Time.timeScale` behind BDA's back | detected; `sim.time.*` refuses with a hint while it is active |
| **BDArmory version / fork** | hooks missing or moved | `bda.hooks` (fork, reflection, none) per capability |

## 10. Connection resilience

- **Port conflicts:** the host tries 47549–47559 and writes `PluginData/endpoint.json` `{port, pid, startedUtc, hostVersion, protocol}`; the bridge, CLI and SDKs read it, so a moved port needs no configuration.
- **KSP restart:** clients reconnect with back-off (0.5, 1, 2, 4, max 10 s) and `session.hello {resume}`; leases survive a reconnect within their TTL (they are tied to the actor id, not the socket); subscriptions resume from `fromSeq` or get a full resync.
- **Bridge restart:** the host keeps operations running; the bridge re-attaches with `op.list`.
- **Host fault:** a capability that throws 5 times in 60 s is auto-disabled with `host.auto_disabled`; engines isolate failures per vessel.

## 11. Acceptance (summary)

1. Holds on a ship that drifts beyond the unpack range report `vessel_packed`, keep their setpoints, and resume when it unpacks; god view still shows it; fog view ages it out.
2. After the flown jet dies and KSP switches to a wingman under an agent hold, the hold keeps flying until the human moves the stick.
3. Pausing with Esc during a ×2 BDA time scale and unpausing restores ×2; leases do not expire during a 5-minute pause.
4. Quickload mid-battle: no exceptions, all handles `stale`, orders `suspended`, `c2.resume` restores them against the reloaded vessels; with auto-snapshot on, zones and groups come back too; the save file contains no Skizzy data.
5. Two craft named "DDG" are renamed "DDG" and "DDG #2" before a Skizzy-started competition; scores have two rows.
6. A 2-hour session shows flat event-subscription counts, bounded caches and no rising Unity object counts.
7. `skizzy doctor` on the user's install lists KJR, PRE, KSPCF (MemoryLeaks on), AtmosphereAutopilot, ThrottleMultiplier, Harmony and KSPBurst with the correct notes, and warns if CommNet is enabled.
