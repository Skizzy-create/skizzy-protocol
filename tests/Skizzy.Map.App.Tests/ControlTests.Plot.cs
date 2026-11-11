using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Skizzy.Map.App.Panels;
using Skizzy.Map.Data;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>Inventory rows 17–38: the track list, the plot toolbar and the plot overlays (docs/M3-command-parity.md).</summary>
public class PlotControlTests
{
    // ------------------------------------------------------------ track list

    [AvaloniaFact]
    public void Control_GroupChips_SelectTheGroupWithAToast()
    {
        using var h = Demo();
        var tracks = h.Find<TracksView>()!;
        h.Click(Chip(tracks, "Escort"));
        var escort = h.App.Data.Tracks.Where(t => t.Alive && t.Side == Side.Blue && h.App.Data.GroupOf(t) == "Escort").Select(t => t.Id).OrderBy(i => i).ToList();
        Assert.Equal(5, escort.Count);
        Assert.Equal(escort, h.App.Selection.OrderBy(i => i));
        Assert.Equal("5 units selected.", h.ToastText());
        h.Click(Chip(h.Find<TracksView>()!, "All aircraft"));
        Assert.True(h.App.Selection.Count > 50, $"{h.App.Selection.Count} aircraft");
        Assert.All(h.App.Selection, id => Assert.Equal(TrackDomain.Air, h.App.Data.Get(id)!.Domain));
        Assert.Equal($"{h.App.Selection.Count} units selected.", h.ToastText());
    }

