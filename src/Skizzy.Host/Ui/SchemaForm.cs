using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Skizzy.Protocol.Json;
using UnityEngine;

namespace Skizzy.Host.Ui
{
    /// <summary>
    /// A form generated from a capability's input JSON Schema (21 §3, capability console): strings, numbers, integers
    /// and booleans as fields, enums as selection buttons, defaults prefilled, ranges and units shown, lists of strings as
    /// comma-separated text, and anything nested (objects, arrays of objects, oneOf, free-form) as a raw JSON text area.
    /// Vessel handles get a picker from <see cref="UiVessels"/>.
    /// </summary>
    internal sealed class SchemaForm
    {
        internal enum Kind { Str, Num, Int, Bool, Enum, List, Raw }

        internal sealed class Field
        {
            internal string Name;
            internal Kind Kind;
            internal bool Required;
            internal bool ListAllowsString;   // ["string","array"]: one value is sent as a string
            internal string Text = "";
            internal int BoolState;           // 0 unset, 1 true, 2 false
            internal string[] Enum;
            internal int EnumIndex = -1;
            internal string Help;
            internal string Range;
            internal string HandleKind;
            internal bool Picking;
        }

        internal readonly List<Field> Fields = new List<Field>();
        internal bool Closed;
        internal string Note;
        private Vector2 _pickScroll;

        internal static SchemaForm From(JsonValue schema)
        {
            var f = new SchemaForm();
            if (schema == null || !schema.IsObject) { f.Note = "No input schema: arguments are sent as {}."; return f; }
            f.Closed = schema.Has("additionalProperties") && !schema.GetBool("additionalProperties", true);
            var props = schema.GetObj("properties");
            var req = schema.GetStringList("required") ?? new List<string>();
            if (props == null) return f;
            for (int i = 0; i < props.Count; i++)
            {
                string name = props.KeyAt(i);
                var s = props.ValueAt(i);
                f.Fields.Add(MakeField(name, s, req.Contains(name)));
            }
            return f;
        }

        private static Field MakeField(string name, JsonValue s, bool required)
        {
            var fd = new Field { Name = name, Required = required };
            if (s == null || !s.IsObject) { fd.Kind = Kind.Raw; return fd; }
            fd.Help = s.GetString("description");
            fd.HandleKind = s.GetString("x-skz-handle");
            string type = s.GetString("type");
            var types = s.GetArr("type");
            var en = s.GetArr("enum");
            if (en != null && en.Count > 0)
            {
                fd.Kind = Kind.Enum;
                fd.Enum = new string[en.Count];
                for (int i = 0; i < en.Count; i++) fd.Enum[i] = en[i].IsString ? en[i].AsString() : en[i].ToString();
            }
            else if (types != null)
            {
                bool hasStr = false, hasArr = false;
                for (int i = 0; i < types.Count; i++)
                {
                    if (types[i].AsString() == "string") hasStr = true;
                    else if (types[i].AsString() == "array") hasArr = true;
                }
                var items = s.GetObj("items");
                if (hasArr && (items == null || items.GetString("type") == "string")) { fd.Kind = Kind.List; fd.ListAllowsString = hasStr; }
                else if (hasStr && types.Count == 1) fd.Kind = Kind.Str;
                else fd.Kind = Kind.Raw;
            }
            else
            {
                switch (type)
                {
                    case "string": fd.Kind = Kind.Str; break;
                    case "number": fd.Kind = Kind.Num; break;
                    case "integer": fd.Kind = Kind.Int; break;
                    case "boolean": fd.Kind = Kind.Bool; break;
                    case "array":
                        var items = s.GetObj("items");
                        fd.Kind = items != null && items.GetString("type") == "string" && !items.Has("enum") ? Kind.List : Kind.Raw;
                        break;
                    default: fd.Kind = Kind.Raw; break;
                }
            }
            fd.Range = RangeText(s, fd.Kind);
            var def = s.Get("default");
            if (def != null && !def.IsNull) SetValue(fd, def);
            return fd;
        }

