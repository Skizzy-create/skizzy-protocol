using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using SkiaSharp;
using RenderTheme = Skizzy.Map.Render.Theme;

namespace Skizzy.Map.App.Theme;

/// <summary>
/// The design tokens of design/mockups/command-screen-v1.html (the CSS custom properties), in a dark set (the
/// mockup's own values) and a light set designed for daylight use (not an inversion: a pale slate ground, deeper
/// affiliation hues that keep their contrast on white, a dark top band for altitude).
/// Exposed as Avalonia resources (Color and Brush per token, plus the colour mixes the CSS computes with
/// color-mix()) and as a render-core Theme for the plot.
/// </summary>
public sealed class Palette
{
    public required bool Dark { get; init; }
    public required Color Ink0, Ink1, Ink2, Ink3, Line, LineHi, Sea, Grid, GridHi;
    public required Color Text, TextDim, TextFaint;
    public required Color Friend, Hostile, Unknown, Neutral, Accent;
    public required Color Ok, Warn, Crit;
    public required Color AltSub, AltLow, AltMid, AltHigh;

    public static readonly Palette DarkSet = new()
    {
        Dark = true,
        Ink0 = C("#071013"), Ink1 = C("#0c171b"), Ink2 = C("#28122228"), Ink3 = C("#162a31"),
        Line = C("#1d333b"), LineHi = C("#2a4a55"), Sea = C("#081519"), Grid = C("#10242b"), GridHi = C("#173540"),
        Text = C("#d3e0e4"), TextDim = C("#7f979f"), TextFaint = C("#4f666e"),
        Friend = C("#62c6f2"), Hostile = C("#ff6a57"), Unknown = C("#e9c84f"), Neutral = C("#7cd68e"), Accent = C("#b5a3ff"),
        Ok = C("#59d09a"), Warn = C("#f0a43a"), Crit = C("#ff4b4b"),
        AltSub = C("#3c7fb8"), AltLow = C("#9fb7bf"), AltMid = C("#c9d8dd"), AltHigh = C("#ffffff"),
    };

    public static readonly Palette LightSet = new()
    {
        Dark = false,
        Ink0 = C("#dfe6e9"), Ink1 = C("#eef2f4"), Ink2 = C("#1a2a4a55"), Ink3 = C("#e2e9ec"),
        Line = C("#c9d5da"), LineHi = C("#a9bcc4"), Sea = C("#f8fafa"), Grid = C("#e3ebee"), GridHi = C("#cfdde2"),
        Text = C("#10232a"), TextDim = C("#4d646c"), TextFaint = C("#8197a0"),
        Friend = C("#0f7fbf"), Hostile = C("#d43d2a"), Unknown = C("#b08a00"), Neutral = C("#2f8f4a"), Accent = C("#6b4fd6"),
        Ok = C("#1f9d63"), Warn = C("#c77700"), Crit = C("#d42a2a"),
        AltSub = C("#1f5f99"), AltLow = C("#6f8790"), AltMid = C("#3d5560"), AltHigh = C("#0d1f25"),
    };

    private static Color C(string hex) => Color.Parse(hex);

    /// <summary>CSS color-mix(in srgb, a p%, b): p of a over b.</summary>
    public static Color Mix(Color a, Color b, double p) => Color.FromArgb(
        (byte)Math.Round(a.A * p + b.A * (1 - p)),
        (byte)Math.Round(a.R * p + b.R * (1 - p)),
        (byte)Math.Round(a.G * p + b.G * (1 - p)),
        (byte)Math.Round(a.B * p + b.B * (1 - p)));

    /// <summary>color-mix(in srgb, a p%, transparent).</summary>
    public static Color Alpha(Color a, double p) => Color.FromArgb((byte)Math.Round(a.A * p), a.R, a.G, a.B);

