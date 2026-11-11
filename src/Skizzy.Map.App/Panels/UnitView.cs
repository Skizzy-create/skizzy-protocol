using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Views;
using Skizzy.Map.Data;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App.Panels;

/// <summary>
/// The Unit panel (the mockup's renderUnit): symbol, name and class, control state, bearing/range/altitude/heading/
/// speed/RCS, engagement and data link for missiles, magazines, weapons and fuel, damage, the orders section,
/// flight deck, weapons control and symbol size. Every command goes through <see cref="CommandService"/> (c2.* to
/// the source); a control the source cannot perform is disabled with the reason as its tooltip. The layout is
/// rebuilt only when the shape changes (another unit, a section appears); values and button states update in place.
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
            _app.Commands.Changed += Refresh;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _app.SlowTick -= Refresh;
            _app.SelectionChanged -= Refresh;
            _app.SourceChanged -= Refresh;
            _app.Commands.Changed -= Refresh;
        };
    }

    internal string CurrentShape => _shape;

    private ViewSide View => _app.ActiveMap?.View ?? ViewSide.Blue;

    private CommandService Cmd => _app.Commands;

    /// <summary>Own unit: the panel shows its control state, commands and doctrine.</summary>
    private bool Own(TrackInfo o) => o.Side == Side.Blue || (o.Alive && Cmd.IsOwn(o));

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
            d.Damage is not null, d.TargetName is not null, d.LinkHolder is not null || d.LinkSelfDestructIn is not null, o.Kind == TrackKind.Carrier || d.DeckCount > 0,
            d.Doctrine is not null, d.Emcon is not null, d.OnContact is not null, View, d.Extra.Count, _app.Selection.Any(i => _app.Data.Get(i)?.Side == Side.Blue),
            Own(o), Cmd.CanCommand, Cmd.IsDemo);
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
        bool blue = Own(o);
        var d = o.Details;
        var cmd = Cmd;

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
            var cstate = CState("", "");
            string kind = "";
            var mission = Ui.Text("", "note");
            mission.TextWrapping = TextWrapping.NoWrap;
            mission.TextTrimming = TextTrimming.CharacterEllipsis;
            _root.Children.Add(Ui.Wrap(6, 6, cstate, mission));
            _updaters.Add(() =>
            {
                bool manual = cmd.Manual.Contains(o.Id);
                ((TextBlock)cstate.Child!).Text = (manual ? "Manual · you" : o.ControlText).ToUpperInvariant();
                string k = manual || o.Control is ControlState.Order || cmd.IsControlled(o) ? "manual" : o.Control == ControlState.Agent ? "agent" : "";
                if (k != kind) { kind = k; StyleCState(cstate, k); }
                mission.Text = o.Details.Mission ?? "";
            });
            var take = Ui.Button("Take control", () => { if (cmd.IsControlled(o)) _ = cmd.ReturnToMission(); else _ = cmd.TakeControl(); }, "acc");
            var send = Press("Send to…", cmd.ToggleSendTo);
            var vip = Press("Mark VIP", () => _ = cmd.ToggleVip());
            var fly = Ui.Button("Fly in KSP", () => cmd.FlyInKsp(o), "ghost");
            Exception(fly, cmd.IsDemo ? null : Unsupported.FlyInKsp);
            foreach (var c in new Control[] { take, send, vip }) Exception(c, cmd.CanCommand ? null : Unsupported.Orders);
            _root.Children.Add(Ui.Wrap(6, 6, take, send, vip, fly, Ui.Button("Center", () => _app.CenterOn(o.Id), "ghost")));
            _updaters.Add(() =>
            {
                string t = cmd.IsControlled(o) ? "Return to mission" : "Take control";
                if ((take.Content as string) != t) take.Content = t;
                send.IsChecked = cmd.Pending == PendingMode.SendTo;
                string v = o.Vip ? "VIP ✓" : "Mark VIP";
                if ((vip.Content as string) != v) vip.Content = v;
                vip.IsChecked = o.Vip;
            });
            _root.Children.Add(Ui.Text("Right-click the plot to send the selected units there. Right-click a hostile to attack it. Right-click one of your own units to make the selection protect it. V marks the selection as VIP. Shift-drag to box-select several units.", "note"));
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

        // orders for the selection
        if (blue && o.Alive && !o.IsMissile)
        {
            var hunt = Ui.Button("Attack nearest", () => _ = cmd.AttackNearest(), "acc");
            var rtb = Ui.Button("RTB & land", () => _ = cmd.Rtb(), "acc");
            var track = Press("Racetrack…", () => cmd.ToggleRacetrack(fromKey: false));
            foreach (var c in new Control[] { hunt, rtb, track }) Exception(c, cmd.CanCommand ? null : Unsupported.Orders);
            _updaters.Add(() => track.IsChecked = cmd.Pending == PendingMode.Racetrack);
            _root.Children.Add(Section("Orders for the selection", Ui.Wrap(6, 6, hunt, rtb, track),
                Ui.Text("A (Attack nearest): aircraft hunt the nearest hostiles and missiles nobody is engaging, ships go weapons free at full range, carriers launch their decks; fighters RTB when out of missiles. Shift+A twice = all Blue forces. R sends the selection home: aircraft stack at the marshal point astern of Kestrel and land one at a time; ships return to station. T, then drag on the plot, sets a racetrack patrol: with weapons Free (guard) aircraft leave the pattern to fight anything they detect, then come back. The top-bar RTB all button (or Shift+R twice) sends every aircraft home.", "note")));
        }

        if (blue && (o.Kind == TrackKind.Carrier || d.DeckCount > 0))
        {
            var deck = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var names = Ui.Text("", "note");
            names.TextWrapping = TextWrapping.Wrap;
            var launch = Ui.Button("Launch ready aircraft", () => _ = cmd.LaunchDeck(o), "acc");
            if (!cmd.IsDemo) Exception(launch, Unsupported.DeckLaunch);
            _root.Children.Add(Section("Flight deck", deck, names, Ui.Wrap(6, 6, launch)));
            _updaters.Add(() =>
            {
                var dd = o.Details;
                deck.Text = (dd.DeckCount > 0 ? $"{dd.DeckCount} aircraft on deck, {dd.DeckReady} ready" : "Deck empty") +
                            (dd.DeckCount > dd.DeckReady ? $" · {dd.DeckCount - dd.DeckReady} rearming (90 s)" : "");
                names.Text = dd.DeckNames ?? "";
                if (cmd.IsDemo) launch.IsEnabled = dd.DeckReady > 0 && o.Alive;
            });
        }

        // weapons control: the source's doctrine (demo), or any own vessel on a live host (unit doctrine through c2.*)
        bool liveOwn = blue && !cmd.IsDemo && cmd.CanCommand && o.Alive && !o.IsMissile;
        if (blue && (d.Doctrine is not null || liveOwn))
        {
            string[] wc = ["Free", "Tight", "Hold"];
            var weapons = Ui.Seg(wc, Array.IndexOf(wc, cmd.WeaponsOf(o) ?? ""), i => _ = cmd.SetWeapons(wc[i]), uiFont: true);
            weapons.HorizontalAlignment = HorizontalAlignment.Left;
            _updaters.Add(() => weapons.SetSelected(Array.IndexOf(wc, cmd.WeaponsOf(o) ?? "")));
            var items = new List<Control> { weapons };
            bool ship = d.Emcon is not null || (liveOwn && o.Domain != TrackDomain.Air);
            if (ship)
            {
                string[] em = ["Active", "Passive"];
                var l = Ui.Label("Radar"); l.Margin = new Thickness(0, 6, 0, 0);
                items.Add(l);
                var radar = Ui.Seg(em, Array.IndexOf(em, d.Emcon ?? ""), i => _ = cmd.SetRadar(em[i]), uiFont: true);
                radar.HorizontalAlignment = HorizontalAlignment.Left;
                if (!cmd.IsDemo) Exception(radar, Unsupported.Radar);
                _updaters.Add(() => radar.SetSelected(Array.IndexOf(em, o.Details.Emcon ?? "")));
                items.Add(radar);
                items.Add(Ui.Text("Hold or passive radar stops SAM fire. Close-in guns keep defending the ship.", "note"));
            }
            else
            {
                items.Add(Ui.Text("Free = guard mode: hunts and engages hostiles, and leaves a patrol to fight what it detects. Tight = assigned targets and close threats only. Hold = passive: stays on its pattern and does not fire.", "note"));
                if (d.OnContact is not null || liveOwn)
                {
                    var l = Ui.Label("When a new hostile appears during an order"); l.Margin = new Thickness(0, 6, 0, 0); l.TextWrapping = TextWrapping.Wrap;
                    items.Add(l);
                    string[] oc = ["task", "engage"];
                    var onContact = Ui.Seg(["Stay on task", "Engage on contact"], Array.IndexOf(oc, cmd.OnContactOf(o) ?? "task"), i => _ = cmd.SetOnContact(oc[i]), uiFont: true);
                    onContact.HorizontalAlignment = HorizontalAlignment.Left;
                    _updaters.Add(() => onContact.SetSelected(Array.IndexOf(oc, cmd.OnContactOf(o) ?? "task")));
                    items.Add(onContact);
                    items.Add(Ui.Text("Stay on task: go straight for the assigned target and ignore others crossing the path. Engage on contact: attack any hostile the unit's own radar detects on the way (even one the shared picture missed), then continue the order.", "note"));
                }
            }
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

    /// <summary>A control this source cannot perform: disabled, with the reason as its tooltip (null = enabled).</summary>
    private static void Exception(Control c, string? reason)
    {
        if (reason is null) return;
        c.IsEnabled = false;
        ToolTip.SetTip(c, reason);
        ToolTip.SetShowOnDisabled(c, true);
    }

    /// <summary>A button that shows pressed while its mode is on (the mockup's aria-pressed buttons); the click toggles the mode.</summary>
    private static ToggleButton Press(string text, Action onClick)
    {
        var b = new ToggleButton { Content = text };
        b.Classes.Add("btn");
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Border CState(string text, string kind)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.26 };
        Ui.BindFont(t, "FontDisplay");
        var b = new Border { Child = t, Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        StyleCState(b, kind);
        return b;
    }

    /// <summary>The mockup's .cstate colours: manual/order (accent fill), agent, or plain.</summary>
    private static void StyleCState(Border b, string kind)
    {
        var t = (TextBlock)b.Child!;
        b.ClearValue(Border.BackgroundProperty);
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
                b.ClearValue(Border.BackgroundProperty);
                break;
        }
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