        private static string RangeText(JsonValue s, Kind k)
        {
            var sb = new StringBuilder();
            bool hasMin = s.Has("minimum"), hasMax = s.Has("maximum");
            if (hasMin || hasMax)
                sb.Append(hasMin ? UiKit.N(s.GetDouble("minimum")) : "").Append(" .. ").Append(hasMax ? UiKit.N(s.GetDouble("maximum")) : "");
            if (s.Has("exclusiveMinimum")) sb.Append(sb.Length > 0 ? ", " : "").Append("> ").Append(UiKit.N(s.GetDouble("exclusiveMinimum")));
            string unit = s.GetString("x-skz-unit");
            if (unit != null) sb.Append(sb.Length > 0 ? " " : "").Append(unit);
            if (s.Has("maxLength") && k == Kind.Str) sb.Append(sb.Length > 0 ? ", " : "").Append("max ").Append(UiKit.I(s.GetLong("maxLength"))).Append(" chars");
            string pat = s.GetString("pattern");
            if (pat != null) sb.Append(sb.Length > 0 ? ", " : "").Append("pattern ").Append(pat);
            if (s.Has("maxItems")) sb.Append(sb.Length > 0 ? ", " : "").Append("max ").Append(UiKit.I(s.GetLong("maxItems"))).Append(" items");
            var def = s.Get("default");
            if (def != null && !def.IsNull) sb.Append(sb.Length > 0 ? ", " : "").Append("default ").Append(def.IsString ? def.AsString() : def.ToString());
            return sb.ToString();
        }

        private static void SetValue(Field fd, JsonValue v)
        {
            switch (fd.Kind)
            {
                case Kind.Str: fd.Text = v.IsString ? v.AsString() : v.ToString(); break;
                case Kind.Num:
                case Kind.Int: fd.Text = v.IsNumber ? v.ToString() : v.IsString ? v.AsString() : v.ToString(); break;
                case Kind.Bool: fd.BoolState = v.IsBool ? (v.AsBool() ? 1 : 2) : 0; break;
                case Kind.Enum:
                    fd.EnumIndex = -1;
                    string sv = v.IsString ? v.AsString() : v.ToString();
                    for (int i = 0; i < fd.Enum.Length; i++) if (fd.Enum[i] == sv) fd.EnumIndex = i;
                    break;
                case Kind.List:
                    if (v.IsString) fd.Text = v.AsString();
                    else if (v.IsArray)
                    {
                        var parts = new List<string>();
                        for (int i = 0; i < v.Count; i++) parts.Add(v[i].IsString ? v[i].AsString() : v[i].ToString());
                        fd.Text = string.Join(", ", parts.ToArray());
                    }
                    else fd.Text = v.ToString();
                    break;
                default: fd.Text = v.ToPrettyString(); break;
            }
        }

