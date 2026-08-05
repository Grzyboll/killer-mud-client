using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "MudClientTests", Guid.NewGuid().ToString("N"));

    private ProfileService CreateService() => new(_directory);

    private AppSettingsService CreateSettingsService() => new(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ====================================================================
    // ProfileService
    // ====================================================================

    [Fact]
    public void ListProfileNames_EmptyDirectory_ReturnsEmpty()
    {
        Assert.Empty(CreateService().ListProfileNames());
    }

    [Fact]
    public void PasswordProtector_RoundTripsAndNeverStoresPlainText()
    {
        var encrypted = PasswordProtector.Protect("sekret123");

        if (OperatingSystem.IsWindows())
        {
            Assert.NotEqual(string.Empty, encrypted);
            Assert.DoesNotContain("sekret123", encrypted);
            Assert.Equal("sekret123", PasswordProtector.Unprotect(encrypted));
        }
        else
        {
            // DPAPI jest dostępne tylko na Windows; na innych platformach hasła nie są zapisywane.
            Assert.Equal(string.Empty, encrypted);
        }

        Assert.Equal(string.Empty, PasswordProtector.Unprotect("nie-base64"));
        Assert.Equal(string.Empty, PasswordProtector.Protect(""));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEncryptedPassword()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Gandalf",
            EncryptedPassword = PasswordProtector.Protect("mellon"),
        });

        var loaded = service.Load("Gandalf");

        Assert.NotNull(loaded);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("mellon", PasswordProtector.Unprotect(loaded.EncryptedPassword));
        }
        var json = File.ReadAllText(Path.Combine(_directory, "Gandalf.json"));
        Assert.DoesNotContain("mellon", json);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsProfileData()
    {
        var service = CreateService();
        var profile = new ProfileData
        {
            Name = "Gandalf",
            Login = "gandalf_szary",
            Host = "mud.example.org",
            Port = 4444,
            Notes = [new ProfileNote { Title = "T", Content = "C", CreatedAt = "2026-01-01 10:00" }],
            Rules = [new ProfileRule { Name = "R", Type = "alias", Pattern = "^l$", Action = "look", IsEnabled = true }],
        };

        service.Save(profile);
        var loaded = service.Load("Gandalf");

        Assert.NotNull(loaded);
        Assert.Equal("Gandalf", loaded!.Name);
        Assert.Equal("gandalf_szary", loaded.Login);
        Assert.Equal("mud.example.org", loaded.Host);
        Assert.Equal(4444, loaded.Port);
        var note = Assert.Single(loaded.Notes);
        Assert.Equal("T", note.Title);
        var rule = Assert.Single(loaded.Rules);
        Assert.Equal("look", rule.Action);
    }

    [Fact]
    public void Load_MissingProfile_ReturnsNull()
    {
        Assert.Null(CreateService().Load("nie-istnieje"));
    }

    [Fact]
    public void LoadGlobal_MissingFile_ReturnsEmptyData()
    {
        var global = CreateService().LoadGlobal();

        Assert.Empty(global.Rules);
        Assert.Empty(global.Timers);
        Assert.Empty(global.Locations);
    }

    [Fact]
    public void SaveGlobal_RoundTripsAndIsExcludedFromProfileList()
    {
        var service = CreateService();
        service.Save(new ProfileData { Name = "Gandalf" });
        service.SaveGlobal(new GlobalData
        {
            Notes = [new ProfileNote { Title = "N", Content = "treść", CreatedAt = "2026-07-11 10:00", IsGlobal = true }],
            Rules = [new ProfileRule { Name = "R", Type = "trigger", Pattern = "x", Action = "y", IsGlobal = true }],
            Timers = [new ProfileTimer { Name = "T", Seconds = 5, Commands = ["look"], IsGlobal = true }],
            Locations = [new ProfileLocation { Name = "plac", Vnum = "123", IsGlobal = true }],
        });

        var global = service.LoadGlobal();
        Assert.Equal("N", Assert.Single(global.Notes).Title);
        Assert.Equal("R", Assert.Single(global.Rules).Name);
        Assert.Equal("T", Assert.Single(global.Timers).Name);
        Assert.Equal("123", Assert.Single(global.Locations).Vnum);

        Assert.Equal(["Gandalf"], service.ListProfileNames());
    }

    [Fact]
    public void GetLastWriteTimeUtc_MissingProfile_ReturnsNull()
    {
        Assert.Null(CreateService().GetLastWriteTimeUtc("nie-istnieje"));
    }

    [Fact]
    public void GetLastWriteTimeUtc_ExistingProfile_ReturnsNonNullTimestamp()
    {
        var service = CreateService();
        service.Save(new ProfileData { Name = "Gandalf" });

        Assert.NotNull(service.GetLastWriteTimeUtc("Gandalf"));
    }

    [Fact]
    public void GetLastWriteTimeUtc_ChangesAfterASecondSave()
    {
        var service = CreateService();
        service.Save(new ProfileData { Name = "Gandalf" });
        var first = service.GetLastWriteTimeUtc("Gandalf");

        Thread.Sleep(20);
        service.Save(new ProfileData { Name = "Gandalf", Host = "changed" });
        var second = service.GetLastWriteTimeUtc("Gandalf");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetGlobalLastWriteTimeUtc_MissingFile_ReturnsNull()
    {
        Assert.Null(CreateService().GetGlobalLastWriteTimeUtc());
    }

    [Fact]
    public void GetGlobalLastWriteTimeUtc_ExistingFile_ReturnsNonNullTimestamp()
    {
        var service = CreateService();
        service.SaveGlobal(new GlobalData());

        Assert.NotNull(service.GetGlobalLastWriteTimeUtc());
    }

    [Fact]
    public void Save_ProfileNamedLikeGlobalFile_DoesNotOverwriteGlobalData()
    {
        var service = CreateService();
        service.SaveGlobal(new GlobalData
        {
            Rules = [new ProfileRule { Name = "R", IsGlobal = true }],
        });

        service.Save(new ProfileData { Name = "_global" });

        Assert.Single(service.LoadGlobal().Rules);
    }

    [Fact]
    public void ListProfileNames_ReturnsSavedProfilesSorted()
    {
        var service = CreateService();
        service.Save(new ProfileData { Name = "Zorro" });
        service.Save(new ProfileData { Name = "Aragorn" });

        Assert.Equal(["Aragorn", "Zorro"], service.ListProfileNames());
    }

    [Fact]
    public void Save_SanitizesInvalidFileNameCharacters()
    {
        var service = CreateService();
        service.Save(new ProfileData { Name = "zly:profil?" });

        Assert.True(service.Exists("zly:profil?"));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTimers()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Radagast",
            Timers =
            [
                new ProfileTimer
                {
                    Name = "Leczenie",
                    Minutes = 1,
                    Seconds = 30,
                    Milliseconds = 250,
                    Commands = ["rzuc 'leczenie'", "pij miksture"],
                    IsEnabled = true,
                },
            ],
        });

        var loaded = service.Load("Radagast");

        Assert.NotNull(loaded);
        var timer = Assert.Single(loaded!.Timers);
        Assert.Equal("Leczenie", timer.Name);
        Assert.Equal(1, timer.Minutes);
        Assert.Equal(30, timer.Seconds);
        Assert.Equal(250, timer.Milliseconds);
        Assert.Equal(["rzuc 'leczenie'", "pij miksture"], timer.Commands);
        Assert.True(timer.IsEnabled);
    }

    // ====================================================================
    // AppSettingsService
    // ====================================================================

    [Fact]
    public void AppSettings_SaveAndLoad_RoundTrips()
    {
        var service = new AppSettingsService(_directory);
        service.Save(new AppSettings { OutputFontFamily = "Courier New", OutputFontSize = 18 });

        var loaded = service.Load();

        Assert.Equal("Courier New", loaded.OutputFontFamily);
        Assert.Equal(18, loaded.OutputFontSize);
    }

    [Fact]
    public void AppSettings_MissingFile_ReturnsDefaults()
    {
        var loaded = new AppSettingsService(_directory).Load();

        Assert.Equal(AppSettings.DefaultOutputFontFamily, loaded.OutputFontFamily);
        Assert.Equal(AppSettings.DefaultOutputFontSize, loaded.OutputFontSize);
    }

    [Fact]
    public void AppSettings_Load_ClampsOutOfRangeFontSize()
    {
        var service = new AppSettingsService(_directory);
        service.Save(new AppSettings { OutputFontSize = 100 });

        Assert.Equal(AppSettings.MaxOutputFontSize, service.Load().OutputFontSize);
    }

    [Fact]
    public async Task Vm_ChangingOutputFont_PersistsToSettings()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.OutputFontFamily = "Courier New";
        vm.OutputFontSize = 20;
        vm.OutputFontBold = true;

        var stored = settingsService.Load();
        Assert.Equal("Courier New", stored.OutputFontFamily);
        Assert.Equal(20, stored.OutputFontSize);
        Assert.True(stored.OutputFontBold);
    }

    [Fact]
    public async Task Vm_OutputFontSize_IsClamped()
    {
        await using var vm = new MainWindowViewModel(CreateService(), new AppSettingsService(_directory));

        vm.OutputFontSize = 500;
        Assert.Equal(AppSettings.MaxOutputFontSize, vm.OutputFontSize);

        vm.OutputFontSize = 1;
        Assert.Equal(AppSettings.MinOutputFontSize, vm.OutputFontSize);
    }

    [Fact]
    public async Task Vm_ChangingWidgetFont_PersistsAndClamps()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.WidgetFontFamily = "Verdana";
        vm.WidgetFontSize = 100;
        vm.WidgetFontBold = true;

        var stored = settingsService.Load();
        Assert.Equal("Verdana", stored.WidgetFontFamily);
        Assert.Equal(AppSettings.MaxWidgetFontSize, stored.WidgetFontSize);
        Assert.True(stored.WidgetFontBold);
    }

    [Fact]
    public async Task Vm_ChangingOutputWordWrap_PersistsToSettings()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.OutputWordWrap = false;

        Assert.False(settingsService.Load().OutputWordWrap);
    }

    [Fact]
    public async Task Vm_ChangingClearCommandInputAfterSend_PersistsToSettings()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.ClearCommandInputAfterSend = true;

        Assert.True(settingsService.Load().ClearCommandInputAfterSend);
    }

    [Fact]
    public async Task Vm_ChangingAutoAssistExclusions_PersistsNormalizedNames()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.AutoAssistExcludedMobNamesText = "  Wielki smok  \r\n\r\nOrk\r\nwielki SMOK";

        Assert.Equal(["Wielki smok", "Ork"], settingsService.Load().AutoAssistExcludedMobNames);
        Assert.Equal($"Wielki smok{Environment.NewLine}Ork", vm.AutoAssistExcludedMobNamesText);
    }

    [Fact]
    public async Task Vm_ChangingAutoAssistFollowUpCommands_PersistsMultilineText()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.AutoAssistFollowUpCommands = "wesprzyj\r\nczar 'ochrona'";

        Assert.Equal("wesprzyj\r\nczar 'ochrona'", settingsService.Load().AutoAssistFollowUpCommands);
    }

    [Fact]
    public async Task Vm_ChangingGroupMarkerDisplay_PersistsToSettings()
    {
        var settingsService = new AppSettingsService(_directory);
        await using var vm = new MainWindowViewModel(CreateService(), settingsService);

        vm.Map.ShowGroupMembersAsNumbers = true;

        Assert.True(settingsService.Load().ShowGroupMembersAsNumbers);
    }

    // ====================================================================
    // MainWindowViewModel profile flow
    // ====================================================================

    [Fact]
    public async Task Vm_StartsWithoutActiveProfile()
    {
        await using var vm = new MainWindowViewModel(CreateService(), CreateSettingsService());

        Assert.False(vm.IsProfileSelected);
        Assert.Null(vm.ActiveProfileName);
    }

    [Fact]
    public async Task Vm_CreateProfile_ActivatesAndPersistsIt()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());

        vm.NewProfileName = "Legolas";
        vm.CreateProfileCommand.Execute(null);

        Assert.True(vm.IsProfileSelected);
        Assert.Equal("Legolas", vm.ActiveProfileName);
        Assert.Contains("Legolas", vm.AvailableProfiles);
        Assert.True(service.Exists("Legolas"));
    }

    [Fact]
    public async Task Vm_CreateProfile_PersistsSeparateLoginAndEndpoint()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService())
        {
            NewProfileName = "Łucznik",
            NewProfileLogin = "Legolas",
            NewProfileHost = "mud.example.org",
            NewProfilePort = 4444,
        };

        vm.CreateProfileCommand.Execute(null);

        var stored = Assert.IsType<ProfileData>(service.Load("Łucznik"));
        Assert.Equal("Łucznik", vm.ActiveProfileName);
        Assert.Equal("Legolas", stored.Login);
        Assert.Equal("mud.example.org", stored.Host);
        Assert.Equal(4444, stored.Port);
        Assert.Equal("mud.example.org", vm.Host);
        Assert.Equal(4444, vm.Port);
    }

    [Fact]
    public async Task Vm_SelectProfile_LoadsAndUpdatesPerAccountEndpoint()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Mag",
            Login = "Gandalf",
            Host = "first.example.org",
            Port = 4001,
        });

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.SelectedProfileName = "Mag";

        Assert.Equal("Gandalf", vm.SelectedProfileLogin);
        Assert.Equal("first.example.org", vm.Host);
        Assert.Equal(4001, vm.Port);

        vm.SelectedProfileLogin = "Mithrandir";
        vm.Host = "second.example.org";
        vm.Port = 5005;
        vm.SelectProfileCommand.Execute(null);

        var stored = Assert.IsType<ProfileData>(service.Load("Mag"));
        Assert.Equal("Mithrandir", stored.Login);
        Assert.Equal("second.example.org", stored.Host);
        Assert.Equal(5005, stored.Port);
    }

    [Fact]
    public async Task Vm_SelectLegacyProfile_UsesNameAsLoginAndDefaultEndpoint()
    {
        var service = CreateService();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "StareKonto.json"), """{"Name":"StareKonto"}""");

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.SelectedProfileName = "StareKonto";

        Assert.Equal("StareKonto", vm.SelectedProfileLogin);
        Assert.Equal("killer-mud.pl", vm.Host);
        Assert.Equal(4004, vm.Port);
    }

    [Fact]
    public async Task Vm_SelectProfile_LoadsNotesAndRules()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Gimli",
            Notes = [new ProfileNote { Title = "Kopalnia", Content = "Moria", CreatedAt = "x" }],
            Rules = [new ProfileRule { Name = "Skrót k", Type = "alias", Pattern = "^k$", Action = "kill", IsEnabled = true }],
        });

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.SelectedProfileName = "Gimli";
        vm.SelectProfileCommand.Execute(null);

        Assert.Equal("Gimli", vm.ActiveProfileName);
        Assert.Contains(vm.Notes, n => n.Title == "Kopalnia");
        var rule = Assert.Single(vm.AutomationRules);
        Assert.Equal("kill", rule.Action);
    }

    [Fact]
    public async Task Vm_AddNote_PersistsToActiveProfile()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Frodo";
        vm.CreateProfileCommand.Execute(null);

        vm.NewNoteTitle = "Pierścień";
        vm.NewNoteContent = "Zniszczyć w Mordorze";
        vm.AddNoteCommand.Execute(null);

        var stored = service.Load("Frodo");
        Assert.NotNull(stored);
        Assert.Contains(stored!.Notes, n => n.Title == "Pierścień");
    }

    [Fact]
    public async Task Vm_SwitchProfile_WhenDisconnected_SavesAndShowsPicker()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Sam";
        vm.CreateProfileCommand.Execute(null);
        Assert.True(vm.SwitchProfileCommand.CanExecute(null));

        vm.SwitchProfileCommand.Execute(null);

        Assert.False(vm.IsProfileSelected);
        Assert.Equal("Sam", vm.SelectedProfileName);
        Assert.True(service.Exists("Sam"));
    }

    [Fact]
    public async Task Vm_AddRule_PersistsToActiveProfile()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Arwena";
        vm.CreateProfileCommand.Execute(null);

        vm.NewRuleName = "Zabij cel";
        vm.NewRuleType = "trigger";
        vm.NewRulePattern = @"^(\w+) atakuje cie";
        vm.NewRuleAction = "zabij $1";
        Assert.True(vm.AddRuleCommand.CanExecute(null));
        vm.AddRuleCommand.Execute(null);

        var stored = service.Load("Arwena");
        Assert.NotNull(stored);
        Assert.Contains(stored!.Rules, r =>
            r.Type == "trigger" && r.Pattern == @"^(\w+) atakuje cie" && r.Action == "zabij $1" && r.IsEnabled);

        // Form is cleared after adding.
        Assert.Equal(string.Empty, vm.NewRuleName);
        Assert.Equal(string.Empty, vm.NewRulePattern);
    }

    [Fact]
    public async Task Vm_AddRule_InvalidRegex_IsBlockedWithError()
    {
        await using var vm = new MainWindowViewModel(CreateService(), CreateSettingsService());
        vm.NewProfileName = "Elrond";
        vm.CreateProfileCommand.Execute(null);

        vm.NewRuleName = "Zepsuta";
        vm.NewRulePattern = "([niedomknieta";
        vm.NewRuleAction = "cokolwiek";

        Assert.True(vm.HasNewRulePatternError);
        Assert.False(vm.AddRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task Vm_ToggleAndDeleteRule_PersistToActiveProfile()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Galadriela";
        vm.CreateProfileCommand.Execute(null);

        vm.NewRuleName = "Skrót zbroja";
        vm.NewRuleType = "alias";
        vm.NewRulePattern = "^zb$";
        vm.NewRuleAction = "zaloz zbroje";
        vm.AddRuleCommand.Execute(null);

        var rule = vm.AutomationRules.Single(r => r.Name == "Skrót zbroja");
        vm.ToggleRuleCommand.Execute(rule);
        Assert.False(rule.IsEnabled);
        Assert.False(service.Load("Galadriela")!.Rules.Single(r => r.Name == "Skrót zbroja").IsEnabled);

        vm.DeleteRuleCommand.Execute(rule);
        Assert.DoesNotContain(service.Load("Galadriela")!.Rules, r => r.Name == "Skrót zbroja");
    }

    [Fact]
    public async Task Vm_AddTimer_PersistsToActiveProfile()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Merry";
        vm.CreateProfileCommand.Execute(null);

        vm.NewTimerName = "Jedzenie";
        vm.NewTimerMinutes = "2";
        vm.NewTimerSeconds = "0";
        vm.NewTimerMilliseconds = "500";
        vm.NewTimerCommands = "zjedz chleb\nwypij wode";
        vm.AddTimerCommand.Execute(null);

        var entry = Assert.Single(vm.Timers);
        Assert.Equal("Jedzenie", entry.Name);
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(500), entry.Interval);
        Assert.False(entry.IsEnabled);

        var stored = service.Load("Merry");
        Assert.NotNull(stored);
        var timer = Assert.Single(stored!.Timers);
        Assert.Equal(["zjedz chleb", "wypij wode"], timer.Commands);
        Assert.Equal("zjedz chleb\nwypij wode", timer.CommandsText);
    }

    [Fact]
    public async Task Vm_AddTimer_RejectsZeroInterval()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Boromir";
        vm.CreateProfileCommand.Execute(null);

        vm.NewTimerName = "Zły timer";
        vm.NewTimerMinutes = "0";
        vm.NewTimerSeconds = "0";
        vm.NewTimerMilliseconds = "0";
        vm.NewTimerCommands = "look";
        vm.AddTimerCommand.Execute(null);

        Assert.Empty(vm.Timers);
    }

    [Fact]
    public async Task Vm_ToggleTimer_FlipsEnabledAndPersists()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Eowina";
        vm.CreateProfileCommand.Execute(null);

        vm.NewTimerName = "Skan";
        vm.NewTimerSeconds = "30";
        vm.NewTimerCommands = "scan";
        vm.AddTimerCommand.Execute(null);

        var entry = Assert.Single(vm.Timers);
        vm.ToggleTimerCommand.Execute(entry);
        Assert.True(entry.IsEnabled);
        Assert.NotEmpty(entry.RemainingText);
        Assert.True(service.Load("Eowina")!.Timers.Single().IsEnabled);

        vm.ToggleTimerCommand.Execute(entry);
        Assert.False(entry.IsEnabled);
        Assert.Empty(entry.RemainingText);
        Assert.False(service.Load("Eowina")!.Timers.Single().IsEnabled);
    }

    [Fact]
    public async Task Vm_SelectProfile_LoadsTimers()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Faramir",
            Timers =
            [
                new ProfileTimer
                {
                    Name = "Patrol",
                    Seconds = 45,
                    Commands = ["look", "scan"],
                    IsEnabled = false,
                },
            ],
        });

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.SelectedProfileName = "Faramir";
        vm.SelectProfileCommand.Execute(null);

        var entry = Assert.Single(vm.Timers);
        Assert.Equal("Patrol", entry.Name);
        Assert.Equal(["look", "scan"], entry.GetCommands());
        // Fallback from old-style Commands list populates CommandsText
        Assert.NotEmpty(entry.CommandsText);
        Assert.Contains("look", entry.CommandsText);
        Assert.Contains("scan", entry.CommandsText);
    }

    [Fact]
    public async Task Vm_SelectProfile_LoadsTimers_WithCommandsText()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Denethor",
            Timers =
            [
                new ProfileTimer
                {
                    Name = "Watch",
                    Seconds = 60,
                    CommandsText = "look;north\nscan",
                    Commands = ["look", "north", "scan"],
                    IsEnabled = true,
                },
            ],
        });

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.SelectedProfileName = "Denethor";
        vm.SelectProfileCommand.Execute(null);

        var entry = Assert.Single(vm.Timers);
        Assert.Equal("Watch", entry.Name);
        Assert.Equal("look;north\nscan", entry.CommandsText);
        // Splits using default separator (;) — look, north, scan
        var cmds = entry.GetCommands(vm.CommandStackingSeparator);
        Assert.Equal(3, cmds.Count);
        Assert.Equal("look", cmds[0]);
        Assert.Equal("north", cmds[1]);
        Assert.Equal("scan", cmds[2]);
    }

    [Fact]
    public async Task Vm_SelectProfile_ReplacesPreviousProfileData()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Bilbo",
            Notes = [new ProfileNote { Title = "Smaug", Content = "", CreatedAt = "x" }],
        });

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Pippin";
        vm.CreateProfileCommand.Execute(null);
        vm.NewNoteTitle = "Notatka Pippina";
        vm.AddNoteCommand.Execute(null);

        vm.SwitchProfileCommand.Execute(null);
        vm.SelectedProfileName = "Bilbo";
        vm.SelectProfileCommand.Execute(null);

        Assert.Equal("Bilbo", vm.ActiveProfileName);
        Assert.DoesNotContain(vm.Notes, n => n.Title == "Notatka Pippina");
        Assert.Contains(vm.Notes, n => n.Title == "Smaug");
    }

    // ====================================================================
    // Folders (Etap 2 — model + persistence)
    // ====================================================================

    [Fact]
    public void SaveAndLoad_RoundTripsFoldersAndFolderId()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Thorin",
            Folders = [new ProfileFolder { Id = "f1", Name = "PvP", Kind = FolderKind.Aliases }],
            Rules = [new ProfileRule { Name = "R", Type = "alias", Pattern = "^k$", Action = "kill", FolderId = "f1" }],
        });

        var loaded = service.Load("Thorin");

        Assert.NotNull(loaded);
        var folder = Assert.Single(loaded!.Folders);
        Assert.Equal("PvP", folder.Name);
        Assert.Equal(FolderKind.Aliases, folder.Kind);
        Assert.Equal("f1", Assert.Single(loaded.Rules).FolderId);
    }

    [Fact]
    public void Load_ProfileJsonWithoutFolders_IsBackwardCompatible()
    {
        var service = CreateService();
        Directory.CreateDirectory(_directory);
        // JSON written before folders existed — no Folders array, no FolderId.
        File.WriteAllText(Path.Combine(_directory, "Kili.json"),
            """{"Name":"Kili","Rules":[{"Name":"R","Type":"alias","Pattern":"^l$","Action":"look","IsEnabled":true}]}""");

        var loaded = service.Load("Kili");

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Folders);
        var rule = Assert.Single(loaded.Rules);
        Assert.Null(rule.FolderId);
    }

    [Fact]
    public async Task Vm_SelectProfile_LoadsFoldersAndItemMembership()
    {
        var service = CreateService();
        service.Save(new ProfileData
        {
            Name = "Dwalin",
            Folders = [new ProfileFolder { Id = "f1", Name = "Walka", Kind = FolderKind.Triggers }],
            Rules = [new ProfileRule { Name = "T", Type = "trigger", Pattern = "^x$", Action = "y", IsEnabled = true, FolderId = "f1" }],
        });

        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.SelectedProfileName = "Dwalin";
        vm.SelectProfileCommand.Execute(null);

        var folder = Assert.Single(vm.Folders);
        Assert.Equal("Walka", folder.Name);
        Assert.Equal("f1", Assert.Single(vm.TriggerRules).FolderId);
    }

    [Fact]
    public async Task Vm_GlobalFolder_PersistsToGlobalFileWithItems()
    {
        var service = CreateService();
        await using var vm = new MainWindowViewModel(service, CreateSettingsService());
        vm.NewProfileName = "Nori";
        vm.CreateProfileCommand.Execute(null);

        var folder = new FolderNode { Name = "Wspólne", Kind = FolderKind.Aliases, IsGlobal = true };
        vm.Folders.Add(folder);
        var alias = new AutomationRuleEntry("a", "alias", "^l$", "look", true, isGlobal: true) { FolderId = folder.Id };
        vm.AutomationRules.Add(alias);
        InvokeSaveActiveProfile(vm);

        var global = service.LoadGlobal();
        Assert.Equal("Wspólne", Assert.Single(global.Folders).Name);
        Assert.Equal(folder.Id, Assert.Single(global.Rules).FolderId);
        // The profile file must NOT carry the global folder/rule.
        var storedProfile = service.Load("Nori")!;
        Assert.Empty(storedProfile.Folders);
        Assert.DoesNotContain(storedProfile.Rules, r => r.FolderId == folder.Id);
    }

    [Fact]
    public async Task SetFolderGlobalCascade_MarksSubtreeAndItemsThenClears()
    {
        await using var vm = new MainWindowViewModel(CreateService(), CreateSettingsService());
        var parent = new FolderNode { Name = "PvP", Kind = FolderKind.Aliases };
        var child = new FolderNode { Name = "Sub", Kind = FolderKind.Aliases, ParentId = parent.Id };
        vm.Folders.Add(parent);
        vm.Folders.Add(child);
        var alias = new AutomationRuleEntry("a", "alias", "^x$", "y", true) { FolderId = child.Id };
        vm.AutomationRules.Add(alias);

        InvokeSetFolderGlobalCascade(vm, parent, true);
        Assert.True(parent.IsGlobal);
        Assert.True(child.IsGlobal);
        Assert.True(alias.IsGlobal);

        InvokeSetFolderGlobalCascade(vm, parent, false);
        Assert.False(parent.IsGlobal);
        Assert.False(child.IsGlobal);
        Assert.False(alias.IsGlobal);
    }

    private static void InvokeSetFolderGlobalCascade(MainWindowViewModel vm, FolderNode folder, bool isGlobal)
    {
        var method = typeof(MainWindowViewModel).GetMethod("SetFolderGlobalCascade",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(vm, [folder, isGlobal]);
    }

    private static void InvokeSaveActiveProfile(MainWindowViewModel vm)
    {
        var method = typeof(MainWindowViewModel).GetMethod("SaveActiveProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }
}
