using System;
using System.Collections.Generic;
using System.Globalization;

namespace Skizzy.C2.Fake
{
    /// <summary>One vessel in the fake world: its <see cref="UnitState"/> plus the BDA weapon-manager and AI state the hooks touch.</summary>
    public sealed class FakeUnit
    {
        public readonly UnitState S = new UnitState();
        public uint Id => S.Id;

        // sensors, weapons, kinematics
        public double RadarRangeM = 45000;
        /// <summary>Multiplier on other units' radar range against this one (stealth below 1).</summary>
        public double Rcs = 1;
        public double GuardRangeM = 30000;
        public double EngageRangeM = 20000;
        public double ExtendedRangeM = 30000;
        public double FireIntervalS = 4;
        public double CruiseMps = 250;
        /// <summary>Flies <see cref="UnitState.HeadingDeg"/> at <see cref="UnitState.SpeedMps"/> regardless of AI and hooks (raids, missiles).</summary>
        public bool Scripted;

        // BDA weapon manager
        public FireAuthorityMode Fire = FireAuthorityMode.Free;
        public TargetFilter Filter;
        public uint Override;
        public bool Sticky;
        public double OverrideUntil;
        public uint CurrentTarget;
        public readonly List<uint> Secondary = new List<uint>();
        public bool SecondaryOnly;
        public bool EngageMissiles;
        /// <summary>The setting before the engine first changed it (null: not changed), restored by RestoreEngageMissiles.</summary>
        public bool? EngageMissilesBaseline;
        public bool RangeMax;
        public double NextShot;

        // BDA AI
        public readonly List<GeoPoint> Waypoints = new List<GeoPoint>();
        public int WpIndex;
        public int Lap;
        public int Laps;
        public float WpRadius;
        public bool Yield;
        public float Leash;
        public GeoPoint Anchor;
        public bool HasAnchor;
        public bool PatrolEngaged;
        public uint PatrolTarget;
        public GeoPoint EngageAnchor;
        public readonly Dictionary<uint, double> PatrolCooldown = new Dictionary<uint, double>();
        public bool FlyToActive;
        public GeoPoint FlyTo;

        public bool OnRoute => Waypoints.Count > 0;
        public override string ToString() { return S.Name + " (v:" + S.Id + ")"; }
    }

