using Skizzy.Map.Render.Model;
using Skizzy.Map.RenderDemo;

namespace Skizzy.Map.Render.Tests;

public class SampleSceneTests
{
    // Reference values below were produced by running the mockup's own JS (rnd(), then setup()'s
    // red-aircraft and UAV loops, verbatim) under node.

    [Fact]
    public void Rng_MatchesMockupFirstFiveOutputs()
    {
        var rng = new SampleScene.Rng(11);
        double[] expected = [0.511587049, 0.529946408, 0.608118564, 0.590157636, 0.850776696];
        foreach (var e in expected) Assert.Equal(e, rng.Next(), 1e-8);
    }

    [Fact]
    public void Build_UntranslatedRedAndUavPositionsMatchMockup()
    {
        var s = SampleScene.Build();
        var r2 = Named(s, "R2-01");
        Assert.Equal(232.24516943562776, r2.X, 1e-3);
        Assert.Equal(-17.97589761391282, r2.Y, 1e-3);
        Assert.Equal(3708.974546706304, r2.AltitudeM, 1e-2);
        Assert.Equal(231.778, r2.SpeedMps, 1e-1);

        var r5 = Named(s, "R5-03");
        Assert.Equal(209.1013741288334, r5.X, 1e-3);
        Assert.Equal(95.76373369526118, r5.Y, 1e-3);

        var r6 = Named(s, "R6-10");
        Assert.Equal(211.8441374655813, r6.X, 1e-3);
        Assert.Equal(-98.41004648804665, r6.Y, 1e-3);

        var d1 = Named(s, "D01");
        Assert.Equal(168.09220811421983, d1.X, 1e-3);
        Assert.Equal(112.74126005126163, d1.Y, 1e-3);
        var d20 = Named(s, "D20");
        Assert.Equal(173.99256800781004, d20.X, 1e-3);
        Assert.Equal(116.48736729966477, d20.Y, 1e-3);
    }

    [Fact]
    public void Build_R1IsTranslatedToLaunchPositionKeepingOriginalSpread()
    {
        var s = SampleScene.Build();
        var r1 = s.Tracks.Where(t => t.Name.StartsWith("R1-", StringComparison.Ordinal)).ToList();
        Assert.Equal(10, r1.Count);
        Assert.Equal(112.0, r1.Average(t => t.X), 1e-3);
        Assert.Equal(18.0, r1.Average(t => t.Y), 1e-3);

        // Original (pre-translation) R1-01 is (228.139, -53.641) with group centroid (227.264, -55.698).
        var t1 = Named(s, "R1-01");
        Assert.Equal(228.1390445837751 - 227.26363176880403 + 112.0, t1.X, 1e-3);
        Assert.Equal(-53.64064310118556 - -55.698482709378005 + 18.0, t1.Y, 1e-3);

        // Heading points at the origin.
        double expected = ((90 - Math.Atan2(-t1.Y, -t1.X) * 180 / Math.PI) % 360 + 360) % 360;
        Assert.Equal(expected, t1.HeadingDeg, 1e-2);
    }

    [Fact]
    public void Build_TrackCounts()
    {
        var s = SampleScene.Build();
        var t = s.Tracks;
        Assert.Equal(138, t.Count);

        var ships = t.Where(x => x.Side == Side.Blue && x.Kind is TrackKind.Carrier or TrackKind.Destroyer or TrackKind.Submarine).ToList();
        Assert.Equal(5, ships.Count);
        Assert.Single(ships, x => x.Kind == TrackKind.Submarine);
        Assert.Single(t, x => x.Kind == TrackKind.EarlyWarning);
        Assert.Equal(40, t.Count(x => x.Side == Side.Blue && x.Kind is TrackKind.Fighter or TrackKind.Ucav));

        var redAir = t.Where(x => x.Side == Side.Red && x.Kind is TrackKind.Strike or TrackKind.Fighter).ToList();
        var g03 = redAir.Where(x => x.Affiliation == Affiliation.Hostile).ToList();
        var g45 = redAir.Where(x => x.Affiliation == Affiliation.Unknown).ToList();
        Assert.Equal(40, g03.Count);
        Assert.Equal(16, g03.Count(x => x.Kind == TrackKind.Strike));
        Assert.Equal(20, g45.Count);
        Assert.All(g45, x => Assert.Equal(TrackKind.Fighter, x.Kind));

        Assert.Equal(20, t.Count(x => x.Kind == TrackKind.Uav && x.Affiliation == Affiliation.Hostile));
        Assert.Equal(6, t.Count(x => x.Kind == TrackKind.AntiShipMissile));
        Assert.Equal(4, t.Count(x => x.Kind == TrackKind.SurfaceToAirMissile));
        Assert.Equal(2, t.Count(x => x.Kind == TrackKind.AirToAirMissile));
    }

