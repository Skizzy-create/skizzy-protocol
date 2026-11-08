using System;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Caps
{
    /// <summary>Host counters for host.stats (02 §7).</summary>
    public sealed class HostStats
    {
        public long Calls;
        public long Errors;
        public readonly TimingWindow FrameMs = new TimingWindow();
        public readonly TimingWindow UpdateMs = new TimingWindow();
        public readonly TimingWindow FixedMs = new TimingWindow();
        public readonly TimingWindow CallMs = new TimingWindow();
        public readonly TimingWindow LogSampleMs = new TimingWindow();
        public readonly TimingWindow StreamSampleMs = new TimingWindow();
        public readonly TimingWindow ShotMainMs = new TimingWindow(128);
        public readonly TimingWindow ShotTotalMs = new TimingWindow(128);
        public long Shots;
        public string LastShotPath;

        public static JsonValue Timing(TimingWindow t)
        {
            t.Percentiles(out double p50, out double p99, out double max);
            return JsonValue.Obj().Set("p50", Math.Round(p50, 4)).Set("p99", Math.Round(p99, 4)).Set("max", Math.Round(max, 4)).Set("n", t.Count);
        }
    }

    /// <summary>A sampled or on-change subscription over a query selector (03 §5, spec 0.2 §6.5).</summary>
    public sealed class StreamSub
    {
        public int Id;
        public string SubId;
        public Session Session;
        public QueryPlan Plan;
        public bool OnChange;
        public double Deadband;
        public double RateHz;
        public double NextUt = double.NegativeInfinity;
        public long Seq;
        public bool SentFull;
        public long Samples;
        public long Bytes;
        public int LastBytes;
        public JsonValue Source;
    }

    /// <summary>
    /// Streams: built on the main thread, sampled in FixedUpdate at game-time rates, serialised straight into a pooled
    /// buffer (no DOM, no allocation per sample) and queued on the session's writer.
    /// </summary>
    public sealed class StreamManager
    {
        private readonly HostCore _host;
        private volatile StreamSub[] _subs = new StreamSub[0];
        private readonly object _gate = new object();
        private int _next;

        public StreamManager(HostCore host) { _host = host; }

        public int Count => _subs.Length;

        public void HandleProtocol(Session s, JsonValue id, string method, JsonValue p)
        {
            switch (method)
            {
                case "stream.subscribe": Subscribe(s, id, p); break;
                case "stream.unsubscribe":
                    {
                        var sub = Find(s, p.GetString("sub"));
                        Remove(sub);
                        _host.SendResult(s, id, JsonValue.Obj().Set("sub", sub.SubId).Set("ok", true).Set("samples", sub.Samples));
                        break;
                    }
                case "stream.modify":
                    {
                        var sub = Find(s, p.GetString("sub"));
                        if (p.Has("rateHz")) sub.RateHz = ClampRate(p.GetDouble("rateHz", sub.RateHz));
                        if (p.Has("deadband")) sub.Deadband = Math.Max(0, p.GetDouble("deadband"));
                        sub.NextUt = double.NegativeInfinity;
                        _host.SendResult(s, id, JsonValue.Obj().Set("sub", sub.SubId).Set("effectiveRateHz", sub.RateHz));
                        break;
                    }
                case "stream.list":
                    {
                        var arr = JsonValue.Arr();
                        foreach (var sub in _subs)
                            if (sub.Session == s)
                                arr.Add(JsonValue.Obj().Set("sub", sub.SubId).Set("mode", sub.OnChange ? "onchange" : "sampled")
                                    .Set("rateHz", sub.RateHz).Set("rows", sub.Plan.Rows.Count).Set("samples", sub.Samples).Set("bytes", sub.Bytes));
                        _host.SendResult(s, id, JsonValue.Obj().Set("streams", arr).Set("total", arr.Count));
                        break;
                    }
            }
        }

        private static double ClampRate(double r) { return r < 0.1 ? 0.1 : r > 50 ? 50 : r; }

        private StreamSub Find(Session s, string subId)
        {
            foreach (var sub in _subs) if (sub.Session == s && sub.SubId == subId) return sub;
            throw SkizzyError.NotFound(subId ?? "", "unknown", "Unknown subscription; list yours with stream.list.");
        }

        private void Subscribe(Session s, JsonValue id, JsonValue p)
        {
            int mine = 0;
            foreach (var x in _subs) if (x.Session == s) mine++;
            if (mine >= _host.Settings.Limits.MaxSubscriptions)
                throw SkizzyError.Busy(1000, "subscription limit " + _host.Settings.Limits.MaxSubscriptions + " reached");
            var source = p.GetObj("source");
            var q = source != null ? source.GetObj("query") : null;
            if (q == null) throw SkizzyError.InvalidParam("/source/query", "M0 streams take source.query {select, fields, aggregate, reduce} (tactical sources arrive in M2)");
            if (q.GetArr("fields") == null || q.GetArr("fields").Count == 0) throw SkizzyError.InvalidParam("/source/query/fields", "fields are required");
            var spec = QuerySpec.FromParams(q);
            string mode = p.GetString("mode", "sampled");
            if (mode != "sampled" && mode != "onchange" && mode != "delta") throw SkizzyError.InvalidParam("/mode", "mode must be sampled or onchange");
            var sub = new StreamSub
            {
                Id = Interlocked.Increment(ref _next),
                Session = s,
                OnChange = mode != "sampled",
                Deadband = Math.Max(0, p.GetDouble("deadband", p.GetObj("thresholds")?.GetDouble("default", 0) ?? 0)),
                RateHz = ClampRate(p.GetDouble("rateHz", 1)),
                Source = source,
            };
            sub.SubId = "sub:" + sub.Id;
            sub.Plan = new QueryPlan(_host, spec);
            _host.Dispatcher.EnqueueMain(new Job
            {
                Action = () =>
                {
                    try
                    {
                        var meta = _host.Meta;
                        if (meta.Scene != "FLIGHT")
                            throw SkizzyError.Unavailable(meta.Scene, new[] { "FLIGHT" }, meta.SceneTransition ? "transition" : "wrong_scene");
                        sub.Plan.Build();
                        sub.NextUt = meta.Ut + 1.0 / sub.RateHz;
                        var rows = JsonValue.Arr();
                        foreach (var r in sub.Plan.Rows)
                        {
                            if (!sub.Plan.ReadRow(r)) continue;
                            var row = JsonValue.Arr();
                            row.Add(r.Vessel.Handle).Add(r.Vessel.Name);
                            if (r.Part != null) row.Add(r.Part.Handle);
                            if (r.Module != null) row.Add(r.Module.Handle);
                            for (int f = 0; f < r.Values.Length; f++) { row.Add(r.Values[f].ToJson()); r.Last[f] = r.Values[f]; }
                            r.HasLast = true;
                            rows.Add(row);
                        }
                        sub.SentFull = true;
                        lock (_gate)
                        {
                            var n = new StreamSub[_subs.Length + 1];
                            Array.Copy(_subs, n, _subs.Length);
                            n[_subs.Length] = sub;
                            _subs = n;
                        }
                        lock (s.Subscriptions) s.Subscriptions.Add(sub.Id);
                        var res = JsonValue.Obj().Set("sub", sub.SubId).Set("effectiveRateHz", sub.RateHz).Set("mode", sub.OnChange ? "onchange" : "sampled")
                            .Set("seq", 0).Set("kind", "full").Set("columns", JsonValue.ArrOf(sub.Plan.Columns)).Set("rows", rows);
                        if (sub.Plan.UnknownModuleFields.Count > 0)
                            res.Set("warnings", JsonValue.ArrOf(new List<string>(sub.Plan.UnknownModuleFields).ToArray()));
                        ThreadPool.QueueUserWorkItem(_ => _host.SendResult(s, id, res));
                    }
                    catch (SkizzyError e) { _host.SendError(s.Conn, id, e); }
                }
            });
        }

        public void Remove(StreamSub sub)
        {
            lock (_gate)
            {
                int i = Array.IndexOf(_subs, sub);
                if (i < 0) return;
                var n = new StreamSub[_subs.Length - 1];
                Array.Copy(_subs, 0, n, 0, i);
                Array.Copy(_subs, i + 1, n, i, _subs.Length - i - 1);
                _subs = n;
            }
            lock (sub.Session.Subscriptions) sub.Session.Subscriptions.Remove(sub.Id);
        }

        public void RemoveSession(Session s)
        {
            foreach (var sub in _subs) if (sub.Session == s) Remove(sub);
        }

        public void Clear()
        {
            lock (_gate) _subs = new StreamSub[0];
        }

        /// <summary>FixedUpdate: samples due streams.</summary>
        // skizzy:hot StreamManager.Sample
        public void Sample()
        {
            var subs = _subs;
            if (subs.Length == 0) return;
            long t0 = Clock.Ticks;
            var meta = _host.Meta;
            for (int i = 0; i < subs.Length; i++)
            {
                var sub = subs[i];
                if (meta.Ut < sub.NextUt) continue;
                double period = 1.0 / sub.RateHz;
                sub.NextUt = sub.NextUt + period < meta.Ut ? meta.Ut + period : sub.NextUt + period;
                var c = sub.Session.Conn;
                if (c == null || !c.IsOpen) continue;
                if (c.QueuedBytes > 2 * 1024 * 1024) continue; // slow client: skip (coalesce to the latest state)
                sub.Plan.EnsureCurrent();
                SampleOne(sub, ref meta);
            }
            _host.Stats.StreamSampleMs.Add(Clock.SinceMs(t0));
        }

        // skizzy:hot StreamManager.SampleOne
        private void SampleOne(StreamSub sub, ref MetaState meta)
        {
            var plan = sub.Plan;
            int want = 128 + plan.Rows.Count * (24 + 16 * plan.Spec.Fields.Length);
            int last = sub.LastBytes;
            if (last + (last >> 2) > want) want = last + (last >> 2);
            var b = ByteBufferPool.Rent(want);
            var w = JsonWriterPool.Rent(b);
            int written = 0;
            try
            {
                w.BeginObject().Prop("jsonrpc", "2.0").Prop("method", "stream.data").Name("params").BeginObject()
                    .Prop("sub", sub.SubId).Prop("seq", sub.Seq + 1).Prop("ut", meta.Ut).Prop("kind", sub.OnChange ? "delta" : "full")
                    .Name("rows").BeginArray();
                var rows = plan.Rows;
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (!plan.ReadRow(row)) continue;
                    if (sub.OnChange)
                    {
                        bool changed = !row.HasLast;
                        for (int f = 0; f < row.Values.Length && !changed; f++)
                            if (!row.Values[f].SameAs(ref row.Last[f], sub.Deadband)) changed = true;
                        if (!changed) continue;
                        for (int f = 0; f < row.Values.Length; f++) row.Last[f] = row.Values[f];
                        row.HasLast = true;
                    }
                    w.BeginArray();
                    plan.WriteIdentity(w, row);
                    for (int f = 0; f < row.Values.Length; f++) QueryPlan.WriteValue(w, ref row.Values[f]);
                    w.EndArray();
                    written++;
                }
                w.EndArray();
                _host.WriteMeta(w);
                w.EndObject().EndObject();
            }
            finally { JsonWriterPool.Return(w); }
            if (written == 0 && sub.OnChange)
            {
                ByteBufferPool.Return(b);
                return;
            }
            sub.Seq++;
            sub.Samples++;
            sub.Bytes += b.Length;
            if (b.Length > sub.LastBytes) sub.LastBytes = b.Length;
            sub.Session.Conn.Send(b);
        }
    }
}
