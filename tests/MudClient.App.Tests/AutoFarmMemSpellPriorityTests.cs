using Avalonia.Headless.XUnit;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>Covers the auto-farm "keep memorized" list's required-vs-opportunistic split
/// (discussion #32's "obczarka" request: some spells must block the farm until mem'd, others
/// should only be mem'd "przy okazji" — while the farm is already stopped for another reason).
/// The decision logic that skips blocking for opportunistic-only misses lives in
/// AutoFarmTests.ContinueAutoFarm_OnlyOptionalSpellMissing_DoesNotBlockFarmTraversal; this file
/// covers the text-format round-trip and profile migration, mirroring
/// AutoFarmHealPriorityTests' conventions for the sibling heal-spell-list feature.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoFarmMemSpellPriorityTests
{
    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_MemSpellPriorityTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [AvaloniaFact]
    public async Task AutoFarmMemSpellsText_RoundTripsRequiredAndOpportunisticLines()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.AutoFarmMemSpellsText = "armor\n~haste\nbless";

            Assert.Equal("armor\n~haste\nbless", viewModel.AutoFarmMemSpellsText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AutoFarmMemSpellsText_DedupesCaseInsensitively_FirstOccurrenceWins()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            // "Armor" (required, first) should win over the later "~armor" (opportunistic).
            viewModel.AutoFarmMemSpellsText = "Armor\n~armor\nbless";

            Assert.Equal("Armor\nbless", viewModel.AutoFarmMemSpellsText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AutoFarmMemSpellsText_TildeWithNoNameLeft_IsIgnored()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.AutoFarmMemSpellsText = "~\narmor\n~   ";

            Assert.Equal("armor", viewModel.AutoFarmMemSpellsText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_LegacyRequiredSpellsList_MigratesEveryEntryAsRequired()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData
        {
            Name = "Legacy",
            AutoFarmRequiredMemorizedSpells = ["armor", "bless"],
        });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Legacy";
            viewModel.SelectProfileCommand.Execute(null);

            Assert.Equal("armor\nbless", viewModel.AutoFarmMemSpellsText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_NewMemSpellsListPresent_TakesPrecedenceOverLegacyList()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData
        {
            Name = "Modern",
            AutoFarmRequiredMemorizedSpells = ["old-legacy-entry"],
            AutoFarmMemSpells = [new AutoFarmMemSpell("armor", Required: true), new AutoFarmMemSpell("haste", Required: false)],
        });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Modern";
            viewModel.SelectProfileCommand.Execute(null);

            Assert.Equal("armor\n~haste", viewModel.AutoFarmMemSpellsText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
