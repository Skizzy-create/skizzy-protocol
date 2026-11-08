using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;

namespace Skizzy.Map.App.Shell;

/// <summary>One palette row: an app action, or a capability from the data source's catalog.</summary>
public sealed record PaletteItem(string Title, string Name, string Area, string Description, string Safety, bool Available, string Schema, Action? Local)
{
    public bool IsLocal => Local is not null;

    /// <summary>What the M3 palette lets you do with it: run now, run after a confirm, or nothing.</summary>
    public PaletteGate Gate => IsLocal ? PaletteGate.Run
        : !Available ? PaletteGate.Unavailable
        : Safety switch { "read" => PaletteGate.Run, "control" => PaletteGate.Confirm, _ => PaletteGate.Blocked };
}

public enum PaletteGate { Run, Confirm, Blocked, Unavailable }

/// <summary>
/// The command palette (Ctrl+K): the app's own actions plus the host catalog, searchable. Picking a capability
/// opens a form generated from its input schema; read capabilities run at once, control ones after a confirm,
/// destructive, cheat and unsafe ones are listed but cannot run from the map until the M4 permission flow.
/// </summary>
public sealed class CommandPalette : Panel
{
    private readonly AppState _app;
    private readonly TextBox _search = new() { PlaceholderText = "Search commands and host capabilities…" };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = Ui.Text("", "note");
    private readonly Border _card;
    private readonly Panel _listPage = new();
    private readonly StackPanel _formPage = new() { Spacing = 10, Margin = new Thickness(14, 12) };
    private readonly ScrollViewer _formScroll;
    private readonly List<PaletteItem> _local = new();
    private List<PaletteItem> _all = new();
    private PaletteItem? _current;
    private Func<string?>? _buildArgs;
    private CancellationTokenSource? _cts;

