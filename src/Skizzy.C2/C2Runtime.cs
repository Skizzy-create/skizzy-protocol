using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    /// <summary>
    /// A game binding the runtime builds the engine on: <c>Skizzy.BDArmory</c>'s <c>BdaBinding</c> in KSP, fakes in tests.
    /// </summary>
    public interface IC2Binding
    {
        IC2World World { get; }
        IBdaControl Control { get; }

        /// <summary>Routes the binding's hook events to <paramref name="sink"/> (the engine); null stops forwarding.</summary>
        void Connect(IHookEvents sink);

        /// <summary>Puts every hook setting the binding changed back to the value captured before it (the engine is going away).</summary>
        void ReleaseAll();

        /// <summary>One line for status displays (BDArmory version, hooks found).</summary>
        string Description { get; }
    }

    /// <summary>
    /// The narrow C2 interface every in-game UI uses (the C2 toolbar window now, the host's own UI later). It never
    /// exposes <see cref="C2Engine"/>: orders, doctrine, unit lists and reports only. Main thread only.
    /// <see cref="C2Runtime"/> implements it over the one engine instance; the host may interpose its own
    /// implementation with <see cref="C2Runtime.SetConsole"/> (for example to audit toolbar orders).
    /// </summary>
    public interface IC2Console
    {
        /// <summary>An engine exists (a binding is attached: flight scene with BDArmory).</summary>
        bool Ready { get; }

        /// <summary>One line for the UI: why there is no engine, or who ticks it.</summary>
        string Status { get; }

        HookSet Hooks { get; }

        /// <summary>Who calls Tick: <see cref="C2Runtime.ToolbarOwner"/> or the owner that claimed it (the host).</summary>
        string Owner { get; }

        /// <summary>Incremented whenever reports arrive or the engine changes (cheap UI refresh test).</summary>
        int ReportSerial { get; }

        double Now { get; }

        void ListSides(List<string> into);

        void ListUnits(List<UnitBrief> into, string side);

        UnitView GetUnitView(uint unit);

        IssueResult Issue(OrderRequest request);

        bool Cancel(int orderId, string by);

        bool Release(uint unit, string by);

        List<string> SetDoctrine(DoctrineLevel level, string target, IDictionary<string, object> values);

        void UnsetDoctrine(DoctrineLevel level, string target, IList<string> keys);

        DoctrineValues SideDoctrine(string side);

        DoctrineValues EffectiveDoctrine(uint unit);

        bool IsSideManaged(string side);

        void SetSideManaged(string side, bool managed);

        /// <summary>Copies up to <paramref name="max"/> of the most recent reports, oldest first.</summary>
        void CopyReports(List<C2Event> into, int max);
    }

    /// <summary>
    /// The one C2 engine in the game and who drives it: the v1.0.0 handover point between the in-game C2 toolbar and
    /// the host (M0). Unity-free, main thread only.
    /// <list type="bullet">
    /// <item>The game binding calls <see cref="Attach"/> when a flight scene with BDArmory starts and <see cref="Detach"/>
    /// when it ends. Attach builds a new <see cref="C2Engine"/> on the binding; Detach drops it and the binding restores
    /// every hook setting it changed. Orders do not survive a scene change.</item>
    /// <item>Exactly one owner calls <see cref="Tick"/> at 5 Hz. By default that is the C2 toolbar
    /// (<see cref="ToolbarOwner"/>), so the toolbar works with no host and no AI client. The host takes over with
    /// <see cref="Claim"/> (the toolbar stops ticking on the next frame) and gives it back with <see cref="Unclaim"/>.</item>
    /// <item>Every report the engine emits is drained here, kept in a short ring for UIs and raised on
    /// <see cref="Reported"/>; the owner publishes them (order.* events) from that event and never calls
    /// <see cref="C2Engine.DrainEvents"/> itself.</item>
    /// </list>
    /// </summary>
    public sealed class C2Runtime : IC2Console
    {
        public const string ToolbarOwner = "toolbar";
        public const int ReportRing = 64;
        public const int FaultLimit = 10;

        private static readonly C2Runtime _current = new C2Runtime();

        /// <summary>The process-wide runtime (one per KSP session).</summary>
        public static C2Runtime Current => _current;

        private IC2Binding _binding;
        private C2Engine _engine;
        private IC2Console _console;
        private string _owner = ToolbarOwner;
        private readonly List<C2Event> _drain = new List<C2Event>();
        private readonly C2Event[] _ring = new C2Event[ReportRing];
        private int _ringNext, _ringCount;
        private int _serial;
        private int _faults;
        private string _fault;

        /// <summary>A fresh runtime (tests; the game uses <see cref="Current"/>).</summary>
        public C2Runtime() { }

        /// <summary>Raised after an engine is built (argument) or dropped (null). The host re-wires its c2.* handlers here.</summary>
        public event Action<C2Engine> EngineChanged;

        /// <summary>Raised for every report, in order, right after the tick or call that produced it.</summary>
        public event Action<C2Event> Reported;

        /// <summary>Where the runtime logs its own faults (the KSP side sets it to Debug.LogError).</summary>
        public Action<string> LogError;

        /// <summary>Applied to every engine the runtime builds (the host sets profiling, tunables, groups).</summary>
        public Action<C2Engine> ConfigureEngine;

        public IC2Binding Binding => _binding;

        /// <summary>The engine instance (null without a binding). The owner may call its API directly, but ticks through <see cref="Tick"/>.</summary>
        public C2Engine Engine => _engine;

        /// <summary>What UIs talk to: the host's interposed console, or the runtime itself.</summary>
        public IC2Console Console => _console ?? this;

        /// <summary>Interposes a console (the host's audited one); null restores the runtime's own.</summary>
        public void SetConsole(IC2Console console) { _console = ReferenceEquals(console, this) ? null : console; }

        /// <summary>Set when the engine threw <see cref="FaultLimit"/> ticks in a row; ticking stops until the next attach.</summary>
        public string Fault => _fault;

        // ------------------------------------------------------------------ binding side

        /// <summary>
        /// A binding is ready (flight scene start): builds a new engine on it and connects the binding's hook events.
        /// A previous binding is detached first.
        /// </summary>
        public C2Engine Attach(IC2Binding binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (_binding != null) Detach(_binding);
            var engine = new C2Engine(binding.World, binding.Control);
            try { ConfigureEngine?.Invoke(engine); }
            catch (Exception e) { Log("C2 runtime: ConfigureEngine threw: " + e.Message); }
            _binding = binding;
            _engine = engine;
            _faults = 0;
            _fault = null;
            binding.Connect(engine);
            _serial++;
            RaiseEngineChanged(engine);
            return engine;
        }

        /// <summary>
        /// The binding is going away (scene end): stops event forwarding, drains the last reports, drops the engine and
        /// has the binding restore every hook setting it changed. A binding that is not attached is ignored.
        /// </summary>
        public void Detach(IC2Binding binding)
        {
            if (binding == null || !ReferenceEquals(binding, _binding)) return;
            var engine = _engine;
            try { binding.Connect(null); }
            catch (Exception e) { Log("C2 runtime: binding.Connect(null) threw: " + e.Message); }
            if (engine != null) Flush(engine);
            _engine = null;
            _binding = null;
            try { binding.ReleaseAll(); }
            catch (Exception e) { Log("C2 runtime: binding.ReleaseAll threw: " + e.Message); }
            _serial++;
            RaiseEngineChanged(null);
        }

        // ------------------------------------------------------------------ ownership

        public string Owner => _owner;

        /// <summary>
        /// <paramref name="owner"/> (the host) takes over ticking. The previous owner's <see cref="Tick"/> calls become
        /// no-ops. Returns false for a null or empty owner.
        /// </summary>
        public bool Claim(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return false;
            _owner = owner;
            _serial++;
            return true;
        }

        /// <summary>Gives ticking back to the toolbar, if <paramref name="owner"/> holds it.</summary>
        public void Unclaim(string owner)
        {
            if (owner != _owner || owner == ToolbarOwner) return;
            _owner = ToolbarOwner;
            _serial++;
        }

        /// <summary>
        /// One C2 tick, if <paramref name="caller"/> is the owner and an engine exists: <see cref="C2Engine.Tick"/>, then
        /// the reports. Returns whether the engine ticked. An engine exception is logged and reported; after
        /// <see cref="FaultLimit"/> in a row ticking stops (see <see cref="Fault"/>).
        /// </summary>
        public bool Tick(string caller)
        {
            var engine = _engine;
            if (engine == null || caller != _owner || _fault != null) return false;
            try
            {
                engine.Tick();
                _faults = 0;
            }
            catch (Exception e)
            {
                _faults++;
                Log("C2 engine tick threw (" + _faults + " in a row): " + e);
                if (_faults >= FaultLimit)
                {
                    _fault = e.GetType().Name + ": " + e.Message;
                    Log("C2 engine stopped after " + FaultLimit + " failed ticks: " + _fault);
                }
                _serial++;
            }
            Flush(engine);
            return true;
        }

        // ------------------------------------------------------------------ IC2Console

        public bool Ready => _engine != null;

        public string Status
        {
            get
            {
                if (_engine == null) return "No C2 engine: needs a flight scene with BDArmory and the Skizzy BDArmory binding.";
                if (_fault != null) return "C2 engine stopped: " + _fault;
                return (_owner == ToolbarOwner ? "C2 engine run by the toolbar" : "C2 engine run by " + _owner) +
                       (_binding != null && _binding.Description != null ? "; " + _binding.Description : "");
            }
        }

        public HookSet Hooks => _binding != null ? _binding.World.Hooks : HookSet.None;

        public int ReportSerial => _serial;

        public double Now => _engine != null ? _engine.Now : 0;

        public void ListSides(List<string> into) { _engine?.ListSides(into); }

        public void ListUnits(List<UnitBrief> into, string side) { _engine?.ListUnits(into, side); }

        public UnitView GetUnitView(uint unit) { return _engine?.GetUnitView(unit); }

        public IssueResult Issue(OrderRequest request)
        {
            var engine = _engine;
            if (engine == null)
            {
                var res = new IssueResult { Code = RefusalCode.HookMissing, Message = Status };
                res.Summary = (request != null ? Orders.Wire(request.Type) : "order") + " refused: " + Status;
                return res;
            }
            var r = engine.Issue(request);
            Flush(engine);
            return r;
        }

        public bool Cancel(int orderId, string by)
        {
            var engine = _engine;
            if (engine == null) return false;
            bool ok = engine.Cancel(orderId, by);
            Flush(engine);
            return ok;
        }

        public bool Release(uint unit, string by)
        {
            var engine = _engine;
            if (engine == null) return false;
            bool ok = engine.Release(unit, by);
            Flush(engine);
            return ok;
        }

        public List<string> SetDoctrine(DoctrineLevel level, string target, IDictionary<string, object> values)
        {
            var engine = _engine;
            if (engine == null) return new List<string> { Status };
            var errors = engine.SetDoctrine(level, target, values);
            Flush(engine);
            return errors;
        }

        public void UnsetDoctrine(DoctrineLevel level, string target, IList<string> keys)
        {
            var engine = _engine;
            if (engine == null) return;
            engine.UnsetDoctrine(level, target, keys);
            Flush(engine);
        }

        public DoctrineValues SideDoctrine(string side) { return _engine?.SideDoctrine(side); }

        public DoctrineValues EffectiveDoctrine(uint unit) { return _engine?.EffectiveDoctrine(unit); }

        public bool IsSideManaged(string side) { return _engine != null && _engine.IsSideManaged(side); }

        public void SetSideManaged(string side, bool managed)
        {
            var engine = _engine;
            if (engine == null || side == null) return;
            engine.SetSideManaged(side, managed);
            _serial++;
        }

        public void CopyReports(List<C2Event> into, int max)
        {
            int n = Math.Min(max, _ringCount);
            int start = (_ringNext - n + ReportRing) % ReportRing;
            for (int i = 0; i < n; i++) into.Add(_ring[(start + i) % ReportRing]);
        }

        // ------------------------------------------------------------------ internals

        private void Flush(C2Engine engine)
        {
            _drain.Clear();
            engine.DrainEvents(_drain);
            if (_drain.Count == 0) return;
            _serial++;
            for (int i = 0; i < _drain.Count; i++)
            {
                var e = _drain[i];
                _ring[_ringNext] = e;
                _ringNext = (_ringNext + 1) % ReportRing;
                if (_ringCount < ReportRing) _ringCount++;
                var h = Reported;
                if (h == null) continue;
                try { h(e); }
                catch (Exception ex) { Log("C2 runtime: a Reported handler threw: " + ex.Message); }
            }
            _drain.Clear();
        }

        private void RaiseEngineChanged(C2Engine engine)
        {
            var h = EngineChanged;
            if (h == null) return;
            try { h(engine); }
            catch (Exception e) { Log("C2 runtime: an EngineChanged handler threw: " + e.Message); }
        }

        private void Log(string message)
        {
            try { LogError?.Invoke(message); }
            catch (Exception) { }
        }
    }
}
