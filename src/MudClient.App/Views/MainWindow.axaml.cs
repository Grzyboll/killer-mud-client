using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using MudClient.App.Controls;
using MudClient.App.Docking;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views.Panels;

namespace MudClient.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private Dock.Avalonia.Controls.DockControl? _mainDock;
    private CancellationTokenSource? _pinnedPanelAuditCts;
    private bool _closingAfterRecoveryFlush;
    private readonly DispatcherTimer _idleRefreshTimer;
    private DispatcherTimer? _blueNotificationStopTimer;
    private bool _isFighting;
    private bool _redNotificationActive;
    private bool _greenNotificationActive;
    private bool _blueNotificationActive;
    internal Func<Window, string, string, Task<bool>> ConfirmDeletionAsync { get; set; } =
        DeleteConfirmationDialog.ShowAsync;

    public Exception? DeferredSettingsImportError { get; init; }

    public MainWindow()
    {
        InitializeComponent();

        _idleRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _idleRefreshTimer.Tick += OnIdleRefreshTick;

        Opened += OnOpened;
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        PropertyChanged += OnMainWindowPropertyChanged;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        DataContextChanged += OnDataContextChanged;

        // Safety net: when a dock drag ends (drop or cancel, anywhere in the window),
        // reclaim any panel the drag pipeline dropped on the floor so it reappears in
        // the "Panele" restore menu instead of vanishing.
        var mainDock = this.FindControl<Dock.Avalonia.Controls.DockControl>("MainDock");
        _mainDock = mainDock;
        if (mainDock is not null)
        {
            mainDock.PropertyChanged += (_, args) =>
            {
                if (args.Property == Dock.Avalonia.Controls.DockControl.IsDraggingDockProperty
                    && args.NewValue is false)
                {
                    // Let the drag pipeline finish its bookkeeping first.
                    Dispatcher.UIThread.Post(() => _viewModel?.ReclaimLostPanels(), DispatcherPriority.Background);
                }
            };
        }

        // Intercept text input in the tunneling phase so we can redirect
        // printable characters to the command box when focus is outside
        // any text-editing control.
        AddHandler(InputElement.TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.RefreshIdleTime();
        _idleRefreshTimer.Start();

        try
        {
            // Auto-connect happens after the user picks a profile
            // (MainWindowViewModel.ActivateProfile).
            await viewModel.InitializeAsync();
            viewModel.StartContentUpdateCheck();
            viewModel.StartAppUpdateCheck();
            if (DeferredSettingsImportError is not null)
            {
                viewModel.ReportSettingsImportError(DeferredSettingsImportError);
            }
        }
        catch (Exception exception)
        {
            viewModel.ReportStartupError(exception);
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ReportStartupError(eventArgs.Exception);
        eventArgs.Handled = true;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        ClearTaskbarNotifications();

        // A layout applied while minimized is audited once the Dock visual tree is visible again.
        SchedulePinnedPanelAudit();

        // During Activated, Avalonia may not have restored logical focus yet, so do
        // not inspect FocusManager here. Queue the check after input/focus handling;
        // selecting synchronously is either skipped or overwritten by caret restore.
        var terminal = TerminalPanelView.Current;
        if (terminal is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(TerminalPanelView.Current, terminal) &&
                    FocusManager?.GetFocusedElement() is TextBox currentBox &&
                    terminal.IsCommandBox(currentBox))
                {
                    terminal.SelectAllCommandText();
                }
            }, DispatcherPriority.Background);
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void OnIdleRefreshTick(object? sender, EventArgs eventArgs) =>
        _viewModel?.RefreshIdleTime();

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ChatLineReceived -= OnChatLineReceivedForFlash;
            _viewModel.CombatStateChanged -= OnCombatStateChangedForFlash;
            _viewModel.AutomationFired -= OnAutomationFiredForFlash;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ChatLineReceived += OnChatLineReceivedForFlash;
            _viewModel.CombatStateChanged += OnCombatStateChangedForFlash;
            _viewModel.AutomationFired += OnAutomationFiredForFlash;
            // Pinned edge tabs use fixed proportions of the live dock area: one third of its
            // width at the sides and half its height at the top/bottom. The view supplies the
            // dimensions because the UI-agnostic factory cannot see the rendered DockControl.
            _viewModel.ConfigurePinnedPreviewSize(GetPinnedPreviewSize);
            SchedulePinnedPanelAudit();
        }
    }

    /// <summary>Lights up the green taskbar-overlay badge when a chat message arrives while the
    /// window is not focused — mirrors the community Mudlet package's <c>alert(5)</c> behavior for
    /// this MUD, just with a distinct color instead of a generic blink. Stays lit indefinitely
    /// (unlike the timed blue badge) — <see cref="OnWindowActivated"/> is what clears it, once the
    /// player actually comes back to look.</summary>
    private void OnChatLineReceivedForFlash(string line)
    {
        if (!IsHiddenFromView)
        {
            return;
        }

        _greenNotificationActive = true;
        UpdateTaskbarOverlay();
    }

    /// <summary>Lights up the red taskbar-overlay badge while the window is hidden from view (see
    /// <see cref="IsHiddenFromView"/>) and combat is ongoing (GMCP "fighting"); tracks
    /// <see cref="_isFighting"/> so <see cref="OnWindowDeactivated"/>/<see cref="OnWindowStateChanged"/>
    /// can also arm it if the window is hidden mid-fight rather than only when a fight starts while
    /// already hidden. Clears the moment combat ends, regardless of visibility — unlike green and
    /// blue, this one is tied to a game state, not a fixed duration or manual re-focus.</summary>
    private void OnCombatStateChangedForFlash(bool isFighting)
    {
        _isFighting = isFighting;
        if (isFighting)
        {
            if (IsHiddenFromView)
            {
                _redNotificationActive = true;
                UpdateTaskbarOverlay();
            }
        }
        else
        {
            _redNotificationActive = false;
            UpdateTaskbarOverlay();
        }
    }

    /// <summary>Lights up the blue taskbar-overlay badge for 15 seconds when a Trigger rule
    /// matches or a Timer tick actually fires while the window is hidden from view. The
    /// description itself is shown separately, always (visible or not), by the bottom status bar
    /// bound directly to <see cref="MainWindowViewModel.RecentAutomationActivityText"/> — this
    /// handler only cares that something fired, not what.</summary>
    private void OnAutomationFiredForFlash(string description)
    {
        if (!IsHiddenFromView)
        {
            return;
        }

        _blueNotificationActive = true;
        UpdateTaskbarOverlay();

        _blueNotificationStopTimer?.Stop();
        _blueNotificationStopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _blueNotificationStopTimer.Tick += (_, _) =>
        {
            _blueNotificationStopTimer?.Stop();
            _blueNotificationActive = false;
            UpdateTaskbarOverlay();
        };
        _blueNotificationStopTimer.Start();
    }

    /// <summary>True once the window is either minimized or simply not the OS-focused window —
    /// the single gate all three taskbar-overlay badges arm against. WindowState is checked
    /// explicitly alongside IsActive because minimizing doesn't reliably raise Deactivated on
    /// every platform (the window can stay the last "active" one at the OS level even once
    /// minimized), so IsActive alone isn't a reliable signal for this specific case.</summary>
    private bool IsHiddenFromView => WindowState == WindowState.Minimized || !IsActive;

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isFighting)
        {
            _redNotificationActive = true;
            UpdateTaskbarOverlay();
        }
    }

    /// <summary>Minimizing/restoring via the taskbar or window-chrome buttons doesn't always pair
    /// with Activated/Deactivated (see <see cref="IsHiddenFromView"/>), so WindowState changes are
    /// watched directly too: minimizing mid-fight arms red the same way losing focus does, and
    /// restoring while actually focused clears everything the same way regaining focus does.</summary>
    private void OnMainWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            if (_isFighting)
            {
                _redNotificationActive = true;
                UpdateTaskbarOverlay();
            }
        }
        else if (IsActive)
        {
            ClearTaskbarNotifications();
        }
    }

    private void ClearTaskbarNotifications()
    {
        _blueNotificationStopTimer?.Stop();
        _redNotificationActive = false;
        _greenNotificationActive = false;
        _blueNotificationActive = false;
        TaskbarOverlayIconService.SetState(this, TaskbarNotificationColor.None);
    }

    private void UpdateTaskbarOverlay()
    {
        var color = TaskbarNotificationColor.None;
        if (_redNotificationActive)
        {
            color |= TaskbarNotificationColor.Red;
        }

        if (_greenNotificationActive)
        {
            color |= TaskbarNotificationColor.Green;
        }

        if (_blueNotificationActive)
        {
            color |= TaskbarNotificationColor.Blue;
        }

        TaskbarOverlayIconService.SetState(this, color);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.Layout))
        {
            SchedulePinnedPanelAudit();
        }
    }

    /// <summary>
    /// Debounces rapid preset changes and checks the final Dock visual tree only after rendering
    /// has settled. A pinned model entry without a corresponding visible edge tab is recoverable
    /// from the "Panele" menu instead of remaining a permanently invisible ghost.
    /// </summary>
    private void SchedulePinnedPanelAudit()
    {
        _pinnedPanelAuditCts?.Cancel();
        _pinnedPanelAuditCts?.Dispose();
        _pinnedPanelAuditCts = new CancellationTokenSource();
        _ = AuditPinnedPanelsAfterRenderAsync(_pinnedPanelAuditCts.Token);
    }

    private async Task AuditPinnedPanelsAfterRenderAsync(CancellationToken cancellationToken)
    {
        try
        {
            const int maximumAttempts = 12;
            for (var attempt = 0; attempt < maximumAttempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                var completed = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_viewModel is null
                        || _mainDock?.Layout != _viewModel.Layout
                        || !IsVisible
                        || WindowState == WindowState.Minimized
                        || _mainDock.Bounds is not { Width: > 0, Height: > 0 })
                    {
                        return;
                    }

                    var renderedPanels = RenderedPinnedPanels();
                    var expectedPanels = _viewModel.PinnedPanels;
                    if (expectedPanels.All(renderedPanels.Contains))
                    {
                        completed = true;
                        return;
                    }

                    if (attempt == maximumAttempts - 1)
                    {
                        // Dock's model contains the pins but one or more presenters never
                        // appeared. Normalize those pins on their original edges instead of
                        // turning a slow startup/import into persisted closed panels.
                        _viewModel.RepairUnrenderedPinnedPanels(renderedPanels);
                        completed = true;
                    }
                });

                if (completed)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer layout superseded this audit, or the window is closing.
        }
        catch (Exception exception)
        {
            // Keep failures in this fire-and-forget recovery path observable.
            _viewModel?.ReportStartupError(exception);
        }
    }

    private HashSet<PanelTool> RenderedPinnedPanels() =>
        _mainDock?.GetVisualDescendants()
            .OfType<Dock.Avalonia.Controls.ToolPinItemControl>()
            .Where(control => control.IsEffectivelyVisible
                              && control.Bounds is { Width: > 0, Height: > 0 })
            .Select(control => control.DataContext)
            .OfType<PanelTool>()
            .ToHashSet() ?? [];

    private async void DeleteProfile_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: string profileName } button || _viewModel is null)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            if (await ConfirmDeletionAsync(this, "profil", profileName))
            {
                _viewModel.DeleteProfileCommand.Execute(profileName);
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// The fixed preview size for the given edge: one third of the live dock width for a side
    /// (Left/Right) tab and half its height for a top/bottom tab. Falls back to the window client
    /// size before the dock has been laid out.
    /// </summary>
    private double GetPinnedPreviewSize(Dock.Model.Core.Alignment edge)
    {
        var width = _mainDock?.Bounds.Width ?? 0;
        var height = _mainDock?.Bounds.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            width = ClientSize.Width;
            height = ClientSize.Height;
        }

        var side = edge is Dock.Model.Core.Alignment.Left or Dock.Model.Core.Alignment.Right;
        return side ? width / 3.0 : height / 2.0;
    }

    private void Window_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        // Clicking anywhere on the window should drop focus straight into the command line,
        // unless the click is meant for another interactive control. Right-clicks must keep
        // their current focus so context menus are not immediately dismissed.
        if (eventArgs.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            return;
        }

        var terminal = TerminalPanelView.Current;
        if (terminal is null || eventArgs.Source is not Visual visual)
        {
            return;
        }

        if (terminal.OwnsControl(visual) || visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }

        if (visual.FindAncestorOfType<Button>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<ListBox>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<TabControl>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<GridSplitter>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<ComboBox>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<WorldMapControl>(includeSelf: true) is not null ||
            visual.FindAncestorOfType<ProgressBar>(includeSelf: true) is not null)
        {
            return;
        }

        terminal.FocusCommandBoxAndSelectAll();
    }

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        var terminal = TerminalPanelView.Current;

        if (focused is TextBox focusedTextBox)
        {
            // When the terminal's command box has focus and window focus just returned,
            // select all text so the first typed character replaces existing input.
            if (terminal is not null && terminal.IsCommandBox(focusedTextBox))
            {
                terminal.PrepareCommandBoxForFirstInput();
            }
            else if (terminal is not null)
            {
                // Text input arrived for a non-terminal TextBox (host/port/profile, etc.).
                // Clear any pending select-all mark so it does not hijack the command box
                // when the user later clicks into it and types.
                terminal.ClearSelectAllOnNextInput();
            }

            return;
        }

        // No TextBox has focus – redirect printable characters to the terminal command box.
        if (terminal is null)
        {
            return;
        }

        e.Handled = true;
        terminal.RedirectTextInput(e);
    }

    private void SelectProfileField_OnKeyDown(object? sender, KeyEventArgs eventArgs)
        => ExecuteOnEnter(eventArgs, _viewModel?.SelectProfileCommand);

    private void CreateProfileField_OnKeyDown(object? sender, KeyEventArgs eventArgs)
        => ExecuteOnEnter(eventArgs, _viewModel?.CreateProfileCommand);

    private static void ExecuteOnEnter(KeyEventArgs eventArgs, System.Windows.Input.ICommand? command)
    {
        if (eventArgs.Key is not (Key.Enter or Key.Return) || command is null)
        {
            return;
        }

        eventArgs.Handled = true;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void ProfileList_OnDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Only react to double-clicks on an actual item, not on empty list space.
        if (eventArgs.Source is Visual source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: string profileName })
        {
            _viewModel.SelectedProfileName = profileName;
            if (_viewModel.SelectProfileCommand.CanExecute(null))
            {
                _viewModel.SelectProfileCommand.Execute(null);
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs eventArgs)
    {
        if (!_closingAfterRecoveryFlush && _viewModel?.Map.IsMapEditorDirty == true)
        {
            eventArgs.Cancel = true;
            _ = FlushRecoveryAndCloseAsync();
            return;
        }

        base.OnClosing(eventArgs);
    }

    private async Task FlushRecoveryAndCloseAsync()
    {
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.Map.FlushMapEditorRecoveryAsync();
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or NotSupportedException)
        {
            // Closing must remain possible when the optional recovery checkpoint cannot be written.
            System.Diagnostics.Trace.WriteLine(exception);
        }
        finally
        {
            _closingAfterRecoveryFlush = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        _idleRefreshTimer.Stop();
        _idleRefreshTimer.Tick -= OnIdleRefreshTick;
        _pinnedPanelAuditCts?.Cancel();
        _pinnedPanelAuditCts?.Dispose();
        _pinnedPanelAuditCts = null;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _ = _viewModel.DisposeAsync();
        }

        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;

        base.OnClosed(eventArgs);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
