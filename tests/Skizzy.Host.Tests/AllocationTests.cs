using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Host.Core.Caps;
using Skizzy.Host.Core.Net;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Json;
using Skizzy.Sim;
using Xunit;

namespace Skizzy.Host.Tests
{
    /// <summary>
    /// 02 §2.3 / §8.1: every <c>// skizzy:hot</c> path allocates 0 bytes per call after warm-up, measured exactly
    /// with GC.GetAllocatedBytesForCurrentThread on .NET 10 running the same code as the game.
    /// </summary>
    /// <summary>Timing-sensitive tests run alone so parallel test classes do not skew the numbers.</summary>
    [CollectionDefinition("perf", DisableParallelization = true)]
    public sealed class PerfCollection { }

    [Collection("perf")]
    public class AllocationTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;
        public AllocationTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }
        /// <summary>
        /// Bytes allocated by <paramref name="n"/> calls after warm-up. Takes the best of three windows: a real per-call
        /// allocation shows in every window, while a one-off runtime artefact (tiered JIT, a type load) does not.
        /// </summary>
        private static long Measure(Action a, int warm = 200, int n = 2000)
        {
            for (int i = 0; i < warm; i++) a();
            long best = long.MaxValue;
            for (int attempt = 0; attempt < 3 && best != 0; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < n; i++) a();
                long d = GC.GetAllocatedBytesForCurrentThread() - before;
                if (d < best) best = d;
            }
            return best;
        }

        [Fact]
        public void Number_and_json_writers()
        {
            var b = new ByteBuffer(1 << 16);
            var w = new JsonWriter(b);
            double x = 1203.4567;
            Assert.Equal(0, Measure(() =>
            {
                b.Clear();
                w.Reset(b);
                w.BeginObject().Prop("ut", x).Prop("frame", 60170L).Prop("name", "DDG-1 Kirov").Prop("q", "a\"b").Prop("nan", double.NaN)
                 .Name("arr").BeginArray().Number(0.1).Single(0.3f).Bool(true).Null().EndArray().EndObject();
                NumberFormat.WriteDouble(b, x * 1e-9);
                NumberFormat.WriteLong(b, long.MinValue);
                x += 0.001;
            }));
        }

        [Fact]
        public void Pool_rent_and_return()
        {
            Assert.Equal(0, Measure(() => ByteBufferPool.Return(ByteBufferPool.Rent(4096))));
        }

        [Fact]
        public void Websocket_frame_reader_and_writer()
        {
            // A masked client text frame of 300 bytes, repeated.
            var payload = new byte[300];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 26);
            var frame = new MemoryStream();
            frame.WriteByte(0x81);
            frame.WriteByte(0x80 | 126);
            frame.WriteByte(1); frame.WriteByte(44);
            byte[] mask = { 1, 2, 3, 4 };
            frame.Write(mask, 0, 4);
            for (int i = 0; i < payload.Length; i++) frame.WriteByte((byte)(payload[i] ^ mask[i & 3]));
            var one = frame.ToArray();
            var all = new byte[one.Length * 8000];
            for (int i = 0; i < 8000; i++) Buffer.BlockCopy(one, 0, all, i * one.Length, one.Length);
            var input = new MemoryStream(all);
            var reader = new WsFrameReader(input, 1 << 20);
            var msg = new ByteBuffer(1024);
            byte pending = 0;
            Assert.Equal(0, Measure(() =>
            {
                byte op = reader.Read(msg, ref pending);
                if (op != WsOpcode.Text || msg.Length != 300) throw new Exception("bad frame");
            }, 200, 2500));

            var writer = new WsFrameWriter();
            var sink = new MemoryStream(1 << 24);
            Assert.Equal(0, Measure(() => { sink.Position = 0; writer.Write(sink, WsOpcode.Text, payload, 0, payload.Length); }));
        }

        [Fact]
        public void Timing_and_csv_helpers()
        {
            var t = new TimingWindow();
            var b = new ByteBuffer(1 << 16);
            var now = DateTime.UtcNow;
            Assert.Equal(0, Measure(() =>
            {
                t.Add(0.0123);
                b.Clear();
                LogManager.WriteIsoUtc(b, now);
                LogManager.CsvString(b, "DDG-1, \"Kirov\" \u00e9");
            }));
        }

        [Fact]
        public void Query_rows_meta_and_idle_pump_checks()
        {
            var root = Path.Combine(Path.GetTempPath(), "skizzy-alloc-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var world = FakeWorld.CreateDefault();
            world.AddFleet(100);
            var host = new HostCore(new HostSettings { Listen = false }, new HostPaths(root, Path.Combine(root, "PD")), world, new SimWaker(), null);
            host.GraceS = 0;
            host.OnUpdate(16);
            var spec = QuerySpec.FromParams(JsonValue.Obj()
                .Set("select", JsonValue.Obj().Set("modules", JsonValue.Obj().Set("type", JsonValue.ArrOf("MissileFire", "ModuleEngines"))))
                .Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "throttle", "guardMode", "finalThrust")).Set("reduce", "sum"));
            var plan = new QueryPlan(host, spec);
            plan.Build();
            Assert.True(plan.Rows.Count >= 100);
            var b = new ByteBuffer(1 << 20);
            var w = new JsonWriter(b);
            Assert.Equal(0, Measure(() =>
            {
                b.Clear();
                w.Reset(b);
                w.BeginArray();
                foreach (var row in plan.Rows)
                {
                    plan.ReadRow(row);
                    plan.WriteIdentity(w, row);
                    for (int f = 0; f < row.Values.Length; f++) QueryPlan.WriteValue(w, ref row.Values[f]);
                }
                w.EndArray();
                plan.EnsureCurrent();
            }, 20, 200));

            Assert.Equal(0, Measure(() =>
            {
                b.Clear();
                w.Reset(b);
                w.BeginObject();
                host.WriteMeta(w);
                w.EndObject();
            }));

            // Idle-side checks the pump makes every frame while active, and the dispatcher with an empty queue.
            Assert.Equal(0, Measure(() =>
            {
                bool wants = host.WantsUpdates;
                host.Dispatcher.DrainMain();
                host.Dispatcher.DrainFixed();
                if (wants) throw new Exception("host should be idle");
            }));
        }

        [Fact]
        public void Log_and_stream_sampling_passes_allocate_nothing_in_steady_state()
        {
            var world = FakeWorld.CreateDefault();
            world.AddFleet(100);
            using (var t = new TestHost(world))
            using (var c = t.Connect())
            {
                c.Call("rec.log.start", JsonValue.Obj().Set("name", "alloc").Set("rateHz", 50).Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "throttle")));
                c.Call("rec.log.start", JsonValue.Obj().Set("name", "alloc2").Set("rateHz", 50).Set("format", "jsonl").Set("fields", JsonValue.ArrOf("altitude", "name")));
                c.Call("stream.subscribe", JsonValue.Obj()
                    .Set("source", JsonValue.Obj().Set("query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "throttle", "situation")))).Set("rateHz", 50));
                c.Call("stream.subscribe", JsonValue.Obj()
                    .Set("source", JsonValue.Obj().Set("query", JsonValue.Obj().Set("fields", JsonValue.ArrOf("missionTime")))).Set("mode", "onchange").Set("rateHz", 50));
                Thread.Sleep(1500); // warm-up: pools, queues and plans reach steady state
                t.OnMain(() => { t.Host.Alloc.Reset(); t.Host.Stats.LogSampleMs.Reset(); });
                Thread.Sleep(2000);
                long logPasses = 0, logMax = 0, streamPasses = 0, streamMax = 0;
                t.OnMain(() => { logPasses = t.Host.Alloc.LogPasses; logMax = t.Host.Alloc.LogMax; streamPasses = t.Host.Alloc.StreamPasses; streamMax = t.Host.Alloc.StreamMax; });
                Assert.True(logPasses > 50, "log passes " + logPasses);
                Assert.True(streamPasses > 50, "stream passes " + streamPasses);
                Assert.True(logMax == 0, "log sampling allocated up to " + logMax + " B in one pass (102 vessels x 3 fields at 50 Hz)");
                Assert.True(streamMax == 0, "stream sampling allocated up to " + streamMax + " B in one pass");
                t.Host.Stats.LogSampleMs.Percentiles(out double p50, out double p99, out double max);
                double mean = t.Host.Stats.LogSampleMs.Mean;
                _out.WriteLine("2 logs (102 vessels x 3 fields CSV + 2 fields JSONL) at 50 Hz: sampler mean " + mean.ToString("0.0000") + " ms, p50 " + p50.ToString("0.0000") +
                               " ms, p99 " + p99.ToString("0.0000") + " ms, max " + max.ToString("0.0000") + " ms; alloc max/pass log " + logMax + " B, stream " + streamMax + " B");
                // 10x the acceptance-4 load (two logs, every FixedUpdate): a loose sanity bound; the gate itself is the next test.
                Assert.True(mean < 1.0, "log sampler mean " + mean + " ms per FixedUpdate on FakeKsp");
            }
        }

        /// <summary>Acceptance 4 on FakeKsp: 100 vessels x 3 fields at 10 Hz, sampler cost per FixedUpdate.</summary>
        [Fact]
        public void Log_gate_100_vessels_3_fields_10hz()
        {
            var world = FakeWorld.CreateDefault();
            world.AddFleet(96);
            using (var t = new TestHost(world))
            using (var c = t.Connect())
            {
                c.Call("rec.log.start", JsonValue.Obj().Set("name", "gate").Set("rateHz", 10).Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "throttle")));
                Thread.Sleep(1000);
                t.OnMain(() => t.Host.Stats.LogSampleMs.Reset());
                Thread.Sleep(3000);
                var stop = c.Call("rec.log.stop", JsonValue.Obj().Set("log", "gate"));
                t.Host.Stats.LogSampleMs.Percentiles(out double p50, out double p99, out double max);
                double mean = t.Host.Stats.LogSampleMs.Mean;
                _out.WriteLine("log gate (FakeKsp): 100 vessels x 3 fields @10 Hz: per-FixedUpdate mean " + mean.ToString("0.0000") + " ms, p50 " + p50.ToString("0.0000") +
                               " ms, p99 " + p99.ToString("0.0000") + " ms, max " + max.ToString("0.0000") + " ms, rows " + stop.GetLong("rows"));
                // Acceptance 4: <= 0.2 ms per FixedUpdate (amortised over the sampled and skipped steps).
                Assert.True(mean <= 0.2, "log gate mean " + mean + " ms per FixedUpdate");
            }
        }

        /// <summary>Every hot tag must be covered here; adding a tag without a test fails CI.</summary>
        [Fact]
        public void Every_hot_tag_is_covered_and_hot_files_avoid_linq()
        {
            var covered = new HashSet<string>
            {
                "LogManager.Sample", "LogManager.SampleOne", "LogManager.WriteCsvRow", "LogManager.CsvString", "LogManager.WriteIsoUtc", "LogFile.RentChunk",
                "VesselFieldAccessor.Read", "QueryPlan.ReadRow", "QueryPlan.ReduceInto", "QueryPlan.WriteValue",
                "StreamManager.Sample", "StreamManager.SampleOne", "Dispatcher.DrainMain", "HostCore.WriteMeta", "TimingWindow.Add",
                "Connection.Send", "WsFrameReader.Read", "WsFrameWriter.Write", "ByteBuffer.Ensure", "ByteBufferPool.Rent",
                "JsonWriter.Separator", "JsonWriter.WriteQuoted", "NumberFormat.WriteLong", "NumberFormat.WriteDouble",
                "FakeVessel.ReadField", "FakeModule.Accessor.Read",
            };
            string src = FindRepoDir("src");
            var tag = new Regex(@"//\s*skizzy:hot\s+([A-Za-z0-9_.]+)");
            var found = new List<string>();
            foreach (var f in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                string text = File.ReadAllText(f);
                var ms = tag.Matches(text);
                if (ms.Count == 0) continue;
                Assert.True(text.IndexOf("using System.Linq", StringComparison.Ordinal) < 0, f + " has skizzy:hot paths and must not use LINQ (02 \u00a72.3)");
                // src/Skizzy.Host/Ksp is the Unity layer: it cannot run under .NET 10, so its hot paths are measured in game
                // (host.stats timings, host.selfcheck.perf, idle probe) instead of by an allocation assert here.
                bool unityLayer = f.Contains(Path.DirectorySeparatorChar + "Skizzy.Host" + Path.DirectorySeparatorChar + "Ksp" + Path.DirectorySeparatorChar);
                foreach (Match m in ms) if (!unityLayer) found.Add(m.Groups[1].Value);
            }
            var missing = found.Where(n => !covered.Contains(n)).ToList();
            Assert.True(missing.Count == 0, "hot tags without an allocation test: " + string.Join(", ", missing));
            Assert.True(found.Count >= covered.Count - 2);
        }

        internal static string FindRepoDir(string name)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, name, "Skizzy.Protocol"))) d = d.Parent;
            Assert.NotNull(d);
            return Path.Combine(d.FullName, name);
        }
    }
}
