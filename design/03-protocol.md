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
     "client":{"name":"skizzy-bridge","version":"1.0.0","kind":"bridge","mcpClient":"claude-code"},
     "actor":{"id":"a:blue-cmdr","kind":"llm","role":"commander","side":"Blue","display":"Blue Command"},
     "features":["ops","streams","events","enc.msgpack","confirm.client","lockstep"],
     "view":{"team":"Blue","fog":true},
     "resume":{"session":"s-7f3a","fromEventCursor":5531}}}
← {"jsonrpc":"2.0","id":1,"result":{
     "protocol":"skizzy-protocol","version":"1.0","session":"s-9c21","actor":"a:blue-cmdr",
     "features":["ops","streams","events","enc.msgpack","confirm.game","confirm.client"],
     "limits":{...},"catalogRevision":88,"areas":{...},"grants":{...},
     "precedence":800,"team":"Blue","fog":true,"seat":{"side":"Blue","holder":"a:blue-cmdr"},
     "meta":{"ut":1203.40,"frame":60170,"scene":"FLIGHT","paused":false,"timeScale":1.0,"warp":{"mode":"none","rate":1}}}}
```

- **[R]** `actor.kind` ∈ `human | llm | script | behaviour | harness` (who decides). `client.kind` ∈ `map | bridge | sdk | cli` (which process holds the socket); pass 1.0 mixed the two. **[R2]** `orchestrator` is gone with the built-in agent layers; `client.mcpClient` optionally names the AI tool behind the bridge (`claude-code`, `codex`, …) for the Connections window only. BDArmory is the implicit precedence-0 actor and never connects. **[R2]** `role` ∈ `commander | advisor | delegate | observer | script | behaviour | harness | human` (`15-security-permissions.md` §2): an `llm` actor connects as `commander` (it takes the seat if allowed, else it is an advisor), as `delegate` (with a delegation token, `09-c2-multi-agent.md` §1.2) or as `observer`. The hello result returns the effective role, precedence and seat. **Precedence** = the doctrine's value for the role (or the default band in `00-overview.md` §3), clamped by the permission file's `ROLE maxPrecedence` (`15-security-permissions.md` §2); the client may request a lower value in `hello`, never a higher one. The hello result returns the effective value.
- `view.team` selects the team whose picture this session sees under fog-of-war; `fog:false` requests god view, which requires the `tac.god` grant (`read` level but gated per role: delegates and advisors never get it; agent commanders only when the scenario or the permissions window allows).
- `resume` lets a reconnecting client keep its session id, event cursor and lease ownership (leases are tied to the actor id, not the socket; the TTL keeps running while disconnected).
- Every result and notification carries `meta` with `ut`, `frame`, `scene`, `paused`, `timeScale`, `warp`, `catalogRevision`, and `seq` for tactical data (the snapshot sequence number the payload was computed from).

## 3. Method families

| Family | Methods (abridged) | Affinity | Notes |
|---|---|---|---|
| `session.*` | `hello`, `bye`, `heartbeat`, **[R]** `pause_on` (pause-on-event settings) | any | heartbeat renews the actor's leases |
| `host.*` | `stats`, `state`, `selfcheck`, `selfcheck.perf`, `engines` (stream), **[R]** `sessions` (connected clients and actors, scripts' supervisor status), `kill_switch {engage\|release}` (release only from the in-game UI or a `human` actor), `doctor` (in-game half of `skizzy doctor`) | any/snapshot | |
| `catalog.*` | `get`, `search`, `describe` | any | |
| `cap.call` | generic call; all capabilities below are also callable through it | per capability | |
| `op.*` | `get`, `wait`, `cancel`, `list` | any | |
| `stream.*` | `subscribe`, `modify`, `unsubscribe`, `list` | any | |
| `events.*` | `subscribe`, `unsubscribe`, `poll` | any | ring buffer ≥ 8192 |
| `trigger.*` | `create`, `list`, `delete` | main | host-evaluated conditions → events |
| `ksp.*` | vessels, parts, modules, query, config, action groups, staging, time, scene, game save/load, log | main/fixed/snapshot | PLAN v0.2 §4.4 plus `ksp.camera.*` (incl. `screenshot`), `ksp.editor.*`, **[R]** `ksp.vessel.place`, `ksp.vessel.spawn_craft`, `ksp.vessel.rename`, `ksp.crew.list|transfer|board` (P2), `ksp.log.tail` |
| `craft.*`, `lib.*` | craft documents, blueprints, validation, trials, library | main/any | `05-craft-building.md` |
| `bda.*` | spawn, teams, WM, AI, weapons, radar, competition, settings, logs, hooks | main/fixed/snapshot | `07-bdarmory-integration.md` |
| `tac.*` | `query`, **[R]** `summary` (counts and the top threats in ≤ 6 lines), `sitrep`, `track.get`, `track.history`, **[R2]** `track.mark` (manual classification, `09-c2-multi-agent.md` §11.4), `zones.*`, `bullseye.*`, `threats`, `engagements`, `subscribe` (sugar over stream) | snapshot | `08-tactical-picture.md` |
| `c2.*` | `order.issue|amend|cancel|get|list|claim|report`, `lease.acquire|release|renew|list|grant|delegate`, `group.*`, `doctrine.load|patch|set|unset|effective`, `roe.*`, `tactic.run`, `wta.solve`, `plan.*`, `ask_human`, `resume`, `behaviour.attach|detach|list`, **[R]** `mission.create|assign|unassign|update|activate|pause|resume|cancel|get|list`, `losses`, `control.get|take|release|release_all` (`09-c2-multi-agent.md` §4.1), **[R2]** `seat.get|take|hand|release|brief` (§1.1), `delegate.create|list|update|revoke` (§1.2), `reports.config` (§1.3), `stance.get|propose|respond|declare`, `surrender`, `surrender.demand`, `violations` (`22-communications-diplomacy.md` §4–6) | main | `09-c2-multi-agent.md`, `10-scripting-sdk.md` §5 |
| `comms.*` **[R2]** | `send`, `inbox`, `wait`, `thread`, `tag`, `apply`, `diplo.open|close|status` | any/main | `22-communications-diplomacy.md` §9 |
| `flight.*` (alias `ksp.flight.*`) | `state`, `set_controls`, `hold`, `release`, `setpoint_batch`, `direct`, `route`, `formation`, `handoff`, `switch_vessel`, `depth`, `stage`, `action_group`, **[R]** `orbit_point` | fixed | `06-flight-control.md` |
| `rec.*` | `log.start|stop|list` (log v0, direct CSV/JSONL), `profile.list|get|save|delete|start|stop`, `mark`, `list`, `query` (host: basic; bridge: SQL), `get_file`, `export` (bridge), `append` (client notes, e.g. a commander's intent line) | main/any | `12-logging-recording-replay.md` |
| `sim.*` | `clock`, `time.set`, `time.slow`, `hold`, `step`, `release`, `scenario.load|run|snapshot|restore`, `batch.*` | main | `14-testing-batch-sim.md` |
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
{"id":"o:412","traceId":"tr-7f21","type":"defend_sector","issuer":"a:blue-cmdr","owner":"seat:Blue","recipient":"g:sag-1",
 "params":{"sector":{"center":"bullseye:bravo","fromDeg":300,"toDeg":60,"minM":0,"maxM":60000},"priorityTargets":["missile"]},
 "constraints":{"roe":"tight","zones":{"noFire":["z:harbour"]},"maxMissilesPerTrack":2},
 "priority":800,"validFromUt":1203.4,"validUntilUt":null,"basedOnSeq":88213,
 "status":"executing","statusReason":null,"progress":{"tracksEngaged":6,"tracksKilled":2},
 "claimedBy":null,"children":["o:413","o:414"],"createdUt":1203.4,"updatedUt":1209.0}
```

