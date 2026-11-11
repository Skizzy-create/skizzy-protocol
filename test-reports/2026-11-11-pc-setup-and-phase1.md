# PC setup and phase 1 in-game tests, 2026-11-11

Tester: Claude Code (Opus 5.5) on the owner's desktop, driving KSP through `skizzy.exe`, the skizzy MCP bridge,
log files and shell restarts. The owner did a few in-game clicks early on (first-run wizard, launching one rocket,
kill-switch button, three permission prompts) and then declined further manual steps (see "Owner requirement").

Builds under test:

| Component | Source | Commit |
|---|---|---|
| skizzy-protocol | `main` | `48be7f2` (merge of `m3-map-app`, map app v1.1.0) |
| BDArmory Overdrive with hooks | `overdrive-hooks` | `e113c0de5` (contains `ec38cf761` and `9fac1f3bc`) |
| Skizzy package | `scripts/package.ps1` | 0.1.0: Skizzy.Host, Skizzy.Protocol, Skizzy.C2, Skizzy.BDArmory, Bin/skizzy.exe |

## Owner requirement: the MCP must be able to run these tests itself

The owner asked for this to be recorded: **the skizzy MCP needs to be developed far enough that these test checklists
can be run end to end by an agent, without a human in the game.** In this run every item that needed a scene change,
a spawned craft, flight control or a click in a KSP window could not be done by the agent and is marked
**BLOCKED (needs MCP)** below, with the missing capability named. The capabilities that blocked the most items:

| Missing capability | Blocks |
|---|---|
| Load a save / change scene (`ksp.scene.load` or similar: main menu → space center → flight, VAB, tracking station, revert) | M0 §10.1–10.3, stale handles; M4 B2 (in flight), B3–B13; every HOOKS section B–H |
| Spawn craft / multi-spawn with team, AI and guard (`ksp.vessel.spawn_craft`, planned for M1) | HOOKS B–H; M4 rows 3–28; M0 look-at camera |
| Flight control (throttle, staging, holds), planned for M1 | M0 demo with a moving vessel |
| Set BDA weapon-manager fields and call hook APIs (fire authority, `SetOverrideTarget`, `ExternalTargetFilter`, `SetWaypoints`, team) as a test capability, instead of Unity Explorer or a compiled harness with keypad keys | HOOKS D–H |
| Drive the in-game Skizzy windows (open window, press button, type in a field), or expose each window action as a capability | M0 §6 console, §9 picker, §11.3 Connections, §11.6 focus; M4 B3–B4 |
| UI automation hooks in the map app (click, select, open palette) | M3 §1–§9 interactive items |
| Run a BDA competition and read its result | M0 §7 BDA check; HOOKS B |

## Summary

| Area | Result |
|---|---|
| Part A, mods from CKAN | Pass. 66 requested mods installed with every pinned version; 97 CKAN-managed mods + 2 DLCs (100 after the two fixes below) |
| Part B, personal files | Pass. GameData, saves and Ships copied from the owner's private files repo; the three EVE/cloud paths present |
| Smoke test (main menu, KSP.log) | Pass. Main menu in about 2 min, `ModuleManager: 17778 patches applied`, 6 distinct exceptions, none from Skizzy or BDArmory |
| Part C, BDArmory Overdrive | Pass. Release build 0 warnings, Overdrive tests 122/122 |
| Part C, Skizzy Protocol | Pass. `dotnet test ci.slnf` 288/288 (excluding Perf), package and install OK, MCP registered |
| M0 checklist | 52 rows: 38 pass, 1 fail, 2 partial (one contains the CLI-reconnect fail), 3 not run or not verified, 7 blocked (some rows group several steps), 1 environment issue |
| HOOKS checklist | A pass (load check); B–H blocked (needs spawn, scene and hook-control capabilities) |
| M4 checklist | B1 pass; **B2 fail** (Skizzy.BDArmory.dll does not load on stock BDArmory); rest blocked |
| M3 checklist | 13 pass (incl. live host), 2 partial, the rest blocked (need UI interaction) |

New findings, most important first:

