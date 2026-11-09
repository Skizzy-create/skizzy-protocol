using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using Skizzy.C2.Toolbar;
using UnityEngine;

// Skizzy.BDArmory declares KSPAssemblyDependency("Skizzy.C2", 0, 1): KSP only loads it when this is present.
[assembly: KSPAssembly("Skizzy.C2", 0, 1)]

namespace Skizzy.C2.Ui
{
    /// <summary>
    /// The in-game C2 window ("C2 command"): RTB, all attack, attack nearest, weapons Free/Tight/Hold and advance on
    /// contact for the units of one side, with no AI client needed. It talks only to <see cref="IC2Console"/>
    /// (<c>C2Runtime.Current.Console</c>) through <see cref="C2ToolbarModel"/>.
    /// <list type="bullet">
    /// <item>Opened from the host's Skizzy menu (<see cref="C2MenuEntry"/>). Without the host it adds its own toolbar
    /// button.</item>
    /// <item>Ticks the C2 engine at 5 Hz from FixedUpdate (<c>C2Runtime.Tick("toolbar")</c>) while the window is open or
    /// after it commanded something; the runtime routes that tick through the host when the host owns the engine and
    /// skips it while the host's own C2 slot runs.</item>
    /// <item>IMGUI in BDArmory's look (KSP's HighLogic.Skin, as BDArmory's windows use). Clicks are applied in Update,
    /// never between IMGUI's Layout and Repaint passes.</item>
    /// </list>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class C2WindowAddon : MonoBehaviour
    {
        public const float TickPeriodS = 0.2f;
        private const float RefreshPeriodS = 0.5f;
        private const int WindowId = 0x5C2C0D;

        private C2ToolbarModel _model;
        private Action _toggle;
        private bool _open;
        private Rect _rect = new Rect(Screen.width - 520, 120, 420, 200);
        private Vector2 _scroll;
        private float _tickAcc;
        private float _nextRefresh;
        private readonly List<Action> _queued = new List<Action>();
        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _ownButton;
        private GUIStyle _small, _grey, _yellow, _header, _rowOn, _rowOff, _accent;
        private readonly List<C2Event> _reports = new List<C2Event>();

        private void Start()
        {
            var rt = C2Runtime.Current;
            if (rt.LogError == null) rt.LogError = Debug.LogError;
            _model = new C2ToolbarModel(() => C2Runtime.Current.Console, () => Time.realtimeSinceStartup);
            _toggle = () => Do(ToggleWindow); // applied in Update, never inside another window's IMGUI pass
            C2MenuEntry.Toggle = _toggle;
            C2MenuEntry.IsOpen = () => _open;
            _ownButton = !HostRunning();
            if (_ownButton)
            {
                GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
                GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
                if (ApplicationLauncher.Ready) AddButton();
            }
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(C2MenuEntry.Toggle, _toggle)) { C2MenuEntry.Toggle = null; C2MenuEntry.IsOpen = null; }
            if (_ownButton)
            {
                GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
                GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
                RemoveButton();
            }
            if (_icon != null) Destroy(_icon);
        }

