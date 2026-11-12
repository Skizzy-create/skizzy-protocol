using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Skizzy.C2;
using Skizzy.C2.Tactical;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Tactical
{
    /// <summary>One source.tactical row, copied on the main thread (strings are shared, never built here).</summary>
    internal struct FeedRow
    {
        public string Vessel, Name, Type, Situation;
        public double Lat, Lon, Alt, Speed, Heading, Climb;
        public long Track;
        public string Team, Iff, Domain, Class, IdState;
        public double Quality, Age;
        public string State;
        public double Threat;
        public long EngagedBy, MissilesAt;
        public string MissileTarget, Sources;
        // missile columns (null / NaN for other units)
        public string Launcher, LauncherName, Weapon, MslState;
        public double Cost, FiredUt;
    }

    /// <summary>The rows of one view (a team's picture or the god view) in a frame.</summary>
    internal sealed class FeedView
    {
        public string View;
        public bool God;
        public FeedRow[] Rows = new FeedRow[64];
        public int Count;

        public void Add(ref FeedRow r)
        {
            if (Count == Rows.Length) Array.Resize(ref Rows, Rows.Length * 2);
            Rows[Count++] = r;
        }
    }

    /// <summary>
    /// One snapshot handed from the main thread to the feed worker: the rows of every view a due subscription reads, the
    /// engagement log entries they have not had yet, and the per-team totals. Pooled (two frames); 0 B in steady state.
    /// </summary>
    internal sealed class FeedFrame
    {
        /// <summary>0 free (main may fill), 1 pending (posted), 2 being serialised.</summary>
        public int State;
        public double Ut;
        public long TpeSeq;
        public readonly List<FeedView> Views = new List<FeedView>();
        public int ViewCount;
        public TacticalSub[] Subs = new TacticalSub[4];
        public int[] SubView = new int[4];
        public long[] SubFrom = new long[4], SubTo = new long[4];
        public int SubCount;
        public LedgerEntry[] Entries = new LedgerEntry[64];
        public int EntryCount;
        public TeamTotals[] Totals = new TeamTotals[4];
        public int TotalsCount;

        public FeedView View(string view, bool god)
        {
            for (int i = 0; i < ViewCount; i++) if (Views[i].View == view) return Views[i];
            if (ViewCount == Views.Count) Views.Add(new FeedView());
            var v = Views[ViewCount++];
            v.View = view;
            v.God = god;
            v.Count = 0;
            return v;
        }

        public int ViewIndex(string view)
        {
            for (int i = 0; i < ViewCount; i++) if (Views[i].View == view) return i;
            return -1;
        }

        public void AddSub(TacticalSub s, int view, long from, long to)
        {
            if (SubCount == Subs.Length)
            {
                Array.Resize(ref Subs, SubCount * 2);
                Array.Resize(ref SubView, SubCount * 2);
                Array.Resize(ref SubFrom, SubCount * 2);
                Array.Resize(ref SubTo, SubCount * 2);
            }
            Subs[SubCount] = s;
            SubView[SubCount] = view;
            SubFrom[SubCount] = from;
            SubTo[SubCount] = to;
            SubCount++;
        }

        public void AddEntry(LedgerEntry e)
        {
            if (EntryCount == Entries.Length) Array.Resize(ref Entries, Entries.Length * 2);
            Entries[EntryCount++] = e;
        }

        public void SetTotals(IReadOnlyList<TeamTotals> teams)
        {
            if (Totals.Length < teams.Count)
            {
                int n = Totals.Length;
                Array.Resize(ref Totals, Math.Max(teams.Count, n * 2));
            }
            for (int i = 0; i < teams.Count; i++)
            {
                if (Totals[i] == null) Totals[i] = new TeamTotals();
                teams[i].CopyTo(Totals[i]);
            }
            TotalsCount = teams.Count;
        }

        public void Reset()
        {
            ViewCount = 0;
            for (int i = 0; i < SubCount; i++) Subs[i] = null;
            SubCount = 0;
            for (int i = 0; i < EntryCount; i++) Entries[i] = null;
            EntryCount = 0;
            TotalsCount = 0;
        }
    }

    /// <summary>
    /// The source.tactical feed worker (M2): the main thread copies a snapshot into a free <see cref="FeedFrame"/> and posts
    /// it; this background thread serialises one stream.data message per due subscription into pooled buffers and queues
    /// it on the connection. The main thread never serialises. When both frames are busy the main thread skips a sample
    /// (nothing is lost: log entries go out with the next frame).
    /// </summary>
    internal sealed class TacticalFeed
    {
        private readonly HostCore _h;
        private readonly FeedFrame[] _frames = { new FeedFrame(), new FeedFrame() };
        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private FeedFrame _pending;
        private Thread _thread;
        private readonly Dictionary<uint, string> _handles = new Dictionary<uint, string>();

        public long Posted, Skipped, Frames, BytesOut;
        public double LastSerializeMs, MaxSerializeMs, TotalSerializeMs;

        /// <summary>Tests: serialise on the posting thread instead of the worker.</summary>
        public bool Inline { get; set; }

        public TacticalFeed(HostCore h) { _h = h; }

        /// <summary>Main thread: a free frame, or null when the worker still holds both or hasn't taken the last posted one yet (skip this sample).</summary>
        public FeedFrame Acquire()
        {
            // [lead review] A posted frame the worker hasn't taken yet: skip. Filling the other frame and posting it would overwrite
            // _pending, leaking the first frame at State 1 (after two overlaps on a busy PC the feed stopped for good), and dropping
            // a frame instead would lose its engagement log entries (each subscription's log cursor advances when it's added).
            if (!Inline && Volatile.Read(ref _pending) != null) { Skipped++; return null; }
            for (int i = 0; i < _frames.Length; i++)
                if (Volatile.Read(ref _frames[i].State) == 0)
                {
                    _frames[i].Reset();
                    return _frames[i];
                }
            Skipped++;
            return null;
        }

        /// <summary>Main thread: hands a filled frame to the worker.</summary>
        public void Post(FeedFrame f)
        {
            Posted++;
            if (Inline)
            {
                f.State = 2;
                Serialize(f);
                f.Reset();
                Volatile.Write(ref f.State, 0);
                return;
            }
            lock (_gate)
            {
                Volatile.Write(ref f.State, 1);
                _pending = f;
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "skizzy-tactical-feed" };
                    _thread.Start();
                }
            }
            _wake.Set();
        }

        private void Run()
        {
            while (true)
            {
                FeedFrame f;
                if (!_wake.WaitOne(10000))
                {
                    lock (_gate)
                    {
                        if (_pending == null) { _thread = null; return; } // idle: the next Post starts a new worker
                    }
                }
                lock (_gate)
                {
                    f = _pending;
                    _pending = null;
                    if (f != null) Volatile.Write(ref f.State, 2);
                }
                if (f == null) continue;
                try { Serialize(f); }
                catch (Exception e) { _h.Log.Warn("tactical feed: " + e.GetType().Name + ": " + e.Message); }
                f.Reset();
                Volatile.Write(ref f.State, 0);
            }
        }

        private string Handle(uint id)
        {
            if (id == 0) return null;
            string s;
            if (!_handles.TryGetValue(id, out s))
            {
                s = HandleRegistry.VesselHandle(id);
                if (_handles.Count > 8192) _handles.Clear();
                _handles[id] = s;
            }
            return s;
        }

        /// <summary>Worker thread (or inline in tests): one message per subscription in the frame.</summary>
        internal void Serialize(FeedFrame f)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < f.SubCount; i++)
            {
                var sub = f.Subs[i];
                var conn = sub.Session.Conn;
                if (conn == null || !conn.IsOpen) continue;
                var b = Write(f, i);
                sub.Seq++;
                sub.Samples++;
                sub.Bytes += b.Length;
                if (b.Length > sub.LastBytes) sub.LastBytes = b.Length;
                BytesOut += b.Length;
                conn.Send(b);
            }
            Frames++;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            LastSerializeMs = ms;
            TotalSerializeMs += ms;
            if (ms > MaxSerializeMs) MaxSerializeMs = ms;
        }

        /// <summary>The stream.data message of subscription <paramref name="i"/> of the frame, in a pooled buffer.</summary>
        internal ByteBuffer Write(FeedFrame f, int i)
        {
            var sub = f.Subs[i];
            var view = f.Views[f.SubView[i]];
            int want = 512 + view.Count * 300 + f.EntryCount * 260 + f.TotalsCount * 300;
            if (sub.LastBytes + (sub.LastBytes >> 2) > want) want = sub.LastBytes + (sub.LastBytes >> 2);
            var b = ByteBufferPool.Rent(want);
            var w = JsonWriterPool.Rent(b);
            try
            {
                w.BeginObject().Prop("jsonrpc", "2.0").Prop("method", "stream.data").Name("params").BeginObject()
                    .Prop("sub", sub.SubId).Prop("seq", sub.Seq + 1).Prop("ut", f.Ut).Prop("kind", "full").Prop("tpeSeq", f.TpeSeq)
                    .Name("rows").BeginArray();
                for (int r = 0; r < view.Count; r++) WriteRow(w, ref view.Rows[r]);
                w.EndArray();
                long from = f.SubFrom[i], to = f.SubTo[i];
                w.Name("log").BeginArray();
                for (int k = 0; k < f.EntryCount; k++)
                {
                    var e = f.Entries[k];
                    if (e.Seq <= from || e.Seq > to) continue;
                    if (!view.God && !MissileLedger.Involves(e, view.View)) continue;
                    WriteEntry(w, e);
                }
                w.EndArray();
                w.Prop("logSeq", to);
                w.Name("totals").BeginArray();
                for (int k = 0; k < f.TotalsCount; k++)
                {
                    var t = f.Totals[k];
                    if (!view.God && t.Team != view.View) continue;
                    WriteTotals(w, t);
                }
                w.EndArray();
                _h.WriteMeta(w);
                w.EndObject().EndObject();
            }
            finally { JsonWriterPool.Return(w); }
            return b;
        }

        private static void Str(JsonWriter w, string s) { if (s == null) w.Null(); else w.String(s); }

        private static void Num(JsonWriter w, double d) { if (double.IsNaN(d) || double.IsInfinity(d)) w.Null(); else w.Number(d); }

        private static void WriteRow(JsonWriter w, ref FeedRow r)
        {
            w.BeginArray();
            Str(w, r.Vessel);
            Str(w, r.Name);
            Str(w, r.Type);
            Str(w, r.Situation);
            w.String("Kerbin");
            w.Number(r.Lat).Number(r.Lon).Number(r.Alt).Number(r.Speed).Number(r.Heading).Number(r.Climb).Bool(false).Number(0L).Null();
            w.Number(r.Track);
            Str(w, r.Team);
            Str(w, r.Iff);
            Str(w, r.Domain);
            Str(w, r.Class);
            Str(w, r.IdState);
            w.Number(r.Quality).Number(r.Age);
            Str(w, r.State);
            w.Number(r.Threat).Number(r.EngagedBy).Number(r.MissilesAt);
            Str(w, r.MissileTarget);
            Str(w, r.Sources);
            Str(w, r.Launcher);
            Str(w, r.LauncherName);
            Str(w, r.Weapon);
            Str(w, r.MslState);
            Num(w, r.Cost);
            Num(w, r.FiredUt);
            w.EndArray();
        }

        private void WriteEntry(JsonWriter w, LedgerEntry e)
        {
            w.BeginObject().Prop("seq", e.Seq).Prop("ut", e.Ut).Prop("kind", LedgerEntry.KindName(e.Kind));
            if (e.Missile != 0) w.Prop("missile", Handle(e.Missile));
            if (e.Weapon != null) w.Prop("weapon", e.Weapon);
            if (e.Team != null) w.Prop("team", e.Team);
            if (e.Launcher != 0) w.Prop("launcher", Handle(e.Launcher));
            if (e.LauncherName != null) w.Prop("launcherName", e.LauncherName);
            if (e.Target != 0) w.Prop("target", Handle(e.Target));
            if (e.TargetName != null) w.Prop("targetName", e.TargetName);
            if (e.TargetTeam != null) w.Prop("targetTeam", e.TargetTeam);
            if (e.Other != 0) w.Prop("other", Handle(e.Other));
            if (e.OtherName != null) w.Prop("otherName", e.OtherName);
            if (e.OtherTeam != null) w.Prop("otherTeam", e.OtherTeam);
            if (!double.IsNaN(e.Cost)) w.Prop("cost", e.Cost);
            w.Prop("source", e.Source).Prop("text", e.Text).EndObject();
        }

        internal static void WriteTotals(JsonWriter w, TeamTotals t)
        {
            w.BeginObject().Prop("team", t.Team).Prop("fired", t.Fired).Prop("inFlight", t.InFlight).Prop("hits", t.Hits).Prop("misses", t.Misses)
                .Prop("intercepted", t.Intercepted).Prop("destroyed", t.Destroyed).Prop("interceptsMade", t.InterceptsMade).Prop("kills", t.Kills)
                .Prop("spent", t.Spent).Prop("interceptedCost", t.InterceptedCost).Prop("hitCost", t.HitCost).Prop("missedCost", t.MissedCost)
                .Prop("destroyedCost", t.DestroyedCost).Prop("costUnknown", t.CostUnknown).EndObject();
        }

        public static string Ms(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
    }
}
