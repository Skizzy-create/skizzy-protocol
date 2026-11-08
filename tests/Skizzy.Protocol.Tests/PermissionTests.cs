using System.Collections.Generic;
using Skizzy.Protocol.Config;
using Skizzy.Protocol.Permissions;
using Skizzy.Protocol.Util;
using Xunit;

namespace Skizzy.Protocol.Tests
{
    public class PermissionTests
    {
        [Fact]
        public void Cfg_parses_ksp_syntax_with_comments_and_one_line_blocks()
        {
            var root = CfgNode.Parse(@"
SKIZZY_PERMISSIONS
{
    preset = BattleLab   // trailing comment
    AREAS
    {
        deep = false
        ksp.camera = true
    }
    ROLE { name = advisor
        levels = read }
    MOD
    {
        id = BDArmory
        CAPABILITY
        {
            name = bda.fire_*
            policy = ask
        }
    }
}");
            var n = root.GetNode("SKIZZY_PERMISSIONS");
            Assert.NotNull(n);
            Assert.Equal("BattleLab", n.GetValue("preset"));
            Assert.False(n.GetNode("AREAS").GetBool("deep", true));
            Assert.Equal("advisor", n.GetNode("ROLE").GetValue("name"));
            Assert.Equal("read", n.GetNode("ROLE").GetValue("levels"));
            Assert.Equal("ask", n.GetNode("MOD").GetNode("CAPABILITY").GetValue("policy"));
        }

        [Fact]
        public void BattleLab_round_trips_through_the_file_format()
        {
            var a = PermissionConfig.BattleLab();
            var text = a.Serialize();
            var warnings = new List<string>();
            var b = PermissionConfig.Parse(text, warnings);
            Assert.Empty(warnings);
            Assert.Equal(a.Preset, b.Preset);
            Assert.Equal(a.Roles.Count, b.Roles.Count);
            Assert.Equal(800, b.FindRole("commander").MaxPrecedence);
            Assert.Equal(GodMode.PerScenario, b.FindRole("commander").God);
            Assert.False(b.IsAreaEnabled("deep"));
            Assert.True(b.IsAreaEnabled("ksp.camera"));
            Assert.Equal(b.Serialize(), text);
        }

        [Fact]
        public void BattleLab_levels_match_15_section_1()
        {
            var e = new PermissionEngine(PermissionConfig.BattleLab());
            Assert.Equal(Policy.Allow, e.Evaluate("ksp.vessels.list", "Squad", Safety.Read, "commander", "skizzy-bridge", null).Policy);
            Assert.Equal(Policy.Allow, e.Evaluate("rec.log.start", "Skizzy", Safety.Control, "commander", "skizzy-bridge", null).Policy);
            Assert.Equal(Policy.Ask, e.Evaluate("host.debug.confirm_test", "Skizzy", Safety.Destructive, "commander", "skizzy-bridge", null).Policy);
            Assert.Equal(Policy.Allow, e.Evaluate("bda.spawn", "BDArmory", Safety.Cheat, "commander", "skizzy-bridge", null).Policy);
            // unsafe: never allowed (role clamp for commander; deny by default)
            Assert.Equal(Policy.Deny, e.Evaluate("reflect.set", "Skizzy", Safety.Unsafe, "commander", "skizzy-bridge", null).Policy);
            Assert.Equal(Policy.Deny, e.Evaluate("reflect.set", "Skizzy", Safety.Unsafe, "human", "skizzy-bridge", null).Policy);
        }

        [Fact]
        public void Role_and_client_clamps()
        {
            var e = new PermissionEngine(PermissionConfig.BattleLab());
            var d = e.Evaluate("rec.log.start", "Skizzy", Safety.Control, "observer", "x", null);
            Assert.Equal(Policy.Deny, d.Policy);
            Assert.Equal("role_clamp", d.DenyReason);
            var adv = e.Evaluate("bda.spawn", "BDArmory", Safety.Cheat, "advisor", "x", null);
            Assert.Equal("role_clamp", adv.DenyReason);

            var cfg = PermissionConfig.BattleLab();
            var cl = new ClientRule { NamePattern = "notebook*" };
            cl.Levels.Add(Safety.Read);
            cfg.Clients.Add(cl);
            var e2 = new PermissionEngine(cfg);
            var cd = e2.Evaluate("rec.log.start", "Skizzy", Safety.Control, "script", "notebook-1", null);
            Assert.Equal(Policy.Deny, cd.Policy);
            Assert.Equal("client_cap", cd.DenyReason);
            Assert.Equal(Policy.Allow, e2.Evaluate("rec.log.start", "Skizzy", Safety.Control, "script", "other", null).Policy);
        }

        [Fact]
        public void Capability_rules_beat_mod_and_defaults_and_deny_wins_ties()
        {
            var cfg = PermissionConfig.BattleLab();
            cfg.Caps.Add(new CapRule { NamePattern = "ksp.camera.*", HasPolicy = true, Policy = Policy.Ask });
            cfg.Caps.Add(new CapRule { NamePattern = "ksp.camera.screenshot", HasPolicy = true, Policy = Policy.Allow });
            var m = new ModRule { Id = "Squad" };
            m.Levels[Safety.Control] = Policy.Deny;
            cfg.Mods.Add(m);
            var e = new PermissionEngine(cfg);
            Assert.Equal(Policy.Allow, e.Evaluate("ksp.camera.screenshot", "Squad", Safety.Read, "commander", "c", null).Policy);
            var sm = e.Evaluate("ksp.camera.set_mode", "Squad", Safety.Control, "commander", "c", null);
            Assert.Equal(Policy.Ask, sm.Policy);
            Assert.Equal("CAPABILITY ksp.camera.*", sm.Source);
            Assert.Equal(Policy.Deny, e.Evaluate("ksp.module.set", "Squad", Safety.Control, "commander", "c", null).Policy);

            cfg.Caps.Add(new CapRule { NamePattern = "ksp.camera.*", HasPolicy = true, Policy = Policy.Deny });
            Assert.Equal(Policy.Deny, new PermissionEngine(cfg).Evaluate("ksp.camera.set_mode", "Squad", Safety.Control, "commander", "c", null).Policy);
        }

        [Fact]
        public void Session_grants_turn_ask_into_allow_but_never_unsafe()
        {
            var e = new PermissionEngine(PermissionConfig.BattleLab());
            var grants = new HashSet<string> { "host.debug.confirm_test", "reflect.set" };
            Assert.Equal(Policy.Allow, e.Evaluate("host.debug.confirm_test", "Skizzy", Safety.Destructive, "commander", "c", grants).Policy);
            Assert.NotEqual(Policy.Allow, e.Evaluate("reflect.set", "Skizzy", Safety.Unsafe, "human", "c", grants).Policy);
        }

        [Fact]
        public void Precedence_is_clamped_and_can_only_be_lowered()
        {
            var e = new PermissionEngine(PermissionConfig.BattleLab());
            Assert.Equal(800, e.EffectivePrecedence("commander", null));
            Assert.Equal(800, e.EffectivePrecedence("commander", 5000));
            Assert.Equal(300, e.EffectivePrecedence("commander", 300));
            Assert.Equal(400, e.EffectivePrecedence("script", null));
            Assert.Equal(0, e.EffectivePrecedence("advisor", 900));
            Assert.Equal(1000, e.EffectivePrecedence("human", null));
            var cfg = PermissionConfig.BattleLab();
            cfg.FindRole("commander").MaxPrecedence = 650;
            Assert.Equal(650, new PermissionEngine(cfg).EffectivePrecedence("commander", null));
        }

        [Fact]
        public void God_view_flags()
        {
            var e = new PermissionEngine(PermissionConfig.BattleLab());
            Assert.False(e.MayUseGodView("advisor"));
            Assert.False(e.MayUseGodView("delegate"));
            Assert.False(e.MayUseGodView("commander"));
            Assert.True(e.MayUseGodView("human"));
            Assert.True(e.MayUseGodView("harness"));
            Assert.True(e.MayUseGodView("script"));
            var cfg = PermissionConfig.BattleLab();
            cfg.CommanderGodView = true;
            Assert.True(new PermissionEngine(cfg).MayUseGodView("commander"));
        }

        [Fact]
        public void Actor_roles_by_kind()
        {
            Assert.True(Actors.IsRoleAllowed("llm", "advisor"));
            Assert.False(Actors.IsRoleAllowed("llm", "human"));
            Assert.False(Actors.IsRoleAllowed("script", "commander"));
            Assert.Equal("commander", Actors.DefaultRole("llm"));
        }

        [Fact]
        public void Glob_and_rate_limits()
        {
            Assert.True(Glob.IsMatch("DDG-1 Kirov", "ddg*"));
            Assert.True(Glob.IsMatch("bda.fire_missile", "bda.fire_*"));
            Assert.False(Glob.IsMatch("bda.spawn", "bda.fire_*"));
            Assert.True(Glob.Specificity("ksp.camera.screenshot") > Glob.Specificity("ksp.camera.*"));
            Assert.True(RateLimiter.TryParseSpec("1/2s", out int c, out double p) && c == 1 && p == 2000);
            Assert.True(RateLimiter.TryParseSpec("600/min", out c, out p) && c == 600 && p == 60000);
            Assert.False(RateLimiter.TryParseSpec("x/min", out _, out _));
            var rl = new RateLimiter(1, 2000, "1/2s");
            Assert.Equal(0, rl.TryTake(1000));
            long wait = rl.TryTake(1500);
            Assert.InRange(wait, 1400, 1500);
            Assert.Equal(0, rl.TryTake(3001));
        }

        [Fact]
        public void Levenshtein_suggestions()
        {
            Assert.Equal("ksp.vessels.list", Levenshtein.Closest("ksp.vessel.list", new[] { "ksp.vessels.list", "ksp.query", "host.stats" }));
            Assert.Null(Levenshtein.Closest("zzzzzzzzzz", new[] { "ksp.query" }));
        }
    }
}
