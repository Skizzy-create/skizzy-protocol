# 11 Tactical map

A live, planning-capable map with military symbology, for the human **and** for agents. Decision D6: an external C# desktop application (`skizzy-map.exe`, Avalonia 12.1 + SkiaSharp 3.119; see the D6 update below), fed by the same native msgpack stream everything else uses, plus a thin in-game overlay and a headless renderer. TypeScript/web was rejected by the user; Avalonia keeps one language, renders 1000+ symbols at 60 fps on a Skia canvas, and reuses `Skizzy.Protocol`/`Skizzy.Client` types directly.

**D6 update (2026-11-08, render-core review).** Pin exact versions: Avalonia 12.1.5, SkiaSharp 3.119.4 and Dock.Avalonia 12.1.5 (MIT; meets the §5.1 check). Avalonia 11.3 is pinned to SkiaSharp 2.88.9; only one SkiaSharp version loads per process, and the renderer uses 3.x, so the host must be Avalonia 12. Avalonia 12 targets net8/net10. The in-game overlay is IMGUI and doesn't use Skia. Do not move to SkiaSharp 4.x until Avalonia does. One renderer per UI thread (it isn't thread-safe); layout currently runs on the render thread (about 1–2 ms at 2,000 tracks), and moving it to a worker is still open.

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

**`symbols.json` [R]** (pass 1.0 said "2525D style" without saying which symbol). The mapping from Skizzy classes to MIL-STD-2525D symbol identification codes (SIDC) lives in `GameData/SkizzyProtocol/Defaults/symbols.json`, overridable in `Skizzy/library/symbols.json`, and is shared by the map, the in-game overlay and the headless renderer. The renderer draws frames and icons procedurally from the symbol set + entity; the full 20-digit SIDC is also exported in recordings and GeoJSON exports so other tools can draw the same symbol.

| Skizzy `domain`/`class` | 2525D symbol set | Entity (icon) |
|---|---|---|
| air / fighter, bomber, attack, AWACS, tanker | 01 Air | military fixed wing, with fighter/bomber/attack/AEW/tanker modifiers |
| air / helicopter, VTOL | 01 Air | military rotary wing |
| air / drone | 01 Air | unmanned aircraft |
| missile / AAM, SAM, ASM, cruise | 02 Air missile | missile, with air-to-air / surface-to-air / air-to-surface / surface-to-surface modifiers |
| surface / carrier, cruiser, destroyer, frigate, corvette, patrol boat, amphibious, auxiliary | 30 Sea surface | military combatant: carrier, cruiser, destroyer, frigate, corvette, patrol; amphibious; auxiliary |
| sub / submarine | 35 Sea subsurface | military submarine (nuclear/conventional modifier when known) |
| sub / torpedo, decoy | 35 Sea subsurface | torpedo; decoy |
| land / ground vehicle, SAM site | 15 Land equipment | armoured vehicle; air-defence launcher |
| static / base, launch site | 20 Land installation | installation |
| unknown class | per domain | frame only, `?` text modifier |

Standard identity digits come from `iff`: pending 0, unknown 1, friend 3, neutral 4, hostile 6 (assumed friend 2 and suspect 5 are used when `classConf < 0.5`). The six-digit entity codes for each row are filled in `symbols.json` from the 2525D tables during the M3 symbology task and checked by a unit test against a list of reference SIDCs; this design does not quote them from memory. Each entry also carries the procedural drawing key, a short label for compact text (`DDG`, `FTR`, `ASM`) and an optional APP-6D name.

### 2.1 Clarity at scale (P0, M3) — [IDEAS, added and corrected by the user 2026-11-08]

The design problem: 40 jets and drones with 128 missiles over them must stay readable, **and every one of them must stay visible as itself**: where each missile goes, its altitude, the moment a flight splits to evade, intercepts and collisions as they happen. The renderer therefore never merges or hides tracks by default. Readability comes from sizing, level of detail, trails, labels and time control.

**Rule 1: individual tracks, always.** Every track that passes the user's filters is drawn as its own mark at its own position. Aggregation exists only as an optional **Counts view** (toggle per map window, off by default): either group glyphs such as `MSL ×128 → SAG-1` / `UAV ×20` replacing the individual marks, or, without replacing anything, a counts overlay listing numbers by type, team and status for the visible area. Switching it off returns to individual tracks immediately.

**Rule 2: density-aware, zoom-aware sizing that avoids overlap.** Symbol size is computed per track every render update (≤ 10 times a second, smoothed between updates):

```
desired  = rcsBase(track) × zoomFactor(zoom) × overrides(track)        // what the symbol "wants" to be
spacing  = screen distance to the nearest other visible track          // uniform screen grid, O(N)
allowed  = 0.85 × spacing                                              // symbols do not touch unless the tracks really are that close
size     = clamp(min(desired, allowed), dotPx, maxPx)                   // dotPx = 3 px at 100 % UI scale: never smaller, never hidden
tier     = size ≥ 16 px → full 2525 symbol (frame, icon, modifiers)
           8–16 px      → small symbol (frame shape and affiliation colour, no icon detail)
           < 8 px       → dot (affiliation colour; shape hint: circle friend, diamond hostile, square neutral, quatrefoil unknown)
```

