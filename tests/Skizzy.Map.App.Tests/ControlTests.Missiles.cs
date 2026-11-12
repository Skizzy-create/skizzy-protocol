using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Dock.Model.Core;
using Skizzy.C2;
using Skizzy.C2.Fake;
using Skizzy.C2.Tactical;
using Skizzy.Host.Core;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Shell;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Live;
using Skizzy.Map.Render.Model;
using Skizzy.Sim;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>
/// M2 missile picture controls (docs/M2-map-missiles-action-report.md): the plot toolbar's Missiles and Missile trails
/// toggles, the same two in the Layers panel, the Engagements panel (tab, panel toggle, kind chips, totals, row click),
/// and the live path end to end (FakeKSP host + FakeBda: source.tactical missile rows, log and totals into the map).
/// </summary>
public class MissileControlTests
{
    // ------------------------------------------------------------ helpers

    /// <summary>
    /// A held demo with a 10-missile Red salvo (headed, 12 s trails) inbound to a Blue ship, the view centred on it at
    /// <paramref name="scale"/> px/km. Returns the ship.
    /// </summary>
    private static TrackInfo Salvo(UiHarness h, float scale = 8)
    {
        h.Run(30);
        h.Hold();
        var d = h.App.Data;
        var ship = d.Tracks.First(t => t.Alive && t.Side == Side.Blue && t.Domain == TrackDomain.Surface && t.AffBlue == Affiliation.Friend);
        var (sx, sy) = d.PositionOf(ship, d.ViewTime);
        double now = d.ViewTime;
        for (int i = 0; i < 10; i++)
        {
            var m = d.GetOrAdd("vessel:test-msl-" + i.ToString(CultureInfo.InvariantCulture), now - 12, out _);
            m.Kind = TrackKind.AntiShipMissile; m.Domain = TrackDomain.Air; m.Side = Side.Red; m.Name = "V" + i.ToString("000", CultureInfo.InvariantCulture);
            m.AffBlue = Affiliation.Hostile; m.AffRed = Affiliation.Friend; m.DetBlue = TrackDetection.Shared; m.DetRed = TrackDetection.Shared;
            m.TargetId = ship.Id; m.Rcs = KindInfo.Of(m.Kind).Rcs;
            double y = sy + (i - 5) * 0.6;
            for (int k = 12; k >= 0; k--)
            {
                // 290 m/s westbound, 8 km east of the ship now
                double x = sx + 8 + k * 0.29;
                m.SetSample(now - k, x, y, 30, 270, 290, -0.29, 0, now - k, blendSeconds: 0);
                m.Record(d.HistoryInterval);
            }
        }
        d.TouchRevision();
        h.View(sx + 6, sy, scale);
        h.Pump(4);
        return ship;
    }

    /// <summary>A toolbar or Layers toggle flips the option, changes the plot and puts it back (the LayerToggle pattern).</summary>
    private static void PlotToggle(UiHarness h, Control root, string text, Func<Skizzy.Map.Render.PlotOptions, bool> option, int minChanged)
    {
        var a = h.SettledPlot();
        var tg = Toggle(root, text);
        bool was = option(h.Map().ViewModel.Options);
        Assert.True(was, $"{text} is on by default");
        h.Click(tg);
        Assert.Equal(!was, option(h.Map().ViewModel.Options));
        Assert.Equal(!was, tg.IsChecked);
        int changed = Diff(a, h.SettledPlot());
        Assert.True(changed > minChanged, $"{text}: only {changed} pixels changed");
        h.Click(tg);
        Assert.Equal(was, option(h.Map().ViewModel.Options));
        int back = Diff(a, h.SettledPlot());
        Assert.True(back < changed / 4, $"{text}: the plot did not come back ({back} of {changed} pixels still differ)");
    }

