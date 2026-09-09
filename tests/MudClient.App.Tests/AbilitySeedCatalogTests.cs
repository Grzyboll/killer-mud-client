using MudClient.App.Services;

namespace MudClient.App.Tests;

public sealed class AbilitySeedCatalogTests
{
    [Fact]
    public void Find_KnownClass_IsCaseInsensitiveAndTrimmed()
    {
        var lower = AbilitySeedCatalog.Find("paladyn");
        var upper = AbilitySeedCatalog.Find("PALADYN");
        var padded = AbilitySeedCatalog.Find("  Paladyn  ");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.NotNull(padded);
        Assert.Equal("Paladyn", lower!.Class);
    }

    [Fact]
    public void Find_UnknownClass_ReturnsNull()
    {
        Assert.Null(AbilitySeedCatalog.Find("wedrowiec"));
        Assert.Null(AbilitySeedCatalog.Find(""));
    }

    [Fact]
    public void Paladyn_HasNoDuplicateSkillOrSpellNames()
    {
        var seed = AbilitySeedCatalog.Find("Paladyn")!;

        var duplicateSkills = seed.Skills.GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        var duplicateSpells = seed.Spells.GroupBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();

        Assert.Empty(duplicateSkills);
        Assert.Empty(duplicateSpells);
    }

    [Fact]
    public void Paladyn_EverySkillHasAPositiveMinLevel()
    {
        var seed = AbilitySeedCatalog.Find("Paladyn")!;

        Assert.All(seed.Skills, skill => Assert.True(skill.MinLevel > 0, skill.Name));
    }

    [Fact]
    public void Paladyn_EverySpellHasACircleBetweenOneAndFive()
    {
        var seed = AbilitySeedCatalog.Find("Paladyn")!;

        Assert.All(seed.Spells, spell => Assert.InRange(spell.Circle, 1, 5));
    }

    [Fact]
    public void Paladyn_AllNames_CombinesSkillsThenSpellsWithNoBlanks()
    {
        var seed = AbilitySeedCatalog.Find("Paladyn")!;

        var names = seed.AllNames;

        Assert.Equal(seed.Skills.Count + seed.Spells.Count, names.Count);
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(seed.Skills[0].Name, names[0]);
        Assert.Equal(seed.Spells[0].Name, names[seed.Skills.Count]);
    }

