using System.Diagnostics;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Plot;

/// <summary>
/// What one plot control draws: a scene, the per-window render state and the viewport. The UI thread mutates the
/// scene (data sync, selection, filters) and the render thread draws it, so both sides hold <see cref="Gate"/>.
/// </summary>
public sealed class PlotSurface : IDisposable
{
    /// <summary>Held while the scene is mutated (UI thread) or drawn (render thread).</summary>
    public object Gate { get; } = new();

    public PlotScene Scene { get; set; } = new();

    public RenderState State { get; } = new();

    /// <summary>Last viewport used for drawing (logical pixels of the control).</summary>
    public Viewport View { get; set; } = new(0, 0, 3, 1280, 720);

    /// <summary>The renderer that draws this surface (shared per theme).</summary>
    public PlotRenderer? Renderer { get; set; }

    /// <summary>CPU time of the last draw in ms, measured on the render thread (diagnostics).</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>Frames drawn so far (diagnostics; the perf gate counts these).</summary>
    public long FramesDrawn { get; private set; }

    /// <summary>Set when the last draw found no Skia lease (a non-Skia backend).</summary>
    public bool NoSkia { get; internal set; }

    internal void Draw(SkiaSharp.SKCanvas canvas, float width, float height)
    {
        long t0 = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            var r = Renderer;
            if (r is null) return;
            var vp = View with { Width = (int)MathF.Ceiling(width), Height = (int)MathF.Ceiling(height) };
            View = vp;
            r.Render(canvas, Scene, vp, State);
            FramesDrawn++;
        }
        LastDrawMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    public void Dispose() => State.Dispose();
}
