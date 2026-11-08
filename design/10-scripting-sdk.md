# 10 Scripting and automation

Scripts are first-class actors. Python first (`pip install skizzy`), C# second (`Skizzy.Client`, shared with the bridge and the map). In-game behaviours are behaviour trees and C# plug-ins. A mini-sim lets scripts be developed without KSP.

## 1. Python SDK (`skizzy`, P0)

**[R] Staging.** M0 ships `skizzy` 0.1: a small synchronous JSON client (`skizzy.connect()`, `sk.call(name, **args)`, `sk.watch(selector, hz)`, `sk.log.start/stop`, `sk.shot()`), REPL/notebook helpers and the `skizzy call|watch|log|shot` CLI, so the user can script against KSP from day one without Claude Code (`21-user-experience.md` §4). The typed, async, msgpack SDK below is M4.

Packages: `skizzy.client` (transport), `skizzy.models` (typed records generated from the protocol schemas), `skizzy.tactical` (track mirror, BRAA/geometry, filters), `skizzy.c2` (orders, leases, groups, doctrine), `skizzy.flight` (holds, setpoint batches, routes, formations), `skizzy.bda`, `skizzy.craft`, `skizzy.rec` (recordings), `skizzy.sim` (mini-sim), **[R2]** `skizzy.comms` (team channel, reports, messages), `skizzy.examples.api_commander` (optional API-key commander example with optional spending caps, `09-c2-multi-agent.md` §8.5; pass 1's `skizzy.agents` orchestrator is removed), `skizzy.templates`.

```python
# pseudocode, async-first; sync wrappers exist for notebooks
import asyncio, skizzy

async def main():
    async with skizzy.connect(actor=skizzy.Actor(id="a:swarm-py", kind="script", role="swarm-script"),
                              encoding="msgpack", view=skizzy.View(team="Blue", fog=True)) as sk:
        drones = await sk.c2.group.create("g:swarm", members=await sk.ksp.vessels.find(name_like="Drone_*"))
        await sk.c2.lease.acquire(drones, channels=["steer", "formation"], ttl_s=10, on_expire="bda_ai")
        picture = await sk.tactical.mirror(filter=dict(iff=["hostile"], domain=["air", "missile"],
                                                      within=dict(ref="g:swarm", m=60_000)),
                                            rate_hz=10, thresholds=dict(pos_m=20))
        async for tick in sk.clock(hz=20):                       # UT-aligned ticker
            threats = picture.top(n=5, by="threat", frame=skizzy.BRAA(ref="g:swarm"))
            batch = skizzy.flight.SetpointBatch()
            for i, d in enumerate(drones.members):
                hdg, spd, alt = my_swarm_law(i, d, threats, tick.ut)
                batch.add(d, heading=hdg, speed=spd, altitude=alt)
            await sk.flight.setpoint_batch(batch, deadman_ms=500) # one msgpack frame, fixed-affinity job; silence > 500 ms → hold_last
            if picture.events.pop("missile.inbound"):
                await sk.flight.setpoint_batch(evade(drones, threats))

asyncio.run(main())
```

Design rules: every call has a typed request/response model; streams are async iterators with back-pressure; `mirror` keeps a local track table applying deltas so the script never re-queries; heartbeats are automatic while the context manager is open; exceptions are typed (`LeaseHeld`, `RoeViolation`, `StaleDecision`, `PermissionDenied`); `sk.op.wait` for long operations; `sk.clock(hz)` is driven by `sim.clock` so slow-mo and pauses are respected; `skizzy.geo` has great-circle/BRAA/CPA helpers identical to the host's (shared test vectors).

Helpers: `skizzy.formations` (templates, slot math), `skizzy.tactical.braa(...)`, `skizzy.c2.orders.wait_until(order, status)`, `skizzy.rec.open(path)` → pandas/pyarrow tables, `skizzy.compact.parse(text)` for round-tripping compact text.

## 2. C# SDK (`Skizzy.Client`, P0)

Same surface, `netstandard2.0` + `net10.0`: `SkizzyClient.ConnectAsync(options)`, `IAsyncEnumerable<Delta>` streams, `TrackMirror`, typed exceptions. Used by the map, the bridge, `Skizzy.Sim` tests and third-party tools. The bridge uses it for everything except the MCP layer.

## 3. Script supervisor (P1) — [ADDED]

`skizzy run script.py --actor swarm-script --restart on-failure --cpu 1 --mem 512M` runs a script under a supervisor: it owns the leases' heartbeat from a side thread (so a busy script does not lose leases), restarts on crash with back-off, releases leases cleanly on exit (`on_expire` is still the safety net), captures stdout to the recording's `scripts` channel, and reports its status through `host.sessions` (**[R]** pass 1.0 called this `scripts.status`, which no other file defines), which the map and the in-game connections window show. Scripts declare `requires: {hooks: ["bda.waypoints"], areas: ["ksp.flight"]}` so failures are explicit at start.

## 4. Templates (`skizzy.templates`, P1)

`swarm_controller` (the 20-drone scenario), `ad_coordinator` (WTA loop without an LLM; **[R]** it needs the `targeting` and `weapons` channels, which the default `script` ROLE does not grant, so the template declares `requires: {channels: [targeting, weapons]}` and `skizzy run` fails at start with the exact `ROLE`/`CLIENT` line to add to `permissions.cfg` from the in-game window), `asw_screen`, `formation_keeper`, `craft_iterate`, `batch_ab`, `log_profile_switcher`, `aar_report`. Each is a runnable script with a config block and tests against the mini-sim.

## 5. In-game behaviour engine (P1)

### 5.1 Behaviour trees

JSON/YAML trees loaded per vessel (`c2.behaviour.attach {vessel, tree, blackboard}`), ticked at 10 Hz by the Behaviour engine with a zero-alloc interpreter (nodes are structs in an array; the blackboard is a fixed slot table of floats/ints/handles; conditions and actions are a closed set compiled to delegates once).

```yaml
tree: sub_shoot_and_scoot
blackboard: { fire_depth: -10, hide_depth: -150, reattack_s: 240 }
root:
  selector:
    - sequence:                      # inbound torpedo → crash dive and turn
        - cond: { event: missile.inbound, class: torpedo }
        - act:  { set_depth: { m: -200 } }
        - act:  { turn: { relative_deg: 90, side: away_from_threat } }
    - sequence:                      # attack cycle
        - cond: { track_in_range: { filter: { iff: [hostile], domain: [surface] }, weapon: torpedo } }
        - act:  { set_depth: { m: "$fire_depth" } }
        - wait: { until: { depth_within: 3 } , timeout_s: 60 }
        - act:  { fire_authority: free }
        - wait: { until: { missiles_away_gte: 2 }, timeout_s: 30 }
        - act:  { fire_authority: hold }
        - act:  { set_depth: { m: "$hide_depth" } }
        - act:  { turn: { relative_deg: 90, side: random } }
        - wait: { seconds: "$reattack_s" }
    - act: { bda_default: true }     # otherwise leave it to BDA
```

Node set: composites (`selector`, `sequence`, `parallel`, `repeat`), decorators (`inverter`, `cooldown`, `timeout`, `once`), conditions (track filters/counts, SITREP field comparisons, events, zone membership, order status, random), actions (any `flight.*`/`bda.*`/`c2.*` capability the vessel's leases allow, with the BT's actor precedence 300 unless attached by a higher actor). Trees are validated at load; a tree that throws is detached for that vessel only.

### 5.2 C# plug-ins

`GameData/SkizzyProtocol/Behaviours/*.dll` implementing `ISkizzyBehaviour { void Bind(IBehaviourContext ctx); void Tick(float dt); void Unbind(); }` where `IBehaviourContext` gives allocation-free access to the vessel's SITREP record, the team's track snapshot, BDA facade calls (assign target, set fire authority, command fly-to, set holds) and a blackboard. Plug-ins are trusted code (the user installs them) and are hot-reloadable in debug builds (collectible `AssemblyLoadContext` is not available on net48; hot reload = unload-by-restart of the behaviour and load of a new DLL copy with a version suffix; documented limitation).

### 5.3 Where the fast loops run

| Loop | Where | Rate | Why |
|---|---|---|---|
| steering, fire | BDA AI + Skizzy holds | 50 Hz | physics |
| swarm laws, station keeping | Python over msgpack or in-game BT | 10–20 Hz | loopback latency 1–3 ms is fine at this rate |
| tactics, re-tasking | **[R2]** in-game missions and doctrine, scripts, client-spawned delegates | 1 Hz planners; delegates event-driven, seconds | LLM latency for delegates only |
| command | the commander: a human or one agent (any MCP client) | minutes | |

## 6. Mini-sim (`skizzy.sim`, P1) — [ADDED]

A pure-Python kinematic world speaking the same protocol (subset): vessels as points with heading/speed/altitude dynamics, radar as range circles with probability of detection, missiles as pursuit flyers, simple damage. It runs the same `skizzy` client code unchanged (`skizzy.connect(url="sim://")`), so a swarm script or an AD coordinator can be developed and unit-tested on a laptop in seconds, then pointed at KSP. It is not a physics model; it is a protocol-faithful stand-in. The C# `Skizzy.Sim` (FakeKsp, `14-testing-batch-sim.md`) is the heavier twin used for host-engine CI.

## 7. Declarative files

| File | Schema (summary) | Used by |
|---|---|---|
| **Scenario** (`library/scenarios/*.yaml`) | `name`, `site {body, lat, lon}`, `physicsRangeM`, `settings` (BDA overrides), `teams`, `spawn` (template/ring/list with craft from library or paths, positions, headings, formation templates), `groups`, `doctrine`, `orders` (initial), `waves [{at|when, spawn, orders}]` (**[R2]** `spawn` entries use the spawn-anywhere spec: mode air/sea/submerged/ground/runway, altitude, speed, formation or multi-spawn line, `06-flight-control.md` §9.1), **[R2]** `respawn` rules, `seats {side: human|agent}` with `agentTakeover`, `stance` (initial, per side pair), `comms {model, broadcastCallsigns, agentsMaySurrender}`, `scoring {subduedWeight, violationPenalty}`, `end {ut_limit, when}` (**[R2]** plus `side_surrendered`, `stance_held`, `transit {side, units, min, zone}`, `22-communications-diplomacy.md` §5), `recorder {profile}`, `matrix`, `repeats`, `expect` | `sim.scenario.load`, `skizzy battle run` |
| **Doctrine** (`library/doctrine/*.yaml`) | `09-c2-multi-agent.md` §5 | C2 engine, mission planners |
| **Tactic** (`library/tactics/*.yaml`) | `name`, `params` schema, `expand` (order templates), `triggers`, optional `hook: python:module.fn | csharp:Type` | planner |
| **Behaviour tree** (`library/behaviours/*.yaml`) | §5.1 | Behaviour engine |
| **Log profile** (`library/logprofiles/*.yaml`) | `12-logging-recording-replay.md` §2 | recorder |
| **Blueprint** (`library/blueprints/*.yaml`) | `05-craft-building.md` §3 | assembler |
| **Formation** (in doctrine or standalone) | slots `{right, back, up}` | formation executor |

All files carry `schema: skizzy/<kind>/1` and are validated with published JSON Schemas; the SDK ships them so editors get completion.

## 8. Versioning

The Python package version tracks the protocol MINOR (`skizzy 1.2.x` ↔ protocol `1.2`); older clients negotiate down; generated models carry `x-skz-since`. Breaking protocol changes (2.0) ship a compatibility shim for one minor cycle.

## 9. Acceptance (summary)

1. The swarm script above controls 20 drones at 20 Hz against the mini-sim and then KSP with no code change; host cost ≤ 0.3 ms/step.
2. Killing the script process releases its leases within 15 s; drones return to BDA AI.
3. The `sub_shoot_and_scoot` BT completes one attack cycle on a sub with SubTrim; the BT allocates 0 B per tick after warm-up.
4. `skizzy run` restarts a crashing template twice, then stops and reports.
