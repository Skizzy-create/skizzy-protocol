using System;
using System.Collections.Generic;
using Skizzy.Bda;
using Skizzy.C2;
using Xunit;

#pragma warning disable CS0067, CS0649, CS0169 // look-alike types only need the members to exist

namespace Skizzy.Ksp.Tests.StockBda
{
    // A stock BDArmory: the stock members only.
    public class TargetInfo { }
    public class MissileFire
    {
        public void SetOverrideTarget(TargetInfo t) { }
        public bool guardMode;
    }
    public class BDGenericAIBase
    {
        public void ClearWaypoints() { }
        public void SetWaypoints(List<object> pts) { }
    }
    public class ActiveController { }
    public class BDATargetManager { }
    public class VesselRadarData { }
}

namespace Skizzy.Ksp.Tests.OverdriveBda
{
    public class TargetInfo { }
    public class MissileFire
    {
        public void SetOverrideTarget(TargetInfo t) { }
        public void SetOverrideTarget(TargetInfo t, float d, bool sticky) { }
        public void ClearExternalOverride() { }
        public static event Action<MissileFire, TargetInfo, int> OnExternalOverrideLost;
        public bool HasExternalOverride => false;
        public Func<MissileFire, IList<TargetInfo>> ExternalSecondaryTargets;
        public bool ExternalSecondaryTargetsOnly;
        public int fireAuthority;
        public Func<MissileFire, TargetInfo, bool> ExternalTargetFilter;
        public static event Action<MissileFire, object, object> OnMissileFired;
        public int LastScan => 0;
    }
    public class VesselRadarData
    {
        public IReadOnlyList<object> DisplayedContacts => null;
        public IReadOnlyList<object> DisplayedIRContacts => null;
    }
    public class BDGenericAIBase
    {
        public bool SetWaypoints(List<object> p, float r, int laps, IList<float> s, bool g, IList<float> radii) { return true; }
        public bool IsCustomWaypointRoute => false;
        public static event Action<BDGenericAIBase, int, int> OnCustomWaypointReached;
        public static event Action<BDGenericAIBase> OnCustomWaypointRouteCompleted;
        public bool waypointsYieldToCombat;
        public float patrolLeashDistance;
        public object patrolAnchor;
        public static event Action<BDGenericAIBase, object> OnPatrolEngage;
        public static event Action<BDGenericAIBase, bool> OnPatrolResume;
    }
    public class ActiveController { }
    public class BDATargetManager
    {
        public static event Action<TargetInfo, object> OnTargetDetected;
        public static event Action<TargetInfo, object> OnTargetLost;
    }
    public class CompetitionScores
    {
        public static event Action<string, string, int, int> OnDeath;
    }
}

namespace Skizzy.Ksp.Tests
{
    public class ProbeAndClassifierTests
    {
        private static Func<string, Type> Resolver(string ns)
        {
            var map = new Dictionary<string, string>
            {
                { HookProbe.MissileFire, "MissileFire" }, { HookProbe.TargetInfo, "TargetInfo" },
                { HookProbe.VesselRadarData, "VesselRadarData" }, { HookProbe.GenericAi, "BDGenericAIBase" },
                { HookProbe.TargetManager, "BDATargetManager" }, { HookProbe.Scores, "CompetitionScores" },
                { HookProbe.ActiveController, "ActiveController" },
            };
            return name => map.TryGetValue(name, out var n) ? typeof(ProbeAndClassifierTests).Assembly.GetType("Skizzy.Ksp.Tests." + ns + "." + n, false) : null;
        }

        [Fact]
        public void Probe_finds_no_hooks_on_a_stock_bdarmory_and_every_hook_on_overdrive()
        {
            Assert.Equal(HookSet.None, HookProbe.Probe(Resolver("StockBda")));
            Assert.True(HookProbe.HasStockCore(Resolver("StockBda")));
            Assert.Equal(HookSet.All, HookProbe.Probe(Resolver("OverdriveBda")));
            Assert.Equal(HookSet.None, HookProbe.Probe(null));
            Assert.Equal(HookSet.None, HookProbe.Probe(_ => throw new InvalidOperationException("broken loader")));
            Assert.False(HookProbe.HasStockCore(_ => null));
        }

        [Fact]
        public void Probe_of_a_partial_build_reports_each_hook_separately()
        {
            var od = Resolver("OverdriveBda");
            // no CompetitionScores (F10 needs both target events and OnDeath), no radar data (F5)
            Func<string, Type> partial = n => n == HookProbe.Scores || n == HookProbe.VesselRadarData ? null : od(n);
            var h = HookProbe.Probe(partial);
            Assert.Equal(HookSet.All & ~(HookSet.F10 | HookSet.F5), h);
            Assert.Contains("F1", HookProbe.Describe(h));
            Assert.DoesNotContain("F10", HookProbe.Describe(h));
            Assert.Equal("no Overdrive hooks (stock BDArmory)", HookProbe.Describe(HookSet.None));
        }

