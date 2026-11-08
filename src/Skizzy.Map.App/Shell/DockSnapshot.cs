using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Skizzy.Map.App.Shell;

/// <summary>One node of the docked tree: a split, a tool dock, the map documents dock, or a splitter.</summary>
public sealed class DockNode
{
    /// <summary>split | tools | docs | splitter</summary>
    public string Kind { get; set; } = "";
    public string? Id { get; set; }
    /// <summary>split: Horizontal | Vertical</summary>
    public string? Orientation { get; set; }
    /// <summary>tools: Left | Right | Top | Bottom</summary>
    public string? Alignment { get; set; }
    public double? Proportion { get; set; }
    public string? Active { get; set; }
    public List<DockNode>? Children { get; set; }
    /// <summary>tools / docs: the panel ids ("tracks") or map ids ("map:2"), in tab order.</summary>
    public List<string>? Items { get; set; }
}

/// <summary>A panel floated into its own window, with where the window was.</summary>
public sealed class FloatingNode
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<string> Items { get; set; } = new();
    public string? Active { get; set; }
}

public sealed class HiddenNode
{
    public string Id { get; set; } = "";
    /// <summary>The dock it was hidden from, so showing it again puts it back there.</summary>
    public string? Dock { get; set; }
}

/// <summary>
/// The shell's dock layout in the map's own small, versioned JSON shape (not Dock's serializer, which in
/// 12.1.5 reflects over the Avalonia-based dock model and cannot read back its own output without generated
/// contracts; docs/M3-spikes.md). Ids are the only references: content is attached after a restore.
/// </summary>
public sealed class DockSnapshot
{
    public int Version { get; set; } = 1;
    public DockNode? Main { get; set; }
    public List<HiddenNode> Hidden { get; set; } = new();
    public List<FloatingNode> Windows { get; set; } = new();

    // ------------------------------------------------------------ capture

    public static DockSnapshot Capture(IRootDock root)
    {
        var s = new DockSnapshot();
        var main = root.VisibleDockables?.FirstOrDefault();
        s.Main = main is null ? null : Node(main);
        foreach (var list in new[] { root.HiddenDockables, root.LeftPinnedDockables, root.RightPinnedDockables, root.TopPinnedDockables, root.BottomPinnedDockables })
            if (list is not null)
                foreach (var d in list)
                    if (d.Id is { Length: > 0 } id && d is ITool or IDocument) s.Hidden.Add(new HiddenNode { Id = id, Dock = d.Owner?.Id });
        if (root.Windows is { } wins)
            foreach (var w in wins)
            {
                w.Save(); // the host window's current position and size into the model
                var items = ShellDockFactory.Walk(w.Layout).Where(d => d is ITool or IDocument).Select(d => d.Id).Where(i => !string.IsNullOrEmpty(i)).ToList();
                if (items.Count == 0) continue;
                var active = ShellDockFactory.Walk(w.Layout).OfType<IDock>().Select(d => d.ActiveDockable).FirstOrDefault(a => a is ITool or IDocument)?.Id;
                s.Windows.Add(new FloatingNode { X = w.X, Y = w.Y, Width = w.Width, Height = w.Height, Items = items, Active = active });
            }
        return s;
    }

    private static double? P(double v) => double.IsFinite(v) && v > 0 ? v : null;

    private static DockNode Node(IDockable d) => d switch
    {
        IProportionalDockSplitter => new DockNode { Kind = "splitter" },
        IToolDock td => new DockNode
        {
            Kind = "tools", Id = td.Id, Alignment = td.Alignment.ToString(), Proportion = P(td.Proportion), Active = td.ActiveDockable?.Id,
            Items = td.VisibleDockables?.Where(x => x is ITool).Select(x => x.Id).ToList() ?? [],
        },
        IDocumentDock dd => new DockNode
        {
            Kind = "docs", Id = dd.Id, Proportion = P(dd.Proportion), Active = dd.ActiveDockable?.Id,
            Items = dd.VisibleDockables?.Where(x => x is IDocument).Select(x => x.Id).ToList() ?? [],
        },
        IProportionalDock pd => new DockNode
        {
            Kind = "split", Id = pd.Id, Orientation = pd.Orientation.ToString(), Proportion = P(pd.Proportion),
            Children = pd.VisibleDockables?.Select(Node).ToList() ?? [],
        },
        IDock other => new DockNode
        {
            Kind = "split", Id = other.Id, Orientation = "Horizontal", Proportion = P(other.Proportion),
            Children = other.VisibleDockables?.Select(Node).ToList() ?? [],
        },
        // a bare tool or document straight in a split (only after unusual drags): keep it in a dock of its own
        IDocument doc => new DockNode { Kind = "docs", Id = ShellDockFactory.Maps + "-" + doc.Id, Proportion = P(doc.Proportion), Items = [doc.Id], Active = doc.Id },
        _ => new DockNode { Kind = "tools", Id = "tools-" + d.Id, Alignment = "Right", Proportion = P(d.Proportion), Items = [d.Id], Active = d.Id },
    };

    // ------------------------------------------------------------ restore

