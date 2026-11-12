using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Layout;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render.Tests;

/// <summary>
/// M2 missile picture on the plot: missiles stay headed arrowheads when the view spans 30+ km (the MissileMinPx floor),
/// the Missiles filter and the Missile trails toggle, and the red ring on a missile inbound to a friendly.
/// </summary>
public sealed class MissilePictureTests : IDisposable
{
    private readonly TestRig _rig = new();
    public void Dispose() => _rig.Dispose();

    /// <summary>A salvo of <paramref name="n"/> hostile missiles 300 m apart heading west at a friendly ship, with trails.</summary>
    private static PlotScene Salvo(int n, bool inbound = false)
    {
        var scene = new PlotScene();
        var ship = TestRig.Track(1, TrackKind.Destroyer, -20, 0, Affiliation.Friend, name: "Kirov");
        for (int k = 0; k < 20; k++) ship.Trail.Add(-20 + k * 0.05f - 1f, 0);
        scene.Tracks.Add(ship);
        for (int i = 0; i < n; i++)
        {
            var m = TestRig.Track(100 + i, TrackKind.AntiShipMissile, 10, (i - n / 2) * 0.3f, Affiliation.Hostile, 30, $"V{i:000}");
            m.HeadingDeg = 270; m.SpeedMps = 290; m.TargetId = 1; m.InboundToFriend = inbound;
            for (int k = 0; k < 12; k++) m.Trail.Add(10 + (12 - k) * 0.4f, m.Y);
            scene.Tracks.Add(m);
        }
        return scene;
    }

    private byte[] Frame(PlotScene scene, Viewport vp, Action<PlotOptions>? set = null, RenderState? st = null)
    {
        st ??= new RenderState { SnapNextFrame = true };
        set?.Invoke(st.Options);
        _rig.Render(scene, vp, st);
        using var img = _rig.Surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    private static int Diff(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) n++;
        return n;
    }

    [Fact]
    public void Missiles_stay_arrowheads_when_the_view_spans_more_than_30_km()
    {
        // 1920 px across 300 km (6.4 px/km) and 1920 km (1 px/km): a 40-missile salvo 300 m apart
        foreach (float scale in new[] { 6.4f, 1f })
        {
            var scene = Salvo(40);
            var st = new RenderState { SnapNextFrame = true };
            _rig.Render(scene, TestRig.View(0, 0, scale), st);
            for (int i = 0; i < 40; i++)
            {
                Assert.True(st.TryGetSymbol(100 + i, out float s, out var tier), $"missile {i} not drawn at {scale} px/km");
                Assert.Equal(SymbolTier.Missile, tier);
                Assert.True(s >= 6f - 1e-4f, $"missile {i}: {s} px at {scale} px/km");
            }
            Assert.Equal(40, st.LastFrame.Missiles);
        }

        // the mockup rule (floor off) would have made them dots: the floor is what keeps them readable
        var mock = Salvo(40);
        var ms = new RenderState { SnapNextFrame = true };
        ms.Options.MissileMinPx = 0;
        _rig.Render(mock, TestRig.View(0, 0, 1f), ms);
        Assert.True(ms.TryGetSymbol(120, out _, out var mtier));
        Assert.Equal(SymbolTier.Dot, mtier);
    }

    [Fact]
    public void Missiles_filter_hides_every_missile_and_keeps_the_ships()
    {
        var scene = Salvo(12, inbound: true);
        var vp = TestRig.View(0, 0, 20);
        var on = Frame(scene, vp);
        var st = new RenderState { SnapNextFrame = true };
        var off = Frame(scene, vp, o => o.Missiles = false, st);
        Assert.Equal(12, st.LastFrame.MissilesHidden);
        Assert.Equal(0, st.LastFrame.Missiles);
        Assert.Equal(0, st.LastFrame.InboundMarks);
        for (int i = 0; i < 12; i++) Assert.False(st.TryGetSymbol(100 + i, out _, out _));
        Assert.True(st.TryGetSymbol(1, out _, out _), "the ship stays");
        Assert.True(Diff(on, off) > 300, "hiding the missiles changed too little");
        Assert.True(Diff(on, Frame(scene, vp, o => o.Missiles = true)) == 0, "showing them again restores the frame");
    }

