# 03 Protocol

Normative wire design. It extends `PROTOCOL-SPEC-DRAFT.md` 0.2; sections of that draft that still hold are referenced as "spec 0.2 §n" rather than repeated (transport hygiene §3.2, limits §3.3, catalog data model §7, safety levels §8, handles and value encoding §9, errors §10, Tier 2/3 contract API §12).

## 1. Transport and encodings

- WebSocket at `ws://127.0.0.1:47549/skizzy`, loopback only, token in `Authorization: Bearer`, `Origin` denied unless allow-listed, `Host` checked (spec 0.2 §3). Subprotocol `skizzy.v1`.
- **Text frames** carry one JSON-RPC 2.0 object (UTF-8). **Binary frames** carry one MessagePack-encoded JSON-RPC object with the same structure (keys as strings). A session negotiates `enc.msgpack` in `session.hello`; after that, requests may use either frame type and **the host answers in the frame type of the request**. Notifications (streams, events) use the encoding chosen per subscription (`encoding: "json" | "msgpack"`).
- A third representation, **compact text**, is not a frame type but a `format` for tactical payloads (§6). It is a JSON string field, so it travels inside either frame type.
- MessagePack rules: canonical msgpack 2.0 (no extension types except `-1` timestamp for `wallUtc`), map keys are short strings, numbers are the narrowest exact encoding, binary blobs (images, recording chunks) are `bin`. Python uses `msgpack` (pip); C# uses the in-house zero-alloc writer/reader in `Skizzy.Protocol` (about 600 lines; no external dependency in-game).
- Limits (per session, advertised in hello): `maxMessageBytes` 4 MB (binary 16 MB for recording chunk reads), `maxInflight` 64, `maxSubscriptions` 128, `maxConnections` 32 host-wide, outbound queue 16 MB, idle timeout 10 min (heartbeat pings every 30 s).

## 2. Session and actor

```json
→ {"jsonrpc":"2.0","id":1,"method":"session.hello","params":{
     "protocol":"skizzy-protocol","versions":["1.0"],
     "client":{"name":"skizzy.agents","version":"0.3.0","kind":"orchestrator"},
     "actor":{"id":"a:ad-coord","kind":"llm","role":"group","model":"claude-haiku-5-5","parent":"a:cmdr","display":"AD Coordinator"},
     "features":["ops","streams","events","enc.msgpack","confirm.client","lockstep"],
     "view":{"team":"Blue","fog":true},
     "resume":{"session":"s-7f3a","fromEventCursor":5531}}}
← {"jsonrpc":"2.0","id":1,"result":{
     "protocol":"skizzy-protocol","version":"1.0","session":"s-9c21","actor":"a:ad-coord",
     "features":["ops","streams","events","enc.msgpack","confirm.game","confirm.client"],
     "limits":{...},"catalogRevision":88,"areas":{...},"grants":{...},
     "precedence":700,"team":"Blue","fog":true,
     "meta":{"ut":1203.40,"frame":60170,"scene":"FLIGHT","paused":false,"timeScale":1.0,"warp":{"mode":"none","rate":1}}}}
```

- `actor.kind` ∈ `human | llm | script | behaviour | map | harness | bridge`. `role` is free text validated against the doctrine's role list when one is loaded; `precedence` is assigned by the host from the role table (`09-c2-multi-agent.md` §4) and can only be lowered by the client.
- `view.team` selects the team whose picture this session sees under fog-of-war; `fog:false` requests god view, which requires the `tac.god` grant (`read` level but gated per actor: unit agents normally do not get it).
- `resume` lets a reconnecting client keep its session id, event cursor and lease ownership (leases are tied to the actor id, not the socket; the TTL keeps running while disconnected).
- Every result and notification carries `meta` with `ut`, `frame`, `scene`, `paused`, `timeScale`, `warp`, `catalogRevision`, and `seq` for tactical data (the snapshot sequence number the payload was computed from).

## 3. Method families

