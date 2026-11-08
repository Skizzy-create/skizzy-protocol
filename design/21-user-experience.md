# 21 User experience: first run, install, in-game UI, other clients, docs, upgrades — [REVIEW-ADDED]

Added by review pass 1.1. Pass 1.0 described capabilities but not how a person gets from "downloaded a zip" to "my agent is flying the ship", how every feature is reachable without Claude Code, or how the system is upgraded.

## 1. First run (P0, M0)

On the first flight-scene load after install, the host opens a **first-run wizard** (IMGUI, through ClickThroughBlocker when present):

1. **Welcome and principle:** "Skizzy adds nothing to your game until something connects. BDArmory works exactly as before." (`00-overview.md`).
2. **Token:** generates the 256-bit token in `PluginData/auth.token`, shows its path, and offers **Copy MCP command** (`claude mcp add skizzy -- "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" mcp`) and **Copy Python snippet** (`import skizzy; sk = skizzy.connect()`; the SDK finds the token and port through `endpoint.json`). **[R2]** The MCP command is offered per client (Claude Code, Codex, or a generic stdio command for any other MCP client), and the wizard asks which side the user's own agent commands by default (`09-c2-multi-agent.md` §8.1).
3. **Preset:** Battle Lab (default), Test Director, Full Combat or Observer, with a one-line explanation each and a link to the permissions window.
4. **Port test:** binds 47549 (or the next free port), shows the result, and runs a loopback self-connection.
5. **Optional:** create the test-range save (`18-risks-and-decisions.md` §3.2 D-5); enable auto-snapshot on quicksave; choose log and recording folders.
6. **Doctor summary:** the in-game half of `skizzy doctor` (versions, BDA hook mode, PRE range, KSPCF `MemoryLeaks`, CommNet, other autopilots, Harmony/KSPBurst notes from `20-runtime-lifecycle.md` §9) with green/yellow/red lines.

The wizard can be reopened from the toolbar menu. `skizzy doctor --fix` (CLI) repairs what it safely can: recreate a missing token, write `endpoint.json`, reset a corrupt `settings.cfg` from defaults (keeping a backup), create the range save, register the MCP server with Claude Code if the user agrees. It never edits other mods' files or the user's saves.

## 2. Install (zip P0, CKAN P2)

- **Zip (M0):** one archive with `GameData/SkizzyProtocol/` containing `Plugins/` (in-game DLLs), `Bin/skizzy.exe` (self-contained .NET 10 CLI and bridge, plus the `skizzy-mcp.exe` alias), `Defaults/` (settings, permission presets, `symbols.json`, log profiles, range-save template), `Annotations/`, `Behaviours/` (empty), `README.md`. The map (`skizzy-map.exe`) ships in the same zip under `Bin/Map/` from M3. Python: `pip install skizzy` (PyPI from M9; before that `pip install <KSP>/GameData/SkizzyProtocol/Bin/python/skizzy-*.whl`).
- **CKAN (M9):** `SkizzyProtocol` package (depends on ModuleManager, recommends BDArmory-Skizzy or BDArmory, PhysicsRangeExtender, ClickThroughBlocker, ToolbarControl); the user's `SkizzyNavalPack` metapackage recommends it. CKAN installs the in-game part; the bundled `Bin/` covers the external tools.
- **Uninstall:** delete the folder. Skizzy writes nothing into saves (`20-runtime-lifecycle.md` §5); user data in `<KSP>/Skizzy/` stays until the user deletes it.

## 3. In-game UI inventory (every feature reachable from the game)

All windows use the BDArmory-like IMGUI style, ClickThroughBlocker when installed, one toolbar button (ToolbarControl or stock `ApplicationLauncher`) with a menu, and remember their positions.

