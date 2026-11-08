# skizzy-protocol: master idea list

This is the combined list of every MCP and server idea so far (2026-11-08). It's a **starting point, not a limit**: the designer is expected to add, merge and reprioritise. Where this file conflicts with PLAN.md, this file wins.

**Goal:** a command-and-control and automation platform for KSP 1.12.5 + BDArmory that humans, scripts and AI agents can all use, for everything from designing a ship to commanding a 100+ vessel battle.

---

## 1. Core server
- An in-game C# host plugin, a C# MCP bridge (a single exe) and a shared protocol library.
- A fast native socket for scripts and high-rate agents. MCP for LLM agents. Both use the same API and the same permissions.
- A main-thread dispatcher with a per-frame time budget, and zero work and zero allocations when nothing is connected.
- Each tool fails closed on its own. No exceptions leak into KSP. Scene changes are handled gracefully.
- **Every feature is reachable from scripts (Python/C#), LLM agents, the tactical map and in-game controls.** Nothing exists in only one front end.

## 2. Access to KSP and every mod
- **Tier 0, automatic:** every PartModule/VesselModule field, event and action of every installed mod, plus action groups, resources, staging, the part tree, crew and post-ModuleManager configs.
- **Tier 1, annotations:** config files that add descriptions, units and safety levels, hidden C# properties (e.g. HullBreach flood tonnes), all BDArmory settings, and named methods exposed as tools.
- **Tier 2/3, opt-in protocol:** mods register typed tools, events and streams with no hard dependency.
- **Deep reflection:** private fields, statics and singletons. Off unless the user enables it per mod.
- Bulk queries across all vessels, parts and modules in one call.
- Mod adapters: PhysicsRangeExtender, VesselMover, HullBreach, SubTrim, Kerbal Engineer, Trajectories, AtmosphereAutopilot, Hangar, Aircraft Carrier Accessories, WildBlueTools, KP Naval, HyperEdit and others.

## 3. Vessel creation and craft building (optimised)
- **Editor control (VAB/SPH):** new or load craft; add, remove, move and rotate parts; attach to a node or surface; symmetry; staging; configure any module; save to Ships or AutoSpawn.
- **Blueprints / parametric DSL:** e.g. "Large Boat Parts destroyer hull, 4× 8-cell VLS with ESSM, CIWS fore and aft, SPY-type radar, Surface AI Water". The system assembles the craft.
- **Headless `.craft` generation and editing**, without the editor.
- **Validation:** mass, CoM, CoL, CoT, buoyancy and waterline, stability, TWR, weapon manager and AI present, part count, clipping.
- **Iteration loop:** build, spawn a test battle, measure, adjust, rebuild.
- A shared library of craft, templates and loadouts.

## 4. Flight and vessel control
- Throttle, SAS modes, RCS, staging, gear, brakes, lights, action groups, and switching the active vessel.
- **In-game setpoint autopilots at physics rate:** heading, pitch, roll, altitude, speed and depth hold. Agents and scripts only set targets.
- Waypoints, orbits, loiter, formation keeping.
- Hand-off to and from the BDArmory AI and AtmosphereAutopilot.
- Human and agent co-flying: the human flies one jet while agents fly the wingmen.
- A kill switch that always restores manual control.
- Scene setup and positioning in the style of VesselMover and HyperEdit.

## 5. BDArmory
- **Spawning:** fleets in a ring or in exact formations (naval groups).
- **Teams and weapon manager:** guard mode and the full weapon-manager configuration, including 1024 targets, 128 missiles per target and parallel salvo.
- **AI control:**
  - AI modes: pilot, surface, submarine, VTOL, orbital.
  - Live per-unit AI parameter changes.
  - Commands: attack, fly-to, follow, waypoints.
- **Direct weapons:** fire a missile at a target, guns, countermeasures (including sub bubbles), and radar on/off/lock.
- **Competitions and results:** competitions and tournaments, scores, damage reports, missiles in flight, all BDArmory settings, log tailing.
- **Small upstream hooks:** steering override, target-assignment override, formation following, and AI parameters exposed at runtime.
- Study how BDArmory's AI works, and allow **modified BDArmory AI behaviours** to run per unit.

## 6. Tactical picture: radar, IFF, data sharing
- **A track table per team**, fused from radar, IRST, sonar, visual, datalink and RWR, with IFF (friend/foe/unknown), track quality and age.
- **Fog-of-war** (only what your side detects) or **god view**, configurable.
- **Ask for exactly what you need:** filter by area, role, team or threat; use relative frames (BRAA from my unit, or formation-centric); top-N threats.
- **Subscriptions deliver deltas and events only** (new or lost track, missile launch, threat warning, order status). Never a token dump.
- **Compact encodings:** token-efficient text for LLMs, binary or JSON for scripts.
- **Near-real-time for dogfights:** fast loops run in scripts or in-game behaviours, and never wait on an LLM.

## 7. Command and control: orders, hierarchy, multi-agent
- **Orders as objects:** intent, constraints, rules of engagement, priority, owner, status, timeout.
- **Hierarchy:** commander → groups → units. Roles include swarm, air defence, flight lead, surface group, submarines, carrier ops and strike.
- **Unit ownership (leases),** so two agents or scripts never fight over one craft.
- **Precedence:** human > commander agent > unit agent > script > BDArmory default.
- **Team tactics library:** CAP, intercept, pincer, AD umbrella, ASW screen, strike package, swarm saturation, sub ambush, carrier launch and recovery.
- **Model per role:** a fast model (e.g. claude-haiku-5-5) for tactical units, a strong model for command. Events are pushed to agents, never polled.
- **Time control for thinking:** automatic slow-motion or pause while agents decide.
- **LLMs are one option, not the only one:** scripts, behaviour trees, the BDArmory AI and humans are all first-class actors.

## 8. Scripting and automation
- **SDKs, Python first and C# second:** typed models, async event streams, and helpers for formations, BRAA queries and order lifecycles.
- **Script templates and a plug-in model** for new behaviours and doctrines, e.g. a Python script flying a 20-drone swarm.
- **Modified BDArmory AI:** per-unit parameter and behaviour overrides, pluggable C# behaviours, and scripts layered on top of the BDArmory AI.
- **Declarative scenario and doctrine files** that scripts and agents can load, edit and share.

## 9. Tactical map
- **Live map** with military symbols (APP-6 / MIL-STD-2525 style) per type: fighter, bomber, helicopter, drone, missile, destroyer, carrier, submarine, ground unit.
- **Layers:** tracks, track history, sensor and weapon ranges, engagement lines, orders and their status, threat axes.
- **Planning for the human and agents:** draw routes, areas and formations; assign targets; set rules of engagement. Agent orders appear live.
- Fog-of-war or god view.
- An external desktop or web app, an in-game overlay, or both, fed by the same data stream.

## 10. Logging, recording, replay ("log whatever I want, get it, save it")
- **Log anything:** pick any field, module, vessel, track, order, event or BDArmory stat by query, at any rate (on change or at N Hz).
- **Saved to disk** in open formats (CSV / JSONL / Parquet), with named log profiles that can be switched on per battle.
- **Retrieve any time:** query saved logs by time, vessel, team or event, from scripts, agents or the map.
- **Full battle recording:** tracks, orders, events and damage. Replay it in the tactical map for after-action review, and use it as agent context or training data.
- **Metrics and A/B reports:** missiles per target, hit rates, time to kill, survival, flood/sink times, frame time and memory over time.
- **Session audit log** of every action by any agent or script.

## 11. Vision
- **Screenshots** returned to agents as images, with configurable resolution and rate.
- **Camera modes:** follow, orbit, chase, free, look-at-target.
- Used on demand. The data streams carry the real-time load.

## 12. Testing and batch simulation
- A headless/batch mode that runs many battles from scenario files.
- Repeat runs with statistics, since physics isn't deterministic.
- A/B comparisons, e.g. salvo on vs off, or HullBreach tuning.
- Validation of the user's mods: multi-target, salvo, depth charges, SubTrim, MNWS VLS, Iron Seas guns.

## 13. Permissions and safety
- **Levels:** read, control, destructive, cheat, unsafe, each set to allow, ask or deny per mod and per tool.
- **Defaults:** editor, flight and screenshots are **allowed** for the local user. Destructive actions default to ask, and the user can change that.
- **Safeguards:** a kill switch, rules-of-engagement guards, friendly-fire checks, an audit log, localhost only, and a token.

## 14. Extensibility and extras
- Other mods can add unit types, sensors, weapons and symbology.
- Later: logistics and rearm, damage control, comms degradation and EW, a web dashboard, and an AI battle commentator.

---

## Constraints and preferences
- C# on the KSP side and for the bridge. Python and others are fine for external clients.
- Performance first: zero-alloc hot paths and frame budgets at 100–1000 vessels, because the user just fixed memory leaks.
- Don't break the user's game. Stay compatible with the installed mod set (see `D:\Epic Games\KerbalSpaceProgram\English\GameData`).
- Related repos:
  - BDArmory fork: `D:\KerbalSpaceProgram\BDArmory` (branch multitarget-1.13.1)
  - mod pack: `D:\KerbalSpaceProgram\skizzy-naval`
  - SubTrim: `D:\Epic Games\KerbalSpaceProgram\English\_downloads\patched\SubTrim`
