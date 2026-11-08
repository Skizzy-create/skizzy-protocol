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
        var w = new SpikeWindow();
        w.Surface.Scene = SampleScene.Build();
        w.Surface.View = w.Surface.Scene.View;
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.False(w.Surface.NoSkia);
        Assert.True(w.Surface.FramesDrawn > 0);
        var dir = Path.Combine(AppContext.BaseDirectory, "spike");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, "plot.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
