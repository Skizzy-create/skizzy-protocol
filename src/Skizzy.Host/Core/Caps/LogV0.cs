using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Caps
{
    /// <summary>One running or stopped log v0 file (12 §1.1).</summary>
    public sealed class LogFile
    {
        public int Id;
        public string LogId;
        public string Name;
        public string FullPath;
        public string RelPath;
        public bool Jsonl;
        public double RateHz;
        public bool OnChange;
        public double Deadband;
        public long MaxBytes;
        public QueryPlan Plan;
        public string[] Columns;
        public double NextUt = double.NegativeInfinity;
        public long Rows;
        public long Bytes;
        public long Samples;
        public volatile string State = "running";  // running | stopped | failed
        public string StopReason;
        public string StartedUtc = Clock.WallUtcIso();
        public string StoppedUtc;
        public string OwnerActor;
        internal FileStream Stream;
        internal bool FrozenPlan;
        internal readonly ByteBuffer Scratch = new ByteBuffer(64);
        internal readonly object WriteLock = new object();

        // Per-log buffer recycling: the writer thread hands this log's chunk buffers back here, so buffer sizes do not
        // circulate between unrelated consumers of the shared pool (which would make Ensure grow them mid-sample).
        private const int MaxFree = 8;
        private readonly Stack<ByteBuffer> _free = new Stack<ByteBuffer>(MaxFree);
        internal int LastChunkBytes;

        // skizzy:hot LogFile.RentChunk
        internal ByteBuffer RentChunk(int estimate)
        {
            int want = estimate;
            int last = LastChunkBytes;
            if (last > 0 && last + (last >> 2) > want) want = last + (last >> 2);
            ByteBuffer b = null;
            lock (_free) { if (_free.Count > 0) b = _free.Pop(); }
            if (b == null) return new ByteBuffer(want);
            b.Length = 0;
            if (b.Data.Length < want) b.Ensure(want);
            return b;
        }

        internal void ReturnChunk(ByteBuffer b)
        {
            b.Length = 0;
            lock (_free)
            {
                if (_free.Count < MaxFree) { _free.Push(b); return; }
            }
            ByteBufferPool.Return(b);
        }

        public JsonValue ToJson()
        {
            return JsonValue.Obj().Set("log", LogId).Set("name", Name).Set("path", RelPath).Set("format", Jsonl ? "jsonl" : "csv")
                .Set("rateHz", RateHz).Set("onChange", OnChange).Set("rows", Rows).Set("bytes", Bytes).Set("state", State)
                .Set("startedUtc", StartedUtc).Set("stoppedUtc", StoppedUtc).Set("stopReason", StopReason).Set("owner", OwnerActor)
                .Set("columns", JsonValue.ArrOf(Columns)).Set("targets", Plan.Rows.Count);
        }
    }

    /// <summary>
    /// Log v0: rec.log.start/stop/list/tail and rec.get_file. Sampling happens in FixedUpdate at game-time rates and
    /// writes CSV/JSONL rows into a pooled buffer (allocation-free); a background writer thread does the file I/O.
    /// </summary>
    public sealed class LogManager
    {
        private struct Chunk
        {
            public LogFile Log;
            public ByteBuffer Buf;
            public bool Close;
            public bool Own;   // Buf came from Log.RentChunk and goes back there
        }

        private readonly HostCore _host;
        private readonly List<LogFile> _all = new List<LogFile>();
        private volatile LogFile[] _active = new LogFile[0];
        private readonly object _q = new object();
        private readonly Queue<Chunk> _chunks = new Queue<Chunk>(256);
        private Thread _writer;
        private int _next;

        public LogManager(HostCore host) { _host = host; }

        public int ActiveCount => _active.Length;

        public void Register(HostCore h)
        {
            h.Register("rec.log.start", Start);
            h.Register("rec.log.stop", Stop);
            h.Register("rec.log.list", List);
            h.Register("rec.log.tail", Tail);
            h.Register("rec.get_file", GetFile);
        }

        // ------------------------------------------------------------------ capabilities

        private JsonValue Start(CallContext ctx)
        {
            string name = ctx.Str("name");
            lock (_all)
                foreach (var l in _all)
                    if (l.State == "running" && l.Name == name)
                        throw new SkizzyError(ErrorKind.Conflict, "a log named '" + name + "' is already running", "already_exists",
                            "Stop it with rec.log.stop or pick another name.", JsonValue.Obj().Set("detail", l.LogId));
            var spec = QuerySpec.FromParams(ctx.Params);
            var plan = new QueryPlan(_host, spec);
            plan.Build();
            bool jsonl = ctx.Str("format", "csv") == "jsonl";
            string dir = _host.Paths.LogsDir(DateTime.Now);
            Directory.CreateDirectory(dir);
            string ext = jsonl ? ".jsonl" : ".csv";
            string path = Path.Combine(dir, name + ext);
            for (int i = 1; File.Exists(path); i++) path = Path.Combine(dir, name + "-" + i + ext);
            var cols = new List<string> { "ut", "frame", "wallUtc" };
            cols.AddRange(plan.Columns);
            var log = new LogFile
            {
                Id = Interlocked.Increment(ref _next),
                Name = name,
                FullPath = path,
                RelPath = _host.Paths.Relative(path),
                Jsonl = jsonl,
                RateHz = ctx.Num("rateHz", 5),
                OnChange = ctx.Has("onChange"),
                Deadband = ctx.Params.GetObj("onChange")?.GetDouble("deadband", 0) ?? 0,
                MaxBytes = (long)(ctx.Num("maxMB", 200) * 1024 * 1024),
                Plan = plan,
                Columns = cols.ToArray(),
                OwnerActor = ctx.Session.ActorId,
            };
            log.LogId = "log:" + log.Id;
            log.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            if (!jsonl)
            {
                // UTF-8 BOM so Excel opens non-ASCII vessel names correctly; header row.
                var head = ByteBufferPool.Rent(256);
                head.WriteByte(0xEF); head.WriteByte(0xBB); head.WriteByte(0xBF);
                for (int i = 0; i < log.Columns.Length; i++)
                {
                    if (i > 0) head.WriteByte((byte)',');
                    CsvString(head, log.Columns[i]);
                }
                head.WriteByte((byte)'\r'); head.WriteByte((byte)'\n');
                Enqueue(log, head, false);
            }
            log.NextUt = _host.Meta.Ut;
            lock (_all) { _all.Add(log); PruneLocked(); }
            SetActive();
            _host.Events.Emit("rec.log_started", "system", "log " + name + " started: " + plan.Rows.Count + " targets x " + spec.Fields.Length + " fields at " + Inv.S(log.RateHz) + " Hz",
                JsonValue.Obj().Set("log", log.LogId).Set("path", log.RelPath));
            var res = JsonValue.Obj().Set("log", log.LogId).Set("path", log.RelPath).Set("columns", JsonValue.ArrOf(log.Columns))
                .Set("rateHz", log.RateHz).Set("format", jsonl ? "jsonl" : "csv").Set("targets", plan.Rows.Count);
            if (plan.Rows.Count == 0) res.Set("warning", "the selector matches nothing right now; rows appear when matching vessels load");
            if (plan.UnknownModuleFields.Count > 0) res.Set("unknownFields", JsonValue.ArrOf(new List<string>(plan.UnknownModuleFields).ToArray()));
            return res;
        }

        /// <summary>
        /// Perf gate helper (02 §8.3 log v0): logs altitude, srfSpeed and throttle of every loaded vessel, repeating
        /// rows until <paramref name="targets"/> rows exist, to a CSV under Skizzy/logs.
        /// </summary>
        public LogFile StartSynthetic(int targets, double rateHz)
        {
            var spec = QuerySpec.FromParams(JsonValue.Obj()
                .Set("fields", JsonValue.ArrOf("altitude", "srfSpeed", "ctrlState.mainThrottle")));
            var plan = new QueryPlan(_host, spec);
            plan.Build();
            if (plan.Rows.Count == 0) throw SkizzyError.Precondition("event_inactive", "no loaded vessels to sample", "Run the log gate in flight with at least one vessel.");
            plan.RepeatRowsTo(targets);
            string dir = _host.Paths.LogsDir(DateTime.Now);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "selfcheck-log-gate-" + DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".csv");
            var cols = new List<string> { "ut", "frame", "wallUtc" };
            cols.AddRange(plan.Columns);
            var log = new LogFile
            {
                Id = Interlocked.Increment(ref _next),
                Name = Path.GetFileNameWithoutExtension(path),
                FullPath = path,
                RelPath = _host.Paths.Relative(path),
                RateHz = rateHz,
                MaxBytes = 200L * 1024 * 1024,
                Plan = plan,
                Columns = cols.ToArray(),
                OwnerActor = "selfcheck",
            };
            log.LogId = "log:" + log.Id;
            log.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 65536);
            log.NextUt = _host.Meta.Ut;
            log.FrozenPlan = true;
            lock (_all) { _all.Add(log); PruneLocked(); }
            SetActive();
            return log;
        }

        private LogFile Find(string idOrName)
        {
            lock (_all)
            {
                for (int i = _all.Count - 1; i >= 0; i--)
                    if (_all[i].LogId == idOrName || _all[i].Name == idOrName) return _all[i];
            }
            throw SkizzyError.NotFound(idOrName, "unknown", "Unknown log; list logs with rec.log.list.");
        }

        private JsonValue Stop(CallContext ctx)
        {
            var log = Find(ctx.Str("log"));
            StopLog(log, "rec.log.stop by " + ctx.Session.ActorId);
            return JsonValue.Obj().Set("log", log.LogId).Set("path", log.RelPath).Set("rows", log.Rows).Set("bytes", log.Bytes).Set("state", log.State);
        }

        private JsonValue List(CallContext ctx)
        {
            var arr = JsonValue.Arr();
            lock (_all) foreach (var l in _all) arr.Add(l.ToJson());
            return JsonValue.Obj().Set("logs", arr).Set("total", arr.Count);
        }

        private JsonValue Tail(CallContext ctx)
        {
            var log = Find(ctx.Str("log"));
            int n = ctx.Int("n", 20);
            Flush(log);
            var lines = TailLines(log.FullPath, n + 1);
            if (!log.Jsonl && lines.Count > 0 && lines[0].StartsWith("\uFEFF", StringComparison.Ordinal)) lines[0] = lines[0].Substring(1);
            if (!log.Jsonl && lines.Count > n) lines.RemoveRange(0, lines.Count - n);
            if (!log.Jsonl && lines.Count > 0 && lines[0].StartsWith("ut,", StringComparison.Ordinal)) lines.RemoveAt(0);
            if (ctx.Str("format", "compact") == "compact")
            {
                var sb = new StringBuilder();
                sb.Append("#log ").Append(log.LogId).Append(' ').Append(log.Name).Append(" rows=").Append(log.Rows).Append(" state=").Append(log.State).Append('\n');
                if (!log.Jsonl) sb.Append(string.Join(",", log.Columns)).Append('\n');
                foreach (var l in lines) sb.Append(l).Append('\n');
                return JsonValue.Obj().Set("text", sb.ToString()).Set("rows", lines.Count).Set("log", log.LogId);
            }
            return JsonValue.Obj().Set("log", log.LogId).Set("columns", JsonValue.ArrOf(log.Columns)).Set("lines", JsonValue.ArrOf(lines.ToArray()))
                .Set("format", log.Jsonl ? "jsonl" : "csv");
        }

        private JsonValue GetFile(CallContext ctx)
        {
            string rel = ctx.Str("path");
            string full = _host.Paths.ResolveReadable(rel);
            if (full == null)
                throw new SkizzyError(ErrorKind.PermissionDenied, "path outside Skizzy/logs and Skizzy/screenshots", "policy",
                    "rec.get_file only reads files under Skizzy/logs and Skizzy/screenshots.", JsonValue.Obj().Set("capability", "rec.get_file"));
            if (!File.Exists(full)) throw SkizzyError.NotFound(rel, "unknown", "No such file; rec.log.list shows log paths.");
            foreach (var l in _active) if (string.Equals(l.FullPath, full, StringComparison.OrdinalIgnoreCase)) Flush(l);
            long offset = ctx.Params.GetLong("offset", 0);
            int length = ctx.Int("length", 1048576);
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long size = fs.Length;
                if (offset > size) offset = size;
                fs.Position = offset;
                int toRead = (int)Math.Min(length, size - offset);
                var data = new byte[toRead];
                int got = 0;
                while (got < toRead)
                {
                    int r = fs.Read(data, got, toRead - got);
                    if (r <= 0) break;
                    got += r;
                }
                return JsonValue.Obj().Set("path", _host.Paths.Relative(full)).Set("offset", offset).Set("length", got)
                    .Set("size", size).Set("eof", offset + got >= size).Set("data", JsonValue.Bytes(data, 0, got)).Set("encoding", "base64");
            }
        }

        private static List<string> TailLines(string path, int n)
        {
            var result = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long size = fs.Length;
                int chunk = (int)Math.Min(size, 256 * 1024);
                fs.Position = size - chunk;
                var buf = new byte[chunk];
                int got = 0;
                while (got < chunk)
                {
                    int r = fs.Read(buf, got, chunk - got);
                    if (r <= 0) break;
                    got += r;
                }
                string text = Encoding.UTF8.GetString(buf, 0, got);
                var lines = text.Split('\n');
                int start = size > chunk ? 1 : 0; // first line may be partial
                for (int i = start; i < lines.Length; i++)
                {
                    string l = lines[i].TrimEnd('\r');
                    if (l.Length > 0) result.Add(l);
                }
            }
            if (result.Count > n) result.RemoveRange(0, result.Count - n);
            return result;
        }

        // ------------------------------------------------------------------ lifecycle

        public void StopLog(LogFile log, string reason)
        {
            if (log.State != "running") return;
            log.State = "stopped";
            log.StopReason = reason;
            log.StoppedUtc = Clock.WallUtcIso();
            SetActive();
            Enqueue(log, null, true);
            Flush(log);
            _host.Events.Emit("rec.log_stopped", "system", "log " + log.Name + " stopped (" + reason + "), " + log.Rows + " rows",
                JsonValue.Obj().Set("log", log.LogId).Set("path", log.RelPath).Set("rows", log.Rows));
        }

        public void StopAll(string reason)
        {
            LogFile[] act = _active;
            foreach (var l in act) StopLog(l, reason);
        }

        private void SetActive()
        {
            var list = new List<LogFile>();
            lock (_all) foreach (var l in _all) if (l.State == "running") list.Add(l);
            _active = list.ToArray();
        }

        private void PruneLocked()
        {
            int stopped = 0;
            for (int i = _all.Count - 1; i >= 0; i--)
                if (_all[i].State != "running" && ++stopped > 20) _all.RemoveAt(i);
        }

        /// <summary>Snapshot for the in-game recorder window.</summary>
        public void Snapshot(List<LogFile> into) { lock (_all) into.AddRange(_all); }

        // ------------------------------------------------------------------ sampling (FixedUpdate)

        // skizzy:hot LogManager.Sample
        public void Sample()
        {
            var logs = _active;
            if (logs.Length == 0) return;
            long t0 = Clock.Ticks;
            var meta = _host.Meta;
            for (int i = 0; i < logs.Length; i++)
            {
                var log = logs[i];
                if (meta.Ut < log.NextUt) continue;
                double period = 1.0 / log.RateHz;
                log.NextUt = log.NextUt + period < meta.Ut ? meta.Ut + period : log.NextUt + period;
                if (!log.FrozenPlan) log.Plan.EnsureCurrent();
                SampleOne(log, ref meta);
                if (log.Bytes >= log.MaxBytes) StopLog(log, "maxMB reached");
            }
            _host.Stats.LogSampleMs.Add(Clock.SinceMs(t0));
        }

        // skizzy:hot LogManager.SampleOne
        private void SampleOne(LogFile log, ref MetaState meta)
        {
            var plan = log.Plan;
            var rows = plan.Rows;
            if (rows.Count == 0) return;
            var b = log.RentChunk(rows.Count * (32 + 16 * plan.Spec.Fields.Length) + 64);
            JsonWriter w = log.Jsonl ? JsonWriterPool.Rent(b) : null;
            var now = DateTime.UtcNow;
            int written = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                if (!plan.ReadRow(row)) continue;
                if (log.OnChange)
                {
                    bool changed = !row.HasLast;
                    for (int f = 0; f < row.Values.Length && !changed; f++)
                        if (!row.Values[f].SameAs(ref row.Last[f], log.Deadband)) changed = true;
                    if (!changed) continue;
                    for (int f = 0; f < row.Values.Length; f++) row.Last[f] = row.Values[f];
                    row.HasLast = true;
                }
                if (log.Jsonl) WriteJsonRow(w, b, log, row, ref meta, now);
                else WriteCsvRow(b, plan, row, ref meta, now);
                written++;
            }
            if (w != null) JsonWriterPool.Return(w);
            if (written == 0) { log.ReturnChunk(b); return; }
            log.Rows += written;
            log.Samples++;
            log.Bytes += b.Length;
            if (b.Length > log.LastChunkBytes) log.LastChunkBytes = b.Length;
            Enqueue(log, b, false, true);
        }

        // skizzy:hot LogManager.WriteCsvRow
        private static void WriteCsvRow(ByteBuffer b, QueryPlan plan, QueryRow row, ref MetaState meta, DateTime now)
        {
            NumberFormat.WriteDouble(b, Math.Round(meta.Ut, 4));
            b.WriteByte((byte)',');
            NumberFormat.WriteLong(b, meta.Frame);
            b.WriteByte((byte)',');
            WriteIsoUtc(b, now);
            b.WriteByte((byte)',');
            b.WriteAscii(row.Vessel.Handle);
            b.WriteByte((byte)',');
            CsvString(b, row.Vessel.Name);
            if (row.Part != null) { b.WriteByte((byte)','); b.WriteAscii(row.Part.Handle); }
            if (row.Module != null) { b.WriteByte((byte)','); CsvString(b, row.Module.Handle); }
            var vals = row.Values;
            for (int f = 0; f < vals.Length; f++)
            {
                b.WriteByte((byte)',');
                switch (vals[f].Kind)
                {
                    case FieldKind.Bool: b.WriteAscii(vals[f].Bool ? "true" : "false"); break;
                    case FieldKind.Number: if (NumberFormat.IsFinite(vals[f].Num)) NumberFormat.WriteDouble(b, vals[f].Num); break;
                    case FieldKind.Single: if (NumberFormat.IsFinite(vals[f].Num)) NumberFormat.WriteSingle(b, (float)vals[f].Num); break;
                    case FieldKind.Integer: NumberFormat.WriteLong(b, (long)vals[f].Num); break;
                    case FieldKind.String: CsvString(b, vals[f].Str); break;
                }
            }
            b.WriteByte((byte)'\r');
            b.WriteByte((byte)'\n');
        }

        private static void WriteJsonRow(JsonWriter w, ByteBuffer b, LogFile log, QueryRow row, ref MetaState meta, DateTime now)
        {
            w.Reset(b);
            var t = log.Scratch;
            t.Clear();
            t.WriteByte((byte)'"');
            WriteIsoUtc(t, now);
            t.WriteByte((byte)'"');
            w.BeginObject().Prop("ut", Math.Round(meta.Ut, 4)).Prop("frame", meta.Frame).Name("wallUtc").Raw(t.Data, 0, t.Length);
            w.Prop("vessel", row.Vessel.Handle).Prop("vesselName", row.Vessel.Name);
            if (row.Part != null) w.Prop("part", row.Part.Handle);
            if (row.Module != null) w.Prop("module", row.Module.Handle);
            var fields = log.Plan.Spec.Fields;
            for (int f = 0; f < row.Values.Length; f++)
            {
                w.Name(fields[f]);
                QueryPlan.WriteValue(w, ref row.Values[f]);
            }
            w.EndObject();
            b.WriteByte((byte)'\n');
        }

        /// <summary>Allocation-free CSV field (RFC 4180 quoting when needed).</summary>
        // skizzy:hot LogManager.CsvString
        public static void CsvString(ByteBuffer b, string s)
        {
            if (s == null) return;
            bool quote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ',' || c == '"' || c == '\n' || c == '\r') { quote = true; break; }
            }
            if (!quote) { b.WriteUtf8(s); return; }
            b.WriteByte((byte)'"');
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '"') continue;
                // write s[start..i] then a doubled quote; substring-free by writing chars through a small loop
                WriteRange(b, s, start, i);
                b.WriteByte((byte)'"');
                b.WriteByte((byte)'"');
                start = i + 1;
            }
            WriteRange(b, s, start, s.Length);
            b.WriteByte((byte)'"');
        }

        private static void WriteRange(ByteBuffer b, string s, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                char c = s[i];
                if (c < 0x80) { b.WriteByte((byte)c); continue; }
                if (c < 0x800) { b.WriteByte((byte)(0xC0 | (c >> 6))); b.WriteByte((byte)(0x80 | (c & 0x3F))); continue; }
                if (char.IsHighSurrogate(c) && i + 1 < to && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                    b.WriteByte((byte)(0xF0 | (cp >> 18)));
                    b.WriteByte((byte)(0x80 | ((cp >> 12) & 0x3F)));
                    b.WriteByte((byte)(0x80 | ((cp >> 6) & 0x3F)));
                    b.WriteByte((byte)(0x80 | (cp & 0x3F)));
                    continue;
                }
                if (char.IsSurrogate(c)) c = '\uFFFD';
                b.WriteByte((byte)(0xE0 | (c >> 12)));
                b.WriteByte((byte)(0x80 | ((c >> 6) & 0x3F)));
                b.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
        }

        /// <summary>yyyy-MM-ddTHH:mm:ss.fffZ without allocating.</summary>
        // skizzy:hot LogManager.WriteIsoUtc
        public static void WriteIsoUtc(ByteBuffer b, DateTime t)
        {
            D(b, t.Year, 4); b.WriteByte((byte)'-'); D(b, t.Month, 2); b.WriteByte((byte)'-'); D(b, t.Day, 2);
            b.WriteByte((byte)'T'); D(b, t.Hour, 2); b.WriteByte((byte)':'); D(b, t.Minute, 2); b.WriteByte((byte)':'); D(b, t.Second, 2);
            b.WriteByte((byte)'.'); D(b, t.Millisecond, 3); b.WriteByte((byte)'Z');
        }

        private static void D(ByteBuffer b, int v, int digits)
        {
            b.Ensure(digits);
            for (int i = digits - 1; i >= 0; i--) { b.Data[b.Length + i] = (byte)('0' + v % 10); v /= 10; }
            b.Length += digits;
        }

        // ------------------------------------------------------------------ writer thread

        private void Enqueue(LogFile log, ByteBuffer b, bool close, bool own = false)
        {
            lock (_q)
            {
                _chunks.Enqueue(new Chunk { Log = log, Buf = b, Close = close, Own = own });
                if (_writer == null)
                {
                    _writer = new Thread(WriterMain) { IsBackground = true, Name = "skizzy-log-writer" };
                    _writer.Start();
                }
                Monitor.Pulse(_q);
            }
        }

        private void WriterMain()
        {
            while (true)
            {
                Chunk c;
                lock (_q)
                {
                    while (_chunks.Count == 0) Monitor.Wait(_q);
                    c = _chunks.Dequeue();
                }
                var log = c.Log;
                lock (log.WriteLock)
                {
                    try
                    {
                        if (c.Buf != null && log.Stream != null) log.Stream.Write(c.Buf.Data, 0, c.Buf.Length);
                        if (c.Close && log.Stream != null) { log.Stream.Flush(); log.Stream.Dispose(); log.Stream = null; }
                    }
                    catch (Exception e)
                    {
                        log.State = "failed";
                        log.StopReason = "write failed: " + e.Message;
                        _host.Log.Warn("log " + log.Name + " write failed: " + e.Message);
                        try { log.Stream?.Dispose(); } catch (Exception) { }
                        log.Stream = null;
                    }
                    Monitor.PulseAll(log.WriteLock);
                }
                if (c.Buf == null) continue;
                if (c.Own) log.ReturnChunk(c.Buf);
                else ByteBufferPool.Return(c.Buf);
            }
        }

        /// <summary>Waits until everything queued for <paramref name="log"/> so far is on disk.</summary>
        public void Flush(LogFile log)
        {
            // An empty marker chunk whose completion we wait for: FIFO guarantees earlier chunks are written.
            lock (_q)
            {
                if (_writer == null) return;
            }
            Enqueue(log, null, false);
            long deadline = Clock.NowMs + 2000;
            lock (log.WriteLock)
            {
                while (true)
                {
                    bool pending;
                    lock (_q)
                    {
                        pending = false;
                        foreach (var ch in _chunks) if (ch.Log == log) { pending = true; break; }
                    }
                    if (!pending || Clock.NowMs > deadline) break;
                    Monitor.Wait(log.WriteLock, 50);
                }
                try { log.Stream?.Flush(); } catch (Exception) { }
            }
        }
    }
}
