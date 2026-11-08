using System;
using System.IO;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    /// <summary><c>skizzy shot</c>: ksp.camera.screenshot written to a file, or ksp.camera.save (the host writes it).</summary>
    public static class ShotCommand
    {
        private static readonly string[] Modes = { "follow", "orbit", "chase", "free", "auto", "orbital", "locked" };

        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "shot",
            Summary = "take a screenshot: skizzy shot --target DDG-1 --mode chase --out chase.png",
            Usage = "skizzy shot [--out <file>] [--width <px>=1024] [--format png|jpg] [--target <vessel>] [--mode follow|orbit|chase|free] [--save <name>]\n" +
                    "             [--json] [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>] [--actor <id>] [--role <role>] [--side <side>]\n" +
                    "  Without --save the image is returned by ksp.camera.screenshot and written to --out (default shot-<yyyyMMdd-HHmmss>.<ext> here).\n" +
                    "  With --save <name> the host writes it under Skizzy/screenshots/<date>/ (ksp.camera.save) and the path is printed;\n" +
                    "  add --out as well to also download a copy. --mode accepts follow, orbit, chase, free (also auto, orbital, locked).\n" +
                    "  For a look_at view, run `skizzy call ksp.camera.set_mode mode=look_at target=A to=B` first.",
            Run = Run,
        };

        private static int Run(CliArgs a)
        {
            int width = a.Int("width", 1024);
            string format = a.Opt("format");
            if (format != null)
            {
                format = format.ToLowerInvariant();
                if (format == "jpg") format = "jpeg";
                if (format != "png" && format != "jpeg") throw new UsageException("--format must be png or jpg");
            }
            string mode = a.Opt("mode");
            if (mode != null)
            {
                mode = mode.ToLowerInvariant();
                if (mode == "look_at") throw new UsageException("the screenshot capability has no look_at mode; set it first with `skizzy call ksp.camera.set_mode mode=look_at target=A to=B`");
                if (Array.IndexOf(Modes, mode) < 0) throw new UsageException("--mode must be one of follow, orbit, chase, free, auto, orbital, locked");
            }
            string save = a.Opt("save");
            string outFile = a.Opt("out");
            var p = JsonValue.Obj().Set("width", width);
            if (format != null) p.Set("format", format);
            if (a.Opt("target") != null) p.Set("target", a.Opt("target"));
            if (mode != null) p.Set("mode", mode);
            if (save != null) p.Set("name", save);
            if (save != null && outFile != null) p.Set("returnImage", true);

            using (var c = ConnectionOptions.From(a, "cli", "script").Connect())
            {
                var res = c.Call(save != null ? "ksp.camera.save" : "ksp.camera.screenshot", p, 60);
                string mime = res.GetString("mimeType") ?? "image/png";
                string ext = mime.Contains("png") ? "png" : "jpg";
                int bytes = res.GetInt("bytes");
                string size = res.GetInt("width") + "x" + res.GetInt("height");
                string written = null;
                var img = res.GetObj("image");
                if (img != null && (save == null || outFile != null))
                {
                    byte[] data = Convert.FromBase64String(img.GetString("data") ?? "");
                    if (outFile == null) outFile = "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "." + ext;
                    File.WriteAllBytes(outFile, data);
                    written = outFile;
                    bytes = data.Length;
                }
                if (a.Flag("json"))
                {
                    var o = JsonValue.Parse(res.ToString());
                    if (o.GetObj("image") != null) o.GetObj("image").Set("data", "<" + bytes + " bytes written to " + (written ?? "-") + ">");
                    if (written != null) o.Set("out", Path.GetFullPath(written));
                    Console.Out.WriteLine(o.ToPrettyString());
                    return Program.ExitOk;
                }
                if (save != null) Console.Out.WriteLine("saved " + (res.GetString("path") ?? "?") + " (" + size + ", " + mime + ", " + bytes + " bytes)");
                if (written != null) Console.Out.WriteLine("wrote " + written + " (" + size + ", " + mime + ", " + bytes + " bytes)");
                return Program.ExitOk;
            }
        }
    }
}
