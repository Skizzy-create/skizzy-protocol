using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Skizzy.C2;
using Skizzy.C2.Fake;
using Skizzy.Host.Core;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Xunit;

namespace Skizzy.Host.Tests
{
    /// <summary>
    /// The host wiring of the C2 engine (src/Skizzy.C2) against FakeBda: c2.* capabilities, the 5 Hz C2 slot,
    /// order.* / log.* events, FirstLaunch pause, kill switch and scene change. No ticks before a command (acceptance 1).
    /// </summary>
    [Collection("perf")]
    public class C2WiringTests : IDisposable
    {
        private const string Blue = "Blue", Red = "Red";
        private readonly TestHost _t = new TestHost();
        private readonly FakeBda _bda = new FakeBda();
        private C2Engine _engine;
        public void Dispose() { _t.Dispose(); }

        private void Attach()
        {
            _t.OnMain(() =>
            {
                _engine = _t.Host.C2.Attach(_bda, _bda, "fakebda");
                _bda.Sink = _engine;
            });
        }

        private List<JsonValue> Events(Skizzy.Client.SkizzyClient c, string glob)
        {
            var r = c.Call("events.poll", JsonValue.Obj().Set("cursor", 0).Set("limit", 256).Set("names", JsonValue.Arr().Add(glob)));
            var arr = r.GetArr("events");
            var l = new List<JsonValue>();
            for (int i = 0; i < arr.Count; i++) l.Add(arr[i]);
            return l;
        }

