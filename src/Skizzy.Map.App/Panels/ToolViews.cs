using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Skizzy.Map.App.Map;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Panels;

/// <summary>Layers: every plot layer of the active map window, its picture (fog/god) and symbol sizing.</summary>
public sealed class LayersView : UserControl
{
    private readonly AppState _app;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(12, 10) };
    private MapViewModel? _bound;
    private bool _syncing;
    private readonly Dictionary<string, ToggleButton> _t = new();
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private Segmented _view = null!;
    private Segmented _size = null!;
    private Slider _scale = null!;
    private ToggleButton _legend = null!;

    public LayersView(AppState app)
    {
        _app = app;
        Ui.BindFont(_title, "FontDisplay");
        Build();
        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AttachedToVisualTree += (_, _) => { _app.MapsChanged += Rebind; Rebind(); };
        DetachedFromVisualTree += (_, _) => { _app.MapsChanged -= Rebind; if (_bound is not null) _bound.Changed -= Sync; _bound = null; };
    }

    private void Build()
    {
        _root.Children.Add(Ui.V(2, Ui.Label("Active map window"), _title));
        _view = Ui.Seg(["Blue fog", "Red fog", "God"], 0, i => { if (_bound is not null) _bound.View = (ViewSide)i; }, uiFont: true);
        _view.HorizontalAlignment = HorizontalAlignment.Left;
        _root.Children.Add(Ui.V(6, Ui.Label("Picture"), _view,
            Ui.Text("Blue fog shows what Blue's sensors share; Red fog what Red holds; God shows the truth, stealth included.", "note")));

        ToggleButton T(string key, string text)
        {
            var b = Ui.Toggle(text, false, on => Set(key, on), "tg");
            _t[key] = b;
            return b;
        }
        _root.Children.Add(Ui.V(6, Ui.Label("Tracks"), Ui.Wrap(6, 6, T("trails", "Trails"), T("stalks", "Altitude"), T("labels", "Labels"), T("lines", "Target lines"), T("counts", "Counts"))));
        _root.Children.Add(Ui.V(6, Ui.Label("Overlays"), Ui.Wrap(6, 6, T("rings", "Range rings"), T("zones", "Zones"), T("events", "Events"), T("orders", "Orders"), T("grid", "Grid"), T("scale", "Scale bar"))));
        _legend = Ui.Toggle("Legend", true, on => { if (_bound is not null && !_syncing) { _bound.ShowLegend = on; _bound.NotifyChanged(); } }, "tg");
        _root.Children.Add(Ui.V(6, Ui.Label("Map furniture"), Ui.Wrap(6, 6, _legend)));

        _size = Ui.Seg(["Auto", "Radar", "Zoom", "Fixed"], 0, i => { if (_bound is not null) { _bound.Options.SizeMode = (SizeMode)i; _bound.NotifyChanged(); } }, uiFont: true);
        _size.HorizontalAlignment = HorizontalAlignment.Left;
        _scale = new Slider { Minimum = .6, Maximum = 1.8, Value = 1, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        _scale.Classes.Add("acc");
        _scale.ValueChanged += (_, e) => { if (_bound is not null && !_syncing) { _bound.Options.SizeScale = (float)e.NewValue; _bound.NotifyChanged(); } };
        _root.Children.Add(Ui.V(6, Ui.Label("Symbols"), _size, _scale,
            Ui.Text("Auto: radar cross-section sets the size, zoom scales it, neighbours never overlap. Selected symbols draw 1.2× larger; [ and ] resize the selection.", "note")));

        _root.Children.Add(Ui.V(6, Ui.Label("Windows"), Ui.Wrap(6, 6,
            Ui.Button("New map window", () => NewMapRequested?.Invoke(), "acc"),
            Ui.Button("Fit all", () => _bound?.FitAll(), "ghost"),
            Ui.Button("Fit filter", () => _bound?.FitFilter(), "ghost"))));
    }

    /// <summary>Raised by "New map window" (the shell adds a document).</summary>
    public static event Action? NewMapRequested;

    private void Rebind()
    {
        if (_bound == _app.ActiveMap) { Sync(); return; }
        if (_bound is not null) _bound.Changed -= Sync;
        _bound = _app.ActiveMap;
        if (_bound is not null) _bound.Changed += Sync;
        Sync();
    }

    private void Set(string key, bool on)
    {
        var m = _bound;
        if (m is null || _syncing) return;
        var o = m.Options;
        switch (key)
        {
            case "trails": o.Trails = on; break;
            case "stalks": o.AltitudeStalks = on; break;
            case "labels": o.Labels = on; break;
            case "lines": o.TargetLines = on; break;
            case "counts": o.Counts = on; break;
            case "rings": o.Rings = on; break;
            case "zones": o.Zones = on; break;
            case "events": o.Events = on; break;
            case "orders": o.Orders = on; break;
            case "grid": o.Grid = on; break;
            case "scale": o.ScaleBar = on; break;
        }
        m.NotifyChanged();
    }

    private void Sync()
    {
        var m = _bound;
        IsEnabled = m is not null;
        if (m is null) { _title.Text = "No map window"; return; }
        _syncing = true;
        try
        {
            var o = m.Options;
            _title.Text = m.Title;
            _t["trails"].IsChecked = o.Trails; _t["stalks"].IsChecked = o.AltitudeStalks; _t["labels"].IsChecked = o.Labels;
            _t["lines"].IsChecked = o.TargetLines; _t["counts"].IsChecked = o.Counts; _t["rings"].IsChecked = o.Rings;
            _t["zones"].IsChecked = o.Zones; _t["events"].IsChecked = o.Events; _t["orders"].IsChecked = o.Orders;
            _t["grid"].IsChecked = o.Grid; _t["scale"].IsChecked = o.ScaleBar;
            _legend.IsChecked = m.ShowLegend;
            _view.SetSelected((int)m.View);
            _size.SetSelected((int)o.SizeMode);
            _scale.Value = o.SizeScale;
        }
        finally { _syncing = false; }
    }
}

