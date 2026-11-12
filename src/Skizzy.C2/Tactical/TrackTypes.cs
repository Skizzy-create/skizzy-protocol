using System;
using System.Collections.Generic;

namespace Skizzy.C2.Tactical
{
    /// <summary>Identification friend or foe of a track, from the viewing team's side (08 §2).</summary>
    public enum Iff : byte
    {
        Unknown,
        Pending,
        Friend,
        Neutral,
        Hostile,
    }

    /// <summary>08 §2: identification advances detected, classified, identified; IFF stays unknown until identified under fog.</summary>
    public enum IdState : byte
    {
        Detected,
        Classified,
        Identified,
    }

    /// <summary>Aspect from the angle between the track's course and its line of sight to the reference (08 §5).</summary>
    public enum Aspect : byte
    {
        Hot,
        Flank,
        Beam,
        Cold,
        Drag,
    }

    /// <summary>Where a track's position comes from this tick.</summary>
    [Flags]
    public enum TrackSources : byte
    {
        None = 0,
        /// <summary>Own or allied unit (team truth).</summary>
        Truth = 1,
        /// <summary>A unit of the team holds it on its own radar or IRST (F5).</summary>
        OwnSensor = 2,
        /// <summary>The team's BDArmory target database (TargetInfo.detected[team]: datalink, visual, other sensors).</summary>
        Database = 4,
        /// <summary>A missile warning (F12 LastScan incoming missiles).</summary>
        Warning = 8,
    }

    public enum TrackState : byte
    {
        /// <summary>Held this tick.</summary>
        Live,
        /// <summary>Not held: dead-reckoned until dropped after <see cref="TrackPictureEngine.DropAfterS"/>.</summary>
        Dr,
        /// <summary>Destroyed: kept for <see cref="TrackPictureEngine.DeadKeepS"/>, then removed.</summary>
        Dead,
    }

    /// <summary>
    /// One perceived vessel in one team's picture. Track numbers are stable per (team, vessel) for the whole battle:
    /// a vessel lost and re-acquired keeps its number. Pooled and reused by the engine; copy what you keep.
    /// </summary>
    public sealed class Track
    {
        public int Number;
        public uint Vessel;
        /// <summary>The viewing team ("*" for the god view).</summary>
        public string Team;
        /// <summary>The vessel's real BDA team (truth). Not shown under fog unless the track is identified.</summary>
        public string Side;
        /// <summary>Vessel name: friendly tracks, the god view, and identified tracks when names are revealed; else null.</summary>
        public string Name;
        public Iff Iff;
        public IdState Id;
        public Domain Domain;
        /// <summary>Displayed class: Unknown while the track is only detected.</summary>
        public UnitClass Class;
        public UnitClass TrueClass;
        public TrackState State;
        public TrackSources Sources;
        public GeoPoint Pos;
        public double HeadingDeg;
        public double SpeedMps;
        public double FirstUt;
        public double LastSeenUt;
        public double StateUt;
        public double Quality;
        /// <summary>Own-sensor holders on the team (F5), the sole holder when there is one, and whether a surveillance unit holds it.</summary>
        public int Holders;
        public uint SoleHolder;
        public bool SurveillanceHeld;
        /// <summary>The team unit whose sensors identified it (0 = not identified by range).</summary>
        public uint IdentifiedBy;
        public bool Classified;
        public bool Declared;
        public bool IsMissile;
        /// <summary>Missile tracks: the vessel it homes on (0 unknown).</summary>
        public uint MissileTarget;
        /// <summary>Hostile tracks: team units whose weapon manager targets it, the first of them, and team missiles in flight at it.
        /// Friendly tracks: <see cref="MissilesAt"/> counts hostile missiles inbound.</summary>
        public int EngagedBy;
        public uint FirstEngager;
        public int MissilesAt;
        public double Threat;
        /// <summary>The team unit the threat is scored against (missile target or nearest own unit), range and geometry to it.</summary>
        public uint ThreatTo;
        public double ThreatRangeM = double.NaN;
        public double ClosureMps;
        public double Tcpa = double.NaN;
        public Aspect ThreatAspect;
        public bool MissilesReadyKnown;
        public int MissilesReady;
        public int MissilesMax;
        public bool Alive => State != TrackState.Dead;
        public bool InView => State == TrackState.Live;
        public bool Friendly => Iff == Iff.Friend;
        public double AgeS(double now) { return now > LastSeenUt ? now - LastSeenUt : 0; }
        public bool IsNew(double now, double newForS) { return now - FirstUt < newForS; }
        internal GeoPoint LastPos;
        internal int Slot;
        internal double QualityAtLoss;
        internal bool InboundReported;
        public override string ToString() { return "T" + Number + " (v:" + Vessel + ")"; }
    }

