using System;
using System.Collections.Generic;
using Skizzy.Host.Core;
using Skizzy.Protocol;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// Health (21 §3, M0): host frame-time cost, dispatcher budget, degraded flags, auto-disabled capabilities (with
    /// re-enable) and the memory self-check (host.selfcheck on demand). Text is rebuilt twice a second while open.
    /// </summary>
    internal sealed class HealthWindow : UiWindow
    {
        private readonly List<string[]> _lines = new List<string[]>();   // {style, text}
        private readonly List<string> _disabled = new List<string>();
        private float _next;
        private List<string[]> _check;
        private string _checkError;
        private bool _checking, _textures;
        private Vector2 _scroll, _checkScroll;

        internal HealthWindow(SkizzyUi ui, int index) : base(ui, index, "Health", 760, 90, 560, 300) { }

        internal override void OnOpened() { _next = 0; }

        internal override void OnLayout()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 0.5f;
            _lines.Clear();
            var h = Ui.Host;
            var st = h.Stats;
            var disp = h.Dispatcher;
            Add("", "host " + ProtocolInfo.HostVersion + ", protocol " + ProtocolInfo.Version);
            Add(h.Port > 0 ? "ok" : "fail", h.Port > 0 ? "listening on 127.0.0.1:" + h.Port : "not listening" + (h.StartError != null ? ": " + h.StartError : h.Settings.Listen ? "" : " (listen = false in settings.cfg)"));
            Add(h.WakerMode == "sync_context" ? "ok" : "warn", "idle mode " + h.WakerMode + (h.WakerMode == "sync_context" ? " (pump disabled when idle)" : " (polling fallback: one flag read per idle frame)"));
            Add("", "pump " + (Ui.Boot.PumpAwake ? "awake" : "asleep") + ", dispatcher " + (disp.Awake ? "awake" : "asleep") + ", wakes " + UiKit.I(disp.Wakes) +
                    ", queue main " + disp.MainCount + " / fixed " + disp.FixedCount + ", executed " + UiKit.I(disp.Executed));
            Add("", "budget " + UiKit.N2(disp.BudgetMs) + " ms per frame (" + UiKit.N2(disp.MinBudgetMs) + " .. " + UiKit.N2(disp.MaxBudgetMs) + ")");
            Add("", "sessions " + h.AttachedSessionCount + ", logs " + h.Logs.ActiveCount + ", streams " + h.Streams.Count + ", ops " + h.Ops.RunningCount +
                    ", prompts " + h.Prompts.Count + ", calls " + UiKit.I(st.Calls) + ", errors " + UiKit.I(st.Errors));
            Add("head", "Main-thread cost (p50 / p99 / max ms, last 512 samples; only measured while the pump runs)");
            Timing("host Update", st.UpdateMs, 0.1);
            Timing("host FixedUpdate", st.FixedMs, 0.2);
            Timing("dispatcher drain", disp.DrainMs, 2.0);
            Timing("queue wait", disp.QueueWaitMs, -1);
            Timing("log sampler", st.LogSampleMs, 0.2);
            Timing("stream sampler", st.StreamSampleMs, 0.2);
            Timing("screenshot (main)", st.ShotMainMs, 3.0);
            Timing("call total", st.CallMs, -1);
            Timing("frame time", st.FrameMs, -1);
            Add("head", "Degraded flags");
            int flags = 0;
            if (h.Kill.Engaged) { Add("fail", "kill switch engaged by " + h.Kill.By); flags++; }
            if (!h.Settings.JpegOnWorkerThread) { Add("warn", "JPEG encoding on the main thread (worker-thread spike failed or disabled)"); flags++; }
            if (h.WakerMode != "sync_context") { Add("warn", "no UnitySynchronizationContext: polling idle mode"); flags++; }
            if (h.StartError != null) { Add("fail", "socket: " + h.StartError); flags++; }
            if (h.SceneTransition) { Add("warn", "scene transition in progress"); flags++; }
            if (flags == 0) Add("ok", "none");
            Add("head", "Memory");
            Add("", "GC heap " + UiKit.Bytes(GC.GetTotalMemory(false)) + ", gen0 collections " + GC.CollectionCount(0) + ", gen2 " + GC.CollectionCount(2) +
                    ", audit written " + UiKit.I(h.Audit.Written) + " / dropped " + UiKit.I(h.Audit.Dropped));
            _disabled.Clear();
            _disabled.AddRange(h.Faults.Disabled());
        }

        private void Add(string style, string text) { _lines.Add(new[] { style, text }); }

        private void Timing(string name, TimingWindow t, double budget)
        {
            if (t.Count == 0) { Add("", name + ": no samples"); return; }
            t.Percentiles(out double p50, out double p99, out double max);
            string style = budget > 0 && p99 > budget ? "warn" : "";
            Add(style, name + ": " + UiKit.N(Math.Round(p50, 3)) + " / " + UiKit.N(Math.Round(p99, 3)) + " / " + UiKit.N(Math.Round(max, 3)) +
                       (budget > 0 ? "  (budget " + UiKit.N(budget) + ")" : "") + "  n=" + UiKit.I(t.Count));
        }

        protected override void Body()
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(330));
            for (int i = 0; i < _lines.Count; i++)
            {
                var l = _lines[i];
                if (l[0] == "head") GUILayout.Label(l[1], UiKit.Bold);
                else GUILayout.Label(l[1], l[0] == "" ? UiKit.Wrap : UiKit.StatusStyle(l[0]));
            }
            GUILayout.EndScrollView();

            UiKit.Section("Auto-disabled capabilities (5 faults in 60 s)");
            if (_disabled.Count == 0) GUILayout.Label("none", UiKit.Green);
            for (int i = 0; i < _disabled.Count; i++)
            {
                string cap = _disabled[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(cap, UiKit.Red);
                GUILayout.FlexibleSpace();
                UiKit.Btn("Re-enable", () => { Ui.Host.Faults.Enable(cap); _next = 0; }, GUILayout.Width(90));
                GUILayout.EndHorizontal();
            }

            UiKit.Section("Self-check");
            GUILayout.BeginHorizontal();
            UiKit.Btn(_checking ? "Running..." : "Run host.selfcheck", RunCheck, GUILayout.Width(170));
            UiKit.Tog(_textures, "count textures (slower)", v => _textures = v);
            GUILayout.EndHorizontal();
            if (_checkError != null) GUILayout.Label(_checkError, UiKit.Red);
            if (_check != null)
            {
                _checkScroll = GUILayout.BeginScrollView(_checkScroll, GUILayout.Height(200));
                UiKit.DrawChecks(_check);
                GUILayout.EndScrollView();
            }
        }

        private void RunCheck()
        {
            if (_checking) return;
            _checking = true;
            Ui.Call("host.selfcheck", JsonValue.Obj().Set("textures", _textures), (r, e) =>
            {
                _checking = false;
                if (e != null) { _checkError = UiKit.ErrorText(e); _check = null; return; }
                _checkError = null;
                _check = UiKit.Checks(r);
            });
        }
    }
}
