using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.Core.Killeropedia;

namespace MudClient.App.ViewModels;

public sealed class KilleropediaViewModel : ObservableObject
{
    private readonly IReadOnlyList<TeacherEntry> _allTeachers;
    private readonly IReadOnlyList<QuestEntry> _allQuests;
    private readonly TattooCatalogData _tattooCatalog;
    private readonly IReadOnlyList<LoreEntry> _allLoreEntries;
    private readonly IReadOnlyDictionary<string, LoreEntry> _loreById;
    private readonly BookCatalogStore _bookCatalogStore;
    private readonly Func<Task>? _refreshBooksAsync;
    private readonly Action<TeacherEntry>? _showTeacherOnMap;
    private readonly Action<BookLoadLocationEntry>? _showBookLocationOnMap;
    private readonly AsyncRelayCommand _refreshBooksCommand;
    private readonly List<BookEntry> _allBooks = [];
    private readonly RareCatalogStore _rareCatalogStore;
    private readonly Func<Task>? _refreshRaresAsync;
    private readonly AsyncRelayCommand _refreshRaresCommand;
    private readonly List<RareEntry> _allRares = [];
    private Dictionary<string, RareEntry> _raresByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly AbilityCaptureStore _abilityCaptureStore;
    private readonly List<AbilityCaptureEntry> _allAbilities = [];
    private readonly RelayCommand _reloadAbilitiesCommand;
    private readonly ArtifactTryStore _artifactTryStore;
    private readonly List<ArtifactEntry> _allArtifacts = [];
    private Dictionary<string, ArtifactEntry> _artifactsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedArtifactSortClassNames = new(StringComparer.OrdinalIgnoreCase);
    private string _artifactSearchText = string.Empty;
    private ArtifactEntry? _selectedArtifact;
    private string _abilitySearchText = string.Empty;
    private readonly HashSet<string> _selectedAbilityClassNames = new(StringComparer.OrdinalIgnoreCase) { "Wedrowiec" };
    private AbilitySkillTreeEntry? _selectedAbility;
    private DateTimeOffset? _abilitiesCapturedAtUtc;
    private string _teacherSearchText = string.Empty;
    private string _questSearchText = string.Empty;
    private QuestEntry? _selectedQuest;
    private string _tattooSearchText = string.Empty;
    private TattooBonusEntry? _selectedTattoo;
    private bool _isTattooInfoExpanded;
    private TeacherEntry? _selectedTeacher;
    private string _bookSearchText = string.Empty;
    private string _selectedBookClass = "Wszystkie";
    private BookEntry? _selectedBook;
    private bool _isConnected;
    private WorldMapRegion? _selectedWorldMapRegion;
    private bool _isBookRefreshRunning;
    private string _bookRefreshStatus = string.Empty;
    private DateTimeOffset? _booksGeneratedAtUtc;
    private string _rareSearchText = string.Empty;
    private string _selectedRareCategory = "Wszystkie";
    private RareEntry? _selectedRare;
    private bool _isRareRefreshRunning;
    private string _rareRefreshStatus = string.Empty;
    private DateTimeOffset? _raresGeneratedAtUtc;
    private string _loreSearchText = string.Empty;
    private string _selectedLoreCategory = "Wszystkie";
    private LoreEntry? _selectedLoreEntry;
    private readonly DateTimeOffset? _loreGeneratedAtUtc;
    private readonly string _loreSourceText;
    private readonly string? _loreWarning;

    public KilleropediaViewModel()
        : this(TeacherCatalogLoader.Load(), new BookCatalogStore(), null, null, null, null, QuestCatalogLoader.Load())
    {
    }

    internal KilleropediaViewModel(
        IReadOnlyList<TeacherEntry> teachers,
        BookCatalogStore bookCatalogStore,
        Func<Task>? refreshBooksAsync,
        Action<TeacherEntry>? showTeacherOnMap = null,
        LoreCatalogData? loreCatalog = null,
        string? mapDirectory = null,
        IReadOnlyList<QuestEntry>? quests = null,
        Action<BookLoadLocationEntry>? showBookLocationOnMap = null,
        TattooCatalogData? tattooCatalog = null,
        RareCatalogStore? rareCatalogStore = null,
        Func<Task>? refreshRaresAsync = null,
        AbilityCaptureStore? abilityCaptureStore = null,
        ArtifactTryStore? artifactTryStore = null)
    {
        _allTeachers = teachers;
        _allQuests = quests ?? QuestCatalogLoader.Load();
        _tattooCatalog = tattooCatalog ?? TattooCatalogLoader.Load();
        _bookCatalogStore = bookCatalogStore;
        _refreshBooksAsync = refreshBooksAsync;
        _rareCatalogStore = rareCatalogStore ?? new RareCatalogStore();
        _refreshRaresAsync = refreshRaresAsync;
        _abilityCaptureStore = abilityCaptureStore ?? new AbilityCaptureStore();
        _artifactTryStore = artifactTryStore ?? new ArtifactTryStore();
        _showTeacherOnMap = showTeacherOnMap;
        _showBookLocationOnMap = showBookLocationOnMap;
        var resolvedLoreCatalog = loreCatalog ?? LoreCatalogLoader.Load();
        _allLoreEntries = resolvedLoreCatalog.Entries;
        _loreById = _allLoreEntries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        _loreGeneratedAtUtc = resolvedLoreCatalog.GeneratedAtUtc;
        _loreSourceText = resolvedLoreCatalog.SourceText;
        _loreWarning = resolvedLoreCatalog.Warning;
        AvailableLoreCategories = [
            "Wszystkie",
            .. _allLoreEntries.Select(entry => entry.Category).Distinct(StringComparer.Ordinal),
        ];
        _refreshBooksCommand = new AsyncRelayCommand(RefreshBooksAsync, CanRefreshBooks);
        _refreshRaresCommand = new AsyncRelayCommand(RefreshRaresAsync, CanRefreshRares);
        _reloadAbilitiesCommand = new RelayCommand(LoadAbilityCatalog);
        ToggleAbilityClassCommand = new RelayCommand<string>(ToggleAbilityClass);
        ToggleArtifactSortClassCommand = new RelayCommand<string>(ToggleArtifactSortClass);
        WorldMapRegions = [new WorldMapRegion("Stary Kontynent", "old-continent-overview.png", mapDirectory)];
        ShowTeacherOnMapCommand = new RelayCommand<TeacherEntry>(
            ShowTeacherOnMap,
            teacher => teacher?.HasRoomLocation == true && _showTeacherOnMap is not null);
        ShowBookLocationOnMapCommand = new RelayCommand<BookLoadLocationEntry>(
            ShowBookLocationOnMap,
            location => location?.HasRoomLocation == true && _showBookLocationOnMap is not null);
        NavigateLoreCommand = new RelayCommand<LoreLink>(
            NavigateLore,
            link => link is not null && _loreById.ContainsKey(link.TargetId));
        ToggleTattooInfoCommand = new RelayCommand(() => IsTattooInfoExpanded = !IsTattooInfoExpanded);
        ApplyTeacherFilter();
        ApplyQuestFilter();
        ApplyTattooFilter();
        LoadBookCatalog();
        LoadRareCatalog();
        LoadAbilityCatalog();
        LoadArtifactCatalog();
        ApplyLoreFilter();
        _selectedWorldMapRegion = WorldMapRegions.FirstOrDefault();
    }

