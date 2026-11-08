using System;
using System.Collections.Generic;

namespace Skizzy.Protocol.Json
{
    /// <summary>
    /// Growable byte buffer used by every serialiser. Rented from <see cref="ByteBufferPool"/> so the
    /// steady state allocates nothing (02-architecture.md §2.3, "Serialisers write into pooled byte[] segments").
    /// </summary>
    public sealed class ByteBuffer
    {
        public byte[] Data;
        public int Length;

        public ByteBuffer(int capacity = 256)
        {
            Data = new byte[capacity < 16 ? 16 : capacity];
        }

        public int Capacity => Data.Length;

        public void Clear() { Length = 0; }

        // skizzy:hot ByteBuffer.Ensure
        public void Ensure(int extra)
        {
            int need = Length + extra;
            if (need <= Data.Length) return;
            int cap = Data.Length * 2;
            if (cap < need) cap = need;
            var n = new byte[cap];
            Buffer.BlockCopy(Data, 0, n, 0, Length);
            Data = n;
        }

        public void WriteByte(byte b)
        {
            if (Length == Data.Length) Ensure(1);
            Data[Length++] = b;
        }

        public void WriteBytes(byte[] src, int offset, int count)
        {
            if (count <= 0) return;
            Ensure(count);
            Buffer.BlockCopy(src, offset, Data, Length, count);
            Length += count;
        }

        /// <summary>Writes an ASCII-only string (no escaping, no validation). Allocation-free.</summary>
        public void WriteAscii(string s)
        {
            int n = s.Length;
            Ensure(n);
            var d = Data;
            int p = Length;
            for (int i = 0; i < n; i++) d[p + i] = (byte)s[i];
            Length = p + n;
        }

        /// <summary>Writes a string as UTF-8 without escaping. Lone surrogates become U+FFFD.</summary>
        public void WriteUtf8(string s)
        {
            if (s == null) return;
            int n = s.Length;
            Ensure(n * 3);
            var d = Data;
            int p = Length;
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (c < 0x80) { d[p++] = (byte)c; continue; }
                if (c < 0x800)
                {
                    d[p++] = (byte)(0xC0 | (c >> 6));
                    d[p++] = (byte)(0x80 | (c & 0x3F));
                    continue;
                }
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                    d[p++] = (byte)(0xF0 | (cp >> 18));
                    d[p++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                    d[p++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                    d[p++] = (byte)(0x80 | (cp & 0x3F));
                    continue;
                }
                if (char.IsSurrogate(c)) c = '\uFFFD';
                d[p++] = (byte)(0xE0 | (c >> 12));
                d[p++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                d[p++] = (byte)(0x80 | (c & 0x3F));
            }
            Length = p;
        }

        public byte[] ToArray()
        {
            var r = new byte[Length];
            Buffer.BlockCopy(Data, 0, r, 0, Length);
            return r;
        }

        public string ToUtf8String()
        {
            return System.Text.Encoding.UTF8.GetString(Data, 0, Length);
        }
    }

    /// <summary>Thread-safe pool of <see cref="ByteBuffer"/>s. Buffers larger than <see cref="MaxRetainedBytes"/> are dropped on return.</summary>
    public static class ByteBufferPool
    {
        public const int MaxRetainedBytes = 4 * 1024 * 1024;
        private const int MaxPooled = 64;
        private static readonly Stack<ByteBuffer> Pool = new Stack<ByteBuffer>(MaxPooled);
        private static readonly object Gate = new object();

        // skizzy:hot ByteBufferPool.Rent
        public static ByteBuffer Rent(int minCapacity = 1024)
        {
            lock (Gate)
            {
                if (Pool.Count > 0)
                {
                    var b = Pool.Pop();
                    b.Length = 0;
                    if (b.Data.Length < minCapacity) b.Ensure(minCapacity);
                    return b;
                }
            }
            return new ByteBuffer(minCapacity);
        }

        public static void Return(ByteBuffer b)
        {
            if (b == null || b.Data.Length > MaxRetainedBytes) return;
            b.Length = 0;
            lock (Gate)
            {
                if (Pool.Count < MaxPooled) Pool.Push(b);
            }
        }
    }
}
