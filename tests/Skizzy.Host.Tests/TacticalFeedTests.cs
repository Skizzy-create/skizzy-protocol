using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Skizzy.C2;
using Skizzy.C2.Fake;
using Skizzy.C2.Tactical;
using Skizzy.Client;
using Skizzy.Host.Core.Tactical;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Xunit;
using Xunit.Abstractions;

namespace Skizzy.Host.Tests
{
    /// <summary>
    /// M2 missile picture on the host: the tac.* capabilities over FakeBda, the source.tactical feed (missile rows, log
    /// entries, totals; serialised off the main thread), tac.engagement_log, engagement.* events, the main-thread cost per
    /// sample at 150+ missiles and 0 B steady state.
    /// </summary>
    [Collection("perf")]
    public class TacticalFeedTests : IDisposable
    {
        private const string A = "A", B = "B";
        private readonly TestHost _t = new TestHost();
        private readonly FakeBda _bda = new FakeBda();
        private readonly ITestOutputHelper _out;

        public TacticalFeedTests(ITestOutputHelper output) { _out = output; }

        public void Dispose() { _t.Dispose(); }

        private void Attach()
        {
            _t.OnMain(() =>
            {
                var e = _t.Host.C2.Attach(_bda, _bda, "fakebda");
                _bda.Sink = e;
            });
        }

        /// <summary>One picture period now (FakeBda time advances 0.2 s).</summary>
        private void Period(int n = 1)
        {
            for (int i = 0; i < n; i++)
                _t.OnMain(() =>
                {
                    _bda.Step(0.2);
                    _t.Host.Tactical.StepNow();
                });
        }

        private SkizzyClient Human()
        {
            return _t.Connect(new HelloOptions { ClientName = "map", ClientKind = "map", ActorKind = "human" });
        }

        private FakeUnit Plane(string name, double east, double north = 0, string side = A)
        {
            var u = _bda.Add(name, side, UnitClass.Fighter, east, north);
            u.Scripted = true;
            u.S.GuardMode = false;
            return u;
        }

        private FakeUnit Ship(string name, string side, double east, double north = 0)
        {
            var u = _bda.Add(name, side, UnitClass.Ship, east, north);
            u.Scripted = true;
            u.S.GuardMode = false;
            return u;
        }

        private static JsonValue Subscribe(SkizzyClient c, BlockingCollection<JsonValue> q, string view = null)
        {
            c.Notification += (m, p) => { if (m == "stream.data") q.Add(p); };
            var src = JsonValue.Obj();
            if (view != null) src.Set("view", view);
            return c.Call("stream.subscribe", JsonValue.Obj().Set("source", JsonValue.Obj().Set("tactical", src)).Set("rateHz", 10));
        }

        private static JsonValue NextFrame(BlockingCollection<JsonValue> q, Func<JsonValue, bool> until, int timeoutMs = 5000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
                if (q.TryTake(out var f, 200) && until(f)) return f;
            Assert.Fail("no matching stream frame");
            return null;
        }

        [Fact]
        public void Every_registered_tac_capability_has_a_def_and_the_host_starts()
        {
            foreach (var n in new[] { "tac.status", "tac.configure", "tac.summary", "tac.query", "tac.threats", "tac.track.get", "tac.sitrep",
                                      "tac.engagements", "tac.missiles", "tac.engagement_log" })
            {
                Assert.NotNull(_t.Host.Catalog.Find(n));
                Assert.True(_t.Host.HasHandler(n), n);
            }
            // bda.* is left out of this slice (no KSP operations layer yet): no handler, no def
            Assert.False(_t.Host.HasHandler("bda.fire_missile"));
            using (var c = Human())
            {
                Assert.False(c.Call("tac.status").GetBool("attached"));
                Expect.Error(() => c.Call("tac.engagement_log"), ErrorKind.ExecutorUnavailable, "tactical_not_attached");
            }
        }