Status machine **[R]**: `pending → accepted → executing → achieved | failed | cancelled | superseded | expired`, plus the non-terminal `suspended` (`executing ⇄ suspended`, reasons `scene`, `human_override`, `borrowed`, `vessel_packed`, `kill_switch`, `parent_suspended`) which returns to `executing` on resume. **Orphaned is not a status**: when the owning actor's leases expire, the order keeps its status, `owner` becomes `null`, and an `order.orphaned` event offers it to the parent actor, which can `claim` it. Reasons are an enum plus text: `no_executor`, `lease_denied`, `roe_violation`, `target_lost`, `out_of_ammo`, `unreachable`, `unit_destroyed`, `scene_change`, `stale_decision`, `ai_inactive`, `not_controllable`.

### 4.3 Lease

```json
{"id":"l:91","vessel":"v:118822","channel":"steer","holder":"a:swarm-py","precedence":400,
 "acquiredUt":1200.0,"expiresUt":1215.0,"heartbeatS":5,"preemptible":true,"onExpire":"bda_guard"}
```

Channels: `steer`, `weapons`, `targeting`, `sensors`, `ai_params`, `formation`, `systems`. **[R]** `onExpire` ∈ `bda_ai | bda_guard | hold_last | neutral | keep` (one enum across the design; meanings in `02-architecture.md` §5, validity per channel in `09-c2-multi-agent.md` §4). See `09-c2-multi-agent.md` §4 for semantics.

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
 "leases":[{"channel":"targeting","holder":"seat:Blue"}],"orders":["o:412"],"fuel":0.78}
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
| `unit.*` | **[R2]** `surrendered`, `damaged`, `part_lost`, `flooding`, `foundered`, `destroyed`, `out_of_ammo`, `low_fuel`, `ai_status` (BDA status string changes), `rwr_ping`, `sonar_ping` (an active sonar ping received), `under_fire`, **[R]** `not_controllable` (`IsControllable` turned false) |
| `order.*` | `accepted`, `executing`, `progress`, `achieved`, `failed`, `cancelled`, `superseded`, `expired`, `suspended`, `resumed`, `claimed`, `orphaned` |
| `lease.*` | `acquired`, `released`, `preempted`, **[R]** `suspended`, `restored`, `expired`, `denied` |
| `c2.*` | `roe_changed`, `zone_changed`, `group_changed`, `doctrine_loaded`, `doctrine_changed`, `trigger_fired` |
| `seat.*` **[R2]** | `changed {side, from, to, by}`, `requested {side, by}` (agent asking a human holder), `vacant {side}` |
| `delegate.*` **[R2]** | `created`, `updated`, `revoked`, `expired` (idle TTL) |
| `comms.*` **[R2]** | `message`, `diplo_requested`, `diplo_opened`, `diplo_closed` (`22-communications-diplomacy.md` §9) |
| `stance.*` **[R2]** | `proposed`, `accepted`, `rejected`, `changed` |
| `violation.*` **[R2]** | `recorded {kind, shooter, target, weapon, firedUt}` |
| `control.*` **[R]** | `changed {unit, channel, from, to, by, reason}`, `reminder {unit, heldS}` (`09-c2-multi-agent.md` §4.1) |
| `bda.*` | `competition_started|ended`, `vessel_killed`, `team_changed`, `scores_updated`, `spawn_progress` |
| `vessel.*` **[R]** | `active_changed`, `loaded`, `unloaded`, `packed`, `unpacked`, `renamed`, `split` (parent, children), `pid_changed` (old, new persistentId), `destroyed` |
| `sim.*` | `time_scale`, `warp_changed`, `paused`, `resumed`, `scene`, `scenario_loaded`, `batch_cell_done` |
| `rec.*` | `log_started|stopped`, `profile_started|stopped`, `chunk_written`, `mark`, `disk_warning` |
| `host.*` | `degraded`, `auto_disabled`, `kill_switch`, `kill_switch_released`, `budget_warning`, `memory_warning` |
| `human.*` | `took_controls` (pilot input detected), `released_controls`, `map_drawing` |