| Family | Methods (abridged) | Affinity | Notes |
|---|---|---|---|
| `session.*` | `hello`, `bye`, `heartbeat` | any | heartbeat renews the actor's leases |
| `host.*` | `stats`, `state`, `selfcheck`, `engines` (stream) | any/snapshot | |
| `catalog.*` | `get`, `search`, `describe` | any | |
| `cap.call` | generic call; all capabilities below are also callable through it | per capability | |
| `op.*` | `get`, `wait`, `cancel`, `list` | any | |
| `stream.*` | `subscribe`, `modify`, `unsubscribe`, `list` | any | |
| `events.*` | `subscribe`, `unsubscribe`, `poll` | any | ring buffer ≥ 8192 |
| `trigger.*` | `create`, `list`, `delete` | main | host-evaluated conditions → events |
| `ksp.*` | vessels, parts, modules, query, config, action groups, staging, time, scene, game save/load, log | main/fixed/snapshot | PLAN v0.2 §4.4 plus `ksp.flight.*`, `ksp.camera.*`, `ksp.editor.*` |
| `craft.*`, `lib.*` | craft documents, blueprints, validation, trials, library | main/any | `05-craft-building.md` |
| `bda.*` | spawn, teams, WM, AI, weapons, radar, competition, settings, logs, hooks | main/fixed/snapshot | `07-bdarmory-integration.md` |
| `tac.*` | `query`, `sitrep`, `track.get`, `track.history`, `zones.*`, `bullseye.*`, `threats`, `engagements`, `subscribe` (sugar over stream) | snapshot | `08-tactical-picture.md` |
| `c2.*` | `order.issue|amend|cancel|get|list|claim|report`, `lease.acquire|release|list`, `group.*`, `doctrine.*`, `roe.*`, `tactic.run`, `wta.solve`, `plan.*` | main | `09-c2-multi-agent.md` |
| `flight.*` (alias `ksp.flight.*`) | `state`, `set_controls`, `hold`, `release`, `setpoint_batch`, `direct`, `route`, `formation`, `handoff`, `switch_vessel`, `depth` | fixed | `06-flight-control.md` |
| `rec.*` | `profile.list|start|stop`, `mark`, `list`, `query` (bridge), `export` (bridge) | main/any | `12-logging-recording-replay.md` |
| `sim.*` | `clock`, `time.set`, `time.slow`, `hold`, `step`, `release`, `scenario.load|snapshot|restore`, `batch.*` | main | |
| `map.*` | `annotations.*` (shared drawings), `view.*` | main/any | shared state for map clients and agents |
| `naval.*` | HullBreach, SubTrim, ballast, ACA, NAS, MNWS adapters | main/fixed | `04-ksp-and-mod-access.md` §5 |
| `permissions.*` | `get`, `request` | any | no setter over the wire, ever |
| `reflect.*` | `get`, `set`, `call` | main | gated, `unsafe` |

Full per-capability schemas are generated from the catalog and published as JSON Schema 2020-12 documents with `$id` `https://skizzy.dev/schema/1.0/<name>.json`; the design documents show representative examples only.

## 4. Core record types

### 4.1 Track

```json
{"id":"t:117","seq":88213,"team":"Blue",
 "iff":"hostile","domain":"air","class":"missile","subclass":"ASM",
 "identity":{"name":null,"vessel":null,"teamGuess":"Red"},
 "pos":{"lat":-0.131,"lon":-73.92,"alt":32.0},
 "vel":{"course":271.4,"speed":612.0,"vs":-1.2},
 "quality":0.82,"ageS":0.4,"firstSeenUt":1188.2,"lastSeenUt":1203.0,
 "sources":["radar","datalink"],"lastSensor":{"kind":"radar","by":"v:3281937421"},
 "lockedBy":["v:3281937421"],"engagedBy":["v:3281937421"],"missilesInFlightAt":2,
 "threat":{"score":0.91,"toUnits":["v:3281937421","v:118822"],"tcpaS":41.0,"cpaM":120.0,"closing":true,"reasons":["ASM","closing","tcpa<60"]},
 "state":"alive","dr":false}
```

`dr:true` marks a dead-reckoned position (no sensor update since `lastSeenUt`). Bearing-only tracks (RWR, passive sonar) have `pos:null`, `bearing:{from:"v:..",deg:…,errDeg:…}`, `rangeEstimateM:null`.

### 4.2 Order

```json
{"id":"o:412","traceId":"tr-7f21","type":"defend_sector","issuer":"a:ad-coord","recipient":"g:sag-1",
 "params":{"sector":{"center":"bullseye:bravo","fromDeg":300,"toDeg":60,"minM":0,"maxM":60000},"priorityTargets":["missile"]},
 "constraints":{"roe":"tight","zones":{"noFire":["z:harbour"]},"maxMissilesPerTrack":2},
 "priority":700,"validFromUt":1203.4,"validUntilUt":null,"basedOnSeq":88213,
 "status":"executing","statusReason":null,"progress":{"tracksEngaged":6,"tracksKilled":2},
 "claimedBy":null,"children":["o:413","o:414"],"createdUt":1203.4,"updatedUt":1209.0}
```

