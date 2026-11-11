using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Weapons.Missiles;
using UnityEngine;

// The typed BDArmory Overdrive hook calls. Each class touches the members of one hook only and is JIT-compiled only
// when HookProbe found that hook (HOOKS.md "Detecting the fork at runtime"): on a stock BDArmory none of these methods
// ever runs, so a missing member can never throw MissingMethodException/TypeLoadException. NoInlining keeps Mono from
// pulling a hook member into an always-compiled caller.
namespace Skizzy.Bda.Ksp
{
    internal static class HookF1
    {
        // Delegate-typed: a field typed with an Overdrive-only delegate breaks Assembly.GetTypes() on stock BDArmory.
        private static Delegate _hLost;
        private static Action<MissileFire, TargetInfo, int> _lost;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Set(MissileFire wm, TargetInfo target, float durationS, bool sticky) { wm.SetOverrideTarget(target, durationS, sticky); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Clear(MissileFire wm) { wm.ClearExternalOverride(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Has(MissileFire wm) { return wm.HasExternalOverride; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Subscribe(Action<MissileFire, TargetInfo, int> lost)
        {
            _lost = lost;
            MissileFire.OnExternalOverrideLost -= (Action<MissileFire, TargetInfo, OverrideLostReason>)_hLost;
            { var d = new Action<MissileFire, TargetInfo, OverrideLostReason>(OnLost); MissileFire.OnExternalOverrideLost += d; _hLost = d; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unsubscribe()
        {
            MissileFire.OnExternalOverrideLost -= (Action<MissileFire, TargetInfo, OverrideLostReason>)_hLost;
            _hLost = null;
            _lost = null;
        }

        private static void OnLost(MissileFire wm, TargetInfo target, OverrideLostReason reason) { _lost?.Invoke(wm, target, (int)reason); }
    }

    internal static class HookF2
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Install(MissileFire wm, Func<MissileFire, IList<TargetInfo>> provider, bool only)
        {
            wm.ExternalSecondaryTargets = provider;
            wm.ExternalSecondaryTargetsOnly = only;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Remove(MissileFire wm)
        {
            wm.ExternalSecondaryTargets = null;
            wm.ExternalSecondaryTargetsOnly = false;
        }
    }

    internal static class HookF3
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Get(MissileFire wm) { return (int)wm.fireAuthority; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Set(MissileFire wm, int mode) { wm.fireAuthority = (FireAuthority)mode; }
    }

    internal static class HookF4
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Set(MissileFire wm, Func<MissileFire, TargetInfo, bool> filter) { wm.ExternalTargetFilter = filter; }
    }

    internal static class HookF5
    {
        /// <summary>Contacts the vessel's own radar/sonar and IRST hold (data-linked contacts excluded).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OwnContacts(VesselRadarData vrd, Vessel own, List<uint> into)
        {
            var radar = vrd.DisplayedContacts;
            for (int i = 0; i < radar.Count; i++)
            {
                var c = radar[i];
                if (c.vessel == null || c.detectedByRadar == null || c.detectedByRadar.vessel != own) continue;
                into.Add(c.vessel.persistentId);
            }
            var ir = vrd.DisplayedIRContacts;
            for (int i = 0; i < ir.Count; i++)
            {
                var c = ir[i];
                if (c.vessel == null || c.detectedByIRST == null || c.detectedByIRST.vessel != own) continue;
                into.Add(c.vessel.persistentId);
            }
        }
    }

    internal static class HookF6
    {
        // Delegate-typed: a field typed with an Overdrive-only delegate breaks Assembly.GetTypes() on stock BDArmory.
        private static Delegate _hReached;
        private static Delegate _hCompleted;
        private static Action<BDGenericAIBase, int, int> _reached;
        private static Action<BDGenericAIBase> _completed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Set(BDGenericAIBase ai, List<Vector3> points, float radiusM, int laps, IList<float> maxSpeeds)
        {
            return ai.SetWaypoints(points, radiusM, laps, maxSpeeds, false, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsCustom(BDGenericAIBase ai) { return ai.IsCustomWaypointRoute; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Subscribe(Action<BDGenericAIBase, int, int> reached, Action<BDGenericAIBase> completed)
        {
            _reached = reached;
            _completed = completed;
            BDGenericAIBase.OnCustomWaypointReached -= (Action<BDGenericAIBase, int, int>)_hReached;
            { var d = new Action<BDGenericAIBase, int, int>(OnReached); BDGenericAIBase.OnCustomWaypointReached += d; _hReached = d; }
            BDGenericAIBase.OnCustomWaypointRouteCompleted -= (Action<BDGenericAIBase>)_hCompleted;
            { var d = new Action<BDGenericAIBase>(OnCompleted); BDGenericAIBase.OnCustomWaypointRouteCompleted += d; _hCompleted = d; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unsubscribe()
        {
            BDGenericAIBase.OnCustomWaypointReached -= (Action<BDGenericAIBase, int, int>)_hReached;
            _hReached = null;
            BDGenericAIBase.OnCustomWaypointRouteCompleted -= (Action<BDGenericAIBase>)_hCompleted;
            _hCompleted = null;
            _reached = null;
            _completed = null;
        }

        private static void OnReached(BDGenericAIBase ai, int index, int lap) { _reached?.Invoke(ai, index, lap); }
        private static void OnCompleted(BDGenericAIBase ai) { _completed?.Invoke(ai); }
    }

    internal static class HookF6b
    {
        // Delegate-typed: a field typed with an Overdrive-only delegate breaks Assembly.GetTypes() on stock BDArmory.
        private static Delegate _hEngage;
        private static Delegate _hResume;
        private static Action<BDGenericAIBase, Vessel> _engage;
        private static Action<BDGenericAIBase, bool> _resume;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Get(BDGenericAIBase ai, out bool yield, out float leashM, out Vector3d anchor)
        {
            yield = ai.waypointsYieldToCombat;
            leashM = ai.patrolLeashDistance;
            anchor = ai.patrolAnchor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Set(BDGenericAIBase ai, bool yield, float leashM, Vector3d anchor)
        {
            ai.waypointsYieldToCombat = yield;
            ai.patrolLeashDistance = leashM;
            ai.patrolAnchor = anchor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Subscribe(Action<BDGenericAIBase, Vessel> engage, Action<BDGenericAIBase, bool> resume)
        {
            _engage = engage;
            _resume = resume;
            BDGenericAIBase.OnPatrolEngage -= (Action<BDGenericAIBase, Vessel>)_hEngage;
            { var d = new Action<BDGenericAIBase, Vessel>(OnEngage); BDGenericAIBase.OnPatrolEngage += d; _hEngage = d; }
            BDGenericAIBase.OnPatrolResume -= (Action<BDGenericAIBase, bool>)_hResume;
            { var d = new Action<BDGenericAIBase, bool>(OnResume); BDGenericAIBase.OnPatrolResume += d; _hResume = d; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unsubscribe()
        {
            BDGenericAIBase.OnPatrolEngage -= (Action<BDGenericAIBase, Vessel>)_hEngage;
            _hEngage = null;
            BDGenericAIBase.OnPatrolResume -= (Action<BDGenericAIBase, bool>)_hResume;
            _hResume = null;
            _engage = null;
            _resume = null;
        }

        private static void OnEngage(BDGenericAIBase ai, Vessel target) { _engage?.Invoke(ai, target); }
        private static void OnResume(BDGenericAIBase ai, bool leashBroke) { _resume?.Invoke(ai, leashBroke); }
    }

    internal static class HookF9
    {
        // Delegate-typed: a field typed with an Overdrive-only delegate breaks Assembly.GetTypes() on stock BDArmory.
        private static Delegate _hFired;
        private static Action<MissileFire, MissileBase, Vessel> _fired;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Subscribe(Action<MissileFire, MissileBase, Vessel> fired)
        {
            _fired = fired;
            MissileFire.OnMissileFired -= (Action<MissileFire, MissileBase, Vessel>)_hFired;
            { var d = new Action<MissileFire, MissileBase, Vessel>(OnFired); MissileFire.OnMissileFired += d; _hFired = d; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unsubscribe()
        {
            MissileFire.OnMissileFired -= (Action<MissileFire, MissileBase, Vessel>)_hFired;
            _hFired = null;
            _fired = null;
        }

        private static void OnFired(MissileFire wm, MissileBase missile, Vessel target) { _fired?.Invoke(wm, missile, target); }
    }

    internal static class HookF10
    {
        // Delegate-typed: a field typed with an Overdrive-only delegate breaks Assembly.GetTypes() on stock BDArmory.
        private static Delegate _hDetected;
        private static Delegate _hTLost;
        private static Delegate _hDeath;
        private static Action<TargetInfo, BDTeam> _detected, _lost;
        private static Action<string> _death;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Subscribe(Action<TargetInfo, BDTeam> detected, Action<TargetInfo, BDTeam> lost, Action<string> death)
        {
            _detected = detected;
            _lost = lost;
            _death = death;
            BDATargetManager.OnTargetDetected -= (Action<TargetInfo, BDTeam>)_hDetected;
            { var d = new Action<TargetInfo, BDTeam>(OnDetected); BDATargetManager.OnTargetDetected += d; _hDetected = d; }
            BDATargetManager.OnTargetLost -= (Action<TargetInfo, BDTeam>)_hTLost;
            { var d = new Action<TargetInfo, BDTeam>(OnLost); BDATargetManager.OnTargetLost += d; _hTLost = d; }
            CompetitionScores.OnDeath -= (Action<string, string, DamageFrom, AliveState>)_hDeath;
            { var d = new Action<string, string, DamageFrom, AliveState>(OnDeath); CompetitionScores.OnDeath += d; _hDeath = d; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unsubscribe()
        {
            BDATargetManager.OnTargetDetected -= (Action<TargetInfo, BDTeam>)_hDetected;
            _hDetected = null;
            BDATargetManager.OnTargetLost -= (Action<TargetInfo, BDTeam>)_hTLost;
            _hTLost = null;
            CompetitionScores.OnDeath -= (Action<string, string, DamageFrom, AliveState>)_hDeath;
            _hDeath = null;
            _detected = _lost = null;
            _death = null;
        }

        private static void OnDetected(TargetInfo t, BDTeam team) { _detected?.Invoke(t, team); }
        private static void OnLost(TargetInfo t, BDTeam team) { _lost?.Invoke(t, team); }
        private static void OnDeath(string victim, string attacker, DamageFrom from, AliveState state) { _death?.Invoke(victim); }
    }
}
