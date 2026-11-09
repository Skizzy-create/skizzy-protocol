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

                // One tick per 10 physics steps (5 Hz at KSP's 50 Hz), counted against the FakeKsp FixedUpdates actually run,
                // so a busy machine that slows the physics loop does not fail the check.
                long t0 = 0, f0 = 0, t1 = 0, f1n = 0;
                _t.OnMain(() => { t0 = _t.Host.C2.Ticks; f0 = _t.Sim.Runner.FixedUpdates; });
                Thread.Sleep(1000);
                _t.OnMain(() => { t1 = _t.Host.C2.Ticks; f1n = _t.Sim.Runner.FixedUpdates; });
                long n = t1 - t0, steps = f1n - f0;
                Assert.True(steps >= 10, "physics steps " + steps);
                Assert.InRange(n, steps / 10 - 1, steps / 10 + 1);

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

        /// <summary>A minimal IC2EngineHost over C2Host, as the BDArmory binding's HostSlot does it.</summary>
        private sealed class TestSlot : IC2EngineHost
        {
            private readonly C2Host _c2;
            public TestSlot(C2Host c2) { _c2 = c2; }
            public string Name => "host";
            public bool Ticking => _c2.WantsTicks;
            public void Tick() { _c2.Tick(); }
            public void Detach(string reason) { _c2.Detach(reason); }
        }

        /// <summary>The C2 window's path: C2Runtime adopting the host's engine, with the host's audited console interposed.</summary>
        private IC2Console WindowConsole(out C2Runtime rt)
        {
            var runtime = new C2Runtime();
            var binding = new FakeBinding(_bda);
            IC2Console console = null;
            _t.OnMain(() =>
            {
                _engine = _t.Host.C2.Attach(_bda, _bda, "fakebda");
                runtime.Adopt(binding, _engine, new TestSlot(_t.Host.C2));
                runtime.SetConsole(_t.Host.C2.WrapConsole(runtime));
                console = runtime.Console;
            });
            rt = runtime;
            return console;
        }

        private string EffectiveFrom(uint unit, string key)
        {
            string from = null;
            _t.OnMain(() =>
            {
                foreach (var l in _engine.ExplainDoctrine(unit)) if (l.Key == key) from = l.From + "=" + l.Value;
            });
            return from;
        }

        [Fact]
        public void Kill_switch_unmanages_window_managed_sides_and_resets_window_doctrine()
        {
            var f1 = _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = _bda.Add("Cobra 2", Blue, UnitClass.Fighter, 0, 3);
            var b1 = _bda.Add("Bandit 1", Red, UnitClass.Fighter, 60, 0);
            C2Runtime rt;
            var console = WindowConsole(out rt);
            using (var llm = _t.ConnectLlm(side: Blue))
            {
                // What the C2 window does: Advance (manages the side + side doctrine) and weapons Hold/Tight per unit,
                // straight to the engine through the runtime, never through c2.side.managed.
                _t.OnMain(() =>
                {
                    console.SetSideManaged(Blue, true);
                    Assert.Empty(console.SetDoctrine(DoctrineLevel.Side, Blue, new Dictionary<string, object> { { "advanceOnContact", false } }));
                    Assert.Empty(console.SetDoctrine(DoctrineLevel.Unit, f1.Id.ToString(), new Dictionary<string, object> { { "weaponsControl", "hold" } }));
                    Assert.Empty(console.SetDoctrine(DoctrineLevel.Unit, f2.Id.ToString(), new Dictionary<string, object> { { "weaponsControl", "tight" } }));
                    _engine.SetSideManaged(Red, true); // a side the host never heard of
                });
                // And an agent's side-level lock through c2.doctrine.set.
                llm.Call("c2.doctrine.set", JsonValue.Obj().Set("level", "side").Set("values", JsonValue.Obj().Set("weaponsControl", "tight"))
                    .Set("lock", JsonValue.Arr().Add("weaponsControl")));
                Expect.Eventually(() => _t.Host.C2.Ticks >= 2, 3000, "ticks after the window commands");
                Assert.True(_t.Host.C2.Engaged); // window commands hand ticking to the host's slot (Engage)
                Assert.Equal("host", console.Owner);
                Assert.Equal("side:Blue=tight", EffectiveFrom(f1.Id, "weaponsControl")); // side lock wins over the unit's hold

                llm.Call("host.kill_switch", JsonValue.Obj().Set("action", "engage").Set("reason", "test"));
                Expect.Eventually(() => !_t.Host.C2.Engaged, 3000, "C2 idle after the kill switch");

                bool blue = true, red = true;
                _t.OnMain(() => { blue = _engine.IsSideManaged(Blue); red = _engine.IsSideManaged(Red); });
                Assert.False(blue);
                Assert.False(red);
                Assert.Equal("default=free", EffectiveFrom(f1.Id, "weaponsControl"));
                Assert.Equal("default=free", EffectiveFrom(f2.Id, "weaponsControl"));
                Assert.StartsWith("default=", EffectiveFrom(f1.Id, "advanceOnContact"));
                UnitView v1 = null, v2 = null;
                _t.OnMain(() => { _t.Host.C2.Tick(); v1 = _engine.GetUnitView(f1.Id); v2 = _engine.GetUnitView(f2.Id); });
                Assert.Equal(WeaponsControl.Free, v1.WeaponsControl);
                Assert.Equal(WeaponsControl.Free, v2.WeaponsControl);
                Assert.Equal(FireAuthorityMode.Free, v1.FireAuthority);
                Assert.False(v1.FilterInstalled);

                // The window is refused while the kill switch is engaged; releasing and unsetting still work.
                IssueResult refused = null;
                List<string> docErr = null;
                bool rel = false;
                _t.OnMain(() =>
                {
                    refused = console.Issue(OrderRequest.AttackNearest(Blue, f1.Id));
                    docErr = console.SetDoctrine(DoctrineLevel.Unit, f1.Id.ToString(), new Dictionary<string, object> { { "weaponsControl", "hold" } });
                    console.SetSideManaged(Blue, true);
                    rel = console.Release(f2.Id, "test");
                });
                Assert.False(refused.Accepted);
                Assert.Equal(RefusalCode.NotPermitted, refused.Code);
                Assert.Contains("kill switch", docErr[0]);
                Assert.True(rel);
                _t.OnMain(() => blue = _engine.IsSideManaged(Blue));
                Assert.False(blue);
                Assert.False(_t.Host.C2.Engaged);
                Assert.Contains("Kill switch engaged", console.Status);
            }
            string audit = string.Join("\n", _t.Host.Audit.Recent(200).Select(e => e.ToString()));
            Assert.Contains("\"method\":\"c2.window.side.managed\"", audit);
            Assert.Contains("\"method\":\"c2.window.doctrine.set\"", audit);
            Assert.Contains("deny:kill_switch", audit);
            GC.KeepAlive(b1);
        }

        [Fact]
        public void Window_orders_are_audited_and_engage_the_host_slot()
        {
            var f1 = _bda.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            C2Runtime rt;
            var console = WindowConsole(out rt);
            Assert.False(_t.Host.C2.Engaged);
            IssueResult r = null;
            _t.OnMain(() => r = console.Issue(OrderRequest.MoveTo(Blue, new GeoPoint(0, 0.5), f1.Id)));
            Assert.True(r.Accepted);
            Assert.True(_t.Host.C2.Engaged);
            Expect.Eventually(() => _t.Host.C2.Ticks >= 2, 3000, "host slot ticks the window's order");
            bool cancelled = false;
            _t.OnMain(() => cancelled = console.Cancel(r.Order.Id, "test"));
            Assert.True(cancelled);
            using (var c = _t.Connect())
                Expect.Eventually(() => Events(c, "order.cancelled").Count >= 1, 3000, "window cancel published as order.cancelled");
            string audit = string.Join("\n", _t.Host.Audit.Recent(200).Select(e => e.ToString()));
            Assert.Contains("\"method\":\"c2.window.order.issue\"", audit);
            Assert.Contains("\"method\":\"c2.window.order.cancel\"", audit);
            Assert.Contains("\"actor\":", audit);
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
