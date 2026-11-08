using System.Collections.Generic;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;
using Skizzy.Protocol.Schema;

namespace Skizzy.Protocol.Catalog
{
    /// <summary>
    /// The built-in M0 capabilities (17 §M0 scope). Descriptions are written for an LLM: what it does, when
    /// to use it, side effects, units. The host attaches handlers by name; the bridge projects the `core`
    /// toolset from these definitions so the MCP tool list is identical with or without the game running.
    /// </summary>
    public static class Builtins
    {
        public const string Core = "core";
        public static readonly string[] FlightScenes = { "FLIGHT" };
        public static readonly string[] VesselScenes = { "FLIGHT", "SPACECENTER", "TRACKSTATION" };
        public static readonly string[] Any = { "ANY" };

        private static DefRegistry _registry;
        private static readonly object Gate = new object();

        public static DefRegistry Registry
        {
            get
            {
                lock (Gate)
                {
                    if (_registry == null)
                    {
                        var r = new DefRegistry();
                        foreach (var d in Build()) r.Add(d);
                        _registry = r;
                    }
                    return _registry;
                }
            }
        }

        // ---------------- shared schema pieces ----------------

        public static SchemaB VesselRef(string desc = null)
        {
            return S.Str(desc ?? "Vessel: a handle like \"v:3281937421\", an exact vessel name, or \"active\" for the vessel the player flies.")
                .Handle("vessel").MinLen(1).MaxLen(256);
        }

        public static SchemaB Selector()
        {
            var vessels = S.Obj("Which vessels. All conditions must match; omit to select every loaded vessel.")
                .Prop("active", S.Bool("only the active vessel"))
                .Prop("loaded", S.Bool("only loaded vessels (true) or only unloaded ones (false)"))
                .Prop("handles", S.Arr(S.Str().Handle("vessel"), "explicit vessel handles or names").MaxItems(1024))
                .Prop("nameLike", S.Str("glob on the vessel name, e.g. \"DDG*\"").MaxLen(256))
                .Prop("type", S.StrOrList("KSP vessel types, e.g. Ship, Plane, Probe"))
                .Prop("includeDebris", S.Bool("include debris (excluded by default)").Default(false))
                .Closed();
            var parts = S.Obj("Which parts (only when modules or part fields are wanted).")
                .Prop("nameLike", S.Str("glob on the part name (config name)").MaxLen(256))
                .Prop("titleLike", S.Str("glob on the part title").MaxLen(256))
                .Closed();
            var modules = S.Obj("Which part modules. Fields are then read from each module (Tier 0 KSPFields).")
                .Prop("type", S.StrOrList("module class names, e.g. ModuleHullBreach, MissileFire"))
                .Closed();
            return S.Obj("Selector (04 \u00a72): vessels, optional parts and modules.")
                .Prop("vessels", vessels)
                .Prop("parts", parts)
                .Prop("modules", modules)
                .Closed();
        }

        public static SchemaB Fields()
        {
            return S.Arr(S.Str().MinLen(1).MaxLen(128),
                "Fields to read. Vessel fields (altitude, srfSpeed, ctrlState.mainThrottle, ... see ksp.query description) " +
                "or module KSPField names when modules are selected; prefix with \"vessel.\" to read a vessel field per module row.")
                .MinItems(1).MaxItems(64);
        }

        private static SchemaB Limit(int def, int max)
        {
            return S.Int("maximum rows to return").Min(1).Max(max).Default(def);
        }

        private static SchemaB Cursor() { return S.Str("opaque cursor from a previous result's \"cursor\" to get the next page").MaxLen(64); }

        private static SchemaB Format(string def)
        {
            return S.Enum("\"compact\" = a few readable text lines (best for LLMs), \"json\" = structured", "json", "compact").Default(def);
        }

        private static SchemaB OrbitObj()
        {
            return S.Obj("Camera orbit around the target.")
                .Prop("heading", S.Num("degrees, 0 = camera north of target looking south, 180 = astern for a north-bound vessel").Min(-360).Max(360).Unit("deg"))
                .Prop("pitch", S.Num("degrees above the horizon").Min(-89).Max(89).Unit("deg"))
                .Prop("distance", S.Num("metres from the target").Min(3).Max(150000).Unit("m"))
                .Closed();
        }

