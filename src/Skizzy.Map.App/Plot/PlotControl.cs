using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;

namespace Skizzy.Map.App.Plot;

/// <summary>
/// Hosts the SkiaSharp <see cref="Skizzy.Map.Render.PlotRenderer"/> inside Avalonia: every frame the control emits a
/// custom draw operation that leases Avalonia's own Skia canvas (<see cref="ISkiaSharpApiLeaseFeature"/>) on the
/// render thread, so the plot is drawn straight into the window's GPU surface with no copy.
/// </summary>
public class PlotControl : Avalonia.Controls.Control
{
    public static readonly StyledProperty<PlotSurface?> SurfaceProperty =
        AvaloniaProperty.Register<PlotControl, PlotSurface?>(nameof(Surface));

    static PlotControl()
    {
        AffectsRender<PlotControl>(SurfaceProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PlotControl>(true);
        FocusableProperty.OverrideDefaultValue<PlotControl>(true);
    }

    public PlotSurface? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var s = Surface;
        if (s is null) return;
        context.Custom(new DrawOp(new Rect(Bounds.Size), s));
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly PlotSurface _surface;

        public DrawOp(Rect bounds, PlotSurface surface)
        {
            Bounds = bounds;
            _surface = surface;
        }

        public Rect Bounds { get; }

        public void Dispose() { }

        // never equal: the plot changes every frame
        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                _surface.NoSkia = true;
                return;
            }
            using var lease = feature.Lease();
            _surface.NoSkia = false;
            _surface.Draw(lease.SkCanvas, (float)Bounds.Width, (float)Bounds.Height);
        }
    }
}
