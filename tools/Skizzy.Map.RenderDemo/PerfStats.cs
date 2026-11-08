using System.Diagnostics;
using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.RenderDemo;

/// <summary>
/// Frame-time statistics that hold up on a loaded machine. Frames are measured in batches after a warm-up;
/// background load only ever adds time, so the lowest batch median estimates the renderer's own cost, while the
/// all-frame median and p95 show what a loaded machine sees. Shared by the perf tests and the demo's perf command.
/// </summary>
public readonly record struct PerfStats(double BestBatchMedianMs, double MedianMs, double P95Ms, double MinMs, long AllocBytesPerFrame, int Visible)
{
    public override string ToString() =>
        $"best batch median {BestBatchMedianMs:0.00} ms, all-frame median {MedianMs:0.00} ms, p95 {P95Ms:0.00} ms, min {MinMs:0.00} ms, " +
        $"{AllocBytesPerFrame} B/frame, visible {Visible}";

    /// <summary>
    /// Render <paramref name="batches"/> x <paramref name="frames"/> frames of a scene whose tracks move a little every
    /// frame (so sizes, labels and sprites re-evaluate), after a warm-up of <paramref name="warmupMs"/>.
    /// </summary>
    public static PerfStats Measure(PlotRenderer renderer, PlotScene scene, Viewport vp, RenderState st,
        int batches = 7, int frames = 60, int warmupMs = 1500)
    {
        using var surface = SKSurface.Create(new SKImageInfo(vp.Width, vp.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        int f = 0;
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < warmupMs) { Move(scene, f++); renderer.Render(surface.Canvas, scene, vp, st); surface.Flush(); }

        var all = new double[batches * frames];
        var batch = new double[frames];
        double best = double.MaxValue;
        var sw = new Stopwatch();
        long allocated = 0;
        for (int b = 0; b < batches; b++)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < frames; i++)
            {
                Move(scene, f++);
                sw.Restart();
                renderer.Render(surface.Canvas, scene, vp, st);
                surface.Flush();
                batch[i] = sw.Elapsed.TotalMilliseconds;
            }
            allocated += GC.GetAllocatedBytesForCurrentThread() - a0;
            Array.Copy(batch, 0, all, b * frames, frames);
            Array.Sort(batch);
            best = Math.Min(best, batch[frames / 2]);
        }
        Array.Sort(all);
        return new PerfStats(best, all[all.Length / 2], all[(int)(all.Length * 0.95)], all[0],
            allocated / all.Length, st.LastFrame.TracksVisible);
    }

    /// <summary>Shift every track 10 m east or west, alternating, so the picture is never a pure repeat.</summary>
    public static void Move(PlotScene scene, int frame)
    {
        float dx = (frame % 2 == 0 ? 1 : -1) * 0.01f;
        foreach (var t in scene.Tracks) t.X += dx;
    }
}
