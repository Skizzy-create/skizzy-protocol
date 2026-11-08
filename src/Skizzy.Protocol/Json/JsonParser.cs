using System;
using System.Globalization;
using System.Text;

namespace Skizzy.Protocol.Json
{
    public sealed class JsonParseException : Exception
    {
        public int Offset { get; }
        public JsonParseException(string message, int offset) : base(message + " at byte " + offset) { Offset = offset; }
    }

    /// <summary>
    /// Strict RFC 8259 parser over UTF-8 bytes with size and depth caps (spec 0.2 §3.3: maxJsonDepth 64).
    /// Invalid UTF-8 is a parse error. Runs on reader threads; allocates the DOM (cold path, once per request).
    /// </summary>
    public sealed class JsonParser
    {
        public const int DefaultMaxDepth = 64;

        private readonly byte[] _d;
        private readonly int _end;
        private readonly int _maxDepth;
        private int _p;
        private StringBuilder _sb;

        private JsonParser(byte[] data, int offset, int count, int maxDepth)
        {
            _d = data;
            _p = offset;
            _end = offset + count;
            _maxDepth = maxDepth;
        }

        public static JsonValue Parse(string text, int maxDepth = DefaultMaxDepth)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            return Parse(bytes, 0, bytes.Length, maxDepth);
        }

        public static JsonValue Parse(byte[] data, int offset, int count, int maxDepth = DefaultMaxDepth)
        {
            var p = new JsonParser(data, offset, count, maxDepth);
            // Skip a UTF-8 BOM if present.
            if (count >= 3 && data[offset] == 0xEF && data[offset + 1] == 0xBB && data[offset + 2] == 0xBF) p._p += 3;
            p.SkipWs();
            var v = p.ParseValue(0);
            p.SkipWs();
            if (p._p != p._end) throw new JsonParseException("Unexpected trailing data", p._p);
            return v;
        }

        private void SkipWs()
        {
            while (_p < _end)
            {
                byte c = _d[_p];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _p++;
                else break;
            }
        }

