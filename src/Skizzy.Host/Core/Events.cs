using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Util;

namespace Skizzy.Host.Core
{
    public sealed class HostEvent
    {
        public long Cursor;
        public string Name;
        public string Category;   // contact | weapons | unit | order | mission | c2 | comms | system (03 §7)
        public double Ut;
        public long Frame;
        public string WallUtc;
        public string Summary;
        public JsonValue Data;

        public JsonValue ToJson()
        {
            var o = JsonValue.Obj().Set("cursor", Cursor).Set("name", Name).Set("category", Category)
                .Set("ut", Math.Round(Ut, 3)).Set("frame", Frame).Set("wallUtc", WallUtc).Set("summary", Summary);
            if (Data != null) o.Set("data", Data);
            return o;
        }

        /// <summary>One compact line: "#123 ut=1203.4 vessel.destroyed DDG-2 destroyed".</summary>
        public string ToCompact()
        {
            return "#" + Cursor + " ut=" + Inv.S(Math.Round(Ut, 1)) + " " + Name + " " + Summary;
        }
    }

    /// <summary>
    /// Host event ring (03 §3: ≥ 8192). Any thread may append; readers poll by cursor or wait with a deadline.
    /// Waiters are continuations, not blocked threads.
    /// </summary>
    public sealed class EventRing
    {
        private sealed class Waiter
        {
            public long After;
            public string[] Names;
            public int Max;
            public Action<List<HostEvent>, long> Done;
            public Timer Timer;
            public int Fired;
        }

        private readonly HostEvent[] _ring;
        private long _next = 1;      // cursor of the next event
        private readonly List<Waiter> _waiters = new List<Waiter>();
        public Func<double> UtSource;
        public Func<long> FrameSource;
        public Action<HostEvent> Listeners;

        public EventRing(int capacity = 8192) { _ring = new HostEvent[capacity]; }

        public long LastCursor { get { lock (_ring) return _next - 1; } }
        public int Capacity => _ring.Length;

        public long Emit(string name, string category, string summary, JsonValue data = null)
        {
            var e = new HostEvent
            {
                Name = name,
                Category = category,
                Summary = CompactText.SafeName(summary ?? "", 160),
                Data = data,
                WallUtc = Clock.WallUtcIso(),
            };
            try { e.Ut = UtSource != null ? UtSource() : 0; e.Frame = FrameSource != null ? FrameSource() : 0; }
            catch (Exception) { }
            List<Waiter> fire = null;
            lock (_ring)
            {
                e.Cursor = _next++;
                _ring[e.Cursor % _ring.Length] = e;
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    var w = _waiters[i];
                    if (e.Cursor > w.After && Matches(e.Name, w.Names))
                    {
                        _waiters.RemoveAt(i);
                        (fire ?? (fire = new List<Waiter>())).Add(w);
                    }
                }
            }
            if (fire != null)
            {
                // Let a burst of events in the same frame land before answering (e.g. split + destroyed).
                foreach (var w in fire) ThreadPool.QueueUserWorkItem(FireLater, w);
            }
            try { Listeners?.Invoke(e); } catch (Exception) { }
            return e.Cursor;
        }

        private void FireLater(object o)
        {
            Thread.Sleep(30);
            Fire((Waiter)o);
        }

        private void Fire(Waiter w)
        {
            if (Interlocked.Exchange(ref w.Fired, 1) != 0) return;
            w.Timer?.Dispose();
            var list = new List<HostEvent>();
            long next = Poll(w.After, w.Names, w.Max, list, out _);
            w.Done(list, next);
        }

        /// <summary>
        /// Copies events with cursor &gt; <paramref name="after"/> matching <paramref name="names"/> (globs) into
        /// <paramref name="into"/>, oldest first. Returns the cursor to pass next time. <paramref name="lost"/> is true
        /// when the ring has moved past <paramref name="after"/> (resync required).
        /// </summary>
        public long Poll(long after, string[] names, int limit, List<HostEvent> into, out bool lost)
        {
            lock (_ring)
            {
                long oldest = Math.Max(1, _next - _ring.Length);
                lost = after > 0 && after + 1 < oldest;
                long start = Math.Max(after + 1, oldest);
                long last = after < oldest - 1 ? oldest - 1 : after;
                for (long c = start; c < _next && into.Count < limit; c++)
                {
                    var e = _ring[c % _ring.Length];
                    last = c;
                    if (e != null && Matches(e.Name, names)) into.Add(e);
                }
                if (into.Count < limit) last = _next - 1;
                return last;
            }
        }

        /// <summary>The most recent <paramref name="limit"/> matching events.</summary>
        public long Recent(string[] names, int limit, List<HostEvent> into)
        {
            lock (_ring)
            {
                long oldest = Math.Max(1, _next - _ring.Length);
                for (long c = _next - 1; c >= oldest && into.Count < limit; c--)
                {
                    var e = _ring[c % _ring.Length];
                    if (e != null && Matches(e.Name, names)) into.Add(e);
                }
                into.Reverse();
                return _next - 1;
            }
        }

        /// <summary>Waits for events after <paramref name="after"/> (returns immediately if some exist).</summary>
        public void Wait(long after, string[] names, int max, int timeoutMs, Action<List<HostEvent>, long> done)
        {
            var w = new Waiter { After = after, Names = names, Max = max, Done = done };
            bool immediate;
            lock (_ring)
            {
                immediate = false;
                long oldest = Math.Max(1, _next - _ring.Length);
                for (long c = Math.Max(after + 1, oldest); c < _next; c++)
                {
                    var e = _ring[c % _ring.Length];
                    if (e != null && Matches(e.Name, names)) { immediate = true; break; }
                }
                if (!immediate && timeoutMs > 0)
                {
                    _waiters.Add(w);
                    w.Timer = new Timer(OnTimeout, w, timeoutMs, Timeout.Infinite);
                }
            }
            if (immediate || timeoutMs <= 0) Fire(w);
        }

        private void OnTimeout(object o)
        {
            var w = (Waiter)o;
            lock (_ring) _waiters.Remove(w);
            Fire(w);
        }

        /// <summary>Completes every pending wait now (session closed, host stopping).</summary>
        public void ReleaseAll()
        {
            List<Waiter> all;
            lock (_ring)
            {
                all = new List<Waiter>(_waiters);
                _waiters.Clear();
            }
            foreach (var w in all) Fire(w);
        }

        public int WaiterCount { get { lock (_ring) return _waiters.Count; } }

        public static bool Matches(string name, string[] globs)
        {
            if (globs == null || globs.Length == 0) return true;
            foreach (var g in globs) if (Glob.IsMatch(name, g)) return true;
            return false;
        }

        public static string FormatCompact(List<HostEvent> events, long next, bool lost)
        {
            var sb = new StringBuilder();
            sb.Append("#events n=").Append(events.Count).Append(" next=").Append(next);
            if (lost) sb.Append(" RESYNC (ring moved past your cursor)");
            sb.Append('\n');
            foreach (var e in events) sb.Append(e.ToCompact()).Append('\n');
            return sb.ToString();
        }
    }
}
