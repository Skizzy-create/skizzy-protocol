using System;
using System.IO;
using System.Threading;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Caps
{
    /// <summary>ksp.camera.* (13 §1): parameter handling shared by KSP and FakeKsp; the world does capture and control.</summary>
    public static class CameraCaps
    {
        public static void Register(HostCore h)
        {
            h.Register("ksp.camera.screenshot", c => Capture(c, false));
            h.Register("ksp.camera.save", c => Capture(c, true));
            h.Register("ksp.camera.set_mode", SetMode);
            h.Register("ksp.camera.target", Target);
            h.Register("ksp.camera.orbit", Orbit);
            h.Register("ksp.camera.look_at", LookAt);
            h.Register("ksp.camera.state", c => Cam(c).State());
        }

        private static ICameraService Cam(CallContext c)
        {
            var cam = c.Host.World.Camera;
            if (cam == null) throw SkizzyError.Precondition("api_missing", "no camera in this host", "Screenshots need the game (or FakeKsp's fake camera).");
            if (!cam.Available) throw SkizzyError.Precondition("menu_open", "the camera is not available right now (loading or a menu is open)", "Try again in a moment.");
            return cam;
        }

        internal static OrbitSpec ParseOrbit(JsonValue o)
        {
            var s = new OrbitSpec();
            if (o == null) return s;
            if (o.Has("heading")) { s.HasHeading = true; s.Heading = o.GetDouble("heading"); }
            if (o.Has("pitch")) { s.HasPitch = true; s.Pitch = o.GetDouble("pitch"); }
            if (o.Has("distance")) { s.HasDistance = true; s.Distance = o.GetDouble("distance"); }
            return s;
        }

        private static void Check(SkizzyError e) { if (e != null) throw e; }

        private static JsonValue Capture(CallContext c, bool save)
        {
            var h = c.Host;
            var cam = Cam(c);
            // Optional camera setup in the same call (13 §1: "chase view of DDG-1" in one call).
            if (c.Has("target") || c.Has("mode") || c.Has("orbit"))
            {
                var meta = h.Meta;
                if (meta.Scene != "FLIGHT")
                    throw SkizzyError.Unavailable(meta.Scene, new[] { "FLIGHT" }, "wrong_scene");
                GVessel target = c.Has("target") ? h.Handles.ResolveVessel(c.Str("target"), "/target") : null;
                if (c.Has("mode")) Check(cam.SetMode(c.Str("mode"), target, null, ParseOrbit(c.Params.GetObj("orbit"))));
                else
                {
                    if (target != null) Check(cam.SetTarget(target));
                    if (c.Has("orbit")) Check(cam.SetOrbit(ParseOrbit(c.Params.GetObj("orbit"))));
                }
            }
            var req = new CaptureRequest
            {
                Width = c.Int("width", save ? 1920 : 1024),
                Png = c.Str("format", save ? "png" : "jpeg") == "png",
                Quality = c.Int("quality", save ? 90 : 75),
                HideUi = c.Bool("hideUi", false),
            };
            string name = c.Str("name");
            bool returnImage = !save || c.Bool("returnImage", false);
            c.Defer();
            // Give a camera change one frame to apply before capturing.
            cam.Capture(req, res =>
            {
                if (res.Error != null) { c.Fail(res.Error); return; }
                h.Stats.Shots++;
                h.Stats.ShotMainMs.Add(res.MainThreadMs);
                h.Stats.ShotTotalMs.Add(res.TotalMs);
                h.Stats.LastShotPath = res.Path;
                var o = JsonValue.Obj().Set("width", res.Width).Set("height", res.Height).Set("mimeType", res.MimeType).Set("bytes", res.Length)
                    .Set("mainThreadMs", Math.Round(res.MainThreadMs, 3)).Set("totalMs", Math.Round(res.TotalMs, 3)).Set("pipeline", res.Path);
                var data = new byte[res.Length];
                Buffer.BlockCopy(res.Data, 0, data, 0, res.Length);
                if (returnImage) o.Set("image", JsonValue.Obj().Set("mimeType", res.MimeType).Set("data", JsonValue.Bytes(data)));
                if (!save) { c.Complete(o); return; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        string dir = h.Paths.ScreenshotsDir(DateTime.Now);
                        Directory.CreateDirectory(dir);
                        string stem = string.IsNullOrEmpty(name) ? DateTime.Now.ToString("HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) : name;
                        string ext = req.Png ? ".png" : ".jpg";
                        string path = Path.Combine(dir, stem + ext);
                        for (int i = 1; File.Exists(path); i++) path = Path.Combine(dir, stem + "-" + i + ext);
                        File.WriteAllBytes(path, data);
                        o.Set("path", h.Paths.Relative(path));
                        c.Complete(o);
                    }
                    catch (Exception e) { c.Fail(new SkizzyError(ErrorKind.ProviderError, "could not save the screenshot: " + e.Message)); }
                });
            });
            return null;
        }

        private static JsonValue SetMode(CallContext c)
        {
            var h = c.Host;
            var cam = Cam(c);
            string mode = c.Str("mode");
            GVessel target = c.Has("target") ? h.Handles.ResolveVessel(c.Str("target"), "/target") : null;
            GVessel to = c.Has("to") ? h.Handles.ResolveVessel(c.Str("to"), "/to") : null;
            if (mode == "look_at" && to == null) throw SkizzyError.InvalidParam("/to", "look_at needs `to` (the vessel to keep in view)");
            Check(cam.SetMode(mode, target, to, ParseOrbit(c.Params.GetObj("orbit"))));
            if (mode == "look_at") return JsonValue.Obj().Set("ok", true).Set("camera", cam.State());
            // PC test 2026-11-11: KSP accepted ORBITAL on the pad and switched back to CHASE half a second later, so the
            // call reported a mode the camera was not in. Answer only once the game has kept (or refused) the mode.
            c.Defer();
            cam.ConfirmMode(err =>
            {
                if (err != null) c.Fail(err);
                else c.Complete(JsonValue.Obj().Set("ok", true).Set("camera", cam.State()));
            });
            return null;
        }

        private static JsonValue Target(CallContext c)
        {
            var cam = Cam(c);
            var v = c.Host.Handles.ResolveVessel(c.Str("vessel"));
            Check(cam.SetTarget(v));
            return JsonValue.Obj().Set("ok", true).Set("camera", cam.State());
        }

        private static JsonValue Orbit(CallContext c)
        {
            var cam = Cam(c);
            Check(cam.SetOrbit(ParseOrbit(c.Params)));
            return JsonValue.Obj().Set("ok", true).Set("camera", cam.State());
        }

        private static JsonValue LookAt(CallContext c)
        {
            var h = c.Host;
            var cam = Cam(c);
            var from = h.Handles.ResolveVessel(c.Str("from"), "/from");
            var to = h.Handles.ResolveVessel(c.Str("to"), "/to");
            if (from == to) throw SkizzyError.InvalidParam("/to", "from and to must be different vessels");
            Check(cam.LookAt(from, to, c.Num("distance", 60), c.Num("elevationDeg", 10)));
            return JsonValue.Obj().Set("ok", true).Set("camera", cam.State());
        }
    }
}
