using System;
using System.Collections.Generic;
using Skizzy.Protocol.Config;
using Skizzy.Protocol.Util;

namespace Skizzy.Protocol.Permissions
{
    /// <summary>
    /// The user's permission file (<c>PluginData/permissions.cfg</c>, KSP ConfigNode syntax; PLAN §6.4 with the
    /// changes of 15 §1–2: all areas on except <c>deep</c>, Battle Lab preset, ROLE rules with <c>maxPrecedence</c>).
    /// Nothing over the wire ever changes it (spec 0.2 §6.7, 15 §1).
    /// </summary>
    public sealed class PermissionConfig
    {
        public const int CurrentSchemaVersion = 1;
        public const string NodeName = "SKIZZY_PERMISSIONS";

        public int SchemaVersion = CurrentSchemaVersion;
        public string Preset = "BattleLab";
        public Policy DefaultPolicy = Policy.Deny;
        public string ConfirmChannel = "game";
        public int ConfirmTimeoutSec = 60;
        public readonly Dictionary<string, bool> Areas = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<Safety, Policy> LevelDefaults = new Dictionary<Safety, Policy>();
        public readonly List<ModRule> Mods = new List<ModRule>();
        public readonly List<CapRule> Caps = new List<CapRule>();
        public readonly List<RoleRule> Roles = new List<RoleRule>();
        public readonly List<ClientRule> Clients = new List<ClientRule>();
        /// <summary>God view for the local user's own agent commanders (15 §2 "the permissions window can grant").</summary>
        public bool CommanderGodView;

        public static readonly string[] KnownAreas = { "ksp.read", "ksp.module", "ksp.camera", "ksp.flight", "ksp.editor", "bdarmory", "craft", "rec", "host.debug", "deep" };

        public bool IsAreaEnabled(string area)
        {
            if (string.IsNullOrEmpty(area) || area == "core" || area == "host") return true;
            return Areas.TryGetValue(area, out bool on) ? on : area != "deep";
        }

        public RoleRule FindRole(string name)
        {
            for (int i = 0; i < Roles.Count; i++) if (string.Equals(Roles[i].Name, name, StringComparison.OrdinalIgnoreCase)) return Roles[i];
            return null;
        }

        // ---------------- presets ----------------

        /// <summary>Battle Lab (15 §1, D11): read/control allow, destructive ask, cheat allow, unsafe deny; all areas on except deep.</summary>
        public static PermissionConfig BattleLab()
        {
            var c = new PermissionConfig { Preset = "BattleLab" };
            foreach (var a in KnownAreas) c.Areas[a] = a != "deep";
            c.LevelDefaults[Safety.Read] = Policy.Allow;
            c.LevelDefaults[Safety.Control] = Policy.Allow;
            c.LevelDefaults[Safety.Destructive] = Policy.Ask;
            c.LevelDefaults[Safety.Cheat] = Policy.Allow;
            c.LevelDefaults[Safety.Unsafe] = Policy.Deny;
            c.Caps.Add(new CapRule { NamePattern = "bda.settings.infinite_*", HasPolicy = true, Policy = Policy.Ask });
            c.Caps.Add(new CapRule { NamePattern = "ksp.camera.screenshot", RateLimit = "1/2s" });
            c.Caps.Add(new CapRule { NamePattern = "ksp.camera.save", RateLimit = "1/2s" });
            AddDefaultRoles(c);
            c.Clients.Add(new ClientRule { NamePattern = "*", AllLevels = false });
            c.Clients[0].Levels.Add(Safety.Read);
            c.Clients[0].Levels.Add(Safety.Control);
            c.Clients[0].Levels.Add(Safety.Destructive);
            c.Clients[0].Levels.Add(Safety.Cheat);
            return c;
        }

        public static PermissionConfig Observer()
        {
            var c = BattleLab();
            c.Preset = "Observer";
            c.LevelDefaults[Safety.Control] = Policy.Deny;
            c.LevelDefaults[Safety.Destructive] = Policy.Deny;
            c.LevelDefaults[Safety.Cheat] = Policy.Deny;
            return c;
        }

