using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// IMGUI helpers shared by the windows (21 §3). Every state change made by a click goes through <see cref="Btn"/>,
    /// <see cref="Tog"/> or <see cref="Choice"/>: the change is queued and applied at the start of the next Layout event,
    /// so a click that adds or removes controls never makes the rest of the same event disagree with the layout pass
    /// (no GUIUtility.ExitGUI needed). Data that arrives from other threads is applied on Layout events too
    /// (<see cref="SkizzyUi"/>'s inbox). All string literals are ASCII.
    /// </summary>
    internal static class UiKit
    {
        internal static GUIStyle Wrap, Small, Bold, Header, Green, Yellow, Red, Grey, Code, BannerText, Selected, Plain;
        private static GUISkin _for;
        internal static Action<string> Warn;

        internal static readonly Color GreenC = new Color(0.45f, 0.95f, 0.45f);
        internal static readonly Color YellowC = new Color(1f, 0.85f, 0.3f);
        internal static readonly Color RedC = new Color(1f, 0.4f, 0.35f);
        internal static readonly Color GreyC = new Color(0.7f, 0.7f, 0.7f);

        /// <summary>Main thread, inside OnGUI: (re)builds the styles for the current skin.</summary>
        internal static void Init(GUISkin skin)
        {
            if (_for == skin && Wrap != null) return;
            _for = skin;
            Plain = new GUIStyle(skin.label);
            Wrap = new GUIStyle(skin.label) { wordWrap = true };
            int bs = skin.label.fontSize > 0 ? skin.label.fontSize : 13; // 0 means "the font's default size"
            Small = new GUIStyle(skin.label) { wordWrap = true, fontSize = Math.Max(10, bs - 1) };
            Bold = new GUIStyle(skin.label) { wordWrap = true, fontStyle = FontStyle.Bold };
            Header = new GUIStyle(skin.label) { fontStyle = FontStyle.Bold, fontSize = bs + 2 };
            Green = Colored(skin.label, GreenC);
            Yellow = Colored(skin.label, YellowC);
            Red = Colored(skin.label, RedC);
            Grey = Colored(skin.label, GreyC);
            Code = new GUIStyle(skin.textArea) { wordWrap = true, richText = false };
            BannerText = new GUIStyle(skin.label) { wordWrap = true, fontStyle = FontStyle.Bold, fontSize = bs + 2 };
            BannerText.normal.textColor = RedC;
            Selected = new GUIStyle(skin.button) { fontStyle = FontStyle.Bold };
            Selected.normal.textColor = YellowC;
            Selected.hover.textColor = YellowC;
        }

        private static GUIStyle Colored(GUIStyle baseStyle, Color c)
        {
            var s = new GUIStyle(baseStyle) { wordWrap = true };
            s.normal.textColor = c;
            return s;
        }

        internal static GUIStyle StatusStyle(string status)
        {
            switch (status)
            {
                case "pass": case "ok": case "green": return Green;
                case "warn": case "yellow": return Yellow;
                case "fail": case "error": case "red": return Red;
                default: return Wrap;
            }
        }

        // ------------------------------------------------------------------ input helpers (changes run on the next Layout)

        private static readonly List<Action> Clicks = new List<Action>();

        /// <summary>A button whose <paramref name="onClick"/> runs at the start of the next Layout event.</summary>
        internal static bool Btn(string text, Action onClick, params GUILayoutOption[] opts)
        {
            if (!GUILayout.Button(text, opts)) return false;
            Clicks.Add(onClick);
            return true;
        }

        /// <summary>A button styled as selected when <paramref name="on"/>.</summary>
        internal static bool Choice(bool on, string text, Action onClick, params GUILayoutOption[] opts)
        {
            if (!GUILayout.Button(text, on ? Selected : GUI.skin.button, opts)) return false;
            Clicks.Add(onClick);
            return true;
        }

        internal static void Tog(bool value, string text, Action<bool> set, params GUILayoutOption[] opts)
        {
            bool v = GUILayout.Toggle(value, text, opts);
            if (v != value) Clicks.Add(() => set(v));
        }

        /// <summary>Main thread, start of a Layout event: applies the clicks of the previous events.</summary>
        internal static void RunClicks()
        {
            if (Clicks.Count == 0) return;
            var copy = Clicks.ToArray();
            Clicks.Clear();
            for (int i = 0; i < copy.Length; i++) Run(copy[i]);
        }

        internal static void DropClicks() { Clicks.Clear(); }

        /// <summary>A text field whose control name marks it as ours (so the input lock follows keyboard focus).</summary>
        internal static string Text(string name, string value, params GUILayoutOption[] opts)
        {
            GUI.SetNextControlName("skz." + name);
            return GUILayout.TextField(value ?? "", opts);
        }

        internal static string Area(string name, string value, params GUILayoutOption[] opts)
        {
            GUI.SetNextControlName("skz." + name);
            return GUILayout.TextArea(value ?? "", Code, opts);
        }

        private static void Run(Action a)
        {
            try { a(); }
            catch (ExitGUIException) { throw; }
            catch (Exception e) { Warn?.Invoke("UI action failed: " + e.GetType().Name + ": " + e.Message); }
        }

        internal static void Copy(string text)
        {
            try { GUIUtility.systemCopyBuffer = text ?? ""; }
            catch (Exception e) { Warn?.Invoke("clipboard: " + e.Message); }
        }

        internal static void Section(string title)
        {
            GUILayout.Space(4);
            GUILayout.Label(title, Header);
        }

        internal static void KeyValue(string key, string value, float keyWidth = 130)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(key, Grey, GUILayout.Width(keyWidth));
            GUILayout.Label(value ?? "-", Wrap);
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ formatting (invariant culture, ASCII)

        internal static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        internal static string N2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        internal static string I(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        internal static string Bytes(long b)
        {
            if (b < 1024) return I(b) + " B";
            if (b < 1024 * 1024) return N(Math.Round(b / 1024.0, 1)) + " KB";
            return N(Math.Round(b / (1024.0 * 1024.0), 1)) + " MB";
        }

        internal static string Clip(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        internal static bool TryNum(string text, out double v)
        {
            return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        internal static string ErrorText(SkizzyError e)
        {
            if (e == null) return null;
            var sb = new StringBuilder();
            sb.Append(ErrorKinds.Name(e.Kind)).Append(": ").Append(e.Message);
            if (!string.IsNullOrEmpty(e.Reason)) sb.Append(" (reason: ").Append(e.Reason).Append(')');
            string hint = e.Hint ?? ErrorKinds.DefaultHint(e.Kind);
            if (!string.IsNullOrEmpty(hint)) sb.Append("\nHint: ").Append(hint);
            return sb.ToString();
        }

        /// <summary>Renders host.doctor / host.selfcheck results ({checks:[{status,check,detail}]}) as coloured lines.</summary>
        internal static List<string[]> Checks(JsonValue r)
        {
            var list = new List<string[]>();
            var arr = r != null ? r.GetArr("checks") : null;
            if (arr == null) return list;
            for (int i = 0; i < arr.Count; i++)
            {
                var c = arr[i];
                if (c == null || !c.IsObject) continue;
                list.Add(new[] { c.GetString("status", "?"), c.GetString("check", "?"), c.GetString("detail", "") });
            }
            return list;
        }

        internal static void DrawChecks(List<string[]> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                string tag = l[0] == "pass" ? "[green] " : l[0] == "warn" ? "[yellow] " : l[0] == "fail" ? "[red] " : "[" + l[0] + "] ";
                GUILayout.Label(tag + l[1] + ": " + l[2], StatusStyle(l[0]));
            }
        }

        /// <summary>Shell quoting for the displayed CLI line (bash and PowerShell both accept single quotes).</summary>
        internal static string ShQuote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "''";
            bool plain = true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ',' || c == ':' || c == '-' || c == '/' || c == '='))
                {
                    plain = false;
                    break;
                }
            }
            if (plain) return s;
            return "'" + s.Replace("'", "'\"'\"'") + "'";
        }
    }
}
