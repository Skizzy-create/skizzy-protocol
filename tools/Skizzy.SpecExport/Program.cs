using System;
using System.IO;

namespace Skizzy.SpecExport
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            bool check = Array.IndexOf(args, "--check") >= 0;
            string root = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory());
            if (root == null) { Console.Error.WriteLine("Cannot find the repo root (global.json)."); return 2; }
            string specRoot = Path.Combine(root, "spec", "schemas");
            if (check)
            {
                var diffs = SpecExporter.Diff(specRoot);
                if (diffs.Count == 0) { Console.WriteLine("spec/schemas is up to date."); return 0; }
                Console.Error.WriteLine("spec/schemas differs from the catalog:");
                foreach (var d in diffs) Console.Error.WriteLine("  " + d);
                Console.Error.WriteLine("Run: dotnet run --project tools/Skizzy.SpecExport");
                return 1;
            }
            SpecExporter.Write(specRoot);
            Console.WriteLine("Wrote " + SpecExporter.Generate().Count + " files to " + specRoot);
            return 0;
        }

        private static string FindRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
