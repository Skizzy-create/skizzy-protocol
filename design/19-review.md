# 19 Review pass 1.1

Reviewer and gap-filler pass over design pass 1.0 (commit `cd337ef`), 2026-11-08. Every BDArmory claim was checked against the fork source (`D:\KerbalSpaceProgram\BDArmory\BDArmory`, branch `multitarget-1.13.1`, HEAD `ff5130c19`); mod API claims against the installed DLLs and patched sources; runtime claims against KSP's `KSP_x64_Data/Managed` assemblies and `boot.config`; Claude API claims against the current API reference. Changed text in the other files is marked **[R]**, new features **[REVIEW-ADDED]**.

**[R2] Design pass 2** (the command-model change, communications and diplomacy, spawn anywhere) is recorded in §9.

## 0. Summary

- **Coverage of `IDEAS.md`:** 77 bullets. Before: 56 covered, 21 partial, 0 missing. After: 77 covered. The user's additions made during the review (§8) add 13 more rows, all covered.
- **Top corrections:**
  1. BDA target override does not last 15 s. It is sticky while engageable, lost during a missile routine, dropped when not engageable, and can be overwritten by team assist. The re-issue rule was rewritten.
  2. BDA formation slots work by reflection on stock BDA, so fork patch F7 is not needed (now P2).
  3. BDA's `PlaceVessel` takes no position; Skizzy now places vessels itself.
  4. SubTrim's `IDepthCommandSource` cannot be implemented from a soft-reference assembly; v1 uses `manualDepth`.
  5. Unity 2019.4 has no `GC.GetAllocatedBytesForCurrentThread`, and KSP ships no `netstandard.dll`. Allocation measurement and build targets are redefined.
  6. The kill switch now also hands the active vessel back to the human.
  7. The dead-man rule wrongly applied to plain holds; it is now limited to direct and declared streams.
  8. Precedence was inconsistent. It is now one rule: role value clamped by ROLE `maxPrecedence`.
  9. The milestones were reordered. Connect, read, screenshot and log come in M0, flying in M1. Pass 1.0 had no screenshots in any milestone and no logging before M3.
  10. Performance budgets now separate loaded vessels from tracked records, gain a measurement method and per-milestone gates, and fix the unrealistic editor and screenshot numbers.
  11. Wrong mod API names were fixed: Hangar `TryStoreVessel`, HyperEdit `Model.Lander`, MNWS `GetLoadedMissileName`.
  12. The claim that PLAN M0/M1 code exists was wrong. There is no code yet.
- **Features added:** 34 `[REVIEW-ADDED]` rows (`01-feature-catalog.md` §15), 14 genre-reference rows (§16), 9 user rows (§1, §7, §9), two new design files (`20-runtime-lifecycle.md`, `21-user-experience.md`).
- **Decisions:** 4 left for the user, 24 decided by the review (`18-risks-and-decisions.md` §3; summary in §7 below).

## 1. Traceability matrix

Status: **C** covered, **P** partial, **M** missing. "Before" is pass 1.0, "after" is this pass. Priorities and milestones are those in `01-feature-catalog.md` and `17-milestones.md`.

