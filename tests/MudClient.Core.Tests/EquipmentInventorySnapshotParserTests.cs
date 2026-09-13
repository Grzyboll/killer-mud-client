using MudClient.Core.Equipment;

namespace MudClient.Core.Tests;

public sealed class EquipmentInventorySnapshotParserTests
{
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
