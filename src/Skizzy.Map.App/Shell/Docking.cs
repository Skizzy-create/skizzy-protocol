using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Skizzy.Map.App.Map;
using Skizzy.Map.App.Panels;
using Skizzy.Map.App.Services;

namespace Skizzy.Map.App.Shell;

/// <summary>
/// Docking for the shell (Dock.Avalonia, MIT): panels are Tools, map windows are Documents. Any of them can be
/// dragged, docked elsewhere, tabbed, floated into its own OS window and closed (tools hide, so the Panels
/// toggles bring them back). Layouts are saved as a <see cref="DockSnapshot"/> (ids only); content is attached
/// by dockable Id after a restore.
/// </summary>
public sealed class ShellDockFactory : Factory
{
    public const string Tracks = "tracks", Unit = "unit", Messages = "messages", Comms = "comms", Losses = "losses",
        Layers = "layers", Health = "health", Maps = "maps", Left = "left", Right = "right", Bottom = "bottom";

    public static readonly (string Id, string Title)[] ToolDefs =
    [
        (Tracks, "Tracks"), (Unit, "Unit"), (Messages, "Messages"), (Comms, "Comms"), (Losses, "Losses"), (Layers, "Layers"), (Health, "Host health"),
    ];

    private readonly AppState _app;

    public ShellDockFactory(AppState app)
    {
        _app = app;
        HideToolsOnClose = true;
        HideDocumentsOnClose = false;
    }

    public static bool IsToolId(string? id) => id is not null && ToolDefs.Any(t => t.Id == id);

    /// <summary>The dock a panel goes to by default (Tracks left, Unit/Layers/Health right, the log tabs bottom).</summary>
    public static IToolDock? DefaultDockFor(IRootDock root, string toolId)
    {
        string want = toolId switch { Tracks => Left, Messages or Comms or Losses => Bottom, _ => Right };
        var docks = Walk(root).OfType<IToolDock>().ToList();
        return docks.FirstOrDefault(d => d.Id == want) ?? docks.FirstOrDefault(d => d.Id == Right) ?? docks.FirstOrDefault();
    }

    public Tool NewTool(string id) => new() { Id = id, Title = ToolDefs.First(t => t.Id == id).Title.ToUpperInvariant(), CanClose = true, CanFloat = true, CanPin = false };

    public ToolDock NewToolDock(string id, Alignment alignment, double proportion) =>
        new() { Id = id, Title = id, Alignment = alignment, Proportion = proportion };

    public Document NewMapDoc(string id) => new() { Id = id, Title = "Plot", CanClose = id != "map:1", CanFloat = true };

    public DocumentDock NewMapsDock(string id, double proportion)
    {
        var docs = new DocumentDock { Id = id, Title = "Maps", Proportion = proportion, IsCollapsable = false, CanCreateDocument = true };
        docs.CreateDocument = new NewMapCommand(this, docs);
        return docs;
    }