        [Fact]
        public void Stream_carries_missile_rows_log_entries_and_totals()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            Attach();
            using (var c = Human())
            {
                var q = new BlockingCollection<JsonValue>();
                var sub = Subscribe(c, q);
                var cols = sub.GetStringList("columns");
                foreach (var col in new[] { "vessel", "lat", "lon", "heading", "srfSpeed", "verticalSpeed", "team", "class", "missileTarget", "launcher", "launcherName", "weapon", "missileState", "cost", "firedUt" })
                    Assert.Contains(col, cols);
                int Ci(string n) => cols.IndexOf(n);
                var m = _bda.AddMissile(hornet, kirov, 1, 0, "AGM-84", 900, 250, 250);
                m.S.ClimbMps = -12;
                Period(2);
                var f = NextFrame(q, x => x.GetArr("log").Count > 0);
                var rows = f.GetArr("rows");
                JsonValue row = null;
                for (int i = 0; i < rows.Count; i++) if (rows[i][Ci("vessel")].AsString() == "v:" + m.Id) row = rows[i];
                Assert.NotNull(row);
                Assert.Equal("missile", row[Ci("class")].AsString());
                Assert.Equal(A, row[Ci("team")].AsString());
                Assert.Equal("v:" + hornet.Id, row[Ci("launcher")].AsString());
                Assert.Equal("Hornet 1", row[Ci("launcherName")].AsString());
                Assert.Equal("v:" + kirov.Id, row[Ci("missileTarget")].AsString());
                Assert.Equal("AGM-84", row[Ci("weapon")].AsString());
                Assert.Equal("flying", row[Ci("missileState")].AsString());
                Assert.Equal(1400, row[Ci("cost")].AsDouble());
                Assert.Equal(-12, row[Ci("verticalSpeed")].AsDouble());
                Assert.True(row[Ci("srfSpeed")].AsDouble() > 800);
                var fired = f.GetArr("log")[0];
                Assert.Equal("fired", fired.GetString("kind"));
                Assert.Equal("Team A Hornet 1 fired AGM-84 at Team B Kirov (1,400 funds)", fired.GetString("text"));
                var tot = f.GetArr("totals");
                Assert.Equal(A, tot[0].GetString("team"));
                Assert.Equal(1, tot[0].GetInt("fired"));
                Assert.Equal(1400, tot[0].GetDouble("spent"));

                _t.OnMain(() => _bda.EndMissile(m.Id, MissileFate.Hit, kirov.Id));
                Period(1);
                var f2 = NextFrame(q, x => x.GetArr("log").Count > 0);
                Assert.Equal("hit", f2.GetArr("log")[0].GetString("kind"));
                Assert.True(f2.GetLong("logSeq") >= 2);
                var rows2 = f2.GetArr("rows");
                JsonValue dead = null;
                for (int i = 0; i < rows2.Count; i++) if (rows2[i][Ci("vessel")].AsString() == "v:" + m.Id) dead = rows2[i];
                Assert.NotNull(dead);
                Assert.Equal("hit", dead[Ci("missileState")].AsString());
                Assert.Equal("dead", dead[Ci("state")].AsString());
                Assert.Equal(1, f2.GetArr("totals")[0].GetInt("hits"));
                // the log is not repeated in later frames
                Period(1);
                var f3 = NextFrame(q, x => true);
                Assert.Equal(0, f3.GetArr("log").Count);
                Assert.True(_t.Host.Tactical.Feed.Frames > 0);
            }
        }

