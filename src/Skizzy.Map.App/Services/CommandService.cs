using System.Globalization;
using System.Text.RegularExpressions;
using Skizzy.Client;
using Skizzy.Map.Data;
using Skizzy.Map.Data.Demo;
using Skizzy.Map.Render.Model;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Map.App.Services;

/// <summary>A plot mode waiting for the next click or drag (the mockup's pendingMove and pendingTrack).</summary>
public enum PendingMode { None, SendTo, Racetrack }

/// <summary>One call the command service made: the method, its arguments, and the reply or the error.</summary>
public sealed record CommandRecord(string Method, string Args, bool Ok, string? Reply, string? Error);

/// <summary>Why a control is disabled on a source (the exceptions in docs/M3-command-parity.md).</summary>
public static class Unsupported
{
    public const string FriendlyFire = "Friendly fire in KSP is BDArmory physics. The host has no misfire capability.";
    public const string Seat = "The host's seat is informational in M0. There is no seat hand-over capability yet.";
    public const string FlyInKsp = "The host has no \"make active vessel\" capability (ksp.vessel.set_active is not in the catalog).";
    public const string DeckLaunch = "Deck launch needs the M8 launch adapter (24-ksp-parity row 31).";
    public const string Radar = "The C2 doctrine has no EMCON key, and BDA radar control is not exposed by the host.";
    public const string Orders = "This source does not take orders.";
}

/// <summary>
/// The map's one command path (docs/M3-command-parity.md). Every order, doctrine change and VIP flag goes out as the
/// same c2.* call agents and the CLI use, through <see cref="IDataSource.CallAsync"/>: the demo battle serves it with
/// the real C2 engine in-process; FakeKSP and the live host send it through SkizzyClient. The log lines, toasts and
/// confirmation flows are the mockup's (design/mockups/command-screen-v1.html). Refusals come back with the engine's
/// reasons and go to Messages (Orders) and a toast. UI thread only.
/// </summary>
public sealed partial class CommandService
{
    /// <summary>All attack and RTB all need a second click (or key press) within this many seconds.</summary>
    public const double ConfirmSeconds = 3;

    /// <summary>The mockup's racetrack: 6 km wide; a plain click makes a 20 km leg pointing east.</summary>
    public const double RacetrackWidthKm = 6, RacetrackClickLegKm = 20, RacetrackMinLegKm = 3;

    private readonly AppState _app;
    private readonly List<Task> _inflight = new();
    private readonly Dictionary<string, string> _sideOf = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _weapons = new();
    private readonly Dictionary<int, string> _onContact = new();
    private double _allAttackT = double.NegativeInfinity, _rtbAllT = double.NegativeInfinity;

    public CommandService(AppState app)
    {
        _app = app;
        Clock = () => app.Now;
    }

    /// <summary>Seconds, for the two-click confirmations (tests replace it).</summary>
    public Func<double> Clock { get; set; }

    /// <summary>Every call made, in order (tests and diagnostics).</summary>
    public List<CommandRecord> History { get; } = new();

    /// <summary>Units under "Take control" (shown as "Manual · you" until Return to mission).</summary>
    public HashSet<int> Manual { get; } = new();

    /// <summary>Units with an accepted order from this map (live sources, which have no order picture of their own).</summary>
    public HashSet<int> Commanded { get; } = new();

    public PendingMode Pending { get; private set; }

    /// <summary>The demo's Blue seat is handed to the agent.</summary>
    public bool SeatAgent { get; private set; }

    /// <summary>Last advance-on-contact and posture this map set on a source that does not report them (live).</summary>
    public bool Advance { get; private set; } = true;
    public string Posture { get; private set; } = "Proactive";

    /// <summary>Pending mode, confirmations, seat, manual units or a call result changed: views re-sync.</summary>
    public event Action? Changed;

    private MapData Data => _app.Data;
    private IDataSource Source => _app.Source;

    /// <summary>The source as a command target, or null when it does not take orders.</summary>
    public ICommandSource? Cmd => (Source.Caps & SourceCaps.Orders) != 0 ? Source as ICommandSource : null;

    public bool CanCommand => Cmd is not null;
    public bool IsDemo => Source is DemoBattleSource;

    /// <summary>The source changed (or restarted): pending modes, confirmations and per-unit state start over.</summary>
    internal void Reset(bool keepSeat)
    {
        Pending = PendingMode.None;
        Manual.Clear();
        Commanded.Clear();
        _sideOf.Clear();
        _weapons.Clear();
        _onContact.Clear();
        _allAttackT = _rtbAllT = double.NegativeInfinity;
        if (!keepSeat) SeatAgent = false;
        Advance = true;
        Posture = "Proactive";
        Changed?.Invoke();
    }

