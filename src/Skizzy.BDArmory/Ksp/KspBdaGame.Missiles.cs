using System;
using System.Collections.Generic;
using BDArmory.Extensions;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;
using Skizzy.C2.Tactical;
using UnityEngine;

namespace Skizzy.Bda.Ksp
{
    /// <summary>
    /// M2 missile facts for the ledger: weapon name and funds value (KSP part costs over every part of the missile vessel,
    /// once per missile), and how each missile ended, read from BDArmory's own MissileBase state when KSP destroys the
    /// missile vessel (HasExploded, HasMissed, distance from the detonation to the target's nearest part) plus BDArmory
    /// competition scoring when it runs (Overdrive F10 OnHit / OnDeath: which vessel a missile explosion actually damaged,
    /// and kills). Stock BDArmory members only, except the F10 events (HookScores, subscribed only when the probe found F10).
    /// Main thread; work happens on missile events, the per-scan cost is one dictionary lookup per missile.
    /// </summary>
    public sealed partial class KspBdaGame : IMissileOutcomes
    {
        private sealed class MslInfo
        {
            public MissileBase Mb;
            public string Weapon;
            public double Cost = double.NaN;
            public string Team;
            public string LauncherName;
            public Vessel Target;
            public float Blast;
            public int SeenStamp;
            public bool Ended;
            public double EndUt;
            public bool Exploded, Missed, NearTarget;
            public uint TargetId;
            public uint InterceptedBy;
            public uint VictimId; // nearest enemy unit inside the blast when it was not the target (GPS / cruise missiles have none)
        }

        private struct Detonation
        {
            public double Ut;
            public double Lat, Lon, Alt;
            public string Team;
            public uint Id;
            public float Blast;
        }

        private struct ScoredHit
        {
            public double Ut;
            public string Attacker, Victim;
        }

        private readonly Dictionary<uint, MslInfo> _msl = new Dictionary<uint, MslInfo>();
        private readonly List<uint> _mslGone = new List<uint>();
        private readonly Detonation[] _det = new Detonation[64];
        private int _detNext;
        private readonly List<ScoredHit> _hits = new List<ScoredHit>();
        private readonly List<KillReport> _killQueue = new List<KillReport>();

        /// <summary>Seconds after a missile detonation in which a non-exploding enemy missile destroyed nearby counts as intercepted by it.</summary>
        public const double InterceptWindowS = 0.75;

        private MslInfo MissileInfo(Vessel v, MissileBase mb)
        {
            if (_msl.TryGetValue(v.persistentId, out var m) && ReferenceEquals(m.Mb, mb)) return m;
            m = new MslInfo { Mb = mb };
            try { m.Weapon = !string.IsNullOrEmpty(mb.shortName) ? mb.shortName : mb.part != null && mb.part.partInfo != null ? mb.part.partInfo.title : v.vesselName; }
            catch (Exception) { m.Weapon = v.vesselName; }
            m.Cost = PartsCost(v);
            try { m.Blast = mb.GetBlastRadius(); }
            catch (Exception) { m.Blast = 0; }
            m.Team = mb.Team != null ? mb.Team.Name : null;
            var src = mb.SourceVessel;
            m.LauncherName = src != null ? src.vesselName : null;
            _msl[v.persistentId] = m;
            return m;
        }

