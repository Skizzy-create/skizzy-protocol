using System.Collections.Generic;
using System.Linq;
using Skizzy.Bda;
using Skizzy.C2;
using Xunit;

namespace Skizzy.Ksp.Tests
{
    /// <summary>The real C2 engine and C2Runtime over the binding over a fake game: the path the game takes.</summary>
    public class EngineOverBindingTests
    {
        private const string Blue = "Blue";
        private const string Red = "Red";

        private readonly FakeGame G = new FakeGame();
        private readonly C2Runtime Rt = new C2Runtime();
        private BdaBinding B;

        private C2Engine Attach()
        {
            B = new BdaBinding(G);
            G.Binding = B;
            return Rt.Attach(B);
        }

        private void Run(double seconds)
        {
            for (double t = 0; t < seconds - 1e-9; t += 0.2)
            {
                G.Time += 0.2;
                Assert.True(Rt.Tick(C2Runtime.ToolbarOwner));
            }
            Assert.Null(Rt.Fault);
        }

        private static GeoPoint Km(double eastKm, double northKm)
        {
            return new GeoPoint(northKm / 600000.0 * 1000 * (180 / System.Math.PI), eastKm / 600000.0 * 1000 * (180 / System.Math.PI), 3000);
        }

        private FakeVessel Add(string name, string side, UnitClass cls, double eastKm, double northKm)
        {
            var p = Km(eastKm, northKm);
            var v = G.Add(name, side, cls, p.Lat, p.Lon);
            return v;
        }

        [Fact]
        public void On_a_stock_bdarmory_orders_needing_hooks_are_refused_and_nothing_throws()
        {
            G.HookSetValue = HookSet.None;
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var dd = Add("Halberd", Blue, UnitClass.Ship, -20, 0);
            Add("Kestrel [CV]", Blue, UnitClass.Carrier, -30, 0);
            var bandit = Add("Bandit 1", Red, UnitClass.Fighter, 20, 0);
            G.Detections.Add(Blue + ":" + bandit.Id);
            Attach();
            Run(0.4);
            Assert.Equal(HookSet.None, Rt.Hooks);

            var engage = Rt.Issue(OrderRequest.Engage(Blue, bandit.Id, f.Id));
            Assert.False(engage.Accepted);
            Assert.Equal(RefusalCode.HookMissing, engage.Units[0].Code);
            var rtb = Rt.Issue(OrderRequest.RtbAll(Blue));
            Assert.False(rtb.Accepted);
            Assert.All(rtb.Units, u => Assert.Equal(RefusalCode.HookMissing, u.Code));
            Assert.False(Rt.Issue(OrderRequest.AllAttack(Blue)).Accepted);

            // the stock fly-to still moves ships
            var move = Rt.Issue(OrderRequest.MoveTo(Blue, Km(-20, 10), dd.Id));
            Assert.True(move.Accepted, move.Summary);
            Rt.SetSideManaged(Blue, true);
            Rt.SetSideManaged(Red, true);
            Run(10);
            Assert.Contains("FlyTo " + dd.Id, G.Calls);
            Assert.Equal(0, B.FaultCount); // FakeGame throws on any hook call
            Assert.DoesNotContain(G.Calls, c => c.StartsWith("F1") || c.StartsWith("F2") || c.StartsWith("F3") || c.StartsWith("F4") || c.StartsWith("F6"));
        }

        [Fact]
        public void Engage_reaches_the_weapon_manager_and_release_and_detach_restore_it()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var bandit = Add("Bandit 1", Red, UnitClass.Fighter, 30, 0);
            G.Detections.Add(Blue + ":" + bandit.Id);
            var e = Attach();
            Run(0.4);
            var res = Rt.Issue(OrderRequest.Engage(Blue, bandit.Id, f.Id));
            Assert.True(res.Accepted, res.Summary);
            Run(0.6);
            Assert.Equal(bandit.Id, f.Wm.Override);
            Assert.Equal(bandit.Id, e.GetUnitView(f.Id).OverrideTarget);

            Assert.True(Rt.Release(f.Id, "test"));
            Run(0.6);
            Assert.Equal(0u, f.Wm.Override);
            Assert.Equal(FireAuthorityMode.Free, f.Wm.Fire);
            Assert.Null(f.Wm.Filter);

            // move again, then the scene ends: everything goes back
            Assert.True(Rt.Issue(OrderRequest.MoveTo(Blue, Km(60, 0), f.Id)).Accepted);
            Run(1);
            Assert.NotNull(f.Ai.Route);
            Rt.Detach(B);
            Assert.Null(f.Ai.Route);
            Assert.Equal(FireAuthorityMode.Free, f.Wm.Fire);
            Assert.True(f.Wm.EngageMissiles);
            Assert.Equal(new[] { true, false }, f.Wm.WeaponEngageMissile);
            Assert.False(Rt.Ready);
        }

        [Fact]
        public void An_unresolvable_target_comes_back_to_the_engine_as_not_engageable()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var bandit = Add("Bandit 1", Red, UnitClass.Fighter, 30, 0);
            G.Detections.Add(Blue + ":" + bandit.Id);
            var e = Attach();
            Run(0.4);
            Assert.True(Rt.Issue(OrderRequest.Engage(Blue, bandit.Id, f.Id)).Accepted);
            Run(0.6);
            Assert.Equal(bandit.Id, f.Wm.Override);

            // the target drops out of physics range and the weapon manager is replaced: re-applying fails
            bandit.Loaded = false;
            f.Wm = new FakeWm();
            Run(1);
            Assert.Equal(0u, f.Wm.Override);
            Assert.NotEqual(bandit.Id, e.GetUnitView(f.Id).OverrideTarget);
            Assert.Equal(0, B.FaultCount);
        }

        [Fact]
        public void A_team_change_ends_the_orders_and_the_engine_takes_its_settings_off()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var bandit = Add("Bandit 1", Red, UnitClass.Fighter, 30, 0);
            G.Detections.Add(Blue + ":" + bandit.Id);
            var e = Attach();
            Run(0.4);
            Assert.True(Rt.Issue(OrderRequest.Engage(Blue, bandit.Id, f.Id)).Accepted);
            Run(0.6);
            Assert.Equal(bandit.Id, f.Wm.Override);

            f.Side = Red; // MissileFire.SetTeam
            Run(1);
            Assert.Equal(0u, f.Wm.Override);
            Assert.Equal(Red, e.GetUnitView(f.Id).Side);
            Assert.Equal("guard", e.GetUnitView(f.Id).Task);
            Assert.Equal(FireAuthorityMode.Free, f.Wm.Fire);
        }

        [Fact]
        public void Unit_list_for_the_toolbar_comes_through_the_runtime()
        {
            Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            Add("Bandit 1", Red, UnitClass.Fighter, 30, 0);
            Attach();
            Run(0.4);
            var list = new List<UnitBrief>();
            Rt.ListUnits(list, Blue);
            Assert.Equal(2, list.Count);
            Assert.All(list, u => Assert.Equal("Cobra", u.Group));
            Assert.True(Rt.Ready);
            Assert.Equal("FakeBDA", Rt.Binding.Description);
        }
    }
}
