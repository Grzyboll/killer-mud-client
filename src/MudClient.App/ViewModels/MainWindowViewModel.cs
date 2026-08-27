using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dock.Model.Controls;
using MudClient.App.Controls;
using MudClient.App.Docking;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.Core.Automation;
using MudClient.Core.Character;
using MudClient.Core.Combat;
using MudClient.Core.Gmcp;
using MudClient.Core.Killeropedia;
using MudClient.Core.Map;
using MudClient.Core.Networking;
using MudClient.Core.Text;

namespace MudClient.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly Uri DiscordInviteUri = new("https://discord.gg/6NRnxZeMTC");
    private static readonly Uri DiscussionsUri = new("https://github.com/Grzyboll/killer-mud-client/discussions");
    private static readonly Uri AuthorPageUri = new("https://grzyboll.github.io/killer-mud-client/");

    private readonly MudSession _session = new();
    private readonly AliasEngine _aliases = new();
    private readonly TriggerEngine _triggers;
    // Shared Lua environment for "script" aliases/triggers/timers — one instance for the whole
    // session, reset per-profile (see ActivateProfile) so a script's plain Lua globals persist
    // across every firing of that character's session, but never leak into another character's.
    // See LuaScriptEngine's own doc comment and BuildLuaGameState/OnLuaScriptError below.
    private readonly LuaScriptEngine _lua = new();
    private string _luaLibrarySource = string.Empty;
    private readonly MudTimerService _timers = new();
    private BookCatalogStore _bookCatalogStore;
    private readonly bool _usesCustomBookCatalogStore;
    private readonly BookCatalogRefreshCoordinator _bookCatalogRefreshCoordinator;
    private RareCatalogStore _rareCatalogStore;
    private readonly bool _usesCustomRareCatalogStore;
    private readonly RareCatalogRefreshCoordinator _rareCatalogRefreshCoordinator;
    private readonly AbilityCaptureStore _abilityCaptureStore;
    private readonly AbilityMappingCoordinator _abilityMappingCoordinator;
    private readonly ArtifactTryStore _artifactTryStore;
    private readonly ArtifactTryMappingCoordinator _artifactTryMappingCoordinator;
    private readonly GroupSpellStore _groupSpellStore;
    private readonly GmcpLocationResolver _locationResolver = new();
    private readonly RoomExitsResolver _roomExits = new();
    private readonly RoomSnapshotResolver _roomSnapshots = new();
    private readonly CharacterStateResolver _characterState = new();
    private readonly WorldStateResolver _worldState = new();
    private readonly SkillTimeoutResolver _skillTimeouts = new();
    private readonly Dictionary<string, bool> _lastSkillTimeouts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Backs <see cref="HealthRecoveryPolicy.MinCombatHealCastInterval"/>'s floor in
    /// <see cref="TryAutoFarmCombatHeal"/> — see that policy method's xmldoc for why it exists.</summary>
    private DateTimeOffset? _lastAutoFarmCombatHealCastAt;
    private readonly AutoAssistPolicy _autoAssist = new();
    private readonly GroupExhaustionRefreshPolicy _groupExhaustionRefresh = new();
    private readonly ProfileService _profiles;

    private readonly SemaphoreSlim _triggerSendLock = new(1, 1);
    private CancellationTokenSource _triggerCts = new();

    // Tracks fire-and-forget trigger-batch tasks so they can be safely
    // drained during DisposeAsync, preventing unobserved exceptions
    // and ensuring no task holds _triggerSendLock when it is disposed.
    private readonly object _triggerTasksLock = new();
    private readonly List<Task> _triggerTasks = new();

    /// <summary>
    /// Tail of the FIFO task chain that guarantees trigger batches are
    /// sent in receive order.  Each new batch created by
    /// <c>OnLineReceived</c> awaits this task (swallowing its faults)
    /// before sending its own commands.  Read and written under
    /// <see cref="_triggerTasksLock"/>.
    /// </summary>
    private Task _triggerQueueTail = Task.CompletedTask;

    /// <summary>
    /// When false, new trigger tasks are rejected.  Set and read under
    /// <see cref="_triggerTasksLock"/> to make task acceptance atomic with
    /// disposal, preventing the shutdown race where <c>DisposeAsync</c>
    /// drains an empty list and disposes the semaphore before
    /// <c>OnLineReceived</c> registers a task that will later touch it.
    /// </summary>
    private bool _acceptingTriggerTasks = true;

    private CharacterGroupUpdate? _latestGroupUpdate;
    private bool _isGroupContextMenuOpen;
    private IReadOnlyList<RoomPerson> _latestRoomPeople = [];
    private string? _latestCharacterName;
    private string? _latestCharacterPosition;
    private bool _autoAssistNpcPending;
    private bool _autoKillPending;
    /// <summary>Bumped on every room entry (see <see cref="OnRoomEnterAutomations"/>) — lets
    /// <see cref="TryAutoKillIfConfirmed"/> tell a genuinely fresh <see cref="_latestRoomPeople"/>
    /// snapshot (stamped into <see cref="_autoKillRoomPeopleGeneration"/> by
    /// <see cref="OnRoomPeopleChanged"/>) apart from one that still reflects the room just left.</summary>
    private int _roomEntryGeneration;
    private int _autoKillRoomPeopleGeneration = -1;

    /// <summary>Carries a line's text across chunk boundaries for <see cref="AnnotateDamageLines"/>,
    /// mirroring MudSession's own internal line accumulator.</summary>
    private string _pendingDamageLine = string.Empty;

    /// <summary>Same role as <see cref="_pendingDamageLine"/>, for <see cref="AnnotateScoreLines"/>.</summary>
    private string _pendingScoreLine = string.Empty;

    private readonly AsyncRelayCommand _connectCommand;
    private readonly AsyncRelayCommand _disconnectCommand;
    private readonly AsyncRelayCommand _sendCommandCommand;
    private readonly AsyncRelayCommand _retryStartupCommand;
    private readonly IContentUpdateService _contentUpdateService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IExternalLinkService _externalLinkService;
    private CancellationTokenSource? _contentUpdateCts;
    private Task? _contentUpdateCheckTask;
    private CancellationTokenSource? _appUpdateCts;
    private Task? _appUpdateCheckTask;

    private MudDockFactory _dockFactory;
    private readonly DockLayoutService _dockLayoutService;
    private readonly LayoutPresetService _layoutPresetService;
    private readonly List<LayoutPreset> _layoutPresets;
    private IRootDock _layout = null!;
    private string _newLayoutName = string.Empty;

    private string _host = "killer-mud.pl";
    private int _port = 4004;
    private string _encoding = MudTextEncodings.Auto;
    private string _commandText = string.Empty;
    private string _statusText = "Rozłączono";
    private string? _lastReportedMapEditorStatus;
    private string _idleTimeText = "Idle: —";
    private long _lastCommandSentTimestamp;
    private bool _isConnected;
    private bool _isBusy;
    private string? _startupErrorMessage;
    private string? _startupErrorDetails;
    private bool _isKilleropediaOpen;
    private bool _isHelpOpen;
    private ContentUpdateAvailability? _availableContentUpdate;
    private string _contentUpdateStatus = "Dane wbudowane w aplikację.";
    private bool _isContentUpdateBusy;
    private AppUpdateAvailability? _availableAppUpdate;
    private string _appUpdateStatus = $"Wersja aplikacji: v{AppUpdateService.GetCurrentVersion()}.";
    private bool _isAppUpdateBusy;

    // --- New UI additions ---
    private string _headerAreaText = "--- Niepołączono ---";
    private int _selectedRightTab;
    private string _newNoteTitle = string.Empty;
    private string _newNoteContent = string.Empty;
    private bool _newNoteIsGlobal;
    private NoteEntry? _editedNote;
    private bool _isNoteFormExpanded;

    // --- App settings ---
    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private bool _settingsLoaded;

    // --- Per-profile automation settings (autostand, autoscan, ...) — see ActivateProfile,
    // SaveActiveProfile and LoadLegacyAutomationSettingsSeed. Defaults until a profile loads. ---
    private ProfileAutomationSettings _profileSettings = new();
    private ProfileAutomationSettings? _legacyAutomationSettingsSeed;

    public string SettingsDirectory => _settingsService.DirectoryPath;

    // --- Automation list display (Timery/Aliasy/Triggery) ---
    private bool _isAutomationCompactView;

    // --- New alias/trigger form ---
    private string _newRuleName = string.Empty;
    private string _newRuleType = "alias";
    private string _newRulePattern = string.Empty;
    private string _newRuleAction = string.Empty;
    private string? _newRulePatternError;
    private bool _newRuleIsGlobal;
    private bool _newRuleIsScript;
    private bool _newRulePlaySoundOnMatch;
    private string _newRuleTestInput = string.Empty;
    private string? _newRuleTestOutput;
    private AutomationRuleEntry? _editedRule;
    private bool _isRuleFormExpanded;

    // --- Timers ---
    private string _newTimerName = string.Empty;
    private string _newTimerMinutes = "0";
    private string _newTimerSeconds = "0";
    private string _newTimerMilliseconds = "0";
    private string _newTimerCommands = string.Empty;
    private bool _newTimerIsGlobal;
    private bool _newTimerIsScript;
    private bool _newTimerPlaySoundOnTick;
    private string? _newTimerTestOutput;
    private TimerEntry? _editedTimer;
    private bool _isTimerFormExpanded;
    private int _selectedAutomationTabIndex;

    // --- Autowalk ---
    private string _newLocationName = string.Empty;
    private string _newLocationVnum = string.Empty;
    private bool _newLocationIsGlobal;
    private MapPathfinder? _pathfinder;
    private MapIndex? _pathfinderIndex;
    private MapPath? _autowalkPath;
    /// <summary>Rooms to route around for the walk currently in <see cref="_autowalkPath"/> —
    /// captured when the walk starts (see <see cref="StartAutowalk"/>) and reused whenever the
    /// route gets recomputed mid-walk, so a detour recalculation can't accidentally route back
    /// through a room the original plan was avoiding. Cleared in <see cref="StopAutowalk"/>.</summary>
    private IReadOnlySet<int>? _autowalkExcludedRoomIds;
    private int _autowalkStep;
    private int _autowalkRecomputes;
    private string? _autowalkTargetName;
    private string _autowalkStatusText = "Bezczynny.";
    private AutowalkLocation? _temporaryTarget;

    // --- Recent automation activity (bottom status bar) ---
    private static readonly TimeSpan AutomationActivityDisplayDuration = TimeSpan.FromSeconds(3);
    private string? _recentAutomationActivityText;
    private CancellationTokenSource? _automationActivityClearCts;
    private readonly Dictionary<string, CancellationTokenSource> _triggerRecentlyFiredClearTokens = new();

    // Destination of a walk that was cut short (lost route / off-course), so a
    // bare /walk can pick the journey back up. Cleared on arrival, explicit stop,
    // or when a new walk starts — only an abnormal interruption sets it.
    private AutowalkLocation? _pendingResumeTarget;
    private CancellationTokenSource _autowalkCts = new();

    // --- Auto-farm: repeatedly autowalks to the nearest unvisited room inside one or more
    // user-drawn FarmRegions, letting the existing autokill-on-room-enter automation do the
    // actual fighting, and pausing to heal/rest whenever HP drops below a configurable threshold.
    // Drives the same _autowalkPath/_autowalkStep machinery as a named-location walk — see
    // CompleteAutowalkArrival.
    private bool _autoFarmActive;
    private IReadOnlyList<FarmRegion> _autoFarmRegions = [];
    private HashSet<int> _autoFarmVisitedRoomIds = [];
    // Full visiting order planned once at StartAutoFarm via FarmTraversalPlanner.BuildVisitOrder
    // (nearest-neighbor + 2-opt) — see PickNextAutoFarmRoom, which consumes it instead of
    // FindNearestUnvisitedRoom's old per-arrival greedy pick.
    private IReadOnlyList<MapRoom>? _autoFarmVisitOrder;
    private int _autoFarmHealRecoveryAttempts;
    private const int MaxAutoFarmHealRecoveryAttempts = 5;

    // Bug fix: low-movement recovery (rest → stand) previously had no attempt cap, so a
    // character whose MV never climbed back above the threshold in one rest cycle — e.g.
    // because combat (autokill, mid auto-farm) kept interrupting/re-draining it — would rest,
    // stand, rest, stand forever. Capped the same way _autowalkRecomputes already is below.
    private int _autowalkMovementRecoveryAttempts;
    private const int MaxAutowalkMovementRecoveryAttempts = 5;

    // Bug fix (#34): a move command that's silently swallowed by the server (e.g. a locked door
    // whose GMCP exit was never flagged door+closed, so TryGetOpenCommand never fires, and whose
    // failure text isn't the literal "brama...zamknięta" HandleLockedAutowalkGate matches) left
    // autowalk — and auto-farm, which is just autowalk on a loop — waiting forever for a room
    // change that would never come. This is a generic backstop: if the room hasn't changed a few
    // seconds after a step was sent, try the same generic door-opening commands
    // AutowalkRecoveryPolicy already uses for a recognized locked gate, then give up and exclude
    // the room rather than hang indefinitely.
    private static readonly TimeSpan AutowalkStuckStepTimeout = TimeSpan.FromSeconds(8);
    private int _autowalkStuckRecoveryAttempts;
    private const int MaxAutowalkStuckRecoveryAttempts = 2;
    private int _autoFarmHpThresholdPercent = ProfileData.DefaultAutoFarmHpThresholdPercent;
    private int _autoFarmStepDelayMilliseconds;
    private List<string> _autoFarmHealSpellNames = [];
    private List<AutoFarmMemSpell> _autoFarmMemSpells = [];
    private List<AutoFarmCastSpell> _autoFarmCastSequence = [];
    private string _autoFarmStatusText = "Farma nieaktywna.";
    private CancellationTokenSource? _bookRefreshCts;
    private CancellationTokenSource? _rareRefreshCts;
    private CancellationTokenSource? _mapujCts;
    /// <summary>The in-flight "/mapuj" run, if any — not bound to an AsyncRelayCommand (it's
    /// started from a typed slash command, not a button), so DisposeAsync needs its own way to
    /// await it after cancelling, mirroring how it awaits Killeropedia's book/rare refresh
    /// commands' own ExecutionTask.</summary>
    private Task? _mapujTask;
    private int? _latestMovement;
    private int? _latestMaximumMovement;
    private int? _latestHp;
    private int? _latestMaxHp;
    private IReadOnlyList<MemorizedSpell> _latestMemorizedSpells = [];
    private bool _autowalkRecoveringMovement;
    private bool _autowalkRecoveringPosition;
    private int? _autowalkOpeningStep;
    private bool _autowalkWaitingForGate;
    private bool _autowalkGateCommandsSent;
    private bool _autowalkGateIsOpen;

    // Set while an active walk is on hold because a fight broke out mid-route:
    // no room change arrives during combat, so the walk must be nudged back to
    // life once GMCP reports the character has left the "fighting" position.
    private bool _autowalkPausedForCombat;

    // Set while an active walk is on hold because the player consciously rested
    // mid-route ("rest") — resting doesn't move the character, so without this
    // the stuck-step backstop (MonitorAutowalkStepStuckAsync) would mistake the
    // stall for a blocked door and start knocking/pulling/pushing before
    // resending the movement command anyway.
    private bool _autowalkPausedForResting;

    // --- Required buffs ---
    private string _newBuffName = string.Empty;
    private string _newBuffSetName = string.Empty;
    private string _buffSetNameDraft = string.Empty;
    private BuffSetEntry? _selectedBuffSet;
    private bool _loadingBuffSets;

    /// <summary>
    /// Normalized names from the latest Char.Affects, used to mark
    /// required buffs as active/missing. Updated on the UI thread.
    /// </summary>
    private readonly HashSet<string> _activeAffectNames = new(StringComparer.OrdinalIgnoreCase);

    // --- Profiles ---
    private string? _activeProfileName;
    private string _activeProfileLogin = string.Empty;
    private string? _selectedProfileName;
    private string _selectedProfileLogin = string.Empty;
    private string _newProfileName = string.Empty;
    private string _newProfileLogin = string.Empty;
    private string _newProfileHost = "killer-mud.pl";
    private int _newProfilePort = 4004;
    private string _newProfileEncoding = MudTextEncodings.Auto;
    private string _newProfilePassword = string.Empty;
    private string _selectedProfilePassword = string.Empty;

    /// <summary>Decrypted password of the active account, kept only in memory.</summary>
    private string _activeProfilePassword = string.Empty;

    /// <summary>
    /// True while the active account still needs the MUD character-creation
    /// sequence on connect (mirrors <see cref="ProfileData.NeedsRegistration"/>).
    /// </summary>
    private bool _activeProfileNeedsRegistration;

    /// <summary>
    /// Last-write timestamp of the active profile's file as of the last time this
    /// instance loaded or saved it. Used to detect that another running instance of
    /// the client saved the same profile in the meantime, so a blind overwrite from
    /// here would silently discard that instance's changes.
    /// </summary>
    private DateTime? _activeProfileLastKnownWriteUtc;

    /// <summary>Same as <see cref="_activeProfileLastKnownWriteUtc"/>, but for the shared global file.</summary>
    private DateTime? _globalLastKnownWriteUtc;

    /// <summary>The active character's spell knowledge, keyed by spell name (case-insensitive) —
    /// loaded from <see cref="ProfileData.KnownSpells"/> on activation, updated as "spell"/"spell
    /// all" output is seen (see <see cref="CollectSpellKnowledge"/>), and mirrored into
    /// <see cref="Map"/>'s <see cref="MapViewModel.SpellKnowledge"/> for the map's tooltips.</summary>
    private Dictionary<string, bool> _knownSpells = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The active character's skill knowledge, keyed by skill name (case-insensitive) —
    /// loaded from <see cref="ProfileData.KnownSkills"/> on activation, updated as "skill" output
    /// is seen (see <see cref="CollectSkillKnowledge"/>), and mirrored into <see cref="Map"/>'s
    /// <see cref="MapViewModel.SkillKnowledge"/> for the map's teacher tooltips.</summary>
    private Dictionary<string, int> _knownSkills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What this instance last loaded or saved for the active profile — the "base" side of a
    /// 3-way merge when another instance changed the file first (see SaveActiveProfile).
    /// </summary>
    private ProfileData? _activeProfileBaselineSnapshot;

    /// <summary>Same as <see cref="_activeProfileBaselineSnapshot"/>, but for the shared global file.</summary>
    private GlobalData? _globalBaselineSnapshot;

    public MainWindowViewModel(
        ProfileService? profileService = null,
        AppSettingsService? settingsService = null,
        DockLayoutService? dockLayoutService = null,
        BookCatalogStore? bookCatalogStore = null,
        BookCatalogRefreshCoordinator? bookCatalogRefreshCoordinator = null,
        LayoutPresetService? layoutPresetService = null,
        IExternalLinkService? externalLinkService = null,
        IContentUpdateService? contentUpdateService = null,
        RareCatalogStore? rareCatalogStore = null,
        RareCatalogRefreshCoordinator? rareCatalogRefreshCoordinator = null,
        IAppUpdateService? appUpdateService = null,
        AbilityCaptureStore? abilityCaptureStore = null,
        AbilityMappingCoordinator? abilityMappingCoordinator = null,
        ArtifactTryStore? artifactTryStore = null,
        ArtifactTryMappingCoordinator? artifactTryMappingCoordinator = null,
        GroupSpellStore? groupSpellStore = null)
    {
        _triggers = new TriggerEngine { Aliases = _aliases };
        _aliases.Lua = _lua;
        _triggers.Lua = _lua;
        _lua.GameStateProvider = BuildLuaGameState;
        _lua.Echo += OnLuaEcho;
        _aliases.ScriptError += OnLuaScriptError;
        _triggers.ScriptError += OnLuaScriptError;
        _triggers.RuleMatched += OnTriggerRuleMatched;
        ApplyLuaLibraryCommand = new RelayCommand(ApplyLuaLibrary);
        _profiles = profileService ?? new ProfileService();
        _settingsService = settingsService ?? new AppSettingsService();
        _settings = _settingsService.Load();
        _usesCustomBookCatalogStore = bookCatalogStore is not null;
        _bookCatalogStore = bookCatalogStore ?? CreateBookCatalogStore();
        _bookCatalogRefreshCoordinator = bookCatalogRefreshCoordinator ?? new BookCatalogRefreshCoordinator();
        _usesCustomRareCatalogStore = rareCatalogStore is not null;
        _rareCatalogStore = rareCatalogStore ?? CreateRareCatalogStore();
        _rareCatalogRefreshCoordinator = rareCatalogRefreshCoordinator ?? new RareCatalogRefreshCoordinator();
        _abilityCaptureStore = abilityCaptureStore ?? new AbilityCaptureStore();
        _abilityMappingCoordinator = abilityMappingCoordinator ?? new AbilityMappingCoordinator();
        _artifactTryStore = artifactTryStore ?? new ArtifactTryStore();
        _artifactTryMappingCoordinator = artifactTryMappingCoordinator ?? new ArtifactTryMappingCoordinator();
        _groupSpellStore = groupSpellStore ?? new GroupSpellStore();
        foreach (var shortcut in LoadGroupSpells(_groupSpellStore))
        {
            GroupSpells.Add(shortcut);
        }
        _contentUpdateService = contentUpdateService ?? new ContentUpdateService(_settingsService.DirectoryPath);
        _appUpdateService = appUpdateService ?? new AppUpdateService();
        _externalLinkService = externalLinkService ?? new ExternalLinkService();
        Killeropedia = CreateKilleropediaViewModel();
        AutomationRules.CollectionChanged += (_, _) => OnFolderCollectionsChanged();
        Timers.CollectionChanged += (_, _) => OnFolderCollectionsChanged();
        Notes.CollectionChanged += (_, _) => OnFolderCollectionsChanged();
        Locations.CollectionChanged += (_, _) => OnFolderCollectionsChanged();
        Folders.CollectionChanged += (_, _) => OnFolderCollectionsChanged();
        ApplyWidgetFontResources();
        ApplyTerminalOverlayOpacityResource();
        PopulateAvailableFonts();
        _settingsLoaded = true;
        _connectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        _disconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
        _sendCommandCommand = new AsyncRelayCommand(SendCurrentCommandAsync, CanSendCommand);
        _retryStartupCommand = new AsyncRelayCommand(RetryStartupAsync);
        ExaminePersonCommand = new RelayCommand<string>(ExecuteExaminePerson);
        KillPersonCommand = new RelayCommand<string>(ExecuteKillPerson);
        LordGotoGroupRoomCommand = new RelayCommand<GroupMember>(
            ExecuteLordGotoGroupRoom,
            CanExecuteLordGotoGroupRoom);
        LordGotoGroupMemberCommand = new RelayCommand<GroupMember>(
            ExecuteLordGotoGroupMember,
            CanExecuteLordGotoGroupMember);
        AddGroupSpellCommand = new RelayCommand(ExecuteAddGroupSpell, CanExecuteAddGroupSpell);
        RemoveGroupSpellCommand = new RelayCommand<GroupSpellShortcut>(ExecuteRemoveGroupSpell);
        SelectProfileCommand = new RelayCommand(SelectProfile, () => !string.IsNullOrWhiteSpace(SelectedProfileName));
        CreateProfileCommand = new RelayCommand(CreateProfile, () => !string.IsNullOrWhiteSpace(NewProfileName));
        SwitchProfileCommand = new RelayCommand(SwitchProfile, () => IsProfileSelected && !IsConnected && !IsBusy);
        DeleteProfileCommand = new RelayCommand<string>(DeleteProfile);
        AddTimerCommand = new RelayCommand(AddTimer, () => !string.IsNullOrWhiteSpace(NewTimerName));
        StartAddTimerCommand = new RelayCommand(StartAddTimer);
        DeleteTimerCommand = new RelayCommand<TimerEntry>(DeleteTimer);
        ToggleTimerCommand = new RelayCommand<TimerEntry>(ToggleTimer);
        RestartTimerCommand = new RelayCommand<TimerEntry>(RestartTimer);
        EditTimerCommand = new RelayCommand<TimerEntry>(EditTimer);
        CancelTimerEditCommand = new RelayCommand(CancelTimerEdit);
        TestTimerScriptCommand = new RelayCommand(TestTimerScript, () => NewTimerIsScript);
        AddRuleCommand = new RelayCommand(AddRule, CanAddRule);
        StartAddAliasCommand = new RelayCommand(() => StartAddRule("alias"));
        StartAddTriggerCommand = new RelayCommand(() => StartAddRule("trigger"));
        DeleteRuleCommand = new RelayCommand<AutomationRuleEntry>(DeleteRule);
        ToggleRuleCommand = new RelayCommand<AutomationRuleEntry>(ToggleRule);
        TestRuleScriptCommand = new RelayCommand(TestRuleScript, () => NewRuleIsScript);
        EditRuleCommand = new RelayCommand<AutomationRuleEntry>(EditRule);
        CancelRuleEditCommand = new RelayCommand(CancelRuleEdit);
        AddCurrentLocationCommand = new RelayCommand(AddCurrentLocation);
        AddLocationCommand = new RelayCommand(AddLocation);
        DeleteLocationCommand = new RelayCommand<AutowalkLocation>(DeleteLocation);
        DeleteDeathCommand = new RelayCommand<DeathMarkEntry>(DeleteDeath);
        GoToDeathCommand = new RelayCommand<DeathMarkEntry>(GoToDeath);
        AddBuffCommand = new RelayCommand(AddBuff, () => !string.IsNullOrWhiteSpace(NewBuffName));
        DeleteBuffCommand = new RelayCommand<BuffWatchEntry>(DeleteBuff);
        CreateBuffSetCommand = new RelayCommand(CreateBuffSet, () => !string.IsNullOrWhiteSpace(NewBuffSetName));
        RenameBuffSetCommand = new RelayCommand(RenameSelectedBuffSet, () =>
            SelectedBuffSet is not null && !string.IsNullOrWhiteSpace(BuffSetNameDraft));
        DeleteBuffSetCommand = new RelayCommand(DeleteSelectedBuffSet, () => BuffSets.Count > 1);
        RecastBuffsCommand = new AsyncRelayCommand(RecastMissingBuffsAsync);
        RecastSingleBuffCommand = new AsyncRelayCommand<BuffWatchEntry>(RecastSingleBuffAsync);
        CastRefreshOnGroupCommand = new AsyncRelayCommand(CastRefreshOnGroupAsync);
        var defaultBuffSet = new BuffSetEntry { Name = "Domyślny" };
        BuffSets.Add(defaultBuffSet);
        _selectedBuffSet = defaultBuffSet;
        _buffSetNameDraft = defaultBuffSet.Name;
        GoToLocationCommand = new RelayCommand<AutowalkLocation>(entry =>
        {
            if (entry is not null)
            {
                StartAutowalk(entry);
            }
        });
        StopAutowalkCommand = new RelayCommand(() => StopAutowalk("Autowalk zatrzymany."));
        StartAutoFarmCommand = new RelayCommand(StartAutoFarm, CanStartAutoFarm);
        StopAutoFarmCommand = new RelayCommand(() => StopAutoFarm("Farma zatrzymana."), () => _autoFarmActive);
        GoToTemporaryTargetCommand = new RelayCommand(() =>
        {
            if (_temporaryTarget is not null)
            {
                StartAutowalk(_temporaryTarget);
            }
        });
        GoToSelectedTargetCommand = new RelayCommand(HandleGoToSelectedTarget);

        _characterState.VitalsChanged += OnCharacterVitalsChanged;
        _characterState.ConditionChanged += OnCharacterConditionChanged;
        _characterState.PeopleChanged += OnRoomPeopleChanged;
        _characterState.GroupChanged += OnGroupChanged;
        _characterState.AffectsChanged += OnCharacterAffectsChanged;
        _characterState.MemSpellsChanged += OnMemSpellsChanged;
        _worldState.TimeChanged += OnWorldTimeChanged;
        _worldState.WeatherChanged += OnWorldWeatherChanged;
        _skillTimeouts.TimeoutsChanged += OnSkillTimeoutsChanged;

        _session.TextReceived += OnTextReceived;
        _session.LineReceived += OnLineReceived;
        _session.GmcpReceived += OnGmcpReceived;
        _session.GmcpSent += OnGmcpSent;
        _session.CommandSent += OnCommandSent;
        _session.StatusChanged += OnStatusChanged;
        _session.ConnectionError += OnConnectionError;
        _session.ConnectionClosed += OnConnectionClosed;

        Map = new MapViewModel(AppContext.BaseDirectory, _locationResolver, _settingsService.DirectoryPath)
        {
            LordModeEnabled = _profileSettings.LordModeEnabled,
            ShowGroupMembersAsNumbers = _profileSettings.ShowGroupMembersAsNumbers,
            SelectedDisplayMode = MapDisplayModeOption.All.First(option => option.Mode == _settings.MapDisplayMode),
            AutoWalkOnMapDoubleClick = _profileSettings.AutoWalkOnMapDoubleClick,
            AutoScanOnRoomEnter = _profileSettings.AutoScanOnRoomEnterEnabled,
            AutoKillOnRoomEnter = _profileSettings.AutoKillOnRoomEnterEnabled,
            AutoKillMobNamesText = string.Join(Environment.NewLine, _profileSettings.AutoKillMobNames),
            MainViewModel = this,
        };
        Map.PropertyChanged += OnMapPropertyChanged;
        _locationResolver.LocationChanged += OnAutowalkLocationChanged;
        _locationResolver.LocationChanged += OnRoomEnterAutomations;
        _locationResolver.LocationChanged += OnRoomEnterShowVnum;
        _roomExits.ExitsChanged += OnRoomExitsChanged;
        _roomSnapshots.SnapshotReceived += OnRoomSnapshotReceived;
        Map.RoomDoubleClicked += OnMapRoomDoubleClicked;
        Map.LordGotoRequested += OnLordGotoRequested;
        Map.LordModeChanged += OnMapLordModeChanged;
        Map.GroupMarkerDisplayChanged += OnMapGroupMarkerDisplayChanged;
        Map.DisplayModeChanged += OnMapDisplayModeChanged;
        Map.AutoWalkOnMapDoubleClickChanged += OnMapAutoWalkOnDoubleClickChanged;
        Map.MapEditorActiveChanged += OnMapEditorActiveChanged;
        Map.AutoScanOnRoomEnterChanged += OnMapAutoScanOnRoomEnterChanged;
        Map.AutoKillOnRoomEnterChanged += OnMapAutoKillOnRoomEnterChanged;
        Map.AutoKillMobNamesChanged += OnMapAutoKillMobNamesChanged;
        Map.AutoFarmRegionsChanged += OnMapAutoFarmRegionsChanged;

        _dockFactory = new MudDockFactory(Map, this);
        _dockLayoutService = dockLayoutService ?? new DockLayoutService();
        Layout = _dockFactory.CreateTransparencyLayout();
        _dockFactory.InitLayout(Layout);

        // TRANSPARENCY is always the startup layout now — a snapshot saved from a DEFAULT
        // session (including ones from before this became the default) must not resurrect that
        // shape here. Only a previously saved TRANSPARENCY session (e.g. remembered pinned
        // overlays) is worth restoring on startup.
        var savedLayout = _dockLayoutService.Load();
        if (savedLayout is { IsTransparencyLayout: true })
        {
            _dockFactory.TryApplySnapshot(Layout, savedLayout);
        }

        _dockFactory.HiddenTools.CollectionChanged += OnHiddenToolsChanged;
        _dockFactory.OverlayChanged += OnOverlayChanged;
        ApplyOverlayFromSettings();

        Vitals.PropertyChanged += (_, _) => UpdateTerminalToolTitle();
        WorldTime.PropertyChanged += (_, _) => UpdateTerminalToolTitle();
        UpdateTerminalToolTitle();
        RestorePanelCommand = new RelayCommand<PanelTool>(tool =>
        {
            if (tool is not null)
            {
                _dockFactory.RestoreToTopEdge(tool);
            }
        });

        _layoutPresetService = layoutPresetService ?? new LayoutPresetService();
        _layoutPresets = _layoutPresetService.Load();
        RefreshAvailableLayouts();
        ApplyLayoutCommand = new RelayCommand<string>(ApplyLayout);
        SaveLayoutCommand = new RelayCommand(SaveLayout);
        DeleteLayoutCommand = new RelayCommand<string>(DeleteLayout);
        OpenKilleropediaCommand = new RelayCommand(() =>
        {
            IsHelpOpen = false;
            IsKilleropediaOpen = true;
        });
        OpenHelpCommand = new RelayCommand(() =>
        {
            IsKilleropediaOpen = false;
            IsHelpOpen = true;
        });
        OpenDiscordCommand = new RelayCommand(() => OpenExternalLink(DiscordInviteUri));
        OpenDiscussionsCommand = new RelayCommand(() => OpenExternalLink(DiscussionsUri));
        OpenAuthorPageCommand = new RelayCommand(() => OpenExternalLink(AuthorPageUri));
        CheckContentUpdatesCommand = new AsyncRelayCommand(
            cancellationToken => CheckContentUpdatesAsync(reportErrors: true, cancellationToken),
            () => !IsContentUpdateBusy);
        InstallContentUpdateCommand = new AsyncRelayCommand(
            InstallContentUpdateAsync,
            () => AvailableContentUpdate is not null && !IsContentUpdateBusy);
        CheckAppUpdatesCommand = new AsyncRelayCommand(
            cancellationToken => CheckAppUpdatesAsync(reportErrors: true, notifyOnFound: false, cancellationToken),
            () => !IsAppUpdateBusy);
        OpenAppUpdateCommand = new RelayCommand(
            () => OpenExternalLink(AvailableAppUpdate?.DownloadPageUri),
            () => AvailableAppUpdate is not null);

        PopulateMockData();

        foreach (var name in _profiles.ListProfileNames())
        {
            AvailableProfiles.Add(name);
        }

        // Global entries are usable even before any profile is selected.
        LoadGlobalEntries();
        ApplyAutomation();
        SyncAllTimers();

        AvailableProfiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));

        // Multiboxing: SaveActiveProfile only pulls in another running instance's changes when
        // *this* instance saves something of its own — switching to this window without editing
        // anything here never triggered that, so a trigger/folder added on the other account
        // could sit unseen indefinitely. Calling it periodically closes that gap; it's a no-op
        // (no disk write, no toast) whenever nothing has actually changed on either side.
        // MudTimerService.RunAsync has no catch of its own around a periodic callback, so any
        // unhandled exception here would abandon the do/while loop and silently kill this timer
        // for the rest of the session — belt-and-braces on top of SaveActiveProfile's own
        // try/catch, in case a future change adds a code path that isn't covered by it.
        _timers.StartPeriodic(
            MultiboxSyncTimerName,
            MultiboxSyncInterval,
            async _ =>
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(SaveActiveProfile);
                }
                catch (Exception exception)
                {
                    Dispatcher.UIThread.Post(() => EmitSystem($"Auto-sync: {exception.Message}", 31));
                }
            });
    }

    private const string MultiboxSyncTimerName = "system:multibox-sync";
    private static readonly TimeSpan MultiboxSyncInterval = TimeSpan.FromSeconds(4);

    public MapViewModel Map { get; }

    private KilleropediaViewModel _killeropedia = null!;

    public KilleropediaViewModel Killeropedia
    {
        get => _killeropedia;
        private set => SetProperty(ref _killeropedia, value);
    }

    public IRootDock Layout
    {
        get => _layout;
        private set => SetProperty(ref _layout, value);
    }

    public ObservableCollection<PanelTool> HiddenPanels => _dockFactory.HiddenTools;

    /// <summary>Panels currently pinned as floating overlays on the Terminal — only possible in
    /// TRANSPARENCY mode (see <see cref="MudDockFactory.IsTransparencyLayout"/>). Kept in sync
    /// with <see cref="MudDockFactory.OverlayTools"/> by <see cref="OnOverlayChanged"/>.</summary>
    public ObservableCollection<TerminalOverlayViewModel> TerminalOverlays { get; } = new();

    public IRelayCommand<PanelTool> RestorePanelCommand { get; }

    /// <summary>
    /// Lets the view supply the live fixed preview size: one third of the dock width for side tabs
    /// and half its height for top/bottom tabs. The factory itself is UI-agnostic.
    /// </summary>
    public void ConfigurePinnedPreviewSize(Func<Dock.Model.Core.Alignment, double> provider) =>
        _dockFactory.PinnedPreviewSizeProvider = provider;

    /// <summary>Called after every dock drag ends: panels the drag pipeline lost (dropped over
    /// non-dock chrome like the top bar) are moved to <see cref="HiddenPanels"/> for restore.</summary>
    public void ReclaimLostPanels() => _dockFactory.ReclaimLostTools(Layout);

    /// <summary>
    /// Re-pins tools whose edge tabs did not materialize in the live Dock visual tree.
    /// The view calls this only after the replacement layout has had time to render.
    /// </summary>
    public void RepairUnrenderedPinnedPanels(IReadOnlyCollection<PanelTool> renderedPanels) =>
        _dockFactory.RepairUnrenderedPinnedTools(Layout, renderedPanels);

    internal IReadOnlyCollection<PanelTool> PinnedPanels => _dockFactory.GetPinnedTools(Layout);

    /// <summary>Layout entries offered in the "Układ" menu: built-in DEFAULT first, then saved presets.</summary>
    public ObservableCollection<LayoutMenuItem> AvailableLayouts { get; } = new();

    public IRelayCommand<string> ApplyLayoutCommand { get; }

    public IRelayCommand SaveLayoutCommand { get; }

    public IRelayCommand<string> DeleteLayoutCommand { get; }

    public IRelayCommand OpenKilleropediaCommand { get; }

    public IRelayCommand OpenHelpCommand { get; }

    public IRelayCommand OpenDiscordCommand { get; }

    public IRelayCommand OpenDiscussionsCommand { get; }

    /// <summary>Opens the author's page (see <see cref="AuthorPageUri"/>) — bound to the small
    /// mushroom logo in the top-right corner of the window.</summary>
    public IRelayCommand OpenAuthorPageCommand { get; }

    public IAsyncRelayCommand CheckContentUpdatesCommand { get; }

    public IAsyncRelayCommand InstallContentUpdateCommand { get; }

    public IAsyncRelayCommand CheckAppUpdatesCommand { get; }

    public IRelayCommand OpenAppUpdateCommand { get; }

    public ContentUpdateAvailability? AvailableContentUpdate
    {
        get => _availableContentUpdate;
        private set
        {
            if (SetProperty(ref _availableContentUpdate, value))
            {
                OnPropertyChanged(nameof(IsContentUpdateAvailable));
                OnPropertyChanged(nameof(ContentUpdateDescription));
                InstallContentUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsContentUpdateAvailable => AvailableContentUpdate is not null;

    public bool IsContentUpdateBusy
    {
        get => _isContentUpdateBusy;
        private set
        {
            if (SetProperty(ref _isContentUpdateBusy, value))
            {
                CheckContentUpdatesCommand.NotifyCanExecuteChanged();
                InstallContentUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ContentUpdateStatus
    {
        get => _contentUpdateStatus;
        private set => SetProperty(ref _contentUpdateStatus, value);
    }

    public string ContentUpdateDescription => AvailableContentUpdate is { } update
        ? $"{ComponentVersions(update.Components)} · {FormatBytes(update.DownloadSize)}"
        : string.Empty;

    public AppUpdateAvailability? AvailableAppUpdate
    {
        get => _availableAppUpdate;
        private set
        {
            if (SetProperty(ref _availableAppUpdate, value))
            {
                OnPropertyChanged(nameof(IsAppUpdateAvailable));
                OpenAppUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsAppUpdateAvailable => AvailableAppUpdate is not null;

    public bool IsAppUpdateBusy
    {
        get => _isAppUpdateBusy;
        private set
        {
            if (SetProperty(ref _isAppUpdateBusy, value))
            {
                CheckAppUpdatesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string AppUpdateStatus
    {
        get => _appUpdateStatus;
        private set => SetProperty(ref _appUpdateStatus, value);
    }

    /// <summary>Name typed into the "zapisz układ" field before saving the current arrangement.</summary>
    public string NewLayoutName
    {
        get => _newLayoutName;
        set => SetProperty(ref _newLayoutName, value);
    }

    private void RefreshAvailableLayouts()
    {
        AvailableLayouts.Clear();
        AvailableLayouts.Add(new LayoutMenuItem { Name = LayoutPresetService.DefaultName, CanDelete = false });
        AvailableLayouts.Add(new LayoutMenuItem { Name = LayoutPresetService.TransparencyName, CanDelete = false });
        AvailableLayouts.Add(new LayoutMenuItem { Name = LayoutPresetService.CompactName, CanDelete = false });
        foreach (var preset in _layoutPresets)
        {
            AvailableLayouts.Add(new LayoutMenuItem { Name = preset.Name, CanDelete = true });
        }
    }

    /// <summary>Restores the built-in default layout, the built-in transparency layout, or a
    /// named preset.</summary>
    private void ApplyLayout(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // A DockControl can finish detaching the previous tree after this method returns. Give
        // every replacement tree its own factory so late close/unpin callbacks from the old tree
        // cannot mutate the new root, tool registry, or "Panele" collection.
        var previousFactory = _dockFactory;
        var replacementFactory = new MudDockFactory(Map, this)
        {
            PinnedPreviewSizeProvider = previousFactory.PinnedPreviewSizeProvider,
        };

        IRootDock fresh;
        if (string.Equals(name, LayoutPresetService.TransparencyName, StringComparison.Ordinal))
        {
            fresh = replacementFactory.CreateTransparencyLayout();
            replacementFactory.InitLayout(fresh);

            // Every non-Terminal panel starts hidden in a fresh TRANSPARENCY layout. Restore them
            // all to the top edge right away — the same RestoreToTopEdge a manual "Przywróć panel"
            // click performs — so the user can immediately open one and pin it as an overlay
            // instead of having to restore each panel by hand first.
            foreach (var tool in replacementFactory.HiddenTools.ToList())
            {
                replacementFactory.RestoreToTopEdge(tool);
            }
        }
        else if (string.Equals(name, LayoutPresetService.CompactName, StringComparison.Ordinal))
        {
            fresh = replacementFactory.CreateCompactLayout();
            replacementFactory.InitLayout(fresh);
        }
        else
        {
            fresh = replacementFactory.CreateLayout();
            replacementFactory.InitLayout(fresh);

            if (!string.Equals(name, LayoutPresetService.DefaultName, StringComparison.Ordinal))
            {
                var preset = _layoutPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                if (preset is null)
                {
                    return;
                }

                if (!replacementFactory.TryApplySnapshot(fresh, preset.Snapshot))
                {
                    // Snapshot no longer matches the current set of panels (e.g. after an update).
                    AddToast($"Układ „{name}” jest nieaktualny — wczytano DEFAULT.", "warning");
                }
            }
        }

        previousFactory.HiddenTools.CollectionChanged -= OnHiddenToolsChanged;
        previousFactory.OverlayChanged -= OnOverlayChanged;
        _dockFactory = replacementFactory;
        _dockFactory.HiddenTools.CollectionChanged += OnHiddenToolsChanged;
        _dockFactory.OverlayChanged += OnOverlayChanged;
        Layout = fresh;
        OnPropertyChanged(nameof(HiddenPanels));
        ApplyOverlayFromSettings();

        // ResetToDefault/TryApplySnapshot recreate all tools with default titles.
        UpdateMemToolTitle();
        UpdateTerminalToolTitle();
    }

    private void OnHiddenToolsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HiddenPanels));

    private void OnOverlayChanged(object? sender, EventArgs e)
    {
        SyncTerminalOverlaysFromFactory();
        SaveSettings();
    }

    /// <summary>Rebuilds <see cref="TerminalOverlays"/> and the persisted entry list from
    /// <see cref="MudDockFactory.OverlayTools"/>, reusing each panel's existing
    /// <see cref="TerminalOverlayEntry"/> (and its height weight) when it is still active.</summary>
    private void SyncTerminalOverlaysFromFactory()
    {
        var activeTools = _dockFactory.OverlayTools;
        var newEntries = new List<TerminalOverlayEntry>();

        TerminalOverlays.Clear();
        foreach (var tool in activeTools)
        {
            var entry = _settings.TerminalOverlays.FirstOrDefault(e => e.PanelId == tool.Id)
                ?? new TerminalOverlayEntry { PanelId = tool.Id! };
            newEntries.Add(entry);
            TerminalOverlays.Add(new TerminalOverlayViewModel(tool, entry, SaveSettings, HandleOverlayMove));
        }

        _settings.TerminalOverlays = newEntries;
    }

    /// <summary>Handles a card's move ▲▼◀▶ buttons. Up/Down reorder within the overlay's current
    /// column (a pin-order swap in <see cref="_dockFactory"/>); Left/Right move it toward/away
    /// from the right edge by one column, creating a new column when moving beyond the current
    /// outermost one — the Terminal itself never moves or resizes.</summary>
    private void HandleOverlayMove(TerminalOverlayViewModel overlay, OverlayMoveDirection direction)
    {
        switch (direction)
        {
            case OverlayMoveDirection.Up:
                SwapOverlayWithNeighbor(overlay, -1);
                break;
            case OverlayMoveDirection.Down:
                SwapOverlayWithNeighbor(overlay, 1);
                break;
            case OverlayMoveDirection.Left:
                MoveOverlayColumn(overlay, +1);
                break;
            case OverlayMoveDirection.Right:
                MoveOverlayColumn(overlay, -1);
                break;
        }
    }

    /// <summary>Swaps <paramref name="overlay"/> with the sibling <paramref name="step"/> places
    /// away within its own column (-1 = up/earlier, +1 = down/later). A no-op at either end of
    /// that column.</summary>
    private void SwapOverlayWithNeighbor(TerminalOverlayViewModel overlay, int step)
    {
        var sameColumn = TerminalOverlays.Where(o => o.ColumnIndex == overlay.ColumnIndex).ToList();
        var index = sameColumn.IndexOf(overlay);
        var neighborIndex = index + step;
        if (index < 0 || neighborIndex < 0 || neighborIndex >= sameColumn.Count)
        {
            return;
        }

        _dockFactory.SwapOverlayOrder(overlay.Panel, sameColumn[neighborIndex].Panel);
    }

    /// <summary>Moves <paramref name="overlay"/> <paramref name="direction"/> columns away from
    /// the right edge (+1 = left/further, -1 = right/closer; a no-op past the edge). Unlike
    /// <see cref="SwapOverlayWithNeighbor"/> (which goes through <see cref="_dockFactory"/> and
    /// its own <see cref="MudDockFactory.OverlayChanged"/> notification), the column lives purely
    /// in settings, so this rebuilds and saves directly. Column indices are compacted afterward so
    /// they stay contiguous from 0 with no empty gaps.</summary>
    private void MoveOverlayColumn(TerminalOverlayViewModel overlay, int direction)
    {
        var newIndex = overlay.ColumnIndex + direction;
        if (newIndex < 0)
        {
            return;
        }

        overlay.SetColumnIndex(newIndex);
        CompactOverlayColumns();
        SyncTerminalOverlaysFromFactory();
        SaveSettings();
    }

    /// <summary>Renumbers every overlay's <see cref="TerminalOverlayViewModel.ColumnIndex"/> to
    /// remove gaps left by a move, preserving relative order (e.g. columns in use {0, 2, 5} become
    /// {0, 1, 2}).</summary>
    private void CompactOverlayColumns()
    {
        var orderedIndices = TerminalOverlays.Select(o => o.ColumnIndex).Distinct().OrderBy(i => i).ToList();
        foreach (var overlay in TerminalOverlays)
        {
            overlay.SetColumnIndex(orderedIndices.IndexOf(overlay.ColumnIndex));
        }
    }

    /// <summary>Re-applies the panels remembered as Terminal overlays (if any) after the dock
    /// tree is (re)built — at startup, and again whenever a layout preset switch recreates the
    /// factory. Only meaningful in TRANSPARENCY mode; silently skips any remembered panel id that
    /// no longer exists.</summary>
    private void ApplyOverlayFromSettings()
    {
        TerminalOverlays.Clear();

        if (!_dockFactory.IsTransparencyLayout)
        {
            return;
        }

        foreach (var entry in _settings.TerminalOverlays.ToList())
        {
            var tool = _dockFactory.AllTools.FirstOrDefault(t => t.Id == entry.PanelId);
            if (tool is null || string.Equals(tool.Id, "Terminal", StringComparison.Ordinal))
            {
                continue;
            }

            _dockFactory.PinToolAsOverlay(tool);
        }
    }

    private void SaveLayout()
    {
        var name = NewLayoutName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (string.Equals(name, LayoutPresetService.DefaultName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LayoutPresetService.TransparencyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LayoutPresetService.CompactName, StringComparison.OrdinalIgnoreCase))
        {
            AddToast($"Nazwa „{name}” jest zarezerwowana.", "warning");
            return;
        }

        var snapshot = _dockFactory.Snapshot(Layout);
        var existing = _layoutPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Snapshot = snapshot;
        }
        else
        {
            _layoutPresets.Add(new LayoutPreset { Name = name, Snapshot = snapshot });
        }

        _layoutPresetService.Save(_layoutPresets);
        RefreshAvailableLayouts();
        NewLayoutName = string.Empty;
        AddToast($"Zapisano układ „{name}”.", "info");
    }

    private void DeleteLayout(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, LayoutPresetService.DefaultName, StringComparison.Ordinal)
            || string.Equals(name, LayoutPresetService.TransparencyName, StringComparison.Ordinal)
            || string.Equals(name, LayoutPresetService.CompactName, StringComparison.Ordinal))
        {
            return;
        }

        var removed = _layoutPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (removed > 0)
        {
            _layoutPresetService.Save(_layoutPresets);
            RefreshAvailableLayouts();
            AddToast($"Usunięto układ „{name}”.", "info");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ClearStartupError();
        await Map.InitializeAsync(cancellationToken);
    }

    public void StartContentUpdateCheck()
    {
        if (_contentUpdateCheckTask is not null)
        {
            return;
        }

        _contentUpdateCts = new CancellationTokenSource();
        _contentUpdateCheckTask = CheckContentUpdatesAsync(
            reportErrors: false,
            _contentUpdateCts.Token);
    }

    internal Task? ActiveContentUpdateCheck => _contentUpdateCheckTask;

    /// <summary>Fired once from <see cref="Views.MainWindow"/>'s Opened handler alongside
    /// <see cref="StartContentUpdateCheck"/> — unlike content, a found app update also raises a
    /// toast, since there's no in-app install step to otherwise surface it (see
    /// <see cref="AppUpdateService"/>'s doc comment).</summary>
    public void StartAppUpdateCheck()
    {
        if (_appUpdateCheckTask is not null)
        {
            return;
        }

        _appUpdateCts = new CancellationTokenSource();
        _appUpdateCheckTask = CheckAppUpdatesAsync(
            reportErrors: false,
            notifyOnFound: true,
            _appUpdateCts.Token);
    }

    internal Task? ActiveAppUpdateCheck => _appUpdateCheckTask;

    private async Task CheckAppUpdatesAsync(bool reportErrors, bool notifyOnFound, CancellationToken cancellationToken)
    {
        if (IsAppUpdateBusy)
        {
            return;
        }

        IsAppUpdateBusy = true;
        AppUpdateStatus = "Sprawdzanie aktualizacji aplikacji…";
        try
        {
            AvailableAppUpdate = await _appUpdateService.CheckForUpdateAsync(cancellationToken);
            if (AvailableAppUpdate is { } update)
            {
                AppUpdateStatus = $"Dostępna nowa wersja aplikacji: v{update.Version}.";
                if (notifyOnFound)
                {
                    AddToast($"Dostępna nowa wersja klienta: v{update.Version} — pobierz w Ustawieniach.", "info");
                }
            }
            else
            {
                AppUpdateStatus = $"Wersja aplikacji: v{AppUpdateService.GetCurrentVersion()} (aktualna).";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppUpdateStatus = "Sprawdzanie aktualizacji aplikacji anulowano.";
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            AppUpdateStatus = reportErrors
                ? $"Nie udało się sprawdzić aktualizacji aplikacji: {exception.Message}"
                : "Nie udało się sprawdzić aktualizacji aplikacji. Spróbuj później w ustawieniach.";
        }
        finally
        {
            IsAppUpdateBusy = false;
        }
    }

    private async Task CheckContentUpdatesAsync(bool reportErrors, CancellationToken cancellationToken)
    {
        if (IsContentUpdateBusy)
        {
            return;
        }

        IsContentUpdateBusy = true;
        ContentUpdateStatus = "Sprawdzanie aktualizacji danych…";
        try
        {
            AvailableContentUpdate = await _contentUpdateService.CheckForUpdateAsync(cancellationToken);
            ContentUpdateStatus = AvailableContentUpdate is null
                ? "Mapa i Killeropedia są aktualne."
                : $"Dostępna aktualizacja: {ContentUpdateDescription}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ContentUpdateStatus = "Sprawdzanie aktualizacji anulowano.";
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            if (reportErrors)
            {
                ContentUpdateStatus = $"Nie udało się sprawdzić aktualizacji: {exception.Message}";
            }
            else
            {
                ContentUpdateStatus = "Nie udało się sprawdzić aktualizacji danych. Spróbuj później w ustawieniach.";
            }
        }
        finally
        {
            IsContentUpdateBusy = false;
        }
    }

    private async Task InstallContentUpdateAsync(CancellationToken commandCancellationToken)
    {
        var update = AvailableContentUpdate;
        if (update is null || IsContentUpdateBusy)
        {
            return;
        }

        IsContentUpdateBusy = true;
        using var linkedCancellation = _contentUpdateCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(commandCancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                commandCancellationToken,
                _contentUpdateCts.Token);
        var cancellationToken = linkedCancellation.Token;
        var progress = new Progress<ContentUpdateProgress>(value =>
        {
            var percent = value.TotalBytes == 0
                ? 0
                : (int)Math.Clamp(value.BytesReceived * 100 / value.TotalBytes, 0, 100);
            ContentUpdateStatus = $"Pobieranie {ComponentDisplayName(value.ComponentName)}: {percent}%";
        });
        try
        {
            var result = await _contentUpdateService.InstallAsync(
                update,
                progress,
                cancellationToken);

            ContentUpdateStatus = "Przeładowywanie mapy i Killeropedii…";
            await Map.InitializeAsync(cancellationToken);
            if (!_usesCustomBookCatalogStore)
            {
                _bookCatalogStore = CreateBookCatalogStore();
            }

            if (!_usesCustomRareCatalogStore)
            {
                _rareCatalogStore = CreateRareCatalogStore();
            }

            Killeropedia = CreateKilleropediaViewModel();
            AvailableContentUpdate = null;
            ContentUpdateStatus = $"Zainstalowano dane {result.Release}.";
            AddToast("Mapa i Killeropedia zostały zaktualizowane.", "info");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ContentUpdateStatus = "Aktualizację danych anulowano.";
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            ContentUpdateStatus = $"Aktualizacja nie powiodła się: {exception.Message}";
            AddToast("Nie udało się zaktualizować danych. Poprzednia wersja pozostaje aktywna.", "error");
        }
        finally
        {
            IsContentUpdateBusy = false;
        }
    }

    private BookCatalogStore CreateBookCatalogStore()
    {
        var downloadedDirectory = new ContentPathResolver(_settingsService.DirectoryPath)
            .GetActiveDirectory("killeropedia");
        return new BookCatalogStore(
            DeveloperFeatures.BookCatalogOutputPath
            ?? Path.Combine(_settingsService.DirectoryPath, "killeropedia-books.json"),
            downloadedDirectory is null ? null : Path.Combine(downloadedDirectory, "books.json"));
    }

    private RareCatalogStore CreateRareCatalogStore()
    {
        var downloadedDirectory = new ContentPathResolver(_settingsService.DirectoryPath)
            .GetActiveDirectory("killeropedia");
        return new RareCatalogStore(
            DeveloperFeatures.RareCatalogOutputPath
            ?? Path.Combine(_settingsService.DirectoryPath, "killeropedia-rares.json"),
            downloadedDirectory is null ? null : Path.Combine(downloadedDirectory, "rares.json"));
    }

    private KilleropediaViewModel CreateKilleropediaViewModel()
    {
        var downloadedDirectory = new ContentPathResolver(_settingsService.DirectoryPath)
            .GetActiveDirectory("killeropedia");
        var teachers = TeacherCatalogLoader.Load(
            downloadedDirectory is null ? null : Path.Combine(downloadedDirectory, "teachers.json.gz"));
        var quests = QuestCatalogLoader.Load(
            downloadedDirectory is null ? null : Path.Combine(downloadedDirectory, "quests.json"));
        var tattoos = TattooCatalogLoader.Load(
            downloadedDirectory is null ? null : Path.Combine(downloadedDirectory, "tattoos.json"));
        var lore = LoadLoreCatalog(downloadedDirectory);
        return new KilleropediaViewModel(
            teachers,
            _bookCatalogStore,
            RefreshBookCatalogAsync,
            ShowTeacherOnMap,
            lore,
            new ContentPathResolver(_settingsService.DirectoryPath).GetActiveDirectory("map"),
            quests,
            ShowBookLocationOnMap,
            tattoos,
            _rareCatalogStore,
            RefreshRareCatalogAsync,
            _abilityCaptureStore);
    }

    private LoreCatalogData LoadLoreCatalog(string? downloadedDirectory)
    {
        if (downloadedDirectory is not null)
        {
            var path = Path.Combine(downloadedDirectory, "lore-catalog.json.gz");
            try
            {
                if (File.Exists(path))
                {
                    return LoreCatalogLoader.LoadFile(path);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException)
            {
                // A damaged downloaded override falls back to the legacy override or embedded catalog.
            }
        }

        return LoreCatalogLoader.Load(_settingsService.DirectoryPath);
    }

    private static string ComponentVersions(IReadOnlyList<ContentComponentUpdate> components) =>
        string.Join(" i ", components.Select(component =>
            $"{ComponentDisplayName(component.Name)} {component.Version}"));

    private static string ComponentDisplayName(string name) => name.ToLowerInvariant() switch
    {
        "map" => "mapa",
        "killeropedia" => "Killeropedia",
        _ => name,
    };

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.#} MB"
        : $"{Math.Max(1, bytes / 1024d):0.#} KB";

    /// <summary>Internal (not private) so MapViewModel can reuse it for its own "Zgłoś
    /// znaczniki" report link, via the <see cref="MapViewModel.MainViewModel"/> back-reference,
    /// instead of duplicating the try/catch + failure toast here.</summary>
    internal void OpenExternalLink(Uri? uri)
    {
        if (uri is null)
        {
            return;
        }

        try
        {
            _externalLinkService.Open(uri);
        }
        catch (Exception exception)
        {
            // Opening an external page is user-requested, so report platform/browser failures.
            AddToast($"Nie udało się otworzyć linku: {exception.Message}", "error");
        }
    }

    public event Action<string>? OutputReceived;

    /// <summary>Raised for every line recognized as player communication (say, sayto, tell,
    /// clantell, grouptell, yell, shout — see <see cref="ChatLinePolicy"/>), independent of
    /// whether the Chat panel is currently open. The Chat panel appends it to its own console;
    /// the main window lights up the green taskbar-overlay badge if it isn't focused.</summary>
    public event Action<string>? ChatLineReceived;

    /// <summary>Raised whenever the GMCP "fighting" position starts (true) or stops (false) — the
    /// main window lights up the red taskbar-overlay badge while this is true and the window isn't
    /// focused, and clears it the moment this fires false, regardless of focus.</summary>
    public event Action<bool>? CombatStateChanged;

    /// <summary>Raised whenever a user-defined Trigger rule matches a line, or a Timer tick
    /// actually sends at least one command — carries a short human-readable description ("Trigger:
    /// „Nazwa”" / "Timer: „Nazwa”") used to light up the blue taskbar-overlay badge (main window,
    /// if unfocused). A Trigger match also flashes the matching entry itself on the timers/
    /// triggers status bar (see <see cref="FlashTriggerRecentlyFired"/>) rather than repeating its
    /// name here in text; a Timer firing still shows via <see cref="RecentAutomationActivityText"/>
    /// since a timer has no per-firing highlight of its own. Not raised for every internal command
    /// queued by other automation (auto-assist orders, auto-recast, ...), only for a genuine
    /// Trigger/Timer firing, matching what the player configured directly.</summary>
    public event Action<string>? AutomationFired;

    /// <summary>Raised when a profile becomes active; the view auto-connects then.</summary>
    public event Action<string>? ProfileActivated;

    // ========================================================================
    // Existing connection / command properties (preserved unchanged)
    // ========================================================================

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
            {
                RefreshCommands();
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>Text encoding used for the selected account's connection (see <see cref="MudTextEncodings"/>).</summary>
    public string Encoding
    {
        get => _encoding;
        set => SetProperty(ref _encoding, value);
    }

    /// <summary>Encodings offered in the account encoding picker.</summary>
    public IReadOnlyList<string> AvailableEncodings => MudTextEncodings.All;

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
            {
                _sendCommandCommand.NotifyCanExecuteChanged();
                RefreshCommandSuggestions();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                Killeropedia.SetConnectionState(value);
                RefreshCommands();
                if (value)
                {
                    HeaderAreaText = $"Połączono z {Host}:{Port}";
                }
                else
                {
                    _autoAssist.Reset();
                    _autoAssistCommandPending = false;
                    _autoAssistNpcPending = false;
                    _autoKillPending = false;
                    HeaderAreaText = "--- Rozłączono ---";
                    Killeropedia.SetCharacterLevel(null);
                }
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public ObservableCollection<GmcpEntryViewModel> GmcpMessages { get; } = [];

    public ObservableCollection<GmcpEntryViewModel> SentGmcpMessages { get; } = [];

    public IAsyncRelayCommand ConnectCommand => _connectCommand;
    public IAsyncRelayCommand DisconnectCommand => _disconnectCommand;
    public IAsyncRelayCommand SendCommandCommand => _sendCommandCommand;
    public IAsyncRelayCommand RetryStartupCommand => _retryStartupCommand;

    public bool HasStartupError => !string.IsNullOrWhiteSpace(StartupErrorMessage);

    public string? StartupErrorMessage
    {
        get => _startupErrorMessage;
        private set
        {
            if (SetProperty(ref _startupErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasStartupError));
            }
        }
    }

    public string? StartupErrorDetails
    {
        get => _startupErrorDetails;
        private set => SetProperty(ref _startupErrorDetails, value);
    }

    // ========================================================================
    // New UI properties
    // ========================================================================

    public string HeaderAreaText
    {
        get => _headerAreaText;
        private set => SetProperty(ref _headerAreaText, value);
    }

    public bool IsKilleropediaOpen
    {
        get => _isKilleropediaOpen;
        set => SetProperty(ref _isKilleropediaOpen, value);
    }

    public string IdleTimeText
    {
        get => _idleTimeText;
        private set => SetProperty(ref _idleTimeText, value);
    }

    internal void RefreshIdleTime()
    {
        var timestamp = Interlocked.Read(ref _lastCommandSentTimestamp);
        IdleTimeText = timestamp == 0
            ? "Idle: —"
            : FormatIdleTime(Stopwatch.GetElapsedTime(timestamp));
    }

    internal static string FormatIdleTime(TimeSpan idleTime)
    {
        var totalHours = Math.Max(0, (long)idleTime.TotalHours);
        return $"Idle: {totalHours:00}:{idleTime.Minutes:00}:{idleTime.Seconds:00}";
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set => SetProperty(ref _isHelpOpen, value);
    }

    public int SelectedRightTab
    {
        get => _selectedRightTab;
        set => SetProperty(ref _selectedRightTab, value);
    }

    public string NewNoteTitle
    {
        get => _newNoteTitle;
        set => SetProperty(ref _newNoteTitle, value);
    }

    public string NewNoteContent
    {
        get => _newNoteContent;
        set => SetProperty(ref _newNoteContent, value);
    }

    /// <summary>True = the new note is shared by all profiles.</summary>
    public bool NewNoteIsGlobal
    {
        get => _newNoteIsGlobal;
        set => SetProperty(ref _newNoteIsGlobal, value);
    }

    public bool IsEditingNote => _editedNote is not null;

    /// <summary>Backs the note form Expander (two-way); editing a note opens it.</summary>
    public bool IsNoteFormExpanded
    {
        get => _isNoteFormExpanded;
        set => SetProperty(ref _isNoteFormExpanded, value);
    }

    public string NoteFormButtonText => IsEditingNote ? "Zapisz zmiany" : "Dodaj notatkę";

    public string NoteFormHeader => IsEditingNote ? "✎ Edytuj notatkę" : "＋ Nowa notatka";

    // ========================================================================
    // App settings (system-wide, not per profile)
    // ========================================================================

    public ObservableCollection<string> AvailableFonts { get; } = [];
    public IReadOnlyList<string> AvailableTelnetColorSchemes => AnsiColorPalette.Names;

    public double MinOutputFontSize => AppSettings.MinOutputFontSize;
    public double MaxOutputFontSize => AppSettings.MaxOutputFontSize;
    public double MinWidgetFontSize => AppSettings.MinWidgetFontSize;
    public double MaxWidgetFontSize => AppSettings.MaxWidgetFontSize;

    /// <summary>Font family name for MUD output in the main screen.</summary>
    public string OutputFontFamily
    {
        get => _settings.OutputFontFamily;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _settings.OutputFontFamily == value)
            {
                return;
            }

            _settings.OutputFontFamily = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OutputFontFamilyValue));
            SaveSettings();
        }
    }

    public double OutputFontSize
    {
        get => _settings.OutputFontSize;
        set
        {
            var clamped = Math.Clamp(
                Math.Round(value), AppSettings.MinOutputFontSize, AppSettings.MaxOutputFontSize);
            if (Math.Abs(_settings.OutputFontSize - clamped) < 0.1)
            {
                return;
            }

            _settings.OutputFontSize = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OutputFontSizeText));
            SaveSettings();
        }
    }

    public string OutputFontSizeText => $"{_settings.OutputFontSize:0} px";

    public FontFamily OutputFontFamilyValue => AppFonts.Resolve(_settings.OutputFontFamily);

    public bool OutputFontBold
    {
        get => _settings.OutputFontBold;
        set
        {
            if (_settings.OutputFontBold == value)
            {
                return;
            }

            _settings.OutputFontBold = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OutputFontWeight));
            SaveSettings();
        }
    }

    public FontWeight OutputFontWeight => OutputFontBold ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>Font family shared by all dockable widgets except the terminal.</summary>
    public string WidgetFontFamily
    {
        get => _settings.WidgetFontFamily;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _settings.WidgetFontFamily == value)
            {
                return;
            }

            _settings.WidgetFontFamily = value;
            ApplyWidgetFontResources();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WidgetFontFamilyValue));
            SaveSettings();
        }
    }

    public double WidgetFontSize
    {
        get => _settings.WidgetFontSize;
        set
        {
            var clamped = Math.Clamp(
                Math.Round(value), AppSettings.MinWidgetFontSize, AppSettings.MaxWidgetFontSize);
            if (Math.Abs(_settings.WidgetFontSize - clamped) < 0.1)
            {
                return;
            }

            _settings.WidgetFontSize = clamped;
            ApplyWidgetFontResources();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WidgetFontSizeText));
            SaveSettings();
        }
    }

    public string WidgetFontSizeText => $"{_settings.WidgetFontSize:0} px";

    public FontFamily WidgetFontFamilyValue => AppFonts.Resolve(_settings.WidgetFontFamily);

    public bool WidgetFontBold
    {
        get => _settings.WidgetFontBold;
        set
        {
            if (_settings.WidgetFontBold == value)
            {
                return;
            }

            _settings.WidgetFontBold = value;
            ApplyWidgetFontResources();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WidgetFontWeight));
            SaveSettings();
        }
    }

    public FontWeight WidgetFontWeight => WidgetFontBold ? FontWeight.Bold : FontWeight.Normal;

    public double MinTerminalOverlayOpacity => AppSettings.MinTerminalOverlayOpacity;

    public double MaxTerminalOverlayOpacity => AppSettings.MaxTerminalOverlayOpacity;

    /// <summary>Shared transparency for every panel pinned as a Terminal overlay — one setting
    /// for all of them rather than per-panel, set from the general Settings panel.</summary>
    public double TerminalOverlayOpacity
    {
        get => _settings.TerminalOverlayOpacity;
        set
        {
            var clamped = Math.Clamp(
                value, AppSettings.MinTerminalOverlayOpacity, AppSettings.MaxTerminalOverlayOpacity);
            if (Math.Abs(_settings.TerminalOverlayOpacity - clamped) < 0.001)
            {
                return;
            }

            _settings.TerminalOverlayOpacity = clamped;
            ApplyTerminalOverlayOpacityResource();
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool OutputWordWrap
    {
        get => _profileSettings.OutputWordWrap;
        set
        {
            if (_profileSettings.OutputWordWrap == value)
            {
                return;
            }

            _profileSettings.OutputWordWrap = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool ShowTerminalVitalsBars
    {
        get => _profileSettings.ShowTerminalVitalsBars;
        set
        {
            if (_profileSettings.ShowTerminalVitalsBars == value)
            {
                return;
            }

            _profileSettings.ShowTerminalVitalsBars = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool ShowNumericDamageEnabled
    {
        get => _profileSettings.ShowNumericDamageEnabled;
        set
        {
            if (_profileSettings.ShowNumericDamageEnabled == value)
            {
                return;
            }

            _profileSettings.ShowNumericDamageEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AnnotateRandomBookClassEnabled
    {
        get => _profileSettings.AnnotateRandomBookClassEnabled;
        set
        {
            if (_profileSettings.AnnotateRandomBookClassEnabled == value)
            {
                return;
            }

            _profileSettings.AnnotateRandomBookClassEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AnnotateSkillTrainersEnabled
    {
        get => _profileSettings.AnnotateSkillTrainersEnabled;
        set
        {
            if (_profileSettings.AnnotateSkillTrainersEnabled == value)
            {
                return;
            }

            _profileSettings.AnnotateSkillTrainersEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AnnotateSpellSourcesEnabled
    {
        get => _profileSettings.AnnotateSpellSourcesEnabled;
        set
        {
            if (_profileSettings.AnnotateSpellSourcesEnabled == value)
            {
                return;
            }

            _profileSettings.AnnotateSpellSourcesEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AnnotateScoreEnabled
    {
        get => _profileSettings.AnnotateScoreEnabled;
        set
        {
            if (_profileSettings.AnnotateScoreEnabled == value)
            {
                return;
            }

            _profileSettings.AnnotateScoreEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool ClearCommandInputAfterSend
    {
        get => _profileSettings.ClearCommandInputAfterSend;
        set
        {
            if (_profileSettings.ClearCommandInputAfterSend == value)
            {
                return;
            }

            _profileSettings.ClearCommandInputAfterSend = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>Basic (default): each effect shows only its name. Extended: name plus its
    /// count/duration and description — see EffectsPanelView.</summary>
    public bool ShowExtendedEffects
    {
        get => _profileSettings.ShowExtendedEffects;
        set
        {
            if (_profileSettings.ShowExtendedEffects == value)
            {
                return;
            }

            _profileSettings.ShowExtendedEffects = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public string TelnetColorScheme
    {
        get => _settings.TelnetColorScheme;
        set
        {
            if (!AnsiColorPalette.IsKnown(value) || _settings.TelnetColorScheme == value)
            {
                return;
            }

            _settings.TelnetColorScheme = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>
    /// Separator character for command stacking (e.g. ";").  Commands typed
    /// by the user, alias replacements, trigger actions, and timer commands
    /// are split on newlines and on this separator.  Empty disables stacking
    /// (only newlines remain).
    /// </summary>
    public string CommandStackingSeparator
    {
        get => _settings.CommandStackingSeparator;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (_settings.CommandStackingSeparator == trimmed)
            {
                return;
            }

            _settings.CommandStackingSeparator = trimmed;
            OnPropertyChanged();
            SaveSettings();

            // Re-sync all running timers so their callback closures pick up the new
            // separator; timer command splitting depends on the current separator.
            SyncAllTimers();
        }
    }

    public bool AutoAssistEnabled
    {
        get => _profileSettings.AutoAssistEnabled;
        set
        {
            if (_profileSettings.AutoAssistEnabled == value)
            {
                return;
            }

            _profileSettings.AutoAssistEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
            TryAutoAssist();
        }
    }

    /// <summary>The command autoassist sends to enter combat — see
    /// <see cref="ProfileAutomationSettings.AutoAssistCommandTemplate"/>. Only affects what gets
    /// sent, not when, so unlike <see cref="AutoAssistEnabled"/> this doesn't re-run
    /// <see cref="TryAutoAssist"/>.</summary>
    public string AutoAssistCommandTemplate
    {
        get => _profileSettings.AutoAssistCommandTemplate;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? "as" : value.Trim();
            if (string.Equals(_profileSettings.AutoAssistCommandTemplate, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _profileSettings.AutoAssistCommandTemplate = trimmed;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public string AutoAssistExcludedMobNamesText
    {
        get => string.Join(Environment.NewLine, _profileSettings.AutoAssistExcludedMobNames);
        set
        {
            var names = ParseMobNameLines(value);
            if (_profileSettings.AutoAssistExcludedMobNames.SequenceEqual(names, StringComparer.Ordinal))
            {
                return;
            }

            _profileSettings.AutoAssistExcludedMobNames = names;
            OnPropertyChanged();
            SaveActiveProfile();
            TryAutoAssist();
        }
    }

    public string AutoAssistFollowUpCommands
    {
        get => _profileSettings.AutoAssistFollowUpCommands;
        set
        {
            var commands = value ?? string.Empty;
            if (string.Equals(_profileSettings.AutoAssistFollowUpCommands, commands, StringComparison.Ordinal))
            {
                return;
            }

            _profileSettings.AutoAssistFollowUpCommands = commands;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool GroupOrdersEnabled
    {
        get => _profileSettings.GroupOrdersEnabled;
        set
        {
            if (_profileSettings.GroupOrdersEnabled == value)
            {
                return;
            }

            _profileSettings.GroupOrdersEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>See <see cref="ProfileAutomationSettings.RemoteControlEnabled"/> — executes a
    /// "!"-prefixed say from <see cref="RemoteControlCharacterName"/> as a literal command,
    /// without needing that character (or this one) to be the group's formal leader.</summary>
    public bool RemoteControlEnabled
    {
        get => _profileSettings.RemoteControlEnabled;
        set
        {
            if (_profileSettings.RemoteControlEnabled == value)
            {
                return;
            }

            _profileSettings.RemoteControlEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public string RemoteControlCharacterName
    {
        get => _profileSettings.RemoteControlCharacterName;
        set
        {
            var name = (value ?? string.Empty).Trim();
            if (string.Equals(_profileSettings.RemoteControlCharacterName, name, StringComparison.Ordinal))
            {
                return;
            }

            _profileSettings.RemoteControlCharacterName = name;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoRecastOnLeaderSnapEnabled
    {
        get => _profileSettings.AutoRecastOnLeaderSnapEnabled;
        set
        {
            if (_profileSettings.AutoRecastOnLeaderSnapEnabled == value)
            {
                return;
            }

            _profileSettings.AutoRecastOnLeaderSnapEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public string AutoRecastOnLeaderSnapCommandsText
    {
        get => _profileSettings.AutoRecastOnLeaderSnapCommandsText;
        set
        {
            var commands = value ?? string.Empty;
            if (string.Equals(_profileSettings.AutoRecastOnLeaderSnapCommandsText, commands, StringComparison.Ordinal))
            {
                return;
            }

            _profileSettings.AutoRecastOnLeaderSnapCommandsText = commands;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoFollowLeaderEnabled
    {
        get => _profileSettings.AutoFollowLeaderEnabled;
        set
        {
            if (_profileSettings.AutoFollowLeaderEnabled == value)
            {
                return;
            }

            _profileSettings.AutoFollowLeaderEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>Mirrors the GMCP-reported leader's stand/sit/rest state — see
    /// <see cref="ShouldMirrorLeaderPosition"/>.</summary>
    public bool AutoMirrorLeaderPositionEnabled
    {
        get => _profileSettings.AutoMirrorLeaderPositionEnabled;
        set
        {
            if (_profileSettings.AutoMirrorLeaderPositionEnabled == value)
            {
                return;
            }

            _profileSettings.AutoMirrorLeaderPositionEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutowalkMovementRecoveryEnabled
    {
        get => _profileSettings.AutowalkMovementRecoveryEnabled;
        set
        {
            if (_profileSettings.AutowalkMovementRecoveryEnabled == value)
            {
                return;
            }

            _profileSettings.AutowalkMovementRecoveryEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutowalkRestOnArrivalEnabled
    {
        get => _profileSettings.AutowalkRestOnArrivalEnabled;
        set
        {
            if (_profileSettings.AutowalkRestOnArrivalEnabled == value)
            {
                return;
            }

            _profileSettings.AutowalkRestOnArrivalEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoStandOrderEnabled
    {
        get => _profileSettings.AutoStandOrderEnabled;
        set
        {
            if (_profileSettings.AutoStandOrderEnabled == value)
            {
                return;
            }

            _profileSettings.AutoStandOrderEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoRestOrderEnabled
    {
        get => _profileSettings.AutoRestOrderEnabled;
        set
        {
            if (_profileSettings.AutoRestOrderEnabled == value)
            {
                return;
            }

            _profileSettings.AutoRestOrderEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoGroupRefreshOnExhaustedEnabled
    {
        get => _profileSettings.AutoGroupRefreshOnExhaustedEnabled;
        set
        {
            if (_profileSettings.AutoGroupRefreshOnExhaustedEnabled == value)
            {
                return;
            }

            _profileSettings.AutoGroupRefreshOnExhaustedEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoAssistNpcEnabled
    {
        get => _profileSettings.AutoAssistNpcEnabled;
        set
        {
            if (_profileSettings.AutoAssistNpcEnabled == value)
            {
                return;
            }

            _profileSettings.AutoAssistNpcEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutoStandOnLyingEnabled
    {
        get => _profileSettings.AutoStandOnLyingEnabled;
        set
        {
            if (_profileSettings.AutoStandOnLyingEnabled == value)
            {
                return;
            }

            _profileSettings.AutoStandOnLyingEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public bool AutowieldEnabled
    {
        get => _profileSettings.AutowieldEnabled;
        set
        {
            if (_profileSettings.AutowieldEnabled == value)
            {
                return;
            }

            _profileSettings.AutowieldEnabled = value;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public string AutowieldWeaponName
    {
        get => _profileSettings.AutowieldWeaponName;
        set
        {
            var trimmed = value.Trim();
            if (_profileSettings.AutowieldWeaponName == trimmed)
            {
                return;
            }

            _profileSettings.AutowieldWeaponName = trimmed;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public int MinAutowalkLowMovementThresholdPercent => AppSettings.MinAutowalkLowMovementThresholdPercent;

    public int MaxAutowalkLowMovementThresholdPercent => AppSettings.MaxAutowalkLowMovementThresholdPercent;

    public int AutowalkLowMovementThresholdPercent
    {
        get => _settings.AutowalkLowMovementThresholdPercent;
        set
        {
            var clamped = Math.Clamp(
                value,
                AppSettings.MinAutowalkLowMovementThresholdPercent,
                AppSettings.MaxAutowalkLowMovementThresholdPercent);
            if (_settings.AutowalkLowMovementThresholdPercent == clamped)
            {
                return;
            }

            _settings.AutowalkLowMovementThresholdPercent = clamped;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public int MinAutowalkRestSeconds => AppSettings.MinAutowalkRestSeconds;

    public int MaxAutowalkRestSeconds => AppSettings.MaxAutowalkRestSeconds;

    public int AutowalkRestSeconds
    {
        get => _settings.AutowalkRestSeconds;
        set
        {
            var clamped = Math.Clamp(
                value, AppSettings.MinAutowalkRestSeconds, AppSettings.MaxAutowalkRestSeconds);
            if (_settings.AutowalkRestSeconds == clamped)
            {
                return;
            }

            _settings.AutowalkRestSeconds = clamped;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>Plays a short Windows notification sound (see
    /// <see cref="NotificationSoundPlayer"/>) for every line the Chat panel mirrors — see the
    /// chat-line branch in <see cref="OnLineReceived"/>. Independent of any single trigger's own
    /// <see cref="AutomationRuleEntry.PlaySoundOnMatch"/>.</summary>
    public bool ChatSoundOnNewMessageEnabled
    {
        get => _settings.ChatSoundOnNewMessageEnabled;
        set
        {
            if (_settings.ChatSoundOnNewMessageEnabled == value)
            {
                return;
            }

            _settings.ChatSoundOnNewMessageEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>Backs the "Pokaż na pasku" checkbox on the Timery tab — hides the active-timers
    /// strip on the terminal's timer/skill-cooldown bar (see TerminalPanelView) without touching
    /// any timer's own enabled state. On by default.</summary>
    public bool ShowTimersOnStatusBarEnabled
    {
        get => _settings.ShowTimersOnStatusBarEnabled;
        set
        {
            if (_settings.ShowTimersOnStatusBarEnabled == value)
            {
                return;
            }

            _settings.ShowTimersOnStatusBarEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>Backs the "Pokaż na pasku" checkbox on the Triggery tab — hides the
    /// enabled-triggers strip on the same bar as <see cref="ShowTimersOnStatusBarEnabled"/>
    /// without touching any trigger's own enabled state. On by default.</summary>
    public bool ShowTriggersOnStatusBarEnabled
    {
        get => _settings.ShowTriggersOnStatusBarEnabled;
        set
        {
            if (_settings.ShowTriggersOnStatusBarEnabled == value)
            {
                return;
            }

            _settings.ShowTriggersOnStatusBarEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool LordModeEnabled
    {
        get => _profileSettings.LordModeEnabled;
        set
        {
            if (_profileSettings.LordModeEnabled == value)
            {
                return;
            }

            _profileSettings.LordModeEnabled = value;
            Map.LordModeEnabled = value;
            OnPropertyChanged();
            LordGotoGroupRoomCommand.NotifyCanExecuteChanged();
            LordGotoGroupMemberCommand.NotifyCanExecuteChanged();
            SaveActiveProfile();
        }
    }

    public RelayCommand ResetOutputFontCommand => new(() =>
    {
        OutputFontFamily = AppSettings.DefaultOutputFontFamily;
        OutputFontSize = AppSettings.DefaultOutputFontSize;
        OutputFontBold = false;
    });

    public RelayCommand ResetWidgetFontCommand => new(() =>
    {
        WidgetFontFamily = AppSettings.DefaultWidgetFontFamily;
        WidgetFontSize = AppSettings.DefaultWidgetFontSize;
        WidgetFontBold = false;
    });

    private void ApplyWidgetFontResources()
    {
        if (Avalonia.Application.Current is not { } application)
        {
            return;
        }

        application.Resources["WidgetFontFamilyResource"] = WidgetFontFamilyValue;
        application.Resources["WidgetFontSizeResource"] = _settings.WidgetFontSize;
        application.Resources["WidgetFontWeightResource"] = WidgetFontWeight;
    }

    private void ApplyTerminalOverlayOpacityResource()
    {
        if (Avalonia.Application.Current is not { } application)
        {
            return;
        }

        application.Resources["TerminalOverlayOpacityResource"] = _settings.TerminalOverlayOpacity;
    }

    private void SaveSettings()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception exception)
        {
            AddToast($"Nie udało się zapisać ustawień: {exception.Message}", "error");
        }
    }

    /// <summary>One-time migration source for a profile that predates per-profile automation
    /// settings (its file has no "Automation" block): reads the legacy shared settings.json
    /// through the lens of <see cref="ProfileAutomationSettings"/> instead of resetting to
    /// defaults. Property names match the old <see cref="AppSettings"/> fields exactly, so
    /// deserializing the same file with the new type just works — extra keys on the old file
    /// (fonts, colors, ...) are ignored, and any key genuinely missing falls back to
    /// <see cref="ProfileAutomationSettings"/>'s own defaults. Cached for the life of this
    /// instance since the file no longer changes once every profile has migrated off it.</summary>
    private ProfileAutomationSettings LoadLegacyAutomationSettingsSeed()
    {
        if (_legacyAutomationSettingsSeed is { } cached)
        {
            return cached;
        }

        ProfileAutomationSettings seed;
        try
        {
            var path = Path.Combine(_settingsService.DirectoryPath, "settings.json");
            seed = File.Exists(path)
                ? JsonSerializer.Deserialize<ProfileAutomationSettings>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            seed = new();
        }

        seed.AutoAssistExcludedMobNames = seed.AutoAssistExcludedMobNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _legacyAutomationSettingsSeed = seed;
        return seed;
    }

    /// <summary>Pushes <see cref="_profileSettings"/> onto the handful of Map-hosted mirrors that
    /// aren't plain wrapper properties on this view model (see the constructor's seeding of
    /// <see cref="Map"/> for why these six exist there instead).</summary>
    private void ApplyProfileSettingsToMap()
    {
        Map.LordModeEnabled = _profileSettings.LordModeEnabled;
        Map.ShowGroupMembersAsNumbers = _profileSettings.ShowGroupMembersAsNumbers;
        Map.AutoWalkOnMapDoubleClick = _profileSettings.AutoWalkOnMapDoubleClick;
        Map.AutoScanOnRoomEnter = _profileSettings.AutoScanOnRoomEnterEnabled;
        Map.AutoKillOnRoomEnter = _profileSettings.AutoKillOnRoomEnterEnabled;
        Map.AutoKillMobNamesText = string.Join(Environment.NewLine, _profileSettings.AutoKillMobNames);
    }

    /// <summary>Raises change notifications for every wrapper property backed by
    /// <see cref="_profileSettings"/>, after <see cref="ActivateProfile"/> swaps it wholesale —
    /// a plain field assignment doesn't go through any property setter, so bound UI (Ustawienia
    /// panel checkboxes, mob name text boxes, ...) would otherwise keep showing the previous
    /// profile's values until something else happened to touch them.</summary>
    private void NotifyProfileSettingsChanged()
    {
        OnPropertyChanged(nameof(OutputWordWrap));
        OnPropertyChanged(nameof(ShowTerminalVitalsBars));
        OnPropertyChanged(nameof(ShowNumericDamageEnabled));
        OnPropertyChanged(nameof(AnnotateRandomBookClassEnabled));
        OnPropertyChanged(nameof(AnnotateSkillTrainersEnabled));
        OnPropertyChanged(nameof(AnnotateSpellSourcesEnabled));
        OnPropertyChanged(nameof(AnnotateScoreEnabled));
        OnPropertyChanged(nameof(ClearCommandInputAfterSend));
        OnPropertyChanged(nameof(AutoAssistEnabled));
        OnPropertyChanged(nameof(AutoAssistCommandTemplate));
        OnPropertyChanged(nameof(AutoAssistExcludedMobNamesText));
        OnPropertyChanged(nameof(AutoAssistFollowUpCommands));
        OnPropertyChanged(nameof(GroupOrdersEnabled));
        OnPropertyChanged(nameof(AutoRecastOnLeaderSnapEnabled));
        OnPropertyChanged(nameof(AutoRecastOnLeaderSnapCommandsText));
        OnPropertyChanged(nameof(ShowExtendedEffects));
        OnPropertyChanged(nameof(AutowalkMovementRecoveryEnabled));
        OnPropertyChanged(nameof(AutowalkRestOnArrivalEnabled));
        OnPropertyChanged(nameof(AutoStandOrderEnabled));
        OnPropertyChanged(nameof(AutoRestOrderEnabled));
        OnPropertyChanged(nameof(AutoFollowLeaderEnabled));
        OnPropertyChanged(nameof(AutoMirrorLeaderPositionEnabled));
        OnPropertyChanged(nameof(AutoGroupRefreshOnExhaustedEnabled));
        OnPropertyChanged(nameof(AutoAssistNpcEnabled));
        OnPropertyChanged(nameof(AutoStandOnLyingEnabled));
        OnPropertyChanged(nameof(AutowieldEnabled));
        OnPropertyChanged(nameof(AutowieldWeaponName));
        OnPropertyChanged(nameof(LordModeEnabled));
    }

    private void PopulateAvailableFonts()
    {
        var fonts = new List<string>();
        try
        {
            fonts = Avalonia.Media.FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // Headless environment (e.g. unit tests) — fall back to a curated list.
        }

        if (fonts.Count == 0)
        {
            fonts =
            [
                "Cascadia Mono", "Consolas", "Courier New", "Fira Code",
                "JetBrains Mono", "Lucida Console", "Segoe UI", "Verdana",
            ];
        }

        if (!fonts.Contains(_settings.OutputFontFamily))
        {
            fonts.Insert(0, _settings.OutputFontFamily);
        }

        if (!fonts.Contains(_settings.WidgetFontFamily))
        {
            fonts.Insert(0, _settings.WidgetFontFamily);
        }

        if (!fonts.Contains(AppFonts.OpenDyslexicName, StringComparer.OrdinalIgnoreCase))
        {
            fonts.Add(AppFonts.OpenDyslexicName);
            fonts.Sort(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var font in fonts)
        {
            AvailableFonts.Add(font);
        }
    }

    // ========================================================================
    // Aliases & triggers (regex-based, saved per profile)
    // ========================================================================

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand StartAddAliasCommand { get; }
    public RelayCommand StartAddTriggerCommand { get; }
    public RelayCommand<AutomationRuleEntry> DeleteRuleCommand { get; }
    public RelayCommand<AutomationRuleEntry> ToggleRuleCommand { get; }
    public RelayCommand<AutomationRuleEntry> EditRuleCommand { get; }
    public RelayCommand CancelRuleEditCommand { get; }
    public RelayCommand TestRuleScriptCommand { get; }

    /// <summary>Shared by the Timery/Aliasy/Triggery lists: hides each entry's always-visible
    /// pattern/action (or command list) preview, showing only name + toggle + badges, so a long
    /// list of rules is easier to scan — especially in the narrow COMPACT layout. The full detail
    /// is still one tooltip-hover or edit-click away, never actually removed.</summary>
    public bool IsAutomationCompactView
    {
        get => _isAutomationCompactView;
        set => SetProperty(ref _isAutomationCompactView, value);
    }

    public bool IsEditingRule => _editedRule is not null;

    /// <summary>Backs the rule form Expander (two-way); editing a rule opens it.</summary>
    public bool IsRuleFormExpanded
    {
        get => _isRuleFormExpanded;
        set
        {
            if (SetProperty(ref _isRuleFormExpanded, value))
            {
                OnPropertyChanged(nameof(IsAliasRuleFormVisible));
                OnPropertyChanged(nameof(IsTriggerRuleFormVisible));
            }
        }
    }

    public bool IsAliasRuleFormVisible => IsRuleFormExpanded && NewRuleIsAlias;

    public bool IsTriggerRuleFormVisible => IsRuleFormExpanded && !NewRuleIsAlias;

    public int SelectedAutomationTabIndex
    {
        get => _selectedAutomationTabIndex;
        set => SetProperty(ref _selectedAutomationTabIndex, value);
    }

    public string RuleFormButtonText => IsEditingRule
        ? "Zapisz zmiany"
        : NewRuleIsAlias ? "Dodaj alias" : "Dodaj trigger";

    public string RuleFormHeader => IsEditingRule
        ? NewRuleIsAlias ? "✎ Edytuj alias" : "✎ Edytuj trigger"
        : NewRuleIsAlias ? "＋ Nowy alias" : "＋ Nowy trigger";

    public string NewRuleName
    {
        get => _newRuleName;
        set
        {
            if (SetProperty(ref _newRuleName, value))
            {
                AddRuleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>"alias" or "trigger".</summary>
    public string NewRuleType
    {
        get => _newRuleType;
        set
        {
            if (SetProperty(ref _newRuleType, value))
            {
                OnPropertyChanged(nameof(NewRuleIsAlias));
                OnPropertyChanged(nameof(NewRuleIsTrigger));
                OnPropertyChanged(nameof(RuleFormButtonText));
                OnPropertyChanged(nameof(RuleFormHeader));
                OnPropertyChanged(nameof(IsAliasRuleFormVisible));
                OnPropertyChanged(nameof(IsTriggerRuleFormVisible));
            }
        }
    }

    public bool NewRuleIsAlias => NewRuleType == "alias";

    /// <summary>Gates the "odtwórz dźwięk przy dopasowaniu" checkbox in the shared rule editor
    /// (see <see cref="NewRulePlaySoundOnMatch"/>) — RuleEditorTemplate is shared between aliases
    /// and triggers, and the option only makes sense for the latter.</summary>
    public bool NewRuleIsTrigger => NewRuleType == "trigger";

    /// <summary>.NET regex tested against typed commands (alias) or received lines (trigger).</summary>
    public string NewRulePattern
    {
        get => _newRulePattern;
        set
        {
            if (SetProperty(ref _newRulePattern, value))
            {
                NewRulePatternError = ValidatePattern(value);
                AddRuleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Command to send; may use capture groups like $1.</summary>
    public string NewRuleAction
    {
        get => _newRuleAction;
        set
        {
            if (SetProperty(ref _newRuleAction, value))
            {
                AddRuleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True = the new/edited rule is shared by all profiles.</summary>
    public bool NewRuleIsGlobal
    {
        get => _newRuleIsGlobal;
        set => SetProperty(ref _newRuleIsGlobal, value);
    }

    /// <summary>True = <see cref="NewRuleAction"/> is Lua source (run via <see cref="_lua"/>)
    /// instead of a "$1"-style replacement/command template.</summary>
    public bool NewRuleIsScript
    {
        get => _newRuleIsScript;
        set
        {
            if (SetProperty(ref _newRuleIsScript, value))
            {
                OnPropertyChanged(nameof(NewRuleActionLabel));
                OnPropertyChanged(nameof(NewRuleActionPlaceholder));
                TestRuleScriptCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewRuleActionLabel => NewRuleIsScript
        ? "Akcja — skrypt Lua (send(\"komenda\"), matches[1], hp, mv, roomname…)"
        : "Akcja — komendy w osobnych liniach; grupy: $1, $2…";

    public string NewRuleActionPlaceholder => NewRuleIsScript
        ? "if hp and hp < 50 then\n  send(\"pij miksture\")\nend\nsend(\"atakuj \" .. matches[1])"
        : "np.\nrzuc 'leczenie' $1\npij miksture";

    /// <summary>Trigger-only option (see <see cref="AutomationRuleEntry.PlaySoundOnMatch"/>) —
    /// meaningless for an alias, which fires on typed input rather than server output; the editor
    /// hides this checkbox via <see cref="NewRuleIsTrigger"/> when editing/adding an alias.</summary>
    public bool NewRulePlaySoundOnMatch
    {
        get => _newRulePlaySoundOnMatch;
        set => SetProperty(ref _newRulePlaySoundOnMatch, value);
    }

    /// <summary>Sample input for "Testuj" — the typed command (alias) or MUD line (trigger) to
    /// match <see cref="NewRulePattern"/> against before running <see cref="NewRuleAction"/> as
    /// Lua, so a script's <c>matches</c>/<c>line</c> can be tried without actually triggering it.</summary>
    public string NewRuleTestInput
    {
        get => _newRuleTestInput;
        set => SetProperty(ref _newRuleTestInput, value);
    }

    /// <summary>Result of the last "Testuj" run — the commands the script would <c>send()</c>, a
    /// "pattern didn't match" message, or a Lua error. Null before the button is ever pressed.</summary>
    public string? NewRuleTestOutput
    {
        get => _newRuleTestOutput;
        private set => SetProperty(ref _newRuleTestOutput, value);
    }

    /// <summary>
    /// Runs <see cref="NewRuleAction"/> as Lua against <see cref="NewRuleTestInput"/>, exactly the
    /// way a real firing would (pattern match first, then the shared Lua engine — same globals,
    /// same library, same persistent state) — so testing a script that mutates a global counter
    /// really does mutate it, same as if it had actually fired. That's a deliberate trade-off:
    /// it's what makes testing meaningful against library functions and live game state, at the
    /// cost of not being side-effect-free.
    /// </summary>
    private void TestRuleScript()
    {
        if (!NewRuleIsScript)
        {
            return;
        }

        Match? match = null;
        if (!string.IsNullOrWhiteSpace(NewRulePattern))
        {
            Regex regex;
            try
            {
                regex = new Regex(NewRulePattern);
            }
            catch (ArgumentException exception)
            {
                NewRuleTestOutput = $"Nieprawidłowy wzorzec: {exception.Message}";
                return;
            }

            var candidate = regex.Match(NewRuleTestInput);
            if (!candidate.Success)
            {
                NewRuleTestOutput = "Wzorzec nie pasuje do testowanego tekstu — skrypt by się nie odpalił.";
                return;
            }

            match = candidate;
        }

        try
        {
            var commands = _lua.Run(NewRuleAction, NewRuleTestInput, match);
            NewRuleTestOutput = commands.Count > 0
                ? string.Join(Environment.NewLine, commands.Select(command => $"→ {command}"))
                : "(skrypt nie wywołał send() — brak komend)";
        }
        catch (MoonSharp.Interpreter.InterpreterException exception)
        {
            NewRuleTestOutput = $"Błąd: {exception.DecoratedMessage ?? exception.Message}";
        }
    }

    /// <summary>Live regex validation message, or null when the pattern is valid.</summary>
    public string? NewRulePatternError
    {
        get => _newRulePatternError;
        private set
        {
            if (SetProperty(ref _newRulePatternError, value))
            {
                OnPropertyChanged(nameof(HasNewRulePatternError));
            }
        }
    }

    public bool HasNewRulePatternError => NewRulePatternError is not null;

    private static string? ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            _ = new Regex(pattern);
            return null;
        }
        catch (ArgumentException exception)
        {
            return $"Nieprawidłowy regex: {exception.Message}";
        }
    }

    private bool CanAddRule() =>
        !string.IsNullOrWhiteSpace(NewRuleName) &&
        !string.IsNullOrWhiteSpace(NewRulePattern) &&
        !string.IsNullOrWhiteSpace(NewRuleAction) &&
        ValidatePattern(NewRulePattern) is null;

    private void AddRule()
    {
        if (!CanAddRule())
        {
            return;
        }

        if (_editedRule is { } edited)
        {
            edited.Name = NewRuleName.Trim();
            edited.Type = NewRuleType;
            edited.Pattern = NewRulePattern;
            edited.Action = NewRuleAction;
            edited.IsGlobal = NewRuleIsGlobal;
            edited.IsScript = NewRuleIsScript;
            edited.PlaySoundOnMatch = NewRuleIsTrigger && NewRulePlaySoundOnMatch;
        }
        else
        {
            AutomationRules.Add(new AutomationRuleEntry(
                NewRuleName.Trim(), NewRuleType, NewRulePattern, NewRuleAction,
                isEnabled: true, isGlobal: NewRuleIsGlobal, isScript: NewRuleIsScript,
                playSoundOnMatch: NewRuleIsTrigger && NewRulePlaySoundOnMatch));
        }

        ClearRuleForm();
        RebuildRuleViews();
        RebuildFolderTrees();
        ApplyAutomation();
        SaveActiveProfile();
    }

    private void EditRule(AutomationRuleEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (_editedRule is { } previouslyEdited)
        {
            previouslyEdited.IsEditing = false;
        }

        _editedRule = entry;
        entry.IsEditing = true;
        NewRuleName = entry.Name;
        NewRuleType = entry.Type;
        NewRulePattern = entry.Pattern;
        NewRuleAction = entry.Action;
        NewRuleIsGlobal = entry.IsGlobal;
        NewRuleIsScript = entry.IsScript;
        NewRulePlaySoundOnMatch = entry.PlaySoundOnMatch;
        NewRuleTestInput = string.Empty;
        NewRuleTestOutput = null;
        SelectedAutomationTabIndex = entry.Type == "trigger" ? 2 : 1;
        NotifyRuleEditModeChanged();
    }

    private void StartAddRule(string type)
    {
        ClearRuleForm();
        NewRuleType = type;
        IsRuleFormExpanded = true;
        SelectedAutomationTabIndex = type == "trigger" ? 2 : 1;
    }

    private void CancelRuleEdit() => ClearRuleForm();

    private void ClearRuleForm()
    {
        if (_editedRule is { } edited)
        {
            edited.IsEditing = false;
        }

        _editedRule = null;
        IsRuleFormExpanded = false;
        NewRuleName = string.Empty;
        NewRulePattern = string.Empty;
        NewRuleAction = string.Empty;
        NewRuleIsGlobal = false;
        NewRuleIsScript = false;
        NewRulePlaySoundOnMatch = false;
        NewRuleTestInput = string.Empty;
        NewRuleTestOutput = null;
        NotifyRuleEditModeChanged();
    }

    private void NotifyRuleEditModeChanged()
    {
        OnPropertyChanged(nameof(IsEditingRule));
        OnPropertyChanged(nameof(RuleFormButtonText));
        OnPropertyChanged(nameof(RuleFormHeader));
    }

    private void DeleteRule(AutomationRuleEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (ReferenceEquals(entry, _editedRule))
        {
            ClearRuleForm();
        }

        AutomationRules.Remove(entry);
        RebuildRuleViews();
        ApplyAutomation();
        SaveActiveProfile();
    }

    private void ToggleRule(AutomationRuleEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsEnabled = !entry.IsEnabled;
        ApplyAutomation();
        RebuildFolderTrees();
        SaveActiveProfile();
    }

    // ========================================================================
    // Timers (per-character, repeating until disabled)
    // ========================================================================

    public ObservableCollection<TimerEntry> Timers { get; } = [];

    // --- Skills currently on cooldown (live, from Char.Skills.Timeout GMCP) — shown as
    // "* skillname" alongside Timers for as long as the skill's timeout stays true. ---
    public ObservableCollection<string> SkillsOnCooldown { get; } = [];

    public RelayCommand AddTimerCommand { get; }
    public RelayCommand StartAddTimerCommand { get; }
    public RelayCommand<TimerEntry> DeleteTimerCommand { get; }
    public RelayCommand<TimerEntry> ToggleTimerCommand { get; }
    public RelayCommand<TimerEntry> RestartTimerCommand { get; }
    public RelayCommand<TimerEntry> EditTimerCommand { get; }
    public RelayCommand CancelTimerEditCommand { get; }
    public RelayCommand TestTimerScriptCommand { get; }

    public bool IsEditingTimer => _editedTimer is not null;

    /// <summary>Backs the timer form Expander (two-way); editing a timer opens it.</summary>
    public bool IsTimerFormExpanded
    {
        get => _isTimerFormExpanded;
        set => SetProperty(ref _isTimerFormExpanded, value);
    }

    public string TimerFormButtonText => IsEditingTimer ? "Zapisz zmiany" : "Dodaj timer";

    public string TimerFormHeader => IsEditingTimer ? "✎ Edytuj timer" : "＋ Nowy timer";

    public string NewTimerName
    {
        get => _newTimerName;
        set
        {
            if (SetProperty(ref _newTimerName, value))
            {
                AddTimerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewTimerMinutes
    {
        get => _newTimerMinutes;
        set => SetProperty(ref _newTimerMinutes, value);
    }

    public string NewTimerSeconds
    {
        get => _newTimerSeconds;
        set => SetProperty(ref _newTimerSeconds, value);
    }

    public string NewTimerMilliseconds
    {
        get => _newTimerMilliseconds;
        set => SetProperty(ref _newTimerMilliseconds, value);
    }

    /// <summary>One command per line; sent in order on every tick.</summary>
    public string NewTimerCommands
    {
        get => _newTimerCommands;
        set => SetProperty(ref _newTimerCommands, value);
    }

    /// <summary>True = the new/edited timer is shared by all profiles.</summary>
    public bool NewTimerIsGlobal
    {
        get => _newTimerIsGlobal;
        set => SetProperty(ref _newTimerIsGlobal, value);
    }

    /// <summary>True = the new/edited timer plays a notification sound every time it fires (see
    /// <see cref="TimerEntry.PlaySoundOnTick"/>).</summary>
    public bool NewTimerPlaySoundOnTick
    {
        get => _newTimerPlaySoundOnTick;
        set => SetProperty(ref _newTimerPlaySoundOnTick, value);
    }

    /// <summary>True = <see cref="NewTimerCommands"/> is Lua source (run once per tick via
    /// <see cref="_lua"/>) instead of a plain per-line command list.</summary>
    public bool NewTimerIsScript
    {
        get => _newTimerIsScript;
        set
        {
            if (SetProperty(ref _newTimerIsScript, value))
            {
                OnPropertyChanged(nameof(NewTimerCommandsLabel));
                OnPropertyChanged(nameof(NewTimerCommandsPlaceholder));
                TestTimerScriptCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewTimerCommandsLabel => NewTimerIsScript
        ? "Skrypt Lua — uruchamiany co interwał"
        : "Komendy — każda w osobnej linii";

    public string NewTimerCommandsPlaceholder => NewTimerIsScript
        ? "if hp and maxhp and hp < maxhp then\n  send(\"odpoczywaj\")\nend"
        : "np.\nrzuc 'leczenie'\npij miksture";

    /// <summary>Result of the last "Testuj" run for the timer form — see
    /// <see cref="TestTimerScript"/>. Null before the button is ever pressed.</summary>
    public string? NewTimerTestOutput
    {
        get => _newTimerTestOutput;
        private set => SetProperty(ref _newTimerTestOutput, value);
    }

    /// <summary>Runs <see cref="NewTimerCommands"/> as Lua once, the same way a real tick would
    /// (see <see cref="RunScriptTimer"/>) — a timer has no pattern/line, just current game state,
    /// so unlike <see cref="TestRuleScript"/> there's nothing to match first. Same shared-state
    /// trade-off: a script that mutates a global really does mutate it when tested.</summary>
    private void TestTimerScript()
    {
        if (!NewTimerIsScript)
        {
            return;
        }

        try
        {
            var commands = _lua.Run(NewTimerCommands, line: null, match: null);
            NewTimerTestOutput = commands.Count > 0
                ? string.Join(Environment.NewLine, commands.Select(command => $"→ {command}"))
                : "(skrypt nie wywołał send() — brak komend)";
        }
        catch (MoonSharp.Interpreter.InterpreterException exception)
        {
            NewTimerTestOutput = $"Błąd: {exception.DecoratedMessage ?? exception.Message}";
        }
    }

    private void AddTimer()
    {
        var name = NewTimerName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var minutes = ParseNonNegative(NewTimerMinutes);
        var seconds = ParseNonNegative(NewTimerSeconds);
        var milliseconds = ParseNonNegative(NewTimerMilliseconds);
        var interval = TimeSpan.FromMinutes(minutes) +
                       TimeSpan.FromSeconds(seconds) +
                       TimeSpan.FromMilliseconds(milliseconds);

        if (interval <= TimeSpan.Zero)
        {
            AddToast("Interwał timera musi być większy od zera.", "error");
            return;
        }

        var hasCommands = CommandStacker.Split(NewTimerCommands, CommandStackingSeparator).Count > 0;
        if (!hasCommands)
        {
            AddToast("Timer musi mieć przynajmniej jedną komendę.", "error");
            return;
        }

        if (_editedTimer is { } edited)
        {
            edited.Name = name;
            edited.Minutes = minutes;
            edited.Seconds = seconds;
            edited.Milliseconds = milliseconds;
            edited.CommandsText = NewTimerCommands;
            edited.IsGlobal = NewTimerIsGlobal;
            edited.IsScript = NewTimerIsScript;
            edited.PlaySoundOnTick = NewTimerPlaySoundOnTick;
            SyncTimer(edited);
        }
        else
        {
            Timers.Add(new TimerEntry
            {
                Name = name,
                Minutes = minutes,
                Seconds = seconds,
                Milliseconds = milliseconds,
                CommandsText = NewTimerCommands,
                IsGlobal = NewTimerIsGlobal,
                IsScript = NewTimerIsScript,
                PlaySoundOnTick = NewTimerPlaySoundOnTick,
            });
        }

        ClearTimerForm();
        SaveActiveProfile();
    }

    private void EditTimer(TimerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (_editedTimer is { } previouslyEdited)
        {
            previouslyEdited.IsEditing = false;
        }

        _editedTimer = entry;
        entry.IsEditing = true;
        NewTimerName = entry.Name;
        NewTimerMinutes = entry.Minutes.ToString();
        NewTimerSeconds = entry.Seconds.ToString();
        NewTimerMilliseconds = entry.Milliseconds.ToString();
        NewTimerCommands = entry.CommandsText;
        NewTimerIsGlobal = entry.IsGlobal;
        NewTimerIsScript = entry.IsScript;
        NewTimerPlaySoundOnTick = entry.PlaySoundOnTick;
        NewTimerTestOutput = null;
        SelectedAutomationTabIndex = 0;
        NotifyTimerEditModeChanged();
    }

    private void StartAddTimer()
    {
        ClearTimerForm();
        IsTimerFormExpanded = true;
        SelectedAutomationTabIndex = 0;
    }

    private void CancelTimerEdit() => ClearTimerForm();

    private void ClearTimerForm()
    {
        if (_editedTimer is { } edited)
        {
            edited.IsEditing = false;
        }

        _editedTimer = null;
        IsTimerFormExpanded = false;
        NewTimerName = string.Empty;
        NewTimerMinutes = "0";
        NewTimerSeconds = "0";
        NewTimerMilliseconds = "0";
        NewTimerCommands = string.Empty;
        NewTimerIsGlobal = false;
        NewTimerIsScript = false;
        NewTimerPlaySoundOnTick = false;
        NewTimerTestOutput = null;
        NotifyTimerEditModeChanged();
    }

    private void NotifyTimerEditModeChanged()
    {
        OnPropertyChanged(nameof(IsEditingTimer));
        OnPropertyChanged(nameof(TimerFormButtonText));
        OnPropertyChanged(nameof(TimerFormHeader));
    }

    private void DeleteTimer(TimerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (ReferenceEquals(entry, _editedTimer))
        {
            ClearTimerForm();
        }

        StopTimer(entry);
        Timers.Remove(entry);
        SaveActiveProfile();
    }

    private void ToggleTimer(TimerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsEnabled = !entry.IsEnabled;
        SyncTimer(entry);
        RebuildFolderTrees();
        SaveActiveProfile();

        AddToast(entry.IsEnabled
            ? $"Timer „{entry.Name}” włączony (co {entry.IntervalText})."
            : $"Timer „{entry.Name}” wyłączony.", "info");
    }

    /// <summary>Resets an active timer's countdown back to its full interval, without disabling
    /// it — bound to the small "restart countdown" icon in the terminal's timer strip.</summary>
    private void RestartTimer(TimerEntry? entry)
    {
        if (entry is null || !entry.IsEnabled)
        {
            return;
        }

        SyncTimer(entry);
        AddToast($"Timer „{entry.Name}” zresetowany (co {entry.IntervalText}).", "info");
    }

    private static string TimerKey(TimerEntry entry) => $"user-timer:{entry.Id}";

    /// <summary>Starts or stops the underlying periodic timer to match IsEnabled.</summary>
    private void SyncTimer(TimerEntry entry)
    {
        if (!entry.IsEnabled)
        {
            StopTimer(entry);
            return;
        }

        var interval = entry.Interval;
        if (interval <= TimeSpan.Zero)
        {
            entry.IsEnabled = false;
            entry.ClearNextActivation();
            AddToast($"Timer „{entry.Name}” ma nieprawidłowy interwał.", "error");
            return;
        }

        // A script timer's Lua can depend on live game state (e.g. "if hp < 50 then ..."), so it
        // must be re-run every tick — a plain-text timer's commands never change, so those are
        // still precomputed once here rather than re-parsed on every firing.
        var staticCommands = entry.IsScript
            ? null
            : entry.GetCommands(CommandStackingSeparator)
                .SelectMany(command => _aliases.ProcessAliasCall(command, CommandStackingSeparator))
                .ToArray();
        var now = DateTimeOffset.UtcNow;
        entry.ScheduleNextActivation(now + interval, now);
        _timers.StartPeriodic(TimerKey(entry), interval, async token =>
        {
            if (entry.PlaySoundOnTick)
            {
                PlayNotificationSound();
            }

            if (IsConnected && _bookRefreshCts is null && _rareRefreshCts is null && _mapujCts is null)
            {
                var commands = staticCommands ?? RunScriptTimer(entry);
                if (commands.Count > 0)
                {
                    var description = $"Timer: „{entry.Name}”";
                    Dispatcher.UIThread.Post(() =>
                    {
                        AutomationFired?.Invoke(description);
                        ShowAutomationActivity(description);
                    });
                }

                foreach (var command in commands)
                {
                    token.ThrowIfCancellationRequested();
                    if (Map.IsMapEditorActive)
                    {
                        continue;
                    }

                    Dispatcher.UIThread.Post(() => EmitCommandEcho(command));
                    await _session.SendCommandAsync(command, token);
                }
            }

            var nextIntervalStartedAt = DateTimeOffset.UtcNow;
            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested && entry.IsEnabled)
                {
                    entry.ScheduleNextActivation(
                        nextIntervalStartedAt + interval,
                        nextIntervalStartedAt);
                }
            });
        });
    }

    /// <summary>Runs a script timer's Lua once per tick (see <see cref="SyncTimer"/>) and, like a
    /// script alias/trigger, reports a syntax/runtime error via <see cref="OnLuaScriptError"/>
    /// instead of letting it propagate — a bad script skips that tick's commands, not the whole
    /// timer.</summary>
    private IReadOnlyList<string> RunScriptTimer(TimerEntry entry)
    {
        try
        {
            return _lua.Run(entry.CommandsText, line: null, match: null)
                .SelectMany(command => _aliases.ProcessAliasCall(command, CommandStackingSeparator))
                .ToArray();
        }
        catch (MoonSharp.Interpreter.InterpreterException exception)
        {
            OnLuaScriptError(entry.Name, exception.DecoratedMessage ?? exception.Message);
            return [];
        }
    }

    private void StopTimer(TimerEntry entry)
    {
        _timers.Cancel(TimerKey(entry));
        entry.ClearNextActivation();
    }

    private void CancelAllTimers()
    {
        _timers.CancelAll();
        foreach (var entry in Timers)
        {
            entry.ClearNextActivation();
        }
    }

    private void SyncAllTimers()
    {
        foreach (var entry in Timers)
        {
            SyncTimer(entry);
        }
    }

    private static int ParseNonNegative(string text) =>
        int.TryParse(text?.Trim(), out var value) && value > 0 ? value : 0;

    // ========================================================================
    // Autowalk (named locations + pathfinding over the world map)
    // ========================================================================

    public ObservableCollection<AutowalkLocation> Locations { get; } = [];

    public RelayCommand AddCurrentLocationCommand { get; }
    public RelayCommand AddLocationCommand { get; }
    public RelayCommand<AutowalkLocation> DeleteLocationCommand { get; }
    public RelayCommand<AutowalkLocation> GoToLocationCommand { get; }
    public RelayCommand StopAutowalkCommand { get; }
    public RelayCommand StartAutoFarmCommand { get; }
    public RelayCommand StopAutoFarmCommand { get; }

    public string NewLocationName
    {
        get => _newLocationName;
        set => SetProperty(ref _newLocationName, value);
    }

    /// <summary>Room vnum typed by the user when defining a remote location.</summary>
    public string NewLocationVnum
    {
        get => _newLocationVnum;
        set => SetProperty(ref _newLocationVnum, value);
    }

    /// <summary>True = the new location is shared by all profiles.</summary>
    public bool NewLocationIsGlobal
    {
        get => _newLocationIsGlobal;
        set => SetProperty(ref _newLocationIsGlobal, value);
    }

    public bool IsAutowalking => _autowalkPath is not null;

    public RelayCommand GoToTemporaryTargetCommand { get; }
    public RelayCommand GoToSelectedTargetCommand { get; }

    /// <summary>Target picked by double-clicking the map; not saved to the profile.</summary>
    public bool HasTemporaryTarget => _temporaryTarget is not null;

    public string TemporaryTargetDisplay => _temporaryTarget is { } target
        ? $"Cel z mapy: {target.Name} (vnum {target.Vnum})"
        : string.Empty;

    private void SetTemporaryTarget(AutowalkLocation? target)
    {
        _temporaryTarget = target;
        OnPropertyChanged(nameof(HasTemporaryTarget));
        OnPropertyChanged(nameof(TemporaryTargetDisplay));
    }

    private void OnMapRoomDoubleClicked(MapRoom room)
    {
        PreviewRouteToRoom(room);

        if (Map.AutoWalkOnMapDoubleClick && _temporaryTarget is not null)
        {
            StartAutowalk(_temporaryTarget);
        }
    }

    private void OnLordGotoRequested(MapRoom room)
    {
        if (!LordModeEnabled || string.IsNullOrWhiteSpace(room.Vnum) || !room.Vnum.All(char.IsAsciiDigit))
        {
            return;
        }

        QueueTriggeredCommands([$"walk {room.Vnum}"]);
    }

    private void OnMapLordModeChanged(bool enabled)
    {
        if (_profileSettings.LordModeEnabled == enabled)
        {
            return;
        }

        _profileSettings.LordModeEnabled = enabled;
        OnPropertyChanged(nameof(LordModeEnabled));
        LordGotoGroupRoomCommand.NotifyCanExecuteChanged();
        LordGotoGroupMemberCommand.NotifyCanExecuteChanged();
        SaveActiveProfile();
    }

    private void OnMapGroupMarkerDisplayChanged(bool showAsNumbers)
    {
        if (_profileSettings.ShowGroupMembersAsNumbers == showAsNumbers)
        {
            return;
        }

        _profileSettings.ShowGroupMembersAsNumbers = showAsNumbers;
        SaveActiveProfile();
    }

    private void OnMapDisplayModeChanged(MapDisplayMode mode)
    {
        if (_settings.MapDisplayMode == mode)
        {
            return;
        }

        _settings.MapDisplayMode = mode;
        SaveSettings();
    }

    private void OnMapAutoWalkOnDoubleClickChanged(bool enabled)
    {
        if (_profileSettings.AutoWalkOnMapDoubleClick == enabled)
        {
            return;
        }

        _profileSettings.AutoWalkOnMapDoubleClick = enabled;
        SaveActiveProfile();
    }

    private void OnMapAutoScanOnRoomEnterChanged(bool enabled)
    {
        if (_profileSettings.AutoScanOnRoomEnterEnabled == enabled)
        {
            return;
        }

        _profileSettings.AutoScanOnRoomEnterEnabled = enabled;
        SaveActiveProfile();
    }

    private void OnMapAutoKillOnRoomEnterChanged(bool enabled)
    {
        if (_profileSettings.AutoKillOnRoomEnterEnabled == enabled)
        {
            return;
        }

        _profileSettings.AutoKillOnRoomEnterEnabled = enabled;
        SaveActiveProfile();
    }

    private void OnMapAutoFarmRegionsChanged(IReadOnlyList<FarmRegion> regions)
    {
        _autoFarmRegions = regions;
        StartAutoFarmCommand.NotifyCanExecuteChanged();
        SaveActiveProfile();
    }

    private void OnMapAutoKillMobNamesChanged(string text)
    {
        var names = ParseMobNameLines(text);
        if (_profileSettings.AutoKillMobNames.SequenceEqual(names, StringComparer.Ordinal))
        {
            return;
        }

        _profileSettings.AutoKillMobNames = names;
        SaveActiveProfile();
    }

    /// <summary>Shared by <see cref="AutoAssistExcludedMobNamesText"/> and
    /// <see cref="OnMapAutoKillMobNamesChanged"/>: one mob name per line, trimmed, deduplicated
    /// case-insensitively.</summary>
    private static List<string> ParseMobNameLines(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Fires "scan" (see <see cref="MapViewModel.AutoScanOnRoomEnter"/>) unconditionally
    /// every time GMCP reports a new room, same as before. "kill &lt;name&gt;" (see
    /// <see cref="MapViewModel.AutoKillOnRoomEnter"/>) is no longer sent unconditionally per
    /// configured name — that spammed a "kill" per name into every room regardless of whether it
    /// was actually there. Instead this arms a one-shot check (see
    /// <see cref="TryAutoKillIfConfirmed"/>) that only fires for names Room.People actually
    /// reports present.</summary>
    /// <summary>The room GMCP just told us we entered, waiting to have its vnum spliced onto the
    /// matching room-name line the moment that line actually arrives as raw text — see
    /// <see cref="AnnotateRoomVnum"/>. Superseded (not explicitly cleared) by the next room entry
    /// if the expected name never shows up, so a slow/garbled response can't wedge this open.</summary>
    private (string Vnum, string Name)? _pendingRoomVnumAnnotation;

    /// <summary>Arranges for the new room's vnum to appear right next to its name instead of on
    /// its own line — cheaper on screen space than a standalone echo. Needs the room's expected
    /// name text to find the right line to splice onto (see <see cref="AnnotateRoomVnum"/>), which
    /// only the map knows; an unmapped room falls back to the old standalone-line echo.</summary>
    private void OnRoomEnterShowVnum(string vnum)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(vnum))
        {
            return;
        }

        var roomName = Map.MapIndex?.FindFirstRoomByVnum(vnum)?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(roomName))
        {
            // LocationChanged fires from GMCP processing on the network receive thread, not the
            // UI thread — EmitSystem ultimately touches UI-bound state via OutputReceived, so it
            // must be marshaled (see OnAutowalkLocationChanged's identical Dispatcher.UIThread.Post
            // elsewhere in this file). AnnotateRoomVnum's path doesn't need this: it rides along
            // inside OnTextReceived, which already marshals its own final OutputReceived call.
            Dispatcher.UIThread.Post(() => EmitSystem($"[vnum: {vnum}]", 90));
            return;
        }

        _pendingRoomVnumAnnotation = (vnum, roomName);
    }

    /// <summary>
    /// Splices " [vnum: N]" onto the end of the room-name line the game just printed for
    /// <see cref="_pendingRoomVnumAnnotation"/> (set by <see cref="OnRoomEnterShowVnum"/>) — one
    /// line saved versus a standalone echo. Matches against an ANSI-stripped copy of each line
    /// (room names are commonly colored), the same technique <see cref="SkillTrainerAnnotator"/>
    /// uses, though here the splice always lands at the line's own end so no index-mapping back
    /// into the colored original is needed. Same no-cross-chunk-state trade-off as
    /// <see cref="AnnotateBookClasses"/> for any one attempt — but unlike that annotator, a miss
    /// isn't silently lost forever: the pending entry simply keeps waiting for a later chunk,
    /// naturally superseded whenever the next room is entered.
    /// </summary>
    private string AnnotateRoomVnum(string chunk)
    {
        if (_pendingRoomVnumAnnotation is null || !chunk.Contains('\n'))
        {
            return chunk;
        }

        var segments = chunk.Split('\n');
        var output = new StringBuilder(chunk.Length + 16);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var line = segments[i].TrimEnd('\r');
            if (_pendingRoomVnumAnnotation is { } pending
                && string.Equals(AnsiText.StripAnsi(line).Trim(), pending.Name, StringComparison.Ordinal))
            {
                output.Append(line).Append(" [vnum: ").Append(pending.Vnum).Append(']');
                _pendingRoomVnumAnnotation = null;
            }
            else
            {
                output.Append(line);
            }

            output.Append('\n');
        }

        output.Append(segments[^1]);
        return output.ToString();
    }

    private void OnRoomEnterAutomations(string vnum)
    {
        // Bumped unconditionally (even when disconnected) so TryAutoKillIfConfirmed can never
        // mistake a Room.People snapshot stamped before this room change for a fresh one.
        _roomEntryGeneration++;

        if (!IsConnected)
        {
            return;
        }

        if (Map.AutoScanOnRoomEnter)
        {
            QueueTriggeredCommands(["scan"]);
        }

        if (Map.AutoKillOnRoomEnter && _profileSettings.AutoKillMobNames.Count > 0)
        {
            _autoKillPending = true;
            // In case Room.People for this room already arrived before LocationChanged fired —
            // otherwise TryAutoKillIfConfirmed fires again from OnRoomPeopleChanged as it updates.
            TryAutoKillIfConfirmed();
        }
    }

    /// <summary>Gates the room-enter "kill" list on Room.People actually reporting the new room's
    /// contents. Room.People can still reflect the room just left at the exact moment
    /// LocationChanged fires — the identical race documented on
    /// <see cref="TryAutoAssistNpcIfConfirmed"/> — so this checks <see cref="_roomEntryGeneration"/>
    /// against the generation <see cref="OnRoomPeopleChanged"/> last stamped
    /// <see cref="_latestRoomPeople"/> with, instead of guessing off a possibly-stale snapshot.
    /// Earlier this simply cleared <see cref="_autoKillPending"/> on first call regardless of
    /// freshness — a fast farm run could reach a room, get evaluated here against the previous
    /// room's still-stale Room.People, and never re-check once the real snapshot for the new room
    /// landed, silently skipping mobs that were actually there. Now it just returns (leaving the
    /// pending flag armed) until a same-generation snapshot confirms the room.</summary>
    private void TryAutoKillIfConfirmed()
    {
        if (!_autoKillPending || _autoKillRoomPeopleGeneration != _roomEntryGeneration)
        {
            return;
        }

        _autoKillPending = false;
        var commands = BuildAutoKillCommands(_profileSettings.AutoKillMobNames, _latestRoomPeople);
        if (commands.Count > 0)
        {
            QueueTriggeredCommands(commands);
        }
    }

    /// <summary>Pure decision behind <see cref="TryAutoKillIfConfirmed"/>: only the configured
    /// names that at least one currently-visible <paramref name="roomPeople"/> entry's name
    /// contains (case-insensitive) — the same keyword-style partial match the MUD's own "kill"
    /// command already resolves against a full mob name.</summary>
    internal static IReadOnlyList<string> BuildAutoKillCommands(
        IReadOnlyList<string> autoKillMobNames, IReadOnlyList<RoomPerson> roomPeople) =>
        autoKillMobNames
            .Where(name => roomPeople.Any(person =>
                person.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => $"kill {name}")
            .ToArray();

    private void PreviewRouteToRoom(MapRoom room)
    {
        var vnum = room.Vnum;
        if (string.IsNullOrWhiteSpace(vnum))
        {
            AddToast("Ten pokój nie ma vnum — nie można do niego nawigować.", "error");
            return;
        }

        SetTemporaryTarget(new AutowalkLocation(
            string.IsNullOrWhiteSpace(room.Name) ? $"pokój {vnum}" : room.Name!, vnum, room.Name));

        if (IsAutowalking)
        {
            // Stop the active walk so the user can preview the new route,
            // but keep the fresh temporary target (do NOT call StopAutowalk
            // here — it would also clear _temporaryTarget).
            _autowalkCts.Cancel();
            _autowalkPath = null;
            _autowalkStep = 0;
            _autowalkTargetName = null;
            ResetAutowalkTransientState();
            OnPropertyChanged(nameof(IsAutowalking));
            Map.RouteRooms = null;
            AddToast($"Autowalk przerwany — nowy cel „{_temporaryTarget!.Name}”.", "info");
            // Fall through to preview the new route below.
        }

        // Preview the route without walking.
        var currentVnum = Map.CurrentVnum;
        var path = string.IsNullOrWhiteSpace(currentVnum)
            ? null
            : GetPathfinder()?.FindPathByVnum(currentVnum, vnum);

        if (path is null)
        {
            Map.RouteRooms = null;
            AutowalkStatusText = $"Cel: „{_temporaryTarget!.Name}” — brak podglądu trasy (nieznana pozycja lub brak drogi).";
            return;
        }

        PaintRoute(path, 0);
        AutowalkStatusText = $"Cel: „{_temporaryTarget!.Name}” — {path.Steps.Count} kroków. Wpisz /walk albo kliknij IDŹ DO CELU.";
    }

    private void ShowTeacherOnMap(TeacherEntry teacher)
    {
        IsKilleropediaOpen = false;
        _dockFactory.ShowTool("Map");

        if (teacher.RoomVnum is not { Length: > 0 } roomVnum
            || Map.FocusRoomByVnum(roomVnum) is not { } room)
        {
            Map.RouteRooms = null;
            AddToast($"Lokalizacja nauczyciela „{teacher.Name}” nie jest dostępna na mapie.", "error");
            return;
        }

        PreviewRouteToRoom(room);
    }

    private void ShowBookLocationOnMap(BookLoadLocationEntry location)
    {
        IsKilleropediaOpen = false;
        _dockFactory.ShowTool("Map");

        if (location.RoomVnum is not { Length: > 0 } roomVnum
            || Map.FocusRoomByVnum(roomVnum) is not { } room)
        {
            Map.RouteRooms = null;
            AddToast("Ta lokalizacja księgi nie jest dostępna na mapie.", "error");
            return;
        }

        PreviewRouteToRoom(room);
    }

    /// <summary>
    /// Paints the remaining part of a path on the map, starting at the room
    /// the walker currently occupies (fromStep = next step to execute).
    /// </summary>
    private void PaintRoute(MapPath path, int fromStep)
    {
        var rooms = new List<MapRoom>(path.Steps.Count - fromStep + 1)
        {
            fromStep == 0 ? path.From : path.Steps[fromStep - 1].ToRoom,
        };

        for (var i = fromStep; i < path.Steps.Count; i++)
        {
            rooms.Add(path.Steps[i].ToRoom);
        }

        Map.RouteRooms = rooms;
    }

    public string AutowalkStatusText
    {
        get => _autowalkStatusText;
        private set => SetProperty(ref _autowalkStatusText, value);
    }

    /// <summary>Short-lived "Timer: „Nazwa”" text on the timers/triggers status bar — set by
    /// <see cref="SyncTimer"/> via <see cref="ShowAutomationActivity"/>, cleared automatically a
    /// few seconds later. A Trigger match does not set this — it flashes its own entry on the same
    /// bar instead (see <see cref="FlashTriggerRecentlyFired"/>), since unlike a timer it already
    /// has a persistent, named row to highlight. Null means nothing recent to show.</summary>
    public string? RecentAutomationActivityText
    {
        get => _recentAutomationActivityText;
        private set => SetProperty(ref _recentAutomationActivityText, value);
    }

    /// <summary>Shows <paramref name="description"/> in <see cref="RecentAutomationActivityText"/>
    /// and clears it again after <see cref="AutomationActivityDisplayDuration"/> — a later call
    /// while one is already pending restarts the timer rather than stacking, so a busy stretch of
    /// firings just keeps refreshing the same short-lived line instead of queuing a backlog.</summary>
    private void ShowAutomationActivity(string description)
    {
        RecentAutomationActivityText = description;

        _automationActivityClearCts?.Cancel();
        _automationActivityClearCts?.Dispose();
        var cts = new CancellationTokenSource();
        _automationActivityClearCts = cts;
        _ = ClearAutomationActivityAfterDelayAsync(description, cts.Token);
    }

    private async Task ClearAutomationActivityAfterDelayAsync(string description, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutomationActivityDisplayDuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Only clear if nothing newer has already replaced this line while we were waiting.
        if (!cancellationToken.IsCancellationRequested && RecentAutomationActivityText == description)
        {
            RecentAutomationActivityText = null;
        }
    }

    /// <summary>
    /// Returns the pathfinder for the currently loaded map, building it once
    /// per MapIndex instance (the CSR graph build is the expensive part).
    /// </summary>
    private MapPathfinder? GetPathfinder()
    {
        var index = Map.MapIndex;
        if (index is null)
        {
            return null;
        }

        if (!ReferenceEquals(index, _pathfinderIndex))
        {
            _pathfinder = new MapPathfinder(index);
            _pathfinderIndex = index;
        }

        return _pathfinder;
    }

    private void AddCurrentLocation()
    {
        var vnum = Map.CurrentVnum;
        if (string.IsNullOrWhiteSpace(vnum))
        {
            AddToast("Nieznana obecna pozycja — brak danych GMCP.", "error");
            return;
        }

        AddLocationCore(NewLocationName, vnum);
    }

    private void AddLocation()
    {
        AddLocationCore(NewLocationName, NewLocationVnum);
    }

    private void AddLocationCore(
        string rawName,
        string rawVnum,
        bool? isGlobal = null,
        bool clearEditor = true)
    {
        var name = rawName.Trim();
        var vnum = rawVnum.Trim();

        if (name.Length == 0)
        {
            AddToast("Podaj nazwę lokacji.", "error");
            return;
        }

        if (vnum.Length == 0)
        {
            AddToast("Podaj numer pomieszczenia (vnum).", "error");
            return;
        }

        if (Locations.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            AddToast($"Lokacja „{name}” już istnieje.", "error");
            return;
        }

        var room = Map.MapIndex?.FindFirstRoomByVnum(vnum);
        if (Map.MapIndex is not null && room is null)
        {
            AddToast($"Uwaga: vnum {vnum} nie istnieje w mapie.", "error");
        }

        Locations.Add(new AutowalkLocation(name, vnum, room?.Name, isGlobal ?? NewLocationIsGlobal));
        if (clearEditor)
        {
            NewLocationName = string.Empty;
            NewLocationVnum = string.Empty;
            NewLocationIsGlobal = false;
        }

        SaveActiveProfile();
        AddToast($"Dodano lokację „{name}”.", "info");
    }

    private void DeleteLocation(AutowalkLocation? entry)
    {
        if (entry is null)
        {
            return;
        }

        Locations.Remove(entry);
        SaveActiveProfile();
    }

    private void StartAutowalk(AutowalkLocation entry, IReadOnlySet<int>? excludedRoomIds = null)
    {
        var pathfinder = GetPathfinder();
        if (pathfinder is null)
        {
            AddToast("Mapa nie jest załadowana.", "error");
            return;
        }

        var currentVnum = Map.CurrentVnum;
        if (string.IsNullOrWhiteSpace(currentVnum))
        {
            AddToast("Nieznana obecna pozycja — brak danych GMCP.", "error");
            return;
        }

        var path = pathfinder.FindPathByVnum(currentVnum, entry.Vnum, excludedRoomIds);
        if (path is null)
        {
            AddToast(
                excludedRoomIds is { Count: > 0 }
                    ? $"Nie znaleziono trasy do „{entry.Name}” omijającej oznaczone pokoje."
                    : $"Nie znaleziono trasy do „{entry.Name}”.",
                "error");
            return;
        }

        if (path.Steps.Count == 0)
        {
            AddToast($"Już jesteś w lokacji „{entry.Name}”.", "info");
            return;
        }

        Map.CenterOnPlayer();
        ReplaceAutowalkCancellation();
        ResetAutowalkTransientState();
        _autowalkExcludedRoomIds = excludedRoomIds;
        _autowalkPath = path;
        _autowalkStep = 0;
        _autowalkRecomputes = 0;
        _autowalkMovementRecoveryAttempts = 0;
        _autowalkStuckRecoveryAttempts = 0;
        _autowalkTargetName = entry.Name;
        _pendingResumeTarget = null;
        OnPropertyChanged(nameof(IsAutowalking));
        AutowalkStatusText = $"Idę do „{entry.Name}” — {path.Steps.Count} kroków.";
        PaintRoute(path, 0);
        // Only stand up if GMCP actually reports a non-standing position — this used to check
        // "not sitting" instead, which also fired while already standing (the common case for
        // auto-farm's back-to-back hops) and got "Przecież już stoisz" back from the MUD on
        // every single room entry.
        if (!AutowalkRecoveryPolicy.IsStandingPosition(_latestCharacterPosition))
        {
            _ = SendTriggeredCommandAsync("stand");
        }

        SendAutowalkStep();
    }

    private void StopAutowalk(string message, string toastType = "info", bool resumable = false)
    {
        var wasWalking = _autowalkPath is not null;

        // Remember where we were headed BEFORE clearing state, but only when the
        // walk was cut short (resumable) — an arrival or an explicit /stop leaves
        // nothing to continue. A bare /walk then re-plots from the new position.
        if (resumable && _autowalkPath is { To.Vnum: { Length: > 0 } destVnum } cutPath)
        {
            _pendingResumeTarget = new AutowalkLocation(
                _autowalkTargetName ?? cutPath.To.Name ?? $"pokój {destVnum}",
                destVnum,
                cutPath.To.Name);
        }
        else
        {
            _pendingResumeTarget = null;
        }

        _autowalkCts.Cancel();
        _autowalkPath = null;
        _autowalkExcludedRoomIds = null;
        _autowalkStep = 0;
        _autowalkTargetName = null;
        ResetAutowalkTransientState();
        OnPropertyChanged(nameof(IsAutowalking));
        AutowalkStatusText = "Bezczynny.";
        Map.RouteRooms = null;
        SetTemporaryTarget(null);

        if (wasWalking)
        {
            AddToast(message, toastType);
        }
    }

    /// <summary>
    /// Shared tail of both autowalk-arrival paths in <see cref="OnAutowalkLocationChanged"/> — a
    /// normal rest-on-arrival only makes sense for a single deliberate destination, not for every
    /// hop of an active auto-farm (which would grind it to a halt), so that's skipped while
    /// <see cref="_autoFarmActive"/>; the farm's own HP-threshold recovery (see
    /// <see cref="ContinueAutoFarm"/>) handles resting for it instead.
    /// </summary>
    private void CompleteAutowalkArrival(string? targetName)
    {
        if (_profileSettings.AutowalkRestOnArrivalEnabled && !_autoFarmActive)
        {
            _ = SendTriggeredCommandAsync("rest");
        }

        StopAutowalk($"Dotarłeś do lokacji „{targetName}”.");

        if (_autoFarmActive)
        {
            ContinueAutoFarm();
        }
    }

    private void ReplaceAutowalkCancellation()
    {
        var previous = _autowalkCts;
        _autowalkCts = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    private void ResetAutowalkTransientState()
    {
        _autowalkRecoveringMovement = false;
        _autowalkRecoveringPosition = false;
        _autowalkOpeningStep = null;
        _autowalkWaitingForGate = false;
        _autowalkGateCommandsSent = false;
        _autowalkGateIsOpen = false;
        _autowalkPausedForCombat = false;
        _autowalkPausedForResting = false;
    }

    private void SendAutowalkStep(bool skipMovementCheck = false)
    {
        if (_autowalkPath is null || _autowalkStep >= _autowalkPath.Steps.Count)
        {
            return;
        }

        if (_autowalkWaitingForGate || _autowalkRecoveringMovement ||
            _autowalkRecoveringPosition || _autowalkPausedForCombat || _autowalkPausedForResting)
        {
            return;
        }

        if (AutowalkRecoveryPolicy.IsSittingPosition(_latestCharacterPosition))
        {
            BeginAutowalkStandRecovery();
            return;
        }

        if (!skipMovementCheck && _profileSettings.AutowalkMovementRecoveryEnabled)
        {
            var action = AutowalkRecoveryPolicy.GetLowMovementAction(
                _latestMovement, _latestMaximumMovement, _latestMemorizedSpells,
                _settings.AutowalkLowMovementThresholdPercent);
            if (action != LowMovementAction.None)
            {
                if (_autowalkMovementRecoveryAttempts >= MaxAutowalkMovementRecoveryAttempts)
                {
                    StopAutowalk(
                        "Autowalk przerwany: ruch nie wraca ponad próg mimo kilku prób odpoczynku (walka w trakcie?). Wpisz /walk, aby spróbować dalej.",
                        "error",
                        resumable: true);
                    return;
                }

                _autowalkMovementRecoveryAttempts++;
                _autowalkRecoveringMovement = true;
                _ = RecoverMovementAndContinueAsync(action, _autowalkCts.Token);
                return;
            }
        }

        var step = _autowalkPath.Steps[_autowalkStep];
        var remaining = _autowalkPath.Steps.Count - _autowalkStep;
        AutowalkStatusText = $"Idę do „{_autowalkTargetName}” — pozostało {remaining} kroków.";

        // A named exit (GMCP "name" or a custom exit name in the map) must be
        // entered by its name — the plain direction command does not work.
        var exit = FindGmcpExit(step.Command);
        var moveCommand = RemoveDiacritics(exit?.Name) ?? step.Command;
        if (!string.Equals(moveCommand, step.Command, StringComparison.OrdinalIgnoreCase))
        {
            EmitSystem($"Autowalk: krok „{step.Command}” wysyłam jako „{moveCommand}”.", 90);
        }

        var openCommand = TryGetOpenCommand(exit);
        _autowalkOpeningStep = openCommand is null ? null : _autowalkStep;
        _ = SendAutowalkCommandsAsync(openCommand, moveCommand, _autowalkCts.Token);
        _ = MonitorAutowalkStepStuckAsync(_autowalkStep, _autowalkCts.Token);
    }

    /// <summary>Backstop for a move command the server silently swallows (see the
    /// <see cref="_autowalkStuckRecoveryAttempts"/> field comment) — waits
    /// <see cref="AutowalkStuckStepTimeout"/> and, if <paramref name="step"/> still hasn't advanced
    /// by then, hands off to <see cref="HandleAutowalkStepStuck"/>. A normal room-change (or the
    /// walk stopping/replacing) races this harmlessly: whichever happens first wins, and this task
    /// simply finds nothing to do when it loses.</summary>
    private async Task MonitorAutowalkStepStuckAsync(int step, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutowalkStuckStepTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => HandleAutowalkStepStuck(step, cancellationToken));
    }

    private void HandleAutowalkStepStuck(int step, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _autowalkPath is null || _autowalkStep != step)
        {
            // Stopped, replaced, or this step already advanced normally — nothing stuck.
            return;
        }

        if (_autowalkWaitingForGate || _autowalkRecoveringMovement ||
            _autowalkRecoveringPosition || _autowalkPausedForCombat || _autowalkPausedForResting)
        {
            // Another recovery path already owns this step (e.g. a recognized
            // "brama...zamknięta" line already armed the GMCP gate-reopen wait,
            // or the player consciously rested mid-route).
            return;
        }

        if (_autowalkStuckRecoveryAttempts >= MaxAutowalkStuckRecoveryAttempts)
        {
            var stuckRoom = _autowalkPath.Steps[step].ToRoom;
            var stuckCommand = _autowalkPath.Steps[step].Command;
            _autowalkStuckRecoveryAttempts = 0;
            Map.MarkRoomClosed(stuckRoom.Vnum);

            if (_autoFarmActive)
            {
                EmitSystem(
                    $"Autowalk: krok „{stuckCommand}” nie przechodzi — oznaczam pokój jako zamknięty i kontynuuję farmę.", 33);
                StopAutowalk("Farma: przejście zablokowane — pokój oznaczony jako zamknięty, kontynuuję.", "info");
                ContinueAutoFarm();
            }
            else
            {
                StopAutowalk(
                    "Autowalk przerwany: krok nie przechodzi mimo prób otwarcia przejścia (zablokowane drzwi?). Pokój oznaczony jako zamknięty. Wpisz /walk, aby spróbować dalej.",
                    "error",
                    resumable: true);
            }

            return;
        }

        _autowalkStuckRecoveryAttempts++;
        AutowalkStatusText = "Krok nie przechodzi — próbuję otworzyć przejście i ponawiam.";
        _ = SendStuckStepRecoveryCommandsAsync(step, _autowalkCts.Token);
    }

    /// <summary>Tries an explicit "open &lt;exit&gt;" — using the step's own command/exit name, which
    /// for a custom-named exit (the map's "command" field) is exactly the name a locked non-"brama"
    /// door like a tomb entrance is defined under, e.g. "grobowiec" — followed by the same generic
    /// knock/pull/push commands <see cref="AutowalkRecoveryPolicy.GetGateOpeningCommands"/> already
    /// uses for a recognized locked gate, then resends the step once.</summary>
    private async Task SendStuckStepRecoveryCommandsAsync(int step, CancellationToken cancellationToken)
    {
        try
        {
            if (_autowalkPath is { } path && step < path.Steps.Count)
            {
                var stepCommand = path.Steps[step].Command;
                var exit = FindGmcpExit(stepCommand);
                var openTarget = RemoveDiacritics(exit?.Name) ?? RemoveDiacritics(stepCommand) ?? stepCommand;
                cancellationToken.ThrowIfCancellationRequested();
                await SendTriggeredCommandAsync($"open {openTarget}", cancellationToken);
            }

            foreach (var command in AutowalkRecoveryPolicy.GetGateOpeningCommands())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendTriggeredCommandAsync(command, cancellationToken);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || _autowalkPath is null || _autowalkStep != step)
                {
                    return;
                }

                SendAutowalkStep(skipMovementCheck: true);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The autowalk was stopped or replaced while the recovery sequence was being sent.
        }
    }

    private void BeginAutowalkStandRecovery()
    {
        // _autowalkRecoveringMovement guards against a race where GMCP briefly reports
        // "sitting" while the automatic low-movement rest recovery (RecoverMovementAndContinueAsync)
        // is already mid-flight — without it, OnAutowalkSitting's direct call here would force an
        // early "stand", cutting the rest short and leaving both recovery flags set at once, which
        // then races the recovery task's own later "stand".
        if (_autowalkRecoveringMovement || _autowalkRecoveringPosition || _autowalkPath is null ||
            _autowalkStep >= _autowalkPath.Steps.Count)
        {
            return;
        }

        _autowalkRecoveringPosition = true;
        AutowalkStatusText = $"Postać siedzi — wstaję i wznawiam trasę do „{_autowalkTargetName}”.";
        _ = StandForAutowalkAsync(_autowalkCts.Token);
    }

    private async Task StandForAutowalkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendTriggeredCommandAsync("stand", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping or replacing the autowalk also cancels the stand command.
        }
    }

    private async Task RecoverMovementAndContinueAsync(
        LowMovementAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            if (action == LowMovementAction.CastRefresh)
            {
                Dispatcher.UIThread.Post(() =>
                    AutowalkStatusText = "Mało ruchu — rzucam refresh.");
                await SendTriggeredCommandAsync("cast 'refresh' self", cancellationToken);
            }
            else
            {
                var restSeconds = _settings.AutowalkRestSeconds;
                Dispatcher.UIThread.Post(() =>
                    AutowalkStatusText = $"Mało ruchu — odpoczywam {restSeconds} sekund.");
                await SendTriggeredCommandAsync("rest", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(restSeconds), cancellationToken);

                // The character is still resting — stand up before walking on.
                await SendTriggeredCommandAsync("stand", cancellationToken);

                if (AutowalkRecoveryPolicy.HasMemorizedSpell(_latestMemorizedSpells, "float"))
                {
                    await SendTriggeredCommandAsync("cast 'float' self", cancellationToken);
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || _autowalkPath is null)
                {
                    return;
                }

                _autowalkRecoveringMovement = false;
                SendAutowalkStep(skipMovementCheck: true);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping autowalk also stops its pending recovery delay and sends.
        }
    }

    private async Task SendAutowalkCommandsAsync(
        string? openCommand,
        string moveCommand,
        CancellationToken cancellationToken)
    {
        try
        {
            if (openCommand is not null)
            {
                await SendTriggeredCommandAsync(openCommand, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await SendTriggeredCommandAsync(moveCommand, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user stopped or replaced this autowalk.
        }
    }

    /// <summary>
    /// When GMCP Room.Info reports the step's exit as a closed door, returns
    /// the command that opens it: "open" + the exit name from GMCP, or the
    /// direction when the exit has no name. (The map's "door" field holds the
    /// door state, e.g. "closed" — never a usable name.)
    /// </summary>
    private static string? TryGetOpenCommand(RoomExitInfo? exit)
    {
        if (exit is null || !exit.HasDoor || !exit.IsClosed)
        {
            return null;
        }

        return $"open {RemoveDiacritics(exit.Name) ?? exit.Dir}";
    }

    /// <summary>
    /// Matches a map exit command against the current room's GMCP exits,
    /// either by canonical direction (map "west" ↔ GMCP "W") or, for
    /// custom-named exits, by the exit name itself.
    /// </summary>
    private RoomExitInfo? FindGmcpExit(string stepCommand)
        => FindGmcpExit(stepCommand, _roomExits.CurrentExits);

    private static RoomExitInfo? FindGmcpExit(
        string stepCommand,
        IReadOnlyList<RoomExitInfo> exits)
    {
        var canonical = CanonicalDirection(stepCommand);

        foreach (var exit in exits)
        {
            if (string.Equals(CanonicalDirection(exit.Dir), canonical, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(exit.Name, stepCommand, StringComparison.OrdinalIgnoreCase))
            {
                return exit;
            }
        }

        return null;
    }

    private void OnRoomExitsChanged(IReadOnlyList<RoomExitInfo> exits)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_autowalkWaitingForGate || _autowalkPath is null ||
                _autowalkStep >= _autowalkPath.Steps.Count)
            {
                return;
            }

            var exit = FindGmcpExit(_autowalkPath.Steps[_autowalkStep].Command, exits);
            if (exit is null || exit.IsClosed)
            {
                return;
            }

            _autowalkGateIsOpen = true;
            TryContinueThroughOpenedGate();
        });
    }

    private void TryContinueThroughOpenedGate()
    {
        if (!_autowalkWaitingForGate || !_autowalkGateCommandsSent || !_autowalkGateIsOpen)
        {
            return;
        }

        _autowalkWaitingForGate = false;
        _autowalkOpeningStep = null;
        EmitSystem("Autowalk: przejście otwarte w GMCP — idę dalej.", 90);
        SendAutowalkStep();
    }

    /// <summary>Strips diacritics so autowalk commands are plain ASCII (e.g. "wyjście" → "wyjscie").</summary>
    private static string? RemoveDiacritics(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Maps full direction names to the short form used by GMCP dirs.</summary>
    private static string CanonicalDirection(string direction) => direction.ToLowerInvariant() switch
    {
        "north" => "N",
        "south" => "S",
        "east" => "E",
        "west" => "W",
        "northeast" => "NE",
        "northwest" => "NW",
        "southeast" => "SE",
        "southwest" => "SW",
        "up" => "U",
        "down" => "D",
        _ => direction.ToUpperInvariant(),
    };

    /// <summary>
    /// Advances the walk when GMCP confirms a room change: if the new room is
    /// one of the upcoming path steps we move past it, otherwise the route is
    /// recomputed from the new position (e.g. after a failed or extra move).
    /// </summary>
    private void OnAutowalkLocationChanged(string vnum)
    {
        TryAutoAssist();

        Dispatcher.UIThread.Post(() =>
        {
            if (_autowalkPath is null)
            {
                return;
            }

            _autowalkOpeningStep = null;
            _autowalkWaitingForGate = false;
            _autowalkGateCommandsSent = false;
            _autowalkGateIsOpen = false;
            // A room actually changed, so the walk is moving again — any combat
            // pause (e.g. after fleeing) no longer applies.
            _autowalkPausedForCombat = false;
            _autowalkPausedForResting = false;

            var steps = _autowalkPath.Steps;
            for (var i = _autowalkStep; i < steps.Count; i++)
            {
                if (string.Equals(steps[i].ToRoom.Vnum, vnum, StringComparison.Ordinal))
                {
                    _autowalkRecomputes = 0;
                    _autowalkMovementRecoveryAttempts = 0;
                    _autowalkStuckRecoveryAttempts = 0;

                    if (_autoFarmActive)
                    {
                        // GMCP normally confirms one room per call (i == _autowalkStep), but if
                        // several updates coalesced into one and this jumped ahead, every room in
                        // between was still physically walked through — credit all of them, not
                        // just the last one, or the farm would keep re-targeting rooms it already
                        // passed (the "wanders back and forth" bug).
                        for (var passed = _autowalkStep; passed <= i; passed++)
                        {
                            _autoFarmVisitedRoomIds.Add(steps[passed].ToRoom.Id);
                        }

                        PushAutoFarmVisitedRoomIds();
                    }

                    _autowalkStep = i + 1;
                    if (_autowalkStep >= steps.Count)
                    {
                        CompleteAutowalkArrival(_autowalkTargetName);
                    }
                    else
                    {
                        PaintRoute(_autowalkPath, _autowalkStep);
                        SendAutowalkStep();
                    }

                    return;
                }
            }

            // Off the planned route — recompute from where we actually are.
            // A recompute is expected occasionally (a failed or extra move), but a
            // recompute on every step means the map disagrees with the server
            // (e.g. duplicate vnums or a misdirected named exit) — without this
            // guard the walk degenerates into an endless move/BFS loop that
            // floods the server with commands and starves the UI thread.
            var targetName = _autowalkTargetName;
            _autowalkRecomputes++;
            EmitSystem(
                $"Autowalk: pokój {vnum} poza trasą — przeliczam trasę ({_autowalkRecomputes}/5).", 33);
            if (_autowalkRecomputes >= 5)
            {
                StopAutowalk(
                    $"Autowalk przerwany: trasa do „{targetName}” schodzi z kursu przy każdym kroku (mapa niezgodna z serwerem?). Wpisz /walk, aby spróbować dalej.",
                    "error",
                    resumable: true);
                return;
            }

            var path = GetPathfinder()?.FindPathByVnum(
                vnum, _autowalkPath.To.Vnum ?? string.Empty, _autowalkExcludedRoomIds);
            if (path is null)
            {
                StopAutowalk(
                    $"Zgubiłem trasę do „{targetName}” — autowalk przerwany. Wpisz /walk, aby kontynuować.",
                    "error",
                    resumable: true);
                return;
            }

            if (path.Steps.Count == 0)
            {
                CompleteAutowalkArrival(targetName);
                return;
            }

            _autowalkPath = path;
            _autowalkStep = 0;
            PaintRoute(path, 0);
            SendAutowalkStep();
        });
    }

    /// <summary>
    /// Executes the bare /walk action: walks to the temporary map-picked target
    /// or shows usage help when no target has been picked.
    /// </summary>
    private void HandleGoToSelectedTarget()
    {
        if (_temporaryTarget is { } target)
        {
            StartAutowalk(target);
        }
        else if (_pendingResumeTarget is { } resume)
        {
            AddToast($"Wznawiam podróż do „{resume.Name}”.", "info");
            StartAutowalk(resume);
        }
        else
        {
            AddToast("Użycie: /walk <nazwa lokacji> — albo zaznacz cel podwójnym kliknięciem na mapie i wpisz samo /walk.", "info");
        }
    }

    /// <summary>
    /// Handles chat-bar commands: /walk &lt;nazwa lokacji lub członka grupy&gt;, /walk leader,
    /// /walk_dodaj &lt;nazwa&gt;, /stop (panic-stop, see <see cref="StopEverything"/>) and /start
    /// (restores what /stop turned off, see <see cref="StartEverything"/>). Returns true when
    /// consumed.
    /// </summary>
    private bool TryHandleAutowalkCommand(string command)
    {
        if (string.Equals(command, "/stop", StringComparison.OrdinalIgnoreCase))
        {
            StopEverything();
            return true;
        }

        if (string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase))
        {
            StartEverything();
            return true;
        }

        const string addPrefix = "/walk_dodaj";
        if (command.StartsWith(addPrefix, StringComparison.OrdinalIgnoreCase)
            && (command.Length == addPrefix.Length || char.IsWhiteSpace(command[addPrefix.Length])))
        {
            var name = command.Length > addPrefix.Length
                ? command[addPrefix.Length..].Trim()
                : string.Empty;
            if (name.Length == 0)
            {
                AddToast("Użycie: /walk_dodaj <nazwa>.", "info");
                return true;
            }

            var currentVnum = Map.CurrentVnum;
            if (string.IsNullOrWhiteSpace(currentVnum))
            {
                AddToast("Nieznana obecna pozycja — brak danych GMCP.", "error");
                return true;
            }

            AddLocationCore(name, currentVnum, isGlobal: false, clearEditor: false);
            return true;
        }

        const string prefix = "/walk";
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var argument = command.Length > prefix.Length ? command[prefix.Length..].Trim() : string.Empty;
        if (argument.Length == 0)
        {
            HandleGoToSelectedTarget();
            return true;
        }

        if (string.Equals(argument, "leader", StringComparison.OrdinalIgnoreCase))
        {
            HandleGoToGroupLeader();
            return true;
        }

        var groupMember = _latestGroupUpdate?.Members.FirstOrDefault(
            member => string.Equals(member.Name, argument, StringComparison.OrdinalIgnoreCase));
        if (groupMember is not null)
        {
            var groupTarget = BuildGroupMemberAutowalkTarget(groupMember);
            if (groupTarget is null)
            {
                AddToast($"Brak pozycji GMCP członka grupy „{groupMember.Name}”.", "error");
                return true;
            }

            StartAutowalk(groupTarget);
            return true;
        }

        var entry = Locations.FirstOrDefault(
            l => string.Equals(l.Name, argument, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            AddToast($"Nie znam lokacji „{argument}”.", "error");
            return true;
        }

        StartAutowalk(entry);
        return true;
    }

    // ========================================================================
    // Auto-farm — repeatedly autowalks to the nearest unvisited room inside
    // AutoFarmRegions, letting the existing autokill-on-room-enter automation
    // (see OnRoomEnterAutomations) do the actual fighting, and pausing to
    // heal/rest whenever HP drops below AutoFarmHpThresholdPercent.
    // ========================================================================

    public bool IsAutoFarmActive => _autoFarmActive;

    public string AutoFarmStatusText
    {
        get => _autoFarmStatusText;
        private set => SetProperty(ref _autoFarmStatusText, value);
    }

    public int MinAutoFarmHpThresholdPercent => ProfileData.MinAutoFarmHpThresholdPercent;

    public int MaxAutoFarmHpThresholdPercent => ProfileData.MaxAutoFarmHpThresholdPercent;

    public int AutoFarmHpThresholdPercent
    {
        get => _autoFarmHpThresholdPercent;
        set
        {
            var clamped = Math.Clamp(
                value,
                ProfileData.MinAutoFarmHpThresholdPercent,
                ProfileData.MaxAutoFarmHpThresholdPercent);
            if (_autoFarmHpThresholdPercent == clamped)
            {
                return;
            }

            _autoFarmHpThresholdPercent = clamped;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    public int MinAutoFarmStepDelayMilliseconds => ProfileData.MinAutoFarmStepDelayMilliseconds;

    public int MaxAutoFarmStepDelayMilliseconds => ProfileData.MaxAutoFarmStepDelayMilliseconds;

    /// <summary>See <see cref="ProfileData.AutoFarmStepDelayMilliseconds"/>.</summary>
    public int AutoFarmStepDelayMilliseconds
    {
        get => _autoFarmStepDelayMilliseconds;
        set
        {
            var clamped = Math.Clamp(
                value,
                ProfileData.MinAutoFarmStepDelayMilliseconds,
                ProfileData.MaxAutoFarmStepDelayMilliseconds);
            if (_autoFarmStepDelayMilliseconds == clamped)
            {
                return;
            }

            _autoFarmStepDelayMilliseconds = clamped;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>One spell name per line, strongest first — auto-farm casts the strongest one
    /// that's currently memorized, or memorizes the strongest one not yet memorized if none are
    /// ready (see <see cref="HealthRecoveryPolicy.GetRecoveryAction"/>). Empty means "just rest,
    /// no self-heal".</summary>
    public string AutoFarmHealSpellNamesText
    {
        get => string.Join('\n', _autoFarmHealSpellNames);
        set
        {
            var names = ParseMobNameLines(value);
            if (_autoFarmHealSpellNames.SequenceEqual(names, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _autoFarmHealSpellNames = names;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>One spell name per line — auto-farm tries to keep every one of these memorized
    /// (see <see cref="HealthRecoveryPolicy.GetSpellsNeedingMemorization"/>). A plain line is
    /// required: missing it mems and rests, the same way it does for <see cref="AutoFarmHealSpellNamesText"/>.
    /// A line prefixed with "~" is opportunistic: it only gets mem'd while the farm is already
    /// stopped for a required reason (HP or another required spell) — missing on its own, it never
    /// blocks the farm.</summary>
    public string AutoFarmMemSpellsText
    {
        get => string.Join('\n', _autoFarmMemSpells.Select(spell => spell.Required ? spell.Name : $"~{spell.Name}"));
        set
        {
            var entries = ParseAutoFarmMemSpellLines(value);
            if (_autoFarmMemSpells.SequenceEqual(entries))
            {
                return;
            }

            _autoFarmMemSpells = entries;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>One spell name per line, in the exact order they should be cast — auto-farm skips
    /// whichever buffs are already active and casts the rest the moment combat actually starts
    /// (see <see cref="TryAutoFarmCastSequence"/>/<see cref="AutoFarmCastSequencePolicy.GetSpellsNeedingCast"/>),
    /// never on plain room entry — there's no enemy to target yet. A leading "!" marks the entry
    /// offensive — aimed at whichever mob the character is currently fighting instead of self, and
    /// always cast (no "already active" check makes sense for a damage spell), only ever at combat
    /// start; without it, the entry is a self-cast buff, which — unlike an offensive entry — is
    /// also kept topped up between fights by the farm's own room-hop maintenance pass (see
    /// <see cref="ContinueAutoFarm"/>'s <c>missingBuffsToCast</c>) once it's worn off, not just at
    /// the next fight's start. A not-yet-memorized entry blocks that same maintenance pass
    /// (mem + rest) ahead of time, the same way <see cref="AutoFarmMemSpellsText"/>'s required
    /// entries do, so nothing needs to mem mid-fight.</summary>
    public string AutoFarmCastSpellsText
    {
        get => string.Join('\n', _autoFarmCastSequence.Select(
            spell => spell.Offensive ? $"!{spell.Name}" : spell.Name));
        set
        {
            var entries = ParseAutoFarmCastSpellLines(value);
            if (_autoFarmCastSequence.SequenceEqual(entries))
            {
                return;
            }

            _autoFarmCastSequence = entries;
            OnPropertyChanged();
            SaveActiveProfile();
        }
    }

    /// <summary>Parses <see cref="AutoFarmCastSpellsText"/>: one name per line, trimmed,
    /// deduplicated case-insensitively (first occurrence wins), a leading "!" marking the entry
    /// offensive instead of a self-cast buff.</summary>
    private static List<AutoFarmCastSpell> ParseAutoFarmCastSpellLines(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AutoFarmCastSpell>();

        foreach (var rawLine in (text ?? string.Empty).Split(
                     ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var offensive = false;
            var name = rawLine;
            if (name.StartsWith('!'))
            {
                offensive = true;
                name = name[1..].Trim();
            }

            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            result.Add(new AutoFarmCastSpell(name, offensive));
        }

        return result;
    }

    /// <summary>Parses <see cref="AutoFarmMemSpellsText"/>: one name per line, trimmed,
    /// deduplicated case-insensitively (first occurrence wins), a leading "~" marking the entry
    /// opportunistic instead of required.</summary>
    private static List<AutoFarmMemSpell> ParseAutoFarmMemSpellLines(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AutoFarmMemSpell>();

        foreach (var rawLine in (text ?? string.Empty).Split(
                     ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var required = true;
            var name = rawLine;
            if (name.StartsWith('~'))
            {
                required = false;
                name = name[1..].Trim();
            }

            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            result.Add(new AutoFarmMemSpell(name, required));
        }

        return result;
    }

    private bool CanStartAutoFarm() =>
        !_autoFarmActive && IsConnected && _autoFarmRegions.Count > 0;

    private void StartAutoFarm()
    {
        if (_autoFarmActive)
        {
            return;
        }

        if (_autoFarmRegions.Count == 0)
        {
            AddToast("Najpierw zaznacz obszar farmy na mapie (prawy przycisk + przeciągnięcie).", "error");
            return;
        }

        var pathfinder = GetPathfinder();
        var index = Map.MapIndex;
        if (pathfinder is null || index is null)
        {
            AddToast("Mapa nie jest załadowana.", "error");
            return;
        }

        var currentVnum = Map.CurrentVnum;
        if (string.IsNullOrWhiteSpace(currentVnum))
        {
            AddToast("Nieznana obecna pozycja — brak danych GMCP.", "error");
            return;
        }

        var currentRoom = index.FindFirstRoomByVnum(currentVnum);
        if (currentRoom is null)
        {
            AddToast("Nie można ustalić obecnego pokoju na mapie.", "error");
            return;
        }

        _autoFarmActive = true;
        _autoFarmVisitedRoomIds = [currentRoom.Id];
        _autoFarmVisitOrder = FarmTraversalPlanner.BuildVisitOrder(
            pathfinder, index, _autoFarmRegions, currentRoom.Id, Map.AutoFarmExcludedRoomIds);
        _autoFarmHealRecoveryAttempts = 0;
        OnPropertyChanged(nameof(IsAutoFarmActive));
        AutoFarmStatusText = "Farma uruchomiona.";
        PushAutoFarmVisitedRoomIds();
        RefreshCommands();
        AddToast("Farma uruchomiona.", "info");
        ContinueAutoFarm();
    }

    private void StopAutoFarm(string message)
    {
        if (!_autoFarmActive)
        {
            return;
        }

        _autoFarmActive = false;
        _autoFarmVisitOrder = null;
        OnPropertyChanged(nameof(IsAutoFarmActive));
        AutoFarmStatusText = "Farma nieaktywna.";
        RefreshCommands();
        // The yellow "visited" coloring is scoped to this farm run only — clear it on stop.
        Map.AutoFarmVisitedRoomIds = new HashSet<int>();

        if (_autowalkPath is not null)
        {
            StopAutowalk(message);
        }
        else
        {
            AddToast(message, "info");
        }
    }

    /// <summary>Mirrors <see cref="_autoFarmVisitedRoomIds"/> onto <see cref="Map"/> as a fresh
    /// snapshot so the map can color every visited room yellow while the farm runs.</summary>
    private void PushAutoFarmVisitedRoomIds() =>
        Map.AutoFarmVisitedRoomIds = new HashSet<int>(_autoFarmVisitedRoomIds);

    /// <summary>Picks the farm's next move: HP/required-spell/cast-sequence-buff maintenance first
    /// (see <see cref="MaintainAutoFarmAndContinueAsync"/> — this is also where a self-buff that
    /// wore off between fights gets recast, keeping the cast sequence's buffs up continuously
    /// rather than only at the start of the next fight), otherwise the nearest unvisited,
    /// non-excluded room in <see cref="_autoFarmRegions"/> via <see cref="FarmTraversalPlanner"/>,
    /// walked to with the same <see cref="StartAutowalk"/> machinery a named-location walk uses
    /// (arrival loops back here through <see cref="CompleteAutowalkArrival"/>).</summary>
    private void ContinueAutoFarm()
    {
        if (!_autoFarmActive)
        {
            return;
        }

        var needsHealRecovery = HealthRecoveryPolicy.IsBelowThreshold(
            _latestHp, _latestMaxHp, _autoFarmHpThresholdPercent);
        var requiredMemSpellNames = _autoFarmMemSpells
            .Where(spell => spell.Required).Select(spell => spell.Name).ToList();
        var missingRequiredSpells = HealthRecoveryPolicy.GetSpellsNeedingMemorization(
            requiredMemSpellNames, _latestMemorizedSpells);
        var missingCastSpellsToMem = AutoFarmCastSequencePolicy.GetSpellsNeedingMemorization(
            _autoFarmCastSequence, _latestMemorizedSpells);
        // Self-buffs from the cast sequence that are memorized but have worn off between fights —
        // TryAutoFarmCastSequence only (re)casts these when combat starts, so without this a buff
        // that expires mid-farm (e.g. during a long walk between mobs) would just stay down until
        // the next fight instead of being kept up continuously. Offensive entries are excluded:
        // there's no enemy to target while just walking between rooms.
        var missingBuffsToCast = AutoFarmCastSequencePolicy.GetSpellsNeedingCast(
                _autoFarmCastSequence, _activeAffectNames, _latestMemorizedSpells)
            .Where(spell => !spell.Offensive)
            .Select(spell => spell.Name)
            .ToList();

        if (needsHealRecovery || missingRequiredSpells.Count > 0 || missingCastSpellsToMem.Count > 0 ||
            missingBuffsToCast.Count > 0)
        {
            if (_autoFarmHealRecoveryAttempts >= MaxAutoFarmHealRecoveryAttempts)
            {
                StopAutoFarm(needsHealRecovery
                    ? "Farma zatrzymana: HP wciąż poniżej progu po kilku próbach leczenia."
                    : "Farma zatrzymana: nie udaje się uzupełnić brakujących zaklęć/buffów po kilku próbach.");
                return;
            }

            _autoFarmHealRecoveryAttempts++;
            AutoFarmStatusText = needsHealRecovery
                ? "HP poniżej progu — leczę się."
                : "Uzupełniam zaklęcia/buffy — odpoczywam.";

            // Already stopping for a required reason — piggyback any missing opportunistic spell
            // onto the same mem/rest pass instead of waiting for a required reason to line up.
            var optionalMemSpellNames = _autoFarmMemSpells
                .Where(spell => !spell.Required).Select(spell => spell.Name).ToList();
            var missingOptionalSpells = HealthRecoveryPolicy.GetSpellsNeedingMemorization(
                optionalMemSpellNames, _latestMemorizedSpells);

            _ = MaintainAutoFarmAndContinueAsync(
                needsHealRecovery,
                [.. missingRequiredSpells, .. missingOptionalSpells, .. missingCastSpellsToMem.Select(spell => spell.Name)],
                missingBuffsToCast);
            return;
        }

        _autoFarmHealRecoveryAttempts = 0;

        if (_autoFarmRegions.Count == 0)
        {
            StopAutoFarm("Farma zatrzymana: obszary nie są już zdefiniowane.");
            return;
        }

        var pathfinder = GetPathfinder();
        var index = Map.MapIndex;
        var currentVnum = Map.CurrentVnum;
        if (pathfinder is null || index is null || string.IsNullOrWhiteSpace(currentVnum))
        {
            StopAutoFarm("Farma zatrzymana: brak danych mapy lub pozycji.");
            return;
        }

        var currentRoom = index.FindFirstRoomByVnum(currentVnum);
        if (currentRoom is null)
        {
            StopAutoFarm("Farma zatrzymana: obecny pokój nie istnieje na mapie.");
            return;
        }

        _autoFarmVisitedRoomIds.Add(currentRoom.Id);
        PushAutoFarmVisitedRoomIds();
        var excludedRoomIds = Map.AutoFarmExcludedRoomIds;

        var next = PickNextAutoFarmRoom(pathfinder, index, currentRoom, excludedRoomIds);
        if (next is null)
        {
            StopAutoFarm(
                $"Farma ukończona — odwiedzono wszystkie pokoje w zaznaczonych obszarach ({_autoFarmVisitedRoomIds.Count}).");
            return;
        }

        var remaining = FarmTraversalPlanner.CountUnvisited(index, _autoFarmRegions, _autoFarmVisitedRoomIds, excludedRoomIds);
        var destinationName = next.Name ?? next.Vnum ?? "?";
        AutoFarmStatusText = $"Farma: idę do „{destinationName}” — pozostało {remaining} pokoi.";

        // PickNextAutoFarmRoom only ever returns rooms with a resolvable vnum (BuildVisitOrder
        // starts from RoomsInRegions, same vnum filter FindNearestUnvisitedRoom used).
        var destination = new AutowalkLocation($"Farma: {destinationName}", next.Vnum!, next.Name);
        if (_autoFarmStepDelayMilliseconds > 0)
        {
            _ = StartAutowalkAfterFarmStepDelayAsync(destination, excludedRoomIds);
        }
        else
        {
            StartAutowalk(destination, excludedRoomIds);
        }
    }

    /// <summary>Waits <see cref="AutoFarmStepDelayMilliseconds"/> before hopping to the next farm
    /// room — see that property's xmldoc (ported from <see cref="ProfileData.AutoFarmStepDelayMilliseconds"/>)
    /// for why: a fast farm can outrun a room-arrival trigger (herbalism, skinning, ...) that's
    /// still mid-command-queue, which this delay gives time to finish first. Re-checks
    /// <see cref="_autoFarmActive"/> after the wait since the farm may have been stopped, or
    /// stopped-and-restarted onto a different plan, while this was pending.</summary>
    private async Task StartAutowalkAfterFarmStepDelayAsync(
        AutowalkLocation destination, IReadOnlySet<int>? excludedRoomIds)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(_autoFarmStepDelayMilliseconds));

        Dispatcher.UIThread.Post(() =>
        {
            if (!_autoFarmActive)
            {
                return;
            }

            StartAutowalk(destination, excludedRoomIds);
        });
    }

    /// <summary>Fires <see cref="_autoFarmCastSequence"/> the moment combat actually starts (see
    /// the GMCP position "fighting" transition in <see cref="UpdateCharacterPosition"/>) — the only
    /// point an offensive entry can fire at all, since it needs a real enemy to target. A buff
    /// entry can also be (re)cast here, but is just as often already covered by then: see
    /// <see cref="ContinueAutoFarm"/>'s own room-hop maintenance pass, which keeps buffs topped up
    /// continuously between fights too, not only at the start of the next one. Only casts what's
    /// already memorized (<see cref="AutoFarmCastSequencePolicy.GetSpellsNeedingCast"/>); a
    /// not-yet-memorized entry is handled ahead of time by that same room-hop maintenance pass,
    /// well before any fight, so nothing needs to pause mid-combat to mem. A buff entry always
    /// targets self; an offensive entry targets whichever mob GMCP Room.People currently reports
    /// the character fighting (<see cref="RoomPerson.Enemy"/> on the character's own entry) — an
    /// offensive entry is skipped entirely, not mis-cast at self, if that isn't known yet
    /// (Room.People hasn't caught up with the fresh "fighting" transition).</summary>
    private void TryAutoFarmCastSequence()
    {
        if (!_autoFarmActive)
        {
            return;
        }

        var castSpells = AutoFarmCastSequencePolicy.GetSpellsNeedingCast(
            _autoFarmCastSequence, _activeAffectNames, _latestMemorizedSpells);
        if (castSpells.Count == 0)
        {
            return;
        }

        var enemyName = _latestRoomPeople
            .FirstOrDefault(person => string.Equals(
                person.Name, _latestCharacterName, StringComparison.OrdinalIgnoreCase))
            ?.Enemy;

        var commands = new List<string>();
        foreach (var spell in castSpells)
        {
            if (spell.Offensive)
            {
                if (string.IsNullOrWhiteSpace(enemyName))
                {
                    continue;
                }

                commands.Add($"cast \"{spell.Name}\" {enemyName}");
            }
            else
            {
                commands.Add($"cast \"{spell.Name}\" self");
            }
        }

        if (commands.Count > 0)
        {
            QueueTriggeredCommands(commands);
        }
    }

    /// <summary>Walks <see cref="_autoFarmVisitOrder"/> (the tour <see cref="StartAutoFarm"/>
    /// planned via <see cref="FarmTraversalPlanner.BuildVisitOrder"/>) for the next room this run
    /// hasn't visited or excluded yet. Rebuilds the order from here first if the regions were
    /// redefined mid-run — detected by there still being unvisited rooms overall (per
    /// <see cref="FarmTraversalPlanner.CountUnvisited"/>) even though none are left in the cached
    /// order, which a fixed order computed for the old regions can't reflect on its own.</summary>
    private MapRoom? PickNextAutoFarmRoom(
        MapPathfinder pathfinder,
        MapIndex index,
        MapRoom currentRoom,
        IReadOnlySet<int>? excludedRoomIds)
    {
        var next = _autoFarmVisitOrder?.FirstOrDefault(room =>
            !_autoFarmVisitedRoomIds.Contains(room.Id) && !(excludedRoomIds?.Contains(room.Id) ?? false));
        if (next is not null)
        {
            return next;
        }

        if (FarmTraversalPlanner.CountUnvisited(index, _autoFarmRegions, _autoFarmVisitedRoomIds, excludedRoomIds) == 0)
        {
            return null;
        }

        _autoFarmVisitOrder = FarmTraversalPlanner.BuildVisitOrder(
            pathfinder, index, _autoFarmRegions, currentRoom.Id, excludedRoomIds);
        return _autoFarmVisitOrder.FirstOrDefault(room => !_autoFarmVisitedRoomIds.Contains(room.Id));
    }

    /// <summary>Casts/memorizes the configured heal spell when <paramref name="needsHealRecovery"/>
    /// (see <see cref="HealthRecoveryPolicy.GetRecoveryAction"/>), memorizes every entry in
    /// <paramref name="missingSpells"/>, (re)casts every entry in <paramref name="buffsToCast"/> —
    /// self-buffs from the cast sequence that have worn off between fights, see
    /// <see cref="ContinueAutoFarm"/>'s <c>missingBuffsToCast</c> — then always rests for a beat —
    /// mirroring <see cref="RecoverMovementAndContinueAsync"/>'s shape for autowalk's own
    /// low-movement recovery, just covering more maintenance needs in one pass instead of one.</summary>
    private async Task MaintainAutoFarmAndContinueAsync(
        bool needsHealRecovery, IReadOnlyList<string> missingSpells, IReadOnlyList<string> buffsToCast)
    {
        if (needsHealRecovery)
        {
            var decision = HealthRecoveryPolicy.GetRecoveryAction(_autoFarmHealSpellNames, _latestMemorizedSpells);
            switch (decision.Action)
            {
                case HealthRecoveryAction.CastHeal:
                    await SendTriggeredCommandAsync($"cast \"{decision.SpellName}\" self");
                    break;
                case HealthRecoveryAction.MemorizeHeal:
                    await SendTriggeredCommandAsync($"mem \"{decision.SpellName}\"");
                    break;
            }
        }

        foreach (var spellName in missingSpells)
        {
            await SendTriggeredCommandAsync($"mem \"{spellName}\"");
        }

        foreach (var spellName in buffsToCast)
        {
            await SendTriggeredCommandAsync($"cast \"{spellName}\" self");
        }

        var restSeconds = _settings.AutowalkRestSeconds;
        await SendTriggeredCommandAsync("rest");
        await Task.Delay(TimeSpan.FromSeconds(restSeconds));
        await SendTriggeredCommandAsync("stand");

        Dispatcher.UIThread.Post(() =>
        {
            if (!_autoFarmActive)
            {
                return;
            }

            ContinueAutoFarm();
        });
    }

    /// <summary>Executes /walk leader: walks to the current group's leader (see
    /// <see cref="CharacterGroupMember.IsLeader"/> from GMCP Char.Group).</summary>
    private void HandleGoToGroupLeader()
    {
        var leader = _latestGroupUpdate?.Members.FirstOrDefault(member => member.IsLeader);
        if (leader is null)
        {
            AddToast("Brak informacji o liderze grupy.", "error");
            return;
        }

        if (string.Equals(leader.Name, _latestCharacterName, StringComparison.OrdinalIgnoreCase))
        {
            AddToast("Jesteś liderem grupy.", "info");
            return;
        }

        var target = BuildGroupMemberAutowalkTarget(leader);
        if (target is null)
        {
            AddToast($"Brak pozycji GMCP lidera „{leader.Name}”.", "error");
            return;
        }

        StartAutowalk(target);
    }

    internal AutowalkLocation? BuildGroupMemberAutowalkTarget(CharacterGroupMember member) =>
        string.IsNullOrWhiteSpace(member.Room)
            ? null
            : new AutowalkLocation(member.Name, member.Room, ResolveRoomDisplay(member.Room));

    // ========================================================================
    // Death marks (last 10 death locations, hard-coded server-line trigger)
    // ========================================================================

    private const int MaxDeathMarks = 10;

    // The server announces death with this exact line; depending on the
    // negotiated charset it arrives with or without Polish diacritics.
    // This trigger is intentionally hard-coded, not a user automation rule.
    private static readonly string[] DeathPhrases =
    [
        "Nie żyjesz, co za pech!!!",
        "Nie zyjesz, co za pech!!!",
    ];

    /// <summary>Last death locations, newest first. Persisted per profile.</summary>
    public ObservableCollection<DeathMarkEntry> Deaths { get; } = [];

    public RelayCommand<DeathMarkEntry> DeleteDeathCommand { get; }
    public RelayCommand<DeathMarkEntry> GoToDeathCommand { get; }

    private static bool IsDeathLine(string line)
    {
        foreach (var phrase in DeathPhrases)
        {
            if (line.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryHandleMapEditorCommandAsync(string command)
    {
        var trimmed = command.Trim();
        string? arguments = null;
        foreach (var prefix in new[] { "/map", "/mapa", "+map" })
        {
            if (string.Equals(trimmed, prefix, StringComparison.OrdinalIgnoreCase))
            {
                arguments = string.Empty;
                break;
            }

            if (trimmed.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            {
                arguments = trimmed[(prefix.Length + 1)..].Trim();
                break;
            }
        }

        if (arguments is null)
        {
            return false;
        }

        if (!LordModeEnabled)
        {
            AddToast("Edytor mapy jest dostępny tylko w trybie lorda.", "error");
            return true;
        }

        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();
        switch (action)
        {
            case "edit":
                if (Map.IsMapEditorActive)
                {
                    Map.StopMapEditor();
                }
                else
                {
                    Map.StartMapEditor();
                }

                break;
            case "start":
                Map.StartMapEditor();
                break;
            case "stop":
                Map.StopMapEditor();
                break;
            case "save":
            case "zapisz":
                await Map.SaveMapEditorAsync();
                break;
            case "undo":
            case "cofnij":
                Map.UndoMapEditor();
                break;
            case "redo":
            case "ponow":
                Map.RedoMapEditor();
                break;
            case "cancel":
            case "anuluj":
                Map.CancelMapEditorChanges();
                break;
            case "diff":
            case "roznice":
                AddToast(await Map.GetMapEditorDiffAsync(), "info");
                return true;
            case "export":
            case "eksport":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map export <ścieżka-do-world-map.json>.", "info");
                    return true;
                }

                AddToast(await Map.ExportMapEditorAsync(parts[1]), "info");
                return true;
            case "import":
                if (parts.Length < 2 || !TryParseConfirmedPath(parts[1], out var importPath))
                {
                    AddToast("Import zastępuje mapę roboczą. Użycie: /map import <ścieżka.json> confirm.", "error");
                    return true;
                }

                AddToast(await Map.ImportMapEditorAsync(importPath), "info");
                return true;
            case "discard":
            case "odrzuc":
                if (parts.Length < 2 ||
                    parts[1].ToLowerInvariant() is not ("confirm" or "potwierdz"))
                {
                    AddToast("Ta komenda usuwa zapisaną mapę roboczą. Użycie: /map discard confirm.", "error");
                    return true;
                }

                AddToast(await Map.DiscardWorkingMapAsync(), "info");
                return true;
            case "resolve":
            case "rozwiaz":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map resolve keep|gmcp.", "info");
                    return true;
                }

                var resolution = parts[1].ToLowerInvariant();
                if (resolution is "keep" or "map" or "mapa")
                {
                    Map.ResolveMapConflictKeepMap();
                }
                else if (resolution is "gmcp" or "replace" or "zastap")
                {
                    Map.ResolveMapConflictUseGmcp();
                }
                else
                {
                    AddToast("Użycie: /map resolve keep|gmcp.", "info");
                    return true;
                }

                break;
            case "step":
            case "krok":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var step))
                {
                    AddToast("Użycie: /map step <1-20>.", "info");
                    return true;
                }

                Map.SetMapEditorStep(step);
                break;
            case "area":
            case "obszar":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map area <nazwa>.", "info");
                    return true;
                }

                Map.CreateMapArea(parts[1]);
                break;
            case "reassign":
            case "przenos":
                if (parts.Length < 2 || parts[1].ToLowerInvariant() is not ("on" or "off"))
                {
                    AddToast("Użycie: /map reassign on|off.", "info");
                    return true;
                }

                Map.SetMoveExistingRoomsToNewArea(
                    string.Equals(parts[1], "on", StringComparison.OrdinalIgnoreCase));
                break;
            case "symbol":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map symbol <znak>; wartości -1 lub clear usuwają symbol.", "info");
                    return true;
                }

                Map.SetCurrentMapRoomSymbol(parts[1]);
                break;
            case "label":
            case "etykieta":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map label <tekst>. Prefiksy #, ## i ### zmieniają rozmiar.", "info");
                    return true;
                }

                var labelParts = parts[1].Split(
                    ' ',
                    3,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var labelAction = labelParts[0].ToLowerInvariant();
                if (labelAction is "list" or "lista")
                {
                    Map.ShowCurrentAreaMapLabels();
                }
                else if (labelAction is "delete" or "remove" or "usun")
                {
                    if (labelParts.Length < 2 || !int.TryParse(labelParts[1], out var labelId))
                    {
                        AddToast("Użycie: /map label delete <id>.", "info");
                        return true;
                    }

                    Map.RemoveMapLabel(labelId);
                }
                else if (labelAction is "set" or "edit" or "zmien")
                {
                    if (labelParts.Length < 3 || !int.TryParse(labelParts[1], out var labelId))
                    {
                        AddToast("Użycie: /map label set <id> <tekst>.", "info");
                        return true;
                    }

                    Map.SetMapLabelText(labelId, labelParts[2]);
                }
                else
                {
                    Map.AddCurrentMapLabel(parts[1]);
                }

                break;
            case "room":
            case "pokoj":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map room name|sector|weight|move <wartość>.", "info");
                    return true;
                }

                var roomParts = parts[1].Split(
                    ' ',
                    2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (roomParts.Length < 2)
                {
                    AddToast("Użycie: /map room name|sector|weight|move <wartość>.", "info");
                    return true;
                }

                switch (roomParts[0].ToLowerInvariant())
                {
                    case "name":
                    case "nazwa":
                        Map.SetCurrentMapRoomName(roomParts[1]);
                        break;
                    case "sector":
                    case "sektor":
                        Map.SetCurrentMapRoomSector(roomParts[1]);
                        break;
                    case "weight":
                    case "waga":
                        if (!double.TryParse(
                                roomParts[1],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var weight))
                        {
                            AddToast("Użycie: /map room weight <liczba>.", "info");
                            return true;
                        }

                        Map.SetCurrentMapRoomWeight(weight);
                        break;
                    case "move":
                    case "przenies":
                        var coordinates = roomParts[1].Split(
                            ' ',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (coordinates.Length != 3 ||
                            !double.TryParse(coordinates[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                            !double.TryParse(coordinates[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ||
                            !double.TryParse(coordinates[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                        {
                            AddToast("Użycie: /map room move <x> <y> <z>.", "info");
                            return true;
                        }

                        Map.MoveCurrentMapRoom(new MapCoordinates(x, y, z));
                        break;
                    default:
                        AddToast("Użycie: /map room name|sector|weight|move <wartość>.", "info");
                        return true;
                }

                break;
            case "forget":
            case "zapomnij":
                Map.ForgetCurrentMapRoom();
                break;
            case "special":
            case "specjalne":
                if (parts.Length < 2)
                {
                    AddToast("Użycie: /map special <kierunek> <komenda>; komenda -1 usuwa przejście.", "info");
                    return true;
                }

                var specialParts = parts[1].Split(
                    ' ',
                    2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (specialParts.Length < 2)
                {
                    AddToast("Użycie: /map special <kierunek> <komenda>.", "info");
                    return true;
                }

                if (specialParts[1] == "-1")
                {
                    Map.RemoveMapSpecialExit(specialParts[0]);
                    break;
                }

                var specialDecision = Map.PrepareMapSpecialMovement(specialParts[0], specialParts[1]);
                if (!specialDecision.Allow)
                {
                    AddToast(specialDecision.Message ?? "Nie można dodać przejścia specjalnego.", "error");
                    return true;
                }

                await SendMapSpecialCommandAsync(specialDecision.Command);
                break;
            case "check":
            case "sprawdz":
                Map.ValidateEditedMap();
                break;
            case "status":
                var mapStatus =
                    $"{Map.MapEditorStatus} {Map.MapEditorSourceDescription} " +
                    $"Aktywne: {(Map.IsMapEditorActive ? "tak" : "nie")}; " +
                    $"oczekuje na Room.Info: {(Map.IsMapEditorAwaitingRoomInfo ? "tak" : "nie")}; " +
                    $"vnum: {Map.CurrentVnum ?? "brak"}; " +
                    $"wybrany obszar: {Map.SelectedArea?.Name ?? "brak"}; " +
                    $"przenoszenie znanych pokoi: {(Map.MoveExistingRoomsToNewArea ? "tak" : "nie")}.";
                AddToast(mapStatus, "info");
                EmitSystem($"Mapper: {mapStatus}", 36);
                return true;
            case "info":
                Map.ShowCurrentMapRoomInfo();
                break;
            case "show":
            case "pokaz":
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                {
                    AddToast("Użycie: /map show <vnum>.", "info");
                    return true;
                }

                var showVnum = parts[1].Trim();
                if (Map.FocusRoomByVnum(showVnum) is null)
                {
                    AddToast($"VNUM {showVnum} nie istnieje w mapie.", "error");
                    return true;
                }

                _dockFactory.ShowTool("Map");
                return true;
            default:
                AddToast("Komendy mappera: start, stop, save, undo, redo, cancel, status, info, check, diff, import, export, discard, resolve, step, area, reassign, room, symbol, label, forget, show i special. Działają prefiksy /map, /mapa i +map.", "info");
                return true;
        }

        AddToast(Map.MapEditorStatus, Map.MapEditorStatus.StartsWith("Konflikt", StringComparison.OrdinalIgnoreCase) ? "error" : "info");
        return true;
    }

    private static bool TryParseConfirmedPath(string arguments, out string path)
    {
        foreach (var confirmation in new[] { " confirm", " potwierdz" })
        {
            if (arguments.EndsWith(confirmation, StringComparison.OrdinalIgnoreCase))
            {
                path = arguments[..^confirmation.Length].Trim();
                return path.Length > 0;
            }
        }

        path = string.Empty;
        return false;
    }

    private async Task SendMapSpecialCommandAsync(string command)
    {
        EmitCommandEcho(command);
        try
        {
            await _session.SendCommandAsync(command);
        }
        catch (Exception exception)
        {
            Map.CancelPendingMapMovement($"Nie udało się wysłać przejścia specjalnego: {exception.Message}");
            EmitSystem(exception.Message, 31);
        }
    }

    private void OnRoomSnapshotReceived(RoomSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => Map.HandleRoomSnapshot(snapshot));
    }

    private void OnMapEditorActiveChanged(bool active)
    {
        if (active)
        {
            StopAutowalk("Autowalk zatrzymany na czas mapowania.");
        }
    }

    /// <summary>
    /// Records the current GMCP position as a death mark. Runs on the UI
    /// thread (posted from the network receive loop).
    /// </summary>
    private void RecordDeath()
    {
        var vnum = Map.CurrentVnum;
        if (string.IsNullOrWhiteSpace(vnum))
        {
            AddToast("Zginąłeś, ale pozycja jest nieznana (brak danych GMCP) — miejsce śmierci nie zostało zapisane.", "error");
            return;
        }

        var roomName = Map.MapIndex?.FindFirstRoomByVnum(vnum)?.Name;
        var entry = new DeathMarkEntry(vnum, roomName, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        Deaths.Insert(0, entry);
        while (Deaths.Count > MaxDeathMarks)
        {
            Deaths.RemoveAt(Deaths.Count - 1);
        }

        SaveActiveProfile();
        AddToast($"Zapisano miejsce śmierci: {entry.Display}.", "error");
    }

    private void DeleteDeath(DeathMarkEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        Deaths.Remove(entry);
        SaveActiveProfile();
    }

    private void GoToDeath(DeathMarkEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        StartAutowalk(new AutowalkLocation(
            string.IsNullOrWhiteSpace(entry.RoomName) ? $"miejsce śmierci (vnum {entry.Vnum})" : entry.RoomName!,
            entry.Vnum,
            entry.RoomName));
    }

    // ========================================================================
    // Required buffs (user-defined, matched against Char.Affects)
    // ========================================================================

    /// <summary>Named buff sets persisted per profile.</summary>
    public ObservableCollection<BuffSetEntry> BuffSets { get; } = [];

    /// <summary>The set displayed in the widget and used by /recast.</summary>
    public BuffSetEntry? SelectedBuffSet
    {
        get => _selectedBuffSet;
        set
        {
            if (!SetProperty(ref _selectedBuffSet, value) || value is null)
            {
                return;
            }

            BuffSetNameDraft = value.Name;
            OnPropertyChanged(nameof(RequiredBuffs));
            RefreshBuffIndicators();
            RenameBuffSetCommand.NotifyCanExecuteChanged();
            if (!_loadingBuffSets)
            {
                SaveActiveProfile();
            }
        }
    }

    /// <summary>Buffs in the currently selected set.</summary>
    public ObservableCollection<BuffWatchEntry> RequiredBuffs =>
        SelectedBuffSet?.Buffs ?? [];

    public RelayCommand AddBuffCommand { get; }
    public RelayCommand<BuffWatchEntry> DeleteBuffCommand { get; }
    public RelayCommand CreateBuffSetCommand { get; }
    public RelayCommand RenameBuffSetCommand { get; }
    public RelayCommand DeleteBuffSetCommand { get; }
    public AsyncRelayCommand RecastBuffsCommand { get; }
    public AsyncRelayCommand<BuffWatchEntry> RecastSingleBuffCommand { get; }
    public AsyncRelayCommand CastRefreshOnGroupCommand { get; }

    /// <summary>Header badge for the buffs section, e.g. "2/3" (active/required).</summary>
    public string BuffsBadge => RequiredBuffs.Count == 0
        ? "0"
        : $"{RequiredBuffs.Count(b => b.IsActive)}/{RequiredBuffs.Count}";

    /// <summary>True when at least one required buff is missing.</summary>
    public bool BuffsAlert => RequiredBuffs.Any(b => !b.IsActive);

    public bool CanDeleteBuffSet => BuffSets.Count > 1;

    private void RefreshBuffIndicators()
    {
        OnPropertyChanged(nameof(BuffsBadge));
        OnPropertyChanged(nameof(BuffsAlert));
        UpdateMemToolTitle();
    }

    /// <summary>
    /// Mirrors the buff state onto the Mem dock tab title ("📜 Mem i Buffy 2/3"), so the
    /// missing-buff signal is visible even when another tab covers the panel.
    /// </summary>
    private void UpdateMemToolTitle()
    {
        var tool = _dockFactory.AllTools.FirstOrDefault(
            t => string.Equals(t.Id, "MemSpells", StringComparison.Ordinal));
        if (tool is null)
        {
            return;
        }

        tool.Title = RequiredBuffs.Count == 0 ? "📜 Mem i Buffy" : $"📜 Mem i Buffy {BuffsBadge}";
    }

    /// <summary>
    /// Mirrors the character's live vitals and world time/weather onto the Terminal dock tab
    /// title — folds the former standalone "Postać" panel's fields (Imię/Poziom/Płeć/Pozycja)
    /// plus live Mud.TimeInfo/Mud.Weather GMCP data into the Terminal's own header bar instead
    /// of a separate row inside its content, so they're visible without spending any of the
    /// Terminal's own vertical space.
    /// </summary>
    private void UpdateTerminalToolTitle()
    {
        var tool = _dockFactory.AllTools.FirstOrDefault(
            t => string.Equals(t.Id, "Terminal", StringComparison.Ordinal));
        if (tool is null)
        {
            return;
        }

        tool.Title = $"Terminal — Imię: {Vitals.Name}, Poziom: {Vitals.Level}, " +
                     $"Płeć: {Vitals.SexDisplay}, Pozycja: {Vitals.PositionDisplay} | " +
                     $"{WorldTime.DayName} ({WorldTime.Day} {WorldTime.Month}, {WorldTime.Year} r., {WorldTime.Era}), " +
                     $"{WorldTime.TimeName}, Niebo: {WorldTime.Sky}, Wiatr: {WorldTime.Wind}";
    }

    public string NewBuffName
    {
        get => _newBuffName;
        set
        {
            if (SetProperty(ref _newBuffName, value))
            {
                AddBuffCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewBuffSetName
    {
        get => _newBuffSetName;
        set
        {
            if (SetProperty(ref _newBuffSetName, value))
            {
                CreateBuffSetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string BuffSetNameDraft
    {
        get => _buffSetNameDraft;
        set
        {
            if (SetProperty(ref _buffSetNameDraft, value))
            {
                RenameBuffSetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void CreateBuffSet()
    {
        var name = NewBuffSetName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (BuffSets.Any(set => string.Equals(set.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            AddToast($"Zestaw „{name}” już istnieje.", "info");
            return;
        }

        var set = new BuffSetEntry { Name = name };
        BuffSets.Add(set);
        NewBuffSetName = string.Empty;
        DeleteBuffSetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteBuffSet));
        SelectedBuffSet = set;
    }

    private void RenameSelectedBuffSet()
    {
        if (SelectedBuffSet is not { } selected)
        {
            return;
        }

        var name = BuffSetNameDraft.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (BuffSets.Any(set => !ReferenceEquals(set, selected)
            && string.Equals(set.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            AddToast($"Zestaw „{name}” już istnieje.", "info");
            return;
        }

        selected.Name = name;
        BuffSetNameDraft = name;
        SaveActiveProfile();
    }

    private void DeleteSelectedBuffSet()
    {
        if (SelectedBuffSet is not { } selected || BuffSets.Count <= 1)
        {
            return;
        }

        var index = BuffSets.IndexOf(selected);
        BuffSets.Remove(selected);
        DeleteBuffSetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteBuffSet));
        SelectedBuffSet = BuffSets[Math.Min(index, BuffSets.Count - 1)];
    }

    private void AddBuff()
    {
        var name = NewBuffName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var normalized = BuffWatchEntry.NormalizeName(name);
        if (RequiredBuffs.Any(b => string.Equals(
                BuffWatchEntry.NormalizeName(b.Name), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            AddToast($"Buff „{name}” jest już na liście.", "info");
            return;
        }

        RequiredBuffs.Add(new BuffWatchEntry(name)
        {
            IsActive = _activeAffectNames.Contains(normalized),
        });
        NewBuffName = string.Empty;
        RefreshBuffIndicators();
        SaveActiveProfile();
    }

    private void DeleteBuff(BuffWatchEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        RequiredBuffs.Remove(entry);
        RefreshBuffIndicators();
        SaveActiveProfile();
    }

    /// <summary>
    /// Sends "cast &quot;nazwa&quot; self" for every required buff missing from
    /// the latest Char.Affects. Bound to the RECAST button and the /recast command.
    /// </summary>
    private async Task RecastMissingBuffsAsync()
    {
        if (!IsConnected)
        {
            AddToast("Nie połączono — nie można rzucić buffów.", "error");
            return;
        }

        var missing = RequiredBuffs.Where(b => !b.IsActive).ToList();
        if (missing.Count == 0)
        {
            AddToast("Wszystkie wymagane buffy są aktywne.", "info");
            return;
        }

        foreach (var buff in missing)
        {
            await SendTriggeredCommandAsync($"cast \"{buff.Name}\" self");
        }
    }

    /// <summary>
    /// Sends "cast &quot;nazwa&quot; self" for a single buff. Bound to clicking an
    /// individual buff entry in the buffs panel.
    /// </summary>
    private async Task RecastSingleBuffAsync(BuffWatchEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!IsConnected)
        {
            AddToast("Nie połączono — nie można rzucić buffa.", "error");
            return;
        }

        await SendTriggeredCommandAsync($"cast \"{entry.Name}\" self");
    }

    /// <summary>Orders every other (non-NPC) group member to cast refresh on themselves, in turn,
    /// so everyone can keep moving under their own power during a long group trip instead of
    /// relying on autowalk's own self-only recovery (see <see cref="AutowalkRecoveryPolicy"/>).
    /// Uses the group "order" command rather than casting it directly at each member — the caster
    /// still needs their own refresh memorized and cast on "self" once ordered.</summary>
    private async Task CastRefreshOnGroupAsync()
    {
        if (!IsConnected)
        {
            AddToast("Nie połączono — nie można wysłać rozkazu.", "error");
            return;
        }

        var targets = BuildOtherGroupMemberNames(_latestGroupUpdate, _latestCharacterName);
        if (targets.Count == 0)
        {
            AddToast("Brak członków drużyny do odświeżenia.", "info");
            return;
        }

        foreach (var name in targets)
        {
            await SendTriggeredCommandAsync($"order {name} cast refresh");
        }
    }

    /// <summary>Orders any group member whose GMCP movement just dropped to "zamęczony" to cast
    /// refresh on themselves — see <see cref="GroupExhaustionRefreshPolicy"/> for the once-per-
    /// exhaustion debounce. Runs on whatever thread delivers the GMCP group update, matching
    /// <see cref="TryAutoAssist"/>.</summary>
    private void TryAutoOrderExhaustedGroupRefresh(CharacterGroupUpdate update)
    {
        var names = _groupExhaustionRefresh.GetMembersToOrder(
            _profileSettings.AutoGroupRefreshOnExhaustedEnabled && IsConnected, update, _latestCharacterName);
        if (names.Count == 0)
        {
            return;
        }

        QueueTriggeredCommands(names.Select(name => $"order {name} cast refresh").ToArray());
    }

    /// <summary>Every other (non-NPC) member of the current group — the set that "order" fan-out
    /// commands (refresh, autostand/autosit) target one at a time.</summary>
    internal static IReadOnlyList<string> BuildOtherGroupMemberNames(
        CharacterGroupUpdate? group, string? selfName) =>
        group?.Members
            .Where(member => !member.IsNpc
                && !string.Equals(member.Name, selfName, StringComparison.OrdinalIgnoreCase))
            .Select(member => member.Name)
            .ToArray()
        ?? [];

    /// <summary>Orders every other group member to match a stand/sit change of our own, but only
    /// while we're the GMCP-reported group leader — "order" only works for the leader, and firing
    /// it as a follower would just spam failing commands.</summary>
    private void TryAutoOrderGroupPosition(string command, bool enabled)
    {
        if (!IsConnected)
        {
            return;
        }

        var commands = BuildGroupPositionOrderCommands(
            _latestGroupUpdate, _latestCharacterName, command, enabled);
        if (commands.Count == 0)
        {
            return;
        }

        QueueTriggeredCommands(commands);
    }

    /// <summary>Pure decision behind <see cref="TryAutoOrderGroupPosition"/>: empty unless
    /// <paramref name="enabled"/>, we're the group's own leader, and there's at least one other
    /// member to order.</summary>
    internal static IReadOnlyList<string> BuildGroupPositionOrderCommands(
        CharacterGroupUpdate? group, string? selfName, string command, bool enabled)
    {
        if (!enabled || group is null
            || !string.Equals(group.Leader, selfName, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return BuildOtherGroupMemberNames(group, selfName)
            .Select(name => $"order {name} {command}")
            .ToArray();
    }

    /// <summary>Orders every NPC in the current group (a summoned/charmed pet) to assist as soon
    /// as the local character enters combat. Unlike <see cref="TryAutoOrderGroupPosition"/>,
    /// doesn't require group leadership — ordering your own pet doesn't need it.</summary>
    private void TryAutoAssistNpc()
    {
        if (!IsConnected)
        {
            return;
        }

        var commands = BuildAutoAssistNpcCommands(_latestGroupUpdate, AutoAssistNpcEnabled);
        if (commands.Count == 0)
        {
            return;
        }

        QueueTriggeredCommands(commands);
    }

    /// <summary>Gates <see cref="TryAutoAssistNpc"/> on Room.People actually reporting the local
    /// character as fighting. The GMCP position can flip to "fighting" before the MUD registers
    /// who it's fighting (see the identical race documented on <see cref="AutoAssistPolicy"/>) —
    /// sending "order &lt;pet&gt; assist" before that lands gives the pet nothing to assist into,
    /// so the game just ignores the order. Called both right on the position transition (in case
    /// Room.People already has the answer) and again from <see cref="OnRoomPeopleChanged"/> as
    /// updates arrive, until it fires exactly once per fight.</summary>
    private void TryAutoAssistNpcIfConfirmed()
    {
        if (!_autoAssistNpcPending)
        {
            return;
        }

        var self = _latestRoomPeople.FirstOrDefault(person =>
            string.Equals(person.Name, _latestCharacterName, StringComparison.OrdinalIgnoreCase));
        if (self is not { IsFighting: true })
        {
            return;
        }

        _autoAssistNpcPending = false;
        TryAutoAssistNpc();
    }

    /// <summary>Pure decision behind <see cref="TryAutoAssistNpc"/>: an "order &lt;N&gt;.&lt;keyword&gt;
    /// assist" for every NPC GMCP currently reports in the group. The MUD doesn't recognize the
    /// full display name as an order target (e.g. "order Potężny zombie assist" silently does
    /// nothing) — it needs a single lowercase, diacritic-free keyword (see
    /// <see cref="BuildOrderKeyword"/>), numbered from 1 among NPCs sharing that keyword so two
    /// "Potężny zombie" pets become "1.potezny" and "2.potezny" instead of colliding.</summary>
    internal static IReadOnlyList<string> BuildAutoAssistNpcCommands(
        CharacterGroupUpdate? group, bool enabled)
    {
        if (!enabled || group is null)
        {
            return [];
        }

        var indexByKeyword = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<string>();
        foreach (var member in group.Members)
        {
            if (!member.IsNpc)
            {
                continue;
            }

            var keyword = BuildOrderKeyword(member.Name);
            var index = indexByKeyword.TryGetValue(keyword, out var previous) ? previous + 1 : 1;
            indexByKeyword[keyword] = index;
            commands.Add($"order {index}.{keyword} assist");
        }

        return commands;
    }

    /// <summary>The MUD's keyword-targeting syntax matches on a single word — takes the name's
    /// first word, folds Polish diacritics (see <see cref="PolishText.Fold"/>) and lowercases it,
    /// e.g. "Potężny zombie" -&gt; "potezny".</summary>
    private static string BuildOrderKeyword(string name)
    {
        var firstWord = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return PolishText.Fold(firstWord).ToLowerInvariant();
    }

    /// <summary>Sends "stand" after a knockdown (see "Walka" in Automaty) — fires from both the
    /// GMCP "lying" position transition (<see cref="UpdateCharacterPosition"/>) and a hard-coded
    /// text match (<see cref="OnLineReceived"/>; see <see cref="CombatStatusPolicy.IsKnockedDownLine"/>
    /// for the recognized phrases), whichever arrives first.</summary>
    private void TryAutostand()
    {
        if (!IsConnected || !AutoStandOnLyingEnabled)
        {
            return;
        }

        QueueTriggeredCommands(["stand"]);
    }

    /// <summary>Sends a single movement command (n/s/e/w) fired by arrow-key navigation on the
    /// focused map (see WorldMapControl.MovementKeyPressed) — same queued-send path as autostand/
    /// autowield, so it can't race with an in-flight batch of triggered commands.</summary>
    internal void SendMapMovementCommand(string direction)
    {
        if (!IsConnected)
        {
            return;
        }

        QueueTriggeredCommands([direction]);
    }

    /// <summary>Picks the weapon back up and re-equips it after a disarm (see "Walka" in
    /// Automaty) — fires from the hard-coded "rozbraja cię" text match in
    /// <see cref="OnLineReceived"/>.</summary>
    private void TryAutowield()
    {
        var commands = BuildAutowieldCommands(AutowieldEnabled, AutowieldWeaponName);
        if (!IsConnected || commands.Count == 0)
        {
            return;
        }

        QueueTriggeredCommands(commands);
    }

    /// <summary>Pure decision behind <see cref="TryAutowield"/>: "get &lt;weapon&gt;" then
    /// "wield &lt;weapon&gt;", only when enabled and a weapon name is configured.</summary>
    internal static IReadOnlyList<string> BuildAutowieldCommands(bool enabled, string weaponName)
    {
        if (!enabled || string.IsNullOrWhiteSpace(weaponName))
        {
            return [];
        }

        return [$"get {weaponName}", $"wield {weaponName}"];
    }

    // ========================================================================
    // Profiles
    // ========================================================================

    public ObservableCollection<string> AvailableProfiles { get; } = [];

    public bool HasProfiles => AvailableProfiles.Count > 0;

    public RelayCommand SelectProfileCommand { get; }
    public RelayCommand CreateProfileCommand { get; }
    public RelayCommand SwitchProfileCommand { get; }
    public RelayCommand<string> DeleteProfileCommand { get; }

    /// <summary>Name of the currently active profile, or null before one is chosen.</summary>
    public string? ActiveProfileName
    {
        get => _activeProfileName;
        private set
        {
            if (SetProperty(ref _activeProfileName, value))
            {
                OnPropertyChanged(nameof(IsProfileSelected));
                SwitchProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>False shows the profile-picker overlay.</summary>
    public bool IsProfileSelected => _activeProfileName is not null;

    public string? SelectedProfileName
    {
        get => _selectedProfileName;
        set
        {
            if (SetProperty(ref _selectedProfileName, value))
            {
                LoadSelectedProfileEndpoint(value);
                SelectProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedProfileLogin
    {
        get => _selectedProfileLogin;
        set => SetProperty(ref _selectedProfileLogin, value);
    }

    /// <summary>Password for the account being created in the picker.</summary>
    public string NewProfilePassword
    {
        get => _newProfilePassword;
        set => SetProperty(ref _newProfilePassword, value);
    }

    /// <summary>
    /// Optional new password typed when selecting an existing account;
    /// non-empty replaces the stored one, empty keeps it.
    /// </summary>
    public string SelectedProfilePassword
    {
        get => _selectedProfilePassword;
        set => SetProperty(ref _selectedProfilePassword, value);
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (SetProperty(ref _newProfileName, value))
            {
                CreateProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewProfileLogin
    {
        get => _newProfileLogin;
        set => SetProperty(ref _newProfileLogin, value);
    }

    public string NewProfileHost
    {
        get => _newProfileHost;
        set => SetProperty(ref _newProfileHost, value);
    }

    public int NewProfilePort
    {
        get => _newProfilePort;
        set => SetProperty(ref _newProfilePort, value);
    }

    public string NewProfileEncoding
    {
        get => _newProfileEncoding;
        set => SetProperty(ref _newProfileEncoding, value);
    }

    private void LoadSelectedProfileEndpoint(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || _profiles.Load(name) is not { } profile)
        {
            SelectedProfileLogin = string.Empty;
            Host = "killer-mud.pl";
            Port = 4004;
            Encoding = MudTextEncodings.Auto;
            return;
        }

        SelectedProfileLogin = ResolveProfileLogin(profile);
        Host = ResolveProfileHost(profile);
        Port = ResolveProfilePort(profile);
        Encoding = ResolveProfileEncoding(profile);
    }

    private void SelectProfile()
    {
        var name = SelectedProfileName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var profile = _profiles.Load(name) ?? new ProfileData { Name = name };
        profile.Login = string.IsNullOrWhiteSpace(SelectedProfileLogin)
            ? name
            : SelectedProfileLogin.Trim();
        profile.Host = Host.Trim();
        profile.Port = Port;
        profile.Encoding = Encoding;

        // A password typed in the picker replaces the stored one.
        var typedPassword = SelectedProfilePassword;
        if (!string.IsNullOrEmpty(typedPassword))
        {
            profile.EncryptedPassword = PasswordProtector.Protect(typedPassword);
            SelectedProfilePassword = string.Empty;
        }

        _profiles.Save(profile);
        ActivateProfile(profile);
    }

    private void CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_profiles.Exists(name))
        {
            // Same name already stored — just activate it instead of overwriting.
            ActivateProfile(_profiles.Load(name) ?? new ProfileData { Name = name });
            NewProfileName = string.Empty;
            return;
        }

        var profile = new ProfileData
        {
            Name = name,
            Login = string.IsNullOrWhiteSpace(NewProfileLogin) ? name : NewProfileLogin.Trim(),
            Host = NewProfileHost.Trim(),
            Port = NewProfilePort,
            Encoding = NewProfileEncoding,
            EncryptedPassword = PasswordProtector.Protect(NewProfilePassword),
            NeedsRegistration = true,
            Rules =
            [
                new ProfileRule
                {
                    Name = "Skrót look",
                    Type = "alias",
                    Pattern = "^l$",
                    Action = "look",
                    IsEnabled = true,
                },
            ],
        };

        _profiles.Save(profile);

        if (!AvailableProfiles.Contains(name))
        {
            AvailableProfiles.Add(name);
        }

        NewProfileName = string.Empty;
        NewProfileLogin = string.Empty;
        NewProfilePassword = string.Empty;
        ActivateProfile(profile);
    }

    private void DeleteProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            _profiles.Delete(name);
        }
        catch (IOException exception)
        {
            AddToast($"Nie udało się usunąć konta: {exception.Message}", "error");
            return;
        }

        AvailableProfiles.Remove(name);
        if (SelectedProfileName == name)
        {
            SelectedProfileName = null;
        }

        AddToast($"Konto „{name}” usunięte.", "info");
    }

    private void SwitchProfile()
    {
        if (!IsProfileSelected || IsConnected)
        {
            return;
        }

        SaveActiveProfile();
        CancelAllTimers();
        SelectedProfileName = ActiveProfileName;
        ActiveProfileName = null;
        _activeProfileLogin = string.Empty;
        _activeProfilePassword = string.Empty;
        _activeProfileNeedsRegistration = false;
        _activeProfileLastKnownWriteUtc = null;
    }

    private static FarmRegion ToFarmRegion(ProfileFarmRegion region) =>
        new(region.AreaId, region.Z, region.MinX, region.MinY, region.MaxX, region.MaxY);

    private static ProfileFarmRegion ToProfileFarmRegion(FarmRegion region) => new()
    {
        AreaId = region.AreaId,
        Z = region.Z,
        MinX = region.MinX,
        MinY = region.MinY,
        MaxX = region.MaxX,
        MaxY = region.MaxY,
    };

    private void ActivateProfile(ProfileData profile)
    {
        StopAutowalk("Autowalk zatrzymany (zmiana konta).");

        // Suppress per-add tree rebuilds; rebuild once after the bulk load below.
        _suppressTreeRebuild = true;

        Notes.Clear();
        AutomationRules.Clear();
        Timers.Clear();
        Locations.Clear();
        Folders.Clear();
        Deaths.Clear();
        _loadingBuffSets = true;
        BuffSets.Clear();

        // Globals first, then the profile's own entries.
        LoadGlobalEntries();

        foreach (var folder in profile.Folders)
        {
            Folders.Add(MakeFolderNode(folder, isGlobal: false));
        }

        foreach (var note in profile.Notes)
        {
            Notes.Add(MakeNoteEntry(note, isGlobal: false));
        }

        foreach (var rule in profile.Rules)
        {
            AutomationRules.Add(MakeRuleEntry(rule, isGlobal: false));
        }

        foreach (var timer in profile.Timers)
        {
            Timers.Add(MakeTimerEntry(timer, isGlobal: false));
        }

        foreach (var location in profile.Locations)
        {
            Locations.Add(MakeLocationEntry(location, isGlobal: false));
        }

        foreach (var death in profile.Deaths.Take(MaxDeathMarks))
        {
            var room = Map.MapIndex?.FindFirstRoomByVnum(death.Vnum);
            Deaths.Add(new DeathMarkEntry(
                death.Vnum,
                string.IsNullOrWhiteSpace(death.RoomName) ? room?.Name : death.RoomName,
                death.When));
        }

        _knownSpells = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var spell in profile.KnownSpells)
        {
            _knownSpells[spell.Name] = spell.Known;
        }

        Map.SpellKnowledge = new Dictionary<string, bool>(_knownSpells, StringComparer.OrdinalIgnoreCase);

        _knownSkills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in profile.KnownSkills)
        {
            _knownSkills[skill.Name] = skill.Current;
        }

        Map.SkillKnowledge = new Dictionary<string, int>(_knownSkills, StringComparer.OrdinalIgnoreCase);

        // A profile saved before multiple regions were supported only has the single legacy
        // field — migrate it into a one-entry list instead of silently dropping the character's
        // already-configured region. Once this profile is saved again the list will be non-empty
        // (even a single drawn region ends up there), so this fallback never re-triggers for it.
        _autoFarmRegions = profile.AutoFarmRegions.Count > 0
            ? profile.AutoFarmRegions.Select(ToFarmRegion).ToList()
            : profile.AutoFarmRegion is { } legacyRegion
                ? [ToFarmRegion(legacyRegion)]
                : [];
        Map.SetAutoFarmRegions(_autoFarmRegions);
        _autoFarmHpThresholdPercent = Math.Clamp(
            profile.AutoFarmHpThresholdPercent,
            ProfileData.MinAutoFarmHpThresholdPercent,
            ProfileData.MaxAutoFarmHpThresholdPercent);
        _autoFarmStepDelayMilliseconds = Math.Clamp(
            profile.AutoFarmStepDelayMilliseconds,
            ProfileData.MinAutoFarmStepDelayMilliseconds,
            ProfileData.MaxAutoFarmStepDelayMilliseconds);
        // A profile saved before the priority list existed only has the single legacy field —
        // migrate it into a one-entry list instead of silently dropping the character's
        // already-configured heal spell. Once this profile is saved again the list will be
        // non-empty, so this fallback never re-triggers for it.
        _autoFarmHealSpellNames = profile.AutoFarmHealSpellNames.Count > 0
            ? profile.AutoFarmHealSpellNames.ToList()
            : string.IsNullOrWhiteSpace(profile.AutoFarmHealSpellName)
                ? []
                : [profile.AutoFarmHealSpellName];
        // A profile saved before the required/opportunistic split existed only has the flat
        // legacy list — migrate it into the new list (every entry implicitly required) instead of
        // silently dropping the character's already-configured spells. Once this profile is saved
        // again the list will be non-empty, so this fallback never re-triggers for it.
        _autoFarmMemSpells = profile.AutoFarmMemSpells.Count > 0
            ? profile.AutoFarmMemSpells.ToList()
            : profile.AutoFarmRequiredMemorizedSpells
                .Select(name => new AutoFarmMemSpell(name, Required: true))
                .ToList();
        // A profile saved before offensive entries were distinguished only has the flat legacy
        // list — migrate it into the new list (every entry implicitly a self-cast buff, matching
        // what it always did before this split existed) instead of silently dropping the
        // character's already-configured spells. Once this profile is saved again the list will
        // be non-empty, so this fallback never re-triggers for it.
        _autoFarmCastSequence = profile.AutoFarmCastSequence.Count > 0
            ? profile.AutoFarmCastSequence.ToList()
            : profile.AutoFarmCastSpells
                .Select(name => new AutoFarmCastSpell(name, Offensive: false))
                .ToList();
        OnPropertyChanged(nameof(AutoFarmHpThresholdPercent));
        OnPropertyChanged(nameof(AutoFarmStepDelayMilliseconds));
        OnPropertyChanged(nameof(AutoFarmHealSpellNamesText));
        OnPropertyChanged(nameof(AutoFarmMemSpellsText));
        OnPropertyChanged(nameof(AutoFarmCastSpellsText));
        StartAutoFarmCommand.NotifyCanExecuteChanged();

        var persistedSets = profile.BuffSets ?? [];
        if (persistedSets.Count == 0)
        {
            persistedSets =
            [
                new ProfileBuffSet
                {
                    Name = "Domyślny",
                    Buffs = profile.RequiredBuffs ?? [],
                },
            ];
        }

        foreach (var persistedSet in persistedSets)
        {
            var set = new BuffSetEntry
            {
                Id = string.IsNullOrWhiteSpace(persistedSet.Id)
                    ? Guid.NewGuid().ToString("N")
                    : persistedSet.Id,
                Name = string.IsNullOrWhiteSpace(persistedSet.Name)
                    ? "Bez nazwy"
                    : persistedSet.Name.Trim(),
            };
            foreach (var buffName in persistedSet.Buffs ?? [])
            {
                if (!string.IsNullOrWhiteSpace(buffName))
                {
                    set.Buffs.Add(new BuffWatchEntry(buffName)
                    {
                        IsActive = _activeAffectNames.Contains(BuffWatchEntry.NormalizeName(buffName)),
                    });
                }
            }

            BuffSets.Add(set);
        }

        if (BuffSets.Count == 0)
        {
            BuffSets.Add(new BuffSetEntry { Name = "Domyślny" });
        }

        SelectedBuffSet = BuffSets.FirstOrDefault(set =>
            string.Equals(set.Id, profile.ActiveBuffSetId, StringComparison.Ordinal))
            ?? BuffSets[0];
        _loadingBuffSets = false;
        DeleteBuffSetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteBuffSet));

        _activeProfilePassword = PasswordProtector.Unprotect(profile.EncryptedPassword);
        _activeProfileNeedsRegistration = profile.NeedsRegistration;
        _activeProfileLogin = ResolveProfileLogin(profile);
        Host = ResolveProfileHost(profile);
        Port = ResolveProfilePort(profile);
        Encoding = ResolveProfileEncoding(profile);
        _activeProfileLastKnownWriteUtc = _profiles.GetLastWriteTimeUtc(profile.Name);
        _activeProfileBaselineSnapshot = profile;

        ActiveProfileName = profile.Name;
        _profileSettings = profile.Automation ?? LoadLegacyAutomationSettingsSeed();
        ApplyProfileSettingsToMap();
        NotifyProfileSettingsChanged();
        // A fresh Lua environment per character — see LuaScriptEngine.Reset's own doc comment —
        // so persistent script globals from the previous profile's session can never leak in.
        _lua.Reset();
        LuaLibrarySource = profile.LuaLibrary;
        LoadLuaLibrary(LuaLibrarySource, announceSuccess: false);
        // "/stop"'s memory of what it turned off (see StopEverything/StartEverything) is scoped
        // to one character's session — a rule id from the previous profile would never match this
        // one's rules anyway, but a toggle *command name* (e.g. "autostand") means the same thing
        // on every profile, so a stale entry here could silently flip on a toggle this profile
        // never asked "/start" to restore. Clear it on every switch, not just when it's actually
        // stale, since there's no cheap way to tell those apart.
        _stoppedRuleIds = [];
        _stoppedTimerIds = [];
        _stoppedToggleCommands = [];
        _suppressTreeRebuild = false;
        RebuildRuleViews();
        RebuildFolderTrees();
        ApplyAutomation();
        CancelAllTimers();
        SyncAllTimers();
        AddToast($"Konto „{profile.Name}” aktywne.", "info");
        ProfileActivated?.Invoke(profile.Name);
    }

    private static string ResolveProfileLogin(ProfileData profile) =>
        string.IsNullOrWhiteSpace(profile.Login) ? profile.Name : profile.Login.Trim();

    private static string ResolveProfileHost(ProfileData profile) =>
        string.IsNullOrWhiteSpace(profile.Host) ? "killer-mud.pl" : profile.Host.Trim();

    private static int ResolveProfilePort(ProfileData profile) =>
        profile.Port is >= 1 and <= 65535 ? profile.Port : 4004;

    private static string ResolveProfileEncoding(ProfileData profile) =>
        MudTextEncodings.All.Contains(profile.Encoding) ? profile.Encoding : MudTextEncodings.Auto;

    /// <summary>Appends entries from the shared global store to the working collections.</summary>
    private void LoadGlobalEntries()
    {
        var global = _profiles.LoadGlobal();
        _globalLastKnownWriteUtc = _profiles.GetGlobalLastWriteTimeUtc();
        _globalBaselineSnapshot = global;

        foreach (var folder in global.Folders)
        {
            Folders.Add(MakeFolderNode(folder, isGlobal: true));
        }

        foreach (var note in global.Notes)
        {
            Notes.Add(MakeNoteEntry(note, isGlobal: true));
        }

        foreach (var rule in global.Rules)
        {
            AutomationRules.Add(MakeRuleEntry(rule, isGlobal: true));
        }

        foreach (var timer in global.Timers)
        {
            Timers.Add(MakeTimerEntry(timer, isGlobal: true));
        }

        foreach (var location in global.Locations)
        {
            Locations.Add(MakeLocationEntry(location, isGlobal: true));
        }
    }

    private static NoteEntry MakeNoteEntry(ProfileNote note, bool isGlobal) => new()
    {
        Title = note.Title,
        Content = note.Content,
        CreatedAt = note.CreatedAt,
        IsGlobal = isGlobal,
        FolderId = note.FolderId,
    };

    private static ProfileNote ToProfileNote(NoteEntry n) => new()
    {
        Title = n.Title,
        Content = n.Content,
        CreatedAt = n.CreatedAt,
        IsGlobal = n.IsGlobal,
        FolderId = n.FolderId,
    };

    private static AutomationRuleEntry MakeRuleEntry(ProfileRule rule, bool isGlobal) =>
        new(rule.Name, rule.Type, rule.Pattern, rule.Action, rule.IsEnabled, isGlobal, rule.IsScript,
            rule.PlaySoundOnMatch)
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id,
            FolderId = rule.FolderId,
        };

    private static TimerEntry MakeTimerEntry(ProfileTimer timer, bool isGlobal) => new()
    {
        Id = string.IsNullOrWhiteSpace(timer.Id) ? Guid.NewGuid().ToString("N") : timer.Id,
        Name = timer.Name,
        Minutes = timer.Minutes,
        Seconds = timer.Seconds,
        Milliseconds = timer.Milliseconds,
        CommandsText = !string.IsNullOrEmpty(timer.CommandsText)
            ? timer.CommandsText
            : string.Join(Environment.NewLine, timer.Commands),
        IsScript = timer.IsScript,
        IsEnabled = timer.IsEnabled,
        PlaySoundOnTick = timer.PlaySoundOnTick,
        IsGlobal = isGlobal,
        FolderId = timer.FolderId,
    };

    private AutowalkLocation MakeLocationEntry(ProfileLocation location, bool isGlobal)
    {
        var room = Map.MapIndex?.FindFirstRoomByVnum(location.Vnum);
        return new AutowalkLocation(location.Name, location.Vnum, room?.Name, isGlobal)
        {
            FolderId = location.FolderId,
        };
    }

    private static FolderNode MakeFolderNode(ProfileFolder folder, bool isGlobal) => new()
    {
        Id = string.IsNullOrWhiteSpace(folder.Id) ? Guid.NewGuid().ToString("N") : folder.Id,
        ParentId = folder.ParentId,
        Name = folder.Name,
        Kind = folder.Kind,
        IsGlobal = isGlobal,
    };

    private static ProfileFolder ToProfileFolder(FolderNode f) => new()
    {
        Id = f.Id,
        ParentId = f.ParentId,
        Name = f.Name,
        Kind = f.Kind,
        IsGlobal = f.IsGlobal,
    };

    private static ProfileRule ToProfileRule(AutomationRuleEntry r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Type = r.Type,
        Pattern = r.Pattern,
        Action = r.Action,
        IsScript = r.IsScript,
        IsEnabled = r.IsEnabled,
        IsGlobal = r.IsGlobal,
        FolderId = r.FolderId,
        PlaySoundOnMatch = r.PlaySoundOnMatch,
    };

    private ProfileTimer ToProfileTimer(TimerEntry t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Minutes = t.Minutes,
        Seconds = t.Seconds,
        Milliseconds = t.Milliseconds,
        // A script timer's CommandsText is Lua source, not a real command list — GetCommands
        // would mangle it by splitting on newlines/the stacking separator, so skip that for
        // scripts and just round-trip the raw text via CommandsText below (Commands stays empty).
        Commands = t.IsScript ? [] : t.GetCommands(CommandStackingSeparator).ToList(),
        CommandsText = t.CommandsText,
        IsScript = t.IsScript,
        IsEnabled = t.IsEnabled,
        PlaySoundOnTick = t.PlaySoundOnTick,
        IsGlobal = t.IsGlobal,
        FolderId = t.FolderId,
    };

    private static ProfileLocation ToProfileLocation(AutowalkLocation l) => new()
    {
        Name = l.Name,
        Vnum = l.Vnum,
        IsGlobal = l.IsGlobal,
        FolderId = l.FolderId,
    };

    /// <summary>
    /// Persists the working collections: global entries go to the shared
    /// global file, the rest to the active profile (if any). If another running
    /// instance (multiboxing) changed the same file since this instance last
    /// loaded/saved it, the two sides are 3-way merged first (see
    /// <see cref="ReconcileGlobalWithDisk"/>/<see cref="ReconcileProfileWithDisk"/>)
    /// instead of blindly overwriting whatever that other instance added or changed.
    /// </summary>
    /// <summary>
    /// Persists the working collections and, just as importantly, pulls in whatever another
    /// running instance (multiboxing) wrote since this instance last looked — even when nothing
    /// changed locally at all. This is also what the periodic multibox-sync timer calls (see
    /// constructor), so it must stay cheap and side-effect-free when there is truly nothing new
    /// on either side: it never writes a file whose content already matches what's on disk, and
    /// only shows the "merged" toast when the merge actually added something this instance didn't
    /// already have.
    /// </summary>
    private void SaveActiveProfile()
    {
        var global = new GlobalData
        {
            Notes = Notes.Where(n => n.IsGlobal).Select(ToProfileNote).ToList(),
            Rules = AutomationRules.Where(r => r.IsGlobal).Select(ToProfileRule).ToList(),
            Timers = Timers.Where(t => t.IsGlobal).Select(ToProfileTimer).ToList(),
            Locations = Locations.Where(l => l.IsGlobal).Select(ToProfileLocation).ToList(),
            Folders = Folders.Where(f => f.IsGlobal).Select(ToProfileFolder).ToList(),
        };

        try
        {
            if (HasChangedOnDisk(_globalLastKnownWriteUtc, _profiles.GetGlobalLastWriteTimeUtc()))
            {
                global = ReconcileGlobalWithDisk(global);
            }

            if (!SnapshotsMatch(ToSnapshot(global), ToSnapshot(_profiles.LoadGlobal())))
            {
                _profiles.SaveGlobal(global);
            }

            _globalLastKnownWriteUtc = _profiles.GetGlobalLastWriteTimeUtc();
            _globalBaselineSnapshot = global;
        }
        catch (Exception exception)
        {
            // Reconciling (not just the final write) must stay inside this try: an exception here
            // used to escape SaveActiveProfile entirely, which — since the periodic multibox-sync
            // timer has no catch of its own around its callback — silently killed that timer's
            // loop for the rest of the session on the very first failure.
            AddToast($"Nie udało się zapisać globalnych wpisów: {exception.Message}", "error");
        }

        if (ActiveProfileName is null)
        {
            return;
        }

        var profile = new ProfileData
        {
            Name = ActiveProfileName,
            Login = _activeProfileLogin,
            Host = Host.Trim(),
            Port = Port,
            Notes = Notes.Where(n => !n.IsGlobal).Select(ToProfileNote).ToList(),
            Rules = AutomationRules.Where(r => !r.IsGlobal).Select(ToProfileRule).ToList(),
            Timers = Timers.Where(t => !t.IsGlobal).Select(ToProfileTimer).ToList(),
            Locations = Locations.Where(l => !l.IsGlobal).Select(ToProfileLocation).ToList(),
            Folders = Folders.Where(f => !f.IsGlobal).Select(ToProfileFolder).ToList(),
            Deaths = Deaths.Select(d => new ProfileDeath
            {
                Vnum = d.Vnum,
                RoomName = d.RoomName ?? string.Empty,
                When = d.When,
            }).ToList(),
            KnownSpells = _knownSpells.Select(spell => new ProfileSpellEntry
            {
                Name = spell.Key,
                Known = spell.Value,
            }).ToList(),
            KnownSkills = _knownSkills.Select(skill => new ProfileSkillEntry
            {
                Name = skill.Key,
                Current = skill.Value,
            }).ToList(),
            AutoFarmRegions = _autoFarmRegions.Select(ToProfileFarmRegion).ToList(),
            AutoFarmHpThresholdPercent = _autoFarmHpThresholdPercent,
            AutoFarmStepDelayMilliseconds = _autoFarmStepDelayMilliseconds,
            AutoFarmHealSpellNames = _autoFarmHealSpellNames.ToList(),
            AutoFarmMemSpells = _autoFarmMemSpells.ToList(),
            AutoFarmCastSequence = _autoFarmCastSequence.ToList(),
            RequiredBuffs = RequiredBuffs.Select(b => b.Name).ToList(),
            BuffSets = BuffSets.Select(set => new ProfileBuffSet
            {
                Id = set.Id,
                Name = set.Name,
                Buffs = set.Buffs.Select(buff => buff.Name).ToList(),
            }).ToList(),
            ActiveBuffSetId = SelectedBuffSet?.Id ?? string.Empty,
            EncryptedPassword = PasswordProtector.Protect(_activeProfilePassword),
            NeedsRegistration = _activeProfileNeedsRegistration,
            Automation = _profileSettings,
            LuaLibrary = LuaLibrarySource,
        };

        try
        {
            if (HasChangedOnDisk(_activeProfileLastKnownWriteUtc, _profiles.GetLastWriteTimeUtc(profile.Name)))
            {
                profile = ReconcileProfileWithDisk(profile);
            }

            // Full-object comparison, not just the 5 merge-tracked lists: ProfileData also carries
            // Host/Port/Login/EncryptedPassword/Deaths/BuffSets/ActiveBuffSetId etc., and skipping
            // the write just because triggers/timers/notes/locations/folders happen to match would
            // silently drop a change to any of those other fields.
            var diskProfile = _profiles.Load(profile.Name);
            if (diskProfile is null || JsonSerializer.Serialize(profile) != JsonSerializer.Serialize(diskProfile))
            {
                _profiles.Save(profile);
            }

            _activeProfileLastKnownWriteUtc = _profiles.GetLastWriteTimeUtc(profile.Name);
            _activeProfileBaselineSnapshot = profile;
        }
        catch (Exception exception)
        {
            AddToast($"Nie udało się zapisać konta: {exception.Message}", "error");
        }
    }

    /// <summary>
    /// True when a profile/global file changed on disk after this instance last loaded
    /// or saved it — most likely another running instance of the client saved the same
    /// data in the meantime, so a blind overwrite here would silently discard it. This also
    /// covers the file appearing for the first time (null → non-null): an instance constructed
    /// before anyone had ever saved global/profile data has a null baseline, and if another
    /// instance creates the file afterward, that must still count as "changed" — otherwise this
    /// instance's own next save would blindly overwrite the file with its own empty/unrelated
    /// state instead of reconciling.
    /// </summary>
    private static bool HasChangedOnDisk(DateTime? lastKnownWriteUtc, DateTime? currentWriteUtc) =>
        lastKnownWriteUtc != currentWriteUtc;

    /// <summary>
    /// 3-way merges the shared global file against what's actually on disk right now, using
    /// <see cref="_globalBaselineSnapshot"/> (what this instance last loaded/saved) as the common
    /// ancestor. An entry this instance added or edited (differs from the baseline) wins; an
    /// entry this instance deleted (present in the baseline, missing from <paramref name="current"/>)
    /// stays deleted; anything this instance didn't touch defers to disk's current value, so the
    /// other instance's untouched-by-us additions, edits and deletions all survive instead of
    /// being silently clobbered by this save.
    /// </summary>
    private GlobalData ReconcileGlobalWithDisk(GlobalData current)
    {
        var baseline = _globalBaselineSnapshot ?? new GlobalData();
        var disk = _profiles.LoadGlobal();

        var merged = new GlobalData
        {
            Notes = MergeCollection(baseline.Notes, current.Notes, disk.Notes, NoteMergeKey),
            Rules = MergeCollection(baseline.Rules, current.Rules, disk.Rules, RuleMergeKey),
            Timers = MergeCollection(baseline.Timers, current.Timers, disk.Timers, TimerMergeKey),
            Locations = MergeCollection(baseline.Locations, current.Locations, disk.Locations, LocationMergeKey),
            Folders = MergeCollection(baseline.Folders, current.Folders, disk.Folders, FolderMergeKey),
        };

        if (!SnapshotsMatch(ToSnapshot(current), ToSnapshot(merged)))
        {
            AddToast(
                "Dane globalne zostały zmienione przez inną instancję klienta — zmiany scalone automatycznie.",
                "info");
            ReflectMergedEntries(merged.Rules, merged.Timers, merged.Notes, merged.Locations, merged.Folders, isGlobal: true);
        }

        return merged;
    }

    /// <summary>Same idea as <see cref="ReconcileGlobalWithDisk"/>, for the active profile's own file.</summary>
    private ProfileData ReconcileProfileWithDisk(ProfileData current)
    {
        var baseline = _activeProfileBaselineSnapshot ?? new ProfileData { Name = current.Name };
        var disk = _profiles.Load(current.Name) ?? new ProfileData { Name = current.Name };
        var beforeMerge = ToSnapshot(current);

        current.Notes = MergeCollection(baseline.Notes, current.Notes, disk.Notes, NoteMergeKey);
        current.Rules = MergeCollection(baseline.Rules, current.Rules, disk.Rules, RuleMergeKey);
        current.Timers = MergeCollection(baseline.Timers, current.Timers, disk.Timers, TimerMergeKey);
        current.Locations = MergeCollection(baseline.Locations, current.Locations, disk.Locations, LocationMergeKey);
        current.Folders = MergeCollection(baseline.Folders, current.Folders, disk.Folders, FolderMergeKey);

        if (!SnapshotsMatch(beforeMerge, ToSnapshot(current)))
        {
            AddToast(
                $"Dane konta „{current.Name}” zostały zmienione przez inną instancję klienta — zmiany scalone automatycznie.",
                "info");
            ReflectMergedEntries(current.Rules, current.Timers, current.Notes, current.Locations, current.Folders, isGlobal: false);
        }

        return current;
    }

    /// <summary>The subset of a Global/Profile file that multibox merging cares about, projected
    /// into one shape so both can be compared/merged with the same helpers.</summary>
    private readonly record struct MergeableSnapshot(
        List<ProfileRule> Rules,
        List<ProfileTimer> Timers,
        List<ProfileNote> Notes,
        List<ProfileLocation> Locations,
        List<ProfileFolder> Folders);

    private static MergeableSnapshot ToSnapshot(GlobalData data) =>
        new(data.Rules, data.Timers, data.Notes, data.Locations, data.Folders);

    private static MergeableSnapshot ToSnapshot(ProfileData data) =>
        new(data.Rules, data.Timers, data.Notes, data.Locations, data.Folders);

    /// <summary>
    /// Order-independent equality for two snapshots — MergeCollection's key iteration order isn't
    /// stable, so comparing serialized lists directly would report a "change" on nothing but a
    /// reshuffle. Used both to decide whether a merge actually found something new (toast/reflect)
    /// and whether a save would actually change the file on disk (skip the write if not).
    /// </summary>
    private static bool SnapshotsMatch(MergeableSnapshot a, MergeableSnapshot b) =>
        CollectionsMatch(a.Rules, b.Rules) &&
        CollectionsMatch(a.Timers, b.Timers) &&
        CollectionsMatch(a.Notes, b.Notes) &&
        CollectionsMatch(a.Locations, b.Locations) &&
        CollectionsMatch(a.Folders, b.Folders);

    private static bool CollectionsMatch<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var setA = a.Select(item => JsonSerializer.Serialize(item)).ToHashSet(StringComparer.Ordinal);
        return setA.SetEquals(b.Select(item => JsonSerializer.Serialize(item)));
    }

    /// <summary>
    /// Inserts into the live UI-bound collections any merged entry that isn't already there —
    /// i.e. something the other instance added or changed that this instance didn't know about
    /// yet — without touching entries this instance already has, so an open editor or the
    /// current selection is never disturbed by this. New global timers are also started here
    /// (see <see cref="SyncTimer"/>); everything else just needs its view rebuilt.
    /// </summary>
    private void ReflectMergedEntries(
        List<ProfileRule> mergedRules,
        List<ProfileTimer> mergedTimers,
        List<ProfileNote> mergedNotes,
        List<ProfileLocation> mergedLocations,
        List<ProfileFolder> mergedFolders,
        bool isGlobal)
    {
        var existingRuleKeys = AutomationRules.Where(r => r.IsGlobal == isGlobal)
            .Select(ToProfileRule).Select(RuleMergeKey).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in mergedRules)
        {
            if (existingRuleKeys.Add(RuleMergeKey(rule)))
            {
                AutomationRules.Add(MakeRuleEntry(rule, isGlobal));
            }
        }

        var existingTimerIds = Timers.Where(t => t.IsGlobal == isGlobal)
            .Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var timer in mergedTimers)
        {
            if (!existingTimerIds.Add(timer.Id))
            {
                continue;
            }

            var entry = MakeTimerEntry(timer, isGlobal);
            Timers.Add(entry);
            SyncTimer(entry);
        }

        var existingNoteKeys = Notes.Where(n => n.IsGlobal == isGlobal)
            .Select(ToProfileNote).Select(NoteMergeKey).ToHashSet(StringComparer.Ordinal);
        foreach (var note in mergedNotes)
        {
            if (existingNoteKeys.Add(NoteMergeKey(note)))
            {
                Notes.Add(MakeNoteEntry(note, isGlobal));
            }
        }

        var existingLocationKeys = Locations.Where(l => l.IsGlobal == isGlobal)
            .Select(ToProfileLocation).Select(LocationMergeKey).ToHashSet(StringComparer.Ordinal);
        foreach (var location in mergedLocations)
        {
            if (existingLocationKeys.Add(LocationMergeKey(location)))
            {
                Locations.Add(MakeLocationEntry(location, isGlobal));
            }
        }

        var existingFolderIds = Folders.Where(f => f.IsGlobal == isGlobal)
            .Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in mergedFolders)
        {
            if (existingFolderIds.Add(folder.Id))
            {
                Folders.Add(MakeFolderNode(folder, isGlobal));
            }
        }

        RebuildRuleViews();
        RebuildFolderTrees();
        ApplyAutomation();
    }

    /// <summary>Keys on the rule's stable Id, not Type+Name — two independently created rules
    /// that happen to share a name and type used to be merge-matched as "the same rule", so
    /// whichever instance saved last would silently overwrite the other's Pattern/Action with no
    /// warning (see ProfileSaveConflictTests for the regression case).</summary>
    private static string RuleMergeKey(ProfileRule r) => r.Id;

    private static string TimerMergeKey(ProfileTimer t) => t.Id;

    private static string NoteMergeKey(ProfileNote n) => $"{n.Title}|{n.CreatedAt}";

    private static string LocationMergeKey(ProfileLocation l) => $"{l.Name}|{l.Vnum}";

    private static string FolderMergeKey(ProfileFolder f) => f.Id;

    /// <summary>
    /// 3-way merge for one shared list: <paramref name="baseline"/> is what this instance last
    /// loaded/saved, <paramref name="current"/> is its in-memory state right now, and
    /// <paramref name="disk"/> is what's actually on disk right now. An item missing from
    /// <paramref name="current"/> but present in <paramref name="baseline"/> was deleted by us
    /// and stays deleted; an item that differs from the baseline was added or edited by us and
    /// wins outright; everything else — untouched by this instance — takes disk's current value
    /// (including disk no longer having it at all, meaning the other instance deleted it).
    /// </summary>
    private static List<T> MergeCollection<T>(
        IReadOnlyList<T> baseline,
        IReadOnlyList<T> current,
        IReadOnlyList<T> disk,
        Func<T, string> keySelector)
    {
        var baselineByKey = ToKeyedMap(baseline, keySelector);
        var currentByKey = ToKeyedMap(current, keySelector);
        var diskByKey = ToKeyedMap(disk, keySelector);

        var merged = new List<T>();
        foreach (var key in currentByKey.Keys.Concat(diskByKey.Keys).Distinct(StringComparer.Ordinal))
        {
            var inBaseline = baselineByKey.TryGetValue(key, out var baselineItem);
            var inCurrent = currentByKey.TryGetValue(key, out var currentItem);
            var inDisk = diskByKey.TryGetValue(key, out var diskItem);

            if (inBaseline && !inCurrent)
            {
                continue; // we deleted it — keep it deleted
            }

            var weChangedIt = inCurrent
                && (!inBaseline || JsonSerializer.Serialize(baselineItem) != JsonSerializer.Serialize(currentItem));
            if (weChangedIt)
            {
                merged.Add(currentItem!);
                continue;
            }

            if (inBaseline && !inDisk)
            {
                continue; // untouched by us, but the other instance deleted it — respect that
            }

            if (inDisk)
            {
                merged.Add(diskItem!);
            }
            else if (inCurrent)
            {
                merged.Add(currentItem!);
            }
        }

        return merged;
    }

    private static Dictionary<string, T> ToKeyedMap<T>(IReadOnlyList<T> items, Func<T, string> keySelector) =>
        items.GroupBy(keySelector, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    /// <summary>Snapshot handed to <see cref="_lua"/> right before every script run — see
    /// <see cref="LuaScriptEngine.GameStateProvider"/>.</summary>
    private LuaGameState BuildLuaGameState() => new(
        _latestHp,
        _latestMaxHp,
        _latestMovement,
        _latestMaximumMovement,
        _latestCharacterName,
        _latestCharacterPosition,
        Map.CurrentVnum,
        Map.CurrentVnum is { } vnum ? Map.MapIndex?.FindFirstRoomByVnum(vnum)?.Name : null,
        SkillsOnCooldown);

    /// <summary>A script's <c>echo(text)</c> call — prints the same way a triggered command's own
    /// echo does. Fires from whichever thread ran the script (UI for an alias, network read loop
    /// for a trigger, the timer's own callback thread for a timer), so it must marshal to the UI
    /// thread itself rather than touching <see cref="OutputReceived"/> directly.</summary>
    private void OnLuaEcho(string text) =>
        Dispatcher.UIThread.Post(() => EmitSystem(text, 90));

    /// <summary>A script rule threw (syntax or runtime error) — reported by name so the player
    /// can find and fix it, without taking down the rest of that alias/trigger evaluation.</summary>
    private void OnLuaScriptError(string ruleName, string message) =>
        Dispatcher.UIThread.Post(() => AddToast($"Błąd skryptu Lua w „{ruleName}”: {message}", "error"));

    /// <summary>A trigger's own "odtwórz dźwięk" option (see
    /// <see cref="AutomationRuleEntry.PlaySoundOnMatch"/>) — separate from and in addition to
    /// <see cref="ChatSoundOnNewMessageEnabled"/>. Runs on whatever thread <see cref="OnLineReceived"/>
    /// itself runs on; the Win32 beep call is thread-safe and touches no UI-bound state, so no
    /// dispatch is needed.</summary>
    private void OnTriggerRuleMatched(TriggerRule rule)
    {
        if (rule.PlaySoundOnMatch)
        {
            PlayNotificationSound();
        }

        var description = $"Trigger: „{rule.Name}”";
        Dispatcher.UIThread.Post(() =>
        {
            AutomationFired?.Invoke(description);
            FlashTriggerRecentlyFired(rule.Name);
        });
    }

    /// <summary>Lights up <see cref="AutomationRuleEntry.RecentlyFired"/> on the trigger matching
    /// <paramref name="ruleName"/> for <see cref="AutomationActivityDisplayDuration"/> — the
    /// gray-to-blue flash on the timers/triggers status bar. Matches by name since the Core
    /// <see cref="TriggerRule"/> carries no back-reference to its originating
    /// <see cref="AutomationRuleEntry"/>; a later match on the same trigger restarts the timer
    /// instead of stacking, same as <see cref="ShowAutomationActivity"/>.</summary>
    private void FlashTriggerRecentlyFired(string ruleName)
    {
        var entry = TriggerRules.FirstOrDefault(rule => string.Equals(rule.Name, ruleName, StringComparison.Ordinal));
        if (entry is null)
        {
            return;
        }

        entry.RecentlyFired = true;

        if (_triggerRecentlyFiredClearTokens.TryGetValue(entry.Id, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _triggerRecentlyFiredClearTokens[entry.Id] = cts;
        _ = ClearTriggerRecentlyFiredAfterDelayAsync(entry, cts.Token);
    }

    private async Task ClearTriggerRecentlyFiredAfterDelayAsync(AutomationRuleEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutomationActivityDisplayDuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            entry.RecentlyFired = false;
        }
    }

    /// <summary>Overridable in tests — avoids actually invoking the Windows system beep (and its
    /// audible side effect) during a test run. See <see cref="ChatSoundOnNewMessageEnabled"/>,
    /// <see cref="OnTriggerRuleMatched"/> and <see cref="SyncTimer"/> for the call sites.</summary>
    internal Action PlayNotificationSound { get; set; } = NotificationSoundPlayer.PlayNotification;

    /// <summary>Lua source defining reusable helper functions/values every "script" alias/trigger/
    /// timer on this profile can call — see <see cref="ApplyLuaLibraryCommand"/> and
    /// <see cref="LoadLuaLibrary"/>. Edits here aren't live until applied (or the profile is
    /// re-activated), same as an alias/trigger edit needing "Zapisz zmiany".</summary>
    public string LuaLibrarySource
    {
        get => _luaLibrarySource;
        set => SetProperty(ref _luaLibrarySource, value);
    }

    public RelayCommand ApplyLuaLibraryCommand { get; }

    /// <summary>Persists <see cref="LuaLibrarySource"/> to the active profile and (re-)loads it
    /// into <see cref="_lua"/> immediately, so edits take effect without reconnecting or
    /// switching profiles away and back. Saves regardless of whether the load succeeds — a
    /// temporary typo while editing shouldn't cost the player their in-progress work.</summary>
    private void ApplyLuaLibrary()
    {
        SaveActiveProfile();
        LoadLuaLibrary(LuaLibrarySource, announceSuccess: true);
    }

    /// <summary>Loads Lua library source into <see cref="_lua"/>, reporting a syntax/runtime
    /// error as a toast instead of letting it propagate — used both by
    /// <see cref="ApplyLuaLibrary"/> (explicit "apply" click) and <see cref="ActivateProfile"/>
    /// (silent load on profile switch, where a stale broken library shouldn't nag on every
    /// connect — see <paramref name="announceSuccess"/>).</summary>
    private void LoadLuaLibrary(string source, bool announceSuccess)
    {
        try
        {
            _lua.LoadLibrary(source);
            if (announceSuccess)
            {
                AddToast("Biblioteka Lua wczytana.", "info");
            }
        }
        catch (MoonSharp.Interpreter.InterpreterException exception)
        {
            AddToast($"Błąd w bibliotece Lua: {exception.DecoratedMessage ?? exception.Message}", "error");
        }
    }

    /// <summary>
    /// Rebuilds the alias/trigger engines from the active profile's rules.
    /// Timers are managed separately (see SyncTimer).
    /// </summary>
    private void ApplyAutomation()
    {
        _aliases.Clear();
        _triggers.Clear();

        foreach (var rule in AutomationRules)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }

            try
            {
                switch (rule.Type)
                {
                    case "alias":
                        _aliases.Add(new AliasRule(rule.Name, rule.Pattern, rule.Action, isScript: rule.IsScript));
                        break;

                    case "trigger":
                        _triggers.Add(new TriggerRule(
                            rule.Name, rule.Pattern, rule.Action,
                            isScript: rule.IsScript, playSoundOnMatch: rule.PlaySoundOnMatch));
                        break;
                }
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern in a stored rule — skip it.
                AddToast($"Pominięto regułę „{rule.Name}”: nieprawidłowy wzorzec.", "error");
            }
        }
    }

    /// <summary>Remembers exactly what the last "/stop" turned off, so "/start" can restore
    /// precisely that instead of blindly enabling everything (which would also re-enable rules
    /// the player had deliberately disabled beforehand). Empty means there is nothing to
    /// restore — "/stop" was never used this session for the active profile, a prior "/start"
    /// already consumed it, or the profile was just switched (see ActivateProfile, which resets
    /// these — a rule/timer id from the previous character never matches this one's anyway, but a
    /// toggle *command name* like "autostand" means the same thing on every profile, so leaving
    /// this in place across a switch could silently flip on a toggle this character never asked
    /// "/start" to restore).</summary>
    private HashSet<string> _stoppedRuleIds = [];
    private HashSet<string> _stoppedTimerIds = [];
    private HashSet<string> _stoppedToggleCommands = [];

    /// <summary>
    /// Panic-stop for the "/stop" terminal command: stops any active autowalk/auto-farm run,
    /// disables every alias, trigger, and timer, and switches off every automation toggle in
    /// <see cref="CommandToggles"/> (autostand, autoscan, ...) — one command to kill all running
    /// automation at once. Distinct from the map's right-click STOP button
    /// (<see cref="StopAutowalkCommand"/>), which only stops movement. Remembers what it turned
    /// off so the companion "/start" command (<see cref="StartEverything"/>) can restore exactly
    /// that.
    /// </summary>
    private void StopEverything()
    {
        StopAutoFarm("Farma zatrzymana.");
        StopAutowalk("Autowalk zatrzymany.");

        var stoppedRuleIds = new HashSet<string>();
        foreach (var rule in AutomationRules)
        {
            if (rule.IsEnabled)
            {
                rule.IsEnabled = false;
                stoppedRuleIds.Add(rule.Id);
            }
        }

        if (stoppedRuleIds.Count > 0)
        {
            ApplyAutomation();
        }

        var stoppedTimerIds = new HashSet<string>();
        foreach (var timer in Timers)
        {
            if (timer.IsEnabled)
            {
                timer.IsEnabled = false;
                SyncTimer(timer);
                stoppedTimerIds.Add(timer.Id);
            }
        }

        if (stoppedRuleIds.Count > 0 || stoppedTimerIds.Count > 0)
        {
            RebuildFolderTrees();
        }

        var stoppedToggleCommands = new HashSet<string>();
        foreach (var toggle in CommandToggles)
        {
            if (toggle.Get())
            {
                toggle.Set(false);
                stoppedToggleCommands.Add(toggle.Command);
            }
        }

        _stoppedRuleIds = stoppedRuleIds;
        _stoppedTimerIds = stoppedTimerIds;
        _stoppedToggleCommands = stoppedToggleCommands;

        SaveActiveProfile();

        EmitSystem(
            $"STOP: wyłączono {stoppedRuleIds.Count} aliasów/triggerów, {stoppedTimerIds.Count} timerów i {stoppedToggleCommands.Count} funkcji automatyzacji.",
            90);
    }

    /// <summary>
    /// Companion to <see cref="StopEverything"/> for the "/start" terminal command: restores
    /// exactly the aliases, triggers, timers and automation toggles that the last "/stop" turned
    /// off for the currently active profile (see <see cref="_stoppedRuleIds"/>). When there is
    /// nothing remembered — no "/stop" ran yet this session for this character, it was already
    /// consumed by an earlier "/start", or the profile was just switched — "/start" is a no-op
    /// (with its own toast saying so) rather than guessing and enabling everything, which could
    /// re-enable something this character had deliberately left off.
    /// </summary>
    private void StartEverything()
    {
        var restoredRules = 0;
        foreach (var rule in AutomationRules)
        {
            if (!rule.IsEnabled && _stoppedRuleIds.Contains(rule.Id))
            {
                rule.IsEnabled = true;
                restoredRules++;
            }
        }

        if (restoredRules > 0)
        {
            ApplyAutomation();
        }

        var restoredTimers = 0;
        foreach (var timer in Timers)
        {
            if (!timer.IsEnabled && _stoppedTimerIds.Contains(timer.Id))
            {
                timer.IsEnabled = true;
                SyncTimer(timer);
                restoredTimers++;
            }
        }

        if (restoredRules > 0 || restoredTimers > 0)
        {
            RebuildFolderTrees();
        }

        var restoredToggles = 0;
        foreach (var toggle in CommandToggles)
        {
            if (!toggle.Get() && _stoppedToggleCommands.Contains(toggle.Command))
            {
                toggle.Set(true);
                restoredToggles++;
            }
        }

        var hadAnythingToRestore =
            _stoppedRuleIds.Count > 0 || _stoppedTimerIds.Count > 0 || _stoppedToggleCommands.Count > 0;
        _stoppedRuleIds = [];
        _stoppedTimerIds = [];
        _stoppedToggleCommands = [];

        SaveActiveProfile();

        EmitSystem(
            hadAnythingToRestore
                ? $"START: włączono {restoredRules} aliasów/triggerów, {restoredTimers} timerów i {restoredToggles} funkcji automatyzacji."
                : "START: nie ma nic do przywrócenia (użyj /stop najpierw).",
            90);
    }

    // --- Command history ---
    private const int CommandHistoryMaxSize = 100;
    public ObservableCollection<string> CommandHistory { get; } = [];

    // ========================================================================
    // "/" command autocomplete — see TerminalPanelView's CommandBox for the popup this drives.
    // ========================================================================

    private static readonly string[] FixedCommandNames =
    [
        "stop",
        "start",
        "walk",
        "walk_dodaj",
        "recast",
        "mapuj",
        "map",
    ];

    private IReadOnlyList<string>? _availableCommandNames;

    /// <summary>Every "/" command name the terminal understands — the fixed built-ins (see
    /// <see cref="FixedCommandNames"/>) plus every <see cref="CommandToggles"/> entry — used to
    /// populate <see cref="CommandSuggestions"/> as the player types. Sorted alphabetically so the
    /// dropdown reads predictably regardless of registration order.</summary>
    public IReadOnlyList<string> AvailableCommandNames => _availableCommandNames ??=
        FixedCommandNames
            .Concat(CommandToggles.Select(toggle => toggle.Command))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Command names matching whatever's currently typed after a bare leading "/" in
    /// <see cref="CommandText"/> — see <see cref="RefreshCommandSuggestions"/>. Empty means no
    /// suggestions are showing.</summary>
    public ObservableCollection<string> CommandSuggestions { get; } = [];

    private int _selectedCommandSuggestionIndex = -1;

    public int SelectedCommandSuggestionIndex
    {
        get => _selectedCommandSuggestionIndex;
        set => SetProperty(ref _selectedCommandSuggestionIndex, value);
    }

    /// <summary>True while <see cref="CommandSuggestions"/> has at least one entry — bound to the
    /// suggestions popup's IsOpen in TerminalPanelView. Raised manually since an
    /// ObservableCollection's own CollectionChanged doesn't notify a derived bool property.</summary>
    public bool HasCommandSuggestions => CommandSuggestions.Count > 0;

    /// <summary>Repopulates <see cref="CommandSuggestions"/> from <see cref="CommandText"/> —
    /// populated only while the box holds nothing but a bare "/" plus an in-progress command name
    /// (no space or newline yet), matched case-insensitively by prefix against
    /// <see cref="AvailableCommandNames"/>. Anything past that point (a space, an argument, a
    /// second stacked line) hides the list, since the player has moved on to the argument.</summary>
    private void RefreshCommandSuggestions()
    {
        var text = CommandText;
        if (text.Length == 0 || text[0] != '/' || text.IndexOfAny(['\n', ' ']) >= 0)
        {
            ClearCommandSuggestions();
            return;
        }

        var prefix = text[1..];
        var matches = AvailableCommandNames
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        CommandSuggestions.Clear();
        foreach (var match in matches)
        {
            CommandSuggestions.Add(match);
        }

        SelectedCommandSuggestionIndex = CommandSuggestions.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasCommandSuggestions));
    }

    /// <summary>Hides the suggestions list without touching <see cref="CommandText"/> — called on
    /// Escape. The next keystroke re-evaluates via <see cref="RefreshCommandSuggestions"/> and can
    /// bring it back, same as a normal combo box.</summary>
    public void ClearCommandSuggestions()
    {
        if (CommandSuggestions.Count == 0)
        {
            return;
        }

        CommandSuggestions.Clear();
        SelectedCommandSuggestionIndex = -1;
        OnPropertyChanged(nameof(HasCommandSuggestions));
    }

    /// <summary>Moves the highlighted suggestion by <paramref name="direction"/> (+1/-1), clamped
    /// to the list's bounds rather than wrapping.</summary>
    public void MoveCommandSuggestionSelection(int direction)
    {
        if (CommandSuggestions.Count == 0)
        {
            return;
        }

        SelectedCommandSuggestionIndex = Math.Clamp(
            SelectedCommandSuggestionIndex + direction, 0, CommandSuggestions.Count - 1);
    }

    /// <summary>Completes <see cref="CommandText"/> with the highlighted suggestion, leaving a
    /// trailing space so the caret lands ready for the argument — called on Tab/Enter/click while
    /// the suggestions list is showing.</summary>
    public void AcceptSelectedCommandSuggestion()
    {
        if (SelectedCommandSuggestionIndex < 0 || SelectedCommandSuggestionIndex >= CommandSuggestions.Count)
        {
            return;
        }

        CommandText = $"/{CommandSuggestions[SelectedCommandSuggestionIndex]} ";
    }

    public IRelayCommand<string> ExaminePersonCommand { get; }
    public IRelayCommand<string> KillPersonCommand { get; }
    public RelayCommand<GroupMember> LordGotoGroupRoomCommand { get; }
    public RelayCommand<GroupMember> LordGotoGroupMemberCommand { get; }
    public IRelayCommand AddGroupSpellCommand { get; }
    public IRelayCommand<GroupSpellShortcut> RemoveGroupSpellCommand { get; }

    // --- Character vitals (mock) ---
    public CharacterVitals Vitals { get; } = new();

    // --- World time & weather (live, from Mud.TimeInfo / Mud.Weather GMCP) ---
    public WorldTimeWeather WorldTime { get; } = new();

    // --- Character conditions (live, from Char.Condition GMCP) ---
    public ObservableCollection<string> Conditions { get; } = [];

    // --- Status effects (live, from Char.Affects GMCP) ---
    public ObservableCollection<StatusEffect> Effects { get; } = [];

    // --- People in room (mock) ---
    public ObservableCollection<PersonEntry> People { get; } = [];

    // --- Group members (mock) ---
    public ObservableCollection<GroupMember> Group { get; } = [];

    public string GroupEmptyMessage { get; private set; } = "Brak członków drużyny.";

    // --- Group spell shortcuts (user-defined, persisted via GroupSpellStore; edited from
    // Ustawienia -> Drużyna, shown as buttons on the Group panel) ---
    public ObservableCollection<GroupSpellShortcut> GroupSpells { get; } = [];

    private string _newGroupSpellLabel = string.Empty;

    public string NewGroupSpellLabel
    {
        get => _newGroupSpellLabel;
        set
        {
            if (SetProperty(ref _newGroupSpellLabel, value))
            {
                AddGroupSpellCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private string _newGroupSpellName = string.Empty;

    public string NewGroupSpellName
    {
        get => _newGroupSpellName;
        set
        {
            if (SetProperty(ref _newGroupSpellName, value))
            {
                AddGroupSpellCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<MemSpellCircle> MemSpells { get; } = [];

    /// <summary>Names of spells this character currently has memorized and ready to cast (Memed,
    /// not still Meming) — kept in sync with <see cref="_latestMemorizedSpells"/> in
    /// <see cref="OnMemSpellsChanged"/>. Consumed by the group spell-shortcut buttons (see
    /// GroupPanelView.axaml/SpellMemorizedBrushConverter) to warn when clicking one would fail
    /// because the caster doesn't have that spell ready.</summary>
    public IReadOnlySet<string> MemorizedSpellNames { get; private set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // --- Automation rules (mock) ---
    public ObservableCollection<AutomationRuleEntry> AutomationRules { get; } = [];

    /// <summary>Aliases only (Type == "alias"), a filtered view over <see cref="AutomationRules"/>.</summary>
    public ObservableCollection<AutomationRuleEntry> AliasRules { get; } = [];

    /// <summary>Triggers only (Type == "trigger"), a filtered view over <see cref="AutomationRules"/>.</summary>
    public ObservableCollection<AutomationRuleEntry> TriggerRules { get; } = [];

    /// <summary>
    /// Grouping folders across every kind (timers, aliases, triggers, notes,
    /// autowalk). A folder's <see cref="FolderNode.Kind"/> selects which section
    /// renders it; membership is stored on each item via its FolderId.
    /// </summary>
    public ObservableCollection<FolderNode> Folders { get; } = [];

    /// <summary>
    /// Applies a folder's global flag to the folder itself and, cascading, to
    /// every descendant folder and every item that belongs to the subtree.
    /// Keeps item.IsGlobal in sync with the containing folder so persistence
    /// routes the whole subtree to the same file.
    /// </summary>
    private void SetFolderGlobalCascade(FolderNode folder, bool isGlobal)
    {
        folder.IsGlobal = isGlobal;

        foreach (var child in Folders.Where(f => f.ParentId == folder.Id).ToList())
        {
            SetFolderGlobalCascade(child, isGlobal);
        }

        foreach (var item in ItemsInFolder(folder.Id))
        {
            item.IsGlobal = isGlobal;
        }
    }

    /// <summary>Direct (non-recursive) item members of the given folder.</summary>
    private IEnumerable<IFolderItem> ItemsInFolder(string folderId)
    {
        foreach (var t in Timers.Where(t => t.FolderId == folderId)) yield return t;
        foreach (var r in AutomationRules.Where(r => r.FolderId == folderId)) yield return r;
        foreach (var n in Notes.Where(n => n.FolderId == folderId)) yield return n;
        foreach (var l in Locations.Where(l => l.FolderId == folderId)) yield return l;
    }

    /// <summary>
    /// True when the node lives inside a global folder subtree (any global
    /// ancestor), walking up the ParentId chain.
    /// </summary>
    private bool IsInsideGlobalFolder(string? folderId)
    {
        var guard = 0;
        while (folderId is not null && guard++ < 1000)
        {
            var folder = Folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null) return false;
            if (folder.IsGlobal) return true;
            folderId = folder.ParentId;
        }

        return false;
    }

    // --- Folder trees (hierarchy projected per section for the FolderTreeView) ---
    public ObservableCollection<FolderTreeNode> TimerTree { get; } = [];
    public ObservableCollection<FolderTreeNode> AliasTree { get; } = [];
    public ObservableCollection<FolderTreeNode> TriggerTree { get; } = [];
    public ObservableCollection<FolderTreeNode> NoteTree { get; } = [];
    public ObservableCollection<FolderTreeNode> AutowalkTree { get; } = [];

    // --- Live search boxes above each section's tree (see BuildAutomationSearchPredicate) ---
    private string _timerFilterText = string.Empty;
    private string _aliasFilterText = string.Empty;
    private string _triggerFilterText = string.Empty;

    public string TimerFilterText
    {
        get => _timerFilterText;
        set
        {
            if (SetProperty(ref _timerFilterText, value))
            {
                RebuildFolderTrees();
            }
        }
    }

    public string AliasFilterText
    {
        get => _aliasFilterText;
        set
        {
            if (SetProperty(ref _aliasFilterText, value))
            {
                RebuildFolderTrees();
            }
        }
    }

    public string TriggerFilterText
    {
        get => _triggerFilterText;
        set
        {
            if (SetProperty(ref _triggerFilterText, value))
            {
                RebuildFolderTrees();
            }
        }
    }

    /// <summary>When true, collection-change handlers skip rebuilds (bulk load).</summary>
    private bool _suppressTreeRebuild;

    private void OnFolderCollectionsChanged()
    {
        if (_suppressTreeRebuild)
        {
            return;
        }

        RebuildRuleViews();
        RebuildFolderTrees();
    }

    /// <summary>Rebuilds every section's folder tree from the flat collections.</summary>
    private void RebuildFolderTrees()
    {
        RebuildTree(TimerTree, FolderKind.Timers, Timers, BuildAutomationSearchPredicate(_timerFilterText));
        RebuildTree(AliasTree, FolderKind.Aliases, AliasRules, BuildAutomationSearchPredicate(_aliasFilterText));
        RebuildTree(TriggerTree, FolderKind.Triggers, TriggerRules, BuildAutomationSearchPredicate(_triggerFilterText));
        RebuildTree(NoteTree, FolderKind.Notes, Notes);
        RebuildTree(AutowalkTree, FolderKind.Autowalk, Locations);
    }

    /// <summary>Case-insensitive "contains" match against a timer's name/commands or a rule's
    /// name/pattern/action — null when <paramref name="filterText"/> is blank, so
    /// <see cref="RebuildTree"/> skips filtering entirely rather than allocating a
    /// matches-everything predicate.</summary>
    private static Func<IFolderItem, bool>? BuildAutomationSearchPredicate(string filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return null;
        }

        var needle = filterText.Trim();
        return item => item switch
        {
            TimerEntry timer =>
                timer.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                timer.CommandsText.Contains(needle, StringComparison.OrdinalIgnoreCase),
            AutomationRuleEntry rule =>
                rule.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                rule.Pattern.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                rule.Action.Contains(needle, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// Projects the folders of <paramref name="kind"/> and the given items into a
    /// tree of <see cref="FolderTreeNode"/>. Folders sort by name, items keep
    /// their collection order; loose items (no/unknown folder) render at the root.
    /// When <paramref name="matchesFilter"/> is given, a leaf survives only if it matches, and a
    /// folder survives only if at least one descendant does — the search box's live filter, with
    /// no effect on Notes/Autowalk (neither passes one).
    /// </summary>
    private void RebuildTree(
        ObservableCollection<FolderTreeNode> target,
        FolderKind kind,
        IEnumerable<IFolderItem> items,
        Func<IFolderItem, bool>? matchesFilter = null)
    {
        target.Clear();

        var folders = Folders.Where(f => f.Kind == kind).ToList();
        var folderIds = folders.Select(f => f.Id).ToHashSet();
        var nodesById = folders.ToDictionary(f => f.Id, f => new FolderTreeNode { IsFolder = true, Folder = f });

        // Link subfolders to parents; unknown/absent parents become roots.
        var roots = new List<FolderTreeNode>();
        foreach (var folder in folders)
        {
            var node = nodesById[folder.Id];
            if (folder.ParentId is not null && nodesById.TryGetValue(folder.ParentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        // Attach items to their folder, or to the root when loose.
        var looseItems = new List<IFolderItem>();
        foreach (var item in items)
        {
            var node = new FolderTreeNode { IsFolder = false, Content = item, Folder = null };
            if (item.FolderId is not null && nodesById.TryGetValue(item.FolderId, out var owner))
            {
                owner.Children.Add(node);
            }
            else
            {
                looseItems.Add(item);
            }
        }

        if (matchesFilter is not null)
        {
            for (var i = roots.Count - 1; i >= 0; i--)
            {
                if (!FolderTreeNodeMatchesFilter(roots[i], matchesFilter))
                {
                    roots.RemoveAt(i);
                }
            }

            looseItems = looseItems.Where(matchesFilter).ToList();
        }

        // Recursive item counts and activation state for folder badges/chrome.
        foreach (var root in roots)
        {
            ComputeFolderMetrics(root);
        }

        // Emit roots: folders (by name) first, then loose items in order.
        foreach (var folderNode in roots.OrderBy(n => n.Folder!.Name, StringComparer.OrdinalIgnoreCase))
        {
            SortFolderChildren(folderNode);
            target.Add(folderNode);
        }

        foreach (var item in looseItems)
        {
            target.Add(new FolderTreeNode { IsFolder = false, Content = item });
        }

        _ = folderIds; // reserved for future validation
    }

    /// <summary>Prunes <paramref name="node"/>'s subtree in place to only the branches leading to
    /// a match, returning whether anything survived (a leaf matching directly, or a folder with at
    /// least one surviving child).</summary>
    private static bool FolderTreeNodeMatchesFilter(FolderTreeNode node, Func<IFolderItem, bool> matches)
    {
        if (!node.IsFolder)
        {
            return node.Content is IFolderItem item && matches(item);
        }

        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            if (!FolderTreeNodeMatchesFilter(node.Children[i], matches))
            {
                node.Children.RemoveAt(i);
            }
        }

        return node.Children.Count > 0;
    }

    private static FolderMetrics ComputeFolderMetrics(FolderTreeNode node)
    {
        if (!node.IsFolder)
        {
            return node.Content is IActivatableFolderItem activatable
                ? new FolderMetrics(1, activatable.IsEnabled ? 1 : 0, activatable.IsEnabled ? 0 : 1)
                : new FolderMetrics(1, 0, 0);
        }

        var metrics = new FolderMetrics(0, 0, 0);
        foreach (var child in node.Children)
        {
            metrics += ComputeFolderMetrics(child);
        }

        node.ItemCount = metrics.ItemCount;
        node.HasActivatableItems = metrics.EnabledCount + metrics.DisabledCount > 0;
        node.IsAllEnabled = node.HasActivatableItems && metrics.DisabledCount == 0;
        node.IsAllDisabled = node.HasActivatableItems && metrics.EnabledCount == 0;
        node.IsMixedActivation = metrics.EnabledCount > 0 && metrics.DisabledCount > 0;
        node.ActivationText = node.IsAllEnabled
            ? "AKTYWNY"
            : node.IsAllDisabled ? "WYŁĄCZONY" : node.IsMixedActivation ? "MIESZANY" : string.Empty;
        return metrics;
    }

    private readonly record struct FolderMetrics(int ItemCount, int EnabledCount, int DisabledCount)
    {
        public static FolderMetrics operator +(FolderMetrics left, FolderMetrics right) => new(
            left.ItemCount + right.ItemCount,
            left.EnabledCount + right.EnabledCount,
            left.DisabledCount + right.DisabledCount);
    }

    private static void SortFolderChildren(FolderTreeNode folderNode)
    {
        var ordered = folderNode.Children
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.IsFolder ? c.Folder!.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        folderNode.Children.Clear();
        foreach (var child in ordered)
        {
            folderNode.Children.Add(child);
            if (child.IsFolder)
            {
                SortFolderChildren(child);
            }
        }
    }

    // ========================================================================
    // Folder commands (generic across kinds)
    // ========================================================================

    public RelayCommand<FolderKind> CreateFolderCommand => new(CreateFolder);
    public RelayCommand<FolderNode> CreateSubfolderCommand => new(CreateSubfolder);
    public RelayCommand<FolderNode> RenameFolderCommand => new(RenameFolder);
    public RelayCommand<FolderNode> DeleteFolderCommand => new(DeleteFolder);
    public RelayCommand<FolderNode> ToggleFolderGlobalCommand => new(ToggleFolderGlobal);
    public RelayCommand<FolderNode> ToggleFolderEnabledCommand => new(ToggleFolderEnabled);
    public RelayCommand<FolderMoveRequest> MoveIntoFolderCommand => new(MoveIntoFolder);

    private void CreateFolder(FolderKind kind)
    {
        Folders.Add(new FolderNode { Name = "Nowy folder", Kind = kind });
        SaveActiveProfile();
    }

    private void CreateSubfolder(FolderNode? parent)
    {
        if (parent is null)
        {
            return;
        }

        Folders.Add(new FolderNode
        {
            Name = "Nowy folder",
            Kind = parent.Kind,
            ParentId = parent.Id,
            IsGlobal = parent.IsGlobal,
        });
        SaveActiveProfile();
    }

    /// <summary>Persists an inline folder rename and refreshes the trees.</summary>
    private void RenameFolder(FolderNode? folder)
    {
        if (folder is null)
        {
            return;
        }

        RebuildFolderTrees();
        SaveActiveProfile();
    }

    private void DeleteFolder(FolderNode? folder)
    {
        if (folder is null)
        {
            return;
        }

        var ids = CollectSubtreeFolderIds(folder);

        foreach (var timer in Timers.Where(t => t.FolderId is not null && ids.Contains(t.FolderId)).ToList())
        {
            Timers.Remove(timer);
        }

        foreach (var rule in AutomationRules.Where(r => r.FolderId is not null && ids.Contains(r.FolderId)).ToList())
        {
            AutomationRules.Remove(rule);
        }

        foreach (var note in Notes.Where(n => n.FolderId is not null && ids.Contains(n.FolderId)).ToList())
        {
            Notes.Remove(note);
        }

        foreach (var location in Locations.Where(l => l.FolderId is not null && ids.Contains(l.FolderId)).ToList())
        {
            Locations.Remove(location);
        }

        foreach (var f in Folders.Where(f => ids.Contains(f.Id)).ToList())
        {
            Folders.Remove(f);
        }

        AfterFolderStructureChange(folder.Kind);
    }

    private void ToggleFolderGlobal(FolderNode? folder)
    {
        if (folder is null)
        {
            return;
        }

        SetFolderGlobalCascade(folder, !folder.IsGlobal);
        AfterFolderStructureChange(folder.Kind);
    }

    private void ToggleFolderEnabled(FolderNode? folder)
    {
        if (folder is null)
        {
            return;
        }

        var ids = CollectSubtreeFolderIds(folder);
        var timers = Timers.Where(t => t.FolderId is not null && ids.Contains(t.FolderId)).ToList();
        var rules = AutomationRules.Where(r => r.FolderId is not null && ids.Contains(r.FolderId)).ToList();

        // Enable all when anything is disabled, otherwise disable all.
        var enable = timers.Any(t => !t.IsEnabled) || rules.Any(r => !r.IsEnabled);
        foreach (var timer in timers)
        {
            timer.IsEnabled = enable;
        }

        foreach (var rule in rules)
        {
            rule.IsEnabled = enable;
        }

        AfterFolderStructureChange(folder.Kind);
    }

    /// <summary>
    /// Moves a leaf or a folder into another folder of the same domain. Cycles
    /// and cross-domain moves are rejected; global ownership follows the target.
    /// </summary>
    private void MoveIntoFolder(FolderMoveRequest? request)
    {
        if (request is null || !Folders.Contains(request.Target))
        {
            return;
        }

        if (request.Source is FolderNode sourceFolder)
        {
            if (!Folders.Contains(sourceFolder) || sourceFolder.Kind != request.Target.Kind ||
                sourceFolder.Id == request.Target.Id ||
                CollectSubtreeFolderIds(sourceFolder).Contains(request.Target.Id))
            {
                return;
            }

            sourceFolder.ParentId = request.Target.Id;
            SetFolderGlobalCascade(sourceFolder, request.Target.IsGlobal);
            AfterFolderStructureChange(sourceFolder.Kind);
            return;
        }

        if (request.Source is not IFolderItem item || GetFolderKind(item) != request.Target.Kind)
        {
            return;
        }

        item.FolderId = request.Target.Id;
        item.IsGlobal = request.Target.IsGlobal;
        AfterFolderStructureChange(request.Target.Kind);
    }

    private static FolderKind? GetFolderKind(IFolderItem item) => item switch
    {
        TimerEntry => FolderKind.Timers,
        AutomationRuleEntry { Type: "alias" } => FolderKind.Aliases,
        AutomationRuleEntry { Type: "trigger" } => FolderKind.Triggers,
        NoteEntry => FolderKind.Notes,
        AutowalkLocation => FolderKind.Autowalk,
        _ => null,
    };

    /// <summary>Creates a JSON-ready package for one automation item or folder subtree.</summary>
    public AutomationTransferPackage CreateAutomationTransferPackage(object selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection is FolderNode folder)
        {
            if (folder.Kind is not (FolderKind.Aliases or FolderKind.Triggers or FolderKind.Timers))
            {
                throw new InvalidOperationException("Eksport jest dostępny tylko dla aliasów, triggerów i timerów.");
            }

            var ids = CollectSubtreeFolderIds(folder);
            var package = new AutomationTransferPackage { Kind = folder.Kind };
            package.Folders.AddRange(Folders.Where(f => ids.Contains(f.Id)).Select(f => new ProfileFolder
            {
                Id = f.Id,
                ParentId = ids.Contains(f.ParentId ?? string.Empty) ? f.ParentId : null,
                Name = f.Name,
                Kind = f.Kind,
                IsGlobal = f.IsGlobal,
            }));

            AddTransferItems(package, ids);
            return package;
        }

        return selection switch
        {
            TimerEntry timer => new AutomationTransferPackage
            {
                Kind = FolderKind.Timers,
                Timers = [CloneProfileTimer(ToProfileTimer(timer), folderId: null)],
            },
            AutomationRuleEntry { Type: "alias" } alias => new AutomationTransferPackage
            {
                Kind = FolderKind.Aliases,
                Aliases = [CloneProfileRule(ToProfileRule(alias), folderId: null)],
            },
            AutomationRuleEntry { Type: "trigger" } trigger => new AutomationTransferPackage
            {
                Kind = FolderKind.Triggers,
                Triggers = [CloneProfileRule(ToProfileRule(trigger), folderId: null)],
            },
            _ => throw new InvalidOperationException("Tego elementu nie można wyeksportować."),
        };
    }

    private void AddTransferItems(AutomationTransferPackage package, HashSet<string> folderIds)
    {
        if (package.Kind == FolderKind.Timers)
        {
            package.Timers.AddRange(Timers.Where(t => t.FolderId is not null && folderIds.Contains(t.FolderId))
                .Select(t => CloneProfileTimer(ToProfileTimer(t), t.FolderId)));
        }
        else
        {
            var rules = AutomationRules.Where(r => r.FolderId is not null && folderIds.Contains(r.FolderId));
            var target = package.Kind == FolderKind.Aliases ? package.Aliases : package.Triggers;
            target.AddRange(rules.Where(r => GetFolderKind(r) == package.Kind)
                .Select(r => CloneProfileRule(ToProfileRule(r), r.FolderId)));
        }
    }

    /// <summary>Imports a validated package, remapping every folder id to avoid collisions.</summary>
    public void ImportAutomationTransferPackage(AutomationTransferPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        AutomationTransferService.ValidatePackage(package);

        var idMap = package.Folders.ToDictionary(f => f.Id, _ => Guid.NewGuid().ToString("N"));
        _suppressTreeRebuild = true;
        try
        {
            foreach (var folder in package.Folders)
            {
                Folders.Add(new FolderNode
                {
                    Id = idMap[folder.Id],
                    ParentId = folder.ParentId is not null && idMap.TryGetValue(folder.ParentId, out var parentId)
                        ? parentId
                        : null,
                    Name = folder.Name,
                    Kind = package.Kind,
                    IsGlobal = folder.IsGlobal,
                });
            }

            foreach (var root in package.Folders.Where(folder => folder.ParentId is null))
            {
                SetFolderGlobalCascade(Folders.First(folder => folder.Id == idMap[root.Id]), root.IsGlobal);
            }

            foreach (var timer in package.Timers)
            {
                var folderId = RemapFolderId(timer.FolderId, idMap);
                var isGlobal = ImportedItemIsGlobal(folderId, timer.IsGlobal);
                Timers.Add(MakeTimerEntry(CloneProfileTimer(timer, folderId), isGlobal));
            }

            foreach (var alias in package.Aliases)
            {
                var clone = CloneProfileRule(alias, RemapFolderId(alias.FolderId, idMap));
                clone.Type = "alias";
                AutomationRules.Add(MakeRuleEntry(clone, ImportedItemIsGlobal(clone.FolderId, clone.IsGlobal)));
            }

            foreach (var trigger in package.Triggers)
            {
                var clone = CloneProfileRule(trigger, RemapFolderId(trigger.FolderId, idMap));
                clone.Type = "trigger";
                AutomationRules.Add(MakeRuleEntry(clone, ImportedItemIsGlobal(clone.FolderId, clone.IsGlobal)));
            }
        }
        finally
        {
            _suppressTreeRebuild = false;
        }

        RebuildRuleViews();
        AfterFolderStructureChange(package.Kind);
    }

    public void ReportAutomationTransfer(string message, bool isError = false) =>
        AddToast(message, isError ? "error" : "info");

    private static string? RemapFolderId(string? folderId, IReadOnlyDictionary<string, string> idMap) =>
        folderId is not null && idMap.TryGetValue(folderId, out var mapped) ? mapped : null;

    private bool ImportedItemIsGlobal(string? folderId, bool looseValue) =>
        folderId is null ? looseValue : Folders.First(folder => folder.Id == folderId).IsGlobal;

    private static ProfileRule CloneProfileRule(ProfileRule source, string? folderId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = source.Name,
        Type = source.Type,
        Pattern = source.Pattern,
        Action = source.Action,
        IsScript = source.IsScript,
        IsEnabled = source.IsEnabled,
        IsGlobal = source.IsGlobal,
        FolderId = folderId,
    };

    private static ProfileTimer CloneProfileTimer(ProfileTimer source, string? folderId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = source.Name,
        Minutes = source.Minutes,
        Seconds = source.Seconds,
        Milliseconds = source.Milliseconds,
        Commands = [.. source.Commands],
        CommandsText = source.CommandsText,
        IsScript = source.IsScript,
        IsEnabled = source.IsEnabled,
        IsGlobal = source.IsGlobal,
        FolderId = folderId,
    };

    /// <summary>Folder ids of the given folder plus every descendant folder.</summary>
    private HashSet<string> CollectSubtreeFolderIds(FolderNode root)
    {
        var ids = new HashSet<string> { root.Id };
        var changed = true;
        var guard = 0;
        while (changed && guard++ < 1000)
        {
            changed = false;
            foreach (var folder in Folders)
            {
                if (folder.ParentId is not null && ids.Contains(folder.ParentId) && ids.Add(folder.Id))
                {
                    changed = true;
                }
            }
        }

        return ids;
    }

    /// <summary>Persists and re-syncs the engines affected by a folder change.</summary>
    private void AfterFolderStructureChange(FolderKind kind)
    {
        RebuildFolderTrees();

        if (kind is FolderKind.Aliases or FolderKind.Triggers)
        {
            ApplyAutomation();
        }
        else if (kind is FolderKind.Timers)
        {
            CancelAllTimers();
            SyncAllTimers();
        }

        SaveActiveProfile();
    }

    /// <summary>
    /// Rebuilds the alias/trigger filtered views from <see cref="AutomationRules"/>.
    /// Call after any change to the source collection or a rule's Type.
    /// </summary>
    private void RebuildRuleViews()
    {
        AliasRules.Clear();
        TriggerRules.Clear();
        foreach (var rule in AutomationRules)
        {
            switch (rule.Type)
            {
                case "alias":
                    AliasRules.Add(rule);
                    break;
                case "trigger":
                    TriggerRules.Add(rule);
                    break;
            }
        }
    }

    // --- Notes ---
    public ObservableCollection<NoteEntry> Notes { get; } = [];

    // --- Toast messages ---
    public ObservableCollection<ToastMessage> Toasts { get; } = [];

    // ========================================================================
    // New commands
    // ========================================================================

    public RelayCommand AddNoteCommand => new(AddNote);
    public RelayCommand<NoteEntry> DeleteNoteCommand => new(DeleteNote);
    public RelayCommand<NoteEntry> EditNoteCommand => new(EditNote);
    public RelayCommand CancelNoteEditCommand => new(CancelNoteEdit);
    public RelayCommand<string> CopyToCommandBarCommand => new(CopyToCommandBar);
    public RelayCommand ClearToastsCommand => new(ClearToasts);

    // ========================================================================
    // Existing commands (preserved unchanged)
    // ========================================================================

    private bool CanConnect() =>
        !IsBusy &&
        !IsConnected &&
        !string.IsNullOrWhiteSpace(Host) &&
        Port is >= 1 and <= 65535;

    private bool CanDisconnect() => !IsBusy && IsConnected;

    private bool CanSendCommand() =>
        !IsBusy && IsConnected && _bookRefreshCts is null && _rareRefreshCts is null && _mapujCts is null;

    private async Task ConnectAsync()
    {
        IsBusy = true;
        EmitSystem($"Łączenie z {Host}:{Port}...", 36);

        try
        {
            _session.EncodingMode = Encoding;
            await _session.ConnectAsync(Host.Trim(), Port);
            IsConnected = true;
            await AutoLoginAsync();
        }
        catch (Exception exception)
        {
            IsConnected = false;
            StatusText = "Błąd połączenia";
            EmitSystem(exception.Message, 31);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sends the account name and stored password right after connecting,
    /// so the MUD login prompt is answered automatically.
    /// </summary>
    private async Task AutoLoginAsync()
    {
        var login = _activeProfileLogin;
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(_activeProfilePassword))
        {
            return;
        }

        // Give the server a moment to show the login prompt before answering it.
        await Task.Delay(500);

        if (_activeProfileNeedsRegistration)
        {
            // First connection for a freshly created account. KillerMUD asks to
            // confirm the new character ("t"), then the password twice, and a
            // single space skips the intro screen. This runs only once — the
            // flag is cleared and persisted so later logins use the plain
            // name + password sequence below.
            await _session.SendCommandAsync(login);
            await Task.Delay(500);
            await _session.SendCommandAsync("t");
            await Task.Delay(500);
            await _session.SendCommandAsync(_activeProfilePassword);
            await Task.Delay(500);
            await _session.SendCommandAsync(_activeProfilePassword);
            await Task.Delay(500);
            await _session.SendCommandAsync(" ");

            _activeProfileNeedsRegistration = false;
            SaveActiveProfile();
            EmitSystem($"Utworzono i zalogowano nową postać {login}.", 36);
            await SyncServerCodepageAsync();
            return;
        }

        await _session.SendCommandAsync(login);
        await Task.Delay(500);
        await _session.SendCommandAsync(_activeProfilePassword);
        EmitSystem($"Zalogowano automatycznie jako {login}.", 36);
        await SyncServerCodepageAsync();
    }

    /// <summary>
    /// KillerMUD renders Polish diacritics per its own "config codepage" in-game setting
    /// (iso/win/nopol), independent of anything the client guesses from received bytes.
    /// When the account picks an explicit ISO-8859-2 or Windows-1250 encoding, tell the
    /// server to match it so both sides actually agree instead of relying on detection.
    /// Auto/UTF-8 send nothing — the server has no matching "utf8" mode to request.
    /// </summary>
    private async Task SyncServerCodepageAsync()
    {
        var codepageArg = Encoding switch
        {
            MudTextEncodings.Iso88592 => "iso",
            MudTextEncodings.Windows1250 => "win",
            _ => null,
        };

        if (codepageArg is null)
        {
            return;
        }

        await Task.Delay(300);
        await _session.SendCommandAsync($"config codepage {codepageArg}");
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        Map.StopMapEditor(
            "Mapowanie zatrzymane przed rozłączeniem. Po ponownym połączeniu uruchom je ręcznie.");
        StopAutoFarm("Farma zatrzymana: rozłączono.");

        try
        {
            await _session.DisconnectAsync();
        }
        finally
        {
            IsConnected = false;
            IsBusy = false;
        }
    }

    /// <summary>Client-side "/reconnect" meta-command — disconnects (if connected) and reconnects
    /// using the current profile's host/port, including auto-login. Same effect as manually
    /// clicking Rozłącz then Połącz, but usable from timers/triggers/aliases (via
    /// send("/reconnect")) and from the command bar, the same way "/recast" is.</summary>
    private async Task ReconnectCurrentProfileAsync()
    {
        if (IsBusy)
        {
            EmitSystem("Nie można teraz wykonać /reconnect: klient jest zajęty.", 33);
            return;
        }

        if (IsConnected)
        {
            await DisconnectAsync();
        }

        await ConnectAsync();
    }

    private async Task SendCurrentCommandAsync()
    {
        var sourceCommand = CommandText.Trim();

        // Split on the stacking separator first (also handles newlines).
        // Alias processing runs per segment; autowalk commands are consumed
        // per segment, and non-slash segments are forwarded normally.
        // An empty command is meaningful to a MUD: it sends a bare line ending.
        // CommandStacker intentionally discards empty items for aliases and timers,
        // so preserve only the explicitly empty command entered by the user here.
        IReadOnlyList<string> segments = sourceCommand.Length == 0
            ? [string.Empty]
            : CommandStacker.Split(sourceCommand, CommandStackingSeparator);

        // Track history – record the original typed command as one entry.
        CommandHistory.Insert(0, sourceCommand);
        while (CommandHistory.Count > CommandHistoryMaxSize)
        {
            CommandHistory.RemoveAt(CommandHistory.Count - 1);
        }

        foreach (var segment in segments)
        {
            if (await TryHandleMapEditorCommandAsync(segment))
            {
                continue;
            }

            if (TryHandleAutowalkCommand(segment))
            {
                continue;
            }

            if (string.Equals(segment, "/recast", StringComparison.OrdinalIgnoreCase))
            {
                await RecastMissingBuffsAsync();
                continue;
            }

            if (string.Equals(segment, "/reconnect", StringComparison.OrdinalIgnoreCase))
            {
                await ReconnectCurrentProfileAsync();
                continue;
            }

            if (TryParseMapujCommand(segment, out var mapujArgument))
            {
                if (mapujArgument is null)
                {
                    AddToast("Użycie: /mapuj <klasa> albo /mapuj <liczba prób> (np. /mapuj 127)", "info");
                }
                else if (int.TryParse(mapujArgument, out var tryCount))
                {
                    StartArtifactTryMapping(tryCount);
                }
                else
                {
                    StartAbilityMapping(mapujArgument);
                }

                continue;
            }

            if (TryHandleSettingsToggleCommand(segment))
            {
                continue;
            }

            // Alias processing happens per stacked segment so that an alias
            // that replaces one segment can still produce multiple commands
            // (via newlines in its replacement).
            var commands = _aliases.ProcessCommands(segment, CommandStackingSeparator);

            foreach (var command in commands)
            {
                var mapperDecision = Map.PrepareMapEditorCommand(command);
                if (!mapperDecision.Allow)
                {
                    EmitSystem($"Mapper: {mapperDecision.Message}", 33);
                    continue;
                }

                EmitCommandEcho(command);

                try
                {
                    await _session.SendCommandAsync(command);
                }
                catch (Exception exception)
                {
                    if (Map.IsMapEditorAwaitingRoomInfo)
                    {
                        Map.CancelPendingMapMovement(
                            $"Nie udało się wysłać ruchu mappera: {exception.Message}");
                    }
                    EmitSystem(exception.Message, 31);
                }
            }
        }
    }

    // ========================================================================
    // "/mapuj <klasa>" — loops "help <name>" over a class's seeded skills/spells
    // (see AbilitySeedCatalog) and saves the captured text via AbilityCaptureStore.
    // "/mapuj <liczba>" — loops "try 1".."try <liczba>" instead (artifact identification) and
    // saves the captured text via ArtifactTryStore — see StartArtifactTryMapping below.
    // ========================================================================

    /// <summary>Pure parse of "/mapuj &lt;argument&gt;". Returns false for anything not starting
    /// with "/mapuj". When true: <paramref name="className"/> is the trimmed argument — a class
    /// name, or a plain integer meaning "/mapuj &lt;liczba&gt;" (the caller distinguishes the two
    /// with <c>int.TryParse</c>) — or null when no argument was given (caller shows a usage
    /// message rather than starting a run).</summary>
    internal static bool TryParseMapujCommand(string command, out string? className)
    {
        className = null;
        const string prefix = "/mapuj";
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (command.Length > prefix.Length && !char.IsWhiteSpace(command[prefix.Length]))
        {
            // e.g. "/mapujwhatever" — a different command that merely starts with "/mapuj".
            return false;
        }

        var argument = command[prefix.Length..].Trim();
        className = argument.Length == 0 ? null : argument;
        return true;
    }

    private void StartAbilityMapping(string className)
    {
        var seed = AbilitySeedCatalog.Find(className);
        if (seed is null)
        {
            var known = AbilitySeedCatalog.KnownClasses.Count == 0
                ? "brak"
                : string.Join(", ", AbilitySeedCatalog.KnownClasses);
            AddToast($"Brak zapisanych umiejętności/zaklęć dla klasy „{className}”. Znane klasy: {known}.", "error");
            return;
        }

        if (_bookRefreshCts is not null || _rareRefreshCts is not null || _mapujCts is not null)
        {
            AddToast("Inne odświeżanie/mapowanie katalogu jest już w toku.", "error");
            return;
        }

        _mapujTask = StartAbilityMappingAsync(seed);
    }

    private async Task StartAbilityMappingAsync(ClassAbilitySeed seed)
    {
        var cancellation = new CancellationTokenSource();
        _mapujCts = cancellation;
        _sendCommandCommand.NotifyCanExecuteChanged();
        var names = seed.AllNames;
        AddToast($"Mapuję „{seed.Class}” — {names.Count} umiejętności/zaklęć...", "info");
        var lockTaken = false;

        try
        {
            await _triggerSendLock.WaitAsync(cancellation.Token);
            lockTaken = true;
            var document = _abilityCaptureStore.Load();
            // Dedupe by ability name, not by which class triggered the original capture — a skill
            // shared by several classes (e.g. "axe") would otherwise pick up one duplicate entry
            // per class ever mapped, since AvailableForClasses already lists every class regardless
            // of which one's /mapuj run captured it (see AbilityCaptureEntry.AvailableForClasses).
            var namesBeingMapped = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var byName = document.Entries
                .Where(entry => !namesBeingMapped.Contains(entry.Name))
                .ToList();

            var captured = await _abilityMappingCoordinator.RunAsync(
                seed.Class,
                names,
                SendAbilityMappingCommandAsync,
                cancellationToken: cancellation.Token,
                // A class can be 40+ round-trips — persist after every one so a disconnect or
                // crash partway through doesn't discard everything mapped in this run.
                onEntryCaptured: (mappedSoFar, token) => _abilityCaptureStore.SaveAsync(
                    new AbilityCaptureDocument { Entries = [.. byName, .. mappedSoFar] },
                    token));

            await _abilityCaptureStore.SaveAsync(
                new AbilityCaptureDocument { Entries = [.. byName, .. captured] },
                cancellation.Token);
            AddToast($"Zmapowano {captured.Count} umiejętności/zaklęć dla „{seed.Class}”.", "info");
        }
        catch (OperationCanceledException)
        {
            AddToast($"Mapowanie „{seed.Class}” zostało anulowane.", "info");
        }
        catch (Exception exception)
        {
            AddToast($"Mapowanie „{seed.Class}” nie powiodło się: {exception.Message}", "error");
            EmitSystem($"/mapuj: {exception.Message}", 31);
        }
        finally
        {
            if (lockTaken)
            {
                _triggerSendLock.Release();
            }

            _mapujCts = null;
            _sendCommandCommand.NotifyCanExecuteChanged();
            cancellation.Dispose();
        }
    }

    // ========================================================================
    // "/mapuj <liczba>" — loops "try 1".."try <liczba>" (the game's artifact-identification
    // command) and saves the captured text via ArtifactTryStore.
    // ========================================================================

    /// <summary>Above this, "/mapuj &lt;liczba&gt;" almost certainly reflects a typo rather than a
    /// genuinely huge artifact count — each "try" round-trip takes at least a few hundred ms, so
    /// an unbounded count could turn one fat-fingered command into an hours-long unattended run.</summary>
    private const int MaxArtifactTryCount = 2000;

    private void StartArtifactTryMapping(int count)
    {
        if (count < 1)
        {
            AddToast("Użycie: /mapuj <liczba prób> (np. /mapuj 127) — wykonuje try 1..<liczba>.", "info");
            return;
        }

        if (count > MaxArtifactTryCount)
        {
            AddToast($"Zbyt duża liczba prób ({count}) — maksimum to {MaxArtifactTryCount}.", "error");
            return;
        }

        if (_bookRefreshCts is not null || _rareRefreshCts is not null || _mapujCts is not null)
        {
            AddToast("Inne odświeżanie/mapowanie katalogu jest już w toku.", "error");
            return;
        }

        _mapujTask = StartArtifactTryMappingAsync(count);
    }

    private async Task StartArtifactTryMappingAsync(int count)
    {
        var cancellation = new CancellationTokenSource();
        _mapujCts = cancellation;
        _sendCommandCommand.NotifyCanExecuteChanged();
        AddToast($"Mapuję artefakty — try 1..{count}...", "info");
        var lockTaken = false;

        try
        {
            await _triggerSendLock.WaitAsync(cancellation.Token);
            lockTaken = true;

            var captured = await _artifactTryMappingCoordinator.RunAsync(
                count,
                SendAbilityMappingCommandAsync,
                cancellationToken: cancellation.Token,
                // A run can be a hundred-plus round-trips — persist after every one so a
                // disconnect or crash partway through doesn't discard everything captured so far.
                onEntryCaptured: (mappedSoFar, token) => _artifactTryStore.SaveAsync(
                    new ArtifactTryDocument { Entries = [.. mappedSoFar] },
                    token));

            await _artifactTryStore.SaveAsync(new ArtifactTryDocument { Entries = [.. captured] }, cancellation.Token);
            AddToast($"Zmapowano {captured.Count} artefaktów (try 1..{count}) do {_artifactTryStore.Path}.", "info");
        }
        catch (OperationCanceledException)
        {
            AddToast("Mapowanie artefaktów zostało anulowane.", "info");
        }
        catch (Exception exception)
        {
            AddToast($"Mapowanie artefaktów nie powiodło się: {exception.Message}", "error");
            EmitSystem($"/mapuj: {exception.Message}", 31);
        }
        finally
        {
            if (lockTaken)
            {
                _triggerSendLock.Release();
            }

            _mapujCts = null;
            _sendCommandCommand.NotifyCanExecuteChanged();
            cancellation.Dispose();
        }
    }

    private async Task SendAbilityMappingCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (Map.IsMapEditorActive)
        {
            throw new InvalidOperationException("Mapowanie umiejętności jest niedostępne podczas mapowania.");
        }

        var echo = command.Length == 0 ? "[PUSTA WIADOMOŚĆ]" : command;
        await Dispatcher.UIThread.InvokeAsync(() => EmitSystem($"> {echo}", 90));
        await _session.SendCommandAsync(command, cancellationToken);
    }

    // ========================================================================
    // "/<nazwa> [on|off]" — generic toggle commands for every automation/preference switch that
    // otherwise only lived behind a checkbox somewhere in the UI. CommandToggles is the single
    // source of truth for both the command dispatch below and the Help panel's "Automatyzacje" tab
    // (MainWindow.axaml), so the two can never drift the way the hand-copied Help text already had
    // for other commands (e.g. "/mapuj" going undocumented for a while).
    // ========================================================================

    private IReadOnlyList<CommandToggleEntry>? _commandToggles;

    public IReadOnlyList<CommandToggleEntry> CommandToggles => _commandToggles ??= BuildCommandToggles();

    private IReadOnlyList<CommandToggleEntry> BuildCommandToggles() =>
    [
        new("autostand", "Autostand", "Automatyczne wstawanie, gdy zostaniesz powalony.",
            () => AutoStandOnLyingEnabled, v => AutoStandOnLyingEnabled = v),
        new("standorder", "Autostand (drużyna)", "Rozkaz wstania dla drużyny, gdy Ty wstajesz (tylko lider).",
            () => AutoStandOrderEnabled, v => AutoStandOrderEnabled = v),
        new("restorder", "Autorest (drużyna)", "Rozkaz odpoczynku dla drużyny, gdy Ty odpoczywasz (tylko lider).",
            () => AutoRestOrderEnabled, v => AutoRestOrderEnabled = v),
        new("autofollow", "Autofollow", "Automatyczne podążanie za liderem drużyny.",
            () => AutoFollowLeaderEnabled, v => AutoFollowLeaderEnabled = v),
        new("mirrorposition", "Kopiuj postawę lidera",
            "Gdy lider usiądzie/wstanie/odpocznie, robisz to samo — przydatne, gdy liderem nie jest ten klient.",
            () => AutoMirrorLeaderPositionEnabled, v => AutoMirrorLeaderPositionEnabled = v),
        new("autoassist", "Autoassist", "Automatyczna pomoc liderowi w walce.",
            () => AutoAssistEnabled, v => AutoAssistEnabled = v),
        new("autoassistnpc", "Autoassist NPC", "Automatyczna pomoc sojuszniczemu NPC w walce.",
            () => AutoAssistNpcEnabled, v => AutoAssistNpcEnabled = v),
        new("autowield", "Autowield", "Automatyczne dobywanie broni przed walką.",
            () => AutowieldEnabled, v => AutowieldEnabled = v),
        new("autoscan", "Autoscan", "Automatyczny „scan” po wejściu do pokoju.",
            () => Map.AutoScanOnRoomEnter, v => Map.AutoScanOnRoomEnter = v),
        new("autokill", "Autokill", "Automatyczny atak wybranych mobów po wejściu do pokoju.",
            () => Map.AutoKillOnRoomEnter, v => Map.AutoKillOnRoomEnter = v),
        new("autorecastsnap", "Autorecast (przeskok lidera)", "Auto-recast brakujących buffów po przeskoku do lidera.",
            () => AutoRecastOnLeaderSnapEnabled, v => AutoRecastOnLeaderSnapEnabled = v),
        new("grouporders", "Rozkazy drużynowe", "Wykonywanie rozkazów drużynowych wysyłanych przez lidera.",
            () => GroupOrdersEnabled, v => GroupOrdersEnabled = v),
        new("movementrecovery", "Odzyskiwanie ruchu", "Automatyczne wznawianie autowalk po utracie ruchu.",
            () => AutowalkMovementRecoveryEnabled, v => AutowalkMovementRecoveryEnabled = v),
        new("restonarrival", "Odpoczynek po dotarciu", "Automatyczny „rest” po dotarciu do celu autowalk.",
            () => AutowalkRestOnArrivalEnabled, v => AutowalkRestOnArrivalEnabled = v),
        new("autogrouprefresh", "Odświeżanie drużyny", "Automatyczne odświeżanie składu drużyny po wyczerpaniu.",
            () => AutoGroupRefreshOnExhaustedEnabled, v => AutoGroupRefreshOnExhaustedEnabled = v),
        new("lordmode", "Tryb lorda", "Tryb administracyjny mappera (Lord Mode).",
            () => LordModeEnabled, v => LordModeEnabled = v),
        new("numericdamage", "Liczbowe obrażenia", "Liczbowe obrażenia przy komunikatach walki.",
            () => ShowNumericDamageEnabled, v => ShowNumericDamageEnabled = v),
        new("bookclasses", "Klasy przy księgach", "Adnotacja klasy przy losowych księgach.",
            () => AnnotateRandomBookClassEnabled, v => AnnotateRandomBookClassEnabled = v),
        new("skilltrainers", "Nauczyciele przy skillach", "Adnotacja nauczyciela przy liście umiejętności.",
            () => AnnotateSkillTrainersEnabled, v => AnnotateSkillTrainersEnabled = v),
        new("spellsources", "Moby przy czarach", "Adnotacja źródłowego moba przy liście czarów.",
            () => AnnotateSpellSourcesEnabled, v => AnnotateSpellSourcesEnabled = v),
        new("scoreranges", "Liczby przy score", "Adnotacja przybliżonego zakresu liczbowego przy cechach postaci.",
            () => AnnotateScoreEnabled, v => AnnotateScoreEnabled = v),
        new("wordwrap", "Zawijanie tekstu", "Zawijanie tekstu w terminalu.",
            () => OutputWordWrap, v => OutputWordWrap = v),
        new("vitalsbars", "Paski żywotności", "Paski HP/many/ruchu w terminalu.",
            () => ShowTerminalVitalsBars, v => ShowTerminalVitalsBars = v),
        new("clearinput", "Czyszczenie pola komend", "Czyszczenie pola komend po wysłaniu.",
            () => ClearCommandInputAfterSend, v => ClearCommandInputAfterSend = v),
        new("groupnumbers", "Numery drużyny", "Numery zamiast imion członków drużyny na mapie.",
            () => Map.ShowGroupMembersAsNumbers, v => Map.ShowGroupMembersAsNumbers = v),
        new("mapdoubleclick", "Autowalk po kliknięciu", "Autowalk po podwójnym kliknięciu na mapie.",
            () => Map.AutoWalkOnMapDoubleClick, v => Map.AutoWalkOnMapDoubleClick = v),
        new("extendedeffects", "Rozszerzone efekty", "Rozszerzone informacje o aktywnych efektach.",
            () => ShowExtendedEffects, v => ShowExtendedEffects = v),
        new("chatsound", "Dźwięk czatu", "Krótki dźwięk przy nowej wiadomości na czacie.",
            () => ChatSoundOnNewMessageEnabled, v => ChatSoundOnNewMessageEnabled = v),
        new("timersbar", "Timery na pasku", "Pasek aktywnych timerów nad polem komend.",
            () => ShowTimersOnStatusBarEnabled, v => ShowTimersOnStatusBarEnabled = v),
        new("triggersbar", "Triggery na pasku", "Pasek aktywnych triggerów nad polem komend.",
            () => ShowTriggersOnStatusBarEnabled, v => ShowTriggersOnStatusBarEnabled = v),
    ];

    /// <summary>Bare "/&lt;nazwa&gt;" flips the current value; "/&lt;nazwa&gt; on|off" (also
    /// start/stop, wlacz/wylacz, 1/0, tak/nie) sets it explicitly — the same start/stop vocabulary
    /// as the global panic commands, just scoped to one function instead of everything (see
    /// <see cref="TryParseToggleArgument"/>). Returns false — letting the dispatch chain in
    /// <see cref="SendCurrentCommandAsync"/> keep looking — for anything that isn't one of
    /// <see cref="CommandToggles"/>' own command names, so this can never swallow an unrelated "/"
    /// command.</summary>
    private bool TryHandleSettingsToggleCommand(string command)
    {
        if (command.Length < 2 || command[0] != '/')
        {
            return false;
        }

        var spaceIndex = command.IndexOf(' ');
        var name = spaceIndex < 0 ? command[1..] : command[1..spaceIndex];
        var argument = spaceIndex < 0 ? string.Empty : command[(spaceIndex + 1)..].Trim();

        var toggle = CommandToggles.FirstOrDefault(
            entry => string.Equals(entry.Command, name, StringComparison.OrdinalIgnoreCase));
        if (toggle is null)
        {
            return false;
        }

        bool newValue;
        if (argument.Length == 0)
        {
            newValue = !toggle.Get();
        }
        else if (TryParseToggleArgument(argument, out var parsed))
        {
            newValue = parsed;
        }
        else
        {
            AddToast($"Użycie: /{toggle.Command} [start|stop]", "info");
            return true;
        }

        toggle.Set(newValue);
        EmitSystem($"{toggle.DisplayName}: {(newValue ? "włączone" : "wyłączone")}", 90);
        return true;
    }

    private static bool TryParseToggleArgument(string argument, out bool value)
    {
        switch (argument.ToLowerInvariant())
        {
            case "on":
            case "start":
            case "wlacz":
            case "włącz":
            case "1":
            case "tak":
                value = true;
                return true;
            case "off":
            case "stop":
            case "wylacz":
            case "wyłącz":
            case "0":
            case "nie":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    // ========================================================================
    // New command implementations
    // ========================================================================

    private void ExecuteExaminePerson(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && IsConnected)
        {
            _ = SendUiCommandAsync($"exa {name}");
        }
    }

    private void ExecuteKillPerson(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && IsConnected)
        {
            _ = SendUiCommandAsync($"kill {name}");
        }
    }

    private async Task SendUiCommandAsync(string command)
    {
        if (Map.IsMapEditorActive)
        {
            AddToast("Automatyczne i przyciskowe komendy są zablokowane podczas mapowania.", "info");
            return;
        }

        try
        {
            await _session.SendCommandAsync(command);
        }
        catch (Exception exception)
        {
            EmitSystem(exception.Message, 31);
        }
    }

    private bool CanExecuteLordGotoGroupRoom(GroupMember? member) =>
        LordModeEnabled && BuildLordGotoGroupRoomCommand(member) is not null;

    private void ExecuteLordGotoGroupRoom(GroupMember? member)
    {
        if (CanExecuteLordGotoGroupRoom(member) && BuildLordGotoGroupRoomCommand(member) is { } command)
        {
            QueueTriggeredCommands([command]);
        }
    }

    private bool CanExecuteLordGotoGroupMember(GroupMember? member) =>
        LordModeEnabled && BuildLordGotoGroupMemberCommand(member) is not null;

    private void ExecuteLordGotoGroupMember(GroupMember? member)
    {
        if (CanExecuteLordGotoGroupMember(member) && BuildLordGotoGroupMemberCommand(member) is { } command)
        {
            QueueTriggeredCommands([command]);
        }
    }

    internal static string? BuildLordGotoGroupRoomCommand(GroupMember? member) =>
        IsSafeVnum(member?.Room) ? $"walk {member!.Room}" : null;

    internal static string? BuildLordGotoGroupMemberCommand(GroupMember? member) =>
        IsSafeCharacterName(member?.Name) ? $"walk {member!.Name}" : null;

    private static bool IsSafeVnum(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(char.IsAsciiDigit);

    private static bool IsSafeCharacterName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsLetter(character) || character is '-' or '\'');

    /// <summary>Casts or uses <paramref name="shortcut"/>'s spell/skill on <paramref name="member"/>
    /// — invoked directly from GroupPanelView's code-behind Click handler. A two-argument per-row
    /// action like this has no ICommand-binding precedent in this codebase (see
    /// <see cref="BuildLordGotoGroupMemberCommand"/> for the equivalent single-argument case), so
    /// the view carries the member through via each button's Tag instead of a second
    /// CommandParameter.</summary>
    public void CastGroupSpellOnMember(GroupMember? member, GroupSpellShortcut? shortcut)
    {
        var matchedAbility = Killeropedia.FindAbilityByName(shortcut?.SpellName);
        if (BuildCastGroupSpellCommand(member, shortcut, matchedAbility) is { } command)
        {
            QueueTriggeredCommands([command]);
        }
    }

    /// <summary>Builds the command a group shortcut button sends. A spell always goes through
    /// "cast '&lt;name&gt;' &lt;target&gt;"; an active skill instead uses its own captured
    /// <see cref="AbilityCaptureEntry.Syntax"/> as the verb — several skills invoke under a
    /// different word than their "help" lookup name (e.g. "healing touch" is bare "touch", "call
    /// avatar" is bare "call"), so guessing from the shortcut's own name would misfire for those.
    /// Falls back to the "cast" form when <paramref name="matchedAbility"/> is null (name never
    /// captured via "/mapuj") or has no known syntax, matching this method's original
    /// spell-only behavior.</summary>
    internal static string? BuildCastGroupSpellCommand(
        GroupMember? member, GroupSpellShortcut? shortcut, AbilityCaptureEntry? matchedAbility)
    {
        if (!IsSafeCharacterName(member?.Name) || string.IsNullOrWhiteSpace(shortcut?.SpellName))
        {
            return null;
        }

        if (matchedAbility is { Type: { } type } && type.StartsWith("skill", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(matchedAbility.Syntax))
        {
            return $"{matchedAbility.Syntax.Trim()} {member!.Name}";
        }

        return $"cast \"{shortcut!.SpellName}\" {member!.Name}";
    }

    private bool CanExecuteAddGroupSpell() =>
        !string.IsNullOrWhiteSpace(NewGroupSpellLabel) && !string.IsNullOrWhiteSpace(NewGroupSpellName);

    private void ExecuteAddGroupSpell()
    {
        if (!CanExecuteAddGroupSpell())
        {
            return;
        }

        GroupSpells.Add(new GroupSpellShortcut
        {
            Label = NewGroupSpellLabel.Trim(),
            SpellName = NewGroupSpellName.Trim(),
        });
        NewGroupSpellLabel = string.Empty;
        NewGroupSpellName = string.Empty;
        SaveGroupSpells();
    }

    private void ExecuteRemoveGroupSpell(GroupSpellShortcut? shortcut)
    {
        if (shortcut is not null && GroupSpells.Remove(shortcut))
        {
            SaveGroupSpells();
        }
    }

    private static IReadOnlyList<GroupSpellShortcut> LoadGroupSpells(GroupSpellStore store)
    {
        try
        {
            return store.Load().Entries;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return [];
        }
    }

    private void SaveGroupSpells()
    {
        var document = new GroupSpellDocument { Entries = [.. GroupSpells] };
        _ = SaveGroupSpellsAsync(document);
    }

    private async Task SaveGroupSpellsAsync(GroupSpellDocument document)
    {
        try
        {
            await _groupSpellStore.SaveAsync(document);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            EmitSystem($"Nie udało się zapisać skrótów czarów drużyny: {exception.Message}", 31);
        }
    }

    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(NewNoteTitle))
        {
            return;
        }

        if (_editedNote is { } edited)
        {
            edited.Title = NewNoteTitle;
            edited.Content = NewNoteContent;
            edited.IsGlobal = NewNoteIsGlobal;
        }
        else
        {
            Notes.Insert(0, new NoteEntry
            {
                Title = NewNoteTitle,
                Content = NewNoteContent,
                CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
                IsGlobal = NewNoteIsGlobal,
            });
        }

        ClearNoteForm();
        SaveActiveProfile();
    }

    private void EditNote(NoteEntry? note)
    {
        if (note is null)
        {
            return;
        }

        _editedNote = note;
        NewNoteTitle = note.Title;
        NewNoteContent = note.Content;
        NewNoteIsGlobal = note.IsGlobal;
        IsNoteFormExpanded = true;
        NotifyNoteEditModeChanged();
    }

    private void CancelNoteEdit() => ClearNoteForm();

    private void ClearNoteForm()
    {
        _editedNote = null;
        NewNoteTitle = string.Empty;
        NewNoteContent = string.Empty;
        NewNoteIsGlobal = false;
        NotifyNoteEditModeChanged();
    }

    private void NotifyNoteEditModeChanged()
    {
        OnPropertyChanged(nameof(IsEditingNote));
        OnPropertyChanged(nameof(NoteFormButtonText));
        OnPropertyChanged(nameof(NoteFormHeader));
    }

    private void DeleteNote(NoteEntry? note)
    {
        if (note is null)
        {
            return;
        }

        if (ReferenceEquals(note, _editedNote))
        {
            ClearNoteForm();
        }

        Notes.Remove(note);
        SaveActiveProfile();
    }

    private void CopyToCommandBar(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            CommandText = text;
        }
    }

    private void ClearToasts()
    {
        Toasts.Clear();
    }

    public void ReportStartupError(Exception exception)
    {
        // Unwrap TargetInvocationException etc. so the dialog shows the real cause.
        var rootCause = exception.GetBaseException();
        StartupErrorMessage = "Nie udało się uruchomić interfejsu.";
        StartupErrorDetails = rootCause.Message;
        AddToast("Wystąpił błąd uruchamiania interfejsu.", "error");
        EmitSystem(rootCause.Message, 31);
    }

    public void ReportSettingsImportError(Exception exception)
    {
        var rootCause = exception.GetBaseException();
        StartupErrorMessage = "Nie udało się zastosować importu ustawień.";
        StartupErrorDetails = rootCause.Message;
        AddToast("Nie udało się zaimportować ustawień.", "error");
        EmitSystem($"Import ustawień: {rootCause.Message}", 31);
    }

    private void ClearStartupError()
    {
        StartupErrorMessage = null;
        StartupErrorDetails = null;
    }

    private async Task RetryStartupAsync()
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            ReportStartupError(exception);
        }
    }

    /// <summary>Not readonly — overridden via reflection in tests to avoid a real 30s wait.</summary>
    private static TimeSpan ToastLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Internal (not private) so MapViewModel can surface its own toasts (e.g. "Zgłoś
    /// znaczniki" results) through the same top-bar strip, via the
    /// <see cref="MapViewModel.MainViewModel"/> back-reference.</summary>
    internal void AddToast(string text, string type = "info")
    {
        // Newest goes last: the top-bar strip is right-aligned, so the latest
        // toast hugs the right edge and older ones get clipped on the left.
        var toast = new ToastMessage { Text = text, Type = type };
        Toasts.Add(toast);
        while (Toasts.Count > 10)
        {
            Toasts.RemoveAt(0);
        }

        _ = RemoveToastAfterDelayAsync(toast);
    }

    private async Task RemoveToastAfterDelayAsync(ToastMessage toast)
    {
        await Task.Delay(ToastLifetime);
        Dispatcher.UIThread.Post(() => Toasts.Remove(toast));
    }

    // ========================================================================
    // Session event handlers (preserved)
    // ========================================================================

    private void OnTextReceived(string text)
    {
        _bookCatalogRefreshCoordinator.ObserveText(text);
        _rareCatalogRefreshCoordinator.ObserveText(text);
        _abilityMappingCoordinator.ObserveText(text);
        _artifactTryMappingCoordinator.ObserveText(text);
        CollectSpellKnowledge(text);
        CollectSkillKnowledge(text);
        var toDisplay = _profileSettings.ShowNumericDamageEnabled ? AnnotateDamageLines(text) : text;
        toDisplay = _profileSettings.AnnotateRandomBookClassEnabled ? AnnotateBookClasses(toDisplay) : toDisplay;
        toDisplay = _profileSettings.AnnotateSkillTrainersEnabled ? AnnotateSkillTrainers(toDisplay) : toDisplay;
        toDisplay = _profileSettings.AnnotateSpellSourcesEnabled ? AnnotateSpellSources(toDisplay) : toDisplay;
        toDisplay = _profileSettings.AnnotateScoreEnabled ? AnnotateScoreLines(toDisplay) : toDisplay;
        toDisplay = AnnotateRoomVnum(toDisplay);
        Dispatcher.UIThread.Post(() => OutputReceived?.Invoke(toDisplay));
    }

    /// <summary>
    /// Splices " (N)" onto the end of any complete line in <paramref name="chunk"/> recognized by
    /// <see cref="DamagePhrases"/> — e.g. "Twoje miażdżące walnięcie dewastuje sędziwego
    /// krasnoluda. (44)". Runs on the raw incoming text (same layer as <see cref="LineAccumulator"/>
    /// inside MudSession) rather than on already-completed lines in the output buffer, since by the
    /// time a line is known to be complete its unmodified text has already been forwarded to the
    /// terminal — appending after the fact would only ever land on a new line below it, not the end
    /// of the same one. <see cref="_pendingDamageLine"/> carries a line's text across chunk
    /// boundaries the same way MudSession's own line accumulator does, since a single line can
    /// arrive split across multiple reads.
    /// </summary>
    private string AnnotateDamageLines(string chunk)
    {
        if (!chunk.Contains('\n'))
        {
            _pendingDamageLine += chunk;
            return chunk;
        }

        var segments = chunk.Split('\n');
        var output = new StringBuilder(chunk.Length + 8);

        var firstLine = (_pendingDamageLine + segments[0]).TrimEnd('\r');
        output.Append(segments[0].TrimEnd('\r'));
        AppendDamageSuffix(output, firstLine);
        output.Append('\n');

        for (var i = 1; i < segments.Length - 1; i++)
        {
            var line = segments[i].TrimEnd('\r');
            output.Append(line);
            AppendDamageSuffix(output, line);
            output.Append('\n');
        }

        _pendingDamageLine = segments[^1];
        output.Append(_pendingDamageLine);

        return output.ToString();
    }

    private static void AppendDamageSuffix(StringBuilder output, string line)
    {
        if (DamagePhrases.TryGetDamage(line, out var damage))
        {
            output.Append(" (").Append(damage).Append(')');
        }
    }

    /// <summary>
    /// Splices " (N-M)" onto the end of any complete "score" stat line in <paramref name="chunk"/>
    /// recognized by <see cref="ScorePhrases"/> — e.g. "Twoja sila jest srednia. (116-129)". Same
    /// raw-text/chunk-boundary approach as <see cref="AnnotateDamageLines"/>, for the same reason:
    /// by the time a line is known complete its unmodified text has already reached the terminal.
    /// </summary>
    private string AnnotateScoreLines(string chunk)
    {
        if (!chunk.Contains('\n'))
        {
            _pendingScoreLine += chunk;
            return chunk;
        }

        var segments = chunk.Split('\n');
        var output = new StringBuilder(chunk.Length + 8);

        var firstLine = (_pendingScoreLine + segments[0]).TrimEnd('\r');
        output.Append(segments[0].TrimEnd('\r'));
        AppendScoreSuffix(output, firstLine);
        output.Append('\n');

        for (var i = 1; i < segments.Length - 1; i++)
        {
            var line = segments[i].TrimEnd('\r');
            output.Append(line);
            AppendScoreSuffix(output, line);
            output.Append('\n');
        }

        _pendingScoreLine = segments[^1];
        output.Append(_pendingScoreLine);

        return output.ToString();
    }

    private static void AppendScoreSuffix(StringBuilder output, string line)
    {
        if (ScorePhrases.TryGetRange(line, out var range))
        {
            output.Append(" (").Append(range).Append(')');
        }
    }

    /// <summary>
    /// Splices " (Klasa)" right after a recognized random-book name (e.g. "duża księga triumfu
    /// (Paladyn)") — see <see cref="RandomBookNaming"/>. Unlike <see cref="AnnotateDamageLines"/>,
    /// which only ever appends at a line's end (position-independent), this inserts mid-line, so
    /// it only ever runs on complete, newline-terminated segments of a single chunk — never
    /// reconstructed across a chunk boundary, since that could try to splice text into content
    /// already flushed to the terminal by a previous call. This only misses the rare case of a
    /// book name split exactly across a socket read, which is an acceptable trade for never
    /// corrupting already-displayed output.
    /// </summary>
    private static string AnnotateBookClasses(string chunk)
    {
        if (!chunk.Contains('\n'))
        {
            return chunk;
        }

        var segments = chunk.Split('\n');
        var output = new StringBuilder(chunk.Length + 16);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            output.Append(RandomBookNaming.AnnotateClasses(segments[i].TrimEnd('\r')));
            output.Append('\n');
        }

        output.Append(segments[^1]);
        return output.ToString();
    }

    /// <summary>
    /// Splices " (Nauczyciel)" onto each row of the "skill" command's output — see
    /// <see cref="SkillTrainerAnnotator"/>. Same mid-line-splice/no-cross-chunk-state trade-off as
    /// <see cref="AnnotateBookClasses"/>.
    /// </summary>
    private string AnnotateSkillTrainers(string chunk)
    {
        if (!chunk.Contains('\n'))
        {
            return chunk;
        }

        var teachers = Map.TeacherCatalog;
        var segments = chunk.Split('\n');
        var output = new StringBuilder(chunk.Length + 16);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            output.Append(SkillTrainerAnnotator.Annotate(segments[i].TrimEnd('\r'), teachers));
            output.Append('\n');
        }

        output.Append(segments[^1]);
        return output.ToString();
    }

    /// <summary>
    /// Splices " (Moby)" onto each still-missing entry of the "spell" command's output — see
    /// <see cref="SpellSourceAnnotator"/>. Same mid-line-splice/no-cross-chunk-state trade-off as
    /// <see cref="AnnotateBookClasses"/>.
    /// </summary>
    private string AnnotateSpellSources(string chunk)
    {
        if (!chunk.Contains('\n'))
        {
            return chunk;
        }

        var spellMobs = Map.SpellMobCatalog;
        var segments = chunk.Split('\n');
        var output = new StringBuilder(chunk.Length + 16);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            output.Append(SpellSourceAnnotator.Annotate(segments[i].TrimEnd('\r'), spellMobs));
            output.Append('\n');
        }

        output.Append(segments[^1]);
        return output.ToString();
    }

    /// <summary>
    /// Parses any "spell"/"spell all" rows in <paramref name="chunk"/> (see
    /// <see cref="SpellKnowledgeParser"/>) and, if any are new or changed, persists them into
    /// <see cref="_knownSpells"/> and mirrors the result onto <see cref="Map"/> for the map's
    /// tooltip coloring. <paramref name="chunk"/> arrives on the network receive thread (same as
    /// the rest of <see cref="OnTextReceived"/>), so the actual mutation is posted to the UI
    /// thread — <see cref="_knownSpells"/> and <see cref="MapViewModel.SpellKnowledge"/> are both
    /// only ever touched from there.
    /// </summary>
    private void CollectSpellKnowledge(string chunk)
    {
        var entries = SpellKnowledgeParser.Parse(chunk);
        if (entries.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySpellKnowledge(entries));
    }

    private void ApplySpellKnowledge(IReadOnlyList<(string Name, bool Known)> entries)
    {
        var changed = false;
        foreach (var (name, known) in entries)
        {
            if (!_knownSpells.TryGetValue(name, out var existing) || existing != known)
            {
                _knownSpells[name] = known;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Map.SpellKnowledge = new Dictionary<string, bool>(_knownSpells, StringComparer.OrdinalIgnoreCase);
        SaveActiveProfile();
    }

    /// <summary>
    /// Parses any "skill" rows in <paramref name="chunk"/> (see <see cref="SkillKnowledgeParser"/>)
    /// and, if any are new or changed, persists them into <see cref="_knownSkills"/> and mirrors
    /// the result onto <see cref="Map"/> for the map's teacher-tooltip coloring. Same
    /// background-thread caveat as <see cref="CollectSpellKnowledge"/> — the mutation is posted to
    /// the UI thread.
    /// </summary>
    private void CollectSkillKnowledge(string chunk)
    {
        var entries = SkillKnowledgeParser.Parse(chunk);
        if (entries.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplySkillKnowledge(entries));
    }

    private void ApplySkillKnowledge(IReadOnlyList<(string Name, int Current)> entries)
    {
        var changed = false;
        foreach (var (name, current) in entries)
        {
            if (!_knownSkills.TryGetValue(name, out var existing) || existing != current)
            {
                _knownSkills[name] = current;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Map.SkillKnowledge = new Dictionary<string, int>(_knownSkills, StringComparer.OrdinalIgnoreCase);
        SaveActiveProfile();
    }

    private void OnLineReceived(string line)
    {
        // The creator-only book/rare refreshes and "/mapuj" own complete response lines while
        // active. Raw text still reaches the terminal through TextReceived, but their output must
        // not fire user triggers (only one can be capturing at a time — see the mutual
        // _bookRefreshCts/_rareRefreshCts/_mapujCts gating in
        // CanRefreshBookCatalog/CanRefreshRareCatalog/CanSendCommand).
        if (_bookCatalogRefreshCoordinator.TryCaptureLine(line)
            || _rareCatalogRefreshCoordinator.TryCaptureLine(line)
            || _abilityMappingCoordinator.TryCaptureLine(line)
            || _artifactTryMappingCoordinator.TryCaptureLine(line))
        {
            return;
        }

        if (ChatLinePolicy.IsCommunicationLine(line))
        {
            Dispatcher.UIThread.Post(() => ChatLineReceived?.Invoke(line));
            if (ChatSoundOnNewMessageEnabled)
            {
                PlayNotificationSound();
            }
        }

        if (IsDeathLine(line))
        {
            // Capture the position on the UI thread — Map state is UI-bound.
            Dispatcher.UIThread.Post(RecordDeath);
        }

        if (AutowalkRecoveryPolicy.IsLockedGateMessage(line))
        {
            Dispatcher.UIThread.Post(HandleLockedAutowalkGate);
        }

        if (CombatStatusPolicy.IsKnockedDownLine(line))
        {
            Dispatcher.UIThread.Post(TryAutostand);
        }

        if (CombatStatusPolicy.IsDisarmedLine(line))
        {
            Dispatcher.UIThread.Post(TryAutowield);
        }

        if (GroupOrdersEnabled
            && GroupOrderPolicy.TryGetCommand(
                line, _latestCharacterName, _latestGroupUpdate, out var orderedCommand))
        {
            QueueTriggeredCommands([orderedCommand]);
        }

        if (RemoteControlEnabled
            && RemoteCommandPolicy.TryGetCommand(line, RemoteControlCharacterName, out var remoteCommand))
        {
            QueueTriggeredCommands([remoteCommand]);
        }

        if (AutoRecastOnLeaderSnapEnabled
            && LeaderSnapPolicy.IsLeaderSnap(line, _latestCharacterName, _latestGroupUpdate))
        {
            var snapCommands = CommandStacker.Split(AutoRecastOnLeaderSnapCommandsText, CommandStackingSeparator);
            if (snapCommands.Count > 0)
            {
                QueueTriggeredCommands(snapCommands);
            }
        }

        var commands = _triggers.Evaluate(line, CommandStackingSeparator);
        if (commands.Count == 0)
        {
            return;
        }

        QueueTriggeredCommands(commands);
    }

    /// <summary>
    /// Records the latest GMCP position and pauses or recovers an active
    /// autowalk after combat or a knockdown. Runs on the network thread; the
    /// autowalk nudges are posted to the UI thread.
    /// </summary>
    private void UpdateCharacterPosition(string position)
    {
        var wasFighting = AutowalkRecoveryPolicy.IsCombatPosition(_latestCharacterPosition);
        var wasSitting = AutowalkRecoveryPolicy.IsSittingPosition(_latestCharacterPosition);
        var wasStanding = AutowalkRecoveryPolicy.IsStandingPosition(_latestCharacterPosition);
        var wasResting = AutowalkRecoveryPolicy.IsRestingPosition(_latestCharacterPosition);
        var wasLying = CombatStatusPolicy.IsLyingPosition(_latestCharacterPosition);
        var nowFighting = AutowalkRecoveryPolicy.IsCombatPosition(position);
        var nowSitting = AutowalkRecoveryPolicy.IsSittingPosition(position);
        var nowStanding = AutowalkRecoveryPolicy.IsStandingPosition(position);
        var nowResting = AutowalkRecoveryPolicy.IsRestingPosition(position);
        var nowLying = CombatStatusPolicy.IsLyingPosition(position);
        _latestCharacterPosition = position;

        if (nowFighting && !wasFighting)
        {
            OnAutowalkCombatStarted();
            _autoAssistNpcPending = true;
            TryAutoAssistNpcIfConfirmed();
            Dispatcher.UIThread.Post(TryAutoFarmCastSequence);
            Dispatcher.UIThread.Post(() => CombatStateChanged?.Invoke(true));
        }

        if (nowLying && !wasLying)
        {
            TryAutostand();
        }

        if (nowSitting && !wasSitting)
        {
            OnAutowalkSitting();
        }

        if (nowStanding && !wasStanding)
        {
            OnAutowalkStanding();
            TryAutoOrderGroupPosition("stand", _profileSettings.AutoStandOrderEnabled);
        }

        // "resting" (the "rest" command) is a distinct GMCP position from "sitting" ("sit") — the
        // group order is "rest", so it fires on this transition specifically, not on sitting down.
        if (nowResting && !wasResting)
        {
            TryAutoOrderGroupPosition("rest", _profileSettings.AutoRestOrderEnabled);
            OnAutowalkRestingStarted();
        }

        if (wasResting && !nowResting)
        {
            OnAutowalkRestingEnded();
        }

        if (wasFighting && !nowFighting)
        {
            _autoAssistNpcPending = false;
            Dispatcher.UIThread.Post(() => CombatStateChanged?.Invoke(false));
        }

        if (wasFighting && !nowFighting && !nowSitting)
        {
            OnAutowalkCombatEnded();
        }
    }

    /// <summary>Marks an active walk as paused so it can be resumed once the fight is over.</summary>
    private void OnAutowalkCombatStarted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_autowalkPath is null || _autowalkStep >= _autowalkPath.Steps.Count)
            {
                return;
            }

            _autowalkPausedForCombat = true;
            AutowalkStatusText = $"Walka — autowalk wstrzymany (cel „{_autowalkTargetName}”).";
        });
    }

    /// <summary>
    /// Resumes a walk that a fight put on hold. The walk stalled because no room
    /// change arrived during combat, so the pending step is re-sent.
    /// </summary>
    private void OnAutowalkCombatEnded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_autowalkPausedForCombat || _autowalkPath is null ||
                _autowalkStep >= _autowalkPath.Steps.Count)
            {
                return;
            }

            _autowalkPausedForCombat = false;
            AutowalkStatusText = $"Walka skończona — wracam na trasę do „{_autowalkTargetName}”.";
            if (!AutowalkRecoveryPolicy.IsStandingPosition(_latestCharacterPosition))
            {
                _ = SendTriggeredCommandAsync("stand");
            }

            SendAutowalkStep();
        });
    }

    private void OnAutowalkSitting()
    {
        Dispatcher.UIThread.Post(BeginAutowalkStandRecovery);
    }

    /// <summary>The player consciously rested mid-route — pause the walk rather than let the
    /// stuck-step backstop mistake the stall for a blocked door (see
    /// <see cref="_autowalkPausedForResting"/>). Runs on the network thread; posts the actual
    /// state change to the UI thread like the sitting/combat handlers above.</summary>
    private void OnAutowalkRestingStarted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_autowalkPath is null || _autowalkStep >= _autowalkPath.Steps.Count)
            {
                return;
            }

            _autowalkPausedForResting = true;
            AutowalkStatusText = $"Odpoczywasz — autowalk wstrzymany (cel „{_autowalkTargetName}”).";
        });
    }

    /// <summary>Resumes a walk the player paused by resting. The step that was in flight never
    /// advanced (resting doesn't move the character), so it's simply resent — SendAutowalkStep
    /// re-checks the live position itself, so this is safe even if GMCP reports something other
    /// than fully "standing" by the time this fires.</summary>
    private void OnAutowalkRestingEnded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_autowalkPausedForResting || _autowalkPath is null ||
                _autowalkStep >= _autowalkPath.Steps.Count)
            {
                return;
            }

            _autowalkPausedForResting = false;
            AutowalkStatusText = $"Wracam na trasę do „{_autowalkTargetName}”.";
            SendAutowalkStep();
        });
    }

    private void OnAutowalkStanding()
    {
        Dispatcher.UIThread.Post(HandleAutowalkStanding);
    }

    private void HandleAutowalkStanding()
    {
        if (!_autowalkRecoveringPosition || _autowalkPath is null ||
            _autowalkStep >= _autowalkPath.Steps.Count)
        {
            return;
        }

        _autowalkRecoveringPosition = false;
        _autowalkPausedForCombat = false;
        AutowalkStatusText = $"Postać wstała — wracam na trasę do „{_autowalkTargetName}”.";

        if (_profileSettings.AutoStandOrderEnabled)
        {
            // The same standing transition also queues "order <name> stand" to the group (see
            // UpdateCharacterPosition/TryAutoOrderGroupPosition) — that's only queued, not
            // confirmed, so resuming the walk immediately could move the leader into the next
            // room before followers have even started standing, leaving them behind in a
            // different vnum. Give it a couple of seconds before moving on.
            _ = ResumeAutowalkAfterGroupStandOrderAsync(_autowalkCts.Token);
        }
        else
        {
            SendAutowalkStep();
        }
    }

    private async Task ResumeAutowalkAfterGroupStandOrderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested && _autowalkPath is not null)
                {
                    SendAutowalkStep();
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping or replacing the autowalk also cancels this delay.
        }
    }

    /// <summary>Set once <see cref="AutoAssistPolicy.ShouldAssist"/> fires and cleared once a
    /// command is actually sent (or the fight resolves without one) — decouples "an assist
    /// situation started" (ShouldAssist's one-shot signal) from "we have everything needed to
    /// send the configured command", since a "{cel}" template may need to wait for Room.People to
    /// deliver the enemy name a moment after Char.Group already reported the fight. Without this,
    /// a command needing "{cel}" would silently never send at all whenever that race lost — the
    /// one shot would already be spent by the time the enemy name arrived.</summary>
    private bool _autoAssistCommandPending;

    private void TryAutoAssist()
    {
        if (_autoAssist.ShouldAssist(
                AutoAssistEnabled && IsConnected,
                Map.CurrentVnum,
                _latestCharacterName,
                string.Equals(_latestCharacterPosition, "fighting", StringComparison.OrdinalIgnoreCase),
                _latestGroupUpdate,
                _latestRoomPeople,
                _profileSettings.AutoAssistExcludedMobNames))
        {
            _autoAssistCommandPending = true;
        }

        if (!_autoAssistCommandPending)
        {
            return;
        }

        if (!AutoAssistEnabled || !IsConnected)
        {
            _autoAssistCommandPending = false;
            return;
        }

        var (isFighting, enemyName) = AutoAssistPolicy.FindFightingEnemyName(
            Map.CurrentVnum,
            _latestCharacterName,
            _latestGroupUpdate,
            _latestRoomPeople);

        if (!isFighting)
        {
            // The group member stopped fighting (or left the room) before Room.People ever
            // delivered an enemy name — nothing left to assist into this fight.
            _autoAssistCommandPending = false;
            return;
        }

        var commands = BuildAutoAssistCommands(
            _profileSettings.AutoAssistCommandTemplate,
            enemyName,
            _profileSettings.AutoAssistFollowUpCommands,
            CommandStackingSeparator);
        if (commands.Count == 0)
        {
            // Needs "{cel}" but Room.People hasn't delivered the enemy name yet — stay pending;
            // TryAutoAssist runs again on the next Char.Group/Room.People update.
            return;
        }

        _autoAssistCommandPending = false;
        QueueTriggeredCommands(commands);
    }

    /// <summary>Builds the command(s) autoassist sends. <paramref name="commandTemplate"/> is sent
    /// as-is unless it contains "{cel}", in which case that token is replaced with
    /// <paramref name="enemyName"/> — e.g. "charge {cel}" becomes "charge Wielki smok". A template
    /// that needs "{cel}" but has no enemy name to substitute yet is skipped (returns an empty
    /// list) rather than sending a broken command with a literal "{cel}" in it — see
    /// <see cref="TryAutoAssist"/> and <see cref="_autoAssistCommandPending"/> for how the caller
    /// retries once the name does arrive.</summary>
    internal static IReadOnlyList<string> BuildAutoAssistCommands(
        string? commandTemplate,
        string? enemyName,
        string? followUpCommands,
        string? separator)
    {
        var template = string.IsNullOrWhiteSpace(commandTemplate) ? "as" : commandTemplate.Trim();
        var needsTarget = template.Contains("{cel}", StringComparison.OrdinalIgnoreCase);
        if (needsTarget && string.IsNullOrWhiteSpace(enemyName))
        {
            return [];
        }

        var command = needsTarget
            ? template.Replace("{cel}", enemyName, StringComparison.OrdinalIgnoreCase)
            : template;
        var commands = new List<string> { command };
        commands.AddRange(CommandStacker.Split(followUpCommands, separator));
        return commands;
    }

    private void QueueTriggeredCommands(IReadOnlyList<string> commands)
    {
        Task task;
        lock (_triggerTasksLock)
        {
            // Reject new work if the view-model is shutting down.
            // This check + task creation + registration are all inside
            // the same critical section that DisposeAsync uses to flip
            // _acceptingTriggerTasks, so no task can be started after
            // DisposeAsync has already drained and disposed the semaphore.
            if (!_acceptingTriggerTasks)
            {
                return;
            }

            // Capture the current tail of the FIFO chain.  The new task
            // will await this previous batch (swallowing its faults) so
            // that batches are sent strictly in receive order.
            var previous = _triggerQueueTail;

            // Create the new batch task and register it as the new tail.
            // EnqueueBatchAsync yields immediately so the lock is held
            // only for the duration of the synchronous preamble.
            task = EnqueueBatchAsync(previous, commands);
            _triggerQueueTail = task;
            _triggerTasks.Add(task);
        }

        // Fire-and-forget continuation that removes the task from the
        // tracking list once it completes, preventing unbounded growth
        // of _triggerTasks during normal operation.
        _ = RemoveWhenCompleted(task);
    }

    private void HandleLockedAutowalkGate()
    {
        if (_autowalkPath is null || _autowalkWaitingForGate ||
            _autowalkOpeningStep != _autowalkStep)
        {
            return;
        }

        _autowalkWaitingForGate = true;
        _autowalkOpeningStep = null;
        _autowalkGateCommandsSent = false;
        _autowalkGateIsOpen = false;
        AutowalkStatusText = "Brama zamknięta — próbuję ją uruchomić i czekam na GMCP.";
        _ = SendGateCommandsAsync(_autowalkCts.Token);
    }

    private async Task SendGateCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var command in AutowalkRecoveryPolicy.GetGateOpeningCommands())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendTriggeredCommandAsync(command, cancellationToken);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || !_autowalkWaitingForGate)
                {
                    return;
                }

                _autowalkGateCommandsSent = true;
                TryContinueThroughOpenedGate();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The autowalk was stopped while the gate sequence was being sent.
        }
    }

    /// <summary>
    /// Awaits <paramref name="task"/> and removes it from
    /// <see cref="_triggerTasks"/> under lock when it completes (or faults,
    /// or is cancelled).  All exceptions are swallowed — trigger-command
    /// errors are already logged inside <see cref="SendTriggeredCommandAsync"/>,
    /// and <see cref="OperationCanceledException"/> is expected during
    /// disposal shutdown.
    /// </summary>
    private async Task RemoveWhenCompleted(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Swallow all exceptions (see xmldoc above).
        }

        lock (_triggerTasksLock)
        {
            _triggerTasks.Remove(task);
        }
    }

    /// <summary>
    /// Awaits <paramref name="previous"/> (the prior batch in the FIFO
    /// chain) and then sends <paramref name="commands"/>.  Exceptions
    /// from the previous task are swallowed so a faulted batch never
    /// stalls later batches.  The semaphore inside
    /// <see cref="SendTriggeredCommandsAsync"/> provides an additional
    /// layer of non-interleaving protection (belt-and-suspenders).
    /// </summary>
    private async Task EnqueueBatchAsync(Task previous, IReadOnlyList<string> commands)
    {
        // Yield immediately so the caller's lock is released and this
        // method returns a Task to the caller.  The continuation runs
        // on a thread-pool thread (the caller fires from the network
        // receive loop, which has no SynchronizationContext).
        await Task.Yield();

        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Swallow all exceptions from the prior batch so the FIFO
            // chain continues.  Individual command errors are already
            // logged inside SendTriggeredCommandAsync, and cancellation
            // of the current batch will be observed in its own
            // SendTriggeredCommandsAsync call below.
        }

        await SendTriggeredCommandsAsync(commands);
    }

    private async Task SendTriggeredCommandsAsync(IReadOnlyList<string> commands)
    {
        await _triggerSendLock.WaitAsync(_triggerCts.Token);
        try
        {
            foreach (var command in commands)
            {
                await SendTriggeredCommandAsync(command);
            }
        }
        finally
        {
            _triggerSendLock.Release();
        }
    }

    private async Task SendTriggeredCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        if (Map.IsMapEditorActive)
        {
            return;
        }

        // "/recast" is a client-side meta-command (see the matching check in
        // SendCurrentCommandAsync) that expands to "cast <buff> self" per missing buff — the MUD
        // itself has no such command. Any automation that can produce it as a literal string
        // (timers, triggers, alias replacements, and AutoRecastOnLeaderSnapCommandsText) funnels
        // through here, so it must be intercepted here too, or it gets sent to the server as raw
        // text and rejected (e.g. "nie ma tutaj tej osoby").
        if (string.Equals(command, "/recast", StringComparison.OrdinalIgnoreCase))
        {
            await RecastMissingBuffsAsync();
            return;
        }

        // "/reconnect" is the same kind of client-side meta-command as "/recast" above — see
        // ReconnectCurrentProfileAsync. Automation (timers/triggers/aliases) reaches it here.
        if (string.Equals(command, "/reconnect", StringComparison.OrdinalIgnoreCase))
        {
            await ReconnectCurrentProfileAsync();
            return;
        }

        Dispatcher.UIThread.Post(() => EmitCommandEcho(command));

        try
        {
            await _session.SendCommandAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => EmitSystem(exception.Message, 31));
        }
    }

    private void OnGmcpReceived(GmcpMessage message)
    {
        // Exits must be parsed before the location resolver fires
        // LocationChanged, so autowalk sees the new room's doors.
        _roomSnapshots.Process(message);
        _roomExits.Process(message);
        _locationResolver.Process(message);
        _characterState.Process(message);
        _worldState.Process(message);
        _skillTimeouts.Process(message);

        Dispatcher.UIThread.Post(() =>
        {
            GmcpMessages.Insert(0, new GmcpEntryViewModel(
                message.Package,
                string.IsNullOrWhiteSpace(message.Json) ? "(bez danych)" : message.Json,
                DateTimeOffset.Now.ToString("HH:mm:ss")));

            while (GmcpMessages.Count > 100)
            {
                GmcpMessages.RemoveAt(GmcpMessages.Count - 1);
            }
        });
    }

    private void OnCharacterVitalsChanged(CharacterVitalsUpdate update)
    {
        if (update.Mv is { } movement) _latestMovement = movement;
        if (update.MaxMv is { } maximumMovement) _latestMaximumMovement = maximumMovement;
        if (update.Hp is { } hpValue) _latestHp = hpValue;
        if (update.MaxHp is { } maxHpValue) _latestMaxHp = maxHpValue;
        if (update.Name is { } name) _latestCharacterName = name;
        if (update.Position is { } position) UpdateCharacterPosition(position);
        TryAutoAssist();

        Dispatcher.UIThread.Post(() =>
        {
            if (update.Hp is { } hp) Vitals.HitPoints = hp;
            if (update.MaxHp is { } maxHp) Vitals.MaxHitPoints = maxHp;
            if (update.Mv is { } mv) Vitals.EndurancePoints = mv;
            if (update.MaxMv is { } maxMv) Vitals.MaxEndurancePoints = maxMv;
            if (update.Level is { } level)
            {
                Vitals.Level = level;
                Killeropedia.SetCharacterLevel(level);
            }
            if (update.Name is { } name) Vitals.Name = name;
            if (update.Sex is { } sex) Vitals.SexDisplay = TranslateSex(sex);
            if (update.Position is { } position) Vitals.PositionDisplay = TranslatePosition(position);

            if (update.Mem is { } mem)
            {
                Vitals.SpellPoints = mem;
                if (mem > Vitals.MaxSpellPoints)
                {
                    Vitals.MaxSpellPoints = mem;
                }
            }

            // Runs here (not right after _latestHp/_latestMaxHp are set above) because it reads
            // _lastSkillTimeouts, which — like SkillsOnCooldown — is only ever touched on the UI
            // thread (see OnSkillTimeoutsChanged).
            TryAutoFarmCombatHeal();
        });
    }

    /// <summary>Reacts to every single Char.Vitals update while auto-farm is running, not just
    /// room arrivals (see <see cref="ContinueAutoFarm"/>) — lets a heal spell fire mid-fight the
    /// moment HP drops below <see cref="_autoFarmHpThresholdPercent"/>, instead of only after the
    /// farm finishes walking to its next room. Memorizing/resting stay the room-arrival flow's
    /// job (see <see cref="HealthRecoveryPolicy.ShouldCastCombatHeal"/>'s xmldoc for why).</summary>
    private void TryAutoFarmCombatHeal()
    {
        var now = DateTimeOffset.UtcNow;
        var (shouldCast, spellName) = HealthRecoveryPolicy.ShouldCastCombatHeal(
            _autoFarmActive,
            _latestHp,
            _latestMaxHp,
            _autoFarmHpThresholdPercent,
            _autoFarmHealSpellNames,
            _latestMemorizedSpells,
            _lastSkillTimeouts,
            now,
            _lastAutoFarmCombatHealCastAt);

        if (!shouldCast)
        {
            return;
        }

        _lastAutoFarmCombatHealCastAt = now;
        QueueTriggeredCommands([$"cast \"{spellName}\" self"]);
    }

    private void OnWorldTimeChanged(WorldTimeUpdate update)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (update.Day is { } day) WorldTime.Day = day;
            if (update.DayName is { } dayName) WorldTime.DayName = dayName;
            if (update.Era is { } era) WorldTime.Era = era;
            if (update.Month is { } month) WorldTime.Month = month;
            if (update.Time is { } time) WorldTime.Time = time;
            if (update.TimeName is { } timeName) WorldTime.TimeName = timeName;
            if (update.Year is { } year) WorldTime.Year = year;
        });
    }

    private void OnWorldWeatherChanged(WorldWeatherUpdate update)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (update.Sky is { } sky) WorldTime.Sky = sky;
            if (update.Wind is { } wind) WorldTime.Wind = wind;
        });
    }

    /// <summary>
    /// Char.Skills.Timeout reports the current snapshot of skills on cooldown. Only skills
    /// currently unusable are surfaced (SkillsOnCooldown, "* skillname") — there is no separate
    /// "ready again" notice, since a skill simply disappearing from that list already says it can
    /// be used again.
    /// </summary>
    private void OnSkillTimeoutsChanged(IReadOnlyList<SkillTimeoutEntry> entries)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                seen.Add(entry.Name);
                _lastSkillTimeouts[entry.Name] = entry.Timeout;
                SetSkillOnCooldown(entry.Name, entry.Timeout);
            }

            foreach (var name in _lastSkillTimeouts.Keys.ToList())
            {
                if (seen.Contains(name) || !_lastSkillTimeouts[name])
                {
                    continue;
                }

                _lastSkillTimeouts.Remove(name);
                SetSkillOnCooldown(name, onCooldown: false);
            }
        });
    }

    private void SetSkillOnCooldown(string skillName, bool onCooldown)
    {
        var index = -1;
        for (var i = 0; i < SkillsOnCooldown.Count; i++)
        {
            if (string.Equals(SkillsOnCooldown[i], skillName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (onCooldown)
        {
            if (index < 0)
            {
                SkillsOnCooldown.Add(skillName);
            }
        }
        else if (index >= 0)
        {
            SkillsOnCooldown.RemoveAt(index);
        }
    }

    private void OnCharacterConditionChanged(CharacterConditionUpdate update)
    {
        if (update.Position is { } position)
        {
            UpdateCharacterPosition(position);
        }

        TryAutoAssist();

        Dispatcher.UIThread.Post(() =>
        {
            if (update.Position is { } position)
            {
                Vitals.PositionDisplay = TranslatePosition(position);
            }

            Conditions.Clear();
            foreach (var (flag, active) in update.Flags)
            {
                if (active)
                {
                    Conditions.Add(TranslateCondition(flag));
                }
            }
        });
    }

    private void OnCharacterAffectsChanged(IReadOnlyList<CharacterAffect> affects)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Effects.Clear();
            _activeAffectNames.Clear();
            foreach (var affect in affects)
            {
                Effects.Add(StatusEffect.FromCore(affect));
                _activeAffectNames.Add(BuffWatchEntry.NormalizeName(affect.Name));
            }

            foreach (var buff in BuffSets.SelectMany(set => set.Buffs))
            {
                buff.IsActive = _activeAffectNames.Contains(BuffWatchEntry.NormalizeName(buff.Name));
            }

            RefreshBuffIndicators();
        });
    }

    private void OnRoomPeopleChanged(IReadOnlyList<RoomPerson> people)
    {
        _latestRoomPeople = people.ToArray();
        _autoKillRoomPeopleGeneration = _roomEntryGeneration;
        TryAutoAssist();
        TryAutoAssistNpcIfConfirmed();
        TryAutoKillIfConfirmed();

        Dispatcher.UIThread.Post(() =>
        {
            People.Clear();
            foreach (var person in people)
            {
                var isSelf = string.Equals(person.Name, _latestCharacterName, StringComparison.OrdinalIgnoreCase);
                People.Add(new PersonEntry(person.Name, person.IsFighting, person.Enemy, isSelf));
            }
        });
    }

    private void OnGroupChanged(CharacterGroupUpdate update)
    {
        _latestGroupUpdate = update;
        TryAutoAssist();
        TryAutoOrderExhaustedGroupRefresh(update);
        TryAutoMirrorLeaderPosition(update);
        Dispatcher.UIThread.Post(() =>
        {
            GroupEmptyMessage = string.IsNullOrWhiteSpace(update.UnavailableReason)
                ? "Brak członków drużyny."
                : update.UnavailableReason;
            OnPropertyChanged(nameof(GroupEmptyMessage));
            Map.UpdateGroupMembers(update.Members, _latestCharacterName);
            RefreshVisibleGroup(update);
            TryAutoFollowLeader(update);
        });
    }

    /// <summary>
    /// For a non-leader group member: as soon as GMCP's own Char.Group reports the leader in a
    /// different room than this character, starts the same walk "/walk leader" would. No
    /// coordination with the leader's own client is needed — this character already receives the
    /// leader's current room via its own GMCP group feed, so there's nothing to relay and no race
    /// with the leader's next move (unlike ordering the leader's client to notify this one).
    /// </summary>
    private void TryAutoFollowLeader(CharacterGroupUpdate update)
    {
        if (!ShouldAutoFollowLeader(
                AutoFollowLeaderEnabled, IsConnected, IsAutowalking, _latestCharacterPosition,
                update, _latestCharacterName, Map.CurrentVnum, out var leader) ||
            leader is null)
        {
            return;
        }

        if (BuildGroupMemberAutowalkTarget(leader) is { } target)
        {
            StartAutowalk(target);
        }
    }

    /// <summary>Pure decision behind <see cref="TryAutoFollowLeader"/>: true only for a non-leader
    /// group member, connected and not already autowalking or fighting, whose own room (
    /// <paramref name="currentVnum"/>) differs from the GMCP-reported leader's — the same
    /// condition "/walk leader" already resolves via <see cref="BuildGroupMemberAutowalkTarget"/>,
    /// just checked automatically instead of on a manual command.</summary>
    internal static bool ShouldAutoFollowLeader(
        bool enabled,
        bool isConnected,
        bool isAutowalking,
        string? position,
        CharacterGroupUpdate? update,
        string? selfName,
        string? currentVnum,
        out CharacterGroupMember? leader)
    {
        leader = null;

        if (!enabled || !isConnected || isAutowalking || update is null)
        {
            return false;
        }

        if (string.Equals(update.Leader, selfName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AutowalkRecoveryPolicy.IsCombatPosition(position))
        {
            return false;
        }

        var candidate = update.Members.FirstOrDefault(member => member.IsLeader);
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Room) ||
            string.IsNullOrWhiteSpace(currentVnum) ||
            string.Equals(currentVnum, candidate.Room, StringComparison.Ordinal))
        {
            return false;
        }

        leader = candidate;
        return true;
    }

    /// <summary>For a non-leader group member: mirrors the GMCP-reported leader's stand/sit/rest
    /// state, so an automated follower keeps pace with a leader that isn't this client (e.g. a
    /// real person playing the lead character) without needing an explicit "order ... stand/rest"
    /// from them. See <see cref="ShouldMirrorLeaderPosition"/> for the exact decision.</summary>
    private void TryAutoMirrorLeaderPosition(CharacterGroupUpdate update)
    {
        if (!ShouldMirrorLeaderPosition(
                AutoMirrorLeaderPositionEnabled, IsConnected, IsAutowalking, _latestCharacterPosition,
                update, _latestCharacterName, out var command) ||
            command is null)
        {
            return;
        }

        QueueTriggeredCommands([command]);
    }

    /// <summary>Pure decision behind <see cref="TryAutoMirrorLeaderPosition"/>: true only for a
    /// non-leader group member, connected, not mid-autowalk and not fighting/lying, whose own
    /// stand/sit/rest state doesn't already match the GMCP-reported leader's — <paramref
    /// name="command"/> is the exact command to send ("stand"/"sit"/"rest") when it returns
    /// true, matching whichever of those three the leader's own position maps to (any other
    /// leader position, e.g. fighting, is left alone — there's nothing sensible to mirror).</summary>
    internal static bool ShouldMirrorLeaderPosition(
        bool enabled,
        bool isConnected,
        bool isAutowalking,
        string? position,
        CharacterGroupUpdate? update,
        string? selfName,
        out string? command)
    {
        command = null;

        if (!enabled || !isConnected || isAutowalking || update is null)
        {
            return false;
        }

        if (string.Equals(update.Leader, selfName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AutowalkRecoveryPolicy.IsCombatPosition(position) || CombatStatusPolicy.IsLyingPosition(position))
        {
            return false;
        }

        var leader = update.Members.FirstOrDefault(member => member.IsLeader);
        if (leader is null)
        {
            return false;
        }

        if (AutowalkRecoveryPolicy.IsStandingPosition(leader.Position) && !AutowalkRecoveryPolicy.IsStandingPosition(position))
        {
            command = "stand";
        }
        else if (AutowalkRecoveryPolicy.IsRestingPosition(leader.Position) && !AutowalkRecoveryPolicy.IsRestingPosition(position))
        {
            command = "rest";
        }
        else if (AutowalkRecoveryPolicy.IsSittingPosition(leader.Position) && !AutowalkRecoveryPolicy.IsSittingPosition(position))
        {
            command = "sit";
        }

        return command is not null;
    }

    internal void SetGroupContextMenuOpen(bool isOpen)
    {
        if (_isGroupContextMenuOpen == isOpen)
        {
            return;
        }

        _isGroupContextMenuOpen = isOpen;
        if (!isOpen && _latestGroupUpdate is { } update)
        {
            RefreshVisibleGroup(update);
        }
    }

    /// <summary>Updates <see cref="Group"/> in place instead of Clear()+Add()ing every member on
    /// every single Char.Group GMCP update (which fires on essentially any state change for any
    /// member — HP ticking in combat, position changes, etc., often multiple times a second). A
    /// full rebuild tears down and recreates every member row's visual container, including the
    /// per-member spell-shortcut buttons — landing between a pointer-press and pointer-release
    /// silently drops the click (the release lands on a brand-new button instance that never saw
    /// the press). Skipping unchanged entries (<see cref="GroupMember"/> is a record, so
    /// value-equality is free) keeps their containers completely untouched; only a member whose
    /// data actually changed gets replaced, and only that one row is affected.</summary>
    internal void RefreshVisibleGroup(CharacterGroupUpdate update)
    {
        if (_isGroupContextMenuOpen)
        {
            return;
        }

        var index = 0;
        foreach (var member in OrderGroupMembers(update.Members, _latestCharacterName))
        {
            var isSelf = IsOwnCharacter(member, _latestCharacterName);
            var roomDisplay = ResolveRoomDisplay(member.Room);
            var updated = GroupMember.FromCore(member, roomDisplay, isSelf);

            if (index < Group.Count)
            {
                if (Group[index] != updated)
                {
                    Group[index] = updated;
                }
            }
            else
            {
                Group.Add(updated);
            }

            index++;
        }

        while (Group.Count > index)
        {
            Group.RemoveAt(Group.Count - 1);
        }
    }

    /// <summary>Alphabetical by name, except: the player's own character always comes first (so a
    /// group spell shortcut can target yourself), and NPCs/charms/summoned monsters always come
    /// last.</summary>
    internal static IEnumerable<CharacterGroupMember> OrderGroupMembers(
        IReadOnlyList<CharacterGroupMember> members, string? ownCharacterName) =>
        members
            .OrderBy(member => IsOwnCharacter(member, ownCharacterName) ? 0 : member.IsNpc ? 2 : 1)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase);

    private static bool IsOwnCharacter(CharacterGroupMember member, string? ownCharacterName) =>
        string.Equals(member.Name, ownCharacterName, StringComparison.OrdinalIgnoreCase);

    private void OnMemSpellsChanged(IReadOnlyList<MemorizedSpell> spells)
    {
        _latestMemorizedSpells = spells.ToArray();

        Dispatcher.UIThread.Post(() =>
        {
            MemSpells.Clear();
            foreach (var circle in MemSpellCircle.FromCore(spells))
            {
                MemSpells.Add(circle);
            }

            MemorizedSpellNames = spells
                .Where(spell => spell.Memed)
                .Select(spell => spell.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(MemorizedSpellNames));
        });
    }

    /// <summary>
    /// Resolves a raw room vnum to a display string.
    /// Uses the loaded map room name when available, falls back to "pokój {vnum}",
    /// or "?" when there is no room value at all.
    /// </summary>
    private string ResolveRoomDisplay(string? room)
    {
        if (room is null)
        {
            return "?";
        }

        var mapRoom = Map.MapIndex?.FindFirstRoomByVnum(room);
        var mapName = mapRoom?.Name?.Trim();
        if (!string.IsNullOrEmpty(mapName))
        {
            return mapName;
        }

        return $"pokój {room}";
    }

    /// <summary>
    /// Rebuilds the Group collection when MapIndex becomes available after map loading,
    /// so that entries that previously showed "pokój xxx" switch to resolved room names.
    /// </summary>
    private void OnMapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.MapEditorStatus))
        {
            var status = Map.MapEditorStatus;
            if (!string.Equals(status, _lastReportedMapEditorStatus, StringComparison.Ordinal))
            {
                _lastReportedMapEditorStatus = status;
                EmitSystem($"Mapper: {status}", 36);
            }
        }

        if (e.PropertyName == nameof(MapViewModel.MapIndex) && _latestGroupUpdate is not null)
        {
            var update = _latestGroupUpdate;
            Dispatcher.UIThread.Post(() =>
            {
                Map.UpdateGroupMembers(update.Members, _latestCharacterName);
                RefreshVisibleGroup(update);
            });
        }
    }

    private static string TranslateSex(string sex) => sex.ToUpperInvariant() switch
    {
        "M" => "Mężczyzna",
        "F" or "K" => "Kobieta",
        _ => sex,
    };

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

    private void OnGmcpSent(GmcpMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SentGmcpMessages.Insert(0, new GmcpEntryViewModel(
                message.Package,
                string.IsNullOrWhiteSpace(message.Json) ? "(bez danych)" : message.Json,
                DateTimeOffset.Now.ToString("HH:mm:ss")));

            while (SentGmcpMessages.Count > 100)
            {
                SentGmcpMessages.RemoveAt(SentGmcpMessages.Count - 1);
            }
        });
    }

    private void OnCommandSent(string _)
    {
        Interlocked.Exchange(ref _lastCommandSentTimestamp, Stopwatch.GetTimestamp());
        Dispatcher.UIThread.Post(RefreshIdleTime);
    }

    private void OnStatusChanged(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = status;
        });
    }

    private void OnConnectionClosed()
    {
        _bookRefreshCts?.Cancel();
        _rareRefreshCts?.Cancel();
        _mapujCts?.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            Map.StopMapEditor(
                "Mapowanie zatrzymane po utracie połączenia. Po ponownym połączeniu uruchom je ręcznie.");
            ClearLiveGroupState();
            StopAutoFarm("Farma zatrzymana: utracono połączenie.");
        });
    }

    private void OnConnectionError(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            Map.StopMapEditor(
                "Mapowanie zatrzymane po błędzie połączenia. Po ponownym połączeniu uruchom je ręcznie.");
            ClearLiveGroupState();
            StopAutoFarm("Farma zatrzymana: błąd połączenia.");
            EmitSystem(exception.Message, 31);
        });
    }

    private void ClearLiveGroupState()
    {
        _latestGroupUpdate = null;
        Group.Clear();
        Map.UpdateGroupMembers([], _latestCharacterName);
        GroupEmptyMessage = "Brak członków drużyny.";
        OnPropertyChanged(nameof(GroupEmptyMessage));
    }

    private void EmitSystem(string text, int ansiColor)
    {
        OutputReceived?.Invoke($"\u001b[{ansiColor}m{text}\u001b[0m\n");
    }

    // Manual/alias, trigger and timer paths use the same terminal echo so automated
    // commands remain visible even when the MUD does not echo client input.
    private void EmitCommandEcho(string command) => EmitSystem($"> {command}", 90);

    private bool CanRefreshBookCatalog() =>
        DeveloperFeatures.EnableBookCatalogRefreshButton
        && IsConnected
        && _bookRefreshCts is null
        && _rareRefreshCts is null
        && _mapujCts is null;

    private async Task RefreshBookCatalogAsync()
    {
        if (!CanRefreshBookCatalog())
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _bookRefreshCts = cancellation;
        _sendCommandCommand.NotifyCanExecuteChanged();
        Killeropedia.BeginBookRefresh();
        var lockTaken = false;

        try
        {
            await _triggerSendLock.WaitAsync(cancellation.Token);
            lockTaken = true;
            var progress = new Progress<BookCatalogRefreshProgress>(Killeropedia.ReportBookRefresh);
            var catalog = await _bookCatalogRefreshCoordinator.RefreshAsync(
                SendBookCatalogCommandAsync,
                progress,
                cancellation.Token);
            await _bookCatalogStore.SaveAsync(catalog, cancellation.Token);
            Killeropedia.CompleteBookRefresh(catalog);
            AddToast($"Odświeżono katalog ksiąg ({catalog.Books.Count}).", "info");
        }
        catch (OperationCanceledException)
        {
            Killeropedia.FailBookRefresh("Odświeżanie katalogu ksiąg zostało anulowane.");
        }
        catch (Exception exception)
        {
            Killeropedia.FailBookRefresh($"Błąd odświeżania: {exception.Message}");
            EmitSystem($"Killeropedia: {exception.Message}", 31);
        }
        finally
        {
            if (lockTaken)
            {
                _triggerSendLock.Release();
            }

            _bookRefreshCts = null;
            _sendCommandCommand.NotifyCanExecuteChanged();
            cancellation.Dispose();
            if (Killeropedia.IsBookRefreshRunning)
            {
                Killeropedia.FailBookRefresh("Odświeżanie katalogu ksiąg zakończone bez zapisu.");
            }
        }
    }

    private async Task SendBookCatalogCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (Map.IsMapEditorActive)
        {
            throw new InvalidOperationException("Odświeżanie katalogu jest niedostępne podczas mapowania.");
        }

        var echo = command.Length == 0 ? "[PUSTA WIADOMOŚĆ]" : command;
        await Dispatcher.UIThread.InvokeAsync(() => EmitSystem($"> {echo}", 90));
        await _session.SendCommandAsync(command, cancellationToken);
    }

    private bool CanRefreshRareCatalog() =>
        DeveloperFeatures.EnableRareCatalogRefreshButton
        && IsConnected
        && _bookRefreshCts is null
        && _rareRefreshCts is null
        && _mapujCts is null;

    private async Task RefreshRareCatalogAsync()
    {
        if (!CanRefreshRareCatalog())
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _rareRefreshCts = cancellation;
        _sendCommandCommand.NotifyCanExecuteChanged();
        Killeropedia.BeginRareRefresh();
        var lockTaken = false;

        try
        {
            await _triggerSendLock.WaitAsync(cancellation.Token);
            lockTaken = true;
            var progress = new Progress<RareCatalogRefreshProgress>(Killeropedia.ReportRareRefresh);
            var catalog = await _rareCatalogRefreshCoordinator.RefreshAsync(
                SendRareCatalogCommandAsync,
                progress,
                cancellation.Token,
                knownDetails: Killeropedia.GetKnownRareDetails(),
                // A full refresh walks hundreds of vnums one at a time and can take a very long
                // time — persist after every newly-mapped item so a disconnect or crash partway
                // through doesn't throw away everything mapped in this run. The next refresh
                // (or app launch) then only has to fetch whatever is still missing.
                onEntryMapped: (mappedSoFar, token) => _rareCatalogStore.SaveAsync(
                    new RareCatalogDocument { GeneratedAtUtc = DateTimeOffset.UtcNow, Rares = mappedSoFar.ToList() },
                    token));
            await _rareCatalogStore.SaveAsync(catalog, cancellation.Token);
            Killeropedia.CompleteRareRefresh(catalog);
            AddToast($"Odświeżono katalog przedmiotów ({catalog.Rares.Count}).", "info");
        }
        catch (OperationCanceledException)
        {
            Killeropedia.FailRareRefresh("Odświeżanie katalogu przedmiotów zostało anulowane.");
        }
        catch (Exception exception)
        {
            Killeropedia.FailRareRefresh($"Błąd odświeżania: {exception.Message}");
            EmitSystem($"Killeropedia: {exception.Message}", 31);
        }
        finally
        {
            if (lockTaken)
            {
                _triggerSendLock.Release();
            }

            _rareRefreshCts = null;
            _sendCommandCommand.NotifyCanExecuteChanged();
            cancellation.Dispose();
            if (Killeropedia.IsRareRefreshRunning)
            {
                Killeropedia.FailRareRefresh("Odświeżanie katalogu przedmiotów zakończone bez zapisu.");
            }
        }
    }

    private async Task SendRareCatalogCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (Map.IsMapEditorActive)
        {
            throw new InvalidOperationException("Odświeżanie katalogu jest niedostępne podczas mapowania.");
        }

        var echo = command.Length == 0 ? "[PUSTA WIADOMOŚĆ]" : command;
        await Dispatcher.UIThread.InvokeAsync(() => EmitSystem($"> {echo}", 90));
        await _session.SendCommandAsync(command, cancellationToken);
    }

    private void RefreshCommands()
    {
        _connectCommand.NotifyCanExecuteChanged();
        _disconnectCommand.NotifyCanExecuteChanged();
        _sendCommandCommand.NotifyCanExecuteChanged();
        SwitchProfileCommand.NotifyCanExecuteChanged();
        StartAutoFarmCommand.NotifyCanExecuteChanged();
        StopAutoFarmCommand.NotifyCanExecuteChanged();
    }

    // ========================================================================
    // Mock data
    // ========================================================================

    private void PopulateMockData()
    {
        // Status effects are populated live from Char.Affects GMCP.

        // Group members are populated live from Char.Group GMCP.

        // Notes (mock)
        Notes.Add(new NoteEntry
        {
            Title = "Lista zakupów",
            Content = "- Mikstura leczenia x5\n- Zwój teleportacji\n- Nowy miecz",
            CreatedAt = "2026-01-15 14:22",
        });
        Notes.Add(new NoteEntry
        {
            Title = "Kluczowe lokacje",
            Content = "Gildia magów: 3n, 2w od rynku\nKowal: 1e, 4s od rynku",
            CreatedAt = "2026-01-14 09:10",
        });

        // Welcome toast
        AddToast("Witaj w MudClient! Łączenie automatyczne — możesz zmienić host/port i połączyć się ręcznie.", "info");
    }

    // ========================================================================
    // Dispose
    // ========================================================================

    public async ValueTask DisposeAsync()
    {
        SaveActiveProfile();

        _contentUpdateCts?.Cancel();
        CheckContentUpdatesCommand.Cancel();
        InstallContentUpdateCommand.Cancel();
        if (_contentUpdateCheckTask is not null)
        {
            try
            {
                await _contentUpdateCheckTask;
            }
            catch (OperationCanceledException)
            {
                // The optional content check was cancelled during shutdown.
            }
        }

        foreach (var contentTask in new[]
                 {
                     CheckContentUpdatesCommand.ExecutionTask,
                     InstallContentUpdateCommand.ExecutionTask,
                 }.Where(task => task is not null))
        {
            try
            {
                await contentTask!;
            }
            catch (OperationCanceledException)
            {
                // Expected when the window closes during a manual content operation.
            }
        }
        _contentUpdateCts?.Dispose();

        _appUpdateCts?.Cancel();
        CheckAppUpdatesCommand.Cancel();
        if (_appUpdateCheckTask is not null)
        {
            try
            {
                await _appUpdateCheckTask;
            }
            catch (OperationCanceledException)
            {
                // The optional app-update check was cancelled during shutdown.
            }
        }

        if (CheckAppUpdatesCommand.ExecutionTask is { } appUpdateCommandTask)
        {
            try
            {
                await appUpdateCommandTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the window closes during a manual app-update check.
            }
        }
        _appUpdateCts?.Dispose();

        try
        {
            _dockLayoutService.Save(_dockFactory.Snapshot(Layout));
        }
        catch (IOException)
        {
            // Best-effort; the previous layout file (if any) remains on disk.
        }

        _characterState.VitalsChanged -= OnCharacterVitalsChanged;
        _characterState.ConditionChanged -= OnCharacterConditionChanged;
        _characterState.PeopleChanged -= OnRoomPeopleChanged;
        _characterState.GroupChanged -= OnGroupChanged;
        _characterState.MemSpellsChanged -= OnMemSpellsChanged;
        _characterState.AffectsChanged -= OnCharacterAffectsChanged;
        _worldState.TimeChanged -= OnWorldTimeChanged;
        _worldState.WeatherChanged -= OnWorldWeatherChanged;
        _skillTimeouts.TimeoutsChanged -= OnSkillTimeoutsChanged;

        _session.TextReceived -= OnTextReceived;
        _session.LineReceived -= OnLineReceived;
        _session.GmcpReceived -= OnGmcpReceived;
        _session.GmcpSent -= OnGmcpSent;
        _session.CommandSent -= OnCommandSent;
        _session.StatusChanged -= OnStatusChanged;
        _session.ConnectionError -= OnConnectionError;
        _session.ConnectionClosed -= OnConnectionClosed;

        Map.PropertyChanged -= OnMapPropertyChanged;
        _locationResolver.LocationChanged -= OnAutowalkLocationChanged;
        _locationResolver.LocationChanged -= OnRoomEnterAutomations;
        _locationResolver.LocationChanged -= OnRoomEnterShowVnum;
        _roomExits.ExitsChanged -= OnRoomExitsChanged;
        _roomSnapshots.SnapshotReceived -= OnRoomSnapshotReceived;
        Map.MapEditorActiveChanged -= OnMapEditorActiveChanged;
        Map.RoomDoubleClicked -= OnMapRoomDoubleClicked;
        Map.LordGotoRequested -= OnLordGotoRequested;
        Map.LordModeChanged -= OnMapLordModeChanged;
        Map.GroupMarkerDisplayChanged -= OnMapGroupMarkerDisplayChanged;
        Map.DisplayModeChanged -= OnMapDisplayModeChanged;
        Map.AutoWalkOnMapDoubleClickChanged -= OnMapAutoWalkOnDoubleClickChanged;
        Map.AutoScanOnRoomEnterChanged -= OnMapAutoScanOnRoomEnterChanged;
        Map.AutoKillOnRoomEnterChanged -= OnMapAutoKillOnRoomEnterChanged;
        Map.AutoKillMobNamesChanged -= OnMapAutoKillMobNamesChanged;
        Map.AutoFarmRegionsChanged -= OnMapAutoFarmRegionsChanged;

        _autowalkCts.Cancel();
        _bookRefreshCts?.Cancel();
        if (Killeropedia.RefreshBooksCommand.ExecutionTask is { } bookRefreshTask)
        {
            try
            {
                await bookRefreshTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when closing the application during a creator refresh.
            }
        }

        _rareRefreshCts?.Cancel();
        if (Killeropedia.RefreshRaresCommand.ExecutionTask is { } rareRefreshTask)
        {
            try
            {
                await rareRefreshTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when closing the application during a creator refresh.
            }
        }

        _mapujCts?.Cancel();
        if (_mapujTask is { } mapujTask)
        {
            try
            {
                await mapujTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when closing the application during a "/mapuj" run.
            }
        }

        // Phase 1 — stop accepting new trigger tasks atomically.
        // OnLineReceived holds the same lock when it checks the flag,
        // creates a task, and registers it, so after this block no new
        // task will be added to _triggerTasks.
        List<Task> pending;
        lock (_triggerTasksLock)
        {
            _acceptingTriggerTasks = false;
            pending = new List<Task>(_triggerTasks);
            _triggerTasks.Clear();
        }

        // Phase 2 — cancel the CTS so any in-flight WaitAsync calls
        // on the semaphore observe cancellation and exit without
        // acquiring the lock.
        _triggerCts.Cancel();

        // Phase 3 — drain the tasks we snapshotted above.
        foreach (var task in pending)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected — the batch was cancelled by our CTS.
            }
            catch (Exception)
            {
                // Swallow any other exceptions during shutdown so that
                // they do not become unobserved and tear down the process.
            }
        }

        // Phase 4 — belt-and-suspenders re-check.  The flag gate above
        // prevents new additions, and RemoveWhenCompleted only removes
        // from the list, so this loop should be empty.  We keep it as a
        // defense-in-depth measure against any unanticipated path.
        while (true)
        {
            lock (_triggerTasksLock)
            {
                if (_triggerTasks.Count == 0)
                {
                    break;
                }

                pending = new List<Task>(_triggerTasks);
                _triggerTasks.Clear();
            }

            foreach (var task in pending)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
                catch (Exception)
                {
                    // Swallow.
                }
            }
        }

        // Final gate: acquire the semaphore and release it immediately.
        // This protects against the edge case where a trigger task managed
        // to acquire the semaphore before the CTS was cancelled but had
        // not yet released it.  Waiting ensures the release happened.
        await _triggerSendLock.WaitAsync();
        _triggerSendLock.Release();

        await _timers.DisposeAsync();
        await _session.DisposeAsync();
        await Map.DisposeAsync();
        _triggerSendLock.Dispose();
        _triggerCts.Dispose();
        _autowalkCts.Dispose();
        _automationActivityClearCts?.Dispose();
        foreach (var cts in _triggerRecentlyFiredClearTokens.Values)
        {
            cts.Dispose();
        }
    }
}
