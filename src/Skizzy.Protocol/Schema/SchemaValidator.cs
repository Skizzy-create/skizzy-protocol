using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Skizzy.Protocol.Json;

namespace Skizzy.Protocol.Schema
{
    public struct SchemaError
    {
        public string Pointer;
        public string Message;
        public SchemaError(string pointer, string message) { Pointer = pointer; Message = message; }
        public override string ToString() { return (string.IsNullOrEmpty(Pointer) ? "/" : Pointer) + ": " + Message; }
    }

    /// <summary>
    /// Validates instances against the JSON Schema 2020-12 subset produced by <see cref="S"/>:
    /// type, enum, const, minimum/maximum (+exclusive), minLength/maxLength, pattern, properties,
    /// required, additionalProperties, items, minItems/maxItems, oneOf, anyOf. Unknown keywords are ignored.
    /// Errors carry JSON pointers (spec 0.2 §6.3 step 3).
    /// </summary>
    public static class SchemaValidator
    {
        private static readonly Dictionary<string, Regex> RegexCache = new Dictionary<string, Regex>();
        private static readonly object RegexGate = new object();

        public static List<SchemaError> Validate(JsonValue instance, JsonValue schema, int maxErrors = 16)
        {
            var errors = new List<SchemaError>();
            Check(instance, schema, "", errors, maxErrors);
            return errors;
        }

        public static JsonValue ErrorsToJson(List<SchemaError> errors)
        {
            var a = JsonValue.Arr();
            foreach (var e in errors) a.Add(JsonValue.Obj().Set("pointer", e.Pointer).Set("message", e.Message));
            return a;
        }

        private static bool Check(JsonValue v, JsonValue s, string ptr, List<SchemaError> errs, int max)
        {
            if (s == null || !s.IsObject) return true;
            int before = errs.Count;

            var type = s.Get("type");
            if (type != null && !TypeMatches(v, type))
            {
                Add(errs, max, ptr, "expected " + TypeText(type) + ", got " + KindText(v));
                return false;
            }

            var en = s.GetArr("enum");
            if (en != null)
            {
                bool found = false;
                for (int i = 0; i < en.Count; i++) if (JsonValue.DeepEquals(en[i], v)) { found = true; break; }
                if (!found) Add(errs, max, ptr, "must be one of " + EnumText(en));
            }
            var cn = s.Get("const");
            if (cn != null && !JsonValue.DeepEquals(cn, v)) Add(errs, max, ptr, "must equal " + cn);

            if (v.IsNumber)
            {
                double d = v.AsDouble();
                var mn = s.Get("minimum");
                if (mn != null && d < mn.AsDouble()) Add(errs, max, ptr, "must be >= " + Fmt(mn.AsDouble()));
                var mx = s.Get("maximum");
                if (mx != null && d > mx.AsDouble()) Add(errs, max, ptr, "must be <= " + Fmt(mx.AsDouble()));
                var emn = s.Get("exclusiveMinimum");
                if (emn != null && d <= emn.AsDouble()) Add(errs, max, ptr, "must be > " + Fmt(emn.AsDouble()));
                var emx = s.Get("exclusiveMaximum");
                if (emx != null && d >= emx.AsDouble()) Add(errs, max, ptr, "must be < " + Fmt(emx.AsDouble()));
            }
            else if (v.IsString)
            {
                string str = v.AsString();
                var mnl = s.Get("minLength");
                if (mnl != null && str.Length < mnl.AsInt()) Add(errs, max, ptr, "must be at least " + mnl.AsInt() + " characters");
                var mxl = s.Get("maxLength");
                if (mxl != null && str.Length > mxl.AsInt()) Add(errs, max, ptr, "must be at most " + mxl.AsInt() + " characters");
                var pat = s.GetString("pattern");
                if (pat != null && !GetRegex(pat).IsMatch(str)) Add(errs, max, ptr, "must match pattern " + pat);
            }
            else if (v.IsArray)
            {
                var mni = s.Get("minItems");
                if (mni != null && v.Count < mni.AsInt()) Add(errs, max, ptr, "must have at least " + mni.AsInt() + " items");
                var mxi = s.Get("maxItems");
                if (mxi != null && v.Count > mxi.AsInt()) Add(errs, max, ptr, "must have at most " + mxi.AsInt() + " items");
                var items = s.GetObj("items");
                if (items != null)
                    for (int i = 0; i < v.Count && errs.Count < max; i++) Check(v[i], items, ptr + "/" + i, errs, max);
            }
            else if (v.IsObject)
            {
                var req = s.GetArr("required");
                if (req != null)
                    for (int i = 0; i < req.Count; i++)
                    {
                        string name = req[i].AsString();
                        var mv = v.Get(name);
                        if (mv == null) Add(errs, max, ptr + "/" + Escape(name), "is required");
                    }
                var props = s.GetObj("properties");
                var addl = s.Get("additionalProperties");
                for (int i = 0; i < v.Count && errs.Count < max; i++)
                {
                    string k = v.KeyAt(i);
                    var ps = props?.Get(k);
                    if (ps != null)
                    {
                        Check(v.ValueAt(i), ps, ptr + "/" + Escape(k), errs, max);
                    }
                    else if (addl != null)
                    {
                        if (addl.IsBool && !addl.AsBool())
                        {
                            string hint = props != null ? " (known: " + KeysText(props) + ")" : "";
                            Add(errs, max, ptr + "/" + Escape(k), "unknown property" + hint);
                        }
                        else if (addl.IsObject) Check(v.ValueAt(i), addl, ptr + "/" + Escape(k), errs, max);
                    }
                }
            }

            var oneOf = s.GetArr("oneOf");
            if (oneOf != null)
            {
                int matches = 0;
                for (int i = 0; i < oneOf.Count; i++)
                {
                    var tmp = new List<SchemaError>();
                    if (Check(v, oneOf[i], ptr, tmp, 4) && tmp.Count == 0) matches++;
                }
                if (matches != 1) Add(errs, max, ptr, matches == 0 ? "does not match any allowed form" : "matches more than one allowed form");
            }
            var anyOf = s.GetArr("anyOf");
            if (anyOf != null)
            {
                bool any = false;
                for (int i = 0; i < anyOf.Count && !any; i++)
                {
                    var tmp = new List<SchemaError>();
                    if (Check(v, anyOf[i], ptr, tmp, 4) && tmp.Count == 0) any = true;
                }
                if (!any) Add(errs, max, ptr, "does not match any allowed form");
            }
            return errs.Count == before;
        }