    [Fact]
    public void Paladyn_WeaponMasteryEntries_ShareTheSameNote()
    {
        var seed = AbilitySeedCatalog.Find("Paladyn")!;

        var masteries = seed.Skills.Where(skill => skill.Name.EndsWith("mastery", StringComparison.Ordinal)
            && skill.MinLevel == 20).ToArray();

        Assert.Equal(3, masteries.Length);
        Assert.All(masteries, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Note)));
        Assert.Single(masteries.Select(skill => skill.Note).Distinct());
    }

    [Fact]
    public void KnownClasses_ContainsPaladyn()
    {
        Assert.Contains("Paladyn", AbilitySeedCatalog.KnownClasses);
    }

    // ====================================================================
    // Czarny Rycerz, Złodziej, Druid, Nomad, Kleryk, Wojownik, Barbarzyńca, Mag —
    // supplied later than Paladyn, checked the same way.
    // ====================================================================

    public static IEnumerable<object[]> AllSeededClasses => new[]
    {
        "Paladyn", "Czarny Rycerz", "Złodziej", "Druid", "Nomad", "Kleryk", "Wojownik", "Barbarzyńca", "Mag",
        "Przemiany", "Przywołania", "Poznanie", "Zauroczenie", "Iluzje", "Inwokacje", "Nekromancja",
    }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(AllSeededClasses))]
    public void KnownClasses_ContainsEverySeededClass(string className)
    {
        Assert.Contains(className, AbilitySeedCatalog.KnownClasses);
        Assert.NotNull(AbilitySeedCatalog.Find(className));
    }

    [Theory]
    [MemberData(nameof(AllClassesWithSkills))]
    public void EveryClass_EverySkillHasAPositiveMinLevel(string className)
    {
        var seed = AbilitySeedCatalog.Find(className)!;

        Assert.NotEmpty(seed.Skills);
        Assert.All(seed.Skills, skill => Assert.True(skill.MinLevel > 0, skill.Name));
    }

    public static IEnumerable<object[]> AllClassesWithSkills => AllSeededClasses
        .Where(row => AbilitySeedCatalog.Find((string)row[0])!.Skills.Count > 0);

    [Theory]
    [MemberData(nameof(AllSeededClasses))]
    public void EveryClass_EverySpellHasAPositiveCircle(string className)
    {
        var seed = AbilitySeedCatalog.Find(className)!;

        Assert.All(seed.Spells, spell => Assert.True(spell.Circle > 0, spell.Name));
    }

    [Theory]
    [MemberData(nameof(AllSeededClasses))]
    public void EveryClass_AllNames_CombinesSkillsThenSpellsWithNoBlanks(string className)
    {
        var seed = AbilitySeedCatalog.Find(className)!;

        var names = seed.AllNames;

        Assert.Equal(seed.Skills.Count + seed.Spells.Count, names.Count);
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    [Fact]
    public void Druid_HasNoUnexpectedDuplicateSkillNames()
    {
        // "light armor" is the one deliberate exception — it really is listed twice in the
        // source (once under "umiejętności zbroi", once as its own standalone [P] bullet).
        var seed = AbilitySeedCatalog.Find("Druid")!;

        var duplicates = seed.Skills.GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();

        Assert.Equal(["light armor"], duplicates);
    }

    [Theory]
    [InlineData("Czarny Rycerz")]
    [InlineData("Wojownik")]
    public void WeaponMasteryChoices_ShareTheSameNote(string className)
    {
        var seed = AbilitySeedCatalog.Find(className)!;

        var masteries = seed.Skills.Where(skill => skill.Name.EndsWith("mastery", StringComparison.Ordinal)
            && skill.MinLevel == 20).ToArray();

        Assert.NotEmpty(masteries);
        Assert.All(masteries, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Note)));
        Assert.Single(masteries.Select(skill => skill.Note).Distinct());
    }

    [Fact]
    public void Nomad_SharesDruidsNatureSpells_PlusSandStorm()
    {
        var druid = AbilitySeedCatalog.Find("Druid")!;
        var nomad = AbilitySeedCatalog.Find("Nomad")!;

        Assert.Equal(druid.Spells.Count + 1, nomad.Spells.Count);
        Assert.Equal(
            druid.Spells.Select(spell => (spell.Name, spell.Circle)),
            nomad.Spells.Take(druid.Spells.Count).Select(spell => (spell.Name, spell.Circle)));
        Assert.Contains(nomad.Spells, spell => spell.Name == "sand storm" && spell.Circle == 8);
        Assert.All(nomad.Spells, spell => Assert.Equal("Nomad", spell.Class));
    }

    [Fact]
    public void Mag_HasSkillsButNoGeneralSpellsYet()
    {
        // Mag's own general/unspecialized spell list hasn't been supplied yet — only its
        // per-school specializations (e.g. "Odrzucanie") have spells seeded so far.
        var seed = AbilitySeedCatalog.Find("Mag")!;

        Assert.NotEmpty(seed.Skills);
        Assert.Empty(seed.Spells);
    }

    [Fact]
    public void Odrzucanie_IsItsOwnSeparatelySeededWandererSpecialization()
    {
        // A Mag's spell schools are each their own separately pickable Wędrowiec specialization
        // (see AbilitySeedCatalog's own class header comment), not sub-categories of "Mag" —
        // so "Odrzucanie" is its own seed entry with no skills of its own, only spells.
        var seed = AbilitySeedCatalog.Find("Odrzucanie")!;

        Assert.Contains("Odrzucanie", AbilitySeedCatalog.KnownClasses);
        Assert.Empty(seed.Skills);
        Assert.NotEmpty(seed.Spells);
        Assert.Contains(seed.Spells, spell => spell.Circle == 9);
        Assert.All(seed.Spells, spell => Assert.InRange(spell.Circle, 1, 9));
        Assert.All(seed.Spells, spell => Assert.Equal("Odrzucanie", spell.Class));
        Assert.Equal(seed.Spells.Select(spell => spell.Name), seed.AllNames);
    }

    [Fact]
    public void Przemiany_HasTheSuppliedFullSpellListAcrossAllNineCircles()
    {
        var seed = AbilitySeedCatalog.Find("  PRZEMIANY  ")!;

        Assert.Contains("Przemiany", AbilitySeedCatalog.KnownClasses);
        Assert.Empty(seed.Skills);
        Assert.Equal(120, seed.Spells.Count);
        Assert.Equal(17, seed.Spells.Count(spell => spell.Circle == 1));
        Assert.Equal(26, seed.Spells.Count(spell => spell.Circle == 2));
        Assert.Equal(17, seed.Spells.Count(spell => spell.Circle == 3));
        Assert.Equal(10, seed.Spells.Count(spell => spell.Circle == 4));
        Assert.Equal(10, seed.Spells.Count(spell => spell.Circle == 5));
        Assert.Equal(12, seed.Spells.Count(spell => spell.Circle == 6));
        Assert.Equal(10, seed.Spells.Count(spell => spell.Circle == 7));
        Assert.Equal(12, seed.Spells.Count(spell => spell.Circle == 8));
        Assert.Equal(6, seed.Spells.Count(spell => spell.Circle == 9));
        Assert.All(seed.Spells, spell => Assert.Equal("Przemiany", spell.Class));
        Assert.DoesNotContain(seed.Spells.GroupBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase),
            group => group.Count() > 1);
    }

    public static IEnumerable<object[]> SuppliedSchoolSpellCounts =>
    [
        ["Przemiany", 120], ["Przywołania", 107], ["Poznanie", 149], ["Zauroczenie", 109],
        ["Iluzje", 86], ["Inwokacje", 107], ["Nekromancja", 115],
    ];

    [Theory]
    [MemberData(nameof(SuppliedSchoolSpellCounts))]
    public void SuppliedSchool_HasTheExactProvidedSpellCountAndNoDuplicates(string school, int expectedCount)
    {
        var seed = AbilitySeedCatalog.Find(school)!;

        Assert.Empty(seed.Skills);
        Assert.Equal(expectedCount, seed.Spells.Count);
        Assert.All(seed.Spells, spell => Assert.InRange(spell.Circle, 1, 9));
        Assert.All(seed.Spells, spell => Assert.Equal(school, spell.Class));
        Assert.DoesNotContain(seed.Spells.GroupBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase),
            group => group.Count() > 1);
    }
}
