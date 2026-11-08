# 23. Visual design and mockup learnings

**Status:** approved look (user, 2026-11-08). The reference implementation is `design/mockups/command-screen-v1.html`, also published as the "Skizzy Command" artifact. When this file and the mockup disagree, the mockup wins on look and this file wins on rules. The tactical map (M3), the in-game overlay and the headless renderer all follow it.

## 1. Direction

- **Combat-information-centre console.** Panels dock around one live plot. The plot is the brightest thing on screen; the panels recede.
- **Dark, blue-green slate,** like a darkened operations room. This is a deliberate single dark theme. A daylight theme is P2 and must redefine the tokens, not invert them.
- **Calm by default, loud only for events.** Red/amber/green appear for hostile identity, warnings and losses, never as decoration.

## 2. Tokens

The source of truth is the `:root` block in the mockup. The Avalonia map mirrors these as resources.

| Token | Value | Use |
|---|---|---|
| `ink-0` | `#071013` | app ground |
| `ink-1` | `#0c171b` | panels |
| `ink-3` | `#162a31` | raised controls, pressed state |
| `line` / `line-hi` | `#1d333b` / `#2a4a55` | borders, dividers |
| `sea` | `#081519` | plot background |
| `grid` / `grid-hi` | `#10242b` / `#173540` | grid lines, every 5th line |
| `text` / `text-dim` / `text-faint` | `#d3e0e4` / `#7f979f` / `#4f666e` | text levels |
| `friend` | `#62c6f2` | MIL-STD-2525 friend |
| `hostile` | `#ff6a57` | hostile |
| `unknown` | `#e9c84f` | unknown / pending |
| `neutral` | `#7cd68e` | neutral |
| `accent` | `#b5a3ff` | the operator: selection, your orders, your controls, the M/O badges |
| `ok` / `warn` / `crit` | `#59d09a` / `#f0a43a` / `#ff4b4b` | intercept, warning/damage, critical/loss |

Rules:
- Affiliation colours are never used for UI chrome.
- The accent is never used for tracks.
- Shape always carries the same meaning as colour, so colourblind players can read it: friend round, hostile diamond, unknown quatrefoil.

## 3. Type

| Role | Face | Use |
|---|---|---|
| Display / labels | Saira Semi Condensed 500–700 | panel titles, uppercase labels (letter-spacing 0.12–0.16em), unit names |
| UI | IBM Plex Sans Condensed 400–600 | buttons, messages, prose |
| Data | IBM Plex Mono 400–500, tabular numbers | bearings, ranges, altitudes, clock, track ids, tables, plot labels |

Scale is 10.5 / 12 / 13 / 15 / 18 px. Bundle the fonts with the map (OFL licence); the map makes no network fetch for them.

## 4. Plot rendering rules

These implement `11-tactical-map.md` §2.1 and add what the mockup proved.

