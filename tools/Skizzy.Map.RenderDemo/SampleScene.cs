using System.Numerics;
using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.RenderDemo;

/// <summary>
/// Deterministic sample scenes ported from the mockup's <c>setup()</c> (design/mockups/command-screen-v1.html),
/// plus some missiles in flight, orders and a drawn route.
/// </summary>
public static class SampleScene
{
    /// <summary>The mockup's mulberry32 <c>rnd()</c>, using 32-bit integer arithmetic exactly like the JS.</summary>
    public sealed class Rng
    {
        private int _seed;

        public Rng(int seed) => _seed = seed;

        public double Next()
        {
            unchecked
            {
                _seed += 0x6D2B79F5;
                int t = (_seed ^ (int)((uint)_seed >> 15)) * (1 | _seed);
                t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
                return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
            }
        }
    }

    private const double KmPerS = 1000.0;

    /// <summary>Default speed in km/s from the mockup's K table.</summary>
    private static double KindSpd(TrackKind k) => k switch
    {
        TrackKind.Carrier or TrackKind.Destroyer => 0.008,
        TrackKind.Submarine => 0.006,
        TrackKind.EarlyWarning => 0.16,
        TrackKind.Fighter => 0.25,
        TrackKind.Strike => 0.24,
        TrackKind.Uav => 0.05,
        TrackKind.Ucav => 0.09,
        TrackKind.AntiShipMissile => 0.29,
        TrackKind.SurfaceToAirMissile => 0.9,
        TrackKind.AirToAirMissile => 0.8,
        _ => 0,
    };

