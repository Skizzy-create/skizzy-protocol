using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    /// <summary><c>skizzy log</c>: start, stop, list, tail and fetch log v0 files (rec.log.* and rec.get_file).</summary>
    public static class LogCommand
    {
        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "log",
            Summary = "record fields to CSV/JSONL: skizzy log start climb --fields altitude,srfSpeed",
            Usage = "skizzy log start <name> --fields a,b,c [--rate <Hz>=5] [--format csv|jsonl] [--vessels <selector JSON|name|glob|active>] [--onchange] [--deadband <x>]\n" +
                    "       skizzy log stop <name|log:N>\n" +
                    "       skizzy log list\n" +
                    "       skizzy log tail <name|log:N> [--lines <N>=20]\n" +
                    "       skizzy log get <name|log:N|path> [--out <file>]\n" +
                    "             [--json] [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>] [--actor <id>] [--role <role>] [--side <side>]\n" +
                    "  The host writes Skizzy/logs/<date>/<name>.<csv|jsonl> under the KSP folder (the path is printed, relative to it).\n" +
                    "  `get` downloads the file through rec.get_file (works for a log by name or id, or for a Skizzy/logs/... path); default --out is the file name in the current directory.",
            Flags = new[] { "onchange" },
            Run = Run,
        };

        private static int Run(CliArgs a)
        {
            string sub = a.Positional(0, "subcommand (start, stop, list, tail, get)").ToLowerInvariant();
            switch (sub)
            {
                case "start": return Start(a);
                case "stop": return Stop(a);
                case "list": return List(a);
                case "tail": return Tail(a);
                case "get": return Get(a);
                default: throw new UsageException("unknown log subcommand '" + sub + "' (start, stop, list, tail, get)");
            }
        }

        private static int Start(CliArgs a)
        {
            string name = a.Positional(1, "log name");
            var fields = Selectors.SplitList(new List<string>(new[] { a.Opt("fields") ?? "" }), 0);
            if (fields.Count == 0) throw new UsageException("log start needs --fields a,b,c");
            string format = (a.Opt("format") ?? "csv").ToLowerInvariant();
            if (format != "csv" && format != "jsonl") throw new UsageException("--format must be csv or jsonl");
            double rate = a.Num("rate", 5);
            if (rate <= 0) throw new UsageException("--rate must be greater than 0");
            var p = JsonValue.Obj().Set("name", name).Set("fields", JsonValue.ArrOf(fields.ToArray())).Set("rateHz", rate).Set("format", format);
            if (a.Opt("vessels") != null) p.Set("select", Selectors.Parse(a.Opt("vessels")));
            if (a.Flag("onchange"))
            {
                var oc = JsonValue.Obj();
                if (a.Has("deadband")) oc.Set("deadband", a.Num("deadband", 0));
                p.Set("onChange", oc);
            }
            else if (a.Has("deadband")) throw new UsageException("--deadband needs --onchange");
            using (var c = ConnectionOptions.From(a, "cli", "script").Connect())
            {
                var res = c.Call("rec.log.start", p, 30);
                if (a.Flag("json")) { Console.Out.WriteLine(res.ToPrettyString()); return Program.ExitOk; }
                Console.Out.WriteLine(res.GetString("log") + " started: " + res.GetString("path") + " (" + res.GetInt("targets") + " targets, " +
                                      Num(res.GetDouble("rateHz")) + " Hz, " + res.GetString("format") + ")");
                if (res.GetString("warning") != null) Console.Error.WriteLine("warning: " + res.GetString("warning"));
                var unk = res.GetArr("unknownFields");
                if (unk != null && unk.Count > 0)
                {
                    var names = new List<string>();
                    for (int i = 0; i < unk.Count; i++) names.Add(unk[i].AsString(""));
                    Console.Error.WriteLine("warning: unknown fields: " + string.Join(", ", names));
                }
                return Program.ExitOk;
            }
        }

        private static int Stop(CliArgs a)
        {
            string log = a.Positional(1, "log name or id (log:N)");
            using (var c = ConnectionOptions.From(a, "cli", "script").Connect())
            {
                var res = c.Call("rec.log.stop", JsonValue.Obj().Set("log", log), 30);
                if (a.Flag("json")) { Console.Out.WriteLine(res.ToPrettyString()); return Program.ExitOk; }
                Console.Out.WriteLine(res.GetString("log") + " " + res.GetString("state") + ": " + res.GetString("path") + " (" + res.GetLong("rows") + " rows, " + res.GetLong("bytes") + " bytes)");
                return Program.ExitOk;
            }
        }

        private static int List(CliArgs a)
        {
            using (var c = ConnectionOptions.From(a, "cli", "script").Connect())
            {
                var res = c.Call("rec.log.list", JsonValue.Obj(), 30);
                if (a.Flag("json")) { Console.Out.WriteLine(res.ToPrettyString()); return Program.ExitOk; }
                var logs = res.GetArr("logs");
                if (logs == null || logs.Count == 0) { Console.Out.WriteLine("no logs"); return Program.ExitOk; }
                var rows = new List<string[]> { new[] { "LOG", "NAME", "STATE", "ROWS", "BYTES", "HZ", "PATH" } };
                for (int i = 0; i < logs.Count; i++)
                {
                    var l = logs[i];
                    rows.Add(new[] { l.GetString("log", "?"), l.GetString("name", "?"), l.GetString("state", "?"), l.GetLong("rows").ToString(CultureInfo.InvariantCulture),
                        l.GetLong("bytes").ToString(CultureInfo.InvariantCulture), Num(l.GetDouble("rateHz")), l.GetString("path", "") });
                }
                var w = new int[7];
                foreach (var r in rows) for (int i = 0; i < 7; i++) w[i] = Math.Max(w[i], r[i].Length);
                foreach (var r in rows)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < 7; i++)
                    {
                        if (i > 0) sb.Append("  ");
                        sb.Append(i == 3 || i == 4 || i == 5 ? r[i].PadLeft(w[i]) : r[i].PadRight(w[i]));
                    }
                    Console.Out.WriteLine(sb.ToString().TrimEnd());
                }
                return Program.ExitOk;
            }
        }

        private static int Tail(CliArgs a)
        {
            string log = a.Positional(1, "log name or id (log:N)");
            int lines = a.Int("lines", 20);
            if (lines < 1) throw new UsageException("--lines must be at least 1");
            using (var c = ConnectionOptions.From(a, "cli", "script").Connect())
            {
                var p = JsonValue.Obj().Set("log", log).Set("n", lines).Set("format", a.Flag("json") ? "json" : "compact");
                var res = c.Call("rec.log.tail", p, 30);
                if (a.Flag("json")) { Console.Out.WriteLine(res.ToPrettyString()); return Program.ExitOk; }
                Console.Out.WriteLine((res.GetString("text") ?? "").TrimEnd('\n'));
                return Program.ExitOk;
            }
        }

        private static int Get(CliArgs a)
        {
            string what = a.Positional(1, "log name, id (log:N) or Skizzy/logs/... path");
            using (var c = ConnectionOptions.From(a, "cli", "script").Connect())
            {
                string path = null;
                bool looksLikePath = what.IndexOf('/') >= 0 || what.IndexOf('\\') >= 0;
                if (looksLikePath) path = what.Replace('\\', '/');
                else
                {
                    var logs = c.Call("rec.log.list", JsonValue.Obj(), 30).GetArr("logs");
                    for (int i = logs == null ? -1 : logs.Count - 1; i >= 0; i--)
                        if (logs[i].GetString("log") == what || logs[i].GetString("name") == what) { path = logs[i].GetString("path"); break; }
                    if (path == null) throw SkizzyError.NotFound(what, "unknown", "Unknown log; list logs with `skizzy log list`.");
                }
                string outFile = a.Opt("out") ?? Path.GetFileName(path);
                long offset = 0, total = 0;
                using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    while (true)
                    {
                        var res = c.Call("rec.get_file", JsonValue.Obj().Set("path", path).Set("offset", offset), 60);
                        string b64 = res.GetString("data");
                        byte[] chunk = string.IsNullOrEmpty(b64) ? new byte[0] : Convert.FromBase64String(b64);
                        fs.Write(chunk, 0, chunk.Length);
                        offset += chunk.Length;
                        total = res.GetLong("size");
                        if (res.GetBool("eof") || chunk.Length == 0) break;
                    }
                }
                if (a.Flag("json")) Console.Out.WriteLine(JsonValue.Obj().Set("path", path).Set("out", Path.GetFullPath(outFile)).Set("bytes", offset).Set("size", total).ToPrettyString());
                else Console.Out.WriteLine("wrote " + outFile + " (" + offset + " bytes) from " + path);
                return Program.ExitOk;
            }
        }

        private static string Num(double d)
        {
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
