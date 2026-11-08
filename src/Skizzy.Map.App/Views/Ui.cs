using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skizzy.Map.App.Views;

/// <summary>Small builders for the mockup's controls, so views read like the mockup's HTML.</summary>
public static class Ui
{
    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    /// <summary>.lbl: small caps label (the CSS uppercases it; we pass the text through ToUpperInvariant).</summary>
    public static TextBlock Label(string text) => Text(text.ToUpperInvariant(), "lbl");

    public static Button Button(string text, Action onClick, params string[] classes)
    {
        var b = new Button { Content = text };
        b.Classes.Add("btn");
        foreach (var c in classes) b.Classes.Add(c);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static ToggleButton Toggle(string text, bool on, Action<bool> changed, params string[] classes)
    {
        var b = new ToggleButton { Content = text, IsChecked = on };
        foreach (var c in classes) b.Classes.Add(c);
        if (classes.Length == 0) b.Classes.Add("btn");
        b.IsCheckedChanged += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    /// <summary>A chip with a trailing mono count ("Aircraft 412").</summary>
    public static ToggleButton Chip(string text, string? count, bool on, Action onClick, string? key = null)
    {
        var name = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (key is not null)
        {
            var k = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = 10, Margin = new Thickness(0, 0, 1, 0) };
            k.Bind(TextBlock.FontFamilyProperty, k.GetResourceObservable("FontData"));
            k.Bind(TextBlock.ForegroundProperty, k.GetResourceObservable("AccentBrush"));
            panel.Children.Add(k);
        }
        panel.Children.Add(name);
        if (count is not null)
        {
            var c = new TextBlock { Text = count, VerticalAlignment = VerticalAlignment.Center, FontSize = 10.5 };
            c.Bind(TextBlock.FontFamilyProperty, c.GetResourceObservable("FontData"));
            c.Bind(TextBlock.ForegroundProperty, c.GetResourceObservable("TextFaintBrush"));
            panel.Children.Add(c);
        }
        var b = new ToggleButton { Content = panel, IsChecked = on };
        b.Classes.Add("chip");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// A segmented control (.seg). <paramref name="onSelect"/> gets the index; the caller re-syncs IsChecked through
    /// the returned <see cref="Segmented"/>.
    /// </summary>
    public static Segmented Seg(IReadOnlyList<string> options, int selected, Action<int> onSelect, bool uiFont = false)
    {
        var seg = new Segmented();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < options.Count; i++)
        {
            if (i > 0) { var sep = new Border(); sep.Classes.Add("segsep"); row.Children.Add(sep); }
            int idx = i;
            var b = new ToggleButton { Content = options[i], IsChecked = i == selected };
            b.Classes.Add("seg");
            if (uiFont) b.Classes.Add("ui");
            b.Click += (_, _) => { seg.SetSelected(idx); onSelect(idx); };
            seg.Buttons.Add(b);
            row.Children.Add(b);
        }
        seg.Child = row;
        seg.Classes.Add("seg");
        return seg;
    }

    public static StackPanel H(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        p.Children.AddRange(children);
        return p;
    }

    public static StackPanel V(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    public static WrapPanel Wrap(double h, double v, params Control[] children)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = h, LineSpacing = v };
        p.Children.AddRange(children);
        return p;
    }

    public static Border Sep()
    {
        var b = new Border();
        b.Classes.Add("sep");
        return b;
    }

    public static Border Pill(string text, params string[] classes)
    {
        var b = new Border { Child = new TextBlock { Text = text.ToUpperInvariant() } };
        b.Classes.Add("pill");
        foreach (var c in classes) b.Classes.Add(c);
        return b;
    }

    public static IBrush Brush(Control c, string key) =>
        c.TryFindResource(key, c.ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Magenta;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string> BrushKeys = new();

    /// <summary>Binds a TextBlock's foreground to a theme brush (no-op when already bound to that key).</summary>
    public static void BindBrush(TextBlock t, string key)
    {
        if (BrushKeys.TryGetValue(t, out var k) && k == key) return;
        BrushKeys.AddOrUpdate(t, key);
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(key));
    }

    public static void BindFont(TextBlock t, string key) => t.Bind(TextBlock.FontFamilyProperty, t.GetResourceObservable(key));
}

/// <summary>The .seg border with its toggle segments; exactly one is checked.</summary>
public sealed class Segmented : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    public List<ToggleButton> Buttons { get; } = new();

    public int Selected { get; private set; }

    public void SetSelected(int i)
    {
        Selected = i;
        for (int k = 0; k < Buttons.Count; k++) Buttons[k].IsChecked = k == i;
    }
}

/// <summary>A custom-drawn control that repaints when the theme's resources change (it looks brushes up while drawing).</summary>
public abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }
}
