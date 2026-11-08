using Avalonia;
using Avalonia.Headless;
using Skizzy.Map.App;

[assembly: AvaloniaTestApplication(typeof(Skizzy.Map.App.Tests.TestApp))]

namespace Skizzy.Map.App.Tests;

/// <summary>Headless Avalonia with real Skia drawing, so frames can be captured and the plot's Skia lease works.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(App.FontOptions);
}
