using System.Globalization;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Data;

/// <summary>Message log categories, as in the mockup's Messages tab, plus System for the map's own notices.</summary>
public enum LogCategory { Contact, Weapons, Intercept, Damage, Orders, Comms, System }

public readonly record struct LogEntry(long Seq, double T, LogCategory Category, string Text, double? X, double? Y);

/// <summary>Who sent a comms message (colours the sender line).</summary>
public enum CommsSide { Blue, Red, Unknown, You }

public readonly record struct CommsEntry(long Seq, double T, string Channel, string Who, CommsSide Side, string Text, bool Unverified);

/// <summary>Bounded message log. Oldest entries drop off in blocks once the cap is reached.</summary>
public sealed class MessageLog
{
    private readonly List<LogEntry> _entries = new();
    private long _seq;

    public MessageLog(int capacity = 10_000) { Capacity = capacity; }

    public int Capacity { get; }
    public IReadOnlyList<LogEntry> Entries => _entries;
    /// <summary>Bumps on every change; panels re-render when it moves.</summary>
    public long Version { get; private set; }

    public LogEntry Add(double t, LogCategory cat, string text, double? x = null, double? y = null)
    {
        var e = new LogEntry(++_seq, t, cat, text, x, y);
        _entries.Add(e);
        if (_entries.Count > Capacity) _entries.RemoveRange(0, Math.Max(1, Capacity / 10));
        Version++;
        return e;
    }

    public int CountOf(LogCategory c)
    {
        int n = 0;
        foreach (var e in _entries) if (e.Category == c) n++;
        return n;
    }

    public void Clear() { _entries.Clear(); Version++; }

    public static string CategoryName(LogCategory c) => c switch
    {
        LogCategory.Contact => "contact",
        LogCategory.Weapons => "weapons",
        LogCategory.Intercept => "intercept",
        LogCategory.Damage => "damage",
        LogCategory.Orders => "orders",
        LogCategory.Comms => "comms",
        _ => "system",
    };

    public static string CategoryTitle(LogCategory c) => c switch
    {
        LogCategory.Contact => "Contacts",
        LogCategory.Weapons => "Weapons",
        LogCategory.Intercept => "Intercepts",
        LogCategory.Damage => "Damage & losses",
        LogCategory.Orders => "Orders",
        LogCategory.Comms => "Comms",
        _ => "System",
    };
}

/// <summary>One column of the Losses tab: losses by class, then expenditure and counters.</summary>
public sealed class SideLosses
{
    public List<KeyValuePair<string, int>> Lost { get; } = new();
    public List<KeyValuePair<string, string>> Lines { get; } = new();
}

public sealed class LossReport
{
    public SideLosses Blue { get; } = new();
    public SideLosses Red { get; } = new();
    public long Version { get; set; }
    public string? Note { get; set; }
}

/// <summary>Live host and data-source health for the Host health panel.</summary>
public sealed record HealthSnapshot(
    string Source,
    string State,
    string? Endpoint,
    string? HostVersion,
    double? LatencyMs,
    double? UpdateHz,
    int Tracks,
    long Messages,
    IReadOnlyList<KeyValuePair<string, string>> Extra);

/// <summary>
/// The map's mirror of the world: tracks with history, the message log, comms, events, losses, zones and the
/// pause-on-event state. One instance per app; every map window reads it on the UI thread.
/// </summary>
/// <summary>
/// The current orders as the map draws them (doc 09 §2.1.2): one <see cref="PlotOrder"/> per unit with an order,
/// plus the shared drawn routes and racetracks. The source fills it (demo: from the C2 engine; live: from order
/// events); <see cref="SceneSync"/> copies it into each window's scene without allocating. Objects are pooled.
/// </summary>
public sealed class OrderPicture
{
    private readonly List<PlotOrder> _orderPool = new();
    private readonly List<PlotRoute> _routePool = new();
    private readonly List<PlotRacetrack> _trackPool = new();

    public List<PlotOrder> Orders { get; } = new();
    public List<PlotRoute> Routes { get; } = new();
    public List<PlotRacetrack> Racetracks { get; } = new();

    /// <summary>Incremented by <see cref="Commit"/> and <see cref="Clear"/>.</summary>
    public long Version { get; private set; }

    /// <summary>Starts a rebuild: empties the lists (pooled objects are reused by the Add calls).</summary>
    public void Begin()
    {
        Orders.Clear();
        Routes.Clear();
        Racetracks.Clear();
    }

    public PlotOrder AddOrder()
    {
        if (_orderPool.Count <= Orders.Count) _orderPool.Add(new PlotOrder());
        var o = _orderPool[Orders.Count];
        o.TrackId = 0; o.Type = OrderType.Move; o.Destination = default; o.TargetTrackId = 0; o.RouteId = 0; o.RouteIndex = 0;
        o.RouteOffset = default; o.PatternId = 0; o.RtbPhase = RtbPhase.Inbound; o.MarshalAsternKm = 8f;
        Orders.Add(o);
        return o;
    }