    /// <summary>A shot recorded by the fake weapon manager.</summary>
    public struct FakeShot
    {
        public double Ut;
        public uint Shooter;
        public uint Target;
        public bool Guns;
        public override string ToString() { return "shot " + Shooter + "->" + Target + " at " + Ut.ToString("0.0", CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// Deterministic stepped world implementing <see cref="IC2World"/> and <see cref="IBdaControl"/> with the
    /// Overdrive hook semantics (HOOKS.md at overdrive-hooks 9fac1f3bc): F1 sticky / non-sticky and loss reasons,
    /// F3 Free/Tight/Hold, F4 filter (vessels only), F2 secondaries, F6 waypoints with events (false when the AI is
    /// inactive), F6b yield / leash / 30 s cooldown (pilot AI only; Hold never yields; Tight yields only to permitted
    /// targets), F9 launches and F10 deaths. Hook events go to <see cref="Sink"/> synchronously.
    /// </summary>
    public sealed class FakeBda : IC2World, IBdaControl
    {
        public double Now { get; set; } = 1000;
        public double BodyRadius { get; set; } = Geo.KerbinRadius;
        public HookSet Hooks { get; set; } = HookSet.All;
        public IHookEvents Sink;
        /// <summary>A shot kills its target at once (otherwise tests kill units with <see cref="Kill"/>).</summary>
        public bool KillOnShot;
        /// <summary>Seconds a leash-broken target stays out of the patrol's reach (F6b patrolLeashCooldown).</summary>
        public double PatrolLeashCooldownS = 30;

        public readonly Geo Geo;
        public readonly GeoPoint Origin = new GeoPoint(1.0, -70.0, 0);
        public readonly List<FakeUnit> List = new List<FakeUnit>();
        public readonly Dictionary<uint, FakeUnit> Units = new Dictionary<uint, FakeUnit>();
        public readonly List<FakeShot> Shots = new List<FakeShot>();
        /// <summary>Every IBdaControl call, e.g. "F1 set 3 7 sticky", for assertions and debugging.</summary>
        public readonly List<string> Calls = new List<string>();
        /// <summary>Extra detections per side (data link from platforms not modelled).</summary>
        public readonly Dictionary<string, HashSet<uint>> DataLink = new Dictionary<string, HashSet<uint>>();
        public readonly HashSet<string> Allies = new HashSet<string>();

        private uint _nextId = 1;

        public FakeBda()
        {
            Geo = new Geo(BodyRadius);
        }

        // ------------------------------------------------------------------ building the world

        public GeoPoint At(double eastKm, double northKm, double altM = 0)
        {
            var p = Geo.Offset(Origin, eastKm * 1000, northKm * 1000);
            p.Alt = altM;
            return p;
        }

        public FakeUnit Add(string name, string side, UnitClass cls, double eastKm, double northKm, double altM = double.NaN)
        {
            var u = new FakeUnit();
            var s = u.S;
            s.Id = _nextId++;
            s.Name = name;
            s.Side = side;
            s.Class = cls;
            s.Alive = true;
            s.AiActive = true;
            s.HasWeaponManager = true;
            s.GuardMode = true;
            switch (cls)
            {
                case UnitClass.Fighter:
                    u.RadarRangeM = 45000; u.CruiseMps = 250; s.MissilesMax = 4; s.HasGuns = true; u.EngageRangeM = 20000;
                    if (double.IsNaN(altM)) altM = 4000;
                    break;
                case UnitClass.Drone:
                    u.RadarRangeM = 25000; u.CruiseMps = 180; s.MissilesMax = 2; s.HasGuns = true; u.EngageRangeM = 12000;
                    if (double.IsNaN(altM)) altM = 1500;
                    break;
                case UnitClass.Bomber:
                    u.RadarRangeM = 45000; u.CruiseMps = 200; s.MissilesMax = 4; u.EngageRangeM = 60000;
                    if (double.IsNaN(altM)) altM = 3000;
                    break;
                case UnitClass.Aew:
                    u.RadarRangeM = 175000; u.CruiseMps = 160; s.HasWeaponManager = false; s.GuardMode = false;
                    if (double.IsNaN(altM)) altM = 8500;
                    break;
                case UnitClass.Carrier:
                    u.RadarRangeM = 150000; u.CruiseMps = 15; s.MissilesMax = 8; u.EngageRangeM = 40000; u.ExtendedRangeM = 60000; s.IsBase = true;
                    u.GuardRangeM = 60000;
                    break;
                case UnitClass.Ship:
                    u.RadarRangeM = 150000; u.CruiseMps = 15; s.MissilesMax = 16; u.EngageRangeM = 60000; u.ExtendedRangeM = 90000;
                    u.GuardRangeM = 90000;
                    break;
                case UnitClass.Submarine:
                    u.RadarRangeM = 20000; u.CruiseMps = 10; s.MissilesMax = 4; u.EngageRangeM = 30000;
                    if (double.IsNaN(altM)) altM = -75;
                    break;
                case UnitClass.Airfield:
                    u.RadarRangeM = 60000; u.CruiseMps = 0; s.IsBase = true; s.AiActive = false; s.HasWeaponManager = false; s.GuardMode = false;
                    break;
                case UnitClass.Missile:
                    u.RadarRangeM = 0; u.CruiseMps = 900; u.Scripted = true; s.HasWeaponManager = false; s.GuardMode = false; s.AiActive = false;
                    break;
            }
            if (double.IsNaN(altM)) altM = 0;
            s.MissilesReady = s.MissilesMax;
            s.MissileRangeM = u.EngageRangeM;
            s.Pos = At(eastKm, northKm, altM);
            if (UnitClasses.IsShip(cls) || cls == UnitClass.Submarine) { s.Home = s.Pos; s.HasHome = true; }
            List.Add(u);
            _senseDirty = true;
            Units[s.Id] = u;
            return u;
        }

        /// <summary>A missile in flight from <paramref name="shooter"/> homing on <paramref name="target"/>.</summary>
        public FakeUnit AddMissile(FakeUnit shooter, FakeUnit target, double eastKm, double northKm)
        {
            var m = Add(shooter.S.Name + " missile", shooter.S.Side, UnitClass.Missile, eastKm, northKm, 3000);
            m.S.MissileTarget = target.Id;
            m.S.SpeedMps = m.CruiseMps;
            m.S.HeadingDeg = Geo.Bearing(m.S.Pos, target.S.Pos);
            return m;
        }

        public FakeUnit Get(uint id)
        {
            FakeUnit u;
            return Units.TryGetValue(id, out u) ? u : null;
        }

        public void Kill(uint id)
        {
            var u = Get(id);
            if (u == null || !u.S.Alive) return;
            u.S.Alive = false;
            _senseDirty = true;
            Sink?.OnUnitDestroyed(id);
        }

        public void Detect(string side, uint target)
        {
            HashSet<uint> s;
            if (!DataLink.TryGetValue(side, out s)) DataLink[side] = s = new HashSet<uint>();
            s.Add(target);
        }

        public double DistKm(FakeUnit a, FakeUnit b) { return Geo.Distance(a.S.Pos, b.S.Pos) / 1000.0; }

        public int CallCount(string prefix)
        {
            int n = 0;
            for (int i = 0; i < Calls.Count; i++) if (Calls[i].StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        // ------------------------------------------------------------------ stepping

        /// <summary>Advances the world by <paramref name="dt"/> seconds: override bookkeeping, guard scan, patrol engage, movement, firing.</summary>
        public void Step(double dt)
        {
            Now += dt;
            _senseDirty = true;
            for (int i = 0; i < List.Count; i++)
            {
                var u = List[i];
                if (!u.S.Alive) continue;
                if (u.Scripted)
                {
                    MoveScripted(u, dt);
                    continue;
                }
                UpdateOverride(u);
                SelectTarget(u);
                Patrol(u);
                Move(u, dt);
                FireAt(u);
            }
            _senseDirty = true; // positions moved during the step
            Sense();
        }

        private void MoveScripted(FakeUnit u, double dt)
        {
            if (u.S.IsMissile && u.S.MissileTarget != 0)
            {
                var t = Get(u.S.MissileTarget);
                if (t != null && t.S.Alive) u.S.HeadingDeg = Geo.Bearing(u.S.Pos, t.S.Pos);
            }
            if (u.S.SpeedMps <= 0) return;
            var p = Geo.Destination(u.S.Pos, u.S.HeadingDeg, u.S.SpeedMps * dt);
            p.Alt = u.S.Pos.Alt;
            u.S.Pos = p;
        }

        private double Range(FakeUnit u) { return u.RangeMax ? Math.Max(u.EngageRangeM, u.ExtendedRangeM) : u.EngageRangeM; }

        private bool HasWeaponsFor(FakeUnit u, double d)
        {
            return (u.S.MissilesReady > 0 && d <= Range(u)) || (u.S.HasGuns && d <= 2000);
        }

        public bool Engageable(FakeUnit u, FakeUnit t)
        {
            if (t == null || !t.S.Alive || !SideDetects(u.S.Side, t.Id)) return false;
            return HasWeaponsFor(u, Geo.Distance(u.S.Pos, t.S.Pos));
        }

        /// <summary>F3 + F4: may this weapon manager fire at <paramref name="t"/>?</summary>
        public bool Permitted(FakeUnit u, FakeUnit t)
        {
            if (u.Fire == FireAuthorityMode.Hold) return false;
            if (t.S.IsMissile) return true;
            if (u.Fire == FireAuthorityMode.Tight) return t.Id == u.Override || (u.Filter != null && u.Filter.Allows(t.Id));
            return u.Filter == null || u.Filter.Allows(t.Id) || t.Id == u.Override;
        }

        private void Lose(FakeUnit u, OverrideLostReason reason, bool targetGone)
        {
            uint old = u.Override;
            if (old == 0) return;
            u.Override = 0;
            u.Sticky = false;
            u.OverrideUntil = 0;
            if (u.CurrentTarget == old) u.CurrentTarget = 0;
            Sink?.OnOverrideLost(u.Id, targetGone ? 0 : old, reason);
        }

        /// <summary>The F1 checks BDA runs at each target scan.</summary>
        private void ScanOverride(FakeUnit u)
        {
            if (u.Override == 0) return;
            var t = Get(u.Override);
            if (t == null || !t.S.Alive) { Lose(u, OverrideLostReason.TargetDestroyed, true); return; }
            if (u.OverrideUntil > 0 && Now >= u.OverrideUntil) { Lose(u, OverrideLostReason.Expired, false); return; }
            if (u.Filter != null && !t.S.IsMissile && !u.Filter.Allows(t.Id)) { Lose(u, OverrideLostReason.Filtered, false); return; }
            if (!u.Sticky && !Engageable(u, t)) Lose(u, OverrideLostReason.NotEngageable, false);
        }

        private void UpdateOverride(FakeUnit u)
        {
            if (!u.S.HasWeaponManager) return;
            ScanOverride(u);
        }

        private void SelectTarget(FakeUnit u)
        {
            if (!u.S.HasWeaponManager || !u.S.GuardMode) { u.CurrentTarget = 0; return; }
            if (u.Override != 0) { u.CurrentTarget = u.Override; return; }
            for (int i = 0; i < u.Secondary.Count; i++)
            {
                var s = Get(u.Secondary[i]);
                if (s != null && Engageable(u, s)) { u.CurrentTarget = s.Id; return; }
            }
            if (u.SecondaryOnly) { u.CurrentTarget = 0; return; }
            FakeUnit best = null;
            double bd = u.GuardRangeM;
            bool ship = !UnitClasses.IsAircraft(u.S.Class);
            for (int i = 0; i < List.Count; i++)
            {
                var t = List[i];
                if (t == u || !t.S.Alive || !AreHostile(u.S.Side, t.S.Side) || !SideDetects(u.S.Side, t.Id)) continue;
                double cd;
                if (u.PatrolCooldown.TryGetValue(t.Id, out cd) && Now < cd) continue;
                if (t.S.IsMissile) { if (!ship && !u.EngageMissiles) continue; }
                else if (u.Filter != null ? !u.Filter.Allows(t.Id) : u.Fire == FireAuthorityMode.Tight) continue;
                double d = Geo.Distance(u.S.Pos, t.S.Pos);
                if (d <= bd) { best = t; bd = d; }
            }
            u.CurrentTarget = best != null ? best.Id : 0;
        }

        private void Patrol(FakeUnit u)
        {
            if (!UnitClasses.IsAircraft(u.S.Class)) return;
            var t = Get(u.CurrentTarget);
            bool canYield = u.OnRoute && u.Yield && t != null && t.S.Alive && u.Fire != FireAuthorityMode.Hold && Permitted(u, t);
            if (canYield)
            {
                if (!u.PatrolEngaged)
                {
                    u.PatrolEngaged = true;
                    u.PatrolTarget = t.Id;
                    u.EngageAnchor = u.HasAnchor ? u.Anchor : u.Waypoints[u.WpIndex];
                    Sink?.OnPatrolEngage(u.Id, t.Id);
                }
                if (u.Leash > 0 && Geo.Distance(u.S.Pos, u.EngageAnchor) > u.Leash)
                {
                    u.PatrolCooldown[t.Id] = Now + PatrolLeashCooldownS;
                    u.PatrolEngaged = false;
                    u.CurrentTarget = 0;
                    Sink?.OnPatrolResume(u.Id, true);
                    if (u.Override == t.Id) Lose(u, OverrideLostReason.Cleared, false);
                }
            }
            else if (u.PatrolEngaged)
            {
                u.PatrolEngaged = false;
                Sink?.OnPatrolResume(u.Id, false);
            }
        }

        private void Move(FakeUnit u, double dt)
        {
            if (!u.S.AiActive || u.S.Landed || u.CruiseMps <= 0) { u.S.SpeedMps = 0; return; }
            bool air = UnitClasses.IsAircraft(u.S.Class);
            GeoPoint goal = default(GeoPoint);
            bool has = false;
            double stopAt = 0;
            var t = Get(u.CurrentTarget);
            bool chase = air && t != null && t.S.Alive && u.Fire != FireAuthorityMode.Hold;
            if (u.OnRoute && !u.PatrolEngaged)
            {
                var wp = u.Waypoints[u.WpIndex];
                double d = Geo.Distance(u.S.Pos, wp);
                if (d <= Math.Max(u.WpRadius, u.CruiseMps * dt))
                {
                    WaypointReached(u);
                    if (!u.OnRoute) { u.S.SpeedMps = 0; return; }
                    wp = u.Waypoints[u.WpIndex];
                }
                goal = wp;
                has = true;
            }
            else if (u.OnRoute && u.PatrolEngaged && chase)
            {
                goal = t.S.Pos;
                has = true;
                stopAt = Math.Min(u.EngageRangeM * 0.5, 5000);
            }
            else if (u.FlyToActive)
            {
                goal = u.FlyTo;
                has = true;
                stopAt = air ? 2500 : 200;
            }
            else if (chase)
            {
                goal = t.S.Pos;
                has = true;
                stopAt = Math.Min(u.EngageRangeM * 0.5, 5000);
            }
            if (!has) { u.S.SpeedMps = 0; return; }
            double dist = Geo.Distance(u.S.Pos, goal);
            if (dist <= stopAt) { u.S.SpeedMps = 0; return; }
            double step = Math.Min(u.CruiseMps * dt, dist - stopAt);
            u.S.HeadingDeg = Geo.Bearing(u.S.Pos, goal);
            u.S.SpeedMps = u.CruiseMps;
            var p = Geo.Destination(u.S.Pos, u.S.HeadingDeg, step);
            double alt = u.S.Pos.Alt;
            if (air && goal.Alt > 0)
            {
                double dz = goal.Alt - alt;
                double maxDz = 60 * dt;
                alt += Math.Abs(dz) <= maxDz ? dz : Math.Sign(dz) * maxDz;
            }
            p.Alt = alt;
            u.S.Pos = p;
        }

        private void WaypointReached(FakeUnit u)
        {
            int idx = u.WpIndex, lap = u.Lap;
            Sink?.OnWaypointReached(u.Id, idx, lap);
            u.WpIndex++;
            if (u.WpIndex >= u.Waypoints.Count)
            {
                u.Lap++;
                u.WpIndex = 0;
                if (u.Lap >= u.Laps)
                {
                    u.Waypoints.Clear();
                    u.PatrolEngaged = false;
                    Sink?.OnRouteCompleted(u.Id);
                }
            }
        }

        private void FireAt(FakeUnit u)
        {
            if (!u.S.HasWeaponManager || !u.S.GuardMode || u.CurrentTarget == 0 || Now < u.NextShot) return;
            var t = Get(u.CurrentTarget);
            if (t == null || !t.S.Alive || !Permitted(u, t)) return;
            double d = Geo.Distance(u.S.Pos, t.S.Pos);
            bool guns;
            if (u.S.MissilesReady > 0 && d <= Range(u)) { u.S.MissilesReady--; guns = false; }
            else if (u.S.HasGuns && d <= 2000) guns = true;
            else return;
            u.NextShot = Now + u.FireIntervalS;
            Shots.Add(new FakeShot { Ut = Now, Shooter = u.Id, Target = t.Id, Guns = guns });
            if (!guns) Sink?.OnMissileFired(u.Id, 0, t.Id);
            if (KillOnShot) Kill(t.Id);
        }

        // ------------------------------------------------------------------ IC2World

        public void GetUnits(List<UnitState> into)
        {
            for (int i = 0; i < List.Count; i++) into.Add(List[i].S);
        }

        // Sensor queries are cached per step (positions only change in Step); tests that move units or change
        // radar/RCS between steps are covered because every Step and every Add/Kill marks the cache dirty.
        private bool _senseDirty = true;
        private readonly Dictionary<uint, List<uint>> _contacts = new Dictionary<uint, List<uint>>();
        private readonly Dictionary<string, HashSet<uint>> _sideView = new Dictionary<string, HashSet<uint>>();

        /// <summary>Forces sensor recomputation (after a test changes positions, radar ranges or RCS mid-step).</summary>
        public void Invalidate() { _senseDirty = true; }

        private void Sense()
        {
            if (!_senseDirty) return;
            _senseDirty = false;
            foreach (var l in _contacts.Values) l.Clear();
            foreach (var s in _sideView.Values) s.Clear();
            for (int i = 0; i < List.Count; i++)
            {
                var u = List[i];
                List<uint> mine;
                if (!_contacts.TryGetValue(u.Id, out mine)) _contacts[u.Id] = mine = new List<uint>();
                if (!u.S.Alive || u.RadarRangeM <= 0 || u.S.Side == null) continue;
                HashSet<uint> view;
                if (!_sideView.TryGetValue(u.S.Side, out view)) _sideView[u.S.Side] = view = new HashSet<uint>();
                for (int k = 0; k < List.Count; k++)
                {
                    var t = List[k];
                    if (t == u || !t.S.Alive) continue;
                    if (Geo.Distance(u.S.Pos, t.S.Pos) <= u.RadarRangeM * t.Rcs)
                    {
                        mine.Add(t.Id);
                        view.Add(t.Id);
                    }
                }
            }
        }

        public void GetOwnContacts(uint unit, List<uint> into)
        {
            Sense();
            var u = Get(unit);
            List<uint> mine;
            if (u == null || !u.S.Alive || !_contacts.TryGetValue(unit, out mine)) return;
            into.AddRange(mine);
        }

        public bool SideDetects(string side, uint target)
        {
            HashSet<uint> link;
            if (side != null && DataLink.TryGetValue(side, out link) && link.Contains(target)) return true;
            var t = Get(target);
            if (t == null || !t.S.Alive || side == null) return false;
            Sense();
            HashSet<uint> view;
            return _sideView.TryGetValue(side, out view) && view.Contains(target);
        }

        public bool AreHostile(string a, string b)
        {
            if (a == null || b == null || a == b) return false;
            if (Allies.Count == 0) return true;
            return !Allies.Contains(a + "|" + b) && !Allies.Contains(b + "|" + a);
        }

        public int FriendliesEngaging(string side, uint target, uint exceptUnit)
        {
            int n = 0;
            for (int i = 0; i < List.Count; i++)
            {
                var u = List[i];
                if (u.Id == exceptUnit) continue;
                if (u.S.Alive && u.S.Side == side && u.CurrentTarget == target) n++;
                else if (u.S.Alive && u.S.IsMissile && u.S.Side == side && u.S.MissileTarget == target) n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ IBdaControl

        private FakeUnit Live(uint unit)
        {
            var u = Get(unit);
            return u != null && u.S.Alive ? u : null;
        }

        public void SetOverrideTarget(uint unit, uint target, float durationS, bool sticky)
        {
            Calls.Add("F1 set " + unit + " " + target + (sticky ? " sticky" : "") + (durationS > 0 ? " " + durationS.ToString("0", CultureInfo.InvariantCulture) + "s" : ""));
            var u = Live(unit);
            if (u == null || !u.S.HasWeaponManager) return;
            var t = Get(target);
            if (u.Override == target)
            {
                u.Sticky = sticky;
                u.OverrideUntil = durationS > 0 ? Now + durationS : 0;
                return;
            }
            if (u.Override != 0) Lose(u, OverrideLostReason.Replaced, false);
            if (t == null || !t.S.Alive || !SideDetects(u.S.Side, target))
            {
                // the binding cannot resolve a TargetInfo: reported as not engageable
                Sink?.OnOverrideLost(unit, target, OverrideLostReason.NotEngageable);
                return;
            }
            u.Override = target;
            u.Sticky = sticky;
            u.OverrideUntil = durationS > 0 ? Now + durationS : 0;
            ScanOverride(u); // SetOverrideTarget forces a target scan
            if (u.Override == target) u.CurrentTarget = target;
        }

        public void ClearOverrideTarget(uint unit)
        {
            Calls.Add("F1 clear " + unit);
            var u = Live(unit);
            if (u != null) Lose(u, OverrideLostReason.Cleared, false);
        }

        public void SetSecondaryTargets(uint unit, IList<uint> targets, bool only)
        {
            Calls.Add("F2 " + unit + " " + (targets == null ? "clear" : string.Join(",", targets)));
            var u = Live(unit);
            if (u == null) return;
            u.Secondary.Clear();
            if (targets != null) u.Secondary.AddRange(targets);
            u.SecondaryOnly = targets != null && only;
        }

        public void SetFireAuthority(uint unit, FireAuthorityMode mode)
        {
            Calls.Add("F3 " + unit + " " + mode);
            var u = Live(unit);
            if (u != null) u.Fire = mode;
        }

        public void SetTargetFilter(uint unit, TargetFilter filter)
        {
            Calls.Add("F4 " + unit + " " + (filter == null ? "remove" : "install"));
            var u = Live(unit);
            if (u != null) u.Filter = filter;
        }

        public bool SetWaypoints(uint unit, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps)
        {
            Calls.Add("F6 set " + unit + " " + points.Count + "pt r" + radiusM.ToString("0", CultureInfo.InvariantCulture) + " x" + laps);
            var u = Live(unit);
            if (u == null || !u.S.AiActive || points == null || points.Count == 0) return false;
            u.Waypoints.Clear();
            u.Waypoints.AddRange(points);
            u.WpIndex = 0;
            u.Lap = 0;
            u.Laps = Math.Max(1, laps);
            u.WpRadius = radiusM;
            u.FlyToActive = false;
            u.PatrolEngaged = false;
            return true;
        }

        public void ClearWaypoints(uint unit)
        {
            Calls.Add("F6 clear " + unit);
            var u = Live(unit);
            if (u == null) return;
            u.Waypoints.Clear();
            u.PatrolEngaged = false;
        }

        public void SetPatrolEngage(uint unit, bool yieldToCombat, float leashM, GeoPoint anchor, bool hasAnchor)
        {
            Calls.Add("F6b " + unit + " " + (yieldToCombat ? "yield" : "noyield") + " leash" + leashM.ToString("0", CultureInfo.InvariantCulture));
            var u = Live(unit);
            if (u == null || !UnitClasses.IsAircraft(u.S.Class)) return;
            u.Yield = yieldToCombat;
            u.Leash = leashM;
            u.Anchor = anchor;
            u.HasAnchor = hasAnchor;
        }

        public bool CommandFlyTo(uint unit, GeoPoint point)
        {
            Calls.Add("FlyTo " + unit);
            var u = Live(unit);
            if (u == null || !u.S.AiActive) return false;
            u.Waypoints.Clear();
            u.FlyToActive = true;
            u.FlyTo = point;
            return true;
        }

        public void ReleaseCommand(uint unit)
        {
            Calls.Add("Release " + unit);
            var u = Live(unit);
            if (u != null) u.FlyToActive = false;
        }

        public void SetGuardMode(uint unit, bool on)
        {
            Calls.Add("Guard " + unit + " " + on);
            var u = Live(unit);
            if (u != null && u.S.HasWeaponManager) u.S.GuardMode = on;
        }

        public void SetEngageMissiles(uint unit, bool on)
        {
            Calls.Add("Missiles " + unit + " " + on);
            var u = Live(unit);
            if (u == null) return;
            if (!u.EngageMissilesBaseline.HasValue) u.EngageMissilesBaseline = u.EngageMissiles;
            u.EngageMissiles = on;
        }

        public void RestoreEngageMissiles(uint unit)
        {
            Calls.Add("Missiles " + unit + " restore");
            var u = Live(unit);
            if (u == null || !u.EngageMissilesBaseline.HasValue) return;
            u.EngageMissiles = u.EngageMissilesBaseline.Value;
            u.EngageMissilesBaseline = null;
        }

        public void SetMissileRangeMax(uint unit, bool extended)
        {
            Calls.Add("Range " + unit + " " + extended);
            var u = Live(unit);
            if (u == null) return;
            u.RangeMax = extended;
            u.S.MissileRangeM = Range(u);
        }
    }
}
