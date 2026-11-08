# 11 Tactical map

A live, planning-capable map with military symbology, for the human **and** for agents. Decision D6: an external C# desktop application (`skizzy-map.exe`, Avalonia 11 + SkiaSharp), fed by the same native msgpack stream everything else uses, plus a thin in-game overlay and a headless renderer. TypeScript/web was rejected by the user; Avalonia keeps one language, renders 1000+ symbols at 60 fps on a Skia canvas, and reuses `Skizzy.Protocol`/`Skizzy.Client` types directly.

## 1. Data

- Subscribes to the tactical stream (`mode:"delta"`, 5 Hz, msgpack, god or fog view per operator), events, orders, leases, zones, bullseyes, groups, shared annotations and `host.engines`.
- Keeps a `TrackMirror` plus a per-track history ring (default 10 min at 1 Hz downsampled) for trails.
- Replay mode reads `.skzrec` files through `skizzy rec replay-server` (same stream interface at variable speed) so live and replay share one code path.

## 2. Symbology

MIL-STD-2525D / APP-6D style, rendered procedurally (no font dependency):

| Track field | Symbol element |
|---|---|
| `iff` | frame shape and colour: friend blue rounded rectangle/circle, hostile red diamond, neutral green square, unknown yellow quatrefoil, pending dashed |
| `domain` | battle dimension: air (frame open at bottom), surface (circle), subsurface (frame open at top), land (rectangle), missile (air frame with missile modifier) |
| `class` | icon: fighter, bomber, rotary wing, UAV/drone, cruise/anti-ship missile, SAM, destroyer/frigate, cruiser, carrier, submarine, patrol boat, ground vehicle, static |
| velocity | speed leader of `speed × leaderSeconds` |
| `quality`/`dr` | frame opacity and a dashed frame for dead-reckoned tracks |
| `engagedBy`, `missilesInFlightAt` | engagement lines to shooters; badge with count |
| `threat.score` | optional halo intensity |
| text modifiers | track id, name (friendly / god), altitude or depth, speed, course, `NEW`/`LOST` |

Unit types added by mods map through the annotation packs (`symbol = …`), so a new class gets a symbol without a map release.

## 3. Layers (toggle, order, opacity)

Terrain/coastline (from KSP body data cached once by the bridge: a low-res `pqs` height sampling exported with `skizzy map export-terrain`, P2; until then a simple coastline from sea-level sampling), grid and range rings, tracks, trails, sensor coverage (radar/sonar ranges from `bda.get_radar_contacts` metadata, union per team), weapon envelopes (DLZ circles/sectors from `bda.dlz` and doctrine envelopes), engagement lines, missile tracks with predicted impact, zones (MEZ/FEZ/no-fire/patrol), bullseyes, orders (route lines, loiter circles, sector fans, status colour), leases (who owns what: small actor tags), groups (hull), threat axes (from `tac.threats`), annotations, performance/health strip.

## 4. Views

Fog-of-war (the operator's team) or god view (grant `tac.god`), multiple windows (two teams side by side for the user watching a Blue-vs-Red AI battle), follow mode (centre on a unit/group), BRAA readout from a selected reference, measurement tool, time controls (`sim.time.set`, slow-mo, lockstep step), kill switch.

## 5. Panels

Units (SITREP table, sortable), orders (tree by group with status), leases, agents (role, model, last turn, tokens, $ spent, budget bar, "freeze"/"wake" buttons), events log (filterable, compact text), recorder (profiles start/stop, marks), scenario (load/snapshot/waves), doctrine/ROE editor (profiles, zones, envelopes), craft library (spawn from library), host health (frame time, budgets, degraded flags, auto-disabled tools).

## 6. Planning tools (human → orders)

| Tool | Gesture | Produces |
|---|---|---|
| Route | click waypoints, double-click to end; drag to edit | `c2.order.issue type:move_route` for the selected unit/group; speed/altitude per leg in a side panel |
| Patrol area / loiter | drag box/circle | `patrol_area` / `loiter` |
| Sector | drag fan from a point | `tac.zones.create kind:sector` + optional `defend_sector` |
| No-fire / stay-in zone | polygon | `tac.zones.create` + ROE patch |
| Formation | pick template, drag slots | `flight.formation` |
| Assign target | drag from unit to track | `bda.assign_target` / `engage` with missile budget prompt |
| ROE | buttons hold/tight/free on selection | `weapons_*` orders |
| Lease | right-click → "take control", "give to agent X" | `c2.lease.acquire/grant` |
| Ask agent | text box to the commander agent | orchestrator message |
| Mark | M key | `rec.mark {label}` |

Every tool issues the same orders agents issue, with the operator actor at precedence 1000. Agent orders appear live with status and the issuing actor's tag; the operator can amend or cancel them (preemption rules apply).

## 7. Shared annotations (`map.annotations.*`, P1) — [ADDED]

Drawings (polylines, polygons, text, arrows), stored in the host so every map client and agent sees them (`tac.query` can return annotations by area; agents can `map.annotations.create` to point at things: "T117 group is a feint"). Persisted in recordings for AAR.

## 8. In-game overlay (P2)

An IMGUI window in BDA style showing a mini-map (top-down, north-up, 2525-lite glyphs drawn with `GUI.DrawTexture` and GL lines), the SITREP of the active vessel, inbound-missile BRAA text, order status and the kill switch; reads the same TPE snapshot without a socket. Deliberately minimal: the desktop map is the real tool.

## 9. Headless render for agents (`skizzy map render`, P2) — [ADDED]

Same Skia renderer, no window: `{center, scaleMPerPx, size, layers, view}` → PNG returned as an MCP image content block (`tac_map_image` tool). Used sparingly by commanders when geometry matters (routes vs zones); the compact text remains the primary channel.

## 10. Performance

Delta application is O(changed tracks); rendering uses one `SKPicture` per symbol class cached at zoom levels, batched draws, and culls off-screen tracks; target ≥ 60 fps at 1000 tracks, 5 Hz updates, on an integrated GPU; memory for a 60-minute battle with 1000 tracks and 1 Hz trails ≈ 1000 × 3600 × 24 B ≈ 86 MB (configurable trail length).

## 11. Acceptance (summary)

1. Two map windows (Blue fog, Red fog) and a god window update at 5 Hz for a 200-vessel battle with no dropped frames on the user's machine.
2. Drawing a 6-leg route for DDG-1 produces a `move_route` order that the destroyer executes; editing a waypoint during execution amends the order live.
3. Replay of a 20-minute recording scrubs smoothly; engagement lines and missile tracks match the live view.
4. An agent's `defend_sector` order shows as a fan with the agent tag within one event latency (≤ 300 ms).
