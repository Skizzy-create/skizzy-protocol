using System.Diagnostics;
using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Plot;

/// <summary>
/// What one plot control draws: the scene buffers, the per-window renderer and render state, and the viewport.
/// The renderer and <see cref="State"/> are used by one thread at a time, always under <see cref="Gate"/> (the render
/// core is not thread-safe). Two modes:
/// <list type="bullet">
/// <item><b>Synchronous</b> (headless tests, <c>--plot-sync</c>): one scene. The UI thread syncs it under
/// <see cref="Gate"/> and Avalonia's render thread draws it under <see cref="Gate"/>, so each waits for the other.</item>
/// <item><b>Threaded</b> (the desktop app): three scenes and a plot thread per map window. The UI thread fills a free
/// scene and publishes it without waiting; the plot thread renders the latest published scene into a CPU raster
/// bitmap; Avalonia's render thread only draws that bitmap, moved to the current viewport so pans and zooms follow
/// the pointer even when the plot thread is a frame behind.</item>
/// </list>
/// </summary>
public sealed class PlotSurface : IDisposable
{
    private readonly PlotScene[] _scenes;
    private readonly object _swap = new();
    private readonly object _frameLock = new();
    private readonly object _statsLock = new();
    private readonly Thread? _thread;
    private readonly AutoResetEvent? _signal;
    private readonly SKSurface?[] _bitmaps = new SKSurface?[2];
    private int _write, _ready = 1, _render = 2;
    private bool _dirty;
    private Viewport _readyView;
    private int _back;
    private SKImage? _front;
    private Viewport _frontView;
    private volatile float _deviceScale = 1;
    private long _lastBlit = Environment.TickCount64;

    /// <summary>A plot nobody has drawn for this long (a hidden tab, a minimised window) stops rendering, ms.</summary>
    public const int IdleAfterMs = 500;
    private volatile bool _disposed;
    private SKSurface? _raster;
    private FrameStats _stats;
    private FrameTimings _timings;
    private int _sprites;

    public PlotSurface(bool threaded = false)
    {
        Threaded = threaded;
        _scenes = threaded ? [new(), new(), new()] : [new()];
        if (!threaded) return;
        _signal = new AutoResetEvent(false);
        _thread = new Thread(Loop) { IsBackground = true, Name = "skizzy plot" };
        _thread.Start();
    }

    /// <summary>True when a plot thread renders this surface (see the class summary).</summary>
    public bool Threaded { get; }

    /// <summary>Held while the renderer and <see cref="State"/> are used (and, synchronous, while the scene is synced).</summary>
    public object Gate { get; } = new();

    /// <summary>The scene buffers: one when synchronous, three when threaded. A <c>SceneSync</c> per buffer fills them.</summary>
    public IReadOnlyList<PlotScene> Scenes => _scenes;

    /// <summary>Index of the scene the UI thread published last (read it, never write it).</summary>
    public int LastWritten { get; private set; }

    /// <summary>The scene the UI thread published last. Settable only when synchronous (tests).</summary>
    public PlotScene Scene
    {
        get => _scenes[LastWritten];
        set
        {
            if (Threaded) throw new InvalidOperationException("a threaded plot surface owns its scene buffers");
            _scenes[0] = value;
        }
    }

    public RenderState State { get; } = new();

    /// <summary>Layer and size options, owned by the UI thread; copied into <see cref="State"/> at the start of every frame.</summary>
    public PlotOptions Options { get; } = new();

    /// <summary>Synchronous: the viewport of the next and last draw (logical pixels of the control).</summary>
    public Viewport View { get; set; } = new(0, 0, 3, 1280, 720);

    /// <summary>The viewport the UI published last; the draw operation moves the last bitmap onto it.</summary>
    public Viewport UiView { get; private set; } = new(0, 0, 3, 1280, 720);

    /// <summary>The viewport the last rendered frame used (hit boxes are in its screen space).</summary>
    public Viewport DrawnView { get; private set; } = new(0, 0, 3, 1280, 720);

    /// <summary>The renderer that draws this surface. Swap it under <see cref="Gate"/>.</summary>
    public PlotRenderer? Renderer { get; set; }

    /// <summary>CPU time of the last plot render in ms, lock wait excluded (diagnostics).</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>Synchronous: how long the last draw waited for the UI thread's sync, ms.</summary>
    public double LastWaitMs { get; private set; }

    /// <summary>Threaded: how long Avalonia's render thread took to draw the last bitmap, ms.</summary>
    public double LastBlitMs { get; private set; }

    /// <summary>Plot frames rendered so far (diagnostics; the perf gate counts these).</summary>
    public long FramesDrawn { get; private set; }

    /// <summary>Set when the last draw found no Skia lease (a non-Skia backend).</summary>
    public bool NoSkia { get; internal set; }

