using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// The four M0 spikes (17 M0) as <c>host.debug.spike {name}</c>, run in the real game and returning measured
    /// numbers: jpg_thread, async_readback, sync_context, nostdlib. Results are kept for host.selfcheck.
    /// </summary>
    internal static class KspSpikes
    {
        internal static JsonValue Last;
        private static SkizzyBootstrap _boot;

        internal static void Register(HostCore h, SkizzyBootstrap boot)
        {
            _boot = boot;
            h.RegisterOp("host.debug.spike", Run);
        }

        private static IEnumerator<object> Run(CallContext c, Operation op)
        {
            string name = c.Str("name", "all");
            var results = JsonValue.Obj();
            bool pass = true;
            var names = name == "all" ? new[] { "nostdlib", "sync_context", "jpg_thread", "async_readback" } : new[] { name };
            for (int i = 0; i < names.Length && !op.CancelRequested; i++)
            {
                op.ProgressText = "spike " + names[i];
                op.Progress = (double)i / names.Length;
                var slot = new JsonValue[1];
                IEnumerator<object> steps;
                switch (names[i])
                {
                    case "jpg_thread": steps = JpgThread(slot); break;
                    case "async_readback": steps = AsyncReadback(slot, op); break;
                    case "sync_context": steps = SyncContext(slot); break;
                    default: steps = NoStdLib(slot); break;
                }
                while (true)
                {
                    bool more;
                    try { more = steps.MoveNext(); }
                    catch (Exception e)
                    {
                        slot[0] = JsonValue.Obj().Set("pass", false).Set("error", e.GetType().Name + ": " + e.Message);
                        break;
                    }
                    if (!more) break;
                    yield return null;
                }
                var r = slot[0] ?? JsonValue.Obj().Set("pass", false).Set("error", "no result");
                pass &= r.GetBool("pass", false);
                results.Set(names[i], r);
            }
            op.Progress = 1;
            var res = JsonValue.Obj().Set("pass", pass).Set("spikes", results).Set("ranUtc", Clock.WallUtcIso());
            if (name == "all" || Last == null) Last = res;
            else
            {
                var merged = Last.Clone();
                var spikes = merged.GetObj("spikes") ?? JsonValue.Obj();
                spikes.Set(name, results.Get(name));
                merged.Set("spikes", spikes).Set("ranUtc", Clock.WallUtcIso());
                Last = merged;
            }
            op.Result = res;
        }

        // ---------------------------------------------------------------- 1. JPEG encoding off the main thread

        private static IEnumerator<object> JpgThread(JsonValue[] slot)
        {
            const int W = 1024, H = 576;
            var raw = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int p = (y * W + x) * 4;
                    raw[p] = (byte)(x * 255 / W); raw[p + 1] = (byte)(y * 255 / H); raw[p + 2] = (byte)((x ^ y) & 0xFF); raw[p + 3] = 255;
                }
            var req = new CaptureRequest { Width = W, Quality = 75 };
            long t0 = Clock.Ticks;
            byte[] reference = KspCamera.EncodeNow(raw, W, H, req);
            double mainMs = Clock.SinceMs(t0);

            var times = new List<double>();
            int mismatches = 0, done = 0, rounds = 6, parallel = 2;
            string error = null;
            var gate = new object();
            for (int r = 0; r < rounds; r++)
            {
                int finished = 0;
                for (int k = 0; k < parallel; k++)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        long w0 = Clock.Ticks;
                        try
                        {
                            var enc = KspCamera.EncodeNow(raw, W, H, req);
                            double ms = Clock.SinceMs(w0);
                            lock (gate)
                            {
                                times.Add(ms);
                                if (!Same(enc, reference)) mismatches++;
                            }
                        }
                        catch (Exception e) { lock (gate) error = e.GetType().Name + ": " + e.Message; }
                        Interlocked.Increment(ref finished);
                    });
                }
                long deadline = Clock.NowMs + 10000;
                while (Volatile.Read(ref finished) < parallel && Clock.NowMs < deadline) yield return null;
                done += Volatile.Read(ref finished);
            }
            times.Sort();
            bool pass = error == null && mismatches == 0 && done == rounds * parallel;
            slot[0] = JsonValue.Obj().Set("pass", pass)
                .Set("mainThreadEncodeMs", Math.Round(mainMs, 3))
                .Set("workerEncodes", done).Set("parallel", parallel)
                .Set("workerMsP50", times.Count > 0 ? Math.Round(times[(times.Count - 1) / 2], 3) : 0)
                .Set("workerMsMax", times.Count > 0 ? Math.Round(times[times.Count - 1], 3) : 0)
                .Set("identicalToMainThread", mismatches == 0).Set("jpegBytes", reference.Length).Set("error", error)
                .Set("jpegOnWorkerThread", _boot.Host.Settings.JpegOnWorkerThread)
                .Set("advice", pass ? "worker-thread encoding is safe: keep jpegOnWorkerThread = true"
                    : "set jpegOnWorkerThread = false in PluginData/settings.cfg (main-thread encoding, <= 8 ms per 1024 px frame)");
            if (!pass) _boot.Host.Settings.JpegOnWorkerThread = false;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------------------------------------------------------------- 2. AsyncGPUReadback

        private static IEnumerator<object> AsyncReadback(JsonValue[] slot, Operation op)
        {
            var o = JsonValue.Obj()
                .Set("supported", SystemInfo.supportsAsyncGPUReadback)
                .Set("graphicsApi", SystemInfo.graphicsDeviceType.ToString())
                .Set("uvStartsAtTop", SystemInfo.graphicsUVStartsAtTop)
                .Set("flip", _boot.Camera.Flip);
            bool pass = true;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                const int W = 1024, H = 576;
                var rt = _boot.Camera.RentRt(W, H);
                Graphics.Blit(Texture2D.whiteTexture, rt);
                bool done = false, error = false;
                int frames = 0, length = 0, white = 0;
                double cbMs = 0;
                long t0 = Clock.Ticks;
                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, r =>
                {
                    long c0 = Clock.Ticks;
                    if (r.hasError) error = true;
                    else
                    {
                        var d = r.GetData<byte>(0);
                        length = d.Length;
                        if (length > 0) white = d[0];
                    }
                    cbMs = Clock.SinceMs(c0);
                    done = true;
                });
                double requestMs = Clock.SinceMs(t0);
                long deadline = Clock.NowMs + 3000;
                while (!done && Clock.NowMs < deadline) { frames++; yield return null; }
                double totalMs = Clock.SinceMs(t0);
                _boot.Camera.ReturnRt(rt);
                bool ok = done && !error && length == W * H * 4;
                pass &= ok;
                o.Set("readback", JsonValue.Obj().Set("ok", ok).Set("requestMs", Math.Round(requestMs, 3)).Set("callbackCopyMs", Math.Round(cbMs, 3))
                    .Set("framesUntilDone", frames).Set("totalMs", Math.Round(totalMs, 2)).Set("bytes", length).Set("firstByte", white));
            }
            if (_boot.Camera.Available)
            {
                op.ProgressText = "spike async_readback: real 1024 px capture";
                CaptureResult res = null;
                _boot.Camera.Capture(new CaptureRequest { Width = 1024, Quality = 75 }, r => res = r);
                long deadline = Clock.NowMs + 5000;
                while (res == null && Clock.NowMs < deadline) yield return null;
                o.Set("capture", Describe(res));
                if (res == null || res.Error != null) pass = false;
                else if (res.Path == "async_readback" && res.MainThreadMs > 3) o.Set("warning", "main-thread capture cost above the 3 ms budget");

                op.ProgressText = "spike async_readback: ReadPixels fallback for comparison";
                CaptureResult fb = null;
                _boot.Camera.ForceReadPixels = true;
                _boot.Camera.Capture(new CaptureRequest { Width = 1024, Quality = 75 }, r => fb = r);
                deadline = Clock.NowMs + 5000;
                while (fb == null && Clock.NowMs < deadline) yield return null;
                _boot.Camera.ForceReadPixels = false;
                o.Set("fallbackCapture", Describe(fb));
            }
            else o.Set("capture", "camera not available in this scene");
            o.Set("pass", pass)
                .Set("check", "open a screenshot (skizzy shot -o shot.jpg): if it is upside down, set captureFlip = " + (!_boot.Camera.Flip ? "true" : "false") + " in PluginData/settings.cfg");
            slot[0] = o;
        }

        private static JsonValue Describe(CaptureResult r)
        {
            if (r == null) return JsonValue.Obj().Set("ok", false).Set("error", "timed out");
            if (r.Error != null) return JsonValue.Obj().Set("ok", false).Set("error", r.Error.ToOneLine());
            return JsonValue.Obj().Set("ok", true).Set("pipeline", r.Path).Set("mainThreadMs", Math.Round(r.MainThreadMs, 3))
                .Set("totalMs", Math.Round(r.TotalMs, 2)).Set("width", r.Width).Set("height", r.Height).Set("bytes", r.Length);
        }

        // ---------------------------------------------------------------- 3. UnitySynchronizationContext idle wake

        private static IEnumerator<object> SyncContext(JsonValue[] slot)
        {
            var waker = _boot.Waker;
            var ctx = waker.Context;
            var o = JsonValue.Obj().Set("contextType", waker.ContextType).Set("mode", waker.Mode);
            if (ctx == null)
            {
                slot[0] = o.Set("pass", false).Set("advice", "no UnitySynchronizationContext: the polling fallback is active (one volatile read per frame while idle)");
                yield break;
            }
            var latencies = new List<double>();
            var frameGaps = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                bool done = false;
                double ms = 0;
                int postedFrame = Time.frameCount, ranFrame = 0;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    long t0 = Clock.Ticks;
                    ctx.Post(__ =>
                    {
                        ms = Clock.SinceMs(t0);
                        ranFrame = Time.frameCount;
                        done = true;
                    }, null);
                });
                long deadline = Clock.NowMs + 2000;
                while (!done && Clock.NowMs < deadline) yield return null;
                if (done) { latencies.Add(ms); frameGaps.Add(ranFrame - postedFrame); }
            }
            latencies.Sort();
            frameGaps.Sort();
            bool pass = latencies.Count == 20 && frameGaps[frameGaps.Count - 1] <= 2;
            slot[0] = o.Set("pass", pass).Set("posts", latencies.Count)
                .Set("latencyMsP50", latencies.Count > 0 ? Math.Round(latencies[(latencies.Count - 1) / 2], 3) : 0)
                .Set("latencyMsMax", latencies.Count > 0 ? Math.Round(latencies[latencies.Count - 1], 3) : 0)
                .Set("framesMax", frameGaps.Count > 0 ? frameGaps[frameGaps.Count - 1] : -1)
                .Set("wakes", _boot.Host.Dispatcher.Wakes).Set("wakePosts", waker.Posts)
                .Set("note", "posted callbacks run within one or two frames even with the pump disabled; idle cost is zero callbacks");
        }

        // ---------------------------------------------------------------- 4. NoStdLib / references

        private static IEnumerator<object> NoStdLib(JsonValue[] slot)
        {
            var corlib = typeof(object).Assembly;
            string managed = Path.GetDirectoryName(corlib.Location) ?? "";
            var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (!loaded.ContainsKey(n)) loaded[n] = a;
            }
            var refs = JsonValue.Arr();
            var bad = new List<string>();
            foreach (var own in new[] { typeof(KspSpikes).Assembly, typeof(JsonValue).Assembly })
            {
                foreach (var r in own.GetReferencedAssemblies())
                {
                    string where = "not loaded";
                    if (loaded.TryGetValue(r.Name, out var a))
                    {
                        string loc = SafeLocation(a);
                        where = loc.StartsWith(managed, StringComparison.OrdinalIgnoreCase) ? "Managed" : _boot.Host.Paths.Relative(loc);
                    }
                    if (r.Name == "netstandard" || r.Name == "System.Runtime" || r.Name == "System.Memory" || r.Name == "System.Runtime.CompilerServices.Unsafe")
                        bad.Add(r.Name);
                    refs.Add(JsonValue.Obj().Set("from", own.GetName().Name).Set("name", r.Name).Set("version", r.Version.ToString()).Set("resolvedFrom", where));
                }
            }
            string mono = null;
            try
            {
                var t = Type.GetType("Mono.Runtime");
                var m = t != null ? t.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static) : null;
                mono = m != null ? m.Invoke(null, null) as string : null;
            }
            catch (Exception) { }
            // Compiled accessors (DynamicMethod) must work in this runtime.
            var probe = FieldGetters.Get<float>(typeof(SpikeProbe).GetField("Value"));
            bool emitOk = probe != null && Math.Abs(probe(new SpikeProbe { Value = 4.5f }) - 4.5f) < 1e-6;
            bool pass = bad.Count == 0 && emitOk;
            slot[0] = JsonValue.Obj().Set("pass", pass).Set("mscorlib", corlib.GetName().Version.ToString()).Set("imageRuntime", corlib.ImageRuntimeVersion)
                .Set("mono", mono).Set("unity", Application.unityVersion).Set("references", refs)
                .Set("forbiddenReferences", JsonValue.ArrOf(bad.ToArray())).Set("dynamicMethod", emitOk)
                .Set("dynamicMethodError", FieldGetters.LastError);
            yield break;
        }

        private static string SafeLocation(Assembly a)
        {
            try { return a.Location ?? ""; }
            catch (Exception) { return ""; }
        }

        private sealed class SpikeProbe
        {
            public float Value;
        }
    }
}
