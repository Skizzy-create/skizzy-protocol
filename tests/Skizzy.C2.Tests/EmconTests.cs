using System.Collections.Generic;
using Skizzy.C2.Fake;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Doctrine emcon (parity row 76): passive switches the unit's radars off through IRadarControl; back to active on unset.</summary>
    public sealed class EmconTests : TestKit
    {
        [Fact]
        public void Unit_emcon_passive_turns_the_radar_off_and_its_contacts_go_with_it()
        {
            var ddg = Add("DDG 1", Blue, UnitClass.Ship, 0, 0);
            ddg.CruiseMps = 0;
            var red = Add("Red 1", Red, UnitClass.Fighter, 60, 0);
            red.Scripted = true;
            red.RadarRangeM = 0;
            E.SetSideManaged(Blue, true);
            Settle(1.2);
            Assert.True(E.Picture(Blue).IsInView(red.Id));
            Assert.True(E.HasRadarControl);
            SetUnitDoctrine(ddg, "emcon", "passive");
            Settle(1.2);
            Assert.False(ddg.RadarOn);
            Assert.Equal(1, W.CallCount("radar " + ddg.Id + " passive"));
            Assert.False(E.Picture(Blue).IsInView(red.Id));     // nothing else holds it
            E.UnsetDoctrine(DoctrineLevel.Unit, ddg.Id.ToString(), new List<string> { "emcon" });
            Settle(1.2);
            Assert.True(ddg.RadarOn);
            Assert.True(E.Picture(Blue).IsInView(red.Id));
        }

        [Fact]
        public void Side_emcon_passive_applies_to_every_unit_of_an_unmanaged_side()
        {
            var a = Add("DDG 1", Blue, UnitClass.Ship, 0, 0);
            var b = Add("DDG 2", Blue, UnitClass.Ship, 0, 10);
            a.CruiseMps = b.CruiseMps = 0;
            SetSideDoctrine(Blue, "emcon", "passive");
            Settle(0.4);
            Assert.False(a.RadarOn);
            Assert.False(b.RadarOn);
        }
    }
}
