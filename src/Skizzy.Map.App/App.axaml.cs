using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Shell;
using SkzPalette = Skizzy.Map.App.Theme.Palette;

namespace Skizzy.Map.App;

public partial class App : Application
{
    public const string FontRoot = "avares://skizzy-map/Assets/Fonts";

    public static FontManagerOptions FontOptions => new()
    {
        DefaultFamilyName = FontRoot + "#IBM Plex Sans Condensed",
    };

    /// <summary>
    /// Command line: --source demo|fake|live, --home &lt;settings folder&gt;, --bench 5000|demo [--bench-seconds s]
    /// [--bench-out file], --plot-sync (draw the plot on Avalonia's render thread, no plot thread), --plot-gpu (with
    /// --plot-sync: straight onto the GPU canvas, no raster bitmap).
    /// </summary>
    public static string[] Args { get; set; } = [];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SplitterKeys.Install();
        // the palette before any window exists, so nothing paints with Fluent's defaults first
        Resources.MergedDictionaries.Add(SkzPalette.DarkSet.ToResources());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? bench = Arg("--bench");
            if (Array.IndexOf(Args, "--plot-gpu") >= 0) Plot.PlotSurface.RasterOnGpu = false; // draw the plot straight on the GPU canvas
            bool threaded = Array.IndexOf(Args, "--plot-sync") < 0;
            string? home = bench is not null ? Path.Combine(Path.GetTempPath(), "skizzy-map-bench") : Arg("--home");
            var settings = new SettingsStore(string.IsNullOrWhiteSpace(home) ? SettingsStore.DefaultRoot() : home);
            MainWindow win;
            AppState app;
            if (bench is not null)
            {
                // a clean profile, the default layout, maximised: the desktop perf measurement (checklist P1)
                app = new AppState(DesktopBench.Source(bench, out var label), settings) { ThreadedPlots = threaded };
                win = new MainWindow(app, restore: false) { WindowState = Avalonia.Controls.WindowState.Maximized };
                DesktopBench.Prepare(app);
                double secs = double.TryParse(Arg("--bench-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 10;
                DesktopBench.Attach(win, app, label, secs, Arg("--bench-out") ?? Path.Combine(AppContext.BaseDirectory, "bench.txt"));
            }
            else
            {
                app = new AppState(settings: settings) { ThreadedPlots = threaded };
                win = new MainWindow(app);
                string kind = Arg("--source") ?? settings.Current.Source.Kind;
                if (kind is "fake" or "live") win.UseSource(kind);
            }
            desktop.MainWindow = win;
            // floating panels are windows too: closing the main window ends the app
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) => app.Source.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string? Arg(string name)
    {
        int i = Array.IndexOf(Args, name);
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

    /// <summary>Installs a palette as the app's resources (dark or light).</summary>
    public static void UsePalette(ThemeVariant v)
    {
        if (Current is not { } a) return;
        a.RequestedThemeVariant = v;
        a.Resources.MergedDictionaries.Clear();
        a.Resources.MergedDictionaries.Add(SkzPalette.For(v).ToResources());
    }
}
