using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;

namespace MudClient.App.Tests;

/// <summary>Covers the "rest" sent automatically when autowalk reaches its destination — now
/// gated behind <see cref="MainWindowViewModel.AutowalkRestOnArrivalEnabled"/> instead of always
/// firing. OnAutowalkLocationChanged does its work inside a Dispatcher.UIThread.Post, so these
/// need a real headless dispatcher pump (a plain xUnit test class never runs posted work).</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutowalkArrivalTests
{
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

    /// <summary>Arranges a one-step autowalk about to arrive at "999", then reports that room
    /// change — triggering the arrival branch inside OnAutowalkLocationChanged.</summary>
    private static void ArriveAtDestination(MainWindowViewModel viewModel)
    {
        var from = CreateRoom(998, "998");
        var to = CreateRoom(999, "999");
        typeof(MainWindowViewModel).GetField("_autowalkPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, new MapPath
            {
                From = from,
                To = to,
                Steps = [new MapPathStep("north", to)],
                TotalCost = 1,
            });
        typeof(MainWindowViewModel).GetField("_autowalkStep", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, 0);
        typeof(MainWindowViewModel).GetField("_autowalkTargetName", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, "Cel");

        var method = typeof(MainWindowViewModel).GetMethod(
            "OnAutowalkLocationChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(viewModel, ["999"]);

        // OnAutowalkLocationChanged's body, and SendTriggeredCommandAsync's echo inside it, are
        // each queued via Dispatcher.UIThread.Post — drain twice to run both layers.
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Arrival_WithRestEnabled_SendsRest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_AutowalkArrivalTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            Assert.True(viewModel.AutowalkRestOnArrivalEnabled);
            ArriveAtDestination(viewModel);

            Assert.Contains(output, line => line.Contains("> rest"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Arrival_WithRestDisabled_DoesNotSendRest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_AutowalkArrivalTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            viewModel.AutowalkRestOnArrivalEnabled = false;
            ArriveAtDestination(viewModel);

            Assert.DoesNotContain(output, line => line.Contains("> rest"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
