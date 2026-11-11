using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Skizzy.Map.App.Services;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>Inventory rows 39–51: plot gestures and the orders they issue (docs/M3-command-parity.md).</summary>
public class GestureControlTests
{
    // ------------------------------------------------------------ selection and view

    [AvaloniaFact]
    public void Control_LeftClick_SelectsATrackAndEmptyPlotClears()
    {
        using var h = Demo();
        var k = h.Track("Kestrel");
        var p = h.Focus(k, 10);
        h.LeftClick(p.X, p.Y);
        Assert.Equal(k.Id, h.App.Primary);
        Assert.Single(h.App.Selection);
        var e = h.Empty(p.X - 6, p.Y + 6);
        h.LeftClick(e.X, e.Y);
        Assert.Empty(h.App.Selection);
    }

    [AvaloniaFact]
    public void Control_ShiftClick_TogglesTheTrackInTheSelection()
    {
        using var h = Demo();
        var k = h.Track("Kestrel");
        var p = h.Focus(k, 10);
        h.LeftClick(p.X, p.Y);
        var hal = h.Track("Halyard");
        var q = h.Pos(hal);
        h.LeftClick(q.X, q.Y, RawInputModifiers.Shift);
        Assert.Equal(new[] { k.Id, hal.Id }.OrderBy(i => i), h.App.Selection.OrderBy(i => i));
        Thread.Sleep(700); // a second click right away would be a double-click (the whole group)
        h.LeftClick(q.X, q.Y, RawInputModifiers.Shift);
        Assert.Equal(new[] { k.Id }, h.App.Selection);
    }

    [AvaloniaFact]
    public void Control_ShiftDragBox_BoxSelectsPreferringOwnUnits()
    {
        using var h = Demo();
        var k = h.Track("Kestrel");
        var p = h.Focus(k, 6);
        h.Drag(MouseButton.Left, [(p.X - 30, p.Y + 30), (p.X, p.Y), (p.X + 30, p.Y - 30)], mods: RawInputModifiers.Shift);
        Assert.Contains(k.Id, h.App.Selection);
        Assert.True(h.App.Selection.Count >= 3, $"{h.App.Selection.Count} selected");
        Assert.All(h.App.Selection, id => Assert.Equal(Side.Blue, h.App.Data.Get(id)!.Side));
    }

    [AvaloniaFact]
    public void Control_Drag_PansThePlot()
    {
        using var h = Demo();
        h.View(-150, 150, 4);
        var m = h.Map().ViewModel;
        var a = h.Empty(-150, 150);
        h.Drag(MouseButton.Left, [a, (a.X + 10, a.Y), (a.X + 20, a.Y)]); // 80 px right at 4 px/km
        h.Pump(30);
        Assert.InRange(m.CenterX, -170.5f, -169.5f);
        Assert.InRange(m.CenterY, 149.5f, 150.5f);
        Assert.Empty(h.App.Selection); // a drag is not a click
    }

    [AvaloniaFact]
    public void Control_Wheel_ZoomsAtTheCursor()
    {
        using var h = Demo();
        h.View(0, 0, 4);
        var m = h.Map().ViewModel;
        var p = h.At(-60, 40);
        h.Win.MouseWheel(p, new Vector(0, 2));
        h.Pump(60);
        Assert.True(m.Scale > 4.5f, $"scale {m.Scale}");
        var plot = h.Win.TranslatePoint(p, h.Map().Plot)!.Value;
        var (wx, wy) = m.ToWorld((float)plot.X, (float)plot.Y);
        Assert.InRange(wx, -60.5f, -59.5f); // the point under the cursor stays put
        Assert.InRange(wy, 39.5f, 40.5f);
    }

    [AvaloniaFact]
    public void Control_DoubleClick_SelectsTheWholeGroup()
    {
        using var h = Demo();
        var hawk = h.Track("Hawk 1");
        var p = h.Focus(hawk, 20);
        h.ClickAt(h.At(p.X, p.Y), clicks: 2);
        var escort = h.App.Data.Tracks.Where(t => t.Alive && t.Side == Side.Blue && h.App.Data.GroupOf(t) == "Escort").Select(t => t.Id).OrderBy(i => i);
        Assert.Equal(escort, h.App.Selection.OrderBy(i => i));
        Assert.Equal("5 units selected.", h.ToastText());
    }

    // ------------------------------------------------------------ orders

