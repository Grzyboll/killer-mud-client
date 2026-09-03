using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>Covers the "/repair" meta-command's wiring on <see cref="MainWindowViewModel"/> — the
/// gating (must be connected, must have <see cref="MainWindowViewModel.AutoRepairEnabled"/> checked
/// first in Auto: Ekwipunek) and that it's actually reachable through
/// <c>SendTriggeredCommandAsync</c> the same way "/recast"/"/reconnect" are (see
/// AutomationCommandEchoUiTests for those). The full "remove all" → repair → "wear all" sequencing
/// itself is covered directly against <see cref="AutoRepairCoordinator"/> in
/// AutoRepairCoordinatorTests, which doesn't need a live MUD connection.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoRepairMetaCommandTests
{
    private static Task InvokeSendTriggeredCommandAsync(MainWindowViewModel viewModel, string command)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "SendTriggeredCommandAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, [command, CancellationToken.None]));
    }

    [AvaloniaFact]
    public async Task TriggeredRepairCommand_NotConnected_ShowsErrorAndSendsNothing()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            viewModel.AutoRepairEnabled = true;

            await InvokeSendTriggeredCommandAsync(viewModel, "/repair");
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("Nie połączono"));
            Assert.DoesNotContain(output, line => line.Contains("> remove all", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredRepairCommand_NotArmedWithCheckbox_ShowsErrorAndSendsNothing()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            Assert.False(viewModel.AutoRepairEnabled);

            await InvokeSendTriggeredCommandAsync(viewModel, "/repair");
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("Auto: Ekwipunek"));
            Assert.DoesNotContain(output, line => line.Contains("> remove all", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredRepairCommand_ConnectedAndArmed_ReachesTheCoordinator()
    {
        // No real MUD is connected, so the coordinator's "remove all" capture never sees a
        // response and eventually times out (using a short timeout here so the test stays fast) —
        // this still proves the gating let the call through and the meta-command actually reached
        // AutoRepairCoordinator.RunAsync, which is what this test is for. The full success path is
        // covered directly against the coordinator in AutoRepairCoordinatorTests.
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoRepairEnabled = true;

            await InvokeSendTriggeredCommandAsync(viewModel, "/repair");
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("> remove all", StringComparison.Ordinal));
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("/repair"));
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
            "KillerMudClient_AutoRepair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var coordinator = new AutoRepairCoordinator(
            quietPeriod: TimeSpan.FromMilliseconds(20),
            responseTimeout: TimeSpan.FromMilliseconds(200));
        return (
            new MainWindowViewModel(
                settingsService: new AppSettingsService(directory),
                autoRepairCoordinator: coordinator),
            directory);
    }

    private static void SetConnected(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_isConnected",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(viewModel, true);
    }

    private static async Task DisposeAsync(MainWindowViewModel viewModel, string directory)
    {
        await viewModel.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }
}
