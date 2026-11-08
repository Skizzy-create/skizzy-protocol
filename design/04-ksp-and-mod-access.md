# 04 KSP and mod access: tiers, annotations, adapters, permissions

## 1. Tiers (unchanged from PLAN v0.2 §4–5, summarised)

| Tier | Mechanism | Effort for mod author | What it gives |
|---|---|---|---|
| 0 | Reflection over `PartModule`/`VesselModule` `BaseField`s, `BaseEvent`s, `BaseAction`s, `UI_Control` ranges, resources, action groups, part tree, post-ModuleManager configs; compiled accessors; PAW-equivalent write path | none | every field/event/action of every installed mod |
| 1 | `SKIZZY_ANNOTATIONS` ConfigNodes (MM-patchable): descriptions, units, safety, hidden C# properties, statics, declared methods | none (anyone writes a cfg) | semantics for LLMs, hidden state, settings |
| 2 | duck-typed attributes (`SkizzyTool`, …) matched by class name | copy one CC0 file | typed tools, coroutines, events, no dependency |
| 3a/3b | contract DLL / JSON-delegate ABI | reference or copy `SkizzyLink.cs` | dynamic catalogs, push streams |
| deep | `reflect.get/set/call` on any type/member | — | debugging; `unsafe`, off by default |

Changes from PLAN: (a) the Tier 1 grammar gains `PART_ROLES` and `HULL_SLOTS` nodes (§3); (b) Tier 1 can declare **vessel-module properties as SITREP fields** (`sitrep = flood.tonnes`) so adapters feed the tactical picture declaratively; (c) deep reflection results can be subscribed as streams for the user's own debugging of the multi-target patch (`reflect.watch`, P2).

## 2. Universal operations (P0)

`ksp.mods.list`, `ksp.vessels.list`, `ksp.vessel.get`, `ksp.parts.find`, `ksp.module.describe|get|set|invoke|action`, `ksp.vessel.action_group`, `ksp.vessel.stage`, `ksp.query` (bulk selector), `ksp.config.get`, `ksp.log.tail`, `ksp.time.*`, `ksp.scene.*`, `ksp.game.save|load|quicksave|quickload` (destructive), `ksp.vessel.rename|set_type|destroy` (destructive), `ksp.vessel.place` (VesselMover-style, cheat).

`ksp.query` selector grammar (used by streams and log profiles too):

```json
{"select":{"vessels":{"loaded":true,"team":"Blue","class":["destroyer","carrier"],"nameLike":"DDG*"},
           "modules":{"type":["ModuleHullBreach","MissileFire"]}},
 "fields":["floodedDisplay","inflowDisplay","guardMode","firedMissiles"],
 "aggregate":"perVessel","limit":500}
```

## 3. Annotation packs shipped with the host (`GameData/SkizzyProtocol/Annotations/`)

