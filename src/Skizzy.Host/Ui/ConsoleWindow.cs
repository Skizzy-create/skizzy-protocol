using System;
using System.Collections.Generic;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// Capability console (21 §3, M0): any capability in the catalog is callable from the game the day it exists, through
    /// the same pipeline as every client (InvokeLocal: schema validation, scene check, permissions, audit).
    /// </summary>
    internal sealed class ConsoleWindow : UiWindow
    {
        private sealed class Recent
        {
            internal string Capability, Args, When, Status;
            internal double Ms;
        }

        private const int MaxShown = 12000;
        private string _search = "", _filteredFor;
        private readonly List<CapabilityDef> _filtered = new List<CapabilityDef>();
        private CapabilityDef _sel;
        private SchemaForm _form;
        private bool _raw;
        private string _rawText = "";
        private string _formError;
        private Vector2 _listScroll, _formScroll, _resultScroll;
        private string _resultFull, _resultShown, _error, _resultTitle;
        private bool _calling;
        private readonly List<Recent> _recent = new List<Recent>();
        private string _policyText;
        private GUIStyle _policyStyle;

        internal ConsoleWindow(SkizzyUi ui, int index) : base(ui, index, "Capability console", 80, 90, 820, 400) { }

        internal override void OnLayout()
        {
            if (_filteredFor == _search) return;
            _filteredFor = _search;
            _filtered.Clear();
            string q = (_search ?? "").Trim();
            var all = Ui.Host.Catalog.All;
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                if (q.Length == 0 || d.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (d.Description != null && d.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                    _filtered.Add(d);
            }
        }

        private void Select(CapabilityDef d, JsonValue args)
        {
            _sel = d;
            _form = SchemaForm.From(d.InputSchema);
            _formError = null;
            if (args != null)
            {
                _formError = _form.Load(args);
                _rawText = args.ToPrettyString();
            }
            else
            {
                var built = _form.Build(out _);
                _rawText = built != null ? built.ToPrettyString() : "{}";
            }
            try
            {
                var dec = Ui.Host.Permissions.Evaluate(d.Name, d.Mod, d.Safety, "human", "in-game console", null);
                _policyText = "In-game policy: " + SafetyNames.PolicyName(dec.Policy) + " (" + dec.Source + ")" +
                              (dec.Policy == Policy.Ask ? "; pressing Call counts as your confirmation" : "");
                _policyStyle = dec.Policy == Policy.Deny ? UiKit.Red : dec.Policy == Policy.Ask ? UiKit.Yellow : UiKit.Grey;
            }
            catch (Exception e) { _policyText = "policy: " + e.Message; _policyStyle = UiKit.Grey; }
        }

        protected override void Body()
        {
            GUILayout.BeginHorizontal();
            // ---- catalog list
            GUILayout.BeginVertical(GUILayout.Width(250));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(50));
            _search = UiKit.Text("console.search", _search);
            GUILayout.EndHorizontal();
            GUILayout.Label(_filtered.Count + " of " + Ui.Host.Catalog.All.Count + " capabilities", UiKit.Grey);
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(470));
            for (int i = 0; i < _filtered.Count; i++)
            {
                var d = _filtered[i];
                UiKit.Choice(_sel == d, d.Name, () => Select(d, null));
            }
            GUILayout.EndScrollView();
            RecentList();
            GUILayout.EndVertical();

            // ---- selected capability
            GUILayout.BeginVertical();
            if (_sel == null) GUILayout.Label("Pick a capability on the left. Calls go through the same permission checks and audit as any client.", UiKit.Wrap);
            else SelectedPanel();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void SelectedPanel()
        {
            var d = _sel;
            GUILayout.BeginHorizontal();
            GUILayout.Label(d.Name, UiKit.Header);
            GUILayout.FlexibleSpace();
            string safety = SafetyNames.Name(d.Safety);
            GUILayout.Label(safety, PromptView.SafetyStyle(safety));
            GUILayout.EndHorizontal();
            string scene = Ui.Host.Meta.Scene;
            bool inScene = d.InScene(scene);
            GUILayout.Label("mod " + d.Mod + ", area " + (d.Area ?? "-") + ", scenes " + string.Join(", ", d.Scenes ?? new string[0]) +
                            (d.LongRunning ? ", long-running (returns an op)" : "") + (inScene ? "" : "  -- not available in " + scene), inScene ? UiKit.Small : UiKit.Yellow);
            if (_policyText != null) GUILayout.Label(_policyText, _policyStyle ?? UiKit.Grey);
            if (!Ui.Host.HasHandler(d.Name)) GUILayout.Label("No handler in this build (defined for later milestones).", UiKit.Yellow);

            _formScroll = GUILayout.BeginScrollView(_formScroll, GUILayout.Height(250));
            GUILayout.Label(d.Description ?? "", UiKit.Wrap);
            GUILayout.Space(4);
            if (_raw) _rawText = UiKit.Area("console.raw", _rawText, GUILayout.MinHeight(120), GUILayout.ExpandWidth(true));
            else _form.Draw(Ui, "console.f");
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            UiKit.Choice(!_raw, "Form", ToForm, GUILayout.Width(70));
            UiKit.Choice(_raw, "Raw JSON", ToRaw, GUILayout.Width(90));
            GUILayout.FlexibleSpace();
            if (_calling) GUILayout.Label("calling...", UiKit.Yellow, GUILayout.Width(80));
            UiKit.Btn("Call", DoCall, GUILayout.Width(90));
            GUILayout.EndHorizontal();
            if (_formError != null) GUILayout.Label(_formError, UiKit.Yellow);

            // ---- result
            if (_error != null || _resultShown != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(_resultTitle ?? "Result", UiKit.Bold);
                GUILayout.FlexibleSpace();
                if (_resultFull != null) UiKit.Btn("Copy", () => UiKit.Copy(_resultFull), GUILayout.Width(60));
                UiKit.Btn("Clear", () => { _resultFull = _resultShown = _error = null; }, GUILayout.Width(60));
                GUILayout.EndHorizontal();
                _resultScroll = GUILayout.BeginScrollView(_resultScroll, GUILayout.Height(170));
                if (_error != null) GUILayout.Label(_error, UiKit.Red);
                if (_resultShown != null) GUILayout.Label(_resultShown, UiKit.Wrap);
                GUILayout.EndScrollView();
            }
        }

        private void ToRaw()
        {
            if (_raw) return;
            var built = _form.Build(out string err);
            if (built != null) _rawText = built.ToPrettyString();
            _formError = err != null ? "Form incomplete (" + err + "); edit the JSON directly." : null;
            _raw = true;
        }

        private void ToForm()
        {
            if (!_raw) return;
            try
            {
                var v = JsonValue.Parse(string.IsNullOrEmpty(_rawText.Trim()) ? "{}" : _rawText);
                _formError = _form.Load(v);
                _raw = false;
            }
            catch (Exception e) { _formError = "Raw JSON does not parse (" + e.Message + "); fix it or keep using raw mode."; }
        }

        private void DoCall()
        {
            if (_sel == null || _calling) return;
            JsonValue args;
            if (_raw)
            {
                try { args = JsonValue.Parse(string.IsNullOrEmpty(_rawText.Trim()) ? "{}" : _rawText); }
                catch (Exception e) { _formError = "Raw JSON does not parse: " + e.Message; return; }
                if (!args.IsObject) { _formError = "Arguments must be a JSON object."; return; }
            }
            else
            {
                args = _form.Build(out string err);
                if (args == null) { _formError = err; return; }
            }
            _formError = null;
            var def = _sel;
            var rec = new Recent { Capability = def.Name, Args = args.ToString(), When = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), Status = "running" };
            _recent.Insert(0, rec);
            if (_recent.Count > 10) _recent.RemoveAt(_recent.Count - 1);
            _calling = true;
            float t0 = Time.realtimeSinceStartup;
            Ui.Call(def.Name, args, (r, e) =>
            {
                _calling = false;
                rec.Ms = (Time.realtimeSinceStartup - t0) * 1000.0;
                rec.Status = e == null ? "ok" : ErrorKinds.Name(e.Kind);
                _resultScroll = Vector2.zero;
                _resultTitle = def.Name + ": " + (e == null ? "ok" : "error") + " (" + UiKit.N(Math.Round(rec.Ms, 1)) + " ms)";
                if (e != null)
                {
                    _error = UiKit.ErrorText(e);
                    _resultFull = e.Extra != null ? e.Extra.ToPrettyString() : null;
                    _resultShown = _resultFull != null ? "Details:\n" + UiKit.Clip(_resultFull, MaxShown) : null;
                    return;
                }
                _error = null;
                _resultFull = r != null ? r.ToPrettyString() : "null";
                _resultShown = _resultFull.Length > MaxShown
                    ? _resultFull.Substring(0, MaxShown) + "\n... (" + _resultFull.Length + " chars; Copy for the full result)"
                    : _resultFull;
            });
        }

        private void RecentList()
        {
            if (_recent.Count == 0) return;
            GUILayout.Label("Recent calls", UiKit.Bold);
            for (int i = 0; i < _recent.Count && i < 6; i++)
            {
                var rc = _recent[i];
                string label = rc.When + " " + rc.Capability + " " + rc.Status + (rc.Status != "running" ? " " + UiKit.N(Math.Round(rc.Ms)) + "ms" : "");
                UiKit.Btn(label, () =>
                {
                    var d = Ui.Host.Catalog.Find(rc.Capability);
                    if (d == null) return;
                    JsonValue a = null;
                    try { a = JsonValue.Parse(rc.Args); } catch (Exception) { }
                    Select(d, a);
                });
            }
        }
    }
}
