using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// Recorder / logs (21 §3, M0 part): running and recent logs (rec.log.list) with stop and tail. The list is fetched
    /// on open and on demand only (each call is audited, so there is no polling).
    /// </summary>
    internal sealed class RecorderWindow : UiWindow
    {
        private sealed class LogRow
        {
            internal string Id, Name, State, Path, Format, Detail, StopReason;
        }

        private readonly List<LogRow> _rows = new List<LogRow>();
        private bool _loading;
        private string _error, _status, _tail, _tailFor;
        private Vector2 _scroll, _tailScroll;

        internal RecorderWindow(SkizzyUi ui, int index) : base(ui, index, "Recorder / logs", 100, 140, 600, 200) { }

        internal override void OnOpened() { Refresh(); }

        internal void Refresh()
        {
            if (_loading) return;
            _loading = true;
            Ui.Call("rec.log.list", JsonValue.Obj(), (r, e) =>
            {
                _loading = false;
                _rows.Clear();
                if (e != null) { _error = UiKit.ErrorText(e); return; }
                _error = null;
                var arr = r != null ? r.GetArr("logs") : null;
                if (arr == null) return;
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    var l = arr[i];
                    _rows.Add(new LogRow
                    {
                        Id = l.GetString("log", "?"),
                        Name = l.GetString("name", "?"),
                        State = l.GetString("state", "?"),
                        Path = l.GetString("path", ""),
                        Format = l.GetString("format", "?"),
                        StopReason = l.GetString("stopReason"),
                        Detail = UiKit.I(l.GetLong("rows")) + " rows, " + UiKit.Bytes(l.GetLong("bytes")) + ", " + UiKit.N(l.GetDouble("rateHz")) + " Hz" +
                                 (l.GetBool("onChange") ? " on change" : "") + ", " + UiKit.I(l.GetLong("targets")) + " targets",
                    });
                }
            });
        }

        protected override void Body()
        {
            GUILayout.BeginHorizontal();
            UiKit.Btn("Log...", () => Ui.Open(Ui.Picker), GUILayout.Width(80));
            UiKit.Btn(_loading ? "Refreshing..." : "Refresh", Refresh, GUILayout.Width(110));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Files: Skizzy/logs/<date>/ under the KSP folder", UiKit.Grey);
            GUILayout.EndHorizontal();
            if (_error != null) GUILayout.Label(_error, UiKit.Red);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Math.Min(320, 50 + _rows.Count * 74)));
            if (_rows.Count == 0 && _error == null) GUILayout.Label(_loading ? "Loading..." : "No logs yet. Use Log... to record fields to CSV or JSONL.", UiKit.Grey);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Id + "  " + row.Name + "  (" + row.Format + ")", UiKit.Bold);
                GUILayout.FlexibleSpace();
                GUILayout.Label(row.State, row.State == "running" ? UiKit.Green : row.State == "failed" ? UiKit.Red : UiKit.Grey);
                GUILayout.EndHorizontal();
                GUILayout.Label(row.Detail + (row.StopReason != null ? ", stopped: " + row.StopReason : ""), UiKit.Small);
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Path, UiKit.Small);
                GUILayout.FlexibleSpace();
                string id = row.Id, path = row.Path;
                UiKit.Btn("Copy path", () => UiKit.Copy(path), GUILayout.Width(80));
                UiKit.Btn("Tail", () => Tail(id), GUILayout.Width(50));
                if (row.State == "running") UiKit.Btn("Stop", () => Stop(id), GUILayout.Width(50));
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
            if (_status != null) GUILayout.Label(_status, UiKit.Wrap);
            if (_tail != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Last rows of " + _tailFor, UiKit.Bold);
                GUILayout.FlexibleSpace();
                UiKit.Btn("Close", () => _tail = null, GUILayout.Width(60));
                GUILayout.EndHorizontal();
                _tailScroll = GUILayout.BeginScrollView(_tailScroll, GUILayout.Height(140));
                GUILayout.Label(_tail, UiKit.Small);
                GUILayout.EndScrollView();
            }
        }

        private void Stop(string id)
        {
            _status = "Stopping " + id + "...";
            Ui.Call("rec.log.stop", JsonValue.Obj().Set("log", id), (r, e) =>
            {
                _status = e != null ? UiKit.ErrorText(e) : r.GetString("log") + " " + r.GetString("state") + ": " + r.GetString("path") + " (" + UiKit.I(r.GetLong("rows")) + " rows)";
                Refresh();
            });
        }

        private void Tail(string id)
        {
            Ui.Call("rec.log.tail", JsonValue.Obj().Set("log", id).Set("n", 10).Set("format", "compact"), (r, e) =>
            {
                _tailFor = id;
                _tail = e != null ? UiKit.ErrorText(e) : UiKit.Clip(r.GetString("text") ?? "", 6000);
            });
        }
    }

    /// <summary>
    /// The Log... picker (21 §3, 17 M0 acceptance 4): vessel -> part -> module -> field, rate and format, built into the
    /// same rec.log.start arguments as <c>skizzy log start</c> builds from its flags (LogCommand.Start: name, fields,
    /// rateHz, format, select, onChange), with the equivalent CLI line shown for copy.
    /// </summary>
    internal sealed class LogPickerWindow : UiWindow
    {
        private sealed class PartRow
        {
            internal string Handle, Name, Title;
            internal readonly List<string> ModuleClasses = new List<string>();
            internal readonly List<string> ModuleHandles = new List<string>();
        }

        private sealed class FieldRow
        {
            internal string Name, Label;
        }

        private static readonly string[] VesselModes = { "Active vessel", "Pick a vessel", "Name glob", "All loaded" };
        private static readonly string[] Formats = { "csv", "jsonl" };

        private int _vesselMode;                 // 0 active, 1 handle, 2 glob, 3 all loaded
        private string _vesselHandle, _glob = "";
        private readonly List<PartRow> _parts = new List<PartRow>();
        private string _partsFor, _partsError, _partsNote;
        private bool _partsLoading;
        private int _partIdx = -1;               // -1 = any part
        private string _module;                  // null = vessel fields
        private string _moduleTyped = "";
        private readonly List<string> _moduleChoices = new List<string>();
        private readonly List<FieldRow> _moduleFields = new List<FieldRow>();
        private string _fieldsFor, _fieldsError;
        private bool _fieldsLoading;
        private readonly List<FieldRow> _vesselFields = new List<FieldRow>();
        private readonly List<string> _selected = new List<string>();
        private string _extra = "", _rate = "5", _name = "", _deadband = "";
        private int _format;
        private bool _onChange;
        private Vector2 _vScroll, _pScroll, _mScroll, _fScroll;
        private string _cli, _argsText, _buildError, _status;
        private JsonValue _args;
        private bool _starting;
        private int _vesselsVersion = -1;

        internal LogPickerWindow(SkizzyUi ui, int index) : base(ui, index, "Log... picker", 140, 80, 640, 300)
        {
            for (int i = 0; i < VesselFields.All.Length; i++)
            {
                var f = VesselFields.All[i];
                _vesselFields.Add(new FieldRow { Name = f.Name, Label = f.Name + (f.Unit != null ? " [" + f.Unit + "]" : "") });
            }
        }

        internal override void OnOpened()
        {
            if (_name.Length == 0) _name = "log-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
            if (_selected.Count == 0) { _selected.Add("altitude"); _selected.Add("srfSpeed"); }
        }

        internal override void OnLayout()
        {
            if (_vesselsVersion != Ui.Vessels.Version)
            {
                _vesselsVersion = Ui.Vessels.Version;
                if (_vesselMode == 1 && _vesselHandle != null && Ui.Vessels.Loaded && Ui.Vessels.Find(_vesselHandle) == null) { _vesselHandle = null; ClearParts(); }
            }
            RebuildModuleChoices();
            BuildArgs();
        }

        private string VesselRef => _vesselMode == 0 ? "active" : _vesselMode == 1 ? _vesselHandle : null;

        private void ClearParts()
        {
            _parts.Clear();
            _partsFor = null;
            _partsError = null;
            _partsNote = null;
            _partIdx = -1;
            SetModule(null);
        }

        private void SetModule(string m)
        {
            if (m == _module) return;
            _module = m;
            _moduleFields.Clear();
            _fieldsFor = null;
            _fieldsError = null;
            _selected.Clear();
            if (m == null) { _selected.Add("altitude"); _selected.Add("srfSpeed"); }
            else LoadModuleFields();
        }

        private void LoadParts()
        {
            string v = VesselRef;
            if (v == null) return;
            _partsLoading = true;
            _partsError = null;
            Ui.Call("ksp.vessel.get", JsonValue.Obj().Set("vessel", v).Set("depth", 2).Set("maxParts", 1000), (r, e) =>
            {
                _partsLoading = false;
                _parts.Clear();
                _partIdx = -1;
                _partsFor = v;
                if (e != null) { _partsError = UiKit.ErrorText(e); return; }
                _partsNote = r.GetString("note");
                var arr = r.GetArr("parts");
                if (arr == null) return;
                for (int i = 0; i < arr.Count; i++)
                {
                    var p = arr[i];
                    var row = new PartRow { Handle = p.GetString("handle"), Name = p.GetString("name", "?"), Title = p.GetString("title", "?") };
                    var mods = p.GetArr("modules");
                    if (mods != null)
                        for (int k = 0; k < mods.Count; k++)
                        {
                            var m = mods[k];
                            row.ModuleClasses.Add(m.IsString ? m.AsString() : m.GetString("class", "?"));
                            row.ModuleHandles.Add(m.IsObject ? m.GetString("handle") : null);
                        }
                    _parts.Add(row);
                }
                if (r.GetBool("partsTruncated")) _partsNote = "Only the first " + _parts.Count + " parts are listed.";
            });
        }

        private void RebuildModuleChoices()
        {
            _moduleChoices.Clear();
            if (_partIdx >= 0 && _partIdx < _parts.Count)
            {
                foreach (var c in _parts[_partIdx].ModuleClasses) if (!_moduleChoices.Contains(c)) _moduleChoices.Add(c);
            }
            else
            {
                for (int i = 0; i < _parts.Count; i++)
                    foreach (var c in _parts[i].ModuleClasses) if (!_moduleChoices.Contains(c)) _moduleChoices.Add(c);
            }
            _moduleChoices.Sort(StringComparer.Ordinal);
        }

        private string FindModuleHandle(string cls)
        {
            int from = _partIdx >= 0 ? _partIdx : 0, to = _partIdx >= 0 ? _partIdx + 1 : _parts.Count;
            for (int i = from; i < to && i < _parts.Count; i++)
            {
                var p = _parts[i];
                for (int k = 0; k < p.ModuleClasses.Count; k++)
                    if (p.ModuleClasses[k] == cls && p.ModuleHandles[k] != null) return p.ModuleHandles[k];
            }
            return null;
        }

        private void LoadModuleFields()
        {
            string cls = _module;
            if (cls == null) return;
            string handle = FindModuleHandle(cls);
            var args = handle != null ? JsonValue.Obj().Set("module", handle) : JsonValue.Obj().Set("type", cls);
            _fieldsLoading = true;
            _fieldsError = null;
            Ui.Call("ksp.module.describe", args, (r, e) =>
            {
                if (_module != cls) return; // the user moved on
                _fieldsLoading = false;
                _fieldsFor = cls;
                _moduleFields.Clear();
                if (e != null) { _fieldsError = UiKit.ErrorText(e) + "\nYou can still type field names below."; return; }
                var arr = r.GetArr("fields");
                if (arr == null) return;
                for (int i = 0; i < arr.Count; i++)
                {
                    var f = arr[i];
                    string n = f.GetString("name");
                    if (n == null) continue;
                    string gui = f.GetString("guiName"), units = f.GetString("units");
                    _moduleFields.Add(new FieldRow
                    {
                        Name = n,
                        Label = n + (string.IsNullOrEmpty(gui) || gui == n ? "" : "  \"" + gui + "\"") + " (" + f.GetString("type", "?") + (string.IsNullOrEmpty(units) ? "" : ", " + units) + ")",
                    });
                }
            });
        }

        // ------------------------------------------------------------------ the selector (same shape as the CLI's)

        private void BuildArgs()
        {
            _buildError = null;
            _args = null;
            var fields = new List<string>(_selected);
            foreach (var p in (_extra ?? "").Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0 && !fields.Contains(t)) fields.Add(t);
            }
            string name = (_name ?? "").Trim();
            if (name.Length == 0) _buildError = "Give the log a name.";
            else if (!ValidName(name)) _buildError = "Name: letters, digits, '_', '.', '-' only (max 64).";
            if (fields.Count == 0) _buildError = _buildError ?? "Pick at least one field.";
            if (!UiKit.TryNum(_rate, out double rate) || rate <= 0) { _buildError = _buildError ?? "Rate must be a number greater than 0."; rate = 5; }
            else if (rate < 0.1 || rate > 50) _buildError = _buildError ?? "Rate must be between 0.1 and 50 Hz.";
            double deadband = 0;
            bool hasDeadband = _onChange && (_deadband ?? "").Trim().Length > 0;
            if (hasDeadband && (!UiKit.TryNum(_deadband, out deadband) || deadband < 0)) _buildError = _buildError ?? "Deadband must be a number >= 0.";

            // Selector: the object `skizzy log start --vessels <...>` produces for the same choice (Selectors.Parse).
            JsonValue vessels = null;
            if (_vesselMode == 0) vessels = JsonValue.Obj().Set("active", true);
            else if (_vesselMode == 1)
            {
                if (_vesselHandle == null) _buildError = _buildError ?? "Pick a vessel.";
                else vessels = JsonValue.Obj().Set("handles", JsonValue.ArrOf(_vesselHandle));
            }
            else if (_vesselMode == 2)
            {
                string g = (_glob ?? "").Trim();
                if (g.Length == 0) _buildError = _buildError ?? "Type a name glob, e.g. DDG*.";
                else vessels = JsonValue.Obj().Set("nameLike", g);
            }
            string module = EffectiveModule;
            string partName = module != null && _partIdx >= 0 && _partIdx < _parts.Count ? _parts[_partIdx].Name : null;
            JsonValue select = null;
            if (vessels != null || module != null)
            {
                select = JsonValue.Obj();
                if (vessels != null) select.Set("vessels", vessels);
                if (partName != null) select.Set("parts", JsonValue.Obj().Set("nameLike", partName));
                if (module != null) select.Set("modules", JsonValue.Obj().Set("type", module));
            }

            // Same keys, order and types as LogCommand.Start.
            var p2 = JsonValue.Obj().Set("name", name).Set("fields", JsonValue.ArrOf(fields.ToArray())).Set("rateHz", rate).Set("format", Formats[_format]);
            if (select != null) p2.Set("select", select);
            if (_onChange)
            {
                var oc = JsonValue.Obj();
                if (hasDeadband) oc.Set("deadband", deadband);
                p2.Set("onChange", oc);
            }
            _argsText = p2.ToString();
            if (_buildError == null) _args = p2;

            var sb = new StringBuilder("skizzy log start ");
            sb.Append(UiKit.ShQuote(name.Length > 0 ? name : "<name>"));
            sb.Append(" --fields ").Append(UiKit.ShQuote(string.Join(",", fields.ToArray())));
            sb.Append(" --rate ").Append(rate.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(" --format ").Append(Formats[_format]);
            if (select != null) sb.Append(" --vessels ").Append(VesselsArg(select));
            if (_onChange)
            {
                sb.Append(" --onchange");
                if (hasDeadband) sb.Append(" --deadband ").Append(deadband.ToString("0.###", CultureInfo.InvariantCulture));
            }
            _cli = sb.ToString();
        }

        /// <summary>The shortest --vessels value that Selectors.Parse turns back into exactly <paramref name="select"/>.</summary>
        private static string VesselsArg(JsonValue select)
        {
            var v = select.GetObj("vessels");
            if (select.Count == 1 && v != null && v.Count == 1)
            {
                if (v.GetBool("active")) return "active";
                string glob = v.GetString("nameLike");
                if (glob != null && (glob.IndexOf('*') >= 0 || glob.IndexOf('?') >= 0) && glob.Trim() == glob && !glob.StartsWith("{", StringComparison.Ordinal))
                    return UiKit.ShQuote(glob);
                var hs = v.GetArr("handles");
                if (hs != null && hs.Count == 1)
                {
                    string h = hs[0].AsString("");
                    if (h.Length > 0 && h.IndexOf('*') < 0 && h.IndexOf('?') < 0 && !h.StartsWith("{", StringComparison.Ordinal) &&
                        !string.Equals(h, "active", StringComparison.OrdinalIgnoreCase) && h.Trim() == h)
                        return UiKit.ShQuote(h);
                }
            }
            return UiKit.ShQuote(select.ToString());
        }

        private static bool ValidName(string s)
        {
            if (s.Length < 1 || s.Length > 64) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-')) return false;
            }
            return true;
        }

        private bool PartsAvailable => _vesselMode == 0 || _vesselMode == 1;

        /// <summary>The chosen module class: from the part list, or typed when no part list is available.</summary>
        private string EffectiveModule
        {
            get
            {
                if (PartsAvailable) return _module;
                string t = (_moduleTyped ?? "").Trim();
                return t.Length > 0 ? t : null;
            }
        }

        // ------------------------------------------------------------------ drawing

        protected override void Body()
        {
            // 1. vessel
            GUILayout.Label("1. Vessel", UiKit.Header);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < VesselModes.Length; i++)
            {
                int m = i;
                UiKit.Choice(_vesselMode == m, VesselModes[i], () => { if (_vesselMode != m) { _vesselMode = m; ClearParts(); } });
            }
            GUILayout.EndHorizontal();
            if (_vesselMode == 1)
            {
                Ui.Vessels.EnsureLoaded();
                Ui.Vessels.DrawPicker(ref _vScroll, _vesselHandle, row => { if (_vesselHandle != row.Handle) { _vesselHandle = row.Handle; ClearParts(); } }, 110);
            }
            else if (_vesselMode == 2)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("nameLike", GUILayout.Width(80));
                _glob = UiKit.Text("picker.glob", _glob);
                GUILayout.EndHorizontal();
            }

            // 2. part and 3. module
            GUILayout.Label("2. Part and 3. module (optional: leave on vessel fields to log vessel values)", UiKit.Header);
            if (PartsAvailable)
            {
                GUILayout.BeginHorizontal();
                bool canLoad = VesselRef != null;
                if (canLoad) UiKit.Btn(_partsLoading ? "Loading parts..." : (_partsFor == VesselRef ? "Reload parts" : "Load parts and modules"), LoadParts, GUILayout.Width(200));
                else GUILayout.Label("Pick a vessel first.", UiKit.Grey);
                GUILayout.FlexibleSpace();
                if (_parts.Count > 0) GUILayout.Label(_parts.Count + " parts", UiKit.Grey);
                GUILayout.EndHorizontal();
                if (_partsError != null) GUILayout.Label(_partsError, UiKit.Red);
                if (_partsNote != null) GUILayout.Label(_partsNote, UiKit.Yellow);
                if (_parts.Count > 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical(GUILayout.Width(300));
                    GUILayout.Label("Part", UiKit.Bold);
                    _pScroll = GUILayout.BeginScrollView(_pScroll, GUILayout.Height(130));
                    UiKit.Choice(_partIdx < 0, "(any part)", () => { _partIdx = -1; });
                    for (int i = 0; i < _parts.Count; i++)
                    {
                        int idx = i;
                        UiKit.Choice(_partIdx == i, _parts[i].Title + " (" + _parts[i].Name + ")", () =>
                        {
                            _partIdx = idx;
                            if (_module != null && !_parts[idx].ModuleClasses.Contains(_module)) SetModule(null);
                        });
                    }
                    GUILayout.EndScrollView();
                    GUILayout.EndVertical();
                    GUILayout.BeginVertical();
                    GUILayout.Label("Module", UiKit.Bold);
                    _mScroll = GUILayout.BeginScrollView(_mScroll, GUILayout.Height(130));
                    UiKit.Choice(_module == null, "(vessel fields)", () => SetModule(null));
                    for (int i = 0; i < _moduleChoices.Count; i++)
                    {
                        string c = _moduleChoices[i];
                        UiKit.Choice(_module == c, c, () => SetModule(c));
                    }
                    GUILayout.EndScrollView();
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Module class (optional)", GUILayout.Width(160));
                _moduleTyped = UiKit.Text("picker.module", _moduleTyped);
                GUILayout.EndHorizontal();
                GUILayout.Label("With a glob or all loaded vessels there is no part list; type a module class (e.g. MissileFire) or leave empty for vessel fields.", UiKit.Small);
            }

            // 4. fields
            string module = EffectiveModule;
            GUILayout.Label("4. Fields" + (module != null ? " of " + module : " (vessel)"), UiKit.Header);
            List<FieldRow> source = module == null ? _vesselFields : _moduleFields;
            if (module != null && PartsAvailable)
            {
                if (_fieldsLoading) GUILayout.Label("Loading fields...", UiKit.Grey);
                if (_fieldsError != null) GUILayout.Label(_fieldsError, UiKit.Yellow);
            }
            if (source.Count > 0)
            {
                _fScroll = GUILayout.BeginScrollView(_fScroll, GUILayout.Height(140));
                for (int i = 0; i < source.Count; i += 2)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = i; j < Math.Min(i + 2, source.Count); j++)
                    {
                        var f = source[j];
                        bool on = _selected.Contains(f.Name);
                        UiKit.Tog(on, f.Label, v => { if (v) { if (!_selected.Contains(f.Name)) _selected.Add(f.Name); } else _selected.Remove(f.Name); }, GUILayout.Width(290));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("More fields (comma separated)", GUILayout.Width(200));
            _extra = UiKit.Text("picker.extra", _extra);
            GUILayout.EndHorizontal();

            // 5. rate, format, name
            GUILayout.Label("5. Rate, format, name", UiKit.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Rate (Hz, game time)", GUILayout.Width(140));
            _rate = UiKit.Text("picker.rate", _rate, GUILayout.Width(60));
            GUILayout.Space(10);
            for (int i = 0; i < Formats.Length; i++)
            {
                int fi = i;
                UiKit.Choice(_format == fi, Formats[i], () => _format = fi, GUILayout.Width(60));
            }
            GUILayout.Space(10);
            UiKit.Tog(_onChange, "on change", v => _onChange = v, GUILayout.Width(90));
            if (_onChange)
            {
                GUILayout.Label("deadband", GUILayout.Width(65));
                _deadband = UiKit.Text("picker.deadband", _deadband, GUILayout.Width(60));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(140));
            _name = UiKit.Text("picker.name", _name, GUILayout.Width(220));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 6. result
            GUILayout.Label("Equivalent CLI (bash / PowerShell quoting):", UiKit.Bold);
            GUILayout.Label(_cli ?? "", UiKit.Code);
            GUILayout.Label("rec.log.start arguments: " + (_argsText ?? ""), UiKit.Small);
            if (_buildError != null) GUILayout.Label(_buildError, UiKit.Yellow);
            GUILayout.BeginHorizontal();
            UiKit.Btn("Copy CLI", () => UiKit.Copy(_cli), GUILayout.Width(90));
            UiKit.Btn("Copy JSON", () => UiKit.Copy(_argsText), GUILayout.Width(90));
            GUILayout.FlexibleSpace();
            if (_starting) GUILayout.Label("starting...", UiKit.Yellow);
            UiKit.Btn("Start log", Start, GUILayout.Width(110));
            GUILayout.EndHorizontal();
            if (_status != null) GUILayout.Label(_status, UiKit.Wrap);
        }

        private void Start()
        {
            BuildArgs();
            if (_args == null || _starting) { if (_args == null) _status = _buildError; return; }
            _starting = true;
            _status = null;
            string usedName = _name;
            Ui.Call("rec.log.start", _args, (r, e) =>
            {
                _starting = false;
                if (e != null) { _status = UiKit.ErrorText(e); return; }
                var sb = new StringBuilder();
                sb.Append(r.GetString("log")).Append(" started: ").Append(r.GetString("path")).Append(" (").Append(UiKit.I(r.GetLong("targets"))).Append(" targets, ")
                  .Append(UiKit.N(r.GetDouble("rateHz"))).Append(" Hz, ").Append(r.GetString("format")).Append(')');
                if (r.GetString("warning") != null) sb.Append("\nwarning: ").Append(r.GetString("warning"));
                var unk = r.GetStringList("unknownFields");
                if (unk != null && unk.Count > 0) sb.Append("\nwarning: unknown fields: ").Append(string.Join(", ", unk.ToArray()));
                _status = sb.ToString();
                if (_name == usedName) _name = "log-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
                if (Ui.Recorder.Open) Ui.Recorder.Refresh();
            });
        }
    }
}