    [Fact]
    public void Missile_trails_toggle_removes_only_missile_trails()
    {
        var scene = Salvo(8);
        var vp = TestRig.View(0, 0, 20);
        var on = Frame(scene, vp);
        var off = Frame(scene, vp, o => o.MissileTrails = false);
        int changed = Diff(on, off);
        Assert.True(changed > 200, $"missile trails off changed only {changed} pixels");
        // the ship's trail (x -21..-20 km) is untouched: compare a band around it
        int sx0 = (int)vp.ToScreenX(-21.2f), sx1 = (int)vp.ToScreenX(-20.4f), sy = (int)vp.ToScreenY(0);
        for (int x = sx0; x < sx1; x++)
            for (int y = sy - 2; y <= sy + 2; y++)
            {
                int i = (y * TestRig.W + x) * 4;
                Assert.True(on[i] == off[i] && on[i + 1] == off[i + 1] && on[i + 2] == off[i + 2], $"ship trail pixel {x},{y} changed");
            }
        // Trails off hides the ship trail too, missile trails on or not
        var none = Frame(scene, vp, o => o.Trails = false);
        Assert.True(Diff(off, none) > 0);
    }

    [Fact]
    public void A_missile_inbound_to_a_friendly_gets_a_red_ring()
    {
        var theme = Theme.Default;
        var scene = Salvo(1, inbound: true);
        var vp = TestRig.View(10, 0, 40);
        var st = new RenderState { SnapNextFrame = true };
        var with = Frame(scene, vp, null, st);
        Assert.Equal(1, st.LastFrame.InboundMarks);
        Assert.True(st.TryGetSymbol(100, out float s, out _));
        float r = SymbolSizer.InboundRadius(s);
        // the ring's top: critical red
        int cx = (int)MathF.Round(vp.ToScreenX(10)), cy = (int)MathF.Round(vp.ToScreenY(0) - r);
        // anti-aliased: one of the pixels across the 1.5 px stroke carries the full colour
        Assert.True(RingAt(with, cx, cy, theme.Crit), $"no critical red ({theme.Crit}) ring pixel at {cx},{cy}±1: {Px(with, cx, cy)}");

        scene.Tracks[1].InboundToFriend = false;
        var st2 = new RenderState { SnapNextFrame = true };
        var without = Frame(scene, vp, null, st2);
        Assert.Equal(0, st2.LastFrame.InboundMarks);
        Assert.False(RingAt(without, cx, cy, theme.Crit));
    }

    [Fact]
    public void Missiles_are_coloured_by_affiliation()
    {
        var theme = Theme.Default;
        foreach (var aff in new[] { Affiliation.Hostile, Affiliation.Friend })
        {
            var scene = new PlotScene();
            var m = TestRig.Track(5, TrackKind.AntiShipMissile, 0, 0, aff, 30);
            m.HeadingDeg = 90; m.SpeedMps = 290;
            scene.Tracks.Add(m);
            var vp = TestRig.View(0, 0, 60);
            var px = Frame(scene, vp);
            var col = theme.ForAffiliation(aff);
            int cx = (int)vp.ToScreenX(0), cy = (int)vp.ToScreenY(0);
            bool found = false;
            for (int dx = -8; dx <= 8 && !found; dx++)
                for (int dy = -8; dy <= 8 && !found; dy++)
                    found = Near(Px(px, cx + dx, cy + dy), col);
            Assert.True(found, $"no {aff} coloured pixel at the missile");
        }
    }

    private static bool RingAt(byte[] b, int x, int y, SKColor c)
    {
        for (int d = -1; d <= 1; d++) if (Near(Px(b, x, y + d), c)) return true;
        return false;
    }

    private static SKColor Px(byte[] b, int x, int y)
    {
        int i = (y * TestRig.W + x) * 4;
        return new SKColor(b[i + 2], b[i + 1], b[i], b[i + 3]);
    }

    private static bool Near(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) < 90;
}