1. **Every track is drawn as itself.** Counts ("MSL ×N") are only an optional toggle.
2. **Size = RCS hierarchy × zoom factor**, then capped by the screen distance to the nearest *peer*.
   - Only peers count. **Missiles and much smaller tracks (RCS < 1/50 of this track's) never shrink a ship or an aircraft.** *Learned:* a destroyer shrank every time it fired a SAM because the SAM counted as a neighbour.
   - The small marks are drawn on top instead.
3. **Tiers:**
   - ≥ 16 px: full frame plus icon.
   - 8–16 px: frame only.
   - Below 8 px: dot, with a shape hint.
   - Missiles: an arrowhead down to 5 px, then a dot.
   - Hostile missiles are filled; own missiles are outlined.
4. **Ease size changes over about 150 ms** so symbols never pop. Use ±10% hysteresis on tier changes.
5. **Altitude stalk** under air tracks: length tracks altitude, colour by band (amber for sea-skimming, grey low, white high). Dots carry altitude this way too.
6. **Speed leader** ahead of the symbol, about 45 s of travel. **Trails** are two strokes: a faint old half and a brighter recent half (cheap, still reads as fading).
7. **Event marks** fade after an event:
   - intercept: green ring
   - CIWS kill: amber ring
   - impact: red burst
   - kill: ×
8. **Labels** go in four positions, greedily placed with a dark backing box and culled when they can't be placed. Missile labels appear only when zoomed in or selected. A label never hides a symbol.
9. **Control-state badge** on own units:
   - **M** (accent): you have manual control.
   - **O** (accent): your order is running.
   - **A** (friend blue): an agent or sub-agent controls the unit.
10. **Orders on the plot:**
    - move: accent dashed line plus diamond.
    - attack: accent dashed line to the target.
    - drawn route: solid accent polyline drawn once per group, plus a faint line from each unit to its next point.
    - When more than 6 orders are active, lines fade to about 28% so a big group doesn't paint a thick band.
11. **Zones:** dashed hostile exclusion line with a 3.5% fill, dashed friend patrol circle, bullseye rings.
12. **Range rings:** a faint ring around each ship. The selected ship gets bright rings plus its radar ring and range labels.

## 5. Interaction (proven in the mockup)

| Action | Result |
|---|---|
| Wheel | zoom at the cursor |
| Drag | pan |
| **F** | fit all |
| Click | select |
| Shift-click | add to the selection |
| **Shift-drag** | box-select (own units preferred) |
| **Double-click** a unit | select its whole group |
| **Right-click** | move the selection there, as a grid formation (aircraft 2.2 km apart, ships 1.5 km) |
| Right-click a hostile | attack it and its group |
| **Right-drag** | draw a route the selection follows in formation |
| **Make group** (Unit panel) | name a group made from the current selection |
| **Alt+1…9** | put the selection on a number key |
| **1…9** | select that group again |
| `[` / `]` | shrink / enlarge the selected symbols (per-item override) |
| **R** | RTB the selection (`09-c2-multi-agent.md` §2.1.1) |
| **Shift+R** | RTB all own units; a confirm strip shows the count and where they are going; Enter confirms, Esc cancels |
| Space | pause |
| Esc | clear the selection |
| Take control / Return to mission / Send to… / **RTB** / Fly in KSP / Center | Unit panel controls; the right-click menu on own units also offers RTB |

- **Panels:** drag the edges to resize. Hide and show from the top bar. Layout is remembered. Reset layout restores the defaults.
- **Timeline:** scrub back for replay; Live returns. Event ticks are drawn on the bar.
- **Top bar:** UT clock (Kerbin 6-hour days), speed 0.25–8×, Restart, side stance pill, Blue posture (Defensive/Proactive), friendly-fire setting, seat holder plus hand-over.

## 6. Learnings from the mockup that change behaviour (not only look)

These were recorded by the user on 2026-11-08 and must hold in the real system:

1. **Attack orders distribute and continue.**
   - An attack order on a member of a group means *engage that group*. Shooters spread across its members (weapon-target assignment).
   - When a shooter's target dies, it **switches to the nearest unengaged member** instead of turning home.
   - It returns to its mission only when the group is destroyed, or the shooter is out of weapons (winchester) or out of fuel (bingo).
   - *Mockup v7 sent everyone at one target and turned them all around after the first kill.*
   - In the real system: the C2 layer's WTA (`09-c2-multi-agent.md`) feeds BDA target overrides, re-assigning on `unit.destroyed`.
2. **Swarms must split targets intelligently.** Twenty drones should not all chase one aircraft. BDArmory's AI does most of this in the game; Skizzy adds the assignment layer above it (point 1).
3. **Group selection must scale.**
   - In the mockup, the long track list made it hard to pick a group with many members.
   - The real map needs a **Groups panel** (collapsible tree: side → group → units) where one click selects a whole group, plus the number-key slots.
   - The track list stays for individual tracks.
4. **User-made groups and drawn routes are core (P0 in M3/M4),** not extras.
5. **Friendly fire:**
   - BDArmory simulates it physically: blasts and bullets hit anything.
   - Skizzy detects and logs it: "friendly fire" damage events, a counter in the Losses panel, pause-on-event.
   - The mockup's "1 in 1,000 missiles" setting is a demo stand-in, not a game rule.
6. **Posture switch:** a side-level Defensive/Proactive doctrine preset is useful at a glance (fighter hunt radius, ship engagement range, alert launches). It maps to doctrine presets in `09-c2-multi-agent.md` §11.
7. **Comms tab works as designed:** team reports, diplomatic demands and an unverified broadcast (spoofing) read clearly. Keep that layout.
8. **Dead time before contact.**
   - In the mockup the first missile launch comes about 8 minutes of game time in, roughly 2 real minutes at 4×.
   - The real map needs **"Skip to next event"**: run at the highest safe warp or time scale until a trigger fires (new hostile contact, a launch, a unit inside a zone, an order completing), then drop back to 1× and pause if pause-on-event is on.
   - The scenario editor also needs a "start at contact" option.
   - Ties into `sim.time.*` and the triggers in `03-protocol.md`.
9. **Performance:**
   - The plot must not tie simulation time to frame rate.
   - In the real map, KSP time is authoritative and the map only interpolates, so this is satisfied by design.
   - Keep draw calls per trail at two strokes.
10. **Orders must never be silently dropped.** Reported by the user on 2026-11-08: the CAP South flight ignored orders.
    - The likely cause in the mockup is ammunition. Under the Proactive posture the CAP South flight spent its missiles early, so every later attack order hit the "out of missiles → return to mission" check and was dropped in the same tick. To the operator that looks like disobedience.
    - Rule 1 — **validate on issue.** When an order is given, check whether each unit can carry it out: weapons, fuel, damage, range, current lease holder. Show the result straight away ("Cobra 1–4: no missiles left, attack refused. Send them home?"). Never accept an order and then discard it.
    - Rule 2 — **orders outrank posture and mission behaviour.** This matches the precedence bands: commander 800 beats behaviour 300 and BDA 0. Only self-defence may interrupt an order, and when it does it is logged.
    - Rule 3 — **every order ends with a report.** Completed, refused, aborted, or overridden (by whom and why) goes in the message log and the unit panel.
11. **RTB for the selection or everyone** (user request, 2026-11-08). This is a one-click order: the R and Shift+R keys, the Unit panel button and the right-click menu.
    - Each unit resolves its own base. Units with no base refuse with a reason (point 10).
    - Aircraft hold at the marshal point because BDArmory cannot land on a carrier.
    - Spec in `09-c2-multi-agent.md` §2.1.1.
