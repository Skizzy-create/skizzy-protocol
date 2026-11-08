using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Layout;
using Skizzy.Map.Render.Model;
using Skizzy.Map.Render.Symbols;
using Skizzy.Map.Render.Text;
using Skizzy.Map.RenderDemo;
using Xunit.Abstractions;

namespace Skizzy.Map.Render.Tests;

/// <summary>Regression tests for the defects found in the review of the render core.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly ITestOutputHelper _out;
    public ReviewFixTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _rig.Dispose();

    /// <summary>
    /// One renderer serving two map windows on monitors with different DPI used to flush its sprite cache on every
    /// switch (about 60 ms and 1.2 MB of garbage per pair of 2,000-track frames). Each scale now keeps its own store.
    /// </summary>
    [Fact]
    public void Two_windows_at_different_DPI_share_a_renderer_without_flushing_sprites()
    {
        var scene = PerfScene.Build(500);
        var stA = new RenderState();
        var stB = new RenderState();
        using var b = SKSurface.Create(new SKImageInfo(TestRig.W * 3 / 2, TestRig.H * 3 / 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        var vp = TestRig.View(scene.View.CenterX, scene.View.CenterY, scene.View.Scale);
        void Pair()
        {
            _rig.Render(scene, vp, stA);
            b.Canvas.Save(); b.Canvas.Scale(1.5f);
            _rig.Renderer.Render(b.Canvas, scene, vp, stB);
            b.Canvas.Restore();
        }
        for (int i = 0; i < 5; i++) Pair();
        int sprites = _rig.Renderer.SpriteCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Pair();
        long perPair = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        _out.WriteLine($"sprites {sprites}, scales {_rig.Renderer.SpriteScales}, {perPair} B per pair");
        Assert.Equal(2, _rig.Renderer.SpriteScales);
        Assert.Equal(sprites, _rig.Renderer.SpriteCount);
        Assert.Equal(0, perPair);
    }

    [Fact]
    public void Sprite_cache_memory_stays_bounded_and_recent_sprites_survive_a_turnover()
    {
        using var paths = new SymbolPaths();
        using var cache = new SpriteCache(Theme.Default, paths, FontSet.Default.UiSemiBold, FontSet.Default.UiBold);
        cache.SetScale(2f); // device pixels x4: reaches the byte bound quickly
        var keep = cache.Frame(Affiliation.Friend, TrackDomain.Surface, true, false, 30f, TrackKind.Carrier);
        int made = 0;
        for (float s = 5f; s <= 44f; s += 0.5f)
            for (int dir = 0; dir < 64; dir++)
            {
                cache.Missile(Affiliation.Hostile, s, dir * 360f / 64);
                made++;
                // a sprite the plot keeps using is promoted on every lookup, so it is never the one dropped
                Assert.Same(keep, cache.Frame(Affiliation.Friend, TrackDomain.Surface, true, false, 30f, TrackKind.Carrier));
                Assert.True(cache.Bytes <= SpriteCache.MaxBytesPerScale, $"{cache.Bytes} B after {made} sprites");
                Assert.True(cache.Count <= 2 * SpriteCache.MaxEntriesPerGeneration);
            }
        _out.WriteLine($"made {made} missile sprites; holding {cache.Count} sprites, {cache.Bytes / 1024} KiB");
        Assert.True(cache.Count < made, "old variants must have been dropped");
    }

    [Fact]
    public void A_new_but_equal_name_string_does_not_reshape_the_label()
    {
        var scene = SampleScene.Build();
        var st = new RenderState();
        for (int i = 0; i < 5; i++) _rig.Render(scene, scene.View, st);
        // what a protocol adapter does every update: a fresh string with the same text
        var fresh = scene.Tracks.Select(t => t.Name.ToCharArray()).ToArray();
        long allocated = 0;
        for (int f = 0; f < 10; f++)
        {
            for (int i = 0; i < scene.Tracks.Count; i++) scene.Tracks[i].Name = new string(fresh[i]);
            long before = GC.GetAllocatedBytesForCurrentThread();
            _rig.Render(scene, scene.View, st);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Altitude_label_and_its_cache_key_round_the_same_way()
    {
        using var font = new SKFont(FontSet.Default.DataRegular, 10.5f);
        var t = TestRig.Track(1, TrackKind.Fighter, 0, 0, alt: 1240, name: "Viper 1");
        var v = new TrackVisual();
        var first = LabelPlacer.LabelFor(t, v, font);
        Assert.Equal("1.2k", LabelPlacer.AltText(t));
        t.AltitudeM = 1250; // 12.5 hundreds: the old key rounded to even (12) and kept the stale "1.2k"
        var second = LabelPlacer.LabelFor(t, v, font);
        Assert.Equal("1.3k", LabelPlacer.AltText(t));
        Assert.NotSame(first, second);

        Assert.Equal("312 m", LabelPlacer.AltText(TestRig.Track(2, TrackKind.Uav, 0, 0, alt: 312.4f)));
        Assert.Equal("1000 m", LabelPlacer.AltText(TestRig.Track(3, TrackKind.Uav, 0, 0, alt: 999.6f)));
        Assert.Equal("9.0k", LabelPlacer.AltText(TestRig.Track(4, TrackKind.EarlyWarning, 0, 0, alt: 9000)));
        Assert.Equal("−120 m", LabelPlacer.AltText(TestRig.Track(5, TrackKind.Submarine, 0, 0, alt: -120)));
        Assert.Equal("", LabelPlacer.AltText(TestRig.Track(6, TrackKind.Destroyer, 0, 0)));
        Assert.Equal("", LabelPlacer.AltText(TestRig.Track(7, TrackKind.AntiShipMissile, 0, 0, alt: 15)));
    }

    [Fact]
    public void Bad_numbers_from_the_wire_do_not_crash_the_frame()
    {
        var scene = new PlotScene();
        scene.Tracks.Add(TestRig.Track(1, TrackKind.Fighter, float.NaN, 0, alt: 5000));
        var rcs = TestRig.Track(2, TrackKind.Fighter, 1, 0, alt: float.NaN);
        rcs.Rcs = float.NaN; rcs.HeadingDeg = float.NaN; rcs.SpeedMps = float.NaN;
        scene.Tracks.Add(rcs);
        scene.Tracks.Add(TestRig.Track(3, TrackKind.Destroyer, float.PositiveInfinity, 0));
        var st = new RenderState();
        _rig.Render(scene, TestRig.View(0, 0, 10), st);
        Assert.Equal(1, st.LastFrame.TracksVisible); // the NaN-RCS track is still drawn
        Assert.True(st.TryGetSymbol(2, out float s, out _) && float.IsFinite(s));
        rcs.Rcs = 4; // and recovers once the data is good again
        _rig.Render(scene, TestRig.View(0, 0, 10), st);
        Assert.True(st.TryGetSymbol(2, out s, out _) && s > SymbolSizer.MinSizePx);
    }

    [Theory]
    [InlineData(0f, 1920, 1080)]
    [InlineData(float.NaN, 1920, 1080)]
    [InlineData(-3f, 1920, 1080)]
    [InlineData(3f, 0, 0)]
    [InlineData(1e-7f, 1920, 1080)]
    [InlineData(1e6f, 1920, 1080)]
    public async Task Degenerate_views_draw_without_hanging(float scale, int w, int h)
    {
        var scene = SampleScene.Build();
        var st = new RenderState();
        var frame = Task.Run(() => _rig.Render(scene, new Viewport(0, 0, scale, w, h), st));
        var done = await Task.WhenAny(frame, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == frame, "the frame must finish");
        await frame;
    }

    [Theory]
    [InlineData(3f, 80, 200, 40, 50)]     // mockup range: unchanged
    [InlineData(0.35f, 80, 200, 40, 200)] // mockup's own fallback at its widest zoom
    [InlineData(0.05f, 80, 200, 40, 2000)] // whole-body view: lines stay >= 80 px apart
    [InlineData(0.35f, 90, 100, 30, 100)] // scale bar at the mockup's widest zoom
    [InlineData(0.05f, 90, 100, 30, 2000)]
    public void Grid_and_scale_steps_follow_the_mockup_then_extend(float scale, float minPx, int last, float floor, int expected) =>
        Assert.Equal(expected, PlotRenderer.NiceStep(scale, minPx, last, floor));

    /// <summary>A selected ship just off the left edge keeps its rings where they cross the screen (doc 23 §4.12).</summary>
    [Fact]
    public void Rings_of_a_selected_ship_just_off_screen_are_still_drawn()
    {
        var scene = new PlotScene();
        var ddg = TestRig.Track(1, TrackKind.Destroyer, 0, 0, name: "Halyard");
        ddg.Rings = [new RangeRing(38, "SM-2 38 km"), new RangeRing(22, "ESSM 22 km"), new RangeRing(150, "Radar 150 km", true)];
        ddg.Selected = true;
        scene.Tracks.Add(ddg);
        // the ship sits 100 px left of the screen; at 3 px/km its 38 km ring reaches x = 14 px
        var vp = new Viewport((TestRig.W / 2f + 100f) / 3f, 0, 3, TestRig.W, TestRig.H);
        var st = new RenderState();
        st.Options.Grid = st.Options.ScaleBar = false;
        _rig.Render(scene, vp, st);
        Assert.Equal(0, st.LastFrame.TracksVisible);
        float ringX = vp.ToScreenX(0) + 38 * 3, ringY = vp.ToScreenY(0);
        Assert.True(Brightness((int)ringX, (int)ringY, 2) > 40, "the selected ship's SM-2 ring must cross the screen");
    }

    private float Brightness(int x, int y, int r)
    {
        var sea = _rig.Renderer.Theme.Sea;
        float best = 0;
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= TestRig.W || py >= TestRig.H) continue;
                var p = _rig.Pixel(px, py);
                best = MathF.Max(best, (p.Red - sea.Red) + (p.Green - sea.Green) + (p.Blue - sea.Blue));
            }
        return best;
    }
}