        /// <summary>KSP's own price of a vessel: catalog cost plus module costs of every part (MissileCosts, shared with the fakes).</summary>
        private static double PartsCost(Vessel v)
        {
            var parts = v.parts;
            if (parts == null || parts.Count == 0) return double.NaN;
            double sum = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.partInfo == null) continue;
                float catalog = p.partInfo.cost;
                float modules = 0;
                try { modules = p.GetModuleCosts(catalog); }
                catch (Exception) { }
                sum += MissileCosts.Part(catalog, modules);
            }
            return sum;
        }

        /// <summary>Called from FillMissile on every scan: missile facts into the unit record.</summary>
        private void FillMissileFacts(Vessel v, MissileBase mb, Skizzy.C2.UnitState s)
        {
            var m = MissileInfo(v, mb);
            m.SeenStamp = _stamp;
            var t = mb.targetVessel;
            m.Target = t != null ? t.Vessel : null;
            if (m.LauncherName == null) { var src = mb.SourceVessel; if (src != null) m.LauncherName = src.vesselName; }
            s.Weapon = m.Weapon;
            s.Cost = m.Cost;
            s.ClimbMps = v.verticalSpeed;
        }

        /// <summary>
        /// KSP is about to destroy a vessel (GameEvents.onVesselWillDestroy). For a missile we follow: read how it ended while
        /// its state and position are still valid.
        /// </summary>
        public void MissileVesselWillDestroy(Vessel v)
        {
            if (v == null || !_msl.TryGetValue(v.persistentId, out var m) || m.Ended) return;
            m.Ended = true;
            m.EndUt = Now;
            var mb = m.Mb;
            try
            {
                m.Exploded = mb.HasExploded;
                m.Missed = mb.HasMissed;
            }
            catch (Exception) { }
            var body = v.mainBody;
            Vector3d pos = v.CoMD;
            var tgt = m.Target;
            if (tgt != null)
            {
                m.TargetId = tgt.persistentId;
                m.NearTarget = NearestPartM(tgt, pos) <= Math.Max(m.Blast * 1.1, 20.0);
            }
            if (m.Exploded && !m.NearTarget) m.VictimId = EnemyInBlast(v, pos, m);
            if (m.Exploded)
            {
                _det[_detNext] = new Detonation { Ut = m.EndUt, Lat = v.latitude, Lon = v.longitude, Alt = v.altitude, Team = m.Team, Id = v.persistentId, Blast = m.Blast };
                _detNext = (_detNext + 1) % _det.Length;
            }
            else if (body != null)
            {
                // destroyed without exploding: an enemy missile that just detonated close by intercepted it
                double best = double.MaxValue;
                for (int i = 0; i < _det.Length; i++)
                {
                    var d = _det[i];
                    if (d.Id == 0 || m.EndUt - d.Ut > InterceptWindowS || m.EndUt < d.Ut || d.Team == m.Team) continue;
                    double dist = (body.GetWorldSurfacePosition(d.Lat, d.Lon, d.Alt) - pos).magnitude;
                    if (dist <= d.Blast * 1.5 + 30 && dist < best) { best = dist; m.InterceptedBy = d.Id; }
                }
            }
        }

        /// <summary>
        /// The nearest loaded enemy unit (a vessel with a weapon manager on another team, not a missile) with a part inside
        /// the missile's blast at detonation, or 0. Many missiles (GPS, cruise, unguided) have no target vessel at all.
        /// </summary>
        private static uint EnemyInBlast(Vessel missile, Vector3d pos, MslInfo m)
        {
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return 0;
            double radius = Math.Max(m.Blast * 1.1, 20.0), best = double.MaxValue;
            uint id = 0;
            for (int i = 0; i < loaded.Count; i++)
            {
                var w = loaded[i];
                if (w == null || w == missile || w.state == Vessel.State.DEAD) continue;
                if ((w.CoMD - pos).sqrMagnitude > (radius + 300) * (radius + 300)) continue; // no vessel part is that far from its CoM
                try
                {
                    var ac = w.ActiveController();
                    var wm = ac != null ? ac.WM : null;
                    if (wm == null || (m.Team != null && wm.teamString == m.Team)) continue;
                    var mb = VesselModuleRegistry.GetModule<MissileBase>(w);
                    if (mb != null && mb.HasFired) continue; // missiles: interceptions are scored on their own
                }
                catch (Exception) { continue; }
                double d = NearestPartM(w, pos);
                if (d <= radius && d < best) { best = d; id = w.persistentId; }
            }
            return id;
        }

        private static double NearestPartM(Vessel t, Vector3d pos)
        {
            if (t == null) return double.MaxValue;
            var parts = t.parts;
            if (parts == null || parts.Count == 0) return (t.CoMD - pos).magnitude;
            double best = double.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                double d = ((Vector3d)p.transform.position - pos).sqrMagnitude;
                if (d < best) best = d;
            }
            return Math.Sqrt(best);
        }

        public bool TryGetEnd(uint missile, out MissileEnd end)
        {
            end = default(MissileEnd);
            if (!_msl.TryGetValue(missile, out var m)) return false;
            _msl.Remove(missile);
            if (!m.Ended) return false; // left the loaded list without being destroyed (unloaded, recovered): the ledger decides
            if (m.Exploded)
            {
                // BDArmory scoring saw which vessel the explosion damaged (competition only, Overdrive F10)
                string victim = null;
                for (int i = _hits.Count - 1; i >= 0; i--)
                {
                    var h = _hits[i];
                    if (h.Ut < m.EndUt - 0.5) break;
                    if (m.LauncherName != null && h.Attacker == m.LauncherName) { victim = h.Victim; break; }
                }
                uint vid = victim != null ? IdOfName(victim) : 0;
                if (vid != 0) { end = new MissileEnd { Fate = MissileFate.Hit, Victim = vid, Source = "bda_scoring" }; return true; }
                if (m.NearTarget) { end = new MissileEnd { Fate = MissileFate.Hit, Victim = m.TargetId, Source = "proximity" }; return true; }
                if (m.VictimId != 0) { end = new MissileEnd { Fate = MissileFate.Hit, Victim = m.VictimId, Source = "proximity" }; return true; }
                end = new MissileEnd { Fate = MissileFate.Missed, Source = "proximity" };
                return true;
            }
            if (m.Missed) { end = new MissileEnd { Fate = MissileFate.Missed, Source = "binding" }; return true; }
            if (m.InterceptedBy != 0) { end = new MissileEnd { Fate = MissileFate.Intercepted, By = m.InterceptedBy, Source = "proximity" }; return true; }
            end = new MissileEnd { Fate = MissileFate.Destroyed, Source = "binding" };
            return true;
        }

        public void DrainKills(List<KillReport> into)
        {
            if (_killQueue.Count == 0) return;
            into.AddRange(_killQueue);
            _killQueue.Clear();
        }

        /// <summary>F10 CompetitionScores.OnHit (attacker, victim, damage type, parts hit): missile hits only.</summary>
        public void ScoredHitEvent(string attacker, string victim, int damageFrom, int partsHit)
        {
            if (damageFrom != (int)BDArmory.Competition.DamageFrom.Missiles || string.IsNullOrEmpty(attacker) || string.IsNullOrEmpty(victim)) return;
            _hits.Add(new ScoredHit { Ut = Now, Attacker = attacker, Victim = victim });
            if (_hits.Count > 256) _hits.RemoveRange(0, 128);
        }

        /// <summary>F10 CompetitionScores.OnDeath (victim, last attacker, last damage type, alive state).</summary>
        public void ScoredDeathEvent(string victim, string attacker, int damageFrom, int aliveState)
        {
            if (string.IsNullOrEmpty(victim)) return;
            _killQueue.Add(new KillReport
            {
                Victim = IdOfName(victim),
                VictimName = victim,
                AttackerName = attacker,
                Damage = ((BDArmory.Competition.DamageFrom)damageFrom).ToString(),
                Ut = Now,
                Source = "bda_scoring",
            });
            if (_killQueue.Count > 512) _killQueue.RemoveRange(0, 256);
        }

        private uint IdOfName(string name)
        {
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return 0;
            for (int i = 0; i < loaded.Count; i++)
            {
                var v = loaded[i];
                if (v != null && v.vesselName == name) return v.persistentId;
            }
            foreach (var kv in _info)
                if (kv.Key != null && kv.Value.Name == name) return kv.Key.persistentId;
            return 0;
        }

        /// <summary>Forgets missiles not seen for a few hundred scans and never asked about (called with the vessel-info sweep).</summary>
        private void SweepMissiles()
        {
            foreach (var kv in _msl)
                if (_stamp - kv.Value.SeenStamp > 255) _mslGone.Add(kv.Key);
            for (int i = 0; i < _mslGone.Count; i++) _msl.Remove(_mslGone[i]);
            _mslGone.Clear();
        }

        private void ClearMissiles()
        {
            _msl.Clear();
            _hits.Clear();
            _killQueue.Clear();
            Array.Clear(_det, 0, _det.Length);
        }
    }
}
