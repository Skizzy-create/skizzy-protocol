using Skizzy.C2;

namespace Skizzy.Map.Data.Demo;

/// <summary>
/// The BDArmory-level state of one demo unit that the C2 engine drives through <see cref="IBdaControl"/>: the
/// Overdrive hooks F1 (override target), F2 (secondary targets), F3 (fire authority), F4 (target filter), F6
/// (waypoints) and F6b (patrol engage), plus stock fly-to, guard mode and the missile settings. Semantics follow
/// the reference executor <c>Skizzy.C2.Fake.FakeBda</c>, in the demo battle's km plane.
/// </summary>
public sealed class SimBda
{
    public FireAuthorityMode Fire = FireAuthorityMode.Free;
    public TargetFilter? Filter;
    public int Override;
    public bool Sticky;
    public double OverrideUntil;
    public readonly List<int> Secondary = new();
    public bool SecondaryOnly;
    /// <summary>Route points: x, y (km) and altitude (m, 0 = keep).</summary>
    public readonly List<(double X, double Y, double Alt)> Wp = new();
    public int WpIndex, Lap, Laps = 1;
    public double WpRadiusKm = 1;
    public bool Yield;
    public double LeashKm;
    public bool HasAnchor;
    public double AnchorX, AnchorY;
    public bool PatrolEngaged;
    public int PatrolTarget;
    public double EngAnchorX, EngAnchorY;
    public readonly Dictionary<int, double> PatrolCooldown = new();
    public bool FlyTo;
    public double FlyX, FlyY;
    public bool Guard = true;
    public bool EngageMissiles = true;
    public bool? EngageMissilesBaseline;
    public bool RangeMax;
    /// <summary>The AI's current target (0 = none).</summary>
    public int Current;
    public double NextPick;

    public bool OnRoute => Wp.Count > 0;

    /// <summary>The engine moves or targets the unit: it flies the executor instead of its mission.</summary>
    public bool Commanded => Wp.Count > 0 || FlyTo || Override != 0 || Secondary.Count > 0;
}

public sealed partial class BattleSim
{
    /// <summary>Patrol engage: after breaking the leash a unit ignores that target this long (FakeBda's cooldown).</summary>
    private const double PatrolCooldownS = 30;

    /// <summary>Hook events from the executor go here (the C2 engine). Null until the demo's C2 layer attaches.</summary>
    public IHookEvents? Hooks { get; set; }

    /// <summary>Called after every fixed step (the C2 engine ticks every second step: 5 Hz of game time).</summary>
    public Action? AfterStep { get; set; }

    /// <summary>Objects that died since the C2 layer last drained this list (only filled while <see cref="Hooks"/> is set).</summary>
    public List<int> Died { get; } = new();

    /// <summary>Aircraft that landed on a deck since the C2 layer last drained this list.</summary>
    public List<SimObj> Landings { get; } = new();

    /// <summary>Game time in seconds, as the C2 engine sees it (Kerbin UT).</summary>
    public double Ut => Ut0 + SimT;

    public static bool IsAir(SimObj o) => o.K.Domain == "air";

    /// <summary>The executor state for a unit, created on first use.</summary>
    public static SimBda BdaOf(SimObj o) => o.Bda ??= new SimBda();

    // ------------------------------------------------------------ F1 / F2 / F4 checks (FakeBda semantics)

    private bool BlueDetects(SimObj t) => t.Side != "red" || !t.Stealth || t.Seen;

    private double AamRange(SimObj o) => o.Kind == "ucav" ? 20 : 42;

    private bool Engageable(SimObj o, SimObj t)
    {
        if (!t.Alive || !BlueDetects(t)) return false;
        double d = Dist(o, t);
        if (IsAir(o)) return (o.Aam > 0 && d <= Math.Max(AamRange(o), SensorR(o))) || ((t.Kind is "asm" or "uav") && d <= 2);
        return d <= (o.Bda?.RangeMax == true ? 90 : 60);
    }

