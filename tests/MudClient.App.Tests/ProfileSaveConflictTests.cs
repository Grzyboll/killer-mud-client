using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>
/// Two running instances of the client sharing the same profile/global files have no way to
/// merge their changes — whichever instance saves last silently overwrites what the other one
/// wrote. These tests verify the guard that at least surfaces this instead of staying silent:
/// a warning toast when a save is about to overwrite a file that changed on disk since this
/// instance last loaded or saved it.
/// </summary>
public sealed class ProfileSaveConflictTests
{
    private static async Task<string> CreateTempDirectoryAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "KillerMudClient_SaveConflictTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await Task.CompletedTask;
        return directory;
    }

    [Fact]
    public async Task SavingProfile_AfterAnotherInstanceSavedIt_ShowsWarningToast()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService);
            vm1.NewProfileName = "TestHero";
            vm1.NewProfileHost = "killer-mud.pl";
            vm1.NewProfilePort = 4004;
            vm1.CreateProfileCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": loads and re-saves the same profile, moving the on-disk
            // timestamp past what vm1 last knew about.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService);
            vm2.SelectedProfileName = "TestHero";
            vm2.SelectProfileCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            vm1.NewRuleName = "MojTrigger";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "Jestes ranny";
            vm1.NewRuleAction = "heal";
            vm1.NewRuleIsGlobal = false;
            vm1.AddRuleCommand.Execute(null);

            Assert.Contains(vm1.Toasts, t => t.Type == "warning" && t.Text.Contains("TestHero"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingProfile_RepeatedlyFromSameInstance_NeverShowsWarningToast()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm = new MainWindowViewModel(profileService, settingsService);
            vm.NewProfileName = "TestHero";
            vm.NewProfileHost = "killer-mud.pl";
            vm.NewProfilePort = 4004;
            vm.CreateProfileCommand.Execute(null);

            for (var i = 0; i < 5; i++)
            {
                vm.NewRuleName = $"Rule{i}";
                vm.NewRuleType = "trigger";
                vm.NewRulePattern = "x";
                vm.NewRuleAction = "y";
                vm.NewRuleIsGlobal = false;
                vm.AddRuleCommand.Execute(null);
            }

            Assert.DoesNotContain(vm.Toasts, t => t.Type == "warning");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingGlobalData_AfterAnotherInstanceSavedIt_ShowsWarningToast()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService);
            vm1.NewRuleName = "GlobalOne";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "x";
            vm1.NewRuleAction = "y";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": loads the global file at construction, then saves its own
            // global addition, moving the on-disk timestamp past what vm1 last knew about.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService);
            vm2.NewRuleName = "GlobalTwo";
            vm2.NewRuleType = "trigger";
            vm2.NewRulePattern = "z";
            vm2.NewRuleAction = "w";
            vm2.NewRuleIsGlobal = true;
            vm2.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            vm1.NewRuleName = "GlobalThree";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "q";
            vm1.NewRuleAction = "r";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            Assert.Contains(vm1.Toasts, t => t.Type == "warning" && t.Text.Contains("globalne"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
