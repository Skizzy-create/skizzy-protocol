# skizzy-protocol

skizzy-protocol connects AI agents and scripts to Kerbal Space Program 1.12 with BDArmory. It has two parts:

- An in-game host plugin. It serves WebSocket JSON-RPC on `127.0.0.1` (ports 47549-47559) and requires a token.
- `skizzy.exe`, a command-line tool that is also a stdio MCP server for Claude Code, Codex CLI or any other MCP client. A small Python client is included.

This is milestone M0, "First contact". It covers:

- reading vessels, parts and modules;
- queries and streams, and events;
- screenshots and camera control;
- CSV/JSONL logging (log v0);
- permissions with in-game prompts, an audit log and a kill switch.

Principle: with nothing connected, the plugin costs practically nothing and BDArmory works exactly as before.

Nothing in this repository has been run inside KSP yet. See [Status](#status).

## Requirements

- Kerbal Space Program 1.12.x on Windows.
- BDArmory is optional for the M0 read features.
- Python 3.9 or newer (`clients/python/pyproject.toml`), for the Python client only.
- The .NET 10 SDK (`global.json`: 10.0.400 or a later feature band), only to build from source. The packaged `skizzy.exe` is self-contained and needs no .NET runtime.

## Quickstart

### 1. Get the package

Build the zip with `scripts/package.ps1` (see [Build from source](#build-from-source)). It writes:

- `artifacts/GameData/SkizzyProtocol/` (the folder to install), and
- `artifacts/skizzy-protocol-<version>.zip` (the same folder under `GameData/SkizzyProtocol/`).

The package holds `Plugins/` (`Skizzy.Host.dll`, `Skizzy.Protocol.dll`), `Bin/skizzy.exe`, `LICENSE`, `NOTICE` and `skizzy-protocol.version`. It does not ship `PluginData/`.

### 2. Install

Close KSP first. In PowerShell, from the repository root, preview the install:

```powershell
.\scripts\install.ps1 -KspDir "<KSP>" -DryRun
```

Then install:

```powershell
.\scripts\install.ps1 -KspDir "<KSP>"
```

`-KspDir` defaults to the `KSPDIR` environment variable. Other parameters: `-Source <folder>` (use an unzipped package instead of `artifacts/`) and `-Force` (install while KSP runs).

The script writes only inside `<KSP>\GameData\SkizzyProtocol\`. It never touches `PluginData/`. It warns about other copies of the same `Skizzy*.dll` files elsewhere under `GameData`, because two copies conflict in KSP.

You can also unzip the archive into `<KSP>` by hand, so that `GameData/SkizzyProtocol/` ends up under it.

### 3. Start KSP

The host starts with the game and creates `PluginData/auth.token` and `PluginData/endpoint.json` under `<KSP>\GameData\SkizzyProtocol\`. You do not need to load a save to connect, but vessel reads need a scene that has vessels (flight or tracking station).

On the first Space Center or flight scene the host opens a first-run wizard (`design/21-user-experience.md` §1): principle text, token path with copy-command buttons, permission preset, port status and a doctor summary. Finish stores `firstRunDone = true` in `settings.cfg`; "Later" hides it until the next start. The Skizzy toolbar button (Space Center, flight, map, tracking station) opens a menu with the wizard, the connections, permissions, console, recorder, camera and health windows, and the kill switch. The quickstart below does not depend on the wizard.

### 4. Connect Claude Code

```powershell
claude mcp add skizzy -- "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" mcp
```

`skizzy.exe mcp --print-config` prints this command with the real path of the exe you run. The install script prints it too.

The bridge needs no flags in the normal case. It finds the host by itself (see [Endpoint discovery](#endpoint-discovery)).

### 5. Check the connection

```powershell
& "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" status
& "<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe" doctor
```

`status` works with the game closed and says whether a host was found. `doctor` is read-only. It prints `PASS`, `WARN`, `FAIL` or `INFO` lines for the install layout, token, endpoint, port, versions and the host's own checks. `doctor --fix` is not available in this version.

Exit codes for every command: 0 ok, 1 host error, 2 usage, 3 not connected.

### 6. First prompts

Ask Claude Code, with a vessel loaded:

1. "Read the active vessel and summarise its state."
2. "Take a chase-camera screenshot of the active vessel."
3. "Log altitude, srfSpeed and verticalSpeed of the active vessel at 5 Hz to CSV, stop after a minute, and show me the last ten lines."

The third is the M0 demo. The same steps from the shell:

```powershell
skizzy call ksp.read vessel=active
skizzy shot --mode chase --out chase.png
skizzy log start demo --fields altitude,srfSpeed,verticalSpeed --rate 5 --vessels active
skizzy log tail demo --lines 10
skizzy log stop demo
skizzy log get demo --out demo.csv
```

`skizzy` here means `<KSP>\GameData\SkizzyProtocol\Bin\skizzy.exe`. The log is written to `<KSP>/Skizzy/logs/<date>/demo.csv`.

## Codex CLI and other MCP clients

`skizzy mcp --print-config` prints three blocks. With a packaged exe they look like this:

```
Claude Code:
  claude mcp add skizzy -- "<path to>\skizzy.exe" mcp

Codex CLI (~/.codex/config.toml):
  [mcp_servers.skizzy]
  command = "<path to>\\skizzy.exe"
  args = ["mcp"]

Any other MCP client: stdio command "<path to>\skizzy.exe" mcp
```

Put the Codex block in `~/.codex/config.toml`. For any other client, register the stdio command `skizzy.exe mcp`. The tool list is static, so every client sees the same tools even with the game closed.

`mcp` options (from `skizzy mcp --help`):

```
skizzy mcp [--toolset core] [--actor <id>] [--role commander|advisor|observer|delegate] [--side <side>] [--actor-token <t>]
           [--client-name <name>] [--max-text <bytes>] [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>]
           [--print-config]
```

The default role is `commander`. The default `--max-text` is 8192 bytes.

## CLI

`skizzy help` lists the commands: `call`, `mcp`, `status`, `version`, `watch`, `log`, `shot`, `doctor`. `skizzy <command> --help` prints the usage. Examples:

```powershell
# Call any capability. Values are JSON when they parse, else strings; a.b=1 nests.
skizzy call ksp.vessels.list
skizzy call ksp.read vessel=active format=compact
skizzy call catalog.search q=log
skizzy call ksp.vessels.list --json          # whole result including meta

# Stream fields live (Ctrl+C, --count N or --duration S stops it)
skizzy watch altitude srfSpeed --vessels "DDG*" --rate 2
skizzy watch altitude --vessels active --onchange --deadband 0.5
skizzy watch events "vessel.*" --duration 30

# Log to CSV or JSONL
skizzy log start climb --fields altitude,srfSpeed --rate 5 --format csv --vessels active
skizzy log list
skizzy log tail climb --lines 20
skizzy log stop climb
skizzy log get climb --out climb.csv

# Screenshots
skizzy shot --out shot.png --width 1024 --format png
skizzy shot --target "DDG-1" --mode chase --save chase1   # host saves under Skizzy/screenshots/<date>/

# Install and connection checks
skizzy status
skizzy doctor --json
```

Connection options, accepted by every command: `--ksp <KSP folder>`, `--endpoint <endpoint.json>`, `--token-file <file>`. Also `--actor`, `--role` and `--side`. Without `--vessels`, `log start` logs every vessel.

### Endpoint discovery

The first matching rule wins (`src/Skizzy.Client/SkizzyClient.cs`, `Endpoint.Discover`):

1. `--endpoint <endpoint.json>`: use the PluginData folder that contains it.
2. `--ksp <KSP folder>`: use `<KSP>\GameData\SkizzyProtocol\PluginData`.
3. Environment variable `SKIZZY_ENDPOINT` (path to an `endpoint.json`).
4. Environment variable `KSPDIR`.
5. The `PluginData` folder next to the exe (`..\PluginData` relative to `Bin\`), if it holds `auth.token`.
6. The per-user pointer `%LOCALAPPDATA%\skizzy-protocol\endpoint.json`, which the running game writes.

`--token-file` overrides the token path in any of these cases. The Python client uses the same order, minus rule 5 (see below).

## Python client

The client is a synchronous package in `clients/python/`. It depends on `websockets>=12`. It is not on PyPI yet, so install from the checkout:

```powershell
python -m pip install .\clients\python
```

```python
import skizzy

with skizzy.connect() as sk:                      # finds the host like the CLI does
    print(sk.state()["scene"])
    print(sk.read("active")["fields"]["srfSpeed"])
    sk.screenshot("shot.png", format="png")
    sk.log_start("climb", ["altitude", "srfSpeed"], rate_hz=5)
```

Other calls: `sk.vessels()`, `sk.query(fields, selector)`, `sk.subscribe(fields, rate_hz=...)`, `sk.log_stop(name)`, `sk.logs()`, `sk.log_tail(name)`, `sk.events_wait(...)`, and `sk.call("any.capability", **params)`. Errors raise `skizzy.SkizzyError` (`kind`, `reason`, `hint`). `connect()` also accepts `ksp=`, `endpoint_file=`, `token_file=`, `url=` with `token=`, and `auto_reconnect=True`. See `clients/python/README.md`.

## Try it without KSP

`tools/Skizzy.FakeHost` runs the same host code against a simulated world with a few vessels. It needs only the .NET SDK.

```powershell
dotnet run --project tools/Skizzy.FakeHost -- --dir "$env:TEMP\skizzy-fake" --port 0
```

It prints one JSON line (url, port, paths; never the token) and runs until Ctrl+C. Options:

```
skizzy-fakehost [--dir PATH] [--port N|0] [--vessels N] [--auto-answer allow|deny] [--stdin-exit] [--verbose]
```

Point the CLI at the `--dir` folder, which plays the role of the KSP folder:

```powershell
dotnet run --project src/Skizzy.Cli -- status --ksp "$env:TEMP\skizzy-fake"
dotnet run --project src/Skizzy.Cli -- call ksp.vessels.list --ksp "$env:TEMP\skizzy-fake"
dotnet run --project src/Skizzy.Cli -- shot --out fake.png --ksp "$env:TEMP\skizzy-fake"
```

The simulated vessels are synthetic. Output from FakeHost says nothing about how the game behaves.

## Where files go

Plugin state, written by the host, under `<KSP>\GameData\SkizzyProtocol\PluginData\`:

| File | Content |
|---|---|
| `auth.token` | the connection token (never shown by `status` or `doctor`, which print only its length) |
| `endpoint.json` | URL and process id of the running host |
| `permissions.cfg` | permission preset and rules |
| `settings.cfg` | ports, `killSwitchKey`, prompt timeout and similar |
| `host.log` | host log |
| `audit/` | audit log, JSONL |

User data, under `<KSP>\Skizzy\`:

| Folder | Content |
|---|---|
| `logs/<yyyy-MM-dd>/` | CSV/JSONL logs |
| `screenshots/<yyyy-MM-dd>/` | screenshots saved with `--save` |

The running game also writes a per-user pointer to `%LOCALAPPDATA%\skizzy-protocol\endpoint.json`. It tells the CLI where `PluginData` is.

## Safety

- **Local only.** The host listens on `127.0.0.1`. Connections without the token, with a wrong token, or with a browser `Origin` header are refused (`design/15-security-permissions.md` §4, §8; automated tests in `tests/Skizzy.Host.Tests/SecurityTests.cs`).
- **Token.** It lives in `PluginData/auth.token` and is read from disk by local tools. No capability returns it. Do not copy it into prompts, issues or logs.
- **Permissions.** Calls have a safety level: `read`, `control`, `destructive`, `cheat` or `unsafe`. The default preset is Battle Lab: `read`, `control` and `cheat` are allowed, `destructive` asks the player, `unsafe` is denied. Other presets: Test Director, Full Combat, Observer. The preset is set in `permissions.cfg`. A call that needs approval waits for an answer in the game and fails if nobody answers within `confirmTimeoutS` (60 s by default). Clients can request a prompt but cannot change permissions over the wire.
- **Kill switch.** Engage it from the Skizzy toolbar menu at any time, or with Ctrl+Alt+K while a client is connected (the hotkey is only polled while the host is awake, so it costs nothing when idle). Change the key with `killSwitchKey` in `settings.cfg` (for example `ctrl+alt+k`). While it is engaged, non-read calls are denied and clients are read-only; a banner shows who engaged it and has a release button. Pending permission prompts appear in the Permissions window.
- **Audit.** Every call, allowed or not, is recorded as JSONL in `PluginData/audit/`.
- **File access.** Clients can read only under `<KSP>/Skizzy/logs` and `<KSP>/Skizzy/screenshots`.

## Uninstall

```powershell
.\scripts\uninstall.ps1 -KspDir "<KSP>" -DryRun
.\scripts\uninstall.ps1 -KspDir "<KSP>"
```

By default this removes `Plugins/`, `Bin/`, `LICENSE`, `NOTICE` and the version file from `GameData\SkizzyProtocol\`. It keeps `PluginData/`, `<KSP>\Skizzy\` and the per-user pointer. Options:

- `-RemoveData`: also delete `GameData\SkizzyProtocol\` including `PluginData/`, and the per-user pointer.
- `-RemoveLogs`: also delete `<KSP>\Skizzy\` (logs and screenshots).
- `-Force`: run while KSP is running.

Skizzy writes nothing into saves, so removing it cannot damage a save. Unregister the MCP server separately, for example `claude mcp remove skizzy`.

## Build from source

You need the .NET 10 SDK and PowerShell. The in-game projects (`src/Skizzy.Protocol.Ksp`, `src/Skizzy.Host`) compile against KSP's own `KSP_x64_Data\Managed` folder, so they need a KSP 1.12 install. Everything else builds without KSP. Give the build the KSP location in one of two ways:

- Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` and set `<KSPDIR>`. The file is git-ignored.
- Set the `KSPDIR` environment variable.

Build and test without KSP (these projects need no game files):

```powershell
dotnet build ci.slnf -c Release
dotnet test ci.slnf -c Release --no-build
dotnet run --project tools/Skizzy.SpecExport -c Release --no-build -- --check   # schemas and catalog in sync
```

`ci.slnf` holds every project that builds without the game; CI (`.github/workflows/ci.yml`) runs exactly these steps plus the Python tests. `Skizzy.slnx` holds all projects, including the in-game ones that need `KSPDIR`.

The Python tests are in `clients/python/tests` and need `pytest` and the FakeHost (`SKIZZY_FAKEHOST` points to its path).

Build the in-game plugin and make the package:

```powershell
.\scripts\package.ps1                      # uses KSPDIR / Directory.Build.props.user
.\scripts\package.ps1 -KspDir "<KSP>"      # or pass the folder
.\scripts\package.ps1 -SkipHost            # CLI only, no KSP needed (partial package, not for release)
```

Other parameters: `-Configuration` (default `Release`) and `-SkipCli`. The CLI is published as a self-contained single-file `win-x64` exe. KSP and Unity DLLs are only referenced at build time and are never packaged.

## Status

M0 "First contact". Version 0.1.0.

Verified by automated tests in this repository (net10.0, no game): the protocol and permission model (`tests/Skizzy.Protocol.Tests`), the host core against the simulated world, including security checks and allocation checks (`tests/Skizzy.Host.Tests`), and the CLI and MCP bridge (`tests/Skizzy.Cli.Tests`).

Not verified yet, because it needs a running KSP:

- the in-game plugin (`Skizzy.Host.dll`) running in KSP 1.12, with and without BDArmory;
- the idle-cost, screenshot and logging performance gates;
- the in-game windows in `src/Skizzy.Host/Ui` (toolbar menu, first-run wizard, permission prompts, connections, capability console, recorder, camera and health windows, kill banner). They compile against KSP 1.12 but have not been opened in the game;
- the "clean install to a working Claude Code connection in under 5 minutes" target, and Codex CLI against the real host.

The manual in-game checklist is [`docs/M0-checklist.md`](docs/M0-checklist.md). Design documents are in `design/`; the scope of this milestone is in `design/17-milestones.md`.

## Licence

Apache License 2.0. See [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).

Redistributions and derivative works must keep the `NOTICE` file (Apache-2.0 section 4(d)). The credit line is: "Based on Skizzy Protocol by Skizzy-create".

KSP and BDArmory are not included in this repository or in the package, and their source is not copied. The plugin only references their assemblies at build time and at run time inside the game.