    public IReadOnlyList<WorldMapRegion> WorldMapRegions { get; }

    public WorldMapRegion? SelectedWorldMapRegion
    {
        get => _selectedWorldMapRegion;
        set => SetProperty(ref _selectedWorldMapRegion, value);
    }

    public ObservableCollection<TeacherEntry> FilteredTeachers { get; } = [];

    public ObservableCollection<QuestEntry> FilteredQuests { get; } = [];

    public string QuestSearchText
    {
        get => _questSearchText;
        set
        {
            if (SetProperty(ref _questSearchText, value))
            {
                ApplyQuestFilter();
            }
        }
    }

    public QuestEntry? SelectedQuest
    {
        get => _selectedQuest;
        set => SetProperty(ref _selectedQuest, value);
    }

    public string FilteredQuestCountText => $"Zadania: {FilteredQuests.Count} z {_allQuests.Count}";

    public bool HasNoQuestResults => FilteredQuests.Count == 0;

    public string TattooIntro => _tattooCatalog.Intro;

    public IReadOnlyList<TattooCommandEntry> TattooCommands => _tattooCatalog.Commands;

    public IReadOnlyList<TattooRuneEntry> TattooRuneTypes => _tattooCatalog.RuneTypes;

    public string TattooStackingNotes => _tattooCatalog.StackingNotes;

    public bool IsTattooInfoExpanded
    {
        get => _isTattooInfoExpanded;
        set => SetProperty(ref _isTattooInfoExpanded, value);
    }

    public IRelayCommand ToggleTattooInfoCommand { get; }

    public ObservableCollection<TattooBonusEntry> FilteredTattoos { get; } = [];

    public string TattooSearchText
    {
        get => _tattooSearchText;
        set
        {
            if (SetProperty(ref _tattooSearchText, value))
            {
                ApplyTattooFilter();
            }
        }
    }

    public TattooBonusEntry? SelectedTattoo
    {
        get => _selectedTattoo;
        set => SetProperty(ref _selectedTattoo, value);
    }

    public string FilteredTattooCountText => $"Bonusy: {FilteredTattoos.Count} z {_tattooCatalog.Bonuses.Count}";

    public bool HasNoTattooResults => FilteredTattoos.Count == 0;

    public IReadOnlyList<string> RandomBookMagWords => RandomBookNaming.MagWords;

    public IReadOnlyList<string> RandomBookKlerykWords => RandomBookNaming.KlerykWords;

    public IReadOnlyList<string> RandomBookPaladynWords => RandomBookNaming.PaladynWords;

    public IReadOnlyList<string> RandomBookDruidWords => RandomBookNaming.DruidWords;

    public IReadOnlyList<string> RandomBookNomadWords => RandomBookNaming.NomadWords;

    public IReadOnlyList<string> RandomBookNazwaWords => RandomBookNaming.NazwaWords;

    public IReadOnlyList<string> RandomBookWartoscDuzaWords => RandomBookNaming.WartoscDuzaWords;

    public IReadOnlyList<string> RandomBookWartoscMalaWords => RandomBookNaming.WartoscMalaWords;

    public IReadOnlyList<string> RandomBookWagaMalaWords => RandomBookNaming.WagaMalaWords;

    public ObservableCollection<LoreEntry> FilteredLoreEntries { get; } = [];

    public IReadOnlyList<LoreEntry> LoreEntries => _allLoreEntries;

    public IReadOnlyList<string> AvailableLoreCategories { get; }

    public string LoreSearchText
    {
        get => _loreSearchText;
        set
        {
            if (SetProperty(ref _loreSearchText, value))
            {
                ApplyLoreFilter();
            }
        }
    }

    public string SelectedLoreCategory
    {
        get => _selectedLoreCategory;
        set
        {
            if (SetProperty(ref _selectedLoreCategory, value))
            {
                ApplyLoreFilter();
            }
        }
    }

    public LoreEntry? SelectedLoreEntry
    {
        get => _selectedLoreEntry;
        set => SetProperty(ref _selectedLoreEntry, value);
    }

    public IRelayCommand<LoreLink> NavigateLoreCommand { get; }

    public string FilteredLoreCountText => $"Hasła: {FilteredLoreEntries.Count} z {_allLoreEntries.Count}";

