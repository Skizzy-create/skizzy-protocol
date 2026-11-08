using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Skizzy.Map.RenderDemo;

namespace Skizzy.Map.App.Tests;

public class SpikeTests
{
    [AvaloniaFact]
    public void Plot_draws_through_the_skia_lease_in_headless_mode()
    {
        var surface = new Plot.PlotSurface { Renderer = new Render.PlotRenderer() };
        surface.Scene = SampleScene.Build();
        surface.View = surface.Scene.View;
        var w = new Avalonia.Controls.Window { Width = 1280, Height = 800, Content = new Plot.PlotControl { Surface = surface } };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.False(surface.NoSkia);
        Assert.True(surface.FramesDrawn > 0);
        var dir = Path.Combine(AppContext.BaseDirectory, "spike");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, "plot.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