1. **[M4 B2, fail] Skizzy.BDArmory.dll fails to load against a stock BDArmory.dll.** Details in M4 B2 below.
2. **[M0 §10.4, fail] `skizzy watch` does not reconnect after a KSP restart.** It exits with
   `UNAVAILABLE: disconnected`. This is by design (`src/Skizzy.Cli/Common.cs:138`, "Connects once (no auto-reconnect)"),
   so the checklist line "the watch window resumes or prints a resync line" cannot pass. The MCP bridge and the Python
   client (`auto_reconnect=True`) both reconnected by themselves. Fix the checklist or add reconnect to `watch`.
3. **[M0 §8, fail, minor] `ksp.camera.set_mode mode=orbit` (and `orbital`) reports success but does not stick on the
   ground.** The call returns `{"ok":true,"kspMode":"ORBITAL"}`; 0.5 s later `ksp.camera.state` reads `kspMode:"CHASE"`
   (KSP refuses the orbital camera near the surface). The host should verify the mode, or refuse it with a reason.
4. **[M0 §11.1, doc] The checklist's `skizzy call host.debug.confirm_test` cannot work as written.** The CLI connects
   as actor kind `script`, which is clamped from destructive capabilities (`PERMISSION_DENIED: role_clamp`), and
   `--role commander` is refused for kind `script`. The prompt test passed when run through the MCP bridge (commander).
5. **[M4 §0.1, minor] No per-hook report.** M4 expects `host.status` (or a probe) to list F1–F10 as present.
   There is no `host.status` capability and `host.stats.c2` has no hook list; only the KSP.log line
   `C2 binding attached (host engine): BDArmory 1.13.1.0, all Overdrive hooks` says so.
6. **[M0 §1.2–1.3, doc] Stale package contents in the checklist.** It lists two plugin DLLs; the package has four
   (adds Skizzy.C2.dll and Skizzy.BDArmory.dll), and `package.ps1` stops unless `scripts/copy-refs.ps1` has been run
   first. The step list for a new PC should include `copy-refs.ps1`.
7. **[Environment] Ctrl+Alt+K is taken by another program on this PC** (pressing it switched away from KSP). The kill
   switch itself works from the menu and banner. Change `killSwitchKey` if this matters.

## PC specs

| | |
|---|---|
| CPU | AMD Ryzen 9 9950X3D, 16 cores / 32 threads |
| RAM | 31 GB |
| GPU | NVIDIA GeForce RTX 5080 (driver 32.0.16.2114); AMD Radeon iGPU also present |
| Display | 2560×1440 at 143 Hz, scale 100 % |
| OS | Windows 11 Pro 10.0.26300 |
| Game drive | D:, Corsair MP700 PRO NVMe SSD (game and installed mods only) |
| Data drive | E:, Storage Space on 2 × Seagate IronWolf 4 TB HDD (CKAN cache, sources, backups, test artifacts) |
| KSP | 1.12.5.3190 (Epic build 03190), Making History + Breaking Ground, Unity 2019.4.18f1, Direct3D 11 |
| Tools | .NET SDK 10.0.404, Git 2.55.0, GitHub CLI 2.103.3, CKAN 1.36.4, Python 3.12 (venv for the client) |

## Part A: mods from CKAN

| Step | Result | Notes |
|---|---|---|
| 1. Find KSP, version, DLCs | Pass | `$KSP` = `D:\Touch_Grass_Later\KerbalSpaceProgram\English`; `buildID64.txt` build 03190, readme "Version 1.12.5"; `GameData\SquadExpansion` has MakingHistory and Serenity |
| 2. Download ckan.exe 1.36.4 | Pass | Authenticode signature valid |
| 3. Instance, cache, compat, repo | Pass | Instance `Main` (default), cache `E:\CKAN-cache`, compat 1.8–1.11 added, repos KSP-default (priority 0) and SkizzyNaval (1); 2557 modules |
| 4. Install the list | Pass | Exit 0. Every pinned version installed, so no retries. BDArmory provider: BDArmory-Overdrive `2:v1.13.1.0-overdrive.1`. No EVE, clouds or BDArmoryForRunwayProject from CKAN |
| 5. `ckan list` | Pass | 97 managed mods + 2 DLCs |

