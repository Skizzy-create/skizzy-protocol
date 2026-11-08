using System.Numerics;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;
using Skizzy.Map.RenderDemo;

namespace Skizzy.Map.Render.Tests;

public sealed class LabelAndOrderTests : IDisposable
{
    private readonly TestRig _rig = new();
    public void Dispose() => _rig.Dispose();

    [Fact]
    public void Labels_never_overlap_each_other_or_cover_a_symbol()
    {
        int scenes = 0;
        foreach (var (scene, vp) in SizingTests.Scenes())
        {
            var st = new RenderState { SnapNextFrame = true };
            _rig.Render(scene, vp, st);
            var labels = st.LastLabelBoxes.ToArray();
            var symbols = st.LastSymbolBoxes.ToArray();
            Assert.True(labels.Length > 5, "expected labels to be placed");
            for (int i = 0; i < labels.Length; i++)
            {
                for (int j = i + 1; j < labels.Length; j++)
                    Assert.False(labels[i].Intersects(labels[j]), $"labels {labels[i]} and {labels[j]} overlap");
                foreach (var s in symbols)
                    Assert.False(labels[i].Intersects(s), $"label {labels[i]} covers symbol {s}");
            }
            scenes++;
        }
        Assert.Equal(6, scenes);
    }

    [Fact]
    public void Crowded_labels_are_culled_not_stacked()
    {
        // 30 fighters 300 m apart in a line: every symbol must still be drawn, most labels culled
        var scene = new PlotScene();
        for (int i = 0; i < 30; i++) scene.Tracks.Add(TestRig.Track(i + 1, TrackKind.Fighter, i * 0.3f, 0, alt: 5000, name: $"Viper {i + 1}"));
        var st = new RenderState();
        _rig.Render(scene, TestRig.View(4.5f, 0, 60), st);
        var f = st.LastFrame;
        Assert.Equal(30, f.TracksVisible);
        Assert.True(f.LabelsCulled > 0);
        Assert.Equal(f.LabelsPlaced, st.LastLabelBoxes.Length);
    }

    [Fact]
    public void Missile_labels_need_zoom_or_selection_and_dots_get_no_label()
    {
        var scene = new PlotScene();
        var msl = TestRig.Track(1, TrackKind.AntiShipMissile, 0, 0, Affiliation.Hostile, 15, "V001");
        msl.HeadingDeg = 270; msl.SpeedMps = 290;
        scene.Tracks.Add(msl);
        var st = new RenderState();
        _rig.Render(scene, TestRig.View(0, 0, 5), st);
        Assert.Equal(0, st.LastFrame.LabelsPlaced);
        _rig.Render(scene, TestRig.View(0, 0, 10), st);
        Assert.Equal(1, st.LastFrame.LabelsPlaced);
        msl.Selected = true;
        _rig.Render(scene, TestRig.View(0, 0, 5), st);
        Assert.Equal(1, st.LastFrame.LabelsPlaced);
    }

    [Theory]
    [InlineData(1, 0.75f)]
    [InlineData(6, 0.75f)]
    [InlineData(7, 0.28f)]
    [InlineData(20, 0.28f)]
    public void Order_lines_fade_above_six_active_orders(int orders, float expectedAlpha)
    {
        var (scene, vp) = OrderScene(orders);
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        Assert.Equal(orders, st.LastFrame.ActiveOrders);
        Assert.Equal(expectedAlpha, st.LastFrame.OrderLineAlpha, 3);
    }

    [Fact]
    public void Faded_order_lines_are_visibly_dimmer_on_the_plot()
    {
        float six = LinePixelBrightness(6), seven = LinePixelBrightness(7);
        Assert.True(six > 0 && seven > 0, "the order line must be drawn");
        Assert.True(seven < six * 0.6f, $"7 orders should draw dimmer lines than 6 (6: {six}, 7: {seven})");
    }

    [Fact]
    public void Hidden_units_do_not_count_as_active_orders()
    {
        var (scene, vp) = OrderScene(8);
        scene.Tracks[0].Visible = false;
        scene.Tracks[1].Visible = false;
        var st = new RenderState();
        _rig.Render(scene, vp, st);
        Assert.Equal(6, st.LastFrame.ActiveOrders);
        Assert.Equal(0.75f, st.LastFrame.OrderLineAlpha, 3);
    }

    private float LinePixelBrightness(int orders)
    {
        var (scene, vp) = OrderScene(orders);
        var st = new RenderState();
        st.Options.Grid = st.Options.Labels = st.Options.Trails = st.Options.ScaleBar = false;
        _rig.Render(scene, vp, st);
        // first unit at (0, 0) moves east to (40, 0): sample the brightest pixel in the first dash, 30 px out
        float sx = vp.ToScreenX(0) + 30, sy = vp.ToScreenY(0);
        var sea = _rig.Renderer.Theme.Sea;
        float best = 0;
        for (int dx = 0; dx < 9; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var p = _rig.Pixel((int)sx + dx, (int)MathF.Round(sy) + dy);
                best = MathF.Max(best, (p.Red - sea.Red) + (p.Green - sea.Green) + (p.Blue - sea.Blue));
            }
        return best;
    }

    private static (PlotScene, Viewport) OrderScene(int orders)
    {
        var scene = new PlotScene();
        for (int i = 0; i < orders; i++)
        {
            var t = TestRig.Track(i + 1, TrackKind.Fighter, 0, -i * 6f, alt: 5000);
            t.Control = ControlState.Order;
            scene.Tracks.Add(t);
            scene.Orders.Add(new PlotOrder { TrackId = t.Id, Type = OrderType.Move, Destination = new Vector2(40, -i * 6f) });
        }
        return (scene, TestRig.View(20, -40, 8));
    }
}
