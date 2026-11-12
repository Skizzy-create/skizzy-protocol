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

    // ------------------------------------------------------------ the M2 tactical stream: teams, orders, deaths

    private static void Until(UiHarness h, Func<bool> cond, string what, double seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(30);
            h.Pump(1);
        }
        Assert.True(cond(), what);
    }

    /// <summary>
    /// FakeKSP with a BDArmory binding: Hornet 1 (team A, first seen) and team B's Kirov and Viper 1, Viper being KSP's
    /// active vessel. Returns once the map is on source.tactical with the three units.
    /// </summary>
    private static UiHarness Tactical(out FakeKspDataSource src, out FakeBda bda, out uint viperId, out uint hornetId)
    {
        src = FakeKspDataSource.Start();
        var h = new UiHarness(src);
        var s = src;
        Until(h, () => s.StreamKind is not null && h.App.Data.Tracks.Any(t => t.Alive && t.Control == ControlState.Manual), "no active vessel on the vessel stream");
        uint activePid = Pid(h.App.Data.Tracks.First(t => t.Alive && t.Control == ControlState.Manual));
        var b = new FakeBda();
        var sim = src.Sim;
        FakeUnit hornet = null!;
        OnMain(sim, () =>
        {
            hornet = b.Add("Hornet 1", "A", UnitClass.Fighter, 0, 0);
            var kirov = b.Add("Kirov", "B", UnitClass.Ship, 40, 0);
            var viper = b.Add("Viper 1", "B", UnitClass.Fighter, 40, 5);
            b.Units.Remove(viper.Id);
            viper.S.Id = activePid;
            b.Units[activePid] = viper;
            foreach (var u in new[] { hornet, kirov, viper }) { u.Scripted = true; u.S.GuardMode = false; u.S.SpeedMps = 0; }
            b.Sink = sim.Host.C2.Attach(b, b, "fakebda");
        });
        Until(h, () => s.StreamKind == "tactical", "the map did not move up to source.tactical", 15);
        var d = h.App.Data;
        Until(h, () => d.Tracks.Any(t => t.Alive && t.Name == "Hornet 1") && d.Tracks.Any(t => t.Alive && t.Name == "Kirov")
                       && d.Tracks.Any(t => t.Alive && t.Name == "Viper 1" && t.Team == "B"), "the units are not on the map");
        bda = b;
        viperId = activePid;
        hornetId = hornet.Id;
        return h;
    }

    [AvaloniaFact]
    public void Live_Tactical_ActiveVesselsTeamIsBlue_AndEitherTeamTakesMoveOrders()
    {
        using var h = Tactical(out var src, out var bda, out uint viperId, out uint hornetId);
        var sim = src.Sim;
        var d = h.App.Data;
        // the commanded team follows KSP's active vessel (Viper, team B), not the order the teams appeared in
        Until(h, () => src.OwnTeam == "B", "the active vessel's team is not commanded: " + src.OwnTeam);
        h.Pump(2);
        var vt = h.Track("Viper 1");
        var kt = h.Track("Kirov");
        var ht = h.Track("Hornet 1");
        Until(h, () => vt.Side == Side.Blue && kt.Side == Side.Blue && ht.Side == Side.Red, $"sides: Viper {vt.Side}, Kirov {kt.Side}, Hornet {ht.Side}");
        Assert.Equal(ControlState.Manual, vt.Control); // isActive comes through the tactical stream
        Assert.True(h.Cmd().IsOwn(vt));
        Assert.False(h.Cmd().IsOwn(ht));

        // select Viper, right-click empty plot: move_to for team B, the waypoint reaches BDArmory
        h.Select("Viper 1");
        var p = h.Focus(vt, 10);
        var e = h.Empty(p.X + 6, p.Y + 6);
        h.RightClick(e.X, e.Y);
        Idle(h);
        var (args, reply) = h.Issued("move_to");
        Assert.Equal("B", args.GetString("side"));
        Assert.Equal(1, reply.GetInt("accepted"));
        h.Logged("Viper 1: move to bearing");
        BdaCall(sim, bda, "F6 set " + viperId);

        // the context menu's "Command team A" (on Hornet): Hornet turns blue and takes orders, B turns red
        h.Cmd().CommandTeamOf(ht);
        Assert.Equal("A", src.OwnTeam);
        Assert.Equal(Side.Blue, ht.Side);
        Assert.Equal(Side.Red, kt.Side);
        Assert.Empty(h.App.Selection);
        h.Logged("You now command team A.");
        h.Select("Hornet 1");
        p = h.Focus(ht, 10);
        e = h.Empty(p.X + 6, p.Y + 6);
        h.RightClick(e.X, e.Y);
        Idle(h);
        (args, reply) = h.Issued("move_to");
        Assert.Equal("A", args.GetString("side"));
        Assert.Equal(1, reply.GetInt("accepted"));
        BdaCall(sim, bda, "F6 set " + hornetId);
        // a picked team stays picked: the active vessel (team B) no longer moves it
        for (int i = 0; i < 4; i++) { OnMain(sim, () => bda.Step(0.2)); Thread.Sleep(60); h.Pump(1); }
        Assert.Equal("A", src.OwnTeam);
        Assert.Equal(Side.Red, h.Track("Viper 1").Side);
    }

    /// <summary>Advances FakeBda's game clock 0.2 s at a time (the host samples on game time) until the condition holds; returns the steps.</summary>
    private static int StepUntil(UiHarness h, SimHost sim, FakeBda bda, Func<bool> cond, string what)
    {
        for (int i = 1; i <= 40; i++)
        {
            OnMain(sim, () => bda.Step(0.2));
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 250) { Thread.Sleep(15); h.Pump(1); if (cond()) return i; }
        }
        Assert.Fail(what);
        return -1;
    }

    [AvaloniaFact]
    public void Live_Tactical_DestroyedUnitEndsOnTheNextFrame()
    {
        using var h = Tactical(out var src, out var bda, out uint viperId, out uint hornetId);
        var sim = src.Sim;
        var d = h.App.Data;
        var vt = h.Track("Viper 1");
        OnMain(sim, () => bda.Kill(viperId));
        int frames = StepUntil(h, sim, bda, () => !vt.Alive, "the destroyed unit is still alive on the map");
        Assert.True(frames <= 3, $"the destroyed unit ended after {frames} game steps of 0.2 s");
        // the host's "dead" row ended it (not the missed-frames fallback, which logs "left the picture")
        Assert.Contains(d.Log.Entries, x => x.Category == LogCategory.Damage && x.Text == "Viper 1 destroyed.");
        Assert.DoesNotContain(d.Log.Entries, x => x.Text.StartsWith("Viper 1 left the picture", StringComparison.Ordinal));
        // and a unit taken out of the world entirely (KSP destroyed the vessel) ends the same way
        var ht = h.Track("Hornet 1");
        OnMain(sim, () => bda.Remove(hornetId));
        frames = StepUntil(h, sim, bda, () => !ht.Alive, "the removed unit is still alive on the map");
        Assert.True(frames <= 3, $"the removed unit ended after {frames} game steps of 0.2 s");
        Assert.Contains(d.Log.Entries, x => x.Category == LogCategory.Damage && x.Text == "Hornet 1 destroyed.");
        // the Losses tab counts both from the destructions (no BDArmory competition running): Viper is team B, the
        // commanded team (KSP's active vessel), so Blue; Hornet is team A, Red
        Assert.Contains(d.Losses.Blue.Lost, kv => kv.Key == "Fighter" && kv.Value == 1);
        Assert.Contains(d.Losses.Red.Lost, kv => kv.Key == "Fighter" && kv.Value == 1);
    }

    [AvaloniaFact]
    public void Live_Tactical_RevertTakesTimeBack_TheMapStartsOverAndKeepsUpdating()
    {
        using var h = Tactical(out var src, out var bda, out _, out _);
        var sim = src.Sim;
        var d = h.App.Data;
        // play on a while, then revert: the binding detaches, KSP reloads the scene 60 s earlier, the binding attaches again
        StepUntil(h, sim, bda, () => d.LiveTime > 1.5, "game time did not run");
        var bda2 = new FakeBda { Now = bda.Now - 60 };
        OnMain(sim, () =>
        {
            sim.Host.C2.Detach("scene change");
            var a = bda2.Add("Hornet 1", "A", UnitClass.Fighter, 0, 0);
            var k = bda2.Add("Kirov", "B", UnitClass.Ship, 40, 0);
            foreach (var u in new[] { a, k }) { u.Scripted = true; u.S.GuardMode = false; u.S.SpeedMps = 0; }
            bda2.Sink = sim.Host.C2.Attach(bda2, bda2, "fakebda");
        });
        StepUntil(h, sim, bda2, () => d.Log.Entries.Any(x => x.Text.StartsWith("Game time went back", StringComparison.Ordinal)), "the map did not notice the revert");
        // the stream did not stall waiting for the old time: the new scene's units come and keep moving on the map
        var t0 = d.LiveTime;
        StepUntil(h, sim, bda2, () => d.Tracks.Any(t => t.Alive && t.Name == "Kirov") && d.LiveTime > t0 + 0.5, "no frames after the revert");
        Assert.DoesNotContain(d.Tracks, t => t.Alive && t.Name == "Viper 1"); // the old picture is gone
        Assert.Equal(SourceState.Online, src.State);
    }

    [AvaloniaFact]
    public void Control_ReconnectButton_ShowsForLiveAndReconnects()
    {
        using var h = Tactical(out var src, out _, out _, out _);
        var bar = h.Find<TopBar>()!;
        var btn = bar.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Reconnect");
        Assert.True(btn.IsVisible);
        h.Click(btn);
        Until(h, () => h.App.Data.Log.Entries.Any(x => x.Text == "Reconnecting…"), "no reconnect");
        Until(h, () => src.State == SourceState.Online && src.StreamKind == "tactical", "did not come back online on the tactical stream", 20);
    }
}
