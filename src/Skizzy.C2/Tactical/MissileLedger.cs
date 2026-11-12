using System;
using System.Collections.Generic;
using System.Globalization;

namespace Skizzy.C2.Tactical
{
    /// <summary>How a missile's flight ended (<see cref="Flying"/> while it is in the air).</summary>
    public enum MissileFate : byte { Flying, Hit, Missed, Intercepted, Destroyed }

    /// <summary>What the game knows about how a missile left the world (<see cref="IMissileOutcomes.TryGetEnd"/>).</summary>
    public struct MissileEnd
    {
        /// <summary><see cref="MissileFate.Flying"/> = the game does not know; the ledger decides from geometry.</summary>
        public MissileFate Fate;
        /// <summary>Hit: the vessel it hit (0 = its target).</summary>
        public uint Victim;
        /// <summary>Intercepted or Destroyed: the interceptor missile (or shooter) when known, else 0.</summary>
        public uint By;
        /// <summary>Where the verdict comes from: "bda_scoring", "proximity", "binding" ...</summary>
        public string Source;
    }

    /// <summary>A kill the game scored itself (BDArmory competition scoring, Overdrive F10 OnDeath).</summary>
    public struct KillReport
    {
        public uint Victim;
        public string VictimName;
        public string VictimTeam;
        /// <summary>Scoring name (vessel name) of the last attacker; null or empty = none.</summary>
        public string AttackerName;
        /// <summary>BDArmory damage type of the last hit ("Missiles", "Guns", ...).</summary>
        public string Damage;
        public double Ut;
        public string Source;
    }

    /// <summary>
    /// Optional world interface: missile outcomes the game knows (the KSP binding reads BDArmory's MissileBase state at
    /// the moment the missile vessel is destroyed, and BDArmory's competition scoring). Main thread.
    /// </summary>
    public interface IMissileOutcomes
    {
        /// <summary>Called once when a missile leaves the world. True when the game knows something about it.</summary>
        bool TryGetEnd(uint missile, out MissileEnd end);

        /// <summary>Kills scored by the game since the last call (moved into <paramref name="into"/>).</summary>
        void DrainKills(List<KillReport> into);
    }

    public enum LedgerKind : byte { Fired, Hit, Intercepted, Missed, Destroyed, Killed }

    /// <summary>One engagement log line. Immutable once created (safe to hand to another thread).</summary>
    public sealed class LedgerEntry
    {
        public long Seq;
        public double Ut;
        public LedgerKind Kind;
        /// <summary>The missile (0 for kills).</summary>
        public uint Missile;
        public string Weapon;
        /// <summary>The shooting team (for kills: the team credited with the kill).</summary>
        public string Team;
        public uint Launcher;
        public string LauncherName;
        public uint Target;
        public string TargetName;
        public string TargetTeam;
        /// <summary>Hit: the vessel hit. Intercepted: the interceptor missile. Destroyed: the shooter when known. Killed: the victim.</summary>
        public uint Other;
        public string OtherName;
        public string OtherTeam;
        /// <summary>Funds value of the missile (NaN unknown).</summary>
        public double Cost;
        /// <summary>"ledger" (seen in the units list), "bda_scoring", "proximity", "geometry" or "binding".</summary>
        public string Source;
        public string Text;

        public static string KindName(LedgerKind k)
        {
            switch (k)
            {
                case LedgerKind.Fired: return "fired";
                case LedgerKind.Hit: return "hit";
                case LedgerKind.Intercepted: return "intercepted";
                case LedgerKind.Missed: return "missed";
                case LedgerKind.Destroyed: return "destroyed";
                default: return "killed";
            }
        }
    }

