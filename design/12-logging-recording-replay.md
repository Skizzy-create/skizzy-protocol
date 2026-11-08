# 12 Logging, recording, replay

"Log whatever I want, get it, save it." The **Recorder** engine in the host samples any selector at any rate into open files; the bridge exports, indexes and queries them; the map replays them; agents read summaries. Decision D7: host writes chunked MessagePack `.skzrec`; events and audit are JSONL; Parquet/CSV are export formats.

## 1. What can be logged

Any of: `ksp.query` selectors (any field of any module of any vessel), tactical snapshots (tracks per team, god or fog), SITREP fields, orders/leases/zones state, events (all namespaces), BDArmory scores and missiles in flight, `host.engines` performance, agent turns (from the orchestrator: tokens, cost, decision text, tool calls), scripts' stdout, map annotations, screenshots at low rate (P2, as `bin`). Sampling modes: `onChange` (with dead-band), fixed `rateHz` (0.1–50), `onEvent`.

### 1.1 Log v0: direct CSV/JSONL, from M0 [R]

Pass 1.0 delivered logging only with the full recorder (then M3). The user's first request is "log whatever I want, get it, save it", so M0 ships a direct path that needs no profile file, no bridge and no `.skzrec` reader:

```
rec.log.start {name:"flood-test", select:{vessels:{nameLike:"DDG*"}, modules:{type:["ModuleHullBreach"]}},
               fields:["floodedDisplay","inflowDisplay"], rateHz:5 | onChange:{deadband:0.01},
               format:"csv"|"jsonl", aggregate:"perVessel"|"perModule", maxMB:200}
  → {log:"log:3", path:"Skizzy/logs/2026-11-08/flood-test.csv", columns:[ut, frame, wallUtc, vessel, vesselName, part, module, floodedDisplay, inflowDisplay]}
rec.log.stop {log} · rec.log.list · rec.log.tail {log, n:20, format:"compact"} · rec.get_file {path, offset, length}
```

- Any `ksp.query` selector and any field it can read (Tier 0, Tier 1 hidden properties, SITREP and tactical fields, `host.engines`). One header row; numbers in invariant culture; UT and frame on every row.
- Reachable from every front end: the in-game Recorder window has a **Log…** button with a picker (vessel → part → module → field checkboxes, rate) that builds the selector; MCP `rec_log_start|stop|tail`; CLI `skizzy log start flood-test --select … --fields … --hz 5`; Python `sk.log.start(...)`.
- "Get it": `rec.log.tail` returns the last rows as compact text (capped at 50 rows), `rec.get_file` streams the file in 1 MB `bin` chunks to any client, and the files are plain CSV/JSONL on disk for Excel or pandas.
- Cost: the same sampler as the recorder (shared per-tick query results), a pooled UTF-8 writer with an allocation-free number formatter, flush every second on the writer thread. Budget ≤ 0.2 ms per FixedUpdate for 100 vessels × 3 fields at 10 Hz.
- From M2, profiles (§2) can also declare `sink: csv|jsonl` per channel, so log v0 and the recorder share one implementation.

## 2. Log profiles (`library/logprofiles/*.yaml`, P0)

```yaml
profile: battle.standard
channels:
  tracks:   { source: tactical, view: god, rateHz: 5, fields: [id, team, iff, class, pos, vel, quality, engagedBy, missilesInFlightAt] }
  sitreps:  { source: sitrep, selector: { vessels: { team: ["Blue","Red"] } }, rateHz: 2 }
  flood:    { source: query, selector: { modules: { type: [ModuleHullBreach] } }, fields: [floodedDisplay, inflowDisplay, hullStatus], mode: onChange, deadband: 0.02, aggregate: perVessel }
  wm:       { source: query, selector: { modules: { type: [MissileFire] } }, fields: [guardMode, firedMissiles, engagedTargets, currentTarget, targetsAssigned.Count, missilesAssigned.Count], rateHz: 2 }
  missiles: { source: bda.missiles, rateHz: 10 }
  scores:   { source: bda.scores, mode: onChange }
  events:   { source: events, names: ["*"] }
  orders:   { source: c2, mode: onChange }
  perf:     { source: host.engines, rateHz: 1 }
  agents:   { source: agents, mode: onEvent }     # filled by the orchestrator through rec.append
```