| ID | IDEAS bullet (short) | Before | After | Where | M |
|---|---|---|---|---|---|
| 1.1 | host plugin, bridge exe, protocol library | C | C | 00 §2, 02 §1 | M0 |
| 1.2 | native socket + MCP, same API and permissions | C | C | 03 §1, §11 | M0 |
| 1.3 | dispatcher budget; zero work and zero allocations when idle | P: allocation measured with an API Unity lacks; network threads ignored | C | 02 §2, §2.3, §8.1 | M0 |
| 1.4 | fail closed, no exceptions into KSP, scene changes | C | C | 02 §5, 20 §5 | M0 |
| 1.5 | every feature from scripts, agents, map and game | P: in-game and map surfaces hand-built per feature; screenshots and logs had none | C: capability console, map palette, CLI | 21 §3–4, 11 §5 | M0/M3 |
| 2.1 | Tier 0 incl. crew | P: crew read-only, no crew operations | C | 04 §2 | M0 (read), M8 (ops, P2) |
| 2.2 | Tier 1 annotations | C | C | 04 §3 | M0–M2 |
| 2.3 | Tier 2/3 opt-in protocol | C | C | 04 §1, §7 | M8 |
| 2.4 | deep reflection, off per mod | C | C | 04 §1, 15 §1 | M1 |
| 2.5 | bulk queries | C | C | 04 §2, §6 | M0 |
| 2.6 | mod adapters | P: wrong member names (Hangar, HyperEdit, MNWS), WildBlueTools missing | C | 04 §3, §5 | M1–M8 |
| 3.1 | editor control | C | C | 05 §2 | M5 |
| 3.2 | blueprints / DSL | C | C | 05 §3 | M5 |
| 3.3 | headless craft | C | C | 05 §1 | M5 |
| 3.4 | validation | C | C | 05 §4 | M5 |
| 3.5 | iteration loop | C | C | 05 §7, 16 §1 | M5 |
| 3.6 | shared library | C | C | 05 §6, §10 | M5 |
| 4.1 | throttle, SAS, RCS, staging, gear…, vessel switch | C | C | 06 §2 | M1 |
| 4.2 | physics-rate setpoint holds incl. depth | P: SubTrim interface not implementable as designed; dead-man on plain holds | C | 06 §3, 04 §5 | M1 |
| 4.3 | waypoints, orbits, loiter, formation | P: "orbits" unaddressed; formation patch wrongly required | C | 06 §2, §5, §6 | M1/M4 |
| 4.4 | hand-off to/from BDA AI and AA | C | C | 06 §7 | M1 |
| 4.5 | human + agent co-flying | C | C | 06 §8, 20 §2 | M1/M4 |
| 4.6 | kill switch always restores manual control | P: BDA pilot kept flying the active vessel | C | 06 §10 | M0/M1 |
| 4.7 | scene setup and positioning | P: relied on `PlaceVessel`, which takes no position | C | 06 §9, 04 §5 | M1 |
| 5.1 | spawning rings and formations | C | C | 07 §3 | M2 |
| 5.2 | teams, guard, full WM config (1024/128/salvo) | C | C | 07 §2–3 | M2 |
| 5.3 | AI modes | C | C | 07 §3 | M1/M2 |
| 5.4 | live AI parameters | C | C | 07 §2 | M2 |
| 5.5 | commands attack/fly-to/follow/waypoints | C | C (silent no-op when the pilot is off now handled) | 07 §1.3, 06 §2 | M1/M4 |
| 5.6 | direct weapons | C | C | 07 §2–3 | M2 |
| 5.7 | competitions, scores, settings, logs | C | C | 07 §1.6, §7 | M2 |
| 5.8 | small upstream hooks | P: override semantics wrong; F7 not needed | C | 07 §1.3–1.4, §5 | M2/M4 |
| 5.9 | modified BDA AI per unit | C | C | 07 §4, 10 §5 | M6 |
| 6.1 | per-team fused track table | C | C | 08 §1 | M2 |
| 6.2 | fog-of-war or god view | C | C | 08 §2 | M2 |
| 6.3 | filter by area/role/team/threat; frames; top-N | P: no role filter; no output caps | C | 08 §6, 03 §11 | M2 |
| 6.4 | deltas and events only, never a token dump | C | C | 03 §5, 08 §8 | M2 |
| 6.5 | compact encodings | C | C | 03 §6 | M2 |
| 6.6 | fast loops never wait on an LLM | C | C | 02 §3.3, 06 | M1/M4 |
| 7.1 | orders as objects | C | C | 09 §2 | M4 |
| 7.2 | hierarchy and roles | C | C | 09 §3 | M4/M6 |
| 7.3 | leases | C | C | 09 §4 | M1/M4 |
| 7.4 | precedence human > commander > unit > script > BDA | P: script cap 400 contradicted "doctrine can promote scripts"; two field names | C | 15 §2, 09 §4, 00 §3 | M1/M4 |
| 7.5 | team tactics library | C | C | 09 §6 | M6/M8 |
| 7.6 | model per role; events pushed | C | C | 09 §8 | M2/M6 |
| 7.7 | time control for thinking | C | C | 09 §8.4 | M6 |
| 7.8 | LLMs one option | C | C | 00 §3 | M0 |
| 8.1 | SDKs Python and C# | C | C | 10 §1–2 | M0/M4 |
| 8.2 | templates and plug-ins | C | C | 10 §4–5 | M4/M6 |
| 8.3 | modified BDA AI layering | C | C | 10 §5, 07 §4 | M6 |
| 8.4 | scenario and doctrine files | C | C | 10 §7, 09 §5 | M6/M7 |
| 9.1 | live map with symbols per type | P: no concrete symbol mapping | C | 11 §2 | M3 |
| 9.2 | layers | C | C | 11 §3 | M3 |
| 9.3 | planning for humans and agents | C | C | 11 §6 | M4 |
| 9.4 | fog or god view | C | C | 11 §4 | M3 |
| 9.5 | external app and in-game overlay | C | C | 11 §8 | M3 |
| 10.1 | log anything at any rate | P: only through the full recorder, not before M3 | C | 12 §1.1 | M0 |
| 10.2 | saved as CSV/JSONL/Parquet, profiles | P: CSV/JSONL only by export | C | 12 §1.1, §2 | M0/M2/M5 |
| 10.3 | retrieve any time from scripts, agents, map | P: query only in the bridge | C | 12 §4 | M2 |
| 10.4 | full recording, replay, agent context or training data | P: no training-data format | C | 12 §4, §6 | M2/M3/M8 |
| 10.5 | metrics and A/B | C | C | 12 §5, 14 §5 | M5/M7 |
| 10.6 | audit log | C | C | 12 §7, 15 §7 | M0 |
| 11.1 | screenshots as images | P: in no milestone; 30 ms main-thread stall | C | 13 §1 | M0 |
| 11.2 | camera modes | C | C | 13 §1.2 | M0 |
| 11.3 | on demand | C | C | 13 | M0 |
| 12.1 | headless/batch mode | P: KSP cannot run headless; not stated | C: unattended batch + headless CI world | 14 intro, §4 | M7 |
| 12.2 | repeats with statistics | C | C | 14 §5 | M7 |
| 12.3 | A/B comparisons | C | C | 14 §5 | M7 |
| 12.4 | validation of the user's mods | C | C | 14 §6 | M7 |
| 13.1 | levels allow/ask/deny | C | C | 15 §1 | M0 |
| 13.2 | defaults | C | C | 15 §1 | M0 |
| 13.3 | safeguards | C | C | 15 §6 | M0–M4 |
| 14.1 | other mods add unit types, sensors, weapons, symbology | P: annotation keys only | C | 04 §7, 11 §2 | M3/M8 |
| 14.2 | later extras | C | C | 01 §14 | P2/P3 |
| C1 | C# in KSP and bridge | C | C | 02 §1 | all |
| C2 | performance first at 100–1000 vessels | P: loaded and tracked mixed; no method; unrealistic numbers | C | 02 §8 | all |
| C3 | don't break the game; installed-mod compatibility | P: no interference analysis; save writes not ruled out; assembly conflicts | C | 20 §5, §9, 02 §1 | M0 |

Late rows from the user's additions on 2026-11-08 (§8):

| ID | Requirement | Before | After | Where | Pri / M |
|---|---|---|---|---|---|
| L1 | command-screen layout: resizable, docking, saved layouts, zoom/pan/fit | M | C | 11 §4.1, §5.1 | P0 / M3 |
| L2 | individual tracks always; counts only as an optional view | M | C | 11 §2.1 rule 1 | P0 / M3 |
| L3 | density- and zoom-aware sizing, symbol → small → dot, never hide or merge | M | C | 11 §2.1 rule 2 | P0 / M3 |
| L4 | RCS visual hierarchy with overrides | M | C | 11 §2.1 rule 3 | P0 / M3 |
| L5 | per-track readability: altitude, trails, target lines, event markers, label declutter | M | C | 11 §2.1 rule 4 | P0 / M3 |
| L6 | filters and highlight | P (filters existed) | C | 11 §2.1 | P0 / M3 |
| L7 | replay and slow-motion scrubbing | P (replay existed) | C | 11 §2.1 rule 5, 12 §2 | P0 / M3 |
| L8 | thousands of symbols at 60 fps | P (1000 tracks) | C | 11 §10 | P0 / M3 |
| L9 | memos pinned to tracks or points | P (annotations) | C | 11 §7 | P1 / M4 |
| L10 | optional layer, zero cost with nothing connected | P (idle gate only) | C | 00 (principle), 02 §8 | P0 / M0 |
| L11 | parallelism where cheap and safe | M | C | 02 §2.4 | P2 / M8 |
| L12 | CMO-style genre features (14 items) | P (about half) | C | 11 §12, 09 §11, 01 §16 | P0–P2 / M2–M8 |
| L13 | Mission command **plus** direct control:<br>• per unit/group mission-controlled vs manual state on the map<br>• one-click take-over and release<br>• the human lease preempts<br>• hand back to the mission<br>• Skizzy's own strengths beyond the genre kept up front | P (implicit stick lease and supersede-on-preempt only) | C | 09 §4.1, §11.7; 11 §6.1, §12; 01 §7; 03; 08 §7 | P0 / M4 (orders), M6 (missions) |

## 2. Corrections (file → change → why)

