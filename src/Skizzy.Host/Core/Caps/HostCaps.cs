using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Skizzy.Protocol;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;

namespace Skizzy.Host.Core.Caps
{
    /// <summary>Registers the Unity-free built-in handlers. The KSP layer adds spikes and KSP-only self-checks.</summary>
    public static class BuiltinCaps
    {
        public static void Register(HostCore h)
        {
            HostCaps.Register(h);
            KspReadCaps.Register(h);
            CameraCaps.Register(h);
            h.Logs.Register(h);
            C2Host.Register(h);
            Tactical.TacticalHost.Register(h);
            // bda.* (BdaHost) stays unregistered until the KSP operations layer (IBdaOps over BDArmory) exists: HANDOFF.md
        }
    }

    public static class HostCaps
    {
        public static void Register(HostCore h)
        {
            h.Register("host.stats", Stats);
            h.Register("host.state", State);
            h.Register("host.sessions", Sessions);
            h.Register("host.selfcheck", SelfCheck);
            h.RegisterOp("host.selfcheck.perf", PerfGate);
            h.Register("host.kill_switch", KillSwitchCap);
            h.Register("host.doctor", Doctor);
            h.Register("host.debug.confirm_test", c => JsonValue.Obj().Set("confirmed", true).Set("note", c.Str("note")));
            h.Register("events.poll", EventsPoll);
            h.Register("events.wait", EventsWait);
            h.Register("permissions.get", PermissionsGet);
            h.Register("permissions.request", PermissionsRequest);
        }

        // ---------------------------------------------------------------- host.*

        private static JsonValue Stats(CallContext c)
        {
            var h = c.Host;
            var st = h.Stats;
            var srv = h.Server;
            return JsonValue.Obj()
                .Set("hostVersion", ProtocolInfo.HostVersion)
                .Set("idleMode", h.WakerMode)
                .Set("pumpAwake", h.Dispatcher.Awake)
                .Set("budgetMs", Math.Round(h.Dispatcher.BudgetMs, 3))
                .Set("queue", JsonValue.Obj().Set("main", h.Dispatcher.MainCount).Set("fixed", h.Dispatcher.FixedCount))
                .Set("mainThread", JsonValue.Obj()
                    .Set("updateMs", HostStats.Timing(st.UpdateMs))
                    .Set("fixedUpdateMs", HostStats.Timing(st.FixedMs))
                    .Set("dispatcherDrainMs", HostStats.Timing(h.Dispatcher.DrainMs))
                    .Set("queueWaitMs", HostStats.Timing(h.Dispatcher.QueueWaitMs)))
                .Set("frameMs", HostStats.Timing(st.FrameMs))
                .Set("callMs", HostStats.Timing(st.CallMs))
                .Set("calls", st.Calls)
                .Set("errors", st.Errors)
                .Set("sessions", h.AttachedSessionCount)
                .Set("gc", JsonValue.Obj().Set("gen0", GC.CollectionCount(0)).Set("totalMemoryBytes", GC.GetTotalMemory(false)))
                .Set("logs", JsonValue.Obj().Set("active", h.Logs.ActiveCount).Set("sampleMs", HostStats.Timing(st.LogSampleMs)))
                .Set("streams", JsonValue.Obj().Set("active", h.Streams.Count).Set("sampleMs", HostStats.Timing(st.StreamSampleMs)))
                .Set("screenshots", JsonValue.Obj().Set("count", st.Shots).Set("mainThreadMs", HostStats.Timing(st.ShotMainMs))
                    .Set("totalMs", HostStats.Timing(st.ShotTotalMs)).Set("lastPath", st.LastShotPath))
                .Set("ops", h.Ops.RunningCount)
                .Set("autoDisabled", JsonValue.ArrOf(h.Faults.Disabled().ToArray()))
                .Set("killSwitch", h.Kill.ToJson())
                .Set("c2", h.C2.StatsJson())
                .Set("events", JsonValue.Obj().Set("lastCursor", h.Events.LastCursor).Set("waiters", h.Events.WaiterCount))
                .Set("audit", JsonValue.Obj().Set("written", h.Audit.Written).Set("dropped", h.Audit.Dropped))
                .Set("server", srv == null ? JsonValue.Null : JsonValue.Obj().Set("port", srv.Port).Set("connections", srv.ConnectionCount)
                    .Set("accepted", srv.Accepted).Set("refused", srv.Refused).Set("lastRefusal", srv.LastRefusal))
                .Set("wakes", h.Dispatcher.Wakes);
        }