    /// <summary>What a side perceives of one target, the shape the C2 engine's picture is built from (<see cref="IC2PictureFeed"/>).</summary>
    public struct PictureEntry
    {
        public uint Target;
        public int Holders;
        public uint SoleHolder;
        public bool SurveillanceHeld;
        public uint IdentifiedBy;
    }

    /// <summary>One team's track table.</summary>
    public sealed class TeamPicture
    {
        public readonly string Team;
        /// <summary>Team index in the engine; -1 for the god view.</summary>
        public readonly int Index;
        /// <summary>Live, dead-reckoned and recently destroyed tracks (friendly units included).</summary>
        public readonly List<Track> Tracks = new List<Track>();
        /// <summary>Everything non-friendly the team holds this tick (own sensors, database, warnings), for the C2 engine.</summary>
        public readonly List<PictureEntry> Feed = new List<PictureEntry>();
        internal readonly Dictionary<uint, Track> ByVessel = new Dictionary<uint, Track>();
        internal readonly Dictionary<uint, int> Numbers = new Dictionary<uint, int>();
        internal readonly Dictionary<int, Track> ByNumber = new Dictionary<int, Track>();
        internal readonly Dictionary<uint, uint> IdentifiedBy = new Dictionary<uint, uint>();
        internal readonly List<TUnit> Own = new List<TUnit>();
        internal int NextNumber = 1;
        internal int ThreatCursor;
        /// <summary>Changes whenever a track is added or removed.</summary>
        public long Version;
        public int OwnUnits;
        public int Hostiles;
        public int Unknowns;
        public int Friends;
        public int HostileMissiles;
        public int InboundToOwn;
        public double MinTcpa = double.NaN;
        public int LostRecently;

        public TeamPicture(string team, int index)
        {
            Team = team;
            Index = index;
        }

        public Track ByTrackNumber(int number)
        {
            Track t;
            return ByNumber.TryGetValue(number, out t) ? t : null;
        }

        public Track ByVesselId(uint vessel)
        {
            Track t;
            return ByVessel.TryGetValue(vessel, out t) ? t : null;
        }

        /// <summary>The track number this vessel has (or had) in this team's picture; 0 if never seen.</summary>
        public int NumberOf(uint vessel)
        {
            int n;
            return Numbers.TryGetValue(vessel, out n) ? n : 0;
        }
    }

    public enum TacEventKind : byte
    {
        TrackNew,
        TrackLost,
        TrackIdentified,
        MissileLaunch,
        MissileInbound,
        UnitDestroyed,
    }

    /// <summary>A picture change (track.*, missile.*, unit.destroyed in 03's event taxonomy).</summary>
    public sealed class TacEvent
    {
        public TacEventKind Kind;
        public string Team;
        public int Track;
        public uint Vessel;
        public uint Other;
        public double Ut;
        public string Reason;
        public string Text;

        public string Name
        {
            get
            {
                switch (Kind)
                {
                    case TacEventKind.TrackNew: return "track.new";
                    case TacEventKind.TrackLost: return "track.lost";
                    case TacEventKind.TrackIdentified: return "track.identified";
                    case TacEventKind.MissileLaunch: return "missile.launch";
                    case TacEventKind.MissileInbound: return "missile.inbound";
                    default: return "unit.destroyed";
                }
            }
        }
    }

    /// <summary>
    /// Optional sensor facts a game binding can give the TPE beyond <see cref="IC2World"/>: what each weapon manager
    /// is engaging (engagement columns, FriendliesEngaging in O(1)) and missile warnings (F12 LastScan).
    /// </summary>
    public interface ITacticalSensors
    {
        /// <summary>The vessel this unit's weapon manager targets now, 0 for none.</summary>
        uint CurrentTarget(uint unit);

        /// <summary>Missiles (vessel ids) this unit's missile warning reports incoming.</summary>
        void GetMissileWarnings(uint unit, List<uint> into);
    }
}

namespace Skizzy.C2
{
    using System.Collections.Generic;
    using Skizzy.C2.Tactical;

    /// <summary>
    /// A world that can hand the C2 engine a built picture: when the engine's world implements this, the engine copies
    /// each side's picture from the track picture engine instead of reading every unit's sensors itself.
    /// </summary>
    public interface IC2PictureFeed
    {
        /// <summary>What <paramref name="side"/> perceives now; null when the feed has no picture for it.</summary>
        List<PictureEntry> SidePicture(string side);

        /// <summary>
        /// Copies <paramref name="unit"/>'s own-sensor contacts into <paramref name="into"/> when they changed since
        /// <paramref name="knownStamp"/>; returns the current stamp.
        /// </summary>
        int OwnContacts(uint unit, List<uint> into, int knownStamp);

        /// <summary>Identification range of a holder (the engine's doctrine); 0 or less = this holder cannot identify.</summary>
        System.Func<uint, double> IdentifyRange { get; set; }
    }
}