        public static PermissionConfig TestDirector()
        {
            var c = BattleLab();
            c.Preset = "TestDirector";
            return c;
        }

        public static PermissionConfig FullCombat()
        {
            var c = BattleLab();
            c.Preset = "FullCombat";
            c.LevelDefaults[Safety.Destructive] = Policy.Allow;
            return c;
        }

        public static PermissionConfig FromPreset(string name)
        {
            switch ((name ?? "").Replace(" ", "").ToLowerInvariant())
            {
                case "observer": return Observer();
                case "testdirector": return TestDirector();
                case "fullcombat": return FullCombat();
                default: return BattleLab();
            }
        }

        public static readonly string[] PresetNames = { "BattleLab", "TestDirector", "FullCombat", "Observer" };

        private static void AddDefaultRoles(PermissionConfig c)
        {
            c.Roles.Add(Role("commander", "read, control, destructive, cheat", GodMode.PerScenario, 800, seat: true, bulk: "ask"));
            c.Roles.Add(Role("advisor", "read, control", GodMode.No, 0, channels: "none", comms: "team, direct"));
            c.Roles.Add(Role("delegate", "read, control", GodMode.No, 700, channels: "per-delegation", comms: "team, direct", scope: "cluster"));
            c.Roles.Add(Role("observer", "read", GodMode.PerSession, 0));
            c.Roles.Add(Role("script", "read, control", GodMode.PerSession, 500, channels: "steer, formation"));
            c.Roles.Add(Role("behaviour", "read, control", GodMode.No, 400, channels: "steer, weapons, targeting, sensors, ai_params, formation"));
            c.Roles.Add(Role("harness", "read, control, cheat", GodMode.Yes, 500, channels: "*"));
            c.Roles.Add(Role("human", "*", GodMode.Yes, 1000));
        }

        private static RoleRule Role(string name, string levels, GodMode god, int maxPrec, string channels = "", string comms = "", string scope = "", bool seat = false, string bulk = "")
        {
            var r = new RoleRule { Name = name, God = god, MaxPrecedence = maxPrec, Channels = channels, Comms = comms, Scope = scope, Seat = seat, BulkOps = bulk };
            ParseLevels(levels, out r.AllLevels, r.Levels);
            return r;
        }

        // ---------------- parse / serialise ----------------

        public static PermissionConfig Parse(string text, List<string> warnings = null)
        {
            var root = CfgNode.Parse(text);
            var node = root.GetNode(NodeName) ?? root;
            return FromNode(node, warnings);
        }

