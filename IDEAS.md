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
- **Mission command plus direct control (added 2026-11-08):** both, never one instead of the other.
  - Command at mission level: assign units to patrol, strike or escort and let doctrine plus the BDArmory AI run them.
  - At any moment, drop to direct micromanagement of any unit or group: steer, pick a target, fire this missile, hold fire, or fly it yourself.
  - Hand it back to the mission afterwards.
  - Each unit and group shows whether it is mission-controlled or manual, with one-click take-over and release (the human lease preempts).

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
- **Command-screen layout (added 2026-11-08):**
  - The whole window is freely resizable.
  - Panels are dockable, movable and detachable: track list, unit details, orders, timeline, log, layers.
  - The layout is saved and restored per user.
  - Smooth zoom (wheel/pinch) and pan, plus fit-to-selection and fit-all.
- **Clarity at scale is the core problem.** Example: 40 jets and drones with 128 missiles flying over them at once must stay readable. (Corrected by the user 2026-11-08: individual tracks always.)
  - **Every track is shown individually by default:** every missile and aircraft, where each one goes, its altitude, when a flight splits to evade, intercepts and collisions as they happen. Nothing is aggregated by default.
  - Aggregated counts ("MSL ×128", "UAV ×20") are only an optional alternative view or toggle, for when the user wants numbers.
  - **Dynamic, zoom-aware sizing that avoids overlap:** symbols shrink when zooming out and grow when zooming in, but never so far that they overlap; when zoomed in, things don't sit on top of each other unless they really are that close. Sizing comes from local density/spacing on screen, not just a fixed zoom factor.
  - At high density or far zoom, degrade gracefully: full symbol → small symbol → small dot. Never hide or merge.
  - **Visual hierarchy by radar cross-section** (that's how things are tracked): larger RCS gets a larger or more prominent symbol; small-RCS items (missiles, drones) are small but always visible. The user can override size per item, type or selection.
  - Per-track readability: compact altitude/depth (label or colour/tick); short motion trails or velocity leaders so splits and manoeuvres are readable; optional missile → target lines; intercept, impact and kill events marked briefly on the map; label decluttering that never hides the symbol itself.
  - Filters: show or hide by type, team or threat. Highlight on hover and selection.
  - **Replay and slow-motion scrubbing** so dense engagements can be watched move by move.
  - Performance target: thousands of symbols at 60 fps.
- **Memos:** pin notes and labels to tracks or map points.
- **Genre reference (added 2026-11-08):** the command screen should offer what a serious naval/air C2 wargame screen offers, in the spirit of *Command: Modern Operations* (features only; the UI stays our own): NTDS/2525 symbology options, posture colours, track uncertainty areas, sensor/weapon coverage and emission state, EMCON, doctrine/ROE with inheritance, missions as first-class objects, reference points and zones, a filtered message log, losses and expenditures, time compression and pause-on-event, magazine views, the unknown → classified → identified flow, and a side/scenario editor. This is *in addition to* Skizzy's own strengths, which stay up front:
  - real KSP physics and the BDArmory AI;
  - LLM and script actors;
  - flying a unit yourself;
  - craft building;
  - full logging and replay.

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

## 15. Command model: who runs what (user decision, 2026-11-08)
- **Any AI client, not only Claude Code.** MCP is the main way in, and any MCP client works: Claude Code, Codex, others, or a local model. A direct API key and scripts remain options. LLM spending caps are an optional guard for API-key mode and are not used with Claude Code.
- **The AI tool is the chat.** The map doesn't need its own LLM chat client, only a comms panel that passes messages to and from whatever agents are connected.
- **Default: no agent on each craft.**
  - **Who commands:** the commander is either a human on the command screen or one agent, and either can take over from the other at any time. They plan, move flights, and send a swarm at the enemy's high-value assets.
  - **Who fights:** BDArmory AI flies and fights each craft.
  - **Automatic reports:** each unit or group reports back without any LLM: engaged, contacts, weapons spent, damage, kills, out of ammo, returning to base, lost.
- **The commander controls all assets directly and talks to the other side's commander.** BDArmory AI is good enough for the fighting. Where it falls short, we add CMO-style features (missions, doctrine, ROE, EMCON, routes) as in-game logic, not more agents.
- **Group agents are removed from the plan for now** and become a later item. There are no built-in group or unit agent layers and no Haiku-per-unit default.
- **Team-internal "talk" for now:** the commander's own AI tool deploys a sub-agent to handle one cluster, e.g. Claude Code or Codex spawning a subagent. That sub-agent connects through the same MCP and is given a lease on just that cluster, reporting to the commander. The protocol only needs:
  - actor identities
  - leases scoped to a cluster
  - a team channel

  It doesn't need its own agent runtime or orchestrator.
- **Plain guard mode still works with nothing connected.**

## 16. Communications, diplomacy and influence (user idea, 2026-11-08)
- **Channels:**
  - team (always on)
  - cross-team/diplomatic (both sides must open it)
  - broadcast/open frequency
  - direct (unit to unit, commander to commander)

  Every message records sender, recipient, channel and time, and is logged.
- **Agents in a team talk to each other:** commander to group to unit-reporting, plus peer coordination.
- **Talking to the enemy:**
  - demand surrender, negotiate a ceasefire, make threats, bluff
  - **psychological operations and deception:** messages are not verified, so lies are possible, and receivers decide whether to believe them
  - "subdue or talk it out" as a valid way to win a battle
- **Side stance:** war / ceasefire / peace / surrender, mapped onto BDArmory team allies and weapons hold. Surrender makes units safe their weapons, marks them surrendered and scores them. Firing on a surrendered unit counts as a violation.
- **Optional realism:** comms range and jamming (EW). Off by default.
- **Visibility:** the human sees their own side's traffic plus the diplomatic channel, or everything in god view. Use it for agent-vs-agent scenarios, e.g. two AI commanders negotiating mid-battle.

---

## Constraints and preferences
- C# on the KSP side and for the bridge. Python and others are fine for external clients.
- Performance first: zero-alloc hot paths and frame budgets at 100–1000 vessels, because the user just fixed memory leaks.
- **Optional layer, zero cost when unused (added 2026-11-08):** plain BDArmory guard mode works exactly as now with nothing connected (no LLM, no bridge, no map). Everything skizzy-protocol adds is an optional layer, and with nothing connected it costs nothing.
- **Parallelism, kept realistic (added 2026-11-08):** use multiple cores and the GPU where it's cheap and safe. External processes (bridge, map renderer, recording export, batch analysis) can be multi-threaded freely. In-game, KSP and Unity APIs are main-thread only, so at most pure math on snapshot copies goes to worker threads, and only where measurement shows it helps. Low priority; must not complicate the first milestones.
- Don't break the user's game. Stay compatible with the installed mod set (see `D:\Epic Games\KerbalSpaceProgram\English\GameData`).
- Related repos:
  - BDArmory fork: `D:\KerbalSpaceProgram\BDArmory` (branch multitarget-1.13.1)
  - mod pack: `D:\KerbalSpaceProgram\skizzy-naval`
  - SubTrim: `D:\Epic Games\KerbalSpaceProgram\English\_downloads\patched\SubTrim`
