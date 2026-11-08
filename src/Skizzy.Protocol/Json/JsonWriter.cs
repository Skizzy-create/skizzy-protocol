using System;

namespace Skizzy.Protocol.Json
{
    /// <summary>
    /// Streaming UTF-8 JSON writer into a <see cref="ByteBuffer"/>. Allocation-free for every primitive,
    /// so stream and log serialisation on network/writer threads produces no garbage (02 §2.3).
    /// Not thread-safe; one writer per thread (reuse via <see cref="Reset"/>).
    /// </summary>
    public sealed class JsonWriter
    {
        private const int MaxDepth = 128;
        private static readonly byte[] Hex = { (byte)'0', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9', (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f' };
        private static readonly byte[] B64 = System.Text.Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

        private readonly bool[] _needComma = new bool[MaxDepth];
        private int _depth;
        private bool _afterName;
        private bool _pretty;
        public ByteBuffer Buffer { get; private set; }

        public JsonWriter(ByteBuffer buffer, bool pretty = false)
        {
            Buffer = buffer;
            _pretty = pretty;
        }

        public void Reset(ByteBuffer buffer, bool pretty = false)
        {
            Buffer = buffer;
            _depth = 0;
            _afterName = false;
            _pretty = pretty;
            _needComma[0] = false;
        }

        public int Depth => _depth;

        // skizzy:hot JsonWriter.Separator
        private void BeforeValue()
        {
            if (_afterName) { _afterName = false; return; }
            if (_needComma[_depth]) Buffer.WriteByte((byte)',');
            if (_pretty && _depth > 0) NewLine();
            _needComma[_depth] = true;
        }

        private void NewLine()
        {
            Buffer.WriteByte((byte)'\n');
            for (int i = 0; i < _depth; i++) { Buffer.WriteByte((byte)' '); Buffer.WriteByte((byte)' '); }
        }

        public JsonWriter BeginObject()
        {
            BeforeValue();
            Buffer.WriteByte((byte)'{');
            Push();
            return this;
        }

        public JsonWriter EndObject()
        {
            bool had = _needComma[_depth];
            Pop();
            if (_pretty && had) NewLine();
            Buffer.WriteByte((byte)'}');
            return this;
        }

        public JsonWriter BeginArray()
        {
            BeforeValue();
            Buffer.WriteByte((byte)'[');
            Push();
            return this;
        }

        public JsonWriter EndArray()
        {
            bool had = _needComma[_depth];
            Pop();
            if (_pretty && had) NewLine();
            Buffer.WriteByte((byte)']');
            return this;
        }

        private void Push()
        {
            if (++_depth >= MaxDepth) throw new InvalidOperationException("JSON nesting too deep");
            _needComma[_depth] = false;
        }

        private void Pop()
        {
            if (_depth == 0) throw new InvalidOperationException("Unbalanced JSON writer");
            _depth--;
        }

        public JsonWriter Name(string name)
        {
            BeforeValue();
            WriteQuoted(name);
            Buffer.WriteByte((byte)':');
            if (_pretty) Buffer.WriteByte((byte)' ');
            _afterName = true;
            return this;
        }

        public JsonWriter String(string s)
        {
            BeforeValue();
            if (s == null) Buffer.WriteAscii("null");
            else WriteQuoted(s);
            return this;
        }

        public JsonWriter Number(long v)
        {
            BeforeValue();
            NumberFormat.WriteLong(Buffer, v);
            return this;
        }

        public JsonWriter Number(int v) { return Number((long)v); }

        /// <summary>Finite doubles as numbers; NaN/Infinity as null.</summary>
        public JsonWriter Number(double v)
        {
            BeforeValue();
            if (NumberFormat.IsFinite(v)) NumberFormat.WriteDouble(Buffer, v);
            else Buffer.WriteAscii("null");
            return this;
        }

        public JsonWriter Single(float v)
        {
            BeforeValue();
            if (NumberFormat.IsFinite(v)) NumberFormat.WriteSingle(Buffer, v);
            else Buffer.WriteAscii("null");
            return this;
        }

        public JsonWriter Bool(bool v)
        {
            BeforeValue();
            Buffer.WriteAscii(v ? "true" : "false");
            return this;
        }

        public JsonWriter Null()
        {
            BeforeValue();
            Buffer.WriteAscii("null");
            return this;
        }

        /// <summary>Writes a pre-encoded JSON fragment as one value.</summary>
        public JsonWriter Raw(byte[] json, int offset, int count)
        {
            BeforeValue();
            Buffer.WriteBytes(json, offset, count);
            return this;
        }

        public JsonWriter Base64(byte[] data, int offset, int count)
        {
            BeforeValue();
            Buffer.WriteByte((byte)'"');
            WriteBase64Body(Buffer, data, offset, count);
            Buffer.WriteByte((byte)'"');
            return this;
        }

        // ---- property helpers ----
        public JsonWriter Prop(string name, string v) { Name(name); return String(v); }
        public JsonWriter Prop(string name, long v) { Name(name); return Number(v); }
        public JsonWriter Prop(string name, int v) { Name(name); return Number((long)v); }
        public JsonWriter Prop(string name, bool v) { Name(name); return Bool(v); }

        /// <summary>Writes a double property; NaN/Infinity become null plus "name$nonFinite" (spec 0.2 §9).</summary>
        public JsonWriter Prop(string name, double v)
        {
            Name(name);
            if (NumberFormat.IsFinite(v)) return Number(v);
            Null();
            NameWithSuffix(name, "$nonFinite");
            return String(double.IsNaN(v) ? "NaN" : v > 0 ? "+Inf" : "-Inf");
        }

