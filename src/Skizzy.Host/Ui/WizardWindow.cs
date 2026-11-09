using System;
using System.Collections.Generic;
using System.IO;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// First-run wizard (21 §1, M0 steps): principle, token path and copyable MCP/Python snippets, preset, listener
    /// status, doctor summary. Finish sets <c>FirstRunDone</c> and saves settings.cfg. Reopened from the toolbar menu.
    /// </summary>
    internal sealed class WizardWindow : UiWindow
    {
        private static readonly string[] Steps = { "Welcome", "Connect", "Preset", "Listener", "Doctor" };
        private int _step;
        private string _exe, _exeNote;
        private bool _tokenExists;
        private string _copied, _presetStatus, _confirmPreset, _finishError;
        private List<string[]> _doctor;
        private string _doctorError;
        private bool _doctorRunning, _doctorRan;
        private Vector2 _scroll;

        internal WizardWindow(SkizzyUi ui, int index) : base(ui, index, "first run", Screen.width / 2f - 300, 110, 600, 300) { }

        protected override string Title => "Skizzy Protocol: setup (" + (_step + 1) + "/" + Steps.Length + " " + Steps[_step] + ")";

        internal override void OnOpened()
        {
            _step = 0;
            _copied = _presetStatus = _confirmPreset = _finishError = null;
            var paths = Ui.Host.Paths;
            try
            {
                string mod = Path.GetDirectoryName(Path.GetFullPath(paths.PluginData));
                _exe = Path.Combine(Path.Combine(mod, "Bin"), "skizzy.exe");
                _exeNote = File.Exists(_exe) ? null : "skizzy.exe was not found there; check that the Bin folder was installed.";
            }
            catch (Exception e)
            {
                _exe = Path.Combine(Path.Combine(Path.Combine(paths.KspRoot, "GameData"), "SkizzyProtocol"), Path.Combine("Bin", "skizzy.exe"));
                _exeNote = "Could not resolve the install folder: " + e.Message;
            }
            try { _tokenExists = File.Exists(paths.TokenFile); } catch (Exception) { _tokenExists = false; }
        }

        protected override void Close()
        {
            base.Close();
            Ui.DismissWizard(); // "later": not shown again this session, shown again next start until finished
        }

        private string ClaudeCmd => "claude mcp add skizzy -- \"" + _exe + "\" mcp";
        private string GenericCmd => "\"" + _exe + "\" mcp";
        private string CodexToml => "[mcp_servers.skizzy]\ncommand = " + Toml(_exe) + "\nargs = [\"mcp\"]";
        private const string PythonSnippet = "import skizzy; sk = skizzy.connect()";

        private static string Toml(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        protected override void Body()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Steps.Length; i++)
            {
                int s = i;
                UiKit.Choice(_step == s, (i + 1) + ". " + Steps[i], () => GoTo(s));
            }
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(330));
            switch (_step)
            {
                case 0: Welcome(); break;
                case 1: Connect(); break;
                case 2: Preset(); break;
                case 3: Listener(); break;
                default: Doctor(); break;
            }
            GUILayout.EndScrollView();

            if (_finishError != null) GUILayout.Label(_finishError, UiKit.Red);
            GUILayout.BeginHorizontal();
            UiKit.Btn("Later", Close, GUILayout.Width(80));
            GUILayout.FlexibleSpace();
            if (_step > 0) UiKit.Btn("Back", () => GoTo(_step - 1), GUILayout.Width(80));
            if (_step < Steps.Length - 1) UiKit.Btn("Next", () => GoTo(_step + 1), GUILayout.Width(80));
            else UiKit.Btn("Finish", Finish, GUILayout.Width(100));
            GUILayout.EndHorizontal();
        }

        private void GoTo(int s)
        {
            _step = Math.Max(0, Math.Min(Steps.Length - 1, s));
            _scroll = Vector2.zero;
            _copied = null;
            if (_step == Steps.Length - 1 && !_doctorRan) RunDoctor();
        }

        private void Welcome()
        {
            GUILayout.Label("Skizzy adds nothing to your game until something connects. BDArmory works exactly as before.", UiKit.Header);
            GUILayout.Space(6);
            GUILayout.Label("Skizzy Protocol lets an AI agent, a script or the CLI read the game, move the camera, take screenshots and log values, " +
                            "through a local socket on this computer only (127.0.0.1). With nothing connected the plugin does no per-frame work.", UiKit.Wrap);
            GUILayout.Space(6);
            GUILayout.Label("You stay in charge:", UiKit.Bold);
            GUILayout.Label("- Permissions decide what clients may do; destructive actions ask you here in game first.", UiKit.Wrap);
            GUILayout.Label("- The kill switch (" + Ui.Host.Settings.KillSwitchKey + " while a client is connected, or the toolbar menu) makes every client read-only at once.", UiKit.Wrap);
            GUILayout.Label("- Every call is written to the audit log in PluginData/audit.", UiKit.Wrap);
            GUILayout.Space(6);
            GUILayout.Label("This wizard takes a minute. You can reopen it any time from the Skizzy toolbar button.", UiKit.Grey);
        }

        private void Connect()
        {
            var paths = Ui.Host.Paths;
            UiKit.KeyValue("Token file", paths.Relative(paths.TokenFile) + (_tokenExists ? "" : "  (missing: restart KSP; skizzy doctor explains why)"), 90);
            GUILayout.Label("The token never leaves this computer and is never shown here. Clients find it, and the port, through PluginData/endpoint.json.", UiKit.Small);
            UiKit.KeyValue("skizzy.exe", _exe, 90);
            if (_exeNote != null) GUILayout.Label(_exeNote, UiKit.Yellow);

            Snippet("Claude Code (run in a terminal):", ClaudeCmd, "Claude Code command");
            Snippet("Codex CLI (add to ~/.codex/config.toml):", CodexToml, "Codex config");
            Snippet("Any other MCP client (stdio command):", GenericCmd, "stdio command");
            Snippet("Python (pip install skizzy):", PythonSnippet, "Python snippet");
            GUILayout.Label("Tip: skizzy mcp --print-config prints the same lines from a terminal.", UiKit.Small);
            if (_copied != null) GUILayout.Label("Copied: " + _copied, UiKit.Green);
        }

        private void Snippet(string title, string text, string what)
        {
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, UiKit.Bold);
            GUILayout.FlexibleSpace();
            UiKit.Btn("Copy", () => { UiKit.Copy(text); _copied = what; }, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            GUILayout.Label(text, UiKit.Code);
        }

        private void Preset()
        {
            string cur = Ui.Host.Permissions.Config.Preset;
            GUILayout.Label("Choose how much clients may do. You can fine-tune everything later in the Permissions window.", UiKit.Wrap);
            for (int i = 0; i < PermissionConfig.PresetNames.Length; i++)
            {
                string p = PermissionConfig.PresetNames[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                UiKit.Choice(cur == p, PermissionsWindow.PresetTitle(p) + (p == "BattleLab" ? " (default)" : ""), () => PickPreset(p, cur), GUILayout.Width(200));
                GUILayout.Label(PermissionsWindow.PresetHelp(p), UiKit.Small);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            if (_confirmPreset != null)
            {
                GUILayout.Label("Your permissions.cfg has been set up already; switching to " + PermissionsWindow.PresetTitle(_confirmPreset) + " replaces all its rules.", UiKit.Yellow);
                GUILayout.BeginHorizontal();
                UiKit.Btn("Replace", () => { ApplyPreset(_confirmPreset); _confirmPreset = null; }, GUILayout.Width(90));
                UiKit.Btn("Cancel", () => _confirmPreset = null, GUILayout.Width(80));
                GUILayout.EndHorizontal();
            }
            if (_presetStatus != null) GUILayout.Label(_presetStatus, UiKit.Green);
            UiKit.Btn("Open the Permissions window", () => Ui.Open(Ui.Permissions), GUILayout.Width(220));
        }

        private void PickPreset(string p, string cur)
        {
            if (p == cur) { _presetStatus = PermissionsWindow.PresetTitle(p) + " is active."; return; }
            if (Ui.Host.Settings.FirstRunDone) { _confirmPreset = p; return; } // reopened later: confirm before replacing custom rules
            ApplyPreset(p);
        }

        private void ApplyPreset(string p)
        {
            try
            {
                Ui.Host.SavePermissions(PermissionConfig.FromPreset(p));
                _presetStatus = "Saved: " + PermissionsWindow.PresetTitle(p) + " (permissions.cfg).";
            }
            catch (Exception e) { _presetStatus = "Could not save permissions.cfg: " + e.Message; }
        }

        private void Listener()
        {
            var h = Ui.Host;
            if (h.Port > 0)
            {
                GUILayout.Label("Listening on ws://127.0.0.1:" + h.Port + " (loopback only; nothing is reachable from other computers).", UiKit.Green);
                if (h.Port != h.Settings.PortFirst) GUILayout.Label("Port " + h.Settings.PortFirst + " was busy, so the next free port was used. Clients read it from endpoint.json.", UiKit.Yellow);
            }
            else if (!h.Settings.Listen) GUILayout.Label("The listener is turned off (listen = false in PluginData/settings.cfg). Nothing can connect.", UiKit.Yellow);
            else GUILayout.Label("No socket is open: " + (h.StartError ?? "unknown reason") + ". Ports " + h.Settings.PortFirst + "-" + h.Settings.PortLast + " were tried.", UiKit.Red);
            UiKit.KeyValue("Endpoint file", h.Paths.Relative(h.Paths.EndpointFile), 110);
            UiKit.KeyValue("Idle mode", h.WakerMode, 110);
            GUILayout.Space(6);
            GUILayout.Label("To test the connection end to end, run in a terminal:", UiKit.Wrap);
            GUILayout.Label("\"" + _exe + "\" doctor", UiKit.Code);
            GUILayout.Label("It connects through the same loopback socket, checks the token and prints green/yellow/red lines.", UiKit.Small);
        }

        private void Doctor()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("The in-game half of skizzy doctor:", UiKit.Bold);
            GUILayout.FlexibleSpace();
            UiKit.Btn(_doctorRunning ? "Running..." : "Run again", RunDoctor, GUILayout.Width(100));
            GUILayout.EndHorizontal();
            if (_doctorError != null) GUILayout.Label(_doctorError, UiKit.Red);
            if (_doctor != null) UiKit.DrawChecks(_doctor);
            else if (_doctorRunning) GUILayout.Label("Running host.doctor...", UiKit.Grey);
            GUILayout.Space(6);
            GUILayout.Label("Finish saves firstRunDone = true in PluginData/settings.cfg so this wizard does not open again.", UiKit.Small);
        }

        private void RunDoctor()
        {
            if (_doctorRunning) return;
            _doctorRunning = true;
            _doctorRan = true;
            Ui.Call("host.doctor", JsonValue.Obj(), (r, e) =>
            {
                _doctorRunning = false;
                if (e != null) { _doctorError = UiKit.ErrorText(e); _doctor = null; return; }
                _doctorError = null;
                _doctor = UiKit.Checks(r);
            });
        }

        private void Finish()
        {
            Ui.Host.Settings.FirstRunDone = true;
            string err = Ui.Boot.SaveSettings();
            if (err != null)
            {
                _finishError = "Could not save settings.cfg (" + err + "); the wizard will open again next time.";
                return;
            }
            _finishError = null;
            Open = false;
            ScreenMessages.PostScreenMessage("Skizzy is set up. Toolbar button: windows, permissions, kill switch.", 5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