Status machine: `pending → accepted → executing → achieved | failed | cancelled | superseded | expired | suspended`. Reasons are an enum plus text: `no_executor`, `lease_denied`, `roe_violation`, `target_lost`, `out_of_ammo`, `unreachable`, `unit_destroyed`, `scene_change`, `stale_decision`.

### 4.3 Lease

```json
{"id":"l:91","vessel":"v:118822","channel":"steer","holder":"a:swarm-py","precedence":400,
 "acquiredUt":1200.0,"expiresUt":1215.0,"heartbeatS":5,"preemptible":true,"onExpire":"bda_guard"}
```

Channels: `steer`, `weapons`, `targeting`, `sensors`, `ai_params`, `formation`, `systems`. See `09-c2-multi-agent.md` §4 for semantics.

### 4.4 Zone and bullseye

```json
{"id":"z:mez-1","kind":"sector","name":"MEZ Bravo","team":"Blue","shape":{"type":"sector","center":{"lat":-0.15,"lon":-74.2},"fromDeg":300,"toDeg":60,"minM":0,"maxM":60000},
 "altM":{"min":0,"max":20000},"role":"weapons_engagement","owner":"a:cmdr","createdUt":1190.0}
{"id":"be:bravo","name":"BRAVO","team":"Blue","pos":{"lat":-0.15,"lon":-74.2}}
```

Shapes: `circle`, `sector`, `polygon`, `corridor` (polyline + width), `box` (rotated rectangle). Positions are always body-relative geo.

### 4.5 SITREP (own unit)

```json
{"vessel":"v:3281937421","name":"DDG-1 Kirov","class":"destroyer","team":"Blue","hp":0.93,
 "pos":{...},"vel":{...},"depthM":0,"flood":{"tonnes":0,"capacity":820,"inflowTps":0,"leaking":0,"foundered":false},
 "ai":{"type":"surface","surfaceType":"Water","enabled":true,"command":"Free","status":"Engaging"},
 "wm":{"guard":true,"fireAuthority":"tight","target":"t:117","secondaryTargets":4,"missilesAway":6,"engagedTargets":3,"incomingMissiles":2,"underFire":false},
 "weapons":[{"name":"ESSM","class":"Missile","ready":22,"total":32,"range":{"min":1500,"max":50000}},{"name":"CIWS 20mm","class":"Gun","ammo":1800,"turret":true,"range":{"max":2500}}],
 "sensors":{"radar":[{"name":"SPY","on":true,"locks":3,"maxLocks":8,"rangeM":120000}],"sonar":[],"rwr":true},
 "leases":[{"channel":"targeting","holder":"a:ad-coord"}],"orders":["o:412"],"fuel":0.78}
```

## 5. Streams, deltas and thresholds

```json
→ {"method":"stream.subscribe","params":{
     "source":{"tactical":{"filter":{"iff":["hostile","unknown"],"domain":["air","missile"],"within":{"ref":"g:sag-1","m":80000}},
                           "frame":{"type":"braa","ref":"v:3281937421"},
                           "fields":["id","iff","class","braa","threat.score","engagedBy","missilesInFlightAt"]}},
     "mode":"delta","rateHz":4,"encoding":"json","format":"compact",
     "thresholds":{"posM":200,"courseDeg":5,"speedMps":10,"threatScore":0.1},
     "events":["track.new","track.lost","track.iff","missile.launch","missile.inbound"]}}
← {"result":{"sub":"sub:14","effectiveRateHz":4,"seq":88213,"kind":"full","data":"..."}}
← {"method":"stream.data","params":{"sub":"sub:14","seq":88217,"ut":1204.2,"kind":"delta",
     "added":[...],"changed":[...],"removed":["t:103"],"events":[...],"degraded":false}}
```

Rules: the first message is `full`; afterwards `delta`s include only tracks whose selected fields crossed a threshold or whose membership in the filter changed; `removed` lists ids that left the filter or were dropped; `events` are inlined when requested so a subscriber needs one subscription, not two; `stream.modify` changes filter/frame/rate in place keeping the `sub` id; if the client falls behind (outbound queue > 2 MB for this sub) the host coalesces to the latest state and sets `kind:"resync"`.

