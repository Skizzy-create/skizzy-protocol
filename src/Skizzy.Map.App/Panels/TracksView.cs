using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Panels;

/// <summary>Sortable columns of the track table.</summary>
public enum TrackColumn { Track, Type, Brg, Rng, Alt, Spd }

/// <summary>
/// The Tracks panel (the mockup's left panel): group chips with number-key badges, filter chips with counts and
/// a sortable track table (nearest first by default). Click selects, Shift-click adds, double-click centres.
/// </summary>
public sealed class TracksView : UserControl
{
    private readonly AppState _app;
    private readonly WrapPanel _groups = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly WrapPanel _filters = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly TextBlock _count = Ui.Text("", "note");
    private readonly TrackTable _table;
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, AllowAutoHide = true };
    private readonly Grid _header = new();
    private readonly List<TextBlock> _headerText = new();
    private string _groupSig = "";

    private static readonly (TrackFilter F, string Name)[] Filters =
        [(TrackFilter.All, "All"), (TrackFilter.Air, "Aircraft"), (TrackFilter.Missiles, "Missiles"), (TrackFilter.Ships, "Ships & subs"), (TrackFilter.Inbound, "Inbound")];

    public TracksView(AppState app)
    {
        _app = app;
        _table = new TrackTable(app);
        _table.ScrollChanged += () => SyncScroll();
        _scroll.ValueChanged += (_, e) => _table.Offset = e.NewValue;

        var groupsBox = new Border { Child = _groups };
        groupsBox.Classes.Add("chips");
        var filtersBox = new Border { Child = _filters };
        filtersBox.Classes.Add("chips");

        BuildHeader();
        var headerBox = new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBox.Bind(Border.BorderBrushProperty, headerBox.GetResourceObservable("LineBrush"));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        body.Children.Add(_table);
        Grid.SetColumn(_scroll, 1);
        body.Children.Add(_scroll);

        var countRow = new Border { Padding = new Thickness(10, 6, 10, 0), Child = _count };
        var root = new DockPanel();
        DockPanel.SetDock(countRow, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(groupsBox, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(filtersBox, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(headerBox, Avalonia.Controls.Dock.Top);
        root.Children.Add(countRow);
        root.Children.Add(groupsBox);
        root.Children.Add(filtersBox);
        root.Children.Add(headerBox);
        root.Children.Add(body);
        Content = root;

        AttachedToVisualTree += (_, _) =>
        {
            _app.SlowTick += Refresh;
            _app.SelectionChanged += OnSelection;
            _app.GroupsChanged += Refresh;
            _app.FiltersChanged += Refresh;
            _app.SourceChanged += Refresh;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _app.SlowTick -= Refresh;
            _app.SelectionChanged -= OnSelection;
            _app.GroupsChanged -= Refresh;
            _app.FiltersChanged -= Refresh;
            _app.SourceChanged -= Refresh;
        };
    }

    internal TrackTable Table => _table;

    private void OnSelection() => _table.InvalidateVisual();

    private void BuildHeader()
    {
        _header.ColumnDefinitions = new ColumnDefinitions(TrackTable.ColumnSpec);
        string[] names = ["TRACK", "TYPE", "BRG", "RNG KM", "ALT", "M/S"];
        for (int i = 0; i < names.Length; i++)
        {
            var col = (TrackColumn)i;
            var t = Ui.Label(names[i]);
            t.HorizontalAlignment = i >= 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            t.LetterSpacing = 1.33;
            var cell = new Border { Padding = new Thickness(6, 7), Background = Brushes.Transparent, Child = t, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(cell, "Sort by " + names[i].ToLowerInvariant());
            cell.PointerPressed += (_, e) => { _table.SortBy(col); UpdateHeader(); e.Handled = true; };
            Grid.SetColumn(cell, i);
            _header.Children.Add(cell);
            _headerText.Add(t);
        }
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        string[] names = ["TRACK", "TYPE", "BRG", "RNG KM", "ALT", "M/S"];
        for (int i = 0; i < _headerText.Count; i++)
        {
            bool on = (int)_table.Sort == i;
            _headerText[i].Text = names[i] + (on ? (_table.Descending ? " ▾" : " ▴") : "");
            _headerText[i].Classes.Set("sorted", on);
            if (on) Ui.BindBrush(_headerText[i], "TextDimBrush"); else Ui.BindBrush(_headerText[i], "TextFaintBrush");
        }
    }

    private void SyncScroll()
    {
        _scroll.Maximum = Math.Max(0, _table.Extent - _table.Viewport);
        _scroll.ViewportSize = _table.Viewport;
        _scroll.Value = _table.Offset;
        _scroll.IsVisible = _table.Extent > _table.Viewport + 1;
    }

    public void Refresh()
    {
        var data = _app.Data;
        var view = _app.ActiveMap?.View ?? ViewSide.Blue;
        _table.Rebuild(view);
        SyncScroll();
        _count.Text = $"{_table.LiveCount.ToString("#,0", CultureInfo.InvariantCulture)} live" +
                      (_app.Data.InReplay ? " · replay " + MapData.ElapsedText(data.ViewTime) : "");

        // filter chips
        var counts = new int[Filters.Length];
        foreach (var t in data.Tracks)
        {
            if (!TrackTable.Listed(data, t, view)) continue;
            for (int i = 0; i < Filters.Length; i++) if (AppState.Passes(Filters[i].F, t)) counts[i]++;
        }
        if (_filters.Children.Count != Filters.Length)
        {
            _filters.Children.Clear();
            foreach (var (f, name) in Filters)
            {
                var ff = f;
                _filters.Children.Add(Ui.Chip(name, "0", false, () => _app.SetTrackFilter(ff)));
            }
        }
        for (int i = 0; i < Filters.Length; i++)
        {
            var chip = (ToggleButton)_filters.Children[i];
            chip.IsChecked = _app.TrackListFilter == Filters[i].F;
            if (chip.Content is StackPanel sp && sp.Children[^1] is TextBlock c) c.Text = counts[i].ToString(CultureInfo.InvariantCulture);
        }

        // group chips: rebuilt only when the groups change
        var groups = _app.OwnGroups();
        int air = groups.Sum(g => g.Value.Count(t => t.Domain == TrackDomain.Air));
        var sig = string.Join("|", groups.Select(g => g.Key + ":" + g.Value.Count)) + "#" + air + "#" + string.Join(",", _app.Slots.Select(s => s.Key + s.Value));
        if (sig == _groupSig) return;
        _groupSig = sig;
        _groups.Children.Clear();
        var lbl = Ui.Label("Select group");
        lbl.Margin = new Thickness(0, 0, 4, 0);
        _groups.Children.Add(lbl);
        foreach (var (name, list) in groups)
        {
            var key = _app.Slots.FirstOrDefault(s => s.Value == name).Key;
            var n = name;
            var chip = Ui.Chip(string.IsNullOrEmpty(name) ? "Ungrouped" : name, list.Count.ToString(CultureInfo.InvariantCulture), false, () => { _app.SelectGroup(n); _groupSig = ""; Refresh(); }, key);
            _groups.Children.Add(chip);
        }
        if (air > 0)
            _groups.Children.Add(Ui.Chip("All aircraft", air.ToString(CultureInfo.InvariantCulture), false, () => { _app.SelectGroup(AppState.AllAircraft); _groupSig = ""; Refresh(); }));
    }
}

/// <summary>
/// The track table body, drawn directly (one FormattedText per visible cell): a thousand rows that change every
/// refresh would churn a virtualised ItemsControl. Rows are 21 px in IBM Plex Mono 10.5 like the mockup's table.
/// </summary>
public sealed class TrackTable : ThemedControl
{
    public const string ColumnSpec = "*,44,40,58,52,44";
    private static readonly double[] Fixed = [44, 40, 58, 52, 44];
    public const double RowH = 21;

    private readonly AppState _app;
    private readonly List<Row> _rows = new();
    private int _hover = -1;
    private Typeface _face;
    private bool _faceSet;

    public TrackTable(AppState app)
    {
        _app = app;
        ClipToBounds = true;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public TrackColumn Sort { get; private set; } = TrackColumn.Rng;
    public bool Descending { get; private set; }
    public double Offset { get => _offset; set { _offset = Math.Clamp(value, 0, Math.Max(0, Extent - Viewport)); InvalidateVisual(); } }
    private double _offset;
    public double Extent => _rows.Count * RowH;
    public double Viewport => Bounds.Height;
    public int LiveCount { get; private set; }
    public IReadOnlyList<Row> Rows => _rows;

    public event Action? ScrollChanged;

    public sealed class Row
    {
        public int Id;
        public string Name = "", Code = "", Cls = "", Alt = "";
        public double Brg, Rng, Spd;
        public Affiliation Aff;
        public bool Alive;
    }

    /// <summary>The mockup's buildList: live (or existing at the replay time), visible to the view's picture.</summary>
    public static bool Listed(MapData d, TrackInfo t, ViewSide view) =>
        (d.InReplay ? t.ExistsAt(d.ViewTime) : t.Alive) && t.DetectionFor(view) != TrackDetection.None;

    public void SortBy(TrackColumn c)
    {
        if (Sort == c) Descending = !Descending;
        else { Sort = c; Descending = c is TrackColumn.Spd or TrackColumn.Alt; }
        SortRows();
        InvalidateVisual();
    }

    public void Rebuild(ViewSide view)
    {
        var d = _app.Data;
        double now = d.ViewTime;
        var (rx, ry) = d.ReferencePoint(now);
        _rows.Clear();
        int live = 0;
        foreach (var t in d.Tracks)
        {
            if (!Listed(d, t, view)) continue;
            live++;
            if (!AppState.Passes(_app.TrackListFilter, t)) continue;
            var (x, y) = d.PositionOf(t, now);
            float alt = t.AltitudeM, spd = t.SpeedMps;
            if (d.InReplay && t.History.TrySample(now, out _, out _, out var a2, out _, out var s2)) { alt = a2; spd = s2; }
            _rows.Add(new Row
            {
                Id = t.Id, Name = t.Name, Code = t.Code, Cls = t.ClassName,
                Brg = MapData.Bearing(rx, ry, x, y), Rng = Math.Sqrt((x - rx) * (x - rx) + (y - ry) * (y - ry)),
                Alt = MapData.AltText(t, alt), Spd = spd, Aff = t.AffiliationFor(view), Alive = t.Alive,
            });
        }
        LiveCount = live;
        SortRows();
        _offset = Math.Clamp(_offset, 0, Math.Max(0, Extent - Viewport));
        InvalidateVisual();
    }

    private void SortRows()
    {
        Comparison<Row> cmp = Sort switch
        {
            TrackColumn.Track => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            TrackColumn.Type => (a, b) => string.CompareOrdinal(a.Code, b.Code) is var c && c != 0 ? c : a.Rng.CompareTo(b.Rng),
            TrackColumn.Brg => (a, b) => a.Brg.CompareTo(b.Brg),
            TrackColumn.Alt => (a, b) => AltKey(a).CompareTo(AltKey(b)),
            TrackColumn.Spd => (a, b) => a.Spd.CompareTo(b.Spd),
            _ => (a, b) => a.Rng.CompareTo(b.Rng),
        };
        _rows.Sort(Descending ? (a, b) => cmp(b, a) : cmp);
    }

    private static double AltKey(Row r)
    {
        var s = r.Alt;
        if (s.Length == 0) return 0;
        if (s.EndsWith('k')) return double.Parse(s[..^1], CultureInfo.InvariantCulture) * 1000;
        var num = new string(s.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
        double v = num.Length > 0 ? double.Parse(num, CultureInfo.InvariantCulture) : 0;
        return s.StartsWith('−') ? -v : v;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Offset -= e.Delta.Y * RowH * 3;
        ScrollChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        int i = RowAt(e.GetPosition(this).Y);
        if (i != _hover) { _hover = i; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        int i = RowAt(e.GetPosition(this).Y);
        if (i < 0 || i >= _rows.Count) return;
        var id = _rows[i].Id;
        if (e.ClickCount >= 2) { _app.CenterOn(id); e.Handled = true; return; }
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) _app.ToggleSelect(id);
        else _app.SelectOne(id);
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, Extent - Viewport));
        ScrollChanged?.Invoke();
    }

    private int RowAt(double y) => (int)Math.Floor((y + _offset) / RowH);

    public override void Render(DrawingContext ctx)
    {
        if (!_faceSet)
        {
            var ff = this.TryFindResource("FontData", ActualThemeVariant, out var f) && f is FontFamily fam ? fam : FontFamily.Default;
            _face = new Typeface(ff);
            _faceSet = true;
        }
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        var text = Ui.Brush(this, "TextBrush");
        var faint = Ui.Brush(this, "TextFaintBrush");
        var sel = Ui.Brush(this, "RowSelectedBrush");
        var hover = Ui.Brush(this, "Ink2Brush");
        var line = new Pen(Ui.Brush(this, "LineSoftBrush"), 1);
        double nameW = Math.Max(60, w - Fixed.Sum());
        double[] x = new double[7];
        x[0] = 0; x[1] = nameW;
        for (int i = 0; i < Fixed.Length; i++) x[i + 2] = x[i + 1] + Fixed[i];
        int first = Math.Max(0, (int)(_offset / RowH));
        int last = Math.Min(_rows.Count - 1, (int)((_offset + h) / RowH) + 1);
        var selection = _app.Selection;
        for (int i = first; i <= last; i++)
        {
            var r = _rows[i];
            double y = i * RowH - _offset;
            if (selection.Contains(r.Id)) ctx.FillRectangle(sel, new Rect(0, y, w, RowH));
            else if (i == _hover) ctx.FillRectangle(hover, new Rect(0, y, w, RowH));
            ctx.DrawLine(line, new Point(0, y + RowH - .5), new Point(w, y + RowH - .5));
            var brush = r.Alive ? text : faint;
            Swatch(ctx, r.Aff, r.Alive, 6 + 4, y + RowH / 2);
            Cell(ctx, r.Name, x[0] + 6 + 14, y, x[1] - x[0] - 6 - 14 - 4, brush, false);
            Cell(ctx, r.Code, x[1] + 6, y, Fixed[0] - 8, brush, false);
            Cell(ctx, MapData.Pad3(r.Brg), x[3] - 6, y, Fixed[1] - 8, brush, true);
            Cell(ctx, r.Rng.ToString("0.0", CultureInfo.InvariantCulture), x[4] - 6, y, Fixed[2] - 8, brush, true);
            Cell(ctx, r.Alt.Length == 0 ? "—" : r.Alt, x[5] - 6, y, Fixed[3] - 8, brush, true);
            Cell(ctx, Math.Round(r.Spd).ToString("0", CultureInfo.InvariantCulture), x[6] - 6, y, Fixed[4] - 8, brush, true);
        }
        if (_rows.Count == 0)
        {
            var ft = Format("No tracks match this filter.", faint);
            ctx.DrawText(ft, new Point(10, 10));
        }
    }

    private FormattedText Format(string s, IBrush b) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10.5, b);

    private void Cell(DrawingContext ctx, string s, double x, double y, double maxW, IBrush b, bool right)
    {
        var ft = Format(s, b);
        ft.MaxTextWidth = Math.Max(1, maxW);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        double tx = right ? x - Math.Min(ft.Width, maxW) : x;
        ctx.DrawText(ft, new Point(tx, y + (RowH - ft.Height) / 2));
    }

    private void Swatch(DrawingContext ctx, Affiliation a, bool alive, double cx, double cy)
    {
        IBrush b = alive ? Ui.Brush(this, Glyphs.BrushKey(a)) : Ui.Brush(this, "TextFaintBrush");
        switch (a)
        {
            case Affiliation.Friend:
                ctx.DrawEllipse(b, null, new Point(cx, cy), 4, 4);
                break;
            case Affiliation.Hostile:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    double r = 4 * .85 * 1.414;
                    c.BeginFigure(new Point(cx, cy - r), true);
                    c.LineTo(new Point(cx + r, cy)); c.LineTo(new Point(cx, cy + r)); c.LineTo(new Point(cx - r, cy));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(b, null, g);
                break;
            }
            default:
                ctx.DrawRectangle(b, null, new Rect(cx - 4, cy - 4, 8, 8), 2, 2);
                break;
        }
    }
}
