using MudClient.Core.Equipment;

namespace MudClient.Core.Tests;

public sealed class EquipmentBonusSummaryTests
{
    [Fact]
    public void OrdersTotalsByTheEquipmentPanelCategories()
    {
        var totals = EquipmentBonusSummary.SummarizeWithSources(
        [
            ("amulet", "Wplywa na umiejetnosc 'backstab' o 2."),
            ("buty", "Wplywa na punkty ruchu o 10."),
            ("helm", "Wplywa na klase pancerza o 3."),
            ("rekawice", "Wplywa na zrecznosc o 4."),
            ("pierscien", "Wplywa na sile o 2."),
            ("tarcza", "Wplywa na odpornosci na ogien o 5."),
            ("miecz", "Wplywa na obrazenia o 1."),
            ("pas", "Wplywa na punkty zycia o 20.")
        ]);

        Assert.Equal(
            ["sile", "zrecznosc", "punkty zycia", "punkty ruchu", "klase pancerza", "obrazenia", "odpornosci na ogien", "umiejetnosc 'backstab'"],
            totals.Select(total => total.Name));
        Assert.Equal("Statystyki bazowe", totals[0].Category);
        Assert.Equal("Umiejętności", totals[^1].Category);
    }

    [Fact]
    public void CapturesTheObservedPermanentEffectsWithoutTreatingThemAsNumericBonuses()
    {
        var effects = EquipmentBonusSummary.SummarizeStaticEffectsWithSources(
        [
            ("obraczka Niraso", "Wplywa na punkty ruchu o 30.\nDodaje aura_of_vigor."),
            ("druga obraczka", "Dodaje aura_of_vigor.")
        ]);

        var effect = Assert.Single(effects);
        Assert.Equal("aura_of_vigor", effect.Name);
        Assert.Equal(["obraczka Niraso", "druga obraczka"], effect.Contributions.Select(item => item.Item));
    }

    [Fact]
    public void ExtractsWeaponAndShieldStatsFromTheirEquipmentSlots()
    {
        var items = EquipmentBonusSummary.SummarizeCombatItems(
        [
            ("trzymane dwurecznie", "dwureczny mlot", "Typ broni: 'maczuga'.\nBonus do trafienia: 2.\nObrazenia zadawane 3d6 + 2 (srednio 12)."),
            ("druga bron", "sztylet", "Typ broni: 'sztylet'.\nBonus do trafienia: 1.\nObrazenia zadawane 2d4 + 1 (srednio 6)."),
            ("uzywane jako tarcza", "stalowa tarcza", "Rodzaj pancerza: Heavy armor\nKlasa pancerza: 2 klujace, 3 obuchowe, 1 ciecie")
        ]);

        Assert.Equal(3, items.Count);
        Assert.Equal("dwuręczna", items[0].Grip);
        Assert.Equal("jednoręczna", items[1].Grip);
        Assert.Equal(["typ: maczuga", "trafienie: +2", "obrażenia: 3d6 + 2 (srednio 12)"], items[0].Stats);
        Assert.Equal("Tarcza", items[2].Category);
        Assert.Equal(["rodzaj: Heavy armor", "klasa pancerza: 2 klujace, 3 obuchowe, 1 ciecie"], items[2].Stats);
    }

    [Fact]
    public void IncludesObservedTattooBonusWordingInTotals()
    {
        var totals = EquipmentBonusSummary.SummarizeWithSources(
        [
            ("helm", "Wplywa na inteligencje o 2."),
            ("gigantyczny tatuaz", "[Wplywa na inteligencje o 5]\n[Poprawia obrazenia o 3]")
        ]);

        Assert.Equal(["inteligencje", "obrazenia"], totals.Select(total => total.Name));
        Assert.Equal(7, totals[0].Value);
        Assert.Equal("gigantyczny tatuaz", Assert.Single(totals[1].Contributions).Item);
    }
}
