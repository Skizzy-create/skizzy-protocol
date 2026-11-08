using SkiaSharp;

namespace Skizzy.Map.Render.Symbols;

/// <summary>
/// Pooled list of independent line segments drawn with <see cref="SKCanvas.DrawPoints(SKPointMode, SKPoint[], SKPaint)"/>
/// in <see cref="SKPointMode.Lines"/> mode. For thousands of short dashes this is about three times cheaper than one
/// contour per dash in a path. SkiaSharp takes whole arrays, so points live in fixed-size chunks and the last chunk's
/// tail is padded with degenerate off-screen segments; nothing is allocated once the chunks exist.
/// </summary>
internal sealed class SegmentBatch
{
    private const int ChunkPoints = 256; // 128 segments
    private static readonly SKPoint Park = new(-1e5f, -1e5f);
    private readonly List<SKPoint[]> _chunks = new();
    private int _count; // points used

    public bool IsEmpty => _count == 0;

    public void Reset() => _count = 0;

    public void Add(float x0, float y0, float x1, float y1)
    {
        int chunk = _count / ChunkPoints, at = _count % ChunkPoints;
        if (chunk == _chunks.Count) _chunks.Add(new SKPoint[ChunkPoints]);
        var a = _chunks[chunk];
        a[at] = new SKPoint(x0, y0);
        a[at + 1] = new SKPoint(x1, y1);
        _count += 2;
    }

    public void Draw(SKCanvas c, SKPaint paint)
    {
        if (_count == 0) return;
        int full = _count / ChunkPoints, rest = _count % ChunkPoints;
        for (int i = 0; i < full; i++) c.DrawPoints(SKPointMode.Lines, _chunks[i], paint);
        if (rest > 0)
        {
            var last = _chunks[full];
            Array.Fill(last, Park, rest, ChunkPoints - rest);
            c.DrawPoints(SKPointMode.Lines, last, paint);
        }
    }
}
