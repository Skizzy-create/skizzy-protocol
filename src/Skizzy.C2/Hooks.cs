using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    /// <summary>BDArmory Overdrive hooks the binding has (07 §5; HOOKS.md "Detecting the fork at runtime").</summary>
    [Flags]
    public enum HookSet
    {
        None = 0,
        F1 = 1 << 0,   // SetOverrideTarget(target, durationS, sticky) + OnExternalOverrideLost
        F2 = 1 << 1,   // ExternalSecondaryTargets
        F3 = 1 << 2,   // fireAuthority
        F4 = 1 << 3,   // ExternalTargetFilter
        F5 = 1 << 4,   // VesselRadarData.DisplayedContacts
        F6 = 1 << 5,   // SetWaypoints(points, radius, laps, ...)
        F6b = 1 << 6,  // waypointsYieldToCombat, patrolLeashDistance, patrolAnchor
        F9 = 1 << 7,   // FireMissileAt + OnMissileFired
        F10 = 1 << 8,  // OnDeath, OnTargetDetected/Lost
        F12 = 1 << 9,  // LastScan
        All = F1 | F2 | F3 | F4 | F5 | F6 | F6b | F9 | F10 | F12,
    }

    /// <summary>Mirrors <c>BDArmory.Control.FireAuthority</c> (F3); same numeric values.</summary>
    public enum FireAuthorityMode
    {
        Free = 0,
        Tight = 1,
        Hold = 2,
    }

    /// <summary>
    /// Mirrors <c>BDArmory.Control.OverrideLostReason</c> (F1); same numeric values. BDArmory only ever appends values;
    /// the binding maps a value it does not know to <see cref="NotEngageable"/>.
    /// </summary>
    public enum OverrideLostReason
    {
        None = 0,
        Expired,
        Cleared,
        Replaced,
        TargetDestroyed,
        NotEngageable,
        Filtered,
        /// <summary>At a scan the target was no longer an enemy of the unit's team (a team change on either side).</summary>
        NotHostile,
    }

    /// <summary>
    /// The per-unit target filter the engine maintains for F4. The binding installs it once as
    /// <c>wm.ExternalTargetFilter = (mf, t) => t.Vessel != null &amp;&amp; filter.Allows(t.Vessel.persistentId)</c>;
    /// the engine then edits the allow set in place (main thread) without re-installing the delegate.
    /// <see cref="Allows"/> is a hash lookup: allocation-free and safe on BDA's hot path.
    /// </summary>
    public sealed class TargetFilter
    {
        private HashSet<uint> _allow = new HashSet<uint>();
        private HashSet<uint> _next = new HashSet<uint>();

        /// <summary>Incremented whenever the allow set changes (diagnostics and tests).</summary>
        public int Version { get; private set; }

        public uint Owner { get; }

        public TargetFilter(uint owner) { Owner = owner; }

        // skizzy:hot
        public bool Allows(uint target) { return _allow.Contains(target); }

        public int Count => _allow.Count;

        /// <summary>Copies the allow set into <paramref name="into"/> (tests, diagnostics).</summary>
        public void CopyTo(List<uint> into)
        {
            foreach (var id in _allow) into.Add(id);
        }

        /// <summary>Replaces the allow set with <paramref name="ids"/> (duplicates allowed); bumps <see cref="Version"/> only when it changed.</summary>
        internal void SetTo(List<uint> ids)
        {
            _next.Clear();
            for (int i = 0; i < ids.Count; i++) _next.Add(ids[i]);
            if (_next.SetEquals(_allow)) return;
            var t = _allow;
            _allow = _next;
            _next = t;
            Version++;
        }

        internal void Clear()
        {
            if (_allow.Count == 0) return;
            _allow.Clear();
            Version++;
        }
    }

    /// <summary>
    /// What the engine reads from the game each tick. Implemented by the KSP binding over BDArmory (TargetList,
    /// VesselRadarData.DisplayedContacts (F5), weapon managers, AIs) and by FakeBda for tests. Main thread only.
    /// </summary>
    public interface IC2World
    {
        /// <summary>Game time in seconds (UT). Monotonic while not paused.</summary>
        double Now { get; }

        /// <summary>Radius of the current body in metres (Kerbin 600 km).</summary>
        double BodyRadius { get; }

        /// <summary>Which Overdrive hooks are live (from the runtime probe).</summary>
        HookSet Hooks { get; }

        /// <summary>
        /// Adds every loaded vessel the engine may command or target: units with a BDA AI or weapon manager on any
        /// team, and missiles in flight. Dead units may be included with <c>Alive=false</c> or omitted.
        /// </summary>
        void GetUnits(List<UnitState> into);

        /// <summary>
        /// Adds the vessels that <paramref name="unit"/>'s own sensors hold (radar, sonar, IRST), excluding
        /// contacts that only reached it through the data link (F5 entries whose <c>detectedByRadar.vessel</c> is
        /// another vessel).
        /// </summary>
        void GetOwnContacts(uint unit, List<uint> into);

        /// <summary>Whether <paramref name="target"/> is in <paramref name="side"/>'s BDA target database (detected by that team).</summary>
        bool SideDetects(string side, uint target);

        /// <summary>BDArmory team hostility (different teams that are not allies).</summary>
        bool AreHostile(string sideA, string sideB);

        /// <summary>
        /// Friendly weapon managers of <paramref name="side"/> engaging <paramref name="target"/>
        /// (<c>TargetInfo.NumFriendliesEngaging</c>), or missiles of that side in flight at it, not counting
        /// <paramref name="exceptUnit"/>'s own weapon manager (the asking unit must not see its own engagement).
        /// </summary>
        int FriendliesEngaging(string side, uint target, uint exceptUnit);
    }

    /// <summary>
    /// What the engine writes. Every method is idempotent from the engine's point of view: the engine calls a
    /// method only when the desired value changed. Main thread only. The binding never throws for a dead or
    /// unknown unit; it ignores the call (and returns false where a result exists).
    /// </summary>
    /// <summary>
    /// Optional, implemented by a binding's <see cref="IBdaControl"/>: which Overdrive hooks the running BDArmory has, so
    /// the host can report them (host.stats c2.hooks, host.selfcheck; M4 checklist 0.1).
    /// </summary>
    public interface IHookReport
    {
        HookSet Hooks { get; }
        /// <summary>One line, e.g. "BDArmory 1.13.1.0, all Overdrive hooks".</summary>
        string Description { get; }
    }

    public interface IBdaControl
    {
        /// <summary>
        /// F1: <c>wm.SetOverrideTarget(targetInfo, durationS, sticky)</c>. <paramref name="durationS"/> &lt;= 0 means
        /// until cleared. Re-assigning the same target only refreshes duration and stickiness (no loss event). When the
        /// target cannot be resolved to a TargetInfo of the unit's team, the binding raises
        /// <see cref="IHookEvents.OnOverrideLost"/>(unit, target, NotEngageable) instead of failing silently.
        /// </summary>
        void SetOverrideTarget(uint unit, uint target, float durationS, bool sticky);

        /// <summary>F1: <c>wm.ClearExternalOverride()</c> (raises OnExternalOverrideLost(Cleared) if one was live).</summary>
        void ClearOverrideTarget(uint unit);

        /// <summary>
        /// F2: sets <c>wm.ExternalSecondaryTargets</c> to return these targets in priority order (the binding copies
        /// the list), and <c>ExternalSecondaryTargetsOnly = only</c>. <paramref name="targets"/> null clears the delegate.
        /// </summary>
        void SetSecondaryTargets(uint unit, IList<uint> targets, bool only);

        /// <summary>F3: <c>wm.fireAuthority</c>.</summary>
        void SetFireAuthority(uint unit, FireAuthorityMode mode);

        /// <summary>F4: installs (non-null) or removes (null) <c>wm.ExternalTargetFilter</c> backed by <paramref name="filter"/>.</summary>
        void SetTargetFilter(uint unit, TargetFilter filter);

        /// <summary>
        /// F6: <c>ai.SetWaypoints(points, radiusM, laps, maxSpeeds, guardAtEnd: false)</c>. Points are lat, lon,
        /// altitude above terrain. <paramref name="maxSpeedMps"/> &lt;= 0 keeps the AI's own speed. Returns false when
        /// the AI is not active (nothing changed).
        /// </summary>
        bool SetWaypoints(uint unit, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps);

        /// <summary>F6: <c>ai.ClearWaypoints()</c>.</summary>
        void ClearWaypoints(uint unit);

        /// <summary>
        /// F6b: <c>waypointsYieldToCombat</c>, <c>patrolLeashDistance</c> (0 = unlimited) and <c>patrolAnchor</c>
        /// (<paramref name="hasAnchor"/> false = zero, i.e. the current waypoint).
        /// </summary>
        void SetPatrolEngage(uint unit, bool yieldToCombat, float leashM, GeoPoint anchor, bool hasAnchor);

        /// <summary>Stock <c>ai.CommandFlyTo(gps)</c>; false when the AI is not active.</summary>
        bool CommandFlyTo(uint unit, GeoPoint point);

        /// <summary>Stock <c>ai.ReleaseCommand()</c>.</summary>
        void ReleaseCommand(uint unit);

        /// <summary>Stock weapon manager <c>guardMode</c>.</summary>
        void SetGuardMode(uint unit, bool on);

        /// <summary>
        /// Row 12: the unit's missile-engagement setting (BDA's weapon manager "Engage Missiles" toggle: the weapon
        /// manager flag and every weapon's <c>engageMissile</c>). The binding captures the previous values before the
        /// first change.
        /// </summary>
        void SetEngageMissiles(uint unit, bool on);

        /// <summary>
        /// Row 12: puts the missile-engagement setting back to the values captured before the first
        /// <see cref="SetEngageMissiles"/> (the engine calls this when it stops driving the setting, for example when the
        /// unit is released or its side is no longer managed).
        /// </summary>
        void RestoreEngageMissiles(uint unit);

        /// <summary>
        /// Row 25: <c>true</c> raises <c>EngageableWeapon.engageRangeMax</c> of the unit's missile launchers to the
        /// part's <c>maxStaticLaunchRange</c>; <c>false</c> restores the values captured before.
        /// </summary>
        void SetMissileRangeMax(uint unit, bool extended);
    }

    /// <summary>
    /// Hook events the binding forwards (subscribed to BDArmory's static events). The engine queues them and
    /// processes them at its next tick, so handlers return immediately and may be called from inside BDA's loop.
    /// </summary>
    public interface IHookEvents
    {
        /// <summary>F1 <c>MissileFire.OnExternalOverrideLost</c>. <paramref name="target"/> 0 when the target is gone.</summary>
        void OnOverrideLost(uint unit, uint target, OverrideLostReason reason);

        /// <summary>F10 <c>CompetitionScores.OnDeath</c> or <c>GameEvents.onVesselWillDestroy</c>, whichever comes first.</summary>
        void OnUnitDestroyed(uint unit);

        /// <summary>F10 <c>BDATargetManager.OnTargetDetected</c>.</summary>
        void OnTargetDetected(uint target, string side);

        /// <summary>F10 <c>BDATargetManager.OnTargetLost</c>.</summary>
        void OnTargetLost(uint target, string side);

        /// <summary>F6 <c>BDGenericAIBase.OnCustomWaypointReached</c>.</summary>
        void OnWaypointReached(uint unit, int index, int lap);

        /// <summary>F6 <c>BDGenericAIBase.OnCustomWaypointRouteCompleted</c>.</summary>
        void OnRouteCompleted(uint unit);

        /// <summary>F6b <c>BDGenericAIBase.OnPatrolEngage</c>.</summary>
        void OnPatrolEngage(uint unit, uint target);

        /// <summary>F6b <c>BDGenericAIBase.OnPatrolResume</c>.</summary>
        void OnPatrolResume(uint unit, bool leashBroke);

        /// <summary>F9 <c>MissileFire.OnMissileFired</c>. <paramref name="target"/> 0 when unknown.</summary>
        void OnMissileFired(uint shooter, uint missile, uint target);
    }
}
