using System.Diagnostics;
using Avalonia.Styling;
using Skizzy.Map.App.Map;
using Skizzy.Map.App.Services;
using Skizzy.Map.App.Theme;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.App;

/// <summary>Track list filters (the mockup's TF table).</summary>
public enum TrackFilter { All, Air, Missiles, Ships, Inbound }

/// <summary>
/// Application state shared by every panel and map window: the data source and mirror, the selection, number-key
/// groups, filters, the play/replay controls and the theme. UI thread only. Panels subscribe to the events and
/// refresh themselves; the frame loop calls <see cref="Frame"/> once per display frame.
/// </summary>
public sealed class AppState
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _slowAcc;
    private ThemeVariant _theme = ThemeVariant.Dark;

    public AppState(IDataSource? source = null, SettingsStore? settings = null)
    {
        Settings = settings ?? new SettingsStore(SettingsStore.DefaultRoot());
        Data = new MapData(historyCapacity: 1200);
        Source = source ?? new DemoBattleSource();
        Source.Attach(Data);
    }

    public SettingsStore Settings { get; }
    public MapData Data { get; }
    public IDataSource Source { get; private set; }

    // ------------------------------------------------------------ events

    /// <summary>After every frame (source ticked, maps synced).</summary>
    public event Action? Ticked;
    /// <summary>About every 0.4 s: lists and panels refresh (the mockup's uiT cadence).</summary>
    public event Action? SlowTick;
    public event Action? SelectionChanged;
    public event Action? GroupsChanged;
    public event Action? SourceChanged;
    public event Action? ThemeChanged;
    public event Action<string>? ToastRequested;
    public event Action? MapsChanged;
    public event Action? FiltersChanged;

    // ------------------------------------------------------------ maps

    public List<MapViewModel> Maps { get; } = new();
    public MapViewModel? ActiveMap { get; private set; }

    public MapViewModel CreateMap(string id, string? title = null)
    {
        var m = new MapViewModel(this, id, title ?? TitleFor(id));
        Maps.Add(m);
        ActiveMap ??= m;
        MapsChanged?.Invoke();
        return m;
    }

    public MapViewModel GetOrCreateMap(string id) => Maps.FirstOrDefault(m => m.Id == id) ?? CreateMap(id);

    public void RemoveMap(MapViewModel m)
    {
        Maps.Remove(m);
        if (ActiveMap == m) ActiveMap = Maps.FirstOrDefault();
        m.Dispose();
        MapsChanged?.Invoke();
    }

    public void Activate(MapViewModel m)
    {
        if (ActiveMap == m) return;
        ActiveMap = m;
        MapsChanged?.Invoke();
    }

    public string NextMapId()
    {
        for (int i = 1; ; i++)
            if (Maps.All(m => m.Id != "map:" + i)) return "map:" + i;
    }

    private static string TitleFor(string id) => id == "map:1" ? "Plot" : "Plot " + id.Split(':').Last();

    // ------------------------------------------------------------ theme

    public ThemeVariant Theme => _theme;
    public Palette Palette => Palette.For(_theme);

    public void SetTheme(ThemeVariant v)
    {
        if (v == _theme) return;
        _theme = v;
        foreach (var m in Maps) m.ApplyTheme(Palette);
        ThemeChanged?.Invoke();
    }

    // ------------------------------------------------------------ selection

    private readonly HashSet<int> _selection = new();
    public IReadOnlySet<int> Selection => _selection;
    public int Primary { get; private set; }

    /// <summary>Per-track symbol size multipliers ([ and ]).</summary>
    public Dictionary<int, float> SizeMul { get; } = new();

    public TrackInfo? PrimaryTrack => Primary != 0 && _selection.Contains(Primary) ? Data.Get(Primary)
        : _selection.Count > 0 ? Data.Get(_selection.First()) : null;

    public void Select(IEnumerable<int> ids, int primary = 0)
    {
        _selection.Clear();
        foreach (var id in ids) _selection.Add(id);
        Primary = primary != 0 ? primary : _selection.FirstOrDefault();
        SelectionChanged?.Invoke();
    }

    public void SelectOne(int id) => Select([id], id);

    public void ToggleSelect(int id)
    {
        if (!_selection.Remove(id)) _selection.Add(id);
        Primary = id;
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        Primary = 0;
        SelectionChanged?.Invoke();
    }

    /// <summary>[ / ] / reset: scales the selected symbols (×1.25, ×0.8, 0 = reset), clamped to 0.4..3.</summary>
    public void SizeSelection(float k)
    {
        foreach (var id in _selection)
        {
            float m = SizeMul.GetValueOrDefault(id, 1f);
            m = k == 0 ? 1f : Math.Clamp(m * k, .4f, 3f);
            if (m == 1f) SizeMul.Remove(id); else SizeMul[id] = m;
        }
        SelectionChanged?.Invoke();
    }

    // ------------------------------------------------------------ groups and number keys

    /// <summary>Number keys 1-9 → group name.</summary>
    public Dictionary<string, string> Slots { get; } = new();

    /// <summary>Own (Blue, non-missile, live) units by group, in first-seen order (the mockup's ownGroups).</summary>
    public List<KeyValuePair<string, List<TrackInfo>>> OwnGroups()
    {
        var map = new Dictionary<string, List<TrackInfo>>();
        var order = new List<string>();
        foreach (var t in Data.Tracks)
        {
            if (!t.Alive || t.Side != Side.Blue || t.IsMissile) continue;
            var g = Data.GroupOf(t);
            if (!map.TryGetValue(g, out var l)) { map[g] = l = new List<TrackInfo>(); order.Add(g); }
            l.Add(t);
        }
        return order.Select(g => new KeyValuePair<string, List<TrackInfo>>(g, map[g])).ToList();
    }

    public const string AllAircraft = "__air";

    public void SelectGroup(string name)
    {
        var groups = OwnGroups();
        var list = name == AllAircraft
            ? groups.SelectMany(g => g.Value).Where(t => t.Domain == TrackDomain.Air).ToList()
            : groups.FirstOrDefault(g => g.Key == name).Value ?? new List<TrackInfo>();
        if (list.Count == 0) return;
        Select(list.Select(t => t.Id), list[0].Id);
        Toast($"{list.Count} units selected.");
    }

    /// <summary>Double-click: the whole group of a track (own units by map group; hostiles by their source group).</summary>
    public void SelectGroupOf(TrackInfo t)
    {
        if (t.Side == Side.Blue) { SelectGroup(Data.GroupOf(t)); return; }
        if (string.IsNullOrEmpty(t.Group)) { SelectOne(t.Id); return; }
        Select(Data.Tracks.Where(x => x.Alive && x.Side == t.Side && x.Group == t.Group && !x.IsMissile).Select(x => x.Id), t.Id);
    }

    /// <summary>"Make group": names the selected own units (map-side grouping, not an order).</summary>
    public string? MakeGroup(string name, IEnumerable<int> ids)
    {
        var units = ids.Select(Data.Get).Where(u => u is { Alive: true, Side: Side.Blue, IsMissile: false }).Cast<TrackInfo>().ToList();
        if (units.Count == 0) { Toast("Select some of your own units first."); return null; }
        foreach (var u in units) Data.GroupOverrides[u.Id] = name;
        var names = string.Join(", ", units.Take(6).Select(u => u.Name)) + (units.Count > 6 ? "…" : "");
        Data.Log.Add(Data.LiveTime, LogCategory.Orders, $"New group \"{name}\": {units.Count} units ({names}).", units[0].X, units[0].Y);
        Toast($"Group \"{name}\" created with {units.Count} units.");
        GroupsChanged?.Invoke();
        return name;
    }

    private int _groupSeq;

    public string NextGroupName() => $"Group {(char)('A' + _groupSeq++ % 26)}";

    /// <summary>Alt+digit: puts the selection on a number key (making a group when it is not one already).</summary>
    public void AssignSlot(string digit)
    {
        var ids = _selection.Where(i => Data.Get(i)?.Side == Side.Blue).ToList();
        if (ids.Count == 0) { Toast($"Select your units first, then press Alt+{digit}."); return; }
        var gs = ids.Select(i => Data.GroupOf(Data.Get(i)!)).Distinct().ToList();
        string? name = gs[0];
        var own = OwnGroups().FirstOrDefault(g => g.Key == name).Value;
        if (!(gs.Count == 1 && own is not null && own.Count == ids.Count)) name = MakeGroup($"Group {digit}", ids);
        if (name is null) return;
        Slots[digit] = name;
        GroupsChanged?.Invoke();
        Toast($"\"{name}\" is on key {digit}. Press {digit} to select it.");
    }

    public void RecallSlot(string digit)
    {
        if (Slots.TryGetValue(digit, out var g)) SelectGroup(g);
    }

    // ------------------------------------------------------------ filters (track list and messages)

    public TrackFilter TrackListFilter { get; private set; } = TrackFilter.All;

    public void SetTrackFilter(TrackFilter f)
    {
        TrackListFilter = f;
        FiltersChanged?.Invoke();
    }

    public static bool Passes(TrackFilter f, TrackInfo t) => f switch
    {
        TrackFilter.Air => t.Domain == TrackDomain.Air && !t.IsMissile,
        TrackFilter.Missiles => t.IsMissile,
        TrackFilter.Ships => t.Domain != TrackDomain.Air,
        TrackFilter.Inbound => t.Side == Side.Red && (t.Kind == TrackKind.AntiShipMissile || t.Kind == TrackKind.Uav),
        _ => true,
    };

    public HashSet<LogCategory> LogOn { get; } = [LogCategory.Contact, LogCategory.Weapons, LogCategory.Intercept, LogCategory.Damage, LogCategory.Orders, LogCategory.Comms, LogCategory.System];
    public string CommsChannel { get; set; } = "All";
    public int UnreadComms { get; set; }
    private long _seenComms;

    public void MarkCommsRead() { UnreadComms = 0; _seenComms = Data.CommsVersion; }

    // ------------------------------------------------------------ time: play, speed, banner, replay

    public static readonly double[] Speeds = [.25, .5, 1, 2, 4, 8];

    public bool Paused => Source.Paused;

    public void TogglePlay()
    {
        if (Data.InReplay) GoLive();
        Source.SetPaused(!Source.Paused);
        if (!Source.Paused) Data.PauseBanner = null;
    }

    public void Resume()
    {
        Source.SetPaused(false);
        Data.PauseBanner = null;
    }

    public void DontPauseAgain()
    {
        if (Source is DemoBattleSource d) d.DisablePauseOnEvents();
        Resume();
    }

    public void Restart()
    {
        Source.Restart();
        ClearSelection();
        Slots.Clear();
        SizeMul.Clear();
        UnreadComms = 0;
        _seenComms = 0;
        GroupsChanged?.Invoke();
        foreach (var m in Maps) m.FitAll(animate: false);
        SourceChanged?.Invoke();
    }

    public void GoLive() => Data.ReplayTime = null;

    /// <summary>Scrubs to time t (seconds). At or past the live edge it returns to live.</summary>
    public void ScrubTo(double t)
    {
        if (t >= Data.LiveTime - 0.25) { GoLive(); return; }
        Data.ReplayTime = Math.Max(ReplayFloor, t);
    }

    public double ReplayFloor => Math.Max(Data.ReplayStart, Data.LiveTime - Data.HistoryCapacity * Data.HistoryInterval);

    // ------------------------------------------------------------ data source

    public void SwitchSource(IDataSource source)
    {
        var old = Source;
        Source = source;
        ClearSelection();
        Slots.Clear();
        SizeMul.Clear();
        source.Attach(Data);
        old.Dispose();
        _seenComms = 0;
        UnreadComms = 0;
        GroupsChanged?.Invoke();
        SourceChanged?.Invoke();
        foreach (var m in Maps) m.FitAll(animate: false);
    }

    // ------------------------------------------------------------ toast and focus

    public void Toast(string text) => ToastRequested?.Invoke(text);

    /// <summary>Centres the active map on a track (list double-click, message jump).</summary>
    public void CenterOn(int id)
    {
        var t = Data.Get(id);
        if (t is null || ActiveMap is null) return;
        var (x, y) = Data.PositionOf(t, Data.ViewTime);
        ActiveMap.CenterOn(x, y);
    }

    // ------------------------------------------------------------ frame loop

    /// <summary>Wall time of the last frame, s (for panels that animate).</summary>
    public double Now => _clock.Elapsed.TotalSeconds;

    public long Frames { get; private set; }

    /// <summary>One display frame: advance the source, sync and repaint every map, and fire the slow tick.</summary>
    public void Frame(double dt)
    {
        Frames++;
        dt = Math.Clamp(dt, 0, 0.5);
        Source.Tick(dt);
        if (Data.CommsVersion != _seenComms)
        {
            UnreadComms += (int)Math.Max(0, Data.CommsVersion - _seenComms);
            _seenComms = Data.CommsVersion;
        }
        // selections of dead tracks fall away (the mockup filters ids by byId)
        if (_selection.Count > 0 && !Data.InReplay)
        {
            int before = _selection.Count;
            _selection.RemoveWhere(id => Data.Get(id) is null);
            if (_selection.Count != before) { if (!_selection.Contains(Primary)) Primary = _selection.FirstOrDefault(); SelectionChanged?.Invoke(); }
        }
        foreach (var m in Maps) m.Frame(dt);
        Ticked?.Invoke();
        _slowAcc += dt;
        if (_slowAcc >= .4)
        {
            _slowAcc = 0;
            SlowTick?.Invoke();
        }
    }

    /// <summary>Forces the slow tick now (tests, and after big changes).</summary>
    public void RefreshPanels() => SlowTick?.Invoke();
}
