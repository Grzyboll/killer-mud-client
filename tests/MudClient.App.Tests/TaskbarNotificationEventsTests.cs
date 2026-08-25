using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>Covers the ViewModel-level events that drive the taskbar-overlay notification badge
/// (see <see cref="MainWindow"/>'s OnCombatStateChangedForFlash/OnAutomationFiredForFlash/
/// OnChatLineReceivedForFlash and <see cref="TaskbarOverlayIconService"/>) — this file only proves
/// each event fires (or doesn't) at the right moment; the actual badge rendering is covered by
/// TaskbarOverlayIconServiceTests.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class TaskbarNotificationEventsTests
{
    [AvaloniaFact]
    public async Task CombatStateChanged_FightingStartsThenEnds_RaisesTrueThenFalse()
    {
        var (viewModel, directory) = CreateViewModel();
        var states = new List<bool>();
        viewModel.CombatStateChanged += states.Add;

        try
        {
            InvokeUpdateCharacterPosition(viewModel, "fighting");
            Dispatcher.UIThread.RunJobs();
            InvokeUpdateCharacterPosition(viewModel, "standing");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([true, false], states);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task CombatStateChanged_NoPositionChange_DoesNotRaiseAgain()
    {
        var (viewModel, directory) = CreateViewModel();
        var raiseCount = 0;
        viewModel.CombatStateChanged += _ => raiseCount++;

        try
        {
            InvokeUpdateCharacterPosition(viewModel, "fighting");
            Dispatcher.UIThread.RunJobs();
            InvokeUpdateCharacterPosition(viewModel, "fighting");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, raiseCount);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task AutomationFired_TriggerMatches_RaisesWithRuleName()
    {
        var (viewModel, directory) = CreateViewModel();
        var fired = new List<string>();
        viewModel.AutomationFired += fired.Add;

        try
        {
            viewModel.AutomationRules.Add(new AutomationRuleEntry(
                "boss", "trigger", "Zabijasz golema", "attack", isEnabled: true));
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Zabijasz golema.");
            Dispatcher.UIThread.RunJobs();

            var description = Assert.Single(fired);
            Assert.Contains("boss", description, StringComparison.Ordinal);
            Assert.Equal(description, viewModel.RecentAutomationActivityText);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task AutomationFired_NonMatchingLine_DoesNotRaise()
    {
        var (viewModel, directory) = CreateViewModel();
        var fired = 0;
        viewModel.AutomationFired += _ => fired++;

        try
        {
            viewModel.AutomationRules.Add(new AutomationRuleEntry(
                "boss", "trigger", "Zabijasz golema", "attack", isEnabled: true));
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Nic się nie dzieje.");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, fired);
            Assert.Null(viewModel.RecentAutomationActivityText);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task RecentAutomationActivityText_ClearsAfterDisplayDuration()
    {
        var (viewModel, directory) = CreateViewModel();

        try
        {
            viewModel.AutomationRules.Add(new AutomationRuleEntry(
                "boss", "trigger", "Zabijasz golema", "attack", isEnabled: true));
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Zabijasz golema.");
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(viewModel.RecentAutomationActivityText);

            // AutomationActivityDisplayDuration is 4 seconds — wait past it and pump the
            // dispatcher so the queued clear continuation actually runs.
            await Task.Delay(TimeSpan.FromSeconds(4.5));
            Dispatcher.UIThread.RunJobs();

            Assert.Null(viewModel.RecentAutomationActivityText);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task AutomationFired_TimerTickWithCommands_RaisesWithTimerName()
    {
        var (viewModel, directory) = CreateViewModel();
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.AutomationFired += description => fired.TrySetResult(description);

        try
        {
            SetConnected(viewModel);
            var timer = new TimerEntry
            {
                Name = "Obserwacja",
                Milliseconds = 10,
                CommandsText = "spojrz",
                IsEnabled = true,
            };

            var method = typeof(MainWindowViewModel).GetMethod(
                "SyncTimer", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method!.Invoke(viewModel, [timer]);

            var description = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("Obserwacja", description, StringComparison.Ordinal);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    private static (MainWindowViewModel ViewModel, string Directory) CreateViewModel()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "KillerMudClient_TaskbarNotificationEvents_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (new MainWindowViewModel(settingsService: new AppSettingsService(directory)), directory);
    }

    private static void SetConnected(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_isConnected", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(viewModel, true);
    }

    private static void InvokeUpdateCharacterPosition(MainWindowViewModel viewModel, string position) =>
        typeof(MainWindowViewModel)
            .GetMethod("UpdateCharacterPosition", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [position]);

    private static void InvokeOnLineReceived(MainWindowViewModel viewModel, string line) =>
        typeof(MainWindowViewModel)
            .GetMethod("OnLineReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [line]);

    private static void InvokeApplyAutomation(MainWindowViewModel viewModel) =>
        typeof(MainWindowViewModel)
            .GetMethod("ApplyAutomation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null);

    private static async ValueTask DisposeAsync(MainWindowViewModel viewModel, string directory)
    {
        await viewModel.DisposeAsync();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
