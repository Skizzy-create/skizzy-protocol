using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// Screenshots and camera control (13 §1). Capture: end of frame → <c>CaptureScreenshotIntoRenderTexture</c> →
    /// <c>Blit</c> (downscale, flip where needed) → <c>AsyncGPUReadback</c> (fallback <c>ReadPixels</c>) → one copy into a
    /// pooled array → JPEG/PNG encode on a worker thread (main thread if the spike says so). RenderTextures, textures
    /// and arrays are pooled per size and released after 60 s unused. Coroutines run on the bootstrap behaviour, which
    /// has no per-frame methods, so nothing runs unless a capture or look-at is in progress.
    /// </summary>
    internal sealed class KspCamera : ICameraService
    {
        private const double Deg2Rad = Math.PI / 180.0, Rad2Deg = 180.0 / Math.PI;
        private const float PoolIdleS = 60f;
        private readonly MonoBehaviour _runner;
        private readonly HostCore _h;
        private readonly KspWorld _w;
        private readonly IHostLog _log;
        private readonly WaitForEndOfFrame _eof = new WaitForEndOfFrame();
        private readonly Dictionary<long, Stack<RenderTexture>> _rts = new Dictionary<long, Stack<RenderTexture>>();
        private readonly Dictionary<long, Stack<Texture2D>> _texs = new Dictionary<long, Stack<Texture2D>>();
        private readonly Dictionary<long, float> _lastUse = new Dictionary<long, float>();
        private readonly Dictionary<int, Stack<byte[]>> _bytes = new Dictionary<int, Stack<byte[]>>();
        private readonly Dictionary<int, long> _bytesLastUse = new Dictionary<int, long>();
        private bool _janitorRunning;
        private int _pooledRt, _pooledTex, _liveRt, _liveTex;

        /// <summary>null = automatic (<c>SystemInfo.graphicsUVStartsAtTop</c>); settings.cfg <c>captureFlip</c> overrides.</summary>
        internal bool? FlipOverride;
        internal bool ForceReadPixels;
        internal long Captures, Failures, WorkerEncodes, MainEncodes;
        internal string LastError;

        private string _mode = "auto";
        private KspVessel _lookFrom, _lookTo;
        private double _lookDistance = 60, _lookElevation = 10;
        private bool _lookActive;

        public KspCamera(MonoBehaviour runner, HostCore h, KspWorld w, IHostLog log)
        {
            _runner = runner;
            _h = h;
            _w = w;
            _log = log;
        }

        internal bool Flip => FlipOverride ?? SystemInfo.graphicsUVStartsAtTop;
        internal int PooledRenderTextures => _pooledRt;
        internal int PooledTextures => _pooledTex;
        internal int LiveRenderTextures => _liveRt;
        internal int LiveTextures => _liveTex;

        public bool Available
        {
            get
            {
                var s = HighLogic.LoadedScene;
                return s != GameScenes.LOADING && s != GameScenes.LOADINGBUFFER && Screen.width > 0 && Screen.height > 0;
            }
        }

        // ---------------------------------------------------------------- capture

        public void Capture(CaptureRequest req, Action<CaptureResult> done)
        {
            _runner.StartCoroutine(CaptureRoutine(req, done));
        }

        private sealed class Readback
        {
            public bool Done;
            public string Error;
            public byte[] Bytes;
            public double CallbackMs;
        }

        private IEnumerator CaptureRoutine(CaptureRequest req, Action<CaptureResult> done)
        {
            long t0 = Clock.Ticks;
            double mainMs = 0;
            bool hid = false;
            if (req.HideUi)
            {
                GameEvents.onHideUI.Fire();
                hid = true;
                yield return null;
            }
            yield return _eof;

            long m0 = Clock.Ticks;
            int sw = Screen.width, sh = Screen.height;
            int w = Math.Max(16, Math.Min(req.Width, Math.Min(1920, sw)));
            int h = Math.Max(1, (int)Math.Round((double)w * sh / sw));
            RenderTexture small = null;
            string err = GrabScreen(sw, sh, w, h, out small);
            if (hid) GameEvents.onShowUI.Fire();
            mainMs += Clock.SinceMs(m0);
            if (err != null) { Finish(done, null, 0, 0, req, mainMs, t0, null, err); yield break; }

            byte[] raw;
            string path;
            if (SystemInfo.supportsAsyncGPUReadback && !ForceReadPixels)
            {
                path = "async_readback";
                var state = new Readback();
                int expected = w * h * 4;
                long r0 = Clock.Ticks;
                try { AsyncGPUReadback.Request(small, 0, TextureFormat.RGBA32, r => OnReadback(r, state, expected)); }
                catch (Exception e) { state.Done = true; state.Error = "AsyncGPUReadback.Request: " + e.Message; }
                mainMs += Clock.SinceMs(r0);
                long deadline = Clock.NowMs + 3000;
                while (!state.Done && Clock.NowMs < deadline) yield return null;
                mainMs += state.CallbackMs;
                ReturnRt(small);
                if (!state.Done) { Finish(done, null, 0, 0, req, mainMs, t0, path, "GPU readback timed out after 3 s"); yield break; }
                if (state.Error != null) { Finish(done, null, 0, 0, req, mainMs, t0, path, state.Error); yield break; }
                raw = state.Bytes;
            }
            else
            {
                path = "readpixels";
                long r0 = Clock.Ticks;
                raw = ReadPixels(small, w, h, out err);
                ReturnRt(small);
                mainMs += Clock.SinceMs(r0);
                if (err != null) { Finish(done, null, 0, 0, req, mainMs, t0, path, err); yield break; }
            }
            Encode(raw, w, h, req, mainMs, t0, path, done);
        }

        /// <summary>Main thread at end of frame: screen into a pooled screen-sized RT, then downscale/flip into the output RT.</summary>
        private string GrabScreen(int sw, int sh, int w, int h, out RenderTexture small)
        {
            small = null;
            RenderTexture full = null;
            try
            {
                full = RentRt(sw, sh);
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                small = RentRt(w, h);
                if (Flip) Graphics.Blit(full, small, new Vector2(1f, -1f), new Vector2(0f, 1f));
                else Graphics.Blit(full, small);
                return null;
            }
            catch (Exception e)
            {
                if (small != null) { ReturnRt(small); small = null; }
                return "screen capture failed: " + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (full != null) ReturnRt(full);
            }
        }

        private void OnReadback(AsyncGPUReadbackRequest r, Readback state, int expected)
        {
            long c0 = Clock.Ticks;
            try
            {
                if (r.hasError) { state.Error = "GPU readback reported an error"; return; }
                var data = r.GetData<byte>(0);
                if (data.Length != expected) { state.Error = "GPU readback size " + data.Length + " != " + expected; return; }
                var buf = RentBytes(expected);
                data.CopyTo(buf);
                state.Bytes = buf;
            }
            catch (Exception e) { state.Error = "GPU readback: " + e.GetType().Name + ": " + e.Message; }
            finally
            {
                state.CallbackMs = Clock.SinceMs(c0);
                state.Done = true;
            }
        }

        private byte[] ReadPixels(RenderTexture src, int w, int h, out string err)
        {
            err = null;
            Texture2D tex = null;
            var prev = RenderTexture.active;
            try
            {
                tex = RentTex(w, h);
                RenderTexture.active = src;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                RenderTexture.active = prev;
                var data = tex.GetRawTextureData<byte>();
                var buf = RentBytes(data.Length);
                data.CopyTo(buf);
                return buf;
            }
            catch (Exception e)
            {
                RenderTexture.active = prev;
                err = "ReadPixels failed: " + e.GetType().Name + ": " + e.Message;
                return null;
            }
            finally
            {
                if (tex != null) ReturnTex(tex);
            }
        }

        private void Encode(byte[] raw, int w, int h, CaptureRequest req, double mainMs, long t0, string path, Action<CaptureResult> done)
        {
            if (_h.Settings.JpegOnWorkerThread)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    byte[] enc;
                    try { enc = EncodeNow(raw, w, h, req); }
                    catch (Exception e)
                    {
                        // Spike 1 failed in practice: encode on the main thread from now on (13 §1.1).
                        _h.Settings.JpegOnWorkerThread = false;
                        _log.Warn("JPEG encoding off the main thread failed (" + e.GetType().Name + ": " + e.Message + "); switching to main-thread encoding");
                        _h.Dispatcher.EnqueueMain(new Job { Action = () => EncodeOnMain(raw, w, h, req, mainMs, t0, path, done) });
                        return;
                    }
                    Interlocked.Increment(ref WorkerEncodes);
                    ReturnBytes(raw);
                    Finish(done, enc, w, h, req, mainMs, t0, path, null);
                });
            }
            else EncodeOnMain(raw, w, h, req, mainMs, t0, path, done);
        }

        private void EncodeOnMain(byte[] raw, int w, int h, CaptureRequest req, double mainMs, long t0, string path, Action<CaptureResult> done)
        {
            long e0 = Clock.Ticks;
            byte[] enc = null;
            string err = null;
            try { enc = EncodeNow(raw, w, h, req); }
            catch (Exception e) { err = "image encoding failed: " + e.GetType().Name + ": " + e.Message; }
            mainMs += Clock.SinceMs(e0);
            Interlocked.Increment(ref MainEncodes);
            ReturnBytes(raw);
            Finish(done, enc, w, h, req, mainMs, t0, path, err);
        }

        internal static byte[] EncodeNow(byte[] raw, int w, int h, CaptureRequest req)
        {
            int q = Math.Max(1, Math.Min(100, req.Quality));
            return req.Png
                ? ImageConversion.EncodeArrayToPNG(raw, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0)
                : ImageConversion.EncodeArrayToJPG(raw, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, q);
        }

        private void Finish(Action<CaptureResult> done, byte[] enc, int w, int h, CaptureRequest req, double mainMs, long t0, string path, string err)
        {
            CaptureResult res;
            if (err != null || enc == null)
            {
                Interlocked.Increment(ref Failures);
                LastError = err ?? "encoder returned nothing";
                res = new CaptureResult { Error = new SkizzyError(ErrorKind.ProviderError, LastError, "capture_failed", "Try again; if it keeps failing run host.debug.spike {name:\"async_readback\"}."), Path = path, MainThreadMs = mainMs, TotalMs = Clock.SinceMs(t0) };
            }
            else
            {
                Interlocked.Increment(ref Captures);
                res = new CaptureResult
                {
                    Data = enc, Length = enc.Length, Width = w, Height = h, MimeType = req.Png ? "image/png" : "image/jpeg",
                    MainThreadMs = mainMs, TotalMs = Clock.SinceMs(t0), Path = path,
                };
            }
            try { done(res); }
            catch (Exception e) { _log.Warn("capture callback failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- pools (main thread for Unity objects)

        private static long Key(int w, int h) { return ((long)w << 32) | (uint)h; }

        internal RenderTexture RentRt(int w, int h)
        {
            long k = Key(w, h);
            _lastUse[k] = Time.realtimeSinceStartup;
            if (_rts.TryGetValue(k, out var s) && s.Count > 0)
            {
                _pooledRt--;
                var pooled = s.Pop();
                if (pooled != null && pooled.IsCreated()) return pooled;
                if (pooled != null) { UnityEngine.Object.Destroy(pooled); _liveRt--; }
            }
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Skizzy.Capture" };
            rt.Create();
            _liveRt++;
            StartJanitor();
            return rt;
        }

        internal void ReturnRt(RenderTexture rt)
        {
            if (rt == null) return;
            long k = Key(rt.width, rt.height);
            if (!_rts.TryGetValue(k, out var s)) _rts[k] = s = new Stack<RenderTexture>();
            s.Push(rt);
            _pooledRt++;
            _lastUse[k] = Time.realtimeSinceStartup;
        }

        private Texture2D RentTex(int w, int h)
        {
            long k = Key(w, h);
            _lastUse[k] = Time.realtimeSinceStartup;
            if (_texs.TryGetValue(k, out var s) && s.Count > 0)
            {
                _pooledTex--;
                var pooled = s.Pop();
                if (pooled != null) return pooled;
                _liveTex--;
            }
            _liveTex++;
            StartJanitor();
            return new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Skizzy.Readback" };
        }

        private void ReturnTex(Texture2D t)
        {
            long k = Key(t.width, t.height);
            if (!_texs.TryGetValue(k, out var s)) _texs[k] = s = new Stack<Texture2D>();
            s.Push(t);
            _pooledTex++;
        }

        /// <summary>Any thread. Exact-size arrays (NativeArray.CopyTo and the encoder need the exact length).</summary>
        internal byte[] RentBytes(int len)
        {
            lock (_bytes)
            {
                _bytesLastUse[len] = Clock.NowMs;
                if (_bytes.TryGetValue(len, out var s) && s.Count > 0) return s.Pop();
            }
            return new byte[len];
        }

        internal void ReturnBytes(byte[] b)
        {
            if (b == null) return;
            lock (_bytes)
            {
                if (!_bytes.TryGetValue(b.Length, out var s)) _bytes[b.Length] = s = new Stack<byte[]>();
                if (s.Count < 4) s.Push(b);
                _bytesLastUse[b.Length] = Clock.NowMs;
            }
        }

        private void StartJanitor()
        {
            if (_janitorRunning) return;
            _janitorRunning = true;
            _runner.StartCoroutine(Janitor());
        }

        private IEnumerator Janitor()
        {
            var wait = new WaitForSecondsRealtime(20f);
            while (true)
            {
                yield return wait;
                Trim(false);
                if (_liveRt == 0 && _liveTex == 0) break;
            }
            _janitorRunning = false;
        }

        /// <summary>Destroys pooled objects unused for 60 s (or all of them when <paramref name="all"/>).</summary>
        internal void Trim(bool all)
        {
            float now = Time.realtimeSinceStartup;
            var stale = new List<long>();
            foreach (var kv in _lastUse) if (all || now - kv.Value > PoolIdleS) stale.Add(kv.Key);
            foreach (var k in stale)
            {
                if (_rts.TryGetValue(k, out var rs))
                {
                    while (rs.Count > 0)
                    {
                        var rt = rs.Pop();
                        _pooledRt--;
                        _liveRt--;
                        if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                    }
                    _rts.Remove(k);
                }
                if (_texs.TryGetValue(k, out var ts))
                {
                    while (ts.Count > 0)
                    {
                        var t = ts.Pop();
                        _pooledTex--;
                        _liveTex--;
                        if (t != null) UnityEngine.Object.Destroy(t);
                    }
                    _texs.Remove(k);
                }
                _lastUse.Remove(k); // objects still rented out re-register when returned
            }
            long nowMs = Clock.NowMs;
            lock (_bytes)
            {
                var old = new List<int>();
                foreach (var kv in _bytesLastUse) if (all || nowMs - kv.Value > PoolIdleS * 1000) old.Add(kv.Key);
                foreach (var k in old) { _bytes.Remove(k); _bytesLastUse.Remove(k); }
            }
        }

        /// <summary>Unity object counts for the screenshot leak gate, minus Skizzy's own pooled objects (13 §1.1).</summary>
        internal JsonValue CountObjects()
        {
            int tex = Resources.FindObjectsOfTypeAll(typeof(Texture2D)).Length - _pooledTex;
            int rt = Resources.FindObjectsOfTypeAll(typeof(RenderTexture)).Length - _pooledRt;
            return JsonValue.Obj().Set("Texture2D", tex).Set("RenderTexture", rt);
        }

        // ---------------------------------------------------------------- camera control (13 §1.2)

        private static SkizzyError CheckFlight()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightCamera.fetch == null)
                return SkizzyError.Unavailable(KspWorld.SceneName(HighLogic.LoadedScene), new[] { "FLIGHT" }, "wrong_scene");
            return null;
        }

        private static SkizzyError CheckLoaded(GVessel v, string pointer)
        {
            var kv = v as KspVessel;
            if (kv == null || !kv.IsAlive) return SkizzyError.NotFound(v != null ? v.Handle : "?", "destroyed", "That vessel no longer exists; list vessels again.");
            if (!kv.V.loaded) return SkizzyError.Precondition("vessel_unloaded", kv.Name + " is outside the load range; the camera can only show loaded vessels", "Pick a vessel within physics range.");
            return null;
        }

        public SkizzyError SetMode(string mode, GVessel target, GVessel to, OrbitSpec orbit)
        {
            var e = CheckFlight();
            if (e != null) return e;
            if (mode == "look_at")
            {
                if (to == null) return SkizzyError.InvalidParam("/to", "look_at needs `to` (the vessel to keep in view)");
                return LookAt(target ?? _w.ActiveVessel, to, orbit.HasDistance ? orbit.Distance : 60, orbit.HasPitch ? orbit.Pitch : 10);
            }
            FlightCamera.Modes m;
            switch (mode)
            {
                case "follow": case "auto": m = FlightCamera.Modes.AUTO; break;
                case "orbit": case "orbital": m = FlightCamera.Modes.ORBITAL; break;
                case "chase": m = FlightCamera.Modes.CHASE; break;
                case "free": m = FlightCamera.Modes.FREE; break;
                case "locked": m = FlightCamera.Modes.LOCKED; break;
                default: return SkizzyError.InvalidParam("/mode", "unknown camera mode '" + mode + "' (follow, orbit, chase, free, look_at, locked)");
            }
            StopLookAt();
            if (target != null)
            {
                e = SetTarget(target);
                if (e != null) return e;
            }
            FlightCamera.SetModeImmediate(m);
            _mode = mode;
            return SetOrbit(orbit);
        }

        /// <summary>Frames to wait before checking that KSP kept the requested mode (it reverts ORBITAL in its own update).</summary>
        private const int ConfirmFrames = 15;

        public void ConfirmMode(Action<SkizzyError> done)
        {
            var cam = FlightCamera.fetch;
            if (cam == null) { done(null); return; }
            _runner.StartCoroutine(ConfirmRoutine(cam.mode, _mode, done));
        }

        private IEnumerator ConfirmRoutine(FlightCamera.Modes wanted, string asked, Action<SkizzyError> done)
        {
            for (int i = 0; i < ConfirmFrames; i++) yield return null;
            var cam = FlightCamera.fetch;
            if (cam == null || HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                done(SkizzyError.Unavailable(KspWorld.SceneName(HighLogic.LoadedScene), new[] { "FLIGHT" }, "wrong_scene"));
                yield break;
            }
            // AUTO resolves to a concrete mode by itself, so only explicit modes are checked.
            if (wanted == FlightCamera.Modes.AUTO || cam.mode == wanted) { done(null); yield break; }
            _mode = ModeName(cam.mode);
            string hint = wanted == FlightCamera.Modes.ORBITAL
                ? "KSP allows the orbital camera only away from the ground; use chase, free, follow or look_at here."
                : "Use another camera mode.";
            done(SkizzyError.Precondition("mode_rejected", "KSP switched the camera from " + asked + " (" + wanted + ") back to " + cam.mode, hint));
        }

        private static string ModeName(FlightCamera.Modes m)
        {
            switch (m)
            {
                case FlightCamera.Modes.AUTO: return "follow";
                case FlightCamera.Modes.ORBITAL: return "orbit";
                case FlightCamera.Modes.CHASE: return "chase";
                case FlightCamera.Modes.FREE: return "free";
                case FlightCamera.Modes.LOCKED: return "locked";
                default: return m.ToString().ToLowerInvariant();
            }
        }

        public SkizzyError SetTarget(GVessel target)
        {
            var e = CheckFlight() ?? CheckLoaded(target, "/target");
            if (e != null) return e;
            // Camera-only: never FlightGlobals.SetActiveVessel (13 §1.2, the active vessel must not change).
            FlightCamera.fetch.SetTargetVessel(((KspVessel)target).V);
            return null;
        }

        public SkizzyError SetOrbit(OrbitSpec o)
        {
            var e = CheckFlight();
            if (e != null) return e;
            var cam = FlightCamera.fetch;
            if (o.HasHeading) cam.camHdg = (float)(o.Heading * Deg2Rad);
            if (o.HasPitch) cam.camPitch = (float)(Math.Max(-89.0, Math.Min(89.0, o.Pitch)) * Deg2Rad);
            if (o.HasDistance)
            {
                float d = (float)o.Distance;
                d = Mathf.Max(cam.minDistance, Mathf.Min(cam.maxDistance, d));
                cam.SetDistanceImmediate(d);
            }
            return null;
        }

        public SkizzyError LookAt(GVessel from, GVessel to, double distance, double elevationDeg)
        {
            var e = CheckFlight() ?? CheckLoaded(from, "/from") ?? CheckLoaded(to, "/to");
            if (e != null) return e;
            FlightCamera.fetch.SetTargetVessel(((KspVessel)from).V);
            FlightCamera.SetModeImmediate(FlightCamera.Modes.FREE);
            _lookFrom = (KspVessel)from;
            _lookTo = (KspVessel)to;
            _lookDistance = Math.Max(5, Math.Min(5000, distance));
            _lookElevation = Math.Max(-80, Math.Min(80, elevationDeg));
            _mode = "look_at";
            ApplyLookAt();
            if (!_lookActive)
            {
                _lookActive = true;
                _runner.StartCoroutine(LookAtLoop());
            }
            return null;
        }

        private IEnumerator LookAtLoop()
        {
            while (_lookActive)
            {
                yield return null;
                if (_lookActive && !ApplyLookAt()) StopLookAt();
            }
        }

        /// <summary>Puts the camera on the far side of <c>from</c> from <c>to</c>, raised by the elevation. False = stop.</summary>
        private bool ApplyLookAt()
        {
            var cam = FlightCamera.fetch;
            if (cam == null || HighLogic.LoadedScene != GameScenes.FLIGHT) return false;
            if (_lookFrom == null || _lookTo == null || !_lookFrom.Loaded || !_lookTo.Loaded) return false;
            if (cam.mode != FlightCamera.Modes.FREE) return false; // the player switched modes: hand the camera back
            Vector3d fromPos = _lookFrom.V.CoMD, toPos = _lookTo.V.CoMD;
            Vector3d up = _lookFrom.V.upAxis;
            Vector3d away = fromPos - toPos;
            Vector3d horiz = Vector3d.Exclude(up, away);
            if (horiz.sqrMagnitude < 1e-6) horiz = Vector3d.Exclude(up, _lookFrom.V.north);
            horiz = horiz.normalized;
            double el = _lookElevation * Deg2Rad;
            Vector3d dir = horiz * Math.Cos(el) + up * Math.Sin(el);
            cam.SetCamCoordsFromPosition((Vector3)(fromPos + dir * _lookDistance));
            return true;
        }

        internal void StopLookAt()
        {
            _lookActive = false;
            _lookFrom = _lookTo = null;
            if (_mode == "look_at") _mode = "free";
        }

        internal void OnSceneChanging()
        {
            StopLookAt();
            _mode = "auto";
            Trim(true);
        }

        public JsonValue State()
        {
            var cam = FlightCamera.fetch;
            if (cam == null || HighLogic.LoadedScene != GameScenes.FLIGHT)
                return JsonValue.Obj().Set("mode", _mode).Set("available", false).Set("scene", KspWorld.SceneName(HighLogic.LoadedScene));
            var tgt = cam.vesselTarget != null ? _w.Wrap(cam.vesselTarget) : null;
            return JsonValue.Obj().Set("mode", _mode).Set("kspMode", cam.mode.ToString()).Set("target", tgt != null ? tgt.Handle : null)
                .Set("targetName", tgt != null ? tgt.Name : null).Set("heading", Math.Round(cam.camHdg * Rad2Deg, 2))
                .Set("pitch", Math.Round(cam.camPitch * Rad2Deg, 2)).Set("distance", Math.Round(cam.Distance, 1))
                .Set("lookAt", _lookActive && _lookTo != null ? _lookTo.Handle : null).Set("available", true);
        }
    }
}
