using Skizzy.Map.Render;
using Skizzy.Map.Render.Layout;
using Skizzy.Map.Render.Model;
using Skizzy.Map.RenderDemo;

namespace Skizzy.Map.Render.Tests;

public sealed class SizingTests : IDisposable
{
    private readonly TestRig _rig = new();
    public void Dispose() => _rig.Dispose();

    /// <summary>
    /// The mockup bug (doc 23 §4.2): a destroyer shrank every time it fired a SAM because the SAM counted as its
    /// nearest neighbour. Missiles and tracks with RCS under 1/50 of its own must never shrink it.
    /// </summary>
    [Fact]
    public void Destroyer_size_does_not_change_when_SAMs_spawn_next_to_it()
    {
        var scene = new PlotScene();
        var ddg = TestRig.Track(1, TrackKind.Destroyer, 0, 0, name: "Halyard");
        // a peer ship close enough to cap the destroyer, so the spacing rule is genuinely active
        var peer = TestRig.Track(2, TrackKind.Destroyer, 0.9f, 0, name: "Bowline");
        scene.Tracks.Add(ddg);
        scene.Tracks.Add(peer);
        var vp = TestRig.View(0, 0, 30);
        var st = new RenderState { SnapNextFrame = true };

        _rig.Render(scene, vp, st);
        Assert.True(st.TryGetSymbol(1, out float before, out _));
        float uncapped = SymbolSizer.BaseSize(9000, false, SizeMode.Auto, SymbolSizer.ZoomFactor(30), 1);
        Assert.True(before < uncapped - 1, $"precondition: the peer should cap the destroyer ({before} vs {uncapped})");

        // eased frames at 60 Hz; from frame 10 SAMs spawn 50-300 m away, plus a fighter (RCS 4 < 9000/50) and a drone
        var sizes = new List<float>();
        for (int f = 0; f < 40; f++)
        {
            if (f == 10)
            {
                for (int k = 0; k < 4; k++)
                {
                    var sam = TestRig.Track(100 + k, TrackKind.SurfaceToAirMissile, 0.05f + 0.06f * k, 0.04f * k, alt: 30);
                    sam.HeadingDeg = 45; sam.SpeedMps = 900;
                    scene.Tracks.Add(sam);
                }
                scene.Tracks.Add(TestRig.Track(200, TrackKind.Fighter, -0.2f, 0.1f, alt: 300));
                scene.Tracks.Add(TestRig.Track(201, TrackKind.Uav, 0.1f, -0.2f, Affiliation.Hostile, alt: 100));
            }
            st.FrameSeconds = 1 / 60f;
            _rig.Render(scene, vp, st);
            Assert.True(st.TryGetSymbol(1, out float s, out _));
            sizes.Add(s);
        }
        Assert.All(sizes, s => Assert.Equal(before, s, 4));

        // the SAMs themselves do respect every neighbour (they are the small marks drawn on top)
        Assert.True(st.TryGetSymbol(100, out float samSize, out _));
        Assert.True(samSize < 9f, $"SAM next to the ship should be small, was {samSize}");
    }

    [Fact]
    public void A_nearby_peer_ship_does_shrink_the_destroyer()
    {
        var scene = new PlotScene();
        scene.Tracks.Add(TestRig.Track(1, TrackKind.Destroyer, 0, 0));
        var vp = TestRig.View(0, 0, 30);
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out float alone, out _);