- Zooming in grows symbols until either their desired size or the local spacing stops them; zooming out shrinks them the same way. Two tracks overlap on screen only when their true positions are closer than the dot size, i.e. when they really are that close.
- Sizes animate over ~150 ms and tier changes use ±10 % hysteresis, so symbols do not flicker as tracks move past each other.
- The local spacing is computed on a worker thread from a copy of the mirror (screen-space grid hash, O(N)); a 10 000-track picture costs about 1 ms per update.

**Rule 3: visual hierarchy by radar cross-section.** The default size mode is `rcs`: `rcsBase = s0 × (rcs / rcsRef)^0.25`, clamped between the missile floor and the carrier ceiling, so a carrier is large, a destroyer smaller, a fighter smaller still, a stealthy drone or a missile small but always at least a dot. Under fog the RCS is the TPE's estimate (radar signal strength and class prior, `08-tactical-picture.md` §3); in god view and for own units it is BDA's radar signature. Prominence follows the same order (outline weight, draw order: small-RCS marks are drawn on top so they are never covered). Alternative size modes, each still passing through Rule 2: `screen` (fixed size per class group), `physical` (vessel length or class nominal size), `true` (true scale on the map with a dot floor).

**Overrides:** `size × class[c] × team[t] × item[id] × (selected ? 1.25 : 1)` before Rule 2. Any single track, a type, a team or the current selection can be enlarged or shrunk (context menu, Ctrl+wheel on the selection, Layers panel); per-item overrides persist for the battle, stored with the layout keyed by vessel name.

**Rule 4: per-track readability.**
- *Altitude/depth:* compact in the label (`A22` = 22 000 ft or `A6.7` km per unit setting; `D150` for depth), plus a short coloured altitude tick on the symbol (colour ramp by altitude band, blue shades below the surface) so even dots carry altitude.
- *Motion:* short trails (default 10 s, 0–120 s) or velocity leaders (`speed × leaderSeconds`), per layer and per type; trails fade with age, so a flight splitting to evade is visible as diverging trails.
- *Missiles:* their own glyph (arrowhead along the velocity vector in the affiliation colour), optional trail, optional dashed line to the target (`targetGuess`), red emphasis when inbound to own units.
- *Events on the map:* intercept (`missile.defeated`), impact/hit (`missile.impact`, `unit.damaged`), kill (`unit.destroyed`, `bda.vessel_killed`) and splash are marked briefly with small burst glyphs at the event position that fade over 5 s (configurable); in replay they appear at their event time.
- *Labels:* priority selected > hostile above a threat threshold > own units > others; greedy placement in eight positions with **leader lines** when displaced; labels shorten (`T117 ASM A2 1190kt` → `T117` → none) and are culled when they cannot be placed. Label decluttering **never hides a symbol**.

**Filters and highlight:** a chip bar for type, team, IFF and threat level (presets "inbound only", "own forces", "air picture"), plus the full tactical filter grammar (`08-tactical-picture.md` §6). Filters are the only thing that removes marks, and a badge shows how many are filtered out. Hover shows a halo plus the track's engagement lines, its target and the missiles aimed at it; selection makes it persistent; box/lasso multi-select; selection groups on Ctrl+1…9; an optional `Focus` mode dims unrelated tracks (dims, never hides).

**Rule 5: time control for dense engagements.**
- *Replay:* speed 0.1×–16×, single-step to the next sample or the next event, scrub on a timeline with event markers, jump to an event, loop a time window. Positions between samples are interpolated (Hermite, with velocity) so motion is smooth at any speed.
- *Recording rate:* move-by-move missile replay needs data; the `battle.detailed` log profile records tracks at 10 Hz and missiles at 20 Hz (≈ 330 KB/s at 200 vessels + 128 missiles, about 1.2 GB per hour; `battle.standard` stays at 5/10 Hz).
- *Live:* the map can request 10–20 Hz updates for its window's area during a dense engagement (subscription `rateHz`), extrapolates positions with velocity between updates at render rate, and has a slow-motion button (`sim.time.slow`, control level) to watch the engagement live at 0.25×.

## 3. Layers (toggle, order, opacity)

Terrain/coastline (from KSP body data cached once by the bridge: a low-res `pqs` height sampling exported with `skizzy map export-terrain`, P2; until then a simple coastline from sea-level sampling), grid and range rings, tracks, trails, sensor coverage (radar/sonar ranges from `bda.get_radar_contacts` metadata, union per team), weapon envelopes (DLZ circles/sectors from `bda.dlz` and doctrine envelopes), engagement lines, missile tracks with predicted impact, zones (MEZ/FEZ/no-fire/patrol), bullseyes, orders (route lines, loiter circles, sector fans, status colour), leases (who owns what: small actor tags), groups (hull), threat axes (from `tac.threats`), annotations, performance/health strip.

## 4. Views