Every event carries `ut`, `frame`, `cursor`, optional `traceId`, a `summary` string suitable for an LLM transcript, and **[R]** a `category` (`contact | weapons | unit | order | mission | c2 | comms | system`) for message-log filtering. **[R]** `mission.*` events: `created`, `activated`, `paused`, `phase`, `state`, `unit_assigned`, `unit_released`, `unit_suspended`, `unit_resumed`, `complete`, `failed` (`09-c2-multi-agent.md` §11.2, §4.1). **[R2]** Automatic reports (`09-c2-multi-agent.md` §1.3) are delivered as `comms.message` events with `kind:"report"`, not as a separate namespace.

## 8. Triggers

```json
→ {"method":"trigger.create","params":{"name":"raid-warning","when":{"all":[
      {"tracks":{"filter":{"iff":["hostile"],"domain":["missile","air"],"within":{"ref":"g:sag-1","m":60000}},"count":{"gte":4}}},
      {"not":{"orders":{"type":"defend_sector","status":["executing"],"recipient":"g:sag-1"}}}]},
   "emit":{"event":"c2.trigger_fired","payload":{"tactic":"ad_umbrella"}},"cooldownS":30,"once":false}}
```

Triggers are compiled to a small predicate tree evaluated by the C2 engine at 5 Hz against the current snapshot (no allocations after compile). Supported atoms: track counts by filter, distances between handles/zones, order status, lease status, unit status fields (hp, ammo, flood), time windows, custom expression over SITREP fields (`expr: "fuel < 0.3 and depthM < -50"`, a small arithmetic/comparison language compiled once).

