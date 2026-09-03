using MudClient.Core.Killeropedia;

namespace MudClient.Core.Tests;

/// <summary>
/// The word pools in <see cref="RandomBookNaming"/> are sourced from a modern wiki page with
/// proper Polish diacritics, but this MUD's server never sends diacritics in its own output — so
/// these tests deliberately use plain-ASCII input throughout (e.g. "duza ksiega triumfu", not
/// "duża księga triumfu"), matching what a real game session actually looks like.
/// </summary>
public sealed class RandomBookNamingTests
{
    [Theory]
    [InlineData("duza ksiega triumfu", "duza ksiega triumfu (Paladyn)")]
    [InlineData("Widzisz tutaj: duza ksiega triumfu.", "Widzisz tutaj: duza ksiega triumfu (Paladyn).")]
    [InlineData("ksiega triumfu", "ksiega triumfu (Paladyn)")]
    [InlineData("kolosalny tom magii", "kolosalny tom magii (Mag)")]
    [InlineData("filigranowy wolumen Zapomnianego Boga", "filigranowy wolumen Zapomnianego Boga (Kleryk)")]
    [InlineData("masywne cymelium wiedzy", "masywne cymelium wiedzy (Mag)")]
    [InlineData("stary folial walki", "stary folial walki (Paladyn)")]
    [InlineData("mala ksiazka lasu", "mala ksiazka lasu (Druid)")]
    [InlineData("wolumin piasku", "wolumin piasku (Nomad)")]
    public void AnnotateClasses_MatchesKnownBookNames(string input, string expected)
    {
        Assert.Equal(expected, RandomBookNaming.AnnotateClasses(input));
    }

    [Theory]
    [InlineData("Brakuje ci sily do dalszej walki.")]
    [InlineData("Rzucasz zaklecie mocy.")]
    [InlineData("Twoja wiara w zwyciestwo rosnie.")]
    [InlineData("")]
    [InlineData("pojedyncze")]
    public void AnnotateClasses_LeavesUnrelatedTextUnchanged(string input)
    {
        Assert.Equal(input, RandomBookNaming.AnnotateClasses(input));
    }

    [Fact]
    public void AnnotateClasses_MultiWordClassPhrase_MatchesAsSingleUnit()
    {
        var result = RandomBookNaming.AnnotateClasses("nowa ksiega z rubinami");

        Assert.Equal("nowa ksiega z rubinami (Mag)", result);
    }

    [Fact]
    public void AnnotateClasses_AnnotatesEachMatchIndependently()
    {
        var result = RandomBookNaming.AnnotateClasses("duza ksiega triumfu obok kolosalny tom magii");

        Assert.Equal("duza ksiega triumfu (Paladyn) obok kolosalny tom magii (Mag)", result);
    }

    [Fact]
    public void AnnotateClasses_AlsoMatchesProperlyAccentedInput()
    {
        // The wiki-sourced word lists still need to fold correctly the other direction too, in
        // case some other text source ever does carry real diacritics.
        var result = RandomBookNaming.AnnotateClasses("duża księga triumfu");

        Assert.Equal("duża księga triumfu (Paladyn)", result);
    }

    // ====================================================================
    // FindBookNames — same matching as AnnotateClasses, but returns the matched name itself
    // (verbatim, as it appeared in the line) instead of splicing "(Klasa)" after it. This is what
    // the "/autoget" automation uses to build a "get <name>" command.
    // ====================================================================

    [Fact]
    public void FindBookNames_SingleMatch_ReturnsItVerbatim()
    {
        var names = RandomBookNaming.FindBookNames("Nosisz: duza ksiega triumfu.");

        Assert.Equal(["duza ksiega triumfu"], names);
    }

    [Fact]
    public void FindBookNames_MultipleMatchesInOneLine_ReturnsBothInOrder()
    {
        var names = RandomBookNaming.FindBookNames("duza ksiega triumfu obok mala ksiazka lasu");

        Assert.Equal(["duza ksiega triumfu", "mala ksiazka lasu"], names);
    }

    [Fact]
    public void FindBookNames_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(RandomBookNaming.FindBookNames("Nosisz: zardzewialy miecz."));
    }

    [Fact]
    public void FindBookNames_PreservesRealDiacriticsFromTheOriginalLine()
    {
        // Matching runs against a folded copy, but the returned name must come from the original
        // text verbatim — this is fed straight into a "get <name>" command, so it must be typed
        // exactly as the game would recognize it, diacritics included if the source had them.
        var names = RandomBookNaming.FindBookNames("duża księga triumfu");

        Assert.Equal(["duża księga triumfu"], names);
    }
}