| Window | Contents | M |
|---|---|---|
| **Connections** | sessions, actors and clients (`host.sessions`, with `client.mcpClient` such as Claude Code or Codex), per-session rate and bytes, disconnect, script supervisor status; **[R2]** delegation tokens by name with **Revoke** (never the token itself) | M0 / M4 |
| **Permissions** | presets, areas, per-mod/per-capability levels, ROLE/CLIENT rules, pending `ask` prompts, deep-reflection enable per mod | M0 |
| **Capability console** | catalog search; selecting a capability shows its description and a **form generated from its JSON Schema** (enums as dropdowns, numbers with ranges and units, handles with a vessel/part picker); Call; result viewer (compact text or JSON); recent calls; errors with hints | M0 |
| **Recorder / logs** | Log… picker (vessel → part → module → field, rate, format), running logs and recordings, profiles, marks, disk usage, prune | M0 / M2 |
| **Camera** | mode buttons (follow/orbit/chase/free/look-at), target picker, screenshot and save | M0 |
| **[R2] Spawn** | craft picker (library and `.craft` files), mode (air, sea, submerged, ground, runway), altitude or depth, speed, heading, pitch, engines, throttle, gear, **count field (any number)**, layout (single or line), abreast or trail, spacing (auto or metres), per-row count, team, guard, AI, group; spawn at the camera target, at a picked vessel or at typed coordinates; slot preview markers in the scene; the large-count warning with its estimate above 50 (`06-flight-control.md` §9.1) | M1 |
| **Flight** | holds and setpoints per vessel, lease holder, hand-off buttons, human-override banner; **[R]** control state of the active vessel with **Take control** and **Return to mission** (`09-c2-multi-agent.md` §4.1, from M4) | M1 / M4 |
| **Kill switch banner** | always visible while engaged; release button | M0 |
| **Health** | frame-time cost, budgets, degraded flags, memory self-check, auto-disabled capabilities | M0 |
| **Tactical overlay** | mini-map and picture (`11-tactical-map.md` §8) | M3 |
| **Orders / leases** | orders by group with status, leases with holders, grant/revoke | M4 |
| **[R2] Command** | per side: the seat holder (human or agent, with the client name), **Take**, **Hand to…**, **Release**, the `agentTakeover` prompt (Allow, Deny), advisors, delegates with their cluster and **Revoke**, `ask_human` prompts; pass 1's Agents window with budgets and freeze/wake is gone | M4 / M6 |
| **[R2] Comms** | compact list of the side's team traffic and reports, compose, the stance strip, accept or reject for proposals addressed to a human-held seat (`22-communications-diplomacy.md` §11) | M6 |

**Why the console matters.** Pass 1.0 promised that every feature is usable from scripts, agents, the map and the game, but each in-game window was hand-built, so new capabilities had no in-game surface until someone wrote one. The capability console makes the rule structural: any capability in the catalog is callable from the game the day it exists, with the same permission checks and audit as any other client. The map's command palette (`11-tactical-map.md` §5) is the same component. Dedicated windows exist only where a better UI pays off.

## 4. Using Skizzy without Claude Code (P0/P1)

| Path | What it is | M |
|---|---|---|
| **CLI** | `skizzy call <capability> key=value…` (JSON or compact output), `skizzy watch <selector> --hz 2` (live table in the terminal), `skizzy log start|stop|tail`, `skizzy shot [--target --mode -o file]`, `skizzy fly hold --heading 90 --speed 15`, `skizzy doctor`; scriptable from PowerShell or batch files | M0 (call/watch/log/shot/doctor), M1 (fly) |
| **Python** | `skizzy` 0.1 synchronous client in M0 (REPL and Jupyter friendly: `sk.read("active")`, `sk.watch(...)`, `sk.log.start(...)`, `sk.shot()` displays inline), the full async SDK in M4 | M0 / M4 |
| **Any MCP client** | `skizzy mcp` is a standard stdio MCP server; `skizzy serve` adds Streamable HTTP on loopback; any MCP-capable client or IDE works (**[R2]** Claude Code and Codex are checked in M0, delegation modes in M4), and the toolsets and output caps behave the same | M0 / M6 |
| **[R2] API key** | `skizzy.examples.api_commander`: a short Python commander using the user's own API key, with optional spending caps (`09-c2-multi-agent.md` §8.5) | M6 |
| **Tactical map** | full human command screen, including the command palette | M3 |
| **In-game console** | §3 | M0 |
| **C# SDK** | `Skizzy.Client` for tools and mods | M4 |
| **Plain WebSocket** | the protocol is documented JSON-RPC; a 30-line client in any language works (`03-protocol.md`) | M0 |