Sampled streams (`mode:"sampled"`) and on-change streams for arbitrary `ksp.query` selectors behave as in spec 0.2 §6.5.

## 6. Compact text format (LLM-facing)

Design goals: one line per object, fixed column order declared once in a header, no JSON punctuation, naval or SI units, stable abbreviations. Example for the subscription above (`units:"naval"`):

```
#tac seq=88217 ut=1204.2 ref=DDG-1 frame=BRAA units=nm/kt/ft n=6 (+2 ~3 -1)
T117 HOS MSL ASM   B271 R17.2 A100 KT1190 HOT  thr.91 eng:DDG-1(2)   q.82 age0
T121 HOS MSL ASM   B268 R19.0 A120 KT1180 HOT  thr.88 eng:-          q.80 age0 NEW
T093 HOS AIR FTR   B015 R41.5 A22000 KT540 FLANK thr.44 eng:CG-2(1) q.70 age2
T130 UNK AIR ?     B340 R58.0 A3000 KT160 COLD thr.12 eng:-         q.35 age6 DR
-T103 lost (splashed, by DDG-1 ESSM)
!E missile.launch from T093 toward DDG-1 ut=1203.9
```

Grammar: `T<id> <IFF:FRD|HOS|NEU|UNK> <DOM:AIR|SUR|SUB|LND|MSL> <CLS> B<bearing> R<range> A<alt|D depth> KT<speed> <ASPECT:HOT|FLANK|BEAM|COLD|DRAG> thr<score> eng:<units(missiles)> q<quality> age<s> [NEW|DR|LOST]`. Headers declare the column set so the agent prompt can include the grammar once. Compact SITREP, orders and leases have analogous one-line forms (`08-tactical-picture.md` §7). The format is produced by `Skizzy.Protocol.Compact` so the bridge, host and SDKs agree byte-for-byte.

## 7. Events taxonomy

| Namespace | Events |
|---|---|
| `track.*` | `new`, `updated` (threshold), `lost`, `iff`, `class`, `merged`, `split` |
| `missile.*` | `launch` (source, target guess, kind), `inbound` (to own unit, tcpa), `defeated`, `impact`, `splash`, `timeout` |
| `engagement.*` | `start`, `end`, `assignment` (who engages whom), `saturation` (target at max missiles) |
| `unit.*` | `damaged`, `part_lost`, `flooding`, `foundered`, `destroyed`, `out_of_ammo`, `low_fuel`, `ai_status` (BDA status string changes), `rwr_ping`, `under_fire` |
| `order.*` | `accepted`, `executing`, `progress`, `achieved`, `failed`, `superseded`, `expired`, `claimed`, `orphaned` |
| `lease.*` | `acquired`, `released`, `preempted`, `expired`, `denied` |
| `c2.*` | `roe_changed`, `zone_changed`, `group_changed`, `doctrine_loaded`, `trigger_fired` |
| `bda.*` | `competition_started|ended`, `vessel_killed`, `team_changed`, `scores_updated`, `spawn_progress` |
| `sim.*` | `time_scale`, `paused`, `resumed`, `scene`, `scenario_loaded`, `batch_cell_done` |
| `rec.*` | `profile_started|stopped`, `chunk_written`, `mark` |
| `host.*` | `degraded`, `auto_disabled`, `kill_switch`, `budget_warning` |
| `human.*` | `took_controls` (pilot input detected), `released_controls`, `map_drawing` |

Every event carries `ut`, `frame`, `cursor`, optional `traceId`, and a `summary` string suitable for an LLM transcript.

## 8. Triggers

```json
→ {"method":"trigger.create","params":{"name":"raid-warning","when":{"all":[
      {"tracks":{"filter":{"iff":["hostile"],"domain":["missile","air"],"within":{"ref":"g:sag-1","m":60000}},"count":{"gte":4}}},
      {"not":{"orders":{"type":"defend_sector","status":["executing"],"recipient":"g:sag-1"}}}]},
   "emit":{"event":"c2.trigger_fired","payload":{"tactic":"ad_umbrella"}},"cooldownS":30,"once":false}}
```