    /// <summary>The mockup's setup() scene plus a few missiles in flight, orders and a drawn route.</summary>
    public static PlotScene Build()
    {
        var b = new Builder(11);
        b.AddBlueShips();
        b.AddWatch1();
        b.AddFlight(4, TrackKind.Fighter, "Viper", 62, 8, 12, 6800);
        b.AddFlight(4, TrackKind.Fighter, "Cobra", 55, -40, 12, 6200);
        b.AddFlight(8, TrackKind.Fighter, "Hawk", 22, 6, 8, 5200);
        b.AddFlight(24, TrackKind.Ucav, "Wasp", 38, 26, 6, 1200);

        // Red groups 0-3: 4 strike then 6 fighters. Object-literal order: x, y, alt, spd, off.
        for (int g = 0; g < 4; g++)
        {
            double cy = -54 + g * 36, cx = 228 + g * 7;
            for (int i = 0; i < 10; i++)
            {
                var kind = i < 4 ? TrackKind.Strike : TrackKind.Fighter;
                double x = cx + (b.Rng.Next() - .5) * 12;
                double y = cy + (b.Rng.Next() - .5) * 12;
                double alt = kind == TrackKind.Strike ? 1800 + b.Rng.Next() * 2600 : 4500 + b.Rng.Next() * 5000;
                double spd = KindSpd(kind) + (b.Rng.Next() - .5) * .02;
                _ = b.Rng.Next(); // 'off': unused by the plot, but the mockup consumes it
                b.AddRed(kind, $"R{g + 1}-{i + 1:00}", x, y, alt, spd, Affiliation.Hostile, ControlState.Bda, CompassTo(x, y, 0, 0));
            }
        }

        // Red groups 4-5: x, y, alt.
        for (int g = 4; g < 6; g++)
        {
            double cy = g == 4 ? 95 : -100, cx = 215;
            for (int i = 0; i < 10; i++)
            {
                double x = cx + (b.Rng.Next() - .5) * 12;
                double y = cy + (b.Rng.Next() - .5) * 12;
                double alt = 6000 + b.Rng.Next() * 4000;
                b.AddRed(TrackKind.Fighter, $"R{g + 1}-{i + 1:00}", x, y, alt, 0.27, Affiliation.Unknown, ControlState.Bda, CompassTo(x, y, 0, 0));
            }
        }

        // UAVs: x, y, alt.
        for (int i = 0; i < 20; i++)
        {
            double x = 168 + (i % 5) * 1.4 + b.Rng.Next();
            double y = 112 + (i / 5) * 1.4 + b.Rng.Next();
            double alt = 200 + b.Rng.Next() * 300;
            b.AddRed(TrackKind.Uav, $"D{i + 1:00}", x, y, alt, 0.05, Affiliation.Hostile, ControlState.Script, CompassTo(x, y, -10, 0));
        }

        var scene = b.Scene;

        // Damage for visual variety.
        b.ByName("Bowline").Health = 0.55f;
        b.ByName("Cleat").Health = 0.30f;

        // Advance R1 into its launch position, keeping headings on the origin.
        var r1 = scene.Tracks.Where(t => t.Name.StartsWith("R1-", StringComparison.Ordinal)).ToList();
        float cenX = r1.Average(t => t.X), cenY = r1.Average(t => t.Y);
        float dX = 112f - cenX, dY = 18f - cenY;
        foreach (var t in r1)
        {
            t.X += dX;
            t.Y += dY;
            t.HeadingDeg = CompassTo(t.X, t.Y, 0, 0);
        }

        // Missiles in flight.
        var kestrel = b.ByName("Kestrel");
        var halyard = b.ByName("Halyard");
        var bowline = b.ByName("Bowline");
        var cleat = b.ByName("Cleat");
        Vector2 launch = new(112f, 18f);
        Vector2 cvn = new(kestrel.X, kestrel.Y);
        Vector2 dir = Vector2.Normalize(cvn - launch);
        Vector2 perp = new(-dir.Y, dir.X);
        float[] fr = [0.25f, 0.35f, 0.45f, 0.55f, 0.65f, 0.75f];
        var asms = new PlotTrack[6];
        for (int i = 0; i < 6; i++)
        {
            var p = launch + (cvn - launch) * fr[i] + perp * ((i - 2.5f) * 1.2f);
            var target = i % 2 == 0 ? kestrel : halyard;
            asms[i] = b.AddMissile(TrackKind.AntiShipMissile, $"V{i + 1:000}", Side.Red, Affiliation.Hostile, p.X, p.Y, 15, 290, target);
        }

        b.AddInterceptor(TrackKind.SurfaceToAirMissile, "HAL-SM2-01", halyard, asms[0], 0.30f, 800, 900);
        b.AddInterceptor(TrackKind.SurfaceToAirMissile, "HAL-SM2-02", halyard, asms[1], 0.45f, 800, 900);
        b.AddInterceptor(TrackKind.SurfaceToAirMissile, "BOW-SM2-01", bowline, asms[2], 0.35f, 800, 900);
        b.AddInterceptor(TrackKind.SurfaceToAirMissile, "BOW-SM2-02", bowline, asms[3], 0.55f, 800, 900);
        b.AddInterceptor(TrackKind.AirToAirMissile, "VIP1-AAM-01", b.ByName("Viper 1"), b.ByName("R1-05"), 0.5f, 6500, 800);
        b.AddInterceptor(TrackKind.AirToAirMissile, "VIP2-AAM-01", b.ByName("Viper 2"), b.ByName("R1-06"), 0.5f, 6500, 800);

        // Orders: Vipers attack R1, Hawks follow a drawn route, Cleat moves.
        var route = new PlotRoute { Id = 1 };
        route.Points.Add(new Vector2(22, 6));
        route.Points.Add(new Vector2(40, 14));
        route.Points.Add(new Vector2(62, 10));
        route.Points.Add(new Vector2(80, -2));
        scene.Routes.Add(route);

        for (int j = 0; j < 4; j++)
        {
            var viper = b.ByName($"Viper {j + 1}");
            scene.Orders.Add(new PlotOrder { TrackId = viper.Id, Type = OrderType.Attack, TargetTrackId = r1[j % r1.Count].Id });
            viper.Control = ControlState.Order;
        }

        const int n = 8;
        int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
        const float sp = 2.2f;
        for (int i = 0; i < n; i++)
        {
            var hawk = b.ByName($"Hawk {i + 1}");
            int r = i / cols, c = i % cols;
            scene.Orders.Add(new PlotOrder
            {
                TrackId = hawk.Id,
                Type = OrderType.Route,
                RouteId = 1,
                RouteIndex = 1,
                RouteOffset = new Vector2((c - (cols - 1) / 2f) * sp, -(r - (rows - 1) / 2f) * sp),
            });
            hawk.Control = ControlState.Order;
        }

        scene.Orders.Add(new PlotOrder { TrackId = cleat.Id, Type = OrderType.Move, Destination = new Vector2(-6, -14) });
        cleat.Control = ControlState.Order;

        // Zones.
        var excl = new PlotZone
        {
            Kind = ZoneKind.Exclusion,
            Affiliation = Affiliation.Hostile,
            Label = "RED DECLARED EXCLUSION ZONE",
            LabelAnchor = new Vector2(30, 0),
        };
        excl.Fill.AddRange([new Vector2(30, -160), new Vector2(400, -160), new Vector2(400, 160), new Vector2(30, 160)]);
        excl.Boundary.AddRange([new Vector2(30, -160), new Vector2(30, 160)]);
        scene.Zones.Add(excl);
        scene.Zones.Add(new PlotZone { Kind = ZoneKind.Patrol, Affiliation = Affiliation.Friend, Label = "CAP NORTH", Center = new Vector2(62, 0), RadiiKm = [18f] });
        scene.Zones.Add(new PlotZone { Kind = ZoneKind.Bullseye, Affiliation = Affiliation.Neutral, Label = "BULLSEYE ANVIL", Center = new Vector2(100, 40), RadiiKm = [10f, 25f] });

        // Time and events.
        scene.Time = 480;
        var r104 = b.ByName("R1-04");
        scene.Events.Add(new PlotEvent(PlotEventKind.Kill, r104.X, r104.Y, 476));
        scene.Events.Add(new PlotEvent(PlotEventKind.Impact, cleat.X, cleat.Y, 478.5));
        scene.Events.Add(new PlotEvent(PlotEventKind.Ciws, -10.2f, 2.6f, 479.2));
        scene.Events.Add(new PlotEvent(PlotEventKind.Intercept, asms[5].X + 0.4f, asms[5].Y, 479.4));
        scene.Events.Add(new PlotEvent(PlotEventKind.Miss, 60, 16, 479.6));
        r104.Visible = false;

        b.AddTrails();
        scene.View = Viewport.Fit(-40, -60, 250, 120, 1920, 1080);
        return scene;
    }

