using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Skizzy.Client;
using Skizzy.Map.Render.Model;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Map.Data.Live;

/// <summary>
/// A live Skizzy host (KSP with the Skizzy plugin, or the in-process FakeKsp host). Connects with SkizzyClient,
/// reads the catalog for the command palette, and subscribes to vessels: the M2 tactical stream when the host has
/// it (<c>source.tactical</c>), otherwise M0's <c>source.query</c> vessel stream at 5 Hz. Latitude and longitude
/// are projected azimuthal-equidistant about the first position seen, so the plot is in km like the demo.
/// Notifications arrive on the client's read thread and are applied on the UI thread in <see cref="Tick"/>.
/// The token is never logged or shown.
/// </summary>
public class HostDataSource : IDataSource, ICommandSource
{
    public static readonly string[] Fields =
        ["type", "situation", "body", "lat", "lon", "altitude", "srfSpeed", "heading", "verticalSpeed", "isActive", "crewCount", "mass"];

    /// <summary>Body radii (km) for the projection; unknown bodies use Kerbin's.</summary>
    private static readonly Dictionary<string, double> Radii = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Kerbin"] = 600, ["Mun"] = 200, ["Minmus"] = 60, ["Duna"] = 320, ["Ike"] = 130, ["Eve"] = 700, ["Gilly"] = 13, ["Laythe"] = 500,
        ["Vall"] = 300, ["Tylo"] = 600, ["Bop"] = 65, ["Pol"] = 44, ["Moho"] = 250, ["Dres"] = 138, ["Eeloo"] = 210, ["Jool"] = 6000,
    };

    private readonly Func<Endpoint> _endpoint;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly CancellationTokenSource _cts = new();
    private SkizzyClient? _client;
    private MapData? _data;
    private string? _sub;
    private string[] _columns = [];
    private double _lastUt = double.NaN;
    private long _lastUtWall;
    private double? _lat0, _lon0;
    private string? _body0;
    private readonly Dictionary<string, int> _missing = new();
    private long _samples;
    private long _sampleWindowStart = Stopwatch.GetTimestamp();
    private long _sampleWindowCount;
    private double _rateHz;
    private double? _rttMs;
    private string? _hostVersion;
    private string _url = "";
    private List<CatalogEntry> _catalog = new();
    private Task? _loop;
    // M2 tactical stream: team -> map side (first team seen Blue, second Red, third Green, the rest Other), the
    // engagement log's last sequence already written to the Messages log, and the per-frame missile scratch
    private readonly Dictionary<string, Side> _teamSides = new(StringComparer.Ordinal);
    private long _loggedSeq;
    private bool _freshSub;
    private readonly List<(TrackInfo Missile, string? Target, string? Launcher)> _missileRows = new();

    public HostDataSource(Func<Endpoint> endpoint, string name = "Live host", SourceKind kind = SourceKind.LiveHost, string? description = null)
    {
        _endpoint = endpoint;
        Name = name;
        Kind = kind;
        Description = description ?? "Live · Skizzy host in KSP";
    }

    public string Name { get; }
    public string Description { get; }
    public SourceKind Kind { get; }
    /// <summary>Orders go to the host's c2.* capabilities; the host answers (or refuses) them itself.</summary>
    public virtual SourceCaps Caps => SourceCaps.Catalog | SourceCaps.Orders;
    public SourceState State { get; private set; } = SourceState.Idle;
    public string StatusText { get; private set; } = "Not connected";
    public bool Paused => false;
    public double Speed => 1;
    public IReadOnlyList<CatalogEntry> Catalog => _catalog;
    /// <summary>"tactical" (M2) or "query" (M0 vessel stream) once subscribed.</summary>
    public string? StreamKind { get; private set; }

    /// <summary>The Engagements panel's note while there is no tactical stream.</summary>
    public const string NoTacticalNote =
        "The engagement log needs the M2 tactical stream (source.tactical): the Skizzy BDArmory binding in a flight scene. The map retries every 5 s.";

    /// <summary>The map side a BDArmory team is drawn as (assigned in the order teams first appear).</summary>
    public Side SideOfTeam(string team)
    {
        if (_teamSides.TryGetValue(team, out var s)) return s;
        s = _teamSides.Count switch { 0 => Side.Blue, 1 => Side.Red, 2 => Side.Green, _ => Side.Other };
        _teamSides[team] = s;
        return s;
    }

    /// <summary>Teams seen so far and their map sides.</summary>
    public IReadOnlyDictionary<string, Side> TeamSides => _teamSides;

    public virtual void Attach(MapData data)
    {
        _data = data;
        data.Reset();
        data.Epoch = double.NaN;
        data.DayLength = 21600;
        data.Losses.Note = "Losses need the M2 tactical stream (kills and expenditure by side). The M0 vessel stream has neither.";
        data.Losses.Version++;
        data.Engagements.Note = NoTacticalNote;
        data.Engagements.Touch();
        data.Log.Add(0, LogCategory.System, $"Connecting to {Name}…");
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void SetPaused(bool paused) { }
    public void SetSpeed(double speed) { }

    public void Restart()
    {
        // reconnect: the client re-hellos and the stream is re-subscribed
        var c = _client;
        _inbox.Enqueue(() => _data?.Log.Add(_data.LiveTime, LogCategory.System, "Reconnecting…"));
        try { c?.Dispose(); } catch (ObjectDisposedException) { }
    }

    public HealthSnapshot Health => new(Name, State.ToString(), string.IsNullOrEmpty(_url) ? null : _url, _hostVersion, _rttMs, _rateHz,
        _data?.Tracks.Count(t => t.Alive) ?? 0, _samples,
        [
            new("Stream", StreamKind is null ? "—" : StreamKind == "tactical" ? "source.tactical (M2)" : "source.query vessels (M0)"),
            new("Projection origin", _lat0 is null ? "—" : $"{_lat0.Value.ToString("0.0000", CultureInfo.InvariantCulture)}, {_lon0!.Value.ToString("0.0000", CultureInfo.InvariantCulture)} ({_body0})"),
            new("Reconnects", (_client?.Reconnects ?? 0).ToString(CultureInfo.InvariantCulture)),
        ]);

    // ------------------------------------------------------------ connection (background)

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(SourceState.Connecting, "Connecting…");
                var client = new SkizzyClient { AutoReconnect = false };
                client.Notification += OnNotification;
                _client = client;
                await client.ConnectAsync(EndpointWithUrl, new HelloOptions
                {
                    ClientName = "skizzy-map", ClientKind = "map", ActorKind = "human", Display = "Skizzy Map", Features = ["ops", "streams", "events"],
                }, ct).ConfigureAwait(false);
                _hostVersion = client.HelloResult?.GetString("hostVersion") ?? client.HelloResult?.GetObj("host")?.GetString("version");
                SetState(SourceState.Online, "Connected · reading the catalog");
                await LoadCatalogAsync(client, ct).ConfigureAwait(false);
                await SubscribeAsync(client, ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    // the binding attaches the tactical picture only in a flight scene: move up to it once it is there
                    if (StreamKind == "query") await TryTacticalAsync(client, ct, replace: true).ConfigureAwait(false);
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await client.CallAsync("host.stats", JsonValue.Obj(), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        _rttMs = sw.Elapsed.TotalMilliseconds;
                    }
                    catch (Exception e) when (IsCallError(e)) { _rttMs = null; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // anything else (protocol errors included) drops the connection and retries: the loop must never die
                SetState(SourceState.Reconnecting, "Not connected: " + Short(e.Message) + " · retrying");
            }
            finally
            {
                var c = _client;
                _client = null;
                _sub = null;
                try { c?.Dispose(); } catch (ObjectDisposedException) { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>A call that failed at the host (SkizzyError: protocol error) or in the client (SkizzyClientException).</summary>
    private static bool IsCallError(Exception e) => e is SkizzyClientException or SkizzyError or TimeoutException;

    private Endpoint EndpointWithUrl()
    {
        var ep = _endpoint();
        _url = ep.Url ?? "";
        return ep;
    }

    private static string Short(string s) => s.Length > 140 ? s[..140] + "…" : s;

    private void SetState(SourceState s, string text)
    {
        _inbox.Enqueue(() =>
        {
            bool changed = State != s;
            State = s;
            StatusText = text;
            if (changed && _data is not null) _data.Log.Add(_data.LiveTime, LogCategory.System, $"{Name}: {text}");
        });
    }

    private async Task LoadCatalogAsync(SkizzyClient client, CancellationToken ct)
    {
        try
        {
            var res = await client.CallAsync("catalog.get", JsonValue.Obj(), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var list = new List<CatalogEntry>();
            var caps = res.GetArr("capabilities");
            if (caps is not null)
                foreach (var d in caps.Items)
                {
                    var schema = d.GetObj("inputSchema");
                    list.Add(new CatalogEntry(d.GetString("name") ?? "", d.GetString("title") ?? d.GetString("name") ?? "", d.GetString("description") ?? "",
                        d.GetString("area") ?? "", d.GetString("safety") ?? "read", d.GetBool("available", true),
                        schema is null ? "{\"type\":\"object\"}" : JsonWriter.Serialize(schema)));
                }
            _inbox.Enqueue(() => _catalog = list);
        }
        catch (Exception e) when (IsCallError(e)) { }
    }

    private async Task SubscribeAsync(SkizzyClient client, CancellationToken ct)
    {
        // M2 tactical first; M0 hosts answer invalid_param (and M2 hosts outside a flight scene tactical_not_attached), and
        // we fall back to the vessel query stream, retrying the tactical one every 5 s
        StreamKind = null;
        if (await TryTacticalAsync(client, ct, replace: false).ConfigureAwait(false)) return;

        while (!ct.IsCancellationRequested && client.IsConnected)
        {
            try
            {
                var q = JsonValue.Obj()
                    .Set("source", JsonValue.Obj().Set("query", JsonValue.Obj()
                        .Set("select", JsonValue.Obj().Set("vessels", JsonValue.Obj()))
                        .Set("fields", JsonValue.ArrOf(Fields))))
                    .Set("mode", "sampled").Set("rateHz", 5);
                var res = await client.CallAsync("stream.subscribe", q, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                _sub = res.GetString("sub");
                StreamKind = "query";
                var cols = res.GetStringList("columns");
                var rows = res.GetArr("rows");
                double ut = res.GetDouble("ut", res.GetObj("meta")?.GetDouble("ut", double.NaN) ?? double.NaN);
                _inbox.Enqueue(() =>
                {
                    if (cols is not null) _columns = cols.ToArray();
                    if (rows is not null) ApplyRows(rows, ut, full: true);
                });
                SetState(SourceState.Online, "Live · vessel stream 5 Hz (M0)");
                return;
            }
            catch (Exception e) when (IsCallError(e))
            {
                bool unavailable = e is SkizzyClientException { Code: "unavailable" } || e is SkizzyError { Kind: var k } && ErrorKinds.Name(k) == "unavailable";
                SetState(SourceState.Online, "Connected · " + (unavailable ? "waiting for the flight scene" : Short(e.Message)));
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Subscribes to source.tactical. With <paramref name="replace"/> the M0 vessel stream it replaces is dropped once the
    /// tactical one is live. False when the host refused (no binding attached yet, or an M0 host).
    /// </summary>
    private async Task<bool> TryTacticalAsync(SkizzyClient client, CancellationToken ct, bool replace)
    {
        try
        {
            var tac = JsonValue.Obj().Set("source", JsonValue.Obj().Set("tactical", JsonValue.Obj())).Set("mode", "delta").Set("rateHz", 10);
            var res = await client.CallAsync("stream.subscribe", tac, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var old = _sub;
            var cols = res.GetStringList("columns");
            string? sub = res.GetString("sub");
            _inbox.Enqueue(() =>
            {
                if (cols is not null) _columns = cols.ToArray();
                _freshSub = true;
                if (_data is not null)
                {
                    _data.Engagements.Clear(); // the new subscription replays the host's log from its start
                    _data.Engagements.Note = null;
                    _data.Losses.Note = null;
                    _data.Losses.Version++;
                }
            });
            _sub = sub;
            StreamKind = "tactical";
            SetState(SourceState.Online, "Live · tactical stream");
            if (replace && old is not null)
            {
                try { await client.CallAsync("stream.unsubscribe", JsonValue.Obj().Set("sub", old), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (Exception e) when (IsCallError(e)) { }
            }
            return true;
        }
        catch (Exception e) when (IsCallError(e)) { return false; }
    }

    private void OnNotification(string method, JsonValue p)
    {
        if (method != "stream.data" || p is null) return;
        var sub = p.GetString("sub");
        if (sub is null || sub != _sub) return;
        var rows = p.GetArr("rows");
        double ut = p.GetDouble("ut", double.NaN);
        bool full = p.GetString("kind") != "delta";
        var log = p.GetArr("log");
        var totals = p.GetArr("totals");
        if (rows is not null) _inbox.Enqueue(() => ApplyRows(rows, ut, full));
        if (log is not null || totals is not null) _inbox.Enqueue(() => ApplyLedger(log, totals));
    }

    // ------------------------------------------------------------ UI thread

    public void Tick(double realDt)
    {
        int n = 0;
        while (n++ < 256 && _inbox.TryDequeue(out var a)) a();
        var d = _data;
        if (d is null || double.IsNaN(_lastUt) || double.IsNaN(d.Epoch)) return;
        // between samples the clock runs on wall time (bounded): tracks extrapolate smoothly at 60 fps
        double since = Math.Min(1.0, (Stopwatch.GetTimestamp() - _lastUtWall) / (double)Stopwatch.Frequency);
        if (!d.InReplay) d.LiveTime = Math.Max(d.LiveTime, _lastUt - d.Epoch + since);
    }

    private int Col(string name) => Array.IndexOf(_columns, name);

    private void ApplyRows(JsonValue rows, double ut, bool full)
    {
        var d = _data;
        if (d is null) return;
        if (double.IsNaN(ut)) ut = double.IsNaN(_lastUt) ? 0 : _lastUt + 0.2;
        if (double.IsNaN(d.Epoch)) { d.Epoch = ut; d.ReplayStart = 0; }
        _lastUt = ut;
        _lastUtWall = Stopwatch.GetTimestamp();
        double t = ut - d.Epoch;
        _samples++;
        _sampleWindowCount++;
        double win = (Stopwatch.GetTimestamp() - _sampleWindowStart) / (double)Stopwatch.Frequency;
        if (win >= 2) { _rateHz = _sampleWindowCount / win; _sampleWindowCount = 0; _sampleWindowStart = Stopwatch.GetTimestamp(); }

        // columns are ["vessel", "vesselName", fields...]; rows follow them exactly (the tactical stream adds its own after)
        var seen = new HashSet<string>();
        bool tactical = Col("team") >= 0;
        _missileRows.Clear();
        foreach (var row in rows.Items)
        {
            if (!row.IsArray || row.Count < 2) continue;
            string handle = row[0].AsString() ?? row[0].ToString();
            string name = row[1].AsString() ?? handle;
            JsonValue? F(string f)
            {
                int idx = _columns.Length > 0 ? Col(f) : Array.IndexOf(Fields, f) + 2;
                return idx >= 0 && idx < row.Count ? row[idx] : null;
            }
            double lat = F("lat")?.AsDouble(double.NaN) ?? double.NaN, lon = F("lon")?.AsDouble(double.NaN) ?? double.NaN;
            if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
            string body = F("body")?.AsString() ?? "Kerbin";
            string type = F("type")?.AsString() ?? "Ship";
            string situation = F("situation")?.AsString() ?? "";
            double alt = F("altitude")?.AsDouble() ?? 0;
            double spd = F("srfSpeed")?.AsDouble() ?? 0;
            double hdg = F("heading")?.AsDouble() ?? 0;
            double vs = F("verticalSpeed")?.AsDouble() ?? 0;
            bool active = F("isActive")?.AsBool() ?? false;
            if (_lat0 is null || (_body0 is not null && !string.Equals(body, _body0, StringComparison.OrdinalIgnoreCase) && active))
            {
                _lat0 = lat; _lon0 = lon; _body0 = body;
            }
            var (x, y) = Project(lat, lon, Radii.GetValueOrDefault(body, 600));
            string? cls = tactical ? F("class")?.AsString() : null;
            string? rowState = tactical ? F("state")?.AsString() : null;
            if (rowState == "dead")
            {
                // a missile the host's ledger resolved (hit, missed, intercepted, destroyed): it ends where it was last seen
                var gone = d.GetByKey("vessel:" + handle);
                if (gone is not null) seen.Add(gone.Key);
                if (gone is { Alive: true }) EndMissile(d, gone, F("missileState")?.AsString(), t, x, y, (float)alt);
                continue;
            }
            var tr = d.GetOrAdd("vessel:" + handle, t, out bool created);
            seen.Add(tr.Key);
            if (!tr.Alive) continue;
            if (tactical)
            {
                ClassifyTactical(tr, cls, F("domain")?.AsString(), F("team")?.AsString(), type);
                if (created && !tr.IsMissile) d.Log.Add(t, LogCategory.Contact, $"New contact: {name} ({tr.ClassName}{(tr.Side == Side.Other ? "" : ", " + tr.Side)}).", x, y);
            }
            else
            {
                if (created) d.Log.Add(t, LogCategory.Contact, $"New vessel: {name} ({type}, {situation.ToLowerInvariant().Replace('_', ' ')}).", x, y);
                Classify(tr, type, situation, alt, active);
            }
            tr.Name = name;
            tr.Health = -1;
            tr.ControlText = active ? "Active vessel" : tr.IsMissile ? "Missile" : "KSP";
            tr.Control = active ? ControlState.Manual : tr.IsMissile ? ControlState.None : ControlState.Bda;
            tr.Details.Extra.Clear();
            if (tactical && tr.IsMissile)
            {
                string? weapon = F("weapon")?.AsString(), launcherName = F("launcherName")?.AsString(), target = F("missileTarget")?.AsString();
                double cost = F("cost")?.AsDouble(double.NaN) ?? double.NaN;
                if (weapon is not null) tr.Details.Extra.Add(new("Weapon", weapon));
                if (launcherName is not null) tr.Details.Extra.Add(new("Launcher", launcherName));
                tr.Details.Extra.Add(new("State", F("missileState")?.AsString() ?? "flying"));
                if (!double.IsNaN(cost)) tr.Details.Extra.Add(new("Cost", EngagementBook.Funds(cost) + " funds"));
                _missileRows.Add((tr, target, F("launcher")?.AsString()));
            }
            if (tactical && F("team")?.AsString() is { } team) tr.Details.Extra.Add(new("Team", team));
            tr.Details.Extra.Add(new("Situation", situation.Replace('_', ' ').ToLowerInvariant()));
            tr.Details.Extra.Add(new("Body", body));
            tr.Details.Extra.Add(new("Lat, lon", $"{lat.ToString("0.0000", CultureInfo.InvariantCulture)}, {lon.ToString("0.0000", CultureInfo.InvariantCulture)}"));
            tr.Details.Extra.Add(new("Vertical speed", vs.ToString("0.0", CultureInfo.InvariantCulture) + " m/s"));
            if (F("crewCount")?.AsInt(-1) is int crew and >= 0) tr.Details.Extra.Add(new("Crew", crew.ToString(CultureInfo.InvariantCulture)));
            if (F("mass")?.AsDouble(double.NaN) is double mass && !double.IsNaN(mass)) tr.Details.Extra.Add(new("Mass", mass.ToString("0.0", CultureInfo.InvariantCulture) + " t"));
            HistoryRing.Velocity((float)hdg, (float)spd, out var vx, out var vy);
            tr.SetSample(t, x, y, (float)alt, (float)hdg, (float)spd, vx, vy, d.LiveTime);
            tr.Record(d.HistoryInterval);
            if (active && d.ReferenceTrackId == 0) d.ReferenceTrackId = tr.Id;
        }
        if (full)
        {
            foreach (var tr in d.Tracks)
            {
                if (!tr.Alive || seen.Contains(tr.Key)) { _missing.Remove(tr.Key); continue; }
                int miss = _missing.GetValueOrDefault(tr.Key) + 1;
                _missing[tr.Key] = miss;
                if (miss >= 3)
                {
                    d.MarkDead(tr, t);
                    // a missile's end is in the engagement log; only other vessels get the line
                    if (!tr.IsMissile) d.Log.Add(t, LogCategory.Damage, $"{tr.Name} left the picture (destroyed, recovered or unloaded).", tr.X, tr.Y);
                    _missing.Remove(tr.Key);
                }
            }
        }
        if (_missileRows.Count > 0) LinkMissiles(d);
        d.LiveTime = Math.Max(d.LiveTime, t);
        if (_samples % 50 == 0) d.Prune();
    }

    private static void Classify(TrackInfo tr, string type, string situation, double alt, bool active)
    {
        bool flying = situation is "FLYING" or "SUB_ORBITAL" or "ORBITING" or "ESCAPING";
        if (alt < -5 && !flying) { tr.Kind = TrackKind.Submarine; tr.Domain = TrackDomain.Subsurface; tr.ClassName = "Submarine"; tr.Code = "SUB"; }
        else if (type is "Plane" || flying) { tr.Kind = TrackKind.Fighter; tr.Domain = TrackDomain.Air; tr.ClassName = type == "Plane" ? "Aircraft" : type; tr.Code = type == "Plane" ? "AC" : type.ToUpperInvariant()[..Math.Min(3, type.Length)]; }
        else { tr.Kind = TrackKind.Destroyer; tr.Domain = TrackDomain.Surface; tr.ClassName = type == "Ship" ? "Ship" : type; tr.Code = type.ToUpperInvariant()[..Math.Min(3, type.Length)]; }
        tr.Rcs = KindInfo.Of(tr.Kind).Rcs;
        tr.Side = active ? Side.Blue : Side.Other;
        tr.AffBlue = active ? Affiliation.Friend : Affiliation.Unknown;
        tr.AffRed = active ? Affiliation.Hostile : Affiliation.Unknown;
        tr.DetBlue = TrackDetection.Shared;
        tr.DetRed = TrackDetection.Shared;
    }

    /// <summary>A tactical row: kind and domain from the engine's class, side and affiliations from the team.</summary>
    private void ClassifyTactical(TrackInfo tr, string? cls, string? domain, string? team, string type)
    {
        switch (cls)
        {
            case "missile":
                if (!tr.IsMissile) tr.Kind = TrackKind.AirToAirMissile; // refined by LinkMissiles once launcher and target are known
                tr.Domain = TrackDomain.Air; tr.ClassName = KindInfo.Of(tr.Kind).ClassName; tr.Code = KindInfo.Of(tr.Kind).Code;
                break;
            case "carrier": Set(TrackKind.Carrier, TrackDomain.Surface, "Carrier", "CV"); break;
            case "ship": Set(TrackKind.Destroyer, TrackDomain.Surface, "Ship", "SHIP"); break;
            case "submarine": Set(TrackKind.Submarine, TrackDomain.Subsurface, "Submarine", "SUB"); break;
            case "aew": Set(TrackKind.EarlyWarning, TrackDomain.Air, "AEW", "AEW"); break;
            case "bomber": Set(TrackKind.Strike, TrackDomain.Air, "Bomber", "BMR"); break;
            case "drone": Set(TrackKind.Uav, TrackDomain.Air, "Drone", "UAV"); break;
            case "fighter": Set(TrackKind.Fighter, TrackDomain.Air, "Fighter", "FTR"); break;
            case "airfield" or "ground": Set(TrackKind.Unknown, TrackDomain.Surface, cls == "airfield" ? "Airfield" : "Ground", cls == "airfield" ? "AF" : "GND"); break;
            default:
                if (domain == "air") Set(TrackKind.Fighter, TrackDomain.Air, "Aircraft", "AC");
                else if (domain == "subsurface") Set(TrackKind.Submarine, TrackDomain.Subsurface, "Submarine", "SUB");
                else Set(TrackKind.Destroyer, TrackDomain.Surface, type == "Ship" ? "Ship" : type, "SHIP");
                break;
        }
        tr.Rcs = KindInfo.Of(tr.Kind).Rcs;
        if (team is null)
        {
            tr.Side = Side.Other;
            tr.AffBlue = tr.AffRed = Affiliation.Unknown;
        }
        else
        {
            tr.Side = SideOfTeam(team);
            tr.AffBlue = tr.Side == Side.Blue ? Affiliation.Friend : Affiliation.Hostile;
            tr.AffRed = tr.Side == Side.Red ? Affiliation.Friend : Affiliation.Hostile;
        }
        tr.DetBlue = TrackDetection.Shared;
        tr.DetRed = TrackDetection.Shared;

        void Set(TrackKind k, TrackDomain dm, string name, string code) { tr.Kind = k; tr.Domain = dm; tr.ClassName = name; tr.Code = code; }
    }

    /// <summary>Missile target ids (target lines, inbound rings) and the missile kind from what it was fired from and at.</summary>
    private void LinkMissiles(MapData d)
    {
        foreach (var (m, target, launcher) in _missileRows)
        {
            var tg = target is null ? null : d.GetByKey("vessel:" + target);
            var ln = launcher is null ? null : d.GetByKey("vessel:" + launcher);
            m.TargetId = tg?.Id ?? 0;
            m.Details.TargetName = tg?.Name;
            bool fromSurface = ln is not null && ln.Domain is TrackDomain.Surface or TrackDomain.Subsurface;
            var kind = tg is null ? (fromSurface ? TrackKind.SurfaceToAirMissile : m.Kind)
                : tg.IsMissile ? TrackKind.SurfaceToAirMissile
                : tg.Domain is TrackDomain.Surface or TrackDomain.Subsurface ? TrackKind.AntiShipMissile
                : fromSurface ? TrackKind.SurfaceToAirMissile : TrackKind.AirToAirMissile;
            if (kind != m.Kind)
            {
                m.Kind = kind;
                m.ClassName = KindInfo.Of(kind).ClassName;
                m.Code = KindInfo.Of(kind).Code;
                m.Rcs = KindInfo.Of(kind).Rcs;
            }
            if (tg is not null) m.Details.Extra.Insert(Math.Min(2, m.Details.Extra.Count), new("Target", tg.Name));
        }
        _missileRows.Clear();
    }

    /// <summary>A missile the host resolved: last position, dead, and a plot event for how it ended.</summary>
    private static void EndMissile(MapData d, TrackInfo tr, string? state, double t, double x, double y, float alt)
    {
        tr.SetSample(t, x, y, alt, tr.HeadingDeg, 0, 0, 0, d.LiveTime);
        d.MarkDead(tr, t);
        var kind = state switch
        {
            "hit" => PlotEventKind.Impact,
            "intercepted" => PlotEventKind.Intercept,
            "destroyed" => PlotEventKind.Ciws,
            _ => PlotEventKind.Miss,
        };
        d.AddEvent(kind, x, y, t);
    }

    /// <summary>The tactical stream's "log" and "totals": the engagement book, the Messages log and the Losses tab.</summary>
    private void ApplyLedger(JsonValue? log, JsonValue? totals)
    {
        var d = _data;
        if (d is null) return;
        double epoch = double.IsNaN(d.Epoch) ? 0 : d.Epoch;
        var book = d.Engagements;
        if (log is not null && log.Count > 0)
        {
            if (_freshSub)
            {
                // a new subscription replays the log from the start: a host that restarted counts from 1 again
                long first = log[0].GetLong("seq", 0);
                if (first <= 1)
                {
                    long max = 0;
                    foreach (var e in log.Items) max = Math.Max(max, e.GetLong("seq", 0));
                    if (max < _loggedSeq) _loggedSeq = 0;
                }
            }
            foreach (var e in log.Items)
            {
                long seq = e.GetLong("seq", 0);
                string kind = e.GetString("kind") ?? "?";
                double t = e.GetDouble("ut", double.NaN) - epoch;
                if (double.IsNaN(t)) t = d.LiveTime;
                var entry = new EngagementEntry(seq, t, kind, e.GetString("team"), e.GetString("weapon"), e.GetString("missile"), e.GetString("launcherName"),
                    e.GetString("targetName"), e.GetString("targetTeam"), e.GetString("otherName"), e.GetString("otherTeam"), e.GetDouble("cost", double.NaN),
                    e.GetString("source"), e.GetString("text") ?? kind);
                if (!book.Add(entry) || seq <= _loggedSeq) continue;
                _loggedSeq = seq;
                // the Messages tab gets the line at the missile's (or, for a kill, the victim's) last position
                string? at = kind == "killed" ? e.GetString("other") : e.GetString("missile");
                var tr = at is null ? null : d.GetByKey("vessel:" + at);
                d.Log.Add(t, EngagementBook.CategoryOf(kind), entry.Text, tr?.X, tr?.Y);
                if (kind == "killed" && tr is not null)
                {
                    d.AddEvent(PlotEventKind.Kill, tr.X, tr.Y, t);
                    Side? side = entry.OtherTeam is null ? null : SideOfTeam(entry.OtherTeam);
                    var col = side == Side.Blue ? d.Losses.Blue : side == Side.Red ? d.Losses.Red : null;
                    if (col is not null)
                    {
                        string cls = string.IsNullOrEmpty(tr.ClassName) ? "Vessel" : tr.ClassName;
                        int i = col.Lost.FindIndex(kv => kv.Key == cls);
                        if (i < 0) col.Lost.Add(new(cls, 1)); else col.Lost[i] = new(cls, col.Lost[i].Value + 1);
                        d.Losses.Version++;
                    }
                }
            }
            _freshSub = false;
        }
        if (totals is not null)
        {
            var list = new List<EngagementTotals>(totals.Count);
            foreach (var o in totals.Items)
            {
                string? team = o.GetString("team");
                if (team is null) continue;
                SideOfTeam(team);
                list.Add(new EngagementTotals(team, o.GetInt("fired"), o.GetInt("inFlight"), o.GetInt("hits"), o.GetInt("misses"), o.GetInt("intercepted"),
                    o.GetInt("destroyed"), o.GetInt("interceptsMade"), o.GetInt("kills"), o.GetDouble("spent", double.NaN), o.GetDouble("interceptedCost", double.NaN),
                    o.GetDouble("hitCost", double.NaN), o.GetDouble("missedCost", double.NaN), o.GetInt("costUnknown")));
            }
            long v = book.Version;
            book.SetTotals(list);
            if (book.Version != v) FillLosses(d, list);
        }
    }

    /// <summary>The Losses tab's expenditure lines for the Blue and Red teams.</summary>
    private void FillLosses(MapData d, List<EngagementTotals> totals)
    {
        d.Losses.Blue.Lines.Clear();
        d.Losses.Red.Lines.Clear();
        foreach (var t in totals)
        {
            var side = SideOfTeam(t.Team);
            var col = side == Side.Blue ? d.Losses.Blue : side == Side.Red ? d.Losses.Red : null;
            if (col is null) continue;
            col.Lines.Add(new("Team", t.Team));
            col.Lines.Add(new("Missiles fired", N(t.Fired)));
            col.Lines.Add(new("Hits", N(t.Hits)));
            col.Lines.Add(new("Missed", N(t.Misses)));
            col.Lines.Add(new("Lost to intercepts", N(t.Intercepted)));
            col.Lines.Add(new("Intercepts made", N(t.InterceptsMade)));
            col.Lines.Add(new("Kills", N(t.Kills)));
            col.Lines.Add(new("Funds spent", EngagementBook.Funds(t.Spent)));
        }
        d.Losses.Version++;

        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------ ICommandSource

    /// <summary>The inverse of <see cref="Project"/>: a plot point back to lat/lon on the origin's body.</summary>
    public bool TryToGeo(double xKm, double yKm, out double lat, out double lon)
    {
        lat = lon = 0;
        if (_lat0 is not double lat0 || _lon0 is not double lon0) return false;
        double r = Radii.GetValueOrDefault(_body0 ?? "Kerbin", 600);
        double rho = Math.Sqrt(xKm * xKm + yKm * yKm);
        if (rho < 1e-9) { lat = lat0; lon = lon0; return true; }
        double la0 = lat0 * Math.PI / 180, lo0 = lon0 * Math.PI / 180, c = rho / r;
        double sinC = Math.Sin(c), cosC = Math.Cos(c);
        double la = Math.Asin(Math.Clamp(cosC * Math.Sin(la0) + yKm * sinC * Math.Cos(la0) / rho, -1, 1));
        double lo = lo0 + Math.Atan2(xKm * sinC, rho * Math.Cos(la0) * cosC - yKm * Math.Sin(la0) * sinC);
        lat = la * 180 / Math.PI;
        lon = (lo * 180 / Math.PI + 540) % 360 - 180;
        return true;
    }

    /// <summary>The host's vessel handle ("v:&lt;pid&gt;"), which the C2 engine resolves as a unit.</summary>
    public string? UnitRef(TrackInfo track) =>
        track.Key.StartsWith("vessel:", StringComparison.Ordinal) ? track.Key["vessel:".Length..] : null;

    /// <summary>The map's session has no side; the side comes from the engine (c2.unit.status of the ordered units).</summary>
    public string? CommandSide => null;

    /// <summary>Azimuthal equidistant about the origin: km east (+X) and north (+Y).</summary>
    private (double X, double Y) Project(double lat, double lon, double radiusKm)
    {
        double la0 = _lat0!.Value * Math.PI / 180, lo0 = _lon0!.Value * Math.PI / 180;
        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
        double dlo = lo - lo0;
        double cosC = Math.Sin(la0) * Math.Sin(la) + Math.Cos(la0) * Math.Cos(la) * Math.Cos(dlo);
        double c = Math.Acos(Math.Clamp(cosC, -1, 1));
        if (c < 1e-12) return (0, 0);
        double k = c / Math.Sin(c);
        double x = k * Math.Cos(la) * Math.Sin(dlo);
        double y = k * (Math.Cos(la0) * Math.Sin(la) - Math.Sin(la0) * Math.Cos(la) * Math.Cos(dlo));
        return (x * radiusKm, y * radiusKm);
    }

    public async Task<string> CallAsync(string method, string argsJson, CancellationToken ct)
    {
        var c = _client ?? throw new InvalidOperationException("Not connected to a host.");
        var args = string.IsNullOrWhiteSpace(argsJson) ? JsonValue.Obj() : JsonParser.Parse(argsJson);
        var res = await c.CallAsync(method, args, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return JsonWriter.Serialize(res, true);
    }

    public virtual void Dispose()
    {
        _cts.Cancel();
        try { _client?.Dispose(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _data = null;
        GC.SuppressFinalize(this);
    }
}
