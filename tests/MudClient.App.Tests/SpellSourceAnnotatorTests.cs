using MudClient.App.Models;
using MudClient.App.Services;
using Xunit;

namespace MudClient.App.Tests;

public sealed class SpellSourceAnnotatorTests
{
    private static SpellMobEntry Mob(string name, params string[] spells) =>
        new(null, name, "Region", "Mag", spells, null, false, false, false, false, null);

    [Fact]
    public void Annotate_MissingSpellWithKnownSource_AppendsMobName()
    {
        var mobs = new[] { Mob("Rogaty demon", "transmute staff") };
        var line = "Krag 1: (  ) transmute staff";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Equal("Krag 1: (  ) transmute staff (Rogaty demon)", result);
    }

    [Fact]
    public void Annotate_AlreadyKnownSpell_LeavesEntryUnchanged()
    {
        var mobs = new[] { Mob("Rogaty demon", "armor") };
        var line = "Krag 1: (29)[1] armor";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Equal(line, result);
    }

    [Fact]
    public void Annotate_MissingSpellWithNoKnownSource_LeavesEntryUnchanged()
    {
        var mobs = new[] { Mob("Rogaty demon", "armor") };
        var line = "Krag 1: (  )[1] transmute staff";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Equal(line, result);
    }

    [Fact]
    public void Annotate_MultiWordSpellName_IsCapturedInFull()
    {
        var mobs = new[] { Mob("Rogaty demon", "cause light") };
        var line = "Krag 1: (  ) cause light                 (29)[1] bless";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Contains("cause light (Rogaty demon)", result);
        Assert.Contains("(29)[1] bless", result);
    }

    [Fact]
    public void Annotate_MultipleMobsTeachSameSpell_ListsAllSortedAlphabetically()
    {
        var mobs = new[] { Mob("Zorro", "shield"), Mob("Anna", "shield") };
        var line = "(  ) shield";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Equal("(  ) shield (Anna, Zorro)", result);
    }

    [Fact]
    public void Annotate_SeveralEntriesOnOneLine_AnnotatesOnlyMissingOnesIndependently()
    {
        var mobs = new[] { Mob("Rogaty demon", "transmute staff") };
        var line = "(29)[1] armor                    (29)[1] bless                    (  ) transmute staff";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.StartsWith("(29)[1] armor                    (29)[1] bless", result);
        Assert.EndsWith("(  ) transmute staff (Rogaty demon)", result);
    }

    [Theory]
    [InlineData("Witaj w krainie Killer.")]
    [InlineData("")]
    [InlineData("Widzisz tutaj strażnika miasta.")]
    public void Annotate_LineWithoutSpellRow_LeavesLineUnchanged(string line)
    {
        var mobs = new[] { Mob("Rogaty demon", "shield") };

        Assert.Equal(line, SpellSourceAnnotator.Annotate(line, mobs));
    }

    [Fact]
    public void Annotate_NoSpellMobsLoaded_LeavesLineUnchanged()
    {
        var line = "(  )[1] shield";

        Assert.Equal(line, SpellSourceAnnotator.Annotate(line, []));
    }

    [Fact]
    public void FindSpellSources_CaseInsensitiveMatch_StillFound()
    {
        var mobs = new[] { Mob("Rogaty demon", "Shield") };

        var sources = SpellSourceAnnotator.FindSpellSources("shield", mobs);

        Assert.Equal(["Rogaty demon"], sources);
    }

    [Fact]
    public void FindSpellSources_SameMobListedTwice_ReturnedOnce()
    {
        var mobs = new[] { Mob("Rogaty demon", "shield"), Mob("Rogaty demon", "shield") };

        var sources = SpellSourceAnnotator.FindSpellSources("shield", mobs);

        Assert.Equal(["Rogaty demon"], sources);
    }

    // ====================================================================
    // Regression guard: this MUD colors "spell" output entries — the escape codes sit
    // right inside what looks like plain column padding, which broke SkillTrainerAnnotator
    // before it started matching against an ANSI-stripped copy (see AnsiText.StripAnsiWithMap).
    // Applied here from the start.
    // ====================================================================

    private const string Esc = "\x1B";

    [Fact]
    public void Annotate_ColoredMissingEntry_StillMatchesAndAnnotates()
    {
        var mobs = new[] { Mob("Rogaty demon", "shield") };
        var line = $"({Esc}[32m  {Esc}[0m) shield";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Contains("(Rogaty demon)", result);
    }

    [Fact]
    public void Annotate_ColoredLine_PreservesOriginalEscapeCodesVerbatim()
    {
        var mobs = new[] { Mob("Rogaty demon", "shield") };
        var line = $"({Esc}[32m  {Esc}[0m) shield";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Contains($"({Esc}[32m  {Esc}[0m) shield", result);
    }

    [Fact]
    public void Annotate_ColoredKnownCount_IsNotTreatedAsMissing()
    {
        var mobs = new[] { Mob("Rogaty demon", "armor") };
        var line = $"({Esc}[32m29{Esc}[0m)[1] armor";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Equal(line, result);
    }

    // A game update introduced "( + )" for a spell the player already has but cannot yet
    // cast (e.g. still below the required level) — a non-blank count, so it's already
    // "known" under the existing check and must not get a source annotation appended.
    [Fact]
    public void Annotate_PlusMarkerSpell_IsNotTreatedAsMissing()
    {
        var mobs = new[] { Mob("Rogaty demon", "charm person") };
        var line = "Krag 1: ( + ) charm person";

        var result = SpellSourceAnnotator.Annotate(line, mobs);

        Assert.Equal(line, result);
    }
}
