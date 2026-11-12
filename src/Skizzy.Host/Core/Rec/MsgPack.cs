using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Skizzy.Host.Core.Rec
{
    /// <summary>Kind of the next MessagePack value, as reported by <c>MsgPackReader.PeekType</c>.</summary>
    public enum MsgPackType
    {
        Nil,
        Bool,
        Int,
        Float,
        String,
        Bin,
        Array,
        Map,
        Ext,
    }

    /// <summary>Reinterprets float bits without unsafe code (net48 has no BitConverter.SingleToInt32Bits).</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct MsgPackFloatBits
    {
        [FieldOffset(0)]
        public float Single;

        [FieldOffset(0)]
        public int Int32;
    }

    /// <summary>
    /// Minimal MessagePack encoder (big-endian, per the public MessagePack spec) into a growable byte[] buffer.
    /// Integers always use the smallest encoding. The buffer is reused across <c>Clear()</c> calls.
    /// </summary>
    public sealed class MsgPackWriter
    {
        private const int MaxDepth = 256;
        private byte[] _buf;
        private int _len;

        public MsgPackWriter()
            : this(256)
        {
        }

        public MsgPackWriter(int initialCapacity)
        {
            _buf = new byte[initialCapacity < 16 ? 16 : initialCapacity];
        }

        /// <summary>Number of valid bytes in <c>Buffer</c>.</summary>
        public int Length
        {
            get { return _len; }
        }

        /// <summary>The raw buffer; only the first <c>Length</c> bytes are valid. Replaced when it grows.</summary>
        public byte[] Buffer
        {
            get { return _buf; }
        }

        public void Clear()
        {
            _len = 0;
        }

        public byte[] ToArray()
        {
            var a = new byte[_len];
            System.Buffer.BlockCopy(_buf, 0, a, 0, _len);
            return a;
        }

        /// <summary>Drops everything after <paramref name="length"/> (used to roll back a partly written record).</summary>
        internal void Truncate(int length)
        {
            if (length < 0 || length > _len)
            {
                throw new ArgumentOutOfRangeException("length");
            }
            _len = length;
        }

        public void WriteNil()
        {
            Ensure(1);
            _buf[_len++] = 0xc0;
        }

        public void WriteBool(bool value)
        {
            Ensure(1);
            _buf[_len++] = value ? (byte)0xc3 : (byte)0xc2;
        }

        public void WriteInt(long value)
        {
            if (value >= 0)
            {
                if (value <= 0x7f)
                {
                    Ensure(1);
                    _buf[_len++] = (byte)value;
                }
                else if (value <= 0xff)
                {
                    Ensure(2);
                    _buf[_len++] = 0xcc;
                    _buf[_len++] = (byte)value;
                }
                else if (value <= 0xffff)
                {
                    Ensure(3);
                    _buf[_len++] = 0xcd;
                    Put16((uint)value);
                }
                else if (value <= 0xffffffffL)
                {
                    Ensure(5);
                    _buf[_len++] = 0xce;
                    Put32((uint)value);
                }
                else
                {
                    Ensure(9);
                    _buf[_len++] = 0xcf;
                    Put64((ulong)value);
                }
            }
            else
            {
                if (value >= -32)
                {
                    Ensure(1);
                    _buf[_len++] = unchecked((byte)value);
                }
                else if (value >= sbyte.MinValue)
                {
                    Ensure(2);
                    _buf[_len++] = 0xd0;
                    _buf[_len++] = unchecked((byte)value);
                }
                else if (value >= short.MinValue)
                {
                    Ensure(3);
                    _buf[_len++] = 0xd1;
                    Put16(unchecked((uint)value));
                }
                else if (value >= int.MinValue)
                {
                    Ensure(5);
                    _buf[_len++] = 0xd2;
                    Put32(unchecked((uint)value));
                }
                else
                {
                    Ensure(9);
                    _buf[_len++] = 0xd3;
                    Put64(unchecked((ulong)value));
                }
            }
        }

        public void WriteUInt(ulong value)
        {
            if (value <= long.MaxValue)
            {
                WriteInt((long)value);
                return;
            }
            Ensure(9);
            _buf[_len++] = 0xcf;
            Put64(value);
        }

        public void WriteDouble(double value)
        {
            Ensure(9);
            _buf[_len++] = 0xcb;
            Put64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        }

        public void WriteFloat(float value)
        {
            var bits = new MsgPackFloatBits();
            bits.Single = value;
            Ensure(5);
            _buf[_len++] = 0xca;
            Put32(unchecked((uint)bits.Int32));
        }

        /// <summary>UTF-8 string; null is written as nil.</summary>
        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteNil();
                return;
            }
            int n = Encoding.UTF8.GetByteCount(value);
            if (n <= 31)
            {
                Ensure(1 + n);
                _buf[_len++] = (byte)(0xa0 | n);
            }
            else if (n <= 0xff)
            {
                Ensure(2 + n);
                _buf[_len++] = 0xd9;
                _buf[_len++] = (byte)n;
            }
            else if (n <= 0xffff)
            {
                Ensure(3 + n);
                _buf[_len++] = 0xda;
                Put16((uint)n);
            }
            else
            {
                Ensure(5 + n);
                _buf[_len++] = 0xdb;
                Put32((uint)n);
            }
            _len += Encoding.UTF8.GetBytes(value, 0, value.Length, _buf, _len);
        }

        /// <summary>Binary value (bin8/16/32); a null array is written as nil.</summary>
        public void WriteBin(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                WriteNil();
                return;
            }
            CheckRange(data, offset, count);
            WriteBinHeader(count);
            WriteRaw(data, offset, count);
        }

        public void WriteBin(byte[] data)
        {
            WriteBin(data, 0, data == null ? 0 : data.Length);
        }

        /// <summary>Only the bin header; the caller must append exactly <paramref name="count"/> bytes with <c>WriteRaw</c>.</summary>
        public void WriteBinHeader(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count <= 0xff)
            {
                Ensure(2);
                _buf[_len++] = 0xc4;
                _buf[_len++] = (byte)count;
            }
            else if (count <= 0xffff)
            {
                Ensure(3);
                _buf[_len++] = 0xc5;
                Put16((uint)count);
            }
            else
            {
                Ensure(5);
                _buf[_len++] = 0xc6;
                Put32((uint)count);
            }
        }

        public void WriteArrayHeader(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count <= 15)
            {
                Ensure(1);
                _buf[_len++] = (byte)(0x90 | count);
            }
            else if (count <= 0xffff)
            {
                Ensure(3);
                _buf[_len++] = 0xdc;
                Put16((uint)count);
            }
            else
            {
                Ensure(5);
                _buf[_len++] = 0xdd;
                Put32((uint)count);
            }
        }

        public void WriteMapHeader(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count <= 15)
            {
                Ensure(1);
                _buf[_len++] = (byte)(0x80 | count);
            }
            else if (count <= 0xffff)
            {
                Ensure(3);
                _buf[_len++] = 0xde;
                Put16((uint)count);
            }
            else
            {
                Ensure(5);
                _buf[_len++] = 0xdf;
                Put32((uint)count);
            }
        }

        /// <summary>Appends bytes that are already MessagePack-encoded.</summary>
        public void WriteRaw(byte[] data, int offset, int count)
        {
            CheckRange(data, offset, count);
            Ensure(count);
            System.Buffer.BlockCopy(data, offset, _buf, _len, count);
            _len += count;
        }

        /// <summary>
        /// Writes a CLR value: null, bool, any integer type, float, double, string, byte[] (bin), IList&lt;object&gt; or
        /// object[] (array), IDictionary&lt;string, object&gt; (map); non-generic IList/IDictionary are also accepted.
        /// Anything else throws ArgumentException.
        /// </summary>
        public void WriteValue(object value)
        {
            WriteValue(value, 0);
        }

        private void WriteValue(object v, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ArgumentException("MsgPackWriter: value nested too deeply (cycle?)");
            }
            if (v == null)
            {
                WriteNil();
                return;
            }
            if (v is string s)
            {
                WriteString(s);
                return;
            }
            if (v is bool b)
            {
                WriteBool(b);
                return;
            }
            if (v is int i32)
            {
                WriteInt(i32);
                return;
            }
            if (v is long i64)
            {
                WriteInt(i64);
                return;
            }
            if (v is double d)
            {
                WriteDouble(d);
                return;
            }
            if (v is float f)
            {
                WriteFloat(f);
                return;
            }
            if (v is byte u8)
            {
                WriteInt(u8);
                return;
            }
            if (v is sbyte i8)
            {
                WriteInt(i8);
                return;
            }
            if (v is short i16)
            {
                WriteInt(i16);
                return;
            }
            if (v is ushort u16)
            {
                WriteInt(u16);
                return;
            }
            if (v is uint u32)
            {
                WriteInt(u32);
                return;
            }
            if (v is ulong u64)
            {
                WriteUInt(u64);
                return;
            }
            if (v is byte[] bytes)
            {
                WriteBin(bytes, 0, bytes.Length);
                return;
            }
            if (v is IDictionary<string, object> map)
            {
                WriteMapHeader(map.Count);
                foreach (KeyValuePair<string, object> kv in map)
                {
                    WriteString(kv.Key);
                    WriteValue(kv.Value, depth + 1);
                }
                return;
            }
            if (v is IList<object> list)
            {
                int n = list.Count;
                WriteArrayHeader(n);
                for (int k = 0; k < n; k++)
                {
                    WriteValue(list[k], depth + 1);
                }
                return;
            }
            if (v is IDictionary dict)
            {
                WriteMapHeader(dict.Count);
                foreach (DictionaryEntry e in dict)
                {
                    WriteString(MsgPackReader.KeyString(e.Key));
                    WriteValue(e.Value, depth + 1);
                }
                return;
            }
            if (v is IList ilist)
            {
                int n = ilist.Count;
                WriteArrayHeader(n);
                for (int k = 0; k < n; k++)
                {
                    WriteValue(ilist[k], depth + 1);
                }
                return;
            }
            throw new ArgumentException("MsgPackWriter: unsupported value type " + v.GetType().FullName);
        }

        private void Put16(uint v)
        {
            _buf[_len++] = (byte)(v >> 8);
            _buf[_len++] = (byte)v;
        }

        private void Put32(uint v)
        {
            _buf[_len++] = (byte)(v >> 24);
            _buf[_len++] = (byte)(v >> 16);
            _buf[_len++] = (byte)(v >> 8);
            _buf[_len++] = (byte)v;
        }

        private void Put64(ulong v)
        {
            Put32((uint)(v >> 32));
            Put32((uint)v);
        }

        private void Ensure(int extra)
        {
            int need = _len + extra;
            if (extra < 0 || need < 0)
            {
                throw new InvalidOperationException("MsgPackWriter: buffer too large");
            }
            if (need <= _buf.Length)
            {
                return;
            }
            int cap = _buf.Length;
            while (cap < need)
            {
                cap = cap > 0x3fffffff ? int.MaxValue : cap * 2;
            }
            var nb = new byte[cap];
            System.Buffer.BlockCopy(_buf, 0, nb, 0, _len);
            _buf = nb;
        }

        private static void CheckRange(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException("offset");
            }
        }
    }

    /// <summary>
    /// Minimal MessagePack decoder over a byte[] slice. Truncated or invalid input throws FormatException
    /// (never IndexOutOfRangeException).
    /// </summary>
    public sealed class MsgPackReader
    {
        private const int MaxDepth = 256;
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;

        public MsgPackReader(byte[] data)
            : this(data, 0, data == null ? 0 : data.Length)
        {
        }

        public MsgPackReader(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException("offset");
            }
            _data = data;
            _pos = offset;
            _end = offset + count;
        }

        /// <summary>Absolute index into the underlying array of the next byte to read.</summary>
        public int Position
        {
            get { return _pos; }
        }

        public bool End
        {
            get { return _pos >= _end; }
        }

        public MsgPackType PeekType()
        {
            Need(1);
            return TypeOf(_data[_pos]);
        }

        /// <summary>Consumes a nil and returns true, or returns false and leaves the position unchanged.</summary>
        public bool TryReadNil()
        {
            Need(1);
            if (_data[_pos] != 0xc0)
            {
                return false;
            }
            _pos++;
            return true;
        }

        public bool ReadBool()
        {
            Need(1);
            byte b = _data[_pos];
            if (b == 0xc2 || b == 0xc3)
            {
                _pos++;
                return b == 0xc3;
            }
            throw Mismatch("bool", b);
        }

        /// <summary>Any integer encoding; float32/float64 are truncated toward zero.</summary>
        public long ReadInt64()
        {
            Need(1);
            byte b = _data[_pos];
            if (b <= 0x7f)
            {
                _pos++;
                return b;
            }
            if (b >= 0xe0)
            {
                _pos++;
                return unchecked((sbyte)b);
            }
            switch (b)
            {
                case 0xcc:
                    Need(2);
                    _pos++;
                    return U8();
                case 0xcd:
                    Need(3);
                    _pos++;
                    return U16();
                case 0xce:
                    Need(5);
                    _pos++;
                    return U32();
                case 0xcf:
                    {
                        Need(9);
                        ulong u = Peek64(_pos + 1);
                        if (u > long.MaxValue)
                        {
                            throw Bad("uint64 value out of Int64 range");
                        }
                        _pos += 9;
                        return (long)u;
                    }
                case 0xd0:
                    Need(2);
                    _pos++;
                    return unchecked((sbyte)U8());
                case 0xd1:
                    Need(3);
                    _pos++;
                    return unchecked((short)U16());
                case 0xd2:
                    Need(5);
                    _pos++;
                    return unchecked((int)U32());
                case 0xd3:
                    Need(9);
                    _pos++;
                    return unchecked((long)U64());
                case 0xca:
                case 0xcb:
                    return (long)ReadDouble();
                default:
                    throw Mismatch("integer", b);
            }
        }

        /// <summary>float32 (widened), float64 or any integer encoding.</summary>
        public double ReadDouble()
        {
            Need(1);
            byte b = _data[_pos];
            if (b == 0xcb)
            {
                Need(9);
                _pos++;
                return BitConverter.Int64BitsToDouble(unchecked((long)U64()));
            }
            if (b == 0xca)
            {
                Need(5);
                _pos++;
                var bits = new MsgPackFloatBits();
                bits.Int32 = unchecked((int)U32());
                return bits.Single;
            }
            if (b == 0xcf)
            {
                Need(9);
                _pos++;
                return U64();
            }
            if (TypeOf(b) == MsgPackType.Int)
            {
                return ReadInt64();
            }
            throw Mismatch("number", b);
        }

        /// <summary>UTF-8 string; nil returns null.</summary>
        public string ReadString()
        {
            Need(1);
            byte b = _data[_pos];
            int n;
            if (b >= 0xa0 && b <= 0xbf)
            {
                _pos++;
                n = b & 0x1f;
            }
            else if (b == 0xc0)
            {
                _pos++;
                return null;
            }
            else if (b == 0xd9)
            {
                Need(2);
                _pos++;
                n = U8();
            }
            else if (b == 0xda)
            {
                Need(3);
                _pos++;
                n = U16();
            }
            else if (b == 0xdb)
            {
                Need(5);
                _pos++;
                n = Len32();
            }
            else
            {
                throw Mismatch("string", b);
            }
            Need(n);
            string s = Encoding.UTF8.GetString(_data, _pos, n);
            _pos += n;
            return s;
        }

        /// <summary>Binary value (a copy); nil returns null.</summary>
        public byte[] ReadBin()
        {
            Need(1);
            byte b = _data[_pos];
            int n;
            if (b == 0xc0)
            {
                _pos++;
                return null;
            }
            if (b == 0xc4)
            {
                Need(2);
                _pos++;
                n = U8();
            }
            else if (b == 0xc5)
            {
                Need(3);
                _pos++;
                n = U16();
            }
            else if (b == 0xc6)
            {
                Need(5);
                _pos++;
                n = Len32();
            }
            else
            {
                throw Mismatch("bin", b);
            }
            Need(n);
            var a = new byte[n];
            Buffer.BlockCopy(_data, _pos, a, 0, n);
            _pos += n;
            return a;
        }

        public int ReadArrayHeader()
        {
            Need(1);
            byte b = _data[_pos];
            int n;
            if (b >= 0x90 && b <= 0x9f)
            {
                _pos++;
                n = b & 0x0f;
            }
            else if (b == 0xdc)
            {
                Need(3);
                _pos++;
                n = U16();
            }
            else if (b == 0xdd)
            {
                Need(5);
                _pos++;
                n = Len32();
            }
            else
            {
                throw Mismatch("array", b);
            }
            if (n > _end - _pos)
            {
                throw Bad("array length exceeds the data");
            }
            return n;
        }

        public int ReadMapHeader()
        {
            Need(1);
            byte b = _data[_pos];
            int n;
            if (b >= 0x80 && b <= 0x8f)
            {
                _pos++;
                n = b & 0x0f;
            }
            else if (b == 0xde)
            {
                Need(3);
                _pos++;
                n = U16();
            }
            else if (b == 0xdf)
            {
                Need(5);
                _pos++;
                n = Len32();
            }
            else
            {
                throw Mismatch("map", b);
            }
            if (n > (_end - _pos) / 2)
            {
                throw Bad("map length exceeds the data");
            }
            return n;
        }

        /// <summary>Skips one complete value, including everything nested in it (iterative, no recursion).</summary>
        public void Skip()
        {
            long pending = 1;
            while (pending > 0)
            {
                pending--;
                Need(1);
                byte b = _data[_pos++];
                if (b <= 0x7f || b >= 0xe0)
                {
                    continue;
                }
                if (b <= 0x8f)
                {
                    pending += 2 * (b & 0x0f);
                }
                else if (b <= 0x9f)
                {
                    pending += b & 0x0f;
                }
                else if (b <= 0xbf)
                {
                    Advance(b & 0x1f);
                }
                else
                {
                    switch (b)
                    {
                        case 0xc0:
                        case 0xc2:
                        case 0xc3:
                            break;
                        case 0xc4:
                        case 0xd9:
                            Need(1);
                            Advance(U8());
                            break;
                        case 0xc5:
                        case 0xda:
                            Need(2);
                            Advance(U16());
                            break;
                        case 0xc6:
                        case 0xdb:
                            Need(4);
                            Advance(U32());
                            break;
                        case 0xc7:
                            Need(1);
                            Advance(1L + U8());
                            break;
                        case 0xc8:
                            Need(2);
                            Advance(1L + U16());
                            break;
                        case 0xc9:
                            Need(4);
                            Advance(1L + U32());
                            break;
                        case 0xcc:
                        case 0xd0:
                            Advance(1);
                            break;
                        case 0xcd:
                        case 0xd1:
                            Advance(2);
                            break;
                        case 0xca:
                        case 0xce:
                        case 0xd2:
                            Advance(4);
                            break;
                        case 0xcb:
                        case 0xcf:
                        case 0xd3:
                            Advance(8);
                            break;
                        case 0xd4:
                            Advance(2);
                            break;
                        case 0xd5:
                            Advance(3);
                            break;
                        case 0xd6:
                            Advance(5);
                            break;
                        case 0xd7:
                            Advance(9);
                            break;
                        case 0xd8:
                            Advance(17);
                            break;
                        case 0xdc:
                            Need(2);
                            pending += U16();
                            break;
                        case 0xdd:
                            Need(4);
                            pending += U32();
                            break;
                        case 0xde:
                            Need(2);
                            pending += 2L * U16();
                            break;
                        case 0xdf:
                            Need(4);
                            pending += 2L * U32();
                            break;
                        default:
                            throw Bad("invalid type byte 0x" + b.ToString("x2", CultureInfo.InvariantCulture));
                    }
                }
                if (pending > _end - _pos)
                {
                    throw Bad("unexpected end of data");
                }
            }
        }

        /// <summary>
        /// Reads any value: null, bool, long (all ints; uint64 above long.MaxValue returns ulong), double (float32
        /// widened), string, byte[], object[] or Dictionary&lt;string, object&gt; (non-string keys use ToString()).
        /// Ext values throw FormatException.
        /// </summary>
        public object ReadObject()
        {
            return ReadObject(0);
        }

        private object ReadObject(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Bad("values nested too deeply");
            }
            Need(1);
            byte b = _data[_pos];
            switch (TypeOf(b))
            {
                case MsgPackType.Nil:
                    _pos++;
                    return null;
                case MsgPackType.Bool:
                    return ReadBool();
                case MsgPackType.Int:
                    if (b == 0xcf)
                    {
                        Need(9);
                        ulong u = Peek64(_pos + 1);
                        if (u > long.MaxValue)
                        {
                            _pos += 9;
                            return u;
                        }
                    }
                    return ReadInt64();
                case MsgPackType.Float:
                    return ReadDouble();
                case MsgPackType.String:
                    return ReadString();
                case MsgPackType.Bin:
                    return ReadBin();
                case MsgPackType.Array:
                    {
                        int n = ReadArrayHeader();
                        var a = new object[n];
                        for (int i = 0; i < n; i++)
                        {
                            a[i] = ReadObject(depth + 1);
                        }
                        return a;
                    }
                case MsgPackType.Map:
                    {
                        int n = ReadMapHeader();
                        var m = new Dictionary<string, object>(n, StringComparer.Ordinal);
                        for (int i = 0; i < n; i++)
                        {
                            string key = KeyString(ReadObject(depth + 1));
                            m[key] = ReadObject(depth + 1);
                        }
                        return m;
                    }
                default:
                    throw Bad("ext values are not supported");
            }
        }

        internal static string KeyString(object key)
        {
            if (key == null)
            {
                return string.Empty;
            }
            if (key is string s)
            {
                return s;
            }
            if (key is IFormattable f)
            {
                return f.ToString(null, CultureInfo.InvariantCulture);
            }
            return key.ToString();
        }

        private static MsgPackType TypeOf(byte b)
        {
            if (b <= 0x7f || b >= 0xe0)
            {
                return MsgPackType.Int;
            }
            if (b <= 0x8f)
            {
                return MsgPackType.Map;
            }
            if (b <= 0x9f)
            {
                return MsgPackType.Array;
            }
            if (b <= 0xbf)
            {
                return MsgPackType.String;
            }
            switch (b)
            {
                case 0xc0:
                    return MsgPackType.Nil;
                case 0xc2:
                case 0xc3:
                    return MsgPackType.Bool;
                case 0xc4:
                case 0xc5:
                case 0xc6:
                    return MsgPackType.Bin;
                case 0xc7:
                case 0xc8:
                case 0xc9:
                case 0xd4:
                case 0xd5:
                case 0xd6:
                case 0xd7:
                case 0xd8:
                    return MsgPackType.Ext;
                case 0xca:
                case 0xcb:
                    return MsgPackType.Float;
                case 0xd9:
                case 0xda:
                case 0xdb:
                    return MsgPackType.String;
                case 0xdc:
                case 0xdd:
                    return MsgPackType.Array;
                case 0xde:
                case 0xdf:
                    return MsgPackType.Map;
                case 0xc1:
                    throw Bad("invalid type byte 0xc1");
                default:
                    return MsgPackType.Int; // 0xcc..0xd3
            }
        }

        private void Need(long n)
        {
            if (n < 0 || n > _end - _pos)
            {
                throw Bad("unexpected end of data");
            }
        }

        private void Advance(long n)
        {
            Need(n);
            _pos += (int)n;
        }

        // The U* helpers assume the caller already checked the bytes are available.
        private int U8()
        {
            return _data[_pos++];
        }

        private int U16()
        {
            int v = (_data[_pos] << 8) | _data[_pos + 1];
            _pos += 2;
            return v;
        }

        private uint U32()
        {
            uint v = ((uint)_data[_pos] << 24) | ((uint)_data[_pos + 1] << 16) | ((uint)_data[_pos + 2] << 8) | _data[_pos + 3];
            _pos += 4;
            return v;
        }

        private ulong U64()
        {
            ulong v = Peek64(_pos);
            _pos += 8;
            return v;
        }

        private ulong Peek64(int at)
        {
            ulong hi = ((uint)_data[at] << 24) | ((uint)_data[at + 1] << 16) | ((uint)_data[at + 2] << 8) | _data[at + 3];
            ulong lo = ((uint)_data[at + 4] << 24) | ((uint)_data[at + 5] << 16) | ((uint)_data[at + 6] << 8) | _data[at + 7];
            return (hi << 32) | lo;
        }

        private int Len32()
        {
            uint n = U32();
            if (n > int.MaxValue)
            {
                throw Bad("length out of range");
            }
            return (int)n;
        }

        private static FormatException Bad(string message)
        {
            return new FormatException("msgpack: " + message);
        }

        private static FormatException Mismatch(string expected, byte b)
        {
            return Bad("expected " + expected + ", found type byte 0x" + b.ToString("x2", CultureInfo.InvariantCulture));
        }
    }
}
