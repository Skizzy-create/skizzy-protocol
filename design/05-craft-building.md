# 05 Craft building

Three ways to build, one document model, one validator, one library.

```
blueprint.yaml ──assembler──► CraftDocument ──writer──► .craft ──spawn──► sea trials ──metrics──► library version
editor (VAB/SPH) ◄──round-trip──┘   ▲                                                  │
headless craft.* ops ───────────────┘   ◄──────────────── adjust (agent/script/human) ◄──┘
```

## 1. CraftDocument (P0)

An in-memory, dependency-free model of a `.craft` ConfigNode: header (`ship`, `version`, `type`, `size`, `description`), parts (`part`, `persistentId`, `pos`, `rot`, `attPos`, `attRot`, `mir`, `istg`, `dstg`, `sqor`, `sidx`, `link`, `attN`, `srfN`, `sym`), `MODULE` and `RESOURCE` nodes, plus derived data (tree, attach-node geometry from `PartLoader` prefabs, bounds, mass). It is the same structure the editor round-trip uses (PLAN v0.2 §8.1), so headless edits and editor edits are the same code.

Operations (`craft.*`, affinity `main` for anything touching `PartLoader`, otherwise `any`): `open {path|library}`, `create {facility, name, rootPart}`, `add_part {parent, part, attach:{node,parentNode}|surface:{pos,normal}, symmetry}`, `remove_part`, `move|rotate|offset`, `set_module_field` (PAW-validated against the prefab's UI ranges), `set_resource`, `set_stage`, `set_action_group`, `rename`, `diff {docA, docB}`, `save {path|library, overwrite}`, `validate`, `thumbnail` (P2), `apply {ops:[…]}` (batch). Documents are `doc:N` handles with an expected content hash to avoid lost updates.

## 2. Editor control (`ksp.editor.*`, P1)

As PLAN v0.2 §8.1: ConfigNode round-trip through `ShipConstruct.SaveShip/LoadShip` + `EditorLogic.SetBackup` for undo, `ksp.editor.apply` for batches (one reload per batch), `ksp.parts.catalog` with role filters (`role:"vls"`), `ksp.editor.validate`, save to `Ships/` or `AutoSpawn/`. The editor path is required for: live visual check (screenshot after apply), B9PartSwitch/TweakScale state that only resolves in the editor, and KerbalEngineer readouts. The reload-path spike (which API replaces the ship cleanly) stays the first task of the craft milestone.

## 3. Blueprint DSL (P1)

A blueprint is a YAML file that names a hull family, fills hull slots by role, sets loadouts and AI/WM configuration. The assembler resolves roles to concrete parts through the part-role catalog and `HULL_SLOTS` (`04-ksp-and-mod-access.md` §3.1).

```yaml
blueprint: ddg-burke-v3
class: destroyer
hull:
  family: lbp.ddg            # LShipPartsModern destroyer hull sections
  sections: [bow, fwd, mid, aft, stern]
  length_m: 155
slots:
  fwd_vls:   { role: vls, part: mnws.MissleCell2050, count: 4, loadout: { ESSM: 32 } }
  aft_vls:   { role: vls, part: mnws.MissleCell2050, count: 4, loadout: { Harpoon-ish: 8, SM-2: 24 } }
  fwd_gun:   { role: gun.main, prefer: [mnws.127mm, ironseas.5in] }
  ciws_fwd:  { role: ciws, part: mnws.35mmAAMS }
  ciws_aft:  { role: ciws, part: mnws.35mmAAMS }
  radar_main:{ role: radar.air, part: mnws.SPY1D }
  sonar:     { role: sonar.hull, prefer: [nas.hullSonar] }
  rwr:       { role: rwr }
  decoys:    { role: decoy.chaff, count: 2 }
  helo_deck: { role: hangar, optional: true }
systems:
  wm: { guardRange: 60000, multiTargetNum: 16, multiMissileTgtNum: 8, maxMissilesOnTarget: 2,
        parallelFire: true, parallelSalvoSize: 4, targetMissiles: true, targetWeightThreat: 1.5 }
  ai: { type: surface, SurfaceTypeName: Water, MaxSpeed: 18, CruiseSpeed: 12, MaxEngagementRange: 8000, BroadsideAttack: true }
  hullbreach: { crushDepth: 60 }
  action_groups: { 1: [radar.toggle], 2: [decoys.fire] }
constraints:
  max_parts: 450
  max_mass_t: 9500
  min_freeboard_m: 1.5
  gm_min_m: 0.8
```

Assembly algorithm: (1) instantiate hull sections in order along the hull axis using their stack nodes; (2) for each slot, pick a part whose role matches (explicit `part`, then `prefer` list, then any part with the role, preferring the pack the hull came from); (3) attach at the slot's node (or surface point with the slot's `offset`/`heading`), mirror-symmetric when `count` is even and the slot is `mirror:true`; (4) write module fields (loadout via `MultiMissileLauncher`/`ModuleMissileRearm` fields, WM/AI settings via the Tier 0 PAW path in the document); (5) place `MissileFire`, the AI and `ModuleWingCommander` as root children (BDA's `SpawnUtils.CheckAIWMPlacement` rule); (6) run validation; (7) return the document plus an **assembly report** (which part filled which slot, substitutions, unfilled optional slots).

Why hull slots are needed: Large Boat Parts hulls expose stack nodes but deck positions for weapons are surface attachments; without a curated or probed slot list the agent would have to guess coordinates. Slots are authored once per hull family (shipped for LBP destroyer/carrier hulls and the MNWS/Maritime hulls present in the mod pack) and can also be **probed**: `craft.probe_slots {hull}` raycasts a grid over the deck collider in the editor and proposes flat, unobstructed slots.

## 4. Validation report (P1)

| Check | Method | Output |
|---|---|---|
| Mass, CoM | Σ mᵢ(posᵢ + CoMOffset), wet and dry | t, m |
| CoL / CoT / TWR | `ILiftProvider`, `ModuleEngines.maxThrust` and `thrustTransforms` vs body surface gravity | per stage |
| Buoyancy and waterline | Σ ρ g Vᵢ·`part.buoyancy` with Vᵢ from drag cubes; binary search on draft; BuoyancyAdjuster overrides noted | draft m, freeboard m, reserve buoyancy % |
| **Initial stability (GM estimate)** [ADDED] | waterplane second moment from part footprints at the waterline → BM; KB from submerged volume centroid; GM = KB + BM − KG | m, with a warning below `gm_min_m` |
| Roll/pitch inertia | from part masses and positions | t·m² |
| WM/AI placement | BDA rule: WM and AI on root or direct child; root is command/seat | pass/fail with fix suggestion |
| Weapon coverage | turret arcs (`ModuleTurret` yaw limits) and VLS counts by role | blind arcs in degrees |
| Sensor set | radar/sonar/IRST/RWR present and powered (EC budget) | list |
| Part count, clipping | thresholds (500/1000), pairwise bounds overlap via spatial hash | warnings |
| Resource budgets | EC generation vs radar drain, fuel for `CruiseSpeed` endurance estimate | hours |
| HullBreach | compartments with `hull=true`, flood capacity vs displacement, crush depth vs design depth | |
| Mod dependencies | parts whose mod is not installed; MM patch presence | |

The report is JSON plus a compact text summary for agents.

## 5. Sea trials and flight trials (P1) — [ADDED]

`craft.trial {doc|library, profile}` is a long operation that spawns the craft at the test range (a dedicated save `saves/skizzy-range`, open water east of KSC; see decision list), runs a scripted profile under Skizzy holds and records metrics with a dedicated log profile:

| Profile | Measures |
|---|---|
| `naval.basic` | observed draft vs predicted, settled trim/list, top speed at full throttle, acceleration 0→10 kt, turning circle at 10/20 kt, roll period after a rudder kick, stopping distance |
| `sub.basic` | dive to 30/60/90 m under SubTrim, depth hold error, surface time, crush margin |
| `air.basic` | take-off speed, climb rate, sustained turn rate at 300 m/s, max speed, stall speed, fuel burn |
| `combat.smoke` | guard on vs a scripted drone: time to first detection, first lock, first launch; weapon arcs exercised |

Results are stored in the library version's `trials.json` and compared across versions (`lib.craft.compare`).

## 6. Library (P1)

`<KSP>/Skizzy/library/craft/<name>/<version>/` with `manifest.json` (`class`, `roles` summary, `blueprint` hash, `tags`, `author actor`, `created`), the `.craft`, `trials.json`, `battles.json` (aggregated results from recordings: survival rate, kills, missiles expended per kill), thumbnails. `lib.craft.list|get|promote|tag`, `lib.blueprint.*`, `lib.loadout.*`. BDA's `AutoSpawn` staging copies from the library.

## 7. Iteration loop (agent view, P1)

```
lib.blueprint.get ddg-burke-v2 → craft.assemble → craft.validate (GM 0.6 < 0.8: warning)
→ amend blueprint (lower VLS, add ballast) → craft.assemble → craft.validate ok → lib.craft.save v3
→ craft.trial naval.basic (draft 6.1 m predicted 6.4 m; top speed 17.8 kt)
→ sim.scenario.run "ddg-vs-raid-20" x3 (recorder profile battle.standard)
→ rec.query metrics: survival 2/3, ESSM per kill 2.4, CIWS saved 3 leakers
→ amend: maxMissilesOnTarget 2→3, parallelSalvoSize 4→6 → repeat
```

Each step is one or two tool calls; the whole loop is also available as a script template (`skizzy.templates.craft_iterate`).

## 8. Permissions

`craft.*` and `ksp.editor.*` are `read`/`control`; overwriting an existing file is `destructive` (ask); copying to `AutoSpawn` stays `cheat`. Paths are sandboxed to `Skizzy/library`, `saves/<profile>/Ships`, `AutoSpawn`.

## 9. Acceptance (summary; full list in `17-milestones.md` M5)

1. `craft.assemble` from the blueprint above produces a craft that loads in the SPH with identical part count and tree; validation reports GM, draft and WM/AI placement.
2. Headless edits round-trip through the editor without diffs (`craft.diff` empty).
3. `craft.trial naval.basic` predicts draft within 10 % and reports speed and turning circle; results appear in the library and in `lib.craft.compare`.
4. The destroyer scenario (`16-scenarios.md` §1) completes end-to-end from Claude Code.
