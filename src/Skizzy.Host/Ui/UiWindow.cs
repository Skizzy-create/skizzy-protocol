using System;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// One IMGUI window (GUILayout.Window) with a stable id, a position remembered for the session and its own fault
    /// fence: an exception in one window's body is caught, logged (throttled) and replaced by an error panel with a
    /// Retry button, so it cannot break the other windows.
    /// </summary>
    internal abstract class UiWindow
    {
        /// <summary>"SKZ" + index: stable ids so Unity keeps focus and order across frames.</summary>
        internal const int IdBase = 0x534B5A00;

        protected readonly SkizzyUi Ui;
        internal readonly int Id;
        internal readonly string Name;
        internal Rect Rect;
        internal bool Open;
        /// <summary>Visibility frozen on the Layout event so every event of a frame draws the same windows.</summary>
        internal bool Shown;
        protected float Width;
        private string _fault;
        private int _faults;
        private readonly GUI.WindowFunction _func;

        protected UiWindow(SkizzyUi ui, int index, string name, float x, float y, float width, float height)
        {
            Ui = ui;
            Id = IdBase + index;
            Name = name;
            Width = width;
            Rect = new Rect(x, y, width, height);
            _func = Func;
        }

        /// <summary>Whether the window wants to be drawn (default: the user opened it).</summary>
        internal virtual bool WantsShown => Open;

        protected virtual string Title => "Skizzy: " + Name;

        protected virtual bool Closable => true;

        /// <summary>Main thread (a click, the toolbar or a scene hook): called when the window is opened.</summary>
        internal virtual void OnOpened() { }

        /// <summary>Main thread, Layout event only, while shown: refresh snapshots here (never in Body).</summary>
        internal virtual void OnLayout() { }

        protected abstract void Body();

        internal void Toggle()
        {
            Open = !Open;
            if (Open) OnOpened();
        }

        internal void Show()
        {
            if (Open) return;
            Open = true;
            OnOpened();
        }

        internal void Draw()
        {
            Rect = GUILayout.Window(Id, Rect, _func, Title, GUILayout.Width(Width), GUILayout.MinHeight(60));
            // Keep at least a grabbable piece on screen (resolution changes, windows dragged away).
            float maxX = Math.Max(0, Screen.width - 80), maxY = Math.Max(0, Screen.height - 40);
            if (Rect.x > maxX) Rect.x = maxX;
            if (Rect.y > maxY) Rect.y = maxY;
            if (Rect.x < 80 - Rect.width) Rect.x = 80 - Rect.width;
            if (Rect.y < 0) Rect.y = 0;
        }

        private void Func(int id)
        {
            if (Closable && GUI.Button(new Rect(Rect.width - 24, 3, 20, 18), "x")) Close();
            try
            {
                if (_fault != null) DrawFault();
                else Body();
            }
            catch (ExitGUIException) { throw; }
            catch (Exception e)
            {
                _fault = e.GetType().Name + ": " + e.Message;
                _faults++;
                if (_faults <= 3 || _faults % 100 == 0) Ui.Warn("window '" + Name + "' failed (#" + _faults + "): " + e);
            }
            if (Event.current.type == EventType.Repaint && GUIUtility.keyboardControl != 0)
            {
                string f = GUI.GetNameOfFocusedControl();
                if (f != null && f.StartsWith("skz.", StringComparison.Ordinal)) Ui.TypingFrame = Time.frameCount;
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        protected virtual void Close() { Open = false; }

        private void DrawFault()
        {
            GUILayout.Label("This window hit an error and was paused:", UiKit.Red);
            GUILayout.Label(_fault, UiKit.Wrap);
            UiKit.Btn("Retry", () => _fault = null);
        }
    }
}
