using System.Globalization;
using System.Numerics;
using Skizzy.Map.Render.Model;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Map.Data.Demo;

/// <summary>
/// The built-in demo feed: the mockup's battle (<see cref="BattleSim"/>) published into the map mirror the way a
/// live tactical stream would be: kinematic samples every step, unit facts at 2 Hz, the Red fog picture at 1 Hz,
/// log, comms and events as they happen. Runs with no KSP and no host.
/// </summary>
public sealed class DemoBattleSource : IDataSource, ICommandSource
{
    private static readonly RangeRing Essm = new(22, "ESSM 22 km");
    private static readonly RangeRing Radar = new(150, "Radar 150 km", IsRadar: true);
    private static readonly RangeRing Sm2 = new(38, "SM-2 38 km (sea-skimmers)");
    private static readonly RangeRing Ssam = new(35, "SSAM 35 km");
    private static readonly RangeRing[] CvRings = [Essm, Radar];
    private static readonly RangeRing[] DdRings = [Sm2, Essm, Radar];
    private static readonly RangeRing[] SsRings = [Ssam];

    /// <summary>Red's own picture: Blue units within this range of a live Red aircraft are seen.</summary>
    public const double RedSensorKm = 150;

    private BattleSim _sim = new();
    private MapData? _data;
    private double _lastSampleT = double.NaN;
    private double _lastFactsT = double.NegativeInfinity;
    private double _lastFogT = double.NegativeInfinity;
    private readonly List<(double Due, string Text)> _replies = new();
    private readonly List<CatalogEntry> _catalog;
    private long _ticks;
    private DemoC2? _c2;
    private long _ordersVersion = -1;

    public DemoBattleSource()
    {
        _catalog = BuildCatalog();
    }

    public string Name => "Demo battle";
    public string Description => "Kerbin Strait · air raid on Task Group 1 · sample scenario";
    public SourceKind Kind => SourceKind.Demo;
    public SourceCaps Caps => SourceCaps.Pause | SourceCaps.Speed | SourceCaps.Restart | SourceCaps.Catalog | SourceCaps.DemoControls | SourceCaps.Compose | SourceCaps.Orders;
    public SourceState State => _data is null ? SourceState.Idle : SourceState.Online;
    public string StatusText => "Built-in scenario · no KSP";
    public bool Paused { get; private set; }
    public double Speed { get; private set; } = 1;

    /// <summary>The running battle (tests and the demo's top-bar controls).</summary>
    public BattleSim Sim => _sim;

    /// <summary>The demo's C2 engine and executor endpoint, created on the first c2.* call.</summary>
    public DemoC2 C2 => _c2 ??= new DemoC2(_sim);

    /// <summary>The C2 layer if the map has commanded anything since the last restart.</summary>
    public DemoC2? C2IfStarted => _c2;

    // ------------------------------------------------------------ ICommandSource

    public bool TryToGeo(double xKm, double yKm, out double lat, out double lon)
    {
        var g = DemoC2.ToGeo(xKm, yKm);
        (lat, lon) = (g.Lat, g.Lon);
        return true;
    }

    public string? UnitRef(TrackInfo track) => "v:" + track.Id.ToString(CultureInfo.InvariantCulture);

    public string? CommandSide => DemoC2.BlueSide;

    public IReadOnlyList<CatalogEntry> Catalog => _catalog;

    // ------------------------------------------------------------ side-level controls (mockup top bar)

    public string Posture => _sim.Posture;
    public bool AdvanceOnContact => _sim.AdvanceAuto;
    public double FriendlyFire => _sim.FriendlyFire;
    public bool PauseOnEvents => _sim.PauseOnEvents;

    public void SetPosture(string posture)
    {
        if (posture != "Proactive" && posture != "Defensive") throw new ArgumentOutOfRangeException(nameof(posture));
        _sim.Posture = posture;
        Log(LogCategory.Orders, posture == "Proactive"
            ? "Blue posture proactive: fighters hunt the strike aircraft, destroyers engage aircraft out to 90 km, the carrier launches alert fighters."
            : "Blue posture defensive: fighters hold their station, destroyers engage aircraft inside 60 km.");
    }

