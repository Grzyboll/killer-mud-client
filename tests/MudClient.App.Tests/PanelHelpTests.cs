using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudClient.App.Controls;
using MudClient.App.Docking;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views.Panels;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class PanelHelpTests
{
    [Fact]
    public void Catalog_HasOneCompleteTopicForEverySupportedPanel()
    {
        Assert.Equal(14, PanelHelpCatalog.All.Count);
        Assert.Equal(14, PanelHelpCatalog.All.Select(topic => topic.PanelId).Distinct().Count());
        Assert.Equal([
                "Terminal", "Group", "MemSpells", "OffensiveActions", "Automation",
                "AutomationTeam", "AutomationTravel", "AutomationCombat", "AutomationFarm", "Notes",
                "Gmcp", "Chat", "Settings", "Map"
            ],
            PanelHelpCatalog.All.Select(topic => topic.PanelId));

        Assert.All(PanelHelpCatalog.All, topic =>
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Overview));
            Assert.False(string.IsNullOrWhiteSpace(topic.Settings));
        });
    }

    [Fact]
    public void Catalog_UsesDescriptionsThatMatchCurrentCapabilities()
    {
        var group = PanelHelpCatalog.Find("Group")!;
        var mem = PanelHelpCatalog.Find("MemSpells")!;
        var offensive = PanelHelpCatalog.Find("OffensiveActions")!;
        var movement = PanelHelpCatalog.Find("Map")!;
        var terminal = PanelHelpCatalog.Find("Terminal")!;
        var automation = PanelHelpCatalog.Find("Automation")!;
        var farm = PanelHelpCatalog.Find("AutomationFarm")!;
        var gmcp = PanelHelpCatalog.Find("Gmcp")!;

        Assert.Contains("zapamiętanych użyć", group.Indicators.Single());
        Assert.Contains(mem.Indicators, indicator => indicator.Contains("[2/1]", StringComparison.Ordinal));
        Assert.Contains("dodawać i usuwać", offensive.Settings);
        Assert.DoesNotContain("edyt", offensive.Settings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NumPad 8", movement.Shortcuts[0]);
        Assert.Contains("🔒", movement.Indicators.Single());
        Assert.Contains(terminal.Shortcuts, shortcut => shortcut.Contains("Shift+Enter", StringComparison.Ordinal));
        Assert.Contains(automation.Indicators, indicator => indicator.Contains("naprawdę go uruchamia", StringComparison.Ordinal));
        Assert.Contains(farm.Indicators, indicator => indicator.Contains("~zaklęcie", StringComparison.Ordinal));
        Assert.Contains("diagnostyczny", gmcp.Overview, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void DockedPanel_HelpButtonUsesTheCatalogTopic()
    {
        var topic = PanelHelpCatalog.Find("Group")!;
        var tool = CreateTool("Group", typeof(GroupPanelView));
        var host = new PanelToolView { DataContext = tool };
        var window = new Window { Width = 500, Height = 500, Content = host };

        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var button = host.FindControl<Button>("HelpButton")!;
            Assert.True(button.IsVisible);
            button.Flyout!.ShowAt(button);
            Dispatcher.UIThread.RunJobs();

            var content = window.GetVisualDescendants().OfType<PanelHelpContent>().Single();
            Assert.Same(topic, content.DataContext);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TerminalOverlay_HelpButtonUsesTheSameCatalogTopic()
    {
        var topic = PanelHelpCatalog.Find("OffensiveActions")!;
        var card = new TerminalOverlayCard
        {
            DataContext = CreateTool("OffensiveActions", typeof(OffensiveActionsPanelView)),
        };
        var window = new Window { Width = 600, Height = 700, Content = card };

        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var button = card.GetVisualDescendants().OfType<Button>()
                .Single(candidate => candidate.Content?.ToString() == "?" && candidate.IsVisible);
            button.Flyout!.ShowAt(button);
            Dispatcher.UIThread.RunJobs();

            var content = window.GetVisualDescendants().OfType<PanelHelpContent>().Single();
            Assert.Same(topic, content.DataContext);
        }
        finally
        {
            window.Close();
        }
    }

    private static PanelTool CreateTool(string id, Type viewType) => new()
    {
        Id = id,
        Title = id,
        ViewType = viewType,
        Context = id == "Map"
            ? new MapViewModel(AppContext.BaseDirectory, new MudClient.Core.Map.GmcpLocationResolver())
            : null,
    };
}