        scene.Tracks.Add(TestRig.Track(2, TrackKind.Destroyer, 0.6f, 0));
        st.SnapNextFrame = true;
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out float near, out _);
        Assert.True(near < alone * 0.8f, $"peer should cap: alone {alone}, with peer {near}");
        Assert.Equal(MathF.Max(3f, 0.6f * 30 * 0.85f), near, 3);
    }

    [Fact]
    public void Tracks_below_8px_are_dots_and_missiles_below_5px_are_dots()
    {
        foreach (var (scene, vp) in Scenes())
        {
            var st = new RenderState { SnapNextFrame = true };
            _rig.Render(scene, vp, st);
            int checkedCount = 0;
            foreach (var t in scene.Tracks)
            {
                if (!st.TryGetSymbol(t.Id, out float s, out var tier)) continue;
                checkedCount++;
                if (t.IsMissile)
                    Assert.Equal(s >= 5f ? SymbolTier.Missile : SymbolTier.Dot, tier);
                else
                {
                    var expected = s >= 16f ? SymbolTier.Full : s >= 8f ? SymbolTier.Frame : SymbolTier.Dot;
                    Assert.True(expected == tier, $"{t.Name}: size {s:0.00} drew as {tier}, expected {expected}");
                }
                Assert.True(s >= 3f - 1e-4f, $"{t.Name}: {s} px is below the 3 px floor");
            }
            Assert.True(checkedCount >= 10);
        }
    }

    [Fact]
    public void Tier_hysteresis_does_not_flicker_at_the_16px_boundary()
    {
        var scene = new PlotScene();
        var ftr = TestRig.Track(1, TrackKind.Fighter, 0, 0, alt: 5000);
        scene.Tracks.Add(ftr);
        var vp = TestRig.View(0, 0, 3);
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out float s0, out _);

        float Mul(float px) => px / s0;
        var tiers = new List<SymbolTier>();

        // start just under 16 px, then jitter +-2.5 % around the boundary every frame, both snapped and eased
        ftr.SizeMultiplier = Mul(15.6f);
        st.SnapNextFrame = true;
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out _, out var first);
        Assert.Equal(SymbolTier.Frame, first);
        for (int f = 0; f < 120; f++)
        {
            ftr.SizeMultiplier = Mul(f % 2 == 0 ? 16.4f : 15.6f);
            st.SnapNextFrame = f < 60; // first half jumps, second half eases
            _rig.Render(scene, vp, st);
            st.TryGetSymbol(1, out _, out var tier);
            tiers.Add(tier);
        }
        Assert.All(tiers, t => Assert.Equal(SymbolTier.Frame, t));

        // a real change crosses: 18 px is full; then jitter again stays full
        ftr.SizeMultiplier = Mul(18f);
        st.SnapNextFrame = true;
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out _, out var up);
        Assert.Equal(SymbolTier.Full, up);
        for (int f = 0; f < 60; f++)
        {
            ftr.SizeMultiplier = Mul(f % 2 == 0 ? 15.6f : 16.4f);
            st.SnapNextFrame = true;
            _rig.Render(scene, vp, st);
            st.TryGetSymbol(1, out _, out var tier);
            Assert.Equal(SymbolTier.Full, tier);
        }
        ftr.SizeMultiplier = Mul(14f);
        st.SnapNextFrame = true;
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out _, out var down);
        Assert.Equal(SymbolTier.Frame, down);
    }

    [Theory]
    [InlineData(15.9f, SymbolTier.Frame, SymbolTier.Frame)]
    [InlineData(17.5f, SymbolTier.Frame, SymbolTier.Frame)]
    [InlineData(17.7f, SymbolTier.Frame, SymbolTier.Full)]
    [InlineData(14.5f, SymbolTier.Full, SymbolTier.Full)]
    [InlineData(14.3f, SymbolTier.Full, SymbolTier.Frame)]
    [InlineData(7.3f, SymbolTier.Frame, SymbolTier.Frame)]
    [InlineData(7.1f, SymbolTier.Frame, SymbolTier.Dot)]
    [InlineData(8.7f, SymbolTier.Dot, SymbolTier.Dot)]
    [InlineData(8.9f, SymbolTier.Dot, SymbolTier.Frame)]
    [InlineData(20f, SymbolTier.Dot, SymbolTier.Full)]
    public void TierFor_applies_ten_percent_hysteresis(float size, SymbolTier previous, SymbolTier expected) =>
        Assert.Equal(expected, SymbolSizer.TierFor(size, false, previous));

    [Fact]
    public void Missile_tier_hysteresis_and_new_tracks_use_plain_thresholds()
    {
        Assert.Equal(SymbolTier.Missile, SymbolSizer.TierFor(5f, true, null));
        Assert.Equal(SymbolTier.Dot, SymbolSizer.TierFor(4.9f, true, null));
        Assert.Equal(SymbolTier.Missile, SymbolSizer.TierFor(4.6f, true, SymbolTier.Missile));
        Assert.Equal(SymbolTier.Dot, SymbolSizer.TierFor(5.4f, true, SymbolTier.Dot));
        Assert.Equal(SymbolTier.Full, SymbolSizer.TierFor(16f, false, null));
        Assert.Equal(SymbolTier.Dot, SymbolSizer.TierFor(7.99f, false, null));
    }

    [Fact]
    public void Size_changes_ease_over_about_150ms()
    {
        var scene = new PlotScene();
        var ftr = TestRig.Track(1, TrackKind.Fighter, 0, 0, alt: 5000);
        scene.Tracks.Add(ftr);
        var vp = TestRig.View(0, 0, 3);
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        st.TryGetSymbol(1, out float s0, out _);
        ftr.SizeMultiplier = 1.8f; // a jump well under the 30 px snap limit
        float target = s0 * 1.8f;
        var seen = new List<float>();
        for (int f = 0; f < 12; f++)
        {
            st.FrameSeconds = 1 / 60f;
            _rig.Render(scene, vp, st);
            st.TryGetSymbol(1, out float s, out _);
            seen.Add(s);
        }
        Assert.True(seen[0] > s0 && seen[0] < s0 + (target - s0) * 0.3f, "first frame moves only part of the way");
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1], "monotonic");
        float at150 = seen[8]; // 9 frames = 150 ms
        Assert.True((target - at150) / (target - s0) < 0.25f, $"at 150 ms within 25 % of target ({at150} -> {target})");
    }

    [Fact]
    public void Selected_tracks_draw_1_2x_and_per_item_override_applies()
    {
        var scene = new PlotScene();
        var a = TestRig.Track(1, TrackKind.Fighter, 0, 0, alt: 5000);
        var b = TestRig.Track(2, TrackKind.Fighter, 50, 0, alt: 5000);
        scene.Tracks.Add(a); scene.Tracks.Add(b);
        b.Selected = true;
        var st = new RenderState();
        _rig.Render(scene, TestRig.View(25, 0, 3), st);
        st.TryGetSymbol(1, out float sa, out _);
        st.TryGetSymbol(2, out float sb, out _);
        Assert.Equal(sa * 1.2f, sb, 3);
    }

    internal static IEnumerable<(PlotScene, Viewport)> Scenes()
    {
        var sample = SampleScene.Build();
        yield return (sample, sample.View);
        yield return (sample, TestRig.View(48, 4, 9));
        yield return (sample, TestRig.View(3, 0, 34));
        var raid = SampleScene.BuildMissileRaid();
        yield return (raid, raid.View);
        var perf = PerfScene.Build(2000);
        yield return (perf, perf.View);
    }
}
