using MudClient.Core.Automation;

namespace MudClient.Core.Tests;

public sealed class ExperienceGainPolicyTests
{
    [Theory]
    [InlineData("Zdobyłeś 120 punktów doświadczenia.", true)]
    [InlineData("Zdobyles 120 punktow doswiadczenia.", true)]
    [InlineData("zdobyles 120 punktow doswiadczenia.", true)]
    [InlineData("Zdobyłeś 1 punkt doświadczenia.", true)]
    [InlineData("Zdobyłeś 3 punkty doświadczenia.", true)]
    [InlineData("Zabijasz golema. Zdobyłeś 500 punktów doświadczenia.", true)]
    [InlineData("Nic się nie dzieje.", false)]
    [InlineData("Zdobyłeś nowy poziom!", false)]
    [InlineData("Tracisz 10 punktów doświadczenia.", false)]
    public void IsExperienceGainLine_FoldsDiacriticsBeforeMatching(string line, bool expected)
    {
        Assert.Equal(expected, ExperienceGainPolicy.IsExperienceGainLine(line));
    }
}
