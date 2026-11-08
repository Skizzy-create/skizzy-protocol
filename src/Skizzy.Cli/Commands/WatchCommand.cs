using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Skizzy.Client;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    /// <summary>
    /// <c>skizzy watch</c>: a stream.subscribe (or events.subscribe) printed as aligned text lines or JSON lines until
    /// Ctrl+C, <c>--count</c> messages or <c>--duration</c> seconds.
    /// </summary>
    public static class WatchCommand
    {
        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "watch",
            Summary = "stream fields live: skizzy watch altitude srfSpeed --vessels 'DDG*' --rate 2",
            Usage = "skizzy watch <field> [<field>...] [--vessels <selector JSON|name|glob|active>] [--rate <Hz>=2] [--onchange] [--deadband <x>]\n" +
                    "             [--count <N>] [--duration <s>] [--json] [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>] [--actor <id>] [--role <role>] [--side <side>]\n" +
                    "       skizzy watch events [<glob>...] [--count <N>] [--duration <s>] [--json]\n" +
                    "  Subscribes a stream and prints one aligned line per vessel for every sample (the first line set is the full snapshot);\n" +
                    "  --onchange prints only the rows whose values changed. --json prints one JSON object per message.\n" +
                    "  `watch events` prints host events (vessel.*, sim.*, rec.*, ...) one compact line each, optionally filtered by name globs.\n" +
                    "  Stops on Ctrl+C, after --count messages (events for `watch events`) or --duration seconds, and unsubscribes cleanly.",
            Flags = new[] { "onchange" },
            Run = Run,
        };

        private static int Run(CliArgs a)
        {
            if (a.Positionals.Count == 0) throw new UsageException("missing field name (e.g. altitude) or 'events'");
            bool events = string.Equals(a.Positionals[0], "events", StringComparison.OrdinalIgnoreCase);
            double rate = a.Num("rate", 2);
            if (rate <= 0) throw new UsageException("--rate must be greater than 0");
            double deadband = a.Num("deadband", 0);
            if (deadband < 0) throw new UsageException("--deadband must not be negative");
            int count = a.Int("count", 0);
            double duration = a.Num("duration", 0);
            if (count < 0) throw new UsageException("--count must not be negative");
            if (duration < 0) throw new UsageException("--duration must not be negative");
            bool json = a.Flag("json");
            JsonValue select = a.Opt("vessels") != null ? Selectors.Parse(a.Opt("vessels")) : null;
            var fields = events ? null : Selectors.SplitList(a.Positionals, 0);
            if (!events && fields.Count == 0) throw new UsageException("missing field name (e.g. altitude)");

            var conn = ConnectionOptions.From(a, "cli", "script");
            using (var cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler onCancel = (s, e) => { e.Cancel = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
                bool hooked = false;
                try { Console.CancelKeyPress += onCancel; hooked = true; } catch (Exception) { }
                try
                {
                    using (var c = conn.Connect())
                    {
                        return events
                            ? WatchEvents(c, a, count, duration, json, cts.Token)
                            : WatchStream(c, fields, select, rate, a.Flag("onchange"), a.Has("deadband") ? deadband : -1, count, duration, json, cts.Token);
                    }
                }
                finally
                {
                    if (hooked) { try { Console.CancelKeyPress -= onCancel; } catch (Exception) { } }
                }
            }
        }

        // ------------------------------------------------------------------ streams

        private static int WatchStream(SkizzyClient c, List<string> fields, JsonValue select, double rate, bool onChange, double deadband,
            int count, double duration, bool json, CancellationToken ct)
        {
            var queue = new BlockingCollection<JsonValue>();
            c.Notification += (m, p) => { if (m == "stream.data") queue.Add(p); };
            var query = JsonValue.Obj();
            if (select != null) query.Set("select", select);
            query.Set("fields", JsonValue.ArrOf(fields.ToArray()));
            var prms = JsonValue.Obj().Set("source", JsonValue.Obj().Set("query", query))
                .Set("mode", onChange ? "onchange" : "sampled").Set("rateHz", rate);
            if (deadband >= 0) prms.Set("deadband", deadband);
            var snap = c.Call("stream.subscribe", prms, 30);
            string sub = snap.GetString("sub");
            try
            {
                var warnings = snap.GetArr("warnings");
                if (warnings != null)
                    for (int i = 0; i < warnings.Count; i++) Console.Error.WriteLine("warning: " + warnings[i].AsString(warnings[i].ToString()));
                var cols = new List<string>();
                var colArr = snap.GetArr("columns");
                if (colArr != null) for (int i = 0; i < colArr.Count; i++) cols.Add(colArr[i].AsString(""));
                var printer = new RowPrinter(cols, json);
                int messages = 0;
                printer.Print(snap.GetArr("rows"), double.NaN, "full", snap.GetLong("seq"), snap, true);
                messages++;
                var deadline = duration > 0 ? DateTime.UtcNow.AddSeconds(duration) : DateTime.MaxValue;
                while (!(count > 0 && messages >= count))
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) break;
                    if (!c.IsConnected)
                    {
                        Console.Error.WriteLine("UNAVAILABLE: disconnected - " + (c.LastError ?? "connection lost"));
                        return Program.ExitNotConnected;
                    }
                    if (!queue.TryTake(out var p, 200)) continue;
                    if (p.GetString("sub") != null && p.GetString("sub") != sub) continue;
                    printer.Print(p.GetArr("rows"), p.GetDouble("ut", double.NaN), p.GetString("kind", "full"), p.GetLong("seq"), p, false);
                    messages++;
                }
                return Program.ExitOk;
            }
            finally
            {
                if (c.IsConnected)
                {
                    try { c.Call("stream.unsubscribe", JsonValue.Obj().Set("sub", sub), 5); } catch (Exception) { }
                }
            }
        }

        /// <summary>Aligned rows: ut, vessel name, then the fields; or one JSON object per message.</summary>
        private sealed class RowPrinter
        {
            private readonly List<string> _cols;
            private readonly bool _json;
            private int[] _widths;
            private bool _header;
            private int _identity;

            public RowPrinter(List<string> cols, bool json)
            {
                _cols = cols;
                _json = json;
                _identity = 2;
                for (int i = 0; i < cols.Count; i++) if (cols[i] == "vesselName") { _identity = i + 1; break; }
                // perPart / perModule add part and module identity columns after vesselName; the fields follow.
                while (_identity < cols.Count && (cols[_identity] == "part" || cols[_identity] == "module")) _identity++;
            }

            public void Print(JsonValue rows, double ut, string kind, long seq, JsonValue msg, bool snapshot)
            {
                if (rows == null) return;
                if (_json)
                {
                    var o = JsonValue.Obj().Set("seq", seq);
                    if (!double.IsNaN(ut)) o.Set("ut", ut);
                    else if (snapshot && msg != null && msg.GetObj("meta") != null) o.Set("ut", msg.GetObj("meta").GetDouble("ut", 0));
                    o.Set("kind", kind);
                    var arr = JsonValue.Arr();
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        var ro = JsonValue.Obj();
                        for (int i = 0; i < row.Count; i++) ro.Set(i < _cols.Count ? _cols[i] : "c" + i, row[i]);
                        arr.Add(ro);
                    }
                    o.Set("rows", arr);
                    Console.Out.WriteLine(o.ToString());
                    return;
                }
                if (double.IsNaN(ut) && snapshot && msg != null && msg.GetObj("meta") != null) ut = msg.GetObj("meta").GetDouble("ut", double.NaN);
                var table = new List<string[]>();
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    var cells = new List<string> { double.IsNaN(ut) ? "-" : Math.Round(ut, 1).ToString("0.0", CultureInfo.InvariantCulture) };
                    cells.Add(row.Count > 1 ? row[1].AsString(row[1].ToString()) : (row.Count > 0 ? row[0].AsString("?") : "?"));
                    for (int i = _identity; i < row.Count; i++) cells.Add(Cell(row[i]));
                    table.Add(cells.ToArray());
                }
                if (!_header)
                {
                    var head = new List<string> { "ut", "vessel" };
                    for (int i = _identity; i < _cols.Count; i++) head.Add(_cols[i]);
                    _widths = new int[head.Count];
                    for (int i = 0; i < head.Count; i++) _widths[i] = Math.Max(head[i].Length, i == 0 ? 8 : 6);
                    foreach (var t in table) Widen(t);
                    Console.Out.WriteLine(Line(head.ToArray(), true));
                    _header = true;
                }
                foreach (var t in table) Console.Out.WriteLine(Line(t, false));
            }

            private void Widen(string[] cells)
            {
                for (int i = 0; i < cells.Length && i < _widths.Length; i++)
                    if (cells[i].Length > _widths[i]) _widths[i] = cells[i].Length;
            }

            private string Line(string[] cells, bool header)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < cells.Length; i++)
                {
                    int w = i < _widths.Length ? _widths[i] : 0;
                    if (i > 0) sb.Append("  ");
                    bool left = i == 1 || header || !LooksNumeric(cells[i]);
                    sb.Append(left ? cells[i].PadRight(w) : cells[i].PadLeft(w));
                }
                return sb.ToString().TrimEnd();
            }

            private static bool LooksNumeric(string s)
            {
                return s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }
        }

        internal static string Cell(JsonValue v)
        {
            if (v == null || v.IsNull) return "-";
            if (v.IsString) return v.AsString();
            if (v.IsBool) return v.AsBool() ? "true" : "false";
            if (v.IsNumber)
            {
                double d = v.AsDouble();
                if (double.IsNaN(d) || double.IsInfinity(d)) return "-";
                double abs = Math.Abs(d);
                if (v.IsInteger && abs < 1e15) return d.ToString("0", CultureInfo.InvariantCulture);
                if (abs >= 1e9 || (abs > 0 && abs < 1e-4)) return d.ToString("0.###E+0", CultureInfo.InvariantCulture);
                return Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);
            }
            return v.ToString();
        }

        // ------------------------------------------------------------------ events

        private static int WatchEvents(SkizzyClient c, CliArgs a, int count, double duration, bool json, CancellationToken ct)
        {
            var queue = new BlockingCollection<JsonValue>();
            c.Notification += (m, p) => { if (m == "events.data") queue.Add(p); };
            var globs = Selectors.SplitList(a.Positionals, 1);
            var prms = JsonValue.Obj();
            if (globs.Count > 0) prms.Set("names", JsonValue.ArrOf(globs.ToArray()));
            c.Call("events.subscribe", prms, 30);
            try
            {
                int seen = 0;
                var deadline = duration > 0 ? DateTime.UtcNow.AddSeconds(duration) : DateTime.MaxValue;
                while (!(count > 0 && seen >= count))
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) break;
                    if (!c.IsConnected)
                    {
                        Console.Error.WriteLine("UNAVAILABLE: disconnected - " + (c.LastError ?? "connection lost"));
                        return Program.ExitNotConnected;
                    }
                    if (!queue.TryTake(out var p, 200)) continue;
                    var list = p.GetArr("events");
                    if (list == null) continue;
                    for (int i = 0; i < list.Count && !(count > 0 && seen >= count); i++)
                    {
                        Console.Out.WriteLine(json ? list[i].ToString() : EventLine(list[i]));
                        seen++;
                    }
                }
                return Program.ExitOk;
            }
            finally
            {
                if (c.IsConnected)
                {
                    try { c.Call("events.unsubscribe", JsonValue.Obj(), 5); } catch (Exception) { }
                }
            }
        }

        /// <summary>"#123 ut=1203.4 vessel.destroyed DDG-2 destroyed" (the host's compact event line).</summary>
        internal static string EventLine(JsonValue e)
        {
            return "#" + e.GetLong("cursor") + " ut=" + Math.Round(e.GetDouble("ut"), 1).ToString("0.0", CultureInfo.InvariantCulture) + " " +
                   e.GetString("name", "?") + " " + e.GetString("summary", "");
        }
    }

    /// <summary>Selector and list helpers shared by watch and log.</summary>
    internal static class Selectors
    {
        /// <summary>
        /// A <c>--vessels</c> value to a <c>select</c> object: JSON (a full select with vessels/parts/modules, or just the
        /// vessels filter), "active", a glob (nameLike) or an exact vessel name / handle.
        /// </summary>
        public static JsonValue Parse(string text)
        {
            string t = text.Trim();
            if (t.Length == 0) throw new UsageException("--vessels is empty");
            if (t.StartsWith("{", StringComparison.Ordinal))
            {
                JsonValue v;
                try { v = JsonValue.Parse(t); }
                catch (Exception e) { throw new UsageException("--vessels is not valid JSON: " + e.Message); }
                if (v.Kind != JsonKind.Object) throw new UsageException("--vessels JSON must be an object");
                if (v.Has("vessels") || v.Has("parts") || v.Has("modules")) return v;
                return JsonValue.Obj().Set("vessels", v);
            }
            if (string.Equals(t, "active", StringComparison.OrdinalIgnoreCase))
                return JsonValue.Obj().Set("vessels", JsonValue.Obj().Set("active", true));
            if (t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0)
                return JsonValue.Obj().Set("vessels", JsonValue.Obj().Set("nameLike", t));
            return JsonValue.Obj().Set("vessels", JsonValue.Obj().Set("handles", JsonValue.ArrOf(t)));
        }

        /// <summary>Items from <paramref name="start"/> on, with comma-separated values split.</summary>
        public static List<string> SplitList(IList<string> items, int start)
        {
            var list = new List<string>();
            for (int i = start; i < items.Count; i++)
                foreach (var part in items[i].Split(','))
                    if (part.Trim().Length > 0) list.Add(part.Trim());
            return list;
        }
    }
}