    private static Control Tab(UiHarness h, string title) =>
        h.Win.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name.EndsWith("TabStripItem", StringComparison.Ordinal) && c.DataContext is IDockable d && string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase));

    private static EngagementsView ShowEngagements(UiHarness h)
    {
        h.Click(Tab(h, "Engagements"));
        h.Pump(3);
        var v = h.Find<EngagementsView>();
        Assert.True(v is { IsEffectivelyVisible: true }, "the Engagements panel is not showing");
        return v!;
    }

    private static IEnumerable<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    /// <summary>A small engagement: Team A fires 3 (one 2-part missile), one hits, one is intercepted by Team B, one misses.</summary>
    private static void Book(UiHarness h, string? missileKey = null)
    {
        var b = h.App.Data.Engagements;
        double t = h.App.Data.ViewTime;
        b.Add(new EngagementEntry(1, t - 9, "fired", "A", "AGM-84", missileKey, "Hornet 1", "Kirov", "B", null, null, 1250, "ledger", "Team A Hornet 1 fired AGM-84 at Team B Kirov (1,250 funds)"));
        b.Add(new EngagementEntry(2, t - 8, "fired", "A", "AGM-84", null, "Hornet 1", "Kirov", "B", null, null, 1250, "ledger", "Team A Hornet 1 fired AGM-84 at Team B Kirov (1,250 funds)"));
        b.Add(new EngagementEntry(3, t - 7, "fired", "A", "AIM-9", null, "Hornet 2", "Kirov", "B", null, null, 900, "ledger", "Team A Hornet 2 fired AIM-9 at Team B Kirov (900 funds)"));
        b.Add(new EngagementEntry(4, t - 5, "fired", "B", "SM-2", null, "Kirov", null, null, null, null, 700, "ledger", "Team B Kirov fired SM-2 at Team A AGM-84 (700 funds)"));
        b.Add(new EngagementEntry(5, t - 3, "intercepted", "A", "AGM-84", null, "Hornet 1", "Kirov", "B", "SM-2", "B", 1250, "proximity", "Team A AGM-84 from Hornet 1 intercepted by Team B SM-2 from Kirov (1,250 funds)"));
        b.Add(new EngagementEntry(6, t - 2, "hit", "A", "AGM-84", missileKey, "Hornet 1", "Kirov", "B", "Kirov", "B", 1250, "bda_scoring", "Team A AGM-84 from Hornet 1 hit Kirov (1,250 funds)"));
        b.Add(new EngagementEntry(7, t - 1, "missed", "A", "AIM-9", null, "Hornet 2", "Kirov", "B", null, null, 900, "proximity", "Team A AIM-9 from Hornet 2 missed Team B Kirov"));
        b.SetTotals(
        [
            new EngagementTotals("A", 3, 0, 1, 1, 1, 0, 0, 0, 3400, 1250, 1250, 900, 0),
            new EngagementTotals("B", 1, 0, 0, 0, 0, 0, 1, 0, 700, 0, 0, 0, 0),
        ]);
        h.App.RefreshPanels();
        h.Pump(2);
    }

    // ------------------------------------------------------------ plot toolbar

    [AvaloniaFact]
    public void Control_MissilesToggle_ShowsAndHidesMissiles()
    {
        using var h = Demo();
        Salvo(h);
        PlotToggle(h, h.Map(), "Missiles", o => o.Missiles, 150);
    }

    [AvaloniaFact]
    public void Control_MissileTrailsToggle_ShowsAndHidesMissileTrails()
    {
        using var h = Demo();
        Salvo(h, scale: 12);
        PlotToggle(h, h.Map(), "Missile trails", o => o.MissileTrails, 150);
        // only missile trails: the Trails toggle (every track) is untouched
        Assert.True(h.Map().ViewModel.Options.Trails);
    }

    [AvaloniaFact]
    public void Control_MissileToggles_PersistInTheLayoutConfig()
    {
        using var h = Demo();
        var vm = h.Map().ViewModel;
        h.Click(Toggle(h.Map(), "Missiles"));
        h.Click(Toggle(h.Map(), "Missile trails"));
        var cfg = vm.ToConfig();
        Assert.False(cfg.Missiles);
        Assert.False(cfg.MissileTrails);
        cfg.Missiles = true;
        cfg.MissileTrails = true;
        vm.Apply(cfg);
        h.Pump(2);
        Assert.True(vm.Options.Missiles && vm.Options.MissileTrails);
    }

    [AvaloniaFact]
    public void Control_InboundRing_MarksMissilesInboundToAFriendly()
    {
        using var h = Demo();
        var ship = Salvo(h);
        var plot = h.Map().ViewModel.Surface;
        h.SettledPlot();
        Assert.Equal(10, plot.State.LastFrame.InboundMarks);
        // the Red picture: the same missiles are Red's own, nothing inbound to a Red friendly
        h.Map().ViewModel.View = ViewSide.Red;
        h.SettledPlot();
        Assert.Equal(0, plot.State.LastFrame.InboundMarks);
        h.Map().ViewModel.View = ViewSide.Blue;
        // Missiles off: no rings either
        h.Click(Toggle(h.Map(), "Missiles"));
        h.SettledPlot();
        Assert.Equal(0, plot.State.LastFrame.InboundMarks);
        Assert.Equal(10, plot.State.LastFrame.MissilesHidden);
        Assert.True(ship.Alive);
    }

    [AvaloniaFact]
    public void Control_MissilesStayArrowheads_ZoomedOutPast30Km()
    {
        using var h = Demo();
        Salvo(h, scale: 1.5f); // ~1,000 px of plot across ~700 km
        var plot = h.Map().ViewModel.Surface;
        h.SettledPlot();
        var st = plot.State;
        int arrows = 0;
        foreach (var t in h.App.Data.Tracks.Where(t => t.Key.StartsWith("vessel:test-msl-", StringComparison.Ordinal)))
            if (st.TryGetSymbol(t.Id, out float s, out var tier) && tier == SymbolTier.Missile && s >= 6f - 1e-3f) arrows++;
        Assert.Equal(10, arrows);
    }

    // ------------------------------------------------------------ Layers panel

    [AvaloniaFact]
    public void Control_LayersMissileToggles_FlipTheActiveMapAndSyncTheToolbar()
    {
        using var h = Demo();
        Salvo(h);
        h.Click(Tab(h, "Layers"));
        h.Pump(3);
        var layers = h.Find<LayersView>()!;
        Assert.True(layers.IsEffectivelyVisible);
        PlotToggle(h, layers, "Missiles", o => o.Missiles, 150);
        h.Click(Toggle(layers, "Missile trails"));
        h.Pump(2);
        Assert.False(h.Map().ViewModel.Options.MissileTrails);
        Assert.False(Toggle(h.Map(), "Missile trails").IsChecked);
        h.Click(Toggle(h.Map(), "Missile trails"));
        h.Pump(2);
        Assert.True(Toggle(h.Find<LayersView>()!, "Missile trails").IsChecked);
    }

    // ------------------------------------------------------------ Engagements panel

    [AvaloniaFact]
    public void Control_EngagementsTab_ShowsThePanelWithItsNote()
    {
        using var h = Demo();
        var v = ShowEngagements(h);
        Assert.True(v.Note.IsVisible);
        Assert.Equal("No missiles fired yet.", v.Note.Text);
        Book(h);
        Assert.False(h.Find<EngagementsView>()!.Note.IsVisible);
        h.Click(Tab(h, "Messages"));
        h.Pump(3);
        Assert.False(h.Find<EngagementsView>() is { IsEffectivelyVisible: true });
    }

    [AvaloniaFact]
    public void Control_EngagementsPaletteAction_ShowsAndHidesThePanel()
    {
        using var h = Demo();
        // the top bar's panel toggles are the mockup's three; every panel, Engagements included, has a palette action
        void Run()
        {
            h.Win.KeyPressQwerty(Avalonia.Input.PhysicalKey.K, Avalonia.Input.RawInputModifiers.Control);
            h.Pump(1);
            var p = h.Win.Commands;
            p.Search.Text = "Show / hide Engagements";
            h.Pump(1);
            p.OpenItem(p.Shown.First(i => i.Title == "Show / hide Engagements"));
            h.Pump(2);
        }
        Assert.True(h.Win.Layouts.IsVisible(ShellDockFactory.Engagements));
        Run();
        Assert.False(h.Win.Layouts.IsVisible(ShellDockFactory.Engagements));
        Assert.Null(h.Find<EngagementsView>());
        Run();
        Assert.True(h.Win.Layouts.IsVisible(ShellDockFactory.Engagements));
        ShowEngagements(h);
    }

    [AvaloniaFact]
    public void Control_EngagementTotals_ShowPerTeamCountsAndFunds()
    {
        using var h = Demo();
        var v = ShowEngagements(h);
        Book(h);
        var texts = Texts(v.TotalsGrid).ToList();
        foreach (var (head, _) in EngagementsView.Columns) Assert.Contains(head.ToUpperInvariant(), texts);
        Assert.Contains("A", texts);
        Assert.Contains("B", texts);
        Assert.Contains("3,400", texts); // A spent (incl. the 2-part missile priced by the host)
        Assert.Contains("1,250", texts); // A lost to intercepts and hit value
        Assert.Contains("700", texts);   // B spent
        // team A coloured as Blue (first team), B as Red
        Assert.Equal(Side.Blue, v.SideOf("A"));
        Assert.Equal(Side.Red, v.SideOf("B"));
        Assert.Equal(7, v.Shown.Count);
        Assert.Equal(7L, v.Shown[0].Seq); // newest first
    }

    [AvaloniaFact]
    public void Control_EngagementChips_ToggleAKindWithCounts()
    {
        using var h = Demo();
        ShowEngagements(h);
        Book(h);
        var v = h.Find<EngagementsView>()!;
        var fired = Chip(v, "Fired");
        Assert.Equal("4", ((StackPanel)fired.Content!).Children.OfType<TextBlock>().Last().Text);
        Assert.Equal("1", ((StackPanel)Chip(v, "Intercepted").Content!).Children.OfType<TextBlock>().Last().Text);
        h.Click(fired);
        Assert.DoesNotContain(h.Find<EngagementsView>()!.Shown, e => e.Kind == "fired");
        Assert.Equal(3, h.Find<EngagementsView>()!.Shown.Count);
        Assert.False(Chip(h.Find<EngagementsView>()!, "Fired").IsChecked);
        h.Click(Chip(h.Find<EngagementsView>()!, "Fired"));
        Assert.Equal(7, h.Find<EngagementsView>()!.Shown.Count);
        // a new entry arrives while the panel is open: counted and listed on top
        h.App.Data.Engagements.Add(new EngagementEntry(8, h.App.Data.ViewTime, "fired", "B", "SM-2", null, "Kirov", null, null, null, null, 700, "ledger", "Team B Kirov fired SM-2"));
        h.App.RefreshPanels();
        h.Pump(1);
        Assert.Equal(8L, h.Find<EngagementsView>()!.Shown[0].Seq);
        Assert.Equal("5", ((StackPanel)Chip(h.Find<EngagementsView>()!, "Fired").Content!).Children.OfType<TextBlock>().Last().Text);
    }

    [AvaloniaFact]
    public void Control_EngagementRow_Click_PansTheMapToTheMissile()
    {
        using var h = Demo();
        var ship = Salvo(h);
        var d = h.App.Data;
        var msl = d.GetByKey("vessel:test-msl-3")!;
        ShowEngagements(h);
        Book(h, missileKey: "test-msl-3");
        h.View(0, 0, 2);
        var v = h.Find<EngagementsView>()!;
        var list = v.GetLogicalDescendants().OfType<ListBox>().First();
        var row = list.GetVisualDescendants().OfType<EngagementRow>().First(r => r.DataContext is EngagementEntry { Seq: 6 });
        h.Click(row);
        h.Pump(30);
        var target = h.Map().ViewModel.ToConfig(); // where the view is going (it eases there)
        var (mx, my) = d.PositionOf(msl, d.ViewTime);
        Assert.InRange(target.CenterX, mx - 0.5, mx + 0.5);
        Assert.InRange(target.CenterY, my - 0.5, my + 0.5);
        Assert.True(target.Scale >= 8, $"scale {target.Scale}");
        Assert.Contains(msl.Id, h.App.Selection);
        Assert.True(ship.Alive);
    }

    // ------------------------------------------------------------ live: FakeKSP + FakeBda over source.tactical

    private static void OnMain(SimHost sim, Action a)
    {
        using var done = new ManualResetEventSlim();
        Exception? err = null;
        sim.Host.Dispatcher.EnqueueMain(new Job { Action = () => { try { a(); } catch (Exception e) { err = e; } finally { done.Set(); } } });
        Assert.True(done.Wait(5000), "main-thread action timed out");
        if (err is not null) throw new InvalidOperationException("main-thread action failed", err);
    }

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

    [AvaloniaFact]
    public void Live_TacticalStream_DrawsMissilesAndFillsTheEngagementLog()
    {
        var src = FakeKspDataSource.Start();
        using var h = new UiHarness(src);
        var sim = src.Sim;
        Until(h, () => src.StreamKind is not null, "the map did not subscribe");
        Assert.Equal("query", src.StreamKind); // no binding yet: the M0 vessel stream
        Assert.Equal(HostDataSource.NoTacticalNote, h.App.Data.Engagements.Note);

        // the binding attaches in a flight scene: two teams, a ship and two fighters
        var bda = new FakeBda();
        FakeUnit kirov = null!, hornet = null!, hornet2 = null!;
        OnMain(sim, () =>
        {
            hornet = bda.Add("Hornet 1", "A", UnitClass.Fighter, 0, 0);
            hornet2 = bda.Add("Hornet 2", "A", UnitClass.Fighter, 0, 2);
            kirov = bda.Add("Kirov", "B", UnitClass.Ship, 40, 0);
            foreach (var u in new[] { hornet, hornet2, kirov }) { u.Scripted = true; u.S.GuardMode = false; u.S.SpeedMps = 0; }
            bda.Sink = sim.Host.C2.Attach(bda, bda, "fakebda");
        });
        Until(h, () => src.StreamKind == "tactical", "the map did not move up to source.tactical", 15);
        Until(h, () => h.App.Data.Tracks.Any(t => t.Alive && t.Name == "Kirov"), "no Kirov on the map");

        // a salvo: two 2-part AGM-84s and an AIM-9 at Kirov, then Kirov's SM-2 at the first
        FakeUnit m1 = null!, m2 = null!, m3 = null!, sm2 = null!;
        OnMain(sim, () =>
        {
            m1 = bda.AddMissile(hornet, kirov, 1, 0, "AGM-84", 1000, 250);
            m2 = bda.AddMissile(hornet, kirov, 1, 0.3, "AGM-84", 1000, 250);
            m3 = bda.AddMissile(hornet2, kirov, 1, 2, "AIM-9", 900);
            foreach (var m in new[] { m1, m2, m3 }) m.S.SpeedMps = 300;
        });
        OnMain(sim, () => { sm2 = bda.AddMissile(kirov, m1, 39, 0, "SM-2", 700); sm2.S.SpeedMps = 300; });
        for (int i = 0; i < 6; i++) { OnMain(sim, () => bda.Step(0.2)); Thread.Sleep(60); h.Pump(1); }
        var d = h.App.Data;
        Until(h, () => d.Tracks.Count(t => t.Alive && t.IsMissile) == 4, "the 4 missiles are not on the map");
        Until(h, () => d.Engagements.Entries.Count(e => e.Kind == "fired") == 4, "the 4 fired lines did not arrive");

        var kt = d.Tracks.First(t => t.Alive && t.Name == "Kirov");
        var ht = d.Tracks.First(t => t.Alive && t.Name == "Hornet 1");
        Assert.Equal(Side.Blue, ht.Side); // team A seen first
        Assert.Equal(Side.Red, kt.Side);
        var agm = d.Tracks.Where(t => t.Alive && t.IsMissile && t.TargetId == kt.Id).ToList();
        Assert.Equal(3, agm.Count);
        Assert.All(agm, t => Assert.Equal(TrackKind.AntiShipMissile, t.Kind));
        Assert.All(agm, t => Assert.Equal(Side.Blue, t.Side));
        var smt = d.Tracks.Single(t => t.Alive && t.IsMissile && t.Side == Side.Red);
        Assert.Equal(TrackKind.SurfaceToAirMissile, smt.Kind);
        Assert.Contains(agm[0].Details.Extra, kv => kv.Key == "Weapon");
        Assert.Contains(agm.SelectMany(t => t.Details.Extra), kv => kv.Key == "Cost" && kv.Value == "1,250 funds");
        // Red's view: the AGM-84s are inbound to Red's Kirov
        var plot = h.Map().ViewModel.Surface;
        h.Map().ViewModel.View = ViewSide.Red;
        h.Focus(kt, 4);
        h.SettledPlot();
        Assert.True(plot.State.LastFrame.InboundMarks >= 3, $"{plot.State.LastFrame.InboundMarks} inbound rings in Red's view");

        // outcomes: SM-2 intercepts m1, m2 hits Kirov (BDA scoring), m3 misses
        OnMain(sim, () =>
        {
            bda.EndMissile(m1.Id, MissileFate.Intercepted, sm2.Id, source: "proximity");
            bda.EndMissile(sm2.Id, MissileFate.Hit, m1.Id, source: "proximity");
            bda.EndMissile(m2.Id, MissileFate.Hit, kirov.Id, source: "bda_scoring");
            bda.EndMissile(m3.Id, MissileFate.Missed, source: "proximity");
        });
        for (int i = 0; i < 4; i++) { OnMain(sim, () => bda.Step(0.2)); Thread.Sleep(60); h.Pump(1); }
        Until(h, () => d.Engagements.Entries.Any(e => e.Kind == "missed") && d.Engagements.Entries.Any(e => e.Kind == "hit") && d.Engagements.Entries.Any(e => e.Kind == "intercepted"),
            "the outcome lines did not arrive: " + string.Join(" | ", d.Engagements.Entries.Select(e => e.Text)));
        Until(h, () => d.Tracks.Count(t => t.Alive && t.IsMissile) == 0, "resolved missiles are still alive on the map");
        Assert.Contains(d.Events, e => e.Kind == PlotEventKind.Impact);
        Assert.Contains(d.Events, e => e.Kind == PlotEventKind.Intercept);
        Assert.Contains(d.Events, e => e.Kind == PlotEventKind.Miss);
        Assert.Contains(d.Log.Entries, e => e.Category == LogCategory.Intercept && e.Text.Contains("intercepted by", StringComparison.Ordinal));
        Assert.Contains(d.Log.Entries, e => e.Category == LogCategory.Damage && e.Text.Contains(" hit Kirov", StringComparison.Ordinal));
        Assert.DoesNotContain(d.Log.Entries, e => e.Text.Contains("missile left the picture", StringComparison.Ordinal));

        Until(h, () => d.Engagements.TotalsOf("A") is { Fired: 3, Hits: 1, Misses: 1, Intercepted: 1 }, "team A totals");
        var a = d.Engagements.TotalsOf("A")!;
        Assert.Equal(3400, a.Spent, 3);          // 1,250 + 1,250 + 900
        Assert.Equal(1250, a.InterceptedCost, 3);
        Assert.Equal(1250, a.HitCost, 3);
        Assert.Equal(1, d.Engagements.TotalsOf("B")!.InterceptsMade);

        // the panel shows it, and the Losses tab got the expenditure lines
        var v = ShowEngagements(h);
        h.App.RefreshPanels();
        h.Pump(2);
        Assert.Contains(v.Shown, e => e.Kind == "intercepted");
        Assert.Contains("3,400", Texts(v.TotalsGrid));
        Assert.Contains(d.Losses.Blue.Lines, kv => kv.Key == "Funds spent" && kv.Value == "3,400");
    }
}
