using System;
using KSP.UI.Screens;
using Skizzy.Host.Ksp;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// The one stock toolbar button (21 §3). Launcher events are registered through <see cref="KspEvents.AddLauncher"/>
    /// so host.selfcheck counts them. The icon is drawn in code (no texture files) and destroyed on shutdown.
    /// </summary>
    internal sealed class UiToolbar
    {
        private const ApplicationLauncher.AppScenes Scenes =
            ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT |
            ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.TRACKSTATION;

        private readonly SkizzyUi _ui;
        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _setting;

        internal UiToolbar(SkizzyUi ui) { _ui = ui; }

        internal void Attach(KspEvents events)
        {
            events.AddLauncher(OnReady, OnDestroyed);
            if (ApplicationLauncher.Ready) OnReady();
        }

        private void OnReady()
        {
            try
            {
                if (!ReferenceEquals(_button, null) || ApplicationLauncher.Instance == null) return;
                if (_icon == null) _icon = MakeIcon();
                _button = AddButton(ApplicationLauncher.Instance, _icon);
                if (_ui.Menu != null && _ui.Menu.Open && !ReferenceEquals(_button, null))
                {
                    _setting = true;
                    try { _button.SetTrue(false); } finally { _setting = false; }
                }
            }
            catch (Exception e) { _ui.Warn("toolbar button: " + e.Message); }
        }

        private void OnDestroyed()
        {
            Remove();
            if (_ui.Menu != null) _ui.CloseMenu();
        }

        private static void Noop() { }

        /// <summary>AddModApplication(onTrue, onFalse, onHover, onHoverOut, onEnable, onDisable, scenes, Texture). Once per scene.</summary>
        private ApplicationLauncherButton AddButton(ApplicationLauncher launcher, Texture icon)
        {
            return launcher.AddModApplication(OnTrue, OnFalse, Noop, Noop, Noop, Noop, Scenes, icon);
        }

        private void OnTrue()
        {
            if (_setting) return;
            _ui.OpenMenu();
        }

        private void OnFalse()
        {
            if (_setting) return;
            _ui.CloseMenu();
        }

        /// <summary>Main thread: mirror the menu state on the button without calling back.</summary>
        internal void SetState(bool on)
        {
            if (ReferenceEquals(_button, null)) return;
            _setting = true;
            try
            {
                if (on) _button.SetTrue(false);
                else _button.SetFalse(false);
            }
            catch (Exception e) { _ui.Warn("toolbar state: " + e.Message); }
            finally { _setting = false; }
        }

        private void Remove()
        {
            var b = _button;
            _button = null;
            if (ReferenceEquals(b, null)) return;
            try { if (ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(b); }
            catch (Exception e) { _ui.Warn("toolbar remove: " + e.Message); }
        }

        internal void Shutdown()
        {
            Remove();
            if (_icon != null) UnityEngine.Object.Destroy(_icon);
            _icon = null;
        }

        /// <summary>38x38 icon: a ring with a crosshair and a centre dot (a range finder), white on transparent.</summary>
        private static Texture2D MakeIcon()
        {
            const int S = 38;
            var tex = new Texture2D(S, S, TextureFormat.ARGB32, false);
            var px = new Color32[S * S];
            var clear = new Color32(0, 0, 0, 0);
            var white = new Color32(235, 240, 245, 255);
            var accent = new Color32(255, 170, 60, 255);
            float c = (S - 1) / 2f;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = x - c, dy = y - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    Color32 col = clear;
                    if (r >= 12.5f && r <= 15f) col = white;                                     // ring
                    else if ((Mathf.Abs(dx) <= 1f && Mathf.Abs(dy) >= 5f && Mathf.Abs(dy) <= 18f) ||
                             (Mathf.Abs(dy) <= 1f && Mathf.Abs(dx) >= 5f && Mathf.Abs(dx) <= 18f)) col = white; // crosshair ticks
                    else if (r <= 2.6f) col = accent;                                            // centre dot
                    px[y * S + x] = col;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            tex.name = "SkizzyToolbarIcon";
            return tex;
        }
    }
}
