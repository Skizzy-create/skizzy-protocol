using System;
using System.Collections.Generic;
using Skizzy.Bda;
using Skizzy.C2;

namespace Skizzy.Ksp.Tests
{
    /// <summary>A weapon manager: the hook fields with BDArmory's defaults, plus the persistent weapon settings.</summary>
    public sealed class FakeWm
    {
        public bool Live = true;
        public uint Override;
        public float Duration;
        public bool Sticky;
        public List<uint> Secondary;
        public bool SecondaryOnly;
        public FireAuthorityMode Fire = FireAuthorityMode.Free;
        public TargetFilter Filter;
        public bool Guard;
        public bool EngageMissiles = true;
        public bool[] WeaponEngageMissile = { true, false };
        public float[] MissileRange = { 5000, 8000 };
        public float[] MissileMaxStatic = { 20000, 40000 };
    }

    public sealed class FakeAi
    {
        public bool Live = true;
        public bool Active = true;
        public List<GeoPoint> Route;
        public bool Custom;
        public PatrolSettings Patrol;
        public bool Commanded;
        public GeoPoint CommandPoint;
    }

    public sealed class FakeVessel
    {
        public bool Live = true;
        public bool Loaded = true;
        public bool Packed;
        public uint Id;
        public string Name;
        public string Side;
        public UnitClass Class;
        public GeoPoint Pos;
        public FakeWm Wm;
        public FakeAi Ai;
        public bool Missile;
        public uint MissileTarget;
    }

    /// <summary>
    /// <see cref="IBdaGame"/> with BDArmory's semantics where the binding depends on them: F1 raises Replaced/Cleared
    /// synchronously, target resolution needs a loaded vessel and an enemy team. A hook the game does not have throws
    /// <see cref="MissingMethodException"/> (what a stock BDArmory would do if the binding JIT-compiled a hook call).
    /// </summary>
    public sealed class FakeGame : IBdaGame
    {
        public readonly List<FakeVessel> Vessels = new List<FakeVessel>();
        public readonly List<string> Calls = new List<string>();
        private readonly List<GameUnit> _pool = new List<GameUnit>();
        private uint _nextId = 100;

        public HookSet HookSetValue = HookSet.All;
        public double Time = 1000;
        public BdaBinding Binding;
        /// <summary>Teams that are allies (not hostile) of each other, besides same-name teams.</summary>
        public readonly HashSet<string> Allied = new HashSet<string>();

        public double Now => Time;
        public double BodyRadius => Geo.KerbinRadius;
        public HookSet Hooks => HookSetValue;
        public string Description => "FakeBDA";

        public FakeVessel Add(string name, string side, UnitClass cls, double lat = 0, double lon = 0, bool wm = true, bool ai = true)
        {
            var v = new FakeVessel { Id = _nextId++, Name = name, Side = side, Class = cls, Pos = new GeoPoint(lat, lon, 1000) };
            if (wm) v.Wm = new FakeWm();
            if (ai) v.Ai = new FakeAi();
            Vessels.Add(v);
            return v;
        }

        public FakeVessel AddMissile(string side, uint target)
        {
            var v = new FakeVessel { Id = _nextId++, Name = "AIM-120", Side = side, Class = UnitClass.Missile, Missile = true, MissileTarget = target };
            Vessels.Add(v);
            return v;
        }

        public FakeVessel Find(uint id) { return Vessels.Find(x => x.Id == id); }

        public FakeVessel VesselOfWm(object wm) { return Vessels.Find(x => ReferenceEquals(x.Wm, wm)); }

        private void Need(HookSet h, string what)
        {
            if ((HookSetValue & h) == 0) throw new MissingMethodException("stock BDArmory has no " + what);
        }

        private static string Id(object handle, FakeGame g)
        {
            foreach (var v in g.Vessels)
                if (ReferenceEquals(v.Wm, handle) || ReferenceEquals(v.Ai, handle)) return v.Id.ToString();
            return "?";
        }

        public void Scan(List<GameUnit> into)
        {
            into.Clear();
            foreach (var v in Vessels)
            {
                if (!v.Live || !v.Loaded) continue;
                if (into.Count == _pool.Count) _pool.Add(new GameUnit());
                var g = _pool[into.Count];
                into.Add(g);
                var s = g.S;
                s.Id = v.Id;
                s.Name = v.Name;
                s.Side = v.Side;
                s.Class = v.Missile ? UnitClass.Missile : v.Class;
                s.Group = UnitClassifier.CallsignGroup(v.Name);
                s.Alive = true;
                s.Pos = v.Pos;
                s.AiActive = v.Ai != null && v.Ai.Active;
                s.HasWeaponManager = v.Wm != null;
                s.GuardMode = v.Wm != null && v.Wm.Guard;
                s.IsBase = v.Class == UnitClass.Carrier || v.Class == UnitClass.Airfield;
                s.MissilesMax = 4;
                s.MissilesReady = 4;
                s.MissileRangeM = 40000;
                s.HasGuns = true;
                s.MissileTarget = v.MissileTarget;
                g.Vessel = v;
                g.Wm = v.Wm != null && v.Wm.Live ? v.Wm : null;
                g.Ai = v.Ai != null && v.Ai.Live ? v.Ai : null;
                g.Packed = v.Packed;
            }
        }

