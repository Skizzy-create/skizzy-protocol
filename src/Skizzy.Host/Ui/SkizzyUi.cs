using System;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Host.Ksp;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// The in-game UI (21 §1, §3): one MonoBehaviour that owns every window. It is <c>enabled = false</c> whenever no
    /// window, prompt, kill banner or wizard is visible, so Unity never calls its OnGUI and the game pays nothing
    /// (acceptance 1). It has no Update. It is switched on only from the main thread: the toolbar button, the
    /// bootstrap's per-frame hook while the pump is awake (prompts pending or kill switch engaged), a post to the
    /// UnitySynchronizationContext when a prompt arrives, and the first-run wizard on scene load. It switches itself off
    /// on the first Layout event where nothing is visible.
    /// </summary>
    internal sealed class SkizzyUi : MonoBehaviour
    {
        internal SkizzyBootstrap Boot;
        internal HostCore Host;
        private bool _ready;

        // windows
        internal MenuWindow Menu;
        internal WizardWindow Wizard;
        internal ConnectionsWindow Connections;
        internal PermissionsWindow Permissions;
        internal ConsoleWindow CapConsole;
        internal RecorderWindow Recorder;
        internal LogPickerWindow Picker;
        internal CameraWindow CameraWin;
        internal HealthWindow Health;
        internal PromptWindow PromptWin;
        internal KillBannerWindow KillBanner;
        private readonly List<UiWindow> _windows = new List<UiWindow>();

        internal UiToolbar Toolbar;
        internal UiVessels Vessels;

        // Layout snapshots (read by every event of the frame).
        internal readonly List<PromptRequest> Pending = new List<PromptRequest>();
        internal bool KillEngaged;
        internal string KillBy, KillReason, KillSince;

        // Results from other threads, applied on the next Layout event.
        private readonly List<Action> _inbox = new List<Action>();
        private readonly List<Action> _drain = new List<Action>();
        private bool _drawCycle;
        private bool _raisePrompt, _raiseKill;
        private bool _locked;
        private const string LockId = "SkizzyProtocolUi";
        private int _warns;
        private bool _wizardDismissed;
        private Action _promptsChanged;
        private SendOrPostCallback _wakeFromPost;
        /// <summary>Frame in which a window saw one of our text fields focused (set inside the window functions).</summary>
        internal int TypingFrame = -10;

        internal void Init(SkizzyBootstrap boot)
        {
            Boot = boot;
            Host = boot.Host;
            UiKit.Warn = Warn;
            Vessels = new UiVessels(this);
            int i = 0;
            Menu = Add(new MenuWindow(this, i++));
            Wizard = Add(new WizardWindow(this, i++));
            Connections = Add(new ConnectionsWindow(this, i++));
            Permissions = Add(new PermissionsWindow(this, i++));
            CapConsole = Add(new ConsoleWindow(this, i++));
            Recorder = Add(new RecorderWindow(this, i++));
            Picker = Add(new LogPickerWindow(this, i++));
            CameraWin = Add(new CameraWindow(this, i++));
            Health = Add(new HealthWindow(this, i++));
            PromptWin = Add(new PromptWindow(this, i++));
            KillBanner = Add(new KillBannerWindow(this, i++));

            _wakeFromPost = _ => WakeIfNeeded();
            _promptsChanged = OnPromptsChanged;
            Host.Prompts.Changed += _promptsChanged;
            Toolbar = new UiToolbar(this);
            Toolbar.Attach(boot.Events);
            _ready = true;
        }

        private T Add<T>(T w) where T : UiWindow
        {
            _windows.Add(w);
            return w;
        }

        // ------------------------------------------------------------------ opening (main thread only)

        /// <summary>Main thread: turn OnGUI on (no-op when already on).</summary>
        internal void Wake()
        {
            if (_ready && !enabled) enabled = true;
        }

        /// <summary>Main thread: turn on if a prompt or the kill banner needs to be seen.</summary>
        internal void WakeIfNeeded()
        {
            if (this == null || !_ready || enabled) return;
            if (Host.Prompts.Count > 0 || Host.Kill.Engaged) enabled = true;
        }

        /// <summary>Any thread (PromptQueue.Changed): post the wake to the main thread; never touch Unity here.</summary>
        private void OnPromptsChanged()
        {
            var ctx = Boot != null && Boot.Waker != null ? Boot.Waker.Context : null;
            if (ctx != null) ctx.Post(_wakeFromPost, null);
            // Polling mode: a pending prompt keeps the pump awake and the bootstrap's frame hook wakes us.
        }

        internal void Open(UiWindow w)
        {
            w.Show();
            Wake();
        }

        internal void ToggleWindow(UiWindow w)
        {
            w.Toggle();
            if (w.Open) Wake();
        }

        internal void OpenMenu()
        {
            Menu.Show();
            Wake();
        }

        internal void CloseMenu() { Menu.Open = false; }

        internal void OpenWizard()
        {
            Wizard.Show();
            Wake();
        }

        internal void DismissWizard() { _wizardDismissed = true; }

        /// <summary>Main thread, after a scene's GUI is ready.</summary>
        internal void SceneReady(GameScenes scene)
        {
            if (!_ready) return;
            Menu.Open = false;
            Vessels.Invalidate();
            if (!Host.Settings.FirstRunDone && !_wizardDismissed && (scene == GameScenes.SPACECENTER || scene == GameScenes.FLIGHT))
                OpenWizard();
        }

        // ------------------------------------------------------------------ calls (same pipeline as any client)

        /// <summary>
        /// Main thread: runs a capability through <see cref="HostCore.InvokeLocal"/> (permission checks and audit like any
        /// client). <paramref name="done"/> runs on the main thread during a Layout event, whatever thread finished the call.
        /// </summary>
        internal void Call(string capability, JsonValue args, Action<JsonValue, SkizzyError> done)
        {
            try
            {
                Host.RefreshMeta(); // the scene check uses meta, which is only refreshed while the pump runs
                Host.InvokeLocal(capability, args ?? JsonValue.Obj(), (r, e) => Post(() => done(r, e)));
            }
            catch (Exception ex)
            {
                var err = ex as SkizzyError ?? new SkizzyError(ErrorKind.InternalError, ex.GetType().Name + ": " + ex.Message);
                Post(() => done(null, err));
            }
        }

        /// <summary>Any thread: queue work for the next Layout event.</summary>
        internal void Post(Action a)
        {
            lock (_inbox) _inbox.Add(a);
        }

        private void Drain()
        {
            lock (_inbox)
            {
                if (_inbox.Count == 0) return;
                _drain.AddRange(_inbox);
                _inbox.Clear();
            }
            for (int i = 0; i < _drain.Count; i++)
            {
                try { _drain[i](); }
                catch (Exception e) { Warn("UI callback failed: " + e.Message); }
            }
            _drain.Clear();
        }

        internal void Warn(string msg)
        {
            // Throttled: a persistent fault must not flood KSP.log every frame.
            _warns++;
            if (_warns <= 20 || _warns % 500 == 0)
            {
                try { Boot.Log.Warn("ui: " + msg); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------ OnGUI (only while enabled)

        private void OnGUI()
        {
            if (!_ready) { enabled = false; return; }
            var ev = Event.current;
            if (ev.type == EventType.Layout)
            {
                UiKit.RunClicks();
                Drain();
                Snapshot();
                bool any = false;
                for (int i = 0; i < _windows.Count; i++)
                {
                    var w = _windows[i];
                    bool show;
                    try { show = w.WantsShown; }
                    catch (Exception e) { show = false; Warn("visibility of " + w.Name + ": " + e.Message); }
                    w.Shown = show;
                    if (show)
                    {
                        any = true;
                        try { w.OnLayout(); }
                        catch (Exception e) { Warn("layout of " + w.Name + ": " + e.Message); }
                    }
                }
                _drawCycle = any;
                if (!any)
                {
                    SetLock(false);
                    if (Toolbar != null) Toolbar.SetState(false);
                    enabled = false; // nothing visible: Unity stops calling OnGUI
                    return;
                }
            }
            if (!_drawCycle) return;

            var prevSkin = GUI.skin;
            try
            {
                var skin = HighLogic.Skin;
                if (skin != null) GUI.skin = skin;
                UiKit.Init(GUI.skin);
                for (int i = 0; i < _windows.Count; i++)
                    if (_windows[i].Shown) _windows[i].Draw();
                if (ev.type == EventType.Repaint)
                {
                    // A new prompt or a fresh kill banner goes on top of the other windows once (not every frame).
                    if (_raisePrompt && PromptWin.Shown) GUI.BringWindowToFront(PromptWin.Id);
                    if (_raiseKill && KillBanner.Shown) GUI.BringWindowToFront(KillBanner.Id);
                    _raisePrompt = _raiseKill = false;
                }
                if (ev.type == EventType.Repaint || ev.type == EventType.MouseDown) UpdateLock(ev);
            }
            finally { GUI.skin = prevSkin; }
        }

        private void Snapshot()
        {
            int before = Pending.Count;
            bool killBefore = KillEngaged;
            Pending.Clear();
            Host.Prompts.Snapshot(Pending);
            if (Pending.Count > before) _raisePrompt = true;
            var k = Host.Kill;
            KillEngaged = k.Engaged;
            if (KillEngaged && !killBefore) _raiseKill = true;
            if (KillEngaged)
            {
                KillBy = k.By;
                KillReason = k.Reason;
                KillSince = k.Since;
            }
        }

        /// <summary>Blocks clicks and key bindings from reaching the game while the mouse is over a window or one of our text fields has focus.</summary>
        private void UpdateLock(Event ev)
        {
            Vector2 mouse = ev.mousePosition;
            bool over = false;
            for (int i = 0; i < _windows.Count; i++)
                if (_windows[i].Shown && _windows[i].Rect.Contains(mouse)) { over = true; break; }
            bool typing = Time.frameCount - TypingFrame <= 2;
            if (ev.type == EventType.MouseDown && !over && typing)
            {
                GUIUtility.keyboardControl = 0; // a click in the game gives the keyboard back to the game
                TypingFrame = -10;
                typing = false;
            }
            SetLock(over || typing);
        }

        private void SetLock(bool on)
        {
            if (on == _locked) return;
            try
            {
                if (on) InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockId);
                else InputLockManager.RemoveControlLock(LockId);
                _locked = on;
            }
            catch (Exception e) { Warn("input lock: " + e.Message); }
        }

        private void OnDisable()
        {
            if (_locked) SetLock(false);
        }

        // ------------------------------------------------------------------ kill switch (in-game, human)

        internal void EngageKill()
        {
            if (Host.EngageKill("human (in-game)", "engaged from the in-game menu"))
                ScreenMessages.PostScreenMessage("Skizzy KILL SWITCH ENGAGED: clients are read-only", 5f, ScreenMessageStyle.UPPER_CENTER);
            Wake();
        }

        internal void ReleaseKill()
        {
            if (Host.ReleaseKill("human (in-game)"))
                ScreenMessages.PostScreenMessage("Skizzy kill switch released", 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ------------------------------------------------------------------ shutdown

        internal void Shutdown()
        {
            if (!_ready) return;
            _ready = false;
            try { if (_promptsChanged != null) Host.Prompts.Changed -= _promptsChanged; } catch (Exception) { }
            try { if (Toolbar != null) Toolbar.Shutdown(); } catch (Exception e) { Warn("toolbar shutdown: " + e.Message); }
            SetLock(false);
            UiKit.DropClicks();
            enabled = false;
        }
    }

    /// <summary>A vessel row shared by the pickers (from ksp.vessels.list).</summary>
    internal sealed class VesselRow
    {
        internal string Handle, Name, Type, Situation, State;
        internal bool Active;
        internal string Label => (Active ? "* " : "") + Name + "  (" + Type + ", " + Situation + (State != null && State != "live" ? ", " + State : "") + ")";
    }

    /// <summary>The vessel list shared by the console, camera and Log... picker; loaded on demand only.</summary>
    internal sealed class UiVessels
    {
        private readonly SkizzyUi _ui;
        internal readonly List<VesselRow> Rows = new List<VesselRow>();
        internal bool Loading;
        internal bool Loaded;
        internal string Error;
        internal int Version;

        internal UiVessels(SkizzyUi ui) { _ui = ui; }

        internal void Invalidate()
        {
            Rows.Clear();
            Loaded = false;
            Error = null;
            Version++;
        }

        internal void EnsureLoaded()
        {
            if (!Loaded && !Loading && Error == null) Refresh();
        }

        internal void Refresh()
        {
            if (Loading) return;
            Loading = true;
            Error = null;
            _ui.Call("ksp.vessels.list", JsonValue.Obj().Set("limit", 1000), (r, e) =>
            {
                Loading = false;
                Loaded = true;
                Rows.Clear();
                Version++;
                if (e != null) { Error = UiKit.ErrorText(e); return; }
                var arr = r != null ? r.GetArr("vessels") : null;
                if (arr == null) return;
                for (int i = 0; i < arr.Count; i++)
                {
                    var v = arr[i];
                    Rows.Add(new VesselRow
                    {
                        Handle = v.GetString("handle"),
                        Name = v.GetString("name", "?"),
                        Type = v.GetString("type", "?"),
                        Situation = v.GetString("situation", "?"),
                        State = v.GetString("state"),
                        Active = v.GetBool("active"),
                    });
                }
            });
        }

        internal VesselRow Find(string handle)
        {
            if (handle == null) return null;
            for (int i = 0; i < Rows.Count; i++) if (Rows[i].Handle == handle) return Rows[i];
            return null;
        }

        /// <summary>Draws a compact vessel list; returns nothing, calls <paramref name="pick"/> on click.</summary>
        internal void DrawPicker(ref Vector2 scroll, string selectedHandle, Action<VesselRow> pick, float height)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loading ? "Loading vessels..." : Rows.Count + " vessels", UiKit.Grey);
            UiKit.Btn("Refresh", Refresh, GUILayout.Width(80));
            GUILayout.EndHorizontal();
            if (Error != null) GUILayout.Label(Error, UiKit.Red);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                UiKit.Choice(row.Handle == selectedHandle, row.Label, () => pick(row));
            }
            if (Loaded && Rows.Count == 0 && Error == null) GUILayout.Label("(no vessels)", UiKit.Grey);
            GUILayout.EndScrollView();
        }
    }
}
