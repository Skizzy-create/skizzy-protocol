using SkiaSharp;
using Skizzy.Map.Render.Model;
using Skizzy.Map.Render.Text;

namespace Skizzy.Map.Render;

/// <summary>An axis-aligned screen rectangle.</summary>
public readonly record struct ScreenRect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Intersects(in ScreenRect o) => X < o.Right && Right > o.X && Y < o.Bottom && Bottom > o.Y;
}

/// <summary>What the last frame drew, for tests, diagnostics and hit-testing.</summary>
public struct FrameStats
{
    public int TracksVisible;
    public int Dots;
    public int Frames;
    public int FullSymbols;
    public int Missiles;
    public int LabelsPlaced;
    public int LabelsCulled;
    public int ActiveOrders;
    /// <summary>Alpha used for per-unit order lines: 0.75, or 0.28 when more than 6 orders are active.</summary>
    public float OrderLineAlpha;
    /// <summary>Per-unit dashed order lines drawn (move, attack, hunt, defend, RTB inbound).</summary>
    public int OrderLines;
    /// <summary>Racetrack patterns drawn (each shared pattern once).</summary>
    public int Racetracks;
    /// <summary>MARSHAL rings drawn (one per base with an RTB order).</summary>
    public int MarshalRings;
    /// <summary>On-screen VIP symbols given corner brackets.</summary>
    public int VipMarkers;
    /// <summary>VIP tags drawn (selected VIPs only).</summary>
    public int VipTags;
    /// <summary>VIP protection rings that cross the view.</summary>
    public int VipRings;
    /// <summary>On-screen tracks marked "own sensor only".</summary>
    public int OwnSensorMarks;
    /// <summary>On-screen missiles given the inbound-to-friendly ring.</summary>
    public int InboundMarks;
    /// <summary>Missiles left off the plot by the Missiles filter.</summary>
    public int MissilesHidden;
    /// <summary>Visible tracks not drawn because the viewing side has not detected them.</summary>
    public int Undetected;
}

/// <summary>Per-stage CPU time of the last frame, in milliseconds (diagnostics; costs a few timestamps per frame).</summary>
public struct FrameTimings
{
    public double Background, Layout, Rings, Trails, Orders, Tracks, Labels, Overlay, Total;
}

/// <summary>Per-track display state carried between frames.</summary>
internal sealed class TrackVisual
{
    public float Size = -1f;
    public SymbolTier Tier;
    public bool HasTier;
    public long LastFrame;

    // label cache: rebuilt only when the inputs change
    public string? LabelName;
    public int LabelAltKey = int.MinValue;
    public bool LabelIsMissile;
    public TextRun? Label;

    public void SetLabel(TextRun? run)
    {
        Label?.Blob?.Dispose();
        Label = run;
    }
}

/// <summary>Counts-view accumulator for one screen cell.</summary>
internal struct PillAccumulator
{
    public bool Missile;
    public int N;
    public float SumX, SumY;
}

/// <summary>One visible track in the current frame.</summary>
internal struct DrawItem
{
    public int TrackIndex;
    public int Id;
    public float Sx, Sy;
    /// <summary>Displayed (eased) symbol size in px.</summary>
    public float S;
    public SymbolTier Tier;
    public bool Missile;
    public bool Selected;
    public bool Badge;
    public bool Vip;
    /// <summary>Known only from one own unit's own sensor (<see cref="TrackDetection.OwnSensor"/>).</summary>
    public bool OwnSensor;
    /// <summary>Half-size of the symbol's footprint (for label avoidance and hit testing).</summary>
    public float Half;
    public TrackVisual Visual;
}

/// <summary>
/// State carried between frames for one map window: symbol easing and tier hysteresis, cached label text,
/// pooled per-frame buffers, and what the last frame drew (hit-testing seam for the interaction layer).
/// Not thread-safe; one per map window.
/// </summary>
public sealed class RenderState : IDisposable
{
    private const int PruneEvery = 120;

