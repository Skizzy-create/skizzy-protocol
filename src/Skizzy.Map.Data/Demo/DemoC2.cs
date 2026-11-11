using System.Globalization;
using Skizzy.C2;
using Skizzy.Host.Core;
using Skizzy.Map.Render.Model;
using DrawType = Skizzy.Map.Render.Model.OrderType;
using OrderType = Skizzy.C2.OrderType;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Map.Data.Demo;

/// <summary>What the C2 engine is doing with one Blue unit, for the unit panel and the order overlay.</summary>
public readonly record struct DemoUnitOrder(string ControlText, DrawType Draw, int TargetId, float DestX, float DestY, int RouteId, int RouteIndex,
    float OffX, float OffY, int PatternId, RtbPhase Phase, bool HasDest);

/// <summary>
/// The demo battle's C2 layer: the real <see cref="C2Engine"/> over <see cref="BattleSim"/>. It is the game binding
/// (<see cref="IC2World"/> and <see cref="IBdaControl"/> over the sim's executor, hook events from the sim) and the
/// executor endpoint for the c2.* contract, parsed and answered with the host's own <see cref="C2Wire"/>, so the map
/// issues the same calls to the demo as to a live host. Adds the demo's recover and launch adapters (24-ksp-parity
/// row 31): aircraft in a marshal stack land one every 5 s, and <c>demo.deck.launch</c> launches a carrier's ready
/// aircraft. Main (UI) thread only.
/// </summary>
public sealed class DemoC2 : IC2World, IBdaControl
{
    /// <summary>The demo plane is mapped to lat/lon on a body this size (near-planar over the battle area).</summary>
    public const double RadiusM = 6_371_000;
    private static readonly double KmPerDeg = RadiusM / 1000 * Math.PI / 180;
    public const string BlueSide = "Blue", RedSide = "Red";

    private readonly BattleSim _sim;
    private readonly Dictionary<int, UnitState> _states = new();
    private readonly List<UnitState> _scratch = new();
    private readonly List<C2Event> _events = new();
    private readonly List<UnitBrief> _briefs = new();
    private readonly Dictionary<int, int> _engaging = new();
    private double _engagingAt = double.NaN;
    private readonly Dictionary<int, double> _nextLand = new();
    private readonly List<uint> _pendingHunt = new();
    private int _step;

    public DemoC2(BattleSim sim)
    {
        _sim = sim;
        Engine = new C2Engine(this, this);
        _sim.Hooks = Engine;
        _sim.AfterStep = OnStep;
        // the sim keeps its own first-launch pause and log; the engine mirrors the sim's side settings
        foreach (var side in new[] { BlueSide, RedSide })
            Engine.SetDoctrine(DoctrineLevel.Side, side, new Dictionary<string, object> { ["pauseOnFirstLaunch"] = false });
        Engine.SetDoctrine(DoctrineLevel.Side, BlueSide, new Dictionary<string, object>
        {
            ["posture"] = sim.Posture.ToLowerInvariant(),
            ["advanceOnContact"] = sim.AdvanceAuto,
        });
    }

    public C2Engine Engine { get; }

    /// <summary>True once the map commanded something: from then on the engine ticks at 5 Hz of game time.</summary>
    public bool Engaged { get; private set; }

    /// <summary>Every c2.* and demo.* call served, "method {json}" (tests).</summary>
    public List<string> Calls { get; } = new();

    /// <summary>The engine's current order per Blue unit, rebuilt every engine tick.</summary>
    public Dictionary<int, DemoUnitOrder> UnitOrders { get; } = new();

    /// <summary>Drawn routes (order id → points, km) and racetracks of the current orders.</summary>
    public Dictionary<int, List<(float X, float Y)>> Routes { get; } = new();
    public Dictionary<int, PlotRacetrack> Racetracks { get; } = new();

    /// <summary>Incremented whenever <see cref="UnitOrders"/>, <see cref="Routes"/> or <see cref="Racetracks"/> were rebuilt.</summary>
    public long Version { get; private set; }

    // ------------------------------------------------------------ projection

    public static GeoPoint ToGeo(double xKm, double yKm, double alt = 0) => new(yKm / KmPerDeg, xKm / KmPerDeg, alt);

    public static (double X, double Y) FromGeo(GeoPoint p) => (p.Lon * KmPerDeg, p.Lat * KmPerDeg);

