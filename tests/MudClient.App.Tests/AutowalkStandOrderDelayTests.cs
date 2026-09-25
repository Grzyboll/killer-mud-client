using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Regression test for a race where, when autowalk stood the character back up, it resumed
/// walking to the next room immediately — before the group's own "order &lt;name&gt; stand"
/// (queued on the same standing transition, see TryAutoOrderGroupPosition) had actually reached
/// and been processed by each follower, leaving them behind in a different room entirely. Fixed
/// with a short delay before resuming when AutoStandOrderEnabled is on.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutowalkStandOrderDelayTests
{
    private static void SetPrivateField(MainWindowViewModel viewModel, string fieldName, object? value) =>
        typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, value);

    private static MapRoom CreateRoom(int id, string vnum) => new()
    {
        Id = id,
        AreaId = 1,
        Coordinates = new MapCoordinates(0, 0, 0),
        UserData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement(vnum),
        },
    };

    private static void ArrangeStandingRecovery(MainWindowViewModel viewModel)
    {
        var from = CreateRoom(1, "1");
        var to = CreateRoom(2, "2");
        SetPrivateField(viewModel, "_autowalkPath", new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("north", to)],
            TotalCost = 1,
        });
        SetPrivateField(viewModel, "_autowalkStep", 0);
        SetPrivateField(viewModel, "_autowalkTargetName", "Cel");
        SetPrivateField(viewModel, "_autowalkRecoveringPosition", true);
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStanding_AutoStandOrderEnabled_DoesNotMoveImmediately()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_StandOrderDelayTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            viewModel.AutoStandOrderEnabled = true;
            ArrangeStandingRecovery(viewModel);

            var method = typeof(MainWindowViewModel).GetMethod(
                "HandleAutowalkStanding", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(viewModel, null);
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(output, line => line.Contains("> north"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStanding_AutoStandOrderDisabled_MovesImmediately()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_StandOrderDelayTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            viewModel.AutoStandOrderEnabled = false;
            ArrangeStandingRecovery(viewModel);

            var method = typeof(MainWindowViewModel).GetMethod(
                "HandleAutowalkStanding", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(viewModel, null);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("> north"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