## 5. Documentation and examples

| Item | Content | Pri | M |
|---|---|---|---|
| README quickstart | install, first-run wizard, connect Claude Code, five example prompts, the same five as CLI commands, kill switch | P0 | M0 |
| Troubleshooting | doctor output explained line by line; port, token, CommNet, packed vessels, BDA hook modes, assembly conflicts | P0 | M0, grows each milestone |
| Capability reference | generated from the catalog schemas (one page per namespace), with examples | P0 | M0, regenerated |
| Compact-text grammar | the track, SITREP, order and summary line formats (`03-protocol.md` §6, `08-tactical-picture.md` §7) as a one-page sheet for prompts | P0 | M2 |
| Cookbook | recipes: log a field, fly a route, spawn a battle (**[R2]** mid-air, at sea, submerged, a line of N, a swarm formation), assign targets, **[R2]** command a side from Claude Code or Codex, spawn a delegate sub-agent for a cluster, negotiate a ceasefire, write a swarm script, build a blueprint, run an A/B batch, replay a battle | P1 | M4 onward |
| Agent prompt templates | **[R2]** system prompts and doctrine excerpts for the commander (any client), a delegate sub-agent (with how to pass its token or `as`), the builder, and an agent-vs-agent commander with only Skizzy tools (R31), kept cache-stable; pass 1's group, unit and analyst templates are P3 | P1 | M4 (commander, delegate) / M6 |
| Scenario and doctrine examples | the `16-scenarios.md` walkthroughs as runnable files in `Skizzy/library/` | P1 | per milestone |
| Docs site | the above published with search | P2 | M9 |

## 6. Upgrades and compatibility (P1)

- **Settings and permission files** carry `schemaVersion`; the host migrates older versions on load with a backup (`permissions.cfg.bak-<version>`), never silently widening permissions (a new capability gets the preset's default level for its area and appears in the permissions window as "new").
- **Recordings and logs** carry their format and protocol versions (`12-logging-recording-replay.md` §3); readers support every older 1.x format; `skizzy rec upgrade` rewrites old files when a format changes.
- **Library items** (blueprints, doctrine, scenarios, log profiles, `symbols.json`) carry `schemaVersion` and are migrated on read; the library keeps the original.
- **BDArmory updates:** the host records the BDA assembly version and hash; on a change it re-resolves hooks, reports the new `bda.hooks` modes and lists capabilities that lost a hook; annotation packs for a different `modVersion` are ignored with a warning (`04-ksp-and-mod-access.md` §3).
- **Protocol and SDK compatibility:** protocol MINOR is additive; the hello handshake negotiates; the Python package version tracks the protocol MINOR (`10-scripting-sdk.md` §8); the bridge supports hosts from 1.0 up.
- **KSP updates:** KSP 1.12.5 is the target; the build guard compiles against the installed `Managed` folder, so a different KSP build shows API breaks at compile time.

## 7. Acceptance (summary)

1. From a clean KSP with the zip installed, a new user reaches "Claude Code reads my vessel and takes a screenshot" in under 5 minutes using only the wizard and the README.
2. A capability added to the catalog appears in the in-game console and the map palette with a working generated form, without UI code.
3. Every M0 capability is usable from the CLI, Python, an MCP client and the in-game console.
4. Upgrading from an earlier milestone build migrates settings and permissions, keeps a backup, and lists new capabilities as "new" without enabling anything beyond the preset.