    private static string SideOf(SimObj o) => o.Side == "blue" ? BlueSide : RedSide;

    private static UnitClass ClassOf(SimObj o) => o.Kind switch
    {
        "cvn" => UnitClass.Carrier,
        "ddg" => UnitClass.Ship,
        "ssn" => UnitClass.Submarine,
        "aew" => UnitClass.Aew,
        "ftr" => UnitClass.Fighter,
        "ucav" or "uav" => UnitClass.Drone,
        "bmr" => UnitClass.Bomber,
        _ => UnitClass.Missile,
    };

    private SimObj? Live(uint unit)
    {
        var o = _sim.Get((int)unit);
        return o is { Alive: true, Deck: null } ? o : null;
    }

    // ------------------------------------------------------------ IC2World

    public double Now => _sim.Ut;
    public double BodyRadius => RadiusM;
    public HookSet Hooks => HookSet.All;

    public void GetUnits(List<UnitState> into)
    {
        foreach (var o in _sim.Objs)
        {
            if (o.Deck is not null) continue; // stowed on a deck: not loaded
            if (o.Side == "blue" && o.K.Missile) continue; // own missiles count through FriendliesEngaging
            if (!o.Alive && (o.K.Missile || double.IsNaN(o.DiedT))) continue;
            if (!_states.TryGetValue(o.Id, out var s)) _states[o.Id] = s = new UnitState { Id = (uint)o.Id };
            s.Name = o.Name;
            s.Side = SideOf(o);
            s.Class = ClassOf(o);
            s.Group = o.Group;
            s.Alive = o.Alive;
            s.Pos = ToGeo(o.X, o.Y, o.Alt);
            s.HeadingDeg = BattleSim.HdgDeg(o);
            s.SpeedMps = o.Spd * 1000;
            s.AiActive = o.Alive;
            s.Landed = false;
            s.HasWeaponManager = !o.K.Missile;
            s.GuardMode = o.Bda?.Guard ?? true;
            switch (o.Kind)
            {
                case "ftr" or "ucav" or "bmr":
                    s.MissilesReady = o.Kind == "bmr" ? o.Asm : o.Aam;
                    s.MissilesMax = o.Kind == "bmr" ? 16 : o.AamMax ?? 6;
                    s.MissileRangeM = o.Kind == "ucav" ? 20000 : 42000;
                    s.HasGuns = o.Kind != "bmr";
                    break;
                case "cvn" or "ddg":
                {
                    int ready = 0, max = 0;
                    if (o.Mag is not null)
                        foreach (var (k, v) in o.Mag)
                            if (k is "SM-2" or "ESSM") { ready += v[0]; max += v[1]; }
                    s.MissilesReady = ready;
                    s.MissilesMax = max;
                    s.MissileRangeM = o.Bda?.RangeMax == true || o.Kind == "ddg" ? 90000 : 22000;
                    s.HasGuns = true;
                    break;
                }
                default:
                    s.MissilesReady = s.MissilesMax = 0;
                    s.MissileRangeM = 0;
                    s.HasGuns = false;
                    break;
            }
            s.Fuel = o.Fuel ?? double.NaN;
            s.HomeBase = o.HomeBase is int hb ? (uint)hb : 0;
            s.HasHome = o.St is not null;
            if (o.St is { } st) s.Home = ToGeo(st[0], st[1]);
            s.IsBase = o.Kind == "cvn";
            s.MissileTarget = o.Target is int tg ? (uint)tg : 0;
            into.Add(s);
        }
    }

    public void GetOwnContacts(uint unit, List<uint> into)
    {
        var o = _sim.Get((int)unit);
        if (o is not null) _sim.OwnContacts(o, into);
    }

    public bool SideDetects(string side, uint target)
    {
        var t = _sim.Get((int)target);
        if (t is null || !t.Alive) return false;
        if (side == BlueSide && t.Side == "red") return !t.Stealth || t.Seen;
        return true;
    }

    public bool AreHostile(string sideA, string sideB) => sideA is not null && sideB is not null && sideA != sideB;

