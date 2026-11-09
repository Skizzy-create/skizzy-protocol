using SkiaSharp;

namespace Skizzy.Map.Render.Symbols;

/// <summary>
/// Builds dashed geometry directly into a path, culled to the visible rectangle. Skia's dash path effect
/// dashes the whole circumference on every draw (a 150 km radar ring at close zoom is ~5,000 dashes) and
/// cannot be batched across shapes; this emits only visible dashes, and many shapes share one stroke.
/// Dashes start at angle 0 (east) and run clockwise on screen, like canvas <c>setLineDash</c> on <c>arc()</c>.
/// </summary>
internal static class Dasher
{
    /// <summary>Above this many dashes per circle the visible arc is found first instead of scanning every dash.</summary>
    private const int ScanLimit = 512;

    public static void Circle(SegmentBatch path, float cx, float cy, float r, float on, float off, in SKRect clip)
    {
        if (r <= 0) return;
        // reject circles that do not cross the visible rectangle at all
        float nearX = Math.Clamp(cx, clip.Left, clip.Right), nearY = Math.Clamp(cy, clip.Top, clip.Bottom);
        float nd = Hyp(nearX - cx, nearY - cy);
        float fd = MathF.Max(Hyp(clip.Left - cx, clip.Top - cy), MathF.Max(Hyp(clip.Right - cx, clip.Top - cy),
                   MathF.Max(Hyp(clip.Left - cx, clip.Bottom - cy), Hyp(clip.Right - cx, clip.Bottom - cy))));
        if (nd > r + 1 || fd < r - 1) return;

        float period = on + off;
        float circ = 2 * MathF.PI * r;
        int n = Math.Max(1, (int)(circ / period));
        float step = 2 * MathF.PI / n;           // stretch the pattern slightly so it closes cleanly
        float onA = step * on / period;

        int k0 = 0, k1 = n;
        if (n > ScanLimit)
        {
            // only the arc that can be on screen: the angle span of the clip rectangle seen from the centre
            if (!VisibleArc(cx, cy, clip, out float a0, out float a1)) { a0 = 0; a1 = 2 * MathF.PI; }
            k0 = (int)MathF.Floor(a0 / step) - 1;
            k1 = (int)MathF.Ceiling(a1 / step) + 1;
        }
        var grow = SKRect.Inflate(clip, 2, 2);
        for (int k = k0; k < k1; k++)
        {
            float a = k * step, b = a + onA;
            float x0 = cx + r * MathF.Cos(a), y0 = cy + r * MathF.Sin(a);
            float x1 = cx + r * MathF.Cos(b), y1 = cy + r * MathF.Sin(b);
            if ((x0 < grow.Left && x1 < grow.Left) || (x0 > grow.Right && x1 > grow.Right) ||
                (y0 < grow.Top && y1 < grow.Top) || (y0 > grow.Bottom && y1 > grow.Bottom)) continue;
            if (on > 2.5f)
            {
                // longer dashes follow the curve
                float m = (a + b) / 2, mx = cx + r * MathF.Cos(m), my = cy + r * MathF.Sin(m);
                path.Add(x0, y0, mx, my);
                path.Add(mx, my, x1, y1);
            }
            else path.Add(x0, y0, x1, y1);
        }
    }

    /// <summary>Dashed segment, clipped to <paramref name="clip"/> first; the pattern keeps its phase from the start point.</summary>
    public static void Line(SegmentBatch path, float x0, float y0, float x1, float y1, float on, float off, in SKRect clip)
    {
        float dx = x1 - x0, dy = y1 - y0, len = Hyp(dx, dy);
        if (len < 0.01f) return;
        var g = SKRect.Inflate(clip, 4, 4);
        // Liang-Barsky on the distance parameter t in [0, len]
        float t0 = 0, t1 = len, ux = dx / len, uy = dy / len;
        if (!ClipT(-ux, x0 - g.Left, ref t0, ref t1) || !ClipT(ux, g.Right - x0, ref t0, ref t1) ||
            !ClipT(-uy, y0 - g.Top, ref t0, ref t1) || !ClipT(uy, g.Bottom - y0, ref t0, ref t1)) return;
        float period = on + off;
        for (float d = MathF.Floor(t0 / period) * period; d < t1; d += period)
        {
            float s = MathF.Max(d, t0), e = MathF.Min(t1, d + on);
            if (e <= s) continue;
            path.Add(x0 + ux * s, y0 + uy * s, x0 + ux * e, y0 + uy * e);
        }
    }

    private static bool ClipT(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        float r = q / p;
        if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    /// <summary>Angular interval [a0, a1] (radians, a1 may exceed 2π) covering the rectangle as seen from (cx, cy).</summary>
    private static bool VisibleArc(float cx, float cy, in SKRect r, out float a0, out float a1)
    {
        a0 = a1 = 0;
        if (cx >= r.Left && cx <= r.Right && cy >= r.Top && cy <= r.Bottom) return false; // centre on screen: full circle
        Span<float> ang = stackalloc float[4];
        ang[0] = MathF.Atan2(r.Top - cy, r.Left - cx);
        ang[1] = MathF.Atan2(r.Top - cy, r.Right - cx);
        ang[2] = MathF.Atan2(r.Bottom - cy, r.Left - cx);
        ang[3] = MathF.Atan2(r.Bottom - cy, r.Right - cx);
        // the centre is outside, so the corners span less than PI; unwrap around the first corner
        float refA = ang[0], lo = 0, hi = 0;
        for (int i = 1; i < 4; i++)
        {
            float d = ang[i] - refA;
            while (d > MathF.PI) d -= 2 * MathF.PI;
            while (d < -MathF.PI) d += 2 * MathF.PI;
            lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
        }
        a0 = refA + lo; a1 = refA + hi;
        while (a0 < 0) { a0 += 2 * MathF.PI; a1 += 2 * MathF.PI; }
        return true;
    }

    private static float Hyp(float x, float y) => MathF.Sqrt(x * x + y * y);
}
