# 09 Command and control: orders, leases, hierarchy, multi-agent

## 1. Model

```
Commander (human | claude-opus-5-5)                 intent, task organisation, ROE, zones, priorities
   └─ Groups (SAG-1, CAP-A, ASW screen, Strike 1)   group agents (claude-haiku-5-5 | scripts), tactics
        └─ Units (DDG-1, F-22-3, SSN-1)              unit agents (Haiku) | BTs | scripts | BDA AI
             └─ Channels: steer, weapons, targeting, sensors, ai_params, formation, systems
```

Everything an actor wants a unit to do is an **order**; everything that lets an actor touch a channel is a **lease**. Orders are executed either by in-game **executors** (deterministic code translating the order into BDA commands, holds and WM settings) or **claimed** by an actor that executes them itself (a script or agent). The C2 engine runs in the host so that orders outlive the actors that issued them.

## 2. Orders

### 2.1 Taxonomy (v1)

| Family | Types | Default executor |
|---|---|---|
| Movement | `move_to`, `move_route`, `patrol_area`, `loiter {center, radius, alt}`, `follow {leader, slot}`, `formation {template}`, `hold_position`, `return_to {point|carrier}`, `set_depth`, `set_speed_limit`, `set_alt_band` | in-game (BDA commands + holds, `06-flight-control.md`) |
| Engagement | `engage {track}`, `engage_all {filter}`, `defend_sector {sector, priorities}`, `defend_unit {unit, radius}`, `intercept {track}`, `screen {main_body, bearings}`, `escort {unit}`, `suppress {zone}`, `strike {target, package}`, `attack_position {pos}` | in-game (assignments, fire authority, BDA `CommandAttack`) |
| Fire control | `weapons_hold`, `weapons_tight`, `weapons_free`, `set_roe {profile}`, `missile_budget {per_track, total}`, `salvo_policy {size, stagger}` | in-game (WM fields, F3 hook) |
| Sensors | `emcon {level}`, `radar {on|off}`, `sonar {passive|active|off}`, `irst`, `datalink {on|off}` (comms model) | in-game |
| Systems | `launch {aircraft}`, `recover {aircraft}`, `refuel`, `rearm`, `damage_control {priority}`, `action_group {n}` | in-game adapters (P2) |
| Tactic | `tactic {name, params}` from the library (§6) | planner expands into child orders |
| Meta | `report {what, interval}`, `claim {order}` (an actor takes execution), `stand_down` | — |

Every order type has a JSON Schema, preconditions (`requires: ["lease:steer", "ammo:missile>0"]`), an **achievement test** (`move_to`: within radius; `engage`: track dead or `maxMissilesPerTrack` spent or lost; `defend_sector`: open-ended until superseded), progress fields, and failure reasons.

### 2.2 Lifecycle

```
c2.order.issue ──► validate (schema, doctrine, ROE, zones, basedOnSeq) ──► acquire leases (channels per type)
   ├─ LEASE_HELD → status failed(lease_denied) unless preempt allowed
   └─ accepted ──► executor.bind ──► executing ──► progress events ──► achieved | failed | superseded | expired | cancelled
c2.order.amend {params}      keeps id, re-binds
c2.order.cancel              releases leases held for this order
c2.order.claim {actor}       marks "executed externally"; the host still tracks TTL and achievement
c2.order.report {status, note} for claimed orders (scripts/agents report progress)
```

Orders to a group fan out to child orders per unit (`children[]`), keeping the parent's priority and constraints. An order **supersedes** earlier orders of the same family on the same channel for that unit unless `stack:true`. Suspension on human takeover (`human.took_controls`) and scene change; resumption rules in doctrine.

### 2.3 Constraints and ROE

Order `constraints` are checked at issue and continuously by the executor: `roe` (hold/tight/free), `zones.noFire`, `zones.stayIn`, `altBand`, `speedMax`, `depthBand`, `maxMissilesPerTrack`, `weaponAllow/Deny`, `requireIff` (never engage `unknown`), `pid` (positive id required), `minRangeToFriendlyM` (line-of-fire check through `BDATargetManager.CheckSafeToFireGuns` and a missile-path check). A violation stops the action and emits `ROE_VIOLATION`.

## 3. Groups and task organisation

`c2.group.create {id, name, members, role, leader?, parentGroup?}`, `add|remove|dissolve`, `set_leader`, `assign_actor {actor}`. Groups are also picture objects (centroid, course, speed, extent) usable as `ref` in frames and filters. Roles from doctrine (`sag`, `cap`, `asw_screen`, `strike`, `carrier_ops`, `swarm`, `ad`).