    public ResourceDictionary ToResources()
    {
        var d = new ResourceDictionary();
        void Put(string name, Color c)
        {
            d[name + "Color"] = c;
            d[name + "Brush"] = new SolidColorBrush(c);
        }
        Put("Ink0", Ink0); Put("Ink1", Ink1); Put("Ink2", Ink2); Put("Ink3", Ink3);
        Put("Line", Line); Put("LineHi", LineHi); Put("Sea", Sea); Put("Grid", Grid); Put("GridHi", GridHi);
        Put("Text", Text); Put("TextDim", TextDim); Put("TextFaint", TextFaint);
        Put("Friend", Friend); Put("Hostile", Hostile); Put("Unknown", Unknown); Put("Neutral", Neutral); Put("Accent", Accent);
        Put("Ok", Ok); Put("Warn", Warn); Put("Crit", Crit);

        // the CSS mixes
        Put("LineSoft", Mix(Line, Ink1, .55));                 // td border: color-mix(line 55%, transparent) over the panel
        Put("LineSofter", Mix(Line, Ink1, .5));
        Put("BtnPressed", Mix(Accent, Ink3, .16));               // .btn[aria-pressed]
        Put("BtnPressedBorder", Mix(Accent, Ink1, .55));
        Put("AccBorder", Mix(Accent, Ink1, .60));                // .btn.acc border
        Put("RowSelected", Mix(Accent, Ink1, .12));              // tr.sel
        Put("WarFill", Mix(Hostile, Ink0, .09));                 // .pill.war
        Put("WarBorder", Mix(Hostile, Ink0, .50));
        Put("SeatBorder", Mix(Accent, Ink0, .45));
        Put("ManualFill", Mix(Accent, Ink1, .10));               // .cstate.manual
        Put("ManualBorder", Mix(Accent, Ink1, .55));
        Put("AgentBorder", Mix(Friend, Ink1, .45));
        Put("Overlay", Alpha(Ink1, .88));                        // .mtool / .tlbox
        Put("OverlayStrong", Alpha(Ink1, .94));                  // .tip
        Put("Legend", Alpha(Ink1, .90));
        Put("BannerFill", Mix(Crit, Ink1, .14));                 // .banner
        Put("BannerBorder", Mix(Crit, Ink1, .55));
        Put("ToastFill", Mix(Accent, Ink1, .14));                // .toast
        Put("ToastBorder", Mix(Accent, Ink1, .50));
        Put("UnvBorder", Mix(Warn, Ink1, .50));                  // .unv
        Put("Scrim", Color.FromArgb(Dark ? (byte)150 : (byte)90, 2, 8, 10));

        // Dock.Avalonia theme tokens, so the docking chrome is the mockup's panels rather than Fluent's
        d["DockThemeBackgroundBrush"] = new SolidColorBrush(Ink0);
        d["DockThemeForegroundBrush"] = new SolidColorBrush(Text);
        d["DockThemeControlBackgroundBrush"] = new SolidColorBrush(Ink1);
        d["DockThemeBorderLowBrush"] = new SolidColorBrush(Line);
        d["DockThemeAccentBrush"] = new SolidColorBrush(Accent);
        d["DockApplicationAccentBrushHigh"] = new SolidColorBrush(Accent);
        d["DockApplicationAccentBrushMed"] = new SolidColorBrush(Mix(Accent, Ink1, .6));
        d["DockApplicationAccentBrushLow"] = new SolidColorBrush(Mix(Accent, Ink1, .25));
        d["DockApplicationAccentBrushIndicator"] = new SolidColorBrush(Accent);
        d["DockApplicationAccentForegroundBrush"] = new SolidColorBrush(Text);
        d["DockSurfaceWorkbenchBrush"] = new SolidColorBrush(Ink0);
        d["DockSurfaceEditorBrush"] = new SolidColorBrush(Sea);
        d["DockSurfacePanelBrush"] = new SolidColorBrush(Ink1);
        d["DockSurfaceSidebarBrush"] = new SolidColorBrush(Ink1);
        d["DockSurfaceHeaderBrush"] = new SolidColorBrush(Ink1);
        d["DockSurfaceHeaderActiveBrush"] = new SolidColorBrush(Ink1);
        d["DockBorderSubtleBrush"] = new SolidColorBrush(Line);
        d["DockBorderStrongBrush"] = new SolidColorBrush(LineHi);
        d["DockSeparatorBrush"] = new SolidColorBrush(Line);
        d["DockTabBackgroundBrush"] = Brushes.Transparent;
        d["DockTabHoverBackgroundBrush"] = new SolidColorBrush(Ink2);
        d["DockTabActiveBackgroundBrush"] = Brushes.Transparent;
        d["DockTabForegroundBrush"] = new SolidColorBrush(TextFaint);
        d["DockTabActiveForegroundBrush"] = new SolidColorBrush(Text);
        d["DockTabSelectedForegroundBrush"] = new SolidColorBrush(Text);
        d["DockTabActiveIndicatorBrush"] = new SolidColorBrush(Accent);
        d["DockTabCloseHoverBackgroundBrush"] = new SolidColorBrush(Ink3);
        d["DockDocumentTabStripBackgroundBrush"] = new SolidColorBrush(Ink0);
        d["DockDocumentTabSelectedForegroundBrush"] = new SolidColorBrush(Text);
        d["DockDocumentTabPointerOverForegroundBrush"] = new SolidColorBrush(Text);
        d["DockDocumentContentBorderBrush"] = new SolidColorBrush(Line);
        d["DockSplitterIdleBrush"] = Brushes.Transparent;
        d["DockSplitterHoverBrush"] = new SolidColorBrush(Accent);
        d["DockSplitterDragBrush"] = new SolidColorBrush(Accent);
        d["DockTargetIndicatorBrush"] = new SolidColorBrush(Alpha(Accent, .35));
        d["DockChromeButtonForegroundBrush"] = new SolidColorBrush(TextFaint);
        d["DockChromeButtonHoverBackgroundBrush"] = new SolidColorBrush(Ink3);
        d["DockChromeButtonPressedBackgroundBrush"] = new SolidColorBrush(LineHi);
        d["DockChromeGripBrush"] = new SolidColorBrush(Line);
        d["DockToolChromeIconBrush"] = new SolidColorBrush(TextFaint);
        d["DockWindowChromeBackgroundBrush"] = new SolidColorBrush(Ink0);
        d["DockWindowChromeBorderBrush"] = new SolidColorBrush(Line);
        d["DockWindowChromeForegroundBrush"] = new SolidColorBrush(TextDim);
        d["DockWindowChromeTitleBarBackgroundBrush"] = new SolidColorBrush(Ink1);
        d["DockSelectorOverlayBackdropBrush"] = new SolidColorBrush(Alpha(Ink0, .6));

        // Fluent base colours the few stock controls fall back on (menus, tooltips, text boxes)
        d["SystemAccentColor"] = Accent;
        d["SystemControlHighlightAccentBrush"] = new SolidColorBrush(Accent);
        d["ToolTipBackground"] = new SolidColorBrush(Ink1);
        d["ToolTipForeground"] = new SolidColorBrush(Text);
        d["ToolTipBorderBrush"] = new SolidColorBrush(LineHi);
        d["MenuFlyoutPresenterBackground"] = new SolidColorBrush(Ink1);
        d["MenuFlyoutPresenterBorderBrush"] = new SolidColorBrush(LineHi);
        d["MenuFlyoutItemBackgroundPointerOver"] = new SolidColorBrush(Ink3);
        d["MenuFlyoutItemForeground"] = new SolidColorBrush(Text);
        d["MenuFlyoutItemForegroundPointerOver"] = new SolidColorBrush(Text);
        d["MenuFlyoutItemForegroundDisabled"] = new SolidColorBrush(TextFaint);
        d["MenuFlyoutSubItemChevron"] = new SolidColorBrush(TextDim);
        d["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = new SolidColorBrush(TextFaint);
        d["MenuFlyoutSeparatorBackground"] = new SolidColorBrush(Line);
        d["ComboBoxDropDownBackground"] = new SolidColorBrush(Ink1);
        d["ComboBoxDropDownBorderBrush"] = new SolidColorBrush(LineHi);
        d["ComboBoxItemBackgroundPointerOver"] = new SolidColorBrush(Ink3);
        d["ComboBoxItemBackgroundSelected"] = new SolidColorBrush(Mix(Accent, Ink1, .16));
        d["ComboBoxItemBackgroundSelectedPointerOver"] = new SolidColorBrush(Mix(Accent, Ink1, .22));
        d["ComboBoxItemForegroundSelected"] = new SolidColorBrush(Text);
        d["ComboBoxItemForegroundPointerOver"] = new SolidColorBrush(Text);
        d["TextControlSelectionHighlightColor"] = new SolidColorBrush(Mix(Accent, Ink1, .45));
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed" })
            d[k] = new SolidColorBrush(Ink3);
        d["ComboBoxBackgroundDisabled"] = new SolidColorBrush(Ink1);
        foreach (var k in new[] { "TextControlBorderBrush", "ComboBoxBorderBrush" }) d[k] = new SolidColorBrush(LineHi);
        foreach (var k in new[] { "TextControlBorderBrushPointerOver", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed" }) d[k] = new SolidColorBrush(TextFaint);
        d["TextControlBorderBrushFocused"] = new SolidColorBrush(Accent);
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "ComboBoxForeground", "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxDropDownGlyphForeground" })
            d[k] = new SolidColorBrush(Text);
        d["TextControlPlaceholderForeground"] = new SolidColorBrush(TextFaint);
        d["TextControlPlaceholderForegroundPointerOver"] = new SolidColorBrush(TextFaint);
        d["TextControlPlaceholderForegroundFocused"] = new SolidColorBrush(TextFaint);
        foreach (var k in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
            d[k] = new SolidColorBrush(Accent);
        foreach (var k in new[] { "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed" }) d[k] = new SolidColorBrush(Ink3);
        d["ScrollBarThumbFill"] = new SolidColorBrush(LineHi);
        d["ScrollBarThumbFillPointerOver"] = new SolidColorBrush(TextFaint);
        d["ScrollBarThumbFillPressed"] = new SolidColorBrush(TextDim);
        d["ScrollBarPanningThumbBackground"] = new SolidColorBrush(LineHi);
        d["ScrollBarTrackFill"] = Brushes.Transparent;
        d["ScrollBarTrackFillPointerOver"] = new SolidColorBrush(Alpha(Ink3, .6));
        d["ScrollBarButtonArrowForeground"] = new SolidColorBrush(TextFaint);
        return d;
    }

    /// <summary>The plot's colours for the render core.</summary>
    public RenderTheme ToRenderTheme() => new()
    {
        Ink0 = S(Ink0), Ink1 = S(Ink1), Ink3 = S(Ink3), Line = S(Line), LineHi = S(LineHi), Sea = S(Sea), Grid = S(Grid), GridHi = S(GridHi),
        Text = S(Text), TextDim = S(TextDim), TextFaint = S(TextFaint),
        Friend = S(Friend), Hostile = S(Hostile), Unknown = S(Unknown), Neutral = S(Neutral), Accent = S(Accent),
        Ok = S(Ok), Warn = S(Warn), Crit = S(Crit),
        AltSubsurface = S(AltSub), AltLow = S(AltLow), AltMid = S(AltMid), AltHigh = S(AltHigh),
    };

    private static SKColor S(Color c) => new(c.R, c.G, c.B, c.A);

    public static Palette For(ThemeVariant? v) => v == ThemeVariant.Light ? LightSet : DarkSet;
}
