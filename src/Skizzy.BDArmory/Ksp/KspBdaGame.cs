using System;
using System.Collections.Generic;
using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;
using Skizzy.C2;
using UnityEngine;

namespace Skizzy.Bda.Ksp
{
    /// <summary>
    /// <see cref="IBdaGame"/> over KSP and BDArmory. Only stock BDArmory members are used here; hook members live in
    /// <c>HookF*</c> and are only reached when the probe found them (the binding never calls a missing hook).
    /// Per-tick work is pooled: unit records, side detection sets, secondary-target providers and filter delegates
    /// are reused, and the expensive per-vessel facts (class, armament) are cached and refreshed every few seconds.
    /// </summary>
    public sealed partial class KspBdaGame : IBdaGame
    {
        private sealed class VesselInfo
        {
            public int Parts = -1;
            public double ClassAt = double.NegativeInfinity, ArmsAt = double.NegativeInfinity;
            public string Name, Group;
            public Classification Cls;
            public int Missiles, MissilesMax;
            public double RangeM;
            public bool Guns;
            public GeoPoint Home;
            public bool HasHome;
            public int SeenStamp;
        }

        private sealed class SideSet
        {
            public readonly HashSet<uint> Ids = new HashSet<uint>();
            public int Stamp = -1;
        }

        private sealed class SecondaryProvider
        {
            public readonly List<uint> Ids = new List<uint>();
            public readonly List<TargetInfo> Buf = new List<TargetInfo>();
            public Func<MissileFire, IList<TargetInfo>> Fn;
        }

        private sealed class FilterHolder
        {
            public TargetFilter Filter;
            public Func<MissileFire, TargetInfo, bool> Fn;
        }

        private sealed class MissilesBaseline
        {
            public bool Wm;
            public readonly List<EngageableWeapon> Weapons = new List<EngageableWeapon>();
            public readonly List<bool> Values = new List<bool>();
        }

        private sealed class RangeBaseline
        {
            public readonly List<MissileBase> Missiles = new List<MissileBase>();
            public readonly List<float> Values = new List<float>();
        }

        public const double ClassifyEveryS = 10;
        public const double ArmsEveryS = 2;

        private readonly HookSet _hooks;
        private readonly string _description;
        private readonly List<GameUnit> _pool = new List<GameUnit>(64);
        private readonly Dictionary<Vessel, VesselInfo> _info = new Dictionary<Vessel, VesselInfo>();
        private readonly List<Vessel> _infoGone = new List<Vessel>();
        private readonly Dictionary<string, SideSet> _sides = new Dictionary<string, SideSet>();
        private readonly Dictionary<MissileFire, SecondaryProvider> _secondary = new Dictionary<MissileFire, SecondaryProvider>();
        private readonly Dictionary<MissileFire, FilterHolder> _filters = new Dictionary<MissileFire, FilterHolder>();
        private readonly List<Vector3> _wp = new List<Vector3>();
        private readonly List<float> _speeds = new List<float>();
        private int _stamp;
        private int _scanFaults;

        public Action<string> Log;

        public KspBdaGame(HookSet hooks, string bdaVersion)
        {
            _hooks = hooks;
            _description = "BDArmory " + (bdaVersion ?? "?") + ", " + HookProbe.Describe(hooks);
        }

        public double Now => Planetarium.GetUniversalTime();
        public double BodyRadius => FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.Radius : Geo.KerbinRadius;
        public HookSet Hooks => _hooks;
        public string Description => _description;

        public bool IsLive(object handle)
        {
            if (handle is UnityEngine.Object o) return o != null;
            return handle != null;
        }

        // ---- scan ---------------------------------------------------------------------------------

        public void Scan(List<GameUnit> into)
        {
            into.Clear();
            _stamp++;
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return;
            double now = Now;
            for (int i = 0; i < loaded.Count; i++)
            {
                var v = loaded[i];
                if (v == null || v.state == Vessel.State.DEAD) continue;
                int before = into.Count;
                try { Fill(v, into, now); }
                catch (Exception e)
                {
                    if (++_scanFaults <= 20) Log?.Invoke("[Skizzy.BDArmory] scan of " + v.vesselName + " failed: " + e.GetType().Name + ": " + e.Message);
                }
                if (into.Count == before) _last.Remove(v.persistentId); // loaded and no longer a unit (lost its weapon manager, missile ended)
            }
            ScanUnloaded(into);
            // forget vessels that are no longer loaded (every few hundred scans)
            if ((_stamp & 255) == 0)
            {
                foreach (var kv in _info)
                    if (kv.Key == null || _stamp - kv.Value.SeenStamp > 255) _infoGone.Add(kv.Key);
                for (int i = 0; i < _infoGone.Count; i++) _info.Remove(_infoGone[i]);
                _infoGone.Clear();
                foreach (var kv in _last)
                    if (_stamp - Math.Max(kv.Value.Stamp, kv.Value.UnloadedStamp) > 255) _lastGone.Add(kv.Key);
                for (int i = 0; i < _lastGone.Count; i++) _last.Remove(_lastGone[i]);
                _lastGone.Clear();
                SweepMissiles();
            }
        }