    public bool HasLore => _allLoreEntries.Count > 0;

    public bool HasNoLoreResults => FilteredLoreEntries.Count == 0;

    public string LoreCatalogStatusText
    {
        get
        {
            var generated = _loreGeneratedAtUtc is null
                ? "data nieznana"
                : _loreGeneratedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return $"Katalog: {generated} · {_loreSourceText}";
        }
    }

    public string LoreCatalogWarning => _loreWarning ?? string.Empty;

    public bool HasLoreCatalogWarning => !string.IsNullOrWhiteSpace(_loreWarning);

    public string TeacherSearchText
    {
        get => _teacherSearchText;
        set
        {
            if (SetProperty(ref _teacherSearchText, value))
            {
                ApplyTeacherFilter();
            }
        }
    }

    public TeacherEntry? SelectedTeacher
    {
        get => _selectedTeacher;
        set => SetProperty(ref _selectedTeacher, value);
    }

    public string FilteredTeacherCountText => $"Nauczyciele: {FilteredTeachers.Count} z {_allTeachers.Count}";

    public IRelayCommand<TeacherEntry> ShowTeacherOnMapCommand { get; }

    public IRelayCommand<BookLoadLocationEntry> ShowBookLocationOnMapCommand { get; }

    public ObservableCollection<BookEntry> FilteredBooks { get; } = [];

    public IReadOnlyList<string> AvailableBookClasses { get; } =
        ["Wszystkie", .. BookCatalogRefreshCoordinator.BookClasses];

    public IAsyncRelayCommand RefreshBooksCommand => _refreshBooksCommand;

    public bool IsBookRefreshButtonVisible => DeveloperFeatures.ShowBookCatalogRefreshButton;

    public bool IsBookRefreshEnabled =>
        DeveloperFeatures.EnableBookCatalogRefreshButton
        && _isConnected
        && !_isBookRefreshRunning;

