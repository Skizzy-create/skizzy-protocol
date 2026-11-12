using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Live;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Panels;

/// <summary>
/// The Engagements tab (M2): per-team missile totals with their KSP funds (fired, hits, missed, lost to intercepts,
/// intercepts made, kills; spent, value intercepted, value of hits), kind chips with counts (toggle each), and the
/// running engagement log newest first. Clicking a line pans the active map to the missile's last position (the
/// victim's for a kill); in replay it also scrubs to the line's time. The data is the host's ledger, read from the
/// source.tactical stream (tac.engagement_log answers the same query over MCP).
/// </summary>
public sealed class EngagementsView : UserControl
{
    private readonly AppState _app;
    private readonly WrapPanel _chips = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly Grid _totals = new() { ColumnSpacing = 10, RowSpacing = 3, Margin = new Thickness(10, 6, 10, 6) };
    private readonly TextBlock _note = Ui.Text("", "note");
    private readonly ObservableCollection<EngagementEntry> _items = new();
    private readonly ListBox _list;
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);
    private long _version = -1;
    private long _shownSeq;
    private const int MaxShown = 1000;

    /// <summary>Entry kinds in chip order, with the chip titles.</summary>
    public static readonly (string Kind, string Title)[] Kinds =
        [("fired", "Fired"), ("hit", "Hits"), ("intercepted", "Intercepted"), ("missed", "Missed"), ("destroyed", "Destroyed"), ("killed", "Kills")];

    /// <summary>Totals columns: header, tooltip.</summary>
    public static readonly (string Head, string Tip)[] Columns =
    [
        ("Team", "BDArmory team"), ("Fired", "Missiles fired"), ("Air", "In flight now"), ("Hits", "Missiles that hit"), ("Missed", "Missiles that missed"),
        ("Int'd", "Own missiles intercepted by the enemy"), ("Int made", "Enemy missiles this team's interceptors killed"), ("Kills", "Vessels killed by this team's missiles"),
        ("Spent", "Funds fired (KSP part costs, summed over each missile's parts)"), ("Lost to int", "Funds of own missiles intercepted"), ("Hit value", "Funds of own missiles that hit"),
    ];

    public EngagementsView(AppState app)
    {
        _app = app;
        _list = new ListBox
        {
            ItemsSource = _items,
            ItemTemplate = new FuncDataTemplate<EngagementEntry>((_, _) => new EngagementRow(app), supportsRecycling: true),
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
        };
        _list.Classes.Add("log");
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is EngagementEntry e) Jump(e);
            _list.SelectedItem = null;
        };
        _note.Margin = new Thickness(10, 4, 10, 6);
        _note.TextWrapping = TextWrapping.Wrap;
        var chipsBox = new Border { Child = _chips };
        chipsBox.Classes.Add("chips");
        var totalsBox = new Border { Child = new ScrollViewer { Content = _totals, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, BorderThickness = new Thickness(0, 0, 0, 1) };
        totalsBox.Bind(Border.BorderBrushProperty, totalsBox.GetResourceObservable("LineBrush"));
        var top = Ui.V(0, chipsBox, totalsBox, _note);
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_list);
        Content = root;
        AttachedToVisualTree += (_, _) => { _app.SlowTick += Refresh; _app.SourceChanged += Rebuild; Rebuild(); };
        DetachedFromVisualTree += (_, _) => { _app.SlowTick -= Refresh; _app.SourceChanged -= Rebuild; };
    }

    internal IReadOnlyList<EngagementEntry> Shown => _items;
    internal Grid TotalsGrid => _totals;
    internal TextBlock Note => _note;

    /// <summary>The map side a team is drawn as: the live source's assignment, else the order of the totals.</summary>
    internal Side SideOf(string? team)
    {
        if (team is null) return Side.Other;
        if (_app.Source is HostDataSource h) return h.SideOfTeam(team);
        var t = _app.Data.Engagements.Totals;
        for (int i = 0; i < t.Count; i++)
            if (t[i].Team == team) return i switch { 0 => Side.Blue, 1 => Side.Red, 2 => Side.Green, _ => Side.Other };
        return Side.Other;
    }

    internal static string BrushOf(Side s) => s switch { Side.Blue => "FriendBrush", Side.Red => "HostileBrush", Side.Green => "NeutralBrush", _ => "UnknownBrush" };

    private void Jump(EngagementEntry e)
    {
        if (_app.Data.InReplay) _app.ScrubTo(e.T);
        if (e.Missile is null || _app.ActiveMap is not { } m) return;
        var tr = _app.Data.GetByKey("vessel:" + e.Missile);
        if (tr is null) return;
        var (x, y) = _app.Data.PositionOf(tr, Math.Min(_app.Data.ViewTime, tr.DiedT));
        m.SetView((float)x, (float)y, Math.Max(m.Scale, 8));
        if (tr.Alive) _app.SelectOne(tr.Id);
    }

    private void Rebuild()
    {
        _items.Clear();
        var book = _app.Data.Engagements;
        var e = book.Entries;
        for (int i = e.Count - 1; i >= 0 && _items.Count < MaxShown; i--)
            if (!_off.Contains(e[i].Kind)) _items.Add(e[i]);
        _shownSeq = e.Count > 0 ? e[^1].Seq : 0;
        _version = book.Version;
        BuildChips();
        BuildTotals();
        UpdateNote();
    }

    public void Refresh()
    {
        var book = _app.Data.Engagements;
        if (book.Version == _version) return;
        _version = book.Version;
        var e = book.Entries;
        // cleared (a new subscription replays the log) or trimmed past what we showed: start over
        if (e.Count == 0 || e[^1].Seq < _shownSeq || (e[0].Seq > _shownSeq + 1 && _shownSeq != 0)) { Rebuild(); return; }
        int start = e.Count;
        while (start > 0 && e[start - 1].Seq > _shownSeq) start--;
        for (int i = start; i < e.Count; i++)
            if (!_off.Contains(e[i].Kind)) _items.Insert(0, e[i]);
        while (_items.Count > MaxShown) _items.RemoveAt(_items.Count - 1);
        _shownSeq = e[^1].Seq;
        UpdateChipCounts();
        BuildTotals();
        UpdateNote();
    }

    private void UpdateNote()
    {
        var book = _app.Data.Engagements;
        string? text = book.Entries.Count == 0 && book.Totals.Count == 0
            ? book.Note ?? "No missiles fired yet."
            : null;
        _note.Text = text ?? "";
        _note.IsVisible = text is not null;
    }

    private int CountOf(string kind)
    {
        int n = 0;
        foreach (var e in _app.Data.Engagements.Entries) if (e.Kind == kind) n++;
        return n;
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        foreach (var (kind, title) in Kinds)
        {
            var k = kind;
            var chip = Ui.Chip(title, CountOf(kind).ToString(CultureInfo.InvariantCulture), !_off.Contains(kind), () =>
            {
                if (!_off.Remove(k)) _off.Add(k);
                Rebuild();
            });
            chip.Tag = kind;
            _chips.Children.Add(chip);
        }
    }

    private void UpdateChipCounts()
    {
        foreach (var c in _chips.Children.OfType<ToggleButton>())
            if (c.Tag is string kind && c.Content is StackPanel sp && sp.Children[^1] is TextBlock t)
                t.Text = CountOf(kind).ToString(CultureInfo.InvariantCulture);
    }

    private void BuildTotals()
    {
        _totals.Children.Clear();
        _totals.RowDefinitions.Clear();
        _totals.ColumnDefinitions.Clear();
        var totals = _app.Data.Engagements.Totals;
        if (totals.Count == 0) return;
        foreach (var _ in Columns) _totals.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _totals.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < Columns.Length; c++)
        {
            var h = Ui.Text(Columns[c].Head.ToUpperInvariant(), "lbl");
            ToolTip.SetTip(h, Columns[c].Tip);
            if (c > 0) h.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(h, c);
            _totals.Children.Add(h);
        }
        int row = 1;
        foreach (var t in totals)
        {
            _totals.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            string[] cells =
            [
                t.Team, N(t.Fired), N(t.InFlight), N(t.Hits), N(t.Misses), N(t.Intercepted), N(t.InterceptsMade), N(t.Kills),
                EngagementBook.Funds(t.Spent), EngagementBook.Funds(t.InterceptedCost), EngagementBook.Funds(t.HitCost),
            ];
            for (int c = 0; c < cells.Length; c++)
            {
                var tb = Ui.Text(cells[c], "data");
                if (c == 0) { Ui.BindBrush(tb, BrushOf(SideOf(t.Team))); tb.FontWeight = FontWeight.SemiBold; }
                else tb.HorizontalAlignment = HorizontalAlignment.Right;
                if (c >= 8 && t.CostUnknown > 0) ToolTip.SetTip(tb, $"{t.CostUnknown} missile(s) of unknown cost are not in this sum.");
                Grid.SetRow(tb, row); Grid.SetColumn(tb, c);
                _totals.Children.Add(tb);
            }
            row++;
        }
    }
}