        /// <summary>Loads values from a JSON object (raw mode back to the form). Unknown keys are reported.</summary>
        internal string Load(JsonValue args)
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                var fd = Fields[i];
                fd.Text = "";
                fd.BoolState = 0;
                fd.EnumIndex = -1;
            }
            if (args == null || !args.IsObject) return null;
            var unknown = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                var fd = Find(args.KeyAt(i));
                if (fd == null) { unknown.Add(args.KeyAt(i)); continue; }
                SetValue(fd, args.ValueAt(i));
            }
            return unknown.Count > 0 ? "not in the form (dropped): " + string.Join(", ", unknown.ToArray()) : null;
        }

        private Field Find(string name)
        {
            for (int i = 0; i < Fields.Count; i++) if (Fields[i].Name == name) return Fields[i];
            return null;
        }

        /// <summary>Builds the arguments object, or returns null and sets <paramref name="error"/>.</summary>
        internal JsonValue Build(out string error)
        {
            error = null;
            var o = JsonValue.Obj();
            for (int i = 0; i < Fields.Count; i++)
            {
                var fd = Fields[i];
                string t = (fd.Text ?? "").Trim();
                switch (fd.Kind)
                {
                    case Kind.Str:
                        if (fd.Text.Length > 0) o.Set(fd.Name, fd.Text);
                        else if (fd.Required) { error = fd.Name + " is required"; return null; }
                        break;
                    case Kind.Num:
                        if (t.Length == 0) { if (fd.Required) { error = fd.Name + " is required"; return null; } break; }
                        if (!UiKit.TryNum(t, out double d)) { error = fd.Name + ": '" + t + "' is not a number"; return null; }
                        o.Set(fd.Name, d);
                        break;
                    case Kind.Int:
                        if (t.Length == 0) { if (fd.Required) { error = fd.Name + " is required"; return null; } break; }
                        if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { error = fd.Name + ": '" + t + "' is not an integer"; return null; }
                        o.Set(fd.Name, l);
                        break;
                    case Kind.Bool:
                        if (fd.BoolState == 1) o.Set(fd.Name, true);
                        else if (fd.BoolState == 2) o.Set(fd.Name, false);
                        else if (fd.Required) { error = fd.Name + " is required"; return null; }
                        break;
                    case Kind.Enum:
                        if (fd.EnumIndex >= 0 && fd.EnumIndex < fd.Enum.Length) o.Set(fd.Name, fd.Enum[fd.EnumIndex]);
                        else if (fd.Required) { error = fd.Name + " is required"; return null; }
                        break;
                    case Kind.List:
                        {
                            var parts = new List<string>();
                            foreach (var p in t.Split(',')) if (p.Trim().Length > 0) parts.Add(p.Trim());
                            if (parts.Count == 0) { if (fd.Required) { error = fd.Name + " is required"; return null; } break; }
                            if (parts.Count == 1 && fd.ListAllowsString) o.Set(fd.Name, parts[0]);
                            else o.Set(fd.Name, JsonValue.ArrOf(parts.ToArray()));
                            break;
                        }
                    default:
                        if (t.Length == 0) { if (fd.Required) { error = fd.Name + " is required"; return null; } break; }
                        try { o.Set(fd.Name, JsonValue.Parse(t)); }
                        catch (Exception e) { error = fd.Name + ": not valid JSON (" + e.Message + ")"; return null; }
                        break;
                }
            }
            return o;
        }

        // ------------------------------------------------------------------ drawing

        internal void Draw(SkizzyUi ui, string formId)
        {
            if (Note != null) GUILayout.Label(Note, UiKit.Grey);
            if (Fields.Count == 0 && Note == null) GUILayout.Label("This capability takes no arguments.", UiKit.Grey);
            for (int i = 0; i < Fields.Count; i++)
            {
                var fd = Fields[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(fd.Name + (fd.Required ? " *" : ""), UiKit.Bold, GUILayout.Width(150));
                DrawInput(ui, formId, fd);
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(fd.Help) || !string.IsNullOrEmpty(fd.Range))
                    GUILayout.Label(UiKit.Clip(fd.Help, 400) + (string.IsNullOrEmpty(fd.Range) ? "" : (string.IsNullOrEmpty(fd.Help) ? "" : "  ") + "[" + fd.Range + "]"), UiKit.Small);
                if (fd.Picking)
                {
                    ui.Vessels.EnsureLoaded();
                    var f = fd;
                    ui.Vessels.DrawPicker(ref _pickScroll, null, row => { f.Text = row.Handle; f.Picking = false; }, 140);
                    UiKit.Btn("Use \"active\"", () => { f.Text = "active"; f.Picking = false; }, GUILayout.Width(110));
                }
                GUILayout.EndVertical();
            }
        }

        private static void DrawInput(SkizzyUi ui, string formId, Field fd)
        {
            string ctl = formId + "." + fd.Name;
            switch (fd.Kind)
            {
                case Kind.Bool:
                    if (!fd.Required) UiKit.Choice(fd.BoolState == 0, "unset", () => fd.BoolState = 0, GUILayout.Width(60));
                    UiKit.Choice(fd.BoolState == 1, "true", () => fd.BoolState = 1, GUILayout.Width(60));
                    UiKit.Choice(fd.BoolState == 2, "false", () => fd.BoolState = 2, GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    break;
                case Kind.Enum:
                    GUILayout.BeginVertical();
                    GUILayout.BeginHorizontal();
                    int perRow = 4, n = 0;
                    if (!fd.Required) { UiKit.Choice(fd.EnumIndex < 0, "unset", () => fd.EnumIndex = -1); n++; }
                    for (int i = 0; i < fd.Enum.Length; i++)
                    {
                        if (n > 0 && n % perRow == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                        int idx = i;
                        UiKit.Choice(fd.EnumIndex == i, fd.Enum[i], () => fd.EnumIndex = idx);
                        n++;
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    break;
                case Kind.Raw:
                    fd.Text = UiKit.Area(ctl, fd.Text, GUILayout.MinHeight(44), GUILayout.ExpandWidth(true));
                    break;
                default:
                    fd.Text = UiKit.Text(ctl, fd.Text, GUILayout.ExpandWidth(true));
                    if (fd.HandleKind == "vessel") UiKit.Choice(fd.Picking, "pick", () => fd.Picking = !fd.Picking, GUILayout.Width(44));
                    break;
            }
        }
    }
}