Fog-of-war (the operator's team) or god view (grant `tac.god`), multiple windows (two teams side by side for the user watching a Blue-vs-Red AI battle), follow mode (centre on a unit/group), BRAA readout from a selected reference, measurement tool, time controls (`sim.time.set`, slow-mo, lockstep step), kill switch.

### 4.1 Zoom, pan, fit (P0, M3) — [IDEAS, added 2026-11-08]

Smooth zoom on the mouse wheel and touchpad pinch, animated and anchored at the cursor; pan by drag, middle button or arrow keys; `F` = fit to selection, `Home` = fit all (own forces + tracks), `Shift+Home` = fit the current filter. Zoom range from a 2 km view to a whole-body view; the projection is local azimuthal-equidistant around the view centre, so bearings and ranges read correctly at any zoom.

## 5. Panels

**Command palette [R]** (Ctrl+K): searches the host catalog and opens a form generated from the capability's JSON Schema, the same component as the in-game console (`21-user-experience.md` §3), so every capability is usable from the map even before it has a dedicated panel; results and errors show in the events panel. Units (SITREP table, sortable), orders (tree by group with status), leases, **[R2]** actors (the side's seat holder with Take, Hand and Release; advisors; delegates with their cluster, last call and **Revoke**; scripts and behaviours; pass 1's per-agent model, token and budget columns are gone, and the API-key example's spending shows only when it reports it), comms (§5.2), missions (§12.7), losses (§12.4), events log (filterable, compact text), recorder (profiles start/stop, marks), scenario (load/snapshot/waves), doctrine/ROE editor (profiles, zones, envelopes), craft library (drag-spawn, §6), host health (frame time, budgets, degraded flags, auto-disabled tools).

### 5.1 Layout and docking (P0, M3) — [IDEAS, added 2026-11-08]

- The main window is freely resizable and remembers its size, position and monitor.
- Every panel (track list, unit details, orders, timeline, events/log, layers, **[R2]** actors, comms, missions, losses, leases, recorder, memos, host health) is dockable, movable, tabbable and detachable into its own window (multi-monitor). Map views are documents: several can be open (Blue fog, Red fog, god view) and tiled.
- Docking uses the MIT-licensed `Dock.Avalonia` library (layout model with serialisation); its Avalonia 11 compatibility and licence are checked at the start of M3, with a fallback of fixed splitters plus detachable windows if it does not fit.
- Layouts are saved per Windows user in `%APPDATA%/skizzy/map/layouts/<name>.json` (panels, sizes, open map views and their size mode, overrides, filters, layers), restored on start, with named layouts ("watch battle", "plan strike") and "reset to default".

### 5.2 Comms panel and command strip (P1: v1 M4, v2 M6, diplomatic M7) — [R2, IDEAS §15–16]

The AI client is the chat, so the map has no LLM chat. It has a **comms panel** for the side's channels (`22-communications-diplomacy.md`) and a **command strip** for the seat.

**Command strip** (top of the map window, always visible):

- **Seat.** "Blue: Claude Code (agent) since 14:02", with buttons **Take**, **Hand to…** and **Release**.
  - An agent asking for a human-held seat shows a prompt with Allow and Deny, declined after 30 s (`09-c2-multi-agent.md` §1.1).
  - A vacant seat shows in amber.
- **Stance chips** (M6). One chip per other side: `Red: WAR`, `Green: CEASEFIRE 12:40 left`, `Red·Surrendered ×3`. Clicking a chip opens the stance thread.
- **Comms state.** "Diplomatic: Red requested" when the other side has asked to open the channel; link quality when comms realism is on (M8).
- **Kill switch** and the time bar (§12.5).

**Comms panel** (dockable; tabs with unread counts):

| Tab | Contents | M |
|---|---|---|
| Team | the side's team channel: messages from the commander, advisors, delegates, scripts and humans | M4 |
| Reports | automatic reports (`09-c2-multi-agent.md` §1.3), grouped per group or mission, newest first; filter chips per kind (engaged, contact, weapons spent, damaged, kill, winchester, bingo/RTB, lost, surrendered) | M4 |
| Direct | one thread per counterpart (own commander agent, a delegate, the other seat while diplomatic is open) | M4 (own side), M7 (cross-side) |
| Broadcast | the open frequency; unauthenticated callsigns shown as `"Blue Command" (unauth)` | M6 |
| Diplomatic | the negotiation, one thread per topic, with the formal messages shown as cards | M7 |

- **Compose.**
  - A channel and addressee picker: seat, actor, delegate, group, unit, side, all. Only what the holder may use is offered.
  - Free text up to 2 KB.
  - Attachments by drag from the map: a track, a zone, a unit, a point or an order.
  - **Templates** (M6), with fields filled from the selection:
    - "Report status", "Hold fire", "Return to mission" (team);
    - "Warning: leave zone X within N minutes", "Surrender demand" (broadcast and diplomatic);
    - "Propose ceasefire N minutes with zones…", "Propose peace" (diplomatic).
- **Proposals as cards** (M6).
  - A stance proposal, demand or surrender offer shows its structured part: stance, duration, zones drawn on the map, action.
  - Buttons: **Accept**, **Reject**, **Counter** (opens the same card for editing).
  - Only the seat holder, or a human, can press them.
- **Order proposals from own advisors and delegates** show the order payload with **Apply** (`comms.apply`) and a map preview.
- **Credibility tags** (M7): per message, believe, doubt, false or unknown, with a note. They are private to the side, kept in the recording, and shown as a coloured edge on the message.
- **Every message** shows the time (UT and wall), sender, addressee, channel and authenticated or unauthenticated state.
  - Messages from other sides carry the `untrusted` mark.
  - Clicking a message pans to its attachments. In replay, it jumps to its UT.
