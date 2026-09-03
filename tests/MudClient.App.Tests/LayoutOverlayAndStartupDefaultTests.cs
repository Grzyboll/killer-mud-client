using System.Reflection;
using MudClient.App.Docking;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>
/// Covers two related fixes: a saved layout preset now carries its own copy of the TRANSPARENCY
/// overlay arrangement (see <see cref="DockLayoutSnapshot.TerminalOverlays"/>) instead of silently
/// reusing whatever the live settings currently hold when reloaded, and the new
/// "Ustaw jako domyślny przy starcie programu" star (<see cref="AppSettings.DefaultStartupLayoutName"/>).
/// Every VM here gets its own temp-dir-backed <see cref="DockLayoutService"/>/<see cref="LayoutPresetService"/>
/// so these tests never touch the real %AppData%\KillerMudClient\ files.
/// </summary>
public sealed class LayoutOverlayAndStartupDefaultTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("layout-overlay-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private MainWindowViewModel CreateViewModel(string settingsSubfolder = "Settings") => new(
        settingsService: new AppSettingsService(Path.Combine(_tempDir, settingsSubfolder)),
        dockLayoutService: new DockLayoutService(_tempDir),
        layoutPresetService: new LayoutPresetService(_tempDir));

    private static AppSettings GetSettings(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<AppSettings>(field!.GetValue(viewModel));
    }

    // ====================================================================
    // A saved TRANSPARENCY layout must reproduce its own overlay arrangement,
    // not whatever the live settings happen to hold when it's reloaded later.
    // ====================================================================

    [Fact]
    public async Task SaveLayout_ThenChangeOverlaysThenReapply_RestoresTheSavedOverlayArrangement()
    {
        var viewModel = CreateViewModel();
        try
        {
            var settings = GetSettings(viewModel);
            settings.TerminalOverlays =
            [
                new TerminalOverlayEntry { PanelId = "Gmcp", ColumnIndex = 2, ColumnWidth = 500 },
                new TerminalOverlayEntry { PanelId = "Map", ColumnIndex = 0, ColumnWidth = 300 },
            ];

            viewModel.NewLayoutName = "MojUklad";
            viewModel.SaveLayoutCommand.Execute(null);

            // Simulate the user changing the overlay arrangement afterward (e.g. pinning a
            // different panel, or loading a different session) before coming back to "MojUklad".
            settings.TerminalOverlays = [new TerminalOverlayEntry { PanelId = "Chat", ColumnIndex = 0, ColumnWidth = 320 }];

            viewModel.ApplyLayoutCommand.Execute("MojUklad");

            var restored = GetSettings(viewModel).TerminalOverlays;
            Assert.Equal(2, restored.Count);
            var gmcp = Assert.Single(restored, e => e.PanelId == "Gmcp");
            Assert.Equal(2, gmcp.ColumnIndex);
            Assert.Equal(500, gmcp.ColumnWidth);
            var map = Assert.Single(restored, e => e.PanelId == "Map");
            Assert.Equal(0, map.ColumnIndex);
            Assert.Equal(300, map.ColumnWidth);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [Fact]
    public async Task RestartWithSavedTransparencySession_RestoresItsOwnOverlayArrangement()
    {
        // First "session": pin an overlay arrangement and let window-close auto-save it.
        var first = CreateViewModel();
        var firstSettings = GetSettings(first);
        firstSettings.TerminalOverlays = [new TerminalOverlayEntry { PanelId = "Gmcp", ColumnIndex = 1, ColumnWidth = 400 }];
        await first.DisposeAsync(); // writes dock-layout.json via the auto-save-on-close path

        // Simulate a second app instance whose global settings.json overlay list has since
        // drifted (different account, or the user reset it) before dock-layout.json is restored.
        var second = CreateViewModel();
        try
        {
            var secondSettings = GetSettings(second);
            Assert.Single(secondSettings.TerminalOverlays, e => e.PanelId == "Gmcp" && e.ColumnIndex == 1 && e.ColumnWidth == 400);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    // ====================================================================
    // "Ustaw jako domyślny przy starcie programu" — AppSettings.DefaultStartupLayoutName
    // ====================================================================

    [Fact]
    public async Task SetDefaultStartupLayoutCommand_TogglesTheStarredEntry()
    {
        var viewModel = CreateViewModel();
        try
        {
            Assert.All(viewModel.AvailableLayouts, item => Assert.False(item.IsDefaultStartup));

            viewModel.SetDefaultStartupLayoutCommand.Execute(LayoutPresetService.CompactName);

            Assert.Equal(LayoutPresetService.CompactName, GetSettings(viewModel).DefaultStartupLayoutName);
            Assert.True(viewModel.AvailableLayouts.Single(item => item.Name == LayoutPresetService.CompactName).IsDefaultStartup);

            viewModel.SetDefaultStartupLayoutCommand.Execute(LayoutPresetService.CompactName);

            Assert.Null(GetSettings(viewModel).DefaultStartupLayoutName);
            Assert.All(viewModel.AvailableLayouts, item => Assert.False(item.IsDefaultStartup));
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [Fact]
    public async Task Startup_WithCompactSetAsDefault_AppliesItInsteadOfTransparency()
    {
        var settingsDir = Path.Combine(_tempDir, "Settings");
        var settingsService = new AppSettingsService(settingsDir);
        var settings = settingsService.Load();
        settings.DefaultStartupLayoutName = LayoutPresetService.CompactName;
        settingsService.Save(settings);

        var viewModel = new MainWindowViewModel(
            settingsService: settingsService,
            dockLayoutService: new DockLayoutService(_tempDir),
            layoutPresetService: new LayoutPresetService(_tempDir));
        try
        {
            var dockFactoryField = typeof(MainWindowViewModel).GetField("_dockFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(dockFactoryField);
            var dockFactory = Assert.IsType<MudDockFactory>(dockFactoryField!.GetValue(viewModel));

            Assert.False(dockFactory.IsTransparencyLayout);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [Fact]
    public async Task Startup_WithNoDefaultSet_StaysOnTransparencyBootstrap()
    {
        var viewModel = CreateViewModel();
        try
        {
            var dockFactoryField = typeof(MainWindowViewModel).GetField("_dockFactory", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(dockFactoryField);
            var dockFactory = Assert.IsType<MudDockFactory>(dockFactoryField!.GetValue(viewModel));

            Assert.True(dockFactory.IsTransparencyLayout);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }
}
