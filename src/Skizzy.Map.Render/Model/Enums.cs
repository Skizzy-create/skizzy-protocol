namespace Skizzy.Map.Render.Model;

/// <summary>The team that owns a track (ground truth). Rendering uses <see cref="Affiliation"/>.</summary>
public enum Side : byte { Blue, Red, Green, Other }

/// <summary>Standard identity as the viewing operator perceives it (MIL-STD-2525 frame + colour).</summary>
public enum Affiliation : byte { Friend, Hostile, Unknown, Neutral }

/// <summary>Battle dimension, which selects the frame variant.</summary>
public enum TrackDomain : byte { Air, Surface, Subsurface }

/// <summary>Track class. Drives the icon glyph, the default RCS and whether it is a missile.</summary>
public enum TrackKind : byte
{
    Unknown,
    Carrier,
    Destroyer,
    Submarine,
    EarlyWarning,
    Fighter,
    Strike,
    Uav,
    Ucav,
    AntiShipMissile,
    SurfaceToAirMissile,
    AirToAirMissile,
}

/// <summary>Who drives a unit. Only <see cref="Manual"/>, <see cref="Order"/> and <see cref="Agent"/> get a badge.</summary>
public enum ControlState : byte { None, Mission, Bda, Script, Order, Manual, Agent }

/// <summary>
/// Order kinds drawn on the plot (design/23 §4.10 and §6 learnings 13–15, mockup <c>drawOrders()</c>).
/// Every kind counts towards the "more than 6 active orders" fade.
/// </summary>
public enum OrderType : byte
{
    /// <summary>Accent dashed line to <see cref="PlotOrder.Destination"/> plus a diamond.</summary>
    Move,
    /// <summary>Accent dashed line to <see cref="PlotOrder.TargetTrackId"/>.</summary>
    Attack,
    /// <summary>Shared route (<see cref="PlotOrder.RouteId"/>) drawn once, plus a faint line to this unit's next point.</summary>
    Route,
    /// <summary>
    /// Racetrack patrol (<c>hold_pattern</c>, doc 09 §2.1.2): the shared pattern (<see cref="PlotOrder.PatternId"/>)
    /// is drawn once as a closed stadium with a direction arrow; no per-unit line.
    /// </summary>
    Racetrack,
    /// <summary>
    /// Aircraft RTB to a carrier or airfield (<c>rtb</c>, doc 09 §2.1.1). <see cref="PlotOrder.TargetTrackId"/> is the
    /// base. Each base with at least one RTB order gets a dashed MARSHAL ring astern of it; units in the
    /// <see cref="RtbPhase.Inbound"/> phase also get a dashed line to the marshal point. Ships returning to station are
    /// sent as <see cref="Move"/> (fixed station) or <see cref="Defend"/> (station on the carrier).
    /// </summary>
    Rtb,
    /// <summary>Protect a unit (<c>defend_unit</c>): accent dashed line to the protected unit, <see cref="PlotOrder.TargetTrackId"/>.</summary>
    Defend,
    /// <summary>
    /// Hunt / attack-nearest (an engage order that retargets to the nearest unengaged member, design/23 §6.1, or an
    /// engage-on-contact diversion): accent dashed line to the current target, <see cref="PlotOrder.TargetTrackId"/>;
    /// no line while it has none (0).
    /// </summary>
    Hunt,
}

/// <summary>Phase of an aircraft RTB (doc 09 §2.1.2): only <see cref="Inbound"/> draws the line to the marshal point.</summary>
public enum RtbPhase : byte { Inbound, Marshal, Approach }

/// <summary>
/// How the viewing side knows a track (doc 24 row 7, design/23 §6.15 "stealth").
/// </summary>
public enum TrackDetection : byte
{
    /// <summary>On the side's shared picture (the default).</summary>
    Shared,
    /// <summary>
    /// Seen only by one own unit's own sensor, not yet on the shared picture. Drawn normally plus a dashed halo in
    /// the affiliation colour (the renderer's proposal; the mockup has no such state yet).
    /// </summary>
    OwnSensor,
    /// <summary>Not detected (for example a stealth contact no own sensor sees): not drawn at all.</summary>
    None,
}

public enum ZoneKind : byte { Exclusion, Patrol, Bullseye }

public enum PlotEventKind : byte { Intercept, Ciws, Impact, Kill, Miss }

/// <summary>Symbol sizing mode (the mockup's "Symbols" selector).</summary>
public enum SizeMode : byte
{
    /// <summary>RCS hierarchy x zoom factor, capped by the nearest peer. The default.</summary>
    Auto,
    /// <summary>RCS hierarchy only.</summary>
    Rcs,
    /// <summary>Fixed base size that grows with zoom.</summary>
    Zoom,
    /// <summary>Fixed size.</summary>
    Fixed,
}

/// <summary>Level of detail a symbol is drawn at.</summary>
public enum SymbolTier : byte
{
    /// <summary>Under 8 px: a dot with a shape hint.</summary>
    Dot,
    /// <summary>8-16 px: frame only.</summary>
    Frame,
    /// <summary>16 px and over: frame plus icon.</summary>
    Full,
    /// <summary>Missile arrowhead (5 px and over).</summary>
    Missile,
}