Follow-up, approved by the owner: the headless install skipped three dependencies (CKAN reported "Unsatisfied
dependency" for each), so these were installed afterwards:
`Scatterer-config` and `Scatterer-sunflare` 3:v0.0878 (without them Scatterer has no atmosphere config) and
`WildBlue-PlayMode-CRP` v1.90.5 (WildBlueTools requires one play mode; CRP chosen because CommunityResourcePack is
already installed). The registry is consistent afterwards. Total: 100 CKAN-managed mods. CKAN also lists 11 EVE DLLs
from the personal files as unmanaged (`A ... (unmanaged)`), as expected.

## Part B: personal files

| Step | Result | Notes |
|---|---|---|
| 6. Clone and copy | Pass | Private repo cloned to `E:\ksp-personal`, robocopy of GameData, saves and Ships (exit codes 1–3, no failures) |
| 6. Check the three paths | Pass | `GameData\EnvironmentalVisualEnhancements`, `GameData\StockVolumetricClouds`, `GameData\HQ_volumetricClouds.cfg` present |
| 7. Smoke test | Pass | See below |
| 7. Baseline backup | Pass, moved | Copied before Part C (19,350 files, 11.3 GB). At the owner's request it now lives on the HDD at `E:\dev\backup\KSP-baseline` (verified identical file count and bytes) instead of `D:\Touch_Grass_Later\KSP-baseline` |

Smoke test, KSP.log (first full load): main menu after about 115 s; `ModuleManager: 17778 patches applied`.
"Exception" lines by mod:

| Mod | Line | Impact |
|---|---|---|
| EVE `_BuildManager` | `ArgumentException: Invalid path` in `_BuildManager.logVersion` | Harmless (version logging) |
| KSPCommunityFixes | `Exception: Terminating stock loader coroutine, this is intended and not an error` | None, intended |
| BDModularMissileParts | `LOAD FAILED : UnityException: LoadRawTextureData: not enough data provided` for `ProceduralTextures/SolidEnds.dds` | One texture missing |
| KerbalPowersNaval | `Module ModuleSplashedThrust threw during OnLoad: ArgumentException: Requested value 'Naval' was not found.` (×3, herring propeller) | Engine type field ignored; not checked in game |
| KerbalChangelog | `ArgumentException: Invalid version: 0.43.0.11b` | Cosmetic |
| EVE (clouds) | `EVEManager: Issue loading DropletsManager! NullReferenceException at Atmosphere.CloudsManager.PreprocessConfigs` (×3) | Clouds render in flight (seen in screenshots); rain droplets possibly missing |

No Skizzy or BDArmory exceptions.

## Part C: in-development mods

| Step | Result | Notes |
|---|---|---|
| 8. winget tools | Pass with note | GitHub.cli 2.103.3 and .NET SDK 10.0.404 installed. `Git.Git` needs admin and failed (UAC); Git 2.55 was already installed |
| 9. Clone `overdrive-hooks`, KSPRefs | Pass | 90 DLLs copied to `E:\dev\_LocalDev\KSPRefs` |
| 9. `dotnet build` BDArmory Release | Pass with workaround | First attempt: `MSB3644 reference assemblies for .NETFramework,Version=v4.8 were not found`. Installing the Developer Pack needs admin, so the official `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3 package was unpacked to `E:\dev\_LocalDev\net48refs` and passed as `FrameworkPathOverride` (the method in `Notes.md`). Then 0 warnings, 0 errors |
| 9. `OverdriveTests` | Pass, 122/122 | The test project targets net9.0 and only the 8 and 10 runtimes are installed; ran with `DOTNET_ROLL_FORWARD=Major` on .NET 10 |
| 9. Install the hooks DLL | Pass | Stock CKAN DLL backed up to `E:\dev\backup\BDArmory.dll` (sha256 `b70b1ded…`); hooks build installed (`31175cc7…`) |
| 10. `dotnet test ci.slnf -c Release --filter "Category!=Perf"` | Pass, 288/288 | Ksp 44, Protocol 64, C2 90, Cli 23, Host 67 |
| 10. `package.ps1` | Pass after `copy-refs.ps1` | `package.ps1` requires `refs\BDArmory.dll`; ran `scripts\copy-refs.ps1` (default source: the installed hooks build) first |
| 10. `install.ps1 -DryRun`, `install.ps1` | Pass | 11 actions, all under `GameData\SkizzyProtocol\` |
| 10. Connect Claude Code | Pass | `claude mcp add skizzy -- "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" mcp`; `claude mcp list` shows it connected |

## M0 checklist (`docs/M0-checklist.md`)

| Item | Result | Notes |
|---|---|---|
| 1.1 KSPDIR | Pass | `$env:KSPDIR` |
| 1.2 package | Pass (doc stale) | 4 plugin DLLs, not 2; needs `copy-refs.ps1` first (finding 6) |
| 1.3 dry run | Pass | Nothing outside `GameData\SkizzyProtocol\` |
| 1.4 install | Pass | |
| 1.5 uninstall | Pass (dry run only) | `uninstall.ps1 -DryRun`: 5 removals, keeps PluginData and `<KSP>\Skizzy`. Not run for real, so the install stays |
| 2.1–2.2 first start, PluginData | Pass | `auth.token`, `endpoint.json`, `settings.cfg`, `permissions.cfg`, `host.log`; `audit\` appears with the first audited call, not at start |
| 2.3 host.log header | Pass | `Skizzy host 0.1.0 starting: KSP 1.12.5.3190 (WindowsPlayer x64), Unity 2019.4.18f1, Direct3D11, idle wake sync_context ...`, `listening on ws://127.0.0.1:47549/skizzy` |
| 2.4 no `[Skizzy]` lines in KSP.log | Pass | Also `skizzy doctor`: "no [ERR/[EXC entries mentioning Skizzy". (`[SkizzyRig...]` lines are from the SkizzyNaval turret mod, unrelated) |
| 2.5 first-run wizard | Pass | Owner stepped through with defaults and pressed Finish; `firstRunDone = true` written |
| 2.6 toolbar button and menu | Pass | Seen in flight screenshots: Connections, Permissions, Capability console, Recorder / logs, Log... picker, Camera, Health, C2 command, First-run wizard, ENGAGE KILL SWITCH |
| 3 spike `nostdlib` | Pass | `forbiddenReferences` empty, `dynamicMethod` true, mscorlib 4.0.0.0, all refs from Managed or GameData |
| 3 spike `sync_context` | Pass | 20/20 posts, `framesMax` 0, `latencyMsP50` 0.52 ms |
| 3 spike `jpg_thread` | Pass | `identicalToMainThread` true, worker p50 4.14 ms vs main 4.16 ms |
| 3 spike `async_readback` | Pass | Direct3D11, capture `mainThreadMs` 0.107; `readpixels` fallback 9.1 ms |
| 3 screenshot orientation | Pass | Upright with `captureFlip = auto` |
| 4.1 `mcp --print-config` | Pass | |
| 4.2 `claude mcp add` | Pass | |
| 4.3 tool list | Pass | All 13 listed core tools present (25 tools total); `skizzy_status` works |
| 4.4 demo prompt | Pass (in parts) | In flight via MCP: `ksp_read` active vessel, chase screenshot; CSV log via CLI (see §9). Not run as one prompt |
| 4.5 clean install under 5 min | Not run | No clean-install stopwatch run |
| 5 Codex CLI | Not run | Owner: Claude Code only for now (Codex not installed) |
| 6 `skizzy doctor` in flight | Pass | `connect - hello in 47 ms`; WARNs only for CommNet on and AtmosphereAutopilot present |
| 6 `ksp.read`, `ksp.vessels.list`, `ksp.query` | Pass | |
| 6 `ksp.module.describe` / `ksp.module.get` | Pass | On `ModuleEnginesFX` (note: they take no `format` parameter) |
| 6 `ksp.mods.list` | Pass | 100 entries |
| 6 `watch` 5 Hz 10 s | Pass | 51 rows |
| 6 `watch events` | Partial | Only `host.session_*` events captured in the window that ran; no staging or vessel switching was possible |
| 6 RTT, Python client | Pass | 200 × `read("active")` on the pad: **p50 13.82 ms, p99 15.21 ms**, max 15.52 (target p50 < 20) |
| 6 in-game console | BLOCKED (needs MCP) | Needs window interaction |
| 7 idle run B (`listen = true`) | Pass | 673 samples / 672 s at the main menu, sessions 0, `pumpAwake` 0 after the grace period, `pumpUpdates` and `pumpFixedUpdates` constant (1), wakes 0. `idle probe: heap slope -409759 B/s, gen0 38.468/min`. Owner closed one KSP message dialog during this run |
| 7 idle run A (`listen = false`) | Pass | 672 samples / 672 s, same pump figures. `heap slope -361217 B/s, gen0 38.503/min`. B is within A's variation (heap growth in both runs is KSP's main menu itself) |
| 7 host.selfcheck footprint | Pass | `OnFlyByWire callbacks 0, Harmony patches 0`; 20 GameEvents handlers while connected (2 scene + 2 toolbar + 16 vessel/time) |
| 7 connected-idle gate | Pass | `measuredMsPerFrameP50` **0.0075 ms** (budget 0.1) |
| 7 BDA competition ×3 with/without | BLOCKED (needs MCP) | Needs spawn + competition control |
| 8 screenshot gate, 100 captures | Pass | `async_readback`, main thread **p50 0.11 ms** / p99 0.18 (budget 3), 0 failures, Texture2D 8392 → 8392, RenderTexture 166 → 166 |
| 8 camera `follow`, `chase`, `free`, `auto`, `locked` | Pass | Mode read back correctly from `ksp.camera.state` |
| 8 camera `orbit` / `orbital` | Fail (minor) | Reported ORBITAL, reverted to CHASE on the pad (finding 3) |
| 8 look-at | BLOCKED (needs MCP) | Needs a second vessel |
| 8 `shot --save demo` | Pass | `Skizzy/screenshots/2026-11-11/demo.png` |
| 8 in-game Camera window | BLOCKED (needs MCP) | |
| 9 log start/tail/stop/get | Pass | 33 rows, header + rows, UTF-8 BOM for Excel |
| 9 log gate, 100 vessels × 3 fields at 10 Hz, 600 s | Pass | `sampleMs` p50 0.0008, p99 0.092, max 0.14 ms (budget 0.2 mean; the gate reports p50/p99, not the mean); 599,900 rows; the host deleted its gate file afterwards |
| 9 in-game Log picker | BLOCKED (needs MCP) | |
| 10.1–10.3 five scene switches, counts, stale handle | BLOCKED (needs MCP) | Needs scene changes; `ksp.read` of an old handle at the main menu returns `UNAVAILABLE_IN_SCENE` (correct, but not the stale test) |
| 10.4 restart with client connected | Partial (**fail for CLI**) | KSP closed and restarted from the shell. MCP bridge: reconnected (`reconnects 1`). Python client `auto_reconnect=True`: calls waited and resumed (LOADING, then MAINMENU). `skizzy watch` (alt and events): exited with `UNAVAILABLE: disconnected` (finding 2) |
| 11.1 permission prompt | Pass (via MCP) | Approve → `{"confirmed":true}`; Deny → `PERMISSION_DENIED` "the human denied it"; no answer → "confirmation timed out" at 60.0 s; audit lines `ask:granted`/`ask:denied`/`ask:timeout`. The CLI form of the command fails (finding 4). On-top check not reported |
| 11.2 kill switch | Pass | Engaged from the menu: `rec.log.start` → `KILL_SWITCH_ACTIVE`, `ksp.read` works, release over the wire refused ("only the human ... may release"), banner shows who/when + release button; released from the banner |
| 11.2 Ctrl+Alt+K | Environment | Another program on this PC takes Ctrl+Alt+K |
| 11.3 Connections window | Not verified | Opened by the owner (shows "Connections (1)"); details not reported. `host.sessions` shows `skizzy-bridge` / `claude-code`, role commander |
| 11.4 Health window | Pass | Matches `host.selfcheck` (listener, idle mode, budget, Update cost) |
| 11.5 idle with UI closed | Pass | Covered by the §7 runs (main menu, no Skizzy windows) |
| 11.6 console focus | BLOCKED (needs MCP) | |