    /// <summary>Last exception the plot thread caught while rendering (it keeps going), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Synchronous on a GPU window: render the plot into a CPU raster bitmap and draw that (default on). The plot is
    /// thousands of small anti-aliased paths, sprites and text runs: Skia's CPU rasteriser draws that several times
    /// faster than its GPU backend, and one bitmap upload is cheap (docs/M3-spikes.md §2).
    /// </summary>
    public static bool RasterOnGpu { get; set; } = true;

    /// <summary>How the last frame reached the window: "plot thread", "raster bitmap" or "direct canvas".</summary>
    public string Mode { get; private set; } = "none";

    /// <summary>Stats of the last rendered frame (safe from any thread).</summary>
    public (FrameStats Frame, FrameTimings Timings, int Sprites) Stats
    {
        get { lock (_statsLock) return (_stats, _timings, _sprites); }
    }

    // ------------------------------------------------------------ UI thread

    /// <summary>
    /// UI thread: returns the index of the scene to fill now. Synchronous, this takes <see cref="Gate"/> (waiting for
    /// a draw in progress); always pair it with <see cref="EndWrite"/> in a finally block.
    /// </summary>
    public int BeginWrite()
    {
        if (!Threaded) { Monitor.Enter(Gate); return 0; }
        return _write;
    }

    /// <summary>UI thread: publishes the scene filled since <see cref="BeginWrite"/> with its viewport.</summary>
    public void EndWrite(Viewport view, float frameSeconds)
    {
        UiView = view;
        if (!Threaded)
        {
            View = view with { Width = View.Width, Height = View.Height };
            State.FrameSeconds = frameSeconds;
            Monitor.Exit(Gate);
            return;
        }
        lock (_swap)
        {
            LastWritten = _write;
            (_write, _ready) = (_ready, _write);
            _readyView = view;
            _dirty = true;
        }
        _signal!.Set();
    }

    /// <summary>
    /// The track under a point of the current view (<paramref name="current"/>), from the last rendered frame's
    /// symbols; 0 for none. With <paramref name="wait"/> false (hover) it returns -1 instead of waiting for a frame
    /// being rendered.
    /// </summary>
    public int HitTest(float sx, float sy, Viewport current, bool wait)
    {
        if (wait) Monitor.Enter(Gate);
        else if (!Monitor.TryEnter(Gate)) return -1;
        try
        {
            var d = DrawnView;
            float x = d.ToScreenX(current.ToWorldX(sx)), y = d.ToScreenY(current.ToWorldY(sy));
            return State.HitTest(x, y);
        }
        finally { Monitor.Exit(Gate); }
    }

    // ------------------------------------------------------------ Avalonia's render thread

