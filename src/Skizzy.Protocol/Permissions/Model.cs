using System;
using System.Collections.Generic;

namespace Skizzy.Protocol.Permissions
{
    /// <summary>Safety levels (spec 0.2 §8.1). Independent grants, not a hierarchy.</summary>
    public enum Safety { Read, Control, Destructive, Cheat, Unsafe }

    public enum Policy { Allow, Ask, Deny }

    /// <summary>ROLE "god" flag (15 §2): may this role see through fog of war.</summary>
    public enum GodMode { No, Yes, PerScenario, PerSession }

    public static class SafetyNames
    {
        public static string Name(Safety s)
        {
            switch (s)
            {
                case Safety.Read: return "read";
                case Safety.Control: return "control";
                case Safety.Destructive: return "destructive";
                case Safety.Cheat: return "cheat";
                default: return "unsafe";
            }
        }

        public static bool TryParse(string s, out Safety v)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "read": v = Safety.Read; return true;
                case "control": v = Safety.Control; return true;
                case "destructive": v = Safety.Destructive; return true;
                case "cheat": v = Safety.Cheat; return true;
                case "unsafe": v = Safety.Unsafe; return true;
            }
            v = Safety.Read;
            return false;
        }

        public static string PolicyName(Policy p) { return p == Policy.Allow ? "allow" : p == Policy.Ask ? "ask" : "deny"; }

        public static bool TryParsePolicy(string s, out Policy p)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "allow": p = Policy.Allow; return true;
                case "ask": p = Policy.Ask; return true;
                case "deny": p = Policy.Deny; return true;
            }
            p = Policy.Deny;
            return false;
        }

        public static readonly Safety[] All = { Safety.Read, Safety.Control, Safety.Destructive, Safety.Cheat, Safety.Unsafe };
    }

    /// <summary>
    /// Actor and client kinds (03 §2): <c>actor.kind</c> says who decides, <c>client.kind</c> which process holds the socket.
    /// Roles follow the pass 2 command model (15 §2). Precedence bands from 00 §3.
    /// </summary>
    public static class Actors
    {
        public static readonly string[] Kinds = { "human", "llm", "script", "behaviour", "harness" };
        public static readonly string[] ClientKinds = { "map", "bridge", "sdk", "cli" };
        public static readonly string[] Roles = { "commander", "advisor", "delegate", "observer", "script", "behaviour", "harness", "human" };

        public static bool IsKind(string k) { return Array.IndexOf(Kinds, k) >= 0; }
        public static bool IsClientKind(string k) { return Array.IndexOf(ClientKinds, k) >= 0; }
        public static bool IsRole(string r) { return Array.IndexOf(Roles, r) >= 0; }

        public static string DefaultRole(string kind)
        {
            switch (kind)
            {
                case "human": return "human";
                case "llm": return "commander";
                case "script": return "script";
                case "behaviour": return "behaviour";
                case "harness": return "harness";
            }
            return null;
        }

        /// <summary>Which roles an actor kind may declare (a mis-declared role is INVALID_PARAMS, never a silent upgrade).</summary>
        public static bool IsRoleAllowed(string kind, string role)
        {
            switch (kind)
            {
                case "human": return role == "human" || role == "observer";
                case "llm": return role == "commander" || role == "advisor" || role == "delegate" || role == "observer";
                case "script": return role == "script" || role == "delegate" || role == "observer";
                case "behaviour": return role == "behaviour";
                case "harness": return role == "harness" || role == "observer";
            }
            return false;
        }

        public static int DefaultPrecedence(string role)
        {
            switch (role)
            {
                case "human": return 1000;
                case "commander": return 800;
                case "delegate": return 600;
                case "harness": return 500;
                case "script": return 400;
                case "behaviour": return 300;
                default: return 0; // advisor, observer: no leases
            }
        }
    }

    public sealed class RoleRule
    {
        public string Name;
        public bool AllLevels;
        public readonly HashSet<Safety> Levels = new HashSet<Safety>();
        public GodMode God = GodMode.No;
        public int MaxPrecedence;
        public string Channels = "";
        public string Comms = "";
        public string Scope = "";
        public bool Seat;
        public string BulkOps = "";

        public bool AllowsLevel(Safety s) { return AllLevels || Levels.Contains(s); }
    }

    public sealed class ClientRule
    {
        public string NamePattern = "*";
        public bool AllLevels;
        public readonly HashSet<Safety> Levels = new HashSet<Safety>();
        public bool AllowsLevel(Safety s) { return AllLevels || Levels.Contains(s); }
    }

    public sealed class CapRule
    {
        public string NamePattern;
        public bool HasPolicy;
        public Policy Policy;
        public string RateLimit;
    }

    public sealed class ModRule
    {
        public string Id;
        public readonly Dictionary<Safety, Policy> Levels = new Dictionary<Safety, Policy>();
        public readonly List<CapRule> Caps = new List<CapRule>();
    }

    /// <summary>The outcome of a permission evaluation (spec 0.2 §8.2, 15 §2).</summary>
    public struct Decision
    {
        public Policy Policy;
        /// <summary>The matched rule, for audit and errors, e.g. "CAPABILITY ksp.camera.*", "MOD Squad", "DEFAULTS".</summary>
        public string Source;
        /// <summary>For deny: policy | role_clamp | client_cap.</summary>
        public string DenyReason;
        public string RateLimit;
        public Decision(Policy p, string source, string denyReason = null, string rateLimit = null)
        {
            Policy = p;
            Source = source;
            DenyReason = denyReason;
            RateLimit = rateLimit;
        }
    }
}
