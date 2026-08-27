using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>Covers <see cref="AutomationRuleEntry.RecentlyFired"/> — the gray-to-blue flash on the
/// timers/triggers status bar (TerminalPanelView) driven by
/// <see cref="MainWindowViewModel"/>'s FlashTriggerRecentlyFired, set from OnTriggerRuleMatched.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class TriggerRecentlyFiredTests
{
    private static (MainWindowViewModel ViewModel, string Directory) CreateViewModel()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_TriggerRecentlyFired_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (new MainWindowViewModel(settingsService: new AppSettingsService(directory)), directory);
    }

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

    [AvaloniaFact]
    public async Task TriggerMatches_TurnsRecentlyFiredOn()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            var trigger = new AutomationRuleEntry("boss", "trigger", "Zabijasz golema", "attack", isEnabled: true);
            viewModel.AutomationRules.Add(trigger);
            InvokeApplyAutomation(viewModel);
            Assert.False(trigger.RecentlyFired);

            InvokeOnLineReceived(viewModel, "Zabijasz golema.");
            Dispatcher.UIThread.RunJobs();

            Assert.True(trigger.RecentlyFired);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task NonMatchingLine_DoesNotTurnRecentlyFiredOn()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            var trigger = new AutomationRuleEntry("boss", "trigger", "Zabijasz golema", "attack", isEnabled: true);
            viewModel.AutomationRules.Add(trigger);
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Nic się nie dzieje.");
            Dispatcher.UIThread.RunJobs();

            Assert.False(trigger.RecentlyFired);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task UnrelatedTrigger_IsNotFlashedByAnotherOnesMatch()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            var matching = new AutomationRuleEntry("boss", "trigger", "Zabijasz golema", "attack", isEnabled: true);
            var other = new AutomationRuleEntry("heal", "trigger", "Leczysz sie", "heal", isEnabled: true);
            viewModel.AutomationRules.Add(matching);
            viewModel.AutomationRules.Add(other);
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Zabijasz golema.");
            Dispatcher.UIThread.RunJobs();

            Assert.True(matching.RecentlyFired);
            Assert.False(other.RecentlyFired);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task RecentlyFired_ClearsAfterDisplayDuration()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            var trigger = new AutomationRuleEntry("boss", "trigger", "Zabijasz golema", "attack", isEnabled: true);
            viewModel.AutomationRules.Add(trigger);
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Zabijasz golema.");
            Dispatcher.UIThread.RunJobs();
            Assert.True(trigger.RecentlyFired);

            // AutomationActivityDisplayDuration is 3 seconds — wait past it and pump the
            // dispatcher so the queued clear continuation actually runs.
            await Task.Delay(TimeSpan.FromSeconds(3.5));
            Dispatcher.UIThread.RunJobs();

            Assert.False(trigger.RecentlyFired);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task MatchingAgainWhileStillLit_RestartsTheTimerInsteadOfLettingItExpireEarly()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            var trigger = new AutomationRuleEntry("boss", "trigger", "Zabijasz golema", "attack", isEnabled: true);
            viewModel.AutomationRules.Add(trigger);
            InvokeApplyAutomation(viewModel);

            InvokeOnLineReceived(viewModel, "Zabijasz golema.");
            Dispatcher.UIThread.RunJobs();

            await Task.Delay(TimeSpan.FromSeconds(2));
            InvokeOnLineReceived(viewModel, "Zabijasz golema."); // re-fires with ~1s left on the first window
            Dispatcher.UIThread.RunJobs();

            await Task.Delay(TimeSpan.FromSeconds(2)); // first window would have expired by now
            Dispatcher.UIThread.RunJobs();

            Assert.True(trigger.RecentlyFired);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }
}