        [Fact]
        public void Without_the_binding_c2_calls_fail_and_nothing_ticks()
        {
            using (var llm = _t.ConnectLlm())
            {
                var e = Expect.Error(() => llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "attack_nearest").Set("units", JsonValue.Arr().Add("v:1"))),
                    ErrorKind.ExecutorUnavailable, "c2_not_attached");
                Assert.Contains("binding", e.Hint);
                Assert.False(_t.Host.C2.WantsTicks);
                Assert.Equal(0, _t.Host.C2.Ticks);
                var stats = llm.Call("host.stats").GetObj("c2");
                Assert.False(stats.GetBool("attached"));
            }
            foreach (var n in new[] { "c2.order.issue", "c2.order.cancel", "c2.doctrine.set", "c2.doctrine.unset", "c2.doctrine.effective",
                                      "c2.unit.status", "c2.unit.release", "c2.vip.set", "c2.side.managed" })
            {
                Assert.NotNull(_t.Host.Catalog.Find(n));
                Assert.True(_t.Host.HasHandler(n), n);
            }
        }

        [Fact]
        public void Move_order_ticks_at_5_hz_only_after_a_command_and_publishes_order_events()
        {
            var f1 = _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = _bda.Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            Attach();
            Thread.Sleep(400);
            Assert.Equal(0, _t.Host.C2.Ticks); // attached but nothing commanded: no C2 cost
            using (var llm = _t.ConnectLlm())
            {
                var r = llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "move_to")
                    .Set("units", JsonValue.Arr().Add("Cobra 1").Add("v:" + f2.Id))
                    .Set("point", JsonValue.Obj().Set("lat", 0.0).Set("lon", 0.5)).Set("altitudeM", 3000));
                Assert.Equal("o:1", r.GetString("order"));
                Assert.Equal("move_to", r.GetString("type"));
                Assert.Equal(2, r.GetInt("accepted"));
                Assert.Equal(Blue, r.GetString("side"));
                var accepted = Events(llm, "order.*");
                Assert.Contains(accepted, ev => ev.GetString("name") == "order.accepted" && ev.GetObj("data").GetString("order") == "o:1");

                long t0 = _t.Host.C2.Ticks;
                Thread.Sleep(1000);
                long n = _t.Host.C2.Ticks - t0;
                Assert.InRange(n, 3, 8); // 5 Hz from the 50 Hz FakeKsp physics loop

                var st = llm.Call("c2.unit.status", JsonValue.Obj().Set("unit", "v:" + f1.Id));
                Assert.Equal("Cobra 1", st.GetString("name"));
                Assert.Equal("move_to", st.GetObj("movement").GetString("type"));
                Assert.Equal("executing", st.GetObj("movement").GetString("status"));

                var stats = llm.Call("host.stats").GetObj("c2");
                Assert.True(stats.GetBool("attached"));
                Assert.True(stats.GetBool("engaged"));
                Assert.Equal(1, stats.GetInt("activeOrders"));

                var c = llm.Call("c2.order.cancel", JsonValue.Obj().Set("order", "o:1"));
                Assert.Equal("cancelled", c.GetString("status"));
                Expect.Eventually(() => Events(llm, "order.cancelled").Count >= 1, 3000, "order.cancelled event");
                Expect.Error(() => llm.Call("c2.order.cancel", JsonValue.Obj().Set("order", "o:1")), ErrorKind.PreconditionFailed, "already_cancelled");
                Expect.Error(() => llm.Call("c2.order.cancel", JsonValue.Obj().Set("order", "o:99")), ErrorKind.NotFound);
            }
        }

        [Fact]
        public void Refusals_and_side_checks_map_to_protocol_errors()
        {
            var b1 = _bda.Add("Bandit 1", Red, UnitClass.Fighter, 20, 0);
            _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Attach();
            using (var llm = _t.ConnectLlm(side: Blue))
            {
                var e = Expect.Error(() => llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "move_to")
                    .Set("units", JsonValue.Arr().Add("Bandit 1")).Set("point", JsonValue.Obj().Set("lat", 0.0).Set("lon", 0.1))),
                    ErrorKind.PermissionDenied, "not_own_side");
                Assert.Equal("NOT_OWN_SIDE", e.Extra.GetString("code"));
                Expect.Error(() => llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "attack_nearest").Set("side", Red)
                    .Set("units", JsonValue.Arr().Add("v:" + b1.Id))), ErrorKind.PermissionDenied, "not_own_side");
                Expect.Error(() => llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "move_to").Set("units", JsonValue.Arr().Add("Nobody"))
                    .Set("point", JsonValue.Obj().Set("lat", 0.0).Set("lon", 0.1))), ErrorKind.NotFound);
                Expect.Error(() => llm.Call("c2.side.managed", JsonValue.Obj().Set("side", Red).Set("managed", true)), ErrorKind.PermissionDenied, "not_own_side");
            }
            Assert.Equal(0, _t.Host.C2.Ticks); // refusals command nothing
        }

        [Fact]
        public void Doctrine_set_effective_and_locks()
        {
            var f1 = _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Attach();
            using (var llm = _t.ConnectLlm(side: Blue))
            {
                llm.Call("c2.doctrine.set", JsonValue.Obj().Set("level", "side")
                    .Set("values", JsonValue.Obj().Set("weaponsControl", "tight")).Set("lock", JsonValue.Arr().Add("weaponsControl")));
                Expect.Eventually(() => _t.Host.C2.Ticks >= 1, 3000, "first tick after the doctrine change");
                Expect.Error(() => llm.Call("c2.doctrine.set", JsonValue.Obj().Set("level", "unit").Set("target", "Cobra 1")
                    .Set("values", JsonValue.Obj().Set("weaponsControl", "free"))), ErrorKind.PreconditionFailed, "locked_by:side");
                Expect.Error(() => llm.Call("c2.doctrine.set", JsonValue.Obj().Set("level", "side")
                    .Set("values", JsonValue.Obj().Set("noSuchKey", 1))), ErrorKind.InvalidParams, "doctrine");
                var eff = llm.Call("c2.doctrine.effective", JsonValue.Obj().Set("unit", "v:" + f1.Id)).GetArr("doctrine");
                JsonValue wc = null;
                for (int i = 0; i < eff.Count; i++) if (eff[i].GetString("key") == "weaponsControl") wc = eff[i];
                Assert.NotNull(wc);
                Assert.Equal("side:Blue", wc.GetString("from"));
                Assert.True(wc.GetBool("locked"));
                llm.Call("c2.doctrine.unset", JsonValue.Obj().Set("level", "side").Set("keys", JsonValue.Arr().Add("weaponsControl")));
                Expect.Eventually(() =>
                {
                    var e2 = llm.Call("c2.doctrine.effective", JsonValue.Obj().Set("unit", "Cobra 1")).GetArr("doctrine");
                    for (int i = 0; i < e2.Count; i++) if (e2[i].GetString("key") == "weaponsControl") return e2[i].GetString("from") == "default";
                    return false;
                }, 3000, "weaponsControl back to default");
                var vip = llm.Call("c2.vip.set", JsonValue.Obj().Set("unit", "Cobra 1"));
                Assert.True(vip.GetBool("vip"));
            }
        }

        [Fact]
        public void First_launch_publishes_log_event_and_pauses_the_game()
        {
            var f1 = _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = _bda.Add("Bandit 1", Red, UnitClass.Fighter, 15, 0);
            Attach();
            using (var llm = _t.ConnectLlm(side: Blue))
            {
                Assert.True(llm.Call("c2.side.managed", JsonValue.Obj().Set("managed", true)).GetBool("managed"));
                _t.OnMain(() => _engine.SetSideManaged(Red, true));
                Expect.Eventually(() => _t.Host.C2.Ticks >= 1, 3000, "first tick");
                _t.OnMain(() => _engine.OnMissileFired(b1.Id, 500, f1.Id));
                Expect.Eventually(() => Events(llm, "log.first_launch").Count >= 1, 3000, "log.first_launch event");
                var ev = Events(llm, "log.first_launch")[0];
                Assert.Equal("weapons", ev.GetString("category"));
                var data = ev.GetObj("data");
                Assert.Equal(Red, data.GetString("side"));
                Assert.True(data.GetBool("pauseRequested"));
                Assert.True(data.GetBool("paused"));
                Assert.Contains(_t.World.PauseRequests, r => r.Contains("Red"));
                Expect.Eventually(() => Events(llm, "sim.paused").Count >= 1, 3000, "sim.paused event");
                Assert.Equal(1, _t.Host.C2.Pauses);
            }
        }

        [Fact]
        public void Kill_switch_cancels_orders_releases_units_and_stops_ticking()
        {
            _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Attach();
            using (var llm = _t.ConnectLlm(side: Blue))
            {
                llm.Call("c2.side.managed", JsonValue.Obj().Set("managed", true));
                llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "move_to").Set("units", JsonValue.Arr().Add("Cobra 1"))
                    .Set("point", JsonValue.Obj().Set("lat", 0.0).Set("lon", 0.5)));
                llm.Call("host.kill_switch", JsonValue.Obj().Set("action", "engage").Set("reason", "test"));
                Expect.Eventually(() => !_t.Host.C2.Engaged, 3000, "C2 idle after the kill switch");
                Order o = null;
                bool managed = true;
                _t.OnMain(() => { o = _engine.GetOrder(1); managed = _engine.IsSideManaged(Blue); });
                Assert.Equal(OrderStatus.Cancelled, o.Status);
                Assert.False(managed);
                Assert.True(Events(llm, "order.cancelled").Count >= 1);
                Expect.Error(() => llm.Call("c2.order.issue", JsonValue.Obj().Set("type", "attack_nearest").Set("units", JsonValue.Arr().Add("Cobra 1"))),
                    ErrorKind.KillSwitchActive);
                long ticks = _t.Host.C2.Ticks;
                Thread.Sleep(500);
                Assert.Equal(ticks, _t.Host.C2.Ticks);
            }
        }

        [Fact]
        public void Scene_change_detaches_the_engine()
        {
            _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Attach();
            string reason = null;
            _t.Host.C2.Detached += r => reason = r;
            Assert.True(_t.Host.C2.Attached);
            _t.World.ChangeScene("SPACECENTER");
            Expect.Eventually(() => !_t.Host.C2.Attached, 3000, "engine detached");
            Assert.Equal("scene_change", reason);
            using (var llm = _t.ConnectLlm(side: Blue))
                Assert.True(Events(llm, "host.c2_detached").Count >= 1);
        }

        [Fact]
        public void Event_names_cover_every_report_kind()
        {
            foreach (C2EventKind k in Enum.GetValues(typeof(C2EventKind)))
            {
                string n = C2Host.EventName(k);
                Assert.True(n.StartsWith("order.", StringComparison.Ordinal) || n.StartsWith("log.", StringComparison.Ordinal), n);
                if (k.ToString().StartsWith("Order", StringComparison.Ordinal)) Assert.StartsWith("order.", n);
            }
            Assert.Equal("order.accepted", C2Host.EventName(C2EventKind.OrderAccepted));
            Assert.Equal("order.resumed", C2Host.EventName(C2EventKind.OrderResumed));
            Assert.Equal("log.first_launch", C2Host.EventName(C2EventKind.FirstLaunch));
            Assert.Equal("log.info", C2Host.EventName(C2EventKind.Info));
        }
    }
}
