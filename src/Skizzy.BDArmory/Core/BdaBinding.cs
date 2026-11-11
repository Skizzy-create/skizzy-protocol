using System;
using System.Collections.Generic;
using Skizzy.C2;

namespace Skizzy.Bda
{
    /// <summary>
    /// The C2 engine's binding to BDArmory (07 §5): <see cref="IC2World"/>, <see cref="IBdaControl"/> and the hook event
    /// forwarding, over the thin <see cref="IBdaGame"/> seam. Unity-free, so the logic is unit-tested off-game.
    /// <list type="bullet">
    /// <item>Units are vessels; settings go to the vessel's primary weapon manager and its AI. The binding keeps a
    /// per-unit mirror of what the engine set, so when the primary weapon manager or AI changes (part destroyed,
    /// decoupled, vessel reloaded) the old one is restored and the mirror is applied to the new one.</item>
    /// <item>Every value the binding changes is captured first and put back on release (<see cref="ReleaseAll"/>,
    /// a weapon manager change, or the engine's own restore calls). Persistent fields (engage missiles, engage range)
    /// are also put back while a vessel is on rails, so KSP never saves the engine's values with it.</item>
    /// <item>A hook the probe did not find is never called: the method does nothing (the engine refuses the orders
    /// that need it with HOOK_MISSING).</item>
    /// <item>Events the binding causes itself (Cleared/Replaced when it reassigns, PatrolResume when it replaces a
    /// route) are not forwarded; events from a weapon manager or AI that is no longer the unit's are dropped.</item>
    /// </list>
    /// Main thread only. Never throws into the engine: game errors are logged and the call is dropped.
    /// </summary>
    public sealed class BdaBinding : IC2World, IBdaControl, IC2Binding, IHookReport
    {
        private sealed class Slot
        {
            public readonly uint Id;
            public readonly UnitState S = new UnitState();
            public object Vessel, Wm, Ai;
            public bool Present, Packed, DeathSent;
            public int Seen;

            // mirror: what the engine set (applies to whichever weapon manager / AI is current)
            public uint OvTarget;
            public float OvDuration;
            public bool OvSticky;
            public double OvSetAt;
            public readonly List<uint> Secondary = new List<uint>();
            public bool SecondaryOn, SecondaryOnly;
            public bool FireSet;
            public FireAuthorityMode Fire;
            public TargetFilter Filter;
            public bool GuardSet, Guard;
            public readonly List<GeoPoint> Route = new List<GeoPoint>();
            public bool RouteOn, RoutePending;
            public float RouteRadius, RouteSpeed;
            public int RouteLaps, RouteNext, RouteOffset;
            public bool PatrolSet;
            public PatrolSettings Patrol;
            public bool Commanded;
            public GeoPoint FlyTo;
            public bool MissilesSet, MissilesOn;
            public bool RangeExtended;

            // baselines on the current weapon manager / AI
            public bool FireBaselineKnown;
            public FireAuthorityMode FireBaseline;
            public bool PatrolBaselineKnown;
            public PatrolSettings PatrolBaseline;
            public object MissilesBaseline;
            public object RangeBaseline;

            public Slot(uint id) { Id = id; }
        }

        private readonly IBdaGame _game;
        private readonly HookSet _hooks;
        private readonly List<GameUnit> _scan = new List<GameUnit>(64);
        private readonly Dictionary<uint, Slot> _slots = new Dictionary<uint, Slot>();
        private readonly Dictionary<object, Slot> _byWm = new Dictionary<object, Slot>(RefEq.Instance);
        private readonly Dictionary<object, Slot> _byAi = new Dictionary<object, Slot>(RefEq.Instance);
        private readonly List<Slot> _work = new List<Slot>();
        private readonly List<GeoPoint> _routeTmp = new List<GeoPoint>();
        private IHookEvents _sink;
        private int _selfCall;
        private int _serial;
        private int _faults;

        /// <summary>Error log (the KSP layer sets Debug.LogError). Rate-limited by the binding.</summary>
        public Action<string> Log;

        public BdaBinding(IBdaGame game)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _hooks = game.Hooks;
        }

