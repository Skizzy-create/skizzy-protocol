using System;

namespace Skizzy.Protocol.Json
{
    /// <summary>
    /// Allocation-free, culture-invariant number formatting into a <see cref="ByteBuffer"/>.
    /// Doubles use up to 15 significant digits (enough for UT with sub-millisecond precision),
    /// floats up to 7 (so 0.1f prints as "0.1"). Integers print exactly.
    /// Output is valid JSON number syntax and parses back with <c>double.Parse(InvariantCulture)</c>.
    /// </summary>
    public static class NumberFormat
    {
        public const int DoubleDigits = 15;
        public const int SingleDigits = 7;

        private static readonly double[] Pow10D =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
            1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
        };

        private static readonly long[] Pow10L =
        {
            1L, 10L, 100L, 1000L, 10000L, 100000L, 1000000L, 10000000L, 100000000L, 1000000000L,
            10000000000L, 100000000000L, 1000000000000L, 10000000000000L, 100000000000000L,
            1000000000000000L, 10000000000000000L, 100000000000000000L, 1000000000000000000L
        };

        [ThreadStatic] private static byte[] _digits;

        // skizzy:hot NumberFormat.WriteLong
        public static void WriteLong(ByteBuffer b, long v)
        {
            b.Ensure(20);
            var d = b.Data;
            int p = b.Length;
            if (v == 0) { d[p] = (byte)'0'; b.Length = p + 1; return; }
            ulong u;
            if (v < 0)
            {
                d[p++] = (byte)'-';
                u = v == long.MinValue ? 9223372036854775808UL : (ulong)(-v);
            }
            else u = (ulong)v;
            int start = p;
            while (u != 0)
            {
                ulong q = u / 10;
                d[p++] = (byte)('0' + (int)(u - q * 10));
                u = q;
            }
            // reverse
            int i = start, j = p - 1;
            while (i < j)
            {
                byte t = d[i]; d[i] = d[j]; d[j] = t;
                i++; j--;
            }
            b.Length = p;
        }

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>Writes a finite double. Callers must handle NaN/Infinity (JSON writes null).</summary>
        // skizzy:hot NumberFormat.WriteDouble
        public static void WriteDouble(ByteBuffer b, double v, int digits = DoubleDigits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { b.WriteAscii("null"); return; }
            if (v == 0) { b.WriteByte((byte)'0'); return; }
            if (digits < 1) digits = 1;
            if (digits > 17) digits = 17;
            double a = v < 0 ? -v : v;
            if (a < 1e15 && a == Math.Floor(a))
            {
                // Integral value: exact. Floats keep their own precision rule below only if huge.
                if (digits >= 15 || a < Pow10D[digits])
                {
                    WriteLong(b, (long)v);
                    return;
                }
            }

            int e = (int)Math.Floor(Math.Log10(a));
            long m = ScaleToDigits(a, digits, ref e);
            if (m <= 0) { b.WriteByte((byte)'0'); return; }

            var dig = _digits ?? (_digits = new byte[24]);
            // m has exactly `digits` digits.
            for (int i = digits - 1; i >= 0; i--)
            {
                long q = m / 10;
                dig[i] = (byte)('0' + (int)(m - q * 10));
                m = q;
            }
            int n = digits;
            while (n > 1 && dig[n - 1] == (byte)'0') n--;

            b.Ensure(32);
            var d = b.Data;
            int p = b.Length;
            if (v < 0) d[p++] = (byte)'-';

            if (e >= -6 && e < 15)
            {
                if (e < 0)
                {
                    d[p++] = (byte)'0';
                    d[p++] = (byte)'.';
                    for (int z = 0; z < -e - 1; z++) d[p++] = (byte)'0';
                    for (int i = 0; i < n; i++) d[p++] = dig[i];
                }
                else
                {
                    int intDigits = e + 1;
                    for (int i = 0; i < intDigits; i++) d[p++] = i < n ? dig[i] : (byte)'0';
                    if (n > intDigits)
                    {
                        d[p++] = (byte)'.';
                        for (int i = intDigits; i < n; i++) d[p++] = dig[i];
                    }
                }
            }
            else
            {
                d[p++] = dig[0];
                if (n > 1)
                {
                    d[p++] = (byte)'.';
                    for (int i = 1; i < n; i++) d[p++] = dig[i];
                }
                d[p++] = (byte)'e';
                int ee = e;
                if (ee < 0) { d[p++] = (byte)'-'; ee = -ee; }
                else d[p++] = (byte)'+';
                if (ee >= 100) { d[p++] = (byte)('0' + ee / 100); ee %= 100; d[p++] = (byte)('0' + ee / 10); d[p++] = (byte)('0' + ee % 10); }
                else if (ee >= 10) { d[p++] = (byte)('0' + ee / 10); d[p++] = (byte)('0' + ee % 10); }
                else d[p++] = (byte)('0' + ee);
            }
            b.Length = p;
        }

        public static void WriteSingle(ByteBuffer b, float v)
        {
            WriteDouble(b, v, SingleDigits);
        }

        /// <summary>Scales |v| to an integer with exactly <paramref name="digits"/> digits; adjusts the decimal exponent.</summary>
        private static long ScaleToDigits(double a, int digits, ref int e)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int shift = digits - 1 - e;
                double scaled = shift >= 0 ? a * Pow10(shift) : a / Pow10(-shift);
                if (double.IsInfinity(scaled) || double.IsNaN(scaled))
                {
                    // Extreme magnitudes: scale in two steps.
                    scaled = shift >= 0 ? (a * Pow10(shift / 2)) * Pow10(shift - shift / 2) : (a / Pow10(-shift / 2)) / Pow10(-shift + shift / 2);
                }
                long m = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
                if (m >= Pow10L[digits]) { e++; continue; }
                if (m < Pow10L[digits - 1]) { e--; continue; }
                return m;
            }
            // Fallback after rounding at a boundary (e.g. 9.9999999999999999 -> 10^digits).
            int s2 = digits - 1 - e;
            double sc = s2 >= 0 ? a * Pow10(s2) : a / Pow10(-s2);
            long mm = (long)Math.Round(sc, MidpointRounding.AwayFromZero);
            if (mm >= Pow10L[digits]) { mm /= 10; e++; }
            return mm;
        }

        private static double Pow10(int n)
        {
            if (n >= 0 && n < Pow10D.Length) return Pow10D[n];
            return Math.Pow(10, n);
        }
    }
}
