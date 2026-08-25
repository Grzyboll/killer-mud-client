using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class AutomationCommandEchoUiTests
{
    [AvaloniaFact]
    public async Task TriggerCommand_IsEchoedToTerminal()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            var method = typeof(MainWindowViewModel).GetMethod(
                "SendTriggeredCommandAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            await Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, ["wstan", CancellationToken.None]));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("> wstan", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredRecastCommand_ExpandsToMissingBuffCasts_InsteadOfRawText()
    {
        // "/recast" is a client-side meta-command (see SendCurrentCommandAsync) — automations
        // that produce it as a literal string (e.g. AutoRecastOnLeaderSnapCommandsText) must get
        // the same expansion SendTriggeredCommandAsync now applies, or the MUD receives "/recast"
        // as raw text it doesn't understand.
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            var buffSet = new BuffSetEntry { Name = "Domyślny" };
            buffSet.Buffs.Add(new BuffWatchEntry("armor"));
            viewModel.BuffSets.Add(buffSet);
            viewModel.SelectedBuffSet = buffSet;

            var method = typeof(MainWindowViewModel).GetMethod(
                "SendTriggeredCommandAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            await Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, ["/recast", CancellationToken.None]));
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(output, line => line.Contains("> /recast", StringComparison.Ordinal));
            Assert.Contains(output, line => line.Contains("> cast \"armor\" self", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredRecastCommand_SkipsBuffsAlreadyActive()
    {
        // RecastMissingBuffsAsync is meant to only cast RequiredBuffs where IsActive is false
        // (see MainWindowViewModel.RecastMissingBuffsAsync) — a buff already reported active by
        // Char.Affects must not be recast alongside the genuinely missing ones.
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            var buffSet = new BuffSetEntry { Name = "Domyślny" };
            buffSet.Buffs.Add(new BuffWatchEntry("armor") { IsActive = true });
            buffSet.Buffs.Add(new BuffWatchEntry("bless") { IsActive = false });
            viewModel.BuffSets.Add(buffSet);
            viewModel.SelectedBuffSet = buffSet;

            var method = typeof(MainWindowViewModel).GetMethod(
                "SendTriggeredCommandAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            await Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, ["/recast", CancellationToken.None]));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("> cast \"bless\" self", StringComparison.Ordinal));
            Assert.DoesNotContain(output, line => line.Contains("> cast \"armor\" self", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredReconnectCommand_WhileBusy_ShowsErrorAndDoesNotTouchConnection()
    {
        // The busy guard in ReconnectCurrentProfileAsync must short-circuit before touching
        // _session at all — otherwise this test would need a real/loopback connection just to
        // prove the guard works.
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            typeof(MainWindowViewModel).GetField("_isBusy", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(viewModel, true);

            var method = typeof(MainWindowViewModel).GetMethod(
                "SendTriggeredCommandAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            await Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, ["/reconnect", CancellationToken.None]));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("zajęty", StringComparison.Ordinal));
            Assert.DoesNotContain(output, line => line.Contains("> /reconnect", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TriggeredReconnectCommand_ReopensCurrentConnection()
    {
        // "/reconnect" (see MainWindowViewModel.ReconnectCurrentProfileAsync) disconnects and
        // reconnects using the current profile's host/port. MudSession has no injectable transport
        // seam (see MainWindowViewModelTests' "Auto-connect startup — validation notes"), so this
        // exercises the real connect/disconnect cycle over a loopback TCP listener instead of
        // mocking _session.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (viewModel, directory) = CreateViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TcpClient? firstClient = null;
        TcpClient? secondClient = null;

        try
        {
            viewModel.Host = IPAddress.Loopback.ToString();
            viewModel.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var firstAccept = listener.AcceptTcpClientAsync(timeout.Token);

            await viewModel.ConnectCommand.ExecuteAsync(null);
            firstClient = await firstAccept;
            Assert.True(viewModel.IsConnected);

            var secondAccept = listener.AcceptTcpClientAsync(timeout.Token);

            var method = typeof(MainWindowViewModel).GetMethod(
                "SendTriggeredCommandAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            await Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, ["/reconnect", CancellationToken.None]));

            secondClient = await secondAccept;
            Assert.True(viewModel.IsConnected);
        }
        finally
        {
            firstClient?.Dispose();
            secondClient?.Dispose();
            listener.Stop();
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TimerCommand_IsEchoedToTerminal()
    {
        var (viewModel, directory) = CreateViewModel();
        var echoReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.OutputReceived += text =>
        {
            if (text.Contains("> spojrz", StringComparison.Ordinal))
            {
                echoReceived.TrySetResult(text);
            }
        };

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
                "SyncTimer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method!.Invoke(viewModel, [timer]);

            var echo = await echoReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("> spojrz", echo, StringComparison.Ordinal);
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
            "KillerMudClient_AutomationEcho_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (new MainWindowViewModel(settingsService: new AppSettingsService(directory)), directory);
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