        // ---- IC2Binding ---------------------------------------------------------------------------

        public IC2World World => this;
        public IBdaControl Control => this;
        public string Description => _game.Description;

        public void Connect(IHookEvents sink) { _sink = sink; }

        /// <summary>Puts every setting the binding changed back (hooks, persistent fields, commands) and forgets all units.</summary>
        public void ReleaseAll()
        {
            foreach (var s in _slots.Values) _work.Add(s);
            for (int i = 0; i < _work.Count; i++)
            {
                var s = _work[i];
                try
                {
                    if (s.Wm != null && _game.IsLive(s.Wm)) RestoreWm(s, s.Wm);
                    if (s.Ai != null && _game.IsLive(s.Ai)) RestoreAi(s, s.Ai);
                }
                catch (Exception e) { Fault("release " + s.Id, e); }
            }
            _work.Clear();
            _slots.Clear();
            _byWm.Clear();
            _byAi.Clear();
        }

        // ---- IC2World -----------------------------------------------------------------------------

        public double Now => _game.Now;
        public double BodyRadius => _game.BodyRadius;
        public HookSet Hooks => _hooks;

        public void GetUnits(List<UnitState> into)
        {
            Refresh();
            foreach (var s in _slots.Values)
                if (s.Present) into.Add(s.S);
        }

        public void GetOwnContacts(uint unit, List<uint> into)
        {
            if (!Has(HookSet.F5) || !_slots.TryGetValue(unit, out var s) || !s.Present || s.Wm == null) return;
            try { _game.GetOwnContacts(s.Vessel, s.Wm, into); }
            catch (Exception e) { Fault("contacts " + unit, e); }
        }

        public bool SideDetects(string side, uint target)
        {
            try { return _game.SideDetects(side, target); }
            catch (Exception e) { Fault("detects", e); return false; }
        }

        public bool AreHostile(string sideA, string sideB)
        {
            try { return _game.AreHostile(sideA, sideB); }
            catch (Exception e) { Fault("hostile", e); return sideA != sideB; }
        }

        public int FriendliesEngaging(string side, uint target, uint exceptUnit)
        {
            object wm = null;
            if (exceptUnit != 0 && _slots.TryGetValue(exceptUnit, out var s)) wm = s.Wm;
            try { return _game.FriendliesEngaging(side, target, wm); }
            catch (Exception e) { Fault("engaging", e); return 0; }
        }

        /// <summary>Scans the game and reconciles every unit (weapon manager / AI changes, rails, unloads).</summary>
        public void Refresh()
        {
            _serial++;
            try { _game.Scan(_scan); }
            catch (Exception e) { Fault("scan", e); _scan.Clear(); }
            for (int i = 0; i < _scan.Count; i++)
            {
                var u = _scan[i];
                uint id = u.S.Id;
                if (id == 0) continue;
                if (!_slots.TryGetValue(id, out var s))
                {
                    s = new Slot(id);
                    _slots[id] = s;
                }
                s.Seen = _serial;
                s.S.CopyFrom(u.S);
                s.Vessel = u.Vessel;
                s.Present = true;
                try { Reconcile(s, u.Wm, u.Ai, u.Packed); }
                catch (Exception e) { Fault("reconcile " + id, e); }
            }
            foreach (var s in _slots.Values)
                if (s.Seen != _serial) _work.Add(s);
            for (int i = 0; i < _work.Count; i++)
            {
                var s = _work[i];
                if (s.Present)
                {
                    s.Present = false;
                    try { Reconcile(s, null, null, s.Packed); }
                    catch (Exception e) { Fault("absent " + s.Id, e); }
                }
                if (s.Vessel == null || !_game.IsLive(s.Vessel)) Gone(s); // destroyed without an event
            }
            _work.Clear();
        }

