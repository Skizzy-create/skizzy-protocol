using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Skizzy.Host.Core;
using Skizzy.Host.Core.World;
using Skizzy.Protocol;
using UnityEngine;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// <see cref="IGameWorld"/> for KSP (02 §1.1). Main thread only. Wrappers are cached per persistentId and checked
    /// against the live KSP object, so a scene reload or a persistentId change yields a fresh wrapper and the old one
    /// reports <c>IsAlive == false</c>.
    /// </summary>
    internal sealed class KspWorld : IGameWorld
    {
        private readonly HostPaths _paths;
        private readonly IHostLog _log;
        private readonly Dictionary<uint, KspVessel> _vessels = new Dictionary<uint, KspVessel>();
        private readonly Dictionary<uint, KspPart> _parts = new Dictionary<uint, KspPart>();
        private readonly List<ModInfo> _mods = new List<ModInfo>();
        private int _topology = 1;
        private int _epoch = 1;
        private float _sceneFixedTime;
        internal KspCamera CameraImpl;
        private static readonly string[] SceneNames = BuildSceneNames();

        public KspWorld(HostPaths paths, IHostLog log)
        {
            _paths = paths;
            _log = log;
        }

        private static string[] BuildSceneNames()
        {
            var vals = (GameScenes[])Enum.GetValues(typeof(GameScenes));
            int max = 0;
            foreach (var v in vals) if ((int)v > max) max = (int)v;
            var arr = new string[max + 1];
            foreach (var v in vals) if ((int)v >= 0) arr[(int)v] = v.ToString();
            return arr;
        }

        internal static string SceneName(GameScenes s)
        {
            int i = (int)s;
            return i >= 0 && i < SceneNames.Length && SceneNames[i] != null ? SceneNames[i] : "UNKNOWN";
        }

        // ---------------------------------------------------------------- scene and topology bookkeeping

        /// <summary>Scene load requested: every KSP object is about to be recreated (20 §5).</summary>
        internal void OnSceneChanging()
        {
            _epoch++;
            _topology++;
            _vessels.Clear();
            _parts.Clear();
        }

        internal void OnSceneReady()
        {
            _sceneFixedTime = Time.fixedTime;
            _topology++;
            _vessels.Clear();
            _parts.Clear();
        }

        internal void BumpTopology() { _topology++; }

        internal void Forget(Vessel v)
        {
            if (v == null) return;
            if (_vessels.TryGetValue(v.persistentId, out var w) && ReferenceEquals(w.V, v))
            {
                w.MarkDead();
                _vessels.Remove(v.persistentId);
            }
            _topology++;
        }

        internal KspVessel Wrap(Vessel v)
        {
            if (v == null) return null;
            uint pid = v.persistentId;
            if (_vessels.TryGetValue(pid, out var w) && ReferenceEquals(w.V, v)) return w;
            w = new KspVessel(this, v);
            _vessels[pid] = w;
            return w;
        }

        internal KspPart WrapPart(Part p)
        {
            if (p == null) return null;
            uint pid = p.persistentId;
            if (_parts.TryGetValue(pid, out var w) && ReferenceEquals(w.P, p)) return w;
            w = new KspPart(this, p);
            _parts[pid] = w;
            if (_parts.Count > 50000) _parts.Clear(); // bounded (20 §8); wrappers are rebuilt on demand
            return w;
        }

        private static bool HasVessels => HighLogic.LoadedSceneHasPlanetarium && FlightGlobals.fetch != null;

        // ---------------------------------------------------------------- IGameWorld

        // skizzy:hot KspWorld.ReadMeta
        public void ReadMeta(ref MetaState m)
        {
            var scene = HighLogic.LoadedScene;
            m.Scene = SceneName(scene);
            var game = HighLogic.CurrentGame;
            m.Ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : game != null ? game.UniversalTime : 0;
            float dt = Time.fixedDeltaTime;
            m.Frame = dt > 0 ? (long)((Time.fixedTime - _sceneFixedTime) / dt) : 0;
            m.TimeScale = Time.timeScale;
            m.Paused = (scene == GameScenes.FLIGHT && FlightDriver.Pause) || Time.timeScale == 0f;
            float rate = TimeWarp.fetch != null ? TimeWarp.CurrentRate : 1f;
            m.WarpRate = rate;
            m.WarpMode = rate <= 1.0001f ? "none" : TimeWarp.WarpMode == TimeWarp.Modes.HIGH ? "rails" : "physics";
            m.ActiveVessel = null;
            m.ActiveVesselName = null;
            if (scene == GameScenes.FLIGHT && FlightGlobals.ready)
            {
                var a = Wrap(FlightGlobals.ActiveVessel);
                if (a != null)
                {
                    m.ActiveVessel = a.Handle;
                    m.ActiveVesselName = a.V.vesselName;
                }
            }
            m.SceneTransition = false;
        }

        public int TopologyVersion => _topology;
        public int SceneEpoch => _epoch;

        public void GetVessels(List<GVessel> into)
        {
            if (!HasVessels) return;
            var list = FlightGlobals.Vessels;
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v != null && v.state != Vessel.State.DEAD) into.Add(Wrap(v));
            }
        }

        public GVessel FindVessel(uint persistentId)
        {
            if (!HasVessels) return null;
            return FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null ? Wrap(v) : null;
        }

        public GPart FindPart(uint persistentId)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !HasVessels) return null;
            return FlightGlobals.FindLoadedPart(persistentId, out Part p) && p != null ? WrapPart(p) : null;
        }

        public GVessel ActiveVessel => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready ? Wrap(FlightGlobals.ActiveVessel) : null;

        public ModuleTypeInfo DescribeModuleType(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            // 1. A live instance in a loaded vessel (exact runtime state, including ModuleManager-patched config).
            if (HasVessels)
            {
                var loaded = FlightGlobals.VesselsLoaded;
                for (int i = 0; i < loaded.Count; i++)
                {
                    var v = loaded[i];
                    if (v == null) continue;
                    for (int p = 0; p < v.parts.Count; p++)
                    {
                        var mods = v.parts[p].Modules;
                        for (int m = 0; m < mods.Count; m++)
                            if (mods[m] != null && mods[m].GetType().Name == className) return Describe(mods[m]);
                    }
                }
            }
            // 2. A part prefab (any scene after loading).
            var parts = PartLoader.LoadedPartsList;
            if (parts != null)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    var prefab = parts[i] != null ? parts[i].partPrefab : null;
                    if (prefab == null) continue;
                    var mods = prefab.Modules;
                    for (int m = 0; m < mods.Count; m++)
                        if (mods[m] != null && mods[m].GetType().Name == className) return Describe(mods[m]);
                }
            }
            // 3. The type alone (attributes only).
            Type t = AssemblyLoader.GetClassByName(typeof(PartModule), className);
            return t == null ? null : ModuleReflector.DescribeType(t);
        }

        private static ModuleTypeInfo Describe(PartModule m)
        {
            var t = m.GetType();
            return new ModuleTypeInfo
            {
                ClassName = t.Name,
                Assembly = t.Assembly.GetName().Name,
                Fields = ModuleReflector.Fields(m),
                Events = ModuleReflector.Events(m),
                Actions = ModuleReflector.Actions(m),
            };
        }

        public void GetMods(List<ModInfo> into)
        {
            if (_mods.Count == 0 && HighLogic.LoadedScene != GameScenes.LOADING) BuildMods();
            into.AddRange(_mods);
        }

        private void BuildMods()
        {
            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
            {
                string url = la.url ?? "";
                int slash = url.IndexOf('/');
                _mods.Add(new ModInfo
                {
                    Name = la.name,
                    Version = AssemblyVersion(la),
                    Folder = slash > 0 ? url.Substring(0, slash) : url,
                    File = "GameData/" + (url.Length > 0 ? url + "/" : "") + la.dllName,
                    Kind = "assembly",
                });
            }
            try
            {
                string gameData = Path.Combine(_paths.KspRoot, "GameData");
                var dirs = Directory.GetDirectories(gameData);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirs)
                {
                    string name = Path.GetFileName(d);
                    _mods.Add(new ModInfo { Name = name, Folder = name, Kind = "folder" });
                }
            }
            catch (Exception e) { _log.Warn("could not list GameData folders: " + e.Message); }
        }

        private static string AssemblyVersion(AssemblyLoader.LoadedAssembly la)
        {
            try
            {
                var attr = la.assembly != null
                    ? Attribute.GetCustomAttribute(la.assembly, typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute
                    : null;
                if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion)) return attr.InformationalVersion;
            }
            catch (Exception) { }
            return la.versionMajor + "." + la.versionMinor + "." + la.versionRevision;
        }

        public ICameraService Camera => CameraImpl;

        public string LogPath => _paths.KspLog;

        public void Doctor(List<string[]> lines)
        {
            string ksp = Versioning.GetVersionStringFull();
            lines.Add(new[] { ksp.StartsWith("1.12", StringComparison.Ordinal) ? "pass" : "warn", "KSP", ksp + " (Skizzy targets 1.12.x)" });
            lines.Add(new[] { "pass", "Unity", Application.unityVersion + ", " + SystemInfo.graphicsDeviceType + ", mscorlib " + typeof(object).Assembly.ImageRuntimeVersion });
            lines.Add(new[] { SystemInfo.supportsAsyncGPUReadback ? "pass" : "warn", "screenshots",
                SystemInfo.supportsAsyncGPUReadback ? "AsyncGPUReadback supported (fast path)" : "AsyncGPUReadback not supported on this graphics API: ReadPixels fallback (<= 30 ms per capture)" });
            lines.Add(new[] { "pass", "Skizzy host", ProtocolInfo.HostVersion + " from " + _paths.Relative(typeof(KspWorld).Assembly.Location) });

            var names = new Dictionary<string, List<AssemblyLoader.LoadedAssembly>>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
            {
                if (!names.TryGetValue(la.name, out var l)) names[la.name] = l = new List<AssemblyLoader.LoadedAssembly>();
                l.Add(la);
            }
            foreach (var own in new[] { "Skizzy.Host", "Skizzy.Protocol" })
                if (names.TryGetValue(own, out var dup) && dup.Count > 1)
                    lines.Add(new[] { "fail", "duplicate " + own, dup.Count + " copies loaded: " + Urls(dup) + " - keep only GameData/SkizzyProtocol" });

            // KSPCommunityFixes MemoryLeaks (the user's leak guard, 20 §9).
            if (names.ContainsKey("KSPCommunityFixes"))
            {
                string leaks = null;
                try
                {
                    var nodes = GameDatabase.Instance != null ? GameDatabase.Instance.GetConfigNodes("KSP_COMMUNITY_FIXES") : null;
                    if (nodes != null) foreach (var n in nodes) leaks = n.GetValue("MemoryLeaks") ?? leaks;
                }
                catch (Exception) { }
                bool on = leaks != null && leaks.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                lines.Add(new[] { on ? "pass" : "warn", "KSPCommunityFixes", on ? "MemoryLeaks = true" : "MemoryLeaks is not true (" + (leaks ?? "unset") + ")" });
            }
            else lines.Add(new[] { "warn", "KSPCommunityFixes", "not installed (its MemoryLeaks fix is recommended for long sessions)" });

            var game = HighLogic.CurrentGame;
            if (game != null && game.Parameters != null && game.Parameters.Difficulty != null)
                lines.Add(new[] { game.Parameters.Difficulty.EnableCommNet ? "warn" : "pass", "CommNet",
                    game.Parameters.Difficulty.EnableCommNet ? "on in this game: unconnected probes are not controllable and BDA guard mode stops" : "off in this game" });
            else lines.Add(new[] { "pass", "CommNet", "no game loaded yet" });

            var autopilots = new List<string>();
            foreach (var ap in new[] { "AtmosphereAutopilot", "MechJeb2", "kOS", "KRPC", "ThrottleControlledAvionics" })
                if (names.ContainsKey(ap)) autopilots.Add(ap);
            lines.Add(new[] { autopilots.Count == 0 ? "pass" : "warn", "other autopilots",
                autopilots.Count == 0 ? "none" : string.Join(", ", autopilots.ToArray()) + " (OnFlyByWire users; Skizzy M0 registers none)" });
            Presence(lines, names, "KerbalJointReinforcement", "KJR", "placement waits one settle frame (M1)");
            Presence(lines, names, "PhysicsRangeExtender", "PhysicsRangeExtender", "effective ranges are checked in preflight (M2)");
            Presence(lines, names, "ClickThroughBlocker", "ClickThroughBlocker", "in-game windows use it when present");
            Presence(lines, names, "ToolbarControl", "ToolbarControl", "toolbar button registers through it when present");

            if (names.TryGetValue("0Harmony", out var harmony))
            {
                var versions = new List<string>();
                foreach (var h in harmony)
                {
                    string v = h.assembly != null ? h.assembly.GetName().Version.ToString() : "?";
                    if (!versions.Contains(v)) versions.Add(v);
                }
                lines.Add(new[] { versions.Count > 1 ? "warn" : "pass", "Harmony", harmony.Count + " loaded (" + string.Join(", ", versions.ToArray()) + "); Skizzy applies no patches" });
            }
            var sys = new List<string>();
            foreach (var kv in names)
                if (kv.Key.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || kv.Key == "netstandard") sys.Add(kv.Key + " (" + Urls(kv.Value) + ")");
            lines.Add(new[] { "pass", "System.* in GameData", sys.Count == 0 ? "none" : string.Join("; ", sys.ToArray()) + " - Skizzy references none of them" });
        }

        private static void Presence(List<string[]> lines, Dictionary<string, List<AssemblyLoader.LoadedAssembly>> names, string asm, string label, string note)
        {
            lines.Add(new[] { "pass", label, names.ContainsKey(asm) ? "installed: " + note : "not installed" });
        }

        private static string Urls(List<AssemblyLoader.LoadedAssembly> l)
        {
            var s = new List<string>();
            foreach (var a in l) s.Add("GameData/" + a.url + "/" + a.dllName);
            return string.Join(", ", s.ToArray());
        }
    }
}
