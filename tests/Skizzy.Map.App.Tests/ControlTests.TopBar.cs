using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Shell;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>Inventory rows 1–16: the top bar and the panel chrome (docs/M3-command-parity.md).</summary>
public class TopBarControlTests
{
    private static TopBar Top(UiHarness h) => h.Find<TopBar>()!;

    [AvaloniaFact]
    public void Control_PausePlay_TogglesTheSimClock()
    {
        using var h = Demo();
        var play = Btn(Top(h), "❚❚");
        h.Click(play);
        Assert.True(h.App.Paused);
        Assert.Equal("▶", play.Content);
        double t = h.App.Data.LiveTime;
        h.Pump(30);
        Assert.Equal(t, h.App.Data.LiveTime);
        h.Click(play);
        h.Pump(30);
        Assert.False(h.App.Paused);
        Assert.True(h.App.Data.LiveTime > t);
    }

    [AvaloniaFact]
    public void Control_SpeedSegment_SetsTheSimRate()
    {
        using var h = Demo();
        h.Click(Toggle(Top(h), "4×"));
        Assert.Equal(4, h.App.Source.Speed);
        double t0 = h.App.Data.LiveTime;
        for (int i = 0; i < 30; i++) h.App.Frame(1 / 30.0);
        Assert.InRange(h.App.Data.LiveTime - t0, 3.5, 4.5); // one wall second at 4×
    }