        private void Reconcile(Slot s, object wm, object ai, bool packed)
        {
            bool wasPacked = s.Packed;
            bool wmChanged = !ReferenceEquals(s.Wm, wm);
            if (packed && !wasPacked && !wmChanged && s.Wm != null) RestorePersistent(s, s.Wm);
            s.Packed = packed;
            if (wmChanged)
            {
                var old = s.Wm;
                if (old != null)
                {
                    if (_byWm.TryGetValue(old, out var owner) && owner == s) _byWm.Remove(old);
                    if (_game.IsLive(old)) RestoreWm(s, old);
                }
                s.FireBaselineKnown = false;
                s.MissilesBaseline = null;
                s.RangeBaseline = null;
                s.Wm = wm;
                if (wm != null)
                {
                    _byWm[wm] = s;
                    ApplyWm(s, wm);
                }
            }
            else if (!packed && wasPacked && s.Wm != null) ApplyPersistent(s, s.Wm);

            if (!ReferenceEquals(s.Ai, ai))
            {
                var old = s.Ai;
                if (old != null)
                {
                    if (_byAi.TryGetValue(old, out var owner) && owner == s) _byAi.Remove(old);
                    if (_game.IsLive(old)) RestoreAi(s, old);
                }
                s.PatrolBaselineKnown = false;
                s.Ai = ai;
                if (ai != null)
                {
                    _byAi[ai] = s;
                    ApplyAi(s, ai);
                }
            }
            else if (s.RoutePending && ai != null && s.S.AiActive) ApplyRoute(s, ai);
        }

        // ---- applying and restoring --------------------------------------------------------------

        private void ApplyWm(Slot s, object wm)
        {
            if (s.OvTarget != 0 && Has(HookSet.F1))
            {
                float d = s.OvDuration;
                if (d > 0)
                {
                    d -= (float)(Now - s.OvSetAt);
                    if (d <= 0)
                    {
                        uint t = s.OvTarget;
                        s.OvTarget = 0;
                        Raise(s.Id, t, OverrideLostReason.Expired);
                        goto secondary;
                    }
                }
                AssignOverride(s, wm, s.OvTarget, d, s.OvSticky);
            }
        secondary:
            if (s.SecondaryOn && Has(HookSet.F2)) _game.SetSecondary(wm, s.Secondary, s.SecondaryOnly);
            if (s.FireSet && Has(HookSet.F3)) ApplyFire(s, wm);
            if (s.Filter != null && Has(HookSet.F4)) _game.SetFilter(wm, s.Filter);
            if (s.GuardSet) _game.SetGuard(wm, s.Guard);
            if (!s.Packed) ApplyPersistent(s, wm);
        }

        private void RestoreWm(Slot s, object wm)
        {
            if (Has(HookSet.F1) && _game.HasOverride(wm)) Self(wm, OpClearOverride);
            if (s.SecondaryOn && Has(HookSet.F2)) _game.SetSecondary(wm, null, false);
            if (s.FireBaselineKnown && Has(HookSet.F3)) _game.SetFireAuthority(wm, s.FireBaseline);
            s.FireBaselineKnown = false;
            if (s.Filter != null && Has(HookSet.F4)) _game.SetFilter(wm, null);
            RestorePersistent(s, wm);
        }

        private void ApplyAi(Slot s, object ai)
        {
            if (s.RouteOn && Has(HookSet.F6)) ApplyRoute(s, ai);
            else if (s.Commanded)
            {
                _selfCall++;
                try { if (!_game.FlyTo(ai, s.FlyTo)) s.Commanded = false; }
                finally { _selfCall--; }
            }
            if (s.PatrolSet && Has(HookSet.F6b)) ApplyPatrol(s, ai);
        }

        private void RestoreAi(Slot s, object ai)
        {
            _selfCall++;
            try
            {
                if (s.RouteOn && Has(HookSet.F6) && _game.IsCustomRoute(ai)) _game.ClearWaypoints(ai);
                if (s.Commanded) _game.ReleaseCommand(ai);
            }
            finally { _selfCall--; }
            if (s.PatrolBaselineKnown && Has(HookSet.F6b)) _game.SetPatrol(ai, s.PatrolBaseline);
            s.PatrolBaselineKnown = false;
        }

