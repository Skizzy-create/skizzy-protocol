using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Rows 25 (all attack), 26 (attack nearest), 27 (first launch), 28 (advance on contact) and 22 (escort).</summary>
    public class AttackTests : TestKit
    {
        private FakeUnitPair Raid(double eastKm)
        {
            var b1 = Add("Bandit 1", Red, UnitClass.Fighter, eastKm, 3);
            var b2 = Add("Bandit 2", Red, UnitClass.Fighter, eastKm, -3);
            foreach (var b in new[] { b1, b2 })
            {
                b.Scripted = true;
                b.S.HeadingDeg = 270;
                b.S.SpeedMps = 200;
                b.RadarRangeM = 0;
            }
            return new FakeUnitPair { A = b1, B = b2 };
        }

        private sealed class FakeUnitPair { public Skizzy.C2.Fake.FakeUnit A, B; }

        [Fact]
        public void Row26_hunters_claim_different_targets_and_repick_after_a_kill()
        {
            var aew = Add("Eye", Blue, UnitClass.Aew, -20, 0); // identifies the raid far out
            aew.CruiseMps = 0;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 1);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, -1);
            var raid = Raid(120);
            Settle(0.2);
            var res = Ok(OrderRequest.AttackNearest(Blue, f1.Id, f2.Id));
            Assert.Contains("2 aircraft hunt the nearest hostiles", res.Summary);
            Settle(0.4);
            Assert.NotEqual(0u, f1.Override);
            Assert.NotEqual(0u, f2.Override);
            Assert.NotEqual(f1.Override, f2.Override); // claims spread the hunters
            Assert.True(f1.Sticky); // huntSticky (default)
            Assert.Equal(1, E.Picture(Blue).ClaimCount(f1.Override));
            Assert.True(H.Logged(C2EventKind.OrderProgress, "hunting Bandit"));

            W.Kill(f2.Override);
            Settle(1.2);
            Assert.Equal(f1.Override, f2.Override); // up to two hunters per target
            Assert.Equal(2, E.Picture(Blue).ClaimCount(f1.Override));
            Assert.True(H.Logged(C2EventKind.OrderProgress, "was destroyed"));
        }

        [Fact]
        public void Row26_unengaged_missile_when_no_aircraft()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, -10, 0);
            var shooter = Add("Raider", Red, UnitClass.Bomber, 0, 300);
            var msl = W.AddMissile(shooter, cvn, 30, 0);
            Settle(0.2);
            Ok(OrderRequest.AttackNearest(Blue, f1.Id));
            Settle(0.4);
            Assert.Equal(0u, f1.Override); // the carrier's guard already engages it: not unengaged
            cvn.GuardRangeM = 0;
            H.Run(1.2);
            Assert.Equal(msl.Id, f1.Override);
        }

        [Fact]
        public void Row26_winchester_hunter_is_sent_home_instead()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, -10, 0);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            f2.S.MissilesReady = 0;
            var aew = Add("Eye", Blue, UnitClass.Aew, 0, 5);
            Raid(100);
            Settle(0.2);
            var res = Ok(OrderRequest.AttackNearest(Blue, f1.Id, f2.Id, aew.Id));
            Assert.Equal(RefusalCode.Winchester, res.For(f2.Id).Code);
            Assert.Equal(OrderType.Rtb, res.For(f2.Id).SubstitutedBy);
            Assert.Equal(RefusalCode.NotApplicable, res.For(aew.Id).Code);
            Assert.Contains("not armed for this", res.For(aew.Id).Reason);
            Assert.Contains("Not armed for this: Eye", res.Summary);
            Assert.Contains(E.ActiveOrders, o => o.Type == OrderType.Rtb && o.Units.Any(u => u.Unit == f2.Id));
        }

        [Fact]
        public void Row26_winchester_during_the_hunt_ends_it_with_rtb()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, -10, 0);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var aew = Add("Eye", Blue, UnitClass.Aew, -20, 0);
            aew.CruiseMps = 0;
            Raid(150);
            Settle(0.2);
            var res = Ok(OrderRequest.AttackNearest(Blue, f1.Id));
            Settle(1);
            f1.S.MissilesReady = 0;
            Settle(0.4);
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Assert.Equal(Reasons.Winchester, res.Order.Units[0].Reason);
            Assert.True(H.Logged(C2EventKind.OrderAchieved, "winchester"));
            Assert.Equal("rtb", V(f1).Task);
        }

        [Fact]
        public void Row26_nothing_left_ends_the_hunt_and_resumes_the_movement_order()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            var pat = Ok(OrderRequest.HoldPattern(Blue, W.At(0, 0), W.At(20, 0), f1.Id));
            Settle(1);
            var hunt = Ok(OrderRequest.AttackNearest(Blue, f1.Id));
            Settle(0.4);
            Assert.Equal(OrderStatus.Suspended, pat.Order.Units[0].Status);
            Assert.True(H.RunUntil(() => hunt.Order.IsTerminal, 30));
            Assert.Equal(Reasons.NothingLeft, hunt.Order.Units[0].Reason);
            Settle(0.4);
            Assert.Equal(OrderStatus.Executing, pat.Order.Units[0].Status);
            Assert.Equal(44, f1.Waypoints.Count);
        }

        [Fact]
        public void Row25_all_attack_every_unit_engages_and_ships_extend_range()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, -10, 0);
            var ddg = Add("Halberd", Blue, UnitClass.Ship, -5, 5);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var aew = Add("Eye", Blue, UnitClass.Aew, -20, 0);
            SetSideDoctrine(Blue, "weaponsControl", "tight");
            Settle(0.2);
            var denied = OrderRequest.AllAttack(Blue);
            denied.Role = IssuerRole.Delegate;
            Assert.Equal(RefusalCode.NotPermitted, H.Issue(denied).Code);

            var res = Ok(OrderRequest.AllAttack(Blue));
            Assert.Contains("ships weapons free at full range", res.Summary);
            Assert.Equal(RefusalCode.NotApplicable, res.For(aew.Id).Code);
            Settle(0.4);
            Assert.True(ddg.RangeMax);
            Assert.True(cvn.RangeMax);
            Assert.Equal(90000, ddg.S.MissileRangeM);
            Assert.Equal(FireAuthorityMode.Free, ddg.Fire); // weapons free despite side Tight
            Assert.Equal(FireAuthorityMode.Free, f1.Fire);
            Assert.Equal("attack_nearest", V(f1).Task);

            E.Cancel(res.Order.Id, "seat");
            Settle(0.4);
            Assert.False(ddg.RangeMax); // restored
            Assert.Equal(FireAuthorityMode.Tight, ddg.Fire);
        }

        [Fact]
        public void Row25_ship_attack_keeps_its_movement_order()
        {
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, 0);
            Settle(0.2);
            var move = Ok(OrderRequest.MoveTo(Blue, W.At(5, 0), ddg.Id));
            var req = OrderRequest.AttackNearest(Blue, ddg.Id);
            req.ExtendRange = true;
            Ok(req);
            Settle(0.4);
            Assert.Equal(OrderStatus.Executing, move.Order.Units[0].Status);
            Assert.True(ddg.FlyToActive);
            Assert.True(ddg.RangeMax);
        }

        [Fact]
        public void Row27_first_launch_per_side_alerts_once_and_requests_a_pause()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bandit 1", Red, UnitClass.Fighter, 15, 0);
            f1.CruiseMps = b1.CruiseMps = 0;
            E.SetSideManaged(Blue, true);
            E.SetSideManaged(Red, true);
            Settle(0.2);
            E.OnMissileFired(b1.Id, 500, f1.Id);
            E.OnMissileFired(b1.Id, 501, f1.Id);
            E.OnMissileFired(f1.Id, 502, b1.Id);
            Settle(0.2);
            var alerts = H.Of(C2EventKind.FirstLaunch);
            Assert.Equal(2, alerts.Count);
            Assert.Contains(alerts, a => a.Side == Red && a.Text.Contains("First Red launch: Bandit 1 fired at Cobra 1"));
            Assert.Contains(alerts, a => a.Side == Blue);
            Assert.All(alerts, a => Assert.True(a.PauseRequested));
        }

        [Fact]
        public void Row28_advance_on_contact_from_the_first_identified_hostile()
        {
            var aew = Add("Eye", Blue, UnitClass.Aew, -50, 0);
            aew.CruiseMps = 0;
            aew.RadarRangeM = 0; // nothing identified yet
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var d1 = Add("Wasp 1", Blue, UnitClass.Drone, 0, 5);
            var holdF = Add("Cobra 2", Blue, UnitClass.Fighter, 0, -5);
            var ordered = Add("Cobra 3", Blue, UnitClass.Fighter, 0, -10);
            SetUnitDoctrine(holdF, "weaponsControl", "hold");
            var raid = Add("Raid", Red, UnitClass.Bomber, 150, 0);
            raid.RadarRangeM = 0;
            E.SetSideManaged(Blue, true);
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(0, -60), ordered.Id));
            Settle(1);
            Assert.False(f1.OnRoute);
            Assert.Equal(0u, V(f1).Advance);

            aew.RadarRangeM = 250000; // AEW identifies the raid at 200 km (class range 175: closer needed)
            aew.S.Pos = W.At(0, 0);
            Assert.True(H.RunUntil(() => E.Picture(Blue).AnyIdentified, 5));
            Settle(0.4);
            Assert.Equal(raid.Id, V(f1).Advance);
            Assert.Equal(raid.Id, V(d1).Advance);
            Assert.Equal(0u, V(holdF).Advance);
            Assert.Equal(0u, V(ordered).Advance);
            Assert.Equal(0u, V(aew).Advance);
            Assert.Single(f1.Waypoints);
            Assert.True(W.Geo.Distance(f1.Waypoints[0], raid.S.Pos) < 100);
            Assert.True(f1.S.GuardMode);
            Assert.Single(H.Of(C2EventKind.AdvanceStarted));

            // re-picked every 2 s; re-fed only when the hostile moved more than 2 km
            int feeds = W.CallCount("F6 set " + f1.Id + " ");
            Settle(4);
            Assert.Equal(feeds, W.CallCount("F6 set " + f1.Id + " "));
            raid.S.Pos = W.At(150, 5);
            Settle(2.2);
            Assert.Equal(feeds + 1, W.CallCount("F6 set " + f1.Id + " "));

            // any operator order replaces it
            Ok(OrderRequest.MoveTo(Blue, W.At(-30, 0), f1.Id));
            Settle(0.4);
            Assert.Equal(0u, V(f1).Advance);
        }

        [Fact]
        public void Row28_advance_off_does_nothing()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var raid = Add("Raid", Red, UnitClass.Bomber, 40, 0);
            raid.RadarRangeM = 0;
            SetSideDoctrine(Blue, "advanceOnContact", false);
            E.SetSideManaged(Blue, true);
            Settle(1);
            Assert.True(E.Picture(Blue).AnyIdentified);
            Assert.Equal(0u, V(f1).Advance);
            Assert.False(f1.OnRoute);
        }

        [Fact]
        public void Row22_escorts_screen_ahead_of_the_package_and_follow_it()
        {
            var bomber = Add("Strike 1", Blue, UnitClass.Bomber, 0, 0);
            bomber.Scripted = true;
            bomber.S.HeadingDeg = 90;
            bomber.S.SpeedMps = 200;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, -5, 2);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, -5, -2);
            Settle(0.2);
            var res = Ok(OrderRequest.Escort(Blue, bomber.Id, f1.Id, f2.Id));
            Assert.Contains("screening 14 km ahead", res.Summary);
            Settle(0.2);
            Assert.Single(f1.Waypoints);
            Assert.Equal(30000f, f1.Leash);
            Assert.Equal(FireAuthorityMode.Free, f1.Fire); // escorts run guard
            var ahead = W.Geo.Destination(bomber.S.Pos, 90, 14000);
            Assert.True(W.Geo.Distance(f1.Waypoints[0], ahead) < 2500);
            int feeds = W.CallCount("F6 set " + f1.Id + " ");
            Settle(30);
            Assert.True(W.CallCount("F6 set " + f1.Id + " ") > feeds); // slot re-fed as the leader moves
            Assert.True(H.RunUntil(() => UnitOrderOf(res, f1).Phase == "on_station", 900), H.Log());

            W.Kill(bomber.Id);
            Settle(0.4);
            Assert.Equal(OrderStatus.Failed, res.Order.Status);
            Assert.All(res.Order.Units, u => Assert.Equal(Reasons.EscorteeLost, u.Reason));
        }
    }
}
