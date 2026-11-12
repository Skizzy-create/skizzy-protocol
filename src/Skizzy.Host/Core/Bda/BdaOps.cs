using System.Collections.Generic;
using Skizzy.C2;

namespace Skizzy.Host.Core.Bda
{
    /// <summary>A weapon manager's settings and state (bda.get_weapon_manager).</summary>
    public sealed class WmInfo
    {
        public uint Unit;
        public string Name;
        public string Team;
        public bool GuardMode;
        /// <summary>"free", "tight" or "hold" (F3); "free" on a BDArmory without the hook.</summary>
        public string FireAuthority = "free";
        public double GuardRangeM, GunRangeM, GuardAngleDeg, TargetScanIntervalS;
        public int MultiTargetNum, MultiMissileTgtNum, MaxMissilesOnTarget;
        public bool ParallelFire;
        public int ParallelSalvoSize;
        public double ParallelSalvoStaggerS;
        public bool IsVip, TargetMissiles, CmOutsideGuard;
        public int WeaponChannel;
        public uint CurrentTarget;
        public bool MissileIncoming, UnderFire, OutOfAmmo;
        public readonly List<WeaponInfo> Weapons = new List<WeaponInfo>();
        public int Flares, Chaff, Smoke, Decoys;
        public int Jammers, JammersOn;
        public bool EcmOn;
        public int Radars, RadarsOn;
    }

    /// <summary>One weapon type on a unit: missiles grouped by name, guns and CIWS per name.</summary>
    public sealed class WeaponInfo
    {
        public string Name;
        /// <summary>missile, bomb, torpedo, gun, ciws, rocket, laser.</summary>
        public string Kind;
        public int Count;
        public bool Enabled = true;
        public double RangeMinM, RangeMaxM;
        public string Guidance;
        public double Ammo = double.NaN;
    }

    /// <summary>Weapon manager changes (null = leave). Applied through the part's own fields, as the PAW does.</summary>
    public sealed class WmChange
    {
        public bool? GuardMode;
        public string Team;
        public double? GuardRangeM, GunRangeM, GuardAngleDeg, TargetScanIntervalS, ParallelSalvoStaggerS;
        public int? MultiTargetNum, MultiMissileTgtNum, MaxMissilesOnTarget, ParallelSalvoSize, WeaponChannel;
        public bool? ParallelFire, IsVip, TargetMissiles, CmOutsideGuard;
    }

    public enum FireRefusal
    {
        None,
        HookMissing,
        NoWeaponManager,
        NoWeapon,
        OutOfRange,
        NotLocked,
        FireHold,
        TargetDestroyed,
        NotHostile,
        LaunchFailed,
    }

    public sealed class FireRequest
    {
        public uint Unit;
        public uint Target;
        /// <summary>Case-insensitive substring of the missile name, or a kind (missile, torpedo, bomb); null = any missile.</summary>
        public string Weapon;
        public bool CheckClearance = true;
        /// <summary>Radar-guided missiles need the target held by the unit's own radar.</summary>
        public bool RequireLock = true;
        /// <summary>
        /// Test mode (HOOKS-CHECKLIST H2): pick the missile, call F9 FireMissileAt as is and report its result (LaunchFailed
        /// when BDArmory refuses, e.g. under Hold with guard mode on); no host range, lock or fire-authority checks.
        /// </summary>
        public bool Raw;
    }

    /// <summary>The live state of every hook on one unit (bda.hook.state), read from the hook members by name.</summary>
    public sealed class HookStateInfo
    {
        public uint Unit;
        public string Name;
        public bool HasWeaponManager, GuardMode;
        public uint GuardTarget;
        // F1
        public bool HasOverride, OverrideSticky, OverrideEngageable;
        public uint OverrideTarget;
        public double OverrideRemainingS = double.NaN;
        public string LastLostReason;
        public uint LastLostTarget;
        public double LastLostUt = double.NaN;
        public int TeamAssistBlocked = -1;
        // F2, F3, F4
        public bool SecondaryInstalled, SecondaryOnly;
        public int SecondaryCount = -1;
        public string FireAuthority;
        public bool FilterInstalled;
        // F6, F6b (the vessel's active AI)
        public string AiModule;
        public bool AiActive, CustomRoute, RunningWaypoints, YieldToCombat, PatrolEngaged;
        public int WaypointIndex = -1, WaypointLap = -1, LapLimit = -1;
        public double LeashM = double.NaN;
        public uint PatrolTarget;
    }

    /// <summary>One hook of the per-hook report (bda.hooks): F1-F12, OD1 and later ids.</summary>
    public struct HookPresence
    {
        public string Id;
        public bool Present;
        public string Members;

        public HookPresence(string id, bool present, string members)
        {
            Id = id;
            Present = present;
            Members = members;
        }
    }

    public sealed class FireResult
    {
        public FireRefusal Refusal;
        public string Detail;
        public string Weapon;
        public double RangeM = double.NaN;
        public double MaxRangeM = double.NaN;
        public bool Ok => Refusal == FireRefusal.None;
    }

    /// <summary>An AI module on a vessel (pilot, surface, VTOL, orbital) with its fields when asked for.</summary>
    public sealed class AiInfo
    {
        /// <summary>Module class name (BDModulePilotAI, BDModuleSurfaceAI, BDModuleVTOLAI, BDModuleOrbitalAI, ...).</summary>
        public string Module;
        /// <summary>pilot, surface, vtol, orbital or other.</summary>
        public string Kind;
        public bool Active;
        /// <summary>The AI's own status text (taking off, extending, evading, returning, ...).</summary>
        public string State;
        public uint Target;
        public string Waypoint;
        public readonly List<AiField> Fields = new List<AiField>();
    }