        /// <summary>The host (Skizzy.Host) is running, so its menu carries the C2 entry. Read by reflection: no reference to the host.</summary>
        private static bool HostRunning()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Skizzy.Host") continue;
                    var t = a.GetType("Skizzy.Host.Ksp.SkizzyBootstrap", false);
                    var p = t != null ? t.GetProperty("CurrentHost", BindingFlags.Public | BindingFlags.Static) : null;
                    return p != null && p.GetValue(null, null) != null;
                }
            }
            catch (Exception e) { Debug.LogWarning("[Skizzy.C2] host check failed: " + e.Message); }
            return false;
        }

        private void ToggleWindow()
        {
            _open = !_open;
            if (_open) _nextRefresh = 0;
            if (_button != null)
            {
                if (_open) _button.SetTrue(false);
                else _button.SetFalse(false);
            }
        }

        // ---- own toolbar button (no host) ---------------------------------------------------------

        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;
            if (_icon == null) _icon = MakeIcon();
            _button = ApplicationLauncher.Instance.AddModApplication(OnButtonOn, OnButtonOff, Noop, Noop, Noop, Noop,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, _icon);
        }

        private void RemoveButton()
        {
            if (_button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        private static void Noop() { }
        private void OnButtonOn() { if (!_open) { _open = true; _nextRefresh = 0; } }
        private void OnButtonOff() { _open = false; }

        private static Texture2D MakeIcon()
        {
            // 38x38 "C2" glyph drawn in code: no texture files shipped.
            var t = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var ink = new Color(0.95f, 0.75f, 0.2f, 1f);
            for (int y = 0; y < 38; y++)
                for (int x = 0; x < 38; x++)
                {
                    bool c = (x >= 5 && x <= 8 && y >= 10 && y <= 27) || ((y >= 24 && y <= 27 || y >= 10 && y <= 13) && x >= 5 && x <= 17);
                    bool two = (y >= 24 && y <= 27 && x >= 21 && x <= 33) || (x >= 30 && x <= 33 && y >= 17 && y <= 27)
                               || (y >= 17 && y <= 20 && x >= 21 && x <= 33) || (x >= 21 && x <= 24 && y >= 10 && y <= 20)
                               || (y >= 10 && y <= 13 && x >= 21 && x <= 33);
                    t.SetPixel(x, y, c || two ? ink : clear);
                }
            t.Apply();
            return t;
        }

        // ---- ticking and refresh ------------------------------------------------------------------

        private void FixedUpdate()
        {
            _tickAcc += Time.fixedDeltaTime;
            if (_tickAcc < TickPeriodS) return;
            _tickAcc -= TickPeriodS;
            if (_tickAcc > TickPeriodS) _tickAcc = 0; // after a hitch, don't burst
            var rt = C2Runtime.Current;
            if (rt.Engine == null || !(_open || rt.Commanded)) return;
            rt.Tick(C2Runtime.ToolbarOwner);
        }

        private void Update()
        {
            if (_queued.Count > 0)
            {
                var run = _queued.ToArray();
                _queued.Clear();
                foreach (var a in run)
                {
                    try { a(); }
                    catch (Exception e)
                    {
                        Debug.LogError("[Skizzy.C2] window action failed: " + e);
                        _model.Message = "That failed: " + e.Message;
                    }
                }
                _nextRefresh = 0;
            }
            if (!_open || Time.realtimeSinceStartup < _nextRefresh) return;
            _nextRefresh = Time.realtimeSinceStartup + RefreshPeriodS;
            var v = FlightGlobals.ActiveVessel;
            try { _model.Refresh(v != null ? v.persistentId : 0); }
            catch (Exception e) { Debug.LogError("[Skizzy.C2] window refresh failed: " + e); }
            _reports.Clear();
            C2Runtime.Current.Console.CopyReports(_reports, 4);
        }

        private void Do(Action a) { _queued.Add(a); }

        // ---- drawing ------------------------------------------------------------------------------

        private void OnGUI()
        {
            if (!_open || _model == null) return;
            GUI.skin = HighLogic.Skin;
            if (_small == null) Styles();
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Skizzy C2", GUILayout.Width(420));
        }

        private void Styles()
        {
            var skin = HighLogic.Skin;
            _small = new GUIStyle(skin.label) { wordWrap = true, fontSize = 11 };
            _grey = new GUIStyle(_small);
            _grey.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            _yellow = new GUIStyle(_small);
            _yellow.normal.textColor = new Color(1f, 0.85f, 0.3f);
            _header = new GUIStyle(skin.label) { fontStyle = FontStyle.Bold };
            _rowOff = new GUIStyle(skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 11 };
            _rowOn = new GUIStyle(_rowOff) { fontStyle = FontStyle.Bold };
            _rowOn.normal.textColor = _rowOn.hover.textColor = new Color(0.4f, 1f, 0.4f);
            _accent = new GUIStyle(skin.button) { fontStyle = FontStyle.Bold };
            _accent.normal.textColor = _accent.hover.textColor = new Color(1f, 0.85f, 0.3f);
        }

        private void DrawWindow(int id)
        {
            var m = _model;
            GUILayout.BeginVertical();
            GUILayout.Label(m.Status, _grey);
            if (!m.Ready)
            {
                GUILayout.Label("Start a flight with BDArmory vessels. The window commands one side's units through the C2 engine.", _small);
                Close();
                GUILayout.EndVertical();
                GUI.DragWindow();
                return;
            }

            // side
            GUILayout.BeginHorizontal();
            GUILayout.Label("Side", _header, GUILayout.Width(40));
            for (int i = 0; i < m.Sides.Count; i++)
            {
                string s = m.Sides[i];
                bool on = s == m.Side;
                if (GUILayout.Toggle(on, s, HighLogic.Skin.button) && !on) Do(() => m.PickSide(s));
            }
            bool follow = !m.SidePicked;
            if (GUILayout.Toggle(follow, "Active vessel's", HighLogic.Skin.button) != follow) Do(() => m.PickSide(follow ? m.Side : null));
            GUILayout.EndHorizontal();

            // side-wide orders (top bar of the mockup)
            GUILayout.BeginHorizontal();
            bool adv = m.AdvanceOn;
            if (GUILayout.Toggle(adv, m.AdvanceLabel, HighLogic.Skin.button) != adv) Do(() => m.SetAdvance(!adv));
            if (GUILayout.Button(m.AllAttackLabel, m.AllAttackArmed ? _accent : HighLogic.Skin.button)) Do(() => m.AllAttack());
            if (GUILayout.Button(m.RtbAllLabel, m.RtbAllArmed ? _accent : HighLogic.Skin.button)) Do(() => m.RtbAll());
            GUILayout.EndHorizontal();

            // selection orders
            GUILayout.Label("Orders for the selection (" + m.Selected.Count + ")", _header);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Attack nearest", _accent)) Do(() => m.AttackNearest());
            if (GUILayout.Button("RTB & land", _accent)) Do(() => m.RtbSelected());
            if (GUILayout.Button("Release")) Do(() => m.ReleaseSelected());
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Weapons control", GUILayout.Width(120));
            var wc = m.SelectionWeapons;
            Seg("Free", wc == WeaponsControl.Free, () => m.SetWeapons(WeaponsControl.Free));
            Seg("Tight", wc == WeaponsControl.Tight, () => m.SetWeapons(WeaponsControl.Tight));
            Seg("Hold", wc == WeaponsControl.Hold, () => m.SetWeapons(WeaponsControl.Hold));
            GUILayout.EndHorizontal();

            // outcome
            if (!string.IsNullOrEmpty(m.Message)) GUILayout.Label(m.Message, _small);
            for (int i = 0; i < m.Lines.Count; i++) GUILayout.Label(m.Lines[i], _yellow);

            // unit list
            GUILayout.BeginHorizontal();
            GUILayout.Label("Units (" + m.Units.Count + ")", _header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All", GUILayout.Width(44))) Do(m.SelectAll);
            if (GUILayout.Button("None", GUILayout.Width(52))) Do(m.ClearSelection);
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Clamp(24 * m.Units.Count + 8, 40, 260)));
            for (int i = 0; i < m.Units.Count; i++)
            {
                var u = m.Units[i];
                bool sel = m.Selected.Contains(u.Id);
                uint uid = u.Id;
                if (GUILayout.Button((sel ? "> " : "   ") + C2ToolbarModel.Row(u), sel ? _rowOn : _rowOff))
                {
                    bool group = Event.current != null && Event.current.shift;
                    Do(() => { if (group) m.SelectGroupOf(uid); else m.Toggle(uid); });
                }
            }
            GUILayout.EndScrollView();

            // recent reports
            for (int i = 0; i < _reports.Count; i++) GUILayout.Label(_reports[i].Text, _grey);

            GUILayout.Label("Switching vessel selects it and its callsign group (\"Cobra 1\", \"Cobra 2\"...). Shift-click a row adds its group. " +
                            "Free = guard mode: engages hostiles it detects. Tight = assigned targets and close threats only. Hold = does not fire.", _grey);
            Close();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void Seg(string label, bool on, Action a)
        {
            if (GUILayout.Toggle(on, label, HighLogic.Skin.button, GUILayout.Width(80)) && !on) Do(a);
        }

        private void Close()
        {
            if (GUILayout.Button("Close")) Do(() => { if (_open) ToggleWindow(); });
        }
    }
}