        public static PermissionConfig FromNode(CfgNode n, List<string> warnings = null)
        {
            // Start from the named preset so a short file only lists overrides; explicit nodes replace preset lists.
            var c = FromPreset(n.GetValue("preset", "BattleLab"));
            c.SchemaVersion = n.GetInt("schemaVersion", n.GetInt("version", 1));
            if (SafetyNames.TryParsePolicy(n.GetValue("defaultPolicy"), out var dp)) c.DefaultPolicy = dp;
            c.ConfirmChannel = n.GetValue("confirmChannel", c.ConfirmChannel).Trim();
            c.ConfirmTimeoutSec = Math.Max(5, Math.Min(600, n.GetInt("confirmTimeoutSec", c.ConfirmTimeoutSec)));
            c.CommanderGodView = n.GetBool("commanderGodView", false);

            var areas = n.GetNode("AREAS");
            if (areas != null)
                foreach (var kv in areas.Values) c.Areas[kv.Key.Trim()] = areas.GetBool(kv.Key, true);

            var defs = n.GetNode("DEFAULTS");
            if (defs != null)
                foreach (var kv in defs.Values)
                {
                    if (SafetyNames.TryParse(kv.Key, out var s) && SafetyNames.TryParsePolicy(kv.Value, out var p)) c.LevelDefaults[s] = p;
                    else warnings?.Add("DEFAULTS: ignored '" + kv.Key + " = " + kv.Value + "'");
                }
            // unsafe can never be allowed from a preset default
            if (c.LevelDefaults.TryGetValue(Safety.Unsafe, out var up) && up == Policy.Allow) c.LevelDefaults[Safety.Unsafe] = Policy.Ask;

            var mods = n.GetNodes("MOD");
            if (mods.Count > 0)
            {
                c.Mods.Clear();
                foreach (var m in mods)
                {
                    var mr = new ModRule { Id = m.GetValue("id", "").Trim() };
                    foreach (var s in SafetyNames.All)
                    {
                        var pv = m.GetValue(SafetyNames.Name(s));
                        if (pv != null && SafetyNames.TryParsePolicy(pv, out var p)) mr.Levels[s] = p;
                    }
                    foreach (var cn in m.GetNodes("CAPABILITY")) mr.Caps.Add(ParseCap(cn, warnings));
                    if (mr.Id.Length > 0) c.Mods.Add(mr);
                }
            }

            var caps = n.GetNodes("CAPABILITY");
            if (caps.Count > 0)
            {
                c.Caps.Clear();
                foreach (var cn in caps) c.Caps.Add(ParseCap(cn, warnings));
            }

            var roles = n.GetNodes("ROLE");
            if (roles.Count > 0)
            {
                foreach (var rn in roles)
                {
                    string name = rn.GetValue("name", "").Trim();
                    if (!Actors.IsRole(name)) { warnings?.Add("ROLE: unknown role '" + name + "'"); continue; }
                    var r = c.FindRole(name);
                    if (r == null) { r = new RoleRule { Name = name }; c.Roles.Add(r); }
                    var lv = rn.GetValue("levels");
                    if (lv != null) { r.Levels.Clear(); ParseLevels(lv, out r.AllLevels, r.Levels); }
                    var god = rn.GetValue("god");
                    if (god != null) r.God = ParseGod(god);
                    r.MaxPrecedence = Math.Max(0, Math.Min(1000, rn.GetInt("maxPrecedence", r.MaxPrecedence)));
                    r.Channels = rn.GetValue("channels", r.Channels);
                    r.Comms = rn.GetValue("comms", r.Comms);
                    r.Scope = rn.GetValue("scope", r.Scope);
                    r.Seat = rn.GetBool("seat", r.Seat);
                    r.BulkOps = rn.GetValue("bulkOps", r.BulkOps);
                    if (name != "human" && r.AllLevels) { r.AllLevels = false; foreach (var s in SafetyNames.All) if (s != Safety.Unsafe) r.Levels.Add(s); }
                }
            }

            var clients = n.GetNodes("CLIENT");
            if (clients.Count > 0)
            {
                c.Clients.Clear();
                foreach (var cl in clients)
                {
                    var cr = new ClientRule { NamePattern = cl.GetValue("name", "*").Trim() };
                    ParseLevels(cl.GetValue("levels", "read"), out cr.AllLevels, cr.Levels);
                    c.Clients.Add(cr);
                }
            }
            return c;
        }

        private static CapRule ParseCap(CfgNode cn, List<string> warnings)
        {
            var r = new CapRule { NamePattern = cn.GetValue("name", "").Trim(), RateLimit = cn.GetValue("rateLimit") };
            var pv = cn.GetValue("policy");
            if (pv != null)
            {
                if (SafetyNames.TryParsePolicy(pv, out var p)) { r.HasPolicy = true; r.Policy = p; }
                else warnings?.Add("CAPABILITY " + r.NamePattern + ": bad policy '" + pv + "'");
            }
            if (r.RateLimit != null && !RateLimiter.TryParseSpec(r.RateLimit, out _, out _))
            {
                warnings?.Add("CAPABILITY " + r.NamePattern + ": bad rateLimit '" + r.RateLimit + "'");
                r.RateLimit = null;
            }
            return r;
        }

        private static GodMode ParseGod(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "yes": return GodMode.Yes;
                case "per-scenario": case "perscenario": return GodMode.PerScenario;
                case "per-session": case "persession": return GodMode.PerSession;
                default: return GodMode.No;
            }
        }

        private static string GodName(GodMode g)
        {
            switch (g)
            {
                case GodMode.Yes: return "true";
                case GodMode.PerScenario: return "per-scenario";
                case GodMode.PerSession: return "per-session";
                default: return "false";
            }
        }