M0 summary table:

| Item | Expected | Measured | Pass |
|---|---|---|---|
| Spikes | all pass | all 4 pass | yes |
| Idle, nothing connected | pump callbacks constant, slope equal to A | constant (1), wakes 0; slope B −410 KB/s vs A −361 KB/s, gen0 equal | yes |
| Idle, connected | < 0.1 ms/frame p50 | 0.0075 ms | yes |
| hello to ksp.read RTT | < 20 ms p50 | 13.82 ms (hello 43–47 ms in doctor) | yes |
| Screenshot, 1024 px | ≤ 3 ms main thread, counts unchanged | 0.11 ms, unchanged | yes |
| Log, 100 × 3 fields at 10 Hz | ≤ 0.2 ms per FixedUpdate | p50 0.0008, p99 0.092 ms | yes |
| 5 scene switches and a restart | no exceptions, no growth, reconnect, stale handles | restart: MCP and Python reconnect, CLI watch does not; switches blocked | partial |
| Clean install to Claude Code | < 5 min | not timed | not run |
| Codex CLI | same tool list | | not run |
| BDA competition ×3 with and without | same results | | blocked |

## HOOKS checklist (`BDArmory/Overdrive/HOOKS-CHECKLIST.md`)

| Item | Result | Notes |
|---|---|---|
| A1–A2 back up DLL outside GameData | Pass | `E:\dev\backup\BDArmory.dll` |
| A3 back up BDA `settings.cfg` | Not done separately | Covered by the full baseline at `E:\dev\backup\KSP-baseline` (taken before the DLL swap) |
| A4 install Release DLL | Pass | Timestamp and sha256 match the fresh build |
| A5 main menu, no BDArmory exceptions | Pass | Several starts; no BDArmory `Exception` lines |
| A6 rollback | Pass (exercised) | Stock DLL swapped in for M4 B2 and the hooks build restored afterwards (hash verified) |
| B1–B6 regression, old vs new DLL (air, naval, waypoint course) | BLOCKED (needs MCP) | Needs spawned armed teams, competitions and scoring readout |
| C1–C4 multi-spawn | BLOCKED (needs MCP) | Needs Vessel Mover clicks or a spawn capability |
| D Hold / Tight | BLOCKED (needs MCP) | Needs a target scenario plus a way to set `fireAuthority` and assignments (harness or capability) |
| E external assignments | BLOCKED (needs MCP) | As D |
| F custom route | BLOCKED (needs MCP) | As D |
| G patrol engage | BLOCKED (needs MCP) | As D |
| H events | BLOCKED (needs MCP) | As D |

