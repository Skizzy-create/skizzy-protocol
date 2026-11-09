using System;
using System.Reflection;
using System.Text;
using Skizzy.C2;

namespace Skizzy.Bda
{
    /// <summary>
    /// Finds the BDArmory Overdrive hooks by reflection (HOOKS.md "Detecting the fork at runtime"), so the typed hook
    /// classes are only ever JIT-compiled when their members exist. On a stock BDArmory every hook is missing and
    /// the engine refuses the orders that need one (HOOK_MISSING) instead of throwing.
    /// </summary>
    public static class HookProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        public const string MissileFire = "BDArmory.Control.MissileFire";
        public const string TargetInfo = "BDArmory.Targeting.TargetInfo";
        public const string VesselRadarData = "BDArmory.Radar.VesselRadarData";
        public const string GenericAi = "BDArmory.Control.BDGenericAIBase";
        public const string TargetManager = "BDArmory.UI.BDATargetManager";
        public const string Scores = "BDArmory.Competition.CompetitionScores";
        public const string ActiveController = "BDArmory.Utils.ActiveController";

        /// <summary>Hooks present, given a resolver for BDArmory's type names (null = missing).</summary>
        public static HookSet Probe(Func<string, Type> typeOf)
        {
            if (typeOf == null) return HookSet.None;
            var mf = Safe(typeOf, MissileFire);
            var ti = Safe(typeOf, TargetInfo);
            var vrd = Safe(typeOf, VesselRadarData);
            var ai = Safe(typeOf, GenericAi);
            var tm = Safe(typeOf, TargetManager);
            var sc = Safe(typeOf, Scores);
            var h = HookSet.None;
            // Each hook is probed on its own: one that throws (a foreign or half-loaded assembly) costs only itself.
            // Parameter types are compared by name, so the probe also works on metadata-only types.
            h |= Check(HookSet.F1, () => mf != null && ti != null
                && HasMethod(mf, "SetOverrideTarget", ti.FullName, "System.Single", "System.Boolean")
                && HasMethod(mf, "ClearExternalOverride")
                && mf.GetEvent("OnExternalOverrideLost", All) != null
                && mf.GetProperty("HasExternalOverride", All) != null);
            h |= Check(HookSet.F2, () => mf?.GetField("ExternalSecondaryTargets", All) != null && mf.GetField("ExternalSecondaryTargetsOnly", All) != null);
            h |= Check(HookSet.F3, () => mf?.GetField("fireAuthority", All) != null);
            h |= Check(HookSet.F4, () => mf?.GetField("ExternalTargetFilter", All) != null);
            h |= Check(HookSet.F5, () => vrd?.GetProperty("DisplayedContacts", All) != null && vrd.GetProperty("DisplayedIRContacts", All) != null);
            h |= Check(HookSet.F6, () => ai != null && HasArity(ai, "SetWaypoints", 6) && ai.GetEvent("OnCustomWaypointReached", All) != null
                && ai.GetEvent("OnCustomWaypointRouteCompleted", All) != null && ai.GetProperty("IsCustomWaypointRoute", All) != null);
            h |= Check(HookSet.F6b, () => ai?.GetField("waypointsYieldToCombat", All) != null && ai.GetField("patrolLeashDistance", All) != null
                && ai.GetField("patrolAnchor", All) != null && ai.GetEvent("OnPatrolEngage", All) != null
                && ai.GetEvent("OnPatrolResume", All) != null);
            h |= Check(HookSet.F9, () => mf?.GetEvent("OnMissileFired", All) != null);
            h |= Check(HookSet.F10, () => tm?.GetEvent("OnTargetDetected", All) != null && tm.GetEvent("OnTargetLost", All) != null
                && sc?.GetEvent("OnDeath", All) != null);
            h |= Check(HookSet.F12, () => mf?.GetProperty("LastScan", All) != null);
            return h;
        }

        /// <summary>The stock members the binding cannot work without (weapon manager, AI, active controller, target records).</summary>
        public static bool HasStockCore(Func<string, Type> typeOf)
        {
            return Safe(typeOf, MissileFire) != null && Safe(typeOf, TargetInfo) != null
                && Safe(typeOf, GenericAi) != null && Safe(typeOf, ActiveController) != null && Safe(typeOf, TargetManager) != null;
        }

        /// <summary>Resolver over the loaded assembly named "BDArmory" (null when BDArmory is not installed).</summary>
        public static Func<string, Type> FromLoadedAssemblies()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName n;
                try { n = a.GetName(); }
                catch (Exception) { continue; }
                if (n.Name == "BDArmory")
                {
                    var asm = a;
                    return name => asm.GetType(name, false);
                }
            }
            return null;
        }

        public static string Describe(HookSet h)
        {
            if (h == HookSet.All) return "all Overdrive hooks";
            if (h == HookSet.None) return "no Overdrive hooks (stock BDArmory)";
            var sb = new StringBuilder("hooks ");
            bool first = true;
            foreach (HookSet f in new[] { HookSet.F1, HookSet.F2, HookSet.F3, HookSet.F4, HookSet.F5, HookSet.F6, HookSet.F6b, HookSet.F9, HookSet.F10, HookSet.F12 })
            {
                if ((h & f) == 0) continue;
                if (!first) sb.Append(',');
                sb.Append(f);
                first = false;
            }
            return sb.ToString();
        }

        private static Type Safe(Func<string, Type> typeOf, string name)
        {
            try { return typeOf(name); }
            catch (Exception) { return null; }
        }

        private static HookSet Check(HookSet hook, Func<bool> present)
        {
            try { return present() ? hook : HookSet.None; }
            catch (Exception) { return HookSet.None; }
        }

        private static bool HasArity(Type t, string name, int arity)
        {
            foreach (var m in t.GetMethods(All))
                if (m.Name == name && m.GetParameters().Length == arity) return true;
            return false;
        }

        /// <summary>A method with exactly these parameter type names (full names).</summary>
        private static bool HasMethod(Type t, string name, params string[] parameterTypes)
        {
            foreach (var m in t.GetMethods(All))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != parameterTypes.Length) continue;
                bool same = true;
                for (int i = 0; i < ps.Length && same; i++) same = ps[i].ParameterType.FullName == parameterTypes[i];
                if (same) return true;
            }
            return false;
        }
    }
}
