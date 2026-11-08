using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Protocol;
using Skizzy.Protocol.Config;
using UnityEngine;
using UnityEngine.Profiling;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// The KSP entry point (02 §1.1): created once at startup and kept across scenes. It owns the host core, opens the
    /// socket and writes <c>endpoint.json</c>, and adds the pump, which is disabled whenever nothing is connected or
    /// running. It registers no <c>OnFlyByWire</c> callback, no Harmony patch, no engine tick, no PartModule,
    /// VesselModule or ScenarioModule (acceptance 1; 20 §5: saves are never touched). This behaviour itself has no
    /// Update/FixedUpdate/OnGUI, so Unity never calls it per frame; it only hosts coroutines for captures.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class SkizzyBootstrap : MonoBehaviour
    {
        internal static SkizzyBootstrap Instance;
        internal static string StartFailure;

        internal HostCore Host;
        internal KspWorld World;
        internal KspCamera Camera;
        internal KspEvents Events;
        internal KspWaker Waker;
        internal SkizzyPump Pump;
        internal KspLog Log;
        private IdleProbe _probe;
        private bool _stopped;
        internal volatile bool PumpAwake;
        private KeyCode _killKey = KeyCode.K;
        private bool _killCtrl = true, _killAlt = true, _killShift;

        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            try { Init(); }
            catch (Exception e)
            {
                StartFailure = e.GetType().Name + ": " + e.Message;
                Debug.LogError("[Skizzy] host failed to start: " + e);
            }
        }

        private void Init()
        {
            string kspRoot = KSPUtil.ApplicationRootPath;
            string pluginData = ResolvePluginData(kspRoot);
            Directory.CreateDirectory(pluginData);
            var paths = new HostPaths(kspRoot, pluginData);
            var ctx = SynchronizationContext.Current;
            Log = new KspLog(paths.HostLog) { MainThreadId = Thread.CurrentThread.ManagedThreadId, MainContext = ctx };

            CfgNode node = LoadSettings(paths.SettingsFile);
            var settings = HostSettings.FromNode(node);
            // In the game the per-user endpoint pointer is on unless settings.cfg turns it off (02 §6, discovery order).
            if (node == null || !node.HasValue("writeUserPointer")) settings.WriteUserPointer = true;
            if (node == null) WriteDefaultSettings(paths.SettingsFile, settings);

            World = new KspWorld(paths, Log);
            Waker = new KspWaker(ctx);
            Host = new HostCore(settings, paths, World, Waker, Log);
            Camera = new KspCamera(this, Host, World, Log);
            World.CameraImpl = Camera;
            string flip = node != null ? node.GetValue("captureFlip") : null;
            if (flip == "true") Camera.FlipOverride = true;
            else if (flip == "false") Camera.FlipOverride = false;

            Events = new KspEvents(Host, World, Log) { SceneChanging = () => Camera.OnSceneChanging() };
            Events.AddScene();
            Host.SelfCheckExtras = SelfCheck;
            Host.CountUnityObjects = Camera.CountObjects;
            KspSpikes.Register(Host, this);
            ParseKillKey(settings.KillSwitchKey);

            Log.Info("Skizzy host " + ProtocolInfo.HostVersion + " starting: KSP " + Versioning.GetVersionStringFull() + ", Unity " + Application.unityVersion +
                     ", " + SystemInfo.graphicsDeviceType + ", idle wake " + Waker.Mode + " (" + Waker.ContextType + "), pluginData " + paths.Relative(pluginData));
            if (VesselCodes.Missing().Count > 0) Log.Warn("vessel fields without a KSP reader: " + string.Join(", ", VesselCodes.Missing().ToArray()));
            if (!KspPart.TableMatches()) Log.Error("part field table changed: KspPart.ReadField needs updating");

            Host.Start();
            if (Host.StartError != null) Log.Error("socket not opened: " + Host.StartError);

            Pump = gameObject.AddComponent<SkizzyPump>();
            Pump.enabled = false;
            Pump.Host = Host;
            Pump.Waker = Waker;
            Pump.Boot = this;
            Waker.Pump = Pump;
            Pump.enabled = true; // one frame, then it sleeps unless something is connected or running

            if (settings.IdleProbe)
            {
                _probe = new IdleProbe(Host, this, paths.IdleProbeFile);
                _probe.Start();
                Log.Info("idle probe on: writing " + paths.Relative(paths.IdleProbeFile));
            }
        }

        private static string ResolvePluginData(string kspRoot)
        {
            try
            {
                string plugins = Path.GetDirectoryName(typeof(SkizzyBootstrap).Assembly.Location);
                string mod = plugins != null ? Path.GetDirectoryName(plugins) : null;
                if (mod != null && string.Equals(Path.GetFileName(mod), "SkizzyProtocol", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(mod, "PluginData");
            }
            catch (Exception) { }
            return Path.Combine(Path.Combine(Path.Combine(kspRoot, "GameData"), "SkizzyProtocol"), "PluginData");
        }

        private static CfgNode LoadSettings(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var root = CfgNode.Parse(File.ReadAllText(file));
                return root.GetNode("SKIZZY_SETTINGS") ?? root;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Skizzy] settings.cfg unreadable, using defaults: " + e.Message);
                return null;
            }
        }

        private static void WriteDefaultSettings(string file, HostSettings s)
        {
            try
            {
                var n = s.ToNode();
                n.AddValue("captureFlip", "auto");
                File.WriteAllText(file, "// Skizzy host settings (02 §6). Edit while KSP is closed.\n" + n.Serialize());
            }
            catch (Exception) { }
        }

        private void ParseKillKey(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;
            bool ctrl = false, alt = false, shift = false;
            KeyCode key = KeyCode.None;
            foreach (var raw in spec.Split('+'))
            {
                string p = raw.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") ctrl = true;
                else if (p == "alt") alt = true;
                else if (p == "shift") shift = true;
                else
                {
                    try { key = (KeyCode)Enum.Parse(typeof(KeyCode), p, true); }
                    catch (Exception) { key = KeyCode.None; }
                }
            }
            if (key == KeyCode.None) { Log.Warn("killSwitchKey '" + spec + "' not understood; using ctrl+alt+k"); return; }
            _killKey = key;
            _killCtrl = ctrl;
            _killAlt = alt;
            _killShift = shift;
        }

        // ---------------------------------------------------------------- pump callbacks (main thread)

        internal void OnPumpAwake()
        {
            PumpAwake = true;
            Events.AddDynamic();
        }

        internal void OnPumpAsleep()
        {
            PumpAwake = false;
            Events.RemoveDynamic();
            Log.FlushUnity();
        }

        /// <summary>Every frame while the pump is awake (so only while a client, log, stream or op exists).</summary>
        // skizzy:hot SkizzyBootstrap.OnFrame
        internal void OnFrame()
        {
            Log.FlushUnity();
            if (Input.GetKeyDown(_killKey) && Held(_killCtrl, KeyCode.LeftControl, KeyCode.RightControl) &&
                Held(_killAlt, KeyCode.LeftAlt, KeyCode.RightAlt) && Held(_killShift, KeyCode.LeftShift, KeyCode.RightShift))
                ToggleKill();
        }

        private static bool Held(bool needed, KeyCode a, KeyCode b) { return !needed || Input.GetKey(a) || Input.GetKey(b); }

        internal void ToggleKill()
        {
            if (Host.Kill.Engaged)
            {
                Host.ReleaseKill("human (hotkey)");
                ScreenMessages.PostScreenMessage("Skizzy kill switch released", 4f, ScreenMessageStyle.UPPER_CENTER);
            }
            else
            {
                Host.EngageKill("human (hotkey)", "hotkey " + Host.Settings.KillSwitchKey);
                ScreenMessages.PostScreenMessage("Skizzy KILL SWITCH ENGAGED: clients are read-only (" + Host.Settings.KillSwitchKey + " to release)", 6f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        // ---------------------------------------------------------------- self-check lines (host.selfcheck, main thread)

        private void SelfCheck(CallContext c, List<string[]> lines)
        {
            lines.Add(new[] { "pass", "plugin footprint", "OnFlyByWire callbacks 0, Harmony patches 0, engine ticks 0, PartModule/VesselModule/ScenarioModule 0 (M0 registers none)" });
            int expected = 2 + (Events.DynamicOn ? 16 : 0);
            var parts = new List<string>();
            foreach (var kv in Events.Counts) parts.Add(kv.Key + (kv.Value != 1 ? " x" + kv.Value : ""));
            parts.Sort(StringComparer.Ordinal);
            lines.Add(new[] { Events.Total == expected ? "pass" : "warn", "GameEvents handlers",
                Events.Total + " (" + (Events.DynamicOn ? "scene + vessel/time while awake" : "scene only") + "): " + string.Join(", ", parts.ToArray()) });
            lines.Add(new[] { "pass", "pump", (PumpAwake ? "awake" : "asleep") + ", " + Waker.Mode + " (" + Waker.ContextType + "), updates " + SkizzyPump.Updates +
                ", fixed updates " + SkizzyPump.FixedUpdates + ", wakes " + Host.Dispatcher.Wakes });
            var missing = VesselCodes.Missing();
            lines.Add(new[] { missing.Count == 0 && KspPart.TableMatches() ? "pass" : "fail", "field tables",
                missing.Count == 0 ? "all vessel and part fields have KSP readers" : "missing: " + string.Join(", ", missing.ToArray()) });
            lines.Add(new[] { FieldGetters.LastError == null ? "pass" : "warn", "compiled accessors",
                ModuleReflector.Compiled + " compiled, " + ModuleReflector.Fallbacks + " boxed fallbacks" + (FieldGetters.LastError != null ? "; last error " + FieldGetters.LastError : "") });
            lines.Add(new[] { Camera.Failures == 0 ? "pass" : "warn", "screenshots",
                (SystemInfo.supportsAsyncGPUReadback ? "async readback" : "ReadPixels fallback") + ", flip " + (Camera.Flip ? "on" : "off") +
                ", encode on " + (Host.Settings.JpegOnWorkerThread ? "worker thread" : "main thread") + ", " + Camera.Captures + " ok / " + Camera.Failures + " failed" +
                (Camera.LastError != null ? " (last: " + Camera.LastError + ")" : "") + ", pooled RT " + Camera.PooledRenderTextures + " / Texture2D " + Camera.PooledTextures +
                ", live RT " + Camera.LiveRenderTextures + " / Texture2D " + Camera.LiveTextures });
            lines.Add(new[] { "pass", "heap", "mono used " + (Profiler.GetMonoUsedSizeLong() / (1024 * 1024)) + " MB, GC total " + (GC.GetTotalMemory(false) / (1024 * 1024)) +
                " MB, gen0 collections " + GC.CollectionCount(0) + (_probe != null ? "; idle probe writing " + Host.Paths.Relative(Host.Paths.IdleProbeFile) : "; idle probe off (idleProbe = true in settings.cfg)") });
            var spikes = KspSpikes.Last;
            lines.Add(new[] { spikes == null ? "warn" : spikes.GetBool("pass") ? "pass" : "warn", "M0 spikes",
                spikes == null ? "not run yet: skizzy call host.debug.spike" : "last run " + spikes.GetString("ranUtc") + ": " + SpikeSummary(spikes) });
            lines.Add(new[] { "pass", "kill-switch hotkey", Host.Settings.KillSwitchKey + " (works while a client is connected)" });
        }

        private static string SpikeSummary(Skizzy.Protocol.Json.JsonValue r)
        {
            var s = r.GetObj("spikes");
            if (s == null) return "?";
            var parts = new List<string>();
            foreach (var name in new[] { "nostdlib", "sync_context", "jpg_thread", "async_readback" })
            {
                var x = s.GetObj(name);
                if (x != null) parts.Add(name + " " + (x.GetBool("pass") ? "pass" : "FAIL"));
            }
            return string.Join(", ", parts.ToArray());
        }

        // ---------------------------------------------------------------- shutdown

        private void OnApplicationQuit() { Shutdown(); }

        private void OnDestroy()
        {
            if (Instance == this) Shutdown();
        }

        private void Shutdown()
        {
            if (_stopped || Host == null) return;
            _stopped = true;
            try
            {
                Events.RemoveDynamic();
                Events.RemoveScene();
                if (_probe != null) _probe.Stop();
                Host.Stop();
                Camera.Trim(true);
                Log.Info("Skizzy host stopped");
                Log.FlushSync();
            }
            catch (Exception e) { Debug.LogError("[Skizzy] shutdown failed: " + e); }
            if (Instance == this) Instance = null;
        }
    }
}