| File | Change | Why |
|---|---|---|
| 00 | status 1.1; "no code exists yet"; principle "optional layer, zero cost"; actor table with `maxPrecedence`; `actor.kind` vs `client.kind`; D2 in-house msgpack in-game; D3 network threads zero-alloc; namespaces `flight.`, `permissions.`; P0–P3 bound to milestones; line numbers are navigation aids only | repo has only documents; user principle; precedence inconsistency; KSPBurst DLL conflicts; Boehm GC is one shared heap |
| 01 | priority buckets redefined (P0 by M4, P1 by M7, P2 by M9); CraftDocument P0→P1, model per role P0→P1, Tier 2/3 P1→P2, overlay P2→P1, live map P1→P0; §15 review additions; §16 genre rows; user map rows | old buckets were unreachable with the old milestones; the user's map requirements |
| 02 | Protocol built twice (net48 in-game / netstandard2.0 outside) with a build guard; soft refs cannot implement interfaces; zero-alloc on all threads; JPEG encode on the writer thread; TPE time-sliced; allocation measured by heap slope in-game and exact asserts in CI; dead-man limited to direct and declared streams; pause resets `timeScale`; `detectedTime` is `Time.time`; exact lockstep stepping; one `onExpire` enum; new failure rows (active vessel, packed, port, restart); data layout adds logs/screenshots/endpoint; budgets rewritten (§8, §8.1–8.3); §2.4 parallelism | no `netstandard.dll` and no per-thread allocation counter in Unity 2019.4; unrealistic or unmeasurable budgets; user requirement |
| 03 | kinds split; precedence clamp; `c2.control.*` methods, `control.*` and `lease.suspended/restored` events, order reason `borrowed`; method families completed (`host.sessions`, `host.kill_switch`, `tac.summary`, `c2.lease.grant/delegate/renew`, `c2.ask_human`, `c2.resume`, `c2.behaviour.*`, `c2.mission.*`, `rec.log.*`, `rec.profile.get/save/delete`, `rec.get_file`, `rec.append`, `sim.scenario.run`, `flight.stage/action_group/orbit_point`, `ksp.vessel.place/spawn_craft/rename`, `ksp.crew.*`, `session.pause_on`); `suspended` non-terminal, `orphaned` is an event; events `order.cancelled/suspended/resumed`, `vessel.*`, `mission.*`, event `category`; unified error taxonomy with `KILL_SWITCH_ACTIVE`, `DUPLICATE_NAME` and reasons; builder toolset gains `sim`; core toolset contents; output caps | methods used in other files were undefined; two error tables; token-dump risk |
| 04 | `spawn_craft`, crew ops, rename = control; PAW path stated; MNWS loadout from BDA modules; Hangar `TryRestoreVessel`/`HangarStorageDynamic` (no `TryStoreVessel`); KK `LaunchSiteManager.AllLaunchSites`; HyperEdit `DoLander` (no `Model.Lander`); WildBlueTools row; PRE via BDA's helper; SubTrim v1 `manualDepth`, v2 separate assembly + priority option; Skizzy's own placement; §7 extension points | names checked in the installed DLLs; soft references cannot implement interfaces |
| 05 | CraftDocument P1; editor reload ≤ 1.5 s target (was 150 ms); consolidated capability list incl. `lib.craft.save/compare`, `lib.blueprint.patch` | names used in scenarios were undefined; 150 ms not credible |
| 06 | callbacks only on unpacked vessels; `IsControllable`; staging resolver; `flight.orbit_point`; `pilotEnabled` checks; SubTrim v1; hold lifetime (no dead-man); setpoint streams with `deadmanMs`; native waypoints only when safe; formation slots by reflection; human override uses trim-relative axes and throttle *changes*; active-vessel switch rule; placement implemented by Skizzy; kill switch deactivates the BDA pilot on the active vessel; new acceptance items | verified BDA behaviour; IDEAS 4.3/4.6/4.7 |
| 07 | review header; `Command*` silent no-ops; formation dictionary is mutable; waypoint coupling details (`WAYPOINT_*` globals, `:572`/`:603`); `IsControllable` gate; **Override semantics** and the re-issue executor; `detectedTime` is `Time.time`; per-team dictionary costs; scores keyed by name; `OnChangeTeam`; hook table rows fixed; F1/F3/F6/F8 line refs (`SmartFindTargetInternal` is `:5872`); F7 P0→P2 | checked against the fork source |
| 08 | packed/unloaded/debris sources; `role` and `state` filter atoms; output caps; `tac.summary`; time slicing; BDA access costs; loaded vs tracked; `idState`; `ctl` control-state token in the SITREP | IDEAS 6.3; budgets; genre flow |
| 09 | `onExpire` validity per channel; precedence clamp; delegation semantics; BDA does not know about leases (re-issue rule); builder toolset; Haiku price tier above 100 K; append-only history with compaction (trimming invalid); refusal handling; §11 genre features (doctrine inheritance, missions, EMCON, classification, posture, message log, pause-on-event) and §11.7 Skizzy's strengths beyond the genre; **preemption by a human now suspends instead of superseding**, with a suspension stack per channel (§4 rules 2 and 8); new §4.1 mission command plus direct control (control state, `c2.control.take/release`, one-shot interventions, Fly in KSP, re-plan on return, disconnect grace) | current Claude API behaviour; consistency; genre reference; pass 1.0 superseded an agent's orders when the human preempted, so take-over could not be undone, which contradicted 06 §8 (holds resume after the human lets go) |
| 10 | `skizzy` 0.1 sync client in M0; setpoint batch with dead-man; `scripts.status` → `host.sessions`; `ad_coordinator` needs targeting/weapons grants | undefined method; ROLE defaults |
| 11 | `symbols.json` with 2525D symbol sets (entity codes deliberately not quoted from memory); command palette; overlay P1 v1 in M3; §2.1 clarity at scale (corrected: individual tracks always); §4.1 zoom; §5.1 docking; memos; sprite-atlas rendering; §12 genre features; §6.1 control badges and halos, one-click Take control / Return, Manual units panel, Fly in KSP | IDEAS 9.1; user requirements |
| 12 | §1.1 log v0 (direct CSV/JSONL from M0); no auto-delete retention; host-side `rec.query`; training-episode export; `battle.detailed` profile | IDEAS 10.1–10.4; replay of dense engagements |
| 13 | screenshots in M0; `AsyncGPUReadback` pipeline (≤ 3 ms), APIs checked in KSP's Unity; camera mode mapping; `ksp.camera.save` | IDEAS 11.1; 30 ms stalls |
| 14 | "headless" means unattended; soak tiers; CI allocation asserts; in-KSP synthetic load; unattended profile; range save decided | honesty; measurement |
| 15 | `preemptMax` → `maxPrecedence`, script cap 500, `behaviour` and `harness` roles; staging and rename levels; kill-switch checklist | consistency with 00/03/09 |
| 16 | §0 first-contact walkthrough; tool names fixed (`naval.carrier.*` via orders, `lib_blueprint_patch` defined); screenshot budget | scenario for M0/M1; undefined names |
| 17 | rewritten: M0–M9, no existing code, spikes first, acceptance and perf gates per milestone | user priorities; testability |
| 18 | decisions consolidated (4 for the user, 24 decided); risks R24–R29; rejected alternatives added; spikes per milestone | |
| IDEAS | the user's 2026-11-08 additions (map layout, clarity with the correction, memos, genre reference, optional-layer principle, parallelism) | requested by the user |

## 3. Features added

