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
