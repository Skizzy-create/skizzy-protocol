using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>
    /// The engine side of the KSP binding contract: restore on release (row 12 missile setting), the NotHostile loss
    /// reason, team changes, hook degradation (HOOK_MISSING, never a call for a missing hook) and the UI list API.
    /// </summary>
    public class BindingContractTests : TestKit
    {
        private static readonly string[] HookCallPrefixes = { "F1 ", "F2 ", "F3 ", "F4 ", "F6 ", "F6b " };

        private int HookCalls(HookSet missing)
        {
            int n = 0;
            foreach (var c in W.Calls)
            {
                if ((missing & HookSet.F1) != 0 && c.StartsWith("F1 ")) n++;
                if ((missing & HookSet.F2) != 0 && c.StartsWith("F2 ")) n++;
                if ((missing & HookSet.F3) != 0 && c.StartsWith("F3 ")) n++;
                if ((missing & HookSet.F4) != 0 && c.StartsWith("F4 ")) n++;
                if ((missing & HookSet.F6) != 0 && c.StartsWith("F6 ")) n++;
                if ((missing & HookSet.F6b) != 0 && c.StartsWith("F6b ")) n++;
            }
            return n;
        }

        [Fact]
        public void Row12_side_no_longer_managed_restores_the_missile_setting()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Add("Bandit 1", Red, UnitClass.Fighter, 300, 0);
            Assert.False(f.EngageMissiles);
            E.SetSideManaged(Blue, true);
            Settle(1);
            Assert.True(f.EngageMissiles); // no hostile aircraft within 75 km
            Assert.Equal(false, f.EngageMissilesBaseline);

            E.SetSideManaged(Blue, false);
            Settle(1);
            Assert.False(f.EngageMissiles);
            Assert.Null(f.EngageMissilesBaseline);
            Assert.Contains("Missiles " + f.Id + " restore", W.Calls);

            // nothing of the engine's is left, so the unit is not touched again
            int calls = W.Calls.Count(c => c.Contains(" " + f.Id + " ") || c.EndsWith(" " + f.Id));
            Settle(3);
            Assert.Equal(calls, W.Calls.Count(c => c.Contains(" " + f.Id + " ") || c.EndsWith(" " + f.Id)));
        }

        [Fact]
        public void Row12_release_of_an_ordered_unit_restores_every_setting()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Add("Bandit 1", Red, UnitClass.Bomber, 200, 0);
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(60, 0), f.Id));
            Settle(1);
            Assert.True(f.EngageMissiles);
            Assert.True(f.OnRoute);
            Assert.Equal(FireAuthorityMode.Tight, f.Fire); // stay on task during move_to

            Assert.True(E.Release(f.Id, "test"));
            Settle(1);
            Assert.False(f.EngageMissiles);
            Assert.False(f.OnRoute);
            Assert.Equal(FireAuthorityMode.Free, f.Fire);
            Assert.Null(f.Filter);
            Assert.False(f.Yield);
            Assert.Equal(0u, f.Override);
            var v = V(f);
            Assert.Equal("guard", v.Task);
            Assert.False(v.FilterInstalled);
        }

        [Fact]
        public void NotHostile_loss_backs_off_before_reassigning()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b = Add("Raider 1", Red, UnitClass.Bomber, 30, 0);
            Settle(0.4);
            Ok(OrderRequest.Engage(Blue, b.Id, f.Id));
            Settle(0.4);
            Assert.Equal(b.Id, V(f).OverrideTarget);

            int before = W.CallCount("F1 set " + f.Id + " " + b.Id);
            f.Override = 0; // BDA dropped it at a scan
            E.OnOverrideLost(f.Id, b.Id, OverrideLostReason.NotHostile);
            Settle(1);
            Assert.Equal(before, W.CallCount("F1 set " + f.Id + " " + b.Id));
            Settle(2);
            Assert.True(W.CallCount("F1 set " + f.Id + " " + b.Id) > before); // still hostile in this world: re-asserted
        }

        [Fact]
        public void Unknown_loss_reasons_from_a_newer_bda_are_handled()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b = Add("Raider 1", Red, UnitClass.Bomber, 30, 0);
            Settle(0.4);
            Ok(OrderRequest.Engage(Blue, b.Id, f.Id));
            Settle(0.4);
            E.OnOverrideLost(f.Id, b.Id, (OverrideLostReason)42);
            Settle(0.4);
            Assert.Equal(b.Id, V(f).OverrideTarget); // cleared, then re-asserted: no exception, no stuck state
        }

        [Fact]
        public void Team_change_ends_the_units_orders()
        {
            var f = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var r = Ok(OrderRequest.MoveTo(Blue, W.At(60, 0), f.Id));
            E.SetVip(f.Id, true);
            Settle(0.4);
            Assert.NotNull(V(f).Movement);

            f.S.Side = Red; // BDA team change
            Settle(0.4);
            var uo = UnitOrderOf(r, f);
            Assert.Equal(OrderStatus.Cancelled, uo.Status);
            Assert.Equal(Reasons.TeamChanged, uo.Reason);
            Assert.True(H.Logged(C2EventKind.Info, "changed team from Blue to Red"));
            Assert.False(E.Picture(Blue).IsVip(f.Id));
            Assert.Equal("guard", V(f).Task);
            Assert.Equal(Red, V(f).Side);
        }

        [Fact]
        public void Without_overdrive_hooks_orders_are_refused_and_no_hook_is_called()
        {
            W.Hooks = HookSet.None; // a stock BDArmory
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 3);
            var cv = Add("Kestrel", Blue, UnitClass.Carrier, -20, 0);
            var dd = Add("Halberd", Blue, UnitClass.Ship, -20, 5);
            var bandit = Add("Bandit 1", Red, UnitClass.Fighter, 20, 0);
            E.SetSideManaged(Blue, true);
            E.SetSideManaged(Red, true);
            Settle(0.4);

            var engage = H.Issue(OrderRequest.Engage(Blue, bandit.Id, f1.Id, f2.Id));
            Assert.False(engage.Accepted);
            Assert.All(engage.Units, u => Assert.Equal(RefusalCode.HookMissing, u.Code));
            Assert.Equal("EXECUTOR_UNAVAILABLE", Orders.ErrorKind(engage.Code));

            var rtb = H.Issue(OrderRequest.Rtb(Blue, f1.Id));
            Assert.False(rtb.Accepted);
            Assert.Equal(RefusalCode.HookMissing, rtb.Units[0].Code);

            var hunt = H.Issue(OrderRequest.AttackNearest(Blue, f1.Id, dd.Id));
            Assert.Equal(RefusalCode.HookMissing, hunt.For(f1.Id).Code);
            Assert.Equal(RefusalCode.HookMissing, hunt.For(dd.Id).Code); // ships need F3 for weapons free

            // ships' move_to uses the stock fly-to: still works
            var move = Ok(OrderRequest.MoveTo(Blue, W.At(-20, 10), dd.Id));
            Settle(20);
            Assert.Contains("FlyTo " + dd.Id, W.Calls);

            // behaviours of the managed sides never reach a missing hook, and nothing throws
            Assert.Equal(0, HookCalls(HookSet.All));
            Assert.False(H.Logged(C2EventKind.Interruption, "self-defence"));
            Assert.NotNull(V(cv));
        }

        [Fact]
        public void Partial_hooks_degrade_per_hook()
        {
            W.Hooks = HookSet.F1 | HookSet.F3 | HookSet.F4 | HookSet.F10; // no routes, no secondary targets
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var eye = Add("Eye", Blue, UnitClass.Aew, -10, 0);
            var bandit = Add("Bandit 1", Red, UnitClass.Fighter, 120, 0);
            E.SetSideManaged(Blue, true);
            W.Detect(Blue, bandit.Id);
            Settle(0.4);

            var pattern = H.Issue(OrderRequest.HoldPattern(Blue, W.At(10, 0), W.At(30, 0), f1.Id));
            Assert.Equal(RefusalCode.HookMissing, pattern.Units[0].Code);
            Assert.Contains("F6", pattern.Units[0].Reason);

            var engage = H.Issue(OrderRequest.Engage(Blue, bandit.Id, f1.Id));
            Assert.True(engage.Accepted, engage.Summary);
            Settle(2);
            Assert.True(W.CallCount("F1 set " + f1.Id) > 0);
            Assert.Equal(0, HookCalls(HookSet.F2 | HookSet.F6 | HookSet.F6b));
            Assert.NotNull(V(eye));
        }

        [Fact]
        public void Unit_and_side_lists_for_the_ui()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var dd = Add("Halberd", Blue, UnitClass.Ship, -20, 5);
            var b = Add("Bandit 1", Red, UnitClass.Fighter, 200, 0);
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(40, 0), f1.Id));
            SetUnitDoctrine(dd, "weaponsControl", "hold");
            E.SetGroup(f1.Id, "CAP North");
            Settle(0.4);

            var sides = new List<string>();
            E.ListSides(sides);
            Assert.Equal(new[] { Blue, Red }, sides);

            var units = new List<UnitBrief>();
            E.ListUnits(units, Blue);
            Assert.Equal(2, units.Count);
            var a = units.First(u => u.Id == f1.Id);
            Assert.Equal("move_to", a.Task);
            Assert.True(a.Order > 0);
            Assert.Equal("CAP North", a.C2Group);
            Assert.True(a.Alive && a.Present);
            var s = units.First(u => u.Id == dd.Id);
            Assert.Equal(WeaponsControl.Hold, s.WeaponsControl);
            Assert.Equal(FireAuthorityMode.Hold, s.FireAuthority);

            units.Clear();
            E.ListUnits(units);
            Assert.Equal(3, units.Count);
            Assert.Contains(units, u => u.Id == b.Id && u.Side == Red);

            SetSideDoctrine(Blue, "advanceOnContact", false);
            Assert.False(E.SideDoctrine(Blue).AdvanceOnContact);
            Assert.True(E.SideDoctrine(Red).AdvanceOnContact);
        }
    }
}
