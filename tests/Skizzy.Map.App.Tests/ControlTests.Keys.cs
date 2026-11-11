using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Skizzy.Map.App.Services;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>Inventory rows 52–63: the keyboard (docs/M3-command-parity.md).</summary>
public class KeyControlTests
{
    [AvaloniaFact]
    public void Control_Space_PausesAndPlays()
    {
        using var h = Demo();
        h.Key(PhysicalKey.Space);
        Assert.True(h.App.Paused);
        double t = h.App.Data.LiveTime;
        h.Pump(30);
        Assert.Equal(t, h.App.Data.LiveTime);
        h.Key(PhysicalKey.Space);
        Assert.False(h.App.Paused);
        h.Pump(30);
        Assert.True(h.App.Data.LiveTime > t);
    }

    [AvaloniaFact]
    public void Control_F_FitsAllAndShiftFFitsTheSelection()
    {
        using var h = Demo();
        var m = h.Map().ViewModel;
        h.View(300, 300, 30);
        h.Key(PhysicalKey.F);
        h.Pump(60);
        float all = m.Scale;
        Assert.True(all < 3, $"scale {all}");
        Assert.InRange(m.CenterX, -100, 250); // the whole battle, not the old corner
        h.Select("Kestrel");
        h.Key(PhysicalKey.F, RawInputModifiers.Shift);
        h.Pump(60);
        var k = h.Pos(h.Track("Kestrel"));
        Assert.True(m.Scale > all * 4, $"scale {m.Scale} vs fit all {all}");
        Assert.InRange(m.CenterX, k.X - 1, k.X + 1);
        Assert.InRange(m.CenterY, k.Y - 1, k.Y + 1);
    }

    [AvaloniaFact]
    public void Control_Escape_CancelsAPendingModeThenClearsTheSelection()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.Key(PhysicalKey.T);
        Assert.Equal(PendingMode.Racetrack, h.Cmd().Pending);
        h.Key(PhysicalKey.Escape);
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        Assert.Single(h.App.Selection); // the first Esc only cancelled the racetrack

        // a racetrack being dragged: Esc drops it and the release orders nothing
        h.View(40, 20, 4);
        h.Key(PhysicalKey.T);
        var a = h.Empty(30, 40);
        var win = Line(a, (60, 40), 3).Select(p => h.At(p.X, p.Y)).ToList();
        h.Win.MouseDown(win[0], MouseButton.Left);
        foreach (var p in win.Skip(1)) h.Win.MouseMove(p, RawInputModifiers.LeftMouseButton);
        h.Pump(1);
        Assert.True(h.Map().Preview.IsVisible);
        h.Key(PhysicalKey.Escape);
        Assert.False(h.Map().Preview.IsVisible);
        h.Win.MouseUp(win[^1], MouseButton.Left);
        h.Pump(1);
        Assert.DoesNotContain(h.Cmd().History, r => r.Method == "c2.order.issue");
        Assert.Equal(PendingMode.None, h.Cmd().Pending);

