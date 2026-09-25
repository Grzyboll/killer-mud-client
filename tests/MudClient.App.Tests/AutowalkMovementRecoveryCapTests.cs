using System.Reflection;
using Avalonia.Headless.XUnit;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Regression test for a bug where low-movement recovery (rest → stand) had no attempt cap: a
/// character whose MV never climbed back above the threshold in one rest cycle — e.g. because
/// combat kept interrupting/re-draining it during auto-farm — would rest, stand, rest, stand
/// forever. Fixed with an attempt counter mirroring the existing _autowalkRecomputes cap.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutowalkMovementRecoveryCapTests
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

    [AvaloniaFact]
    public async Task SendAutowalkStep_MovementStillLowAfterMaxAttempts_StopsInsteadOfRecoveringAgain()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_MovementRecoveryCapTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            Assert.True(viewModel.AutowalkMovementRecoveryEnabled);

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
            SetPrivateField(viewModel, "_latestCharacterPosition", "standing");
            SetPrivateField(viewModel, "_latestMovement", 5);
            SetPrivateField(viewModel, "_latestMaximumMovement", 100);

            // Simulate having already exhausted every recovery attempt on prior steps.
            var maxAttempts = (int)typeof(MainWindowViewModel)
                .GetField("MaxAutowalkMovementRecoveryAttempts", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            SetPrivateField(viewModel, "_autowalkMovementRecoveryAttempts", maxAttempts);

            var method = typeof(MainWindowViewModel).GetMethod(
                "SendAutowalkStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(viewModel, [false]);

            Assert.False(viewModel.IsAutowalking);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("ruch nie wraca"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SendAutowalkStep_MovementLowButAttemptsNotExhausted_StillTriggersRecovery()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_MovementRecoveryCapTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
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
            SetPrivateField(viewModel, "_latestCharacterPosition", "standing");
            SetPrivateField(viewModel, "_latestMovement", 5);
            SetPrivateField(viewModel, "_latestMaximumMovement", 100);
            SetPrivateField(viewModel, "_autowalkMovementRecoveryAttempts", 0);

            var method = typeof(MainWindowViewModel).GetMethod(
                "SendAutowalkStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(viewModel, [false]);

            // Not stopped — recovery kicked off instead (still autowalking, no cap-exceeded toast).
            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(viewModel.Toasts, t => t.Text.Contains("ruch nie wraca"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
