using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudClient.App.Controls;
using MudClient.App.Models;
using MudClient.App.Behaviors;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views;
using MudClient.Core.Killeropedia;

namespace MudClient.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class KilleropediaTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "KillerMudClient_Killeropedia_" + Guid.NewGuid().ToString("N"));

    public KilleropediaTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Catalog_MergesSupplementaryTeachersAndSkillsWithoutDuplicates()
    {
        var teachers = TeacherCatalogLoader.Load();

        Assert.Equal(151, teachers.Count);
        Assert.Equal(1892, teachers.Sum(teacher => teacher.Skills.Count));

        var renegade = Assert.Single(teachers, teacher => teacher.MobVnum == "19216");
        Assert.Contains(renegade.Skills, skill => skill.Name == "whirlwind");
        Assert.Contains(renegade.Skills, skill => skill.Name == "cyclone");

        var haghburg = Assert.Single(teachers, teacher => teacher.MobVnum == "6611");
        Assert.Single(haghburg.Skills, skill => skill.Name == "whirlwind");
        Assert.Contains(haghburg.Skills, skill => skill.Name == "cyclone");
        Assert.Equal("Koszmary Pustyni Kaan-ar", haghburg.Area);

        Assert.Equal(8, teachers.Sum(teacher => teacher.Skills.Count(skill => skill.Name == "twohanded weapon mastery")));
        Assert.Equal(9, teachers.Sum(teacher => teacher.Skills.Count(skill => skill.Name == "unity with familiar")));
        Assert.Equal(5, teachers.Sum(teacher => teacher.Skills.Count(skill => skill.Name == "bladedance")));
        Assert.Equal(5, teachers.Sum(teacher => teacher.Skills.Count(skill => skill.Name == "bladefury")));
        Assert.Equal(5, teachers.Sum(teacher => teacher.Skills.Count(skill => skill.Name == "desert bond")));
        Assert.Equal(4, teachers.Sum(teacher => teacher.Skills.Count(skill => skill.Name == "loth prayer")));

        var yergiz = Assert.Single(teachers, teacher => teacher.MobVnum == "52");
        Assert.Contains(yergiz.Skills, skill => skill == new TeacherSkillEntry(
            "twohanded weapon mastery", 0, 45, 35, 40));

        var sareech = Assert.Single(teachers, teacher => teacher.MobVnum == "34626");
        Assert.Contains(sareech.Skills, skill => skill == new TeacherSkillEntry(
            "unity with familiar", 0, 29, 0, 0));

        var lothTeacher = Assert.Single(teachers, teacher => teacher.MobVnum == "66989");
        Assert.Contains(lothTeacher.Skills, skill => skill == new TeacherSkillEntry(
            "loth prayer", 70, 95, 55, 65));
    }

    [Fact]
    public void Catalog_ImportsTeacherTricksWithLearnChanceAndPrice()
    {
        var teachers = TeacherCatalogLoader.Load();
        (string MobVnum, string Name, int LearnChance, int Price)[] expected =
        [
            ("1354", "vertical kick", 25, 5000),
            ("1354", "staff swirl", 23, 5250),
            ("27577", "entwine", 20, 8000),
            ("27577", "weapon wrench", 23, 9000),
            ("27662", "riposte", 19, 11000),
            ("6611", "cyclone", 18, 7500),
            ("6199", "flabbergast", 12, 3700),
            ("6460", "dragon strike", 16, 10000),
            ("6460", "glorious impale", 16, 8188),
            ("28598", "decapitation", 11, 6000),
            ("10952", "thundering whack", 18, 7450),
            ("17938", "strucking wallop", 19, 7111),
            ("16601", "shove", 35, 5900),
            ("16601", "thigh jab", 25, 6666),
            ("4507", "bleed", 21, 7878),
            ("43911", "ravaging orb", 23, 8000),
            ("40342", "crushing mace", 25, 6543),
            ("33013", "thousandslayer", 21, 8765),
            ("33013", "divine impact", 15, 7240),
            ("923", "divine impact", 15, 7240),
            ("14961", "lethal blow", 5, 25000),
            ("14961", "thigh jab", 10, 5000),
        ];

        Assert.Equal(expected.Length, teachers.Sum(teacher => teacher.Tricks.Count));
        foreach (var item in expected)
        {
            var teacher = Assert.Single(teachers, teacher => teacher.MobVnum == item.MobVnum);
            Assert.Contains(
                teacher.Tricks,
                trick => trick.Name == item.Name && trick.LearnChance == item.LearnChance && trick.Price == item.Price);
        }

        var keredel = Assert.Single(teachers, teacher => teacher.MobVnum == "1354");
        Assert.Equal("Jedzący mnich Keredel", keredel.Name);
        Assert.False(keredel.HasRoomLocation);

        // Every taught trick was transcribed from the wiki alongside its verified game numbers.
        Assert.All(teachers.SelectMany(teacher => teacher.Tricks), trick => Assert.True(trick.HasDescription));
        var verticalKick = keredel.Tricks.Single(trick => trick.Name == "vertical kick");
        Assert.Equal("kick", verticalKick.EnhancesText);
        Assert.Equal(["kick"], verticalKick.Requirements!.Select(r => r.SkillName));
        Assert.Equal(85, verticalKick.Requirements![0].MinPercent);
    }

    [Fact]
    public void TeacherSearch_MatchesDiacriticsSkillsAndVnum()
    {
        var viewModel = CreateViewModel();

        viewModel.TeacherSearchText = "zlodziej bladesplash";
        Assert.Contains(viewModel.FilteredTeachers, teacher => teacher.MobVnum == "1960");

        viewModel.TeacherSearchText = "42832 panther";
        var della = Assert.Single(viewModel.FilteredTeachers);
        Assert.Equal("Druidka Della", della.Name);
        Assert.Same(della, viewModel.SelectedTeacher);

        viewModel.TeacherSearchText = "33013 thousandslayer";
        var trickTeacher = Assert.Single(viewModel.FilteredTeachers);
        Assert.Equal("Władca mroku", trickTeacher.Name);
    }

    [Fact]
    public void QuestCatalog_ContainsPlayerQuestsWithoutVnums()
    {
        var quests = QuestCatalogLoader.Load();

        Assert.Equal(26, quests.Count);
        Assert.Contains(quests, quest => quest.Name == "Łowcy Smoków"
            && quest.Region == "Forteca"
            && quest.Giver == "Wielki Łowca");
        Assert.DoesNotContain(quests, quest => quest.SearchableText.Contains("vnum", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void QuestsView_RendersQuestListAndSelectedQuestDetails()
    {
        var viewModel = CreateViewModel();
        var view = new KilleropediaQuestsView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 720, Content = view };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(26, list.ItemCount);
        Assert.NotNull(viewModel.SelectedQuest);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == viewModel.SelectedQuest.Name);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == viewModel.SelectedQuest.Giver);

        window.Close();
    }

    [Fact]
    public void TattooCatalog_ContainsClassBonusesWithGeneralInfo()
    {
        var catalog = TattooCatalogLoader.Load();

        Assert.Equal(40, catalog.Bonuses.Count);
        Assert.Equal(4, catalog.RuneTypes.Count);
        Assert.Equal(3, catalog.Commands.Count);
        Assert.False(string.IsNullOrWhiteSpace(catalog.Intro));
        Assert.False(string.IsNullOrWhiteSpace(catalog.StackingNotes));

        var nieumarly = Assert.Single(catalog.Bonuses, bonus => bonus.Name == "nieumarły");
        Assert.Equal(["Nekromanta", "Czarny Rycerz"], nieumarly.Classes);

        Assert.Contains(catalog.Bonuses, bonus => bonus.Name == "cień" && bonus.Classes.Contains("Złodziej"));
        Assert.Contains(catalog.RuneTypes, rune => rune.Name == "runa umysłu");
        Assert.Contains(catalog.Commands, command => command.Name.StartsWith("tattoo make", StringComparison.Ordinal));
    }

    [Fact]
    public void TattooSearch_MatchesNameClassAndDescription()
    {
        var viewModel = CreateViewModel();

        viewModel.TattooSearchText = "nekromanta";
        Assert.Contains(viewModel.FilteredTattoos, bonus => bonus.Name == "nieumarły");

        viewModel.TattooSearchText = "backstab";
        Assert.Contains(viewModel.FilteredTattoos, bonus => bonus.Name == "cios śmierci");

        viewModel.TattooSearchText = "nie ma takiego bonusu";
        Assert.Empty(viewModel.FilteredTattoos);
        Assert.True(viewModel.HasNoTattooResults);
    }

    [Fact]
    public void ToggleTattooInfoCommand_TogglesInfoPanelVisibility()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsTattooInfoExpanded);
        viewModel.ToggleTattooInfoCommand.Execute(null);
        Assert.True(viewModel.IsTattooInfoExpanded);
        viewModel.ToggleTattooInfoCommand.Execute(null);
        Assert.False(viewModel.IsTattooInfoExpanded);
    }

    [AvaloniaFact]
    public void TattoosView_RendersBonusListAndSelectedBonusDetails()
    {
        var viewModel = CreateViewModel();
        var view = new KilleropediaTattoosView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 720, Content = view };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(40, list.ItemCount);
        Assert.NotNull(viewModel.SelectedTattoo);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == viewModel.SelectedTattoo.Name);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == viewModel.SelectedTattoo.Description);

        window.Close();
    }

    [Fact]
    public void ShowTeacherOnMapCommand_OnlyInvokesCallbackForKnownRoom()
    {
        TeacherEntry? requestedTeacher = null;
        var teachers = TeacherCatalogLoader.Load();
        var mappedTeacher = teachers.First(teacher => teacher.HasRoomLocation);
        var viewModel = new KilleropediaViewModel(
            teachers,
            CreateBookStore(),
            null,
            teacher => requestedTeacher = teacher,
            CreateLoreCatalog());

        Assert.True(viewModel.ShowTeacherOnMapCommand.CanExecute(mappedTeacher));
        viewModel.ShowTeacherOnMapCommand.Execute(mappedTeacher);

        Assert.Same(mappedTeacher, requestedTeacher);
        Assert.False(viewModel.ShowTeacherOnMapCommand.CanExecute(mappedTeacher with { RoomVnum = null }));
    }

    [Fact]
    public async Task ShowBookLocationOnMapCommand_OnlyInvokesCallbackWhenLocationHasVnum()
    {
        var store = CreateBookStore();
        await store.SaveAsync(new BookCatalogDocument
        {
            Books =
            [
                new BookEntry
                {
                    Vnum = 16818,
                    Name = "ksiega zaklec",
                    LoadLocations =
                    [
                        "na mobie: Zeerith'din (Podmrok)",
                        "w pokoju: Biblioteka (vnum 1234)",
                    ],
                },
            ],
        }, TestContext.Current.CancellationToken);

        BookLoadLocationEntry? requestedLocation = null;
        var viewModel = new KilleropediaViewModel(
            TeacherCatalogLoader.Load(),
            store,
            null,
            loreCatalog: CreateLoreCatalog(),
            showBookLocationOnMap: location => requestedLocation = location);

        var book = Assert.Single(viewModel.FilteredBooks);
        var mobLocation = book.LoadLocationEntries[0];
        var roomLocation = book.LoadLocationEntries[1];

        Assert.False(viewModel.ShowBookLocationOnMapCommand.CanExecute(mobLocation));
        Assert.True(viewModel.ShowBookLocationOnMapCommand.CanExecute(roomLocation));

        viewModel.ShowBookLocationOnMapCommand.Execute(roomLocation);

        Assert.Same(roomLocation, requestedLocation);
    }

    [AvaloniaFact]
    public void TeachersView_RendersCatalogAndSelectedTeacherDetails()
    {
        var viewModel = CreateViewModel();
        var view = new KilleropediaTeachersView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 720, Content = view };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(14, view.FontSize);
        Assert.Contains("Inter", view.FontFamily.ToString(), StringComparison.Ordinal);
        Assert.Equal(Avalonia.Media.FontStyle.Normal, view.FontStyle);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, view.FontWeight);

        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(151, list.ItemCount);
        Assert.NotNull(viewModel.SelectedTeacher);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == viewModel.SelectedTeacher.Name);

        var offeringTabs = view.FindControl<TabControl>("TeacherOfferingTabs");
        Assert.NotNull(offeringTabs);
        Assert.Equal(2, offeringTabs!.ItemCount);
        Assert.Equal("Umiejętności", Assert.IsType<TabItem>(offeringTabs.Items[0]).Header);
        Assert.Equal("Triki", Assert.IsType<TabItem>(offeringTabs.Items[1]).Header);

        var detailsScroller = Assert.Single(
            view.GetVisualDescendants().OfType<ScrollViewer>(),
            scroller => scroller.Classes.Contains("killeropedia-content-scroll"));
        Assert.Equal(14, detailsScroller.Padding.Right);

        var bookPages = view.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("killeropedia-left-page") ||
                             border.Classes.Contains("killeropedia-right-page"))
            .ToArray();
        Assert.Equal(2, bookPages.Length);
        Assert.All(bookPages, page => Assert.IsType<Avalonia.Media.ImageBrush>(page.Background));

        window.Close();
    }

    [AvaloniaFact]
    public async Task BooksView_LoadsJsonFiltersSpellsAndKeepsDeveloperRefreshDisabled()
    {
        var store = CreateBookStore();
        await store.SaveAsync(new BookCatalogDocument
        {
            GeneratedAtUtc = DateTimeOffset.Parse("2026-07-13T12:00:00Z"),
            Classes = ["mag", "druid"],
            Books =
            [
                new BookEntry
                {
                    Vnum = 16818,
                    Name = "ksiega zaklec",
                    Classes = ["mag"],
                    Spells = ["force bolt", "decay"],
                    LoadLocations = ["na mobie: Zeerith'din (Podmrok)"],
                },
                new BookEntry
                {
                    Vnum = 25000,
                    Name = "druidzki notatnik",
                    Classes = ["druid"],
                    Spells = ["bear form"],
                },
            ],
        }, TestContext.Current.CancellationToken);
        var viewModel = new KilleropediaViewModel(
            TeacherCatalogLoader.Load(),
            store,
            null,
            loreCatalog: CreateLoreCatalog());
        viewModel.BookSearchText = "zeerith force";
        Assert.Equal(16818, Assert.Single(viewModel.FilteredBooks).Vnum);

        viewModel.BookSearchText = string.Empty;
        viewModel.SelectedBookClass = "druid";
        Assert.Equal(25000, Assert.Single(viewModel.FilteredBooks).Vnum);
        Assert.False(viewModel.IsBookRefreshEnabled);

        var view = new KilleropediaBooksView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 720, Content = view };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var refresh = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content?.ToString() == "Odśwież");
        Assert.False(refresh.IsEnabled);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "druidzki notatnik");

        var detailsScroller = Assert.Single(
            view.GetVisualDescendants().OfType<ScrollViewer>(),
            scroller => scroller.Classes.Contains("killeropedia-content-scroll"));
        Assert.Equal(14, detailsScroller.Padding.Right);

        window.Close();
    }

    [Fact]
    public void LoreCatalog_LoadsArticlesRecordsAndClickableRelations()
    {
        var catalog = CreateLoreCatalog();

        Assert.Equal(191, catalog.Entries.Count);
        var arras = Assert.Single(catalog.Entries, entry => entry.Id == "place:arras");
        Assert.Equal("Arras", arras.Name);
        Assert.Contains(arras.Sections, section => section.Title == "Władza i porządek");
        Assert.Contains(arras.Links, link => link.TargetId == "character:khabar");
        Assert.Contains(arras.Sources, source => source.DisplayText.Contains("arras.are", StringComparison.Ordinal));

        var easterial = Assert.Single(catalog.Entries, entry => entry.Id == "place:easterial");
        Assert.Contains(easterial.Links, link => link.TargetId == "place:dinneshere");
        Assert.Contains(easterial.Links, link => link.TargetId == "character:eltar-odwazny");
        var easterialCharacter = Assert.Single(easterial.Facts, fact => fact.Label == "Charakter osady");
        Assert.DoesNotContain("settlement", easterialCharacter.Label, StringComparison.OrdinalIgnoreCase);
        var silea = Assert.Single(catalog.Entries, entry => entry.Id == "place:silea");
        Assert.Contains(silea.Links, link => link.TargetId == "deity:silea");
        Assert.Contains(silea.Links, link => link.TargetId == "character:morhin-atyer");
        Assert.Contains(silea.Sections, section => section.Title == "Miasto i port");
        Assert.DoesNotContain(
            catalog.Entries.SelectMany(entry => entry.Facts).Select(fact => fact.Label),
            label => label.Contains('_'));
        Assert.DoesNotContain(
            catalog.Entries.SelectMany(entry => entry.Links).Select(link => link.RelationText),
            label => label.Contains('_'));
        Assert.Single(catalog.Entries, entry => entry.Id == "place:karakris");
    }

    [Fact]
    public void LoreSearch_MatchesWithoutDiacriticsAndNavigatesRelations()
    {
        var viewModel = CreateViewModel();

        viewModel.LoreSearchText = "eltar odwazny";
        Assert.Contains(viewModel.FilteredLoreEntries, entry => entry.Id == "character:eltar-odwazny");

        viewModel.LoreSearchText = string.Empty;
        viewModel.SelectedLoreCategory = "Miejsca";
        var easterial = Assert.Single(viewModel.FilteredLoreEntries, entry => entry.Id == "place:easterial");
        var eltarLink = Assert.Single(easterial.Links, link => link.TargetId == "character:eltar-odwazny");

        viewModel.NavigateLoreCommand.Execute(eltarLink);

        Assert.Equal("Wszystkie", viewModel.SelectedLoreCategory);
        Assert.Equal("character:eltar-odwazny", viewModel.SelectedLoreEntry?.Id);
    }

    [AvaloniaFact]
    public void LoreText_CreatesClickableLinksForKnownEntries()
    {
        var viewModel = CreateViewModel();
        var arras = Assert.Single(viewModel.LoreEntries, entry => entry.Id == "place:arras");
        var textBlock = new TextBlock { FontSize = 18 };

        LoreTextLinks.SetText(textBlock, "Z Arras trakt prowadzi do Carrallak.");
        LoreTextLinks.SetEntries(textBlock, viewModel.LoreEntries);
        LoreTextLinks.SetCurrentEntryId(textBlock, arras.Id);
        LoreTextLinks.SetCommand(textBlock, viewModel.NavigateLoreCommand);

        var inline = Assert.Single(textBlock.Inlines!.OfType<InlineUIContainer>());
        Assert.Equal(Avalonia.Media.BaselineAlignment.TextBottom, inline.BaselineAlignment);
        var button = Assert.IsType<Button>(inline.Child);
        var label = Assert.IsType<TextBlock>(button.Content);
        Assert.Equal(textBlock.FontSize, label.FontSize);
        var link = Assert.IsType<LoreLink>(button.CommandParameter);
        Assert.Equal("place:carrallak", link.TargetId);
        Assert.Contains("killeropedia-inline-link", button.Classes);

        button.Command!.Execute(button.CommandParameter);

        Assert.Equal("place:carrallak", viewModel.SelectedLoreEntry?.Id);
    }

    [Fact]
    public void LoreCatalog_InvalidExternalOverrideFallsBackToEmbeddedCatalog()
    {
        var dataDirectory = Path.Combine(_directory, "Data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "lore-catalog.json.gz"), "uszkodzony katalog");

        var catalog = LoreCatalogLoader.Load(_directory);

        Assert.Equal(191, catalog.Entries.Count);
        Assert.Equal("katalog wbudowany", catalog.SourceText);
        Assert.Contains("Nie udało się wczytać", catalog.Warning);
    }

    [AvaloniaFact]
    public void LoreView_RendersCatalogDetailsAndClickableRelations()
    {
        var viewModel = CreateViewModel();
        viewModel.LoreSearchText = "Arras";
        var view = new KilleropediaLoreView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 720, Content = view };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var searchBox = Assert.Single(view.GetVisualDescendants().OfType<TextBox>());
        Assert.True(searchBox.Focus());
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var searchBorder = Assert.Single(
            searchBox.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "PART_BorderElement");
        var searchBackground = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(searchBorder.Background);
        Assert.Equal(Avalonia.Media.Color.Parse("#D8EBD6A8"), searchBackground.Color);

        var categoryTabs = Assert.Single(
            view.GetVisualDescendants().OfType<ListBox>(),
            list => list.Classes.Contains("killeropedia-category-tabs"));
        Assert.Equal(viewModel.AvailableLoreCategories.Count, categoryTabs.ItemCount);
        Assert.DoesNotContain(view.GetVisualDescendants(), visual => visual is ComboBox);

        var list = Assert.Single(
            view.GetVisualDescendants().OfType<ListBox>(),
            candidate => !candidate.Classes.Contains("killeropedia-category-tabs"));
        Assert.True(list.ItemCount > 0);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Arras" || MudClient.App.Behaviors.SearchHighlight.GetText(text) == "Arras");
        Assert.Contains(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.Command == viewModel.NavigateLoreCommand);
        var inlineLinks = view.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("killeropedia-inline-link"))
            .ToArray();
        Assert.NotEmpty(inlineLinks);
        var inlineLink = inlineLinks[0];
        Assert.Equal(new Avalonia.Thickness(0), inlineLink.BorderThickness);
        Assert.Equal(
            Avalonia.Media.Colors.Transparent,
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(inlineLink.Background).Color);
        var inlineLabel = Assert.IsType<TextBlock>(inlineLink.Content);
        Assert.Equal(
            Avalonia.Media.Color.Parse("#2C2110"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(inlineLabel.Foreground).Color);
        Assert.NotNull(inlineLabel.TextDecorations);

        var detailsScroller = Assert.Single(
            view.GetVisualDescendants().OfType<ScrollViewer>(),
            scroller => scroller.Classes.Contains("killeropedia-content-scroll"));
        Assert.Equal(14, detailsScroller.Padding.Right);

        window.Close();
    }

    [Fact]
    public void RareCatalog_LoadsBundledArtifactSnapshot()
    {
        var rares = new RareCatalogStore(Path.Combine(_directory, "nie-istnieje.json")).Load();

        Assert.Equal(274, rares.Rares.Count);
        Assert.Contains(rares.Rares, rare => rare.Name == "trojzab Turlitha"
            && rare.ItemType == "wlocznia"
            && rare.Category == "artefakt");
    }

    [Fact]
    public void RareSearch_MatchesNameTypeAndCategory()
    {
        var viewModel = CreateViewModel();

        viewModel.RareSearchText = "trojzab";
        Assert.Contains(viewModel.FilteredRares, rare => rare.Vnum == 215);

        viewModel.RareSearchText = string.Empty;
        viewModel.SelectedRareCategory = "rzadki";
        Assert.DoesNotContain(viewModel.FilteredRares, rare => rare.Category == "artefakt");
        Assert.NotEmpty(viewModel.FilteredRares);
    }

    [AvaloniaFact]
    public void ArtifactsView_RendersRareListAndSelectedRareDetails()
    {
        var viewModel = CreateViewModel();
        var view = new KilleropediaArtifactsView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 720, Content = view };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = 1;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(274, list.ItemCount);
        Assert.NotNull(viewModel.SelectedRare);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == viewModel.SelectedRare.Name);

        window.Close();
    }

    [AvaloniaFact]
    public void BookNamingView_RendersClassWordSections()
    {
        var viewModel = CreateViewModel();
        var view = new KilleropediaBookNamingView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 720, Content = view };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var textBlocks = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("triumfu", textBlocks);
        Assert.Contains("magii", textBlocks);
        Assert.Contains("piasku", textBlocks);
        Assert.Contains("księga", textBlocks);

        window.Close();
    }

    // ====================================================================
    // Wędrowiec skill-tree class filter — multi-select / combining classes
    // ====================================================================

    [AvaloniaFact]
    public void AbilityClassOptions_DefaultsToWedrowiecOnly()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));

        Assert.Equal("Wedrowiec", viewModel.SelectedAbilityClassesSummaryText);
        Assert.All(viewModel.AbilityClassOptions, option =>
            Assert.Equal(
                string.Equals(option.Name, "Wedrowiec", StringComparison.OrdinalIgnoreCase), option.IsSelected));
    }

    [AvaloniaFact]
    public void ToggleAbilityClass_SelectingAnotherClass_DropsWedrowiecBaseline()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        Assert.Equal("Paladyn", viewModel.SelectedAbilityClassesSummaryText);
        Assert.True(viewModel.AbilityClassOptions.Single(option => option.Name == "Paladyn").IsSelected);
        Assert.False(viewModel.AbilityClassOptions.Single(option => option.Name == "Wedrowiec").IsSelected);
        Assert.Contains(viewModel.FilteredAbilities, item => item.Name == "aura of protection");
    }

    [AvaloniaFact]
    public void ToggleAbilityClass_SelectingTwoClasses_CombinesTheirAbilitiesIntoOneTree()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)),
            MakeAbility("desert bond", "Nomad", "Nomad", ("Nomad", 8), ("Wedrowiec", 1)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");
        viewModel.ToggleAbilityClassCommand.Execute("Nomad");

        Assert.Equal("Nomad, Paladyn", viewModel.SelectedAbilityClassesSummaryText);
        Assert.Contains(viewModel.FilteredAbilities, item => item.Name == "aura of protection");
        Assert.Contains(viewModel.FilteredAbilities, item => item.Name == "desert bond");
    }

    [AvaloniaFact]
    public void ToggleAbilityClass_TogglingSameClassAgain_RemovesItAndFallsBackToWedrowiec()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");
        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        Assert.Equal("Wedrowiec", viewModel.SelectedAbilityClassesSummaryText);
        Assert.DoesNotContain(viewModel.FilteredAbilities, item => item.Name == "aura of protection");
    }

    [AvaloniaFact]
    public void ToggleAbilityClass_ReselectingWedrowiec_ClearsOtherSelections()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)),
            MakeAbility("desert bond", "Nomad", "Nomad", ("Nomad", 8), ("Wedrowiec", 1)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");
        viewModel.ToggleAbilityClassCommand.Execute("Nomad");
        viewModel.ToggleAbilityClassCommand.Execute("Wedrowiec");

        Assert.Equal("Wedrowiec", viewModel.SelectedAbilityClassesSummaryText);
        Assert.True(viewModel.AbilityClassOptions.Single(option => option.Name == "Wedrowiec").IsSelected);
        Assert.All(
            viewModel.AbilityClassOptions.Where(option => option.Name != "Wedrowiec"),
            option => Assert.False(option.IsSelected));
    }

    [AvaloniaFact]
    public void ToggleAbilityClass_UniversalAbility_IsNotDuplicatedWhenTwoClassesAreCombined()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("smite evil", "Paladyn", "kazda specjalizacja", ("Paladyn", 4), ("Nomad", 6), ("Wedrowiec", 4)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");
        viewModel.ToggleAbilityClassCommand.Execute("Nomad");

        Assert.Single(viewModel.FilteredAbilities, item => item.Name == "smite evil");
    }

    [AvaloniaFact]
    public void AllAbilities_SameNameCapturedUnderSeveralClasses_CollapsesToOneEntryForWedrowiec()
    {
        // "/mapuj <klasa>" captures "help <name>" once per class's seed list, so a skill shared by
        // several classes (e.g. "axe") ends up as several AbilityCaptureEntry rows in the saved
        // document — one per class whose /mapuj run happened to capture it — even though they all
        // describe the exact same universal ability. Browsing base Wędrowiec must show it once.
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("axe", "Paladyn", "kazda specjalizacja", ("Wojownik", 1), ("Paladyn", 1), ("Wedrowiec", 1)),
            MakeAbility("axe", "Wojownik", "kazda specjalizacja", ("Wojownik", 1), ("Paladyn", 1), ("Wedrowiec", 1)),
            MakeAbility("axe", "Barbarzynca", "kazda specjalizacja", ("Wojownik", 1), ("Paladyn", 1), ("Wedrowiec", 1)));

        Assert.Single(viewModel.FilteredAbilities, item => item.Name == "axe");
    }

    // ====================================================================
    // NewAbilities / HasNewAbilities — "Sprawdź co zyskasz" button's data source
    // ====================================================================

    [AvaloniaFact]
    public void NewAbilities_DefaultWedrowiecBaseline_IsEmpty()
    {
        // Browsing the baseline (no specialization picked) excludes specialization-gated
        // abilities entirely — see AbilitySkillTreeEntry.Create — so there's nothing to preview
        // yet, and "co zyskasz" has nothing to show until a specialization is selected.
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));

        Assert.Empty(viewModel.NewAbilities);
        Assert.False(viewModel.HasNewAbilities);
    }

    [AvaloniaFact]
    public void NewAbilities_AfterSelectingASpecialization_ContainsItsPreviewAbilities()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        var gained = Assert.Single(viewModel.NewAbilities);
        Assert.Equal("aura of protection", gained.Name);
        Assert.False(gained.IsOwned);
        Assert.True(viewModel.HasNewAbilities);
    }

    [AvaloniaFact]
    public void NewAbilities_UniversalAbility_NeverCountsAsNew()
    {
        // "kazda specjalizacja" abilities are already owned regardless of browsed class — nothing
        // to gain from picking a specialization, so they must never show up in the "co zyskasz" list.
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("smite evil", "Paladyn", "kazda specjalizacja", ("Paladyn", 4), ("Wedrowiec", 4)));

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        Assert.DoesNotContain(viewModel.NewAbilities, item => item.Name == "smite evil");
    }

    [AvaloniaFact]
    public void NewAbilities_DeselectingBackToWedrowiec_ClearsTheList()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));
        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");
        Assert.True(viewModel.HasNewAbilities);

        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        Assert.Empty(viewModel.NewAbilities);
        Assert.False(viewModel.HasNewAbilities);
    }

    // ====================================================================
    // NewTattoos / HasNewTattoos — tattoo class bonuses in the same "Sprawdź co
    // zyskasz" flyout as NewAbilities. Uses the real embedded tattoo catalog
    // (read-only static data, no test-isolation hazard) rather than a fake one.
    // ====================================================================

    [AvaloniaFact]
    public void NewTattoos_DefaultWedrowiecBaseline_IsEmpty()
    {
        var viewModel = CreateViewModelWithAbilities();

        Assert.Empty(viewModel.NewTattoos);
        Assert.False(viewModel.HasNewTattoos);
    }

    [AvaloniaFact]
    public void NewTattoos_AfterSelectingAClass_ContainsItsTattoos()
    {
        var viewModel = CreateViewModelWithAbilities();

        viewModel.ToggleAbilityClassCommand.Execute("Kleryk");

        Assert.True(viewModel.HasNewTattoos);
        Assert.Contains(viewModel.NewTattoos, tattoo => tattoo.Name == "wybraniec bogów");
        Assert.DoesNotContain(viewModel.NewTattoos, tattoo => tattoo.Name == "mistrz zaklęć"); // Mag-only
    }

    [AvaloniaFact]
    public void NewTattoos_ClassNameFromRawGameTextLacksDiacritics_StillMatchesTheHandTypedCatalog()
    {
        // AbilityClasses (and so ToggleAbilityClassCommand's argument) comes from the game's own
        // raw "help" text, which drops Polish diacritics entirely — so a real browsed class here
        // is "Barbarzynca", not "Barbarzyńca" as tattoos.json (hand-typed with proper spelling)
        // gates its Barbarzyńca-only bonuses. A plain string comparison would silently match
        // nothing for this (or any other diacritic-bearing class, e.g. "Zlodziej"/"Złodziej").
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("berserk", "Barbarzynca", "Barbarzynca", ("Barbarzynca", 10), ("Wedrowiec", 10)));

        viewModel.ToggleAbilityClassCommand.Execute("Barbarzynca");

        Assert.True(viewModel.HasNewTattoos);
        Assert.Contains(viewModel.NewTattoos, tattoo => tattoo.Name == "szaleniec");
    }

    [AvaloniaFact]
    public void NewTattoos_UniversalTattoo_NeverAppears()
    {
        // A "Wszystkie" tattoo already applies to every class, so it isn't something picking a
        // specialization grants — it must never show up as a "gain" regardless of selection.
        var viewModel = CreateViewModelWithAbilities();

        viewModel.ToggleAbilityClassCommand.Execute("Kleryk");

        Assert.DoesNotContain(viewModel.NewTattoos, tattoo => tattoo.Name == "charyzmatyczny przywódca");
    }

    [AvaloniaFact]
    public void NewTattoos_DeselectingBackToWedrowiec_ClearsTheList()
    {
        var viewModel = CreateViewModelWithAbilities();
        viewModel.ToggleAbilityClassCommand.Execute("Kleryk");
        Assert.True(viewModel.HasNewTattoos);

        viewModel.ToggleAbilityClassCommand.Execute("Kleryk");

        Assert.Empty(viewModel.NewTattoos);
        Assert.False(viewModel.HasNewTattoos);
    }

    // ====================================================================
    // CurrentCharacterLevel — an owned ability above the connected character's
    // actual level also counts as "new" (not yet reached).
    // ====================================================================

    [AvaloniaFact]
    public void NewAbilities_UniversalAbilityAboveCurrentLevel_CountsAsNew()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("smite evil", "Paladyn", "kazda specjalizacja", ("Paladyn", 12), ("Wedrowiec", 12)));

        viewModel.SetCharacterLevel(5);

        var gained = Assert.Single(viewModel.NewAbilities);
        Assert.Equal("smite evil", gained.Name);
        Assert.True(gained.IsOwned);
        Assert.True(viewModel.HasNewAbilities);
    }

    [AvaloniaFact]
    public void NewAbilities_UniversalAbilityAtOrBelowCurrentLevel_DoesNotCountAsNew()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("smite evil", "Paladyn", "kazda specjalizacja", ("Paladyn", 4), ("Wedrowiec", 4)));

        viewModel.SetCharacterLevel(4);

        Assert.Empty(viewModel.NewAbilities);
        Assert.False(viewModel.HasNewAbilities);
    }

    [AvaloniaFact]
    public void NewAbilities_WithoutAConnectedCharacter_IgnoresLevelEntirely()
    {
        // CurrentCharacterLevel defaults to null (never connected / just disconnected) — a level
        // gate that can't be evaluated must never silently mark something "new".
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("smite evil", "Paladyn", "kazda specjalizacja", ("Paladyn", 30), ("Wedrowiec", 30)));

        Assert.Null(viewModel.CurrentCharacterLevel);
        Assert.Empty(viewModel.NewAbilities);
    }

    [AvaloniaFact]
    public void SetCharacterLevel_Null_ClearsPreviouslyGainedByLevel()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("smite evil", "Paladyn", "kazda specjalizacja", ("Paladyn", 12), ("Wedrowiec", 12)));
        viewModel.SetCharacterLevel(5);
        Assert.True(viewModel.HasNewAbilities);

        viewModel.SetCharacterLevel(null);

        Assert.Empty(viewModel.NewAbilities);
        Assert.False(viewModel.HasNewAbilities);
    }

    // ====================================================================
    // "Sprawdź co zyskasz" row hover pulses the matching node on the canvas
    // ====================================================================

    [AvaloniaFact]
    public void SkillsView_HoveringANewAbilityRow_PulsesTheMatchingNodeOnTheCanvas()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));
        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        var view = new KilleropediaSkillsView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var treeCanvas = view.FindControl<AbilitySkillTreeCanvas>("TreeCanvas");
        Assert.NotNull(treeCanvas);
        var ability = Assert.Single(viewModel.NewAbilities);
        var row = new Border { DataContext = ability };

        var enterMethod = typeof(KilleropediaSkillsView).GetMethod(
            "OnNewAbilityRowPointerEntered", BindingFlags.NonPublic | BindingFlags.Instance)!;
        enterMethod.Invoke(view, [row, null]);

        Assert.Same(ability, treeCanvas!.HighlightedAbility);

        var exitMethod = typeof(KilleropediaSkillsView).GetMethod(
            "OnNewAbilityRowPointerExited", BindingFlags.NonPublic | BindingFlags.Instance)!;
        exitMethod.Invoke(view, [row, null]);

        Assert.Null(treeCanvas.HighlightedAbility);

        window.Close();
    }

    [AvaloniaFact]
    public void SkillsView_CheckWhatYouGainButton_ListsPreviewAbilitiesWithTooltips()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));
        viewModel.ToggleAbilityClassCommand.Execute("Paladyn");

        var view = new KilleropediaSkillsView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var button = view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content?.ToString() == "Sprawdź co zyskasz");
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("aura of protection", texts);

        window.Close();
    }

    [AvaloniaFact]
    public void SkillsView_CheckWhatYouGainButton_WithNothingNew_ShowsHintInstead()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("aura of protection", "Paladyn", "Paladyn", ("Paladyn", 13), ("Wedrowiec", 1)));

        var view = new KilleropediaSkillsView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var button = view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content?.ToString() == "Sprawdź co zyskasz");
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain("aura of protection", texts);
        Assert.Contains(texts, text => text is not null && text.Contains("wybierz", StringComparison.OrdinalIgnoreCase));

        window.Close();
    }

    [AvaloniaFact]
    public void AbilityClassOptions_IncludesWandererSpecializationsNotJustRealClasses()
    {
        // A Mag's spell schools (e.g. "Nekromancja") are each their own separately pickable
        // Wędrowiec specialization, distinct from "Mag" itself — so browsing must offer them as
        // their own option even though the game never lists "Nekromancja" as a real class in
        // "Dostepne dla klas" (only WandererSpecialization says so).
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("raise zombie", "Mag", "Nekromancja", ("Mag", 20), ("Wedrowiec", 20)));

        Assert.Contains(viewModel.AbilityClassOptions, option => option.Name == "Nekromancja");
        Assert.Contains(viewModel.AbilityClassOptions, option => option.Name == "Mag");
    }

    [AvaloniaFact]
    public void ToggleAbilityClass_SelectingASpellSchoolSpecialization_ShowsItsGatedSpellAsPreview()
    {
        var viewModel = CreateViewModelWithAbilities(
            MakeAbility("raise zombie", "Mag", "Nekromancja", ("Mag", 20), ("Wedrowiec", 20)));

        viewModel.ToggleAbilityClassCommand.Execute("Nekromancja");

        var entry = Assert.Single(viewModel.FilteredAbilities, item => item.Name == "raise zombie");
        Assert.False(entry.IsOwned);
    }

    private KilleropediaViewModel CreateViewModelWithAbilities(params AbilityCaptureEntry[] entries)
    {
        var store = new AbilityCaptureStore(Path.Combine(_directory, "ability-help.json"));
        Task.Run(() => store.SaveAsync(new AbilityCaptureDocument { Entries = entries.ToList() })).GetAwaiter().GetResult();
        return new(
            TeacherCatalogLoader.Load(),
            CreateBookStore(),
            null,
            loreCatalog: CreateLoreCatalog(),
            rareCatalogStore: CreateRareStore(),
            abilityCaptureStore: store);
    }

    private static AbilityCaptureEntry MakeAbility(
        string name,
        string primaryClass,
        string? wandererSpecialization,
        params (string ClassName, int MinLevel)[] classLevels) => new()
    {
        Name = name,
        Class = primaryClass,
        WandererSpecialization = wandererSpecialization,
        AvailableForClasses = classLevels
            .Select(entry => new ClassLevelRequirement(entry.ClassName, entry.MinLevel))
            .ToList(),
    };

    // ====================================================================
    // Artefakty (try) catalog — parsing, same-name merge, class-sort, rarelist enrichment
    // ====================================================================

    [AvaloniaFact]
    public void LoadArtifactCatalog_DuplicateNameAcrossCaptures_KeepsTheMostCompleteOne()
    {
        var sparse = new ArtifactTryEntry
        {
            Number = 1,
            RawText = "Postanawiasz dokladniej obejrzec zloty pierscien.\n\n" +
                "Zloty pierscien jest w znakomitym stanie.\n\n" +
                "Zloty pierscien prawie nic nie wazy, przedmiot ten wykonano z materialu 'zloto'.",
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var rich = new ArtifactTryEntry
        {
            Number = 2,
            RawText = "Postanawiasz dokladniej obejrzec zloty pierscien.\n\n" +
                "Zloty pierscien jest w znakomitym stanie.\n\n" +
                "Zloty pierscien prawie nic nie wazy, przedmiot ten wykonano z materialu 'zloto'.\n" +
                "Przedmiot ten moga uzywac tylko paladyni.\n" +
                "Wplywa na charyzme o 5.\n" +
                "Dodaje detect_evil.",
            CapturedAt = DateTimeOffset.UtcNow,
        };

        var viewModel = CreateViewModelWithArtifacts(sparse, rich);

        var artifact = Assert.Single(viewModel.FilteredArtifacts);
        Assert.Equal(["Paladyn"], artifact.AllowedClassesOnly);
        Assert.Contains(artifact.GrantedAbilities, ability => ability == "detect_evil");
    }

    [AvaloniaFact]
    public void ToggleArtifactSortClass_PutsAFittingItemBeforeANonFittingOne()
    {
        var forPaladyn = new ArtifactTryEntry
        {
            Number = 1,
            RawText = "Postanawiasz dokladniej obejrzec tarcze paladyna.\n\n" +
                "Tarcza paladyna jest w znakomitym stanie.\n\n" +
                "Przedmiot ten moga uzywac tylko paladyni.",
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var forMag = new ArtifactTryEntry
        {
            Number = 2,
            RawText = "Postanawiasz dokladniej obejrzec laske maga.\n\n" +
                "Laska maga jest w znakomitym stanie.\n\n" +
                "Przedmiot ten moga uzywac tylko magowie.",
            CapturedAt = DateTimeOffset.UtcNow,
        };

        var viewModel = CreateViewModelWithArtifacts(forMag, forPaladyn);
        Assert.Equal("Laska maga", viewModel.FilteredArtifacts[0].Name);

        viewModel.ToggleArtifactSortClassCommand.Execute("Paladyn");

        Assert.Equal("Tarcza paladyna", viewModel.FilteredArtifacts[0].Name);
    }

    [AvaloniaFact]
    public void SelectedRareArtifactDetail_MatchesByName_WhenRareAndArtifactShareAName()
    {
        var artifact = new ArtifactTryEntry
        {
            Number = 1,
            RawText = "Postanawiasz dokladniej obejrzec smoczy pierscien.\n\n" +
                "Smoczy pierscien jest w znakomitym stanie.\n\n" +
                "Przedmiot ten moga uzywac tylko paladyni.",
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var rareStore = CreateRareStore();
        Task.Run(() => rareStore.SaveAsync(new RareCatalogDocument
        {
            Rares = [new RareEntry { Vnum = 1234, Name = "Smoczy pierscien", ItemType = "ring", Category = "artefakt" }],
        })).GetAwaiter().GetResult();
        var artifactStore = new ArtifactTryStore(Path.Combine(_directory, "artifact-try.json"));
        Task.Run(() => artifactStore.SaveAsync(new ArtifactTryDocument { Entries = [artifact] })).GetAwaiter().GetResult();

        var viewModel = new KilleropediaViewModel(
            TeacherCatalogLoader.Load(),
            CreateBookStore(),
            null,
            loreCatalog: CreateLoreCatalog(),
            rareCatalogStore: rareStore,
            artifactTryStore: artifactStore);

        viewModel.SelectedRare = viewModel.FilteredRares.Single(rare => rare.Vnum == 1234);

        Assert.True(viewModel.HasSelectedRareArtifactDetail);
        Assert.Equal(["Paladyn"], viewModel.SelectedRareArtifactDetail!.AllowedClassesOnly);
    }

    [AvaloniaFact]
    public void SelectedArtifactRareDetail_MatchesByName_WhenArtifactAndRareShareAName()
    {
        var artifact = new ArtifactTryEntry
        {
            Number = 1,
            RawText = "Postanawiasz dokladniej obejrzec smoczy pierscien.\n\n" +
                "Smoczy pierscien jest w znakomitym stanie.\n\n" +
                "Przedmiot ten moga uzywac tylko paladyni.",
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var rareStore = CreateRareStore();
        Task.Run(() => rareStore.SaveAsync(new RareCatalogDocument
        {
            Rares = [new RareEntry { Vnum = 1234, Name = "Smoczy pierscien", ItemType = "ring", Category = "artefakt" }],
        })).GetAwaiter().GetResult();
        var artifactStore = new ArtifactTryStore(Path.Combine(_directory, "artifact-try.json"));
        Task.Run(() => artifactStore.SaveAsync(new ArtifactTryDocument { Entries = [artifact] })).GetAwaiter().GetResult();

        var viewModel = new KilleropediaViewModel(
            TeacherCatalogLoader.Load(),
            CreateBookStore(),
            null,
            loreCatalog: CreateLoreCatalog(),
            rareCatalogStore: rareStore,
            artifactTryStore: artifactStore);

        viewModel.SelectedArtifact = viewModel.FilteredArtifacts.Single(entry => entry.Name == "Smoczy pierscien");

        Assert.True(viewModel.HasSelectedArtifactRareDetail);
        Assert.Equal(1234, viewModel.SelectedArtifactRareDetail!.Vnum);
        Assert.Equal("artefakt", viewModel.SelectedArtifactRareDetail!.Category);
    }

    private KilleropediaViewModel CreateViewModelWithArtifacts(params ArtifactTryEntry[] entries)
    {
        var store = new ArtifactTryStore(Path.Combine(_directory, "artifact-try.json"));
        Task.Run(() => store.SaveAsync(new ArtifactTryDocument { Entries = entries.ToList() })).GetAwaiter().GetResult();
        return new(
            TeacherCatalogLoader.Load(),
            CreateBookStore(),
            null,
            loreCatalog: CreateLoreCatalog(),
            rareCatalogStore: CreateRareStore(),
            artifactTryStore: store);
    }

    private KilleropediaViewModel CreateViewModel() =>
        new(
            TeacherCatalogLoader.Load(),
            CreateBookStore(),
            null,
            loreCatalog: CreateLoreCatalog(),
            rareCatalogStore: CreateRareStore(),
            abilityCaptureStore: CreateAbilityCaptureStore(),
            artifactTryStore: CreateArtifactStore());

    private static LoreCatalogData CreateLoreCatalog() => LoreCatalogLoader.LoadEmbedded();

    private BookCatalogStore CreateBookStore() =>
        new(Path.Combine(_directory, "killeropedia-books.json"));

    private RareCatalogStore CreateRareStore() =>
        new(Path.Combine(_directory, "killeropedia-rares.json"));

    private AbilityCaptureStore CreateAbilityCaptureStore() =>
        new(Path.Combine(_directory, "ability-help.json"));

    private ArtifactTryStore CreateArtifactStore() =>
        new(Path.Combine(_directory, "artifact-try.json"));
}
