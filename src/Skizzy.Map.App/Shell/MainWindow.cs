using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Dock.Avalonia.Controls;
using Skizzy.Client;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Data.Live;

namespace Skizzy.Map.App.Shell;

/// <summary>
/// The main window: the mockup's top bar over a docking area (Tracks left, map windows centre with Messages,
/// Comms and Losses below, Unit, Layers and Host health right), the command palette (Ctrl+K) and the global
/// keyboard. Remembers its size, position, monitor, theme and layout.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppState _app;
    private readonly DockControl _dock = new();
    private readonly LayoutManager _layouts;
    private readonly TopBar _top;
    private readonly CommandPalette _palette;
    private TimeSpan? _lastFrame;
    private bool _frameLoop;

    public MainWindow() : this(new AppState()) { }

    public MainWindow(AppState app, bool restore = true)
    {
        _app = app;
        Title = "Skizzy Map";
        MinWidth = 900;
        MinHeight = 560;
        Width = 1440;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _layouts = new LayoutManager(app, _dock);
        _top = new TopBar(app, this);
        _palette = new CommandPalette(app) { IsVisible = false };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(_top);
        var dockHost = new Border { Child = _dock, Padding = new Thickness(0) };
        Grid.SetRow(dockHost, 1);
        grid.Children.Add(dockHost);
        var root = new Panel { Margin = new Thickness(16, 10, 16, 10) };
        root.Children.Add(grid);
        var overlay = new Panel();
        overlay.Children.Add(root);
        overlay.Children.Add(_palette);
        Content = overlay;

        LayersView.NewMapRequested += _layouts.AddMap;
        AddPaletteActions();
        if (restore) RestoreSession(); else _layouts.UseDefault();
        ApplyTheme(Enum.TryParse<ThemeName>(_app.Settings.Current.Theme, out var tn) && tn == ThemeName.Light ? ThemeVariant.Light : ThemeVariant.Dark);
        if (restore) RestorePlacement();

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => { if (AutoFrames) StartFrameLoop(); foreach (var m in _app.Maps) m.FitAll(animate: false); };
        Closing += (_, _) => SaveSession();
        Closed += (_, _) => { LayersView.NewMapRequested -= _layouts.AddMap; _frameLoop = false; _layouts.CloseFloating(); };
    }

    private enum ThemeName { Dark, Light }

    /// <summary>Drive <see cref="AppState.Frame"/> from the display's frame clock (off in tests, which step frames themselves).</summary>
    public bool AutoFrames { get; set; } = true;

    public AppState App => _app;
    public LayoutManager Layouts => _layouts;
    internal CommandPalette Commands => _palette;
    internal TopBar Top => _top;

    // ------------------------------------------------------------ frame loop

    private void StartFrameLoop()
    {
        if (_frameLoop) return;
        _frameLoop = true;
        RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan t)
    {
        if (!_frameLoop) return;
        double dt = _lastFrame is { } last ? (t - last).TotalSeconds : 1 / 60.0;
        _lastFrame = t;
        _app.Frame(dt);
        RequestAnimationFrame(OnFrame);
    }

    // ------------------------------------------------------------ theme

    public void ApplyTheme(ThemeVariant v)
    {
        Skizzy.Map.App.App.UsePalette(v);
        _app.SetTheme(v);
        _app.Settings.Current.Theme = v == ThemeVariant.Light ? "Light" : "Dark";
        _top.SyncTheme();
    }

    // ------------------------------------------------------------ placement and session

    private void RestorePlacement()
    {
        var p = _app.Settings.Current.Window;
        if (p is null || p.Width < 400 || p.Height < 300) return;
        var screens = Screens?.All;
        var rect = new PixelRect(p.X, p.Y, (int)p.Width, (int)p.Height);
        bool fits = screens is null || screens.Count == 0 || screens.Any(s => s.WorkingArea.Intersect(rect).Width >= 200 && s.WorkingArea.Intersect(rect).Height >= 150);
        Width = p.Width;
        Height = p.Height;
        if (fits)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(p.X, p.Y);
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var s = _app.Settings.Current;
        bool max = WindowState == WindowState.Maximized;
        var prev = s.Window;
        var screen = Screens?.ScreenFromWindow(this);
        s.Window = new WindowPlacement
        {
            X = max && prev is not null ? prev.X : Position.X,
            Y = max && prev is not null ? prev.Y : Position.Y,
            Width = max && prev is not null ? prev.Width : Width,
            Height = max && prev is not null ? prev.Height : Height,
            Maximized = max,
            Screen = screen is null ? null : $"{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height}",
        };
    }

    private void RestoreSession()
    {
        var session = _app.Settings.LoadSession();
        if (session is null || !_layouts.Apply(session)) _layouts.UseDefault();
    }

    public void SaveSession()
    {
        SavePlacement();
        try { _app.Settings.SaveSession(_layouts.Capture(SettingsStore.SessionLayoutName)); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Trace.WriteLine("skizzy-map: session layout not saved: " + e.Message);
        }
        _app.Settings.Save();
    }

    // ------------------------------------------------------------ named layouts

    public void SaveLayoutAs(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        _app.Settings.SaveLayout(_layouts.Capture(name));
        _app.Settings.Current.Layout = name;
        _app.Settings.Save();
        _app.Toast($"Layout \"{name}\" saved.");
    }

    public void LoadLayout(string name)
    {
        var l = _app.Settings.LoadLayout(name);
        if (l is null) { _app.Toast($"Layout \"{name}\" is missing."); return; }
        if (_layouts.Apply(l))
        {
            _app.Settings.Current.Layout = name;
            _app.Settings.Save();
            ToastAfterLayout($"Layout \"{name}\" loaded.");
        }
    }

    public void ResetLayout()
    {
        _layouts.UseDefault();
        foreach (var m in _app.Maps) m.FitAll(animate: false);
        _app.Settings.Current.Layout = null;
        ToastAfterLayout("Default layout restored.");
    }

    /// <summary>A new layout re-creates the map views; they pick up a toast raised just before they were built (AppState.LastToast).</summary>
    private void ToastAfterLayout(string text) => _app.Toast(text);

    // ------------------------------------------------------------ sources

    public void UseSource(string kind)
    {
        IDataSource src;
        try
        {
            src = kind switch
            {
                "fake" => FakeKspDataSource.Start(),
                "live" => new HostDataSource(LiveEndpoint, "Live host", SourceKind.LiveHost, "Live · KSP with the Skizzy plugin"),
                _ => new DemoBattleSource(),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            _app.Toast("Could not start that source: " + e.Message);
            return;
        }
        _app.SwitchSource(src);
        _app.Settings.Current.Source.Kind = kind;
        _top.SyncSource();
    }

    private Endpoint LiveEndpoint()
    {
        var s = _app.Settings.Current.Source;
        return Endpoint.Discover(s.EndpointFile!, s.KspRoot!, null!);
    }

    // ------------------------------------------------------------ keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && (e.KeyModifiers & KeyModifiers.Control) != 0) { TogglePalette(); e.Handled = true; return; }
        if (_palette.IsVisible)
        {
            if (e.Key == Key.Escape) { _palette.Escape(); e.Handled = true; }
            return;
        }
        // like the mockup, keys typed into a field, a select or a slider stay there (splitters take the arrow keys)
        if (FocusManager?.GetFocusedElement() is TextBox or ComboBox or Slider or Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter) return;
        var m = _app.ActiveMap;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0, alt = (e.KeyModifiers & KeyModifiers.Alt) != 0, ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl) return;
        var cmd = _app.Commands;
        switch (e.Key)
        {
            case Key.Space: _app.TogglePlay(); break;
            case Key.F: if (shift) m?.FitSelection(); else m?.FitAll(); break;
            case Key.Home: if (shift) m?.FitFilter(); else m?.FitAll(); break;
            case Key.Escape:
                // a racetrack or route being drawn, then a pending mode, then the selection (the mockup's order)
                if (m?.CancelGesture?.Invoke() == true || cmd.CancelPending()) break;
                _app.ClearSelection();
                break;
            case Key.V: _ = cmd.ToggleVip(); break;
            case Key.A: _ = shift ? cmd.AllAttack() : cmd.AttackNearest(); break;
            case Key.R: _ = shift ? cmd.RtbAll() : cmd.Rtb(); break;
            case Key.T: cmd.ToggleRacetrack(fromKey: true); break;
            case Key.OemCloseBrackets: _app.SizeSelection(1.25f); break;
            case Key.OemOpenBrackets: _app.SizeSelection(.8f); break;
            case Key.OemPlus or Key.Add: m?.ZoomAt(m.Width / 2, m.Height / 2, 1.4f); break;
            case Key.OemMinus or Key.Subtract: m?.ZoomAt(m.Width / 2, m.Height / 2, 1 / 1.4f); break;
            case Key.Left: m?.PanBy(80, 0); break;
            case Key.Right: m?.PanBy(-80, 0); break;
            case Key.Up: m?.PanBy(0, 80); break;
            case Key.Down: m?.PanBy(0, -80); break;
            case >= Key.D1 and <= Key.D9:
            {
                var d = ((int)(e.Key - Key.D1) + 1).ToString(CultureInfo.InvariantCulture);
                if (alt) _app.AssignSlot(d); else _app.RecallSlot(d);
                break;
            }
            default: return;
        }
        e.Handled = true;
    }

    private void AddPaletteActions()
    {
        var p = _palette;
        p.AddAction("Pause / play", "Time", "Space", () => _app.TogglePlay());
        p.AddAction("Back to live", "Time", "leave replay", () => _app.GoLive());
        p.AddAction("Fit all", "Map", "F or Home", () => _app.ActiveMap?.FitAll());
        p.AddAction("Fit selection", "Map", "Shift+F", () => _app.ActiveMap?.FitSelection());
        p.AddAction("Attack nearest", "Orders", "A: the selection hunts the nearest hostiles", () => _app.Commands.AttackNearest());
        p.AddAction("RTB & land", "Orders", "R: the selection goes home", () => _app.Commands.Rtb());
        p.AddAction("All attack", "Orders", "Shift+A twice: every unit attacks the nearest hostiles", () => _app.Commands.AllAttack());
        p.AddAction("RTB all", "Orders", "Shift+R twice: every aircraft goes home", () => _app.Commands.RtbAll());
        p.AddAction("Mark VIP", "Orders", "V: toggles VIP on the selection", () => _app.Commands.ToggleVip());
        p.AddAction("Racetrack", "Orders", "T, then drag the long leg on the plot", () => _app.Commands.ToggleRacetrack(fromKey: false));
        p.AddAction("Fit filter", "Map", "Shift+Home", () => _app.ActiveMap?.FitFilter());
        p.AddAction("New map window", "Map", "another plot, dockable and detachable", () => _layouts.AddMap());
        p.AddAction("Toggle dark / light theme", "View", "", () => ApplyTheme(_app.Theme == ThemeVariant.Light ? ThemeVariant.Dark : ThemeVariant.Light));
        p.AddAction("Reset layout", "View", "the default panel arrangement", ResetLayout);
        foreach (var (id, title) in ShellDockFactory.ToolDefs)
        {
            var tid = id;
            p.AddAction("Show / hide " + title, "Panels", "", () => _layouts.SetVisible(tid, !_layouts.IsVisible(tid)));
        }
        p.AddAction("Source: demo battle", "Source", "the mockup's scenario, offline", () => UseSource("demo"));
        p.AddAction("Source: FakeKSP", "Source", "in-process M0 host with a fake KSP", () => UseSource("fake"));
        p.AddAction("Source: live host", "Source", "KSP with the Skizzy plugin", () => UseSource("live"));
    }

    public void TogglePalette()
    {
        if (_palette.IsVisible) _palette.Close();
        else _palette.Open();
    }
}

