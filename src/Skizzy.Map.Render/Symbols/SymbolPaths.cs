using SkiaSharp;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render.Symbols;

/// <summary>
/// Simplified MIL-STD-2525 frames at unit radius (r = 1, centred on the origin, screen y down), ported
/// from the mockup's <c>framePath()</c> and <c>drawMissile()</c>. Built once and drawn with a canvas
/// transform, so no path is built per frame.
/// <list type="bullet">
/// <item>Friend: circle (surface), dome open at the bottom (air), bowl open at the top (subsurface).</item>
/// <item>Hostile: diamond (surface), upper diamond with legs (air), lower diamond with legs (subsurface).</item>
/// <item>Unknown: quatrefoil (surface), upper half-quatrefoil (air and subsurface, as in the mockup).</item>
/// <item>Neutral: square (surface), open-bottom square (air), open-top square (subsurface); not in the mockup, per doc 11 §2.</item>
/// </list>
/// </summary>
public sealed class SymbolPaths : IDisposable
{
    private readonly SKPath[] _frames = new SKPath[4 * 3];

    public SymbolPaths()
    {
        foreach (Affiliation a in Enum.GetValues<Affiliation>())
            foreach (TrackDomain d in Enum.GetValues<TrackDomain>())
                _frames[Index(a, d)] = BuildFrame(a, d);
        Missile = BuildMissile();
        Diamond = BuildDiamond();
    }

    /// <summary>Unit frame for an affiliation and domain.</summary>
    public SKPath Frame(Affiliation a, TrackDomain d) => _frames[Index(a, d)];

    /// <summary>Missile arrowhead pointing along +X, length scale L = 1 (tip at 0.6, tail at -0.45).</summary>
    public SKPath Missile { get; }

    /// <summary>Unit diamond, used for hostile dots.</summary>
    public SKPath Diamond { get; }

    private static int Index(Affiliation a, TrackDomain d) => (int)a * 3 + (int)d;

    // Canvas arc(cx, cy, r, a0, a1, ccw) maps to ArcTo(oval, deg(a0), ±sweep). Canvas and Skia both measure angles clockwise in a y-down space.
    private static SKRect Oval(float cx, float cy, float r) => new(cx - r, cy - r, cx + r, cy + r);

    internal static SKPath BuildFrame(Affiliation a, TrackDomain d)
    {
        var p = new SKPath();
        const float r = 1f;
        switch (a)
        {
            case Affiliation.Friend:
                if (d == TrackDomain.Surface) p.AddCircle(0, 0, r);
                else if (d == TrackDomain.Air)
                {
                    // moveTo(-r, .4r); arc(0, .4r, r, PI, 0) - the dome above, open at the bottom
                    p.MoveTo(-r, r * .4f);
                    p.ArcTo(Oval(0, r * .4f, r), 180, 180, false);
                    p.Close();
                }
                else
                {
                    // moveTo(-r, -.4r); arc(0, -.4r, r, PI, 0, ccw) - the bowl below, open at the top
                    p.MoveTo(-r, -r * .4f);
                    p.ArcTo(Oval(0, -r * .4f, r), 180, -180, false);
                    p.Close();
                }
                break;

            case Affiliation.Hostile:
                if (d == TrackDomain.Surface)
                {
                    p.MoveTo(0, -r); p.LineTo(r, 0); p.LineTo(0, r); p.LineTo(-r, 0); p.Close();
                }
                else if (d == TrackDomain.Air)
                {
                    p.MoveTo(-r, r * .45f); p.LineTo(-r, 0); p.LineTo(0, -r); p.LineTo(r, 0); p.LineTo(r, r * .45f); p.Close();
                }
                else
                {
                    p.MoveTo(-r, -r * .45f); p.LineTo(-r, 0); p.LineTo(0, r); p.LineTo(r, 0); p.LineTo(r, -r * .45f); p.Close();
                }
                break;

            case Affiliation.Unknown:
            {
                const float q = r / 2;
                if (d == TrackDomain.Surface)
                {
                    // four lobes: top, right, bottom, left
                    p.ArcTo(Oval(0, -q, q), 180, 180, true);
                    p.ArcTo(Oval(q, 0, q), 270, 180, false);
                    p.ArcTo(Oval(0, q, q), 0, 180, false);
                    p.ArcTo(Oval(-q, 0, q), 90, 180, false);
                    p.Close();
                }
                else
                {
                    // translate(0, .3r): moveTo(-r,0); left quarter lobe, top lobe, right quarter lobe
                    const float ty = r * .3f;
                    p.MoveTo(-r, ty);
                    p.ArcTo(Oval(-q, ty, q), 180, 90, false);
                    p.ArcTo(Oval(0, ty - q, q), 180, 180, false);
                    p.ArcTo(Oval(q, ty, q), 270, 90, false);
                    p.Close();
                }
                break;
            }

            default: // Neutral
                if (d == TrackDomain.Surface) p.AddRect(new SKRect(-r * .85f, -r * .85f, r * .85f, r * .85f));
                else if (d == TrackDomain.Air)
                {
                    p.MoveTo(-r * .85f, r * .45f); p.LineTo(-r * .85f, -r * .85f); p.LineTo(r * .85f, -r * .85f); p.LineTo(r * .85f, r * .45f); p.Close();
                }
                else
                {
                    p.MoveTo(-r * .85f, -r * .45f); p.LineTo(-r * .85f, r * .85f); p.LineTo(r * .85f, r * .85f); p.LineTo(r * .85f, -r * .45f); p.Close();
                }
                break;
        }
        return p;
    }

    private static SKPath BuildMissile()
    {
        // L = 1, w = .42L: moveTo(.6L,0); lineTo(-.45L, w); lineTo(-.2L, 0); lineTo(-.45L, -w)
        var p = new SKPath();
        p.MoveTo(.6f, 0); p.LineTo(-.45f, .42f); p.LineTo(-.2f, 0); p.LineTo(-.45f, -.42f); p.Close();
        return p;
    }

    private static SKPath BuildDiamond()
    {
        var p = new SKPath();
        p.MoveTo(0, -1); p.LineTo(1, 0); p.LineTo(0, 1); p.LineTo(-1, 0); p.Close();
        return p;
    }

    public void Dispose()
    {
        foreach (var f in _frames) f.Dispose();
        Missile.Dispose();
        Diamond.Dispose();
    }
}