    /// <summary>Running totals of one team. Counts are of the team's own missiles unless noted.</summary>
    public sealed class TeamTotals
    {
        public string Team;
        public int Fired, InFlight, Hits, Misses, Intercepted, Destroyed;
        /// <summary>Enemy missiles this team's missiles intercepted.</summary>
        public int InterceptsMade;
        /// <summary>Enemy vessels destroyed with this team's missile as the last hit.</summary>
        public int Kills;
        /// <summary>Funds: every missile fired; own missiles intercepted; own missiles that hit; missed; destroyed.</summary>
        public double Spent, InterceptedCost, HitCost, MissedCost, DestroyedCost;
        /// <summary>Missiles whose cost the game did not report (excluded from the sums).</summary>
        public int CostUnknown;

        public void CopyTo(TeamTotals t)
        {
            t.Team = Team; t.Fired = Fired; t.InFlight = InFlight; t.Hits = Hits; t.Misses = Misses; t.Intercepted = Intercepted; t.Destroyed = Destroyed;
            t.InterceptsMade = InterceptsMade; t.Kills = Kills; t.Spent = Spent; t.InterceptedCost = InterceptedCost; t.HitCost = HitCost;
            t.MissedCost = MissedCost; t.DestroyedCost = DestroyedCost; t.CostUnknown = CostUnknown;
        }
    }

    /// <summary>One missile the ledger follows. Pooled: valid while in <see cref="MissileLedger.Flying"/> or <see cref="MissileLedger.Resolved"/>.</summary>
    public sealed class MissileRec
    {
        public uint Id;
        public string Name;
        public string Team;
        public string Weapon;
        public uint Launcher;
        public string LauncherName;
        public uint Target;
        public string TargetName;
        public string TargetTeam;
        public bool TargetIsMissile;
        public double Cost;
        public double FiredUt;
        public MissileFate State;
        public double EndUt;
        /// <summary>Hit: victim; Intercepted: interceptor; Destroyed: shooter (0 unknown).</summary>
        public uint Other;
        public GeoPoint Pos;
        public double HeadingDeg, SpeedMps, ClimbMps;
        internal int Stamp;
        internal GeoPoint TgtPos;
        internal double TgtSeenUt = double.NegativeInfinity;
        internal bool TgtAlive;
        internal bool HasEnd;
        internal MissileEnd End;
        internal double PendingSince = double.NaN;
        internal string Src;

        public static string StateName(MissileFate f)
        {
            switch (f)
            {
                case MissileFate.Hit: return "hit";
                case MissileFate.Missed: return "missed";
                case MissileFate.Intercepted: return "intercepted";
                case MissileFate.Destroyed: return "destroyed";
                default: return "flying";
            }
        }
    }

    /// <summary>
    /// The missile ledger (M2): follows every missile in flight from the units the track picture engine read (truth),
    /// decides how each one ended (the game's own verdict through <see cref="IMissileOutcomes"/> when it has one, else
    /// geometry and interceptor matching), and keeps the engagement log and per-team totals with KSP part costs.
    /// One <see cref="Update"/> per picture period on the main thread; 0 B allocated while missiles only fly (records are
    /// pooled; log lines are allocated when something happens). Not thread-safe.
    /// </summary>
    public sealed class MissileLedger
    {
        /// <summary>Geometry fallback: a missile that vanishes within this distance of its target (plus one period of travel) hit it.</summary>
        public double HitRadiusM = 60;
        /// <summary>An interceptor and its target missile vanishing this close in time are one intercept.</summary>
        public double InterceptWindowS = 1.5;
        /// <summary>Resolved missiles stay in <see cref="Resolved"/> (final state for the stream) this long.</summary>
        public double KeepResolvedS = 3;
        /// <summary>A vessel that dies this long after a missile hit is that missile's kill (when the game does not score it).</summary>
        public double KillWindowS = 30;
        public int MaxEntries = 8192;

