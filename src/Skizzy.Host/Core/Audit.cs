using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// Audit JSONL (PLAN §6.7, 15 §7): every call, allowed or not, from a background writer thread — never the main
    /// thread. File <c>PluginData/audit/audit-YYYYMMDD.jsonl</c>; a file over 50 MB rolls to <c>-N</c>; 10 newest
    /// files kept. The thread starts on the first record, so an idle host has no audit thread.
    /// </summary>
    public sealed class AuditWriter
    {
        private readonly string _dir;
        private readonly IHostLog _log;
        private readonly object _q = new object();
        private readonly Queue<ByteBuffer> _pending = new Queue<ByteBuffer>();
        private readonly List<JsonValue> _recent = new List<JsonValue>();
        private Thread _thread;
        private bool _stopping;
        private FileStream _file;
        private string _fileDay;
        private int _fileIndex;
        public long MaxFileBytes = 50L * 1024 * 1024;
        public int KeepFiles = 10;
        public long Written;
        public long Dropped;

        public AuditWriter(string dir, IHostLog log)
        {
            _dir = dir;
            _log = log;
        }

        public void Record(JsonValue entry)
        {
            var b = ByteBufferPool.Rent(512);
            new JsonWriter(b).Value(entry);
            b.WriteByte((byte)'\n');
            lock (_q)
            {
                _recent.Add(entry);
                if (_recent.Count > 200) _recent.RemoveRange(0, 50);
                if (_pending.Count > 10000) { Dropped++; ByteBufferPool.Return(b); return; }
                _pending.Enqueue(b);
                if (_thread == null && !_stopping)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "skizzy-audit" };
                    _thread.Start();
                }
                Monitor.Pulse(_q);
            }
        }

        /// <summary>Most recent entries (in-game Activity view, host.sessions).</summary>
        public List<JsonValue> Recent(int n)
        {
            lock (_q)
            {
                int start = Math.Max(0, _recent.Count - n);
                return _recent.GetRange(start, _recent.Count - start);
            }
        }

        private void Run()
        {
            while (true)
            {
                ByteBuffer b;
                lock (_q)
                {
                    while (_pending.Count == 0 && !_stopping) Monitor.Wait(_q);
                    if (_pending.Count == 0) break;
                    b = _pending.Dequeue();
                }
                try
                {
                    EnsureFile(b.Length);
                    _file.Write(b.Data, 0, b.Length);
                    Written++;
                    bool idle;
                    lock (_q) idle = _pending.Count == 0;
                    if (idle) _file.Flush();
                }
                catch (Exception e)
                {
                    Dropped++;
                    _log.Warn("audit write failed: " + e.Message);
                    CloseFile();
                }
                finally { ByteBufferPool.Return(b); }
            }
            CloseFile();
        }

        private void EnsureFile(int next)
        {
            string day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_file != null && day == _fileDay && _file.Length + next <= MaxFileBytes) return;
            CloseFile();
            Directory.CreateDirectory(_dir);
            if (day != _fileDay) { _fileDay = day; _fileIndex = 0; }
            while (true)
            {
                string path = Path.Combine(_dir, "audit-" + day + (_fileIndex == 0 ? "" : "-" + _fileIndex) + ".jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + next > MaxFileBytes) { _fileIndex++; continue; }
                _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                break;
            }
            Prune();
        }

        private void Prune()
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(_dir).GetFiles("audit-*.jsonl"));
                if (files.Count <= KeepFiles) return;
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                for (int i = 0; i < files.Count - KeepFiles; i++) files[i].Delete();
            }
            catch (Exception e) { _log.Warn("audit prune failed: " + e.Message); }
        }

        private void CloseFile()
        {
            try { _file?.Dispose(); } catch (Exception) { }
            _file = null;
        }

        public void Stop()
        {
            Thread t;
            lock (_q)
            {
                _stopping = true;
                t = _thread;
                Monitor.PulseAll(_q);
            }
            t?.Join(2000);
        }
    }
}
