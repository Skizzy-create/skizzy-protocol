# 24. KSP parity: every mockup feature runs in KSP

**Rule (user, 2026-11-08).** The web mockup is the reference for look and behaviour. It is not the product. Every behaviour proven there must run in KSP with BDArmory Overdrive. A feature is not "done" until it passes in-game acceptance on the test machine (§3).

This table is the source of truth for that mapping. Update it whenever the mockup or a milestone changes.

## 1. Feature map

Hooks are in `07-bdarmory-integration.md` §5. "Skizzy" means the C2 engine in `Skizzy.Host`. "BDA" means BDArmory Overdrive.

| # | Mockup feature (version) | How it runs in KSP | Hooks | Milestone |
|---|---|---|---|---|
| 1 | Tactical plot, symbols, density sizing (v1–8) | Skizzy.Map renders the live track picture streamed from the host | — | M3 (render core in progress) |
| 2 | Groups, number keys, box/double-click select, Groups panel (v6–8) | Map-side selection; groups stored in the host (`c2.group.*`) so agents see them too | — | M3/M4 |
| 3 | Move / attack by right-click; drawn routes (v6–8) | `move_to`, `engage`, `move_route` orders. The executor feeds BDA waypoints and target overrides | F1, F6 | M4 |
| 4 | Attack spreads across the target group, retargets after a kill (learning) | Skizzy WTA assigns one override per shooter and re-assigns on `unit.destroyed` | F1, F10 | M4 |
| 5 | **Stay on task** (no distraction by crossing hostiles) (v11) | F3 `fireAuthority=Tight` plus an F4 `ExternalTargetFilter` allowing only the assigned group, plus an F1 sticky override. BDA guard, team assist and `GetGuardNonTarget` all obey the filter | F1, F3, F4 | M4 |
| 6 | **Engage on contact** (divert to any hostile the unit's own radar sees, then continue) (v11) | `fireAuthority=Free`, no filter. Skizzy watches per-vessel detections (F5, F10 `OnTargetDetected`) and re-points the override, then restores the original order when the contact dies or is lost | F1, F5, F10 | M4 |
| 7 | **Stealth contact seen only by a drone's own radar** (v11) | Per-vessel radar contacts (F5), not only team data link. The picture marks the track as "own-sensor only" until shared | F5 | M2/M4 |
| 8 | **Weapons control per aircraft: Free = guard, Tight, Hold = passive** (v11) | Free = guard mode on, fire authority Free. Tight = guard on, fire authority Tight with the filter. Hold = fire authority Hold (scans and locks, never fires); countermeasures still allowed | F3 | M4 |
| 9 | **Racetrack patrol that fights** (v10–11) | `hold_pattern` turns the racetrack into BDA waypoints with laps. **F6b** lets waypoints yield to combat when guard is on and a target exists, with a leash and a resume at the same waypoint. Hold stays on the pattern. Verified gap: stock `BDModulePilotAI` puts `FlyWaypoints` before the attack branch, so a stock patrol never fights | F6, **F6b**, F3 | M4 |
| 10 | RTB, RTB all (top bar), refusals with reasons (v10–11) | `rtb` order (`09` §2.1.1): base resolution, marshal point, refusals reported per unit; an in-game toolbar "RTB selected / RTB all" that works without any AI client | F6 | M4 |
| 11 | Carrier landing, deck, rearm, launch (v10) | Needs a carrier-landing autopilot driving ACA arrestor wires (the `recover` adapter). Until then `rtb` ends at marshal. Raised from P2 to P1 at the user's request | new adapter | M8 |
| 12 | Fighters and drones engage missiles when no aircraft are in view (v9) | Doctrine `airEngageMissiles`; BDA interceptor / missile-targeting settings on the weapon manager, priority via the F2/F4 filter | F2, F4 | M4/M6 |
| 13 | VIPs and protect orders (v9) | `defend_unit` order plus a side-level `vip` flag feeding WTA priority; protectors get waypoints orbiting the VIP and a filter limited to threats within the radius | F1, F4, F6 | M4 |
| 14 | Ship SAM priority for VIP threats (v9) | WTA priority on surface units' assignments (F2 secondary targets for multi-target ships) | F2 | M4 |
| 15 | Friendly fire logging (v7) | BDA simulates it physically; Skizzy attributes it from `Scoring` events and logs a counter and pause-on-event | F10 | M2 |
| 16 | Posture Defensive / Proactive (v5) | Side doctrine preset (`09` §11) | — | M6 |
| 17 | Orders never silently dropped (learning) | Validation on issue with typed refusals; every order ends with a report | — | M4 (rule from M0) |
| 18 | Comms, diplomacy, stance (v3+) | `22-communications-diplomacy.md` | — | M6/M7 |
| 19 | Timeline replay, skip to next event (v4, learning) | Recorder plus `sim.time.*` warp-until-trigger | — | M2/M3 |
| 20 | Multi-spawn in a line, any count (design) | OD1 in Overdrive plus `ksp.vessel.spawn_craft {layout:"line"}` | OD1, F11 | M1 (OD1 in progress) |

## 2. Rules for builders

- When a mockup behaviour has no KSP mechanism yet, add a row with "gap" and the proposed hook *before* building it in the mockup. Example: F6b came from the patrol request.
- Every row gets an in-game acceptance item in the milestone's checklist (`docs/Mx-checklist.md`) and, where it can be scripted, an automated scenario test driven through the Skizzy MCP once M0 exists (§3).
- Behaviour constants proven in the mockup are defaults in the KSP implementation and stay configurable in doctrine:
  - patrol leash 55 km
  - drone own-radar range 25 km, fighter 45 km (the real values come from the craft's radar parts)
  - marshal 8 km astern
  - landing gap 5 s
  - deck turnaround 90 s

## 3. Testing on the user's desktop

Test machine: the user's desktop (Ryzen 9 9950X3D, RTX 5080, 32 GB).

- **A separate test agent runs there,** in Claude Code. It is a different agent from the builders: builders don't grade their own work.
- **Builds reach it through git.** Branches are pushed to the user's GitHub with the user's approval. The test agent pulls, builds, installs into a **separate test copy of KSP** (never the user's play install), runs the checklist, and pushes a report (`test-reports/<date>-<milestone>.md` plus `KSP.log` excerpts) back for the builders to read.
- **Before M0 works,** tests are guided manual runs from the checklists, with log scraping.
- **After M0,** the test agent drives KSP itself through `skizzy mcp`: it spawns scenarios, runs BDA competitions, reads tracks, takes screenshots and logs fields. In-game acceptance becomes scripted and repeatable, with real numbers.
- **Optional live debugging from the build machine:** run `skizzy serve` on the desktop, reach it over an SSH tunnel or Tailscale (never an open port), and authenticate with the token.
