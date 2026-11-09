using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Skizzy.Client;
using Skizzy.Host.Core;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Xunit;

namespace Skizzy.Host.Tests
{
    public class SafetyTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        public void Dispose() { _t.Dispose(); }

        [Fact]
        public void Kill_switch_denies_non_read_and_only_a_human_releases()
        {
            using (var llm = _t.ConnectLlm())
            {
                var k = llm.Call("host.kill_switch", JsonValue.Obj().Set("action", "engage").Set("reason", "test"));
                Assert.True(k.GetBool("engaged"));
                var e = Expect.Error(() => llm.Call("ksp.camera.set_mode", JsonValue.Obj().Set("mode", "chase")), ErrorKind.KillSwitchActive);
                Assert.NotNull(e.Extra.GetString("since"));
                Assert.Equal("DDG-1 Kirov", llm.Call("ksp.read").GetString("name")); // reads still work
                Expect.Error(() => llm.Call("host.kill_switch", JsonValue.Obj().Set("action", "release")), ErrorKind.PermissionDenied, "role_clamp");
                JsonValue rel = null;
                var done = new ManualResetEventSlim();
                _t.Host.InvokeLocal("host.kill_switch", JsonValue.Obj().Set("action", "release"), (r, err) => { rel = r; done.Set(); });
                Assert.True(done.Wait(3000));
                Assert.False(rel.GetBool("engaged"));
                Assert.True(llm.Call("ksp.camera.set_mode", JsonValue.Obj().Set("mode", "chase")).GetBool("ok"));
            }
        }

        [Fact]
        public void Ask_prompt_allow_and_deny()
        {
            using (var c = _t.ConnectLlm())
            {
                _t.Host.Prompts.AutoAnswer = PromptAnswer.Deny;
                var e = Expect.Error(() => c.Call("host.debug.confirm_test", JsonValue.Obj().Set("note", "hi")), ErrorKind.PermissionDenied, "policy");
                Assert.Equal("Deny", e.Extra.GetString("answer"));
                _t.Host.Prompts.AutoAnswer = PromptAnswer.AllowOnce;
                Assert.True(c.Call("host.debug.confirm_test").GetBool("confirmed"));
                _t.Host.Prompts.AutoAnswer = PromptAnswer.AllowSession;
                c.Call("host.debug.confirm_test");
                _t.Host.Prompts.AutoAnswer = PromptAnswer.Deny;
                Assert.True(c.Call("host.debug.confirm_test").GetBool("confirmed")); // session grant now
            }
        }

        [Fact]
        public void Ask_times_out_as_deny()
        {
            _t.Host.Settings.ConfirmTimeoutS = 0.3;
            using (var c = _t.ConnectLlm())
            {
                var e = Expect.Error(() => c.Call("host.debug.confirm_test"), ErrorKind.PermissionDenied);
                Assert.Equal("Timeout", e.Extra.GetString("answer"));
            }
        }

        [Fact]
        public void Observer_role_is_clamped()
        {
            using (var c = _t.Connect(new HelloOptions { ActorKind = "llm", ClientKind = "bridge", Role = "observer" }))
            {
                Expect.Error(() => c.Call("rec.log.start", JsonValue.Obj().Set("name", "x").Set("fields", JsonValue.ArrOf("altitude"))), ErrorKind.PermissionDenied, "role_clamp");
                Assert.NotNull(c.Call("ksp.read").GetString("name"));
            }
        }

        [Fact]
        public void Screenshot_rate_limit_1_per_2s()
        {
            using (var c = _t.ConnectLlm())
            {
                var r = c.Call("ksp.camera.screenshot", JsonValue.Obj().Set("width", 256));
                Assert.Equal("image/png", r.GetObj("image").GetString("mimeType"));
                var e = Expect.Error(() => c.Call("ksp.camera.screenshot", JsonValue.Obj().Set("width", 256)), ErrorKind.RateLimited);
                Assert.True(e.Extra.GetLong("retryAfterMs") > 0);
            }
        }