        public bool IsLive(object handle)
        {
            switch (handle)
            {
                case FakeWm w: return w.Live;
                case FakeAi a: return a.Live;
                case FakeVessel v: return v.Live;
                default: return handle != null;
            }
        }

        public void GetOwnContacts(object vessel, object wm, List<uint> into) { Need(HookSet.F5, "F5"); }
        public readonly HashSet<string> Detections = new HashSet<string>();
        public bool SideDetects(string side, uint target) { return Detections.Contains(side + ":" + target); }

        public bool AreHostile(string a, string b)
        {
            if (a == null || b == null || a == b) return false;
            return !(Allied.Contains(a) && Allied.Contains(b));
        }

        public int FriendliesEngaging(string side, uint target, object exceptWm)
        {
            int n = 0;
            foreach (var v in Vessels)
                if (v.Live && v.Side == side && v.Wm != null && v.Wm.Override == target && !ReferenceEquals(v.Wm, exceptWm)) n++;
            return n;
        }

        // F1 ----------------------------------------------------------------------------------------
        public TargetResolve SetOverride(object wm, uint target, float durationS, bool sticky)
        {
            Need(HookSet.F1, "SetOverrideTarget(TargetInfo, float, bool)");
            var w = (FakeWm)wm;
            var shooter = VesselOfWm(wm);
            var t = Find(target);
            if (t == null || !t.Live || !t.Loaded) return TargetResolve.Unresolved;
            if (!AreHostile(shooter.Side, t.Side)) return TargetResolve.NotHostile;
            Calls.Add("F1 set " + shooter.Id + " " + target);
            uint old = w.Override;
            if (old != 0 && old != target)
            {
                w.Override = 0;
                Binding?.GameOverrideLost(wm, old, (int)OverrideLostReason.Replaced);
            }
            w.Override = target;
            w.Duration = durationS;
            w.Sticky = sticky;
            return TargetResolve.Ok;
        }

        public void ClearOverride(object wm)
        {
            Need(HookSet.F1, "ClearExternalOverride");
            var w = (FakeWm)wm;
            Calls.Add("F1 clear " + Id(wm, this));
            uint old = w.Override;
            if (old == 0) return;
            w.Override = 0;
            Binding?.GameOverrideLost(wm, old, (int)OverrideLostReason.Cleared);
        }

        public bool HasOverride(object wm) { Need(HookSet.F1, "HasExternalOverride"); return ((FakeWm)wm).Override != 0; }

        /// <summary>BDArmory drops the assignment itself (scan, leash, expiry) and raises the event.</summary>
        public void BdaDropsOverride(FakeVessel v, OverrideLostReason reason)
        {
            uint old = v.Wm.Override;
            v.Wm.Override = 0;
            Binding?.GameOverrideLost(v.Wm, old, (int)reason);
        }

        // F2-F4 ------------------------------------------------------------------------------------
        public void SetSecondary(object wm, IList<uint> targets, bool only)
        {
            Need(HookSet.F2, "ExternalSecondaryTargets");
            var w = (FakeWm)wm;
            Calls.Add("F2 " + Id(wm, this) + (targets == null ? " null" : " " + targets.Count));
            w.Secondary = targets == null ? null : new List<uint>(targets);
            w.SecondaryOnly = only;
        }

        public FireAuthorityMode GetFireAuthority(object wm) { Need(HookSet.F3, "fireAuthority"); return ((FakeWm)wm).Fire; }

        public void SetFireAuthority(object wm, FireAuthorityMode mode)
        {
            Need(HookSet.F3, "fireAuthority");
            Calls.Add("F3 " + Id(wm, this) + " " + mode);
            ((FakeWm)wm).Fire = mode;
        }

        public void SetFilter(object wm, TargetFilter filter)
        {
            Need(HookSet.F4, "ExternalTargetFilter");
            Calls.Add("F4 " + Id(wm, this) + (filter == null ? " null" : " set"));
            ((FakeWm)wm).Filter = filter;
        }

        // F6, F6b ----------------------------------------------------------------------------------
        public bool SetWaypoints(object ai, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps)
        {
            Need(HookSet.F6, "SetWaypoints(6)");
            var a = (FakeAi)ai;
            if (!a.Active) return false;
            Calls.Add("F6 " + Id(ai, this) + " " + points.Count);
            a.Route = new List<GeoPoint>(points);
            a.Custom = true;
            a.Commanded = false;
            return true;
        }

