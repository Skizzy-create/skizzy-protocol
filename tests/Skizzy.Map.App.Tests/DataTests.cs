using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;
using Xunit;

namespace Skizzy.Map.App.Tests;

public class HistoryRingTests
{
    [Fact]
    public void StraightLineInterpolationIsExact()
    {
        var h = new HistoryRing(16);
        // 250 m/s due east (heading 090) = 0.25 km/s on +X
        for (int i = 0; i <= 5; i++) h.Add(i, i * 0.25, 10, 5000, 90, 250);
        Assert.True(h.TrySample(2.5, out var x, out var y, out var alt, out var hdg, out var spd));
        Assert.Equal(0.625, x, 4);
        Assert.Equal(10, y, 4);
        Assert.Equal(5000, alt, 1);
        Assert.Equal(90, hdg, 2);
        Assert.Equal(250, spd, 2);
    }

    [Fact]
    public void BeforeFirstSampleFailsAndExtrapolationIsBounded()
    {
        var h = new HistoryRing(8);
        h.Add(10, 0, 0, 0, 0, 1000); // 1 km/s north
        Assert.False(h.TrySample(9.9, out _, out _, out _, out _, out _));
        Assert.True(h.TrySample(11, out _, out var y, out _, out _, out _));
        Assert.Equal(1, y, 4);
        Assert.False(h.TrySample(13, out _, out _, out _, out _, out _, maxAhead: 1.5));
    }

    [Fact]
    public void WrapsAtCapacityKeepingNewest()
    {
        var h = new HistoryRing(4);
        for (int i = 0; i < 10; i++) h.Add(i, i, 0, 0, 90, 0);
        Assert.Equal(4, h.Count);
        Assert.Equal(6, h.FirstT);
        Assert.Equal(9, h.LastT);
        Assert.Equal(3, h.IndexAtOrBefore(9.5));
        Assert.Equal(-1, h.IndexAtOrBefore(5));
    }

    [Fact]
    public void SameInstantReplacesOlderIsIgnored()
    {
        var h = new HistoryRing(4);
        h.Add(1, 1, 0, 0, 0, 0);
        h.Add(2, 2, 0, 0, 0, 0);
        h.Add(2, 5, 0, 0, 0, 0);
        h.Add(1.5, 9, 0, 0, 0, 0);
        Assert.Equal(2, h.Count);
        Assert.Equal(5, h.PointAt(1).X);
    }

    [Fact]
    public void HermiteFollowsATurnInsteadOfTheChord()
    {
        var h = new HistoryRing(4);
        // quarter turn: heading north at (0,0), heading east at (1,1), 1 km/s; chord midpoint is (0.5,0.5)
        h.Add(0, 0, 0, 0, 0, 1000);
        h.Add(1.5708, 1, 1, 0, 90, 1000);
        Assert.True(h.TrySample(0.7854, out var x, out var y, out _, out _, out _));
        Assert.True(y > x, $"mid-turn point ({x:0.000},{y:0.000}) should bulge toward the arc");
        Assert.InRange(x, 0.2, 0.45);
    }
}

public class TrackInfoTests
{
    [Fact]
    public void ExtrapolatesAlongVelocityAndFreezesAfterLimit()
    {
        var t = new TrackInfo(1, "a", 8);
        t.SetSample(10, 0, 0, 0, 90, 250, 0.25, 0, displayNow: 10);
        Assert.Equal(0.25, t.PositionAt(11).X, 6);
        Assert.Equal(0.25 * TrackInfo.MaxExtrapolation, t.PositionAt(100).X, 6);
    }

    [Fact]
    public void NewSampleJumpIsBlendedAway()
    {
        var t = new TrackInfo(1, "a", 8);
        t.SetSample(0, 0, 0, 0, 90, 0, 0, 0, displayNow: 0);
        t.SetSample(1, 1, 0, 0, 90, 0, 0, 0, displayNow: 1, blendSeconds: 0.25); // jumps 1 km east
        Assert.Equal(0, t.PositionAt(1).X, 6);             // drawn where it was
        Assert.InRange(t.PositionAt(1.25).X, 0.6, 0.7);    // 1 - e^-1
        Assert.Equal(1, t.PositionAt(3).X, 3);             // converged
    }