        /// <summary>Re-applies the rest of the route on a new AI: from the next gate when single-lap (indices offset), else all of it.</summary>
        private void ApplyRoute(Slot s, object ai)
        {
            _routeTmp.Clear();
            int from = s.RouteLaps <= 1 ? Math.Min(s.RouteNext, s.Route.Count) : 0;
            for (int i = from; i < s.Route.Count; i++) _routeTmp.Add(s.Route[i]);
            if (_routeTmp.Count == 0)
            {
                s.RouteOn = false;
                s.RoutePending = false;
                return;
            }
            bool ok;
            _selfCall++;
            try { ok = _game.SetWaypoints(ai, _routeTmp, s.RouteRadius, s.RouteLaps, s.RouteSpeed); }
            finally { _selfCall--; }
            s.RoutePending = !ok;
            if (ok) s.RouteOffset = from;
        }

        private void ApplyFire(Slot s, object wm)
        {
            if (!s.FireBaselineKnown)
            {
                s.FireBaseline = _game.GetFireAuthority(wm);
                s.FireBaselineKnown = true;
            }
            _game.SetFireAuthority(wm, s.Fire);
        }

        private void ApplyPatrol(Slot s, object ai)
        {
            if (!s.PatrolBaselineKnown)
            {
                s.PatrolBaseline = _game.GetPatrol(ai);
                s.PatrolBaselineKnown = true;
            }
            _game.SetPatrol(ai, s.Patrol);
        }

        private void ApplyPersistent(Slot s, object wm)
        {
            if (s.MissilesSet)
            {
                if (s.MissilesBaseline == null) s.MissilesBaseline = _game.CaptureEngageMissiles(wm);
                _game.SetEngageMissiles(wm, s.MissilesOn);
            }
            if (s.RangeExtended)
            {
                if (s.RangeBaseline == null) s.RangeBaseline = _game.CaptureMissileRange(wm);
                _game.ExtendMissileRange(wm);
            }
        }

        private void RestorePersistent(Slot s, object wm)
        {
            if (s.MissilesBaseline != null)
            {
                var b = s.MissilesBaseline;
                s.MissilesBaseline = null;
                _game.RestoreEngageMissiles(wm, b);
            }
            if (s.RangeBaseline != null)
            {
                var b = s.RangeBaseline;
                s.RangeBaseline = null;
                _game.RestoreMissileRange(wm, b);
            }
        }

        private void AssignOverride(Slot s, object wm, uint target, float durationS, bool sticky)
        {
            TargetResolve r;
            _selfCall++;
            try
            {
                r = _game.SetOverride(wm, target, durationS, sticky);
                // the old assignment must not outlive a failed replacement
                if (r != TargetResolve.Ok && _game.HasOverride(wm)) _game.ClearOverride(wm);
            }
            finally { _selfCall--; }
            if (r == TargetResolve.Ok) return;
            s.OvTarget = 0;
            Raise(s.Id, target, r == TargetResolve.NotHostile ? OverrideLostReason.NotHostile : OverrideLostReason.NotEngageable);
        }

        private const int OpClearOverride = 1;

        private void Self(object wm, int op)
        {
            _selfCall++;
            try
            {
                if (op == OpClearOverride) _game.ClearOverride(wm);
            }
            finally { _selfCall--; }
        }

        // ---- IBdaControl --------------------------------------------------------------------------

        public void SetOverrideTarget(uint unit, uint target, float durationS, bool sticky)
        {
            if (!Has(HookSet.F1) || !_slots.TryGetValue(unit, out var s)) return;
            if (target == 0) { ClearOverrideTarget(unit); return; }
            s.OvTarget = target;
            s.OvDuration = durationS;
            s.OvSticky = sticky;
            s.OvSetAt = Now;
            if (s.Wm == null) return; // applied when a weapon manager appears
            try { AssignOverride(s, s.Wm, target, durationS, sticky); }
            catch (Exception e) { Fault("F1 set " + unit, e); }
        }

        public void ClearOverrideTarget(uint unit)
        {
            if (!Has(HookSet.F1) || !_slots.TryGetValue(unit, out var s)) return;
            s.OvTarget = 0;
            if (s.Wm == null) return;
            try { if (_game.HasOverride(s.Wm)) Self(s.Wm, OpClearOverride); }
            catch (Exception e) { Fault("F1 clear " + unit, e); }
        }

