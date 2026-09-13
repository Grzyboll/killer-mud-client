using MudClient.Core.Equipment;
using MudClient.Core.Gmcp;

namespace MudClient.Core.Tests;

public sealed class ItemIdentificationPolicyTests
{
    [Fact]
    public void HidesSpellActionWhenIdentifyIsNotKnown()
    {
        Assert.Equal(IdentifySpellState.Unavailable, ItemIdentificationPolicy.GetIdentifySpellState(false, []));
    }

    [Fact]
    public void ReportsKnownButNotMemorizedFromTheSpellList()
    {
        Assert.Equal(IdentifySpellState.KnownButNotMemorized, ItemIdentificationPolicy.GetIdentifySpellState(true, []));
    }

    [Fact]
    public void TreatsAReadyIdentifyMemorizationAsCastable()
    {
        var spells = new[] { new MemorizedSpell(1, 3, "identify", Memed: true, Meming: false) };

        Assert.Equal(IdentifySpellState.Ready, ItemIdentificationPolicy.GetIdentifySpellState(false, spells));
    }
}
