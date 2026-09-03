using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>Covers the "/autoget" meta-command — sends <see cref="MainWindowViewModel.AutoGetCommandsText"/>
/// one command per line, in order (see <see cref="MainWindowViewModel.AutoGetCommand"/>'s backing
/// method), and its automatic trigger on an experience-gain line (see
/// MainWindowViewModel.OnLineReceived's ExperienceGainPolicy check — pure line-matching itself is
/// covered by ExperienceGainPolicyTests in MudClient.Core.Tests). Most lines are plain
/// fire-and-forget through the trigger queue like every other automation, but "exa"/"examine"
/// lines go through <see cref="AutoGetCoordinator"/>, which captures the response and looks for a
/// random magic-book name to "get" — that specific behavior is exercised here by injecting a
/// simulated MUD response into the same coordinator instance the view model uses (see
/// <see cref="CreateViewModel"/>), concurrently with the awaited "/autoget" call, the same way a
/// real reply would arrive mid-flight. The pure parsing/sequencing logic itself (no view model
/// needed) is covered in AutoGetCoordinatorTests.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoGetMetaCommandTests
{
    private static Task InvokeSendTriggeredCommandAsync(MainWindowViewModel viewModel, string command)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "SendTriggeredCommandAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, [command, CancellationToken.None]));
    }

    private static void PumpTriggerQueue()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void InvokeOnLineReceived(MainWindowViewModel viewModel, string line)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "OnLineReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [line]);
    }

    /// <summary>Polls until <paramref name="coordinator"/> is actively waiting on an "exa" response
    /// — safe to do before injecting a simulated line via <see cref="AutoGetCoordinator.TryCaptureLine"/>,
    /// since capturing starts (and stays true) well before the coordinator's own quiet-period/timeout
    /// window could have already elapsed with nothing captured.</summary>
    private static async Task WaitUntilCapturingAsync(AutoGetCoordinator coordinator)
    {
        for (var i = 0; i < 200 && !coordinator.IsCapturing; i++)
        {
            await Task.Delay(5);
        }

        Assert.True(coordinator.IsCapturing, "Coordinator never started capturing the exa response.");
    }

    [AvaloniaFact]
    public async Task TriggeredAutoGetCommand_NotConnected_ShowsErrorAndSendsNothing()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            viewModel.AutoGetEnabled = true;

            await InvokeSendTriggeredCommandAsync(viewModel, "/autoget");
            PumpTriggerQueue();

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("Nie połączono"));
            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredAutoGetCommand_NotArmedWithCheckbox_ShowsErrorAndSendsNothing()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            Assert.False(viewModel.AutoGetEnabled);

            await InvokeSendTriggeredCommandAsync(viewModel, "/autoget");
            PumpTriggerQueue();

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("Auto: Ekwipunek"));
            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredAutoGetCommand_EmptyCommandList_ShowsInfoToastAndSendsNothing()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoGetEnabled = true;
            viewModel.AutoGetCommandsText = "   ";

            await InvokeSendTriggeredCommandAsync(viewModel, "/autoget");
            PumpTriggerQueue();

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("pusta"));
            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredAutoGetCommand_ConnectedAndArmed_SendsEveryConfiguredLineInOrder()
    {
        var (viewModel, directory, coordinator) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoGetEnabled = true;
            viewModel.AutoGetCommandsText = "get all.klej cia\nexa cia\nput all.klej 2.tor";

            var task = InvokeSendTriggeredCommandAsync(viewModel, "/autoget");
            // A real MUD always echoes something for "exa" (even just a plain description) —
            // simulate that so the coordinator's capture wait resolves quickly instead of running
            // out its full timeout waiting for a response that, unlike a real session, never comes.
            await WaitUntilCapturingAsync(coordinator);
            coordinator.TryCaptureLine("Zwłoki wyglądają na już przeszukane.");

            await task;
            PumpTriggerQueue();

            var getIndex = output.FindIndex(line => line.Contains("> get all.klej cia", StringComparison.Ordinal));
            var exaIndex = output.FindIndex(line => line.Contains("> exa cia", StringComparison.Ordinal));
            var putIndex = output.FindIndex(line => line.Contains("> put all.klej 2.tor", StringComparison.Ordinal));

            Assert.True(getIndex >= 0 && exaIndex >= 0 && putIndex >= 0, "All three commands should have been echoed.");
            Assert.True(getIndex < exaIndex && exaIndex < putIndex, "Commands must be sent in the configured order.");
            Assert.False(coordinator.IsCapturing);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredAutoGetCommand_ExaResponseContainsRandomBook_GetsItFromTheExaminedTarget()
    {
        var (viewModel, directory, coordinator) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoGetEnabled = true;
            viewModel.AutoGetCommandsText = "exa cia\nput all.klej {klej}";

            var task = InvokeSendTriggeredCommandAsync(viewModel, "/autoget");
            await WaitUntilCapturingAsync(coordinator);
            coordinator.TryCaptureLine("Nosisz: duza ksiega triumfu.");

            await task;
            PumpTriggerQueue();

            Assert.Contains(output, l => l.Contains("> get duza ksiega triumfu cia", StringComparison.Ordinal));
            Assert.Contains(viewModel.Toasts, t =>
                t.Text.Contains("znaleziono i pobrano 1") && t.Text.Contains("duza ksiega triumfu"));

            // The injected "get" must land before the rest of the configured list continues.
            var getBookIndex = output.FindIndex(l => l.Contains("> get duza ksiega triumfu cia", StringComparison.Ordinal));
            var putIndex = output.FindIndex(l => l.Contains("> put all.klej 2.tor", StringComparison.Ordinal));
            Assert.True(getBookIndex >= 0 && putIndex >= 0 && getBookIndex < putIndex);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredAutoGetCommand_ExaResponseWithoutRandomBook_DoesNotSendGet()
    {
        var (viewModel, directory, coordinator) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoGetEnabled = true;
            viewModel.AutoGetCommandsText = "exa cia\nput all.klej {klej}";

            var task = InvokeSendTriggeredCommandAsync(viewModel, "/autoget");
            await WaitUntilCapturingAsync(coordinator);
            coordinator.TryCaptureLine("Nosisz: zardzewiały miecz.");

            await task;
            PumpTriggerQueue();

            Assert.DoesNotContain(output, l => l.Contains("> get ", StringComparison.Ordinal));
            Assert.DoesNotContain(viewModel.Toasts, t => t.Text.Contains("losowych ksiąg"));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task ExperienceGainLine_WhileArmedAndConnected_TriggersAutoGetAutomatically()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoGetEnabled = true;
            viewModel.AutoGetCommandsText = "get all.klej cia";

            InvokeOnLineReceived(viewModel, "Zabijasz golema. Zdobyłeś 500 punktów doświadczenia.");
            await Task.Delay(50);
            PumpTriggerQueue();

            Assert.Contains(output, l => l.Contains("> get all.klej cia", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task ExperienceGainLine_WhileNotArmed_DoesNothing()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            Assert.False(viewModel.AutoGetEnabled);

            InvokeOnLineReceived(viewModel, "Zdobyłeś 500 punktów doświadczenia.");
            await Task.Delay(50);
            PumpTriggerQueue();

            Assert.Empty(output);
            Assert.DoesNotContain(viewModel.Toasts, t => t.Text.Contains("/autoget"));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task ExperienceGainLine_ArmedButNotConnected_ShowsErrorToastInsteadOfSending()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            viewModel.AutoGetEnabled = true;

            InvokeOnLineReceived(viewModel, "Zdobyłeś 500 punktów doświadczenia.");
            await Task.Delay(50);
            PumpTriggerQueue();

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("Nie połączono"));
            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task UnrelatedLine_WhileArmedAndConnected_DoesNotTriggerAutoGet()
    {
        var (viewModel, directory, _) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoGetEnabled = true;
            viewModel.AutoGetCommandsText = "get all.klej cia";

            InvokeOnLineReceived(viewModel, "Golem uderza cię pięścią.");
            await Task.Delay(50);
            PumpTriggerQueue();

            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task AutoGetCommandsText_DefaultsToTheLootingSequenceTheFeatureWasModeledOn()
    {
        var (viewModel, directory, _) = CreateViewModel();
        try
        {
            var lines = viewModel.AutoGetCommandsText
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("get all.klej cia", lines);
            Assert.Contains("get all.gem cia", lines);
            Assert.Contains("exa cia", lines);
            Assert.Contains("put all.klej {klej}", lines);
            Assert.Contains("put all.gem {gem}", lines);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (MainWindowViewModel ViewModel, string Directory, AutoGetCoordinator Coordinator) CreateViewModel()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "KillerMudClient_AutoGet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var coordinator = new AutoGetCoordinator(
            quietPeriod: TimeSpan.FromMilliseconds(30),
            responseTimeout: TimeSpan.FromSeconds(3));
        var viewModel = new MainWindowViewModel(
            settingsService: new AppSettingsService(directory),
            autoGetCoordinator: coordinator);
        return (viewModel, directory, coordinator);
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
