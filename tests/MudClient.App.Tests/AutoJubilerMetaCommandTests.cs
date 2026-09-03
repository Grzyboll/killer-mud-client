using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;

namespace MudClient.App.Tests;

/// <summary>Covers the "/jubiler" meta-command — both its manual invocation (same shape as
/// "/autoget", see AutoGetMetaCommandTests) and its automatic trigger on entering a room named in
/// <see cref="MainWindowViewModel.AutoJubilerRoomNamesText"/> (see
/// <c>MainWindowViewModel.TryAutoJubiler</c>, wired into <c>OnRoomEnterAutomations</c> the same way
/// <c>MainWindowViewModelTests</c>' own OnRoomEnterAutomations tests exercise Autokill). Also covers
/// the shared "{klej}"/"{gem}" container placeholders both "/autoget" and "/jubiler" resolve
/// against.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoJubilerMetaCommandTests
{
    private static Task InvokeSendTriggeredCommandAsync(MainWindowViewModel viewModel, string command)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "SendTriggeredCommandAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, [command, CancellationToken.None]));
    }

    private static void InvokeOnRoomEnterAutomations(MainWindowViewModel viewModel, string vnum)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "OnRoomEnterAutomations", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [vnum]);
    }

    private static void PumpTriggerQueue()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TriggeredJubilerCommand_NotConnected_ShowsErrorAndSendsNothing()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            viewModel.AutoJubilerEnabled = true;

            await InvokeSendTriggeredCommandAsync(viewModel, "/jubiler");
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
    public async Task TriggeredJubilerCommand_NotArmedWithCheckbox_ShowsErrorAndSendsNothing()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            Assert.False(viewModel.AutoJubilerEnabled);

            await InvokeSendTriggeredCommandAsync(viewModel, "/jubiler");
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
    public async Task TriggeredJubilerCommand_ConnectedAndArmed_SubstitutesContainersAndSendsInOrder()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoJubilerEnabled = true;
            viewModel.AutoLootGlueContainerName = "3.worek";
            viewModel.AutoLootGemContainerName = "4.sakiewka";
            // Default AutoJubilerCommandsText already uses {klej}/{gem} — exercise it as-is.

            await InvokeSendTriggeredCommandAsync(viewModel, "/jubiler");
            PumpTriggerQueue();

            var remKlejIndex = output.FindIndex(l => l.Contains("> rem all.klej 3.worek", StringComparison.Ordinal));
            var sellKlejIndex = output.FindIndex(l => l.Contains("> sell all.klej", StringComparison.Ordinal));
            var remGemIndex = output.FindIndex(l => l.Contains("> rem all.gem 4.sakiewka", StringComparison.Ordinal));
            var sellGemIndex = output.FindIndex(l => l.Contains("> sell all.gem", StringComparison.Ordinal));

            Assert.True(remKlejIndex >= 0 && sellKlejIndex >= 0 && remGemIndex >= 0 && sellGemIndex >= 0,
                "All four commands should have been echoed with containers substituted.");
            Assert.True(remKlejIndex < sellKlejIndex && sellKlejIndex < remGemIndex && remGemIndex < sellGemIndex);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task EnteringAMatchingRoom_WhileArmed_FiresAutomatically()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoJubilerEnabled = true;
            ArrangeSingleRoomMap(viewModel, vnum: "500", roomName: "Jubiler");

            InvokeOnRoomEnterAutomations(viewModel, "500");
            PumpTriggerQueue();

            Assert.Contains(output, l => l.Contains("> rem all.klej 2.tor", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task EnteringAMatchingRoom_CaseAndDiacriticsInsensitive_StillFires()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoJubilerEnabled = true;
            // Default room list has "Złotnik" — enter a room whose mapped name differs only by
            // case/diacritics folding ("zlotnik", no diacritic, matching how the MUD's own text
            // never carries one — see PolishText.Fold).
            ArrangeSingleRoomMap(viewModel, vnum: "501", roomName: "zlotnik");

            InvokeOnRoomEnterAutomations(viewModel, "501");
            PumpTriggerQueue();

            Assert.Contains(output, l => l.Contains("> sell all.gem", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task EnteringAnUnrelatedRoom_DoesNotFire()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            viewModel.AutoJubilerEnabled = true;
            ArrangeSingleRoomMap(viewModel, vnum: "502", roomName: "Plac targowy");

            InvokeOnRoomEnterAutomations(viewModel, "502");
            PumpTriggerQueue();

            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [AvaloniaFact]
    public async Task EnteringAMatchingRoom_WhileNotArmed_DoesNotFire()
    {
        var (viewModel, directory) = CreateViewModel();
        var output = new List<string>();
        viewModel.OutputReceived += output.Add;

        try
        {
            SetConnected(viewModel);
            Assert.False(viewModel.AutoJubilerEnabled);
            ArrangeSingleRoomMap(viewModel, vnum: "503", roomName: "Jubiler");

            InvokeOnRoomEnterAutomations(viewModel, "503");
            PumpTriggerQueue();

            Assert.Empty(output);
        }
        finally
        {
            await DisposeAsync(viewModel, directory);
        }
    }

    [Fact]
    public async Task AutoJubilerRoomNamesText_DefaultsToJubilerAndZlotnik()
    {
        var (viewModel, directory) = CreateViewModel();
        try
        {
            var names = viewModel.AutoJubilerRoomNamesText
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("Jubiler", names);
            Assert.Contains("Złotnik", names);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ArrangeSingleRoomMap(MainWindowViewModel viewModel, string vnum, string roomName)
    {
        var document = new MapDocument
        {
            Areas =
            [
                new MapArea
                {
                    Id = 1,
                    Rooms =
                    [
                        new MapRoom
                        {
                            Id = 1,
                            AreaId = 1,
                            Name = roomName,
                            Coordinates = new MapCoordinates(0, 0, 0),
                            UserData = new Dictionary<string, System.Text.Json.JsonElement>
                            {
                                ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement(vnum),
                            },
                        },
                    ],
                },
            ],
        };

        typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
            .SetValue(viewModel.Map, new MapIndex(document));
    }

    private static (MainWindowViewModel ViewModel, string Directory) CreateViewModel()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "KillerMudClient_AutoJubiler_" + Guid.NewGuid().ToString("N"));
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