        private static JsonValue State(CallContext c)
        {
            var m = c.Host.Meta;
            return JsonValue.Obj().Set("scene", m.Scene).Set("paused", m.Paused).Set("timeScale", m.TimeScale)
                .Set("warp", JsonValue.Obj().Set("mode", m.WarpMode).Set("rate", m.WarpRate))
                .Set("ut", m.Ut).Set("activeVessel", m.ActiveVessel).Set("activeVesselName", m.ActiveVesselName)
                .Set("sceneTransition", m.SceneTransition);
        }

        private static JsonValue Sessions(CallContext c)
        {
            var list = new List<Session>();
            c.Host.SnapshotSessions(list);
            var arr = JsonValue.Arr();
            foreach (var s in list) arr.Add(s.Describe());
            return JsonValue.Obj().Set("sessions", arr).Set("total", arr.Count).Set("you", c.Session.Id);
        }

        private static JsonValue SelfCheck(CallContext c)
        {
            var h = c.Host;
            var lines = new List<string[]>();
            bool sessions = h.AttachedSessionCount > 0;
            lines.Add(new[] { h.WakerMode == "sync_context" ? "pass" : "warn", "idle wake", h.WakerMode == "sync_context"
                ? "pump disabled when idle; woken through UnitySynchronizationContext"
                : "polling fallback: pump stays enabled with one volatile read per frame" });
            lines.Add(new[] { "pass", "listener", h.Port > 0 ? "ws://127.0.0.1:" + h.Port + ProtocolInfo.Path : (h.Settings.Listen ? "not listening: " + h.StartError : "listen=false (baseline mode)") });
            h.Stats.UpdateMs.Percentiles(out double u50, out double u99, out double umax);
            lines.Add(new[] { u50 < 0.1 ? "pass" : "warn", "host Update cost (connected)", "p50 " + Inv.S(u50) + " ms, p99 " + Inv.S(u99) + " ms, max " + Inv.S(umax) + " ms" });
            h.Dispatcher.DrainMs.Percentiles(out double d50, out double d99, out double dmax);
            lines.Add(new[] { "pass", "dispatcher", "budget " + Inv.S(h.Dispatcher.BudgetMs) + " ms, drain p50 " + Inv.S(d50) + " / p99 " + Inv.S(d99) + " ms, " + h.Dispatcher.Executed + " jobs" });
            var disabled = h.Faults.Disabled();
            lines.Add(new[] { disabled.Count == 0 ? "pass" : "warn", "auto-disabled capabilities", disabled.Count == 0 ? "none" : string.Join(", ", disabled.ToArray()) });
            lines.Add(new[] { h.Kill.Engaged ? "warn" : "pass", "kill switch", h.Kill.Engaged ? "engaged by " + h.Kill.By : "released" });
            lines.Add(new[] { h.C2.Faults == 0 ? "pass" : "warn", "c2 engine", h.C2.Describe() });
            try { h.SelfCheckExtras?.Invoke(c, lines); }
            catch (Exception e) { lines.Add(new[] { "fail", "self-check provider", e.GetType().Name + ": " + e.Message }); }
            var arr = JsonValue.Arr();
            int fails = 0, warns = 0;
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                if (l[0] == "fail") fails++;
                else if (l[0] == "warn") warns++;
                arr.Add(JsonValue.Obj().Set("status", l[0]).Set("check", l[1]).Set("detail", l[2]));
                sb.Append(l[0].ToUpperInvariant()).Append("  ").Append(l[1]).Append(": ").Append(l[2]).Append('\n');
            }
            return JsonValue.Obj().Set("result", fails > 0 ? "fail" : warns > 0 ? "warn" : "pass").Set("checks", arr).Set("text", sb.ToString())
                .Set("connectedSessions", sessions);
        }

