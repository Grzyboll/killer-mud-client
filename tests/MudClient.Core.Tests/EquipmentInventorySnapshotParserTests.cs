using MudClient.Core.Equipment;

namespace MudClient.Core.Tests;

public sealed class EquipmentInventorySnapshotParserTests
{
    [Fact]
    public void RandomItemCatalogKeepsServerExtractedNamesByWearLocation()
    {
        var categories = RandomItemNameCatalog.NamesByCategory;

        Assert.Equal(16, categories.Count);
        Assert.Contains("szafir", RandomItemNameCatalog.GetNames(RandomItemCategory.Gem));
        Assert.Contains("kolczyk", RandomItemNameCatalog.GetNames(RandomItemCategory.Ear));
        Assert.Contains("nagolenniki", RandomItemNameCatalog.GetNames(RandomItemCategory.Legs));
        Assert.Contains("bransoletka", RandomItemNameCatalog.GetNames(RandomItemCategory.Wrist));
        Assert.Contains("jablko", RandomItemNameCatalog.GetNames(RandomItemCategory.Food));
        Assert.All(categories.Values, names => Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    [Fact]
    public void RandomItemCatalogLabelsOnlyUnambiguousDisplayedNames()
    {
        Assert.Equal("gem", RandomItemNameCatalog.GetPolishSlotLabel("szafir gwiazdzisty"));
        Assert.Equal("twarz", RandomItemNameCatalog.GetPolishSlotLabel("maska Szczurze Oblicze"));
        Assert.Equal("jedzenie", RandomItemNameCatalog.GetPolishSlotLabel("jablko"));
        Assert.Equal("nadgarstek", RandomItemNameCatalog.GetPolishSlotLabel("bransoleta z ametystem"));
        Assert.Equal(string.Empty, RandomItemNameCatalog.GetPolishSlotLabel("kamien ksiezycowy"));
    }

    [Fact]
    public void RandomItemCatalogBuildsObservedBulkCommandsForExactTypeGemsAndJewellery()
    {
        var groups = RandomItemNameCatalog.GetBulkGroups([
            "srebrny kolczyk", "zloty kolczyk", "bransoleta z ametystem",
            "szafir gwiazdzisty", "rubin"
        ]);

        Assert.Contains(groups, group => group.CommandArgument == "all.kolczyk" && group.Count == 2);
        Assert.Contains(groups, group => group.CommandArgument == "all.klejnot" && group.Count == 2);
        Assert.Contains(groups, group => group.CommandArgument == "all.gem" && group.Count == 3);
    }

    [Fact]
    public void RandomItemCatalogShowsBulkActionsOnlyForTheSelectedItemsGroup()
    {
        var groups = RandomItemNameCatalog.GetBulkGroups(
            ["srebrny kolczyk", "zloty kolczyk", "brazowa bransoleta"],
            "brazowa bransoleta");

        var group = Assert.Single(groups);
        Assert.Equal("all.gem", group.CommandArgument);
        Assert.Equal(3, group.Count);
    }

    [Theory]
    [InlineData("gigantyczny tatuaz ukazujacy rune zniszczenia", "gigantyczny tatuaz reprezetujacy golema stali", "all.tatuaz")]
    [InlineData("tajemniczy kamien mocy", "zagadkowy kamien mocy", "all.kamien")]
    public void RandomItemCatalogBuildsExactFallbackGroupForRepeatedNounOutsideCatalog(string first, string second, string command)
    {
        var groups = RandomItemNameCatalog.GetBulkGroups([first, second], first);

        var group = Assert.Single(groups);
        Assert.Equal(command, group.CommandArgument);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void ParsesOnlyConfirmedEquipmentTableRows()
    {
        const string text = "nie jest to ekwipunek\nUzywasz:\n<pierwsza bron> miecz\n<uzywane jako tarcza> tarcza\n";
        Assert.True(EquipmentInventorySnapshotParser.TryParseEquipment(text, out var rows));
        Assert.Collection(rows, first => Assert.Equal(("pierwsza bron", "miecz"), (first.Location, first.Name)), second => Assert.Equal(("uzywane jako tarcza", "tarcza"), (second.Location, second.Name)));
    }

    [Fact]
    public void InventoryMustHaveItsOwnConfirmedHeading()
    {
        Assert.False(EquipmentInventorySnapshotParser.TryParseInventory("miecz\ntarcza", out var rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void InventoryStopsAtTheObservedPrompt()
    {
        const string text = "Nosisz przy sobie:\n( 2) czarna ksiega\n<428/428hp 130/130mv> pokoj\n";
        Assert.True(EquipmentInventorySnapshotParser.TryParseInventory(text, out var rows));
        var item = Assert.Single(rows);
        Assert.Equal("( 2) czarna ksiega", item.Name);
    }

    [Fact]
    public void ExtractsDurabilityAndLeavesOtherParentheticalAnnotationsUntouched()
    {
        const string item = "obraczka Niraso (29%) (pod pancernymi rekawicami)";

        Assert.Equal(29, EquipmentInventorySnapshotParser.GetDurabilityPercent(item));
        Assert.Equal("obraczka Niraso  (pod pancernymi rekawicami)", EquipmentInventorySnapshotParser.WithoutDurabilityPercent(item));
        Assert.Null(EquipmentInventorySnapshotParser.GetDurabilityPercent("tajemniczy kamien mocy"));
    }

    [Theory]
    [InlineData("Upuszczasz koral.")]
    [InlineData("Podnosisz koral.")]
    [InlineData("Wkladasz koral do zszywanej torby.")]
    [InlineData("Wyjmujesz koral z zszywanej torby.")]
    [InlineData("Sprzedajesz koral za 30 miedzianych monet.")]
    [InlineData("Kupujesz zdobione lustro za 14 miedzianych monet.")]
    [InlineData("Agron daje ci ksiege.")]
    [InlineData("Dajesz ksiege Agronowi.")]
    public void RecognizesObservedInventoryMutationMessages(string line)
    {
        Assert.True(EquipmentInventorySnapshotParser.IsInventoryMutationMessage(line));
    }

    [Fact]
    public void DoesNotTreatAnUnrelatedSentenceAsInventoryMutation()
    {
        Assert.False(EquipmentInventorySnapshotParser.IsInventoryMutationMessage("Koral mówi: Podnosisz mnie?"));
    }

    [Fact]
    public void DoesNotTreatObservedCoinPilePickupAsInventoryMutation()
    {
        Assert.False(EquipmentInventorySnapshotParser.IsInventoryMutationMessage("Podnosisz kupke monet."));
        Assert.Null(EquipmentInventorySnapshotParser.GetInventoryMutationKind("Podnosisz kupke monet."));
        Assert.False(EquipmentInventorySnapshotParser.IsInventoryMutationMessage("Wyjmujesz kupke monet z ciala."));
        Assert.Null(EquipmentInventorySnapshotParser.GetInventoryMutationKind("Wyjmujesz kupke monet z ciala."));
    }

    [Theory]
    [InlineData("Podnosisz koral.", InventoryMutationKind.Added)]
    [InlineData("Kupujesz zdobione lustro.", InventoryMutationKind.Added)]
    [InlineData("Upuszczasz koral.", InventoryMutationKind.Removed)]
    [InlineData("Sprzedajesz koral.", InventoryMutationKind.Removed)]
    [InlineData("Dajesz ksiege Agronowi.", InventoryMutationKind.Removed)]
    [InlineData("Agron daje ci ksiege.", InventoryMutationKind.Added)]
    [InlineData("Wkladasz koral do torby.", InventoryMutationKind.PutIntoContainer)]
    [InlineData("Wyjmujesz koral z torby.", InventoryMutationKind.TakenFromContainer)]
    public void ClassifiesObservedInventoryMutationMessages(string line, InventoryMutationKind expected)
    {
        Assert.Equal(expected, EquipmentInventorySnapshotParser.GetInventoryMutationKind(line));
    }

    [Fact]
    public void ManualContainerReferenceUsesGroundInventoryEquipmentOccurrenceOrder()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("plecy", "podrozna torba")],
            [new InventoryItem("mala torba"), new InventoryItem("zszywana torba")],
            [new InventoryItem("torba na ziemi")]);

        var resolved = EquipmentInventorySnapshotParser.TryResolveInventoryItemIndex(snapshot, "3.torba", out var index);

        Assert.True(resolved);
        Assert.Equal(1, index);
    }

    [Fact]
    public void ExamineDuplicateUsesInventoryBeforeEquipmentAndFirstWordOnly()
    {
        var snapshot = new EquipmentInventorySnapshot([new EquipmentItem("nadgarstek", "bransoleta c")], [new InventoryItem("bransoleta a"), new InventoryItem("bransoleta b")]);
        var occurrence = EquipmentInventorySnapshotParser.GetExamineOccurrence(snapshot, "bransoleta c", false, 0);
        Assert.Equal(3, occurrence);
        Assert.Equal("examine 3.bransoleta", EquipmentInventorySnapshotParser.BuildExamineCommand("bransoleta c", occurrence));
    }

    [Fact]
    public void ExamineIgnoresParentheticalVisualAnnotationsInTheName()
    {
        Assert.Equal("examine szkarlatny", EquipmentInventorySnapshotParser.BuildExamineCommand("(pulsuje) szkarlatny mlot (95%)", 1));
    }

    [Fact]
    public void PlainItemNameRemovesColoursAndParentheticalAnnotations()
    {
        const string item = "\u001b[31m(pulsuje) szkarlatny mlot bojowy (95%) (pod rekawicami)\u001b[0m";

        Assert.Equal("szkarlatny mlot bojowy", EquipmentInventorySnapshotParser.GetPlainItemName(item));
    }

    [Fact]
    public void ParsesOnlyContainerContentsFromObservedExamineResponse()
    {
        const string response = "Opis torby.\n\nZszywana torba (nosisz przy sobie) zawiera:\n(pulsuje) dwureczny miecz 'Krwawa Klinga'\nszczurze oko\n\n<700/700hp 100/100mv> pokoj";

        Assert.True(EquipmentInventorySnapshotParser.TryParseContainerContents(response, "zszywana torba (74%)", out var contents));
        Assert.Collection(contents,
            first => Assert.Equal("(pulsuje) dwureczny miecz 'Krwawa Klinga'", first.Name),
            second => Assert.Equal("szczurze oko", second.Name));
    }

    [Fact]
    public void DoesNotTreatOrdinaryExamineResponseAsAContainer()
    {
        Assert.False(EquipmentInventorySnapshotParser.TryParseContainerContents("Zszywana torba polyskuje magicznym blaskiem.", "zszywana torba", out var contents));
        Assert.Empty(contents);
    }

    [Fact]
    public void ParsesGroundContainerContentsWhenServerUsesItsShorterContainerName()
    {
        const string response = "Wielki kufer wykonany z mithrilowej blachy.\nMithrilowy kufer (lezy na ziemi) zawiera:\nkupka monet\n";

        Assert.True(EquipmentInventorySnapshotParser.TryParseContainerContents(response, "Wielki kufer wykonany z mithrilowej blachy", out var contents));
        Assert.Equal("kupka monet", Assert.Single(contents).Name);
    }

    [Fact]
    public void ParsesCorpseContentsWhenTheServerAbbreviatesTheCorpseHeader()
    {
        const string response = "Zmasakrowane cialo gwardzisty lezy tu i psuje sie powoli.\nCialo gwardzisty (lezy na ziemi) zawiera:\nszkarlatna zbroja\n";

        Assert.True(EquipmentInventorySnapshotParser.TryParseContainerContents(response, "Zmasakrowane cialo gwardzisty", out var contents, acceptServerContainerAlias: true));
        Assert.Equal("szkarlatna zbroja", Assert.Single(contents).Name);
    }

    [Theory]
    [InlineData("Sluzacy nie zyje!!")]
    [InlineData("Sluzacy pada na ziemie... MARTWY.")]
    [InlineData("Gwardzista pada na ziemie... MARTWY!!")]
    public void RecognizesObservedOpponentDeathAnnouncements(string line)
    {
        Assert.True(EquipmentInventorySnapshotParser.ContainsObservedOpponentDeath(line));
    }

    [Fact]
    public void RecognizesGroundCorpseButNotChest()
    {
        Assert.True(EquipmentInventorySnapshotParser.IsGroundCorpse("Zmasakrowane cialo gwardzisty"));
        Assert.False(EquipmentInventorySnapshotParser.IsGroundCorpse("Wielki kufer wykonany z mithrilowej blachy"));
    }

    [Fact]
    public void PrefixCollisionUsesServerOccurrenceOrder()
    {
        var snapshot = new EquipmentInventorySnapshot([new EquipmentItem("unoszacy", "krysztal Tellany")], [new InventoryItem("krysztalowy pierscien")]);
        Assert.Equal(2, EquipmentInventorySnapshotParser.GetExamineOccurrence(snapshot, "krysztal Tellany", false, 0));
        Assert.Equal("examine 2.krysztal", EquipmentInventorySnapshotParser.BuildExamineCommand("krysztal Tellany", 2));
    }

    [Fact]
    public void OccurrenceCountsMatchingWordBeyondTheFirstWord()
    {
        var snapshot = new EquipmentInventorySnapshot([new EquipmentItem("ucho", "kolczyk wszystkich bogow")], [new InventoryItem("fikusny kolczyk")]);
        Assert.Equal(2, EquipmentInventorySnapshotParser.GetExamineOccurrence(snapshot, "kolczyk wszystkich bogow", false, 0));
    }

    [Fact]
    public void CommandReferencePrefersAUniqueDescriptiveWordOverTheGenericFirstWord()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("nadgarstek", "bransoleta z spinelem")],
            [new InventoryItem("bransoleta z koralem"), new InventoryItem("bransoleta z jaspisem")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "bransoleta z spinelem", false, 0);

        Assert.Equal("spinelem", reference.Word);
        Assert.Equal(1, reference.Occurrence);
        Assert.Equal("remove spinelem", EquipmentInventorySnapshotParser.BuildItemCommand("remove", reference));
    }

    [Fact]
    public void CommandReferenceAvoidsObservedPrefixCollisionWhenAnotherWordIsUnique()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("unoszacy", "krysztal Tellany")],
            [new InventoryItem("krysztalowy pierscien")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "krysztal Tellany", false, 0);

