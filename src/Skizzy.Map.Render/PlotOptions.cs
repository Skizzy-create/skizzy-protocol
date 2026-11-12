using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render;

/// <summary>Per-map-window layer toggles and sizing, mirroring the mockup's plot toolbar. Defaults match the mockup.</summary>
public sealed class PlotOptions
{
    public bool Trails { get; set; } = true;
    public bool AltitudeStalks { get; set; } = true;
    public bool TargetLines { get; set; }
    public bool Rings { get; set; } = true;
    public bool Labels { get; set; } = true;

    /// <summary>Optional Counts view: hostile missiles and drones grouped into "MSL ×N" pills. Off by default (doc 11 §2.1 rule 1).</summary>
    public bool Counts { get; set; }

    public bool Grid { get; set; } = true;
    public bool Zones { get; set; } = true;
    public bool Orders { get; set; } = true;
    public bool Events { get; set; } = true;
    public bool ScaleBar { get; set; } = true;

    public SizeMode SizeMode { get; set; } = SizeMode.Auto;

    /// <summary>Global symbol scale (the toolbar slider, 0.6–1.8).</summary>
    public float SizeScale { get; set; } = 1f;

    /// <summary>Missile labels appear only when selected or zoomed past this scale (px/km). Mockup: 9.</summary>
    public float MissileLabelMinScale { get; set; } = 9f;

    /// <summary>Missiles on the plot (the toolbar's Missiles filter). Off hides every missile, its trail and its inbound ring.</summary>
    public bool Missiles { get; set; } = true;

    /// <summary>Missile trails (the toolbar's Missile trails toggle); the other tracks' trails follow <see cref="Trails"/>.</summary>
    public bool MissileTrails { get; set; } = true;

    /// <summary>
    /// Smallest missile symbol in px (M2): a missile never shrinks below this, whatever the zoom or the salvo spacing, so a
    /// raid stays a set of headed arrowheads when the view spans 30+ km. 0 restores the mockup rule (dots below 5 px).
    /// </summary>
    public float MissileMinPx { get; set; } = 6f;
}
