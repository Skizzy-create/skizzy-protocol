# M2 handoff: missile picture slice (2026-11-12)

Worktree `D:/KerbalSpaceProgram/skizzy-protocol-m2`, branch `m2-bda-picture`, with local main (54e1199) merged in.
**Nothing is committed. Everything is staged** for the lead to review. `Directory.Build.props.user` and `refs/` are
ignored and not staged.

Rules that still apply:
- Agents never commit or push. The lead reviews the staged diff, then the user signs the commits.
- The git author is asliakartik@gmail.com.
- Never touch the user's KSP or GameData, or any BDArmory repo. Read-only use of BDArmory source is fine; never copy
  BDArmory or KSP source.
- Apache-compatible dependencies only.
- New hook code must follow the stock-load rule: no field, property, base or nested type mentions an Overdrive-only
  type, and every Overdrive-only delegate is built with an explicit `new Action<...>`.

## Scope of this slice (coordinator brief)
Show every missile and what happened to it in the map app, while keeping KSP's extra work small. The recorder and
replay, and the rest of the bda.* command surface, are **out of scope** (see below).

## Done and tested
- **Merge**: main merged. `C2Host.Issue` keeps track-number targets. `C2Wire.ResolveInList` was added.
  `StockBdaLoadTests` passes against a stock BDArmory.dll (`SKIZZY_STOCK_BDA=...`).
- **Start-up blocker**: `src/Skizzy.Protocol/Catalog/M2Defs.cs` defines all 11 `tac.*` defs, hooked in through
  `Builtins`. **bda.\* stays unregistered**: `HostCaps.cs` no longer calls `BdaHost.Register`, and the code remains
  in `src/Skizzy.Host/Core/Bda/`, untested. The spec has been regenerated (`spec/schemas`).
- **Ledger** `src/Skizzy.C2/Tactical/MissileLedger.cs`:
  - Lifecycle states: flying, hit, missed, intercepted, destroyed. Kills are logged too.
  - Team attribution, costs, and per-team totals.
  - Interceptor matching with a 1.5 s window.
  - Kills come from BDA scoring, with a fallback rule: a vessel that dies within 30 s of a missile hit.
  - 0 B steady state.
  - Tests: `tests/Skizzy.C2.Tests/MissileLedgerTests.cs`.
- **Host** `src/Skizzy.Host/Core/Tactical/TacticalFeed.cs` and `TacticalHost.cs`:
  - `source.tactical` rows carry missile columns: launcher, launcherName, weapon, missileState, cost, firedUt.
  - Each frame also carries `log` (per-subscriber seq range, at most 256 per frame) and `totals`.
  - Snapshots are taken on the main thread into a pooled double-buffered frame. The worker thread
    `skizzy-tactical-feed` serialises and sends; when it is busy, frames are skipped and nothing is lost.
  - New capabilities and events: `tac.engagement_log`, extended `tac.missiles` (now with position and velocity),
    `tac.status` cost counters, `tac.configure sampleHz` (1–10, default 5), and `engagement.*` events.
  - Tests: `tests/Skizzy.Host.Tests/TacticalFeedTests.cs`.
- **KSP layer**:
  - `src/Skizzy.BDArmory/Ksp/KspBdaGame.Missiles.cs` reads the weapon name and KSP part costs, and decides each
    outcome at `onVesselWillDestroy` from `HasExploded`/`HasMissed` plus a nearest-part distance check and intercept
    matching.
  - `Ksp/HookCallsM2.cs` subscribes to the F10 `CompetitionScores.OnHit/OnDeath` events, inside try/catch.
  - The wiring is in `BdaBindingAddon` and `BdaExtras`.
  - Compiles, and the stock-load test passes. **Not run in game.**
- **Map**:
  - Render: `PlotOptions.Missiles`, `MissileTrails`, and `MissileMinPx` (default 6 px, so missiles stay arrowheads
    at any zoom), plus `PlotTrack.InboundToFriend`, drawn as a red ring.
  - Data: `HostDataSource` parses the tactical columns. Teams map to sides (first Blue, second Red, third Green).
    Missile kind is worked out from the launcher and target. Dead rows give plot events. Log and totals go to
    `MapData.Engagements` (`Engagements.cs`), the Messages log and Losses. While on the M0 stream, the map retries
    `source.tactical` every 5 s.
  - App: toolbar and Layers toggles "Missiles" and "Missile trails", saved in the layout, and a new Engagements
    panel (`Panels/EngagementsView.cs`) in the bottom dock.
  - Tests: `tests/Skizzy.Map.Render.Tests/MissilePictureTests.cs` and
    `tests/Skizzy.Map.App.Tests/ControlTests.Missiles.cs`. The action report is
    `docs/M2-map-missiles-action-report.md`.
- **Docs**: `docs/M2-checklist.md` (in-game steps over MCP for a PC test agent) and the README capability table.

## Measured main-thread cost (off-game, FakeBda, 160 missiles / 185 units)
- Mean per sample at 5 Hz: 1.1 ms (Debug) and 0.86 ms (Release, final run). The max was 13.5–14.6 ms, a single
  outlier per run.
- Medians (Release): picture period 0.38 ms, ledger 0.04 ms, snapshot 0.10 ms.
- Serialisation takes 0.7–1.25 ms mean, on the worker thread.
- 0 B steady state on the main thread and in serialisation.
- In game, the binding's scan is part of the picture period and shows live in `tac.status` (`periodMsMean`,
  `mainThread.*`). Checklist G13 measures it.

## Out of scope / left for later
- Recorder v1 host and replay server. The codec (`Core/Rec/MsgPack.cs`, `SkzRec.cs`) and its tests are in place;
  `Recorder.cs`, `rec.*` and the replay server are not.
- The bda.* surface:
  - `BdaHost` handlers stay unregistered and have no catalog defs.
  - `IBdaOps` is not implemented: `KspBdaOps.cs` and `KspBdaGame.Extras.cs` (missile warnings, radar on/off) are
    missing.
  - The FakeBdaOps tests are missing.
- Competition start/stop from MCP (still BDArmory's own UI).

## Risks
- In-game outcome accuracy is unproven:
  - The proximity threshold is max(blast × 1.1, 20 m), and the intercept window is 0.75 s and blast × 1.5 + 30 m.
  - BDArmory scoring events fire only during a competition, and only on the Overdrive build (stock has no OnHit).
  - Every entry carries its `source`, so a wrong verdict can be traced.
- The 13–15 ms max sample outlier off-game (one per run) is probably the JIT or GC. Watch `sampleMsMax` in game.
- Team-to-colour mapping follows first-seen order, so colours can differ between sessions.
- The perf-budget tests `WholeAppWith5000TracksReportsItsFrameRate` and `RenderCoreDraws5000Symbols...` fail on this
  machine before and after these changes. They are timing gates, not regressions.

## Test status (final run, Release, 2026-11-12)
- Builds: `Skizzy.slnx` (KSP projects included) and `Skizzy.Map.slnx` both have 0 warnings and 0 errors.
- C2: 117/117.
- Host: 130/130. `TacticalHost.SampleStreams` was added to the hot-tag coverage list; its allocation test is
  `Feed_allocates_nothing_in_steady_state`.
- Protocol: 64/64.
- Cli: 26/26.
- Ksp: 45/45, with `SKIZZY_STOCK_BDA` pointing at the stock BDArmory.dll.
- Map.Render: 93/93.
- Map.App: 156/157. The one failure is the `RenderCoreDraws5000SymbolsInsideTheFrameBudget` perf gate, which also
  failed on the baseline before these changes. The 12 new `MissileControlTests` passed in three consecutive runs.
