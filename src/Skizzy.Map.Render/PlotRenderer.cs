using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using Skizzy.Map.Render.Layout;
using Skizzy.Map.Render.Model;
using Skizzy.Map.Render.Symbols;
using Skizzy.Map.Render.Text;

namespace Skizzy.Map.Render;

/// <summary>
/// Draws a <see cref="PlotScene"/> onto any <see cref="SKCanvas"/>: an Avalonia control's Skia surface, an
/// in-game overlay texture or a headless PNG. A port of the plot-drawing code in
/// design/mockups/command-screen-v1.html, following design/23-visual-design.md §4.
/// <para>
/// Steady-state frames do not allocate: paints, paths, fonts and shaped text are cached here; per-frame buffers
/// live in <see cref="RenderState"/>. Lines of the same style (trails, leaders, stalks, dots, missiles) are batched
/// into one path per colour so a 2,000-track frame costs a few hundred draw calls, not tens of thousands.
/// </para>
/// One renderer can serve several map windows (each with its own <see cref="RenderState"/>); it is not thread-safe.
/// </summary>
public sealed class PlotRenderer : IDisposable
{
    // font slots in the text cache
    private const int SlotLabel = 0, SlotSmall = 1, SlotZone = 2, SlotBadge = 3, SlotPill = 4, SlotVip = 5, SlotMarshal = 6, SlotGlyphBase = 7;
    private const int MaxGlyphPx = 24;

    private readonly Theme _theme;
    private readonly SymbolPaths _symbols = new();
    private readonly SpriteCache _sprites;
    private readonly SKFont[] _fonts;
    private readonly float[] _middle; // baseline offset that centres text vertically, per font slot
    private readonly float _vipDescent; // the VIP tag sits on canvas textBaseline = 'bottom'
    private readonly TextCache _text;
    private readonly RectGrid _labelGrid = new();
    private readonly Dictionary<int, string> _kmText = new();
    private readonly Dictionary<int, string> _pillText = new();

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _ink = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    /// <summary>
    /// Stroke width for 1 px lines: 0 (Skia's hairline, one device pixel, several times cheaper than a stroked path)
    /// when the canvas carries a uniform scale up to 2 (any normal DPI), else 1. Set per frame with
    /// <see cref="_hairGain"/>; use <see cref="Hairline"/> to set the paint.
    /// </summary>
    private float _hair = 1f;
    /// <summary>Device pixels per logical pixel when hairlines stand in for 1 px lines (1 on an unscaled canvas).</summary>
    private float _hairGain = 1f;
    private SKRect _clip;

    // batched per-colour paths, reset every frame
    private readonly SKPath[] _trailOld = NewPaths(4), _trailNew = NewPaths(4);
    private readonly SegmentBatch[] _leaders = [new(), new(), new(), new()];
    private readonly SegmentBatch[] _stalks = [new(), new(), new(), new(), new()];
    private readonly SKPath[] _dots = NewPaths(4), _mslFill = NewPaths(4), _mslStroke = NewPaths(4);
    private readonly SegmentBatch[] _targetLines = [new(), new()];
    private readonly SKPath _gridLo = new(), _gridHi = new(), _scratch = new(), _labelBacks = new(), _brackets = new();
    private readonly SegmentBatch _ringPrimary = new(), _ringSecondary = new(), _ringRadar = new();
    private readonly SegmentBatch _orderLines = new(), _routeLegs = new(), _dashes = new();
    private readonly SKPath _orderMarks = new(), _racetracks = new(), _arrows = new(), _vipBrackets = new(), _routes = new(), _routeEnds = new();
    private readonly SegmentBatch _marshal = new(), _vipRing = new(), _vipRingSelected = new();
    private readonly SegmentBatch[] _ownSensor = [new(), new(), new(), new()];
    private readonly SKPath _inbound = new();

    public PlotRenderer(Theme? theme = null, FontSet? fonts = null)
    {
        _theme = theme ?? Theme.Default;
        var fs = fonts ?? FontSet.Default;
        _fonts = new SKFont[SlotGlyphBase + MaxGlyphPx + 1];
        _fonts[SlotLabel] = NewFont(fs.DataRegular, 10.5f);
        _fonts[SlotSmall] = NewFont(fs.DataRegular, 10f);
        _fonts[SlotZone] = NewFont(fs.DisplaySemiBold, 10f);
        _fonts[SlotBadge] = NewFont(fs.UiBold, 8f);
        _fonts[SlotPill] = NewFont(fs.DataMedium, 10.5f);
        _fonts[SlotVip] = NewFont(fs.UiBold, 9f);          // mockup: 700 9px UI
        _fonts[SlotMarshal] = NewFont(fs.DataMedium, 9f);  // mockup: 600 9px data; the bundled mono stops at 500
        for (int px = 0; px <= MaxGlyphPx; px++) _fonts[SlotGlyphBase + px] = NewFont(fs.UiSemiBold, Math.Max(1, px));
        _middle = new float[_fonts.Length];
        for (int i = 0; i < _fonts.Length; i++)
        {
            var m = _fonts[i].Metrics;
            _middle[i] = -(m.Ascent + m.Descent) / 2f; // canvas textBaseline = 'middle'
        }
        _vipDescent = _fonts[SlotVip].Metrics.Descent;
        _text = new TextCache(_fonts);
        _sprites = new SpriteCache(_theme, _symbols, fs.UiSemiBold, fs.UiBold);
    }

    /// <summary>
    /// Draw symbols from the sprite cache (default). Off draws every symbol as vector paths, which is what
    /// happens anyway on a rotated or skewed canvas; about ten times slower on a CPU surface.
    /// </summary>
    public bool UseSprites { get; set; } = true;

    /// <summary>Number of cached symbol sprites, across every device scale (diagnostics).</summary>
    public int SpriteCount => _sprites.Count;

    /// <summary>Pixel memory held by cached sprites, bytes (diagnostics). Bounded per device scale.</summary>
    public long SpriteBytes => _sprites.Bytes;

    /// <summary>Device scales with cached sprites (diagnostics): one per DPI the renderer has drawn at recently.</summary>
    public int SpriteScales => _sprites.Scales;

    public Theme Theme => _theme;

    /// <summary>
    /// Draw one frame. The canvas may already carry a transform (for example a DPI scale); the plot is drawn in
    /// logical pixels of <paramref name="vp"/>.
    /// </summary>
    public void Render(SKCanvas canvas, PlotScene scene, Viewport vp, RenderState state)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(state);
        state.BeginFrame();
        var opt = state.Options;
        state.LastFrame = default;

        long t0 = Stopwatch.GetTimestamp();
        var tm = new FrameTimings();
        var cm = canvas.TotalMatrix;
        // A 1 px line on a 1.5x (150 % DPI) canvas is 1.5 device pixels wide; stroking that as a path made the trails
        // and rings of a 2,000-track frame cost ~3x more. Up to 2x, draw a hairline instead and raise its alpha by the
        // scale, which keeps the line's weight (coverage) the same.
        bool uniform = cm.SkewX == 0 && cm.SkewY == 0 && cm.Persp0 == 0 && cm.Persp1 == 0 && MathF.Abs(cm.ScaleX - cm.ScaleY) < 1e-3f;
        if (uniform && MathF.Abs(cm.ScaleX - 1f) < 0.01f) { _hair = 0f; _hairGain = 1f; }
        else if (uniform && cm.ScaleX >= 0.5f && cm.ScaleX <= 2.01f) { _hair = 0f; _hairGain = cm.ScaleX; }
        else { _hair = 1f; _hairGain = 1f; }
        _clip = new SKRect(0, 0, vp.Width, vp.Height);
        canvas.Save();
        canvas.ClipRect(_clip);
        _fill.Color = _theme.Sea;
        canvas.DrawRect(0, 0, vp.Width, vp.Height, _fill);
        if (vp.Width <= 0 || vp.Height <= 0 || !(vp.Scale > 0) || !float.IsFinite(vp.Scale) ||
            !float.IsFinite(vp.CenterX) || !float.IsFinite(vp.CenterY))
        {
            // a minimised window or a broken view: nothing to place (and the grid loop would never end at scale 0)
            state.ItemCount = 0; state.SymbolBoxCount = 0; state.LabelBoxCount = 0;
            canvas.Restore();
            state.EndFrame();
            return;
        }