    [AvaloniaFact]
    public void Control_FilterChips_FilterTheListWithCounts()
    {
        using var h = Demo();
        var tracks = h.Find<TracksView>()!;
        var chip = Chip(tracks, "Ships & subs");
        h.Click(chip);
        h.Pump(2);
        Assert.Equal(TrackFilter.Ships, h.App.TrackListFilter);
        Assert.True(chip.IsChecked);
        var rows = tracks.Table.Rows;
        int ships = rows.Count;
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.NotEqual(TrackDomain.Air, h.App.Data.Get(r.Id)!.Domain));
        var count = ((StackPanel)chip.Content!).Children.OfType<TextBlock>().Last().Text;
        Assert.Equal(ships.ToString(System.Globalization.CultureInfo.InvariantCulture), count);
        h.Click(Chip(tracks, "All"));
        h.Pump(2);
        Assert.Equal(TrackFilter.All, h.App.TrackListFilter);
        Assert.True(tracks.Table.Rows.Count > ships);
    }

    private static Point RowPoint(UiHarness h, int row)
    {
        var table = h.Find<TracksView>()!.Table;
        return table.TranslatePoint(new Point(40, row * TrackTable.RowH + TrackTable.RowH / 2 - table.Offset), h.Win)!.Value;
    }

    [AvaloniaFact]
    public void Control_TrackRowClick_SelectsTheTrack()
    {
        using var h = Demo();
        var table = h.Find<TracksView>()!.Table;
        int id = table.Rows[2].Id;
        h.ClickAt(RowPoint(h, 2));
        Assert.Equal(id, h.App.Primary);
        Assert.Single(h.App.Selection);
        Assert.Equal(h.App.Data.Get(id)!.Name, h.UnitPanel().GetLogicalDescendants().OfType<TextBlock>().First(t => t.FontSize == 18).Text);
    }

    [AvaloniaFact]
    public void Control_TrackRowDoubleClick_CentresThePlotOnTheTrack()
    {
        using var h = Demo();
        h.View(200, 200, 4);
        var table = h.Find<TracksView>()!.Table;
        var t = h.App.Data.Get(table.Rows[3].Id)!;
        h.ClickAt(RowPoint(h, 3), clicks: 2);
        h.Pump(60);
        var p = h.Pos(t);
        var m = h.Map().ViewModel;
        Assert.InRange(m.CenterX, p.X - 2, p.X + 2);
        Assert.InRange(m.CenterY, p.Y - 2, p.Y + 2);
    }

    // ------------------------------------------------------------ toolbar

    [AvaloniaFact]
    public void Control_SymbolsMode_ChangesHowSymbolsAreSized()
    {
        using var h = Demo();
        h.Hold();
        var before = h.SettledPlot();
        var combo = h.Map().GetLogicalDescendants().OfType<ComboBox>().First();
        h.KeyOn(combo, PhysicalKey.ArrowDown);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal((SizeMode)1, h.Map().ViewModel.Options.SizeMode);
        Assert.True(Diff(before, h.SettledPlot()) > 150, "the plot did not change");
    }

    [AvaloniaFact]
    public void Control_SymbolScale_ScalesAllSymbols()
    {
        using var h = Demo();
        h.Hold();
        var before = h.SettledPlot();
        var slider = h.Map().GetLogicalDescendants().OfType<Slider>().First(s => ToolTip.GetTip(s) as string == "Symbol scale");
        h.KeyOn(slider, PhysicalKey.ArrowRight, 4);
        Assert.InRange(h.Map().ViewModel.Options.SizeScale, 1.15f, 1.25f);
        Assert.True(Diff(before, h.SettledPlot()) > 150, "the plot did not change");
    }

    /// <summary>A layer toggle flips its plot option and the drawn plot changes (pictures taken once the symbol sizes settle).</summary>
    private static void LayerToggle(string text, Func<PlotOptions, bool> option, double runFirst = 60)
    {
        using var h = Demo();
        h.Run(runFirst);
        h.Map().ViewModel.FitAll(animate: false);
        h.Hold();
        var a = h.SettledPlot();
        var tg = Toggle(h.Map(), text);
        bool was = option(h.Map().ViewModel.Options);
        h.Click(tg);
        Assert.Equal(!was, option(h.Map().ViewModel.Options));
        Assert.Equal(!was, tg.IsChecked);
        int changed = Diff(a, h.SettledPlot());
        Assert.True(changed > 150, $"{text}: only {changed} pixels changed");
        h.Click(tg);
        Assert.Equal(was, option(h.Map().ViewModel.Options));
        int back = Diff(a, h.SettledPlot());
        Assert.True(back < changed / 4, $"{text}: the plot did not come back ({back} of {changed} pixels still differ)");
    }

    [AvaloniaFact]
    public void Control_TrailsToggle_ShowsAndHidesTrails() => LayerToggle("Trails", o => o.Trails);

    [AvaloniaFact]
    public void Control_AltitudeToggle_ShowsAndHidesAltitudeStalks() => LayerToggle("Altitude", o => o.AltitudeStalks);

    [AvaloniaFact]
    public void Control_TargetLinesToggle_ShowsAndHidesTargetLines() => LayerToggle("Target lines", o => o.TargetLines, runFirst: 700);

    [AvaloniaFact]
    public void Control_RingsToggle_ShowsAndHidesRangeRings() => LayerToggle("Rings", o => o.Rings);

    [AvaloniaFact]
    public void Control_LabelsToggle_ShowsAndHidesLabels() => LayerToggle("Labels", o => o.Labels);

    [AvaloniaFact]
    public void Control_CountsToggle_GroupsDronesAndMissilesIntoCounts() => LayerToggle("Counts", o => o.Counts, runFirst: 700);

    [AvaloniaFact]
    public void Control_ZoomOut_ZoomsOut()
    {
        using var h = Demo();
        h.View(0, 0, 4);
        h.Click(Btn(h.Map(), "−"));
        h.Pump(60);
        Assert.InRange(h.Map().ViewModel.Scale, 4 / 1.4f - .01f, 4 / 1.4f + .01f);
    }

    [AvaloniaFact]
    public void Control_ZoomIn_ZoomsIn()
    {
        using var h = Demo();
        h.View(0, 0, 4);
        h.Click(Btn(h.Map(), "+"));
        h.Pump(60);
        Assert.InRange(h.Map().ViewModel.Scale, 4 * 1.4f - .01f, 4 * 1.4f + .01f);
    }

    [AvaloniaFact]
    public void Control_Fit_FitsAllTracks()
    {
        using var h = Demo();
        h.View(300, 300, 30);
        h.Click(Btn(h.Map(), "Fit"));
        h.Pump(60);
        var m = h.Map().ViewModel;
        int outside = 0, n = 0;
        foreach (var t in h.App.Data.Tracks)
        {
            if (!t.Alive || t.DetectionFor(ViewSide.Blue) == TrackDetection.None) continue;
            var (x, y) = h.Pos(t);
            if (Math.Abs(x) >= 400 || Math.Abs(y) >= 400) continue;
            n++;
            var (sx, sy) = m.ToScreen((float)x, (float)y);
            if (sx < 0 || sy < 0 || sx > m.Width || sy > m.Height) outside++;
        }
        Assert.True(n > 100);
        Assert.Equal(0, outside);
    }

    // ------------------------------------------------------------ banner

    private static UiHarness PausedOnEvent()
    {
        var h = new UiHarness(); // pause on events stays on
        var d = h.Demo!;
        d.SetSpeed(64);
        for (int i = 0; i < 150 && !d.Paused; i++) h.App.Frame(0.3);
        Assert.True(d.Paused, "the first launch should pause the battle");
        d.SetSpeed(1);
        h.Pump(2);
        Assert.True(h.Map().Banner.IsVisible);
        return h;
    }

    [AvaloniaFact]
    public void Control_BannerResume_ResumesTheBattle()
    {
        using var h = PausedOnEvent();
        double t = h.App.Data.LiveTime;
        h.Click(Btn(h.Map().Banner, "Resume"));
        Assert.False(h.App.Paused);
        Assert.False(h.Map().Banner.IsVisible);
        h.Pump(60);
        Assert.True(h.App.Data.LiveTime > t);
    }

    [AvaloniaFact]
    public void Control_BannerDontPauseAgain_KeepsRunningThroughEvents()
    {
        using var h = PausedOnEvent();
        h.Click(Btn(h.Map().Banner, "Don't pause again"));
        Assert.False(h.Src().PauseOnEvents);
        Assert.False(h.App.Paused);
        Assert.True(h.HasLog("Pause on events off. Events are still logged."));
        h.Src().SetSpeed(64);
        for (int i = 0; i < 40; i++) h.App.Frame(0.3);
        Assert.False(h.App.Paused);
    }

    // ------------------------------------------------------------ overlays

    [AvaloniaFact]
    public void Control_HoverTooltip_ShowsTheTrackDetails()
    {
        using var h = Demo();
        var k = h.Track("Kestrel");
        var p = h.Focus(k, 10);
        h.Pump(2);
        h.Win.MouseMove(h.At(p.X, p.Y));
        h.Pump(2);
        var tip = h.Map().Tip;
        Assert.True(tip.IsVisible);
        Assert.Equal("Kestrel", h.Map().TipTitle);
        var body = tip.GetLogicalDescendants().OfType<TextBlock>().Last().Text ?? "";
        Assert.Contains("BRG ", body);
        Assert.Contains("from Kestrel", body);
        h.Win.MouseMove(h.At(p.X + 30, p.Y + 30));
        h.Pump(2);
        Assert.False(tip.IsVisible);
    }

    [AvaloniaFact]
    public void Control_Toast_ShowsAStatusLineFor4_5Seconds()
    {
        using var h = Demo();
        h.App.ClearSelection();
        h.Key(PhysicalKey.V); // V with nothing selected answers with a toast
        Assert.Equal("Select one or more of your own units, then press V or Mark VIP.", h.ToastText());
        var sw = Stopwatch.StartNew();
        Wait(4.2); // the dispatcher runs its timers meanwhile
        Assert.True(h.Map().Toast.IsVisible, "the toast went before 4.5 s");
        while (sw.Elapsed.TotalSeconds < 6 && h.Map().Toast.IsVisible) Wait(.05);
        Assert.False(h.Map().Toast.IsVisible, "the toast stayed past 4.5 s");
        Assert.InRange(sw.Elapsed.TotalSeconds, 4.4, 5.5);
    }

    /// <summary>Runs the dispatcher (timers included) for <paramref name="seconds"/> of wall time.</summary>
    private static void Wait(double seconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    [AvaloniaFact]
    public void Control_Legend_ShowsTheSymbolKey()
    {
        using var h = Demo();
        var legend = h.Map().LegendBox;
        Assert.True(legend.IsEffectivelyVisible);
        Assert.True(legend.Bounds.Width > 60 && legend.Bounds.Height > 40, $"legend {legend.Bounds}");
        Assert.IsType<Skizzy.Map.App.Views.Legend>(legend.Child);
    }

    [AvaloniaFact]
    public void Control_ReplayScrub_EntersReplayAtThatFrame()
    {
        using var h = Demo();
        h.Run(300);
        var scrub = h.Map().Scrubber;
        var hawk = h.Track("Hawk 1");
        var live = h.Pos(hawk);
        var p = scrub.TranslatePoint(new Point(scrub.Bounds.Width * .3, scrub.Bounds.Height / 2), h.Win)!.Value;
        h.ClickAt(p);
        Assert.True(h.App.Data.InReplay);
        Assert.InRange(h.App.Data.ViewTime, scrub.Minimum + (scrub.Maximum - scrub.Minimum) * .2, scrub.Minimum + (scrub.Maximum - scrub.Minimum) * .4);
        var past = h.Pos(hawk);
        Assert.True(Math.Abs(past.X - live.X) + Math.Abs(past.Y - live.Y) > 1, "the plot still shows the live position");
        Assert.True(h.Map().LiveButton.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Control_BackToLive_LeavesReplay()
    {
        using var h = Demo();
        h.Run(120);
        h.App.ScrubTo(h.App.Data.LiveTime - 60);
        h.Pump(2);
        Assert.True(h.App.Data.InReplay);
        h.Click(h.Map().LiveButton);
        Assert.False(h.App.Data.InReplay);
        Assert.Equal(h.App.Data.LiveTime, h.App.Data.ViewTime);
    }
}