- **`[REVIEW-ADDED]`** (34 rows, `01-feature-catalog.md` §15). Highlights:
  - in-game capability console (P0)
  - log v0 (P0)
  - host `rec.query` (P0)
  - `AsyncGPUReadback` screenshots (P0)
  - vessel lifecycle state machine (P0)
  - active-vessel change handling (P0)
  - warp and pause policy (P0)
  - quickload handling and auto-snapshot (P1)
  - no `.sfs` writes (P0)
  - duplicate-name handling (P0)
  - splits and debris (P0)
  - long-session hygiene (P0)
  - mod-interference doctor (P0)
  - auto-reconnect and port fallback (P0)
  - first-run wizard (P0)
  - zip install (P0) and CKAN (P2)
  - docs and examples (P0/P1)
  - no-Claude-Code path (P0/P1)
  - upgrades (P1)
  - unified error taxonomy (P0)
  - net48 build guard (P0)
  - in-game allocation gate (P0)
  - time-sliced TPE (P0)
  - output caps and `tac.summary` (P0)
  - `flight.orbit_point` (P1)
  - crew operations (P2)
  - staging resolver (P1)
  - unattended batch profile (P2)
  - sensor and unit providers (P2)
  - `symbols.json` (P1)
  - orchestrator refusal handling and compaction (P1)
  - training export (P2)
  - lockstep exact stepping (P2)
  - map command palette (P1)
- **Genre reference rows** (14, `01-feature-catalog.md` §16):
  - NTDS option
  - track uncertainty display
  - emission badges and EMCON layer
  - EMCON
  - doctrine inheritance
  - missions
  - reference points and zone roles
  - message log
  - losses and scorecard
  - time bar and pause-on-event
  - magazine view
  - classification flow
  - side posture
  - scenario editor
- **User rows from 2026-11-08** (`01-feature-catalog.md` §1, §9):
  - optional-layer principle
  - parallelism
  - command-screen layout
  - clarity at scale with individual tracks
  - replay and slow motion
  - rendering performance
  - memos
  - mission command plus direct control (`01` §7)
- **New files:** `20-runtime-lifecycle.md` (vessel states, active-vessel switches, warp/pause, lockstep, quickload and saves, duplicate names, splits, long sessions, mod interference, connection resilience) and `21-user-experience.md` (first run, install, in-game UI inventory, other clients, docs, upgrades).

## 4. Verified and unverified claims

### 4.1 Verified (source checked in this pass)

| Claim | Evidence | Result |
|---|---|---|
| `SetOverrideTarget` "15 s, re-issue every 10 s" | `MissileFire.cs:11164-11166` sets only `overrideTarget`; `overrideTimer` `:439`; `TargetOverride` `:441-443` = `overrideTimer > 0`; consumed in `SmartFindTargetInternal` (`:5872`) at `:5947-5965` (timer = 15 at `:5957` when engageable, nulled at `:5965` when not); decay and null at the end of `GuardMode` `:10044-10051`; scan skipped while `guardFiringMissile` (`:9944`, public at `:638`) | **wrong**, rewritten (07 §1.4 "Override semantics") |
| `SetWaypoints` coupled to courses (`:540-626`) | `BDGenericAIBase.cs:540` `SetWaypoints(List<Vector3>)`; `UpdateWaypoint` `:560` reads course `scale` `:572` (unless `WAYPOINTS_SCALE > 0`) and `maxSpeed` `:603` always | **correct**, refined (IndexOutOfRange risk, globals) |
| `displayedTargets`/`displayedIRTargets` private | `VesselRadarData.cs:141-142` `private List<…>` | correct |
| `FireCurrentMissile` private | `MissileFire.cs:4632` | correct |
| formation slots need a patch | `ModuleWingCommander.cs:687` `readonly Dictionary<int,Vector2>`: contents mutable | **wrong**, reflection works; F7 → P2 |
| `Command*` behaviour | `BDGenericAIBase.cs:426,445,455,470` return when `!pilotEnabled` | new constraint |
| guard requires controllability | `MissileFire.cs:2477` `guardMode && vessel.IsControllable` | new constraint |
| F-patch line refs | `GuardMissileLaunchAuthorized` `:9200`, `GuardParallelSalvoRoutine` `:9324`, `GuardTurretRoutine` `:2732`, `GuardMode` `:9877`, `SmartFindSecondaryTargets` `:6178`, `FireParallelMissile` `:9853`, `autoPilot` `:111` (registered `:180`), `FlyOrbit(…, 2500, …)` `BDModulePilotAI.cs:5335,5357` | fixed (`SmartFindTargetInternal` is `:5872`) |
| scoring keys | `Scoring.cs:26` `Dictionary<string, ScoringData>` by vessel name, `:29` `deathOrder` names | new constraint (20 §6) |
| team-change event | `MissileFire.OnChangeTeam` static event `:1173` | added |
| detection timestamps | `TargetInfo.detectedTime` uses `Time.time`; `BDTeam.GetHashCode` hashes the name (`BDTeam.cs:79`); `TargetList(team)` returns the live list (`BDATargetManager.cs:1358`) | added |
| BDA placement | `VesselMover.PlaceVessel(Vessel, bool skipMovingCheck)` `:445` lowers from the current position; `PickUpAndDrop` `:542` | **wrong use**, Skizzy places itself |
| BDA PRE helper | `ModIntegration/PhysicsRangeExtender.cs:14-46` public static `CheckForPhysicsRangeExtender`, `IsPREEnabled`, `GetPRERange`, `SetPRERange(int)` | used instead of own reflection |
| team relations | `BDTeam.Allies` list, `Neutral` flag (`BDTeam.cs:16-19`) | used for posture |
| Hangar API | installed `Hangar.dll`: `TryRestoreVessel`, `LaunchVessel`, `HangarStorageDynamic`, no `TryStoreVessel` | **wrong**, fixed |
| HyperEdit API | installed `HyperEdit.dll`: `DoLander`, `OrbitEditor`, `MiscEditor`, no `Model.Lander` | **wrong**, fixed |
| KerbalKonstructs API | `LaunchSiteManager.allLaunchSites` field and `AllLaunchSites` property | clarified |
| MNWS loadout | `GetLoadedMissileName` is private in `ModuleInjectTransforms` | **wrong**, read BDA modules |
| WildBlueTools | `WBIBallastTank`, `WBIDiveComputer` present | row added |
| SubTrim API | `Api.cs`: `IDepthCommandSource`, `IDepthCommandSourceFactory`, `SubTrimRegistry`; selection does not let an external source outrank the AI source | v1 via `manualDepth` |
| Unity runtime | KSP Unity 2019.4.18f1, `mono-2.0-bdwgc`, `boot.config` `gc-max-time-slice=3`; no `netstandard.dll` in `Managed`; mscorlib has no `GC.GetAllocatedBytesForCurrentThread`; `Span<T>` internal; `UnitySynchronizationContext` present; `ConcurrentQueue`, `Interlocked`, `Stopwatch` present | budgets and build redefined |
| screenshot APIs | `ScreenCapture.CaptureScreenshotIntoRenderTexture`, `AsyncGPUReadbackRequest`, `ImageConversion.EncodeArrayToJPG/PNG` present in KSP's modules | pipeline adopted |
| serialiser conflicts | KSPBurst ships `System.Runtime.dll` and `System.Runtime.CompilerServices.Unsafe.dll` | in-house msgpack in-game |
| compiled accessors | `DynamicMethod`/`Expression.Compile` used by SubTrim `FastAccess` and BDA `ReflectionUtils` in this install | feasible |
| Harmony | `000_Harmony` (HarmonyKSP 2.2.1) installed and used by KSPCF, TweakScale, Deferred | optional stopgap feasible |
| own WebSocket | `TcpListener` and `System.Security.Cryptography.SHA1` present in KSP's mscorlib/System | RFC 6455 server feasible (PLAN decision kept) |
| KSP events | `onVesselPersistentIdChanged`, `onVesselGoOnRails/OffRails`, `onVesselLoaded/Unloaded`, `onVesselSwitching`, `onVesselChange`, `onGamePause/Unpause`, `onGameSceneLoadRequested`, `onTimeWarpRateChanged`, `onVesselWasModified`, `onVesselPartCountChanged`, `onShowUI/HideUI`, `OnFlyByWire`, autopilot update hooks, `IsControllable` | used in 20 |
| Claude API | ids and prices (`claude-opus-5-5` $4/$20, `claude-haiku-5-5` $0.10/$0.50 up to 100 K then $0.50/$2.50, `claude-sonnet-5-5` $2/$10); forced `tool_choice` is a 400 on Opus/Sonnet 5.5; effort defaults medium; adaptive thinking default; compaction beta `compact-2026-01-12`; thinking blocks valid only in an append-only history; `stop_reason: "refusal"` | 09 updated |
| repository state | `git ls-files`: only documents | "no code yet" |