/// <summary>The mockup's top bar.</summary>
public sealed class TopBar : Border
{
    private readonly AppState _app;
    private readonly MainWindow _win;
    private readonly TextBlock _desc = Ui.Text("", "dim");
    private readonly TextBlock _ut = Ui.Text("Y1 D014 03:12:00", "ut");
    private readonly Button _play;
    private readonly Segmented _speeds;
    private readonly Button _restart;
    private readonly Control[] _orders;
    private readonly ToggleButton _advance;
    private readonly Button _allAtk, _rtbAll, _seatBtn;
    private readonly Border _seat;
    private readonly Segmented _posture;
    private readonly ComboBox _ff;
    private bool _allArmed, _rtbArmed;
    private readonly ComboBox _source;
    private readonly ToggleButton _tgTracks, _tgUnit, _tgLog;
    private readonly Segmented _theme;
    private readonly Border _stance;
    private bool _syncing;

    public TopBar(AppState app, MainWindow win)
    {
        _app = app;
        _win = win;
        Padding = new Thickness(2, 0, 2, 10);
        Margin = new Thickness(0, 0, 0, 8);
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var brand = Ui.Text("SKIZZY COMMAND", "brand");
        _desc.FontSize = 12; _desc.FontWeight = FontWeight.Medium;
        var brandRow = Ui.H(10, brand, _desc);
        brandRow.VerticalAlignment = VerticalAlignment.Center;

        var utLbl = Ui.Text("UNIVERSAL TIME (KERBIN)");
        utLbl.FontSize = 9.5; utLbl.FontWeight = FontWeight.Medium; utLbl.LetterSpacing = 1.52; utLbl.Margin = new Thickness(0, 0, 0, 3);
        Ui.BindFont(utLbl, "FontDisplay"); Ui.BindBrush(utLbl, "TextFaintBrush");
        var utBlock = Ui.V(0, utLbl, _ut);

        _play = Ui.Button("❚❚", () => _app.TogglePlay(), "icon");
        ToolTip.SetTip(_play, "Pause / play (Space)");
        _speeds = Ui.Seg(AppState.Speeds.Select(s => (s < 1 ? s.ToString(CultureInfo.InvariantCulture).Replace("0.", ".", StringComparison.Ordinal) : s.ToString(CultureInfo.InvariantCulture)) + "×").ToList(), 2,
            i => _app.Source.SetSpeed(AppState.Speeds[i]));
        _restart = Ui.Button("Restart", () => _app.Restart(), "ghost");
        var timeGrp = Ui.H(4, _play, _speeds, _restart);

        _stance = Ui.Pill("Blue ↔ Red · War", "war");
        ToolTip.SetTip(_stance, "Side stance");

        var cmd = _app.Commands;
        _advance = Ui.Toggle("Advance: on", true, on =>
        {
            if (_syncing) return;
            _advance!.Content = "Advance: " + (on ? "on" : "off");
            _ = cmd.SetAdvance(on);
        }, "btn");
        ToolTip.SetTip(_advance, "Advance on contact: when the first hostile is identified, airborne fighters and drones head for the nearest identified hostile.");
        _allAtk = Ui.Button("All attack", () => cmd.AllAttack());
        ToolTip.SetTip(_allAtk, "Every Blue unit attacks the nearest hostiles; carriers launch their decks. Click twice to confirm.");
        _rtbAll = Ui.Button("RTB all", () => cmd.RtbAll());
        ToolTip.SetTip(_rtbAll, "Send every Blue aircraft home to land on Kestrel. Click twice to confirm.");
        _posture = Ui.Seg(["Defensive", "Proactive"], 1, i => { if (!_syncing) _ = cmd.SetPosture(i == 0 ? "Defensive" : "Proactive"); }, uiFont: true);
        var postureGrp = Ui.H(4, Ui.Label("Blue posture"), _posture);
        _ff = new ComboBox { ItemsSource = new[] { "Off", "1 in 1,000 missiles", "1 in 100 missiles" }, SelectedIndex = 1 };
        _ff.Classes.Add("tsel");
        _ff.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            cmd.SetFriendlyFire(_ff.SelectedIndex switch { 0 => 0, 2 => .01, _ => .001 });
        };
        var ffGrp = Ui.H(4, Ui.Label("Friendly fire"), _ff);
        _seat = Ui.Pill("Blue seat · You", "seat");
        _seatBtn = Ui.Button("Hand seat to agent", () => cmd.ToggleSeat(), "ghost");
        _orders = [_advance, Ui.H(6, _allAtk, _rtbAll), postureGrp];
        var reset = Ui.Button("Reset layout", () => _win.ResetLayout(), "ghost");
        ToolTip.SetTip(reset, "Restore the default panel sizes and show every panel");

