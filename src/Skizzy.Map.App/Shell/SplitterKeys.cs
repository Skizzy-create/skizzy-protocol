using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Core;

namespace Skizzy.Map.App.Shell;

/// <summary>
/// The mockup's splitter keys: a splitter takes focus when clicked or tabbed to, and each arrow key moves it 20 px
/// (Left/Up shrink the panel before it, Right/Down grow it). Dock's splitters only drag, so this adds the keys to
/// every <see cref="ProportionalStackPanelSplitter"/>.
/// </summary>
public static class SplitterKeys
{
    public const double StepPx = 20;

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputElement.FocusableProperty.OverrideDefaultValue<ProportionalStackPanelSplitter>(true);
        InputElement.KeyDownEvent.AddClassHandler<ProportionalStackPanelSplitter>(OnKeyDown);
        InputElement.PointerPressedEvent.AddClassHandler<ProportionalStackPanelSplitter>((s, _) => s.Focus(), handledEventsToo: true);
    }

    private static void OnKeyDown(ProportionalStackPanelSplitter s, KeyEventArgs e)
    {
        double d = e.Key switch { Key.Left or Key.Up => -StepPx, Key.Right or Key.Down => StepPx, _ => 0 };
        if (d == 0) return;
        if (Move(s, d)) e.Handled = true;
    }

    /// <summary>Moves the splitter by <paramref name="px"/> along its panel: the panel before it grows by that much.</summary>
    public static bool Move(ProportionalStackPanelSplitter s, double px)
    {
        // the splitter sits in an item container that is a direct child of the proportional panel
        Control c = s;
        while (c.GetVisualParent() is Control parent && parent is not ProportionalStackPanel) c = parent;
        if (c.GetVisualParent() is not ProportionalStackPanel panel) return false;
        int i = panel.Children.IndexOf(c);
        if (i <= 0 || i >= panel.Children.Count - 1) return false;
        Control prev = panel.Children[i - 1], next = panel.Children[i + 1];
        bool horizontal = panel.Orientation == Avalonia.Layout.Orientation.Horizontal;
        double size = horizontal ? prev.Bounds.Width + next.Bounds.Width : prev.Bounds.Height + next.Bounds.Height;
        double p = ProportionalStackPanel.GetProportion(prev), q = ProportionalStackPanel.GetProportion(next);
        if (size <= 1 || double.IsNaN(p) || double.IsNaN(q)) return false;
        double sum = p + q, dp = px * sum / size, min = sum * 40 / size; // keep 40 px either side
        double np = Math.Clamp(p + dp, min, sum - min);
        if (Math.Abs(np - p) < 1e-9) return true;
        Set(prev, np);
        Set(next, sum - np);
        panel.InvalidateMeasure();
        return true;
    }

    private static void Set(Control c, double proportion)
    {
        ProportionalStackPanel.SetProportion(c, proportion);
        if (c.DataContext is IDockable d) d.Proportion = proportion;
    }
}