    public void SetAdvanceOnContact(bool on)
    {
        _sim.AdvanceAuto = on;
        Log(LogCategory.Orders, on ? "Advance on contact on." : "Advance on contact off: aircraft hold their stations until ordered.");
    }

    public void SetFriendlyFire(double chance)
    {
        _sim.FriendlyFire = Math.Clamp(chance, 0, 1);
        Log(LogCategory.Orders, chance > 0
            ? $"Friendly-fire simulation: about one Blue missile in {Math.Round(1 / chance).ToString("#,0", CultureInfo.InvariantCulture)} loses its target and finds a friend."
            : "Friendly-fire simulation off.");
    }

    /// <summary>The banner's "Don't pause again".</summary>
    public void DisablePauseOnEvents()
    {
        _sim.PauseOnEvents = false;
        Log(LogCategory.Orders, "Pause on events off. Events are still logged.");
    }

    /// <summary>The Comms tab's compose box. A Diplomatic message gets Red's canned reply 2.5 s later.</summary>
    public void SendComms(string channel, string text)
    {
        if (_data is null || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        _data.AddComms(_data.LiveTime, channel, "Blue commander (you)", CommsSide.You, text);
        _data.Log.Add(_data.LiveTime, LogCategory.Comms, $"You ({channel}): {text}");
        if (channel == "Diplomatic") _replies.Add((2.5, "Message received. Your position is noted. Our terms stand."));
    }

    private void Log(LogCategory c, string text) => _data?.Log.Add(_data.LiveTime, c, text);

    // ------------------------------------------------------------ IDataSource

    public void Attach(MapData data)
    {
        _data = data;
        Reset();
    }

    private void Reset()
    {
        var data = _data!;
        data.Reset();
        data.Epoch = BattleSim.Ut0;
        data.DayLength = 21600;
        data.ReferenceTrackId = 1;
        AddZones(data.Zones);
        _lastSampleT = double.NaN;
        _lastFactsT = _lastFogT = double.NegativeInfinity;
        _replies.Clear();
        Publish();
    }

    public void Restart()
    {
        var keep = (_sim.Posture, _sim.AdvanceAuto, _sim.FriendlyFire, _sim.PauseOnEvents);
        _sim = new BattleSim { Posture = keep.Posture, AdvanceAuto = keep.AdvanceAuto, FriendlyFire = keep.FriendlyFire, PauseOnEvents = keep.PauseOnEvents };
        _c2 = null;
        _ordersVersion = -1;
        Paused = false;
        if (_data is not null) Reset();
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (!paused && _data is not null) _data.PauseBanner = null;
    }

    public void SetSpeed(double speed) => Speed = Math.Clamp(speed, 0.05, 64);

    public void Tick(double realDt)
    {
        var data = _data;
        if (data is null) return;
        _ticks++;
        double dt = Math.Clamp(realDt, 0, 0.3); // the mockup's frame clamp: a stall never jumps the battle
        for (int i = _replies.Count - 1; i >= 0; i--)
        {
            var r = _replies[i];
            r.Due -= dt;
            if (r.Due <= 0)
            {
                _replies.RemoveAt(i);
                data.AddComms(data.LiveTime, "Diplomatic", "Red commander", CommsSide.Red, r.Text);
                data.Log.Add(data.LiveTime, LogCategory.Comms, $"Red commander (Diplomatic): {r.Text}");
            }
            else _replies[i] = r;
        }
        if (!Paused && !data.InReplay) _sim.Simulate(dt * Speed);
        if (_sim.PauseRequest is { } why)
        {
            _sim.PauseRequest = null;
            Paused = true;
            data.PauseBanner = why;
        }
        Publish();
    }

    public Task<string> CallAsync(string method, string argsJson, CancellationToken ct)
    {
        var data = _data ?? throw new InvalidOperationException("not attached");
        if (method.StartsWith("c2.", StringComparison.Ordinal) || method.StartsWith("demo.", StringComparison.Ordinal))
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argsJson) ? JsonValue.Obj() : JsonParser.Parse(argsJson);
                var res = C2.Call(method, args);
                return Task.FromResult(JsonWriter.Serialize(res, true));
            }
            catch (SkizzyError e) { return Task.FromException<string>(e); }
            finally
            {
                _lastFactsT = double.NegativeInfinity; // the unit panel shows the new state at once
                Publish();
            }
        }
        switch (method)
        {
            case "host.stats":
            {
                var o = JsonValue.Obj().Set("source", "demo").Set("simT", Math.Round(_sim.SimT, 1)).Set("objects", _sim.Objs.Count)
                    .Set("alive", _sim.Objs.Count(x => x.Alive)).Set("ticks", _ticks).Set("paused", Paused).Set("speed", Speed);
                return Task.FromResult(JsonWriter.Serialize(o, true));
            }
            case "ksp.vessels.list":
            {
                var arr = JsonValue.Arr();
                foreach (var t in data.Tracks)
                    if (t.Alive && !t.IsMissile)
                        arr.Add(JsonValue.Obj().Set("handle", t.Key).Set("name", t.Name).Set("type", t.Code).Set("side", t.Side.ToString().ToLowerInvariant()));
                return Task.FromResult(JsonWriter.Serialize(JsonValue.Obj().Set("vessels", arr).Set("total", arr.Count), true));
            }
            case "ksp.vessel.get":
            {
                var args = string.IsNullOrWhiteSpace(argsJson) ? JsonValue.Obj() : JsonParser.Parse(argsJson);
                string? key = args.GetString("vessel") ?? args.GetString("handle") ?? args.GetString("name");
                var t = data.Tracks.FirstOrDefault(x => x.Key == key || string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No unit '{key}' in the demo battle.");
                var o = JsonValue.Obj().Set("handle", t.Key).Set("name", t.Name).Set("class", t.ClassName).Set("alive", t.Alive)
                    .Set("x_km", Math.Round(t.X, 2)).Set("y_km", Math.Round(t.Y, 2)).Set("altitude", Math.Round(t.AltitudeM)).Set("heading", Math.Round(t.HeadingDeg))
                    .Set("srfSpeed", Math.Round(t.SpeedMps));
                return Task.FromResult(JsonWriter.Serialize(o, true));
            }
            default:
                throw new InvalidOperationException($"'{method}' needs a live host. The demo battle answers host.stats, ksp.vessels.list, ksp.vessel.get, the c2.* orders and doctrine capabilities and demo.*.");
        }
    }

    public HealthSnapshot Health
    {
        get
        {
            var d = _data;
            int alive = d is null ? 0 : d.Tracks.Count(t => t.Alive);
            return new HealthSnapshot(Name, Paused ? "Paused" : "Running", null, null, null, 1 / BattleSim.Step, alive, d?.Log.Entries.Count ?? 0,
            [
                new("Scenario time", BattleSim.FormatClock(_sim.SimT)),
                new("Sim objects", _sim.Objs.Count.ToString(CultureInfo.InvariantCulture)),
                new("Speed", Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×"),
                new("Posture", _sim.Posture),
            ]);
        }
    }

    public void Dispose() => _data = null;

    // ------------------------------------------------------------ publishing

    private void Publish()
    {
        var data = _data!;
        double now = _sim.SimT + _sim.Pending;
        bool newSample = _sim.SimT != _lastSampleT;
        bool facts = Math.Floor(_sim.SimT * 2) != Math.Floor(_lastFactsT * 2) || double.IsNegativeInfinity(_lastFactsT);
        bool fog = Math.Floor(_sim.SimT) != Math.Floor(_lastFogT) || double.IsNegativeInfinity(_lastFogT);
        if (fog) _sim.RefreshGrid();

        foreach (var o in _sim.Objs)
        {
            var tr = data.Get(o.Id);
            if (tr is null)
            {
                if (!o.Alive) continue; // never airborne (deck reserve) or died unseen
                tr = data.AddWithId(o.Id, "demo:" + o.Id, Math.Min(o.Born == 0 ? _sim.SimT : o.Born, _sim.SimT));
                InitStatic(tr, o);
                Facts(tr, o);
                Fog(tr, o);
                Sample(tr, o, now);
                tr.RecordNow();
                continue;
            }
            if (!tr.Alive) continue;
            if (newSample) Sample(tr, o, now);
            if (facts || !o.Alive) Facts(tr, o);
            if (fog) Fog(tr, o);
            if (!o.Alive) data.MarkDead(tr, double.IsNaN(o.DiedT) ? _sim.SimT : o.DiedT);
            else tr.Record(data.HistoryInterval);
        }
        // objects the sim purged (dead missiles) since the last publish
        if (newSample)
            foreach (var tr in data.Tracks)
                if (tr.Alive && _sim.Get(tr.Id) is { Alive: false } dead)
                    data.MarkDead(tr, double.IsNaN(dead.DiedT) ? _sim.SimT : dead.DiedT);

        foreach (var l in _sim.NewLogs) data.Log.Add(l.T, Category(l.Cat), l.Text, l.X, l.Y);
        _sim.NewLogs.Clear();
        foreach (var c in _sim.NewComms)
            data.AddComms(c.T, c.Ch, c.Who, c.Side switch { "blue" => CommsSide.Blue, "red" => CommsSide.Red, _ => CommsSide.Unknown }, c.Text, c.Unverified);
        _sim.NewComms.Clear();
        foreach (var e in _sim.NewEvents) data.AddEvent(EventKind(e.Type), e.X, e.Y, e.T);
        _sim.NewEvents.Clear();

        if (facts) Losses(data.Losses);
        if (_c2 is not null && _c2.Version != _ordersVersion) PublishOrders(data.Orders, _c2);
        data.LiveTime = now;
        if (newSample && Math.Floor(_sim.SimT / 10) != Math.Floor(_lastSampleT / 10)) data.Prune();
        _lastSampleT = _sim.SimT;
        if (facts) _lastFactsT = _sim.SimT;
        if (fog) _lastFogT = _sim.SimT;
    }

    /// <summary>The engine's orders as the plot draws them.</summary>
    private void PublishOrders(OrderPicture pic, DemoC2 c2)
    {
        _ordersVersion = c2.Version;
        pic.Begin();
        foreach (var (id, u) in c2.UnitOrders)
        {
            if (u.Draw == OrderType.Move && !u.HasDest) continue;
            var o = pic.AddOrder();
            o.TrackId = id;
            o.Type = u.Draw;
            o.Destination = new Vector2(u.DestX, u.DestY);
            o.TargetTrackId = u.TargetId;
            o.RouteId = u.RouteId;
            o.RouteIndex = u.RouteIndex;
            o.RouteOffset = new Vector2(u.OffX, u.OffY);
            o.PatternId = u.PatternId;
            o.RtbPhase = u.Phase;
        }
        foreach (var (id, pts) in c2.Routes)
        {
            var r = pic.AddRoute(id);
            foreach (var (x, y) in pts) r.Points.Add(new Vector2(x, y));
        }
        foreach (var rt in c2.Racetracks.Values)
        {
            var r = pic.AddRacetrack();
            r.Id = rt.Id; r.A = rt.A; r.B = rt.B; r.WidthKm = rt.WidthKm; r.TurnRight = rt.TurnRight;
        }
        pic.Commit();
    }

    private void Sample(TrackInfo tr, SimObj o, double now)
    {
        float hdg = (float)BattleSim.HdgDeg(o);
        double vx = o.Alive ? Math.Cos(o.Hdg) * o.Spd : 0, vy = o.Alive ? Math.Sin(o.Hdg) * o.Spd : 0;
        tr.SetSample(_sim.SimT, o.X, o.Y, (float)o.Alt, hdg, (float)(o.Spd * 1000), vx, vy, now, 0.2);
    }

    private static void InitStatic(TrackInfo tr, SimObj o)
    {
        var k = o.K;
        tr.Kind = k.Kind;
        tr.Domain = k.Domain switch { "surf" => TrackDomain.Surface, "sub" => TrackDomain.Subsurface, _ => TrackDomain.Air };
        tr.Side = o.Side == "blue" ? Side.Blue : Side.Red;
        tr.Name = o.Name;
        tr.ClassName = k.Cls;
        tr.Code = k.Code;
        tr.Rcs = (float)k.Rcs;
        if (o.Side == "blue")
            tr.Rings = o.Kind switch { "cvn" => CvRings, "ddg" => DdRings, "ssn" => SsRings, _ => null };
    }

    private void Facts(TrackInfo tr, SimObj o)
    {
        tr.Group = o.Group ?? "";
        tr.Vip = o.Vip;
        tr.Health = o.Hp is double hp ? (float)(hp / 100) : -1f;
        (tr.Control, tr.ControlText) = o.Ctrl switch
        {
            "Mission" => (ControlState.Mission, "Mission"),
            "Agent" => (ControlState.Agent, "Agent"),
            "Script" => (ControlState.Script, "Script"),
            "Manual" => (ControlState.Manual, "Manual · you"),
            "Order" => (ControlState.Order, "Your order"),
            _ => (ControlState.Bda, "BDArmory AI"),
        };
        if (_c2 is not null && _c2.UnitOrders.TryGetValue(o.Id, out var uo)) (tr.Control, tr.ControlText) = (ControlState.Order, uo.ControlText);
        tr.TargetId = o.K.Missile ? o.Target ?? 0 : o.TgtId is int tg && _sim.Get(tg)?.Alive == true ? tg : 0;

        var d = tr.Details;
        d.Mission = o.Mission;
        d.Doctrine = o.Doctrine;
        d.Emcon = o.Emcon;
        d.OnContact = o.Side == "blue" && o.K.Domain == "air" && !o.K.Missile ? o.OnContact ?? "task" : null;
        d.Magazines.Clear();
        if (o.Mag is not null) foreach (var (name, v) in o.Mag) d.Magazines.Add(new MagazineInfo(name, v[0], v[1]));
        bool armedAir = o.Kind is "ftr" or "ucav" or "bmr";
        d.Aam = armedAir ? o.Aam : null;
        d.AamMax = armedAir ? o.AamMax ?? 6 : null;
        d.Fuel = o.Fuel is double f ? (float)f : null;
        d.Damage = o.Hp is double h ? (float)((100 - h) / 100) : null;
        d.TargetName = null; d.TimeToGo = null; d.LinkHolder = null; d.LinkSelfDestructIn = null;
        if (o.K.Missile && _sim.Get(o.Target) is { } tg2)
        {
            d.TargetName = tg2.Name;
            double dist = Math.Sqrt((o.X - tg2.X) * (o.X - tg2.X) + (o.Y - tg2.Y) * (o.Y - tg2.Y));
            d.TimeToGo = o.Spd > 0 ? dist / o.Spd : null;
            if (o.Kind == "ssam")
            {
                var link = _sim.Get(o.Link);
                if (o.LostT is null && link is not null) d.LinkHolder = link.Name;
                else d.LinkSelfDestructIn = Math.Max(0, 10 - (_sim.SimT - (o.LostT ?? _sim.SimT)));
            }
        }
        if (o.Kind == "cvn")
        {
            var deck = _sim.DeckOf(o).ToList();
            d.DeckCount = deck.Count;
            d.DeckReady = deck.Count(x => x.Ready);
            d.DeckNames = deck.Count == 0 ? null : string.Join(", ", deck.Take(12).Select(x => x.Name)) + (deck.Count > 12 ? "…" : "");
        }
    }

    private void Fog(TrackInfo tr, SimObj o)
    {
        if (o.Side == "blue")
        {
            tr.AffBlue = Affiliation.Friend;
            tr.DetBlue = TrackDetection.Shared;
            tr.AffRed = Affiliation.Hostile;
            bool seen = o.Alive && _sim.AnyWithin(o.X, o.Y, x => x.Side == "red" && !x.K.Missile && x.Kind != "uav", RedSensorKm);
            tr.DetRed = seen || (o.K.Missile && o.Alive) ? TrackDetection.Shared : TrackDetection.None;
        }
        else
        {
            tr.AffRed = Affiliation.Friend;
            tr.DetRed = TrackDetection.Shared;
            tr.AffBlue = o.IdState == "unknown" ? Affiliation.Unknown : Affiliation.Hostile;
            tr.DetBlue = o.Stealth ? (o.Seen ? TrackDetection.OwnSensor : TrackDetection.None) : TrackDetection.Shared;
        }
    }

    private void Losses(LossReport r)
    {
        var s = _sim.Stats;
        r.Blue.Lost.Clear();
        foreach (var kv in s.LostBlue) r.Blue.Lost.Add(kv);
        r.Blue.Lines.Clear();
        foreach (var (k, v) in s.ExpBlue) r.Blue.Lines.Add(new(k == "CIWS" ? "CIWS kills" : k + " fired", Num(v)));
        r.Blue.Lines.Add(new("Missiles intercepted", Num(s.Intercepts + s.Ciws)));
        r.Blue.Lines.Add(new("Hits taken", Num(s.Hits)));
        r.Blue.Lines.Add(new("Friendly-fire incidents", Num(s.Ff)));
        r.Red.Lost.Clear();
        foreach (var kv in s.LostRed) r.Red.Lost.Add(kv);
        r.Red.Lines.Clear();
        r.Red.Lines.Add(new("Anti-ship missiles fired", Num(s.ExpRed["ASM"])));
        r.Red.Lines.Add(new("Drones expended", Num(s.ExpRed["Drones"])));
        int shots = s.ExpRed["ASM"] + s.ExpRed["Drones"];
        r.Red.Lines.Add(new("Leak rate", s.ExpRed["ASM"] > 0 ? Math.Round(100.0 * s.Hits / Math.Max(1, shots)).ToString("0", CultureInfo.InvariantCulture) + "%" : "—"));
        r.Version++;
    }

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static LogCategory Category(string c) => c switch
    {
        "contact" => LogCategory.Contact,
        "weapons" => LogCategory.Weapons,
        "intercept" => LogCategory.Intercept,
        "damage" => LogCategory.Damage,
        "orders" => LogCategory.Orders,
        "comms" => LogCategory.Comms,
        _ => LogCategory.System,
    };

    private static PlotEventKind EventKind(string t) => t switch
    {
        "intercept" => PlotEventKind.Intercept,
        "ciws" => PlotEventKind.Ciws,
        "impact" => PlotEventKind.Impact,
        "kill" => PlotEventKind.Kill,
        _ => PlotEventKind.Miss,
    };

    /// <summary>The mockup's zones: Red's declared exclusion zone, the CAP North station and bullseye ANVIL.</summary>
    public static void AddZones(List<PlotZone> zones)
    {
        var excl = new PlotZone { Kind = ZoneKind.Exclusion, Affiliation = Affiliation.Hostile, Label = "RED DECLARED EXCLUSION ZONE", LabelAnchor = new Vector2(30, 0) };
        excl.Fill.AddRange([new Vector2(30, -160), new Vector2(400, -160), new Vector2(400, 160), new Vector2(30, 160)]);
        excl.Boundary.AddRange([new Vector2(30, -160), new Vector2(30, 160)]);
        zones.Add(excl);
        zones.Add(new PlotZone { Kind = ZoneKind.Patrol, Affiliation = Affiliation.Friend, Label = "CAP NORTH", Center = new Vector2(62, 0), RadiiKm = [18f] });
        zones.Add(new PlotZone { Kind = ZoneKind.Bullseye, Affiliation = Affiliation.Neutral, Label = "BULLSEYE ANVIL", Center = new Vector2(100, 40), RadiiKm = [10f, 25f] });
    }

    private static List<CatalogEntry> BuildCatalog()
    {
        var live = new HashSet<string> { "host.stats", "ksp.vessels.list", "ksp.vessel.get" };
        var list = new List<CatalogEntry>();
        foreach (var d in Builtins.Registry.All)
        {
            var schema = d.InputSchema is null ? "{\"type\":\"object\"}" : JsonWriter.Serialize(d.InputSchema);
            list.Add(new CatalogEntry(d.Name, d.Title ?? d.Name, d.Description ?? "", d.Area ?? "", Skizzy.Protocol.Permissions.SafetyNames.Name(d.Safety), live.Contains(d.Name), schema));
        }
        return list;
    }
}
