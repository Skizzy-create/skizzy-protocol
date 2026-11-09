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
using Skizzy.Map.Data.Demo;

namespace Skizzy.Map.App.Panels;

/// <summary>
/// The Messages tab: category chips with counts (toggle each), newest first. Clicking a line with a position pans
/// the active map there (zooming in to at least 8 px/km); in replay it also scrubs to the line's time.
/// </summary>
public sealed class MessagesView : UserControl
{
    private readonly AppState _app;
    private readonly WrapPanel _chips = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly ObservableCollection<LogEntry> _items = new();
    private readonly ListBox _list;
    private long _version = -1;
    private long _lastSeq;
    private const int MaxShown = 1000;

    private static readonly LogCategory[] Cats =
        [LogCategory.Contact, LogCategory.Weapons, LogCategory.Intercept, LogCategory.Damage, LogCategory.Orders, LogCategory.Comms, LogCategory.System];

    public MessagesView(AppState app)
    {
        _app = app;
        _list = new ListBox
        {
            ItemsSource = _items,
            ItemTemplate = new FuncDataTemplate<LogEntry>((_, _) => new LogRow(app), supportsRecycling: true),
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
        };
        _list.Classes.Add("log");
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is LogEntry e) Jump(e);
            _list.SelectedItem = null;
        };
        var chipsBox = new Border { Child = _chips };
        chipsBox.Classes.Add("chips");
        var root = new DockPanel();
        DockPanel.SetDock(chipsBox, Avalonia.Controls.Dock.Top);
        root.Children.Add(chipsBox);
        root.Children.Add(_list);
        Content = root;
        AttachedToVisualTree += (_, _) => { _app.SlowTick += Refresh; _app.SourceChanged += Rebuild; Rebuild(); };
        DetachedFromVisualTree += (_, _) => { _app.SlowTick -= Refresh; _app.SourceChanged -= Rebuild; };
    }

    internal IReadOnlyList<LogEntry> Shown => _items;

    private void Jump(LogEntry e)
    {
        if (_app.Data.InReplay) _app.ScrubTo(e.T);
        if (e.X is double x && e.Y is double y && _app.ActiveMap is { } m)
            m.SetView((float)x, (float)y, Math.Max(m.Scale, 8));
    }

    private void Rebuild()
    {
        _items.Clear();
        var log = _app.Data.Log.Entries;
        for (int i = log.Count - 1; i >= 0 && _items.Count < MaxShown; i--)
            if (_app.LogOn.Contains(log[i].Category)) _items.Add(log[i]);
        _lastSeq = log.Count > 0 ? log[^1].Seq : 0;
        _version = _app.Data.Log.Version;
        BuildChips();
    }

    public void Refresh()
    {
        var log = _app.Data.Log;
        if (log.Version == _version) return;
        _version = log.Version;
        var e = log.Entries;
        if (e.Count > 0 && e[0].Seq > _lastSeq + 1 && _lastSeq != 0) { Rebuild(); return; } // trimmed past what we showed
        int start = e.Count;
        while (start > 0 && e[start - 1].Seq > _lastSeq) start--;
        for (int i = start; i < e.Count; i++)
            if (_app.LogOn.Contains(e[i].Category)) _items.Insert(0, e[i]);
        while (_items.Count > MaxShown) _items.RemoveAt(_items.Count - 1);
        if (e.Count > 0) _lastSeq = e[^1].Seq;
        UpdateChipCounts();
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        foreach (var c in Cats)
        {
            if (c == LogCategory.System && _app.Data.Log.CountOf(c) == 0) continue;
            var cc = c;
            var chip = Ui.Chip(MessageLog.CategoryTitle(c), _app.Data.Log.CountOf(c).ToString(CultureInfo.InvariantCulture), _app.LogOn.Contains(c), () =>
            {
                if (!_app.LogOn.Remove(cc)) _app.LogOn.Add(cc);
                Rebuild();
            });
            chip.Tag = c;
            _chips.Children.Add(chip);
        }
    }

    private void UpdateChipCounts()
    {
        bool hasSystem = _app.Data.Log.CountOf(LogCategory.System) > 0;
        if (hasSystem && !_chips.Children.Any(c => c.Tag is LogCategory.System)) { BuildChips(); return; }
        var counts = new int[Cats.Length];
        foreach (var e in _app.Data.Log.Entries) counts[(int)e.Category]++;
        foreach (var c in _chips.Children.OfType<ToggleButton>())
            if (c.Tag is LogCategory cat && c.Content is StackPanel sp && sp.Children[^1] is TextBlock t)
                t.Text = counts[(int)cat].ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>One log line: time, category (coloured small caps), message (wraps).</summary>
public sealed class LogRow : Grid
{
    private readonly AppState _app;
    private readonly TextBlock _t = new() { FontSize = 10.5 };
    private readonly TextBlock _c = new() { FontSize = 9.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.14, Margin = new Thickness(0, 1, 0, 0) };
    private readonly TextBlock _m = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

    public LogRow(AppState app)
    {
        _app = app;
        ColumnDefinitions = new ColumnDefinitions("66,74,*");
        ColumnSpacing = 8;
        Ui.BindFont(_t, "FontData");
        Ui.BindBrush(_t, "TextFaintBrush");
        Ui.BindFont(_c, "FontDisplay");
        Children.Add(_t);
        SetColumn(_c, 1); Children.Add(_c);
        SetColumn(_m, 2); Children.Add(_m);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not LogEntry le) return;
        _t.Text = _app.Data.ClockText(le.T);
        _c.Text = MessageLog.CategoryName(le.Category).ToUpperInvariant();
        Ui.BindBrush(_c, le.Category switch
        {
            LogCategory.Contact => "UnknownBrush",
            LogCategory.Weapons => "FriendBrush",
            LogCategory.Intercept => "OkBrush",
            LogCategory.Damage => "CritBrush",
            LogCategory.Orders => "AccentBrush",
            _ => "TextDimBrush",
        });
        _m.Text = le.Text;
    }
}

