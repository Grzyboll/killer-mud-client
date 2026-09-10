using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudClient.App.Controls;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views;
using MudClient.App.Views.Panels;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Tests for click-to-focus, select-all, and focus-reactivation behavior.
/// Production members live in <see cref="TerminalPanelView"/> (not MainWindow);
/// the window delegates to <see cref="TerminalPanelView.Current"/>.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class MainWindowClickTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "KillerMudClient-MainWindowClickTests", Guid.NewGuid().ToString("N"));
    private MainWindow? _window;

    public void Dispose()
    {
        // Every test in this class opens one MainWindow. Closing it before Avalonia.Headless
        // tears down the per-test compositor prevents its render-loop task leaking into the
        // next isolated application on a different test thread. Keep the window reference
        // directly: TerminalPanelView.Current can already belong to another isolated session
        // or be cleared while the visual tree is detaching.
        _window?.Close();

        Dispatcher.UIThread.RunJobs();

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private MainWindowViewModel CreateViewModel() => new(
        new ProfileService(_tempDirectory),
        new AppSettingsService(_tempDirectory),
        new DockLayoutService(_tempDirectory),
        layoutPresetService: new LayoutPresetService(_tempDirectory),
        groupSpellStore: new GroupSpellStore(Path.Combine(_tempDirectory, "group-spells.json")));

    /// <summary>
    /// Returns the live TerminalPanelView instance associated with the given
    /// <paramref name="window"/>.  Uses the static <see cref="TerminalPanelView.Current"/>
    /// reference when available, otherwise falls back to a visual-tree walk
    /// with pumping.  (In the headless environment, the XAML template may not
    /// be fully expanded synchronously during Show().)
    /// </summary>
    private TerminalPanelView GetPanel(Window window)
    {
        _window = Assert.IsType<MainWindow>(window);

        // Fast path: static reference already set and belongs to THIS window. Tests never
        // close their windows, so a prior test's panel stays attached and TerminalPanelView.Current
        // may still point at it (its new panel attaches asynchronously in headless). Trusting a
        // stale Current leaks that panel's select-all flag into this test — verify the root first.
        var panel = TerminalPanelView.Current;
        if (panel is not null &&
            panel.IsAttachedToVisualTree() &&
            ReferenceEquals(panel.FindAncestorOfType<Window>(), window))
        {
            return panel;
        }

        // Slow path: search the visual tree with pumping.  The TerminalPanelView
        // is created during XAML loading, but in headless mode the layout system
        // may need multiple cycles before the control is discoverable.
        for (var i = 0; i < 15; i++)
        {
            panel = window.GetVisualDescendants()
                .OfType<TerminalPanelView>()
                .FirstOrDefault(p => p.IsAttachedToVisualTree());

            if (panel is not null)
                return panel;

            // Pump both timer-tick (layout/animation) and dispatcher jobs
            // to ensure the XAML template is fully expanded.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        throw new InvalidOperationException(
            "TerminalPanelView not found in the window's visual tree after pumping.");
    }

    /// <summary>Gets the private _commandBox field via reflection.</summary>
    private static TextBox GetCommandBox(TerminalPanelView panel)
    {
        var field = typeof(TerminalPanelView).GetField("_commandBox",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (TextBox)field!.GetValue(panel)!;
    }

    /// <summary>Gets the private _historyIndex field via reflection.</summary>
    private static int GetHistoryIndex(TerminalPanelView panel)
    {
        var field = typeof(TerminalPanelView).GetField("_historyIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (int)field!.GetValue(panel)!;
    }

    /// <summary>Sets the private _historyIndex field via reflection.</summary>
    private static void SetHistoryIndex(TerminalPanelView panel, int value)
    {
        var field = typeof(TerminalPanelView).GetField("_historyIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(panel, value);
    }

    /// <summary>Invokes the private HandlePostSend method via reflection.</summary>
    private static void InvokeHandlePostSend(TerminalPanelView panel)
    {
        var method = typeof(TerminalPanelView).GetMethod("HandlePostSend",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(panel, null);
    }

    /// <summary>Gets the private _mudOutput field via reflection.</summary>
    private static MudOutputView GetMudOutput(TerminalPanelView panel)
    {
        var field = typeof(TerminalPanelView).GetField("_mudOutput",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (MudOutputView)field!.GetValue(panel)!;
    }

    /// <summary>Gets the private _shouldSelectAllOnNextInput flag via reflection.</summary>
    private static bool GetShouldSelectAllOnNextInput(TerminalPanelView panel)
    {
        var field = typeof(TerminalPanelView).GetField("_shouldSelectAllOnNextInput",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (bool)field!.GetValue(panel)!;
    }

    /// <summary>Helper: fully lay out the window so Bounds and TranslatePoint work.</summary>
    private void EnsureLayout(Window window)
    {
        _window = Assert.IsType<MainWindow>(window);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    /// <summary>Invokes the private OnWindowActivated handler via reflection.</summary>
    private static void InvokeOnWindowActivated(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("OnWindowActivated",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(window, [null, EventArgs.Empty]);
    }

    /// <summary>Invokes the private OnPreviewTextInput handler via reflection.</summary>
    private static void InvokeOnPreviewTextInput(MainWindow window, TextInputEventArgs args)
    {
        var method = typeof(MainWindow).GetMethod("OnPreviewTextInput",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(window, [null, args]);
    }

    // ==================================================================
    // FocusCommandBoxAndSelectAll (public API on TerminalPanelView)
    // ==================================================================

    [AvaloniaFact]
    public void HelpButton_ShowsAvailableClientCommands()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var helpWidget = window.GetVisualDescendants()
            .OfType<LargeTabbedWidget>()
            .Single(widget => widget.Title == "Pomoc");
        Assert.False(helpWidget.IsVisible);

        var helpButton = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content?.ToString() == "Pomoc");
        Assert.NotNull(helpButton.Command);
        helpButton.Command.Execute(helpButton.CommandParameter);
        Assert.True(viewModel.IsHelpOpen);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();

        Assert.True(helpWidget.IsVisible);
        var helpTexts = helpWidget.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(text => text.Text)
            .ToList();
        Assert.Contains("/walk", helpTexts);
        Assert.Contains("/walk <cel>", helpTexts);
        Assert.Contains("/walk_dodaj <nazwa>", helpTexts);
        Assert.Contains("/stop", helpTexts);
        Assert.Contains("/recast", helpTexts);
        Assert.Contains("/map <komenda>", helpTexts);
        Assert.Contains("/map show <vnum>", helpTexts);

        var helpTabs = helpWidget.GetVisualDescendants().OfType<TabControl>().Single();
        helpTabs.SelectedItem = helpTabs.Items
            .OfType<TabItem>()
            .Single(tab => tab.Header?.ToString() == "Panele");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();

        var panelHelpTexts = helpWidget.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(text => text.Text)
            .ToList();
        Assert.Contains("Terminal", panelHelpTexts);
        Assert.Contains("Drużyna", panelHelpTexts);
        Assert.Contains("Stan postaci", panelHelpTexts);
        Assert.Contains("Offensywne i Definiowalne", panelHelpTexts);
        Assert.Contains("Automaty", panelHelpTexts);
        Assert.Contains("Auto: Drużyna", panelHelpTexts);
        Assert.Contains("Auto: Podróż", panelHelpTexts);
        Assert.Contains("Auto: Walka", panelHelpTexts);
        Assert.Contains("Auto: Farma", panelHelpTexts);
        Assert.Contains("Notatki", panelHelpTexts);
        Assert.Contains("GMCP", panelHelpTexts);
        Assert.Contains("Czat", panelHelpTexts);
        Assert.Contains("Ustawienia", panelHelpTexts);
        Assert.Contains("Ruch", panelHelpTexts);
    }

    [AvaloniaFact]
    public void LayoutMenu_ShowsUpdateButtonForSavedPresets()
    {
        var viewModel = CreateViewModel();
        viewModel.NewLayoutName = "moja";
        viewModel.SaveLayoutCommand.Execute(null);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var layoutMenuButton = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content?.ToString() == "Układy paneli");
        layoutMenuButton.Flyout!.ShowAt(layoutMenuButton);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();

        var flyout = window.GetVisualDescendants()
            .OfType<FlyoutPresenter>()
            .First();
        var presetButtons = flyout.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Content?.ToString() == "↺")
            .ToList();

        Assert.NotEmpty(presetButtons);

        var updateButton = presetButtons.Single(button =>
            Equals(button.CommandParameter, "moja"));
        updateButton.Command!.Execute(updateButton.CommandParameter);
    }

    [AvaloniaFact]
    public void DiscussionsButton_IsWiredToOpenDiscussionsCommand()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var discussionsButton = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content?.ToString() == "Dyskusja");

        // Never execute the command in a test — it opens a real browser via
        // IExternalLinkService.Open (Process.Start with UseShellExecute). Checking the binding
        // wires to the right command object is enough to catch a compiled-but-inert binding
        // (see PinnedTabUiTests' own prior lesson on that failure mode).
        Assert.Same(viewModel.OpenDiscussionsCommand, discussionsButton.Command);
    }

    [AvaloniaFact]
    public void AuthorLogoButton_IsWiredToOpenAuthorPageCommand_WithHoverTip()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var logoButton = window.FindControl<Button>("AuthorLogoButton");
        Assert.NotNull(logoButton);

        Assert.Same(viewModel.OpenAuthorPageCommand, logoButton!.Command);
        Assert.Equal(
            "Podoba ci się to co robię? \"Toss A Coin To Your Witcher\" - jak mnie spotkasz na trakcie ;)",
            ToolTip.GetTip(logoButton));

        // Confirms the actual asset resolves at runtime, not just a compiled-looking avares:// URI
        // — a typo'd resource path fails silently as a blank Image rather than a build error.
        var image = Assert.IsType<Image>(logoButton.Content);
        Assert.NotNull(image.Source);
    }

    [AvaloniaFact]
    public void Header_ShowsIdleTimeSinceLastMudCommand()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var idleTime = window.FindControl<TextBlock>("IdleTimeIndicator");

        Assert.NotNull(idleTime);
        Assert.Equal("Idle: —", idleTime!.Text);
        Assert.Equal("Czas od ostatniej komendy wysłanej do MUD-a", ToolTip.GetTip(idleTime));
    }

    [AvaloniaFact]
    public void ProfilePicker_ContainsServerFieldsAndHeaderDoesNot()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var profileServerFields = window.FindControl<StackPanel>("ProfileServerFields");
        var newProfileServerFields = window.FindControl<Grid>("NewProfileServerFields");
        var connectionActions = window.FindControl<StackPanel>("TopActionsRow");

        Assert.NotNull(profileServerFields);
        Assert.NotNull(newProfileServerFields);
        Assert.NotNull(connectionActions);
        Assert.Contains(profileServerFields!.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText == "host");
        Assert.Contains(profileServerFields.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText == "port");
        Assert.Contains(newProfileServerFields!.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText == "host");
        Assert.Contains(newProfileServerFields.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText == "port");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText == "Nazwa konta w aplikacji");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText == "Login MUD");
        Assert.DoesNotContain(connectionActions!.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.PlaceholderText is "host" or "port");
    }

    [AvaloniaFact]
    public void FocusCommandBoxAndSelectAll_FocusesAndSelectsAll()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "hello world";

        // Act
        panel.FocusCommandBoxAndSelectAll();

        // Assert
        Assert.True(commandBox.IsFocused);
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("hello world".Length, commandBox.SelectionEnd);
    }

    [AvaloniaFact]
    public void FocusCommandBoxAndSelectAll_WithEmptyText_SelectsNothing()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = string.Empty;

        // Act
        panel.FocusCommandBoxAndSelectAll();

        // Assert
        Assert.True(commandBox.IsFocused);
        // SelectAll on an empty TextBox gives SelectionStart == 0, SelectionEnd == 0
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal(0, commandBox.SelectionEnd);
    }

    // ==================================================================
    // HandlePostSend (private on TerminalPanelView, tested via reflection)
    // ==================================================================

    [AvaloniaFact]
    public void HandlePostSend_FocusesAndSelectsAllAndResetsHistoryIndex()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "look north";

        // Simulate being in the middle of history navigation.
        SetHistoryIndex(panel, 3);
        Assert.Equal(3, GetHistoryIndex(panel));

        // Act
        InvokeHandlePostSend(panel);

        // Assert
        Assert.True(commandBox.IsFocused);
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("look north".Length, commandBox.SelectionEnd);
        Assert.Equal(-1, GetHistoryIndex(panel));
    }

    [AvaloniaFact]
    public void HandlePostSend_WithEmptyText_ResetsHistoryIndex()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = string.Empty;
        SetHistoryIndex(panel, 1);

        // Act
        InvokeHandlePostSend(panel);

        // Assert
        Assert.True(commandBox.IsFocused);
        Assert.Equal(-1, GetHistoryIndex(panel));
    }

    [AvaloniaFact]
    public void HandlePostSend_WithClearingEnabled_ClearsTextAndResetsHistoryIndex()
    {
        var viewModel = CreateViewModel();
        viewModel.ClearCommandInputAfterSend = true;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "look north";
        SetHistoryIndex(panel, 2);

        InvokeHandlePostSend(panel);

        Assert.True(commandBox.IsFocused);
        Assert.Equal(string.Empty, commandBox.Text);
        Assert.Equal(string.Empty, viewModel.CommandText);
        Assert.Equal(-1, GetHistoryIndex(panel));
    }

    // ==================================================================
    // Window_OnPointerPressed — redirect to command box
    // ==================================================================

    [AvaloniaFact]
    public void PointerPressed_OnNonInteractiveArea_FocusesCommandBoxAndSelectsAll()
    {
        // Verifies the end-to-end behavior: a pointer press on a
        // non-excluded area redirects focus to the command box.
        //
        // Headless MouseDown/MouseUp coordinate-based hit-testing is
        // fragile with the DockControl and the profile overlay, so
        // we raise PointerPressedEventArgs directly on the root grid
        // (a non-excluded visual) to exercise the real handler.
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "select-all-test";

        // Act: construct a PointerPressed event whose source is the
        // root Grid (a non-excluded visual).
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties();
        var rootGrid = (Control)window.Content!;
        var args = new PointerPressedEventArgs(
            rootGrid,
            pointer,
            window,
            new Point(0, 0),
            0UL,
            props,
            KeyModifiers.None,
            clickCount: 1);

        rootGrid.RaiseEvent(args);

        // Assert: the handler redirected focus and selected all text.
        Assert.True(commandBox.IsFocused,
            "CommandBox should be focused after pointer press on non-excluded area.");
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("select-all-test".Length, commandBox.SelectionEnd);
    }

    [AvaloniaFact]
    public void PointerPressed_WithRightButton_DoesNotFocusCommandBox()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        Assert.False(commandBox.IsFocused);

        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var properties = new PointerPointProperties(
            RawInputModifiers.RightMouseButton,
            PointerUpdateKind.RightButtonPressed);
        var rootGrid = (Control)window.Content!;
        var args = new PointerPressedEventArgs(
            rootGrid,
            pointer,
            window,
            new Point(0, 0),
            0UL,
            properties,
            KeyModifiers.None,
            clickCount: 1);

        rootGrid.RaiseEvent(args);

        Assert.False(commandBox.IsFocused);
    }

    [AvaloniaFact]
    public void PointerPressed_OnButton_FindAncestorOfType_ExcludesButton()
    {
        // Validates exclusion logic directly: a Button ancestor causes
        // the handler to return early.
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        // Pick a visible Button with non-empty bounds.
        var anyButton = window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Bounds.Width > 0 && b.Bounds.Height > 0);
        Assert.NotNull(anyButton);
        Assert.True(anyButton.Bounds.Width > 0,
            "Chosen button must have non-zero width.");

        // Act: simulate what Window_OnPointerPressed does — check
        // FindAncestorOfType<Button> on the source.
        var ancestorButton = anyButton.FindAncestorOfType<Button>(includeSelf: true);

        // Assert: the source IS a Button, so the exclusion check
        // returns non-null and the handler would return early.
        Assert.NotNull(ancestorButton);
        Assert.Same(anyButton, ancestorButton);
    }

    [AvaloniaFact]
    public void PointerPressed_OnMudOutput_FindAncestorOfType_DoesNotMatchButton()
    {
        // Validates that clicking on MudOutputView does NOT find a Button
        // ancestor, so the handler proceeds to FocusCommandBoxAndSelectAll.
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var mudOutput = GetMudOutput(panel);

        // The MudOutputView's first visual child at the top-left is likely
        // a Border (its outermost element in the XAML template).
        var firstChild = mudOutput.GetVisualDescendants().FirstOrDefault();
        Assert.NotNull(firstChild);
        var visual = firstChild as Visual ?? mudOutput;

        // Act / Assert: no Button ancestor is found.
        Assert.Null(visual.FindAncestorOfType<Button>(includeSelf: true));
    }

    [AvaloniaFact]
    public void PointerPressed_OnSendButton_FocusesCommandBoxViaHandler()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "look";

        // The Send button is inside TerminalPanelView (not directly in
        // the window's logical tree due to DockControl nesting).  Search
        // within the panel's visual descendants instead.
        var sendButton = ((Visual)panel)
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => ToolTip.GetTip(b) is string s && s.Contains("Wyślij"));
        Assert.NotNull(sendButton);

        // Act: raise the Click event as the UI would after mouse-up.
        // SendButton_OnClick → HandlePostSend().
        sendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // Assert: command box is focused and text is selected.
        Assert.True(commandBox.IsFocused);
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("look".Length, commandBox.SelectionEnd);
        Assert.Equal(-1, GetHistoryIndex(panel));
    }

    // ==================================================================
    // OwnsControl — MudOutput children are no longer "owned", so the
    // window handler does not return early and redirect proceeds.
    // ==================================================================

    /// <summary>
    /// Verifies that <see cref="TerminalPanelView.OwnsControl"/> returns
    /// <c>false</c> for a child of MudOutputView.  After the fix,
    /// <c>OwnsControl</c> only returns <c>true</c> for the command box
    /// itself, so the <c>Window_OnPointerPressed</c> handler will
    /// <em>not</em> return early for MudOutput children — instead the
    /// redirect to the command box proceeds.
    /// </summary>
    [AvaloniaFact]
    public void OwnsControl_ReturnsFalseForMudOutputChild()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var mudOutput = GetMudOutput(panel);

        // Any visual descendant of MudOutput should NOT be "owned".
        var mudChild = mudOutput
            .GetVisualDescendants()
            .OfType<OutputPaneControl>()
            .FirstOrDefault();
        Assert.NotNull(mudChild);

        // Act
        var isOwned = panel.OwnsControl(mudChild);

        // Assert
        Assert.False(isOwned,
            "MudOutput children must NOT be recognised as owned; only the command box is owned.");
    }

    /// <summary>
    /// End-to-end test: clicking a child of MudOutputView redirects focus
    /// to the command box and selects all existing text.  This validates
    /// the fix — MudOutput children are no longer excluded by
    /// <c>OwnsControl</c> and fall through to the redirect logic.
    /// </summary>
    [AvaloniaFact]
    public void PointerPressed_OnMudOutputChild_RedirectsFocusAndSelectsAll()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "mud-output-click-redirect";

        var mudOutput = GetMudOutput(panel);

        // Grab a visual descendant inside MudOutputView (an OutputPaneControl).
        var mudChild = mudOutput
            .GetVisualDescendants()
            .OfType<OutputPaneControl>()
            .FirstOrDefault(c => c.Bounds.Width > 0 && c.Bounds.Height > 0);
        Assert.NotNull(mudChild);

        // Act: raise a PointerPressed event whose source is the MudOutput child.
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties();
        var args = new PointerPressedEventArgs(
            mudChild,
            pointer,
            window,
            new Point(0, 0),
            0UL,
            props,
            KeyModifiers.None,
            clickCount: 1);

        mudChild.RaiseEvent(args);

        // Assert: the handler redirected focus and selected all text.
        Assert.True(commandBox.IsFocused,
            "CommandBox should be focused after pointer press on MudOutput child.");
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("mud-output-click-redirect".Length, commandBox.SelectionEnd);
    }

    // ==================================================================
    // SelectableTextBlock outside MudOutputView → excluded
    // ==================================================================

    /// <summary>
    /// Clicking a SelectableTextBlock that is NOT inside MudOutputView
    /// must NOT redirect focus to the command box. Verifies the fix for
    /// the reviewer's MEDIUM finding (regression of non-output
    /// selectable text areas).
    /// </summary>
    [AvaloniaFact]
    public void PointerPressed_OnNonOutputSelectableTextBlock_IsExcluded()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        // Add a SelectableTextBlock to the window's visual tree
        // outside MudOutputView.
        var rootGrid = Assert.IsAssignableFrom<Grid>(window.Content);
        var nonOutputBlock = new SelectableTextBlock
        {
            Text = "non-output excluded text",
            Height = 20,
        };
        Grid.SetRow(nonOutputBlock, 2);
        rootGrid.Children.Add(nonOutputBlock);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "do not select";

        EnsureLayout(window);

        // Act: construct PointerPressedEventArgs whose Source will be
        // the non-output SelectableTextBlock, then raise it on the
        // block so the event bubbles up to Window_OnPointerPressed.
        var avaloniaPointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties();
        var pointerArgs = new PointerPressedEventArgs(
            nonOutputBlock,
            avaloniaPointer,
            window,
            new Point(0, 0),
            0UL,
            props,
            KeyModifiers.None,
            clickCount: 1);

        nonOutputBlock.RaiseEvent(pointerArgs);

        // Assert: command box must NOT be focused (exclusion should
        // have prevented the redirect).
        Assert.False(commandBox.IsFocused,
            "CommandBox must NOT be focused after clicking non-output SelectableTextBlock.");
    }

    // ==================================================================
    // Direct FindAncestorOfType logic tests (no event simulation)
    // ==================================================================

    /// <summary>
    /// A control inside MudOutputView correctly finds its
    /// MudOutputView ancestor — this is the condition that enables the
    /// click-to-focus redirect for MUD output text.
    /// </summary>
    [AvaloniaFact]
    public void PointerPressed_MudOutputSelectableTextBlock_FindsMudOutputAncestor()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var mudOutput = GetMudOutput(panel);
        mudOutput.AppendText("test line\n");
        EnsureLayout(window);

        var mudLine = mudOutput
            .GetVisualDescendants()
            .OfType<OutputPaneControl>()
            .FirstOrDefault(s => s.Bounds.Width > 0);
        Assert.NotNull(mudLine);

        // Act: the output pane must be inside MudOutputView, so clicks on it
        // are not excluded from the command-box focus redirect.
        var mudOutputAncestor = mudLine.FindAncestorOfType<MudOutputView>(includeSelf: true);

        // Assert
        Assert.NotNull(mudOutputAncestor);
        Assert.Same(mudOutput, mudOutputAncestor);
    }

    /// <summary>
    /// A SelectableTextBlock that is NOT inside MudOutputView does NOT
    /// find a MudOutputView ancestor — this is the exclusion condition
    /// that prevents redirect for non-output selectable text areas.
    /// </summary>
    [AvaloniaFact]
    public void PointerPressed_NonOutputSelectableTextBlock_FindsNoMudOutputAncestor()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var rootGrid = Assert.IsAssignableFrom<Grid>(window.Content);
        var nonOutputBlock = new SelectableTextBlock
        {
            Text = "non-output",
            Height = 20,
        };
        Grid.SetRow(nonOutputBlock, 2);
        rootGrid.Children.Add(nonOutputBlock);
        EnsureLayout(window);

        // Act: simulate what Window_OnPointerPressed does.
        var selectable = nonOutputBlock.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        Assert.NotNull(selectable);
        var mudOutputAncestor = selectable.FindAncestorOfType<MudOutputView>(includeSelf: true);

        // Assert: SelectableTextBlock is found (self), but no
        // MudOutputView ancestor — handler returns early (excluded).
        Assert.Same(nonOutputBlock, selectable);
        Assert.Null(mudOutputAncestor);
    }

    // ==================================================================
    // Focus reactivation — PrepareCommandBoxForFirstInput / select-all mark
    // ==================================================================

    [AvaloniaFact]
    public void PrepareCommandBoxForFirstInput_WithMark_SelectsAll()
    {
        // Arrange: command box has text, mark is set (as after window activation).
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "existing command text";
        commandBox.SelectionStart = 3;
        commandBox.SelectionEnd = 10;

        panel.MarkForSelectAllOnNextInput();

        // Act
        panel.PrepareCommandBoxForFirstInput();

        // Assert: all text selected, mark cleared.
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("existing command text".Length, commandBox.SelectionEnd);
        Assert.False(GetShouldSelectAllOnNextInput(panel));
    }

    [AvaloniaFact]
    public void PrepareCommandBoxForFirstInput_WithoutMark_DoesNotChangeSelection()
    {
        // Arrange: command box has text, mark is NOT set.
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "existing command text";
        commandBox.SelectionStart = 3;
        commandBox.SelectionEnd = 10;

        // Act — no MarkForSelectAllOnNextInput() was called.
        panel.PrepareCommandBoxForFirstInput();

        // Assert: selection unchanged.
        Assert.Equal(3, commandBox.SelectionStart);
        Assert.Equal(10, commandBox.SelectionEnd);
    }

    [AvaloniaFact]
    public void PrepareCommandBoxForFirstInput_WithMarkAndEmptyText_SelectsNothing()
    {
        // Arrange: empty command box, mark is set.
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = string.Empty;

        panel.MarkForSelectAllOnNextInput();

        // Act
        panel.PrepareCommandBoxForFirstInput();

        // Assert: SelectAll on empty text gives start=0, end=0.
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal(0, commandBox.SelectionEnd);
        Assert.False(GetShouldSelectAllOnNextInput(panel));
    }

    [AvaloniaFact]
    public void MarkForSelectAllOnNextInput_SetsFlag()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);

        Assert.False(GetShouldSelectAllOnNextInput(panel));

        // Act
        panel.MarkForSelectAllOnNextInput();

        // Assert
        Assert.True(GetShouldSelectAllOnNextInput(panel));
    }

    [AvaloniaFact]
    public void FocusCommandBoxAndSelectAll_ClearsMark()
    {
        // The public FocusCommandBoxAndSelectAll should also clear the
        // select-all-on-next-input flag.
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        panel.MarkForSelectAllOnNextInput();
        Assert.True(GetShouldSelectAllOnNextInput(panel));

        // Act
        panel.FocusCommandBoxAndSelectAll();

        // Assert: mark cleared.
        Assert.False(GetShouldSelectAllOnNextInput(panel));
    }

    // ==================================================================
    // IsCommandBox — non-terminal TextBoxes are not affected
    // ==================================================================

    [AvaloniaFact]
    public void IsCommandBox_ReturnsTrueForOwnCommandBox()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);

        // Act / Assert
        Assert.True(panel.IsCommandBox(commandBox));
    }

    [AvaloniaFact]
    public void IsCommandBox_ReturnsFalseForOtherTextBox()
    {
        // Arrange: create a non-terminal TextBox (e.g. Host box).
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);

        // Find the Host TextBox in the top bar (not the command box).
        var hostBox = window
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Name is null && t.PlaceholderText == "host" && t.IsEffectivelyVisible);
        Assert.NotNull(hostBox);

        // Act / Assert: IsCommandBox returns false for it.
        Assert.False(panel.IsCommandBox(hostBox));
    }

    // ==================================================================
    // ClearSelectAllOnNextInput (public API on TerminalPanelView)
    // ==================================================================

    [AvaloniaFact]
    public void ClearSelectAllOnNextInput_ClearsFlag()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        panel.MarkForSelectAllOnNextInput();
        Assert.True(GetShouldSelectAllOnNextInput(panel));

        // Act
        panel.ClearSelectAllOnNextInput();

        // Assert
        Assert.False(GetShouldSelectAllOnNextInput(panel));
    }

    [AvaloniaFact]
    public void ClearSelectAllOnNextInput_WhenNotSet_StaysFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        Assert.False(GetShouldSelectAllOnNextInput(panel));

        // Act
        panel.ClearSelectAllOnNextInput();

        // Assert
        Assert.False(GetShouldSelectAllOnNextInput(panel));
    }

    // ==================================================================
    // OnWindowActivated — selects after the OS restores the command-box caret
    // ==================================================================

    [AvaloniaFact]
    public void OnWindowActivated_BeforeCommandBoxFocusIsRestored_SelectsAllAfterRestoration()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);
        var commandBox = GetCommandBox(panel);
        commandBox.Text = "look north";
        window.Focus();
        Assert.False(commandBox.IsFocused);
        commandBox.SelectionStart = commandBox.Text.Length;
        commandBox.SelectionEnd = commandBox.Text.Length;
        Assert.False(GetShouldSelectAllOnNextInput(panel));

        // Act: Activated arrives before Avalonia restores logical focus to the input.
        InvokeOnWindowActivated(window);
        commandBox.Focus();
        Assert.True(commandBox.IsFocused);
        commandBox.SelectionStart = commandBox.Text.Length;
        commandBox.SelectionEnd = commandBox.Text.Length;
        Dispatcher.UIThread.RunJobs();

        // Assert: the deferred selection wins over caret restoration.
        Assert.Equal(0, commandBox.SelectionStart);
        Assert.Equal("look north".Length, commandBox.SelectionEnd);
        Assert.False(GetShouldSelectAllOnNextInput(panel));
    }

    [AvaloniaFact]
    public void OnWindowActivated_WithNonCommandBoxFocused_DoesNotSetMark()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);

        // Find a non-terminal TextBox (e.g. Host box).
        var hostBox = window
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Name is null && t.PlaceholderText == "host" && t.IsEffectivelyVisible);
        Assert.NotNull(hostBox);
        hostBox.Focus();
        Assert.True(hostBox.IsFocused);
        Assert.False(GetShouldSelectAllOnNextInput(panel));

        // Act: simulate window activation while a non-terminal TextBox holds focus.
        InvokeOnWindowActivated(window);

        // Assert: mark must NOT be set — a stale flag would hijack the command box later.
        Assert.False(GetShouldSelectAllOnNextInput(panel),
            "Activation with non-terminal focus must not set the select-all mark.");
    }

    // ==================================================================
    // OnPreviewTextInput — clears pending mark for non-terminal TextBox
    // ==================================================================

    [AvaloniaFact]
    public void OnPreviewTextInput_WithNonTerminalTextBox_ClearsPendingMark()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        EnsureLayout(window);

        var panel = GetPanel(window);

        // Focus a non-terminal TextBox.
        var hostBox = window
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Name is null && t.PlaceholderText == "host" && t.IsEffectivelyVisible);
        Assert.NotNull(hostBox);
        hostBox.Focus();
        Assert.True(hostBox.IsFocused);

        // Set a pending mark (as if a stale flag exists from a previous activation).
        panel.MarkForSelectAllOnNextInput();
        Assert.True(GetShouldSelectAllOnNextInput(panel));

        var args = new TextInputEventArgs { Text = "h" };

        // Act: simulate text input arriving in the non-terminal TextBox.
        InvokeOnPreviewTextInput(window, args);

        // Assert: the stale mark is cleared.
        Assert.False(GetShouldSelectAllOnNextInput(panel),
            "Text input in a non-terminal TextBox must clear any pending select-all mark.");
    }
}