        private static string VesselFieldList()
        {
            var names = new List<string>();
            foreach (var f in VesselFields.All) names.Add(f.Name);
            return string.Join(", ", names.ToArray());
        }

        private static CapabilityDef Def(string name, Safety safety, string area, string mod, Affinity aff, string[] scenes, string desc, SchemaB input, params string[] toolsets)
        {
            return new CapabilityDef
            {
                Name = name,
                Provider = name.StartsWith("ksp.") ? "ksp" : name.StartsWith("rec.") ? "rec" : name.StartsWith("host.") ? "host" : name.StartsWith("c2.") ? "c2" : "core",
                Title = name,
                Description = desc,
                Safety = safety,
                Area = area,
                Mod = mod,
                Affinity = aff,
                Scenes = scenes,
                InputSchema = (input ?? S.Obj().Closed()).Build(),
                Toolsets = toolsets ?? new string[0],
            };
        }

        private static IEnumerable<CapabilityDef> Build()
        {
            // ---------------- host ----------------
            yield return Def("host.stats", Safety.Read, "host", "Skizzy", Affinity.Any, Any,
                "Host performance and health: dispatcher budget and main-thread time (p50/p99), queue depth, sessions, " +
                "frame time while active, GC collections, idle mode, screenshot and log sampler timings, auto-disabled capabilities, kill switch state.",
                S.Obj().Closed(), Core);

            yield return Def("host.state", Safety.Read, "host", "Skizzy", Affinity.Any, Any,
                "Current game state: scene, paused, time scale, warp, active vessel handle and name, UT.",
                S.Obj().Closed());

            yield return Def("host.sessions", Safety.Read, "host", "Skizzy", Affinity.Any, Any,
                "Connected sessions: actor (id, kind, role, side, precedence), client (name, kind, MCP client), age, calls, bytes in/out.",
                S.Obj().Closed());

            yield return Def("host.selfcheck", Safety.Read, "host", "Skizzy", Affinity.Main, Any,
                "Runs the host self-checks and returns pass/warn/fail lines: idle mode (UnitySynchronizationContext wake or polling), " +
                "registered GameEvents per event, OnFlyByWire callbacks and Harmony patches by Skizzy (must be 0), dispatcher timing, heap slope, " +
                "M0 spike results, assembly references, and optionally Unity texture/RenderTexture/Material/GameObject counts (textures:true; allocates once).",
                S.Obj()
                    .Prop("textures", S.Bool("also count Unity textures and objects (slower, allocates once)").Default(false))
                    .Closed(), Core);

            var perf = Def("host.selfcheck.perf", Safety.Read, "host", "Skizzy", Affinity.Main, Any,
                "Runs a performance gate (02 \u00a78.3) and reports measured numbers against the budget. gate=idle: per-frame host cost with this " +
                "client connected and idle (budget < 0.1 ms) plus the last idle-probe result; gate=screenshot: N captures at 1024 px measuring " +
                "main-thread ms (budget <= 3 ms async readback, <= 30 ms fallback) and Texture2D/RenderTexture counts before/after; gate=log: a " +
                "synthetic log of `vessels` targets x 3 fields at 10 Hz measuring sampler ms per FixedUpdate (budget <= 0.2 ms). Long-running: returns an op.",
                S.Obj()
                    .Prop("gate", S.Enum("which gate", "idle", "screenshot", "log", "all").Default("all"))
                    .Prop("durationS", S.Num("measurement window in seconds (idle, log)").Min(2).Max(900).Default(20))
                    .Prop("captures", S.Int("number of captures for the screenshot gate (100 for the leak check)").Min(1).Max(200).Default(20))
                    .Prop("vessels", S.Int("log gate: number of vessel targets to sample (loaded vessels are repeated to reach it)").Min(1).Max(1000).Default(100))
                    .Closed());
            perf.LongRunning = true;
            yield return perf;

            var ks = Def("host.kill_switch", Safety.Control, "host", "Skizzy", Affinity.Any, Any,
                "Kill switch. action=engage: denies every non-read call from every client, cancels operations and pending confirmations, " +
                "and shows a banner in game (M1 adds releasing all flight holds). action=release: only from the in-game banner or a human actor. " +
                "action=status: returns the state. Always allowed to engage.",
                S.Obj()
                    .Prop("action", S.Enum("engage, release or status", "engage", "release", "status").Default("status"))
                    .Prop("reason", S.Str("shown in the banner and audit").MaxLen(200))
                    .Closed(), Core);
            ks.KillSwitchExempt = true;
            yield return ks;

            yield return Def("host.doctor", Safety.Read, "host", "Skizzy", Affinity.Main, Any,
                "The in-game half of `skizzy doctor`: versions, KSP build, KSPCommunityFixes MemoryLeaks setting, CommNet in the current game, " +
                "other autopilots (AtmosphereAutopilot, MechJeb, kOS), KJR, PhysicsRangeExtender, Harmony and KSPBurst notes, with green/yellow/red lines.",
                S.Obj().Closed());

            var spike = Def("host.debug.spike", Safety.Read, "host.debug", "Skizzy", Affinity.Main, Any,
                "Runs one of the M0 spikes in the real game and returns measured results: jpg_thread (ImageConversion.EncodeArrayToJPG off the main thread), " +
                "async_readback (AsyncGPUReadback support and cost on this graphics API), sync_context (UnitySynchronizationContext idle wake latency), " +
                "nostdlib (assembly references and runtime), or all.",
                S.Obj()
                    .Prop("name", S.Enum("which spike", "jpg_thread", "async_readback", "sync_context", "nostdlib", "all").Default("all"))
                    .Closed());
            spike.LongRunning = true;
            yield return spike;

            yield return Def("host.debug.confirm_test", Safety.Destructive, "host.debug", "Skizzy", Affinity.Any, Any,
                "Does nothing, but is classified destructive so the user's 'ask' prompt can be tested end to end. Returns {confirmed:true} if approved.",
                S.Obj().Prop("note", S.Str("shown in the prompt").MaxLen(200)).Closed());

            // ---------------- events / permissions ----------------
            var poll = Def("events.poll", Safety.Read, "core", "Skizzy", Affinity.Any, Any,
                "Reads events from the host's ring buffer (8192 events) after `cursor`. Namespaces: vessel.*, sim.*, host.*, rec.*. " +
                "Returns {events, nextCursor, resyncRequired}. Use nextCursor in the next call.",
                S.Obj()
                    .Prop("cursor", S.Int("return events with cursor > this; omit for the most recent `limit` events").Min(0))
                    .Prop("limit", S.Int("max events").Min(1).Max(256).Default(50))
                    .Prop("names", S.Arr(S.Str().MaxLen(64), "event name globs, e.g. [\"vessel.*\", \"sim.paused\"]").MaxItems(32))
                    .Prop("format", Format("json"))
                    .Closed());
            poll.ListShaped = true;
            yield return poll;

            var wait = Def("events.wait", Safety.Read, "core", "Skizzy", Affinity.Any, Any,
                "Long-poll: waits up to timeoutS for events after `cursor` that match `names`, then returns them (or an empty list on timeout). " +
                "MCP has no server push, so agents call this to be woken by game events instead of polling.",
                S.Obj()
                    .Prop("cursor", S.Int("wait for events with cursor > this; omit to wait for new events from now").Min(0))
                    .Prop("names", S.Arr(S.Str().MaxLen(64), "event name globs").MaxItems(32))
                    .Prop("timeoutS", S.Num("seconds to wait").Min(0).Max(120).Default(20))
                    .Prop("maxEvents", S.Int("max events returned").Min(1).Max(100).Default(20))
                    .Prop("format", Format("compact"))
                    .Closed(), Core);
            wait.ListShaped = true;
            wait.McpName = "events_wait";
            yield return wait;

            yield return Def("permissions.get", Safety.Read, "core", "Skizzy", Affinity.Any, Any,
                "The effective permission decision for this session: for one capability (allow/ask/deny with the matched rule), or a summary of levels.",
                S.Obj().Prop("capability", S.Str("capability name").MaxLen(128)).Closed());

            yield return Def("permissions.request", Safety.Read, "core", "Skizzy", Affinity.Any, Any,
                "Asks the human in game to grant a capability for this session. Your reason is shown labelled as AI-supplied text. " +
                "Returns {status: granted|denied|timeout, scope}. Nothing over the wire can change permissions without the human.",
                S.Obj()
                    .Prop("capability", S.Str("capability name").MinLen(1).MaxLen(128), true)
                    .Prop("reason", S.Str("why you need it").MaxLen(300))
                    .Closed());

            // ---------------- ksp reads ----------------
            var list = Def("ksp.vessels.list", Safety.Read, "ksp.read", "Squad", Affinity.Main, VesselScenes,
                "Lists vessels with handle, name, type, situation, body, lat/lon/altitude, state (live, packed, unloaded), parts, crew, " +
                "surface speed and whether it is the active vessel. Debris is excluded unless includeDebris. Paged: use `cursor`.",
                S.Obj()
                    .Prop("loaded", S.Bool("only loaded (true) or only unloaded (false) vessels"))
                    .Prop("nameLike", S.Str("glob on the name, e.g. \"DDG*\"").MaxLen(256))
                    .Prop("type", S.StrOrList("vessel types, e.g. Ship, Plane"))
                    .Prop("includeDebris", S.Bool("include debris").Default(false))
                    .Prop("limit", Limit(50, 1000))
                    .Prop("cursor", Cursor())
                    .Prop("format", Format("json"))
                    .Closed(), Core);
            list.ListShaped = true;
            list.McpDefaults = JsonValue.Obj().Set("format", "compact");
            yield return list;

            var get = Def("ksp.vessel.get", Safety.Read, "ksp.read", "Squad", Affinity.Main, VesselScenes,
                "Reads one vessel. depth 0: summary (name, situation, speed, altitude, heading, throttle, parts, crew, controllable, " +
                "BDArmory weapon manager / AI present); depth 1: + parts (handle, name, title, modules); depth 2: + module handles. " +
                "Alias ksp.read. Use vessel:\"active\" for the vessel the player flies.",
                S.Obj()
                    .Prop("vessel", VesselRef().Default("active"))
                    .Prop("depth", S.Int("0 summary, 1 parts, 2 parts and modules").Min(0).Max(2).Default(0))
                    .Prop("maxParts", S.Int("cap on listed parts").Min(1).Max(2000).Default(200))
                    .Prop("format", Format("json"))
                    .Closed(), Core);
            get.Aliases = new[] { "ksp.read" };
            get.McpName = "ksp_read";
            get.McpDefaults = JsonValue.Obj().Set("format", "compact");
            yield return get;

            var query = Def("ksp.query", Safety.Read, "ksp.read", "Squad", Affinity.Main, FlightScenes,
                "Bulk read with one selector instead of many calls (04 \u00a72): vessels (active, loaded, handles, nameLike, type), optional parts " +
                "and modules (type), and the fields to read. Vessel fields: " + VesselFieldList() + ". Module fields are the module's KSPField " +
                "names (see ksp.module.describe). aggregate: perVessel (one row per vessel; module fields reduced with `reduce`), perModule or perPart.",
                S.Obj()
                    .Prop("select", Selector())
                    .Prop("fields", Fields(), true)
                    .Prop("aggregate", S.Enum("row granularity", "perVessel", "perPart", "perModule").Default("perVessel"))
                    .Prop("reduce", S.Enum("how perVessel combines several modules' numeric values", "first", "sum", "min", "max", "avg").Default("first"))
                    .Prop("limit", Limit(50, 1000))
                    .Prop("cursor", Cursor())
                    .Prop("format", Format("json"))
                    .Closed(), Core);
            query.ListShaped = true;
            yield return query;

            yield return Def("ksp.module.describe", Safety.Read, "ksp.read", "Squad", Affinity.Main, new[] { "FLIGHT", "EDITOR", "SPACECENTER" },
                "Describes a part module type (Tier 0): its KSPFields with JSON Schema types, ranges and units from the UI controls, " +
                "whether each is shown in the part action window, plus events and actions. Give `type` (class name) or a `module` handle.",
                S.Obj()
                    .Prop("type", S.Str("module class name, e.g. MissileFire").MaxLen(128))
                    .Prop("module", S.Str("module handle m:<partPid>:<index>:<Class>").Handle("module").MaxLen(160))
                    .Closed(), Core);

            yield return Def("ksp.module.get", Safety.Read, "ksp.read", "Squad", Affinity.Main, FlightScenes,
                "Reads field values of one part module by handle (m:<partPid>:<index>:<Class>, from ksp.vessel.get depth 2 or ksp.query).",
                S.Obj()
                    .Prop("module", S.Str("module handle").Handle("module").MinLen(3).MaxLen(160), true)
                    .Prop("fields", S.Arr(S.Str().MaxLen(128), "field names; omit for all").MaxItems(128))
                    .Closed(), Core);

            var mods = Def("ksp.mods.list", Safety.Read, "ksp.read", "Squad", Affinity.Main, Any,
                "Lists loaded mod assemblies (name, version, GameData folder, file) and GameData top-level folders.",
                S.Obj()
                    .Prop("nameLike", S.Str("glob on assembly or folder name").MaxLen(128))
                    .Prop("limit", Limit(100, 1000))
                    .Prop("cursor", Cursor())
                    .Closed(), Core);
            mods.ListShaped = true;
            yield return mods;

            var tail = Def("ksp.log.tail", Safety.Read, "ksp.read", "Squad", Affinity.Any, Any,
                "Returns the last lines of KSP.log (read from disk; the host never hooks Unity's log). Filter by level or substring.",
                S.Obj()
                    .Prop("lines", S.Int("lines to return").Min(1).Max(200).Default(50))
                    .Prop("level", S.Enum("minimum level", "all", "warning", "error").Default("all"))
                    .Prop("contains", S.Str("only lines containing this text (case-insensitive)").MaxLen(200))
                    .Closed(), Core);
            tail.ListShaped = true;
            yield return tail;

            // ---------------- camera ----------------
            var shotSchema = S.Obj()
                .Prop("width", S.Int("image width in pixels").Min(256).Max(1920).Default(1024))
                .Prop("format", S.Enum("image format", "jpeg", "png").Default("jpeg"))
                .Prop("quality", S.Int("JPEG quality").Min(10).Max(95).Default(75))
                .Prop("hideUi", S.Bool("hide the KSP UI for one frame").Default(false))
                .Prop("target", VesselRef("optional vessel to point the camera at first (does not change the active vessel)"))
                .Prop("mode", S.Enum("optional camera mode to set first", "follow", "orbit", "chase", "free", "auto", "orbital", "locked"))
                .Prop("orbit", OrbitObj());
            var shot = Def("ksp.camera.screenshot", Safety.Read, "ksp.camera", "Squad", Affinity.Main, Any,
                "Takes a screenshot of what the player sees and returns it as an image. Optionally targets a vessel and sets a camera mode " +
                "and orbit first, in one call (e.g. chase view of DDG-1). Limited to 1 capture per 2 s per session. ~1024 px is enough for an LLM.",
                shotSchema.Closed(), Core);
            yield return shot;

            var saveSchema = S.Obj()
                .Prop("name", S.Str("file name stem (letters, digits, - _ .); default is a timestamp").Pattern("^[A-Za-z0-9 _.-]{1,64}$"))
                .Prop("width", S.Int("image width in pixels").Min(256).Max(1920).Default(1920))
                .Prop("format", S.Enum("image format", "jpeg", "png").Default("png"))
                .Prop("quality", S.Int("JPEG quality").Min(10).Max(95).Default(90))
                .Prop("hideUi", S.Bool("hide the KSP UI for one frame").Default(false))
                .Prop("target", VesselRef("optional vessel to point the camera at first"))
                .Prop("mode", S.Enum("optional camera mode", "follow", "orbit", "chase", "free", "auto", "orbital", "locked"))
                .Prop("returnImage", S.Bool("also return the image").Default(false));
            yield return Def("ksp.camera.save", Safety.Read, "ksp.camera", "Squad", Affinity.Main, Any,
                "Same capture as ksp.camera.screenshot, written to Skizzy/screenshots/<date>/<name>.<ext> under the KSP folder. Returns the path.",
                saveSchema.Closed(), Core);

            yield return Def("ksp.camera.set_mode", Safety.Control, "ksp.camera", "Squad", Affinity.Main, FlightScenes,
                "Sets the flight camera mode (13 \u00a71.2): follow (auto mode on the target), orbit (heading/pitch/distance), chase (behind the " +
                "velocity vector), free (stays in place), look_at (keeps `to` behind the target in view), locked. Never changes the active vessel.",
                S.Obj()
                    .Prop("mode", S.Enum("camera mode", "follow", "orbit", "chase", "free", "look_at", "auto", "orbital", "locked"), true)
                    .Prop("target", VesselRef("vessel to target first (default: current camera target)"))
                    .Prop("to", VesselRef("look_at: the vessel to keep in view beyond the target"))
                    .Prop("orbit", OrbitObj())
                    .Closed(), Core);

            yield return Def("ksp.camera.target", Safety.Control, "ksp.camera", "Squad", Affinity.Main, FlightScenes,
                "Points the camera at a vessel without changing the active vessel (leases and the human's controls are unaffected).",
                S.Obj().Prop("vessel", VesselRef(), true).Closed());

            yield return Def("ksp.camera.orbit", Safety.Control, "ksp.camera", "Squad", Affinity.Main, FlightScenes,
                "Sets heading, pitch and distance of the camera around its target (clamped to KSP's limits).",
                OrbitObj());

            yield return Def("ksp.camera.look_at", Safety.Control, "ksp.camera", "Squad", Affinity.Main, FlightScenes,
                "Frames an engagement: the camera sits behind `from` so that `to` is visible beyond it, updated every frame until another camera call.",
                S.Obj()
                    .Prop("from", VesselRef("near vessel"), true)
                    .Prop("to", VesselRef("far vessel to keep in view"), true)
                    .Prop("distance", S.Num("camera distance behind `from`, metres").Min(5).Max(20000).Default(60).Unit("m"))
                    .Prop("elevationDeg", S.Num("camera elevation").Min(-30).Max(60).Default(10).Unit("deg"))
                    .Closed());

            yield return Def("ksp.camera.state", Safety.Read, "ksp.camera", "Squad", Affinity.Main, Any,
                "Current camera mode, target, heading, pitch, distance and whether look_at is active.",
                S.Obj().Closed(), Core);

            // ---------------- log v0 ----------------
            var start = Def("rec.log.start", Safety.Control, "rec", "Skizzy", Affinity.Main, FlightScenes,
                "Starts logging fields to CSV or JSONL (12 \u00a71.1) at a fixed rate or on change, from a ksp.query-style selector. " +
                "Writes Skizzy/logs/<date>/<name>.<csv|jsonl> under the KSP folder with columns ut, frame, wallUtc, vessel, vesselName " +
                "(+ part, module for perModule) and the fields. Returns {log, path, columns}. Stop with rec.log.stop.",
                S.Obj()
                    .Prop("name", S.Str("log name, used as the file name").Pattern("^[A-Za-z0-9_.-]{1,64}$"), true)
                    .Prop("select", Selector())
                    .Prop("fields", Fields(), true)
                    .Prop("rateHz", S.Num("samples per second of game time").Min(0.1).Max(50).Default(5))
                    .Prop("onChange", S.Obj("write a row only when a value changes by more than deadband (rateHz then caps the check rate)")
                        .Prop("deadband", S.Num("absolute change threshold for numbers").Min(0).Default(0)).Closed())
                    .Prop("format", S.Enum("file format", "csv", "jsonl").Default("csv"))
                    .Prop("aggregate", S.Enum("row granularity", "perVessel", "perPart", "perModule").Default("perVessel"))
                    .Prop("reduce", S.Enum("perVessel reduction of several modules", "first", "sum", "min", "max", "avg").Default("first"))
                    .Prop("maxMB", S.Num("stop when the file reaches this size").Min(1).Max(10000).Default(200).Unit("MB"))
                    .Closed(), Core);
            yield return start;

            var stop = Def("rec.log.stop", Safety.Control, "rec", "Skizzy", Affinity.Main, Any,
                "Stops a log started with rec.log.start and closes its file. Returns the path, row count and bytes.",
                S.Obj().Prop("log", S.Str("log id \"log:N\" or the log name").MinLen(1).MaxLen(80), true).Closed(), Core);
            stop.KillSwitchExempt = true;
            yield return stop;

            var llist = Def("rec.log.list", Safety.Read, "rec", "Skizzy", Affinity.Any, Any,
                "Lists running and recently stopped logs with path, rate, rows, bytes and state.",
                S.Obj().Closed(), Core);
            llist.ListShaped = true;
            yield return llist;

            var ltail = Def("rec.log.tail", Safety.Read, "rec", "Skizzy", Affinity.Any, Any,
                "Returns the last n rows of a log (running or stopped) as compact text or JSON.",
                S.Obj()
                    .Prop("log", S.Str("log id \"log:N\" or name").MinLen(1).MaxLen(80), true)
                    .Prop("n", S.Int("rows").Min(1).Max(50).Default(20))
                    .Prop("format", Format("compact"))
                    .Closed(), Core);
            ltail.ListShaped = true;
            yield return ltail;

            yield return Def("rec.get_file", Safety.Read, "rec", "Skizzy", Affinity.Any, Any,
                "Reads a chunk (up to 1 MB) of a file under Skizzy/logs or Skizzy/screenshots, base64 encoded. Loop with offset until eof.",
                S.Obj()
                    .Prop("path", S.Str("path relative to the KSP folder, e.g. Skizzy/logs/2026-11-08/climb.csv").MinLen(1).MaxLen(400), true)
                    .Prop("offset", S.Int("byte offset").Min(0).Default(0))
                    .Prop("length", S.Int("bytes to read").Min(1).Max(1048576).Default(1048576))
                    .Closed());

            foreach (var d in C2()) yield return d;
        }

