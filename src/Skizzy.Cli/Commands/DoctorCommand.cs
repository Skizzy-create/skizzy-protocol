using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Skizzy.Client;
using Skizzy.Protocol;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    /// <summary>
    /// <c>skizzy doctor</c> (v0, read-only): checks the KSP folder, the install layout, the PluginData files, the live
    /// connection, versions, the host's own doctor/selfcheck and KSP.log. Never prints the token.
    /// </summary>
    public static class DoctorCommand
    {
        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "doctor",
            Summary = "check the install, the connection and the host (read-only)",
            Usage = "skizzy doctor [--json] [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>] [--actor <id>] [--role <role>] [--side <side>]\n" +
                    "  Prints `PASS|WARN|FAIL|INFO  <check> - <detail>` lines; exit 0 when nothing FAILs, 1 otherwise.\n" +
                    "  Checks the KSP folder and KSP_x64.exe, GameData/SkizzyProtocol/Plugins, stray Skizzy*.dll copies, auth.token (length only),\n" +
                    "  endpoint.json and its pid, the port, connect + hello time, host/protocol versions, host.doctor, host.selfcheck,\n" +
                    "  KSPCommunityFixes, BDArmory and Skizzy errors in KSP.log. --fix is not available in doctor v0.",
            Flags = new[] { "fix" },
            Run = Run,
        };

        private sealed class Check
        {
            public string Status, Name, Detail;
        }

        private static int Run(CliArgs a)
        {
            var checks = new List<Check>();
            Action<string, string, string> add = (s, n, d) => checks.Add(new Check { Status = s, Name = n, Detail = (d ?? "").Replace("\r", "").Replace('\n', ' ') });
            var conn = ConnectionOptions.From(a, "cli", "script");

            if (a.Flag("fix")) add("INFO", "fix", "not available in doctor v0");

            ResolveFolders(conn, out string ksp, out string pluginData, out string source);
            ChecksFolder(add, ksp, pluginData, source);
            if (ksp != null && Directory.Exists(ksp)) ChecksInstall(add, ksp);
            string url = ChecksPluginData(add, conn, pluginData);
            ChecksConnection(add, conn, url);
            if (ksp != null && Directory.Exists(ksp))
            {
                ChecksMods(add, ksp);
                ChecksLog(add, ksp);
            }

            int fails = 0;
            foreach (var c in checks) if (c.Status == "FAIL") fails++;
            if (a.Flag("json"))
            {
                var arr = JsonValue.Arr();
                foreach (var c in checks) arr.Add(JsonValue.Obj().Set("status", c.Status).Set("check", c.Name).Set("detail", c.Detail));
                Console.Out.WriteLine(JsonValue.Obj().Set("ok", fails == 0).Set("fails", fails).Set("checks", arr).ToPrettyString());
            }
            else
            {
                foreach (var c in checks) Console.Out.WriteLine(c.Status + "  " + c.Name + " - " + c.Detail);
            }
            return fails == 0 ? Program.ExitOk : Program.ExitHostError;
        }

        // ------------------------------------------------------------------ folders

        /// <summary>Same order as <see cref="Endpoint.Discover"/>, but never throws; the KSP folder is derived from PluginData.</summary>
        private static void ResolveFolders(ConnectionOptions o, out string ksp, out string pluginData, out string source)
        {
            ksp = null; pluginData = null; source = null;
            string envEp = Environment.GetEnvironmentVariable("SKIZZY_ENDPOINT");
            string envKsp = Environment.GetEnvironmentVariable("KSPDIR");
            try
            {
                if (!string.IsNullOrEmpty(o.EndpointFile)) { pluginData = Path.GetDirectoryName(Path.GetFullPath(o.EndpointFile)); source = "--endpoint"; }
                else if (!string.IsNullOrEmpty(o.Ksp)) { ksp = Path.GetFullPath(o.Ksp); source = "--ksp"; }
                else if (!string.IsNullOrEmpty(envEp)) { pluginData = Path.GetDirectoryName(Path.GetFullPath(envEp)); source = "SKIZZY_ENDPOINT"; }
                else if (!string.IsNullOrEmpty(envKsp)) { ksp = Path.GetFullPath(envKsp); source = "KSPDIR"; }
                else
                {
                    string sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "PluginData"));
                    if (File.Exists(Path.Combine(sibling, "auth.token"))) { pluginData = sibling; source = "PluginData next to the exe"; }
                    else
                    {
                        string ptr = Endpoint.UserPointerPath();
                        if (ptr != null && File.Exists(ptr))
                        {
                            try { pluginData = JsonValue.Parse(File.ReadAllText(ptr)).GetString("pluginData"); } catch (Exception) { pluginData = null; }
                            if (pluginData != null) source = "per-user pointer " + ptr;
                        }
                    }
                }
            }
            catch (Exception) { }
            if (ksp != null && pluginData == null) pluginData = Endpoint.PluginDataFor(ksp);
            if (pluginData != null && ksp == null) ksp = KspFromPluginData(pluginData);
        }

        /// <summary>KSP/GameData/SkizzyProtocol/PluginData to KSP, or null when the folder does not look like that.</summary>
        private static string KspFromPluginData(string pluginData)
        {
            try
            {
                string pd = pluginData.TrimEnd('/', '\\');
                if (!string.Equals(Path.GetFileName(pd), "PluginData", StringComparison.OrdinalIgnoreCase)) return null;
                string skizzy = Path.GetDirectoryName(pd);
                if (skizzy == null || !string.Equals(Path.GetFileName(skizzy), "SkizzyProtocol", StringComparison.OrdinalIgnoreCase)) return null;
                string gameData = Path.GetDirectoryName(skizzy);
                if (gameData == null || !string.Equals(Path.GetFileName(gameData), "GameData", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.GetDirectoryName(gameData);
            }
            catch (Exception) { return null; }
        }

        private static void ChecksFolder(Action<string, string, string> add, string ksp, string pluginData, string source)
        {
            if (ksp == null)
            {
                if (pluginData != null) add("WARN", "KSP folder", "PluginData " + pluginData + " (" + source + ") is not under <KSP>/GameData/SkizzyProtocol; KSP-folder checks skipped");
                else add("FAIL", "KSP folder", "not found; pass --ksp <KSP folder>, set KSPDIR, or start the game once so it writes the per-user pointer");
                return;
            }
            if (!Directory.Exists(ksp)) { add("FAIL", "KSP folder", ksp + " does not exist (" + source + ")"); return; }
            add("PASS", "KSP folder", ksp + " (" + (source ?? "derived from PluginData") + ")");
            string exe = Path.Combine(ksp, "KSP_x64.exe");
            if (File.Exists(exe)) add("PASS", "KSP_x64.exe", "present");
            else add("FAIL", "KSP_x64.exe", "not found in " + ksp + "; is this the KSP root?");
        }

        // ------------------------------------------------------------------ install layout

        private static void ChecksInstall(Action<string, string, string> add, string ksp)
        {
            string gameData = Path.Combine(ksp, "GameData");
            string plugins = Path.Combine(Path.Combine(gameData, "SkizzyProtocol"), "Plugins");
            foreach (var dll in new[] { "Skizzy.Host.dll", "Skizzy.Protocol.dll" })
            {
                string f = Path.Combine(plugins, dll);
                if (File.Exists(f)) add("PASS", "install " + dll, "GameData/SkizzyProtocol/Plugins/" + dll + " (" + new FileInfo(f).Length + " bytes)");
                else add("FAIL", "install " + dll, "missing: GameData/SkizzyProtocol/Plugins/" + dll);
            }
            if (!Directory.Exists(gameData)) return;
            string bin = Path.Combine(Path.Combine(gameData, "SkizzyProtocol"), "Bin");
            var others = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(gameData, "*.dll", SearchOption.AllDirectories))
                {
                    string n = Path.GetFileName(f);
                    // Only our own file names: other mods ship DLLs that merely start with "Skizzy" (e.g. SkizzyRig.dll).
                    if (!n.Equals("Skizzy.Host.dll", StringComparison.OrdinalIgnoreCase) && !n.Equals("Skizzy.Protocol.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string dir = Path.GetDirectoryName(f);
                    if (SamePath(dir, plugins) || SamePath(dir, bin)) continue;
                    others.Add(f.Substring(ksp.Length).TrimStart('/', '\\').Replace('\\', '/'));
                }
            }
            catch (Exception e) { add("INFO", "install copies", "could not scan GameData: " + e.Message); return; }
            if (others.Count == 0) add("PASS", "install copies", "no other Skizzy.Host.dll or Skizzy.Protocol.dll under GameData");
            else add("WARN", "install copies", others.Count + " other copies of Skizzy.Host.dll or Skizzy.Protocol.dll (duplicates can load the wrong version): " + string.Join(", ", others.GetRange(0, Math.Min(others.Count, 5)).ToArray()));
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ PluginData

        /// <summary>auth.token (length only) and endpoint.json. Returns the host URL to probe, or null.</summary>
        private static string ChecksPluginData(Action<string, string, string> add, ConnectionOptions conn, string pluginData)
        {
            string tokenFile = !string.IsNullOrEmpty(conn.TokenFile) ? conn.TokenFile : (pluginData != null ? Path.Combine(pluginData, "auth.token") : null);
            if (tokenFile == null) add("FAIL", "auth.token", "PluginData unknown");
            else if (!File.Exists(tokenFile)) add("FAIL", "auth.token", "not found at " + tokenFile + "; start KSP once with Skizzy installed (it creates the token)");
            else
            {
                int len = 0;
                try { len = File.ReadAllText(tokenFile).Trim().Length; } catch (Exception e) { add("FAIL", "auth.token", "cannot read " + tokenFile + ": " + e.Message); tokenFile = null; }
                if (tokenFile != null)
                {
                    if (len > 0) add("PASS", "auth.token", "present, " + len + " characters");
                    else add("FAIL", "auth.token", "empty file at " + tokenFile);
                }
            }
            string url = null;
            if (pluginData == null) { add("FAIL", "endpoint.json", "PluginData unknown"); return null; }
            string ep = Path.Combine(pluginData, "endpoint.json");
            if (!File.Exists(ep)) { add("FAIL", "endpoint.json", "not found in " + pluginData + "; the game has not started the host yet"); return null; }
            try
            {
                var j = JsonValue.Parse(File.ReadAllText(ep));
                url = j.GetString("url") ?? ("ws://127.0.0.1:" + j.GetInt("port", ProtocolInfo.DefaultPort) + ProtocolInfo.Path);
                int pid = j.GetInt("pid", 0);
                if (pid <= 0) add("WARN", "endpoint.json", url + " (no pid recorded)");
                else if (IsRunning(pid)) add("PASS", "endpoint.json", url + ", pid " + pid + " is running");
                else add("WARN", "endpoint.json", url + ", pid " + pid + " is not running (stale file; the game is closed)");
            }
            catch (Exception e) { add("FAIL", "endpoint.json", "unreadable: " + e.Message); }
            return url;
        }

        private static bool IsRunning(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ connection

        private static void ChecksConnection(Action<string, string, string> add, ConnectionOptions conn, string fileUrl)
        {
            Endpoint ep = null;
            try { ep = conn.Discover(); }
            catch (SkizzyClientException e) { add("FAIL", "connect", e.Message + (e.Hint != null ? " (" + e.Hint + ")" : "")); }
            string url = ep != null ? ep.Url : fileUrl;
            if (url != null)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) add("FAIL", "port", "bad url '" + url + "'");
                else if (CanReach(uri.Host, uri.Port, 1500)) add("PASS", "port", uri.Host + ":" + uri.Port + " accepts connections");
                else add("FAIL", "port", uri.Host + ":" + uri.Port + " does not accept connections; is KSP running and past the loading screen?");
            }
            if (ep == null) return;
            SkizzyClient c = null;
            var sw = Stopwatch.StartNew();
            try { c = conn.Connect(5); }
            catch (SkizzyClientException e) { add("FAIL", "connect", e.Message + (e.Hint != null ? " (" + e.Hint + ")" : "")); return; }
            catch (SkizzyError e) { add("FAIL", "connect", e.Reason + (e.Hint != null ? " (" + e.Hint + ")" : "")); return; }
            sw.Stop();
            using (c)
            {
                var hello = c.HelloResult ?? JsonValue.Obj();
                add("PASS", "connect", "hello in " + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms (session " + c.SessionId + ", role " + hello.GetString("role") + ")");
                string hostVersion = hello.GetString("hostVersion");
                string protocol = hello.GetString("version");
                if (hostVersion == ProtocolInfo.HostVersion) add("PASS", "host version", hostVersion + " (matches this CLI)");
                else add("WARN", "host version", "host " + (hostVersion ?? "?") + ", this CLI " + ProtocolInfo.HostVersion + "; update both together");
                if (protocol == ProtocolInfo.Version) add("PASS", "protocol version", protocol + " (matches this CLI)");
                else add("FAIL", "protocol version", "host speaks " + (protocol ?? "?") + ", this CLI " + ProtocolInfo.Version);
                HostDoctor(add, c);
                SelfCheck(add, c);
            }
        }

        private static bool CanReach(string host, int port, int timeoutMs)
        {
            try
            {
                using (var t = new TcpClient())
                {
                    var task = t.ConnectAsync(host, port);
                    return task.Wait(timeoutMs) && t.Connected;
                }
            }
            catch (Exception) { return false; }
        }

        private static string Map(string status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "pass": case "green": return "PASS";
                case "warn": case "yellow": return "WARN";
                case "fail": case "red": return "FAIL";
                default: return "INFO";
            }
        }

        private static void HostDoctor(Action<string, string, string> add, SkizzyClient c)
        {
            try
            {
                var res = c.Call("host.doctor", JsonValue.Obj(), 20);
                var arr = res.GetArr("checks");
                if (arr == null || arr.Count == 0) { add("INFO", "host.doctor", "no lines"); return; }
                for (int i = 0; i < arr.Count; i++)
                    add(Map(arr[i].GetString("status")), "host." + arr[i].GetString("check", "?"), arr[i].GetString("detail", ""));
            }
            catch (SkizzyError e) { add("WARN", "host.doctor", "could not run: " + e.Reason + (e.Hint != null ? " (" + e.Hint + ")" : "")); }
            catch (SkizzyClientException e) { add("WARN", "host.doctor", "could not run: " + e.Message); }
        }

        private static void SelfCheck(Action<string, string, string> add, SkizzyClient c)
        {
            try
            {
                var res = c.Call("host.selfcheck", JsonValue.Obj(), 30);
                var arr = res.GetArr("checks");
                int n = arr != null ? arr.Count : 0, warns = 0, fails = 0;
                for (int i = 0; i < n; i++)
                {
                    string s = Map(arr[i].GetString("status"));
                    if (s == "WARN") warns++;
                    else if (s == "FAIL") fails++;
                }
                add(Map(res.GetString("result")), "host.selfcheck", n + " checks, " + warns + " warn, " + fails + " fail");
                for (int i = 0; i < n; i++)
                {
                    string s = Map(arr[i].GetString("status"));
                    if (s == "WARN" || s == "FAIL") add(s, "host.selfcheck/" + arr[i].GetString("check", "?"), arr[i].GetString("detail", ""));
                }
            }
            catch (SkizzyError e) { add("WARN", "host.selfcheck", "could not run: " + e.Reason + (e.Hint != null ? " (" + e.Hint + ")" : "")); }
            catch (SkizzyClientException e) { add("WARN", "host.selfcheck", "could not run: " + e.Message); }
        }

        // ------------------------------------------------------------------ mods and KSP.log

        private static void ChecksMods(Action<string, string, string> add, string ksp)
        {
            string gd = Path.Combine(ksp, "GameData");
            if (Directory.Exists(Path.Combine(gd, "KSPCommunityFixes"))) add("PASS", "KSPCommunityFixes", "installed");
            else add("WARN", "KSPCommunityFixes", "not installed (recommended: fixes KSP memory leaks that long sessions with many vessels hit)");
            if (Directory.Exists(Path.Combine(gd, "BDArmory"))) add("PASS", "BDArmory", "installed");
            else add("INFO", "BDArmory", "not installed (optional; BDArmory fields and events are unavailable)");
        }

        private static void ChecksLog(Action<string, string, string> add, string ksp)
        {
            string log = Path.Combine(ksp, "KSP.log");
            if (!File.Exists(log)) { add("INFO", "KSP.log", "not found (the game has not run yet)"); return; }
            var last = new Queue<string>();
            int count = 0;
            string header = null;
            bool mentions = false;
            Action close = () =>
            {
                if (header == null || !mentions) return;
                count++;
                last.Enqueue(header);
                if (last.Count > 5) last.Dequeue();
            };
            try
            {
                using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var rd = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = rd.ReadLine()) != null)
                    {
                        if (IsEntryStart(line))
                        {
                            close();
                            header = line.StartsWith("[ERR", StringComparison.Ordinal) || line.StartsWith("[EXC", StringComparison.Ordinal) ? line : null;
                            mentions = header != null && line.IndexOf("Skizzy", StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        else if (header != null && !mentions && line.IndexOf("Skizzy", StringComparison.OrdinalIgnoreCase) >= 0) mentions = true;
                    }
                    close();
                }
            }
            catch (Exception e) { add("INFO", "KSP.log", "could not read: " + e.Message); return; }
            if (count == 0) { add("PASS", "KSP.log", "no [ERR/[EXC entries mentioning Skizzy"); return; }
            add("WARN", "KSP.log", count + " [ERR/[EXC entr" + (count == 1 ? "y" : "ies") + " mention Skizzy; last " + last.Count + " below");
            foreach (var l in last) add("INFO", "KSP.log", l.Length > 220 ? l.Substring(0, 220) + "..." : l);
        }

        /// <summary>"[LOG 12:00:00.000] ..." style header: '[', three capitals, space, digit.</summary>
        private static bool IsEntryStart(string line)
        {
            if (line.Length < 6 || line[0] != '[' || line[4] != ' ' || !char.IsDigit(line[5])) return false;
            for (int i = 1; i <= 3; i++) if (line[i] < 'A' || line[i] > 'Z') return false;
            return true;
        }
    }
}
