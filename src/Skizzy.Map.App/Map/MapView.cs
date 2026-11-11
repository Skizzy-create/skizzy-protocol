using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skizzy.Map.App.Plot;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Map;

/// <summary>
/// A map window's content: the plot plus the mockup's overlays (plot toolbar with layer toggles and filter chips,
/// pause-on-event banner, hover tooltip, toast, legend, timeline with replay scrubber) and the plot interactions
/// (wheel zoom at the cursor, drag pan, Shift-drag box select, click and Shift-click, double-click group) and the
/// order gestures through <see cref="CommandService"/>: right-click to move, attack or protect, right-drag to draw a
/// route, the racetrack drag and the Send to… click, with a live preview of what is being drawn. Ctrl+right-click
/// opens the view menu.
/// </summary>
public sealed class MapView : UserControl
{
    private readonly AppState _app;
    private readonly MapViewModel _vm;
    private readonly PlotControl _plot;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _box = new() { BorderThickness = new Thickness(1), IsVisible = false };

    private readonly Dictionary<string, ToggleButton> _toggles = new();
    private Segmented _viewSeg = null!;
    private ComboBox _sizeMode = null!;
    private Slider _sizeMul = null!;
    private readonly WrapPanel _chipRow = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly TextBlock _filterBadge = Ui.Text("", "note");