## 9. Error taxonomy (unified) [R]

Pass 1.0 added codes on top of spec 0.2 §10 without saying how the two fit. This is the single list. Clients switch on `data.kind`, then on `data.reason` where present; `code` is the JSON-RPC number and never changes for a kind. Every error carries `data.hint` (one sentence telling an LLM or a script what to do next) and `data.retryable` (`no | yes | later`).

| code | `data.kind` | retryable | `data.reason` values and typical data |
|---|---|---|---|
| -32700 | `PARSE_ERROR` | no | invalid JSON or msgpack |
| -32600 | `INVALID_REQUEST` | no | not JSON-RPC 2.0, a batch, or a call before `session.hello` |
| -32601 | `METHOD_NOT_FOUND` | no | `{name, didYouMean?}` |
| -32602 | `INVALID_PARAMS` | no | `{errors:[{pointer, message}], candidates?}` |
| -32603 | `INTERNAL_ERROR` | later | host bug, logged with a trace id |
| -32001 | `UNAUTHENTICATED` | no | connection closed |
| -32002 | `PERMISSION_DENIED` | no | `{capability, mod, required, policy, source, role?, hint}`; reasons `policy`, `role_clamp`, `client_cap`, `god_view`, **[R2]** `out_of_scope` (delegate outside its cluster), `not_seat` (needs the command seat), `seat_held` (agent take-over declined) |
| -32003 | `CONFIRMATION_REQUIRED` | yes | `{confirmId, summary, level, expiresInMs, twoKey?}` |
| -32004 | `UNAVAILABLE_IN_SCENE` | later | `{scene, required[]}`; reasons `wrong_scene`, `transition` (scene load in progress) |
| -32005 | `NOT_FOUND` | no | `{handle}`; reasons `unknown`, `stale` (handle from before a scene reload or a persistentId change), `destroyed` |
| -32006 | `BUSY` | yes | `{retryAfterMs}` |
| -32007 | `TIMEOUT` | yes | `{timeoutMs, op?}` |
| -32008 | `CONFLICT` | no | `{detail}`; reasons `competition_running`, `guard_on`, `already_exists` |
| -32009 | `PRECONDITION_FAILED` | later | reasons **[R2]** `channel_not_open`, `no_link`, `surrendered`, `paused`, `vessel_packed`, `vessel_unloaded`, `not_controllable` (CommNet/RemoteTech/Kerbalism says no), `ai_inactive` (BDA `Command*` would silently do nothing), `bda_api_changed`, `api_missing`, `auto_disabled`, `event_inactive`, `too_large`, `warp_active`, `menu_open` |
| -32010 | `PROVIDER_ERROR` | later | exception in mod code: `{provider, exceptionType, message}` |
| -32011 | `CANCELLED` | no | reasons `client`, `scene_change`, `kill_switch`, `timeout`, `superseded` |
| -32012 | `RATE_LIMITED` | yes | `{retryAfterMs, limit}` |
| -32013 | `VERSION_UNSUPPORTED` | no | `{supported:["1.0"]}` |
| -32020 | `LEASE_HELD` | later | `{vessel, channel, holder, precedence, preemptible}` |
| -32021 | `ROE_VIOLATION` | no | `{rule, zone?, target?}`; **[R2]** rules `ceasefire`, `surrendered_target` (override with `acceptViolation:true`, which records a violation) |
| -32022 | `STALE_DECISION` | yes | `{basedOnSeq, currentSeq, maxStaleSeq}` |
| -32023 | `BUDGET_EXHAUSTED` | no | `{actor, budget, spent}`; **[R2]** raised only by the optional API-key example's spending cap (`09-c2-multi-agent.md` §8.5), never by the host |
| -32024 | `SLOW_CONSUMER` | — | close reason only |
| -32025 | `NOT_IN_VIEW` | no | track or vessel not visible to this session's team under fog |
| -32026 | `EXECUTOR_UNAVAILABLE` | no | `{orderType}`; reasons `needs_fork_patch`, `needs_script`, `needs_mod` |
| -32027 | `KILL_SWITCH_ACTIVE` | later | every non-read call while the kill switch is engaged; `{since, by}` |
| -32028 | `DUPLICATE_NAME` | no | spawn/rename with `uniqueName:"strict"` hit a name already in use; `{name, existing:[handles], suggestion}` (default mode renames automatically, `20-runtime-lifecycle.md` §6) |