        _source = new ComboBox { ItemsSource = new[] { "Demo battle", "FakeKSP (in-process)", "Live host (KSP)" }, SelectedIndex = 0 };
        _source.Classes.Add("tsel");
        _source.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _win.UseSource(_source.SelectedIndex switch { 1 => "fake", 2 => "live", _ => "demo" });
        };
        var sourceGrp = Ui.H(4, Ui.Label("Source"), _source);

        _tgTracks = Ui.Toggle("Tracks", true, on => { if (!_syncing) _win.Layouts.SetVisible(ShellDockFactory.Tracks, on); }, "tg");
        _tgUnit = Ui.Toggle("Unit", true, on => { if (!_syncing) _win.Layouts.SetVisible(ShellDockFactory.Unit, on); }, "tg");
        _tgLog = Ui.Toggle("Log", true, on => { if (!_syncing) _win.Layouts.SetVisible(ShellDockFactory.Messages, on); }, "tg");
        var layouts = Ui.Button("Layouts ▾", () => { }, "ghost");
        layouts.Flyout = BuildLayoutsMenu();
        ToolTip.SetTip(layouts, "Save, load or delete named layouts; open another map window; reset");
        var panelsGrp = Ui.H(4, Ui.Label("Panels"), _tgTracks, _tgUnit, _tgLog, reset, layouts);

        _theme = Ui.Seg(["Dark", "Light"], 0, i => { if (!_syncing) _win.ApplyTheme(i == 1 ? ThemeVariant.Light : ThemeVariant.Dark); }, uiFont: true);
        ToolTip.SetTip(_theme, "Theme");
        var cmdBtn = Ui.Button("Commands", () => _win.TogglePalette(), "ghost");
        ToolTip.SetTip(cmdBtn, "Command palette: every app action and host capability (Ctrl+K)");

        // the mockup's header: one flex-wrap row, with the panel controls pushed right on whichever row they land
        var bar = new FlowBar { ItemSpacing = 18, LineSpacing = 10 };
        bar.Children.AddRange([brandRow, utBlock, timeGrp, _stance, .. _orders, ffGrp, Ui.H(10, _seat, _seatBtn), Ui.H(18, sourceGrp, panelsGrp, _theme, cmdBtn)]);
        Child = bar;

        _app.Ticked += OnTick;
        _app.SourceChanged += SyncSource;
        _app.SlowTick += SyncPanels;
        cmd.Changed += SyncCommands;
        SyncSource();
    }

    internal Button AllAttackButton => _allAtk;
    internal Button RtbAllButton => _rtbAll;
    internal ToggleButton AdvanceButton => _advance;
    internal Segmented PostureSeg => _posture;
    internal ComboBox FriendlyFireBox => _ff;
    internal Border SeatPill => _seat;
    internal Button SeatButton => _seatBtn;

    /// <summary>Confirm labels (3 s), seat pill and the side-level values the command service holds.</summary>
    private void SyncCommands()
    {
        var c = _app.Commands;
        bool aa = c.AllAttackArmed, ra = c.RtbAllArmed;
        if (aa != _allArmed) { _allArmed = aa; _allAtk.Content = aa ? "Confirm all attack" : "All attack"; _allAtk.Classes.Set("acc", aa); }
        if (ra != _rtbArmed) { _rtbArmed = ra; _rtbAll.Content = ra ? "Confirm RTB all" : "RTB all"; _rtbAll.Classes.Set("acc", ra); }
        string pill = (c.SeatAgent ? "Blue seat · Agent (Claude Code)" : "Blue seat · You").ToUpperInvariant();
        if (_seat.Child is TextBlock t && t.Text != pill) t.Text = pill;
        string btn = c.SeatAgent ? "Take seat back" : "Hand seat to agent";
        if ((_seatBtn.Content as string) != btn) _seatBtn.Content = btn;
    }

    private MenuFlyout BuildLayoutsMenu()
    {
        var fly = new MenuFlyout();
        fly.Opening += (_, _) =>
        {
            var items = new List<object>();
            var name = new TextBox { PlaceholderText = "Layout name", MinWidth = 180 };
            name.Classes.Add("inp");
            var save = Ui.Button("Save", () => { _win.SaveLayoutAs(name.Text ?? ""); fly.Hide(); }, "acc");
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _win.SaveLayoutAs(name.Text ?? ""); fly.Hide(); e.Handled = true; } };
            items.Add(new MenuItem { Header = Ui.H(6, name, save), StaysOpenOnClick = true });
            var saved = _app.Settings.ListLayouts();
            if (saved.Count > 0) items.Add(new Separator());
            foreach (var n in saved)
            {
                var nn = n;
                var mi = new MenuItem { Header = n + (_app.Settings.Current.Layout == n ? "  ✓" : "") };
                mi.Click += (_, _) => _win.LoadLayout(nn);
                var del = new MenuItem { Header = "Delete" };
                del.Click += (_, _) => { _app.Settings.DeleteLayout(nn); _app.Toast($"Layout \"{nn}\" deleted."); };
                mi.Items.Add(new MenuItem { Header = "Load" });
                ((MenuItem)mi.Items[0]!).Click += (_, _) => _win.LoadLayout(nn);
                mi.Items.Add(del);
                items.Add(mi);
            }
            items.Add(new Separator());
            var newMap = new MenuItem { Header = "New map window" };
            newMap.Click += (_, _) => _win.Layouts.AddMap();
            items.Add(newMap);
            var reset = new MenuItem { Header = "Reset to default layout" };
            reset.Click += (_, _) => _win.ResetLayout();
            items.Add(reset);
            fly.ItemsSource = items;
        };
        fly.ItemsSource = new List<object> { new MenuItem { Header = "…" } };
        return fly;
    }

    private void OnTick()
    {
        var d = _app.Data;
        _ut.Text = d.UtText(d.ViewTime);
        _play.Content = _app.Paused ? "▶" : "❚❚";
        var c = _app.Commands;
        if (c.AllAttackArmed != _allArmed || c.RtbAllArmed != _rtbArmed) SyncCommands(); // the 3 s confirmations lapse
    }

    public void SyncSource()
    {
        _syncing = true;
        try
        {
            var s = _app.Source;
            _desc.Text = s.Description;
            bool demo = s is DemoBattleSource;
            var cmd = _app.Commands;
            foreach (var c in _orders) c.IsVisible = cmd.CanCommand;
            _stance.IsVisible = demo;
            Exception(_ff, demo ? null : Unsupported.FriendlyFire);
            Exception(_seatBtn, demo ? null : Unsupported.Seat);
            _play.IsEnabled = (s.Caps & SourceCaps.Pause) != 0;
            _speeds.IsVisible = (s.Caps & SourceCaps.Speed) != 0;
            _restart.IsVisible = (s.Caps & SourceCaps.Restart) != 0;
            bool live = s is Skizzy.Map.Data.Live.HostDataSource;
            _restart.Content = live ? "Reconnect" : "Restart";
            ToolTip.SetTip(_restart, live ? "Drop the connection to KSP and connect again (the stream and the engagement log are fetched afresh)." : "Restart the scenario");
            int si = Array.IndexOf(AppState.Speeds, s.Speed);
            _speeds.SetSelected(si < 0 ? 2 : si);
            bool adv = s is DemoBattleSource dd ? dd.AdvanceOnContact : cmd.Advance;
            _advance.IsChecked = adv;
            _advance.Content = "Advance: " + (adv ? "on" : "off");
            _posture.SetSelected((s is DemoBattleSource dp ? dp.Posture : cmd.Posture) == "Defensive" ? 0 : 1);
            if (s is DemoBattleSource d) _ff.SelectedIndex = d.FriendlyFire <= 0 ? 0 : d.FriendlyFire >= .005 ? 2 : 1;
            SyncCommands();
            _source.SelectedIndex = s.Kind switch { SourceKind.FakeKsp => 1, SourceKind.LiveHost => 2, _ => 0 };
        }
        finally { _syncing = false; }
    }

    /// <summary>A control this source cannot perform: disabled, with the reason as its tooltip (null = enabled).</summary>
    private static void Exception(Control c, string? reason)
    {
        c.IsEnabled = reason is null;
        ToolTip.SetTip(c, reason);
        ToolTip.SetShowOnDisabled(c, reason is not null);
    }

    public void SyncTheme()
    {
        _syncing = true;
        bool light = _app.Theme == ThemeVariant.Light;
        _theme.SetSelected(light ? 1 : 0);
        _syncing = false;
    }

    private void SyncPanels()
    {
        _syncing = true;
        try
        {
            _tgTracks.IsChecked = _win.Layouts.IsVisible(ShellDockFactory.Tracks);
            _tgUnit.IsChecked = _win.Layouts.IsVisible(ShellDockFactory.Unit);
            _tgLog.IsChecked = _win.Layouts.IsVisible(ShellDockFactory.Messages);
        }
        finally { _syncing = false; }
    }
}

