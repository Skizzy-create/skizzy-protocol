# 08 Tactical picture: radar, IFF, data sharing

The Tactical Picture Engine (TPE) turns BDArmory's per-vessel sensor state into a per-team **track table** that every actor queries in the frame it needs, subscribes to for deltas, and records. It never dumps state: a query is filter + frame + fields + format + limit.

## 1. Sources and fusion

| Source | BDA data | Track effect |
|---|---|---|
| Own team truth | all vessels with a WM on `view.team` (friendly) | `iff:friend`, full state, `quality:1`, identity known |
| Radar/sonar/IRST detection (team-wide) | `TargetInfo.detected[team]` true | `sources += radar|sonar|irst` (kind from the detecting `ModuleRadar.sonarMode` / `ModuleIRST` when per-unit contacts are read; else `sensor`), position from the vessel (BDA detection is binary per team), `quality` from signal strength when available, `lastSeenUt = now` |
| Visual / recent contact | `TargetInfo.detectedTime[team]` within 30 s (**[R]** `detectedTime` is Unity `Time.time`, so the age is `Time.time - detectedTime` scaled by the current time scale history; it is converted to UT with the per-tick `(Time.time, ut)` pair) | `sources += visual` if no sensor flag; stale beyond `staleAfterS` → `dr:true` |
| Per-unit radar contacts | `VesselRadarData.displayedTargets` (hook F5 or reflection) | `lockedBy`, `signalStrength → quality`, `detectedBy` unit; jamming (`jammedIndex`) lowers quality |
| Per-unit IRST contacts | `displayedIRTargets` | `sources += irst`; if `!irstRanging` the track is **bearing-only** until another source ranges it |
| RWR | `OnRadarPing` events, `pingsData` | emitter tracks: bearing-only from the receiving unit, `class` from `RWRThreatTypes` (SAM, fighter, AWACS, torpedo, …), persist `persistTime` |
| Passive sonar | `ModuleRadar` with `sonarMode=passive` contacts | bearing-only, `domain:sub|surface` guess from depth if ranged |
| Datalink | BDA linked VRDs already merge contacts into the receiving VRD; the TPE additionally shares all per-team detections (BDA's team-wide `detected` flag is effectively a perfect datalink) | `sources += datalink` when the detecting unit is not the viewer; the optional comms model (§9) can delay or drop it |
| Missiles | `BDATargetManager.FiredMissiles`, `MissileBase` state | `domain:missile`, `class` from guidance (`ASM`, `AAM`, `SAM`, `torpedo`, `bomb`), `targetGuess` from `targetVessel`/`TargetPosition`, `inboundTo` |
| God view | every vessel | all of the above with `quality:1`, plus truth fields (`truth:{team,name}`) |
| **[R]** Packed vessels (loaded, on rails) | `vessel.GetWorldPos3D()`, orbit velocity | positions keep updating; `state:packed`; BDA does not detect packed vessels, so under fog they age like lost contacts |
| **[R]** Unloaded vessels (outside load range) | `ProtoVessel` + `orbit` propagated at 1 Hz | **god view only** (`state:unloaded`, `quality:1`, no sensor sources); never visible under fog; cost bounded by the 1 Hz rate |
| **[R]** Debris | `VesselType.Debris`, or no command part and no WM after a split | ignored by default (no track, no events); `includeDebris:true` shows them as `class:debris` in god view; parent/child links come from `vessel.split` events (`20-runtime-lifecycle.md` §7) |

Fusion is by **vessel identity**: BDA already resolves which `Vessel` a contact belongs to, so there is no association problem for radar/visual tracks; one track per enemy vessel per team. Bearing-only RWR/passive sonar contacts are separate tracks until the TPE associates them with a positional track (bearing within `assocDeg` of an existing track from the same unit for ≥ 3 ticks) and merges (`track.merged` event). Decoys (`TargetSignatureData.isDecoy`) become tracks with `class:decoy` once identified.

## 2. IFF and identity

- `friend`: vessel on `view.team` or an allied team (`BDTeam.Allies`).
- `hostile`: `BDTeam.IsEnemy` for a vessel whose team is *known*. Under fog, the team is known when the track is classified from visual/IFF-bearing sources: radar-only tracks are `unknown` until within `idRangeM` (doctrine, default 40 km for air, 20 km for surface) or until the track behaves hostile (launches a missile at a friend, `engagement.assignment` from BDA: `TargetInfo.Team` is engaged by the enemy WM). God view and `fogIff:false` (a simpler mode) make IFF truthful immediately.
- `neutral`: team with `Neutral` flag.
- `pending`: a friend's launched missile before BDA assigns a `Team`.
- **[R] Identification state** (genre feature, `09-c2-multi-agent.md` §11.4): `idState` ∈ `detected | classified | identified`, advancing with sensor quality, range and time; `iff` stays `unknown` until `identified` (under fog).
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

Filter atoms: `iff`, `domain`, `class`, `team`, **[R]** `role` (friendly units by their group role from the order of battle, e.g. `escort`, `picket`, `cap`; hostile tracks by inferred role `striker|cap|awacs|picket|unknown`), `state` (`live|packed|unloaded|dead`), `within {ref|pos, m}`, `zone`, `bearing {from, deg, widthDeg}`, `altM {min,max}`, `speedMps`, `minThreat`, `engagedBy`, `inboundTo`, `sources`, `minQuality`, `maxAgeS`, `ids`, `nameLike` (friendly only). Combinable with `all/any/not`.

**Output caps and pagination [R].** `limit` defaults to 10 and is capped at 50 per call; results carry `total`, `truncated` and a `cursor` (valid for the snapshot `seq` it was issued on plus 30 s). Asking for more than 50 rows is an `INVALID_PARAMS` with a hint to filter or page. This is the mechanism behind "compact relative picture, never a token dump": the default answer to "what's out there" is a summary plus ten lines.

**`tac.summary {ref?, units?}` [R]** returns the whole picture in ≤ 6 lines, for the first call of a turn:

```
#sum seq=88217 ut=1204.2 view=Blue fog ref=DDG-1 units=nm
HOS 14 (AIR 4, MSL 8, SUR 2)  UNK 3  FRD 9 (2 dmg)  inbound to own: 6 (tcpa min 41s)
TOP T117 MSL ASM B271 R17.2 HOT tcpa41 eng:DDG-1(2) | T121 MSL ASM B268 R19.0 HOT unassigned | T093 AIR FTR B015 R41.5 FLANK
OWN ammo ESSM 22/32, SM2 8/16 | CIWS 1800 | flood 0 | leases: targeting seat:Blue
ORD o:412 defend_sector executing 6 eng / 2 killed
```

## 7. Compact text

Grammar in `03-protocol.md` §6. SITREP compact form:

```
#sitrep DDG-1 Kirov DDG Blue hp93% pos -0.150,-74.200 crs 090 kt 14 depth 0 fuel 78%
ai surface/Water on Free "Engaging"   wm guard on auth TIGHT tgt T117 sec 4 away 6 eng 3 inbound 2
wpn ESSM 22/32 r1.5-50km | SM-2 20/24 r5-120km | CIWS 1800rd r2.5km turret | chaff 12 | decoys 4
sns SPY on locks 3/8 r120km | sonar off | rwr on
flood 0t/820t inflow 0 leaks 0   ctl M:m3 H:w   leases targeting:seat:Blue steer:bda   orders o:412(defend_sector executing)
```

**[R]** `ctl` is the unit's control state (`09-c2-multi-agent.md` §4.1.1):

- `M:<mission>` means mission-controlled.
- `H:<channels>` means a human holds those channels.
- `P` means the human is flying the unit in KSP.
- `C` means the agent commander drives it directly, and `D:<delegate>` means a delegate holds it.
- The token is omitted when BDA alone is in control.

Agents use it to avoid issuing orders to units the human is micromanaging.

Orders and leases: `o:412 defend_sector g:sag-1 by seat:Blue EXECUTING prio800 eng6 kill2 valid→∞`.

## 8. Subscriptions and events

`stream.subscribe {source:{tactical:{filter, frame, fields}}, mode:"delta", rateHz, thresholds, events}` (`03-protocol.md` §5). Typical configurations:

| Consumer | Rate | Thresholds | Payload |
|---|---|---|---|
| **[R2]** Agent commander (any MCP client) | event-driven: `tac_wait_events` + `comms_wait` (reports); `tac_summary` on wake | pos 1000 m, threat 0.2 | compact, top-20 by threat per group |
| **[R2]** Delegate (client-spawned sub-agent) | event-driven, filtered to its cluster | missile.inbound, order.*, track.new within 60 km of the cluster | compact |
| Python swarm script | 10–20 Hz | pos 20 m, course 2° | msgpack columnar (layout `tracks.v1`: parallel arrays id, lat, lon, alt, course, speed, iff, class, quality) |
| Map | 5 Hz | pos 5 m | msgpack full objects with history ring |
| Recorder | profile rate | none (full) | msgpack |

Events emitted by the TPE: `track.new|updated|lost|iff|class|merged|split`, `missile.launch|inbound|defeated|impact|splash|timeout`, `engagement.start|end|assignment|saturation`, `unit.rwr_ping|under_fire|damaged|flooding|destroyed`. `missile.inbound` includes `tcpaS`, `bearing`, `guidance` (from BDA's `IncomingMissile.guidanceType` when the hook exists, else from the missile's `TargetingMode`) and `suggestedCm` (chaff/flares/bubbles per BDA's own logic) so a unit agent can act in one step.

## 9. Comms, latency and EW model (P2) — [ADDED]

Default: perfect, instant team-wide sharing (matches BDA). Optional per-scenario model: a per-team link graph where each unit's contribution to the team picture is delayed by `latencyS`, dropped when the unit is beyond `linkRangeM` of any relay or when jammed (`VesselECMJInfo` on the unit's area), and bearing-only tracks never leave the detecting unit without a link. Orders to units without a link are queued (`order.pending {reason:"no_comms"}`). This produces realistic ASW/EW play without touching BDA.

## 10. Performance design

- `TrackRecord` is a struct of ~120 bytes (ids, floats, bitmasks, interned name index); `TrackTable` per team is a struct array with a free list; a `Dictionary<uint vesselPid, int slot>` per team for identity (allocated once, grows rarely).
- Tick: (1) walk `LoadedVessels` once, refresh positions/velocities of all friendly and god tracks; (2) per team, walk `TargetList(team)` and update detection flags; (3) walk `FiredMissiles`; (4) round-robin K units' VRDs for locks/quality; (5) threat scoring for tracks within the max weapon range of any friendly (bounded); (6) write deltas into buffer B; swap.
- **[R] Time slicing.** The tick above is not run in one physics step. A full pass is divided into fixed slices spread over the physics steps of one period (at 5 Hz and 50 Hz physics, 10 steps): step 1 refreshes friendly/god positions for slice 1 of `LoadedVessels`, later steps continue the slices and the per-team detection walk, the last step scores threats, writes deltas and swaps. Consumers only ever see complete snapshots. Targets: amortised ≤ 0.25 ms per physics step and ≤ 1.0 ms in the worst step at 1000 tracked records / 4 teams, 0 B steady-state allocation (`02-architecture.md` §8). Rate degrades (5 → 2 Hz) before the budget is exceeded; `degraded:true` on subscriptions.
- **[R] BDA access costs.** `TargetList(team)` returns BDA's live `List<TargetInfo>` for the team (it allocates only the first time a team is seen); `TargetInfo.detected`/`detectedTime` are `Dictionary<BDTeam, …>` whose key hash is the team-name string hash, so the TPE walks each team's list once and reads only that team's entry, never all teams for every vessel. Lists are read on the main thread only and never stored past the tick.
- **[R] Loaded vs tracked.** Realistic battles have 50–200 *loaded* vessels; the 1000 figure counts tracked records including packed, unloaded and synthetic ones. The synthetic mode (`host.selfcheck.perf --synthetic 1000`, `14-testing-batch-sim.md` §3) drives the in-game TPE with injected sources to test the 1000-record budget inside KSP.
- Snapshot consumers (queries, subs, recorder) never touch the game; per-subscriber delta state is a `seq` and a small per-track hash array.

## 11. Threat scoring

`threat.score = w_class·C + w_geo·G + w_intent·I + w_eng·E`, clipped to [0,1], where C is a class prior (ASM 0.9, torpedo 0.9, fighter 0.6, bomber 0.5, destroyer 0.5 to a ship, sub 0.7 to a ship …, doctrine-tunable), G from range vs the track's weapon envelope (known classes) and `tcpa`, I from aspect (`HOT`, closing speed, launch events), E from `engagedBy` count (already engaged lowers priority for *new* assignments but not for `threat`). Reasons are listed so an LLM can explain or override.

## 12. Acceptance (summary)

1. Fog view: a hostile ship beyond radar range is absent; it appears as `UNK SUR` on radar detection with `quality` from signal strength, becomes `HOS` on launch, and is dead-reckoned with `DR` after loss, dropped after 90 s.
2. BRAA from DDG-1 matches a hand computation within 1° / 50 m for a track at 30 km.
3. 40 inbound missiles: `tac.threats` ranks them by `tcpa`, `missile.inbound` events fire within one TPE tick of BDA's `missileIsIncoming`, compact output for top-10 is under 600 characters.
4. 1000-record god view (synthetic mode in KSP and `Skizzy.Sim` in CI): TPE amortised ≤ 0.25 ms per physics step, worst step ≤ 1.0 ms, 0 B/tick alloc after warm-up (CI assert), subscription of a 50-track filter at 5 Hz costs ≤ 50 µs on the network thread.
6. **[R]** `tac.query` without `limit` returns 10 rows plus `total`/`cursor`; `limit: 200` is refused with a hint; `tac.summary` is ≤ 6 lines and ≤ 500 characters for a 40-missile raid.
7. **[R]** God view lists an unloaded vessel 200 km away with `state:unloaded`; fog view does not; debris from a destroyed ship produces no tracks unless `includeDebris:true`.
5. Sonar-only: a submarine with passive sonar sees bearing-only tracks; active ping produces ranged tracks and an `unit.rwr_ping`-like `sonar_ping` event on the target's side.