        internal static void ParseLevels(string text, out bool all, HashSet<Safety> into)
        {
            all = false;
            if (text == null) return;
            foreach (var part in text.Split(','))
            {
                var t = part.Trim();
                if (t == "*") { all = true; continue; }
                if (SafetyNames.TryParse(t, out var s)) into.Add(s);
            }
        }

        private static string LevelsText(bool all, HashSet<Safety> levels)
        {
            if (all) return "*";
            var parts = new List<string>();
            foreach (var s in SafetyNames.All) if (levels.Contains(s)) parts.Add(SafetyNames.Name(s));
            return string.Join(", ", parts.ToArray());
        }

        public CfgNode ToNode()
        {
            var n = new CfgNode(NodeName);
            n.AddValue("schemaVersion", SchemaVersion.ToString());
            n.AddValue("preset", Preset);
            n.AddValue("defaultPolicy", SafetyNames.PolicyName(DefaultPolicy));
            n.AddValue("confirmChannel", ConfirmChannel);
            n.AddValue("confirmTimeoutSec", ConfirmTimeoutSec.ToString());
            n.AddValue("commanderGodView", CommanderGodView ? "true" : "false");
            var areas = n.AddNode("AREAS");
            foreach (var a in KnownAreas) areas.AddValue(a, IsAreaEnabled(a) ? "true" : "false");
            foreach (var kv in Areas) if (Array.IndexOf(KnownAreas, kv.Key) < 0) areas.AddValue(kv.Key, kv.Value ? "true" : "false");
            var defs = n.AddNode("DEFAULTS");
            foreach (var s in SafetyNames.All) if (LevelDefaults.TryGetValue(s, out var p)) defs.AddValue(SafetyNames.Name(s), SafetyNames.PolicyName(p));
            foreach (var m in Mods)
            {
                var mn = n.AddNode("MOD");
                mn.AddValue("id", m.Id);
                foreach (var kv in m.Levels) mn.AddValue(SafetyNames.Name(kv.Key), SafetyNames.PolicyName(kv.Value));
                foreach (var cr in m.Caps) WriteCap(mn, cr);
            }
            foreach (var cr in Caps) WriteCap(n, cr);
            foreach (var r in Roles)
            {
                var rn = n.AddNode("ROLE");
                rn.AddValue("name", r.Name);
                rn.AddValue("levels", LevelsText(r.AllLevels, r.Levels));
                rn.AddValue("god", GodName(r.God));
                rn.AddValue("maxPrecedence", r.MaxPrecedence.ToString());
                if (!string.IsNullOrEmpty(r.Channels)) rn.AddValue("channels", r.Channels);
                if (!string.IsNullOrEmpty(r.Comms)) rn.AddValue("comms", r.Comms);
                if (!string.IsNullOrEmpty(r.Scope)) rn.AddValue("scope", r.Scope);
                if (r.Seat) rn.AddValue("seat", "true");
                if (!string.IsNullOrEmpty(r.BulkOps)) rn.AddValue("bulkOps", r.BulkOps);
            }
            foreach (var cl in Clients)
            {
                var cn = n.AddNode("CLIENT");
                cn.AddValue("name", cl.NamePattern);
                cn.AddValue("levels", LevelsText(cl.AllLevels, cl.Levels));
            }
            return n;
        }

        private static void WriteCap(CfgNode parent, CapRule cr)
        {
            var cn = parent.AddNode("CAPABILITY");
            cn.AddValue("name", cr.NamePattern);
            if (cr.HasPolicy) cn.AddValue("policy", SafetyNames.PolicyName(cr.Policy));
            if (!string.IsNullOrEmpty(cr.RateLimit)) cn.AddValue("rateLimit", cr.RateLimit);
        }

        public string Serialize()
        {
            return "// skizzy-protocol permissions. Edited by the in-game Permissions window; safe to edit by hand.\n"
                 + "// Nothing over the network can change this file. Levels: read, control, destructive, cheat, unsafe.\n"
                 + ToNode().Serialize();
        }
    }
}
