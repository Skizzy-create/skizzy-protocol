using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Sim
{
    /// <summary>
    /// Fake camera: renders a small gradient with the target's name hash into a real PNG (zlib via
    /// System.IO.Compression), completing on a worker thread like the KSP encoder. Always PNG; mimeType says so.
    /// </summary>
    public sealed class FakeCamera : ICameraService
    {
        private readonly FakeWorld _w;
        public string Mode = "auto";
        public GVessel Target;
        public GVessel LookTo;
        public double Heading = 0, Pitch = 15, Distance = 50;
        public int Captures;

        public FakeCamera(FakeWorld w) { _w = w; }

        public bool Available => true;

        public void Capture(CaptureRequest req, Action<CaptureResult> done)
        {
            long t0 = Stopwatch.GetTimestamp();
            int w = Math.Max(16, Math.Min(req.Width, 1920));
            int h = w * 9 / 16;
            int seed = (Target ?? _w.ActiveVessel)?.Name?.GetHashCode() ?? 7;
            double mainMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var png = FakePng.Gradient(w, h, seed);
                Interlocked.Increment(ref Captures);
                done(new CaptureResult
                {
                    Data = png,
                    Length = png.Length,
                    Width = w,
                    Height = h,
                    MimeType = "image/png",
                    MainThreadMs = mainMs,
                    TotalMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency,
                    Path = "fake",
                });
            });
        }

        public SkizzyError SetMode(string mode, GVessel target, GVessel to, OrbitSpec orbit)
        {
            if (_w.Scene != "FLIGHT") return SkizzyError.Unavailable(_w.Scene, new[] { "FLIGHT" }, "wrong_scene");
            if (mode == "look_at" && to == null) return SkizzyError.InvalidParam("/to", "look_at needs to");
            Mode = mode;
            if (target != null) Target = target;
            LookTo = mode == "look_at" ? to : null;
            ApplyOrbit(orbit);
            return null;
        }

        public SkizzyError SetTarget(GVessel target) { Target = target; return null; }

        public SkizzyError SetOrbit(OrbitSpec orbit) { ApplyOrbit(orbit); return null; }

        private void ApplyOrbit(OrbitSpec o)
        {
            if (o.HasHeading) Heading = o.Heading;
            if (o.HasPitch) Pitch = o.Pitch;
            if (o.HasDistance) Distance = Math.Max(3, Math.Min(150000, o.Distance));
        }

        public SkizzyError LookAt(GVessel from, GVessel to, double distance, double elevationDeg)
        {
            Mode = "look_at";
            Target = from;
            LookTo = to;
            Distance = distance;
            Pitch = elevationDeg;
            return null;
        }

        public JsonValue State()
        {
            var t = Target ?? _w.ActiveVessel;
            return JsonValue.Obj().Set("mode", Mode).Set("target", t?.Handle).Set("targetName", t?.Name).Set("heading", Heading)
                .Set("pitch", Pitch).Set("distance", Distance).Set("lookAt", LookTo?.Handle);
        }
    }

    public static class FakePng
    {
        private static readonly uint[] Crc = BuildCrc();

        public static byte[] Gradient(int w, int h, int seed)
        {
            var raw = new byte[(w * 3 + 1) * h];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < w; x++)
                {
                    raw[p++] = (byte)(x * 255 / w);
                    raw[p++] = (byte)(y * 255 / h);
                    raw[p++] = (byte)(seed >> ((x / 32 + y / 32) % 3 * 8));
                }
            }
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                Be(ihdr, 0, (uint)w); Be(ihdr, 4, (uint)h);
                ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(ms, "IHDR", ihdr);
                byte[] z;
                using (var zs = new MemoryStream())
                {
                    using (var zl = new ZLibStream(zs, CompressionLevel.Fastest, true)) zl.Write(raw, 0, raw.Length);
                    z = zs.ToArray();
                }
                Chunk(ms, "IDAT", z);
                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            Be(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint c = 0xFFFFFFFF;
            foreach (var b in t) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
            var crc = new byte[4];
            Be(crc, 0, c ^ 0xFFFFFFFF);
            s.Write(crc, 0, 4);
        }

        private static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }

    /// <summary>Wakes the <see cref="SimRunner"/> thread, the FakeKsp equivalent of the UnitySynchronizationContext post.</summary>
    public sealed class SimWaker : IMainThreadWaker
    {
        internal readonly AutoResetEvent Signal = new AutoResetEvent(false);
        public long WakeCount;
        public void Wake() { Interlocked.Increment(ref WakeCount); Signal.Set(); }
        public string Mode => "sync_context";
    }

    /// <summary>
    /// The FakeKsp "main thread": FixedUpdate at 50 Hz and Update at about 60 Hz while the host wants updates; when
    /// it does not, the thread blocks until woken (same contract as the KSP pump being disabled).
    /// </summary>
    public sealed class SimRunner : IDisposable
    {
        public readonly HostCore Host;
        public readonly FakeWorld World;
        public readonly SimWaker Waker;
        private readonly Thread _thread;
        private volatile bool _stop;
        public long Updates;
        public long FixedUpdates;
        public volatile bool PumpEnabled = true;

        public SimRunner(HostCore host, FakeWorld world, SimWaker waker)
        {
            Host = host;
            World = world;
            Waker = waker;
            _thread = new Thread(Run) { IsBackground = true, Name = "fakeksp-main" };
        }

        public void Start() { _thread.Start(); }

        private void Run()
        {
            var sw = Stopwatch.StartNew();
            double nextFixed = 0, nextFrame = 0, lastFrame = 0;
            while (!_stop)
            {
                if (!PumpEnabled)
                {
                    // The timeout only lets Dispose stop the thread; it must not re-enable the pump (a disabled KSP pump
                    // gets no callbacks until woken, and the idle tests count them).
                    bool woken = Waker.Signal.WaitOne(500);
                    if (_stop) return;
                    if (!woken) continue;
                    PumpEnabled = true;
                    Host.Dispatcher.MarkAwake();
                    nextFixed = nextFrame = sw.Elapsed.TotalMilliseconds;
                }
                double now = sw.Elapsed.TotalMilliseconds;
                if (now >= nextFixed)
                {
                    nextFixed += 20;
                    if (now - nextFixed > 200) nextFixed = now + 20;
                    FixedUpdates++;
                    Host.OnFixedUpdate();
                }
                if (now >= nextFrame)
                {
                    nextFrame += 16.6;
                    if (now - nextFrame > 200) nextFrame = now + 16.6;
                    Updates++;
                    Host.OnUpdate(now - lastFrame);
                    lastFrame = now;
                    if (!Host.AfterFrame()) PumpEnabled = false;
                }
                double sleep = Math.Min(nextFixed, nextFrame) - sw.Elapsed.TotalMilliseconds;
                if (sleep > 1) Thread.Sleep((int)sleep);
            }
        }

        public void Dispose()
        {
            _stop = true;
            Waker.Signal.Set();
            _thread.Join(2000);
        }
    }

    /// <summary>One-call FakeKsp host for tests and tools: world + host core + runner on loopback.</summary>
    public sealed class SimHost : IDisposable
    {
        public readonly FakeWorld World;
        public readonly HostCore Host;
        public readonly SimRunner Runner;
        public readonly string Root;

        public SimHost(string root, FakeWorld world = null, HostSettings settings = null, IHostLog log = null)
        {
            Root = root;
            World = world ?? FakeWorld.CreateDefault();
            var paths = new HostPaths(root, Path.Combine(root, "GameData", "SkizzyProtocol", "PluginData"));
            Directory.CreateDirectory(paths.PluginData);
            World.LogFile = paths.KspLog;
            if (!File.Exists(paths.KspLog))
                File.WriteAllText(paths.KspLog, "[LOG 12:00:00.000] FakeKsp started\n[WRN 12:00:01.000] [FakeKsp] a warning\n[ERR 12:00:02.000] [FakeKsp] an error\n");
            var waker = new SimWaker();
            Host = new HostCore(settings ?? new HostSettings { PortFirst = 0, PortLast = 0 }, paths, World, waker, log);
            World.Host = Host;
            Runner = new SimRunner(Host, World, waker);
        }

        public void Start()
        {
            Host.Start();
            if (Host.StartError != null) throw new InvalidOperationException(Host.StartError);
            Runner.Start();
        }

        public string Token => File.ReadAllText(Host.Paths.TokenFile).Trim();
        public string Url => "ws://127.0.0.1:" + Host.Port + Skizzy.Protocol.ProtocolInfo.Path;

        public void Dispose()
        {
            Runner.Dispose();
            Host.Stop();
        }
    }
}
