using System;
using System.Collections.Generic;
using Skizzy.Protocol.Permissions;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// Permissions (21 §3, M0): presets, areas, level defaults, per-mod and per-capability rules, ROLE and CLIENT rules,
    /// and the pending <c>ask</c> prompts. Edits go to a working copy; Save writes permissions.cfg through
    /// <see cref="Skizzy.Host.Core.HostCore.SavePermissions"/>. Nothing over the wire can do this.
    /// </summary>
    internal sealed class PermissionsWindow : UiWindow
    {
        private PermissionConfig _work;
        private int _baseRevision;
        private bool _dirty;
        private string _status;
        private string _confirmPreset;
        private Vector2 _scroll;
        private string _newCap = "", _newMod = "";
        private Policy _newCapPolicy = Policy.Ask;
        private int _tab;
        private static readonly string[] Tabs = { "Levels and areas", "Rules", "Roles and clients" };

        internal PermissionsWindow(SkizzyUi ui, int index) : base(ui, index, "Permissions", 640, 110, 600, 300) { }

        internal override void OnOpened() { Reload(); }

        private void Reload()
        {
            try
            {
                var cur = Ui.Host.Permissions.Config;
                _work = PermissionConfig.Parse(cur.Serialize(), new List<string>());
                _baseRevision = Ui.Host.CatalogRevision;
                _dirty = false;
                _confirmPreset = null;
            }
            catch (Exception e)
            {
                _status = "Could not read the current permissions: " + e.Message;
                _work = null;
                _baseRevision = Ui.Host.CatalogRevision; // retried on the next save or with the Reload button, not every frame
            }
        }

        internal override void OnLayout()
        {
            // Picked up "Always allow" answers or other saves while the copy is unchanged.
            if (!_dirty && Ui.Host.CatalogRevision != _baseRevision) Reload();
        }

        private void Changed() { _dirty = true; _status = null; }

        private void Save()
        {
            try
            {
                Ui.Host.SavePermissions(_work);
                Reload();
                _status = "Saved permissions.cfg (preset " + _work.Preset + ").";
            }
            catch (Exception e) { _status = "Save failed: " + e.Message; }
        }

        internal static string PresetTitle(string p)
        {
            switch (p)
            {
                case "BattleLab": return "Battle Lab";
                case "TestDirector": return "Test Director";
                case "FullCombat": return "Full Combat";
                case "Observer": return "Observer";
                default: return p ?? "?";
            }
        }

        internal static string PresetHelp(string p)
        {
            switch (p)
            {
                case "BattleLab": return "Default. Read and control allowed, destructive actions ask you first, cheats allowed (it is a test range), unsafe denied.";
                case "TestDirector": return "For scripted test runs. Same levels as Battle Lab in M0; later milestones add spawning and reset rules here.";
                case "FullCombat": return "Like Battle Lab, but destructive actions run without asking. For unattended AI-vs-AI battles.";
                case "Observer": return "Read only: agents can look, read and take screenshots, but cannot move the camera, start logs or control anything.";
                default: return "";
            }
        }

        protected override void Body()
        {
            if (Ui.Pending.Count > 0)
            {
                UiKit.Section("Pending prompts (" + Ui.Pending.Count + ")");
                for (int i = 0; i < Ui.Pending.Count; i++) PromptView.Draw(Ui, Ui.Pending[i]);
            }
            if (_work == null)
            {
                GUILayout.Label(_status ?? "No permissions loaded.", UiKit.Red);
                UiKit.Btn("Reload", Reload);
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset: " + PresetTitle(_work.Preset) + (_dirty ? "  (unsaved changes)" : ""), _dirty ? UiKit.Yellow : UiKit.Bold);
            GUILayout.FlexibleSpace();
            if (_dirty)
            {
                UiKit.Btn("Save", Save, GUILayout.Width(70));
                UiKit.Btn("Revert", () => { Reload(); _status = "Reverted."; }, GUILayout.Width(70));
            }
            GUILayout.EndHorizontal();
            if (_dirty && Ui.Host.CatalogRevision != _baseRevision)
                GUILayout.Label("permissions.cfg changed since you started editing (e.g. an Always allow answer); Save overwrites it.", UiKit.Yellow);

            GUILayout.BeginHorizontal();
            for (int i = 0; i < PermissionConfig.PresetNames.Length; i++)
            {
                string p = PermissionConfig.PresetNames[i];
                UiKit.Choice(_work.Preset == p, PresetTitle(p), () => _confirmPreset = p);
            }
            GUILayout.EndHorizontal();
            if (_confirmPreset != null)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(PresetTitle(_confirmPreset) + ": " + PresetHelp(_confirmPreset), UiKit.Wrap);
                GUILayout.Label("Applying a preset replaces all rules in permissions.cfg.", UiKit.Yellow);
                GUILayout.BeginHorizontal();
                UiKit.Btn("Apply and save", () =>
                {
                    _work = PermissionConfig.FromPreset(_confirmPreset);
                    _confirmPreset = null;
                    Save();
                }, GUILayout.Width(140));
                UiKit.Btn("Cancel", () => _confirmPreset = null, GUILayout.Width(80));
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < Tabs.Length; i++)
            {
                int t = i;
                UiKit.Choice(_tab == t, Tabs[i], () => _tab = t);
            }
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(380));
            if (_tab == 0) LevelsTab();
            else if (_tab == 1) RulesTab();
            else RolesTab();
            GUILayout.EndScrollView();
            if (_status != null) GUILayout.Label(_status, UiKit.Wrap);
        }

        // ------------------------------------------------------------------ tabs

        private void LevelsTab()
        {
            UiKit.Section("Level defaults");
            for (int i = 0; i < SafetyNames.All.Length; i++)
            {
                var s = SafetyNames.All[i];
                Policy cur = _work.LevelDefaults.TryGetValue(s, out var p) ? p : _work.DefaultPolicy;
                GUILayout.BeginHorizontal();
                GUILayout.Label(SafetyNames.Name(s), PromptView.SafetyStyle(SafetyNames.Name(s)), GUILayout.Width(110));
                PolicyButtons(cur, np => { _work.LevelDefaults[s] = np; Changed(); });
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Unsafe is never granted remotely: allow still asks in game.", UiKit.Small);

            UiKit.Section("Areas");
            var areas = PermissionConfig.KnownAreas;
            for (int i = 0; i < areas.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Math.Min(i + 2, areas.Length); j++)
                {
                    string a = areas[j];
                    bool on = _work.IsAreaEnabled(a);
                    string label = a == "deep" ? "deep (deep reflection, all mods)" : a;
                    UiKit.Tog(on, label, v => { _work.Areas[a] = v; Changed(); }, GUILayout.Width(270));
                }
                GUILayout.EndHorizontal();
            }
            UiKit.KeyValue("Confirm timeout", UiKit.I(_work.ConfirmTimeoutSec) + " s (settings.cfg confirmTimeoutS: " + UiKit.N(Ui.Host.Settings.ConfirmTimeoutS) + " s)", 130);
        }

        private void RulesTab()
        {
            UiKit.Section("Capability rules (most specific pattern wins)");
            if (_work.Caps.Count == 0) GUILayout.Label("(none)", UiKit.Grey);
            for (int i = 0; i < _work.Caps.Count; i++)
            {
                var cr = _work.Caps[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(cr.NamePattern + (string.IsNullOrEmpty(cr.RateLimit) ? "" : "  rate " + cr.RateLimit), GUILayout.Width(230));
                if (cr.HasPolicy) PolicyButtons(cr.Policy, np => { cr.Policy = np; Changed(); });
                else GUILayout.Label("(rate limit only)", UiKit.Grey);
                GUILayout.FlexibleSpace();
                int idx = i;
                UiKit.Btn("Remove", () => { _work.Caps.RemoveAt(idx); Changed(); }, GUILayout.Width(70));
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            _newCap = UiKit.Text("perm.newcap", _newCap, GUILayout.Width(220));
            PolicyButtons(_newCapPolicy, np => _newCapPolicy = np);
            UiKit.Btn("Add", () =>
            {
                string pat = (_newCap ?? "").Trim();
                if (pat.Length == 0) { _status = "Type a capability name or glob, e.g. ksp.camera.* or bda.*"; return; }
                _work.Caps.Add(new CapRule { NamePattern = pat, HasPolicy = true, Policy = _newCapPolicy });
                _newCap = "";
                Changed();
            }, GUILayout.Width(50));
            GUILayout.EndHorizontal();

            UiKit.Section("Per-mod levels");
            if (_work.Mods.Count == 0) GUILayout.Label("(none: every mod uses the level defaults)", UiKit.Grey);
            for (int i = 0; i < _work.Mods.Count; i++)
            {
                var m = _work.Mods[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label("MOD " + m.Id, UiKit.Bold);
                GUILayout.FlexibleSpace();
                int idx = i;
                UiKit.Btn("Remove", () => { _work.Mods.RemoveAt(idx); Changed(); }, GUILayout.Width(70));
                GUILayout.EndHorizontal();
                for (int k = 0; k < SafetyNames.All.Length; k++)
                {
                    var s = SafetyNames.All[k];
                    bool has = m.Levels.TryGetValue(s, out var mp);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(SafetyNames.Name(s), GUILayout.Width(110));
                    UiKit.Choice(!has, "default", () => { m.Levels.Remove(s); Changed(); }, GUILayout.Width(70));
                    PolicyButtons(has ? mp : (Policy)(-1), np => { m.Levels[s] = np; Changed(); });
                    GUILayout.EndHorizontal();
                }
                for (int k = 0; k < m.Caps.Count; k++)
                    GUILayout.Label("CAPABILITY " + m.Caps[k].NamePattern + (m.Caps[k].HasPolicy ? " " + SafetyNames.PolicyName(m.Caps[k].Policy) : "") +
                                    (string.IsNullOrEmpty(m.Caps[k].RateLimit) ? "" : " rate " + m.Caps[k].RateLimit), UiKit.Small);
                GUILayout.EndVertical();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Add mod (provider id, e.g. BDArmory):", GUILayout.Width(250));
            _newMod = UiKit.Text("perm.newmod", _newMod, GUILayout.Width(160));
            UiKit.Btn("Add", () =>
            {
                string id = (_newMod ?? "").Trim();
                if (id.Length == 0) return;
                for (int i = 0; i < _work.Mods.Count; i++)
                    if (string.Equals(_work.Mods[i].Id, id, StringComparison.OrdinalIgnoreCase)) { _status = "MOD " + id + " already exists."; return; }
                _work.Mods.Add(new ModRule { Id = id });
                _newMod = "";
                Changed();
            }, GUILayout.Width(50));
            GUILayout.EndHorizontal();
        }

        private void RolesTab()
        {
            UiKit.Section("ROLE rules (clamp what each role may do)");
            for (int i = 0; i < _work.Roles.Count; i++)
            {
                var r = _work.Roles[i];
                GUILayout.Label(r.Name + ": levels " + Levels(r.AllLevels, r.Levels) + ", max precedence " + r.MaxPrecedence +
                                (string.IsNullOrEmpty(r.Channels) ? "" : ", channels " + r.Channels) + (r.Seat ? ", seat" : ""), UiKit.Wrap);
            }
            UiKit.Section("CLIENT rules (cap levels by client name)");
            for (int i = 0; i < _work.Clients.Count; i++)
            {
                var c = _work.Clients[i];
                GUILayout.Label("\"" + c.NamePattern + "\": levels " + Levels(c.AllLevels, c.Levels), UiKit.Wrap);
            }
            GUILayout.Label("ROLE and CLIENT rules are edited in permissions.cfg (PluginData) while KSP is closed.", UiKit.Small);
        }

        private static string Levels(bool all, HashSet<Safety> levels)
        {
            if (all) return "*";
            var parts = new List<string>();
            foreach (var s in SafetyNames.All) if (levels.Contains(s)) parts.Add(SafetyNames.Name(s));
            return parts.Count == 0 ? "(none)" : string.Join(", ", parts.ToArray());
        }

        private static void PolicyButtons(Policy cur, Action<Policy> set)
        {
            UiKit.Choice(cur == Policy.Allow, "allow", () => set(Policy.Allow), GUILayout.Width(60));
            UiKit.Choice(cur == Policy.Ask, "ask", () => set(Policy.Ask), GUILayout.Width(50));
            UiKit.Choice(cur == Policy.Deny, "deny", () => set(Policy.Deny), GUILayout.Width(55));
        }
    }
}
