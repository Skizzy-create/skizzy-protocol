using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Skizzy.Client;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    public sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    /// <summary>
    /// Parsed command line: positionals plus <c>--name value</c>, <c>--name=value</c> and boolean <c>--flag</c>
    /// (a flag is only boolean if the command lists it, so <c>--ksp D:\KSP</c> never eats a positional by mistake).
    /// </summary>
    public sealed class CliArgs
    {
        public readonly List<string> Positionals = new List<string>();
        private readonly Dictionary<string, string> _opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Boolean options every command accepts.</summary>
        public static readonly string[] CommonFlags = { "help", "json", "quiet", "verbose" };

        public static CliArgs Parse(string[] args, int start, string[] flags)
        {
            var known = new HashSet<string>(CommonFlags, StringComparer.OrdinalIgnoreCase);
            if (flags != null) foreach (var f in flags) known.Add(f);
            var a = new CliArgs();
            bool rest = false;
            for (int i = start; i < args.Length; i++)
            {
                string t = args[i];
                if (!rest && t == "--") { rest = true; continue; }
                if (rest || !t.StartsWith("--", StringComparison.Ordinal))
                {
                    a.Positionals.Add(t);
                    continue;
                }
                string name = t.Substring(2);
                string value = null;
                int eq = name.IndexOf('=');
                if (eq >= 0) { value = name.Substring(eq + 1); name = name.Substring(0, eq); }
                if (name.Length == 0) throw new UsageException("empty option name");
                if (known.Contains(name))
                {
                    if (value != null && value != "true" && value != "false") throw new UsageException("--" + name + " takes no value");
                    if (value != "false") a._flags.Add(name);
                    continue;
                }
                if (value == null)
                {
                    if (i + 1 >= args.Length) throw new UsageException("--" + name + " needs a value");
                    value = args[++i];
                }
                a._opts[name] = value;
            }
            return a;
        }

        public bool Flag(string name) { return _flags.Contains(name); }
        public string Opt(string name, string def = null) { return _opts.TryGetValue(name, out var v) ? v : def; }
        public bool Has(string name) { return _opts.ContainsKey(name) || _flags.Contains(name); }
        public IEnumerable<string> OptionNames => _opts.Keys;

        public double Num(string name, double def)
        {
            string v = Opt(name);
            if (v == null) return def;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) throw new UsageException("--" + name + " must be a number");
            return d;
        }

        public int Int(string name, int def)
        {
            string v = Opt(name);
            if (v == null) return def;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)) throw new UsageException("--" + name + " must be an integer");
            return d;
        }

        public string Positional(int i, string what)
        {
            if (i >= Positionals.Count) throw new UsageException("missing " + what);
            return Positionals[i];
        }
    }

    /// <summary>Connection options shared by every command, and the connect helper.</summary>
    public sealed class ConnectionOptions
    {
        public string Ksp, EndpointFile, TokenFile;
        public string ActorId, ActorKind, Role, Side, ActorToken, ClientName, ClientKind;
        public string McpClient;

        public static ConnectionOptions From(CliArgs a, string clientKind, string defaultActorKind)
        {
            return new ConnectionOptions
            {
                Ksp = a.Opt("ksp"),
                EndpointFile = a.Opt("endpoint"),
                TokenFile = a.Opt("token-file"),
                ActorId = a.Opt("actor"),
                ActorKind = a.Opt("actor-kind", defaultActorKind),
                Role = a.Opt("role"),
                Side = a.Opt("side"),
                ActorToken = a.Opt("actor-token"),
                ClientName = a.Opt("client-name", clientKind == "bridge" ? "skizzy-bridge" : "skizzy-cli"),
                ClientKind = clientKind,
            };
        }

        public Endpoint Discover(List<string> searched = null)
        {
            return Endpoint.Discover(EndpointFile, Ksp, TokenFile, searched);
        }

        public HelloOptions Hello()
        {
            return new HelloOptions
            {
                ClientName = ClientName,
                ClientKind = ClientKind,
                ActorKind = ActorKind,
                ActorId = ActorId,
                Role = Role,
                Side = Side,
                McpClient = McpClient,
                DelegationToken = ActorToken,
            };
        }

        /// <summary>Connects once (no auto-reconnect): CLI commands are short-lived.</summary>
        public SkizzyClient Connect(double timeoutS = 5)
        {
            var c = new SkizzyClient();
            var ep = Discover();
            var task = c.ConnectAsync(ep, Hello());
            bool done;
            try { done = task.Wait(TimeSpan.FromSeconds(timeoutS)); }
            catch (AggregateException) { done = true; }
            if (!done)
            {
                c.Dispose();
                throw new SkizzyClientException("CONNECT_TIMEOUT", "no answer from " + ep.Url + " within " + timeoutS.ToString(CultureInfo.InvariantCulture) + " s",
                    "Is KSP running and past the loading screen? `skizzy status` shows what was found.");
            }
            try { task.GetAwaiter().GetResult(); }
            catch (Exception) { c.Dispose(); throw; }
            return c;
        }
    }

    /// <summary>Parameter parsing and output rendering shared by the CLI and the bridge.</summary>
    public static class Render
    {
        /// <summary>"KIND: reason - hint" (first line), then the message (03 §9, bridge rendering).</summary>
        public static string Error(SkizzyError e)
        {
            var sb = new StringBuilder();
            sb.Append(ErrorKinds.Name(e.Kind)).Append(": ").Append(e.Reason ?? "error");
            if (!string.IsNullOrEmpty(e.Hint)) sb.Append(" - ").Append(e.Hint);
            if (!string.IsNullOrEmpty(e.Message)) sb.Append('\n').Append(e.Message);
            return sb.ToString();
        }

        public static string ClientError(SkizzyClientException e)
        {
            var sb = new StringBuilder("UNAVAILABLE: ");
            sb.Append(e.Code == "NO_ENDPOINT" || e.Code == "NO_TOKEN" ? "game_not_running" : e.Code.ToLowerInvariant());
            if (!string.IsNullOrEmpty(e.Hint)) sb.Append(" - ").Append(e.Hint);
            if (!string.IsNullOrEmpty(e.Message)) sb.Append('\n').Append(e.Message);
            return sb.ToString();
        }

        /// <summary>
        /// Builds params from CLI positionals: one JSON object, <c>@file.json</c>, <c>-</c> (stdin), or
        /// <c>key=value</c> pairs (value parsed as JSON when it is valid JSON, else a string; dotted keys nest).
        /// </summary>
        public static JsonValue ParseParams(IList<string> items, int start)
        {
            var p = JsonValue.Obj();
            for (int i = start; i < items.Count; i++)
            {
                string t = items[i];
                if (i == start && items.Count == start + 1)
                {
                    string text = null;
                    if (t == "-") text = Console.In.ReadToEnd();
                    else if (t.StartsWith("@", StringComparison.Ordinal)) text = File.ReadAllText(t.Substring(1));
                    else if (t.TrimStart().StartsWith("{", StringComparison.Ordinal)) text = t;
                    if (text != null)
                    {
                        JsonValue v;
                        try { v = JsonValue.Parse(text); }
                        catch (Exception e) { throw new UsageException("params are not valid JSON: " + e.Message); }
                        if (v.Kind != JsonKind.Object) throw new UsageException("params must be a JSON object");
                        return v;
                    }
                }
                int eq = t.IndexOf('=');
                if (eq <= 0) throw new UsageException("expected key=value, a JSON object, @file.json or -, got '" + t + "'");
                SetPath(p, t.Substring(0, eq), ParseScalar(t.Substring(eq + 1)));
            }
            return p;
        }

        public static JsonValue ParseScalar(string raw)
        {
            if (raw.Length == 0) return JsonValue.Str("");
            char c = raw[0];
            if (c == '{' || c == '[' || c == '"' || c == '-' || (c >= '0' && c <= '9') || raw == "true" || raw == "false" || raw == "null")
            {
                try { return JsonValue.Parse(raw); }
                catch (Exception) { }
            }
            return JsonValue.Str(raw);
        }

        private static void SetPath(JsonValue root, string key, JsonValue v)
        {
            string[] parts = key.Split('.');
            var cur = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var next = cur.Get(parts[i]);
                if (next == null || next.Kind != JsonKind.Object) { next = JsonValue.Obj(); cur.Set(parts[i], next); }
                cur = next;
            }
            cur.Set(parts[parts.Length - 1], v);
        }

        /// <summary>
        /// Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes at a line boundary and adds
        /// a "...N more lines, use cursor" trailer (03 §11 output caps).
        /// </summary>
        public static string CutText(string text, int maxBytes, string moreHint = "use cursor")
        {
            if (text == null || maxBytes <= 0) return text;
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
            int budget = maxBytes - 64;
            if (budget < 64) budget = 64;
            int bytes = 0, end = 0;
            for (; end < text.Length; end++)
            {
                char ch = text[end];
                int n = ch < 0x80 ? 1 : ch < 0x800 ? 2 : char.IsSurrogate(ch) ? 2 : 3;
                if (bytes + n > budget) break;
                bytes += n;
            }
            if (end > 0 && end < text.Length && char.IsLowSurrogate(text[end])) end--;
            int nl = end > 0 ? text.LastIndexOf('\n', end - 1) : -1;
            int cut = nl > end / 2 ? nl : end;
            string rest = text.Substring(cut);
            int moreLines = 0;
            foreach (char ch in rest) if (ch == '\n') moreLines++;
            if (moreLines == 0) moreLines = 1;
            string trailer = "\n\u2026" + moreLines.ToString(CultureInfo.InvariantCulture) + " more line" + (moreLines == 1 ? "" : "s") + ", " + moreHint;
            return text.Substring(0, cut).TrimEnd('\r', '\n') + trailer;
        }

        /// <summary>One short line of result meta: scene, UT, pause, warp, kill switch.</summary>
        public static string MetaLine(JsonValue meta)
        {
            if (meta == null || meta.Kind != JsonKind.Object) return null;
            var sb = new StringBuilder("[");
            sb.Append(meta.GetString("scene") ?? "?");
            double ut = meta.GetDouble("ut", double.NaN);
            if (!double.IsNaN(ut)) sb.Append(" ut ").Append(Math.Round(ut, 1).ToString(CultureInfo.InvariantCulture));
            if (meta.GetBool("paused")) sb.Append(" PAUSED");
            var warp = meta.GetObj("warp");
            double rate = warp != null ? warp.GetDouble("rate", 1) : 1;
            if (rate > 1) sb.Append(" warp x").Append(rate.ToString(CultureInfo.InvariantCulture));
            var ks = meta.Get("killSwitch");
            if (ks != null && ks.Kind != JsonKind.Null && !(ks.Kind == JsonKind.Bool && !meta.GetBool("killSwitch"))) sb.Append(" KILL-SWITCH");
            sb.Append(']');
            return sb.ToString();
        }
    }
}
