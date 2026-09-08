using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MudClient.App.Controls;
using MudClient.App.Docking;
using MudClient.App.ViewModels;

namespace MudClient.App.Views.Panels;

/// <summary>
/// The single generic, reusable widget that hosts every dockable panel.
/// Given a <see cref="PanelTool"/> as its DataContext, it instantiates
/// <c>PanelTool.ViewType</c> and binds it to <c>PanelTool.Context</c>.
/// </summary>
public partial class PanelToolView : UserControl
{
    private Type? _builtViewType;

    public PanelToolView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebuild();
    }

    /// <summary>Overridable in tests — see AutomationDeletionConfirmationUiTests.</summary>
    internal Func<Window, string, string, Task<bool>> ConfirmDeletionAsync { get; set; } =
        DeleteConfirmationDialog.ShowAsync;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        var host = this.FindControl<ContentControl>("Host")!;
        var helpButton = this.FindControl<Button>("HelpButton")!;
        var settingsButton = this.FindControl<Button>("SettingsButton")!;
        var memSettingsButton = this.FindControl<Button>("MemSettingsButton")!;
        var groupSettingsButton = this.FindControl<Button>("GroupSettingsButton")!;
        var chatSettingsButton = this.FindControl<Button>("ChatSettingsButton")!;
        var offensiveSettingsButton = this.FindControl<Button>("OffensiveSettingsButton")!;
        var statisticsSettingsButton = this.FindControl<Button>("StatisticsSettingsButton")!;

        if (DataContext is not PanelTool tool)
        {
            Classes.Set("mud-configurable-widget", false);
            _builtViewType = null;
            host.Content = null;
            helpButton.IsVisible = false;
            settingsButton.IsVisible = false;
            memSettingsButton.IsVisible = false;
            groupSettingsButton.IsVisible = false;
            chatSettingsButton.IsVisible = false;
            offensiveSettingsButton.IsVisible = false;
            statisticsSettingsButton.IsVisible = false;
            return;
        }

        Classes.Set("mud-configurable-widget",
            !string.Equals(tool.Id, "Terminal", StringComparison.Ordinal));

        // A tool rendered inside a Terminal overlay card gets its settings button from the card's
        // own title bar instead (see TerminalOverlayCard.axaml) — these would otherwise duplicate it.
        var isOverlaid = this.FindAncestorOfType<TerminalOverlayCard>() is not null;

        helpButton.IsVisible = tool.HasHelpTopic && !isOverlaid;

        // Terminal has no per-panel settings; Map, Mem, Group, and Chat show their own
        // real settings button instead of this
        // inert placeholder.
        settingsButton.IsVisible =
            !string.Equals(tool.Id, "Terminal", StringComparison.Ordinal)
            && !string.Equals(tool.Id, "Map", StringComparison.Ordinal)
            && !tool.IsMemTool
            && !tool.IsGroupTool
            && !tool.IsChatTool
            && !tool.IsOffensiveTool
            && !tool.IsStatisticsTool
            && !isOverlaid;

        memSettingsButton.IsVisible = tool.IsMemTool && !isOverlaid;
        groupSettingsButton.IsVisible = tool.IsGroupTool && !isOverlaid;
        chatSettingsButton.IsVisible = tool.IsChatTool && !isOverlaid;
        offensiveSettingsButton.IsVisible = tool.IsOffensiveTool && !isOverlaid;
        statisticsSettingsButton.IsVisible = tool.IsStatisticsTool && !isOverlaid;

        if (host.Content is Control existing && _builtViewType == tool.ViewType)
        {
            existing.DataContext = tool.Context;
            return;
        }

        var view = (Control)Activator.CreateInstance(tool.ViewType)!;
        view.DataContext = tool.Context;
        _builtViewType = tool.ViewType;
        host.Content = view;
    }

    private async void ResetStatistics_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not PanelTool { Context: MainWindowViewModel viewModel } ||
            string.IsNullOrWhiteSpace(viewModel.ActiveProfileName) ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (await ConfirmDeletionAsync(owner, "statystyki postaci", viewModel.ActiveProfileName))
        {
            viewModel.ResetExperienceStatistics();
        }
    }

    // ========================================================================
    // Mem's settings — buff-set management moved here from the former Buffs
    // panel. DataContext is the PanelTool itself; Context is MainWindowViewModel
    // (shared with most other panels) — see TerminalOverlayCard.axaml.cs for
    // the duplicate copy of these same handlers used when Mem is pinned as an
    // overlay instead of docked normally.
    // ========================================================================

    private void NewBuffSetBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is not (Key.Enter or Key.Return)
            || DataContext is not PanelTool { Context: MainWindowViewModel viewModel })
        {
            return;
        }

        eventArgs.Handled = true;
        if (viewModel.CreateBuffSetCommand.CanExecute(null))
        {
            viewModel.CreateBuffSetCommand.Execute(null);
        }
    }

    private void CreateBuffSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is PanelTool { Context: MainWindowViewModel viewModel }
            && viewModel.CreateBuffSetCommand.CanExecute(null))
        {
            viewModel.CreateBuffSetCommand.Execute(null);
        }
    }

    private async void DeleteBuffSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not PanelTool { Context: MainWindowViewModel viewModel }
            || viewModel.SelectedBuffSet is not { } selected
            || !viewModel.DeleteBuffSetCommand.CanExecute(null)
            || TopLevel.GetTopLevel(this) is not Window owner
            || sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            if (await ConfirmDeletionAsync(owner, "zestaw buffów", selected.Name))
            {
                viewModel.DeleteBuffSetCommand.Execute(null);
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void NewBuffBox_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is not (Key.Enter or Key.Return)
            || DataContext is not PanelTool { Context: MainWindowViewModel viewModel })
        {
            return;
        }

        eventArgs.Handled = true;
        if (viewModel.AddBuffCommand.CanExecute(null))
        {
            viewModel.AddBuffCommand.Execute(null);
        }
    }

    private void AddBuff_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is PanelTool { Context: MainWindowViewModel viewModel }
            && viewModel.AddBuffCommand.CanExecute(null))
        {
            viewModel.AddBuffCommand.Execute(null);
        }
    }
}
