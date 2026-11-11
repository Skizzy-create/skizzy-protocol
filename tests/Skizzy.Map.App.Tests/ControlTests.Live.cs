using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Skizzy.C2;
using Skizzy.C2.Fake;
using Skizzy.Host.Core;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Shell;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Live;
using Skizzy.Map.Render.Model;
using Skizzy.Sim;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>
/// The live path: the same controls against FakeKSP (the in-process Skizzy host over the M0 vessel stream). Orders
/// go through SkizzyClient as c2.* calls and reach the host's C2 engine and, through it, the BDArmory-level control
/// (FakeBda in place of the BDArmory binding). The controls the host cannot perform are disabled with their reasons.
/// </summary>
public class LiveControlTests
{
    private static UiHarness Live(out FakeKspDataSource src)
    {
        src = FakeKspDataSource.Start();
        var h = new UiHarness(src);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && h.App.Data.Tracks.Count(t => t.Alive) < 5)
        {
            Thread.Sleep(30);
            h.Pump(1);
        }
        Assert.True(h.App.Data.Tracks.Count(t => t.Alive) >= 5, "FakeKSP sent no vessels");
        Assert.True(h.Cmd().CanCommand);
        return h;
    }

    private static void OnMain(SimHost sim, Action a)
    {
        using var done = new ManualResetEventSlim();
        Exception? err = null;
        sim.Host.Dispatcher.EnqueueMain(new Job { Action = () => { try { a(); } catch (Exception e) { err = e; } finally { done.Set(); } } });
        Assert.True(done.Wait(5000), "main-thread action timed out");
        if (err is not null) throw new InvalidOperationException("main-thread action failed", err);
    }

    /// <summary>Pumps the window until the command service has no call in flight (the replies come back over the socket).</summary>
    private static void Idle(UiHarness h)
    {
        var t = h.Cmd().WhenIdle();
        var sw = Stopwatch.StartNew();
        while (!t.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(10);
            h.Pump(1);
        }
        Assert.True(t.IsCompleted, "the c2.* calls did not finish");
        h.Pump(1);
    }

    private static uint Pid(TrackInfo t) => uint.Parse(t.Key["vessel:v:".Length..], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Waits until the BDArmory-level control has seen a call starting with <paramref name="prefix"/>.</summary>
    private static void BdaCall(SimHost sim, FakeBda bda, string prefix)
    {
        var sw = Stopwatch.StartNew();
        List<string> calls = [];
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            OnMain(sim, () => calls = bda.Calls.ToList());
            if (calls.Any(c => c.StartsWith(prefix, StringComparison.Ordinal))) return;
            Thread.Sleep(50);
        }
        Assert.Fail($"no BDA call \"{prefix}…\"; calls: {string.Join(" | ", calls)}");
    }

    [AvaloniaFact]
    public void Control_LivePath_C2CallsArriveAtTheHost()
    {
        using var h = Live(out var src);
        var sim = src.Sim;
        var f14 = h.Track("F-14 Tomcat");
        var ffg = h.Track("FFG-3 Neustrashimy");

        // attach the C2 engine as the BDArmory binding does in a flight scene, with the vessels' pids as unit ids
        var bda = new FakeBda();
        void Unit(TrackInfo t, string side, UnitClass cls, double e, double n)
        {
            var u = bda.Add(t.Name, side, cls, e, n);
            bda.Units.Remove(u.Id);
            u.S.Id = Pid(t);
            bda.Units[u.S.Id] = u;
        }
        Unit(h.Track("DDG-1 Kirov"), "Blue", UnitClass.Ship, 0, -5);
        Unit(h.Track("DDG-2 Udaloy"), "Blue", UnitClass.Ship, 3, -5);
        Unit(f14, "Blue", UnitClass.Fighter, 0, 0);
        Unit(ffg, "Red", UnitClass.Ship, 10, 0);
        OnMain(sim, () => bda.Sink = sim.Host.C2.Attach(bda, bda, "fakebda"));

        // right-click on empty plot: move_to through SkizzyClient
        h.Select("F-14 Tomcat");
        var p = h.Focus(f14, 10);
        var e = h.Empty(p.X + 6, p.Y + 6);
        h.RightClick(e.X, e.Y);
        Idle(h);
        var (_, status) = h.Called("c2.unit.status");
        Assert.Equal("Blue", status.GetString("side"));
        var (args, reply) = h.Issued("move_to");
        Assert.Equal("Blue", args.GetString("side"));
        Assert.Equal("v:" + Pid(f14), args.GetArr("units")![0].AsString());
        Assert.Equal(1, reply.GetInt("accepted"));
        h.Logged("F-14 Tomcat: move to bearing");
        BdaCall(sim, bda, "F6 set " + Pid(f14)); // the waypoint

        // V: c2.vip.set
        h.Key(PhysicalKey.V);
        Idle(h);
        Assert.True(h.Called("c2.vip.set").Reply.GetBool("vip"));
        h.Logged("F-14 Tomcat marked VIP.");

        // weapons control Hold: unit doctrine, applied as BDA fire authority
        h.App.RefreshPanels();
        h.Pump(1);
        h.Click(Toggle(h.UnitPanel(), "Hold"));
        Idle(h);
        var (dargs, _) = h.Called("c2.doctrine.set");
        Assert.Equal("unit", dargs.GetString("level"));
        Assert.Equal("hold", dargs.GetObj("values")!.GetString("weaponsControl"));
        h.Logged("F-14 Tomcat: weapons hold (passive).");
        BdaCall(sim, bda, "F3 " + Pid(f14));
        h.Click(Toggle(h.UnitPanel(), "Free"));
        Idle(h);

        // right-click a vessel the engine puts on another side: engage
        var q = h.Focus(ffg, 40);
        h.RightClick(q.X, q.Y);
        Idle(h);
        var (eargs, ereply) = h.Issued("engage");
        Assert.Equal("v:" + Pid(ffg), eargs.GetString("target"));
        Assert.Equal(1, ereply.GetInt("accepted"));
        h.Logged("F-14 Tomcat: attack FFG-3 Neustrashimy");
        BdaCall(sim, bda, $"F1 set {Pid(f14)} {Pid(ffg)}");

        // R on the F-14: RTB is refused by the engine with its reason (no carrier or airfield on the live side)
        h.Select("F-14 Tomcat");
        h.Key(PhysicalKey.R);
        Idle(h);
        Assert.Contains(h.Cmd().History, r => r.Method == "c2.order.issue" && r.Args.Contains("\"rtb\"", StringComparison.Ordinal));
        Assert.Contains(h.App.Data.Log.Entries, x => x.Category == LogCategory.Orders && (x.Text.StartsWith("RTB refused", StringComparison.Ordinal) || x.Text.StartsWith("F-14 Tomcat: RTB", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public void Control_LiveExceptions_AreDisabledWithTheirReasons()
    {
        using var h = Live(out _);
        var top = h.Find<TopBar>()!;
        Assert.False(top.FriendlyFireBox.IsEnabled);
        Assert.Equal(Unsupported.FriendlyFire, ToolTip.GetTip(top.FriendlyFireBox));
        Assert.False(top.SeatButton.IsEnabled);
        Assert.Equal(Unsupported.Seat, ToolTip.GetTip(top.SeatButton));

        h.Select("DDG-1 Kirov");
        var panel = h.UnitPanel();
        var fly = Btn(panel, "Fly in KSP");
        Assert.False(fly.IsEnabled);
        Assert.Equal(Unsupported.FlyInKsp, ToolTip.GetTip(fly));
        var radar = panel.GetLogicalDescendants().OfType<Segmented>().First(s => s.Buttons.Any(b => b.Content as string == "Passive"));
        Assert.False(radar.IsEnabled);
        Assert.Equal(Unsupported.Radar, ToolTip.GetTip(radar));
        // the orders themselves are enabled on the live host (the engine answers)
        Assert.True(Btn(panel, "Take control").IsEnabled);
        Assert.True(Btn(panel, "Attack nearest").IsEnabled);

        // a carrier's deck launch (live vessels never classify as carriers yet, so make one)
        var t = h.Track("DDG-1 Kirov");
        t.Kind = TrackKind.Carrier;
        t.Details.DeckCount = 2;
        t.Details.DeckReady = 2;
        panel.Refresh();
        var launch = Btn(panel, "Launch ready aircraft");
        Assert.False(launch.IsEnabled);
        Assert.Equal(Unsupported.DeckLaunch, ToolTip.GetTip(launch));

        // without the BDArmory binding an order is refused with the host's reason, in the log
        h.Select("F-14 Tomcat");
        var p = h.Focus(h.Track("F-14 Tomcat"), 10);
        var e = h.Empty(p.X + 6, p.Y + 6);
        h.RightClick(e.X, e.Y);
        Idle(h);
        Assert.StartsWith("Move refused: the C2 engine is not attached", h.Logged("Move refused"));
    }
}
