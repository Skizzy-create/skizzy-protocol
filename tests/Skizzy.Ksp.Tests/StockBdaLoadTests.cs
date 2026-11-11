using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace Skizzy.Ksp.Tests
{
    /// <summary>
    /// Skizzy.BDArmory.dll must load on a BDArmory without the Overdrive hooks. Mods such as Scatterer call
    /// Assembly.GetTypes() on every loaded assembly, and Mono fails the whole call when one type has a field whose type
    /// does not resolve. On 2026-11-11 a compiler-generated delegate cache field typed
    /// Action&lt;MissileFire, TargetInfo, OverrideLostReason&gt; did exactly that on the PC test machine (M4 checklist B2).
    /// This test resolves every type, base type, interface and field type of the built binding against a BDArmory.dll
    /// that has no hooks. It returns early (passes) when the built binding, KSP or such a BDArmory is not on the machine.
    /// </summary>
    public class StockBdaLoadTests
    {
        [Fact]
        public void Binding_types_and_fields_resolve_against_a_bdarmory_without_hooks()
        {
            string root = RepoRoot();
            string binding = FindBinding(root);
            string ksp = KspDir(root);
            if (binding == null || ksp == null) return;
            string managed = Path.Combine(ksp, "KSP_x64_Data", "Managed");
            string stock = StockBda(ksp);
            if (!Directory.Exists(managed) || stock == null) return;

            // The binding's own dependencies (Skizzy.C2, Skizzy.Host, ...) sit in their projects' net48 output folders.
            var dirs = new List<string> { Path.GetDirectoryName(binding) };
            string cfg = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(binding)));
            foreach (var proj in Directory.GetDirectories(Path.Combine(root, "src")))
            {
                var d = Path.Combine(proj, "bin", cfg, "net48");
                if (Directory.Exists(d)) dirs.Add(d);
            }
            dirs.Add(managed);
            dirs.Add(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            using var mlc = new MetadataLoadContext(new Resolver(stock, dirs), "mscorlib");
            var asm = mlc.LoadFromAssemblyPath(binding);
            Assert.Null(asm.GetType("BDArmory.Control.OverrideLostReason", false)); // sanity: no hooks in this BDArmory
            Assert.Null(mlc.LoadFromAssemblyName("BDArmory").GetType("BDArmory.Control.OverrideLostReason", false));

            var failures = new List<string>();
            foreach (var t in asm.GetTypes())
            {
                Touch(failures, t.FullName + " (base)", () => Resolve(t.BaseType));
                Touch(failures, t.FullName + " (interfaces)", () => { foreach (var i in t.GetInterfaces()) Resolve(i); });
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    Touch(failures, t.FullName + "." + f.Name, () => Resolve(f.FieldType));
            }
            Assert.True(failures.Count == 0, "Types that would break Assembly.GetTypes() on stock BDArmory:\n" + string.Join("\n", failures));
        }

        private static void Touch(List<string> failures, string what, Action a)
        {
            try { a(); }
            catch (Exception e) { failures.Add(what + ": " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Resolve(Type t)
        {
            if (t == null) return;
            _ = t.FullName;
            _ = t.Assembly;
            if (t.HasElementType) Resolve(t.GetElementType());
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Resolve(a);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Skizzy.slnx"))) d = d.Parent;
            return d?.FullName;
        }

        private static string FindBinding(string root)
        {
            if (root == null) return null;
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var p = Path.Combine(root, "src", "Skizzy.BDArmory", "bin", cfg, "net48", "Skizzy.BDArmory.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // KSPDIR from the environment or the untracked Directory.Build.props.user (as the build does).
        private static string KspDir(string root)
        {
            var env = Environment.GetEnvironmentVariable("KSPDIR");
            if (!string.IsNullOrEmpty(env)) return env;
            if (root == null) return null;
            var user = Path.Combine(root, "Directory.Build.props.user");
            if (!File.Exists(user)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(user), @"<KSPDIR>\s*([^<]+?)\s*</KSPDIR>");
            return m.Success ? m.Groups[1].Value : null;
        }

        // SKIZZY_STOCK_BDA, else the installed BDArmory.dll if it has no hooks (released Overdrive or BDArmory Plus).
        private static string StockBda(string ksp)
        {
            var env = Environment.GetEnvironmentVariable("SKIZZY_STOCK_BDA");
            if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
            var p = Path.Combine(ksp, "GameData", "BDArmory", "Plugins", "BDArmory.dll");
            if (!File.Exists(p)) return null;
            var bytes = File.ReadAllBytes(p);
            return IndexOf(bytes, System.Text.Encoding.ASCII.GetBytes("OverrideLostReason")) < 0 ? p : null;
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private sealed class Resolver : MetadataAssemblyResolver
        {
            private readonly string _bda;
            private readonly IEnumerable<string> _dirs;
            public Resolver(string bda, IEnumerable<string> dirs) { _bda = bda; _dirs = dirs; }

            public override Assembly Resolve(MetadataLoadContext context, AssemblyName name)
            {
                if (name.Name == "BDArmory") return context.LoadFromAssemblyPath(_bda);
                foreach (var dir in _dirs)
                {
                    var p = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(p)) return context.LoadFromAssemblyPath(p);
                }
                return null;
            }
        }
    }
}
