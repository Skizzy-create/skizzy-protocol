using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Dock.Model.Core;
using Skizzy.Map.App.Panels;
using Skizzy.Map.Data;
using static Skizzy.Map.App.Tests.ControlKit;

namespace Skizzy.Map.App.Tests;

/// <summary>Inventory rows 80–85: the bottom panels (docs/M3-command-parity.md).</summary>
public class BottomPanelControlTests
{
    /// <summary>The dock tab of a panel ("Messages", "Comms", "Losses").</summary>
    private static Control Tab(UiHarness h, string title) =>
        h.Win.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name.EndsWith("TabStripItem", StringComparison.Ordinal) && c.DataContext is IDockable d && string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase));

    private static void ShowTab(UiHarness h, string title)
    {
        h.Click(Tab(h, title));
        h.Pump(3);
    }

    private static IEnumerable<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void Control_BottomTabs_SwitchThePanel()
    {
        using var h = Demo();
        Assert.True(h.Find<MessagesView>()!.IsEffectivelyVisible);
        ShowTab(h, "Comms");
        Assert.True(h.Find<CommsView>() is { IsEffectivelyVisible: true });
        Assert.False(h.Find<MessagesView>() is { IsEffectivelyVisible: true });
        ShowTab(h, "Losses");
        Assert.True(h.Find<LossesView>() is { IsEffectivelyVisible: true });
        ShowTab(h, "Messages");
        Assert.True(h.Find<MessagesView>() is { IsEffectivelyVisible: true });
    }

    [AvaloniaFact]
    public void Control_MessageCategoryChips_ToggleACategoryWithCounts()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.Key(PhysicalKey.R); // an Orders line
        h.Pump(2);
        h.App.RefreshPanels();
        var view = h.Find<MessagesView>()!;
        var chip = Chip(view, "Orders");
        int orders = h.App.Data.Log.CountOf(LogCategory.Orders);
        Assert.True(orders > 0);
        Assert.Equal(orders.ToString(System.Globalization.CultureInfo.InvariantCulture), ((StackPanel)chip.Content!).Children.OfType<TextBlock>().Last().Text);
        Assert.Contains(view.Shown, e => e.Category == LogCategory.Orders);
        h.Click(chip);
        Assert.DoesNotContain(view.Shown, e => e.Category == LogCategory.Orders);
        Assert.False(Chip(h.Find<MessagesView>()!, "Orders").IsChecked);
        h.Click(Chip(h.Find<MessagesView>()!, "Orders"));
        Assert.Contains(view.Shown, e => e.Category == LogCategory.Orders);
    }

    [AvaloniaFact]
    public void Control_LogLineClick_JumpsToTheLineAndScrubsInReplay()
    {
        using var h = Demo();
        h.Select("Hawk 1");
        h.View(-20, 10, 4);
        var e = h.Empty(-60, 10);
        h.RightClick(e.X, e.Y);
        h.App.RefreshPanels();
        h.Pump(2);
        h.View(200, -200, 4);
        var view = h.Find<MessagesView>()!;
        var entry = view.Shown.First(x => x.Text.StartsWith("Hawk 1: move to", StringComparison.Ordinal));
        var item = view.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is LogEntry le && le.Seq == entry.Seq);
        h.Click(item);
        h.Pump(60);
        var m = h.Map().ViewModel;
        Assert.InRange(m.CenterX, (float)e.X - 1, (float)e.X + 1);
        Assert.InRange(m.CenterY, (float)e.Y - 1, (float)e.Y + 1);

        // in replay the click also scrubs to the line's time
        double lineT = entry.T;
        h.Run(120);
        h.App.ScrubTo(h.App.Data.LiveTime - 10);
        h.Pump(2);
        Assert.True(h.App.Data.InReplay);
        var list = h.Find<MessagesView>()!.GetLogicalDescendants().OfType<ListBox>().First();
        list.ScrollIntoView(entry); // the user scrolls back to the line
        h.Pump(2);
        item = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is LogEntry le && le.Seq == entry.Seq);
        h.Click(item);
        Assert.InRange(h.App.Data.ViewTime, lineT - .5, lineT + .5);
    }

    [AvaloniaFact]
    public void Control_CommsChannelChips_FilterByChannel()
    {
        using var h = Demo();
        h.Run(600); // team reports and enemy transmissions
        ShowTab(h, "Comms");
        var view = h.Find<CommsView>()!;
        int team = h.App.Data.Comms.Count(c => c.Channel == "Team");
        Assert.True(team > 0 && team < h.App.Data.Comms.Count, $"{team} of {h.App.Data.Comms.Count} on Team");
        h.Click(Chip(view, "Team"));
        Assert.Equal("Team", h.App.CommsChannel);
        var metas = Texts(h.Find<CommsView>()!).Where(t => t.Contains(" · ", StringComparison.Ordinal) && (t.StartsWith("Team", StringComparison.Ordinal) || t.StartsWith("Diplomatic", StringComparison.Ordinal) || t.StartsWith("Broadcast", StringComparison.Ordinal))).ToList();
        Assert.Equal(Math.Min(team, 300), metas.Count);
        Assert.All(metas, t => Assert.StartsWith("Team · ", t));
        h.Click(Chip(h.Find<CommsView>()!, "All"));
        Assert.Equal("All", h.App.CommsChannel);
    }

    [AvaloniaFact]
    public void Control_CommsSend_PostsAsTheBlueCommanderAndDiplomaticGetsAReply()
    {
        using var h = Demo();
        ShowTab(h, "Comms");
        var view = h.Find<CommsView>()!;
        var channel = view.GetLogicalDescendants().OfType<ComboBox>().First();
        h.KeyOn(channel, PhysicalKey.ArrowDown); // Team -> Diplomatic
        Assert.Equal("Diplomatic", channel.SelectedItem);
        var box = view.GetLogicalDescendants().OfType<TextBox>().First();
        h.Type(box, "Hold your fire.");
        h.Click(Btn(view, "Send"));
        Assert.Equal("", box.Text);
        var mine = h.App.Data.Comms.Last();
        Assert.Equal("Blue commander (you)", mine.Who);
        Assert.Equal("Diplomatic", mine.Channel);
        Assert.Equal("Hold your fire.", mine.Text);
        Assert.True(h.HasLog("You (Diplomatic): Hold your fire."));
        h.Pump(1);
        Assert.Contains("Hold your fire.", Texts(h.Find<CommsView>()!));
        const string reply = "Message received. Your position is noted. Our terms stand.";
        for (int i = 0; i < 7; i++) h.App.Frame(.3); // 2.1 s: not yet (the reply comes after 2.5 s)
        Assert.DoesNotContain(h.App.Data.Comms, c => c.Text == reply);
        for (int i = 0; i < 2; i++) h.App.Frame(.3);
        Assert.Contains(h.App.Data.Comms, c => c.Text == reply && c.Who == "Red commander");
    }

    [AvaloniaFact]
    public void Control_Losses_ShowsPerSideLossesAndExpenditure()
    {
        using var h = Demo();
        h.Run(1500);
        ShowTab(h, "Losses");
        var view = h.Find<LossesView>()!;
        var texts = Texts(view).ToList();
        Assert.Contains("BLUE", texts);
        Assert.Contains("RED", texts);
        var red = h.App.Data.Losses.Red;
        Assert.NotEmpty(red.Lost); // the battle has cost Red aircraft by now
        foreach (var (k, v) in red.Lost)
        {
            Assert.Contains(k, texts);
            Assert.Contains(v.ToString(System.Globalization.CultureInfo.InvariantCulture), texts);
        }
        foreach (var (k, v) in h.App.Data.Losses.Blue.Lines) Assert.Contains(k, texts);
    }
}