- **Diplomatic handshake** (M7): **Open diplomatic channel with…** sends the request. An incoming request shows in the command strip and the tab, with Open and Ignore. Either side can close.
- **Visibility** follows `22-communications-diplomacy.md` §8. A fog window shows only its own side's channels plus broadcast and diplomatic. A god window shows all traffic with true senders and marks spoofed callsigns.
- **Talking to one's own agent.** A `direct` message to the seat (or to a delegate) is how the human talks to their commander agent from the map. The agent reads it with `comms_wait`. Users can just as well talk to it in their own AI tool.

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
| Take / return **[R]** | `T` or the toolbar button on the selection; right-click → Take control (default channels, or steer, weapons, targeting or sensors only), Return to mission, Return to BDA, Give to agent ▸, Fly in KSP; `R` returns the selection | `c2.control.take/release` (§6.1) |
| Lease | right-click → "lease channel…" for fine-grained cases | `c2.lease.acquire/grant` |
| Message | **[R2]** comms panel compose (§5.2): to the own commander agent, a delegate, or the other side | `comms.send` |
| Spawn **[R2]** | drag a craft from the library onto the map; spawn card with mode, altitude, depth, speed, heading arrow, count (any number; multi-spawn line that wraps into rows, with slot preview and a performance warning above 50) or Shift-drag for a formation | `ksp.vessel.spawn_craft`, `ksp.vessel.spawn_formation` (`06-flight-control.md` §9.1) |
| Mark | M key | `rec.mark {label}` |

Every tool issues the same orders agents issue, with the operator actor at precedence 1000. Agent orders appear live with status and the issuing actor's tag; the operator can amend or cancel them (preemption rules apply).

### 6.1 Mission-controlled or manual: control state, take-over and hand-back (P0, M4; missions M6) — [REVIEW-ADDED, user clarification 2026-11-08]

The user commands at mission level and drops to direct control of any unit or group at any moment, then hands it back. The C2 side is in `09-c2-multi-agent.md` §4.1. On the map the switch takes one click, and the current state is always visible.

**Seeing who is in control.**

- **Control badge.** A small badge sits at the upper-right of every own unit's symbol. It never replaces the 2525 symbol or its affiliation colour.

  | Badge | State |
  |---|---|
  | none | BDA AI and guard (the quiet default) |
  | `M` + the mission's colour tag | mission-controlled |
  | `O` | explicit standing order |
  | `C` / `D` / `S` | **[R2]** the commander agent / a delegate (with its name on hover) / a script or behaviour |
  | `H` | manual: a human holds channels |
  | stick icon | flown by the human in KSP |

- **Partial take-over.** It shows `H` with small channel ticks (s, w, t, e for steer, weapons, targeting, emitters).
- **Halo.** Units under manual control also get a bright halo, so the user sees at a glance which ships they are micromanaging, even at dot size (§2.1). The halo follows the density tiers and never hides other symbols.
- **Groups.** A group's hull label carries its mixed counts (`SAG-1 · M3 H1`).
- **Mission state.**
  - The unit details panel has one row per channel: controller, actor, since, the order or mission behind it, and a resume target.
  - The Missions panel (M6) lists each mission's units as `active`, `suspended (manual)` or `suspended (order)`.
  - Mission participation is drawn as a thin line from the unit to its mission's area or reference point while the unit is selected.
- **Filters.** Filter chips `manual`, `mission: <name>`, `agent`, `BDA` select or dim by control state.
- **Manual units panel.** Lists every unit the user holds, with the channels, the time held, a Return button per unit and **Return all**.

**Taking over and handing back (one click each).**

- **Take control.** `T`, the toolbar button or the context menu acts on the selection: a unit, several units, or a whole group by clicking its hull. The default channels are steer, weapons and targeting. The context menu also offers weapons only (the mission keeps steering, the user controls fire), steer only, and all channels.
  - Nothing moves until the user commands (`09-c2-multi-agent.md` §4.1.2): course and speed are held, and fire authority and locks stay as they were.
- **Return to mission.** `R` or the button restores the suspended mission or order, which re-plans from the unit's current position. The unit's planned rejoin path previews as a dashed line for 3 s.
  - **Return to BDA** drops the mission for that unit.
  - **Give to agent ▸** hands it to an agent or script.
- **One-shot interventions need no Take click.** These direct gestures work on a mission unit straight away:
  - drawing a route;
  - dragging "assign target" to a track;
  - firing a specific weapon from the unit details magazine view;
  - setting hold fire.

  The map shows a toast: "DDG-2 left Patrol North for this order. It returns when done. [Return now] [Keep manual]". **Keep manual** turns the intervention into a sticky take-over.
- **Fly in KSP.** The button on the unit details panel or the context menu switches the active vessel in KSP and gives the user the stick. The map badge turns to the stick icon. The in-game Flight window shows **Return to mission**.
- **Agents.** Agent and script control is shown the same way. When the user takes a unit from an agent, the agent is told (`lease.preempted`, reason `human_override`); on release the agent's control returns.
- **Reminder.** A unit held manually with no command for 5 min shows a gentle reminder on its badge and in the Manual units panel. Nothing is released automatically.

The same take and return actions are available in the in-game overlay v2 and the in-game Flight window. They are also reachable from Claude Code, the CLI and scripts (`skizzy call c2.control.take units=DDG-2`).

## 7. Shared annotations (`map.annotations.*`, P1) — [ADDED]

