using System.Diagnostics;
using SkiaSharp;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;
using Skizzy.Map.Render.Text;
using Skizzy.Map.RenderDemo;

// Headless renderer demo: draws the mockup's sample scene at three zoom levels, a dense "128 missiles inbound"
// frame and an orders/VIP frame, and writes PNGs. Usage: skizzy-render-demo [outputDir] | bench | perf
if (args.Length > 0 && args[0] == "bench") return Bench.Run();
if (args.Length > 0 && args[0] == "perf") return Bench.Perf();
string outDir = args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), "docs", "render-samples");
Directory.CreateDirectory(outDir);
const int W = 1920, H = 1080;

Console.WriteLine($"fonts: {(FontSet.Default.UsingFallback ? "SYSTEM FALLBACK" : "bundled OFL faces")}");

using var renderer = new PlotRenderer();
var scene = SampleScene.Build();
var raid = SampleScene.BuildMissileRaid();
var orders = SampleScene.BuildVipsAndOrders();

Render("01-theatre.png", scene, scene.View);
Render("02-mid.png", scene, new Viewport(48, 4, 9f, W, H));
Render("03-close-carrier-group.png", scene, new Viewport(3, 0, 34f, W, H));
Render("04-raid-128-missiles.png", raid, raid.View);
Render("05-vip-racetrack-marshal-hunt.png", orders, orders.View);
return 0;

void Render(string file, PlotScene s, Viewport vp)
{
    using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
    var state = new RenderState(); // fresh state: the first frame snaps sizes (no easing)
    var sw = Stopwatch.StartNew();
    renderer.Render(surface.Canvas, s, vp, state);
    double firstMs = sw.Elapsed.TotalMilliseconds;
    // a few more frames to report a warm number
    sw.Restart();
    const int n = 20;
    for (int i = 0; i < n; i++) renderer.Render(surface.Canvas, s, vp, state);
    double warmMs = sw.Elapsed.TotalMilliseconds / n;
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    var path = Path.Combine(outDir, file);
    using (var f = File.Create(path)) data.SaveTo(f);
    var st = state.LastFrame;
    Console.WriteLine($"{file,-30} scale {vp.Scale,6:0.00} px/km  tracks {st.TracksVisible,4}  full {st.FullSymbols,3} frame {st.Frames,3} dot {st.Dots,3} msl {st.Missiles,3}  " +
                      $"labels {st.LabelsPlaced,3} (culled {st.LabelsCulled,3})  orders {st.ActiveOrders} a={st.OrderLineAlpha:0.00} lines {st.OrderLines} rt {st.Racetracks} marshal {st.MarshalRings} vip {st.VipMarkers}/{st.VipRings}/{st.VipTags} own {st.OwnSensorMarks} undet {st.Undetected}  first {firstMs,6:0.0} ms  warm {warmMs,5:0.00} ms");
    state.Dispose();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Skizzy.Map.Render.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
