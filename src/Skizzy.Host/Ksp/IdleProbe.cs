using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Skizzy.Host.Core;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// Acceptance 1 measurement (02 §8.1), only when <c>idleProbe = true</c> in settings.cfg: a background thread samples
    /// the managed heap, GC counts and the pump's callback counters once a second into <c>PluginData/idle-probe.csv</c>.
    /// It touches no Unity API. Run once with <c>listen = false</c> (baseline) and once normally, nothing connected,
    /// and compare the heap slope and the pump callback counts (docs/M0-checklist.md).
    /// </summary>
    internal sealed class IdleProbe
    {
        private readonly HostCore _h;
        private readonly SkizzyBootstrap _boot;
        private readonly string _path;
        private Thread _thread;
        private volatile bool _stop;
        private readonly AutoResetEvent _tick = new AutoResetEvent(false);
        // Least-squares heap slope over the run.
        private double _n, _sx, _sy, _sxx, _sxy;
        private long _firstGen0 = -1, _lastGen0, _firstUpdates = -1, _lastUpdates;
        private double _lastT;

        public IdleProbe(HostCore h, SkizzyBootstrap boot, string path)
        {
            _h = h;
            _boot = boot;
            _path = path;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "skizzy-idle-probe", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _tick.Set();
            if (_thread != null) _thread.Join(2000);
            if (_n >= 2)
            {
                double slope = (_n * _sxy - _sx * _sy) / Math.Max(1e-9, _n * _sxx - _sx * _sx);
                double minutes = Math.Max(1e-9, _lastT / 60.0);
                _h.Log.Info("idle probe: " + (int)_n + " samples over " + Inv.S(_lastT) + " s, heap slope " + Inv.S(slope) + " B/s, gen0 " +
                            Inv.S((_lastGen0 - _firstGen0) / minutes) + "/min, pump updates " + (_lastUpdates - _firstUpdates) +
                            " (listen=" + (_h.Settings.Listen ? "true" : "false") + ")");
            }
        }

        private void Run()
        {
            try
            {
                using (var w = new StreamWriter(_path, false, new UTF8Encoding(false)))
                {
                    w.WriteLine("wallUtc,elapsedS,listen,sessions,pumpAwake,pumpUpdates,pumpFixedUpdates,wakes,gcTotalBytes,gen0,gen1,gen2");
                    long start = Clock.Ticks;
                    int lines = 0;
                    while (!_stop)
                    {
                        double t = Clock.SinceMs(start) / 1000.0;
                        long heap = GC.GetTotalMemory(false);
                        long g0 = GC.CollectionCount(0);
                        long upd = Interlocked.Read(ref SkizzyPump.Updates);
                        long fix = Interlocked.Read(ref SkizzyPump.FixedUpdates);
                        w.Write(Clock.WallUtcIso()); w.Write(',');
                        w.Write(t.ToString("0.0", CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(_h.Settings.Listen ? "1" : "0"); w.Write(',');
                        w.Write(_h.AttachedSessionCount.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(_boot.PumpAwake ? "1" : "0"); w.Write(',');
                        w.Write(upd.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(fix.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(_h.Dispatcher.Wakes.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(heap.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(g0.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.Write(GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture)); w.Write(',');
                        w.WriteLine(GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture));
                        if (++lines % 10 == 0) w.Flush();
                        _n++; _sx += t; _sy += heap; _sxx += t * t; _sxy += t * heap;
                        if (_firstGen0 < 0) { _firstGen0 = g0; _firstUpdates = upd; }
                        _lastGen0 = g0;
                        _lastUpdates = upd;
                        _lastT = t;
                        _tick.WaitOne(1000);
                    }
                }
            }
            catch (Exception e) { _h.Log.Warn("idle probe stopped: " + e.Message); }
        }
    }
}
