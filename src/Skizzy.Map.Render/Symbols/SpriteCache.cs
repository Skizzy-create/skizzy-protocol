using SkiaSharp;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render.Symbols;

/// <summary>A pre-rendered symbol and the pixel in it that sits on the track position.</summary>
internal sealed class Sprite
{
    public Sprite(SKImage image, int originX, int originY) { Image = image; OriginX = originX; OriginY = originY; }
    public SKImage Image { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public long Bytes => 4L * Image.Width * Image.Height;
}

/// <summary>
/// Raster cache of symbols in device pixels. On a CPU canvas one small anti-aliased vector shape costs
/// ~8 µs, a blit of the same shape ~1 µs, so every frame, icon, dot, missile arrowhead and badge is drawn
/// once per variant and blitted afterwards. Sizes are quantised to 0.5 px, missile headings to 64 directions;
/// positions snap to whole device pixels. Variants are created lazily.
/// <para>
/// One store per device scale (up to <see cref="MaxScales"/>), so map windows on monitors with different DPI can
/// share a renderer without flushing each other's sprites every frame. Each store keeps two generations: lookups
/// promote from the old generation to the new one, and when the new one passes half of <see cref="MaxBytesPerScale"/>
/// (or <see cref="MaxEntriesPerGeneration"/>) the old generation is disposed and the new one takes its place. Memory
/// stays bounded, and a zoom that creates many short-lived sizes only drops what has not been used since the last
/// turn-over instead of flushing everything at once.
/// </para>
/// </summary>
internal sealed class SpriteCache : IDisposable
{
    public const int MaxScales = 4;
    public const long MaxBytesPerScale = 16L << 20;
    public const int MaxEntriesPerGeneration = 4096;
    public const int MissileDirections = 64;

    private sealed class Store
    {
        public float Scale;
        public long LastUse;
        public Dictionary<long, Sprite> Hot = new(), Cold = new();
        public long HotBytes, ColdBytes;

        public void Dispose()
        {
            foreach (var s in Hot.Values) s.Image.Dispose();
            foreach (var s in Cold.Values) s.Image.Dispose();
            Hot.Clear(); Cold.Clear();
            HotBytes = ColdBytes = 0;
        }
    }

    private readonly Theme _theme;
    private readonly SymbolPaths _paths;
    private readonly SKTypeface _glyphFace, _badgeFace;
    private readonly List<Store> _stores = new();
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private Store _cur;
    private long _tick;

    public SpriteCache(Theme theme, SymbolPaths paths, SKTypeface glyphFace, SKTypeface badgeFace)
    {
        _theme = theme; _paths = paths; _glyphFace = glyphFace; _badgeFace = badgeFace;
        _cur = new Store { Scale = 1f };
        _stores.Add(_cur);
    }

    /// <summary>Sprites held across every scale and generation (diagnostics).</summary>
    public int Count
    {
        get { int n = 0; foreach (var s in _stores) n += s.Hot.Count + s.Cold.Count; return n; }
    }

    /// <summary>Pixel memory held across every scale and generation, bytes (diagnostics).</summary>
    public long Bytes
    {
        get { long n = 0; foreach (var s in _stores) n += s.HotBytes + s.ColdBytes; return n; }
    }

    /// <summary>Number of device scales with a store (diagnostics).</summary>
    public int Scales => _stores.Count;

    /// <summary>Device pixels per logical pixel for the following lookups. Selects (or creates) that scale's store.</summary>
    public void SetScale(float scale)
    {
        _tick++;
        if (MathF.Abs(scale - _cur.Scale) < 1e-4f) { _cur.LastUse = _tick; return; }
        Store? found = null;
        foreach (var s in _stores) if (MathF.Abs(scale - s.Scale) < 1e-4f) { found = s; break; }
        if (found is null)
        {
            if (_stores.Count >= MaxScales)
            {
                // drop the scale that has gone unused the longest
                var lru = _stores[0];
                foreach (var s in _stores) if (s.LastUse < lru.LastUse) lru = s;
                lru.Dispose();
                _stores.Remove(lru);
            }
            found = new Store { Scale = scale };
            _stores.Add(found);
        }
        _cur = found;
        _cur.LastUse = _tick;
    }