/// <summary>One engagement line: time, kind (coloured small caps), text (wraps), funds.</summary>
public sealed class EngagementRow : Grid
{
    private readonly AppState _app;
    private readonly TextBlock _t = new() { FontSize = 10.5 };
    private readonly TextBlock _k = new() { FontSize = 9.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.14, Margin = new Thickness(0, 1, 0, 0) };
    private readonly TextBlock _m = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _c = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right };

    public EngagementRow(AppState app)
    {
        _app = app;
        ColumnDefinitions = new ColumnDefinitions("66,84,*,Auto");
        Background = Brushes.Transparent; // the whole line takes the click, not only its text
        ColumnSpacing = 8;
        Ui.BindFont(_t, "FontData");
        Ui.BindBrush(_t, "TextFaintBrush");
        Ui.BindFont(_k, "FontDisplay");
        Ui.BindFont(_c, "FontData");
        Ui.BindBrush(_c, "TextDimBrush");
        Children.Add(_t);
        SetColumn(_k, 1); Children.Add(_k);
        SetColumn(_m, 2); Children.Add(_m);
        SetColumn(_c, 3); Children.Add(_c);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not EngagementEntry en) return;
        _t.Text = _app.Data.ClockText(en.T);
        _k.Text = en.Kind.ToUpperInvariant();
        Ui.BindBrush(_k, en.Kind switch
        {
            "fired" => "FriendBrush",
            "intercepted" => "OkBrush",
            "hit" or "killed" => "CritBrush",
            "missed" => "TextDimBrush",
            _ => "WarnBrush",
        });
        _m.Text = en.Text;
        _c.Text = double.IsNaN(en.Cost) ? "" : EngagementBook.Funds(en.Cost);
        ToolTip.SetTip(this, en.Source is null ? null : "source: " + en.Source);
    }
}