    /// <summary>F3 + F4: may this unit fire at <paramref name="t"/>?</summary>
    private static bool Permitted(SimBda b, SimObj t)
    {
        if (b.Fire == FireAuthorityMode.Hold) return false;
        if (t.K.Missile || t.Kind == "uav") return true;
        if (b.Fire == FireAuthorityMode.Tight) return t.Id == b.Override || (b.Filter is not null && b.Filter.Allows((uint)t.Id));
        return b.Filter is null || b.Filter.Allows((uint)t.Id) || t.Id == b.Override;
    }

    private void LoseOverride(SimObj o, SimBda b, OverrideLostReason why, bool targetGone)
    {
        int old = b.Override;
        if (old == 0) return;
        b.Override = 0;
        b.Sticky = false;
        b.OverrideUntil = 0;
        if (b.Current == old) b.Current = 0;
        Hooks?.OnOverrideLost((uint)o.Id, targetGone ? 0 : (uint)old, why);
    }

    private void ScanOverride(SimObj o, SimBda b)
    {
        if (b.Override == 0) return;
        var t = Get(b.Override);
        if (t is null || !t.Alive) { LoseOverride(o, b, OverrideLostReason.TargetDestroyed, true); return; }
        if (b.OverrideUntil > 0 && Ut >= b.OverrideUntil) { LoseOverride(o, b, OverrideLostReason.Expired, false); return; }
        if (b.Filter is not null && !t.K.Missile && !b.Filter.Allows((uint)t.Id)) { LoseOverride(o, b, OverrideLostReason.Filtered, false); return; }
        if (!b.Sticky && !Engageable(o, t)) LoseOverride(o, b, OverrideLostReason.NotEngageable, false);
    }

    /// <summary>F1 set: the engine's SetOverrideTarget. Unknown or undetected targets are reported as not engageable.</summary>
    public void SetOverride(SimObj o, int target, double durationS, bool sticky)
    {
        var b = BdaOf(o);
        if (b.Override == target)
        {
            b.Sticky = sticky;
            b.OverrideUntil = durationS > 0 ? Ut + durationS : 0;
            return;
        }
        if (b.Override != 0) LoseOverride(o, b, OverrideLostReason.Replaced, false);
        var t = Get(target);
        if (t is null || !t.Alive || !BlueDetects(t))
        {
            Hooks?.OnOverrideLost((uint)o.Id, (uint)target, OverrideLostReason.NotEngageable);
            return;
        }
        b.Override = target;
        b.Sticky = sticky;
        b.OverrideUntil = durationS > 0 ? Ut + durationS : 0;
        ScanOverride(o, b);
        if (b.Override == target) b.Current = target;
    }

    public void ClearOverride(SimObj o)
    {
        if (o.Bda is { } b) LoseOverride(o, b, OverrideLostReason.Cleared, false);
    }

    /// <summary>Guard-mode target choice: override, then secondary targets, then the nearest permitted hostile in guard range.</summary>
    private void SelectTarget(SimObj o, SimBda b)
    {
        if (!b.Guard) { b.Current = 0; return; }
        if (b.Override != 0) { b.Current = b.Override; return; }
        foreach (var id in b.Secondary)
            if (Get(id) is { } s && Engageable(o, s)) { b.Current = s.Id; return; }
        if (b.SecondaryOnly) { b.Current = 0; return; }
        var cur = Get(b.Current);
        if (cur is not null && cur.Alive && SimT < b.NextPick) return;
        b.NextPick = SimT + .8 + (o.Id % 5) * .1;
        double range = IsAir(o) ? Math.Max(25, SensorR(o)) : 0;
        if (range <= 0) { b.Current = 0; return; }
        var best = Nearest(o, x =>
        {
            if (x.Side != "red" || !BlueDetects(x)) return false;
            if (b.PatrolCooldown.TryGetValue(x.Id, out var until) && SimT < until) return false;
            if (x.K.Missile) return b.EngageMissiles && x.Kind == "asm";
            if (x.Kind != "uav" && x.IdState != "hostile") return false;
            return b.Filter is not null ? b.Filter.Allows((uint)x.Id) : b.Fire != FireAuthorityMode.Tight;
        }, range);
        b.Current = best?.Id ?? 0;
    }

