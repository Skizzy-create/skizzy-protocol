using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    internal enum HoldKind
    {
        /// <summary>Orbit a fixed point (aircraft) or hold position there (ships), after move_to / move_route.</summary>
        Point,
        /// <summary>Marshal stack astern of a base after RTB (aircraft).</summary>
        Marshal,
        /// <summary>Home station after RTB (ships).</summary>
        Home,
    }

    /// <summary>What an achieved movement order leaves behind: the unit holds there until the next order or a release.</summary>
    internal sealed class HoldSpec
    {
        public HoldKind Kind;
        public GeoPoint Point;
        public uint Base;
        public int Slot;
        public double Alt;
        public double RingM;
        /// <summary>The order that set it (for log text and weapons control).</summary>
        public OrderType From;
        public WeaponsControl? Weapons;
        public GeoPoint Fed;
        public bool HasFed;
        public int Version;
        /// <summary>Ship station relative to a base (Home holds): bearing off the base heading and distance.</summary>
        public double Bearing, DistM;
    }

    internal enum RouteKind
    {
        None,
        Waypoints,
        FlyTo,
    }

    /// <summary>The control the executor wants this tick. Compared with <see cref="Applied"/>; hooks are called only on change.</summary>
    internal sealed class Desired
    {
        public FireAuthorityMode Fire;
        public bool UseFilter;
        public readonly List<uint> Allow = new List<uint>();
        public uint Override;
        public bool Sticky;
        public float Duration;
        public RouteKind Route;
        public int RouteId;
        public float Radius;
        public int Laps;
        public GeoPoint FlyTo;
        public bool Yield;
        public float Leash;
        public GeoPoint Anchor;
        public bool HasAnchor;
        public bool SetEngageMissiles;
        public bool EngageMissiles;
        public bool RangeMax;
        public bool HasSecondary;
        public readonly List<uint> Secondary = new List<uint>();

        public void Reset()
        {
            Fire = FireAuthorityMode.Free;
            UseFilter = false;
            Allow.Clear();
            Override = 0;
            Sticky = false;
            Duration = 0;
            Route = RouteKind.None;
            RouteId = 0;
            Radius = 0;
            Laps = 1;
            Yield = false;
            Leash = 0;
            HasAnchor = false;
            Anchor = default(GeoPoint);
            SetEngageMissiles = false;
            EngageMissiles = false;
            RangeMax = false;
            HasSecondary = false;
            Secondary.Clear();
        }
    }

    /// <summary>What the engine last wrote through <see cref="IBdaControl"/> (initially stock BDA: nothing).</summary>
    internal sealed class Applied
    {
        public FireAuthorityMode Fire = FireAuthorityMode.Free;
        public bool Filter;
        public uint Override;
        public bool Sticky;
        public float Duration;
        public RouteKind Route;
        public int RouteId;
        public bool Yield;
        public float Leash;
        public GeoPoint Anchor;
        public bool HasAnchor;
        public GeoPoint FlyTo;
        public bool EngageMissilesKnown;
        public bool EngageMissiles;
        public bool RangeMax;
        public bool HasSecondary;
        public readonly List<uint> Secondary = new List<uint>();

        /// <summary>Nothing of the engine's is left on the unit (the missile-engagement setting included: it is restored, row 12).</summary>
        public bool IsStock =>
            Fire == FireAuthorityMode.Free && !Filter && Override == 0 && Route == RouteKind.None && !Yield && Leash == 0 && !RangeMax && !HasSecondary &&
            !EngageMissilesKnown;
    }

    /// <summary>Everything the engine keeps about one unit.</summary>
    internal sealed class UnitRec
    {
        public readonly UnitState S = new UnitState();
        public bool Present;
        public bool Dead;
        public double LastSeenUt;

        public readonly DoctrineValues Doc = new DoctrineValues();
        public int DocVersion = -1;
        public string DocGroup;
        public string DocSide;
        public string Group;

        public UnitOrder Move;
        public UnitOrder Engage;
        public HoldSpec Hold;

        // behaviours
        public uint Divert;
        public double NextContactPick;
        public uint SelfDef;
        public uint Advance;
        public double NextAdvancePick;
        public GeoPoint AdvanceFed;
        public uint VipThreat;
        public uint Claim; // the target this unit currently counts as a claim in its side picture

        // routes the executor owns (rebuilt only when needed)
        public object RouteOwner;
        public int RouteId;
        public readonly List<GeoPoint> RoutePoints = new List<GeoPoint>();
        public bool RouteCompleted;
        public int WaypointIndex = -1;
        public int WaypointLap;

        // hook bookkeeping
        public uint BackoffTarget;
        public double BackoffUntil;
        public uint PatrolTarget;
        public bool PatrolEngaged;
        public int AiFailures;
        public bool Managed;
        public bool GuardForced;
        public double DivertSeenUt;
        public int CompletedRouteId;
        public double AiRetryAt;
        public int RouteVersion;
        public int AdvanceVersion;
        public double NextVipPick;

        public readonly Desired Want = new Desired();
        public readonly Applied Have = new Applied();
        public TargetFilter Filter;
        public readonly List<uint> Contacts = new List<uint>();
        /// <summary>The unit's side picture (null without a side) and its position on the body sphere, refreshed each tick.</summary>
        public SidePicture P;
        public double X, Y, Z;
        // Own-sensor picture, time-sliced (C2Engine.ContactSlices): this unit's contacts are re-read every few ticks and
        // what it holds is counted on the targets incrementally, so a tick only does the units whose slice is due.
        /// <summary>Tick of the next own-contact refresh (0 = at the next tick).</summary>
        public int ContactsDue;
        /// <summary>Side index this unit's holds are counted under (-1 = none counted).</summary>
        public int ContribSide = -1;
        public bool ContribSurv;
        /// <summary>The hostile contacts counted on their targets at the last refresh (records are never removed).</summary>
        public readonly List<UnitRec> Contrib = new List<UnitRec>();
        // on the target, per side index: units holding it on their own sensors, the sum of their ids (the holder when
        // there is one) and how many are surveillance platforms
        public int[] HeldN;
        public ulong[] HeldSum;
        public int[] HeldSurvN;
        // duplicate guard while one holder refreshes (radar and IRST can both report a vessel)
        public uint CountedBy;
        public int CountedTick;
        // row 12 toggle, re-evaluated at the scan rate
        public bool MissilesOn;
        public bool MissilesKnown;

        public uint Id => S.Id;
        public string Name => S.Name ?? ("v:" + S.Id);
        public bool Alive => Present && !Dead && S.Alive;

        public bool InBackoff(uint target, double now) { return target != 0 && target == BackoffTarget && now < BackoffUntil; }
    }

    internal enum HookEvtKind
    {
        OverrideLost,
        UnitDestroyed,
        TargetDetected,
        TargetLost,
        WaypointReached,
        RouteCompleted,
        PatrolEngage,
        PatrolResume,
        MissileFired,
    }

    internal struct HookEvt
    {
        public HookEvtKind Kind;
        public uint Unit;
        public uint Target;
        public OverrideLostReason Reason;
        public int Index;
        public int Lap;
        public bool Flag;
        public string Side;
    }
}
