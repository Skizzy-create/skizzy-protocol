using System.Numerics;
using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Layout;
using Skizzy.Map.Render.Model;
using Skizzy.Map.RenderDemo;

namespace Skizzy.Map.Render.Tests;

/// <summary>VIP markers, racetracks, RTB / MARSHAL, defend and hunt lines, and detection states (design/23 §6 learnings 13–15).</summary>
public sealed class OverlayTests : IDisposable
{
    private readonly TestRig _rig = new();
    public void Dispose() => _rig.Dispose();

    private static PlotTrack Carrier(int id, float x, float y, float hdg = 90, string? name = null)
    {
        var t = TestRig.Track(id, TrackKind.Carrier, x, y, name: name);
        t.HeadingDeg = hdg;
        return t;
    }

    private static PlotTrack Fighter(int id, float x, float y, Affiliation aff = Affiliation.Friend) =>
        TestRig.Track(id, TrackKind.Fighter, x, y, aff, alt: 5000);

    private static RenderState Plain()
    {
        var st = new RenderState();
        var o = st.Options; o.Grid = o.ScaleBar = o.Labels = o.Rings = o.Trails = false;
        return st;
    }

    // ------------------------------------------------------------------ VIP

    [Fact]
    public void A_vip_gets_brackets_and_a_ring_and_the_tag_only_while_selected()
    {
        var scene = new PlotScene();
        var a = Carrier(1, -30, 0); a.Vip = true; a.Selected = true;
        var b = Carrier(2, 30, 0); b.Vip = true;
        scene.Tracks.Add(a); scene.Tracks.Add(b);
        scene.Tracks.Add(Carrier(3, 0, 25)); // not a VIP
        var st = Plain();
        var vp = TestRig.View(0, 0, 6);
        _rig.Render(scene, vp, st);
        var f = st.LastFrame;
        Assert.Equal(2, f.VipMarkers);
        Assert.Equal(2, f.VipRings);
        Assert.Equal(1, f.VipTags);

        // the selected VIP's protection ring reads brighter (alpha .45 against .16); sample where the rings do not overlap
        float R = 40 * vp.Scale;
        float ya = vp.ToScreenY(0);
        int axLeft = (int)(vp.ToScreenX(-30) - R), bxRight = (int)(vp.ToScreenX(30) + R);
        float sel = MaxBrightness(axLeft, (int)ya, 3), unsel = MaxBrightness(bxRight, (int)ya, 3);
        Assert.True(sel > 1.8f * unsel && unsel > 5, $"selected ring {sel}, unselected ring {unsel}");

        // the tag is part of the symbol footprints, so labels keep clear of it
        Assert.Equal(f.TracksVisible + 1, st.LastSymbolBoxes.Length);
    }

    [Fact]
    public void The_ring_of_a_vip_just_off_screen_still_crosses_the_view()
    {
        var scene = new PlotScene();
        var cv = Carrier(1, 0, 0); cv.Vip = true;
        scene.Tracks.Add(cv);
        // 100 px left of the screen at 3 px/km: the 40 km ring (120 px) reaches x = 20
        var vp = new Viewport((TestRig.W / 2f + 100f) / 3f, 0, 3, TestRig.W, TestRig.H);
        var st = Plain();
        _rig.Render(scene, vp, st);
        Assert.Equal(0, st.LastFrame.VipMarkers);
        Assert.Equal(1, st.LastFrame.VipRings);
        Assert.True(MaxBrightness(20, (int)vp.ToScreenY(0), 2) > 5, "the ring must be visible where it crosses the screen");
    }

    [Fact]
    public void Labels_clear_the_vip_brackets()
    {
        var scene = new PlotScene();
        var cv = Carrier(1, 0, 0, name: "Kestrel"); cv.Vip = true;
        scene.Tracks.Add(cv);
        var st = new RenderState();
        var vp = TestRig.View(0, 0, 6);
        _rig.Render(scene, vp, st);
        Assert.True(st.TryGetSymbol(1, out float s, out _));
        var label = Assert.Single(st.LastLabelBoxes.ToArray());
        float r = SymbolSizer.VipBracketRadius(s);
        Assert.True(label.X >= vp.ToScreenX(0) + r * .9f, $"label at {label.X} overlaps the brackets (radius {r})");
    }

    // ------------------------------------------------------------------ racetrack

