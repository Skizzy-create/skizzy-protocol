namespace Skizzy.Map.Render.Model;

/// <summary>A weapon or sensor range ring drawn around a ship.</summary>
public readonly record struct RangeRing(float RadiusKm, string Label, bool IsRadar = false);

/// <summary>
/// One track on the plot. Mutable on purpose: a protocol adapter updates tracks in place every
/// tick, so the renderer never sees a new object for an existing track.
/// Coordinates are a flat local plane in km: +X east, +Y north.
/// </summary>
public sealed class PlotTrack
{
    public PlotTrack(int id, TrackKind kind)
    {
        Id = id;
        Kind = kind;
        var k = KindInfo.Of(kind);
        Domain = k.Domain;
        Rcs = k.Rcs;
    }

    public int Id { get; }
    public TrackKind Kind { get; set; }
    public TrackDomain Domain { get; set; }
    public Side Side { get; set; }
    public Affiliation Affiliation { get; set; }

    /// <summary>Radar cross-section in m². Defaults to the class value.</summary>
    public float Rcs { get; set; }

    public float X { get; set; }
    public float Y { get; set; }

    /// <summary>Metres above sea level; negative is depth.</summary>
    public float AltitudeM { get; set; }

    /// <summary>Compass heading in degrees, clockwise from north.</summary>
    public float HeadingDeg { get; set; }

    public float SpeedMps { get; set; }

    /// <summary>The name shown in the label (track id or unit name).</summary>
    public string Name { get; set; } = "";

    public TrailBuffer Trail { get; } = new();

    public bool Selected { get; set; }
    public ControlState Control { get; set; }

    /// <summary>Per-item size override (the <c>[</c>/<c>]</c> keys), 1 = none.</summary>
    public float SizeMultiplier { get; set; } = 1f;

    /// <summary>Hull/airframe health 0..1, or negative when unknown or not applicable.</summary>
    public float Health { get; set; } = -1f;

    /// <summary>Missile target, for the optional target lines; 0 = none.</summary>
    public int TargetId { get; set; }

    /// <summary>A missile (not the viewer's own) whose target is friendly to the viewing side: drawn with a red inbound ring.</summary>
    public bool InboundToFriend { get; set; }

    /// <summary>Range rings for own ships. Null = none.</summary>
    public RangeRing[]? Rings { get; set; }

    /// <summary>False hides the track (dead, or filtered out). Filters are the only thing that removes marks.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>How the viewing side knows this track. <see cref="TrackDetection.None"/> (an undetected stealth contact) is not drawn.</summary>
    public TrackDetection Detection { get; set; }

    /// <summary>
    /// VIP (design/23 §6.13): four accent corner brackets around the symbol, a faint dashed protection ring of
    /// <see cref="VipRadiusKm"/>, and a "VIP" tag while the track is selected.
    /// </summary>
    public bool Vip { get; set; }

    /// <summary>VIP protection radius, km (threats inside it get priority). Default 40, as in the mockup.</summary>
    public float VipRadiusKm { get; set; } = 40f;

    public bool IsMissile => KindInfo.Of(Kind).IsMissile;

    /// <summary>True when the track is drawn: visible (not filtered or dead) and detected.</summary>
    public bool IsDrawn => Visible && Detection != TrackDetection.None;
}
