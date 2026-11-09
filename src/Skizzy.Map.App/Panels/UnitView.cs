using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Panels;

/// <summary>
/// The Unit panel (the mockup's renderUnit): symbol, name and class, control state, bearing/range/altitude/heading/
/// speed/RCS, engagement and data link for missiles, magazines, weapons and fuel, damage, the orders section,
/// flight deck, weapons control and symbol size. The orders and weapons-control controls are laid out as in the
/// mockup but read-only until the M4 orders engine. The layout is rebuilt only when the shape changes (another
/// unit, a section appears); values update in place every refresh.
/// </summary>
public sealed class UnitView : UserControl
{
    private readonly AppState _app;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(12, 10) };
    private readonly List<Action> _updaters = new();
    private string _shape = "";

    public UnitView(AppState app)
    {
        _app = app;
        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        AttachedToVisualTree += (_, _) =>
        {
            _app.SlowTick += Refresh;
            _app.SelectionChanged += Refresh;
            _app.SourceChanged += Refresh;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _app.SlowTick -= Refresh;
            _app.SelectionChanged -= Refresh;
            _app.SourceChanged -= Refresh;
        };
    }

    internal string CurrentShape => _shape;

    private ViewSide View => _app.ActiveMap?.View ?? ViewSide.Blue;

    public void Refresh()
    {
        var o = _app.PrimaryTrack;
        string shape = Shape(o);
        if (shape != _shape)
        {
            _shape = shape;
            Build(o);
        }
        foreach (var u in _updaters) u();
    }

    private string Shape(TrackInfo? o)
    {
        if (o is null) return "none";
        var d = o.Details;
        return string.Join("|", o.Id, _app.Selection.Count > 1 ? "multi" : "one", o.Alive, d.Magazines.Count, d.Aam is not null, d.Fuel is not null,
            d.Damage is not null, d.TargetName is not null, d.LinkHolder is not null || d.LinkSelfDestructIn is not null, d.DeckCount > 0, d.Doctrine, d.Emcon,
            d.OnContact, View, d.Extra.Count, _app.Selection.Any(i => _app.Data.Get(i)?.Side == Side.Blue));
    }

    private void Build(TrackInfo? o)
    {
        _root.Children.Clear();
        _updaters.Clear();
        if (o is null)
        {
            _root.Children.Add(Ui.Text("Click a track on the plot or in the list. Shift-click adds to the selection. [ and ] shrink or enlarge the selected symbols.", "note"));
            return;
        }
        var data = _app.Data;
        bool blue = o.Side == Side.Blue;
        var d = o.Details;

        // header: symbol, name, class · side
        var sym = new SymbolView();
        var name = new TextBlock { Text = o.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, LetterSpacing = .36 };
        Ui.BindFont(name, "FontDisplay");
        var sub = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        Ui.BindBrush(sub, "TextDimBrush");
        _root.Children.Add(Ui.H(10, sym, Ui.V(0, name, sub)));
        _updaters.Add(() =>
        {
            sym.Show(o, o.AffiliationFor(View));
            sub.Inlines?.Clear();
            sub.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
            sub.Inlines.Add(new Avalonia.Controls.Documents.Run($"{o.ClassName} · {SideName(o.Side)}"));
            if (!o.Alive)
            {
                sub.Inlines.Add(new Avalonia.Controls.Documents.Run(" · "));
                var lost = new Avalonia.Controls.Documents.Run("lost");
                lost.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, lost.GetResourceObservable("CritBrush"));
                sub.Inlines.Add(lost);
            }
        });

        int n = _app.Selection.Count;
        if (n > 1)
            _root.Children.Add(Ui.Text($"{n} tracks selected. Size buttons apply to all of them.", "note"));
        if (n > 1 && _app.Selection.Any(i => data.Get(i)?.Side == Side.Blue))
        {
            var input = new TextBox { PlaceholderText = "Group name, e.g. Strike A" };
            input.Classes.Add("inp");
            var make = Ui.Button("Make group", () =>
            {
                var nm = string.IsNullOrWhiteSpace(input.Text) ? _app.NextGroupName() : input.Text.Trim();
                _app.MakeGroup(nm, _app.Selection.ToList());
                input.Text = "";
            }, "acc");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(input);
            Grid.SetColumn(make, 1);
            row.Children.Add(make);
            _root.Children.Add(Section("Make a group from the selection", row,
                Ui.Text("Tip: Alt+1…9 puts the selection on a number key. Press the number to select the group again.", "note")));
        }

        // control state and the unit commands
        if (blue)
        {
            var cstate = CState("", o.Control is ControlState.Manual or ControlState.Order ? "manual" : o.Control == ControlState.Agent ? "agent" : "");
            var mission = Ui.Text("", "note");
            mission.TextWrapping = TextWrapping.NoWrap;
            mission.TextTrimming = TextTrimming.CharacterEllipsis;
            _root.Children.Add(Ui.Wrap(6, 6, cstate, mission));
            _updaters.Add(() => { ((TextBlock)cstate.Child!).Text = o.ControlText.ToUpperInvariant(); mission.Text = o.Details.Mission ?? ""; });
            _root.Children.Add(Ui.Wrap(6, 6,
                Disabled(Ui.Button("Take control", () => { }, "acc")),
                Disabled(Ui.Button("Send to…", () => { })),
                Disabled(Ui.Button(o.Vip ? "VIP ✓" : "Mark VIP", () => { })),
                Disabled(Ui.Button("Fly in KSP", () => { }, "ghost")),
                Ui.Button("Center", () => _app.CenterOn(o.Id), "ghost")));
            _root.Children.Add(Ui.Text("Orders arrive with the M4 orders engine: right-click to send, attack or protect will work here and on the plot. Shift-drag box-selects; double-click selects a group.", "note"));
        }
        else
        {
            var cs = CState("", "");
            _root.Children.Add(Ui.Wrap(6, 6, cs, Ui.Button("Center", () => _app.CenterOn(o.Id), "ghost")));
            _updaters.Add(() =>
            {
                var aff = o.AffiliationFor(View);
                var t = (TextBlock)cs.Child!;
                t.Text = aff switch { Affiliation.Unknown => "UNKNOWN", Affiliation.Friend => "FRIEND", Affiliation.Neutral => "NEUTRAL", _ => "HOSTILE" };
                Ui.BindBrush(t, Glyphs.BrushKey(aff));
            });
        }

        // key values
        var kv = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 10, RowSpacing = 8 };
        var vBrg = Kv(kv, 0, 0, "Brg"); var vRng = Kv(kv, 0, 1, "Rng km"); var vAlt = Kv(kv, 0, 2, o.Domain == TrackDomain.Subsurface ? "Depth" : "Alt");
        var vHdg = Kv(kv, 1, 0, "Hdg"); var vSpd = Kv(kv, 1, 1, "Speed"); var vRcs = Kv(kv, 1, 2, "RCS m²");
        _root.Children.Add(kv);
        _updaters.Add(() =>
        {
            double now = data.ViewTime;
            var (x, y) = data.PositionOf(o, now);
            var (rx, ry) = data.ReferencePoint(now);
            float alt = o.AltitudeM, hdg = o.HeadingDeg, spd = o.SpeedMps;
            if (data.InReplay && o.History.TrySample(now, out _, out _, out var a2, out var h2, out var s2)) { alt = a2; hdg = h2; spd = s2; }
            vBrg.Text = MapData.Pad3(MapData.Bearing(rx, ry, x, y));
            vRng.Text = Math.Sqrt((x - rx) * (x - rx) + (y - ry) * (y - ry)).ToString("0.0", CultureInfo.InvariantCulture);
            var at = MapData.AltText(o, alt);
            vAlt.Text = at.Length == 0 ? "surface" : at;
            vHdg.Text = MapData.Pad3(hdg);
            vSpd.Text = Math.Round(spd).ToString("0", CultureInfo.InvariantCulture) + " m/s";
            vRcs.Text = o.Rcs >= 1 ? Math.Round(o.Rcs).ToString("#,0", CultureInfo.InvariantCulture) : o.Rcs.ToString("0.###", CultureInfo.InvariantCulture);
        });

        // missiles: engagement and data link
        if (d.TargetName is not null)
        {
            var eng = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _root.Children.Add(Section("Engagement", eng));
            _updaters.Add(() => eng.Text = $"Target {o.Details.TargetName}" + (o.Details.TimeToGo is double ttg ? $" · time to go {Math.Round(ttg).ToString(CultureInfo.InvariantCulture)} s" : ""));
        }
        if (d.LinkHolder is not null || d.LinkSelfDestructIn is not null)
        {
            var link = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _root.Children.Add(Section("Data link", link));
            _updaters.Add(() =>
            {
                if (o.Details.LinkHolder is { } h) { link.Text = "Guided by " + h; Ui.BindBrush(link, "TextBrush"); }
                else { link.Text = $"Lost · self-destruct in {Math.Round(o.Details.LinkSelfDestructIn ?? 0).ToString(CultureInfo.InvariantCulture)} s"; Ui.BindBrush(link, "CritBrush"); }
            });
        }

        if (d.Magazines.Count > 0)
        {
            var rows = new StackPanel { Spacing = 6 };
            var bars = d.Magazines.Select(m => { var b = new MagBar(m.Name); rows.Children.Add(b); return b; }).ToList();
            _root.Children.Add(Section("Magazine", rows));
            _updaters.Add(() =>
            {
                for (int i = 0; i < bars.Count && i < o.Details.Magazines.Count; i++)
                {
                    var m = o.Details.Magazines[i];
                    bars[i].Set(m.Max > 0 ? (double)m.Left / m.Max : 0, m.Left == 0 ? "empty" : $"{m.Left}/{m.Max}", m.Left == 0);
                }
            });
        }
        if (d.Aam is not null)
        {
            var aam = new MagBar("AAM");
            var fuel = new MagBar("Fuel");
            _root.Children.Add(Section("Weapons & fuel", aam, fuel));
            _updaters.Add(() =>
            {
                int a = o.Details.Aam ?? 0, mx = o.Details.AamMax ?? 6;
                aam.Set(mx > 0 ? (double)a / mx : 0, $"{a}/{mx}", a == 0);
                float f = o.Details.Fuel ?? 0;
                fuel.Set(f, Math.Round(f * 100).ToString(CultureInfo.InvariantCulture) + "%", false);
                fuel.IsVisible = o.Details.Fuel is not null;
            });
        }
        else if (d.Fuel is not null)
        {
            var fuel = new MagBar("Fuel");
            _root.Children.Add(Section("Fuel", fuel));
            _updaters.Add(() => { float f = o.Details.Fuel ?? 0; fuel.Set(f, Math.Round(f * 100).ToString(CultureInfo.InvariantCulture) + "%", false); });
        }
        if (d.Damage is not null)
        {
            var hull = new MagBar("Hull", damage: true);
            _root.Children.Add(Section("Damage", hull));
            _updaters.Add(() => { float dm = o.Details.Damage ?? 0; hull.Set(dm, Math.Round(dm * 100).ToString(CultureInfo.InvariantCulture) + "%", false); });
        }

        // live host extras (situation, body, ...)
        if (d.Extra.Count > 0)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 4 };
            var vals = new List<TextBlock>();
            for (int i = 0; i < d.Extra.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var k = Ui.Text(d.Extra[i].Key, "data", "dim");
                var v = Ui.Text(d.Extra[i].Value, "data");
                v.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
                grid.Children.Add(k); grid.Children.Add(v);
                vals.Add(v);
            }
            _root.Children.Add(Section("Vessel", grid));
            _updaters.Add(() => { for (int i = 0; i < vals.Count && i < o.Details.Extra.Count; i++) vals[i].Text = o.Details.Extra[i].Value; });
        }

        // orders for the selection (read-only until M4)
        if (blue && o.Alive && !o.IsMissile)
        {
            _root.Children.Add(Section("Orders for the selection",
                Ui.Wrap(6, 6, Disabled(Ui.Button("Attack nearest", () => { }, "acc")), Disabled(Ui.Button("RTB & land", () => { }, "acc")), Disabled(Ui.Button("Racetrack…", () => { }))),
                Ui.Text("Attack nearest (A), RTB & land (R) and racetrack patrols (T) are shown where they will live; they need the orders engine (M4). Until then units follow their missions and BDArmory AI.", "note")));
        }

        if (blue && d.DeckCount > 0)
        {
            var deck = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var names = Ui.Text("", "note");
            _root.Children.Add(Section("Flight deck", deck, names, Ui.Wrap(6, 6, Disabled(Ui.Button("Launch ready aircraft", () => { }, "acc")))));
            _updaters.Add(() =>
            {
                var dd = o.Details;
                deck.Text = (dd.DeckCount > 0 ? $"{dd.DeckCount} aircraft on deck, {dd.DeckReady} ready" : "Deck empty") +
                            (dd.DeckCount > dd.DeckReady ? $" · {dd.DeckCount - dd.DeckReady} rearming (90 s)" : "");
                names.Text = dd.DeckNames ?? "";
            });
        }

        if (blue && d.Doctrine is not null)
        {
            var items = new List<Control> { SegReadOnly(["Free", "Tight", "Hold"], d.Doctrine) };
            if (d.Emcon is not null)
            {
                var l = Ui.Label("Radar"); l.Margin = new Thickness(0, 6, 0, 0);
                items.Add(l);
                items.Add(SegReadOnly(["Active", "Passive"], d.Emcon));
                items.Add(Ui.Text("Hold or passive radar stops SAM fire. Close-in guns keep defending the ship.", "note"));
            }
            else
            {
                items.Add(Ui.Text("Free = guard mode: hunts and engages hostiles. Tight = assigned targets and close threats only. Hold = passive: does not fire.", "note"));
                if (d.OnContact is not null)
                {
                    var l = Ui.Label("When a new hostile appears during an order"); l.Margin = new Thickness(0, 6, 0, 0); l.TextWrapping = TextWrapping.Wrap;
                    items.Add(l);
                    items.Add(SegReadOnly(["Stay on task", "Engage on contact"], d.OnContact == "engage" ? "Engage on contact" : "Stay on task"));
                }
            }
            items.Add(Ui.Text("Shown read-only: changing weapons control is an order (M4).", "note"));
            _root.Children.Add(Section("Weapons control", items.ToArray()));
        }

        var mul = Ui.Text("", "note");
        _root.Children.Add(Section("Symbol size", Ui.Wrap(6, 6,
            Ui.Button("Smaller", () => { _app.SizeSelection(.8f); }, "ghost"),
            Ui.Button("Larger", () => { _app.SizeSelection(1.25f); }, "ghost"),
            Ui.Button("Reset", () => { _app.SizeSelection(0); }, "ghost"), mul)));
        _updaters.Add(() => mul.Text = "×" + _app.SizeMul.GetValueOrDefault(o.Id, 1f).ToString("0.00", CultureInfo.InvariantCulture));
    }

    private static string SideName(Side s) => s switch { Side.Blue => "Blue", Side.Red => "Red", Side.Green => "Green", _ => "Other" };

    private static T Disabled<T>(T c) where T : Control
    {
        c.IsEnabled = false;
        ToolTip.SetTip(c, "Orders arrive in M4");
        ToolTip.SetShowOnDisabled(c, true);
        return c;
    }

    private static Border CState(string text, string kind)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.26 };
        Ui.BindFont(t, "FontDisplay");
        var b = new Border { Child = t, Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        switch (kind)
        {
            case "manual":
                Ui.BindBrush(t, "AccentBrush");
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("ManualBorderBrush"));
                b.Bind(Border.BackgroundProperty, b.GetResourceObservable("ManualFillBrush"));
                break;
            case "agent":
                Ui.BindBrush(t, "FriendBrush");
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("AgentBorderBrush"));
                break;
            default:
                Ui.BindBrush(t, "TextDimBrush");
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("LineHiBrush"));
                break;
        }
        return b;
    }

    private static TextBlock Kv(Grid g, int row, int col, string label)
    {
        var l = Ui.Label(label);
        var v = new TextBlock { FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.BindFont(v, "FontData");
        var cell = Ui.V(3, l, v);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, col);
        g.Children.Add(cell);
        return v;
    }

    private static StackPanel Section(string label, params Control[] body)
    {
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Ui.Label(label));
        p.Children.AddRange(body);
        return p;
    }

    private static Segmented SegReadOnly(string[] options, string current)
    {
        var s = Ui.Seg(options, Array.IndexOf(options, current), _ => { }, uiFont: true);
        s.IsEnabled = false;
        s.HorizontalAlignment = HorizontalAlignment.Left;
        return s;
    }
}