    /// <summary>
    /// The sample scene with the newer overlays (design/23 §6 learnings 13–15): two VIP carriers (Kestrel selected,
    /// Osprey not), Cobras on a shared racetrack, Vipers on RTB to Kestrel (two inbound, two in the marshal stack),
    /// Hawks 1–2 protecting Kestrel, Wasps hunting (one with no target yet), and two stealth fighters: S-01 seen by one
    /// own sensor only, S-02 not detected at all (so not drawn).
    /// </summary>
    public static PlotScene BuildVipsAndOrders()
    {
        var scene = Build();
        var tracks = scene.Tracks;
        PlotTrack T(string name) => tracks.First(t => t.Name == name);
        scene.Orders.Clear();
        scene.Routes.Clear();
        foreach (var t in tracks) if (t.Control == ControlState.Order) t.Control = ControlState.Mission;
        var kestrel = T("Kestrel");
        int id = tracks.Max(t => t.Id) + 1;

        // a second carrier: a VIP that is not selected (the mockup's Osprey)
        var osprey = new PlotTrack(id++, TrackKind.Carrier)
        {
            Side = Side.Blue, Affiliation = Affiliation.Friend, X = -6, Y = -34, HeadingDeg = kestrel.HeadingDeg, SpeedMps = 8,
            Name = "Osprey", Control = ControlState.Mission, Health = 1f, Rings = kestrel.Rings, Vip = true,
        };
        Reckon(osprey, 20);
        tracks.Add(osprey);

        // Cobras: a racetrack south-east of the group, 32 km legs, 6 km wide, right-hand turns, stacked 300 m apart
        var rt = new PlotRacetrack { Id = 1, A = new Vector2(40, -46), B = new Vector2(72, -46) };
        scene.Racetracks.Add(rt);
        // (positions keep the labels off the direction arrow, which sits mid-way along the inbound leg)
        (float X, float Y, float Hdg)[] slots = [(66, -43, 90), (75, -46, 180), (58, -49, 270), (44, -49, 270)];
        for (int i = 0; i < 4; i++)
        {
            var c = T($"Cobra {i + 1}");
            (c.X, c.Y, c.HeadingDeg, c.AltitudeM) = (slots[i].X, slots[i].Y, slots[i].Hdg, 4500 + i * 300);
            Reckon(c, 8);
            c.Control = ControlState.Order;
            scene.Orders.Add(new PlotOrder { TrackId = c.Id, Type = OrderType.Racetrack, PatternId = rt.Id });
        }

        // Cleat on station north of the carrier, clear of the marshal stack astern
        var cleat = T("Cleat");
        (cleat.X, cleat.Y) = (-6f, 13f);
        Reckon(cleat, 20);
        for (int i = 0; i < scene.Events.Count; i++)
            if (scene.Events[i].Kind == PlotEventKind.Impact) scene.Events[i] = scene.Events[i] with { X = cleat.X, Y = cleat.Y };

        // Vipers: RTB to Kestrel. 1-2 inbound (a line to the marshal point), 3-4 already orbiting in the stack.
        float kh = kestrel.HeadingDeg * MathF.PI / 180f;
        Vector2 mp = new(kestrel.X - MathF.Sin(kh) * 8f, kestrel.Y - MathF.Cos(kh) * 8f);
        for (int i = 0; i < 4; i++)
        {
            var v = T($"Viper {i + 1}");
            bool inbound = i < 2;
            if (inbound)
            {
                v.HeadingDeg = CompassTo(v.X, v.Y, mp.X, mp.Y);
                Reckon(v, 20);
            }
            else
            {
                double a = i == 2 ? 0.6 : 0.6 + Math.PI;
                v.X = mp.X + (float)(Math.Cos(a) * 3.5);
                v.Y = mp.Y + (float)(Math.Sin(a) * 3.5);
                v.AltitudeM = 1500 + (i - 2) * 300;
                v.HeadingDeg = CompassFromMath(a + Math.PI / 2);
                v.Trail.Clear();
                double step = v.SpeedMps / KmPerS / 3.5;
                for (int k = 20; k >= 1; k--)
                    v.Trail.Add(mp.X + (float)(Math.Cos(a - step * k) * 3.5), mp.Y + (float)(Math.Sin(a - step * k) * 3.5));
            }
            v.Control = ControlState.Order;
            scene.Orders.Add(new PlotOrder { TrackId = v.Id, Type = OrderType.Rtb, TargetTrackId = kestrel.Id, RtbPhase = inbound ? RtbPhase.Inbound : RtbPhase.Marshal });
        }

        // Hawks 1-2 protect Kestrel
        for (int i = 0; i < 2; i++)
        {
            var h = T($"Hawk {i + 1}");
            h.Control = ControlState.Order;
            scene.Orders.Add(new PlotOrder { TrackId = h.Id, Type = OrderType.Defend, TargetTrackId = kestrel.Id });
        }

        // Wasps 1-3 hunt the nearest R1 aircraft; Wasp 4 hunts but has no target yet (no line)
        for (int i = 0; i < 4; i++)
        {
            var w = T($"Wasp {i + 1}");
            w.Control = ControlState.Order;
            scene.Orders.Add(new PlotOrder { TrackId = w.Id, Type = OrderType.Hunt, TargetTrackId = i < 3 ? T($"R1-0{i + 1}").Id : 0 });
        }

        // stealth fighters: S-01 held by one own sensor only, S-02 undetected
        for (int i = 0; i < 2; i++)
        {
            var s = new PlotTrack(id++, TrackKind.Fighter)
            {
                Side = Side.Red, Affiliation = Affiliation.Unknown, X = 92 + i * 1.5f, Y = -16 + i, AltitudeM = 9500, SpeedMps = 240,
                Name = $"S-0{i + 1}", Control = ControlState.Bda, Detection = i == 0 ? TrackDetection.OwnSensor : TrackDetection.None,
            };
            s.HeadingDeg = CompassTo(s.X, s.Y, 0, 0);
            Reckon(s, 20);
            tracks.Add(s);
        }

        scene.View = Viewport.Fit(-48, -60, 118, 46, 1920, 1080);
        return scene;
    }

