using Avalonia.Controls;
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
public sealed class CharacterStatusPanelUiTests
{
    [AvaloniaFact]
    public async Task OtherEffects_OmitsEffectRepresentedByOwnBuff()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillerMudClient-CharacterStatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));
            viewModel.Effects.Add(new StatusEffect("armor", "[+]", "", false, "", false, false, null));
            viewModel.Effects.Add(new StatusEffect("zatrucie", "[-]", "", true, "", true, false, null));
            viewModel.RequiredBuffs.Add(new BuffWatchEntry("armor"));

            typeof(MainWindowViewModel).GetMethod("RefreshOtherEffects",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(viewModel, null);

            var remaining = Assert.Single(viewModel.OtherEffects);
            Assert.Equal("zatrucie", remaining.Name);
            Assert.True(remaining.IsDebuff);
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
            viewModel.OtherEffects.Add(new StatusEffect(
                Name: "odbicie lustrzane",
                Icon: "[+]",
                Duration: "8",
                IsDebuff: false,
                Description: "Jesteś ukryty pośród swoich odbić.",
                Negative: false,
                Ending: false,
                ExtraValue: "8"));

            var tool = new PanelTool
            {
                Id = "MemSpells",
                Title = "Stan postaci",
                ViewType = typeof(MemSpellsPanelView),
                Context = viewModel,
            };
            var host = new PanelToolView { DataContext = tool };
            var window = new Window { Content = host };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var durationLabel = host.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "(8)");
            Assert.True(durationLabel.IsEffectivelyVisible);

            var descriptionLabel = host.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "Jesteś ukryty pośród swoich odbić.");
            Assert.False(descriptionLabel.IsEffectivelyVisible);

            viewModel.ShowExtendedEffects = true;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            Assert.True(durationLabel.IsEffectivelyVisible);
            Assert.True(descriptionLabel.IsEffectivelyVisible);

            window.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