    /// <summary>A KSPField of an AI module, discovered by name with its UI range.</summary>
    public sealed class AiField
    {
        public string Name;
        public string Label;
        /// <summary>float, int, bool, string or enum.</summary>
        public string Type;
        public string Value;
        public double Min = double.NaN, Max = double.NaN, Step = double.NaN;
        public string[] Options;
        public bool ReadOnly;
    }

    /// <summary>A BDArmory competition's state and scores.</summary>
    public sealed class CompetitionInfo
    {
        public bool Available;
        public bool Running;
        public bool Starting;
        public int Id;
        public double StartUt = double.NaN;
        public string Status;
        public readonly List<string> DeathOrder = new List<string>();
        public readonly List<ScoreRow> Scores = new List<ScoreRow>();
    }

    public sealed class ScoreRow
    {
        public string Vessel;
        public uint VesselId;
        public string Team;
        public bool Alive = true;
        public int Hits, ShotsFired, RocketStrikes, MissileStrikes, Kills, Assists;
        public double DamageDealt;
        public string KilledBy;
        public string DeathHow;
        public double DeathUt = double.NaN;
    }

    public sealed class CompetitionStart
    {
        public double DistanceM = 1000;
        public bool StartDespiteFailures;
        public string Tag = "skizzy";
        /// <summary>ffa or sequenced.</summary>
        public string Type = "ffa";
    }

    /// <summary>
    /// The BDArmory operations the host's bda.* capabilities need beyond the C2 binding (hooks F1-F6b stay on the
    /// binding's <see cref="IBdaControl"/>): weapon manager settings, missile fire (F9), guns, countermeasures, ECM,
    /// radars, AI modules and competitions. The BDArmory plugin implements it in game (Skizzy.Bda.Ksp.KspBdaOps); tests
    /// use a fake over FakeBda. Main thread only. Methods return null for success or a refusal reason code.
    /// </summary>
    public interface IBdaOps
    {
        string Version { get; }
        HookSet Hooks { get; }

        /// <summary>Every hook id the binding knows (F1-F12 including the not-done F8/F11, OD1, ...) with whether this BDArmory has it.</summary>
        void HookReport(List<HookPresence> into);

        /// <summary>The live hook state of a unit; false when it is unknown, dead or unloaded.</summary>
        bool HookState(uint unit, HookStateInfo into);

        /// <summary>BDArmory settings (static fields of BDArmorySettings: bool, int, float, string) whose name contains <paramref name="filter"/>.</summary>
        int ListSettings(string filter, List<AiField> into);

        /// <summary>Sets one BDArmory setting by name (in memory, not saved). Reasons: unknown_field, bad_value, read_only, unavailable.</summary>
        string SetSetting(string name, string value, out string applied);

        /// <summary>False when the unit is unknown, dead, unloaded or has no weapon manager.</summary>
        bool GetWeaponManager(uint unit, WmInfo into);

        /// <summary>Applies the non-null changes; names of applied and refused (with reason) settings are appended. Null or "no_weapon_manager".</summary>
        string Configure(uint unit, WmChange change, List<string> applied, List<string> refused);

        /// <summary>The checks a launch would make, without launching.</summary>
        FireResult CheckFire(FireRequest req);

        /// <summary>One missile through F9 FireMissileAt.</summary>
        FireResult FireMissile(FireRequest req);

        /// <summary>Guns (scope "guns"), CIWS/APS (scope "ciws") or both ("all") on or off. Returns how many weapons changed, -1 without a weapon manager.</summary>
        int SetGuns(uint unit, string scope, bool enabled);

        /// <summary>Drops countermeasures of a kind (flare, chaff, smoke, decoy). Returns how many droppers fired, -1 without a weapon manager.</summary>
        int Countermeasure(uint unit, string kind, int count);

        /// <summary>ECM jammers on or off. Returns how many jammers changed, -1 without a weapon manager.</summary>
        int SetEcm(uint unit, bool on);

        /// <summary>Radars on (active) or off (passive). Returns how many radars changed, -1 for an unknown or dead unit.</summary>
        int SetRadar(uint unit, bool active);

        /// <summary>The unit's AI modules, with their fields when <paramref name="withFields"/>. -1 for an unknown, dead or unloaded unit.</summary>
        int ListAi(uint unit, List<AiInfo> into, bool withFields);

        /// <summary>Activates or deactivates an AI module (and guard mode with it when <paramref name="guard"/> is set). Reasons: no_ai, ambiguous_ai, unknown_unit.</summary>
        string SetAiActive(uint unit, string module, bool active, bool? guard);

        /// <summary>Sets one AI field by name with range validation. Reasons: no_ai, ambiguous_ai, unknown_field, out_of_range, read_only, bad_value, unknown_unit.</summary>
        string SetAiField(uint unit, string module, string field, string value, out string applied);

        void Competition(CompetitionInfo into);

        /// <summary>Reasons: competition_running, no_competitors, unavailable.</summary>
        string StartCompetition(CompetitionStart start);

        /// <summary>Reasons: no_competition, unavailable.</summary>
        string StopCompetition();
    }
}
