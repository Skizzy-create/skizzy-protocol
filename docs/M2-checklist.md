# M2 checklist: missile picture and engagement log

Branch `m2-bda-picture`. This covers the narrowed M2 slice: every missile in flight in `source.tactical`, the missiles
on the map, and the engagement log with per-team totals and KSP costs. The recorder/replay and the rest of the bda.*
command surface are out of scope (see HANDOFF.md).

Each check lists its steps and the expected result. Mark each one pass or fail, and add a note for every failure.
Read-only safety: nothing here edits GameData, the BDArmory repository or KSP files. The in-game part only connects
to a running KSP.

## 0. Build and automated tests (no game)

Run these from the worktree root in Git Bash.

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
dotnet build Skizzy.slnx -c Release                      # 0 warnings, 0 errors (warnings are errors)
dotnet build Skizzy.Map.slnx -c Release
dotnet test tests/Skizzy.C2.Tests -c Release --no-build
dotnet test tests/Skizzy.Host.Tests -c Release --no-build
SKIZZY_STOCK_BDA="<path to a stock BDArmory.dll>" dotnet test tests/Skizzy.Ksp.Tests -c Release --no-build
dotnet test tests/Skizzy.Protocol.Tests -c Release --no-build
dotnet test tests/Skizzy.Map.Render.Tests -c Release --no-build
tests/Skizzy.Map.App.Tests/bin/Release/net10.0/Skizzy.Map.App.Tests.exe -class "*MissileControlTests"
```

- [ ] A0.1: Both builds end with 0 warnings and 0 errors.
- [ ] A0.2: `MissileLedgerTests` (C2) pass. They cover lifecycle (fired, hit, intercepted, missed, destroyed),
  team attribution, multi-part costs, kills, a 220-missile raid, and 0 B steady state.
- [ ] A0.3: `TacticalFeedTests` (Host) pass. Copy the line printed by
  `Main_thread_cost_per_sample_at_160_missiles` into your report. Last measured off-game: mean 1.1 ms per sample
  at 160 missiles and 185 units (picture 0.70, ledger 0.06, snapshot 0.17 ms medians), with serialisation off the
  main thread.
- [ ] A0.4: `StockBdaLoadTests` passes with `SKIZZY_STOCK_BDA` set. Without the variable it passes vacuously.
- [ ] A0.5: `MissilePictureTests` (Render) and `MissileControlTests` (App) pass. The per-control results are in
  docs/M2-map-missiles-action-report.md.

## 1. In-game setup (PC test agent)

You need KSP 1.12.5 with BDArmory and this build of Skizzy installed (installing is the user's job; do not copy
files into GameData yourself). Load a flight scene with two BDArmory teams. Ships and aircraft with missiles are best,
for example the user's naval test craft with team A and team B.

Every step below uses MCP. In Claude Code the tool is `cap_call` with `name` and `args`. From a shell, the same call is
`skizzy call <name> key=value`. JSON values are parsed, so `limit=20` is a number.

- [ ] G1: `skizzy call tac.status`. Expect `attached: true`, `sampleHz` (default 5), `missilesInFlight: 0`,
  `logEntries: 0`, and the `mainThread` and `feedWorker` objects. If it reports `tactical_not_attached`, the
  BDArmory binding has not attached. Check `skizzy call host.stats` (`c2.hooks`) and the KSP log.
- [ ] G2: `skizzy call catalog.search q=tac`. Expect every `tac.*` capability, including `tac.missiles` and
  `tac.engagement_log`. If the host failed to start, catalog defs are missing; report the KSP.log error.

## 2. Missiles in source.tactical

Start the battle (for example with BDArmory's competition start, or by enabling guard mode on both teams) so that
missiles fly.

- [ ] G3: While missiles are in flight, run `skizzy call tac.missiles view=god limit=50`. Each row has `missile`
  (a v:handle), `team`, `weapon`, `launcherName`, `launcher`, `target`, `lat`/`lon`/`altM`, `headingDeg`/`speedMps`,
  `state: "flying"`, `firedUt`, and `cost` (KSP funds). The `text` lines read like `T12 A MSL AIM-120 from Hornet_1 >Kirov ...`.
- [ ] G4: `skizzy watch events "engagement.*" --duration 60`. Expect `engagement.fired` lines, then
  `engagement.hit`, `engagement.intercepted`, `engagement.missed` or `engagement.destroyed` lines with matching
  missile handles.
- [ ] G5: Subscribe to the stream with the CLI or Python client:
  `stream.subscribe {"source":{"tactical":{}},"rateHz":10}`. Frames come at `sampleHz`. Each frame has `rows`
  (missile rows with `class: "missile"` and `missileState`), `log` (new entries only, at most 256 per frame) and
  `totals`. A resolved missile stays as a `state: "dead"` row for 3 s with its final `missileState`.
- [ ] G6: Rate: `skizzy call tac.configure sampleHz=10`, then `tac.status`. Expect `sampleHz: 10`. Set it back
  with `sampleHz=5`. `tac.configure` is a control capability and may ask for permission once.

## 3. Engagement log and costs

- [ ] G7: `skizzy call tac.engagement_log view=god limit=50`. Expect lines like these:
  - `Team A Hornet 1 fired AIM-120 at Team B Kirov (1,250 funds)`
  - `... intercepted by Team B SM-2 from Kirov`
  - `... hit Kirov`
  - `... missed Team B Kirov`

  Then a `#team` totals block with `fired`, `interceptsMade`, `hits`, `misses`, `spent`, `interceptedCost` and
  `hitCost`.
