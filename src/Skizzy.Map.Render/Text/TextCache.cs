using SkiaSharp;

namespace Skizzy.Map.Render.Text;

/// <summary>A shaped run of text at the origin (baseline y = 0) and its advance width.</summary>
internal sealed class TextRun
{
    public TextRun(SKTextBlob? blob, float width) { Blob = blob; Width = width; }
    public SKTextBlob? Blob { get; }
    public float Width { get; }
}

/// <summary>
/// Caches shaped text so steady-state frames do not shape or allocate. Keyed by string and font slot.
/// Bounded: when it grows past <see cref="MaxEntries"/> it is flushed (labels are re-shaped on the next frame).
/// </summary>
internal sealed class TextCache : IDisposable
{
    public const int MaxEntries = 8192;

    private readonly SKFont[] _fonts;
    private readonly Dictionary<(string Text, int Slot), TextRun> _runs = new();

    public TextCache(SKFont[] fonts) => _fonts = fonts;

    public int Count => _runs.Count;

    public TextRun Get(string text, int slot)
    {
        if (_runs.TryGetValue((text, slot), out var run)) return run;
        if (_runs.Count >= MaxEntries) Flush();
        run = Shape(text, _fonts[slot]);
        _runs[(text, slot)] = run;
        return run;
    }

    public static TextRun Shape(string text, SKFont font)
    {
        if (text.Length == 0) return new TextRun(null, 0);
        float w = font.MeasureText(text);
        return new TextRun(SKTextBlob.Create(text, font), w);
    }

    public void Flush()
    {
        foreach (var r in _runs.Values) r.Blob?.Dispose();
        _runs.Clear();
    }

    public void Dispose() => Flush();
}
