using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudClient.App.Controls;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views.Panels;
using Xunit;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class MudOutputViewTests
{
    [AvaloniaFact]
    public void SearchBox_IsOnRightSideOfTerminalInput()
    {
        var terminal = new TerminalPanelView();
        var commandBox = terminal.FindControl<TextBox>("CommandBox");
        var searchBox = terminal.FindControl<TextBox>("SearchBox");

        Assert.NotNull(commandBox);
        Assert.NotNull(searchBox);
        Assert.Equal("Wyszukaj...", searchBox!.PlaceholderText);
        Assert.Same(commandBox!.Parent, searchBox.Parent);
        Assert.Equal(0, Grid.GetColumn(commandBox));
        Assert.Equal(1, Grid.GetColumn(searchBox));
    }

    [AvaloniaFact]
    public async Task TimerStrip_RendersEnabledTimer_AndButtonsResolveRealCommands()
    {
        // The timer strip moved from a floating overlay atop the output (where pinned overlay
        // columns used to cover it) into a compact bar just above the command box. Verifying a
        // Command-bound button (unlike a Click="..." code-behind one) means confirming its Command
        // and CommandParameter resolve to the right instances and that executing them produces the
        // right effect — RaiseEvent(Button.ClickEvent) does NOT exercise Avalonia's own
        // Command-invocation path (that only runs from real pointer/keyboard input, inside
        // Button.OnClick — raising the event from outside just notifies external Click="..."
        // subscribers, which these buttons don't have), so it would silently prove nothing here.
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_TimerStripUiTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));
        var timer = new TimerEntry { Name = "Refresh", Seconds = 5, IsEnabled = true, CommandsText = "look" };
        viewModel.Timers.Add(timer);

        var terminal = new TerminalPanelView { DataContext = viewModel };
        var window = new Window { Width = 800, Height = 500, Content = terminal };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();

        try
        {
            var nameLabel = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(textBlock => textBlock.Text == "Refresh");
            Assert.True(nameLabel.IsEffectivelyVisible);

            var restartButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "⟳") && ReferenceEquals(button.DataContext, timer));
            Assert.Same(viewModel.RestartTimerCommand, restartButton.Command);
            Assert.Same(timer, restartButton.CommandParameter);
            restartButton.Command!.Execute(restartButton.CommandParameter);
            Assert.Contains(viewModel.Toasts, toast => toast.Text.Contains("zresetowany"));

            var pauseButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "⏸") && ReferenceEquals(button.DataContext, timer));
            Assert.Same(viewModel.ToggleTimerCommand, pauseButton.Command);
            Assert.Same(timer, pauseButton.CommandParameter);
            pauseButton.Command!.Execute(pauseButton.CommandParameter);
            window.UpdateLayout();

            // The row must stay put after pausing (not disappear) — that's the whole point of the
            // ▶ resume button existing.
            Assert.False(timer.IsEnabled);
            Assert.True(nameLabel.IsEffectivelyVisible);
            var resumeButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "▶") && ReferenceEquals(button.DataContext, timer));
            Assert.True(resumeButton.IsEffectivelyVisible);
            Assert.Same(viewModel.ToggleTimerCommand, resumeButton.Command);
            resumeButton.Command!.Execute(resumeButton.CommandParameter);

            Assert.True(timer.IsEnabled);
        }
        finally
        {
            window.Close();
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AddToast_RemovesItselfAfterLifetime()
    {
        // Uses the headless dispatcher's RunJobs to actually drain the Dispatcher.UIThread.Post
        // the removal is scheduled through — a plain (non-Avalonia) xUnit test never pumps that
        // queue, so this needs to live in an [AvaloniaFact] test.
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_ToastLifetimeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));

        var lifetimeField = typeof(MainWindowViewModel).GetField("ToastLifetime",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(lifetimeField);
        var original = (TimeSpan)lifetimeField!.GetValue(null)!;
        lifetimeField.SetValue(null, TimeSpan.FromMilliseconds(20));

        try
        {
            var addToast = typeof(MainWindowViewModel).GetMethod("AddToast",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(addToast);
            addToast!.Invoke(viewModel, ["test toast", "info"]);

            Assert.Contains(viewModel.Toasts, toast => toast.Text == "test toast");

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(viewModel.Toasts, toast => toast.Text == "test toast");
        }
        finally
        {
            lifetimeField.SetValue(null, original);
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task VitalsBars_HideAndReleaseTheirTerminalColumns()
    {
        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "KillerMudClient_VitalsUiTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDirectory);
        var viewModel = new MainWindowViewModel(
            settingsService: new AppSettingsService(settingsDirectory));
        var terminal = new TerminalPanelView { DataContext = viewModel };
        var window = new Window { Width = 800, Height = 500, Content = terminal };
        window.Show();

        try
        {
            var hpBar = terminal.FindControl<Border>("HitPointsBar")!;
            var mvBar = terminal.FindControl<Border>("MovementPointsBar")!;
            var output = terminal.FindControl<MudOutputView>("MudOutput")!;
            var widthWithBars = output.Bounds.Width;

            Assert.True(hpBar.IsVisible);
            Assert.True(mvBar.IsVisible);

            viewModel.ShowTerminalVitalsBars = false;
            window.UpdateLayout();

            Assert.False(hpBar.IsVisible);
            Assert.False(mvBar.IsVisible);
            Assert.True(output.Bounds.Width > widthWithBars + 80);
        }
        finally
        {
            window.Close();
            await viewModel.DisposeAsync();
            Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void WordWrap_DisablesHorizontalScrollbars()
    {
        var output = new MudOutputView { WordWrap = true };

        Assert.Equal(
            ScrollBarVisibility.Disabled,
            output.FindControl<ScrollViewer>("ScrollbackScroller")!.HorizontalScrollBarVisibility);

        output.WordWrap = false;

        Assert.Equal(
            ScrollBarVisibility.Auto,
            output.FindControl<ScrollViewer>("ScrollbackScroller")!.HorizontalScrollBarVisibility);
    }

    [AvaloniaFact]
    public void CloseSplitButton_ReturnsOutputToSinglePane()
    {
        var output = new MudOutputView();
        var splitBar = output.FindControl<Grid>("SplitBar");
        var liveTail = output.FindControl<ScrollViewer>("LiveTailScroller");
        var closeButton = output.FindControl<Button>("CloseSplitButton");

        Assert.NotNull(splitBar);
        Assert.NotNull(liveTail);
        Assert.NotNull(closeButton);

        SetSplitMode(output, true);
        Assert.True(splitBar!.IsVisible);
        Assert.True(liveTail!.IsVisible);

        closeButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(splitBar.IsVisible);
        Assert.False(liveTail.IsVisible);
    }

    [AvaloniaFact]
    public void Search_SelectsNewestMatch_AndMovesThroughOlderMatches()
    {
        var output = new MudOutputView();
        output.AppendText("pierwszy smok\nbez trafienia\nostatni smok\n");

        Assert.True(output.Search("SMOK"));
        Assert.Equal("smok", output.SelectedSearchText);
        Assert.Equal(2, output.SelectedSearchGlobalLine);

        Assert.True(output.Search("smok"));
        Assert.Equal("smok", output.SelectedSearchText);
        Assert.Equal(0, output.SelectedSearchGlobalLine);

        var splitBar = output.FindControl<Grid>("SplitBar");
        Assert.True(splitBar!.IsVisible);
    }

    [AvaloniaFact]
    public void SearchNavigationButtons_StepThroughMatchesLikeEnterAndShiftEnter()
    {
        var terminal = new TerminalPanelView();
        var output = terminal.FindControl<MudOutputView>("MudOutput")!;
        var searchBox = terminal.FindControl<TextBox>("SearchBox")!;
        var prevButton = terminal.FindControl<Button>("SearchPrevButton")!;
        var nextButton = terminal.FindControl<Button>("SearchNextButton")!;

        // TerminalPanelView seeds the output with a greeting on construction, so match
        // positions are asserted relative to each other rather than as absolute line numbers.
        output.AppendText("pierwszy smok\nbez trafienia\nostatni smok\n");
        searchBox.Text = "smok";
        Assert.True(output.UpdateSearch("smok"));
        var newestMatchLine = output.SelectedSearchGlobalLine;
        Assert.NotNull(newestMatchLine);

        prevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var olderMatchLine = output.SelectedSearchGlobalLine;
        Assert.NotNull(olderMatchLine);
        Assert.True(olderMatchLine < newestMatchLine);

        nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(newestMatchLine, output.SelectedSearchGlobalLine);
    }

    [AvaloniaFact]
    public void TerminalWheelScroll_UsesFourLineLogicalStep()
    {
        var pane = new OutputPaneControl();
        var logicalScrollable = (ILogicalScrollable)pane;

        Assert.True(logicalScrollable.ScrollSize.Height > 40);
    }

    [AvaloniaFact]
    public async Task TerminalCtrlC_CopiesOutputSelection_AndFallsBackToCommandInput()
    {
        var terminal = new TerminalPanelView();
        var commandBox = terminal.FindControl<TextBox>("CommandBox")!;
        var output = terminal.FindControl<MudOutputView>("MudOutput")!;
        var window = new Window { Content = terminal };
        window.Show();
        var clipboard = Assert.IsAssignableFrom<IClipboard>(window.Clipboard);

        try
        {
            commandBox.Text = "tekst inputu";
            commandBox.SelectAll();
            commandBox.Focus();

            output.AppendText("tekst terminala\n");
            Assert.True(output.UpdateSearch("tekst terminala"));

            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

            Assert.Equal("tekst terminala", await clipboard.TryGetTextAsync());

            output.Clear();
            commandBox.SelectAll();

            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

            Assert.Equal("tekst inputu", await clipboard.TryGetTextAsync());
        }
        finally
        {
            window.Close();
        }
    }

    private static void SetSplitMode(MudOutputView output, bool enabled)
    {
        var method = typeof(MudOutputView).GetMethod(
            "SetSplitMode",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(output, [enabled]);
    }

    private static object GetBuffer(MudOutputView output)
    {
        var field = typeof(MudOutputView).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!.GetValue(output)!;
    }

    private static int GetBufferCapacity(object buffer) =>
        (int)buffer.GetType().GetProperty("Capacity")!.GetValue(buffer)!;

    private static int GetBufferCount(object buffer) =>
        (int)buffer.GetType().GetProperty("Count")!.GetValue(buffer)!;

    // ====================================================================
    // MaxOutputLines — "Ile linijek pamięta terminal" (Ustawienia → Tekst z MUD-a)
    // ====================================================================

    [AvaloniaFact]
    public void MaxOutputLines_Changed_ReallocatesBufferWithNewCapacity()
    {
        var view = new MudOutputView();

        view.MaxOutputLines = 500;

        Assert.Equal(500, GetBufferCapacity(GetBuffer(view)));
    }

    [AvaloniaFact]
    public void MaxOutputLines_SetToSameValue_IsANoOp()
    {
        var view = new MudOutputView();
        var originalBuffer = GetBuffer(view);

        view.MaxOutputLines = view.MaxOutputLines;

        Assert.Same(originalBuffer, GetBuffer(view));
    }

    [AvaloniaFact]
    public void MaxOutputLines_Changed_ClearsExistingScrollback()
    {
        // A deliberate trade-off, not a bug — see MudOutputView.SetMaxLines' own xmldoc for why
        // resizing the ring in place isn't done instead.
        var view = new MudOutputView();
        view.AppendText("linia jeden\nlinia dwa\n");

        view.MaxOutputLines = 500;

        Assert.Equal(1, GetBufferCount(GetBuffer(view)));
    }
}