    public override IRootDock CreateLayout()
    {
        ToolDock Tools(string id, Alignment a, double p, params string[] tools)
        {
            var d = NewToolDock(id, a, p);
            d.VisibleDockables = CreateList<IDockable>(tools.Select(t => (IDockable)NewTool(t)).ToArray());
            d.ActiveDockable = d.VisibleDockables[0];
            return d;
        }
        var left = Tools(Left, Alignment.Left, 0.21, Tracks);
        var right = Tools(Right, Alignment.Right, 0.215, Unit, Layers, Health);
        var bottom = Tools(Bottom, Alignment.Bottom, 0.25, Messages, Comms, Losses);

        var map = NewMapDoc("map:1");
        var docs = NewMapsDock(Maps, 0.75);
        docs.VisibleDockables = CreateList<IDockable>(map);
        docs.ActiveDockable = map;

        var center = new ProportionalDock
        {
            Id = "center", Orientation = Orientation.Vertical, Proportion = 0.575,
            VisibleDockables = CreateList<IDockable>(docs, new ProportionalDockSplitter(), bottom),
        };
        var main = new ProportionalDock
        {
            Id = "main", Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), center, new ProportionalDockSplitter(), right),
        };
        var root = CreateRootDock();
        root.Id = "root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        return root;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>();
        DefaultHostWindowLocator = () => new HostWindow();
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>> { [nameof(IDockWindow)] = () => new HostWindow() };
        base.InitLayout(layout);
    }

    /// <summary>Set while a layout replaces another: closing its windows must not drop map windows the new one keeps.</summary>
    public bool Replacing { get; set; }

    public override void OnDockableClosed(IDockable? dockable)
    {
        base.OnDockableClosed(dockable);
        if (Replacing) return;
        if (dockable is Document d && d.Id.StartsWith("map:", StringComparison.Ordinal))
        {
            var m = _app.Maps.FirstOrDefault(x => x.Id == d.Id);
            if (m is not null && _app.Maps.Count > 1) _app.RemoveMap(m);
        }
    }

    public override void OnActiveDockableChanged(IDockable? dockable)
    {
        base.OnActiveDockableChanged(dockable);
        if (dockable is Document d && _app.Maps.FirstOrDefault(m => m.Id == d.Id) is { } vm) _app.Activate(vm);
    }

    /// <summary>Adds a new map window as a document next to the others.</summary>
    public Document AddMap(IDocumentDock docs)
    {
        var id = _app.NextMapId();
        var vm = _app.CreateMap(id);
        var src = _app.ActiveMap ?? vm;
        if (src != vm) { vm.Apply(src.ToConfig()); vm.Title = "Plot " + id.Split(':')[1]; }
        var doc = NewMapDoc(id);
        doc.Title = vm.Title;
        Attach(doc);
        AddDockable(docs, doc);
        SetActiveDockable(doc);
        SetFocusedDockable(docs, doc);
        _app.Activate(vm);
        return doc;
    }

    // ------------------------------------------------------------ content

    /// <summary>Attaches views to every dockable in a layout (including floating windows) by Id.</summary>
    public void AttachAll(IRootDock root)
    {
        foreach (var d in Walk(root)) Attach(d);
    }

    public void Attach(IDockable d)
    {
        switch (d)
        {
            case Tool t:
                t.Content = t.Id switch
                {
                    Tracks => new Func<IServiceProvider, object>(_ => new TracksView(_app)),
                    Unit => new Func<IServiceProvider, object>(_ => new UnitView(_app)),
                    Messages => new Func<IServiceProvider, object>(_ => new MessagesView(_app)),
                    Comms => new Func<IServiceProvider, object>(_ => new CommsView(_app)),
                    Losses => new Func<IServiceProvider, object>(_ => new LossesView(_app)),
                    Layers => new Func<IServiceProvider, object>(_ => new LayersView(_app)),
                    Health => new Func<IServiceProvider, object>(_ => new HealthView(_app)),
                    _ => t.Content,
                };
                break;
            case Document doc when doc.Id.StartsWith("map:", StringComparison.Ordinal):
            {
                var vm = _app.GetOrCreateMap(doc.Id);
                doc.Title = vm.Title;
                doc.Content = new Func<IServiceProvider, object>(_ => new MapView(_app, vm));
                break;
            }
        }
    }

    /// <summary>Every dockable in the layout: the main tree, floating windows, hidden and pinned lists.</summary>
    public static IEnumerable<IDockable> Walk(IDockable? d)
    {
        if (d is null) yield break;
        yield return d;
        if (d is IDock dock && dock.VisibleDockables is { } vis)
            foreach (var c in vis.ToList())
                foreach (var x in Walk(c)) yield return x;
        if (d is IRootDock root)
        {
            if (root.Windows is { } wins)
                foreach (var w in wins.ToList())
                    foreach (var x in Walk(w.Layout)) yield return x;
            foreach (var list in new[] { root.HiddenDockables, root.LeftPinnedDockables, root.RightPinnedDockables, root.TopPinnedDockables, root.BottomPinnedDockables })
                if (list is not null)
                    foreach (var c in list.ToList())
                        foreach (var x in Walk(c)) yield return x;
        }
    }

    public static IDockable? FindById(IRootDock root, string id) => Walk(root).FirstOrDefault(d => d.Id == id);

    public static bool IsHidden(IRootDock root, IDockable d) => root.HiddenDockables?.Contains(d) == true;

    public static IDocumentDock? FindDocs(IRootDock root) =>
        Walk(root).OfType<IDocumentDock>().FirstOrDefault(d => d.Id == Maps) ?? Walk(root).OfType<IDocumentDock>().FirstOrDefault();

    private sealed class NewMapCommand(ShellDockFactory f, IDocumentDock docs) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => f.AddMap(docs);
    }
}

/// <summary>Builds, saves and restores the shell's dock layout and the map windows' settings.</summary>
public sealed class LayoutManager
{
    private readonly AppState _app;
    private readonly DockControl _dock;

