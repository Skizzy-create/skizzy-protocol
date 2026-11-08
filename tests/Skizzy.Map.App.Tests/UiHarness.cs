using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Shell;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Tests;

/// <summary>
/// The whole app in a headless window: its own settings folder, a data source, the main window shown at a fixed
/// size, frames stepped by hand (the display clock is off) so every test is deterministic.
/// </summary>
internal sealed class UiHarness : IDisposable
{
    public UiHarness(IDataSource? source = null, bool restore = false, int width = 1600, int height = 1000, string? home = null)
    {
        Home = home ?? Path.Combine(Path.GetTempPath(), "skizzy-map-ui-" + Guid.NewGuid().ToString("N")[..8]);
        OwnsHome = home is null;
        Directory.CreateDirectory(Home);
        App = new AppState(source ?? new DemoBattleSource(), new SettingsStore(Home));
        Win = new MainWindow(App, restore) { AutoFrames = false, Width = width, Height = height };
        Win.Show();
        Pump(3);
    }

    public string Home { get; }
    private bool OwnsHome { get; }
    public AppState App { get; }
    public MainWindow Win { get; }
    public DemoBattleSource? Demo => App.Source as DemoBattleSource;

    /// <summary>Steps <paramref name="frames"/> app frames of <paramref name="dt"/> seconds, running layout and render jobs.</summary>
    public void Pump(int frames, double dt = 1 / 60.0)
    {
        for (int i = 0; i < frames; i++)
        {
            App.Frame(dt);
            Dispatcher.UIThread.RunJobs();
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs the demo battle forward quickly (speed 64, frame clamp 0.3 s) without stopping on events.</summary>
    public void FastForward(double simSeconds)
    {
        var d = Demo ?? throw new InvalidOperationException("demo source only");
        d.DisablePauseOnEvents();
        if (d.Paused) App.Resume();
        double was = d.Speed;
        d.SetSpeed(64);
        int frames = (int)Math.Ceiling(simSeconds / (0.3 * 64));
        for (int i = 0; i < frames; i++) App.Frame(0.3);
        d.SetSpeed(was);
        Pump(4);
    }

    public T? Find<T>() where T : class => Win.GetVisualDescendants().OfType<T>().FirstOrDefault();

    public IEnumerable<T> FindLogical<T>() where T : class => Win.GetLogicalDescendants().OfType<T>();

    public WriteableBitmap Capture()
    {
        Pump(1);
        return Win.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame captured");
    }

    public static string OutDir(string sub = "ui")
    {
        var dir = Path.Combine(AppContext.BaseDirectory, sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string Save(string name)
    {
        var path = Path.Combine(OutDir(), name);
        Capture().Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    public void Dispose()
    {
        Win.Close();
        Dispatcher.UIThread.RunJobs();
        App.Source.Dispose();
        foreach (var m in App.Maps.ToList()) m.Dispose();
        Skizzy.Map.App.App.UsePalette(Avalonia.Styling.ThemeVariant.Dark);
        if (OwnsHome) try { Directory.Delete(Home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// N tracks on straight lines, clustered like raids, every one sampled every frame: the perf gate's load
/// (the worst case for the data path: no track ever rests).
/// </summary>
internal sealed class SyntheticSource : IDataSource
{
    private readonly int _n;
    private MapData? _data;
    private readonly List<(TrackInfo t, double x, double y, double vx, double vy, float alt, float hdg, float spd)> _tracks = new();
    private double _t;

    public SyntheticSource(int n) => _n = n;

    public string Name => "Synthetic";
    public string Description => $"Perf gate · {_n:#,0} synthetic tracks";
    public SourceKind Kind => SourceKind.Demo;
    public SourceCaps Caps => SourceCaps.Pause;
    public SourceState State => SourceState.Online;
    public string StatusText => "synthetic";
    public bool Paused { get; private set; }
    public double Speed => 1;
    public IReadOnlyList<CatalogEntry> Catalog => [];
    public HealthSnapshot Health => new(Name, "Online", null, null, null, null, _n, 0, []);

    public void Attach(MapData data)
    {
        _data = data;
        data.Reset();
        data.Epoch = 0;
        var rng = new Random(7);
        TrackKind[] mix = [TrackKind.Fighter, TrackKind.Fighter, TrackKind.Strike, TrackKind.Ucav, TrackKind.Uav, TrackKind.AntiShipMissile, TrackKind.SurfaceToAirMissile, TrackKind.AirToAirMissile, TrackKind.EarlyWarning];
        for (int i = 0; i < _n; i++)
        {
            var kind = i < 4 ? TrackKind.Carrier : i < 40 ? TrackKind.Destroyer : mix[rng.Next(mix.Length)];
            int g = rng.Next(40);
            double gx = -40 + (g * 37 % 290), gy = -80 + (g * 53 % 160);
            var tr = data.AddWithId(i + 1, "s" + i, 0);
            var info = KindInfo.Of(kind);
            tr.Kind = kind;
            tr.Domain = info.Domain;
            tr.Side = g % 3 == 0 ? Side.Blue : Side.Red;
            tr.AffBlue = tr.Side == Side.Blue ? Affiliation.Friend : g % 7 == 0 ? Affiliation.Unknown : Affiliation.Hostile;
            tr.AffRed = tr.Side == Side.Red ? Affiliation.Friend : Affiliation.Hostile;
            tr.DetBlue = tr.DetRed = TrackDetection.Shared;
            tr.Name = $"T{i + 1:0000}";
            tr.Rcs = info.IsMissile ? 0.1f : info.Domain == TrackDomain.Surface ? 10000 : 5;
            tr.Health = info.Domain == TrackDomain.Surface ? 0.8f : -1;
            float hdg = (float)(rng.NextDouble() * 360), spd = info.IsMissile ? 300 + (float)rng.NextDouble() * 600 : info.Domain == TrackDomain.Surface ? 12 : 120 + (float)rng.NextDouble() * 150;
            HistoryRing.Velocity(hdg, spd, out double vx, out double vy);
            _tracks.Add((tr, gx + (rng.NextDouble() - .5) * 14, gy + (rng.NextDouble() - .5) * 14, vx, vy, info.Domain == TrackDomain.Surface ? 0 : (float)(rng.NextDouble() * 9000), hdg, spd));
        }
        Publish();
    }

    public void Tick(double realDt)
    {
        if (_data is null || Paused) return;
        _t += realDt;
        Publish();
    }

    private void Publish()
    {
        var d = _data!;
        d.LiveTime = _t;
        foreach (var (t, x, y, vx, vy, alt, hdg, spd) in _tracks)
        {
            t.SetSample(_t, x + vx * _t, y + vy * _t, alt, hdg, spd, vx, vy, _t);
            t.Record(d.HistoryInterval);
        }
        d.TouchRevision();
    }

    public void SetPaused(bool paused) => Paused = paused;
    public void SetSpeed(double speed) { }
    public void Restart() { _t = 0; }
    public Task<string> CallAsync(string method, string argsJson, CancellationToken ct) => throw new NotSupportedException();
    public void Dispose() => _data = null;
}