    /// <summary>F6b: leave the route for a permitted target, come back after it or when the leash breaks.</summary>
    private void Patrol(SimObj o, SimBda b)
    {
        var t = Get(b.Current);
        bool canYield = b.OnRoute && b.Yield && t is not null && t.Alive && b.Fire != FireAuthorityMode.Hold && Permitted(b, t);
        if (canYield)
        {
            if (!b.PatrolEngaged)
            {
                b.PatrolEngaged = true;
                b.PatrolTarget = t!.Id;
                var wp = b.Wp[Math.Min(b.WpIndex, b.Wp.Count - 1)];
                (b.EngAnchorX, b.EngAnchorY) = b.HasAnchor ? (b.AnchorX, b.AnchorY) : (wp.X, wp.Y);
                Hooks?.OnPatrolEngage((uint)o.Id, (uint)t.Id);
            }
            if (b.LeashKm > 0 && Math.Sqrt((o.X - b.EngAnchorX) * (o.X - b.EngAnchorX) + (o.Y - b.EngAnchorY) * (o.Y - b.EngAnchorY)) > b.LeashKm)
            {
                b.PatrolCooldown[t!.Id] = SimT + PatrolCooldownS;
                b.PatrolEngaged = false;
                b.Current = 0;
                Hooks?.OnPatrolResume((uint)o.Id, true);
                if (b.Override == t.Id) LoseOverride(o, b, OverrideLostReason.Cleared, false);
            }
        }
        else if (b.PatrolEngaged)
        {
            b.PatrolEngaged = false;
            Hooks?.OnPatrolResume((uint)o.Id, false);
        }
    }

    private void WaypointReached(SimObj o, SimBda b)
    {
        int idx = b.WpIndex, lap = b.Lap;
        b.WpIndex++;
        if (b.WpIndex >= b.Wp.Count)
        {
            b.Lap++;
            b.WpIndex = 0;
            if (b.Lap >= b.Laps)
            {
                b.Wp.Clear();
                b.PatrolEngaged = false;
                Hooks?.OnWaypointReached((uint)o.Id, idx, lap);
                Hooks?.OnRouteCompleted((uint)o.Id);
                return;
            }
        }
        Hooks?.OnWaypointReached((uint)o.Id, idx, lap);
    }

    private static void ClimbTo(SimObj o, double alt, double dt)
    {
        if (alt <= 0) return;
        double dz = alt - o.Alt, m = 60 * dt;
        o.Alt += Math.Abs(dz) <= m ? dz : Math.Sign(dz) * m;
    }

    private static double DistTo(SimObj o, double x, double y) => Math.Sqrt((o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y));

    // ------------------------------------------------------------ the executor

    /// <summary>
    /// One step of a unit under C2 control (or landing). Returns false when the unit is no longer commanded and its
    /// mission behaviour should run this step.
    /// </summary>
    private bool ThinkCommanded(SimObj o, double dt)
    {
        if (o.ApproachBase is int bid) { FlyApproach(o, bid, dt); return true; }
        var b = o.Bda;
        bool air = IsAir(o);
        if (b is null || !b.Commanded)
        {
            // released: ships take back their mission speed; aircraft fall back to their mission at once
            if (!air && o.BaseSpd is double sp) o.Spd = sp;
            o.BaseSpd = null;
            if (b is not null) b.Current = 0;
            return false;
        }
        if (!air)
        {
            o.BaseSpd ??= o.Spd;
            ThinkShip(o, b, dt);
            return true;
        }
        ScanOverride(o, b);
        SelectTarget(o, b);
        Patrol(o, b);
        o.Fuel = Math.Max(0, (o.Fuel ?? 0) - dt * .00008);
        o.Spd = o.K.Spd;
        var t = Get(b.Current);
        bool chase = t is not null && t.Alive && b.Fire != FireAuthorityMode.Hold;
        if (b.OnRoute && !b.PatrolEngaged)
        {
            var wp = b.Wp[b.WpIndex];
            double d = DistTo(o, wp.X, wp.Y);
            if (d <= Math.Max(b.WpRadiusKm, o.Spd * dt))
            {
                WaypointReached(o, b);
                if (!b.OnRoute) { FireAt(o, b, dt); return true; }
                wp = b.Wp[b.WpIndex];
            }
            SteerTo(o, Math.Atan2(wp.Y - o.Y, wp.X - o.X), .5, dt);
            ClimbTo(o, wp.Alt, dt);
        }
        else if (chase && (b.OnRoute || !b.FlyTo))
        {
            SteerTo(o, Math.Atan2(t!.Y - o.Y, t.X - o.X), .55, dt);
        }
        else if (b.FlyTo)
        {
            double d = DistTo(o, b.FlyX, b.FlyY);
            if (d < 3)
            {
                double a = Math.Atan2(o.Y - b.FlyY, o.X - b.FlyX);
                SteerTo(o, a + Math.PI / 2 + (d - 3) * .05, .35, dt);
            }
            else SteerTo(o, Math.Atan2(b.FlyY - o.Y, b.FlyX - o.X), .5, dt);
        }
        FireAt(o, b, dt);
        return true;
    }