        Assert.Equal("Tellany", reference.Word);
        Assert.Equal("examine Tellany", EquipmentInventorySnapshotParser.BuildItemCommand("examine", reference));
    }

    [Fact]
    public void CommandReferenceAvoidsObservedKrysztalowaBransoletkaCollision()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("unoszacy", "krysztal Tellany")],
            [new InventoryItem("krysztalowa bransoletka")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "krysztal Tellany", false, 0);

        Assert.Equal("Tellany", reference.Word);
        Assert.Equal("examine Tellany", EquipmentInventorySnapshotParser.BuildItemCommand("examine", reference));
    }

    [Fact]
    public void ItemCommandReferenceRemovesDisplayPunctuationFromWords()
    {
        var snapshot = new EquipmentInventorySnapshot([], [new InventoryItem("Prosta, drewniana szafka")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "Prosta, drewniana szafka", true, 0);

        Assert.Equal("Prosta", reference.Word);
        Assert.Equal("examine Prosta", EquipmentInventorySnapshotParser.BuildItemCommand("examine", reference));
    }

    [Fact]
    public void ExamineResponseMatchesAllNameWordsAfterPolishInflection()
    {
        var response = "Waga mithrilowej bransolety celnosci wynosi okolo 0.54 kg.\n<700/700hp 100/100mv>";

        Assert.True(EquipmentInventorySnapshotParser.IsLikelyExamineResponseForItem(
            response,
            "mithrilowa bransoleta celnosci"));
    }

    [Fact]
    public void ExamineResponseRejectsDifferentItemWhenANameWordIsMissing()
    {
        var response = "Krysztalowa bransoletka prawie nic nie wazy.\n<700/700hp 100/100mv>";

        Assert.False(EquipmentInventorySnapshotParser.IsLikelyExamineResponseForItem(
            response,
            "krysztal Tellany"));
    }

    [Fact]
    public void DirectionQuestionIsRecognizedForCommandWordRetry()
    {
        Assert.True(EquipmentInventorySnapshotParser.IsDirectionQuestion("W jakim kierunku chcesz spojrzec?"));
    }

    [Fact]
    public void ClosedLineConfirmsContainerBeforeItsContentsAreVisible()
    {
        Assert.True(EquipmentInventorySnapshotParser.IsClosedContainerResponse(
            "Masz przed soba drewniana szafke.\n\nZamkniete.\n\n<700/700hp 100/100mv>"));
    }

    [Fact]
    public void RecognizesObservedContainerAccessOutcomes()
    {
        Assert.True(EquipmentInventorySnapshotParser.IsContainerOpenedMessage("Otwierasz drewniana szafke."));
        Assert.True(EquipmentInventorySnapshotParser.IsContainerLockedMessage("Ten obiekt jest zamkniety na klucz."));
        Assert.True(EquipmentInventorySnapshotParser.IsContainerUnlockedMessage("Odkluczasz mithrilowy kufer."));
        Assert.True(EquipmentInventorySnapshotParser.IsContainerLockedByKeyMessage("Zamykasz mithrilowy kufer na klucz."));
        Assert.True(EquipmentInventorySnapshotParser.IsContainerKeyMissingMessage("Brakuje ci niestety klucza."));
        Assert.False(EquipmentInventorySnapshotParser.IsContainerKeyMissingMessage("Nie mozesz tego zrobic."));
    }

    [Fact]
    public void ResolvesContainerFromItsServerConfirmedOperationName()
    {
        var snapshot = new EquipmentInventorySnapshot([], [], [new InventoryItem("Wielki kufer wykonany z mithrilowej blachy")]);

        Assert.True(EquipmentInventorySnapshotParser.TryGetContainerOperationOutcome("Odkluczasz mithrilowy kufer.", out var outcome, out var name));
        Assert.Equal(ContainerOperationOutcome.Unlocked, outcome);
        Assert.True(EquipmentInventorySnapshotParser.TryResolveGroundContainerIndexFromServerName(snapshot, name, out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void RecognizesClosingAContainerBeforeLockingIt()
    {
        Assert.True(EquipmentInventorySnapshotParser.TryGetContainerOperationOutcome("Zamykasz mithrilowy kufer.", out var outcome, out _));
        Assert.Equal(ContainerOperationOutcome.Closed, outcome);
    }

    [Fact]
    public void RecognizesSleepingAndWakeUpServerResponses()
    {
        Assert.True(EquipmentInventorySnapshotParser.IsSleepingCharacterResponse("W snach czy co?"));
        Assert.True(EquipmentInventorySnapshotParser.IsWakeUpMessage("Budzisz sie i wstajesz."));
        Assert.False(EquipmentInventorySnapshotParser.IsSleepingCharacterResponse("Nie mozesz tego zrobic."));
    }

    [Fact]
    public void RetryReferenceUsesSharedOccurrenceOrderForTheNextWord()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("ucho", "kolczyk wszystkich bogow")],
            [new InventoryItem("fikusny kolczyk")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReferenceForWord(snapshot, false, 0, "wszystkich");

        Assert.Equal("wszystkich", reference.Argument);
    }

    [Fact]
    public void CommandReferenceDoesNotUseVisualStateInParentheses()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("bron", "(pulsuje) szkarlatny mlot")],
            [new InventoryItem("stalowy mlot")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "(pulsuje) szkarlatny mlot", false, 0);

        Assert.Equal("szkarlatny", reference.Word);
    }

    [Fact]
    public void CommandReferenceUsesTheServerIndexWhenEveryNameWordCollides()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("palec", "krysztalowy pierscien")],
            [new InventoryItem("krysztalowy pierscien")]);

        var reference = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "krysztalowy pierscien", false, 0);

        Assert.Equal("krysztalowy", reference.Word);
        Assert.Equal(2, reference.Occurrence);
        Assert.Equal("wear 2.krysztalowy", EquipmentInventorySnapshotParser.BuildItemCommand("wear", reference));
    }

    [Fact]
    public void GroundItemsAreParsedFromObservedRoomObjectLinesButNotRoomPeople()
    {
        const string response = "Hematyt lezy tutaj.\nMieszkaniec miasta przechadza sie tutaj.\nMieszkaniec miasta stoi tutaj.\n(NPK) Agron mezczyzna polork stoi tutaj.\nTablica skarg i wnioskow stoi tutaj.\n<700/700hp 100/100mv> pokoj";

        var items = EquipmentInventorySnapshotParser.ParseGroundItems(response, ["Mieszkaniec miasta", "Agron"]);

        Assert.Equal(["Hematyt", "Tablica skarg i wnioskow"], items.Select(item => item.Name));
    }

    [Fact]
    public void ParsesObservedGroundItemPresentationForms()
    {
        const string response = "Widzisz dlugi i ostry miecz.\nSzkarlatny pas wykonany ze skory wala sie tutaj.\nLeza tu szkarlatne buty.\nSzkarlatne rekawice przyciagaja twoj wzrok.\nPiekna, szkarlatna zbroja wykonana z elfiej stali.\nCzyjs rozgnieciony mozg plywa sobie tutaj.\nSluzacy przebiega obok ciebie bardzo sie spieszac.";

        var items = EquipmentInventorySnapshotParser.ParseGroundItems(response, ["Sluzacy"]);

        Assert.Equal(
            ["dlugi i ostry miecz", "Szkarlatny pas wykonany ze skory", "szkarlatne buty", "Szkarlatne rekawice", "Piekna, szkarlatna zbroja wykonana z elfiej stali", "Czyjs rozgnieciony mozg"],
            items.Select(item => item.Name));
    }

    [Fact]
    public void TreatsEveryPostDescriptionLineAsGroundItemExceptGmcpPeople()
    {
        const string response = "\nSkarbiec\n[Wyjscia: Wyjscie]\nOpis pomieszczenia.\n\nNieznany przedmiot o nietypowym opisie.\n(NPK) Norga kobieta czlowiek stoi tutaj.\n\n<700/700hp 100/100mv> Skarbiec";

        var items = EquipmentInventorySnapshotParser.ParseGroundItems(response, ["Norga"]);

        Assert.Equal(["Nieznany przedmiot o nietypowym opisie"], items.Select(item => item.Name));
    }

    [Fact]
    public void DoesNotTreatRoomHeaderAndDescriptionAsGroundItems()
    {
        const string response = "\nSkarbiec\n[Wyjscia: Wyjscie]\nOpis pomieszczenia.\n\nSzafir gwiazdzisty lezy tutaj.\nBransoleta z ametystem polyskuje magicznym blaskiem.\nWielki kufer wykonany z mithrilowej blachy.\n\n<700/700hp 100/100mv> Skarbiec";

        var items = EquipmentInventorySnapshotParser.ParseGroundItems(response, []);

        Assert.Equal(["Szafir gwiazdzisty", "Bransoleta z ametystem polyskuje magicznym blaskiem", "Wielki kufer wykonany z mithrilowej blachy"], items.Select(item => item.Name));
    }

    [Fact]
    public void StandingCharacterWithNarrativeSuffixIsNotAGroundItem()
    {
        const string response = "Zoldak stoi tutaj i bacznie cie obserwuje.";

        var items = EquipmentInventorySnapshotParser.ParseGroundItems(response, []);

        Assert.Empty(items);
    }

    [Fact]
    public void ParsesObservedBodiesAndChestAsGroundContainerCandidates()
    {
        const string response = "Zmasakrowane cialo Vierdona lezy tu i psuje sie powoli.\nWielki kufer wykonany z mithrilowej blachy.";

        var items = EquipmentInventorySnapshotParser.ParseGroundItems(response, []);

        Assert.Equal(["Zmasakrowane cialo Vierdona", "Wielki kufer wykonany z mithrilowej blachy"], items.Select(item => item.Name));
        Assert.All(items, item => Assert.True(EquipmentInventorySnapshotParser.IsPotentialGroundContainer(item.Name)));
    }

    [Theory]
    [InlineData("zamknieta skrzynia z debowego drewna")]
    [InlineData("stara szafka")]
    [InlineData("wysoki regal")]
    [InlineData("kamienny sarkofag")]
    public void PotentialGroundContainerVocabularyOnlyQualifiesForExamine(string name)
    {
        Assert.True(EquipmentInventorySnapshotParser.IsPotentialGroundContainer(name));
    }

    [Fact]
    public void ConfirmsGroundBodyContainerFromObservedContainsHeader()
    {
        const string response = "Cialo Vierdona (lezy na ziemi) zawiera:\nszkarlatna zbroja\n<700/700hp 100/100mv> pokoj";

        var parsed = EquipmentInventorySnapshotParser.TryParseContainerContents(response, "Zmasakrowane cialo Vierdona", out var contents);

        Assert.True(parsed);
        Assert.Equal("szkarlatna zbroja", Assert.Single(contents).Name);
    }

    [Fact]
    public void ParsesObservedLocatedStandAndConfirmsItsEmptyContainerBlock()
    {
        var items = EquipmentInventorySnapshotParser.ParseGroundItems("Pod sciana znajduje sie jakis stojak.", []);
        Assert.Equal("jakis stojak", Assert.Single(items).Name);

        var parsed = EquipmentInventorySnapshotParser.TryParseContainerContents(
            "Stojak na bron (lezy na ziemi) zawiera:\nOgolnie nic.\n<700/700hp 97/100mv> Magazyn",
            "jakis stojak", out var contents);

        Assert.True(parsed);
        Assert.Empty(contents);
    }

    [Fact]
    public void GroundItemsTakePriorityInCommandOccurrenceOrder()
    {
        var snapshot = new EquipmentInventorySnapshot(
            [new EquipmentItem("palec", "krysztalowy pierscien")],
            [new InventoryItem("krysztalowy pierscien")],
            [new InventoryItem("krysztalowy pierscien")]);

        var ground = EquipmentInventorySnapshotParser.ResolveGroundItemCommandReference(snapshot, "krysztalowy pierscien", 0);
        var inventory = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "krysztalowy pierscien", true, 0);
        var equipment = EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, "krysztalowy pierscien", false, 0);

        Assert.Equal("krysztalowy", ground.Argument);
        Assert.Equal("2.krysztalowy", inventory.Argument);
        Assert.Equal("3.krysztalowy", equipment.Argument);
    }

    [Fact]
    public void ManualGroundContainerReferenceResolvesAUserTypedContainerWord()
    {
        var snapshot = new EquipmentInventorySnapshot([], [], [new InventoryItem("Wielki kufer wykonany z mithrilowej blachy")]);

        Assert.True(EquipmentInventorySnapshotParser.TryResolveGroundItemIndex(snapshot, "kufer", out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void PickupAcknowledgementReturnsTheGroundItemName()
    {
        Assert.True(EquipmentInventorySnapshotParser.TryGetPickedUpItemName("Podnosisz jaspis.", out var item));
        Assert.Equal("jaspis", item);
        Assert.False(EquipmentInventorySnapshotParser.TryGetPickedUpItemName("Nie mozesz tego podniesc.", out _));
    }

    [Fact]
    public void GroundDropIsRecognizedOnlyFromTheObservedSuccessMessage()
    {
        Assert.True(EquipmentInventorySnapshotParser.IsGroundDropMessage("Upuszczasz jaspis."));
        Assert.False(EquipmentInventorySnapshotParser.IsGroundDropMessage("Nie mozesz tego upuscic."));
    }

    [Theory]
    [InlineData("Poltorareczny miecz rozsypuje sie w proch.")]
    [InlineData("Szkarlatny pas rozpada sie.")]
    [InlineData("Szkarlatne buty rozpadaja sie.")]
    public void RecognizesObservedItemDisintegrationNotices(string line)
    {
        Assert.True(EquipmentInventorySnapshotParser.IsItemDisintegrationMessage(line));
    }

    [Fact]
    public void DoesNotTreatAnOrdinaryRoomSentenceAsItemDisintegration()
    {
        Assert.False(EquipmentInventorySnapshotParser.IsItemDisintegrationMessage("Szkarlatny pas lezy tutaj."));
    }

    [Fact]
    public void PromptIsDetectedAfterExamineDescription()
    {
        Assert.True(EquipmentInventorySnapshotParser.ContainsPrompt("Krysztalowy pierscien poblyskuje ukryta moca.\n\n<428/428hp 130/130mv> Ulica Handlowa"));
    }

    [Fact]
    public void TooltipKeepsSourceBlankLinesButRemovesPromptAndCr()
    {
        Assert.Equal("Pierwsza linia\n\nDruga linia", EquipmentInventorySnapshotParser.WithoutPromptForTooltip("Pierwsza linia\r\n\r\nDruga linia\r\n<1/1hp 1/1mv> pokoj"));
    }

    [Fact]
    public void TooltipOmitsFlavorDescriptionBeforeTheFirstFullItemNameLine()
    {
        const string text = "Opis fabularny pierwsza linia.\n\nDruga linia opisu.\n\nObraczka Niraso prawie nic nie wazy.\nWplywa na punkty ruchu o 30.\n<1/1hp 1/1mv> pokoj";

        Assert.Equal("Obraczka Niraso prawie nic nie wazy.\nWplywa na punkty ruchu o 30.", EquipmentInventorySnapshotParser.WithoutPromptForTooltip(text, "(pulsuje) obraczka Niraso (100%)"));
    }

    [Fact]
    public void TooltipKeepsTheWholeResponseWhenNoFullItemNameLineIsPresent()
    {
        const string text = "Niepozorny opis.\n\nBrak dalszych danych.";

        Assert.Equal(text, EquipmentInventorySnapshotParser.WithoutPromptForTooltip(text, "obraczka Niraso"));
    }

    [Fact]
    public void SelfExamineExtractsOnlyTattooBlocksAndTheirBracketedBonuses()
    {
        const string response = "Widzisz, ze Agron jest pijany.\nNa twarzy masz gigantyczny tatuaz z wizerunkiem rune zniszczenia.\n[Wplywa na inteligencje o 5]\n[Poprawia obrazenia o 3]\n\nNa ramionach masz gigantyczny tatuaz z wizerunkiem golema stali.\n[Wplywa na sile o 5]\n\nAgron, polork, jest w doskonalej kondycji.\n\nAgron uzywa:\n<pierwsza bron> miecz\n";

        var tattoos = SelfExamineTattooParser.Parse(response);

        Assert.Collection(tattoos,
            first =>
            {
                Assert.Equal("twarzy", first.Location);
                Assert.Equal("gigantyczny tatuaz z wizerunkiem rune zniszczenia", first.Name);
                Assert.Equal("[Wplywa na inteligencje o 5]\n[Poprawia obrazenia o 3]", first.Description);
            },
            second =>
            {
                Assert.Equal("ramionach", second.Location);
                Assert.Equal("gigantyczny tatuaz z wizerunkiem golema stali", second.Name);
                Assert.Equal("[Wplywa na sile o 5]", second.Description);
            });
    }
}