## 4. Leases and precedence

| Channel | What it gates |
|---|---|
| `steer` | holds, direct control, routes, formation keeping, BDA `Command*`, AI activation |
| `weapons` | fire authority, manual fire, weapon selection, salvo policy, countermeasures |
| `targeting` | target assignments/filters, override targets, secondary target lists |
| `sensors` | radar/sonar/IRST/RWR on/off, locks, emcon |
| `ai_params` | AI and WM parameter writes |
| `formation` | WingCommander slots and templates |
| `systems` | action groups, SubTrim depth mode, ballast, catapults, hangar |

Rules:
1. One holder per (vessel, channel). Acquire with `c2.lease.acquire {vessel|group, channels, ttlS, preempt:false}`; heartbeats renew (`session.heartbeat`); expiry releases with `lease.expired` and applies the channel's `onExpire` fallback (`bda_guard` for weapons/targeting, `bda_ai` or `hold_last`/`neutral` for steer, `keep` for sensors/params).
2. **Precedence**: an actor may preempt a lease held by a lower-precedence actor when `preempt:true` (the loser gets `lease.preempted {by, reason}` and its orders become `superseded`). Equal or higher precedence → `LEASE_HELD`. Bands: human 1000, commander 800, group 700, unit 600, script 400, behaviour 300, BDA 0; doctrine may override per actor id.
3. **Humans**: the KSP pilot's implicit `steer` lease on the active vessel preempts everything and is released after idle; the map operator holds explicit leases like any actor but at 1000; `c2.lease.grant` lets a human delegate a channel at their precedence to an agent ("you have the ship").
4. **Delegation**: a commander can `c2.lease.delegate {to, vessels, channels, maxPrecedence}` so a unit agent acts with 700 on its units without the commander holding the leases itself.
5. **ACL**: doctrine lists which roles may hold which channels (unit agents never get `cheat` capabilities; scripts default to `steer`+`formation` only).
6. Leases are the arbitration layer for *authority*; BDA remains the default behaviour on every channel that has no lease, so a quiet system is just BDA.

## 5. Doctrine files (P1)

```yaml
doctrine: blue-naval-default
roles:
  commander: { precedence: 800, model: claude-opus-5-5, tools: [core, tactical, c2, bda, rec, sim, map] }
  group:     { precedence: 700, model: claude-haiku-5-5, tools: [core, tactical, c2, flight] }
  unit:      { precedence: 600, model: claude-haiku-5-5, tools: [core, tactical, c2, flight] }
  swarm-script: { precedence: 450, channels: [steer, formation] }
roe:
  default: tight
  profiles:
    tight: { engage: [hostile], requireIff: true, noFireZones: [harbour], maxMissilesPerTrack: 2 }
    free:  { engage: [hostile, unknown-closing], requireIff: false, maxMissilesPerTrack: 3 }
envelopes:
  ESSM: { minM: 1500, maxM: 50000, domains: [air, missile] }
  SM-2: { minM: 5000, maxM: 120000, domains: [air] }
  CIWS: { maxM: 2500, domains: [missile, air] }
threat_weights: { ASM: 0.9, torpedo: 0.9, fighter: 0.6, destroyer: 0.5, submarine: 0.7 }
formations:
  screen_3: { slots: [{right: 0, back: -4000}, {right: 3000, back: 1500}, {right: -3000, back: 1500}] }
fallbacks: { steer: bda_ai, weapons: bda_guard, targeting: bda_guard }
triggers:
  - name: raid
    when: { tracks: { filter: { iff: [hostile], domain: [missile, air], within: { ref: g:sag-1, m: 60000 } }, count: { gte: 4 } } }
    do:   { tactic: ad_umbrella, params: { group: g:sag-1 } }
  - name: sub_contact
    when: { tracks: { filter: { domain: [sub], within: { ref: g:sag-1, m: 30000 } }, count: { gte: 1 } } }
    do:   { notify: [commander], tactic: asw_prosecute }
budgets: { commander: { usdPerBattle: 3.0 }, unit: { usdPerBattle: 0.5, maxTurnsPerMinute: 6 } }
think_time: { slowmo: { enable: true, scale: 0.25, when: "decision_pending and tcpa_min < 20" } }
```

Doctrine is loaded with `c2.doctrine.load`, validated against a schema, versioned in the library, editable from the map (ROE and zones panel) and by agents (`c2.doctrine.patch` with a diff; `control` level; the commander role only).

## 6. Tactics library (P1)

A tactic is a parameterised plan that the C2 planner expands into orders and triggers, with an **executor assignment** per step:

