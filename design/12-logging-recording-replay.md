# 12 Logging, recording, replay

"Log whatever I want, get it, save it." The **Recorder** engine in the host samples any selector at any rate into open files; the bridge exports, indexes and queries them; the map replays them; agents read summaries. Decision D7: host writes chunked MessagePack `.skzrec`; events and audit are JSONL; Parquet/CSV are export formats.

## 1. What can be logged

Any of: `ksp.query` selectors (any field of any module of any vessel), tactical snapshots (tracks per team, god or fog), SITREP fields, orders/leases/zones state, events (all namespaces), BDArmory scores and missiles in flight, `host.engines` performance, agent turns (from the orchestrator: tokens, cost, decision text, tool calls), scripts' stdout, map annotations, screenshots at low rate (P2, as `bin`). Sampling modes: `onChange` (with dead-band), fixed `rateHz` (0.1–50), `onEvent`.

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
retention: { keepDays: 60 }
```

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

## 4. Export and query (bridge, P0/P1)

- `skizzy rec export <rec> --format parquet|csv|jsonl --channels tracks,flood --from-ut --to-ut` (Parquet via Parquet.Net in the bridge; one table per channel; tracks columnar).
- `skizzy rec query <rec|folder> "select vessel, max(floodedDisplay) from flood group by vessel"`: a small SQL subset over the Parquet export using DuckDB (optional extra; falls back to a built-in filter/aggregate engine for the common cases: time window, vessel/team/event filters, downsample, min/max/avg/count). Python users get `skizzy.rec.open()` → pyarrow/pandas and can use DuckDB directly.
- `rec.query` is also an MCP tool so agents can ask "missiles per kill for DDG-1 in battle B" and receive compact text; results are capped and paginated.
- `skizzy rec compare runs/A runs/B` aligns matrix cells and reports deltas with bootstrap CIs (`14-testing-batch-sim.md` §6).

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