        [Fact]
        public void Probe_of_the_real_overdrive_bdarmory_finds_every_hook_when_refs_has_it()
        {
            // refs/BDArmory.dll is git-ignored (never committed); skip quietly where it is absent (CI).
            var dll = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "refs", "BDArmory.dll");
            if (!System.IO.File.Exists(dll)) return;
            var ksp = RefsResolver.KspDir(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dll))));
            if (ksp == null || !System.IO.Directory.Exists(System.IO.Path.Combine(ksp, "KSP_x64_Data", "Managed"))) return;
            using var mlc = new System.Reflection.MetadataLoadContext(new RefsResolver(dll), "mscorlib");
            var asm = mlc.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dll));
            Func<string, Type> typeOf = n => asm.GetType(n, false);
            Assert.True(HookProbe.HasStockCore(typeOf));
            Assert.Equal(HookSet.All, HookProbe.Probe(typeOf));
        }

        private sealed class RefsResolver : System.Reflection.MetadataAssemblyResolver
        {
            private readonly string _dir;
            private readonly string _ksp;
            public RefsResolver(string dll)
            {
                _dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dll));
                _ksp = KspDir(System.IO.Path.GetDirectoryName(_dir));
            }

            // KSPDIR from the environment or the untracked Directory.Build.props.user (as the build does).
            public static string KspDir(string repoRoot)
            {
                var env = Environment.GetEnvironmentVariable("KSPDIR");
                if (!string.IsNullOrEmpty(env)) return env;
                var user = System.IO.Path.Combine(repoRoot, "Directory.Build.props.user");
                if (!System.IO.File.Exists(user)) return null;
                var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(user), @"<KSPDIR>\s*([^<]+?)\s*</KSPDIR>");
                return m.Success ? m.Groups[1].Value : null;
            }
            public override System.Reflection.Assembly Resolve(System.Reflection.MetadataLoadContext context, System.Reflection.AssemblyName name)
            {
                foreach (var dir in new[] { _dir, _ksp == null ? null : System.IO.Path.Combine(_ksp, "KSP_x64_Data", "Managed"),
                                            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() })
                {
                    if (dir == null) continue;
                    var p = System.IO.Path.Combine(dir, name.Name + ".dll");
                    if (System.IO.File.Exists(p)) return context.LoadFromAssemblyPath(p);
                }
                return null;
            }
        }

        [Theory]
        [InlineData("Cobra 1", "Cobra")]
        [InlineData("Cobra 12", "Cobra")]
        [InlineData("Cobra 1-2", "Cobra")]
        [InlineData("Cobra-3", "Cobra")]
        [InlineData("Cobra_4", "Cobra")]
        [InlineData("[UAV] Viper 2", "Viper")]
        [InlineData("Red Fox #3", "Red Fox")]
        [InlineData("Kestrel", null)]
        [InlineData("[CV] Kestrel", null)]
        [InlineData("12", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Callsign_group_is_the_name_without_its_trailing_number(string name, string group)
        {
            Assert.Equal(group, UnitClassifier.CallsignGroup(name));
        }

        [Fact]
        public void Name_tags_win_then_ai_type_and_parts_decide()
        {
            Assert.Equal(UnitClass.Carrier, UnitClassifier.Classify(new VesselFeatures { Name = "[CV] Kestrel", PilotAi = true }).Class);
            Assert.True(UnitClassifier.Classify(new VesselFeatures { Name = "Home [af]" }).IsBase);
            Assert.Equal(UnitClass.Aew, UnitClassifier.Classify(new VesselFeatures { Name = "Eye [AWACS]", SurfaceAi = true }).Class);
            Assert.Equal(UnitClass.Bomber, UnitClassifier.Classify(new VesselFeatures { Name = "[BMR] Heavy 1", PilotAi = true, Armed = true }).Class);
            Assert.Equal(UnitClass.Submarine, UnitClassifier.Classify(new VesselFeatures { Name = "[SSN] Deep" }).Class);
            Assert.Equal(UnitClass.Unknown, UnitClassifier.FromTag("[XYZ] [Squadron] Bob"));

            Assert.Equal(UnitClass.Fighter, UnitClassifier.Classify(new VesselFeatures { Name = "Cobra 1", PilotAi = true, Armed = true, MassT = 12, Crewed = false }).Class);
            Assert.Equal(UnitClass.Drone, UnitClassifier.Classify(new VesselFeatures { Name = "Gnat 1", PilotAi = true, Armed = true, MassT = 1.2 }).Class);
            Assert.Equal(UnitClass.Fighter, UnitClassifier.Classify(new VesselFeatures { Name = "Gnat 2", PilotAi = true, Armed = true, MassT = 1.2, Crewed = true }).Class);
            Assert.Equal(UnitClass.Aew, UnitClassifier.Classify(new VesselFeatures { Name = "Eye", PilotAi = true, HasRadar = true, MassT = 30 }).Class);
            Assert.Equal(UnitClass.Submarine, UnitClassifier.Classify(new VesselFeatures { SurfaceAi = true, Submarine = true }).Class);
            var cv = UnitClassifier.Classify(new VesselFeatures { SurfaceAi = true, CarrierParts = 3 });
            Assert.Equal(UnitClass.Carrier, cv.Class);
            Assert.True(cv.IsBase);
            Assert.Equal(UnitClass.Carrier, UnitClassifier.Classify(new VesselFeatures { Name = "Big Carrier", SurfaceAi = true }).Class);
            Assert.Equal(UnitClass.Ship, UnitClassifier.Classify(new VesselFeatures { Name = "Halberd", SurfaceAi = true }).Class);
            Assert.Equal(UnitClass.Ground, UnitClassifier.Classify(new VesselFeatures { SurfaceAi = true, LandVehicle = true }).Class);
            Assert.Equal(UnitClass.Airfield, UnitClassifier.Classify(new VesselFeatures { SurfaceAi = true, Stationary = true, CarrierParts = 1 }).Class);
            Assert.Equal(UnitClass.Ship, UnitClassifier.Classify(new VesselFeatures { Splashed = true }).Class);
            Assert.Equal(UnitClass.Airfield, UnitClassifier.Classify(new VesselFeatures { Landed = true, CarrierParts = 2 }).Class);
            Assert.Equal(UnitClass.Unknown, UnitClassifier.Classify(new VesselFeatures { OrbitalAi = true }).Class);
        }
    }
}