Rules: a new failure mode gets a new `reason` on an existing kind unless clients must handle it differently from every existing kind; new kinds take the next free code from -32029 upward. The SDKs expose kinds as exception classes (`skizzy.errors.LeaseHeld`) and reasons as an enum attribute; the MCP bridge renders `kind: reason — hint` as the first line of the tool error text.

## 10. Versioning and compatibility

- Protocol `1.0` is the first stable version; `0.x` from PLAN is abandoned at M0 since nothing shipped. From 1.0, MINOR additive only.
- Capability versions `name@N`, provider semver, catalog revision: spec 0.2 §11.
- **Schema ids** for msgpack columnar frames: each columnar layout has an integer `layoutId`; the hello response lists supported layouts; a client that does not know a layout asks for JSON (`encoding:"json"`) for that stream. Layouts never change once published; new fields create a new layout id.
- **Recording files** embed the protocol version, layout ids and the catalog descriptors used, so a `.skzrec` from 1.0 remains readable by 1.x readers.
- **Compatibility matrix** is tested in CI: bridge 1.x against host 1.0 … 1.x; Python SDK pins `protocol>=1.0,<2`.
- **BDArmory hooks**: the adapter publishes `bda.hooks` with `{steering:true|false, targeting:…, fireAuthority:…, formation:…, waypoints:…, events:…, source:"fork"|"reflection"|"none"}` so clients and executors know which features are available on this install (`07-bdarmory-integration.md` §5).

## 11. MCP projection (changes from spec 0.2 §14)