    /// <summary>Completes when every call in flight has answered (live sources answer asynchronously).</summary>
    public async Task WhenIdle()
    {
        while (_inflight.Count > 0)
        {
            var t = _inflight[0];
            _inflight.RemoveAt(0);
            await t;
        }
    }

    // ------------------------------------------------------------ ownership and selection

    /// <summary>
    /// A unit the operator may order: Blue on the demo; on a live host every vessel but missiles, because the map's
    /// stream does not carry BDArmory teams. The engine refuses another side's units with its reason.
    /// </summary>
    public bool IsOwn(TrackInfo t) =>
        t.Alive && !t.IsMissile && (t.Side == Side.Blue || (Cmd is { CommandSide: null } && t.Side == Side.Other));

    public List<TrackInfo> SelectedOwn(TrackInfo? except = null)
    {
        var l = new List<TrackInfo>();
        foreach (var id in _app.Selection)
            if (Data.Get(id) is { } t && t != except && IsOwn(t)) l.Add(t);
        return l;
    }

    /// <summary>Every unit the operator may order, the active vessel first.</summary>
    public List<TrackInfo> AllOwn()
    {
        var l = new List<TrackInfo>();
        foreach (var t in Data.Tracks) if (IsOwn(t)) l.Add(t);
        l.Sort((a, b) => (a.Side == Side.Blue ? 0 : 1).CompareTo(b.Side == Side.Blue ? 0 : 1));
        return l;
    }

    /// <summary>"Return to mission" applies: the unit is manual or under an order.</summary>
    public bool IsControlled(TrackInfo t) =>
        Manual.Contains(t.Id) || (IsDemo ? t.Control == ControlState.Order : Commanded.Contains(t.Id));

    /// <summary>Weapons control shown in the unit panel: the source's value, else what this map last set.</summary>
    public string? WeaponsOf(TrackInfo t) => t.Details.Doctrine ?? _weapons.GetValueOrDefault(t.Id);

    public string? OnContactOf(TrackInfo t) => t.Details.OnContact ?? _onContact.GetValueOrDefault(t.Id);

    // ------------------------------------------------------------ pending modes

    public void ToggleSendTo()
    {
        Pending = Pending == PendingMode.SendTo ? PendingMode.None : PendingMode.SendTo;
        if (Pending == PendingMode.SendTo) _app.Toast("Click the plot where the selected units should go, or click a hostile to attack it.");
        Changed?.Invoke();
    }

    /// <summary>T or Racetrack…: the next drag on the plot sets the racetrack's long leg.</summary>
    public void ToggleRacetrack(bool fromKey)
    {
        Pending = Pending == PendingMode.Racetrack ? PendingMode.None : PendingMode.Racetrack;
        if (Pending == PendingMode.Racetrack)
            _app.Toast("Racetrack: drag on the plot along the long leg. A plain click makes a 20 km leg pointing east. " + (fromKey ? "T again cancels." : "Esc cancels."));
        Changed?.Invoke();
    }

    /// <summary>Esc: drops a pending mode. False when there was none.</summary>
    public bool CancelPending()
    {
        if (Pending == PendingMode.None) return false;
        Pending = PendingMode.None;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The click after Send to…: move there, or attack the clicked hostile.</summary>
    public Task SendToClick(double x, double y, TrackInfo? hit)
    {
        Pending = PendingMode.None;
        Changed?.Invoke();
        return hit is { Alive: true, Side: Side.Red } ? Attack(hit, x, y) : MoveTo(x, y);
    }

    // ------------------------------------------------------------ two-click confirmations

    public bool AllAttackArmed => Clock() - _allAttackT <= ConfirmSeconds;
    public bool RtbAllArmed => Clock() - _rtbAllT <= ConfirmSeconds;

    /// <summary>All attack (top bar or Shift+A): the first press arms it for 3 s, the second issues it.</summary>
    public Task AllAttack()
    {
        if (!AllAttackArmed)
        {
            _allAttackT = Clock();
            _app.Toast("Click All attack again (or Shift+A) within 3 s: every Blue unit attacks the nearest hostiles and the carriers launch their decks.");
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        _allAttackT = double.NegativeInfinity;
        Changed?.Invoke();
        return Run(AllAttackCore);
    }

    /// <summary>RTB all (top bar or Shift+R): the first press arms it for 3 s, the second sends every aircraft home.</summary>
    public Task RtbAll()
    {
        var air = AllOwn().Where(t => t.Domain == TrackDomain.Air).ToList();
        if (!RtbAllArmed)
        {
            _rtbAllT = Clock();
            _app.Toast($"Press Shift+R again (or click RTB all again) within 3 s to send all {air.Count} aircraft home to their carriers.");
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        _rtbAllT = double.NegativeInfinity;
        Changed?.Invoke();
        if (air.Count == 0) { _app.Toast("No aircraft to send home."); return Task.CompletedTask; }
        return Run(() => RtbCore(air, $"All aircraft ({air.Count})"));
    }

    // ------------------------------------------------------------ plot gestures

    /// <summary>
    /// Right-click on the plot (the mockup's contextmenu): with own units selected, right-clicking another own unit
    /// makes them protect it, a hostile is attacked with its whole group, anywhere else is a move in formation.
    /// </summary>
    public Task RightClick(double x, double y, TrackInfo? hit)
    {
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select one or more of your own units first, then right-click where they should go.");
            return Task.CompletedTask;
        }
        if (hit is { Alive: true, IsMissile: false, Side: Side.Blue } && !_app.Selection.Contains(hit.Id)) return Protect(hit);
        if (hit is { Alive: true, Side: Side.Red }) return Attack(hit, x, y);
        if (hit is { Alive: true, IsMissile: false, Side: Side.Other } && Cmd is { CommandSide: null })
            return Run(() => Guard("Order", (x, y), async () =>
            {
                // a live vessel's side is the engine's: same side = protect it, another side = attack it
                string? ours = await SideFor(units);
                string? theirs = await SideOf(hit);
                if (theirs is null) await MoveCore(x, y, units);
                else if (theirs == ours) { if (_app.Selection.Contains(hit.Id)) await MoveCore(x, y, units); else await ProtectCore(hit, SelectedOwn(hit)); }
                else await AttackCore(hit, units, x, y);
            }));
        return MoveTo(x, y);
    }

    public Task MoveTo(double x, double y)
    {
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select one or more of your own units first, then right-click where they should go.");
            return Task.CompletedTask;
        }
        return Run(() => MoveCore(x, y, units));
    }

    public Task Attack(TrackInfo target, double x, double y)
    {
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select one or more of your own units first, then right-click where they should go.");
            return Task.CompletedTask;
        }
        return Run(() => AttackCore(target, units, x, y));
    }

