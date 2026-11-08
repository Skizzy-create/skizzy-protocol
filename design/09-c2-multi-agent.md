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

Orders to a group fan out to child orders per unit (`children[]`), keeping the parent's priority and constraints. An order **supersedes** earlier orders of the same family on the same channel for that unit unless `stack:true`. Suspension on human takeover (`human.took_controls`, `c2.control.take`, one-shot interventions) and scene change; resumption follows the suspension stack (§4 rule 8) and re-planning rules in §4.1.2.

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
1. One holder per (vessel, channel). Acquire with `c2.lease.acquire {vessel|group, channels, ttlS, preempt:false, suspend?:false, onExpire?}`; heartbeats renew (`session.heartbeat` renews all of an actor's leases, `c2.lease.renew` one); expiry releases with `lease.expired` and applies the channel's `onExpire` fallback. **[R]** One enum, `bda_ai | bda_guard | hold_last | neutral | keep` (meanings in `02-architecture.md` §5), valid per channel as follows (default in bold):

   | Channel | Valid `onExpire` |
   |---|---|
   | `steer` | **`bda_guard`**, `bda_ai`, `hold_last`, `neutral` |
   | `weapons` | **`bda_guard`** (fire authority back to Free, BDA guard decides), `keep` |
   | `targeting` | **`bda_guard`** (clear Skizzy assignments and filters), `keep` |
   | `sensors` | **`keep`**, `bda_guard` (BDA manages radars again) |
   | `ai_params` | **`keep`**, `bda_ai` (restore the parameter values captured when the lease was acquired) |
   | `formation` | **`bda_ai`** (members `ReleaseCommand`), `keep` |
   | `systems` | **`keep`**, `bda_ai` (restore captured SubTrim/ballast modes) |

   An invalid combination is an `INVALID_PARAMS` at acquire time.
2. **Precedence**: an actor may preempt a lease held by a lower-precedence actor when `preempt:true` (the loser gets `lease.preempted {by, reason}` and its orders become `superseded`). **[R]** Exception, so that take-over is reversible (§4.1): preemption by a **human**, or by any actor passing `suspend:true`, *suspends* the loser's lease and orders instead of superseding them (`order.suspended`, reason `human_override` or `borrowed`), and they are restored when the preempting lease ends. Equal or higher precedence → `LEASE_HELD`. Bands: human 1000, commander 800, group 700, unit 600, script 400, behaviour 300, BDA 0; doctrine may override per actor id. **[R]** The effective precedence is always `min(doctrine value, ROLE maxPrecedence)` from the permission file (`15-security-permissions.md` §2), so a doctrine file cannot lift a script above what the user's permission file allows.
3. **Humans**: the KSP pilot's implicit `steer` lease on the active vessel preempts everything and is released after idle; the map operator holds explicit leases like any actor but at 1000; `c2.lease.grant` lets a human delegate a channel at their precedence to an agent ("you have the ship").
4. **Delegation**: a commander can `c2.lease.delegate {to, vessels, channels, maxPrecedence}` so a unit agent acts with 700 on its units without the commander holding the leases itself. **[R]** The delegated precedence is a property of those leases, not of the actor: it must be ≤ the delegator's own effective precedence, and the delegate's other actions keep its own (clamped) precedence.
5. **ACL**: doctrine lists which roles may hold which channels (unit agents never get `cheat` capabilities; scripts default to `steer`+`formation` only).
6. Leases are the arbitration layer for *authority*; BDA remains the default behaviour on every channel that has no lease, so a quiet system is just BDA.
7. **[R] BDA does not know about leases.** A `targeting` lease makes Skizzy re-assert its assignment, but stock BDA can drop or overwrite an override target (missile routine in progress, team assist). The targeting executor therefore runs the re-issue loop in `07-bdarmory-integration.md` §1.4 ("Override semantics") and reports `assignment_lost` after three failed re-issues; with fork hook F1 the assignment is sticky and team assist cannot overwrite it.
8. **[R] Suspension stack.** Per (vessel, channel), the arbiter keeps the active lease plus a stack of suspended claims under it, ordered by precedence. When the active lease ends (release, achieved order, expiry), the top suspended claim is restored if its actor is still connected and still allowed. If that actor is gone, its orders follow the orphan rule (rule 1, `order.orphaned`). The channel's `onExpire` fallback applies only when the stack is empty. The stack is bounded at 4 entries per channel; a fifth suspension supersedes the oldest entry.

### 4.1 Mission command and direct control, together (P0, M4; mission level M6) — [REVIEW-ADDED, user clarification 2026-11-08]

The user wants both levels of command at all times, never one instead of the other:

- **Mission level.** Assign units or groups to missions (patrol, strike, escort, §11.2) or standing orders. Doctrine, the in-game executors and BDA's own AI then run them.
- **Direct level.** At any moment, drop to any unit or group and micromanage it: steer it, pick its target, fire a specific weapon, hold fire, set its emitters, or fly it yourself in KSP.
- **Hand back.** One action returns the unit to its mission, which re-plans from where the unit is now.

This is not a separate mode switch bolted onto the C2 engine. It falls out of leases and precedence: the human is an actor at 1000, a take-over is a set of human leases, and the suspension rules (§4 rules 2 and 8) make it reversible.

#### 4.1.1 Control state

The host derives a **control state** for every unit. It is recomputed only when a lease, order, mission assignment or pilot input changes, never per tick, so it costs nothing in a quiet battle.

```json
{"unit":"v:118822","summary":"manual","mission":{"id":"m:3","name":"Patrol North","participation":"suspended","reason":"human_override"},
 "channels":{
   "steer":    {"controller":"human","actor":"a:map-user","via":"take","since":1290.4},
   "weapons":  {"controller":"human","actor":"a:map-user","via":"take","since":1290.4},
   "targeting":{"controller":"mission","actor":"a:commander","via":"m:3/o:518","since":1201.0},
   "sensors":  {"controller":"mission","actor":"a:commander","via":"m:3","since":1201.0}},
 "resumeTo":{"steer":"m:3/o:517","weapons":"m:3"}}
```

| `controller` per channel | Meaning |
|---|---|
| `bda` | no lease: BDA AI and guard decide (the quiet default) |
| `mission` | the mission's planner holds the lease through an order |
| `order` | an explicit standing order by an actor holds the lease |
| `agent` / `script` / `behaviour` | that actor drives the channel directly |
| `human` | a human holds the lease (map, in-game window or CLI) |
| `pilot` | the human is flying the vessel in KSP (implicit stick lease) |

- **Unit summary.** The unit's `summary` is the highest-precedence controller over its channels: `pilot > human > agent > script > behaviour > order > mission > bda`. `partial` is set when a human holds some channels but not all.
- **Group summary.** A group's summary is the common value or `mixed` with counts (`mission 3 · manual 1`).
- **Reading it.**
  - `c2.control.get {units|group}` returns the state.
  - The `control.changed` event reports every transition: `{unit, channel, from, to, by, reason}`, category `c2`.
  - The SITREP carries a compact token: `ctl=M:m3` (mission), `ctl=H:sw` (human holds steer and weapons), `ctl=P` (piloted), `ctl=A:ddg2-ai`, or nothing for BDA.

#### 4.1.2 Take-over and release

| Call | Effect |
|---|---|
| `c2.control.take {units|group, channels?, reason?}` | **Sticky take-over.**<br>• Channels default to `steer, weapons, targeting`; `all` adds `sensors, formation, systems, ai_params`.<br>• Acquires human leases at the human's precedence with preemption. Lower holders are suspended (§4 rule 2), so their orders become `suspended` (reason `human_override`), not `superseded`.<br>• Mission participation becomes `suspended` on the taken channels only.<br>• The leases stay until released. |
| `c2.control.release {units|group, channels?, to?}` | `to` takes one of three values:<br>• `resume` (default): restores the suspension stack, so the mission or order continues.<br>• `bda`: cancels the suspended orders on those channels and unassigns the unit from its mission on them.<br>• An actor id: grants the channels to an agent or script (`c2.lease.grant`). |
| `c2.control.release_all {}` | Releases everything the calling human holds, each unit with `resume`. |
| any direct order or action by a human on a unit it does not hold | **One-shot intervention.**<br>• Applies to `flight.*` holds, `bda.assign_target`, `bda.fire`, `weapons_hold`, `move_to` and the like.<br>• Takes only the channels that action needs, with `via:"order"`.<br>• Returns them automatically (`resume`) when the order ends (achieved, failed or cancelled), or after the single action for `bda.fire`.<br>• A human `weapons_hold` is a standing order. It lasts until the human lifts it or releases the unit. |

**What take-over does physically: nothing, until the human acts.** Taking control changes who decides, not what the unit is doing:

- `steer` switches to `hold_last`: current heading, speed and altitude or depth, via the hold engine in `06-flight-control.md` §3.
- Fire authority, the current target assignment and the emitters stay as they were.
- The human's first command then replaces them.

So a take-over in the middle of a turn or an engagement never jerks the unit or drops a lock.

**What release does.**

- **Missions re-plan from the present.** On `resume`, a mission unit gets a fresh child order from the mission's planner, computed from its current position, fuel, ammunition and the current picture:
  - rejoin the route at the nearest leg;
  - return to the patrol zone by the shortest path;
  - re-evaluate targets under the mission's doctrine.

  It does not get the stale order it had before the take-over.
- **Explicit orders.** A suspended explicit order resumes if it is still valid, and is re-validated against `basedOnSeq` and the ROE. Otherwise it fails with `stale_decision` and the parent is notified.

**Missions while units are taken.** The planner treats taken channels as unavailable:

- A strike package whose escort is taken reports `degraded`.
- With doctrine `replanOnTakeover: true` (default false), the planner assigns another unit from the mission.

**Flying a unit yourself.**

- **From the map.** "Fly in KSP" (`c2.control.take` with `channels:[steer]` and `via:"pilot"`, then `flight.switch_vessel`) makes the vessel active and gives the KSP pilot a **sticky** steer lease. The in-game Flight window shows **Return to mission** (and a hotkey) to release it with `resume`.
- **Without taking over.** Touching the stick on the active vessel keeps the existing transient behaviour (`06-flight-control.md` §8): it suspends whatever holds steer and resumes it after `humanIdleS`.
- **Other channels.** Weapons, targeting and sensors stay with the mission unless the human takes them too, so the ship keeps defending itself while the human steers.

**Agents use the same calls.**

- **Commander borrowing.** A commander agent can take a unit out of a mission at its own precedence with `suspend:true` (borrow) and release it back.
- **Humans always win.** A human take-over suspends the agent's borrow in turn, and release restores it.
- **Handing a unit to an agent.** "Give to agent X" is a release to an actor, and taking it back is another `take`.

**Disconnects and safety.**

- **Grace period.** Human take-over leases belong to the human actor, not the connection. If the map disconnects, they survive for `humanGraceS` (default 120 s) so a restarted map re-attaches them. After that they release with `resume`.
- **Standing orders.** Human standing orders (`weapons_hold`, a route) stay in force until cancelled, and the in-game banner lists them.
- **Kill switch.** It releases everything at once (`06-flight-control.md` §10).
- **Idle reminder.** If a manually held unit sees no human command for `manualIdleReminderS` (default 300 s), the map and in-game window show a reminder ("DDG-2 manual for 5 min: return to mission?"). There is no automatic release, so the user's choice stands.

**Permissions.** `c2.control.*` is `control` level. A human's take-over is clamped like any lease by the ROLE table (`15-security-permissions.md` §2), and humans are at 1000.

#### 4.1.3 Acceptance

Milestones: M4 runs these with standing orders (`patrol_area`, `follow`) in place of missions; M6 reruns them with missions (`17-milestones.md`).

1. A destroyer on a `patrol` mission is taken over from the map with one click. It keeps its heading and speed, and its lock on a hostile track is not dropped.
   - The human steers it 5 km off the patrol line and orders `weapons_hold`.
   - **Return to mission** restores the patrol from the new position by the shortest path, and the mission's `weaponsControl` applies again.
   - Five `control.changed` events tell the story, and the mission shows the unit `suspended` and then `active`.
2. Partial take-over: the human takes only `weapons` on a frigate in an escort mission. The mission keeps steering it in the screen. The human fires one specific missile at a chosen track (`bda.fire`) and the ship holds fire otherwise; release returns fire authority to doctrine.
3. One-shot intervention: dragging "assign target" from a mission unit to a track, without taking over, engages that track. When the track dies, the unit returns to its mission with no further input.
4. "Fly in KSP" on a mission fighter switches the active vessel. The human flies it; the fighter's guard mode keeps firing countermeasures and missiles under the mission's doctrine. **Return to mission** hands steering back and the fighter rejoins its CAP station.
5. A commander agent borrows a unit (`suspend:true`), the human takes the same unit, then releases it. Control returns to the agent's borrow, and after the agent releases, to the mission. Nothing is superseded on the way.
6. The map is closed during a take-over and reopened within 120 s: the take-over is still held. If it stays closed, the unit returns to its mission after 120 s, while the human's `weapons_hold` stays in force.

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
| Builder (design loop) | `claude-opus-5-5` | `high` | interactive | core, craft, bda, rec, sim |
| Analyst (AAR) | `claude-sonnet-5-5` or Opus | `medium` | post-battle | rec, tactical (replay) |

Prices (first-party API, 2026-10-06 table): Opus 5.5 $4/$20 per MTok, Haiku 5.5 $0.10/$0.50 (≤ 100 K prompt; $0.50/$2.50 beyond, so unit-agent contexts are kept under 100 K), Sonnet 5.5 $2/$10. Model ids are exact strings, no date suffixes. Adaptive thinking is on by default on these models; `thinking: {type:"adaptive"}` or omitted; effort via `output_config.effort`. Forced `tool_choice` (`any`/`tool`) is a 400 on Opus 5.5 and Sonnet 5.5, so agents use `auto` with `strict:true` tool schemas and prompt instructions. Verify bindings against the Anthropic SDK docs at build time; this design does not pin SDK versions.

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
- **Context [R]**: the orchestrator's message history per agent is **append-only**. Thinking blocks from earlier turns are only valid in the conversation that produced them, so trimming to "the last K turns" or editing earlier messages (pass 1.0's fallback) breaks the request. Long battles use server-side compaction (beta `compact-2026-01-12`); when compaction is unavailable the orchestrator starts a fresh conversation whose first user message is its own battle-memory summary plus the current SITREP. Volatile data only ever goes into new messages at the end.
- **Refusals and empty turns [R]**: a response with `stop_reason: "refusal"` is logged to the audit with the triggering inbox, the agent's units stay on doctrine defaults, the parent actor gets a `c2` note, and the same input is not retried more than once. A turn that ends without tool calls is treated as a report (logged as `c2.order.report`), not as an error. `max_tokens` stops are retried once with a larger limit.
- **Stale decisions**: every order the agent issues carries `basedOnSeq` (the `seq` of its last `tac_query`); the host rejects orders older than `maxStaleSeq` (doctrine, default 3 s at 5 Hz) with `STALE_DECISION`; the orchestrator re-queries automatically once.

