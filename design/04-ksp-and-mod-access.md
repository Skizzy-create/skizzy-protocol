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

`ksp.mods.list`, `ksp.vessels.list`, `ksp.vessel.get`, `ksp.parts.find`, `ksp.module.describe|get|set|invoke|action`, `ksp.vessel.action_group`, `ksp.vessel.stage`, `ksp.query` (bulk selector), `ksp.config.get`, `ksp.log.tail`, `ksp.time.*`, `ksp.scene.*`, `ksp.game.save|load|quicksave|quickload` (destructive), `ksp.vessel.rename|set_type` (control; **[R]** renaming is reversible and not destructive), `ksp.vessel.destroy` (destructive), `ksp.vessel.place` (VesselMover-style, cheat), **[R]** `ksp.vessel.spawn_craft {craft, lat, lon, alt|"surface", heading, team?, name?}` (cheat, M1: spawns an existing `.craft` at a position without BDA's spawner), `ksp.crew.list|transfer|board` (P2; `transfer`/`board` are control, using `CrewTransfer`/`KerbalEVA` stock paths).

All writes to module fields go through the PLAN v0.2 §4.5 PAW-equivalent path (field set, `UI_Control.onFieldChanged`, symmetry counterparts, `part.Events`/`GameEvents.onPartActionUIChanged`), so mods react exactly as if the player had moved the slider.

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
| `MNWS.cfg` | `VLS`/`AVLS` cell parts, `ModuleMNWSBayLink` | loadout reporting. **[R]** `GetLoadedMissileName` is a private method of `ModuleInjectTransforms`, not a public `VLS` member; the loadout is read from the BDA `MultiMissileLauncher`/`MissileLauncher`/`ModuleMissileRearm` modules on the cell parts instead |
| `PRE.cfg` | `PreSettings.ModEnabled`, `GlobalRange` (km) statics, method `PhysicsRangeExtender.UpdateRanges(bool)` → `ksp.physics_range.get|set` (control) | **[R]** call BDA's public static helper `BDArmory.ModIntegration.PhysicsRangeExtender` (`CheckForPhysicsRangeExtender`, `IsPREEnabled`, `GetPRERange`, `SetPRERange(int)`) instead of re-implementing the reflection |
| `Trajectories.cfg` | static `API.GetImpactPosition`, `GetTimeTillImpact`, `SetTarget`, `HasTarget` | bomb/impact prediction |
| `KerbalEngineer.cfg` | `SimManager`/`FlightEngineerCore` readouts: `DeltaVTotal`, `DeltaVStaged`, stage info → `ksp.engineer.*` (read) | editor validation TWR/dV cross-check |
| `AtmosphereAutopilot.cfg` | `TopModuleManager.activateAutopilot(Type)`, `StandardFlyByWire`, `CruiseController` (`desired_altitude`, `desired_course`, `desired_latitude/longitude`, `desired_velocity`) | hand-off target |
| `Hangar.cfg` | `HangarMachinery`, `TryRestoreVessel`, `LaunchVessel`, `HangarStorageDynamic`/`SimpleHangarStorage`/`SingleUseHangarStorage` | carrier hangar ops. **[R]** there is no `TryStoreVessel` in the installed `Hangar.dll`; storing is done by the hangar's own trigger volume, Skizzy only launches and restores |
| `KerbalKonstructs.cfg` | `LaunchSiteManager.AllLaunchSites` (property over the `allLaunchSites` field) | spawn points |
| `HyperEdit.cfg` | `DoLander` (land at lat/lon/alt), `OrbitEditor`, `MiscEditor` | cheat. **[R]** no `Model.Lander` type exists in the installed `HyperEdit.dll`; Skizzy's own placement (§5) is preferred anyway |
| `WildBlueTools.cfg` **[R]** | `WBIBallastTank`, `WBIDiveComputer` fields | read; `flight.depth` defers to SubTrim when both are on a vessel |
| `VesselMover.cfg` | standalone mod `VesselMove`/`SpawnVesselFromCraftFile`; BDA built-in `VesselMover.PlaceVessel`/`PickUpAndDrop` and `VesselSpawner.SpawnVesselFromCraftFile` preferred; **[R2]** Skizzy's spawn-anywhere and multi-spawn build on them (`06-flight-control.md` §9.1) | cheat |

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

Additional per-actor clamps (`15-security-permissions.md` §2): **[R2]** delegates and advisors cannot use `cheat`, cannot read god view, cannot preempt leases above their precedence, and delegates cannot act outside their cluster.

## 5. Adapters (what they add beyond Tier 0/1)

| Adapter (`Skizzy.Naval.dll` unless noted) | Capabilities | Priority | API anchors |
|---|---|---|---|
| **SubTrim** | **[R]** v1 (M1): `flight.depth {vessel, depthM}` writes `ModuleSubTrim.mode = manual` and `manualDepth`/`targetDepth` through the PAW path on **every** `ModuleSubTrim` on the vessel, remembers the previous mode/depth, and restores them on release or lease expiry; `naval.subtrim.status`; emergency blow (control). v2 (P2, optional): a separate `Skizzy.SubTrim.dll` with `[KSPAssemblyDependency("SubTrim", 1, 0)]` that references SubTrim directly and registers an `IDepthCommandSourceFactory` through `SubTrimRegistry`; a soft-reference assembly cannot implement SubTrim's interface, and SubTrim's current source selection does not let an external source outrank the BDA-AI source, so v2 also needs a small priority option in SubTrim (the user's own mod) | P1 | `_downloads\patched\SubTrim\Source\SubTrim\Api.cs` (`IDepthCommandSource`, `IDepthCommandSourceFactory`, `SubTrimRegistry`), `ModuleSubTrim.cs:17-143` |
| **HullBreach** | `naval.flood.state` per vessel (from `HullBreachVesselModule`), `unit.flooding`/`unit.foundered` events (threshold crossings), damage-control actions when the patched source exposes them (pumps, counter-flooding: P3, needs HullBreach change) | P1 | `HullBreachVesselModule.cs:32-40` |
| **KP Naval ballast** | `flight.depth` fallback via `ModuleShipBallast.targetDepth/useTargetDepth` when SubTrim is absent | P2 | `Cavitation.dll` members |
| **ACA carrier ops** | `naval.carrier.launch {carrier, aircraft}` (hook to catapult, launch), `naval.carrier.recover` (hook deploy, wire state), `naval.carrier.deck` state machine stream | P2 | `ACA.dll`: `Catapult.Launch`, `IsLaunching`, `Hook.HookDeploy`, `IsHooked`, `Arrestwire.IsWire` |
| **Hangar** | launch/restore vessels on carriers and bases | P2 | `HangarMachinery.LaunchVessel`, `TryRestoreVessel`, `HangarStorageDynamic` |
| **NAS/ASW** | depth-charge/torpedo settings, ASW events | P1 | module fields |
| **MNWS** | VLS loadout report and reload (`ModuleMissileRearm`) | P1 | **[R]** BDA modules on the cell parts (`MultiMissileLauncher`, `MissileLauncher`, `ModuleMissileRearm`); MNWS's own loaded-missile name is private |
| **PhysicsRangeExtender** | `ksp.physics_range.get|set` (control); batch runner sets range from scenario | P0 | BDA's public helper `PhysicsRangeExtender.GetPRERange/SetPRERange/IsPREEnabled` (`ModIntegration/PhysicsRangeExtender.cs:14-46`) |
| **Placement** (Skizzy's own, BDA VesselMover optional) | `ksp.vessel.place {vessel, lat, lon, alt?|"surface", heading, pitch?, roll?, speedMps?}`, `lift`, `drop` | P0 | **[R]** Skizzy places vessels itself: `body.GetWorldSurfacePosition(lat, lon, alt)`, `vessel.SetPosition`, `vessel.SetRotation`, `vessel.SetWorldVelocity`, `vessel.IgnoreGForces(n)` for the frames after the move, with Krakensbane offset handled and KJR given a settle frame. BDA's `VesselMover.Instance.PlaceVessel(v, skipMovingCheck: true)` (`VesselSpawning/VesselMover.cs:445`) is used only to lower a vessel gently onto land when BDA is present; `PickUpAndDrop(v, alt)` (`:542`) for `lift`/`drop` |
| **HyperEdit** | `ksp.vessel.teleport` incl. orbit (cheat) | P2 | `DoLander`, `OrbitEditor` by reflection; orbits can also be set with stock `Orbit.SetOrbit` + `vessel.orbitDriver` without HyperEdit |
| **AtmosphereAutopilot** | `flight.handoff {to:"atmosphereautopilot", mode}` and AA cruise setpoints (`desired_altitude`, `desired_course`) as a hold backend for aircraft | P1 | `TopModuleManager.activateAutopilot` |
| **KerbalEngineer** | validation cross-check (TWR, dV) | P2 | `SimManager` |
| **Trajectories** | bomb/impact prediction for strike planning | P2 | `API.GetImpactPosition` |
| **KerbalKonstructs** | spawn-site catalog for scenarios | P2 | `LaunchSiteManager.AllLaunchSites` |
| **WildBlueTools** **[R]** | ballast/dive-computer read-out in the SITREP; depth control only when SubTrim is absent | P3 | `WBIBallastTank`, `WBIDiveComputer` |
| **KSPCommunityFixes** | nothing to control; `skizzy doctor` verifies `MemoryLeaks = true` and reports KSPCF's leak warnings from the log | P0 | `Settings.cfg:406` |
| **BDArmory** (`Skizzy.BDArmory.dll`) | everything in `07-bdarmory-integration.md` | P0 | |

All adapters are soft: types are resolved once by name at startup (`AssemblyLoader.loadedAssemblies`), every member cached as a compiled delegate (`DynamicMethod`/`Expression.Compile` both work on this Mono; SubTrim's `FastAccess` and BDA's `ReflectionUtils` already use them), and a missing member disables only the capabilities that need it (`availability.reason = "api_missing"`). **[R]** A soft adapter can read, write and call, but it cannot *implement* a mod's interface or subclass a mod's type; where that is required (SubTrim v2, Tier 3 contracts), the adapter lives in its own small assembly with a hard reference and a `KSPAssemblyDependency`, so KSP skips it cleanly when the mod is missing.

## 6. Bulk access and token economy

- `ksp.query` and `tac.query` are the only sanctioned ways to read many objects; the bridge never exposes one tool per Tier 0 member.
- `format:"compact"` exists for `ksp.query` too: `v:118 DDG-1 | ModuleHullBreach x14: flooded max .31 avg .04 | MissileFire: guard on, away 6`.
- `ksp.module.describe` returns schemas with `x-skz-units`, `x-skz-step`, `x-skz-enumLabels` and `x-skz-dependsOn`, so an LLM gets the full semantics in one call and can cache it.

## 7. Extension points for other mods (sensors, unit types, symbology) — [R]

IDEAS 14.1 asks that other mods can add unit types, sensors, weapons and symbology. Three levels, cheapest first:

| Level | How | Example | Priority |
|---|---|---|---|
| Config only | `SKIZZY_PART_ROLES` entries (§3.1), `SKIZZY_CLASS` rules, and `symbols.json` entries (`11-tactical-map.md` §2) shipped by the mod or by the user | a new hull pack declares its VLS cells and its ship classes; the map draws them with the right 2525 icon | P1 (M3) |
| Annotated fields | Tier 1 `sensor = …` and `sitrep = …` keys on module fields: range, detection flag, bearing field | a sonar mod marks `contactBearing` as a bearing-only sensor output; the TPE turns it into bearing-only tracks | P2 (M8) |
| Provider interface | Tier 3 contract: `ISkizzySensorProvider { void Sample(IVesselView v, ISensorSink sink); }` and `ISkizzyUnitClassifier { bool TryClassify(IVesselView v, out UnitClass c); }`, called by the TPE in its time slice with pre-allocated sinks (no allocation, main thread only) | a custom EW mod contributes jamming strobes | P2 (M8) |

Providers are listed in `bda.hooks`-style `host.extensions` with their source assembly, and each one's time per tick appears in `host.stats` so a slow provider is visible and can be disabled from the in-game window.
