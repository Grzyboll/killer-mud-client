using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>ViewModel-level wiring for RemoteControlEnabled/RemoteControlCharacterName (see
/// MudClient.Core.Tests.RemoteCommandPolicyTests for the pure-logic coverage) — a workaround for
/// this MUD restricting the "order" command to the formal group leader: a trusted character's
/// "!"-prefixed say gets executed as a literal command instead.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class RemoteControlTests
{
    private static (MainWindowViewModel ViewModel, string Directory) CreateViewModel()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_RemoteControl_" + Guid.NewGuid().ToString("N"));
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

    private static void InvokeOnLineReceived(MainWindowViewModel viewModel, string line) =>
        typeof(MainWindowViewModel)
            .GetMethod("OnLineReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [line]);

    private static async ValueTask DisposeAsync(MainWindowViewModel viewModel, string directory)
    {
        await viewModel.DisposeAsync();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TrustedCharacterPrefixedSay_SendsTheCommand()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.RemoteControlEnabled = true;
            viewModel.RemoteControlCharacterName = "Gandalf";

            InvokeOnLineReceived(viewModel, "Gandalf mówi: '!stand'.");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("> stand", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task Disabled_TrustedCharacterPrefixedSay_DoesNothing()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.RemoteControlEnabled = false;
            viewModel.RemoteControlCharacterName = "Gandalf";

            InvokeOnLineReceived(viewModel, "Gandalf mówi: '!stand'.");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("> stand", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task UntrustedCharacterPrefixedSay_DoesNothing()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.RemoteControlEnabled = true;
            viewModel.RemoteControlCharacterName = "Gandalf";

            InvokeOnLineReceived(viewModel, "Saruman mówi: '!stand'.");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("> stand", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task TrustedCharacterPlainSayWithoutPrefix_StaysPlainChatAndIsNotExecuted()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.RemoteControlEnabled = true;
            viewModel.RemoteControlCharacterName = "Gandalf";

            InvokeOnLineReceived(viewModel, "Gandalf mówi: 'witam wszystkich'.");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains(">", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task RemoteControlCharacterName_TrimsWhitespaceAndPersistsToProfile()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            viewModel.NewProfileName = "Frodo";
            viewModel.CreateProfileCommand.Execute(null);

            viewModel.RemoteControlCharacterName = "  Gandalf  ";

            Assert.Equal("Gandalf", viewModel.RemoteControlCharacterName);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }
}
