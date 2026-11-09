using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Views;

/// <summary>
/// The mockup's symbol frames (framePath, drawMissile) as Avalonia geometry, for the legend, the unit panel's
/// symbol and list swatches. The plot itself draws with the render core.
/// </summary>
public static class Glyphs
{
    public static Geometry Frame(Affiliation aff, TrackDomain domain, double r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        const double k = 0.5522847498; // circle by beziers
        switch (aff)
        {
            case Affiliation.Friend:
                if (domain == TrackDomain.Surface) Circle(c, 0, 0, r);
                else if (domain == TrackDomain.Air)
                {
                    // half circle on top, flat bottom at +0.4r (canvas y down)
                    c.BeginFigure(new Point(-r, r * .4), true);
                    c.CubicBezierTo(new Point(-r, r * .4 - r * k), new Point(-r * k, r * .4 - r), new Point(0, r * .4 - r));
                    c.CubicBezierTo(new Point(r * k, r * .4 - r), new Point(r, r * .4 - r * k), new Point(r, r * .4));
                    c.EndFigure(true);
                }
                else
                {
                    c.BeginFigure(new Point(-r, -r * .4), true);
                    c.CubicBezierTo(new Point(-r, -r * .4 + r * k), new Point(-r * k, -r * .4 + r), new Point(0, -r * .4 + r));
                    c.CubicBezierTo(new Point(r * k, -r * .4 + r), new Point(r, -r * .4 + r * k), new Point(r, -r * .4));
                    c.EndFigure(true);
                }
                break;
            case Affiliation.Hostile:
                if (domain == TrackDomain.Surface) Poly(c, (0, -r), (r, 0), (0, r), (-r, 0));
                else if (domain == TrackDomain.Air) Poly(c, (-r, r * .45), (-r, 0), (0, -r), (r, 0), (r, r * .45));
                else Poly(c, (-r, -r * .45), (-r, 0), (0, r), (r, 0), (r, -r * .45));
                break;
            default:
            {
                double q = r / 2;
                if (domain == TrackDomain.Surface)
                {
                    // quatrefoil: four half-circles
                    c.BeginFigure(new Point(-q, -q), true);
                    c.ArcTo(new Point(q, -q), new Size(q, q), 0, false, SweepDirection.Clockwise);
                    c.ArcTo(new Point(q, q), new Size(q, q), 0, false, SweepDirection.Clockwise);
                    c.ArcTo(new Point(-q, q), new Size(q, q), 0, false, SweepDirection.Clockwise);
                    c.ArcTo(new Point(-q, -q), new Size(q, q), 0, false, SweepDirection.Clockwise);
                    c.EndFigure(true);
                }
                else
                {
                    double oy = r * .3 * (domain == TrackDomain.Subsurface ? -1 : 1);
                    double s = domain == TrackDomain.Subsurface ? -1 : 1;
                    c.BeginFigure(new Point(-r, oy), true);
                    c.ArcTo(new Point(-q, oy - q * s), new Size(q, q), 0, false, s > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                    c.ArcTo(new Point(q, oy - q * s), new Size(q, q), 0, false, s > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                    c.ArcTo(new Point(r, oy), new Size(q, q), 0, false, s > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                    c.EndFigure(true);
                }
                break;
            }
        }
        return g;
    }

    private static void Circle(StreamGeometryContext c, double cx, double cy, double r)
    {
        c.BeginFigure(new Point(cx - r, cy), true);
        c.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.ArcTo(new Point(cx - r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.EndFigure(true);
    }

    private static void Poly(StreamGeometryContext c, params (double X, double Y)[] pts)
    {
        c.BeginFigure(new Point(pts[0].X, pts[0].Y), true);
        for (int i = 1; i < pts.Length; i++) c.LineTo(new Point(pts[i].X, pts[i].Y));
        c.EndFigure(true);
    }

    /// <summary>The missile arrow pointing +X (rotate for heading).</summary>
    public static Geometry Missile(double len)
    {
        double w = len * .42;
        var g = new StreamGeometry();
        using var c = g.Open();
        Poly(c, (len * .6, 0), (-len * .45, w), (-len * .2, 0), (-len * .45, -w));
        return g;
    }

    public static string BrushKey(Affiliation a) => a switch
    {
        Affiliation.Friend => "FriendBrush",
        Affiliation.Hostile => "HostileBrush",
        Affiliation.Neutral => "NeutralBrush",
        _ => "UnknownBrush",
    };

    public static Color ColorOf(Control c, Affiliation a) =>
        c.TryFindResource(BrushKey(a).Replace("Brush", "Color", StringComparison.Ordinal), c.ActualThemeVariant, out var v) && v is Color col ? col : Colors.Gray;

    public static string GlyphText(TrackKind kind) => KindInfo.Of(kind).Glyph;
}

/// <summary>A track's symbol at panel size (the mockup's symCanvas, 40 px).</summary>
public sealed class SymbolView : ThemedControl
{
    private TrackInfo? _track;
    private Affiliation _aff;

    public SymbolView() { Width = 40; Height = 40; }

    public void Show(TrackInfo? t, Affiliation aff)
    {
        _track = t;
        _aff = aff;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var t = _track;
        if (t is null) return;
        var col = Glyphs.ColorOf(this, _aff);
        var stroke = new Pen(new SolidColorBrush(col), 1.6);
        using (ctx.PushTransform(Matrix.CreateTranslation(20, 20)))
        {
            if (t.IsMissile)
            {
                var m = Glyphs.Missile(20);
                ctx.DrawGeometry(t.Side == Side.Red ? new SolidColorBrush(col) : null, t.Side == Side.Red ? null : stroke, m);
                return;
            }
            ctx.DrawGeometry(new SolidColorBrush(col, .16), stroke, Glyphs.Frame(_aff, t.Domain, 15));
            var glyph = Glyphs.GlyphText(t.Kind);
            if (string.IsNullOrEmpty(glyph)) return;
            var face = this.TryFindResource("FontUi", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
            var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(face, FontStyle.Normal, FontWeight.SemiBold), glyph.Length > 2 ? 9 : 12, new SolidColorBrush(col));
            double dy = t.Domain == TrackDomain.Air ? 2 : t.Domain == TrackDomain.Subsurface ? -2 : 0;
            ctx.DrawText(ft, new Point(-ft.Width / 2, dy - ft.Height / 2));
        }
    }
}

/// <summary>The plot legend (the mockup's 208 x 70 symbol key).</summary>
public sealed class Legend : ThemedControl
{
    public Legend() { Width = 208; Height = 64; }

    public override void Render(DrawingContext ctx)
    {
        var face = this.TryFindResource("FontUi", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        var dim = Ui.Brush(this, "TextDimBrush");
        var rows = new[] { (Affiliation.Friend, "Friend"), (Affiliation.Hostile, "Hostile"), (Affiliation.Unknown, "Unknown") };
        var doms = new[] { TrackDomain.Air, TrackDomain.Surface, TrackDomain.Subsurface };
        for (int row = 0; row < rows.Length; row++)
        {
            var (aff, name) = rows[row];
            double y = 10 + row * 22;
            var ft = new FormattedText(name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(face), 10.5, dim);
            ctx.DrawText(ft, new Point(0, y - ft.Height / 2));
            var col = Glyphs.ColorOf(this, aff);
            var pen = new Pen(new SolidColorBrush(col), 1.3);
            for (int i = 0; i < doms.Length; i++)
                using (ctx.PushTransform(Matrix.CreateTranslation(70 + i * 24, y)))
                    ctx.DrawGeometry(new SolidColorBrush(col, .15), pen, Glyphs.Frame(aff, doms[i], 7));
            using (ctx.PushTransform(Matrix.CreateTranslation(150, y)))
            {
                var m = Glyphs.Missile(9);
                if (aff == Affiliation.Friend) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(col), 1.1), m);
                else ctx.DrawGeometry(new SolidColorBrush(col), null, m);
            }
            ctx.DrawEllipse(new SolidColorBrush(col), null, new Point(176, y), 2.4, 2.4);
        }
    }
}

/// <summary>Event ticks above the replay scrubber: impacts (crit), kills (text), intercepts (ok, short).</summary>
public sealed class TimelineTicks : ThemedControl
{
    public MapData? Data { get; set; }
    public double T0 { get; set; }
    public double T1 { get; set; } = 1;

    public override void Render(DrawingContext ctx)
    {
        var d = Data;
        if (d is null || T1 <= T0) return;
        double w = Bounds.Width;
        var crit = Ui.Brush(this, "CritBrush");
        var text = Ui.Brush(this, "TextBrush");
        var ok = Glyphs.ColorOf(this, Affiliation.Neutral);
        var okB = this.TryFindResource("OkColor", ActualThemeVariant, out var oc) && oc is Color okc ? new SolidColorBrush(okc, .6) : new SolidColorBrush(ok, .6);
        foreach (var e in d.Events)
        {
            if (e.Kind is not (PlotEventKind.Impact or PlotEventKind.Kill or PlotEventKind.Intercept)) continue;
            if (e.Time < T0 || e.Time > T1) continue;
            double x = (e.Time - T0) / (T1 - T0) * w;
            var b = e.Kind == PlotEventKind.Impact ? crit : e.Kind == PlotEventKind.Kill ? text : okB;
            ctx.FillRectangle(b, new Rect(x, 0, 1.5, e.Kind == PlotEventKind.Intercept ? 3 : 6));
        }
    }
}
