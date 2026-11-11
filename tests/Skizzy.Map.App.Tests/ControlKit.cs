using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Skizzy.Map.App.Map;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Services;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;
using Skizzy.Protocol.Json;

namespace Skizzy.Map.App.Tests;

/// <summary>
/// Real user actions for the per-control tests (docs/M3-command-parity-report.md): mouse clicks at a control's centre
/// after a hit test (so a disabled, hidden or covered control fails the test), keys on the window, plot gestures in
/// plot km, and the checks every order test makes: the c2.* call that went out and what the log and toast said.
/// </summary>
internal static class ControlKit
{
    /// <summary>The demo battle with pause-on-events off (it would stop the clock at the first launch).</summary>
    public static UiHarness Demo()
    {
        var h = new UiHarness();
        h.Demo!.Sim.PauseOnEvents = false;
        return h;
    }

    public static DemoBattleSource Src(this UiHarness h) => h.Demo!;
    public static BattleSim Sim(this UiHarness h) => h.Demo!.Sim;
    public static SimObj Unit(this UiHarness h, string name) => h.Sim().Objs.First(o => o.Name == name);
    public static TrackInfo Track(this UiHarness h, string name) => h.App.Data.Tracks.First(t => t.Name == name && t.Alive);
    public static CommandService Cmd(this UiHarness h) => h.App.Commands;
    public static MapView Map(this UiHarness h) => h.Find<MapView>()!;
    public static UnitView UnitPanel(this UiHarness h) => h.Find<UnitView>()!;

    // ------------------------------------------------------------ clicks and keys

    /// <summary>A left click at the control's centre, after scrolling it into view and checking the click lands on it.</summary>
    public static void Click(this UiHarness h, Control c, RawInputModifiers mods = RawInputModifiers.None)
    {
        c.BringIntoView();
        h.Pump(1);
        Assert.True(c.IsEffectivelyVisible, $"{Describe(c)} is not visible");
        Assert.True(c.IsEffectivelyEnabled, $"{Describe(c)} is disabled ({ToolTip.GetTip(c)})");
        var p = Centre(h, c);
        var hit = h.Win.InputHitTest(p) as Visual;
        Assert.True(hit == c || (hit is not null && hit.GetVisualAncestors().Contains(c)), $"a click on {Describe(c)} at {p} lands on {hit}");
        h.Win.MouseDown(p, MouseButton.Left, mods);
        h.Win.MouseUp(p, MouseButton.Left, mods);
        h.Pump(1);
    }