    [Fact]
    public void TeleportSnaps()
    {
        var t = new TrackInfo(1, "a", 8);
        t.SetSample(0, 0, 0, 0, 0, 0, 0, 0, displayNow: 0);
        t.SetSample(1, 50, 0, 0, 0, 0, 0, 0, displayNow: 1);
        Assert.Equal(50, t.PositionAt(1).X, 6);
    }

    [Fact]
    public void RecordsOncePerInterval()
    {
        var t = new TrackInfo(1, "a", 64);
        for (int i = 0; i < 50; i++)
        {
            t.SetSample(i * 0.1, i, 0, 0, 0, 0, 0, 0, i * 0.1, 0);
            t.Record(1.0);
        }
        Assert.Equal(5, t.History.Count); // t = 0, 1.0, 2.0, 3.0, 4.0
    }
}

public class DemoBattleTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static (DemoBattleSource src, MapData data) Run(double seconds, double speed = 8, bool autoResume = true)
    {
        var data = new MapData();
        var src = new DemoBattleSource();
        src.Attach(data);
        src.SetSpeed(speed);
        double frame = 1 / 30.0;
        int frames = (int)Math.Ceiling(seconds / (frame * speed));
        for (int i = 0; i < frames; i++)
        {
            src.Tick(frame);
            if (src.Paused && autoResume) src.SetPaused(false);
        }
        return (src, data);
    }

    [Fact]
    public void SetupMatchesTheMockup()
    {
        var data = new MapData();
        var src = new DemoBattleSource();
        src.Attach(data);
        Assert.Equal("Kestrel", data.Get(1)!.Name);
        Assert.Equal("Cleat", data.Get(4)!.Name);
        Assert.True(data.Get(1)!.Vip);
        int red = data.Tracks.Count(t => t.Side == Side.Red);
        int ships = data.Tracks.Count(t => t.Side == Side.Blue && t.Domain != TrackDomain.Air);
        Assert.Equal(320 + 200 + 24 + 200 + 20, red); // strike packages, sweeps, DCAP, drones, stealth
        Assert.Equal(3 + 30 + 6, ships);
        // stealth tracks are invisible to Blue until detected; Red sees its own
        var stealth = data.Tracks.First(t => t.Name == "S-01");
        Assert.Equal(TrackDetection.None, stealth.DetBlue);
        Assert.Equal(TrackDetection.Shared, stealth.DetRed);
        Assert.Contains(data.Log.Entries, e => e.Text.StartsWith("New air contacts: 62 groups, 544 tracks", StringComparison.Ordinal));
        Assert.Equal("Y1 D014 03:12:00", data.UtText(0));
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = Run(60).data;
        var b = Run(60).data;
        Assert.Equal(a.Tracks.Count, b.Tracks.Count);
        foreach (var t in a.Tracks)
        {
            var u = b.Get(t.Id)!;
            Assert.Equal(t.X, u.X, 9);
            Assert.Equal(t.Y, u.Y, 9);
            Assert.Equal(t.Alive, u.Alive);
        }
    }

    [Fact]
    public void BattleUnfolds()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (src, data) = Run(900); // the first strike aircraft reach launch range after about ten minutes
        _out.WriteLine($"900 s of battle in {sw.Elapsed.TotalSeconds:0.0} s wall ({sw.Elapsed.TotalMilliseconds / 9000:0.00} ms per 0.1 s step); {src.Sim.Objs.Count} objects");
        var s = src.Sim.Stats;
        Assert.True(s.ExpRed["ASM"] > 0, "Red launched anti-ship missiles");
        Assert.True(s.ExpBlue["SM-2"] + s.ExpBlue["ESSM"] > 0, "Blue ships fired");
        Assert.True(s.ExpBlue["AAM"] > 0, "Blue fighters fired");
        Assert.True(s.Intercepts + s.Ciws > 0, "missiles were intercepted");
        Assert.True(s.LostRed.Values.Sum() > 0, "Red lost aircraft");
        foreach (var c in new[] { LogCategory.Contact, LogCategory.Weapons, LogCategory.Orders, LogCategory.Comms, LogCategory.Damage })
            Assert.True(data.Log.CountOf(c) > 0, $"log has {c} entries");
        Assert.NotEmpty(data.Comms);
        Assert.Contains(data.Events, e => e.Kind == PlotEventKind.Intercept || e.Kind == PlotEventKind.Ciws);
        Assert.Contains(data.Tracks, t => t.IsMissile && !t.Alive);
        Assert.True(data.Losses.Version > 0);
        Assert.InRange(data.LiveTime, 899, 902);
    }

    [Fact]
    public void FirstLaunchPausesWithBannerAndResumeHidesIt()
    {
        var data = new MapData();
        var src = new DemoBattleSource();
        src.Attach(data);
        src.SetSpeed(8);
        int guard = 0;
        while (!src.Paused && guard++ < 20000) src.Tick(1 / 30.0);
        Assert.True(src.Paused);
        Assert.NotNull(data.PauseBanner);
        double t = data.LiveTime;
        src.Tick(1 / 30.0);
        Assert.Equal(t, data.LiveTime, 6); // paused: time stands still
        src.SetPaused(false);
        Assert.Null(data.PauseBanner);
        src.Tick(1 / 30.0);
        Assert.True(data.LiveTime > t);
    }

    [Fact]
    public void ReplayShowsThePastAndHaltsTheBattle()
    {
        var (src, data) = Run(200);
        double live = data.LiveTime;
        data.ReplayTime = 30;
        src.Tick(1 / 30.0);
        Assert.Equal(live, data.LiveTime, 6); // demo halts while replaying, as the mockup does
        var scene = new PlotScene();
        var sync = new SceneSync(scene);
        sync.Sync(data, new HashSet<int>(), new Dictionary<int, float>(), 0);
        Assert.Equal(30, scene.Time, 6);
        // a missile launched after t=30 is absent; Kestrel is near its start
        Assert.DoesNotContain(scene.Tracks, p => data.Get(p.Id)!.BornT > 30.5);
        var k = scene.Find(1)!;
        Assert.InRange(k.X, 0, 1.5);
        Assert.True(k.Trail.Count > 10);
    }

    [Fact]
    public void RedFogHidesDistantBlueUnits()
    {
        var data = new MapData();
        var src = new DemoBattleSource();
        src.Attach(data);
        var scene = new PlotScene();
        var sync = new SceneSync(scene) { View = ViewSide.Red };
        sync.Sync(data, new HashSet<int>(), new Dictionary<int, float>(), 0);
        var kestrel = scene.Find(1)!;
        Assert.Equal(TrackDetection.None, kestrel.Detection); // 300+ km from the raid at t=0
        Assert.Equal(Affiliation.Hostile, kestrel.Affiliation);
        var red = scene.Tracks.First(p => p.Side == Side.Red);
        Assert.Equal(Affiliation.Friend, red.Affiliation);
        sync.View = ViewSide.God;
        sync.Sync(data, new HashSet<int>(), new Dictionary<int, float>(), 0);
        Assert.Equal(TrackDetection.Shared, scene.Find(1)!.Detection);
        Assert.Equal(TrackDetection.Shared, scene.Tracks.First(p => p.Name == "S-01").Detection);
    }

    [Fact]
    public void FilterHidesButKeepsTracks()
    {
        var data = new MapData();
        var src = new DemoBattleSource();
        src.Attach(data);
        var scene = new PlotScene();
        var sync = new SceneSync(scene) { Filter = t => t.Domain != TrackDomain.Air };
        sync.Sync(data, new HashSet<int> { 1 }, new Dictionary<int, float> { [1] = 1.5f }, 0);
        Assert.True(sync.FilteredOut > 500);
        Assert.False(scene.Tracks.First(p => p.Domain == TrackDomain.Air).Visible);
        Assert.True(scene.Find(1)!.Selected);
        Assert.Equal(1.5f, scene.Find(1)!.SizeMultiplier);
    }
}