**Retention [R]:** nothing is deleted automatically (pass 1.0 defaulted to 60-day auto-delete, which loses data the user may want for comparisons). `skizzy rec prune --older-than 60d [--keep-marked]` and the in-game recorder window delete on request; the host emits `rec.disk_warning` and shows a banner when `Skizzy/recordings` + `Skizzy/logs` exceed 20 GB (configurable).

Shipped profiles: `battle.standard` (above), **[R]** `battle.detailed` (tracks 10 Hz, missiles 20 Hz, for move-by-move replay of dense engagements, ≈ 330 KB/s at 200 vessels + 128 missiles, `11-tactical-map.md` §2.1), `perf.only`, `flight.basic`.

`rec.profile.start {profile, battleId?, tags}` begins a recording (`rec:<id>`), `rec.profile.stop`, `rec.mark {label, note}` inserts a marker, `rec.append {channel, record}` lets external actors add rows (agent turns, script notes). Profiles can be switched per battle from the map, scripts, agents or the in-game recorder window; the batch runner sets them from the scenario. Rates adapt downward under budget pressure and the file records the effective rate.

## 3. `.skzrec` format (P0)

```
file := header chunk, { data chunk | index chunk }, footer
header: { magic "SKZREC", version 1, protocolVersion, hostVersion, bdaVersion, battleId, profile (full YAML), startUt, startWallUtc, startFrame,
          channels: [{ id, name, source, layoutId?, schema (JSON Schema of a record) }] }
data chunk: { chunkSeq, channelId, utFrom, utTo, frameFrom, frameTo, wallUtcAtClose, count, codec: "raw"|"lz4", payload: bin }
            payload = msgpack array of records (objects or columnar arrays per layoutId)
index chunk: every 64 data chunks: [{ chunkSeq, channelId, utFrom, utTo, byteOffset }]
footer: { totalChunks, endUt, endWallUtc, indexOffsets[], marks[], scoresSummary, crc32 }
```

- Written by a background writer thread from pooled buffers; the main thread only serialises into the pooled buffer (bounded by the recorder budget). Chunks close on 1 MB or 5 s. `lz4` is optional (own tiny implementation or off; default off in-game, applied by `skizzy rec compact`).
- Crash-safe: a reader recovers everything up to the last complete chunk even without a footer (`skizzy rec repair`).
- Side files in the same folder: `events.jsonl` (also inside the recording for self-containment), `meta.json`, `scores.json` (BDA `ScoreData` dump at stop), `bda.log` (copied competition log), `audit-excerpt.jsonl`, `scenario.yaml` (if from a scenario), `screenshots/`.

## 4. Export and query (host P0, bridge P0/P1)

- **Host-side `rec.query` [R]** (P0, M2): `{rec|log, channel, fromUt?, toUt?, vessel?, team?, fields?, decimateS?, limit≤50, cursor?, format:"compact"|"json"|"msgpack"}` reads `.skzrec` chunks through the index and CSV/JSONL logs directly in the host on a background thread, so the map, agents and the in-game console retrieve logged data without the bridge. Aggregates: `min|max|avg|last|count` per vessel. The bridge adds SQL and Parquet on top.

