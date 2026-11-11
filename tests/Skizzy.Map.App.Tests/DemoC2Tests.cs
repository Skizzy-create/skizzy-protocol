using System.Diagnostics;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Map.App.Tests;

/// <summary>
/// The demo battle's C2 layer at the data level: the real C2 engine drives the sim's executor through the c2.*
/// contract (move, attack, RTB and land, racetrack, deck launch, refusals), as the map's command service uses it.
/// </summary>
public class DemoC2Tests
{
    private sealed class Rig
    {
        public readonly DemoBattleSource Src = new();
        public readonly MapData Data = new();
        public Rig() { Src.Attach(Data); Src.Sim.PauseOnEvents = false; }
        public BattleSim Sim => Src.Sim;
        public SimObj U(string name) => Sim.Objs.First(o => o.Name == name);
        public static string Ref(SimObj o) => "v:" + o.Id;

        public JsonValue Call(string method, JsonValue args) =>
            JsonParser.Parse(Src.CallAsync(method, JsonWriter.Serialize(args), CancellationToken.None).GetAwaiter().GetResult());

        public SkizzyError Refused(string method, JsonValue args)
        {
            var t = Src.CallAsync(method, JsonWriter.Serialize(args), CancellationToken.None);
            var ex = Assert.Throws<AggregateException>(() => t.Wait());
            return Assert.IsType<SkizzyError>(ex.InnerException);
        }

        public void Run(double seconds)
        {
            for (double t = 0; t < seconds; t += 5) { Sim.Simulate(5); Src.Tick(0); }
        }

        public static JsonValue Pt(double x, double y)
        {
            var g = DemoC2.ToGeo(x, y);
            return JsonValue.Obj().Set("lat", g.Lat).Set("lon", g.Lon);
        }
    }

    private static double Dist(SimObj o, double x, double y) => Math.Sqrt((o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y));

    [Fact]
    public void MoveToFliesTheUnitToThePointAndHoldsThere()
    {
        var r = new Rig();
        var u = r.U("Hawk 1");
        double tx = u.X - 40, ty = u.Y + 30;
        var res = r.Call("c2.order.issue", JsonValue.Obj().Set("type", "move_to").Set("side", "Blue")
            .Set("units", JsonValue.ArrOf(Rig.Ref(u))).Set("point", Rig.Pt(tx, ty)));
        Assert.Equal(1, res.GetInt("accepted"));
        double d0 = Dist(u, tx, ty);
        r.Run(260); // 50 km at 250 m/s
        Assert.True(Dist(u, tx, ty) < 8, $"{u.Name} is {Dist(u, tx, ty):0.0} km from the point (was {d0:0.0})");
        Assert.StartsWith("Your order · move", r.Data.Get(u.Id)!.ControlText);
        Assert.Contains(r.Data.Orders.Orders, o => o.TrackId == u.Id);
    }

    [Fact]
    public void RtbRefusesTheCarrierAndLandsAircraftOnDeck()
    {
        var r = new Rig();
        var k = r.U("Kestrel");
        var err = r.Refused("c2.order.issue", JsonValue.Obj().Set("type", "rtb").Set("side", "Blue").Set("units", JsonValue.ArrOf(Rig.Ref(k))));
        Assert.Contains("carrier", err.Extra?.GetString("summary") ?? err.Message, StringComparison.OrdinalIgnoreCase);

        var u = r.U("Hawk 2");
        var res = r.Call("c2.order.issue", JsonValue.Obj().Set("type", "rtb").Set("side", "Blue").Set("units", JsonValue.ArrOf(Rig.Ref(u))));
        Assert.Contains("land one every 5 s", res.GetString("summary"));
        for (int i = 0; i < 60 && u.Deck is null; i++) r.Run(10);
        Assert.NotNull(u.Deck);
        Assert.Contains(r.Data.Log.Entries, e => e.Text.Contains($"{u.Name} landed on", StringComparison.Ordinal));
    }

    [Fact]
    public void RacetrackRefusesShipsAndFliesThePattern()
    {
        var r = new Rig();
        var a = r.U("Viper 1");
        var ship = r.U("Halyard");
        double ax = a.X + 10, ay = a.Y + 10;
        var res = r.Call("c2.order.issue", JsonValue.Obj().Set("type", "hold_pattern").Set("side", "Blue")
            .Set("units", JsonValue.ArrOf(Rig.Ref(a), Rig.Ref(ship))).Set("patternA", Rig.Pt(ax, ay)).Set("patternB", Rig.Pt(ax + 30, ay)).Set("widthKm", 6));
        Assert.Equal(1, res.GetInt("accepted"));
        Assert.Equal(1, res.GetInt("refused"));
        r.Run(120);
        Assert.Single(r.Data.Orders.Racetracks);
        Assert.Equal("Your order · racetrack", r.Data.Get(a.Id)!.ControlText);
        Assert.True(a.Bda!.OnRoute);
    }

    [Fact]
    public void EngageSendsShootersAtTheTargetGroup()
    {
        var r = new Rig();
        r.Run(60);
        var target = r.Sim.Objs.First(o => o.Alive && o.Side == "red" && o.Kind == "ftr" && !o.Stealth);
        var u = r.U("Saber 1");
        var res = r.Call("c2.order.issue", JsonValue.Obj().Set("type", "engage").Set("side", "Blue")
            .Set("units", JsonValue.ArrOf(Rig.Ref(u))).Set("target", Rig.Ref(target)));
        Assert.Equal(1, res.GetInt("accepted"));
        r.Run(1);
        Assert.Equal("Your order · attack", r.Data.Get(u.Id)!.ControlText);
        Assert.True(u.Bda!.Override != 0);
    }

    [Fact]
    public void DeckLaunchPutsReadyAircraftInTheAirAndRefusesAnEmptyDeck()
    {
        var r = new Rig();
        var k = r.U("Kestrel");
        int deck = r.Sim.DeckOf(k).Count();
        var res = r.Call("demo.deck.launch", JsonValue.Obj().Set("carrier", Rig.Ref(k)).Set("hunt", false));
        Assert.Equal(deck, res.GetInt("launched"));
        Assert.Empty(r.Sim.DeckOf(k));
        var err = r.Refused("demo.deck.launch", JsonValue.Obj().Set("carrier", Rig.Ref(k)));
        Assert.StartsWith("No aircraft are ready yet", err.Message);
    }

    [Fact]
    public void AllAttackLaunchesTheDecksAndHunts()
    {
        var r = new Rig();
        r.Run(30);
        var res = r.Call("c2.order.issue", JsonValue.Obj().Set("type", "all_attack").Set("side", "Blue").Set("label", "All Blue forces"));
        var s = res.GetString("summary")!;
        Assert.StartsWith("All Blue forces: attack nearest.", s);
        Assert.Contains("carriers launched 18 fighters", s);
        Assert.Contains("Not armed for this", s);
        var sw = Stopwatch.StartNew();
        r.Run(30);
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"30 s of battle with all units commanded took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains(r.Data.Tracks, t => t.ControlText == "Your order · attack nearest");
    }
}
