using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>Inventory rows 64–79: the unit panel (docs/M3-command-parity.md).</summary>
public class UnitPanelControlTests
{
    private static IEnumerable<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    private static ToggleButton Seg(UiHarness h, string text) => Toggle(h.UnitPanel(), text);

    [AvaloniaFact]
    public void Control_UnitHeader_ShowsTheStateAndValues()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        var p = h.UnitPanel();
        Assert.Contains("Hawk 1", Texts(p));
        Assert.Contains(Texts(p), t => t.StartsWith("MISSION", StringComparison.Ordinal));
        Assert.Contains("Escort", Texts(p)); // the mission
        var kv = Texts(p).ToList();
        Assert.Contains(kv, t => t.EndsWith(" m/s", StringComparison.Ordinal));
        var bars = p.GetLogicalDescendants().OfType<MagBar>().ToList();
        Assert.Contains(bars, b => Texts(b).Contains("AAM"));
        Assert.Contains(bars, b => Texts(b).Contains("Fuel"));
        // an order shows in the control state
        h.View(-20, 10, 4);
        var e = h.Empty(-60, 10);
        h.RightClick(e.X, e.Y);
        h.Run(2);
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Contains(Texts(h.UnitPanel()), t => t.StartsWith("YOUR ORDER · MOVE", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Control_MakeGroup_GroupsTheSelection()
    {
        using var h = Demo();
        h.Select("Hawk 1", "Hawk 2");
        var box = h.UnitPanel().GetLogicalDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Group name, e.g. Strike A");
        h.Type(box, "Strike A");
        h.Click(Btn(h.UnitPanel(), "Make group"));
        h.Logged("New group \"Strike A\": 2 units (Hawk 1, Hawk 2).");
        Assert.Equal("Group \"Strike A\" created with 2 units.", h.ToastText());
        Assert.Equal("Strike A", h.App.Data.GroupOf(h.Track("Hawk 2")));
        h.Pump(2);
        Chip(h.Find<TracksView>()!, "Strike A");
    }

    [AvaloniaFact]
    public void Control_TakeControl_HoldsTheUnitAndReturnToMissionReleasesIt()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        var u = h.Unit("Hawk 1");
        double x0 = u.X, y0 = u.Y;
        h.Click(Btn(h.UnitPanel(), "Take control"));
        var (args, _) = h.Issued("move_to");
        Assert.Equal("take_control", args.GetString("label"));
        h.Logged("You took control of Hawk 1. The mission is suspended, not cancelled.");
        Assert.Contains(h.Track("Hawk 1").Id, h.Cmd().Manual);
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Contains("MANUAL · YOU", Texts(h.UnitPanel()));
        h.Run(120);
        Assert.True(Dist(u, x0, y0) < 6, $"Hawk 1 drifted {Dist(u, x0, y0):0.0} km while under your control");
        var other = h.Unit("Hawk 2");

        h.Click(Btn(h.UnitPanel(), "Return to mission"));
        h.Called("c2.unit.release");
        h.Logged("Hawk 1 returned to Escort. The mission re-plans from the unit's position.");
        Assert.Empty(h.Cmd().Manual);
        h.Run(30);
        Assert.DoesNotContain("Your order", h.Track("Hawk 1").ControlText);
        Assert.Equal(other.Ctrl, u.Ctrl); // back under its mission like the rest of the flight
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Equal("Take control", Btn(h.UnitPanel(), "Take control").Content);
    }

    [AvaloniaFact]
    public void Control_AttackNearest_HuntsLaunchesTheDeckAndRefusesUnarmedUnits()
    {
        using var h = Demo();
        h.Run(300); // hostiles on the picture (with none, the hunt ends: "nothing left to hunt")
        h.Select("Hawk 1", "Watch 1", "Kestrel");
        var deck = h.Sim().DeckOf(h.Unit("Kestrel")).ToList();
        h.Click(Btn(h.UnitPanel(), "Attack nearest"));
        h.Issued("attack_nearest");
        string line = h.Logged("Selection: attack nearest.");
        Assert.Contains("carriers launched", line);
        Assert.Contains("Not armed for this: Watch 1.", line);
        Assert.Empty(h.Sim().DeckOf(h.Unit("Kestrel")));
        h.Run(10);
        Assert.StartsWith("Your order · attack nearest", h.Track("Hawk 1").ControlText);
        Assert.True(h.HasLog("Hawk 1: hunting "), "Hawk 1 picked no target");
        h.Run(50);
        Assert.Contains(deck, o => o.Alive && Dist(o, h.Unit("Kestrel")) > 5);
    }

    [AvaloniaFact]
    public void Control_RtbAndLand_SendsAircraftHomeAndRefusesTheCarrier()
    {
        using var h = Demo();
        h.Select("Hawk 2", "Kestrel");
        h.Click(Btn(h.UnitPanel(), "RTB & land"));
        h.Issued("rtb");
        h.Logged("Hawk 2: RTB to Kestrel.");
        Assert.Equal("RTB refused: Kestrel is a carrier (a base).", h.Logged("RTB refused"));
        Assert.Equal("RTB refused: Kestrel is a carrier (a base).", h.ToastText());
        for (int i = 0; i < 20 && !h.HasLog("Hawk 2 landed on Kestrel"); i++) h.Run(30);
        Assert.True(h.HasLog("Hawk 2 landed on Kestrel"), "Hawk 2 did not land");
    }

    [AvaloniaFact]
    public void Control_SendToButton_TogglesThePendingMove()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        var b = Seg(h, "Send to…");
        h.Click(b);
        Assert.Equal(PendingMode.SendTo, h.Cmd().Pending);
        Assert.True(b.IsChecked);
        Assert.Equal("Click the plot where the selected units should go, or click a hostile to attack it.", h.ToastText());
        h.Click(b);
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        Assert.False(b.IsChecked);
    }