    /// <summary>Replace a track's trail with one dead-reckoned back along its heading.</summary>
    private static void Reckon(PlotTrack t, int n)
    {
        t.Trail.Clear();
        double h = t.HeadingDeg * Math.PI / 180, v = t.SpeedMps / KmPerS;
        for (int k = n; k >= 1; k--) t.Trail.Add((float)(t.X - Math.Sin(h) * v * k), (float)(t.Y - Math.Cos(h) * v * k));
    }

    /// <summary>Dense frame: "128 missiles inbound" on the carrier group.</summary>
    public static PlotScene BuildMissileRaid()
    {
        var b = new Builder(11);
        b.AddBlueShips();
        for (int i = 0; i < 8; i++) b.AddFlightMember(TrackKind.Fighter, "Hawk", i, 22, 6, 8, 5200);
        for (int i = 0; i < 8; i++) b.AddFlightMember(TrackKind.Fighter, "Viper", i, 40, -10, 8, 6000);

        // 24 hostile drones in a 6x4 block, heading for Cleat.
        for (int i = 0; i < 24; i++)
        {
            int c = i % 6, r = i / 6;
            double x = 48 + (c - 2.5) * 1.4, y = 30 + (r - 1.5) * 1.4;
            b.AddRed(TrackKind.Uav, $"D{i + 1:00}", x, y, 300, 0.05, Affiliation.Hostile, ControlState.Script, CompassTo(x, y, -11, 2));
        }

        var kestrel = b.ByName("Kestrel");
        var ships = new[] { kestrel, b.ByName("Halyard"), b.ByName("Bowline"), b.ByName("Cleat") };
        var rng = new Rng(23);
        var asms = new PlotTrack[128];
        for (int m = 0; m < 128; m++)
        {
            int w = m / 32, j = m % 32;
            double bearing = (40 + 100.0 * j / 31) * Math.PI / 180;
            double radius = 18 + w * 14 + (rng.Next() * 2 - 1) * 0.6;
            double x = kestrel.X + radius * Math.Sin(bearing);
            double y = kestrel.Y + radius * Math.Cos(bearing);
            asms[m] = b.AddMissile(TrackKind.AntiShipMissile, $"V{m + 1:000}", Side.Red, Affiliation.Hostile, x, y, 15, 290, ships[m % 4]);
        }

        // Shooters cycle Halyard, Bowline, Cleat, Kestrel (ESSM).
        PlotTrack[] shooters = [ships[1], ships[2], ships[3], ships[0]];
        string[] prefix = ["HAL-SM2", "BOW-SM2", "CLT-SM2", "KES-ESSM"];
        for (int k = 0; k < 24; k++)
        {
            int s = k % 4;
            b.AddInterceptor(TrackKind.SurfaceToAirMissile, $"{prefix[s]}-{k / 4 + 1:00}", shooters[s], asms[4 * k], 0.4f, 600, 900);
        }

        // Events: four intercepts and two CIWS bursts on the inner 18 km arc, an impact, a kill.
        var scene = b.Scene;
        scene.Time = 300;
        PlotEventKind[] kinds =
        [
            PlotEventKind.Intercept, PlotEventKind.Intercept, PlotEventKind.Intercept, PlotEventKind.Intercept,
            PlotEventKind.Ciws, PlotEventKind.Ciws, PlotEventKind.Impact, PlotEventKind.Kill,
        ];
        double[] arcBearings = [60, 80, 100, 120, 70, 110];
        var bowline = ships[2];
        for (int e = 0; e < 8; e++)
        {
            float x, y;
            if (e < 6)
            {
                double br = arcBearings[e] * Math.PI / 180;
                x = kestrel.X + (float)(18 * Math.Sin(br));
                y = kestrel.Y + (float)(18 * Math.Cos(br));
            }
            else if (e == 6) { x = bowline.X; y = bowline.Y; }
            else { x = 30; y = 5; }
            scene.Events.Add(new PlotEvent(kinds[e], x, y, 297.0 + e * 0.4));
        }

        b.AddTrails();
        scene.View = Viewport.Fit(-22, -50, 72, 50, 1920, 1080);
        return scene;
    }