    public static Point Centre(UiHarness h, Control c) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), h.Win) ?? throw new InvalidOperationException(Describe(c) + " is not in the window");

    public static void Key(this UiHarness h, PhysicalKey k, RawInputModifiers m = RawInputModifiers.None)
    {
        h.Win.KeyPressQwerty(k, m);
        h.Pump(1);
    }

    private static string Describe(Control c) => $"{c.GetType().Name} \"{Text(c)}\"";

    public static string Text(Control c) => c switch
    {
        ContentControl { Content: string s } => s,
        TextBlock t => t.Text ?? "",
        Border { Child: TextBlock t } => t.Text ?? "",
        _ => "",
    };

    /// <summary>The button (or toggle) with this text under <paramref name="root"/>.</summary>
    public static T Btn<T>(Control root, string text) where T : ContentControl =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(b => b.Content as string == text)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} \"{text}\" in {root.GetType().Name}");

    public static Button Btn(Control root, string text) => Btn<Button>(root, text);
    public static ToggleButton Toggle(Control root, string text) => Btn<ToggleButton>(root, text);

    /// <summary>The chip (Ui.Chip: name and count) with this name under <paramref name="root"/>.</summary>
    public static ToggleButton Chip(Control root, string name) =>
        root.GetLogicalDescendants().OfType<ToggleButton>().FirstOrDefault(b => b.Classes.Contains("chip") && b.Content is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text == name))
        ?? throw new InvalidOperationException($"no chip \"{name}\" in {root.GetType().Name}");

    /// <summary>Tabs to a control (keyboard focus) and presses a key on it: how a user drives a select or a slider from the keyboard.</summary>
    public static void KeyOn(this UiHarness h, Control c, PhysicalKey k, int times = 1)
    {
        c.BringIntoView();
        h.Pump(1);
        Assert.True(c.IsEffectivelyEnabled, $"{Describe(c)} is disabled ({ToolTip.GetTip(c)})");
        Assert.True(c.Focus(NavigationMethod.Tab), $"{Describe(c)} does not take focus");
        for (int i = 0; i < times; i++) h.Win.KeyPressQwerty(k, RawInputModifiers.None);
        h.Pump(1);
    }

    /// <summary>Clicks into a text box and types.</summary>
    public static void Type(this UiHarness h, TextBox box, string text)
    {
        h.Click(box);
        h.Win.KeyTextInput(text);
        h.Pump(1);
    }

    /// <summary>A left click (or two quick ones) at a window point.</summary>
    public static void ClickAt(this UiHarness h, Point p, int clicks = 1, RawInputModifiers mods = RawInputModifiers.None, MouseButton button = MouseButton.Left)
    {
        for (int i = 0; i < clicks; i++)
        {
            h.Win.MouseDown(p, button, mods);
            h.Win.MouseUp(p, button, mods);
        }
        h.Pump(1);
    }

    /// <summary>Pauses the clock so the plot holds still between two pictures.</summary>
    public static void Hold(this UiHarness h)
    {
        if (!h.App.Paused) h.App.TogglePlay();
        h.Pump(3);
    }

    /// <summary>The plot's pixels (inside the toolbar and the timeline) from a rendered frame.</summary>
    public static byte[] PlotPixels(this UiHarness h)
    {
        h.Pump(3);
        var bmp = h.Capture();
        var v = h.Map();
        var tl = v.Plot.TranslatePoint(new Point(0, 0), h.Win)!.Value;
        int x0 = (int)tl.X + 10, x1 = (int)(tl.X + v.Plot.Bounds.Width) - 10, y0 = (int)tl.Y + 150, y1 = (int)(tl.Y + v.Plot.Bounds.Height) - 70;
        using var fb = bmp.Lock();
        int w = (x1 - x0) * 4;
        var buf = new byte[w * (y1 - y0)];
        for (int y = y0; y < y1; y++)
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes + x0 * 4, buf, (y - y0) * w, w);
        return buf;
    }

    /// <summary>The plot's pixels once the symbol sizes have eased in (two pictures 80 ms apart nearly agree).</summary>
    public static byte[] SettledPlot(this UiHarness h)
    {
        var prev = h.PlotPixels();
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(80);
            var cur = h.PlotPixels();
            if (Diff(prev, cur) <= 4) return cur;
            prev = cur;
        }
        return prev;
    }

    /// <summary>How many pixels differ between two plot pictures.</summary>
    public static int Diff(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
        return n;
    }

    /// <summary>A plot point (km) with nothing under it on the drawn frame.</summary>
    public static (double X, double Y) Empty(this UiHarness h, double x, double y)
    {
        var v = h.Map().ViewModel;
        var (sx, sy) = v.ToScreen((float)x, (float)y);
        Assert.Equal(0, v.HitTest(sx, sy));
        return (x, y);
    }

    /// <summary>Centres the plot on a track at <paramref name="scale"/> px/km and returns its plot position.</summary>
    public static (double X, double Y) Focus(this UiHarness h, TrackInfo t, float scale = 10)
    {
        var p = h.Pos(t);
        h.View(p.X, p.Y, scale);
        return h.Pos(t);
    }

    /// <summary>A hostile the Blue picture shows (detected and drawn), after the battle has run a while.</summary>
    public static TrackInfo Hostile(this UiHarness h, Func<TrackInfo, bool>? where = null)
    {
        var map = h.Map().ViewModel;
        var drawn = map.Surface.Scene.Tracks.Where(p => p.IsDrawn && p.Side == Side.Red).Select(p => p.Id).ToHashSet();
        var t = h.App.Data.Tracks.FirstOrDefault(x => x.Alive && x.Side == Side.Red && !x.IsMissile && drawn.Contains(x.Id) && x.DetectionFor(ViewSide.Blue) != TrackDetection.None && (where?.Invoke(x) ?? true));
        Assert.True(t is not null, "no hostile on the Blue picture");
        return t!;
    }

    /// <summary>Selects tracks by name (the panel shows the first).</summary>
    public static void Select(this UiHarness h, params string[] names)
    {
        var ids = names.Select(n => h.Track(n).Id).ToList();
        h.App.Select(ids, ids[0]);
        h.Pump(2);
        h.App.RefreshPanels();
        h.Pump(1);
    }

    // ------------------------------------------------------------ plot gestures

    /// <summary>Centres the active plot at (x, y) km with <paramref name="scale"/> px/km.</summary>
    public static void View(this UiHarness h, double x, double y, float scale)
    {
        h.Map().ViewModel.SetView((float)x, (float)y, scale, animate: false);
        h.Pump(3);
    }

    /// <summary>A plot point (km) in window coordinates.</summary>
    public static Point At(this UiHarness h, double x, double y)
    {
        var v = h.Map();
        var (sx, sy) = v.ViewModel.ToScreen((float)x, (float)y);
        return v.Plot.TranslatePoint(new Point(sx, sy), h.Win)!.Value;
    }

    public static (double X, double Y) Pos(this UiHarness h, TrackInfo t) => h.App.Data.PositionOf(t, h.App.Data.ViewTime);

    public static void RightClick(this UiHarness h, double x, double y, RawInputModifiers mods = RawInputModifiers.None)
    {
        var p = h.At(x, y);
        h.Win.MouseDown(p, MouseButton.Right, mods);
        h.Win.MouseUp(p, MouseButton.Right, mods);
        h.Pump(1);
    }

    public static void LeftClick(this UiHarness h, double x, double y, RawInputModifiers mods = RawInputModifiers.None)
    {
        var p = h.At(x, y);
        h.Win.MouseDown(p, MouseButton.Left, mods);
        h.Win.MouseUp(p, MouseButton.Left, mods);
        h.Pump(1);
    }

    /// <summary>A drag through plot points (km) with one mouse button; <paramref name="during"/> runs before the release.</summary>
    public static void Drag(this UiHarness h, MouseButton button, IReadOnlyList<(double X, double Y)> pts, Action? during = null, RawInputModifiers mods = RawInputModifiers.None)
    {
        var held = mods | (button == MouseButton.Right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton);
        var win = pts.Select(p => h.At(p.X, p.Y)).ToList(); // window points from the view before the drag (a pan moves the view)
        h.Win.MouseDown(win[0], button, mods);
        for (int i = 1; i < win.Count; i++) h.Win.MouseMove(win[i], held);
        h.Pump(1);
        during?.Invoke();
        h.Win.MouseUp(win[^1], button, mods);
        h.Pump(1);
    }

    /// <summary>Points from A to B every <paramref name="stepKm"/>.</summary>
    public static List<(double X, double Y)> Line((double X, double Y) a, (double X, double Y) b, double stepKm)
    {
        double d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        int n = Math.Max(1, (int)Math.Ceiling(d / stepKm));
        return Enumerable.Range(0, n + 1).Select(i => (a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n)).ToList();
    }

    // ------------------------------------------------------------ the checks

    /// <summary>The last c2.order.issue of this type: it went out and was accepted. Returns (args, reply).</summary>
    public static (JsonValue Args, JsonValue Reply) Issued(this UiHarness h, string type)
    {
        var rec = h.Cmd().History.LastOrDefault(r => r.Method == "c2.order.issue" && JsonParser.Parse(r.Args).GetString("type") == type);
        Assert.True(rec is not null, $"no c2.order.issue {type}; calls: {string.Join(" | ", h.Cmd().History.Select(r => r.Method + " " + r.Args))}");
        Assert.True(rec!.Ok, $"c2.order.issue {type} failed: {rec.Error}");
        return (JsonParser.Parse(rec.Args), JsonParser.Parse(rec.Reply!));
    }

    /// <summary>The last call of this method: it went out and succeeded.</summary>
    public static (JsonValue Args, JsonValue Reply) Called(this UiHarness h, string method)
    {
        var rec = h.Cmd().History.LastOrDefault(r => r.Method == method);
        Assert.True(rec is not null, $"no {method}; calls: {string.Join(" | ", h.Cmd().History.Select(r => r.Method))}");
        Assert.True(rec!.Ok, $"{method} failed: {rec.Error}");
        return (JsonParser.Parse(rec.Args), JsonParser.Parse(rec.Reply!));
    }

    /// <summary>A refused call of this method (the engine's error came back).</summary>
    public static CommandRecord Refused(this UiHarness h, string method)
    {
        var rec = h.Cmd().History.LastOrDefault(r => r.Method == method && !r.Ok);
        Assert.True(rec is not null, $"no refused {method}");
        return rec!;
    }

    /// <summary>The newest Orders line starting with <paramref name="start"/>.</summary>
    public static string Logged(this UiHarness h, string start)
    {
        var e = h.App.Data.Log.Entries.LastOrDefault(x => x.Category == LogCategory.Orders && x.Text.StartsWith(start, StringComparison.Ordinal));
        Assert.True(e.Text is not null, $"no Orders line starting \"{start}\"; last lines: {string.Join(" | ", h.App.Data.Log.Entries.TakeLast(6).Select(x => x.Text))}");
        return e.Text!;
    }

    public static bool HasLog(this UiHarness h, string contains) => h.App.Data.Log.Entries.Any(x => x.Text.Contains(contains, StringComparison.Ordinal));

    public static string ToastText(this UiHarness h)
    {
        var t = h.Map().Toast;
        Assert.True(t.IsVisible, "no toast");
        return ((TextBlock)t.Child!).Text ?? "";
    }

    /// <summary>Runs the demo battle forward <paramref name="simSeconds"/> (64× speed).</summary>
    public static void Run(this UiHarness h, double simSeconds) => h.FastForward(simSeconds);

    public static double Dist(SimObj o, double x, double y) => Math.Sqrt((o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y));

    public static double Dist(SimObj a, SimObj b) => Dist(a, b.X, b.Y);
}
