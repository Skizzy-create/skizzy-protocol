using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Skizzy.Map.App.Plot;
using Skizzy.Map.Render;

namespace Skizzy.Map.App.Tests;

/// <summary>The desktop plot pipeline: a plot thread per map window renders into bitmaps the window only draws.</summary>
public class PlotThreadTests
{
    [Fact]
    public void CopyOptionsCopiesEveryOption()
    {
        var from = new PlotOptions();
        var props = typeof(PlotOptions).GetProperties().Where(p => p.CanWrite && p.CanRead).ToList();
        foreach (var p in props)
        {
            object cur = p.GetValue(from)!;
            object v = cur switch
            {
                bool b => !b,
                float f => f + .5f,
                Enum e => Enum.GetValues(p.PropertyType).Cast<object>().First(x => !x.Equals(e)),
                _ => throw new InvalidOperationException($"teach this test about {p.PropertyType.Name} ({p.Name})"),
            };
            p.SetValue(from, v);
        }
        var to = new PlotOptions();
        PlotSurface.CopyOptions(from, to);
        Assert.All(props, p => Assert.Equal(p.GetValue(from), p.GetValue(to)));
    }

    [AvaloniaFact]
    public void ThreadedPlotDrawsTheBitmapAndHitTestsFollowPans()
    {
        using var h = new UiHarness(threadedPlots: true, width: 1600, height: 1000);
        var map = h.App.ActiveMap!;
        Assert.True(map.Surface.Threaded);
        WaitForFrames(h, map.Surface, 3);
        h.Pump(2);
        Assert.Equal("plot thread", map.Surface.Mode);
        Assert.Null(map.Surface.LastError);
        Assert.True(map.Surface.Stats.Frame.TracksVisible > 50, $"{map.Surface.Stats.Frame.TracksVisible} tracks drawn");

        // the window shows the plot thread's bitmap: the plot area is a picture, not a flat fill
        var plot = h.Find<PlotControl>()!;
        var origin = plot.TranslatePoint(new Point(0, 0), h.Win)!.Value;
        using (var frame = h.Capture())
        {
            Assert.True(DistinctColours(frame, origin, plot.Bounds.Size) > 40);
            frame.Save(Path.Combine(UiHarness.OutDir(), "threaded-plot.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        // a drawn track under the pointer, then the same track after a pan the plot thread has not rendered yet
        var t = map.Surface.Scene.Tracks.First(p => p.IsDrawn && !p.IsMissile);
        var (sx, sy) = map.ToScreen(t.X, t.Y);
        Assert.NotEqual(0, map.HitTest(sx, sy));
        map.PanBy(120, -40);
        var (px, py) = map.ToScreen(t.X, t.Y);
        Assert.Equal(sx + 120, px, 1);
        Assert.NotEqual(0, map.HitTest(px, py));

        // closing the window stops the plot thread
        h.App.RemoveMap(map);
        Assert.Throws<InvalidOperationException>(() => map.Surface.Scene = new());
    }

    private static void WaitForFrames(UiHarness h, PlotSurface s, long n)
    {
        var sw = Stopwatch.StartNew();
        while (s.FramesDrawn < n && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            h.Pump(1);
            Thread.Sleep(5);
        }
        Assert.True(s.FramesDrawn >= n, $"plot thread drew {s.FramesDrawn} frames in {sw.Elapsed.TotalSeconds:0.0} s");
    }

    private static int DistinctColours(Avalonia.Media.Imaging.WriteableBitmap bmp, Point origin, Size size)
    {
        using var fb = bmp.Lock();
        var seen = new HashSet<int>();
        double scale = fb.Size.Width / bmp.Size.Width;
        for (int gy = 0; gy < 60; gy++)
            for (int gx = 0; gx < 80; gx++)
            {
                int x = (int)((origin.X + size.Width * (gx + .5) / 80) * scale), y = (int)((origin.Y + size.Height * (gy + .5) / 60) * scale);
                if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height) continue;
                seen.Add(Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4));
            }
        return seen.Count;
    }
}