    [AvaloniaFact]
    public void Control_RightClickEmpty_MovesTheSelectionInFormation()
    {
        using var h = Demo();
        h.App.ClearSelection();
        h.View(-20, 10, 4);
        var e = h.Empty(-60, 10);
        h.RightClick(e.X, e.Y);
        Assert.Equal("Select one or more of your own units first, then right-click where they should go.", h.ToastText());
        Assert.Empty(h.Cmd().History);

        h.Select("Hawk 1", "Hawk 2");
        h.RightClick(e.X, e.Y);
        var (args, reply) = h.Issued("move_to");
        Assert.Equal(2, args.GetArr("units")!.Count);
        Assert.Equal(2, reply.GetInt("accepted"));
        Assert.Matches(@"^Hawk 1, Hawk 2: move to bearing \d{3}, \d+ km from Kestrel\.$", h.Logged("Hawk 1, Hawk 2: move"));
        h.Run(400);
        var (h1, h2) = (h.Unit("Hawk 1"), h.Unit("Hawk 2"));
        Assert.True(Dist(h1, e.X, e.Y) < 8 && Dist(h2, e.X, e.Y) < 8, $"Hawk 1 {Dist(h1, e.X, e.Y):0.0} km, Hawk 2 {Dist(h2, e.X, e.Y):0.0} km from the point");
        Assert.True(Dist(h1, h2) > .3, "the two aircraft did not spread into formation");
        Assert.StartsWith("Your order · move", h.Track("Hawk 1").ControlText);
    }