/// <summary>The Comms tab: channel chips, messages newest first, and (demo) a compose box.</summary>
public sealed class CommsView : UserControl
{
    private readonly AppState _app;
    private readonly WrapPanel _chips = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly StackPanel _list = new();
    private readonly Border _compose;
    private readonly ComboBox _channel = new() { ItemsSource = new[] { "Team", "Diplomatic", "Broadcast" }, SelectedIndex = 0 };
    private readonly TextBox _input = new() { PlaceholderText = "Message as Blue commander" };
    private long _version = -1;
    private static readonly string[] Channels = ["All", "Team", "Diplomatic", "Broadcast"];

    public CommsView(AppState app)
    {
        _app = app;
        _channel.Classes.Add("tsel");
        _input.Classes.Add("inp");
        var send = Ui.Button("Send", Send, "acc");
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Send(); e.Handled = true; } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(_channel);
        Grid.SetColumn(_input, 1); row.Children.Add(_input);
        Grid.SetColumn(send, 2); row.Children.Add(send);
        _compose = new Border { Child = row, Padding = new Thickness(10, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        _compose.Bind(Border.BorderBrushProperty, _compose.GetResourceObservable("LineBrush"));

        var chipsBox = new Border { Child = _chips };
        chipsBox.Classes.Add("chips");
        var root = new DockPanel();
        DockPanel.SetDock(chipsBox, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_compose, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(chipsBox);
        root.Children.Add(_compose);
        root.Children.Add(new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        AttachedToVisualTree += (_, _) => { _app.SlowTick += Refresh; _app.SourceChanged += Refresh; _version = -1; Refresh(); _app.MarkCommsRead(); };
        DetachedFromVisualTree += (_, _) => { _app.SlowTick -= Refresh; _app.SourceChanged -= Refresh; };
    }

    private void Send()
    {
        if (_app.Source is DemoBattleSource d && !string.IsNullOrWhiteSpace(_input.Text))
        {
            d.SendComms((string)_channel.SelectedItem!, _input.Text);
            _input.Text = "";
            _version = -1;
            Refresh();
        }
    }

    public void Refresh()
    {
        _compose.IsVisible = (_app.Source.Caps & SourceCaps.Compose) != 0;
        var comms = _app.Data.Comms;
        if (_app.Data.CommsVersion == _version) return;
        _version = _app.Data.CommsVersion;
        if (IsEffectivelyVisible) _app.MarkCommsRead();
        _chips.Children.Clear();
        foreach (var ch in Channels)
        {
            var c = ch;
            int n = ch == "All" ? comms.Count : comms.Count(m => m.Channel == ch);
            _chips.Children.Add(Ui.Chip(ch, n.ToString(CultureInfo.InvariantCulture), _app.CommsChannel == ch, () => { _app.CommsChannel = c; _version = -1; Refresh(); }));
        }
        _list.Children.Clear();
        var shown = comms.Where(m => _app.CommsChannel == "All" || m.Channel == _app.CommsChannel).Reverse().Take(300).ToList();
        if (shown.Count == 0)
        {
            var note = Ui.Text("No messages yet. Team reports and enemy transmissions appear here.", "note");
            note.Margin = new Thickness(10);
            _list.Children.Add(note);
            return;
        }
        foreach (var m in shown) _list.Children.Add(Msg(m));
    }

    private Control Msg(CommsEntry m)
    {
        var who = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        who.Inlines = new Avalonia.Controls.Documents.InlineCollection { new Avalonia.Controls.Documents.Run(m.Who) };
        Ui.BindBrush(who, m.Side switch { CommsSide.Red => "HostileBrush", CommsSide.Blue => "FriendBrush", CommsSide.You => "AccentBrush", _ => "TextBrush" });
        var meta = new TextBlock { Text = $"{m.Channel} · {_app.Data.ClockText(m.T)}", FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Ui.BindFont(meta, "FontData");
        Ui.BindBrush(meta, "TextFaintBrush");
        var head = Ui.H(0, who, meta);
        if (m.Unverified)
        {
            var u = new TextBlock { Text = "UNVERIFIED SENDER", FontSize = 9.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.14 };
            Ui.BindFont(u, "FontDisplay");
            Ui.BindBrush(u, "WarnBrush");
            var ub = new Border { Child = u, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 2), Margin = new Thickness(6, 0, 0, 0) };
            ub.Bind(Border.BorderBrushProperty, ub.GetResourceObservable("UnvBorderBrush"));
            head.Children.Add(ub);
        }
        var body = new TextBlock { Text = m.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, LineHeight = 19 };
        var b = new Border { Padding = new Thickness(10, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.V(2, head, body) };
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("LineSofterBrush"));
        return b;
    }
}

/// <summary>The Losses tab: Blue and Red columns, losses by class then expenditure and counters.</summary>
public sealed class LossesView : UserControl
{
    private readonly AppState _app;
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("*,*") };
    private long _version = -1;

    public LossesView(AppState app)
    {
        _app = app;
        Content = new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AttachedToVisualTree += (_, _) => { _app.SlowTick += Refresh; _app.SourceChanged += Refresh; _version = -1; Refresh(); };
        DetachedFromVisualTree += (_, _) => { _app.SlowTick -= Refresh; _app.SourceChanged -= Refresh; };
    }

    public void Refresh()
    {
        var r = _app.Data.Losses;
        if (r.Version == _version) return;
        _version = r.Version;
        _grid.Children.Clear();
        var blue = Column("Blue", "FriendBrush", r.Blue);
        var red = Column("Red", "HostileBrush", r.Red);
        Grid.SetColumn(red, 1);
        red.BorderThickness = new Thickness(1, 0, 0, 0);
        red.Bind(Border.BorderBrushProperty, red.GetResourceObservable("LineBrush"));
        _grid.Children.Add(blue);
        _grid.Children.Add(red);
        if (r.Note is { } note)
        {
            _grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            var n = Ui.Text(note, "note");
            n.Margin = new Thickness(12, 0, 12, 10);
            Grid.SetRow(n, 1); Grid.SetColumnSpan(n, 2);
            _grid.Children.Add(n);
        }
    }

    private static Border Column(string title, string brush, SideLosses s)
    {
        var h = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.76, Margin = new Thickness(0, 0, 0, 8) };
        Ui.BindFont(h, "FontDisplay");
        Ui.BindBrush(h, brush);
        var dl = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, RowSpacing = 4 };
        int row = 0;
        void Line(string k, string v)
        {
            dl.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var dt = Ui.Text(k, "data", "dim");
            var dd = Ui.Text(v, "data");
            dd.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(dt, row); Grid.SetRow(dd, row); Grid.SetColumn(dd, 1);
            dl.Children.Add(dt); dl.Children.Add(dd);
            row++;
        }
        if (s.Lost.Count == 0) Line("None", "0");
        foreach (var (k, v) in s.Lost) Line(k, v.ToString(CultureInfo.InvariantCulture));
        if (s.Lines.Count > 0) Line(" ", "");
        foreach (var (k, v) in s.Lines) Line(k, v);
        return new Border { Padding = new Thickness(12, 10), Child = Ui.V(0, h, dl) };
    }
}
