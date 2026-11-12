using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Skizzy.C2.Tactical
{
    /// <summary>The TPE's record of one unit: a copy of its state, kinematics and per-team sensor bookkeeping.</summary>
    internal sealed class TUnit
    {
        public readonly UnitState S = new UnitState();
        public bool Present;
        public bool Alive;
        public bool Seen;
        public int TeamIdx = -1;
        public double X, Y, Z, VX, VY, VZ;
        public readonly List<uint> Contacts = new List<uint>();
        public int ContactsStamp;
        public int ContactsDue;
        public int ContribTeam = -1;
        public readonly List<TUnit> Contrib = new List<TUnit>();
        public int[] HeldN = new int[2];
        public long[] HeldSum = new long[2];
        public int[] HeldSurvN = new int[2];
        public int[] EngN = new int[2];
        public uint[] FirstEng = new uint[2];
        public int[] MslN = new int[2];
        public int[] WarnUntil = new int[2];
        public bool[] Declared = new bool[2];
        public uint CountedBy;
        public int CountedStep;
        public uint Target;
    }

    /// <summary>
    /// The track picture engine (08): per-team pictures from the team's own sensors (F5 contacts), the team's BDA
    /// target database (datalink, visual) and missile warnings (F12), with identity states, stable track numbers,
    /// a missile list, engagement counts, dead reckoning and threat scores. Unity-free; one <see cref="Step"/> per
    /// period (5 Hz), main thread. Own-sensor reads are time-sliced over <see cref="ContactSlices"/> steps and threat
    /// scoring over <see cref="ThreatScoresPerStep"/> tracks per team per step; 0 B steady-state allocation.
    /// </summary>
    public sealed class TrackPictureEngine
    {
        private readonly IC2World _world;
        private readonly ITacticalSensors _sensors;
        private Geo _geo;
        private double _r;

        /// <summary>Tracks not held are dead-reckoned and dropped after this long (08 §4).</summary>
        public double DropAfterS = 90;
        /// <summary>Quality decays to 0 over this long once a track is not held.</summary>
        public double StaleAfterS = 30;
        /// <summary>Tracks newer than this carry NEW.</summary>
        public double NewForS = 10;
        /// <summary>Destroyed tracks stay (state dead) this long.</summary>
        public double DeadKeepS = 10;
        /// <summary>A track perceived this long is classified even without an own-sensor hold.</summary>
        public double ClassifyAfterS = 8;
        public int ContactSlices = 5;
        public int ThreatScoresPerStep = 32;
        /// <summary>Fog: IFF unknown until identified (range, behaviour). False = truthful IFF at once (08 §2 fogIff:false).</summary>
        public bool Fog = true;
        /// <summary>Show identified hostile names (08 §2 revealNames; off under fog by default).</summary>
        public bool RevealNames;
        /// <summary>Default identification ranges by target domain when no <see cref="IdentifyRange"/> is set (08 §2: 40 km air, 20 km surface).</summary>
        public double IdRangeAirM = 40000, IdRangeSurfaceM = 20000;
        /// <summary>Holder id to identification range (the C2 engine's doctrine). When set it decides alone; NaN = holder not known yet (asked again next step).</summary>
        public Func<uint, double> IdentifyRange;

        public long Seq;
        public double Now;
        public double LastStepMs;
        public double MaxStepMs;
        public double TotalStepMs;
        public long Steps;
        /// <summary>Accumulated step time by phase: units and engagement, own-sensor contacts, team tracks, threat scoring (ms).</summary>
        public readonly double[] PhaseMs = new double[4];

        private int _step;
        private readonly List<UnitState> _states = new List<UnitState>(256);
        private readonly Dictionary<uint, TUnit> _units = new Dictionary<uint, TUnit>(256);
        private readonly List<TUnit> _list = new List<TUnit>(256);
        private readonly List<TUnit> _order = new List<TUnit>(256);
        private readonly List<TeamPicture> _teams = new List<TeamPicture>();
        private readonly Dictionary<string, TeamPicture> _byTeam = new Dictionary<string, TeamPicture>(StringComparer.Ordinal);
        private readonly Stack<Track> _pool = new Stack<Track>();
        private readonly List<uint> _warn = new List<uint>();
        private readonly List<TacEvent> _events = new List<TacEvent>();
        private sbyte[] _hostile = new sbyte[0];
        private int _hostileN;
        private int _arrN = 2;

        /// <summary>The god view: every present unit, truth, quality 1.</summary>
        public readonly TeamPicture God = new TeamPicture("*", -1);

        public TrackPictureEngine(IC2World world, ITacticalSensors sensors = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _sensors = sensors ?? world as ITacticalSensors;
        }

        public IC2World World => _world;
        public bool HasEngagementData => _sensors != null;
        public IReadOnlyList<TeamPicture> Teams => _teams;
        public Geo Geo => _geo ?? (_geo = new Geo(_world.BodyRadius));
        public int MaxEvents = 2048;

        public TeamPicture Team(string team)
        {
            if (team == null) return null;
            if (team == "*") return God;
            TeamPicture p;
            return _byTeam.TryGetValue(team, out p) ? p : null;
        }

        /// <summary>The latest state the TPE read for a unit (truth), or null.</summary>
        public UnitState Unit(uint id)
        {
            TUnit u;
            return _units.TryGetValue(id, out u) && u.Present ? u.S : null;
        }

        /// <summary>Present units in the order the world reported them (truth).</summary>
        public void GetUnits(List<UnitState> into)
        {
            for (int i = 0; i < _order.Count; i++) into.Add(_order[i].S);
        }

        public int UnitCount => _order.Count;

        /// <summary>The vessel this unit's weapon manager targeted at the last step (0 none or no engagement data).</summary>
        public uint TargetOf(uint unit)
        {
            TUnit u;
            return _units.TryGetValue(unit, out u) && u.Alive ? u.Target : 0;
        }

        /// <summary>The unit's own-sensor contacts as of its last refresh (copy).</summary>
        public void ContactsOf(uint unit, List<uint> into)
        {
            TUnit u;
            if (_units.TryGetValue(unit, out u)) into.AddRange(u.Contacts);
        }

        public void DrainEvents(List<TacEvent> into)
        {
            into.AddRange(_events);
            _events.Clear();
        }

        public int PendingEvents => _events.Count;

        // ------------------------------------------------------------------ step

        /// <summary>One TPE period: read units, engagement, the due slice of own-sensor contacts, rebuild every team's tracks, score threats.</summary>
        public void Step()
        {
            if (_slice != 0) while (!StepSlice()) { } // finish a sliced period first
            while (!StepSlice()) { }
        }

        private int _slice;
        private double _periodMs;

        /// <summary>Slices in one period: units and engagement, own-sensor contacts, one per team, the god view, threats.</summary>
        public int SlicesPerPeriod => _teams.Count + 4;

        /// <summary>The largest single slice so far (the worst physics step when the host runs one slice per step), ms.</summary>
        public double MaxSliceMs;

        /// <summary>
        /// One slice of a period (08 §10 time slicing): the host calls this once per physics step. Slice 0 reads units
        /// and engagement, slice 1 the due own-sensor contacts; then one team's tracks per slice; then the god view; then
        /// threat scoring for every team. Each team's table is complete whenever it is read. Returns true when a period ends.
        /// </summary>
        public bool StepSlice()
        {
            long t0 = Stopwatch.GetTimestamp();
            double f = 1000.0 / Stopwatch.Frequency;
            bool done = false;
            int teams = _teams.Count;
            if (_slice == 0)
            {
                Now = _world.Now;
                if (_geo == null) _geo = new Geo(_world.BodyRadius);
                _r = _geo.Radius;
                _step++;
                if (_hostileN > 0) Array.Clear(_hostile, 0, _hostile.Length);
                _periodMs = 0;
                ReadUnits();
                ReadEngagement();
                PhaseMs[0] += (Stopwatch.GetTimestamp() - t0) * f;
                _slice = 1;
            }
            else if (_slice == 1)
            {
                ReadContacts();
                PhaseMs[1] += (Stopwatch.GetTimestamp() - t0) * f;
                _slice = 2;
            }
            else if (_slice <= teams + 1)
            {
                BuildTeam(_teams[_slice - 2]);
                _slice++;
                PhaseMs[2] += (Stopwatch.GetTimestamp() - t0) * f;
            }
            else if (_slice == teams + 2)
            {
                BuildGod();
                _slice++;
                PhaseMs[2] += (Stopwatch.GetTimestamp() - t0) * f;
            }
            else
            {
                for (int t = 0; t < teams; t++) ScoreThreats(_teams[t]);
                PhaseMs[3] += (Stopwatch.GetTimestamp() - t0) * f;
                _slice = 0;
                done = true;
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * f;
            if (ms > MaxSliceMs) MaxSliceMs = ms;
            _periodMs += ms;
            if (done)
            {
                Seq++;
                LastStepMs = _periodMs;
                TotalStepMs += _periodMs;
                Steps++;
                if (_periodMs > MaxStepMs) MaxStepMs = _periodMs;
            }
            return done;
        }

        /// <summary>True between the slices of a period (a period started and has not finished).</summary>
        public bool MidPeriod => _slice != 0;

        private TeamPicture TeamOf(string side)
        {
            TeamPicture p;
            if (_byTeam.TryGetValue(side, out p)) return p;
            p = new TeamPicture(side, _teams.Count);
            _teams.Add(p);
            _byTeam[side] = p;
            if (_teams.Count > _arrN)
            {
                _arrN = _teams.Count * 2;
                for (int i = 0; i < _list.Count; i++) Grow(_list[i]);
            }
            return p;
        }

        private void Grow(TUnit u)
        {
            int n = _arrN;
            if (u.HeldN.Length >= n) return;
            Array.Resize(ref u.HeldN, n);
            Array.Resize(ref u.HeldSum, n);
            Array.Resize(ref u.HeldSurvN, n);
            Array.Resize(ref u.EngN, n);
            Array.Resize(ref u.FirstEng, n);
            Array.Resize(ref u.MslN, n);
            Array.Resize(ref u.WarnUntil, n);
            Array.Resize(ref u.Declared, n);
        }

        private const double Deg = Math.PI / 180.0;

        private void ReadUnits()
        {
            for (int i = 0; i < _list.Count; i++) _list[i].Present = false;
            _order.Clear();
            _states.Clear();
            _world.GetUnits(_states);
            for (int i = 0; i < _states.Count; i++)
            {
                var s = _states[i];
                if (s == null || s.Id == 0) continue;
                TUnit u;
                if (!_units.TryGetValue(s.Id, out u))
                {
                    u = new TUnit();
                    Grow(u);
                    _units[s.Id] = u;
                    _list.Add(u);
                }
                else if (u.Present) continue; // reported twice
                bool wasAlive = u.Alive, wasSeen = u.Seen;
                u.S.CopyFrom(s);
                u.Present = true;
                u.Alive = s.Alive;
                u.Seen = true;
                u.TeamIdx = s.Side != null ? TeamOf(s.Side).Index : -1;
                _order.Add(u);
                // sphere position and velocity vectors (radius R): distances, aspect and closure without per-pair trigonometry
                double lat = s.Pos.Lat * Deg, lon = s.Pos.Lon * Deg, h = s.HeadingDeg * Deg;
                double cl = Math.Cos(lat), sl = Math.Sin(lat), co = Math.Cos(lon), so = Math.Sin(lon);
                u.X = _r * cl * co;
                u.Y = _r * cl * so;
                u.Z = _r * sl;
                double ve = Math.Sin(h) * s.SpeedMps, vn = Math.Cos(h) * s.SpeedMps;
                u.VX = -so * ve - sl * co * vn;
                u.VY = co * ve - sl * so * vn;
                u.VZ = cl * vn;
                if (!wasSeen && s.Alive && s.IsMissile && s.Side != null) MissileLaunched(u);
                if (wasAlive && !s.Alive) Destroyed(u);
            }
            for (int i = 0; i < _list.Count; i++)
            {
                var u = _list[i];
                if (u.Present || !u.Alive) continue;
                u.Alive = false; // vanished from the world: destroyed or unloaded
                Destroyed(u);
            }
        }

        private void ReadEngagement()
        {
            int n = _teams.Count;
            for (int i = 0; i < _list.Count; i++)
            {
                var u = _list[i];
                Array.Clear(u.EngN, 0, n);
                Array.Clear(u.FirstEng, 0, n);
                Array.Clear(u.MslN, 0, n);
            }
            for (int i = 0; i < _order.Count; i++)
            {
                var u = _order[i];
                if (!u.Alive || u.TeamIdx < 0) continue;
                TUnit t;
                if (u.S.IsMissile)
                {
                    if (u.S.MissileTarget != 0 && _units.TryGetValue(u.S.MissileTarget, out t))
                    {
                        t.MslN[u.TeamIdx]++;
                        // a launch at a team's unit declares the launcher hostile to that team (08 §12.1)
                        TUnit l;
                        if (u.S.Launcher != 0 && t.TeamIdx >= 0 && _units.TryGetValue(u.S.Launcher, out l)) l.Declared[t.TeamIdx] = true;
                    }
                    continue;
                }
                u.Target = _sensors != null ? _sensors.CurrentTarget(u.S.Id) : 0;
                if (u.Target == 0 || !_units.TryGetValue(u.Target, out t)) continue;
                if (t.EngN[u.TeamIdx]++ == 0) t.FirstEng[u.TeamIdx] = u.S.Id;
            }
        }

        private void ReadContacts()
        {
            int slices = ContactSlices < 1 ? 1 : ContactSlices;
            for (int i = 0; i < _list.Count; i++)
            {
                var r = _list[i];
                if (!r.Present || !r.Alive || r.S.IsMissile || r.TeamIdx < 0)
                {
                    if (r.ContribTeam >= 0) Uncount(r);
                    if (r.Contacts.Count > 0) { r.Contacts.Clear(); r.ContactsStamp++; }
                    r.ContactsDue = 0;
                    continue;
                }
                int ti = r.TeamIdx;
                if (r.ContribTeam >= 0 && r.ContribTeam != ti) { Uncount(r); r.ContactsDue = 0; } // changed team
                if (r.ContactsDue > _step) continue;
                int phase = (int)(r.S.Id % (uint)slices);
                r.ContactsDue = _step + 1 + ((phase - (_step + 1) % slices) % slices + slices) % slices;
                Uncount(r);
                r.Contacts.Clear();
                _world.GetOwnContacts(r.S.Id, r.Contacts);
                r.ContactsStamp++;
                var team = _teams[ti];
                bool surveillance = UnitClasses.IsSurveillance(r.S.Class);
                r.ContribTeam = ti;
                for (int c = 0; c < r.Contacts.Count; c++)
                {
                    TUnit t;
                    if (!_units.TryGetValue(r.Contacts[c], out t) || !t.Present || !t.Alive || t.TeamIdx == ti) continue;
                    if (t.CountedBy == r.S.Id && t.CountedStep == _step) continue; // reported twice (radar and IRST)
                    t.CountedBy = r.S.Id;
                    t.CountedStep = _step;
                    t.HeldN[ti]++;
                    t.HeldSum[ti] += r.S.Id;
                    if (surveillance) t.HeldSurvN[ti]++;
                    r.Contrib.Add(t);
                    if (!t.S.IsMissile && !team.IdentifiedBy.ContainsKey(t.S.Id))
                    {
                        double range = IdRange(r, t);
                        if (double.IsNaN(range)) r.ContactsDue = _step + 1; // holder not known to the range source yet: re-read next step
                        else if (range > 0 && Dist(r, t) <= range) team.IdentifiedBy[t.S.Id] = r.S.Id;
                    }
                }
                if (_sensors != null)
                {
                    _warn.Clear();
                    _sensors.GetMissileWarnings(r.S.Id, _warn);
                    for (int w = 0; w < _warn.Count; w++)
                    {
                        TUnit m;
                        if (_units.TryGetValue(_warn[w], out m) && m.Alive && m.TeamIdx != ti) m.WarnUntil[ti] = r.ContactsDue + 1;
                    }
                }
            }
        }

        private void Uncount(TUnit r)
        {
            int si = r.ContribTeam;
            if (si >= 0)
            {
                for (int i = 0; i < r.Contrib.Count; i++)
                {
                    var t = r.Contrib[i];
                    t.HeldN[si]--;
                    t.HeldSum[si] -= r.S.Id;
                    if (UnitClasses.IsSurveillance(r.S.Class)) t.HeldSurvN[si]--;
                }
            }
            r.Contrib.Clear();
            r.ContribTeam = -1;
        }

        private double IdRange(TUnit holder, TUnit t)
        {
            var f = IdentifyRange;
            if (f != null) return f(holder.S.Id);
            var d = UnitClasses.DomainOf(t.S.Class);
            return d == Domain.Air ? IdRangeAirM : IdRangeSurfaceM;
        }

        private bool Hostile(int a, int b)
        {
            if (a < 0 || b < 0 || a == b) return false;
            int n = _teams.Count;
            if (_hostileN != n)
            {
                _hostile = new sbyte[n * n];
                _hostileN = n;
            }
            int k = a * n + b;
            sbyte v = _hostile[k];
            if (v == 0)
            {
                v = _world.AreHostile(_teams[a].Team, _teams[b].Team) ? (sbyte)1 : (sbyte)2;
                _hostile[k] = v;
            }
            return v == 1;
        }

        /// <summary>Friendly to team <paramref name="ti"/>: the same team or a team BDA does not count as an enemy.</summary>
        private bool FriendOf(int ti, TUnit u) { return u.TeamIdx == ti || (u.TeamIdx >= 0 && !Hostile(ti, u.TeamIdx)); }

        // ------------------------------------------------------------------ tracks

        private void BuildTeam(TeamPicture p)
        {
            int ti = p.Index;
            p.Feed.Clear();
            p.Own.Clear();
            p.Hostiles = p.Unknowns = p.Friends = p.HostileMissiles = p.InboundToOwn = 0;
            p.MinTcpa = double.NaN;
            for (int i = 0; i < _list.Count; i++)
            {
                var u = _list[i];
                Track tr;
                p.ByVessel.TryGetValue(u.S.Id, out tr);
                if (!u.Alive)
                {
                    if (tr != null) UpdateGone(p, tr);
                    continue;
                }
                bool friend = FriendOf(ti, u);
                if (friend && u.TeamIdx == ti && !u.S.IsMissile) p.Own.Add(u);
                var src = TrackSources.None;
                if (friend) src = TrackSources.Truth;
                else
                {
                    if (u.HeldN[ti] > 0) src |= TrackSources.OwnSensor;
                    if (u.WarnUntil[ti] > _step) src |= TrackSources.Warning;
                    if (src == TrackSources.None && _world.SideDetects(p.Team, u.S.Id)) src = TrackSources.Database;
                }
                if (src == TrackSources.None)
                {
                    if (tr != null) UpdateUnheld(p, tr);
                    continue;
                }
                if (tr == null) tr = AddTrack(p, u);
                Refresh(p, tr, u, src, friend);
            }
            // tracks of vessels the world no longer reports at all
            for (int i = p.Tracks.Count - 1; i >= 0; i--)
            {
                var tr = p.Tracks[i];
                TUnit u;
                if (!_units.TryGetValue(tr.Vessel, out u) || (!u.Present && tr.State != TrackState.Dead)) UpdateGone(p, tr);
                if (tr.State == TrackState.Dead && Now - tr.StateUt >= DeadKeepS) RemoveAt(p, i);
                else if (tr.State == TrackState.Dr && Now - tr.LastSeenUt >= DropAfterS)
                {
                    Emit(TacEventKind.TrackLost, p, tr, 0, "faded", p.Team + ": T" + tr.Number + " dropped (not held for " + DropAfterS.ToString("0", CultureInfo.InvariantCulture) + " s).");
                    RemoveAt(p, i);
                }
            }
        }

        private Track AddTrack(TeamPicture p, TUnit u)
        {
            var tr = _pool.Count > 0 ? _pool.Pop() : new Track();
            int n;
            if (!p.Numbers.TryGetValue(u.S.Id, out n))
            {
                n = p.NextNumber++;
                p.Numbers[u.S.Id] = n;
            }
            tr.Number = n;
            tr.Vessel = u.S.Id;
            tr.Team = p.Team;
            tr.FirstUt = Now;
            tr.LastSeenUt = Now;
            tr.StateUt = Now;
            tr.State = TrackState.Dr; // Refresh makes it live (and reports it new)
            tr.Classified = false;
            tr.Declared = false;
            tr.InboundReported = false;
            tr.Iff = Iff.Unknown;
            tr.Id = IdState.Detected;
            tr.Threat = 0;
            tr.ThreatTo = 0;
            tr.ThreatRangeM = double.NaN;
            tr.Tcpa = double.NaN;
            tr.ClosureMps = 0;
            tr.ThreatAspect = Aspect.Beam;
            tr.Slot = p.Tracks.Count;
            p.Tracks.Add(tr);
            p.ByVessel[u.S.Id] = tr;
            p.ByNumber[n] = tr;
            p.Version++;
            return tr;
        }

        private void RemoveAt(TeamPicture p, int i)
        {
            var tr = p.Tracks[i];
            int last = p.Tracks.Count - 1;
            if (i != last)
            {
                p.Tracks[i] = p.Tracks[last];
                p.Tracks[i].Slot = i;
            }
            p.Tracks.RemoveAt(last);
            p.ByVessel.Remove(tr.Vessel);
            p.ByNumber.Remove(tr.Number);
            p.Version++;
            tr.Name = null;
            tr.Side = null;
            _pool.Push(tr);
        }

        private void Refresh(TeamPicture p, Track tr, TUnit u, TrackSources src, bool friend)
        {
            int ti = p.Index;
            var s = u.S;
            bool wasLive = tr.State == TrackState.Live;
            tr.State = TrackState.Live;
            if (!wasLive) tr.StateUt = Now;
            tr.Sources = src;
            tr.LastSeenUt = Now;
            tr.Pos = s.Pos;
            tr.LastPos = s.Pos;
            tr.HeadingDeg = s.HeadingDeg;
            tr.SpeedMps = s.SpeedMps;
            tr.Side = s.Side;
            tr.TrueClass = s.Class;
            tr.Domain = UnitClasses.DomainOf(s.Class);
            tr.IsMissile = s.IsMissile;
            tr.MissileTarget = s.IsMissile ? s.MissileTarget : 0;
            tr.Holders = u.HeldN[ti];
            tr.SoleHolder = tr.Holders == 1 ? (uint)u.HeldSum[ti] : 0;
            tr.SurveillanceHeld = u.HeldSurvN[ti] > 0;
            tr.EngagedBy = friend ? 0 : u.EngN[ti];
            tr.FirstEngager = friend ? 0 : u.FirstEng[ti];
            if (friend)
            {
                int inbound = 0;
                for (int k = 0; k < _teams.Count; k++) if (k != ti && Hostile(ti, k)) inbound += u.MslN[k];
                tr.MissilesAt = inbound;
                tr.Iff = Iff.Friend;
                tr.Id = IdState.Identified;
                tr.Class = s.Class;
                tr.Name = s.Name;
                tr.Quality = 1;
                tr.Classified = true;
                tr.IdentifiedBy = 0;
                tr.MissilesReadyKnown = s.MissilesMax > 0;
                tr.MissilesReady = s.MissilesReady;
                tr.MissilesMax = s.MissilesMax;
                p.Friends++;
                return;
            }
            tr.MissilesAt = u.MslN[ti];
            tr.MissilesReadyKnown = false;
            if ((src & TrackSources.OwnSensor) != 0) tr.Classified = true;
            uint idBy;
            tr.IdentifiedBy = p.IdentifiedBy.TryGetValue(s.Id, out idBy) ? idBy : 0;
            // behaviour declares: a missile homing on a friend, a weapon manager targeting a friend, a missile warning
            if (!tr.Declared)
            {
                TUnit mt;
                if (u.Declared[ti]) tr.Declared = true;
                else if (s.IsMissile && s.MissileTarget != 0 && _units.TryGetValue(s.MissileTarget, out mt) && FriendOf(ti, mt)) tr.Declared = true;
                else if ((src & TrackSources.Warning) != 0) tr.Declared = true;
                else if (u.Target != 0 && _units.TryGetValue(u.Target, out mt) && FriendOf(ti, mt)) tr.Declared = true;
            }
            bool identified = !Fog || tr.IdentifiedBy != 0;
            var oldIff = tr.Iff;
            var oldId = tr.Id;
            if (identified || tr.Declared)
                tr.Iff = u.TeamIdx < 0 ? Iff.Neutral : (Hostile(ti, u.TeamIdx) ? Iff.Hostile : Iff.Friend);
            else tr.Iff = Iff.Unknown;
            if (identified) tr.Id = IdState.Identified;
            else if (tr.Classified || tr.Declared || Now - tr.FirstUt >= ClassifyAfterS) tr.Id = IdState.Classified;
            else tr.Id = IdState.Detected;
            tr.Class = tr.Id == IdState.Detected ? (s.IsMissile ? UnitClass.Missile : UnitClass.Unknown) : s.Class;
            tr.Name = (identified && (RevealNames || !Fog)) ? s.Name : null;
            double q = 0;
            if ((src & TrackSources.OwnSensor) != 0) q = 0.9;
            if ((src & TrackSources.Database) != 0 && q < 0.85) q = 0.85;
            if ((src & TrackSources.Warning) != 0 && q < 0.5) q = 0.5;
            tr.Quality = q;
            if (tr.Iff == Iff.Hostile) { p.Hostiles++; if (tr.IsMissile) p.HostileMissiles++; }
            else if (tr.Iff == Iff.Unknown) p.Unknowns++;
            if (tr.Iff != oldIff && oldIff == Iff.Unknown && tr.Iff != Iff.Unknown && tr.FirstUt < Now)
                Emit(TacEventKind.TrackIdentified, p, tr, tr.IdentifiedBy, tr.Declared && tr.IdentifiedBy == 0 ? "behaviour" : "range",
                    p.Team + ": T" + tr.Number + " identified " + IffText(tr.Iff) + " " + UnitClasses.Name(tr.Class) + ".");
            else if (tr.Id == IdState.Identified && oldId != IdState.Identified && tr.FirstUt < Now && tr.Iff == oldIff)
                Emit(TacEventKind.TrackIdentified, p, tr, tr.IdentifiedBy, "range", p.Team + ": T" + tr.Number + " identified " + UnitClasses.Name(tr.Class) + ".");
            if (tr.FirstUt == Now && !wasLive)
                Emit(TacEventKind.TrackNew, p, tr, 0, SourceText(src), p.Team + ": new track T" + tr.Number + " " + IffText(tr.Iff) + " " + DomainText(tr.Domain) + " (" + SourceText(src) + ").");
            if (tr.IsMissile && !tr.InboundReported && tr.MissileTarget != 0)
            {
                TUnit mt;
                if (_units.TryGetValue(tr.MissileTarget, out mt) && FriendOf(ti, mt))
                {
                    tr.InboundReported = true;
                    Emit(TacEventKind.MissileInbound, p, tr, tr.MissileTarget, null,
                        p.Team + ": missile T" + tr.Number + " inbound to " + mt.S.Name + ".");
                }
            }
            p.Feed.Add(new PictureEntry
            {
                Target = s.Id,
                Holders = tr.Holders,
                SoleHolder = tr.SoleHolder,
                SurveillanceHeld = tr.SurveillanceHeld,
                IdentifiedBy = tr.IdentifiedBy,
            });
        }

        private void UpdateUnheld(TeamPicture p, Track tr)
        {
            if (tr.State == TrackState.Dead) return;
            if (tr.Iff == Iff.Friend)
            {
                // an ally that turned hostile or a friend not reported: drop at once
                tr.State = TrackState.Dr;
                tr.LastSeenUt = Now - DropAfterS;
                return;
            }
            if (tr.State == TrackState.Live)
            {
                tr.State = TrackState.Dr;
                tr.StateUt = Now;
                tr.QualityAtLoss = tr.Quality;
                tr.Holders = 0;
                tr.SoleHolder = 0;
                tr.SurveillanceHeld = false;
            }
            double dt = Now - tr.LastSeenUt;
            tr.Pos = dt > 0 && tr.SpeedMps > 0 ? _geo.Destination(tr.LastPos, tr.HeadingDeg, tr.SpeedMps * dt) : tr.LastPos;
            tr.Pos.Alt = tr.LastPos.Alt;
            double f = StaleAfterS > 0 ? 1 - dt / StaleAfterS : 0;
            tr.Quality = f > 0 ? tr.QualityAtLoss * f : 0;
            if (tr.Iff == Iff.Hostile) { p.Hostiles++; if (tr.IsMissile) p.HostileMissiles++; }
            else if (tr.Iff == Iff.Unknown) p.Unknowns++;
        }

        private void UpdateGone(TeamPicture p, Track tr)
        {
            if (tr.State == TrackState.Dead) return;
            bool friend = tr.Iff == Iff.Friend;
            tr.State = TrackState.Dead;
            tr.StateUt = Now;
            tr.Quality = 0;
            if (!friend && !tr.IsMissile)
                Emit(TacEventKind.TrackLost, p, tr, 0, "destroyed", p.Team + ": T" + tr.Number + " destroyed.");
            else if (!friend)
                Emit(TacEventKind.TrackLost, p, tr, 0, "destroyed", p.Team + ": missile T" + tr.Number + " gone.");
        }

        private void BuildGod()
        {
            var p = God;
            p.Feed.Clear();
            p.Hostiles = p.Unknowns = p.Friends = p.HostileMissiles = p.InboundToOwn = 0;
            for (int i = 0; i < _list.Count; i++)
            {
                var u = _list[i];
                Track tr;
                p.ByVessel.TryGetValue(u.S.Id, out tr);
                if (!u.Alive || !u.Present)
                {
                    if (tr != null && tr.State != TrackState.Dead) { tr.State = TrackState.Dead; tr.StateUt = Now; }
                    continue;
                }
                if (tr == null) tr = AddTrack(p, u);
                var s = u.S;
                tr.State = TrackState.Live;
                tr.Sources = TrackSources.Truth;
                tr.LastSeenUt = Now;
                tr.Pos = s.Pos;
                tr.LastPos = s.Pos;
                tr.HeadingDeg = s.HeadingDeg;
                tr.SpeedMps = s.SpeedMps;
                tr.Side = s.Side;
                tr.Name = s.Name;
                tr.Class = tr.TrueClass = s.Class;
                tr.Domain = UnitClasses.DomainOf(s.Class);
                tr.IsMissile = s.IsMissile;
                tr.MissileTarget = s.IsMissile ? s.MissileTarget : 0;
                tr.Iff = Iff.Unknown;
                tr.Id = IdState.Identified;
                tr.Quality = 1;
                tr.Classified = true;
                tr.MissilesReadyKnown = s.MissilesMax > 0;
                tr.MissilesReady = s.MissilesReady;
                tr.MissilesMax = s.MissilesMax;
                int eng = 0, msl = 0;
                for (int k = 0; k < _teams.Count; k++) { eng += u.EngN[k]; msl += u.MslN[k]; }
                tr.EngagedBy = eng;
                tr.MissilesAt = msl;
                tr.FirstEngager = 0;
                p.Friends++;
            }
            for (int i = p.Tracks.Count - 1; i >= 0; i--)
            {
                var tr = p.Tracks[i];
                TUnit u;
                if ((!_units.TryGetValue(tr.Vessel, out u) || !u.Present) && tr.State != TrackState.Dead) { tr.State = TrackState.Dead; tr.StateUt = Now; }
                if (tr.State == TrackState.Dead && Now - tr.StateUt >= DeadKeepS) RemoveAt(p, i);
            }
        }

        // ------------------------------------------------------------------ threats (08 §11)

        private void ScoreThreats(TeamPicture p)
        {
            int n = p.Tracks.Count;
            if (n == 0) return;
            int budget = ThreatScoresPerStep < 1 ? 1 : ThreatScoresPerStep;
            int start = p.ThreatCursor % n;
            // missiles every step; other tracks round-robin
            for (int i = 0; i < n; i++)
            {
                var tr = p.Tracks[i];
                if (tr.Iff == Iff.Friend || tr.State == TrackState.Dead) { if (tr.Iff != Iff.Friend) tr.Threat = 0; continue; }
                if (tr.IsMissile) Score(p, tr);
            }
            int done = 0;
            for (int k = 0; k < n && done < budget; k++)
            {
                var tr = p.Tracks[(start + k) % n];
                if (tr.Iff == Iff.Friend || tr.State == TrackState.Dead || tr.IsMissile) continue;
                Score(p, tr);
                done++;
            }
            p.ThreatCursor = start + budget;
            for (int i = 0; i < n; i++)
            {
                var tr = p.Tracks[i];
                if (!tr.IsMissile || tr.State == TrackState.Dead || tr.Iff == Iff.Friend || tr.ThreatTo == 0 || tr.ThreatTo != tr.MissileTarget) continue;
                p.InboundToOwn++;
                if (!double.IsNaN(tr.Tcpa) && (double.IsNaN(p.MinTcpa) || tr.Tcpa < p.MinTcpa)) p.MinTcpa = tr.Tcpa;
            }
        }

        private void Score(TeamPicture p, Track tr)
        {
            TUnit t;
            if (!_units.TryGetValue(tr.Vessel, out t)) return;
            TUnit refU = null;
            bool inbound = false;
            if (tr.IsMissile && tr.MissileTarget != 0)
            {
                TUnit mt;
                if (_units.TryGetValue(tr.MissileTarget, out mt) && mt.Alive && FriendOf(p.Index, mt)) { refU = mt; inbound = true; }
            }
            double best = double.MaxValue;
            if (refU == null)
            {
                for (int i = 0; i < p.Own.Count; i++)
                {
                    var o = p.Own[i];
                    double d = Dist(o, t);
                    if (d < best) { best = d; refU = o; }
                }
            }
            if (refU == null)
            {
                tr.ThreatTo = 0;
                tr.ThreatRangeM = double.NaN;
                tr.Threat = 0.3 * ClassPrior(tr);
                return;
            }
            // geometry from the track's position: the unit while held, the dead-reckoned point when not (never the truth)
            double tx = t.X, ty = t.Y, tz = t.Z, vx = t.VX, vy = t.VY, vz = t.VZ;
            if (tr.State != TrackState.Live)
            {
                Vectors(tr.Pos, tr.HeadingDeg, tr.SpeedMps, out tx, out ty, out tz, out vx, out vy, out vz);
                best = double.MaxValue;
                if (!inbound)
                {
                    refU = null;
                    for (int i = 0; i < p.Own.Count; i++)
                    {
                        var o = p.Own[i];
                        double d = Chord(o, tx, ty, tz);
                        if (d < best) { best = d; refU = o; }
                    }
                    if (refU == null) return;
                }
            }
            double range = inbound || tr.State != TrackState.Live ? Chord(refU, tx, ty, tz) : best;
            double lx = refU.X - tx, ly = refU.Y - ty, lz = refU.Z - tz;
            double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            double vv = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            Aspect aspect = Aspect.Beam;
            double closure = 0;
            if (ll > 1)
            {
                if (vv > 3)
                {
                    double cos = (vx * lx + vy * ly + vz * lz) / (vv * ll);
                    aspect = cos >= 0.866 ? Aspect.Hot : cos >= 0.342 ? Aspect.Flank : cos >= -0.342 ? Aspect.Beam : cos >= -0.866 ? Aspect.Cold : Aspect.Drag;
                }
                closure = ((vx - refU.VX) * lx + (vy - refU.VY) * ly + (vz - refU.VZ) * lz) / ll;
            }
            tr.ThreatTo = refU.S.Id;
            tr.ThreatRangeM = range;
            tr.ThreatAspect = aspect;
            tr.ClosureMps = closure;
            tr.Tcpa = closure > 1 ? range / closure : double.NaN;
            double c = ClassPrior(tr);
            double env = Envelope(tr);
            double g = 1 - range / env;
            if (g < 0) g = 0;
            double ai;
            switch (aspect)
            {
                case Aspect.Hot: ai = 1; break;
                case Aspect.Flank: ai = 0.7; break;
                case Aspect.Beam: ai = vv > 3 ? 0.4 : 0.3; break;
                case Aspect.Cold: ai = 0.15; break;
                default: ai = 0; break;
            }
            double score = 0.35 * c + 0.35 * g + 0.30 * ai;
            if (inbound)
            {
                double u = double.IsNaN(tr.Tcpa) ? 0.5 : 1 - tr.Tcpa / 60.0;
                if (u < 0) u = 0;
                double m = 0.8 + 0.2 * u;
                if (m > score) score = m;
            }
            if (tr.Iff == Iff.Unknown) score *= 0.8;
            if (tr.State == TrackState.Dr) score *= 0.5 + 0.5 * tr.Quality;
            tr.Threat = score < 0 ? 0 : score > 1 ? 1 : score;
        }

        public static double ClassPrior(Track tr)
        {
            switch (tr.Class)
            {
                case UnitClass.Missile: return 0.9;
                case UnitClass.Submarine: return 0.7;
                case UnitClass.Fighter: return 0.6;
                case UnitClass.Bomber: return 0.5;
                case UnitClass.Ship: return 0.5;
                case UnitClass.Drone: return 0.4;
                case UnitClass.Carrier: return 0.4;
                case UnitClass.Ground: return 0.3;
                case UnitClass.Aew: return 0.2;
                case UnitClass.Airfield: return 0.1;
                default: return 0.4;
            }
        }

        /// <summary>Range at which a track of this class threatens (geometry term of the score), metres.</summary>
        public static double Envelope(Track tr)
        {
            switch (tr.Class)
            {
                case UnitClass.Missile: return 60000;
                case UnitClass.Fighter: return 80000;
                case UnitClass.Bomber: return 100000;
                case UnitClass.Drone: return 40000;
                case UnitClass.Ship: return 150000;
                case UnitClass.Carrier: return 100000;
                case UnitClass.Submarine: return 40000;
                default: return 60000;
            }
        }

        // ------------------------------------------------------------------ queries used by the C2 feed

        /// <summary>The team perceives the vessel now (held, database or warning).</summary>
        public bool InView(string team, uint vessel)
        {
            var p = Team(team);
            if (p == null) return false;
            var tr = p.ByVesselId(vessel);
            return tr != null && tr.State == TrackState.Live;
        }

        /// <summary>Units of <paramref name="side"/> engaging <paramref name="target"/> (weapon manager target) plus the side's missiles at it, except one unit.</summary>
        public int FriendliesEngaging(string side, uint target, uint exceptUnit)
        {
            var p = Team(side);
            TUnit t;
            if (p == null || p.Index < 0 || !_units.TryGetValue(target, out t)) return 0;
            int n = t.EngN[p.Index] + t.MslN[p.Index];
            TUnit x;
            if (exceptUnit != 0 && _units.TryGetValue(exceptUnit, out x) && x.Alive && x.TeamIdx == p.Index && x.Target == target) n--;
            return n < 0 ? 0 : n;
        }

        public int OwnContacts(uint unit, List<uint> into, int knownStamp)
        {
            TUnit u;
            if (!_units.TryGetValue(unit, out u)) { if (into.Count > 0) into.Clear(); return -1; }
            if (u.ContactsStamp == knownStamp) return knownStamp;
            into.Clear();
            into.AddRange(u.Contacts);
            return u.ContactsStamp;
        }

        public List<PictureEntry> SidePicture(string side)
        {
            var p = Team(side);
            return p != null && p.Index >= 0 ? p.Feed : null;
        }

        public double Dist(GeoPoint a, GeoPoint b) { return Geo.Distance(a, b); }

        private double Chord(TUnit a, double x, double y, double z)
        {
            double dx = a.X - x, dy = a.Y - y, dz = a.Z - z;
            double c2 = dx * dx + dy * dy + dz * dz;
            double c = Math.Sqrt(c2);
            double q = c2 / (_r * _r);
            return c * (1 + q / 24 + 3 * q * q / 640);
        }

        private void Vectors(GeoPoint p, double headingDeg, double speed, out double x, out double y, out double z, out double vx, out double vy, out double vz)
        {
            double lat = p.Lat * Deg, lon = p.Lon * Deg, h = headingDeg * Deg;
            double cl = Math.Cos(lat), sl = Math.Sin(lat), co = Math.Cos(lon), so = Math.Sin(lon);
            x = _r * cl * co;
            y = _r * cl * so;
            z = _r * sl;
            double ve = Math.Sin(h) * speed, vn = Math.Cos(h) * speed;
            vx = -so * ve - sl * co * vn;
            vy = co * ve - sl * so * vn;
            vz = cl * vn;
        }

        private double Dist(TUnit a, TUnit b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            double c2 = dx * dx + dy * dy + dz * dz;
            double c = Math.Sqrt(c2);
            double q = c2 / (_r * _r);
            return c * (1 + q / 24 + 3 * q * q / 640);
        }

        // ------------------------------------------------------------------ events

        private void MissileLaunched(TUnit m)
        {
            var e = NewEvent(TacEventKind.MissileLaunch, m.S.Side, 0, m.S.Id, m.S.MissileTarget, null);
            if (e == null) return;
            TUnit t;
            string tn = m.S.MissileTarget != 0 && _units.TryGetValue(m.S.MissileTarget, out t) ? t.S.Name : null;
            e.Text = m.S.Side + ": " + (m.S.Name ?? "missile") + " launched" + (tn != null ? " at " + tn : "") + ".";
        }

        private void Destroyed(TUnit u)
        {
            if (u.S.IsMissile) return;
            var e = NewEvent(TacEventKind.UnitDestroyed, u.S.Side, 0, u.S.Id, 0, u.Present ? "destroyed" : "vanished");
            if (e != null) e.Text = (u.S.Name ?? "vessel") + " (" + (u.S.Side ?? "no team") + ") " + (u.Present ? "destroyed." : "gone from the world.");
        }

        private void Emit(TacEventKind kind, TeamPicture p, Track tr, uint other, string reason, string text)
        {
            var e = NewEvent(kind, p.Team, tr.Number, tr.Vessel, other, reason);
            if (e != null) e.Text = text;
        }

        private TacEvent NewEvent(TacEventKind kind, string team, int track, uint vessel, uint other, string reason)
        {
            if (_events.Count >= MaxEvents) return null;
            var e = new TacEvent { Kind = kind, Team = team, Track = track, Vessel = vessel, Other = other, Ut = Now, Reason = reason };
            _events.Add(e);
            return e;
        }

        public static string IffText(Iff i)
        {
            switch (i)
            {
                case Iff.Friend: return "friend";
                case Iff.Hostile: return "hostile";
                case Iff.Neutral: return "neutral";
                case Iff.Pending: return "pending";
                default: return "unknown";
            }
        }

        public static string DomainText(Domain d)
        {
            switch (d)
            {
                case Domain.Air: return "air";
                case Domain.Surface: return "surface";
                case Domain.Subsurface: return "subsurface";
                case Domain.Missile: return "missile";
                default: return "land";
            }
        }

        public static string SourceText(TrackSources s)
        {
            if ((s & TrackSources.Truth) != 0) return "own";
            if ((s & TrackSources.OwnSensor) != 0) return "own sensors";
            if ((s & TrackSources.Warning) != 0) return "missile warning";
            if ((s & TrackSources.Database) != 0) return "datalink";
            return "none";
        }
    }
}