    private bool TryGet(long key, out Sprite sp)
    {
        var s = _cur;
        if (s.Hot.TryGetValue(key, out sp!)) return true;
        if (s.Cold.Remove(key, out sp!))
        {
            s.ColdBytes -= sp.Bytes;
            Insert(key, sp);
            return true;
        }
        return false;
    }

    private Sprite Insert(long key, Sprite sp)
    {
        var s = _cur;
        if (s.HotBytes + sp.Bytes > MaxBytesPerScale / 2 || s.Hot.Count >= MaxEntriesPerGeneration)
        {
            // turn over: the old generation goes, the current one becomes old
            foreach (var old in s.Cold.Values) old.Image.Dispose();
            s.Cold.Clear();
            (s.Hot, s.Cold) = (s.Cold, s.Hot);
            s.ColdBytes = s.HotBytes;
            s.HotBytes = 0;
        }
        s.Hot[key] = sp;
        s.HotBytes += sp.Bytes;
        return sp;
    }

    // key layout: kind(4 bits) | affiliation(2) | domain(2) | tier(2) | damaged(1) | size*2 (12) | extra (12)
    private static long Key(int kind, int aff, int domain, int tier, int damaged, int sizeQ, int extra) =>
        ((long)kind << 40) | ((long)aff << 38) | ((long)domain << 36) | ((long)tier << 34) | ((long)damaged << 33) |
        ((long)(sizeQ & 0xFFF) << 16) | (uint)(extra & 0xFFFF);

    private const int KindFrame = 1, KindDot = 2, KindMissile = 3, KindBadge = 4;

    /// <summary>Frame (and icon glyph for the full tier).</summary>
    public Sprite Frame(Affiliation aff, TrackDomain domain, bool full, bool damaged, float size, TrackKind kind)
    {
        int sq = Math.Clamp((int)MathF.Round(size * 2f), 2, 4000);
        int glyph = full ? (int)kind : 0;
        long key = Key(KindFrame, (int)aff, (int)domain, full ? 1 : 0, damaged ? 1 : 0, sq, glyph);
        if (TryGet(key, out var sp)) return sp;
        return Insert(key, BuildFrame(aff, domain, full, damaged, sq / 2f, kind));
    }

    /// <summary>Dot with a shape hint: diamond hostile (not missiles), square unknown/neutral, else circle.</summary>
    public Sprite Dot(Affiliation aff, bool missile, float radius)
    {
        int rq = Math.Clamp((int)MathF.Round(radius * 4f), 1, 4000);
        long key = Key(KindDot, (int)aff, 0, missile ? 1 : 0, 0, rq, 0);
        if (TryGet(key, out var sp)) return sp;
        return Insert(key, BuildDot(aff, missile, rq / 4f));
    }

    /// <summary>Missile arrowhead, hostile/unknown filled, own outlined, at a quantised heading.</summary>
    public Sprite Missile(Affiliation aff, float length, float headingDeg)
    {
        int lq = Math.Clamp((int)MathF.Round(length * 2f), 2, 4000);
        int dir = ((int)MathF.Round(headingDeg / 360f * MissileDirections) % MissileDirections + MissileDirections) % MissileDirections;
        long key = Key(KindMissile, (int)aff, 0, 0, 0, lq, dir);
        if (TryGet(key, out var sp)) return sp;
        return Insert(key, BuildMissile(aff, lq / 2f, dir * 360f / MissileDirections));
    }

    /// <summary>Control-state badge: M / O (accent) or A (friend blue).</summary>
    public Sprite Badge(ControlState ctrl)
    {
        long key = Key(KindBadge, 0, 0, 0, 0, 0, (int)ctrl);
        if (TryGet(key, out var sp)) return sp;
        return Insert(key, BuildBadge(ctrl));
    }

    private (SKSurface, SKCanvas, int) NewSurface(float halfLogical)
    {
        float scale = _cur.Scale;
        int half = (int)MathF.Ceiling(halfLogical * scale) + 2;
        var surface = SKSurface.Create(new SKImageInfo(half * 2, half * 2, SKColorType.Bgra8888, SKAlphaType.Premul))
                      ?? throw new InvalidOperationException("Could not allocate a sprite surface.");
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Translate(half, half);
        c.Scale(scale);
        return (surface, c, half);
    }