    private readonly Border _banner;
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560, FontSize = 12, FontWeight = FontWeight.Medium };
    private readonly Button _noPause;

    private readonly Border _tip;
    private readonly TextBlock _tipName = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _tipBody = new() { FontSize = 10.5, LineHeight = 15 };

    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12, FontWeight = FontWeight.Medium };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(ToastSeconds) };

    private readonly Border _legendBox;
    private readonly TextBlock _tlTime = Ui.Text("T+0:00");
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1, Value = 1 };
    private readonly TimelineTicks _ticks = new() { Height = 6, IsHitTestVisible = false };
    private readonly Button _live;
    private readonly TextBlock _replayPill = Ui.Text("");
    private bool _updatingScrub;

    // pointer state
    private Point _downPos, _lastPos;
    private bool _down, _moved, _boxing, _panning;

    /// <summary>The press was the second click of a double-click: its release must not undo the group selection.</summary>
    private bool _second;
    private Point _hoverPos;

    // order gestures: right-drag route (a point every 14 px once past 10 px), racetrack drag
    private bool _rdown, _rmoved, _tdrag;
    private Point _rStart, _rLast;
    private readonly List<(double X, double Y)> _route = new();
    private (double X, double Y) _ta, _tb;
    private readonly Polyline _preview = new() { IsHitTestVisible = false, IsVisible = false, StrokeThickness = 1.5 };

    public MapView(AppState app, MapViewModel vm)
    {
        _app = app;
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;

        _plot = new PlotControl { Surface = vm.Surface, Cursor = new Cursor(StandardCursorType.Cross) };
        vm.Invalidate = _plot.InvalidateVisual;

        _box.Bind(Border.BorderBrushProperty, _box.GetResourceObservable("AccentBrush"));
        _box.Background = new SolidColorBrush(Color.FromArgb(28, 181, 163, 255));
        _overlay.Children.Add(_box);
        _preview.Bind(Shape.StrokeProperty, _preview.GetResourceObservable("AccentBrush"));
        _preview.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 3 };
        _overlay.Children.Add(_preview);
        vm.CancelGesture = CancelGesture;

        var toolbar = BuildToolbar();

        _noPause = Ui.Button("Don't pause again", () => _app.DontPauseAgain());
        ToolTip.SetTip(_noPause, "Keep running through later events. They are still logged.");
        var bannerHead = Ui.Text("PAUSED ON EVENT");
        bannerHead.FontSize = 10.5; bannerHead.FontWeight = FontWeight.SemiBold; bannerHead.LetterSpacing = 1.47;
        Ui.BindFont(bannerHead, "FontDisplay"); Ui.BindBrush(bannerHead, "CritBrush");
        bannerHead.Margin = new Thickness(0, 0, 0, 3);
        _banner = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 58, 12, 0),
            IsVisible = false,
            Child = Ui.H(12, Ui.V(0, bannerHead, _bannerText), Ui.Button("Resume", () => _app.Resume()), _noPause),
        };
        _banner.Bind(Border.BackgroundProperty, _banner.GetResourceObservable("BannerFillBrush"));
        _banner.Bind(Border.BorderBrushProperty, _banner.GetResourceObservable("BannerBorderBrush"));

        Ui.BindFont(_tipBody, "FontData");
        _tip = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, IsVisible = false,
            Child = Ui.V(1, _tipName, _tipBody),
        };
        _tip.Bind(Border.BackgroundProperty, _tip.GetResourceObservable("OverlayStrongBrush"));
        _tip.Bind(Border.BorderBrushProperty, _tip.GetResourceObservable("LineHiBrush"));

        _toast = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 52),
            MaxWidth = 560, IsVisible = false, IsHitTestVisible = false, Child = _toastText,
        };
        _toast.Bind(Border.BackgroundProperty, _toast.GetResourceObservable("ToastFillBrush"));
        _toast.Bind(Border.BorderBrushProperty, _toast.GetResourceObservable("ToastBorderBrush"));
        _toastTimer.Tick += (_, _) => { _toast.IsVisible = false; _toastTimer.Stop(); };

        _legendBox = new Border
        {
            Padding = new Thickness(10, 8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 50), Child = new Legend(), IsHitTestVisible = false,
        };
        _legendBox.Classes.Add("overlay");
        _legendBox.Bind(Border.BackgroundProperty, _legendBox.GetResourceObservable("LegendBrush"));

        // timeline
        _tlTime.FontSize = 11; _tlTime.MinWidth = 62; _tlTime.FontWeight = FontWeight.Medium;
        Ui.BindFont(_tlTime, "FontData"); Ui.BindBrush(_tlTime, "TextDimBrush");
        _scrub.Classes.Add("acc");
        _scrub.ValueChanged += (_, e) =>
        {
            if (_updatingScrub) return;
            _app.ScrubTo(e.NewValue);
        };
        _live = Ui.Button("Live", () => _app.GoLive(), "acc");
        _replayPill.FontSize = 10; _replayPill.FontWeight = FontWeight.SemiBold; _replayPill.LetterSpacing = 1.2;
        Ui.BindFont(_replayPill, "FontDisplay"); Ui.BindBrush(_replayPill, "WarnBrush");
        var tlGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        tlGrid.Children.Add(new Border { Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = _ticks });
        tlGrid.Children.Add(_scrub);
        var tlBox = new Border { Height = 30, Padding = new Thickness(10, 0), Child = tlGrid };
        tlBox.Classes.Add("overlay");
        var tl = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 10),
        };
        tl.Children.Add(_tlTime);
        Grid.SetColumn(tlBox, 1); tl.Children.Add(tlBox);
        Grid.SetColumn(_replayPill, 2); tl.Children.Add(_replayPill);
        Grid.SetColumn(_live, 3); tl.Children.Add(_live);

        var root = new Panel();
        root.Children.Add(_plot);
        root.Children.Add(_overlay);
        root.Children.Add(toolbar);
        root.Children.Add(_banner);
        // the banner sits just under the floating toolbar, whatever height its rows wrap to
        toolbar.SizeChanged += (_, e) => _banner.Margin = new Thickness(12, toolbar.Margin.Top + e.NewSize.Height + 8, 12, 0);
        root.Children.Add(_legendBox);
        root.Children.Add(tl);
        root.Children.Add(_toast);
        root.Children.Add(_tip);
        var frame = new Border { Child = root, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("LineBrush"));
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("SeaBrush"));
        Content = frame;

        _plot.PointerPressed += OnPressed;
        _plot.PointerMoved += OnMoved;
        _plot.PointerReleased += OnReleased;
        _plot.PointerExited += (_, _) => { _vm.HoverId = 0; _tip.IsVisible = false; };
        _plot.PointerWheelChanged += OnWheel;
        _plot.DoubleTapped += OnDoubleTapped;
        _plot.PointerCaptureLost += (_, _) => { EndDrag(); EndOrderGesture(); };
        _plot.SizeChanged += (_, e) => _vm.SetSize((float)e.NewSize.Width, (float)e.NewSize.Height);
        GotFocus += (_, _) => _app.Activate(_vm);
        PointerPressed += (_, _) => _app.Activate(_vm);

        _app.Ticked += OnTick;
        _app.SlowTick += OnSlowTick;
        _app.ToastRequested += OnToast;
        _app.SourceChanged += SyncToolbar;
        _vm.Changed += SyncToolbar;
        DetachedFromVisualTree += (_, _) => Unhook();
        AttachedToVisualTree += (_, _) => Hook();
        _hooked = true;
        SyncToolbar();
        OnSlowTick();
        // a layout change re-creates the views: a toast raised a moment before (“Default layout restored.”) still shows
        if (_app.LastToast is { } last && _app.Now - last.At < 1.0) OnToast(last.Text);
    }

    /// <summary>How long a toast stays up (the mockup's 4.5 s).</summary>
    public const double ToastSeconds = 4.5;

    private bool _hooked;

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _app.Ticked += OnTick;
        _app.SlowTick += OnSlowTick;
        _app.ToastRequested += OnToast;
        _app.SourceChanged += SyncToolbar;
        _vm.Changed += SyncToolbar;
        _vm.Invalidate = _plot.InvalidateVisual;
        _vm.CancelGesture = CancelGesture;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        _app.Ticked -= OnTick;
        _app.SlowTick -= OnSlowTick;
        _app.ToastRequested -= OnToast;
        _app.SourceChanged -= SyncToolbar;
        _vm.Changed -= SyncToolbar;
    }

    public MapViewModel ViewModel => _vm;
    public PlotControl Plot => _plot;
    internal Border Banner => _banner;
    internal Border Toast => _toast;
    internal Border Tip => _tip;
    internal string TipTitle => _tipName.Text ?? "";
    internal Border LegendBox => _legendBox;
    internal Slider Scrubber => _scrub;
    internal Button LiveButton => _live;

    /// <summary>The route or racetrack being drawn (screen points), hidden when nothing is.</summary>
    internal Polyline Preview => _preview;

    // ------------------------------------------------------------ toolbar

    private Border BuildToolbar()
    {
        _sizeMode = new ComboBox
        {
            ItemsSource = new[] { "Auto: radar size, no overlap", "Radar size only", "Grow with zoom", "Fixed size" },
            SelectedIndex = 0,
        };
        _sizeMode.Classes.Add("tsel");
        _sizeMode.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _vm.Options.SizeMode = (SizeMode)Math.Max(0, _sizeMode.SelectedIndex);
        };
        _sizeMul = new Slider { Minimum = .6, Maximum = 1.8, Value = 1, Width = 84, SmallChange = .05, LargeChange = .1 };
        _sizeMul.Classes.Add("acc");
        ToolTip.SetTip(_sizeMul, "Symbol scale");
        _sizeMul.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _vm.Options.SizeScale = (float)e.NewValue;
        };

        ToggleButton Tg(string key, string text, string? tip = null)
        {
            var b = Ui.Toggle(text, false, on => SetOption(key, on), "tg");
            if (tip is not null) ToolTip.SetTip(b, tip);
            _toggles[key] = b;
            return b;
        }

        var zout = Ui.Button("−", () => _vm.ZoomAt(_vm.Width / 2, _vm.Height / 2, 1 / 1.4f), "ghost", "icon");
        var zin = Ui.Button("+", () => _vm.ZoomAt(_vm.Width / 2, _vm.Height / 2, 1.4f), "ghost", "icon");
        var fit = Ui.Button("Fit", () => _vm.FitAll(), "ghost", "icon");
        ToolTip.SetTip(zout, "Zoom out (−)");
        ToolTip.SetTip(zin, "Zoom in (+)");
        ToolTip.SetTip(fit, "Fit all (F or Home). Shift+F fits the selection.");

        _viewSeg = Ui.Seg(["Blue fog", "Red fog", "God"], 0, i => _vm.View = (ViewSide)i, uiFont: true);
        ToolTip.SetTip(_viewSeg, "Picture: what Blue knows, what Red knows, or the truth");

        var row1 = Ui.Wrap(6, 6,
            Ui.Label("Symbols"), _sizeMode, _sizeMul, Ui.Sep(),
            Tg("trails", "Trails"), Tg("stalks", "Altitude"), Tg("lines", "Target lines"), Tg("rings", "Rings"), Tg("labels", "Labels"),
            Tg("counts", "Counts", "Optional: group missiles and drones into counts"), Ui.Sep(),
            zout, zin, fit, Ui.Sep(), _viewSeg);
        foreach (var c in row1.Children) c.VerticalAlignment = VerticalAlignment.Center;

        var panel = Ui.V(6, row1, Ui.H(6, _chipRow, _filterBadge));
        var bar = new Border
        {
            Padding = new Thickness(8, 6), Margin = new Thickness(10, 10, 10, 0), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Child = panel,
        };
        bar.Classes.Add("overlay");
        return bar;
    }

    private bool _syncing;

    private void SetOption(string key, bool on)
    {
        if (_syncing) return;
        var o = _vm.Options;
        switch (key)
        {
            case "trails": o.Trails = on; break;
            case "stalks": o.AltitudeStalks = on; break;
            case "lines": o.TargetLines = on; break;
            case "rings": o.Rings = on; break;
            case "labels": o.Labels = on; break;
            case "counts": o.Counts = on; break;
        }
        _vm.NotifyChanged();
    }

    private void SyncToolbar()
    {
        _syncing = true;
        try
        {
            var o = _vm.Options;
            _toggles["trails"].IsChecked = o.Trails;
            _toggles["stalks"].IsChecked = o.AltitudeStalks;
            _toggles["lines"].IsChecked = o.TargetLines;
            _toggles["rings"].IsChecked = o.Rings;
            _toggles["labels"].IsChecked = o.Labels;
            _toggles["counts"].IsChecked = o.Counts;
            _sizeMode.SelectedIndex = (int)o.SizeMode;
            _sizeMul.Value = o.SizeScale;
            _viewSeg.SetSelected((int)_vm.View);
            _legendBox.IsVisible = _vm.ShowLegend;
            _noPause.IsVisible = _app.Source is DemoBattleSource { PauseOnEvents: true };
        }
        finally { _syncing = false; }
        RebuildChips();
    }

    private static readonly (PlotFilter F, string Name)[] ChipDefs =
    [
        (PlotFilter.All, "All"), (PlotFilter.Air, "Aircraft"), (PlotFilter.Missiles, "Missiles"), (PlotFilter.Ships, "Ships & subs"),
        (PlotFilter.Inbound, "Inbound"), (PlotFilter.Hostile, "Hostile"), (PlotFilter.Friendly, "Friendly"),
    ];

    private void RebuildChips()
    {
        var counts = CountByFilter();
        _chipRow.Children.Clear();
        foreach (var (f, name) in ChipDefs)
        {
            var ff = f;
            _chipRow.Children.Add(Ui.Chip(name, counts[(int)f].ToString(CultureInfo.InvariantCulture), _vm.Filter == f, () => { _vm.Filter = ff; }));
        }
        _filterBadge.Text = _vm.Filter == PlotFilter.All || _vm.Sync.FilteredOut == 0 ? "" : $"{_vm.Sync.FilteredOut} hidden by filter";
    }

    private int[] CountByFilter()
    {
        var n = new int[ChipDefs.Length];
        var d = _app.Data;
        double t = d.ViewTime;
        foreach (var tr in d.Tracks)
        {
            if (!(d.InReplay ? tr.ExistsAt(t) : tr.Alive) || tr.DetectionFor(_vm.View) == TrackDetection.None) continue;
            for (int i = 0; i < ChipDefs.Length; i++)
                if (MapViewModel.FilterFor(ChipDefs[i].F)?.Invoke(tr) ?? true) n[i]++;
        }
        return n;
    }

    // ------------------------------------------------------------ per frame

    private void OnTick()
    {
        var d = _app.Data;
        bool show = d.PauseBanner is not null;
        if (_banner.IsVisible != show) _banner.IsVisible = show;
        if (show && _bannerText.Text != d.PauseBanner) _bannerText.Text = d.PauseBanner;

        _tlTime.Text = "T+" + MapData.ElapsedText(d.ViewTime);
        _updatingScrub = true;
        try
        {
            double lo = _app.ReplayFloor, hi = Math.Max(lo + 1, d.LiveTime);
            _scrub.Minimum = lo;
            _scrub.Maximum = hi;
            _scrub.Value = Math.Clamp(d.ViewTime, lo, hi);
            _ticks.Data = d; _ticks.T0 = lo; _ticks.T1 = hi;
        }
        finally { _updatingScrub = false; }
        bool replay = d.InReplay;
        _live.Content = replay ? "Back to live" : "Live";
        _live.Classes.Set("live", !replay);
        _replayPill.Text = replay ? "REPLAY" : "";

        if (_vm.HoverId != 0 && _tip.IsVisible) ShowTip(_vm.HoverId, _hoverPos);
    }

    private void OnSlowTick()
    {
        RebuildChips();
        _ticks.InvalidateVisual();
    }

    private void OnToast(string text)
    {
        if (_app.ActiveMap != _vm) return;
        _toastText.Text = text;
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------ pointer

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _app.Activate(_vm);
        Focus();
        var pt = e.GetCurrentPoint(_plot);
        _downPos = _lastPos = pt.Position;
        _moved = false;
        if (pt.Properties.IsRightButtonPressed)
        {
            // right: a click orders (move / attack / protect), a drag draws a route
            _rdown = true;
            _rmoved = false;
            _rStart = _rLast = pt.Position;
            _route.Clear();
            _route.Add(World(pt.Position));
            e.Pointer.Capture(_plot);
            e.Handled = true;
            return;
        }
        if (pt.Properties.IsLeftButtonPressed && _app.Commands.Pending == PendingMode.Racetrack)
        {
            _tdrag = true;
            _ta = _tb = World(pt.Position);
            e.Pointer.Capture(_plot);
            ShowTrackPreview();
            e.Handled = true;
            return;
        }
        if (pt.Properties.IsLeftButtonPressed)
        {
            _down = true;
            _second = e.ClickCount >= 2;
            _boxing = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            _panning = false;
            e.Pointer.Capture(_plot);
        }
        else if (pt.Properties.IsMiddleButtonPressed)
        {
            _down = true;
            _boxing = false;
            _panning = true;
            e.Pointer.Capture(_plot);
        }
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_plot);
        _hoverPos = p;
        if (_tdrag)
        {
            _tb = World(p);
            ShowTrackPreview();
        }
        if (_rdown)
        {
            if (!_rmoved && Dist(p, _rStart) > 10) _rmoved = true;
            if (_rmoved && Dist(p, _rLast) > 14) { _route.Add(World(p)); _rLast = p; }
            if (_rmoved) ShowRoutePreview(p);
        }
        if (_down)
        {
            var dx = p.X - _downPos.X;
            var dy = p.Y - _downPos.Y;
            if (!_moved && Math.Abs(dx) + Math.Abs(dy) > 3)
            {
                _moved = true;
                if (!_boxing) { _panning = true; _plot.Cursor = new Cursor(StandardCursorType.SizeAll); }
            }
            if (_moved)
            {
                if (_boxing)
                {
                    double x0 = Math.Min(_downPos.X, p.X), y0 = Math.Min(_downPos.Y, p.Y);
                    Canvas.SetLeft(_box, x0); Canvas.SetTop(_box, y0);
                    _box.Width = Math.Abs(dx); _box.Height = Math.Abs(dy);
                    _box.IsVisible = true;
                }
                else if (_panning)
                {
                    _vm.PanBy((float)(p.X - _lastPos.X), (float)(p.Y - _lastPos.Y));
                }
            }
            _lastPos = p;
        }
        int hit = _vm.HitTest((float)p.X, (float)p.Y, hover: true);
        _vm.HoverId = hit;
        if (hit != 0 && !_moved) ShowTip(hit, p);
        else _tip.IsVisible = false;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var p = e.GetPosition(_plot);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            e.Handled = true;
            if (!_rdown) return; // Esc cancelled the route
            bool moved = _rmoved;
            List<(double X, double Y)>? route = null;
            if (moved) { _route.Add(World(p)); route = _route.ToList(); }
            EndOrderGesture();
            e.Pointer.Capture(null);
            if (route is not null) { _ = _app.Commands.Route(route); return; }
            if ((e.KeyModifiers & KeyModifiers.Control) != 0) { ShowContextMenu(p); return; }
            int rhit = _vm.HitTest((float)p.X, (float)p.Y);
            var (rx, ry) = World(p);
            _ = _app.Commands.RightClick(rx, ry, rhit != 0 ? _app.Data.Get(rhit) : null);
            return;
        }
        if (_tdrag && e.InitialPressMouseButton == MouseButton.Left)
        {
            _tb = World(p);
            var (a, b) = (_ta, _tb);
            EndOrderGesture();
            e.Pointer.Capture(null);
            e.Handled = true;
            _ = _app.Commands.Racetrack(a, b);
            return;
        }
        if (!_down) return;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (_boxing && _moved)
        {
            float x0 = (float)Math.Min(_downPos.X, p.X), x1 = (float)Math.Max(_downPos.X, p.X);
            float y0 = (float)Math.Min(_downPos.Y, p.Y), y1 = (float)Math.Max(_downPos.Y, p.Y);
            var inBox = _vm.TracksInRect(x0, y0, x1, y1);
            var own = inBox.Where(id => _app.Data.Get(id) is { Side: Side.Blue, IsMissile: false }).ToList();
            var pick = own.Count > 0 ? own : inBox;
            _app.Select(pick, pick.FirstOrDefault());
        }
        else if (!_moved && e.InitialPressMouseButton == MouseButton.Left && _app.Commands.Pending == PendingMode.SendTo)
        {
            int hit = _vm.HitTest((float)p.X, (float)p.Y);
            var (wx, wy) = World(p);
            _ = _app.Commands.SendToClick(wx, wy, hit != 0 ? _app.Data.Get(hit) : null);
        }
        else if (!_moved && e.InitialPressMouseButton == MouseButton.Left && !_second) // a double-click selected the group on its second press
        {
            int hit = _vm.HitTest((float)p.X, (float)p.Y);
            if (hit != 0)
            {
                if (shift) _app.ToggleSelect(hit);
                else _app.SelectOne(hit);
            }
            else if (!shift) _app.ClearSelection();
        }
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndDrag()
    {
        _down = _moved = _boxing = _panning = false;
        _box.IsVisible = false;
        _plot.Cursor = new Cursor(StandardCursorType.Cross);
    }

    // ------------------------------------------------------------ order gestures and their preview

    private (double X, double Y) World(Point p)
    {
        var (x, y) = _vm.ToWorld((float)p.X, (float)p.Y);
        return (x, y);
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void EndOrderGesture()
    {
        _rdown = _rmoved = _tdrag = false;
        _route.Clear();
        _preview.IsVisible = false;
    }

    /// <summary>Esc while drawing: drops the route or the racetrack (and the racetrack mode with it).</summary>
    private bool CancelGesture()
    {
        if (!_rdown && !_tdrag) return false;
        bool track = _tdrag;
        EndOrderGesture();
        if (track) _app.Commands.CancelPending();
        return true;
    }

    private void ShowRoutePreview(Point cursor)
    {
        var pts = new List<Point>(_route.Count + 1);
        foreach (var (x, y) in _route) pts.Add(Screen(x, y));
        pts.Add(cursor);
        _preview.Points = pts;
        _preview.IsVisible = true;
    }

    /// <summary>The mockup's trackPts: both legs and the two half circles, 3 km either side of A-B.</summary>
    private void ShowTrackPreview()
    {
        var (a, b) = (_ta, _tb);
        double ux = b.X - a.X, uy = b.Y - a.Y, len = Math.Sqrt(ux * ux + uy * uy);
        if (len < CommandService.RacetrackMinLegKm) { b = (a.X + CommandService.RacetrackClickLegKm, a.Y); ux = b.X - a.X; uy = 0; len = ux; }
        double r = CommandService.RacetrackWidthKm / 2, nx = -uy / len, ny = ux / len, an = Math.Atan2(ny, nx);
        var pts = new List<Point>(46);
        for (int k = 0; k <= 10; k++) { double t = k / 10.0; pts.Add(Screen(a.X + nx * r + ux * t, a.Y + ny * r + uy * t)); }
        for (int k = 1; k < 12; k++) { double th = an - Math.PI * k / 12; pts.Add(Screen(b.X + Math.Cos(th) * r, b.Y + Math.Sin(th) * r)); }
        for (int k = 0; k <= 10; k++) { double t = k / 10.0; pts.Add(Screen(b.X - nx * r - ux * t, b.Y - ny * r - uy * t)); }
        for (int k = 1; k < 12; k++) { double th = an + Math.PI - Math.PI * k / 12; pts.Add(Screen(a.X + Math.Cos(th) * r, a.Y + Math.Sin(th) * r)); }
        pts.Add(pts[0]);
        _preview.Points = pts;
        _preview.IsVisible = true;
    }

    private Point Screen(double x, double y)
    {
        var (sx, sy) = _vm.ToScreen((float)x, (float)y);
        return new Point(sx, sy);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var p = e.GetPosition(_plot);
        float k = (float)Math.Exp(Math.Clamp(e.Delta.Y, -6, 6) * 0.18);
        _vm.ZoomAt((float)p.X, (float)p.Y, k);
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var p = e.GetPosition(_plot);
        int hit = _vm.HitTest((float)p.X, (float)p.Y);
        if (hit == 0 || _app.Data.Get(hit) is not { } t) return;
        _app.SelectGroupOf(t);
        e.Handled = true;
    }

    // ------------------------------------------------------------ tooltip

    private void ShowTip(int id, Point p)
    {
        var d = _app.Data;
        var t = d.Get(id);
        if (t is null) { _tip.IsVisible = false; return; }
        double now = d.ViewTime;
        var (x, y) = d.PositionOf(t, now);
        var (rx, ry) = d.ReferencePoint(now);
        var refName = d.Get(d.ReferenceTrackId)?.Name ?? "origin";
        var aff = t.AffiliationFor(_vm.View);
        string affText = aff switch { Affiliation.Friend => "friend", Affiliation.Hostile => "hostile", Affiliation.Neutral => "neutral", _ => "unknown" };
        var (alt, hdg, spd) = Kinematics(t, now);
        var sb = new System.Text.StringBuilder();
        sb.Append(t.ClassName).Append(" · ").Append(affText).Append('\n');
        double rng = Math.Sqrt((x - rx) * (x - rx) + (y - ry) * (y - ry));
        sb.Append("BRG ").Append(MapData.Pad3(MapData.Bearing(rx, ry, x, y))).Append("  RNG ").Append(rng.ToString("0.0", CultureInfo.InvariantCulture)).Append(" km  from ").Append(refName).Append('\n');
        sb.Append("HDG ").Append(MapData.Pad3(hdg)).Append("  SPD ").Append(Math.Round(spd).ToString(CultureInfo.InvariantCulture)).Append(" m/s  ")
          .Append(t.Domain == TrackDomain.Surface ? "surface" : "ALT " + MapData.AltText(t, alt));
        if (t.IsMissile && t.Details.TargetName is { } tn)
            sb.Append('\n').Append("Target ").Append(tn).Append(t.Details.TimeToGo is double ttg ? $"  TTG {Math.Round(ttg).ToString(CultureInfo.InvariantCulture)} s" : "");
        if (!t.Alive) sb.Append("\nLost");
        _tipName.Text = string.IsNullOrEmpty(t.Name) ? t.ClassName : t.Name;
        _tipBody.Text = sb.ToString();
        _tip.IsVisible = true;
        _tip.Measure(Size.Infinity);
        double w = _tip.DesiredSize.Width, h = _tip.DesiredSize.Height;
        double left = Math.Min(p.X + 14, Bounds.Width - w - 8), top = Math.Min(p.Y + 14, Bounds.Height - h - 8);
        _tip.Margin = new Thickness(Math.Max(4, left), Math.Max(4, top), 0, 0);
    }

    private (float Alt, float Hdg, float Spd) Kinematics(TrackInfo t, double now)
    {
        var d = _app.Data;
        if (d.InReplay && t.History.TrySample(now, out _, out _, out var a, out var h, out var s)) return (a, h, s);
        return (t.AltitudeM, t.HeadingDeg, t.SpeedMps);
    }

    // ------------------------------------------------------------ context menu

    private void ShowContextMenu(Point p)
    {
        int hit = _vm.HitTest((float)p.X, (float)p.Y);
        var (wx, wy) = _vm.ToWorld((float)p.X, (float)p.Y);
        var t = hit != 0 ? _app.Data.Get(hit) : null;
        var items = new List<Control>();
        MenuItem Item(string header, Action? act, bool enabled = true, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled && act is not null };
            if (gesture is not null) mi.InputGesture = KeyGesture.Parse(gesture);
            if (act is not null) mi.Click += (_, _) => act();
            return mi;
        }
        if (t is not null)
        {
            items.Add(Item($"Select {t.Name}", () => _app.SelectOne(t.Id)));
            items.Add(Item($"Select group{(string.IsNullOrEmpty(_app.Data.GroupOf(t)) ? "" : " " + _app.Data.GroupOf(t))}", () => _app.SelectGroupOf(t)));
            items.Add(Item($"Center on {t.Name}", () => _app.CenterOn(t.Id)));
        }
        items.Add(Item("Center here", () => _vm.CenterOn(wx, wy)));
        items.Add(Item("Fit selection", () => _vm.FitSelection(), gesture: "Shift+F"));
        items.Add(Item("Fit all", () => _vm.FitAll(), gesture: "F"));
        items.Add(new Separator());
        // the orders, as the plain right-click and the keys give them
        var cmd = _app.Commands;
        string sel = _app.Selection.Count > 1 ? $"{_app.Selection.Count} units" : "selection";
        items.Add(Item($"Send {sel} here", () => cmd.MoveTo(wx, wy)));
        if (t is { Side: Side.Red }) items.Add(Item($"Attack {t.Name}", () => cmd.Attack(t, wx, wy)));
        if (t is { Side: Side.Blue, IsMissile: false }) items.Add(Item($"Protect {t.Name}", () => cmd.Protect(t)));
        items.Add(Item("Racetrack…", () => cmd.ToggleRacetrack(fromKey: false), gesture: "T"));
        items.Add(Item("Attack nearest", () => cmd.AttackNearest(), gesture: "A"));
        items.Add(Item("RTB & land", () => cmd.Rtb(), gesture: "R"));
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(_plot);
    }
}