    private static float CompassTo(double x0, double y0, double x1, double y1) =>
        CompassFromMath(Math.Atan2(y1 - y0, x1 - x0));

    private static float CompassFromMath(double mathRad) =>
        (float)((((90 - mathRad * 180 / Math.PI) % 360) + 360) % 360);

    private sealed class Builder
    {
        private int _nextId = 1;
        private readonly Dictionary<int, (double Cx, double Cy, double R)> _orbits = new();

        public Builder(int seed) => Rng = new Rng(seed);

        public Rng Rng { get; }
        public PlotScene Scene { get; } = new();

        public PlotTrack ByName(string name) => Scene.Tracks.First(t => t.Name == name);

        private PlotTrack Add(TrackKind kind, string name, Side side, Affiliation aff, double x, double y,
            double altM, float headingDeg, double speedKmps, ControlState control)
        {
            var t = new PlotTrack(_nextId++, kind)
            {
                Side = side,
                Affiliation = aff,
                X = (float)x,
                Y = (float)y,
                AltitudeM = (float)altM,
                HeadingDeg = headingDeg,
                SpeedMps = (float)(speedKmps * KmPerS),
                Name = name,
                Control = control,
            };
            Scene.Tracks.Add(t);
            return t;
        }

        public void AddBlueShips()
        {
            float hdg = CompassFromMath(0.12);
            var essm = new RangeRing(22, "ESSM 22 km");
            var radar = new RangeRing(150, "Radar 150 km", IsRadar: true);
            var sm2 = new RangeRing(38, "SM-2 38 km (sea-skimmers)");

            var cvn = Add(TrackKind.Carrier, "Kestrel", Side.Blue, Affiliation.Friend, 0, 0, 0, hdg, 0.008, ControlState.Mission);
            cvn.Rings = [essm, radar];
            cvn.Health = 1f;
            cvn.Selected = true;
            cvn.Vip = true; // as in the mockup's setup()
            foreach (var (name, x, y) in new[] { ("Halyard", 14.0, 9.0), ("Bowline", 14.0, -9.0), ("Cleat", -11.0, 2.0) })
            {
                var d = Add(TrackKind.Destroyer, name, Side.Blue, Affiliation.Friend, x, y, 0, hdg, 0.008,
                    name == "Halyard" ? ControlState.Manual : ControlState.Mission);
                d.Rings = [sm2, essm, radar];
                d.Health = 1f;
            }
            Add(TrackKind.Submarine, "Tide", Side.Blue, Affiliation.Friend, 34, -30, -120, hdg, 0.006, ControlState.Agent).Health = 1f;
        }