Drawings (polylines, polygons, text, arrows) and **memos** (notes and labels pinned to a track, a vessel or a map point; they follow a moving track, show as a pin that expands to the note, and are listed in the Memos panel; visibility `me|team|all`; IDEAS addition 2026-11-08, P1 in M4), stored in the host so every map client and agent sees them (`tac.query` can return annotations by area; agents can `map.annotations.create` to point at things: "T117 group is a feint"). Persisted in recordings for AAR.

## 8. In-game overlay (P1, v1 in M3) [R]

**[R]** Raised from P2: IDEAS asks for the overlay alongside the external map, and the user plays in the KSP window. v1 (M3): own-team symbols and hostile tracks from the TPE snapshot on a north-up mini-map, BRAA of the selected track, inbound-missile list, kill switch. v2 (M6): orders, zones, lease tags. An IMGUI window in BDA style showing a mini-map (top-down, north-up, 2525-lite glyphs drawn with `GUI.DrawTexture` and GL lines), the SITREP of the active vessel, inbound-missile BRAA text, order status and the kill switch; reads the same TPE snapshot without a socket. Deliberately minimal: the desktop map is the real tool.

## 9. Headless render for agents (`skizzy map render`, P2) — [ADDED]

Same Skia renderer, no window: `{center, scaleMPerPx, size, layers, view}` → PNG returned as an MCP image content block (`tac_map_image` tool). Used sparingly by commanders when geometry matters (routes vs zones); the compact text remains the primary channel.

## 10. Performance

Delta application is O(changed tracks); rendering uses one `SKPicture` per symbol class cached at zoom levels, batched draws, and culls off-screen tracks. **[R]** For the "thousands of symbols" requirement, symbols are pre-rendered into a sprite atlas (per class, IFF and a few sizes) and drawn with one `SKCanvas.DrawAtlas` call per layer, on Skia's GPU backend; density-aware sizing (nearest-neighbour spacing) and label placement run on a worker thread on a copy of the mirror (§2.1, `02-architecture.md` §2.4) and hand the result to the render thread; trails are drawn as batched GPU line strips. Targets: 60 fps with 5000 individually drawn symbols (1000 tracks + 2000 missiles + trails and labels) on the user's GPU, ≥ 60 fps at 1000 tracks on an integrated GPU, render ≤ 8 ms per frame, 5 Hz updates; memory for a 60-minute battle with 1000 tracks and 1 Hz trails ≈ 1000 × 3600 × 24 B ≈ 86 MB (configurable trail length).

## 11. Acceptance (summary)

1. Two map windows (Blue fog, Red fog) and a god window update at 5 Hz for a 200-vessel battle with no dropped frames on the user's machine.
2. Drawing a 6-leg route for DDG-1 produces a `move_route` order that the destroyer executes; editing a waypoint during execution amends the order live.
3. Replay of a 20-minute recording scrubs smoothly; engagement lines and missile tracks match the live view.
4. An agent's `defend_sector` order shows as a fan with the agent tag within one event latency (≤ 300 ms).
5. **[R]** Every class in `symbols.json` renders with the right frame for each IFF value (snapshot test of 4 × N symbols); the SIDC unit test passes; editing `symbols.json` changes a symbol without rebuilding the map.
6. **[R]** The command palette finds `bda.set_guard_mode`, renders its form from the schema, and the call succeeds against the live host.
7. **[R]** The in-game overlay v1 shows the same hostile tracks as the map's fog view for the active vessel's team and costs ≤ 0.3 ms/frame.
8. **Clarity at scale (2026-11-08, corrected):** a synthetic raid of 40 aircraft and 128 missiles over a 3-ship SAG renders at ≥ 60 fps with **all 168 tracks individually visible** at every zoom (counted in a render test: no track hidden or merged unless the Counts view is on); zooming out moves marks through full symbol → small symbol → dot, and two marks overlap only when their true positions are within the dot size; a carrier is drawn larger than a fighter and a missile smaller but never below the dot size; a flight of four splitting to evade shows four diverging trails; intercept and kill events show burst markers for 5 s; labels never cover each other and never remove a symbol; the Counts view toggles to `MSL ×128 → SAG-1` and back; a 5000-symbol synthetic picture holds 60 fps.
9. **Layout (2026-11-08):** panels dock, tab, float to a second monitor and come back; closing and reopening the map restores the layout, map views, size modes and filters; wheel/pinch zoom is smooth; `F`/`Home` fit selection/all.
10. **Memos (2026-11-08, M4):** a memo pinned to a moving track follows it, is visible to the team, appears in the recording and is readable by an agent as data.
11. **Replay and slow motion (2026-11-08):** a recording made with `battle.detailed` replays at 0.25× with smooth interpolated motion, single-steps event by event, and loops a 20-second window; live slow motion from the map button sets the time scale to 0.25 and back.
12. **Take-over and hand-back (2026-11-08, M4; missions M6):**
    - Selecting a destroyer on a patrol and pressing `T` turns its badge to `H` with a halo within one event latency (≤ 300 ms). The ship holds course, and its lock is kept.
    - A drawn route and hold fire take effect.
    - `R` shows the rejoin preview and the badge returns to `M`.
    - Weapons-only take-over shows `H` with a `w` tick, and the ship keeps following the mission.
    - The Manual units panel lists exactly the units the user holds, and **Return all** empties it.
    - "Fly in KSP" switches the active vessel and shows the stick icon.