        if (opt.Zones) DrawZoneFills(canvas, scene, vp);
        if (opt.Grid) DrawGrid(canvas, vp);
        if (opt.Zones) DrawZones(canvas, scene, vp);
        long t1 = Stopwatch.GetTimestamp(); tm.Background = Ms(t0, t1);

        SymbolSizer.Compute(scene, vp, state);
        BuildSymbolBoxes(state);
        SortBySizeDescending(state);
        long t2 = Stopwatch.GetTimestamp(); tm.Layout = Ms(t1, t2);

        if (opt.Rings) DrawRings(canvas, scene, vp, state);
        DrawMarkers(canvas, scene, vp, state);
        long t3 = Stopwatch.GetTimestamp(); tm.Rings = Ms(t2, t3);
        if (opt.Trails) DrawTrails(canvas, scene, vp, state);
        long t4 = Stopwatch.GetTimestamp(); tm.Trails = Ms(t3, t4);
        if (opt.TargetLines) DrawTargetLines(canvas, scene, vp, state);
        if (opt.Orders) DrawOrders(canvas, scene, vp, state);
        long t5 = Stopwatch.GetTimestamp(); tm.Orders = Ms(t4, t5);
        DrawTracks(canvas, scene, vp, state);
        if (opt.Counts) DrawPills(canvas, state);
        if (opt.Events) DrawEvents(canvas, scene, vp);
        long t6 = Stopwatch.GetTimestamp(); tm.Tracks = Ms(t5, t6);
        if (opt.Labels) DrawLabels(canvas, scene, vp, state);
        else state.LabelBoxCount = 0;
        long t7 = Stopwatch.GetTimestamp(); tm.Labels = Ms(t6, t7);
        DrawSelection(canvas, scene, state);
        if (opt.ScaleBar) DrawScale(canvas, vp);
        long t8 = Stopwatch.GetTimestamp(); tm.Overlay = Ms(t7, t8);
        tm.Total = Ms(t0, t8);
        state.LastTimings = tm;

