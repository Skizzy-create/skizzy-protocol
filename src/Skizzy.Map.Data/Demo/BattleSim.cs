using System.Globalization;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Data.Demo;

/// <summary>Static per-class facts, the mockup's <c>K</c> table (speeds in km/s).</summary>
public sealed record SimKind(string Key, TrackKind Kind, string Domain, double Rcs, string Glyph, string Cls, double Spd, bool Missile, string Code);

/// <summary>One object of the demo battle (the mockup's plain JS objects). Public fields on purpose: this is a port.</summary>
public sealed class SimObj
{
    public int Id;
    public string Side = "blue";
    public string Kind = "ftr";
    public SimKind K = BattleSim.Kinds["ftr"];
    public string Name = "";
    public double X, Y, Alt, Hdg, Spd;
    public double? Hp;
    public string Ctrl = "BDA";
    public string? Mission;
    public string? Doctrine;
    public string? Emcon;
    public double[]? St;
    public Dictionary<string, int[]>? Mag;
    public int Channels;
    public bool Vip;
    public string? Group;
    public string? GType;
    public string? State;
    public int Asm;
    public int Aam;
    public int? AamMax;
    public int Pair;
    public double Off;
    public string? IdState;
    public Orbit? Cap;
    public Orbit? OrbitAt;
    public Orbit? Home;
    public double? Fuel;
    public int? HomeBase;
    public string? OnContact;
    public bool Stealth, Seen;
    public bool Alive = true;
    public readonly List<double> Trail = new();
    public double LastTrail = -9;
    public double Cool;
    public int? Target;
    public double Born;
    public int? Shooter;
    public string? Weapon;
    public bool Ff, FfDone;
    public int? Link;
    public double? LostT;
    public double Lkx, Lky, Lchk;
    public int? EngagedBy;
    public int? TgtId;
    public double TgtT;
    public double AdvT;
    public int? AdvId;
    public double CtT;
    public int? CtEx;
    public int? CtId;
    public double Until;
    public double BreakHdg;
    public double Egress;
    public int? Deck;
    public bool Ready;
    public double LandedT;
    public int Shots;
    public int? Diverted;
    /// <summary>BDArmory-level control the C2 engine drives (overrides, routes, fire authority); null until it touches the unit.</summary>
    public SimBda? Bda;
    /// <summary>Recover adapter: the carrier this aircraft is approaching to land on (null = not landing).</summary>
    public int? ApproachBase;
    /// <summary>Ship speed before the C2 engine first moved it (restored when it is released).</summary>
    public double? BaseSpd;
    /// <summary>Time the object died (for replay and losses), NaN while alive.</summary>
    public double DiedT = double.NaN;
}

/// <summary>A circle the mockup's <c>orbit()</c> flies around.</summary>
public readonly record struct Orbit(double X, double Y, double R);

public readonly record struct SimLog(double T, string Cat, string Text, double? X, double? Y);

public readonly record struct SimComms(double T, string Ch, string Who, string Side, string Text, bool Unverified);

public readonly record struct SimEvent(double T, string Type, double X, double Y);

/// <summary>Running totals for the Losses panel (the mockup's <c>stats</c>).</summary>
public sealed class SimStats
{
    public readonly Dictionary<string, int> ExpBlue = new() { ["SM-2"] = 0, ["ESSM"] = 0, ["AAM"] = 0, ["SSAM"] = 0, ["CIWS"] = 0 };
    public readonly Dictionary<string, int> ExpRed = new() { ["ASM"] = 0, ["AAM"] = 0, ["Drones"] = 0 };
    public readonly Dictionary<string, int> LostBlue = new();
    public readonly Dictionary<string, int> LostRed = new();
    public int Intercepts, Hits, Ciws, Ff, SsamSd, SsamHand;
}

/// <summary>
/// The demo battle: a C# port of the simulation in design/mockups/command-screen-v1.html (v15): three Blue carrier
/// groups with 30 destroyers, six submarines and their air wings against a four-wave Red raid of strike packages,
/// fighter sweeps, defensive CAPs, 200 attack drones and stealth pairs. Deterministic (the mockup's mulberry32
/// with seed 11). Operator orders come from the real C2 engine (<c>Skizzy.C2</c>) through
/// <see cref="DemoC2"/>, which drives the BDArmory-level executor in <c>BattleSim.Executor.cs</c>; units it does not
/// command keep the mockup's mission behaviour. The side-level controls (posture, advance on contact,
/// friendly-fire stand-in, pause on events) are ported as they are.
/// </summary>
public sealed partial class BattleSim
{
    public static readonly IReadOnlyDictionary<string, SimKind> Kinds = new Dictionary<string, SimKind>
    {
        ["cvn"] = new("cvn", TrackKind.Carrier, "surf", 40000, "CV", "Carrier", .008, false, "CVN"),
        ["ddg"] = new("ddg", TrackKind.Destroyer, "surf", 9000, "DD", "Destroyer", .008, false, "DDG"),
        ["ssn"] = new("ssn", TrackKind.Submarine, "sub", 300, "SS", "Attack submarine", .006, false, "SSN"),
        ["aew"] = new("aew", TrackKind.EarlyWarning, "air", 60, "AEW", "Early warning aircraft", .16, false, "AEW"),
        ["ftr"] = new("ftr", TrackKind.Fighter, "air", 4, "F", "Fighter", .25, false, "FTR"),
        ["bmr"] = new("bmr", TrackKind.Strike, "air", 25, "A", "Strike aircraft", .24, false, "STK"),
        ["uav"] = new("uav", TrackKind.Uav, "air", .15, "U", "Attack drone", .05, false, "UAV"),
        ["ucav"] = new("ucav", TrackKind.Ucav, "air", .3, "U", "Armed drone", .09, false, "UCAV"),
        ["asm"] = new("asm", TrackKind.AntiShipMissile, "air", .05, "", "Anti-ship missile", .29, true, "ASM"),
        ["sam"] = new("sam", TrackKind.SurfaceToAirMissile, "air", .08, "", "Surface-to-air missile", .9, true, "SAM"),
        ["aam"] = new("aam", TrackKind.AirToAirMissile, "air", .03, "", "Air-to-air missile", .8, true, "AAM"),
        ["ssam"] = new("ssam", TrackKind.SurfaceToAirMissile, "air", .06, "", "Sub-launched SAM", .7, true, "SSAM"),
    };

    /// <summary>Kerbin universal time of the scenario start (Y1 D014 03:12:00), seconds.</summary>
    public const double Ut0 = 13 * 21600 + 3 * 3600 + 12 * 60;

    public const double Step = .1;
    private const double GC = 40;
    private const double VipR = 40, PatrolLeash = 55, AsmLr = 110, SsamR = 35, SsamSd = 10;
    private const double LandGap = 5, MarshalD = 8, DeckTurn = 90;
    private static readonly Dictionary<string, double> LinkR = new() { ["ftr"] = 45, ["ucav"] = 25, ["ddg"] = 60 };

    private int _seed;
    private double _acc;
    private int _nextId;
    private Dictionary<long, List<SimObj>> _grid = new();
    private HashSet<int> _aamOn = new();
    private List<SimObj> _vips = new();
    private readonly Dictionary<string, int> _groupClassified = new();
    private readonly Dictionary<int, Dictionary<string, int>> _rep = new();
    private readonly Dictionary<string, double> _grpLogT = new();
    private int _scriptIdx;
    private bool _firstLaunch, _blueFirst, _ssamFirst, _alertDone, _saidEscorts, _advanceOn;
    private double _firstLaunchT;

    public BattleSim() => Setup();

    public List<SimObj> Objs { get; private set; } = new();
    public Dictionary<int, SimObj> ById { get; } = new();
    public double SimT { get; private set; }

    /// <summary>Game time accumulated toward the next fixed step (0 to 0.1 s). Display time is SimT + Pending.</summary>
    public double Pending => _acc;
    public SimStats Stats { get; private set; } = new();

    // ------------------------------------------------------------ side-level controls (the mockup's top bar)

    /// <summary>"Proactive" or "Defensive".</summary>
    public string Posture { get; set; } = "Proactive";

