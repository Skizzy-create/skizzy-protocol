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
    /// <summary>
    /// Size-classed buffer pool (power-of-two buckets). A rented buffer always has at least the requested capacity
    /// without growing, so buffers of different sizes cannot circulate between consumers and force <c>Ensure</c>
    /// to reallocate in steady state. Retention is bounded per bucket and in total.
    /// </summary>
    public static class ByteBufferPool
    {
        public const int MaxRetainedBytes = 4 * 1024 * 1024;
        private const int MinShift = 8;    // 256 B
        private const int MaxShift = 22;   // 4 MB
        private const int Buckets = MaxShift - MinShift + 1;
        private const int LargeShift = 16;  // >= 64 KB counts against the large-buffer budget
        private const long MaxLargeRetained = 24L * 1024 * 1024;
        private static readonly ByteBuffer[][] Pool = CreatePool();
        private static readonly int[] Counts = new int[Buckets];
        private static long _retained, _largeRetained;
        private static readonly object Gate = new object();

        private static ByteBuffer[][] CreatePool()
        {
            var p = new ByteBuffer[Buckets][];
            for (int k = 0; k < Buckets; k++) p[k] = new ByteBuffer[PerBucket(k + MinShift)];
            return p;
        }

        private static int PerBucket(int shift)
        {
            if (shift >= 20) return 4;     // >= 1 MB
            if (shift >= 16) return 16;    // >= 64 KB
            return 32;
        }

        /// <summary>Smallest shift with (1 &lt;&lt; shift) &gt;= n.</summary>
        internal static int CeilShift(int n)
        {
            int k = MinShift;
            while (k < 31 && (1 << k) < n) k++;
            return k;
        }

        /// <summary>Largest shift with (1 &lt;&lt; shift) &lt;= n.</summary>
        internal static int FloorShift(int n)
        {
            int k = MinShift;
            while (k < 30 && (1 << (k + 1)) <= n) k++;
            return k;
        }

        public static int RetainedBytes { get { lock (Gate) return (int)_retained; } }

        // skizzy:hot ByteBufferPool.Rent
        public static ByteBuffer Rent(int minCapacity = 1024)
        {
            int k = CeilShift(minCapacity);
            if (k <= MaxShift)
            {
                int top = k + 3 > MaxShift ? MaxShift : k + 3;
                lock (Gate)
                {
                    for (int j = k; j <= top; j++)
                    {
                        int c = Counts[j - MinShift];
                        if (c == 0) continue;
                        var bucket = Pool[j - MinShift];
                        var b = bucket[c - 1];
                        bucket[c - 1] = null;
                        Counts[j - MinShift] = c - 1;
                        _retained -= b.Data.Length;
                        if (j >= LargeShift) _largeRetained -= b.Data.Length;
                        b.Length = 0;
                        return b;
                    }
                }
                return new ByteBuffer(1 << k);
            }
            return new ByteBuffer(minCapacity);
        }

        public static void Return(ByteBuffer b)
        {
            if (b == null) return;
            int cap = b.Data.Length;
            if (cap > MaxRetainedBytes || cap < (1 << MinShift)) return;
            b.Length = 0;
            int shift = FloorShift(cap);
            int k = shift - MinShift;
            bool large = shift >= LargeShift;
            lock (Gate)
            {
                int c = Counts[k];
                var bucket = Pool[k];
                if (c >= bucket.Length) return;
                // Only large buffers share a byte budget, so a burst of big messages can never starve the small buckets.
                if (large && _largeRetained + cap > MaxLargeRetained) return;
                for (int i = 0; i < c; i++) if (ReferenceEquals(bucket[i], b)) return;   // double return guard
                bucket[c] = b;
                Counts[k] = c + 1;
                _retained += cap;
                if (large) _largeRetained += cap;
            }
        }
    }
}
