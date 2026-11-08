using System;
using System.Collections.Generic;
using Skizzy.Protocol.Util;

namespace Skizzy.Protocol.Permissions
{
    /// <summary>
    /// Evaluates a call against the permission file (spec 0.2 §8.2; PLAN §6.2; 15 §2):
    /// session grant → CAPABILITY rule (most specific) → MOD rule → DEFAULTS → defaultPolicy,
    /// then clamped by the ROLE levels and the CLIENT cap. <c>unsafe</c> is never allowed over the wire.
    /// Pure and thread-safe for a given config instance (configs are replaced, never mutated in place).
    /// </summary>
    public sealed class PermissionEngine
    {
        private readonly PermissionConfig _cfg;

        public PermissionEngine(PermissionConfig cfg)
        {
            _cfg = cfg ?? PermissionConfig.BattleLab();
        }

        public PermissionConfig Config => _cfg;

        public Decision Evaluate(string capability, string mod, Safety safety, string role, string clientName, ICollection<string> sessionGrants)
        {
            Decision d = Base(capability, mod, safety, sessionGrants);
            if (d.Policy == Policy.Deny) return d;

            // ROLE clamp
            var rr = role != null ? _cfg.FindRole(role) : null;
            if (rr != null && !rr.AllowsLevel(safety))
                return new Decision(Policy.Deny, "ROLE " + rr.Name, "role_clamp", d.RateLimit);

            // CLIENT cap (most specific matching rule)
            ClientRule best = null;
            int bestSpec = -1;
            foreach (var cl in _cfg.Clients)
            {
                if (!Glob.IsMatch(clientName ?? "", cl.NamePattern)) continue;
                int sp = Glob.Specificity(cl.NamePattern);
                if (sp > bestSpec) { best = cl; bestSpec = sp; }
            }
            if (best != null && !best.AllowsLevel(safety))
                return new Decision(Policy.Deny, "CLIENT " + best.NamePattern, "client_cap", d.RateLimit);

            if (safety == Safety.Unsafe && d.Policy == Policy.Allow)
                d.Policy = Policy.Ask; // spec 0.2 §8.3: unsafe cannot be granted remotely
            return d;
        }

        private Decision Base(string capability, string mod, Safety safety, ICollection<string> sessionGrants)
        {
            string rate = FindRate(capability, mod);
            if (safety != Safety.Unsafe && sessionGrants != null && sessionGrants.Contains(capability))
                return new Decision(Policy.Allow, "SESSION grant", null, rate);

            // CAPABILITY rules: mod-scoped and top-level, most specific pattern wins; deny > ask > allow on ties.
            CapRule best = null;
            int bestSpec = -1;
            string bestSrc = null;
            ScanCaps(_cfg.Caps, capability, ref best, ref bestSpec, ref bestSrc, "CAPABILITY ");
            ModRule modRule = FindMod(mod);
            if (modRule != null) ScanCaps(modRule.Caps, capability, ref best, ref bestSpec, ref bestSrc, "MOD " + modRule.Id + " CAPABILITY ");
            if (best != null) return Finish(best.Policy, bestSrc, rate);

            if (modRule != null && modRule.Levels.TryGetValue(safety, out var mp)) return Finish(mp, "MOD " + modRule.Id, rate);
            if (_cfg.LevelDefaults.TryGetValue(safety, out var lp)) return Finish(lp, "DEFAULTS " + SafetyNames.Name(safety), rate);
            return Finish(_cfg.DefaultPolicy, "defaultPolicy", rate);
        }

        private static Decision Finish(Policy p, string src, string rate)
        {
            return new Decision(p, src, p == Policy.Deny ? "policy" : null, rate);
        }

        private static void ScanCaps(List<CapRule> caps, string capability, ref CapRule best, ref int bestSpec, ref string bestSrc, string prefix)
        {
            foreach (var cr in caps)
            {
                if (!cr.HasPolicy || !Glob.IsMatch(capability, cr.NamePattern)) continue;
                int sp = Glob.Specificity(cr.NamePattern);
                if (sp > bestSpec || (sp == bestSpec && Stricter(cr.Policy, best.Policy)))
                {
                    best = cr;
                    bestSpec = sp;
                    bestSrc = prefix + cr.NamePattern;
                }
            }
        }

        private static bool Stricter(Policy a, Policy b) { return (int)a > (int)b; }

        private ModRule FindMod(string mod)
        {
            if (string.IsNullOrEmpty(mod)) return null;
            foreach (var m in _cfg.Mods) if (string.Equals(m.Id, mod, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        /// <summary>The configured rate limit for a capability, most specific rule first, or null.</summary>
        public string FindRate(string capability, string mod)
        {
            string best = null;
            int bestSpec = -1;
            foreach (var cr in _cfg.Caps)
            {
                if (cr.RateLimit == null || !Glob.IsMatch(capability, cr.NamePattern)) continue;
                int sp = Glob.Specificity(cr.NamePattern);
                if (sp > bestSpec) { best = cr.RateLimit; bestSpec = sp; }
            }
            var m = FindMod(mod);
            if (m != null)
                foreach (var cr in m.Caps)
                {
                    if (cr.RateLimit == null || !Glob.IsMatch(capability, cr.NamePattern)) continue;
                    int sp = Glob.Specificity(cr.NamePattern);
                    if (sp > bestSpec) { best = cr.RateLimit; bestSpec = sp; }
                }
            return best;
        }

        /// <summary>
        /// Effective precedence (00 §3, 15 §2): min(requested or the role's default band, ROLE maxPrecedence).
        /// A client may lower its precedence, never raise it.
        /// </summary>
        public int EffectivePrecedence(string role, int? requested)
        {
            int band = Actors.DefaultPrecedence(role);
            var rr = _cfg.FindRole(role);
            int cap = rr != null ? rr.MaxPrecedence : band;
            int v = Math.Min(band, cap);
            if (requested.HasValue) v = Math.Min(v, Math.Max(0, requested.Value));
            return v;
        }

        /// <summary>Whether a role may use god view (no fog). Scenario grants arrive with M2/M6; the file flag covers the local agent.</summary>
        public bool MayUseGodView(string role)
        {
            var rr = _cfg.FindRole(role);
            if (rr == null) return role == "human";
            switch (rr.God)
            {
                case GodMode.Yes: return true;
                case GodMode.PerSession: return true;
                case GodMode.PerScenario: return _cfg.CommanderGodView;
                default: return false;
            }
        }
    }
}
