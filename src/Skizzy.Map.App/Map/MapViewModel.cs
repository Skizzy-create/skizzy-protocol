using Skizzy.Map.App.Plot;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Theme;
using Skizzy.Map.Data;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Map;

/// <summary>Plot filter chips (second toolbar row): what the plot itself shows.</summary>
public enum PlotFilter { All, Air, Missiles, Ships, Inbound, Hostile, Friendly }

/// <summary>
/// One map window: its own scenes, render state, renderer and viewport (the renderer is per window: it is not
/// thread-safe and keeps per-window caches), its view side (Blue fog, Red fog, God), layer options and filter.
/// With <see cref="AppState.ThreadedPlots"/> the window's plot renders on its own thread (see <see cref="PlotSurface"/>).
/// The viewport animates: wheel zoom eases toward the target scale around the cursor, Fit eases to the box.
/// </summary>
public sealed class MapViewModel : IDisposable
{
    public const float MinScale = .35f, MaxScale = 220f;

    private readonly AppState _app;
    private float _cx = 90, _cy, _scale = 3;
    private float _tcx = 90, _tcy, _tscale = 3;
    private (float Wx, float Wy, float Sx, float Sy)? _anchor;
    private bool _animating;

    public MapViewModel(AppState app, string id, string title)
    {
        _app = app;
        Id = id;
        Title = title;
        Surface = new PlotSurface(app.ThreadedPlots) { Renderer = new PlotRenderer(app.Palette.ToRenderTheme()) };
        _syncs = Surface.Scenes.Select(s => new SceneSync(s)).ToArray();
    }

    private readonly SceneSync[] _syncs;

    public string Id { get; }
    public string Title { get; set; }
    public PlotSurface Surface { get; }
    /// <summary>The scene sync of the scene published last (counts, tracks by id). UI thread only.</summary>
    public SceneSync Sync => _syncs[Surface.LastWritten];

    /// <summary>Layer and size options (UI thread; the plot copies them every frame).</summary>
    public PlotOptions Options => Surface.Options;

    /// <summary>Set by the view: repaint the plot control.</summary>
    public Action? Invalidate { get; set; }

    /// <summary>Set by the map view: cancels a racetrack or route being drawn (Esc). True when there was one.</summary>
    public Func<bool>? CancelGesture { get; set; }

    public int HoverId { get; set; }

    /// <summary>Logical size of the plot control.</summary>
    public float Width { get; private set; } = 1280;
    public float Height { get; private set; } = 720;

    public ViewSide View
    {
        get => _syncs[0].View;
        set { foreach (var s in _syncs) s.View = value; Changed?.Invoke(); }
    }