    public PlotOptions Options { get; } = new();

    /// <summary>Seconds since the previous frame; drives the ~150 ms size easing. Set by the host each frame.</summary>
    public float FrameSeconds { get; set; } = 1f / 60f;

    /// <summary>When true, the next frame snaps sizes instead of easing (first frame, headless renders, after a jump).</summary>
    public bool SnapNextFrame { get; set; } = true;

    public long FrameIndex { get; private set; }

    public FrameStats LastFrame { get; internal set; }

    public FrameTimings LastTimings { get; internal set; }

    internal readonly Dictionary<int, TrackVisual> Visuals = new();
    private readonly List<int> _pruneScratch = new();

    // pooled per-frame buffers
    internal DrawItem[] Items = new DrawItem[256];
    internal int ItemCount;
    internal int[] Order = new int[256];
    internal double[] SortKeys = new double[256];
    internal int[] CellStart = new int[64];
    internal int[] CellItems = new int[256];
    internal ScreenRect[] SymbolBoxes = new ScreenRect[256];
    internal int SymbolBoxCount;
    internal ScreenRect[] LabelBoxes = new ScreenRect[256];
    internal int LabelBoxCount;
    internal int[] LabelOwner = new int[256];
    internal readonly Dictionary<long, PillAccumulator> Pills = new();
    internal readonly HashSet<int> RoutesDrawn = new();
    internal readonly HashSet<int> PatternsDrawn = new();
    internal readonly HashSet<int> MarshalBases = new();
    internal readonly Dictionary<int, int> IndexById = new();

    /// <summary>Footprints of every symbol drawn in the last frame.</summary>
    public ReadOnlySpan<ScreenRect> LastSymbolBoxes => SymbolBoxes.AsSpan(0, SymbolBoxCount);

    /// <summary>Backing boxes of every label placed in the last frame.</summary>
    public ReadOnlySpan<ScreenRect> LastLabelBoxes => LabelBoxes.AsSpan(0, LabelBoxCount);

    /// <summary>Displayed size and tier of a track in the last frame.</summary>
    public bool TryGetSymbol(int trackId, out float size, out SymbolTier tier)
    {
        if (Visuals.TryGetValue(trackId, out var v) && v.LastFrame == FrameIndex && v.Size >= 0)
        {
            size = v.Size; tier = v.Tier; return true;
        }
        size = 0; tier = SymbolTier.Dot; return false;
    }

    /// <summary>The mockup's <c>pick()</c>: nearest track within max(7, size/2 + 4) px of a screen point, or 0.</summary>
    public int HitTest(float x, float y)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < ItemCount; i++)
        {
            ref var it = ref Items[i];
            float dx = it.Sx - x, dy = it.Sy - y, d = MathF.Sqrt(dx * dx + dy * dy);
            float r = MathF.Max(7f, it.S / 2f + 4f);
            if (d < r && d < bd) { bd = d; best = it.Id; }
        }
        return best;
    }

    internal void BeginFrame() => FrameIndex++;

    internal void EndFrame()
    {
        SnapNextFrame = false;
        if (FrameIndex % PruneEvery != 0) return;
        _pruneScratch.Clear();
        foreach (var kv in Visuals) if (FrameIndex - kv.Value.LastFrame > 2) _pruneScratch.Add(kv.Key);
        foreach (var id in _pruneScratch)
        {
            Visuals[id].SetLabel(null);
            Visuals.Remove(id);
        }
    }

    /// <summary>Make sure a scratch buffer holds at least n items. Contents are NOT preserved (use before refilling).</summary>
    internal static void Ensure<T>(ref T[] a, int n)
    {
        if (a.Length < n) a = new T[Math.Max(n, a.Length * 2)];
    }

    public void Dispose()
    {
        foreach (var v in Visuals.Values) v.Label?.Blob?.Dispose();
        Visuals.Clear();
    }
}
