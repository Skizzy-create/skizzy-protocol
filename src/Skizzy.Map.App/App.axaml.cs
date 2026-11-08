using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Skizzy.Map.App;

public partial class App : Application
{
    public const string FontRoot = "avares://skizzy-map/Assets/Fonts";

    public static FontManagerOptions FontOptions => new()
    {
        DefaultFamilyName = FontRoot + "#IBM Plex Sans Condensed",
    };

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SpikeWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