### 4.2 Not verified here (each is a spike in `17-milestones.md`)

- `EncodeArrayToJPG` safe off the main thread (M0); `AsyncGPUReadback` support on the user's graphics API (M0).
- KSP restores `Time.timeScale = 1` on unpause, and how physics warp interacts with holds (M1; expected from KSP behaviour, not read in code).
- Placement with KJR and Krakensbane offsets (M1).
- How often BDA team assist overwrites assignments in practice (M2); VRD reflection cost at 200 radars (M2); whether BDA's spawner already de-duplicates names (M2).
- `Dock.Avalonia` on Avalonia 11; Skia `DrawAtlas` throughput (M3); 2525D entity codes (filled from the standard's tables in M3, not quoted from memory).
- `CommandFlyTo` arrival behaviour for the surface AI (M4).
- Editor reload time at 300 parts (M5).
- Streamable HTTP actor identity in the MCP C# SDK; compaction over a 2-hour battle (M6).
- ACA catapult sequence; passive-sonar noise model (M8).
- Claims carried from PLAN v0.2 without re-checking: the PAW write path details (PLAN §4.5) and the editor round-trip APIs (PLAN §8.1).

## 5. Performance review

- **Picture ≤ 1 ms at 1000 vessels.** It is now stated as amortised ≤ 0.25 ms per physics step and ≤ 1.0 ms in the worst step, at 1000 *tracked* records via time slicing. 1000 *loaded* vessels is not achievable in KSP, so the figure is tested with the in-KSP synthetic mode. Realistic battles have 50–200 loaded vessels and carry their own budgets (`02-architecture.md` §8).
- **0 B per tick.** Exact asserts per hot path run in CI under .NET 10. In-game, a heap-slope gate (≤ baseline + 16 KB/s over 10 minutes) and a memory gate apply, because Unity's mscorlib cannot count per-thread allocation. The rule now covers the network threads too.
- **Recording.** About 90 KB/s at 200 vessels × 5 Hz, and about 330 KB/s with `battle.detailed`. Nothing is deleted automatically; a warning fires at 20 GB.
- **Bandwidth.** Map 20–60 KB/s; swarm about 40 KB/s; LLM top-10 about 600 characters. Output caps enforce "no token dumps".
- **Screenshots.** ≤ 3 ms main thread with async readback, ≤ 30 ms on the fallback.
- **Editor batch.** The target is ≤ 1.5 s at 300 parts, to be set by the M5 spike (pass 1.0's 150 ms was not credible).
- **Map.** 5000 individually drawn symbols at 60 fps.
- **Gates.** Each milestone runs the subset that applies (`02-architecture.md` §8.3). Soak tests run at 30 min (M2), 2 h (M4) and 4 h (M9).

## 6. Milestones

The new order is in `17-milestones.md`:

- M0: connect, read, screenshot, log, in-game console.
- M1: fly, place, kill switch.
- M2: BDA core, compact picture, recorder.
- M3: map v1 with the user's clarity rules and replay.
- M4: control depth and C2 basics.
- M5: craft and analytics.
- M6: multi-agent.
- M7: batch.
- M8: naval depth and extensibility.
- M9: release.

Each milestone has spikes first, acceptance tests that can be run in-game, performance gates and its governing files. Each is sized for one Opus agent working through PR-sized tasks. The total is about 45 weeks.

## 7. Decisions

**For the user** (defaults assumed until answered; details in `18-risks-and-decisions.md` §3.1):

1. **Fork patch coordination and upstream timing.**
   - Default: small PRs (F1/F2/F5 first), opened after the current multi-target/leak work is merged and reviewed by the user. Pitch upstream after M6.
   - Why: another agent is changing the fork now, and every patch has a fallback.
2. **LLM spending caps.**
   - Default: commander $3, unit agent $0.50, total $8 per battle. Batch runs never use LLMs unless a scenario opts in.
   - Why: this stops runaway loops with headroom over the estimates.
3. **First hull families for slot templates.**
   - Default: LBP destroyer, LBP carrier, one MNWS/Maritime hull.
   - Why: they are in the user's pack; the rest use the probe.
4. **Order after M2.**
   - Default: map, then control depth, then craft.
   - Why: the map and the compact picture were the most-missed items.

**Decided by the review** (`18-risks-and-decisions.md` §3.2, D-1…D-24):

- Battle Lab permissions.
- Avalonia map.
- `skizzy.exe` with an alias.
- Competition as referee.
- The range save created by the wizard, with loading set to ask.
- Python `anthropic` SDK orchestrator.
- Loopback only.
- Harmony optional and off.
- No auto-delete.
- `hideUi` false.
- No Lua.
- Fog defaults kept and tunable.
- MCP SDK pin policy.
- SubTrim via `manualDepth`.
- The kill switch releases the active vessel's BDA pilot.
- Staging resolver.
- Rename is control.
- No `.sfs` writes.
- CommNet: warn only.
- Individual tracks always on the map.
- `Dock.Avalonia` for docking.
- At most one in-game worker thread, and not before M8.
- Models per role.
- Take-over and hand-back defaults (D-24).

## 8. Requirements the user added during the review (2026-11-08)

| Requirement | Folded into |
|---|---|
| **A. Command screen.** Resizable window; dockable, detachable panels; layouts saved per user; smooth zoom/pan; fit-to-selection and fit-all. | IDEAS §9; `11-tactical-map.md` §4.1, §5.1; `01` §9 (P0); `17` M3 |
| **A, as corrected by the user.** Clarity at scale:<br>• individual tracks by default<br>• counts only as an optional view<br>• density- and zoom-aware sizing without overlap, degrading symbol → small symbol → dot, never hiding or merging<br>• RCS hierarchy with overrides<br>• altitude labels and ticks, trails/leaders, missile target lines, intercept/impact/kill markers<br>• label declutter that never hides symbols<br>• filters and highlight<br>• replay and slow-motion scrubbing<br>• thousands of symbols at 60 fps | IDEAS §9 (rewritten per the correction); `11-tactical-map.md` §2.1 rules 1–5, §10, §11 items 8 and 11; `12` `battle.detailed`; `01` §9 (P0); `17` M3; `18` D-20, R19 |
| **A. Memos** pinned to tracks or points | IDEAS §9; `11-tactical-map.md` §7; `01` §9 (P1); `17` M4 |
| **B. Parallelism.** Free outside KSP; in-game only pure math on snapshot copies on one worker, where measured; P2; must not complicate M0–M3. | IDEAS constraints; `02-architecture.md` §2.4; `01` §1 (P2); `17` M8; `18` D-22 and a rejected alternative (Jobs/Burst) |
| **C. Principle.** BDArmory guard mode is unchanged with nothing connected; everything is an optional layer; zero cost when unused. | IDEAS constraints; `00-overview.md` (principle paragraph); `01` §1 (P0); `17` acceptance M0 item 1 and the rule for every milestone; `21` §1 |
| **Genre reference: *Command: Modern Operations*** (features only, not UI) | IDEAS §9 (reference bullet); `11-tactical-map.md` §12; `09-c2-multi-agent.md` §11; `01` §16; `03` (event `category`, `mission.*`, `session.pause_on`); `08` (`idState`); `17` M2–M8 |
| **Genre reference, as clarified by the user:** CMO-style mission and doctrine command **plus** Skizzy's own capabilities, not instead of them:<br>• command at mission level<br>• drop to direct micromanagement of any unit or group at any time (steer, pick target, fire this missile, hold fire)<br>• hand back to the mission<br>• seamless switching shown per unit with one-click take-over and release<br>• extras kept up front: real physics and BDA AI, LLM and script actors, flying a unit yourself, craft building, full logging and replay | IDEAS §7 (new bullet) and §9 (reference bullet); `09-c2-multi-agent.md` §4 rules 2 and 8, §4.1, §11 intro, §11.2, §11.7; `11-tactical-map.md` §6, §6.1, §11 item 12, §12; `01` §7, §16; `03` (`c2.control.*`, `control.*`, `lease.suspended/restored`); `06` §8; `08` §7 (`ctl`); `21` §3; `17` M4 and M6 (scope, demo, acceptance); `18` D-24 |

Genre coverage check:

- **Already covered before:**
  - 2525 symbology and posture colours
  - sensor and weapon range layers
  - zones and bullseye
  - the events taxonomy (the message content)
  - time scale control
  - the scenario files and the SITREP magazine data
- **Added:**
  - NTDS option (P2)
  - uncertainty ellipses and wedges (P1)
  - emission badges and the EMCON executor (P1)
  - doctrine inheritance from side to mission to group to unit, with winchester/bingo/withdraw (P1)
  - missions as first-class objects (P1)
  - reference points and zone roles (P1)
  - a filtered message log (P0)
  - the losses panel and scorecard (P1)
  - pause-on-event (P0)
  - magazine view (P0)
  - the detected → classified → identified flow (P1)
  - side posture (P1)
  - the map scenario editor (P1)

## 9. Pass 2: command-model change (2026-11-08) — [R2]

**Inputs.**

- `IDEAS.md` §15 (commit `e6f48b0`) and §16 (commit `237827d`).
- Three user requirements relayed during the pass:
  - spawn anywhere as a first-class feature from M1;
  - multi-spawn in a line with clear-space finding, also as a BDArmory Overdrive feature;
  - the correction that the multi-spawn count has no cap.
- BDArmory claims re-checked read-only against the fork at `db8db1078` (branch `multitarget-1.13.1`). Another agent is committing there, so members are cited by name with the line at that commit.

Changed text carries **[R2]**.

### 9.1 What changed, by file

| File | Change |
|---|---|
| `00-overview.md` | Status and vision: any AI client, one commander per side, BDA fights. Layer table, architecture diagram, actor table (commander 800, delegate 600/700, advisor and observer, harness 500), seat paragraph, `client.kind` without `orchestrator`, the referee, D10 rewritten, reading order with doc 22 |
| `01-feature-catalog.md` | §7 rows re-prioritised: model per role → P3, agent memory → P3, budgets only in the API-key example. Two-key row: spawns warn only. New §17 (19 rows: command model, comms, diplomacy) and §18 (8 rows: spawning, OD1) |
| `02-architecture.md` | Python package without the orchestrator; host internals gain Seats, Delegations, Missions, ReportEngine, Stance and Referee, `Comms/` and the spawner; think-time line; failure rows for commander disconnect and delegate idle; budget rows for the C2 additions and spawning |
| `03-protocol.md` | Hello example as `a:blue-cmdr`; roles; `c2.seat.*`, `c2.delegate.*`, `c2.reports.config`, `c2.stance.*`, `c2.surrender*`, `c2.violations`; the `comms.*` family; events `seat.*`, `delegate.*`, `comms.*`, `stance.*`, `violation.*`; error reasons `out_of_scope`, `not_seat`, `seat_held`, `channel_not_open`, `no_link`, `surrendered`; MCP `--actor-token` and the `as` argument; toolset presets `commander`, `delegate`, `builder`; `comms_wait`; examples use the seat as owner |
| `04`, `05`, `08`, `10`, `12`, `14`, `20` | Small alignments: delegate clamps; spawn rows pointing to `06` §9.1; `seat:Blue` and the `C`/`D:` control tokens in the picture; SDK packages, loops and the scenario schema (spawn specs, respawn, seats, stance, comms, scoring, new end conditions); recording of comms and notes, AAR by the user's own client; `FakeCommander` in CI; name de-duplication for every spawn path |
| `06-flight-control.md` | New §9.1 **Spawn anywhere**: modes, the uncapped multi-spawn with clear-space search and rows, formations, staggering, waves and respawn, the reuse-vs-own table, surfaces, permissions (warning, never two-key); acceptance items 11–15 |
| `07-bdarmory-integration.md` | New §5.1: Overdrive feature **OD1**, multi-spawn in the fork's VesselMover spawn window (P1, M1), listed next to the F-patches |
| `09-c2-multi-agent.md` | Rewritten. §0 summary of the decision; §1.1 command seat (with free-play seating); §1.2 delegation; §1.3 automatic reports; new precedence bands; §4.1 controllers `commander`/`delegate`; doctrine without models or budgets; §8 AI clients (any MCP client, event delivery, guidance, think-time, API-key example, human in the loop, deferred agent layers); acceptance renumbered 1–9; §11 strengthened (below) |
| `11-tactical-map.md` | New §5.2 comms panel and command strip; actors panel replaces the agents panel; control badges `C`/`D`/`S`; spawn and message tools; §12 table rows updated and new specs §12.1–12.9; acceptance items 13–15 |
| `15-security-permissions.md` | ROLE rules for commander, advisor, delegate, observer, script, behaviour, harness, human; tokens; comms injection handling; spawns removed from two-key; whole-side surrender is `destructive` |
| `16-scenarios.md` | §0 gains "spawning on day one"; §2–6 and §8 rewritten for the command model; new §10 agent-vs-agent strait standoff; the latency table (now §11) drops the Haiku row and adds reports, diplomatic messages and spawning |
| `17-milestones.md` | Re-scoped (§9.6 below); new "Later (P3)" section; acceptance references renumbered |
| `18-risks-and-decisions.md` | R7, R8, R9 and R15 updated; R30–R33 new; U2 resolved, U5 new; D-6 and D-23 changed; D-25 to D-33 new; five rejected alternatives; spikes for M1, M4 and M6 |
| `21-user-experience.md` | Wizard offers the MCP command per client; Connections lists delegation tokens; new Spawn, Command and Comms windows replace the Agents window; API-key path; cookbook and prompt templates for commander and delegate |
| `22-communications-diplomacy.md` | **New.** Channels, message model, authenticity and deception, stance mapped to BDA, surrender, violations, optional realism, visibility and recording, interfaces, permissions and prompt-injection handling, surfaces, acceptance |
| `IDEAS.md` | §4 gains the spawn-anywhere and multi-spawn bullet (with the uncapped count); short design notes on the §7 and §16 bullets that §15 supersedes |

### 9.2 Traceability for the new requirements

| ID | Requirement (short) | Where | Pri | M |
|---|---|---|---|---|
| 15.1 | Any MCP client (Claude Code, Codex, others, local model); API key and scripts first-class; spending caps optional, API-key mode only | `09` §8.1, §8.5; `03` §11; `21` §1, §4; `18` U2 | P0 (client), P1 (example) | M0, M6 |
| 15.2 | The AI tool is the chat; the map has only a comms panel | `11` §5.2; `22` §11; `18` D-33 | P1 | M4 |
| 15.3 | Commander is a human or one agent; either can take over | `09` §1.1; `11` §5.2; `21` §3; `18` U5 | P0 | M4 |
| 15.4 | BDA AI flies and fights each craft | `09` §0, §11; `00` D10 | P0 | — |
| 15.5 | Units and groups report automatically without an LLM | `09` §1.3; `22` §1–2 | P0 | M4 |
| 15.6 | The commander controls assets directly; gaps filled by CMO-style in-game logic | `09` §4.1, §11; `11` §6.1, §12.1–12.9 | P0/P1 | M4, M6 |
| 15.7 | Group and unit agents removed (later); no Haiku-per-unit default | `09` §8.7; `17` Later; `01` §7 | P3 | Later |
| 15.8 | Team-internal delegation: the client spawns a sub-agent with its own identity and a cluster lease, reporting on the team channel | `09` §1.2; `03` §11; `15` §2; `18` R30, D-27 | P0 | M4 |
| 15.9 | Plain guard mode still works with nothing connected | `09` §1.1; `00` principle | P0 | M0 |
| 16.1 | Channels team, diplomatic (both open), broadcast, direct; every message logged with sender, recipient, channel, time | `22` §1–2, §8 | P0 (team, direct), P1 (broadcast), P2 (diplomatic) | M4, M6, M7 |
| 16.2 | Agents in a team talk to each other | `22` §2; `09` §1.2–1.3 | P0 | M4 |
| 16.3 | Surrender demands, ceasefire, threats, bluffs | `22` §4–5; `16` §10 | P1 (stance), P2 (negotiation) | M6, M7 |
| 16.4 | Psyops and deception: unverified messages, receivers judge | `22` §3, §10; `18` R31, D-29 | P2 | M7 |
| 16.5 | "Subdue or talk it out" as a win | `22` §5; `10` §7 | P1 | M6 |
| 16.6 | Stance war/ceasefire/peace/surrender mapped to BDA allies and weapons hold; surrender safes, marks and scores; firing on surrendered is a violation | `22` §4–6; `18` D-26 | P1 | M6 |
| 16.7 | Optional comms range and jamming, off by default | `22` §7 | P2 | M8 |
| 16.8 | Visibility own side plus diplomatic, or god view; agent-vs-agent scenario | `22` §8; `16` §10 | P1, P2 | M4, M7 |
| S.1 | Spawn anywhere, first-class | `06` §9.1; `01` §18 | P0 | M1 |
| S.2 | Positions: mid-air, sea surface, submerged, ground/runway; orbit later | `06` §9.1 modes | P0 (orbit P2) | M1 (orbit M8) |
| S.3 | Formation or swarm in one call, staggered, with team, guard, AI, group, mission | `06` §9.1 `spawn_formation` | P0 | M1 |
| S.4 | Sources: map drag with altitude and speed, scripts, agents | `11` §6, §12.8; `06` §9.1 | P0 | M1, M3 |
| S.5 | Mid-battle reinforcements: waves, BDA continuous spawning, respawn rules | `06` §9.1; `09` §9; `10` §7 | P1 | M6 |
| S.6 | `cheat` level, allowed in Battle Lab | `15` §1; `06` §9.1 | P0 | M1 |
| S.7 | Note reuse of BDA's VesselMover/VesselSpawner vs Skizzy's own placement | `06` §9.1 table; `18` D-31 | — | — |
| S.8 | Multi-spawn: N in a line, clear-space search, ground/sea/air, options, staggered, from map, scripts, agents, in-game | `06` §9.1; `21` §3 | P0 | M1 |
| S.9 | Multi-spawn as a BDArmory Overdrive fork feature | `07` §5.1 (OD1); `01` §18 | P1 | M1 |
| S.10 | No cap on count; warning above about 50 with an estimate; rows when space runs out | `06` §9.1; `15` §3; `18` D-32 | P0 | M1 |

All 27 rows are covered. None is partial.

### 9.3 Scenarios re-checked under the new model

| Scenario | Still works? | How now |
|---|---|---|
| §0 first contact | yes | The agent takes the vacant seat in free play (precedence 800); spawning added on day one |
| §1 destroyer design | yes | Any client with the `builder` toolset; the cost is on the user's client |
| §2 SAG vs 40 missiles | yes | The `ad_umbrella` tactic, the WTA solver, BDA guard and the `leaker` trigger do the AD coordinator's and unit agents' work; reports reach the commander; an ASW delegate is optional |
| §3 20-drone swarm | yes | Spawned mid-air in one call; run by a script with a delegation token, or by a client-spawned sub-agent with a cluster lease |
| §4 co-fly with wingmen | yes | Formation executor plus the human's direct orders; automatic reports; optional delegate |
| §5 carrier strike | yes (P2) | The strike mission planner (phases, time on target, re-route, abort) replaces the strike-lead agent |
| §6 submarine ambush | yes | `sub_shoot_and_scoot` behaviour tree with reports; the commander or a delegate can take targeting |
| §7 200-vessel batch | unchanged | Never used agents |
| §8 map route | yes | The mission planner re-slots the screen; the commander agent is told |
| §9 logging | unchanged | — |
| §10 agent vs agent | new | Claude Code vs Codex in a strait standoff, three endings (negotiated, surrender, fight) |

### 9.4 CMO-style features: completeness check

These features now carry the work the agents used to do, so each was checked for a usable specification on both the C2 side and the map side.

| Feature | C2 side | Map side | Before pass 2 | Pass 2 change |
|---|---|---|---|---|
| Missions | `09` §11.2: JSON, lifecycle, assignment, type table (patrol subtypes, strike, escort; support and ferry P2), planners, API, events | `11` §12.7 | C2 specified; map one row | Planners for CAP rotation, strike timing and threat-aware re-route, escort; Missions panel spec; acceptance `09` §11.8 items 6–8 |
| Doctrine inheritance | `09` §11.1: resolution, locks, API, key table, cached evaluation | `11` §12.9 | specified without locks or UI | Locks with `locked_by`, `c2.doctrine.effective` sources, the editor tree |
| EMCON | `09` §11.3: presets, triggers, executor, effects | badges, layer, `11` §12.9 | executor sketched | Presets table, `goActiveWhen` and `revertAfterS`, radar re-apply with the M4 spike, editor |
| Range rings | layers from `bda.dlz` and radar data | `11` §12.1 | one line in §3 | Per unit and per side, threat rings, altitude slider, cost rule |
| Message log | `09` §11.6: one stream of events, reports and comms | `11` §12.2 | one row | Filters, saved views, jump, export, retention |
| Magazine view | `bda.ammo`, SITREP | `11` §12.3 | one row | Full unit details spec with doctrine sources and multi-selection table |
| Losses report | `09` §11.6: `c2.losses` with a compact format | `11` §12.4 | one row | Subdued and surrendered, fog rule, drill-down, scorecard |
| Contact classification | `09` §11.4: thresholds, confidence growth, manual marks (`tac.track.mark`) | `11` §12.6 | specified without manual marks | Manual marks, track card |
| Time compression and pause-on-event | `09` §11.6: scale limits, `sim.time.drop_on`, defaults per seat kind | `11` §12.5 | one row | Defaults table for human and agent seats, think-time banner with veto, multi-human rule |
| Scenario editor | scenario schema (`10` §7) | `11` §12.8 | one row | Forces as spawn specs (any count, rows, mid-air), sides and seats, stance, comms, missions, waves, respawn, end conditions, validate and run |

### 9.5 Verified claims (BDArmory fork at `db8db1078`)

| Claim | Evidence | Result |
|---|---|---|
| Team hostility is one-directional | `BDTeam.IsEnemy` (`Competition/BDTeam.cs:35-40`): false if either is `Neutral`, names match, or *this* team's `Allies` has the other | ceasefire sets both teams' `Allies` (D-26) |
| One shared team object | `BDTeam.Get` and `Deserialize` return the instance from `BDArmorySetup.Instance.Teams` | stance changes reach every weapon manager at once |
| Allies do not share radar | `VesselRadarData.cs:687, 717` compare team identity | a ceasefire hands over no picture |
| IFF missiles skip friendly teams | `MissileBase.cs:1112, 1266, 1432`; `MissileLauncher.cs:2867`; `MultiMissileLauncher.cs:1465` | missiles in flight drop new allies |
| Proximity fuse treats allies as friendly | `MissileFire.cs:11328` | no proximity kills across a ceasefire |
| `isArmed` only gates the manual fire key | `MissileFire.isArmed` `:1246`, used at `:1814` | surrender also needs guard off and a referee lock |
| Team change clears targeting | `MissileFire.SetTeam` `:1175` clears the target, removes `TargetInfo`, raises `OnChangeTeam` | used to move surrendered units |
| Craft loading from a file | `VesselSpawner.SpawnVesselFromCraftFile(craftURL, gpsCoords, heading, pitch, roll, out EditorFacility, crew)` (`VesselSpawning/VesselSpawner.cs:70`, public static) | reused by Skizzy's spawner |
| BDA airborne spawn | `SingleVesselSpawning.SpawnVessel` (`:57`) treats more than 10 m AGL as airborne; `SpawnUtils.AirborneActivation` (`:244`) fires AG10, activates the pilot, commands take-off and turns guard off; idle speed only when `VESSEL_SPAWN_INITIAL_VELOCITY` (default false, `BDArmorySettings.cs:368`) | Skizzy sets pose and velocity itself and keeps guard on (D-31) |
| Engine helpers | `SpawnUtils.ActivateAllEngines` (`:131`), `CountActiveEngines` (`:126`), public static | reused |
| VesselMover | `PlaceVessel` (`VesselMover.cs:445`) lowers from the current position; its spawn path `SpawnVessel` (`:614`) is where OD1 goes | wrapped, not copied |
| Continuous spawning | `ContinuousSpawning.SpawnVesselsContinuously` (`:93`) | wrapped as `bda.spawn_continuous` |

### 9.6 Not verified (spikes)

- **M0:** Codex CLI against the same `skizzy mcp` tool list.
- **M1:**
  - spawn settling at sea and submerged with KJR;
  - initial airborne velocity with guard kept on;
  - calibrating the large-spawn performance estimate.
- **M4:**
  - whether Claude Code and Codex subagents can open their own connection or share the parent's (R30);
  - whether BDA guard mode re-activates radars that EMCON switched off.
- **M6:** how BDA's competition counts allied and neutral teams when it decides a battle is over (R32).

### 9.7 New milestone list

| M | Theme | Est. |
|---|---|---|
| M0 | First contact (any MCP client; Claude Code and Codex checked) | 3 wk |
| M1 | Fly, place and **spawn** (spawn anywhere, uncapped multi-spawn with rows and clear-space, formations, in-game Spawn window, OD1) | 4 wk |
| M2 | BDA core, compact picture, recorder v1 | 5 wk |
| M3 | Tactical map v1 (with map drag-spawn) | 5 wk |
| M4 | **Command basics**: seat, delegation, automatic reports, team and direct channels, comms panel v1, leases, orders, take-over, EMCON, basic doctrine, SDK | 6 wk |
| M5 | Craft building v1 and analytics | 5 wk |
| M6 | **Missions, doctrine and stance**: mission planners, full doctrine, WTA, broadcast, stance, surrender, violations, comms panel v2, waves and respawn, API-key example, think-time, scenario editor | 6 wk |
| M7 | **Batch, CI and diplomacy**: diplomatic channel, negotiation, psyops, agent vs agent | 5 wk |
| M8 | Naval depth and extensibility (comms range and jamming, orbit spawn) | 6 wk |
| M9 | Hardening and release | 4 wk |
| Later | P3: built-in agent layers, per-role models and budgets, agent memory, analyst, automatic surrender | — |

Total ≈ 49 weeks, up from 45.

### 9.8 Decisions

- **Resolved by the user.** U2: spending caps are optional, in API-key mode only.
- **New for the user.** U5: may an agent take a seat a human holds? The default is `ask` (a prompt, declined after 30 s).
- **Decided in this pass.**
  - D-25: comms in its own doc.
  - D-26: stance mapping.
  - D-27: delegation identity.
  - D-28: report defaults.
  - D-29: free broadcast callsigns.
  - D-30: no god view for agent commanders by default.
  - D-31: spawn reuse vs own.
  - D-32: uncapped multi-spawn.
  - D-33: comms panel, not chat.
- **Changed.** D-6: no orchestrator. D-23: the client picks the model.