| Tactic | Inputs | Expansion (sketch) | Executor |
|---|---|---|---|
| `ad_umbrella` | group, threat axis (auto from tracks), sector count | compute sectors around the group's centroid weighted by threat bearing; per ship `defend_sector`; `weapons_tight`; `missile_budget {per_track:2}`; CIWS auto | in-game + WTA solver |
| `cap` | station point, altitude block, flight, cycle time | `loiter` racetrack; trigger `intercept` on hostile air within range; return at bingo fuel | in-game |
| `intercept` | flight, track | `engage` with vectoring (`tac.intercept`), BDA `CommandAttack` on the predicted point | in-game |
| `pincer` | two groups, target group | two `move_route`s converging at ±60°; `weapons_hold` until both within range, then `weapons_free` | in-game + trigger |
| `asw_screen` | group, sub threat sector | escorts `screen` ahead; `sonar active` alternating; helicopters `patrol_area` (P2) | in-game |
| `asw_prosecute` | contact track, assets | `move_to` datum, active sonar, depth charges/torpedoes when ranged (NAS/BDA SLW) | in-game |
| `strike_package` | targets, strikers, escorts, routes | `move_route` ingress, `strike` with weapon allocation, `escort`, egress, `return_to carrier` | in-game, agent monitors |
| `saturation` | shooters, target, missiles per target | `salvo_policy`, synchronised `engage` with time-on-target offsets | in-game |
| `sub_ambush` | sub, patrol box, ambush line | passive sonar, `set_depth` deep, `weapons_hold`; trigger on bearing-only contact → `move_to` firing position, periscope depth, `engage` then `set_depth` deep + `move_route` evasion | BT + in-game |
| `carrier_launch`, `carrier_recover` | carrier, aircraft list | deck cycle through `naval.carrier.*` | in-game (P2) |

Tactics are YAML templates with Python/C# hooks allowed for custom logic (`10-scripting-sdk.md`).

## 7. Weapon–target assignment solver (`c2.wta.solve`, P1) — [ADDED]

Input: threats (tracks with `tcpa`, class), shooters (units with weapons, ready counts, DLZ per track from `bda.dlz`, lock capacity), constraints (max missiles per track, per-shooter salvo caps, sector ownership, hold-fire units). Output: assignment list `{shooter, track, weapon, count, launchAtUt}` maximising expected kills under the constraints with a greedy + local-search heuristic (exact MILP is unnecessary at this scale; 40 threats × 6 shooters solves in < 1 ms). The AD umbrella tactic calls it every 2 s and pushes results through `bda.assign_targets` (primary + secondary lists) and `missile_budget`. Agents can call it as a tool and override.

## 8. LLM agents (orchestrator `skizzy.agents`, Python)

### 8.1 Roles and models

| Role | Model | Effort | Cadence | Tool set |
|---|---|---|---|---|
| Commander | `claude-opus-5-5` | `high` (default is `medium`; set explicitly) | event-driven; digest every 30–60 s of game time; hard decisions may request slow-mo | core, tactical, c2, bda, rec, sim, map (~60 tools) |
| Group / unit | `claude-haiku-5-5` | `low`–`medium` | event-driven (`missile.inbound`, `order.*`, `track.new` in sector); max N turns/min from budget | core, tactical, c2, flight (~28 tools) |
| Builder (design loop) | `claude-opus-5-5` | `high` | interactive | core, craft, bda, rec |
| Analyst (AAR) | `claude-sonnet-5-5` or Opus | `medium` | post-battle | rec, tactical (replay) |

Prices (first-party API, 2026-10-06 table): Opus 5.5 $4/$20 per MTok, Haiku 5.5 $0.10/$0.50 (≤ 100 K prompt), Sonnet 5.5 $2/$10. Model ids are exact strings, no date suffixes. Adaptive thinking is on by default on these models; `thinking: {type:"adaptive"}` or omitted; effort via `output_config.effort`. Forced `tool_choice` (`any`/`tool`) is a 400 on Opus 5.5 and Sonnet 5.5, so agents use `auto` with `strict:true` tool schemas and prompt instructions. Verify bindings against the Anthropic SDK docs at build time; this design does not pin SDK versions.

### 8.2 Loop

```
orchestrator (one process, asyncio):
  native WS session per agent actor (events, leases, heartbeats)  +  MCP session per agent (its tools)
  for each agent: inbox ← events matching its subscription (digested, compact text, deduplicated)
  wake rule: inbox has a trigger-class event, or digest timer fired, or parent sent an order/message
  turn: system (stable, cached) + doctrine excerpt (stable) + [prior turns compacted] + "SITREP + inbox digest" user message
        → tool calls (tac_*, c2_*, flight_*) → final short "intent" text logged as c2.order.report note
  guard rails: per-turn tool-call cap, per-minute turn cap, dollar budget, decision deadline (if exceeded → slow-mo request or fallback)
```

