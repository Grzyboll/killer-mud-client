using System.Reflection;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using Xunit;

namespace MudClient.App.Tests;

public sealed class SpellListCaptureRegressionTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MudClientTests", Guid.NewGuid().ToString("N"));
    private readonly MainWindowViewModel _viewModel;
    private readonly MethodInfo _bufferSpellList;

    public SpellListCaptureRegressionTests()
    {
        Directory.CreateDirectory(_tempDir);
        _viewModel = new MainWindowViewModel(
            profileService: new ProfileService(_tempDir),
            settingsService: new AppSettingsService(_tempDir));
        _bufferSpellList = typeof(MainWindowViewModel).GetMethod(
            "BufferSpellList", BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string Buffer(string text) => (string)_bufferSpellList.Invoke(_viewModel, [text])!;

    [Fact]
    public void MemOutput_WithCircleRows_IsNeverCapturedAsSpellBook()
    {
        const string mem = "==<>==< Czary aktualnie zapamietane >==<>==\nKrag 1: [ 1]armor\n";

        Assert.Equal(mem, Buffer(mem));
        Assert.Equal("Zwykla odpowiedz serwera.\n", Buffer("Zwykla odpowiedz serwera.\n"));
    }

    [Fact]
    public void SpellBookOutput_IsCapturedOnlyAfterItsHeaderAndReleasedAtFooter()
    {
        const string header = "==<>==<> Ksiega Zaklec <>==<>==\n\n";
        const string rows = "Krag 1:\n(26)[1] armor\n";
        const string footer = "Aby sprawdzic, jakie komponenty znasz do zaklecia uzyj 'spells <nazwa_zaklecia>'.\n";

        Assert.Equal(header, Buffer(header));
        Assert.Empty(Buffer(rows));

        var result = Buffer(footer);

        Assert.Contains("Krag 1:", result);
        Assert.Contains("armor", result);
        Assert.Contains("Aby sprawdzic", result);
    }
}