    public int FriendliesEngaging(string side, uint target, uint exceptUnit)
    {
        if (_engagingAt != _sim.SimT)
        {
            _engagingAt = _sim.SimT;
            _engaging.Clear();
            foreach (var o in _sim.Objs)
            {
                if (!o.Alive || o.Side != "blue") continue;
                int t = o.K.Missile ? o.Target ?? 0 : o.Bda is { Current: > 0 } b ? b.Current : o.TgtId ?? 0;
                if (t != 0) _engaging[t] = _engaging.GetValueOrDefault(t) + 1;
            }
        }
        int n = _engaging.GetValueOrDefault((int)target);
        var ex = _sim.Get((int)exceptUnit);
        if (ex is not null && n > 0 && ((ex.Bda?.Current ?? ex.TgtId ?? 0) == (int)target)) n--;
        return side == BlueSide ? n : 0;
    }

    // ------------------------------------------------------------ IBdaControl

    public void SetOverrideTarget(uint unit, uint target, float durationS, bool sticky)
    {
        if (Live(unit) is { } o) _sim.SetOverride(o, (int)target, durationS, sticky);
    }

    public void ClearOverrideTarget(uint unit)
    {
        if (Live(unit) is { } o) _sim.ClearOverride(o);
    }

    public void SetSecondaryTargets(uint unit, IList<uint> targets, bool only)
    {
        if (Live(unit) is not { } o) return;
        var b = BattleSim.BdaOf(o);
        b.Secondary.Clear();
        if (targets is not null) foreach (var t in targets) b.Secondary.Add((int)t);
        b.SecondaryOnly = targets is not null && only;
    }

    public void SetFireAuthority(uint unit, FireAuthorityMode mode)
    {
        if (Live(unit) is not { } o) return;
        BattleSim.BdaOf(o).Fire = mode;
        if (o.Doctrine is not null) o.Doctrine = mode switch { FireAuthorityMode.Hold => "Hold", FireAuthorityMode.Tight => "Tight", _ => "Free" };
    }

    public void SetTargetFilter(uint unit, TargetFilter filter)
    {
        if (Live(unit) is { } o) BattleSim.BdaOf(o).Filter = filter;
    }

    public bool SetWaypoints(uint unit, IList<GeoPoint> points, float radiusM, int laps, float maxSpeedMps)
    {
        if (Live(unit) is not { } o || points is null || points.Count == 0) return false;
        var b = BattleSim.BdaOf(o);
        b.Wp.Clear();
        foreach (var p in points)
        {
            var (x, y) = FromGeo(p);
            b.Wp.Add((x, y, p.Alt));
        }
        b.WpIndex = 0;
        b.Lap = 0;
        b.Laps = Math.Max(1, laps);
        b.WpRadiusKm = Math.Max(.3, radiusM / 1000.0);
        b.FlyTo = false;
        b.PatrolEngaged = false;
        return true;
    }

    public void ClearWaypoints(uint unit)
    {
        if (Live(unit) is not { Bda: { } b }) return;
        b.Wp.Clear();
        b.PatrolEngaged = false;
    }

    public void SetPatrolEngage(uint unit, bool yieldToCombat, float leashM, GeoPoint anchor, bool hasAnchor)
    {
        if (Live(unit) is not { } o || !BattleSim.IsAir(o)) return;
        var b = BattleSim.BdaOf(o);
        b.Yield = yieldToCombat;
        b.LeashKm = leashM / 1000.0;
        b.HasAnchor = hasAnchor;
        (b.AnchorX, b.AnchorY) = FromGeo(anchor);
    }

    public bool CommandFlyTo(uint unit, GeoPoint point)
    {
        if (Live(unit) is not { } o) return false;
        var b = BattleSim.BdaOf(o);
        b.Wp.Clear();
        b.FlyTo = true;
        (b.FlyX, b.FlyY) = FromGeo(point);
        return true;
    }

    public void ReleaseCommand(uint unit)
    {
        if (Live(unit) is { Bda: { } b }) b.FlyTo = false;
    }

    public void SetGuardMode(uint unit, bool on)
    {
        if (Live(unit) is { } o) BattleSim.BdaOf(o).Guard = on;
    }

    public void SetEngageMissiles(uint unit, bool on)
    {
        if (Live(unit) is not { } o) return;
        var b = BattleSim.BdaOf(o);
        b.EngageMissilesBaseline ??= b.EngageMissiles;
        b.EngageMissiles = on;
    }

