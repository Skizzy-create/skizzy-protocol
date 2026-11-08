using System.Diagnostics;
using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.RenderDemo;

/// <summary>Layer-by-layer timing of the renderer (skizzy-render-demo bench).</summary>
internal static class Bench
{
    public static int Run()
    {
        using var renderer = new PlotRenderer();
        var scene = SampleScene.Build();
        var big = PerfScene.Build(2000);
        var empty = new PlotScene();
        var st0 = new RenderState();
        var o = st0.Options; o.Grid = o.Zones = o.Rings = o.Trails = o.Labels = o.Orders = o.Events = o.ScaleBar = false;
        Console.WriteLine($"empty scene, sea fill only: {Time(renderer, empty, new Viewport(0, 0, 3, 1920, 1080), st0),6:0.00} ms");
        using (var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) surface.Canvas.Clear(SKColors.Black);
            Console.WriteLine($"raw canvas.Clear 1920x1080: {sw.Elapsed.TotalMilliseconds / 200,6:0.00} ms");
            var sizer = Stopwatch.StartNew();
        }
        Measure(renderer, "sample theatre", scene, scene.View);
        Measure(renderer, "sample close", scene, new Viewport(3, 0, 34f, 1920, 1080));
        Measure(renderer, "2000 tracks", big, big.View);
        return 0;
    }

    private static void Measure(PlotRenderer r, string name, PlotScene scene, Viewport vp)
    {
        Console.WriteLine($"== {name}");
        foreach (var (label, apply) in Variants())
        {
            var st = new RenderState();
            apply(st.Options);
            double ms = Time(r, scene, vp, st);
            var t = st.LastTimings; var fs = st.LastFrame;
            if (label == "all layers") Console.WriteLine($"  [visible {fs.TracksVisible} full {fs.FullSymbols} frame {fs.Frames} dot {fs.Dots} msl {fs.Missiles} labels {fs.LabelsPlaced}]");
            Console.WriteLine($"  {label,-14} {ms,6:0.00} ms | bg {t.Background,5:0.00} layout {t.Layout,5:0.00} rings {t.Rings,5:0.00} trails {t.Trails,5:0.00} orders {t.Orders,5:0.00} tracks {t.Tracks,5:0.00} labels {t.Labels,5:0.00} ovl {t.Overlay,5:0.00}");
        }
    }

    public static double Time(PlotRenderer r, PlotScene scene, Viewport vp, RenderState st, int frames = 60)
    {
        using var surface = SKSurface.Create(new SKImageInfo(vp.Width, vp.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < 1500) r.Render(surface.Canvas, scene, vp, st);
        var samples = new double[frames];
        var sw = new Stopwatch();
        for (int i = 0; i < frames; i++)
        {
            sw.Restart();
            r.Render(surface.Canvas, scene, vp, st);
            surface.Flush();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[frames / 2];
    }

    private static IEnumerable<(string, Action<PlotOptions>)> Variants()
    {
        yield return ("all layers", _ => { });
        yield return ("no grid", o => o.Grid = false);
        yield return ("no zones", o => o.Zones = false);
        yield return ("no rings", o => o.Rings = false);
        yield return ("no trails", o => o.Trails = false);
        yield return ("no labels", o => o.Labels = false);
        yield return ("no orders", o => o.Orders = false);
        yield return ("tracks only", o => { o.Grid = o.Zones = o.Rings = o.Trails = o.Labels = o.Orders = o.Events = o.ScaleBar = false; });
    }
}
