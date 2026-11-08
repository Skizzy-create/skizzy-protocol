using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Rows 9 and 21 (racetrack patrol that fights) and row 10 (RTB, RTB all, refusals).</summary>
    public class PatrolRtbTests : TestKit
    {
        [Fact]
        public void Row9_racetrack_becomes_looping_waypoints_with_patrol_engage_and_altitude_stack()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 1, 0);
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, -5);
            Settle(0.2);
            var res = H.Issue(OrderRequest.HoldPattern(Blue, W.At(20, 0), W.At(40, 0), f1.Id, f2.Id, ddg.Id));
            Assert.True(res.Accepted);
            Assert.Equal(RefusalCode.NotApplicable, res.For(ddg.Id).Code);
            Assert.Contains("ships cannot fly a holding pattern", res.For(ddg.Id).Reason);
            Assert.Contains("racetrack holding pattern, 20 km legs, 6 km wide", res.Summary);
            Settle(0.2);
            Assert.Equal(44, f1.Waypoints.Count);
            Assert.Equal(1000, f1.Laps);
            Assert.True(f1.Yield);
            Assert.Equal(55000f, f1.Leash);
            Assert.Equal(4500, UnitOrderOf(res, f1).Alt);
            Assert.Equal(4800, UnitOrderOf(res, f2).Alt);
            Assert.Equal(FireAuthorityMode.Free, f1.Fire); // a patrol fights (no stay-on-task filter)
            Assert.True(H.RunUntil(() => UnitOrderOf(res, f1).Phase == "on_station", 300), H.Log());
            Assert.True(H.Logged(C2EventKind.OrderProgress, "on the racetrack at 4500 m"));
        }

        [Fact]
        public void Row9_patrol_leaves_to_fight_and_resumes_without_refeeding()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 20, 0);
            f1.S.MissilesReady = 0; f1.S.HasGuns = false; // can't kill; the test kills
            Settle(0.2);
            var res = Ok(OrderRequest.HoldPattern(Blue, W.At(20, 0), W.At(40, 0), f1.Id));
            H.Run(5);
            int feeds = W.CallCount("F6 set");
            var bandit = Add("Bandit", Red, UnitClass.Bomber, 30, 20);
            bandit.RadarRangeM = 0;
            Assert.True(H.RunUntil(() => f1.PatrolEngaged, 20), H.Log());
            Settle(0.4);
            Assert.True(H.Logged(C2EventKind.Diversion, "leaving the patrol to engage Bandit"));
            Assert.Equal("engaging", UnitOrderOf(res, f1).Phase);
            W.Kill(bandit.Id);
            Settle(0.6);
            Assert.True(H.Logged(C2EventKind.BackOnTask, "resuming the patrol"));
            Assert.Equal(feeds, W.CallCount("F6 set")); // BDA resumes at the same waypoint; no re-feed
            Assert.Equal(OrderStatus.Executing, res.Order.Status);
        }

        [Fact]
        public void Row9_leash_breaks_off_and_the_target_is_not_reassigned()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 20, 0);
            f1.S.MissilesReady = 0; f1.S.HasGuns = false;
            f1.GuardRangeM = 200000;
            SetUnitDoctrine(f1, "patrolLeashKm", 20);
            Settle(0.2);
            var res = Ok(OrderRequest.HoldPattern(Blue, W.At(20, 0), W.At(40, 0), f1.Id));
            H.Run(2);
            var runner = Add("Runner", Red, UnitClass.Bomber, 30, 30);
            runner.Scripted = true;
            runner.S.HeadingDeg = 0;
            runner.S.SpeedMps = 400;
            W.Detect(Blue, runner.Id);
            Assert.True(H.RunUntil(() => H.Logged(C2EventKind.BackOnTask, "leash reached"), 300), H.Log());
            Assert.Equal(OrderStatus.Executing, res.Order.Status);
            Settle(5);
            Assert.False(f1.PatrolEngaged); // 30 s cooldown on that target
        }

        [Fact]
        public void Row9_weapons_hold_stays_on_the_pattern()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 20, 0);
            SetUnitDoctrine(f1, "weaponsControl", "hold");
            Settle(0.2);
            Ok(OrderRequest.HoldPattern(Blue, W.At(20, 0), W.At(40, 0), f1.Id));
            var bandit = Add("Bandit", Red, UnitClass.Bomber, 25, 5);
            H.Run(10);
            Assert.False(f1.Yield);
            Assert.False(f1.PatrolEngaged);
            Assert.Equal(FireAuthorityMode.Hold, f1.Fire);
        }

        [Fact]
        public void Row21_the_scenario_director_gives_red_caps_the_same_patrol()
        {
            var r1 = Add("Red CAP 1", Red, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var req = OrderRequest.HoldPattern(Red, W.At(0, 10), W.At(0, 40), r1.Id);
            req.Role = IssuerRole.Director;
            req.Issuer = "director";
            Ok(req);
            Settle(0.2);
            Assert.Equal(44, r1.Waypoints.Count);
            Assert.True(r1.Yield);
        }

        [Fact]
        public void Row10_rtb_flies_to_the_marshal_point_and_stacks()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, 0, 0);
            cvn.S.HeadingDeg = 90;
            cvn.CruiseMps = 0;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 40, 0, 4000);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 40, 3, 4000);
            f1.S.HomeBase = f2.S.HomeBase = cvn.Id;
            Settle(0.2);
            var res = Ok(OrderRequest.Rtb(Blue, f1.Id, f2.Id));
            Assert.Contains("RTB to Kestrel", res.Summary);
            Assert.Contains("marshal stack 8 km astern; landing waits for the recover adapter", res.Summary);
            Settle(0.2);
            var marshal = W.Geo.Destination(cvn.S.Pos, 270, 8000);
            Assert.True(W.Geo.Distance(f1.Waypoints[0], marshal) < 10);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire); // rtbWeapons: Tight

            Assert.True(H.RunUntil(() => res.Order.IsTerminal, 600), H.Log());
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Assert.All(res.Order.Units, u => Assert.Equal(Reasons.OnStation, u.Reason));
            Assert.True(H.Logged(C2EventKind.OrderAchieved, "Landing waits for the recover adapter"));
            Settle(0.4);
            var v1 = V(f1);
            var v2 = V(f2);
            Assert.Equal("marshal", v1.Hold);
            Assert.Equal(8, f1.Waypoints.Count);
            var alts = new[] { v1.HoldAltM, v2.HoldAltM }.OrderBy(a => a).ToArray();
            Assert.Equal(new[] { 1200.0, 1500.0 }, alts);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire);
        }

        [Fact]
        public void Row10_base_resolution_and_refusals()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, 0, 0);
            var field = Add("Island Field", Blue, UnitClass.Airfield, 100, 0);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 90, 0);        // no home: nearest = field
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 90, 2);
            f2.S.HomeBase = cvn.Id;                                          // home = carrier
            var f3 = Add("Cobra 3", Blue, UnitClass.Fighter, 90, 4);         // explicit base
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 5, 5);
            Settle(0.2);

            var a = Ok(OrderRequest.Rtb(Blue, f1.Id, f2.Id, cvn.Id));
            Assert.Equal(field.Id, UnitOrderOf(a, f1).Base);
            Assert.Equal(cvn.Id, UnitOrderOf(a, f2).Base);
            Assert.Equal(RefusalCode.NotApplicable, a.For(cvn.Id).Code);
            Assert.Contains("is a carrier (a base)", a.For(cvn.Id).Reason);

            var req = OrderRequest.Rtb(Blue, f3.Id);
            req.Base = cvn.Id;
            var b = Ok(req);
            Assert.Equal(cvn.Id, UnitOrderOf(b, f3).Base);

            // no base left: refusal with a reason
            W.Kill(cvn.Id);
            W.Kill(field.Id);
            Settle(0.2);
            var f4 = Add("Cobra 4", Blue, UnitClass.Fighter, 10, 10);
            Settle(0.2);
            var c = H.Issue(OrderRequest.Rtb(Blue, f4.Id));
            Assert.False(c.Accepted);
            Assert.Equal(RefusalCode.NoBase, c.Code);
            Assert.Equal("PRECONDITION_FAILED", Orders.ErrorKind(c.Code));
        }

        [Fact]
        public void Row10_rtb_all_takes_every_aircraft_and_needs_the_seat()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, 0, 0);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 30, 0);
            var d1 = Add("Wasp 1", Blue, UnitClass.Drone, 30, 5);
            var aew = Add("Eye", Blue, UnitClass.Aew, 20, -5);
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 5, 0);
            var red = Add("Bandit", Red, UnitClass.Fighter, 200, 0);
            Settle(0.2);
            var res = Ok(OrderRequest.RtbAll(Blue));
            var ids = res.Order.Units.Select(u => u.Unit).ToList();
            Assert.Contains(f1.Id, ids);
            Assert.Contains(d1.Id, ids);
            Assert.Contains(aew.Id, ids);
            Assert.DoesNotContain(ddg.Id, ids);
            Assert.DoesNotContain(red.Id, ids);
            Assert.DoesNotContain(cvn.Id, ids);
        }

        [Fact]
        public void Row10_base_lost_in_transit_diverts_or_fails()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, 0, 0);
            var cvn2 = Add("Osprey", Blue, UnitClass.Carrier, 0, 60);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 80, 0);
            f1.S.HomeBase = cvn.Id;
            Settle(0.2);
            var res = Ok(OrderRequest.Rtb(Blue, f1.Id));
            Settle(1);
            W.Kill(cvn.Id);
            Settle(0.4);
            Assert.True(H.Logged(C2EventKind.OrderProgress, "Kestrel is lost; diverting to Osprey"));
            Assert.Equal(cvn2.Id, UnitOrderOf(res, f1).Base);
            W.Kill(cvn2.Id);
            Settle(0.4);
            Assert.Equal(OrderStatus.Failed, res.Order.Status);
            Assert.Equal(Reasons.BaseLost, res.Order.Units[0].Reason);
        }

        [Fact]
        public void Row10_ships_return_to_their_home_station()
        {
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, 0);
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, -10, 0);
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(3, 0), ddg.Id));
            Settle(300);
            var res = Ok(OrderRequest.Rtb(Blue, ddg.Id));
            Settle(0.2);
            Assert.True(ddg.FlyToActive);
            Assert.True(H.RunUntil(() => res.Order.IsTerminal, 600), H.Log());
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Assert.True(H.Logged(C2EventKind.OrderAchieved, "Halberd back on station"));
            Settle(0.4);
            Assert.Equal("home", V(ddg).Hold);
        }
    }
}