- [ ] G8: Paging: call again with `since=<last seq>` (only newer entries come back) and with `before=<first seq>`
  (older entries come back).
- [ ] G9: Side rules: from an agent session bound to side A, `tac.engagement_log view=B` is refused with
  `not_own_side`, and `view=god` with `god_view_denied` while fog is on. A human, referee or director session can
  read every team. `team=B` filters to entries where B fired or was fired at.
- [ ] G10: Costs. Pick one missile type, open it in the VAB/SPH part list and add up the part costs, including
  multi-part missiles such as a booster plus a kill vehicle. That sum should equal the `cost` of each of its
  `fired` lines (catalog cost plus module costs; ammunition and fuel resources follow KSP's own `GetModuleCosts`).
- [ ] G11: Sources. Each log entry has a `source` field:
  - `bda_scoring`: BDArmory competition scoring (the Overdrive F10 OnHit/OnDeath events, during a competition only)
    named the vessel the missile damaged, or the kill.
  - `proximity`: the binding read BDArmory's `MissileBase.HasExploded` when KSP destroyed the missile and found the
    detonation near the target's nearest part (hit) or not (missed). An enemy detonation within 0.75 s and its blast
    radius means intercepted.
  - `binding`: BDArmory's own `HasMissed` flag, or the missile was destroyed without exploding.
  - `ledger`: no game verdict was available, so the geometry fallback decided.

  Record which sources appear. Without a competition, expect `proximity` and `binding` only.
- [ ] G12: Kills. When a vessel dies after a missile hit, expect a `killed` entry, either
  `... killed by Team A X (missiles, BDArmory scoring)` during a competition, or the ledger fallback
  `... destroyed, last missile hit ...` within 30 s of the hit. The team's `kills` total goes up by one.

## 4. Main-thread cost in game

- [ ] G13: During a large exchange (aim for 150+ missiles; BDArmory's missile salvo settings help), call
  `skizzy call tac.status` every few seconds. Record:
  - `mainThread.sampleMsMean`, `sampleMsMax`, `ledgerMsMax` and `snapshotMsMax`
  - `periodMsMean` (the picture period, which includes the binding's scan)
  - `feedWorker.serializeMsMean`, `skipped` and `bytes`

  Pass: `sampleMsMean` stays under 2 ms at 5 Hz. `skipped` may grow only while a client is slow; skipped
  frames lose nothing, because the next frame carries the log from the client's last seq. Compare against the
  `host.stats` frame-time numbers with the feed idle.

## 5. Map app against the game

Start the map with the live source: `dotnet run --project src/Skizzy.Map.App -c Release -- --source live`.

- [ ] G14: Host health shows `Stream: source.tactical (M2)`. If the map connected before the flight scene loaded,
  it starts on the M0 vessel stream and moves up to the tactical stream within 5 s of the binding attaching.
- [ ] G15: Missiles are drawn as arrowheads that point along their heading, coloured by team. The first team seen is
  Blue, the second Red, the third Green. Each missile has a short trail. Zoom out until the view spans 30+ km: the
  missiles stay arrowheads and do not turn into dots.
- [ ] G16: In Blue fog, a Red missile guiding on a Blue vessel has a red ring. Switch to Red fog: the rings move to
  Blue missiles guiding on Red vessels.
- [ ] G17: Toolbar `Missiles` hides every missile with its trail and ring, and a second click brings them back.
  `Missile trails` removes only the missile trails; the ship trails stay under `Trails`. The Layers panel has the
  same two toggles, and the two places stay in sync.
- [ ] G18: The Engagements tab (bottom dock, also under Panels) shows the per-team totals table (Fired, Air, Hits,
  Missed, Int'd, Int made, Kills, Spent, Lost to int, Hit value) and the running log, newest first. The kind chips
  (Fired, Hits, Intercepted, Missed, Destroyed, Kills) hide and show each kind, with counts. Clicking a line pans
  the map to that missile and selects it while it is alive.
- [ ] G19: Messages has the same lines under Weapons, Intercepts and Damage. A missile's end does not produce a
  "left the picture" line. Losses shows each team's missiles fired, hits, intercepts and funds spent.
- [ ] G20: Plot events: an impact flash where a missile hit, an intercept burst where one was intercepted, and a
  miss marker where one missed.