/// <summary>Host health: the data source's connection and rates, plus the active map's render statistics.</summary>
public sealed class HealthView : UserControl
{
    private readonly AppState _app;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(12, 10) };
    private long _lastFrames;
    private double _lastT;
    private double _fps;

    public HealthView(AppState app)
    {
        _app = app;
        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AttachedToVisualTree += (_, _) => { _app.SlowTick += Refresh; Refresh(); };
        DetachedFromVisualTree += (_, _) => _app.SlowTick -= Refresh;
    }

    public void Refresh()
    {
        var h = _app.Source.Health;
        _root.Children.Clear();
        var state = _app.Source.State;
        string cls = state switch { SourceState.Online => "ok", SourceState.Connecting or SourceState.Reconnecting => "warn", SourceState.Failed or SourceState.Offline => "crit", _ => "" };
        var head = Ui.H(8, Ui.Pill(h.State, cls), Ui.Text(h.Source, "dim"));
        _root.Children.Add(Ui.V(6, Ui.Label("Data source"), head, Ui.Text(_app.Source.StatusText, "note")));

        var rows = new List<(string, string)>();
        if (h.Endpoint is not null) rows.Add(("Endpoint", h.Endpoint));
        if (h.HostVersion is not null) rows.Add(("Host version", h.HostVersion));
        if (h.LatencyMs is double l) rows.Add(("Round trip", l.ToString("0.0", CultureInfo.InvariantCulture) + " ms"));
        if (h.UpdateHz is double hz) rows.Add(("Update rate", hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz"));
        rows.Add(("Live tracks", h.Tracks.ToString("#,0", CultureInfo.InvariantCulture)));
        rows.Add(("Messages", h.Messages.ToString("#,0", CultureInfo.InvariantCulture)));
        foreach (var (k, v) in h.Extra) rows.Add((k, v));
        _root.Children.Add(Table(rows));

        if (_app.ActiveMap is { } m)
        {
            double now = _app.Now;
            long frames = m.Surface.FramesDrawn;
            if (_lastT > 0 && now > _lastT) _fps = (frames - _lastFrames) / (now - _lastT);
            _lastFrames = frames; _lastT = now;
            var (st, tm, sprites) = m.Surface.Stats;
            var r = new List<(string, string)>
            {
                ("Frames per second", _fps.ToString("0", CultureInfo.InvariantCulture)),
                ("Plot pipeline", m.Surface.Mode),
                ("Draw time", m.Surface.LastDrawMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms"),
                ("UI frame (source / sync)", $"{_app.LastFrameMs:0.00} ({_app.LastSourceMs:0.00} / {m.LastSyncMs:0.00}) ms"),
                ("Layout / tracks / labels", $"{tm.Layout:0.00} / {tm.Tracks:0.00} / {tm.Labels:0.00} ms"),
                ("Tracks drawn", st.TracksVisible.ToString("#,0", CultureInfo.InvariantCulture)),
                ("Full symbols / dots", $"{st.FullSymbols:#,0} / {st.Dots:#,0}"),
                ("Labels placed / culled", $"{st.LabelsPlaced:#,0} / {st.LabelsCulled:#,0}"),
                ("Sprites cached", sprites.ToString("#,0", CultureInfo.InvariantCulture)),
                ("Scale", m.Scale.ToString("0.00", CultureInfo.InvariantCulture) + " px/km"),
            };
            if (m.Surface.LastError is { } err) r.Add(("Last plot error", err));
            _root.Children.Add(Ui.V(6, Ui.Label("Plot · " + m.Title), Table(r)));
        }
    }

    private static Grid Table(List<(string, string)> rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 4 };
        for (int i = 0; i < rows.Count; i++)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = Ui.Text(rows[i].Item1, "data", "dim");
            var v = Ui.Text(rows[i].Item2, "data");
            v.HorizontalAlignment = HorizontalAlignment.Right;
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
        }
        return g;
    }
}
