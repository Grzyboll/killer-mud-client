using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Automation;
using MudClient.Core.Gmcp;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>Covers auto-farm's "use these the moment combat starts" skill sequence
/// (<see cref="MainWindowViewModel.AutoFarmSkillsText"/>/TryAutoFarmSkillSequence) — the skill
/// counterpart of the cast-sequence tests in AutoFarmTests.cs. Pure decision logic is covered
/// separately by AutoFarmSkillSequencePolicyTests in MudClient.Core.Tests; this file covers the
/// ViewModel wiring, including the cooldown-driven mid-fight re-check a spell entry doesn't need.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoFarmSkillSequenceTests
{
    private static MainWindowViewModel CreateViewModel(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_AutoFarmSkillTest_" + Guid.NewGuid().ToString("N"));
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

    [AvaloniaFact]
    public async Task CombatStarts_SkillSequenceOneAlreadyActive_UsesOnlyTheMissingOne()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            viewModel.AutoFarmSkillsText = "second wind\nberserk";
            GetPrivateField<HashSet<string>>(viewModel, "_activeAffectNames").Add("berserk");

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("second wind"));
            Assert.DoesNotContain(output, line => line.Contains("berserk"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_TwoSkillsInSequence_UsesThemInTheConfiguredOrder()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: "golem"),
            });
            viewModel.AutoFarmSkillsText = "!bash\n!kick";

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            var bashIndex = output.FindIndex(line => line.Contains("bash golem"));
            var kickIndex = output.FindIndex(line => line.Contains("kick golem"));
            Assert.True(bashIndex >= 0 && kickIndex >= 0, "Both skills should have been used.");
            Assert.True(bashIndex < kickIndex, "bash was configured before kick and should fire first.");
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_AutoFarmNotActive_DoesNotUseTheSequence()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            viewModel.AutoFarmSkillsText = "berserk";

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("berserk"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_OffensiveEntry_UsesAtTheCurrentEnemyNotSelf()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: "golem"),
            });
            viewModel.AutoFarmSkillsText = "!kick";

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("kick golem"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_OffensiveEntryWithNoKnownEnemyYet_IsSkippedNotMisusedAtSelf()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            // Room.People hasn't caught up with the fresh "fighting" transition yet — no Enemy.
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: null),
            });
            viewModel.AutoFarmSkillsText = "!kick";

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("kick"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_MixOfSelfAndOffensiveEntries_SelfTargetsSelfOffensiveTargetsEnemy()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: "golem"),
            });
            viewModel.AutoFarmSkillsText = "berserk\n!kick";

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("> berserk"));
            Assert.Contains(output, line => line.Contains("kick golem"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_SkillOnCooldown_IsSkipped()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            viewModel.AutoFarmSkillsText = "berserk";
            GetPrivateField<Dictionary<string, bool>>(viewModel, "_lastSkillTimeouts")["berserk"] = true;

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("berserk"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task MidFight_SkillComesOffCooldown_UsedWithoutWaitingForNextFight()
    {
        // Regression target: unlike a cast-sequence spell, a skill's cooldown can clear mid-fight —
        // TryAutoFarmSkillSequence is expected to re-check on every Char.Vitals tick (see its own
        // xmldoc), not just once at the "fighting" transition like TryAutoFarmCastSequence does.
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterPosition", "fighting");
            viewModel.AutoFarmSkillsText = "berserk";
            GetPrivateField<Dictionary<string, bool>>(viewModel, "_lastSkillTimeouts")["berserk"] = true;

            InvokePrivate(viewModel, "TryAutoFarmSkillSequence");
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(output, line => line.Contains("berserk"));

            GetPrivateField<Dictionary<string, bool>>(viewModel, "_lastSkillTimeouts")["berserk"] = false;

            InvokePrivate(viewModel, "TryAutoFarmSkillSequence");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(output, line => line.Contains("berserk"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task NotFighting_SkillSequenceDoesNotFire()
    {
        // A self skill must not spam outside combat just because it's configured and off cooldown.
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterPosition", "standing");
            viewModel.AutoFarmSkillsText = "berserk";

            InvokePrivate(viewModel, "TryAutoFarmSkillSequence");
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(output, line => line.Contains("berserk"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AutoFarmSkillsText_RoundTripsOffensiveMarker()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            viewModel.AutoFarmSkillsText = "berserk\n!kick";

            Assert.Equal("berserk\n!kick", viewModel.AutoFarmSkillsText);
            Assert.Equal(
                [new AutoFarmSkill("berserk", Offensive: false), new AutoFarmSkill("kick", Offensive: true)],
                GetPrivateField<List<AutoFarmSkill>>(viewModel, "_autoFarmSkillSequence"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
