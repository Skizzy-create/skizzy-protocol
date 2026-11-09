using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Skizzy.Map.App.Map;
using Skizzy.Map.Data.Demo;

namespace Skizzy.Map.App.Shell;

/// <summary>
/// <c>skizzy-map --bench 5000|demo [--bench-seconds 10] [--bench-out file]</c>: opens the real desktop window
/// (GPU compositor, display clock), warms up, then measures for a few seconds and appends one line to the output
/// file before exiting. This is the desktop counterpart of the headless perf test (checklist P1).
/// </summary>
public static class DesktopBench
{
    public const double WarmupSeconds = 3;

    /// <summary>Creates the bench source: N synthetic tracks, or the demo battle fast-forwarded into the dense raid.</summary>
    public static Skizzy.Map.Data.IDataSource Source(string what, out string label)
    {
        if (int.TryParse(what, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
        {
            label = $"{n} synthetic tracks";
            return new SyntheticSource(n);
        }
        label = "demo battle, dense raid";
        return new DemoBattleSource();
    }

    /// <summary>Runs the demo battle into the raid (about 12.7 sim minutes) before the window opens.</summary>
    public static void Prepare(AppState app)
    {
        if (app.Source is not DemoBattleSource d) return;
        d.DisablePauseOnEvents();
        d.SetSpeed(64);
        for (int i = 0; i < 40; i++) app.Frame(0.3);
        d.SetSpeed(8);
    }

    public static void Attach(MainWindow win, AppState app, string label, double seconds, string outPath)
    {
        var draws = new List<double>();
        var work = new List<double>();
        var src = new List<double>();
        var sync = new List<double>();
        var uiWait = new List<double>();
        var rtWait = new List<double>();
        var panels = new List<double>();
        var blit = new List<double>();
        var layout = new List<double>();
        var tracks = new List<double>();
        var trails = new List<double>();
        var labels = new List<double>();
        bool measuring = false;
        long f0 = 0, d0 = 0;
        var sw = new Stopwatch();
        MapViewModel? map = null;

        void OnTick()
        {
            if (!measuring || map is null) return;
            draws.Add(map.Surface.LastDrawMs);
            work.Add(app.LastFrameMs);
            src.Add(app.LastSourceMs);
            sync.Add(map.LastSyncMs);
            uiWait.Add(map.LastWaitMs);
            rtWait.Add(map.Surface.LastWaitMs);
            panels.Add(app.LastPanelsMs);
            blit.Add(map.Surface.LastBlitMs);
            var tm = map.Surface.Stats.Timings;
            layout.Add(tm.Layout); tracks.Add(tm.Tracks); trails.Add(tm.Trails); labels.Add(tm.Labels);
        }

        win.Opened += (_, _) =>
        {
            app.Ticked += OnTick;
            DispatcherTimer.RunOnce(() =>
            {
                map = app.ActiveMap;
                if (map is null) { Finish("no map window"); return; }
                f0 = app.Frames;
                d0 = map.Surface.FramesDrawn;
                measuring = true;
                sw.Start();
                DispatcherTimer.RunOnce(() => Finish(null), TimeSpan.FromSeconds(seconds));
            }, TimeSpan.FromSeconds(WarmupSeconds));
        };

        void Finish(string? error)
        {
            measuring = false;
            app.Ticked -= OnTick;
            string line;
            if (error is not null || map is null) line = "bench failed: " + error;
            else
            {
                double t = sw.Elapsed.TotalSeconds;
                double appFps = (app.Frames - f0) / t, drawFps = (map.Surface.FramesDrawn - d0) / t;
                foreach (var l in new[] { draws, work, src, sync, uiWait, rtWait, panels, blit, layout, tracks, trails, labels }) l.Sort();
                double Med(List<double> a) => a.Count == 0 ? double.NaN : a[a.Count / 2];
                double P95(List<double> a) => a.Count == 0 ? double.NaN : a[(int)(a.Count * .95)];
                var st = map.Surface.Stats.Frame;
                var screen = win.Screens?.ScreenFromWindow(win);
                line = string.Create(CultureInfo.InvariantCulture,
                    $"desktop bench, {label}: window {win.Bounds.Width:0}x{win.Bounds.Height:0} (scale {win.RenderScaling:0.##}, screen {screen?.Bounds.Width}x{screen?.Bounds.Height}), " +
                    $"plot {map.Width:0}x{map.Height:0} px via {map.Surface.Mode}; over {t:0.0} s: app frames {appFps:0.0} fps, plot frames drawn {drawFps:0.0} fps; " +
                    $"plot draw median {Med(draws):0.00} ms (p95 {P95(draws):0.00}; layout {Med(layout):0.0}, tracks {Med(tracks):0.0}, trails {Med(trails):0.0}, labels {Med(labels):0.0}), UI frame work median {Med(work):0.00} ms (p95 {P95(work):0.00}); " +
                    $"UI parts median: source {Med(src):0.00}, sync {Med(sync):0.00}, wait for render {Med(uiWait):0.00}, panels {Med(panels):0.00} ms; render waits for UI {Med(rtWait):0.00} ms, bitmap blit {Med(blit):0.00} ms; " +
                    $"on screen {st.TracksVisible} of {map.Sync.Visible} ({st.FullSymbols} symbols, {st.Dots} dots), view {map.CenterX:0},{map.CenterY:0} at {map.Scale:0.00} px/km");
            }
            try { File.AppendAllText(outPath, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Console.WriteLine(line);
            win.Close();
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }
}