| Pack | Covers | Highlights |
|---|---|---|
| `Squad.cfg` | stock modules | destructive classification of `Decouple`, `Jettison`, `Abort`; units for engines, wheels, control surfaces |
| `BDArmory.cfg` | `MissileFire`, AIs, `ModuleRadar`, `ModuleIRST`, `RadarWarningReceiver`, `ModuleWeapon`, `MissileLauncher`, `MultiMissileLauncher`, `ModuleMissileRearm`, `ModuleWingCommander`, `ModuleTargetingCamera`, `HitpointTracker`, `BDArmorySettings` statics (409 fields; `INFINITE_*`, `GRAVITY_*`, `SF_*` = cheat; `REMOTE_ORCHESTRATION_*` hidden) | descriptions written for LLMs, dependency order (`maxMissilesOnTarget` before `parallelSalvoSize`) |
| `HullBreach.cfg` | `ModuleHullBreach` (`crushDepth`, `flowMultiplier`, `hullStatus`, `floodedDisplay`, `inflowDisplay`, `severityDisplay`, `depthDisplay`; `ToggleTestBreach` = cheat), `HullBreachVesselModule` properties `IsSubmarine`, `Foundered`, `HullCount`, `LeakingCount`, `CriticalCount`, `WaterTonnes`, `CapacityTonnes`, `InflowTonnesPerSec` → SITREP `flood.*` | source: `_downloads\patched\HullBreach\src\HullBreachVesselModule.cs:32-40`, `ModuleHullBreach.cs:40-98` |
| `SubTrim.cfg` | `ModuleSubTrim` (`mode`, `manualDepth`, `targetDepth`, `status`, `trim`, `ballast`, `isSubmarine`; events `ToggleEmergencyBlow` = control; actions deeper/shallower/follow-AI) | `ModuleSubTrim.cs:17-143` |
| `KerbalPowersNaval.cfg` | `ModuleShipBallast` (`targetDepth`, `useTargetDepth`, `currentDepth`, `maxDepth`, `DepthPumpAdjust`, `FloodPumpAdjust`), `ModuleEngineFlooding` | from `Cavitation.dll` member names |
| `BuoyancyAdjuster.cfg` | `BuoyancyAdjuster` (`AdjustBuoyancy`, `GetBuoyancyLevel`, `RestoreBuoyancy`) | |
| `ACA.cfg` | `Catapult` (`Launch`, `IsLaunching`), `Arrestwire` (`IsWire`, `MaxWireLength`), `Hook` (`HookDeploy`, `HookRetract`, `IsHooked`), `MagneticHook`, `CLLS` | carrier ops |
| `NAS.cfg` | `ModuleAntiSubmarineWeapon` (`maxDepth`, `autoReload`, `reloadTime`, `accuracy`, `recognitionAccuracy`), `ModuleDepthChargeProjector` | |
| `MNWS.cfg` | `VLS`/`AVLS` (`GetLoadedMissileName`), `ModuleMNWSBayLink` | loadout reporting |
| `PRE.cfg` | `PreSettings.ModEnabled`, `GlobalRange` (km) statics, method `PhysicsRangeExtender.UpdateRanges(bool)` → `ksp.physics_range.get|set` (control) | same reflection BDA uses (`ModIntegration/PhysicsRangeExtender.cs`) |
| `Trajectories.cfg` | static `API.GetImpactPosition`, `GetTimeTillImpact`, `SetTarget`, `HasTarget` | bomb/impact prediction |
| `KerbalEngineer.cfg` | `SimManager`/`FlightEngineerCore` readouts: `DeltaVTotal`, `DeltaVStaged`, stage info → `ksp.engineer.*` (read) | editor validation TWR/dV cross-check |
| `AtmosphereAutopilot.cfg` | `TopModuleManager.activateAutopilot(Type)`, `StandardFlyByWire`, `CruiseController` (`desired_altitude`, `desired_course`, `desired_latitude/longitude`, `desired_velocity`) | hand-off target |
| `Hangar.cfg` | `HangarMachinery.TryStoreVessel`, `LaunchVessel`, `HangarStorage` | carrier hangar ops |
| `KerbalKonstructs.cfg` | `LaunchSite` list (`AllLaunchSites`) | spawn points |
| `HyperEdit.cfg` | `HyperEdit.Model.Lander` (teleport lat/lon/alt), `OrbitEditor`, `MiscEditor` | cheat |
| `VesselMover.cfg` | standalone mod `VesselMove`/`SpawnVesselFromCraftFile`; BDA built-in `VesselMover.PlaceVessel`/`PickUpAndDrop` preferred | cheat |

Each pack states `modVersion` and is ignored with a warning when the installed assembly version mismatches (fail closed).

### 3.1 Part-role catalog (`PART_ROLES`) — [ADDED]

```
SKIZZY_ANNOTATIONS
{
    mod = Johnny          // MNWS
    PART_ROLES
    {
        PART { name = MissleCell2050     role = vls          cells = 8   loadoutModule = MultiMissileLauncher  domain = surface }
        PART { name = 35mmAAMS           role = ciws         domain = surface  arc = 360 }
        PART { name = SPY1D              role = radar.air    bands = S   omnidirectional = true }
        PART { name = 208mmRailgun       role = gun.main }
    }
}
SKIZZY_ANNOTATIONS
{
    mod = LShipPartsModern
    PART_ROLES
    {
        PART { name = LBP_DDG_Hull_Fwd   role = hull.destroyer.section  class = destroyer  waterlineOffset = -2.1 }
    }
    HULL_SLOTS      // where things go on this hull family; used by the blueprint assembler (05-craft-building.md §4)
    {
        HULL { name = LBP_DDG_Hull_Fwd
               SLOT { id = fwd_vls   role = vls        node = top_01  count = 2  heading = 0 }
               SLOT { id = fwd_gun   role = gun.main   node = top_00  count = 1 }
               SLOT { id = bow_sonar role = sonar.hull node = bottom_00 } }
    }
}
```

Roles form a small taxonomy: `hull.*`, `vls`, `mml`, `ciws`, `gun.*`, `torpedo_tube`, `depth_charge`, `radar.{air,surface,fire_control}`, `irst`, `sonar.{hull,towed,active,passive}`, `rwr`, `ecm`, `decoy.*`, `catapult`, `arresting_wire`, `hangar`, `ballast`, `propulsion.*`, `command`, `wm`, `ai.*`. Unannotated parts are classified heuristically from their BDA modules (`ModuleWeapon` with `turret` → `gun.turret`, `ModuleRadar sonarMode != None` → `sonar.*`, `MultiMissileLauncher` → `vls|mml`) and reported with `roleSource:"heuristic"`.

### 3.2 Vessel classification — [ADDED]

Computed once per vessel and on `onVesselWasModified`: `domain` (air/surface/sub/land) from the active AI type and `SurfaceTypeName`, situation and parts; `class` from roles and mass (destroyer, cruiser, carrier, patrol boat, submarine, fighter, bomber, helicopter/VTOL, drone (unmanned + mass < 2 t), missile, ground vehicle, static). Users override per craft via a `SKIZZY_CLASS` field in the craft description or `library` manifest. The class feeds map symbols, threat scores and doctrine.

