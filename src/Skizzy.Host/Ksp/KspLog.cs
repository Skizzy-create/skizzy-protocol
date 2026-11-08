using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Skizzy.Host.Core;
using UnityEngine;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// Host log (02 §7): every line goes to <c>PluginData/host.log</c> (rotated at 50 MB, 10 kept) from a
    /// background drain, so callers never do file I/O; warnings and errors also go to KSP.log, but only from the main
    /// thread (no Unity API off the main thread, 02 §2.4), so off-thread ones are posted to it.
    /// </summary>
    internal sealed class KspLog : IHostLog
    {
        private const long MaxBytes = 50L * 1024 * 1024;
        private const int Keep = 10;
        private readonly string _path;
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _unity = new ConcurrentQueue<string>();
        private readonly SendOrPostCallback _flushUnity;
        private int _draining;
        private long _size = -1;
        internal int MainThreadId = -1;
        internal SynchronizationContext MainContext;

        public KspLog(string path)
        {
            _path = path;
            _flushUnity = _ => FlushUnity();
        }

        public void Info(string msg) { Write("INF", msg); }

        public void Warn(string msg)
        {
            Write("WRN", msg);
            ToUnity("W" + msg);
        }

        public void Error(string msg)
        {
            Write("ERR", msg);
            ToUnity("E" + msg);
        }

        private void ToUnity(string tagged)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainThreadId) { Emit(tagged); return; }
            _unity.Enqueue(tagged);
            var ctx = MainContext;
            if (ctx != null) ctx.Post(_flushUnity, null);
        }

        /// <summary>Main thread: writes queued warnings/errors to KSP.log (also called by the pump each frame it runs).</summary>
        internal void FlushUnity()
        {
            while (_unity.TryDequeue(out var t)) Emit(t);
        }

        private static void Emit(string tagged)
        {
            string m = "[Skizzy] " + tagged.Substring(1);
            if (tagged[0] == 'E') Debug.LogError(m); else Debug.LogWarning(m);
        }

        private void Write(string level, string msg)
        {
            _lines.Enqueue(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + level + "] " + msg);
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) ThreadPool.QueueUserWorkItem(Drain);
        }

        private void Drain(object state)
        {
            try
            {
                while (true)
                {
                    if (_size < 0) _size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                    using (var w = new StreamWriter(_path, true, new UTF8Encoding(false)))
                    {
                        while (_lines.TryDequeue(out var line))
                        {
                            w.WriteLine(line);
                            _size += line.Length + 2;
                        }
                    }
                    if (_size > MaxBytes) Rotate();
                    Interlocked.Exchange(ref _draining, 0);
                    // A line enqueued after the last TryDequeue but before the flag cleared would otherwise wait for the next write.
                    if (_lines.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
                }
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _draining, 0);
            }
        }

        /// <summary>Shutdown: drain on the calling thread so the last lines reach the file before the process exits.</summary>
        internal void FlushSync()
        {
            for (int i = 0; i < 50 && Volatile.Read(ref _draining) != 0; i++) Thread.Sleep(10);
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) Drain(null);
        }

        private void Rotate()
        {
            try
            {
                string oldest = _path + "." + Keep;
                if (File.Exists(oldest)) File.Delete(oldest);
                for (int i = Keep - 1; i >= 1; i--)
                {
                    string from = _path + "." + i;
                    if (File.Exists(from)) File.Move(from, _path + "." + (i + 1));
                }
                File.Move(_path, _path + ".1");
            }
            catch (Exception) { }
            _size = 0;
        }
    }
}
