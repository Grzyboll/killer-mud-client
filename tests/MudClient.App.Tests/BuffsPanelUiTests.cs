using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudClient.App.Docking;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views;
using MudClient.App.Views.Panels;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class BuffsPanelUiTests
{
    // The buff-set management UI (create/rename/delete a set, add a buff) moved from the old
    // dedicated Buffs panel into Mem's own settings gear (MemSettingsButton, see PanelToolView.axaml)
    // when the two tools merged. Clicking the actual rendered TextBox/Button here (rather than
    // calling the ViewModel commands directly) catches a bad binding path or Click wiring in that
    // hand-written flyout — see TerminalOverlayCard's ▲▼◀▶ buttons earlier this session for why a
    // ViewModel-level test alone would not have caught that class of bug.
    [AvaloniaFact]
    public async Task MemSettingsFlyout_CreateSetThenAddBuff_ThroughRenderedControls()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "KillerMudClient-MemSettingsFlyoutUiTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(
            new ProfileService(directory),
            new AppSettingsService(directory),
            new DockLayoutService(directory));
        var tool = new PanelTool
        {
            Id = "MemSpells",
            Title = "📜 Mem, Buffy i Kondycja",
            ViewType = typeof(MemSpellsPanelView),
            Context = viewModel,
        };
        var host = new PanelToolView { DataContext = tool };
        var window = new Window { Width = 640, Height = 800, Content = host };

        try
        {
            window.Show();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();

            var settingsButton = host.FindControl<Button>("MemSettingsButton")!;
            Assert.True(settingsButton.IsVisible);
            settingsButton.Flyout!.ShowAt(settingsButton);
            Dispatcher.UIThread.RunJobs();

            // Both the "new set" and "rename active set" TextBoxes share the same placeholder —
            // the new-set one starts empty (NewBuffSetName), the rename one starts pre-filled with
            // the active set's current name (BuffSetNameDraft).
            var newSetBox = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.PlaceholderText == "Nazwa zestawu" && string.IsNullOrEmpty(box.Text));
            newSetBox.Text = "PvP";
            var createButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Utwórz"));
            createButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(viewModel.BuffSets, set => set.Name == "PvP");
            Assert.Equal("PvP", viewModel.SelectedBuffSet?.Name);

            var addBuffBox = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.PlaceholderText == "Nazwa buffa (np. armor)");
            addBuffBox.Text = "armor";
            var addButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Dodaj"));
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(viewModel.RequiredBuffs, buff => buff.Name == "armor");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void BuffRows_RenderNamesInsideClickableButtons()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "KillerMudClient-BuffsPanelUiTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(
            new ProfileService(directory),
            new AppSettingsService(directory),
            new DockLayoutService(directory),
            layoutPresetService: new LayoutPresetService(directory));
        viewModel.NewBuffName = "armor";
        viewModel.AddBuffCommand.Execute(null);
        viewModel.NewBuffName = "sanctuary";
        viewModel.AddBuffCommand.Execute(null);
        var window = new MainWindow
        {
            Width = 1400,
            Height = 900,
            DataContext = viewModel,
        };
        window.Show();
        // TRANSPARENCY (Terminal only, everything else hidden) is the app's own startup layout —
        // switch to DEFAULT so MemSpells is actually docked and renderable below.
        viewModel.ApplyLayoutCommand.Execute(LayoutPresetService.DefaultName);
        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        factory.SetActiveDockable(
            factory.AllTools.Single(tool => tool.Id == "MemSpells"));
        for (var i = 0; i < 15; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        try
        {
            var panel = window.GetVisualDescendants().OfType<MemSpellsPanelView>()
                .First(control => control.IsEffectivelyVisible);
            var recastButtons = panel.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible
                    && ToolTip.GetTip(button)?.ToString() == "Rzuć ten spell")
                .ToList();
            var buffList = panel.GetVisualDescendants().OfType<ItemsControl>()
                .Single(control => ReferenceEquals(control.ItemsSource, viewModel.RequiredBuffs));
            var setSelector = panel.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => ReferenceEquals(control.ItemsSource, viewModel.BuffSets));

            Assert.Equal(2, recastButtons.Count);
            Assert.Same(viewModel.SelectedBuffSet, setSelector.SelectedItem);

            foreach (var recastButton in recastButtons)
            {
                var buff = Assert.IsType<BuffWatchEntry>(recastButton.DataContext);
                var nameLabel = Assert.Single(
                    recastButton.GetVisualDescendants().OfType<TextBlock>(),
                    textBlock => textBlock.IsEffectivelyVisible && textBlock.Text == buff.Name);

                Assert.True(
                    recastButton.Bounds.Width > 200,
                    $"Buff button remained collapsed at {recastButton.Bounds.Width}px.");
                // Each row now has a drag handle ("⠿") column beside the button, so the button fills
                // the list minus that handle rather than the whole width.
                var handle = panel.GetVisualDescendants().OfType<TextBlock>()
                    .First(text => text.Text == "⠿" && text.IsEffectivelyVisible);
                var available = buffList.Bounds.Width - handle.Bounds.Width;
                Assert.True(
                    recastButton.Bounds.Width >= available - 6,
                    $"Buff button width {recastButton.Bounds.Width}px did not fill "
                    + $"the {available}px left beside the drag handle.");
                Assert.True(nameLabel.Bounds.Width > 20);
                Assert.Contains(
                    recastButton.GetVisualDescendants().OfType<Button>(),
                    button => button.Content?.ToString() == "✕");
            }

            var clickableBuff = recastButtons[0];
            Assert.True(clickableBuff.IsHitTestVisible);
            Assert.True(clickableBuff.IsEnabled);
            Assert.NotNull(clickableBuff.Command);
            clickableBuff.Command!.Execute(clickableBuff.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(
                viewModel.Toasts,
                toast => toast.Text == "Nie połączono — nie można rzucić buffa.");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, recursive: true);
        }
    }
}