## 4. Permissions for mod access

Unchanged model (levels, policy order, `ask` flow, audit: PLAN v0.2 §6). Defaults change (D11): all areas on except `deep`; `read` and `control` allow for all mods; `cheat` allow for `bdarmory`, `ksp.vessel.place`, `sim.*` (the user is a test director); `destructive` ask; `unsafe` deny. Presets stay (`Observer`, `Test Director`, `Full Combat`, and the new default **`Battle Lab`**).

Additional per-actor clamps (`15-security-permissions.md` §3): unit agents cannot use `cheat`, cannot read god view, cannot preempt leases above their precedence.

## 5. Adapters (what they add beyond Tier 0/1)

| Adapter (`Skizzy.Naval.dll` unless noted) | Capabilities | Priority | API anchors |
|---|---|---|---|
| **SubTrim** | registers `SkizzyDepthSource : IDepthCommandSource` through `SubTrimRegistry.Register` so `flight.depth {vessel, depthM}` becomes a first-class setpoint on SubTrim-equipped subs; `naval.subtrim.status`; emergency blow (control) | P1 | `_downloads\patched\SubTrim\Source\SubTrim\Api.cs` (`IDepthCommandSource`, `IDepthCommandSourceFactory`, `SubTrimRegistry`) |
| **HullBreach** | `naval.flood.state` per vessel (from `HullBreachVesselModule`), `unit.flooding`/`unit.foundered` events (threshold crossings), damage-control actions when the patched source exposes them (pumps, counter-flooding: P3, needs HullBreach change) | P1 | `HullBreachVesselModule.cs:32-40` |
| **KP Naval ballast** | `flight.depth` fallback via `ModuleShipBallast.targetDepth/useTargetDepth` when SubTrim is absent | P2 | `Cavitation.dll` members |
| **ACA carrier ops** | `naval.carrier.launch {carrier, aircraft}` (hook to catapult, launch), `naval.carrier.recover` (hook deploy, wire state), `naval.carrier.deck` state machine stream | P2 | `ACA.dll`: `Catapult.Launch`, `IsLaunching`, `Hook.HookDeploy`, `IsHooked`, `Arrestwire.IsWire` |
| **Hangar** | store/launch vessels on carriers and bases | P2 | `HangarMachinery.TryStoreVessel/LaunchVessel` |
| **NAS/ASW** | depth-charge/torpedo settings, ASW events | P1 | module fields |
| **MNWS** | VLS loadout report and reload (`ModuleMissileRearm`) | P1 | `VLS.GetLoadedMissileName` |
| **PhysicsRangeExtender** | `ksp.physics_range.get|set` (control); batch runner sets range from scenario | P0 | as BDA's `PhysicsRangeExtender.cs` |
| **VesselMover (BDA built-in)** | `ksp.vessel.place {vessel, lat, lon, alt?, heading}`, `lift`, `drop` | P0 | `VesselSpawning/VesselMover.cs:445,542` |
| **HyperEdit** | `ksp.vessel.teleport` incl. orbit (cheat) | P2 | reflection |
| **AtmosphereAutopilot** | `flight.handoff {to:"atmosphereautopilot", mode}` and AA cruise setpoints (`desired_altitude`, `desired_course`) as a hold backend for aircraft | P1 | `TopModuleManager.activateAutopilot` |
| **KerbalEngineer** | validation cross-check (TWR, dV) | P2 | `SimManager` |
| **Trajectories** | bomb/impact prediction for strike planning | P2 | `API.GetImpactPosition` |
| **KerbalKonstructs** | spawn-site catalog for scenarios | P2 | `AllLaunchSites` |
| **KSPCommunityFixes** | nothing to control; `skizzy doctor` verifies `MemoryLeaks = true` and reports KSPCF's leak warnings from the log | P0 | `Settings.cfg:406` |
| **BDArmory** (`Skizzy.BDArmory.dll`) | everything in `07-bdarmory-integration.md` | P0 | |

All adapters are soft: types are resolved once by name at startup (`AssemblyLoader.loadedAssemblies`), every member cached as a compiled delegate, and a missing member disables only the capabilities that need it (`availability.reason = "api_missing"`).

## 6. Bulk access and token economy

- `ksp.query` and `tac.query` are the only sanctioned ways to read many objects; the bridge never exposes one tool per Tier 0 member.
- `format:"compact"` exists for `ksp.query` too: `v:118 DDG-1 | ModuleHullBreach x14: flooded max .31 avg .04 | MissileFire: guard on, away 6`.
- `ksp.module.describe` returns schemas with `x-skz-units`, `x-skz-step`, `x-skz-enumLabels` and `x-skz-dependsOn`, so an LLM gets the full semantics in one call and can cache it.
