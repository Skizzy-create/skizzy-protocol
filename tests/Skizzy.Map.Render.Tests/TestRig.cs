using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render.Tests;

/// <summary>Shared fixtures: a renderer, a raster surface and small scene builders.</summary>
internal sealed class TestRig : IDisposable
{
    public const int W = 1920, H = 1080;

    public PlotRenderer Renderer { get; } = new();
    public SKSurface Surface { get; } = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));

    public void Render(PlotScene scene, Viewport vp, RenderState state) => Renderer.Render(Surface.Canvas, scene, vp, state);

    public SKColor Pixel(int x, int y)
    {
        using var img = Surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    public static PlotTrack Track(int id, TrackKind kind, float x, float y, Affiliation aff = Affiliation.Friend, float alt = 0, string? name = null)
    {
        var t = new PlotTrack(id, kind)
        {
            X = x, Y = y, AltitudeM = alt, Affiliation = aff,
            Side = aff == Affiliation.Friend ? Side.Blue : Side.Red,
            Name = name ?? $"T{id}",
        };
        return t;
    }

    public static Viewport View(float cx, float cy, float scale) => new(cx, cy, scale, W, H);

    public void Dispose()
    {
        Surface.Dispose();
        Renderer.Dispose();
    }
}
