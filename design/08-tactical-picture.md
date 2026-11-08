# 08 Tactical picture: radar, IFF, data sharing

The Tactical Picture Engine (TPE) turns BDArmory's per-vessel sensor state into a per-team **track table** that every actor queries in the frame it needs, subscribes to for deltas, and records. It never dumps state: a query is filter + frame + fields + format + limit.

## 1. Sources and fusion

| Source | BDA data | Track effect |
|---|---|---|
| Own team truth | all vessels with a WM on `view.team` (friendly) | `iff:friend`, full state, `quality:1`, identity known |
| Radar/sonar/IRST detection (team-wide) | `TargetInfo.detected[team]` true | `sources += radar|sonar|irst` (kind from the detecting `ModuleRadar.sonarMode` / `ModuleIRST` when per-unit contacts are read; else `sensor`), position from the vessel (BDA detection is binary per team), `quality` from signal strength when available, `lastSeenUt = now` |
| Visual / recent contact | `TargetInfo.detectedTime[team]` within 30 s | `sources += visual` if no sensor flag; stale beyond `staleAfterS` → `dr:true` |
| Per-unit radar contacts | `VesselRadarData.displayedTargets` (hook F5 or reflection) | `lockedBy`, `signalStrength → quality`, `detectedBy` unit; jamming (`jammedIndex`) lowers quality |
| Per-unit IRST contacts | `displayedIRTargets` | `sources += irst`; if `!irstRanging` the track is **bearing-only** until another source ranges it |
| RWR | `OnRadarPing` events, `pingsData` | emitter tracks: bearing-only from the receiving unit, `class` from `RWRThreatTypes` (SAM, fighter, AWACS, torpedo, …), persist `persistTime` |
| Passive sonar | `ModuleRadar` with `sonarMode=passive` contacts | bearing-only, `domain:sub|surface` guess from depth if ranged |
| Datalink | BDA linked VRDs already merge contacts into the receiving VRD; the TPE additionally shares all per-team detections (BDA's team-wide `detected` flag is effectively a perfect datalink) | `sources += datalink` when the detecting unit is not the viewer; the optional comms model (§9) can delay or drop it |
| Missiles | `BDATargetManager.FiredMissiles`, `MissileBase` state | `domain:missile`, `class` from guidance (`ASM`, `AAM`, `SAM`, `torpedo`, `bomb`), `targetGuess` from `targetVessel`/`TargetPosition`, `inboundTo` |
| God view | every vessel | all of the above with `quality:1`, plus truth fields (`truth:{team,name}`) |

Fusion is by **vessel identity**: BDA already resolves which `Vessel` a contact belongs to, so there is no association problem for radar/visual tracks; one track per enemy vessel per team. Bearing-only RWR/passive sonar contacts are separate tracks until the TPE associates them with a positional track (bearing within `assocDeg` of an existing track from the same unit for ≥ 3 ticks) and merges (`track.merged` event). Decoys (`TargetSignatureData.isDecoy`) become tracks with `class:decoy` once identified.

## 2. IFF and identity

- `friend`: vessel on `view.team` or an allied team (`BDTeam.Allies`).
- `hostile`: `BDTeam.IsEnemy` for a vessel whose team is *known*. Under fog, the team is known when the track is classified from visual/IFF-bearing sources: radar-only tracks are `unknown` until within `idRangeM` (doctrine, default 40 km for air, 20 km for surface) or until the track behaves hostile (launches a missile at a friend, `engagement.assignment` from BDA: `TargetInfo.Team` is engaged by the enemy WM). God view and `fogIff:false` (a simpler mode) make IFF truthful immediately.
- `neutral`: team with `Neutral` flag.
- `pending`: a friend's launched missile before BDA assigns a `Team`.
- Identity: friendly tracks carry `name`/`vessel`; hostile tracks carry `identity.name` only when `revealNames` is on (default on for god view, off under fog; the agent sees `T117 HOS SUR DDG?` with a class guess).

## 3. Classification

`class` comes from the vessel classifier (`04-ksp-and-mod-access.md` §3.2) when identity is known; under fog it is inferred: domain from altitude/depth/situation (`isFlying`, `isSplashed`, `isUnderwater` on `TargetInfo`), size class from `TargetInfo.bounds` and RCS (`radarBaseSignature`), speed (missile vs aircraft vs ship), emitters (RWR type). Confidence is reported (`classConf`). Classes map 1:1 to map symbols (`11-tactical-map.md` §2).

## 4. Quality, age, dead reckoning, dropping

- `quality` ∈ [0,1]: 1 for truth; radar from normalised `signalStrength` vs lock curve; visual 0.7; datalink inherits × 0.95; bearing-only 0.3; decays linearly to 0 over `staleAfterS` (default 30 s, BDA's permanence window) once `lastSeenUt` stops advancing.
- Dead reckoning: position = last position + velocity × Δt (plus BDA's own stale-error model is not replicated); `dr:true`, `drErrorM` grows at `speed × Δt × 0.5`.
- Drop after `dropAfterS` (default 90 s) or on `onVesselDestroy` (becomes `state:dead` for 10 s then removed; `track.lost {reason: destroyed|faded|merged}`).
- Track ids are stable per (team, vessel) for the whole battle; a vessel seen, lost and re-acquired keeps its id (identity is by `Vessel`), which lets an agent say "re-engage T117" minutes later.

## 5. Frames

- `geo`: `{lat, lon, alt}` (alt negative = depth).
- `braa` (`ref` unit or point): bearing (true, from ref), range, altitude (or depth), aspect (`HOT|FLANK|BEAM|COLD|DRAG` from the angle between the track's velocity and the line of sight), closure speed, `tcpa`/`cpa`.
- `bullseye` (named point): bearing/range from the bullseye, altitude.
- `formation` (`ref` group): position in the group's course-aligned frame `{alongM, acrossM, upM}`; used by station keeping and screens.
- `local` (`ref` vessel): north/east/up metres (scripts).

Geometry runs on the network thread from the snapshot (`snapshot` affinity) using body-relative spherical math (great-circle bearing/range on `CelestialBody.Radius`, altitude difference), never Unity world coordinates, so Krakensbane is irrelevant to clients.

## 6. Queries

```json
{"method":"cap.call","params":{"name":"tac.query","args":{
   "filter":{"iff":["hostile","unknown"],"domain":["air","missile"],"class":["ASM","fighter"],
             "within":{"ref":"g:sag-1","m":80000},"zone":"z:mez-1","minThreat":0.3,"engagedBy":null,"inboundTo":"v:118822"},
   "frame":{"type":"braa","ref":"v:118822"},
   "sort":"threat","limit":10,
   "fields":["id","iff","class","braa","threat.score","engagedBy","missilesInFlightAt","quality","ageS"],
   "format":"compact","units":"naval"}}}
```

Other queries: `tac.sitrep {vessel|group|"self"}`, `tac.track.get {id, history?:{seconds, stepS}}`, `tac.threats {to: unit|group, limit}` (ranked with reasons), `tac.engagements {team}` (who engages whom, missiles per track, saturation), `tac.coverage {group}` (union of weapon/sensor envelopes vs threat axis), `tac.zones.list|create|update|delete`, `tac.bullseye.set|list`, `tac.intercept {shooter, track}` (DLZ + geometry: `inRange`, `timeToInRange`, bearing to steer), `tac.los {from, to}`.

Filter atoms: `iff`, `domain`, `class`, `team`, `within {ref|pos, m}`, `zone`, `bearing {from, deg, widthDeg}`, `altM {min,max}`, `speedMps`, `minThreat`, `engagedBy`, `inboundTo`, `sources`, `minQuality`, `maxAgeS`, `ids`, `nameLike` (friendly only). Combinable with `all/any/not`.

## 7. Compact text

Grammar in `03-protocol.md` §6. SITREP compact form:

```
#sitrep DDG-1 Kirov DDG Blue hp93% pos -0.150,-74.200 crs 090 kt 14 depth 0 fuel 78%
ai surface/Water on Free "Engaging"   wm guard on auth TIGHT tgt T117 sec 4 away 6 eng 3 inbound 2
wpn ESSM 22/32 r1.5-50km | SM-2 20/24 r5-120km | CIWS 1800rd r2.5km turret | chaff 12 | decoys 4
sns SPY on locks 3/8 r120km | sonar off | rwr on
flood 0t/820t inflow 0 leaks 0   leases targeting:a:ad-coord steer:bda   orders o:412(defend_sector executing)
```

Orders and leases: `o:412 defend_sector g:sag-1 by a:ad-coord EXECUTING prio700 eng6 kill2 valid→∞`.

## 8. Subscriptions and events

`stream.subscribe {source:{tactical:{filter, frame, fields}}, mode:"delta", rateHz, thresholds, events}` (`03-protocol.md` §5). Typical configurations:

| Consumer | Rate | Thresholds | Payload |
|---|---|---|---|
| Opus commander (via orchestrator digest) | 0.2 Hz digest + events | pos 1000 m, threat 0.2 | compact, top-20 by threat per group |
| Haiku unit agent | event-driven; `tac_wait_events` | missile.inbound, order.*, track.new within 60 km | compact |
| Python swarm script | 10–20 Hz | pos 20 m, course 2° | msgpack columnar (layout `tracks.v1`: parallel arrays id, lat, lon, alt, course, speed, iff, class, quality) |
| Map | 5 Hz | pos 5 m | msgpack full objects with history ring |
| Recorder | profile rate | none (full) | msgpack |

Events emitted by the TPE: `track.new|updated|lost|iff|class|merged|split`, `missile.launch|inbound|defeated|impact|splash|timeout`, `engagement.start|end|assignment|saturation`, `unit.rwr_ping|under_fire|damaged|flooding|destroyed`. `missile.inbound` includes `tcpaS`, `bearing`, `guidance` (from BDA's `IncomingMissile.guidanceType` when the hook exists, else from the missile's `TargetingMode`) and `suggestedCm` (chaff/flares/bubbles per BDA's own logic) so a unit agent can act in one step.

## 9. Comms, latency and EW model (P2) — [ADDED]

Default: perfect, instant team-wide sharing (matches BDA). Optional per-scenario model: a per-team link graph where each unit's contribution to the team picture is delayed by `latencyS`, dropped when the unit is beyond `linkRangeM` of any relay or when jammed (`VesselECMJInfo` on the unit's area), and bearing-only tracks never leave the detecting unit without a link. Orders to units without a link are queued (`order.pending {reason:"no_comms"}`). This produces realistic ASW/EW play without touching BDA.

## 10. Performance design

- `TrackRecord` is a struct of ~120 bytes (ids, floats, bitmasks, interned name index); `TrackTable` per team is a struct array with a free list; a `Dictionary<uint vesselPid, int slot>` per team for identity (allocated once, grows rarely).
- Tick: (1) walk `LoadedVessels` once, refresh positions/velocities of all friendly and god tracks; (2) per team, walk `TargetList(team)` and update detection flags; (3) walk `FiredMissiles`; (4) round-robin K units' VRDs for locks/quality; (5) threat scoring for tracks within the max weapon range of any friendly (bounded); (6) write deltas into buffer B; swap.
- Measured target: ≤ 1.0 ms per tick at 1000 vessels / 4 teams on the user's machine, 0 B steady-state allocation. Rate degrades (5 → 2 Hz) before the budget is exceeded; `degraded:true` on subscriptions.
- Snapshot consumers (queries, subs, recorder) never touch the game; per-subscriber delta state is a `seq` and a small per-track hash array.

## 11. Threat scoring

`threat.score = w_class·C + w_geo·G + w_intent·I + w_eng·E`, clipped to [0,1], where C is a class prior (ASM 0.9, torpedo 0.9, fighter 0.6, bomber 0.5, destroyer 0.5 to a ship, sub 0.7 to a ship …, doctrine-tunable), G from range vs the track's weapon envelope (known classes) and `tcpa`, I from aspect (`HOT`, closing speed, launch events), E from `engagedBy` count (already engaged lowers priority for *new* assignments but not for `threat`). Reasons are listed so an LLM can explain or override.

## 12. Acceptance (summary)

1. Fog view: a hostile ship beyond radar range is absent; it appears as `UNK SUR` on radar detection with `quality` from signal strength, becomes `HOS` on launch, and is dead-reckoned with `DR` after loss, dropped after 90 s.
2. BRAA from DDG-1 matches a hand computation within 1° / 50 m for a track at 30 km.
3. 40 inbound missiles: `tac.threats` ranks them by `tcpa`, `missile.inbound` events fire within one TPE tick of BDA's `missileIsIncoming`, compact output for top-10 is under 600 characters.
4. 1000-vessel god view: TPE tick ≤ 1.0 ms, 0 B/tick alloc after warm-up, subscription of a 50-track filter at 5 Hz costs ≤ 50 µs on the network thread.
5. Sonar-only: a submarine with passive sonar sees bearing-only tracks; active ping produces ranged tracks and an `unit.rwr_ping`-like `sonar_ping` event on the target's side.
