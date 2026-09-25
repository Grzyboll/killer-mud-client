using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudClient.App.Controls;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.Core.Gmcp;
using MudClient.Core.Map;

namespace MudClient.App.ViewModels;

public sealed class MapViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private const double BaseMovementButtonWidth = 88;
    private const double BaseMovementButtonHeight = 42;

    private static readonly IReadOnlyDictionary<string, bool> EmptySpellKnowledge =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, int> EmptySkillKnowledge =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<int> EmptyAutoFarmVisitedRoomIds = new HashSet<int>();

    private MainWindowViewModel? _mainViewModel;

    /// <summary>Set once by <see cref="MainWindowViewModel"/> after constructing this instance.
    /// Lets Map's own settings flyout (see MapPanelView.axaml / TerminalOverlayCard.axaml) reach
    /// the Autowalk state and commands (Locations, Deaths, travel status) that live there instead
    /// of being duplicated onto MapViewModel — those panels' functionality was moved into this
    /// flyout, but the underlying state remains ordinary MainWindowViewModel state shared by
    /// other bindings too. Null only in tests that construct a MapViewModel standalone. Also
    /// tracks <see cref="MainWindowViewModel.Deaths"/> so death marks can be drawn directly on the
    /// map (see <see cref="DeathMarkers"/>), not just listed in the settings flyout.</summary>
    public MainWindowViewModel? MainViewModel
    {
        get => _mainViewModel;
        set
        {
            if (ReferenceEquals(_mainViewModel, value))
            {
                return;
            }

            if (_mainViewModel is not null)
            {
                _mainViewModel.Deaths.CollectionChanged -= OnDeathsChanged;
            }

            _mainViewModel = value;

            if (_mainViewModel is not null)
            {
                _mainViewModel.Deaths.CollectionChanged += OnDeathsChanged;
            }

            RefreshDeathMarkers();
        }
    }

    private void OnDeathsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RefreshDeathMarkers();

    private readonly string _packagedMapDirectory;
    private readonly ContentPathResolver? _contentPaths;
    private readonly string? _mapEditorPath;
    private readonly MapEditorRecoveryStore? _mapEditorRecoveryStore;
    private readonly TimeSpan _mapMovementTimeout;
    private string _baseWorldMapPath = string.Empty;
    private string _worldMapPath = string.Empty;
    private string _mapSettingsPath = string.Empty;
    private string _sectorDirectory = string.Empty;
    private string _sectorManifestPath = string.Empty;
    private string _roomImageDirectory = string.Empty;
    private readonly GmcpLocationResolver _locationResolver;

    private MapIndex? _mapIndex;
    private SectorTextureCache? _textureCache;
    private RoomImageCache? _roomImages;
    private MapSettings _settings = MapSettings.CreateDefault();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _mapMovementTimeoutCancellation;
    private Task _mapMovementTimeoutTask = Task.CompletedTask;

    private bool _isLoading;
    private string? _errorMessage;
    private string _statusMessage = "Mapa nie została jeszcze załadowana.";

    private MapArea? _selectedArea;
    private double _selectedZ;
    private MapRoom? _currentRoom;
    private MapRoom? _selectedRoom;
    private IReadOnlyList<MapRoom>? _routeRooms;
    private IReadOnlyList<CharacterGroupMember> _groupMembers = [];
    private IReadOnlyList<GroupMapMarker> _groupMarkers = [];
    private IReadOnlyList<DeathMapMarker> _deathMarkers = [];
    private IReadOnlyList<RoomMapMarker> _roomMarkers = [];
    private readonly MapMarkerStore? _markerStore;
    private readonly Dictionary<string, MapMarker> _markersByVnum = new(StringComparer.Ordinal);
    /// <summary>Holds a marker cut via <see cref="CutMarkerOnSelectedRoomCommand"/> until it's
    /// dropped onto another room with <see cref="PasteMarkerOnSelectedRoomCommand"/> — lets a
    /// misplaced marker (symbol and note both) move to the correct room without retyping the note.
    /// Stays put after a paste (not consumed) so the same fix can be pasted onto more than one room
    /// if needed; only replaced by cutting something else.</summary>
    private MapMarker? _cutMarker;
    private readonly SharedMapMarkerStore _sharedMarkerStore = new();
    private readonly IReadOnlyList<MapMarker> _sharedMarkerCatalog;
    private readonly IReadOnlyList<TeacherEntry> _teacherCatalog;
    private IReadOnlyList<TeacherMapMarker> _teacherMarkers = [];
    private IReadOnlyList<MapSearchEntry> _searchEntries = [];
    private readonly IReadOnlyList<SpellMobEntry> _spellMobCatalog;
    private IReadOnlyList<SpellMobMapMarker> _spellMobMarkers = [];
    private IReadOnlyDictionary<string, bool> _spellKnowledge = EmptySpellKnowledge;
    private IReadOnlyDictionary<string, int> _skillKnowledge = EmptySkillKnowledge;
    private string? _currentSectorName;
    private bool _followPlayer = true;
    private bool _lordModeEnabled;
    private bool _showGroupMembersAsNumbers;
    private bool _autoWalkOnMapDoubleClick = true;
    private double _movementButtonScalePercent = AppSettings.DefaultMapMovementButtonScalePercent;
    private bool _autoScanOnRoomEnter;
    private bool _autoKillOnRoomEnter;
    private string _autoKillMobNamesText = string.Empty;
    private IReadOnlyList<FarmRegion> _autoFarmRegions = [];
    private bool _isDefiningAutoFarmRegion;
    private IReadOnlySet<int> _autoFarmVisitedRoomIds = EmptyAutoFarmVisitedRoomIds;
    private bool _isUsingWorkingMap;
    private bool _isUsingRecoveryMap;
    private string _newMapAreaName = string.Empty;
    private bool _moveExistingRoomsToNewArea;
    private MapDisplayModeOption _selectedDisplayMode;
    private readonly RelayCommand _lordGotoSelectedRoomCommand;
    private readonly RelayCommand<string> _setMarkerOnSelectedRoomCommand;
    private readonly RelayCommand _removeMarkerFromSelectedRoomCommand;
    private readonly RelayCommand _cutMarkerFromSelectedRoomCommand;
    private readonly RelayCommand _pasteMarkerOnSelectedRoomCommand;
    private readonly RelayCommand _reportMarkersCommand;
    private readonly RelayCommand _findNearestRentCommand;
    private readonly RelayCommand _startMapEditorCommand;
    private readonly RelayCommand _stopMapEditorCommand;
    private readonly RelayCommand _undoMapEditorCommand;
    private readonly RelayCommand _redoMapEditorCommand;
    private readonly RelayCommand _createMapAreaCommand;
    private readonly AsyncRelayCommand _saveMapEditorCommand;
    private readonly RelayCommand _clearAutoFarmRegionCommand;
    private readonly RelayCommand<string> _moveThroughExitCommand;
    private RoomExitInfo? _northExit;
    private RoomExitInfo? _southExit;
    private RoomExitInfo? _westExit;
    private RoomExitInfo? _eastExit;
    private RoomExitInfo? _upExit;
    private RoomExitInfo? _downExit;
    private readonly RelayCommand<FarmRegionSummary> _removeAutoFarmRegionCommand;
    private MapEditorSession? _mapEditor;

    public MapViewModel(
        string appBaseDirectory,
        GmcpLocationResolver locationResolver,
        string? dataRoot = null,
        TimeSpan? mapMovementTimeout = null,
        IReadOnlyList<TeacherEntry>? teacherCatalogOverride = null,
        IReadOnlyList<SpellMobEntry>? spellMobCatalogOverride = null,
        IReadOnlyList<MapMarker>? sharedMarkerCatalogOverride = null)
    {
        _packagedMapDirectory = Path.Combine(appBaseDirectory, "Assets", "Map");
        _contentPaths = string.IsNullOrWhiteSpace(dataRoot) ? null : new ContentPathResolver(dataRoot);
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var editorDirectory = Path.Combine(dataRoot, "MapEditor");
            _mapEditorPath = Path.Combine(editorDirectory, "world-map.json");
            _mapEditorRecoveryStore = new MapEditorRecoveryStore(editorDirectory);

            _markerStore = new MapMarkerStore(Path.Combine(dataRoot, "map-markers.json"));
            try
            {
                foreach (var marker in _markerStore.Load().Markers)
                {
                    _markersByVnum[marker.Vnum] = marker;
                }
            }
            catch (InvalidDataException)
            {
                // A corrupt local marker file shouldn't block the map from loading at all —
                // start fresh; the file gets overwritten on the next SetMarkerOnSelectedRoom.
            }
        }
        // Same source Killeropedia uses (see MainWindowViewModel.CreateKilleropediaViewModel) —
        // loaded independently here so the map's "T" markers don't depend on Killeropedia having
        // been opened first. Tests inject teacherCatalogOverride instead of depending on the real
        // embedded/downloaded catalog's contents.
        if (teacherCatalogOverride is not null)
        {
            _teacherCatalog = teacherCatalogOverride;
        }
        else
        {
            var downloadedKilleropediaDirectory = _contentPaths?.GetActiveDirectory("killeropedia");
            _teacherCatalog = TeacherCatalogLoader.Load(
                downloadedKilleropediaDirectory is null
                    ? null
                    : Path.Combine(downloadedKilleropediaDirectory, "teachers.json.gz"));
        }

        // Community-sourced mob/room/spell list; embedded only (unlike teachers, there's no
        // downloadable Killeropedia variant to prefer). Tests inject spellMobCatalogOverride.
        _spellMobCatalog = spellMobCatalogOverride ?? SpellMobCatalogLoader.Load();

        // The community's currently-accepted markers (see SharedMapMarkerStore) — rendered as the
        // lowest-priority auto layer in RefreshRoomMarkers, same idea as "T"/"B" but with the
        // symbol coming straight from the catalog instead of a fixed constant. Tests inject
        // sharedMarkerCatalogOverride instead of depending on the real embedded catalog's contents.
        _sharedMarkerCatalog = sharedMarkerCatalogOverride ?? _sharedMarkerStore.Load().Markers;

        _mapMovementTimeout = mapMovementTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : TimeSpan.FromSeconds(8);
        SetMapPaths(_packagedMapDirectory);

        _selectedDisplayMode = MapDisplayModeOption.All[0];
        _locationResolver = locationResolver;
        _locationResolver.LocationChanged += OnLocationChanged;

        ReloadCommand = new AsyncRelayCommand(InitializeAsync);
        CenterCommand = new RelayCommand(RequestCenterOnCurrentRoom);
        _lordGotoSelectedRoomCommand = new RelayCommand(
            RequestLordGotoSelectedRoom,
            CanLordGotoSelectedRoom);
        _setMarkerOnSelectedRoomCommand = new RelayCommand<string>(SetMarkerOnSelectedRoom, _ => CanEditSelectedRoomMarker);
        _removeMarkerFromSelectedRoomCommand = new RelayCommand(RemoveMarkerFromSelectedRoom, () => SelectedRoomHasMarker);
        _cutMarkerFromSelectedRoomCommand = new RelayCommand(CutMarkerOnSelectedRoom, () => SelectedRoomHasMarker);
        _pasteMarkerOnSelectedRoomCommand = new RelayCommand(PasteMarkerOnSelectedRoom, () => CanEditSelectedRoomMarker && HasCutMarker);
        _reportMarkersCommand = new RelayCommand(ReportMarkers, () => _markersByVnum.Count > 0);
        _findNearestRentCommand = new RelayCommand(
            FindNearestRent,
            () => RoomMarkers.Any(marker => marker.Symbol == RentMarkerSymbol));
        _startMapEditorCommand = new RelayCommand(StartMapEditor, CanStartMapEditor);
        _stopMapEditorCommand = new RelayCommand(StopMapEditor, () => IsMapEditorActive);
        _undoMapEditorCommand = new RelayCommand(UndoMapEditor, () => _mapEditor?.CanUndo == true);
        _redoMapEditorCommand = new RelayCommand(RedoMapEditor, () => _mapEditor?.CanRedo == true);
        _createMapAreaCommand = new RelayCommand(CreateMapAreaFromInput, CanCreateMapAreaFromInput);
        _saveMapEditorCommand = new AsyncRelayCommand(SaveMapEditorAsync, () => _mapEditor?.IsDirty == true);
        _clearAutoFarmRegionCommand = new RelayCommand(ClearAutoFarmRegion, () => AutoFarmRegions.Count > 0);
        _moveThroughExitCommand = new RelayCommand<string>(direction =>
        {
            if (!string.IsNullOrWhiteSpace(direction))
            {
                MainViewModel?.SendMapMovementCommand(direction);
            }
        });
        _removeAutoFarmRegionCommand = new RelayCommand<FarmRegionSummary>(RemoveAutoFarmRegion);
    }

    public event Action? CenterOnCurrentRoomRequested;

    public event Action<MapRoom>? CenterOnRoomRequested;

    /// <summary>Raised by the view when the user double-clicks a room on the map.</summary>
    public event Action<MapRoom>? RoomDoubleClicked;

    public event Action<MapRoom>? LordGotoRequested;

    public event Action<bool>? LordModeChanged;

    public event Action<bool>? GroupMarkerDisplayChanged;

    public event Action<bool>? AutoWalkOnMapDoubleClickChanged;

    public event Action<double>? MovementButtonScalePercentChanged;

    public event Action<bool>? MapEditorActiveChanged;

    public event Action<bool>? AutoScanOnRoomEnterChanged;

    public event Action<bool>? AutoKillOnRoomEnterChanged;

    public event Action<string>? AutoKillMobNamesChanged;

    /// <summary>Fired whenever <see cref="AutoFarmRegions"/> changes (a region drawn or added,
    /// all cleared, or reloaded on profile switch) — <see cref="MainWindowViewModel"/> subscribes
    /// to persist it and to know which regions the auto-farm state machine may roam within.</summary>
    public event Action<IReadOnlyList<FarmRegion>>? AutoFarmRegionsChanged;

    public ObservableCollection<MapArea> Areas { get; } = [];

    public ObservableCollection<double> ZLevels { get; } = [];

    public IAsyncRelayCommand ReloadCommand { get; }

    public IRelayCommand CenterCommand { get; }

    public IRelayCommand LordGotoSelectedRoomCommand => _lordGotoSelectedRoomCommand;

    public IRelayCommand<string> SetMarkerOnSelectedRoomCommand => _setMarkerOnSelectedRoomCommand;

    public IRelayCommand RemoveMarkerFromSelectedRoomCommand => _removeMarkerFromSelectedRoomCommand;

    public IRelayCommand CutMarkerFromSelectedRoomCommand => _cutMarkerFromSelectedRoomCommand;

    public IRelayCommand PasteMarkerOnSelectedRoomCommand => _pasteMarkerOnSelectedRoomCommand;

    public IRelayCommand ReportMarkersCommand => _reportMarkersCommand;

    public IRelayCommand FindNearestRentCommand => _findNearestRentCommand;

    public IRelayCommand StartMapEditorCommand => _startMapEditorCommand;

    public IRelayCommand StopMapEditorCommand => _stopMapEditorCommand;

    public IRelayCommand UndoMapEditorCommand => _undoMapEditorCommand;

    public IRelayCommand RedoMapEditorCommand => _redoMapEditorCommand;

    public IRelayCommand CreateMapAreaCommand => _createMapAreaCommand;

    public IAsyncRelayCommand SaveMapEditorCommand => _saveMapEditorCommand;

    public IRelayCommand ClearAutoFarmRegionCommand => _clearAutoFarmRegionCommand;

    public IRelayCommand<string> MoveThroughExitCommand => _moveThroughExitCommand;

    public double MinMovementButtonScalePercent => AppSettings.MinMapMovementButtonScalePercent;

    public double MaxMovementButtonScalePercent => AppSettings.MaxMapMovementButtonScalePercent;

    public double MovementButtonScalePercent
    {
        get => _movementButtonScalePercent;
        set
        {
            var clamped = Math.Clamp(
                Math.Round(value),
                AppSettings.MinMapMovementButtonScalePercent,
                AppSettings.MaxMapMovementButtonScalePercent);
            if (!SetProperty(ref _movementButtonScalePercent, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(MovementButtonScaleText));
            OnPropertyChanged(nameof(MovementButtonWidth));
            OnPropertyChanged(nameof(MovementButtonHeight));
            OnPropertyChanged(nameof(MovementButtonNameMaxWidth));
            MovementButtonScalePercentChanged?.Invoke(clamped);
        }
    }

    public string MovementButtonScaleText => $"{MovementButtonScalePercent:0}%";

    public double MovementButtonWidth =>
        BaseMovementButtonWidth * MovementButtonScalePercent / 100;

    public double MovementButtonHeight =>
        BaseMovementButtonHeight * MovementButtonScalePercent / 100;

    public double MovementButtonNameMaxWidth => Math.Max(30, MovementButtonWidth - 36);

    public RoomExitInfo? NorthExit
    {
        get => _northExit;
        private set => SetMovementExit(ref _northExit, value);
    }

    public RoomExitInfo? SouthExit
    {
        get => _southExit;
        private set => SetMovementExit(ref _southExit, value);
    }

    public RoomExitInfo? WestExit
    {
        get => _westExit;
        private set => SetMovementExit(ref _westExit, value);
    }

    public RoomExitInfo? EastExit
    {
        get => _eastExit;
        private set => SetMovementExit(ref _eastExit, value);
    }

    public RoomExitInfo? UpExit
    {
        get => _upExit;
        private set => SetMovementExit(ref _upExit, value);
    }

    public RoomExitInfo? DownExit
    {
        get => _downExit;
        private set => SetMovementExit(ref _downExit, value);
    }

    public string? NorthExitName => NorthExit?.Name;
    public string? SouthExitName => SouthExit?.Name;
    public string? WestExitName => WestExit?.Name;
    public string? EastExitName => EastExit?.Name;
    public string? UpExitName => UpExit?.Name;
    public string? DownExitName => DownExit?.Name;

    public bool NorthExitIsClosedDoor => NorthExit?.IsClosedDoor == true;
    public bool SouthExitIsClosedDoor => SouthExit?.IsClosedDoor == true;
    public bool WestExitIsClosedDoor => WestExit?.IsClosedDoor == true;
    public bool EastExitIsClosedDoor => EastExit?.IsClosedDoor == true;
    public bool UpExitIsClosedDoor => UpExit?.IsClosedDoor == true;
    public bool DownExitIsClosedDoor => DownExit?.IsClosedDoor == true;

    public bool HasMovementExits =>
        NorthExit is not null || SouthExit is not null || WestExit is not null ||
        EastExit is not null || UpExit is not null || DownExit is not null;

    internal void UpdateRoomExits(IReadOnlyList<RoomExitInfo> exits)
    {
        NorthExit = FindMovementExit("N", exits);
        SouthExit = FindMovementExit("S", exits);
        WestExit = FindMovementExit("W", exits);
        EastExit = FindMovementExit("E", exits);
        UpExit = FindMovementExit("U", exits);
        DownExit = FindMovementExit("D", exits);
    }

    private static RoomExitInfo? FindMovementExit(
        string direction,
        IReadOnlyList<RoomExitInfo> exits) =>
        exits.FirstOrDefault(exit => string.Equals(
            RoomMovementPolicy.CanonicalDirection(exit.Dir),
            RoomMovementPolicy.CanonicalDirection(direction),
            StringComparison.Ordinal));

    private void SetMovementExit(
        ref RoomExitInfo? field,
        RoomExitInfo? value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged($"{propertyName}Name");
            OnPropertyChanged($"{propertyName}IsClosedDoor");
            OnPropertyChanged(nameof(HasMovementExits));
        }
    }
    public IRelayCommand<FarmRegionSummary> RemoveAutoFarmRegionCommand => _removeAutoFarmRegionCommand;

    public string NewMapAreaName
    {
        get => _newMapAreaName;
        set
        {
            if (SetProperty(ref _newMapAreaName, value))
            {
                _createMapAreaCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool MoveExistingRoomsToNewArea
    {
        get => _moveExistingRoomsToNewArea;
        set => SetMoveExistingRoomsToNewArea(value);
    }

    public bool CanMoveExistingRoomsToNewArea =>
        _mapEditor is not null && SelectedArea is not null && !IsMapEditorActive;

    public MapIndex? MapIndex
    {
        get => _mapIndex;
        private set
        {
            if (SetProperty(ref _mapIndex, value))
            {
                RefreshGroupMarkers();
                RefreshDeathMarkers();
                RefreshTeacherMarkers();
                RefreshSpellMobMarkers();
                RefreshSearchEntries();
                RefreshRoomMarkers();
                // Per-region room counts in AutoFarmRegionStatusText/AutoFarmRegionSummaries
                // depend on the map being loaded — refresh them too, in case regions were
                // already restored (profile load) before the map finished loading.
                OnPropertyChanged(nameof(AutoFarmRegionStatusText));
                OnPropertyChanged(nameof(AutoFarmRegionSummaries));
            }
        }
    }

    public SectorTextureCache? TextureCache
    {
        get => _textureCache;
        private set
        {
            if (SetProperty(ref _textureCache, value))
            {
                OnPropertyChanged(nameof(SelectedRoomIcon));
            }
        }
    }

    public RoomImageCache? RoomImages
    {
        get => _roomImages;
        private set
        {
            if (SetProperty(ref _roomImages, value))
            {
                OnPropertyChanged(nameof(SelectedRoomIcon));
            }
        }
    }

    public MapSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(LordModeStatusMessage));
            }
        }
    }

    public MapArea? SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (SetProperty(ref _selectedArea, value) && value is not null)
            {
                if (MoveExistingRoomsToNewArea && !IsMapEditorActive)
                {
                    _mapEditor?.SetMoveKnownRoomsToTargetArea(true, value.Id);
                }
                FollowPlayer = false;
                RefreshZLevels();
                FocusFirstRoom(value);
                OnPropertyChanged(nameof(CanMoveExistingRoomsToNewArea));
            }
        }
    }

    public double SelectedZ
    {
        get => _selectedZ;
        set
        {
            if (SetProperty(ref _selectedZ, value))
            {
                OnPropertyChanged(nameof(SelectedZIndex));
                FollowPlayer = false;
            }
        }
    }

    /// <summary>
    /// Index projection for the Z-level ComboBox. Avalonia temporarily selects -1 while
    /// the level list is rebuilt after an area change; unlike SelectedItem, that transition
    /// does not require converting null to <see cref="double"/>.
    /// </summary>
    public int SelectedZIndex
    {
        get => ZLevels.IndexOf(SelectedZ);
        set
        {
            if (value >= 0 && value < ZLevels.Count)
            {
                SelectedZ = ZLevels[value];
            }
        }
    }

    public MapRoom? CurrentRoom
    {
        get => _currentRoom;
        private set => SetProperty(ref _currentRoom, value);
    }

    public MapRoom? SelectedRoom
    {
        get => _selectedRoom;
        set
        {
            if (SetProperty(ref _selectedRoom, value))
            {
                OnPropertyChanged(nameof(SelectedRoomIcon));
                OnPropertyChanged(nameof(LordGotoMenuHeader));
                OnPropertyChanged(nameof(CanEditSelectedRoomMarker));
                OnPropertyChanged(nameof(SelectedRoomHasMarker));
                OnPropertyChanged(nameof(SelectedRoomNote));
                _lordGotoSelectedRoomCommand.NotifyCanExecuteChanged();
                _setMarkerOnSelectedRoomCommand.NotifyCanExecuteChanged();
                _removeMarkerFromSelectedRoomCommand.NotifyCanExecuteChanged();
                _cutMarkerFromSelectedRoomCommand.NotifyCanExecuteChanged();
                _pasteMarkerOnSelectedRoomCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Autowalk route to paint on the map, or null when idle.</summary>
    public IReadOnlyList<MapRoom>? RouteRooms
    {
        get => _routeRooms;
        set => SetProperty(ref _routeRooms, value);
    }

    public void NotifyRoomDoubleClicked(MapRoom room) => RoomDoubleClicked?.Invoke(room);

    public bool LordModeEnabled
    {
        get => _lordModeEnabled;
        set
        {
            if (!SetProperty(ref _lordModeEnabled, value))
            {
                return;
            }

            _lordGotoSelectedRoomCommand.NotifyCanExecuteChanged();
            if (!value && IsMapEditorActive)
            {
                StopMapEditor();
            }

            NotifyMapEditorCommands();
            LordModeChanged?.Invoke(value);
        }
    }

    public bool ShowGroupMembersAsNumbers
    {
        get => _showGroupMembersAsNumbers;
        set
        {
            if (SetProperty(ref _showGroupMembersAsNumbers, value))
            {
                GroupMarkerDisplayChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Basic (default, on): double-clicking a room on the map starts walking there
    /// immediately. Off: double-click only previews the route until confirmed.</summary>
    public bool AutoWalkOnMapDoubleClick
    {
        get => _autoWalkOnMapDoubleClick;
        set
        {
            if (SetProperty(ref _autoWalkOnMapDoubleClick, value))
            {
                AutoWalkOnMapDoubleClickChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Sends "scan" every time GMCP reports the character entering a new room — actually
    /// sent by <see cref="MainWindowViewModel"/>, which owns the game session; this just carries
    /// the toggle and its persisted value.</summary>
    public bool AutoScanOnRoomEnter
    {
        get => _autoScanOnRoomEnter;
        set
        {
            if (SetProperty(ref _autoScanOnRoomEnter, value))
            {
                AutoScanOnRoomEnterChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Sends "kill &lt;name&gt;" for every name in <see cref="AutoKillMobNamesText"/>
    /// every time GMCP reports the character entering a new room — unconditionally per name,
    /// same as <see cref="AutoScanOnRoomEnter"/>; the MUD itself reports when a name isn't
    /// actually present.</summary>
    public bool AutoKillOnRoomEnter
    {
        get => _autoKillOnRoomEnter;
        set
        {
            if (SetProperty(ref _autoKillOnRoomEnter, value))
            {
                AutoKillOnRoomEnterChanged?.Invoke(value);
            }
        }
    }

    /// <summary>One mob name per line — everyone <see cref="AutoKillOnRoomEnter"/> attacks on
    /// sight (e.g. "strażnik", "keton").</summary>
    public string AutoKillMobNamesText
    {
        get => _autoKillMobNamesText;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _autoKillMobNamesText, normalized))
            {
                AutoKillMobNamesChanged?.Invoke(normalized);
            }
        }
    }

    /// <summary>Rectangular map regions auto-farm may roam within — grown one at a time by
    /// drawing right-click drags on the map (see <see cref="WorldMapControl.RegionSelected"/>)
    /// while <see cref="IsDefiningAutoFarmRegion"/> is true, or bulk-replaced when reloaded from
    /// the active profile on switch (see <see cref="SetAutoFarmRegions"/>). Empty until first
    /// defined.</summary>
    public IReadOnlyList<FarmRegion> AutoFarmRegions
    {
        get => _autoFarmRegions;
        private set
        {
            if (SetProperty(ref _autoFarmRegions, value))
            {
                OnPropertyChanged(nameof(AutoFarmRegionStatusText));
                OnPropertyChanged(nameof(AutoFarmRegionSummaries));
                _clearAutoFarmRegionCommand.NotifyCanExecuteChanged();
                AutoFarmRegionsChanged?.Invoke(value);
            }
        }
    }

    /// <summary>True while the map is waiting for a right-click drag to draw a new region added
    /// to <see cref="AutoFarmRegions"/> — see <see cref="WorldMapControl.IsRegionSelectModeEnabled"/>.
    /// Turned off automatically as soon as a region is drawn.</summary>
    public bool IsDefiningAutoFarmRegion
    {
        get => _isDefiningAutoFarmRegion;
        set => SetProperty(ref _isDefiningAutoFarmRegion, value);
    }

    /// <summary>Marker symbols auto-farm will never deliberately pick as a destination — see
    /// <see cref="MarkerLegend"/> ("X"=Zamknięte, "#"=Przepaść, "!"=Niebezpieczeństwo,
    /// "!!"=Wielkie niebezpieczeństwo).</summary>
    public static readonly IReadOnlyCollection<string> AutoFarmAvoidedMarkerSymbols =
        new HashSet<string>(StringComparer.Ordinal) { "X", "#", "!", "!!" };

    /// <summary>Room ids currently marked with one of <see cref="AutoFarmAvoidedMarkerSymbols"/> —
    /// recomputed from the live <see cref="RoomMarkers"/> list each time it's read.</summary>
    public HashSet<int> AutoFarmExcludedRoomIds => RoomMarkers
        .Where(marker => AutoFarmAvoidedMarkerSymbols.Contains(marker.Symbol))
        .Select(marker => marker.Room.Id)
        .ToHashSet();

    /// <summary>Room ids the active auto-farm run has already visited, set by
    /// <see cref="MainWindowViewModel"/> as a fresh snapshot on every hop and cleared when the
    /// farm stops — colored yellow on the map (see <see cref="Controls.WorldMapControl.AutoFarmVisitedRoomIds"/>)
    /// so it's obvious at a glance which rooms are still left.</summary>
    public IReadOnlySet<int> AutoFarmVisitedRoomIds
    {
        get => _autoFarmVisitedRoomIds;
        set => SetProperty(ref _autoFarmVisitedRoomIds, value ?? EmptyAutoFarmVisitedRoomIds);
    }

    public string AutoFarmRegionStatusText
    {
        get
        {
            if (AutoFarmRegions.Count == 0)
            {
                return "Obszar farmy nie jest jeszcze zaznaczony.";
            }

            var count = MapIndex is null
                ? 0
                : FarmTraversalPlanner.CountTotal(MapIndex, AutoFarmRegions, AutoFarmExcludedRoomIds);

            if (AutoFarmRegions.Count == 1)
            {
                var region = AutoFarmRegions[0];
                return $"Obszar farmy: {count} pokoi (obszar {region.AreaId}, poziom {region.Z:0.##}).";
            }

            return $"Obszary farmy: {AutoFarmRegions.Count}, łącznie {count} pokoi.";
        }
    }

    /// <summary>One row per <see cref="AutoFarmRegions"/> entry, numbered and with its own room
    /// count, for a per-region "usuń" list — <see cref="AutoFarmRegionStatusText"/> only ever
    /// aggregates, so a list of several regions couldn't otherwise be told apart to remove just
    /// one.</summary>
    public IReadOnlyList<FarmRegionSummary> AutoFarmRegionSummaries => AutoFarmRegions
        .Select((region, index) =>
        {
            var count = MapIndex is null ? 0 : FarmTraversalPlanner.CountTotal(MapIndex, [region], AutoFarmExcludedRoomIds);
            return new FarmRegionSummary(
                region,
                $"{index + 1}. obszar {region.AreaId}, poziom {region.Z:0.##} — {count} pokoi");
        })
        .ToArray();

    /// <summary>Called by MapPanelView's code-behind when a right-drag on the map finishes while
    /// <see cref="IsDefiningAutoFarmRegion"/> was on — adds it alongside any regions already
    /// drawn instead of replacing them, so a run can cover several separate areas of the map.</summary>
    public void NotifyAutoFarmRegionDrawn(FarmRegion region)
    {
        IsDefiningAutoFarmRegion = false;
        AutoFarmRegions = [.. _autoFarmRegions, region];
    }

    public void ClearAutoFarmRegion()
    {
        IsDefiningAutoFarmRegion = false;
        AutoFarmRegions = [];
    }

    /// <summary>Removes just the one region behind <paramref name="summary"/> — bound to each row's
    /// own "usuń" button (see <see cref="AutoFarmRegionSummaries"/>), unlike
    /// <see cref="ClearAutoFarmRegion"/> which wipes all of them. <see cref="FarmRegion"/> is a
    /// value type with no id of its own, so this removes by value (the first structural match) —
    /// harmless even for two identically-drawn regions, since either is indistinguishable from the
    /// other and removing "a" match leaves the same net set of covered rooms either way.</summary>
    public void RemoveAutoFarmRegion(FarmRegionSummary? summary)
    {
        if (summary is null)
        {
            return;
        }

        var regions = _autoFarmRegions.ToList();
        if (regions.Remove(summary.Region))
        {
            AutoFarmRegions = regions;
        }
    }

    /// <summary>Bulk-replaces every region at once — used only when reloading from the active
    /// profile on switch (see MainWindowViewModel.ActivateProfile). Drawing/clearing one at a
    /// time go through <see cref="NotifyAutoFarmRegionDrawn"/>/<see cref="ClearAutoFarmRegion"/>
    /// instead.</summary>
    public void SetAutoFarmRegions(IReadOnlyList<FarmRegion> regions) => AutoFarmRegions = regions;

    public string LordGotoMenuHeader => SelectedRoom is { } room
        ? $"Walk: {(string.IsNullOrWhiteSpace(room.Name) ? "pokój" : room.Name)} [{room.Vnum ?? "brak vnum"}]"
        : "Walk";

    private bool CanLordGotoSelectedRoom() =>
        LordModeEnabled && IsSafeVnum(SelectedRoom?.Vnum);

    private void RequestLordGotoSelectedRoom()
    {
        if (SelectedRoom is { } room && CanLordGotoSelectedRoom())
        {
            LordGotoRequested?.Invoke(room);
        }
    }

    private static bool IsSafeVnum(string? vnum) =>
        !string.IsNullOrWhiteSpace(vnum) && vnum.All(char.IsAsciiDigit);

    public Bitmap? SelectedRoomIcon =>
        RoomImages?.GetFullImage(SelectedRoom?.Vnum)
        ?? TextureCache?.GetTexture(SelectedRoom?.Sector ?? string.Empty);

    public string? CurrentVnum => _locationResolver.CurrentVnum;

    public string CurrentRoomName => CurrentRoom?.Name ?? "(brak)";

    public string CurrentSectorName => _currentSectorName ?? "(brak)";

    public bool IsMapEditorActive => _mapEditor?.IsMapping == true;

    public bool IsMapEditorDirty => _mapEditor?.IsDirty == true;

    public bool IsMapEditorAwaitingRoomInfo => _mapEditor?.IsAwaitingRoomInfo == true;

    public int MapEditorStep => _mapEditor?.Step ?? 2;

    public string MapEditorStatus => _mapEditor?.Status ?? "Edytor mapy nie jest jeszcze gotowy.";

    /// <summary>Combined footer text shown only in Lord mode (see MapPanelView's footer bar) —
    /// the load status and the map editor's readiness/state joined into one line.</summary>
    public string LordModeStatusMessage => $"{StatusMessage} oraz {MapEditorStatus}";

    public bool IsUsingWorkingMap
    {
        get => _isUsingWorkingMap;
        private set
        {
            if (SetProperty(ref _isUsingWorkingMap, value))
            {
                OnPropertyChanged(nameof(MapEditorSourceDescription));
            }
        }
    }

    public string MapEditorSourceDescription => IsUsingWorkingMap
        ? IsUsingRecoveryMap
            ? "Źródło: odzyskane niezapisane zmiany mapy roboczej."
            : "Źródło: mapa robocza z katalogu MapEditor."
        : IsUsingRecoveryMap
            ? "Źródło: odzyskane niezapisane zmiany mapy bazowej."
            : "Źródło: aktualna mapa bazowa.";

    public bool IsUsingRecoveryMap
    {
        get => _isUsingRecoveryMap;
        private set
        {
            if (SetProperty(ref _isUsingRecoveryMap, value))
            {
                OnPropertyChanged(nameof(MapEditorSourceDescription));
            }
        }
    }

    public bool FollowPlayer
    {
        get => _followPlayer;
        set => SetProperty(ref _followPlayer, value);
    }

    /// <summary>Group members whose GMCP room can be resolved on the loaded map.</summary>
    public IReadOnlyList<GroupMapMarker> GroupMarkers
    {
        get => _groupMarkers;
        private set => SetProperty(ref _groupMarkers, value);
    }

    public void UpdateGroupMembers(IEnumerable<CharacterGroupMember> members, string? selfName)
    {
        ArgumentNullException.ThrowIfNull(members);

        _groupMembers = members
            .Where(member => !string.Equals(member.Name, selfName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        RefreshGroupMarkers();
    }

    private void RefreshGroupMarkers()
    {
        if (MapIndex is null)
        {
            GroupMarkers = [];
            return;
        }

        GroupMarkers = _groupMembers
            .Select((member, index) => (Member: member, Number: index + 1,
                Room: string.IsNullOrWhiteSpace(member.Room)
                ? null
                : MapIndex.FindFirstRoomByVnum(member.Room)))
            .Where(item => item.Room is not null)
            .Select(item => new GroupMapMarker(
                item.Member.Name,
                item.Member.IsLeader,
                item.Member.IsNpc,
                item.Room!,
                item.Number))
            .ToArray();
    }

    /// <summary>Recorded deaths (see <see cref="MainWindowViewModel.Deaths"/>) whose vnum can be
    /// resolved on the loaded map — drawn directly on the canvas, in addition to the list in the
    /// settings flyout.</summary>
    public IReadOnlyList<DeathMapMarker> DeathMarkers
    {
        get => _deathMarkers;
        private set => SetProperty(ref _deathMarkers, value);
    }

    private void RefreshDeathMarkers()
    {
        if (MapIndex is null || MainViewModel is null)
        {
            DeathMarkers = [];
            return;
        }

        DeathMarkers = MainViewModel.Deaths
            .Select(death => (Death: death, Room: MapIndex.FindFirstRoomByVnum(death.Vnum)))
            .Where(item => item.Room is not null)
            .Select(item => new DeathMapMarker(item.Room!, item.Death.Display, item.Death.When))
            .ToArray();
    }

    /// <summary>Player-placed local markers (see <see cref="SetMarkerOnSelectedRoomCommand"/>)
    /// merged with an auto "T" marker for every known Killeropedia teacher room (see
    /// <see cref="RefreshTeacherMarkers"/>), an auto "B" marker for every known spellbook-mob
    /// room (see <see cref="RefreshSpellMobMarkers"/>), and finally the community's
    /// currently-accepted markers (see <see cref="SharedMapMarkerStore"/>) — each layer only
    /// filling in rooms the previous ones haven't already claimed, resolved to their rooms so
    /// <see cref="Controls.WorldMapControl"/> can draw them directly.</summary>
    public IReadOnlyList<RoomMapMarker> RoomMarkers
    {
        get => _roomMarkers;
        private set => SetProperty(ref _roomMarkers, value);
    }

    private void RefreshRoomMarkers()
    {
        if (MapIndex is null)
        {
            RoomMarkers = [];
            _findNearestRentCommand.NotifyCanExecuteChanged();
            return;
        }

        var explicitMarkers = _markersByVnum.Values
            .Select(marker => (Marker: marker, Room: MapIndex.FindFirstRoomByVnum(marker.Vnum)))
            .Where(item => item.Room is not null)
            .Select(item => new RoomMapMarker(item.Room!, item.Marker.Symbol, item.Marker.Note))
            .ToArray();

        var claimedRoomIds = explicitMarkers.Select(marker => marker.Room.Id).ToHashSet();

        var autoTeacherMarkers = TeacherMarkers
            .Where(teacher => !claimedRoomIds.Contains(teacher.Room.Id))
            .Select(teacher => new RoomMapMarker(teacher.Room, TeacherMarkerSymbol))
            .ToArray();
        claimedRoomIds.UnionWith(autoTeacherMarkers.Select(marker => marker.Room.Id));

        var autoSpellMobMarkers = SpellMobMarkers
            .Where(spellMob => !claimedRoomIds.Contains(spellMob.Room.Id))
            .Select(spellMob => new RoomMapMarker(spellMob.Room, SpellMobMarkerSymbol))
            .ToArray();
        claimedRoomIds.UnionWith(autoSpellMobMarkers.Select(marker => marker.Room.Id));

        var autoSharedMarkers = _sharedMarkerCatalog
            .Select(marker => (Marker: marker, Room: MapIndex.FindFirstRoomByVnum(marker.Vnum)))
            .Where(item => item.Room is not null && !claimedRoomIds.Contains(item.Room!.Id))
            .Select(item => new RoomMapMarker(item.Room!, item.Marker.Symbol, item.Marker.Note));

        RoomMarkers = explicitMarkers
            .Concat(autoTeacherMarkers)
            .Concat(autoSpellMobMarkers)
            .Concat(autoSharedMarkers)
            .ToArray();
        _findNearestRentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The full known-teacher catalog, independent of whether their room resolved on
    /// the loaded map (unlike <see cref="TeacherMarkers"/>) — used by
    /// <see cref="Services.SkillTrainerAnnotator"/> to annotate the "skill" command's output with
    /// who can still train each skill further.</summary>
    public IReadOnlyList<TeacherEntry> TeacherCatalog => _teacherCatalog;

    /// <summary>Killeropedia teachers resolved to their map room, grouped per room — feeds both
    /// the auto "T" marker merged into <see cref="RoomMarkers"/> and the hover tooltip in
    /// <see cref="Controls.WorldMapControl"/> that lists what each teacher trains.</summary>
    public IReadOnlyList<TeacherMapMarker> TeacherMarkers
    {
        get => _teacherMarkers;
        private set => SetProperty(ref _teacherMarkers, value);
    }

    private void RefreshTeacherMarkers()
    {
        if (MapIndex is null)
        {
            TeacherMarkers = [];
            return;
        }

        TeacherMarkers = _teacherCatalog
            .Where(teacher => !string.IsNullOrWhiteSpace(teacher.RoomVnum))
            .Select(teacher => (Teacher: teacher, Room: MapIndex.FindFirstRoomByVnum(teacher.RoomVnum!)))
            .Where(item => item.Room is not null)
            .GroupBy(item => item.Room!.Id)
            .Select(group => new TeacherMapMarker(group.First().Room!, group.Select(item => item.Teacher).ToArray()))
            .ToArray();
    }

    /// <summary>Everything a <see cref="MapSearchEntry"/> should match on for a teacher: their
    /// own name, plus every skill/trick they teach — so searching a spell like "cyclone" or a
    /// range like "65" surfaces the teacher who trains it, not just their name.</summary>
    private static string BuildTeacherSearchText(TeacherEntry teacher)
    {
        var parts = new List<string> { teacher.Name };
        parts.AddRange(teacher.Skills.Select(skill => $"{skill.Name} {skill.RangeText}"));
        parts.AddRange(teacher.Tricks.Select(trick => $"{trick.Name} {trick.EnhancesText}"));
        return string.Join(" | ", parts);
    }

    /// <summary>The full known spellbook-mob catalog, independent of whether their room resolved
    /// on the loaded map (unlike <see cref="SpellMobMarkers"/>) — used by
    /// <see cref="Services.SpellSourceAnnotator"/> to annotate the "spell" command's output with
    /// who drops the book for each spell the player is still missing.</summary>
    public IReadOnlyList<SpellMobEntry> SpellMobCatalog => _spellMobCatalog;

    /// <summary>Spellbook-dropping mobs resolved to their map room, grouped per room — feeds both
    /// the auto "B" marker merged into <see cref="RoomMarkers"/> and the hover tooltip in
    /// <see cref="Controls.WorldMapControl"/> that lists what each mob's book teaches.</summary>
    public IReadOnlyList<SpellMobMapMarker> SpellMobMarkers
    {
        get => _spellMobMarkers;
        private set => SetProperty(ref _spellMobMarkers, value);
    }

    /// <summary>This character's spell name -&gt; known/missing map, set by
    /// <see cref="MainWindowViewModel"/> from <see cref="Models.ProfileSpellEntry"/> as "spell"/
    /// "spell all" output is seen (and reloaded on profile switch). Consumed by
    /// <see cref="Controls.WorldMapControl"/> — via <see cref="Services.SpellKnowledgeClassifier"/>
    /// — to color-code each spell in a "B" marker's tooltip. Empty (never null) until any spell
    /// data has been collected for the active character.</summary>
    public IReadOnlyDictionary<string, bool> SpellKnowledge
    {
        get => _spellKnowledge;
        set => SetProperty(ref _spellKnowledge, value ?? EmptySpellKnowledge);
    }

    /// <summary>Room ids whose spell-mob teaches at least one spell not yet known — see
    /// <see cref="Services.SpellKnowledgeClassifier"/>. Recomputed on every access from
    /// <see cref="SpellMobMarkers"/>/<see cref="SpellKnowledge"/>, so it's always current;
    /// consumed by <see cref="Controls.WorldMapControl.RoomsWithMissingSpell"/> to color
    /// currently-visible rooms gold/orange.</summary>
    public IReadOnlySet<int> RoomsWithMissingSpell => SpellMobMarkers
        .Where(marker => marker.Mobs.Any(mob => mob.Spells.Any(spell =>
            SpellKnowledgeClassifier.Classify(spell, SpellKnowledge) == SpellKnowledgeState.Missing)))
        .Select(marker => marker.Room.Id)
        .ToHashSet();

    /// <summary>This character's skill name -&gt; current level map, set by
    /// <see cref="MainWindowViewModel"/> from <see cref="Models.ProfileSkillEntry"/> as "skill"
    /// output is seen (and reloaded on profile switch). Consumed by
    /// <see cref="Controls.WorldMapControl"/> — via <see cref="Services.SkillKnowledgeClassifier"/>
    /// — to color-code each skill in a "T" marker's tooltip. Empty (never null) until any skill
    /// data has been collected for the active character.</summary>
    public IReadOnlyDictionary<string, int> SkillKnowledge
    {
        get => _skillKnowledge;
        set => SetProperty(ref _skillKnowledge, value ?? EmptySkillKnowledge);
    }

    private void RefreshSpellMobMarkers()
    {
        if (MapIndex is null)
        {
            SpellMobMarkers = [];
            return;
        }

        SpellMobMarkers = _spellMobCatalog
            .Where(spellMob => spellMob.HasRoomLocation)
            .Select(spellMob => (SpellMob: spellMob, Room: MapIndex.FindFirstRoomByVnum(spellMob.RoomVnum!)))
            .Where(item => item.Room is not null)
            .GroupBy(item => item.Room!.Id)
            .Select(group => new SpellMobMapMarker(group.First().Room!, group.Select(item => item.SpellMob).ToArray()))
            .ToArray();
    }

    /// <summary>Everything a <see cref="MapSearchEntry"/> should match on for a spellbook mob:
    /// its own name, plus every spell its book teaches — so searching "cyclone" or "Rogaty demon"
    /// both find it.</summary>
    private static string BuildSpellMobSearchText(SpellMobEntry mob) =>
        mob.Spells.Count == 0 ? mob.Mob : $"{mob.Mob} | {string.Join(" | ", mob.Spells)}";

    /// <summary>Closed, autocompleting list backing the map's "Szukaj..." dialog — one entry per
    /// teacher (see <see cref="TeacherMarkers"/>) and one per spellbook mob (see
    /// <see cref="SpellMobMarkers"/>) whose room resolved on the loaded map. See
    /// <see cref="MapSearchEntry"/> for what's actually searchable.</summary>
    public IReadOnlyList<MapSearchEntry> SearchEntries
    {
        get => _searchEntries;
        private set => SetProperty(ref _searchEntries, value);
    }

    private void RefreshSearchEntries()
    {
        var teacherEntries = TeacherMarkers
            .SelectMany(marker => marker.Teachers)
            .Select(teacher => new MapSearchEntry(teacher.Name, BuildTeacherSearchText(teacher)));

        var spellMobEntries = SpellMobMarkers
            .SelectMany(marker => marker.Mobs)
            .Select(mob => new MapSearchEntry(mob.Mob, BuildSpellMobSearchText(mob)));

        SearchEntries = teacherEntries
            .Concat(spellMobEntries)
            .OrderBy(entry => entry.Name, StringComparer.Create(CultureInfo.GetCultureInfo("pl-PL"), true))
            .ToArray();
    }

    /// <summary>Symbol used for the "Rent" marker (see <see cref="MarkerLegend"/>) — the one
    /// <see cref="FindNearestRentCommand"/> searches for.</summary>
    private const string RentMarkerSymbol = "R";

    /// <summary>Symbol auto-applied to every known Killeropedia teacher's room (see
    /// <see cref="RefreshTeacherMarkers"/>) — same symbol as the manual "T — Nauczyciel" legend
    /// entry, since Killeropedia already knows these locations without the player marking them.</summary>
    private const string TeacherMarkerSymbol = "T";

    /// <summary>Symbol auto-applied to every known spellbook-mob's room (see
    /// <see cref="RefreshSpellMobMarkers"/>) — same symbol as the manual "B — Księga" legend
    /// entry, since the community-sourced catalog already knows these locations without the
    /// player marking them.</summary>
    private const string SpellMobMarkerSymbol = "B";

    /// <summary>
    /// The fixed set of marker symbols, shown both in the map's right-click "Dodaj znacznik"
    /// submenu and as a read-only legend in the map settings flyout. Phase 1 offers no way to
    /// add symbols beyond this list.
    /// </summary>
    public static IReadOnlyList<MarkerLegendEntry> MarkerLegend { get; } =
    [
        new("R", "Rent"),
        new("@", "Oaza"),
        new("!", "Niebezpieczeństwo (np. słaby agresywny mob)"),
        new("!!", "Wielkie niebezpieczeństwo"),
        new("X", "Zamknięte"),
        new("#", "Przepaść"),
        new("T", "Nauczyciel"),
        new("B", "Księga"),
        new("+", "Sklep"),
        new("Q", "Zadanie"),
        new("D", "Drzwi"),
        new("?", "Inne..."),
    ];

    public bool CanEditSelectedRoomMarker => !string.IsNullOrWhiteSpace(SelectedRoom?.Vnum);

    public bool SelectedRoomHasMarker =>
        SelectedRoom?.Vnum is { } vnum && _markersByVnum.ContainsKey(vnum);

    /// <summary>The selected room's current note, if any — read by the "Notatka..." dialog to
    /// prefill the text box with what's already there (see <see cref="SetNoteOnSelectedRoom"/>).</summary>
    public string? SelectedRoomNote =>
        SelectedRoom?.Vnum is { } vnum && _markersByVnum.TryGetValue(vnum, out var marker) ? marker.Note : null;

    private void SetMarkerOnSelectedRoom(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || SelectedRoom?.Vnum is not { } vnum || string.IsNullOrWhiteSpace(vnum))
        {
            return;
        }

        // Picking a symbol replaces the symbol but keeps whatever note was already there — a
        // room can carry both, e.g. a "Q" quest room with your own reminder text.
        var note = _markersByVnum.TryGetValue(vnum, out var existing) ? existing.Note : null;
        _markersByVnum[vnum] = new MapMarker(vnum, symbol, note);
        OnMarkersChanged();
    }

    /// <summary>Sets, replaces, or clears the selected room's free-text note (see
    /// <see cref="Models.MapMarker.Note"/>) — driven by MapPanelView's "Notatka..." dialog.
    /// Blank/null clears the note but keeps the room's existing symbol, if any; if the room had no
    /// marker at all yet, a blank note is simply a no-op rather than creating an empty one. A new
    /// note on a room with no marker of the player's own keeps whatever symbol is already showing
    /// there — an auto "T"/"B" badge (Killeropedia teacher/spellbook mob) or a shared-catalog
    /// symbol — falling back to "?" (Inne...) only when the room has no symbol at all yet. Without
    /// this, adding a note to e.g. a teacher's room would silently swap its "T" for a bare "?",
    /// since that badge was never an *explicit* marker of the player's own to begin with.</summary>
    public void SetNoteOnSelectedRoom(string? note)
    {
        if (SelectedRoom is not { } selectedRoom || string.IsNullOrWhiteSpace(selectedRoom.Vnum))
        {
            return;
        }

        var vnum = selectedRoom.Vnum;
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var hasExisting = _markersByVnum.TryGetValue(vnum, out var existing);
        if (trimmed is null && !hasExisting)
        {
            return;
        }

        var symbol = hasExisting
            ? existing!.Symbol
            : RoomMarkers.FirstOrDefault(marker => marker.Room.Id == selectedRoom.Id)?.Symbol ?? "?";
        _markersByVnum[vnum] = new MapMarker(vnum, symbol, trimmed);
        OnMarkersChanged();
    }

    private void RemoveMarkerFromSelectedRoom()
    {
        if (SelectedRoom?.Vnum is not { } vnum || !_markersByVnum.Remove(vnum))
        {
            return;
        }

        OnMarkersChanged();
    }

    /// <summary>Whether <see cref="CutMarkerFromSelectedRoomCommand"/> has something waiting to be
    /// dropped onto another room via <see cref="PasteMarkerOnSelectedRoomCommand"/>.</summary>
    public bool HasCutMarker => _cutMarker is not null;

    /// <summary>Removes the selected room's marker (symbol and note both) and holds onto it so
    /// <see cref="PasteMarkerOnSelectedRoomCommand"/> can drop it onto the correct room instead —
    /// for fixing a marker placed on the wrong room without retyping its note. See
    /// <see cref="_cutMarker"/>'s own doc comment for why a paste doesn't consume it.</summary>
    private void CutMarkerOnSelectedRoom()
    {
        if (SelectedRoom?.Vnum is not { } vnum || !_markersByVnum.TryGetValue(vnum, out var marker))
        {
            return;
        }

        _cutMarker = marker;
        _markersByVnum.Remove(vnum);
        OnPropertyChanged(nameof(HasCutMarker));
        _pasteMarkerOnSelectedRoomCommand.NotifyCanExecuteChanged();
        OnMarkersChanged();
    }

    /// <summary>Drops the marker held by <see cref="CutMarkerFromSelectedRoomCommand"/> onto the
    /// selected room, replacing whatever marker (and note) is already there. Does not clear
    /// <see cref="_cutMarker"/> — see its own doc comment.</summary>
    private void PasteMarkerOnSelectedRoom()
    {
        if (_cutMarker is not { } cut || SelectedRoom?.Vnum is not { } vnum || string.IsNullOrWhiteSpace(vnum))
        {
            return;
        }

        _markersByVnum[vnum] = cut with { Vnum = vnum };
        OnMarkersChanged();
    }

    private void OnMarkersChanged()
    {
        RefreshRoomMarkers();
        OnPropertyChanged(nameof(SelectedRoomHasMarker));
        OnPropertyChanged(nameof(SelectedRoomNote));
        _removeMarkerFromSelectedRoomCommand.NotifyCanExecuteChanged();
        _cutMarkerFromSelectedRoomCommand.NotifyCanExecuteChanged();
        _reportMarkersCommand.NotifyCanExecuteChanged();
        _findNearestRentCommand.NotifyCanExecuteChanged();

        if (_markerStore is null)
        {
            return;
        }

        _ = _markerStore.SaveAsync(new MapMarkerDocument { Markers = _markersByVnum.Values.ToList() });
    }

    /// <summary>Points "Zgłoś wszystko" at this fork's own issue tracker — the maintainer reviews
    /// and manually merges accepted proposals into the bundled shared-markers dataset (see
    /// SharedMapMarkerStore); there is no automated write path from the client.</summary>
    private static readonly Uri MarkerReportRepositoryIssuesUri =
        new("https://github.com/Grzyboll/killer-mud-client/issues/new");

    /// <summary>
    /// Compares this player's local markers against the community's currently-accepted set and,
    /// if anything is new or different, opens a pre-filled GitHub issue for the maintainer to
    /// review — a single bulk report rather than one per marker, and never resubmits a marker
    /// that's already known unchanged.
    /// </summary>
    private void ReportMarkers()
    {
        MapMarkerDocument shared;
        try
        {
            shared = _sharedMarkerStore.Load();
        }
        catch (InvalidDataException exception)
        {
            MainViewModel?.AddToast(
                $"Nie udało się wczytać wspólnego katalogu znaczników: {exception.Message}", "error");
            return;
        }

        var sharedByVnum = shared.Markers.ToDictionary(marker => marker.Vnum, StringComparer.Ordinal);
        var diff = ComputeMarkerReportDiff(_markersByVnum, sharedByVnum);
        if (diff.Count == 0)
        {
            MainViewModel?.AddToast(
                "Wszystkie Twoje znaczniki są już znane społeczności — nic do zgłoszenia.", "info");
            return;
        }

        MainViewModel?.OpenExternalLink(BuildMarkerReportIssueUri(diff));
    }

    /// <summary>Pure decision behind <see cref="ReportMarkers"/>: a vnum missing from
    /// <paramref name="shared"/>, or present with a different symbol or note, needs reporting; an
    /// already-accepted, unchanged marker never gets resubmitted.</summary>
    internal static IReadOnlyList<MapMarkerReportEntry> ComputeMarkerReportDiff(
        IReadOnlyDictionary<string, MapMarker> local,
        IReadOnlyDictionary<string, MapMarker> shared)
    {
        var entries = new List<MapMarkerReportEntry>();
        foreach (var marker in local.Values)
        {
            if (!shared.TryGetValue(marker.Vnum, out var sharedMarker))
            {
                entries.Add(new MapMarkerReportEntry(marker.Vnum, marker.Symbol, PreviousSymbol: null, marker.Note));
                continue;
            }

            var symbolChanged = !string.Equals(sharedMarker.Symbol, marker.Symbol, StringComparison.Ordinal);
            var noteChanged = !string.Equals(sharedMarker.Note, marker.Note, StringComparison.Ordinal);
            if (symbolChanged || noteChanged)
            {
                entries.Add(new MapMarkerReportEntry(marker.Vnum, marker.Symbol, sharedMarker.Symbol, marker.Note));
            }
        }

        return entries.OrderBy(entry => entry.Vnum, StringComparer.Ordinal).ToList();
    }

    /// <summary>Builds a "github.com/.../issues/new?title=...&amp;body=..." link with the diff
    /// pre-filled, so the player only has to review and submit it in their own browser/account —
    /// no token or write access is ever needed inside the client itself.</summary>
    internal static Uri BuildMarkerReportIssueUri(IReadOnlyList<MapMarkerReportEntry> entries)
    {
        var lines = entries.Select(entry =>
        {
            var symbolPart = entry.PreviousSymbol is null
                ? $"[NOWY] vnum {entry.Vnum} -> {entry.NewSymbol}"
                : $"[ZMIANA] vnum {entry.Vnum}: {entry.PreviousSymbol} -> {entry.NewSymbol}";
            return string.IsNullOrWhiteSpace(entry.Note)
                ? $"- {symbolPart}"
                : $"- {symbolPart}; notatka: \"{entry.Note}\"";
        });
        var body = "Propozycja znaczników mapy (wygenerowane automatycznie przez klienta):\n\n"
            + string.Join('\n', lines)
            + "\n\nFormat: vnum -> symbol. Legenda: "
            + string.Join(", ", MarkerLegend.Select(entry => $"{entry.Symbol}={entry.Label}"));

        var query = $"title={Uri.EscapeDataString("Propozycja znaczników mapy")}&body={Uri.EscapeDataString(body)}";
        return new Uri($"{MarkerReportRepositoryIssuesUri}?{query}");
    }

    /// <summary>
    /// Finds the closest "R" (Rent) marker to the player's current room — searching the same
    /// fully-merged set actually drawn on the map (<see cref="RoomMarkers"/>: explicit player
    /// markers, then auto "T"/"B", then the community's shared catalog), not just this player's
    /// own local markers — and focuses it the same way "Szukaj pokój" does (see
    /// <see cref="FocusRoom"/>): centers/selects it without engaging follow-player mode, leaving
    /// "double-click to walk there" to the map's existing double-click handling.
    /// </summary>
    private void FindNearestRent()
    {
        if (MapIndex is null || CurrentRoom is not { } current)
        {
            MainViewModel?.AddToast(
                "Nie znam aktualnej pozycji postaci — połącz się i zlokalizuj się na mapie.", "error");
            return;
        }

        if (FindNearestRentMarker(RoomMarkers, current) is not { } marker)
        {
            MainViewModel?.AddToast("Nie znaleziono żadnego renta (R) w tej okolicy.", "error");
            return;
        }

        FocusRoom(marker.Room);
    }

    /// <summary>Pure decision behind <see cref="FindNearestRent"/>: the "R" marker whose room is
    /// closest to <paramref name="current"/> by straight-line map distance, restricted to the
    /// same area and floor — a rent in a different area isn't meaningfully "nearest" even if its
    /// raw coordinates happen to be close, since areas don't share a coordinate space.</summary>
    internal static RoomMapMarker? FindNearestRentMarker(
        IReadOnlyList<RoomMapMarker> roomMarkers,
        MapRoom current)
    {
        RoomMapMarker? nearest = null;
        var nearestDistanceSquared = double.MaxValue;

        foreach (var marker in roomMarkers)
        {
            if (marker.Symbol != RentMarkerSymbol
                || marker.Room.AreaId != current.AreaId
                || marker.Room.Coordinates.Z != current.Coordinates.Z)
            {
                continue;
            }

            var dx = marker.Room.Coordinates.X - current.Coordinates.X;
            var dy = marker.Room.Coordinates.Y - current.Coordinates.Y;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearest = marker;
            }
        }

        return nearest;
    }

    public IReadOnlyList<MapDisplayModeOption> DisplayModes { get; } = MapDisplayModeOption.All;

    public event Action<MapDisplayMode>? DisplayModeChanged;

    public MapDisplayModeOption SelectedDisplayMode
    {
        get => _selectedDisplayMode;
        set
        {
            if (SetProperty(ref _selectedDisplayMode, value))
            {
                DisplayModeChanged?.Invoke(value.Mode);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _loadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCancellation = cancellation;

        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "Ładowanie mapy…";

        try
        {
            var downloadedMapDirectory = _contentPaths?.GetActiveDirectory("map");
            if (downloadedMapDirectory is not null)
            {
                try
                {
                    _ = await new MapLoader().LoadAsync(
                            Path.Combine(downloadedMapDirectory, "world-map.json"),
                            cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is MapLoadException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    // A damaged downloaded map must not hide the packaged fallback.
                    System.Diagnostics.Trace.WriteLine(exception);
                    downloadedMapDirectory = null;
                }
            }

            SetMapPaths(downloadedMapDirectory ?? _packagedMapDirectory);
            var settingsLoader = new MapSettingsLoader();
            Settings = await settingsLoader.LoadAsync(_mapSettingsPath, cancellation.Token).ConfigureAwait(false);

            TextureCache?.Dispose();
            TextureCache = new SectorTextureCache(_sectorDirectory, _sectorManifestPath);

            RoomImages?.Dispose();
            RoomImages = new RoomImageCache(_roomImageDirectory);

            var mapPathToLoad = _baseWorldMapPath;
            var useWorkingMap = false;
            if (_mapEditorPath is not null && File.Exists(_mapEditorPath))
            {
                try
                {
                    _ = await new MapLoader().LoadAsync(_mapEditorPath, cancellation.Token).ConfigureAwait(false);
                    mapPathToLoad = _mapEditorPath;
                    useWorkingMap = true;
                }
                catch (Exception exception) when (exception is MapLoadException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    // A damaged optional working map must not hide the current content map.
                    System.Diagnostics.Trace.WriteLine(exception);
                }
            }

            _worldMapPath = mapPathToLoad;

            if (!File.Exists(_worldMapPath))
            {
                ErrorMessage = $"Nie znaleziono pliku mapy: {_worldMapPath}";
                StatusMessage = "Brak pliku mapy.";
                return;
            }

            var loader = new MapLoader();
            var result = await loader.LoadAsync(_worldMapPath, cancellation.Token).ConfigureAwait(false);
            var recovery = _mapEditorRecoveryStore is null
                ? null
                : await _mapEditorRecoveryStore.LoadAsync(cancellation.Token).ConfigureAwait(false);
            var baselineIdentity = GetMapBaselineIdentity();
            var recoveryMatchesBaseline = recovery is not null &&
                                          string.Equals(
                                              recovery.BaselineIdentity,
                                              baselineIdentity,
                                              StringComparison.OrdinalIgnoreCase);
            var recoveredDirtyMap = recovery?.IsDirty == true;
            var editorDocument = recoveredDirtyMap ? recovery!.Current! : result.Document;
            var undoHistory = recovery is not null && (recoveredDirtyMap || recoveryMatchesBaseline)
                ? recovery.UndoHistory
                : [];
            _mapEditor = new MapEditorSession(editorDocument, undoHistory, recoveredDirtyMap);
            _moveExistingRoomsToNewArea = false;

            var index = new MapIndex(editorDocument, Settings.SpatialBucketSize);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MapIndex = index;
                Areas.Clear();
                foreach (var area in index.Document.Areas)
                {
                    Areas.Add(area);
                }

                var defaultArea = Areas.FirstOrDefault();
                if (defaultArea is not null)
                {
                    SetSelectedAreaInternal(defaultArea);
                }

                var roomCount = index.RoomsById.Count;
                var warningSuffix = result.Warnings.Count > 0
                    ? $" ({result.Warnings.Count} ostrzeżeń pominiętych pokoi)"
                    : string.Empty;

                StatusMessage = $"Załadowano {index.Document.Areas.Count} obszarów, {roomCount} pokoi{warningSuffix}.";
                IsUsingWorkingMap = useWorkingMap;
                IsUsingRecoveryMap = recoveredDirtyMap;
                OnPropertyChanged(nameof(MoveExistingRoomsToNewArea));
                NotifyMapEditorStateChanged();
            });

            TryResolveCurrentRoom();
        }
        catch (MapLoadException exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Błąd ładowania mapy.";
            System.Diagnostics.Trace.WriteLine(exception);
        }
        catch (OperationCanceledException)
        {
            // Load was superseded by a newer request.
        }
        catch (Exception exception)
        {
            ErrorMessage = "Nieoczekiwany błąd podczas ładowania mapy.";
            StatusMessage = ErrorMessage;
            System.Diagnostics.Trace.WriteLine(exception);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RefreshZLevels()
    {
        ZLevels.Clear();

        if (SelectedArea is null || MapIndex is null)
        {
            return;
        }

        foreach (var z in MapIndex.GetZLevels(SelectedArea.Id))
        {
            ZLevels.Add(z);
        }

        if (ZLevels.Count > 0 && !ZLevels.Contains(SelectedZ))
        {
            SelectedZ = ZLevels[0];
        }

        OnPropertyChanged(nameof(SelectedZIndex));
    }

    /// <summary>
    /// Refreshes Z levels and auto-selects a valid default Z without using
    /// the public <see cref="SelectedZ"/> setter, so <see cref="FollowPlayer"/>
    /// is not affected. Used during automatic (programmatic) area/Z updates.
    /// </summary>
    private void RefreshZLevelsInternal()
    {
        ZLevels.Clear();

        if (SelectedArea is null || MapIndex is null)
        {
            return;
        }

        foreach (var z in MapIndex.GetZLevels(SelectedArea.Id))
        {
            ZLevels.Add(z);
        }

        if (ZLevels.Count > 0 && !ZLevels.Contains(_selectedZ))
        {
            _selectedZ = ZLevels[0];
            OnPropertyChanged(nameof(SelectedZ));
        }

        OnPropertyChanged(nameof(SelectedZIndex));
    }

    private void SetMapPaths(string mapDirectory)
    {
        _baseWorldMapPath = Path.Combine(mapDirectory, "world-map.json");
        _worldMapPath = _baseWorldMapPath;
        _mapSettingsPath = Path.Combine(mapDirectory, "map-settings.json");
        _sectorDirectory = Path.Combine(mapDirectory, "Sectors");
        _sectorManifestPath = Path.Combine(_sectorDirectory, "sectors.json");
        _roomImageDirectory = Path.Combine(mapDirectory, "Rooms");
    }

    /// <summary>
    /// Sets the selected area without disabling follow-player mode.
    /// Use only for programmatic updates driven by current-room resolution or centering.
    /// </summary>
    private void SetSelectedAreaInternal(MapArea area)
    {
        if (SetProperty(ref _selectedArea, area, nameof(SelectedArea)) && area is not null)
        {
            RefreshZLevelsInternal();
        }
    }

    /// <summary>
    /// Sets the selected Z without disabling follow-player mode.
    /// Use only for programmatic updates driven by current-room resolution or centering.
    /// </summary>
    private void SetSelectedZInternal(double z)
    {
        if (SetProperty(ref _selectedZ, z, nameof(SelectedZ)))
        {
            OnPropertyChanged(nameof(SelectedZIndex));
        }
    }

    private void FocusFirstRoom(MapArea area)
    {
        var room = area.Rooms.FirstOrDefault();
        if (room is null)
        {
            return;
        }

        SetSelectedZInternal(room.Coordinates.Z);
        SelectedRoom = room;
        CenterOnRoomRequested?.Invoke(room);
    }

    private void OnLocationChanged(string vnum)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CurrentVnum));
            TryResolveCurrentRoom();
            NotifyMapEditorCommands();
        });
    }

    private void TryResolveCurrentRoom()
    {
        if (MapIndex is null)
        {
            return;
        }

        var vnum = _locationResolver.CurrentVnum;
        if (vnum is null)
        {
            return;
        }

        var room = MapIndex.FindFirstRoomByVnum(vnum);

        if (room is null)
        {
            CurrentRoom = null;
            _currentSectorName = null;
            OnPropertyChanged(nameof(CurrentRoomName));
            OnPropertyChanged(nameof(CurrentSectorName));
            StatusMessage = $"VNUM {vnum} nie istnieje w mapie.";
            return;
        }

        CurrentRoom = room;
        _currentSectorName = room.Sector;
        OnPropertyChanged(nameof(CurrentRoomName));
        OnPropertyChanged(nameof(CurrentSectorName));

        var area = MapIndex.AreasById.GetValueOrDefault(room.AreaId);
        if (area is not null && !ReferenceEquals(SelectedArea, area))
        {
            SetSelectedAreaInternal(area);
        }

        SetSelectedZInternal(room.Coordinates.Z);

        // Keep the details panel and icon in sync with the current room during
        // walking. Clicking a room on the map still sets SelectedRoom through
        // OnRoomClicked, but the next GMCP walking update switches the
        // selection/image back to the current room.
        if (!ReferenceEquals(SelectedRoom, room))
        {
            SelectedRoom = room;
        }

        FollowPlayer = true;
        CenterOnCurrentRoomRequested?.Invoke();
        NotifyMapEditorCommands();
    }

    public void CenterOnPlayer()
    {
        if (CurrentRoom is null)
        {
            return;
        }

        var area = MapIndex?.AreasById.GetValueOrDefault(CurrentRoom.AreaId);
        if (area is not null)
        {
            SetSelectedAreaInternal(area);
        }

        SetSelectedZInternal(CurrentRoom.Coordinates.Z);

        FollowPlayer = true;
        CenterOnCurrentRoomRequested?.Invoke();
    }

    /// <summary>
    /// Selects and centers a mapped room without enabling follow-player mode.
    /// Returns null when the vnum is not present in the loaded map.
    /// </summary>
    public MapRoom? FocusRoomByVnum(string vnum) =>
        MapIndex?.FindFirstRoomByVnum(vnum) is { } room ? FocusRoom(room) : null;

    /// <summary>Finds a Killeropedia teacher by name — case-insensitive substring match, so e.g.
    /// "barbarzyński" matches "Mistrz barbarzyński" — among <see cref="TeacherMarkers"/> and
    /// focuses their room the same way "Szukaj pokój" does. Returns null when no known teacher's
    /// name matches (this only searches teachers whose room already resolved on the loaded map —
    /// see <see cref="RefreshTeacherMarkers"/>).</summary>
    public MapRoom? FocusTeacherByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var match = TeacherMarkers.FirstOrDefault(marker => marker.Teachers.Any(
            teacher => teacher.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)));
        return match is null ? null : FocusRoom(match.Room);
    }

    /// <summary>Finds a spellbook-dropping mob by name — case-insensitive substring match, so
    /// e.g. "rogaty" matches "Rogaty demon" — among <see cref="SpellMobMarkers"/> and focuses
    /// their room the same way "Szukaj pokój" does. Returns null when no known mob's name
    /// matches (this only searches mobs whose room already resolved on the loaded map — see
    /// <see cref="RefreshSpellMobMarkers"/>).</summary>
    public MapRoom? FocusSpellMobByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var match = SpellMobMarkers.FirstOrDefault(marker => marker.Mobs.Any(
            mob => mob.Mob.Contains(trimmed, StringComparison.OrdinalIgnoreCase)));
        return match is null ? null : FocusRoom(match.Room);
    }

    /// <summary>What the map's "Szukaj..." dialog actually calls: tries a teacher match first
    /// (see <see cref="FocusTeacherByName"/>), then a spellbook mob (see
    /// <see cref="FocusSpellMobByName"/>). Returns null only when neither matches.</summary>
    public MapRoom? FocusSearchResultByName(string name) =>
        FocusTeacherByName(name) ?? FocusSpellMobByName(name);

    /// <summary>Shared by <see cref="FocusRoomByVnum"/>/<see cref="FocusTeacherByName"/>/
    /// <see cref="FocusSpellMobByName"/>/<see cref="FindNearestRent"/>: selects and centers a
    /// room without enabling follow-player mode.</summary>
    private MapRoom FocusRoom(MapRoom room)
    {
        if (MapIndex?.AreasById.GetValueOrDefault(room.AreaId) is { } area)
        {
            SetSelectedAreaInternal(area);
        }

        SetSelectedZInternal(room.Coordinates.Z);
        SelectedRoom = room;
        FollowPlayer = false;
        CenterOnRoomRequested?.Invoke(room);
        return room;
    }

    public MapEditorCommandDecision PrepareMapEditorCommand(string command)
    {
        if (_mapEditor is null)
        {
            return new MapEditorCommandDecision(true, command);
        }

        var decision = _mapEditor.PrepareManualCommand(command);
        if (decision.Allow && _mapEditor.IsAwaitingRoomInfo)
        {
            StartMapMovementTimeout();
        }
        NotifyMapEditorStateChanged();
        return decision;
    }

    public bool SetMapEditorStep(int step)
    {
        if (_mapEditor is null)
        {
            return false;
        }

        var result = _mapEditor.SetStep(step);
        NotifyMapEditorStateChanged();
        return result;
    }

    public bool CreateMapArea(string name)
    {
        if (_mapEditor?.CreateArea(name) != true)
        {
            NotifyMapEditorStateChanged();
            return false;
        }

        ApplyMapEditorDocument();
        if (Areas.LastOrDefault(area => string.Equals(area.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } area)
        {
            SetSelectedAreaInternal(area);
        }

        NotifyMapEditorStateChanged();
        return true;
    }

    public bool SetMoveExistingRoomsToNewArea(bool enabled)
    {
        if (_mapEditor?.SetMoveKnownRoomsToTargetArea(enabled, SelectedArea?.Id) != true)
        {
            NotifyMapEditorStateChanged();
            return false;
        }

        SetProperty(ref _moveExistingRoomsToNewArea, enabled, nameof(MoveExistingRoomsToNewArea));
        NotifyMapEditorStateChanged();
        return true;
    }

    private bool CanCreateMapAreaFromInput() =>
        LordModeEnabled
        && _mapEditor is not null
        && !IsMapEditorActive
        && !string.IsNullOrWhiteSpace(NewMapAreaName);

    private void CreateMapAreaFromInput()
    {
        if (CreateMapArea(NewMapAreaName))
        {
            NewMapAreaName = string.Empty;
        }
    }

    public bool SetCurrentMapRoomSymbol(string symbol) => ApplyMapEditorOperation(
        editor => editor.SetCurrentRoomSymbol(symbol));

    public bool AddCurrentMapLabel(string text) => ApplyMapEditorOperation(
        editor => editor.AddLabel(text));

    public IReadOnlyList<MapLabel> ShowCurrentAreaMapLabels()
    {
        var labels = _mapEditor?.ShowCurrentAreaLabels() ?? [];
        NotifyMapEditorStateChanged();
        return labels;
    }

    public bool SetMapLabelText(int id, string text) => ApplyMapEditorOperation(
        editor => editor.SetLabelText(id, text));

    public bool RemoveMapLabel(int id) => ApplyMapEditorOperation(
        editor => editor.RemoveLabel(id));

    public bool SetCurrentMapRoomName(string name) => ApplyMapEditorOperation(
        editor => editor.SetCurrentRoomName(name));

    public bool SetCurrentMapRoomSector(string sector) => ApplyMapEditorOperation(
        editor => editor.SetCurrentRoomSector(sector));

    public bool SetCurrentMapRoomWeight(double weight) => ApplyMapEditorOperation(
        editor => editor.SetCurrentRoomWeight(weight));

    public bool MoveCurrentMapRoom(MapCoordinates coordinates) => ApplyMapEditorOperation(
        editor => editor.MoveCurrentRoom(coordinates));

    public bool ForgetCurrentMapRoom()
    {
        var wasActive = IsMapEditorActive;
        var changed = ApplyMapEditorOperation(editor => editor.ForgetCurrentRoom());
        if (changed && wasActive)
        {
            MapEditorActiveChanged?.Invoke(false);
        }

        return changed;
    }

    public bool RemoveMapSpecialExit(string direction) => ApplyMapEditorOperation(
        editor => editor.RemoveSpecialExit(direction));

    public MapEditorCommandDecision PrepareMapSpecialMovement(string direction, string command)
    {
        var decision = _mapEditor?.PrepareSpecialMovement(direction, command)
                       ?? new MapEditorCommandDecision(false, command, "Edytor mapy nie jest gotowy.");
        if (decision.Allow && _mapEditor?.IsAwaitingRoomInfo == true)
        {
            StartMapMovementTimeout();
        }
        NotifyMapEditorStateChanged();
        return decision;
    }

    public void CancelPendingMapMovement(string message)
    {
        CancelMapMovementTimeout();
        _mapEditor?.CancelPendingMovement(message);
        NotifyMapEditorStateChanged();
    }

    public bool CancelMapEditorChanges()
    {
        CancelMapMovementTimeout();
        var changed = _mapEditor?.CancelChanges() == true;
        if (changed)
        {
            ApplyMapEditorDocument();
        }

        NotifyMapEditorStateChanged();
        return changed;
    }

    public bool ResolveMapConflictKeepMap() => ApplyMapEditorOperation(
        editor => editor.ResolveConflictKeepMap(),
        applyDocument: false);

    public bool ResolveMapConflictUseGmcp() => ApplyMapEditorOperation(
        editor => editor.ResolveConflictUseGmcp());

    public async Task<string> GetMapEditorDiffAsync(CancellationToken cancellationToken = default)
    {
        if (_mapEditor is null || !File.Exists(_baseWorldMapPath))
        {
            return "Nie można porównać mapy: brak edytora albo mapy bazowej.";
        }

        try
        {
            var baseline = await new MapLoader().LoadAsync(_baseWorldMapPath, cancellationToken)
                .ConfigureAwait(false);
            return MapDocumentDiffer.Compare(baseline.Document, _mapEditor.Document).ToPolishSummary();
        }
        catch (Exception exception) when (exception is MapLoadException or IOException or UnauthorizedAccessException)
        {
            return $"Nie udało się porównać mapy: {exception.Message}";
        }
    }

    public async Task<string> ExportMapEditorAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (_mapEditor is null)
        {
            return "Edytor mapy nie jest gotowy.";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "Użycie: /map export <ścieżka-do-world-map.json>.";
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                return "Eksport mapy wymaga ścieżki zakończonej rozszerzeniem .json.";
            }

            await new MapWriter().SaveAsync(
                    _mapEditor.Document,
                    fullPath,
                    cancellationToken,
                    baselinePath: _worldMapPath)
                .ConfigureAwait(false);
            return $"Wyeksportowano mapę do {fullPath}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Nie udało się wyeksportować mapy: {exception.Message}";
        }
    }

    public async Task<string> ImportMapEditorAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (_mapEditorPath is null)
        {
            return "Brak katalogu danych dla mapy roboczej.";
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (!File.Exists(fullPath))
            {
                return $"Nie znaleziono pliku mapy: {fullPath}.";
            }

            var imported = await new MapLoader().LoadAsync(fullPath, cancellationToken);
            if (_mapEditorRecoveryStore is not null)
            {
                await _mapEditorRecoveryStore.DeleteAsync(cancellationToken);
            }

            StopMapEditor();
            await new MapWriter().SaveAsync(
                    imported.Document,
                    _mapEditorPath,
                    cancellationToken,
                    baselinePath: fullPath);
            await InitializeAsync(cancellationToken);
            return $"Zaimportowano mapę roboczą z {fullPath}.";
        }
        catch (Exception exception) when (exception is MapLoadException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return $"Nie udało się zaimportować mapy: {exception.Message}";
        }
    }

    public async Task<string> DiscardWorkingMapAsync(CancellationToken cancellationToken = default)
    {
        if (_mapEditorPath is null ||
            (!File.Exists(_mapEditorPath) && _mapEditorRecoveryStore?.Exists != true))
        {
            return "Brak zapisanej mapy roboczej do odrzucenia.";
        }

        StopMapEditor();
        try
        {
            if (File.Exists(_mapEditorPath))
            {
                File.Delete(_mapEditorPath);
            }

            if (_mapEditorRecoveryStore is not null)
            {
                await _mapEditorRecoveryStore.DeleteAsync(cancellationToken);
            }

            await InitializeAsync(cancellationToken);
            return "Odrzucono mapę roboczą i załadowano aktualną mapę bazową.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Nie udało się odrzucić mapy roboczej: {exception.Message}";
        }
    }

    public Task FlushMapEditorRecoveryAsync(CancellationToken cancellationToken = default) =>
        _mapEditorRecoveryStore?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public IReadOnlyList<string> ValidateEditedMap()
    {
        var issues = _mapEditor?.Validate() ?? ["Edytor mapy nie jest gotowy."];
        NotifyMapEditorStateChanged();
        return issues;
    }

    public void ShowCurrentMapRoomInfo()
    {
        _mapEditor?.ShowCurrentRoomInfo();
        NotifyMapEditorStateChanged();
    }

    public void HandleRoomSnapshot(RoomSnapshot snapshot)
    {
        if (_mapEditor is null)
        {
            return;
        }

        CancelMapMovementTimeout();
        var wasActive = IsMapEditorActive;
        if (_mapEditor.ProcessSnapshot(snapshot))
        {
            ApplyMapEditorDocument();
        }
        if (wasActive && !IsMapEditorActive)
        {
            MapEditorActiveChanged?.Invoke(false);
        }

        NotifyMapEditorStateChanged();
    }

    public void StartMapEditor()
    {
        if (_mapEditor is null)
        {
            return;
        }

        if (!LordModeEnabled)
        {
            _mapEditor.Stop();
            NotifyMapEditorStateChanged();
            return;
        }

        var wasActive = IsMapEditorActive;
        var documentBeforeStart = _mapEditor.Document;
        _mapEditor.Start(CurrentVnum);
        if (!ReferenceEquals(documentBeforeStart, _mapEditor.Document))
        {
            ApplyMapEditorDocument();
        }
        if (!wasActive && IsMapEditorActive)
        {
            MapEditorActiveChanged?.Invoke(true);
        }

        NotifyMapEditorStateChanged();
    }

    public void StopMapEditor() => StopMapEditor(null);

    public void StopMapEditor(string? reason)
    {
        CancelMapMovementTimeout();
        var wasActive = IsMapEditorActive;
        _mapEditor?.Stop(reason);
        if (wasActive)
        {
            MapEditorActiveChanged?.Invoke(false);
        }

        NotifyMapEditorStateChanged();
    }

    public void UndoMapEditor()
    {
        if (_mapEditor?.Undo() == true)
        {
            ApplyMapEditorDocument();
        }

        NotifyMapEditorStateChanged();
    }

    public void RedoMapEditor()
    {
        if (_mapEditor?.Redo() == true)
        {
            ApplyMapEditorDocument();
        }

        NotifyMapEditorStateChanged();
    }

    public async Task SaveMapEditorAsync()
    {
        if (_mapEditor is null)
        {
            return;
        }

        if (_mapEditorPath is null)
        {
            StatusMessage = "Brak katalogu danych dla roboczej mapy.";
            return;
        }

        try
        {
            await new MapWriter().SaveAsync(
                    _mapEditor.Document,
                    _mapEditorPath,
                    _loadCancellation?.Token ?? default,
                    baselinePath: _worldMapPath)
                .ConfigureAwait(false);
            _mapEditor.MarkSaved();
            _worldMapPath = _mapEditorPath;
            if (_mapEditorRecoveryStore is not null)
            {
                await _mapEditorRecoveryStore.SaveCheckpointAsync(
                    _mapEditor.Document,
                    _mapEditor.GetUndoHistory(),
                    isDirty: false,
                    baselineIdentity: GetMapBaselineIdentity(),
                    cancellationToken: _loadCancellation?.Token ?? default).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _mapEditor.Stop();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Nie udało się zapisać mapy: {exception.Message}";
            System.Diagnostics.Trace.WriteLine(exception);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsUsingWorkingMap = File.Exists(_mapEditorPath);
            IsUsingRecoveryMap = false;
            NotifyMapEditorStateChanged();
        });
    }

    private bool CanStartMapEditor() =>
        LordModeEnabled
        && _mapEditor is not null
        && !IsMapEditorActive
        && !string.IsNullOrWhiteSpace(CurrentVnum)
        && (CurrentRoom is not null || _mapEditor.HasTargetArea);

    private bool ApplyMapEditorOperation(
        Func<MapEditorSession, bool> operation,
        bool applyDocument = true)
    {
        if (_mapEditor is null || !operation(_mapEditor))
        {
            NotifyMapEditorStateChanged();
            return false;
        }

        if (applyDocument)
        {
            ApplyMapEditorDocument();
        }
        NotifyMapEditorStateChanged();
        return true;
    }

    private void ApplyMapEditorDocument()
    {
        if (_mapEditor is null)
        {
            return;
        }

        var selectedAreaId = SelectedArea?.Id;
        MapIndex = new MapIndex(_mapEditor.Document, Settings.SpatialBucketSize);
        Areas.Clear();
        foreach (var area in MapIndex.Document.Areas)
        {
            Areas.Add(area);
        }

        if (selectedAreaId is not null && MapIndex.AreasById.GetValueOrDefault(selectedAreaId.Value) is { } selectedArea)
        {
            SetSelectedAreaInternal(selectedArea);
        }

        TryResolveCurrentRoom();
        ScheduleMapEditorRecovery();
    }

    private void NotifyMapEditorStateChanged()
    {
        if (_mapEditor is { } editor &&
            _moveExistingRoomsToNewArea != editor.MoveKnownRoomsToTargetArea)
        {
            _moveExistingRoomsToNewArea = editor.MoveKnownRoomsToTargetArea;
        }

        OnPropertyChanged(nameof(IsMapEditorActive));
        OnPropertyChanged(nameof(IsMapEditorDirty));
        OnPropertyChanged(nameof(IsMapEditorAwaitingRoomInfo));
        OnPropertyChanged(nameof(MapEditorStep));
        OnPropertyChanged(nameof(MapEditorStatus));
        OnPropertyChanged(nameof(LordModeStatusMessage));
        OnPropertyChanged(nameof(MapEditorSourceDescription));
        OnPropertyChanged(nameof(CanMoveExistingRoomsToNewArea));
        OnPropertyChanged(nameof(MoveExistingRoomsToNewArea));
        NotifyMapEditorCommands();
    }

    private void ScheduleMapEditorRecovery()
    {
        if (_mapEditor is null || _mapEditorRecoveryStore is null)
        {
            return;
        }

        _mapEditorRecoveryStore.Schedule(
            _mapEditor.Document,
            _mapEditor.GetUndoHistory(),
            _mapEditor.IsDirty,
            GetMapBaselineIdentity());
    }

    private void StartMapMovementTimeout()
    {
        CancelMapMovementTimeout();
        var cancellation = new CancellationTokenSource();
        _mapMovementTimeoutCancellation = cancellation;
        _mapMovementTimeoutTask = WaitForMapMovementTimeoutAsync(cancellation);
    }

    private async Task WaitForMapMovementTimeoutAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_mapMovementTimeout, cancellation.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_mapMovementTimeoutCancellation, cancellation)
                    || _mapEditor?.IsAwaitingRoomInfo != true)
                {
                    return;
                }

                _mapMovementTimeoutCancellation = null;
                var seconds = _mapMovementTimeout.TotalSeconds.ToString("0.#");
                _mapEditor.CancelPendingMovement(
                    $"Brak Room.Info przez {seconds} s. Anulowano oczekiwanie na ruch; mapowanie pozostaje aktywne.");
                NotifyMapEditorStateChanged();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A snapshot, explicit cancellation, stop, or disposal superseded this timeout.
        }
        catch (Exception exception)
        {
            // Dispatcher shutdown can race application disposal; the mapper timeout must never
            // surface as an unobserved background-task exception.
            System.Diagnostics.Debug.WriteLine($"Map movement timeout failed: {exception}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _mapMovementTimeoutCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelMapMovementTimeout()
    {
        Interlocked.Exchange(ref _mapMovementTimeoutCancellation, null)?.Cancel();
    }

    private string GetMapBaselineIdentity()
    {
        if (!File.Exists(_worldMapPath))
        {
            return Path.GetFullPath(_worldMapPath);
        }

        var file = new FileInfo(_worldMapPath);
        return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private void NotifyMapEditorCommands()
    {
        _startMapEditorCommand.NotifyCanExecuteChanged();
        _stopMapEditorCommand.NotifyCanExecuteChanged();
        _undoMapEditorCommand.NotifyCanExecuteChanged();
        _redoMapEditorCommand.NotifyCanExecuteChanged();
        _createMapAreaCommand.NotifyCanExecuteChanged();
        _saveMapEditorCommand.NotifyCanExecuteChanged();
    }

    private void RequestCenterOnCurrentRoom() => CenterOnPlayer();

    public void Dispose()
    {
        _locationResolver.LocationChanged -= OnLocationChanged;
        if (_mainViewModel is not null)
        {
            _mainViewModel.Deaths.CollectionChanged -= OnDeathsChanged;
        }

        CancelMapMovementTimeout();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        TextureCache?.Dispose();
        RoomImages?.Dispose();
        _mapEditorRecoveryStore?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _locationResolver.LocationChanged -= OnLocationChanged;
        if (_mainViewModel is not null)
        {
            _mainViewModel.Deaths.CollectionChanged -= OnDeathsChanged;
        }

        CancelMapMovementTimeout();
        await _mapMovementTimeoutTask.ConfigureAwait(false);
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        TextureCache?.Dispose();
        RoomImages?.Dispose();
        if (_mapEditorRecoveryStore is not null)
        {
            await _mapEditorRecoveryStore.DisposeAsync();
        }
    }
}
