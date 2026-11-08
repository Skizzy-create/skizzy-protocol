using System.Collections.Generic;
using System.Linq;
using Skizzy.C2.Fake;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Shared scenario helpers: a harness, side names and quick unit views.</summary>
    public abstract class TestKit
    {
        protected const string Blue = "Blue";
        protected const string Red = "Red";
        protected readonly C2Harness H = new C2Harness();

        protected FakeBda W => H.World;
        protected C2Engine E => H.Engine;

        protected FakeUnit Add(string name, string side, UnitClass cls, double eastKm, double northKm, double altM = double.NaN)
        {
            return W.Add(name, side, cls, eastKm, northKm, altM);
        }

        protected UnitView V(FakeUnit u) { return E.GetUnitView(u.Id); }

        protected IssueResult Ok(OrderRequest req)
        {
            var r = H.Issue(req);
            Assert.True(r.Accepted, "order refused: " + r.Summary + "\n" + H.Log());
            return r;
        }

        protected static OrderRequest Units(OrderRequest r, params FakeUnit[] units)
        {
            foreach (var u in units) r.Units.Add(u.Id);
            return r;
        }

        protected static uint[] Ids(params FakeUnit[] units) { return units.Select(u => u.Id).ToArray(); }

        protected void SetUnitDoctrine(FakeUnit u, string key, object value)
        {
            var errors = E.SetDoctrine(DoctrineLevel.Unit, u.Id.ToString(), new Dictionary<string, object> { { key, value } });
            Assert.Empty(errors);
        }

        protected void SetSideDoctrine(string side, string key, object value)
        {
            var errors = E.SetDoctrine(DoctrineLevel.Side, side, new Dictionary<string, object> { { key, value } });
            Assert.Empty(errors);
        }

        protected UnitOrder UnitOrderOf(IssueResult r, FakeUnit u) { return r.Order.Units.First(x => x.Unit == u.Id); }

        protected void Settle(double s = 1) { H.Run(s); }
    }
}
