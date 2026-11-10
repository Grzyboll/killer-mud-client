using MudClient.Core.Automation;

namespace MudClient.Core.Tests;

public sealed class PulsingItemPolicyTests
{
    [Fact]
    public void FindPulsingItemKeywords_TaggedItem_ReturnsItsFirstWord()
    {
        var keywords = PulsingItemPolicy.FindPulsingItemKeywords("(pulsuje) helm z czaszki smoka");

        Assert.Equal(["helm"], keywords);
    }

    [Theory]
    [InlineData("Nosisz: zardzewialy miecz.")]
    [InlineData("Nic tu nie ma.")]
    [InlineData("")]
    public void FindPulsingItemKeywords_NoTag_ReturnsEmpty(string line)
    {
        Assert.Empty(PulsingItemPolicy.FindPulsingItemKeywords(line));
    }

    [Fact]
    public void FindPulsingItemKeywords_CaseInsensitiveTag_StillMatches()
    {
        var keywords = PulsingItemPolicy.FindPulsingItemKeywords("(PULSUJE) pierscien mocy");

        Assert.Equal(["pierscien"], keywords);
    }

    [Fact]
    public void FindPulsingItemKeywords_PrecedingTextIgnored_OnlyWhatFollowsTheTagMatters()
    {
        var keywords = PulsingItemPolicy.FindPulsingItemKeywords("Na trupie zauwazasz: (pulsuje) helm z czaszki smoka.");

        Assert.Equal(["helm"], keywords);
    }

    [Fact]
    public void FindPulsingItemKeywords_TrailingPunctuationOnTheFirstWord_IsStripped()
    {
        // A single-word item name right before a sentence-ending period, e.g. "(pulsuje) amulet."
        var keywords = PulsingItemPolicy.FindPulsingItemKeywords("(pulsuje) amulet.");

        Assert.Equal(["amulet"], keywords);
    }

    [Fact]
    public void FindPulsingItemKeywords_TagWithNothingAfterIt_ReturnsEmpty()
    {
        Assert.Empty(PulsingItemPolicy.FindPulsingItemKeywords("(pulsuje)"));
    }

    [Fact]
    public void FindPulsingItemKeywords_TwoTaggedItemsOnOneLine_ReturnsBothKeywords()
    {
        var keywords = PulsingItemPolicy.FindPulsingItemKeywords(
            "(pulsuje) helm z czaszki smoka oraz (pulsuje) pierscien mocy");

        Assert.Equal(["helm", "pierscien"], keywords);
    }

    [Fact]
    public void FindPulsingItemKeywords_PulsujeItselfIsNeverReturnedAsTheKeyword()
    {
        // The whole point: "pulsuje" is a status tag, not part of the item's own name, so it must
        // never be the thing a caller sends "get <keyword>" for.
        var keywords = PulsingItemPolicy.FindPulsingItemKeywords("(pulsuje) helm z czaszki smoka");

        Assert.DoesNotContain("pulsuje", keywords);
    }
}
