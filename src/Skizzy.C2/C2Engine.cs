using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Skizzy.C2
{
    /// <summary>A read-only view of one unit's C2 state (c2.unit.status, tests, the in-game window).</summary>
    public sealed class UnitView
    {
        public uint Id;
        public string Name;
        public string Side;
        public bool Alive;
        public UnitOrder Movement;
        public UnitOrder Engagement;
        public bool MovementSuspended;
        /// <summary>null, "point", "marshal" or "home".</summary>
        public string Hold;
        public int MarshalSlot;
        public double HoldAltM;
        /// <summary>What drives the unit now: an order type, "hold", "advance", "vip_priority" or "guard".</summary>
        public string Task;
        public FireAuthorityMode FireAuthority;
        public bool FilterInstalled;
        public uint OverrideTarget;
        public bool OverrideSticky;
        /// <summary>none | waypoints | flyto.</summary>
        public string Route;
        public int RouteId;
        public readonly List<GeoPoint> RoutePoints = new List<GeoPoint>();
        public bool YieldToCombat;
        public float LeashM;
        public uint Divert;
        public uint SelfDefence;
        public uint Advance;
        public uint VipThreat;
        public bool Vip;
        public bool RangeExtended;
        public readonly List<uint> Secondary = new List<uint>();
        public readonly List<uint> FilterAllows = new List<uint>();
        public WeaponsControl WeaponsControl;
        public OnContact OnContact;
    }

    /// <summary>
    /// The C2 orders and doctrine engine (24-ksp-parity.md Phase v1). Turns orders and doctrine into BDArmory
    /// Overdrive hook calls through <see cref="IBdaControl"/>, reading the game through <see cref="IC2World"/>.
    /// <para>
    /// Call <see cref="Tick"/> from the C2 engine slot of the host's FixedUpdate (5 Hz, 02 §2.2). Hook events are
    /// queued by the <see cref="IHookEvents"/> methods and handled at the start of the next tick. Main thread only.
    /// </para>
    /// </summary>
    public sealed partial class C2Engine : IHookEvents
    {
        private readonly IC2World _world;
        private readonly IBdaControl _ctl;
        private Geo _geo;
        private readonly DoctrineStore _doctrine = new DoctrineStore();

        private readonly Dictionary<uint, UnitRec> _units = new Dictionary<uint, UnitRec>();
        private readonly List<UnitRec> _unitList = new List<UnitRec>();
        private readonly Dictionary<string, SidePicture> _sides = new Dictionary<string, SidePicture>(StringComparer.Ordinal);
        private readonly List<SidePicture> _sideList = new List<SidePicture>();
        private readonly Dictionary<string, List<uint>> _groups = new Dictionary<string, List<uint>>(StringComparer.Ordinal);

        private readonly Dictionary<int, Order> _orders = new Dictionary<int, Order>();
        private readonly List<Order> _active = new List<Order>();
        private readonly List<C2Event> _events = new List<C2Event>();
        private readonly List<HookEvt> _queue = new List<HookEvt>();
        private readonly List<HookEvt> _queueWork = new List<HookEvt>();
        private readonly List<UnitState> _states = new List<UnitState>();
        private readonly List<uint> _ids = new List<uint>();
        private readonly List<uint> _ids2 = new List<uint>();
        private readonly List<GeoPoint> _ptScratch = new List<GeoPoint>();
        private readonly Dictionary<uint, int> _marshalSeq = new Dictionary<uint, int>();

        private HookSet _hooks = HookSet.All;
        private int _nextOrderId = 1;
        private int _routeSerial;
        private double _now;
        private bool _ticked;
        private int _tickNo;

        /// <summary>Seconds a target may be out of view before an engage order on it fails with target_lost.</summary>
        public double TargetLostS = 30;

        /// <summary>Back-off before re-assigning a target that BDA dropped as not engageable (non-sticky F1).</summary>
        public double NotEngageableBackoffS = 2;

        /// <summary>Back-off after a human or another mod replaced the assignment in game (stock SetOverrideTarget).</summary>
        public double ReplacedBackoffS = 10;

        /// <summary>F6b <c>patrolLeashCooldown</c>: a leash-broken target is not re-assigned for this long.</summary>
        public double LeashBackoffS = 30;

        /// <summary>Consecutive failed BDA command attempts (AI inactive) before an order fails with ai_inactive.</summary>
        public int AiFailureLimit = 3;

        /// <summary>Completed orders kept for queries.</summary>
        public int CompletedOrderMemory = 256;

        public C2Engine(IC2World world, IBdaControl control)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _ctl = control ?? throw new ArgumentNullException(nameof(control));
            _geo = new Geo(world.BodyRadius);
        }

        public DoctrineStore Doctrine => _doctrine;
        public Geo Geo => _geo;
        public double Now => _now;

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Puts a side under Skizzy control: behaviours (advance on contact, VIP priority, missile engagement, ship SAM
        /// priority) run for its units even without orders. Orders work for any side regardless; a side with orders
        /// but not managed keeps plain BDA behaviour for its other units.
        /// </summary>
        public void SetSideManaged(string side, bool managed)
        {
            Side(side).Managed = managed;
        }

        public bool IsSideManaged(string side)
        {
            SidePicture p;
            return side != null && _sides.TryGetValue(side, out p) && p.Managed;
        }

        /// <summary>Marks or clears a VIP (side-level flag feeding WTA priority, row 13).</summary>
        public bool SetVip(uint unit, bool vip)
        {
            var r = Rec(unit);
            if (r == null || r.S.Side == null)
            {
                _now = _world.Now;
                RefreshUnits();
                r = Rec(unit);
            }
            if (r == null || r.S.Side == null) return false;
            var p = Side(r.S.Side);
            if (vip) p.Vips.Add(unit); else p.Vips.Remove(unit);
            Emit(C2EventKind.Info, "orders", r.S.Side, 0, unit, null,
                vip ? r.Name + " marked VIP. Fighters, drones and ships now give threats to it priority." : r.Name + ": VIP protection removed.");
            return true;
        }

        public bool IsVip(uint unit)
        {
            var r = Rec(unit);
            SidePicture p;
            return r != null && r.S.Side != null && _sides.TryGetValue(r.S.Side, out p) && p.Vips.Contains(unit);
        }

        /// <summary>Sets a unit's C2 group (one group per unit; groups also carry doctrine). Null removes it.</summary>
        public void SetGroup(uint unit, string group)
        {
            var r = RecOrCreate(unit);
            if (r.Group != null && _groups.TryGetValue(r.Group, out var old)) old.Remove(unit);
            r.Group = group;
            if (group != null)
            {
                if (!_groups.TryGetValue(group, out var list)) _groups[group] = list = new List<uint>();
                if (!list.Contains(unit)) list.Add(unit);
            }
            r.DocVersion = -1;
        }

        public string GroupOf(uint unit)
        {
            var r = Rec(unit);
            return r?.Group;
        }

        public IList<uint> GroupMembers(string group)
        {
            List<uint> l;
            return group != null && _groups.TryGetValue(group, out l) ? l : (IList<uint>)Array.Empty<uint>();
        }

        /// <summary>
        /// Sets doctrine at side, group or unit level (09 §11.1). Returns errors (unknown key, bad value,
        /// <c>locked_by:side|group</c>); nothing changes on error.
        /// </summary>
        public List<string> SetDoctrine(DoctrineLevel level, string target, IDictionary<string, object> values, IList<string> lockKeys = null)
        {
            string side = null, group = null;
            uint unit = 0;
            switch (level)
            {
                case DoctrineLevel.Side:
                    side = target;
                    break;
                case DoctrineLevel.Group:
                    group = target;
                    side = SideOfGroup(target);
                    break;
                default:
                    if (!TryParseUnit(target, out unit)) return new List<string> { "unit target must be a vessel id or v:<id>" };
                    var r = Rec(unit);
                    if (r != null) { side = r.S.Side; group = r.Group; }
                    break;
            }
            var errors = _doctrine.Set(level, side, group, unit, values, lockKeys);
            if (errors.Count == 0)
                Emit(C2EventKind.Info, "c2", side, 0, unit, null, "Doctrine changed at " + LevelName(level) + " " + target + ".");
            return errors;
        }

        public void UnsetDoctrine(DoctrineLevel level, string target, IList<string> keys)
        {
            string side = null, group = null;
            uint unit = 0;
            if (level == DoctrineLevel.Side) side = target;
            else if (level == DoctrineLevel.Group) group = target;
            else if (!TryParseUnit(target, out unit)) return;
            _doctrine.Unset(level, side, group, unit, keys);
        }

        /// <summary>The effective doctrine of a unit (cached; refreshed at each tick).</summary>
        public DoctrineValues EffectiveDoctrine(uint unit)
        {
            var r = Rec(unit);
            if (r == null) return null;
            ResolveDoctrine(r, true);
            return r.Doc;
        }

        public List<DoctrineExplain> ExplainDoctrine(uint unit)
        {
            var r = RecNow(unit);
            return r == null ? null : _doctrine.Explain(r.S.Side, r.Group, unit);
        }

        /// <summary>Cancels an order (all its unit orders). An engagement order gives the unit back its movement order.</summary>
        public bool Cancel(int orderId, string by = null)
        {
            Order o;
            if (!_orders.TryGetValue(orderId, out o) || o.IsTerminal) return false;
            for (int i = 0; i < o.Units.Count; i++)
            {
                var uo = o.Units[i];
                if (uo.IsTerminal) continue;
                var r = Rec(uo.Unit);
                EndUnitOrder(r, uo, OrderStatus.Cancelled, Reasons.Cancelled,
                    (r != null ? r.Name : "unit") + ": " + Orders.Wire(uo.Type) + " cancelled" + (by != null ? " by " + by : "") + ".", true);
            }
            return true;
        }

        /// <summary>
        /// Returns a unit to plain BDA guard behaviour: cancels its orders (with reports) and clears any hold.
        /// Doctrine still applies (a unit on weapons Hold stays on Hold).
        /// </summary>
        public bool Release(uint unit, string by = null)
        {
            var r = Rec(unit);
            if (r == null) return false;
            string who = by != null ? " by " + by : "";
            if (r.Engage != null) EndUnitOrder(r, r.Engage, OrderStatus.Cancelled, Reasons.Released, r.Name + ": released" + who + ".", false);
            if (r.Move != null) EndUnitOrder(r, r.Move, OrderStatus.Cancelled, Reasons.Released, r.Name + ": released" + who + ".", false);
            r.Hold = null;
            return true;
        }

        public Order GetOrder(int id)
        {
            Order o;
            return _orders.TryGetValue(id, out o) ? o : null;
        }

        /// <summary>Orders still executing (or with executing unit orders).</summary>
        public IList<Order> ActiveOrders => _active;

        /// <summary>
        /// Sees every report as it is emitted, without draining it (the in-game UIs, through <see cref="C2Runtime"/>, while
        /// the host drains and publishes). Called synchronously inside engine calls: it must only record the report.
        /// </summary>
        public Action<C2Event> ReportObserver;

        /// <summary>Moves pending reports into <paramref name="into"/>.</summary>
        public void DrainEvents(List<C2Event> into)
        {
            into.AddRange(_events);
            _events.Clear();
        }

        public SidePicture Picture(string side)
        {
            SidePicture p;
            return side != null && _sides.TryGetValue(side, out p) ? p : null;
        }

        /// <summary>
        /// Adds a line per known unit (missiles only with <paramref name="missiles"/>), optionally of one side, in the
        /// order the binding first reported them. Main thread; no per-unit allocation.
        /// </summary>
        public void ListUnits(List<UnitBrief> into, string side = null, bool missiles = false)
        {
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (r.S.IsMissile && !missiles) continue;
                if (side != null && r.S.Side != side) continue;
                ResolveDoctrine(r, false);
                into.Add(new UnitBrief
                {
                    Id = r.Id,
                    Name = r.Name,
                    Side = r.S.Side,
                    Class = r.S.Class,
                    Group = r.S.Group,
                    C2Group = r.Group,
                    Alive = r.Alive,
                    Present = r.Present,
                    AiActive = r.S.AiActive,
                    HasWeaponManager = r.S.HasWeaponManager,
                    Landed = r.S.Landed,
                    IsBase = r.S.IsBase,
                    MissilesReady = r.S.MissilesReady,
                    MissilesMax = r.S.MissilesMax,
                    Task = TaskName(r),
                    Order = OrderIdOf(r),
                    WeaponsControl = r.Doc.WeaponsControl,
                    FireAuthority = r.Have.Fire,
                });
            }
        }

        /// <summary>Adds every side (BDA team) the engine has seen, in first-seen order.</summary>
        public void ListSides(List<string> into)
        {
            for (int i = 0; i < _sideList.Count; i++)
                if (!string.IsNullOrEmpty(_sideList[i].Side)) into.Add(_sideList[i].Side);
        }

        /// <summary>The side-level doctrine in force (defaults overlaid with the side layer). A fresh copy.</summary>
        public DoctrineValues SideDoctrine(string side)
        {
            return _doctrine.Resolve(side, null, 0);
        }

        /// <summary>Which Overdrive hooks the binding reports (the engine refuses orders that need missing ones).</summary>
        public HookSet Hooks => _world.Hooks;

        public UnitView GetUnitView(uint unit)
        {
            var r = RecNow(unit);
            if (r == null) return null;
            var v = new UnitView
            {
                Id = r.Id,
                Name = r.Name,
                Side = r.S.Side,
                Alive = r.Alive,
                Movement = r.Move,
                Engagement = r.Engage,
                MovementSuspended = r.Move != null && r.Move.Status == OrderStatus.Suspended,
                Hold = r.Hold == null ? null : r.Hold.Kind == HoldKind.Point ? "point" : r.Hold.Kind == HoldKind.Marshal ? "marshal" : "home",
                MarshalSlot = r.Hold != null ? r.Hold.Slot : 0,
                HoldAltM = r.Hold != null ? r.Hold.Alt : double.NaN,
                Task = TaskName(r),
                FireAuthority = r.Have.Fire,
                FilterInstalled = r.Have.Filter,
                OverrideTarget = r.Have.Override,
                OverrideSticky = r.Have.Sticky,
                Route = r.Have.Route == RouteKind.None ? "none" : r.Have.Route == RouteKind.Waypoints ? "waypoints" : "flyto",
                RouteId = r.Have.RouteId,
                YieldToCombat = r.Have.Yield,
                LeashM = r.Have.Leash,
                Divert = r.Divert,
                SelfDefence = r.SelfDef,
                Advance = r.Advance,
                VipThreat = r.VipThreat,
                Vip = IsVip(unit),
                RangeExtended = r.Have.RangeMax,
                WeaponsControl = r.Doc.WeaponsControl,
                OnContact = r.Doc.OnContact,
            };
            if (r.Have.Route != RouteKind.None) v.RoutePoints.AddRange(r.RoutePoints);
            v.Secondary.AddRange(r.Have.Secondary);
            if (r.Filter != null && r.Have.Filter) r.Filter.CopyTo(v.FilterAllows);
            return v;
        }

        // ------------------------------------------------------------------ IHookEvents (queued)

        public void OnOverrideLost(uint unit, uint target, OverrideLostReason reason) { Enqueue(new HookEvt { Kind = HookEvtKind.OverrideLost, Unit = unit, Target = target, Reason = reason }); }
        public void OnUnitDestroyed(uint unit) { Enqueue(new HookEvt { Kind = HookEvtKind.UnitDestroyed, Unit = unit }); }
        public void OnTargetDetected(uint target, string side) { Enqueue(new HookEvt { Kind = HookEvtKind.TargetDetected, Target = target, Side = side }); }
        public void OnTargetLost(uint target, string side) { Enqueue(new HookEvt { Kind = HookEvtKind.TargetLost, Target = target, Side = side }); }
        public void OnWaypointReached(uint unit, int index, int lap) { Enqueue(new HookEvt { Kind = HookEvtKind.WaypointReached, Unit = unit, Index = index, Lap = lap }); }
        public void OnRouteCompleted(uint unit) { Enqueue(new HookEvt { Kind = HookEvtKind.RouteCompleted, Unit = unit }); }
        public void OnPatrolEngage(uint unit, uint target) { Enqueue(new HookEvt { Kind = HookEvtKind.PatrolEngage, Unit = unit, Target = target }); }
        public void OnPatrolResume(uint unit, bool leashBroke) { Enqueue(new HookEvt { Kind = HookEvtKind.PatrolResume, Unit = unit, Flag = leashBroke }); }
        public void OnMissileFired(uint shooter, uint missile, uint target) { Enqueue(new HookEvt { Kind = HookEvtKind.MissileFired, Unit = shooter, Target = target, Index = (int)missile }); }

        private void Enqueue(HookEvt e)
        {
            if (_queue.Count < 4096) _queue.Add(e);
        }

        // ------------------------------------------------------------------ tick

        /// <summary>One C2 tick: read the world, handle hook events, run every unit's order and behaviours, write hook changes.</summary>
        public void Tick()
        {
            _now = _world.Now;
            _hooks = _world.Hooks;
            if (!_ticked)
            {
                _geo = new Geo(_world.BodyRadius);
                _ticked = true;
            }
            _tickNo++;
            if (_hostileN > 0) Array.Clear(_hostile, 0, _hostile.Length);
            long t0 = Profile ? Stopwatch.GetTimestamp() : 0;
            RefreshUnits();
            ProcessHookEvents();
            RefreshDoctrine();
            long t1 = Profile ? Stopwatch.GetTimestamp() : 0;
            BuildPictures();
            RebuildClaims();
            long t2 = Profile ? Stopwatch.GetTimestamp() : 0;
            long step = 0, apply = 0;
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (r.S.IsMissile) continue;
                if (!r.Alive)
                {
                    if (r.Dead && (r.Move != null || r.Engage != null)) HandleDeath(r);
                    continue;
                }
                bool managed = IsSideManaged(r.S.Side) || r.Move != null || r.Engage != null || r.Hold != null ||
                               r.Doc.WeaponsControl != WeaponsControl.Free;
                if (!managed && r.Have.IsStock && r.Have.Route == RouteKind.None) { r.Managed = false; continue; }
                r.Managed = managed;
                long a = Profile ? Stopwatch.GetTimestamp() : 0;
                StepUnit(r);
                long b = Profile ? Stopwatch.GetTimestamp() : 0;
                Apply(r);
                if (Profile) { step += b - a; apply += Stopwatch.GetTimestamp() - b; }
            }
            FinishOrders();
            if (Profile)
            {
                double f = 1000.0 / Stopwatch.Frequency;
                PhaseMs[0] += (t1 - t0) * f;
                PhaseMs[1] += (t2 - t1) * f;
                PhaseMs[2] += step * f;
                PhaseMs[3] += apply * f;
                PhaseMs[4] += (Stopwatch.GetTimestamp() - t0) * f;
            }
        }

        /// <summary>Accumulate per-phase tick time in <see cref="PhaseMs"/>: refresh, picture, step, apply, total (host.stats).</summary>
        public bool Profile;
        public readonly double[] PhaseMs = new double[5];

        private void RefreshUnits()
        {
            for (int i = 0; i < _unitList.Count; i++) _unitList[i].Present = false;
            _states.Clear();
            _world.GetUnits(_states);
            for (int i = 0; i < _states.Count; i++)
            {
                var s = _states[i];
                if (s == null || s.Id == 0) continue;
                var r = RecOrCreate(s.Id);
                string oldSide = r.S.Side;
                r.S.CopyFrom(s);
                r.P = s.Side != null ? Side(s.Side) : null;
                SetVector(r);
                r.Present = true;
                if (s.Alive) r.LastSeenUt = _now;
                else if (!r.Dead) MarkDead(r);
                if (oldSide != s.Side)
                {
                    r.DocVersion = -1;
                    if (oldSide != null && !r.Dead) SideChanged(r, oldSide);
                }
                if (s.Side != null) Side(s.Side);
            }
        }

        /// <summary>
        /// A unit changed BDA team: its orders, hold and behaviours belong to the old side, so they end (reported), and
        /// the old side's VIP flag goes. The new side commands it from scratch.
        /// </summary>
        private void SideChanged(UnitRec r, string oldSide)
        {
            string text = r.Name + ": changed team from " + oldSide + " to " + (r.S.Side ?? "none") + "; its orders ended.";
            if (r.Engage != null) EndUnitOrder(r, r.Engage, OrderStatus.Cancelled, Reasons.TeamChanged, text, false);
            if (r.Move != null) EndUnitOrder(r, r.Move, OrderStatus.Cancelled, Reasons.TeamChanged, text, false);
            r.Hold = null;
            r.Divert = 0;
            r.SelfDef = 0;
            r.Advance = 0;
            r.VipThreat = 0;
            r.Claim = 0;
            SidePicture old;
            if (_sides.TryGetValue(oldSide, out old)) old.Vips.Remove(r.Id);
            Emit(C2EventKind.Info, "orders", oldSide, 0, r.Id, Reasons.TeamChanged, text);
        }

        private void ProcessHookEvents()
        {
            if (_queue.Count == 0) return;
            _queueWork.Clear();
            _queueWork.AddRange(_queue);
            _queue.Clear();
            for (int i = 0; i < _queueWork.Count; i++)
            {
                var e = _queueWork[i];
                switch (e.Kind)
                {
                    case HookEvtKind.UnitDestroyed:
                    {
                        var r = Rec(e.Unit);
                        if (r != null && !r.Dead) MarkDead(r);
                        break;
                    }
                    case HookEvtKind.OverrideLost:
                    {
                        var r = Rec(e.Unit);
                        if (r == null || r.Have.Override == 0 || (e.Target != 0 && r.Have.Override != e.Target)) break; // stale: we already moved on
                        uint lost = r.Have.Override;
                        r.Have.Override = 0;
                        r.Have.Sticky = false;
                        switch (e.Reason)
                        {
                            case OverrideLostReason.NotEngageable:
                                r.BackoffTarget = lost;
                                r.BackoffUntil = _now + NotEngageableBackoffS;
                                break;
                            case OverrideLostReason.Replaced:
                                r.BackoffTarget = lost;
                                r.BackoffUntil = _now + ReplacedBackoffS;
                                Emit(C2EventKind.Info, "orders", r.S.Side, OrderIdOf(r), r.Id, "assignment_replaced",
                                    r.Name + ": target assignment replaced in game; Skizzy re-asserts it in " + ReplacedBackoffS.ToString("0", CultureInfo.InvariantCulture) + " s if the order still stands.");
                                break;
                            case OverrideLostReason.Cleared:
                                if (r.PatrolTarget == lost && !r.PatrolEngaged)
                                {
                                    r.BackoffTarget = lost;
                                    r.BackoffUntil = _now + LeashBackoffS;
                                }
                                break;
                            case OverrideLostReason.TargetDestroyed:
                                // F10 death may arrive later; do not re-assign a wreck meanwhile
                                r.BackoffTarget = lost;
                                r.BackoffUntil = _now + TargetLostS;
                                break;
                            case OverrideLostReason.Filtered:
                            case OverrideLostReason.NotHostile:
                                // NotHostile: a team change on either side; the picture stops counting it as hostile
                                r.BackoffTarget = lost;
                                r.BackoffUntil = _now + NotEngageableBackoffS;
                                break;
                        }
                        break;
                    }
                    case HookEvtKind.RouteCompleted:
                    {
                        var r = Rec(e.Unit);
                        if (r == null || r.Have.Route != RouteKind.Waypoints) break;
                        if (r.Have.RouteId == r.RouteId) r.RouteCompleted = true;
                        r.CompletedRouteId = r.Have.RouteId;
                        r.Have.Route = RouteKind.None;
                        r.Have.RouteId = 0;
                        break;
                    }
                    case HookEvtKind.WaypointReached:
                    {
                        var r = Rec(e.Unit);
                        if (r == null || r.Have.Route != RouteKind.Waypoints || r.Have.RouteId != r.RouteId) break;
                        r.WaypointIndex = e.Index;
                        r.WaypointLap = e.Lap;
                        var uo = r.RouteOwner as UnitOrder;
                        if (uo != null && uo.Type == OrderType.MoveRoute && !uo.IsTerminal && uo.Path != null)
                        {
                            int next = uo.FedStart + e.Index + 1;
                            if (next > uo.NextIndex)
                            {
                                uo.NextIndex = next;
                                if (next < uo.Path.Count)
                                    Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "waypoint",
                                        r.Name + ": waypoint " + next + " of " + uo.Path.Count + ".");
                            }
                        }
                        break;
                    }
                    case HookEvtKind.TargetDetected:
                    {
                        // a new detection: engage-on-contact units of that side look again at once
                        for (int k = 0; k < _unitList.Count; k++)
                        {
                            var r = _unitList[k];
                            if (r.S.Side == e.Side) r.NextContactPick = 0;
                        }
                        break;
                    }
                    case HookEvtKind.PatrolEngage:
                    {
                        var r = Rec(e.Unit);
                        if (r == null) break;
                        bool first = !r.PatrolEngaged;
                        r.PatrolEngaged = true;
                        r.PatrolTarget = e.Target;
                        if (first && r.Move != null && r.Move.Type == OrderType.HoldPattern && r.Engage == null)
                        {
                            var t = Rec(e.Target);
                            Emit(C2EventKind.Diversion, "orders", r.S.Side, r.Move.Parent.Id, r.Id, null,
                                r.Name + ": leaving the patrol to engage " + (t != null ? t.Name : "a contact") + (t != null ? ", " + Km(r, t) + " km" : "") + ".");
                        }
                        break;
                    }
                    case HookEvtKind.PatrolResume:
                    {
                        var r = Rec(e.Unit);
                        if (r == null) break;
                        bool was = r.PatrolEngaged;
                        r.PatrolEngaged = false;
                        if (e.Flag && r.PatrolTarget != 0)
                        {
                            r.BackoffTarget = r.PatrolTarget;
                            r.BackoffUntil = _now + LeashBackoffS;
                        }
                        if (was && r.Move != null && r.Move.Type == OrderType.HoldPattern && r.Engage == null)
                            Emit(C2EventKind.BackOnTask, "orders", r.S.Side, r.Move.Parent.Id, r.Id, e.Flag ? "leash" : null,
                                r.Name + (e.Flag ? ": leash reached, breaking off and resuming the patrol." : ": resuming the patrol."));
                        break;
                    }
                    case HookEvtKind.MissileFired:
                        OnFired(e.Unit, (uint)e.Index, e.Target);
                        break;
                }
            }
        }

        private void OnFired(uint shooter, uint missile, uint target)
        {
            var s = Rec(shooter);
            if (s == null || s.S.Side == null) return;
            var p = Side(s.S.Side);
            if (p.FirstLaunchSeen) return;
            p.FirstLaunchSeen = true;
            var t = Rec(target);
            string text = "First " + s.S.Side + " launch: " + s.Name + " fired at " + (t != null ? t.Name + ", " + Km(s, t) + " km" : "an unknown target") + ".";
            var e = Emit(C2EventKind.FirstLaunch, "weapons", s.S.Side, 0, shooter, null, text);
            ResolveDoctrine(s, false);
            e.PauseRequested = s.Doc.PauseOnFirstLaunch;
        }

        private void RefreshDoctrine()
        {
            int v = _doctrine.Version;
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (r.DocVersion != v || r.DocGroup != r.Group || r.DocSide != r.S.Side) ResolveDoctrine(r, true);
            }
        }

        private void ResolveDoctrine(UnitRec r, bool force)
        {
            int v = _doctrine.Version;
            if (!force && r.DocVersion == v && r.DocGroup == r.Group && r.DocSide == r.S.Side) return;
            _doctrine.Resolve(r.S.Side, r.Group, r.Id, r.Doc);
            r.DocVersion = v;
            r.DocGroup = r.Group;
            r.DocSide = r.S.Side;
        }

        // ------------------------------------------------------------------ picture

        private void BuildPictures()
        {
            for (int s = 0; s < _sideList.Count; s++)
            {
                var p = _sideList[s];
                p.Hostiles.Clear();
                p.HostileMissiles.Clear();
                p.HostileRecs.Clear();
                p.MissileRecs.Clear();
                p.InView.Clear();
                p.Holders.Clear();
                p.SoleHolder.Clear();
                p.SurveillanceHeld.Clear();
                p.Active = p.Managed;
            }
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (r.Alive && r.P != null && (r.Managed || r.Move != null || r.Engage != null || r.Hold != null)) r.P.Active = true;
            }
            int sides = _sideList.Count;
            int slices = ContactSlices < 1 ? 1 : ContactSlices;
            // own-sensor contacts per unit (F5), identification (row 24), holders (row 7): time-sliced, counted on the
            // target record incrementally (a unit's holds are removed and re-added only when its slice is due)
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (!r.Alive || r.S.IsMissile || r.P == null || !r.P.Active)
                {
                    if (r.ContribSide >= 0) Uncount(r);
                    r.Contacts.Clear();
                    r.ContactsDue = 0;
                    continue;
                }
                var p = r.P;
                int si = p.Index;
                if (r.ContribSide >= 0 && r.ContribSide != si) { Uncount(r); r.ContactsDue = 0; } // changed side
                if (r.ContactsDue > _tickNo) continue;
                int phase = (int)(r.Id % (uint)slices);
                r.ContactsDue = _tickNo + 1 + ((phase - (_tickNo + 1) % slices) % slices + slices) % slices;
                Uncount(r);
                r.Contacts.Clear();
                _world.GetOwnContacts(r.Id, r.Contacts);
                double classRange = r.Doc.ClassificationRangeM(r.S.Class);
                bool surveillance = UnitClasses.IsSurveillance(r.S.Class);
                r.ContribSide = si;
                r.ContribSurv = surveillance;
                for (int c = 0; c < r.Contacts.Count; c++)
                {
                    var t = Rec(r.Contacts[c]);
                    if (t == null || !t.Alive || t.P == null || !HostileSides(p, t.P)) continue;
                    if (t.CountedBy == r.Id && t.CountedTick == _tickNo) continue; // reported twice (radar and IRST)
                    t.CountedBy = r.Id;
                    t.CountedTick = _tickNo;
                    if (t.HeldN == null || t.HeldN.Length < sides)
                    {
                        int n = sides < 2 ? 2 : sides;
                        Array.Resize(ref t.HeldN, n);
                        Array.Resize(ref t.HeldSum, n);
                        Array.Resize(ref t.HeldSurvN, n);
                    }
                    t.HeldN[si]++;
                    t.HeldSum[si] += r.Id;
                    if (surveillance) t.HeldSurvN[si]++;
                    r.Contrib.Add(t);
                    if (!t.S.IsMissile && classRange > 0 && !p.Identified.Contains(t.Id) && Dist(r, t) <= classRange)
                    {
                        p.Identified.Add(t.Id);
                        OnIdentified(p, r, t);
                    }
                }
            }
            // in view: an own sensor or the side's BDA database
            for (int s = 0; s < _sideList.Count; s++)
            {
                var p = _sideList[s];
                if (!p.Active) continue;
                int si = p.Index;
                for (int i = 0; i < _unitList.Count; i++)
                {
                    var t = _unitList[i];
                    if (!t.Alive || t.P == null || !HostileSides(p, t.P)) continue;
                    bool held = t.HeldN != null && t.HeldN.Length > si && t.HeldN[si] > 0;
                    if (held)
                    {
                        p.Holders[t.Id] = t.HeldN[si];
                        if (t.HeldN[si] == 1) p.SoleHolder[t.Id] = (uint)t.HeldSum[si];
                        if (t.HeldSurvN[si] > 0) p.SurveillanceHeld.Add(t.Id);
                    }
                    else if (!_world.SideDetects(p.Side, t.Id)) continue;
                    p.InView.Add(t.Id);
                    p.LastInView[t.Id] = _now;
                    if (t.S.IsMissile) { p.HostileMissiles.Add(t.Id); p.MissileRecs.Add(t); }
                    else { p.Hostiles.Add(t.Id); p.HostileRecs.Add(t); }
                }
                // own-sensor-only contacts (row 7): log once per track
                foreach (var kv in p.SoleHolder)
                {
                    if (!p.IsOwnSensorOnly(kv.Key) || p.OwnOnlyReported.Contains(kv.Key)) continue;
                    var t = Rec(kv.Key);
                    var h = Rec(kv.Value);
                    if (t == null || h == null || t.S.IsMissile || !UnitClasses.IsAircraft(h.S.Class)) continue;
                    p.OwnOnlyReported.Add(kv.Key);
                    Emit(C2EventKind.Contact, "contact", p.Side, 0, t.Id, "own_sensor_only",
                        "Own-sensor contact: " + h.Name + "'s own radar holds " + t.Name + " at " + Km(h, t) + " km. No other unit holds it.");
                }
            }
        }

        /// <summary>
        /// Own-sensor contacts are re-read for each unit every this many ticks, staggered by unit id (5 = every 1 s at
        /// 5 Hz, the rate of BDArmory's own guard scan). 1 = every tick. The interim picture's main cost (M2 replaces it).
        /// </summary>
        public int ContactSlices = 5;

        /// <summary>Removes what <paramref name="r"/> holds from its targets' holder counts.</summary>
        private void Uncount(UnitRec r)
        {
            int si = r.ContribSide;
            if (si >= 0)
            {
                for (int i = 0; i < r.Contrib.Count; i++)
                {
                    var t = r.Contrib[i];
                    if (t.HeldN == null || t.HeldN.Length <= si) continue;
                    t.HeldN[si]--;
                    t.HeldSum[si] -= r.Id;
                    if (r.ContribSurv) t.HeldSurvN[si]--;
                }
            }
            r.Contrib.Clear();
            r.ContribSide = -1;
        }

        private bool SideHasOrders(SidePicture p)
        {
            for (int i = 0; i < _active.Count; i++) if (_active[i].Side == p.Side) return true;
            return false;
        }

        private bool SideHasUnitsWithTasks(SidePicture p)
        {
            if (SideHasOrders(p)) return true;
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (r.S.Side == p.Side && r.Hold != null) return true;
            }
            return false;
        }

        private void OnIdentified(SidePicture p, UnitRec by, UnitRec t)
        {
            if (p.AnyIdentified) return;
            p.AnyIdentified = true;
            p.FirstIdentifiedUt = _now;
            p.FirstIdentified = t.Id;
            Emit(C2EventKind.Contact, "contact", p.Side, 0, t.Id, "first_identified",
                "First hostile identified: " + t.Name + " (" + UnitClasses.Name(t.S.Class) + ") by " + by.Name + "'s own radar at " + Km(by, t) + " km.");
        }

        private void RebuildClaims()
        {
            for (int s = 0; s < _sideList.Count; s++) _sideList[s].Claims.Clear();
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (!r.Alive || r.Claim == 0 || r.S.Side == null) { r.Claim = 0; continue; }
                Side(r.S.Side).AddClaim(r.Claim);
            }
        }

        private void SetClaim(UnitRec r, uint target)
        {
            if (r.Claim == target) return;
            var p = Side(r.S.Side);
            p.RemoveClaim(r.Claim);
            r.Claim = target;
            p.AddClaim(target);
        }

        // ------------------------------------------------------------------ deaths and order completion

        private void MarkDead(UnitRec r)
        {
            r.Dead = true;
            r.S.Alive = false;
            if (r.S.Side != null)
            {
                var p = Side(r.S.Side);
                p.Vips.Remove(r.Id);
                p.RemoveClaim(r.Claim);
            }
            r.Claim = 0;
            for (int s = 0; s < _sideList.Count; s++) _sideList[s].Identified.Remove(r.Id);
            HandleDeath(r);
        }

        private void HandleDeath(UnitRec r)
        {
            if (r.Engage != null) EndUnitOrder(r, r.Engage, OrderStatus.Failed, Reasons.UnitDestroyed, r.Name + " lost; " + Orders.Wire(r.Engage.Type) + " ended.", false);
            if (r.Move != null) EndUnitOrder(r, r.Move, OrderStatus.Failed, Reasons.UnitDestroyed, r.Name + " lost; " + Orders.Wire(r.Move.Type) + " ended.", false);
            r.Hold = null;
            r.Divert = 0;
            r.SelfDef = 0;
            r.Advance = 0;
            r.VipThreat = 0;
        }

        /// <summary>
        /// Ends a unit order with a report. <paramref name="resume"/>: when an engagement order ends, the unit's
        /// suspended movement order (if any) resumes.
        /// </summary>
        private void EndUnitOrder(UnitRec r, UnitOrder uo, OrderStatus status, string reason, string text, bool resume)
        {
            if (uo == null || uo.IsTerminal) return;
            uo.Status = status;
            uo.Reason = reason;
            uo.Parent.UpdatedUt = _now;
            C2EventKind kind = status == OrderStatus.Achieved ? C2EventKind.OrderAchieved
                : status == OrderStatus.Failed ? C2EventKind.OrderFailed
                : status == OrderStatus.Cancelled ? C2EventKind.OrderCancelled
                : C2EventKind.OrderSuperseded;
            Emit(kind, "orders", uo.Parent.Side, uo.Parent.Id, uo.Unit, reason, text);
            if (r == null) return;
            if (r.Engage == uo)
            {
                r.Engage = null;
                if (r.Claim != 0 && r.S.Side != null) SetClaim(r, 0);
                r.Divert = 0;
                if (resume && r.Move != null && r.Move.Status == OrderStatus.Suspended)
                {
                    r.Move.Status = OrderStatus.Executing;
                    r.Move.Fed = false;
                    Emit(C2EventKind.OrderResumed, "orders", r.Move.Parent.Side, r.Move.Parent.Id, r.Id, null,
                        r.Name + ": resuming " + Orders.Wire(r.Move.Type) + ".");
                }
            }
            else if (r.Move == uo)
            {
                r.Move = null;
                r.Divert = 0;
            }
        }

        private void FinishOrders()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var o = _active[i];
                int achieved = 0, failed = 0, cancelled = 0, superseded = 0, open = 0;
                for (int k = 0; k < o.Units.Count; k++)
                {
                    switch (o.Units[k].Status)
                    {
                        case OrderStatus.Achieved: achieved++; break;
                        case OrderStatus.Failed: failed++; break;
                        case OrderStatus.Cancelled: cancelled++; break;
                        case OrderStatus.Superseded: superseded++; break;
                        default: open++; break;
                    }
                }
                if (open > 0) continue;
                int n = o.Units.Count;
                if (achieved == n) { o.Status = OrderStatus.Achieved; o.Reason = Reasons.Arrived; }
                else if (superseded == n) { o.Status = OrderStatus.Superseded; o.Reason = Reasons.Superseded; }
                else if (cancelled == n) { o.Status = OrderStatus.Cancelled; o.Reason = Reasons.Cancelled; }
                else if (achieved > 0) { o.Status = OrderStatus.Achieved; o.Reason = "partial"; }
                else if (failed > 0) { o.Status = OrderStatus.Failed; o.Reason = "failed"; }
                else if (superseded > 0) { o.Status = OrderStatus.Superseded; o.Reason = Reasons.Superseded; }
                else { o.Status = OrderStatus.Cancelled; o.Reason = Reasons.Cancelled; }
                o.UpdatedUt = _now;
                string counts = achieved + " achieved, " + failed + " failed, " + cancelled + " cancelled, " + superseded + " superseded";
                C2EventKind kind = o.Status == OrderStatus.Achieved ? C2EventKind.OrderAchieved
                    : o.Status == OrderStatus.Failed ? C2EventKind.OrderFailed
                    : o.Status == OrderStatus.Cancelled ? C2EventKind.OrderCancelled
                    : C2EventKind.OrderSuperseded;
                Emit(kind, "orders", o.Side, o.Id, 0, o.Reason, o.Handle + " " + Orders.Wire(o.Type) + " ended: " + counts + ".");
                _active.RemoveAt(i);
                PruneCompleted();
            }
        }

        private void PruneCompleted()
        {
            if (_orders.Count <= CompletedOrderMemory + _active.Count) return;
            int oldest = int.MaxValue;
            foreach (var kv in _orders) if (kv.Value.IsTerminal && kv.Key < oldest) oldest = kv.Key;
            if (oldest != int.MaxValue) _orders.Remove(oldest);
        }

        // ------------------------------------------------------------------ helpers

        private SidePicture Side(string side)
        {
            if (side == null) side = "";
            SidePicture p;
            if (!_sides.TryGetValue(side, out p))
            {
                p = new SidePicture(side) { Index = _sideList.Count };
                _sides[side] = p;
                _sideList.Add(p);
            }
            return p;
        }

        private UnitRec Rec(uint id)
        {
            UnitRec r;
            return id != 0 && _units.TryGetValue(id, out r) ? r : null;
        }

        /// <summary>
        /// A unit the engine has not read yet is read now: the engine reads the world on its tick and on an order, and
        /// it only ticks once commanded, so a status or doctrine query before the first order would not find the unit.
        /// </summary>
        private UnitRec RecNow(uint id)
        {
            var r = Rec(id);
            if (r != null || id == 0) return r;
            _now = _world.Now;
            RefreshUnits();
            return Rec(id);
        }

        private UnitRec RecOrCreate(uint id)
        {
            UnitRec r;
            if (_units.TryGetValue(id, out r)) return r;
            r = new UnitRec();
            r.S.Id = id;
            _units[id] = r;
            _unitList.Add(r);
            return r;
        }

        private UnitRec AliveRec(uint id)
        {
            var r = Rec(id);
            return r != null && r.Alive ? r : null;
        }

        private const double Deg = Math.PI / 180.0;

        private void SetVector(UnitRec r)
        {
            double lat = r.S.Pos.Lat * Deg, lon = r.S.Pos.Lon * Deg;
            double c = Math.Cos(lat);
            double R = _geo.Radius;
            r.X = R * c * Math.Cos(lon);
            r.Y = R * c * Math.Sin(lon);
            r.Z = R * Math.Sin(lat);
        }

        /// <summary>
        /// Surface distance between two units from their cached sphere vectors: the chord, converted to the arc with
        /// the asin series (relative error below 0.1% out to one body radius). No trigonometry per pair.
        /// </summary>
        private double Dist(UnitRec a, UnitRec b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            double c2 = dx * dx + dy * dy + dz * dz;
            double c = Math.Sqrt(c2);
            double q = c2 / (_geo.Radius * _geo.Radius);
            return c * (1 + q / 24 + 3 * q * q / 640);
        }

        private sbyte[] _hostile = new sbyte[0];
        private int _hostileN;

        /// <summary>BDA team hostility, asked once per side pair per tick.</summary>
        private bool HostileSides(SidePicture a, SidePicture b)
        {
            int n = _sideList.Count;
            if (_hostileN != n)
            {
                _hostile = new sbyte[n * n];
                _hostileN = n;
            }
            int k = a.Index * n + b.Index;
            sbyte v = _hostile[k];
            if (v == 0)
            {
                v = _world.AreHostile(a.Side, b.Side) ? (sbyte)1 : (sbyte)2;
                _hostile[k] = v;
            }
            return v == 1;
        }

        private string Km(UnitRec a, UnitRec b) { return (Dist(a, b) / 1000.0).ToString("0", CultureInfo.InvariantCulture); }

        private static string KmText(double m) { return (m / 1000.0).ToString("0.#", CultureInfo.InvariantCulture); }

        private bool Hostile(UnitRec r, UnitRec t)
        {
            return r.P != null && t.P != null && HostileSides(r.P, t.P);
        }

        private C2Event Emit(C2EventKind kind, string category, string side, int order, uint unit, string code, string text)
        {
            var e = new C2Event { Ut = _now, Kind = kind, Category = category, Side = side, Order = order, Unit = unit, Code = code, Text = text };
            _events.Add(e);
            var obs = ReportObserver;
            if (obs != null)
            {
                try { obs(e); }
                catch (Exception) { /* an observer must never break the engine */ }
            }
            return e;
        }

        private static int OrderIdOf(UnitRec r)
        {
            if (r.Engage != null) return r.Engage.Parent.Id;
            if (r.Move != null) return r.Move.Parent.Id;
            return 0;
        }

        private string SideOfGroup(string group)
        {
            List<uint> l;
            if (group == null || !_groups.TryGetValue(group, out l)) return null;
            for (int i = 0; i < l.Count; i++)
            {
                var r = Rec(l[i]);
                if (r != null && r.S.Side != null) return r.S.Side;
            }
            return null;
        }

        private static string LevelName(DoctrineLevel l) { return l == DoctrineLevel.Side ? "side" : l == DoctrineLevel.Group ? "group" : "unit"; }

        internal static bool TryParseUnit(string s, out uint id)
        {
            id = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("v:", StringComparison.Ordinal)) s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
        }

        private static string TaskName(UnitRec r)
        {
            if (r.Engage != null) return Orders.Wire(r.Engage.Type);
            if (r.Move != null) return Orders.Wire(r.Move.Type);
            if (r.Hold != null) return "hold";
            if (r.Advance != 0) return "advance";
            if (r.VipThreat != 0) return "vip_priority";
            return "guard";
        }

        private bool IsWinchester(UnitRec r)
        {
            if (r.S.MissilesMax <= 0) return false;
            if (r.S.MissilesReady <= 0) return true;
            return r.Doc.WinchesterAmmoPct > 0 && 100.0 * r.S.MissilesReady / r.S.MissilesMax <= r.Doc.WinchesterAmmoPct;
        }
    }
}