- `skizzy rec export <rec> --format parquet|csv|jsonl --channels tracks,flood --from-ut --to-ut` (Parquet via Parquet.Net in the bridge; one table per channel; tracks columnar).
- `skizzy rec query <rec|folder> "select vessel, max(floodedDisplay) from flood group by vessel"`: a small SQL subset over the Parquet export using DuckDB (optional extra; falls back to a built-in filter/aggregate engine for the common cases: time window, vessel/team/event filters, downsample, min/max/avg/count). Python users get `skizzy.rec.open()` → pyarrow/pandas and can use DuckDB directly.
- `rec.query` is also an MCP tool so agents can ask "missiles per kill for DDG-1 in battle B" and receive compact text; results are capped and paginated.
- `skizzy rec compare runs/A runs/B` aligns matrix cells and reports deltas with bootstrap CIs (`14-testing-batch-sim.md` §5).
- **Training-episode export [R]** (P2, IDEAS 10.4): `skizzy rec export <rec> --format episodes --actor a:ddg-1` writes one row per decision: observation (the compact SITREP and picture the actor saw, by `basedOnSeq`), action (tool calls, orders, setpoints), outcome over the next N seconds and a doctrine-defined reward (kills, losses, missiles per kill, time in zone), as JSONL and Parquet. Scripts and BDA-only units produce episodes too (their actions are the orders and setpoints they issued), so supervised and RL datasets can be built from batch runs without LLM cost.

## 5. Metrics (computed by `skizzy rec metrics`, P1)

Per battle and per vessel/team: survival, kills by weapon, missiles fired / hits / kills (effectiveness), time to first detection/lock/launch, missiles per target over time (max, p95; the multi-target assertion), distinct targets engaged, retargets, salvo spread (Δt between launches vs `parallelSalvoStagger`), CIWS leakers, flood tonnes and time to founder, depth profile for subs, order latency (issue → executing → achieved), agent decision latency and cost, frame time p50/p99, physics steps per frame, GC collections, host budget usage, degraded time. All available as a Markdown summary (`summary.md`) and JSON.

## 6. Replay (P1)

`skizzy rec replay-server <rec> --speed 4 --from-ut 1200` serves the recording through the same stream API; the map connects to it as if to a live host (`sim://replay`), scrubs by seeking the index, and shows orders/annotations/agent decisions alongside tracks for after-action review. The orchestrator's analyst agent reads `summary.md` + selected `rec.query` results to write the AAR (P2) stored in `memory/`.

## 7. Audit (P0)

JSONL as PLAN v0.2 §6.7, extended with `actor`, `traceId`, `lease`, `order`, decision and `basedOnSeq`; readable as `skizzy://audit/recent` and in the recorder as `audit-excerpt.jsonl`.

## 8. Permissions and costs

Starting/stopping recordings is `control`; reading recordings is `read` (sandboxed to `Skizzy/recordings`). Budget: ≤ 0.5 ms per FixedUpdate for sampling at `battle.standard` on 200 vessels; the writer thread's disk rate at 200 vessels/5 Hz tracks ≈ 200 × 5 × ~90 B ≈ 90 KB/s plus channels ≈ 0.5–1 GB per hour-long battle uncompressed (lz4 halves it).

## 9. Acceptance (summary)

1. Profile with a 10 Hz `query` of 3 custom fields over 100 vessels records for 10 minutes with ≤ 0.3 ms per FixedUpdate and 0 B/tick alloc; `skizzy rec query` returns the max flooded value per vessel; Parquet export opens in pandas.
2. A recording interrupted by killing KSP is repaired and replays up to the last chunk.
3. Replay of a 20-minute battle in the map at 8× with scrubbing; marks and agent decisions appear on the timeline.
4. `rec.query` as an MCP tool returns compact text under 2 KB for "missiles per kill per ship".
5. **[R]** M0: from the in-game window, log `floodedDisplay` of every `ModuleHullBreach` on a ship at 5 Hz to CSV, stop, and open the file in Excel; the same via `skizzy log start` and via an MCP tool; `rec.log.tail` returns the last 20 rows; 10 minutes of logging shows no heap-slope increase beyond the gate (`02-architecture.md` §8.1).
6. **[R]** M2: host-side `rec.query` returns max flood per vessel from a `.skzrec` with the bridge not running; the map's recorder panel shows the same numbers.
