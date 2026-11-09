using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    /// <summary>
    /// A game binding the engine runs on: <c>Skizzy.BDArmory</c>'s <c>BdaBinding</c> in KSP, fakes in tests.
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
    /// An engine owner outside the runtime: the host's C2 slot (<c>Skizzy.Host.Core.C2Host</c>, 5 Hz from FixedUpdate,
    /// reports published as order.*/log.* events). The BDArmory binding adapts it; tests fake it.
    /// </summary>
    public interface IC2EngineHost
    {
        /// <summary>Short name for status lines and <see cref="IC2Console.Owner"/> ("host").</summary>
        string Name { get; }

        /// <summary>The host's own 5 Hz slot is ticking the engine now (<c>C2Host.WantsTicks</c>: a c2.* call commanded something).</summary>
        bool Ticking { get; }

        /// <summary>One tick through the host: engine tick, then the host drains and publishes the reports (<c>C2Host.Tick()</c>).</summary>
        void Tick();

        /// <summary>The binding is going away: the host drops the engine (<c>C2Host.Detach</c>), if it still holds this one.</summary>
        void Detach(string reason);
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

        /// <summary>Who ticks the engine now: the host's name while its 5 Hz slot runs, else <see cref="C2Runtime.ToolbarOwner"/> (or a claimant).</summary>
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
    /// The one C2 engine in the game and who drives it: the v1.0.0 handover point between the in-game C2 toolbar, the
    /// host (M0) and the BDArmory binding. Unity-free, main thread only.
    /// <list type="bullet">
    /// <item><b>With the host running</b> (the normal install), the binding attaches the engine through the host
    /// (<c>SkizzyBootstrap.CurrentHost.C2.Attach(world, control, "bdarmory")</c>, M0's wiring) and hands it here with
    /// <see cref="Adopt"/>. The host owns it: c2.* capabilities, the 5 Hz C2 slot once a c2.* call commanded something,
    /// order.*/log.* publication, kill switch, scene change. While the host's slot is idle (no AI client has commanded
    /// anything yet) toolbar orders still run: the toolbar ticks the engine at 5 Hz <i>through the host</i>
    /// (<see cref="IC2EngineHost.Tick"/>), so the host still publishes every report. When the host drops the engine
    /// (scene change, replaced) the binding calls <see cref="Detach"/>.</item>
    /// <item><b>Without the host</b> (not installed, or it failed to start), the binding calls <see cref="Attach"/> and the
    /// runtime builds its own engine; the toolbar ticks it at 5 Hz. Another owner can take ticking with <see cref="Claim"/>.</item>
    /// <item>Reports reach UIs through <see cref="C2Engine.ReportObserver"/> without draining (the host drains); they are
    /// kept in a short ring and raised on <see cref="Reported"/> after the call or tick that produced them.</item>
    /// <item>Ticking costs nothing until something is commanded: the toolbar ticks only while its window is open or
    /// after a console call changed something (<see cref="Commanded"/>), like the host's own rule.</item>
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
        private IC2EngineHost _host;
        private IC2Console _console;
        private string _owner = ToolbarOwner;
        private readonly List<C2Event> _drain = new List<C2Event>();
        private readonly List<C2Event> _pending = new List<C2Event>();
        private readonly List<C2Event> _deliver = new List<C2Event>();
        private readonly Action<C2Event> _observe;
        private readonly C2Event[] _ring = new C2Event[ReportRing];
        private int _ringNext, _ringCount;
        private int _serial;
        private int _faults;
        private string _fault;

        /// <summary>A fresh runtime (tests; the game uses <see cref="Current"/>).</summary>
        public C2Runtime() { _observe = e => _pending.Add(e); }

        /// <summary>Raised after an engine is attached or adopted (argument) or dropped (null).</summary>
        public event Action<C2Engine> EngineChanged;

        /// <summary>Raised for every report, in order, after the call or tick that produced it.</summary>
        public event Action<C2Event> Reported;

        /// <summary>Where the runtime logs its own faults (the KSP side sets it to Debug.LogError).</summary>
        public Action<string> LogError;

        /// <summary>Applied to every engine the runtime builds itself (not to an adopted one: the host configures that).</summary>
        public Action<C2Engine> ConfigureEngine;

        public IC2Binding Binding => _binding;

        /// <summary>The engine instance (null without a binding).</summary>
        public C2Engine Engine => _engine;

        /// <summary>The host that owns the engine (null: the runtime's own engine).</summary>
        public IC2EngineHost Host => _host;

        /// <summary>What UIs talk to: the host's interposed console, or the runtime itself.</summary>
        public IC2Console Console => _console ?? this;

        /// <summary>Interposes a console (the host's audited one); null restores the runtime's own.</summary>
        public void SetConsole(IC2Console console) { _console = ReferenceEquals(console, this) ? null : console; }

        /// <summary>Set when the engine threw <see cref="FaultLimit"/> ticks in a row; ticking stops until the next attach.</summary>
        public string Fault => _fault;

        /// <summary>A console call changed something since the engine was attached: the toolbar keeps ticking.</summary>
        public bool Commanded { get; private set; }

        // ------------------------------------------------------------------ binding side

        /// <summary>
        /// No host: builds the runtime's own engine on <paramref name="binding"/> and connects the binding's hook
        /// events to it. A previous binding is detached first.
        /// </summary>
        public C2Engine Attach(IC2Binding binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var engine = new C2Engine(binding.World, binding.Control);
            try { ConfigureEngine?.Invoke(engine); }
            catch (Exception e) { Log("C2 runtime: ConfigureEngine threw: " + e.Message); }
            Install(binding, engine, null);
            return engine;
        }

        /// <summary>
        /// The host built <paramref name="engine"/> on <paramref name="binding"/> (<c>C2Host.Attach</c>): the runtime
        /// connects the binding's hook events to it and gives UIs access; <paramref name="host"/> keeps owning it.
        /// </summary>
        public void Adopt(IC2Binding binding, C2Engine engine, IC2EngineHost host)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            if (host == null) throw new ArgumentNullException(nameof(host));
            Install(binding, engine, host);
        }

        private void Install(IC2Binding binding, C2Engine engine, IC2EngineHost host)
        {
            if (_binding != null) Detach(_binding);
            _binding = binding;
            _engine = engine;
            _host = host;
            _faults = 0;
            _fault = null;
            Commanded = false;
            _pending.Clear();
            engine.ReportObserver = _observe;
            binding.Connect(engine);
            _serial++;
            RaiseEngineChanged(engine);
        }

        /// <summary>
        /// The binding is going away (scene end), or the host dropped the engine: stops event forwarding, has the host
        /// drop the engine (if it still holds it), delivers the last reports and has the binding restore every setting
        /// it changed. A binding that is not attached is ignored, so a second call is harmless.
        /// </summary>
        public void Detach(IC2Binding binding)
        {
            if (binding == null || !ReferenceEquals(binding, _binding)) return;
            var engine = _engine;
            var host = _host;
            _engine = null;
            _binding = null;
            _host = null;
            Commanded = false;
            try { binding.Connect(null); }
            catch (Exception e) { Log("C2 runtime: binding.Connect(null) threw: " + e.Message); }
            if (host != null)
            {
                try { host.Detach("binding_detached"); }
                catch (Exception e) { Log("C2 runtime: host detach threw: " + e.Message); }
            }
            else if (engine != null) DrainOwn(engine);
            if (engine != null) engine.ReportObserver = null;
            Deliver();
            try { binding.ReleaseAll(); }
            catch (Exception e) { Log("C2 runtime: binding.ReleaseAll threw: " + e.Message); }
            _serial++;
            RaiseEngineChanged(null);
        }

        // ------------------------------------------------------------------ ownership

        /// <summary>Who ticks now: the host's name while its slot runs, else the toolbar or the claimant.</summary>
        public string Owner => _host != null ? (_host.Ticking ? _host.Name : ToolbarOwner) : _owner;

        /// <summary>
        /// Runtime's own engine only: <paramref name="owner"/> takes over ticking. The previous owner's
        /// <see cref="Tick"/> calls become no-ops. Returns false for a null or empty owner.
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
        /// One C2 tick if <paramref name="caller"/> owns ticking now (see <see cref="Owner"/>): through the host when it
        /// owns the engine, else on the runtime's own engine. Delivers pending reports either way. Returns whether a
        /// tick ran. An exception is logged; after <see cref="FaultLimit"/> in a row ticking stops (see <see cref="Fault"/>).
        /// </summary>
        public bool Tick(string caller)
        {
            var engine = _engine;
            bool ran = false;
            if (engine != null && _fault == null && caller == Owner && (_host == null || !_host.Ticking))
            {
                ran = true;
                try
                {
                    if (_host != null) _host.Tick(); // the host ticks, drains and publishes
                    else engine.Tick();
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
                if (_host == null) DrainOwn(engine);
            }
            Deliver();
            return ran;
        }

        // ------------------------------------------------------------------ IC2Console

        public bool Ready => _engine != null;

        public string Status
        {
            get
            {
                if (_engine == null) return "No C2 engine: needs a flight scene with BDArmory and the Skizzy BDArmory binding.";
                if (_fault != null) return "C2 engine stopped: " + _fault;
                string who;
                if (_host != null) who = _host.Ticking ? "C2 engine run by the " + _host.Name : "C2 engine of the " + _host.Name + ", ticked by the toolbar";
                else who = _owner == ToolbarOwner ? "C2 engine run by the toolbar" : "C2 engine run by " + _owner;
                return who + (_binding != null && _binding.Description != null ? "; " + _binding.Description : "");
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
            if (r.Accepted) Commanded = true;
            After(engine);
            return r;
        }

        public bool Cancel(int orderId, string by)
        {
            var engine = _engine;
            if (engine == null) return false;
            bool ok = engine.Cancel(orderId, by);
            if (ok) Commanded = true;
            After(engine);
            return ok;
        }

        public bool Release(uint unit, string by)
        {
            var engine = _engine;
            if (engine == null) return false;
            bool ok = engine.Release(unit, by);
            if (ok) Commanded = true;
            After(engine);
            return ok;
        }

        public List<string> SetDoctrine(DoctrineLevel level, string target, IDictionary<string, object> values)
        {
            var engine = _engine;
            if (engine == null) return new List<string> { Status };
            var errors = engine.SetDoctrine(level, target, values);
            if (errors == null || errors.Count == 0) Commanded = true;
            After(engine);
            return errors;
        }

        public void UnsetDoctrine(DoctrineLevel level, string target, IList<string> keys)
        {
            var engine = _engine;
            if (engine == null) return;
            engine.UnsetDoctrine(level, target, keys);
            Commanded = true;
            After(engine);
        }

        public DoctrineValues SideDoctrine(string side) { return _engine?.SideDoctrine(side); }

        public DoctrineValues EffectiveDoctrine(uint unit) { return _engine?.EffectiveDoctrine(unit); }

        public bool IsSideManaged(string side) { return _engine != null && _engine.IsSideManaged(side); }

        public void SetSideManaged(string side, bool managed)
        {
            var engine = _engine;
            if (engine == null || side == null) return;
            engine.SetSideManaged(side, managed);
            Commanded = true;
            _serial++;
        }

        public void CopyReports(List<C2Event> into, int max)
        {
            int n = Math.Min(max, _ringCount);
            int start = (_ringNext - n + ReportRing) % ReportRing;
            for (int i = 0; i < n; i++) into.Add(_ring[(start + i) % ReportRing]);
        }

        // ------------------------------------------------------------------ internals

        private void After(C2Engine engine)
        {
            if (_host == null) DrainOwn(engine); // a host engine is drained by the host when it publishes
            Deliver();
        }

        /// <summary>Own engine: nobody else drains it; the reports were already seen through the observer.</summary>
        private void DrainOwn(C2Engine engine)
        {
            _drain.Clear();
            engine.DrainEvents(_drain);
            _drain.Clear();
        }

        private void Deliver()
        {
            if (_pending.Count == 0) return;
            _deliver.Clear();
            _deliver.AddRange(_pending);
            _pending.Clear();
            _serial++;
            for (int i = 0; i < _deliver.Count; i++)
            {
                var e = _deliver[i];
                _ring[_ringNext] = e;
                _ringNext = (_ringNext + 1) % ReportRing;
                if (_ringCount < ReportRing) _ringCount++;
                var h = Reported;
                if (h == null) continue;
                try { h(e); }
                catch (Exception ex) { Log("C2 runtime: a Reported handler threw: " + ex.Message); }
            }
            _deliver.Clear();
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