Automated coverage that did run: `Overdrive/tests/OverdriveTests` 122/122 (multi-spawn layout and wrap, budget,
override-lost reasons and others), and the binding contract tests in skizzy-protocol (`BindingContractTests`,
`BdaBindingTests`, part of the 288).

## M4 checklist (`docs/M4-v1-checklist.md`)

| Item | Result | Notes |
|---|---|---|
| 0.1 hooks build at `9fac1f3bc` or later | Pass | `e113c0de5` contains it |
| 0.1 host reports F1–F10 present | Fail (minor) | No per-hook report (finding 5); KSP.log says "all Overdrive hooks" |
| 0.2–0.3 binding and M0 wiring present | Pass | `c2.*` capabilities in the catalog; selfcheck `c2 engine: attached (bdarmory), idle until a c2.* command` |
| 0.4 test scene | BLOCKED (needs MCP) | 15 named craft on two sides need spawn |
| B1 attach | Pass | KSP.log `[Skizzy.BDArmory] C2 binding attached (host engine): BDArmory 1.13.1.0, all Overdrive hooks`; host.log `c2: engine attached (bdarmory)`; no exceptions |
| B2 stock BDArmory | **Fail** | See below |
| B3 window and unit list | Partial | "C2 command" item is in the Skizzy menu, no second toolbar button. Unit list, classes and group selection blocked (needs craft) |
| B4–B8, B10–B13 | BLOCKED (needs MCP) | Need armed units, orders in flight, vessel switching |
| B9 restore across scenes | Partial | On the owner's revert: `the host dropped the C2 engine (scene_change); hook settings restored.` then re-attach. With no orders active, the restored-values part is untested |
| Rows 3–28 | BLOCKED (needs MCP) | Need the test scene. The FakeBda scenario tests (`Skizzy.C2.Tests`, 90 pass) cover the rows headless |
| Row 12 / 25 / 26 / 27 / 28 | BLOCKED (needs MCP) | As above |