    public void RestoreEngageMissiles(uint unit)
    {
        if (Live(unit) is not { Bda: { EngageMissilesBaseline: bool was } b }) return;
        b.EngageMissiles = was;
        b.EngageMissilesBaseline = null;
    }

    public void SetMissileRangeMax(uint unit, bool extended)
    {
        if (Live(unit) is { } o) BattleSim.BdaOf(o).RangeMax = extended;
    }

    // ------------------------------------------------------------ ticking

    private void OnStep()
    {
        if (!Engaged || ++_step % 2 != 0) return;
        Tick();
    }

    /// <summary>One engine tick (5 Hz of game time while engaged), then the recover adapter and the report drain.</summary>
    public void Tick()
    {
        foreach (var id in _sim.Died) Engine.OnUnitDestroyed((uint)id);
        _sim.Died.Clear();
        foreach (var o in _sim.Landings) Engine.Release((uint)o.Id);
        _sim.Landings.Clear();
        Engine.Tick();
        if (_pendingHunt.Count > 0)
        {
            var req = OrderRequest.AttackNearest(BlueSide, _pendingHunt.ToArray());
            req.Issuer = "launch adapter";
            req.Label = "Launched aircraft";
            Engine.Issue(req);
            _pendingHunt.Clear();
        }
        Recover();
        Drain();
        Rebuild();
    }

    private void Engage()
    {
        if (Engaged) return;
        Engaged = true;
        Engine.Tick(); // the engine learns the units before the first order
        Drain();
    }

    /// <summary>
    /// Recover adapter: per carrier, the aircraft lowest in its marshal stack starts its approach when the deck is
    /// free (one landing every 5 s, the mockup's LAND_GAP); landing ends its hold.
    /// </summary>
    private void Recover()
    {
        _briefs.Clear();
        Engine.ListUnits(_briefs, BlueSide);
        Dictionary<int, (SimObj Unit, int Slot)>? first = null;
        foreach (var br in _briefs)
        {
            if (br.Task != "hold" || !br.Alive) continue;
            var o = _sim.Get((int)br.Id);
            if (o is null || !o.Alive || !BattleSim.IsAir(o) || o.ApproachBase is not null) continue;
            var v = Engine.GetUnitView(br.Id);
            if (v?.Hold != "marshal") continue;
            var b = NearestMarshal(o);
            if (b is null) continue;
            first ??= new();
            if (!first.TryGetValue(b.Id, out var cur) || v.MarshalSlot < cur.Slot) first[b.Id] = (o, v.MarshalSlot);
        }
        if (first is null) return;
        foreach (var (baseId, (o, _)) in first)
        {
            if (_sim.Objs.Any(x => x.Alive && x.ApproachBase == baseId)) continue;
            if (_sim.SimT < _nextLand.GetValueOrDefault(baseId)) continue;
            _sim.StartApproach(o, _sim.Get(baseId)!);
            _nextLand[baseId] = _sim.SimT + Engine.EffectiveDoctrine((uint)o.Id).LandGapS;
        }
    }