        // the last state of every unit seen loaded: a vessel KSP unloads (out of physics range at a 100 km fight) still
        // exists, so it stays listed (position from KSP, on rails, no weapon manager) instead of reading as destroyed
        private sealed class LastSeen
        {
            public readonly Skizzy.C2.UnitState S = new Skizzy.C2.UnitState();
            public int Stamp, UnloadedStamp;
        }

        private readonly Dictionary<uint, LastSeen> _last = new Dictionary<uint, LastSeen>();
        private readonly List<uint> _lastGone = new List<uint>();

        private void Remember(Skizzy.C2.UnitState s)
        {
            if (!_last.TryGetValue(s.Id, out var l)) _last[s.Id] = l = new LastSeen();
            l.S.CopyFrom(s);
            l.Stamp = _stamp;
        }

        /// <summary>Units seen loaded before that KSP has unloaded but not destroyed (FlightGlobals.Vessels, not loaded).</summary>
        private void ScanUnloaded(List<GameUnit> into)
        {
            if (_last.Count == 0) return;
            var all = FlightGlobals.Vessels;
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v == null || v.loaded || v.state == Vessel.State.DEAD) continue;
                if (!_last.TryGetValue(v.persistentId, out var l) || l.Stamp == _stamp) continue;
                l.UnloadedStamp = _stamp;
                var g = Next(into);
                g.S.CopyFrom(l.S);
                g.S.Pos = new GeoPoint(v.latitude, v.longitude, v.altitude);
                g.S.SpeedMps = v.srfSpeed;
                g.S.AiActive = false;
                g.Vessel = v;
                g.Wm = null;
                g.Ai = null;
                g.Packed = true;
            }
        }

        private GameUnit Next(List<GameUnit> into)
        {
            if (into.Count == _pool.Count) _pool.Add(new GameUnit());
            var g = _pool[into.Count];
            into.Add(g);
            return g;
        }

        private void Fill(Vessel v, List<GameUnit> into, double now)
        {
            var ac = v.ActiveController();
            MissileFire wm = ac != null ? ac.WM : null;
            IBDAIControl ai = ac != null ? ac.AI : null;
            if (wm == null && (ai == null || (ai as PartModule) == null))
            {
                FillMissile(v, into);
                return;
            }
            var info = InfoOf(v, wm, ai, now);
            var g = Next(into);
            var s = g.S;
            s.Id = v.persistentId;
            s.Name = v.vesselName;
            s.Side = wm != null ? wm.teamString : (ai.WeaponManager != null ? ai.WeaponManager.teamString : "Neutral");
            s.Class = info.Cls.Class;
            s.IsBase = info.Cls.IsBase;
            s.Group = info.Group;
            s.Alive = true;
            s.Pos = new GeoPoint(v.latitude, v.longitude, v.altitude);
            s.HeadingDeg = Heading(v);
            s.SpeedMps = v.srfSpeed;
            s.AiActive = ai != null && ai.pilotEnabled;
            s.Landed = UnitClasses.IsAircraft(info.Cls.Class) && v.LandedOrSplashed;
            s.HasWeaponManager = wm != null;
            s.GuardMode = wm != null && wm.guardMode;
            if (now - info.ArmsAt >= ArmsEveryS) Arms(v, info, now);
            s.MissilesReady = info.Missiles;
            s.MissilesMax = info.MissilesMax;
            s.MissileRangeM = info.RangeM;
            s.HasGuns = info.Guns;
            s.Fuel = double.NaN;
            s.HomeBase = 0;
            if (!info.HasHome && UnitClasses.IsShip(info.Cls.Class))
            {
                info.Home = s.Pos;
                info.HasHome = true;
            }
            s.Home = info.Home;
            s.HasHome = info.HasHome;
            s.MissileTarget = 0;
            s.Launcher = 0;
            g.Vessel = v;
            g.Wm = wm;
            g.Ai = ai as PartModule;
            g.Packed = v.packed;
            Remember(s);
        }

        private void FillMissile(Vessel v, List<GameUnit> into)
        {
            var mb = VesselModuleRegistry.GetModule<MissileBase>(v);
            if (mb == null || !mb.HasFired) return;
            if (mb.HasExploded)
            {
                // A modular missile that detonates through its proximity action group (or a warhead that went off without
                // the guidance part) leaves its body flying: BDArmory sets HasExploded but destroys no parts. The missile
                // ended here, not when the wreck finally dies: record the end now and stop listing it.
                MissileVesselWillDestroy(v);
                _last.Remove(v.persistentId);
                return;
            }
            var g = Next(into);
            var s = g.S;
            s.Id = v.persistentId;
            s.Name = v.vesselName;
            s.Side = mb.Team != null ? mb.Team.Name : "Neutral";
            s.Class = UnitClass.Missile;
            s.IsBase = false;
            s.Group = null;
            s.Alive = true;
            s.Pos = new GeoPoint(v.latitude, v.longitude, v.altitude);
            s.HeadingDeg = Heading(v);
            s.SpeedMps = v.srfSpeed;
            s.AiActive = false;
            s.Landed = false;
            s.HasWeaponManager = false;
            s.GuardMode = false;
            s.MissilesReady = s.MissilesMax = 0;
            s.MissileRangeM = 0;
            s.HasGuns = false;
            s.Fuel = double.NaN;
            s.HomeBase = 0;
            s.HasHome = false;
            var t = mb.targetVessel;
            s.MissileTarget = t != null && t.Vessel != null ? t.Vessel.persistentId : 0;
            var src = mb.SourceVessel;
            s.Launcher = src != null ? src.persistentId : 0;
            FillMissileFacts(v, mb, s);
            g.Vessel = v;
            g.Wm = null;
            g.Ai = null;
            g.Packed = v.packed;
            Remember(s);
        }

        private static double Heading(Vessel v)
        {
            var body = v.mainBody;
            if (body == null) return 0;
            Vector3d up = (v.CoMD - body.position).normalized;
            Vector3d north = Vector3d.Exclude(up, body.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north);
            Vector3d fwd = v.srfSpeed > 2 ? (Vector3d)v.srf_velocity : (v.ReferenceTransform != null ? (Vector3d)v.ReferenceTransform.up : north);
            fwd = Vector3d.Exclude(up, fwd);
            double h = Math.Atan2(Vector3d.Dot(fwd, east), Vector3d.Dot(fwd, north)) * (180.0 / Math.PI);
            return h < 0 ? h + 360 : h;
        }

        private VesselInfo InfoOf(Vessel v, MissileFire wm, IBDAIControl ai, double now)
        {
            if (!_info.TryGetValue(v, out var info))
            {
                info = new VesselInfo();
                _info[v] = info;
            }
            info.SeenStamp = _stamp;
            int parts = v.parts != null ? v.parts.Count : 0;
            if (info.Parts != parts || info.Name != v.vesselName || now - info.ClassAt >= ClassifyEveryS)
            {
                if (info.Parts != parts) info.ArmsAt = double.NegativeInfinity;
                if (now - info.ArmsAt >= ArmsEveryS) Arms(v, info, now);
                info.Parts = parts;
                info.ClassAt = now;
                if (info.Name != v.vesselName)
                {
                    info.Name = v.vesselName;
                    info.Group = UnitClassifier.CallsignGroup(info.Name);
                }
                info.Cls = UnitClassifier.Classify(Features(v, ai, info));
            }
            return info;
        }

        private static VesselFeatures Features(Vessel v, IBDAIControl ai, VesselInfo info)
        {
            var f = new VesselFeatures
            {
                Name = v.vesselName,
                Crewed = v.GetCrewCount() > 0,
                MassT = v.GetTotalMass(),
                Armed = info.Missiles > 0 || info.Guns,
                HasRadar = VesselModuleRegistry.GetModuleCount<ModuleRadar>(v) > 0,
                Landed = v.Landed,
                Splashed = v.Splashed,
            };
            if (ai != null && (ai as PartModule) != null)
            {
                switch (ai.aiType)
                {
                    case AIType.PilotAI: f.PilotAi = true; break;
                    case AIType.VTOLAI: f.VtolAi = true; break;
                    case AIType.OrbitalAI: f.OrbitalAi = true; break;
                    case AIType.SurfaceAI:
                        f.SurfaceAi = true;
                        if (ai is BDModuleSurfaceAI sai)
                        {
                            f.Submarine = sai.SurfaceType == AIUtils.VehicleMovementType.Submarine;
                            f.Stationary = sai.SurfaceType == AIUtils.VehicleMovementType.Stationary;
                            f.LandVehicle = sai.SurfaceType == AIUtils.VehicleMovementType.Land;
                        }
                        break;
                }
            }
            if (!f.PilotAi && !f.VtolAi) f.CarrierParts = CarrierParts(v);
            return f;
        }

        /// <summary>Parts with catapult, arrestor-wire, hangar or deck-elevator modules (ACA, Hangar and similar mods).</summary>
        private static int CarrierParts(Vessel v)
        {
            int n = 0;
            var parts = v.parts;
            if (parts == null) return 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                var mods = p.Modules;
                for (int j = 0; j < mods.Count; j++)
                {
                    var name = mods[j].moduleName;
                    if (name == null) continue;
                    if (name.IndexOf("Catapult", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Arrest", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Hangar", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Elevator", StringComparison.OrdinalIgnoreCase) >= 0)
                    { n++; break; }
                }
            }
            return n;
        }

        /// <summary>Offensive missiles (AAM for aircraft; SAM/ASM/torpedo for the rest), counted with rail and magazine ammo.</summary>
        private void Arms(Vessel v, VesselInfo info, double now)
        {
            info.ArmsAt = now;
            bool aircraft = UnitClasses.IsAircraft(info.Cls.Class);
            int ready = 0;
            double range = 0;
            var missiles = VesselModuleRegistry.GetModules<MissileBase>(v);
            if (missiles != null)
            {
                for (int i = 0; i < missiles.Count; i++)
                {
                    var mb = missiles[i];
                    if (mb == null || mb.HasFired || mb.launched) continue;
                    bool offensive = aircraft ? mb.engageAir : (mb.engageAir || mb.engageGround || mb.engageSLW);
                    if (!offensive) continue;
                    var rail = mb.reloadableRail;
                    ready += rail != null ? Math.Max(0, rail.ammoCount) : 1;
                    if (mb.engageRangeMax > range) range = mb.engageRangeMax;
                }
            }
            info.Missiles = ready;
            if (ready > info.MissilesMax) info.MissilesMax = ready;
            info.RangeM = range;
            info.Guns = VesselModuleRegistry.GetModuleCount<ModuleWeapon>(v) > 0;
        }

        /// <summary>F9: a missile left this weapon manager; recount its missiles at the next scan.</summary>
        public void MarkArmsDirty(MissileFire wm)
        {
            if (wm != null && wm.vessel != null && _info.TryGetValue(wm.vessel, out var info)) info.ArmsAt = double.NegativeInfinity;
        }

        // ---- world queries ----------------------------------------------------------------------

        /// <summary>The target record of a loaded vessel (null when not loaded or BDA has none).</summary>
        public static TargetInfo TargetOf(uint id)
        {
            if (id == 0 || !FlightGlobals.FindVessel(id, out Vessel v) || v == null || !v.loaded) return null;
            return v.gameObject.TryGetComponent<TargetInfo>(out var ti) ? ti : null;
        }

        public void GetOwnContacts(object vessel, object wm, List<uint> into)
        {
            var mf = wm as MissileFire;
            var own = vessel as Vessel;
            if (mf == null || own == null || mf.vesselRadarData == null) return;
            HookF5.OwnContacts(mf.vesselRadarData, own, into);
        }

        public bool SideDetects(string side, uint target)
        {
            if (side == null) return false;
            if (!_sides.TryGetValue(side, out var set))
            {
                set = new SideSet();
                _sides[side] = set;
            }
            if (set.Stamp != _stamp)
            {
                set.Stamp = _stamp;
                set.Ids.Clear();
                var list = BDATargetManager.TargetList(BDTeam.Get(side));
                for (int i = 0; i < list.Count; i++)
                {
                    var ti = list[i];
                    if (ti == null) continue;
                    var tv = ti.Vessel;
                    if (tv != null) set.Ids.Add(tv.persistentId);
                }
            }
            return set.Ids.Contains(target);
        }

        public bool AreHostile(string sideA, string sideB)
        {
            if (sideA == null || sideB == null || sideA == sideB) return false;
            return BDTeam.Get(sideA).IsEnemy(BDTeam.Get(sideB));
        }

        public int FriendliesEngaging(string side, uint target, object exceptWm)
        {
            var ti = TargetOf(target);
            if (ti == null || side == null) return 0;
            int n = ti.NumFriendliesEngaging(BDTeam.Get(side));
            var mf = exceptWm as MissileFire;
            if (mf != null && mf.currentTarget == ti && mf.teamString == side) n--;
            return n < 0 ? 0 : n;
        }

        // ---- F1-F6b -------------------------------------------------------------------------------

        public TargetResolve SetOverride(object wm, uint target, float durationS, bool sticky)
        {
            var mf = wm as MissileFire;
            if (mf == null) return TargetResolve.Unresolved;
            var ti = TargetOf(target);
            if (ti == null) return TargetResolve.Unresolved;
            if (ti.Team != null && !mf.Team.IsEnemy(ti.Team)) return TargetResolve.NotHostile;
            HookF1.Set(mf, ti, durationS, sticky);
            return TargetResolve.Ok;
        }

        public void ClearOverride(object wm)
        {
            if (wm is MissileFire mf && mf != null) HookF1.Clear(mf);
        }

        public bool HasOverride(object wm)
        {
            return wm is MissileFire mf && mf != null && HookF1.Has(mf);
        }

        public void SetSecondary(object wm, IList<uint> targets, bool only)
        {
            var mf = wm as MissileFire;
            if (mf == null) return;
            if (targets == null)
            {
                HookF2.Remove(mf);
                _secondary.Remove(mf);
                return;
            }
            if (!_secondary.TryGetValue(mf, out var p))
            {
                p = new SecondaryProvider();
                var captured = p;
                p.Fn = m => Resolve(captured);
                _secondary[mf] = p;
            }
            p.Ids.Clear();
            for (int i = 0; i < targets.Count; i++) p.Ids.Add(targets[i]);
            HookF2.Install(mf, p.Fn, only);
        }

        // Called by BDArmory once per target scan: allocation-free.
        private static IList<TargetInfo> Resolve(SecondaryProvider p)
        {
            p.Buf.Clear();
            for (int i = 0; i < p.Ids.Count; i++)
            {
                var ti = TargetOf(p.Ids[i]);
                if (ti != null) p.Buf.Add(ti);
            }
            return p.Buf;
        }

        public FireAuthorityMode GetFireAuthority(object wm)
        {
            var mf = wm as MissileFire;
            return mf != null ? (FireAuthorityMode)HookF3.Get(mf) : FireAuthorityMode.Free;
        }

        public void SetFireAuthority(object wm, FireAuthorityMode mode)
        {
            if (wm is MissileFire mf && mf != null) HookF3.Set(mf, (int)mode);
        }

        public void SetFilter(object wm, TargetFilter filter)
        {
            var mf = wm as MissileFire;
            if (mf == null) return;
            if (filter == null)
            {
                HookF4.Set(mf, null);
                _filters.Remove(mf);
                return;
            }
            if (!_filters.TryGetValue(mf, out var h) || !ReferenceEquals(h.Filter, filter))
            {
                var f = filter;
                h = new FilterHolder { Filter = f, Fn = (m, t) => t != null && t.Vessel != null && f.Allows(t.Vessel.persistentId) };
                _filters[mf] = h;
            }
            HookF4.Set(mf, h.Fn);
        }

        public bool SetWaypoints(object ai, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps)
        {
            var g = ai as BDGenericAIBase;
            if (g == null || points == null) return false;
            _wp.Clear();
            _speeds.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                _wp.Add(new Vector3((float)p.Lat, (float)p.Lon, (float)p.Alt));
                if (maxSpeedMps > 0) _speeds.Add(maxSpeedMps);
            }
            return HookF6.Set(g, _wp, radiusM, laps, maxSpeedMps > 0 ? _speeds : null);
        }

        public void ClearWaypoints(object ai)
        {
            var g = ai as BDGenericAIBase;
            if (g != null) g.ClearWaypoints();
        }

        public bool IsCustomRoute(object ai)
        {
            var g = ai as BDGenericAIBase;
            return g != null && HookF6.IsCustom(g);
        }

        public PatrolSettings GetPatrol(object ai)
        {
            var g = ai as BDGenericAIBase;
            if (g == null) return default(PatrolSettings);
            HookF6b.Get(g, out bool y, out float leash, out Vector3d a);
            bool has = a != Vector3d.zero;
            return new PatrolSettings { Yield = y, LeashM = leash, HasAnchor = has, Anchor = has ? new GeoPoint(a.x, a.y, a.z) : default(GeoPoint) };
        }

        public void SetPatrol(object ai, PatrolSettings p)
        {
            var g = ai as BDGenericAIBase;
            if (g == null) return;
            HookF6b.Set(g, p.Yield, p.LeashM, p.HasAnchor ? new Vector3d(p.Anchor.Lat, p.Anchor.Lon, p.Anchor.Alt) : Vector3d.zero);
        }

        // ---- stock ------------------------------------------------------------------------------

        public bool FlyTo(object ai, GeoPoint point)
        {
            var c = ai as IBDAIControl;
            if (c == null || (ai as PartModule) == null || !c.pilotEnabled) return false;
            c.CommandFlyTo(new Vector3((float)point.Lat, (float)point.Lon, (float)point.Alt));
            return true;
        }

        public void ReleaseCommand(object ai)
        {
            var c = ai as IBDAIControl;
            if (c != null && (ai as PartModule) != null) c.ReleaseCommand(true, false);
        }

        public void SetGuard(object wm, bool on)
        {
            if (wm is MissileFire mf && mf != null && mf.guardMode != on) mf.ToggleGuardMode();
        }

        public object CaptureEngageMissiles(object wm)
        {
            var mf = wm as MissileFire;
            if (mf == null) return null;
            var b = new MissilesBaseline { Wm = mf.engageMissile };
            var weapons = VesselModuleRegistry.GetModules<IBDWeapon>(mf.vessel);
            if (weapons != null)
                for (int i = 0; i < weapons.Count; i++)
                {
                    if (weapons[i] is EngageableWeapon ew && ew != null)
                    {
                        b.Weapons.Add(ew);
                        b.Values.Add(ew.engageMissile);
                    }
                }
            return b;
        }

        public void SetEngageMissiles(object wm, bool on)
        {
            var mf = wm as MissileFire;
            if (mf == null) return;
            mf.engageMissile = on;
            var weapons = VesselModuleRegistry.GetModules<IBDWeapon>(mf.vessel);
            if (weapons != null)
                for (int i = 0; i < weapons.Count; i++)
                    if (weapons[i] is EngageableWeapon ew && ew != null) ew.engageMissile = on;
            mf.UpdateList();
        }

        public void RestoreEngageMissiles(object wm, object baseline)
        {
            var mf = wm as MissileFire;
            var b = baseline as MissilesBaseline;
            if (mf == null || b == null) return;
            mf.engageMissile = b.Wm;
            for (int i = 0; i < b.Weapons.Count; i++)
                if (b.Weapons[i] != null) b.Weapons[i].engageMissile = b.Values[i];
            mf.UpdateList();
        }

        public object CaptureMissileRange(object wm)
        {
            var mf = wm as MissileFire;
            if (mf == null) return null;
            var b = new RangeBaseline();
            var missiles = VesselModuleRegistry.GetModules<MissileBase>(mf.vessel);
            if (missiles != null)
                for (int i = 0; i < missiles.Count; i++)
                {
                    var mb = missiles[i];
                    if (mb == null || mb.HasFired) continue;
                    b.Missiles.Add(mb);
                    b.Values.Add(mb.engageRangeMax);
                }
            return b;
        }

        public void ExtendMissileRange(object wm)
        {
            var mf = wm as MissileFire;
            if (mf == null) return;
            var missiles = VesselModuleRegistry.GetModules<MissileBase>(mf.vessel);
            if (missiles == null) return;
            for (int i = 0; i < missiles.Count; i++)
            {
                var mb = missiles[i];
                if (mb == null || mb.HasFired) continue;
                if (mb.maxStaticLaunchRange > mb.engageRangeMax) mb.engageRangeMax = mb.maxStaticLaunchRange;
            }
        }

        public void RestoreMissileRange(object wm, object baseline)
        {
            var b = baseline as RangeBaseline;
            if (b == null) return;
            for (int i = 0; i < b.Missiles.Count; i++)
            {
                var mb = b.Missiles[i];
                if (mb != null && !mb.HasFired) mb.engageRangeMax = b.Values[i];
            }
        }

        /// <summary>Drops cached delegates and side sets (scene end).</summary>
        public void Clear()
        {
            _secondary.Clear();
            _filters.Clear();
            _sides.Clear();
            _info.Clear();
            ClearMissiles();
        }
    }
}