    public PlotRoute AddRoute(int id)
    {
        if (_routePool.Count <= Routes.Count) _routePool.Add(new PlotRoute());
        var r = _routePool[Routes.Count];
        r.Id = id;
        r.Points.Clear();
        Routes.Add(r);
        return r;
    }

    public PlotRacetrack AddRacetrack()
    {
        if (_trackPool.Count <= Racetracks.Count) _trackPool.Add(new PlotRacetrack());
        var r = _trackPool[Racetracks.Count];
        Racetracks.Add(r);
        return r;
    }

    public void Commit() => Version++;

    public void Clear()
    {
        Begin();
        Version++;
    }
}

public sealed class MapData
{
    private readonly List<TrackInfo> _tracks = new();
    private readonly Dictionary<int, TrackInfo> _byId = new();
    private readonly Dictionary<string, TrackInfo> _byKey = new(StringComparer.Ordinal);
    private int _nextId = 1;
    private long _commsSeq;

    public MapData(int historyCapacity = 1200, double historyInterval = 1.0)
    {
        HistoryCapacity = historyCapacity;
        HistoryInterval = historyInterval;
    }

    public int HistoryCapacity { get; }
    public double HistoryInterval { get; }

    public IReadOnlyList<TrackInfo> Tracks => _tracks;
    /// <summary>Bumps when tracks are added, removed or die (lists re-sort on it).</summary>
    public long Revision { get; private set; }

    public MessageLog Log { get; } = new();
    public List<CommsEntry> Comms { get; } = new();
    public long CommsVersion { get; private set; }
    public List<PlotEvent> Events { get; } = new();
    public LossReport Losses { get; } = new();
    public List<PlotZone> Zones { get; } = new();

    /// <summary>The current orders (drawn on the plot in live view).</summary>
    public OrderPicture Orders { get; } = new();

    /// <summary>The track bearings and ranges are measured from (the flagship). 0 = plot origin.</summary>
    public int ReferenceTrackId { get; set; }

    /// <summary>Latest source time, seconds on the map clock (0 = when the source started).</summary>
    public double LiveTime { get; set; }
    /// <summary>Replay position, or null when showing live.</summary>
    public double? ReplayTime { get; set; }
    public double ViewTime => ReplayTime ?? LiveTime;
    public bool InReplay => ReplayTime is not null;

    /// <summary>Earliest time the replay scrubber can reach (bounded by the history rings).</summary>
    public double ReplayStart { get; set; }

    /// <summary>Universal time at map time 0, for the clock. NaN when the source has no UT.</summary>
    public double Epoch { get; set; } = double.NaN;
    /// <summary>Length of a day in seconds for clock text (Kerbin: 6 h).</summary>
    public double DayLength { get; set; } = 21600;

    /// <summary>Text of the pause-on-event banner, or null when it is hidden.</summary>
    public string? PauseBanner { get; set; }

    /// <summary>Map-side group names for own units ("make group", Alt+digit). Overrides the source group.</summary>
    public Dictionary<int, string> GroupOverrides { get; } = new();

    public string GroupOf(TrackInfo t) => GroupOverrides.TryGetValue(t.Id, out var g) ? g : t.Group;

    public TrackInfo? Get(int id) => _byId.GetValueOrDefault(id);