    private SimObj? NearestMarshal(SimObj o)
    {
        SimObj? best = null;
        double bd = double.MaxValue;
        foreach (var c in _sim.Objs)
        {
            if (!c.Alive || c.Kind != "cvn" || c.Side != o.Side) continue;
            var (mx, my) = BattleSim.MarshalPoint(c);
            double d = (mx - o.X) * (mx - o.X) + (my - o.Y) * (my - o.Y);
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    /// <summary>Engine reports become message-log lines. Issue-time reports are left to the caller's reply.</summary>
    private void Drain()
    {
        _events.Clear();
        Engine.DrainEvents(_events);
        foreach (var e in _events)
        {
            if (e.Kind is C2EventKind.OrderAccepted or C2EventKind.OrderRefused or C2EventKind.FirstLaunch) continue;
            if (string.IsNullOrEmpty(e.Text)) continue;
            var o = e.Unit != 0 ? _sim.Get((int)e.Unit) : null;
            _sim.NewLogs.Add(new SimLog(_sim.SimT, e.Category is "contact" or "weapons" ? e.Category : "orders", e.Text, o?.X, o?.Y));
        }
    }

    // ------------------------------------------------------------ the order picture

    private static string Phrase(UnitOrder u, SimObj o, Func<uint, string?> name) => u.Type switch
    {
        OrderType.Engage => "Your order · attack",
        OrderType.AttackNearest => "Your order · attack nearest",
        OrderType.Rtb => BattleSim.IsAir(o) ? "Your order · RTB · inbound" : "Your order · RTB to station",
        OrderType.HoldPattern => "Your order · racetrack",
        OrderType.DefendUnit => "Your order · protect " + (name(u.Parent.Request.Protect) ?? ""),
        OrderType.MoveRoute => "Your order · route",
        OrderType.Escort => "Your order · escort " + (name(u.Parent.Request.EscortOf) ?? ""),
        _ => "Your order · move",
    };

    private void Rebuild()
    {
        UnitOrders.Clear();
        Routes.Clear();
        Racetracks.Clear();
        _briefs.Clear();
        Engine.ListUnits(_briefs, BlueSide);
        string? Name(uint id) => _sim.Get((int)id)?.Name;
        foreach (var br in _briefs)
        {
            if (!br.Alive) continue;
            var o = _sim.Get((int)br.Id);
            if (o is null || !o.Alive) continue;
            bool landing = o.ApproachBase is not null;
            if (br.Order == 0 && br.Task != "hold" && !landing) continue;
            var v = Engine.GetUnitView(br.Id);
            if (v is null) continue;
            var b = o.Bda;
            string text;
            var draw = DrawType.Move;
            int target = 0, routeId = 0, routeIndex = 0, pattern = 0;
            float dx = 0, dy = 0, ox = 0, oy = 0;
            bool hasDest = false;
            var phase = RtbPhase.Inbound;
            if (landing)
            {
                text = "Your order · RTB · landing";
                draw = DrawType.Rtb; target = o.ApproachBase!.Value; phase = RtbPhase.Approach;
            }
            else if (v.Engagement is { } en)
            {
                text = Phrase(en, o, Name);
                draw = en.Type == OrderType.AttackNearest ? DrawType.Hunt : DrawType.Attack;
                target = (int)(v.OverrideTarget != 0 ? v.OverrideTarget : en.Type == OrderType.Engage ? en.Parent.Request.Target : 0);
            }
            else if (v.Movement is { } mv)
            {
                text = Phrase(mv, o, Name);
                var req = mv.Parent.Request;
                switch (mv.Type)
                {
                    case OrderType.Rtb when BattleSim.IsAir(o):
                        draw = DrawType.Rtb; target = NearestMarshal(o)?.Id ?? o.HomeBase ?? 0; phase = RtbPhase.Inbound;
                        if (b is { Wp.Count: > 0 }) { var w = b.Wp[^1]; target = BaseNear(w.X, w.Y) ?? target; }
                        break;
                    case OrderType.DefendUnit:
                        draw = DrawType.Defend; target = (int)req.Protect;
                        break;
                    case OrderType.Escort:
                        draw = DrawType.Defend; target = (int)req.EscortOf;
                        break;
                    case OrderType.HoldPattern:
                    {
                        draw = DrawType.Racetrack; pattern = mv.Parent.Id;
                        if (!Racetracks.ContainsKey(pattern))
                        {
                            var (ax, ay) = FromGeo(req.PatternA);
                            var (bx, by) = req.PatternB.IsZero ? (ax + 20, ay) : FromGeo(req.PatternB);
                            if (Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) < 3) (bx, by) = (ax + 20, ay);
                            Racetracks[pattern] = new PlotRacetrack
                            {
                                Id = pattern, A = new((float)ax, (float)ay), B = new((float)bx, (float)by),
                                WidthKm = double.IsNaN(req.WidthKm) ? 6f : (float)req.WidthKm, TurnRight = req.TurnRight,
                            };
                        }
                        break;
                    }
                    case OrderType.MoveRoute when req.Route is { Count: > 0 } route:
                    {
                        draw = DrawType.Route; routeId = mv.Parent.Id;
                        if (!Routes.ContainsKey(routeId))
                            Routes[routeId] = route.Select(p => { var (x, y) = FromGeo(p); return ((float)x, (float)y); }).ToList();
                        if (b is { Wp.Count: > 0 })
                        {
                            var (lx, ly) = FromGeo(route[^1]);
                            var w = b.Wp[^1];
                            ox = (float)(w.X - lx); oy = (float)(w.Y - ly);
                            routeIndex = Math.Clamp(route.Count - (b.Wp.Count - b.WpIndex), 0, route.Count - 1);
                        }
                        else routeIndex = route.Count - 1;
                        break;
                    }
                    default:
                        (hasDest, dx, dy) = Dest(o, b);
                        break;
                }
            }
            else
            {
                // a hold left by an achieved order
                if (v.Hold == "marshal") { text = "Your order · RTB · in the marshal stack"; draw = DrawType.Rtb; target = NearestMarshal(o)?.Id ?? 0; phase = RtbPhase.Marshal; }
                else if (v.Hold == "home") { text = "Your order · RTB to station"; (hasDest, dx, dy) = Dest(o, b); }
                else { text = "Your order · move"; (hasDest, dx, dy) = Dest(o, b); }
            }
            if (v.Divert != 0 && Name(v.Divert) is { } dn) text += " · engaging " + dn;
            UnitOrders[o.Id] = new DemoUnitOrder(text, draw, target, dx, dy, routeId, routeIndex, ox, oy, pattern, phase, hasDest || draw != DrawType.Move);
        }
        Version++;
    }

    private int? BaseNear(double x, double y)
    {
        foreach (var c in _sim.Objs)
        {
            if (!c.Alive || c.Kind != "cvn") continue;
            var (mx, my) = BattleSim.MarshalPoint(c);
            if ((mx - x) * (mx - x) + (my - y) * (my - y) < 4 * 4) return c.Id;
        }
        return null;
    }

    /// <summary>Where the executor is sending the unit: its fly-to point, its one-point route, or the centre of its orbit.</summary>
    private static (bool, float, float) Dest(SimObj o, SimBda? b)
    {
        if (b is null) return (false, 0, 0);
        if (b.FlyTo) return (true, (float)b.FlyX, (float)b.FlyY);
        if (b.Wp.Count == 1) return (true, (float)b.Wp[0].X, (float)b.Wp[0].Y);
        if (b.Wp.Count > 1)
        {
            double sx = 0, sy = 0;
            foreach (var w in b.Wp) { sx += w.X; sy += w.Y; }
            return (true, (float)(sx / b.Wp.Count), (float)(sy / b.Wp.Count));
        }
        return (false, 0, 0);
    }

    // ------------------------------------------------------------ the c2.* endpoint

    private uint Resolve(string reference, string pointer) => C2Wire.ResolveInWorld(this, _scratch, reference, pointer);

    private SimObj Unit(JsonValue p, string key)
    {
        uint id = Resolve(p.GetString(key) ?? "", "/" + key);
        return _sim.Get((int)id) ?? throw C2Wire.UnknownUnit(id);
    }

    /// <summary>Serves one call. Throws <see cref="SkizzyError"/> exactly as the host would (typed refusals included).</summary>
    public JsonValue Call(string method, JsonValue p)
    {
        Calls.Add(method + " " + JsonWriter.Serialize(p));
        Engage();
        try
        {
            return method switch
            {
                "c2.order.issue" => Issue(p),
                "c2.order.cancel" => Cancel(p),
                "c2.doctrine.set" => DoctrineSet(p),
                "c2.doctrine.unset" => DoctrineUnset(p),
                "c2.doctrine.effective" => DoctrineEffective(p),
                "c2.unit.status" => UnitStatus(p),
                "c2.unit.release" => Release(p),
                "c2.vip.set" => VipSet(p),
                "c2.side.managed" => SideManaged(p),
                "demo.deck.launch" => DeckLaunch(p),
                "demo.unit.emcon" => Emcon(p),
                _ => throw new SkizzyError(ErrorKind.NotFound, "unknown method " + method, "unknown_method"),
            };
        }
        finally
        {
            Drain();
            Rebuild();
        }
    }

    private static string RequiredSide(JsonValue p) =>
        p.GetString("side") ?? throw SkizzyError.InvalidParam("/side", "no side given and the session has none", "Pass side, or connect with --side.");

    private JsonValue Issue(JsonValue p)
    {
        var req = C2Wire.ParseIssue(p, Resolve);
        req.Issuer = "map";
        req.Role = IssuerRole.Seat;
        req.Side = RequiredSide(p);
        var r = Engine.Issue(req);
        if (!r.Accepted) throw C2Wire.Refusal(r);
        var reply = C2Wire.IssueReply(r);
        string summary = r.Summary ?? "";
        if (req.Type == OrderType.DefendUnit && _sim.Get((int)req.Protect) is { } vip) vip.Vip = true; // the engine marked it VIP
        if (req.Type == OrderType.Rtb)
            summary = summary.Replace("; landing waits for the recover adapter.", ", then land one every 5 s.", StringComparison.Ordinal);
        if (req.Type is OrderType.AttackNearest or OrderType.AllAttack)
        {
            // launch adapter: the carriers in the order launch their ready aircraft to join the hunt
            int launched = 0;
            foreach (var u in r.Units)
            {
                if (!u.Accepted || _sim.Get((int)u.Unit) is not { Kind: "cvn", Alive: true } cv) continue;
                foreach (var a in _sim.LaunchDeck(cv, hunt: true)) { _pendingHunt.Add((uint)a.Id); launched++; }
            }
            if (launched > 0)
            {
                int k = summary.IndexOf(" Not armed for this:", StringComparison.Ordinal);
                string head = k >= 0 ? summary[..k] : summary, tail = k >= 0 ? summary[k..] : "";
                summary = head.TrimEnd('.') + $"; carriers launched {launched} fighters from their decks to join the hunt." + tail;
            }
        }
        reply.Set("summary", summary).Set("text", summary);
        return reply;
    }

    private JsonValue Cancel(JsonValue p)
    {
        int id = C2Wire.OrderId(p.GetString("order"));
        var o = Engine.GetOrder(id) ?? throw SkizzyError.NotFound("o:" + id.ToString(CultureInfo.InvariantCulture), "unknown", "No such order (completed orders are forgotten after a while).");
        return C2Wire.Cancel(Engine, o, "map");
    }

    private string DoctrineTarget(JsonValue p, DoctrineLevel level)
    {
        string? t = p.GetString("target");
        if (level == DoctrineLevel.Side) return t ?? RequiredSide(p);
        if (t is null) throw SkizzyError.InvalidParam("/target", "target is required for level " + p.GetString("level"));
        return level == DoctrineLevel.Unit ? C2Wire.Handle(Resolve(t, "/target")) : t;
    }

    private JsonValue DoctrineSet(JsonValue p)
    {
        var level = C2Wire.Level(p.GetString("level"));
        string target = DoctrineTarget(p, level);
        var dict = C2Wire.DoctrineValues(p.GetObj("values"));
        var locks = C2Wire.Strings(p.Get("lock"));
        var errors = Engine.SetDoctrine(level, target, dict, locks.Count > 0 ? locks : null);
        if (errors.Count > 0) throw C2Wire.DoctrineError(errors);
        Mirror(level, target, dict);
        return JsonValue.Obj().Set("level", p.GetString("level")).Set("target", target).Set("set", dict.Count).Set("locked", locks.Count)
            .Set("text", "doctrine set at " + p.GetString("level") + " " + target);
    }

    /// <summary>Doctrine the demo's mission behaviour reads too: side advance and posture, unit weapons control and on-contact.</summary>
    private void Mirror(DoctrineLevel level, string target, Dictionary<string, object> values)
    {
        if (level == DoctrineLevel.Side && target == BlueSide)
        {
            if (values.TryGetValue("advanceOnContact", out var adv) && adv is bool a) _sim.AdvanceAuto = a;
            if (values.TryGetValue("posture", out var pos) && pos is string ps) _sim.Posture = ps.Equals("defensive", StringComparison.OrdinalIgnoreCase) ? "Defensive" : "Proactive";
        }
        else if (level == DoctrineLevel.Unit && HandleToObj(target) is { } o)
        {
            if (values.TryGetValue("weaponsControl", out var wc) && wc is string w && o.Doctrine is not null)
                o.Doctrine = w.ToLowerInvariant() switch { "hold" => "Hold", "tight" => "Tight", _ => "Free" };
            if (values.TryGetValue("onContact", out var oc) && oc is string c)
                o.OnContact = c.Equals("engage", StringComparison.OrdinalIgnoreCase) ? "engage" : "task";
        }
    }

    private SimObj? HandleToObj(string handle) =>
        Skizzy.Host.Core.World.HandleRegistry.TryParseVessel(handle, out var id) ? _sim.Get((int)id) : null;

    private JsonValue DoctrineUnset(JsonValue p)
    {
        var level = C2Wire.Level(p.GetString("level"));
        string target = DoctrineTarget(p, level);
        var keys = C2Wire.Strings(p.Get("keys"));
        Engine.UnsetDoctrine(level, target, keys);
        return JsonValue.Obj().Set("level", p.GetString("level")).Set("target", target).Set("unset", keys.Count)
            .Set("text", "doctrine unset at " + p.GetString("level") + " " + target + ": " + string.Join(", ", keys));
    }

    private JsonValue DoctrineEffective(JsonValue p)
    {
        uint id = Resolve(p.GetString("unit") ?? "", "/unit");
        var lines = Engine.ExplainDoctrine(id) ?? throw C2Wire.UnknownUnit(id);
        return C2Wire.DoctrineExplained(id, lines);
    }

    private JsonValue UnitStatus(JsonValue p)
    {
        uint id = Resolve(p.GetString("unit") ?? "", "/unit");
        var v = Engine.GetUnitView(id) ?? throw C2Wire.UnknownUnit(id);
        return C2Wire.UnitStatus(v);
    }

    private JsonValue Release(JsonValue p)
    {
        var units = p.Get("units");
        var arr = JsonValue.Arr();
        int released = 0;
        for (int i = 0; units is not null && i < units.Count; i++)
        {
            uint id = Resolve(units[i].AsString(), "/units/" + i);
            bool ok = Engine.Release(id, "map");
            if (ok) released++;
            arr.Add(JsonValue.Obj().Set("unit", C2Wire.Handle(id)).Set("released", ok));
        }
        return JsonValue.Obj().Set("released", released).Set("units", arr).Set("text", released + " unit(s) released to BDArmory guard behaviour");
    }

    private JsonValue VipSet(JsonValue p)
    {
        uint id = Resolve(p.GetString("unit") ?? "", "/unit");
        bool vip = p.GetBool("vip", true);
        if (!Engine.SetVip(id, vip)) throw C2Wire.UnknownUnit(id);
        if (_sim.Get((int)id) is { } o) o.Vip = vip;
        return JsonValue.Obj().Set("unit", C2Wire.Handle(id)).Set("vip", Engine.IsVip(id)).Set("text", C2Wire.Handle(id) + (vip ? " flagged VIP" : " VIP flag cleared"));
    }

    private JsonValue SideManaged(JsonValue p)
    {
        string side = RequiredSide(p);
        if (p.Get("managed") is { IsNull: false }) Engine.SetSideManaged(side, p.GetBool("managed"));
        bool now = Engine.IsSideManaged(side);
        return JsonValue.Obj().Set("side", side).Set("managed", now).Set("text", "side " + side + (now ? " is managed by Skizzy" : " runs plain BDArmory behaviour"));
    }

    private JsonValue DeckLaunch(JsonValue p)
    {
        var cv = Unit(p, "carrier");
        if (cv.Kind != "cvn" || !cv.Alive) throw SkizzyError.Precondition("not_a_carrier", cv.Name + " is not a carrier");
        bool hunt = p.GetBool("hunt");
        var launched = _sim.LaunchDeck(cv, hunt);
        if (launched.Count == 0)
            throw SkizzyError.Precondition("deck_not_ready", "No aircraft are ready yet. Rearming and refuelling take 90 s after landing.");
        if (hunt) foreach (var a in launched) _pendingHunt.Add((uint)a.Id);
        var arr = JsonValue.Arr();
        foreach (var a in launched) arr.Add(C2Wire.Handle((uint)a.Id));
        return JsonValue.Obj().Set("carrier", C2Wire.Handle((uint)cv.Id)).Set("launched", launched.Count).Set("units", arr)
            .Set("text", $"{cv.Name} launched {launched.Count} aircraft.");
    }

    private JsonValue Emcon(JsonValue p)
    {
        var o = Unit(p, "unit");
        string e = p.GetString("emcon") ?? "";
        if (o.Emcon is null) throw SkizzyError.Precondition("no_radar_control", o.Name + " has no radar emission control");
        o.Emcon = e.Equals("passive", StringComparison.OrdinalIgnoreCase) ? "Passive" : "Active";
        return JsonValue.Obj().Set("unit", C2Wire.Handle((uint)o.Id)).Set("emcon", o.Emcon.ToLowerInvariant()).Set("text", o.Name + ": radar " + o.Emcon.ToLowerInvariant() + ".");
    }
}