        public void AddWatch1()
        {
            var t = Add(TrackKind.EarlyWarning, "Watch 1", Side.Blue, Affiliation.Friend, -25, 28, 9000,
                CompassFromMath(0), 0.16, ControlState.Bda);
            _orbits[t.Id] = (-25, 15, 13);
        }

        public void AddFlight(int n, TrackKind kind, string call, double cx, double cy, double r, double alt)
        {
            for (int i = 0; i < n; i++) AddFlightMember(kind, call, i, cx, cy, r, alt);
        }

        public void AddFlightMember(TrackKind kind, string call, int i, double cx, double cy, double r, double alt)
        {
            var t = Add(kind, $"{call} {i + 1}", Side.Blue, Affiliation.Friend,
                cx + Math.Cos(i * 2.4) * r * .6, cy + Math.Sin(i * 2.4) * r * .6, alt + i * 60,
                CompassFromMath(Math.PI / 2), KindSpd(kind), ControlState.Mission);
            _orbits[t.Id] = (cx, cy, r);
        }

        public PlotTrack AddRed(TrackKind kind, string name, double x, double y, double alt, double spdKmps,
            Affiliation aff, ControlState ctrl, float headingDeg) =>
            Add(kind, name, Side.Red, aff, x, y, alt, headingDeg, spdKmps, ctrl);