13. **[R2] Comms panel and command strip (M4 v1, M6 v2, M7 diplomatic).**
    - A delegate's team message and an automatic `kill` report appear in the Team and Reports tabs within 300 ms, and clicking the report pans to the unit.
    - The human's direct message to the seat reaches the commander agent's `comms_wait`.
    - **Take** on the strip moves the seat from the agent to the human, and the badge on agent-controlled units changes from `C` to `H` without any order being re-issued.
    - (M6) A ceasefire proposal card shows its zones on the map. **Accept** changes the stance chip, and the countdown runs.
    - (M7) A spoofed broadcast callsign shows as unauthenticated in the Blue fog window and with its true sender in the god window.
14. **[R2] Drag-spawn (M3).** Dragging a fighter with count 30, 3 km and 250 m/s shows a slot preview that wraps into two rows and moves clear of a parked vessel. Count 60 shows the warning and still spawns, and the spawned aircraft fly at the set speed.
15. **[R2] CMO-style specs (M3–M6).**
    - Range rings for a selected destroyer match `bda.dlz` and radar ranges within 2 %.
    - The magazine view matches `bda.ammo` after each salvo.
    - The losses panel matches `c2.losses`.
    - A scenario built in the editor (two sides, an agent seat, a 20-drone mid-air wave, a ceasefire start, a `stance_held` end condition) validates, saves, reloads identically and runs.

## 12. Genre reference: CMO-style command-screen features — [REVIEW-ADDED]

The user's reference for the command screen is the genre of *Command: Modern Operations* (naval/air wargame). It is used only as a checklist of what a serious C2 screen needs; Skizzy does not copy its UI, art or layout. Coverage of each genre-standard feature on the map side (the C2 side is in `09-c2-multi-agent.md` §11):

| Genre feature | Skizzy design | Pri | M |
|---|---|---|---|
| Symbology: MIL-STD-2525 | §2 (`symbols.json`, procedural 2525D) | P0 | M3 |
| Symbology: NTDS option | alternative symbol style (NTDS shapes: friend circle/arc, hostile diamond/caret, unknown square, by domain), chosen per map window; same `symbols.json` with an `ntds` key | P2 | M8 |
| Posture colours (friendly/hostile/neutral/unknown) | 2525 affiliation colours from `iff` (§2); side posture from BDA team relations; **[R2]** stance (war, ceasefire, peace) and the surrendered white-flag modifier from `22-communications-diplomacy.md` §4–5, shown in the command strip (§5.2) | P0 | M3 (M6 stance) |
| Track uncertainty for passive/bearing-only contacts | bearing-only tracks drawn as a bearing line or wedge from the detecting unit (width = bearing error); dead-reckoned and passively ranged tracks drawn with an uncertainty ellipse from `drErrorM`; both fade with age | P1 | M3 (DR ellipses), M8 (passive sonar wedges) |
| Coverage display: sensor and weapon range rings, radar/sonar coverage | §3 layers (sensor coverage, weapon envelopes/DLZ), per unit or union per side; **[R2]** specified in §12.1 | P1 | M3 |
| Emission state per unit | small badges on own-unit symbols: radar on/off and lock count, sonar active/passive/off, jammer on; an EMCON layer colouring units by emission state | P1 | M3 (badges), M4 (EMCON layer with the executor) |
| Reference points and zones | §6 planning tools: named reference points (fixed or relative to a unit, e.g. a screen station astern of the carrier), zones built from reference points, zone roles `patrol`, `prosecution`, `exclusion`, `no_fire`, `mez`, `fez`, `jez`, bullseye | P1 | M4 (zones from M2 data, drawing in M3) |
| Message log with filters | events panel becomes a **message log**: category chips (contact new/lost/classified/identified, weapon fired, hit, miss, unit damaged, unit lost, orders/missions, leases, system), side filter, text search; clicking a message pans to the position and, in replay, jumps to the time; **[R2]** reports and comms join it in M4; specified in §12.2 | P0 | M3 |
| Losses and expenditures report; post-battle scorecard | live **Losses & Expenditures** panel per side (units lost by class, weapons expended by type, hits/misses, kills by weapon) from BDA scores and Skizzy events; post-battle **scorecard** view from `rec metrics` (exchange ratio, missiles per kill, mission outcomes, survival); **[R2]** subdued and surrendered units listed separately; specified in §12.4 | P1 | M3 (live panel), M5 (scorecard) |
| Time compression controls and pause-on-event | time bar: pause, 0.25×, 0.5×, 1×, 2×, 4× (BDA time scale, `sim.time.set`), lockstep step (M7); **pause-on-event** toggles: new hostile/unknown contact, missile launched at own units, own unit lost, own weapon fired, mission state change → `sim.hold` (or slow motion) with a banner naming the event; **[R2]** defaults per seat kind and the think-time banner, specified in §12.5 | P0 | M3 |
| Loadout/magazine view per unit | unit details panel: per weapon ready/total and reload state, countermeasures left, fuel, damage, flooding (from SITREP and `bda.ammo`), with "winchester" and "bingo" markers from doctrine; **[R2]** specified in §12.3 | P0 | M3 |
| Contact classification flow (unknown → classified → identified) | TPE `idState` (`08-tactical-picture.md` §2): the symbol shows frame-only for detected, icon with a `?` modifier for classified, full symbol and name for identified; **[R2]** manual marks, §12.6 | P1 | M3 (display; TPE side in M2) |
| Side and scenario editor | map **Scenario editor** mode: place units from the craft library by drag, set side/team, side posture, doctrine at side/group/unit level, missions, waves; save as a scenario YAML (`sim.scenario.save`) that the batch runner and director load; **[R2]** spawn specs with any count, rows and mid-air starts, respawn rules, seats and `agentTakeover`, initial stance, comms settings and the negotiated end conditions, specified in §12.8 | P1 | M6 |
| Missions as first-class objects | Missions panel: create, assign units by drag, see status (`09-c2-multi-agent.md` §11.2); **[R2]** specified in §12.7 | P1 | M6 |
| Doctrine and ROE with inheritance; EMCON | **[R2]** doctrine, ROE and EMCON editor with the side → mission → group → unit tree and locks, §12.9 (`09-c2-multi-agent.md` §11.1, §11.3) | P1 | M4 (basic), M6 (full) |
| Message traffic between sides | **[R2]** beyond the genre: comms panel with broadcast and diplomatic channels, proposals and credibility tags (§5.2, `22-communications-diplomacy.md`) | P1/P2 | M6/M7 |
| Mission command alongside direct control **[R]** | not a choice between the two: every unit shows whether it is mission-controlled or manual, and the user can take any unit or group with one click, micromanage it, and return it to its mission (§6.1, `09-c2-multi-agent.md` §4.1) | P0 | M4 (orders), M6 (missions) |

