using System;
using System.Runtime.CompilerServices;
using Skizzy.C2;
using Skizzy.Host.Core;
using Skizzy.Host.Ksp;

namespace Skizzy.Bda.Ksp
{
    /// <summary>
    /// Attaches the C2 engine through the host when it runs (M0's wiring:
    /// <c>SkizzyBootstrap.CurrentHost.C2.Attach(world, control, "bdarmory")</c>), so the host, its c2.* capabilities,
    /// the C2 toolbar and the binding share one engine. Skizzy.Host types are only touched after
    /// <see cref="HostLoaded"/> says the assembly is there, so the binding still loads without it (then the runtime
    /// builds its own engine).
    /// </summary>
    internal static class HostBridge
    {
        public static bool HostLoaded()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == "Skizzy.Host") return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>The host's engine slot adapted for <see cref="C2Runtime.Adopt"/>, or null when the host is not running.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IC2EngineHost TryAttach(IC2Binding binding, Action<string> hostDetached, out C2Engine engine)
        {
            engine = null;
            var h = SkizzyBootstrap.CurrentHost;
            if (h == null || h.C2 == null) return null;
            engine = h.C2.Attach(binding.World, binding.Control, "bdarmory");
            return new HostSlot(h.C2, engine, hostDetached);
        }
    }

    /// <summary><see cref="IC2EngineHost"/> over the host's <c>C2Host</c>, bound to the one engine it attached for us.</summary>
    internal sealed class HostSlot : IC2EngineHost
    {
        private readonly C2Host _c2;
        private readonly C2Engine _engine;
        private Action<string> _detached;

        public HostSlot(C2Host c2, C2Engine engine, Action<string> detached)
        {
            _c2 = c2;
            _engine = engine;
            _detached = detached;
            _c2.Detached += OnDetached;
        }

        private bool Ours => ReferenceEquals(_c2.Engine, _engine);

        public string Name => "host";

        public bool Ticking => Ours && _c2.WantsTicks;

        public void Tick()
        {
            if (Ours) _c2.Tick();
        }

        public void Detach(string reason)
        {
            _c2.Detached -= OnDetached;
            _detached = null;
            if (Ours) _c2.Detach(reason);
        }

        // Scene change, kill of the binding, or another attach replaced our engine.
        private void OnDetached(string reason)
        {
            if (Ours) return; // a different engine was dropped
            _c2.Detached -= OnDetached;
            var d = _detached;
            _detached = null;
            d?.Invoke(reason);
        }
    }
}
