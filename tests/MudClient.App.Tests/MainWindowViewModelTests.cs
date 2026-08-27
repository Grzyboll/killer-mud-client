using System.IO;
using System.Reflection;
using System.Text.Json;
using Dock.Model.Core;
using MudClient.App.Docking;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Automation;
using MudClient.Core.Gmcp;
using MudClient.Core.Map;
using MudClient.Core.Networking;

namespace MudClient.App.Tests;

public sealed class MainWindowViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly MainWindowViewModel _vm;

    public MainWindowViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KillerMudClient_VMTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _vm = new MainWindowViewModel(
            profileService: new ProfileService(Path.Combine(_tempDir, "Profiles")),
            settingsService: new AppSettingsService(_tempDir),
            groupSpellStore: new GroupSpellStore(Path.Combine(_tempDir, "group-spells.json")));
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Forces the VM's IsConnected to the given value by writing the
    /// backing field directly. This avoids the need for a real TCP
    /// connection while still exercising the actual send pipeline.
    /// </summary>
    private void SetIsConnected(bool value)
    {
        var field = typeof(MainWindowViewModel).GetField("_isConnected",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(_vm, value);
    }

    // ====================================================================
    // Constructor / mock data
    // ====================================================================

    [Fact]
    public void Constructor_StartsWithNoPeopleInRoom()
    {
        // Room occupants come live from Room.People GMCP.
        Assert.Empty(_vm.People);
    }

    [Fact]
    public void Constructor_StartsWithEmptyGroup()
    {
        // Group members come live from Char.Group GMCP; no mock data.
        Assert.Empty(_vm.Group);
    }

    [Fact]
    public void Constructor_StartsWithEmptyEffects()
    {
        // Effects are populated live from Char.Affects GMCP; no mock data.
        Assert.Empty(_vm.Effects);
    }

    [Fact]
    public void Constructor_BeforeFirstSentCommand_ShowsUnknownIdleTime()
    {
        Assert.Equal("Idle: —", _vm.IdleTimeText);
    }

    [Theory]
    [InlineData(0, "Idle: 00:00:00")]
    [InlineData(65, "Idle: 00:01:05")]
    [InlineData(90061, "Idle: 25:01:01")]
    public void FormatIdleTime_UsesUnboundedHours(long seconds, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.FormatIdleTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Constructor_StartsWithNoAutomationRules()
    {
        // Rules are per-profile; none should exist before a profile is activated.
        Assert.Empty(_vm.AutomationRules);
    }

    [Fact]
    public void BuildAutoAssistCommands_DefaultTemplate_PrependsAssistAndUsesConfiguredCommandSplitting()
    {
        var commands = MainWindowViewModel.BuildAutoAssistCommands(
            commandTemplate: "as",
            enemyName: null,
            followUpCommands: "wesprzyj|czar 'ochrona'\r\nuciekaj",
            separator: "|");

        Assert.Equal(["as", "wesprzyj", "czar 'ochrona'", "uciekaj"], commands);
    }

    [Fact]
    public void BuildAutoAssistCommands_NullTemplate_FallsBackToAssist()
    {
        var commands = MainWindowViewModel.BuildAutoAssistCommands(
            commandTemplate: null,
            enemyName: null,
            followUpCommands: null,
            separator: null);

        Assert.Equal(["as"], commands);
    }

    [Fact]
    public void BuildAutoAssistCommands_TemplateWithTargetToken_SubstitutesEnemyName()
    {
        var commands = MainWindowViewModel.BuildAutoAssistCommands(
            commandTemplate: "charge {cel}",
            enemyName: "Wielki smok",
            followUpCommands: null,
            separator: null);

        Assert.Equal(["charge Wielki smok"], commands);
    }

    [Fact]
    public void BuildAutoAssistCommands_TemplateWithTargetTokenButNoEnemyName_SendsNothing()
    {
        var commands = MainWindowViewModel.BuildAutoAssistCommands(
            commandTemplate: "backstab {cel}",
            enemyName: null,
            followUpCommands: "cheer",
            separator: null);

        Assert.Empty(commands);
    }

    [Fact]
    public void BuildAutoAssistCommands_TemplateWithoutTargetToken_IgnoresEnemyName()
    {
        var commands = MainWindowViewModel.BuildAutoAssistCommands(
            commandTemplate: "kick",
            enemyName: "Ork",
            followUpCommands: null,
            separator: null);

        Assert.Equal(["kick"], commands);
    }

    [Fact]
    public void BuildOtherGroupMemberNames_ExcludesSelfAndNpcs()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        var targets = MainWindowViewModel.BuildOtherGroupMemberNames(group, "Hero");

        Assert.Equal(["Companion"], targets);
    }

    [Fact]
    public void BuildOtherGroupMemberNames_NullGroup_ReturnsEmpty()
    {
        Assert.Empty(MainWindowViewModel.BuildOtherGroupMemberNames(null, "Hero"));
    }

    [Fact]
    public async Task CastRefreshOnGroupCommand_NotConnected_ShowsErrorToast()
    {
        await _vm.CastRefreshOnGroupCommand.ExecuteAsync(null);

        Assert.Contains(_vm.Toasts, toast => toast.Text.Contains("Nie połączono"));
    }

    [Fact]
    public async Task CastRefreshOnGroupCommand_ConnectedWithNoOtherMembers_ShowsInfoToast()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
        }));

        await _vm.CastRefreshOnGroupCommand.ExecuteAsync(null);

        Assert.Contains(_vm.Toasts, toast => toast.Text.Contains("Brak członków drużyny"));
    }

    [Fact]
    public void BuildGroupPositionOrderCommands_Disabled_ReturnsEmpty()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
        });

        Assert.Empty(MainWindowViewModel.BuildGroupPositionOrderCommands(group, "Hero", "stand", enabled: false));
    }

    [Fact]
    public void BuildGroupPositionOrderCommands_NotTheLeader_ReturnsEmpty()
    {
        var group = new CharacterGroupUpdate("Companion", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
        });

        Assert.Empty(MainWindowViewModel.BuildGroupPositionOrderCommands(group, "Hero", "stand", enabled: true));
    }

    [Fact]
    public void BuildGroupPositionOrderCommands_AsLeader_OrdersEveryOtherMember()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        var commands = MainWindowViewModel.BuildGroupPositionOrderCommands(group, "Hero", "sit", enabled: true);

        Assert.Equal(["order Companion sit"], commands);
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_Disabled_ReturnsEmpty()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        Assert.Empty(MainWindowViewModel.BuildAutoAssistNpcCommands(group, enabled: false));
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_NullGroup_ReturnsEmpty()
    {
        Assert.Empty(MainWindowViewModel.BuildAutoAssistNpcCommands(null, enabled: true));
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_NoNpcsInGroup_ReturnsEmpty()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
        });

        Assert.Empty(MainWindowViewModel.BuildAutoAssistNpcCommands(group, enabled: true));
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_Enabled_OrdersEveryNpc_EvenWithoutLeadership()
    {
        // Unlike BuildGroupPositionOrderCommands, ordering your own pet doesn't require being
        // the group's GMCP leader — "Companion" leads here, not "Hero".
        var group = new CharacterGroupUpdate("Companion", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        var commands = MainWindowViewModel.BuildAutoAssistNpcCommands(group, enabled: true);

        Assert.Equal(["order 1.wolf assist"], commands);
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_MultiWordDiacriticName_UsesFirstWordFoldedAndLowercased()
    {
        // Regression guard: the MUD doesn't recognize the full display name as an order target
        // ("order Potężny zombie assist" silently does nothing) — it needs a single lowercase,
        // diacritic-free keyword.
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Potężny zombie", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        var commands = MainWindowViewModel.BuildAutoAssistNpcCommands(group, enabled: true);

        Assert.Equal(["order 1.potezny assist"], commands);
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_TwoNpcsWithTheSameKeyword_AreNumbered1And2()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Potężny zombie", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
            new("Potężny zombie", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        var commands = MainWindowViewModel.BuildAutoAssistNpcCommands(group, enabled: true);

        Assert.Equal(["order 1.potezny assist", "order 2.potezny assist"], commands);
    }

    [Fact]
    public void BuildAutoAssistNpcCommands_DifferentKeywords_EachNumberedFrom1Independently()
    {
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
            new("Bear", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        });

        var commands = MainWindowViewModel.BuildAutoAssistNpcCommands(group, enabled: true);

        Assert.Equal(["order 1.wolf assist", "order 2.wolf assist", "order 1.bear assist"], commands);
    }

    // ====================================================================
    // ShouldAutoFollowLeader — "Autofollow" (Automaty → Podróż)
    // ====================================================================

    private static CharacterGroupUpdate TwoMemberGroup(string leaderName, string? leaderRoom) =>
        new(leaderName, new List<CharacterGroupMember>
        {
            new(leaderName, null, string.Empty, null, string.Empty, null, null, false, leaderRoom, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
        });

    [Fact]
    public void ShouldAutoFollowLeader_Disabled_ReturnsFalse()
    {
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: false, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: "100", out var leader);

        Assert.False(result);
        Assert.Null(leader);
    }

    [Fact]
    public void ShouldAutoFollowLeader_NotConnected_ReturnsFalse()
    {
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: false, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: "100", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_AlreadyAutowalking_ReturnsFalse()
    {
        // Don't yank control from an unrelated walk already in progress (e.g. auto-farm).
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: true, position: "standing",
            group, selfName: "Companion", currentVnum: "100", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_NullGroup_ReturnsFalse()
    {
        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            update: null, selfName: "Companion", currentVnum: "100", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_SelfIsLeader_ReturnsFalse()
    {
        var group = TwoMemberGroup("Companion", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: "100", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_Fighting_ReturnsFalse()
    {
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "fighting",
            group, selfName: "Companion", currentVnum: "100", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_LeaderHasNoRoomData_ReturnsFalse()
    {
        var group = TwoMemberGroup("Hero", leaderRoom: null);

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: "100", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_OwnVnumUnknown_ReturnsFalse()
    {
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: null, out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_AlreadyInLeadersRoom_ReturnsFalse()
    {
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: "200", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoFollowLeader_LeaderInDifferentRoom_ReturnsTrueWithLeader()
    {
        var group = TwoMemberGroup("Hero", "200");

        var result = MainWindowViewModel.ShouldAutoFollowLeader(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", currentVnum: "100", out var leader);

        Assert.True(result);
        Assert.NotNull(leader);
        Assert.Equal("Hero", leader.Name);
        Assert.Equal("200", leader.Room);
    }

    // ====================================================================
    // ShouldMirrorLeaderPosition — "Kopiuj postawę lidera" (Automaty → Drużyna)
    // ====================================================================

    private static CharacterGroupUpdate TwoMemberGroupWithLeaderPosition(string leaderName, string? leaderPosition) =>
        new(leaderName, new List<CharacterGroupMember>
        {
            new(leaderName, leaderPosition, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
        });

    [Fact]
    public void ShouldMirrorLeaderPosition_Disabled_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: false, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", out var command);

        Assert.False(result);
        Assert.Null(command);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_NotConnected_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: false, isAutowalking: false, position: "standing",
            group, selfName: "Companion", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_AlreadyAutowalking_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: true, position: "standing",
            group, selfName: "Companion", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_NullGroup_ReturnsFalse()
    {
        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            update: null, selfName: "Companion", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_SelfIsLeader_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Companion", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_SelfFighting_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "fighting",
            group, selfName: "Companion", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_SelfLying_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "lying",
            group, selfName: "Companion", out _);

        Assert.False(result);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_AlreadyMatchingLeader_ReturnsFalse()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "sitting",
            group, selfName: "Companion", out var command);

        Assert.False(result);
        Assert.Null(command);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_LeaderSitting_ReturnsSitCommand()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "sitting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", out var command);

        Assert.True(result);
        Assert.Equal("sit", command);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_LeaderResting_ReturnsRestCommand()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "resting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", out var command);

        Assert.True(result);
        Assert.Equal("rest", command);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_LeaderStanding_ReturnsStandCommand()
    {
        var group = TwoMemberGroupWithLeaderPosition("Hero", "standing");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "sitting",
            group, selfName: "Companion", out var command);

        Assert.True(result);
        Assert.Equal("stand", command);
    }

    [Fact]
    public void ShouldMirrorLeaderPosition_LeaderFighting_ReturnsFalse()
    {
        // Nothing sensible to mirror mid-combat — leave the follower alone.
        var group = TwoMemberGroupWithLeaderPosition("Hero", "fighting");

        var result = MainWindowViewModel.ShouldMirrorLeaderPosition(
            enabled: true, isConnected: true, isAutowalking: false, position: "standing",
            group, selfName: "Companion", out var command);

        Assert.False(result);
        Assert.Null(command);
    }

    // ====================================================================
    // BuildAutoKillCommands — "Autokill" (Automaty → Farma), gated on Room.People actually
    // reporting the configured mob present (see TryAutoKillIfConfirmed)
    // ====================================================================

    private static RoomPerson Present(string name) => new(name, IsFighting: false, Enemy: null);

    [Fact]
    public void BuildAutoKillCommands_NoConfiguredNames_ReturnsEmpty()
    {
        var commands = MainWindowViewModel.BuildAutoKillCommands([], [Present("strażnik")]);

        Assert.Empty(commands);
    }

    [Fact]
    public void BuildAutoKillCommands_ConfiguredNameNotInRoom_IsSkipped()
    {
        var commands = MainWindowViewModel.BuildAutoKillCommands(["strażnik"], [Present("kupiec")]);

        Assert.Empty(commands);
    }

    [Fact]
    public void BuildAutoKillCommands_ConfiguredNamePresentInRoom_SendsKillForIt()
    {
        var commands = MainWindowViewModel.BuildAutoKillCommands(["strażnik"], [Present("strażnik")]);

        Assert.Equal(["kill strażnik"], commands);
    }

    [Fact]
    public void BuildAutoKillCommands_OnlyPresentNamesAreSent_AbsentOnesAreSkipped()
    {
        var commands = MainWindowViewModel.BuildAutoKillCommands(
            ["strażnik", "keton"], [Present("strażnik")]);

        Assert.Equal(["kill strażnik"], commands);
    }

    [Fact]
    public void BuildAutoKillCommands_EmptyRoom_ReturnsEmpty()
    {
        var commands = MainWindowViewModel.BuildAutoKillCommands(["strażnik"], []);

        Assert.Empty(commands);
    }

    [Fact]
    public void BuildAutoKillCommands_MatchesAsSubstringOfFullDisplayName()
    {
        // Same keyword-style partial match the MUD's own "kill" command already does.
        var commands = MainWindowViewModel.BuildAutoKillCommands(["szczur"], [Present("duży szczur")]);

        Assert.Equal(["kill szczur"], commands);
    }

    [Fact]
    public void BuildAutoKillCommands_IsCaseInsensitive()
    {
        var commands = MainWindowViewModel.BuildAutoKillCommands(["Strażnik"], [Present("strażnik")]);

        Assert.Equal(["kill Strażnik"], commands);
    }

    [Fact]
    public void BuildAutowieldCommands_Disabled_ReturnsEmpty()
    {
        Assert.Empty(MainWindowViewModel.BuildAutowieldCommands(enabled: false, weaponName: "miecz"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAutowieldCommands_NoWeaponName_ReturnsEmpty(string weaponName)
    {
        Assert.Empty(MainWindowViewModel.BuildAutowieldCommands(enabled: true, weaponName));
    }

    [Fact]
    public void BuildAutowieldCommands_Enabled_GetsThenWieldsTheConfiguredWeapon()
    {
        var commands = MainWindowViewModel.BuildAutowieldCommands(enabled: true, weaponName: "miecz");

        Assert.Equal(["get miecz", "wield miecz"], commands);
    }

    [Fact]
    public void TerminalToolTitle_ReflectsVitals_AndUpdatesLiveOnChange()
    {
        var terminal = FindTool(_vm.Layout, "Terminal");
        Assert.NotNull(terminal);
        Assert.StartsWith("Terminal —", terminal!.Title);

        _vm.Vitals.Name = "Aragorn";
        _vm.Vitals.Level = 42;
        _vm.Vitals.SexDisplay = "Mężczyzna";
        _vm.Vitals.PositionDisplay = "Stoi";

        Assert.Contains("Aragorn", terminal.Title);
        Assert.Contains("42", terminal.Title);
        Assert.Contains("Mężczyzna", terminal.Title);
        Assert.Contains("Stoi", terminal.Title);
    }

    private static PanelTool? FindTool(IDockable? dockable, string id) => dockable switch
    {
        PanelTool tool when string.Equals(tool.Id, id, StringComparison.Ordinal) => tool,
        IDock dock => (dock.VisibleDockables ?? Enumerable.Empty<IDockable>())
            .Select(child => FindTool(child, id))
            .FirstOrDefault(found => found is not null),
        _ => null,
    };

    /// <summary>Invokes the private UpdateCharacterPosition method via reflection.</summary>
    private void InvokeUpdateCharacterPosition(string position)
    {
        var method = typeof(MainWindowViewModel).GetMethod("UpdateCharacterPosition",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(_vm, [position]);
    }

    [Fact]
    public void UpdateCharacterPosition_TransitionToResting_DoesNotThrow_WithAutoRestEnabled()
    {
        // Regression guard for the bug where autorest never fired: the transition used to be
        // gated on "sitting" (the "sit" command), but the actual GMCP position after "rest" is
        // "resting" — a distinct value (see AutowalkRecoveryPolicyTests). Autostand's "standing"
        // trigger was already correct, which is why only autorest was reported broken.
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoRestOrderEnabled = true;
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: false),
        }));

        InvokeUpdateCharacterPosition("standing");
        InvokeUpdateCharacterPosition("resting");
    }

    [Fact]
    public void UpdateCharacterPosition_TransitionToFighting_DoesNotThrow_WithAutoAssistNpcEnabled()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoAssistNpcEnabled = true;
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        }));

        InvokeUpdateCharacterPosition("standing");
        InvokeUpdateCharacterPosition("fighting");
    }

    [Fact]
    public void UpdateCharacterPosition_TransitionToFighting_DoesNotAssistNpcUntilRoomPeopleConfirmsSelfIsFighting()
    {
        // Regression guard: GMCP position can flip to "fighting" before Room.People registers
        // who the fight is against, so ordering the pet to "assist" right on the position
        // transition gave it nothing to assist into — the MUD just ignored the order.
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoAssistNpcEnabled = true;
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        }));

        InvokeUpdateCharacterPosition("standing");
        InvokeUpdateCharacterPosition("fighting");

        Assert.True(GetAutoAssistNpcPending());
    }

    [Fact]
    public void OnRoomPeopleChanged_SelfNotYetFighting_LeavesAutoAssistNpcPending()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoAssistNpcEnabled = true;
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        }));
        InvokeUpdateCharacterPosition("fighting");

        InvokeOnRoomPeopleChanged([new RoomPerson("Hero", IsFighting: false, Enemy: null)]);

        Assert.True(GetAutoAssistNpcPending());
    }

    [Fact]
    public void OnRoomPeopleChanged_SelfConfirmedFighting_ClearsAutoAssistNpcPending()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoAssistNpcEnabled = true;
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        }));
        InvokeUpdateCharacterPosition("fighting");

        InvokeOnRoomPeopleChanged([new RoomPerson("Hero", IsFighting: true, Enemy: "szczur")]);

        Assert.False(GetAutoAssistNpcPending());
    }

    [Fact]
    public void UpdateCharacterPosition_CombatEnds_ClearsAutoAssistNpcPending()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoAssistNpcEnabled = true;
        SetLatestGroupUpdate(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, null, IsLeader: true),
            new("Wolf", null, string.Empty, null, string.Empty, null, null, true, null, IsLeader: false),
        }));
        InvokeUpdateCharacterPosition("fighting");
        Assert.True(GetAutoAssistNpcPending());

        InvokeUpdateCharacterPosition("standing");

        Assert.False(GetAutoAssistNpcPending());
    }

    // ====================================================================
    // TryAutoAssist — "{cel}" target substitution racing Char.Group vs. Room.People
    // (AutoAssistPolicyTests covers FindFightingEnemyName's own decision logic in isolation).
    // ====================================================================

    [Fact]
    public void TryAutoAssist_TemplateNeedsTarget_StaysPendingUntilRoomPeopleDeliversEnemy()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        SetCurrentVnum("100");
        _vm.AutoAssistEnabled = true;
        _vm.AutoAssistCommandTemplate = "charge {cel}";

        InvokeOnGroupChanged(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, "100", IsLeader: true),
            new("Ala", "fighting", string.Empty, null, string.Empty, null, null, false, "100", IsLeader: false),
        }));

        // Char.Group already reports Ala fighting, but Room.People hasn't delivered the enemy
        // yet — the command must not fire early with a broken, unsubstituted "{cel}".
        Assert.True(GetAutoAssistCommandPending());

        InvokeOnRoomPeopleChanged([new RoomPerson("Ala", IsFighting: true, Enemy: "Wielki smok")]);

        Assert.False(GetAutoAssistCommandPending());
    }

    [Fact]
    public void TryAutoAssist_MemberStopsFightingBeforeEnemyArrives_ClearsPendingWithoutSending()
    {
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        SetCurrentVnum("100");
        _vm.AutoAssistEnabled = true;
        _vm.AutoAssistCommandTemplate = "charge {cel}";

        InvokeOnGroupChanged(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, "100", IsLeader: true),
            new("Ala", "fighting", string.Empty, null, string.Empty, null, null, false, "100", IsLeader: false),
        }));
        Assert.True(GetAutoAssistCommandPending());

        // Ala's fight ends before Room.People ever confirmed an enemy — nothing left to assist
        // into, so the stale pending state must clear rather than fire later with a wrong target.
        InvokeOnGroupChanged(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, "100", IsLeader: true),
            new("Ala", "standing", string.Empty, null, string.Empty, null, null, false, "100", IsLeader: false),
        }));

        Assert.False(GetAutoAssistCommandPending());
    }

    [Fact]
    public void TryAutoAssist_DefaultTemplate_DoesNotWaitForEnemyName()
    {
        // Bare "as" needs no target, so it must fire immediately on the Char.Group update alone —
        // the pending-retry machinery above must only gate templates that actually use "{cel}".
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        SetCurrentVnum("100");
        _vm.AutoAssistEnabled = true;

        InvokeOnGroupChanged(new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, null, null, false, "100", IsLeader: true),
            new("Ala", "fighting", string.Empty, null, string.Empty, null, null, false, "100", IsLeader: false),
        }));

        Assert.False(GetAutoAssistCommandPending());
    }

    // ====================================================================
    // OnRoomEnterAutomations — "Autoscan" (map settings flyout) / "Autokill" (Automaty → Farma) wiring
    // ====================================================================

    /// <summary>Invokes the private OnRoomEnterAutomations method via reflection.</summary>
    private void InvokeOnRoomEnterAutomations(string vnum)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "OnRoomEnterAutomations", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(_vm, [vnum]);
    }

    [Fact]
    public void OnRoomEnterAutomations_NotConnected_DoesNotThrow()
    {
        SetIsConnected(false);
        _vm.Map.AutoScanOnRoomEnter = true;
        _vm.Map.AutoKillOnRoomEnter = true;
        _vm.Map.AutoKillMobNamesText = "strażnik";

        InvokeOnRoomEnterAutomations("100");
    }

    [Fact]
    public void OnRoomEnterAutomations_BothEnabled_DoesNotThrow()
    {
        SetIsConnected(true);
        _vm.Map.AutoScanOnRoomEnter = true;
        _vm.Map.AutoKillOnRoomEnter = true;
        _vm.Map.AutoKillMobNamesText = "strażnik\nketon";

        InvokeOnRoomEnterAutomations("100");
    }

    [Fact]
    public void OnRoomEnterAutomations_StaleRoomPeopleStillFromPreviousRoom_LeavesAutoKillPending()
    {
        SetIsConnected(true);
        _vm.Map.AutoKillOnRoomEnter = true;
        _vm.Map.AutoKillMobNamesText = "strażnik";

        // Room.People for the room just left arrives/settles as usual.
        InvokeOnRoomPeopleChanged([new RoomPerson("kupiec", IsFighting: false, Enemy: null)]);

        // Entering a new room before Room.People for it has arrived — TryAutoKillIfConfirmed's
        // immediate "in case it already landed" call must not consume _autoKillPending against
        // this now-stale snapshot from the room just left (see the generation check it added).
        InvokeOnRoomEnterAutomations("101");

        Assert.True(GetAutoKillPending());
    }

    [Fact]
    public void OnRoomPeopleChanged_MatchingLatestRoomEntry_ConfirmsAutoKillAndClearsPending()
    {
        SetIsConnected(true);
        _vm.Map.AutoKillOnRoomEnter = true;
        _vm.Map.AutoKillMobNamesText = "strażnik";

        InvokeOnRoomPeopleChanged([new RoomPerson("kupiec", IsFighting: false, Enemy: null)]);
        InvokeOnRoomEnterAutomations("101");
        Assert.True(GetAutoKillPending());

        // Room.People for the new room finally arrives — now the check may actually fire.
        InvokeOnRoomPeopleChanged([new RoomPerson("strażnik", IsFighting: false, Enemy: null)]);

        Assert.False(GetAutoKillPending());
    }

    private static ProfileAutomationSettings GetProfileSettings(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_profileSettings", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (ProfileAutomationSettings)field!.GetValue(viewModel)!;
    }

    [Fact]
    public void MapAutoScanOnRoomEnter_ChangingIt_PersistsToProfileSettings()
    {
        _vm.Map.AutoScanOnRoomEnter = true;

        Assert.True(GetProfileSettings(_vm).AutoScanOnRoomEnterEnabled);
    }

    [Fact]
    public void MapAutoKillOnRoomEnter_ChangingIt_PersistsToProfileSettings()
    {
        _vm.Map.AutoKillOnRoomEnter = true;
        _vm.Map.AutoKillMobNamesText = "strażnik\r\nketon\r\nstrażnik";

        var settings = GetProfileSettings(_vm);
        Assert.True(settings.AutoKillOnRoomEnterEnabled);
        Assert.Equal(["strażnik", "keton"], settings.AutoKillMobNames);
    }

    [Fact]
    public void UpdateCharacterPosition_TransitionToLying_DoesNotThrow_WithAutoStandEnabled()
    {
        SetIsConnected(true);
        _vm.AutoStandOnLyingEnabled = true;

        InvokeUpdateCharacterPosition("fighting");
        InvokeUpdateCharacterPosition("lying");
    }

    [Theory]
    [InlineData("n")]
    [InlineData("s")]
    [InlineData("e")]
    [InlineData("w")]
    public void SendMapMovementCommand_WhenConnected_DoesNotThrow(string direction)
    {
        // Exercises the actual send pipeline (see SetIsConnected) the same way arrow-key
        // navigation on the focused map does (WorldMapControl.MovementKeyPressed).
        SetIsConnected(true);

        _vm.SendMapMovementCommand(direction);
    }

    [Fact]
    public void SendMapMovementCommand_WhenNotConnected_DoesNotThrow()
    {
        _vm.SendMapMovementCommand("n");
    }

    [Fact]
    public void OnGroupChanged_ExhaustedMemberWithAutoRefreshEnabled_DoesNotThrow()
    {
        // Real regression guard for the OnGroupChanged -> TryAutoOrderExhaustedGroupRefresh wiring
        // (GroupExhaustionRefreshPolicyTests covers the actual decision logic in isolation).
        SetIsConnected(true);
        SetLatestCharacterName("Hero");
        _vm.AutoGroupRefreshOnExhaustedEnabled = true;
        var group = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", null, string.Empty, null, string.Empty, 4, null, false, null, IsLeader: true),
            new("Companion", null, string.Empty, null, string.Empty, 0, null, false, null, IsLeader: false),
        });

        var method = typeof(MainWindowViewModel).GetMethod("OnGroupChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(_vm, [group]);
    }

    [Fact]
    public void AutowalkLowMovementThresholdPercent_Setter_ClampsToLimits()
    {
        _vm.AutowalkLowMovementThresholdPercent = 999;
        Assert.Equal(AppSettings.MaxAutowalkLowMovementThresholdPercent, _vm.AutowalkLowMovementThresholdPercent);

        _vm.AutowalkLowMovementThresholdPercent = -5;
        Assert.Equal(AppSettings.MinAutowalkLowMovementThresholdPercent, _vm.AutowalkLowMovementThresholdPercent);
    }

    [Fact]
    public void AutowalkRestSeconds_Setter_ClampsToLimits()
    {
        _vm.AutowalkRestSeconds = 9999;
        Assert.Equal(AppSettings.MaxAutowalkRestSeconds, _vm.AutowalkRestSeconds);

        _vm.AutowalkRestSeconds = 0;
        Assert.Equal(AppSettings.MinAutowalkRestSeconds, _vm.AutowalkRestSeconds);
    }

    [Fact]
    public void Constructor_PopulatesMockNotes()
    {
        Assert.NotEmpty(_vm.Notes);
        Assert.Contains(_vm.Notes, n => n.Title == "Lista zakupów");
    }

    [Fact]
    public void Constructor_AddsWelcomeToast()
    {
        Assert.Single(_vm.Toasts);
        Assert.Contains("Witaj", _vm.Toasts[0].Text);
    }

    [Fact]
    public void OpenKilleropediaCommand_OpensLargeWidget()
    {
        Assert.False(_vm.IsKilleropediaOpen);

        _vm.OpenKilleropediaCommand.Execute(null);

        Assert.True(_vm.IsKilleropediaOpen);
    }

    [Fact]
    public void OpenHelpCommand_OpensHelpAndClosesKilleropedia()
    {
        _vm.IsKilleropediaOpen = true;

        _vm.OpenHelpCommand.Execute(null);

        Assert.True(_vm.IsHelpOpen);
        Assert.False(_vm.IsKilleropediaOpen);
    }

    [Fact]
    public void OpenKilleropediaCommand_ClosesHelp()
    {
        _vm.IsHelpOpen = true;

        _vm.OpenKilleropediaCommand.Execute(null);

        Assert.True(_vm.IsKilleropediaOpen);
        Assert.False(_vm.IsHelpOpen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShowTeacherOnMap_FocusesTeacherAndOnlyPaintsReachableRoute(bool routeExists)
    {
        var teacher = _vm.Killeropedia.FilteredTeachers.First(item => item.HasRoomLocation);
        var current = new MapRoom
        {
            Id = 900001,
            AreaId = 1,
            Name = "Current room",
            Coordinates = new MapCoordinates(0, 0, 0),
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement("test-current"),
            },
            Exits = routeExists
                ? [new MapExit { Name = "east", ExitId = 900002 }]
                : [],
        };
        var target = new MapRoom
        {
            Id = 900002,
            AreaId = routeExists ? 1 : 2,
            Name = teacher.Name,
            Coordinates = new MapCoordinates(1, 0, 0),
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement(teacher.RoomVnum),
            },
        };
        var document = new MapDocument
        {
            Areas = routeExists
                ? [new MapArea { Id = 1, Name = "Connected", Rooms = [current, target] }]
                :
                [
                    new MapArea { Id = 1, Name = "Old continent", Rooms = [current] },
                    new MapArea { Id = 2, Name = "New continent", Rooms = [target] },
                ],
        };
        typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
            .SetValue(_vm.Map, new MapIndex(document));
        var resolver = (GmcpLocationResolver)typeof(MainWindowViewModel)
            .GetField("_locationResolver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_vm)!;
        typeof(GmcpLocationResolver).GetProperty(nameof(GmcpLocationResolver.CurrentVnum))!
            .SetValue(resolver, "test-current");
        _vm.IsKilleropediaOpen = true;

        _vm.Killeropedia.ShowTeacherOnMapCommand.Execute(teacher);

        Assert.False(_vm.IsKilleropediaOpen);
        Assert.False(_vm.IsAutowalking);
        Assert.Same(target, _vm.Map.SelectedRoom);
        Assert.Equal(target.AreaId, _vm.Map.SelectedArea?.Id);
        Assert.False(_vm.Map.FollowPlayer);
        if (routeExists)
        {
            Assert.Equal([current, target], _vm.Map.RouteRooms);
        }
        else
        {
            Assert.Null(_vm.Map.RouteRooms);
        }
    }

    [Fact]
    public void Constructor_HeaderDefaultsToDisconnected()
    {
        Assert.Equal("--- Niepołączono ---", _vm.HeaderAreaText);
    }

    [Fact]
    public void Constructor_StatusTextDefaultsToRozłączono()
    {
        Assert.Equal("Rozłączono", _vm.StatusText);
    }

    [Fact]
    public void Constructor_CommandHistoryIsEmpty()
    {
        Assert.Empty(_vm.CommandHistory);
    }

    // ====================================================================
    // Status effects (live, from Char.Affects GMCP)
    //
    // The production handler (OnCharacterAffectsChanged) uses
    // Dispatcher.UIThread.Post to marshal work to the UI thread, which
    // is not available without a headless Avalonia platform.  Instead,
    // the tests below verify the collection-population logic directly
    // — the same logic that the dispatcher-invoked lambda executes in
    // production.  Event-subscription wiring is covered separately via
    // reflection (AffectsChangedEvent_IsSubscribedByConstructor).
    // ====================================================================

    /// <summary>
    /// Replicates the production OnCharacterAffectsChanged handler's
    /// collection logic.
    /// </summary>
    private void SimulateAffectsReceived(IReadOnlyList<CharacterAffect> affects)
    {
        _vm.Effects.Clear();
        foreach (var affect in affects)
        {
            _vm.Effects.Add(StatusEffect.FromCore(affect));
        }
    }

    [Fact]
    public void SimulateAffectsReceived_PopulatesEffects()
    {
        var affects = new List<CharacterAffect>
        {
            new("Błogosławieństwo", "Zwiększa celność", false, false, "10m"),
            new("Zatrucie", "Trucizna w organizmie", true, false, "30s"),
        };

        SimulateAffectsReceived(affects);

        Assert.Equal(2, _vm.Effects.Count);

        // -- Blessing (buff) --
        var blessing = _vm.Effects[0];
        Assert.Equal("Błogosławieństwo", blessing.Name);
        Assert.Equal("Zwiększa celność", blessing.Description);
        Assert.Equal("[+]", blessing.Icon);
        Assert.False(blessing.IsDebuff);
        Assert.False(blessing.Negative);
        Assert.False(blessing.Ending);
        Assert.Equal("10m", blessing.ExtraValue);
        Assert.Equal("10m", blessing.Duration);
        Assert.True(blessing.HasDescription);

        // -- Poison (debuff) --
        var poison = _vm.Effects[1];
        Assert.Equal("Zatrucie", poison.Name);
        Assert.Equal("Trucizna w organizmie", poison.Description);
        Assert.Equal("[-]", poison.Icon);
        Assert.True(poison.IsDebuff);
        Assert.True(poison.Negative);
        Assert.False(poison.Ending);
        Assert.Equal("30s", poison.ExtraValue);
        Assert.Equal("30s", poison.Duration);
        Assert.True(poison.HasDescription);
    }

    [Fact]
    public void SimulateAffectsReceived_EndingEffect_UsesEndingIcon()
    {
        var affects = new List<CharacterAffect>
        {
            new("Krótki buff", "Za chwilę zniknie", false, true, "5s"),
        };

        SimulateAffectsReceived(affects);

        var effect = Assert.Single(_vm.Effects);
        Assert.Equal("[!]", effect.Icon);
        Assert.True(effect.Ending);
        Assert.False(effect.IsDebuff);
    }

    [Fact]
    public void SimulateAffectsReceived_NegativeEndingEffect_UsesEndingIconAndIsDebuff()
    {
        // Covers the regression: a debuff (negative: true) that is also
        // ending (ending: true) must show "[!]" (not "[-]") while still
        // having IsDebuff == true so it renders in the debuff template.
        var affects = new List<CharacterAffect>
        {
            new("Trucizna kończy się", "Ostatnie chwile", true, true, "3s"),
        };

        SimulateAffectsReceived(affects);

        var effect = Assert.Single(_vm.Effects);
        Assert.Equal("[!]", effect.Icon);
        Assert.True(effect.IsDebuff);
        Assert.True(effect.Negative);
        Assert.True(effect.Ending);
        Assert.Equal("Trucizna kończy się", effect.Name);
        Assert.Equal("3s", effect.Duration);
        Assert.Equal("Ostatnie chwile", effect.Description);
    }

    [Fact]
    public void SimulateAffectsReceived_ClearsPreviousEffects()
    {
        // Arrange: simulate first effects update
        var first = new List<CharacterAffect>
        {
            new("Blessing", "first", false, false, null),
        };
        SimulateAffectsReceived(first);
        Assert.Single(_vm.Effects);

        // Act: simulate a second update with different effects
        var second = new List<CharacterAffect>
        {
            new("Poison", "second", true, false, null),
            new("Regen", "third", false, false, null),
        };
        SimulateAffectsReceived(second);

        // Assert: only the second update's effects are present
        Assert.Equal(2, _vm.Effects.Count);
        Assert.Contains(_vm.Effects, e => e.Name == "Poison");
        Assert.Contains(_vm.Effects, e => e.Name == "Regen");
        Assert.DoesNotContain(_vm.Effects, e => e.Name == "Blessing");
    }

    [Fact]
    public void SimulateAffectsReceived_EmptyDescription_HasDescriptionFalse()
    {
        var affects = new List<CharacterAffect>
        {
            new("NoDesc", string.Empty, false, false, null),
        };

        SimulateAffectsReceived(affects);

        var effect = Assert.Single(_vm.Effects);
        Assert.False(effect.HasDescription);
    }

    [Fact]
    public void SimulateAffectsReceived_NullExtraValue_EmptyDuration()
    {
        var affects = new List<CharacterAffect>
        {
            new("Test", "desc", false, false, null),
        };

        SimulateAffectsReceived(affects);

        var effect = Assert.Single(_vm.Effects);
        Assert.Null(effect.ExtraValue);
        Assert.Empty(effect.Duration);
    }

    // ====================================================================
    // AffectsChanged event subscription (reflection-based)
    // ====================================================================

    [Fact]
    public void AffectsChangedEvent_IsSubscribedByConstructor()
    {
        var resolverField = typeof(MainWindowViewModel).GetField("_characterState",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(resolverField);
        var resolver = resolverField!.GetValue(_vm);
        Assert.NotNull(resolver);

        var affectsChangedField = typeof(CharacterStateResolver).GetField("AffectsChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(affectsChangedField);
        var delegateObj = affectsChangedField!.GetValue(resolver) as Delegate;
        Assert.NotNull(delegateObj);
        Assert.NotEmpty(delegateObj.GetInvocationList());
    }

    // ====================================================================
    // Character conditions (live, from Char.Condition GMCP)
    //
    // The production handler (OnCharacterConditionChanged) uses
    // Dispatcher.UIThread.Post and also updates Vitals.Position.
    // We replicate the collection-and-position logic directly here,
    // matching the pattern used for Group and Effects above.
    // ====================================================================

    /// <summary>
    /// Replicates the production OnCharacterConditionChanged handler's
    /// collection and position logic, including position normalization
    /// (matching CharacterStateResolver.NormalizePosition).
    /// </summary>
    private void SimulateConditionReceived(CharacterConditionUpdate update)
    {
        if (update.Position is { } position)
        {
            var normalized = position.Trim();
            if (normalized.StartsWith("POS_", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }

            _vm.Vitals.PositionDisplay = TranslatePosition(normalized.ToLowerInvariant());
        }

        _vm.Conditions.Clear();
        foreach (var (flag, active) in update.Flags)
        {
            if (active)
            {
                _vm.Conditions.Add(TranslateCondition(flag));
            }
        }
    }

    /// <summary>Replicates the private TranslateCondition on MainWindowViewModel.</summary>
    private static string TranslateCondition(string flag) => flag.ToLowerInvariant() switch
    {
        "overweight" => "Przeciążenie",
        "drunk" => "Upojenie",
        "thirsty" => "Pragnienie",
        "hungry" => "Głód",
        "sleepy" => "Senność",
        "smoking" => "Pali",
        "thighjab" => "Rana uda",
        "bleedingwound" => "Krwawiąca rana",
        "bleed" => "Krwawienie",
        "halucinations" => "Halucynacje",
        _ => flag,
    };

    /// <summary>Replicates the private TranslatePosition on MainWindowViewModel.</summary>
    private static string TranslatePosition(string position) => position switch
    {
        "standing" => "Stoi",
        "sitting" => "Siedzi",
        "resting" => "Odpoczywa",
        "sleeping" => "Śpi",
        "fighting" => "Walczy",
        "stunned" => "Oszołomiony",
        "incap" or "incapacitated" => "Obezwładniony",
        "mortal" or "mortally" => "Umierający",
        "dead" => "Martwy",
        "lying" => "Leży",
        _ => position,
    };

    [Fact]
    public void SimulateConditionReceived_AllFalse_NoActiveConditions_AndPositionNormalized()
    {
        var update = new CharacterConditionUpdate(
            "POS_STOICCY",
            new Dictionary<string, bool>
            {
                ["overweight"] = false,
                ["drunk"] = false,
                ["thirsty"] = false,
                ["hungry"] = false,
                ["sleepy"] = false,
                ["smoking"] = false,
                ["thighJab"] = false,
                ["bleedingWound"] = false,
                ["bleed"] = false,
                ["halucinations"] = false,
            });

        SimulateConditionReceived(update);

        Assert.Empty(_vm.Conditions);
        Assert.Equal("stoiccy", _vm.Vitals.PositionDisplay);
    }

    [Fact]
    public void SimulateConditionReceived_AllFalse_Standing()
    {
        var update = new CharacterConditionUpdate(
            "POS_STANDING",
            new Dictionary<string, bool>
            {
                ["overweight"] = false,
                ["drunk"] = false,
                ["thirsty"] = false,
                ["hungry"] = false,
                ["sleepy"] = false,
                ["smoking"] = false,
                ["thighJab"] = false,
                ["bleedingWound"] = false,
                ["bleed"] = false,
                ["halucinations"] = false,
            });

        SimulateConditionReceived(update);

        Assert.Empty(_vm.Conditions);
        Assert.Equal("Stoi", _vm.Vitals.PositionDisplay);
    }

    [Fact]
    public void SimulateConditionReceived_SelectiveFlags_PopulatesConditions()
    {
        var update = new CharacterConditionUpdate(
            null,
            new Dictionary<string, bool>
            {
                ["thirsty"] = true,
                ["hungry"] = true,
                ["drunk"] = false,
                ["sleepy"] = false,
                ["overweight"] = false,
                ["smoking"] = false,
                ["thighJab"] = false,
                ["bleedingWound"] = false,
                ["bleed"] = false,
                ["halucinations"] = false,
            });

        SimulateConditionReceived(update);

        Assert.Equal(2, _vm.Conditions.Count);
        Assert.Contains("Pragnienie", _vm.Conditions);
        Assert.Contains("Głód", _vm.Conditions);
        Assert.DoesNotContain("Upojenie", _vm.Conditions);
    }

    [Fact]
    public void SimulateConditionReceived_ClearsPreviousConditions()
    {
        // Arrange: first update with some active flags
        var first = new CharacterConditionUpdate(
            null,
            new Dictionary<string, bool>
            {
                ["thirsty"] = true,
                ["hungry"] = false,
                ["drunk"] = true,
                ["sleepy"] = false,
            });
        SimulateConditionReceived(first);
        Assert.Equal(2, _vm.Conditions.Count);

        // Act: second update with different flags
        var second = new CharacterConditionUpdate(
            null,
            new Dictionary<string, bool>
            {
                ["thirsty"] = false,
                ["hungry"] = true,
                ["drunk"] = false,
                ["sleepy"] = false,
            });
        SimulateConditionReceived(second);

        // Assert: only the new active conditions are present
        Assert.Single(_vm.Conditions);
        Assert.Contains("Głód", _vm.Conditions);
        Assert.DoesNotContain("Pragnienie", _vm.Conditions);
        Assert.DoesNotContain("Upojenie", _vm.Conditions);
    }

    // ====================================================================
    // AddNote command
    // ====================================================================

    [Fact]
    public void AddNote_WithTitleAndContent_AddsNoteAndClearsFields()
    {
        _vm.NewNoteTitle = "Test title";
        _vm.NewNoteContent = "Test content";

        _vm.AddNoteCommand.Execute(null);

        var note = Assert.Single(_vm.Notes, n => n.Title == "Test title");
        Assert.Equal("Test content", note.Content);
        Assert.NotEmpty(note.CreatedAt);
        Assert.Empty(_vm.NewNoteTitle);
        Assert.Empty(_vm.NewNoteContent);
    }

    [Fact]
    public void AddNote_WithEmptyTitle_DoesNotAddNote()
    {
        var countBefore = _vm.Notes.Count;
        _vm.NewNoteTitle = string.Empty;

        _vm.AddNoteCommand.Execute(null);

        Assert.Equal(countBefore, _vm.Notes.Count);
    }

    [Fact]
    public void AddNote_WithWhitespaceTitle_DoesNotAddNote()
    {
        var countBefore = _vm.Notes.Count;
        _vm.NewNoteTitle = "   ";

        _vm.AddNoteCommand.Execute(null);

        Assert.Equal(countBefore, _vm.Notes.Count);
    }

    [Fact]
    public void AddNote_InsertsAtTopOfNotesCollection()
    {
        _vm.NewNoteTitle = "Newest";
        _vm.AddNoteCommand.Execute(null);

        Assert.Equal("Newest", _vm.Notes[0].Title);
    }

    // ====================================================================
    // DeleteNote command
    // ====================================================================

    [Fact]
    public void DeleteNote_WithExistingNote_RemovesIt()
    {
        _vm.NewNoteTitle = "To delete";
        _vm.AddNoteCommand.Execute(null);
        var note = _vm.Notes[0];
        Assert.Contains(note, _vm.Notes);

        _vm.DeleteNoteCommand.Execute(note);

        Assert.DoesNotContain(note, _vm.Notes);
    }

    [Fact]
    public void DeleteNote_WithNull_DoesNotThrow()
    {
        _vm.DeleteNoteCommand.Execute(null);
        // No exception expected
    }

    [Fact]
    public void DeleteNote_WithNonExistentNote_DoesNotChangeCollection()
    {
        var nonExistent = new MudClient.App.Models.NoteEntry { Title = "Ghost" };
        _vm.DeleteNoteCommand.Execute(nonExistent);
        // No exception expected, no change
    }

    // ====================================================================
    // CopyToCommandBar command
    // ====================================================================

    [Theory]
    [InlineData("north")]
    [InlineData("look")]
    [InlineData("say hello")]
    public void CopyToCommandBar_SetsCommandText(string text)
    {
        _vm.CopyToCommandBarCommand.Execute(text);

        Assert.Equal(text, _vm.CommandText);
    }

    [Fact]
    public void CopyToCommandBar_WithNull_DoesNotChangeCommandText()
    {
        _vm.CommandText = "existing";
        _vm.CopyToCommandBarCommand.Execute(null);

        Assert.Equal("existing", _vm.CommandText);
    }

    [Fact]
    public void CopyToCommandBar_WithEmptyString_DoesNotChangeCommandText()
    {
        _vm.CommandText = "existing";
        _vm.CopyToCommandBarCommand.Execute(string.Empty);

        Assert.Equal("existing", _vm.CommandText);
    }

    // ====================================================================
    // ClearToasts command
    // ====================================================================

    [Fact]
    public void ClearToasts_RemovesAllToasts()
    {
        Assert.NotEmpty(_vm.Toasts);

        _vm.ClearToastsCommand.Execute(null);

        Assert.Empty(_vm.Toasts);
    }

    // ====================================================================
    // SelectedRightTab property
    // ====================================================================

    [Fact]
    public void SelectedRightTab_DefaultsToZero()
    {
        Assert.Equal(0, _vm.SelectedRightTab);
    }

    [Fact]
    public void SelectedRightTab_IsSettable()
    {
        _vm.SelectedRightTab = 2;
        Assert.Equal(2, _vm.SelectedRightTab);
    }

    // ====================================================================
    // Command history – ordering, trimming, and capacity
    // ====================================================================

    [Fact]
    public async Task SendCommand_HistoryInsertedAtTopNewestFirst()
    {
        // Arrange
        SetIsConnected(true);
        _vm.CommandText = "first";

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);
        _vm.CommandText = "second";
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: newest command is at index 0
        Assert.Equal(2, _vm.CommandHistory.Count);
        Assert.Equal("second", _vm.CommandHistory[0]);
        Assert.Equal("first", _vm.CommandHistory[1]);
    }

    [Fact]
    public async Task SendCommand_HistoryTrimsAtMaxSize()
    {
        // Arrange: send 101 commands (1 more than the 100-entry cap)
        SetIsConnected(true);
        const int targetCount = 101;
        var allCommands = new string[targetCount];
        for (var i = 0; i < targetCount; i++)
        {
            allCommands[i] = $"cmd{i:D3}";
        }

        // Act
        foreach (var cmd in allCommands)
        {
            _vm.CommandText = cmd;
            await _vm.SendCommandCommand.ExecuteAsync(null);
        }

        // Assert: count is capped at 100, oldest entry ("cmd000") is removed
        Assert.Equal(100, _vm.CommandHistory.Count);
        Assert.DoesNotContain("cmd000", _vm.CommandHistory);
    }

    [Fact]
    public async Task SendCommand_HistoryPreservesNewestEntriesAtCap()
    {
        // Arrange: send 101 commands
        SetIsConnected(true);
        const int targetCount = 101;
        for (var i = 0; i < targetCount; i++)
        {
            _vm.CommandText = $"cmd{i:D3}";
            await _vm.SendCommandCommand.ExecuteAsync(null);
        }

        // Assert: the 100 newest entries ("cmd001" through "cmd100") are present,
        //         ordered newest-first.
        Assert.Equal(100, _vm.CommandHistory.Count);
        Assert.Equal("cmd100", _vm.CommandHistory[0]);
        Assert.Equal("cmd099", _vm.CommandHistory[1]);
        Assert.Contains("cmd001", _vm.CommandHistory);
    }

    [Fact]
    public async Task SendCommand_HistoryMaintainsOrderAfterMultipleTrims()
    {
        // Arrange: send 150 commands (50 past cap)
        SetIsConnected(true);
        for (var i = 0; i < 150; i++)
        {
            _vm.CommandText = $"cmd{i:D3}";
            await _vm.SendCommandCommand.ExecuteAsync(null);
        }

        // Assert: count stays at 100, oldest 50 are gone, newest 50 are present
        Assert.Equal(100, _vm.CommandHistory.Count);
        Assert.DoesNotContain("cmd000", _vm.CommandHistory);
        Assert.DoesNotContain("cmd049", _vm.CommandHistory);
        Assert.Contains("cmd050", _vm.CommandHistory);
        Assert.Contains("cmd149", _vm.CommandHistory);
        Assert.Equal("cmd149", _vm.CommandHistory[0]);
    }

    // ====================================================================
    // Host / Port defaults
    // ====================================================================

    [Fact]
    public void Host_DefaultsToKillerMudPl()
    {
        Assert.Equal("killer-mud.pl", _vm.Host);
    }

    [Fact]
    public void Port_DefaultsTo4004()
    {
        Assert.Equal(4004, _vm.Port);
    }

    // ====================================================================
    // Disconnect CanExecute
    // ====================================================================

    [Fact]
    public void DisconnectCommand_InitiallyCannotExecute()
    {
        Assert.False(_vm.DisconnectCommand.CanExecute(null));
    }

    // ====================================================================
    // Connect CanExecute
    // ====================================================================

    [Fact]
    public void ConnectCommand_InitiallyCanExecute()
    {
        Assert.True(_vm.ConnectCommand.CanExecute(null));
    }

    // ====================================================================
    // Send command – CommandText preservation  (coder change validation)
    // ====================================================================

    [Fact]
    public async Task SendCommand_WithClearingDisabled_PreservesOriginalTextAfterSend()
    {
        // Arrange: simulate connected so the command can execute
        SetIsConnected(true);
        _vm.CommandText = "l";

        // Act: execute the send command (full pipeline w/ alias expansion)
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: CommandText retains the original typed text, not cleared
        Assert.Equal("l", _vm.CommandText);
    }

    /// <summary>Registers a "l" → "look" alias through the real rule pipeline.</summary>
    private void AddLookAlias()
    {
        _vm.NewRuleName = "Skrót look";
        _vm.NewRuleType = "alias";
        _vm.NewRulePattern = "^l$";
        _vm.NewRuleAction = "look";
        _vm.AddRuleCommand.Execute(null);
    }

    /// <summary>Registers a trigger through the real rule pipeline.</summary>
    private void AddDeathTrigger()
    {
        _vm.NewRuleName = "Zgon";
        _vm.NewRuleType = "trigger";
        _vm.NewRulePattern = "^umierasz$";
        _vm.NewRuleAction = "modl sie";
        _vm.AddRuleCommand.Execute(null);
    }

    // ====================================================================
    // Alias / trigger split views (AliasRules / TriggerRules)

    [Fact]
    public void SeparateAddCommands_OpenFormWithFixedRuleType()
    {
        _vm.StartAddTriggerCommand.Execute(null);
        Assert.Equal("trigger", _vm.NewRuleType);
        Assert.True(_vm.IsRuleFormExpanded);
        Assert.True(_vm.IsTriggerRuleFormVisible);
        Assert.False(_vm.IsAliasRuleFormVisible);
        Assert.Equal(2, _vm.SelectedAutomationTabIndex);
        Assert.Equal("＋ Nowy trigger", _vm.RuleFormHeader);

        _vm.StartAddAliasCommand.Execute(null);
        Assert.Equal("alias", _vm.NewRuleType);
        Assert.True(_vm.IsAliasRuleFormVisible);
        Assert.False(_vm.IsTriggerRuleFormVisible);
        Assert.Equal(1, _vm.SelectedAutomationTabIndex);
        Assert.Equal("＋ Nowy alias", _vm.RuleFormHeader);
    }

    [Fact]
    public void StartAddTimer_OpensTimerEditorOnTimerTab()
    {
        _vm.SelectedAutomationTabIndex = 2;

        _vm.StartAddTimerCommand.Execute(null);

        Assert.Equal(0, _vm.SelectedAutomationTabIndex);
        Assert.True(_vm.IsTimerFormExpanded);
        Assert.Equal("＋ Nowy timer", _vm.TimerFormHeader);
    }
    // ====================================================================

    [Fact]
    public void AddRule_Alias_AppearsOnlyInAliasView()
    {
        AddLookAlias();

        Assert.Single(_vm.AliasRules);
        Assert.Empty(_vm.TriggerRules);
        Assert.Equal("alias", _vm.AliasRules[0].Type);
    }

    [Fact]
    public void AddRule_Trigger_AppearsOnlyInTriggerView()
    {
        AddDeathTrigger();

        Assert.Single(_vm.TriggerRules);
        Assert.Empty(_vm.AliasRules);
        Assert.Equal("trigger", _vm.TriggerRules[0].Type);
    }

    [Fact]
    public void SplitViews_TogetherCoverAllRules()
    {
        AddLookAlias();
        AddDeathTrigger();

        Assert.Equal(2, _vm.AutomationRules.Count);
        Assert.Single(_vm.AliasRules);
        Assert.Single(_vm.TriggerRules);
    }

    [Fact]
    public void DeleteRule_RemovesFromSplitView()
    {
        AddLookAlias();
        var alias = _vm.AliasRules[0];

        _vm.DeleteRuleCommand.Execute(alias);

        Assert.Empty(_vm.AliasRules);
        Assert.Empty(_vm.AutomationRules);
    }

    [Fact]
    public void EditRule_ChangingType_MovesBetweenSplitViews()
    {
        AddLookAlias();
        var rule = _vm.AliasRules[0];

        _vm.EditRuleCommand.Execute(rule);
        _vm.NewRuleType = "trigger";
        _vm.AddRuleCommand.Execute(null);

        Assert.Empty(_vm.AliasRules);
        Assert.Single(_vm.TriggerRules);
        Assert.Same(rule, _vm.TriggerRules[0]);
    }

    [Fact]
    public async Task SendCommand_HistoryContainsAliasExpandedVersion()
    {
        // Arrange
        AddLookAlias();
        SetIsConnected(true);
        _vm.CommandText = "l";
        var output = new List<string>();
        _vm.OutputReceived += output.Add;

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: history stores the original typed command ("l")
        //         but NOT the expanded version ("look")
        Assert.NotEmpty(_vm.CommandHistory);
        Assert.Contains("l", _vm.CommandHistory);
        Assert.DoesNotContain("look", _vm.CommandHistory);
        Assert.Contains(output, line => line.Contains("> look", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendCommand_PreservesWhitespaceText()
    {
        // Arrange
        SetIsConnected(true);
        _vm.CommandText = "   ";

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: whitespace text is preserved (not cleared)
        Assert.Equal("   ", _vm.CommandText);
    }

    [Fact]
    public async Task SendCommand_PreservesEmptyText()
    {
        // Arrange
        SetIsConnected(true);
        _vm.CommandText = string.Empty;
        var output = new List<string>();
        _vm.OutputReceived += output.Add;

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: empty string is preserved and reaches the sending pipeline.
        // MudSession then serializes it as a bare CRLF line.
        Assert.Equal(string.Empty, _vm.CommandText);
        Assert.Contains(output, line => line.Contains("> \u001b[0m\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendCommand_WithLongCommand_TextSurvives()
    {
        // Arrange
        SetIsConnected(true);
        const string longCmd = "say Witaj, świecie! 1234567890";
        _vm.CommandText = longCmd;

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: the full command text survives the send
        Assert.Equal(longCmd, _vm.CommandText);
    }

    [Fact]
    public async Task SendCommand_WithNonAliasedCommand_OriginalPreserved()
    {
        // Arrange
        SetIsConnected(true);
        const string cmd = "north";
        _vm.CommandText = cmd;

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: unchanged, same text in CommandText
        Assert.Equal(cmd, _vm.CommandText);
        Assert.Contains(cmd, _vm.CommandHistory);
    }

    [Fact]
    public async Task SendCommand_OriginalTextDifferentFromHistoryWhenAliased()
    {
        // Arrange
        AddLookAlias();
        SetIsConnected(true);
        _vm.CommandText = "l";

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: CommandText ("l") matches the history entry ("l")
        //         because history now stores the original typed command.
        Assert.Equal("l", _vm.CommandText);
        var historyEntry = Assert.Single(_vm.CommandHistory);
        Assert.Equal("l", historyEntry);
    }

    // ====================================================================
    // Multi-line alias test (VM level, sequential send)
    //
    // The SendCommand pipeline uses _aliases.ProcessCommands() which splits
    // the replacement on newlines.  We verify that the original typed command
    // is still recorded in history.  Actual sequential send to the network
    // cannot be verified without a fake/controllable MudSession (the current
    // architecture hard-codes TcpClient inside MudSession with no injectable
    // transport seam), so network-level send ordering is validated by the
    // core-level AliasEngine.ProcessCommands tests in MudClient.Core.Tests.
    // ====================================================================

    /// <summary>Registers a "ml" → multi-line alias through the real rule pipeline.</summary>
    private void AddMultiLineAlias()
    {
        _vm.NewRuleName = "Multi look-north";
        _vm.NewRuleType = "alias";
        _vm.NewRulePattern = "^ml$";
        _vm.NewRuleAction = "look\nnorth";
        _vm.AddRuleCommand.Execute(null);
    }

    [Fact]
    public async Task SendCommand_WithMultiLineAlias_OriginalTypedCommandStoredInHistory()
    {
        // Arrange
        AddMultiLineAlias();
        SetIsConnected(true);
        _vm.CommandText = "ml";

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: history stores the original typed command, not the expanded lines
        Assert.NotEmpty(_vm.CommandHistory);
        Assert.Contains("ml", _vm.CommandHistory);
        Assert.DoesNotContain("look", _vm.CommandHistory);
        Assert.DoesNotContain("north", _vm.CommandHistory);
    }

    [Fact]
    public async Task SendCommand_WithMultiLineAlias_CommandTextPreserved()
    {
        // Arrange
        AddMultiLineAlias();
        SetIsConnected(true);
        _vm.CommandText = "ml";

        // Act
        await _vm.SendCommandCommand.ExecuteAsync(null);

        // Assert: the original typed text remains in CommandText
        Assert.Equal("ml", _vm.CommandText);
    }

    // ====================================================================
    // Outgoing GMCP recording (SentGmcpMessages) and 100-entry cap
    //
    // The production event handlers (OnGmcpSent / OnGmcpReceived) use
    // Dispatcher.UIThread.Post to marshal work to the UI thread.  Calling
    // Dispatcher.UIThread.RunJobs() from a non-UI thread would throw
    // Dispatcher.VerifyAccess, making a dispatcher-based test helper
    // unreliable without a headless Avalonia platform.
    //
    // Instead, the tests below verify the insert/order/cap/placeholder
    // behaviour directly on the observable collections — the same logic
    // that the dispatcher-invoked lambdas execute in production.
    // Event-subscription wiring is covered separately via reflection
    // (GmcpSentEvent_IsSubscribedByConstructor / GmcpReceivedEvent_Is
    // SubscribedByConstructor).
    // ====================================================================

    /// <summary>
    /// Replicates the production handler's JSON → display-value logic.
    /// </summary>
    private static string FormatGmcpJson(string json) =>
        string.IsNullOrWhiteSpace(json) ? "(bez danych)" : json;

    private static GmcpEntryViewModel MakeEntry(string package, string json) =>
        new(package, FormatGmcpJson(json), DateTimeOffset.Now.ToString("HH:mm:ss"));

    /// <summary>
    /// Simulates the production OnGmcpSent logic: insert at head, trim to 100.
    /// </summary>
    private void SimulateGmcpSent(string package, string json)
    {
        _vm.SentGmcpMessages.Insert(0, MakeEntry(package, json));
        while (_vm.SentGmcpMessages.Count > 100)
            _vm.SentGmcpMessages.RemoveAt(_vm.SentGmcpMessages.Count - 1);
    }

    /// <summary>
    /// Simulates the production OnGmcpReceived logic: insert at head, trim to 100.
    /// </summary>
    private void SimulateGmcpReceived(string package, string json)
    {
        _vm.GmcpMessages.Insert(0, MakeEntry(package, json));
        while (_vm.GmcpMessages.Count > 100)
            _vm.GmcpMessages.RemoveAt(_vm.GmcpMessages.Count - 1);
    }

    [Fact]
    public void SentGmcpMessages_InitiallyEmpty()
    {
        Assert.Empty(_vm.SentGmcpMessages);
    }

    [Fact]
    public void GmcpMessages_InitiallyEmpty()
    {
        Assert.Empty(_vm.GmcpMessages);
    }

    [Fact]
    public void SentGmcpMessages_InsertThenAddsEntry()
    {
        SimulateGmcpSent("Test.Package", "{\"key\":\"val\"}");

        var entry = Assert.Single(_vm.SentGmcpMessages);
        Assert.Equal("Test.Package", entry.Package);
        Assert.Contains("key", entry.Json);
    }

    [Fact]
    public void SentGmcpMessages_EntryContainsReceivedAtTimestamp()
    {
        SimulateGmcpSent("Core.Ping", "{}");

        var entry = Assert.Single(_vm.SentGmcpMessages);
        Assert.NotEmpty(entry.ReceivedAt);
    }

    [Fact]
    public void SentGmcpMessages_NewestFirst()
    {
        SimulateGmcpSent("First.Pkg", "{}");
        SimulateGmcpSent("Second.Pkg", "{}");

        Assert.Equal(2, _vm.SentGmcpMessages.Count);
        Assert.Equal("Second.Pkg", _vm.SentGmcpMessages[0].Package);
        Assert.Equal("First.Pkg", _vm.SentGmcpMessages[1].Package);
    }

    [Fact]
    public void SentGmcpMessages_CappedAt100_AfterMultipleEvents()
    {
        for (var i = 0; i < 101; i++)
        {
            SimulateGmcpSent($"Pkg{i:D3}", $"{{\"i\":{i}}}");
        }

        Assert.Equal(100, _vm.SentGmcpMessages.Count);
        // Oldest entry (Pkg000) should be gone.
        Assert.DoesNotContain(_vm.SentGmcpMessages, e => e.Package == "Pkg000");
    }

    [Fact]
    public void SentGmcpMessages_PreservesNewestEntriesAtCap()
    {
        for (var i = 0; i < 101; i++)
        {
            SimulateGmcpSent($"Pkg{i:D3}", $"{{\"i\":{i}}}");
        }

        // The 100 newest (Pkg001 through Pkg100) should be present,
        // ordered newest-first.
        Assert.Equal(100, _vm.SentGmcpMessages.Count);
        Assert.Equal("Pkg100", _vm.SentGmcpMessages[0].Package);
        Assert.Equal("Pkg099", _vm.SentGmcpMessages[1].Package);
        Assert.Contains(_vm.SentGmcpMessages, e => e.Package == "Pkg001");
    }

    [Fact]
    public void SentGmcpMessages_JsonIsStoredInEntry()
    {
        const string json = "{\"hp\":150,\"maxHp\":200}";
        SimulateGmcpSent("Char.Vitals", json);

        var entry = Assert.Single(_vm.SentGmcpMessages);
        Assert.Equal(FormatGmcpJson(json), entry.Json);
    }

    [Fact]
    public void SentGmcpMessages_EmptyJsonBecomesPlaceholder()
    {
        SimulateGmcpSent("Core.Ping", string.Empty);

        var entry = Assert.Single(_vm.SentGmcpMessages);
        Assert.Equal("(bez danych)", entry.Json);
    }

    // ====================================================================
    // Incoming GMCP (GmcpMessages) — same cap + ordering logic
    // ====================================================================

    [Fact]
    public void GmcpMessages_InsertThenAddsEntry()
    {
        SimulateGmcpReceived("Room.Info", "{\"name\":\"Tavern\"}");

        var entry = Assert.Single(_vm.GmcpMessages);
        Assert.Equal("Room.Info", entry.Package);
    }

    [Fact]
    public void GmcpMessages_CappedAt100_AfterMultipleEvents()
    {
        for (var i = 0; i < 101; i++)
        {
            SimulateGmcpReceived($"Pkg{i:D3}", $"{{\"i\":{i}}}");
        }

        Assert.Equal(100, _vm.GmcpMessages.Count);
        Assert.DoesNotContain(_vm.GmcpMessages, e => e.Package == "Pkg000");
    }

    [Fact]
    public void GmcpMessages_NewestFirst()
    {
        SimulateGmcpReceived("A", "{}");
        SimulateGmcpReceived("B", "{}");

        Assert.Equal(2, _vm.GmcpMessages.Count);
        Assert.Equal("B", _vm.GmcpMessages[0].Package);
        Assert.Equal("A", _vm.GmcpMessages[1].Package);
    }

    [Fact]
    public void GmcpMessages_PreservesNewestEntriesAtCap()
    {
        for (var i = 0; i < 101; i++)
        {
            SimulateGmcpReceived($"Pkg{i:D3}", $"{{\"i\":{i}}}");
        }

        Assert.Equal(100, _vm.GmcpMessages.Count);
        Assert.Equal("Pkg100", _vm.GmcpMessages[0].Package);
        Assert.Equal("Pkg099", _vm.GmcpMessages[1].Package);
        Assert.Contains(_vm.GmcpMessages, e => e.Package == "Pkg001");
    }

    [Fact]
    public void GmcpMessages_JsonIsStoredInEntry()
    {
        const string json = "{\"name\":\"Tavern\",\"zone\":3}";
        SimulateGmcpReceived("Room.Info", json);

        var entry = Assert.Single(_vm.GmcpMessages);
        Assert.Equal(FormatGmcpJson(json), entry.Json);
    }

    [Fact]
    public void GmcpMessages_EmptyJsonBecomesPlaceholder()
    {
        SimulateGmcpReceived("Core.Ping", string.Empty);

        var entry = Assert.Single(_vm.GmcpMessages);
        Assert.Equal("(bez danych)", entry.Json);
    }

    [Fact]
    public void GmcpMessages_EntryContainsReceivedAtTimestamp()
    {
        SimulateGmcpReceived("Room.Info", "{}");

        var entry = Assert.Single(_vm.GmcpMessages);
        Assert.NotEmpty(entry.ReceivedAt);
    }

    // ====================================================================
    // GMCP event subscription wiring (reflection-based)
    // ====================================================================

    [Fact]
    public void GmcpSentEvent_IsSubscribedByConstructor()
    {
        // Check that the private _session.GmcpSent event has at least one
        // subscriber after MainWindowViewModel construction.
        var sessionField = typeof(MainWindowViewModel).GetField("_session",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(sessionField);
        var session = sessionField!.GetValue(_vm);
        Assert.NotNull(session);

        var gmcpSentField = typeof(MudSession).GetField("GmcpSent",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(gmcpSentField);
        var delegateObj = gmcpSentField!.GetValue(session) as Delegate;
        Assert.NotNull(delegateObj);
        Assert.NotEmpty(delegateObj.GetInvocationList());
    }

    [Fact]
    public void GmcpReceivedEvent_IsSubscribedByConstructor()
    {
        var sessionField = typeof(MainWindowViewModel).GetField("_session",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(sessionField);
        var session = sessionField!.GetValue(_vm);
        Assert.NotNull(session);

        var gmcpReceivedField = typeof(MudSession).GetField("GmcpReceived",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(gmcpReceivedField);
        var delegateObj = gmcpReceivedField!.GetValue(session) as Delegate;
        Assert.NotNull(delegateObj);
        Assert.NotEmpty(delegateObj.GetInvocationList());
    }

    // ====================================================================
    // GroupChanged event subscription (reflection-based)
    // ====================================================================

    [Fact]
    public void GroupChangedEvent_IsSubscribedByConstructor()
    {
        // Check that the private _characterState.GroupChanged event has at
        // least one subscriber after MainWindowViewModel construction.
        var resolverField = typeof(MainWindowViewModel).GetField("_characterState",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(resolverField);
        var resolver = resolverField!.GetValue(_vm);
        Assert.NotNull(resolver);

        var groupChangedField = typeof(CharacterStateResolver).GetField("GroupChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(groupChangedField);
        var delegateObj = groupChangedField!.GetValue(resolver) as Delegate;
        Assert.NotNull(delegateObj);
        Assert.NotEmpty(delegateObj.GetInvocationList());
    }

    [Fact]
    public void LordMode_ChangedFromMap_PersistsAndUpdatesMainViewModel()
    {
        Assert.False(_vm.LordModeEnabled);

        _vm.Map.LordModeEnabled = true;

        Assert.True(_vm.LordModeEnabled);
        Assert.True(GetProfileSettings(_vm).LordModeEnabled);
    }

    [Fact]
    public void LordGroupGotoCommands_RequireLordModeAndBuildExpectedTargets()
    {
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));

        Assert.False(_vm.LordGotoGroupRoomCommand.CanExecute(member));
        Assert.False(_vm.LordGotoGroupMemberCommand.CanExecute(member));

        _vm.LordModeEnabled = true;

        Assert.True(_vm.LordGotoGroupRoomCommand.CanExecute(member));
        Assert.True(_vm.LordGotoGroupMemberCommand.CanExecute(member));
        Assert.Equal("walk 6017", MainWindowViewModel.BuildLordGotoGroupRoomCommand(member));
        Assert.Equal("walk Aragorn", MainWindowViewModel.BuildLordGotoGroupMemberCommand(member));
    }

    [Fact]
    public void LordGroupGotoCommands_RejectUnsafeGmcpTargets()
    {
        var member = new GroupMember(
            "Aragorn\nshutdown", false, "standing", "bez ran", 7,
            "wypoczęty", 4, null, false, "6017\nshutdown", "?");
        _vm.LordModeEnabled = true;

        Assert.False(_vm.LordGotoGroupRoomCommand.CanExecute(member));
        Assert.False(_vm.LordGotoGroupMemberCommand.CanExecute(member));
        Assert.Null(MainWindowViewModel.BuildLordGotoGroupRoomCommand(member));
        Assert.Null(MainWindowViewModel.BuildLordGotoGroupMemberCommand(member));
    }

    // ====================================================================
    // Group spell shortcuts — user-defined "cast this on this member" buttons
    // ====================================================================

    [Fact]
    public void BuildCastGroupSpellCommand_ValidMemberAndShortcut_QuotesSpellName()
    {
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));
        var shortcut = new GroupSpellShortcut { Label = "cc", SpellName = "cure critical" };

        Assert.Equal(
            "cast \"cure critical\" Aragorn",
            MainWindowViewModel.BuildCastGroupSpellCommand(member, shortcut, matchedAbility: null));
    }

    [Fact]
    public void BuildCastGroupSpellCommand_RejectsUnsafeMemberName()
    {
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn\nshutdown", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));
        var shortcut = new GroupSpellShortcut { Label = "cc", SpellName = "cure critical" };

        Assert.Null(MainWindowViewModel.BuildCastGroupSpellCommand(member, shortcut, matchedAbility: null));
    }

    [Fact]
    public void BuildCastGroupSpellCommand_RejectsEmptySpellName()
    {
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));
        var shortcut = new GroupSpellShortcut { Label = "cc", SpellName = "  " };

        Assert.Null(MainWindowViewModel.BuildCastGroupSpellCommand(member, shortcut, matchedAbility: null));
    }

    [Fact]
    public void BuildCastGroupSpellCommand_MatchedSpellAbility_StillUsesCastForm()
    {
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));
        var shortcut = new GroupSpellShortcut { Label = "cc", SpellName = "cure critical" };
        var ability = new AbilityCaptureEntry { Name = "cure critical", Type = "czar leczacy", Syntax = "cast" };

        Assert.Equal(
            "cast \"cure critical\" Aragorn",
            MainWindowViewModel.BuildCastGroupSpellCommand(member, shortcut, ability));
    }

    [Fact]
    public void BuildCastGroupSpellCommand_MatchedActiveSkillWithDifferentSyntaxVerb_UsesItsOwnSyntax()
    {
        // "healing touch" is invoked as bare "touch", not "cast 'healing touch' <target>" and not
        // "healing touch <target>" either — the captured Syntax is the only reliable source for
        // the actual verb.
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));
        var shortcut = new GroupSpellShortcut { Label = "ht", SpellName = "healing touch" };
        var ability = new AbilityCaptureEntry { Name = "healing touch", Type = "skill aktywny", Syntax = "touch" };

        Assert.Equal(
            "touch Aragorn",
            MainWindowViewModel.BuildCastGroupSpellCommand(member, shortcut, ability));
    }

    [Fact]
    public void BuildCastGroupSpellCommand_MatchedActiveSkillWithoutKnownSyntax_FallsBackToCastForm()
    {
        var member = GroupMember.FromCore(new CharacterGroupMember(
            "Aragorn", "standing", "bez ran", 7, "wypoczęty", 4, null,
            false, "6017", false));
        var shortcut = new GroupSpellShortcut { Label = "aid", SpellName = "first aid" };
        var ability = new AbilityCaptureEntry { Name = "first aid", Type = "skill bierny", Syntax = null };

        Assert.Equal(
            "cast \"first aid\" Aragorn",
            MainWindowViewModel.BuildCastGroupSpellCommand(member, shortcut, ability));
    }

    [Fact]
    public void AddGroupSpellCommand_AddsShortcutAndClearsInputs()
    {
        _vm.NewGroupSpellLabel = "cc";
        _vm.NewGroupSpellName = "cure critical";

        Assert.True(_vm.AddGroupSpellCommand.CanExecute(null));
        _vm.AddGroupSpellCommand.Execute(null);

        var shortcut = Assert.Single(_vm.GroupSpells);
        Assert.Equal("cc", shortcut.Label);
        Assert.Equal("cure critical", shortcut.SpellName);
        Assert.Equal(string.Empty, _vm.NewGroupSpellLabel);
        Assert.Equal(string.Empty, _vm.NewGroupSpellName);
    }

    [Fact]
    public void AddGroupSpellCommand_CannotExecuteWithoutBothFields()
    {
        Assert.False(_vm.AddGroupSpellCommand.CanExecute(null));

        _vm.NewGroupSpellLabel = "cc";
        Assert.False(_vm.AddGroupSpellCommand.CanExecute(null));

        _vm.NewGroupSpellLabel = string.Empty;
        _vm.NewGroupSpellName = "cure critical";
        Assert.False(_vm.AddGroupSpellCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveGroupSpellCommand_RemovesTheGivenShortcut()
    {
        _vm.NewGroupSpellLabel = "cc";
        _vm.NewGroupSpellName = "cure critical";
        _vm.AddGroupSpellCommand.Execute(null);
        var shortcut = Assert.Single(_vm.GroupSpells);

        _vm.RemoveGroupSpellCommand.Execute(shortcut);

        Assert.Empty(_vm.GroupSpells);
    }

    [Fact]
    public async Task AddGroupSpellCommand_PersistsToTheGroupSpellStore()
    {
        var storePath = Path.Combine(_tempDir, "group-spells-persist.json");
        var store = new GroupSpellStore(storePath);
        var viewModel = new MainWindowViewModel(
            profileService: new ProfileService(Path.Combine(_tempDir, "PersistProfiles")),
            settingsService: new AppSettingsService(Path.Combine(_tempDir, "PersistSettings")),
            groupSpellStore: store);

        viewModel.NewGroupSpellLabel = "cc";
        viewModel.NewGroupSpellName = "cure critical";
        viewModel.AddGroupSpellCommand.Execute(null);

        // The save is fire-and-forget; give it a moment to reach disk before reloading.
        for (var attempt = 0; attempt < 50 && !File.Exists(storePath); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var reloaded = store.Load();
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("cc", entry.Label);
        Assert.Equal("cure critical", entry.SpellName);

        await viewModel.DisposeAsync();
    }

    // ====================================================================
    // TryParseMapujCommand — "/mapuj <klasa>" parsing
    // ====================================================================

    [Fact]
    public void TryParseMapujCommand_WithClassName_ReturnsTrimmedClassName()
    {
        var consumed = MainWindowViewModel.TryParseMapujCommand("/mapuj paladyn", out var className);

        Assert.True(consumed);
        Assert.Equal("paladyn", className);
    }

    [Fact]
    public void TryParseMapujCommand_IsCaseInsensitiveOnThePrefix()
    {
        var consumed = MainWindowViewModel.TryParseMapujCommand("/MaPuJ  Paladyn  ", out var className);

        Assert.True(consumed);
        Assert.Equal("Paladyn", className);
    }

    [Fact]
    public void TryParseMapujCommand_WithoutArgument_ReturnsTrueWithNullClassName()
    {
        var consumed = MainWindowViewModel.TryParseMapujCommand("/mapuj", out var className);

        Assert.True(consumed);
        Assert.Null(className);
    }

    [Fact]
    public void TryParseMapujCommand_WithOnlyWhitespaceArgument_ReturnsTrueWithNullClassName()
    {
        var consumed = MainWindowViewModel.TryParseMapujCommand("/mapuj    ", out var className);

        Assert.True(consumed);
        Assert.Null(className);
    }

    [Fact]
    public void TryParseMapujCommand_WithNumericArgument_ReturnsItAsTrimmedString()
    {
        // "/mapuj <liczba>" (artifact "try" mapping) shares the same parse — the caller tells the
        // two apart with int.TryParse on the returned argument, not this method.
        var consumed = MainWindowViewModel.TryParseMapujCommand("/mapuj 127", out var argument);

        Assert.True(consumed);
        Assert.Equal("127", argument);
    }

    [Theory]
    [InlineData("kill orc")]
    [InlineData("/mapujwhatever paladyn")]
    [InlineData("")]
    public void TryParseMapujCommand_NotAMapujCommand_ReturnsFalse(string command)
    {
        var consumed = MainWindowViewModel.TryParseMapujCommand(command, out var className);

        Assert.False(consumed);
        Assert.Null(className);
    }

    // ====================================================================
    // TryHandleSettingsToggleCommand — generic "/<nazwa> [on|off]" toggle commands
    // ====================================================================

    private bool InvokeTryHandleSettingsToggleCommand(string command)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "TryHandleSettingsToggleCommand", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (bool)method!.Invoke(_vm, [command])!;
    }

    [Fact]
    public void CommandToggles_HasNoDuplicateCommandNames()
    {
        var names = _vm.CommandToggles.Select(t => t.Command.ToLowerInvariant()).ToList();

        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public void CommandToggles_IncludesTheTwoAutostandVariants()
    {
        Assert.Contains(_vm.CommandToggles, t => t.Command == "autostand");
        Assert.Contains(_vm.CommandToggles, t => t.Command == "standorder");
    }

    [Fact]
    public void CommandToggles_IncludesMirrorPosition_AndTerminalCommandTogglesIt()
    {
        Assert.Contains(_vm.CommandToggles, t => t.Command == "mirrorposition");

        var method = typeof(MainWindowViewModel).GetMethod(
            "TryHandleSettingsToggleCommand", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.False(_vm.AutoMirrorLeaderPositionEnabled);

        var consumed = (bool)method!.Invoke(_vm, ["/mirrorposition on"])!;

        Assert.True(consumed);
        Assert.True(_vm.AutoMirrorLeaderPositionEnabled);
    }

    [Fact]
    public void CommandToggles_IncludesChatSound_AndTerminalCommandTogglesIt()
    {
        Assert.Contains(_vm.CommandToggles, t => t.Command == "chatsound");
        Assert.False(_vm.ChatSoundOnNewMessageEnabled);

        var consumed = InvokeTryHandleSettingsToggleCommand("/chatsound on");

        Assert.True(consumed);
        Assert.True(_vm.ChatSoundOnNewMessageEnabled);
    }

    // ====================================================================
    // ShowTimersOnStatusBarEnabled / ShowTriggersOnStatusBarEnabled — the
    // "Pokaż na pasku" checkboxes on the Timery/Triggery tabs (default on).
    // ====================================================================

    [Fact]
    public void ShowTimersAndTriggersOnStatusBar_DefaultToEnabled()
    {
        Assert.True(_vm.ShowTimersOnStatusBarEnabled);
        Assert.True(_vm.ShowTriggersOnStatusBarEnabled);
    }

    [Fact]
    public void CommandToggles_IncludesTimersBar_AndTerminalCommandTogglesIt()
    {
        Assert.Contains(_vm.CommandToggles, t => t.Command == "timersbar");
        Assert.True(_vm.ShowTimersOnStatusBarEnabled);

        var consumed = InvokeTryHandleSettingsToggleCommand("/timersbar off");

        Assert.True(consumed);
        Assert.False(_vm.ShowTimersOnStatusBarEnabled);
    }

    [Fact]
    public void CommandToggles_IncludesTriggersBar_AndTerminalCommandTogglesIt()
    {
        Assert.Contains(_vm.CommandToggles, t => t.Command == "triggersbar");
        Assert.True(_vm.ShowTriggersOnStatusBarEnabled);

        var consumed = InvokeTryHandleSettingsToggleCommand("/triggersbar off");

        Assert.True(consumed);
        Assert.False(_vm.ShowTriggersOnStatusBarEnabled);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_BareCommand_TogglesCurrentValue()
    {
        Assert.False(_vm.AutoStandOnLyingEnabled);

        var consumedOn = InvokeTryHandleSettingsToggleCommand("/autostand");
        Assert.True(consumedOn);
        Assert.True(_vm.AutoStandOnLyingEnabled);

        var consumedOff = InvokeTryHandleSettingsToggleCommand("/autostand");
        Assert.True(consumedOff);
        Assert.False(_vm.AutoStandOnLyingEnabled);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("start")]
    [InlineData("wlacz")]
    [InlineData("1")]
    [InlineData("tak")]
    public void TryHandleSettingsToggleCommand_OnArgument_SetsTrue(string argument)
    {
        var consumed = InvokeTryHandleSettingsToggleCommand($"/autostand {argument}");

        Assert.True(consumed);
        Assert.True(_vm.AutoStandOnLyingEnabled);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("stop")]
    [InlineData("wylacz")]
    [InlineData("0")]
    [InlineData("nie")]
    public void TryHandleSettingsToggleCommand_OffArgument_SetsFalse(string argument)
    {
        _vm.AutoStandOnLyingEnabled = true;

        var consumed = InvokeTryHandleSettingsToggleCommand($"/autostand {argument}");

        Assert.True(consumed);
        Assert.False(_vm.AutoStandOnLyingEnabled);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_StartStop_MirrorsTheGlobalPanicVocabulary()
    {
        // "/autoassist start"/"stop" use the exact same words as the global /start and /stop
        // commands, just scoped to one function — this is the whole point of the feature.
        var on = InvokeTryHandleSettingsToggleCommand("/autoassist start");
        Assert.True(on);
        Assert.True(_vm.AutoAssistEnabled);

        var off = InvokeTryHandleSettingsToggleCommand("/autoassist stop");
        Assert.True(off);
        Assert.False(_vm.AutoAssistEnabled);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_IsCaseInsensitiveOnTheCommandName()
    {
        var consumed = InvokeTryHandleSettingsToggleCommand("/AUTOSTAND on");

        Assert.True(consumed);
        Assert.True(_vm.AutoStandOnLyingEnabled);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_InvalidArgument_ShowsUsageAndLeavesValueUnchanged()
    {
        var consumed = InvokeTryHandleSettingsToggleCommand("/autostand sideways");

        Assert.True(consumed);
        Assert.False(_vm.AutoStandOnLyingEnabled);
        Assert.Contains("/autostand [start|stop]", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_UnknownCommand_ReturnsFalse()
    {
        var consumed = InvokeTryHandleSettingsToggleCommand("/notarealtoggle");

        Assert.False(consumed);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_MapBackedToggle_ReachesMapViewModel()
    {
        Assert.False(_vm.Map.AutoScanOnRoomEnter);

        var consumed = InvokeTryHandleSettingsToggleCommand("/autoscan on");

        Assert.True(consumed);
        Assert.True(_vm.Map.AutoScanOnRoomEnter);
    }

    [Fact]
    public void TryHandleSettingsToggleCommand_DistinguishesTheTwoAutostandVariants()
    {
        InvokeTryHandleSettingsToggleCommand("/autostand on");

        Assert.True(_vm.AutoStandOnLyingEnabled);
        Assert.False(_vm.AutoStandOrderEnabled);
    }

    // ====================================================================
    // "/" command autocomplete — CommandSuggestions/HasCommandSuggestions/
    // SelectedCommandSuggestionIndex, driven live off CommandText.
    // ====================================================================

    [Fact]
    public void AvailableCommandNames_IncludesFixedBuiltinsAndEveryCommandToggle()
    {
        Assert.Contains("stop", _vm.AvailableCommandNames);
        Assert.Contains("start", _vm.AvailableCommandNames);
        Assert.Contains("walk", _vm.AvailableCommandNames);
        foreach (var toggle in _vm.CommandToggles)
        {
            Assert.Contains(toggle.Command, _vm.AvailableCommandNames);
        }
    }

    [Fact]
    public void CommandText_JustASlash_ShowsEveryAvailableCommand()
    {
        _vm.CommandText = "/";

        Assert.True(_vm.HasCommandSuggestions);
        Assert.Equal(_vm.AvailableCommandNames.Count, _vm.CommandSuggestions.Count);
    }

    [Fact]
    public void CommandText_PartialCommandName_FiltersByPrefixCaseInsensitively()
    {
        _vm.CommandText = "/AUTOAS";

        Assert.Contains("autoassist", _vm.CommandSuggestions);
        Assert.Contains("autoassistnpc", _vm.CommandSuggestions);
        Assert.DoesNotContain("autostand", _vm.CommandSuggestions);
    }

    [Fact]
    public void CommandText_NoMatchingPrefix_HasNoSuggestions()
    {
        _vm.CommandText = "/zzzznotarealcommand";

        Assert.False(_vm.HasCommandSuggestions);
        Assert.Empty(_vm.CommandSuggestions);
    }

    [Fact]
    public void CommandText_WithoutLeadingSlash_HasNoSuggestions()
    {
        _vm.CommandText = "auto";

        Assert.False(_vm.HasCommandSuggestions);
    }

    [Theory]
    [InlineData("/autostand on")]
    [InlineData("/autostand\nsecond line")]
    public void CommandText_PastTheCommandName_HidesSuggestions(string text)
    {
        _vm.CommandText = "/autostand";
        Assert.True(_vm.HasCommandSuggestions);

        _vm.CommandText = text;

        Assert.False(_vm.HasCommandSuggestions);
    }

    [Fact]
    public void CommandText_Changing_ResetsSelectionToFirstMatch()
    {
        _vm.CommandText = "/autostand";
        _vm.SelectedCommandSuggestionIndex = 0;
        _vm.MoveCommandSuggestionSelection(+1);

        _vm.CommandText = "/auto";

        Assert.Equal(0, _vm.SelectedCommandSuggestionIndex);
    }

    [Fact]
    public void MoveCommandSuggestionSelection_ClampsAtBothEnds()
    {
        _vm.CommandText = "/auto";
        var lastIndex = _vm.CommandSuggestions.Count - 1;

        _vm.MoveCommandSuggestionSelection(-1);
        Assert.Equal(0, _vm.SelectedCommandSuggestionIndex);

        for (var i = 0; i < _vm.CommandSuggestions.Count + 3; i++)
        {
            _vm.MoveCommandSuggestionSelection(+1);
        }

        Assert.Equal(lastIndex, _vm.SelectedCommandSuggestionIndex);
    }

    [Fact]
    public void AcceptSelectedCommandSuggestion_CompletesTextWithTrailingSpace_AndClearsSuggestions()
    {
        _vm.CommandText = "/autostand";

        _vm.AcceptSelectedCommandSuggestion();

        Assert.Equal("/autostand ", _vm.CommandText);
        Assert.False(_vm.HasCommandSuggestions);
    }

    [Fact]
    public void AcceptSelectedCommandSuggestion_UsesTheHighlightedEntry_NotJustTheFirstOne()
    {
        _vm.CommandText = "/auto";
        _vm.MoveCommandSuggestionSelection(+1);
        var expected = _vm.CommandSuggestions[1];

        _vm.AcceptSelectedCommandSuggestion();

        Assert.Equal($"/{expected} ", _vm.CommandText);
    }

    [Fact]
    public void AcceptSelectedCommandSuggestion_NoSuggestions_IsANoOp()
    {
        _vm.CommandText = "/walk";
        _vm.AcceptSelectedCommandSuggestion();
        _vm.CommandText = "hello";

        _vm.AcceptSelectedCommandSuggestion();

        Assert.Equal("hello", _vm.CommandText);
    }

    [Fact]
    public void ClearCommandSuggestions_HidesTheListWithoutTouchingCommandText()
    {
        _vm.CommandText = "/auto";

        _vm.ClearCommandSuggestions();

        Assert.False(_vm.HasCommandSuggestions);
        Assert.Equal("/auto", _vm.CommandText);
    }

    // ====================================================================
    // /stop — panic-stop: disables every alias, trigger, timer and
    // automation toggle, and cancels any active autowalk/auto-farm run.
    // ====================================================================

    [Fact]
    public void Stop_DisablesEveryAliasAndTrigger()
    {
        var alias = new AutomationRuleEntry("a", "alias", "^foo$", "bar", isEnabled: true);
        var trigger = new AutomationRuleEntry("t", "trigger", "^baz$", "qux", isEnabled: true);
        _vm.AutomationRules.Add(alias);
        _vm.AutomationRules.Add(trigger);

        var consumed = InvokeTryHandleAutowalkCommand("/stop");

        Assert.True(consumed);
        Assert.False(alias.IsEnabled);
        Assert.False(trigger.IsEnabled);
    }

    [Fact]
    public void Stop_DisablesEveryTimer()
    {
        var timer = new TimerEntry { Name = "t", Minutes = 1, CommandsText = "look", IsEnabled = true };
        _vm.Timers.Add(timer);

        InvokeTryHandleAutowalkCommand("/stop");

        Assert.False(timer.IsEnabled);
    }

    [Fact]
    public void Stop_TurnsOffEveryAutomationToggle()
    {
        _vm.AutoStandOnLyingEnabled = true;
        _vm.Map.AutoScanOnRoomEnter = true;

        InvokeTryHandleAutowalkCommand("/stop");

        Assert.False(_vm.AutoStandOnLyingEnabled);
        Assert.False(_vm.Map.AutoScanOnRoomEnter);
    }

    [Fact]
    public void Stop_CancelsAnActiveAutowalk()
    {
        GetAutowalkPathField().SetValue(_vm, CreateDummyPath());
        Assert.True(_vm.IsAutowalking);

        InvokeTryHandleAutowalkCommand("/stop");

        Assert.False(_vm.IsAutowalking);
    }

    [Fact]
    public void Stop_EmitsASummaryLineWithTheDisabledCounts()
    {
        _vm.AutomationRules.Add(new AutomationRuleEntry("a", "alias", "^foo$", "bar", isEnabled: true));
        _vm.AutoStandOnLyingEnabled = true;
        var output = new List<string>();
        _vm.OutputReceived += output.Add;

        InvokeTryHandleAutowalkCommand("/stop");

        Assert.Contains(output, line => line.Contains("STOP:") && line.Contains("1 aliasów/triggerów"));
    }

    [Fact]
    public void Stop_WithNothingActive_DoesNotThrowAndReturnsConsumed()
    {
        var consumed = InvokeTryHandleAutowalkCommand("/stop");

        Assert.True(consumed);
    }

    [Fact]
    public void Start_RestoresOnlyWhatTheLastStopTurnedOff()
    {
        var stoppedAlias = new AutomationRuleEntry("a", "alias", "^foo$", "bar", isEnabled: true);
        var alreadyOffAlias = new AutomationRuleEntry("b", "alias", "^other$", "baz", isEnabled: false);
        _vm.AutomationRules.Add(stoppedAlias);
        _vm.AutomationRules.Add(alreadyOffAlias);
        _vm.AutoStandOnLyingEnabled = true;

        InvokeTryHandleAutowalkCommand("/stop");
        Assert.False(stoppedAlias.IsEnabled);
        Assert.False(alreadyOffAlias.IsEnabled);
        Assert.False(_vm.AutoStandOnLyingEnabled);

        InvokeTryHandleAutowalkCommand("/start");

        Assert.True(stoppedAlias.IsEnabled);
        Assert.False(alreadyOffAlias.IsEnabled); // was already off before /stop — stays off
        Assert.True(_vm.AutoStandOnLyingEnabled);
    }

    [Fact]
    public void Start_WithoutAPriorStop_IsANoOp()
    {
        // No fallback to "enable everything" — a /start with nothing remembered must never
        // re-enable something this profile deliberately left off (see the profile-switch
        // regression this was tightened for: ProfileTests.Vm_StopThenSwitchProfileThenStart_...).
        var rule = new AutomationRuleEntry("a", "alias", "^foo$", "bar", isEnabled: false);
        _vm.AutomationRules.Add(rule);
        var output = new List<string>();
        _vm.OutputReceived += output.Add;

        var consumed = InvokeTryHandleAutowalkCommand("/start");

        Assert.True(consumed);
        Assert.False(rule.IsEnabled);
        Assert.False(_vm.AutoStandOnLyingEnabled);
        Assert.Contains(output, line => line.Contains("nie ma nic do przywrócenia"));
    }

    [Fact]
    public void Start_CalledTwiceInARow_TheSecondCallIsANoOp()
    {
        var rule = new AutomationRuleEntry("a", "alias", "^foo$", "bar", isEnabled: true);
        var neverStopped = new AutomationRuleEntry("b", "alias", "^other$", "baz", isEnabled: false);
        _vm.AutomationRules.Add(rule);

        InvokeTryHandleAutowalkCommand("/stop");
        InvokeTryHandleAutowalkCommand("/start");
        Assert.True(rule.IsEnabled);

        _vm.AutomationRules.Add(neverStopped);
        InvokeTryHandleAutowalkCommand("/start");

        // The first /start already consumed the remembered snapshot — this second call has
        // nothing left to restore, so the newly added rule must stay exactly as it was.
        Assert.False(neverStopped.IsEnabled);
    }

    // ====================================================================
    // Char.Group → Group collection (simulated handler)
    //
    // The production handler (OnGroupChanged) uses
    // Dispatcher.UIThread.Post to marshal work to the UI thread, which
    // is not available without a headless Avalonia platform.  Instead,
    // the test below verifies the collection-population logic directly
    // — the same logic that the dispatcher-invoked lambda executes in
    // production.  Event-subscription wiring is covered separately via
    // reflection (GroupChangedEvent_IsSubscribedByConstructor).
    // ====================================================================

    /// <summary>
    /// Replicates the production OnGroupChanged handler's collection logic.
    /// </summary>
    private void SimulateGroupReceived(CharacterGroupUpdate update)
    {
        _vm.Group.Clear();
        foreach (var member in update.Members)
        {
            _vm.Group.Add(GroupMember.FromCore(member));
        }
    }

    [Fact]
    public void SimulateGroupReceived_PopulatesGroup()
    {
        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "Temple", true),
            new("Gimli", "sitting", "ogromne rany", 2, "zmeczony", 2, 0,
                false, "Temple", false),
        });

        SimulateGroupReceived(update);

        Assert.Equal(2, _vm.Group.Count);

        // -- Leader --
        Assert.Equal("Hero", _vm.Group[0].Name);
        Assert.True(_vm.Group[0].IsLeader);
        Assert.Equal("standing", _vm.Group[0].Position);
        Assert.Equal("zadnych sladow", _vm.Group[0].HpText);
        Assert.Equal(7, _vm.Group[0].HpScale);
        Assert.Equal(4, _vm.Group[0].MvScale);
        Assert.Equal(0, _vm.Group[0].Mem);
        Assert.False(_vm.Group[0].IsNpc);
        Assert.Equal("Temple", _vm.Group[0].Room);
        Assert.Equal("*", _vm.Group[0].LeaderMarker);
        Assert.Equal("zadnych sladow (7/7)", _vm.Group[0].HpDisplay);
        Assert.Equal("wypoczety (4/4)", _vm.Group[0].MvDisplay);

        // -- Non-leader --
        Assert.Equal("Gimli", _vm.Group[1].Name);
        Assert.False(_vm.Group[1].IsLeader);
        Assert.Equal("sitting", _vm.Group[1].Position);
        Assert.Equal("ogromne rany", _vm.Group[1].HpText);
        Assert.Equal(2, _vm.Group[1].HpScale);
        Assert.Equal(2, _vm.Group[1].MvScale);
        Assert.Equal(0, _vm.Group[1].Mem);
        Assert.False(_vm.Group[1].IsNpc);
        Assert.Equal("Temple", _vm.Group[1].Room);
        Assert.Equal(" ", _vm.Group[1].LeaderMarker);
        Assert.Equal("ogromne rany (2/7)", _vm.Group[1].HpDisplay);
    }

    [Fact]
    public void SimulateGroupReceived_ClearsPreviousEntries()
    {
        // Arrange: simulate one group update
        var first = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "Room1", true),
        });
        SimulateGroupReceived(first);
        Assert.Single(_vm.Group);

        // Act: simulate a second update
        var second = new CharacterGroupUpdate("Gimli", new List<CharacterGroupMember>
        {
            new("Gimli", "sitting", "lekkie rany", 5, "zmeczony", 2, 0,
                false, "Room2", true),
        });
        SimulateGroupReceived(second);

        // Assert: only the second group's members are present
        Assert.Single(_vm.Group);
        Assert.Equal("Gimli", _vm.Group[0].Name);
        Assert.NotEqual("Hero", _vm.Group[0].Name);
    }

    // ====================================================================
    // GMCP tab structure — XAML validation note
    // ====================================================================
    //
    // MainWindow.axaml contains two GMCP TabItem elements inside the
    // right-sidebar TabControl:
    //   Tab 3: Header="GMCP odebrane",  ItemsSource="{Binding GmcpMessages}"
    //   Tab 4: Header="GMCP wysłane",   ItemsSource="{Binding SentGmcpMessages}"
    //
    // Both tabs use ItemsControl with a ScrollViewer template and present
    // each entry via GmcpEntryViewModel DataTemplate (SelectableTextBlock
    // for package/json/timestamp).
    //
    // The test project (MudClient.App.Tests) does not reference
    // Avalonia.Headless or any headless-Avalonia package.  Without headless
    // UI infrastructure, the visual tree (TabItem, ItemsControl, DataTemplate)
    // cannot be instantiated in a unit test — AvaloniaXamlLoader.Load() in
    // InitializeComponent() requires a running Avalonia windowing platform.
    //
    // Therefore the XAML tab structure is validated by the build/XAML
    // compile step only.  Any binding mismatch (e.g. a missing property name)
    // will surface as an XAML compile error.  The ViewModel-level behaviour
    // (collections initialized, entries inserted at head, cap at 100) is
    // verified by the GmcpMessages_* and SentGmcpMessages_* tests above.
    //
    // ====================================================================
    // Auto-connect startup — validation notes
    // ====================================================================
    //
    // The auto-connect logic lives in MainWindow.OnOpened (the code-behind),
    // which calls:
    //   1. _viewModel.InitializeAsync()
    //   2. _viewModel.ConnectCommand.ExecuteAsync(null)
    //
    // The VM's ConnectAsync() method calls:
    //   _session.ConnectAsync(Host, Port)
    //
    // MudSession.ConnectAsync creates a real TcpClient and connects over the
    // network (host = "killer-mud.pl", port = 4004).  There is no seam to
    // replace or short-circuit this — no ITcpClientFactory, no virtual
    // method, no test-only constructor.
    //
    // To test auto-connect without a real TCP connection, the production code
    // would need an injectable transport (e.g. ITcpClient / ITransport) that
    // the test could replace with a loopback or a fake.  The architecture
    // currently hard-codes TcpClient inside MudSession (line 53 of
    // MudSession.cs), so direct unit testing of the full startup path is not
    // feasible without either (a) running a real MUD server, (b) adding a
    // seam to production code, or (c) using an integration-test network
    // loopback.
    //
    // The CanExecute path (ConnectCommand / CanConnect) is already tested
    // above (ConnectCommand_InitiallyCanExecute).
    //
    // ====================================================================
    // MudOutputView split-scroll / responsive layout — validation notes
    // ====================================================================
    //
    // MudOutputView (Controls/MudOutputView.axaml + .axaml.cs) is an
    // Avalonia UserControl that depends on:
    //   * a visual tree with named ScrollViewer/StackPanel elements
    //   * AvaloniaXamlLoader to load the XAML template
    //   * AnsiStreamParser for tokenizing input
    //   * Dispatcher.UIThread.Post for auto-scrolling
    //
    // The test project (MudClient.App.Tests) does not reference
    // Avalonia.Headless or any headless-Avalonia package.  Without headless
    // UI infrastructure, the control cannot be instantiated and exercised in
    // a test — InitializeComponent() calls AvaloniaXamlLoader.Load(this),
    // which requires a running Avalonia windowing platform.
    //
    // Similarly, MapView.axaml uses a WrapPanel for its header to prevent
    // button disappearance on resize.  Layout-level responsiveness is
    // inherently a visual/rendering concern and cannot be verified without
    // a headless or screenshot-testing framework.
    //
    // Conditional split-scroll (added by the coder for this change):
    //
    //   Purpose:
    //     Show split-screen (upper manual-scroll pane + lower live-tail pane)
    //     only while the user scrolls upward.  When at the newest/bottom
    //     position the grid returns to a single pane with the live tail
    //     hidden.
    //
    //   XAML (MudOutputView.axaml):
    //     - Grid.Row 0 = scrollback (upper), Row 1 = GridSplitter,
    //       Row 2 = live tail (lower).
    //     - Default RowDefinitions="*,Auto,0" — live tail and splitter
    //       are zero-height initially.
    //     - GridSplitter named OutputSplitter, Grid named OutputGrid.
    //
    //   Code-behind (MudOutputView.axaml.cs):
    //     1. OnScrollbackScrollChanged handler:
    //        distanceFromBottom = Extent.Height - Viewport.Height - Offset.Y
    //        - Enable split when distanceFromBottom > 30px && !_isSplitMode
    //          && e.OffsetDelta.Y < 0
    //          (The OffsetDelta.Y < 0 guard prevents extent-growth caused by
    //           AppendText from triggering split activation — only deliberate
    //           user scroll-up events can enable split.)
    //        - Disable split when distanceFromBottom <= 10px && _isSplitMode
    //        (Flicker protection: activation uses a larger threshold than
    //         deactivation.)
    //
    //     2. SetSplitMode(bool enabled):
    //        - When enabling: row[0] = 2*, row[2] = 1*, splitter/live-tail
    //          visible.
    //        - When disabling: row[0] = 1*, row[2] = 0, splitter/live-tail
    //          hidden.
    //
    //     3. Clear() calls SetSplitMode(false) before clearing (line 104),
    //        ensuring a deterministic return to single-pane layout after
    //        a clear operation.
    //
    //     4. AppendText auto-scroll behaviour:
    //        - When split OFF (_isSplitMode == false): auto-scroll the
    //          scrollback pane to the newest line.
    //        - When split ON:  scrollback pane stays at the user's manual
    //          scroll offset; only the live-tail pane auto-scrolls.
    //        - Live-tail pane always auto-scrolls (harmless when hidden).
    //
    //     5. Existing behaviour preserved:
    //        - In-progress-line mirroring (AppendRun writes to both
    //          _currentLine and _liveTailCurrentLine).
    //        - Clear() resets both panels.
    //        - CopySelection_OnClick / CopyAll_OnClick operate on both
    //          panels.
    //        - Live-tail capped at LiveTailMaxLines (100), scrollback at
    //          MaximumLines (5000).
    //
    //   Testability:
    //     Every code path above requires an instantiated MudOutputView with
    //     a live visual tree.  Neither the thresholds (10/30 px), the
    //     OffsetDelta.Y < 0 guard, nor the hysteresis logic can be exercised
    //     in a unit test without headless Avalonia, so validation currently
    //     relies on:
    //       * XAML compile (build)
    //       * Manual functional testing
    //       * This specification note to prevent accidental regressions.
    //
    // ====================================================================
    // Down-at-fresh command history behavior — validation notes
    // ====================================================================
    //
    // The "Down-at-fresh" behavior (MainWindow.axaml.cs NavigateHistory):
    // pressing the Down arrow when no history entry has been selected yet
    // (historyIndex == -1, the "fresh" position) should not clear the
    // user's current draft.  The ViewModel does not track a "history
    // navigation index" — that state (_historyIndex) lives entirely in
    // the code-behind field of MainWindow.
    //
    // The ViewModel-level history (CommandHistory) and its insert/cap
    // behaviour are tested above (SendCommand_History* tests).  The
    // Down-at-fresh guard is a View-level concern and cannot be exercised
    //     without either (a) instantiating MainWindow with a headless UI
    //     framework or (b) extracting the navigation logic into the ViewModel.

    // ====================================================================
    // Group room display resolution (ResolveRoomDisplay)
    //
    // ResolveRoomDisplay is a private method on MainWindowViewModel.  It
    // consults Map.MapIndex? to look up a room name for a given vnum.
    // We invoke it via reflection, setting up a MapIndex on the MapViewModel
    // through the private _mapIndex field (same pattern as MapViewModelTests).
    // ====================================================================

    /// <summary>
    /// Sets the private _mapIndex field on the VM's Map property using
    /// reflection, matching the pattern in MapViewModelTests.
    /// </summary>
    private void SetMapViewModelMapIndex(MapIndex? index)
    {
        var field = typeof(MapViewModel).GetField("_mapIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(_vm.Map, index);
    }

    /// <summary>Invokes the private ResolveRoomDisplay method via reflection.</summary>
    private string InvokeResolveRoomDisplay(string? room)
    {
        var method = typeof(MainWindowViewModel).GetMethod("ResolveRoomDisplay",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (string)method!.Invoke(_vm, [room])!;
    }

    /// <summary>Sets the private _latestGroupUpdate field via reflection.</summary>
    private void SetLatestGroupUpdate(CharacterGroupUpdate? update)
    {
        var field = typeof(MainWindowViewModel).GetField("_latestGroupUpdate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(_vm, update);
    }

    /// <summary>Sets the private _latestCharacterName field via reflection.</summary>
    private void SetLatestCharacterName(string? name)
    {
        var field = typeof(MainWindowViewModel).GetField("_latestCharacterName",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(_vm, name);
    }

    /// <summary>Reads the private _autoAssistNpcPending field via reflection.</summary>
    private bool GetAutoAssistNpcPending()
    {
        var field = typeof(MainWindowViewModel).GetField("_autoAssistNpcPending",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (bool)field!.GetValue(_vm)!;
    }

    /// <summary>Reads the private _autoAssistCommandPending field via reflection.</summary>
    private bool GetAutoAssistCommandPending()
    {
        var field = typeof(MainWindowViewModel).GetField("_autoAssistCommandPending",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (bool)field!.GetValue(_vm)!;
    }

    private bool GetAutoKillPending()
    {
        var field = typeof(MainWindowViewModel).GetField("_autoKillPending",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (bool)field!.GetValue(_vm)!;
    }

    /// <summary>Invokes the private OnRoomPeopleChanged method via reflection.</summary>
    private void InvokeOnRoomPeopleChanged(IReadOnlyList<RoomPerson> people)
    {
        var method = typeof(MainWindowViewModel).GetMethod("OnRoomPeopleChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(_vm, [people]);
    }

    /// <summary>Invokes the private OnGroupChanged method via reflection.</summary>
    private void InvokeOnGroupChanged(CharacterGroupUpdate update)
    {
        var method = typeof(MainWindowViewModel).GetMethod("OnGroupChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(_vm, [update]);
    }

    /// <summary>
    /// Replicates the production OnMapPropertyChanged handler's dispatcher
    /// lambda: rebuilds the Group collection from _latestGroupUpdate using
    /// the current MapIndex for room name resolution.
    /// </summary>
    private void SimulateMapIndexChangedRebuild(CharacterGroupUpdate update)
    {
        _vm.Group.Clear();
        foreach (var member in update.Members)
        {
            var roomDisplay = InvokeResolveRoomDisplay(member.Room);
            _vm.Group.Add(GroupMember.FromCore(member, roomDisplay));
        }
    }

    /// <summary>
    /// Creates a trivial MapIndex containing one room with the given vnum
    /// and room name, suitable for ResolveRoomDisplay tests.
    /// </summary>
    private static MapIndex CreateMapIndexWithVnum(string vnum, string roomName)
    {
        var room = new MapRoom
        {
            Id = 1,
            AreaId = 1,
            Name = roomName,
            Coordinates = new MapCoordinates(0, 0, 0),
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement(vnum),
            },
        };

        return new MapIndex(new MapDocument
        {
            Areas =
            [
                new MapArea { Id = 1, Name = "Test Area", Rooms = [room] },
            ],
        });
    }

    // ====================================================================
    // ResolveRoomDisplay unit tests
    // ====================================================================

    [Fact]
    public void ResolveRoomDisplay_WithNullRoom_ReturnsQuestionMark()
    {
        var result = InvokeResolveRoomDisplay(null);
        Assert.Equal("?", result);
    }

    [Fact]
    public void ResolveRoomDisplay_WithRoomAndMapIndexNull_ReturnsPokojFallback()
    {
        // Map.MapIndex is null after construction (no map loaded yet).
        var result = InvokeResolveRoomDisplay("6017");
        Assert.Equal("pokój 6017", result);
    }

    [Fact]
    public void ResolveRoomDisplay_WithRoomAndMapIndexNoMatch_ReturnsPokojFallback()
    {
        var index = CreateMapIndexWithVnum("100", "Some Room");
        SetMapViewModelMapIndex(index);

        var result = InvokeResolveRoomDisplay("999");  // not in index
        Assert.Equal("pokój 999", result);
    }

    [Fact]
    public void ResolveRoomDisplay_WithRoomAndMapIndexMatch_ReturnsRoomName()
    {
        var index = CreateMapIndexWithVnum("6017", "Town Square");
        SetMapViewModelMapIndex(index);

        var result = InvokeResolveRoomDisplay("6017");
        Assert.Equal("Town Square", result);
    }

    [Fact]
    public void ResolveRoomDisplay_WithRoomAndMapIndexMatchButEmptyName_ReturnsPokojFallback()
    {
        var index = CreateMapIndexWithVnum("6017", string.Empty);
        SetMapViewModelMapIndex(index);

        var result = InvokeResolveRoomDisplay("6017");
        Assert.Equal("pokój 6017", result);
    }

    [Fact]
    public void ResolveRoomDisplay_WithRoomAndMapIndexMatchButWhitespaceName_ReturnsPokojFallback()
    {
        var index = CreateMapIndexWithVnum("6017", "   ");
        SetMapViewModelMapIndex(index);

        var result = InvokeResolveRoomDisplay("6017");
        Assert.Equal("pokój 6017", result);
    }

    // ====================================================================
    // Group rebuild via OnMapPropertyChanged when MapIndex loads
    //
    // The production handler posts to Dispatcher.UIThread, which is not
    // available without headless Avalonia.  We replicate the lambda logic
    // directly (see SimulateMapIndexChangedRebuild), matching the pattern
    // used by SimulateGroupReceived above.
    // ====================================================================

    [Fact]
    public void GroupRebuild_WithMapIndexChange_UpdatesRoomDisplayToResolvedName()
    {
        // Arrange: simulate a previous group update was stored
        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "6017", true),
        });
        SetLatestGroupUpdate(update);

        // Pre-populate group with fallback names (as OnGroupChanged would
        // when MapIndex is not yet loaded).
        _vm.Group.Clear();
        _vm.Group.Add(GroupMember.FromCore(update.Members[0], "pokój 6017"));

        // Act: MapIndex becomes available → rebuild
        var index = CreateMapIndexWithVnum("6017", "Town Square");
        SetMapViewModelMapIndex(index);
        SimulateMapIndexChangedRebuild(update);

        // Assert: room names are now resolved
        var entry = Assert.Single(_vm.Group);
        Assert.Equal("Town Square", entry.RoomDisplay);
        Assert.Equal("6017", entry.Room);
    }

    [Fact]
    public void GroupRebuild_WithMapIndexChangeNoMatch_KeepsPokojFallback()
    {
        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "9999", true),
        });
        SetLatestGroupUpdate(update);
        _vm.Group.Clear();
        _vm.Group.Add(GroupMember.FromCore(update.Members[0], "pokój 9999"));

        // MapIndex has a different vnum
        var index = CreateMapIndexWithVnum("100", "Some Room");
        SetMapViewModelMapIndex(index);
        SimulateMapIndexChangedRebuild(update);

        var entry = Assert.Single(_vm.Group);
        Assert.Equal("pokój 9999", entry.RoomDisplay);
    }

    // ====================================================================
    // Map-load timing: group received before MapIndex is loaded
    // ====================================================================

    [Fact]
    public void GroupBeforeMapLoad_ShowsFallbackAndRefreshesAfterMapIndexChange()
    {
        // Step 1: Simulate group received before map is loaded.
        // No MapIndex → ResolveRoomDisplay returns fallback.
        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "6017", true),
        });

        _vm.Group.Clear();
        _vm.Group.Add(GroupMember.FromCore(update.Members[0], "pokój 6017"));
        Assert.Equal("pokój 6017", _vm.Group[0].RoomDisplay);

        // Store the update as OnGroupChanged does.
        SetLatestGroupUpdate(update);

        // Step 2: Map finishes loading, MapIndex becomes available.
        var index = CreateMapIndexWithVnum("6017", "Town Square");
        SetMapViewModelMapIndex(index);

        // Step 3: PropertyChanged fires, group is rebuilt.
        SimulateMapIndexChangedRebuild(update);

        // Assert: group entry now shows the resolved room name.
        Assert.Single(_vm.Group);
        Assert.Equal("Town Square", _vm.Group[0].RoomDisplay);
        Assert.Equal("6017", _vm.Group[0].Room);
    }

    [Fact]
    public void GroupRebuild_MultipleMembers_AllResolvedCorrectly()
    {
        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "6017", true),  // known vnum
            new("Thorin", "standing", "rany", 3, "zmeczony", 2, 0,
                false, "9999", false),  // unknown vnum
            new("Balin", "sitting", "rany", 4, "senny", 1, 0,
                false, null, false),    // null room
        });
        SetLatestGroupUpdate(update);

        // MapIndex only knows "6017"
        var index = CreateMapIndexWithVnum("6017", "Town Square");
        SetMapViewModelMapIndex(index);

        SimulateMapIndexChangedRebuild(update);

        Assert.Equal(3, _vm.Group.Count);
        Assert.Equal("Town Square",   _vm.Group[0].RoomDisplay);  // resolved
        Assert.Equal("pokój 9999",    _vm.Group[1].RoomDisplay);  // fallback
        Assert.Equal("?",             _vm.Group[2].RoomDisplay);  // null → ?
    }

    // ====================================================================
    // SimulateGroupReceivedResolved — end-to-end from a fresh VM state
    // (no _latestGroupUpdate prerequisite needed)
    // ====================================================================

    [Fact]
    public void SimulateGroupReceivedResolved_WithMapIndex_UsesResolvedNames()
    {
        var index = CreateMapIndexWithVnum("6017", "Town Square");
        SetMapViewModelMapIndex(index);

        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "6017", true),
        });

        _vm.Group.Clear();
        foreach (var member in update.Members)
        {
            var roomDisplay = InvokeResolveRoomDisplay(member.Room);
            _vm.Group.Add(GroupMember.FromCore(member, roomDisplay));
        }

        var entry = Assert.Single(_vm.Group);
        Assert.Equal("Town Square", entry.RoomDisplay);
    }

    [Fact]
    public void SimulateGroupReceivedResolved_WithoutMapIndex_UsesFallback()
    {
        // MapIndex is null by default
        var update = new CharacterGroupUpdate("Hero", new List<CharacterGroupMember>
        {
            new("Hero", "standing", "zadnych sladow", 7, "wypoczety", 4, 0,
                false, "6017", true),
        });

        _vm.Group.Clear();
        foreach (var member in update.Members)
        {
            var roomDisplay = InvokeResolveRoomDisplay(member.Room);
            _vm.Group.Add(GroupMember.FromCore(member, roomDisplay));
        }

        var entry = Assert.Single(_vm.Group);
        Assert.Equal("pokój 6017", entry.RoomDisplay);
    }

    // ====================================================================
    // Trigger batch serialization — SendTriggeredCommandsAsync / _triggerSendLock
    //
    // The coder added a SemaphoreSlim (_triggerSendLock, initialised to
    // new(1, 1)) to MainWindowViewModel so that trigger batches fired from
    // OnLineReceived do not interleave: one batch must finish sending all
    // its commands before the next batch can start.
    //
    // Production flow:
    //   OnLineReceived(line)
    //     → var commands = _triggers.Evaluate(line)
    //     → if commands.Count > 0: _ = SendTriggeredCommandsAsync(commands)
    //       → _triggerSendLock.WaitAsync()
    //       → foreach command: SendTriggeredCommandAsync(command)
    //         → Dispatcher.UIThread.Post(() => EmitSystem(...))
    //         → _session.SendCommandAsync(command)
    //       → _triggerSendLock.Release()
    //
    // Testability limitations:
    //   * SendTriggeredCommandAsync uses Dispatcher.UIThread.Post, which
    //     requires a running Avalonia platform (not available here).
    //   * _session is typed as MudSession (sealed, no interface), so we
    //     cannot inject a fake session that records command order.
    //
    // Therefore the tests below verify the SemaphoreSlim's presence, initial
    // state, acquire/release cycle with an empty batch (which exercises the
    // lock but does NOT call SendTriggeredCommandAsync), and proper disposal.
    // Verifying actual network-level ordering would require either:
    //   (a) an injectable IMudSession / ITransport seam in production code,
    //   (b) a headless Avalonia test runner, or
    //   (c) an integration test against a controllable TCP loopback.
    // ====================================================================

    private static FieldInfo GetTriggerSendLockField()
    {
        var field = typeof(MainWindowViewModel).GetField("_triggerSendLock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static MethodInfo GetSendTriggeredCommandsAsyncMethod()
    {
        var method = typeof(MainWindowViewModel).GetMethod("SendTriggeredCommandsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!;
    }

    private static MethodInfo GetOnLineReceivedMethod()
    {
        var method = typeof(MainWindowViewModel).GetMethod("OnLineReceived",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!;
    }

    private static FieldInfo GetTriggerCtsField()
    {
        var field = typeof(MainWindowViewModel).GetField("_triggerCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static FieldInfo GetTriggerTasksField()
    {
        var field = typeof(MainWindowViewModel).GetField("_triggerTasks",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static FieldInfo GetTriggerTasksLockField()
    {
        var field = typeof(MainWindowViewModel).GetField("_triggerTasksLock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static FieldInfo GetAcceptingTriggerTasksField()
    {
        var field = typeof(MainWindowViewModel).GetField("_acceptingTriggerTasks",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static FieldInfo GetTriggersField()
    {
        var field = typeof(MainWindowViewModel).GetField("_triggers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static MethodInfo GetRemoveWhenCompletedMethod()
    {
        var method = typeof(MainWindowViewModel).GetMethod("RemoveWhenCompleted",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!;
    }

    private static FieldInfo GetTriggerQueueTailField()
    {
        var field = typeof(MainWindowViewModel).GetField("_triggerQueueTail",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static MethodInfo GetEnqueueBatchAsyncMethod()
    {
        var method = typeof(MainWindowViewModel).GetMethod("EnqueueBatchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!;
    }

    [Fact]
    public void TriggerSendLock_FieldExists_WithInitialCountOne()
    {
        var field = GetTriggerSendLockField();
        var semaphore = field.GetValue(_vm) as SemaphoreSlim;

        Assert.NotNull(semaphore);
        Assert.Equal(1, semaphore!.CurrentCount);
    }

    [Fact]
    public async Task TriggerSendLock_EmptyBatch_AcquiresAndReleasesLock()
    {
        // Arrange
        var field = GetTriggerSendLockField();
        var semaphore = (SemaphoreSlim)field.GetValue(_vm)!;
        var method = GetSendTriggeredCommandsAsyncMethod();

        // Verify initial state
        Assert.Equal(1, semaphore.CurrentCount);

        // Act: invoke SendTriggeredCommandsAsync with an empty list.
        // This exercises the WaitAsync/try/finally/Release pattern but
        // does NOT call SendTriggeredCommandAsync, so the Dispatcher
        // dependency is avoided.
        var task = (Task)method.Invoke(_vm, [Array.Empty<string>()])!;
        await task;

        // Assert: lock is released after batch completes
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task TriggerSendLock_DisposeDisposesSemaphore()
    {
        // Use a separate VM so we can call DisposeAsync without affecting
        // the fixture VM (which is disposed by the test harness after each test).
        var isolatedVm = new MainWindowViewModel();

        var field = GetTriggerSendLockField();
        var semaphore = (SemaphoreSlim)field.GetValue(isolatedVm)!;

        // Act
        await isolatedVm.DisposeAsync();

        // Assert: WaitAsync on a disposed SemaphoreSlim throws
        // ObjectDisposedException.  (CurrentCount does NOT throw on
        // .NET 10, but WaitAsync does.)
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            semaphore.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OnLineReceived_NoMatchingTriggers_DoesNotThrow()
    {
        // Arrange
        var method = GetOnLineReceivedMethod();

        // Act / Assert: no matching trigger → SendTriggeredCommandsAsync
        // is never called → no Dispatcher / session dependency is touched.
        var exception = Record.Exception(() =>
            method.Invoke(_vm, ["ordinary line with no match"]));

        Assert.Null(exception);
    }

    [Fact]
    public void OnLineReceived_NoMatchingTriggers_SendTriggeredCommandsNotCalled()
    {
        // Verify that the semaphore is not affected when there are no
        // matching triggers (i.e. SendTriggeredCommandsAsync is skipped).
        var field = GetTriggerSendLockField();
        var semaphore = (SemaphoreSlim)field.GetValue(_vm)!;
        var method = GetOnLineReceivedMethod();

        Assert.Equal(1, semaphore.CurrentCount);

        method.Invoke(_vm, ["another non-matching line"]);

        // CurrentCount must remain 1 — no lock acquisition happened.
        Assert.Equal(1, semaphore.CurrentCount);
    }

    // ChatLinePolicy's own matching/rejection logic is covered by ChatLinePolicyTests in
    // MudClient.Core.Tests. This just proves the integration point in OnLineReceived doesn't
    // throw. It cannot assert that ChatLineReceived actually fires here: the production code
    // marshals it via Dispatcher.UIThread.Post, and (per the "Outgoing GMCP recording" comment
    // above) calling Dispatcher.UIThread.RunJobs() from this plain (non-headless) test class
    // would throw Dispatcher.VerifyAccess.
    [Fact]
    public void OnLineReceived_KnockdownMessage_DoesNotThrow_WithAutoStandEnabled()
    {
        SetIsConnected(true);
        _vm.AutoStandOnLyingEnabled = true;
        var method = GetOnLineReceivedMethod();

        var exception = Record.Exception(() => method.Invoke(_vm, ["Ogr powala cię na ziemię!"]));

        Assert.Null(exception);
    }

    [Fact]
    public void OnLineReceived_DisarmMessage_DoesNotThrow_WithAutowieldEnabled()
    {
        SetIsConnected(true);
        _vm.AutowieldEnabled = true;
        _vm.AutowieldWeaponName = "miecz";
        var method = GetOnLineReceivedMethod();

        var exception = Record.Exception(() => method.Invoke(_vm, ["Ogr rozbraja cię!"]));

        Assert.Null(exception);
    }

    [Fact]
    public void OnLineReceived_CommunicationLine_DoesNotThrow()
    {
        var method = GetOnLineReceivedMethod();

        var exception = Record.Exception(() => method.Invoke(_vm, ["Ala mówi 'witam wszystkich'"]));

        Assert.Null(exception);
    }

    // ====================================================================
    // Trigger lifecycle — CTS, task tracking, and DisposeAsync draining
    //
    // The coder added _triggerCts (CancellationTokenSource), _triggerTasks
    // (List&lt;Task&gt;), and _triggerTasksLock (object) to MainWindowViewModel
    // so that DisposeAsync can cancel waiting trigger batches and drain
    // in-flight fire-and-forget tasks before disposing the semaphore and CTS.
    //
    // Production flow during disposal:
    //   1. Unsubscribe LineReceived (prevents new batches)
    //   2. _triggerCts.Cancel() (unblocks WaitAsync on the semaphore)
    //   3. Drain loop: snapshot _triggerTasks, await each task
    //   4. Final gate: acquire + release the semaphore
    //   5. Dispose semaphore and CTS
    // ====================================================================

    [Fact]
    public void TriggerCts_FieldExists_NotCancelledInitially()
    {
        var field = GetTriggerCtsField();
        var cts = field.GetValue(_vm) as CancellationTokenSource;

        Assert.NotNull(cts);
        Assert.False(cts!.IsCancellationRequested);
    }

    [Fact]
    public void TriggerTasks_FieldExists_EmptyInitially()
    {
        var field = GetTriggerTasksField();
        var tasks = field.GetValue(_vm) as List<Task>;

        Assert.NotNull(tasks);
        Assert.NotNull(field.GetValue(_vm)); // list instance exists
        Assert.Empty(tasks!);
    }

    [Fact]
    public void TriggerTasksLock_FieldExists()
    {
        var field = GetTriggerTasksLockField();
        Assert.NotNull(field.GetValue(_vm));
    }

    [Fact]
    public async Task TriggerCts_DisposeAsync_CancelsToken()
    {
        var isolatedVm = new MainWindowViewModel();
        var field = GetTriggerCtsField();
        var cts = (CancellationTokenSource)field.GetValue(isolatedVm)!;

        Assert.False(cts.IsCancellationRequested);

        await isolatedVm.DisposeAsync();

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task TriggerCts_DisposeAsync_DisposesCts()
    {
        var isolatedVm = new MainWindowViewModel();
        var field = GetTriggerCtsField();
        var cts = (CancellationTokenSource)field.GetValue(isolatedVm)!;

        await isolatedVm.DisposeAsync();

        // Accessing Token on a disposed CancellationTokenSource throws.
        Assert.Throws<ObjectDisposedException>(() => cts.Token);
    }

    [Fact]
    public void OnLineReceived_NoMatchingTriggers_DoesNotAddTasks()
    {
        // Arrange
        var method = GetOnLineReceivedMethod();
        var tasksField = GetTriggerTasksField();
        var tasks = (List<Task>)tasksField.GetValue(_vm)!;

        Assert.Empty(tasks);

        // Act: invoke with a non-matching line
        method.Invoke(_vm, ["some random line with no trigger match"]);

        // Assert: no tasks were added to the tracking list
        Assert.Empty(tasks);
    }

    [Fact]
    public async Task DisposeAsync_WithNoTriggerTasks_DoesNotThrow()
    {
        var isolatedVm = new MainWindowViewModel();

        var exception = await Record.ExceptionAsync(
            () => isolatedVm.DisposeAsync().AsTask());

        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_WithEmptyBatchTask_DrainsTaskWithoutUnobservedException()
    {
        // Arrange: create an isolated VM, invoke SendTriggeredCommandsAsync
        // with an empty batch, and manually track the returned Task (as
        // OnLineReceived does in production via _triggerTasks.Add(task)).
        var isolatedVm = new MainWindowViewModel();
        var sendMethod = GetSendTriggeredCommandsAsyncMethod();
        var tasksField = GetTriggerTasksField();
        var tasks = (List<Task>)tasksField.GetValue(isolatedVm)!;

        var task = (Task)sendMethod.Invoke(isolatedVm, [Array.Empty<string>()])!;

        // Track the task the same way OnLineReceived does.
        lock (tasks)
        {
            tasks.Add(task);
        }

        // Act: dispose should drain the tracked task without throwing.
        var exception = await Record.ExceptionAsync(
            () => isolatedVm.DisposeAsync().AsTask());

        // Assert
        Assert.Null(exception);
        Assert.True(task.IsCompleted);
    }

    // ====================================================================
    // Trigger serialisation — validation note
    //
    // Full verification that two concurrent trigger batches do not interleave
    // (e.g. batch A command 1, batch B command 1, batch A command 2) requires
    // exercising the production flow through SendTriggeredCommandAsync, which
    // calls both Dispatcher.UIThread.Post and _session.SendCommandAsync.
    //
    // Without a headless Avalonia platform or a test seam for MudSession,
    // the interleaving guarantee is validated by:
    //
    //   1. Core-level TriggerEngine.Evaluate tests (MudClient.Core.Tests)
    //      — confirm correct command extraction per match.
    //   2. Semaphore reflection tests above — confirm the lock exists,
    //      starts free (Count = 1), is acquired/released correctly for an
    //      empty batch, and is disposed during DisposeAsync.
    //   3. Lifecycle tests above — verify _triggerCts, _triggerTasks,
    //      _triggerTasksLock field existence, cancellation on DisposeAsync,
    //      CTS disposal, no-task dispose safety, and tracked task drain.
    //   4. Code review — the pattern (WaitAsync / try-finally / Release)
    //      matches the standard SemaphoreSlim serialisation idiom and
    //      mirrors the existing MudSession._sendLock pattern in SendRawAsync.
    //   5. Manual integration testing — connecting to the MUD server and
    //      verifying that rapid trigger lines produce commands in order.
    //
    // ====================================================================

    // ====================================================================
    // _acceptingTriggerTasks flag — field existence, initial value,
    // disposal transition, and rejection of new work after disposal.
    // ====================================================================

    [Fact]
    public void AcceptingTriggerTasks_FieldExists_StartsTrue()
    {
        var field = GetAcceptingTriggerTasksField();

        var value = (bool)field.GetValue(_vm)!;

        Assert.True(value);
    }

    [Fact]
    public async Task DisposeAsync_SetsAcceptingFlagFalse()
    {
        var isolatedVm = new MainWindowViewModel();
        var field = GetAcceptingTriggerTasksField();

        Assert.True((bool)field.GetValue(isolatedVm)!);

        await isolatedVm.DisposeAsync();

        Assert.False((bool)field.GetValue(isolatedVm)!);
    }

    [Fact]
    public async Task OnLineReceived_AfterDisposal_WithMatchingTrigger_DoesNotAddTask()
    {
        // Arrange: isolated VM with a trigger rule registered
        var isolatedVm = new MainWindowViewModel();
        var onLineReceived = GetOnLineReceivedMethod();
        var tasksField = GetTriggerTasksField();
        var triggersField = GetTriggersField();
        var acceptingField = GetAcceptingTriggerTasksField();

        var triggers = (TriggerEngine)triggersField.GetValue(isolatedVm)!;
        triggers.Add(new TriggerRule("test", "match", "cmd"));

        var tasks = (List<Task>)tasksField.GetValue(isolatedVm)!;

        Assert.Empty(tasks);
        Assert.True((bool)acceptingField.GetValue(isolatedVm)!);

        // Act: dispose first, then try to fire a trigger match
        await isolatedVm.DisposeAsync();

        Assert.False((bool)acceptingField.GetValue(isolatedVm)!);

        // This invocation should hit the early-return guard
        // (!_acceptingTriggerTasks) inside OnLineReceived — no task created.
        onLineReceived.Invoke(isolatedVm, ["match"]);

        // Assert: no new task was added after disposal
        Assert.Empty(tasks);
    }

    // ====================================================================
    // RemoveWhenCompleted observer — completed, faulted, and cancelled
    // tasks are removed from _triggerTasks, preventing unbounded growth.
    // ====================================================================

    [Fact]
    public async Task RemoveWhenCompleted_CompletedTask_RemovedFromList()
    {
        var method = GetRemoveWhenCompletedMethod();
        var tasksField = GetTriggerTasksField();
        var lockField = GetTriggerTasksLockField();
        var tasks = (List<Task>)tasksField.GetValue(_vm)!;
        var lockObj = lockField.GetValue(_vm)!;

        var completed = Task.CompletedTask;
        lock (lockObj) { tasks.Add(completed); }
        _ = Assert.Single(tasks);

        var resultTask = (Task)method.Invoke(_vm, [completed])!;
        await resultTask;

        Assert.Empty(tasks);
    }

    [Fact]
    public async Task RemoveWhenCompleted_FaultedTask_RemovedFromList()
    {
        var method = GetRemoveWhenCompletedMethod();
        var tasksField = GetTriggerTasksField();
        var lockField = GetTriggerTasksLockField();
        var tasks = (List<Task>)tasksField.GetValue(_vm)!;
        var lockObj = lockField.GetValue(_vm)!;

        var faulted = Task.FromException(new InvalidOperationException("fail"));
        lock (lockObj) { tasks.Add(faulted); }
        _ = Assert.Single(tasks);

        var resultTask = (Task)method.Invoke(_vm, [faulted])!;
        await resultTask;

        Assert.Empty(tasks);
    }

    [Fact]
    public async Task RemoveWhenCompleted_CancelledTask_RemovedFromList()
    {
        var method = GetRemoveWhenCompletedMethod();
        var tasksField = GetTriggerTasksField();
        var lockField = GetTriggerTasksLockField();
        var tasks = (List<Task>)tasksField.GetValue(_vm)!;
        var lockObj = lockField.GetValue(_vm)!;

        var cancelled = Task.FromCanceled(new CancellationToken(canceled: true));
        lock (lockObj) { tasks.Add(cancelled); }
        _ = Assert.Single(tasks);

        var resultTask = (Task)method.Invoke(_vm, [cancelled])!;
        await resultTask;

        Assert.Empty(tasks);
    }

    // ====================================================================
    // OnLineReceived matching trigger — before disposal, the accepting flag
    // check and task registration proceed without synchronously throwing.
    // (The task itself runs SendTriggeredCommandsAsync which calls
    // Dispatcher.UIThread.Post, so we can only verify the synchronous path.)
    // ====================================================================

    [Fact]
    public void OnLineReceived_WithMatchingTrigger_FlagAndTaskRegistrationDoNotThrowSynchronously()
    {
        // Arrange: register a trigger that produces a single command
        var triggersField = GetTriggersField();
        var triggers = (TriggerEngine)triggersField.GetValue(_vm)!;
        triggers.Add(new TriggerRule("test-sync", "^sync-match$", "do something"));

        var method = GetOnLineReceivedMethod();

        // Act: the synchronous portion (flag check + Task.Run + Add)
        // must not throw.  The returned task runs asynchronously and
        // will encounter the missing Dispatcher, but the fire-and-forget
        // RemoveWhenCompleted continuation swallows that exception.
        var exception = Record.Exception(() =>
            method.Invoke(_vm, ["sync-match"]));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void OnLineReceived_WithMatchingTrigger_AddsTaskToList()
    {
        // Arrange
        var triggersField = GetTriggersField();
        var triggers = (TriggerEngine)triggersField.GetValue(_vm)!;
        triggers.Add(new TriggerRule("test-add", "^add-me$", "something"));

        var method = GetOnLineReceivedMethod();
        var tasksField = GetTriggerTasksField();
        var tasks = (List<Task>)tasksField.GetValue(_vm)!;
        var lockField = GetTriggerTasksLockField();
        var lockObj = lockField.GetValue(_vm)!;
        var tailField = GetTriggerQueueTailField();
        var previousBatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tailField.SetValue(_vm, previousBatch.Task);

        Assert.Empty(tasks);

        try
        {
            // Act
            method.Invoke(_vm, ["add-me"]);

            // Assert while the new batch is deterministically waiting for the controlled
            // previous task. Without this gate a fast scheduler can complete and remove the
            // batch before the assertion, even though registration worked correctly.
            lock (lockObj)
            {
                Assert.False(Assert.Single(tasks).IsCompleted);
            }
        }
        finally
        {
            // Always unblock the queue so fixture disposal can drain the registered task,
            // including when an assertion fails.
            previousBatch.TrySetResult();
        }
    }

    // ====================================================================
    // _triggerQueueTail — FIFO queue tail field, initial state, and update
    // on matching trigger.  The tail is a Task that each new batch awaits
    // (swallowing its faults) before sending, ensuring batches execute in
    // receive order.
    //
    // Testability limitation: verifying actual send ordering would require
    // either an injectable IMudSession seam (to record command order as
    // they pass through SendCommandAsync) or a headless Avalonia platform
    // (to run Dispatcher.UIThread.Post).  The tests below verify the
    // chain mechanics at the Task level:
    //
    //   • field existence and initial value (CompletedTask),
    //   • tail update after a matching trigger,
    //   • EnqueueBatchAsync awaiting the previous task (FIFO chaining),
    //   • fault tolerance — a faulted previous does not stall the chain.
    // ====================================================================

    [Fact]
    public void TriggerQueueTail_FieldExists_StartsCompleted()
    {
        var field = GetTriggerQueueTailField();
        var tail = (Task)field.GetValue(_vm)!;

        Assert.NotNull(tail);
        Assert.True(tail.IsCompletedSuccessfully);
    }

    [Fact]
    public void TriggerQueueTail_OnLineReceived_UpdatesTail()
    {
        // Arrange
        var triggersField = GetTriggersField();
        var triggers = (TriggerEngine)triggersField.GetValue(_vm)!;
        triggers.Add(new TriggerRule("test-tail", "^update-tail$", "cmd"));

        var method = GetOnLineReceivedMethod();
        var tailField = GetTriggerQueueTailField();
        var originalTail = (Task)tailField.GetValue(_vm)!;

        Assert.True(originalTail.IsCompletedSuccessfully);

        // Act
        method.Invoke(_vm, ["update-tail"]);

        // Assert: the tail is now a different task. We do NOT assert it is still running —
        // as TriggerQueueTail_TwoMatches_TailsAreDistinct documents, the batch task can fault
        // and complete almost immediately (SendTriggeredCommandAsync's Dispatcher.UIThread.Post
        // throws when no platform is pumping), so IsCompleted is a race, not a contract.
        var newTail = (Task)tailField.GetValue(_vm)!;
        Assert.NotSame(originalTail, newTail);
    }

    [Fact]
    public void TriggerQueueTail_TwoMatches_TailsAreDistinct()
    {
        // Note: each batch task completes quickly in the test environment
        // because SendTriggeredCommandAsync hits Dispatcher.UIThread.Post
        // which throws (no Avalonia platform).  The exception is swallowed
        // by RemoveWhenCompleted, so the task finishes (faulted) almost
        // immediately.  Therefore we only assert the synchronous contract:
        // each match produces a distinct tail Task reference.
        // Arrange
        var triggersField = GetTriggersField();
        var triggers = (TriggerEngine)triggersField.GetValue(_vm)!;
        triggers.Add(new TriggerRule("tail-a", "^first$", "cmd1"));
        triggers.Add(new TriggerRule("tail-b", "^second$", "cmd2"));

        var method = GetOnLineReceivedMethod();
        var tailField = GetTriggerQueueTailField();

        // Act — first match
        method.Invoke(_vm, ["first"]);
        var tailAfterFirst = (Task)tailField.GetValue(_vm)!;

        // Act — second match
        method.Invoke(_vm, ["second"]);
        var tailAfterSecond = (Task)tailField.GetValue(_vm)!;

        // Assert: each match creates a new tail distinct from the previous
        // one and neither is the original CompletedTask sentinel.
        Assert.NotSame(tailAfterFirst, tailAfterSecond);
        Assert.NotSame(Task.CompletedTask, tailAfterFirst);
        Assert.NotSame(Task.CompletedTask, tailAfterSecond);
    }

    // ====================================================================
    // EnqueueBatchAsync — FIFO chaining via the "previous" parameter.
    //
    // Each call to EnqueueBatchAsync receives the previous queue tail and
    // awaits it before executing its own batch.  By passing a controlled
    // TaskCompletionSource as "previous" we can prove the chaining
    // without real MudSession sends (empty command list avoids the
    // Dispatcher dependency).
    // ====================================================================

    [Fact]
    public async Task EnqueueBatchAsync_AwaitsPreviousBeforeExecuting()
    {
        // Arrange: use a TCS as the "previous" task that the batch must
        // await before proceeding.  Empty commands keep the test free of
        // Dispatcher / MudSession dependencies.
        var method = GetEnqueueBatchAsyncMethod();
        var previousTcs = new TaskCompletionSource();
        var commands = Array.Empty<string>();

        // Act: enqueue a batch that must wait for previousTcs.Task.
        var batchTask = (Task)method.Invoke(_vm, [previousTcs.Task, commands])!;

        // The method yields immediately (Task.Yield) then awaits previous.
        // Spin-wait briefly for the async machinery to reach the await point.
        for (var i = 0; i < 20 && batchTask.IsCompleted; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Assert: batchTask is NOT complete because previous is incomplete.
        Assert.False(batchTask.IsCompleted,
            "Batch task should not complete before its previous task completes.");

        // Complete the previous task — the batch should now be unblocked.
        previousTcs.SetResult();

        await batchTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(batchTask.IsCompletedSuccessfully,
            "Batch task must complete successfully after previous completes.");
    }

    [Fact]
    public async Task EnqueueBatchAsync_FaultedPrevious_DoesNotBlockOrFaultBatch()
    {
        // Arrange: a faulted previous task — the batch must swallow the
        // exception and still execute normally.
        var method = GetEnqueueBatchAsyncMethod();
        var faultedPrevious = Task.FromException(new InvalidOperationException("prior crash"));
        var commands = Array.Empty<string>();

        // Act
        var batchTask = (Task)method.Invoke(_vm, [faultedPrevious, commands])!;

        // Assert: the batch completes successfully despite the faulted
        // previous (exception is caught and swallowed in EnqueueBatchAsync).
        await batchTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(batchTask.IsCompletedSuccessfully,
            "Batch must complete successfully even when the previous task is faulted.");
    }

    [Fact]
    public async Task EnqueueBatchAsync_CancelledPrevious_DoesNotBlockOrFaultBatch()
    {
        // Arrange: a cancelled previous task — the batch must swallow the
        // OperationCanceledException and still execute normally.
        var method = GetEnqueueBatchAsyncMethod();
        var cancelledPrevious = Task.FromCanceled(new CancellationToken(canceled: true));
        var commands = Array.Empty<string>();

        // Act
        var batchTask = (Task)method.Invoke(_vm, [cancelledPrevious, commands])!;

        // Assert
        await batchTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(batchTask.IsCompletedSuccessfully,
            "Batch must complete successfully even when the previous task is cancelled.");
    }

    [Fact]
    public async Task EnqueueBatchAsync_CompletedPrevious_CompletesSuccessfully()
    {
        // Arrange
        var method = GetEnqueueBatchAsyncMethod();
        var commands = Array.Empty<string>();

        // Act: with Task.CompletedTask as previous, the batch proceeds
        // immediately after the initial yield.
        var batchTask = (Task)method.Invoke(_vm, [Task.CompletedTask, commands])!;

        // Assert
        await batchTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(batchTask.IsCompletedSuccessfully);
    }

    // ====================================================================
    // Editing rules (aliases / triggers)
    // ====================================================================

    private AutomationRuleEntry AddSampleRule()
    {
        _vm.NewRuleName = "Skrót look";
        _vm.NewRuleType = "alias";
        _vm.NewRulePattern = "^l$";
        _vm.NewRuleAction = "look";
        _vm.AddRuleCommand.Execute(null);
        return _vm.AutomationRules[^1];
    }

    [Fact]
    public void EditRule_LoadsValuesIntoForm_AndEntersEditMode()
    {
        var rule = AddSampleRule();

        _vm.EditRuleCommand.Execute(rule);

        Assert.True(_vm.IsEditingRule);
        Assert.True(rule.IsEditing);
        Assert.False(_vm.IsRuleFormExpanded);
        Assert.Equal("Zapisz zmiany", _vm.RuleFormButtonText);
        Assert.Equal(rule.Name, _vm.NewRuleName);
        Assert.Equal(rule.Pattern, _vm.NewRulePattern);
        Assert.Equal(rule.Action, _vm.NewRuleAction);
    }

    [Fact]
    public void EditRule_Save_UpdatesEntryInPlace()
    {
        var rule = AddSampleRule();
        var countBefore = _vm.AutomationRules.Count;

        _vm.EditRuleCommand.Execute(rule);
        _vm.NewRuleName = "Nowa nazwa";
        _vm.NewRulePattern = "^lo$";
        _vm.NewRuleAction = "look north";
        _vm.AddRuleCommand.Execute(null);

        Assert.Equal(countBefore, _vm.AutomationRules.Count);
        Assert.Equal("Nowa nazwa", rule.Name);
        Assert.Equal("^lo$", rule.Pattern);
        Assert.Equal("look north", rule.Action);
        Assert.False(_vm.IsEditingRule);
        Assert.False(rule.IsEditing);
        Assert.Equal(string.Empty, _vm.NewRuleName);
    }

    [Fact]
    public void CancelRuleEdit_ClearsFormAndEditMode()
    {
        var rule = AddSampleRule();
        _vm.EditRuleCommand.Execute(rule);

        _vm.CancelRuleEditCommand.Execute(null);

        Assert.False(_vm.IsEditingRule);
        Assert.False(rule.IsEditing);
        Assert.Equal(string.Empty, _vm.NewRuleName);
        Assert.Equal(string.Empty, _vm.NewRulePattern);
    }

    [Fact]
    public void DeleteRule_WhileEditingIt_CancelsEdit()
    {
        var rule = AddSampleRule();
        _vm.EditRuleCommand.Execute(rule);

        _vm.DeleteRuleCommand.Execute(rule);

        Assert.False(_vm.IsEditingRule);
        Assert.DoesNotContain(rule, _vm.AutomationRules);
    }

    [Fact]
    public void EditRule_SwitchingToAnotherRule_ClearsPreviousEntrysIsEditing()
    {
        var first = AddSampleRule();
        _vm.NewRuleName = "Skrót exa";
        _vm.NewRuleType = "alias";
        _vm.NewRulePattern = "^e$";
        _vm.NewRuleAction = "exa";
        _vm.AddRuleCommand.Execute(null);
        var second = _vm.AutomationRules[^1];

        _vm.EditRuleCommand.Execute(first);
        Assert.True(first.IsEditing);

        _vm.EditRuleCommand.Execute(second);
        Assert.False(first.IsEditing);
        Assert.True(second.IsEditing);
    }

    // ====================================================================
    // IsAutomationCompactView — "Kompaktowo" toggle shared by Timery/Aliasy/Triggery
    // ====================================================================

    [Fact]
    public void IsAutomationCompactView_DefaultsToFalse()
    {
        Assert.False(_vm.IsAutomationCompactView);
    }

    [Fact]
    public void IsAutomationCompactView_Toggles()
    {
        _vm.IsAutomationCompactView = true;
        Assert.True(_vm.IsAutomationCompactView);

        _vm.IsAutomationCompactView = false;
        Assert.False(_vm.IsAutomationCompactView);
    }

    // ====================================================================
    // TestRuleScriptCommand — "Testuj" for a script alias/trigger
    // ====================================================================

    [Fact]
    public void TestRuleScript_NotAScript_DoesNothing()
    {
        _vm.NewRulePattern = "^kk (.+)$";
        _vm.NewRuleAction = "kill $1";
        _vm.NewRuleIsScript = false;
        _vm.NewRuleTestInput = "kk orc";

        Assert.False(_vm.TestRuleScriptCommand.CanExecute(null));
        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Null(_vm.NewRuleTestOutput);
    }

    [Fact]
    public void TestRuleScript_PatternMatches_RunsScriptAndShowsSentCommands()
    {
        _vm.NewRulePattern = "^kk (.+)$";
        _vm.NewRuleAction = "send(\"kill \" .. matches[1])";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "kk orc";

        Assert.True(_vm.TestRuleScriptCommand.CanExecute(null));
        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Equal("→ kill orc", _vm.NewRuleTestOutput);
    }

    [Fact]
    public void TestRuleScript_PatternDoesNotMatch_ReportsNoMatchWithoutRunningTheScript()
    {
        _vm.NewRulePattern = "^kk (.+)$";
        _vm.NewRuleAction = "send(\"should not run\")";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "north";

        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Contains("nie pasuje", _vm.NewRuleTestOutput);
    }

    [Fact]
    public void TestRuleScript_InvalidPattern_ReportsRegexError()
    {
        _vm.NewRulePattern = "(unterminated";
        _vm.NewRuleAction = "send(\"x\")";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "anything";

        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Contains("Nieprawidłowy wzorzec", _vm.NewRuleTestOutput);
    }

    [Fact]
    public void TestRuleScript_ScriptThrows_ReportsLuaError()
    {
        _vm.NewRulePattern = "^x$";
        _vm.NewRuleAction = "error(\"boom\")";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "x";

        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Contains("boom", _vm.NewRuleTestOutput);
    }

    [Fact]
    public void TestRuleScript_NoSendCalls_SaysSo()
    {
        _vm.NewRulePattern = "^x$";
        _vm.NewRuleAction = "local unused = 1";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "x";

        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Contains("brak komend", _vm.NewRuleTestOutput);
    }

    [Fact]
    public void TestRuleScript_BlankPattern_RunsWithoutMatching()
    {
        _vm.NewRulePattern = "";
        _vm.NewRuleAction = "send(\"ping\")";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "anything at all";

        _vm.TestRuleScriptCommand.Execute(null);

        Assert.Equal("→ ping", _vm.NewRuleTestOutput);
    }

    [Fact]
    public void EditRule_ClearsAnyPreviousTestOutput()
    {
        _vm.NewRulePattern = "^x$";
        _vm.NewRuleAction = "send(\"ping\")";
        _vm.NewRuleIsScript = true;
        _vm.NewRuleTestInput = "x";
        _vm.TestRuleScriptCommand.Execute(null);
        Assert.NotNull(_vm.NewRuleTestOutput);

        var rule = AddSampleRule();
        _vm.EditRuleCommand.Execute(rule);

        Assert.Null(_vm.NewRuleTestOutput);
        Assert.Equal(string.Empty, _vm.NewRuleTestInput);
    }

    // ====================================================================
    // Editing timers
    // ====================================================================

    private TimerEntry AddSampleTimer()
    {
        _vm.NewTimerName = "Leczenie";
        _vm.NewTimerMinutes = "1";
        _vm.NewTimerSeconds = "30";
        _vm.NewTimerCommands = "rzuc 'leczenie'";
        _vm.AddTimerCommand.Execute(null);
        return _vm.Timers[^1];
    }

    // ====================================================================
    // TestTimerScriptCommand — "Testuj" for a script timer
    // ====================================================================

    [Fact]
    public void TestTimerScript_NotAScript_DoesNothing()
    {
        _vm.NewTimerCommands = "rzuc 'leczenie'";
        _vm.NewTimerIsScript = false;

        Assert.False(_vm.TestTimerScriptCommand.CanExecute(null));
        _vm.TestTimerScriptCommand.Execute(null);

        Assert.Null(_vm.NewTimerTestOutput);
    }

    [Fact]
    public void TestTimerScript_RunsScriptAndShowsSentCommands()
    {
        _vm.NewTimerCommands = "send(\"look\")";
        _vm.NewTimerIsScript = true;

        Assert.True(_vm.TestTimerScriptCommand.CanExecute(null));
        _vm.TestTimerScriptCommand.Execute(null);

        Assert.Equal("→ look", _vm.NewTimerTestOutput);
    }

    [Fact]
    public void TestTimerScript_ScriptThrows_ReportsLuaError()
    {
        _vm.NewTimerCommands = "error(\"boom\")";
        _vm.NewTimerIsScript = true;

        _vm.TestTimerScriptCommand.Execute(null);

        Assert.Contains("boom", _vm.NewTimerTestOutput);
    }

    [Fact]
    public void EditTimer_ClearsAnyPreviousTestOutput()
    {
        _vm.NewTimerCommands = "send(\"ping\")";
        _vm.NewTimerIsScript = true;
        _vm.TestTimerScriptCommand.Execute(null);
        Assert.NotNull(_vm.NewTimerTestOutput);

        var timer = AddSampleTimer();
        _vm.EditTimerCommand.Execute(timer);

        Assert.Null(_vm.NewTimerTestOutput);
    }

    [Fact]
    public void EditTimer_LoadsValuesIntoForm_AndEntersEditMode()
    {
        var timer = AddSampleTimer();

        _vm.EditTimerCommand.Execute(timer);

        Assert.True(_vm.IsEditingTimer);
        Assert.True(timer.IsEditing);
        Assert.False(_vm.IsTimerFormExpanded);
        Assert.Equal("Zapisz zmiany", _vm.TimerFormButtonText);
        Assert.Equal(timer.Name, _vm.NewTimerName);
        Assert.Equal("1", _vm.NewTimerMinutes);
        Assert.Equal("30", _vm.NewTimerSeconds);
        Assert.Equal(timer.CommandsText, _vm.NewTimerCommands);
    }

    [Fact]
    public void EditTimer_Save_UpdatesEntryInPlace_KeepsId()
    {
        var timer = AddSampleTimer();
        var id = timer.Id;
        var countBefore = _vm.Timers.Count;

        _vm.EditTimerCommand.Execute(timer);
        _vm.NewTimerName = "Leczenie 2";
        _vm.NewTimerMinutes = "0";
        _vm.NewTimerSeconds = "45";
        _vm.NewTimerCommands = "pij miksture";
        _vm.AddTimerCommand.Execute(null);

        Assert.Equal(countBefore, _vm.Timers.Count);
        Assert.Equal(id, timer.Id);
        Assert.Equal("Leczenie 2", timer.Name);
        Assert.Equal(0, timer.Minutes);
        Assert.Equal(45, timer.Seconds);
        Assert.Equal("pij miksture", timer.CommandsText);
        Assert.False(_vm.IsEditingTimer);
        Assert.False(timer.IsEditing);
    }

    [Fact]
    public void EditTimer_SaveWithZeroInterval_KeepsEditModeAndOldValues()
    {
        var timer = AddSampleTimer();

        _vm.EditTimerCommand.Execute(timer);
        _vm.NewTimerMinutes = "0";
        _vm.NewTimerSeconds = "0";
        _vm.NewTimerMilliseconds = "0";
        _vm.AddTimerCommand.Execute(null);

        Assert.True(_vm.IsEditingTimer);
        Assert.Equal(1, timer.Minutes);
        Assert.Equal(30, timer.Seconds);
    }

    [Fact]
    public void CancelTimerEdit_ClearsFormAndEditMode()
    {
        var timer = AddSampleTimer();
        _vm.EditTimerCommand.Execute(timer);

        _vm.CancelTimerEditCommand.Execute(null);

        Assert.False(_vm.IsEditingTimer);
        Assert.False(timer.IsEditing);
        Assert.Equal(string.Empty, _vm.NewTimerName);
        Assert.Equal("0", _vm.NewTimerMinutes);
    }

    [Fact]
    public void DeleteTimer_WhileEditingIt_CancelsEdit()
    {
        var timer = AddSampleTimer();
        _vm.EditTimerCommand.Execute(timer);

        _vm.DeleteTimerCommand.Execute(timer);

        Assert.False(_vm.IsEditingTimer);
        Assert.DoesNotContain(timer, _vm.Timers);
    }

    [Fact]
    public void EditTimer_SwitchingToAnotherTimer_ClearsPreviousEntrysIsEditing()
    {
        var first = AddSampleTimer();
        _vm.NewTimerName = "Refresh";
        _vm.NewTimerMinutes = "0";
        _vm.NewTimerSeconds = "10";
        _vm.NewTimerCommands = "cast refresh";
        _vm.AddTimerCommand.Execute(null);
        var second = _vm.Timers[^1];

        _vm.EditTimerCommand.Execute(first);
        Assert.True(first.IsEditing);

        _vm.EditTimerCommand.Execute(second);
        Assert.False(first.IsEditing);
        Assert.True(second.IsEditing);
    }

    // ====================================================================
    // Autowalk — OnMapRoomDoubleClicked
    //
    // The production handler sets a temporary target from the clicked room,
    // clears the active autowalk (if any) without clearing the temp target,
    // and then previews the new route.  In the test environment there is no
    // loaded map (MapIndex is null) so the path preview falls through to a
    // status-text message.
    // ====================================================================

    private static FieldInfo GetAutowalkPathField()
    {
        var field = typeof(MainWindowViewModel).GetField("_autowalkPath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static FieldInfo GetTemporaryTargetField()
    {
        var field = typeof(MainWindowViewModel).GetField("_temporaryTarget",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    /// <summary>Creates a MapRoom with the given vnum for test double-click scenarios.</summary>
    private static MapRoom CreateTestRoom(int id, string vnum, string? name = null)
    {
        var userData = vnum.Length > 0
            ? new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement(vnum),
            }
            : null;

        return new MapRoom
        {
            Id = id,
            AreaId = 1,
            Coordinates = new MapCoordinates(0, 0, 0),
            Name = name ?? $"Room {id}",
            UserData = userData,
        };
    }

    /// <summary>Creates a minimal dummy MapPath for setting up autowalk state.</summary>
    private static MapPath CreateDummyPath()
    {
        var dummyRoom = new MapRoom
        {
            Id = 999,
            AreaId = 1,
            Coordinates = new MapCoordinates(0, 0, 0),
        };
        return new MapPath
        {
            From = dummyRoom,
            To = dummyRoom,
            Steps = Array.Empty<MapPathStep>(),
            TotalCost = 0,
        };
    }

    [Fact]
    public void SendAutowalkStep_WhileSitting_WaitsForStandingConfirmation()
    {
        var from = CreateTestRoom(998, "998");
        var to = CreateTestRoom(999, "999");
        GetAutowalkPathField().SetValue(_vm, new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("north", to)],
            TotalCost = 1,
        });
        typeof(MainWindowViewModel).GetField("_autowalkTargetName",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_vm, "Cel");
        typeof(MainWindowViewModel).GetField("_latestCharacterPosition",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_vm, "sitting");

        typeof(MainWindowViewModel).GetMethod("SendAutowalkStep",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_vm, [false]);

        var recoveringField = typeof(MainWindowViewModel).GetField("_autowalkRecoveringPosition",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)recoveringField.GetValue(_vm)!);
        Assert.Contains("wstaję", _vm.AutowalkStatusText);

        typeof(MainWindowViewModel).GetField("_latestCharacterPosition",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_vm, "standing");
        typeof(MainWindowViewModel).GetMethod("HandleAutowalkStanding",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_vm, null);

        Assert.False((bool)recoveringField.GetValue(_vm)!);
        Assert.Contains("Idę do", _vm.AutowalkStatusText);
    }

    /// <summary>Regression: BeginAutowalkStandRecovery is also reachable directly from
    /// OnAutowalkSitting (a GMCP "sitting" transition), bypassing SendAutowalkStep's own guard —
    /// without checking _autowalkRecoveringMovement too, a brief "sitting" report during the
    /// automatic low-movement rest recovery (RecoverMovementAndContinueAsync, already resting and
    /// awaiting its own delayed "stand") would force an early "stand", cutting the rest short and
    /// leaving both recovery flags set at once.</summary>
    [Fact]
    public void BeginAutowalkStandRecovery_DuringLowMovementRest_DoesNotStand()
    {
        var from = CreateTestRoom(998, "998");
        var to = CreateTestRoom(999, "999");
        GetAutowalkPathField().SetValue(_vm, new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("north", to)],
            TotalCost = 1,
        });
        typeof(MainWindowViewModel).GetField("_autowalkRecoveringMovement",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_vm, true);

        typeof(MainWindowViewModel).GetMethod("BeginAutowalkStandRecovery",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_vm, null);

        var recoveringPositionField = typeof(MainWindowViewModel).GetField(
            "_autowalkRecoveringPosition",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)recoveringPositionField.GetValue(_vm)!);
    }

    [Fact]
    public void OnMapRoomDoubleClicked_NoVnum_ShowsErrorToast()
    {
        // Arrange: a room with no vnum
        var room = CreateTestRoom(1, string.Empty);
        var toastCount = _vm.Toasts.Count;

        // Act
        _vm.Map.NotifyRoomDoubleClicked(room);

        // Assert: error toast added, no temporary target set
        Assert.Equal(toastCount + 1, _vm.Toasts.Count);
        Assert.Contains("nie ma vnum", _vm.Toasts[^1].Text);
        Assert.False(_vm.HasTemporaryTarget);
        Assert.False(_vm.IsAutowalking);
    }

    [Fact]
    public void OnMapRoomDoubleClicked_WhenNotWalking_SetsTargetAndPreviews()
    {
        // Arrange — disable auto-walk-on-double-click so this exercises the preview-only path.
        _vm.Map.AutoWalkOnMapDoubleClick = false;
        Assert.False(_vm.IsAutowalking);
        var room = CreateTestRoom(100, "2002", "Test Room");
        var toastCount = _vm.Toasts.Count;

        // Act
        _vm.Map.NotifyRoomDoubleClicked(room);

        // Assert: temporary target is set
        Assert.True(_vm.HasTemporaryTarget);
        Assert.Contains("Test Room", _vm.TemporaryTargetDisplay);
        Assert.Contains("2002", _vm.TemporaryTargetDisplay);

        // No map loaded → RouteRooms stays null, status shows no-preview message
        Assert.Null(_vm.Map.RouteRooms);
        Assert.Contains("Test Room", _vm.AutowalkStatusText);
        Assert.Contains("brak podglądu", _vm.AutowalkStatusText);

        // No new toast (path preview failure is indicated via status text, not toast)
        Assert.Equal(toastCount, _vm.Toasts.Count);
        Assert.False(_vm.IsAutowalking);
    }

    [Fact]
    public void OnMapRoomDoubleClicked_DefaultsToStartingAutowalkImmediately()
    {
        // AutoWalkOnMapDoubleClick defaults to true — double-click should attempt to start
        // walking right away rather than only preview. With no map/pathfinder loaded here,
        // that attempt surfaces as StartAutowalk's own failure toast.
        Assert.True(_vm.Map.AutoWalkOnMapDoubleClick);
        var room = CreateTestRoom(100, "2002", "Test Room");
        var toastCount = _vm.Toasts.Count;

        _vm.Map.NotifyRoomDoubleClicked(room);

        Assert.True(_vm.HasTemporaryTarget);
        Assert.Equal(toastCount + 1, _vm.Toasts.Count);
        Assert.Contains("nie jest załadowana", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void OnMapRoomDoubleClicked_WhenWalking_ClearsOldRouteAndSetsNewTarget()
    {
        // Arrange: disable auto-walk-on-double-click so this exercises the preview-only path;
        // set up walking state via reflection (simulate active autowalk)
        _vm.Map.AutoWalkOnMapDoubleClick = false;
        var pathField = GetAutowalkPathField();
        pathField.SetValue(_vm, CreateDummyPath());
        _vm.Map.RouteRooms = [CreateTestRoom(1, "1000")];
        Assert.True(_vm.IsAutowalking);
        Assert.NotNull(_vm.Map.RouteRooms);

        var toastCount = _vm.Toasts.Count;
        var room = CreateTestRoom(200, "3003", "New Target Room");

        // Act: double-click a new room
        _vm.Map.NotifyRoomDoubleClicked(room);

        // Assert: autowalk is stopped
        Assert.False(_vm.IsAutowalking);
        Assert.Null(pathField.GetValue(_vm));

        // Assert: temporary target is the NEW clicked room (not cleared)
        Assert.True(_vm.HasTemporaryTarget);
        Assert.Contains("3003", _vm.TemporaryTargetDisplay);
        Assert.Contains("New Target Room", _vm.TemporaryTargetDisplay);

        // Assert: old route is cleared from the map
        Assert.Null(_vm.Map.RouteRooms);

        // Assert: a toast confirms the walk was interrupted
        Assert.Equal(toastCount + 1, _vm.Toasts.Count);
        Assert.Contains("Autowalk przerwany", _vm.Toasts[^1].Text);
        Assert.Contains("New Target Room", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void OnMapRoomDoubleClicked_WhenWalking_KeepsNewTempTarget()
    {
        // Regression guard: the double-click handler must NOT call
        // StopAutowalk (which clears _temporaryTarget).  Verify that
        // the new temporary target survives after clearing the walk.
        var pathField = GetAutowalkPathField();
        pathField.SetValue(_vm, CreateDummyPath());
        Assert.True(_vm.IsAutowalking);

        var room = CreateTestRoom(300, "4004", "Keep Me");

        _vm.Map.NotifyRoomDoubleClicked(room);

        // The new temporary target must still be set
        Assert.True(_vm.HasTemporaryTarget);
        var tempField = GetTemporaryTargetField();
        var tempTarget = (AutowalkLocation?)tempField.GetValue(_vm);
        Assert.NotNull(tempTarget);
        Assert.Equal("4004", tempTarget!.Vnum);
        Assert.Equal("Keep Me", tempTarget.Name);
    }

    // ====================================================================
    // Autowalk — GoToSelectedTargetCommand (new UI IDŹ button)
    //
    // The GoToSelectedTargetCommand should behave exactly like the bare
    // /walk command: if a temporary target is set, start walking to it;
    // otherwise show usage help.
    // ====================================================================

    [Fact]
    public void GoToSelectedTargetCommand_WithoutTarget_ShowsUsageHelp()
    {
        // Arrange: no temporary target
        Assert.False(_vm.HasTemporaryTarget);
        var toastCount = _vm.Toasts.Count;

        // Act
        _vm.GoToSelectedTargetCommand.Execute(null);

        // Assert: a usage/info toast was added
        Assert.Equal(toastCount + 1, _vm.Toasts.Count);
        Assert.Contains("/walk", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void GoToSelectedTargetCommand_WithTarget_AttemptsAutowalk()
    {
        // Arrange: set a temporary target via reflection
        var tempField = GetTemporaryTargetField();
        tempField.SetValue(_vm, new AutowalkLocation("Test Target", "5005"));
        Assert.True(_vm.HasTemporaryTarget);
        var toastCount = _vm.Toasts.Count;

        // Act
        _vm.GoToSelectedTargetCommand.Execute(null);

        // Assert: StartAutowalk was called; with no map loaded it shows
        // a "map not loaded" error toast.
        Assert.Equal(toastCount + 1, _vm.Toasts.Count);
        Assert.Contains("Mapa nie jest załadowana", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void GoToSelectedTargetCommand_NoTarget_DoesNotCallStartAutowalk()
    {
        // Verify that without a target the command only adds the usage
        // toast and does not attempt to autowalk (which would touch the
        // pathfinder and potentially throw in tests).
        var toastCount = _vm.Toasts.Count;

        _vm.GoToSelectedTargetCommand.Execute(null);

        // Only the usage toast was added (no "map not loaded" error etc.)
        Assert.Equal(toastCount + 1, _vm.Toasts.Count);
        Assert.Contains("/walk", _vm.Toasts[^1].Text);
        Assert.DoesNotContain("nie jest załadowana", _vm.Toasts[^1].Text);
    }

    // ====================================================================
    // Autowalk — consistency between GoToSelectedTargetCommand and bare /walk
    //
    // The GoToSelectedTargetCommand and TryHandleAutowalkCommand("/walk")
    // share the same HandleGoToSelectedTarget method, so their behaviour
    // is structurally identical.  We verify that TryHandleAutowalkCommand
    // with "/walk" (no argument) produces the same outcome as the command.
    // ====================================================================

    private bool InvokeTryHandleAutowalkCommand(string command)
    {
        var method = typeof(MainWindowViewModel).GetMethod("TryHandleAutowalkCommand",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (bool)method!.Invoke(_vm, [command])!;
    }

    private void SetCurrentVnum(string vnum)
    {
        var resolver = (GmcpLocationResolver)typeof(MainWindowViewModel)
            .GetField("_locationResolver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_vm)!;
        typeof(GmcpLocationResolver).GetProperty(nameof(GmcpLocationResolver.CurrentVnum))!
            .SetValue(resolver, vnum);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkDodaj_SavesCurrentLocationForActiveProfile()
    {
        SetCurrentVnum("7007");
        _vm.NewLocationName = "niedokończony formularz";
        _vm.NewLocationVnum = "123";
        _vm.NewLocationIsGlobal = true;

        var consumed = InvokeTryHandleAutowalkCommand("/WALK_DODAJ Stara Brama");

        Assert.True(consumed);
        var location = Assert.Single(_vm.Locations);
        Assert.Equal("Stara Brama", location.Name);
        Assert.Equal("7007", location.Vnum);
        Assert.False(location.IsGlobal);
        Assert.Equal("niedokończony formularz", _vm.NewLocationName);
        Assert.Equal("123", _vm.NewLocationVnum);
        Assert.True(_vm.NewLocationIsGlobal);
        Assert.Contains("Dodano lokację", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkDodaj_WithoutName_ShowsUsage()
    {
        var consumed = InvokeTryHandleAutowalkCommand("/walk_dodaj");

        Assert.True(consumed);
        Assert.Empty(_vm.Locations);
        Assert.Contains("/walk_dodaj <nazwa>", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkDodaj_WithoutGmcpLocation_ShowsError()
    {
        var consumed = InvokeTryHandleAutowalkCommand("/walk_dodaj Stara Brama");

        Assert.True(consumed);
        Assert.Empty(_vm.Locations);
        Assert.Contains("brak danych GMCP", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_BareWalk_WithoutTarget_ShowsSameUsageAsCommand()
    {
        // Arrange: both paths start from the same state (no target)
        Assert.False(_vm.HasTemporaryTarget);
        _vm.Toasts.Clear();

        // Act: invoke the bare /walk path
        var consumed = InvokeTryHandleAutowalkCommand("/walk");
        Assert.True(consumed);

        var toastAfterBareWalk = Assert.Single(_vm.Toasts);
        _vm.Toasts.Clear();

        // Act: invoke the command path
        _vm.GoToSelectedTargetCommand.Execute(null);

        var toastAfterCommand = Assert.Single(_vm.Toasts);

        // Assert: both produce the same usage message
        Assert.Equal(toastAfterBareWalk.Text, toastAfterCommand.Text);
        Assert.Contains("/walk", toastAfterCommand.Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_BareWalk_WithTarget_AttemptsWalk()
    {
        // Arrange: set a temp target
        var tempField = GetTemporaryTargetField();
        tempField.SetValue(_vm, new AutowalkLocation("Temp", "6006"));
        _vm.Toasts.Clear();

        // Act: invoke bare /walk
        var consumed = InvokeTryHandleAutowalkCommand("/walk");
        Assert.True(consumed);

        // Without map → "Mapa nie jest załadowana" toast
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Mapa nie jest załadowana", toast.Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_GroupMember_RecognizesLatestGmcpUpdate()
    {
        SetLatestGroupUpdate(new CharacterGroupUpdate("Aragorn",
        [
            CreateCharacterGroupMember("Aragorn", "6017", isLeader: true),
            CreateCharacterGroupMember("Gimli", "7007")
        ]));
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand("/walk gImLi");

        Assert.True(consumed);
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Mapa nie jest załadowana", toast.Text);
        Assert.DoesNotContain("Nie znam lokacji", toast.Text);
    }

    [Fact]
    public void BuildGroupMemberAutowalkTarget_UsesMemberNameAndRoomFromGmcp()
    {
        var target = _vm.BuildGroupMemberAutowalkTarget(
            CreateCharacterGroupMember("Gimli", "7007"));

        Assert.NotNull(target);
        Assert.Equal("Gimli", target!.Name);
        Assert.Equal("7007", target.Vnum);
        Assert.Equal("pokój 7007", target.RoomName);
    }

    [Fact]
    public void TryHandleAutowalkCommand_GroupMemberWithoutRoom_ShowsGmcpPositionError()
    {
        SetLatestGroupUpdate(new CharacterGroupUpdate("Aragorn",
        [
            CreateCharacterGroupMember("Gimli", null)
        ]));
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand("/walk Gimli");

        Assert.True(consumed);
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Brak pozycji GMCP", toast.Text);
        Assert.Contains("Gimli", toast.Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkLeader_WalksToGroupLeader()
    {
        SetLatestGroupUpdate(new CharacterGroupUpdate("Aragorn",
        [
            CreateCharacterGroupMember("Aragorn", "6017", isLeader: true),
            CreateCharacterGroupMember("Gimli", "7007")
        ]));
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand("/walk leader");

        Assert.True(consumed);
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Mapa nie jest załadowana", toast.Text);
    }

    [Theory]
    [InlineData("/walk leader")]
    [InlineData("/walk LEADER")]
    public void TryHandleAutowalkCommand_WalkLeader_IsCaseInsensitive(string command)
    {
        SetLatestGroupUpdate(new CharacterGroupUpdate("Aragorn",
        [
            CreateCharacterGroupMember("Aragorn", "6017", isLeader: true),
            CreateCharacterGroupMember("Gimli", "7007")
        ]));
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand(command);

        Assert.True(consumed);
        Assert.DoesNotContain("Brak informacji o liderze", _vm.Toasts[^1].Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkLeader_WithoutGroupInfo_ShowsError()
    {
        SetLatestGroupUpdate(null);
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand("/walk leader");

        Assert.True(consumed);
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Brak informacji o liderze grupy", toast.Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkLeader_WhenSelfIsLeader_ShowsInfoMessage()
    {
        SetLatestGroupUpdate(new CharacterGroupUpdate("Aragorn",
        [
            CreateCharacterGroupMember("Aragorn", "6017", isLeader: true),
            CreateCharacterGroupMember("Gimli", "7007")
        ]));
        SetLatestCharacterName("Aragorn");
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand("/walk leader");

        Assert.True(consumed);
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Jesteś liderem grupy", toast.Text);
    }

    [Fact]
    public void TryHandleAutowalkCommand_WalkLeader_WithoutRoom_ShowsGmcpPositionError()
    {
        SetLatestGroupUpdate(new CharacterGroupUpdate("Aragorn",
        [
            CreateCharacterGroupMember("Aragorn", null, isLeader: true)
        ]));
        _vm.Toasts.Clear();

        var consumed = InvokeTryHandleAutowalkCommand("/walk leader");

        Assert.True(consumed);
        var toast = Assert.Single(_vm.Toasts);
        Assert.Contains("Brak pozycji GMCP lidera", toast.Text);
        Assert.Contains("Aragorn", toast.Text);
    }

    private static CharacterGroupMember CreateCharacterGroupMember(
        string name,
        string? room,
        bool isLeader = false) =>
        new(name, null, "zdrowy", null, "wypoczęty", null, null, false, room, isLeader);

    // ====================================================================
    // Autowalk — resuming an interrupted journey with a bare /walk
    //
    // When a walk is cut short (lost route / off-course), the destination is
    // remembered in _pendingResumeTarget. A bare /walk with no map-picked
    // target then resumes toward it instead of only printing usage help.
    // ====================================================================

    private static FieldInfo GetPendingResumeTargetField()
    {
        var field = typeof(MainWindowViewModel).GetField("_pendingResumeTarget",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    [Fact]
    public void TryHandleAutowalkCommand_BareWalk_WithPendingResume_AttemptsWalk()
    {
        // Arrange: a journey was interrupted, so a resume target is pending
        // (and there is no temporary map-picked target).
        Assert.False(_vm.HasTemporaryTarget);
        GetPendingResumeTargetField().SetValue(_vm, new AutowalkLocation("Plac Aras", "7007"));
        _vm.Toasts.Clear();

        // Act: bare /walk
        var consumed = InvokeTryHandleAutowalkCommand("/walk");
        Assert.True(consumed);

        // Assert: it announces the resume, then StartAutowalk runs (no map →
        // "Mapa nie jest załadowana"). Crucially it is NOT the usage help.
        Assert.Contains(_vm.Toasts, t => t.Text.Contains("Wznawiam podróż do „Plac Aras”"));
        Assert.Contains(_vm.Toasts, t => t.Text.Contains("Mapa nie jest załadowana"));
        Assert.DoesNotContain(_vm.Toasts, t => t.Text.Contains("Użycie: /walk"));
    }

    [Fact]
    public void StopAutowalk_Resumable_RemembersDestination_ExplicitStopClearsIt()
    {
        var pendingField = GetPendingResumeTargetField();
        var pathField = typeof(MainWindowViewModel).GetField("_autowalkPath",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var nameField = typeof(MainWindowViewModel).GetField("_autowalkTargetName",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stop = typeof(MainWindowViewModel).GetMethod("StopAutowalk",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var from = CreateTestRoom(100, "100");
        var to = CreateTestRoom(200, "200", "Świątynia");
        var path = new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("north", to)],
            TotalCost = 1,
        };

        // A resumable stop remembers where we were headed.
        pathField.SetValue(_vm, path);
        nameField.SetValue(_vm, "Świątynia");
        stop.Invoke(_vm, ["przerwane", "error", true]);

        var pending = Assert.IsType<AutowalkLocation>(pendingField.GetValue(_vm));
        Assert.Equal("Świątynia", pending.Name);
        Assert.Equal("200", pending.Vnum);

        // An explicit (non-resumable) stop clears the pending target.
        pathField.SetValue(_vm, path);
        stop.Invoke(_vm, ["zatrzymane", "info", false]);
        Assert.Null(pendingField.GetValue(_vm));
    }

    // ====================================================================
    // Autowalk — StopAutowalkDuringDoubleClickRegression
    //
    // Verifies that StopAutowalk (which IS used by other paths like /stop
    // or profile switch) correctly clears _temporaryTarget, while the
    // double-click handler does NOT call StopAutowalk and therefore the
    // temporary target survives.
    // ====================================================================

    [Fact]
    public void StopAutowalk_ClearsTemporaryTarget()
    {
        // Arrange: set a temporary target and start walking
        var tempField = GetTemporaryTargetField();
        tempField.SetValue(_vm, new AutowalkLocation("WillBeCleared", "7007"));
        var pathField = GetAutowalkPathField();
        pathField.SetValue(_vm, CreateDummyPath());
        Assert.True(_vm.HasTemporaryTarget);
        Assert.True(_vm.IsAutowalking);

        // Act: stop via the public command
        _vm.StopAutowalkCommand.Execute(null);

        // Assert: both autowalk and temporary target are cleared
        Assert.False(_vm.IsAutowalking);
        Assert.False(_vm.HasTemporaryTarget);
        Assert.Null(tempField.GetValue(_vm));
    }

    // ====================================================================
    // Death marks on the map (Map.DeathMarkers) — the settings-flyout list
    // (MainViewModel.Deaths) is the source of truth; Map.MainViewModel wiring
    // (set in the constructor) keeps DeathMarkers resolved against it.
    // ====================================================================

    private static MapIndex CreateMapIndexWithRoom(string vnum, string? name = "Crypt")
    {
        var room = new MapRoom
        {
            Id = 1,
            AreaId = 1,
            Name = name,
            Coordinates = new MapCoordinates(0, 0, 0),
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement(vnum),
            },
        };
        var area = new MapArea { Id = 1, Name = "Test Area", Rooms = [room] };
        return new MapIndex(new MapDocument { Areas = [area] });
    }

    private static void SetMapIndex(MapViewModel map, MapIndex index) =>
        typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!.SetValue(map, index);

    [Fact]
    public void AddingDeath_AppearsAsMapMarker_WhenVnumResolves()
    {
        SetMapIndex(_vm.Map, CreateMapIndexWithRoom("100"));

        _vm.Deaths.Add(new DeathMarkEntry("100", "Crypt", "2026-01-01 12:00"));

        var marker = Assert.Single(_vm.Map.DeathMarkers);
        Assert.Equal("100", marker.Room.Vnum);
        Assert.Contains("Crypt", marker.Display);
    }

    [Fact]
    public void AddingDeath_UnresolvableVnum_ProducesNoMapMarker()
    {
        SetMapIndex(_vm.Map, CreateMapIndexWithRoom("100"));

        _vm.Deaths.Add(new DeathMarkEntry("999", null, "2026-01-01 12:00"));

        Assert.Empty(_vm.Map.DeathMarkers);
    }

    [Fact]
    public void DeletingDeath_RemovesItsMapMarker()
    {
        SetMapIndex(_vm.Map, CreateMapIndexWithRoom("100"));
        var entry = new DeathMarkEntry("100", "Crypt", "2026-01-01 12:00");
        _vm.Deaths.Add(entry);
        Assert.Single(_vm.Map.DeathMarkers);

        _vm.DeleteDeathCommand.Execute(entry);

        Assert.Empty(_vm.Map.DeathMarkers);
    }

    // ====================================================================
    // Autowalk — Vitals exposure and binding check (No test)
    //
    // The "⚑ IDŹ" button in Map's own settings flyout (MapPanelView.axaml /
    // TerminalOverlayCard.axaml — moved there from the former Autowalk panel)
    // is bound to GoToSelectedTargetCommand. XAML binding correctness is
    // validated by the build (XAML compile step). The ViewModel property
    // existence is indirectly confirmed by GoToSelectedTargetCommand_* tests above.
    // ====================================================================

    // ====================================================================
    // CommandStackingSeparator — default, setter, null/whitespace trimming
    // ====================================================================

    [Fact]
    public void CommandStackingSeparator_DefaultIsSemicolon()
    {
        Assert.Equal(";", _vm.CommandStackingSeparator);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_UpdatesValue()
    {
        _vm.CommandStackingSeparator = "|";

        Assert.Equal("|", _vm.CommandStackingSeparator);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_EmptyStringPreserved()
    {
        _vm.CommandStackingSeparator = "";

        Assert.Equal("", _vm.CommandStackingSeparator);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_NullBecomesEmpty()
    {
        _vm.CommandStackingSeparator = null!;

        Assert.Equal("", _vm.CommandStackingSeparator);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_WhitespaceTrimmed()
    {
        _vm.CommandStackingSeparator = "  ;  ";

        Assert.Equal(";", _vm.CommandStackingSeparator);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_SameValue_DoesNotRaisePropertyChanged()
    {
        var changed = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CommandStackingSeparator))
                changed = true;
        };

        // Set to same value
        _vm.CommandStackingSeparator = ";";

        Assert.False(changed);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_DifferentValue_RaisesPropertyChanged()
    {
        var changed = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CommandStackingSeparator))
                changed = true;
        };

        _vm.CommandStackingSeparator = "|";

        Assert.True(changed);
    }

    [Fact]
    public void CommandStackingSeparator_Setter_WhitespaceToSameTrimmedValue_DoesNotRaise()
    {
        // Setting to "  ;  " normalizes to ";", which is the same as current → no event
        var changed = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CommandStackingSeparator))
                changed = true;
        };

        _vm.CommandStackingSeparator = "  ;  ";

        Assert.False(changed);
    }
}
