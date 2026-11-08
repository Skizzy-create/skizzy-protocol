using Skizzy.Map.Render;
using Skizzy.Map.RenderDemo;
using Xunit.Abstractions;

namespace Skizzy.Map.Render.Tests;

/// <summary>Timing tests run alone: no other test class executes in parallel with this collection.</summary>
[CollectionDefinition("Perf", DisableParallelization = true)]
public sealed class PerfCollection;

/// <summary>
/// The frame-time budget. Tagged <c>Category=Perf</c> so CI can run them on their own or skip them
/// (<c>--filter "Category!=Perf"</c>) on shared runners.
/// <para>
/// Load-resistant: after a warm-up, frames are timed in 7 batches of 60. Background load only ever adds time, so the
/// gate uses the lowest batch median (what the renderer costs on a free core); the all-frame median and p95 are
/// reported for what a busy machine sees. A failing measurement is repeated once before the test fails.
/// </para>
/// </summary>
[Collection("Perf")]
[Trait("Category", "Perf")]
public sealed class PerfTests : IDisposable
{
    private const double BudgetMs = 16.0;
    private readonly PlotRenderer _renderer = new();
    private readonly ITestOutputHelper _out;
    public PerfTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _renderer.Dispose();

    /// <summary>The performance budget (doc 11): 2,000 moving tracks at 1920x1080 in under 16 ms, with no garbage.</summary>
    [Fact]
    public void Renders_2000_tracks_at_1080p_under_16ms()
    {
        var r = Measure(2000, out var st);
        if (r.BestBatchMedianMs >= BudgetMs)
        {
            _out.WriteLine($"first run over budget ({r.BestBatchMedianMs:0.00} ms), measuring again");
            r = Measure(2000, out st);
        }
        Report("2000", r, st);
        Assert.Equal(2000, r.Visible);
        Assert.Equal(0, r.AllocBytesPerFrame);
        Assert.True(r.BestBatchMedianMs < BudgetMs, $"best batch median {r.BestBatchMedianMs:0.00} ms exceeds the {BudgetMs} ms budget");
    }

    /// <summary>
    /// Headroom check at 2,500 tracks (25 % over the budgeted load). Reported, not gated on time: on a laptop it sits
    /// near the 16 ms line. It must still draw every track without garbage.
    /// </summary>
    [Fact]
    public void Renders_2500_tracks_at_1080p_and_reports_the_headroom()
    {
        var r = Measure(2500, out var st);
        Report("2500", r, st);
        Assert.Equal(2500, r.Visible);
        Assert.Equal(0, r.AllocBytesPerFrame);
    }

    private PerfStats Measure(int n, out RenderState st)
    {
        var scene = PerfScene.Build(n);
        st = new RenderState();
        return PerfStats.Measure(_renderer, scene, scene.View, st, batches: 7, frames: 60, warmupMs: 2000);
    }

    private void Report(string name, PerfStats r, RenderState st)
    {
        var f = st.LastFrame;
        var t = st.LastTimings;
        _out.WriteLine($"{name} tracks moving @1920x1080: {r}");
        _out.WriteLine($"visible {f.TracksVisible}: full {f.FullSymbols}, frame {f.Frames}, dot {f.Dots}, missile {f.Missiles}, labels {f.LabelsPlaced} (culled {f.LabelsCulled})");
        _out.WriteLine($"last frame stages ms: background {t.Background:0.00}, layout {t.Layout:0.00}, rings {t.Rings:0.00}, trails {t.Trails:0.00}, orders {t.Orders:0.00}, tracks {t.Tracks:0.00}, labels {t.Labels:0.00}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"perf-result-{name}.txt"),
            $"best_batch_median_ms={r.BestBatchMedianMs:0.000}\nmedian_ms={r.MedianMs:0.000}\np95_ms={r.P95Ms:0.000}\nmin_ms={r.MinMs:0.000}\nalloc_bytes_per_frame={r.AllocBytesPerFrame}\n");
    }
}