    public Task Protect(TrackInfo vip)
    {
        var units = SelectedOwn(vip);
        if (units.Count == 0)
        {
            _app.Toast("Select the units that should protect it first, then right-click the unit to protect.");
            return Task.CompletedTask;
        }
        return Run(() => ProtectCore(vip, units));
    }

    /// <summary>Right-drag: the selection follows the drawn route (points in plot km).</summary>
    public Task Route(IReadOnlyList<(double X, double Y)> pts)
    {
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select your units first, then right-drag a route on the plot.");
            return Task.CompletedTask;
        }
        if (pts.Count == 0) return Task.CompletedTask;
        var copy = pts.ToList();
        return Run(() => RouteCore(copy, units));
    }

    /// <summary>The racetrack drag (after T or Racetrack…): A to B is the long leg; under 3 km it becomes 20 km east.</summary>
    public Task Racetrack((double X, double Y) a, (double X, double Y) b)
    {
        if (Pending == PendingMode.Racetrack) { Pending = PendingMode.None; Changed?.Invoke(); }
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select your aircraft first, then choose Racetrack and drag its long leg on the plot.");
            return Task.CompletedTask;
        }
        if (Dist(a.X, a.Y, b.X, b.Y) < RacetrackMinLegKm) b = (a.X + RacetrackClickLegKm, a.Y);
        return Run(() => RacetrackCore(a, b, units));
    }

    // ------------------------------------------------------------ unit panel and keys

    /// <summary>A or Attack nearest: the selection hunts; ships go weapons free; the demo's carriers launch their decks.</summary>
    public Task AttackNearest()
    {
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select your units first, then press A or Attack nearest.");
            return Task.CompletedTask;
        }
        return Run(() => Guard("Attack nearest", null, async () =>
        {
            var b = Refs(units);
            var (reply, ok) = await Issue(JsonValue.Obj().Set("type", "attack_nearest").Set("label", "Selection"), b, units);
            var (px, py) = Pos(ok.Count > 0 ? ok[0] : units[0]);
            Log(Head(reply, "attack_nearest"), px, py);
            Partial("Attack nearest", reply, (px, py), skipNotArmed: true);
        }));
    }

    /// <summary>R or RTB &amp; land: aircraft to the marshal stack and land; ships back to station; carriers refused.</summary>
    public Task Rtb()
    {
        var units = SelectedOwn();
        if (units.Count == 0)
        {
            _app.Toast("Select your units first, then press R or RTB & land.");
            return Task.CompletedTask;
        }
        return Run(() => RtbCore(units, null));
    }

    /// <summary>V or Mark VIP: toggles VIP on the selection (on unless every one is VIP already).</summary>
    public Task ToggleVip()
    {
        var us = SelectedOwn();
        if (us.Count == 0)
        {
            _app.Toast("Select one or more of your own units, then press V or Mark VIP.");
            return Task.CompletedTask;
        }
        bool on = !us.All(u => u.Vip);
        var (px, py) = Pos(us[0]);
        return Run(() => Guard("VIP", (px, py), async () =>
        {
            var done = await ForEach("VIP", us, (px, py), async u =>
            {
                var r = await Call("c2.vip.set", JsonValue.Obj().Set("unit", Ref(u)).Set("vip", on));
                u.Vip = r.GetBool("vip", on);
            });
            if (done.Count == 0) return;
            string names = NamesN(done, "units");
            Log(on ? $"{names} marked VIP. Fighters, drones and ships now give threats to {(done.Count > 1 ? "them" : "it")} priority." : $"{names}: VIP protection removed.", px, py);
        }));
    }

    /// <summary>
    /// Take control: each selected unit holds where it is (a move_to at its own position, label take_control) until
    /// Return to mission; the panel shows it as "Manual · you".
    /// </summary>
    public Task TakeControl()
    {
        var us = SelectedOwn();
        if (us.Count == 0)
        {
            _app.Toast("Select one or more of your own units first.");
            return Task.CompletedTask;
        }
        var (px, py) = Pos(_app.PrimaryTrack ?? us[0]);
        return Run(() => Guard("Take control", (px, py), async () =>
        {
            var taken = await ForEach("Take control", us, (px, py), async u =>
            {
                var (x, y) = Pos(u);
                var b = Refs([u]);
                await Issue(JsonValue.Obj().Set("type", "move_to").Set("point", Geo(x, y)).Set("label", "take_control"), b, [u]);
            });
            foreach (var u in taken) Manual.Add(u.Id);
            if (taken.Count > 0) Log($"You took control of {Names(taken)}. The mission is suspended, not cancelled.", px, py);
        }));
    }

    /// <summary>Return to mission: releases the selection's manual and ordered units; the mission re-plans.</summary>
    public Task ReturnToMission()
    {
        var o = _app.PrimaryTrack;
        var us = SelectedOwn().Where(IsControlled).ToList();
        if (o is null || us.Count == 0)
        {
            _app.Toast("Select a unit under your control or order first.");
            return Task.CompletedTask;
        }
        var (px, py) = Pos(o);
        return Run(() => Guard("Return to mission", (px, py), async () =>
        {
            var b = Refs(us);
            await Call("c2.unit.release", JsonValue.Obj().Set("units", b.Refs));
            foreach (var u in us) { Manual.Remove(u.Id); Commanded.Remove(u.Id); }
            Log($"{o.Name} returned to {o.Details.Mission ?? "its mission"}. The mission re-plans from the unit's position.", px, py);
        }));
    }

    /// <summary>Weapons control Free / Tight / Hold for the selection (unit doctrine weaponsControl).</summary>
    public Task SetWeapons(string level)
    {
        var us = SelectedOwn().Where(u => u.Details.Doctrine is not null || !IsDemo).ToList();
        if (us.Count == 0) return Task.CompletedTask;
        var (px, py) = Pos(_app.PrimaryTrack ?? us[0]);
        string v = level.ToLowerInvariant();
        return Run(() => Guard("Weapons control", (px, py), async () =>
        {
            var done = await ForEach("Weapons control", us, (px, py), async u =>
            {
                await Call("c2.doctrine.set", JsonValue.Obj().Set("level", "unit").Set("target", Ref(u)).Set("values", JsonValue.Obj().Set("weaponsControl", v)));
                _weapons[u.Id] = level;
            });
            if (done.Count == 0) return;
            Log($"{NamesN(done, "units")}: weapons {v}{(level == "Free" ? " (guard)" : level == "Hold" ? " (passive)" : "")}.", px, py);
        }));
    }

    /// <summary>When a new hostile appears during an order: "task" (stay on task) or "engage" (engage on contact).</summary>
    public Task SetOnContact(string value)
    {
        var us = SelectedOwn().Where(u => u.Details.Doctrine is not null || !IsDemo).ToList();
        if (us.Count == 0) return Task.CompletedTask;
        var (px, py) = Pos(_app.PrimaryTrack ?? us[0]);
        return Run(() => Guard("On contact", (px, py), async () =>
        {
            var done = await ForEach("On contact", us, (px, py), async u =>
            {
                await Call("c2.doctrine.set", JsonValue.Obj().Set("level", "unit").Set("target", Ref(u)).Set("values", JsonValue.Obj().Set("onContact", value)));
                _onContact[u.Id] = value;
            });
            if (done.Count == 0) return;
            Log($"{NamesN(done, "units")}: {(value == "engage" ? "engage any hostile detected during orders" : "stay on task during orders")}.", px, py);
        }));
    }

    /// <summary>Radar Active / Passive for the primary ship (demo only; the host has no EMCON control).</summary>
    public Task SetRadar(string emcon)
    {
        var o = _app.PrimaryTrack;
        if (o is null) return Task.CompletedTask;
        if (!IsDemo) { _app.Toast(Unsupported.Radar); return Task.CompletedTask; }
        var (px, py) = Pos(o);
        return Run(() => Guard("Radar", (px, py), async () =>
        {
            await Call("demo.unit.emcon", JsonValue.Obj().Set("unit", Ref(o)).Set("emcon", emcon.ToLowerInvariant()));
            Log($"{o.Name}: radar {emcon.ToLowerInvariant()}.", px, py);
        }));
    }

    /// <summary>Launch ready aircraft (demo's launch adapter; the sim logs the launch).</summary>
    public Task LaunchDeck(TrackInfo carrier)
    {
        if (!IsDemo) { _app.Toast(Unsupported.DeckLaunch); return Task.CompletedTask; }
        var (px, py) = Pos(carrier);
        return Run(() => Guard("Launch", (px, py), async () =>
            await Call("demo.deck.launch", JsonValue.Obj().Set("carrier", Ref(carrier)).Set("hunt", false))));
    }

    /// <summary>Fly in KSP: the mockup's explanation on the demo; the host cannot change the active vessel yet.</summary>
    public void FlyInKsp(TrackInfo t)
    {
        if (IsDemo) _app.Toast($"In the real build this makes {t.Name} the active vessel in KSP so you can fly it yourself. The map keeps tracking it.");
        else _app.Toast(Unsupported.FlyInKsp);
    }

    // ------------------------------------------------------------ side-level controls (top bar)

    public Task SetAdvance(bool on) => Run(() => Guard("Advance on contact", null, async () =>
    {
        await SideDoctrine(JsonValue.Obj().Set("advanceOnContact", on));
        Advance = on;
        Log(on ? "Advance on contact on." : "Advance on contact off: aircraft hold their stations until ordered.");
    }));

    public Task SetPosture(string posture) => Run(() => Guard("Posture", null, async () =>
    {
        bool pro = posture == "Proactive";
        await SideDoctrine(JsonValue.Obj().Set("posture", pro ? "proactive" : "defensive"));
        Posture = pro ? "Proactive" : "Defensive";
        Log(pro
            ? "Blue posture proactive: fighters hunt the strike aircraft, destroyers engage aircraft out to 90 km, the carrier launches alert fighters."
            : "Blue posture defensive: fighters hold their station, destroyers engage aircraft inside 60 km.");
    }));

    /// <summary>Friendly-fire simulation rate (demo; the sim logs the mockup's line).</summary>
    public void SetFriendlyFire(double chance)
    {
        if (Source is DemoBattleSource d) d.SetFriendlyFire(chance);
        else _app.Toast(Unsupported.FriendlyFire);
    }

    /// <summary>Hand the Blue seat to the agent, or take it back (demo).</summary>
    public void ToggleSeat()
    {
        if (!IsDemo) { _app.Toast(Unsupported.Seat); return; }
        SeatAgent = !SeatAgent;
        Log(SeatAgent ? "Blue seat handed to agent (Claude Code). Missions and orders stay as they are." : "You took the Blue seat back. Agent orders are suspended, not deleted.");
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ order cores

    private Task MoveCore(double x, double y, List<TrackInfo> units) => Guard("Move", (x, y), async () =>
    {
        var b = Refs(units);
        var (reply, ok) = await Issue(JsonValue.Obj().Set("type", "move_to").Set("point", Geo(x, y)), b, units);
        var (rx, ry) = RefPt;
        Log($"{NamesGrouped(ok)}: move to bearing {MapData.Pad3(MapData.Bearing(rx, ry, x, y))}, {Km(Dist(rx, ry, x, y))} km from {RefName}.", x, y);
        Partial("Move", reply, (x, y));
    });

    private Task AttackCore(TrackInfo target, List<TrackInfo> units, double x, double y) => Guard("Attack", (x, y), async () =>
    {
        var b = Refs(units);
        var (reply, ok) = await Issue(JsonValue.Obj().Set("type", "engage").Set("target", Ref(target)), b, units);
        var m = TargetsRx().Match(reply.GetString("summary") ?? "");
        int n = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        Log($"{NamesGrouped(ok)}: attack {target.Name}{(n > 1 ? $" and the rest of its group ({n} aircraft)" : "")}.", x, y);
        Partial("Attack", reply, (x, y));
    });

    private Task ProtectCore(TrackInfo vip, List<TrackInfo> units)
    {
        var (vx, vy) = Pos(vip);
        return Guard("Protect", (vx, vy), async () =>
        {
            if (units.Count == 0) { _app.Toast("Select the units that should protect it first, then right-click the unit to protect."); return; }
            bool wasVip = vip.Vip;
            var b = Refs(units);
            var (reply, _) = await Issue(JsonValue.Obj().Set("type", "defend_unit").Set("protect", Ref(vip)), b, units);
            string s = Head(reply, "defend_unit");
            if (!wasVip) s = s.Replace($"protect {vip.Name}.", $"protect {vip.Name} (now marked VIP).", StringComparison.Ordinal);
            vip.Vip = true;
            Log(s, vx, vy);
            Partial("Protect", reply, (vx, vy));
        });
    }

    private Task RouteCore(List<(double X, double Y)> pts, List<TrackInfo> units)
    {
        var end = pts[^1];
        return Guard("Route", end, async () =>
        {
            var route = JsonValue.Arr();
            foreach (var (x, y) in pts) route.Add(Geo(x, y));
            var b = Refs(units);
            var (reply, ok) = await Issue(JsonValue.Obj().Set("type", "move_route").Set("route", route), b, units);
            var (rx, ry) = RefPt;
            Log($"{NamesGrouped(ok)}: follow drawn route, {pts.Count} points, ending bearing {MapData.Pad3(MapData.Bearing(rx, ry, end.X, end.Y))}, {Km(Dist(rx, ry, end.X, end.Y))} km.", end.X, end.Y);
            Partial("Route", reply, end);
        });
    }

    private Task RacetrackCore((double X, double Y) a, (double X, double Y) b, List<TrackInfo> units) => Guard("Racetrack", a, async () =>
    {
        var refs = Refs(units);
        var (reply, ok) = await Issue(JsonValue.Obj().Set("type", "hold_pattern").Set("patternA", Geo(a.X, a.Y)).Set("patternB", Geo(b.X, b.Y))
            .Set("widthKm", RacetrackWidthKm), refs, units);
        Partial("Racetrack", reply, a); // the mockup reports the refused ships first
        var (rx, ry) = RefPt;
        var alt = AltRx().Match(reply.GetString("summary") ?? "");
        string stacked = ok.Count > 1 ? "300 m apart" : "at " + (alt.Success ? alt.Groups[1].Value : DefaultPatternAlt(ok[0]).ToString(CultureInfo.InvariantCulture)) + " m";
        Log($"{NamesN(ok, "aircraft")}: racetrack holding pattern, {Km(Dist(a.X, a.Y, b.X, b.Y))} km legs, {RacetrackWidthKm.ToString(CultureInfo.InvariantCulture)} km wide, " +
            $"{MapData.Pad3(MapData.Bearing(a.X, a.Y, b.X, b.Y))}°, {Km(Dist(rx, ry, a.X, a.Y))} km from {RefName}. Stacked {stacked}.", a.X, a.Y);
    });

    private Task RtbCore(List<TrackInfo> units, string? label)
    {
        var (rx, ry) = RefPt;
        return Guard("RTB", (rx, ry), async () =>
        {
            var args = JsonValue.Obj().Set("type", "rtb");
            if (label is not null) args.Set("label", label);
            var b = Refs(units);
            var (reply, _) = await Issue(args, b, units);
            Log(Head(reply, "rtb"), rx, ry);
            Partial("RTB", reply, (rx, ry));
        });
    }

    private async Task AllAttackCore()
    {
        var own = AllOwn();
        await Guard("All attack", null, async () =>
        {
            var b = Refs(own);
            var (reply, _) = await Issue(JsonValue.Obj().Set("type", "all_attack").Set("scope", "side"), b, own, sendUnits: false);
            Log(Head(reply, "all_attack"));
            Partial("All attack", reply, null, skipNotArmed: true);
        });
    }

    private async Task SideDoctrine(JsonValue values)
    {
        string side = await SideFor(AllOwn()) ?? throw new InvalidOperationException("the C2 engine knows none of your units");
        await Call("c2.doctrine.set", JsonValue.Obj().Set("level", "side").Set("target", side).Set("values", values));
    }

    private static int DefaultPatternAlt(TrackInfo t) => t.Kind switch { TrackKind.Ucav => 1200, TrackKind.EarlyWarning => 8500, _ => 4500 };

    [GeneratedRegex(@"\((\d+) targets\)")]
    private static partial Regex TargetsRx();

    [GeneratedRegex(@" at (\d+) m\.")]
    private static partial Regex AltRx();

    // ------------------------------------------------------------ the c2.* calls

    private sealed class Batch
    {
        public readonly Dictionary<string, TrackInfo> ByRef = new(StringComparer.Ordinal);
        public readonly JsonValue Refs = JsonValue.Arr();
    }

    private Batch Refs(IEnumerable<TrackInfo> units)
    {
        var b = new Batch();
        var cmd = Cmd ?? throw new InvalidOperationException(Unsupported.Orders);
        foreach (var u in units)
            if (cmd.UnitRef(u) is { } r && b.ByRef.TryAdd(r, u)) b.Refs.Add(r);
        return b;
    }

    private string Ref(TrackInfo t) =>
        (Cmd ?? throw new InvalidOperationException(Unsupported.Orders)).UnitRef(t) ?? throw new InvalidOperationException($"{t.Name} is not a vessel the C2 engine knows");

    private JsonValue Geo(double x, double y)
    {
        var cmd = Cmd ?? throw new InvalidOperationException(Unsupported.Orders);
        if (!cmd.TryToGeo(x, y, out var lat, out var lon)) throw new InvalidOperationException("the map has no position fix yet");
        return JsonValue.Obj().Set("lat", Math.Round(lat, 6)).Set("lon", Math.Round(lon, 6));
    }

    /// <summary>c2.order.issue for the batch; returns the reply and the accepted units.</summary>
    private async Task<(JsonValue Reply, List<TrackInfo> Accepted)> Issue(JsonValue args, Batch b, IReadOnlyList<TrackInfo> units, bool sendUnits = true)
    {
        string side = await SideFor(units) ?? throw new InvalidOperationException("the C2 engine knows none of the selected units");
        args.Set("side", side);
        if (sendUnits) args.Set("units", b.Refs);
        var reply = await Call("c2.order.issue", args);
        var ok = new List<TrackInfo>();
        if (reply.GetArr("units") is { } arr)
            for (int i = 0; i < arr.Count; i++)
                if (arr[i].GetBool("accepted") && arr[i].GetString("unit") is { } h && b.ByRef.TryGetValue(h, out var t)) { ok.Add(t); Commanded.Add(t.Id); }
        return (reply, ok);
    }

    /// <summary>The side the operator commands: the source's (demo), else the engine's side of the first known unit.</summary>
    private async Task<string?> SideFor(IReadOnlyList<TrackInfo> units)
    {
        var cmd = Cmd ?? throw new InvalidOperationException(Unsupported.Orders);
        if (cmd.CommandSide is { } s) return s;
        foreach (var u in units.OrderBy(u => u.Side == Side.Blue ? 0 : 1))
            if (await SideOf(u) is { } side) return side;
        return null;
    }

    /// <summary>A unit's side from c2.unit.status (cached); null when the engine does not know the unit.</summary>
    private async Task<string?> SideOf(TrackInfo t)
    {
        var cmd = Cmd ?? throw new InvalidOperationException(Unsupported.Orders);
        if (cmd.UnitRef(t) is not { } r) return null;
        if (_sideOf.TryGetValue(r, out var c)) return c;
        try
        {
            var st = await Call("c2.unit.status", JsonValue.Obj().Set("unit", r));
            if (st.GetString("side") is { } side) { _sideOf[r] = side; return side; }
        }
        catch (SkizzyError e) when (e.Kind == ErrorKind.NotFound) { }
        return null;
    }

    private async Task<JsonValue> Call(string method, JsonValue args)
    {
        string json = JsonWriter.Serialize(args);
        try
        {
            var res = await Source.CallAsync(method, json, CancellationToken.None);
            History.Add(new CommandRecord(method, json, true, res, null));
            return string.IsNullOrWhiteSpace(res) ? JsonValue.Obj() : JsonParser.Parse(res);
        }
        catch (Exception e) when (IsCallError(e))
        {
            History.Add(new CommandRecord(method, json, false, null, Message(e)));
            throw;
        }
    }

    // ------------------------------------------------------------ running and reporting

    private Task Run(Func<Task> body)
    {
        var t = RunCore(body);
        if (!t.IsCompleted) _inflight.Add(t);
        return t;
    }

    private async Task RunCore(Func<Task> body)
    {
        try { await body(); }
        finally
        {
            Changed?.Invoke();
            _app.RefreshPanels();
        }
    }

    /// <summary>Runs one order; a refusal or a failed call goes to Messages and a toast instead of throwing.</summary>
    private async Task Guard(string verb, (double X, double Y)? at, Func<Task> body)
    {
        try { await body(); }
        catch (Exception e) when (IsCallError(e)) { Refused(verb, e, at); }
    }

    /// <summary>The same call per unit; returns the units it worked for and reports the others in one line.</summary>
    private async Task<List<TrackInfo>> ForEach(string verb, List<TrackInfo> units, (double X, double Y) at, Func<TrackInfo, Task> one)
    {
        var done = new List<TrackInfo>();
        var why = new List<string>();
        foreach (var u in units)
        {
            try { await one(u); done.Add(u); }
            catch (Exception e) when (IsCallError(e)) { why.AddRange(Reasons(e, u)); }
        }
        if (why.Count > 0) Report($"{verb} refused: {string.Join("; ", why.Distinct())}.", at);
        return done;
    }

    private static IEnumerable<string> Reasons(Exception e, TrackInfo u)
    {
        if (e is SkizzyError { Extra: { } x } && x.GetArr("units") is { Count: > 0 } arr)
        {
            for (int i = 0; i < arr.Count; i++)
                if (!arr[i].GetBool("accepted") && arr[i].GetString("reason") is { } r) yield return r.TrimEnd('.');
            yield break;
        }
        yield return u.Name + ": " + Sentence(Message(e));
    }

    private void Refused(string verb, Exception e, (double X, double Y)? at)
    {
        if (e is SkizzyError { Extra: { } x } && x.GetArr("units") is { Count: > 0 } units && RefusalLines(verb, units, false) is { Count: > 0 } lines)
        {
            foreach (var l in lines) Report(l, at);
            return;
        }
        if (e is SkizzyError { Reason: "deck_not_ready" })
        {
            Log($"{verb} refused: {Sentence(e.Message)}.", at?.X, at?.Y);
            _app.Toast(e.Message); // the mockup's toast
            return;
        }
        Report($"{verb} refused: {Sentence(Message(e))}.", at);
    }

    /// <summary>Reports the refused units of an accepted order.</summary>
    private void Partial(string verb, JsonValue reply, (double X, double Y)? at, bool skipNotArmed = false)
    {
        if (reply.GetArr("units") is not { Count: > 0 } units) return;
        foreach (var l in RefusalLines(verb, units, skipNotArmed)) Report(l, at);
    }

    private static List<string> RefusalLines(string verb, JsonValue units, bool skipNotArmed)
    {
        var lines = new List<string>();
        var reasons = new List<string>();
        var ships = new List<string>();
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u.GetBool("accepted")) continue;
            string code = u.GetString("code") ?? "", reason = (u.GetString("reason") ?? code).TrimEnd('.');
            if (skipNotArmed && code == "NOT_APPLICABLE") continue; // already in the summary's "Not armed for this"
            if (verb == "Racetrack" && code == "NOT_APPLICABLE" && reason.Contains("holding pattern", StringComparison.Ordinal)) ships.Add(u.GetString("name") ?? reason);
            else reasons.Add(reason);
        }
        if (ships.Count > 0) lines.Add($"Racetrack refused for {string.Join(", ", ships)}: ships cannot fly a holding pattern. Use Send to… or a drawn route.");
        if (reasons.Count > 0) lines.Add($"{verb} refused: {string.Join("; ", reasons)}.");
        return lines;
    }

    private void Report(string text, (double X, double Y)? at)
    {
        Log(text, at?.X, at?.Y);
        _app.Toast(text);
    }

    private void Log(string text, double? x = null, double? y = null) => Data.Log.Add(Data.LiveTime, LogCategory.Orders, text, x, y);

    /// <summary>The engine's summary without the appended "&lt;type&gt; refused for …" (reported on its own line).</summary>
    private static string Head(JsonValue reply, string wireType)
    {
        string s = reply.GetString("summary") ?? reply.GetString("text") ?? "";
        int k = s.IndexOf(" " + wireType + " refused for ", StringComparison.Ordinal);
        return (k >= 0 ? s[..k] : s).Trim();
    }

    internal static bool IsCallError(Exception e) =>
        e is SkizzyError or SkizzyClientException or TimeoutException or InvalidOperationException or OperationCanceledException or IOException
            or System.Net.WebSockets.WebSocketException;

    private static string Message(Exception e)
    {
        if (e is SkizzyError se)
        {
            string m = Sentence(se.Message);
            return string.IsNullOrEmpty(se.Hint) || se.Hint == ErrorKinds.DefaultHint(se.Kind) ? m : m + ". " + Sentence(se.Hint);
        }
        if (e is SkizzyClientException ce) return string.IsNullOrEmpty(ce.Hint) ? Sentence(ce.Message) : Sentence(ce.Message) + ". " + Sentence(ce.Hint);
        return Sentence(e.Message);
    }

    private static string Sentence(string s) => s.Trim().TrimEnd('.');

    // ------------------------------------------------------------ names and geometry (the mockup's wording)

    private (double X, double Y) RefPt => Data.ReferencePoint(Data.LiveTime);
    private string RefName => Data.Get(Data.ReferenceTrackId)?.Name ?? "the origin";
    private (double X, double Y) Pos(TrackInfo t) => Data.PositionOf(t, Data.LiveTime);

    private static string Names(IEnumerable<TrackInfo> us) => string.Join(", ", us.Select(u => u.Name));

    private static string NamesN(List<TrackInfo> us, string noun) => us.Count > 4 ? $"{us.Count} {noun}" : Names(us);

    private string NamesGrouped(List<TrackInfo> us) =>
        us.Count > 4 ? $"{us.Count} units ({string.Join(", ", us.Select(Data.GroupOf).Where(g => g.Length > 0).Distinct())})" : Names(us);

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    private static string Km(double d) => Math.Round(d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}