    /// <summary>
    /// Builds the docked tree. Unknown ids and duplicates are dropped; returns null when no map window survives
    /// (the caller keeps the current layout). <paramref name="afterInstall"/> re-creates floating windows and
    /// hidden panels once the root is installed in the dock control.
    /// </summary>
    public IRootDock? Build(ShellDockFactory f, out Action afterInstall)
    {
        afterInstall = () => { };
        if (Main is null) return null;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var main = BuildNode(f, Main, used);
        if (main is not IDock mainDock) return null;
        var root = f.CreateRootDock();
        root.Id = "root";
        root.IsCollapsable = false;
        root.VisibleDockables = f.CreateList<IDockable>(mainDock);
        root.ActiveDockable = mainDock;
        root.DefaultDockable = mainDock;

        var floats = Windows
            .Select(w => (w, items: w.Items.Where(i => Known(i) && used.Add(i)).ToList()))
            .Where(x => x.items.Count > 0)
            .ToList();
        bool anyMap = used.Any(IsMap);
        if (!anyMap) return null;
        var hidden = Hidden.Where(h => ShellDockFactory.IsToolId(h.Id) && used.Add(h.Id)).ToList();

        afterInstall = () =>
        {
            foreach (var (w, items) in floats) Float(f, root, w, items);
            foreach (var h in hidden)
            {
                var dock = (h.Dock is null ? null : ShellDockFactory.Walk(root).OfType<IToolDock>().FirstOrDefault(d => d.Id == h.Dock))
                           ?? ShellDockFactory.DefaultDockFor(root, h.Id);
                if (dock is null) continue;
                var tool = f.NewTool(h.Id);
                f.Attach(tool);
                f.AddDockable(dock, tool);
                f.HideDockable(tool);
            }
        };
        return root;
    }

    private static bool IsMap(string id) => id.StartsWith("map:", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out var n) && n > 0;

    private static bool Known(string id) => IsMap(id) || ShellDockFactory.IsToolId(id);

    private static IDockable? BuildNode(ShellDockFactory f, DockNode n, HashSet<string> used)
    {
        switch (n.Kind)
        {
            case "splitter":
                return new ProportionalDockSplitter();
            case "tools":
            {
                var items = (n.Items ?? []).Where(i => ShellDockFactory.IsToolId(i) && used.Add(i)).Select(i => (IDockable)f.NewTool(i)).ToList();
                if (items.Count == 0) return null;
                var align = Enum.TryParse<Alignment>(n.Alignment, out var a) ? a : Alignment.Right;
                var td = f.NewToolDock(n.Id ?? "tools", align, n.Proportion ?? double.NaN);
                td.VisibleDockables = f.CreateList(items.ToArray());
                td.ActiveDockable = items.FirstOrDefault(i => i.Id == n.Active) ?? items[0];
                return td;
            }
            case "docs":
            {
                var items = (n.Items ?? []).Where(i => IsMap(i) && used.Add(i)).Select(i => (IDockable)f.NewMapDoc(i)).ToList();
                if (items.Count == 0) return null;
                var dd = f.NewMapsDock(n.Id ?? ShellDockFactory.Maps, n.Proportion ?? double.NaN);
                dd.VisibleDockables = f.CreateList(items.ToArray());
                dd.ActiveDockable = items.FirstOrDefault(i => i.Id == n.Active) ?? items[0];
                return dd;
            }
            case "split":
            {
                var kids = new List<IDockable>();
                foreach (var c in n.Children ?? [])
                {
                    var k = BuildNode(f, c, used);
                    if (k is null) continue;
                    // no splitter first, last or doubled (a dock between them was dropped)
                    if (k is ProportionalDockSplitter && (kids.Count == 0 || kids[^1] is ProportionalDockSplitter)) continue;
                    kids.Add(k);
                }
                while (kids.Count > 0 && kids[^1] is ProportionalDockSplitter) kids.RemoveAt(kids.Count - 1);
                if (kids.Count == 0) return null;
                var orient = n.Orientation == "Vertical" ? Orientation.Vertical : Orientation.Horizontal;
                var pd = new ProportionalDock { Id = n.Id ?? "split", Orientation = orient, Proportion = n.Proportion ?? double.NaN, VisibleDockables = f.CreateList(kids.ToArray()) };
                pd.ActiveDockable = kids.FirstOrDefault(k => k is not ProportionalDockSplitter);
                return pd;
            }
            default:
                return null;
        }
    }

    private static void Float(ShellDockFactory f, IRootDock root, FloatingNode w, List<string> items)
    {
        // park the items in the docked tree, then split the first out to a window and move the rest in with it
        var placed = new List<IDockable>();
        foreach (var id in items)
        {
            IDock? host = IsMap(id) ? ShellDockFactory.FindDocs(root) : ShellDockFactory.DefaultDockFor(root, id);
            if (host is null) continue;
            IDockable d = IsMap(id) ? f.NewMapDoc(id) : f.NewTool(id);
            f.Attach(d);
            f.AddDockable(host, d);
            placed.Add(d);
        }
        if (placed.Count == 0) return;
        var first = placed[0];
        if (first.Owner is not IDock owner) return;
        double width = Math.Clamp(w.Width, 200, 4000), height = Math.Clamp(w.Height, 150, 3000);
        f.SplitToWindow(owner, first, w.X, w.Y, width, height);
        var target = first.Owner as IDock;
        foreach (var d in placed.Skip(1))
        {
            if (target is null || d.Owner is not IDock from) break;
            f.MoveDockable(from, target, d, null!);
        }
        var active = placed.FirstOrDefault(p => p.Id == w.Active);
        if (active is not null) f.SetActiveDockable(active);
    }
}