        [Fact]
        public void Late_subscriber_gets_the_backlog_in_bounded_frames()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            Attach();
            _t.Host.Tactical.MaxLogPerFrame = 100;
            for (int i = 0; i < 250; i++) _bda.AddMissile(hornet, kirov, 1, i * 0.01, "AIM-9", 500);
            Period(2);
            Assert.Equal(250, _t.Host.Tactical.Ledger.Seq);
            using (var c = Human())
            {
                var q = new BlockingCollection<JsonValue>();
                Subscribe(c, q);
                var got = new List<long>();
                while (got.Count < 250)
                {
                    Period(1);
                    var f = NextFrame(q, x => true);
                    Assert.True(f.GetArr("log").Count <= 100);
                    foreach (var e in f.GetArr("log").Items) got.Add(e.GetLong("seq"));
                }
                Assert.Equal(Enumerable.Range(1, 250).Select(x => (long)x), got);
            }
        }

        [Fact]
        public void Engagement_log_query_pages_polls_filters_and_respects_sides()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var viper = Plane("Viper 1", 100, 0, "C");
            Attach();
            var m1 = _bda.AddMissile(hornet, kirov, 1, 0, "AGM-84", 1000);
            var m2 = _bda.AddMissile(viper, kirov, 99, 0, "AGM-65", 300);
            var sam = _bda.AddMissile(kirov, m1, 59, 0, "SM-2", 800);
            Period(1);
            _t.OnMain(() =>
            {
                _bda.EndMissile(sam.Id, MissileFate.Hit, m1.Id);
                _bda.EndMissile(m1.Id, MissileFate.Destroyed);
                _bda.EndMissile(m2.Id, MissileFate.Missed);
            });
            Period(1);
            using (var c = Human())
            {
                var r = c.Call("tac.engagement_log", JsonValue.Obj().Set("limit", 3));
                Assert.Equal("god", r.GetString("view"));
                Assert.Equal(5, r.GetInt("total"));
                Assert.Contains("intercepted by Team B SM-2 from Kirov", r.GetString("text"));
                Assert.Contains("#team A fired 1", r.GetString("text"));
                var a = r.GetArr("totals").Items.First(x => x.GetString("team") == A);
                Assert.Equal(1, a.GetInt("intercepted"));
                Assert.Equal(1000, a.GetDouble("interceptedCost"));
                var b = r.GetArr("totals").Items.First(x => x.GetString("team") == B);
                Assert.Equal(1, b.GetInt("interceptsMade"));
                Assert.Equal(800, b.GetDouble("spent"));
                // newest first, page back with before
                var j = c.Call("tac.engagement_log", JsonValue.Obj().Set("limit", 2).Set("format", "json"));
                var e = j.GetArr("entries");
                Assert.Equal(2, e.Count);
                Assert.True(e[0].GetLong("seq") > e[1].GetLong("seq"));
                var older = c.Call("tac.engagement_log", JsonValue.Obj().Set("limit", 10).Set("format", "json").Set("before", j.GetLong("cursor")));
                Assert.Equal(3, older.GetArr("entries").Count);
                // poll forward
                var poll = c.Call("tac.engagement_log", JsonValue.Obj().Set("since", 3).Set("format", "json"));
                Assert.Equal(new long[] { 4, 5 }, poll.GetArr("entries").Items.Select(x => x.GetLong("seq")).ToArray());
                // team filter: C fired one missile that missed; it never touched A
                var cl = c.Call("tac.engagement_log", JsonValue.Obj().Set("team", "C").Set("format", "json"));
                Assert.Equal(2, cl.GetInt("total"));
                Expect.Error(() => c.Call("tac.engagement_log", JsonValue.Obj().Set("limit", 51)), ErrorKind.InvalidParams);
            }
            using (var llm = _t.ConnectLlm("commander", A))
            {
                var own = llm.Call("tac.engagement_log", JsonValue.Obj().Set("format", "json"));
                Assert.Equal(A, own.GetString("view"));
                Assert.All(own.GetArr("entries").Items, x => Assert.True(x.GetString("team") == A || x.GetString("targetTeam") == A || x.GetString("otherTeam") == A));
                Expect.Error(() => llm.Call("tac.engagement_log", JsonValue.Obj().Set("view", "god")), ErrorKind.PermissionDenied, "god_view_denied");
                Expect.Error(() => llm.Call("tac.engagement_log", JsonValue.Obj().Set("view", B)), ErrorKind.PermissionDenied, "not_own_side");
            }
        }

        [Fact]
        public void Missiles_and_status_capabilities_show_the_ledger()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            Attach();
            _bda.AddMissile(hornet, kirov, 1, 0, "AGM-84", 1000);
            Period(2);
            using (var c = Human())
            {
                var m = c.Call("tac.missiles", JsonValue.Obj().Set("view", "god"));
                var row = m.GetArr("missiles")[0];
                Assert.Equal("AGM-84", row.GetString("weapon"));
                Assert.Equal("Hornet 1", row.GetString("launcherName"));
                Assert.Equal("flying", row.GetString("state"));
                Assert.Equal(1000, row.GetDouble("cost"));
                Assert.Contains("AGM-84 from Hornet_1", m.GetString("text"));
                var s = c.Call("tac.status");
                Assert.Equal(1, s.GetInt("missilesInFlight"));
                Assert.Equal(5, s.GetDouble("sampleHz"));
                Assert.True(s.GetObj("mainThread").GetLong("samples") >= 2);
                var cfg = c.Call("tac.configure", JsonValue.Obj().Set("sampleHz", 10));
                Assert.Equal(10, cfg.GetDouble("sampleHz"));
                Assert.Equal(5, _t.Host.Tactical.PeriodFixedUpdates);
                Expect.Error(() => c.Call("tac.configure", JsonValue.Obj().Set("sampleHz", 50)), ErrorKind.InvalidParams);
            }
            using (var llm = _t.ConnectLlm("commander", A))
                Expect.Error(() => llm.Call("tac.configure", JsonValue.Obj().Set("sampleHz", 2)), ErrorKind.PermissionDenied, "not_privileged");
        }

        [Fact]
        public void Engagement_events_reach_events_poll()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            Attach();
            var m = _bda.AddMissile(hornet, kirov, 1, 0, "AGM-84");
            Period(1);
            _t.OnMain(() => _bda.EndMissile(m.Id, MissileFate.Missed));
            Period(1);
            using (var c = Human())
            {
                var r = c.Call("events.poll", JsonValue.Obj().Set("cursor", 0).Set("limit", 256).Set("names", JsonValue.Arr().Add("engagement.*")));
                var names = r.GetArr("events").Items.Select(x => x.GetString("name")).ToList();
                Assert.Contains("engagement.fired", names);
                Assert.Contains("engagement.missed", names);
            }
        }

        /// <summary>A raid like the user's: 20 planes, 2 cruise missiles each, a ship firing interceptors; 160 missiles in flight.</summary>
        private void Raid(int interceptors)
        {
            var ship = Ship("Kirov", B, 0);
            for (int k = 0; k < 4; k++) Ship("Escort " + k, B, 2 * k, 3);
            var planes = new List<FakeUnit>();
            for (int i = 0; i < 20; i++) planes.Add(Plane("Hornet " + (i + 1), -60, i - 10));
            var asms = new List<FakeUnit>();
            foreach (var p in planes) for (int k = 0; k < 2; k++) asms.Add(_bda.AddMissile(p, ship, -55, k, "AGM-84", 900, 250));
            for (int i = 0; i < interceptors; i++) _bda.AddMissile(ship, asms[i % asms.Count], -1, 0, "SM-2", 800);
        }

        [Fact]
        public void Main_thread_cost_per_sample_at_160_missiles()
        {
            Raid(120);
            Attach();
            using (var c = Human())
            {
                var q = new BlockingCollection<JsonValue>();
                Subscribe(c, q);
                Period(20); // warm-up
                var tac = _t.Host.Tactical;
                tac.SampleMsMax = 0;
                tac.SampleMsTotal = 0;
                tac.SampleCount = 0;
                tac.SnapshotMsMax = 0;
                var tpeMs = new List<double>();
                var ledgerMs = new List<double>();
                var snapMs = new List<double>();
                for (int i = 0; i < 100; i++)
                {
                    Period(1);
                    tpeMs.Add(tac.Tpe.LastStepMs);
                    ledgerMs.Add(tac.Ledger.LastUpdateMs);
                    snapMs.Add(tac.SnapshotMsLast);
                    if (i % 10 == 0) while (q.TryTake(out _)) { }
                }
                Assert.Equal(160, tac.Ledger.Flying.Count);
                double mean = tac.SampleMsTotal / tac.SampleCount;
                tpeMs.Sort(); ledgerMs.Sort(); snapMs.Sort();
                _out.WriteLine("units " + tac.Tpe.UnitCount + ", missiles " + tac.Ledger.Flying.Count + ", samples " + tac.SampleCount);
                _out.WriteLine("main thread per sample: mean " + mean.ToString("0.000") + " ms, max " + tac.SampleMsMax.ToString("0.000") + " ms");
                _out.WriteLine("  picture period median " + tpeMs[50].ToString("0.000") + " ms, ledger median " + ledgerMs[50].ToString("0.0000") + " ms, snapshot median " + snapMs[50].ToString("0.0000") + " ms");
                _out.WriteLine("feed worker: frames " + tac.Feed.Frames + ", serialize mean " + (tac.Feed.TotalSerializeMs / Math.Max(1, tac.Feed.Frames)).ToString("0.000") + " ms (off the main thread)");
                Assert.True(ledgerMs[50] + snapMs[50] < 1.0, "ledger + snapshot median " + (ledgerMs[50] + snapMs[50]) + " ms");
                Assert.True(mean < 10, "sample mean " + mean + " ms");
            }
        }

        [Fact]
        public void Feed_allocates_nothing_in_steady_state()
        {
            Raid(110);
            Attach();
            using (var c = Human())
            {
                var q = new BlockingCollection<JsonValue>();
                Subscribe(c, q);
                c.Notification += (m, p) => { };
                Period(15);
                long best = long.MaxValue;
                _t.OnMain(() =>
                {
                    var tac = _t.Host.Tactical;
                    for (int i = 0; i < 30; i++) { _bda.Now += 0.2; tac.StepNow(); }
                    for (int attempt = 0; attempt < 5 && best != 0; attempt++)
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 40; i++) { _bda.Now += 0.2; tac.StepNow(); }
                        long d = GC.GetAllocatedBytesForCurrentThread() - before;
                        if (d < best) best = d;
                        Thread.Sleep(30);
                    }
                });
                Assert.Equal(0, best);
                Assert.Equal(150, _t.Host.Tactical.Ledger.Flying.Count);
                Assert.True(_t.Host.Tactical.Feed.Posted > 40);
                while (q.TryTake(out _)) { }
            }
        }

        [Fact]
        public void Feed_serialisation_allocates_nothing_once_warm()
        {
            Raid(110);
            Attach();
            using (var c = Human())
            {
                var q = new BlockingCollection<JsonValue>();
                Subscribe(c, q);
                Period(5);
                var feed = _t.Host.Tactical.Feed;
                // build a frame on the main thread, then serialise it here (as the worker does) many times
                FeedFrame frame = null;
                feed.Inline = false;
                _t.OnMain(() =>
                {
                    frame = new FeedFrame();
                    var tac = _t.Host.Tactical;
                    var sub = tac.FirstSubscription;
                    var v = frame.View("*", true);
                    tac.BuildGodView(v);
                    frame.AddSub(sub, 0, 0, tac.Ledger.Seq);
                    foreach (var e in tac.Ledger.Entries) frame.AddEntry(e);
                    frame.SetTotals(tac.Ledger.Teams);
                });
                Assert.True(frame.Views[0].Count >= 150);
                for (int i = 0; i < 20; i++) Skizzy.Protocol.Json.ByteBufferPool.Return(feed.Write(frame, 0));
                long best = long.MaxValue;
                for (int attempt = 0; attempt < 5 && best != 0; attempt++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 50; i++) Skizzy.Protocol.Json.ByteBufferPool.Return(feed.Write(frame, 0));
                    long d = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (d < best) best = d;
                }
                Assert.Equal(0, best);
            }
        }
    }
}
