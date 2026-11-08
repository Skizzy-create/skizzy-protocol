using SkiaSharp;

namespace Skizzy.Map.Render.Text;

/// <summary>
/// The three bundled OFL families (design/23-visual-design.md §3), loaded once from embedded resources.
/// Nothing is fetched at runtime. If a resource is missing the face falls back to a system family and
/// <see cref="UsingFallback"/> is set.
/// </summary>
public sealed class FontSet
{
    private static readonly Lazy<FontSet> Shared = new(() => new FontSet());

    public static FontSet Default => Shared.Value;

    private FontSet()
    {
        DisplaySemiBold = Load("SairaSemiCondensed-SemiBold.ttf", "Arial Narrow", SKFontStyleWeight.SemiBold);
        DisplayBold = Load("SairaSemiCondensed-Bold.ttf", "Arial Narrow", SKFontStyleWeight.Bold);
        UiRegular = Load("IBMPlexSansCondensed-Regular.ttf", "Arial Narrow", SKFontStyleWeight.Normal);
        UiSemiBold = Load("IBMPlexSansCondensed-SemiBold.ttf", "Arial Narrow", SKFontStyleWeight.SemiBold);
        UiBold = Load("IBMPlexSansCondensed-Bold.ttf", "Arial Narrow", SKFontStyleWeight.Bold);
        DataRegular = Load("IBMPlexMono-Regular.ttf", "Consolas", SKFontStyleWeight.Normal);
        DataMedium = Load("IBMPlexMono-Medium.ttf", "Consolas", SKFontStyleWeight.Medium);
    }

    public SKTypeface DisplaySemiBold { get; }
    public SKTypeface DisplayBold { get; }
    public SKTypeface UiRegular { get; }
    public SKTypeface UiSemiBold { get; }
    public SKTypeface UiBold { get; }
    public SKTypeface DataRegular { get; }
    public SKTypeface DataMedium { get; }

    /// <summary>True when at least one bundled face could not be loaded and a system face was used.</summary>
    public bool UsingFallback { get; private set; }

    private SKTypeface Load(string file, string fallbackFamily, SKFontStyleWeight weight)
    {
        var asm = typeof(FontSet).Assembly;
        using var stream = asm.GetManifestResourceStream("Skizzy.Map.Render.Fonts." + file);
        if (stream is not null)
        {
            var tf = SKTypeface.FromStream(stream);
            if (tf is not null) return tf;
        }
        UsingFallback = true;
        return SKTypeface.FromFamilyName(fallbackFamily, weight, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
               ?? SKTypeface.Default;
    }

    internal static IEnumerable<string> BundledResourceNames() =>
        typeof(FontSet).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Skizzy.Map.Render.Fonts.", StringComparison.Ordinal));
}
