using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Row 17: orders are validated with typed refusals and every order ends with a report.</summary>
    public class ValidationTests : TestKit
    {
        [Fact]
        public void Every_recipient_gets_an_outcome_with_a_typed_code()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var dead = Add("Cobra 2", Blue, UnitClass.Fighter, 1, 0);
            var red = Add("Bandit 1", Red, UnitClass.Fighter, 100, 0);
            var field = Add("Kerbin Field", Blue, UnitClass.Airfield, -5, 0);
            var parked = Add("Cobra 3", Blue, UnitClass.Fighter, 2, 0);
            parked.S.AiActive = false;
            Settle(0.2);
            W.Kill(dead.Id);
            Settle(0.2);

            var req = OrderRequest.MoveTo(Blue, W.At(30, 0), f1.Id, dead.Id, red.Id, field.Id, parked.Id, 9999);
            var res = H.Issue(req);

            Assert.True(res.Accepted);
            Assert.Equal(6, res.Units.Count);
            Assert.True(res.For(f1.Id).Accepted);
            Assert.Equal(RefusalCode.UnitDestroyed, res.For(dead.Id).Code);
            Assert.Equal(RefusalCode.NotOwnSide, res.For(red.Id).Code);
            Assert.Equal(RefusalCode.NotApplicable, res.For(field.Id).Code);
            Assert.Equal(RefusalCode.AiInactive, res.For(parked.Id).Code);
            Assert.Equal(RefusalCode.UnknownUnit, res.For(9999).Code);
            // one combined refusal report plus the acceptance
            var refused = H.Of(C2EventKind.OrderRefused);
            Assert.Single(refused);
            Assert.Contains("5 units", refused[0].Text);
            Assert.Single(H.Of(C2EventKind.OrderAccepted));
        }

        [Fact]
        public void Whole_order_refusals_map_to_protocol_error_kinds()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);

            var bad = H.Issue(OrderRequest.MoveTo(Blue, default(GeoPoint), f1.Id));
            Assert.False(bad.Accepted);
            Assert.Equal(RefusalCode.InvalidParams, bad.Code);
            Assert.Equal("INVALID_PARAMS", Orders.ErrorKind(bad.Code));

            var empty = H.Issue(OrderRequest.MoveTo(Blue, W.At(10, 0)));
            Assert.Equal(RefusalCode.EmptySelection, empty.Code);

            var rtbAll = OrderRequest.RtbAll(Blue);
            rtbAll.Role = IssuerRole.Delegate;
            var denied = H.Issue(rtbAll);
            Assert.Equal(RefusalCode.NotPermitted, denied.Code);
            Assert.Equal("PERMISSION_DENIED", Orders.ErrorKind(denied.Code));
            Assert.Equal(3, H.Of(C2EventKind.OrderRefused).Count);
        }

        [Fact]
        public void Missing_hooks_are_refused_not_ignored()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            W.Hooks = HookSet.All & ~HookSet.F6;
            Settle(0.2);
            var res = H.Issue(OrderRequest.MoveTo(Blue, W.At(20, 0), f1.Id));
            Assert.False(res.Accepted);
            Assert.Equal(RefusalCode.HookMissing, res.For(f1.Id).Code);
            Assert.Equal("EXECUTOR_UNAVAILABLE", Orders.ErrorKind(res.Code));
        }

        [Fact]
        public void Engage_target_checks()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var friend = Add("Cobra 2", Blue, UnitClass.Fighter, 5, 0);
            var far = Add("Bandit far", Red, UnitClass.Fighter, 400, 0);
            var near = Add("Bandit near", Red, UnitClass.Fighter, 30, 0);
            Settle(0.2);

            Assert.Equal(RefusalCode.TargetNotHostile, H.Issue(OrderRequest.Engage(Blue, friend.Id, f1.Id)).Code);
            Assert.Equal(RefusalCode.TargetNotInView, H.Issue(OrderRequest.Engage(Blue, far.Id, f1.Id)).Code);
            Assert.Equal(RefusalCode.TargetUnknown, H.Issue(OrderRequest.Engage(Blue, 777, f1.Id)).Code);
            W.Kill(near.Id);
            Settle(0.2);
            Assert.Equal(RefusalCode.TargetDestroyed, H.Issue(OrderRequest.Engage(Blue, near.Id, f1.Id)).Code);
        }

        [Fact]
        public void Winchester_attack_is_refused_with_the_rtb_hint()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.S.MissilesReady = 0;
            f1.S.HasGuns = false;
            var bandit = Add("Bandit", Red, UnitClass.Fighter, 30, 0);
            Settle(0.2);
            var res = H.Issue(OrderRequest.Engage(Blue, bandit.Id, f1.Id));
            Assert.Equal(RefusalCode.Winchester, res.For(f1.Id).Code);
            Assert.Contains("no missiles left, attack refused", res.For(f1.Id).Reason);
            Assert.Contains("rtb", res.For(f1.Id).Reason);
        }

        [Fact]
        public void Every_order_ends_with_a_report()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 1, 0);
            var f3 = Add("Cobra 3", Blue, UnitClass.Fighter, 2, 0);
            Settle(0.2);

            var a = Ok(OrderRequest.MoveTo(Blue, W.At(100, 0), f1.Id));
            var b = Ok(OrderRequest.MoveTo(Blue, W.At(100, 0), f2.Id));
            var c = Ok(OrderRequest.MoveTo(Blue, W.At(100, 0), f3.Id));
            Settle(1);

            // superseded
            var a2 = Ok(OrderRequest.MoveTo(Blue, W.At(0, 50), f1.Id));
            // cancelled
            Assert.True(E.Cancel(b.Order.Id, "seat"));
            // failed: unit destroyed
            W.Kill(f3.Id);
            Settle(1);

            Assert.Equal(OrderStatus.Superseded, a.Order.Status);
            Assert.Equal(OrderStatus.Cancelled, b.Order.Status);
            Assert.Equal(OrderStatus.Failed, c.Order.Status);
            Assert.Equal(Reasons.UnitDestroyed, c.Order.Units[0].Reason);
            Assert.Equal(OrderStatus.Executing, a2.Order.Status);
            foreach (var o in new[] { a.Order, b.Order, c.Order })
                Assert.Contains(H.Events, e => e.Order == o.Id && e.Unit == 0 && e.Kind != C2EventKind.OrderAccepted);
            Assert.DoesNotContain(E.ActiveOrders, o => o.Id == a.Order.Id || o.Id == b.Order.Id || o.Id == c.Order.Id);
        }

        [Fact]
        public void Release_returns_the_unit_to_plain_bda()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var res = Ok(OrderRequest.MoveTo(Blue, W.At(60, 0), f1.Id));
            Settle(1);
            Assert.True(f1.OnRoute);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire); // stay on task by default
            Assert.NotNull(f1.Filter);

            E.Release(f1.Id, "seat");
            Settle(0.4);
            Assert.Equal(OrderStatus.Cancelled, res.Order.Status);
            Assert.Equal(Reasons.Released, res.Order.Units[0].Reason);
            Assert.False(f1.OnRoute);
            Assert.Equal(FireAuthorityMode.Free, f1.Fire);
            Assert.Null(f1.Filter);
            Assert.Equal(0u, f1.Override);
            Assert.Equal("guard", V(f1).Task);
        }

        [Fact]
        public void Hooks_are_written_only_on_change()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(200, 0), f1.Id));
            Settle(2);
            int before = W.Calls.Count;
            Settle(20);
            Assert.Equal(before, W.Calls.Count);
        }

        [Fact]
        public void Same_scenario_gives_the_same_hook_calls()
        {
            string Run()
            {
                var t = new ScenarioRun();
                return t.Go();
            }
            Assert.Equal(Run(), Run());
        }

        private sealed class ScenarioRun : TestKit
        {
            public string Go()
            {
                var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
                var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 2, 0);
                var b1 = Add("Bandit 1", Red, UnitClass.Fighter, 60, 5);
                var b2 = Add("Bandit 2", Red, UnitClass.Fighter, 60, -5);
                W.KillOnShot = true;
                E.SetSideManaged(Blue, true);
                Settle(0.2);
                H.Issue(OrderRequest.AttackNearest(Blue, f1.Id, f2.Id));
                Settle(120);
                return string.Join("\n", W.Calls) + "\n" + H.Log();
            }
        }
    }
}