### M4 B2: Skizzy.BDArmory.dll fails to load on stock BDArmory (fail)

Setup: KSP closed, `GameData\BDArmory\Plugins\BDArmory.dll` replaced with the CKAN stock DLL
(`BDArmory-Overdrive 2:v1.13.1.0-overdrive.1` package, sha256 `b70b1ded…`), KSP started to the main menu, then the
hooks build restored.

Expected (checklist and `scripts/copy-refs.ps1`: "the same Skizzy.BDArmory.dll also loads on a stock BDArmory and
refuses the orders that need a missing hook (HOOK_MISSING)"): the binding logs `no Overdrive hooks (stock BDArmory)`
and engage orders are refused per unit; no `TypeLoadException`.

Observed: the assembly loads (`AssemblyLoader: KSPAssembly 'Skizzy.BDArmory' V0.1.0`) but every `Assembly.GetTypes()`
on it throws. KSPCommunityFixes catches it 7 times during startup:

```
[WRN 15:09:48.468] [KSPCF] A ReflectionTypeLoadException thrown by Assembly.GetTypes() has been handled by KSP Community Fixes.
This is usually harmless, but indicates that the "Skizzy.BDArmory" plugin failed to load (location: "GameData\SkizzyProtocol\Plugins\Skizzy.BDArmory.dll")
Loader Exceptions:
  Could not load type of field 'Skizzy.Bda.Ksp.HookF1+<>O:<0>__OnLost' (0) due to: Could not resolve type with token 01000043 (from typeref, class/assembly BDArmory.Control.OverrideLostReason, BDArmory, Version=1.13.1.0, Culture=neutral, PublicKeyToken=null)
Stacktrace:
  at AssemblyLoader+LoadedAssembly.TypeOperation (System.Action`1[T] action)
  at AssemblyLoader+LoadedAssembyList.TypeOperation (System.Action`1[T] action)
  at Scatterer.ShaderReplacer.getType (System.String name)
  at Scatterer.ShaderReplacer.replaceEVEshaders ()
  at Scatterer.Scatterer.Awake ()
```

Cause: `HookF1` subscribes a handler by method group (`... += OnLost`). The C# compiler caches that delegate in a
generated static field `HookF1.<>O.<0>__OnLost` whose type mentions `OverrideLostReason`, a type that exists only in
the Overdrive build. A type with a field of an unresolvable type cannot be loaded, so `GetTypes()` on the whole
assembly fails. That breaks every mod that scans all assemblies (Scatterer above; without KSPCF the exception is not
caught). The hook classes are meant to be isolated so that only the probe decides; the compiler-generated cache defeats that.

Likely fix (not applied): avoid the method-group conversion in hook classes (e.g. subscribe with a lambda inside a
method that only runs after the probe, or build the delegate by reflection), or move the typed hook code into a
second assembly that is only loaded when the probe finds the hooks. Add a test that loads Skizzy.BDArmory.dll
against a stock BDArmory reference and calls `GetTypes()`.

Not tested (blocked, needs a flight scene): whether the Flight addon still starts in this state, the
`no Overdrive hooks (stock BDArmory)` log line, `HOOK_MISSING` refusals, and stock fly-to for a ship. `c2.order.issue`
at the main menu returns `UNAVAILABLE_IN_SCENE` (correct).

## M3 checklist (`docs/M3-checklist.md`)

| Item | Result | Notes |
|---|---|---|
| A0.1 build | Pass | `dotnet build Skizzy.Map.slnx -c Release`: 0 warnings, 0 errors |
| A0.2 render tests | Pass | 88/88 |
| A0.3 app tests | Pass | 51/51 (run directly, xunit v3) |
| A0.4 perf.txt | Pass | Render core 5000 tracks @1920×1080: all-frame median 6.25 ms, p95 6.60 (160 fps median). Whole app headless: 5.0 ms (200 fps), plot draw 4.9 ms; with software compositor 11.6 ms (86 fps) |
| A0.5 samples | Pass | 8 PNGs; 5 pixel-identical to `docs/map-app-samples/`, the other 3 differ by a mean of 0.15–0.56 per channel |
| S1 window | Pass (partial) | Opens 1440×900 centred (x 552 on a 2560 screen), dark theme, layout as the mockup. Fonts not inspected closely |
| S2–S5 | BLOCKED (needs MCP) | Need clicks, resizing, monitor changes |
| T1–T2, D1–D8 | D1 pass; rest BLOCKED | D1 default layout seen in screenshots; others need interaction |
| DS1 demo | BLOCKED | Not opened interactively (bench demo showed 870 of 1013 tracks on screen later in the run) |
| DS2 FakeKSP | Pass (partial) | `--source fake`: "FakeKSP (in-process)", the 5 vessels (2 × DDG, FFG-3, F-14 Tomcat, Tanker) and contact messages. Host-health and Unit panel details need clicks |
| DS3 | Pass (partial) | `skizzy-map-fake-*` temp folder removed when the app closed; switching sources in the UI not tested |
| L1 live | **Pass** | `--source live` with KSP in flight: "Live host (KSP)", track GDLV3 (PRO, 090), messages "Live host: Connected - reading the catalog", "New vessel: GDLV3 (Probe, prelaunch)", UT clock ticking |
| L2 token | Pass | Token string absent from `settings.json`, `session-layout.json` and stdout |
| L3 non-flight scene | BLOCKED (needs MCP) | Needs a scene change |
| P-1 to P-8, U-1 to U-7, R-1 to R-4, C-1 to C-7, §9 keys | BLOCKED (needs MCP) | Need mouse and keyboard in the app |
| P1 desktop bench | Pass | 2560×1417 window, scale 1. 5000 tracks: **app 130.0 fps, plot 129.6 fps**, plot draw median 5.29 ms. Demo raid: 128.1 / 128.1 fps, 1.87 ms. `--plot-sync`: 64.9 / 64.7 fps, 5.16 ms. All above the reference (70–73 UI, 42–46 plot); plot thread about 2× the old path |
| P1b, P2, P3 | BLOCKED / not run | Need interaction; P3 (30 min soak) not run |
| LIC-1 | Pass | All 35 packages in `obj/project.assets.json` are covered by THIRD-PARTY-NOTICES.md rows (16 by family rows such as "plus Avalonia.Win32, .X11, ..."); licences MIT, BSD-3-Clause, Apache-2.0, OFL; the only GPL mention is "None is GPL, LGPL, AGPL, or non-commercial" |
| LIC-2 | Pass | `Licences/Fonts/OFL-IBMPlexMono.txt`, `OFL-IBMPlexSansCondensed.txt`, `OFL-SairaSemiCondensed.txt` |

## Deviations from the setup steps

- `E:\ksp-personal` and the baseline backup are outside `$KSP`, `E:\CKAN-cache` and `E:\dev`; the owner approved
  following the steps. The baseline was then moved from D: to `E:\dev\backup\KSP-baseline` (owner: backups go on the HDD).
- Three extra CKAN packages (Scatterer-config, Scatterer-sunflare, WildBlue-PlayMode-CRP), approved by the owner.
- Steps 8 tools installed before Part B, because Part B needs `gh`.
- .NET Framework 4.8 reference assemblies from NuGet (`E:\dev\_LocalDev\net48refs`) instead of the admin-only
  Developer Pack; Overdrive tests run with `DOTNET_ROLL_FORWARD=Major`.
- `scripts\copy-refs.ps1` run before `package.ps1`.
- Python client installed in a venv at `E:\dev\_LocalDev\venv`, not in the global Python.
- The idle runs were done at the main menu (no save could be loaded without the owner), with the skizzy MCP bridge
  process stopped so that nothing was connected.

## State left on the PC

- KSP closed. `GameData\BDArmory\Plugins\BDArmory.dll` = hooks build (`31175cc7…`); Skizzy 0.1.0 installed;
  `settings.cfg` back to `listen = true`, `idleProbe = false`.
- Backups: `E:\dev\backup\BDArmory.dll` (stock), `E:\dev\backup\KSP-baseline` (pre-Part C copy of the whole game folder).
- Idle-probe CSVs: `E:\dev\test-artifacts\2026-11-11\idle-probe-A.csv` and `idle-probe-B.csv`.
- The skizzy MCP server is registered with Claude Code for the KSP folder (local scope).
