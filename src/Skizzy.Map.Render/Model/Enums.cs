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

public enum OrderType : byte { Move, Attack, Route }

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
