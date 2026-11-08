using System;
using System.Collections.Generic;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;

namespace Skizzy.Protocol.Catalog
{
    /// <summary>Where a capability runs (02 §2.1).</summary>
    public enum Affinity { Main, Fixed, Snapshot, Any }

    /// <summary>
    /// Static description of a capability (spec 0.2 §7.2). Defined once in <see cref="Builtins"/> and shared by
    /// the host (validation, permissions, dispatch), the MCP bridge (static tool list that works with the game
    /// closed), the in-game console (generated forms) and spec/schemas.
    /// </summary>
    public sealed class CapabilityDef
    {
        public string Name;
        public int Version = 1;
        public string Provider;
        public string Title;
        public string Description;
        public Safety Safety;
        public string[] Scenes = { "ANY" };
        public string Area;
        public string Mod = "Skizzy";
        public Affinity Affinity = Affinity.Main;
        public bool LongRunning;
        public bool Idempotent;
        public string[] Requires = new string[0];
        public JsonValue InputSchema;
        public JsonValue OutputSchema;
        public JsonValue Examples;
        /// <summary>Allowed while the kill switch is engaged even though it is not read-level (engage, stop logging).</summary>
        public bool KillSwitchExempt;
        /// <summary>List-shaped result: the bridge applies the output caps (default limit 10, max 50; 03 §11).</summary>
        public bool ListShaped;
        /// <summary>MCP tool name override (e.g. "ksp_read" for ksp.vessel.get); null = name with '.' → '_'.</summary>
        public string McpName;
        /// <summary>Toolsets that project this capability as an MCP tool.</summary>
        public string[] Toolsets = new string[0];
        /// <summary>Arguments the bridge adds when the agent omits them (e.g. format=compact for LLM-facing reads).</summary>
        public JsonValue McpDefaults;
        public string[] Aliases = new string[0];

        public bool InScene(string scene)
        {
            if (Scenes == null || Scenes.Length == 0) return true;
            for (int i = 0; i < Scenes.Length; i++)
                if (Scenes[i] == "ANY" || string.Equals(Scenes[i], scene, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string ToolName => McpName ?? ToolNaming.ToToolName(Name);

        public JsonValue ToDescriptor()
        {
            var d = JsonValue.Obj()
                .Set("name", Name)
                .Set("version", Version)
                .Set("provider", Provider)
                .Set("title", Title ?? Name)
                .Set("description", Description)
                .Set("safety", SafetyNames.Name(Safety))
                .Set("scenes", JsonValue.ArrOf(Scenes))
                .Set("area", Area)
                .Set("mod", Mod)
                .Set("affinity", AffinityName(Affinity))
                .Set("longRunning", LongRunning)
                .Set("idempotent", Idempotent)
                .Set("requires", JsonValue.ArrOf(Requires))
                .Set("inputSchema", InputSchema ?? JsonValue.Obj().Set("type", "object"));
            if (OutputSchema != null) d.Set("outputSchema", OutputSchema);
            if (Examples != null) d.Set("examples", Examples);
            if (Aliases.Length > 0) d.Set("aliases", JsonValue.ArrOf(Aliases));
            if (KillSwitchExempt) d.Set("x-skz-killSwitchExempt", true);
            if (ListShaped) d.Set("x-skz-listShaped", true);
            return d;
        }

        public static string AffinityName(Affinity a)
        {
            switch (a)
            {
                case Affinity.Fixed: return "fixed";
                case Affinity.Snapshot: return "snapshot";
                case Affinity.Any: return "any";
                default: return "main";
            }
        }
    }

    /// <summary>MCP tool naming (spec 0.2 §14.2): '.' → '_', names over 64 chars truncated to 56 + '_' + 7-char hash.</summary>
    public static class ToolNaming
    {
        public static string ToToolName(string capability)
        {
            var s = capability.Replace('.', '_');
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' ? c : '_');
            s = sb.ToString();
            if (s.Length <= 64) return s;
            return s.Substring(0, 56) + "_" + Hash7(capability);
        }

        private static string Hash7(string s)
        {
            // FNV-1a 32-bit, hex, 7 chars: deterministic across processes and platforms.
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8").Substring(0, 7);
        }
    }

    /// <summary>A registry of capability definitions with lookup by name and alias.</summary>
    public sealed class DefRegistry
    {
        private readonly Dictionary<string, CapabilityDef> _byName = new Dictionary<string, CapabilityDef>(StringComparer.Ordinal);
        private readonly List<CapabilityDef> _all = new List<CapabilityDef>();

        public void Add(CapabilityDef d)
        {
            if (_byName.ContainsKey(d.Name)) throw new InvalidOperationException("Duplicate capability " + d.Name);
            _byName[d.Name] = d;
            _all.Add(d);
            foreach (var a in d.Aliases) _byName[a] = d;
        }

        public CapabilityDef Find(string name)
        {
            if (name == null) return null;
            int at = name.IndexOf('@');
            if (at > 0) name = name.Substring(0, at);
            return _byName.TryGetValue(name, out var d) ? d : null;
        }

        public IReadOnlyList<CapabilityDef> All => _all;
    }
}