    /// <summary>Ships under C2 control: fly-to and waypoints at the mockup's order speed; their SAMs fire in <see cref="ShipsFire"/>.</summary>
    private void ThinkShip(SimObj o, SimBda b, double dt)
    {
        double fast = o.Kind == "ssn" ? .009 : .012;
        double tx, ty;
        if (b.OnRoute)
        {
            var wp = b.Wp[b.WpIndex];
            if (DistTo(o, wp.X, wp.Y) <= Math.Max(b.WpRadiusKm, .8))
            {
                WaypointReached(o, b);
                if (!b.OnRoute) { o.Spd = 0; return; }
                wp = b.Wp[b.WpIndex];
            }
            (tx, ty) = (wp.X, wp.Y);
        }
        else if (b.FlyTo) (tx, ty) = (b.FlyX, b.FlyY);
        else { o.Spd = o.BaseSpd ?? o.K.Spd; return; }
        double d = DistTo(o, tx, ty);
        if (d < .6) { o.Spd = 0; return; }
        o.Spd = d < 2 ? Math.Max(.002, fast * d / 2) : fast;
        SteerTo(o, Math.Atan2(ty - o.Y, tx - o.X), .03, dt);
    }

    /// <summary>Guard-mode fire under C2 control: AAM in range, the gun against missiles and drones up close.</summary>
    private void FireAt(SimObj o, SimBda b, double dt)
    {
        if (!b.Guard || b.Current == 0) return;
        var t = Get(b.Current);
        if (t is null || !t.Alive || !Permitted(b, t)) return;
        if ((o.Kind == "ftr" || o.Kind == "ucav") && o.Aam > 0 && Dist(o, t) < AamRange(o) && SimT > o.Cool && FreeT(t))
        {
            var m = Launch("aam", o, t, "AAM");
            o.Aam--; Stats.ExpBlue["AAM"]++; o.Cool = SimT + 3.5; Report(o, "AAM", 1);
            Hooks?.OnMissileFired((uint)o.Id, (uint)m.Id, (uint)t.Id);
        }
        if ((t.Kind == "asm" || t.Kind == "uav") && t.Alive && Dist(o, t) < 1.6 && Rnd() < .25 * dt)
        {
            Kill(t); Stats.Intercepts++; Event("ciws", t.X, t.Y); Report(o, "Gun", 1);
            LogMsg("intercept", $"{o.Name} shot down {(t.Kind == "asm" ? "missile" : "drone")} {t.Name} with its gun.", t.X, t.Y);
        }
    }

    // ------------------------------------------------------------ recover and launch adapters (24-ksp-parity row 31)

    /// <summary>The marshal point astern of a carrier (the mockup's marshalPt).</summary>
    public static (double X, double Y) MarshalPoint(SimObj b) => (b.X - Math.Cos(b.Hdg) * MarshalD, b.Y - Math.Sin(b.Hdg) * MarshalD);

