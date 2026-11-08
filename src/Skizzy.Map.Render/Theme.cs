using SkiaSharp;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render;

/// <summary>
/// Colour tokens, mirroring design/23-visual-design.md §2 (source of truth: the <c>:root</c> block of
/// design/mockups/command-screen-v1.html). One dark theme; a daylight theme must redefine these, not invert them.
/// </summary>
public sealed class Theme
{
    public static Theme Default { get; } = new();

    // surfaces
    public SKColor Ink0 { get; init; } = SKColor.Parse("#071013");
    public SKColor Ink1 { get; init; } = SKColor.Parse("#0c171b");
    public SKColor Ink3 { get; init; } = SKColor.Parse("#162a31");
    public SKColor Line { get; init; } = SKColor.Parse("#1d333b");
    public SKColor LineHi { get; init; } = SKColor.Parse("#2a4a55");
    public SKColor Sea { get; init; } = SKColor.Parse("#081519");
    public SKColor Grid { get; init; } = SKColor.Parse("#10242b");
    public SKColor GridHi { get; init; } = SKColor.Parse("#173540");

    // text
    public SKColor Text { get; init; } = SKColor.Parse("#d3e0e4");
    public SKColor TextDim { get; init; } = SKColor.Parse("#7f979f");
    public SKColor TextFaint { get; init; } = SKColor.Parse("#4f666e");

    // affiliation (MIL-STD-2525 hues; never used for chrome)
    public SKColor Friend { get; init; } = SKColor.Parse("#62c6f2");
    public SKColor Hostile { get; init; } = SKColor.Parse("#ff6a57");
    public SKColor Unknown { get; init; } = SKColor.Parse("#e9c84f");
    public SKColor Neutral { get; init; } = SKColor.Parse("#7cd68e");

    // operator accent (never used for tracks)
    public SKColor Accent { get; init; } = SKColor.Parse("#b5a3ff");

    // state
    public SKColor Ok { get; init; } = SKColor.Parse("#59d09a");
    public SKColor Warn { get; init; } = SKColor.Parse("#f0a43a");
    public SKColor Crit { get; init; } = SKColor.Parse("#ff4b4b");

    // altitude-stalk bands (mockup altCol(); not tokens in doc 23 but part of the approved look)
    public SKColor AltSubsurface { get; init; } = SKColor.Parse("#3c7fb8");
    public SKColor AltLow { get; init; } = SKColor.Parse("#9fb7bf");
    public SKColor AltMid { get; init; } = SKColor.Parse("#c9d8dd");
    public SKColor AltHigh { get; init; } = SKColor.Parse("#ffffff");

    public SKColor ForAffiliation(Affiliation a) => a switch
    {
        Affiliation.Friend => Friend,
        Affiliation.Hostile => Hostile,
        Affiliation.Unknown => Unknown,
        _ => Neutral,
    };

    /// <summary>Altitude band: 0 subsurface, 1 sea-skimming (&lt;150 m, amber), 2 low, 3 mid, 4 high (≥7 km).</summary>
    public static int AltitudeBand(float altM) =>
        altM < 0 ? 0 : altM < 150 ? 1 : altM < 3000 ? 2 : altM < 7000 ? 3 : 4;

    public SKColor ForAltitudeBand(int band) => band switch
    {
        0 => AltSubsurface,
        1 => Warn,
        2 => AltLow,
        3 => AltMid,
        _ => AltHigh,
    };

    /// <summary>The mockup's <c>rgba(hex, a)</c>.</summary>
    public static SKColor A(SKColor c, float alpha) => c.WithAlpha((byte)Math.Clamp(MathF.Round(alpha * 255f), 0, 255));
}
