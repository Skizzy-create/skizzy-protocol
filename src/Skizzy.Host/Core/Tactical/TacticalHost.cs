using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Skizzy.C2;
using Skizzy.C2.Tactical;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Tactical
{
    /// <summary>A source.tactical subscription (08 §8): full frames of one view's tracks at most once per TPE period.</summary>
    public sealed class TacticalSub
    {
        public string SubId;
        public Session Session;
        public string View;
        public double RateHz;
        public double NextUt = double.NegativeInfinity;
        public long LastSeq;
        public long Seq;
        public long Samples;
        public long Bytes;
        public int LastBytes;
        /// <summary>Engagement log entries up to this seq have been sent (main thread).</summary>
        public long LogSeq;
        public volatile bool Started;
    }

    /// <summary>
    /// The host's track picture engine slot (M2): attached with the C2 world when the BDArmory binding attaches, stepped
    /// one slice per physics step (a full period every 10 steps, 5 Hz at 50 Hz physics; tac.configure sampleHz), publishing
    /// track.*, missile.*, engagement.* and unit.destroyed events, the tac.* capabilities and the source.tactical stream the
    /// map app consumes. After each period the missile ledger follows every missile in flight and the stream snapshot is
    /// copied into a pooled frame; a background worker (<see cref="TacticalFeed"/>) serialises and sends it.
    /// </summary>
    public sealed class TacticalHost
    {
        private readonly HostCore _h;
        private TrackPictureEngine _tpe;
        private TpeWorld _feedWorld;
        private IBdaControl _control;
        private string _source;
        private int _fixed;
        private bool _mid;
        private readonly List<TacEvent> _events = new List<TacEvent>();
        private readonly List<Track> _scratch = new List<Track>();
        private volatile TacticalSub[] _subs = new TacticalSub[0];
        private readonly object _gate = new object();
        private int _nextSub;
        private readonly Dictionary<uint, string> _handles = new Dictionary<uint, string>();
        private readonly Dictionary<long, string> _trackNames = new Dictionary<long, string>();
        private readonly MissileLedger _ledger = new MissileLedger();
        private readonly List<UnitState> _unitScratch = new List<UnitState>(256);
        private readonly TacticalFeed _feed;
        private Func<uint, UnitState> _lookup;
        private IMissileOutcomes _outcomes;
        private long _publishedSeq;

        /// <summary>Main-thread cost of one sample: the whole picture period (all slices, including the binding's scan of the
        /// game), the missile ledger and the stream snapshot copy (ms).</summary>
        public double SampleMsLast, SampleMsMax, SampleMsTotal;
        public long SampleCount;
        public double SnapshotMsLast, SnapshotMsMax;

        /// <summary>Most log entries one stream frame carries (a late subscriber catches up over several frames).</summary>
        public int MaxLogPerFrame = 256;

        /// <summary>Assumed physics rate for <see cref="SampleHz"/> (KSP: 50 Hz).</summary>
        public const double PhysicsHz = 50;

        /// <summary>Physics steps per TPE period (10 = 5 Hz at 50 Hz physics).</summary>
        public int PeriodFixedUpdates = 10;

        /// <summary>Raised on the main thread after each complete TPE period (the recorder samples here).</summary>
        public event Action PeriodCompleted;

        public TacticalHost(HostCore h)
        {
            _h = h;
            _feed = new TacticalFeed(h);
        }

        public TrackPictureEngine Tpe => _tpe;
        public MissileLedger Ledger => _ledger;
        internal TacticalFeed Feed => _feed;

        /// <summary>Snapshot rate of the picture, the ledger and the stream (Hz of game time, 1-10).</summary>
        public double SampleHz
        {
            get { return PhysicsHz / PeriodFixedUpdates; }
            set { PeriodFixedUpdates = Math.Max(5, Math.Min(50, (int)Math.Round(PhysicsHz / Math.Max(1, Math.Min(10, value))))); }
        }
        public IBdaControl Control => _control;
        public string Source => _source;
        public bool Attached => _tpe != null;
        public bool WantsTicks => _tpe != null;
        public int SubscriptionCount => _subs.Length;

        /// <summary>Main thread: builds the TPE over the binding's world; returns the world the C2 engine should use.</summary>
        public IC2World Attach(IC2World world, IBdaControl control, string source)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            _tpe = new TrackPictureEngine(world);
            _feedWorld = new TpeWorld(_tpe) { StepOnRead = false };
            _ledger.Clear();
            _publishedSeq = 0;
            _outcomes = world as IMissileOutcomes;
            if (_lookup == null) _lookup = id => { var t = _tpe; return t != null ? t.Unit(id) : null; };
            _control = control;
            _source = source ?? "binding";
            _fixed = PeriodFixedUpdates - 1;
            _mid = false;
            _handles.Clear();
            _trackNames.Clear();
            // subscriptions outlive a scene change: a revert or quickload takes game time back and empties the ledger, so
            // every subscriber starts over (else NextUt waits for the old, later time and LogSeq points past the new log)
            var subs = _subs;
            for (int i = 0; i < subs.Length; i++) { subs[i].NextUt = double.NegativeInfinity; subs[i].LastSeq = -1; subs[i].LogSeq = 0; }
            _h.Log.Info("tactical: track picture engine attached (" + _source + ")");
            return _feedWorld;
        }

        public void Detach(string reason)
        {
            if (_tpe == null) return;
            _tpe = null;
            _feedWorld = null;
            _control = null;
            _outcomes = null;
            _h.Log.Info("tactical: track picture engine detached (" + reason + ")");
        }

        /// <summary>Main thread, every physics step: one TPE slice while a period runs, a new period every <see cref="PeriodFixedUpdates"/> steps.</summary>
        internal void OnFixedUpdate()
        {
            var tpe = _tpe;
            if (tpe == null) return;
            _fixed++;
            if (!_mid && _fixed < PeriodFixedUpdates) return;
            if (!_mid) _fixed = 0;
            bool done;
            try { done = tpe.StepSlice(); }
            catch (Exception e)
            {
                _h.Log.Warn("tactical: TPE step failed: " + e.Message);
                _mid = false;
                return;
            }
            _mid = !done;
            if (!done) return;
            AfterPeriod(tpe);
            var pc = PeriodCompleted;
            if (pc != null)
            {
                try { pc(); }
                catch (Exception e) { _h.Log.Warn("tactical: period listener failed: " + e.Message); }
            }
        }

        /// <summary>Main thread: runs whole periods now (tests, and the replay server between scrubs).</summary>
        public void StepNow()
        {
            var tpe = _tpe;
            if (tpe == null) return;
            tpe.Step();
            _mid = false;
            _fixed = 0;
            AfterPeriod(tpe);
            PeriodCompleted?.Invoke();
        }

        /// <summary>Main thread, once per complete period: ledger, events, stream snapshot; times the whole sample.</summary>
        private void AfterPeriod(TrackPictureEngine tpe)
        {
            try
            {
                _unitScratch.Clear();
                tpe.GetUnits(_unitScratch);
                _ledger.Update(tpe.Now, _unitScratch, _lookup, _outcomes, tpe.Geo);
                _unitScratch.Clear();
            }
            catch (Exception e) { _h.Log.Warn("tactical: missile ledger failed: " + e.Message); }
            PublishEvents(tpe);
            PublishLedger();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SampleStreams(tpe);
            double snap = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            SnapshotMsLast = snap;
            if (snap > SnapshotMsMax) SnapshotMsMax = snap;
            double ms = tpe.LastStepMs + _ledger.LastUpdateMs + snap;
            SampleMsLast = ms;
            SampleMsTotal += ms;
            SampleCount++;
            if (ms > SampleMsMax) SampleMsMax = ms;
        }

        /// <summary>engagement.fired / hit / intercepted / missed / destroyed / killed host events for new log entries.</summary>
        private void PublishLedger()
        {
            var list = _ledger.Entries;
            if (_ledger.Seq == _publishedSeq || list.Count == 0) return;
            int i = list.Count - 1;
            while (i > 0 && list[i - 1].Seq > _publishedSeq) i--;
            for (; i < list.Count; i++)
            {
                var e = list[i];
                if (e.Seq <= _publishedSeq) continue;
                _h.Events.Emit("engagement." + LedgerEntry.KindName(e.Kind), "missile", e.Text, EntryJson(e));
            }
            _publishedSeq = _ledger.Seq;
        }

        internal JsonValue EntryJson(LedgerEntry e)
        {
            var o = JsonValue.Obj().Set("seq", e.Seq).Set("ut", e.Ut).Set("kind", LedgerEntry.KindName(e.Kind));
            if (e.Missile != 0) o.Set("missile", Handle(e.Missile));
            if (e.Weapon != null) o.Set("weapon", e.Weapon);
            if (e.Team != null) o.Set("team", e.Team);
            if (e.Launcher != 0) o.Set("launcher", Handle(e.Launcher));
            if (e.LauncherName != null) o.Set("launcherName", e.LauncherName);
            if (e.Target != 0) o.Set("target", Handle(e.Target));
            if (e.TargetName != null) o.Set("targetName", e.TargetName);
            if (e.TargetTeam != null) o.Set("targetTeam", e.TargetTeam);
            if (e.Other != 0) o.Set("other", Handle(e.Other));
            if (e.OtherName != null) o.Set("otherName", e.OtherName);
            if (e.OtherTeam != null) o.Set("otherTeam", e.OtherTeam);
            if (!double.IsNaN(e.Cost)) o.Set("cost", e.Cost);
            return o.Set("source", e.Source).Set("text", e.Text);
        }

        private void PublishEvents(TrackPictureEngine tpe)
        {
            if (tpe.PendingEvents == 0) return;
            _events.Clear();
            tpe.DrainEvents(_events);
            for (int i = 0; i < _events.Count; i++)
            {
                var e = _events[i];
                var data = JsonValue.Obj().Set("team", e.Team).Set("ut", e.Ut);
                if (e.Track != 0) data.Set("track", "T" + e.Track.ToString(CultureInfo.InvariantCulture));
                if (e.Kind == TacEventKind.UnitDestroyed || e.Kind == TacEventKind.MissileLaunch) data.Set("vessel", Handle(e.Vessel));
                if (e.Other != 0) data.Set(e.Kind == TacEventKind.MissileInbound || e.Kind == TacEventKind.MissileLaunch ? "target" : "by", Handle(e.Other));
                if (e.Reason != null) data.Set("reason", e.Reason);
                string cat = e.Kind == TacEventKind.MissileLaunch || e.Kind == TacEventKind.MissileInbound ? "missile" : e.Kind == TacEventKind.UnitDestroyed ? "unit" : "track";
                _h.Events.Emit(e.Name, cat, e.Text ?? e.Name, data);
            }
        }

        internal string Handle(uint id)
        {
            string s;
            if (!_handles.TryGetValue(id, out s))
            {
                s = HandleRegistry.VesselHandle(id);
                if (_handles.Count > 4096) _handles.Clear();
                _handles[id] = s;
            }
            return s;
        }

        // ------------------------------------------------------------------ views

        private TrackPictureEngine Need()
        {
            var t = _tpe;
            if (t == null)
                throw new SkizzyError(ErrorKind.ExecutorUnavailable, "the track picture engine is not attached", "tactical_not_attached",
                    "The tactical picture needs the Skizzy BDArmory binding in a flight scene (it attaches with the C2 engine).");
            return t;
        }

        internal static bool Privileged(Session s)
        {
            return s == null || s.ActorKind == "human" || s.Role == "referee" || s.Role == "director";
        }

        /// <summary>The picture a call reads: the requested team (own side unless privileged), else the session's side, else the god view when allowed.</summary>
        internal TeamPicture View(CallContext c, string requested)
        {
            var tpe = Need();
            var s = c.Session;
            string v = requested ?? (s != null ? s.Side ?? s.Team : null);
            if (v == null || v == "god" || v == "*")
            {
                if (!(s == null || !s.Fog || Privileged(s)))
                    throw new SkizzyError(ErrorKind.PermissionDenied, "the god view needs a human, referee or director session (or fog off)", "god_view_denied",
                        "Pass view: <your side>, or connect with --side.");
                return tpe.God;
            }
            if (s != null && s.Side != null && !string.Equals(v, s.Side, StringComparison.Ordinal) && !Privileged(s))
                throw new SkizzyError(ErrorKind.PermissionDenied, "this session sees side " + s.Side + ", not " + v, "not_own_side", "Read your own side's picture.");
            var p = tpe.Team(v);
            if (p == null)
            {
                var names = new List<string>();
                foreach (var t in tpe.Teams) names.Add(t.Team);
                throw SkizzyError.NotFound(v, "unknown_team", names.Count > 0 ? "Teams: " + string.Join(", ", names.ToArray()) + " (or god)." : "No teams yet: no BDArmory units are loaded.");
            }
            return p;
        }

        private static TacUnits UnitsOf(CallContext c)
        {
            string u = c.Str("units", "nm");
            return u == "km" || u == "metric" ? TacUnits.Metric : TacUnits.Naval;
        }

        /// <summary>"T12" or 12: a track of the view; a unit reference otherwise (handle, id, name).</summary>
        internal static bool TryTrackNumber(string reference, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(reference)) return false;
            string s = reference[0] == 'T' || reference[0] == 't' ? reference.Substring(1) : null;
            return s != null && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        /// <summary>Resolves a target reference for a side: a track number of that side's picture, else a unit.</summary>
        internal uint ResolveTarget(CallContext c, string reference, string side, string pointer)
        {
            int n;
            if (TryTrackNumber(reference, out n))
            {
                var p = _tpe != null && side != null ? _tpe.Team(side) : null;
                var tr = p != null ? p.ByTrackNumber(n) : null;
                if (tr == null) throw SkizzyError.NotFound(reference, "unknown_track", "No such track in " + (side ?? "the") + " picture; list them with tac.query.");
                return tr.Vessel;
            }
            return _h.C2.ResolveUnit(reference, pointer);
        }

        private TacRef RefOf(CallContext c, string key)
        {
            string r = c.Str(key);
            if (r == null) return default(TacRef);
            var tpe = Need();
            uint id = _h.C2.ResolveUnitIn(tpe, r, "/" + key);
            var u = tpe.Unit(id);
            if (u == null) throw SkizzyError.NotFound(r, "unknown", "The track picture has no such unit.");
            return TacRef.Of(u);
        }

        // ------------------------------------------------------------------ capabilities

        public static void Register(HostCore h)
        {
            var t = h.Tactical;
            h.Register("tac.status", c => t.Status(c));
            h.Register("tac.configure", c => t.Configure(c));
            h.Register("tac.summary", c => t.Summary(c));
            h.Register("tac.query", c => t.Query(c));
            h.Register("tac.threats", c => t.Threats(c));
            h.Register("tac.track.get", c => t.TrackGet(c));
            h.Register("tac.sitrep", c => t.Sitrep(c));
            h.Register("tac.engagements", c => t.Engagements(c));
            h.Register("tac.missiles", c => t.Missiles(c));
            h.Register("tac.engagement_log", c => t.EngagementLog(c));
        }

        private JsonValue Status(CallContext c)
        {
            var tpe = _tpe;
            var r = JsonValue.Obj().Set("attached", tpe != null).Set("source", _source);
            if (tpe == null) return r.Set("text", "Track picture engine not attached (needs the BDArmory binding in flight).");
            var teams = JsonValue.Arr();
            foreach (var p in tpe.Teams)
                teams.Add(JsonValue.Obj().Set("team", p.Team).Set("tracks", p.Tracks.Count).Set("hostile", p.Hostiles).Set("unknown", p.Unknowns)
                    .Set("friend", p.Friends).Set("inboundToOwn", p.InboundToOwn));
            double mean = tpe.Steps > 0 ? tpe.TotalStepMs / tpe.Steps : 0;
            r.Set("seq", tpe.Seq).Set("ut", tpe.Now).Set("units", tpe.UnitCount).Set("teams", teams).Set("fog", tpe.Fog).Set("revealNames", tpe.RevealNames)
             .Set("periodMsMean", Math.Round(mean, 4)).Set("periodMsMax", Math.Round(tpe.MaxStepMs, 4)).Set("sliceMsMax", Math.Round(tpe.MaxSliceMs, 4))
             .Set("slicesPerPeriod", tpe.SlicesPerPeriod).Set("engagementData", tpe.HasEngagementData).Set("subscriptions", _subs.Length)
             .Set("c2Feed", _h.C2.Engine != null && _h.C2.Engine.UsesPictureFeed)
             .Set("sampleHz", Math.Round(SampleHz, 2))
             .Set("missilesInFlight", _ledger.Flying.Count).Set("logEntries", _ledger.Seq)
             .Set("mainThread", JsonValue.Obj()
                 .Set("sampleMsLast", Math.Round(SampleMsLast, 4)).Set("sampleMsMax", Math.Round(SampleMsMax, 4))
                 .Set("sampleMsMean", SampleCount > 0 ? Math.Round(SampleMsTotal / SampleCount, 4) : 0)
                 .Set("ledgerMsMax", Math.Round(_ledger.MaxUpdateMs, 4)).Set("snapshotMsMax", Math.Round(SnapshotMsMax, 4)).Set("samples", SampleCount))
             .Set("feedWorker", JsonValue.Obj()
                 .Set("frames", _feed.Frames).Set("skipped", _feed.Skipped).Set("serializeMsMax", Math.Round(_feed.MaxSerializeMs, 4))
                 .Set("serializeMsMean", _feed.Frames > 0 ? Math.Round(_feed.TotalSerializeMs / _feed.Frames, 4) : 0).Set("bytes", _feed.BytesOut));
            return r.Set("text", "TPE " + tpe.UnitCount + " units, " + tpe.Teams.Count + " teams, seq " + tpe.Seq + ", period " + mean.ToString("0.000", CultureInfo.InvariantCulture) + " ms mean" +
                                 (tpe.Fog ? ", fog" : ", fog off") + ", " + _ledger.Flying.Count + " missiles in flight, sample " + SampleHz.ToString("0.#", CultureInfo.InvariantCulture) +
                                 " Hz, main thread " + (SampleCount > 0 ? SampleMsTotal / SampleCount : 0).ToString("0.000", CultureInfo.InvariantCulture) + " ms per sample.");
        }

        private JsonValue Configure(CallContext c)
        {
            var tpe = Need();
            if (!Privileged(c.Session))
                throw new SkizzyError(ErrorKind.PermissionDenied, "picture settings apply to every side; a human, referee or director changes them", "not_privileged", "Ask the referee.");
            if (c.Has("fog")) tpe.Fog = c.Bool("fog");
            if (c.Has("revealNames")) tpe.RevealNames = c.Bool("revealNames");
            if (c.Has("dropAfterS")) tpe.DropAfterS = c.Num("dropAfterS");
            if (c.Has("sampleHz")) SampleHz = c.Num("sampleHz");
            return JsonValue.Obj().Set("fog", tpe.Fog).Set("revealNames", tpe.RevealNames).Set("dropAfterS", tpe.DropAfterS).Set("sampleHz", Math.Round(SampleHz, 2))
                .Set("text", "Picture: fog " + (tpe.Fog ? "on" : "off") + ", names " + (tpe.RevealNames ? "revealed" : "hidden") + ", drop after " + tpe.DropAfterS.ToString("0", CultureInfo.InvariantCulture) +
                             " s, sample " + SampleHz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz.");
        }

        private JsonValue Summary(CallContext c)
        {
            var tpe = Need();
            var p = View(c, c.Str("view"));
            var r = RefOf(c, "ref");
            string text = TacticalText.Summary(tpe, p, r, UnitsOf(c), _scratch);
            return JsonValue.Obj().Set("view", p.Index < 0 ? "god" : p.Team).Set("seq", tpe.Seq).Set("ut", tpe.Now)
                .Set("hostile", p.Hostiles).Set("unknown", p.Unknowns).Set("friend", p.Friends).Set("hostileMissiles", p.HostileMissiles)
                .Set("inboundToOwn", p.InboundToOwn).Set("tcpaMinS", double.IsNaN(p.MinTcpa) ? null : JsonValue.Num(Math.Round(p.MinTcpa, 1)))
                .Set("text", text);
        }

        private JsonValue Threats(CallContext c)
        {
            var tpe = Need();
            var p = View(c, c.Str("view"));
            int limit = c.Int("limit", 10);
            if (limit > 50) throw SkizzyError.InvalidParam("/limit", "at most 50 rows per call", "Filter with to:, or page.");
            uint to = 0;
            if (c.Has("to")) to = _h.C2.ResolveUnitIn(tpe, c.Str("to"), "/to");
            var units = UnitsOf(c);
            TacticalText.Threats(p, to, _scratch);
            var res = JsonValue.Obj().Set("view", p.Index < 0 ? "god" : p.Team).Set("seq", tpe.Seq).Set("ut", tpe.Now).Set("total", _scratch.Count)
                .Set("truncated", _scratch.Count > limit).Set("grammar", TacticalText.ThreatGrammar);
            if (c.Str("format", "compact") == "json")
            {
                var rows = JsonValue.Arr();
                for (int i = 0; i < _scratch.Count && i < limit; i++) rows.Add(TrackJson(tpe, p, _scratch[i], default(TacRef), units, true));
                res.Set("threats", rows);
            }
            res.Set("text", TacticalText.ThreatText(tpe, p, _scratch, limit, units));
            return res;
        }

        private JsonValue Query(CallContext c)
        {
            var tpe = Need();
            var p = View(c, c.Str("view"));
            int limit = c.Int("limit", 10);
            if (limit > 50) throw SkizzyError.InvalidParam("/limit", "at most 50 rows per call", "Filter (iff, domain, minThreat, within) or page with cursor.");
            int offset = 0;
            string cursor = c.Str("cursor");
            if (cursor != null && (!cursor.StartsWith("o", StringComparison.Ordinal) || !int.TryParse(cursor.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
                throw SkizzyError.InvalidParam("/cursor", "not a cursor from tac.query");
            var r = RefOf(c, "ref");
            var units = UnitsOf(c);
            var f = c.Params.GetObj("filter");
            var iff = f != null ? f.GetStringList("iff") : null;
            var domain = f != null ? f.GetStringList("domain") : null;
            var cls = f != null ? f.GetStringList("class") : null;
            var state = f != null ? f.GetStringList("state") : null;
            double minThreat = f != null ? f.GetDouble("minThreat", 0) : 0;
            double maxAge = f != null ? f.GetDouble("maxAgeS", double.PositiveInfinity) : double.PositiveInfinity;
            double minQ = f != null ? f.GetDouble("minQuality", 0) : 0;
            uint inbound = 0;
            if (f != null && f.GetString("inboundTo") != null) inbound = _h.C2.ResolveUnitIn(tpe, f.GetString("inboundTo"), "/filter/inboundTo");
            var within = f != null ? f.GetObj("within") : null;
            double withinM = within != null ? within.GetDouble("m", 0) : 0;
            GeoPoint withinAt = default(GeoPoint);
            if (within != null)
            {
                string wr = within.GetString("ref");
                if (wr != null)
                {
                    var u = tpe.Unit(_h.C2.ResolveUnitIn(tpe, wr, "/filter/within/ref"));
                    if (u == null) throw SkizzyError.NotFound(wr, "unknown", "No such unit in the picture.");
                    withinAt = u.Pos;
                }
                else if (r.Has) withinAt = r.Pos;
                else throw SkizzyError.InvalidParam("/filter/within/ref", "within needs ref (or the query's ref)");
            }
            var ids = f != null ? f.GetStringList("ids") : null;
            var list = new List<Track>();
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                var t = p.Tracks[i];
                if (iff != null && !iff.Contains(p.Index < 0 ? "any" : TrackPictureEngine.IffText(t.Iff)) && p.Index >= 0) continue;
                if (domain != null && !domain.Contains(TrackPictureEngine.DomainText(t.Domain))) continue;
                if (cls != null && !cls.Contains(UnitClasses.Name(t.Class))) continue;
                if (state != null && !state.Contains(StateText(t.State))) continue;
                if (state == null && t.State == TrackState.Dead) continue;
                if (t.Threat < minThreat || t.AgeS(tpe.Now) > maxAge || t.Quality < minQ) continue;
                if (inbound != 0 && !(t.IsMissile && t.MissileTarget == inbound)) continue;
                if (within != null && tpe.Geo.Distance(withinAt, t.Pos) > withinM) continue;
                if (ids != null && !ids.Contains("T" + t.Number.ToString(CultureInfo.InvariantCulture))) continue;
                list.Add(t);
            }
            string sort = c.Str("sort", "threat");
            if (sort == "range" && r.Has)
            {
                var geo = tpe.Geo;
                var at = r.Pos;
                list.Sort((a, b) => geo.Distance(at, a.Pos).CompareTo(geo.Distance(at, b.Pos)));
            }
            else if (sort == "id") list.Sort((a, b) => a.Number.CompareTo(b.Number));
            else if (sort == "age") list.Sort((a, b) => a.LastSeenUt.CompareTo(b.LastSeenUt));
            else list.Sort(TacticalText.CompareThreat);
            int total = list.Count;
            int end = Math.Min(total, offset + limit);
            var res = JsonValue.Obj().Set("view", p.Index < 0 ? "god" : p.Team).Set("seq", tpe.Seq).Set("ut", tpe.Now).Set("total", total).Set("truncated", end < total);
            if (end < total) res.Set("cursor", "o" + end.ToString(CultureInfo.InvariantCulture));
            if (c.Str("format", "compact") == "json")
            {
                var rows = JsonValue.Arr();
                for (int i = offset; i < end; i++) rows.Add(TrackJson(tpe, p, list[i], r, units, false));
                res.Set("tracks", rows);
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(TacticalText.Header("tac", tpe, p, r, units, end - offset, total));
                for (int i = offset; i < end; i++) sb.Append('\n').Append(TacticalText.Line(tpe, p, list[i], r, units));
                res.Set("text", sb.ToString()).Set("grammar", TacticalText.LineGrammar);
            }
            return res;
        }

        internal static string StateText(TrackState s) { return s == TrackState.Live ? "live" : s == TrackState.Dr ? "dr" : "dead"; }

        internal JsonValue TrackJson(TrackPictureEngine tpe, TeamPicture p, Track t, TacRef r, TacUnits units, bool threat)
        {
            var o = JsonValue.Obj().Set("id", "T" + t.Number.ToString(CultureInfo.InvariantCulture))
                .Set("iff", p.Index < 0 ? null : JsonValue.Str(TrackPictureEngine.IffText(t.Iff)))
                .Set("idState", t.Id == IdState.Identified ? "identified" : t.Id == IdState.Classified ? "classified" : "detected")
                .Set("domain", TrackPictureEngine.DomainText(t.Domain)).Set("class", UnitClasses.Name(t.Class))
                .Set("state", StateText(t.State)).Set("sources", TrackPictureEngine.SourceText(t.Sources))
                .Set("lat", Math.Round(t.Pos.Lat, 6)).Set("lon", Math.Round(t.Pos.Lon, 6)).Set("altM", Math.Round(t.Pos.Alt, 1))
                .Set("headingDeg", Math.Round(t.HeadingDeg, 1)).Set("speedMps", Math.Round(t.SpeedMps, 1))
                .Set("quality", Math.Round(t.Quality, 2)).Set("ageS", Math.Round(t.AgeS(tpe.Now), 1)).Set("threat", Math.Round(t.Threat, 2))
                .Set("engagedBy", t.EngagedBy).Set("missilesAt", t.MissilesAt).Set("holders", t.Holders);
            if (t.Name != null) o.Set("name", t.Name);
            if (p.Index < 0 || t.Iff == Iff.Friend) { o.Set("vessel", Handle(t.Vessel)); o.Set("team", t.Side); }
            else if (t.Iff != Iff.Unknown) o.Set("team", t.Side);
            if (t.IsMissile && t.MissileTarget != 0) o.Set("missileTarget", Handle(t.MissileTarget));
            if (t.ThreatTo != 0)
            {
                var to = tpe.Unit(t.ThreatTo);
                o.Set("threatTo", Handle(t.ThreatTo)).Set("threatToName", to != null ? to.Name : null).Set("aspect", TacticalText.AspectTok(t.ThreatAspect))
                 .Set("closureMps", Math.Round(t.ClosureMps, 1));
                if (!double.IsNaN(t.ThreatRangeM)) o.Set("rangeToThreatenedM", Math.Round(t.ThreatRangeM));
                if (!double.IsNaN(t.Tcpa)) o.Set("tcpaS", Math.Round(t.Tcpa, 1));
            }
            if (r.Has)
            {
                double b, rng;
                TacticalText.Braa(tpe.Geo, r.Pos, t, out b, out rng);
                o.Set("braa", JsonValue.Obj().Set("ref", r.Name).Set("bearingDeg", Math.Round(b, 1)).Set("rangeM", Math.Round(rng))
                    .Set("altM", Math.Round(t.Pos.Alt)).Set("aspect", TacticalText.AspectTok(TacticalText.AspectTo(tpe.Geo, t, r.Pos))));
            }
            if (t.MissilesReadyKnown) o.Set("missilesReady", t.MissilesReady).Set("missilesMax", t.MissilesMax);
            o.Set("line", threat ? TacticalText.ThreatLine(tpe, p, t, units) : TacticalText.Line(tpe, p, t, r, units));
            return o;
        }

        private JsonValue TrackGet(CallContext c)
        {
            var tpe = Need();
            var p = View(c, c.Str("view"));
            string tr = c.Str("track");
            int n;
            Track t = null;
            if (TryTrackNumber(tr, out n) || int.TryParse(tr, NumberStyles.None, CultureInfo.InvariantCulture, out n)) t = p.ByTrackNumber(n);
            if (t == null) throw SkizzyError.NotFound(tr ?? "", "unknown_track", "No such track in this view; list them with tac.query.");
            return TrackJson(tpe, p, t, RefOf(c, "ref"), UnitsOf(c), false);
        }

        private JsonValue Sitrep(CallContext c)
        {
            var tpe = Need();
            uint id = _h.C2.ResolveUnitIn(tpe, c.Str("unit"), "/unit");
            var u = tpe.Unit(id);
            if (u == null) throw SkizzyError.NotFound(Handle(id), "unknown", "The track picture has no such unit.");
            var s = c.Session;
            if (s != null && s.Side != null && u.Side != s.Side && !Privileged(s))
                throw new SkizzyError(ErrorKind.PermissionDenied, "a sitrep is for your own units", "not_own_side", "Ask for one of " + s.Side + "'s units.");
            var units = UnitsOf(c);
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            lines.Add("#sitrep " + TacticalText.Tok(u.Name) + " " + TacticalText.ClassTok(u.Class) + " " + (u.Side ?? "-") + (u.Alive ? "" : " DEAD") +
                      " pos " + u.Pos.Lat.ToString("0.0000", inv) + "," + u.Pos.Lon.ToString("0.0000", inv) + " " + TacticalText.Alt(u.Pos.Alt, units) +
                      " crs " + ((int)Math.Round(u.HeadingDeg) % 360).ToString("000", inv) + " kt " + (u.SpeedMps * TacticalText.KtPerMps).ToString("0", inv));
            string wm = u.HasWeaponManager ? "wm guard " + (u.GuardMode ? "on" : "off") + " missiles " + u.MissilesReady + "/" + u.MissilesMax + (u.HasGuns ? " guns" : "") : "no weapon manager";
            var p = u.Side != null ? tpe.Team(u.Side) : null;
            uint tgt = tpe.TargetOf(id);
            if (tgt != 0 && p != null)
            {
                int num = p.NumberOf(tgt);
                wm += " tgt " + (num > 0 ? "T" + num.ToString(inv) : Handle(tgt));
            }
            var e = _h.C2.Engine;
            if (e != null)
            {
                var v = e.GetUnitView(id);
                if (v != null)
                    wm += " | c2 " + (v.Task ?? "-") + " auth " + v.FireAuthority.ToString().ToUpperInvariant() + (v.Engagement != null ? " eng " + v.Engagement.Phase : "") +
                          (v.Movement != null ? " mov " + v.Movement.Phase : "");
            }
            lines.Add(wm);
            if (p != null)
            {
                var own = p.ByVesselId(id);
                TacticalText.Threats(p, id, _scratch);
                lines.Add("threats " + _scratch.Count + (own != null && own.MissilesAt > 0 ? " inbound missiles " + own.MissilesAt : ""));
                for (int i = 0; i < _scratch.Count && i < 3; i++) lines.Add(TacticalText.ThreatLine(tpe, p, _scratch[i], units));
            }
            return JsonValue.Obj().Set("unit", Handle(id)).Set("name", u.Name).Set("side", u.Side).Set("text", string.Join("\n", lines.ToArray()));
        }

        private JsonValue Engagements(CallContext c)
        {
            var tpe = Need();
            var p = View(c, c.Str("view"));
            var rows = JsonValue.Arr();
            var lines = new List<string>();
            int unengaged = 0;
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                var t = p.Tracks[i];
                if (t.State == TrackState.Dead || t.Iff == Iff.Friend) continue;
                if (t.EngagedBy == 0 && t.MissilesAt == 0) { if (t.Iff == Iff.Hostile) unengaged++; continue; }
                rows.Add(JsonValue.Obj().Set("track", "T" + t.Number.ToString(CultureInfo.InvariantCulture)).Set("class", UnitClasses.Name(t.Class))
                    .Set("engagedBy", t.EngagedBy).Set("missilesAt", t.MissilesAt).Set("threat", Math.Round(t.Threat, 2)));
                lines.Add("T" + t.Number + " " + TacticalText.ClassTok(t.Class) + " eng:" + TacticalText.Engagement(tpe, t));
            }
            return JsonValue.Obj().Set("view", p.Index < 0 ? "god" : p.Team).Set("engaged", rows).Set("unengagedHostiles", unengaged)
                .Set("text", "#eng " + rows.Count + " engaged, " + unengaged + " hostile unengaged" + (lines.Count > 0 ? "\n" + string.Join("\n", lines.ToArray()) : ""));
        }

        private JsonValue Missiles(CallContext c)
        {
            var tpe = Need();
            var p = View(c, c.Str("view"));
            int limit = c.Int("limit", 10);
            if (limit > 50) throw SkizzyError.InvalidParam("/limit", "at most 50 rows per call");
            var list = new List<Track>();
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                var t = p.Tracks[i];
                if (t.IsMissile && t.State != TrackState.Dead) list.Add(t);
            }
            list.Sort(TacticalText.CompareThreat);
            var rows = JsonValue.Arr();
            var lines = new List<string>();
            for (int i = 0; i < list.Count && i < limit; i++)
            {
                var t = list[i];
                var o = JsonValue.Obj().Set("track", "T" + t.Number.ToString(CultureInfo.InvariantCulture)).Set("iff", p.Index < 0 ? t.Side : TrackPictureEngine.IffText(t.Iff))
                    .Set("lat", Math.Round(t.Pos.Lat, 6)).Set("lon", Math.Round(t.Pos.Lon, 6)).Set("altM", Math.Round(t.Pos.Alt, 1))
                    .Set("headingDeg", Math.Round(t.HeadingDeg, 1)).Set("speedMps", Math.Round(t.SpeedMps, 1));
                if (p.Index < 0 || t.Iff != Iff.Unknown) o.Set("team", t.Side);
                string tn = null;
                if (t.MissileTarget != 0)
                {
                    var tu = tpe.Unit(t.MissileTarget);
                    int num = p.NumberOf(t.MissileTarget);
                    tn = tu != null && (p.Index < 0 || tu.Side == p.Team) ? tu.Name : num > 0 ? "T" + num.ToString(CultureInfo.InvariantCulture) : null;
                    o.Set("target", tn);
                }
                if (!double.IsNaN(t.Tcpa)) o.Set("tcpaS", Math.Round(t.Tcpa, 1));
                var m = p.Index < 0 || t.Iff == Iff.Friend ? _ledger.Find(t.Vessel) : null;
                if (m != null)
                {
                    o.Set("missile", Handle(m.Id)).Set("weapon", m.Weapon).Set("launcherName", m.LauncherName).Set("state", MissileRec.StateName(m.State))
                     .Set("firedUt", Math.Round(m.FiredUt, 1));
                    if (m.Launcher != 0) o.Set("launcher", Handle(m.Launcher));
                    if (!double.IsNaN(m.Cost)) o.Set("cost", m.Cost);
                }
                rows.Add(o);
                lines.Add("T" + t.Number + " " + (p.Index < 0 ? TacticalText.Tok(t.Side) : TacticalText.IffTok(t.Iff)) + " MSL" + (m != null ? " " + TacticalText.Tok(m.Weapon) + " from " + TacticalText.Tok(m.LauncherName) : "") + (tn != null ? " >" + TacticalText.Tok(tn) : "") +
                          (!double.IsNaN(t.Tcpa) ? " t" + t.Tcpa.ToString("0", CultureInfo.InvariantCulture) + "s" : "") + " KT" + (t.SpeedMps * TacticalText.KtPerMps).ToString("0", CultureInfo.InvariantCulture));
            }
            return JsonValue.Obj().Set("view", p.Index < 0 ? "god" : p.Team).Set("total", list.Count).Set("truncated", list.Count > limit).Set("missiles", rows)
                .Set("text", "#msl " + list.Count + " in flight" + (lines.Count > 0 ? "\n" + string.Join("\n", lines.ToArray()) : ""));
        }

        // ------------------------------------------------------------------ source.tactical (03 §5, 08 §8)

        /// <summary>The stream's columns: the M0 vessel layout first (map compatibility), then the tactical columns.</summary>
        public static readonly string[] Columns =
        {
            "vessel", "vesselName", "type", "situation", "body", "lat", "lon", "altitude", "srfSpeed", "heading", "verticalSpeed", "isActive", "crewCount", "mass",
            "track", "team", "iff", "domain", "class", "idState", "quality", "ageS", "state", "threat", "engagedBy", "missilesAt", "missileTarget", "sources",
            "launcher", "launcherName", "weapon", "missileState", "cost", "firedUt",
        };

        /// <summary>Network thread (StreamManager): a tactical subscription; the first frame follows at the next TPE period.</summary>
        internal void Subscribe(Session s, JsonValue id, JsonValue p)
        {
            var src = p.GetObj("source").GetObj("tactical");
            string view = src.GetString("view");
            var ctx = new CallContext { Host = _h, Session = s, Params = JsonValue.Obj() };
            _h.Dispatcher.EnqueueMain(new Job
            {
                Action = () =>
                {
                    try
                    {
                        var pic = View(ctx, view);
                        var sub = new TacticalSub
                        {
                            SubId = "tsub:" + Interlocked.Increment(ref _nextSub).ToString(CultureInfo.InvariantCulture),
                            Session = s,
                            View = pic.Index < 0 ? "*" : pic.Team,
                            RateHz = Math.Max(0.1, Math.Min(10, p.GetDouble("rateHz", 5))),
                        };
                        lock (_gate)
                        {
                            var n = new TacticalSub[_subs.Length + 1];
                            Array.Copy(_subs, n, _subs.Length);
                            n[_subs.Length] = sub;
                            _subs = n;
                        }
                        var res = JsonValue.Obj().Set("sub", sub.SubId).Set("effectiveRateHz", Math.Round(Math.Min(sub.RateHz, SampleHz), 2)).Set("mode", "full")
                            .Set("view", pic.Index < 0 ? "god" : pic.Team).Set("seq", 0).Set("kind", "full").Set("columns", JsonValue.ArrOf(Columns)).Set("rows", JsonValue.Arr());
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            _h.SendResult(s, id, res);
                            sub.Started = true;
                        });
                    }
                    catch (SkizzyError e) { _h.SendError(s.Conn, id, e); }
                }
            });
        }

        /// <summary>stream.unsubscribe for a tactical id; false when the id is not ours.</summary>
        internal bool Unsubscribe(Session s, JsonValue id, string subId)
        {
            if (subId == null || !subId.StartsWith("tsub:", StringComparison.Ordinal)) return false;
            TacticalSub found = null;
            lock (_gate)
            {
                int i = -1;
                for (int k = 0; k < _subs.Length; k++) if (_subs[k].Session == s && _subs[k].SubId == subId) { i = k; break; }
                if (i >= 0)
                {
                    found = _subs[i];
                    var n = new TacticalSub[_subs.Length - 1];
                    Array.Copy(_subs, 0, n, 0, i);
                    Array.Copy(_subs, i + 1, n, i, _subs.Length - i - 1);
                    _subs = n;
                }
            }
            if (found == null) throw SkizzyError.NotFound(subId, "unknown", "Unknown subscription.");
            _h.SendResult(s, id, JsonValue.Obj().Set("sub", subId).Set("ok", true).Set("samples", found.Samples));
            return true;
        }

        private void Drop(TacticalSub sub)
        {
            lock (_gate)
            {
                int i = Array.IndexOf(_subs, sub);
                if (i < 0) return;
                var n = new TacticalSub[_subs.Length - 1];
                Array.Copy(_subs, 0, n, 0, i);
                Array.Copy(_subs, i + 1, n, i, _subs.Length - i - 1);
                _subs = n;
            }
        }

        /// <summary>
        /// Main thread, after each period: decides which subscriptions are due, copies the rows of their views, the log
        /// entries they have not had and the team totals into a pooled frame, and posts it to the feed worker. No
        /// serialisation and no allocation here in steady state.
        /// </summary>
        // skizzy:hot TacticalHost.SampleStreams
        private void SampleStreams(TrackPictureEngine tpe)
        {
            var subs = _subs;
            if (subs.Length == 0) return;
            double ut = tpe.Now;
            FeedFrame f = null;
            for (int i = 0; i < subs.Length; i++)
            {
                var sub = subs[i];
                var conn = sub.Session.Conn;
                if (conn == null || !conn.IsOpen) { Drop(sub); continue; }
                if (ut < sub.NextUt - 2) sub.NextUt = double.NegativeInfinity; // game time went back (quickload): send now
                if (!sub.Started || ut < sub.NextUt || sub.LastSeq == tpe.Seq) continue;
                if (conn.QueuedBytes > 2 * 1024 * 1024) continue; // slow client: skip, it catches up with the next frame
                var p = tpe.Team(sub.View);
                if (p == null) continue;
                if (f == null)
                {
                    f = _feed.Acquire();
                    if (f == null) return; // the worker holds both frames: skip this sample, nothing is lost
                    f.Ut = ut;
                    f.TpeSeq = tpe.Seq;
                }
                sub.NextUt = ut + 1.0 / sub.RateHz - 0.01;
                sub.LastSeq = tpe.Seq;
                int vi = f.ViewIndex(sub.View);
                if (vi < 0)
                {
                    BuildView(tpe, p, f.View(sub.View, p.Index < 0));
                    vi = f.ViewCount - 1;
                }
                long from = sub.LogSeq;
                long to = Math.Min(_ledger.Seq, from + MaxLogPerFrame);
                f.AddSub(sub, vi, from, to);
                sub.LogSeq = to;
            }
            if (f == null || f.SubCount == 0) return;
            long minFrom = long.MaxValue, maxTo = 0;
            for (int i = 0; i < f.SubCount; i++)
            {
                if (f.SubFrom[i] < minFrom) minFrom = f.SubFrom[i];
                if (f.SubTo[i] > maxTo) maxTo = f.SubTo[i];
            }
            var entries = _ledger.Entries;
            if (maxTo > minFrom && entries.Count > 0)
            {
                int lo = 0, hi = entries.Count; // first entry with Seq > minFrom
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (entries[mid].Seq > minFrom) hi = mid; else lo = mid + 1;
                }
                for (int k = lo; k < entries.Count && entries[k].Seq <= maxTo; k++) f.AddEntry(entries[k]);
            }
            f.SetTotals(_ledger.Teams);
            _feed.Post(f);
        }

        /// <summary>Tests: the first source.tactical subscription.</summary>
        internal TacticalSub FirstSubscription => _subs.Length > 0 ? _subs[0] : null;

        /// <summary>Tests: the god view's rows as the next frame would carry them.</summary>
        internal void BuildGodView(FeedView v) { BuildView(Need(), Need().God, v); }

        /// <summary>Copies a view's live tracks (and, for the god view and own missiles, missiles that just ended) into frame rows.</summary>
        private void BuildView(TrackPictureEngine tpe, TeamPicture p, FeedView v)
        {
            bool god = p.Index < 0;
            var row = default(FeedRow);
            uint active = 0;
            try { var av = _h.World != null ? _h.World.ActiveVessel : null; active = av != null ? av.PersistentId : 0; }
            catch (Exception) { active = 0; }
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                var t = p.Tracks[i];
                bool named = god || t.Iff == Iff.Friend;
                if (t.State == TrackState.Dead)
                {
                    // the map ends the track at once instead of waiting for it to miss frames (it keeps dead rows idempotent)
                    if (tpe.Now - t.StateUt > DeadRowS) continue;
                    var lm = t.IsMissile && named ? _ledger.Find(t.Vessel) : null;
                    if (lm != null && lm.State != MissileFate.Flying && (god || lm.Team == p.Team)) continue; // the resolved rows below carry it
                    DeadRow(p, t, named, ref row);
                    v.Add(ref row);
                    continue;
                }
                row.Vessel = named ? Handle(t.Vessel) : TrackName(p, t, false);
                row.Name = t.Name ?? TrackName(p, t, true);
                row.Type = TypeOf(t);
                row.Situation = SituationOf(t);
                row.Lat = t.Pos.Lat; row.Lon = t.Pos.Lon; row.Alt = t.Pos.Alt; row.Speed = t.SpeedMps; row.Heading = t.HeadingDeg;
                var truth = named ? tpe.Unit(t.Vessel) : null;
                row.Climb = truth != null ? truth.ClimbMps : 0;
                row.Active = named && active != 0 && t.Vessel == active;
                row.Track = t.Number;
                row.Team = god || t.Iff != Iff.Unknown ? t.Side : null;
                row.Iff = god ? "god" : TrackPictureEngine.IffText(t.Iff);
                row.Domain = TrackPictureEngine.DomainText(t.Domain);
                row.Class = UnitClasses.Name(t.Class);
                row.IdState = t.Id == IdState.Identified ? "identified" : t.Id == IdState.Classified ? "classified" : "detected";
                row.Quality = Math.Round(t.Quality, 2);
                row.Age = Math.Round(t.AgeS(tpe.Now), 1);
                row.State = StateText(t.State);
                row.Threat = Math.Round(t.Threat, 2);
                row.EngagedBy = t.EngagedBy;
                row.MissilesAt = t.MissilesAt;
                row.MissileTarget = t.MissileTarget != 0 && (god || p.NumberOf(t.MissileTarget) > 0 || t.Iff != Iff.Friend) ? Handle(t.MissileTarget) : null;
                row.Sources = TrackPictureEngine.SourceText(t.Sources);
                row.Launcher = row.LauncherName = row.Weapon = row.MslState = null;
                row.Cost = row.FiredUt = double.NaN;
                if (t.IsMissile)
                {
                    row.MslState = "flying";
                    var m = named ? _ledger.Find(t.Vessel) : null;
                    if (m != null)
                    {
                        row.Launcher = m.Launcher != 0 ? Handle(m.Launcher) : null;
                        row.LauncherName = m.LauncherName;
                        row.Weapon = m.Weapon;
                        row.Cost = m.Cost;
                        row.FiredUt = m.FiredUt;
                    }
                }
                v.Add(ref row);
            }
            var ended = _ledger.Resolved;
            for (int i = 0; i < ended.Count; i++)
            {
                var m = ended[i];
                if (!god && m.Team != p.Team) continue;
                row.Active = false;
                row.Vessel = Handle(m.Id);
                row.Name = m.Name ?? m.Weapon;
                row.Type = "Probe";
                row.Situation = "FLYING";
                row.Lat = m.Pos.Lat; row.Lon = m.Pos.Lon; row.Alt = m.Pos.Alt; row.Speed = m.SpeedMps; row.Heading = m.HeadingDeg; row.Climb = m.ClimbMps;
                row.Track = god ? 0 : p.NumberOf(m.Id);
                row.Team = m.Team;
                row.Iff = god ? "god" : "friend";
                row.Domain = "missile";
                row.Class = "missile";
                row.IdState = "identified";
                row.Quality = 1;
                row.Age = 0;
                row.State = "dead";
                row.Threat = 0;
                row.EngagedBy = 0;
                row.MissilesAt = 0;
                row.MissileTarget = m.Target != 0 ? Handle(m.Target) : null;
                row.Sources = "own";
                row.Launcher = m.Launcher != 0 ? Handle(m.Launcher) : null;
                row.LauncherName = m.LauncherName;
                row.Weapon = m.Weapon;
                row.MslState = MissileRec.StateName(m.State);
                row.Cost = m.Cost;
                row.FiredUt = m.FiredUt;
                v.Add(ref row);
            }
        }

        /// <summary>Game seconds a destroyed track is still sent as a "dead" row (several frames, so a skipped frame loses nothing).</summary>
        public const double DeadRowS = 2;

        /// <summary>A track that died: its last position, state "dead" (missile state "gone" until the ledger resolves it).</summary>
        private void DeadRow(TeamPicture p, Track t, bool named, ref FeedRow row)
        {
            row.Vessel = named ? Handle(t.Vessel) : TrackName(p, t, false);
            row.Name = t.Name ?? TrackName(p, t, true);
            row.Type = TypeOf(t);
            row.Situation = SituationOf(t);
            row.Lat = t.Pos.Lat; row.Lon = t.Pos.Lon; row.Alt = t.Pos.Alt; row.Speed = 0; row.Heading = t.HeadingDeg; row.Climb = 0;
            row.Active = false;
            row.Track = t.Number;
            row.Team = p.Index < 0 || t.Iff != Iff.Unknown ? t.Side : null;
            row.Iff = p.Index < 0 ? "god" : TrackPictureEngine.IffText(t.Iff);
            row.Domain = TrackPictureEngine.DomainText(t.Domain);
            row.Class = UnitClasses.Name(t.Class);
            row.IdState = "detected";
            row.Quality = 0;
            row.Age = 0;
            row.State = "dead";
            row.Threat = 0;
            row.EngagedBy = 0;
            row.MissilesAt = 0;
            row.MissileTarget = null;
            row.Sources = TrackPictureEngine.SourceText(t.Sources);
            row.Launcher = row.LauncherName = row.Weapon = null;
            row.MslState = t.IsMissile ? "gone" : null;
            row.Cost = row.FiredUt = double.NaN;
        }

        // ------------------------------------------------------------------ engagement log

        private JsonValue EngagementLog(CallContext c)
        {
            Need();
            var s = c.Session;
            string view = c.Str("view") ?? (s != null ? s.Side ?? s.Team : null);
            bool god = view == null || view == "god" || view == "*";
            if (god && !(s == null || !s.Fog || Privileged(s)))
                throw new SkizzyError(ErrorKind.PermissionDenied, "the whole engagement log needs a human, referee or director session (or fog off)", "god_view_denied",
                    "Pass view: <your side>.");
            if (!god && s != null && s.Side != null && view != s.Side && !Privileged(s))
                throw new SkizzyError(ErrorKind.PermissionDenied, "this session sees side " + s.Side + ", not " + view, "not_own_side", "Read your own side's log.");
            string team = c.Str("team") ?? (god ? null : view);
            int limit = c.Int("limit", 10);
            if (limit > 50) throw SkizzyError.InvalidParam("/limit", "at most 50 rows per call", "Page with before, or poll with since.");
            long since = c.Has("since") ? (long)c.Num("since") : -1;
            long before = c.Has("before") ? (long)c.Num("before") : long.MaxValue;
            var list = _ledger.Entries;
            var picked = new List<LedgerEntry>();
            if (since >= 0)
            {
                for (int i = 0; i < list.Count && picked.Count < limit; i++)
                {
                    var e = list[i];
                    if (e.Seq <= since || e.Seq >= before || !MissileLedger.Involves(e, team)) continue;
                    picked.Add(e);
                }
            }
            else
            {
                for (int i = list.Count - 1; i >= 0 && picked.Count < limit; i--)
                {
                    var e = list[i];
                    if (e.Seq >= before || !MissileLedger.Involves(e, team)) continue;
                    picked.Add(e);
                }
            }
            int total = 0;
            for (int i = 0; i < list.Count; i++) if (MissileLedger.Involves(list[i], team)) total++;
            var totals = JsonValue.Arr();
            var tl = new List<string>();
            var inv = CultureInfo.InvariantCulture;
            foreach (var t in _ledger.Teams)
            {
                if (team != null && t.Team != team && !god) continue;
                totals.Add(JsonValue.Obj().Set("team", t.Team).Set("fired", t.Fired).Set("inFlight", t.InFlight).Set("hits", t.Hits).Set("misses", t.Misses)
                    .Set("intercepted", t.Intercepted).Set("destroyed", t.Destroyed).Set("interceptsMade", t.InterceptsMade).Set("kills", t.Kills)
                    .Set("spent", t.Spent).Set("interceptedCost", t.InterceptedCost).Set("hitCost", t.HitCost).Set("missedCost", t.MissedCost)
                    .Set("destroyedCost", t.DestroyedCost).Set("costUnknown", t.CostUnknown));
                tl.Add("#team " + TacticalText.Tok(t.Team) + " fired " + t.Fired + " (" + t.InFlight + " flying) hits " + t.Hits + " miss " + t.Misses + " intercepted " + t.Intercepted +
                       " destroyed " + t.Destroyed + " | intercepts made " + t.InterceptsMade + " kills " + t.Kills + " | funds spent " + t.Spent.ToString("0", inv) +
                       " intercepted " + t.InterceptedCost.ToString("0", inv) + " hits " + t.HitCost.ToString("0", inv) + (t.CostUnknown > 0 ? " (" + t.CostUnknown + " unpriced)" : ""));
            }
            var res = JsonValue.Obj().Set("view", god ? "god" : view).Set("total", total).Set("seq", _ledger.Seq).Set("truncated", picked.Count < total && picked.Count == limit)
                .Set("totals", totals);
            if (picked.Count > 0) res.Set("cursor", picked[picked.Count - 1].Seq);
            if (c.Str("format", "compact") == "json")
            {
                var rows = JsonValue.Arr();
                foreach (var e in picked) rows.Add(EntryJson(e));
                res.Set("entries", rows);
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("#log ").Append(picked.Count).Append(" of ").Append(total).Append(" seq ").Append(_ledger.Seq).Append(since >= 0 ? " oldest first" : " newest first");
            foreach (var l in tl) sb.Append('\n').Append(l);
            foreach (var e in picked) sb.Append('\n').Append(e.Seq).Append(" t").Append(e.Ut.ToString("0.0", inv)).Append(' ').Append(e.Text);
            return res.Set("text", sb.ToString());
        }

        private string TrackName(TeamPicture p, Track t, bool withClass)
        {
            long key = ((long)p.Index << 40) ^ ((long)t.Number << 8) ^ (withClass ? (long)t.Class + 1 : 0) ^ ((long)t.Iff << 4);
            string s;
            if (!_trackNames.TryGetValue(key, out s))
            {
                s = "T" + t.Number.ToString(CultureInfo.InvariantCulture);
                if (withClass) s += " " + TacticalText.IffTok(t.Iff) + " " + TacticalText.ClassTok(t.Class);
                if (_trackNames.Count > 8192) _trackNames.Clear();
                _trackNames[key] = s;
            }
            return s;
        }

        private static string TypeOf(Track t)
        {
            switch (t.Domain)
            {
                case Domain.Air: return "Plane";
                case Domain.Missile: return "Probe";
                case Domain.Surface: return "Ship";
                case Domain.Subsurface: return "Ship";
                default: return "Base";
            }
        }

        private static string SituationOf(Track t)
        {
            switch (t.Domain)
            {
                case Domain.Air:
                case Domain.Missile: return "FLYING";
                case Domain.Land: return "LANDED";
                default: return "SPLASHED";
            }
        }
    }
}