    /// <summary>Recover adapter: the aircraft leaves the marshal stack and lands on <paramref name="carrier"/>.</summary>
    public void StartApproach(SimObj o, SimObj carrier)
    {
        o.ApproachBase = carrier.Id;
    }

    private void FlyApproach(SimObj o, int baseId, double dt)
    {
        var b = Get(baseId);
        if (b is null || !b.Alive) { o.ApproachBase = null; return; }
        o.Spd = o.K.Spd;
        SteerTo(o, Math.Atan2(b.Y - o.Y, b.X - o.X), .8, dt);
        o.Alt = Math.Max(20, o.Alt - dt * 90);
        if (Dist(o, b) < .7) Land(o, b);
    }

    /// <summary>The aircraft goes on deck: it rearms and refuels there in 90 s (the mockup's land()).</summary>
    public void Land(SimObj o, SimObj b)
    {
        o.Alive = false;
        o.Deck = b.Id;
        o.LandedT = SimT;
        o.Ready = false;
        o.ApproachBase = null;
        o.Bda = null;
        o.Ctrl = "Mission";
        o.TgtId = null;
        Landings.Add(o);
        int n = Objs.Count(x => x.Deck == b.Id);
        LogAt("orders", $"{o.Name} landed on {b.Name}. {n} aircraft on deck.", b);
    }

    /// <summary>
    /// Launch adapter: the carrier's ready aircraft take off (the mockup's launchDeck). Returns them; empty when none is
    /// ready. With <paramref name="hunt"/> the caller gives them an attack-nearest order.
    /// </summary>
    public List<SimObj> LaunchDeck(SimObj b, bool hunt)
    {
        var r = Objs.Where(o => o.Deck == b.Id && o.Ready).ToList();
        if (r.Count == 0) return r;
        for (int i = 0; i < r.Count; i++)
        {
            var o = r[i];
            o.Alive = true;
            o.DiedT = double.NaN;
            o.Deck = null;
            o.X = b.X + Math.Cos(b.Hdg) * (.4 + i * .15);
            o.Y = b.Y + Math.Sin(b.Hdg) * (.4 + i * .15);
            o.Hdg = b.Hdg;
            o.Alt = 300 + i * 40;
            o.Trail.Clear();
            o.Ctrl = "Mission";
            if (o.Home is { } h) o.OrbitAt = h;
            else o.OrbitAt ??= new Orbit(b.X + 18, b.Y, 10);
        }
        LogAt("orders", $"{b.Name} launched {r.Count} aircraft ({(r.Count > 4 ? r.Count + " aircraft" : string.Join(", ", r.Select(o => o.Name)))}).{(hunt ? " They hunt the nearest hostiles and unengaged missiles." : " They return to their missions.")}", b);
        return r;
    }

    // ------------------------------------------------------------ sensors for the C2 engine (F5, the side picture)

    /// <summary>Own-sensor reach of a Blue unit, km (the ranges <see cref="Classify"/> uses).</summary>
    public static double OwnSensorKm(SimObj o) => o.Kind switch
    {
        "aew" => 175,
        "ftr" => 45,
        "ucav" => 25,
        "cvn" or "ddg" => 150,
        _ => 0,
    };

    /// <summary>Hostile objects inside <paramref name="o"/>'s own sensor range (grid query).</summary>
    public void OwnContacts(SimObj o, List<uint> into)
    {
        double r = OwnSensorKm(o);
        if (r <= 0 || !o.Alive) return;
        long cx = (long)Math.Floor(o.X / GC), cy = (long)Math.Floor(o.Y / GC);
        int R = (int)Math.Ceiling(r / GC) + 1;
        for (long x = cx - R; x <= cx + R; x++)
            for (long y = cy - R; y <= cy + R; y++)
            {
                if (!_grid.TryGetValue(GKey(x, y), out var cell)) continue;
                foreach (var t in cell)
                    if (t.Alive && t.Side != o.Side && Dist(o, t) <= r && (!t.Stealth || Dist(o, t) <= 25)) into.Add((uint)t.Id);
            }
    }
}