/// <summary>The mockup's .mag row: name, 6 px bar, value (crit when empty).</summary>
public sealed class MagBar : Grid
{
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
    private double _frac;

    public MagBar(string name, bool damage = false)
    {
        ColumnDefinitions = new ColumnDefinitions("52,*,54");
        ColumnSpacing = 8;
        var n = new TextBlock { Text = name, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        Ui.BindFont(n, "FontData");
        Ui.BindFont(_value, "FontData");
        Ui.BindBrush(_value, "TextDimBrush");
        _track.Bind(Border.BackgroundProperty, _track.GetResourceObservable("Ink3Brush"));
        _fill.Bind(Border.BackgroundProperty, _fill.GetResourceObservable(damage ? "WarnBrush" : "FriendBrush"));
        _track.Child = _fill;
        _track.SizeChanged += (_, _) => Layout();
        Children.Add(n);
        Grid.SetColumn(_track, 1);
        Children.Add(_track);
        Grid.SetColumn(_value, 2);
        Children.Add(_value);
    }

    public string ValueText => _value.Text ?? "";

    public void Set(double frac, string value, bool empty)
    {
        _frac = Math.Clamp(double.IsFinite(frac) ? frac : 0, 0, 1);
        _value.Text = value;
        Ui.BindBrush(_value, empty ? "CritBrush" : "TextDimBrush");
        Layout();
    }

    private void Layout() => _fill.Width = _track.Bounds.Width * _frac;
}