        private JsonValue ParseValue(int depth)
        {
            if (_p >= _end) throw new JsonParseException("Unexpected end of input", _p);
            byte c = _d[_p];
            switch (c)
            {
                case (byte)'{': return ParseObject(depth + 1);
                case (byte)'[': return ParseArray(depth + 1);
                case (byte)'"': return JsonValue.Str(ParseString());
                case (byte)'t': Expect("true"); return JsonValue.True;
                case (byte)'f': Expect("false"); return JsonValue.False;
                case (byte)'n': Expect("null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    throw new JsonParseException("Unexpected character '" + (char)c + "'", _p);
            }
        }

        private void Expect(string lit)
        {
            if (_end - _p < lit.Length) throw new JsonParseException("Invalid literal", _p);
            for (int i = 0; i < lit.Length; i++)
                if (_d[_p + i] != lit[i]) throw new JsonParseException("Invalid literal", _p);
            _p += lit.Length;
        }

        private JsonValue ParseObject(int depth)
        {
            if (depth > _maxDepth) throw new JsonParseException("Nesting deeper than " + _maxDepth, _p);
            _p++; // {
            var o = JsonValue.Obj();
            SkipWs();
            if (_p < _end && _d[_p] == '}') { _p++; return o; }
            while (true)
            {
                SkipWs();
                if (_p >= _end || _d[_p] != '"') throw new JsonParseException("Expected property name", _p);
                string key = ParseString();
                SkipWs();
                if (_p >= _end || _d[_p] != ':') throw new JsonParseException("Expected ':'", _p);
                _p++;
                SkipWs();
                var v = ParseValue(depth);
                o.Set(key, v);
                SkipWs();
                if (_p >= _end) throw new JsonParseException("Unterminated object", _p);
                if (_d[_p] == ',') { _p++; continue; }
                if (_d[_p] == '}') { _p++; return o; }
                throw new JsonParseException("Expected ',' or '}'", _p);
            }
        }

        private JsonValue ParseArray(int depth)
        {
            if (depth > _maxDepth) throw new JsonParseException("Nesting deeper than " + _maxDepth, _p);
            _p++; // [
            var a = JsonValue.Arr();
            SkipWs();
            if (_p < _end && _d[_p] == ']') { _p++; return a; }
            while (true)
            {
                SkipWs();
                a.Add(ParseValue(depth));
                SkipWs();
                if (_p >= _end) throw new JsonParseException("Unterminated array", _p);
                if (_d[_p] == ',') { _p++; continue; }
                if (_d[_p] == ']') { _p++; return a; }
                throw new JsonParseException("Expected ',' or ']'", _p);
            }
        }

        private JsonValue ParseNumber()
        {
            int start = _p;
            bool isInt = true;
            if (_d[_p] == '-') _p++;
            if (_p >= _end) throw new JsonParseException("Invalid number", start);
            if (_d[_p] == '0')
            {
                _p++;
                if (_p < _end && _d[_p] >= '0' && _d[_p] <= '9') throw new JsonParseException("Leading zeros are not allowed", start);
            }
            else if (_d[_p] >= '1' && _d[_p] <= '9')
            {
                while (_p < _end && _d[_p] >= '0' && _d[_p] <= '9') _p++;
            }
            else throw new JsonParseException("Invalid number", start);

            if (_p < _end && _d[_p] == '.')
            {
                isInt = false;
                _p++;
                int fs = _p;
                while (_p < _end && _d[_p] >= '0' && _d[_p] <= '9') _p++;
                if (_p == fs) throw new JsonParseException("Invalid fraction", start);
            }
            if (_p < _end && (_d[_p] == 'e' || _d[_p] == 'E'))
            {
                isInt = false;
                _p++;
                if (_p < _end && (_d[_p] == '+' || _d[_p] == '-')) _p++;
                int es = _p;
                while (_p < _end && _d[_p] >= '0' && _d[_p] <= '9') _p++;
                if (_p == es) throw new JsonParseException("Invalid exponent", start);
            }
            int len = _p - start;
            if (isInt && len <= 18)
            {
                long v = 0;
                int i = start;
                bool neg = _d[i] == '-';
                if (neg) i++;
                for (; i < _p; i++) v = v * 10 + (_d[i] - '0');
                return JsonValue.Num(neg ? -v : v);
            }
            var s = Encoding.ASCII.GetString(_d, start, len);
            if (isInt && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return JsonValue.Num(l);
            double d = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            return JsonValue.Num(d);
        }

        private string ParseString()
        {
            _p++; // opening quote
            int start = _p;
            // Fast path: ASCII without escapes.
            while (_p < _end)
            {
                byte c = _d[_p];
                if (c == '"')
                {
                    var s = Encoding.ASCII.GetString(_d, start, _p - start);
                    _p++;
                    return s;
                }
                if (c == '\\' || c >= 0x80 || c < 0x20) break;
                _p++;
            }
            var sb = _sb ?? (_sb = new StringBuilder(64));
            sb.Length = 0;
            for (int i = start; i < _p; i++) sb.Append((char)_d[i]);
            while (true)
            {
                if (_p >= _end) throw new JsonParseException("Unterminated string", start);
                byte c = _d[_p];
                if (c == '"') { _p++; return sb.ToString(); }
                if (c < 0x20) throw new JsonParseException("Control character in string", _p);
                if (c == '\\')
                {
                    _p++;
                    if (_p >= _end) throw new JsonParseException("Unterminated escape", _p);
                    byte e = _d[_p++];
                    switch (e)
                    {
                        case (byte)'"': sb.Append('"'); break;
                        case (byte)'\\': sb.Append('\\'); break;
                        case (byte)'/': sb.Append('/'); break;
                        case (byte)'b': sb.Append('\b'); break;
                        case (byte)'f': sb.Append('\f'); break;
                        case (byte)'n': sb.Append('\n'); break;
                        case (byte)'r': sb.Append('\r'); break;
                        case (byte)'t': sb.Append('\t'); break;
                        case (byte)'u':
                            if (_end - _p < 4) throw new JsonParseException("Invalid \\u escape", _p);
                            int cp = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                int h = HexVal(_d[_p + k]);
                                if (h < 0) throw new JsonParseException("Invalid \\u escape", _p);
                                cp = (cp << 4) | h;
                            }
                            _p += 4;
                            sb.Append((char)cp);
                            break;
                        default: throw new JsonParseException("Invalid escape", _p - 1);
                    }
                    continue;
                }
                if (c < 0x80) { sb.Append((char)c); _p++; continue; }
                // UTF-8 multi-byte sequence, validated.
                int need, cpv;
                if ((c & 0xE0) == 0xC0) { need = 1; cpv = c & 0x1F; }
                else if ((c & 0xF0) == 0xE0) { need = 2; cpv = c & 0x0F; }
                else if ((c & 0xF8) == 0xF0) { need = 3; cpv = c & 0x07; }
                else throw new JsonParseException("Invalid UTF-8", _p);
                if (_end - _p <= need) throw new JsonParseException("Truncated UTF-8", _p);
                for (int k = 1; k <= need; k++)
                {
                    byte cc = _d[_p + k];
                    if ((cc & 0xC0) != 0x80) throw new JsonParseException("Invalid UTF-8", _p);
                    cpv = (cpv << 6) | (cc & 0x3F);
                }
                if ((need == 1 && cpv < 0x80) || (need == 2 && cpv < 0x800) || (need == 3 && cpv < 0x10000) || cpv > 0x10FFFF || (cpv >= 0xD800 && cpv <= 0xDFFF))
                    throw new JsonParseException("Invalid UTF-8 (overlong or surrogate)", _p);
                _p += need + 1;
                if (cpv >= 0x10000)
                {
                    cpv -= 0x10000;
                    sb.Append((char)(0xD800 + (cpv >> 10)));
                    sb.Append((char)(0xDC00 + (cpv & 0x3FF)));
                }
                else sb.Append((char)cpv);
            }
        }

        private static int HexVal(byte b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            if (b >= 'A' && b <= 'F') return b - 'A' + 10;
            return -1;
        }

        /// <summary>Validates UTF-8 without decoding (RFC 6455 text frames must be valid UTF-8).</summary>
        public static bool IsValidUtf8(byte[] d, int offset, int count)
        {
            int p = offset, end = offset + count;
            while (p < end)
            {
                byte c = d[p];
                if (c < 0x80) { p++; continue; }
                int need, cp;
                if ((c & 0xE0) == 0xC0) { need = 1; cp = c & 0x1F; }
                else if ((c & 0xF0) == 0xE0) { need = 2; cp = c & 0x0F; }
                else if ((c & 0xF8) == 0xF0) { need = 3; cp = c & 0x07; }
                else return false;
                if (end - p <= need) return false;
                for (int k = 1; k <= need; k++)
                {
                    byte cc = d[p + k];
                    if ((cc & 0xC0) != 0x80) return false;
                    cp = (cp << 6) | (cc & 0x3F);
                }
                if ((need == 1 && cp < 0x80) || (need == 2 && cp < 0x800) || (need == 3 && cp < 0x10000) || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
                p += need + 1;
            }
            return true;
        }
    }
}