        /// <summary>Fills missing object properties that declare a "default" (recursively for nested objects present).</summary>
        public static JsonValue ApplyDefaults(JsonValue instance, JsonValue schema)
        {
            if (schema == null || !schema.IsObject) return instance;
            if (instance == null || instance.IsNull)
            {
                if (schema.GetString("type") == "object") instance = JsonValue.Obj();
                else return instance;
            }
            if (!instance.IsObject) return instance;
            var props = schema.GetObj("properties");
            if (props == null) return instance;
            for (int i = 0; i < props.Count; i++)
            {
                string k = props.KeyAt(i);
                var ps = props.ValueAt(i);
                var cur = instance.Get(k);
                if (cur == null)
                {
                    var def = ps.Get("default");
                    if (def != null) instance.Set(k, def.Clone());
                }
                else if (cur.IsObject && ps.GetObj("properties") != null)
                {
                    ApplyDefaults(cur, ps);
                }
            }
            return instance;
        }

        private static bool TypeMatches(JsonValue v, JsonValue type)
        {
            if (type.IsString) return One(v, type.AsString());
            if (type.IsArray)
            {
                for (int i = 0; i < type.Count; i++) if (One(v, type[i].AsString())) return true;
                return false;
            }
            return true;
        }

        private static bool One(JsonValue v, string t)
        {
            switch (t)
            {
                case "object": return v.IsObject;
                case "array": return v.IsArray;
                case "string": return v.IsString;
                case "number": return v.IsNumber;
                case "integer": return v.IsNumber && v.IsInteger;
                case "boolean": return v.IsBool;
                case "null": return v.IsNull;
            }
            return true;
        }

        private static string TypeText(JsonValue type)
        {
            if (type.IsString) return type.AsString();
            var parts = new List<string>();
            for (int i = 0; i < type.Count; i++) parts.Add(type[i].AsString());
            return string.Join(" or ", parts.ToArray());
        }

        private static string KindText(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Object: return "object";
                case JsonKind.Array: return "array";
                case JsonKind.String: return "string";
                case JsonKind.Number: return v.IsInteger ? "integer" : "number";
                case JsonKind.Bool: return "boolean";
                default: return "null";
            }
        }

        private static string EnumText(JsonValue en)
        {
            var parts = new List<string>();
            for (int i = 0; i < en.Count && i < 24; i++) parts.Add(en[i].ToString());
            return "[" + string.Join(", ", parts.ToArray()) + (en.Count > 24 ? ", ..." : "") + "]";
        }

        private static string KeysText(JsonValue props)
        {
            var parts = new List<string>();
            for (int i = 0; i < props.Count && i < 20; i++) parts.Add(props.KeyAt(i));
            return string.Join(", ", parts.ToArray());
        }

        private static string Fmt(double d)
        {
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Escape(string key)
        {
            return key.Replace("~", "~0").Replace("/", "~1");
        }

        private static void Add(List<SchemaError> errs, int max, string ptr, string msg)
        {
            if (errs.Count < max) errs.Add(new SchemaError(ptr, msg));
        }

        private static Regex GetRegex(string pattern)
        {
            lock (RegexGate)
            {
                if (!RegexCache.TryGetValue(pattern, out var r))
                {
                    r = new Regex(pattern, RegexOptions.CultureInvariant);
                    RegexCache[pattern] = r;
                }
                return r;
            }
        }
    }
}