    /// <summary>Advance on contact (doctrine <c>advanceOnContact</c>, default on).</summary>
    public bool AdvanceAuto { get; set; } = true;

    /// <summary>Friendly-fire stand-in: chance that a Blue missile loses its target and finds a friend (demo only).</summary>
    public double FriendlyFire { get; set; } = .001;

    /// <summary>Pause on the first launch and the first friendly-fire hit (the mockup's <c>pauseOnLaunch</c>).</summary>
    public bool PauseOnEvents { get; set; } = true;

    // ------------------------------------------------------------ outputs, drained by the data source

    public List<SimLog> NewLogs { get; } = new();
    public List<SimComms> NewComms { get; } = new();
    public List<SimEvent> NewEvents { get; } = new();

    /// <summary>Set when an event asks for a pause (banner text). The data source clears it.</summary>
    public string? PauseRequest { get; set; }

    // ------------------------------------------------------------ rng (mulberry32, as in the mockup)

    private double Rnd()
    {
        unchecked
        {
            _seed += 0x6D2B79F5;
            int t = (_seed ^ (int)((uint)_seed >> 15)) * (1 | _seed);
            t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
            return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
        }
    }

    // ------------------------------------------------------------ helpers

    private SimObj Add(SimObj o)
    {
        o.Id = _nextId++;
        o.Alive = true;
        o.LastTrail = -9;
        o.Cool = 0;
        o.K = Kinds[o.Kind];
        Objs.Add(o);
        ById[o.Id] = o;
        return o;
    }

    public SimObj? Get(int? id) => id is int i && ById.TryGetValue(i, out var o) ? o : null;