        private readonly Dictionary<uint, MissileRec> _byId = new Dictionary<uint, MissileRec>(256);
        private readonly List<MissileRec> _flying = new List<MissileRec>(256);
        private readonly List<MissileRec> _resolved = new List<MissileRec>(256);
        private readonly List<MissileRec> _vanished = new List<MissileRec>(64);
        private readonly Stack<MissileRec> _pool = new Stack<MissileRec>();
        private readonly List<LedgerEntry> _entries = new List<LedgerEntry>(1024);
        private readonly Dictionary<string, TeamTotals> _totals = new Dictionary<string, TeamTotals>(StringComparer.Ordinal);
        private readonly List<TeamTotals> _teams = new List<TeamTotals>();
        private readonly Dictionary<uint, LedgerEntry> _victims = new Dictionary<uint, LedgerEntry>();
        private readonly List<uint> _victimScratch = new List<uint>();
        private readonly List<KillReport> _kills = new List<KillReport>();
        private int _stamp;
        private double _lastNow = double.NaN;
        private double _dt = 0.2;
        private long _seq;
        private Geo _geo;

        public long Seq => _seq;
        public long Updates;
        public double LastUpdateMs, MaxUpdateMs, TotalUpdateMs;
        /// <summary>Missiles in the air now (do not modify).</summary>
        public IReadOnlyList<MissileRec> Flying => _flying;
        /// <summary>Missiles resolved in the last <see cref="KeepResolvedS"/> seconds, final state set (do not modify).</summary>
        public IReadOnlyList<MissileRec> Resolved => _resolved;
        /// <summary>The engagement log, oldest first (bounded by <see cref="MaxEntries"/>).</summary>
        public IReadOnlyList<LedgerEntry> Entries => _entries;
        public IReadOnlyList<TeamTotals> Teams => _teams;

        public MissileRec Find(uint id)
        {
            MissileRec r;
            return _byId.TryGetValue(id, out r) ? r : null;
        }

        public TeamTotals TotalsOf(string team)
        {
            TeamTotals t;
            return team != null && _totals.TryGetValue(team, out t) ? t : null;
        }

        public void Clear()
        {
            foreach (var r in _flying) _pool.Push(r);
            foreach (var r in _resolved) _pool.Push(r);
            _flying.Clear(); _resolved.Clear(); _byId.Clear(); _entries.Clear(); _totals.Clear(); _teams.Clear(); _victims.Clear();
            _lastNow = double.NaN;
        }

