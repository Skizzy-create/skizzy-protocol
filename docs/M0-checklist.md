# M0 "First contact": in-game checklist

This is the manual part of the M0 acceptance (`design/17-milestones.md`, M0, items 1 to 8). The automated part runs in CI (`ci.slnf`: protocol, host core on FakeKsp with allocation asserts, CLI/MCP bridge, Python client against FakeHost). Everything below needs a running KSP 1.12 and has not been run yet.

`<KSP>` means your KSP folder (the one with `KSP_x64.exe` and `GameData`). Commands are PowerShell. `skizzy` means `& "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe"`. You can put that folder on `PATH` or define a function: `function skizzy { & "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" @args }`.

When a step fails, keep going if you can and send back the files listed in [section 12](#12-what-to-send-back).

## 1. Build and install

1. Point the build at your game. Either set `$env:KSPDIR = "<KSP>"`, or copy `Directory.Build.props.user.example` to `Directory.Build.props.user` and edit it (that file is git-ignored).
2. Build the package: `.\scripts\package.ps1`. Expected result: `artifacts\GameData\SkizzyProtocol\` containing `Plugins\` (Skizzy.Host.dll and Skizzy.Protocol.dll, the net48 build of the protocol library), `Bin\skizzy.exe`, the version file, LICENSE and NOTICE. The build fails on purpose if mscorlib, System or System.Core would resolve from anywhere other than `<KSP>\KSP_x64_Data\Managed`.
3. Dry run: `.\scripts\install.ps1 -DryRun`. Expected: a list of "create dir" and "copy file" lines (8 actions on a clean install), all under `<KSP>\GameData\SkizzyProtocol\`, and nothing written. Check that nothing outside that folder appears.
4. Install: `.\scripts\install.ps1`. The script refuses to run while KSP is running unless you pass `-Force`. It never touches `PluginData\`.
5. To remove everything later: `.\scripts\uninstall.ps1 -DryRun`, then `.\scripts\uninstall.ps1`. It keeps `PluginData\` (token, settings, audit) unless you add `-RemoveData`, and keeps the `<KSP>\Skizzy\` logs unless you add `-RemoveLogs`.

## 2. First start

1. Start KSP and wait for the main menu.
2. Check `<KSP>\GameData\SkizzyProtocol\PluginData\`. It should contain `auth.token`, `endpoint.json`, `settings.cfg`, `permissions.cfg`, `host.log` and `audit\`.
3. `host.log` starts with `Skizzy host 0.1.0 starting: KSP 1.12.x ..., Unity 2019.4.18f1 ...` followed by `listening on ws://127.0.0.1:<port>/skizzy`.
4. KSP.log should contain no `[Skizzy]` lines (only warnings and errors are copied there). Any `[Skizzy]` line is a finding: send it back.
5. Load a save and go to the Space Center. The first-run wizard opens. Step through it and press Finish (this writes `firstRunDone = true` in `settings.cfg`). "Later" hides it until the next start.
6. The Skizzy toolbar button (a ring with a crosshair) appears in the Space Center, flight, map and tracking station. Clicking it opens the menu.

## 3. Spikes (run these first)

Go to flight with any vessel on the launch pad or runway, then run:

```powershell
skizzy call host.debug.spike name=all          # returns {"op": "op-...", "state": "running"}
skizzy call op.wait op=<the op id> timeoutS=120 --timeout 150 --json
```

`op.wait` returns early when the operation finishes; `--timeout` is the CLI-side limit (default 60 s). You can also run one spike at a time with `name=nostdlib|sync_context|jpg_thread|async_readback`. The last result also appears as the "M0 spikes" line of `skizzy call host.selfcheck`.

| Spike | Pass when | Expected numbers | If it fails |
|---|---|---|---|
| `nostdlib` | `forbiddenReferences` is empty and `dynamicMethod` is true | `mscorlib` 4.0.0.0; every reference has `resolvedFrom: "Managed"` or a GameData path; `unity` 2019.4.18f1 | Send the JSON. A false `dynamicMethod` means the field readers fall back to boxed reflection: slower, but they still work. |
| `sync_context` | 20 of 20 posts ran and `framesMax` is 2 or less | `mode: "sync_context"`, `latencyMsP50` about one frame (under 20 ms at 60 fps) | `mode: "polling"`: idle wake falls back to one volatile read per frame while idle. Acceptance 1 still holds, but report it. |
| `jpg_thread` | no error, `identicalToMainThread` true, every worker encode finished | `workerMsP50` similar to `mainThreadEncodeMs` (a few ms at 1024 px) | Set `jpegOnWorkerThread = false` in `settings.cfg` (KSP closed) and report it. Encoding then runs on the main thread. |
| `async_readback` | `supported` true and the capture succeeded | `graphicsApi` Direct3D11; `capture.pipeline` `async_readback` with `mainThreadMs` 3 or less; `fallbackCapture.mainThreadMs` 30 or less | `supported` false: screenshots use ReadPixels (budget 30 ms). Send the JSON. |

Screenshot orientation: `skizzy shot --out shot.jpg`, then open the file. If it is upside down, set `captureFlip = true` (or `false`, whichever is the opposite of the `flip` value in the `async_readback` result) in `settings.cfg` with KSP closed. The default is `auto`.

## 4. Claude Code connection (acceptance 7, target under 5 minutes)

Start a stopwatch at step 1 of section 1 on a clean install (`uninstall.ps1` first), following only the README.

1. `skizzy mcp --print-config` prints the exact registration lines with your path.
2. `claude mcp add skizzy -- "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" mcp`
3. Start `claude`, run `/mcp`. Expected: `skizzy` connected, with the core tool list (it includes `ksp_read`, `ksp_query`, `ksp_vessels_list`, `ksp_camera_screenshot`, `rec_log_start`, `rec_log_stop`, `host_kill_switch`, `catalog_search`, `catalog_describe`, `cap_call`, `skizzy_status`, `skizzy_wait`, `skizzy_cancel`). The list is the same with the game closed; `skizzy_status` then says the host is not running.
4. Demo prompt (in flight): "Read the active vessel, take a chase screenshot, then log altitude, srfSpeed and verticalSpeed at 5 Hz to CSV for 30 seconds and tell me the file path." Open the CSV in Excel.
5. Note the elapsed time. Pass: under 5 minutes.

## 5. Codex CLI connection (acceptance 7, R2)

1. Add the block printed by `skizzy mcp --print-config` to `~/.codex/config.toml`:
   ```toml
   [mcp_servers.skizzy]
   command = "<KSP>\\GameData\\SkizzyProtocol\\Bin\\skizzy.exe"
   args = ["mcp"]
   ```
2. Start `codex` and list the MCP tools. Pass: the same tool names as in Claude Code.
3. Run the demo prompt from section 4, step 4.

## 6. Reading, RTT and the four front ends (acceptance 2)

In flight:

```powershell
skizzy doctor                         # PASS lines; "connect + hello" should be well under 100 ms
skizzy call ksp.read vessel=active
skizzy call ksp.vessels.list
skizzy call ksp.query fields='["altitude","srfSpeed"]'
skizzy call ksp.module.describe ...   # pick a module handle from ksp.read depth=2
skizzy call ksp.mods.list
skizzy watch altitude srfSpeed --vessels active --rate 5 --duration 10
skizzy watch events --duration 30     # stage, switch vessel or pause to see vessel.* and sim.* events
```

RTT measurement with the Python client (`python -m pip install .\clients\python`):

```python
import time, statistics, skizzy
sk = skizzy.connect()
t = []
for _ in range(200):
    t0 = time.perf_counter(); sk.read("active"); t.append((time.perf_counter() - t0) * 1000)
print("p50 %.2f ms  p99 %.2f ms" % (statistics.median(t), sorted(t)[int(len(t) * 0.99) - 1]))
```

Expected: p50 under 20 ms in flight. The automated FakeKsp test asserts the same bound; in game, a `ksp.read` additionally waits for the next frame's main-thread slot (about 16 ms at 60 fps at worst).

In-game console: open the Skizzy menu, then Console. Run `ksp.read`, `ksp.query` and `rec.log.start` from there. Pass: same results as from the CLI.

## 7. Idle cost and heap slope (acceptance 1 and 8)

Two 10-minute runs, same save, same scene (for example the Space Center or a vessel sitting on the runway), game window focused, no client connected:

| Run | settings.cfg (KSP closed) | What it measures |
|---|---|---|
| A, baseline | `listen = false`, `idleProbe = true` | host loaded, no listener, no sockets |
| B, normal | `listen = true`, `idleProbe = true` | host listening, nothing connected |

Each run writes `PluginData\idle-probe.csv` (rename it after each run) with the columns `wallUtc,elapsedS,listen,sessions,pumpAwake,pumpUpdates,pumpFixedUpdates,wakes,gcTotalBytes,gen0,gen1,gen2`. On exit, `host.log` gets `idle probe: N samples over T s, heap slope X B/s, ...`.

Pass:
- In both runs, `pumpAwake` is false and `pumpUpdates` and `pumpFixedUpdates` stay constant once the 5-second grace period after the last activity has passed. That means Unity calls none of our callbacks while idle (0 ms per frame).
- The heap slope of run B is within the normal variation of run A (run each twice if they differ).
- `skizzy call host.selfcheck` (run it at the end, which wakes the host) shows "plugin footprint: OnFlyByWire callbacks 0, Harmony patches 0 ..." and "GameEvents handlers" as pass. It lists 2 scene handlers and 2 toolbar handlers registered at all times. The 16 vessel and time handlers are registered only while a client is connected.

Connected-idle gate (a client connected but not calling):

```powershell
skizzy call host.selfcheck.perf gate=idle durationS=60
skizzy call op.wait op=<id> timeoutS=120 --timeout 150 --json
```

Pass: `measuredMsPerFrameP50` under 0.1.

Set `idleProbe = false` and `listen = true` again afterwards.

BDA check (acceptance 1): run the same BDArmory competition 3 times with the plugin installed and nothing connected, then 3 times after `uninstall.ps1`. Pass: the same results within normal variance. Plain BDArmory guard mode must behave identically.

## 8. Screenshots and camera (acceptance 3 and 8)

In flight:

```powershell
skizzy call host.selfcheck.perf gate=screenshot captures=100
skizzy call op.wait op=<id> timeoutS=300 --timeout 330 --json
skizzy call host.selfcheck textures=true     # before and after: Texture2D/RenderTexture counts
```

Pass: `mainThreadMs` p50 3 ms or less (30 ms or less if the pipeline is `readpixels`), `failures` 0, and `objectCountsUnchanged` true (100 captures leave the Texture2D and RenderTexture counts unchanged; pooled objects are excluded and freed after 60 s).

Camera modes, each followed by `skizzy shot --out <mode>.jpg`:

```powershell
skizzy shot --mode follow --out follow.jpg
skizzy shot --mode orbit --out orbit.jpg
skizzy shot --mode chase --out chase.jpg
skizzy shot --mode free --out free.jpg
skizzy call ksp.camera.set_mode mode=look_at target=<vessel A> to=<vessel B>
skizzy shot --out lookat.jpg
skizzy shot --save demo                      # ksp.camera.save: writes <KSP>\Skizzy\screenshots\<date>\demo....
```

Pass: five images, each correctly oriented and showing the expected framing; look-at keeps vessel B in view from behind vessel A. The Camera window in game shows the same modes.

## 9. Log v0 (acceptance 4 and 8)

```powershell
skizzy log start climb --fields altitude,srfSpeed,verticalSpeed --rate 5 --format csv --vessels active
skizzy log tail climb
skizzy log stop climb
skizzy log get climb --out climb.csv          # open in Excel: header row plus one row per sample
skizzy call host.selfcheck.perf gate=log vessels=100 durationS=600
skizzy call op.wait op=<id> timeoutS=300 --timeout 330 --json   # repeat until state is done
```

Pass: `sampleMs` mean 0.2 ms or less per FixedUpdate. On FakeKsp the same synthetic gate measures 0.03 ms. The CSV opens in Excel.

In-game picker: Skizzy menu, then Recorder, then **Log...**. Pick the active vessel and the same three fields at 5 Hz. The window shows the equivalent `skizzy log start ...` command. Pass: it builds the same selector as the CLI command above (same `--vessels` value and fields), and the resulting file has the same columns.

## 10. Scene switches and restart (acceptance 5)

With `skizzy watch altitude --vessels active --rate 1` running in one window and `skizzy watch events` in another:

1. Switch scenes five times: flight, Space Center, tracking station, flight, VAB, flight.
2. Pass: no exceptions in KSP.log, `[Skizzy]` warnings at most for expected stale handles, and `skizzy call host.selfcheck textures=true` object counts back to their first value after the last switch.
3. An old vessel handle (copy one from `ksp.vessels.list` before switching) returns `NOT_FOUND` with reason `stale`: `skizzy call ksp.read vessel=<old handle>`.
4. Quit KSP with a client connected (`skizzy watch ...` or Claude Code), then start it again. Pass: the client reconnects by itself, and the `watch` window resumes or prints a resync line.

## 11. In-game UI and safety

1. Permission prompt: `skizzy call host.debug.confirm_test note="checklist"`. A prompt window appears in game, on top of the other windows. Approve: the call returns `{"confirmed": true}`. Deny: `PERMISSION_DENIED` ("the human denied it"). Ignore it for 60 s: `PERMISSION_DENIED` ("confirmation timed out"). Each case gets a line in `PluginData\audit\`.
2. Kill switch: Skizzy menu, then ENGAGE KILL SWITCH. A banner appears. `skizzy call rec.log.start ...` is refused, and `skizzy call ksp.read` still works. Release from the banner. With a client connected, Ctrl+Alt+K toggles it too (the hotkey is only checked while a client is connected).
3. Connections window: shows the session (client name, actor, age, calls). Disconnect works. "Regenerate token..." asks for confirmation, then disconnects all clients; they reconnect by reading the new token file. The token itself is never shown.
4. Health window: the self-check lines match `skizzy call host.selfcheck`.
5. With every window closed and nothing connected, the probe in section 7 still shows `pumpUpdates` constant (the UI component is disabled then).
6. Typing in a console text field does not trigger KSP key bindings (for example staging with space). Clicking outside the window gives the keyboard back to the game.

## 12. What to send back

- `<KSP>\KSP.log` (KSP writes it in the KSP folder; it is overwritten at each start, so copy it right after the session).
- `%USERPROFILE%\AppData\LocalLow\Squad\Kerbal Space Program\Player.log` (Unity's log, for crashes).
- `<KSP>\GameData\SkizzyProtocol\PluginData\host.log` (rotated as `host.log.1` and so on).
- `PluginData\idle-probe.csv` from runs A and B (renamed), and the `idle probe:` lines from `host.log`.
- The JSON of: `host.debug.spike name=all`, the three `host.selfcheck.perf` gates, and `host.selfcheck textures=true` before and after the 100 captures.
- `skizzy doctor --json`.
- The screenshots from section 8 and the CSV from section 9.
- Do not send `auth.token`.

Summary table to fill in:

| Item | Expected | Measured | Pass |
|---|---|---|---|
| Spikes: nostdlib / sync_context / jpg_thread / async_readback | all pass | | |
| Idle, nothing connected | pump callbacks constant, slope equal to A | | |
| Idle, connected | < 0.1 ms/frame p50 | | |
| hello to ksp.read RTT | < 20 ms p50 | | |
| Screenshot, 1024 px | <= 3 ms main thread (<= 30 ms fallback), counts unchanged after 100 | | |
| Log, 100 x 3 fields at 10 Hz | <= 0.2 ms per FixedUpdate | | |
| 5 scene switches and a restart | no exceptions, no growth, reconnect, stale handles | | |
| Clean install to Claude Code | < 5 min | | |
| Codex CLI | same tool list | | |
| BDA competition x3 with and without | same results | | |