    [AvaloniaFact]
    public void Control_RightClickHostile_AttacksItAndTheRestOfItsGroup()
    {
        using var h = Demo();
        h.Run(300);
        var target = h.Hostile(t => t.Kind == TrackKind.Fighter || t.Kind == TrackKind.Strike);
        h.Select("Viper 3", "Viper 4");
        var p = h.Focus(target, 12);
        h.RightClick(p.X, p.Y);
        var (args, reply) = h.Issued("engage");
        Assert.Equal("v:" + target.Id, args.GetString("target"));
        Assert.Equal(2, reply.GetInt("accepted"));
        string line = h.Logged("Viper 3, Viper 4: attack " + target.Name);
        Assert.Matches(@"^Viper 3, Viper 4: attack \S+( and the rest of its group \(\d+ aircraft\))?\.$", line);
        h.Run(5);
        Assert.Equal("Your order · attack", h.Track("Viper 3").ControlText);
        Assert.True(h.Unit("Viper 3").Bda!.Override != 0, "Viper 3 is not attacking");
        var orders = h.App.Data.Orders.Orders.Where(o => o.TrackId == h.Track("Viper 3").Id || o.TrackId == h.Track("Viper 4").Id).ToList();
        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.Equal(target.Group, h.App.Data.Get(o.TargetTrackId)?.Group)); // spread over the target's group
    }

    [AvaloniaFact]
    public void Control_RightClickOwnUnit_ProtectsItAndMarksItVip()
    {
        using var h = Demo();
        var thimble = h.Track("Thimble");
        Assert.False(thimble.Vip);
        h.Select("Hawk 1", "Hawk 2");
        var p = h.Focus(thimble, 12);
        h.RightClick(p.X, p.Y);
        var (args, _) = h.Issued("defend_unit");
        Assert.Equal("v:" + thimble.Id, args.GetString("protect"));
        Assert.Equal("Hawk 1, Hawk 2: protect Thimble (now marked VIP). They stay with it and engage anything that comes within 40 km.", h.Logged("Hawk 1, Hawk 2: protect"));
        Assert.True(h.Unit("Thimble").Vip);
        Assert.Contains(h.App.Data.Orders.Orders, o => o.TrackId == h.Track("Hawk 1").Id && o.TargetTrackId == thimble.Id);
        h.Run(400);
        Assert.True(Dist(h.Unit("Hawk 1"), h.Unit("Thimble")) < 40, $"Hawk 1 is {Dist(h.Unit("Hawk 1"), h.Unit("Thimble")):0} km from Thimble");
    }

    [AvaloniaFact]
    public void Control_RightDrag_DrawsARouteTheSelectionFollows()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.View(-10, 0, 4);
        var pts = Line(h.Empty(20, -30), (-40, -30), 2).Concat(Line((-40, -30), h.Empty(-40, 20), 2).Skip(1)).ToList();
        var preview = h.Map().Preview;
        h.Drag(MouseButton.Right, pts, during: () =>
        {
            Assert.True(preview.IsVisible, "no route preview while drawing");
            Assert.True(preview.Points.Count > 5);
        });
        Assert.False(preview.IsVisible);
        var (args, _) = h.Issued("move_route");
        int n = args.GetArr("route")!.Count;
        Assert.InRange(n, 10, 40); // a point every 14 px of the 110 km (440 px) drag
        Assert.Matches($@"^Hawk 1: follow drawn route, {n} points, ending bearing \d{{3}}, \d+ km\.$", h.Logged("Hawk 1: follow drawn route"));
        Assert.Single(h.App.Data.Orders.Routes);
        h.Run(700);
        Assert.True(Dist(h.Unit("Hawk 1"), -40, 20) < 8, $"Hawk 1 is {Dist(h.Unit("Hawk 1"), -40, 20):0.0} km from the route end");
    }

    [AvaloniaFact]
    public void Control_RacetrackDrag_SetsTheHoldingPatternAndRefusesShips()
    {
        using var h = Demo();
        h.Select("Viper 1", "Halyard");
        h.View(40, 20, 4);
        h.Key(PhysicalKey.T);
        Assert.Equal(PendingMode.Racetrack, h.Cmd().Pending);
        var a = h.Empty(30, 40);
        var preview = h.Map().Preview;
        h.Drag(MouseButton.Left, Line(a, (60, 40), 3), during: () => Assert.True(preview.IsVisible && preview.Points.Count > 10, "no racetrack preview"));
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        var (args, reply) = h.Issued("hold_pattern");
        Assert.Equal(6, args.GetDouble("widthKm"));
        Assert.Equal(1, reply.GetInt("accepted"));
        h.Logged("Racetrack refused for Halyard: ships cannot fly a holding pattern. Use Send to… or a drawn route.");
        Assert.Matches(@"^Viper 1: racetrack holding pattern, 30 km legs, 6 km wide, 090°, \d+ km from Kestrel\. Stacked at \d+ m\.$", h.Logged("Viper 1: racetrack"));
        h.Run(120);
        Assert.Single(h.App.Data.Orders.Racetracks);
        Assert.Equal("Your order · racetrack", h.Track("Viper 1").ControlText);
        Assert.True(h.Unit("Viper 1").Bda!.OnRoute);

        // a plain click makes a 20 km leg pointing east
        h.Select("Viper 2");
        h.Key(PhysicalKey.T);
        var c = h.Empty(10, 60);
        h.LeftClick(c.X, c.Y);
        Assert.Matches(@"^Viper 2: racetrack holding pattern, 20 km legs, 6 km wide, 090°", h.Logged("Viper 2: racetrack"));
    }

    [AvaloniaFact]
    public void Control_SendTo_ThenClickMovesOrAttacks()
    {
        using var h = Demo();
        h.Select("Hawk 3");
        h.Click(Toggle(h.UnitPanel(), "Send to…"));
        Assert.Equal(PendingMode.SendTo, h.Cmd().Pending);
        h.View(-10, 0, 4);
        var e = h.Empty(-50, -20);
        h.LeftClick(e.X, e.Y);
        Assert.Equal(PendingMode.None, h.Cmd().Pending);
        Assert.Contains(h.Track("Hawk 3").Id, h.App.Selection); // the click ordered; it did not select
        h.Issued("move_to");
        h.Logged("Hawk 3: move to bearing");
        h.Run(300);
        Assert.True(Dist(h.Unit("Hawk 3"), e.X, e.Y) < 8, $"Hawk 3 is {Dist(h.Unit("Hawk 3"), e.X, e.Y):0.0} km from the point");

        h.Run(60);
        var target = h.Hostile(t => t.Kind == TrackKind.Fighter || t.Kind == TrackKind.Strike);
        h.Select("Hawk 3");
        h.Click(Toggle(h.UnitPanel(), "Send to…"));
        var p = h.Focus(target, 12);
        h.LeftClick(p.X, p.Y);
        var (args, _) = h.Issued("engage");
        Assert.Equal("v:" + target.Id, args.GetString("target"));
        h.Logged("Hawk 3: attack " + target.Name);
    }

    [AvaloniaFact]
    public void Control_OrderDrawing_FeedsThePlot()
    {
        using var h = Demo();
        h.View(0, 0, 3);
        h.Select("Hawk 1");
        var e = h.Empty(-60, 10);
        h.RightClick(e.X, e.Y);
        h.Select("Hawk 4");
        h.Drag(MouseButton.Right, Line(h.Empty(-50, -80), (-90, -80), 2));
        h.Select("Viper 1");
        h.Key(PhysicalKey.T);
        h.Drag(MouseButton.Left, Line(h.Empty(-100, 60), (-70, 60), 3));
        h.Select("Cobra 1");
        h.Key(PhysicalKey.R);
        h.Pump(3);
        var scene = h.Map().ViewModel.Surface.Scene;
        var move = scene.Orders.First(o => o.TrackId == h.Track("Hawk 1").Id);
        Assert.Equal(OrderType.Move, move.Type);
        Assert.InRange(move.Destination.X, (float)e.X - 3, (float)e.X + 3);
        Assert.Contains(scene.Orders, o => o.TrackId == h.Track("Hawk 4").Id && o.Type == OrderType.Route);
        Assert.Single(scene.Routes);
        Assert.Single(scene.Racetracks);
        Assert.Contains(scene.Orders, o => o.TrackId == h.Track("Cobra 1").Id && o.Type == OrderType.Rtb && h.App.Data.Get(o.TargetTrackId)?.Kind == TrackKind.Carrier); // to the marshal stack of a carrier

        // and the renderer draws them: the plot changes when the orders layer goes off
        h.Hold();
        var on = h.SettledPlot();
        h.Map().ViewModel.Options.Orders = false;
        h.Map().ViewModel.NotifyChanged();
        Assert.True(Diff(on, h.SettledPlot()) > 150, "the orders are not drawn");
    }
}