        [Fact]
        public void Audit_records_every_call()
        {
            using (var c = _t.ConnectLlm())
            {
                c.Call("ksp.read");
                Expect.Error(() => c.Call("ksp.read", JsonValue.Obj().Set("vessel", "nope")), ErrorKind.NotFound);
            }
            Expect.Eventually(() => Directory.Exists(_t.Host.Paths.AuditDir) && Directory.GetFiles(_t.Host.Paths.AuditDir).Length > 0, 3000, "audit file");
            Expect.Eventually(() =>
            {
                var f = Directory.GetFiles(_t.Host.Paths.AuditDir)[0];
                string text;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                return text.Contains("\"errorKind\":\"NOT_FOUND\"") && text.Contains("\"method\":\"ksp.read\"") && text.Contains("session.hello");
            }, 3000, "audit lines");
        }
    }

    public class EventAndStreamTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        public void Dispose() { _t.Dispose(); }

        [Fact]
        public void Events_wait_wakes_on_vessel_destroyed()
        {
            using (var c = _t.Connect())
            {
                long cursor = c.Call("events.poll", JsonValue.Obj().Set("limit", 1)).GetLong("nextCursor");
                JsonValue got = null;
                var th = new Thread(() => got = c.Call("events.wait", JsonValue.Obj().Set("cursor", cursor).Set("names", JsonValue.ArrOf("vessel.*")).Set("timeoutS", 10).Set("format", "json")));
                th.Start();
                Thread.Sleep(200);
                _t.OnMain(() => _t.World.DestroyVessel(_t.World.ByName("FFG-3 Neustrashimy")));
                Assert.True(th.Join(5000));
                var ev = got.GetArr("events")[0];
                Assert.Equal("vessel.destroyed", ev.GetString("name"));
                Assert.Equal("unit", ev.GetString("category"));
                Assert.Contains("FFG-3", ev.GetString("summary"));
                var poll = c.Call("events.poll", JsonValue.Obj().Set("cursor", cursor).Set("names", JsonValue.ArrOf("vessel.destroyed")));
                Assert.Equal(1, poll.GetInt("count"));
            }
        }

        [Fact]
        public void Events_wait_times_out_empty()
        {
            using (var c = _t.Connect())
            {
                var r = c.Call("events.wait", JsonValue.Obj().Set("names", JsonValue.ArrOf("never.*")).Set("timeoutS", 0.3));
                Assert.True(r.GetBool("timedOut"));
                Assert.Contains("#events n=0", r.GetString("text"));
            }
        }

        [Fact]
        public void Events_subscribe_pushes()
        {
            using (var c = _t.Connect())
            {
                var q = new BlockingCollection<JsonValue>();
                c.Notification += (m, p) => { if (m == "events.data") q.Add(p); };
                c.Call("events.subscribe", JsonValue.Obj().Set("names", JsonValue.ArrOf("sim.*")));
                _t.World.SetPaused(true);
                Assert.True(q.TryTake(out var p1, 3000));
                Assert.Equal("sim.paused", p1.GetArr("events")[0].GetString("name"));
                _t.World.SetPaused(false);
            }
        }

        [Fact]
        public void Sampled_and_onchange_streams()
        {
            using (var c = _t.Connect())
            {
                var q = new BlockingCollection<JsonValue>();
                c.Notification += (m, p) => { if (m == "stream.data") q.Add(p); };
                var sub = c.Call("stream.subscribe", JsonValue.Obj()
                    .Set("source", JsonValue.Obj().Set("query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("missionTime", "srfSpeed"))))
                    .Set("mode", "sampled").Set("rateHz", 10));
                Assert.Equal("full", sub.GetString("kind"));
                Assert.Equal(4, sub.GetArr("rows").Count);
                Assert.True(q.TryTake(out var d1, 3000));
                Assert.True(q.TryTake(out var d2, 3000));
                Assert.True(d2.GetLong("seq") > d1.GetLong("seq"));
                Assert.Equal(4, d2.GetArr("rows").Count);
                Assert.NotNull(d2.GetObj("meta"));
                c.Call("stream.unsubscribe", JsonValue.Obj().Set("sub", sub.GetString("sub")));

                var q2 = new BlockingCollection<JsonValue>();
                c.Notification += (m, p) => { if (m == "stream.data") q2.Add(p); };
                var oc = c.Call("stream.subscribe", JsonValue.Obj()
                    .Set("source", JsonValue.Obj().Set("query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("srfSpeed"))))
                    .Set("mode", "onchange").Set("rateHz", 20));
                Thread.Sleep(300);
                while (q2.TryTake(out _)) { }
                Assert.False(q2.TryTake(out _, 300)); // nothing changes: no deltas
                _t.OnMain(() => _t.World.ByName("DDG-2 Udaloy").Speed = 30);
                Assert.True(q2.TryTake(out var delta, 3000));
                Assert.Equal("delta", delta.GetString("kind"));
                Assert.Equal(1, delta.GetArr("rows").Count);
                Assert.Equal("DDG-2 Udaloy", delta.GetArr("rows")[0][1].AsString());
                Assert.Equal(1, c.Call("stream.list").GetInt("total"));
            }
        }

        [Fact]
        public void Long_running_op_and_cancel()
        {
            using (var c = _t.Connect())
            {
                var r = c.Call("host.selfcheck.perf", JsonValue.Obj().Set("gate", "idle").Set("durationS", 2));
                string op = r.GetString("op");
                Assert.StartsWith("op:", op);
                var done = c.Call("op.wait", JsonValue.Obj().Set("op", op).Set("timeoutS", 10), 15);
                Assert.Equal("done", done.GetString("state"));
                var gate = done.GetObj("result").GetArr("gates")[0];
                Assert.Equal("idle", gate.GetString("gate"));
                Assert.True(gate.GetDouble("measuredMsPerFrameP50") < 0.1, "idle gate on FakeKsp: " + gate);

                var r2 = c.Call("host.selfcheck.perf", JsonValue.Obj().Set("gate", "idle").Set("durationS", 30));
                c.Call("op.cancel", JsonValue.Obj().Set("op", r2.GetString("op")));
                var w = c.Call("op.wait", JsonValue.Obj().Set("op", r2.GetString("op")).Set("timeoutS", 5));
                Assert.Equal("cancelled", w.GetString("state"));
            }
        }
    }

    public class LogTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        public void Dispose() { _t.Dispose(); }

        [Fact]
        public void Log_v0_csv_round_trip()
        {
            using (var c = _t.Connect())
            {
                var start = c.Call("rec.log.start", JsonValue.Obj().Set("name", "climb").Set("rateHz", 20)
                    .Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "throttle")));
                Assert.Equal("log:1", start.GetString("log"));
                string path = start.GetString("path");
                Assert.StartsWith("Skizzy/logs/", path);
                Assert.EndsWith("climb.csv", path);
                Expect.Error(() => c.Call("rec.log.start", JsonValue.Obj().Set("name", "climb").Set("fields", JsonValue.ArrOf("altitude"))), ErrorKind.Conflict, "already_exists");
                Thread.Sleep(600);
                var tail = c.Call("rec.log.tail", JsonValue.Obj().Set("log", "climb").Set("n", 5));
                Assert.Contains("#log log:1 climb", tail.GetString("text"));
                var stop = c.Call("rec.log.stop", JsonValue.Obj().Set("log", "log:1"));
                Assert.True(stop.GetLong("rows") >= 8, "rows " + stop.GetLong("rows"));
                string full = Path.Combine(_t.Root, path.Replace('/', Path.DirectorySeparatorChar));
                var lines = File.ReadAllLines(full, Encoding.UTF8);
                Assert.Equal("ut,frame,wallUtc,vessel,vesselName,altitude,srfSpeed,throttle", lines[0].TrimStart('\uFEFF'));
                var cells = lines[1].Split(',');
                Assert.Equal(8, cells.Length);
                Assert.StartsWith("v:", cells[3]);
                Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", cells[2]);
                var list = c.Call("rec.log.list");
                Assert.Equal("stopped", list.GetArr("logs")[0].GetString("state"));
                var file = c.Call("rec.get_file", JsonValue.Obj().Set("path", path));
                Assert.True(file.GetBool("eof"));
                Assert.Equal(new FileInfo(full).Length, file.GetLong("size"));
                Expect.Error(() => c.Call("rec.get_file", JsonValue.Obj().Set("path", "Skizzy/logs/../../GameData/SkizzyProtocol/PluginData/auth.token")), ErrorKind.PermissionDenied);
            }
        }

        [Fact]
        public void Log_jsonl_on_change()
        {
            using (var c = _t.Connect())
            {
                var start = c.Call("rec.log.start", JsonValue.Obj().Set("name", "speeds").Set("format", "jsonl").Set("rateHz", 20)
                    .Set("onChange", JsonValue.Obj().Set("deadband", 0.5)).Set("fields", JsonValue.ArrOf("srfSpeed")));
                Thread.Sleep(300);
                _t.OnMain(() => _t.World.ByName("F-14 Tomcat").Speed = 300);
                Thread.Sleep(300);
                var stop = c.Call("rec.log.stop", JsonValue.Obj().Set("log", "speeds"));
                Assert.Equal(5, stop.GetLong("rows")); // 4 initial rows + 1 change
                string full = Path.Combine(_t.Root, start.GetString("path").Replace('/', Path.DirectorySeparatorChar));
                var last = JsonValue.Parse(File.ReadAllLines(full).Last());
                Assert.Equal("F-14 Tomcat", last.GetString("vesselName"));
                Assert.Equal(300, last.GetDouble("srfSpeed"));
            }
        }

        [Fact]
        public void Logs_keep_running_under_the_kill_switch_and_stop_is_exempt()
        {
            using (var c = _t.ConnectLlm())
            {
                c.Call("rec.log.start", JsonValue.Obj().Set("name", "k").Set("fields", JsonValue.ArrOf("altitude")));
                c.Call("host.kill_switch", JsonValue.Obj().Set("action", "engage"));
                Expect.Error(() => c.Call("rec.log.start", JsonValue.Obj().Set("name", "k2").Set("fields", JsonValue.ArrOf("altitude"))), ErrorKind.KillSwitchActive);
                Assert.Equal("stopped", c.Call("rec.log.stop", JsonValue.Obj().Set("log", "k")).GetString("state"));
            }
        }
    }

    public class CameraTests : IDisposable
    {
        private readonly TestHost _t = new TestHost();
        public void Dispose() { _t.Dispose(); }

        [Fact]
        public void Screenshot_with_target_and_mode_and_save()
        {
            using (var c = _t.Connect(new HelloOptions { ActorKind = "human", Role = "human" }))
            {
                var r = c.Call("ksp.camera.screenshot", JsonValue.Obj().Set("target", "DDG-2 Udaloy").Set("mode", "chase").Set("width", 512));
                Assert.Equal(512, r.GetInt("width"));
                var img = r.GetObj("image");
                var png = Convert.FromBase64String(img.GetString("data"));
                Assert.Equal(0x89, png[0]);
                Assert.Equal((byte)'P', png[1]);
                var st = c.Call("ksp.camera.state");
                Assert.Equal("chase", st.GetString("mode"));
                Assert.Equal("DDG-2 Udaloy", st.GetString("targetName"));
                Thread.Sleep(2100);
                var s = c.Call("ksp.camera.save", JsonValue.Obj().Set("name", "kirov-chase").Set("width", 256));
                Assert.EndsWith("kirov-chase.png", s.GetString("path"));
                Assert.True(File.Exists(Path.Combine(_t.Root, s.GetString("path").Replace('/', Path.DirectorySeparatorChar))));
                Assert.Null(s.GetObj("image"));
                var lk = c.Call("ksp.camera.look_at", JsonValue.Obj().Set("from", "DDG-1 Kirov").Set("to", "F-14 Tomcat"));
                Assert.Equal("look_at", lk.GetObj("camera").GetString("mode"));
                foreach (var mode in new[] { "follow", "orbit", "free", "locked" })
                    Assert.Equal(mode, c.Call("ksp.camera.set_mode", JsonValue.Obj().Set("mode", mode)).GetObj("camera").GetString("mode"));
            }
        }
    }

    public class IdleTests
    {
        [Fact]
        public void Pump_sleeps_when_nothing_is_connected_and_wakes_on_connect()
        {
            using (var t = new TestHost())
            {
                Expect.Eventually(() => !t.Sim.Runner.PumpEnabled, 3000, "pump to sleep");
                long updates = t.Sim.Runner.Updates;
                long metaReads = t.World.ReadMetaCalls;
                Thread.Sleep(500);
                Assert.Equal(updates, t.Sim.Runner.Updates);          // zero per-frame callbacks while idle
                Assert.Equal(metaReads, t.World.ReadMetaCalls);
                using (var c = t.Connect())
                {
                    // The wake is a post to the main thread (next frame in KSP), so allow the FakeKsp thread to be scheduled.
                    Expect.Eventually(() => t.Sim.Runner.PumpEnabled && t.Sim.Runner.Updates > updates, 3000, "pump wakes on connect");
                    c.Call("ksp.read");
                }
                Expect.Eventually(() => !t.Sim.Runner.PumpEnabled, 3000, "pump to sleep after disconnect");
            }
        }

        [Fact]
        public void Listen_false_opens_no_socket()
        {
            using (var t = new TestHost(null, new HostSettings { Listen = false }))
            {
                Assert.Equal(0, t.Host.Port);
                Assert.False(File.Exists(t.Host.Paths.EndpointFile));
            }
        }

        [Fact]
        public void Endpoint_file_and_token_are_written()
        {
            using (var t = new TestHost())
            {
                var ep = JsonValue.Parse(File.ReadAllText(t.Host.Paths.EndpointFile));
                Assert.Equal(t.Host.Port, ep.GetInt("port"));
                Assert.Equal("skizzy.v1", ep.GetString("subprotocol"));
                Assert.DoesNotContain(t.Sim.Token, ep.ToString()); // the token never goes into endpoint.json
                Assert.Matches("^[A-Za-z0-9_-]{43}$", t.Sim.Token);
            }
        }
    }
}