    private static double Dist(SimObj a, SimObj b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private static void SteerTo(SimObj o, double hd, double rate, double dt)
    {
        double d = Wrap(hd - o.Hdg), m = rate * dt;
        o.Hdg += Math.Max(-m, Math.Min(m, d));
    }

    /// <summary>Compass bearing from one point to another, degrees.</summary>
    public static double BrgOf(double fx, double fy, double tx, double ty) =>
        ((90 - Math.Atan2(ty - fy, tx - fx) * 180 / Math.PI) % 360 + 360) % 360;

    private static double BrgOf(SimObj f, SimObj t) => BrgOf(f.X, f.Y, t.X, t.Y);

    /// <summary>Compass heading of an object, degrees.</summary>
    public static double HdgDeg(SimObj o) => ((90 - o.Hdg * 180 / Math.PI) % 360 + 360) % 360;

    private static string Pad3(double n) => ((int)Math.Floor(n + .5) % 360).ToString("000", CultureInfo.InvariantCulture);

    private static string F0(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static long GKey(long cx, long cy) => (cx + 5000) * 20000 + (cy + 5000);

    private bool AliveId(int? id) => Get(id)?.Alive == true;

    private bool FreeT(SimObj x) => !(x.EngagedBy is not null && AliveId(x.EngagedBy));

    private static bool IsMslThreatStatic(SimObj x) => x.Side == "red" && (x.Kind == "asm" || (x.Kind == "uav" && x.IdState == "hostile"));

    private static double SensorR(SimObj o) => o.Kind == "ucav" ? 25 : o.Kind == "ftr" ? 45 : 0;

    // ------------------------------------------------------------ output

    private void LogMsg(string cat, string text, double? x, double? y) => NewLogs.Add(new SimLog(SimT, cat, text, x, y));

    private void LogAt(string cat, string text, SimObj o) => LogMsg(cat, text, o.X, o.Y);

    private void Event(string type, double x, double y) => NewEvents.Add(new SimEvent(SimT, type, x, y));

    private void SayComms(string ch, string who, string side, string text, bool unverified = false)
    {
        NewComms.Add(new SimComms(SimT, ch, who, side, text, unverified));
        LogMsg("comms", $"{who} ({ch}): {text}", null, null);
    }

    private void Pause(string text)
    {
        if (PauseRequest is null) PauseRequest = text;
    }

    // ------------------------------------------------------------ setup (the mockup's setup())

    private SimObj Ship(string kind, string name, double x, double y, Action<SimObj>? extra = null)
    {
        var o = new SimObj
        {
            Side = "blue", Kind = kind, Name = name, X = x, Y = y, Alt = 0, Hdg = .12, Spd = Kinds[kind].Spd, Hp = 100,
            Ctrl = "Mission", Mission = "Sea control ALPHA", Doctrine = "Free", Emcon = "Active", St = [x, y],
        };
        extra?.Invoke(o);
        return Add(o);
    }

    private static Dictionary<string, int[]> DdgMag() => new() { ["SM-2"] = [64, 64], ["ESSM"] = [16, 16], ["CIWS"] = [1, 1] };

    private void Setup()
    {
        _seed = 11;
        Objs = new List<SimObj>();
        ById.Clear();
        _nextId = 1;
        SimT = 0;
        _acc = 0;
        Stats = new SimStats();
        _rep.Clear();
        _groupClassified.Clear();
        _scriptIdx = 0;
        _firstLaunch = _blueFirst = _ssamFirst = _alertDone = _saidEscorts = _advanceOn = false;
        _firstLaunchT = 0;

        // Blue: three carrier groups, 30 destroyers, 6 submarines (ids 1-5 are fixed: Kestrel, Halyard, Bowline, Cleat, Tide)
        Ship("cvn", "Kestrel", 0, 0, o => { o.Mag = new() { ["ESSM"] = [16, 16], ["CIWS"] = [2, 2] }; o.Channels = 2; o.Vip = true; });
        Ship("ddg", "Halyard", 14, 9, o => { o.Mag = DdgMag(); o.Channels = 4; });
        Ship("ddg", "Bowline", 14, -9, o => { o.Mag = DdgMag(); o.Channels = 4; });
        Ship("ddg", "Cleat", -11, 2, o => { o.Mag = new() { ["SM-2"] = [48, 48], ["ESSM"] = [16, 16], ["CIWS"] = [1, 1] }; o.Channels = 3; });
        Ship("ssn", "Tide", 34, -30, o => { o.Alt = -120; o.Ctrl = "Agent"; o.Mission = "ASW screen (sub-agent)"; o.Mag = new() { ["Torpedo"] = [12, 12], ["SSAM"] = [8, 8] }; o.Group = "Submarines"; });
        var osp = Ship("cvn", "Osprey", -6, -34, o => { o.Mag = new() { ["ESSM"] = [16, 16], ["CIWS"] = [2, 2] }; o.Channels = 2; o.Vip = true; });
        foreach (var (n, x, y) in new[] { ("Hawser", 8.0, -26.0), ("Sheave", -20.0, -30.0), ("Thimble", -6.0, -48.0) })
            Ship("ddg", n, x, y, o => { o.Mag = DdgMag(); o.Channels = 4; });
        var front = new List<string> { "Capstan", "Bollard", "Fairlead", "Gunwale", "Grommet", "Lanyard", "Shackle", "Bitts", "Winch" };
        Add(new SimObj { Side = "blue", Kind = "aew", Name = "Watch 1", X = -25, Y = 28, Alt = 9000, Hdg = 0, Spd = Kinds["aew"].Spd, OrbitAt = new(-25, 15, 13), Ctrl = "BDA", Mission = "AEW orbit", Fuel = .82, HomeBase = 1 });
        Add(new SimObj { Side = "blue", Kind = "aew", Name = "Watch 2", X = -32, Y = -44, Alt = 9200, Hdg = 0, Spd = Kinds["aew"].Spd, OrbitAt = new(-32, -50, 13), Ctrl = "BDA", Mission = "AEW orbit", Fuel = .86, HomeBase = osp.Id });
        int homeB = 1;
        void Flight(int n, string kind, string call, double cx, double cy, double r, double alt, string mission, int aam)
        {
            for (int i = 0; i < n; i++)
                Add(new SimObj
                {
                    Side = "blue", Kind = kind, Name = call + " " + (i + 1), X = cx + Math.Cos(i * 2.4) * r * .6, Y = cy + Math.Sin(i * 2.4) * r * .6,
                    Alt = alt + i * 60, Hdg = Math.PI / 2, Spd = Kinds[kind].Spd, OrbitAt = new(cx, cy, r), Aam = aam, AamMax = aam, Ctrl = "Mission",
                    Mission = mission, Group = mission, Doctrine = "Free", OnContact = "task", Fuel = .75 - (i % 4) * .03, HomeBase = homeB,
                });
        }
        void Wing5((string call, double x, double y, double alt, string mission)[] f, (string call, double x, double y, string mission) d)
        {
            foreach (var s in f) Flight(5, "ftr", s.call, s.x, s.y, 12, s.alt, s.mission, 8);
            Flight(6, "ucav", d.call, d.x, d.y, 6, 1250, d.mission, 4);
        }
        Wing5([("Viper", 62, 8, 6800, "CAP North"), ("Cobra", 55, -40, 6200, "CAP South"), ("Hawk", 22, 6, 5200, "Escort"), ("Saber", 84, 30, 7400, "Sweep North")], ("Wasp", 38, 26, "Drone swarm"));
        homeB = osp.Id;
        Wing5([("Falcon", 82, -16, 7200, "CAP Centre"), ("Kite", 48, -84, 6400, "CAP Far South"), ("Harrier", 16, -44, 5400, "Escort 2"), ("Lancer", 86, -60, 7000, "Sweep South")], ("Hornet", 42, -62, "Drone swarm 2"));
        string[] newDd = ["Anchor", "Bosun", "Cutwater", "Dory", "Ensign", "Fathom", "Gaff", "Halliard", "Inboard", "Jib", "Keelson", "Lee", "Mizzen", "Nock", "Oarlock"];
        int ddi = 0;
        var her = Ship("cvn", "Heron", -12, 44, o => { o.Mag = new() { ["ESSM"] = [16, 16], ["CIWS"] = [2, 2] }; o.Channels = 2; o.Vip = true; });
        for (int k = 0; k < 3; k++)
        {
            double a = k * 2 * Math.PI / 3 + .3;
            Ship("ddg", newDd[ddi++], her.X + Math.Cos(a) * 12, her.Y + Math.Sin(a) * 12, o => { o.Mag = DdgMag(); o.Channels = 4; });
        }
        Add(new SimObj { Side = "blue", Kind = "aew", Name = "Watch 3", X = -20, Y = 58, Alt = 9100, Hdg = 0, Spd = Kinds["aew"].Spd, OrbitAt = new(-22, 62, 13), Ctrl = "BDA", Mission = "AEW orbit", Fuel = .84, HomeBase = her.Id });
        homeB = her.Id;
        Wing5([("Eagle", 60, 52, 6600, "CAP North 2"), ("Condor", 42, 92, 6900, "CAP Far North"), ("Hound", 4, 50, 5000, "Escort 3"), ("Puma", 88, 64, 7300, "Sweep Far North")], ("Gnat", 30, 62, "Drone swarm 3"));
        front.AddRange(newDd.Skip(ddi));
        for (int i = 0; i < front.Count; i++) Ship("ddg", front[i], 72 + (i % 2) * 14, -120 + i * 12, o => { o.Mag = DdgMag(); o.Channels = 4; });
        foreach (var (n, x, y) in new[] { ("Trench", 140.0, -10.0), ("Shoal", 150.0, 50.0), ("Eddy", 150.0, -62.0), ("Riptide", 126.0, 100.0), ("Undertow", 126.0, -112.0) })
            Ship("ssn", n, x, y, o => { o.Alt = -150; o.Spd = .002; o.Mission = "Forward air-defence screen"; o.Mag = new() { ["Torpedo"] = [12, 12], ["SSAM"] = [8, 8] }; o.Channels = 2; o.Group = "Submarines"; });
        // every carrier keeps 6 armed fighters ready on deck
        string[] res = ["Ghost", "Talon", "Blade", "Fury", "Spectre", "Raven"];
        int ci = 0;
        foreach (var cv in Objs.Where(o => o.Kind == "cvn").ToList())
        {
            for (int i = 0; i < 6; i++)
            {
                var r = Add(new SimObj
                {
                    Side = "blue", Kind = "ftr", Name = res[ci] + " " + (i + 1), X = cv.X, Y = cv.Y, Alt = 300, Hdg = cv.Hdg, Spd = Kinds["ftr"].Spd,
                    OrbitAt = new(cv.X + 18, cv.Y, 10), Aam = 8, AamMax = 8, Ctrl = "Mission", Mission = "Deck reserve", Group = res[ci] + " (deck)",
                    Doctrine = "Free", OnContact = "task", Fuel = .95, HomeBase = cv.Id,
                });
                r.Home = r.OrbitAt; r.Alive = false; r.Deck = cv.Id; r.Ready = true; r.LandedT = -999;
            }
            ci++;
        }
        foreach (var o in Objs) if (o.Side == "blue" && o.Group is null) o.Group = o.Kind == "aew" ? "AEW" : o.Kind == "ssn" ? "Submarine" : "Surface group";

        const double escAhead = 14, redOff = 120;
        const int escAam = 8, swpAam = 4;
        int redN = 0;
        string Pad(int i) => i.ToString("00", CultureInfo.InvariantCulture);
        SimObj RedA(SimObj o)
        {
            o.Side = "red"; o.Hdg = 0; o.IdState ??= "unknown"; if (o.Ctrl == "BDA") o.Ctrl = "BDA";
            var r = Add(o);
            r.Hdg = Math.Atan2(-r.Y, -r.X);
            redN++;
            return r;
        }
        for (int w = 0; w < 4; w++)
            for (int j = 0; j < 10; j++)
            {
                int g = w * 10 + j;
                double cy = -180 + j * 40, cx = 228 + redOff + w * 90 + (Rnd() - .5) * 10;
                for (int i = 0; i < 8; i++)
                {
                    string kind = i < 2 ? "bmr" : "ftr";
                    double x = cx - (kind == "ftr" ? escAhead : 0) + (Rnd() - .5) * 12;
                    double y = cy + (Rnd() - .5) * 12;
                    double alt = kind == "bmr" ? 1800 + Rnd() * 2600 : 4500 + Rnd() * 5000;
                    double spd = Kinds[kind].Spd + (Rnd() - .5) * .02;
                    double off = (Rnd() - .5) * .5;
                    RedA(new SimObj
                    {
                        Kind = kind, Name = "R" + (g + 1) + "-" + Pad(i + 1), X = x, Y = y, Alt = alt, Spd = spd, Group = "R" + (g + 1), GType = "strike",
                        State = kind == "bmr" ? "ingress" : "escort", Asm = kind == "bmr" ? 16 : 0, Aam = kind == "ftr" ? escAam : 0, Pair = i >> 1, Off = off,
                    });
                }
            }
        double[] swY = [-240, -210, 210, 240, -5];
        for (int k = 0; k < 20; k++)
        {
            int w = k / 5;
            double cy = swY[k % 5], cx = 215 + redOff + w * 90;
            for (int i = 0; i < 10; i++)
            {
                double x = cx + (Rnd() - .5) * 12, y = cy + (Rnd() - .5) * 12, alt = 6000 + Rnd() * 4000;
                RedA(new SimObj { Kind = "ftr", Name = "R" + (41 + k) + "-" + Pad(i + 1), X = x, Y = y, Alt = alt, Spd = .27, Group = "R" + (41 + k), GType = "sweep", State = "escort", Asm = 0, Aam = swpAam, Pair = i >> 1, Off = cy > 0 ? -.25 : .25 });
            }
        }
        for (int q = 0; q < 2; q++)
        {
            var cap = new Orbit(190 + redOff, q == 1 ? -60 : 60, 18);
            for (int i = 0; i < 12; i++)
            {
                double x = cap.X + (Rnd() - .5) * 20, y = cap.Y + (Rnd() - .5) * 20, alt = 8000 + Rnd() * 3000;
                RedA(new SimObj { Kind = "ftr", Name = "DF" + (q + 1) + "-" + Pad(i + 1), X = x, Y = y, Alt = alt, Spd = .25, Group = "DF" + (q + 1), GType = "dcap", State = "dcap", Cap = cap, Asm = 0, Aam = 12, Pair = i >> 1, Off = 0 });
            }
        }
        for (int b = 0; b < 10; b++)
        {
            double ang = (b < 5 ? 20 + b * 10 : -20 - (b - 5) * 10) * Math.PI / 180, r = 300 + (b % 3) * 22, bx = Math.Cos(ang) * r, by = Math.Sin(ang) * r;
            for (int i = 0; i < 20; i++)
            {
                double x = bx + (i % 5) * 1.4 + Rnd();
                double y = by + Math.Floor(i / 5.0) * 1.4 + Rnd();
                double alt = 200 + Rnd() * 300;
                var o = Add(new SimObj { Side = "red", Kind = "uav", Name = "D" + (b * 20 + i + 1).ToString("000", CultureInfo.InvariantCulture), X = x, Y = y, Alt = alt, Spd = .07, IdState = "unknown", Group = "D" + (b + 1), Ctrl = "Script" });
                o.Hdg = Math.Atan2(-o.Y, -o.X - 10);
            }
        }
        for (int q = 0; q < 10; q++)
        {
            int sgn = q % 2 == 1 ? -1 : 1;
            for (int i = 0; i < 2; i++)
            {
                var o = Add(new SimObj
                {
                    Side = "red", Kind = "ftr", Name = "S-" + Pad(q * 2 + i + 1), X = 200 + q * 14 + i * 1.5, Y = sgn * (98 + q * 10) + i, Alt = 9500, Hdg = 0, Spd = .24,
                    IdState = "unknown", Group = "S" + (q + 1), GType = "stealth", State = "escort", Asm = 0, Aam = 0, Pair = i, Off = sgn * .5, Ctrl = "BDA", Stealth = true, Seen = false,
                });
                o.Hdg = Math.Atan2(-o.Y, -o.X);
            }
        }
        LogMsg("contact", $"New air contacts: 62 groups, {redN} tracks in four waves, bearing 040–140, 335–620 km. Identity unknown.", 300, 0);
        LogMsg("contact", "Slow low contacts: 200 tracks in ten blocks, north and south, about 300–345 km. Identity unknown.", 170, 112);
        LogMsg("orders", "Sea control ALPHA active. Doctrine: weapons free against missiles, tight against aircraft.", 0, 0);
        LogMsg("orders", "Six submarines carry sub-launched SAMs. Each missile needs a data link from a fighter, drone or destroyer holding the target. With no link for 10 s it self-destructs.", 0, 0);
    }

    // ------------------------------------------------------------ spatial grid

    private void GridBuild()
    {
        _grid = new Dictionary<long, List<SimObj>>();
        _aamOn = new HashSet<int>();
        _vips = new List<SimObj>();
        foreach (var t in Objs)
        {
            if (!t.Alive) continue;
            if (t.Vip && t.Side == "blue") _vips.Add(t);
            if (t.Kind == "aam" && t.Target is int tg) _aamOn.Add(tg);
            long k = GKey((long)Math.Floor(t.X / GC), (long)Math.Floor(t.Y / GC));
            if (!_grid.TryGetValue(k, out var a)) _grid[k] = a = new List<SimObj>();
            a.Add(t);
        }
    }

    private SimObj? NearestLin(SimObj o, Func<SimObj, bool> pred, double maxD)
    {
        SimObj? b = null; double bd = maxD;
        foreach (var t in Objs)
        {
            if (!t.Alive || t == o || !pred(t)) continue;
            double d = Dist(o, t);
            if (d < bd) { bd = d; b = t; }
        }
        return b;
    }

    /// <summary>Ring search over the grid; objects created this step are found next step.</summary>
    private SimObj? Nearest(SimObj o, Func<SimObj, bool> pred, double maxD)
    {
        if (maxD > 400) return NearestLin(o, pred, maxD);
        long cx = (long)Math.Floor(o.X / GC), cy = (long)Math.Floor(o.Y / GC);
        int R = (int)Math.Ceiling(maxD / GC) + 1;
        SimObj? b = null; double bd = maxD;
        void Scan(long x, long y)
        {
            if (!_grid.TryGetValue(GKey(x, y), out var a)) return;
            foreach (var t in a)
            {
                if (!t.Alive || t == o || !pred(t)) continue;
                double d = Dist(o, t);
                if (d < bd) { bd = d; b = t; }
            }
        }
        for (int r = 0; r <= R; r++)
        {
            if ((r - 1) * GC > bd) break;
            if (r == 0) { Scan(cx, cy); continue; }
            for (int dx = -r; dx <= r; dx++) { Scan(cx + dx, cy - r); Scan(cx + dx, cy + r); }
            for (int dy = -r + 1; dy <= r - 1; dy++) { Scan(cx - r, cy + dy); Scan(cx + r, cy + dy); }
        }
        return b;
    }

    /// <summary>Nearest live object matching a predicate within maxD km of a point (used for Red's fog picture).</summary>
    public bool AnyWithin(double x, double y, Func<SimObj, bool> pred, double maxD)
    {
        var probe = new SimObj { X = x, Y = y };
        return Nearest(probe, pred, maxD) is not null;
    }

    /// <summary>Rebuild the spatial grid outside a step (for queries from the data source).</summary>
    public void RefreshGrid() => GridBuild();

    // ------------------------------------------------------------ behaviours

    private SimObj? ContactTarget(SimObj o, int? excl = null)
    {
        if (SimT < o.CtT && o.CtEx == excl)
        {
            var c = o.CtId is not null ? Get(o.CtId) : null;
            if (c is null || c.Alive) return c;
        }
        var r = ContactTarget0(o, excl);
        o.CtT = SimT + .5; o.CtEx = excl; o.CtId = r?.Id;
        return r;
    }

    private SimObj? ContactTarget0(SimObj o, int? excl)
    {
        double R = SensorR(o);
        if (R == 0) return null;
        return (o.Aam > 0 ? Nearest(o, x => x.Side == "red" && (x.Kind == "ftr" || x.Kind == "bmr") && x.IdState == "hostile" && x.Id != excl, R) : null)
               ?? Nearest(o, x => IsMslThreatStatic(x) && FreeT(x) && x.Id != excl, R * .6);
    }

    private bool ThreatensVip(SimObj x, List<SimObj> vips)
    {
        if (x.Side != "red" || !x.Alive) return false;
        if (x.Kind == "asm") return Get(x.Target)?.Vip == true;
        if (x.Kind is "bmr" or "ftr" or "uav") return x.IdState == "hostile" && vips.Any(v => Dist(v, x) < VipR);
        return false;
    }

    /// <summary>Target choice for Blue fighters and drones: self-defence, VIP threats, aircraft, then missiles and drones.</summary>
    private SimObj? AirTarget(SimObj o)
    {
        var vips = _vips;
        bool ucav = o.Kind == "ucav";
        if (o.Aam > 0)
        {
            var th = Nearest(o, x => x.Side == "red" && x.Kind == "ftr" && x.IdState == "hostile", 25);
            if (th is not null) return th;
        }
        if (vips.Count > 0)
        {
            var t = Nearest(o, x => ThreatensVip(x, vips) && (x.Kind != "asm" || FreeT(x)), ucav ? 60 : 120);
            if (t is not null) return t;
        }
        var a = Posture == "Proactive"
            ? Nearest(o, x => x.Side == "red" && x.Kind == "bmr" && x.IdState == "hostile" && x.State != "egress", 230)
              ?? Nearest(o, x => x.Side == "red" && x.Kind == "ftr" && x.IdState == "hostile", 75)
            : Nearest(o, x => x.Side == "red" && (x.Kind == "ftr" || x.Kind == "bmr") && x.IdState == "hostile", 75);
        if (a is not null) return a;
        return Nearest(o, x => IsMslThreatStatic(x) && FreeT(x), ucav ? 45 : 80);
    }

    /// <summary>Fire at a target: AAM in range, or the gun against missiles and drones up close.</summary>
    private void EngageAir(SimObj o, SimObj t, double dt, double cool = 0)
    {
        if (o.Aam > 0 && Dist(o, t) < (o.Kind == "ucav" ? 20 : 42) && SimT > o.Cool && FreeT(t))
        {
            Launch("aam", o, t, "AAM"); o.Aam--; Stats.ExpBlue["AAM"]++; o.Cool = SimT + (cool != 0 ? cool : 3.5); Report(o, "AAM", 1);
        }
        if ((t.Kind == "asm" || t.Kind == "uav") && t.Alive && Dist(o, t) < 1.6 && Rnd() < .25 * dt)
        {
            Kill(t); Stats.Intercepts++; Event("ciws", t.X, t.Y); Report(o, "Gun", 1);
            LogMsg("intercept", $"{o.Name} shot down {(t.Kind == "asm" ? "missile" : "drone")} {t.Name} with its gun.", t.X, t.Y);
        }
    }

    private List<SimObj> BlueShips() => Objs.Where(o => o.Alive && o.Side == "blue" && (o.Kind == "cvn" || o.Kind == "ddg")).ToList();

    private static void DoOrbit(SimObj o, double dt)
    {
        var c = o.OrbitAt!.Value;
        double a = Math.Atan2(o.Y - c.Y, o.X - c.X), d = Math.Sqrt((o.X - c.X) * (o.X - c.X) + (o.Y - c.Y) * (o.Y - c.Y));
        SteerTo(o, a + Math.PI / 2 + (d - c.R) * .05, .35, dt);
    }

    private SimObj Launch(string kind, SimObj from, SimObj target, string? weapon)
    {
        if (from.Side == "blue" && !_blueFirst)
        {
            _blueFirst = true;
            LogAt("weapons", $"First Blue launch: {from.Name} fired {weapon ?? kind.ToUpperInvariant()} at {target.Name}, {F0(Dist(from, target))} km. Blue has opened fire.", from);
            SayComms("Team", from.Name, "blue", $"First shot of the engagement: {weapon ?? kind.ToUpperInvariant()} away at {target.Name}.");
            if (PauseOnEvents) Pause($"First Blue launch: {from.Name} fired {weapon ?? kind.ToUpperInvariant()} at {target.Name}, {F0(Dist(from, target))} km.");
        }
        var m = Add(new SimObj
        {
            Side = from.Side, Kind = kind, Name = kind == "asm" ? weapon ?? "ASM" : ShotName(from, weapon), X = from.X, Y = from.Y,
            Alt = kind == "asm" ? 15 : Math.Max(30, from.Alt == 0 ? 30 : from.Alt), Hdg = Math.Atan2(target.Y - from.Y, target.X - from.X),
            Spd = Kinds[kind].Spd, Target = target.Id, Born = SimT, Shooter = from.Id, Weapon = weapon, Ctrl = "BDA",
        });
        if (kind != "asm") target.EngagedBy = m.Id;
        if (kind != "asm" && FriendlyFire > 0 && Rnd() < FriendlyFire) m.Ff = true;
        return m;
    }

    private static string ShotName(SimObj from, string? weapon)
    {
        from.Shots++;
        var letters = new string(from.Name.Where(char.IsLetter).ToArray());
        var ab = letters[..Math.Min(3, letters.Length)].ToUpperInvariant() + new string(from.Name.Where(char.IsDigit).ToArray());
        return $"{ab}-{(weapon ?? "MSL").Replace("-", "", StringComparison.Ordinal)}-{from.Shots.ToString("00", CultureInfo.InvariantCulture)}";
    }

    private void AlertLaunch()
    {
        _alertDone = true;
        foreach (var (c, call, oy) in new[] { (Get(1), "Lance", 18.0), (Objs.FirstOrDefault(o => o.Name == "Osprey"), "Saber", -40.0) })
        {
            if (c is null || !c.Alive) continue;
            for (int i = 0; i < 4; i++)
                Add(new SimObj
                {
                    Side = "blue", Kind = "ftr", Name = call + " " + (i + 1), X = c.X + 1 + i * .4, Y = c.Y + (i - 1.5) * .6, Alt = 4500 + i * 200, Hdg = 0, Spd = .25,
                    OrbitAt = new(45, oy + (i < 2 ? 10 : -10), 10), Aam = 8, AamMax = 8, Ctrl = "Mission", Doctrine = "Free", OnContact = "task",
                    Mission = "Alert intercept", Group = "Alert", Fuel = .95, HomeBase = c.Id,
                });
            LogAt("orders", $"{c.Name} launched four alert fighters ({call} 1–4) to hit the strike aircraft before they reach launch range.", c);
        }
    }

    private void Kill(SimObj o)
    {
        if (!o.Alive) return;
        o.Alive = false;
        o.DiedT = SimT;
        if (Hooks is not null) Died.Add(o.Id);
    }

    private void KillObj(SimObj o, string? how = null)
    {
        Kill(o);
        if (o.Kind is "asm" or "uav" or "sam" or "aam" or "ssam") return;
        var lost = o.Side == "blue" ? Stats.LostBlue : Stats.LostRed;
        lost[o.K.Cls] = lost.GetValueOrDefault(o.K.Cls) + 1;
        Event("kill", o.X, o.Y);
        LogAt("damage", $"{o.Name} ({o.K.Cls.ToLowerInvariant()}) lost{(how is not null ? ": " + how : "")}.", o);
    }

    private bool Advance(SimObj o, double dt)
    {
        if (!AdvanceAuto || !_advanceOn || o.Doctrine == "Hold" || !(o.Aam > 0) || (o.Fuel ?? 1) < .15 || o.Mission == "Deck reserve") return false;
        if (SimT >= o.AdvT)
        {
            o.AdvT = SimT + 2 + (o.Id % 7) * .05;
            var h = Nearest(o, x => x.Side == "red" && x.IdState == "hostile" && !x.K.Missile && x.Kind != "uav" && (!x.Stealth || x.Seen), 400);
            o.AdvId = h?.Id;
        }
        var hh = o.AdvId is not null ? Get(o.AdvId) : null;
        if (hh is null || !hh.Alive) { o.AdvT = 0; return false; }
        o.Spd = o.K.Spd;
        SteerTo(o, Math.Atan2(hh.Y - o.Y, hh.X - o.X), .4, dt);
        return true;
    }

    private void AdvanceStart(SimObj t)
    {
        if (_advanceOn) return;
        _advanceOn = true;
        int n = Objs.Count(o => o.Alive && o.Side == "blue" && (o.Kind == "ftr" || o.Kind == "ucav") && o.Doctrine != "Hold");
        if (AdvanceAuto) LogAt("orders", $"First hostile identified: {t.Name}. Advance on contact: {n} fighters and drones turning toward the enemy; courses follow the nearest identified hostile.", t);
    }

    private void Report(SimObj ship, string key, int n)
    {
        if (!_rep.TryGetValue(ship.Id, out var r)) _rep[ship.Id] = r = new Dictionary<string, int>();
        r[key] = r.GetValueOrDefault(key) + n;
    }

    private void SelfDefend(SimObj o)
    {
        if ((o.Kind == "ftr" || o.Kind == "ucav") && o.Aam > 0)
        {
            var t = Nearest(o, x => x.Side == "red" && (x.Kind == "ftr" || x.Kind == "bmr") && x.IdState == "hostile", 30) ?? Nearest(o, x => IsMslThreatStatic(x) && FreeT(x), 25);
            if (t is not null && SimT > o.Cool && !(t.EngagedBy is not null && AliveId(t.EngagedBy)))
            {
                Launch("aam", o, t, "AAM"); o.Aam--; Stats.ExpBlue["AAM"]++; o.Cool = SimT + 3.5; Report(o, "AAM", 1);
            }
        }
    }

    private void Think(SimObj o, double dt)
    {
        if (o.Side == "blue" && (o.ApproachBase is not null || o.Bda is { Commanded: true } || o.BaseSpd is not null) && ThinkCommanded(o, dt)) return;
        switch (o.Kind)
        {
            case "aew":
                DoOrbit(o, dt);
                o.Fuel = Math.Max(0, (o.Fuel ?? 0) - dt * .00004);
                break;
            case "ftr":
            case "ucav":
                if (o.Side == "blue") ThinkBlueAir(o, dt);
                else ThinkRedAir(o, dt);
                break;
            case "bmr":
            {
                var cv = Get(1)!;
                var tgt = cv.Alive ? cv : BlueShips().FirstOrDefault();
                if (o.State == "ingress")
                {
                    SteerTo(o, Math.Atan2(-o.Y, -o.X), .15, dt);
                    if (o.Asm > 0 && (o.Id + (long)Math.Floor(SimT * 10 + .5)) % 3 == 0)
                    {
                        var sh = Nearest(o, x => x.Side == "blue" && (x.Kind == "ddg" || x.Kind == "cvn"), AsmLr);
                        if (sh is not null || (tgt is not null && Dist(o, tgt) < 118)) { o.State = "launch"; o.Cool = SimT + Rnd() * 1.5; }
                    }
                }
                else if (o.State == "launch")
                {
                    if (SimT > o.Cool && o.Asm > 0)
                    {
                        var ships = BlueShips();
                        if (ships.Count > 0)
                        {
                            double r = Rnd();
                            var t = r < .4 && cv.Alive ? cv : ships[(int)Math.Floor(Rnd() * ships.Count)];
                            Launch("asm", o, t, "V" + (Stats.ExpRed["ASM"] + 1).ToString("000", CultureInfo.InvariantCulture));
                            o.Asm--; Stats.ExpRed["ASM"]++; o.Cool = SimT + 1.1;
                            if (!_firstLaunch) { _firstLaunch = true; OnFirstLaunch(o); }
                        }
                    }
                    if (o.Asm <= 0) { o.State = "egress"; o.Egress = (Rnd() - .5) * 1.6; o.Spd = .27; }
                }
                else
                {
                    SteerTo(o, o.Egress, .22, dt);
                    if (o.X > 330) Kill(o);
                }
                break;
            }
            case "uav":
            {
                var c = Get(4);
                var t = c is not null && c.Alive ? c : Nearest(o, x => x.Side == "blue" && (x.Kind == "ddg" || x.Kind == "cvn"), 999);
                if (t is null) break;
                SteerTo(o, Math.Atan2(t.Y - o.Y, t.X - o.X), .4, dt);
                if (Dist(o, t) < .5) Impact(o, t, 9 + Rnd() * 6);
                break;
            }
            case "asm":
            {
                var t = Get(o.Target);
                if (t is null || !t.Alive)
                {
                    var n = Nearest(o, x => x.Side == "blue" && (x.Kind == "ddg" || x.Kind == "cvn"), 999);
                    if (n is null) { Kill(o); break; }
                    o.Target = n.Id; t = n;
                }
                SteerTo(o, Math.Atan2(t.Y - o.Y, t.X - o.X), .6, dt);
                if (Dist(o, t) < .35) Impact(o, t, 20 + Rnd() * 12);
                break;
            }
            case "sam":
            case "aam":
            case "ssam":
                ThinkMissile(o, dt);
                break;
        }
    }

    private void ThinkBlueAir(SimObj o, double dt)
    {
        o.Fuel = Math.Max(0, (o.Fuel ?? 0) - dt * .00008);
        if (SimT >= o.TgtT)
        {
            var nt = o.Doctrine == "Free" || o.Doctrine is null ? AirTarget(o) : null;
            o.TgtId = nt?.Id;
            o.TgtT = SimT + .8 + (o.Id % 5) * .1;
        }
        var t = o.TgtId is not null ? Get(o.TgtId) : null;
        if (t is not null && !t.Alive) { t = null; o.TgtT = 0; }
        if (t is not null) { SteerTo(o, Math.Atan2(t.Y - o.Y, t.X - o.X), .5, dt); EngageAir(o, t, dt); }
        else if (!Advance(o, dt)) { DoOrbit(o, dt); if (o.Doctrine == "Tight") SelfDefend(o); }
    }

    private void ThinkRedAir(SimObj o, double dt)
    {
        static bool BlueAir(SimObj x) => x.Side == "blue" && (x.Kind == "ftr" || x.Kind == "ucav" || x.Kind == "aew");
        void RedShoot(SimObj? v)
        {
            if (v is not null && o.Aam > 0 && Dist(o, v) < 35 && SimT > o.Cool && FreeT(v))
            {
                Launch("aam", o, v, "AAM"); o.Aam--; Stats.ExpRed["AAM"]++; o.Cool = SimT + 4;
            }
        }
        if (o.State == "dcap")
        {
            var c = o.Cap!.Value;
            var v = Nearest(o, x => BlueAir(x) && Math.Sqrt((x.X - c.X) * (x.X - c.X) + (x.Y - c.Y) * (x.Y - c.Y)) < 90, 140);
            if (v is not null) { o.Spd = .3; SteerTo(o, Math.Atan2(v.Y - o.Y, v.X - o.X), .35, dt); RedShoot(v); }
            else { o.Spd = .25; o.OrbitAt = c; DoOrbit(o, dt); }
        }
        else if (o.State == "escort")
        {
            SteerTo(o, Math.Atan2(-o.Y, -o.X) + o.Off, .2, dt);
            bool thr = _aamOn.Contains(o.Id) || ((o.Id + (long)Math.Floor(SimT * 10 + .5)) % 5 == 0 && Nearest(o, BlueAir, 40) is not null);
            if (thr) { o.State = "break"; o.BreakHdg = o.Hdg + (o.Pair % 2 == 1 ? 1 : -1) * (1.3 + Rnd() * .7); o.Until = SimT + 16 + Rnd() * 6; o.Spd = .3; }
        }
        else if (o.State == "break")
        {
            SteerTo(o, o.BreakHdg, .32, dt);
            if (SimT > o.Until) o.State = "engage";
        }
        else if (o.State == "engage")
        {
            var v = Nearest(o, BlueAir, 120);
            if (v is not null) { SteerTo(o, Math.Atan2(v.Y - o.Y, v.X - o.X), .3, dt); RedShoot(v); }
            else o.State = "egress";
            if (SimT > o.Until + 70) o.State = "egress";
        }
        else
        {
            o.Spd = .3;
            SteerTo(o, o.Off * .8, .25, dt);
        }
        if (o.State == "egress" && o.X > 330) Kill(o);
    }

    private void ThinkMissile(SimObj o, double dt)
    {
        var t = Get(o.Target);
        if (o.Ff && !o.FfDone && SimT - o.Born > 3)
        {
            var f = Nearest(o, x => x.Side == o.Side && x.Id != o.Shooter && !x.K.Missile, 25);
            if (f is not null)
            {
                if (t is not null && t.EngagedBy == o.Id) t.EngagedBy = null;
                o.Target = f.Id; o.FfDone = true; t = f;
            }
        }
        if (t is null || !t.Alive)
        {
            var n = Nearest(o, x => x.Side != o.Side
                && (o.Kind == "ssam" ? (x.Kind == "ftr" || x.Kind == "bmr")
                    : o.Kind == "sam" ? (x.Kind is "asm" or "uav" or "ftr" or "bmr")
                    : (x.Kind is "ftr" or "bmr" or "asm" or "uav"))
                && (!x.Stealth || x.Seen) && !(x.EngagedBy is not null && AliveId(x.EngagedBy)), 14);
            if (n is null) { Kill(o); return; }
            o.Target = n.Id; n.EngagedBy = o.Id; t = n;
        }
        if (o.Kind == "ssam" && !SsamGuide(o, t, dt)) return;
        SteerTo(o, Math.Atan2(t.Y - o.Y, t.X - o.X), 3, dt);
        if (SimT - o.Born > (o.Kind == "sam" ? 80 : 60)) { Kill(o); return; }
        if (Dist(o, t) < .45 && t.Side == o.Side)
        {
            Kill(o); Stats.Ff++; Event("impact", t.X, t.Y);
            if (t.Hp is not null)
            {
                t.Hp = Math.Max(0, t.Hp.Value - 12);
                LogAt("damage", $"Friendly fire: {o.Name} hit {t.Name}. Damage {F0(100 - t.Hp.Value)}%.", t);
                if (t.Hp <= 0) KillObj(t, "friendly fire");
            }
            else
            {
                LogAt("damage", $"Friendly fire: {o.Name} hit {t.Name}.", t);
                KillObj(t, "friendly fire");
            }
            if (PauseOnEvents && Stats.Ff == 1)
                Pause($"Friendly fire: {o.Name} hit {t.Name}. One missile in {Math.Round(1 / FriendlyFire).ToString("#,0", CultureInfo.InvariantCulture)} is set to go astray.");
            return;
        }
        if (Dist(o, t) < .45)
        {
            Kill(o);
            double pk = o.Kind == "sam" ? (t.Kind == "asm" ? .72 : .8) : (t.Kind is "asm" or "uav" ? .55 : .62);
            if (Rnd() < pk)
            {
                KillObj(t);
                if (t.Kind is "asm" or "uav") { Stats.Intercepts++; Event("intercept", t.X, t.Y); }
                else Event("kill", t.X, t.Y);
                Kill(t);
            }
            else { t.EngagedBy = null; Event("miss", t.X, t.Y); }
        }
    }

    private SimObj? LinkFor(SimObj t) =>
        Nearest(t, x => x.Side == "blue" && LinkR.ContainsKey(x.Kind) && Dist(x, t) <= LinkR[x.Kind], 60);

    /// <summary>Sub-launched SAM data link: hands over between holders; coasts and self-destructs after 10 s without one.</summary>
    private bool SsamGuide(SimObj o, SimObj t, double dt)
    {
        if (SimT >= o.Lchk)
        {
            o.Lchk = SimT + .5;
            var L = Get(o.Link);
            if (!(L is not null && L.Alive && Dist(L, t) <= LinkR[L.Kind]))
            {
                var n = LinkFor(t);
                if (n is not null)
                {
                    if (o.LostT is not null) LogAt("weapons", $"{o.Name}: data link regained through {n.Name} after {F1(SimT - o.LostT.Value)} s.", o);
                    else Stats.SsamHand++;
                    o.Link = n.Id; o.LostT = null;
                }
                else if (o.LostT is null)
                {
                    o.LostT = SimT;
                    LogAt("weapons", $"{o.Name}: data link lost ({(L is not null && !L.Alive ? L.Name + " destroyed" : "no unit holds " + t.Name)}). Self-destruct in {SsamSd} s unless another unit takes the link.", o);
                }
            }
        }
        if (o.LostT is null) { o.Lkx = t.X; o.Lky = t.Y; return true; }
        if (SimT - o.LostT >= SsamSd)
        {
            Kill(o); Stats.SsamSd++; Event("miss", o.X, o.Y);
            if (t.EngagedBy == o.Id) t.EngagedBy = null;
            LogAt("weapons", $"{o.Name} self-destructed: no data link for {SsamSd} s.", o);
            return false;
        }
        SteerTo(o, Math.Atan2(o.Lky - o.Y, o.Lkx - o.X), 3, dt);
        return false;
    }

    private void SubFire(SimObj s)
    {
        if (s.Doctrine == "Hold" || s.Mag is null || !s.Mag.TryGetValue("SSAM", out var ss) || ss[0] <= 0) return;
        int inflight = 0;
        foreach (var m in Objs) if (m.Alive && m.Kind == "ssam" && m.Shooter == s.Id) inflight++;
        int free = (s.Channels != 0 ? s.Channels : 2) - inflight;
        if (free <= 0) return;
        var c = new List<(double d, SimObj t)>();
        foreach (var t in Objs)
        {
            if (!t.Alive || t.Side != "red" || (t.Kind != "ftr" && t.Kind != "bmr") || t.IdState != "hostile" || (t.Stealth && !t.Seen)) continue;
            if (t.EngagedBy is not null && AliveId(t.EngagedBy)) continue;
            double d = Dist(s, t);
            if (d <= SsamR) c.Add((d, t));
        }
        c.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (var (d, t) in c)
        {
            if (free <= 0 || ss[0] <= 0) break;
            var L = LinkFor(t);
            if (L is null) continue;
            ss[0]--; Stats.ExpBlue["SSAM"]++;
            var m = Launch("ssam", s, t, "SSAM");
            m.Link = L.Id; m.Lkx = t.X; m.Lky = t.Y;
            Report(s, "SSAM", 1); free--;
            if (!_ssamFirst) { _ssamFirst = true; LogAt("weapons", $"{s.Name} launched the first sub-launched SAM at {t.Name}, {F0(d)} km, guided by {L.Name}'s data link.", s); }
            if (ss[0] == 0) LogAt("weapons", $"{s.Name}: SSAM magazine empty.", s);
        }
    }

    private void Impact(SimObj m, SimObj ship, double dmg)
    {
        Kill(m);
        Stats.Hits++;
        ship.Hp = Math.Max(0, (ship.Hp ?? 100) - dmg);
        Event("impact", ship.X, ship.Y);
        LogAt("damage", $"{ship.Name} hit by {(m.Kind == "uav" ? "drone" : "missile")} {m.Name}. Damage {F0(100 - ship.Hp.Value)}%.", ship);
        if (m.Kind == "uav") Stats.ExpRed["Drones"]++;
        if (ship.Hp <= 0) KillObj(ship, "sunk");
    }

    private void ShipsFire()
    {
        var vips = _vips;
        for (int si = 0; si < Objs.Count; si++) // index loop: launches append to Objs (JS for..of semantics)
        {
            var s = Objs[si];
            if (s.Alive && s.Side == "blue" && s.Kind == "ssn") { SubFire(s); continue; }
            if (!s.Alive || s.Side != "blue" || (s.Kind != "ddg" && s.Kind != "cvn")) continue;
            if (s.Doctrine == "Hold" || s.Emcon == "Passive") continue;
            int inflight = 0;
            foreach (var m in Objs) if (m.Alive && m.Kind == "sam" && m.Shooter == s.Id) inflight++;
            int free = s.Channels - inflight;
            if (free <= 0) continue;
            var c = new List<(double d, SimObj t, int p)>();
            var bda = s.Bda; // C2 engine control: override and secondary targets first, filter, full range
            foreach (var t in Objs)
            {
                if (!t.Alive || t.Side != "red") continue;
                if (t.EngagedBy is not null && AliveId(t.EngagedBy)) continue;
                bool msl = t.Kind == "asm" || t.Kind == "uav";
                bool assigned = bda is not null && (t.Id == bda.Override || bda.Secondary.Contains(t.Id));
                if (!msl && !assigned)
                {
                    if (bda?.Filter is { } flt && !flt.Allows((uint)t.Id)) continue;
                    if (s.Doctrine == "Tight" && t.IdState != "hostile") continue;
                    if (t.IdState == "unknown") continue;
                }
                double d = Dist(s, t), r = t.Alt < 100 ? 38 : (Posture == "Proactive" || bda?.RangeMax == true ? 90 : 60);
                int pri = bda is not null && t.Id == bda.Override ? -2 : assigned ? -1 : vips.Count > 0 && ThreatensVip(t, vips) ? 0 : 1;
                if (d <= r) c.Add((d, t, pri));
            }
            c.Sort((a, b) => a.p != b.p ? a.p.CompareTo(b.p) : a.d.CompareTo(b.d));
            var mag = s.Mag!;
            foreach (var (d, t, _) in c)
            {
                if (free <= 0) break;
                string? w = null;
                if (d <= 22 && mag.TryGetValue("ESSM", out var e1) && e1[0] > 0) w = "ESSM";
                else if (mag.TryGetValue("SM-2", out var sm) && sm[0] > 0) w = "SM-2";
                if (w is null) continue;
                mag[w][0]--; Stats.ExpBlue[w]++;
                Launch("sam", s, t, w); Report(s, w, 1); free--;
                if (mag[w][0] == 0) LogAt("weapons", $"{s.Name}: {w} magazine empty.", s);
            }
        }
    }

    private void Ciws(double dt)
    {
        foreach (var s in Objs)
        {
            if (!s.Alive || s.Side != "blue" || (s.Kind != "ddg" && s.Kind != "cvn")) continue;
            long cx = (long)Math.Floor(s.X / GC), cy = (long)Math.Floor(s.Y / GC);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!_grid.TryGetValue(GKey(cx + dx, cy + dy), out var cell)) continue;
                    foreach (var t in cell)
                    {
                        if (!t.Alive || (t.Kind != "asm" && t.Kind != "uav")) continue;
                        if (Dist(s, t) < 1.8 && Rnd() < .5 * dt)
                        {
                            Kill(t); Stats.Ciws++; Stats.ExpBlue["CIWS"]++; Event("ciws", t.X, t.Y); Report(s, "CIWS", 1);
                        }
                    }
                }
        }
    }

    private void Classify()
    {
        // ships and AEW search wide; fighters and drones identify with their own radar at short range
        var sensors = Objs.Where(o => o.Alive && o.Side == "blue" && o.Kind != "ssn" && !o.K.Missile).ToList();
        var kestrel = Get(1)!;
        foreach (var o in Objs)
        {
            if (!o.Alive || !o.Stealth || o.Seen) continue;
            var s = Objs.FirstOrDefault(b => b.Alive && b.Side == "blue" && !b.K.Missile && Dist(b, o) < (b.Kind == "aew" ? 40 : b.Kind == "ucav" ? 18 : b.Kind == "ftr" ? 25 : 30));
            if (s is not null)
            {
                o.Seen = true; o.IdState = "hostile"; AdvanceStart(o);
                LogAt("contact", $"Stealth contact: {s.Name}'s own radar detected {o.Name} at {F1(Dist(s, o))} km, bearing {Pad3(BrgOf(kestrel, o))} from Kestrel. It was not on the shared picture.", o);
            }
        }
        foreach (var o in Objs)
        {
            if (!o.Alive || o.Side != "red" || o.IdState != "unknown" || o.Stealth) continue;
            if (sensors.Any(s => Dist(s, o) < (s.Kind == "aew" ? 175 : s.Kind == "ftr" ? 45 : s.Kind == "ucav" ? 25 : 150)))
            {
                o.IdState = "hostile"; AdvanceStart(o);
                var g = o.Group ?? "";
                if (!_groupClassified.ContainsKey(g))
                {
                    _groupClassified[g] = 1;
                    int n = Objs.Count(x => x.Group == o.Group);
                    LogAt("contact", o.Kind == "uav"
                        ? $"Drone block {o.Group} classified hostile ({n} tracks), heading for Cleat."
                        : $"Group {o.Group} classified hostile: {n} aircraft, {(o.GType == "sweep" ? "fighter sweep" : o.GType == "dcap" ? "defensive fighter squadron" : "strike package with escorts")}.", o);
                }
            }
        }
    }

    private void FlushReports()
    {
        foreach (var (id, r) in _rep)
        {
            var s = Get(id);
            if (s is null) continue;
            var parts = new List<string>();
            foreach (var (k, v) in r) parts.Add(k == "CIWS" ? $"CIWS {v} kill{(v > 1 ? "s" : "")}" : $"{k} ×{v}");
            if (parts.Count == 0) continue;
            string left = s.Mag is not null
                ? string.Join(", ", s.Mag.Where(kv => kv.Key != "CIWS" && kv.Key != "Torpedo").Select(kv => $"{kv.Key} {kv.Value[0]}/{kv.Value[1]}"))
                : $"AAM {s.Aam} left";
            LogAt("weapons", $"{s.Name} fired {string.Join(", ", parts)}. {left}.", s);
            if (s.Kind is "ddg" or "cvn" or "ftr") SayComms("Team", s.Name, "blue", $"Engaged. {string.Join(", ", parts)}. {left}.");
        }
        _rep.Clear();
    }

    private static readonly (double T, string Ch, string Who, string Side, string Text, bool Unv)[] Script =
    [
        (9, "Diplomatic", "Red commander", "red", "Task Group 1, you are inside our declared exclusion zone. Turn south within ten minutes or you will be engaged.", false),
        (16, "Team", "Tide (sub-agent)", "blue", "Holding the ASW screen 45 km ahead. No submerged contacts. Passive sonar only.", false),
        (150, "Broadcast", "\"Kestrel actual\"", "unknown", "All Blue units, cease fire, cease fire. Return to base.", true),
    ];

    private void Scripted()
    {
        while (_scriptIdx < Script.Length && SimT >= Script[_scriptIdx].T)
        {
            var s = Script[_scriptIdx];
            SayComms(s.Ch, s.Who, s.Side, s.Text, s.Unv);
            _scriptIdx++;
        }
    }

    private void OnFirstLaunch(SimObj o)
    {
        _firstLaunchT = SimT;
        var c = Get(1)!;
        LogAt("contact", $"Missile launch. Vampires inbound from bearing {Pad3(BrgOf(c, o))}, {F0(Dist(c, o))} km.", o);
        if (PauseOnEvents) Pause($"Missile launch detected from {o.Name}, bearing {Pad3(BrgOf(c, o))}, {F0(Dist(c, o))} km. More launches will follow.");
    }

    private void DeckTick()
    {
        foreach (var o in Objs)
        {
            if (o.Deck is null) continue;
            var b = Get(o.Deck);
            if (b is null || !b.Alive)
            {
                o.Deck = null;
                o.Alive = true; // KillObj expects a live object
                o.X = b?.X ?? o.X; o.Y = b?.Y ?? o.Y;
                KillObj(o, $"lost on deck with {(b is not null ? b.Name : "the carrier")}");
                continue;
            }
            if (!o.Ready && SimT - o.LandedT >= DeckTurn)
            {
                o.Ready = true;
                if (o.AamMax is not null) o.Aam = o.AamMax.Value;
                if (o.Fuel is not null) o.Fuel = .95;
            }
        }
    }

    /// <summary>Aircraft on a carrier's deck (the Flight deck section of the unit panel).</summary>
    public IEnumerable<SimObj> DeckOf(SimObj carrier) => Objs.Where(o => o.Deck == carrier.Id);

    // ------------------------------------------------------------ the main loop

    /// <summary>Advance the battle by <paramref name="dt"/> seconds of game time (fixed 0.1 s steps, at most 400 per call).</summary>
    public void Simulate(double dt)
    {
        _acc += dt;
        int steps = 0;
        while (_acc >= Step && steps < 400)
        {
            DoStep(Step);
            _acc -= Step;
            steps++;
            if (PauseRequest is not null) { _acc = 0; break; } // a pause takes effect within the step that raised it
        }
    }

    private void DoStep(double dt)
    {
        SimT += dt;
        GridBuild();
        var list = Objs;
        for (int i = 0; i < list.Count; i++) { var o = list[i]; if (o.Alive) Think(o, dt); }
        foreach (var o in Objs)
        {
            if (!o.Alive) continue;
            o.X += Math.Cos(o.Hdg) * o.Spd * dt;
            o.Y += Math.Sin(o.Hdg) * o.Spd * dt;
            if (SimT - o.LastTrail >= 1)
            {
                o.Trail.Add(o.X); o.Trail.Add(o.Y);
                if (o.Trail.Count > 60) o.Trail.RemoveRange(0, 2);
                o.LastTrail = SimT;
            }
        }
        if (Math.Floor(SimT * 2) != Math.Floor((SimT - dt) * 2)) ShipsFire();
        Ciws(dt);
        if (Math.Floor(SimT) != Math.Floor(SimT - dt))
        {
            Classify(); Scripted(); DeckTick();
            if (_firstLaunchT != 0 && !_saidEscorts && SimT > _firstLaunchT + 45)
            {
                _saidEscorts = true;
                SayComms("Diplomatic", "Red commander", "red", "Your escorts are spending their last missiles. Withdraw south and we will hold the second wave.");
            }
            if (Posture == "Proactive" && !_alertDone && SimT >= 20) AlertLaunch();
        }
        if (Math.Floor(SimT / 6) != Math.Floor((SimT - dt) / 6)) FlushReports();
        if (Math.Floor(SimT / 10) != Math.Floor((SimT - dt) / 10)) Objs = Objs.Where(o => o.Alive || !o.K.Missile).ToList();
        AfterStep?.Invoke();
    }

    /// <summary>Formats scenario time as Kerbin UT "hh:mm:ss" (6-hour days), like the mockup's fmtT.</summary>
    public static string FormatClock(double t)
    {
        double s = Ut0 + t;
        int h = (int)Math.Floor(s % 21600 / 3600), m = (int)Math.Floor(s % 3600 / 60), ss = (int)Math.Floor(s % 60);
        return $"{h:00}:{m:00}:{ss:00}";
    }

    /// <summary>"Y1 D014 03:12:00" for scenario time t.</summary>
    public static string FormatUt(double t)
    {
        double s = Ut0 + t;
        int d = (int)Math.Floor(s / 21600) + 1;
        return $"Y1 D{d:000} {FormatClock(t)}";
    }
}