        canvas.Restore();
        state.EndFrame();
    }

    private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Sets <see cref="_stroke"/> for a 1 px line of <paramref name="color"/>: a hairline with its alpha scaled by the
    /// device scale where that is possible, else a real 1 px stroke (an opaque colour cannot be made heavier).
    /// </summary>
    private void Hairline(SKColor color)
    {
        _stroke.StrokeWidth = _hair;
        _stroke.Color = color;
        if (_hairGain == 1f) return;
        float a = color.Alpha * _hairGain;
        if (a > 255.5f) _stroke.StrokeWidth = 1f;
        else _stroke.Color = color.WithAlpha((byte)(a + .5f));
    }

    /// <summary>
    /// Sets <see cref="_stroke"/> for a thin line of <paramref name="widthPx"/> (the order lines' 1.2 px): a hairline
    /// carrying the same ink (alpha scaled by the width and the device scale) where the colour leaves room, else a real
    /// stroke. Skia strokes every dash of a wide line as its own path; hairline dashes go through its fast line blitter,
    /// which keeps thousands of order lines (an all-attack) inside the frame budget.
    /// </summary>
    private void ThinLine(SKColor color, float widthPx)
    {
        _stroke.StrokeWidth = widthPx;
        _stroke.Color = color;
        if (_hair != 0f) return;
        float a = color.Alpha * _hairGain * widthPx;
        if (a > 255.5f) return;
        _stroke.StrokeWidth = 0f;
        _stroke.Color = color.WithAlpha((byte)(a + .5f));
    }

    /// <summary>The device-scale handling chosen for the last frame (for tests).</summary>
    internal (float Width, float Gain) HairlineMode => (_hair, _hairGain);

    // ---------------------------------------------------------------- background

    // an array, not a ReadOnlySpan<int> property: unoptimised (Debug) code builds that span through a field handle,
    // which allocates on every call (once a frame for the grid and once for the scale bar)
    private static readonly int[] NiceSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];

    /// <summary>
    /// The mockup's step choice (first of 1, 2, 5 … <paramref name="mockupLast"/> km that is at least
    /// <paramref name="minPx"/> long, else <paramref name="mockupLast"/>), unchanged over the mockup's zoom range
    /// (0.35–220 px/km). Only when even that fallback would be shorter than <paramref name="floorPx"/> (a whole-body
    /// view, doc 11 §4.1) do the longer steps take over.
    /// </summary>
    internal static int NiceStep(float scale, float minPx, int mockupLast, float floorPx)
    {
        foreach (var v in NiceSteps) { if (v > mockupLast) break; if (v * scale >= minPx) return v; }
        if (mockupLast * scale >= floorPx) return mockupLast;
        foreach (var v in NiceSteps) if (v > mockupLast && v * scale >= minPx) return v;
        return NiceSteps[^1];
    }

    private void DrawGrid(SKCanvas c, in Viewport vp)
    {
        int st = NiceStep(vp.Scale, 80, 200, 40);
        float x0 = vp.ToWorldX(0), x1 = vp.ToWorldX(vp.Width), y0 = vp.ToWorldY(vp.Height), y1 = vp.ToWorldY(0);
        if ((x1 - x0) / st > 400 || (y1 - y0) / st > 400) return; // beyond any real zoom: skip rather than draw thousands of lines
        _gridLo.Reset(); _gridHi.Reset();
        _ink.Color = _theme.TextFaint;
        float labelY = vp.Height - 48;
        for (long x = (long)Math.Ceiling(x0 / st) * st; x <= x1; x += st)
        {
            float sx = MathF.Round(vp.ToScreenX(x)) + .5f;
            var p = x % (st * 5) == 0 ? _gridHi : _gridLo;
            p.MoveTo(sx, 0); p.LineTo(sx, vp.Height);
        }
        for (long y = (long)Math.Ceiling(y0 / st) * st; y <= y1; y += st)
        {
            float sy = MathF.Round(vp.ToScreenY(y)) + .5f;
            var p = y % (st * 5) == 0 ? _gridHi : _gridLo;
            p.MoveTo(0, sy); p.LineTo(vp.Width, sy);
        }
        Hairline(_theme.Grid); c.DrawPath(_gridLo, _stroke);
        Hairline(_theme.GridHi); c.DrawPath(_gridHi, _stroke);
        for (long x = (long)Math.Ceiling(x0 / st) * st; x <= x1; x += st)
        {
            float sx = MathF.Round(vp.ToScreenX(x)) + .5f;
            DrawText(c, KmText((int)x), SlotSmall, sx + 3, labelY, _ink);
        }
    }

    /// <summary>
    /// Exclusion-zone fills (3.5 % of the affiliation colour over the sea). Drawn before the grid as an opaque,
    /// pre-mixed colour: identical on the plain sea, and an opaque fill is several times cheaper than blending a
    /// near-full-screen area every frame.
    /// </summary>
    private void DrawZoneFills(SKCanvas c, PlotScene scene, in Viewport vp)
    {
        foreach (var z in scene.Zones)
        {
            if (z.Kind != ZoneKind.Exclusion || z.Fill.Count < 3) continue;
            _scratch.Reset();
            for (int i = 0; i < z.Fill.Count; i++)
            {
                float sx = vp.ToScreenX(z.Fill[i].X), sy = vp.ToScreenY(z.Fill[i].Y);
                if (i == 0) _scratch.MoveTo(sx, sy); else _scratch.LineTo(sx, sy);
            }
            _scratch.Close();
            _fill.Color = Mix(_theme.Sea, _theme.ForAffiliation(z.Affiliation), .035f);
            c.DrawPath(_scratch, _fill);
        }
    }

    private static SKColor Mix(SKColor under, SKColor over, float a) => new(
        (byte)MathF.Round(under.Red + (over.Red - under.Red) * a),
        (byte)MathF.Round(under.Green + (over.Green - under.Green) * a),
        (byte)MathF.Round(under.Blue + (over.Blue - under.Blue) * a));

    private void DrawZones(SKCanvas c, PlotScene scene, in Viewport vp)
    {
        _stroke.StrokeWidth = 1.2f;
        foreach (var z in scene.Zones)
        {
            var col = z.Kind == ZoneKind.Bullseye && z.Affiliation == Affiliation.Neutral ? _theme.Text : _theme.ForAffiliation(z.Affiliation);
            switch (z.Kind)
            {
                case ZoneKind.Exclusion:
                {
                    // dashed boundary, 3.5 % fill, label kept on screen vertically
                    if (z.Boundary.Count >= 2)
                    {
                        _dashes.Reset();
                        for (int i = 1; i < z.Boundary.Count; i++)
                            Dasher.Line(_dashes, vp.ToScreenX(z.Boundary[i - 1].X), vp.ToScreenY(z.Boundary[i - 1].Y),
                                vp.ToScreenX(z.Boundary[i].X), vp.ToScreenY(z.Boundary[i].Y), 6, 6, _clip);
                        _stroke.Color = Theme.A(col, .55f);
                        _stroke.StrokeWidth = 1.2f;
                        _dashes.Draw(c, _stroke);
                    }
                    if (z.Label.Length > 0)
                    {
                        float lx = vp.ToScreenX(z.LabelAnchor.X), ly = vp.ToScreenY(z.LabelAnchor.Y);
                        _ink.Color = Theme.A(col, .8f);
                        DrawText(c, z.Label, SlotZone, lx + 6, Math.Clamp(ly - 120, 80, Math.Max(80, vp.Height - 80)), _ink);
                    }
                    break;
                }
                case ZoneKind.Patrol:
                {
                    float cx = vp.ToScreenX(z.Center.X), cy = vp.ToScreenY(z.Center.Y);
                    float r = (z.RadiiKm.Length > 0 ? z.RadiiKm[0] : 0) * vp.Scale;
                    _stroke.Color = Theme.A(col, .45f);
                    _stroke.StrokeWidth = 1.2f;
                    _dashes.Reset();
                    Dasher.Circle(_dashes, cx, cy, r, 3, 5, _clip);
                    _dashes.Draw(c, _stroke);
                    _ink.Color = Theme.A(col, .75f);
                    DrawText(c, z.Label, SlotZone, cx - 24, cy - r - 6, _ink);
                    break;
                }
                case ZoneKind.Bullseye:
                {
                    float cx = vp.ToScreenX(z.Center.X), cy = vp.ToScreenY(z.Center.Y);
                    Hairline(Theme.A(col, .28f));
                    foreach (var r in z.RadiiKm) c.DrawCircle(cx, cy, r * vp.Scale, _stroke);
                    c.DrawLine(cx - 6, cy, cx + 6, cy, _stroke);
                    c.DrawLine(cx, cy - 6, cx, cy + 6, _stroke);
                    _ink.Color = Theme.A(col, .55f);
                    DrawText(c, z.Label, SlotZone, cx + 8, cy - 8, _ink);
                    break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- per-track passes

    private void BuildSymbolBoxes(RenderState st)
    {
        RenderState.Ensure(ref st.SymbolBoxes, st.ItemCount * 3);
        int n = 0;
        for (int i = 0; i < st.ItemCount; i++)
        {
            ref var it = ref st.Items[i];
            st.SymbolBoxes[n++] = new ScreenRect(it.Sx - it.Half, it.Sy - it.Half, it.Half * 2, it.Half * 2);
            if (it.Badge)
                st.SymbolBoxes[n++] = new ScreenRect(it.Sx + it.S / 2 - 6, it.Sy - it.S / 2 - 6, 12, 12);
            if (it.Vip && it.Selected)
            {
                // the VIP tag above the brackets: labels keep clear of it like any symbol
                var tag = _text.Get(VipTag, SlotVip);
                float bottom = it.Sy - SymbolSizer.VipBracketRadius(it.S) - 2;
                st.SymbolBoxes[n++] = new ScreenRect(it.Sx - tag.Width / 2 - 1, bottom - 10, tag.Width + 2, 10);
            }
        }
        st.SymbolBoxCount = n;
    }

    private const string VipTag = "VIP";

    /// <summary>Big first, small on top (stable: ties keep scene order).</summary>
    private static void SortBySizeDescending(RenderState st)
    {
        int n = st.ItemCount;
        RenderState.Ensure(ref st.Order, n);
        RenderState.Ensure(ref st.SortKeys, n);
        for (int i = 0; i < n; i++) { st.SortKeys[i] = -st.Items[i].S + i * 1e-7; st.Order[i] = i; }
        Array.Sort(st.SortKeys, st.Order, 0, n);
    }

    /// <summary>
    /// Range rings (design/23 §4.12). Walks every visible own ship, not only the on-screen ones: a selected ship just
    /// off the edge keeps its 150 km radar ring and weapon rings where they cross the screen. Rings that miss the
    /// screen cost nothing (the dasher rejects them).
    /// </summary>
    private void DrawRings(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        var tracks = scene.Tracks;
        _ringPrimary.Reset(); _ringSecondary.Reset(); _ringRadar.Reset();
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.Rings is not { Length: > 0 } rings || !t.IsDrawn || t.Affiliation != Affiliation.Friend || t.Domain != TrackDomain.Surface) continue;
            float sx = vp.ToScreenX(t.X), sy = vp.ToScreenY(t.Y);
            if (!float.IsFinite(sx) || !float.IsFinite(sy)) continue;
            bool on = t.Selected;
            int weapons = 0;
            foreach (var r in rings) if (!r.IsRadar) weapons++;
            int w = 0;
            foreach (var r in rings)
            {
                float R = r.RadiusKm * vp.Scale;
                if (r.IsRadar)
                {
                    // only the selected ship shows its radar ring and label
                    if (!on) continue;
                    Dasher.Circle(_ringRadar, sx, sy, R, 1, 5, _clip);
                    _ink.Color = Theme.A(_theme.Friend, .7f);
                    DrawText(c, r.Label, SlotSmall, sx + 4, sy - R - 4, _ink);
                    continue;
                }
                // the outer weapon ring of a ship with several reads a little brighter (mockup: SM-2 .16/.5, ESSM .12/.4)
                bool primary = weapons > 1 && w == 0;
                w++;
                if (on)
                {
                    if (!CircleCrossesClip(sx, sy, R)) continue;
                    Hairline(Theme.A(_theme.Friend, primary ? .5f : .4f));
                    c.DrawCircle(sx, sy, R, _stroke);
                    _ink.Color = Theme.A(_theme.Friend, .7f);
                    if (primary) DrawText(c, r.Label, SlotSmall, sx + R * .71f + 4, sy - R * .71f, _ink);
                    else DrawText(c, r.Label, SlotSmall, sx + R * .71f + 4, sy + R * .71f + 10, _ink);
                }
                else Dasher.Circle(primary ? _ringPrimary : _ringSecondary, sx, sy, R, 2, 4, _clip);
            }
        }
        if (!_ringPrimary.IsEmpty) { Hairline(Theme.A(_theme.Friend, .16f)); _ringPrimary.Draw(c, _stroke); }
        if (!_ringSecondary.IsEmpty) { Hairline(Theme.A(_theme.Friend, .12f)); _ringSecondary.Draw(c, _stroke); }
        if (!_ringRadar.IsEmpty) { Hairline(Theme.A(_theme.Friend, .35f)); _ringRadar.Draw(c, _stroke); }
    }

    private void DrawTrails(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        foreach (var p in _trailOld) p.Reset();
        foreach (var p in _trailNew) p.Reset();
        var tracks = scene.Tracks;
        bool missileTrails = st.Options.MissileTrails;
        for (int i = 0; i < st.ItemCount; i++)
        {
            ref var it = ref st.Items[i];
            if (it.Missile && !missileTrails) continue;
            var t = tracks[it.TrackIndex];
            var tr = t.Trail;
            int n = tr.Count;
            if (n < 2) continue;
            int a = (int)t.Affiliation;
            int take = it.Missile ? 12 : 20;
            int s0 = Math.Max(0, n - take);
            int mid = (s0 + n) / 2;
            // two strokes per trail: a faint old half and a brighter recent half
            var old = _trailOld[a];
            var (x, y) = tr[s0];
            float px = vp.ToScreenX(x), py = vp.ToScreenY(y);
            old.MoveTo(px, py);
            // screen-space decimation: points closer than 3 px to the last one emitted add nothing visible
            for (int k = s0 + 1; k <= mid && k < n; k++)
            {
                (x, y) = tr[k];
                float qx = vp.ToScreenX(x), qy = vp.ToScreenY(y);
                if (k < mid && Near(qx, qy, px, py)) continue;
                px = qx; py = qy;
                old.LineTo(px, py);
            }
            var recent = _trailNew[a];
            recent.MoveTo(px, py);
            for (int k = mid + 1; k < n; k++)
            {
                (x, y) = tr[k];
                float qx = vp.ToScreenX(x), qy = vp.ToScreenY(y);
                if (Near(qx, qy, px, py)) continue;
                px = qx; py = qy;
                recent.LineTo(px, py);
            }
            recent.LineTo(it.Sx, it.Sy);
        }
        for (int a = 0; a < 4; a++)
        {
            var col = _theme.ForAffiliation((Affiliation)a);
            if (!_trailOld[a].IsEmpty) { Hairline(Theme.A(col, .12f)); c.DrawPath(_trailOld[a], _stroke); }
            if (!_trailNew[a].IsEmpty) { Hairline(Theme.A(col, .4f)); c.DrawPath(_trailNew[a], _stroke); }
        }
    }

    /// <summary>True when a circle's outline passes through the (slightly grown) visible rectangle.</summary>
    private bool CircleCrossesClip(float cx, float cy, float r)
    {
        float nx = Math.Clamp(cx, _clip.Left, _clip.Right) - cx, ny = Math.Clamp(cy, _clip.Top, _clip.Bottom) - cy;
        float fx = MathF.Max(MathF.Abs(_clip.Left - cx), MathF.Abs(_clip.Right - cx));
        float fy = MathF.Max(MathF.Abs(_clip.Top - cy), MathF.Abs(_clip.Bottom - cy));
        return MathF.Sqrt(nx * nx + ny * ny) <= r + 2 && MathF.Sqrt(fx * fx + fy * fy) >= r - 2;
    }

    private static bool Near(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy < 9f;
    }

    private void DrawTargetLines(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        _targetLines[0].Reset(); _targetLines[1].Reset();
        var tracks = scene.Tracks;
        for (int i = 0; i < st.ItemCount; i++)
        {
            ref var it = ref st.Items[i];
            if (!it.Missile) continue;
            var t = tracks[it.TrackIndex];
            if (t.TargetId == 0 || !st.IndexById.TryGetValue(t.TargetId, out int ti)) continue;
            var tg = tracks[ti];
            if (!tg.IsDrawn) continue; // never point at a contact the side has not detected
            Dasher.Line(_targetLines[t.Affiliation == Affiliation.Friend ? 1 : 0], it.Sx, it.Sy, vp.ToScreenX(tg.X), vp.ToScreenY(tg.Y), 2, 4, _clip);
        }
        Hairline(Theme.A(_theme.Hostile, .22f)); _targetLines[0].Draw(c, _stroke);
        Hairline(Theme.A(_theme.Friend, .22f)); _targetLines[1].Draw(c, _stroke);
    }

    /// <summary>
    /// VIP markers and the "own sensor only" mark (design/23 §6.13 and §6.15, mockup <c>drawVips()</c>), drawn after the
    /// rings and before the trails, under the symbols. A VIP gets four accent corner arcs, a faint dashed protection ring
    /// of <see cref="PlotTrack.VipRadiusKm"/> (brighter while selected; also for a VIP just off-screen whose ring
    /// crosses the view) and, while selected, a "VIP" tag. A track known only from one own sensor gets a dashed halo
    /// in its affiliation colour.
    /// </summary>
    private void DrawMarkers(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        var tracks = scene.Tracks;
        var stats = st.LastFrame;
        _vipRing.Reset(); _vipRingSelected.Reset(); _vipBrackets.Reset(); _inbound.Reset();
        foreach (var b in _ownSensor) b.Reset();

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (!t.Vip || !t.IsDrawn) continue;
            float sx = vp.ToScreenX(t.X), sy = vp.ToScreenY(t.Y), R = t.VipRadiusKm * vp.Scale;
            if (!float.IsFinite(sx) || !float.IsFinite(sy) || !(R > 0) || !float.IsFinite(R) || !CircleCrossesClip(sx, sy, R)) continue;
            Dasher.Circle(t.Selected ? _vipRingSelected : _vipRing, sx, sy, R, 3, 6, _clip);
            stats.VipRings++;
        }

        bool tags = false;
        for (int i = 0; i < st.ItemCount; i++)
        {
            ref var it = ref st.Items[i];
            if (it.Missile && tracks[it.TrackIndex].InboundToFriend)
            {
                _inbound.AddCircle(it.Sx, it.Sy, SymbolSizer.InboundRadius(it.S));
                stats.InboundMarks++;
            }
            if (it.OwnSensor)
            {
                var t = tracks[it.TrackIndex];
                Dasher.Circle(_ownSensor[(int)t.Affiliation], it.Sx, it.Sy, SymbolSizer.OwnSensorRadius(it.S), 2, 2, _clip);
                stats.OwnSensorMarks++;
            }
            if (!it.Vip) continue;
            stats.VipMarkers++;
            float r = SymbolSizer.VipBracketRadius(it.S);
            var oval = new SKRect(it.Sx - r, it.Sy - r, it.Sx + r, it.Sy + r);
            for (int q = 0; q < 4; q++) _vipBrackets.AddArc(oval, 45f + q * 90f - VipArcHalfDeg, 2 * VipArcHalfDeg);
            tags |= it.Selected;
        }

        if (!_inbound.IsEmpty)
        {
            // missiles inbound to a friendly: a solid 1.5 px ring in the critical red, under the arrowhead
            _stroke.Color = Theme.A(_theme.Crit, .9f);
            _stroke.StrokeWidth = 1.5f;
            c.DrawPath(_inbound, _stroke);
        }
        if (!_vipRing.IsEmpty) { Hairline(Theme.A(_theme.Accent, .16f)); _vipRing.Draw(c, _stroke); }
        if (!_vipRingSelected.IsEmpty) { Hairline(Theme.A(_theme.Accent, .45f)); _vipRingSelected.Draw(c, _stroke); }
        for (int a = 0; a < 4; a++)
            if (!_ownSensor[a].IsEmpty) { Hairline(Theme.A(_theme.ForAffiliation((Affiliation)a), .75f)); _ownSensor[a].Draw(c, _stroke); }
        if (!_vipBrackets.IsEmpty)
        {
            _stroke.Color = _theme.Accent;
            _stroke.StrokeWidth = 1.5f;
            c.DrawPath(_vipBrackets, _stroke);
        }
        if (tags)
        {
            _ink.Color = _theme.Accent;
            var tag = _text.Get(VipTag, SlotVip);
            for (int i = 0; i < st.ItemCount; i++)
            {
                ref var it = ref st.Items[i];
                if (!it.Vip || !it.Selected || tag.Blob is null) continue;
                // centred, canvas textBaseline 'bottom' at r + 2 above the symbol
                c.DrawText(tag.Blob, it.Sx - tag.Width / 2, it.Sy - SymbolSizer.VipBracketRadius(it.S) - 2 - _vipDescent, _ink);
                stats.VipTags++;
            }
        }
        st.LastFrame = stats;
    }

    /// <summary>Half the sweep of each VIP corner arc: the mockup's ±0.38 rad.</summary>
    private const float VipArcHalfDeg = 0.38f * 180f / MathF.PI;

    /// <summary>
    /// Orders (design/23 §4.10, mockup <c>drawOrders()</c>). Shared geometry is drawn once: a route as a bright path plus a
    /// faint leg from each unit to its next point; a racetrack as a closed stadium with a direction arrow; a MARSHAL ring
    /// astern of each base with RTB traffic. Per-unit accent dashed lines: move (plus a diamond), attack, hunt (to the
    /// current target), defend (to the protected unit) and RTB while inbound (to the marshal point). When more than 6
    /// orders are active, of any kind, the per-unit lines fade so a big group does not paint a thick band.
    /// </summary>
    private void DrawOrders(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        var tracks = scene.Tracks;
        int active = 0;
        foreach (var o in scene.Orders)
            if (UnitIndex(st, tracks, o.TrackId) >= 0) active++;
        float la = active > 6 ? .28f : .75f;
        var stats = st.LastFrame;
        stats.ActiveOrders = active;
        stats.OrderLineAlpha = la;
        st.LastFrame = stats;
        if (active == 0) return;

        var accent = _theme.Accent;
        st.RoutesDrawn.Clear(); st.PatternsDrawn.Clear(); st.MarshalBases.Clear();
        _routeLegs.Reset(); _orderLines.Reset(); _orderMarks.Reset(); _racetracks.Reset(); _arrows.Reset(); _marshal.Reset();
        _routes.Rewind(); _routeEnds.Rewind();

        // drawn routes: the shared path once, bright; each unit a faint line to its next point
        foreach (var o in scene.Orders)
        {
            if (o.Type != OrderType.Route) continue;
            int ui = UnitIndex(st, tracks, o.TrackId);
            if (ui < 0) continue;
            var route = FindRoute(scene, o.RouteId);
            if (route is null || route.Points.Count == 0) continue;
            var u = tracks[ui];
            var nx = route.Points[Math.Clamp(o.RouteIndex, 0, route.Points.Count - 1)];
            Dasher.Line(_routeLegs, vp.ToScreenX(u.X), vp.ToScreenY(u.Y), vp.ToScreenX(nx.X + o.RouteOffset.X), vp.ToScreenY(nx.Y + o.RouteOffset.Y), 2, 4, _clip);
            if (!st.RoutesDrawn.Add(route.Id)) continue;
            for (int i = 0; i < route.Points.Count; i++)
            {
                float sx = vp.ToScreenX(route.Points[i].X), sy = vp.ToScreenY(route.Points[i].Y);
                if (i == 0) _routes.MoveTo(sx, sy); else _routes.LineTo(sx, sy);
            }
            var end = route.Points[^1];
            _routeEnds.AddCircle(vp.ToScreenX(end.X), vp.ToScreenY(end.Y), 5);
        }
        // every shared route in one stroke, every end ring in another (one draw call each, however many routes)
        if (!_routes.IsEmpty)
        {
            _stroke.Color = Theme.A(accent, .8f);
            _stroke.StrokeWidth = 1.6f;
            c.DrawPath(_routes, _stroke);
            ThinLine(Theme.A(accent, .8f), 1.2f);
            c.DrawPath(_routeEnds, _stroke);
        }
        if (!_routeLegs.IsEmpty) { ThinLine(Theme.A(accent, .25f), 1.2f); _routeLegs.Draw(c, _stroke); }

        // racetracks, each shared pattern once; and a MARSHAL ring for each base with RTB traffic
        foreach (var o in scene.Orders)
        {
            if (o.Type is not (OrderType.Racetrack or OrderType.Rtb) || UnitIndex(st, tracks, o.TrackId) < 0) continue;
            if (o.Type == OrderType.Racetrack)
            {
                var rt = FindRacetrack(scene, o.PatternId);
                if (rt is null || !st.PatternsDrawn.Add(rt.Id)) continue;
                AddRacetrack(rt, vp);
                stats.Racetracks++;
            }
            else
            {
                int bi = UnitIndex(st, tracks, o.TargetTrackId);
                if (bi < 0 || !st.MarshalBases.Add(o.TargetTrackId)) continue;
                var (mx, my) = MarshalPoint(tracks[bi], o.MarshalAsternKm);
                float qx = vp.ToScreenX(mx), qy = vp.ToScreenY(my), R = MarshalRadius(vp.Scale);
                Dasher.Circle(_marshal, qx, qy, R, 2, 3, _clip);
                _ink.Color = Theme.A(accent, .8f);
                var label = _text.Get(MarshalLabel, SlotMarshal);
                if (label.Blob is not null) c.DrawText(label.Blob, qx - label.Width / 2, qy - R - 4, _ink);
                stats.MarshalRings++;
            }
        }
        if (!_racetracks.IsEmpty)
        {
            ThinLine(Theme.A(accent, .7f), 1.4f);
            c.DrawPath(_racetracks, _stroke);
            _fill.Color = Theme.A(accent, .8f);
            c.DrawPath(_arrows, _fill);
        }
        if (!_marshal.IsEmpty)
        {
            ThinLine(Theme.A(accent, .55f), 1.2f);
            _marshal.Draw(c, _stroke);
        }

        // per-unit lines
        foreach (var o in scene.Orders)
        {
            if (o.Type is OrderType.Route or OrderType.Racetrack) continue;
            int ui = UnitIndex(st, tracks, o.TrackId);
            if (ui < 0) continue;
            var u = tracks[ui];
            float ax = vp.ToScreenX(u.X), ay = vp.ToScreenY(u.Y), bx, by;
            switch (o.Type)
            {
                case OrderType.Move:
                    bx = vp.ToScreenX(o.Destination.X); by = vp.ToScreenY(o.Destination.Y);
                    break;
                case OrderType.Rtb:
                {
                    if (o.RtbPhase != RtbPhase.Inbound) continue; // in the stack or landing: the ring says enough
                    int bi = UnitIndex(st, tracks, o.TargetTrackId);
                    if (bi < 0) continue;
                    var (mx, my) = MarshalPoint(tracks[bi], o.MarshalAsternKm);
                    bx = vp.ToScreenX(mx); by = vp.ToScreenY(my);
                    break;
                }
                default: // Attack, Hunt (the current target; none yet = no line), Defend (the protected unit)
                {
                    int ti = o.TargetTrackId == 0 ? -1 : UnitIndex(st, tracks, o.TargetTrackId);
                    if (ti < 0) continue;
                    bx = vp.ToScreenX(tracks[ti].X); by = vp.ToScreenY(tracks[ti].Y);
                    break;
                }
            }
            Dasher.Line(_orderLines, ax, ay, bx, by, 5, 4, _clip);
            stats.OrderLines++;
            if (o.Type == OrderType.Move)
            {
                _orderMarks.MoveTo(bx - 5, by); _orderMarks.LineTo(bx, by - 5); _orderMarks.LineTo(bx + 5, by); _orderMarks.LineTo(bx, by + 5); _orderMarks.Close();
            }
        }
        ThinLine(Theme.A(accent, la), 1.2f);
        _orderLines.Draw(c, _stroke);
        if (!_orderMarks.IsEmpty) c.DrawPath(_orderMarks, _stroke);
        st.LastFrame = stats;
    }

    private const string MarshalLabel = "MARSHAL";

    /// <summary>Index of a drawn (visible and detected) track by id, or -1.</summary>
    private static int UnitIndex(RenderState st, List<PlotTrack> tracks, int id) =>
        st.IndexById.TryGetValue(id, out int i) && tracks[i].IsDrawn ? i : -1;

    /// <summary>The marshal point: <paramref name="asternKm"/> behind the base along its heading (mockup <c>marshalPt</c>).</summary>
    internal static (float X, float Y) MarshalPoint(PlotTrack b, float asternKm)
    {
        float h = b.HeadingDeg * (MathF.PI / 180f);
        if (!float.IsFinite(h)) h = 0f;
        return (b.X - MathF.Sin(h) * asternKm, b.Y - MathF.Cos(h) * asternKm);
    }

    /// <summary>The MARSHAL ring's radius: the 3.5 km stack orbit, at least 6 px.</summary>
    internal static float MarshalRadius(float scale) => MathF.Max(6f, 3.5f * scale);

    private static PlotRacetrack? FindRacetrack(PlotScene scene, int id)
    {
        var list = scene.Racetracks;
        for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return list[i];
        return null;
    }

    private const int RtLegSteps = 10, RtTurnSteps = 12; // the mockup's trackPts K1, K2

    /// <summary>Points in one pattern: 11 per leg, 11 per turn (44, as the mockup).</summary>
    internal const int RacetrackPoints = 2 * (RtLegSteps + 1) + 2 * (RtTurnSteps - 1);

    /// <summary>
    /// Point <paramref name="k"/> of the mockup's <c>trackPts(a, b, r)</c>, in km: the inbound leg A→B offset to one side
    /// by half the width, a half circle round B, the outbound leg back towards A on the other side, a half circle round A.
    /// Right-hand turns by default; <see cref="PlotRacetrack.TurnRight"/> = false mirrors the pattern.
    /// </summary>
    internal static (float X, float Y) RacetrackPoint(PlotRacetrack rt, int k)
    {
        float ux = rt.B.X - rt.A.X, uy = rt.B.Y - rt.A.Y, L = MathF.Sqrt(ux * ux + uy * uy);
        if (!(L > 1e-6f)) L = 1f;
        float s = rt.TurnRight ? 1f : -1f, r = rt.WidthKm / 2f;
        float nx = -uy / L * s, ny = ux / L * s, an = MathF.Atan2(ny, nx);
        if (k <= RtLegSteps)
        {
            float t = k / (float)RtLegSteps;
            return (rt.A.X + nx * r + ux * t, rt.A.Y + ny * r + uy * t);
        }
        k -= RtLegSteps;
        if (k < RtTurnSteps)
        {
            float th = an - s * MathF.PI * k / RtTurnSteps;
            return (rt.B.X + MathF.Cos(th) * r, rt.B.Y + MathF.Sin(th) * r);
        }
        k -= RtTurnSteps;
        if (k <= RtLegSteps)
        {
            float t = k / (float)RtLegSteps;
            return (rt.B.X - nx * r - ux * t, rt.B.Y - ny * r - uy * t);
        }
        k -= RtLegSteps;
        float th2 = an + MathF.PI - s * MathF.PI * k / RtTurnSteps;
        return (rt.A.X + MathF.Cos(th2) * r, rt.A.Y + MathF.Sin(th2) * r);
    }

    private void AddRacetrack(PlotRacetrack rt, in Viewport vp)
    {
        for (int k = 0; k < RacetrackPoints; k++)
        {
            var (x, y) = RacetrackPoint(rt, k);
            float sx = vp.ToScreenX(x), sy = vp.ToScreenY(y);
            if (k == 0) _racetracks.MoveTo(sx, sy); else _racetracks.LineTo(sx, sy);
        }
        _racetracks.Close();
        // direction arrow on the inbound leg (the mockup: points 5 -> 6): tip 6 px ahead, base corners 5 px at ±2.5 rad
        var (x5, y5) = RacetrackPoint(rt, 5);
        var (x6, y6) = RacetrackPoint(rt, 6);
        float qx = vp.ToScreenX(x5), qy = vp.ToScreenY(y5);
        float ang = MathF.Atan2(vp.ToScreenY(y6) - qy, vp.ToScreenX(x6) - qx);
        _arrows.MoveTo(qx + MathF.Cos(ang) * 6, qy + MathF.Sin(ang) * 6);
        _arrows.LineTo(qx + MathF.Cos(ang + 2.5f) * 5, qy + MathF.Sin(ang + 2.5f) * 5);
        _arrows.LineTo(qx + MathF.Cos(ang - 2.5f) * 5, qy + MathF.Sin(ang - 2.5f) * 5);
        _arrows.Close();
    }

    private static PlotRoute? FindRoute(PlotScene scene, int id)
    {
        var routes = scene.Routes;
        for (int i = 0; i < routes.Count; i++) if (routes[i].Id == id) return routes[i];
        return null;
    }

    private void DrawTracks(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        var tracks = scene.Tracks;
        var opt = st.Options;
        foreach (var p in _leaders) p.Reset();
        foreach (var p in _stalks) p.Reset();
        foreach (var p in _dots) p.Reset();
        foreach (var p in _mslFill) p.Reset();
        foreach (var p in _mslStroke) p.Reset();

        // pass 1: batched lines that sit under the symbols (speed leaders, altitude stalks)
        for (int i = 0; i < st.ItemCount; i++)
        {
            ref var it = ref st.Items[i];
            var t = tracks[it.TrackIndex];
            if (opt.AltitudeStalks && t.Domain == TrackDomain.Air && !it.Missile)
            {
                // altitude stalk: length tracks altitude so even dots carry it
                float len = MathF.Min(16f, 2f + t.AltitudeM / 900f);
                if (len > 0)
                {
                    float y0 = it.Sy + (it.Tier == SymbolTier.Dot ? 2f : it.S * .35f);
                    _stalks[Theme.AltitudeBand(t.AltitudeM)].Add(it.Sx, y0, it.Sx, y0 + len);
                }
            }
            if (it.Tier is SymbolTier.Frame or SymbolTier.Full && t.SpeedMps > 20f)
            {
                // speed leader: 45 s of travel
                float h = t.HeadingDeg * (MathF.PI / 180f), dx = MathF.Sin(h), dy = -MathF.Cos(h);
                float len = MathF.Max(it.S * .7f, MathF.Min(46f, t.SpeedMps / 1000f * 45f * vp.Scale));
                float r0 = it.S * .5f;
                _leaders[(int)t.Affiliation].Add(it.Sx + dx * r0, it.Sy + dy * r0, it.Sx + dx * (r0 + len), it.Sy + dy * (r0 + len));
            }
        }
        for (int b = 0; b < 5; b++)
            if (!_stalks[b].IsEmpty) { Hairline(Theme.A(_theme.ForAltitudeBand(b), .6f)); _stalks[b].Draw(c, _stroke); }
        for (int a = 0; a < 4; a++)
            if (!_leaders[a].IsEmpty) { Hairline(Theme.A(_theme.ForAffiliation((Affiliation)a), .6f)); _leaders[a].Draw(c, _stroke); }

        // pass 2: symbols, big first (small marks on top)
        var baseMatrix = c.TotalMatrix;
        if (UseSprites && baseMatrix.SkewX == 0 && baseMatrix.SkewY == 0 && baseMatrix.Persp0 == 0 && baseMatrix.Persp1 == 0 &&
            baseMatrix.ScaleX > 0 && MathF.Abs(baseMatrix.ScaleX - baseMatrix.ScaleY) < 1e-3f)
        {
            DrawSymbolsFromSprites(c, tracks, st, baseMatrix);
            return;
        }

        // vector fallback (rotated or skewed canvas): frames per track, dots and missiles batched on top
        var stats = st.LastFrame;
        stats.TracksVisible = st.ItemCount;
        for (int k = 0; k < st.ItemCount; k++)
        {
            ref var it = ref st.Items[st.Order[k]];
            var t = tracks[it.TrackIndex];
            var col = _theme.ForAffiliation(t.Affiliation);
            switch (it.Tier)
            {
                case SymbolTier.Dot:
                {
                    stats.Dots++;
                    float r = SymbolSizer.DotRadius(it.S);
                    var p = _dots[(int)t.Affiliation];
                    if (t.Affiliation == Affiliation.Hostile && !it.Missile)
                    {
                        float q = r + .6f;
                        p.MoveTo(it.Sx, it.Sy - q); p.LineTo(it.Sx + q, it.Sy); p.LineTo(it.Sx, it.Sy + q); p.LineTo(it.Sx - q, it.Sy); p.Close();
                    }
                    else if (t.Affiliation is Affiliation.Unknown or Affiliation.Neutral)
                        p.AddRect(new SKRect(it.Sx - r, it.Sy - r, it.Sx + r, it.Sy + r));
                    else p.AddCircle(it.Sx, it.Sy, r);
                    break;
                }
                case SymbolTier.Missile:
                {
                    stats.Missiles++;
                    // arrowhead along the velocity vector; hostile filled, own outlined
                    float L = MathF.Max(SymbolSizer.MissilePx, it.S), w = L * .42f;
                    float h = t.HeadingDeg * (MathF.PI / 180f), ux = MathF.Sin(h), uy = -MathF.Cos(h); // forward
                    float vx = -uy, vy = ux; // right
                    var p = t.Affiliation == Affiliation.Friend ? _mslStroke[(int)t.Affiliation] : _mslFill[(int)t.Affiliation];
                    p.MoveTo(it.Sx + ux * L * .6f, it.Sy + uy * L * .6f);
                    p.LineTo(it.Sx - ux * L * .45f + vx * w, it.Sy - uy * L * .45f + vy * w);
                    p.LineTo(it.Sx - ux * L * .2f, it.Sy - uy * L * .2f);
                    p.LineTo(it.Sx - ux * L * .45f - vx * w, it.Sy - uy * L * .45f - vy * w);
                    p.Close();
                    break;
                }
                default:
                    if (it.Tier == SymbolTier.Full) stats.FullSymbols++; else stats.Frames++;
                    DrawFrame(c, in baseMatrix, ref it, t, col);
                    break;
            }
        }
        st.LastFrame = stats;

        for (int a = 0; a < 4; a++)
        {
            var col = _theme.ForAffiliation((Affiliation)a);
            if (!_dots[a].IsEmpty) { _fill.Color = col; c.DrawPath(_dots[a], _fill); }
            if (!_mslFill[a].IsEmpty) { _fill.Color = col; c.DrawPath(_mslFill[a], _fill); }
            if (!_mslStroke[a].IsEmpty) { _stroke.Color = col; _stroke.StrokeWidth = 1.2f; c.DrawPath(_mslStroke[a], _stroke); }
        }
    }

    /// <summary>
    /// Every symbol is a cached device-pixel sprite blitted at a whole-pixel position, in strict big-first order
    /// (the mockup's draw order). Health bars are drawn in device space between the blits.
    /// </summary>
    private void DrawSymbolsFromSprites(SKCanvas c, List<PlotTrack> tracks, RenderState st, SKMatrix bm)
    {
        float k = bm.ScaleX, tx = bm.TransX, ty = bm.TransY;
        _sprites.SetScale(k);
        var stats = st.LastFrame;
        stats.TracksVisible = st.ItemCount;
        c.ResetMatrix();
        for (int n = 0; n < st.ItemCount; n++)
        {
            ref var it = ref st.Items[st.Order[n]];
            var t = tracks[it.TrackIndex];
            Sprite sp;
            switch (it.Tier)
            {
                case SymbolTier.Dot:
                    stats.Dots++;
                    sp = _sprites.Dot(t.Affiliation, it.Missile, SymbolSizer.DotRadius(it.S));
                    break;
                case SymbolTier.Missile:
                    stats.Missiles++;
                    // arrowhead along the velocity vector; hostile filled, own outlined
                    sp = _sprites.Missile(t.Affiliation, MathF.Max(SymbolSizer.MissilePx, it.S), t.HeadingDeg);
                    break;
                default:
                    if (it.Tier == SymbolTier.Full) stats.FullSymbols++; else stats.Frames++;
                    sp = _sprites.Frame(t.Affiliation, t.Domain, it.Tier == SymbolTier.Full, t.Health is >= 0 and < .6f, it.S, t.Kind);
                    break;
            }
            float dx = it.Sx * k + tx, dy = it.Sy * k + ty;
            c.DrawImage(sp.Image, MathF.Round(dx) - sp.OriginX, MathF.Round(dy) - sp.OriginY);
            if (it.Tier is SymbolTier.Frame or SymbolTier.Full)
            {
                if (it.Badge)
                {
                    // control-state badge: M manual / O order (accent), A agent (friend blue)
                    var b = _sprites.Badge(t.Control);
                    c.DrawImage(b.Image, MathF.Round(dx + it.S * .5f * k) - b.OriginX, MathF.Round(dy - it.S * .5f * k) - b.OriginY);
                }
                if (t.Health is >= 0 and < 1f)
                {
                    float w = MathF.Max(16f, it.S), x = it.Sx - w / 2, y = it.Sy + it.S * .5f + 4;
                    _fill.Color = Theme.A(_theme.Ink0, .8f);
                    c.DrawRect(x * k + tx, y * k + ty, w * k, 3 * k, _fill);
                    _fill.Color = t.Health < .4f ? _theme.Crit : _theme.Warn;
                    c.DrawRect(x * k + tx, y * k + ty, w * t.Health * k, 3 * k, _fill);
                }
            }
        }
        c.SetMatrix(bm);
        st.LastFrame = stats;
    }

    private void DrawFrame(SKCanvas c, in SKMatrix baseMatrix, ref DrawItem it, PlotTrack t, SKColor col)
    {
        float r = it.S / 2f;
        var path = _symbols.Frame(t.Affiliation, t.Domain);
        var m = baseMatrix.PreConcat(SKMatrix.CreateScaleTranslation(r, r, it.Sx, it.Sy));
        c.SetMatrix(m);
        _fill.Color = Theme.A(col, t.Health is >= 0 and < .6f ? .32f : .16f);
        c.DrawPath(path, _fill);
        _stroke.Color = col;
        _stroke.StrokeWidth = (it.Tier == SymbolTier.Full ? 1.6f : 1.3f) / r;
        c.DrawPath(path, _stroke);
        c.SetMatrix(baseMatrix);
        _stroke.StrokeWidth = 1f;

        var k = KindInfo.Of(t.Kind);
        if (it.Tier == SymbolTier.Full && k.Glyph.Length > 0)
        {
            int px = Math.Clamp((int)MathF.Round(it.S * (k.Glyph.Length > 2 ? .28f : .36f)), 1, MaxGlyphPx);
            float gy = t.Domain == TrackDomain.Air ? it.S * .12f : t.Domain == TrackDomain.Subsurface ? -it.S * .12f : 0f;
            _ink.Color = col;
            DrawTextCentered(c, k.Glyph, SlotGlyphBase + px, it.Sx, it.Sy + gy, _ink);
        }

        if (it.Badge)
        {
            // control-state badge: M manual / O order (accent), A agent (friend blue)
            float bx = it.Sx + it.S * .5f, by = it.Sy - it.S * .5f;
            _fill.Color = t.Control == ControlState.Agent ? _theme.Friend : _theme.Accent;
            c.DrawCircle(bx, by, 5.5f, _fill);
            _ink.Color = _theme.Ink0;
            DrawTextCentered(c, t.Control switch { ControlState.Manual => "M", ControlState.Order => "O", _ => "A" }, SlotBadge, bx, by + .5f, _ink);
        }

        if (t.Health is >= 0 and < 1f)
        {
            float w = MathF.Max(16f, it.S), x = it.Sx - w / 2, y = it.Sy + it.S * .5f + 4;
            _fill.Color = Theme.A(_theme.Ink0, .8f);
            c.DrawRect(x, y, w, 3, _fill);
            _fill.Color = t.Health < .4f ? _theme.Crit : _theme.Warn;
            c.DrawRect(x, y, w * t.Health, 3, _fill);
        }
    }

    private void DrawPills(SKCanvas c, RenderState st)
    {
        foreach (var kv in st.Pills)
        {
            var acc = kv.Value;
            string label = PillText(acc.Missile, acc.N);
            var run = _text.Get(label, SlotPill);
            float w = run.Width + 12, x = acc.SumX / acc.N, y = acc.SumY / acc.N;
            var rr = new SKRect(x - w / 2, y - 9, x + w / 2, y + 9);
            _fill.Color = Theme.A(_theme.Ink0, .85f);
            c.DrawRoundRect(rr, 9, 9, _fill);
            _stroke.Color = _theme.Hostile; _stroke.StrokeWidth = 1f;
            c.DrawRoundRect(rr, 9, 9, _stroke);
            _ink.Color = _theme.Hostile;
            DrawTextCentered(c, label, SlotPill, x, y + .5f, _ink);
        }
    }

    private void DrawEvents(SKCanvas c, PlotScene scene, in Viewport vp)
    {
        double now = scene.Time;
        var ev = scene.Events;
        _stroke.StrokeWidth = 1.4f;
        for (int i = ev.Count - 1; i >= 0; i--)
        {
            var e = ev[i];
            double age = now - e.Time;
            if (age < 0) continue;
            if (age > 6) break;
            float k = (float)(age / (e.Kind == PlotEventKind.Kill ? 6.0 : 2.5));
            if (k > 1) continue;
            float x = vp.ToScreenX(e.X), y = vp.ToScreenY(e.Y);
            switch (e.Kind)
            {
                case PlotEventKind.Intercept:
                    _stroke.Color = Theme.A(_theme.Ok, 1 - k); c.DrawCircle(x, y, 3 + k * 14, _stroke); break;
                case PlotEventKind.Ciws:
                    _stroke.Color = Theme.A(_theme.Warn, 1 - k); c.DrawCircle(x, y, 2 + k * 8, _stroke); break;
                case PlotEventKind.Impact:
                    _stroke.Color = Theme.A(_theme.Crit, 1 - k);
                    _scratch.Reset();
                    for (int j = 0; j < 8; j++)
                    {
                        float a = j * MathF.PI / 4, ca = MathF.Cos(a), sa = MathF.Sin(a);
                        _scratch.MoveTo(x + ca * (4 + k * 6), y + sa * (4 + k * 6));
                        _scratch.LineTo(x + ca * (9 + k * 16), y + sa * (9 + k * 16));
                    }
                    c.DrawPath(_scratch, _stroke);
                    break;
                case PlotEventKind.Kill:
                    _stroke.Color = Theme.A(_theme.Text, .9f * (1 - k));
                    c.DrawLine(x - 5, y - 5, x + 5, y + 5, _stroke);
                    c.DrawLine(x + 5, y - 5, x - 5, y + 5, _stroke);
                    break;
                case PlotEventKind.Miss:
                    _stroke.Color = Theme.A(_theme.TextDim, 1 - k); c.DrawCircle(x, y, 2 + k * 5, _stroke); break;
            }
        }
    }

    private void DrawLabels(SKCanvas c, PlotScene scene, in Viewport vp, RenderState st)
    {
        LabelPlacer.Place(scene, vp, st, _labelGrid, _fonts[SlotLabel]);
        var tracks = scene.Tracks;
        _labelBacks.Reset();
        for (int i = 0; i < st.LabelBoxCount; i++)
        {
            var b = st.LabelBoxes[i];
            _labelBacks.AddRect(new SKRect(b.X, b.Y, b.Right, b.Bottom));
        }
        _fill.Color = Theme.A(_theme.Sea, .7f);
        c.DrawPath(_labelBacks, _fill);
        float mid = _middle[SlotLabel];
        for (int i = 0; i < st.LabelBoxCount; i++)
        {
            var b = st.LabelBoxes[i];
            ref var it = ref st.Items[st.LabelOwner[i]];
            var t = tracks[it.TrackIndex];
            _ink.Color = it.Selected ? _theme.Accent : t.Affiliation switch
            {
                Affiliation.Friend => Theme.A(_theme.Friend, .95f),
                Affiliation.Unknown => Theme.A(_theme.Unknown, .9f),
                Affiliation.Hostile => Theme.A(_theme.Hostile, .9f),
                _ => Theme.A(_theme.Neutral, .9f),
            };
            var run = it.Visual.Label;
            if (run?.Blob is { } blob)
                c.DrawText(blob, b.X + LabelPlacer.BoxPadX, b.Y + LabelPlacer.LabelHeight / 2 + .5f + mid, _ink);
        }
    }

    private void DrawSelection(SKCanvas c, PlotScene scene, RenderState st)
    {
        _brackets.Reset();
        int hoverItem = -1;
        for (int i = 0; i < st.ItemCount; i++)
        {
            ref var it = ref st.Items[i];
            if (it.Id == scene.HoverTrackId && !it.Selected) hoverItem = i;
            if (!it.Selected) continue;
            float r = MathF.Max(8f, it.S / 2 + 5), cc = r * .45f;
            for (int q = 0; q < 4; q++)
            {
                float sx = q is 0 or 3 ? -1 : 1, sy = q < 2 ? -1 : 1;
                _brackets.MoveTo(it.Sx + sx * r, it.Sy + sy * (r - cc));
                _brackets.LineTo(it.Sx + sx * r, it.Sy + sy * r);
                _brackets.LineTo(it.Sx + sx * (r - cc), it.Sy + sy * r);
            }
        }
        _stroke.Color = _theme.Accent;
        _stroke.StrokeWidth = 1.5f;
        c.DrawPath(_brackets, _stroke);
        if (hoverItem >= 0)
        {
            ref var h = ref st.Items[hoverItem];
            Hairline(Theme.A(_theme.Accent, .6f));
            c.DrawCircle(h.Sx, h.Sy, MathF.Max(7f, h.S / 2 + 4), _stroke);
        }
    }

    private void DrawScale(SKCanvas c, in Viewport vp)
    {
        int km = NiceStep(vp.Scale, 90, 100, 30);
        float px = km * vp.Scale, x = 14, y = vp.Height - 66;
        Hairline(_theme.TextDim);
        _scratch.Reset();
        _scratch.MoveTo(x, y); _scratch.LineTo(x + px, y);
        _scratch.MoveTo(x, y - 4); _scratch.LineTo(x, y + 4);
        _scratch.MoveTo(x + px, y - 4); _scratch.LineTo(x + px, y + 4);
        // north arrow
        float nx = vp.Width - 24, ny = 72;
        _scratch.MoveTo(nx, ny + 10); _scratch.LineTo(nx, ny - 10); _scratch.LineTo(nx - 4, ny - 4);
        _scratch.MoveTo(nx, ny - 10); _scratch.LineTo(nx + 4, ny - 4);
        c.DrawPath(_scratch, _stroke);
        _ink.Color = _theme.TextDim;
        DrawText(c, KmText(km), SlotSmall, x + px + 6, y + 3, _ink);
        var n = _text.Get("N", SlotSmall);
        if (n.Blob is not null) c.DrawText(n.Blob, nx - n.Width / 2, ny - 14, _ink);
    }

    // ---------------------------------------------------------------- text helpers

    private void DrawText(SKCanvas c, string text, int slot, float x, float baselineY, SKPaint paint)
    {
        var run = _text.Get(text, slot);
        if (run.Blob is not null) c.DrawText(run.Blob, x, baselineY, paint);
    }

    private void DrawTextCentered(SKCanvas c, string text, int slot, float cx, float cy, SKPaint paint)
    {
        var run = _text.Get(text, slot);
        if (run.Blob is not null) c.DrawText(run.Blob, cx - run.Width / 2, cy + _middle[slot], paint);
    }

    private string KmText(int km)
    {
        if (!_kmText.TryGetValue(km, out var s))
        {
            if (_kmText.Count > 4096) _kmText.Clear();
            s = km.ToString(CultureInfo.InvariantCulture) + " km";
            _kmText[km] = s;
        }
        return s;
    }

    private string PillText(bool missile, int n)
    {
        int key = missile ? n : -n;
        if (!_pillText.TryGetValue(key, out var s))
        {
            s = (missile ? "MSL ×" : "UAV ×") + n.ToString(CultureInfo.InvariantCulture);
            _pillText[key] = s;
        }
        return s;
    }

    private static SKFont NewFont(SKTypeface tf, float size) =>
        new(tf, size) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Slight };

    private static SKPath[] NewPaths(int n)
    {
        var a = new SKPath[n];
        for (int i = 0; i < n; i++) a[i] = new SKPath();
        return a;
    }

    public void Dispose()
    {
        _text.Dispose();
        _sprites.Dispose();
        _symbols.Dispose();
        foreach (var f in _fonts) f.Dispose();
        foreach (var arr in new[] { _trailOld, _trailNew, _dots, _mslFill, _mslStroke })
            foreach (var p in arr) p.Dispose();
        _gridLo.Dispose(); _gridHi.Dispose(); _scratch.Dispose(); _labelBacks.Dispose(); _brackets.Dispose();
        _orderMarks.Dispose(); _racetracks.Dispose(); _arrows.Dispose(); _vipBrackets.Dispose(); _inbound.Dispose();
        _fill.Dispose(); _stroke.Dispose(); _ink.Dispose();
    }
}