**[R2] Why the specs below are fuller now.** With no built-in agent layers (`09-c2-multi-agent.md` §0), the human or the single commander agent runs the force through these features, so each one is specified as a usable tool, not only as a checklist row. Each feature reads the same host data that agents get (`c2.*`, `tac.*`, `rec.*`), so whatever the map shows, an agent can query as compact text.

### 12.1 Range rings and coverage (P1, M3)

- **Per unit** (selected or pinned): rings for each weapon's maximum and no-escape range (from `bda.dlz` and weapon data), each sensor's detection range (radar per RCS reference, IRST, sonar active and passive), and the jammer's burn-through range when known.
- **Per side** (layer): the union of sensor coverage and the union of weapon envelopes, drawn as filled areas with outline only at the edge, so 30 ships do not draw 90 circles.
- **Threat rings** for hostile tracks with a known or classified type: the expected weapon range from the class's doctrine envelope (`tac.threats`), dashed while the type is only classified.
- **Altitude awareness.** Air-defence rings can show the radius at a chosen altitude (radar horizon and missile envelope at that altitude), set by a slider on the layer.
- **Controls.** Toggles per kind (weapon, sensor, threat, jammer), per domain, and per side; opacity; "only for selection".
- **Cost.** Envelopes are recomputed only on change (loadout, EMCON, track class) and drawn from cached paths.

### 12.2 Message log (P0, M3; reports and comms from M4)

- **One stream** of events, automatic reports and comms (`09-c2-multi-agent.md` §11.6), newest at the top, one line per item in the compact text form.
- **Filter chips** by category (contact, weapons, unit, order, mission, c2, comms, system), by side (fog windows show only their own), by unit or group (from the selection), by severity, and by text search.
- **Pinned filters** save as named views ("weapons only", "Strike 1").
- **Clicking a line** pans to its position and selects the unit or track. In replay, it jumps to its UT.
- **Unread markers and sounds** for the pause-on-event keys (§12.5).
- **Export** of the visible lines to CSV, and the same filter as a `rec.query` for agents.
- **Retention:** live, 10,000 lines in memory; everything in the recording.

### 12.3 Unit details and magazine view (P0, M3)

- **Header.** Name, class, side, the control badge (none, `M`, `O`, `C`, `D`, `S`, `H` or the stick icon, §6.1), the mission and group, and the effective doctrine summary (weapons control, EMCON) with the level each value comes from (`c2.doctrine.effective`).
- **Magazine.** One row per weapon type: ready, total, reloading (with time left), in flight, and the winchester threshold marker. Countermeasures (chaff, flares, smoke, decoys) left.
- **State.** Fuel with the bingo marker, HP, flooding (HullBreach inflow and flooded fraction), speed, altitude or depth, current target and guard state, and sensors with their EMCON state.
- **Recent.** The unit's last 10 reports and orders.
- **Actions.** Take control, Return, Fly in KSP, and edit unit doctrine (with locked keys greyed out and the locking level shown).
- **Multi-selection** shows a table: one row per unit with the same columns, sortable, with totals per weapon type.

### 12.4 Losses and expenditures panel (P1, M3 live; M5 scorecard)

- **Live panel** per side, from `c2.losses` (`09-c2-multi-agent.md` §11.6):
  - lost by class, with the killing weapon;
  - subdued and surrendered (M6);
  - expended by weapon type, with hits and kills;
  - countermeasures;
  - exchange ratio and missiles per kill.
- **Drill-down.** Clicking a row lists the units or shots, which pan the map. In replay, it jumps to the time.
- **Fog.** A fog window shows own losses and expenditures exactly, and enemy losses only as observed (kills claimed by own units and observed deaths). God view shows both sides exactly.
- **Post-battle scorecard** (M5) from `rec metrics`: the same numbers plus mission outcomes, the ending kind (victory, surrender, negotiated, timeout), violations and survival.

