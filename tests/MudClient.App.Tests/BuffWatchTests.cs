using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Gmcp;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class BuffWatchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "MudClientTests", Guid.NewGuid().ToString("N"));

    private ProfileService CreateService() => new(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ====================================================================
    // Name normalization (parenthesized counters must be ignored)
    // ====================================================================

    [Theory]
    [InlineData("mirror image (7)", "mirror image")]
    [InlineData("mirror image(7)", "mirror image")]
    [InlineData("blur", "blur")]
    [InlineData("  armor  ", "armor")]
    [InlineData("stone skin (2) ", "stone skin")]
    public void NormalizeName_StripsParenthesizedSuffixAndTrims(string input, string expected)
    {
        Assert.Equal(expected, BuffWatchEntry.NormalizeName(input));
    }

    // ====================================================================
    // Persistence
    // ====================================================================

    [Fact]
    public void SaveAndLoad_RoundTripsRequiredBuffs()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Gandalf",
            RequiredBuffs = ["armor", "mirror image"],
        });

        var loaded = service.Load("Gandalf");

        Assert.NotNull(loaded);
        Assert.Equal(["armor", "mirror image"], loaded!.RequiredBuffs);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNamedSetsAndSelection()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Gandalf",
            ActiveBuffSetId = "combat",
            BuffSets =
            [
                new ProfileBuffSet { Id = "travel", Name = "Podróż", Buffs = ["fly"] },
                new ProfileBuffSet { Id = "combat", Name = "Walka", Buffs = ["armor", "sanctuary"] },
            ],
        });

        var loaded = Assert.IsType<ProfileData>(service.Load("Gandalf"));

        Assert.Equal("combat", loaded.ActiveBuffSetId);
        Assert.Collection(
            loaded.BuffSets,
            set =>
            {
                Assert.Equal("Podróż", set.Name);
                Assert.Equal(["fly"], set.Buffs);
            },
            set =>
            {
                Assert.Equal("Walka", set.Name);
                Assert.Equal(["armor", "sanctuary"], set.Buffs);
            });
    }

    [Fact]
    public void Load_OldProfileWithoutBuffs_ReturnsEmptyList()
    {
        var service = CreateService();
        service.Save(new ProfileData { Name = "Stary" });

        var loaded = service.Load("Stary");

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.RequiredBuffs);
    }

    [Fact]
    public async Task ViewModel_MigratesLegacyListAndPersistsNewSets()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Mag",
            RequiredBuffs = ["armor", "mirror image"],
        });

        await using var viewModel = new MainWindowViewModel(
            service,
            new AppSettingsService(_directory));
        viewModel.SelectedProfileName = "Mag";
        viewModel.SelectProfileCommand.Execute(null);

        Assert.Equal("Domyślny", viewModel.SelectedBuffSet?.Name);
        Assert.Equal(["armor", "mirror image"], viewModel.RequiredBuffs.Select(buff => buff.Name));

        viewModel.NewBuffSetName = "Walka";
        viewModel.CreateBuffSetCommand.Execute(null);
        viewModel.NewBuffName = "sanctuary";
        viewModel.AddBuffCommand.Execute(null);

        var loaded = Assert.IsType<ProfileData>(service.Load("Mag"));
        Assert.Equal("Walka", viewModel.SelectedBuffSet?.Name);
        Assert.Equal(viewModel.SelectedBuffSet?.Id, loaded.ActiveBuffSetId);
        Assert.Collection(
            loaded.BuffSets,
            set => Assert.Equal(["armor", "mirror image"], set.Buffs),
            set => Assert.Equal(["sanctuary"], set.Buffs));
    }

    [Fact]
    public async Task ViewModel_SwitchesVisibleBuffsAndPreventsDuplicateSetNames()
    {
        await using var viewModel = new MainWindowViewModel(
            CreateService(),
            new AppSettingsService(_directory));
        viewModel.NewBuffName = "armor";
        viewModel.AddBuffCommand.Execute(null);
        var defaultSet = Assert.IsType<BuffSetEntry>(viewModel.SelectedBuffSet);

        viewModel.NewBuffSetName = "Walka";
        viewModel.CreateBuffSetCommand.Execute(null);
        viewModel.NewBuffName = "sanctuary";
        viewModel.AddBuffCommand.Execute(null);

        Assert.Equal(["sanctuary"], viewModel.RequiredBuffs.Select(buff => buff.Name));
        viewModel.SelectedBuffSet = defaultSet;
        Assert.Equal(["armor"], viewModel.RequiredBuffs.Select(buff => buff.Name));

        viewModel.NewBuffSetName = "walka";
        viewModel.CreateBuffSetCommand.Execute(null);

        Assert.Equal(2, viewModel.BuffSets.Count);
        Assert.Contains(viewModel.Toasts, toast => toast.Text == "Zestaw „walka” już istnieje.");
    }

    // ====================================================================
    // Mem/circle indicator (see BuffWatchEntry.IsMemorized/Circle) — sourced from Char.MemSpell,
    // which reports every known spell slot (memorized or not), not just currently-memorized ones.
    // ====================================================================

    private static void RaiseMemSpellsChanged(MainWindowViewModel viewModel, params MemorizedSpell[] spells) =>
        typeof(MainWindowViewModel)
            .GetMethod("OnMemSpellsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [spells]);

    [AvaloniaFact]
    public async Task OnMemSpellsChanged_SpellIsMemorized_MarksTheBuffMemorizedWithItsCircle()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "armor";
        viewModel.AddBuffCommand.Execute(null);

        RaiseMemSpellsChanged(viewModel, new MemorizedSpell(1, 2, "armor", Memed: true, Meming: false));
        Dispatcher.UIThread.RunJobs();

        var buff = Assert.Single(viewModel.RequiredBuffs);
        Assert.True(buff.IsMemorized);
        Assert.Equal(2, buff.Circle);
        Assert.Equal("Krąg 2", buff.CircleDisplay);
    }

    [AvaloniaFact]
    public async Task OnMemSpellsChanged_SpellKnownButNotMemorized_ShowsItsCircleWithoutMemorizedFlag()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "sanctuary";
        viewModel.AddBuffCommand.Execute(null);

        RaiseMemSpellsChanged(viewModel, new MemorizedSpell(1, 4, "sanctuary", Memed: false, Meming: false));
        Dispatcher.UIThread.RunJobs();

        var buff = Assert.Single(viewModel.RequiredBuffs);
        Assert.False(buff.IsMemorized);
        Assert.Equal(4, buff.Circle);
    }

    [AvaloniaFact]
    public async Task OnMemSpellsChanged_SpellStillBeingMemorized_DoesNotCountAsMemorizedYet()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "bless";
        viewModel.AddBuffCommand.Execute(null);

        RaiseMemSpellsChanged(viewModel, new MemorizedSpell(1, 1, "bless", Memed: false, Meming: true));
        Dispatcher.UIThread.RunJobs();

        Assert.False(Assert.Single(viewModel.RequiredBuffs).IsMemorized);
    }

    [AvaloniaFact]
    public async Task OnMemSpellsChanged_UnknownSpell_LeavesCircleUnset()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "some unseen spell";
        viewModel.AddBuffCommand.Execute(null);

        RaiseMemSpellsChanged(viewModel, new MemorizedSpell(1, 1, "armor", Memed: true, Meming: false));
        Dispatcher.UIThread.RunJobs();

        var buff = Assert.Single(viewModel.RequiredBuffs);
        Assert.False(buff.IsMemorized);
        Assert.Null(buff.Circle);
        Assert.False(buff.HasCircle);
        Assert.Equal(string.Empty, buff.CircleDisplay);
    }

    [AvaloniaFact]
    public async Task OnMemSpellsChanged_NameWithParenthesizedCounter_StillMatchesByNormalizedName()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "mirror image (7)";
        viewModel.AddBuffCommand.Execute(null);

        RaiseMemSpellsChanged(viewModel, new MemorizedSpell(1, 3, "mirror image", Memed: true, Meming: false));
        Dispatcher.UIThread.RunJobs();

        Assert.True(Assert.Single(viewModel.RequiredBuffs).IsMemorized);
    }

    // ====================================================================
    // Merged Mem/Buffy/Kondycja panel: ending state, unwatched effects, reorder, "Pokaż mem"
    // ====================================================================

    private static void RaiseAffectsChanged(MainWindowViewModel viewModel, params CharacterAffect[] affects) =>
        typeof(MainWindowViewModel)
            .GetMethod("OnCharacterAffectsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, [affects]);

    [AvaloniaFact]
    public async Task OnCharacterAffectsChanged_WatchedBuffAboutToEnd_CarriesEndingDurationAndDescription()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "mirror image";
        viewModel.AddBuffCommand.Execute(null);

        RaiseAffectsChanged(viewModel, new CharacterAffect(
            "mirror image (2)", "Jesteś ukryty pośród swoich odbić.", Negative: false, Ending: true, ExtraValue: "2"));
        Dispatcher.UIThread.RunJobs();

        var buff = Assert.Single(viewModel.RequiredBuffs);
        Assert.True(buff.IsActive);
        Assert.True(buff.IsEnding);
        Assert.Equal("2", buff.Duration);
        Assert.True(buff.HasDuration);
        Assert.Equal("Jesteś ukryty pośród swoich odbić.", buff.Description);
    }

    [AvaloniaFact]
    public async Task OnCharacterAffectsChanged_WatchedBuffGone_ClearsEndingState()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "armor";
        viewModel.AddBuffCommand.Execute(null);
        RaiseAffectsChanged(viewModel, new CharacterAffect("armor", "", Negative: false, Ending: true, ExtraValue: null));
        Dispatcher.UIThread.RunJobs();

        RaiseAffectsChanged(viewModel);
        Dispatcher.UIThread.RunJobs();

        var buff = Assert.Single(viewModel.RequiredBuffs);
        Assert.False(buff.IsActive);
        Assert.False(buff.IsEnding);
        Assert.False(buff.HasDuration);
    }

    [AvaloniaFact]
    public async Task UnwatchedActiveEffects_ExcludesWatchedBuffsAndKeepsEverythingElse()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        viewModel.NewBuffName = "armor";
        viewModel.AddBuffCommand.Execute(null);

        RaiseAffectsChanged(
            viewModel,
            new CharacterAffect("armor", "", Negative: false, Ending: false, ExtraValue: null),
            new CharacterAffect("blindness", "Nic nie widzisz.", Negative: true, Ending: false, ExtraValue: null));
        Dispatcher.UIThread.RunJobs();

        var unwatched = viewModel.UnwatchedActiveEffects.Select(effect => effect.Name).ToList();
        Assert.Equal(["blindness"], unwatched);
    }

    [AvaloniaFact]
    public async Task MoveBuff_ReordersWithinSet()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        foreach (var name in new[] { "armor", "bless", "sanctuary" })
        {
            viewModel.NewBuffName = name;
            viewModel.AddBuffCommand.Execute(null);
        }

        var sanctuary = viewModel.RequiredBuffs.Single(buff => buff.Name == "sanctuary");
        viewModel.MoveBuff(sanctuary, 0);

        Assert.Equal(["sanctuary", "armor", "bless"], viewModel.RequiredBuffs.Select(buff => buff.Name));
    }

    [AvaloniaFact]
    public async Task ShowMemSpellSlots_DefaultsOnAndPersistsToggle()
    {
        await using var viewModel = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));
        Assert.True(viewModel.ShowMemSpellSlots);

        viewModel.ShowMemSpellSlots = false;

        Assert.False(viewModel.ShowMemSpellSlots);
    }
}
