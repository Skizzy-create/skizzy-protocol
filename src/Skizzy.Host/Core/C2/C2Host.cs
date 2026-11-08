using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Skizzy.C2;
using Skizzy.Host.Core.Caps;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// Hosts the C2 orders and doctrine engine (src/Skizzy.C2, 24 Phase v1) inside the host. The BDArmory binding owns
    /// the game side: it implements <see cref="IC2World"/> and <see cref="IBdaControl"/>, calls <see cref="Attach"/>
    /// in flight and forwards the BDArmory hook events to the returned engine (main thread).
    ///
    /// Cost rules (acceptance 1): nothing ticks until a c2.* call has commanded something since the attach
    /// (<see cref="Engaged"/>); without the binding the c2.* capabilities fail with EXECUTOR_UNAVAILABLE and no code
    /// here runs per frame beyond one field read. While engaged, <see cref="C2Engine.Tick"/> runs from the C2 slot of
    /// the host's FixedUpdate every <see cref="TickEveryFixedUpdates"/> physics steps (10 = 5 Hz at KSP's 0.02 s step).
    ///
    /// Reports (<see cref="C2Event"/>) become <c>order.*</c> events (lifecycle) and <c>log.*</c> events (message log:
    /// interruption, diversion, contact, first_launch, advance_started, info), each also written to host.log.
    /// A FirstLaunch report with <c>PauseRequested</c> (doctrine pauseOnFirstLaunch) pauses the game.
    /// The kill switch cancels every order, releases every commanded unit and un-manages every side.
    /// All engine calls happen on the main thread.
    /// </summary>
    public sealed class C2Host
    {
        private readonly HostCore _h;
        private C2Engine _engine;
        private IC2World _world;
        private string _source;
        private readonly HashSet<string> _managedSides = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<C2Event> _drain = new List<C2Event>();
        private readonly List<UnitState> _units = new List<UnitState>();
        private int _fixedCount;
        private int _faultsInARow;
        private volatile bool _killPending;

        /// <summary>Physics steps between ticks: 10 x 0.02 s = 5 Hz.</summary>
        public int TickEveryFixedUpdates = 10;
        /// <summary>Consecutive failing ticks before ticking stops (host.degraded event).</summary>
        public int FaultLimit = 5;

        public long Ticks;
        public long Faults;
        public long EventsPublished;
        public long Pauses;
        public readonly TimingWindow TickMs = new TimingWindow();

        /// <summary>Raised on the main thread when the engine is dropped (scene change, kill of the binding, Detach).</summary>
        public event Action<string> Detached;

        public C2Host(HostCore h) { _h = h; }

        public C2Engine Engine => _engine;
        public bool Attached => _engine != null;
        public string Source => _source;
        /// <summary>A c2.* call commanded something since the attach; the engine ticks only then.</summary>
        public bool Engaged { get; private set; }
        /// <summary>Read by <see cref="HostCore.WantsUpdates"/>.</summary>
        public bool WantsTicks => _engine != null && Engaged;

        // ------------------------------------------------------------------ binding API (main thread)

        /// <summary>
        /// Main thread, in flight. Creates the engine over the binding's world and control and returns it; the binding
        /// forwards BDArmory hook events to it (it implements <see cref="IHookEvents"/>). Replaces a previous engine.
        /// </summary>
        public C2Engine Attach(IC2World world, IBdaControl control, string source)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (control == null) throw new ArgumentNullException(nameof(control));
            if (_engine != null) Detach("replaced");
            _engine = new C2Engine(world, control);
            _world = world;
            _source = source ?? "binding";
            Engaged = false;
            _fixedCount = 0;
            _faultsInARow = 0;
            _managedSides.Clear();
            _h.Log.Info("c2: engine attached (" + _source + ")");
            _h.Events.Emit("host.c2_attached", "c2", "C2 engine attached (" + _source + ")", JsonValue.Obj().Set("source", _source));
            return _engine;
        }

        /// <summary>Main thread. Drops the engine; pending reports are published first.</summary>
        public void Detach(string reason)
        {
            if (_engine == null) return;
            try { Publish(); } catch (Exception e) { _h.Log.Warn("c2: publish on detach failed: " + e.Message); }
            _engine = null;
            _world = null;
            Engaged = false;
            _managedSides.Clear();
            _h.Log.Info("c2: engine detached (" + reason + ")");
            _h.Events.Emit("host.c2_detached", "c2", "C2 engine detached: " + reason, JsonValue.Obj().Set("reason", reason));
            var d = Detached;
            if (d != null)
            {
                try { d(reason); } catch (Exception e) { _h.Log.Warn("c2: Detached handler failed: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ host hooks

        /// <summary>Main thread, every physics step while the pump runs and <see cref="WantsTicks"/>.</summary>
        internal void OnFixedUpdate()
        {
            if (_engine == null || !Engaged) return;
            if (++_fixedCount < TickEveryFixedUpdates) return;
            _fixedCount = 0;
            Tick();
        }

        /// <summary>Main thread, every frame while the pump runs: applies a kill switch engaged on another thread.</summary>
        internal void OnUpdate()
        {
            if (_killPending) ApplyKill();
        }

        /// <summary>Any thread (HostCore.EngageKill).</summary>
        internal void OnKillSwitch()
        {
            if (_engine == null) return;
            _killPending = true;
            _h.Dispatcher.Wake();
        }

        /// <summary>Main thread: the scene is about to change; units and handles die with it.</summary>
        internal void OnSceneChange()
        {
            if (_engine != null) Detach("scene_change");
        }

        /// <summary>Main thread: one engine tick and the report drain (tests call it directly).</summary>
        public void Tick()
        {
            var e = _engine;
            if (e == null) return;
            long t0 = Clock.Ticks;
            try
            {
                e.Tick();
                _faultsInARow = 0;
            }
            catch (Exception ex)
            {
                Faults++;
                _faultsInARow++;
                if (Faults <= 5 || Faults % 100 == 0) _h.Log.Warn("c2: tick failed (#" + Faults + "): " + ex);
                if (_faultsInARow >= FaultLimit)
                {
                    Engaged = false;
                    _h.Events.Emit("host.degraded", "system", "C2 ticking stopped after " + _faultsInARow + " failing ticks",
                        JsonValue.Obj().Set("component", "c2").Set("error", ex.GetType().Name + ": " + ex.Message));
                }
            }
            Ticks++;
            Publish();
            TickMs.Add(Clock.SinceMs(t0));
        }

        private void ApplyKill()
        {
            _killPending = false;
            var e = _engine;
            if (e == null) return;
            var ids = new List<int>();
            foreach (var o in e.ActiveOrders) ids.Add(o.Id);
            foreach (var id in ids) e.Cancel(id, "kill switch");
            _units.Clear();
            try { _world.GetUnits(_units); } catch (Exception ex) { _h.Log.Warn("c2: kill switch unit list failed: " + ex.Message); }
            foreach (var u in _units) e.Release(u.Id, "kill switch");
            foreach (var s in _managedSides) e.SetSideManaged(s, false);
            _managedSides.Clear();
            _h.Log.Info("c2: kill switch: " + ids.Count + " orders cancelled, units released, sides un-managed");
            // One more tick writes the releases to BDArmory; then the engine idles until the next c2.* command.
            Tick();
            Engaged = false;
        }

        // ------------------------------------------------------------------ reports

        private static readonly string[] EventNames =
        {
            "order.accepted", "order.refused", "order.progress", "order.achieved", "order.failed", "order.cancelled",
            "order.superseded", "order.suspended", "order.resumed",
            "log.interruption", "log.back_on_task", "log.diversion", "log.contact", "log.first_launch", "log.advance_started", "log.info",
        };

        /// <summary>The event name for a report kind (order.* lifecycle, log.* message log).</summary>
        public static string EventName(C2EventKind k)
        {
            int i = (int)k;
            return i >= 0 && i < EventNames.Length ? EventNames[i] : "log.info";
        }

        private void Publish()
        {
            var e = _engine;
            if (e == null) return;
            e.DrainEvents(_drain);
            if (_drain.Count == 0) return;
            for (int i = 0; i < _drain.Count; i++)
            {
                var ev = _drain[i];
                string name = EventName(ev.Kind);
                string cat = ev.Category == "orders" ? "order" : (ev.Category ?? "c2");
                var data = JsonValue.Obj()
                    .Set("kind", name.Substring(name.IndexOf('.') + 1))
                    .Set("ut", Math.Round(ev.Ut, 2))
                    .Set("side", ev.Side)
                    .Set("order", ev.Order > 0 ? "o:" + ev.Order.ToString(CultureInfo.InvariantCulture) : null)
                    .Set("unit", ev.Unit != 0 ? HandleRegistry.VesselHandle(ev.Unit) : null)
                    .Set("code", ev.Code)
                    .Set("text", ev.Text);
                if (ev.Kind == C2EventKind.FirstLaunch && ev.PauseRequested)
                {
                    bool paused = false;
                    try { paused = _h.World.RequestPause("first " + (ev.Side ?? "") + " launch"); }
                    catch (Exception ex) { _h.Log.Warn("c2: pause request failed: " + ex.Message); }
                    if (paused) Pauses++;
                    data.Set("pauseRequested", true).Set("paused", paused);
                }
                _h.Events.Emit(name, cat, ev.Text ?? name, data);
                _h.Log.Info("c2: " + name + (ev.Side != null ? " [" + ev.Side + "]" : "") + " " + (ev.Text ?? ""));
                EventsPublished++;
            }
            _drain.Clear();
        }

        // ------------------------------------------------------------------ status

        public JsonValue StatsJson()
        {
            var o = JsonValue.Obj().Set("attached", Attached).Set("source", _source).Set("engaged", Engaged)
                .Set("ticks", Ticks).Set("faults", Faults).Set("events", EventsPublished).Set("pauses", Pauses)
                .Set("tickMs", HostStats.Timing(TickMs));
            var e = _engine;
            if (e != null) o.Set("activeOrders", e.ActiveOrders.Count).Set("managedSides", JsonValue.ArrOf(new List<string>(_managedSides).ToArray()));
            return o;
        }

        public string Describe()
        {
            if (_engine == null) return "not attached (needs the BDArmory binding in flight); no C2 cost";
            TickMs.Percentiles(out double p50, out double p99, out double max);
            return "attached (" + _source + "), " + (Engaged ? "ticking at 5 Hz" : "idle until a c2.* command") + ", " + Ticks + " ticks, tick p50 " +
                   Inv.S(p50) + " / p99 " + Inv.S(p99) + " ms, " + _engine.ActiveOrders.Count + " active orders, " + Faults + " faults";
        }

        // ------------------------------------------------------------------ capabilities

        public static void Register(HostCore h)
        {
            h.Register("c2.order.issue", c => h.C2.Issue(c));
            h.Register("c2.order.cancel", c => h.C2.CancelCap(c));
            h.Register("c2.doctrine.set", c => h.C2.DoctrineSet(c));
            h.Register("c2.doctrine.unset", c => h.C2.DoctrineUnset(c));
            h.Register("c2.doctrine.effective", c => h.C2.DoctrineEffective(c));
            h.Register("c2.unit.status", c => h.C2.UnitStatus(c));
            h.Register("c2.unit.release", c => h.C2.ReleaseCap(c));
            h.Register("c2.vip.set", c => h.C2.VipSet(c));
            h.Register("c2.side.managed", c => h.C2.SideManaged(c));
        }

        private C2Engine Need()
        {
            var e = _engine;
            if (e == null)
                throw new SkizzyError(ErrorKind.ExecutorUnavailable, "the C2 engine is not attached", "c2_not_attached",
                    "C2 orders need the Skizzy BDArmory binding in a flight scene (and BDArmory installed).");
            return e;
        }

        private void Commanded()
        {
            if (!Engaged)
            {
                Engaged = true;
                _fixedCount = TickEveryFixedUpdates - 1; // first tick on the next physics step
            }
            _h.Dispatcher.Wake();
        }

        private static string By(CallContext c)
        {
            var s = c.Session;
            if (s == null) return "unknown";
            return s.ActorId ?? s.Display ?? s.ClientName ?? "unknown";
        }

        private static bool Privileged(Session s)
        {
            return s == null || s.ActorKind == "human" || s.Role == "referee" || s.Role == "director";
        }

        /// <summary>The side a call acts for: the parameter, else the session's side. Other sides need a human, referee or director.</summary>
        private static string ActingSide(CallContext c, string requested, bool required)
        {
            var s = c.Session;
            string own = s != null ? s.Side : null;
            string side = requested ?? own;
            if (side == null)
            {
                if (required) throw SkizzyError.InvalidParam("/side", "no side given and the session has none", "Pass side, or connect with --side.");
                return null;
            }
            if (own != null && !string.Equals(own, side, StringComparison.Ordinal) && !Privileged(s))
                throw new SkizzyError(ErrorKind.PermissionDenied, "this session acts for side " + own + ", not " + side, "not_own_side",
                    "Act for your own side, or connect as a referee.");
            return side;
        }

        private static IssuerRole RoleOf(Session s)
        {
            if (s == null || s.ActorKind == "human" || s.SeatHolder) return IssuerRole.Seat;
            switch (s.Role)
            {
                case "commander": return IssuerRole.Seat;
                case "referee": return IssuerRole.Referee;
                case "delegate": return IssuerRole.Delegate;
                case "director": return IssuerRole.Director;
                default: return IssuerRole.Script;
            }
        }

        /// <summary>Main thread: "active", "v:&lt;id&gt;", a numeric id, or a unit name known to the C2 world.</summary>
        internal uint ResolveUnit(string reference, string pointer)
        {
            if (string.IsNullOrEmpty(reference)) throw SkizzyError.InvalidParam(pointer, "empty unit reference");
            if (reference == "active")
            {
                var a = _h.World.ActiveVessel;
                if (a == null) throw SkizzyError.NotFound("active", "unknown", "There is no active vessel.");
                return a.PersistentId;
            }
            uint id;
            if (HandleRegistry.TryParseVessel(reference, out id)) return id;
            if (uint.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out id)) return id;
            _units.Clear();
            if (_world != null) _world.GetUnits(_units);
            UnitState ci = null;
            int ciCount = 0;
            foreach (var u in _units)
            {
                if (u.Name == null) continue;
                if (string.Equals(u.Name, reference, StringComparison.Ordinal)) return u.Id;
                if (string.Equals(u.Name, reference, StringComparison.OrdinalIgnoreCase)) { ci = u; ciCount++; }
            }
            if (ciCount == 1) return ci.Id;
            if (ciCount > 1) throw SkizzyError.InvalidParam(pointer, "unit name '" + reference + "' is ambiguous", "Use the handle v:<id>.");
            throw SkizzyError.NotFound(reference, "unknown", "No C2 unit with that name; units are vessels with a BDArmory AI or weapon manager. Use ksp.vessels.list for handles.");
        }

        private static GeoPoint Pt(JsonValue o)
        {
            return new GeoPoint(o.GetDouble("lat"), o.GetDouble("lon"));
        }

        private static string Handle(uint id) { return HandleRegistry.VesselHandle(id); }

        private static ErrorKind KindOf(string wire)
        {
            switch (wire)
            {
                case "INVALID_PARAMS": return ErrorKind.InvalidParams;
                case "NOT_FOUND": return ErrorKind.NotFound;
                case "PERMISSION_DENIED": return ErrorKind.PermissionDenied;
                case "NOT_IN_VIEW": return ErrorKind.NotInView;
                case "EXECUTOR_UNAVAILABLE": return ErrorKind.ExecutorUnavailable;
                default: return ErrorKind.PreconditionFailed;
            }
        }

        private static JsonValue OutcomesJson(IssueResult r)
        {
            var arr = JsonValue.Arr();
            foreach (var u in r.Units)
            {
                var o = JsonValue.Obj().Set("unit", Handle(u.Unit)).Set("name", u.Name).Set("accepted", u.Accepted);
                if (!u.Accepted) o.Set("code", Orders.Wire(u.Code)).Set("reason", u.Reason);
                if (u.SubstitutedBy.HasValue) o.Set("substitutedBy", Orders.Wire(u.SubstitutedBy.Value));
                arr.Add(o);
            }
            return arr;
        }

        private JsonValue Issue(CallContext c)
        {
            var e = Need();
            var p = c.Params;
            OrderType type;
            if (!Orders.TryParse(c.Str("type"), out type)) throw SkizzyError.InvalidParam("/type", "unknown order type " + c.Str("type"));
            var req = new OrderRequest
            {
                Type = type,
                Issuer = By(c),
                Role = RoleOf(c.Session),
                Side = ActingSide(c, c.Str("side"), true),
                Group = c.Str("group"),
                SideScope = c.Str("scope", "units") == "side" || type == OrderType.AllAttack,
                Label = c.Str("label"),
            };
            var units = p.Get("units");
            if (units != null && units.IsArray)
                for (int i = 0; i < units.Count; i++) req.Units.Add(ResolveUnit(units[i].AsString(), "/units/" + i));
            switch (c.Str("domain"))
            {
                case "air": req.DomainFilter = Domain.Air; break;
                case "surface": req.DomainFilter = Domain.Surface; break;
                case "subsurface": req.DomainFilter = Domain.Subsurface; break;
                case "land": req.DomainFilter = Domain.Land; break;
            }
            var point = p.GetObj("point");
            if (point != null) req.Point = Pt(point);
            if (c.Has("altitudeM")) req.AltitudeM = c.Num("altitudeM");
            var route = p.Get("route");
            if (route != null && route.IsArray)
            {
                req.Route = new List<GeoPoint>();
                for (int i = 0; i < route.Count; i++) req.Route.Add(Pt(route[i]));
            }
            if (c.Has("target")) req.Target = ResolveUnit(c.Str("target"), "/target");
            var targets = p.Get("targets");
            if (targets != null && targets.IsArray)
            {
                req.Targets = new List<uint>();
                for (int i = 0; i < targets.Count; i++) req.Targets.Add(ResolveUnit(targets[i].AsString(), "/targets/" + i));
            }
            var pa = p.GetObj("patternA");
            if (pa != null) req.PatternA = Pt(pa);
            var pb = p.GetObj("patternB");
            if (pb != null) req.PatternB = Pt(pb);
            if (c.Has("widthKm")) req.WidthKm = c.Num("widthKm");
            req.TurnRight = c.Bool("turnRight", true);
            if (c.Has("altStackM")) req.AltStackM = c.Num("altStackM");
            if (c.Has("base")) req.Base = ResolveUnit(c.Str("base"), "/base");
            switch (c.Str("weapons"))
            {
                case "free": req.Weapons = WeaponsControl.Free; break;
                case "tight": req.Weapons = WeaponsControl.Tight; break;
                case "hold": req.Weapons = WeaponsControl.Hold; break;
            }
            if (c.Has("protect")) req.Protect = ResolveUnit(c.Str("protect"), "/protect");
            if (c.Has("radiusKm")) req.RadiusKm = c.Num("radiusKm");
            if (c.Has("escortOf")) req.EscortOf = ResolveUnit(c.Str("escortOf"), "/escortOf");
            switch (c.Str("slot", "ahead"))
            {
                case "abreast": req.Slot = EscortSlot.Abreast; break;
                case "astern": req.Slot = EscortSlot.Astern; break;
                default: req.Slot = EscortSlot.Ahead; break;
            }
            if (c.Has("distanceKm")) req.DistanceKm = c.Num("distanceKm");
            req.ExtendRange = c.Bool("extendRange") || type == OrderType.AllAttack;

            var r = e.Issue(req);
            Publish(); // order.accepted / refused reports go out with the reply
            if (!r.Accepted)
            {
                string code = Orders.Wire(r.Code);
                throw new SkizzyError(KindOf(Orders.ErrorKind(r.Code)), r.Message ?? ("order refused: " + code), code.ToLowerInvariant(),
                    r.Summary, JsonValue.Obj().Set("code", code).Set("summary", r.Summary).Set("units", OutcomesJson(r)));
            }
            Commanded();
            var o = r.Order;
            return JsonValue.Obj()
                .Set("order", o.Handle)
                .Set("type", Orders.Wire(o.Type))
                .Set("side", o.Side)
                .Set("status", Orders.Wire(o.Status))
                .Set("accepted", r.AcceptedCount)
                .Set("refused", r.Units.Count - r.AcceptedCount)
                .Set("summary", r.Summary)
                .Set("units", OutcomesJson(r))
                .Set("text", r.Summary ?? (o.Handle + " " + Orders.Wire(o.Type)));
        }

        private JsonValue CancelCap(CallContext c)
        {
            var e = Need();
            string s = c.Str("order");
            if (s.StartsWith("o:", StringComparison.Ordinal)) s = s.Substring(2);
            int id;
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw SkizzyError.InvalidParam("/order", "not an order handle");
            var o = e.GetOrder(id);
            if (o == null) throw SkizzyError.NotFound("o:" + s, "unknown", "No such order (completed orders are forgotten after a while).");
            if (c.Session != null && c.Session.Side != null && o.Side != c.Session.Side && !Privileged(c.Session))
                throw new SkizzyError(ErrorKind.PermissionDenied, "order " + o.Handle + " belongs to side " + o.Side, "not_own_side");
            // The order's own status rolls up from its unit orders at the next tick; until then all-terminal unit orders count.
            string ended = o.IsTerminal ? Orders.Wire(o.Status) : null;
            if (ended == null && o.Units.Count > 0)
            {
                bool all = true;
                for (int i = 0; i < o.Units.Count && all; i++) all = o.Units[i].IsTerminal;
                if (all) ended = Orders.Wire(o.Units[0].Status);
            }
            if (ended != null)
                throw SkizzyError.Precondition("already_" + ended, "order " + o.Handle + " is already " + ended);
            bool ok = e.Cancel(id, By(c)); // unit orders end now; the order status rolls up at the next tick
            Publish();
            Commanded();
            return JsonValue.Obj().Set("order", o.Handle).Set("cancelled", ok).Set("status", ok ? "cancelled" : Orders.Wire(o.Status)).Set("text", o.Handle + (ok ? " cancelled" : " not cancelled"));
        }

        private static DoctrineLevel Level(string s)
        {
            return s == "unit" ? DoctrineLevel.Unit : s == "group" ? DoctrineLevel.Group : DoctrineLevel.Side;
        }

        private string DoctrineTarget(CallContext c, DoctrineLevel level)
        {
            string t = c.Str("target");
            if (level == DoctrineLevel.Side) return ActingSide(c, t, true);
            if (t == null) throw SkizzyError.InvalidParam("/target", "target is required for level " + c.Str("level"));
            if (level == DoctrineLevel.Unit) return Handle(ResolveUnit(t, "/target"));
            return t;
        }

        private static List<string> Strings(JsonValue arr)
        {
            var l = new List<string>();
            if (arr != null && arr.IsArray) for (int i = 0; i < arr.Count; i++) { var s = arr[i].AsString(); if (s != null) l.Add(s); }
            return l;
        }

        private JsonValue DoctrineSet(CallContext c)
        {
            var e = Need();
            var level = Level(c.Str("level"));
            string target = DoctrineTarget(c, level);
            var vals = c.Params.GetObj("values");
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < vals.Count; i++)
            {
                var v = vals.ValueAt(i);
                object o;
                switch (v.Kind)
                {
                    case JsonKind.String: o = v.AsString(); break;
                    case JsonKind.Bool: o = v.AsBool(); break;
                    case JsonKind.Number: o = v.AsDouble(); break;
                    default: throw SkizzyError.InvalidParam("/values/" + vals.KeyAt(i), "doctrine values are strings, numbers or booleans");
                }
                dict[vals.KeyAt(i)] = o;
            }
            var locks = Strings(c.Params.Get("lock"));
            var errors = e.SetDoctrine(level, target, dict, locks.Count > 0 ? locks : null);
            if (errors.Count > 0)
            {
                var arr = JsonValue.ArrOf(errors.ToArray());
                foreach (var err in errors)
                    if (err.StartsWith("locked_by:", StringComparison.Ordinal) || err.Contains(" locked_by:"))
                    {
                        int k = err.IndexOf("locked_by:", StringComparison.Ordinal);
                        string reason = err.Substring(k).Split(' ')[0];
                        throw new SkizzyError(ErrorKind.PreconditionFailed, err, reason, "A higher level locked this key; change it there or unset the lock.", JsonValue.Obj().Set("errors", arr));
                    }
                throw new SkizzyError(ErrorKind.InvalidParams, errors[0], "doctrine", null, JsonValue.Obj().Set("errors", arr));
            }
            Publish();
            Commanded();
            return JsonValue.Obj().Set("level", c.Str("level")).Set("target", target).Set("set", dict.Count).Set("locked", locks.Count)
                .Set("text", "doctrine set at " + c.Str("level") + " " + target);
        }

        private JsonValue DoctrineUnset(CallContext c)
        {
            var e = Need();
            var level = Level(c.Str("level"));
            string target = DoctrineTarget(c, level);
            var keys = Strings(c.Params.Get("keys"));
            e.UnsetDoctrine(level, target, keys);
            Publish();
            Commanded();
            return JsonValue.Obj().Set("level", c.Str("level")).Set("target", target).Set("unset", keys.Count)
                .Set("text", "doctrine unset at " + c.Str("level") + " " + target + ": " + string.Join(", ", keys.ToArray()));
        }

        private JsonValue DoctrineEffective(CallContext c)
        {
            var e = Need();
            uint id = ResolveUnit(c.Str("unit"), "/unit");
            var lines = e.ExplainDoctrine(id);
            if (lines == null) throw SkizzyError.NotFound(Handle(id), "unknown", "The C2 engine does not know this unit yet (it appears after the next tick if it has a BDArmory AI or weapon manager).");
            var arr = JsonValue.Arr();
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                arr.Add(JsonValue.Obj().Set("key", l.Key).Set("value", l.Value).Set("from", l.From).Set("locked", l.Locked));
                sb.Append(l.Key).Append('=').Append(l.Value).Append(" (").Append(l.From).Append(l.Locked ? ", locked" : "").Append(")\n");
            }
            return JsonValue.Obj().Set("unit", Handle(id)).Set("doctrine", arr).Set("text", sb.ToString());
        }

        private static JsonValue UnitOrderJson(UnitOrder u)
        {
            if (u == null) return JsonValue.Null;
            return JsonValue.Obj()
                .Set("order", u.Parent != null ? u.Parent.Handle : null)
                .Set("type", Orders.Wire(u.Type))
                .Set("status", Orders.Wire(u.Status))
                .Set("reason", u.Reason)
                .Set("phase", u.Phase);
        }

        private static string Lower(object o) { return o.ToString().ToLowerInvariant(); }

        private JsonValue UnitStatus(CallContext c)
        {
            var e = Need();
            uint id = ResolveUnit(c.Str("unit"), "/unit");
            var v = e.GetUnitView(id);
            if (v == null) throw SkizzyError.NotFound(Handle(id), "unknown", "The C2 engine does not know this unit yet (it appears after the next tick if it has a BDArmory AI or weapon manager).");
            var route = JsonValue.Arr();
            foreach (var pt in v.RoutePoints) route.Add(JsonValue.Obj().Set("lat", Math.Round(pt.Lat, 5)).Set("lon", Math.Round(pt.Lon, 5)));
            var sec = JsonValue.Arr();
            foreach (var s in v.Secondary) sec.Add(Handle(s));
            var allows = JsonValue.Arr();
            foreach (var s in v.FilterAllows) allows.Add(Handle(s));
            var o = JsonValue.Obj()
                .Set("unit", Handle(v.Id)).Set("name", v.Name).Set("side", v.Side).Set("alive", v.Alive)
                .Set("task", v.Task)
                .Set("movement", UnitOrderJson(v.Movement)).Set("movementSuspended", v.MovementSuspended)
                .Set("engagement", UnitOrderJson(v.Engagement))
                .Set("hold", v.Hold).Set("marshalSlot", v.MarshalSlot).SetFinite("holdAltM", v.HoldAltM)
                .Set("fireAuthority", Lower(v.FireAuthority)).Set("filterInstalled", v.FilterInstalled).Set("filterAllows", allows)
                .Set("overrideTarget", v.OverrideTarget != 0 ? Handle(v.OverrideTarget) : null).Set("overrideSticky", v.OverrideSticky)
                .Set("secondary", sec)
                .Set("route", v.Route).Set("routeId", v.RouteId).Set("routePoints", route)
                .Set("yieldToCombat", v.YieldToCombat).Set("leashM", v.LeashM)
                .Set("divert", v.Divert != 0 ? Handle(v.Divert) : null)
                .Set("selfDefence", v.SelfDefence != 0 ? Handle(v.SelfDefence) : null)
                .Set("advance", v.Advance != 0 ? Handle(v.Advance) : null)
                .Set("vipThreat", v.VipThreat != 0 ? Handle(v.VipThreat) : null)
                .Set("vip", v.Vip).Set("rangeExtended", v.RangeExtended)
                .Set("weaponsControl", Lower(v.WeaponsControl)).Set("onContact", Lower(v.OnContact));
            o.Set("text", v.Name + ": " + (v.Task ?? "idle") + (v.Movement != null ? ", " + Orders.Wire(v.Movement.Type) + " " + Orders.Wire(v.Movement.Status) : "") +
                          (v.Engagement != null ? ", " + Orders.Wire(v.Engagement.Type) + " " + Orders.Wire(v.Engagement.Status) : "") +
                          ", weapons " + Lower(v.WeaponsControl));
            return o;
        }

        private JsonValue ReleaseCap(CallContext c)
        {
            var e = Need();
            var units = c.Params.Get("units");
            var arr = JsonValue.Arr();
            int released = 0;
            for (int i = 0; i < units.Count; i++)
            {
                uint id = ResolveUnit(units[i].AsString(), "/units/" + i);
                bool ok = e.Release(id, By(c));
                if (ok) released++;
                arr.Add(JsonValue.Obj().Set("unit", Handle(id)).Set("released", ok));
            }
            Publish();
            if (released > 0) Commanded();
            return JsonValue.Obj().Set("released", released).Set("units", arr).Set("text", released + " unit(s) released to BDArmory guard behaviour");
        }

        private JsonValue VipSet(CallContext c)
        {
            var e = Need();
            uint id = ResolveUnit(c.Str("unit"), "/unit");
            bool vip = c.Bool("vip", true);
            if (!e.SetVip(id, vip))
                throw SkizzyError.NotFound(Handle(id), "unknown", "The C2 engine does not know this unit yet (it appears after the next tick if it has a BDArmory AI or weapon manager).");
            Publish();
            Commanded();
            return JsonValue.Obj().Set("unit", Handle(id)).Set("vip", e.IsVip(id)).Set("text", Handle(id) + (vip ? " flagged VIP" : " VIP flag cleared"));
        }

        private JsonValue SideManaged(CallContext c)
        {
            var e = Need();
            string side = ActingSide(c, c.Str("side"), true);
            if (c.Has("managed"))
            {
                bool m = c.Bool("managed");
                e.SetSideManaged(side, m);
                if (m) _managedSides.Add(side); else _managedSides.Remove(side);
                Publish();
                Commanded();
            }
            bool now = e.IsSideManaged(side);
            return JsonValue.Obj().Set("side", side).Set("managed", now).Set("text", "side " + side + (now ? " is managed by Skizzy" : " runs plain BDArmory behaviour"));
        }
    }
}
