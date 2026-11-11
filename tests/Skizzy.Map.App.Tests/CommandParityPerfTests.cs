using System.Diagnostics;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Tests;

/// <summary>
/// The command parity perf gate (docs/M3-command-parity-report.md): 2,000 tracks with 150 of them under orders drawn
/// on the plot (moves, attacks, routes, racetracks, RTB; the demo's all attack draws about 140) stay inside the 60 fps
/// frame budget, and the order picture reaches the plot without allocating once warm. Heavier loads (400 orders, every
/// track ordered) are measured and reported, not gated. Load-resistant like the other perf gates: it runs alone (the
/// "perf" collection), gates on the best batch median, and repeats an over-budget measurement once.
/// </summary>
[Collection("perf")]
public class CommandParityPerfTests(ITestOutputHelper output)
{
    private const double FrameBudgetMs = 1000 / 60.0;

    /// <summary>
    /// Orders for the first <paramref name="count"/> tracks, shaped like real ones: moves 20–60 km out, attacks on the
    /// nearest other track, route legs and racetracks shared by eight units each (one order per selection), RTB to one base.
    /// </summary>
    private static void FillOrders(UiHarness h, int count)
    {
        var data = h.App.Data;
        double now = data.ViewTime;
        var tracks = data.Tracks.Where(t => t.Alive).ToList();
        var pos = tracks.Select(t => data.PositionOf(t, now)).ToList();
        var pic = data.Orders;
        pic.Begin();
        for (int i = 0; i < count; i++)
        {
            var (x, y) = pos[i];
            var o = pic.AddOrder();
            o.TrackId = tracks[i].Id;
            double a = i * 2.39996, d = 20 + i % 5 * 10;
            switch (i % 5)
            {
                case 0: o.Type = OrderType.Move; o.Destination = new Vector2((float)(x + Math.Cos(a) * d), (float)(y + Math.Sin(a) * d)); break;
                case 1:
                {
                    int best = -1; double bd = double.MaxValue;
                    for (int k = 0; k < tracks.Count; k++)
                    {
                        if (k == i) continue;
                        double dd = (pos[k].X - x) * (pos[k].X - x) + (pos[k].Y - y) * (pos[k].Y - y);
                        if (dd > 25 && dd < bd) { bd = dd; best = k; } // not one in its own formation
                    }
                    o.Type = OrderType.Attack; o.TargetTrackId = tracks[best].Id;
                    break;
                }
                case 2: // a drawn route is one order for the whole selection: 8 units share each
                {
                    int id = 1 + i / 40;
                    if (i % 40 == 2)
                    {
                        var route = pic.AddRoute(id);
                        for (int k = 0; k < 8; k++) route.Points.Add(new Vector2((float)(x + 6 + k * 8 * Math.Cos(a)), (float)(y + k * 8 * Math.Sin(a))));
                    }
                    o.Type = OrderType.Route; o.RouteId = id; o.RouteIndex = 1 + i % 3;
                    break;
                }
                case 3: // likewise a racetrack: one pattern, its aircraft stacked
                {
                    int id = 1 + i / 40;
                    if (i % 40 == 3)
                    {
                        var rt = pic.AddRacetrack();
                        rt.Id = id; rt.A = new Vector2((float)x + 5, (float)y); rt.B = new Vector2((float)x + 25, (float)y); rt.WidthKm = 6;
                    }
                    o.Type = OrderType.Racetrack; o.PatternId = id;
                    break;
                }
                default: o.Type = OrderType.Rtb; o.TargetTrackId = tracks[0].Id; o.RtbPhase = (RtbPhase)(i % 3); break;
            }
        }
        pic.Commit();
    }

    [AvaloniaFact]
    public void OrdersOn2000TracksKeepTheFrameBudgetAndReachThePlotWithoutAllocating()
    {
        using var h = new UiHarness(new SyntheticSource(2000), width: 1920, height: 1080);
        var map = h.App.ActiveMap!;
        h.Pump(30);
        double none = BestFrame(h);

        // the demo's all attack draws about 140 orders: 150 here, gated at 60 fps
        FillOrders(h, 150);
        h.Pump(30);
        Assert.Equal(150, map.Surface.Scene.Orders.Count);
        Assert.True(map.Surface.State.LastFrame.OrderLines > 60, $"{map.Surface.State.LastFrame.OrderLines} order lines drawn");
        double gated = BestFrame(h);
        if (gated >= FrameBudgetMs)
        {
            output.WriteLine($"first run over budget ({gated:0.0} ms), measuring again");
            gated = Math.Min(gated, BestFrame(h));
        }
        double layer = map.Surface.State.LastTimings.Orders;
        string line = $"command parity, 2000 tracks @1920x1080, data+sync+panels+plot best batch median: no orders {none:0.0} ms ({1000 / none:0} fps); " +
                      $"150 drawn orders {gated:0.0} ms ({1000 / gated:0} fps), order layer {layer:0.0} ms";
        output.WriteLine(line);
        Assert.True(gated < FrameBudgetMs, line);

        // the order picture reaches the plot's scene without allocating (pooled orders, routes and racetracks)
        for (int i = 0; i < 10; i++) map.Frame(1 / 60.0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) map.Frame(1 / 60.0);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes == 0, $"the plot sync allocated {bytes} bytes over 30 frames with 150 orders");

        // heavier loads, reported: 400 orders, then every track ordered
        foreach (int n in new[] { 400, 2000 })
        {
            FillOrders(h, n);
            h.Pump(10);
            double ms = BestFrame(h);
            output.WriteLine($"command parity load, 2000 tracks with {n} drawn orders: {ms:0.0} ms ({1000 / ms:0} fps), order layer {map.Surface.State.LastTimings.Orders:0.0} ms");
        }
    }

    /// <summary>Best batch median of data + sync + panels + plot draw (background load only ever adds time).</summary>
    private static double BestFrame(UiHarness h)
    {
        var map = h.App.ActiveMap!;
        const int batches = 6, frames = 40;
        var work = new double[frames];
        double best = double.MaxValue;
        var sw = new Stopwatch();
        for (int i = 0; i < 60; i++) Step(h, sw);
        for (int b = 0; b < batches; b++)
        {
            for (int i = 0; i < frames; i++) work[i] = Step(h, sw) + map.Surface.LastDrawMs;
            Array.Sort(work);
            best = Math.Min(best, work[frames / 2]);
        }
        return best;
    }

    private static double Step(UiHarness h, Stopwatch sw)
    {
        sw.Restart();
        h.App.Frame(1 / 60.0);
        double app = sw.Elapsed.TotalMilliseconds;
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return app;
    }
}
