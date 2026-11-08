using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Row 3: move_to and move_route feed BDA waypoints (F6); arrival leaves a hold; ships use fly-to.</summary>
    public class MovementTests : TestKit
    {
        [Fact]
        public void Move_to_feeds_a_one_point_route_and_holds_on_arrival()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var res = Ok(OrderRequest.MoveTo(Blue, W.At(40, 0), f1.Id));
            Settle(0.2);
            Assert.True(f1.OnRoute);
            Assert.Single(f1.Waypoints);
            Assert.True(f1.Yield); // waypoints yield to combat unless Hold
            Assert.Equal("transit", res.Order.Units[0].Phase);

            Assert.True(H.RunUntil(() => res.Order.IsTerminal, 400), H.Log());
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Assert.Equal(Reasons.Arrived, res.Order.Units[0].Reason);
            Assert.True(H.Logged(C2EventKind.OrderAchieved, "on station, orbiting the ordered point"));
            Settle(0.4);
            var v = V(f1);
            Assert.Equal("point", v.Hold);
            Assert.Equal("hold", v.Task);
            Assert.Equal(8, f1.Waypoints.Count); // orbit ring
            Assert.Equal(1000, f1.Laps);
            Assert.True(W.Geo.Distance(f1.S.Pos, W.At(40, 0)) < 6000);
        }

        [Fact]
        public void Group_move_spreads_destinations_in_formation()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 1, 0);
            var f3 = Add("Cobra 3", Blue, UnitClass.Fighter, 2, 0);
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(50, 0), f1.Id, f2.Id, f3.Id));
            Settle(0.2);
            var d12 = W.Geo.Distance(f1.Waypoints[0], f2.Waypoints[0]);
            var d23 = W.Geo.Distance(f2.Waypoints[0], f3.Waypoints[0]);
            Assert.InRange(d12, 1500, 4000);
            Assert.InRange(d23, 1500, 4000);
        }

        [Fact]
        public void Move_route_flies_the_points_reports_progress_and_completes()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var pts = new[] { W.At(20, 0), W.At(20, 20), W.At(0, 20) };
            var res = Ok(OrderRequest.MoveRoute(Blue, pts, f1.Id));
            Settle(0.2);
            Assert.Equal(3, f1.Waypoints.Count);
            Assert.True(H.RunUntil(() => res.Order.IsTerminal, 600), H.Log());
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Assert.True(H.Logged(C2EventKind.OrderProgress, "waypoint 1 of 3"));
            Assert.True(H.Logged(C2EventKind.OrderProgress, "waypoint 2 of 3"));
        }

        [Fact]
        public void Route_resumes_at_the_next_waypoint_after_an_engagement()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var bandit = Add("Bandit", Red, UnitClass.Bomber, 30, 30);
            bandit.RadarRangeM = 0;
            Settle(0.2);
            var pts = new[] { W.At(20, 0), W.At(20, 20), W.At(40, 20), W.At(40, 0) };
            var route = Ok(OrderRequest.MoveRoute(Blue, pts, f1.Id));
            Assert.True(H.RunUntil(() => route.Order.Units[0].NextIndex >= 2, 400), H.Log());

            var attack = Ok(OrderRequest.Engage(Blue, bandit.Id, f1.Id));
            Settle(0.4);
            Assert.Equal(OrderStatus.Suspended, route.Order.Units[0].Status);
            Assert.True(H.Logged(C2EventKind.OrderSuspended, "resumes when the attack ends"));
            Assert.False(f1.OnRoute);
            Assert.Equal(bandit.Id, f1.Override);

            W.Kill(bandit.Id);
            Settle(0.4);
            Assert.Equal(OrderStatus.Achieved, attack.Order.Status);
            Assert.Equal(OrderStatus.Executing, route.Order.Units[0].Status);
            Assert.True(H.Logged(C2EventKind.OrderResumed, "resuming move_route"));
            // re-fed from the next unreached point, not from the start
            Assert.Equal(2, f1.Waypoints.Count);
            Assert.Equal(pts[2].Lat, f1.Waypoints[0].Lat, 6);
        }

        [Fact]
        public void Ships_move_with_fly_to_and_hold_position()
        {
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, 0);
            Settle(0.2);
            var res = Ok(OrderRequest.MoveTo(Blue, W.At(3, 0), ddg.Id));
            Settle(0.2);
            Assert.True(ddg.FlyToActive);
            Assert.False(ddg.OnRoute);
            Assert.True(H.RunUntil(() => res.Order.IsTerminal, 600), H.Log());
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Settle(0.4);
            Assert.Equal("point", V(ddg).Hold);
            Assert.True(ddg.FlyToActive);
        }

        [Fact]
        public void Ai_inactive_fails_the_order_after_retries()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var res = Ok(OrderRequest.MoveTo(Blue, W.At(40, 0), f1.Id));
            f1.S.AiActive = false; // AI switched off after the order was accepted
            Settle(5);
            Assert.Equal(OrderStatus.Failed, res.Order.Status);
            Assert.Equal(Reasons.AiInactive, res.Order.Units[0].Reason);
            Assert.Equal(3, W.CallCount("F6 set"));
        }

        [Fact]
        public void A_new_movement_order_supersedes_the_old_one_and_clears_the_hold()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var a = Ok(OrderRequest.MoveTo(Blue, W.At(2, 0), f1.Id));
            Settle(1);
            Assert.Equal(OrderStatus.Achieved, a.Order.Status);
            Assert.Equal("point", V(f1).Hold);
            var b = Ok(OrderRequest.MoveTo(Blue, W.At(80, 0), f1.Id));
            Settle(0.4);
            Assert.Null(V(f1).Hold);
            Assert.Single(f1.Waypoints);
            Assert.Equal(OrderStatus.Executing, b.Order.Status);
        }
    }
}
