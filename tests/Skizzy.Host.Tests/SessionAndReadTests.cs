using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Skizzy.Client;
using Skizzy.Host.Core;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Xunit;

namespace Skizzy.Host.Tests
{
    public class SessionTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        public void Dispose() { _t.Dispose(); }

        [Fact]
        public void Hello_returns_session_limits_meta_and_clamped_precedence()
        {
            using (var c = _t.Connect(new HelloOptions { ClientName = "py", ActorKind = "llm", ClientKind = "bridge", Role = "commander", Side = "Blue", Precedence = 5000 }))
            {
                var h = c.HelloResult;
                Assert.Equal("skizzy-protocol", h.GetString("protocol"));
                Assert.Equal("1.0", h.GetString("version"));
                Assert.StartsWith("s-", h.GetString("session"));
                Assert.Equal(800, h.GetInt("precedence"));
                Assert.Equal("commander", h.GetString("role"));
                Assert.True(h.GetBool("fog"));
                Assert.Equal(4 * 1024 * 1024, h.GetObj("limits").GetInt("maxMessageBytes"));
                Assert.Equal("FLIGHT", h.GetObj("meta").GetString("scene"));
                Assert.Equal("allow", h.GetObj("grants").GetString("read"));
                Assert.Equal("ask", h.GetObj("grants").GetString("destructive"));
                Assert.Equal("deny", h.GetObj("grants").GetString("unsafe"));
                Assert.False(h.GetObj("areas").GetBool("deep"));
                Assert.NotNull(h.GetObj("seat").GetString("holder"));
            }
        }

        [Fact]
        public void Precedence_can_be_lowered_not_raised()
        {
            using (var c = _t.Connect(new HelloOptions { ActorKind = "script", Precedence = 100 }))
                Assert.Equal(100, c.HelloResult.GetInt("precedence"));
            using (var c = _t.Connect(new HelloOptions { ActorKind = "script", Precedence = 999 }))
                Assert.Equal(400, c.HelloResult.GetInt("precedence"));
        }

        [Fact]
        public void Advisor_asking_for_god_view_is_denied_acceptance_6()
        {
            var c = new SkizzyClient();
            var ex = Assert.Throws<SkizzyError>(() => c.ConnectAsync(_t.Endpoint,
                new HelloOptions { ActorKind = "llm", ClientKind = "bridge", Role = "advisor", Fog = false }).GetAwaiter().GetResult());
            Assert.Equal(ErrorKind.PermissionDenied, ex.Kind);
            Assert.Equal("god_view", ex.Reason);
            c.Dispose();
        }

        [Fact]
        public void Llm_cannot_claim_human_role()
        {
            var c = new SkizzyClient();
            var ex = Assert.Throws<SkizzyError>(() => c.ConnectAsync(_t.Endpoint, new HelloOptions { ActorKind = "llm", Role = "human" }).GetAwaiter().GetResult());
            Assert.Equal("role_clamp", ex.Reason);
            c.Dispose();
        }

