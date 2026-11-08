using System.Diagnostics;
using Skizzy.Map.Render;
using Skizzy.Map.RenderDemo;
using Xunit.Abstractions;

namespace Skizzy.Map.Render.Tests;

/// <summary>Timing tests run alone: no other test class executes in parallel with this collection.</summary>
[CollectionDefinition("Perf", DisableParallelization = true)]
public sealed class PerfCollection;

[Collection("Perf")]
public sealed class PerfTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly ITestOutputHelper _out;
    public PerfTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _rig.Dispose();

    /// <summary>The performance budget: 2,000 tracks at 1920x1080 in under 16 ms (median, warm).</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Renders_2000_tracks_at_1080p_under_16ms()
    {
        var scene = PerfScene.Build(2000);
        var vp = scene.View;
        var st = new RenderState();
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < 2000) _rig.Render(scene, vp, st);

        const int n = 200;
        var ms = new double[n];
        var sw = new Stopwatch();
        for (int i = 0; i < n; i++)
        {
            // move every track a little so the frame is not a pure repeat (sizes, labels and sprites re-evaluate)
            foreach (var t in scene.Tracks) t.X += (i % 2 == 0 ? 1 : -1) * 0.01f;
            sw.Restart();
            _rig.Render(scene, vp, st);
            _rig.Surface.Flush();
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        double median = ms[n / 2], p95 = ms[(int)(n * 0.95)], min = ms[0];
        var f = st.LastFrame;
        var t0 = st.LastTimings;
        _out.WriteLine($"2000 tracks @1920x1080: median {median:0.00} ms, p95 {p95:0.00} ms, min {min:0.00} ms");
        _out.WriteLine($"visible {f.TracksVisible}: full {f.FullSymbols}, frame {f.Frames}, dot {f.Dots}, missile {f.Missiles}, labels {f.LabelsPlaced} (culled {f.LabelsCulled})");
        _out.WriteLine($"last frame stages ms: background {t0.Background:0.00}, layout {t0.Layout:0.00}, rings {t0.Rings:0.00}, trails {t0.Trails:0.00}, orders {t0.Orders:0.00}, tracks {t0.Tracks:0.00}, labels {t0.Labels:0.00}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "perf-result.txt"),
            $"median_ms={median:0.000}\np95_ms={p95:0.000}\nmin_ms={min:0.000}\n");
        Assert.Equal(2000, f.TracksVisible);
        Assert.True(median < 16.0, $"median {median:0.00} ms exceeds the 16 ms budget");
    }

}
