using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MudClient.App.Docking;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views.Panels;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class WidgetFontUiTests
{
    [AvaloniaFact]
    public async Task ChangingWidgetFont_UpdatesRenderedEmptyStateAndDockTitleResources()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_WidgetFont_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            await using var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));
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

            viewModel.WidgetFontFamily = "Verdana";
            viewModel.WidgetFontSize = 19;
            viewModel.WidgetFontBold = true;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var emptyState = host.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(text => text.Text == "W pełni sił — brak dolegliwości.");
            Assert.Equal("Verdana", emptyState.FontFamily.Name);
            Assert.Equal(19, emptyState.FontSize);
            Assert.Equal(Avalonia.Media.FontWeight.Bold, emptyState.FontWeight);
            Assert.Equal("Verdana", Avalonia.Application.Current!.Resources["WidgetFontFamilyResource"]!.ToString());
            Assert.Equal(19d, Avalonia.Application.Current.Resources["WidgetFontSizeResource"]);
            Assert.Equal(
                Avalonia.Media.FontWeight.Bold,
                Avalonia.Application.Current.Resources["WidgetFontWeightResource"]);

            Assert.Contains(AppFonts.OpenDyslexicName, viewModel.AvailableFonts);
            viewModel.WidgetFontFamily = AppFonts.OpenDyslexicName;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            Assert.Equal(AppFonts.OpenDyslexicName, emptyState.FontFamily.Name);
            Assert.Equal(AppFonts.OpenDyslexicName, viewModel.WidgetFontFamily);
            viewModel.OutputFontFamily = AppFonts.OpenDyslexicName;
            Assert.Equal(AppFonts.OpenDyslexicName, viewModel.OutputFontFamilyValue.Name);

            window.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