        public void SetSecondaryTargets(uint unit, IList<uint> targets, bool only)
        {
            if (!Has(HookSet.F2) || !_slots.TryGetValue(unit, out var s)) return;
            bool wasOn = s.SecondaryOn;
            s.Secondary.Clear();
            if (targets != null)
                for (int i = 0; i < targets.Count; i++) s.Secondary.Add(targets[i]);
            s.SecondaryOn = targets != null;
            s.SecondaryOnly = targets != null && only;
            if (s.Wm == null || (!wasOn && !s.SecondaryOn)) return;
            try { _game.SetSecondary(s.Wm, s.SecondaryOn ? s.Secondary : null, s.SecondaryOnly); }
            catch (Exception e) { Fault("F2 " + unit, e); }
        }

        public void SetFireAuthority(uint unit, FireAuthorityMode mode)
        {
            if (!Has(HookSet.F3) || !_slots.TryGetValue(unit, out var s)) return;
            s.FireSet = true;
            s.Fire = mode;
            if (s.Wm == null) return;
            try { ApplyFire(s, s.Wm); }
            catch (Exception e) { Fault("F3 " + unit, e); }
        }

        public void SetTargetFilter(uint unit, TargetFilter filter)
        {
            if (!Has(HookSet.F4) || !_slots.TryGetValue(unit, out var s)) return;
            bool had = s.Filter != null;
            s.Filter = filter;
            if (s.Wm == null || (!had && filter == null)) return;
            try { _game.SetFilter(s.Wm, filter); }
            catch (Exception e) { Fault("F4 " + unit, e); }
        }

        public bool SetWaypoints(uint unit, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps)
        {
            if (!Has(HookSet.F6) || !_slots.TryGetValue(unit, out var s) || s.Ai == null) return false;
            if (points == null || points.Count == 0) { ClearWaypoints(unit); return false; }
            bool ok;
            _selfCall++;
            try { ok = _game.SetWaypoints(s.Ai, points, radiusM, laps, maxSpeedMps); }
            catch (Exception e) { Fault("F6 " + unit, e); ok = false; }
            finally { _selfCall--; }
            if (!ok) return false;
            s.Route.Clear();
            for (int i = 0; i < points.Count; i++) s.Route.Add(points[i]);
            s.RouteOn = true;
            s.RoutePending = false;
            s.RouteRadius = radiusM;
            s.RouteLaps = laps;
            s.RouteSpeed = maxSpeedMps;
            s.RouteNext = 0;
            s.RouteOffset = 0;
            s.Commanded = false;
            return true;
        }

        public void ClearWaypoints(uint unit)
        {
            if (!Has(HookSet.F6) || !_slots.TryGetValue(unit, out var s)) return;
            bool had = s.RouteOn;
            s.RouteOn = false;
            s.RoutePending = false;
            if (s.Ai == null || !had) return;
            _selfCall++;
            try { _game.ClearWaypoints(s.Ai); }
            catch (Exception e) { Fault("F6 clear " + unit, e); }
            finally { _selfCall--; }
        }

        public void SetPatrolEngage(uint unit, bool yieldToCombat, float leashM, GeoPoint anchor, bool hasAnchor)
        {
            if (!Has(HookSet.F6b) || !_slots.TryGetValue(unit, out var s)) return;
            s.PatrolSet = true;
            s.Patrol = new PatrolSettings { Yield = yieldToCombat, LeashM = leashM, Anchor = hasAnchor ? anchor : default(GeoPoint), HasAnchor = hasAnchor };
            if (s.Ai == null) return;
            try { ApplyPatrol(s, s.Ai); }
            catch (Exception e) { Fault("F6b " + unit, e); }
        }

        public bool CommandFlyTo(uint unit, GeoPoint point)
        {
            if (!_slots.TryGetValue(unit, out var s) || s.Ai == null) return false;
            bool ok;
            _selfCall++;
            try { ok = _game.FlyTo(s.Ai, point); }
            catch (Exception e) { Fault("fly-to " + unit, e); ok = false; }
            finally { _selfCall--; }
            if (!ok) return false;
            s.Commanded = true;
            s.FlyTo = point;
            s.RouteOn = false; // a command replaces a custom route
            s.RoutePending = false;
            return true;
        }