    public bool IsBookRefreshRunning
    {
        get => _isBookRefreshRunning;
        private set
        {
            if (SetProperty(ref _isBookRefreshRunning, value))
            {
                OnPropertyChanged(nameof(IsBookRefreshEnabled));
                OnPropertyChanged(nameof(BookRefreshButtonText));
                _refreshBooksCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string BookRefreshButtonText => IsBookRefreshRunning ? "Odświeżanie..." : "Odśwież";

    public string BookRefreshStatus
    {
        get => _bookRefreshStatus;
        private set => SetProperty(ref _bookRefreshStatus, value);
    }

    public string BookSearchText
    {
        get => _bookSearchText;
        set
        {
            if (SetProperty(ref _bookSearchText, value))
            {
                ApplyBookFilter();
            }
        }
    }

    public string SelectedBookClass
    {
        get => _selectedBookClass;
        set
        {
            if (SetProperty(ref _selectedBookClass, value))
            {
                ApplyBookFilter();
            }
        }
    }

    public BookEntry? SelectedBook
    {
        get => _selectedBook;
        set => SetProperty(ref _selectedBook, value);
    }

    public string FilteredBookCountText => $"Księgi: {FilteredBooks.Count} z {_allBooks.Count}";

    public bool HasBooks => _allBooks.Count > 0;

    public bool HasNoBooks => !HasBooks;

    public string BooksGeneratedText => _booksGeneratedAtUtc is null
        ? "Brak wygenerowanego katalogu."
        : $"Katalog: {_booksGeneratedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";

    public ObservableCollection<RareEntry> FilteredRares { get; } = [];

    public IReadOnlyList<string> AvailableRareCategories { get; } = ["Wszystkie", "artefakt", "rzadki", "instancyjny"];

    public IAsyncRelayCommand RefreshRaresCommand => _refreshRaresCommand;

    public bool IsRareRefreshButtonVisible => DeveloperFeatures.ShowRareCatalogRefreshButton;

    public bool IsRareRefreshEnabled =>
        DeveloperFeatures.EnableRareCatalogRefreshButton
        && _isConnected
        && !_isRareRefreshRunning;

    public bool IsRareRefreshRunning
    {
        get => _isRareRefreshRunning;
        private set
        {
            if (SetProperty(ref _isRareRefreshRunning, value))
            {
                OnPropertyChanged(nameof(IsRareRefreshEnabled));
                OnPropertyChanged(nameof(RareRefreshButtonText));
                _refreshRaresCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RareRefreshButtonText => IsRareRefreshRunning ? "Odświeżanie..." : "Odśwież";

    public string RareRefreshStatus
    {
        get => _rareRefreshStatus;
        private set => SetProperty(ref _rareRefreshStatus, value);
    }

    public string RareSearchText
    {
        get => _rareSearchText;
        set
        {
            if (SetProperty(ref _rareSearchText, value))
            {
                ApplyRareFilter();
            }
        }
    }

    public string SelectedRareCategory
    {
        get => _selectedRareCategory;
        set
        {
            if (SetProperty(ref _selectedRareCategory, value))
            {
                ApplyRareFilter();
            }
        }
    }

    public RareEntry? SelectedRare
    {
        get => _selectedRare;
        set
        {
            if (SetProperty(ref _selectedRare, value))
            {
                OnPropertyChanged(nameof(SelectedRareArtifactDetail));
                OnPropertyChanged(nameof(HasSelectedRareArtifactDetail));
            }
        }
    }

    /// <summary>The try-parsed detail for whatever <see cref="SelectedRare"/> is currently
    /// showing, when a same-name capture exists — lets the rarelist detail panel show every fact
    /// <see cref="ArtifactHelpParser"/> found for that item (class/race/alignment restrictions,
    /// stats, granted abilities, set info) as extra sections, "merging" the two sources by name.</summary>
    public ArtifactEntry? SelectedRareArtifactDetail =>
        SelectedRare is { } rare && _artifactsByName.TryGetValue(rare.Name, out var artifact) ? artifact : null;

    public bool HasSelectedRareArtifactDetail => SelectedRareArtifactDetail is not null;

    public string FilteredRareCountText => $"Przedmioty: {FilteredRares.Count} z {_allRares.Count}";

    public bool HasRares => _allRares.Count > 0;

    public bool HasNoRares => !HasRares;

    public string RaresGeneratedText => _raresGeneratedAtUtc is null
        ? "Brak wygenerowanego katalogu."
        : $"Katalog: {_raresGeneratedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";

    public ObservableCollection<ArtifactEntry> FilteredArtifacts { get; } = [];

    public IRelayCommand<string> ToggleArtifactSortClassCommand { get; }

    public IReadOnlyList<string> AvailableArtifactClasses { get; private set; } = [];

    /// <summary>Checklist of every class referenced by at least one captured artifact's
    /// restrictions, plus whether it's currently one of the "sort these first" classes — backs
    /// the class-sort dropdown's checkboxes, same pattern as <see cref="AbilityClassOptions"/>.</summary>
    public IReadOnlyList<AbilityClassOption> ArtifactClassOptions =>
        AvailableArtifactClasses
            .Select(name => new AbilityClassOption(name, _selectedArtifactSortClassNames.Contains(name)))
            .ToList();

    public string SelectedArtifactClassesSummaryText => _selectedArtifactSortClassNames.Count == 0
        ? "Wszystkie"
        : string.Join(", ", _selectedArtifactSortClassNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    public string ArtifactSearchText
    {
        get => _artifactSearchText;
        set
        {
            if (SetProperty(ref _artifactSearchText, value))
            {
                ApplyArtifactFilter();
            }
        }
    }

    public ArtifactEntry? SelectedArtifact
    {
        get => _selectedArtifact;
        set
        {
            if (SetProperty(ref _selectedArtifact, value))
            {
                OnPropertyChanged(nameof(SelectedArtifactRareDetail));
                OnPropertyChanged(nameof(HasSelectedArtifactRareDetail));
            }
        }
    }

    /// <summary>The rarelist catalog entry for whatever <see cref="SelectedArtifact"/> is
    /// currently showing, when a same-name rarelist capture exists — lets the try-parsed detail
    /// panel show where/how the item was seen (vnum, item type/slot, artefakt/rzadki/instancyjny
    /// category) as an extra section, mirroring <see cref="SelectedRareArtifactDetail"/>.</summary>
    public RareEntry? SelectedArtifactRareDetail =>
        SelectedArtifact is { } artifact && _raresByName.TryGetValue(artifact.Name, out var rare) ? rare : null;

    public bool HasSelectedArtifactRareDetail => SelectedArtifactRareDetail is not null;

    public string FilteredArtifactCountText => $"Przedmioty: {FilteredArtifacts.Count} z {_allArtifacts.Count}";

    public bool HasArtifacts => _allArtifacts.Count > 0;

    public bool HasNoArtifacts => !HasArtifacts;

    public string ArtifactsGeneratedText => _allArtifacts.Count == 0
        ? "Brak zmapowanych przedmiotów — użyj „/mapuj <liczba>” w grze."
        : $"Zmapowanych przedmiotów: {_allArtifacts.Count}";

    /// <summary>Toggles <paramref name="className"/> among the classes new artifacts get sorted to
    /// the front for — a preference used only to reorder <see cref="FilteredArtifacts"/>
    /// (fitting items first), never to hide anything, since restriction data is inherently
    /// incomplete (only what "try" happened to capture).</summary>
    private void ToggleArtifactSortClass(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return;
        }

        if (!_selectedArtifactSortClassNames.Remove(className))
        {
            _selectedArtifactSortClassNames.Add(className);
        }

        OnPropertyChanged(nameof(ArtifactClassOptions));
        OnPropertyChanged(nameof(SelectedArtifactClassesSummaryText));
        ApplyArtifactFilter();
    }

    public ObservableCollection<AbilitySkillTreeEntry> FilteredAbilities { get; } = [];

    private int? _currentCharacterLevel;

    /// <summary>The connected character's current level, from Char.Vitals GMCP — null while
    /// disconnected (see <see cref="SetCharacterLevel"/>/MainWindowViewModel.IsConnected) so a
    /// stale or mock level never gets treated as real. Feeds <see cref="NewAbilities"/>: an
    /// ability whose required level sits above this counts as "new" even when it's otherwise
    /// <see cref="AbilitySkillTreeEntry.IsOwned"/> (already unlocked in principle, just not yet
    /// reached).</summary>
    public int? CurrentCharacterLevel
    {
        get => _currentCharacterLevel;
        private set
        {
            if (SetProperty(ref _currentCharacterLevel, value))
            {
                OnPropertyChanged(nameof(NewAbilities));
                OnPropertyChanged(nameof(HasNewAbilities));
            }
        }
    }

    /// <summary>Called from MainWindowViewModel on every Char.Vitals GMCP update (and with
    /// <see langword="null"/> on disconnect) — the live counterpart to <see cref="SetConnectionState"/>.</summary>
    public void SetCharacterLevel(int? level) => CurrentCharacterLevel = level;

    /// <summary>The abilities within <see cref="FilteredAbilities"/> not yet actually attained by
    /// the connected character — either a preview of the browsed specialization
    /// (<see cref="AbilitySkillTreeEntry.IsOwned"/> false), or already unlocked in principle but
    /// gated by a level the character hasn't reached yet. Backs the "Sprawdź co zyskasz"
    /// button/flyout; recomputed and re-notified alongside <see cref="FilteredAbilities"/> in
    /// <see cref="ApplyAbilityFilter"/> rather than maintained as its own collection, since it's
    /// just a filtered view of the same data.</summary>
    public IReadOnlyList<AbilitySkillTreeEntry> NewAbilities =>
        FilteredAbilities
            .Where(entry => !entry.IsOwned || IsAboveCurrentLevel(entry))
            .ToList();

    private bool IsAboveCurrentLevel(AbilitySkillTreeEntry entry) =>
        CurrentCharacterLevel is { } level && (entry.BrowsedClassLevel ?? entry.WandererLevel) > level;

    public bool HasNewAbilities => NewAbilities.Count > 0;

    /// <summary>Tattoo class bonuses gated to any of the currently browsed class(es)/specialization(s)
    /// — the tattoo-catalog analogue of <see cref="NewAbilities"/>, shown in the same "Sprawdź co
    /// zyskasz" flyout. A tattoo's <c>Classes</c> list holds real game class names (or "Wszystkie"/
    /// "Wędrowiec"/a "Wymaga tricka: ..." pseudo-class for a universal, wanderer-tier, or
    /// trick-gated one) — none of those ever matches a real browsed class name, so a tattoo every
    /// class (or every Wędrowiec regardless of specialization) already has naturally never shows up
    /// here: it isn't something picking a specialization grants. "Wędrowiec" specifically is
    /// excluded rather than left to fall out of the comparison, because it's also
    /// <c>_selectedAbilityClassNames</c>' own default/no-selection sentinel (see
    /// <see cref="ToggleAbilityClassCommand"/>) — without the exclusion, normalizing diacritics
    /// below would make that ASCII sentinel match tattoos.json's proper-spelling "Wędrowiec" and
    /// wrongly show "grabieżca" before any specialization is even chosen.
    /// Recomputed and re-notified alongside <see cref="NewAbilities"/> in
    /// <see cref="ApplyAbilityFilter"/>. Compared with <see cref="SearchText.Normalize"/> rather
    /// than a plain string match — <c>_selectedAbilityClassNames</c> comes from the game's own raw
    /// "help" text, which drops Polish diacritics entirely (e.g. "Barbarzynca", "Zlodziej"), while
    /// tattoos.json is hand-typed with proper spelling ("Barbarzyńca", "Złodziej"); a plain
    /// comparison silently matched nothing for any class with a diacritic in its name.</summary>
    public IReadOnlyList<TattooBonusEntry> NewTattoos
    {
        get
        {
            var selected = _selectedAbilityClassNames
                .Select(SearchText.Normalize)
                .Where(name => name != "wedrowiec")
                .ToHashSet(StringComparer.Ordinal);
            return _tattooCatalog.Bonuses
                .Where(bonus => bonus.Classes.Any(className => selected.Contains(SearchText.Normalize(className))))
                .ToList();
        }
    }

    public bool HasNewTattoos => NewTattoos.Count > 0;

    public IReadOnlyList<string> AbilityClasses { get; private set; } = ["Wedrowiec"];

    /// <summary>Looks up a captured ability by its exact "help" name (case-insensitive). Used by
    /// <see cref="MainWindowViewModel"/>'s group-shortcut command builder to tell a spell from a
    /// skill and, for a skill, find its actual invocation verb (<see cref="AbilityCaptureEntry.Syntax"/>)
    /// — which often differs from the "help" lookup name itself (e.g. "healing touch" is invoked
    /// as bare "touch"). Null when nothing captured matches, e.g. the name was never mapped via
    /// "/mapuj".</summary>
    public AbilityCaptureEntry? FindAbilityByName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _allAbilities.FirstOrDefault(
                entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    public IRelayCommand ReloadAbilitiesCommand => _reloadAbilitiesCommand;

    public string AbilitySearchText
    {
        get => _abilitySearchText;
        set
        {
            if (SetProperty(ref _abilitySearchText, value))
            {
                ApplyAbilityFilter();
            }
        }
    }

    public IRelayCommand<string> ToggleAbilityClassCommand { get; }

    /// <summary>Checklist of every known class plus whether it's currently one of the browsed/
    /// combined specializations — backs the class-filter dropdown's checkboxes. Rebuilt (rather
    /// than mutated in place) whenever the selection or <see cref="AbilityClasses"/> changes, so a
    /// plain <see cref="ObservableObject.OnPropertyChanged(string?)"/> is enough to refresh it.</summary>
    public IReadOnlyList<AbilityClassOption> AbilityClassOptions =>
        AbilityClasses.Select(name => new AbilityClassOption(name, _selectedAbilityClassNames.Contains(name))).ToList();

    /// <summary>Which class(es)' ability kits are being browsed together — "Wedrowiec" alone (the
    /// default) means "no specialization chosen yet", so only unconditionally-available abilities
    /// count as gained. Picking one or more other classes previews "if I specialize as any of
    /// these, what do I gain as Wędrowiec", combined into a single tree.</summary>
    public string SelectedAbilityClassesSummaryText => string.Join(
        ", ", _selectedAbilityClassNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    /// <summary>Toggles <paramref name="className"/> in the browsed-classes set. Picking
    /// "Wedrowiec" clears every other selection back to the baseline; picking any other class
    /// drops "Wedrowiec" from the set (it's implied — universal abilities always show regardless)
    /// and lets multiple specializations combine. The set is never left empty — dropping the last
    /// selected class falls back to "Wedrowiec".</summary>
    private void ToggleAbilityClass(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return;
        }

        if (string.Equals(className, "Wedrowiec", StringComparison.OrdinalIgnoreCase))
        {
            _selectedAbilityClassNames.Clear();
            _selectedAbilityClassNames.Add("Wedrowiec");
        }
        else
        {
            _selectedAbilityClassNames.Remove("Wedrowiec");
            if (!_selectedAbilityClassNames.Remove(className))
            {
                _selectedAbilityClassNames.Add(className);
            }

            if (_selectedAbilityClassNames.Count == 0)
            {
                _selectedAbilityClassNames.Add("Wedrowiec");
            }
        }

        OnPropertyChanged(nameof(AbilityClassOptions));
        OnPropertyChanged(nameof(SelectedAbilityClassesSummaryText));
        ApplyAbilityFilter();
    }

    public AbilitySkillTreeEntry? SelectedAbility
    {
        get => _selectedAbility;
        set
        {
            if (SetProperty(ref _selectedAbility, value))
            {
                OnPropertyChanged(nameof(HasSelectedAbility));
            }
        }
    }

    public bool HasSelectedAbility => _selectedAbility is not null;

    public string FilteredAbilityCountText => $"Umiejętności: {FilteredAbilities.Count} z {_allAbilities.Count}";

    public bool HasAbilities => _allAbilities.Count > 0;

    public bool HasNoAbilities => !HasAbilities;

    public string AbilitiesGeneratedText => _abilitiesCapturedAtUtc is null
        ? "Brak zmapowanych umiejętności — użyj „/mapuj <klasa>” w grze."
        : $"Ostatnia aktualizacja: {_abilitiesCapturedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";

    public void SetConnectionState(bool isConnected)
    {
        if (_isConnected == isConnected)
        {
            return;
        }

        _isConnected = isConnected;
        OnPropertyChanged(nameof(IsBookRefreshEnabled));
        _refreshBooksCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRareRefreshEnabled));
        _refreshRaresCommand.NotifyCanExecuteChanged();
    }

    public void BeginBookRefresh()
    {
        IsBookRefreshRunning = true;
        BookRefreshStatus = "Rozpoczynanie odświeżania...";
    }

    public void ReportBookRefresh(BookCatalogRefreshProgress progress) =>
        BookRefreshStatus = progress.DisplayText;

    public void CompleteBookRefresh(BookCatalogDocument catalog)
    {
        ApplyBookCatalog(catalog);
        BookRefreshStatus = $"Zapisano {_allBooks.Count} ksiąg do {_bookCatalogStore.Path}.";
        IsBookRefreshRunning = false;
    }

    public void FailBookRefresh(string message)
    {
        BookRefreshStatus = message;
        IsBookRefreshRunning = false;
    }

    public void BeginRareRefresh()
    {
        IsRareRefreshRunning = true;
        RareRefreshStatus = "Rozpoczynanie odświeżania...";
    }

    /// <summary>
    /// Vnum → Details for every already-mapped item in the currently loaded catalog, so a new
    /// refresh can skip re-fetching them (see RareCatalogRefreshCoordinator.RefreshAsync).
    /// </summary>
    public IReadOnlyDictionary<int, string> GetKnownRareDetails() =>
        _allRares
            .Where(rare => rare.HasDetails)
            .ToDictionary(rare => rare.Vnum, rare => rare.Details);

    public void ReportRareRefresh(RareCatalogRefreshProgress progress) =>
        RareRefreshStatus = progress.DisplayText;

    public void CompleteRareRefresh(RareCatalogDocument catalog)
    {
        ApplyRareCatalog(catalog);
        RareRefreshStatus = $"Zapisano {_allRares.Count} przedmiotów do {_rareCatalogStore.Path}.";
        IsRareRefreshRunning = false;
    }

    public void FailRareRefresh(string message)
    {
        RareRefreshStatus = message;
        IsRareRefreshRunning = false;
    }

    private void ApplyTeacherFilter()
    {
        var tokens = Normalize(TeacherSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousId = SelectedTeacher?.MobVnum;

        FilteredTeachers.Clear();
        foreach (var teacher in _allTeachers)
        {
            var haystack = Normalize(string.Join(' ',
                teacher.MobVnum,
                teacher.Name,
                teacher.Region,
                teacher.Area,
                teacher.RoomVnum,
                teacher.ClassesText,
                string.Join(' ', teacher.Skills.Select(skill => skill.Name)),
                string.Join(' ', teacher.Tricks.Select(trick => $"{trick.Name} {trick.EnhancesText}"))));
            if (tokens.All(haystack.Contains))
            {
                FilteredTeachers.Add(teacher);
            }
        }

        SelectedTeacher = FilteredTeachers.FirstOrDefault(teacher => teacher.MobVnum == previousId)
            ?? FilteredTeachers.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredTeacherCountText));
    }

    private void ApplyQuestFilter()
    {
        var tokens = Normalize(QuestSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousName = SelectedQuest?.Name;

        FilteredQuests.Clear();
        foreach (var quest in _allQuests)
        {
            if (tokens.All(token => Normalize(quest.SearchableText).Contains(token)))
            {
                FilteredQuests.Add(quest);
            }
        }

        SelectedQuest = FilteredQuests.FirstOrDefault(quest => quest.Name == previousName)
            ?? FilteredQuests.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredQuestCountText));
        OnPropertyChanged(nameof(HasNoQuestResults));
    }

    private void ApplyTattooFilter()
    {
        var tokens = Normalize(TattooSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousName = SelectedTattoo?.Name;

        FilteredTattoos.Clear();
        foreach (var bonus in _tattooCatalog.Bonuses)
        {
            if (tokens.All(token => Normalize(bonus.SearchableText).Contains(token)))
            {
                FilteredTattoos.Add(bonus);
            }
        }

        SelectedTattoo = FilteredTattoos.FirstOrDefault(bonus => bonus.Name == previousName)
            ?? FilteredTattoos.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredTattooCountText));
        OnPropertyChanged(nameof(HasNoTattooResults));
    }

    private void ApplyLoreFilter()
    {
        var tokens = Normalize(LoreSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousId = SelectedLoreEntry?.Id;

        FilteredLoreEntries.Clear();
        foreach (var entry in _allLoreEntries)
        {
            if (!string.Equals(SelectedLoreCategory, "Wszystkie", StringComparison.Ordinal)
                && !string.Equals(entry.Category, SelectedLoreCategory, StringComparison.Ordinal))
            {
                continue;
            }

            var haystack = Normalize(entry.SearchableText);
            if (tokens.All(haystack.Contains))
            {
                FilteredLoreEntries.Add(entry);
            }
        }

        SelectedLoreEntry = FilteredLoreEntries.FirstOrDefault(entry => entry.Id == previousId)
            ?? FilteredLoreEntries.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredLoreCountText));
        OnPropertyChanged(nameof(HasNoLoreResults));
    }

    private void NavigateLore(LoreLink? link)
    {
        if (link is null || !_loreById.TryGetValue(link.TargetId, out var target))
        {
            return;
        }

        if (!string.Equals(SelectedLoreCategory, "Wszystkie", StringComparison.Ordinal))
        {
            SelectedLoreCategory = "Wszystkie";
        }

        if (!string.IsNullOrWhiteSpace(LoreSearchText))
        {
            LoreSearchText = string.Empty;
        }

        SelectedLoreEntry = target;
    }

    private async Task RefreshBooksAsync()
    {
        if (_refreshBooksAsync is not null)
        {
            await _refreshBooksAsync();
        }
    }

    private void ShowTeacherOnMap(TeacherEntry? teacher)
    {
        if (teacher?.HasRoomLocation == true)
        {
            _showTeacherOnMap?.Invoke(teacher);
        }
    }

    private void ShowBookLocationOnMap(BookLoadLocationEntry? location)
    {
        if (location?.HasRoomLocation == true)
        {
            _showBookLocationOnMap?.Invoke(location);
        }
    }

    private bool CanRefreshBooks() => IsBookRefreshEnabled && _refreshBooksAsync is not null;

    private async Task RefreshRaresAsync()
    {
        if (_refreshRaresAsync is not null)
        {
            await _refreshRaresAsync();
        }
    }

    private bool CanRefreshRares() => IsRareRefreshEnabled && _refreshRaresAsync is not null;

    private void LoadBookCatalog()
    {
        try
        {
            ApplyBookCatalog(_bookCatalogStore.Load());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ApplyBookCatalog(new BookCatalogDocument());
            BookRefreshStatus = exception.Message;
        }
    }

    private void ApplyBookCatalog(BookCatalogDocument catalog)
    {
        _allBooks.Clear();
        _allBooks.AddRange(catalog.Books.OrderBy(book => book.Name, StringComparer.OrdinalIgnoreCase));
        _booksGeneratedAtUtc = catalog.GeneratedAtUtc;
        ApplyBookFilter();
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(HasNoBooks));
        OnPropertyChanged(nameof(BooksGeneratedText));
    }