    [Fact]
    public void Racetrack_points_match_the_mockup_trackPts()
    {
        // trackPts([0,0],[20,0],3) in the mockup
        var rt = new PlotRacetrack { Id = 1, A = new Vector2(0, 0), B = new Vector2(20, 0), WidthKm = 6 };
        Assert.Equal(44, PlotRenderer.RacetrackPoints);
        AssertPoint((0, 3), PlotRenderer.RacetrackPoint(rt, 0));
        AssertPoint((10, 3), PlotRenderer.RacetrackPoint(rt, 5));
        AssertPoint((20, 3), PlotRenderer.RacetrackPoint(rt, 10));
        AssertPoint((20 + 3 * MathF.Cos(75 * MathF.PI / 180), 3 * MathF.Sin(75 * MathF.PI / 180)), PlotRenderer.RacetrackPoint(rt, 11));
        AssertPoint((20, -3), PlotRenderer.RacetrackPoint(rt, 22));
        AssertPoint((0, -3), PlotRenderer.RacetrackPoint(rt, 32));
        AssertPoint((3 * MathF.Cos(255 * MathF.PI / 180), 3 * MathF.Sin(255 * MathF.PI / 180)), PlotRenderer.RacetrackPoint(rt, 33));

        // right-hand turns run clockwise (negative area with y up); TurnRight = false mirrors the pattern
        Assert.True(SignedArea(rt) < 0);
        rt.TurnRight = false;
        Assert.True(SignedArea(rt) > 0);
        AssertPoint((0, -3), PlotRenderer.RacetrackPoint(rt, 0));
    }

    [Fact]
    public void A_shared_racetrack_is_drawn_once_with_no_per_unit_lines()
    {
        var scene = new PlotScene();
        scene.Racetracks.Add(new PlotRacetrack { Id = 7, A = new Vector2(-20, 0), B = new Vector2(20, 0) });
        scene.Racetracks.Add(new PlotRacetrack { Id = 8, A = new Vector2(-20, 30), B = new Vector2(20, 30) });
        for (int i = 0; i < 4; i++)
        {
            scene.Tracks.Add(Fighter(i + 1, -10 + i * 6, 3));
            scene.Orders.Add(new PlotOrder { TrackId = i + 1, Type = OrderType.Racetrack, PatternId = i < 3 ? 7 : 8 });
        }
        scene.Tracks.Add(Fighter(5, 0, -20));
        scene.Orders.Add(new PlotOrder { TrackId = 5, Type = OrderType.Racetrack, PatternId = 99 }); // unknown pattern: nothing
        var st = Plain();
        var vp = TestRig.View(0, 10, 12);
        _rig.Render(scene, vp, st);
        var f = st.LastFrame;
        Assert.Equal(2, f.Racetracks);
        Assert.Equal(0, f.OrderLines);
        Assert.Equal(5, f.ActiveOrders);
        // the far end of pattern 7 (round A, 3 km out on its axis) is drawn
        Assert.True(MaxBrightness((int)vp.ToScreenX(-23), (int)vp.ToScreenY(0), 2) > 40);
    }

    // ------------------------------------------------------------------ RTB / MARSHAL

    [Fact]
    public void Rtb_draws_one_marshal_ring_per_base_and_lines_only_while_inbound()
    {
        var scene = new PlotScene();
        var cv = Carrier(1, 0, 0, hdg: 90); // heading east: the marshal point is 8 km west
        var cv2 = Carrier(2, 0, -40, hdg: 0); // heading north: 8 km south
        scene.Tracks.Add(cv); scene.Tracks.Add(cv2);
        RtbPhase[] phases = [RtbPhase.Inbound, RtbPhase.Inbound, RtbPhase.Marshal, RtbPhase.Approach];
        for (int i = 0; i < 4; i++)
        {
            scene.Tracks.Add(Fighter(10 + i, 30 + i * 3, 10));
            scene.Orders.Add(new PlotOrder { TrackId = 10 + i, Type = OrderType.Rtb, TargetTrackId = 1, RtbPhase = phases[i] });
        }
        scene.Tracks.Add(Fighter(20, 20, -40));
        scene.Orders.Add(new PlotOrder { TrackId = 20, Type = OrderType.Rtb, TargetTrackId = 2, RtbPhase = RtbPhase.Marshal });

        var (mx, my) = PlotRenderer.MarshalPoint(cv, 8);
        Assert.Equal(-8f, mx, 3); Assert.Equal(0f, my, 3);
        var (mx2, my2) = PlotRenderer.MarshalPoint(cv2, 8);
        Assert.Equal(0f, mx2, 3); Assert.Equal(-48f, my2, 3);

        var st = Plain();
        var vp = TestRig.View(0, -20, 10);
        _rig.Render(scene, vp, st);
        var f = st.LastFrame;
        Assert.Equal(2, f.MarshalRings);
        Assert.Equal(2, f.OrderLines); // only the two inbound aircraft
        Assert.Equal(5, f.ActiveOrders);
        float R = PlotRenderer.MarshalRadius(vp.Scale);
        Assert.Equal(35f, R);
        // the dashed ring round the marshal point (top of the ring, away from the inbound lines)
        Assert.True(MaxBrightness((int)vp.ToScreenX(-8), (int)(vp.ToScreenY(0) - R), 3) > 20, "the MARSHAL ring");
    }