Triggers are compiled to a small predicate tree evaluated by the C2 engine at 5 Hz against the current snapshot (no allocations after compile). Supported atoms: track counts by filter, distances between handles/zones, order status, lease status, unit status fields (hp, ammo, flood), time windows, custom expression over SITREP fields (`expr: "fuel < 0.3 and depthM < -50"`, a small arithmetic/comparison language compiled once).

## 9. Errors (additions to spec 0.2 §10)

| code | kind | data |
|---|---|---|
| -32020 | `LEASE_HELD` | `{vessel, channel, holder, precedence, preemptible, hint}` |
| -32021 | `ROE_VIOLATION` | `{rule, zone?, target?, hint}` |
| -32022 | `STALE_DECISION` | `{basedOnSeq, currentSeq, maxStaleSeq}` |
| -32023 | `BUDGET_EXHAUSTED` | `{actor, budget, spent}` (bridge/orchestrator side) |
| -32024 | `SLOW_CONSUMER` | sent as close reason |
| -32025 | `NOT_IN_VIEW` | track or vessel not visible to this session's team under fog |
| -32026 | `EXECUTOR_UNAVAILABLE` | `{orderType, reason}` (e.g. `needs_fork_patch`, `needs_script`) |

## 10. Versioning and compatibility

- Protocol `1.0` is the first stable version; `0.x` from PLAN is abandoned at M0 since nothing shipped. From 1.0, MINOR additive only.
- Capability versions `name@N`, provider semver, catalog revision: spec 0.2 §11.
- **Schema ids** for msgpack columnar frames: each columnar layout has an integer `layoutId`; the hello response lists supported layouts; a client that does not know a layout asks for JSON (`encoding:"json"`) for that stream. Layouts never change once published; new fields create a new layout id.
- **Recording files** embed the protocol version, layout ids and the catalog descriptors used, so a `.skzrec` from 1.0 remains readable by 1.x readers.
- **Compatibility matrix** is tested in CI: bridge 1.x against host 1.0 … 1.x; Python SDK pins `protocol>=1.0,<2`.
- **BDArmory hooks**: the adapter publishes `bda.hooks` with `{steering:true|false, targeting:…, fireAuthority:…, formation:…, waypoints:…, events:…, source:"fork"|"reflection"|"none"}` so clients and executors know which features are available on this install (`07-bdarmory-integration.md` §5).

## 11. MCP projection (changes from spec 0.2 §14)

- Transports: stdio (`skizzy mcp`) **and** Streamable HTTP on `127.0.0.1:47551` (`skizzy serve`), both loopback. HTTP sessions carry `X-Skizzy-Actor` (id/role/model) so each agent is a distinct actor at the host; stdio instances pass the actor on the command line (`--actor role=unit,id=a:ddg-1`).
- Toolsets: `core`, `tactical` (query/sitrep/subscribe-wait/threats/zones), `c2` (orders/leases/groups/report), `flight`, `bda`, `craft`, `rec`, `sim`, `naval`, `map`, `deep`. Default for a unit agent: `core+tactical+c2+flight` ≈ 28 tools; commander: `+bda+rec+sim+map` ≈ 60; builder: `core+craft+bda+rec`.
- **Event delivery to MCP agents**: MCP has no server-push the model can consume mid-turn, so the bridge offers `tac_wait_events {names, timeoutS, maxEvents}` (blocks up to N seconds, returns the batch in compact text) and `tac_subscribe_digest` (the orchestrator wakes the agent when a digest is ready). The orchestrator (`skizzy.agents`) uses the native socket for events and injects them as the next user turn; MCP tools are for the agent's own queries and actions.
- Tool list stability: tools never appear/disappear with scene or lease state (availability is returned in results), which keeps prompt caches warm.
- Images: screenshots and map renders are image content blocks (spec 0.2 §14.2).

## 12. Example: a unit agent's turn over MCP

```
tools/call tac_sitrep {}                               → compact SITREP, 12 lines
tools/call tac_query {filter:{iff:["hostile"],domain:["missile"]}, frame:{type:"braa"}, limit:8, format:"compact"}
tools/call c2_order_report {order:"o:413", status:"executing", note:"engaging 2 ASM, 1 more lock available"}
tools/call bda_configure_weapon_manager {vessel:"self", maxMissilesOnTarget:2, parallelFire:true, parallelSalvoSize:2}
tools/call tac_wait_events {names:["missile.inbound","order.*"], timeoutS:20}
```

Five calls, about 1.5 k input tokens of tool results, with the stable system prompt and tool list cached.
