using MudClient.App.Services;
using Xunit;

namespace MudClient.App.Tests;

public sealed class SpellKnowledgeParserTests
{
    [Fact]
    public void Parse_KnownAndMissingRows_ReturnsBothWithCorrectFlag()
    {
        var chunk = "Krag 1: (29)[1] armor                    (  ) transmute staff";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Contains(results, r => r.Name == "armor" && r.Known);
        Assert.Contains(results, r => r.Name == "transmute staff" && !r.Known);
    }

    [Fact]
    public void Parse_MultiLineSpellAllOutput_CollectsRowsFromContinuationLines()
    {
        var chunk =
            "Krag 1: (29)[1] armor                    (29)[1] bless                    (29)[1] cause light              \n" +
            "        (29)[1] create food              (  ) transmute staff          (29)[1] create water             \n" +
            "        (29)[1] light                    (29)[1] cure light               (29)[1] detect magic             ";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Equal(9, results.Count);
        Assert.Contains(results, r => r.Name == "detect magic" && r.Known);
        Assert.Contains(results, r => r.Name == "transmute staff" && !r.Known);
    }

    [Fact]
    public void Parse_TextWithoutCircleHeader_ReturnsEmpty()
    {
        var chunk = "(29)[1] armor  (  ) transmute staff";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("Witaj w krainie Killer.")]
    [InlineData("")]
    public void Parse_UnrelatedText_ReturnsEmpty(string chunk)
    {
        Assert.Empty(SpellKnowledgeParser.Parse(chunk));
    }

    [Fact]
    public void Parse_PolishDiacriticHeader_IsRecognizedToo()
    {
        var chunk = "Krąg 2: (  ) fireball";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Contains(results, r => r.Name == "fireball" && !r.Known);
    }

    // ====================================================================
    // Regression guard: same ANSI-coloring hazard as SkillTrainerAnnotator/SpellSourceAnnotator —
    // this MUD colors the memorization counts in its "spell" output.
    // ====================================================================

    private const string Esc = "\x1B";

    [Fact]
    public void Parse_ColoredCount_StillClassifiesCorrectly()
    {
        var chunk = $"Krag 1: ({Esc}[32m29{Esc}[0m)[1] armor";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Contains(results, r => r.Name == "armor" && r.Known);
    }

    [Fact]
    public void Parse_ColoredBlankCount_StillClassifiesAsMissing()
    {
        var chunk = $"Krag 1: ({Esc}[32m  {Esc}[0m) transmute staff";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Contains(results, r => r.Name == "transmute staff" && !r.Known);
    }

    // ====================================================================
    // Regression guard: a game update introduced "( + )" for a spell the player already
    // has but cannot yet cast (e.g. still below the required level). It's a non-blank
    // count, so the existing "known" check already covers it — this locks that in.
    // ====================================================================

    [Fact]
    public void Parse_PlusMarkerCount_IsClassifiedAsKnown()
    {
        var chunk = "Krag 1: ( + ) charm person";

        var results = SpellKnowledgeParser.Parse(chunk);

        Assert.Contains(results, r => r.Name == "charm person" && r.Known);
    }
}