- Transports: stdio (`skizzy mcp`) **and** Streamable HTTP on `127.0.0.1:47551` (`skizzy serve`), both loopback; any MCP client works (`09-c2-multi-agent.md` §8.1). HTTP sessions carry `X-Skizzy-Actor` (id/role/side or a delegation token) so each agent is a distinct actor at the host; stdio instances pass the actor on the command line (`--actor blue-cmdr --role commander --side Blue`, or `--actor-token <t>` for a delegate). **[R2]** With the `delegation` option, every tool accepts an optional `as` argument carrying a delegation token, so subagents sharing their parent's MCP connection act under their own identity (`09-c2-multi-agent.md` §1.2).
- Toolsets: `core`, `tactical` (query/summary/sitrep/subscribe-wait/threats/zones/mark), `c2` (orders/leases/groups/missions/doctrine/control/seat/delegate), **[R2]** `comms` (send/inbox/wait/tag, stance, surrender), `flight`, `bda`, `craft`, `rec`, `sim`, `naval`, `map`, `deep`. **[R2]** Presets: `commander` = `core+tactical+c2+comms+bda+rec+sim+map` ≈ 70 tools; `delegate` = `core+tactical+c2+flight+comms` (comms limited to team/direct) ≈ 35; `builder` = `core+craft+bda+rec+sim` (**[R]** `sim` added: the build loop runs `sim.scenario.run`). Pass 1's unit-agent preset is gone. `core` includes `ksp_read`, `ksp_query`, `ksp_camera_screenshot`, `rec_log_start|stop`, `host_kill_switch`, `catalog_search` and `cap_call`, so the M0 tool list already reads, logs and screenshots.
- **Output caps [R]** ("never a token dump", enforced by the bridge, not left to the agent): list-shaped tools default to `limit: 10` and refuse more than 50 rows per call; results carry `total`, `truncated` and an opaque `cursor` for the next page; the compact format is the default for tactical tools; a text result over 8 KB (configurable per toolset) is cut at a line boundary with a `…N more, use cursor` trailer. `tac_summary` is the recommended first call: counts by IFF/domain, the top 3 threats and own-force status in ≤ 6 lines.
- **Event delivery to MCP agents**: MCP has no server-push the model can consume mid-turn, so the bridge offers `tac_wait_events {names, timeoutS, maxEvents}` (blocks up to N seconds, returns the batch in compact text) and **[R2]** `comms_wait {channels, kinds, timeoutS}` for reports and messages. Pass 1's `tac_subscribe_digest` and orchestrator injection are gone with the built-in orchestrator. Messages from other sides come back marked `untrusted:true` with a provenance line (`22-communications-diplomacy.md` §10).
- Tool list stability: tools never appear/disappear with scene or lease state (availability is returned in results), which keeps prompt caches warm.
- Images: screenshots and map renders are image content blocks (spec 0.2 §14.2).

## 12. Example: a delegate's turn over MCP **[R2]**

A Claude Code subagent that the commander spawned for the ASW screen, sharing the parent's MCP connection:

```
tools/call comms_wait {channels:["team"], kinds:["report","text"], timeoutS:30, as:"skd_…"}   → "#rpt 1402.1 TEAM FFG-2 contact 1 SUB? B210 R? bearing-only"
tools/call tac_query {filter:{domain:["sub"]}, frame:{type:"braa", ref:"g:asw"}, limit:5, format:"compact", as:"skd_…"}
tools/call c2_order_issue {type:"tactic", name:"asw_prosecute", recipient:"g:asw", params:{track:"t:88"}, basedOnSeq:9120, as:"skd_…"}
tools/call comms_send {channel:"team", to:"seat", kind:"report", body:"ASW: prosecuting T88 with FFG-2/3; ETA datum 6 min", as:"skd_…"}
```

Four calls, about 1.2 k input tokens of tool results. Every call is attributed to `a:blue/asw` and refused outside `g:asw`.
