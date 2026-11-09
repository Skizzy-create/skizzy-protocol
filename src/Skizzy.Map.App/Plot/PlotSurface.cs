using System.Diagnostics;
using SkiaSharp;
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

    /// <summary>CPU time of the last draw in ms, measured on the render thread, lock wait excluded (diagnostics).</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>How long the last draw waited for <see cref="Gate"/> (the UI thread syncing the scene), ms.</summary>
    public double LastWaitMs { get; private set; }

    /// <summary>Frames drawn so far (diagnostics; the perf gate counts these).</summary>
    public long FramesDrawn { get; private set; }

    /// <summary>Set when the last draw found no Skia lease (a non-Skia backend).</summary>
    public bool NoSkia { get; internal set; }

    /// <summary>
    /// When the window draws on the GPU, render the plot into a CPU raster bitmap and draw that (default on).
    /// The plot is thousands of small anti-aliased paths, sprites and text runs: Skia's CPU rasteriser draws
    /// that several times faster than its GPU backend, and one bitmap upload is cheap (docs/M3-spikes.md §2).
    /// </summary>
    public static bool RasterOnGpu { get; set; } = true;

    /// <summary>True when the last frame went through the raster bitmap.</summary>
    public bool LastWasRaster { get; private set; }

    private SKSurface? _raster;

    /// <summary>Draws the plot onto the window's canvas (render thread), through a raster bitmap when on the GPU.</summary>
    internal void DrawTo(SKCanvas canvas, bool gpu, float width, float height)
    {
        if (!(gpu && RasterOnGpu))
        {
            LastWasRaster = false;
            Draw(canvas, width, height);
            return;
        }
        var m = canvas.TotalMatrix;
        float sx = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY), sy = MathF.Sqrt(m.ScaleY * m.ScaleY + m.SkewX * m.SkewX);
        if (!(sx > 0) || !(sy > 0)) { sx = sy = 1; }
        int pw = Math.Max(1, (int)MathF.Ceiling(width * sx)), ph = Math.Max(1, (int)MathF.Ceiling(height * sy));
        lock (Gate)
        {
            if (_disposed) return;
            if (_raster is null || _raster.Canvas.DeviceClipBounds.Width != pw || _raster.Canvas.DeviceClipBounds.Height != ph)
            {
                _raster?.Dispose();
                _raster = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul));
            }
        }
        var rs = _raster;
        if (rs is null) { Draw(canvas, width, height); return; }
        var rc = rs.Canvas;
        rc.ResetMatrix();
        rc.Scale(sx, sy);
        Draw(rc, width, height);
        using var img = rs.Snapshot();
        canvas.DrawImage(img, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        LastWasRaster = true;
    }

    internal void Draw(SkiaSharp.SKCanvas canvas, float width, float height)
    {
        long t0 = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            long t1 = Stopwatch.GetTimestamp();
            LastWaitMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            t0 = t1;
            var r = Renderer;
            if (r is null) return;
            var vp = View with { Width = (int)MathF.Ceiling(width), Height = (int)MathF.Ceiling(height) };
            View = vp;
            r.Render(canvas, Scene, vp, State);
            FramesDrawn++;
        }
        LastDrawMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    private bool _disposed;

    public void Dispose()
    {
        lock (Gate)
        {
            _disposed = true;
            _raster?.Dispose();
            _raster = null;
        }
        State.Dispose();
    }
}