        public void ClearWaypoints(object ai)
        {
            var a = (FakeAi)ai;
            Calls.Add("F6 clear " + Id(ai, this));
            a.Route = null;
            a.Custom = false;
        }

        public bool IsCustomRoute(object ai) { Need(HookSet.F6, "IsCustomWaypointRoute"); return ((FakeAi)ai).Custom; }

        public PatrolSettings GetPatrol(object ai) { Need(HookSet.F6b, "waypointsYieldToCombat"); return ((FakeAi)ai).Patrol; }

        public void SetPatrol(object ai, PatrolSettings p)
        {
            Need(HookSet.F6b, "waypointsYieldToCombat");
            Calls.Add("F6b " + Id(ai, this) + " " + p.Yield + " " + p.LeashM);
            ((FakeAi)ai).Patrol = p;
        }

        // stock ------------------------------------------------------------------------------------
        public bool FlyTo(object ai, GeoPoint point)
        {
            var a = (FakeAi)ai;
            if (!a.Active) return false;
            Calls.Add("FlyTo " + Id(ai, this));
            a.Commanded = true;
            a.CommandPoint = point;
            a.Route = null;
            a.Custom = false;
            return true;
        }

        public void ReleaseCommand(object ai)
        {
            var a = (FakeAi)ai;
            Calls.Add("Release " + Id(ai, this));
            a.Commanded = false;
            a.Route = null;
            a.Custom = false;
        }

        public void SetGuard(object wm, bool on)
        {
            Calls.Add("Guard " + Id(wm, this) + " " + on);
            ((FakeWm)wm).Guard = on;
        }

        private sealed class MissilesB { public bool Wm; public bool[] W; }
        private sealed class RangeB { public float[] R; }

        public object CaptureEngageMissiles(object wm)
        {
            var w = (FakeWm)wm;
            Calls.Add("Missiles capture " + Id(wm, this));
            return new MissilesB { Wm = w.EngageMissiles, W = (bool[])w.WeaponEngageMissile.Clone() };
        }

        public void SetEngageMissiles(object wm, bool on)
        {
            var w = (FakeWm)wm;
            Calls.Add("Missiles " + Id(wm, this) + " " + on);
            w.EngageMissiles = on;
            for (int i = 0; i < w.WeaponEngageMissile.Length; i++) w.WeaponEngageMissile[i] = on;
        }

        public void RestoreEngageMissiles(object wm, object baseline)
        {
            var w = (FakeWm)wm;
            var b = (MissilesB)baseline;
            Calls.Add("Missiles restore " + Id(wm, this));
            w.EngageMissiles = b.Wm;
            w.WeaponEngageMissile = (bool[])b.W.Clone();
        }

        public object CaptureMissileRange(object wm) { return new RangeB { R = (float[])((FakeWm)wm).MissileRange.Clone() }; }

        public void ExtendMissileRange(object wm)
        {
            var w = (FakeWm)wm;
            Calls.Add("Range extend " + Id(wm, this));
            for (int i = 0; i < w.MissileRange.Length; i++) w.MissileRange[i] = Math.Max(w.MissileRange[i], w.MissileMaxStatic[i]);
        }

        public void RestoreMissileRange(object wm, object baseline)
        {
            Calls.Add("Range restore " + Id(wm, this));
            ((FakeWm)wm).MissileRange = (float[])((RangeB)baseline).R.Clone();
        }
    }

    /// <summary>Records the hook events the binding forwards.</summary>
    public sealed class EventLog : IHookEvents
    {
        public readonly List<string> Items = new List<string>();
        public void OnOverrideLost(uint unit, uint target, OverrideLostReason reason) { Items.Add("lost " + unit + " " + target + " " + reason); }
        public void OnUnitDestroyed(uint unit) { Items.Add("destroyed " + unit); }
        public void OnTargetDetected(uint target, string side) { Items.Add("detected " + target + " " + side); }
        public void OnTargetLost(uint target, string side) { Items.Add("targetlost " + target + " " + side); }
        public void OnWaypointReached(uint unit, int index, int lap) { Items.Add("wp " + unit + " " + index + " " + lap); }
        public void OnRouteCompleted(uint unit) { Items.Add("done " + unit); }
        public void OnPatrolEngage(uint unit, uint target) { Items.Add("engage " + unit + " " + target); }
        public void OnPatrolResume(uint unit, bool leashBroke) { Items.Add("resume " + unit + " " + leashBroke); }
        public void OnMissileFired(uint shooter, uint missile, uint target) { Items.Add("fired " + shooter + " " + missile + " " + target); }
    }
}