        /// <summary>Writes the property name <paramref name="name"/> + <paramref name="asciiSuffix"/> without allocating.</summary>
        public JsonWriter NameWithSuffix(string name, string asciiSuffix)
        {
            BeforeValue();
            WriteQuoted(name);
            Buffer.Length--; // drop the closing quote
            Buffer.WriteAscii(asciiSuffix);
            Buffer.WriteByte((byte)'"');
            Buffer.WriteByte((byte)':');
            if (_pretty) Buffer.WriteByte((byte)' ');
            _afterName = true;
            return this;
        }

        // skizzy:hot JsonWriter.WriteQuoted
        private void WriteQuoted(string s)
        {
            var b = Buffer;
            int n = s.Length;
            b.Ensure(n + 2);
            b.Data[b.Length++] = (byte)'"';
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (c >= 0x20 && c < 0x80 && c != '"' && c != '\\')
                {
                    if (b.Length == b.Data.Length) b.Ensure(1);
                    b.Data[b.Length++] = (byte)c;
                    continue;
                }
                switch (c)
                {
                    case '"': Two(b, (byte)'"'); continue;
                    case '\\': Two(b, (byte)'\\'); continue;
                    case '\n': Two(b, (byte)'n'); continue;
                    case '\r': Two(b, (byte)'r'); continue;
                    case '\t': Two(b, (byte)'t'); continue;
                    case '\b': Two(b, (byte)'b'); continue;
                    case '\f': Two(b, (byte)'f'); continue;
                }
                if (c < 0x20)
                {
                    b.Ensure(6);
                    var d = b.Data;
                    int p = b.Length;
                    d[p] = (byte)'\\'; d[p + 1] = (byte)'u'; d[p + 2] = (byte)'0'; d[p + 3] = (byte)'0';
                    d[p + 4] = Hex[c >> 4]; d[p + 5] = Hex[c & 0xF];
                    b.Length = p + 6;
                    continue;
                }
                // non-ASCII: UTF-8
                b.Ensure(4);
                var dd = b.Data;
                int q = b.Length;
                if (c < 0x800)
                {
                    dd[q++] = (byte)(0xC0 | (c >> 6));
                    dd[q++] = (byte)(0x80 | (c & 0x3F));
                }
                else if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                    dd[q++] = (byte)(0xF0 | (cp >> 18));
                    dd[q++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                    dd[q++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                    dd[q++] = (byte)(0x80 | (cp & 0x3F));
                }
                else
                {
                    if (char.IsSurrogate(c)) c = '\uFFFD';
                    dd[q++] = (byte)(0xE0 | (c >> 12));
                    dd[q++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    dd[q++] = (byte)(0x80 | (c & 0x3F));
                }
                b.Length = q;
            }
            if (b.Length == b.Data.Length) b.Ensure(1);
            b.Data[b.Length++] = (byte)'"';
        }

        private static void Two(ByteBuffer b, byte second)
        {
            b.Ensure(2);
            b.Data[b.Length++] = (byte)'\\';
            b.Data[b.Length++] = second;
        }

        public static void WriteBase64Body(ByteBuffer b, byte[] data, int offset, int count)
        {
            int outLen = (count + 2) / 3 * 4;
            b.Ensure(outLen);
            var d = b.Data;
            int p = b.Length;
            int i = offset, end = offset + count;
            while (end - i >= 3)
            {
                int v = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
                d[p++] = B64[(v >> 18) & 63];
                d[p++] = B64[(v >> 12) & 63];
                d[p++] = B64[(v >> 6) & 63];
                d[p++] = B64[v & 63];
                i += 3;
            }
            int rem = end - i;
            if (rem == 1)
            {
                int v = data[i] << 16;
                d[p++] = B64[(v >> 18) & 63];
                d[p++] = B64[(v >> 12) & 63];
                d[p++] = (byte)'=';
                d[p++] = (byte)'=';
            }
            else if (rem == 2)
            {
                int v = (data[i] << 16) | (data[i + 1] << 8);
                d[p++] = B64[(v >> 18) & 63];
                d[p++] = B64[(v >> 12) & 63];
                d[p++] = B64[(v >> 6) & 63];
                d[p++] = (byte)'=';
            }
            b.Length = p;
        }

        // ---- DOM ----
        public JsonWriter Value(JsonValue v)
        {
            if (v == null) return Null();
            switch (v.Kind)
            {
                case JsonKind.Null: return Null();
                case JsonKind.Bool: return Bool(v.AsBool());
                case JsonKind.Number:
                    if (v.NumberIsInt) return Number(v.RawLong);
                    return Number(v.RawDouble);
                case JsonKind.String: return String(v.AsString());
                case JsonKind.Bytes: return Base64(v.BytesData, v.BytesOffset, v.BytesLength);
                case JsonKind.Array:
                    BeginArray();
                    for (int i = 0; i < v.Count; i++) Value(v[i]);
                    return EndArray();
                case JsonKind.Object:
                    BeginObject();
                    for (int i = 0; i < v.Count; i++)
                    {
                        Name(v.KeyAt(i));
                        Value(v.ValueAt(i));
                    }
                    return EndObject();
            }
            return Null();
        }

        public static string Serialize(JsonValue v, bool pretty = false)
        {
            var buf = ByteBufferPool.Rent(256);
            try
            {
                var w = new JsonWriter(buf, pretty);
                w.Value(v);
                return buf.ToUtf8String();
            }
            finally
            {
                ByteBufferPool.Return(buf);
            }
        }

        public static byte[] SerializeToBytes(JsonValue v)
        {
            var buf = ByteBufferPool.Rent(256);
            try
            {
                new JsonWriter(buf).Value(v);
                return buf.ToArray();
            }
            finally
            {
                ByteBufferPool.Return(buf);
            }
        }
    }
}
