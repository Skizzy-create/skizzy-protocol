using System;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// Camera (21 §3, M0): mode buttons, target picker, look-at framing and screenshot save, all through the
    /// ksp.camera.* capabilities (InvokeLocal), so the in-game buttons do exactly what an agent's calls do.
    /// </summary>
    internal sealed class CameraWindow : UiWindow
    {
        private static readonly string[] Modes = { "follow", "orbit", "chase", "free", "look_at" };
        private string _target, _lookTo;
        private bool _pickLookTo;
        private string _heading = "", _pitch = "", _distance = "";
        private string _lookDistance = "60", _lookElev = "10";
        private string _shotName = "", _shotWidth = "1920";
        private int _shotFormat;           // 0 png, 1 jpeg
        private bool _hideUi;
        private string _status, _state;
        private bool _busy;
        private Vector2 _scroll;

        internal CameraWindow(SkizzyUi ui, int index) : base(ui, index, "Camera", 720, 140, 460, 300) { }

        internal override void OnOpened()
        {
            Ui.Vessels.EnsureLoaded();
            RefreshState();
        }

        private void RefreshState()
        {
            Ui.Call("ksp.camera.state", JsonValue.Obj(), (r, e) =>
            {
                if (e != null) { _state = UiKit.ErrorText(e); return; }
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < r.Count; i++)
                {
                    var v = r.ValueAt(i);
                    if (v == null || v.IsNull || v.IsObject || v.IsArray) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(r.KeyAt(i)).Append(' ').Append(v.IsString ? v.AsString() : v.IsNumber ? UiKit.N(Math.Round(v.AsDouble(), 2)) : v.ToString());
                }
                _state = sb.ToString();
            });
        }

        private void Run(string cap, JsonValue args, string what)
        {
            _busy = true;
            _status = what + "...";
            Ui.Call(cap, args, (r, e) =>
            {
                _busy = false;
                if (e != null) { _status = UiKit.ErrorText(e); return; }
                string path = r != null ? r.GetString("path") : null;
                _status = what + ": ok" + (path != null ? " -> " + path : "");
                RefreshState();
            });
        }

        protected override void Body()
        {
            if (_state != null) GUILayout.Label("Now: " + _state, UiKit.Small);
            GUILayout.Label("Target" + (_target != null ? ": " + Describe(_target) : " (none: modes use the current camera target)"), UiKit.Bold);
            Ui.Vessels.DrawPicker(ref _scroll, _pickLookTo ? _lookTo : _target, row =>
            {
                if (_pickLookTo) { _lookTo = row.Handle; _pickLookTo = false; }
                else _target = row.Handle;
            }, 140);
            GUILayout.BeginHorizontal();
            UiKit.Btn("Point camera at target", () =>
            {
                if (_target == null) { _status = "Pick a target vessel first."; return; }
                Run("ksp.camera.target", JsonValue.Obj().Set("vessel", _target), "target " + Describe(_target));
            });
            UiKit.Btn("Clear target", () => _target = null, GUILayout.Width(100));
            GUILayout.EndHorizontal();

            UiKit.Section("Mode");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Modes.Length; i++)
            {
                string m = Modes[i];
                UiKit.Btn(m == "look_at" ? "look at" : m, () => SetMode(m));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("orbit: heading", GUILayout.Width(90));
            _heading = UiKit.Text("cam.heading", _heading, GUILayout.Width(50));
            GUILayout.Label("pitch", GUILayout.Width(36));
            _pitch = UiKit.Text("cam.pitch", _pitch, GUILayout.Width(50));
            GUILayout.Label("dist m", GUILayout.Width(44));
            _distance = UiKit.Text("cam.distance", _distance, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("look at: " + (_lookTo != null ? Describe(_lookTo) : "(pick)"), GUILayout.Width(220));
            UiKit.Choice(_pickLookTo, _pickLookTo ? "click a vessel above" : "pick far vessel", () => _pickLookTo = !_pickLookTo);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("look-at distance m", GUILayout.Width(120));
            _lookDistance = UiKit.Text("cam.lookdist", _lookDistance, GUILayout.Width(60));
            GUILayout.Label("elevation deg", GUILayout.Width(90));
            _lookElev = UiKit.Text("cam.lookelev", _lookElev, GUILayout.Width(50));
            GUILayout.EndHorizontal();

            UiKit.Section("Screenshot");
            GUILayout.BeginHorizontal();
            GUILayout.Label("name", GUILayout.Width(40));
            _shotName = UiKit.Text("cam.shotname", _shotName, GUILayout.Width(150));
            GUILayout.Label("width", GUILayout.Width(40));
            _shotWidth = UiKit.Text("cam.shotwidth", _shotWidth, GUILayout.Width(50));
            UiKit.Choice(_shotFormat == 0, "png", () => _shotFormat = 0, GUILayout.Width(45));
            UiKit.Choice(_shotFormat == 1, "jpeg", () => _shotFormat = 1, GUILayout.Width(50));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            UiKit.Tog(_hideUi, "hide KSP UI", v => _hideUi = v, GUILayout.Width(110));
            GUILayout.FlexibleSpace();
            UiKit.Btn("Save screenshot", SaveShot, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            GUILayout.Label("Saved under Skizzy/screenshots/<date>/ in the KSP folder.", UiKit.Small);
            if (_busy) GUILayout.Label("working...", UiKit.Yellow);
            if (_status != null) GUILayout.Label(_status, UiKit.Wrap);
        }

        private string Describe(string handle)
        {
            var row = Ui.Vessels.Find(handle);
            return row != null ? row.Name : handle;
        }

        private void SetMode(string m)
        {
            if (m == "look_at")
            {
                if (_target == null || _lookTo == null) { _status = "look at needs a target (near vessel) and a far vessel."; return; }
                var la = JsonValue.Obj().Set("from", _target).Set("to", _lookTo);
                if (UiKit.TryNum(_lookDistance, out double ld)) la.Set("distance", ld);
                if (UiKit.TryNum(_lookElev, out double le)) la.Set("elevationDeg", le);
                Run("ksp.camera.look_at", la, "look at " + Describe(_lookTo) + " from " + Describe(_target));
                return;
            }
            var a = JsonValue.Obj().Set("mode", m);
            if (_target != null) a.Set("target", _target);
            if (m == "orbit")
            {
                var o = JsonValue.Obj();
                if (UiKit.TryNum(_heading, out double h)) o.Set("heading", h);
                if (UiKit.TryNum(_pitch, out double p)) o.Set("pitch", p);
                if (UiKit.TryNum(_distance, out double d)) o.Set("distance", d);
                if (o.Count > 0) a.Set("orbit", o);
            }
            Run("ksp.camera.set_mode", a, "mode " + m);
        }

        private void SaveShot()
        {
            var a = JsonValue.Obj();
            string n = (_shotName ?? "").Trim();
            if (n.Length > 0) a.Set("name", n);
            if (int.TryParse((_shotWidth ?? "").Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int w)) a.Set("width", w);
            a.Set("format", _shotFormat == 0 ? "png" : "jpeg");
            if (_hideUi) a.Set("hideUi", true);
            Run("ksp.camera.save", a, "screenshot");
        }
    }
}