        /// <summary>
        /// One period. <paramref name="units"/>: every unit present now (truth); <paramref name="lookup"/>: a present unit by id
        /// (null when gone); <paramref name="outcomes"/>: the game's verdicts, or null.
        /// </summary>
        public void Update(double now, List<UnitState> units, Func<uint, UnitState> lookup, IMissileOutcomes outcomes, Geo geo)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _geo = geo;
            if (!double.IsNaN(_lastNow) && now > _lastNow) _dt = Math.Min(2, now - _lastNow);
            _lastNow = now;
            _stamp++;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || !u.IsMissile || u.Id == 0) continue;
                MissileRec r;
                if (!_byId.TryGetValue(u.Id, out r))
                {
                    if (!u.Alive) continue;
                    r = Launch(u, now, lookup);
                }
                else if (r.State != MissileFate.Flying) continue;
                if (!u.Alive) continue; // reported dead: resolved below as vanished
                r.Stamp = _stamp;
                r.Pos = u.Pos;
                r.HeadingDeg = u.HeadingDeg;
                r.SpeedMps = u.SpeedMps;
                r.ClimbMps = u.ClimbMps;
                if (double.IsNaN(r.Cost) && !double.IsNaN(u.Cost)) LateCost(r, u.Cost);
                if (u.MissileTarget != 0 && u.MissileTarget != r.Target) Retarget(r, u.MissileTarget, lookup);
                if (r.Target != 0)
                {
                    var t = lookup(r.Target);
                    if (t != null)
                    {
                        r.TgtPos = t.Pos;
                        r.TgtSeenUt = now;
                        r.TgtAlive = t.Alive;
                    }
                    else r.TgtAlive = false;
                }
            }
            _vanished.Clear();
            for (int i = 0; i < _flying.Count; i++)
            {
                var r = _flying[i];
                if (r.Stamp != _stamp) _vanished.Add(r);
                else if (!double.IsNaN(r.PendingSince)) r.PendingSince = double.NaN; // back in the list (should not happen)
            }
            if (_vanished.Count > 0) Resolve(now, lookup, outcomes);
            Kills(now, lookup, outcomes);
            for (int i = _resolved.Count - 1; i >= 0; i--)
            {
                var r = _resolved[i];
                if (now - r.EndUt <= KeepResolvedS) continue;
                _resolved.RemoveAt(i);
                _byId.Remove(r.Id);
                _pool.Push(r);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastUpdateMs = ms;
            TotalUpdateMs += ms;
            if (ms > MaxUpdateMs) MaxUpdateMs = ms;
            Updates++;
        }

        // ------------------------------------------------------------------ launch

        private MissileRec Launch(UnitState u, double now, Func<uint, UnitState> lookup)
        {
            var r = _pool.Count > 0 ? _pool.Pop() : new MissileRec();
            r.Id = u.Id;
            r.Name = u.Name;
            r.Launcher = u.Launcher;
            var l = u.Launcher != 0 ? lookup(u.Launcher) : null;
            r.LauncherName = l != null ? l.Name : u.Launcher != 0 ? "v:" + u.Launcher.ToString(CultureInfo.InvariantCulture) : "unknown launcher";
            r.Team = u.Side ?? (l != null ? l.Side : null) ?? "?";
            r.Weapon = !string.IsNullOrEmpty(u.Weapon) ? u.Weapon : u.Name ?? "missile";
            r.Cost = u.Cost;
            r.FiredUt = now;
            r.State = MissileFate.Flying;
            r.EndUt = double.NaN;
            r.Other = 0;
            r.Target = 0;
            r.TargetName = null;
            r.TargetTeam = null;
            r.TargetIsMissile = false;
            r.TgtSeenUt = double.NegativeInfinity;
            r.TgtAlive = false;
            r.HasEnd = false;
            r.End = default(MissileEnd);
            r.PendingSince = double.NaN;
            r.Src = null;
            r.Pos = u.Pos;
            if (u.MissileTarget != 0) Retarget(r, u.MissileTarget, lookup);
            _byId[r.Id] = r;
            _flying.Add(r);
            var tt = Totals(r.Team);
            tt.Fired++;
            tt.InFlight++;
            if (double.IsNaN(r.Cost)) tt.CostUnknown++; else tt.Spent += r.Cost;
            var e = NewEntry(now, LedgerKind.Fired, r, "ledger");
            e.Text = TeamTok(r.Team) + " " + r.LauncherName + " fired " + r.Weapon + (r.Target != 0 ? " at " + Tgt(r) : "") + CostTok(r.Cost);
            Add(e);
            return r;
        }

        private void LateCost(MissileRec r, double cost)
        {
            r.Cost = cost;
            var tt = Totals(r.Team);
            tt.CostUnknown--;
            tt.Spent += cost;
        }

        private void Retarget(MissileRec r, uint target, Func<uint, UnitState> lookup)
        {
            r.Target = target;
            var t = lookup(target);
            r.TargetName = t != null ? t.Name : "v:" + target.ToString(CultureInfo.InvariantCulture);
            r.TargetTeam = t != null ? t.Side : null;
            r.TargetIsMissile = t != null && t.IsMissile;
            if (t != null) { r.TgtPos = t.Pos; r.TgtSeenUt = _lastNow; r.TgtAlive = t.Alive; }
        }

        // ------------------------------------------------------------------ resolution

        private void Resolve(double now, Func<uint, UnitState> lookup, IMissileOutcomes outcomes)
        {
            // 1. the game's verdicts (asked once per missile)
            for (int i = 0; i < _vanished.Count; i++)
            {
                var r = _vanished[i];
                if (!double.IsNaN(r.PendingSince)) continue; // asked when it first vanished
                MissileEnd end;
                r.HasEnd = false;
                if (outcomes != null)
                {
                    try { r.HasEnd = outcomes.TryGetEnd(r.Id, out end); }
                    catch (Exception) { end = default(MissileEnd); r.HasEnd = false; }
                    r.End = end;
                }
            }
            // 2. fates
            for (int i = 0; i < _vanished.Count; i++)
            {
                var r = _vanished[i];
                r.State = MissileFate.Flying;
                r.Other = 0;
                if (r.HasEnd && r.End.Fate != MissileFate.Flying)
                {
                    r.State = r.End.Fate;
                    r.Src = r.End.Source ?? "binding";
                    if (r.State == MissileFate.Hit) r.Other = r.End.Victim != 0 ? r.End.Victim : r.Target;
                    else r.Other = r.End.By;
                    if (r.State == MissileFate.Intercepted && r.Other == 0)
                    {
                        var by = FindInterceptor(r, now);
                        if (by != null) r.Other = by.Id;
                    }
                    if (r.State == MissileFate.Destroyed && r.Other == 0)
                    {
                        var by = FindInterceptor(r, now);
                        if (by != null) { r.State = MissileFate.Intercepted; r.Other = by.Id; }
                    }
                    continue;
                }
                var ic = FindInterceptor(r, now);
                if (ic != null) { r.State = MissileFate.Intercepted; r.Other = ic.Id; r.Src = "geometry"; continue; }
                if (r.TargetIsMissile && r.Target != 0)
                {
                    var tm = Find(r.Target);
                    bool targetGone = tm == null || tm.Stamp != _stamp || tm.State != MissileFate.Flying;
                    if (targetGone) { r.State = MissileFate.Hit; r.Other = r.Target; r.Src = "geometry"; continue; }
                    // the target missile still flies: wait a little, it may die in the next period
                    if (double.IsNaN(r.PendingSince)) r.PendingSince = now;
                    if (now - r.PendingSince < InterceptWindowS) continue;
                }
                if (r.Target != 0 && CloseToTarget(r, now)) { r.State = MissileFate.Hit; r.Other = r.Target; r.Src = "geometry"; continue; }
                r.State = MissileFate.Missed;
                r.Src = "geometry";
            }
            // 3. interceptor and target agree
            for (int i = 0; i < _vanished.Count; i++)
            {
                var r = _vanished[i];
                if (r.State == MissileFate.Intercepted && r.Other != 0)
                {
                    var by = Find(r.Other);
                    if (by != null && by.State != MissileFate.Hit && (by.Stamp != _stamp || by.State != MissileFate.Flying) && _vanished.Contains(by))
                    { by.State = MissileFate.Hit; by.Other = r.Id; if (by.Src == null || by.Src == "geometry") by.Src = r.Src; }
                }
                else if (r.State == MissileFate.Hit && r.TargetIsMissile && r.Other == r.Target)
                {
                    var m = Find(r.Target);
                    if (m != null && m.State != MissileFate.Intercepted && _vanished.Contains(m) && (m.State == MissileFate.Flying || m.State == MissileFate.Missed || (m.State == MissileFate.Destroyed && m.Other == 0)))
                    { m.State = MissileFate.Intercepted; m.Other = r.Id; if (m.Src == null || m.Src == "geometry") m.Src = r.Src; }
                }
            }
            // 4. entries and totals (interceptors that hit a missile are credited in the intercepted line)
            for (int i = 0; i < _vanished.Count; i++)
            {
                var r = _vanished[i];
                if (r.State == MissileFate.Flying) continue; // pending
                Finish(r, now, lookup);
            }
        }

        /// <summary>An opposing missile guiding on <paramref name="m"/> that vanished now or ended recently.</summary>
        private MissileRec FindInterceptor(MissileRec m, double now)
        {
            MissileRec best = null;
            for (int i = 0; i < _vanished.Count; i++)
            {
                var c = _vanished[i];
                if (c == m || c.Target != m.Id || c.Team == m.Team) continue;
                if (c.HasEnd && (c.End.Fate == MissileFate.Missed || c.End.Fate == MissileFate.Intercepted || c.End.Fate == MissileFate.Destroyed)) continue;
                if (c.HasEnd && c.End.Fate == MissileFate.Hit && c.End.Victim != 0 && c.End.Victim != m.Id) continue;
                best = c;
                break;
            }
            if (best != null) return best;
            for (int i = _resolved.Count - 1; i >= 0; i--)
            {
                var c = _resolved[i];
                if (c.Target != m.Id || c.Team == m.Team || now - c.EndUt > InterceptWindowS) continue;
                if (c.State == MissileFate.Hit && (c.Other == m.Id || c.Other == 0)) return c;
            }
            return null;
        }

        private bool CloseToTarget(MissileRec r, double now)
        {
            if (_geo == null || double.IsNegativeInfinity(r.TgtSeenUt) || now - r.TgtSeenUt > 2 * _dt + 0.01) return false;
            double d = _geo.Distance3(r.Pos, r.TgtPos);
            return d <= HitRadiusM + r.SpeedMps * _dt;
        }

        private void Finish(MissileRec r, double now, Func<uint, UnitState> lookup)
        {
            r.EndUt = now;
            r.PendingSince = double.NaN;
            _flying.Remove(r);
            _resolved.Add(r);
            var tt = Totals(r.Team);
            tt.InFlight--;
            double c = double.IsNaN(r.Cost) ? 0 : r.Cost;
            LedgerEntry e = null;
            switch (r.State)
            {
                case MissileFate.Hit:
                {
                    var victim = r.Other;
                    bool missileVictim = victim != 0 && (victim == r.Target ? r.TargetIsMissile : Find(victim) != null);
                    if (missileVictim)
                    {
                        // an intercept, not a hit on a vessel: the target's "intercepted" line credits this team, unless that
                        // line will never come (the target resolved otherwise, or is no longer known)
                        var m = Find(victim);
                        if (m != null && ((m.State == MissileFate.Intercepted && m.Other == r.Id) || m.State == MissileFate.Flying)) break;
                        tt.InterceptsMade++;
                        e = NewEntry(now, LedgerKind.Hit, r, r.Src);
                        e.Other = victim;
                        e.OtherName = m != null ? m.Weapon : NameOf(victim, lookup);
                        e.OtherTeam = m != null ? m.Team : null;
                        e.Text = TeamTok(r.Team) + " " + r.Weapon + " from " + r.LauncherName + " intercepted " + (e.OtherTeam != null ? TeamTok(e.OtherTeam) + " " : "") + e.OtherName;
                        break;
                    }
                    tt.Hits++;
                    tt.HitCost += c;
                    e = NewEntry(now, LedgerKind.Hit, r, r.Src);
                    e.Other = victim;
                    if (victim == r.Target) { e.OtherName = r.TargetName; e.OtherTeam = r.TargetTeam; }
                    else { var v = lookup(victim); e.OtherName = v != null ? v.Name : NameOf(victim, lookup); e.OtherTeam = v != null ? v.Side : null; }
                    e.Text = TeamTok(r.Team) + " " + r.Weapon + " from " + r.LauncherName + " hit " + e.OtherName + CostTok(r.Cost);
                    if (victim != 0) _victims[victim] = e;
                    break;
                }
                case MissileFate.Intercepted:
                {
                    tt.Intercepted++;
                    tt.InterceptedCost += c;
                    e = NewEntry(now, LedgerKind.Intercepted, r, r.Src);
                    var by = r.Other != 0 ? Find(r.Other) : null;
                    e.Other = r.Other;
                    if (by != null)
                    {
                        e.OtherName = by.Weapon + " from " + by.LauncherName;
                        e.OtherTeam = by.Team;
                        Totals(by.Team).InterceptsMade++;
                    }
                    else e.OtherName = r.Other != 0 ? NameOf(r.Other, lookup) : "an interceptor";
                    e.Text = TeamTok(r.Team) + " " + r.Weapon + " from " + r.LauncherName + " intercepted by " + (e.OtherTeam != null ? TeamTok(e.OtherTeam) + " " : "") + e.OtherName + CostTok(r.Cost);
                    break;
                }
                case MissileFate.Destroyed:
                {
                    tt.Destroyed++;
                    tt.DestroyedCost += c;
                    e = NewEntry(now, LedgerKind.Destroyed, r, r.Src);
                    e.Other = r.Other;
                    if (r.Other != 0) { var by = lookup(r.Other); e.OtherName = by != null ? by.Name : NameOf(r.Other, lookup); e.OtherTeam = by != null ? by.Side : null; }
                    e.Text = TeamTok(r.Team) + " " + r.Weapon + " from " + r.LauncherName + " destroyed" + (e.OtherName != null ? " by " + (e.OtherTeam != null ? TeamTok(e.OtherTeam) + " " : "") + e.OtherName : " (shot down or crashed)") + CostTok(r.Cost);
                    break;
                }
                default:
                {
                    tt.Misses++;
                    tt.MissedCost += c;
                    e = NewEntry(now, LedgerKind.Missed, r, r.Src);
                    e.Text = TeamTok(r.Team) + " " + r.Weapon + " from " + r.LauncherName + " missed" + (r.Target != 0 ? " " + Tgt(r) : "") + CostTok(r.Cost);
                    break;
                }
            }
            if (e != null) Add(e);
        }

        // ------------------------------------------------------------------ kills

        private void Kills(double now, Func<uint, UnitState> lookup, IMissileOutcomes outcomes)
        {
            if (outcomes != null)
            {
                _kills.Clear();
                try { outcomes.DrainKills(_kills); }
                catch (Exception) { _kills.Clear(); }
                for (int i = 0; i < _kills.Count; i++) ScoredKill(_kills[i], now);
            }
            if (_victims.Count == 0) return;
            _victimScratch.Clear();
            foreach (var kv in _victims)
            {
                var hit = kv.Value;
                var v = lookup(kv.Key);
                if (v != null && v.Alive)
                {
                    if (now - hit.Ut > KillWindowS) _victimScratch.Add(kv.Key);
                    continue;
                }
                _victimScratch.Add(kv.Key);
                var e = new LedgerEntry
                {
                    Seq = ++_seq, Ut = now, Kind = LedgerKind.Killed, Missile = hit.Missile, Weapon = hit.Weapon, Team = hit.Team, Launcher = hit.Launcher,
                    LauncherName = hit.LauncherName, Other = kv.Key, OtherName = hit.OtherName, OtherTeam = hit.OtherTeam, Cost = double.NaN, Source = "ledger",
                };
                e.Text = (hit.OtherName ?? NameOf(kv.Key, lookup)) + (hit.OtherTeam != null ? " (" + TeamTok(hit.OtherTeam) + ")" : "") + " destroyed, last missile hit " +
                         TeamTok(hit.Team) + " " + hit.LauncherName + " " + hit.Weapon;
                Totals(hit.Team).Kills++;
                Add(e);
            }
            for (int i = 0; i < _victimScratch.Count; i++) _victims.Remove(_victimScratch[i]);
        }

        private void ScoredKill(KillReport k, double now)
        {
            if (string.IsNullOrEmpty(k.AttackerName) || (k.Damage != null && k.Damage != "Missiles")) { if (k.Victim != 0) _victims.Remove(k.Victim); return; }
            LedgerEntry hit = null;
            if (k.Victim != 0) _victims.TryGetValue(k.Victim, out hit);
            string team = hit != null ? hit.Team : TeamOfLauncher(k.AttackerName);
            var e = new LedgerEntry
            {
                Seq = ++_seq, Ut = now, Kind = LedgerKind.Killed, Missile = hit != null ? hit.Missile : 0, Weapon = hit != null ? hit.Weapon : null, Team = team ?? "?",
                LauncherName = k.AttackerName, Launcher = hit != null ? hit.Launcher : 0, Other = k.Victim, OtherName = k.VictimName, OtherTeam = k.VictimTeam ?? (hit != null ? hit.OtherTeam : null),
                Cost = double.NaN, Source = k.Source ?? "bda_scoring",
            };
            e.Text = k.VictimName + (e.OtherTeam != null ? " (" + TeamTok(e.OtherTeam) + ")" : "") + " killed by " + (team != null ? TeamTok(team) + " " : "") + k.AttackerName + " (missiles, BDArmory scoring)";
            if (team != null) Totals(team).Kills++;
            if (k.Victim != 0) _victims.Remove(k.Victim);
            Add(e);
        }

        private string TeamOfLauncher(string name)
        {
            for (int i = _entries.Count - 1; i >= 0 && i >= _entries.Count - 2048; i--)
                if (_entries[i].LauncherName == name) return _entries[i].Team;
            return null;
        }

        // ------------------------------------------------------------------ helpers

        private TeamTotals Totals(string team)
        {
            TeamTotals t;
            if (_totals.TryGetValue(team, out t)) return t;
            t = new TeamTotals { Team = team };
            _totals[team] = t;
            _teams.Add(t);
            return t;
        }

        private LedgerEntry NewEntry(double now, LedgerKind kind, MissileRec r, string source)
        {
            return new LedgerEntry
            {
                Seq = ++_seq, Ut = now, Kind = kind, Missile = r.Id, Weapon = r.Weapon, Team = r.Team, Launcher = r.Launcher, LauncherName = r.LauncherName,
                Target = r.Target, TargetName = r.TargetName, TargetTeam = r.TargetTeam, Cost = r.Cost, Source = source ?? "ledger",
            };
        }

        private void Add(LedgerEntry e)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, Math.Max(1, MaxEntries / 8));
        }

        private static string NameOf(uint id, Func<uint, UnitState> lookup)
        {
            var u = lookup(id);
            return u != null ? u.Name : "v:" + id.ToString(CultureInfo.InvariantCulture);
        }

        private static string Tgt(MissileRec r) { return (r.TargetTeam != null ? TeamTok(r.TargetTeam) + " " : "") + r.TargetName; }

        public static string TeamTok(string team) { return "Team " + (team ?? "?"); }

        private static string CostTok(double cost)
        {
            return double.IsNaN(cost) ? "" : " (" + cost.ToString("#,0", CultureInfo.InvariantCulture) + " funds)";
        }

        /// <summary>The entries a team is part of (shooter, target or victim), newest first, for paging and polling.</summary>
        public static bool Involves(LedgerEntry e, string team)
        {
            return team == null || e.Team == team || e.TargetTeam == team || e.OtherTeam == team;
        }
    }
}

namespace Skizzy.C2.Tactical
{
    /// <summary>
    /// Missile funds value from KSP's own part costs, shared by the KSP binding and the fakes: each part is its catalog
    /// cost (<c>AvailablePart.cost</c>, resources full, as the editor prices it) plus its module costs
    /// (<c>Part.GetModuleCosts</c>), and a missile is the sum over every part of its vessel, so a multi-part or modular
    /// missile counts its boosters and sections.
    /// </summary>
    public static class MissileCosts
    {
        public static double Part(double catalog, double modules)
        {
            double c = (double.IsNaN(catalog) ? 0 : catalog) + (double.IsNaN(modules) ? 0 : modules);
            return c > 0 ? c : 0;
        }

        public static double Sum(IList<double> partCosts)
        {
            if (partCosts == null || partCosts.Count == 0) return double.NaN;
            double s = 0;
            for (int i = 0; i < partCosts.Count; i++) s += Part(partCosts[i], 0);
            return s;
        }
    }
}
