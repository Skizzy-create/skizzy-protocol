using System;
using Skizzy.Host.Core;
using Skizzy.Protocol.Permissions;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>The toolbar button's menu: opens each window, reopens the wizard, engages the kill switch.</summary>
    internal sealed class MenuWindow : UiWindow
    {
        internal MenuWindow(SkizzyUi ui, int index) : base(ui, index, "menu", Screen.width - 300, 80, 230, 100) { }

        protected override string Title => "Skizzy";

        protected override void Close()
        {
            base.Close();
            if (Ui.Toolbar != null) Ui.Toolbar.SetState(false);
        }

        protected override void Body()
        {
            var h = Ui.Host;
            GUILayout.Label(h.Port > 0 ? "Listening on 127.0.0.1:" + h.Port : h.StartError != null ? "Not listening: " + h.StartError : "Not listening", h.Port > 0 ? UiKit.Grey : UiKit.Yellow);
            Item(Ui.Connections, "Connections (" + h.AttachedSessionCount + ")");
            int pending = Ui.Pending.Count;
            Item(Ui.Permissions, pending > 0 ? "Permissions (" + pending + " pending)" : "Permissions");
            Item(Ui.CapConsole, "Capability console");
            Item(Ui.Recorder, "Recorder / logs");
            Item(Ui.Picker, "Log... picker");
            Item(Ui.CameraWin, "Camera");
            Item(Ui.Health, "Health");
            var c2 = Skizzy.C2.Toolbar.C2MenuEntry.Toggle; // the C2 window (Skizzy.C2.dll) registers itself in flight
            if (c2 != null) UiKit.Choice(Skizzy.C2.Toolbar.C2MenuEntry.Open, Skizzy.C2.Toolbar.C2MenuEntry.Label, c2);
            Item(Ui.Wizard, "First-run wizard");
            GUILayout.Space(6);
            if (Ui.KillEngaged) UiKit.Btn("Release kill switch", Ui.ReleaseKill);
            else UiKit.Btn("ENGAGE KILL SWITCH", Ui.EngageKill);
            GUILayout.Label("Hotkey " + h.Settings.KillSwitchKey + " (while a client is connected)", UiKit.Small);
        }

        private void Item(UiWindow w, string label)
        {
            UiKit.Choice(w.Open, label, () => Ui.ToggleWindow(w));
        }
    }

    /// <summary>
    /// Pending <c>ask</c> prompts (PLAN §6.6): shown automatically whenever a prompt is pending, on top of the game.
    /// The same rows are also listed in the Permissions window.
    /// </summary>
    internal sealed class PromptWindow : UiWindow
    {
        private Vector2 _scroll;

        internal PromptWindow(SkizzyUi ui, int index) : base(ui, index, "approval", Screen.width / 2f - 260, 120, 520, 100) { }

        internal override bool WantsShown => Ui.Pending.Count > 0;

        protected override string Title => "Skizzy: approval needed (" + Ui.Pending.Count + ")";

        protected override bool Closable => false; // answer the prompt instead

        protected override void Body()
        {
            if (Ui.Pending.Count > 2) _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(440));
            for (int i = 0; i < Ui.Pending.Count; i++) PromptView.Draw(Ui, Ui.Pending[i]);
            if (Ui.Pending.Count > 2) GUILayout.EndScrollView();
        }
    }

    internal static class PromptView
    {
        internal static void Draw(SkizzyUi ui, PromptRequest p)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            string who = p.ClientName ?? "unknown client";
            if (!string.IsNullOrEmpty(p.Role)) who += " (role " + p.Role + (p.ActorId != null ? ", actor " + p.ActorId : "") + ")";
            GUILayout.Label(who + (p.IsGrantRequest ? " asks for a permission grant:" : " wants to run:"), UiKit.Bold);
            string safety = p.Def != null ? SafetyNames.Name(p.Def.Safety) : "?";
            GUILayout.BeginHorizontal();
            GUILayout.Label(p.Capability ?? "?", UiKit.Bold);
            GUILayout.FlexibleSpace();
            GUILayout.Label("level: " + safety, SafetyStyle(safety));
            GUILayout.EndHorizontal();
            if (p.Def != null && !string.IsNullOrEmpty(p.Def.Description)) GUILayout.Label(UiKit.Clip(p.Def.Description, 300), UiKit.Small);
            GUILayout.Label("Arguments: " + UiKit.Clip(p.Summary ?? "(none)", 600), UiKit.Wrap);
            if (!string.IsNullOrEmpty(p.AiReason))
                GUILayout.Label("Reason given by the AI (AI-supplied, unverified): \"" + UiKit.Clip(p.AiReason, 400) + "\"", UiKit.Yellow);
            GUILayout.Label("Time left: " + (int)Math.Ceiling(p.RemainingS) + " s (then denied automatically)", UiKit.Grey);
            string id = p.Id;
            GUILayout.BeginHorizontal();
            UiKit.Btn("Allow once", () => Answer(ui, id, PromptAnswer.AllowOnce));
            UiKit.Btn("Allow for session", () => Answer(ui, id, PromptAnswer.AllowSession));
            UiKit.Btn("Always allow", () => Answer(ui, id, PromptAnswer.AllowAlways));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            UiKit.Btn("Deny", () => Answer(ui, id, PromptAnswer.Deny));
            UiKit.Btn("Deny and pause AI", () => Answer(ui, id, PromptAnswer.DenyAndPause));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private static void Answer(SkizzyUi ui, string id, PromptAnswer a)
        {
            if (!ui.Host.Prompts.Answer(id, a)) ui.Warn("prompt " + id + " was already answered or timed out");
            if (a == PromptAnswer.DenyAndPause) ui.Wake();
        }

        internal static GUIStyle SafetyStyle(string safety)
        {
            switch (safety)
            {
                case "read": return UiKit.Green;
                case "control": return UiKit.Wrap;
                case "destructive": case "cheat": return UiKit.Yellow;
                default: return UiKit.Red;
            }
        }
    }

    /// <summary>Kill-switch banner (17 M0): always visible while engaged, with the release button.</summary>
    internal sealed class KillBannerWindow : UiWindow
    {
        internal KillBannerWindow(SkizzyUi ui, int index) : base(ui, index, "kill switch", Screen.width / 2f - 280, 30, 560, 60) { }

        internal override bool WantsShown => Ui.KillEngaged;

        protected override string Title => "SKIZZY KILL SWITCH";

        protected override bool Closable => false; // stays while engaged

        protected override void Body()
        {
            GUILayout.Label("KILL SWITCH ENGAGED: every client is read-only; operations and pending prompts were cancelled.", UiKit.BannerText);
            GUILayout.Label("By " + (Ui.KillBy ?? "?") + (string.IsNullOrEmpty(Ui.KillReason) ? "" : ": " + Ui.KillReason) + "  (since " + (Ui.KillSince ?? "?") + ")", UiKit.Wrap);
            GUILayout.BeginHorizontal();
            UiKit.Btn("Release kill switch", Ui.ReleaseKill, GUILayout.Width(200));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Hotkey " + Ui.Host.Settings.KillSwitchKey, UiKit.Grey);
            GUILayout.EndHorizontal();
        }
    }
}
