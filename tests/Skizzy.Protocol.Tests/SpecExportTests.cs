using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;
using Skizzy.Protocol.Catalog;
using Skizzy.SpecExport;

namespace Skizzy.Protocol.Tests
{
    public class SpecExportTests
    {
        private static string SpecRoot()
        {
            var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("SKIZZY_REPO_ROOT") ?? AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("global.json not found above " + AppContext.BaseDirectory);
            return Path.Combine(dir.FullName, "spec", "schemas");
        }

        [Fact]
        public void GeneratedOutputMatchesCommittedFiles()
        {
            string root = SpecRoot();
            var bad = new List<string>();
            foreach (var kv in SpecExporter.Generate())
            {
                string full = Path.Combine(root, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { bad.Add(kv.Key + " (missing)"); continue; }
                if (File.ReadAllText(full).Replace("\r\n", "\n") != kv.Value) bad.Add(kv.Key);
            }
            Assert.True(bad.Count == 0, "spec/schemas is out of date: " + string.Join(", ", bad)
                + ". Run: dotnet run --project tools/Skizzy.SpecExport");
        }

        [Fact]
        public void EveryGeneratedFileIsValidJson()
        {
            foreach (var kv in SpecExporter.Generate())
            {
                using var doc = JsonDocument.Parse(kv.Value);
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            }
        }

        [Fact]
        public void CapabilityFilesHaveSchemaAndObjectInput()
        {
            string dir = Path.Combine(SpecRoot(), "capabilities");
            var files = Directory.GetFiles(dir, "*.json");
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                Assert.True(r.TryGetProperty("$schema", out _), f + " lacks $schema");
                Assert.True(r.TryGetProperty("input", out var input), f + " lacks input");
                Assert.Equal("object", input.GetProperty("type").GetString());
            }
        }

        [Fact]
        public void CapabilityFilesMatchRegistryWithNoStaleFiles()
        {
            string dir = Path.Combine(SpecRoot(), "capabilities");
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in Builtins.Registry.All) expected.Add(d.Name + ".json");
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(dir, "*.json")) actual.Add(Path.GetFileName(f));
            var missing = new List<string>(expected); missing.RemoveAll(actual.Contains);
            var stale = new List<string>(actual); stale.RemoveAll(expected.Contains);
            Assert.True(missing.Count == 0, "Missing capability files: " + string.Join(", ", missing)
                + ". Run: dotnet run --project tools/Skizzy.SpecExport");
            Assert.True(stale.Count == 0, "Stale capability files: " + string.Join(", ", stale)
                + ". Run: dotnet run --project tools/Skizzy.SpecExport");
        }
    }
}