    private static Sprite Finish(SKSurface surface, int half)
    {
        // A raster surface's snapshot is already a raster image (ToRasterImage would return the same instance).
        using (surface) return new Sprite(surface.Snapshot(), half, half);
    }

    private Sprite BuildFrame(Affiliation aff, TrackDomain domain, bool full, bool damaged, float s, TrackKind kind)
    {
        var col = _theme.ForAffiliation(aff);
        float r = s / 2f;
        var (surface, c, half) = NewSurface(r + 1.5f);
        var path = _paths.Frame(aff, domain);
        c.Save();
        c.Scale(r);
        _fill.Color = Theme.A(col, damaged ? .32f : .16f);
        c.DrawPath(path, _fill);
        _stroke.Color = col;
        _stroke.StrokeWidth = (full ? 1.6f : 1.3f) / r;
        c.DrawPath(path, _stroke);
        c.Restore();
        var k = KindInfo.Of(kind);
        if (full && k.Glyph.Length > 0)
        {
            int px = Math.Max(1, (int)MathF.Round(s * (k.Glyph.Length > 2 ? .28f : .36f)));
            using var font = new SKFont(_glyphFace, px) { Subpixel = true, Edging = SKFontEdging.Antialias };
            float gy = domain == TrackDomain.Air ? s * .12f : domain == TrackDomain.Subsurface ? -s * .12f : 0f;
            var m = font.Metrics;
            float w = font.MeasureText(k.Glyph);
            _fill.Color = col;
            c.DrawText(k.Glyph, -w / 2, gy - (m.Ascent + m.Descent) / 2, SKTextAlign.Left, font, _fill);
        }
        return Finish(surface, half);
    }

    private Sprite BuildDot(Affiliation aff, bool missile, float r)
    {
        var (surface, c, half) = NewSurface(r + 1.5f);
        _fill.Color = _theme.ForAffiliation(aff);
        if (aff == Affiliation.Hostile && !missile)
        {
            float q = r + .6f;
            c.Save(); c.Scale(q); c.DrawPath(_paths.Diamond, _fill); c.Restore();
        }
        else if (aff is Affiliation.Unknown or Affiliation.Neutral) c.DrawRect(-r, -r, 2 * r, 2 * r, _fill);
        else c.DrawCircle(0, 0, r, _fill);
        return Finish(surface, half);
    }

    private Sprite BuildMissile(Affiliation aff, float L, float headingDeg)
    {
        var (surface, c, half) = NewSurface(L * .6f + 1.5f);
        var col = _theme.ForAffiliation(aff);
        // compass heading -> screen rotation of the +X arrowhead (0 deg = up)
        c.RotateDegrees(headingDeg - 90f);
        c.Scale(L);
        if (aff == Affiliation.Friend)
        {
            _stroke.Color = col;
            _stroke.StrokeWidth = 1.2f / L;
            c.DrawPath(_paths.Missile, _stroke);
        }
        else
        {
            _fill.Color = col;
            c.DrawPath(_paths.Missile, _fill);
        }
        return Finish(surface, half);
    }

    private Sprite BuildBadge(ControlState ctrl)
    {
        var (surface, c, half) = NewSurface(6.5f);
        _fill.Color = ctrl == ControlState.Agent ? _theme.Friend : _theme.Accent;
        c.DrawCircle(0, 0, 5.5f, _fill);
        using var font = new SKFont(_badgeFace, 8f) { Subpixel = true, Edging = SKFontEdging.Antialias };
        string letter = ctrl switch { ControlState.Manual => "M", ControlState.Order => "O", _ => "A" };
        var m = font.Metrics;
        float w = font.MeasureText(letter);
        _fill.Color = _theme.Ink0;
        c.DrawText(letter, -w / 2, .5f - (m.Ascent + m.Descent) / 2, SKTextAlign.Left, font, _fill);
        return Finish(surface, half);
    }

    /// <summary>Dispose every sprite at every scale.</summary>
    public void Flush()
    {
        foreach (var s in _stores) s.Dispose();
    }

    public void Dispose()
    {
        Flush();
        _fill.Dispose();
        _stroke.Dispose();
    }
}