    [Fact]
    public void A_marshal_ring_needs_its_base()
    {
        var scene = new PlotScene();
        var cv = Carrier(1, 0, 0);
        cv.Visible = false; // sunk
        scene.Tracks.Add(cv);
        scene.Tracks.Add(Fighter(2, 20, 0));
        scene.Orders.Add(new PlotOrder { TrackId = 2, Type = OrderType.Rtb, TargetTrackId = 1 });
        var st = Plain();
        _rig.Render(scene, TestRig.View(0, 0, 10), st);
        Assert.Equal(0, st.LastFrame.MarshalRings);
        Assert.Equal(0, st.LastFrame.OrderLines);
        Assert.Equal(1, st.LastFrame.ActiveOrders);
    }

    // ------------------------------------------------------------------ defend / hunt and the fade

    [Fact]
    public void Defend_and_hunt_point_at_their_unit_and_a_hunt_with_no_target_draws_nothing()
    {
        var scene = new PlotScene();
        scene.Tracks.Add(Carrier(1, 0, 0));
        scene.Tracks.Add(Fighter(2, 20, 10));
        scene.Tracks.Add(Fighter(3, 20, -10));
        scene.Tracks.Add(Fighter(4, 20, -20));
        scene.Tracks.Add(Fighter(9, 60, -10, Affiliation.Hostile));
        scene.Orders.Add(new PlotOrder { TrackId = 2, Type = OrderType.Defend, TargetTrackId = 1 });
        scene.Orders.Add(new PlotOrder { TrackId = 3, Type = OrderType.Hunt, TargetTrackId = 9 });
        scene.Orders.Add(new PlotOrder { TrackId = 4, Type = OrderType.Hunt, TargetTrackId = 0 });
        var st = Plain();
        var vp = TestRig.View(30, 0, 12);
        _rig.Render(scene, vp, st);
        Assert.Equal(3, st.LastFrame.ActiveOrders);
        Assert.Equal(2, st.LastFrame.OrderLines);
        Assert.Equal(.75f, st.LastFrame.OrderLineAlpha);
        // the hunt line runs along y = -10 from x 20 to 60: sample its middle
        Assert.True(MaxBrightness((int)vp.ToScreenX(40), (int)vp.ToScreenY(-10), 2) > 40, "the hunt line");
    }

