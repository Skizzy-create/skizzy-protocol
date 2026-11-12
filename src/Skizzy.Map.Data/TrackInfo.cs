using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Data;

/// <summary>Which picture a map window shows: one side's fog of war, or the truth.</summary>
public enum ViewSide { Blue, Red, God }

public readonly record struct MagazineInfo(string Name, int Left, int Max);

/// <summary>Unit facts for the Unit panel. Every field is optional: sources fill what they know.</summary>
public sealed class UnitDetails
{
    public List<MagazineInfo> Magazines { get; } = new();
    public int? Aam { get; set; }
    public int? AamMax { get; set; }
    /// <summary>0..1.</summary>
    public float? Fuel { get; set; }
    /// <summary>Hull damage 0..1 (ships).</summary>
    public float? Damage { get; set; }
    public string? Doctrine { get; set; }
    public string? Emcon { get; set; }
    public string? OnContact { get; set; }
    /// <summary>Mission or task text shown beside the control state.</summary>
    public string? Mission { get; set; }
    /// <summary>Weapon engagement line for missiles ("Target X · time to go 12 s").</summary>
    public string? TargetName { get; set; }
    public double? TimeToGo { get; set; }
    /// <summary>Data-link holder for link-guided missiles; LinkLost with a countdown when broken.</summary>
    public string? LinkHolder { get; set; }
    public double? LinkSelfDestructIn { get; set; }
    public int DeckCount { get; set; }
    public int DeckReady { get; set; }
    public string? DeckNames { get; set; }
    /// <summary>Source-specific extra rows (live host fields such as situation and body).</summary>
    public List<KeyValuePair<string, string>> Extra { get; } = new();
}

/// <summary>
/// One track in the map's mirror of the world. Written by the data source on the UI thread; map windows copy
/// what they need into their own render scene each frame. Positions in km on the plot plane (+X east, +Y north).
/// </summary>
public sealed class TrackInfo
{
    private double _ex, _ey, _eT = double.NegativeInfinity;

    public TrackInfo(int id, string key, int historyCapacity)
    {
        Id = id;
        Key = key;
        History = new HistoryRing(historyCapacity);
    }

    public int Id { get; }
    /// <summary>Source identity (vessel handle, sim id). Stable across reconnects.</summary>
    public string Key { get; }