/// <summary>
/// A wrapping row like CSS flex-wrap with a spacer before the last child: children flow left to right and wrap,
/// and the last child sits at the right end of whichever row it lands on. Children are centred vertically per row.
/// </summary>
public sealed class FlowBar : Panel
{
    public double ItemSpacing { get; set; } = 18;
    public double LineSpacing { get; set; } = 10;

    private List<(int From, int To, double Height)> Rows(double width)
    {
        var rows = new List<(int, int, double)>();
        int start = 0;
        double x = 0, h = 0;
        var kids = Children.Where(c => c.IsVisible).ToList();
        for (int i = 0; i < kids.Count; i++)
        {
            var d = kids[i].DesiredSize;
            double need = (x > 0 ? ItemSpacing : 0) + d.Width;
            if (x > 0 && x + need > width)
            {
                rows.Add((start, i, h));
                start = i; x = 0; h = 0;
                need = d.Width;
            }
            x += need;
            h = Math.Max(h, d.Height);
        }
        if (start < kids.Count) rows.Add((start, kids.Count, h));
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        double width = double.IsInfinity(availableSize.Width) ? Children.Sum(c => c.DesiredSize.Width + ItemSpacing) : availableSize.Width;
        var rows = Rows(width);
        double height = rows.Sum(r => r.Height) + Math.Max(0, rows.Count - 1) * LineSpacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kids = Children.Where(c => c.IsVisible).ToList();
        var rows = Rows(finalSize.Width);
        double y = 0;
        foreach (var (from, to, h) in rows)
        {
            double x = 0;
            for (int i = from; i < to; i++)
            {
                var c = kids[i];
                var d = c.DesiredSize;
                if (x > 0) x += ItemSpacing;
                double cx = i == kids.Count - 1 ? Math.Max(x, finalSize.Width - d.Width) : x;
                c.Arrange(new Rect(cx, y + (h - d.Height) / 2, d.Width, d.Height));
                x = cx + d.Width;
            }
            y += h + LineSpacing;
        }
        return finalSize;
    }
}