### 12.5 Time bar and pause-on-event (P0, M3)

- **Time bar.** Pause, 0.25×, 0.5×, 1×, 2× and 4×, plus lockstep step (M7). UT and wall clock are shown, and the current scale is highlighted. An amber banner shows when think-time slow motion is active (`09-c2-multi-agent.md` §8.4) with **Veto**.
- **Drop to 1×** on the `sim.time.drop_on` events (§11.6 there), shown as a small toggle list.
- **Pause-on-event toggles** with the defaults for human and agent seats (`09-c2-multi-agent.md` §11.6). When a pause fires:
  - a banner names the event and the unit;
  - the map pans to it;
  - **Resume** continues.
- **Several humans.** Only one pause policy applies per session: that of the human holding the seat, or else the first human on the map.

### 12.6 Contact classification display (P1, M3)

- **States.** Frame-only for `detected`, icon with `?` for `classified`, full symbol and name for `identified` (`08-tactical-picture.md` §2), and a hand mark for manual marks (`tac.track.mark`).
- **Track card.** `idState` and its source (sensor, visual, hostile act, shared, manual), `classConf` as a bar, and the sensors holding the track.
- **Mark action.** Right-click → **Mark as…** (hostile, friendly, neutral, class) with a note. The mark applies only to this side's picture.

### 12.7 Missions panel (P1, M6)

- **List** of the side's missions, with type, state and phase, assigned units, and status counters (on station, transit, RTB, engaged, kills, losses, subdued).
- **Create** from the toolbar or by dragging a zone: pick the type, fill the type's parameters (`09-c2-multi-agent.md` §11.2) in a schema-generated form, and assign units by dragging them from the map or the unit list.
- **Map drawing.** The mission's area, prosecution zone, route, time on target and station are drawn in the mission's colour. Strike phases show on a small timeline.
- **Actions.** Activate, pause, resume and cancel, plus edit mission doctrine. A unit's take-over and return show in the list (§6.1).

### 12.8 Scenario editor (P1, M6)

A mode of the map that builds a scenario YAML (`10-scripting-sdk.md` §7) and can run it straight away.

- **Forces.**
  - Drag craft from the library onto the map with the full spawn card (`06-flight-control.md` §9.1): mode (air, sea, submerged, ground, runway; orbit at M8), altitude or depth, speed, heading, count with no cap (with the line, row and clear-space preview and the warning above 50), formation, team, guard, AI, group and mission.
  - Placed forces stay editable as spawn specs, not as vessels, until the scenario runs.
- **Sides.**
  - Name and team, colour, seat (`human`, `agent <actor>` or `vacant`), `agentTakeover`, god-view grants, initial doctrine with locks, and EMCON.
  - Initial stance per side pair, with an optional ceasefire duration.
  - Comms settings: `broadcastCallsigns`, `agentsMaySurrender`, realism on or off.
- **Missions and zones.** Create them as on the live map. They are saved as part of the scenario and activate at load or at a set UT.
- **Waves and respawn.** A wave list (trigger by UT, by event or by condition; spawn spec; group and mission assignment) and respawn rules (`sim.respawn.*`: when lost, delay, maximum, rejoin mission) per group.
- **End conditions.** Victory by score or elimination, `transit`, `side_surrendered`, `stance_held`, and `timeout`, with scoring weights (`subduedWeight`, `violationPenalty`).
- **Run.** **Validate** checks the craft exist, the spawn slots are clear, the zones are closed and the missions are complete. **Run** loads the scenario (`sim.scenario.load`), and **Save** writes YAML to the library. The batch runner and director use the same file.

### 12.9 Doctrine, ROE and EMCON editor (P1: basic M4, full M6)

- **A tree** with the side, then missions, then groups, then units. Each key shows its effective value, its source level and its lock state (`c2.doctrine.effective`).
- **Editing.** Setting a key at a level shows which units change. Locking a key at side or mission level greys it out below.
- **EMCON.** Choose a preset per level, with per-sensor overrides and the `goActiveWhen` triggers. The EMCON layer previews the result.
- **ROE zones** come from the planning tools (§6). Their effect is shown per unit in the unit details.
- **Import and export** as a doctrine YAML (`09-c2-multi-agent.md` §5).

**[R] Beyond the genre (user clarification 2026-11-08).** The map presents Skizzy's own strengths up front rather than hiding them behind the genre features (`09-c2-multi-agent.md` §11.7):

- **Real physics.** Units are real KSP vessels under real physics, fighting with BDArmory's AI, sensors and damage.
- **Agents and scripts.** The commander agent, its delegates and scripts show on the map as actors with tags and their orders. The user can take over from them or hand units to them.
- **Fly it yourself.** "Fly in KSP" is one click from any own unit.
- **[R2] Spawn anywhere.** Any craft, any count, mid-air at speed, at sea or submerged, by drag from the library (§6).
- **[R2] Talking to the enemy.** Ceasefires, surrender demands, threats and bluffs between commanders in the comms panel (§5.2).
- **Craft building.** The craft library panel spawns the user's own designs into scenarios and missions.
- **Logging and replay.** Full recording and replay with slow-motion scrubbing, markers, the Log… picker and battle comparisons are part of the same screen.
