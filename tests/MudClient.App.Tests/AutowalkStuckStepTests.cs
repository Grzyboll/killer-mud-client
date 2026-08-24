using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Regression coverage for issue #34: a move command silently swallowed by the server (e.g. a
/// locked door whose GMCP exit was never flagged door+closed, so TryGetOpenCommand never fired,
/// and whose failure text wasn't the literal "brama...zamknięta" HandleLockedAutowalkGate
/// matches — a tomb/crypt entrance, for example) previously left autowalk, and therefore
/// auto-farm (which is just autowalk on a loop, see AutoFarmTests), waiting forever for a room
/// change that would never come. HandleAutowalkStepStuck is the generic backstop: it's normally
/// reached asynchronously via MonitorAutowalkStepStuckAsync's delay, but is invoked directly here
/// (same pattern as AutowalkMovementRecoveryCapTests) since its own decision logic is synchronous.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutowalkStuckStepTests
{
    private static MainWindowViewModel CreateViewModel(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_AutowalkStuckStepTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new MainWindowViewModel(settingsService: new AppSettingsService(directory));
    }

    private static void InvokePrivate(MainWindowViewModel viewModel, string methodName, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, args);

    private static void SetPrivateField(MainWindowViewModel viewModel, string fieldName, object? value) =>
        typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, value);

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string fieldName) =>
        (T)typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel)!;

    private static int GetMaxStuckRecoveryAttempts() => (int)typeof(MainWindowViewModel)
        .GetField("MaxAutowalkStuckRecoveryAttempts", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!;

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

    private static void ArrangeSingleStepWalk(MainWindowViewModel viewModel, MapRoom from, MapRoom to)
    {
        SetPrivateField(viewModel, "_autowalkPath", new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("grobowiec", to)],
            TotalCost = 1,
        });
        SetPrivateField(viewModel, "_autowalkStep", 0);
        SetPrivateField(viewModel, "_autowalkTargetName", "Cel");
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_BelowMaxAttempts_RetriesInsteadOfStoppingOrExcluding()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", 0);

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.Equal(1, GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
            Assert.DoesNotContain(viewModel.Toasts, t => t.Text.Contains("zablokowane drzwi"));
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_StepAlreadyAdvanced_DoesNothing()
    {
        // The monitor task races a real room-change: if OnAutowalkLocationChanged already moved
        // _autowalkStep forward by the time the stuck-timeout fires, it must be a no-op.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var middle = CreateRoom(2, "2");
            var final = CreateRoom(3, "3");
            SetPrivateField(viewModel, "_autowalkPath", new MapPath
            {
                From = from,
                To = final,
                Steps = [new MapPathStep("north", middle), new MapPathStep("north", final)],
                TotalCost = 2,
            });
            SetPrivateField(viewModel, "_autowalkStep", 1); // already past step 0
            SetPrivateField(viewModel, "_autowalkTargetName", "Cel");
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.Equal(GetMaxStuckRecoveryAttempts(), GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_AlreadyHandledByGateWait_DoesNothing()
    {
        // A recognized "brama...zamknięta" line already armed HandleLockedAutowalkGate's own
        // GMCP-reopen wait — the generic stuck backstop must not pile a second recovery on top.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autowalkWaitingForGate", true);

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_PausedForResting_DoesNothing()
    {
        // The player consciously rested mid-route (see UpdateCharacterPosition's resting
        // transition) — the generic stuck backstop must not mistake the stall for a blocked
        // door and start knocking/pulling/pushing (zapukaj/pull/pociagnij/uderz) then resend
        // the movement command anyway.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autowalkPausedForResting", true);

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task UpdateCharacterPosition_ConsciousRestMidRoute_PausesAutowalkInsteadOfGateRecovery()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_latestCharacterPosition", "standing");

            InvokePrivate(viewModel, "UpdateCharacterPosition", "resting");
            Dispatcher.UIThread.RunJobs();

            Assert.True(GetPrivateField<bool>(viewModel, "_autowalkPausedForResting"));
            Assert.Contains("Odpoczyw", viewModel.AutowalkStatusText);

            // With the pause flag set, the stuck backstop for the in-flight step must be inert.
            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);
            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task UpdateCharacterPosition_StandingUpAfterRest_ResumesAutowalk()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_latestCharacterPosition", "resting");
            SetPrivateField(viewModel, "_autowalkPausedForResting", true);

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            Dispatcher.UIThread.RunJobs();

            Assert.False(GetPrivateField<bool>(viewModel, "_autowalkPausedForResting"));
            Assert.True(viewModel.IsAutowalking);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_ExceedsMaxAttempts_MarksRoomClosedAndStopsResumably()
    {
        var viewModel = CreateViewModel(out var directory);
        try
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
                            CreateRoom(1, "1"),
                            CreateRoom(2, "2"),
                        ],
                    },
                ],
            };
            typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
                .SetValue(viewModel.Map, new MapIndex(document));

            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.False(viewModel.IsAutowalking);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("zablokowane drzwi"));
            Assert.Contains(2, viewModel.Map.AutoFarmExcludedRoomIds);
            Assert.Equal(0, GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_ExceedsMaxAttemptsDuringAutoFarm_ExcludesRoomAndKeepsFarming()
    {
        var viewModel = CreateViewModel(out var directory);
        try
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
                            CreateRoom(1, "1"),
                            CreateRoom(2, "2"),
                            CreateRoom(3, "3"),
                        ],
                    },
                ],
            };
            typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
                .SetValue(viewModel.Map, new MapIndex(document));

            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmVisitedRoomIds", new HashSet<int> { 1 });
            SetPrivateField(viewModel, "_autoFarmRegions", new List<FarmRegion>()); // ContinueAutoFarm stops cleanly here

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            // The room was excluded and the farm's own continuation ran (proven by its
            // region-cleared stop toast) instead of autowalk just hanging on the blocked step.
            Assert.Contains(2, viewModel.Map.AutoFarmExcludedRoomIds);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("obszary nie są już zdefiniowane"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
