using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Skizzy.Map.App.Map;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Shell;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;
using SkzPalette = Skizzy.Map.App.Theme.Palette;

namespace Skizzy.Map.App.Tests;

public class ShellTests
{
    [AvaloniaFact]
    public void MainWindowShowsEveryDefaultPanelAndDrawsThePlot()
    {
        using var h = new UiHarness();
        h.Pump(30);
        Assert.NotNull(h.Find<TracksView>());
        Assert.NotNull(h.Find<UnitView>());
        Assert.NotNull(h.Find<MessagesView>());
        var map = h.Find<MapView>();
        Assert.NotNull(map);
        Assert.NotNull(h.Find<TopBar>());
        h.Save("shell-dark.png");
        Assert.False(map!.ViewModel.Surface.NoSkia);
        Assert.True(map.ViewModel.Surface.FramesDrawn > 0);
        Assert.True(map.Plot.Bounds.Width > 600 && map.Plot.Bounds.Height > 400, $"plot is {map.Plot.Bounds}");
        Assert.True(map.ViewModel.Sync.Visible > 100, $"{map.ViewModel.Sync.Visible} tracks drawn");
        Assert.True(h.Find<TracksView>()!.Table.Rows.Count > 30);
    }

    [AvaloniaFact]
    public void BundledFontsResolveByFamilyName()
    {
        foreach (var (key, family) in new[] { ("FontDisplay", "Saira SemiCondensed"), ("FontUi", "IBM Plex Sans Condensed"), ("FontData", "IBM Plex Mono") })
        {
            Assert.True(Avalonia.Application.Current!.TryFindResource(key, out var v), key);
            var ff = Assert.IsType<FontFamily>(v);
            foreach (var weight in new[] { FontWeight.Medium, FontWeight.SemiBold })
            {
                Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(ff, FontStyle.Normal, weight), out var g), $"{family} {weight}");
                Assert.StartsWith(family, g!.FamilyName, StringComparison.Ordinal); // files name the weight too ("Saira SemiCondensed Medium")
            }
        }
    }

    [AvaloniaFact]
    public void NamedLayoutRoundTripsThroughDisk()
    {
        using var h = new UiHarness();
        var layouts = h.Win.Layouts;
        var map = h.App.ActiveMap!;
        map.View = ViewSide.God;
        map.Options.Labels = false;
        h.Win.SaveLayoutAs("Watch floor");
        Assert.Contains("Watch floor", h.App.Settings.ListLayouts());
        Assert.True(File.Exists(Path.Combine(h.Home, "layouts", "Watch floor.json")));

        // change everything
        layouts.SetVisible(ShellDockFactory.Tracks, false);
        layouts.SetVisible(ShellDockFactory.Health, true);
        layouts.AddMap();
        map.View = ViewSide.Blue;
        map.Options.Labels = true;
        h.Pump(3);
        Assert.False(layouts.IsVisible(ShellDockFactory.Tracks));
        Assert.Equal(2, h.App.Maps.Count);

        h.Win.LoadLayout("Watch floor");
        h.Pump(3);
        Assert.True(layouts.IsVisible(ShellDockFactory.Tracks));
        Assert.Single(h.App.Maps);
        var restored = h.App.Maps[0];
        Assert.Equal(ViewSide.God, restored.View);
        Assert.False(restored.Options.Labels);
        Assert.NotNull(h.Find<TracksView>());
        Assert.NotNull(h.Find<MapView>());

        Assert.True(h.App.Settings.DeleteLayout("Watch floor"));
        Assert.DoesNotContain("Watch floor", h.App.Settings.ListLayouts());
    }

    [AvaloniaFact]
    public void SessionLayoutAndThemeComeBackOnTheNextStart()
    {
        string home = Path.Combine(Path.GetTempPath(), "skizzy-map-ui-session-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var h = new UiHarness(restore: true, home: home))
            {
                h.Win.Layouts.SetVisible(ShellDockFactory.Unit, false);
                h.Win.Layouts.AddMap();
                h.Win.ApplyTheme(ThemeVariant.Light);
                h.Pump(2);
            } // Close saves the session
            Assert.True(File.Exists(Path.Combine(home, SettingsStore.SessionLayoutName + ".json")));
            using (var h2 = new UiHarness(restore: true, home: home))
            {
                h2.Pump(2);
                Assert.False(h2.Win.Layouts.IsVisible(ShellDockFactory.Unit));
                Assert.True(h2.Win.Layouts.IsVisible(ShellDockFactory.Tracks));
                Assert.Equal(2, h2.App.Maps.Count);
                Assert.Equal(ThemeVariant.Light, h2.App.Theme);
            }
        }
        finally
        {
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void CorruptSessionFallsBackToTheDefaultLayout()
    {
        string home = Path.Combine(Path.GetTempPath(), "skizzy-map-ui-bad-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, SettingsStore.SessionLayoutName + ".json"), "{ \"dock\": \"{not json\", \"maps\": 7 ");
        try
        {
            using var h = new UiHarness(restore: true, home: home);
            Assert.True(h.Win.Layouts.IsVisible(ShellDockFactory.Tracks));
            Assert.NotNull(h.Find<MapView>());
        }
        finally
        {
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void FloatingPanelComesBackInItsOwnWindow()
    {
        using var h = new UiHarness();
        var layouts = h.Win.Layouts;
        var tool = ShellDockFactory.FindById(layouts.Root!, ShellDockFactory.Health)!;
        layouts.Factory.FloatDockable(tool);
        h.Pump(2);
        Assert.Single(layouts.Root!.Windows!);
        var saved = layouts.Capture("floating");
        Assert.Single(saved.Panels!.Windows);
        Assert.Contains(ShellDockFactory.Health, saved.Panels.Windows[0].Items);

        h.Win.ResetLayout();
        h.Pump(2);
        Assert.Empty(layouts.Root!.Windows ?? []);
        Assert.True(layouts.Apply(saved));
        h.Pump(2);
        var wins = layouts.Root!.Windows!;
        Assert.Single(wins);
        Assert.Contains(ShellDockFactory.Walk(wins[0].Layout), d => d.Id == ShellDockFactory.Health);
        Assert.True(layouts.IsVisible(ShellDockFactory.Health));
        Assert.Single(h.App.Maps); // replacing the layout kept the map window
    }

    [AvaloniaFact]
    public void PanelsToggleFromTheTopBar()
    {
        using var h = new UiHarness();
        h.Win.Layouts.SetVisible(ShellDockFactory.Messages, false);
        h.Pump(2);
        Assert.Null(h.Find<MessagesView>());
        h.Win.Layouts.SetVisible(ShellDockFactory.Messages, true);
        h.Pump(2);
        Assert.NotNull(h.Find<MessagesView>());
    }
}

public class SelectionTests
{
    [AvaloniaFact]
    public void ClickSelectionShowsTheUnitAndEscClears()
    {
        using var h = new UiHarness();
        h.App.SelectOne(1);
        h.Pump(30);
        var unit = h.Find<UnitView>()!;
        Assert.Contains(unit.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Kestrel");
        var map = h.App.ActiveMap!;
        Assert.True(map.Sync.Find(1)!.Selected);

        h.Win.Focus();
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        h.Pump(2);
        Assert.Empty(h.App.Selection);
        Assert.False(map.Sync.Find(1)!.Selected);
    }

    private static Avalonia.Point OnWindow(UiHarness h, MapView view, double x, double y)
    {
        var (sx, sy) = view.ViewModel.ToScreen((float)x, (float)y);
        return view.Plot.TranslatePoint(new Avalonia.Point(sx, sy), h.Win)!.Value;
    }

    [AvaloniaFact]
    public void ClickOnTheMapSelectsAndShiftDragBoxSelects()
    {
        using var h = new UiHarness();
        var view = h.Find<MapView>()!;
        var map = view.ViewModel;
        var kestrel = h.App.Data.Get(1)!;
        var (kx, ky) = h.App.Data.PositionOf(kestrel, h.App.Data.ViewTime);
        map.SetView((float)kx, (float)ky, 6f, animate: false);
        h.Pump(4);

        var p = OnWindow(h, view, kx, ky);
        h.Win.MouseDown(p, Avalonia.Input.MouseButton.Left);
        h.Win.MouseUp(p, Avalonia.Input.MouseButton.Left);
        h.Pump(2);
        Assert.Equal(1, h.App.Primary);
        Assert.Single(h.App.Selection);

        h.App.ClearSelection();
        var a = OnWindow(h, view, kx - 30, ky + 30);
        var b = OnWindow(h, view, kx + 30, ky - 30);
        h.Win.MouseDown(a, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Shift);
        h.Win.MouseMove(new Avalonia.Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), Avalonia.Input.RawInputModifiers.Shift | Avalonia.Input.RawInputModifiers.LeftMouseButton);
        h.Win.MouseMove(b, Avalonia.Input.RawInputModifiers.Shift | Avalonia.Input.RawInputModifiers.LeftMouseButton);
        h.Win.MouseUp(b, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Shift);
        h.Pump(2);
        Assert.Contains(1, h.App.Selection);
        Assert.True(h.App.Selection.Count >= 3, $"{h.App.Selection.Count} selected");
        Assert.All(h.App.Selection, id => Assert.Equal(Side.Blue, h.App.Data.Get(id)!.Side)); // own units preferred
    }

    [AvaloniaFact]
    public void DragPansAndWheelZooms()
    {
        using var h = new UiHarness();
        var view = h.Find<MapView>()!;
        var map = view.ViewModel;
        map.SetView(0, 0, 4f, animate: false);
        h.Pump(2);
        var c = view.Plot.TranslatePoint(new Avalonia.Point(map.Width / 2, map.Height / 2), h.Win)!.Value;
        h.Win.MouseDown(c, Avalonia.Input.MouseButton.Left);
        h.Win.MouseMove(c + new Avalonia.Point(40, 0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        h.Win.MouseMove(c + new Avalonia.Point(80, 0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        h.Win.MouseUp(c + new Avalonia.Point(80, 0), Avalonia.Input.MouseButton.Left);
        h.Pump(30);
        Assert.Equal(-20, map.CenterX, 1); // 80 px right at 4 px/km moves the view 20 km west
        Assert.Empty(h.App.Selection); // a drag is not a click
        h.Win.MouseWheel(c, new Avalonia.Vector(0, 2));
        h.Pump(40);
        Assert.True(map.Scale > 4.5f, $"scale {map.Scale}");
    }

    [AvaloniaFact]
    public void NumberKeyGroupsStoreAndRecallASelection()
    {
        using var h = new UiHarness();
        var fighters = h.App.Data.Tracks.Where(t => t.Side == Side.Blue && t.Kind == TrackKind.Fighter && t.Alive).Take(4).Select(t => t.Id).ToList();
        Assert.Equal(4, fighters.Count);
        h.App.Select(fighters, fighters[0]);
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.Digit3, Avalonia.Input.RawInputModifiers.Alt);
        h.App.ClearSelection();
        Assert.Empty(h.App.Selection);
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.Digit3, Avalonia.Input.RawInputModifiers.None);
        Assert.Equal(fighters.OrderBy(i => i), h.App.Selection.OrderBy(i => i));
        h.Pump(2);
        Assert.Contains(h.Find<TracksView>()!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "3");
    }

    [AvaloniaFact]
    public void SelectedSymbolsGrowAndBracketsResize()
    {
        using var h = new UiHarness();
        h.App.SelectOne(1);
        h.Pump(2);
        var map = h.App.ActiveMap!;
        float before = map.Sync.Find(1)!.SizeMultiplier;
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.BracketRight, Avalonia.Input.RawInputModifiers.None);
        h.Pump(2);
        Assert.True(map.Sync.Find(1)!.SizeMultiplier > before * 1.2f);
    }
}

public class FilterTests
{
    [AvaloniaFact]
    public void TrackListFilterKeepsOnlyMatchingRows()
    {
        using var h = new UiHarness();
        h.FastForward(700); // missiles in the air
        var table = h.Find<TracksView>()!.Table;
        h.App.SetTrackFilter(TrackFilter.Ships);
        h.Pump(2);
        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows, r => Assert.NotEqual(TrackDomain.Air, h.App.Data.Get(r.Id)!.Domain)); // ships and submarines
        h.App.SetTrackFilter(TrackFilter.Missiles);
        h.Pump(2);
        Assert.All(table.Rows, r => Assert.True(h.App.Data.Get(r.Id)!.IsMissile));
        h.App.SetTrackFilter(TrackFilter.All);
    }

    [AvaloniaFact]
    public void TrackListSortsByAnyColumnBothWays()
    {
        using var h = new UiHarness();
        var table = h.Find<TracksView>()!.Table;
        h.Pump(2);
        // default: range, nearest first
        Assert.Equal(TrackColumn.Rng, table.Sort);
        Assert.True(table.Rows.Zip(table.Rows.Skip(1)).All(p => p.First.Rng <= p.Second.Rng + 1e-9));
        table.SortBy(TrackColumn.Spd);
        h.Pump(1);
        var spd = table.Rows.Select(r => r.Spd).ToList();
        var sorted = table.Descending ? spd.OrderByDescending(v => v).ToList() : spd.OrderBy(v => v).ToList();
        Assert.Equal(sorted, spd);
        bool desc = table.Descending;
        table.SortBy(TrackColumn.Spd); // same column again flips the direction
        h.Pump(1);
        Assert.NotEqual(desc, table.Descending);
        table.SortBy(TrackColumn.Track);
        h.Pump(1);
        var names = table.Rows.Select(r => r.Name).ToList();
        Assert.Equal(table.Descending ? names.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).ToList() : names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
    }

    [AvaloniaFact]
    public void PlotFilterHidesTracksButKeepsThem()
    {
        using var h = new UiHarness();
        var map = h.App.ActiveMap!;
        h.Pump(2);
        int all = map.Sync.Visible;
        map.Filter = PlotFilter.Ships;
        h.Pump(2);
        Assert.True(map.Sync.FilteredOut > 0);
        Assert.True(map.Sync.Visible < all);
        Assert.All(map.Surface.Scene.Tracks.Where(t => t.Visible), t => Assert.NotEqual(TrackDomain.Air, t.Domain));
        map.Filter = PlotFilter.All;
        h.Pump(2);
        Assert.Equal(0, map.Sync.FilteredOut);
    }

    [AvaloniaFact]
    public void FogAndGodViewsDifferOnStealth()
    {
        using var h = new UiHarness();
        var map = h.App.ActiveMap!;
        h.Pump(2);
        int blue = map.Surface.Scene.Tracks.Count(t => t.Detection != TrackDetection.None && t.Side == Side.Red);
        map.View = ViewSide.God;
        h.Pump(2);
        int god = map.Surface.Scene.Tracks.Count(t => t.Detection != TrackDetection.None && t.Side == Side.Red);
        Assert.True(god > blue, $"god {god} vs blue fog {blue}");
    }
}

public class TimeTests
{
    [AvaloniaFact]
    public void PauseBannerShowsAndResumeHidesIt()
    {
        using var h = new UiHarness();
        var view = h.Find<MapView>()!;
        Assert.False(view.Banner.IsVisible);
        h.App.Source.SetPaused(true);
        h.App.Data.PauseBanner = "First weapons launch: Red fired on Task Group 1.";
        h.Pump(2);
        Assert.True(view.Banner.IsVisible);
        h.Save("banner.png");

        var resume = view.Banner.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Resume");
        resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        h.Pump(2);
        Assert.False(view.Banner.IsVisible);
        Assert.False(h.App.Source.Paused);
        Assert.Null(h.App.Data.PauseBanner);
    }

    [AvaloniaFact]
    public void RealFirstLaunchPausesAndDontPauseAgainKeepsRunning()
    {
        using var h = new UiHarness();
        var d = h.Demo!;
        d.SetSpeed(64);
        for (int i = 0; i < 120 && !d.Paused; i++) h.App.Frame(0.3);
        Assert.True(d.Paused, "the first launch should pause the battle");
        h.Pump(1);
        var view = h.Find<MapView>()!;
        Assert.True(view.Banner.IsVisible);
        var noPause = view.Banner.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Don't pause again");
        noPause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        h.Pump(2);
        Assert.False(view.Banner.IsVisible);
        Assert.False(d.PauseOnEvents);
        for (int i = 0; i < 40; i++) h.App.Frame(0.3);
        Assert.False(d.Paused);
    }

    [AvaloniaFact]
    public void ScrubbingEntersReplayAndBackToLiveLeavesIt()
    {
        using var h = new UiHarness();
        h.FastForward(120);
        var view = h.Find<MapView>()!;
        double live = h.App.Data.LiveTime;
        h.App.ScrubTo(live - 60);
        h.Pump(2);
        Assert.True(h.App.Data.InReplay);
        Assert.True(view.LiveButton.IsEnabled || view.LiveButton.IsVisible);
        view.LiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        h.Pump(2);
        Assert.False(h.App.Data.InReplay);
    }

    [AvaloniaFact]
    public void SpaceTogglesPlay()
    {
        using var h = new UiHarness();
        Assert.False(h.App.Paused);
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Assert.True(h.App.Paused);
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Assert.False(h.App.Paused);
    }
}

public class PaletteTests
{
    [AvaloniaFact]
    public async Task CtrlKOpensThePaletteAndReadCapabilitiesRun()
    {
        using var h = new UiHarness();
        h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.K, Avalonia.Input.RawInputModifiers.Control);
        h.Pump(1);
        var p = h.Win.Commands;
        Assert.True(p.IsVisible);
        Assert.Contains(p.Shown, i => i.IsLocal);
        Assert.Contains(p.Shown, i => i.Name == "ksp.vessels.list");

        p.Search.Text = "vessels list";
        h.Pump(1);
        var item = p.Shown.First(i => i.Name == "ksp.vessels.list");
        Assert.Equal(PaletteGate.Run, item.Gate);
        p.OpenItem(item);
        h.Pump(1);
        h.Save("palette.png");
        var run = p.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Run");
        run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await p.Running!;
        Assert.NotNull(p.LastResult);
        var json = JsonNode.Parse(p.LastResult!);
        Assert.NotNull(json);

        p.Escape(); // back to the list
        Assert.Null(p.Current);
        p.Escape(); // closed
        Assert.False(p.IsVisible);
    }

    [AvaloniaFact]
    public void SafetyGatesFollowTheM3Rules()
    {
        Assert.Equal(PaletteGate.Run, new PaletteItem("a", "x.read", "", "", "read", true, "{}", null).Gate);
        Assert.Equal(PaletteGate.Confirm, new PaletteItem("a", "x.ctl", "", "", "control", true, "{}", null).Gate);
        foreach (var s in new[] { "destructive", "cheat", "unsafe" })
            Assert.Equal(PaletteGate.Blocked, new PaletteItem("a", "x", "", "", s, true, "{}", null).Gate);
        Assert.Equal(PaletteGate.Unavailable, new PaletteItem("a", "x", "", "", "read", false, "{}", null).Gate);
        Assert.Equal(PaletteGate.Run, new PaletteItem("Fit all", "", "Map", "", "app", true, "{}", () => { }).Gate);

        using var h = new UiHarness();
        // the demo's catalog is M0's: blocked and unavailable entries are listed but cannot run
        h.Win.TogglePalette();
        var shown = h.Win.Commands.Shown;
        Assert.Contains(shown, i => i.Gate == PaletteGate.Unavailable);
        var blocked = shown.FirstOrDefault(i => i.Safety is "destructive" or "cheat" or "unsafe");
        if (blocked is not null)
        {
            h.Win.Commands.OpenItem(blocked);
            h.Pump(1);
            var run = h.Win.Commands.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Run");
            Assert.False(run.IsEnabled);
        }
    }

    [AvaloniaFact]
    public void LocalActionsRunAndClose()
    {
        using var h = new UiHarness();
        h.Win.TogglePalette();
        var p = h.Win.Commands;
        p.Search.Text = "new map";
        h.Pump(1);
        p.OpenItem(p.Shown.First(i => i.Title == "New map window"));
        h.Pump(2);
        Assert.False(p.IsVisible);
        Assert.Equal(2, h.App.Maps.Count);
    }

    [AvaloniaFact]
    public void SchemaFormBuildsTypedArguments()
    {
        const string schema = """
        {"type":"object","required":["vessel","count"],
         "properties":{
           "vessel":{"type":"string","description":"Vessel id"},
           "count":{"type":"integer"},
           "throttle":{"type":"number"},
           "sas":{"type":"boolean"},
           "mode":{"enum":["prograde","retrograde"]},
           "tags":{"type":"array"},
           "maybe":{"type":["string","null"]}}}
        """;
        var error = new TextBlock();
        var form = CommandPalette.BuildFields(schema, out var build, error);
        var window = new Window { Content = form };
        window.Show();
        var boxes = form.GetLogicalDescendants().OfType<TextBox>().ToList();
        Assert.Equal(5, boxes.Count); // vessel, count, throttle, tags, maybe

        Assert.Null(build()); // required fields empty
        Assert.True(error.IsVisible);
        Assert.Contains("vessel", error.Text);

        boxes[0].Text = "Kestrel 1";
        boxes[1].Text = "2.5";
        Assert.Null(build());
        Assert.Contains("whole number", error.Text);

        boxes[1].Text = "3";
        boxes[2].Text = "0.75";
        boxes[3].Text = "[\"a\",1]";
        form.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = true;
        form.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex = 2; // (none), prograde, retrograde
        var json = build();
        Assert.NotNull(json);
        var o = JsonNode.Parse(json!)!.AsObject();
        Assert.Equal("Kestrel 1", (string?)o["vessel"]);
        Assert.Equal(3, (long)o["count"]!);
        Assert.Equal(0.75, (double)o["throttle"]!, 6);
        Assert.True((bool)o["sas"]!);
        Assert.Equal("retrograde", (string?)o["mode"]);
        Assert.Equal(2, o["tags"]!.AsArray().Count);
        Assert.False(o.ContainsKey("maybe"));
        Assert.False(error.IsVisible);
        window.Close();
    }

    [Fact]
    public void TextFieldsParseStrictly()
    {
        Assert.False(CommandPalette.ParseText("n", "number", "1,5", false).ok);
        Assert.True(CommandPalette.ParseText("n", "number", "1.5e3", false).ok);
        Assert.False(CommandPalette.ParseText("n", "number", "NaN", false).ok);
        Assert.False(CommandPalette.ParseText("o", "object", "[1]", false).ok);
        Assert.True(CommandPalette.ParseText("o", "object", "{\"a\":1}", false).ok);
        Assert.True(CommandPalette.ParseText("x", "any", "plain words", false).ok);
        Assert.False(CommandPalette.ParseText("x", "string", "  ", true).ok);
        Assert.True(CommandPalette.ParseText("x", "string", "  ", false) is { ok: true, value: null });
        Assert.Equal("{\n  \"a\": 1\n}", CommandPalette.Pretty("{\"a\":1}").Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("not json", CommandPalette.Pretty("not json"));
    }
}

public class ThemeTests
{
    [AvaloniaFact]
    public void ThemeSwitchSwapsTheWholePalette()
    {
        using var h = new UiHarness();
        h.Win.ApplyTheme(ThemeVariant.Light);
        h.Pump(4);
        Assert.True(Avalonia.Application.Current!.TryFindResource("Ink0Color", out var c));
        Assert.Equal(SkzPalette.LightSet.Ink0, (Color)c!);
        Assert.Equal(ThemeVariant.Light, h.App.Theme);
        Assert.Equal("Light", h.App.Settings.Current.Theme);
        // the plot's renderer follows
        Assert.Equal(SkzPalette.LightSet.Sea.R, h.App.ActiveMap!.Surface.Renderer!.Theme.Sea.Red);
        h.Save("shell-light.png");
        h.Win.ApplyTheme(ThemeVariant.Dark);
        h.Pump(2);
        Assert.True(Avalonia.Application.Current!.TryFindResource("Ink0Color", out var d));
        Assert.Equal(SkzPalette.DarkSet.Ink0, (Color)d!);
    }
}
