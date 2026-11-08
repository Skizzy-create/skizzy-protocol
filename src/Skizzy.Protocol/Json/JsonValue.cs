using System;
using System.Collections.Generic;
using System.Globalization;

namespace Skizzy.Protocol.Json
{
    public enum JsonKind : byte
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
        /// <summary>Raw bytes, written as a base64 JSON string (images, file chunks). Never produced by the parser.</summary>
        Bytes,
    }

    /// <summary>
    /// Small JSON DOM shared by host and clients (no Newtonsoft or System.Text.Json in game; PLAN §10.3).
    /// Objects keep insertion order so output is deterministic. Used for requests and results (cold paths);
    /// hot paths write directly with <see cref="JsonWriter"/>.
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);
        public static readonly JsonValue True = new JsonValue(JsonKind.Bool) { _bool = true };
        public static readonly JsonValue False = new JsonValue(JsonKind.Bool) { _bool = false };

        private readonly JsonKind _kind;
        private bool _bool;
        private bool _isInt;
        private long _long;
        private double _num;
        private string _str;
        private List<JsonValue> _items;
        private List<string> _keys;
        private Dictionary<string, int> _index;
        private byte[] _bytes;
        private int _bytesOffset;
        private int _bytesLength;

        private JsonValue(JsonKind kind) { _kind = kind; }

        public JsonKind Kind => _kind;
        public bool IsNull => _kind == JsonKind.Null;
        public bool IsObject => _kind == JsonKind.Object;
        public bool IsArray => _kind == JsonKind.Array;
        public bool IsString => _kind == JsonKind.String;
        public bool IsNumber => _kind == JsonKind.Number;
        public bool IsBool => _kind == JsonKind.Bool;
        public bool IsInteger => _kind == JsonKind.Number && (_isInt || (_num == Math.Floor(_num) && !double.IsInfinity(_num)));

        // ---- factories ----
        public static JsonValue Obj() { return new JsonValue(JsonKind.Object) { _keys = new List<string>(), _items = new List<JsonValue>() }; }
        public static JsonValue Arr() { return new JsonValue(JsonKind.Array) { _items = new List<JsonValue>() }; }
        public static JsonValue Str(string s) { return s == null ? Null : new JsonValue(JsonKind.String) { _str = s }; }
        public static JsonValue Num(double d) { return new JsonValue(JsonKind.Number) { _num = d }; }
        public static JsonValue Num(long l) { return new JsonValue(JsonKind.Number) { _long = l, _num = l, _isInt = true }; }
        public static JsonValue Num(int i) { return Num((long)i); }
        public static JsonValue Bool(bool b) { return b ? True : False; }
        public static JsonValue Bytes(byte[] data) { return Bytes(data, 0, data == null ? 0 : data.Length); }
        public static JsonValue Bytes(byte[] data, int offset, int length)
        {
            return new JsonValue(JsonKind.Bytes) { _bytes = data ?? new byte[0], _bytesOffset = offset, _bytesLength = length };
        }

        public static JsonValue ArrOf(params string[] values)
        {
            var a = Arr();
            if (values != null) for (int i = 0; i < values.Length; i++) a.Add(values[i]);
            return a;
        }

        // ---- scalar access ----
        public bool AsBool(bool def = false) { return _kind == JsonKind.Bool ? _bool : def; }
        public double AsDouble(double def = 0) { return _kind == JsonKind.Number ? (_isInt ? _long : _num) : def; }
        public long AsLong(long def = 0)
        {
            if (_kind != JsonKind.Number) return def;
            if (_isInt) return _long;
            if (_num >= long.MinValue && _num <= long.MaxValue) return (long)_num;
            return def;
        }
        public int AsInt(int def = 0)
        {
            long l = AsLong(def);
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }
        public string AsString(string def = null) { return _kind == JsonKind.String ? _str : def; }
        internal bool NumberIsInt => _isInt;
        internal long RawLong => _long;
        internal double RawDouble => _num;

        public byte[] BytesData => _bytes;
        public int BytesOffset => _bytesOffset;
        public int BytesLength => _bytesLength;

        // ---- containers ----
        public int Count => _kind == JsonKind.Object ? _keys.Count : _kind == JsonKind.Array ? _items.Count : 0;

        public JsonValue this[int index] => _items[index];

        public string KeyAt(int index) { return _keys[index]; }
        public JsonValue ValueAt(int index) { return _items[index]; }

        public IReadOnlyList<JsonValue> Items => _items;
        public IReadOnlyList<string> Keys => _keys;

        public JsonValue Add(JsonValue v)
        {
            RequireArray();
            _items.Add(v ?? Null);
            return this;
        }
        public JsonValue Add(string s) { return Add(Str(s)); }
        public JsonValue Add(double d) { return Add(Num(d)); }
        public JsonValue Add(long l) { return Add(Num(l)); }
        public JsonValue Add(int i) { return Add(Num(i)); }
        public JsonValue Add(bool b) { return Add(Bool(b)); }

        public JsonValue Set(string key, JsonValue v)
        {
            RequireObject();
            if (v == null) v = Null;
            int i = IndexOf(key);
            if (i >= 0) { _items[i] = v; return this; }
            _keys.Add(key);
            _items.Add(v);
            if (_index != null) _index[key] = _keys.Count - 1;
            else if (_keys.Count > 8) BuildIndex();
            return this;
        }
        public JsonValue Set(string key, string s) { return Set(key, Str(s)); }
        public JsonValue Set(string key, double d) { return Set(key, Num(d)); }
        public JsonValue Set(string key, long l) { return Set(key, Num(l)); }
        public JsonValue Set(string key, int i) { return Set(key, Num(i)); }
        public JsonValue Set(string key, uint i) { return Set(key, Num((long)i)); }
        public JsonValue Set(string key, bool b) { return Set(key, Bool(b)); }

        /// <summary>Sets a number, writing null plus "key$nonFinite" for NaN/Infinity (spec 0.2 §9).</summary>
        public JsonValue SetFinite(string key, double d)
        {
            if (NumberFormat.IsFinite(d)) return Set(key, Num(d));
            Set(key, Null);
            return Set(key + "$nonFinite", double.IsNaN(d) ? "NaN" : d > 0 ? "+Inf" : "-Inf");
        }

        public bool Remove(string key)
        {
            RequireObject();
            int i = IndexOf(key);
            if (i < 0) return false;
            _keys.RemoveAt(i);
            _items.RemoveAt(i);
            if (_index != null) BuildIndex();
            return true;
        }

        public bool Has(string key) { return _kind == JsonKind.Object && IndexOf(key) >= 0; }

        /// <summary>Returns the member or C# null when absent (a JSON null member returns <see cref="Null"/>).</summary>
        public JsonValue Get(string key)
        {
            if (_kind != JsonKind.Object) return null;
            int i = IndexOf(key);
            return i < 0 ? null : _items[i];
        }

        public string GetString(string key, string def = null) { var v = Get(key); return v != null && v._kind == JsonKind.String ? v._str : def; }
        public double GetDouble(string key, double def = 0) { var v = Get(key); return v != null && v._kind == JsonKind.Number ? v.AsDouble() : def; }
        public long GetLong(string key, long def = 0) { var v = Get(key); return v != null && v._kind == JsonKind.Number ? v.AsLong(def) : def; }
        public int GetInt(string key, int def = 0) { var v = Get(key); return v != null && v._kind == JsonKind.Number ? v.AsInt(def) : def; }
        public bool GetBool(string key, bool def = false) { var v = Get(key); return v != null && v._kind == JsonKind.Bool ? v._bool : def; }
        public JsonValue GetObj(string key) { var v = Get(key); return v != null && v._kind == JsonKind.Object ? v : null; }
        public JsonValue GetArr(string key) { var v = Get(key); return v != null && v._kind == JsonKind.Array ? v : null; }

        /// <summary>Reads a string array member; a single string is accepted as a one-element array.</summary>
        public List<string> GetStringList(string key)
        {
            var v = Get(key);
            if (v == null || v.IsNull) return null;
            var r = new List<string>();
            if (v._kind == JsonKind.String) { r.Add(v._str); return r; }
            if (v._kind != JsonKind.Array) return r;
            for (int i = 0; i < v._items.Count; i++)
            {
                var s = v._items[i].AsString();
                if (s != null) r.Add(s);
            }
            return r;
        }

        private int IndexOf(string key)
        {
            if (_index != null) return _index.TryGetValue(key, out int i) ? i : -1;
            var keys = _keys;
            for (int i = 0; i < keys.Count; i++) if (string.Equals(keys[i], key, StringComparison.Ordinal)) return i;
            return -1;
        }

        private void BuildIndex()
        {
            _index = new Dictionary<string, int>(_keys.Count * 2, StringComparer.Ordinal);
            for (int i = 0; i < _keys.Count; i++) _index[_keys[i]] = i;
        }

        private void RequireObject()
        {
            if (_kind != JsonKind.Object) throw new InvalidOperationException("JSON value is not an object");
        }

        private void RequireArray()
        {
            if (_kind != JsonKind.Array) throw new InvalidOperationException("JSON value is not an array");
        }

        public JsonValue Clone()
        {
            switch (_kind)
            {
                case JsonKind.Object:
                    var o = Obj();
                    for (int i = 0; i < _keys.Count; i++) o.Set(_keys[i], _items[i].Clone());
                    return o;
                case JsonKind.Array:
                    var a = Arr();
                    for (int i = 0; i < _items.Count; i++) a.Add(_items[i].Clone());
                    return a;
                default:
                    return this; // scalars are immutable
            }
        }

        public static bool DeepEquals(JsonValue a, JsonValue b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a._kind != b._kind) return false;
            switch (a._kind)
            {
                case JsonKind.Null: return true;
                case JsonKind.Bool: return a._bool == b._bool;
                case JsonKind.Number: return a.AsDouble() == b.AsDouble();
                case JsonKind.String: return a._str == b._str;
                case JsonKind.Bytes:
                    if (a._bytesLength != b._bytesLength) return false;
                    for (int i = 0; i < a._bytesLength; i++) if (a._bytes[a._bytesOffset + i] != b._bytes[b._bytesOffset + i]) return false;
                    return true;
                case JsonKind.Array:
                    if (a._items.Count != b._items.Count) return false;
                    for (int i = 0; i < a._items.Count; i++) if (!DeepEquals(a._items[i], b._items[i])) return false;
                    return true;
                case JsonKind.Object:
                    if (a._keys.Count != b._keys.Count) return false;
                    for (int i = 0; i < a._keys.Count; i++)
                    {
                        var bv = b.Get(a._keys[i]);
                        if (bv == null || !DeepEquals(a._items[i], bv)) return false;
                    }
                    return true;
            }
            return false;
        }

        public override string ToString()
        {
            return JsonWriter.Serialize(this);
        }

        public string ToPrettyString()
        {
            return JsonWriter.Serialize(this, true);
        }

        public static JsonValue Parse(string text)
        {
            return JsonParser.Parse(text);
        }

        internal string DebugNumber()
        {
            return _isInt ? _long.ToString(CultureInfo.InvariantCulture) : _num.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