    private void ApplyBookFilter()
    {
        var tokens = Normalize(BookSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousVnum = SelectedBook?.Vnum;
        FilteredBooks.Clear();

        foreach (var book in _allBooks)
        {
            if (!string.Equals(SelectedBookClass, "Wszystkie", StringComparison.OrdinalIgnoreCase)
                && !book.Classes.Contains(SelectedBookClass, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var haystack = Normalize(string.Join(' ',
                book.Vnum,
                book.Name,
                string.Join(' ', book.Classes),
                string.Join(' ', book.Spells),
                string.Join(' ', book.LoadLocations)));
            if (tokens.All(haystack.Contains))
            {
                FilteredBooks.Add(book);
            }
        }

        SelectedBook = FilteredBooks.FirstOrDefault(book => book.Vnum == previousVnum)
            ?? FilteredBooks.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredBookCountText));
    }

    private void LoadRareCatalog()
    {
        try
        {
            ApplyRareCatalog(_rareCatalogStore.Load());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ApplyRareCatalog(new RareCatalogDocument());
            RareRefreshStatus = exception.Message;
        }
    }

    private void ApplyRareCatalog(RareCatalogDocument catalog)
    {
        _allRares.Clear();
        _allRares.AddRange(catalog.Rares.OrderBy(rare => rare.Name, StringComparer.OrdinalIgnoreCase));
        _raresByName = _allRares
            .GroupBy(rare => rare.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _raresGeneratedAtUtc = catalog.GeneratedAtUtc;
        ApplyRareFilter();
        OnPropertyChanged(nameof(HasRares));
        OnPropertyChanged(nameof(HasNoRares));
        OnPropertyChanged(nameof(RaresGeneratedText));
        OnPropertyChanged(nameof(SelectedArtifactRareDetail));
        OnPropertyChanged(nameof(HasSelectedArtifactRareDetail));
    }

    private void ApplyRareFilter()
    {
        var tokens = Normalize(RareSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousVnum = SelectedRare?.Vnum;
        FilteredRares.Clear();

        foreach (var rare in _allRares)
        {
            if (!string.Equals(SelectedRareCategory, "Wszystkie", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rare.Category, SelectedRareCategory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tokens.All(token => Normalize(rare.SearchableText).Contains(token)))
            {
                FilteredRares.Add(rare);
            }
        }

        SelectedRare = FilteredRares.FirstOrDefault(rare => rare.Vnum == previousVnum)
            ?? FilteredRares.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredRareCountText));
    }

    private void LoadArtifactCatalog()
    {
        try
        {
            ApplyArtifactCatalog(_artifactTryStore.Load());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ApplyArtifactCatalog(new ArtifactTryDocument());
        }
    }

    private void ApplyArtifactCatalog(ArtifactTryDocument document)
    {
        _allArtifacts.Clear();
        _allArtifacts.AddRange(ArtifactEntry.MergeByName(document.Entries));
        _artifactsByName = _allArtifacts.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        AvailableArtifactClasses = _allArtifacts
            .SelectMany(entry => entry.ReferencedClasses)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OnPropertyChanged(nameof(AvailableArtifactClasses));

        _selectedArtifactSortClassNames.RemoveWhere(
            name => !AvailableArtifactClasses.Contains(name, StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(ArtifactClassOptions));
        OnPropertyChanged(nameof(SelectedArtifactClassesSummaryText));

        ApplyArtifactFilter();
        OnPropertyChanged(nameof(HasArtifacts));
        OnPropertyChanged(nameof(HasNoArtifacts));
        OnPropertyChanged(nameof(ArtifactsGeneratedText));
        OnPropertyChanged(nameof(SelectedRareArtifactDetail));
        OnPropertyChanged(nameof(HasSelectedRareArtifactDetail));
        OnPropertyChanged(nameof(SelectedArtifactRareDetail));
        OnPropertyChanged(nameof(HasSelectedArtifactRareDetail));
    }

    private void ApplyArtifactFilter()
    {
        var tokens = Normalize(ArtifactSearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousName = SelectedArtifact?.Name;

        IEnumerable<ArtifactEntry> matches = _allArtifacts
            .Where(item => tokens.All(token => Normalize(item.SearchableText).Contains(token)));

        matches = _selectedArtifactSortClassNames.Count > 0
            ? matches
                .OrderBy(item => _selectedArtifactSortClassNames.Any(item.FitsClass) ? 0 : 1)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            : matches.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

        FilteredArtifacts.Clear();
        foreach (var item in matches)
        {
            FilteredArtifacts.Add(item);
        }

        SelectedArtifact = FilteredArtifacts.FirstOrDefault(item =>
                string.Equals(item.Name, previousName, StringComparison.OrdinalIgnoreCase))
            ?? FilteredArtifacts.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredArtifactCountText));
    }

    private void LoadAbilityCatalog()
    {
        try
        {
            ApplyAbilityCatalog(_abilityCaptureStore.Load());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            ApplyAbilityCatalog(new AbilityCaptureDocument());
        }
    }

    private void ApplyAbilityCatalog(AbilityCaptureDocument catalog)
    {
        _allAbilities.Clear();
        // Multiple /mapuj runs can each capture the same shared skill (e.g. "axe" learnable by
        // several classes) under a different triggering class — AvailableForClasses already lists
        // every class regardless of which run captured it, so keep just one entry per name.
        _allAbilities.AddRange(catalog.Entries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase));
        _abilitiesCapturedAtUtc = _allAbilities.Count == 0 ? null : _allAbilities.Max(entry => entry.CapturedAt);

        // A Mag's spell schools (Nekromancja, Odrzucanie, ...) are each their own separately
        // pickable Wędrowiec specialization, not sub-categories of one "Mag" class — so, besides
        // real classes from "Dostepne dla klas", every distinct WandererSpecialization value also
        // becomes its own browsable entry (skipping "kazda specjalizacja", which isn't one).
        var specializations = _allAbilities
            .Select(entry => entry.WandererSpecialization)
            .Where(specialization => !string.IsNullOrWhiteSpace(specialization))
            .SelectMany(specialization => specialization!.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(specialization => !string.Equals(
                specialization, "kazda specjalizacja", StringComparison.OrdinalIgnoreCase));

        var classes = _allAbilities
            .SelectMany(entry => entry.AvailableForClasses)
            .Select(requirement => requirement.ClassName)
            .Concat(specializations)
            .Where(className => !string.Equals(className, "Wedrowiec", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(className => className, StringComparer.OrdinalIgnoreCase);
        AbilityClasses = ["Wedrowiec", .. classes];
        OnPropertyChanged(nameof(AbilityClasses));

        _selectedAbilityClassNames.RemoveWhere(name => !AbilityClasses.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (_selectedAbilityClassNames.Count == 0)
        {
            _selectedAbilityClassNames.Add("Wedrowiec");
        }

        OnPropertyChanged(nameof(AbilityClassOptions));
        OnPropertyChanged(nameof(SelectedAbilityClassesSummaryText));

        ApplyAbilityFilter();
        OnPropertyChanged(nameof(HasAbilities));
        OnPropertyChanged(nameof(HasNoAbilities));
        OnPropertyChanged(nameof(AbilitiesGeneratedText));
    }

    private void ApplyAbilityFilter()
    {
        var tokens = Normalize(AbilitySearchText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var previousName = SelectedAbility?.Name;
        var browsedClasses = _selectedAbilityClassNames.Count == 0
            ? (IReadOnlyList<string>)["Wedrowiec"]
            : _selectedAbilityClassNames.ToArray();

        var matches = _allAbilities
            .Select(entry => browsedClasses
                .Select(browsedClass => AbilitySkillTreeEntry.Create(entry, browsedClass))
                .Where(item => item is not null)
                .Select(item => item!)
                // Combining classes can make more than one selected class grant the same ability
                // (e.g. it's universal, or two selected specializations both list it) — keep the
                // one with the lowest required level so it renders as a single node, not a
                // duplicate per matching class.
                .OrderBy(item => item.BrowsedClassLevel ?? int.MaxValue)
                .FirstOrDefault())
            .Where(item => item is not null)
            .Select(item => item!)
            .Where(item => tokens.All(token => Normalize(item.SearchableText).Contains(token)))
            .OrderBy(item => item.BrowsedClassLevel ?? int.MaxValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

        FilteredAbilities.Clear();
        foreach (var item in matches)
        {
            FilteredAbilities.Add(item);
        }

        SelectedAbility = FilteredAbilities.FirstOrDefault(item =>
                string.Equals(item.Name, previousName, StringComparison.OrdinalIgnoreCase))
            ?? FilteredAbilities.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredAbilityCountText));
        OnPropertyChanged(nameof(NewAbilities));
        OnPropertyChanged(nameof(HasNewAbilities));
        OnPropertyChanged(nameof(NewTattoos));
        OnPropertyChanged(nameof(HasNewTattoos));
    }

    private static string Normalize(string? value) => SearchText.Normalize(value);
}
