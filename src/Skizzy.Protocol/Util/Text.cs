using System;
using System.Collections.Generic;

namespace Skizzy.Protocol.Util
{
    /// <summary>Simple '*'/'?' glob matching, case-insensitive (vessel names, capability rules, event names).</summary>
    public static class Glob
    {
        public static bool IsMatch(string text, string pattern)
        {
            if (pattern == null) return false;
            if (text == null) text = "";
            if (pattern == "*") return true;
            return Match(text, 0, pattern, 0);
        }

        private static bool Match(string t, int ti, string p, int pi)
        {
            while (pi < p.Length)
            {
                char pc = p[pi];
                if (pc == '*')
                {
                    while (pi < p.Length && p[pi] == '*') pi++;
                    if (pi == p.Length) return true;
                    for (int k = ti; k <= t.Length; k++) if (Match(t, k, p, pi)) return true;
                    return false;
                }
                if (ti >= t.Length) return false;
                if (pc != '?' && char.ToLowerInvariant(pc) != char.ToLowerInvariant(t[ti])) return false;
                ti++;
                pi++;
            }
            return ti == t.Length;
        }

        /// <summary>Specificity of a pattern: exact names beat globs; longer literal prefixes beat shorter ones.</summary>
        public static int Specificity(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int star = pattern.IndexOfAny(new[] { '*', '?' });
            if (star < 0) return 10000 + pattern.Length;
            return star;
        }
    }

    public static class Levenshtein
    {
        public static int Distance(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        /// <summary>Best candidate for "did you mean", or null when nothing is reasonably close.</summary>
        public static string Closest(string name, IEnumerable<string> candidates)
        {
            string best = null;
            int bestD = int.MaxValue;
            foreach (var c in candidates)
            {
                int d = Distance(name, c);
                if (c.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) d = Math.Min(d, 2);
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) return null;
            return bestD <= Math.Max(3, name.Length / 3) ? best : null;
        }
    }

    /// <summary>Text helpers for compact LLM-facing output (15 §5: names are data, control characters escaped, 48-char cap).</summary>
    public static class CompactText
    {
        public const int MaxName = 48;

        /// <summary>Untrusted text made safe for one compact line (15 §5): control characters become spaces, cut at max.</summary>
        public static string SafeName(string s) { return SafeName(s, MaxName); }

        public static string SafeName(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            bool clean = s.Length <= max;
            for (int i = 0; clean && i < s.Length; i++) if (s[i] < 0x20 || s[i] == 0x7F) clean = false;
            if (clean) return s;
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, max) + 3);
            for (int i = 0; i < s.Length && sb.Length < max; i++)
            {
                char c = s[i];
                if (c < 0x20 || c == 0x7F) sb.Append(' ');
                else sb.Append(c);
            }
            if (s.Length > max) sb.Append("...");
            return sb.ToString();
        }

        public static string Num(double v, int decimals)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsInfinity(v)) return v > 0 ? "+Inf" : "-Inf";
            return v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Lock-protected token bucket; thread-safe and allocation-free.</summary>
    public sealed class RateLimiter
    {
        private readonly object _gate = new object();
        private readonly double _capacity;
        private readonly double _refillPerMs;
        private double _tokens;
        private long _lastMs;
        public string Description { get; }

        public RateLimiter(int count, double periodMs, string description)
        {
            _capacity = Math.Max(1, count);
            _refillPerMs = count / Math.Max(1.0, periodMs);
            _tokens = _capacity;
            _lastMs = -1;
            Description = description;
        }

        /// <summary>Takes one token; returns 0 on success or the milliseconds until a token is available.</summary>
        public long TryTake(long nowMs)
        {
            lock (_gate)
            {
                if (_lastMs < 0) _lastMs = nowMs;
                double elapsed = nowMs - _lastMs;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerMs);
                    _lastMs = nowMs;
                }
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return 0;
                }
                return (long)Math.Ceiling((1 - _tokens) / _refillPerMs);
            }
        }

        /// <summary>Parses "6/min", "20/s", "1/2s", "600/min".</summary>
        public static bool TryParseSpec(string spec, out int count, out double periodMs)
        {
            count = 0;
            periodMs = 0;
            if (string.IsNullOrEmpty(spec)) return false;
            var parts = spec.Trim().Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out count) || count <= 0) return false;
            string p = parts[1].Trim().ToLowerInvariant();
            double mult = 1;
            int i = 0;
            while (i < p.Length && (char.IsDigit(p[i]) || p[i] == '.')) i++;
            if (i > 0 && !double.TryParse(p.Substring(0, i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mult)) return false;
            string unit = p.Substring(i);
            switch (unit)
            {
                case "ms": periodMs = mult; break;
                case "s": case "sec": periodMs = mult * 1000; break;
                case "min": case "m": periodMs = mult * 60000; break;
                case "h": case "hour": periodMs = mult * 3600000; break;
                default: return false;
            }
            return periodMs > 0;
        }
    }
}
