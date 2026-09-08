using MudClient.App.Docking;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

public sealed class BuffBadgeTests : IAsyncDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "MudClientTests", Guid.NewGuid().ToString("N"));

    private readonly MainWindowViewModel _vm;

    public BuffBadgeTests()
    {
        Directory.CreateDirectory(_tempDir);
        _vm = new MainWindowViewModel(
            profileService: new ProfileService(_tempDir),
            settingsService: new AppSettingsService(_tempDir));
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuffsBadge_ReflectsRequiredBuffCounts()
    {
        Assert.Equal("0", _vm.BuffsBadge);
        Assert.False(_vm.BuffsAlert);

        _vm.NewBuffName = "armor";
        _vm.AddBuffCommand.Execute(null);

        // No Char.Affects received yet, so the buff counts as missing.
        Assert.Equal("0/1", _vm.BuffsBadge);
        Assert.True(_vm.BuffsAlert);
    }

    [Fact]
    public void AddingRequiredBuff_UpdatesMemDockTabTitle()
    {
        // Buff status now mirrors onto the merged Mem tool's title (it absorbed the former,
        // now-removed standalone Buffs tool) instead of a separate "🛡 Buffy" tab.
        var factory = Assert.IsType<MudDockFactory>(_vm.Layout.Factory);
        var memTool = factory.AllTools.Single(tool => tool.Id == "MemSpells");
        Assert.Equal("✨ Stan postaci — Domyślny", memTool.Title);

        _vm.NewBuffName = "armor";
        _vm.AddBuffCommand.Execute(null);

        Assert.Equal("✨ Stan postaci — Domyślny 0/1", memTool.Title);
    }
}
