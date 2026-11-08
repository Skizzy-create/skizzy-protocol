using Avalonia.Controls;
using Skizzy.Map.App.Plot;
using Skizzy.Map.Render;

namespace Skizzy.Map.App;

/// <summary>Spike: the plot control alone in a window.</summary>
public sealed class SpikeWindow : Window
{
    public SpikeWindow()
    {
        Width = 1280; Height = 800; Title = "skizzy-map spike";
        Surface = new PlotSurface { Renderer = new PlotRenderer() };
        Content = new PlotControl { Surface = Surface };
    }

    public PlotSurface Surface { get; }
}