    public CommandPalette(AppState app)
    {
        _app = app;
        _search.Classes.Add("inp");
        _search.FontSize = 14;
        _search.MinHeight = 34;
        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += OnSearchKey;

        _list.ItemTemplate = new FuncDataTemplate<PaletteItem>((it, _) => it is null ? new Panel() : Row(it), supportsRecycling: false);
        _list.DoubleTapped += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

        var esc = KeyBadge("Esc");
        var head = new DockPanel { Margin = new Thickness(10, 4, 10, 4) };
        DockPanel.SetDock(esc, Avalonia.Controls.Dock.Right);
        head.Children.Add(esc);
        var glass = Ui.Text("⌕", "faint");
        glass.FontSize = 16; glass.VerticalAlignment = VerticalAlignment.Center; glass.Margin = new Thickness(4, 0, 2, 0);
        DockPanel.SetDock(glass, Avalonia.Controls.Dock.Left);
        head.Children.Add(glass);
        head.Children.Add(_search);
        var headBox = new Border { Child = head };
        headBox.Classes.Add("ph");

        var foot = new DockPanel { Margin = new Thickness(12, 6) };
        var hint = Ui.Text("↑ ↓ to move · Enter to open · read capabilities run at once, control ones ask first", "note");
        DockPanel.SetDock(_count, Avalonia.Controls.Dock.Right);
        foot.Children.Add(_count);
        foot.Children.Add(hint);
        var footBox = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBox.Bind(Border.BorderBrushProperty, footBox.GetResourceObservable("LineBrush"));

        var listGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        listGrid.Children.Add(_list);
        Grid.SetRow(footBox, 1);
        listGrid.Children.Add(footBox);
        _listPage.Children.Add(listGrid);

        _formScroll = new ScrollViewer { Content = _formPage, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, IsVisible = false };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(headBox);
        var pages = new Panel();
        pages.Children.Add(_listPage);
        pages.Children.Add(_formScroll);
        Grid.SetRow(pages, 1);
        body.Children.Add(pages);

        _card = new Border
        {
            Child = body,
            Width = 720,
            MaxHeight = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 72, 16, 16),
            BoxShadow = BoxShadows.Parse("0 18 50 0 #73000000"),
        };
        _card.Classes.Add("panel");
        _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("LineHiBrush"));

        var scrim = new Border();
        scrim.Bind(Border.BackgroundProperty, scrim.GetResourceObservable("ScrimBrush"));
        scrim.PointerPressed += (_, e) => { Close(); e.Handled = true; };
        Children.Add(scrim);
        Children.Add(_card);
    }

    /// <summary>Adds an app action ("Map: Fit all") shown above the catalog.</summary>
    public void AddAction(string title, string area, string description, Action run) =>
        _local.Add(new PaletteItem(title, "", area, description, "app", true, "{}", run));

    internal IReadOnlyList<PaletteItem> Shown => _list.ItemsSource as IReadOnlyList<PaletteItem> ?? [];
    internal PaletteItem? Current => _current;
    internal TextBox Search => _search;
    internal string? LastResult { get; private set; }
    internal Task? Running { get; private set; }

    public void Open()
    {
        IsVisible = true;
        _all = [.. _local, .. _app.Source.Catalog.OrderBy(c => c.Area, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new PaletteItem(c.Title, c.Name, c.Area, c.Description, c.Safety.ToLowerInvariant(), c.Available, c.InputSchemaJson, null))];
        ShowList();
        _search.Text = "";
        Filter();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _search.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }

    public void Close()
    {
        _cts?.Cancel();
        IsVisible = false;
    }

    /// <summary>Esc: back from a form to the list, else close.</summary>
    public void Escape()
    {
        if (_current is not null) { ShowList(); _search.Focus(); }
        else Close();
    }

    // ------------------------------------------------------------ list

    private void Filter()
    {
        var q = (_search.Text ?? "").Trim();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _all.Where(i => words.All(w =>
                i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.Area.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => Rank(i, q))
            .ToList();
        _list.ItemsSource = shown;
        _list.SelectedIndex = shown.Count > 0 ? 0 : -1;
        int caps = _all.Count(i => !i.IsLocal);
        _count.Text = caps == 0
            ? (_app.Source.State == SourceState.Online || _app.Source.Kind == SourceKind.Demo ? "no host catalog" : "host catalog not loaded yet")
            : $"{shown.Count} of {_all.Count} · {caps} capabilities";
    }

    private static int Rank(PaletteItem i, string q)
    {
        if (q.Length == 0) return i.IsLocal ? 0 : i.Available ? 1 : 2;
        if (i.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase) || i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        return i.Available ? 1 : 2;
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        int n = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down when n > 0:
                _list.SelectedIndex = Math.Min(n - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when n > 0:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
        }
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is PaletteItem it) OpenItem(it);
    }

    internal void OpenItem(PaletteItem it)
    {
        if (it.Local is { } run)
        {
            Close();
            run();
            return;
        }
        ShowForm(it);
    }

    private Control Row(PaletteItem it)
    {
        var title = Ui.Text(it.Title);
        title.FontSize = 13;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!it.Available) Ui.BindBrush(title, "TextFaintBrush");
        var name = Ui.Text(it.IsLocal ? it.Description : it.Name, "data", "faint");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var left = Ui.V(1, title, name);

        var right = Ui.H(8);
        right.VerticalAlignment = VerticalAlignment.Center;
        if (it.Area.Length > 0) right.Children.Add(Ui.Label(it.Area));
        right.Children.Add(SafetyPill(it));

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 6, 12, 6) };
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private static Border SafetyPill(PaletteItem it)
    {
        string text = it.IsLocal ? "app" : !it.Available ? "unavailable" : it.Safety;
        string cls = it.IsLocal ? "" : !it.Available ? "" : it.Safety switch { "read" => "ok", "control" => "warn", _ => "crit" };
        var p = cls.Length > 0 ? Ui.Pill(text, cls) : Ui.Pill(text);
        p.Padding = new Thickness(7, 1);
        return p;
    }

    private static Border KeyBadge(string text)
    {
        var t = Ui.Text(text, "data", "faint");
        t.FontSize = 10;
        var b = new Border { Child = t, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center };
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("LineHiBrush"));
        return b;
    }

    private void ShowList()
    {
        _cts?.Cancel();
        _current = null;
        _buildArgs = null;
        _formScroll.IsVisible = false;
        _listPage.IsVisible = true;
        _search.IsEnabled = true;
    }

    // ------------------------------------------------------------ capability form

    private void ShowForm(PaletteItem it)
    {
        _current = it;
        _listPage.IsVisible = false;
        _formScroll.IsVisible = true;
        _formPage.Children.Clear();
        LastResult = null;

        var back = Ui.Button("‹ Back", () => { ShowList(); _search.Focus(); }, "ghost");
        var title = Ui.Text(it.Title);
        title.FontSize = 15; title.FontWeight = FontWeight.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
        Ui.BindFont(title, "FontDisplay");
        var head = new DockPanel();
        var pill = SafetyPill(it);
        DockPanel.SetDock(pill, Avalonia.Controls.Dock.Right);
        head.Children.Add(pill);
        DockPanel.SetDock(back, Avalonia.Controls.Dock.Left);
        head.Children.Add(back);
        title.Margin = new Thickness(10, 0, 0, 0);
        head.Children.Add(title);
        _formPage.Children.Add(head);
        var meta = Ui.Text(it.Name + (it.Area.Length > 0 ? "  ·  " + it.Area : ""), "data", "dim");
        _formPage.Children.Add(meta);
        if (it.Description.Length > 0)
        {
            var d = Ui.Text(it.Description, "note");
            d.FontSize = 12;
            _formPage.Children.Add(d);
        }

        var error = Ui.Text("", "win");
        error.FontSize = 11.5;
        error.TextWrapping = TextWrapping.Wrap;
        error.IsVisible = false;

        var fields = BuildFields(it.Schema, out _buildArgs, error);
        _formPage.Children.Add(fields);

        var result = new SelectableTextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        result.Bind(TextBlock.FontFamilyProperty, result.GetResourceObservable("FontData"));
        var resultBox = new Border { Child = new ScrollViewer { Content = result, MaxHeight = 240 }, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), IsVisible = false };
        resultBox.Bind(Border.BackgroundProperty, resultBox.GetResourceObservable("SeaBrush"));
        resultBox.Bind(Border.BorderBrushProperty, resultBox.GetResourceObservable("LineBrush"));
        var resultLbl = Ui.Label("Result");
        resultLbl.IsVisible = false;

        var actions = Ui.H(8);
        var gateNote = Ui.Text("", "note");
        gateNote.VerticalAlignment = VerticalAlignment.Center;
        switch (it.Gate)
        {
            case PaletteGate.Run:
                actions.Children.Add(Ui.Button("Run", () => Run(it, error, result, resultBox, resultLbl, actions), "acc"));
                gateNote.Text = "Read only: runs at once.";
                break;
            case PaletteGate.Confirm:
                actions.Children.Add(Ui.Button("Run…", () => AskConfirm(it, error, result, resultBox, resultLbl, actions), "acc"));
                gateNote.Text = "Control: changes the game, asks before it runs.";
                break;
            case PaletteGate.Blocked:
                var b = Ui.Button("Run", () => { }, "acc");
                b.IsEnabled = false;
                actions.Children.Add(b);
                gateNote.Text = $"{Cap(it.Safety)} capabilities cannot run from the map in M3; they need the permission flow that arrives with orders (M4).";
                break;
            default:
                var u = Ui.Button("Run", () => { }, "acc");
                u.IsEnabled = false;
                actions.Children.Add(u);
                gateNote.Text = "Not available from this source right now (wrong scene, or not served by this host).";
                break;
        }
        actions.Children.Add(gateNote);
        gateNote.TextWrapping = TextWrapping.Wrap;
        gateNote.MaxWidth = 520;
        _formPage.Children.Add(actions);
        _formPage.Children.Add(error);
        _formPage.Children.Add(resultLbl);
        _formPage.Children.Add(resultBox);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var first = fields.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (first is not null) first.Focus(); else actions.Children.OfType<Button>().FirstOrDefault()?.Focus();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void AskConfirm(PaletteItem it, TextBlock error, SelectableTextBlock result, Border resultBox, TextBlock resultLbl, StackPanel actions)
    {
        var args = _buildArgs?.Invoke();
        if (args is null) return;
        actions.Children.Clear();
        var q = Ui.Text($"Run {it.Name} on {_app.Source.Name}? It changes the game.", "dim");
        q.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(Ui.Button("Confirm", () => Run(it, error, result, resultBox, resultLbl, actions, args), "acc"));
        actions.Children.Add(Ui.Button("Cancel", () => ShowForm(it), "ghost"));
        actions.Children.Add(q);
    }

    private void Run(PaletteItem it, TextBlock error, SelectableTextBlock result, Border resultBox, TextBlock resultLbl, StackPanel actions, string? argsJson = null)
    {
        argsJson ??= _buildArgs?.Invoke();
        if (argsJson is null) return;
        error.IsVisible = false;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var b in actions.Children.OfType<Button>()) b.IsEnabled = false;
        result.Text = "Running…";
        resultLbl.IsVisible = resultBox.IsVisible = true;
        Running = RunAsync();

        async Task RunAsync()
        {
            string text;
            bool failed = false;
            try
            {
                var raw = await _app.Source.CallAsync(it.Name, argsJson, cts.Token);
                text = Pretty(raw);
            }
            catch (OperationCanceledException)
            {
                text = cts.IsCancellationRequested && IsVisible ? "Timed out after 15 s." : "Cancelled.";
                failed = true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                text = e.Message;
                failed = true;
            }
            if (!ReferenceEquals(_current, it)) return;
            LastResult = text;
            result.Text = text.Length > 40_000 ? text[..40_000] + "\n… (truncated)" : text;
            if (failed) { error.Text = "The call failed."; error.IsVisible = true; }
            if (it.Gate == PaletteGate.Confirm) { ShowFormKeepResult(it, text, failed); return; }
            foreach (var b in actions.Children.OfType<Button>()) b.IsEnabled = true;
        }
    }

    private void ShowFormKeepResult(PaletteItem it, string text, bool failed)
    {
        ShowForm(it);
        LastResult = text;
        var box = _formPage.Children.OfType<Border>().LastOrDefault();
        var lbl = _formPage.Children.OfType<TextBlock>().LastOrDefault(t => t.Classes.Contains("lbl"));
        if (box?.Child is ScrollViewer { Content: SelectableTextBlock r })
        {
            r.Text = text.Length > 40_000 ? text[..40_000] + "\n… (truncated)" : text;
            box.IsVisible = true;
            if (lbl is not null) lbl.IsVisible = true;
        }
        if (failed && _formPage.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("win")) is { } err)
        {
            err.Text = "The call failed.";
            err.IsVisible = true;
        }
    }

    internal static string Pretty(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node is null ? "null" : node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    // ------------------------------------------------------------ schema to form

    /// <summary>
    /// Builds the inputs for a JSON Schema object: strings and numbers as text boxes, booleans as check boxes,
    /// enums as drop-downs, anything else as a JSON text box. <paramref name="build"/> returns the arguments
    /// as JSON text, or null after showing what is wrong in <paramref name="error"/>.
    /// </summary>
    internal static Control BuildFields(string schemaJson, out Func<string?> build, TextBlock error)
    {
        var panel = new StackPanel { Spacing = 8 };
        var getters = new List<Func<(bool ok, JsonNode? value, string? problem)>>();
        var names = new List<string>();
        JsonObject? props = null;
        HashSet<string> required = new(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson) is JsonObject schema)
            {
                props = schema["properties"] as JsonObject;
                if (schema["required"] is JsonArray req)
                    foreach (var r in req) if (r?.GetValueKind() == JsonValueKind.String) required.Add(r.GetValue<string>());
            }
        }
        catch (JsonException) { props = null; }

        if (props is null || props.Count == 0)
        {
            panel.Children.Add(Ui.Text("No arguments.", "note"));
            build = () => "{}";
            return panel;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 8 };
        int row = 0;
        foreach (var (name, node) in props)
        {
            if (node is not JsonObject p) continue;
            bool req = required.Contains(name);
            string type = TypeOf(p);
            string? desc = p["description"]?.GetValueKind() == JsonValueKind.String ? p["description"]!.GetValue<string>() : null;
            var label = Ui.Text(name + (req ? " *" : ""), "data");
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new Thickness(0, 5, 0, 0);
            Control input;
            Func<(bool, JsonNode?, string?)> get;
            var en = p["enum"] as JsonArray;
            if (en is not null && en.Count > 0)
            {
                var options = en.Select(v => v?.ToJsonString() ?? "null").ToList();
                var shown = en.Select(v => v?.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v?.ToJsonString() ?? "null").ToList();
                if (!req) { options.Insert(0, ""); shown.Insert(0, "(none)"); }
                var cb = new ComboBox { ItemsSource = shown, SelectedIndex = DefaultIndex(p, options, req), MinWidth = 200 };
                cb.Classes.Add("tsel");
                input = cb;
                get = () =>
                {
                    int i = cb.SelectedIndex;
                    if (i < 0 || options[i].Length == 0) return req ? (false, null, $"{name} is required.") : (true, null, null);
                    return (true, JsonNode.Parse(options[i]), null);
                };
            }
            else if (type == "boolean")
            {
                var chk = new CheckBox { Content = desc ?? "", IsChecked = p["default"]?.GetValueKind() == JsonValueKind.True };
                input = chk;
                desc = null;
                get = () => (true, JsonValue.Create(chk.IsChecked == true), null);
            }
            else
            {
                var tb = new TextBox { PlaceholderText = type switch { "integer" => "integer", "number" => "number", "string" => "text", _ => "JSON " + type } };
                tb.Classes.Add("inp");
                if (type is "object" or "array" or "any") tb.Bind(TextBox.FontFamilyProperty, tb.GetResourceObservable("FontData"));
                if (p["default"] is { } def) tb.Text = def.GetValueKind() == JsonValueKind.String ? def.GetValue<string>() : def.ToJsonString();
                input = tb;
                string t = type;
                get = () => ParseText(name, t, tb.Text, req);
            }
            var cell = desc is null ? input : Ui.V(3, input, Ui.Text(desc, "note"));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(label, row); Grid.SetRow(cell, row); Grid.SetColumn(cell, 1);
            grid.Children.Add(label); grid.Children.Add(cell);
            getters.Add(get);
            names.Add(name);
            row++;
        }
        panel.Children.Add(grid);
        build = () =>
        {
            var o = new JsonObject();
            for (int i = 0; i < getters.Count; i++)
            {
                var (ok, value, problem) = getters[i]();
                if (!ok)
                {
                    error.Text = problem ?? $"{names[i]} is not valid.";
                    error.IsVisible = true;
                    return null;
                }
                if (value is not null) o[names[i]] = value;
            }
            error.IsVisible = false;
            return o.ToJsonString();
        };
        return panel;
    }

    private static string TypeOf(JsonObject p)
    {
        var t = p["type"];
        if (t is null) return p["properties"] is not null ? "object" : "any";
        if (t.GetValueKind() == JsonValueKind.String) return t.GetValue<string>();
        if (t is JsonArray a) // ["string","null"]: the first non-null type
            foreach (var x in a)
                if (x?.GetValueKind() == JsonValueKind.String && x.GetValue<string>() != "null") return x.GetValue<string>();
        return "any";
    }

    private static int DefaultIndex(JsonObject p, List<string> options, bool req)
    {
        if (p["default"] is { } d)
        {
            int i = options.IndexOf(d.ToJsonString());
            if (i >= 0) return i;
        }
        return req ? -1 : 0;
    }

    internal static (bool ok, JsonNode? value, string? problem) ParseText(string name, string type, string? text, bool required)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return required ? (false, null, $"{name} is required.") : (true, null, null);
        switch (type)
        {
            case "string":
                return (true, JsonValue.Create(text), null);
            case "integer":
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? (true, JsonValue.Create(l), null)
                    : (false, null, $"{name} must be a whole number.");
            case "number":
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                    ? (true, JsonValue.Create(d), null)
                    : (false, null, $"{name} must be a number (use a dot for decimals).");
            default:
                try
                {
                    var n = JsonNode.Parse(s);
                    if (type == "object" && n is not JsonObject) return (false, null, $"{name} must be a JSON object.");
                    if (type == "array" && n is not JsonArray) return (false, null, $"{name} must be a JSON array.");
                    return (true, n, null);
                }
                catch (JsonException)
                {
                    // a bare word for an untyped field is taken as a string
                    return type == "any" ? (true, JsonValue.Create(s), null) : (false, null, $"{name} is not valid JSON.");
                }
        }
    }
}