        public PlotTrack AddMissile(TrackKind kind, string name, Side side, Affiliation aff, double x, double y,
            double alt, double speedMps, PlotTrack target)
        {
            var t = Add(kind, name, side, aff, x, y, alt, CompassTo((float)x, (float)y, target.X, target.Y),
                speedMps / KmPerS, ControlState.Bda);
            t.TargetId = target.Id;
            return t;
        }

        /// <summary>A friendly missile placed <paramref name="frac"/> of the way from the shooter to the target.</summary>
        public PlotTrack AddInterceptor(TrackKind kind, string name, PlotTrack shooter, PlotTrack target,
            float frac, double alt, double speedMps)
        {
            float x = shooter.X + (target.X - shooter.X) * frac;
            float y = shooter.Y + (target.Y - shooter.Y) * frac;
            return AddMissile(kind, name, Side.Blue, Affiliation.Friend, x, y, alt, speedMps, target);
        }

        /// <summary>Synthetic trails: dead-reckoned backwards, or along the orbit circle for orbiting flights.</summary>
        public void AddTrails()
        {
            foreach (var t in Scene.Tracks)
            {
                int n = t.IsMissile ? 12 : 20;
                double v = t.SpeedMps / KmPerS;
                if (_orbits.TryGetValue(t.Id, out var o))
                {
                    double dx = t.X - o.Cx, dy = t.Y - o.Cy;
                    double d = Math.Sqrt(dx * dx + dy * dy), a = Math.Atan2(dy, dx), step = v / o.R;
                    // Review fix: an orbiting aircraft flies along its circle (counter-clockwise, as the mockup's
                    // orbit() steers a + PI/2), so its speed leader must agree with its trail.
                    t.HeadingDeg = CompassFromMath(a + Math.PI / 2);
                    for (int k = n; k >= 1; k--)
                    {
                        double ang = a - step * k;
                        t.Trail.Add((float)(o.Cx + d * Math.Cos(ang)), (float)(o.Cy + d * Math.Sin(ang)));
                    }
                }
                else
                {
                    double h = t.HeadingDeg * Math.PI / 180;
                    double vx = Math.Sin(h) * v, vy = Math.Cos(h) * v;
                    for (int k = n; k >= 1; k--) t.Trail.Add((float)(t.X - vx * k), (float)(t.Y - vy * k));
                }
            }
        }
    }
}