### 8.3 Budgets and cost control — [ADDED]

Per actor: `usdPerBattle`, `tokensPerMinute`, `maxTurnsPerMinute`, `maxToolCallsPerTurn`. The orchestrator accounts `usage` per response and stops an agent at the cap (`BUDGET_EXHAUSTED`, agent frozen, units fall back to doctrine). Estimated cost of the SAG scenario (`16-scenarios.md` §2): commander 20 turns × ~8 k cached input + 1 k output ≈ $0.40–0.60; four Haiku unit agents 150 turns × ~3 k input + 300 output ≈ $0.10 total. A 50-run batch never involves LLMs (scripts and in-game executors only) unless the scenario asks.

### 8.4 Think-time control — [IDEAS]

When a trigger of class `urgent` fires (inbound missiles with `tcpa` under the agent's typical turn time) and the responsible agent has not answered, the orchestrator calls `sim.time.slow {scale:0.25, maxSeconds:20, reason}` (visible in-game as a banner; the human can veto with the kill switch or `sim.time.set 1`). Lockstep (`sim.hold/step`) is used in tests and when the user asks for turn-based play.

### 8.5 Memory between battles (P2) — [ADDED]

`<KSP>/Skizzy/memory/<role>/`: AAR summaries written by the analyst (what worked, losses, missile expenditure), per-craft performance rows, per-opponent notes, and "lessons" the commander appends. The orchestrator injects a role-filtered, size-capped excerpt into the stable prompt prefix. No vector store in v1; files are small and human-editable.

### 8.6 Human in the loop

The human is an actor at precedence 1000 through three surfaces: the KSP controls (implicit steer lease), the map (orders, ROE, zones, leases, budgets, kill switch), and Claude Code (talks to the commander agent or issues orders directly). **[R]** On every surface the human can command at mission level and drop to direct control of any unit at any moment, then hand it back (§4.1). The commander agent can **ask** the human (`c2.ask_human {question, options, timeoutS}` → map/in-game prompt) for decisions doctrine marks as `human_required` (e.g. `weapons_free` on `unknown` tracks).

## 9. Scenario director (P1) — [ADDED]

`sim.scenario.*` loads a scenario file (`10-scripting-sdk.md` §7) with spawn plans, groups, doctrine, initial orders, waves (`at ut`, `when trigger`) and end conditions; `sim.scenario.snapshot` writes the live state (positions, headings, speeds, teams, AI/WM params, orders, zones) back to a scenario file so a battle position can be restored by respawning (`sim.scenario.restore`), independent of KSP saves (which are unreliable mid-combat with missiles in flight).

## 10. Acceptance (summary)

1. Lease conflict: a script holding `steer` on DDG-2 is preempted by the commander agent (`lease.preempted`), and cannot preempt back (`LEASE_HELD`); the human stick takes over both and both resume after 5 s idle.
2. `defend_sector` orders to a 3-ship SAG assign 40 inbound tracks with ≤ 2 missiles per track across sectors; no CIWS fires on a track outside its ship's sector while another ship has it; `c2.wta.solve` runs < 1 ms.
3. Doctrine trigger `raid` expands `ad_umbrella` within one C2 tick of the fourth hostile track.
4. Haiku unit agent wakes on `missile.inbound`, answers within one turn (median ≤ 4 s wall) with `bda_fire_countermeasures` and a report; budget accounting shows cost per unit per battle.
5. Orchestrator crash: leases expire in 15 s, all units fall back to BDA guard, the agents' orders get `owner:null` with `order.orphaned` events, map shows it; restart re-attaches with `resume`.
6. **[R]** A `script` actor whose doctrine asks for precedence 900 gets 500 (ROLE clamp) in the hello result; an invalid `onExpire` for a channel is refused at acquire time.
7. **[R]** A forced refusal in a scripted-fake-LLM test leaves the unit on doctrine defaults, writes one audit entry and notifies the parent; a 2-hour battle with compaction keeps every request valid (no rejected thinking blocks).

## 11. Genre reference: CMO-style C2 features — [REVIEW-ADDED]

The user's reference is the genre of *Command: Modern Operations*. Used as a checklist only; the C2 model below is Skizzy's own and maps onto orders, leases, doctrine and the TPE. Map-side features are in `11-tactical-map.md` §12.

**[R] Mission command plus our own capabilities, not instead of them (user clarification 2026-11-08).** The genre's mission and doctrine command is added on top of direct control, never as a replacement for it. The user can:

- command at mission level, letting doctrine and BDA AI run the units;
- drop to direct micromanagement of any unit or group at any moment (steer, pick a target, fire this missile, hold fire, fly it in KSP);
- hand it back to the mission afterwards.

The switch is seamless and shown per unit (§4.1, `11-tactical-map.md` §6.1).

### 11.1 Doctrine and ROE with inheritance (P1, M4 basic, M6 full)

Doctrine settings resolve along **side → mission → group → unit**; each level may set a value or inherit. The effective value is computed when something changes (not per tick) and cached per unit; `c2.doctrine.effective {unit}` returns each value and the level it came from.

| Setting | Values | Executor |
|---|---|---|
| `weaponsControl` per domain (air, surface, subsurface, missile) | `free`, `tight`, `hold` | fire authority (F3/F4; partial without the fork) |
| `engageUnknowns` | `never`, `if_closing`, `always` | target filter |
| `engageOpportunity` | targets outside the mission's area or type: `yes`/`no` | target filter |
| `salvoPerTarget` by target class | e.g. `{fighter: 2, ASM: 2, destroyer: 4, submarine: 2}` | assignments and WTA (with F1/F2); stock fallback: WM `maxMissilesOnTarget` set to the maximum, Skizzy limits assignments |
| `winchester` | `{ammoPct, action: rtb|disengage|guns_only|continue}` | executor watches `bda.ammo` |
| `bingo` | `{fuelPct, action: rtb|disengage}` | executor watches fuel |
| `withdraw` | `{damagePct, floodPct, action: withdraw_to <ref>|rtb}` | executor watches HP and HullBreach flood |
| `redeploy` | `{when: rearmed|repaired|manual}` | executor |
| `emcon` | radar `active|passive|off`, sonar `active|passive|off`, jammer `on|off` | §11.3 |
| `requireIff`, `minRangeToFriendlyM` | as §2.3 | ROE evaluator |

### 11.2 Missions as first-class objects (P1, M6)

A **mission** is a long-lived container that units are assigned to instead of being micromanaged: `c2.mission.create {type, name, area|target|unit, doctrine?, startUt?, endUt?, assign:[units|groups]}`, `c2.mission.assign|unassign|update|activate|cancel|list`. A unit belongs to at most one mission; its orders come from the mission's planner, and an explicit order to the unit suspends its mission participation until the order ends. **Mission command never locks the user out:** any unit or group in a mission can be taken over, micromanaged and handed back at any moment (§4.1); the mission re-plans from the unit's current state when it returns.

| Mission type | Expands to (via the tactics library, §6) | Pri |
|---|---|---|
| `patrol` with subtype `aaw`, `asuw`, `asw`, `sea_control` | `patrol_area` in the mission zone + engagement rules by subtype (AAW: air/missile tracks; ASuW: surface; ASW: sonar posture, `asw_prosecute` trigger inside a prosecution zone) | P1 |
| `strike` | `strike_package` against a target (track, zone or position), with escorts and a time on target | P1 |
| `escort` | `screen`/`follow` around the escorted unit or group | P1 |
| `support` with subtype `aew`, `tanker` | AEW: orbit at a reference point with radar active; tanker needs refuelling, which BDA does not model, so it is P3 | P2 (AEW) |
| `ferry` | `move_route` to a base or carrier, weapons hold | P2 |

Missions are visible to agents (`c2.mission.list` in compact text) and the commander agent can create and assign them; this is the preferred way for an LLM to command a large force (one mission instead of forty orders).

### 11.3 EMCON (P1, M4)

`emcon` can be set per unit, group, mission or side (through doctrine) and by order (`emcon {level}` in §2.1). The executor switches BDA radars (`ModuleRadar` enable/disable), sonar between active and passive (`sonarMode`), IRST and jammers. It affects detectability through BDA's own mechanics: an active radar produces RWR pings (`RadarWarningReceiver`) and makes the emitter detectable by RWR-equipped enemies; an active sonar ping is heard by the target side (`unit.sonar_ping`); passive keeps a unit silent but blinds it to non-emitting targets. The SITREP and the map show the effective state per unit.

### 11.4 Contact classification flow (P1, M2 TPE / M3 display)

Tracks carry `idState` ∈ `detected → classified → identified` (`08-tactical-picture.md` §2): *detected* = any sensor contact; *classified* = class known with `classConf ≥ 0.6` from size, speed, domain and emitter type; *identified* = side known (visual within `idRangeM`, hostile act, identification shared by a datalinked unit, or god view). Higher sensor quality and shorter range move a track up faster; `track.class` and `track.iff` events mark the steps; compact text shows `?` for unclassified classes.

### 11.5 Sides and posture (P1, M4)

Sides are BDArmory teams. Posture between sides uses BDA's own `BDTeam.Allies` list and `Neutral` flag (`bda.team.relations {team, allies:[…], neutral}`); "unfriendly" postures are expressed as hostile-with-`engageUnknowns:never` in doctrine. The IFF of tracks follows (§2 of `08-tactical-picture.md`).

### 11.6 Message log, losses and pause-on-event (P0/P1)

The events taxonomy (`03-protocol.md` §7) already carries every genre message (contact detected/lost, weapon fired, hit, miss = `missile.defeated|timeout`, unit lost). Additions: a `category` field on every event (`contact`, `weapons`, `unit`, `order`, `mission`, `c2`, `system`) for the map's message log filters; `c2.losses {side?}` returning losses and expenditures per side in compact text; and per-session **pause-on-event** settings (`session.pause_on {events:[…]}`, control level) that hold the simulation via `sim.hold` or slow it via `sim.time.slow` when a listed event fires, with the event named in the banner.

### 11.7 Beyond the genre: what Skizzy keeps and adds

These are Skizzy's own strengths. The genre features must not crowd them out, and the map and docs present them up front:

- **Real physics and real AI.** Units are KSP vessels flying, sailing and diving under real physics. Damage, sensors and weapons are BDArmory's own. A mission does not move abstract counters; it drives real BDA AI through executors.
- **LLM and script actors at every level.** Commander, group and unit agents, Python swarms and behaviour trees use the same orders, leases and missions as the human. A mission can be run by doctrine, by an agent or by a script, and the human can take over from any of them (§4.1).
- **Flying a unit yourself.** One click on the map puts the human in the cockpit in KSP while the rest of the force keeps fighting. Afterwards the unit goes back to its mission.
- **Craft building.** The user designs, validates and trials their own ships and aircraft (`05-craft-building.md`), then fields them in missions and compares them in batches.
- **Full logging and replay.** Any field can be logged at any rate. Battles are recorded, replayed with slow-motion scrubbing, queried, and A/B-compared across batch runs (`12-logging-recording-replay.md`, `14-testing-batch-sim.md`).
- **An open protocol.** Every capability is reachable from scripts, agents, the map, the CLI and the in-game console.

### 11.8 Acceptance (genre features)

1. A side-level `weaponsControl: tight` is overridden by a group-level `free` for one escort group; `c2.doctrine.effective` shows the source of each value; the escorts fire on hostiles, the rest only on assigned targets.
2. A unit at 20 % missiles with `winchester {ammoPct: 25, action: rtb}` returns to its base point and reports why.
3. An ASW patrol mission with three escorts and a prosecution zone switches sonars to passive, prosecutes a sub contact inside the zone, and reports mission status.
4. EMCON passive on a destroyer stops RWR pings on the enemy side within one TPE tick.
5. Pause-on-event "missile launched at own unit" pauses the game within one frame of the launch event, with the banner naming the shooter.