    [Fact]
    public void Every_order_kind_counts_towards_the_fade()
    {
        var scene = new PlotScene();
        scene.Tracks.Add(Carrier(1, 0, 0));
        scene.Racetracks.Add(new PlotRacetrack { Id = 1, A = new Vector2(-20, 30), B = new Vector2(20, 30) });
        OrderType[] kinds = [OrderType.Racetrack, OrderType.Racetrack, OrderType.Rtb, OrderType.Rtb, OrderType.Hunt, OrderType.Defend];
        for (int i = 0; i < kinds.Length; i++)
        {
            scene.Tracks.Add(Fighter(10 + i, 10 + i * 4, 10));
            scene.Orders.Add(new PlotOrder
            {
                TrackId = 10 + i, Type = kinds[i], PatternId = 1, TargetTrackId = kinds[i] == OrderType.Hunt ? 0 : 1,
                RtbPhase = RtbPhase.Marshal,
            });
        }
        var st = Plain();
        _rig.Render(scene, TestRig.View(0, 0, 10), st);
        Assert.Equal(6, st.LastFrame.ActiveOrders);
        Assert.Equal(.75f, st.LastFrame.OrderLineAlpha);
        scene.Tracks.Add(Fighter(30, -20, -10));
        scene.Orders.Add(new PlotOrder { TrackId = 30, Type = OrderType.Hunt });
        _rig.Render(scene, TestRig.View(0, 0, 10), st);
        Assert.Equal(7, st.LastFrame.ActiveOrders);
        Assert.Equal(.28f, st.LastFrame.OrderLineAlpha);
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void An_undetected_contact_is_not_drawn_hit_measured_or_pointed_at()
    {
        var scene = new PlotScene();
        var own = Fighter(1, 0, 0);
        var stealth = Fighter(2, 2f, 0, Affiliation.Unknown);
        stealth.Detection = TrackDetection.None;
        scene.Tracks.Add(own); scene.Tracks.Add(stealth);
        scene.Orders.Add(new PlotOrder { TrackId = 1, Type = OrderType.Hunt, TargetTrackId = 2 });
        var st = Plain();
        var vp = TestRig.View(0, 0, 10);
        _rig.Render(scene, vp, st);
        var f = st.LastFrame;
        Assert.Equal(1, f.TracksVisible);
        Assert.Equal(1, f.Undetected);
        Assert.Equal(0, f.OrderLines);
        Assert.False(st.TryGetSymbol(2, out _, out _));
        Assert.Equal(0, st.HitTest(vp.ToScreenX(2f), vp.ToScreenY(0)));

        // it does not shrink its neighbour either (20 px away it would, as a peer)
        Assert.True(st.TryGetSymbol(1, out float alone, out _));
        stealth.Detection = TrackDetection.Shared;
        var st2 = Plain();
        _rig.Render(scene, vp, st2);
        Assert.True(st2.TryGetSymbol(1, out float crowded, out _));
        Assert.True(crowded < alone, $"control: a detected peer 20 px away shrinks it ({crowded} vs {alone})");
    }

    [Fact]
    public void An_own_sensor_contact_is_drawn_with_a_dashed_halo()
    {
        var scene = new PlotScene();
        var t = Fighter(1, 0, 0, Affiliation.Unknown);
        t.Detection = TrackDetection.OwnSensor;
        scene.Tracks.Add(t);
        var st = Plain();
        var vp = TestRig.View(0, 0, 10);
        _rig.Render(scene, vp, st);
        Assert.Equal(1, st.LastFrame.TracksVisible);
        Assert.Equal(1, st.LastFrame.OwnSensorMarks);
        Assert.True(st.TryGetSymbol(1, out float s, out _));
        float r = SymbolSizer.OwnSensorRadius(s);
        float cx = vp.ToScreenX(0), cy = vp.ToScreenY(0);
        // somewhere on the halo (dashed, so look along a short arc to the lower left, clear of the symbol and its stalk)
        float best = 0;
        for (int d = 200; d <= 250; d += 5)
        {
            float a = d * MathF.PI / 180f;
            best = MathF.Max(best, MaxBrightness((int)(cx + MathF.Cos(a) * r), (int)(cy - MathF.Sin(a) * r), 0));
        }
        Assert.True(best > 40, $"halo brightness {best}");

        t.Detection = TrackDetection.Shared;
        _rig.Render(scene, vp, st);
        Assert.Equal(0, st.LastFrame.OwnSensorMarks);
    }

    // ------------------------------------------------------------------ cost

    [Fact]
    public void Overlay_frames_do_not_allocate()
    {
        var scene = SampleScene.BuildVipsAndOrders();
        var st = new RenderState();
        for (int i = 0; i < 5; i++) _rig.Render(scene, scene.View, st);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) _rig.Render(scene, scene.View, st);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var f = st.LastFrame;
        Assert.True(f.Racetracks == 1 && f.MarshalRings == 1 && f.VipMarkers == 2 && f.VipTags == 1 && f.OwnSensorMarks == 1 && f.Undetected == 1,
            $"rt {f.Racetracks} marshal {f.MarshalRings} vip {f.VipMarkers} tags {f.VipTags} own {f.OwnSensorMarks} undetected {f.Undetected}");
        Assert.Equal(7, f.OrderLines); // 2 RTB inbound, 2 defend, 3 hunt
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertPoint((float X, float Y) expected, (float X, float Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
    }

    private static float SignedArea(PlotRacetrack rt)
    {
        float a = 0;
        for (int k = 0; k < PlotRenderer.RacetrackPoints; k++)
        {
            var p = PlotRenderer.RacetrackPoint(rt, k);
            var q = PlotRenderer.RacetrackPoint(rt, (k + 1) % PlotRenderer.RacetrackPoints);
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2;
    }

    private float MaxBrightness(int x, int y, int r)
    {
        var sea = _rig.Renderer.Theme.Sea;
        using var img = _rig.Surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        float best = 0;
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= TestRig.W || py >= TestRig.H) continue;
                var p = bmp.GetPixel(px, py);
                best = MathF.Max(best, (p.Red - sea.Red) + (p.Green - sea.Green) + (p.Blue - sea.Blue));
            }
        return best;
    }
}
