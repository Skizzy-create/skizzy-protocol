using System.Numerics;

namespace Skizzy.Map.Render.Model;

/// <summary>An operator order shown on the plot. Positions in km.</summary>
public sealed class PlotOrder
{
    public int TrackId { get; set; }
    public OrderType Type { get; set; }

    /// <summary>Move destination (Move orders).</summary>
    public Vector2 Destination { get; set; }

    /// <summary>Attack target (Attack orders).</summary>
    public int TargetTrackId { get; set; }

    /// <summary>Route followed (Route orders).</summary>
    public int RouteId { get; set; }

    /// <summary>Index of the next route point.</summary>
    public int RouteIndex { get; set; }

    /// <summary>This unit's formation slot offset from the route, km.</summary>
    public Vector2 RouteOffset { get; set; }
}

/// <summary>A drawn route, shared by every unit that follows it. Drawn once per route.</summary>
public sealed class PlotRoute
{
    public int Id { get; set; }
    public List<Vector2> Points { get; } = new();
}

/// <summary>A zone, patrol station or bullseye.</summary>
public sealed class PlotZone
{
    public ZoneKind Kind { get; set; }
    public Affiliation Affiliation { get; set; }
    public string Label { get; set; } = "";

    /// <summary>Exclusion: area filled at 3.5 % (closed polygon, km).</summary>
    public List<Vector2> Fill { get; } = new();

    /// <summary>Exclusion: the dashed boundary (open polyline, km). Often only the edge that faces you.</summary>
    public List<Vector2> Boundary { get; } = new();

    /// <summary>Exclusion: where the label sits, km. The renderer keeps it on screen vertically.</summary>
    public Vector2 LabelAnchor { get; set; }

    /// <summary>Patrol and bullseye centre, km.</summary>
    public Vector2 Center { get; set; }

    /// <summary>Patrol radius or bullseye rings, km.</summary>
    public float[] RadiiKm { get; set; } = [];
}

/// <summary>A short-lived mark at an event position.</summary>
public readonly record struct PlotEvent(PlotEventKind Kind, float X, float Y, double Time);

/// <summary>
/// Everything the plot draws, independent of the wire protocol. A protocol adapter fills it;
/// the renderer only reads it.
/// </summary>
public sealed class PlotScene
{
    public List<PlotTrack> Tracks { get; } = new();
    public List<PlotOrder> Orders { get; } = new();
    public List<PlotRoute> Routes { get; } = new();
    public List<PlotZone> Zones { get; } = new();

    /// <summary>Events, oldest first. Marks fade out after a few seconds of <see cref="Time"/>.</summary>
    public List<PlotEvent> Events { get; } = new();

    /// <summary>Scene time in seconds, used to age events.</summary>
    public double Time { get; set; }

    /// <summary>The scene's suggested view (for example the scenario start). The renderer takes the view as an argument.</summary>
    public Viewport View { get; set; } = new(0, 0, 3, 1280, 720);

    /// <summary>Track under the pointer, 0 = none (interaction seam).</summary>
    public int HoverTrackId { get; set; }

    public PlotTrack? Find(int id)
    {
        var list = Tracks;
        for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return list[i];
        return null;
    }
}
