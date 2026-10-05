using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MudClient.App.Docking;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views.Panels;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class EffectsPanelUiTests
{
    [Fact]
    public async Task EffectLineItems_JoinWithCommasExceptAfterTheLast()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_EffectLine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));
            viewModel.Effects.Add(new StatusEffect("odbicie lustrzane", "[+]", "6", false, "", false, false, "6"));
            viewModel.Effects.Add(new StatusEffect("ślepota", "[-]", "", true, "", true, true, null));

            var items = viewModel.EffectLineItems;

            Assert.Equal(["odbicie lustrzane (6), ", "ślepota"], items.Select(item => item.Text));
            Assert.True(items[1].Ending);
            Assert.Equal("W pełni sił — brak dolegliwości.", viewModel.ConditionsLine);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task EffectDuration_ShowsInBasicView_DescriptionStaysExtendedOnly()
    {
        // The duration/count ("(8)") used to be gated behind ShowExtendedEffects along with the
        // description — now it's always shown when present, only the longer description stays
        // behind the toggle.
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_EffectsPanelUiTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            await using var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));
            Assert.False(viewModel.ShowExtendedEffects);
            viewModel.Effects.Add(new StatusEffect(
                Name: "odbicie lustrzane",
                Icon: "[+]",
                Duration: "8",
                IsDebuff: false,
                Description: "Jesteś ukryty pośród swoich odbić.",
                Negative: false,
                Ending: false,
                ExtraValue: "8"));
            typeof(MainWindowViewModel)
                .GetMethod("NotifyEffectLines", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(viewModel, null);

            var tool = new PanelTool
            {
                Id = "MemSpells",
                Title = "Mem, Buffy i Kondycja",
                ViewType = typeof(MemSpellsPanelView),
                Context = viewModel,
            };
            var host = new PanelToolView { DataContext = tool };
            var window = new Window { Content = host };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            // The effects are one comma-separated line now: name and duration in the basic view,
            // the description appended only while the extended view is on.
            var effectLabel = host.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "odbicie lustrzane (8)");
            Assert.True(effectLabel.IsEffectivelyVisible);

            viewModel.ShowExtendedEffects = true;
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var extendedLabel = host.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "odbicie lustrzane (8) — Jesteś ukryty pośród swoich odbić.");
            Assert.True(extendedLabel.IsEffectivelyVisible);

            window.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
