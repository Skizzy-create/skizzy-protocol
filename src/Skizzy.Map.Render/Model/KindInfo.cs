namespace Skizzy.Map.Render.Model;

/// <summary>Static per-class facts, ported from the mockup's <c>K</c> table.</summary>
public readonly record struct KindInfo(TrackDomain Domain, float Rcs, string Glyph, string Code, string ClassName, bool IsMissile)
{
    private static readonly KindInfo[] Table = Build();

    public static KindInfo Of(TrackKind kind) => Table[(int)kind];

    private static KindInfo[] Build()
    {
        var t = new KindInfo[Enum.GetValues<TrackKind>().Length];
        t[(int)TrackKind.Unknown] = new(TrackDomain.Air, 1f, "", "UNK", "Unknown", false);
        t[(int)TrackKind.Carrier] = new(TrackDomain.Surface, 40000f, "CV", "CVN", "Carrier", false);
        t[(int)TrackKind.Destroyer] = new(TrackDomain.Surface, 9000f, "DD", "DDG", "Destroyer", false);
        t[(int)TrackKind.Submarine] = new(TrackDomain.Subsurface, 300f, "SS", "SSN", "Attack submarine", false);
        t[(int)TrackKind.EarlyWarning] = new(TrackDomain.Air, 60f, "AEW", "AEW", "Early warning aircraft", false);
        t[(int)TrackKind.Fighter] = new(TrackDomain.Air, 4f, "F", "FTR", "Fighter", false);
        t[(int)TrackKind.Strike] = new(TrackDomain.Air, 25f, "A", "STK", "Strike aircraft", false);
        t[(int)TrackKind.Uav] = new(TrackDomain.Air, 0.15f, "U", "UAV", "Attack drone", false);
        t[(int)TrackKind.Ucav] = new(TrackDomain.Air, 0.3f, "U", "UCAV", "Armed drone", false);
        t[(int)TrackKind.AntiShipMissile] = new(TrackDomain.Air, 0.05f, "", "ASM", "Anti-ship missile", true);
        t[(int)TrackKind.SurfaceToAirMissile] = new(TrackDomain.Air, 0.08f, "", "SAM", "Surface-to-air missile", true);
        t[(int)TrackKind.AirToAirMissile] = new(TrackDomain.Air, 0.03f, "", "AAM", "Air-to-air missile", true);
        return t;
    }
}
