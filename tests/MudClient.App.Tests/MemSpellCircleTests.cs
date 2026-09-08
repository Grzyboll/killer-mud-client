using MudClient.App.Models;
using MudClient.Core.Gmcp;

namespace MudClient.App.Tests;

public sealed class MemSpellCircleTests
{
    [Fact]
    public void FromCore_SeparatesUnmemorizedSpells()
    {
        MemorizedSpell[] spells =
        [
            new(1, 2, "armor", Memed: true, Meming: false),
            new(2, 2, "refresh", Memed: false, Meming: true),
            new(3, 2, "fireball", Memed: false, Meming: false),
            new(4, 2, "fireball", Memed: false, Meming: false),
        ];

        var circle = Assert.Single(MemSpellCircle.FromCore(spells));

        Assert.Equal("armor", circle.MemedDisplay);
        Assert.Equal("refresh", circle.MemingDisplay);
        Assert.Equal(1, circle.MemedCount);
        Assert.Equal(2, circle.UnmemedCount);
        Assert.Equal("1/2", circle.CountDisplay);
        Assert.Equal("fireball ×2", circle.UnmemedDisplay);
        Assert.True(circle.HasUnmemed);
    }
}