    private PlotFilter _filter = PlotFilter.All;
    public PlotFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            var f = FilterFor(value);
            foreach (var s in _syncs) s.Filter = f;
            Changed?.Invoke();
        }
    }

    public bool ShowLegend { get; set; } = true;

    /// <summary>Raised when view side, filter or options change (toolbars re-sync).</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public static Func<TrackInfo, bool>? FilterFor(PlotFilter f) => f switch
    {
        PlotFilter.Air => t => t.Domain == TrackDomain.Air && !t.IsMissile,
        PlotFilter.Missiles => t => t.IsMissile,
        PlotFilter.Ships => t => t.Domain != TrackDomain.Air,
        PlotFilter.Inbound => t => t.Side == Side.Red && (t.Kind == TrackKind.AntiShipMissile || t.Kind == TrackKind.Uav),
        PlotFilter.Hostile => t => t.Side == Side.Red,
        PlotFilter.Friendly => t => t.Side == Side.Blue,
        _ => null,
    };

    public Viewport Viewport => new(_cx, _cy, _scale, (int)Width, (int)Height);
    public float Scale => _scale;
    public float CenterX => _cx;
    public float CenterY => _cy;

    public void SetSize(float w, float h)
    {
        Width = Math.Max(1, w);
        Height = Math.Max(1, h);
    }

    public (float X, float Y) ToWorld(float sx, float sy) => (_cx + (sx - Width / 2) / _scale, _cy - (sy - Height / 2) / _scale);

    public (float X, float Y) ToScreen(float x, float y) => (Width / 2 + (x - _cx) * _scale, Height / 2 - (y - _cy) * _scale);

    // ------------------------------------------------------------ navigation

    /// <summary>Zoom by k about a screen point (the mockup's zoomAt), eased unless <paramref name="animate"/> is false.</summary>
    public void ZoomAt(float sx, float sy, float k, bool animate = true)
    {
        if (!_animating || _anchor is null || MathF.Abs(_anchor.Value.Sx - sx) > 2 || MathF.Abs(_anchor.Value.Sy - sy) > 2)
        {
            var (wx, wy) = ToWorld(sx, sy);
            _anchor = (wx, wy, sx, sy);
            if (!_animating) _tscale = _scale;
        }
        _tscale = Math.Clamp(_tscale * k, MinScale, MaxScale);
        _animating = true;
        if (!animate) Settle();
    }

    /// <summary>Drag pan: moves at once, cancelling any easing.</summary>
    public void PanBy(float dxPx, float dyPx)
    {
        _cx -= dxPx / _scale;
        _cy += dyPx / _scale;
        _tcx = _cx; _tcy = _cy; _tscale = _scale;
        _anchor = null;
        _animating = false;
    }

    public void SetView(float cx, float cy, float scale, bool animate = true)
    {
        _tcx = cx; _tcy = cy; _tscale = Math.Clamp(scale, MinScale, MaxScale);
        _anchor = null;
        _animating = true;
        if (!animate) Settle();
    }

    public void CenterOn(double x, double y, bool animate = true) => SetView((float)x, (float)y, Math.Max(_tscale, _scale), animate);

    /// <summary>Fit all drawn tracks within 400 km of the origin (the mockup's fitAll).</summary>
    public void FitAll(bool animate = true)
    {
        var pts = new List<(double X, double Y)>();
        var data = _app.Data;
        double t = data.ViewTime;
        foreach (var tr in data.Tracks)
        {
            if (!(data.InReplay ? tr.ExistsAt(t) : tr.Alive) || tr.DetectionFor(View) == TrackDetection.None) continue;
            var p = data.PositionOf(tr, t);
            if (Math.Abs(p.X) < 400 && Math.Abs(p.Y) < 400) pts.Add(p);
        }
        if (pts.Count == 0)
            foreach (var tr in data.Tracks) if (tr.Alive) pts.Add(data.PositionOf(tr, t)); // live host: no 400 km box
        FitPoints(pts, 60, animate);
    }

    /// <summary>Fit the tracks that pass the plot filter (Shift+Home).</summary>
    public void FitFilter(bool animate = true)
    {
        var data = _app.Data;
        var f = Sync.Filter;
        double t = data.ViewTime;
        var pts = data.Tracks.Where(tr => tr.Alive && tr.DetectionFor(View) != TrackDetection.None && (f?.Invoke(tr) ?? true))
            .Select(tr => data.PositionOf(tr, t)).ToList();
        FitPoints(pts, 60, animate);
    }

    /// <summary>F: fit the selection (or everything when nothing is selected).</summary>
    public void FitSelection(bool animate = true)
    {
        var data = _app.Data;
        double t = data.ViewTime;
        var pts = _app.Selection.Select(data.Get).Where(x => x is not null).Select(x => data.PositionOf(x!, t)).ToList();
        if (pts.Count == 0) { FitAll(animate); return; }
        FitPoints(pts, 80, animate, minExtentKm: pts.Count == 1 ? 30 : 8);
    }

    private void FitPoints(List<(double X, double Y)> pts, float maxScale, bool animate, double minExtentKm = 5)
    {
        if (pts.Count == 0) return;
        double a = double.MaxValue, b = double.MinValue, c = double.MaxValue, d = double.MinValue;
        foreach (var (x, y) in pts) { a = Math.Min(a, x); b = Math.Max(b, x); c = Math.Min(c, y); d = Math.Max(d, y); }
        float sc = (float)Math.Min((Width - 80) / Math.Max(minExtentKm, b - a), (Height - 160) / Math.Max(minExtentKm, d - c));
        SetView((float)(a + b) / 2, (float)(c + d) / 2, Math.Clamp(sc, MinScale, maxScale), animate);
    }

    private void Settle()
    {
        if (_anchor is { } an)
        {
            _scale = _tscale;
            _cx = an.Wx - (an.Sx - Width / 2) / _scale;
            _cy = an.Wy + (an.Sy - Height / 2) / _scale;
            _tcx = _cx; _tcy = _cy;
        }
        else { _cx = _tcx; _cy = _tcy; _scale = _tscale; }
        _anchor = null;
        _animating = false;
    }

    private void Animate(double dt)
    {
        if (!_animating) return;
        float a = (float)(1 - Math.Exp(-dt / 0.085));
        float ls = MathF.Log(_scale), lt = MathF.Log(_tscale);
        _scale = MathF.Exp(ls + (lt - ls) * a);
        if (_anchor is { } an)
        {
            _cx = an.Wx - (an.Sx - Width / 2) / _scale;
            _cy = an.Wy + (an.Sy - Height / 2) / _scale;
        }
        else
        {
            _cx += (_tcx - _cx) * a;
            _cy += (_tcy - _cy) * a;
        }
        bool done = MathF.Abs(lt - MathF.Log(_scale)) < 0.002f && (_anchor is not null || (MathF.Abs(_tcx - _cx) * _scale < 0.3f && MathF.Abs(_tcy - _cy) * _scale < 0.3f));
        if (done) Settle();
    }

    public bool IsAnimating => _animating;

    // ------------------------------------------------------------ per frame

    /// <summary>Last frame: ms waiting for a synchronous draw to release the scene, and ms syncing it (diagnostics).</summary>
    public double LastWaitMs { get; private set; }
    public double LastSyncMs { get; private set; }

    public void Frame(double dt)
    {
        Animate(dt);
        var s = Surface;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int i = s.BeginWrite();
        try
        {
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            LastWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            _syncs[i].Sync(_app.Data, _app.Selection, _app.SizeMul, HoverId);
            LastSyncMs = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        }
        finally
        {
            s.EndWrite(new Viewport(_cx, _cy, _scale, (int)MathF.Ceiling(Width), (int)MathF.Ceiling(Height)), (float)Math.Clamp(dt, 1 / 240.0, .25));
        }
        Invalidate?.Invoke();
    }

    /// <summary>
    /// Hit test against the last drawn frame (symbol boxes); 0 when nothing is under the point. A hover test does not
    /// wait for a plot frame being rendered: it keeps the current hover instead.
    /// </summary>
    public int HitTest(float sx, float sy, bool hover = false)
    {
        int hit = Surface.HitTest(sx, sy, Viewport, wait: !hover);
        return hit < 0 ? HoverId : hit;
    }

    /// <summary>Tracks whose symbol centre lies in a screen rectangle (box select).</summary>
    public List<int> TracksInRect(float x0, float y0, float x1, float y1)
    {
        var r = new List<int>();
        // the UI thread is the only writer of the scenes, so reading the last published one needs no lock
        foreach (var p in Surface.Scene.Tracks)
        {
            if (!p.IsDrawn) continue;
            var (sx, sy) = ToScreen(p.X, p.Y);
            if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1) r.Add(p.Id);
        }
        return r;
    }

    public void ApplyTheme(Palette p)
    {
        lock (Surface.Gate)
        {
            var old = Surface.Renderer;
            Surface.Renderer = new PlotRenderer(p.ToRenderTheme());
            Surface.State.SnapNextFrame = true;
            old?.Dispose();
        }
        Invalidate?.Invoke();
    }

    // ------------------------------------------------------------ persistence

    public MapConfig ToConfig()
    {
        var o = Options;
        return new MapConfig
        {
            Title = Title, View = View.ToString(), CenterX = _tcx, CenterY = _tcy, Scale = _tscale,
            Trails = o.Trails, Altitude = o.AltitudeStalks, TargetLines = o.TargetLines, Rings = o.Rings, Labels = o.Labels, Counts = o.Counts,
            Grid = o.Grid, Zones = o.Zones, Events = o.Events, ScaleBar = o.ScaleBar, SizeMode = o.SizeMode.ToString(), SizeScale = o.SizeScale,
            Missiles = o.Missiles, MissileTrails = o.MissileTrails,
            PlotFilter = Filter.ToString(), Legend = ShowLegend,
        };
    }

    public void Apply(MapConfig c)
    {
        Title = string.IsNullOrWhiteSpace(c.Title) ? Title : c.Title;
        var o = Options;
        o.Trails = c.Trails; o.AltitudeStalks = c.Altitude; o.TargetLines = c.TargetLines; o.Rings = c.Rings; o.Labels = c.Labels; o.Counts = c.Counts;
        o.Grid = c.Grid; o.Zones = c.Zones; o.Events = c.Events; o.ScaleBar = c.ScaleBar;
        o.Missiles = c.Missiles; o.MissileTrails = c.MissileTrails;
        o.SizeMode = Enum.TryParse<SizeMode>(c.SizeMode, out var sm) ? sm : SizeMode.Auto;
        o.SizeScale = (float)Math.Clamp(c.SizeScale, .6, 1.8);
        View = Enum.TryParse<ViewSide>(c.View, out var v) ? v : ViewSide.Blue;
        Filter = Enum.TryParse<PlotFilter>(c.PlotFilter, out var f) ? f : PlotFilter.All;
        ShowLegend = c.Legend;
        if (double.IsFinite(c.CenterX) && double.IsFinite(c.CenterY) && c.Scale > 0)
            SetView((float)c.CenterX, (float)c.CenterY, (float)c.Scale, animate: false);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Surface.Dispose(); // stops the plot thread first
        lock (Surface.Gate)
        {
            Surface.Renderer?.Dispose();
            Surface.Renderer = null;
        }
    }
}