    [AvaloniaFact]
    public void Control_Restart_RestartsTheScenarioAndTheC2Engine()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.Key(PhysicalKey.R); // an order, so the C2 engine exists
        Assert.NotNull(h.Src().C2IfStarted);
        h.Run(60);
        h.Click(Btn(Top(h), "Restart"));
        Assert.True(h.App.Data.LiveTime < 2, $"T+{h.App.Data.LiveTime}");
        Assert.Null(h.Src().C2IfStarted);
        Assert.Empty(h.App.Data.Orders.Orders);
        Assert.Empty(h.App.Selection);
    }

    [AvaloniaFact]
    public void Control_StancePill_ShowsTheStance()
    {
        using var h = Demo();
        var pill = Top(h).GetLogicalDescendants().OfType<Border>().First(b => Text(b) == "BLUE ↔ RED · WAR");
        Assert.True(pill.IsEffectivelyVisible);
        Assert.Equal("Side stance", ToolTip.GetTip(pill));
    }

    [AvaloniaFact]
    public void Control_UtClock_ShowsTheScenarioUt()
    {
        using var h = Demo();
        var ut = Top(h).GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("ut"));
        Assert.Equal("Y1 D014 03:12:00", ut.Text);
        h.Run(65);
        Assert.Equal(h.App.Data.UtText(h.App.Data.ViewTime), ut.Text);
        Assert.NotEqual("Y1 D014 03:12:00", ut.Text);
    }

    [AvaloniaFact]
    public void Control_Advance_SetsSideDoctrineAndAircraftHoldTheirStations()
    {
        using var h = Demo();
        var adv = Top(h).AdvanceButton;
        h.Click(adv);
        var (args, _) = h.Called("c2.doctrine.set");
        Assert.Equal("side", args.GetString("level"));
        Assert.False(args.GetObj("values")!.GetBool("advanceOnContact", true));
        Assert.Equal("Advance: off", adv.Content);
        h.Logged("Advance on contact off: aircraft hold their stations until ordered.");
        Assert.False(h.Sim().AdvanceAuto);
        h.Run(400); // past the first identification
        Assert.False(h.HasLog("Advance on contact: "), "fighters advanced although advance on contact is off");
        h.Click(adv);
        h.Logged("Advance on contact on.");
        Assert.True(h.Sim().AdvanceAuto);
    }

    [AvaloniaFact]
    public void Control_AllAttack_ConfirmsThenEveryUnitHuntsAndTheDecksLaunch()
    {
        using var h = Demo();
        double now = 100;
        h.Cmd().Clock = () => now;
        var b = Top(h).AllAttackButton;
        h.Click(b);
        Assert.Equal("Confirm all attack", b.Content);
        Assert.StartsWith("Click All attack again (or Shift+A) within 3 s", h.ToastText());
        Assert.DoesNotContain(h.Cmd().History, r => r.Method == "c2.order.issue");
        now += 4; // the confirmation lapses
        h.Pump(1);
        Assert.Equal("All attack", b.Content);
        var kestrel = h.Unit("Kestrel");
        var deck = h.Sim().DeckOf(kestrel).ToList();
        Assert.NotEmpty(deck);
        h.Click(b);
        now += 1;
        h.Click(b);
        var (args, _) = h.Issued("all_attack");
        Assert.Equal("Blue", args.GetString("side"));
        string line = h.Logged("All Blue forces: attack nearest.");
        Assert.Contains("carriers launched", line);
        Assert.Empty(h.Sim().DeckOf(kestrel));
        h.Run(120);
        Assert.Contains(h.App.Data.Tracks, t => t.ControlText.StartsWith("Your order · attack nearest", StringComparison.Ordinal));
        Assert.Contains(deck, o => o.Alive && Dist(o, kestrel) > 10); // the launched fighters went hunting
    }

    [AvaloniaFact]
    public void Control_RtbAll_ConfirmsThenAircraftLandOnTheCarrier()
    {
        using var h = Demo();
        var b = Top(h).RtbAllButton;
        h.Click(b);
        Assert.Equal("Confirm RTB all", b.Content);
        Assert.Matches(@"^Press Shift\+R again \(or click RTB all again\) within 3 s to send all \d+ aircraft home to their carriers\.$", h.ToastText());
        h.Click(b);
        var (args, _) = h.Issued("rtb");
        Assert.StartsWith("All aircraft (", args.GetString("label"));
        Assert.Matches(@"^All aircraft \(\d+\): RTB to .*Kestrel\. \d+ aircraft to the marshal stack 8 km astern, then land one every 5 s\.", h.Logged("All aircraft ("));
        h.Run(400);
        Assert.True(h.HasLog("landed on Kestrel"), "no aircraft landed");
        Assert.True(h.Unit("Kestrel").Alive && h.Sim().DeckOf(h.Unit("Kestrel")).Any());
    }

    [AvaloniaFact]
    public void Control_Posture_SetsSideDoctrineAndTheCarrierHoldsItsAlertFighters()
    {
        using var h = Demo();
        h.Click(Toggle(Top(h).PostureSeg, "Defensive"));
        var (args, _) = h.Called("c2.doctrine.set");
        Assert.Equal("defensive", args.GetObj("values")!.GetString("posture"));
        h.Logged("Blue posture defensive: fighters hold their station, destroyers engage aircraft inside 60 km.");
        Assert.Equal("Defensive", h.Sim().Posture);
        h.Run(60); // proactive launches the alert fighters at T+20
        Assert.DoesNotContain(h.Sim().Objs, o => o.Name == "Lance 1");
        h.Click(Toggle(Top(h).PostureSeg, "Proactive"));
        h.Logged("Blue posture proactive:");
        h.Run(10);
        Assert.Contains(h.Sim().Objs, o => o.Name == "Lance 1");
    }

    [AvaloniaFact]
    public void Control_FriendlyFire_SetsTheMisfireRateAndLogs()
    {
        using var h = Demo();
        var ff = Top(h).FriendlyFireBox;
        Assert.True(ff.IsEffectivelyEnabled);
        h.KeyOn(ff, PhysicalKey.ArrowDown, 2); // "1 in 100 missiles"
        Assert.Equal(2, ff.SelectedIndex);
        Assert.Equal(.01, h.Sim().FriendlyFire);
        h.Logged("Friendly-fire simulation: about one Blue missile in 100 loses its target and finds a friend.");
        h.KeyOn(ff, PhysicalKey.ArrowUp, 2);
        Assert.Equal(0, ff.SelectedIndex);
        Assert.Equal(0, h.Sim().FriendlyFire);
        h.Logged("Friendly-fire simulation off.");
    }

    [AvaloniaFact]
    public void Control_Seat_HandsTheSeatToTheAgentAndBack()
    {
        using var h = Demo();
        var top = Top(h);
        h.Click(top.SeatButton);
        Assert.Equal("BLUE SEAT · AGENT (CLAUDE CODE)", Text(top.SeatPill));
        Assert.Equal("Take seat back", top.SeatButton.Content);
        h.Logged("Blue seat handed to agent (Claude Code). Missions and orders stay as they are.");
        h.Click(top.SeatButton);
        Assert.Equal("BLUE SEAT · YOU", Text(top.SeatPill));
        Assert.Equal("Hand seat to agent", top.SeatButton.Content);
        h.Logged("You took the Blue seat back. Agent orders are suspended, not deleted.");
    }

    [AvaloniaFact]
    public void Control_PanelToggles_ShowAndHideThePanels()
    {
        using var h = Demo();
        var tg = Toggle(Top(h), "Tracks");
        h.Click(tg);
        h.Pump(2);
        Assert.Null(h.Find<TracksView>());
        h.Click(tg);
        h.Pump(2);
        Assert.NotNull(h.Find<TracksView>());
    }

    [AvaloniaFact]
    public void Control_ResetLayout_RestoresTheDefaultPanels()
    {
        using var h = Demo();
        h.Click(Toggle(Top(h), "Unit"));
        h.Pump(2);
        Assert.Null(h.Find<UnitView>());
        h.Click(Btn(Top(h), "Reset layout"));
        h.Pump(3);
        Assert.NotNull(h.Find<UnitView>());
        Assert.NotNull(h.Find<TracksView>());
        Assert.Equal("Default layout restored.", h.ToastText());
    }

    // ------------------------------------------------------------ panel chrome

    [AvaloniaFact]
    public void Control_PanelClose_HidesThePanel()
    {
        using var h = Demo();
        var tracks = h.Find<TracksView>()!;
        // Dock's tool chrome close button for the Tracks panel
        var chrome = tracks.GetVisualAncestors().OfType<Dock.Avalonia.Controls.ToolChromeControl>().First();
        var close = chrome.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_CloseButton" || (b.Name ?? "").Contains("Close", StringComparison.Ordinal));
        h.Click(close);
        h.Pump(2);
        Assert.Null(h.Find<TracksView>());
        Assert.False(h.Win.Layouts.IsVisible(ShellDockFactory.Tracks));
    }

    private static Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter LeftSplitter(UiHarness h)
    {
        var tracks = h.Find<TracksView>()!;
        double right = tracks.TranslatePoint(new Avalonia.Point(tracks.Bounds.Width, 0), h.Win)!.Value.X;
        return h.Win.GetVisualDescendants().OfType<Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Height > s.Bounds.Width)
            .OrderBy(s => Math.Abs(s.TranslatePoint(new Avalonia.Point(0, 0), h.Win)!.Value.X - right)).First();
    }

    [AvaloniaFact]
    public void Control_SplitterDrag_ResizesThePanel()
    {
        using var h = Demo();
        var tracks = h.Find<TracksView>()!;
        double w0 = tracks.Bounds.Width;
        var sp = LeftSplitter(h);
        var p = Centre(h, sp);
        h.Win.MouseDown(p, MouseButton.Left);
        h.Win.MouseMove(p + new Avalonia.Point(30, 0), RawInputModifiers.LeftMouseButton);
        h.Win.MouseMove(p + new Avalonia.Point(60, 0), RawInputModifiers.LeftMouseButton);
        h.Win.MouseUp(p + new Avalonia.Point(60, 0), MouseButton.Left);
        h.Pump(3);
        Assert.InRange(tracks.Bounds.Width - w0, 40, 80);
    }

    [AvaloniaFact]
    public void Control_SplitterArrowKeys_ResizeBy20Px()
    {
        using var h = Demo();
        var tracks = h.Find<TracksView>()!;
        double w0 = tracks.Bounds.Width;
        var sp = LeftSplitter(h);
        Assert.True(sp.Focusable);
        sp.Focus();
        h.Key(PhysicalKey.ArrowRight);
        h.Pump(2);
        Assert.InRange(tracks.Bounds.Width - w0, 18, 22);
        h.Key(PhysicalKey.ArrowLeft);
        h.Key(PhysicalKey.ArrowLeft);
        h.Pump(2);
        Assert.InRange(w0 - tracks.Bounds.Width, 18, 22);
        Assert.Empty(h.App.Selection); // the arrows did not pan the plot or reach other keys
    }
}
