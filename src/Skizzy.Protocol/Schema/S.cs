using Skizzy.Protocol.Json;

namespace Skizzy.Protocol.Schema
{
    /// <summary>
    /// Fluent builder for the JSON Schema 2020-12 subset the host validates (<see cref="SchemaValidator"/>).
    /// Capability input schemas are written once with this builder and shared by the host (validation),
    /// the bridge (MCP tool schemas), the in-game console (generated forms) and spec/schemas (docs).
    /// </summary>
    public sealed class SchemaB
    {
        public readonly JsonValue J = JsonValue.Obj();
        private JsonValue _props;
        private JsonValue _required;

        internal SchemaB(string type)
        {
            if (type != null) J.Set("type", type);
        }

        public SchemaB Desc(string d) { J.Set("description", d); return this; }
        public SchemaB Title(string t) { J.Set("title", t); return this; }
        public SchemaB Default(JsonValue v) { J.Set("default", v); return this; }
        public SchemaB Default(string v) { return Default(JsonValue.Str(v)); }
        public SchemaB Default(double v) { return Default(JsonValue.Num(v)); }
        public SchemaB Default(long v) { return Default(JsonValue.Num(v)); }
        public SchemaB Default(int v) { return Default(JsonValue.Num(v)); }
        public SchemaB Default(bool v) { return Default(JsonValue.Bool(v)); }
        public SchemaB Min(double v) { J.Set("minimum", v); return this; }
        public SchemaB Max(double v) { J.Set("maximum", v); return this; }
        public SchemaB ExclusiveMin(double v) { J.Set("exclusiveMinimum", v); return this; }
        public SchemaB MinLen(int v) { J.Set("minLength", v); return this; }
        public SchemaB MaxLen(int v) { J.Set("maxLength", v); return this; }
        public SchemaB Pattern(string p) { J.Set("pattern", p); return this; }
        public SchemaB MinItems(int v) { J.Set("minItems", v); return this; }
        public SchemaB MaxItems(int v) { J.Set("maxItems", v); return this; }
        public SchemaB Items(SchemaB item) { J.Set("items", item.J); return this; }
        public SchemaB Unit(string unit) { J.Set("x-skz-unit", unit); return this; }
        /// <summary>Marks a handle-typed property: "vessel" | "part" | "module" (spec 0.2 §7.2). Accepts names too.</summary>
        public SchemaB Handle(string kind) { J.Set("x-skz-handle", kind); return this; }
        public SchemaB Extra(string key, JsonValue v) { J.Set(key, v); return this; }
        public SchemaB Examples(params string[] ex) { J.Set("examples", JsonValue.ArrOf(ex)); return this; }

        public SchemaB Prop(string name, SchemaB schema, bool required = false)
        {
            if (_props == null)
            {
                _props = JsonValue.Obj();
                J.Set("properties", _props);
            }
            _props.Set(name, schema.J);
            if (required)
            {
                if (_required == null)
                {
                    _required = JsonValue.Arr();
                    J.Set("required", _required);
                }
                _required.Add(name);
            }
            return this;
        }

        public SchemaB Closed() { J.Set("additionalProperties", false); return this; }
        public SchemaB Open() { J.Set("additionalProperties", true); return this; }

        public JsonValue Build() { return J; }
    }

    public static class S
    {
        public static SchemaB Obj(string desc = null)
        {
            var s = new SchemaB("object");
            if (desc != null) s.Desc(desc);
            return s;
        }

        public static SchemaB Str(string desc = null) { var s = new SchemaB("string"); if (desc != null) s.Desc(desc); return s; }
        public static SchemaB Num(string desc = null) { var s = new SchemaB("number"); if (desc != null) s.Desc(desc); return s; }
        public static SchemaB Int(string desc = null) { var s = new SchemaB("integer"); if (desc != null) s.Desc(desc); return s; }
        public static SchemaB Bool(string desc = null) { var s = new SchemaB("boolean"); if (desc != null) s.Desc(desc); return s; }
        public static SchemaB Arr(SchemaB items, string desc = null) { var s = new SchemaB("array").Items(items); if (desc != null) s.Desc(desc); return s; }

        public static SchemaB Enum(string desc, params string[] values)
        {
            var s = new SchemaB("string");
            s.J.Set("enum", JsonValue.ArrOf(values));
            if (desc != null) s.Desc(desc);
            return s;
        }

        /// <summary>Accepts any JSON value (used for free-form selectors and generic args).</summary>
        public static SchemaB Any(string desc = null)
        {
            var s = new SchemaB(null);
            if (desc != null) s.Desc(desc);
            return s;
        }

        /// <summary>A string or an array of strings (e.g. one module type or several).</summary>
        public static SchemaB StrOrList(string desc = null)
        {
            var s = new SchemaB(null);
            s.J.Set("type", JsonValue.ArrOf("string", "array"));
            s.J.Set("items", JsonValue.Obj().Set("type", "string"));
            if (desc != null) s.Desc(desc);
            return s;
        }

        public static SchemaB OneOf(string desc, params SchemaB[] options)
        {
            var s = new SchemaB(null);
            var arr = JsonValue.Arr();
            for (int i = 0; i < options.Length; i++) arr.Add(options[i].J);
            s.J.Set("oneOf", arr);
            if (desc != null) s.Desc(desc);
            return s;
        }
    }
}
