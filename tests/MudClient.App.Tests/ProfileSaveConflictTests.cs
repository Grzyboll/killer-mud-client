using System.Reflection;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>
/// Two running instances of the client sharing the same profile/global files (multiboxing)
/// used to have no way to merge their changes — whichever instance saved last silently
/// overwrote what the other one wrote. SaveActiveProfile now 3-way merges against disk
/// whenever it detects the file changed since this instance last loaded/saved it, so an
/// addition, edit or deletion made by the *other* instance survives instead of being clobbered.
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
    public async Task SavingProfile_AfterAnotherInstanceSavedIt_MergesInsteadOfOverwriting()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm1.NewProfileName = "TestHero";
            vm1.NewProfileHost = "killer-mud.pl";
            vm1.NewProfilePort = 4004;
            vm1.CreateProfileCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": loads and re-saves the same profile, moving the on-disk
            // timestamp past what vm1 last knew about.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm2.SelectedProfileName = "TestHero";
            vm2.SelectProfileCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            vm1.NewRuleName = "MojTrigger";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "Jestes ranny";
            vm1.NewRuleAction = "heal";
            vm1.NewRuleIsGlobal = false;
            vm1.AddRuleCommand.Execute(null);

            Assert.Contains(vm1.Toasts, t => t.Type == "info" && t.Text.Contains("TestHero"));

            var onDisk = profileService.Load("TestHero");
            Assert.NotNull(onDisk);
            Assert.Contains(onDisk!.Rules, r => r.Name == "MojTrigger");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingProfile_RepeatedlyFromSameInstance_NeverShowsMergeToast()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
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

            Assert.DoesNotContain(vm.Toasts, t => t.Type is "warning" or "info" && t.Text.Contains("inną instancję"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingGlobalData_AfterAnotherInstanceAddedARule_MergesInsteadOfOverwriting()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm1.NewRuleName = "GlobalOne";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "x";
            vm1.NewRuleAction = "y";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": loads the global file at construction (sees GlobalOne), then adds
            // its own global trigger and saves — moving the on-disk timestamp past what vm1
            // last knew about.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm2.NewRuleName = "GlobalTwo";
            vm2.NewRuleType = "trigger";
            vm2.NewRulePattern = "z";
            vm2.NewRuleAction = "w";
            vm2.NewRuleIsGlobal = true;
            vm2.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // vm1 still only knows about GlobalOne — adding GlobalThree and saving must not
            // drop vm2's GlobalTwo, which is exactly the bug multiboxing users hit.
            vm1.NewRuleName = "GlobalThree";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "q";
            vm1.NewRuleAction = "r";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            Assert.Contains(vm1.Toasts, t => t.Type == "info" && t.Text.Contains("globalne"));

            var onDisk = profileService.LoadGlobal();
            Assert.Contains(onDisk.Rules, r => r.Name == "GlobalOne");
            Assert.Contains(onDisk.Rules, r => r.Name == "GlobalTwo");
            Assert.Contains(onDisk.Rules, r => r.Name == "GlobalThree");

            // vm1's own in-memory view picks up the merged-in GlobalTwo too, so its *next*
            // save won't drop it again.
            Assert.Contains(vm1.AutomationRules, r => r.Name == "GlobalTwo");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingGlobalData_WhenOtherInstanceDeletedAnEntryWeDidNotTouch_RespectsThatDeletion()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm1.NewRuleName = "GlobalOne";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "x";
            vm1.NewRuleAction = "y";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            vm1.NewRuleName = "GlobalTwo";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "z";
            vm1.NewRuleAction = "w";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": loads (sees both), deletes GlobalOne, saves.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            var toDelete = vm2.AutomationRules.First(r => r.Name == "GlobalOne");
            vm2.DeleteRuleCommand.Execute(toDelete);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // vm1 never touched GlobalOne itself — its save must not resurrect it just
            // because vm1's own in-memory copy still has it.
            vm1.NewRuleName = "GlobalThree";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "q";
            vm1.NewRuleAction = "r";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            var onDisk = profileService.LoadGlobal();
            Assert.DoesNotContain(onDisk.Rules, r => r.Name == "GlobalOne");
            Assert.Contains(onDisk.Rules, r => r.Name == "GlobalTwo");
            Assert.Contains(onDisk.Rules, r => r.Name == "GlobalThree");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingGlobalData_AfterAnotherInstanceCreatedAFolderAndFiledARuleIntoIt_MergesInsteadOfOverwriting()
    {
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm1.NewRuleName = "GlobalOne";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "x";
            vm1.NewRuleAction = "y";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": creates a global folder and files a new trigger into it.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm2.CreateFolderCommand.Execute(FolderKind.Triggers);
            var folder = vm2.Folders.Single(f => f.Kind == FolderKind.Triggers);
            vm2.ToggleFolderGlobalCommand.Execute(folder);

            vm2.NewRuleName = "FolderedRule";
            vm2.NewRuleType = "trigger";
            vm2.NewRulePattern = "z";
            vm2.NewRuleAction = "w";
            vm2.NewRuleIsGlobal = false;
            vm2.AddRuleCommand.Execute(null);
            var rule = vm2.AutomationRules.Single(r => r.Name == "FolderedRule");
            vm2.MoveIntoFolderCommand.Execute(new FolderMoveRequest(rule, folder));

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // vm1 never touched folders — its save must not drop vm2's new folder or the rule
            // filed into it. This is the multiboxing complaint: "foldery się nie synchronizują".
            vm1.NewRuleName = "GlobalThree";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "q";
            vm1.NewRuleAction = "r";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            var onDisk = profileService.LoadGlobal();
            var mergedFolder = Assert.Single(onDisk.Folders, f => f.Kind == FolderKind.Triggers);
            Assert.True(mergedFolder.IsGlobal);
            var mergedRule = Assert.Single(onDisk.Rules, r => r.Name == "FolderedRule");
            Assert.Equal(mergedFolder.Id, mergedRule.FolderId);
            Assert.True(mergedRule.IsGlobal);

            // vm1's own in-memory view should also pick up the merged-in folder/rule.
            Assert.Contains(vm1.Folders, f => f.Id == mergedFolder.Id);
            Assert.Contains(vm1.AutomationRules, r => r.Name == "FolderedRule" && r.FolderId == mergedFolder.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingGlobalData_TwoInstancesCreateDifferentRulesWithTheSameNameAndType_BothSurviveDistinctly()
    {
        // Regression guard: rules used to be merge-matched by Type+Name only, so two
        // independently created global rules that happen to share a name (e.g. both called
        // "MojTrigger") were treated as "the same rule" by the 3-way merge — whichever instance
        // synced last would silently overwrite the other's Pattern/Action, with no warning.
        // Merging now keys on each rule's stable Id instead, so same-named-but-different rules
        // coexist.
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm1.NewRuleName = "MojTrigger";
            vm1.NewRuleType = "trigger";
            vm1.NewRulePattern = "Jestes ranny";
            vm1.NewRuleAction = "heal";
            vm1.NewRuleIsGlobal = true;
            vm1.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // "Instance 2": independently creates its own rule that happens to share the same
            // Type+Name as vm1's, but with a different Pattern/Action.
            await using var vm2 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm2.NewRuleName = "MojTrigger";
            vm2.NewRuleType = "trigger";
            vm2.NewRulePattern = "Jestes zmeczony";
            vm2.NewRuleAction = "sleep";
            vm2.NewRuleIsGlobal = true;
            vm2.AddRuleCommand.Execute(null);

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // vm1's next sync (no local edit needed) must not silently drop or overwrite vm2's
            // differently-configured rule.
            InvokeSaveActiveProfile(vm1);

            var onDisk = profileService.LoadGlobal();
            var sameNamed = onDisk.Rules.Where(r => r.Name == "MojTrigger").ToList();
            Assert.Equal(2, sameNamed.Count);
            Assert.Contains(sameNamed, r => r.Pattern == "Jestes ranny" && r.Action == "heal");
            Assert.Contains(sameNamed, r => r.Pattern == "Jestes zmeczony" && r.Action == "sleep");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void InvokeSaveActiveProfile(MainWindowViewModel vm)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "SaveActiveProfile", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    [Fact]
    public async Task PeriodicSync_WithNoLocalEdit_StillPicksUpOtherInstancesFolderAndRule()
    {
        // This is the actual multibox complaint: a folder/trigger created on one window doesn't
        // show up on the other until *something* is edited there — the fix is a periodic call to
        // SaveActiveProfile (see the constructor), simulated here directly via reflection instead
        // of waiting several seconds for the real timer.
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm1 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));

            await using var vm2 = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm2.CreateFolderCommand.Execute(FolderKind.Triggers);
            var folder = vm2.Folders.Single(f => f.Kind == FolderKind.Triggers);
            vm2.ToggleFolderGlobalCommand.Execute(folder);

            vm2.NewRuleName = "FolderedRule";
            vm2.NewRuleType = "trigger";
            vm2.NewRulePattern = "z";
            vm2.NewRuleAction = "w";
            vm2.NewRuleIsGlobal = false;
            vm2.AddRuleCommand.Execute(null);
            var rule = vm2.AutomationRules.Single(r => r.Name == "FolderedRule");
            vm2.MoveIntoFolderCommand.Execute(new FolderMoveRequest(rule, folder));

            await Task.Delay(20, TestContext.Current.CancellationToken);

            // vm1 makes NO local edit at all — only the ambient sync call.
            InvokeSaveActiveProfile(vm1);

            var mergedFolder = Assert.Single(vm1.Folders, f => f.Kind == FolderKind.Triggers);
            Assert.True(mergedFolder.IsGlobal);
            Assert.Contains(
                vm1.AutomationRules,
                r => r.Name == "FolderedRule" && r.FolderId == mergedFolder.Id && r.IsGlobal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveActiveProfile_CalledRepeatedlyWithNothingChanged_NeverRewritesOrToasts()
    {
        // Guards against the periodic sync timer turning into a write/toast ping-pong between
        // two instances: once both sides agree, repeated calls must be complete no-ops.
        var directory = await CreateTempDirectoryAsync();
        var profileService = new ProfileService(directory);
        var settingsService = new AppSettingsService(directory);

        try
        {
            await using var vm = new MainWindowViewModel(profileService, settingsService,
                layoutPresetService: new LayoutPresetService(directory),
                groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
            vm.NewProfileName = "TestHero";
            vm.NewProfileHost = "killer-mud.pl";
            vm.NewProfilePort = 4004;
            vm.CreateProfileCommand.Execute(null);

            vm.NewRuleName = "GlobalOne";
            vm.NewRuleType = "trigger";
            vm.NewRulePattern = "x";
            vm.NewRuleAction = "y";
            vm.NewRuleIsGlobal = true;
            vm.AddRuleCommand.Execute(null);

            var globalWriteTime = profileService.GetGlobalLastWriteTimeUtc();
            var profileWriteTime = profileService.GetLastWriteTimeUtc("TestHero");
            vm.Toasts.Clear();

            for (var i = 0; i < 5; i++)
            {
                InvokeSaveActiveProfile(vm);
            }

            Assert.Equal(globalWriteTime, profileService.GetGlobalLastWriteTimeUtc());
            Assert.Equal(profileWriteTime, profileService.GetLastWriteTimeUtc("TestHero"));
            Assert.Empty(vm.Toasts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