    [Fact]
    public void Build_KestrelIsFirstSelectedAndIdsAreUnique()
    {
        var s = SampleScene.Build();
        var k = s.Tracks[0];
        Assert.Equal(1, k.Id);
        Assert.Equal("Kestrel", k.Name);
        Assert.True(k.Selected);
        Assert.Equal(0f, k.X);
        Assert.Equal(0f, k.Y);
        Assert.Single(s.Tracks, x => x.Selected);
        Assert.Equal(s.Tracks.Count, s.Tracks.Select(x => x.Id).Distinct().Count());
        Assert.Equal(ControlState.Manual, Named(s, "Halyard").Control);
        Assert.Equal(ControlState.Agent, Named(s, "Tide").Control);
    }

    [Fact]
    public void Build_OrdersAreConsistent()
    {
        var s = SampleScene.Build();
        Assert.Equal(13, s.Orders.Count);
        Assert.Equal(4, s.Orders.Count(o => o.Type == OrderType.Attack));
        Assert.Equal(8, s.Orders.Count(o => o.Type == OrderType.Route));
        Assert.Equal(1, s.Orders.Count(o => o.Type == OrderType.Move));

        foreach (var o in s.Orders)
        {
            var unit = s.Find(o.TrackId);
            Assert.NotNull(unit);
            Assert.Equal(ControlState.Order, unit!.Control);
            if (o.Type == OrderType.Route)
            {
                Assert.Equal(1, o.RouteId);
                Assert.Contains(s.Routes, r => r.Id == o.RouteId);
            }
            if (o.Type == OrderType.Attack) Assert.NotNull(s.Find(o.TargetTrackId));
        }
        Assert.Equal(s.Orders.Count, s.Orders.Select(o => o.TrackId).Distinct().Count());
        Assert.Equal(4, Assert.Single(s.Routes).Points.Count);
    }

    [Fact]
    public void Build_VisibleTracksHaveTrailsEndingAtThePosition()
    {
        AssertTrails(SampleScene.Build());
    }

    [Fact]
    public void Build_MissilesAreWiredToTargets()
    {
        var s = SampleScene.Build();
        foreach (var m in s.Tracks.Where(x => x.IsMissile))
        {
            Assert.NotEqual(0, m.TargetId);
            Assert.NotNull(s.Find(m.TargetId));
        }
        Assert.Equal(5, s.Events.Count);
        Assert.False(Named(s, "R1-04").Visible);
        Assert.Equal(480, s.Time);
    }

    [Fact]
    public void Raid_Counts()
    {
        var s = SampleScene.BuildMissileRaid();
        var asm = s.Tracks.Where(x => x.Kind == TrackKind.AntiShipMissile).ToList();
        Assert.Equal(128, asm.Count);
        Assert.All(asm, x => Assert.Equal(Affiliation.Hostile, x.Affiliation));
        Assert.Equal(24, s.Tracks.Count(x => x.Kind == TrackKind.SurfaceToAirMissile));
        Assert.Equal(24, s.Tracks.Count(x => x.Kind == TrackKind.Uav));
        Assert.Equal(16, s.Tracks.Count(x => x.Kind == TrackKind.Fighter));
        Assert.Equal(5, s.Tracks.Count(x => x.Side == Side.Blue && x.Kind is TrackKind.Carrier or TrackKind.Destroyer or TrackKind.Submarine));
        Assert.Equal(s.Tracks.Count, s.Tracks.Select(x => x.Id).Distinct().Count());
        Assert.Equal(8, s.Events.Count);
        Assert.Equal(300, s.Time);
        Assert.True(Named(s, "Kestrel").Selected);
        AssertTrails(s);
    }

    [Fact]
    public void BothScenesAreDeterministic()
    {
        AssertSame(SampleScene.Build(), SampleScene.Build());
        AssertSame(SampleScene.BuildMissileRaid(), SampleScene.BuildMissileRaid());
    }

    private static PlotTrack Named(PlotScene s, string name) => s.Tracks.Single(t => t.Name == name);

    private static void AssertTrails(PlotScene s)
    {
        foreach (var t in s.Tracks.Where(x => x.Visible))
        {
            Assert.True(t.Trail.Count >= 12, $"{t.Name} trail has {t.Trail.Count} points");
            var (lx, ly) = t.Trail[t.Trail.Count - 1];
            double d = Math.Sqrt((lx - t.X) * (lx - t.X) + (ly - t.Y) * (ly - t.Y));
            Assert.True(d <= 1.0, $"{t.Name} last trail point is {d:F3} km from its position");
        }
    }

    private static void AssertSame(PlotScene a, PlotScene b)
    {
        Assert.Equal(a.Tracks.Count, b.Tracks.Count);
        for (int i = 0; i < a.Tracks.Count; i++)
        {
            Assert.Equal(a.Tracks[i].Id, b.Tracks[i].Id);
            Assert.Equal(a.Tracks[i].X, b.Tracks[i].X);
            Assert.Equal(a.Tracks[i].Y, b.Tracks[i].Y);
        }
    }
}