        private static JsonValue Doctor(CallContext c)
        {
            var lines = new List<string[]>();
            lines.Add(new[] { "pass", "host", "skizzy host " + ProtocolInfo.HostVersion + ", protocol " + ProtocolInfo.Version });
            c.Host.World.Doctor(lines);
            var arr = JsonValue.Arr();
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                arr.Add(JsonValue.Obj().Set("status", l[0]).Set("check", l[1]).Set("detail", l[2]));
                string color = l[0] == "pass" ? "green" : l[0] == "warn" ? "yellow" : "red";
                sb.Append('[').Append(color).Append("] ").Append(l[1]).Append(": ").Append(l[2]).Append('\n');
            }
            return JsonValue.Obj().Set("checks", arr).Set("text", sb.ToString());
        }

        private static JsonValue KillSwitchCap(CallContext c)
        {
            var h = c.Host;
            string action = c.Str("action", "status");
            if (action == "engage")
            {
                bool changed = h.EngageKill(c.Session.ActorId ?? c.Session.ClientName, c.Str("reason"));
                return h.Kill.ToJson().Set("changed", changed);
            }
            if (action == "release")
            {
                if (c.Session.ActorKind != "human" && c.Session.Role != "human")
                    throw new SkizzyError(ErrorKind.PermissionDenied, "only the human (in-game banner or a human actor) may release the kill switch", "role_clamp",
                        "Ask the human to release it in game.", JsonValue.Obj().Set("capability", "host.kill_switch").Set("role", c.Session.Role));
                bool changed = h.ReleaseKill(c.Session.ActorId);
                return h.Kill.ToJson().Set("changed", changed);
            }
            return h.Kill.ToJson();
        }

        // ---------------------------------------------------------------- events

        private static JsonValue EventsPoll(CallContext c)
        {
            var h = c.Host;
            var names = c.Params.GetStringList("names");
            string[] globs = names != null ? names.ToArray() : null;
            int limit = c.Int("limit", 50);
            var list = new List<HostEvent>();
            bool lost = false;
            long next;
            if (c.Has("cursor")) next = h.Events.Poll(c.Params.GetLong("cursor"), globs, limit, list, out lost);
            else next = h.Events.Recent(globs, limit, list);
            return EventsResult(list, next, lost, c.Str("format", "json"));
        }

        private static JsonValue EventsResult(List<HostEvent> list, long next, bool lost, string format)
        {
            if (format == "compact")
                return JsonValue.Obj().Set("text", EventRing.FormatCompact(list, next, lost)).Set("nextCursor", next).Set("count", list.Count).Set("resyncRequired", lost);
            var arr = JsonValue.Arr();
            foreach (var e in list) arr.Add(e.ToJson());
            return JsonValue.Obj().Set("events", arr).Set("nextCursor", next).Set("resyncRequired", lost).Set("count", list.Count);
        }

        private static JsonValue EventsWait(CallContext c)
        {
            var h = c.Host;
            var names = c.Params.GetStringList("names");
            string[] globs = names != null ? names.ToArray() : null;
            long after = c.Has("cursor") ? c.Params.GetLong("cursor") : h.Events.LastCursor;
            int max = c.Int("maxEvents", 20);
            int timeoutMs = (int)(c.Num("timeoutS", 20) * 1000);
            string format = c.Str("format", "compact");
            c.Defer();
            h.Events.Wait(after, globs, max, timeoutMs, (list, next) =>
            {
                var res = EventsResult(list, next, false, format);
                if (list.Count == 0) res.Set("timedOut", true);
                c.Complete(res);
            });
            return null;
        }

        // ---------------------------------------------------------------- permissions

        private static JsonValue PermissionsGet(CallContext c)
        {
            var h = c.Host;
            var s = c.Session;
            var perm = h.Permissions;
            string cap = c.Str("capability");
            if (cap != null)
            {
                var def = h.Catalog.Find(cap);
                if (def == null) throw SkizzyError.NotFound(cap, "unknown", "No such capability; search with catalog.search.");
                var d = perm.Evaluate(def.Name, def.Mod, def.Safety, s.Role, s.ClientName, s.Grants);
                return JsonValue.Obj().Set("capability", def.Name).Set("safety", SafetyNames.Name(def.Safety)).Set("policy", SafetyNames.PolicyName(d.Policy))
                    .Set("source", d.Source).Set("reason", d.DenyReason).Set("areaEnabled", perm.Config.IsAreaEnabled(def.Area)).Set("rateLimit", d.RateLimit ?? perm.FindRate(def.Name, def.Mod));
            }
            var levels = JsonValue.Obj();
            foreach (var lvl in SafetyNames.All)
                levels.Set(SafetyNames.Name(lvl), SafetyNames.PolicyName(perm.Evaluate("*", "Skizzy", lvl, s.Role, s.ClientName, s.Grants).Policy));
            var areas = JsonValue.Obj();
            foreach (var a in PermissionConfig.KnownAreas) areas.Set(a, perm.Config.IsAreaEnabled(a));
            var grants = JsonValue.Arr();
            lock (s.Grants) foreach (var g in s.Grants) grants.Add(g);
            return JsonValue.Obj().Set("preset", perm.Config.Preset).Set("role", s.Role).Set("levels", levels).Set("areas", areas).Set("sessionGrants", grants)
                .Set("precedence", s.Precedence);
        }

        private static JsonValue PermissionsRequest(CallContext c)
        {
            var h = c.Host;
            var s = c.Session;
            string cap = c.Str("capability");
            var def = h.Catalog.Find(cap);
            if (def == null) throw SkizzyError.NotFound(cap, "unknown", "No such capability; search with catalog.search.");
            if (def.Safety == Safety.Unsafe)
                throw new SkizzyError(ErrorKind.PermissionDenied, "unsafe capabilities cannot be granted over the wire", "policy",
                    "Only the human can enable unsafe capabilities by editing permissions.cfg.", JsonValue.Obj().Set("capability", def.Name));
            var req = new PromptRequest
            {
                SessionId = s.Id,
                ClientName = s.ClientName,
                ActorId = s.ActorId,
                Role = s.Role,
                Def = def,
                Capability = def.Name,
                Summary = "grant " + def.Name + " (" + SafetyNames.Name(def.Safety) + ") for this session",
                AiReason = c.Str("reason"),
                TimeoutS = h.Settings.ConfirmTimeoutS,
                IsGrantRequest = true,
            };
            c.Defer();
            h.Prompts.Show(req, a =>
            {
                string status, scope = null;
                switch (a)
                {
                    case PromptAnswer.AllowOnce:
                    case PromptAnswer.AllowSession:
                        lock (s.Grants) s.Grants.Add(def.Name);
                        status = "granted"; scope = "session";
                        break;
                    case PromptAnswer.AllowAlways:
                        lock (s.Grants) s.Grants.Add(def.Name);
                        status = "granted"; scope = "persistent";
                        break;
                    case PromptAnswer.Timeout: status = "timeout"; break;
                    default: status = "denied"; break;
                }
                if (a == PromptAnswer.DenyAndPause) h.EngageKill("human", "denied a permission request and paused AI");
                if (scope == "persistent") h.PersistGrantPublic(def.Name);
                c.Complete(JsonValue.Obj().Set("status", status).Set("scope", scope).Set("capability", def.Name));
            });
            return null;
        }

        // ---------------------------------------------------------------- host.selfcheck.perf

        private static IEnumerator<object> PerfGate(CallContext c, Operation op)
        {
            var h = c.Host;
            string gate = c.Str("gate", "all");
            double durationS = c.Num("durationS", 20);
            var results = JsonValue.Arr();
            bool pass = true;

            if (gate == "idle" || gate == "all")
            {
                op.ProgressText = "idle gate: measuring host Update/FixedUpdate cost";
                h.Stats.UpdateMs.Reset();
                h.Stats.FixedMs.Reset();
                long end = Clock.NowMs + (long)(durationS * 1000);
                while (Clock.NowMs < end) { op.Progress = 0.3 * (1 - (end - Clock.NowMs) / (durationS * 1000)); yield return null; }
                h.Stats.UpdateMs.Percentiles(out double u50, out double u99, out double umax);
                h.Stats.FixedMs.Percentiles(out double f50, out double f99, out double fmax);
                double perFrame = u50 + f50;
                bool ok = perFrame < 0.1;
                pass &= ok;
                var r = JsonValue.Obj().Set("gate", "idle").Set("pass", ok).Set("budgetMs", 0.1).Set("measuredMsPerFrameP50", Math.Round(perFrame, 4))
                    .Set("updateMs", HostStats.Timing(h.Stats.UpdateMs)).Set("fixedUpdateMs", HostStats.Timing(h.Stats.FixedMs))
                    .Set("note", "measured with this client connected and idle; the nothing-connected cost is 0 callbacks (pump disabled) - see the idle probe in docs/M0-checklist.md");
                string probe = h.Paths.IdleProbeFile;
                if (File.Exists(probe)) r.Set("idleProbeFile", h.Paths.Relative(probe));
                results.Add(r);
            }

            if (gate == "log" || gate == "all")
            {
                op.ProgressText = "log gate: synthetic log at 10 Hz";
                int targets = c.Int("vessels", 100);
                LogFile log = null;
                SkizzyError err = null;
                try { log = h.Logs.StartSynthetic(targets, 10); }
                catch (SkizzyError e) { err = e; }
                if (err != null)
                {
                    results.Add(JsonValue.Obj().Set("gate", "log").Set("pass", false).Set("error", err.ToOneLine()));
                    pass = false;
                }
                else
                {
                    h.Stats.LogSampleMs.Reset();
                    long end = Clock.NowMs + (long)(durationS * 1000);
                    while (Clock.NowMs < end && !op.CancelRequested) { op.Progress = 0.3 + 0.4 * (1 - (end - Clock.NowMs) / (durationS * 1000)); yield return null; }
                    h.Logs.StopLog(log, "perf gate done");
                    h.Stats.LogSampleMs.Percentiles(out double l50, out double l99, out double lmax);
                    bool ok = l99 <= 0.2;
                    pass &= ok;
                    results.Add(JsonValue.Obj().Set("gate", "log").Set("pass", ok).Set("budgetMsPerFixedUpdate", 0.2)
                        .Set("targets", log.Plan.Rows.Count).Set("fields", 3).Set("rateHz", 10)
                        .Set("sampleMs", HostStats.Timing(h.Stats.LogSampleMs)).Set("rows", log.Rows).Set("file", log.RelPath)
                        .Set("note", "sampleMs counts every FixedUpdate the log sampler ran in (only sampling steps do work)"));
                    try { File.Delete(log.FullPath); } catch (Exception) { }
                }
            }

            if (gate == "screenshot" || gate == "all")
            {
                op.ProgressText = "screenshot gate";
                var cam = h.World.Camera;
                if (cam == null || !cam.Available)
                {
                    results.Add(JsonValue.Obj().Set("gate", "screenshot").Set("pass", false).Set("error", "no camera in this host"));
                    pass = false;
                }
                else
                {
                    int n = c.Int("captures", 20);
                    JsonValue before = h.CountUnityObjects?.Invoke();
                    var main = new TimingWindow(256);
                    var total = new TimingWindow(256);
                    string path = null;
                    int failures = 0;
                    for (int i = 0; i < n && !op.CancelRequested; i++)
                    {
                        World.CaptureResult res = null;
                        cam.Capture(new World.CaptureRequest { Width = 1024, Quality = 75 }, r => res = r);
                        long deadline = Clock.NowMs + 5000;
                        while (res == null && Clock.NowMs < deadline) yield return null;
                        if (res == null || res.Error != null) { failures++; continue; }
                        main.Add(res.MainThreadMs);
                        total.Add(res.TotalMs);
                        path = res.Path;
                        op.Progress = 0.7 + 0.3 * (i + 1) / n;
                        yield return null;
                    }
                    for (int i = 0; i < 10; i++) yield return null;
                    JsonValue after = h.CountUnityObjects?.Invoke();
                    main.Percentiles(out double m50, out double m99, out double mmax);
                    double budget = path == "readpixels" ? 30 : 3;
                    bool countsOk = before == null || JsonValue.DeepEquals(before, after);
                    bool ok = failures == 0 && m50 <= budget && countsOk;
                    pass &= ok;
                    results.Add(JsonValue.Obj().Set("gate", "screenshot").Set("pass", ok).Set("captures", n).Set("failures", failures).Set("path", path)
                        .Set("budgetMainThreadMs", budget).Set("mainThreadMs", HostStats.Timing(main)).Set("totalMs", HostStats.Timing(total))
                        .Set("objectsBefore", before).Set("objectsAfter", after).Set("objectCountsUnchanged", countsOk));
                }
            }
            op.Result = JsonValue.Obj().Set("pass", pass).Set("gates", results);
        }
    }
}