    /// <summary>Draws the plot onto the window's canvas: the plot thread's last bitmap, or a synchronous render.</summary>
    internal void DrawTo(SKCanvas canvas, bool gpu, float width, float height, Viewport current)
    {
        var (sx, sy) = DeviceScale(canvas);
        if (Threaded) { Blit(canvas, width, height, current, sx); return; }
        if (!(gpu && RasterOnGpu))
        {
            Mode = "direct canvas";
            Draw(canvas, width, height);
            return;
        }
        int pw = Math.Max(1, (int)MathF.Ceiling(width * sx)), ph = Math.Max(1, (int)MathF.Ceiling(height * sy));
        SKSurface? rs;
        lock (Gate)
        {
            if (_disposed) return;
            if (_raster is null || _raster.Canvas.DeviceClipBounds.Width != pw || _raster.Canvas.DeviceClipBounds.Height != ph)
            {
                _raster?.Dispose();
                _raster = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul));
            }
            rs = _raster;
        }
        if (rs is null) { Draw(canvas, width, height); return; }
        var rc = rs.Canvas;
        rc.ResetMatrix();
        rc.Scale(sx, sy);
        Draw(rc, width, height);
        using var img = rs.Snapshot();
        canvas.DrawImage(img, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        Mode = "raster bitmap";
    }

    /// <summary>Synchronous render of the scene (used directly by headless tests and the spike).</summary>
    internal void Draw(SKCanvas canvas, float width, float height)
    {
        long t0 = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            long t1 = Stopwatch.GetTimestamp();
            LastWaitMs = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            t0 = t1;
            var r = Renderer;
            if (r is null || _disposed) return;
            var vp = View with { Width = (int)MathF.Ceiling(width), Height = (int)MathF.Ceiling(height) };
            View = vp;
            CopyOptions(Options, State.Options);
            r.Render(canvas, _scenes[0], vp, State);
            DrawnView = vp;
            FramesDrawn++;
            PublishStats(r);
        }
        LastDrawMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void Blit(SKCanvas canvas, float width, float height, Viewport cur, float scale)
    {
        if (MathF.Abs(scale - _deviceScale) > .001f) _deviceScale = scale; // the plot thread sizes its bitmaps from this
        Volatile.Write(ref _lastBlit, Environment.TickCount64);
        long t0 = Stopwatch.GetTimestamp();
        lock (_frameLock)
        {
            var img = _front;
            if (img is null) return;
            var f = _frontView;
            float k = cur.Scale / f.Scale;
            float dx = width / 2 + (f.CenterX - cur.CenterX) * cur.Scale, dy = height / 2 - (f.CenterY - cur.CenterY) * cur.Scale;
            bool moved = MathF.Abs(k - 1) > 1e-4f || MathF.Abs(dx - f.Width / 2f) > .01f || MathF.Abs(dy - f.Height / 2f) > .01f
                || f.Width < width - .5f || f.Height < height - .5f;
            if (moved && Renderer?.Theme.Sea is { } sea)
            {
                using var bg = new SKPaint { Color = sea };
                canvas.DrawRect(0, 0, width, height, bg);
            }
            canvas.Save();
            canvas.Translate(dx, dy);
            canvas.Scale(k);
            canvas.Translate(-f.Width / 2f, -f.Height / 2f);
            canvas.DrawImage(img, new SKRect(0, 0, f.Width, f.Height), new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Restore();
        }
        LastBlitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Mode = "plot thread";
    }

    private static (float X, float Y) DeviceScale(SKCanvas canvas)
    {
        var m = canvas.TotalMatrix;
        float sx = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY), sy = MathF.Sqrt(m.ScaleY * m.ScaleY + m.SkewX * m.SkewX);
        return sx > 0 && sy > 0 ? (sx, sy) : (1, 1);
    }

    // ------------------------------------------------------------ plot thread

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (!_disposed)
        {
            _signal!.WaitOne();
            if (_disposed) break;
            Viewport vp;
            lock (_swap)
            {
                if (!_dirty) continue;
                (_render, _ready) = (_ready, _render);
                _dirty = false;
                vp = _readyView;
            }
            // not on screen: skip the work until the window draws the plot again (it shows the last bitmap meanwhile)
            if (_front is not null && Environment.TickCount64 - Volatile.Read(ref _lastBlit) > IdleAfterMs) continue;
            try
            {
                RenderFrame(_scenes[_render], vp, clock, ref last);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                LastError = e.GetType().Name + ": " + e.Message; // keep the window alive; the health panel shows it
            }
        }
    }

    private void RenderFrame(PlotScene scene, Viewport vp, Stopwatch clock, ref double last)
    {
        if (vp.Width < 1 || vp.Height < 1) return;
        float ds = _deviceScale;
        int pw = Math.Max(1, (int)MathF.Ceiling(vp.Width * ds)), ph = Math.Max(1, (int)MathF.Ceiling(vp.Height * ds));
        ref var bmp = ref _bitmaps[_back];
        if (bmp is null || bmp.Canvas.DeviceClipBounds.Width != pw || bmp.Canvas.DeviceClipBounds.Height != ph)
        {
            bmp?.Dispose();
            bmp = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (bmp is null) return;
        }
        var c = bmp.Canvas;
        c.ResetMatrix();
        c.Scale(ds);
        long t0 = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            var r = Renderer;
            if (r is null || _disposed) return;
            double now = clock.Elapsed.TotalSeconds;
            State.FrameSeconds = (float)Math.Clamp(now - last, 1 / 240.0, .25);
            last = now;
            CopyOptions(Options, State.Options);
            r.Render(c, scene, vp, State);
            DrawnView = vp;
            FramesDrawn++;
            PublishStats(r);
        }
        LastDrawMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var img = bmp.Snapshot();
        SKImage? old;
        lock (_frameLock)
        {
            old = _front;
            _front = img;
            _frontView = vp;
        }
        old?.Dispose(); // a draw still using it holds its own native reference
        _back ^= 1;
    }

    private void PublishStats(PlotRenderer r)
    {
        lock (_statsLock)
        {
            _stats = State.LastFrame;
            _timings = State.LastTimings;
            _sprites = r.SpriteCount;
        }
    }

    /// <summary>Copies every layer and size option (a test checks no property is missed).</summary>
    public static void CopyOptions(PlotOptions from, PlotOptions to)
    {
        to.Trails = from.Trails;
        to.AltitudeStalks = from.AltitudeStalks;
        to.TargetLines = from.TargetLines;
        to.Rings = from.Rings;
        to.Labels = from.Labels;
        to.Counts = from.Counts;
        to.Grid = from.Grid;
        to.Zones = from.Zones;
        to.Orders = from.Orders;
        to.Events = from.Events;
        to.ScaleBar = from.ScaleBar;
        to.SizeMode = from.SizeMode;
        to.SizeScale = from.SizeScale;
        to.MissileLabelMinScale = from.MissileLabelMinScale;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal?.Set();
        bool stopped = _thread is null || Thread.CurrentThread == _thread || _thread.Join(2000);
        lock (Gate)
        {
            _raster?.Dispose();
            _raster = null;
            if (stopped)
            {
                foreach (var b in _bitmaps) b?.Dispose();
                Array.Clear(_bitmaps);
            }
            State.Dispose();
        }
        lock (_frameLock)
        {
            _front?.Dispose();
            _front = null;
        }
    }
}