    [AvaloniaFact]
    public void Control_MarkVip_TogglesVip()
    {
        using var h = Demo();
        h.Select("Halyard");
        var b = Seg(h, "Mark VIP");
        h.Click(b);
        Assert.True(h.Called("c2.vip.set").Args.GetBool("vip"));
        Assert.True(h.Unit("Halyard").Vip);
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Equal("VIP ✓", b.Content);
        Assert.True(b.IsChecked);
        h.Click(b);
        Assert.False(h.Unit("Halyard").Vip);
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Equal("Mark VIP", b.Content);
        Assert.False(b.IsChecked);
    }

    [AvaloniaFact]
    public void Control_FlyInKsp_ExplainsTheRealBuild()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.Click(Btn(h.UnitPanel(), "Fly in KSP"));
        Assert.Equal("In the real build this makes Hawk 1 the active vessel in KSP so you can fly it yourself. The map keeps tracking it.", h.ToastText());
    }

    [AvaloniaFact]
    public void Control_Center_CentresThePlotOnTheUnit()
    {
        using var h = Demo();
        h.View(250, -250, 4);
        h.Select("Hawk 1");
        h.Click(Btn(h.UnitPanel(), "Center"));
        h.Pump(60);
        var p = h.Pos(h.Track("Hawk 1"));
        var m = h.Map().ViewModel;
        Assert.InRange(m.CenterX, p.X - 2, p.X + 2);
        Assert.InRange(m.CenterY, p.Y - 2, p.Y + 2);
    }

    [AvaloniaFact]
    public void Control_RacetrackButton_TogglesThePendingRacetrack()
    {
        using var h = Demo();
        h.Select("Viper 1");
        var b = Seg(h, "Racetrack…");
        h.Click(b);
        Assert.Equal(PendingMode.Racetrack, h.Cmd().Pending);
        Assert.True(b.IsChecked);
        Assert.Equal("Racetrack: drag on the plot along the long leg. A plain click makes a 20 km leg pointing east. Esc cancels.", h.ToastText());
        h.Click(b);
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        Assert.False(b.IsChecked);
    }

    [AvaloniaFact]
    public void Control_LaunchReadyAircraft_LaunchesTheDeck()
    {
        using var h = Demo();
        h.Select("Kestrel");
        var k = h.Unit("Kestrel");
        var deck = h.Sim().DeckOf(k).ToList();
        Assert.Equal(6, deck.Count);
        var launch = Btn(h.UnitPanel(), "Launch ready aircraft");
        h.Click(launch);
        var (args, reply) = h.Called("demo.deck.launch");
        Assert.False(args.GetBool("hunt", true));
        Assert.Equal(6, reply.GetInt("launched"));
        h.Logged("Kestrel launched 6 aircraft (6 aircraft). They return to their missions.");
        Assert.Empty(h.Sim().DeckOf(k));
        h.Run(60);
        Assert.All(deck, o => Assert.True(o.Alive && Dist(o, k) > 3, $"{o.Name} is still at the carrier"));
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.False(Btn(h.UnitPanel(), "Launch ready aircraft").IsEffectivelyEnabled); // nothing ready now
    }

    [AvaloniaFact]
    public void Control_WeaponsControl_SetsUnitDoctrine()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.Click(Seg(h, "Hold"));
        var (args, _) = h.Called("c2.doctrine.set");
        Assert.Equal("unit", args.GetString("level"));
        Assert.Equal("hold", args.GetObj("values")!.GetString("weaponsControl"));
        h.Logged("Hawk 1: weapons hold (passive).");
        Assert.Equal("Hold", h.Unit("Hawk 1").Doctrine);
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.True(Seg(h, "Hold").IsChecked);
        Assert.False(Seg(h, "Free").IsChecked);
        h.Click(Seg(h, "Free"));
        h.Logged("Hawk 1: weapons free (guard).");
        Assert.Equal("Free", h.Unit("Hawk 1").Doctrine);
    }

    [AvaloniaFact]
    public void Control_Radar_SetsShipEmconAndPassiveStopsSamFire()
    {
        using var h = Demo();
        h.Select("Halyard");
        h.Click(Seg(h, "Passive"));
        var (args, _) = h.Called("demo.unit.emcon");
        Assert.Equal("passive", args.GetString("emcon"));
        h.Logged("Halyard: radar passive.");
        var hal = h.Unit("Halyard");
        Assert.Equal("Passive", hal.Emcon);
        static int Sams(SimObj s) => s.Mag!.Where(kv => kv.Key != "CIWS").Sum(kv => kv.Value[0]);
        int before = Sams(hal);
        var others = h.Sim().Objs.Where(o => o.Side == "blue" && o.Kind == "ddg" && o != hal).ToDictionary(o => o, Sams);
        for (int i = 0; i < 10 && !others.Any(kv => Sams(kv.Key) < kv.Value); i++) h.Run(200);
        Assert.True(others.Any(kv => Sams(kv.Key) < kv.Value), "no destroyer fired a SAM");
        Assert.Equal(before, Sams(hal)); // passive radar: Halyard held its SAMs
    }

    [AvaloniaFact]
    public void Control_OnContact_SetsUnitDoctrine()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.Click(Seg(h, "Engage on contact"));
        var (args, _) = h.Called("c2.doctrine.set");
        Assert.Equal("engage", args.GetObj("values")!.GetString("onContact"));
        h.Logged("Hawk 1: engage any hostile detected during orders.");
        Assert.Equal("engage", h.Unit("Hawk 1").OnContact);
        h.Click(Seg(h, "Stay on task"));
        h.Logged("Hawk 1: stay on task during orders.");
        Assert.Equal("task", h.Unit("Hawk 1").OnContact);
    }

    [AvaloniaFact]
    public void Control_SymbolSize_SmallerLargerReset()
    {
        using var h = Demo();
        h.Select("Kestrel");
        h.Click(Btn(h.UnitPanel(), "Larger"));
        Assert.Equal(1.25f, h.App.SizeMul[1], 3);
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Contains("×1.25", Texts(h.UnitPanel()));
        h.Click(Btn(h.UnitPanel(), "Smaller"));
        Assert.Equal(1f, h.App.SizeMul.GetValueOrDefault(1, 1f), 3);
        h.Click(Btn(h.UnitPanel(), "Smaller"));
        Assert.Equal(.8f, h.App.SizeMul[1], 3);
        h.Click(Btn(h.UnitPanel(), "Reset"));
        Assert.Equal(1f, h.App.SizeMul.GetValueOrDefault(1, 1f), 3);
        h.Pump(2);
        Assert.Equal(h.Map().ViewModel.Sync.Find(2)!.SizeMultiplier, h.Map().ViewModel.Sync.Find(1)!.SizeMultiplier, 3);
    }

    [AvaloniaFact]
    public void Control_FlightDeckReadout_ShowsTheDeck()
    {
        using var h = Demo();
        h.Select("Kestrel");
        Assert.Contains("6 aircraft on deck, 6 ready", Texts(h.UnitPanel()));
        Assert.Contains(Texts(h.UnitPanel()), t => t.Contains("Ghost 1", StringComparison.Ordinal));
        h.Click(Btn(h.UnitPanel(), "Launch ready aircraft"));
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Contains("Deck empty", Texts(h.UnitPanel()));
        h.Select("Hawk 2");
        h.Key(PhysicalKey.R);
        for (int i = 0; i < 20 && !h.HasLog("Hawk 2 landed on Kestrel"); i++) h.Run(30);
        h.Select("Kestrel");
        Assert.Contains(Texts(h.UnitPanel()), t => t.StartsWith("1 aircraft on deck, ", StringComparison.Ordinal));
    }
}
