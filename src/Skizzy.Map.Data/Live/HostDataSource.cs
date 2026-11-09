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
public class HostDataSource : IDataSource
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
    public virtual SourceCaps Caps => SourceCaps.Catalog;
    public SourceState State { get; private set; } = SourceState.Idle;
    public string StatusText { get; private set; } = "Not connected";
    public bool Paused => false;
    public double Speed => 1;
    public IReadOnlyList<CatalogEntry> Catalog => _catalog;
    /// <summary>"tactical" (M2) or "query" (M0 vessel stream) once subscribed.</summary>
    public string? StreamKind { get; private set; }

    public virtual void Attach(MapData data)
    {
        _data = data;
        data.Reset();
        data.Epoch = double.NaN;
        data.DayLength = 21600;
        data.Losses.Note = "Losses need the M2 tactical stream (kills and expenditure by side). The M0 vessel stream has neither.";
        data.Losses.Version++;
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
        // M2 tactical first; M0 hosts answer invalid_param and we fall back to the vessel query stream
        try
        {
            var tac = JsonValue.Obj().Set("source", JsonValue.Obj().Set("tactical", JsonValue.Obj())).Set("mode", "delta").Set("rateHz", 10);
            var res = await client.CallAsync("stream.subscribe", tac, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            _sub = res.GetString("sub");
            StreamKind = "tactical";
            SetState(SourceState.Online, "Live · tactical stream");
            return;
        }
        catch (Exception e) when (IsCallError(e)) { }

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

    private void OnNotification(string method, JsonValue p)
    {
        if (method != "stream.data" || p is null) return;
        var sub = p.GetString("sub");
        if (sub is null || sub != _sub) return;
        var rows = p.GetArr("rows");
        double ut = p.GetDouble("ut", double.NaN);
        bool full = p.GetString("kind") != "delta";
        if (rows is not null) _inbox.Enqueue(() => ApplyRows(rows, ut, full));
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

        // columns are ["vessel", "vesselName", fields...]; rows follow them exactly
        var seen = new HashSet<string>();
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
            var tr = d.GetOrAdd("vessel:" + handle, t, out bool created);
            seen.Add(tr.Key);
            if (!tr.Alive) continue;
            if (created) d.Log.Add(t, LogCategory.Contact, $"New vessel: {name} ({type}, {situation.ToLowerInvariant().Replace('_', ' ')}).", x, y);
            Classify(tr, type, situation, alt, active);
            tr.Name = name;
            tr.Health = -1;
            tr.ControlText = active ? "Active vessel" : "KSP";
            tr.Control = active ? ControlState.Manual : ControlState.Bda;
            tr.Details.Extra.Clear();
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
                    d.Log.Add(t, LogCategory.Damage, $"{tr.Name} left the picture (destroyed, recovered or unloaded).", tr.X, tr.Y);
                    _missing.Remove(tr.Key);
                }
            }
        }
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