- Events are pushed; agents never poll. The orchestrator uses the native socket for events and heartbeats; the agent's tools are MCP (stdio per agent or Streamable HTTP on the loopback bridge).
- **Prompt caching**: system prompt + tool list + doctrine excerpt are the stable prefix; the volatile SITREP/digest comes last; operator nudges mid-battle use mid-conversation `system` messages rather than editing the top-level system prompt; `usage.cache_read_input_tokens` is logged per agent and shown in the map's budget panel.
- **Context**: long battles use the API's compaction (beta) or message threads where available; otherwise the orchestrator keeps the last K turns and a running "battle memory" summary it writes itself.
- **Stale decisions**: every order the agent issues carries `basedOnSeq` (the `seq` of its last `tac_query`); the host rejects orders older than `maxStaleSeq` (doctrine, default 3 s at 5 Hz) with `STALE_DECISION`; the orchestrator re-queries automatically once.

### 8.3 Budgets and cost control — [ADDED]

Per actor: `usdPerBattle`, `tokensPerMinute`, `maxTurnsPerMinute`, `maxToolCallsPerTurn`. The orchestrator accounts `usage` per response and stops an agent at the cap (`BUDGET_EXHAUSTED`, agent frozen, units fall back to doctrine). Estimated cost of the SAG scenario (`16-scenarios.md` §2): commander 20 turns × ~8 k cached input + 1 k output ≈ $0.40–0.60; four Haiku unit agents 150 turns × ~3 k input + 300 output ≈ $0.10 total. A 50-run batch never involves LLMs (scripts and in-game executors only) unless the scenario asks.

### 8.4 Think-time control — [IDEAS]

When a trigger of class `urgent` fires (inbound missiles with `tcpa` under the agent's typical turn time) and the responsible agent has not answered, the orchestrator calls `sim.time.slow {scale:0.25, maxSeconds:20, reason}` (visible in-game as a banner; the human can veto with the kill switch or `sim.time.set 1`). Lockstep (`sim.hold/step`) is used in tests and when the user asks for turn-based play.

### 8.5 Memory between battles (P2) — [ADDED]

`<KSP>/Skizzy/memory/<role>/`: AAR summaries written by the analyst (what worked, losses, missile expenditure), per-craft performance rows, per-opponent notes, and "lessons" the commander appends. The orchestrator injects a role-filtered, size-capped excerpt into the stable prompt prefix. No vector store in v1; files are small and human-editable.

### 8.6 Human in the loop

The human is an actor at precedence 1000 through three surfaces: the KSP controls (implicit steer lease), the map (orders, ROE, zones, leases, budgets, kill switch), and Claude Code (talks to the commander agent or issues orders directly). The commander agent can **ask** the human (`c2.ask_human {question, options, timeoutS}` → map/in-game prompt) for decisions doctrine marks as `human_required` (e.g. `weapons_free` on `unknown` tracks).

## 9. Scenario director (P1) — [ADDED]

`sim.scenario.*` loads a scenario file (`10-scripting-sdk.md` §7) with spawn plans, groups, doctrine, initial orders, waves (`at ut`, `when trigger`) and end conditions; `sim.scenario.snapshot` writes the live state (positions, headings, speeds, teams, AI/WM params, orders, zones) back to a scenario file so a battle position can be restored by respawning (`sim.scenario.restore`), independent of KSP saves (which are unreliable mid-combat with missiles in flight).

## 10. Acceptance (summary)

1. Lease conflict: a script holding `steer` on DDG-2 is preempted by the commander agent (`lease.preempted`), and cannot preempt back (`LEASE_HELD`); the human stick takes over both and both resume after 5 s idle.
2. `defend_sector` orders to a 3-ship SAG assign 40 inbound tracks with ≤ 2 missiles per track across sectors; no CIWS fires on a track outside its ship's sector while another ship has it; `c2.wta.solve` runs < 1 ms.
3. Doctrine trigger `raid` expands `ad_umbrella` within one C2 tick of the fourth hostile track.
4. Haiku unit agent wakes on `missile.inbound`, answers within one turn (median ≤ 4 s wall) with `bda_fire_countermeasures` and a report; budget accounting shows cost per unit per battle.
5. Orchestrator crash: leases expire in 15 s, all units fall back to BDA guard, orders become `orphaned`, map shows it; restart re-attaches with `resume`.
