using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Gmcp;
using MudClient.Core.Map;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class ForkIntegrationTests
{
    private static object? Invoke(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args);

    private static void Set(MainWindowViewModel vm, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);

    private static T Get<T>(MainWindowViewModel vm, string name) =>
        (T)typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("fork-integration-").FullName;
        public ProfileService Profiles { get; }
        public MainWindowViewModel Vm { get; }
        public List<string> Output { get; } = [];

        public Fixture()
        {
            Profiles = new ProfileService(DirectoryPath);
            Vm = new MainWindowViewModel(Profiles, new AppSettingsService(DirectoryPath),
                layoutPresetService: new LayoutPresetService(DirectoryPath),
                groupSpellStore: new GroupSpellStore(Path.Combine(DirectoryPath, "group-spells.json")));
            Vm.OutputReceived += Output.Add;
        }

        public async ValueTask DisposeAsync()
        {
            await Vm.DisposeAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static void ArrangeWalk(MainWindowViewModel vm, string position)
    {
        var from = Room(1);
        var to = Room(2);
        Set(vm, "_autowalkPath", new MapPath
        {
            From = from, To = to, Steps = [new MapPathStep("north", to)], TotalCost = 1,
        });
        Set(vm, "_autowalkTargetName", "Cel");
        Set(vm, "_latestCharacterPosition", position);
        Set(vm, "_isConnected", true);
    }

    private static MapRoom Room(int id, params MapExit[] exits) => new()
    {
        Id = id, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0), Exits = exits,
        UserData = new Dictionary<string, JsonElement> { ["vnum"] = JsonSerializer.SerializeToElement(id.ToString()) },
    };

    private static void ArrangeFollow(MainWindowViewModel vm)
    {
        var rooms = new[] { Room(1, new MapExit { Name = "north", ExitId = 2 }),
            Room(2, new MapExit { Name = "east", ExitId = 3 }), Room(3) };
        var index = new MapIndex(new MapDocument { Areas = [new MapArea { Id = 1, Rooms = rooms.ToList() }] });
        typeof(MapViewModel).GetField("_mapIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm.Map, index);
        var resolver = Get<object>(vm, "_locationResolver");
        resolver.GetType().GetProperty("CurrentVnum")!.SetValue(resolver, "1");
        Set(vm, "_isConnected", true);
        Set(vm, "_latestCharacterName", "Follower");
        Set(vm, "_latestCharacterPosition", "resting");
        vm.AutoFollowLeaderEnabled = true;
    }

    private static CharacterGroupUpdate LeaderAt(string room) => new("Leader",
        [new("Leader", "standing", "", null, "", null, null, false, room, IsLeader: true)]);

    [AvaloniaFact]
    public async Task Autofollow_StartsFromRest_RetargetsAfterCombat_StopsWhenLeaderReturns()
    {
        await using var f = new Fixture();
        ArrangeFollow(f.Vm);
        Invoke(f.Vm, "OnGroupChanged", LeaderAt("1"));
        Dispatcher.UIThread.RunJobs();
        Invoke(f.Vm, "OnGroupChanged", LeaderAt("2"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2", Get<MapPath>(f.Vm, "_autowalkPath").To.Vnum);
        Assert.DoesNotContain(f.Output, line => line.Contains("> stand"));
        Invoke(f.Vm, "UpdateCharacterPosition", "fighting");
        Invoke(f.Vm, "OnGroupChanged", LeaderAt("3"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2", Get<MapPath>(f.Vm, "_autowalkPath").To.Vnum);
        Invoke(f.Vm, "UpdateCharacterPosition", "standing");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("3", Get<MapPath>(f.Vm, "_autowalkPath").To.Vnum);
        Assert.Equal(["north", "east"], Get<MapPath>(f.Vm, "_autowalkPath").Steps.Select(step => step.Command));
        Invoke(f.Vm, "OnGroupChanged", LeaderAt("1"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Vm.IsAutowalking);
    }

    [AvaloniaFact]
    public async Task LeaderTrail_CapsAt300_AndClearsOnGroupLossAndDisconnect()
    {
        await using var f = new Fixture();
        for (var i = 0; i < 310; i++) Invoke(f.Vm, "UpdateLeaderRoomTrail", LeaderAt(i.ToString()));
        var trail = Get<List<string>>(f.Vm, "_leaderRoomTrail");
        Assert.Equal(300, trail.Count);
        Assert.Equal("10", trail[0]);
        Invoke(f.Vm, "UpdateLeaderRoomTrail", new CharacterGroupUpdate(null, []));
        Assert.Empty(trail);
        Invoke(f.Vm, "UpdateLeaderRoomTrail", LeaderAt("1"));
        Invoke(f.Vm, "ClearLiveGroupState");
        Assert.Empty(trail);
    }

    [AvaloniaTheory]
    [InlineData("resting", false)]
    [InlineData("standing", false)]
    [InlineData("sleeping", true)]
    [InlineData("sitting", true)]
    public async Task Walk_StandingRequiredOnlyForSitOrSleep(string position, bool needsStand)
    {
        await using var f = new Fixture();
        ArrangeWalk(f.Vm, position);
        Invoke(f.Vm, "SendAutowalkStep", false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(needsStand, Get<bool>(f.Vm, "_autowalkRecoveringPosition"));
        Assert.Equal(needsStand, f.Output.Any(line => line.Contains("> stand")));
        Assert.Equal(!needsStand, f.Output.Any(line => line.Contains("> north")));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestOrderDuringWalk_DoesNotStrandAutofollow_ButManualRestStillPauses(bool following)
    {
        await using var f = new Fixture();
        ArrangeWalk(f.Vm, "standing");
        Set(f.Vm, "_autowalkIsFollowingLeader", following);
        Invoke(f.Vm, "UpdateCharacterPosition", "resting");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(!following, Get<bool>(f.Vm, "_autowalkPausedForResting"));
        Invoke(f.Vm, "SendAutowalkStep", false);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(f.Output, line => line.Contains("> stand"));
        Assert.Equal(following, f.Output.Any(line => line.Contains("> north")));
    }

    [AvaloniaTheory]
    [InlineData("standing")]
    [InlineData("sitting")]
    [InlineData("sleeping")]
    public async Task InterruptedGate_ResumesAfterCombatAndRequiredStand(string position)
    {
        await using var f = new Fixture();
        ArrangeWalk(f.Vm, "fighting");
        Set(f.Vm, "_autowalkWaitingForGate", true);
        await (Task)Invoke(f.Vm, "SendGateCommandsAsync", CancellationToken.None)!;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(f.Output, line => line.Contains("> zapukaj"));
        Invoke(f.Vm, "UpdateCharacterPosition", position);
        Dispatcher.UIThread.RunJobs();
        if (position != "standing")
        {
            Assert.DoesNotContain(f.Output, line => line.Contains("> zapukaj"));
            Invoke(f.Vm, "UpdateCharacterPosition", "standing");
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Contains(f.Output, line => line.Contains("> zapukaj"));
        Assert.Empty(Get<HashSet<int>>(f.Vm, "_autoFarmSessionExcludedRoomIds"));
    }

    [AvaloniaFact]
    public async Task TimeoutDoorRecovery_DoesNotSendCommandsDuringLiveCombat()
    {
        await using var f = new Fixture();
        ArrangeWalk(f.Vm, "fighting");
        await (Task)Invoke(f.Vm, "SendStuckStepRecoveryCommandsAsync", 0, CancellationToken.None)!;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(f.Output, line => line.Contains("> open") || line.Contains("> zapukaj"));
        Assert.True(Get<bool>(f.Vm, "_autowalkPausedForCombat"));
        Assert.Empty(Get<HashSet<int>>(f.Vm, "_autoFarmSessionExcludedRoomIds"));
    }

    [AvaloniaFact]
    public async Task LowMovementRecovery_StillOwnsItsRestDelay()
    {
        await using var f = new Fixture();
        ArrangeWalk(f.Vm, "resting");
        Set(f.Vm, "_autowalkRecoveringMovement", true);
        Invoke(f.Vm, "SendAutowalkStep", false);
        Invoke(f.Vm, "BeginAutowalkStandRecovery");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(f.Output, line => line.Contains("> north") || line.Contains("> stand"));
        Assert.True(Get<bool>(f.Vm, "_autowalkRecoveringMovement"));
    }

    [AvaloniaFact]
    public async Task RecordingToggle_SharesAutomaticCaptureAndManualCommands()
    {
        await using var f = new Fixture();
        var changes = new List<string?>();
        f.Vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Invoke(f.Vm, "OnGmcpReceived", new GmcpMessage("Char.Vitals", """{"name":"Tester","hp":100,"maxhp":100}"""));
        Dispatcher.UIThread.RunJobs();
        var capture = Get<CombatSessionCaptureCoordinator>(f.Vm, "_combatCapture");
        var path = Assert.IsType<string>(capture.ActivePath);
        Assert.True(f.Vm.IsRecordingSession);
        Assert.Contains(nameof(f.Vm.IsRecordingSession), changes);
        Invoke(f.Vm, "StartTelnetLineCapture");
        Assert.Equal(path, capture.ActivePath);
        await f.Vm.ToggleSessionRecordingCommand.ExecuteAsync(null);
        Assert.False(f.Vm.IsRecordingSession);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.jsonl"));
        Assert.NotEmpty(await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken));
        Invoke(f.Vm, "OnGmcpReceived", new GmcpMessage("Char.Vitals", """{"name":"Tester"}"""));
        Assert.False(f.Vm.IsRecordingSession);
        await f.Vm.ToggleSessionRecordingCommand.ExecuteAsync(null);
        Assert.True(f.Vm.IsRecordingSession);
        Assert.NotEqual(path, capture.ActivePath);
        await (Task)Invoke(f.Vm, "StopCombatCaptureAfterConnectionClosedAsync")!;
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Vm.IsRecordingSession);
        Assert.Equal("⏺", f.Vm.SessionRecordingIcon);
    }

    [AvaloniaTheory]
    [InlineData(false, "trigger")]
    [InlineData(false, "alias")]
    [InlineData(true, "trigger")]
    [InlineData(true, "alias")]
    public async Task JsonRuleHotReload_UpdatesAndDeletesRules_WithoutRestartingTimer(bool global, string type)
    {
        await using var f = new Fixture();
        var rule = new ProfileRule { Id = "rule", Name = "obrona", Type = type, Pattern = "atak", Action = "blokuj", IsEnabled = true, IsGlobal = global };
        var timer = new ProfileTimer { Id = "timer", Name = "aktywny", Seconds = 30, CommandsText = "spojrz", IsEnabled = true, IsGlobal = global };
        var folder = new ProfileFolder { Id = "folder", Name = "Stary", Kind = type == "alias" ? FolderKind.Aliases : FolderKind.Triggers, IsGlobal = global };
        if (global)
        {
            f.Profiles.SaveGlobal(new GlobalData { Rules = [rule], Timers = [timer], Folders = [folder] });
            Invoke(f.Vm, "SaveActiveProfile");
        }
        else
        {
            var profile = new ProfileData { Name = "Tester", Rules = [rule], Timers = [timer], Folders = [folder] };
            f.Profiles.Save(profile);
            Invoke(f.Vm, "ActivateProfile", profile);
        }
        var running = Assert.Single(f.Vm.Timers);
        var now = DateTimeOffset.UtcNow;
        running.ScheduleNextActivation(now.AddSeconds(17), now);
        var remaining = running.RemainingText;
        var service = Get<MudClient.Core.Automation.MudTimerService>(f.Vm, "_timers");
        var baselineCancellations = (System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>)
            service.GetType().GetField("_timers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Contains("system:multibox-sync", baselineCancellations.Keys);
        var activeTokens = baselineCancellations.ToArray();
        void SaveChange(bool remove)
        {
            if (global)
            {
                var data = f.Profiles.LoadGlobal();
                if (remove) { data.Rules.Clear(); data.Folders.Clear(); }
                else { data.Rules[0].Action = "unik"; data.Folders[0].Name = "Nowy"; }
                f.Profiles.SaveGlobal(data);
                Set(f.Vm, "_globalLastKnownWriteUtc", DateTime.MinValue);
            }
            else
            {
                var data = f.Profiles.Load("Tester")!;
                if (remove) { data.Rules.Clear(); data.Folders.Clear(); }
                else { data.Rules[0].Action = "unik"; data.Folders[0].Name = "Nowy"; }
                f.Profiles.Save(data);
                Set(f.Vm, "_activeProfileLastKnownWriteUtc", DateTime.MinValue);
            }
            Invoke(f.Vm, "SaveActiveProfile");
        }
        SaveChange(false);
        Assert.Equal("unik", Assert.Single(f.Vm.AutomationRules).Action);
        Assert.Equal("Nowy", Assert.Single(f.Vm.Folders).Name);
        Assert.Same(running, Assert.Single(f.Vm.Timers));
        Assert.Equal(remaining, running.RemainingText);
        Assert.All(activeTokens, entry => Assert.Same(entry.Value, baselineCancellations[entry.Key]));
        SaveChange(true);
        Assert.Empty(f.Vm.AutomationRules);
        Assert.Empty(f.Vm.Folders);
        Assert.Same(running, Assert.Single(f.Vm.Timers));
    }

    [AvaloniaFact]
    public async Task PanicStop_DisablesStandaloneHealing_AndStartRestoresIt()
    {
        await using var f = new Fixture();
        f.Vm.AutoSelfHealEnabled = true;
        Invoke(f.Vm, "StopEverything");
        Assert.False(f.Vm.AutoSelfHealEnabled);
        Invoke(f.Vm, "StartEverything");
        Assert.True(f.Vm.AutoSelfHealEnabled);
    }
}