        public void ReleaseCommand(uint unit)
        {
            if (!_slots.TryGetValue(unit, out var s)) return;
            s.Commanded = false;
            s.RouteOn = false; // release ends a custom route too
            s.RoutePending = false;
            if (s.Ai == null) return;
            _selfCall++;
            try { _game.ReleaseCommand(s.Ai); }
            catch (Exception e) { Fault("release command " + unit, e); }
            finally { _selfCall--; }
        }

        public void SetGuardMode(uint unit, bool on)
        {
            if (!_slots.TryGetValue(unit, out var s)) return;
            s.GuardSet = true;
            s.Guard = on;
            if (s.Wm == null) return;
            try { _game.SetGuard(s.Wm, on); }
            catch (Exception e) { Fault("guard " + unit, e); }
        }

        public void SetEngageMissiles(uint unit, bool on)
        {
            if (!_slots.TryGetValue(unit, out var s)) return;
            s.MissilesSet = true;
            s.MissilesOn = on;
            if (s.Wm == null || s.Packed) return;
            try
            {
                if (s.MissilesBaseline == null) s.MissilesBaseline = _game.CaptureEngageMissiles(s.Wm);
                _game.SetEngageMissiles(s.Wm, on);
            }
            catch (Exception e) { Fault("engage missiles " + unit, e); }
        }

        public void RestoreEngageMissiles(uint unit)
        {
            if (!_slots.TryGetValue(unit, out var s)) return;
            s.MissilesSet = false;
            if (s.Wm == null || s.MissilesBaseline == null) return;
            var b = s.MissilesBaseline;
            s.MissilesBaseline = null;
            try { _game.RestoreEngageMissiles(s.Wm, b); }
            catch (Exception e) { Fault("restore missiles " + unit, e); }
        }

        public void SetMissileRangeMax(uint unit, bool extended)
        {
            if (!_slots.TryGetValue(unit, out var s)) return;
            s.RangeExtended = extended;
            if (s.Wm == null) return;
            try
            {
                if (extended)
                {
                    if (s.Packed) return;
                    if (s.RangeBaseline == null) s.RangeBaseline = _game.CaptureMissileRange(s.Wm);
                    _game.ExtendMissileRange(s.Wm);
                }
                else if (s.RangeBaseline != null)
                {
                    var b = s.RangeBaseline;
                    s.RangeBaseline = null;
                    _game.RestoreMissileRange(s.Wm, b);
                }
            }
            catch (Exception e) { Fault("missile range " + unit, e); }
        }

        // ---- game events (called by the KSP layer from BDArmory's static events) ---------------

        /// <summary>F1 <c>OnExternalOverrideLost</c>. <paramref name="reason"/> is BDA's raw enum value.</summary>
        public void GameOverrideLost(object wm, uint target, int reason)
        {
            if (wm == null || !_byWm.TryGetValue(wm, out var s)) return; // not a unit's primary weapon manager
            var r = MapReason(reason);
            if (_selfCall > 0 && (r == OverrideLostReason.Cleared || r == OverrideLostReason.Replaced)) return;
            if (s.OvTarget == target || target == 0) s.OvTarget = 0;
            Raise(s.Id, target, r);
        }

        /// <summary>BDArmory only appends reasons; one this build does not know is treated as NotEngageable.</summary>
        public static OverrideLostReason MapReason(int reason)
        {
            return reason >= 0 && reason <= (int)OverrideLostReason.NotHostile ? (OverrideLostReason)reason : OverrideLostReason.NotEngageable;
        }

        public void GameMissileFired(object wm, uint missile, uint target)
        {
            if (wm == null || !_byWm.TryGetValue(wm, out var s)) return;
            _sink?.OnMissileFired(s.Id, missile, target);
        }

        public void GameWaypointReached(object ai, int index, int lap)
        {
            if (ai == null || !_byAi.TryGetValue(ai, out var s)) return;
            int i = index + s.RouteOffset;
            s.RouteNext = i + 1;
            _sink?.OnWaypointReached(s.Id, i, lap);
        }