        h.Click(Toggle(h.UnitPanel(), "Send to…"));
        Assert.Equal(PendingMode.SendTo, h.Cmd().Pending);
        h.Key(PhysicalKey.Escape);
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        Assert.Single(h.App.Selection);
        h.Key(PhysicalKey.Escape);
        Assert.Empty(h.App.Selection);
    }

    [AvaloniaFact]
    public void Control_V_TogglesVipOnTheSelection()
    {
        using var h = Demo();
        h.App.ClearSelection();
        h.Key(PhysicalKey.V);
        Assert.Equal("Select one or more of your own units, then press V or Mark VIP.", h.ToastText());
        h.Select("Halyard");
        h.Key(PhysicalKey.V);
        var (args, _) = h.Called("c2.vip.set");
        Assert.True(args.GetBool("vip"));
        h.Logged("Halyard marked VIP. Fighters, drones and ships now give threats to it priority.");
        Assert.True(h.Unit("Halyard").Vip);
        h.Pump(2);
        Assert.True(h.Track("Halyard").Vip);
        h.Key(PhysicalKey.V);
        Assert.False(h.Called("c2.vip.set").Args.GetBool("vip", true));
        h.Logged("Halyard: VIP protection removed.");
        Assert.False(h.Unit("Halyard").Vip);
    }

    [AvaloniaFact]
    public void Control_A_AttackNearestForTheSelection()
    {
        using var h = Demo();
        h.Run(300); // hostiles on the picture (with none, the hunt ends: "nothing left to hunt")
        h.Select("Hawk 1", "Watch 1");
        h.Key(PhysicalKey.A);
        var (args, _) = h.Issued("attack_nearest");
        Assert.Equal(2, args.GetArr("units")!.Count);
        string line = h.Logged("Selection: attack nearest.");
        Assert.Contains("Not armed for this: Watch 1.", line);
        h.Run(10);
        Assert.StartsWith("Your order · attack nearest", h.Track("Hawk 1").ControlText);
        Assert.True(h.HasLog("Hawk 1: hunting "), "Hawk 1 picked no target");
        Assert.DoesNotContain("Your order", h.Track("Watch 1").ControlText);
    }

    [AvaloniaFact]
    public void Control_ShiftA_AllAttackAfterTwoPresses()
    {
        using var h = Demo();
        h.Key(PhysicalKey.A, RawInputModifiers.Shift);
        Assert.StartsWith("Click All attack again (or Shift+A) within 3 s", h.ToastText());
        Assert.DoesNotContain(h.Cmd().History, r => r.Method == "c2.order.issue");
        h.Key(PhysicalKey.A, RawInputModifiers.Shift);
        h.Issued("all_attack");
        h.Logged("All Blue forces: attack nearest.");
        h.Run(30);
        Assert.True(h.App.Data.Tracks.Count(t => t.ControlText.StartsWith("Your order · attack nearest", StringComparison.Ordinal)) > 20);
    }

    [AvaloniaFact]
    public void Control_R_RtbForTheSelectionRefusesCarriers()
    {
        using var h = Demo();
        h.Select("Hawk 2", "Kestrel");
        h.Key(PhysicalKey.R);
        var (_, reply) = h.Issued("rtb");
        Assert.Equal(1, reply.GetInt("accepted"));
        Assert.Matches(@"^Hawk 2: RTB to Kestrel\. 1 aircraft to the marshal stack 8 km astern, then land one every 5 s\.$", h.Logged("Hawk 2: RTB"));
        Assert.Equal("RTB refused: Kestrel is a carrier (a base).", h.Logged("RTB refused"));
        for (int i = 0; i < 20 && !h.HasLog("Hawk 2 landed on Kestrel"); i++) h.Run(30);
        Assert.True(h.HasLog("Hawk 2 landed on Kestrel"), "Hawk 2 did not land");
        Assert.Contains(h.Unit("Hawk 2"), h.Sim().DeckOf(h.Unit("Kestrel")));
    }

    [AvaloniaFact]
    public void Control_ShiftR_RtbAllAfterTwoPresses()
    {
        using var h = Demo();
        h.Key(PhysicalKey.R, RawInputModifiers.Shift);
        Assert.StartsWith("Press Shift+R again (or click RTB all again) within 3 s", h.ToastText());
        Assert.DoesNotContain(h.Cmd().History, r => r.Method == "c2.order.issue");
        h.Key(PhysicalKey.R, RawInputModifiers.Shift);
        var (args, reply) = h.Issued("rtb");
        int n = args.GetArr("units")!.Count;
        Assert.Equal($"All aircraft ({n})", args.GetString("label"));
        Assert.True(reply.GetInt("accepted") > 50);
        h.Run(30);
        Assert.True(h.App.Data.Tracks.Count(t => t.ControlText.StartsWith("Your order · RTB", StringComparison.Ordinal)) > 50);
    }

    [AvaloniaFact]
    public void Control_T_TogglesRacetrackMode()
    {
        using var h = Demo();
        h.Select("Viper 1");
        h.Key(PhysicalKey.T);
        Assert.Equal(PendingMode.Racetrack, h.Cmd().Pending);
        Assert.Equal("Racetrack: drag on the plot along the long leg. A plain click makes a 20 km leg pointing east. T again cancels.", h.ToastText());
        Assert.True(Toggle(h.UnitPanel(), "Racetrack…").IsChecked);
        h.Key(PhysicalKey.T);
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        Assert.False(Toggle(h.UnitPanel(), "Racetrack…").IsChecked);
    }

    [AvaloniaFact]
    public void Control_Brackets_ShrinkAndEnlargeTheSelectedSymbols()
    {
        using var h = Demo();
        h.Select("Kestrel");
        var map = h.Map().ViewModel;
        float before = map.Sync.Find(1)!.SizeMultiplier;
        h.Key(PhysicalKey.BracketRight);
        h.Pump(2);
        Assert.Equal(1.25f, h.App.SizeMul[1], 3);
        Assert.True(map.Sync.Find(1)!.SizeMultiplier > before * 1.2f);
        h.Key(PhysicalKey.BracketLeft);
        h.Pump(2);
        Assert.Equal(1f, h.App.SizeMul.GetValueOrDefault(1, 1f), 3);
        Assert.Equal(before, map.Sync.Find(1)!.SizeMultiplier, 3);
    }

    [AvaloniaFact]
    public void Control_AltDigit_PutsTheSelectionOnANumberKey()
    {
        using var h = Demo();
        h.Select("Hawk 1", "Hawk 2");
        h.Key(PhysicalKey.Digit3, RawInputModifiers.Alt);
        Assert.Equal("Group 3", h.App.Slots["3"]);
        Assert.Equal("\"Group 3\" is on key 3. Press 3 to select it.", h.ToastText());
        h.Logged("New group \"Group 3\": 2 units (Hawk 1, Hawk 2).");
        Assert.Equal("Group 3", h.App.Data.GroupOf(h.Track("Hawk 1")));
        h.Pump(2);
        var chip = Chip(h.Find<Panels.TracksView>()!, "Group 3");
        Assert.Contains(((Avalonia.Controls.StackPanel)chip.Content!).Children.OfType<Avalonia.Controls.TextBlock>(), t => t.Text == "3"); // the key badge
    }

    [AvaloniaFact]
    public void Control_Digit_RecallsTheGroupOnThatKey()
    {
        using var h = Demo();
        h.Select("Hawk 1", "Hawk 2");
        h.Key(PhysicalKey.Digit3, RawInputModifiers.Alt);
        h.App.ClearSelection();
        h.Key(PhysicalKey.Digit3);
        Assert.Equal(new[] { h.Track("Hawk 1").Id, h.Track("Hawk 2").Id }.OrderBy(i => i), h.App.Selection.OrderBy(i => i));
        Assert.Equal("2 units selected.", h.ToastText());
    }
}
