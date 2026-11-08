using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Row 13 (defend_unit and the side-level VIP flag) and row 14 (ship SAM priority for VIP threats).</summary>
    public class VipTests : TestKit
    {
        [Fact]
        public void Row13_protectors_orbit_the_vip_and_engage_only_threats_inside_the_radius()
        {
            var vip = Add("Kestrel", Blue, UnitClass.Carrier, 0, 0);
            vip.CruiseMps = 0;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 5, 0);
            f1.S.MissilesReady = 0; f1.S.HasGuns = false;
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, 3);
            var inside = Add("Raider", Red, UnitClass.Bomber, 0, 30);
            var outside = Add("Far raider", Red, UnitClass.Bomber, 0, -70);
            inside.RadarRangeM = outside.RadarRangeM = 0;
            Settle(0.2);
            var res = Ok(OrderRequest.Defend(Blue, vip.Id, f1.Id, ddg.Id));
            Assert.Contains("protect Kestrel", res.Summary);
            Assert.True(E.IsVip(vip.Id));
            Settle(0.6);
            Assert.Equal(8, f1.Waypoints.Count);
            Assert.Equal(1000, f1.Laps);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire);
            Assert.True(f1.Filter.Allows(inside.Id));
            Assert.False(f1.Filter.Allows(outside.Id));
            Assert.Equal(inside.Id, f1.Override);
            Assert.True(H.Logged(C2EventKind.OrderProgress, "a threat to Kestrel"));
            Assert.True(ddg.FlyToActive); // ship station on the VIP

            W.Kill(vip.Id);
            Settle(0.4);
            Assert.Equal(OrderStatus.Failed, res.Order.Status);
            Assert.All(res.Order.Units, u => Assert.Equal(Reasons.ProtectedLost, u.Reason));
        }

        [Fact]
        public void Row13_side_vip_flag_sends_idle_fighters_after_threats_to_the_vip()
        {
            var vip = Add("Eye", Blue, UnitClass.Aew, 0, 0);
            vip.CruiseMps = 0;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 60, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 60, 5);
            f1.S.MissilesReady = f2.S.MissilesReady = 0;
            f1.S.MissilesMax = f2.S.MissilesMax = 0; // gun fighters, not winchester
            var t1 = Add("Raider 1", Red, UnitClass.Bomber, -30, 0);
            var t2 = Add("Raider 2", Red, UnitClass.Bomber, -30, 4);
            t1.RadarRangeM = t2.RadarRangeM = 0;
            SetSideDoctrine(Blue, "advanceOnContact", false);
            E.SetSideManaged(Blue, true);
            Assert.True(E.SetVip(vip.Id, true));
            Settle(0.6);
            Assert.NotEqual(0u, V(f1).VipThreat);
            Assert.NotEqual(V(f1).VipThreat, V(f2).VipThreat); // spread by claims
            Assert.Equal(V(f1).VipThreat, f1.Override);
            Assert.True(f1.Sticky);
            Assert.True(H.Logged(C2EventKind.Diversion, "VIP priority"));
            W.Kill(t1.Id);
            W.Kill(t2.Id);
            Settle(0.6);
            Assert.Equal(0u, V(f1).VipThreat);
            Assert.Equal(0u, f1.Override);
        }

        [Fact]
        public void Row14_ships_prioritise_threats_to_the_vip_with_secondary_targets()
        {
            var vip = Add("Kestrel", Blue, UnitClass.Carrier, 0, 0);
            vip.CruiseMps = 0;
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, 10);
            ddg.CruiseMps = 0;
            var shooter = Add("Raider", Red, UnitClass.Bomber, 0, 120);
            shooter.RadarRangeM = 0;
            var near = Add("Raider close", Red, UnitClass.Bomber, 25, 0);
            var mid = Add("Raider mid", Red, UnitClass.Bomber, 35, 0);
            near.RadarRangeM = mid.RadarRangeM = 0;
            var msl = W.AddMissile(shooter, vip, 0, 50);
            E.SetSideManaged(Blue, true);
            E.SetVip(vip.Id, true);
            Settle(0.4);
            var v = V(ddg);
            Assert.True(v.Secondary.Count >= 3);
            Assert.Equal(msl.Id, v.Secondary[0]); // missile at the VIP first
            Assert.Equal(near.Id, v.Secondary[1]);
            Assert.Equal(mid.Id, v.Secondary[2]);
            Assert.Equal(v.Secondary, ddg.Secondary);
            Assert.Equal(msl.Id, ddg.Override); // top threat in range: non-sticky F1
            Assert.False(ddg.Sticky);
            Assert.Equal(msl.Id, V(ddg).VipThreat);
        }
    }
}
