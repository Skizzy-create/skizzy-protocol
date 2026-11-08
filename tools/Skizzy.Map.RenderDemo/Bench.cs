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
        // moving: every track shifts a little each frame (as in the perf test)
        {
            var st = new RenderState();
            using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            var warm = Stopwatch.StartNew(); int f = 0;
            while (warm.ElapsedMilliseconds < 2000) { Move(big, f++); renderer.Render(surface.Canvas, big, big.View, st); }
            var ms = new double[200]; var sw = new Stopwatch();
            for (int i = 0; i < 200; i++) { Move(big, f++); sw.Restart(); renderer.Render(surface.Canvas, big, big.View, st); ms[i] = sw.Elapsed.TotalMilliseconds; }
            Array.Sort(ms);
            var t = st.LastTimings;
            Console.WriteLine($"== 2000 moving: median {ms[100]:0.00} min {ms[0]:0.00} p95 {ms[190]:0.00} | layout {t.Layout:0.00} trails {t.Trails:0.00} tracks {t.Tracks:0.00} labels {t.Labels:0.00} | sprites {renderer.SpriteCount}");
        }
        return 0;
    }

    /// <summary>
    /// skizzy-render-demo perf: load-tolerant numbers for the review. Every figure is taken over several
    /// batches of moving frames; "best" is the lowest batch median (the cost on an idle core), "median" and
    /// "p95" are over every frame (what a loaded machine sees).
    /// </summary>
    public static int Perf()
    {
        using var renderer = new PlotRenderer();
        foreach (int n in new[] { 2000, 2500 })
        {
            var scene = PerfScene.Build(n);
            var r = PerfStats.Measure(renderer, scene, scene.View, new RenderState(), batches: 7, frames: 60);
            Console.WriteLine($"{n} tracks moving @1920x1080: {r}");
        }

        // zoom animation: the scale sweeps x4 in and back over 240 frames, so sizes and sprite variants change every frame
        {
            var scene = PerfScene.Build(2000);
            var st = new RenderState();
            using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            var v0 = scene.View;
            Viewport At(int f) => v0 with { Scale = v0.Scale * MathF.Pow(4f, 0.5f - 0.5f * MathF.Cos(f * MathF.PI / 120f)) };
            for (int f = 0; f < 240; f++) renderer.Render(surface.Canvas, scene, At(f), st);
            var ms = new double[480];
            var sw = new Stopwatch();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < ms.Length; f++)
            {
                sw.Restart();
                renderer.Render(surface.Canvas, scene, At(f + 3), st);
                surface.Flush();
                ms[f] = sw.Elapsed.TotalMilliseconds;
            }
            long alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / ms.Length;
            Array.Sort(ms);
            Console.WriteLine($"2000 tracks, zoom sweep x4: median {ms[ms.Length / 2]:0.00} ms, p95 {ms[(int)(ms.Length * .95)]:0.00} ms, max {ms[^1]:0.00} ms, " +
                              $"{alloc} B/frame, sprites {renderer.SpriteCount}");
        }

        // two map windows at different DPI (1.0 and 1.5) sharing one renderer, frames alternating
        {
            var scene = PerfScene.Build(2000);
            var stA = new RenderState();
            var stB = new RenderState();
            using var a = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var b = SKSurface.Create(new SKImageInfo(2880, 1620, SKColorType.Bgra8888, SKAlphaType.Premul));
            void Frame()
            {
                renderer.Render(a.Canvas, scene, scene.View, stA);
                b.Canvas.Save(); b.Canvas.Scale(1.5f);
                renderer.Render(b.Canvas, scene, scene.View, stB);
                b.Canvas.Restore();
            }
            for (int f = 0; f < 30; f++) Frame();
            var ms = new double[120];
            var sw = new Stopwatch();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < ms.Length; f++) { sw.Restart(); Frame(); ms[f] = sw.Elapsed.TotalMilliseconds; }
            long alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / ms.Length;
            Array.Sort(ms);
            Console.WriteLine($"2000 tracks, two windows at DPI 1.0 + 1.5, one renderer: median {ms[60]:0.00} ms per pair, p95 {ms[114]:0.00} ms, {alloc} B/pair");

            // the 1.5x window alone, for comparison (sharing must cost nothing beyond the two frames)
            for (int f = 0; f < ms.Length; f++)
            {
                sw.Restart();
                b.Canvas.Save(); b.Canvas.Scale(1.5f);
                renderer.Render(b.Canvas, scene, scene.View, stB);
                b.Canvas.Restore();
                b.Flush();
                ms[f] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ms);
            var t = stB.LastTimings;
            Console.WriteLine($"  the 1.5x window alone: median {ms[60]:0.00} ms | bg {t.Background:0.00} layout {t.Layout:0.00} rings {t.Rings:0.00} " +
                              $"trails {t.Trails:0.00} orders {t.Orders:0.00} tracks {t.Tracks:0.00} labels {t.Labels:0.00} ovl {t.Overlay:0.00}");
        }
        TrailBench();
        return 0;
    }

    /// <summary>
    /// The trails of 2,000 tracks (20 points each, two halves) drawn three ways, path building included:
    /// two strokes per trail (the mockup), one path per colour and half (the renderer), and independent segments
    /// through DrawPoints(Lines) per colour and half.
    /// </summary>
    private static void TrailBench()
    {
        var scene = PerfScene.Build(2000);
        var vp = scene.View;
        var pts = new SKPoint[scene.Tracks.Count][];
        var aff = new int[scene.Tracks.Count];
        for (int i = 0; i < pts.Length; i++)
        {
            var t = scene.Tracks[i];
            aff[i] = (int)t.Affiliation;
            // the renderer's 3 px screen-space decimation, so the point counts match what it strokes
            var kept = new List<SKPoint>();
            for (int k = 0; k <= t.Trail.Count; k++)
            {
                var q = k < t.Trail.Count ? new SKPoint(vp.ToScreenX(t.Trail[k].X), vp.ToScreenY(t.Trail[k].Y)) : new SKPoint(vp.ToScreenX(t.X), vp.ToScreenY(t.Y));
                if (kept.Count > 0 && k < t.Trail.Count && SKPoint.DistanceSquared(kept[^1], q) < 9f) continue;
                kept.Add(q);
            }
            if (kept.Count < 2) kept.Add(kept[0]);
            pts[i] = kept.ToArray();
        }
        var colours = new[] { SKColor.Parse("#7fd1ff"), SKColor.Parse("#ff6b5e"), SKColor.Parse("#ffd166"), SKColor.Parse("#9be39b") };
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
        using var one = new SKPath();
        var batched = Enumerable.Range(0, 8).Select(_ => new SKPath()).ToArray();
        var seg = Enumerable.Range(0, 8).Select(_ => new List<SKPoint>(16384)).ToArray();
        var segArr = new SKPoint[8][];

        void PerTrail(SKCanvas c)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i]; int mid = p.Length / 2;
                for (int half = 0; half < 2; half++)
                {
                    one.Reset();
                    int from = half == 0 ? 0 : mid, to = half == 0 ? mid : p.Length - 1;
                    one.MoveTo(p[from]);
                    for (int k = from + 1; k <= to; k++) one.LineTo(p[k]);
                    paint.Color = colours[aff[i]].WithAlpha(half == 0 ? (byte)31 : (byte)102);
                    c.DrawPath(one, paint);
                }
            }
        }
        void Batched(SKCanvas c)
        {
            foreach (var b in batched) b.Reset();
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i]; int mid = p.Length / 2;
                var o = batched[aff[i] * 2]; var n = batched[aff[i] * 2 + 1];
                o.MoveTo(p[0]); for (int k = 1; k <= mid; k++) o.LineTo(p[k]);
                n.MoveTo(p[mid]); for (int k = mid + 1; k < p.Length; k++) n.LineTo(p[k]);
            }
            for (int b = 0; b < 8; b++)
            {
                if (batched[b].IsEmpty) continue;
                paint.Color = colours[b / 2].WithAlpha(b % 2 == 0 ? (byte)31 : (byte)102);
                c.DrawPath(batched[b], paint);
            }
        }
        void Segments(SKCanvas c)
        {
            foreach (var s in seg) s.Clear();
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i]; int mid = p.Length / 2;
                for (int k = 1; k < p.Length; k++) { var s = seg[aff[i] * 2 + (k <= mid ? 0 : 1)]; s.Add(p[k - 1]); s.Add(p[k]); }
            }
            for (int b = 0; b < 8; b++)
            {
                if (seg[b].Count == 0) continue;
                // the renderer's SegmentBatch pools fixed chunks; a sized array per batch is the same work for Skia
                if (segArr[b] is null || segArr[b].Length != seg[b].Count) segArr[b] = new SKPoint[seg[b].Count];
                seg[b].CopyTo(segArr[b]);
                paint.Color = colours[b / 2].WithAlpha(b % 2 == 0 ? (byte)31 : (byte)102);
                c.DrawPoints(SKPointMode.Lines, segArr[b], paint);
            }
        }
        double Time(Action<SKCanvas> draw)
        {
            for (int i = 0; i < 40; i++) { surface.Canvas.Clear(SKColors.Black); draw(surface.Canvas); }
            var ms = new double[120];
            var sw = new Stopwatch();
            for (int i = 0; i < ms.Length; i++)
            {
                surface.Canvas.Clear(SKColors.Black);
                sw.Restart(); draw(surface.Canvas); surface.Flush();
                ms[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ms);
            return ms[ms.Length / 2];
        }
        // interleave the three twice so a change in machine load hits them alike
        double a = Time(PerTrail), b2 = Time(Batched), s2 = Time(Segments);
        a = Math.Min(a, Time(PerTrail)); b2 = Math.Min(b2, Time(Batched)); s2 = Math.Min(s2, Time(Segments));
        Console.WriteLine($"trails of 2000 tracks (hairline): per-trail strokes {a:0.00} ms, batched paths {b2:0.00} ms, DrawPoints segments {s2:0.00} ms");
        foreach (var p in batched) p.Dispose();
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

    private static void Move(PlotScene s, int f)
    {
        foreach (var t in s.Tracks) t.X += (f % 2 == 0 ? 1 : -1) * 0.01f;
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
