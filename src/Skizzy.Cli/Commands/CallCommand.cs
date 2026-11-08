using System;
using System.IO;
using Skizzy.Client;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    /// <summary>
    /// <c>skizzy call &lt;capability|method&gt; [params]</c>: one JSON-RPC call, printed as text (compact results) or JSON.
    /// </summary>
    public static class CallCommand
    {
        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "call",
            Summary = "call any capability: skizzy call ksp.read vessel=DDG-1 format=compact",
            Usage = "skizzy call <capability> [key=value ...|'{json}'|@params.json|-] [--json] [--raw] [--out <image file>] [--timeout <s>]\n" +
                    "             [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>] [--actor <id>] [--actor-kind script|human] [--role <role>] [--side <side>]\n" +
                    "  Values are JSON when they parse as JSON (numbers, true/false, [..], {..}), else strings; a.b=1 nests.\n" +
                    "  Output: the result's text field when it has one, else indented JSON. --json prints the whole result including meta;\n" +
                    "  --raw prints compact JSON. Screenshot images are written with --out, otherwise elided.\n" +
                    "  Exit codes: 0 ok, 1 host error (\"KIND: reason - hint\" on stderr), 2 usage, 3 not connected.",
            Flags = new[] { "raw" },
            Run = Run,
        };

        private static int Run(CliArgs a)
        {
            string method = a.Positional(0, "capability name (e.g. ksp.vessels.list)");
            var prms = Render.ParseParams(a.Positionals, 1);
            double timeout = a.Num("timeout", 60);
            var conn = ConnectionOptions.From(a, "cli", "script");
            using (var c = conn.Connect())
            {
                JsonValue res;
                try { res = c.Call(method, prms, timeout); }
                catch (SkizzyError e)
                {
                    if (a.Flag("json")) Console.Out.WriteLine(e.ToErrorObject().ToPrettyString());
                    Console.Error.WriteLine(Render.Error(e));
                    return Program.ExitHostError;
                }
                Console.Out.WriteLine(Format(res, a.Flag("json"), a.Flag("raw"), a.Opt("out"), a.Flag("quiet")));
                return Program.ExitOk;
            }
        }

        /// <summary>Formats a result for the terminal. Writes an embedded image to <paramref name="outFile"/> if given.</summary>
        public static string Format(JsonValue res, bool json, bool raw, string outFile, bool quiet)
        {
            if (res == null) return "null";
            var img = res.Kind == JsonKind.Object ? res.GetObj("image") : null;
            if (img != null)
            {
                string b64 = img.GetString("data");
                byte[] bytes = null;
                if (b64 != null) { try { bytes = Convert.FromBase64String(b64); } catch (FormatException) { } }
                if (outFile != null && bytes != null)
                {
                    File.WriteAllBytes(outFile, bytes);
                    if (!quiet) Console.Error.WriteLine("image written: " + outFile + " (" + bytes.Length + " bytes)");
                }
                var copy = JsonValue.Parse(res.ToString());
                copy.GetObj("image").Set("data", "<" + (bytes != null ? bytes.Length : 0) + " bytes elided; use --out FILE>");
                res = copy;
            }
            if (json) return res.ToPrettyString();
            if (raw) return res.ToString();
            string text = res.Kind == JsonKind.Object ? res.GetString("text") : null;
            if (text != null)
            {
                string more = "";
                if (res.GetBool("truncated"))
                {
                    long total = res.GetLong("total");
                    more = "\n(" + (total > 0 ? total + " total; " : "") + "next page: cursor=" + res.GetString("cursor") + ")";
                }
                return text.TrimEnd('\n') + more;
            }
            if (res.Kind == JsonKind.Object && res.Get("meta") != null)
            {
                var copy = JsonValue.Parse(res.ToString());
                copy.Remove("meta");
                return copy.ToPrettyString();
            }
            return res.ToPrettyString();
        }
    }

    /// <summary><c>skizzy status</c>: where the host is, whether it answers, and the game state. Never needs the game.</summary>
    public static class StatusCommand
    {
        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "status",
            Summary = "show whether the game is reachable, and the host state",
            Usage = "skizzy status [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>] [--json]",
            Run = Run,
        };

        private static int Run(CliArgs a)
        {
            var conn = ConnectionOptions.From(a, "cli", "script");
            var searched = new System.Collections.Generic.List<string>();
            Endpoint ep;
            try { ep = conn.Discover(searched); }
            catch (SkizzyClientException e)
            {
                if (a.Flag("json")) Console.Out.WriteLine(JsonValue.Obj().Set("connected", false).Set("error", e.Message).Set("hint", e.Hint).ToPrettyString());
                else Console.Out.WriteLine("not connected: " + e.Message + "\n  " + e.Hint);
                return Program.ExitNotConnected;
            }
            SkizzyClient c;
            try { c = conn.Connect(); }
            catch (SkizzyClientException e)
            {
                if (a.Flag("json")) Console.Out.WriteLine(JsonValue.Obj().Set("connected", false).Set("url", ep.Url).Set("pluginData", ep.PluginData).Set("error", e.Message).Set("hint", e.Hint).ToPrettyString());
                else Console.Out.WriteLine("not connected: " + ep.Url + " (" + e.Message + ")\n  PluginData: " + ep.PluginData + "\n  " + e.Hint);
                return Program.ExitNotConnected;
            }
            using (c)
            {
                var state = c.Call("host.state", JsonValue.Obj(), 10);
                var hello = c.HelloResult;
                if (a.Flag("json"))
                {
                    Console.Out.WriteLine(JsonValue.Obj().Set("connected", true).Set("url", ep.Url).Set("pluginData", ep.PluginData)
                        .Set("session", c.SessionId).Set("hostVersion", hello.GetString("hostVersion")).Set("role", hello.GetString("role"))
                        .Set("state", state).ToPrettyString());
                    return Program.ExitOk;
                }
                Console.Out.WriteLine("connected: " + ep.Url + " (host " + hello.GetString("hostVersion") + ", protocol " + hello.GetString("version") + ")");
                Console.Out.WriteLine("  session " + c.SessionId + ", role " + hello.GetString("role") + ", PluginData " + ep.PluginData);
                Console.Out.WriteLine("  " + Render.MetaLine(state.GetObj("meta")));
                return Program.ExitOk;
            }
        }
    }
}