        [Fact]
        public void Unsupported_version_is_reported()
        {
            var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _t.Sim.Token);
            ws.ConnectAsync(new Uri(_t.Sim.Url), CancellationToken.None).Wait();
            var b = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session.hello\",\"params\":{\"versions\":[\"0.2\"]}}");
            ws.SendAsync(new ArraySegment<byte>(b), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait();
            var buf = new byte[4096];
            var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
            var err = JsonValue.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, r.Count)).GetObj("error");
            Assert.Equal(-32013, err.GetInt("code"));
            Assert.Equal("1.0", err.GetObj("data").GetArr("supported")[0].AsString());
            ws.Dispose();
        }

        [Fact]
        public void Second_commander_on_a_side_becomes_advisor()
        {
            using (var a = _t.ConnectLlm("commander", "Red"))
            using (var b = _t.ConnectLlm("commander", "Red"))
            {
                Assert.Equal("commander", a.HelloResult.GetString("role"));
                Assert.Equal("advisor", b.HelloResult.GetString("role"));
                Assert.Equal(0, b.HelloResult.GetInt("precedence"));
            }
        }

        [Fact]
        public void Every_result_carries_meta()
        {
            using (var c = _t.Connect())
            {
                var r = c.Call("host.state");
                var m = r.GetObj("meta");
                Assert.NotNull(m);
                Assert.True(m.GetDouble("ut") > 1000);
                Assert.Equal("FLIGHT", m.GetString("scene"));
                Assert.NotNull(m.GetObj("warp"));
                Assert.True(m.GetInt("catalogRevision") >= 1);
            }
        }

        [Fact]
        public void Unknown_method_suggests_a_name()
        {
            using (var c = _t.Connect())
            {
                var e = Expect.Error(() => c.Call("ksp.vessel.list"), ErrorKind.MethodNotFound);
                Assert.Equal("ksp.vessels.list", e.Extra.GetString("didYouMean"));
            }
        }

        [Fact]
        public void Invalid_params_point_at_the_field()
        {
            using (var c = _t.Connect())
            {
                var e = Expect.Error(() => c.Call("ksp.vessel.get", JsonValue.Obj().Set("depth", 7)), ErrorKind.InvalidParams);
                Assert.Equal("/depth", e.Extra.GetArr("errors")[0].GetString("pointer"));
                Expect.Error(() => c.Call("ksp.vessels.list", JsonValue.Obj().Set("bogus", 1)), ErrorKind.InvalidParams);
            }
        }

        [Fact]
        public void Cap_call_and_catalog()
        {
            using (var c = _t.Connect())
            {
                var r = c.Call("cap.call", JsonValue.Obj().Set("name", "ksp.read").Set("args", JsonValue.Obj().Set("vessel", "active")));
                Assert.Equal("DDG-1 Kirov", r.GetString("name"));
                var s = c.Call("catalog.search", JsonValue.Obj().Set("query", "screenshot"));
                Assert.Equal("ksp.camera.screenshot", s.GetArr("results")[0].GetString("name"));
                var d = c.Call("catalog.describe", JsonValue.Obj().Set("name", "rec.log.start"));
                Assert.Equal("control", d.GetObj("capability").GetString("safety"));
                Assert.Equal("allow", d.GetObj("capability").GetString("policy"));
                var g = c.Call("catalog.get", JsonValue.Obj().Set("toolset", "core"));
                Assert.True(g.GetInt("total") >= 15);
            }
        }

        [Fact]
        public void Resume_keeps_the_session_id()
        {
            string sid;
            var hello = new HelloOptions { ActorId = "a:resumer", ActorKind = "script" };
            using (var c = _t.Connect(hello)) sid = c.SessionId;
            Expect.Eventually(() => _t.Host.AttachedSessionCount == 0, 3000, "detach");
            var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _t.Sim.Token);
            ws.ConnectAsync(new Uri(_t.Sim.Url), CancellationToken.None).Wait();
            var p = hello.ToParams(sid, 0);
            var req = JsonValue.Obj().Set("jsonrpc", "2.0").Set("id", 1).Set("method", "session.hello").Set("params", p);
            var b = JsonWriter.SerializeToBytes(req);
            ws.SendAsync(new ArraySegment<byte>(b), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait();
            var buf = new byte[8192];
            var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
            var res = JsonValue.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, r.Count)).GetObj("result");
            Assert.Equal(sid, res.GetString("session"));
            Assert.True(res.GetBool("resumed"));
            ws.Dispose();
        }

        [Fact]
        public void Client_reconnects_automatically_after_host_restart()
        {
            var c = _t.Connect(new HelloOptions { ActorId = "a:auto", ActorKind = "script" });
            c.AutoReconnect = true;
            int port = _t.Host.Port;
            // Simulate the host dropping every connection (KSP restart drops the socket).
            var conns = new List<Skizzy.Host.Core.Net.Connection>();
            _t.Host.Server.Snapshot(conns);
            foreach (var conn in conns) conn.Abort(1001, "test drop");
            Expect.Eventually(() => c.Reconnects >= 1 && c.IsConnected, 8000, "reconnect");
            Assert.Equal("DDG-1 Kirov", c.Call("ksp.read").GetString("name"));
            c.Dispose();
        }
    }

    public class ReadTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        private readonly SkizzyClient _c;
        public ReadTests() { _c = _t.Connect(); }
        public void Dispose() { _c.Dispose(); _t.Dispose(); }

        [Fact]
        public void Vessels_list_excludes_debris_and_pages()
        {
            var r = _c.Call("ksp.vessels.list");
            var names = r.GetArr("vessels").Items.Select(v => v.GetString("name")).ToList();
            Assert.Equal("DDG-1 Kirov", names[0]); // active first
            Assert.DoesNotContain("Debris (Mk41 cap)", names);
            Assert.Contains("Tanker (far)", names);
            Assert.Equal(5, r.GetInt("total"));
            var page = _c.Call("ksp.vessels.list", JsonValue.Obj().Set("limit", 2));
            Assert.True(page.GetBool("truncated"));
            var page2 = _c.Call("ksp.vessels.list", JsonValue.Obj().Set("limit", 2).Set("cursor", page.GetString("cursor")));
            Assert.NotEqual(page.GetArr("vessels")[0].GetString("handle"), page2.GetArr("vessels")[0].GetString("handle"));
            var debris = _c.Call("ksp.vessels.list", JsonValue.Obj().Set("includeDebris", true));
            Assert.Equal(6, debris.GetInt("total"));
            var ships = _c.Call("ksp.vessels.list", JsonValue.Obj().Set("nameLike", "DDG*"));
            Assert.Equal(2, ships.GetInt("total"));
            var compact = _c.Call("ksp.vessels.list", JsonValue.Obj().Set("format", "compact"));
            Assert.Contains("*v:", compact.GetString("text"));
        }

        [Fact]
        public void Read_by_active_handle_and_name()
        {
            var a = _c.Call("ksp.read");
            Assert.Equal("DDG-1 Kirov", a.GetString("name"));
            Assert.True(a.GetObj("bda").GetBool("weaponManager"));
            Assert.True(a.GetObj("bda").GetBool("ai"));
            var byHandle = _c.Call("ksp.vessel.get", JsonValue.Obj().Set("vessel", a.GetString("handle")).Set("depth", 2));
            var parts = byHandle.GetArr("parts");
            Assert.Equal(5, parts.Count);
            Assert.StartsWith("m:", parts[0].GetArr("modules")[0].GetString("handle"));
            var byName = _c.Call("ksp.read", JsonValue.Obj().Set("vessel", "ddg-2 udaloy"));
            Assert.Equal("DDG-2 Udaloy", byName.GetString("name"));
            var compact = _c.Call("ksp.read", JsonValue.Obj().Set("format", "compact"));
            Assert.Contains("#vessel", compact.GetString("text"));
        }

        [Fact]
        public void Ambiguous_and_unknown_names()
        {
            _t.OnMain(() => _t.World.AddVessel("Twin", "Ship", "SPLASHED", 0, 0, 0, 0, 0, 0, false));
            _t.OnMain(() => _t.World.AddVessel("Twin", "Ship", "SPLASHED", 0, 0, 0, 0, 0, 0, false));
            var e = Expect.Error(() => _c.Call("ksp.read", JsonValue.Obj().Set("vessel", "Twin")), ErrorKind.InvalidParams, "ambiguous");
            Assert.Equal(2, e.Extra.GetArr("candidates").Count);
            var nf = Expect.Error(() => _c.Call("ksp.read", JsonValue.Obj().Set("vessel", "DDG-1 Kirow")), ErrorKind.NotFound, "unknown");
            Assert.Contains("DDG-1 Kirov", nf.Hint);
        }

        [Fact]
        public void Destroyed_and_stale_handles_acceptance_5()
        {
            string h2 = _c.Call("ksp.read", JsonValue.Obj().Set("vessel", "DDG-2 Udaloy")).GetString("handle");
            _t.OnMain(() => _t.World.DestroyVessel(_t.World.ByName("DDG-2 Udaloy")));
            Expect.Error(() => _c.Call("ksp.read", JsonValue.Obj().Set("vessel", h2)), ErrorKind.NotFound, "destroyed");

            string hf = _c.Call("ksp.read", JsonValue.Obj().Set("vessel", "FFG-3 Neustrashimy")).GetString("handle");
            _t.OnMain(() => _t.World.ChangeScene("SPACECENTER", dropVessels: true));
            Expect.Eventually(() => _c.Call("host.state").GetString("scene") == "SPACECENTER", 3000, "scene change");
            _t.OnMain(() => _t.World.ChangeScene("FLIGHT"));
            Expect.Eventually(() => _c.Call("host.state").GetString("scene") == "FLIGHT", 3000, "scene back");
            Expect.Error(() => _c.Call("ksp.read", JsonValue.Obj().Set("vessel", hf)), ErrorKind.NotFound, "stale");
        }

        [Fact]
        public void Wrong_scene_is_reported()
        {
            _t.OnMain(() => _t.World.ChangeScene("EDITOR"));
            Expect.Eventually(() => { var st = _c.Call("host.state"); return st.GetString("scene") == "EDITOR" && !st.GetBool("sceneTransition"); }, 3000, "editor");
            var e = Expect.Error(() => _c.Call("ksp.query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("altitude"))), ErrorKind.UnavailableInScene, "wrong_scene");
            Assert.Equal("EDITOR", e.Extra.GetString("scene"));
        }

        [Fact]
        public void Query_per_vessel_per_module_and_reduce()
        {
            var r = _c.Call("ksp.query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "throttle")));
            Assert.Equal(new[] { "vessel", "vesselName", "altitude", "srfSpeed", "throttle" }, r.GetArr("columns").Items.Select(x => x.AsString()));
            Assert.Equal(4, r.GetInt("total")); // no selector: every loaded vessel, debris excluded
            var mods = _c.Call("ksp.query", JsonValue.Obj()
                .Set("select", JsonValue.Obj().Set("vessels", JsonValue.Obj().Set("nameLike", "DDG*")).Set("modules", JsonValue.Obj().Set("type", "MissileFire")))
                .Set("fields", JsonValue.ArrOf("guardMode", "teamString", "vessel.srfSpeed")).Set("aggregate", "perModule"));
            Assert.Equal(2, mods.GetInt("total"));
            Assert.Equal("module", mods.GetArr("columns")[3].AsString());
            Assert.True(mods.GetArr("rows")[0][4].AsBool());
            Assert.Equal("Blue", mods.GetArr("rows")[0][5].AsString());
            var sum = _c.Call("ksp.query", JsonValue.Obj()
                .Set("select", JsonValue.Obj().Set("modules", JsonValue.Obj().Set("type", "ModuleEngines")))
                .Set("fields", JsonValue.ArrOf("finalThrust")).Set("reduce", "sum"));
            Assert.Equal(210, sum.GetArr("rows")[0][2].AsDouble(), 3);
            var bad = Expect.Error(() => _c.Call("ksp.query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("altitud"))), ErrorKind.InvalidParams);
            Assert.Contains("altitude", bad.Message);
        }

        [Fact]
        public void Modules_describe_and_get()
        {
            var v = _c.Call("ksp.vessel.get", JsonValue.Obj().Set("depth", 2));
            string mh = v.GetArr("parts")[1].GetArr("modules")[0].GetString("handle");
            var g = _c.Call("ksp.module.get", JsonValue.Obj().Set("module", mh));
            Assert.Equal("MissileFire", g.GetString("class"));
            Assert.True(g.GetObj("values").GetBool("guardMode"));
            var d = _c.Call("ksp.module.describe", JsonValue.Obj().Set("type", "MissileFire"));
            var f = d.GetArr("fields").Items.First(x => x.GetString("name") == "maxMissilesOnTarget");
            Assert.Equal(1, f.GetDouble("minimum"));
            Assert.Equal(6, f.GetDouble("maximum"));
            Expect.Error(() => _c.Call("ksp.module.get", JsonValue.Obj().Set("module", mh.Replace("MissileFire", "ModuleEngines"))), ErrorKind.NotFound, "stale");
        }

        [Fact]
        public void Mods_and_log_tail()
        {
            var m = _c.Call("ksp.mods.list", JsonValue.Obj().Set("nameLike", "BD*"));
            Assert.Equal("BDArmory", m.GetArr("mods")[0].GetString("name"));
            var t = _c.Call("ksp.log.tail", JsonValue.Obj().Set("level", "error"));
            Assert.Contains("an error", t.GetString("text"));
            Assert.DoesNotContain("a warning", t.GetString("text"));
        }

        /// <summary>
        /// Acceptance 2 on FakeKsp. A wall-clock gate: on a saturated machine the OS scheduler alone adds a quantum per
        /// hop, so it is tagged <c>Category=Perf</c> (CI runs it in its own step; <c>--filter "Category!=Perf"</c> skips it).
        /// </summary>
        [Fact]
        [Trait("Category", "Perf")]
        public void Hello_to_read_rtt_is_fast_acceptance_2_fake()
        {
            // Load-resistant: up to five batches of 50 calls, gated on the best batch's p50. The host's latency is the same in
            // every batch; a batch where a busy machine kept the FakeKsp or socket threads off the CPU only reads higher.
            double best = double.MaxValue;
            var all = new List<string>();
            for (int batch = 0; batch < 5 && best >= 20; batch++)
            {
                var times = new List<double>();
                for (int i = 0; i < 50; i++)
                {
                    var sw = Stopwatch.StartNew();
                    _c.Call("ksp.read");
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                double p50 = times[times.Count / 2];
                all.Add(p50.ToString("0.00"));
                if (p50 < best) best = p50;
            }
            Assert.True(best < 20, "ksp.read p50 per batch " + string.Join(", ", all) + " ms");
        }
    }
}
