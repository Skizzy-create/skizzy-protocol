using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Skizzy.Protocol;
using Skizzy.Protocol.Config;

namespace Skizzy.Host.Core
{
    /// <summary>Host log sink. KSP: warnings and above to KSP.log, everything to PluginData/host.log (02 §7).</summary>
    public interface IHostLog
    {
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg);
    }

    public sealed class NullLog : IHostLog
    {
        public static readonly NullLog Instance = new NullLog();
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }

    /// <summary>Per-session and host-wide limits (03 §1). Advertised in the hello result.</summary>
    public sealed class HostLimits
    {
        public int MaxMessageBytes = 4 * 1024 * 1024;
        public int MaxJsonDepth = 64;
        public int MaxConnections = 32;
        public int MaxInflight = 64;
        public int MaxSubscriptions = 128;
        public long OutboundQueueBytes = 16L * 1024 * 1024;
        public double IdleTimeoutS = 600;
        public double PingIntervalS = 30;
        public int CallsPerMinute = 600;
        public double HelloTimeoutS = 10;
        public double ResumeWindowS = 600;
    }

    /// <summary>settings.cfg (SKIZZY_SETTINGS). Unknown keys are ignored; bad values fall back to defaults.</summary>
    public sealed class HostSettings
    {
        public bool Listen = true;              // false = baseline run for the idle probe (plugin loaded, no socket)
        public int PortFirst = ProtocolInfo.DefaultPort;
        public int PortLast = ProtocolInfo.PortRangeEnd;
        public string[] AllowedOrigins = new string[0];
        public double BudgetMs = 2.0;
        public double BudgetMinMs = 0.5;
        public double BudgetMaxMs = 8.0;
        public double ConfirmTimeoutS = 60;
        public string KillSwitchKey = "ctrl+alt+k";
        public bool IdleProbe = false;          // 1 Hz background heap sampler (acceptance 1 measurement)
        public bool FirstRunDone = false;
        public bool JpegOnWorkerThread = true;  // set false automatically if the spike fails
        public readonly HostLimits Limits = new HostLimits();

        public static HostSettings FromNode(CfgNode n)
        {
            var s = new HostSettings();
            if (n == null) return s;
            s.Listen = n.GetBool("listen", s.Listen);
            s.PortFirst = Clamp(n.GetInt("portFirst", s.PortFirst), 1024, 65535);
            s.PortLast = Clamp(n.GetInt("portLast", s.PortLast), s.PortFirst, 65535);
            var origins = n.GetValues("allowOrigin");
            if (origins.Count > 0) s.AllowedOrigins = origins.ToArray();
            s.BudgetMs = ClampD(n.GetDouble("budgetMs", s.BudgetMs), 0.5, 8);
            s.ConfirmTimeoutS = ClampD(n.GetDouble("confirmTimeoutS", s.ConfirmTimeoutS), 5, 600);
            s.KillSwitchKey = n.GetValue("killSwitchKey") ?? s.KillSwitchKey;
            s.IdleProbe = n.GetBool("idleProbe", s.IdleProbe);
            s.FirstRunDone = n.GetBool("firstRunDone", s.FirstRunDone);
            s.JpegOnWorkerThread = n.GetBool("jpegOnWorkerThread", s.JpegOnWorkerThread);
            s.Limits.MaxConnections = Clamp(n.GetInt("maxConnections", s.Limits.MaxConnections), 1, 64);
            return s;
        }

        public CfgNode ToNode()
        {
            var n = new CfgNode("SKIZZY_SETTINGS");
            n.AddValue("listen", Listen ? "true" : "false");
            n.AddValue("portFirst", PortFirst.ToString(CultureInfo.InvariantCulture));
            n.AddValue("portLast", PortLast.ToString(CultureInfo.InvariantCulture));
            foreach (var o in AllowedOrigins) n.AddValue("allowOrigin", o);
            n.AddValue("budgetMs", BudgetMs.ToString("0.###", CultureInfo.InvariantCulture));
            n.AddValue("confirmTimeoutS", ConfirmTimeoutS.ToString("0.###", CultureInfo.InvariantCulture));
            n.AddValue("killSwitchKey", KillSwitchKey);
            n.AddValue("idleProbe", IdleProbe ? "true" : "false");
            n.AddValue("firstRunDone", FirstRunDone ? "true" : "false");
            n.AddValue("jpegOnWorkerThread", JpegOnWorkerThread ? "true" : "false");
            n.AddValue("maxConnections", Limits.MaxConnections.ToString(CultureInfo.InvariantCulture));
            return n;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        private static double ClampD(double v, double lo, double hi) { return double.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v; }
    }

    /// <summary>
    /// File locations (02 §6). PluginData holds host-written state (token, endpoint, permissions, audit);
    /// the user data root <c>&lt;KSP&gt;/Skizzy</c> holds logs and screenshots.
    /// </summary>
    public sealed class HostPaths
    {
        public string KspRoot;
        public string PluginData;
        public string UserRoot;

        public HostPaths(string kspRoot, string pluginData)
        {
            KspRoot = Path.GetFullPath(kspRoot);
            PluginData = Path.GetFullPath(pluginData);
            UserRoot = Path.Combine(KspRoot, "Skizzy");
        }

        public string TokenFile => Path.Combine(PluginData, "auth.token");
        public string EndpointFile => Path.Combine(PluginData, "endpoint.json");
        public string PermissionsFile => Path.Combine(PluginData, "permissions.cfg");
        public string SettingsFile => Path.Combine(PluginData, "settings.cfg");
        public string AuditDir => Path.Combine(PluginData, "audit");
        public string HostLog => Path.Combine(PluginData, "host.log");
        public string IdleProbeFile => Path.Combine(PluginData, "idle-probe.csv");
        public string KspLog => Path.Combine(KspRoot, "KSP.log");

        public string LogsDir(DateTime local) { return Path.Combine(Path.Combine(UserRoot, "logs"), local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); }
        public string ScreenshotsDir(DateTime local) { return Path.Combine(Path.Combine(UserRoot, "screenshots"), local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); }

        /// <summary>Path relative to the KSP root with forward slashes, for results.</summary>
        public string Relative(string full)
        {
            string f = Path.GetFullPath(full);
            string root = KspRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) f = f.Substring(root.Length);
            return f.Replace('\\', '/');
        }

        /// <summary>
        /// Resolves a client path under the sandbox roots (PLAN §6.8): canonicalised and prefix-checked. Returns null
        /// if it escapes them.
        /// </summary>
        public string ResolveReadable(string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.IndexOf('\0') >= 0) return null;
            string full;
            try { full = Path.GetFullPath(Path.Combine(KspRoot, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception) { return null; }
            foreach (var root in new[] { Path.Combine(UserRoot, "logs"), Path.Combine(UserRoot, "screenshots") })
            {
                string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return full;
            }
            return null;
        }
    }

    /// <summary>Monotonic time helpers (Stopwatch; net48 has no Environment.TickCount64).</summary>
    public static class Clock
    {
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        public static long Ticks => Stopwatch.GetTimestamp();
        public static long NowMs => (long)(Stopwatch.GetTimestamp() * MsPerTick);
        public static double ToMs(long ticks) { return ticks * MsPerTick; }
        public static double SinceMs(long startTicks) { return (Stopwatch.GetTimestamp() - startTicks) * MsPerTick; }
        public static string WallUtcIso() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
    }

    /// <summary>Fixed-size ring of doubles for p50/p99 over a window (timing counters, 02 §7).</summary>
    public sealed class TimingWindow
    {
        private readonly double[] _v;
        private readonly double[] _sorted;
        private int _n, _i;
        public long Count;
        public double Max;
        public double Last;
        public double Total;

        /// <summary>Mean over everything added since the last reset (amortised cost per call).</summary>
        public double Mean { get { lock (_v) return Count == 0 ? 0 : Total / Count; } }

        public TimingWindow(int size = 512) { _v = new double[size]; _sorted = new double[size]; }

        // skizzy:hot TimingWindow.Add
        public void Add(double ms)
        {
            lock (_v)
            {
                _v[_i] = ms;
                _i = (_i + 1) % _v.Length;
                if (_n < _v.Length) _n++;
                Count++;
                Total += ms;
                Last = ms;
                if (ms > Max) Max = ms;
            }
        }

        public void Percentiles(out double p50, out double p99, out double max)
        {
            lock (_v)
            {
                if (_n == 0) { p50 = p99 = max = 0; return; }
                Array.Copy(_v, _sorted, _n);
                Array.Sort(_sorted, 0, _n);
                p50 = _sorted[(_n - 1) / 2];
                p99 = _sorted[Math.Min(_n - 1, (int)Math.Ceiling(_n * 0.99) - 1)];
                max = _sorted[_n - 1];
            }
        }

        public void Reset() { lock (_v) { _n = 0; _i = 0; Count = 0; Max = 0; Total = 0; } }
    }

    public static class Inv
    {
        public static string S(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        public static string S(long v) { return v.ToString(CultureInfo.InvariantCulture); }
    }
}
