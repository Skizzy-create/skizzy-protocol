using System;
using System.Collections.Generic;
using System.Globalization;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.World
{
    /// <summary>
    /// Resolves client references to world objects and explains failures (03 §9 NOT_FOUND reasons, 20 §5):
    /// present → object; destroyed recently → <c>destroyed</c>; seen in an earlier scene epoch or before a
    /// persistentId change → <c>stale</c>; otherwise <c>unknown</c>. Main thread only.
    /// </summary>
    public sealed class HandleRegistry
    {
        private readonly IGameWorld _world;
        private readonly Dictionary<uint, double> _destroyedAtMs = new Dictionary<uint, double>();
        private readonly Dictionary<uint, int> _seenEpoch = new Dictionary<uint, int>();
        private readonly Dictionary<uint, uint> _pidChanged = new Dictionary<uint, uint>();
        private readonly List<GVessel> _scratch = new List<GVessel>();
        private const double DestroyedMemoryMs = 10 * 60 * 1000;

        public HandleRegistry(IGameWorld world) { _world = world; }

        public void NoteSeen(uint pid) { _seenEpoch[pid] = _world.SceneEpoch; }

        public void NoteDestroyed(uint pid)
        {
            _destroyedAtMs[pid] = Clock.NowMs;
            if (_destroyedAtMs.Count > 4096) Prune();
        }

        public void NotePidChanged(uint oldPid, uint newPid) { _pidChanged[oldPid] = newPid; }

        private void Prune()
        {
            double now = Clock.NowMs;
            var old = new List<uint>();
            foreach (var kv in _destroyedAtMs) if (now - kv.Value > DestroyedMemoryMs) old.Add(kv.Key);
            foreach (var k in old) _destroyedAtMs.Remove(k);
        }

        public static string VesselHandle(uint pid) { return "v:" + pid.ToString(CultureInfo.InvariantCulture); }

        public static bool TryParseVessel(string s, out uint pid)
        {
            pid = 0;
            return s != null && s.Length > 2 && s[0] == 'v' && s[1] == ':' &&
                   uint.TryParse(s.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out pid);
        }

        /// <summary>
        /// Resolves "active", a handle "v:&lt;pid&gt;" or an exact vessel name (case-insensitive if unique).
        /// Throws NOT_FOUND or INVALID_PARAMS (ambiguous name, with candidates).
        /// </summary>
        public GVessel ResolveVessel(string reference, string pointer = "/vessel")
        {
            if (string.IsNullOrEmpty(reference) || reference == "active")
            {
                var a = _world.ActiveVessel;
                if (a == null) throw SkizzyError.NotFound("active", "unknown", "There is no active vessel in this scene; list vessels with ksp.vessels.list and pass a handle.");
                NoteSeen(a.PersistentId);
                return a;
            }
            if (TryParseVessel(reference, out uint pid))
            {
                var v = _world.FindVessel(pid);
                if (v != null && v.IsAlive) { NoteSeen(pid); return v; }
                throw Missing(reference, pid);
            }
            _scratch.Clear();
            _world.GetVessels(_scratch);
            GVessel exact = null;
            var ci = new List<GVessel>();
            foreach (var v in _scratch)
            {
                if (!v.IsAlive) continue;
                if (v.Name == reference)
                {
                    if (exact != null) throw Ambiguous(reference, pointer);
                    exact = v;
                }
                else if (string.Equals(v.Name, reference, StringComparison.OrdinalIgnoreCase)) ci.Add(v);
            }
            if (exact != null) { NoteSeen(exact.PersistentId); return exact; }
            if (ci.Count == 1) { NoteSeen(ci[0].PersistentId); return ci[0]; }
            if (ci.Count > 1) throw Ambiguous(reference, pointer);
            var names = new List<string>();
            foreach (var v in _scratch) if (v.IsAlive) names.Add(v.Name);
            string guess = Skizzy.Protocol.Util.Levenshtein.Closest(reference, names);
            throw SkizzyError.NotFound(reference, "unknown",
                guess != null ? "No vessel named '" + Skizzy.Protocol.Util.CompactText.SafeName(reference) + "'. Did you mean '" + guess + "'?"
                              : "No such vessel; list vessels with ksp.vessels.list and pass a handle like v:123.");
        }

        private SkizzyError Ambiguous(string name, string pointer)
        {
            var cands = JsonValue.Arr();
            foreach (var v in _scratch)
                if (v.IsAlive && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    cands.Add(JsonValue.Obj().Set("handle", v.Handle).Set("name", v.Name).Set("type", v.TypeName));
            var extra = JsonValue.Obj().Set("candidates", cands)
                .Set("errors", JsonValue.Arr().Add(JsonValue.Obj().Set("pointer", pointer).Set("message", "ambiguous vessel name")));
            return new SkizzyError(ErrorKind.InvalidParams, "Several vessels are named '" + Skizzy.Protocol.Util.CompactText.SafeName(name) + "'.",
                "ambiguous", "Pass one of the candidate handles instead of the name.", extra);
        }

        private SkizzyError Missing(string handle, uint pid)
        {
            if (_pidChanged.TryGetValue(pid, out uint now))
                return SkizzyError.NotFound(handle, "stale", "The vessel's persistentId changed (dock/undock or reload); its new handle is " + VesselHandle(now) + ".");
            if (_destroyedAtMs.ContainsKey(pid))
                return SkizzyError.NotFound(handle, "destroyed", "That vessel was destroyed; list vessels again.");
            if (_seenEpoch.TryGetValue(pid, out int epoch) && epoch != _world.SceneEpoch)
                return SkizzyError.NotFound(handle, "stale", "The handle is from before a scene change; list vessels again to get current handles.");
            return SkizzyError.NotFound(handle, "unknown", "No vessel with that handle; list vessels with ksp.vessels.list.");
        }

        /// <summary>Resolves "p:&lt;pid&gt;".</summary>
        public GPart ResolvePart(string handle)
        {
            if (handle == null || !handle.StartsWith("p:", StringComparison.Ordinal) ||
                !uint.TryParse(handle.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out uint pid))
                throw SkizzyError.InvalidParam("/part", "part handles look like p:<persistentId>");
            var p = _world.FindPart(pid);
            if (p == null) throw SkizzyError.NotFound(handle, StaleOrUnknown(pid), "Part not found (unloaded vessel or old handle); read the vessel again.");
            return p;
        }

        /// <summary>Resolves "m:&lt;partPid&gt;:&lt;index&gt;:&lt;Class&gt;"; the class name must still match.</summary>
        public GModule ResolveModule(string handle, string pointer = "/module")
        {
            if (handle == null) throw SkizzyError.InvalidParam(pointer, "missing module handle");
            var parts = handle.Split(':');
            if (parts.Length != 4 || parts[0] != "m" ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
                throw SkizzyError.InvalidParam(pointer, "module handles look like m:<partPid>:<index>:<Class>");
            var p = _world.FindPart(pid);
            if (p == null) throw SkizzyError.NotFound(handle, StaleOrUnknown(pid), "Part not loaded or gone; read the vessel again (depth 2) for current module handles.");
            if (idx < 0 || idx >= p.ModuleCount || p.GetModule(idx).ClassName != parts[3])
                throw SkizzyError.NotFound(handle, "stale", "The part's modules changed; read the vessel again (depth 2) for current module handles.");
            NoteSeen(pid);
            return p.GetModule(idx);
        }

        private string StaleOrUnknown(uint pid)
        {
            return _seenEpoch.TryGetValue(pid, out int e) && e != _world.SceneEpoch ? "stale" : "unknown";
        }
    }
}
