using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Row 8 (weapons control), doctrine layers and locks, rows 7 and 24 (own-sensor contacts, classification), row 12.</summary>
    public class DoctrinePictureTests : TestKit
    {
        [Fact]
        public void Row8_weapons_control_maps_to_fire_authority_and_filter()
        {
            var free = Add("Free", Blue, UnitClass.Fighter, 0, 0);
            var tight = Add("Tight", Blue, UnitClass.Fighter, 0, 5);
            var hold = Add("Hold", Blue, UnitClass.Fighter, 0, 10);
            var bandit = Add("Bandit", Red, UnitClass.Bomber, 15, 5);
            bandit.RadarRangeM = 0;
            SetUnitDoctrine(tight, "weaponsControl", "Tight");
            SetUnitDoctrine(hold, "weaponsControl", "HOLD");
            Settle(0.2);
            W.Shots.Clear();
            Settle(10);
            Assert.Equal(FireAuthorityMode.Free, free.Fire);
            Assert.Null(free.Filter);
            Assert.Equal(FireAuthorityMode.Tight, tight.Fire);
            Assert.NotNull(tight.Filter);
            Assert.False(tight.Filter.Allows(bandit.Id)); // a bomber is not a close threat; nothing assigned
            Assert.Equal(FireAuthorityMode.Hold, hold.Fire);
            Assert.True(hold.S.GuardMode); // Hold scans with guard on
            Assert.Contains(W.Shots, s => s.Shooter == free.Id);
            Assert.DoesNotContain(W.Shots, s => s.Shooter == tight.Id);
            Assert.DoesNotContain(W.Shots, s => s.Shooter == hold.Id);
        }

        [Fact]
        public void Row8_an_explicit_engage_order_authorises_its_targets_on_a_hold_unit()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bomber", Red, UnitClass.Bomber, 15, 0);
            b1.RadarRangeM = 0;
            SetUnitDoctrine(f1, "weaponsControl", "hold");
            Settle(0.2);
            Ok(OrderRequest.Engage(Blue, b1.Id, f1.Id));
            Settle(5);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire);
            Assert.Contains(W.Shots, s => s.Shooter == f1.Id && s.Target == b1.Id);
        }

        [Fact]
        public void Doctrine_layers_locks_and_explain()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            E.SetGroup(f1.Id, "Cobra");
            Assert.Empty(E.SetDoctrine(DoctrineLevel.Side, Blue, new Dictionary<string, object> { { "weaponsControl", "tight" } }, new[] { "weaponsControl" }));
            var locked = E.SetDoctrine(DoctrineLevel.Unit, f1.Id.ToString(), new Dictionary<string, object> { { "weaponsControl", "free" } });
            Assert.Contains("locked_by:side", locked.Single());
            Assert.Empty(E.SetDoctrine(DoctrineLevel.Group, "Cobra", new Dictionary<string, object> { { "patrolLeashKm", 40 } }));
            Assert.Empty(E.SetDoctrine(DoctrineLevel.Unit, "v:" + f1.Id, new Dictionary<string, object> { { "onContact", "engage" } }));

            var bad = E.SetDoctrine(DoctrineLevel.Unit, f1.Id.ToString(), new Dictionary<string, object> { { "nope", 1 }, { "advanceOnContact", "maybe" } });
            Assert.Equal(2, bad.Count);

            var d = E.EffectiveDoctrine(f1.Id);
            Assert.Equal(WeaponsControl.Tight, d.WeaponsControl);
            Assert.Equal(40, d.PatrolLeashKm);
            Assert.Equal(OnContact.Engage, d.OnContact);
            Assert.Equal(25, d.SelfDefenceFighterKm); // default
            var explain = E.ExplainDoctrine(f1.Id).ToDictionary(x => x.Key);
            Assert.Equal("side:Blue", explain["weaponsControl"].From);
            Assert.True(explain["weaponsControl"].Locked);
            Assert.Equal("group:Cobra", explain["patrolLeashKm"].From);
            Assert.StartsWith("unit", explain["onContact"].From);
            Assert.Equal("default", explain["huntSticky"].From);
            Settle(0.4);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire);
        }

        [Fact]
        public void Mockup_constants_are_the_defaults()
        {
            var d = new DoctrineValues();
            Assert.Equal(55, d.PatrolLeashKm);
            Assert.Equal(45, d.OwnRadarM(UnitClass.Fighter) / 1000);
            Assert.Equal(25, d.OwnRadarM(UnitClass.Drone) / 1000);
            Assert.Equal(8, d.MarshalKm);
            Assert.Equal(5, d.LandGapS);
            Assert.Equal(90, d.DeckTurnaroundS);
            Assert.Equal(1, d.HuntRepickS);
            Assert.Equal(2, d.AdvanceRepickS);
            Assert.Equal(25, d.SelfDefenceFighterKm);
            Assert.Equal(175, d.ClassificationRangeM(UnitClass.Aew) / 1000);
            Assert.Equal(150, d.ClassificationRangeM(UnitClass.Ship) / 1000);
        }

        [Fact]
        public void Row24_identified_only_inside_the_holders_class_range()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.RadarRangeM = 80000; // radar sees further than the 45 km classification range
            f1.CruiseMps = 0;
            var bandit = Add("Bandit", Red, UnitClass.Fighter, 60, 0);
            bandit.Scripted = true;
            bandit.S.HeadingDeg = 270;
            bandit.S.SpeedMps = 300;
            E.SetSideManaged(Blue, true);
            Settle(0.4);
            var p = E.Picture(Blue);
            Assert.True(p.IsInView(bandit.Id));
            Assert.False(p.IsIdentified(bandit.Id));
            Assert.False(p.AnyIdentified);
            Assert.True(H.RunUntil(() => p.IsIdentified(bandit.Id), 60));
            Assert.True(W.DistKm(f1, bandit) <= 45.5);
            Assert.True(H.Logged(C2EventKind.Contact, "First hostile identified: Bandit (fighter) by Cobra 1's own radar"));
            Assert.Single(H.Events, e => e.Code == "first_identified");
        }

        [Fact]
        public void Row24_data_link_alone_does_not_identify_and_aew_identifies_far()
        {
            var aew = Add("Eye", Blue, UnitClass.Aew, 0, 0);
            aew.CruiseMps = 0;
            var near = Add("Raid", Red, UnitClass.Bomber, 160, 0);
            var linked = Add("Ghost", Red, UnitClass.Bomber, 0, 400);
            W.Detect(Blue, linked.Id);
            E.SetSideManaged(Blue, true);
            Settle(0.4);
            var p = E.Picture(Blue);
            Assert.True(p.IsIdentified(near.Id));     // AEW 175 km
            Assert.True(p.IsInView(linked.Id));
            Assert.False(p.IsIdentified(linked.Id));  // data link only
        }

        [Fact]
        public void Row7_stealth_contact_held_only_by_a_drones_own_radar()
        {
            var drone = Add("Wasp 1", Blue, UnitClass.Drone, 0, 0);
            drone.CruiseMps = 0;
            var ship = Add("Halberd", Blue, UnitClass.Ship, -200, 0);
            var stealth = Add("Shade", Red, UnitClass.Fighter, 10, 0);
            stealth.Rcs = 0.5; // seen within 12.5 km by the drone
            stealth.RadarRangeM = 0;
            E.SetSideManaged(Blue, true);
            Settle(0.4);
            var p = E.Picture(Blue);
            Assert.True(p.IsOwnSensorOnly(stealth.Id));
            Assert.True(H.Logged(C2EventKind.Contact, "Own-sensor contact: Wasp 1's own radar holds Shade"));
            // shared once a surveillance platform holds it too
            var aew = Add("Eye", Blue, UnitClass.Aew, 0, 30);
            aew.RadarRangeM = 100000;
            Settle(0.4);
            Assert.False(p.IsOwnSensorOnly(stealth.Id));
            Assert.Equal(2, p.HolderCount(stealth.Id));
        }

        [Fact]
        public void Row12_fighters_engage_missiles_when_no_aircraft_are_in_view()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.CruiseMps = 0;
            var d1 = Add("Wasp 1", Blue, UnitClass.Drone, 0, 5);
            d1.CruiseMps = 0;
            var never = Add("Cobra 9", Blue, UnitClass.Fighter, 0, -5);
            never.CruiseMps = 0;
            SetUnitDoctrine(never, "airEngageMissiles", "never");
            E.SetSideManaged(Blue, true);
            Settle(0.4);
            Assert.True(f1.EngageMissiles);
            Assert.True(d1.EngageMissiles);
            Assert.False(never.EngageMissiles);
            var raid = Add("Raid", Red, UnitClass.Bomber, 40, 0);
            raid.RadarRangeM = 0;
            Settle(1.2);
            Assert.False(f1.EngageMissiles); // hostile aircraft within 75 km
            raid.S.Pos = W.At(120, 0);
            W.Detect(Blue, raid.Id);
            Settle(1.2);
            Assert.True(f1.EngageMissiles);
        }
    }
}
