using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Gmcp;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>Covers the auto-farm heal-spell PRIORITY list (discussion #32 "Usprawnienie trybu
/// farma") — casts/memorizes the strongest spell from an ordered list instead of a single fixed
/// spell name. Pure priority-selection logic is covered by HealthRecoveryPolicyTests in
/// MudClient.Core.Tests; this file covers the ViewModel wiring (profile migration from the old
/// single-field format, end-to-end command dispatch) using the same reflection-helper conventions
/// as AutoFarmTests.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoFarmHealPriorityTests
{
    private static void SetPrivateField(MainWindowViewModel viewModel, string fieldName, object? value) =>
        typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, value);

    private static void InvokePrivate(MainWindowViewModel viewModel, string methodName, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, args);

    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_HealPriorityTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [AvaloniaFact]
    public async Task AutoFarmHealSpellNamesText_RoundTripsMultilineOrderedList()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.AutoFarmHealSpellNamesText = "cure critical\ncure serious\ncure light";

            Assert.Equal("cure critical\ncure serious\ncure light", viewModel.AutoFarmHealSpellNamesText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_LegacySingleHealSpellField_MigratesIntoPriorityList()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData { Name = "Legacy", AutoFarmHealSpellName = "heal" });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Legacy";
            viewModel.SelectProfileCommand.Execute(null);

            Assert.Equal("heal", viewModel.AutoFarmHealSpellNamesText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_NewPriorityListPresent_TakesPrecedenceOverLegacyField()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData
        {
            Name = "Modern",
            AutoFarmHealSpellName = "old-single-field",
            AutoFarmHealSpellNames = ["cure critical", "cure light"],
        });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Modern";
            viewModel.SelectProfileCommand.Execute(null);

            Assert.Equal("cure critical\ncure light", viewModel.AutoFarmHealSpellNamesText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_NoHealSpellsConfiguredEitherWay_StaysEmpty()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData { Name = "Blank" });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Blank";
            viewModel.SelectProfileCommand.Execute(null);

            Assert.Equal(string.Empty, viewModel.AutoFarmHealSpellNamesText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TryAutoFarmCombatHeal_StrongestNotMemorized_CastsStrongestThatIsMemorizedInstead()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmHealSpellNames", new List<string> { "cure critical", "cure light" });
            SetPrivateField(viewModel, "_autoFarmHpThresholdPercent", 50);
            SetPrivateField(viewModel, "_latestHp", 10);
            SetPrivateField(viewModel, "_latestMaxHp", 100);
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "cure light", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "TryAutoFarmCombatHeal");
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("cure light"));
            Assert.DoesNotContain(output, line => line.Contains("cure critical"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TryAutoFarmCombatHeal_AutoSelfHealEnabledButAutoFarmNotActive_StillCasts()
    {
        // The point of AutoSelfHealEnabled: a follower character that never runs auto-farm's own
        // walking should still be able to react to its own dropping HP.
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            viewModel.AutoSelfHealEnabled = true;
            SetPrivateField(viewModel, "_autoFarmActive", false);
            SetPrivateField(viewModel, "_autoFarmHealSpellNames", new List<string> { "cure critical" });
            SetPrivateField(viewModel, "_autoFarmHpThresholdPercent", 50);
            SetPrivateField(viewModel, "_latestHp", 10);
            SetPrivateField(viewModel, "_latestMaxHp", 100);
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "cure critical", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "TryAutoFarmCombatHeal");
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(output, line => line.Contains("cure critical"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TryAutoFarmCombatHeal_NeitherAutoFarmActiveNorAutoSelfHealEnabled_DoesNotCast()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            SetPrivateField(viewModel, "_autoFarmActive", false);
            SetPrivateField(viewModel, "_autoFarmHealSpellNames", new List<string> { "cure critical" });
            SetPrivateField(viewModel, "_autoFarmHpThresholdPercent", 50);
            SetPrivateField(viewModel, "_latestHp", 10);
            SetPrivateField(viewModel, "_latestMaxHp", 100);
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "cure critical", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "TryAutoFarmCombatHeal");
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(output, line => line.Contains("cure critical"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AutoSelfHealEnabled_PersistsToActiveProfile()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            Assert.False(viewModel.AutoSelfHealEnabled);

            viewModel.AutoSelfHealEnabled = true;

            Assert.True(viewModel.AutoSelfHealEnabled);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TryAutoFarmCombatHeal_NothingMemorized_DoesNotCastAnything()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmHealSpellNames", new List<string> { "cure critical", "cure light" });
            SetPrivateField(viewModel, "_autoFarmHpThresholdPercent", 50);
            SetPrivateField(viewModel, "_latestHp", 10);
            SetPrivateField(viewModel, "_latestMaxHp", 100);
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>());

            InvokePrivate(viewModel, "TryAutoFarmCombatHeal");
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(output, line => line.Contains("> cast"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