    public TrackKind Kind { get; set; }
    public TrackDomain Domain { get; set; }
    public Side Side { get; set; }
    /// <summary>BDArmory team on the live tactical stream (null elsewhere).</summary>
    public string? Team { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Class name ("Destroyer") and short code ("DDG").</summary>
    public string ClassName { get; set; } = "";
    public string Code { get; set; } = "";
    public string Group { get; set; } = "";
    public ControlState Control { get; set; }
    public string ControlText { get; set; } = "";
    public float Rcs { get; set; }
    /// <summary>0..1 or -1 when unknown.</summary>
    public float Health { get; set; } = -1f;
    public bool Vip { get; set; }
    public float VipRadiusKm { get; set; } = 40f;
    public RangeRing[]? Rings { get; set; }
    public int TargetId { get; set; }
    public bool IsMissile => KindInfo.Of(Kind).IsMissile;

    public bool Alive { get; set; } = true;
    public double BornT { get; set; }
    public double DiedT { get; set; } = double.PositiveInfinity;

    /// <summary>Per viewing side: how that side's picture shows this track.</summary>
    public Affiliation AffBlue { get; set; } = Affiliation.Unknown;
    public Affiliation AffRed { get; set; } = Affiliation.Unknown;
    public TrackDetection DetBlue { get; set; }
    public TrackDetection DetRed { get; set; }

    public UnitDetails Details { get; } = new();
    public HistoryRing History { get; }

    // last sample
    public double SampleT { get; private set; } = double.NaN;
    public double X { get; private set; }
    public double Y { get; private set; }
    public float AltitudeM { get; private set; }
    public float HeadingDeg { get; private set; }
    public float SpeedMps { get; private set; }
    /// <summary>km/s.</summary>
    public double Vx { get; private set; }
    public double Vy { get; private set; }

    public bool ExistsAt(double t) => t >= BornT - 1e-6 && t < DiedT;

    public Affiliation AffiliationFor(ViewSide v) => v switch
    {
        ViewSide.Blue => AffBlue,
        ViewSide.Red => AffRed,
        _ => Side switch { Side.Blue => Affiliation.Friend, Side.Red => Affiliation.Hostile, Side.Green => Affiliation.Neutral, _ => Affiliation.Unknown },
    };

    public TrackDetection DetectionFor(ViewSide v) => v switch
    {
        ViewSide.Blue => DetBlue,
        ViewSide.Red => DetRed,
        _ => TrackDetection.Shared,
    };

    /// <summary>
    /// Takes a new kinematic sample. When a track is already moving on screen, the jump between where it was
    /// being drawn and where the new sample puts it is blended away over <paramref name="blendSeconds"/>
    /// (error-decay smoothing), unless it is a teleport (more than <paramref name="snapKm"/>).
    /// </summary>
    public void SetSample(double t, double x, double y, float altM, float hdgDeg, float spdMps, double vx, double vy,
        double displayNow, double blendSeconds = 0.25, double snapKm = 2.0)
    {
        if (!double.IsNaN(SampleT) && blendSeconds > 0)
        {
            var (ox, oy) = PositionAt(displayNow, blendSeconds);
            double nx = x + vx * Math.Clamp(displayNow - t, 0, MaxExtrapolation);
            double ny = y + vy * Math.Clamp(displayNow - t, 0, MaxExtrapolation);
            double ex = ox - nx, ey = oy - ny;
            if (ex * ex + ey * ey < snapKm * snapKm) { _ex = ex; _ey = ey; _eT = displayNow; }
            else { _ex = _ey = 0; _eT = double.NegativeInfinity; }
        }
        SampleT = t; X = x; Y = y; AltitudeM = altM; HeadingDeg = hdgDeg; SpeedMps = spdMps; Vx = vx; Vy = vy;
    }

    /// <summary>Seconds a position may be extrapolated past the last sample before the track freezes.</summary>
    public const double MaxExtrapolation = 3.0;

    /// <summary>Live display position: last sample, extrapolated along its velocity, plus the decaying blend error.</summary>
    public (double X, double Y) PositionAt(double now, double blendSeconds = 0.25)
    {
        if (double.IsNaN(SampleT)) return (X, Y);
        double dt = Math.Clamp(now - SampleT, 0, MaxExtrapolation);
        if (!Alive) dt = Math.Clamp(Math.Min(now, DiedT) - SampleT, 0, MaxExtrapolation);
        double x = X + Vx * dt, y = Y + Vy * dt;
        if (blendSeconds > 0 && now > _eT && _eT > double.NegativeInfinity)
        {
            double k = Math.Exp(-(now - _eT) / blendSeconds);
            if (k > 1e-3) { x += _ex * k; y += _ey * k; }
        }
        else if (now <= _eT) { x += _ex; y += _ey; }
        return (x, y);
    }

    /// <summary>Records the current sample into the history ring when it falls in a new recording interval.</summary>
    public void Record(double interval)
    {
        if (double.IsNaN(SampleT)) return;
        if (History.Count == 0 || Math.Floor(SampleT / interval + 1e-9) > Math.Floor(History.LastT / interval + 1e-9))
            History.Add(SampleT, X, Y, AltitudeM, HeadingDeg, SpeedMps);
    }

    /// <summary>Records the current sample unconditionally (birth and death keep their exact moment).</summary>
    public void RecordNow()
    {
        if (!double.IsNaN(SampleT)) History.Add(SampleT, X, Y, AltitudeM, HeadingDeg, SpeedMps);
    }
}