        // ---------------- c2 (09 §2, §11.1; 24 Phase v1). Executed by Skizzy.C2 through the BDArmory binding. ----------------

        private const string C2Mod = "BDArmory";
        private const string C2Note = " Needs the BDArmory binding (EXECUTOR_UNAVAILABLE without it).";

        private static SchemaB UnitRef(string desc)
        {
            return S.Str(desc + ": a handle \"v:<id>\", a vessel id, an exact unit name, or \"active\".").Handle("vessel").MinLen(1).MaxLen(256);
        }

        private static SchemaB GeoPt(string desc)
        {
            return S.Obj(desc)
                .Prop("lat", S.Num("latitude").Min(-90).Max(90).Unit("deg"), true)
                .Prop("lon", S.Num("longitude").Min(-180).Max(360).Unit("deg"), true)
                .Closed();
        }

        private static SchemaB SideName(string desc)
        {
            return S.Str(desc + " Defaults to the session's side.").MinLen(1).MaxLen(64);
        }

        private static IEnumerable<CapabilityDef> C2()
        {
            yield return Def("c2.order.issue", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Issues an order to units, a group or a whole side (09 \u00a72.1). Types: move_to (point, altitudeM), move_route (route), " +
                "engage (target or targets), hold_pattern (patternA, patternB, widthKm, turnRight, altStackM), rtb (base, weapons), " +
                "defend_unit (protect, radiusKm), escort (escortOf, slot, distanceKm), attack_nearest, all_attack (scope side). " +
                "Returns the order handle o:<id>, a summary line and per-unit outcomes; a whole-order refusal is an error whose reason is " +
                "the refusal code (not_own_side, hook_missing, winchester, ...). Progress arrives as order.* events." + C2Note,
                S.Obj()
                    .Prop("type", S.Enum("order type", "move_to", "move_route", "engage", "hold_pattern", "rtb", "defend_unit", "attack_nearest", "all_attack", "escort"), true)
                    .Prop("side", SideName("Issuing side; units of other sides are refused."))
                    .Prop("units", S.Arr(UnitRef("unit"), "recipients").MaxItems(256))
                    .Prop("group", S.Str("recipient group").MaxLen(64))
                    .Prop("scope", S.Enum("side: every unit of the side (rtb all, all_attack; needs the seat or a referee)", "units", "side").Default("units"))
                    .Prop("domain", S.Enum("side-scope filter", "air", "surface", "subsurface", "land"))
                    .Prop("label", S.Str("label for reports").MaxLen(120))
                    .Prop("point", GeoPt("move_to destination"))
                    .Prop("altitudeM", S.Num("aircraft altitude above terrain; omit to keep").Min(0).Max(30000).Unit("m"))
                    .Prop("route", S.Arr(GeoPt("route point"), "move_route points").MinItems(1).MaxItems(64))
                    .Prop("target", UnitRef("engage target"))
                    .Prop("targets", S.Arr(UnitRef("target"), "explicit engage targets").MaxItems(64))
                    .Prop("patternA", GeoPt("hold_pattern first end"))
                    .Prop("patternB", GeoPt("hold_pattern second end"))
                    .Prop("widthKm", S.Num("hold_pattern width").Min(0.5).Max(100).Unit("km"))
                    .Prop("turnRight", S.Bool("hold_pattern turn direction").Default(true))
                    .Prop("altStackM", S.Num("hold_pattern altitude step between units").Min(0).Max(5000).Unit("m"))
                    .Prop("base", UnitRef("rtb base (carrier or airfield unit); omit for the unit's home"))
                    .Prop("weapons", S.Enum("rtb weapons control on the way home", "free", "tight", "hold"))
                    .Prop("protect", UnitRef("defend_unit protected unit"))
                    .Prop("radiusKm", S.Num("defend_unit radius").Min(1).Max(200).Unit("km"))
                    .Prop("escortOf", UnitRef("escort leader"))
                    .Prop("slot", S.Enum("escort slot", "ahead", "abreast", "astern").Default("ahead"))
                    .Prop("distanceKm", S.Num("escort distance").Min(0.5).Max(100).Unit("km"))
                    .Prop("extendRange", S.Bool("attack_nearest: raise ships' SAM range to the weapon maximum (always on for all_attack)").Default(false))
                    .Closed(), "c2");

            yield return Def("c2.order.cancel", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Cancels an order and all its unit orders; an engagement order gives units back their movement order." + C2Note,
                S.Obj().Prop("order", S.Str("order handle o:<id> (or the bare id)").Pattern("^(o:)?[0-9]+$"), true).Closed(), "c2");

            yield return Def("c2.doctrine.set", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Sets doctrine at side, group or unit level (09 \u00a711.1), e.g. values {weaponsControl:\"tight\", onContact:\"engage\", " +
                "advanceOnContact:false, pauseOnFirstLaunch:true}. lock makes keys binding for lower levels. Nothing changes on error; a key " +
                "locked higher up fails with reason locked_by:side|group." + C2Note,
                S.Obj()
                    .Prop("level", S.Enum("doctrine level", "side", "group", "unit"), true)
                    .Prop("target", S.Str("side name, group name, or unit reference; defaults to the session's side for level side").MinLen(1).MaxLen(256))
                    .Prop("values", S.Obj("doctrine keys and values (strings, numbers, booleans)").Open(), true)
                    .Prop("lock", S.Arr(S.Str().MaxLen(64), "keys to lock at this level").MaxItems(64))
                    .Closed(), "c2");

            yield return Def("c2.doctrine.unset", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Removes doctrine keys (and their locks) at one level so the next level up applies again." + C2Note,
                S.Obj()
                    .Prop("level", S.Enum("doctrine level", "side", "group", "unit"), true)
                    .Prop("target", S.Str("side name, group name, or unit reference; defaults to the session's side for level side").MinLen(1).MaxLen(256))
                    .Prop("keys", S.Arr(S.Str().MaxLen(64), "keys to remove").MinItems(1).MaxItems(64), true)
                    .Closed(), "c2");

            yield return Def("c2.doctrine.effective", Safety.Read, "c2", C2Mod, Affinity.Main, FlightScenes,
                "A unit's effective doctrine: every key with its value, the level it comes from (default, side:<s>, group:<g>, unit:v:<id>) and whether it is locked." + C2Note,
                S.Obj().Prop("unit", UnitRef("unit"), true).Closed(), "c2");

            yield return Def("c2.unit.status", Safety.Read, "c2", C2Mod, Affinity.Main, FlightScenes,
                "What the C2 engine is doing with a unit: task, movement and engagement orders with status, hold, fire authority and filter, " +
                "override target and stickiness, route, yield/leash, divert, self-defence, advance, VIP threat, weapons control, on-contact." + C2Note,
                S.Obj().Prop("unit", UnitRef("unit"), true).Closed(), "c2");

            yield return Def("c2.unit.release", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Returns units to plain BDArmory guard behaviour: cancels their orders (with reports) and clears holds. Doctrine still applies." + C2Note,
                S.Obj().Prop("units", S.Arr(UnitRef("unit"), "units to release").MinItems(1).MaxItems(256), true).Closed(), "c2");

            yield return Def("c2.vip.set", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Flags a unit as a VIP (high-value unit) or clears the flag. On a managed side, idle fighters orbit it, ships take station and threats to it get priority (24 rows 21-23)." + C2Note,
                S.Obj()
                    .Prop("unit", UnitRef("unit"), true)
                    .Prop("vip", S.Bool("true to flag, false to clear").Default(true))
                    .Closed(), "c2");

            yield return Def("c2.side.managed", Safety.Control, "c2", C2Mod, Affinity.Main, FlightScenes,
                "Puts a side under Skizzy control (advance on contact, VIP priority, missile engagement, ship SAM priority run without orders) or returns it to plain BDArmory. " +
                "Orders work for any side either way. Omit managed to read the state." + C2Note,
                S.Obj()
                    .Prop("side", SideName("Side."))
                    .Prop("managed", S.Bool("true to manage, false to release"))
                    .Closed(), "c2");
        }
    }
}
