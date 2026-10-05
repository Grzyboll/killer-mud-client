using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class FollowWalkRecomputeTests
{
    // Same diamond as MainWindowViewModelTests: 1-north-2-north-3-north-4 the long way, plus a
    // direct 1-east-4 shortcut. From 1 the shortest route to 4 is one step east, the leader's
    // route is three steps north.
    private static MapRoom Room(int id, string vnum, params (string Name, int Target)[] exits) => new()
    {
        Id = id,
        AreaId = 1,
        Coordinates = new MapCoordinates(id, 0, 0),
        UserData = new Dictionary<string, JsonElement>
        {
            ["vnum"] = JsonSerializer.SerializeToElement(vnum),
        },
        Exits = exits.Select(e => new MapExit { ExitId = e.Target, Name = e.Name }).ToList(),
    };

    private static MapIndex DiamondIndex() => new(new MapDocument
    {
        Areas =
        [
            new MapArea
            {
                Id = 1,
                Rooms =
                [
                    Room(1, "1", ("north", 2), ("east", 4)),
                    Room(2, "2", ("south", 1), ("north", 3)),
                    Room(3, "3", ("south", 2), ("north", 4)),
                    Room(4, "4", ("south", 3), ("west", 1)),
                ],
            },
        ],
    });

    private static void InvokePrivate(MainWindowViewModel viewModel, string methodName, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, args);

    private static void SetPrivateField(object target, string fieldName, object? value) =>
        target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    [AvaloniaFact]
    public async Task FollowWalk_OffRoute_RecomputesAlongTheLeadersTrailNotTheShortcut()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_FollowRecompute_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));
        try
        {
            SetPrivateField(viewModel.Map, "_mapIndex", DiamondIndex());
            var trail = (List<string>)typeof(MainWindowViewModel)
                .GetField("_leaderRoomTrail", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(viewModel)!;
            trail.AddRange(["1", "2", "3", "4"]);

            // The follower is walking the shortest route 1->4 (one step east) when it ends up in
            // room 1 again instead — off the planned route, so the walk must recompute from there.
            var planned = new MapPath
            {
                From = DiamondIndex().FindFirstRoomByVnum("1")!,
                To = DiamondIndex().FindFirstRoomByVnum("4")!,
                Steps = [new MapPathStep("north", DiamondIndex().FindFirstRoomByVnum("2")!, null)],
                TotalCost = 1,
            };
            SetPrivateField(viewModel, "_autowalkPath", planned);
            SetPrivateField(viewModel, "_autowalkStep", 0);
            SetPrivateField(viewModel, "_autowalkTargetName", "leader");
            SetPrivateField(viewModel, "_autowalkIsFollowingLeader", true);
            SetPrivateField(viewModel, "_autowalkFollowTargetVnum", "4");
            SetPrivateField(viewModel, "_isConnected", true);

            InvokePrivate(viewModel, "OnAutowalkLocationChanged", "1");
            Dispatcher.UIThread.RunJobs();

            var recomputed = Assert.IsType<MapPath>(
                typeof(MainWindowViewModel).GetField("_autowalkPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(viewModel));
            Assert.Equal(["north", "north", "north"], recomputed.Steps.Select(step => step.Command));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
