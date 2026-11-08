using System;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Host.Core.World;
using UnityEngine;

namespace Skizzy.Host.Ksp
{
    /// <summary>
    /// Wakes the pump (02 §2, spike 3). With <c>UnitySynchronizationContext</c> the pump component is disabled when
    /// idle, so Unity calls nothing of ours per frame, and a wake posts one callback that re-enables it. Without it
    /// (polling fallback) the pump stays enabled and an idle frame costs one volatile read.
    /// </summary>
    internal sealed class KspWaker : IMainThreadWaker
    {
        private readonly SynchronizationContext _ctx;
        private readonly SendOrPostCallback _enable;
        private readonly string _mode;
        internal SkizzyPump Pump;
        /// <summary>Set by every wake; the polling pump checks it while asleep.</summary>
        internal volatile bool Pending;
        internal long Posts;

        public KspWaker(SynchronizationContext ctx)
        {
            _ctx = ctx;
            _mode = ctx != null && ctx.GetType().Name == "UnitySynchronizationContext" ? "sync_context" : "polling";
            _enable = OnWake;
        }

        public string Mode => _mode;
        internal string ContextType => _ctx == null ? "none" : _ctx.GetType().FullName;
        internal SynchronizationContext Context => _mode == "sync_context" ? _ctx : null;

        public void Wake()
        {
            Pending = true;
            if (_mode != "sync_context") return;
            Interlocked.Increment(ref Posts);
            _ctx.Post(_enable, null);
        }

        private void OnWake(object state)
        {
            var p = Pump;
            if (p != null && !p.enabled) p.enabled = true;
        }
    }

    /// <summary>
    /// The only per-frame entry point (02 §2): enabled only while <see cref="HostCore.WantsUpdates"/>. Nothing
    /// connected and nothing running means this component is disabled and costs nothing (acceptance 1).
    /// </summary>
    internal sealed class SkizzyPump : MonoBehaviour
    {
        internal HostCore Host;
        internal KspWaker Waker;
        internal SkizzyBootstrap Boot;
        /// <summary>Callback counters for the idle probe (proof of zero callbacks while idle).</summary>
        internal static long Updates, FixedUpdates;
        private bool _sleeping;          // polling mode only
        private int _faults;

        private void OnEnable()
        {
            if (Host == null) return;
            Host.Dispatcher.MarkAwake();
            Boot.OnPumpAwake();
        }

        private void OnDisable()
        {
            if (Host == null) return;
            Boot.OnPumpAsleep();
        }

        // skizzy:hot SkizzyPump.Update
        private void Update()
        {
            if (_sleeping)
            {
                if (!Waker.Pending) return;
                _sleeping = false;
                Host.Dispatcher.MarkAwake();
                Boot.OnPumpAwake();
            }
            Waker.Pending = false;
            Updates++;
            try
            {
                Host.OnUpdate(Time.unscaledDeltaTime * 1000.0);
                Boot.OnFrame();
            }
            catch (Exception e) { Fault("Update", e); }
            if (Host.AfterFrame()) return;
            if (Waker.Mode == "sync_context") enabled = false;
            else
            {
                _sleeping = true;
                Boot.OnPumpAsleep();
            }
        }

        // skizzy:hot SkizzyPump.FixedUpdate
        private void FixedUpdate()
        {
            if (_sleeping) return;
            FixedUpdates++;
            try { Host.OnFixedUpdate(); }
            catch (Exception e) { Fault("FixedUpdate", e); }
        }

        private void Fault(string where, Exception e)
        {
            // Throttled: a persistent fault must not flood KSP.log every frame.
            if (++_faults <= 5 || _faults % 1000 == 0) Host.Log.Error("pump " + where + " fault #" + _faults + ": " + e);
        }
    }
}
