using System.Reflection;
using Avalonia.Headless.XUnit;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>Covers multiple simultaneous auto-farm regions (discussion #32: "możliwość
/// zaznaczania wielu obszarów") — drawing/clearing on <see cref="MapViewModel"/>, profile
/// migration from the old single-region field, and end-to-end traversal covering rooms from every
/// region in one run. The union/dedup logic itself is covered by FarmTraversalPlannerTests in
/// MudClient.Core.Tests; this file covers the ViewModel wiring.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoFarmMultiRegionTests
{
    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_MultiRegionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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

    private static readonly FarmRegion RegionA = new(1, 0, -10, -10, 10, 10);
    private static readonly FarmRegion RegionB = new(1, 0, 90, -10, 110, 10);

    [AvaloniaFact]
    public async Task NotifyAutoFarmRegionDrawn_CalledTwice_AddsBothInsteadOfReplacing()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionB);

            Assert.Equal([RegionA, RegionB], viewModel.Map.AutoFarmRegions);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ClearAutoFarmRegion_RemovesEveryDrawnRegion()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionB);

            viewModel.Map.ClearAutoFarmRegionCommand.Execute(null);

            Assert.Empty(viewModel.Map.AutoFarmRegions);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task RemoveAutoFarmRegion_OneOfTwo_RemovesOnlyThatOneRegion()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionB);
            var summaries = viewModel.Map.AutoFarmRegionSummaries;
            var summaryForRegionA = Assert.Single(summaries, s => s.Region == RegionA);

            viewModel.Map.RemoveAutoFarmRegion(summaryForRegionA);

            Assert.Equal([RegionB], viewModel.Map.AutoFarmRegions);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task RemoveAutoFarmRegionCommand_ExecutedWithSummary_RemovesThatRegion()
    {
        // Exercises the actual ICommand path a "✕" button click goes through, not just the
        // backing method directly — matches ClearAutoFarmRegion_RemovesEveryDrawnRegion's own
        // Command.Execute(...) style below.
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionB);
            var summaryForRegionB = Assert.Single(viewModel.Map.AutoFarmRegionSummaries, s => s.Region == RegionB);

            viewModel.Map.RemoveAutoFarmRegionCommand.Execute(summaryForRegionB);

            Assert.Equal([RegionA], viewModel.Map.AutoFarmRegions);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task RemoveAutoFarmRegion_NullSummary_DoesNothing()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);

            viewModel.Map.RemoveAutoFarmRegion(null);

            Assert.Equal([RegionA], viewModel.Map.AutoFarmRegions);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AutoFarmRegionSummaries_NumbersEachRegionAndMirrorsRemovalImmediately()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionB);

            var summaries = viewModel.Map.AutoFarmRegionSummaries;

            Assert.Equal(2, summaries.Count);
            Assert.StartsWith("1.", summaries[0].Description);
            Assert.StartsWith("2.", summaries[1].Description);
            Assert.Contains("obszar 1, poziom 0", summaries[0].Description);

            viewModel.Map.RemoveAutoFarmRegion(summaries[0]);

            var remaining = Assert.Single(viewModel.Map.AutoFarmRegionSummaries);
            Assert.StartsWith("1.", remaining.Description);
            Assert.Equal(RegionB, remaining.Region);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_LegacySingleRegion_MigratesIntoOneEntryList()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData
        {
            Name = "Legacy",
            AutoFarmRegion = new ProfileFarmRegion { AreaId = 1, Z = 0, MinX = -10, MinY = -10, MaxX = 10, MaxY = 10 },
        });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Legacy";
            viewModel.SelectProfileCommand.Execute(null);

            var region = Assert.Single(viewModel.Map.AutoFarmRegions);
            Assert.Equal(RegionA, region);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchProfile_NewRegionsListPresent_TakesPrecedenceOverLegacyField()
    {
        var directory = CreateDirectory();
        var service = new ProfileService(directory);
        service.Save(new ProfileData
        {
            Name = "Modern",
            AutoFarmRegion = new ProfileFarmRegion { AreaId = 1, Z = 0, MinX = -999, MinY = -999, MaxX = -998, MaxY = -998 },
            AutoFarmRegions =
            [
                new ProfileFarmRegion { AreaId = 1, Z = 0, MinX = -10, MinY = -10, MaxX = 10, MaxY = 10 },
                new ProfileFarmRegion { AreaId = 1, Z = 0, MinX = 90, MinY = -10, MaxX = 110, MaxY = 10 },
            ],
        });
        var viewModel = new MainWindowViewModel(service, new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

        try
        {
            viewModel.SelectedProfileName = "Modern";
            viewModel.SelectProfileCommand.Execute(null);

            Assert.Equal([RegionA, RegionB], viewModel.Map.AutoFarmRegions);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MapRoom CreateRoom(int id, string vnum, double x, double y, params (string Name, int Target)[] exits) => new()
    {
        Id = id,
        AreaId = 1,
        Coordinates = new MapCoordinates(x, y, 0),
        UserData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement(vnum),
        },
        Exits = exits.Select(e => new MapExit { ExitId = e.Target, Name = e.Name }).ToList(),
    };

    private static void SetCurrentVnum(MainWindowViewModel viewModel, string vnum)
    {
        var resolver = typeof(MainWindowViewModel)
            .GetField("_locationResolver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel)!;
        resolver.GetType().GetProperty("CurrentVnum")!.SetValue(resolver, vnum);
    }

    [AvaloniaFact]
    public async Task StartAutoFarm_TwoRegions_VisitOrderCoversRoomsFromBoth()
    {
        var directory = CreateDirectory();
        var viewModel = new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

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
                            CreateRoom(1, "1", 0, 0, ("north", 2), ("east", 4)),
                            CreateRoom(2, "2", 0, 1, ("south", 1), ("east", 3)),
                            CreateRoom(3, "3", 1, 1, ("west", 2)),
                            CreateRoom(4, "4", 100, 0, ("west", 1), ("north", 5)),
                            CreateRoom(5, "5", 100, 1, ("south", 4)),
                        ],
                    },
                ],
            };
            typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
                .SetValue(viewModel.Map, new MapIndex(document));
            SetCurrentVnum(viewModel, "1");
            SetPrivateField(viewModel, "_isConnected", true);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionA);
            viewModel.Map.NotifyAutoFarmRegionDrawn(RegionB);

            InvokePrivate(viewModel, "StartAutoFarm");

            var order = GetPrivateField<IReadOnlyList<MapRoom>?>(viewModel, "_autoFarmVisitOrder");
            Assert.NotNull(order);
            Assert.Equal(new[] { 2, 3, 4, 5 }, order!.Select(r => r.Id).OrderBy(id => id));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