        public void GameRouteCompleted(object ai)
        {
            if (ai == null || !_byAi.TryGetValue(ai, out var s)) return;
            s.RouteOn = false;
            s.RoutePending = false;
            _sink?.OnRouteCompleted(s.Id);
        }

        public void GamePatrolEngage(object ai, uint target)
        {
            if (ai == null || !_byAi.TryGetValue(ai, out var s)) return;
            _sink?.OnPatrolEngage(s.Id, target);
        }

        public void GamePatrolResume(object ai, bool leashBroke)
        {
            if (ai == null || !_byAi.TryGetValue(ai, out var s)) return;
            if (_selfCall > 0 && !leashBroke) return; // the binding replaced or cleared the route
            _sink?.OnPatrolResume(s.Id, leashBroke);
        }

        public void GameTargetDetected(uint target, string side)
        {
            if (target != 0 && side != null) _sink?.OnTargetDetected(target, side);
        }

        public void GameTargetLost(uint target, string side)
        {
            if (target != 0 && side != null) _sink?.OnTargetLost(target, side);
        }

        /// <summary><c>onVesselWillDestroy</c>: the unit is gone (raised once per unit).</summary>
        public void GameVesselDestroyed(uint id)
        {
            if (_slots.TryGetValue(id, out var s)) Gone(s);
        }

        /// <summary>F10 <c>CompetitionScores.OnDeath</c> (scoring name = vessel name).</summary>
        public void GameDeath(string vesselName)
        {
            if (string.IsNullOrEmpty(vesselName)) return;
            Slot hit = null;
            foreach (var s in _slots.Values)
                if (s.Present && !s.DeathSent && s.S.Name == vesselName) { hit = s; break; }
            if (hit != null) Gone(hit);
        }

        /// <summary><c>onVesselGoOnRails</c>: put persistent fields back before KSP can save them with the vessel.</summary>
        public void GamePacked(uint id)
        {
            if (!_slots.TryGetValue(id, out var s) || s.Packed) return;
            try
            {
                if (s.Wm != null && _game.IsLive(s.Wm)) RestorePersistent(s, s.Wm);
            }
            catch (Exception e) { Fault("packed " + id, e); }
            s.Packed = true;
        }

        /// <summary><c>onVesselUnloaded</c>: the part modules are gone; the mirror stays and is applied when the vessel reloads.</summary>
        public void GameUnloaded(uint id)
        {
            if (!_slots.TryGetValue(id, out var s)) return;
            GamePacked(id);
            s.Present = false;
            try { Reconcile(s, null, null, true); }
            catch (Exception e) { Fault("unloaded " + id, e); }
        }

        private void Gone(Slot s)
        {
            if (!s.DeathSent)
            {
                s.DeathSent = true;
                _sink?.OnUnitDestroyed(s.Id);
            }
            if (s.Wm != null && _byWm.TryGetValue(s.Wm, out var a) && a == s) _byWm.Remove(s.Wm);
            if (s.Ai != null && _byAi.TryGetValue(s.Ai, out var b) && b == s) _byAi.Remove(s.Ai);
            _slots.Remove(s.Id);
        }

        // ---- helpers ------------------------------------------------------------------------------

        private bool Has(HookSet h) { return (_hooks & h) != 0; }

        private void Raise(uint unit, uint target, OverrideLostReason r) { _sink?.OnOverrideLost(unit, target, r); }

        private void Fault(string what, Exception e)
        {
            _faults++;
            if (_faults <= 20 || _faults % 500 == 0)
                Log?.Invoke("[Skizzy.BDArmory] " + what + " failed (" + _faults + "): " + e.GetType().Name + ": " + e.Message);
        }

        // ---- diagnostics (tests, status) ----------------------------------------------------------

        public int UnitCount => _slots.Count;
        public int FaultCount => _faults;
        public object WmOf(uint unit) { return _slots.TryGetValue(unit, out var s) ? s.Wm : null; }
        public object AiOf(uint unit) { return _slots.TryGetValue(unit, out var s) ? s.Ai : null; }
    }
}