    public LayoutManager(AppState app, DockControl dock)
    {
        _app = app;
        _dock = dock;
        Factory = new ShellDockFactory(app);
        _dock.Factory = Factory;
        _dock.InitializeFactory = true;
        _dock.InitializeLayout = true;
    }

    public ShellDockFactory Factory { get; }
    public IRootDock? Root => _dock.Layout as IRootDock;

    public void UseDefault()
    {
        var root = Factory.CreateLayout();
        Factory.AttachAll(root);
        Install(root);
        foreach (var m in _app.Maps.ToList()) if (FindDoc(root, m.Id) is null) _app.RemoveMap(m);
    }

    private static IDockable? FindDoc(IRootDock root, string id) => ShellDockFactory.FindById(root, id);

    private void Install(IRootDock root)
    {
        if (!ReferenceEquals(Root, root)) CloseFloating();
        Factory.InitLayout(root);
        _dock.Layout = root;
    }

    /// <summary>Closes the current layout's floating windows (a new layout replaces them; the app is closing).</summary>
    public void CloseFloating()
    {
        if (Root?.Windows is not { Count: > 0 } wins) return;
        Factory.Replacing = true;
        try
        {
            foreach (var w in wins.ToList()) w.Exit();
        }
        finally { Factory.Replacing = false; }
    }

    public SavedLayout Capture(string name)
    {
        var root = Root ?? throw new InvalidOperationException("no layout");
        var maps = new Dictionary<string, MapConfig>();
        foreach (var m in _app.Maps) maps[m.Id] = m.ToConfig();
        return new SavedLayout { Name = name, Panels = DockSnapshot.Capture(root), Maps = maps };
    }

    /// <summary>Applies a saved layout. Returns false (and keeps the current one) when it cannot be read.</summary>
    public bool Apply(SavedLayout layout)
    {
        if (layout.Panels is not { } snap) return false;
        IRootDock? root;
        Action after;
        try { root = snap.Build(Factory, out after); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            System.Diagnostics.Trace.WriteLine("skizzy-map: layout not restored: " + e.Message);
            root = null;
            after = () => { };
        }
        if (root is null)
        {
            _app.Toast("That layout could not be read; keeping the current one.");
            return false;
        }
        // map windows: create the ones the layout has (with their settings), drop the rest
        var ids = ShellDockFactory.Walk(root).OfType<Document>().Select(d => d.Id).ToHashSet();
        foreach (var w in snap.Windows) foreach (var i in w.Items) if (i.StartsWith("map:", StringComparison.Ordinal)) ids.Add(i);
        foreach (var id in ids)
        {
            var vm = _app.GetOrCreateMap(id);
            if (layout.Maps.TryGetValue(id, out var cfg)) vm.Apply(cfg);
        }
        foreach (var m in _app.Maps.ToList()) if (!ids.Contains(m.Id)) _app.RemoveMap(m);
        Factory.AttachAll(root);
        Install(root);
        after();
        if (_app.Maps.FirstOrDefault() is { } first) _app.Activate(first);
        return true;
    }

    // ------------------------------------------------------------ panel toggles

    public bool IsVisible(string toolId)
    {
        var root = Root;
        if (root is null) return false;
        var d = ShellDockFactory.FindById(root, toolId);
        return d is not null && !ShellDockFactory.IsHidden(root, d);
    }

    public void SetVisible(string toolId, bool visible)
    {
        var root = Root;
        if (root is null) return;
        var d = ShellDockFactory.FindById(root, toolId);
        if (visible)
        {
            if (d is null)
            {
                // a layout without this tool: add it to the right-hand dock (or any tool dock)
                if (!ShellDockFactory.IsToolId(toolId)) return;
                var tool = Factory.NewTool(toolId);
                Factory.Attach(tool);
                var dock = ShellDockFactory.DefaultDockFor(root, toolId);
                if (dock is null) return;
                Factory.AddDockable(dock, tool);
                Factory.SetActiveDockable(tool);
                return;
            }
            if (ShellDockFactory.IsHidden(root, d)) Factory.RestoreDockable(d);
            Factory.SetActiveDockable(d);
            if (d.Owner is IDock owner) Factory.SetFocusedDockable(owner, d);
        }
        else if (d is not null && !ShellDockFactory.IsHidden(root, d))
        {
            Factory.HideDockable(d);
        }
    }

    public void AddMap()
    {
        var root = Root;
        if (root is null) return;
        var docs = ShellDockFactory.FindDocs(root);
        if (docs is null) return;
        Factory.AddMap(docs);
    }
}
