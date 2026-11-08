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

    /// <summary>Command line: --source demo|fake|live, --home &lt;settings folder&gt;.</summary>
    public static string[] Args { get; set; } = [];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // the palette before any window exists, so nothing paints with Fluent's defaults first
        Resources.MergedDictionaries.Add(SkzPalette.DarkSet.ToResources());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? home = Arg("--home");
            var settings = new SettingsStore(string.IsNullOrWhiteSpace(home) ? SettingsStore.DefaultRoot() : home);
            var app = new AppState(settings: settings);
            var win = new MainWindow(app);
            string kind = Arg("--source") ?? settings.Current.Source.Kind;
            if (kind is "fake" or "live") win.UseSource(kind);
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
