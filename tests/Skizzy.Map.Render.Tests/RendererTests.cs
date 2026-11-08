using System.Diagnostics;
using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;
using Skizzy.Map.Render.Symbols;
using Skizzy.Map.Render.Text;
using Skizzy.Map.RenderDemo;
using Xunit.Abstractions;

namespace Skizzy.Map.Render.Tests;

public sealed class RendererTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly ITestOutputHelper _out;
    public RendererTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _rig.Dispose();

    /// <summary>Steady-state frames should not allocate managed memory (the target is zero; this records the actual number).</summary>
    [Fact]
    public void Steady_state_frames_do_not_allocate()
    {
        var scene = SampleScene.Build();
        var st = new RenderState();
        for (int i = 0; i < 30; i++) _rig.Render(scene, scene.View, st);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) _rig.Render(scene, scene.View, st);
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        _out.WriteLine($"managed allocation per steady frame: {perFrame} bytes");
        Assert.Equal(0, perFrame);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var scene = SampleScene.Build();
        using var a = Snapshot(scene, scene.View);
        using var b = Snapshot(scene, scene.View);
        Assert.True(a.Bytes.AsSpan().SequenceEqual(b.Bytes));
    }

    [Fact]
    public void Sprites_and_vector_fallback_draw_the_same_picture_closely()
    {
        var scene = SampleScene.Build();
        var vp = TestRig.View(3, 0, 34);
        using var sprites = Snapshot(scene, vp);
        _rig.Renderer.UseSprites = false;
        using var vector = Snapshot(scene, vp);
        _rig.Renderer.UseSprites = true;
        // sprites snap to whole pixels, so allow small per-pixel differences; the pictures must match overall
        long diff = 0;
        var sa = sprites.Bytes; var va = vector.Bytes;
        for (int i = 0; i < sa.Length; i++) diff += Math.Abs(sa[i] - va[i]);
        double mean = diff / (double)sa.Length;
        _out.WriteLine($"mean abs channel difference sprite vs vector: {mean:0.000}");
        Assert.True(mean < 0.5, $"sprite rendering diverges from vector rendering (mean {mean})");
    }

    [Fact]
    public void Bundled_fonts_are_embedded_and_used()
    {
        var names = FontSet.BundledResourceNames().ToList();
        Assert.Equal(9, names.Count);
        Assert.False(FontSet.Default.UsingFallback);
        Assert.Contains("IBM Plex Mono", FontSet.Default.DataRegular.FamilyName);
        Assert.Contains("Saira", FontSet.Default.DisplaySemiBold.FamilyName);
        Assert.Contains("IBM Plex Sans Condensed", FontSet.Default.UiSemiBold.FamilyName);
    }

    [Fact]
    public void Hit_test_finds_the_track_under_the_cursor()
    {
        var scene = SampleScene.Build();
        var vp = TestRig.View(3, 0, 34);
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        Assert.Equal(1, st.HitTest(vp.ToScreenX(0) + 3, vp.ToScreenY(0) - 2));
        Assert.Equal(0, st.HitTest(5, 5));
    }

    [Fact]
    public void Theme_tokens_match_doc_23()
    {
        var t = Theme.Default;
        Assert.Equal(SKColor.Parse("#081519"), t.Sea);
        Assert.Equal(SKColor.Parse("#62c6f2"), t.Friend);
        Assert.Equal(SKColor.Parse("#ff6a57"), t.Hostile);
        Assert.Equal(SKColor.Parse("#e9c84f"), t.Unknown);
        Assert.Equal(SKColor.Parse("#7cd68e"), t.Neutral);
        Assert.Equal(SKColor.Parse("#b5a3ff"), t.Accent);
        Assert.Equal(SKColor.Parse("#10242b"), t.Grid);
        Assert.Equal(SKColor.Parse("#173540"), t.GridHi);
    }

    [Theory]
    [InlineData(Affiliation.Friend, TrackDomain.Surface, -1, -1, 1, 1)]
    [InlineData(Affiliation.Friend, TrackDomain.Air, -1, -0.6, 1, 0.4)]
    [InlineData(Affiliation.Friend, TrackDomain.Subsurface, -1, -0.4, 1, 0.6)]
    [InlineData(Affiliation.Hostile, TrackDomain.Surface, -1, -1, 1, 1)]
    [InlineData(Affiliation.Hostile, TrackDomain.Air, -1, -1, 1, 0.45)]
    [InlineData(Affiliation.Hostile, TrackDomain.Subsurface, -1, -0.45, 1, 1)]
    [InlineData(Affiliation.Unknown, TrackDomain.Surface, -1, -1, 1, 1)]
    [InlineData(Affiliation.Unknown, TrackDomain.Air, -1, -0.7, 1, 0.3)]
    public void Frame_geometry_matches_the_mockup(Affiliation a, TrackDomain d, float l, float t, float r, float b)
    {
        using var p = SymbolPaths.BuildFrame(a, d);
        var bounds = p.TightBounds;
        Assert.Equal(l, bounds.Left, 2);
        Assert.Equal(t, bounds.Top, 2);
        Assert.Equal(r, bounds.Right, 2);
        Assert.Equal(b, bounds.Bottom, 2);
    }

    [Fact]
    public void Dashed_rings_emit_only_the_visible_arc()
    {
        var batch = new SegmentBatch();
        var clip = new SKRect(0, 0, 1920, 1080);
        // a 150 km radar ring at 34 px/km: ~5,100 px radius, centre on screen; only dashes inside the screen are emitted
        Dasher.Circle(batch, 960, 540, 5100, 1, 5, clip);
        Assert.True(batch.IsEmpty, "a ring that encloses the whole screen without crossing it draws nothing");
        Dasher.Circle(batch, -4000, 540, 4500, 1, 5, clip);
        var count = CountSegments(batch);
        Assert.InRange(count, 50, 600); // the arc across the screen, not the 4,700 dashes of the full ring
    }

    private static int CountSegments(SegmentBatch b)
    {
        var f = typeof(SegmentBatch).GetField("_count", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (int)f.GetValue(b)! / 2;
    }

    private sealed class Pixels(byte[] bytes) : IDisposable
    {
        public byte[] Bytes { get; } = bytes;
        public void Dispose() { }
    }

    private Pixels Snapshot(PlotScene scene, Viewport vp)
    {
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        using var img = _rig.Surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return new Pixels(bmp.Bytes);
    }
}