    public TrackInfo? GetByKey(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Finds the track for a source key or creates it (born at <paramref name="t"/>).</summary>
    public TrackInfo GetOrAdd(string key, double t, out bool created)
    {
        if (_byKey.TryGetValue(key, out var tr)) { created = false; return tr; }
        tr = new TrackInfo(_nextId++, key, HistoryCapacity) { BornT = t };
        _tracks.Add(tr);
        _byId[tr.Id] = tr;
        _byKey[key] = tr;
        Revision++;
        created = true;
        return tr;
    }

    /// <summary>Adds a track with a fixed id (the demo keeps the scenario's ids so "Kestrel is 1" holds).</summary>
    public TrackInfo AddWithId(int id, string key, double t)
    {
        var tr = new TrackInfo(id, key, HistoryCapacity) { BornT = t };
        _tracks.Add(tr);
        _byId[id] = tr;
        _byKey[key] = tr;
        _nextId = Math.Max(_nextId, id + 1);
        Revision++;
        return tr;
    }

    public void MarkDead(TrackInfo t, double when)
    {
        if (!t.Alive) return;
        t.Alive = false;
        t.DiedT = when;
        t.RecordNow();
        Revision++;
    }

    public void TouchRevision() => Revision++;

    /// <summary>Drops dead tracks that left the replay window, and old events.</summary>
    public void Prune()
    {
        double keep = LiveTime - HistoryCapacity * HistoryInterval;
        if (_tracks.RemoveAll(t => !t.Alive && t.DiedT < keep && Drop(t)) > 0) Revision++;
        int ev = Events.FindIndex(e => e.Time >= keep);
        if (ev > 0) Events.RemoveRange(0, ev);
        if (Events.Count > 20_000) Events.RemoveRange(0, Events.Count - 20_000);
        ReplayStart = Math.Max(ReplayStart, keep);
    }

    private bool Drop(TrackInfo t)
    {
        _byId.Remove(t.Id);
        _byKey.Remove(t.Key);
        return true;
    }

    public void AddComms(double t, string channel, string who, CommsSide side, string text, bool unverified = false)
    {
        Comms.Add(new CommsEntry(++_commsSeq, t, channel, who, side, text, unverified));
        if (Comms.Count > 2000) Comms.RemoveRange(0, 200);
        CommsVersion++;
    }

    public void AddEvent(PlotEventKind kind, double x, double y, double t) => Events.Add(new PlotEvent(kind, (float)x, (float)y, t));

    /// <summary>Clears everything (source restart or switch).</summary>
    public void Reset()
    {
        _tracks.Clear();
        _byId.Clear();
        _byKey.Clear();
        _nextId = 1;
        Log.Clear();
        Comms.Clear();
        CommsVersion++;
        Events.Clear();
        Losses.Blue.Lost.Clear(); Losses.Blue.Lines.Clear(); Losses.Red.Lost.Clear(); Losses.Red.Lines.Clear();
        Losses.Note = null;
        Losses.Version++;
        Zones.Clear();
        Orders.Clear();
        GroupOverrides.Clear();
        LiveTime = 0;
        ReplayTime = null;
        ReplayStart = 0;
        PauseBanner = null;
        ReferenceTrackId = 0;
        Revision++;
    }

    // ------------------------------------------------------------ geometry helpers for panels

    public (double X, double Y) ReferencePoint(double t)
    {
        var r = Get(ReferenceTrackId);
        return r is null ? (0, 0) : PositionOf(r, t);
    }

    /// <summary>Position at time t: live extrapolation at the live edge, history interpolation in replay.</summary>
    public (double X, double Y) PositionOf(TrackInfo tr, double t)
    {
        if (ReplayTime is null || t >= LiveTime - 1e-6) return tr.PositionAt(t);
        return tr.History.TrySample(t, out var x, out var y, out _, out _, out _) ? (x, y) : (tr.X, tr.Y);
    }

    public static double Bearing(double fx, double fy, double tx, double ty) =>
        ((90 - Math.Atan2(ty - fy, tx - fx) * 180 / Math.PI) % 360 + 360) % 360;

    public static string Pad3(double deg) => ((int)Math.Floor(deg + .5) % 360).ToString("000", CultureInfo.InvariantCulture);

    /// <summary>Altitude text as in the mockup: depth for subs, nothing for ships, metres below 1 km, else "4.5k".</summary>
    public static string AltText(TrackInfo t, float altM)
    {
        if (t.Domain == TrackDomain.Subsurface) return "−" + Math.Abs(Math.Round(altM)).ToString("0", CultureInfo.InvariantCulture) + " m";
        if (t.Domain == TrackDomain.Surface) return "";
        if (altM < 1000) return Math.Round(altM).ToString("0", CultureInfo.InvariantCulture) + " m";
        return (altM / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k";
    }

    /// <summary>Clock text hh:mm:ss for map time t (UT-based when the source has an epoch).</summary>
    public string ClockText(double t)
    {
        double s = double.IsNaN(Epoch) ? t : Epoch + t;
        if (s < 0) s = 0;
        double day = DayLength > 0 ? DayLength : 86400;
        int h = (int)Math.Floor(s % day / 3600), m = (int)Math.Floor(s % 3600 / 60), ss = (int)Math.Floor(s % 60);
        return $"{h:00}:{m:00}:{ss:00}";
    }

    /// <summary>"Y1 D014 03:12:00" (Kerbin calendar: 426-day years).</summary>
    public string UtText(double t)
    {
        if (double.IsNaN(Epoch)) return "T+" + ElapsedText(t);
        double s = Math.Max(0, Epoch + t);
        double day = DayLength > 0 ? DayLength : 86400;
        long d = (long)Math.Floor(s / day);
        long yearDays = day == 21600 ? 426 : 365;
        long y = d / yearDays + 1, dd = d % yearDays + 1;
        return $"Y{y} D{dd:000} {ClockText(t)}";
    }

    /// <summary>"12:05" minutes:seconds since map time 0 (the timeline's T+).</summary>
    public static string ElapsedText(double t)
    {
        if (t < 0) t = 0;
        return $"{(int)Math.Floor(t / 60)}:{(int)Math.Floor(t % 60):00}";
    }
}
