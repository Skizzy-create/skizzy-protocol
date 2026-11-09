using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Skizzy.C2;

namespace Skizzy.Bda
{
    /// <summary>
    /// One loaded vessel as the game layer reports it at a scan. Pooled: the game layer reuses these objects, so
    /// nothing may keep a reference across scans except the handles (which are the game's own objects).
    /// </summary>
    public sealed class GameUnit
    {
        public readonly UnitState S = new UnitState();
        /// <summary>The vessel (identity only).</summary>
        public object Vessel;
        /// <summary>The primary weapon manager (<c>vessel.ActiveController().WM</c>), or null.</summary>
        public object Wm;
        /// <summary>The vessel's BDA AI (<c>vessel.ActiveController().AI</c>), or null.</summary>
        public object Ai;
        /// <summary>On rails (packed): part modules exist but physics does not run.</summary>
        public bool Packed;
    }

    /// <summary>What <see cref="IBdaGame.ResolveTarget"/> found for an F1 assignment.</summary>
    public enum TargetResolve
    {
        Ok,
        /// <summary>No target record for that vessel (not loaded, unknown, or the id is a missile that is gone).</summary>
        Unresolved,
        /// <summary>The vessel exists but is not an enemy of the unit's team.</summary>
        NotHostile,
    }

    /// <summary>F6b fields of an AI (<c>waypointsYieldToCombat</c>, <c>patrolLeashDistance</c>, <c>patrolAnchor</c>).</summary>
    public struct PatrolSettings
    {
        public bool Yield;
        public float LeashM;
        /// <summary>lat, lon, altitude ASL; <see cref="HasAnchor"/> false = zero (where the engagement began).</summary>
        public GeoPoint Anchor;
        public bool HasAnchor;

        public bool SameAs(PatrolSettings o)
        {
            return Yield == o.Yield && LeashM == o.LeashM && HasAnchor == o.HasAnchor
                && (!HasAnchor || (Anchor.Lat == o.Anchor.Lat && Anchor.Lon == o.Anchor.Lon && Anchor.Alt == o.Anchor.Alt));
        }
    }

    /// <summary>
    /// The thin seam between the binding logic (<see cref="BdaBinding"/>, Unity-free and unit-tested) and KSP/BDArmory
    /// (<c>KspBdaGame</c>). Handles are the game's own objects (vessel, weapon manager, AI) passed through opaquely.
    /// Methods for a hook are only called when <see cref="Hooks"/> has it. Main thread only.
    /// </summary>
    public interface IBdaGame
    {
        double Now { get; }
        double BodyRadius { get; }
        HookSet Hooks { get; }
        /// <summary>BDArmory version and hooks found, for status lines.</summary>
        string Description { get; }

        /// <summary>
        /// Clears <paramref name="into"/> and adds the loaded units (vessels with a BDA weapon manager or AI) and
        /// missiles in flight, from the game layer's pool.
        /// </summary>
        void Scan(List<GameUnit> into);

        /// <summary>False when the handle's game object was destroyed (Unity-null).</summary>
        bool IsLive(object handle);

        void GetOwnContacts(object vessel, object wm, List<uint> into);
        bool SideDetects(string side, uint target);
        bool AreHostile(string sideA, string sideB);
        int FriendliesEngaging(string side, uint target, object exceptWm);

        // F1
        TargetResolve SetOverride(object wm, uint target, float durationS, bool sticky);
        void ClearOverride(object wm);
        bool HasOverride(object wm);
        // F2 (null clears the delegate)
        void SetSecondary(object wm, IList<uint> targets, bool only);
        // F3
        FireAuthorityMode GetFireAuthority(object wm);
        void SetFireAuthority(object wm, FireAuthorityMode mode);
        // F4 (null removes the delegate)
        void SetFilter(object wm, TargetFilter filter);
        // F6
        bool SetWaypoints(object ai, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps);
        void ClearWaypoints(object ai);
        bool IsCustomRoute(object ai);
        // F6b
        PatrolSettings GetPatrol(object ai);
        void SetPatrol(object ai, PatrolSettings p);
        // stock
        bool FlyTo(object ai, GeoPoint point);
        void ReleaseCommand(object ai);
        void SetGuard(object wm, bool on);

        // Persistent settings (saved with the vessel): capture returns an opaque baseline.
        object CaptureEngageMissiles(object wm);
        void SetEngageMissiles(object wm, bool on);
        void RestoreEngageMissiles(object wm, object baseline);
        object CaptureMissileRange(object wm);
        void ExtendMissileRange(object wm);
        void RestoreMissileRange(object wm, object baseline);
    }

    /// <summary>Reference identity for game handles (Unity objects override Equals; destroyed ones compare equal to null).</summary>
    public sealed class RefEq : IEqualityComparer<object>
    {
        public static readonly RefEq Instance = new RefEq();
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
    }
}
